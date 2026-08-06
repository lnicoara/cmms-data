#!/usr/bin/env bash
# Load a generated synthetic artifact into a pre-prod tenant via the in-VNet load job (lnicoara/cmms#2917,
# per #2908 and epic #2731).
#
# Why a job and not a laptop: pre-prod SQL sets Deny Public Network Access, so a direct connection from
# outside the VNet is refused at login no matter what firewall rules exist.
#   mssql: login error: Connection was denied because Deny Public Network Access is set to Yes.
#
# PLAN ONLY by default. It stands everything up and runs the loader's plan, which verifies the staged
# artifact and writes nothing. Pass --execute to actually load.
#
# Run from the repo root, logged in to the pre-prod subscription (az login + az account set).
# The default target is demo-health, which is SEEDED. Its write set is cleared before the load, because the
# artifact ships ServiceLines rows on the same well-known Guids DemoDataSeeder writes. That is destructive,
# so it is OFF unless --clear-target=<slug> names the tenant being emptied.
#
#   scripts/pre-prod/load-tenant.sh                            # plan: stage, verify, write nothing
#   scripts/pre-prod/load-tenant.sh --execute --clear-target=demo-health   # wipe, then load
#   TENANT_SLUG=loadtest scripts/pre-prod/load-tenant.sh --execute         # unseeded tenant, no wipe
#   SKIP_UPLOAD=1 scripts/pre-prod/load-tenant.sh --execute    # artifact already staged in blob
set -euo pipefail

# The fixed pre-prod subscription, same id deploy-pre-prod.sh and seed-tenant.sh pin. The script SELECTS it
# below rather than trusting the active az context, so it can never build/deploy/run against another
# subscription that happens to also have an rg-cmms-preprod.
SUBSCRIPTION="${SUBSCRIPTION:-08fd6214-7b3c-4b5e-a59b-320707791129}"
RG="${RG:-rg-cmms-preprod}"
# demo-health, the existing pre-prod tenant. It is SEEDED, and the artifact ships its own ServiceLines rows
# on the same well-known Guids DemoDataSeeder writes (ServiceLineSeed), so those rows collide on the primary
# key. --clear-target is what makes a seeded tenant a valid target: it empties the artifact's write set
# first. That is destructive and deliberate, and it is why the flag is separate from --execute.
TENANT_SLUG="${TENANT_SLUG:-demo-health}"
ARTIFACT_DIR="${ARTIFACT_DIR:-/tmp/gen-full}"
IMAGE_TAG="${IMAGE_TAG:-$(git rev-parse --short HEAD 2>/dev/null || echo manual)}"
TIMEOUT_HOURS="${TIMEOUT_HOURS:-12}"

EXECUTE=false
# OFF by default, and it stays off unless the operator NAMES the tenant they mean to wipe. A default of
# true would have collapsed the loader's two-flag delete fence back into one, because --execute alone would
# then delete, and it would have done so for whatever slug TENANT_SLUG happened to hold.
CLEAR_TARGET=false
CLEAR_CONFIRM=""
for arg in "$@"; do
  case "$arg" in
    --execute) EXECUTE=true ;;
    --clear-target=*) CLEAR_TARGET=true; CLEAR_CONFIRM="${arg#*=}" ;;
    --clear-target) die "--clear-target needs the tenant slug you mean to wipe: --clear-target=<slug>" ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $arg" >&2; exit 2 ;;
  esac
done

die() { echo "ERROR: $*" >&2; exit 1; }
step() { echo; echo "==> $*"; }

command -v az >/dev/null || die "az CLI not found."
[ -f infra/load-job.bicep ] || die "run from the repo root (infra/load-job.bicep not found)."
az account show >/dev/null 2>&1 || die "not logged in: run 'az login' first."

step "Selecting the pre-prod subscription ($SUBSCRIPTION)"
az account set --subscription "$SUBSCRIPTION" || die "cannot select subscription '$SUBSCRIPTION' (are you logged in to it?)."
az group show -n "$RG" >/dev/null 2>&1 || die "resource group '$RG' not found in subscription '$SUBSCRIPTION'."

step "Discovering pre-prod resources in $RG"
ACR=$(az acr list -g "$RG" --query "[0].name" -o tsv)
SQL=$(az sql server list -g "$RG" --query "[0].name" -o tsv)
UAMI_PRINCIPAL=$(az identity show -g "$RG" -n id-cmms-api --query principalId -o tsv 2>/dev/null || true)
# Looked up rather than computed. The Bicep templates derive these names from uniqueString(), and at least
# one of those derivations is already wrong for pre-prod, so the names this script passes to a deployment
# are the names of resources it has actually seen.
KV=$(az keyvault list -g "$RG" --query "[0].name" -o tsv)
[ -n "$ACR" ] || die "no ACR in $RG."
[ -n "$SQL" ] || die "no SQL server in $RG."
[ -n "$KV" ] || die "no Key Vault in $RG."
[ -n "$UAMI_PRINCIPAL" ] || die "managed identity id-cmms-api not found in $RG."
# The signed-in operator needs blob data access to upload the artifact; the account is RBAC-only, so there
# is no key to fall back on. Derived rather than demanded: the store deployment grants it below.
OPERATOR_PRINCIPAL=$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)
[ -n "$OPERATOR_PRINCIPAL" ] || die "cannot resolve the signed-in user's object id (az ad signed-in-user show)."
# A private endpoint must sit in the SUBNET's region, and here that is not the resource group's: the RG is
# westus3 while the VNet is centralus. Derived from the VNet rather than assumed, because assuming
# resourceGroup().location deployed a westus3 endpoint against a centralus VNet and failed outright.
# Named, not "whichever VNet ARM happens to list first". load-test-store.bicep joins the endpoint to
# vnet-cmms-<environment>/snet-pe by that exact name, so the region has to be read from that exact VNet or
# the two can disagree the moment a second VNet appears in the group.
VNET="vnet-cmms-preprod"
az network vnet show -g "$RG" -n "$VNET" >/dev/null 2>&1 \
  || die "VNet '$VNET' not found in $RG; the load job needs it to reach the private data tier."
VNET_LOCATION=$(az network vnet show -g "$RG" -n "$VNET" --query location -o tsv)
[ -n "$VNET_LOCATION" ] || die "cannot read the location of VNet '$VNET'."
# The store denies every public address except this one, so the upload has to say which one it is.
OPERATOR_IP=$(curl -fsS --max-time 10 https://api.ipify.org 2>/dev/null || true)
[ -n "$OPERATOR_IP" ] || die "cannot determine this machine's public IP, which the artifact upload needs to be allowed through."
echo "    ACR=$ACR  SQL=$SQL  tenant=$TENANT_SLUG  tag=$IMAGE_TAG  execute=$EXECUTE  clear=$CLEAR_TARGET"
echo "    VNet=$VNET ($VNET_LOCATION)  operator IP=$OPERATOR_IP"

step "Preflight: this checkout must be the code you think is running"
# IMAGE_TAG comes from HEAD, so the tag is a claim about which source built the image. Two ways that claim
# goes wrong, and both have bitten this load already:
#   - uncommitted changes: the tag names a commit that does not contain them
#   - running from the wrong checkout: the tag names a commit that lacks the fix you meant to run
# The second one silently deployed a binary WITHOUT the LOAD_EXECUTE fix and the job reported success.
# Scoped to the paths the Dockerfile actually COPYs, not the whole tree. A modified README or CLAUDE.md
# cannot change the built image, so refusing to run over one is a false alarm that blocks a real load for
# no reason. It did exactly that on the first run after this guard was added.
DIRTY=$(git status --porcelain -- Directory.Build.props global.json src tools 2>/dev/null)
if [ -n "$DIRTY" ]; then
  echo "$DIRTY" >&2
  die "the files above go INTO the image, and are uncommitted. IMAGE_TAG=$IMAGE_TAG names commit $IMAGE_TAG, which does not contain them, so the job would run code you are not looking at. Commit or stash them first."
fi
BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo unknown)
echo "    checkout $(pwd)"
echo "    branch $BRANCH at $IMAGE_TAG ($(git log -1 --format=%s 2>/dev/null | cut -c1-60))"

step "Preflight: tenant '$TENANT_SLUG' database must already be provisioned"
# demo-health already exists. For a different tenant, provision it first as a pre-prod platform admin:
#   POST https://<pre-prod-app>/api/admin/organizations  {"name":"Load Test","slug":"loadtest"}
if ! az sql db show -g "$RG" --server "$SQL" -n "cmms-tenant-${TENANT_SLUG}" >/dev/null 2>&1; then
  die "database cmms-tenant-${TENANT_SLUG} does not exist. Provision the tenant first (POST /api/admin/organizations), then re-run."
fi
SKU=$(az sql db show -g "$RG" --server "$SQL" -n "cmms-tenant-${TENANT_SLUG}" --query "sku.name" -o tsv)
echo "    cmms-tenant-${TENANT_SLUG} present, sku=$SKU"
# Typing the slug is the second yes. A wrong TENANT_SLUG plus a habitual --execute must not be able to
# empty a tenant, so the name the operator typed has to match the tenant being targeted.
if [ "$CLEAR_TARGET" = "true" ] && [ "$CLEAR_CONFIRM" != "$TENANT_SLUG" ]; then
  die "--clear-target=$CLEAR_CONFIRM does not match the target tenant '$TENANT_SLUG'. Clearing deletes every row the artifact owns, so it must name the tenant it is about to empty."
fi
if [ "$EXECUTE" = "true" ] && [ "$TENANT_SLUG" = "demo-health" ] && [ "$CLEAR_TARGET" != "true" ]; then
  die "demo-health is SEEDED and the artifact carries its own ServiceLines on the same seed Guids, so a load collides on the primary key. Pass --clear-target=demo-health to empty its write set first, or point at an unseeded tenant."
fi

step "Preflight: the artifact must match THIS checkout's schema"
# The single cheapest check in this script, and the one whose absence cost the most. The artifact carries
# whatever columns the model had when it was generated; main moves; and a column added since then makes
# every row short. Without this the mismatch was found by the JOB, after uploading 3.7 GB and building an
# image, and the error was only readable from Log Analytics because the failed replica had been reaped.
#
# --verify-artifact opens no database and needs no credentials, so it is answerable here in seconds.
if [ -d "$ARTIFACT_DIR" ] && command -v dotnet >/dev/null 2>&1; then
  if ! dotnet run --project tools/Cmms.LoadDataRunner -c Release --no-launch-profile -- \
        --verify-artifact --artifact="$ARTIFACT_DIR" --tenant="$TENANT_SLUG" >/tmp/verify-artifact.$$ 2>&1; then
    tail -5 /tmp/verify-artifact.$$ >&2; rm -f /tmp/verify-artifact.$$
    die "the artifact does not match this checkout's model (see above). Regenerate it with tools/Cmms.LoadDataGenerator, or check out the commit it was generated against."
  fi
  grep -E "^  format|rows, in load order" /tmp/verify-artifact.$$ | head -2 | sed 's/^/    /'
  rm -f /tmp/verify-artifact.$$
else
  echo "    SKIPPED (no local artifact directory, or no dotnet); the job will verify it after staging."
fi

step "Bringing '$TENANT_SLUG' up to this checkout's schema (caj-cmms-migrate)"
# The loader REFUSES to load into a database with pending migrations, and it is right to: an artifact built
# from this checkout's model cannot be written to yesterday's tables. But that refusal happens inside the
# JOB, which means it is only reached after deploying the store, uploading 3.7 GB, and building an image.
# A real run died exactly there, on nothing but three migrations that had landed on main since:
#   Tenant 'demo-health' has 3 pending migration(s); run the migration job first. Refusing to load.
#
# It cannot be checked locally instead: pre-prod SQL denies public network access, so nothing outside the
# VNet can ask the database what it has applied. The only way to answer it early is to APPLY them early,
# which is what this does. Migrations here are additive and fan out to every tenant DB by design, and the
# migrate job is the sanctioned mechanism, so this is the same act the deploy pipeline performs.
#
# Built and deployed by digest for the same reason the load image is: caj-cmms-migrate was found sitting on
# a MUTABLE tag ('preprod-fix') pointing at an image from the previous day. Starting that would have applied
# nothing, succeeded, and left the load to fail again on the identical error.
echo "    building cmms-migrate:$IMAGE_TAG"
az acr build --registry "$ACR" --image "cmms-migrate:${IMAGE_TAG}" \
  --file tools/Cmms.MigrationRunner/Dockerfile .

MIG_DIGEST=$(az acr manifest list-metadata --registry "$ACR" --name cmms-migrate \
  --query "[?tags != null] | [?contains(tags, '${IMAGE_TAG}')].digest | [0]" -o tsv 2>&1 | tail -1)
case "$MIG_DIGEST" in
  sha256:*) ;;
  *) die "cannot resolve the digest for cmms-migrate:${IMAGE_TAG} after building it. az said: ${MIG_DIGEST:-<nothing>}" ;;
esac
MIG_IMAGE_REF="${ACR}.azurecr.io/cmms-migrate@${MIG_DIGEST}"
echo "    digest ${MIG_DIGEST}"

# The Container Apps region is DERIVED from the environment the job actually lives in, not taken from the
# template's default. migrate-job.bicep defaults computeLocation to 'eastus2' (it was written for dev) while
# pre-prod's environment is centralus, and a Container App Job cannot be moved by redeploying it:
#   InvalidResourceLocation: The resource 'caj-cmms-migrate' already exists in location 'centralus'...
#   A resource with the same name cannot be created in location 'eastus2'.
# Same failure the private endpoint hit for the same reason, so the same answer: read the region off the
# resource rather than assume it.
# az returns the DISPLAY name ("Central US"); ARM wants the canonical form ("centralus").
COMPUTE_LOCATION=$(az containerapp env show -n cae-cmms-preprod -g "$RG" --query location -o tsv 2>/dev/null \
  | tr -d ' ' | tr '[:upper:]' '[:lower:]')
[ -n "$COMPUTE_LOCATION" ] || die "cannot read the location of the Container Apps environment cae-cmms-preprod."
echo "    compute region ${COMPUTE_LOCATION}"

# Resource names are PASSED, not left to the template's computed defaults. migrate-job.bicep seeds
# sqlServerName with uniqueString(resourceGroup().id, location), and with location defaulting to westus3
# that resolves to a server which does not exist:
#   ResourceNotFound: The Resource 'Microsoft.Sql/servers/sql-cmms-preprod-442vh2mvpeiau' was not found.
# The real one is sql-cmms-preprod-eqqhakurrezvm. This script already looked both of them up at the top, so
# it passes what it FOUND rather than re-deriving a name that has already proven wrong once.
az deployment group create -g "$RG" -n "deploy-preprod-migrate-${IMAGE_TAG}" -f infra/migrate-job.bicep \
  -p environment=preprod containerImage="$MIG_IMAGE_REF" computeLocation="$COMPUTE_LOCATION" \
     acrName="$ACR" sqlServerName="$SQL" keyVaultName="$KV" >/dev/null \
  || die "migrate job deployment failed."

MIG_DEPLOYED=$(az containerapp job show -n caj-cmms-migrate -g "$RG" \
  --query "properties.template.containers[0].image" -o tsv 2>/dev/null)
if [ "$MIG_DEPLOYED" != "$MIG_IMAGE_REF" ]; then
  die "caj-cmms-migrate is set to run '$MIG_DEPLOYED' but this script built '$MIG_IMAGE_REF'. Refusing to start it: a stale migrate image applies nothing and reports success."
fi

MIG_EXEC=$(az containerapp job start -n caj-cmms-migrate -g "$RG" --query name -o tsv 2>/dev/null) \
  || die "could not start caj-cmms-migrate."
echo "    started $MIG_EXEC, waiting for it to finish"
# WAITED ON, not fired and forgotten. Loading against a half-migrated schema is the failure this step
# exists to prevent, so the load must not start until this has actually finished.
MIG_STATUS=""
for _ in $(seq 1 120); do
  MIG_STATUS=$(az containerapp job execution show -n caj-cmms-migrate -g "$RG" \
    --job-execution-name "$MIG_EXEC" --query "properties.status" -o tsv 2>/dev/null || echo "")
  case "$MIG_STATUS" in
    Succeeded|Failed) break ;;
  esac
  sleep 10
done
[ "$MIG_STATUS" = "Succeeded" ] \
  || die "caj-cmms-migrate finished as '${MIG_STATUS:-unknown}'. The schema is not current, so the load would be refused. Read its output from Log Analytics (ContainerJobName_s == 'caj-cmms-migrate') before retrying."
echo "    migrations applied ($MIG_STATUS)"

step "Deploying the load-test store (infra/modules/load-test-store.bicep)"
# Idempotent: re-deploying converges the account, its two containers, the role assignment, and the private
# endpoint. privateLink matches pre-prod's locked posture.
STORE=$(az deployment group create -g "$RG" -n "deploy-preprod-loadstore-${IMAGE_TAG}" \
  -f infra/modules/load-test-store.bicep \
  -p environment=preprod privateLink=true privateEndpointLocation="$VNET_LOCATION" \
     jobPrincipalId="$UAMI_PRINCIPAL" operatorPrincipalId="$OPERATOR_PRINCIPAL" operatorIpAddress="$OPERATOR_IP" \
  --query "properties.outputs.accountName.value" -o tsv) || die "load-test store deployment failed."
echo "    storage account: $STORE"

if [ "${SKIP_UPLOAD:-0}" = "1" ]; then
  step "Skipping artifact upload (SKIP_UPLOAD=1)"
else
  step "Uploading the artifact from $ARTIFACT_DIR to $STORE/artifact"
  [ -d "$ARTIFACT_DIR" ] || die "artifact directory '$ARTIFACT_DIR' not found. Generate it first with tools/Cmms.LoadDataGenerator, or pass ARTIFACT_DIR."
  [ -f "$ARTIFACT_DIR/manifest.json" ] || die "'$ARTIFACT_DIR' has no manifest.json, so it is not an artifact directory."
  command -v azcopy >/dev/null || die "azcopy not found. Install it (brew install azcopy) to upload the artifact, or stage it another way and re-run with SKIP_UPLOAD=1."
  SIZE=$(du -sh "$ARTIFACT_DIR" | cut -f1)
  echo "    $SIZE from $ARTIFACT_DIR, this takes a while"
  # Managed-identity-free path: azcopy uses the operator's own az login, since the account is RBAC-only
  # (allowSharedKeyAccess is false) and there is no account key to pass.
  AZCOPY_AUTO_LOGIN_TYPE=AZCLI azcopy copy "${ARTIFACT_DIR}/*" \
    "https://${STORE}.blob.core.windows.net/artifact/" --recursive --overwrite=ifSourceNewer \
    || die "artifact upload failed. If this is a permissions error, you need Storage Blob Data Contributor on $STORE."
fi

step "Building and pushing the load image ($ACR/cmms-load:$IMAGE_TAG) via az acr build"
az acr build --registry "$ACR" --image "cmms-load:${IMAGE_TAG}" \
  --file tools/Cmms.LoadDataRunner/Dockerfile .

# Deploy by DIGEST, not by tag. A tag is a mutable pointer, so "the job runs cmms-load:abc123" is a
# statement about a name rather than about any particular image. Resolving it here means the thing that
# gets deployed is exactly the thing that was just built.
# `[?tags != null]` first, and it is not defensive padding. Untagged manifests accumulate in the registry
# whenever a tag is moved to a newer build, and JMESPath's contains() THROWS on a null rather than skipping
# it, so the unfiltered query failed outright the moment a second image existed:
#   In function contains(), invalid type for value: None, expected one of: ['array', 'string']
# The script then died at the || below with a message about the digest, which named the symptom and hid
# the cause. stderr is shown now so the next failure explains itself.
DIGEST=$(az acr manifest list-metadata --registry "$ACR" --name cmms-load \
  --query "[?tags != null] | [?contains(tags, '${IMAGE_TAG}')].digest | [0]" -o tsv 2>&1 | tail -1)
case "$DIGEST" in
  sha256:*) ;;
  *) die "cannot resolve the digest for cmms-load:${IMAGE_TAG} after building it. az said: ${DIGEST:-<nothing>}" ;;
esac
IMAGE_REF="${ACR}.azurecr.io/cmms-load@${DIGEST}"
echo "    digest ${DIGEST}"

step "Deploying the load job (infra/load-job.bicep) targeting '$TENANT_SLUG', execute=$EXECUTE"
az deployment group create -g "$RG" -n "deploy-preprod-load-${IMAGE_TAG}" -f infra/load-job.bicep \
  -p environment=preprod containerImage="$IMAGE_REF" \
     tenantSlug="$TENANT_SLUG" loadExecute="$EXECUTE" clearTargetSlug="$CLEAR_CONFIRM" timeoutHours="$TIMEOUT_HOURS"

step "Preflight: the deployed job must carry the image we just built"
# Asserted rather than assumed. A run that silently used an older image reported "Succeeded" while doing
# nothing, which is the most expensive kind of wrong: it looks like the load worked.
DEPLOYED=$(az containerapp job show -n caj-cmms-load -g "$RG" --query "properties.template.containers[0].image" -o tsv 2>/dev/null)
if [ "$DEPLOYED" != "$IMAGE_REF" ]; then
  die "the job is set to run '$DEPLOYED' but this script built '$IMAGE_REF'. Refusing to start it: a stale image would run code you are not looking at."
fi
echo "    confirmed $DEPLOYED"

step "Starting caj-cmms-load"
az containerapp job start -n caj-cmms-load -g "$RG" >/dev/null
echo "    started. Recent executions:"
az containerapp job execution list -n caj-cmms-load -g "$RG" \
  --query "[0].{name:name, status:properties.status, start:properties.startTime}" -o table || true

cat <<NOTE

Done kicking off the load job (execute=$EXECUTE). To watch it and read the runner output:
  az containerapp job execution list -n caj-cmms-load -g $RG -o table
  az containerapp job logs show -n caj-cmms-load -g $RG --container load --tail 100

A job's replica is REAPED once it finishes, and the command above then answers "No replicas found",
which is exactly when you most want the output. Read it from Log Analytics instead, which keeps it:
  WS=\$(az containerapp env show -n cae-cmms-preprod -g $RG \
        --query "properties.appLogsConfiguration.logAnalyticsConfiguration.customerId" -o tsv)
  az monitor log-analytics query -w "\$WS" --analytics-query \
    "ContainerAppConsoleLogs_CL | where ContainerJobName_s == 'caj-cmms-load' \
     | where TimeGenerated > ago(1h) | order by TimeGenerated asc | project TimeGenerated, Log_s" -o table

NOTE

if [ "$EXECUTE" != "true" ]; then
  cat <<NOTE
This was a PLAN. The job staged the artifact, verified it, printed what it would write, and wrote nothing.
Re-run with --execute to load.
NOTE
else
  cat <<NOTE
This run is LOADING. It is resumable: every chunk is one transaction and its checkpoint is a blob in
$STORE/load-checkpoints, so a timeout or a restart resumes rather than reloading.

Afterward, the database posture still has to be restored (lnicoara/cmms#2910): rebuild indexes, re-trust
constraints, update statistics, and re-derive the Compliance Matrix snapshots, which the bulk copy bypasses.
NOTE
fi
