#!/usr/bin/env bash
# Empty a pre-prod tenant, keeping the admin login (lnicoara/cmms#3055).
#
# This is the DESTRUCTIVE counterpart to load-pre-prod.sh, and it is a separate script for exactly that
# reason. A script named for loading must not delete on the way past (#3025); a script named for clearing
# is allowed to, because that is what its name promises and what you typed.
#
# WHAT SURVIVES: the 'admin' login and the access group it belongs to. TargetCleaner holds them aside
# before the first DELETE and puts them back afterwards, so the tenant is still reachable when this
# finishes. Everything else in every table goes.
#
# WHAT GOES: every table in the tenant model, not an artifact's write set. "Empty this tenant" is not a
# question about a dataset, and clearing to some artifact's shape would leave rows behind for a reason
# nobody reading the output could see.
#
# It needs NO artifact and stages nothing. The job downloads no blob and opens no dataset.
#
# PLAN BY DEFAULT, and this is the one place in this repo where that default is kept. load-pre-prod.sh
# loads when you type its name because loading is recoverable. This is not: the rows are gone, and the
# artifact that could refill them takes an hour to generate and hours to load. So the default is to show
# you what would be deleted, and --execute is a second, deliberate act.
#
#   scripts/pre-prod/clear-pre-prod.sh                        # PLAN: counts rows, deletes nothing
#   scripts/pre-prod/clear-pre-prod.sh --execute              # actually empties demo-health
#   TENANT_SLUG=loadtest scripts/pre-prod/clear-pre-prod.sh --execute
#
# The tenant is named TWICE on a real clear: once by TENANT_SLUG (or its default) and once by --execute
# being typed against the plan you just read. The runner then refuses unless the slug it was handed matches
# the tenant it resolved from the catalog, so a stale variable cannot empty a tenant you did not mean.
set -euo pipefail

# Terminal output: colour, phase headers, die(), and the ERR trap that makes a silent exit impossible
# (lnicoara/cmms#3048). Sourced BEFORE the argument loop, because the loop calls die().
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
. "$SCRIPT_DIR/lib/output.sh"

SUBSCRIPTION="${SUBSCRIPTION:-08fd6214-7b3c-4b5e-a59b-320707791129}"
RG="${RG:-rg-cmms-preprod}"
TENANT_SLUG="${TENANT_SLUG:-demo-health}"
IMAGE_TAG="${IMAGE_TAG:-$(git rev-parse --short HEAD 2>/dev/null || echo manual)}"
TIMEOUT_HOURS="${TIMEOUT_HOURS:-6}"

EXECUTE=false
WATCH=true
for arg in "$@"; do
  case "$arg" in
    --execute) EXECUTE=true ;;
    --no-watch) WATCH=false ;;
    -h|--help) sed -n '2,28p' "$0"; exit 0 ;;
    *) die "Unknown argument: $arg" ;;
  esac
done

ROOT=$(git rev-parse --show-toplevel 2>/dev/null) || die "not inside a git checkout of this repo."
cd "$ROOT"
[ -f infra/load-job.bicep ] || die "$ROOT does not look like the cmms-data repo (no infra/load-job.bicep)."
command -v az >/dev/null || die "az CLI not found."
az account show >/dev/null 2>&1 || die "not signed in: run 'az login' first."

step "Selecting the pre-prod subscription ($SUBSCRIPTION)"
az account set --subscription "$SUBSCRIPTION" || die "cannot select subscription '$SUBSCRIPTION'."
az group show -n "$RG" >/dev/null 2>&1 || die "resource group '$RG' not found in subscription '$SUBSCRIPTION'."

step "Discovering pre-prod resources in $RG"
# Looked up, never computed. The Bicep templates derive these from uniqueString() and at least one of those
# derivations is already wrong for pre-prod, so the names passed to a deployment are names actually seen.
ACR=$(az acr list -g "$RG" --query "[0].name" -o tsv)
SQL=$(az sql server list -g "$RG" --query "[0].name" -o tsv)
KV=$(az keyvault list -g "$RG" --query "[0].name" -o tsv)
UAMI_PRINCIPAL=$(az identity show -g "$RG" -n id-cmms-api --query principalId -o tsv 2>/dev/null || true)
[ -n "$ACR" ] || die "no ACR in $RG."
[ -n "$SQL" ] || die "no SQL server in $RG."
[ -n "$KV" ] || die "no Key Vault in $RG."
[ -n "$UAMI_PRINCIPAL" ] || die "managed identity id-cmms-api not found in $RG."
fact "registry" "$ACR"
fact "sql server" "$SQL"
fact "tenant" "$TENANT_SLUG"
fact "mode" "$([ "$EXECUTE" = "true" ] && echo "CLEAR (destructive)" || echo "plan only")"

step "Preflight: tenant '$TENANT_SLUG' must exist"
az sql db show -g "$RG" --server "$SQL" -n "cmms-tenant-${TENANT_SLUG}" >/dev/null 2>&1 \
  || die "database cmms-tenant-${TENANT_SLUG} does not exist in $RG. Nothing to clear, and a typo in the slug should not deploy a job."
ok "cmms-tenant-${TENANT_SLUG} exists"

if [ "$EXECUTE" = "true" ]; then
  warn "this EMPTIES every table in '$TENANT_SLUG'"
  note "the 'admin' login and its access group are held aside and restored; everything else goes"
  note "refilling it means regenerating an artifact (about an hour) and loading it (hours more)"
fi

step "Building the clear image $ACR/cmms-load:$IMAGE_TAG"
# The same image the loader uses, because the clear IS the loader binary with --clear-only. A second image
# would be a second copy of the guard set (tenant Active, secret naming its OWN database on THIS
# environment's server, migrations current), and a DELETE needs those guards more than a load does.
DIGEST=$(az acr manifest list-metadata --registry "$ACR" --name cmms-load \
  --query "[?tags != null] | [?contains(tags, '${IMAGE_TAG}')].digest | [0]" -o tsv 2>&1 | tail -1)
case "$DIGEST" in
  sha256:*) IMAGE_REF="${ACR}.azurecr.io/cmms-load@${DIGEST}"; ok "using cmms-load@${DIGEST}" ;;
  *) die "cmms-load:${IMAGE_TAG} is not in $ACR. Run scripts/pre-prod/load-pre-prod.sh --plan once to build it, then re-run this. Refusing to build an image here: a clear should not be the thing that decides which binary pre-prod runs." ;;
esac

step "Deploying caj-cmms-load in clear-only mode"
COMPUTE_LOCATION=$(az containerapp env show -n cae-cmms-preprod -g "$RG" --query location -o tsv 2>/dev/null \
  | tr -d ' ' | tr '[:upper:]' '[:lower:]')
[ -n "$COMPUTE_LOCATION" ] || die "cannot read the location of the Container Apps environment cae-cmms-preprod."

# artifactProfile is required by the template but never read on this path: the entrypoint skips the
# download entirely when LOAD_CLEAR_ONLY is true. Passed as the tenant name so a human reading the job's
# env sees something true rather than a dataset this run has nothing to do with.
az deployment group create -g "$RG" -n "deploy-preprod-clear-${IMAGE_TAG}" -f infra/load-job.bicep \
  -p environment=preprod containerImage="$IMAGE_REF" artifactProfile="clear-${TENANT_SLUG}" \
     tenantSlug="$TENANT_SLUG" loadExecute="$EXECUTE" clearTargetSlug="$TENANT_SLUG" \
     clearOnly=true timeoutHours="$TIMEOUT_HOURS" \
  >/dev/null || die "clear job deployment failed."

step "Preflight: the deployed job must be the clear we just described"
# Read back, never assumed. This job is shared with the loader, so a previous deployment could have left it
# pointed at another tenant or configured to load; starting it without checking would run that instead.
DEPLOYED=$(az containerapp job show -n caj-cmms-load -g "$RG" --query "properties.template.containers[0].image" -o tsv 2>/dev/null)
[ "$DEPLOYED" = "$IMAGE_REF" ] || die "the job is set to run '$DEPLOYED' but this script deployed '$IMAGE_REF'. Refusing to start it."
env_of() { az containerapp job show -n caj-cmms-load -g "$RG" \
  --query "properties.template.containers[0].env[?name=='$1'].value | [0]" -o tsv 2>/dev/null; }
[ "$(env_of LOAD_CLEAR_ONLY)" = "True" ] || die "the job is not in clear-only mode (LOAD_CLEAR_ONLY=$(env_of LOAD_CLEAR_ONLY)). Refusing: it would load a dataset instead of clearing."
[ "$(env_of LOAD_TENANT_SLUG)" = "$TENANT_SLUG" ] || die "the job targets tenant '$(env_of LOAD_TENANT_SLUG)' but this run is about '$TENANT_SLUG'. Refusing."
[ "$(env_of LOAD_CLEAR_TARGET_SLUG)" = "$TENANT_SLUG" ] || die "the job's clear target is '$(env_of LOAD_CLEAR_TARGET_SLUG)', not '$TENANT_SLUG'. Refusing."
ok "clear-only, targeting $TENANT_SLUG"

step "Starting caj-cmms-load"
EXEC=$(az containerapp job start -n caj-cmms-load -g "$RG" --query name -o tsv 2>/dev/null || true)
[ -n "$EXEC" ] || die "could not start caj-cmms-load."
ok "started $EXEC"

if [ "$WATCH" != "true" ]; then
  step "Started"
  fact "execution" "$EXEC"
  note "check it with:"
  note "  az containerapp job execution show -n caj-cmms-load -g $RG \\"
  note "    --job-execution-name $EXEC --query \"properties.status\" -o tsv"
  exit 0
fi

step "Running"
START_EPOCH=$(date +%s)
while :; do
  STATUS=$(az containerapp job execution show -n caj-cmms-load -g "$RG" \
    --job-execution-name "$EXEC" --query "properties.status" -o tsv 2>/dev/null || echo Unknown)
  NOW=$(date +%s); ELAPSED=$((NOW - START_EPOCH))
  if [ -t 1 ]; then
    printf '\r  %s%02d:%02d:%02d%s  %s   ' "$C_DIM" \
      $((ELAPSED/3600)) $(((ELAPSED%3600)/60)) $((ELAPSED%60)) "$C_RESET" "$STATUS"
  fi
  case "$STATUS" in Succeeded|Failed|Stopped|Degraded) break ;; esac
  sleep 5
done
[ -t 1 ] && printf '\r%*s\r' 60 ''
ELAPSED=$(( $(date +%s) - START_EPOCH ))
DURATION=$(printf '%02d:%02d:%02d' $((ELAPSED/3600)) $(((ELAPSED%3600)/60)) $((ELAPSED%60)))

step "Result"
# WAITED for, not grabbed: the replica is reaped when the job ends and Log Analytics runs a minute or two
# behind, so reading once returns whatever happened to have arrived (lnicoara/cmms#3053).
WS=$(az containerapp env show -n cae-cmms-preprod -g "$RG" \
  --query "properties.appLogsConfiguration.logAnalyticsConfiguration.customerId" -o tsv 2>/dev/null || true)
OUT=$(mktemp)
if [ -n "$WS" ]; then
  for _ in $(seq 1 24); do
    az monitor log-analytics query -w "$WS" --analytics-query \
      "ContainerAppConsoleLogs_CL | where ContainerJobName_s == 'caj-cmms-load' | where TimeGenerated > ago(6h) | order by TimeGenerated asc | project Log_s" \
      --query "[].Log_s" -o tsv 2>/dev/null > "$OUT" || true
    command grep -qE 'cleared|PLAN ONLY|FAILED while|Refusing' "$OUT" && break
    sleep 5
  done
fi
SUMMARY=$(command grep -hE 'CLEARING|rows deleted|cleared|PLAN ONLY|FAILED while|Refusing|holding' "$OUT" 2>/dev/null | awk '!seen[$0]++' | tail -40 || true)
if [ -n "$SUMMARY" ]; then printf '%s\n' "$SUMMARY" | while IFS= read -r l; do note "$l"; done; echo; fi
rm -f "$OUT"

if [ "$STATUS" = "Succeeded" ]; then
  if [ "$EXECUTE" = "true" ]; then
    printf '  %s%sCLEARED %s in %s%s\n' "$C_BOLD" "$C_GREEN" "$TENANT_SLUG" "$DURATION" "$C_RESET"
    note "the 'admin' login was held aside and restored; the tenant is reachable"
    note "load a dataset with: scripts/pre-prod/load-pre-prod.sh --artifact-dir=<dir>"
  else
    printf '  %s%sPLANNED in %s%s\n' "$C_BOLD" "$C_GREEN" "$DURATION" "$C_RESET"
    note "nothing was deleted. Re-run with --execute to clear '$TENANT_SLUG'."
  fi
  fact "execution" "$EXEC"
else
  printf '  %s%sENDED AS %s after %s%s\n' "$C_BOLD" "$C_RED" "$STATUS" "$DURATION" "$C_RESET"
  note "read the full output with:"
  note "  WS=\$(az containerapp env show -n cae-cmms-preprod -g $RG \\"
  note "        --query \"properties.appLogsConfiguration.logAnalyticsConfiguration.customerId\" -o tsv)"
  note "  az monitor log-analytics query -w \"\\\$WS\" --analytics-query \\"
  note "    \"ContainerAppConsoleLogs_CL | where ContainerJobName_s == 'caj-cmms-load' | where TimeGenerated > ago(1h) | order by TimeGenerated asc | project TimeGenerated, Log_s\" -o table"
  die "$EXEC ended in state '$STATUS'."
fi
