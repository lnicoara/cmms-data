#!/usr/bin/env bash
# Load a generated synthetic artifact into a pre-prod tenant via the in-VNet load job (lnicoara/cmms#2917,
# per #2908 and epic #2731).
#
# Why a job and not a laptop: pre-prod SQL sets Deny Public Network Access, so a direct connection from
# outside the VNet is refused at login no matter what firewall rules exist.
#   mssql: login error: Connection was denied because Deny Public Network Access is set to Yes.
#
# IT LOADS. That is its name, and typing the name is the intent, so there is no second flag to confirm it.
# Pass --plan for the dry run: it stands everything up and runs the loader's plan, which verifies the
# staged artifact and writes nothing.
#
# Run from the repo root, logged in to the pre-prod subscription (az login + az account set).
#
# THIS SCRIPT DELETES NOTHING. It is named load-pre-prod, so it loads pre-prod, and a script that empties a
# database is a different act that belongs under a different name. The --clear-target flag that used to
# live here is commented out below rather than deleted, so what was removed and why stays on the record.
#
# It was load-tenant.sh. Renamed because the tooling only ever addresses pre-prod: the subscription, the
# resource group, the VNet, and the Container Apps environment are all pinned to it below, so "tenant" named
# the one part of the target that varies and left out the part that never does.
#
# What it did, and why it had to go: the flag emptied not the artifact's tables but their whole foreign-key
# closure (LoadPlan.ClearOrder), which under lnicoara/cmms#2993 is every table in the tenant model. It ran
# BEFORE the load, and its DELETEs commit one table at a time with no encompassing transaction, so any load
# that failed afterwards left the tenant emptied with nothing to put back. Repeated failed attempts at one
# load are exactly the case it handled worst.
#
# WHERE THE DATASET IS IS A REQUIRED INPUT: --artifact-dir=<absolute path>. Nothing is searched for and
# there is no default location. The script prints the path and the manifest's seed before using it.
#
#   DIR=$HOME/git/cmms-data/data/small        # generate it with scripts/pre-prod/generate-pre-prod-small.sh
#   scripts/pre-prod/load-pre-prod.sh --artifact-dir=$DIR             # loads demo-health
#   scripts/pre-prod/load-pre-prod.sh --artifact-dir=$DIR --plan      # stage, verify, write nothing
#   TENANT_SLUG=loadtest scripts/pre-prod/load-pre-prod.sh --artifact-dir=$DIR    # a different tenant
#   SKIP_UPLOAD=1 scripts/pre-prod/load-pre-prod.sh --artifact-dir=$DIR           # already staged in blob
#   scripts/pre-prod/load-pre-prod.sh --artifact-dir=$DIR --start-only --no-watch # start it and walk away
#
# STARTING A LOAD YOU CAN WALK AWAY FROM (lnicoara/cmms-data#14). The job runs in Azure and outlives this
# script either way: --no-watch already returns the prompt as soon as it is started, and Ctrl-C during the
# watch stops the watching rather than the load. What used to tie an operator to their laptop was getting
# TO the start. --start-only skips the preparation whose output is already in Azure (the artifact upload,
# the local artifact verification, the store deploy, the image build) and refuses if either premise is
# missing: the tag has to be in the registry and the profile's prefix has to hold the dataset. It skips no
# assertion. The deployed image, the deployed profile, and the job's delete configuration are all still
# read back before anything starts.
#
# --profile=<name> names the blob prefix the dataset stages under. It follows the directory's own name
# unless given, so two different directories cannot quietly share one address in the store.
#
# The script STAYS with the run, polling this execution every 5 seconds and reporting only this execution,
# until it ends. Ctrl-C stops the watching and never the job. --no-watch returns the prompt immediately.
set -euo pipefail

# The fixed pre-prod subscription, same id deploy-pre-prod.sh and seed-tenant.sh pin. The script SELECTS it
# below rather than trusting the active az context, so it can never build/deploy/run against another
# subscription that happens to also have an rg-cmms-preprod.
SUBSCRIPTION="${SUBSCRIPTION:-08fd6214-7b3c-4b5e-a59b-320707791129}"
RG="${RG:-rg-cmms-preprod}"
# demo-health, the existing pre-prod tenant. It is SEEDED, and the artifact ships its own ServiceLines rows
# on the same well-known Guids DemoDataSeeder writes (ServiceLineSeed), so those rows collide on the primary
# key. --clear-target used to be what made a seeded tenant a valid target, by emptying the artifact's write
# set first. With the clear gone, a seeded tenant is simply not a valid target for this script, and the
# preflight below says so instead of offering to empty it.
TENANT_SLUG="${TENANT_SLUG:-demo-health}"
# The dataset's NAME, which is the blob prefix it stages under and the address the job is told to read.
# Left empty here on purpose: it is derived from the artifact directory below unless --profile overrides it,
# so the path stays the single thing an operator has to type.
PROFILE="${PROFILE:-}"
IMAGE_TAG="${IMAGE_TAG:-$(git rev-parse --short HEAD 2>/dev/null || echo manual)}"
TIMEOUT_HOURS="${TIMEOUT_HOURS:-12}"

# Terminal output: colour, phase headers, and die(). Sourced BEFORE the argument loop, because the loop
# calls die() and defining it afterwards meant a flag with a missing value died with 'die: command not
# found' instead of its own message. Resolved from THIS script's location, not the working directory, so
# the script works from anywhere. lnicoara/cmms#3046.
# This script's own directory, resolved once. Everything that reaches a sibling script or the output
# library goes through it, rather than re-deriving BASH_SOURCE at each call site: inside a function
# BASH_SOURCE[0] means the file the function was DEFINED in, which is right here but is subtle enough that
# a later reader can reasonably get it wrong, and a wrong answer is a sibling script "not found".
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
. "$SCRIPT_DIR/lib/output.sh"

# The digest ACR holds for $1:$IMAGE_TAG, or whatever az said when there is none. Called before a build to
# decide whether to run one, and after a build to resolve what to deploy, so the two questions share an
# answer instead of drifting apart.
image_digest() {
  az acr manifest list-metadata --registry "$ACR" --name "$1" \
    --query "[?tags != null] | [?contains(tags, '${IMAGE_TAG}')].digest | [0]" -o tsv 2>&1 | tail -1
}

# Build only when the tag is missing from the registry. IMAGE_TAG is HEAD's short sha and the preflight
# below refuses to run while any file the Dockerfile COPYs is uncommitted, so a tag already in ACR was
# built from exactly this source and rebuilding it reproduces the same image. Two remote builds at roughly
# five minutes each ran on every repeat invocation for no change in what got deployed.
#
# What does NOT change: the digest lookup still decides what is deployed, and the job is still asserted to
# carry it before anything starts. A skipped build is never a skipped assertion.
#
# FORCE_BUILD=1 rebuilds regardless, for a base-image refresh or a registry someone has edited by hand.
# IMAGE_TAG=manual (no git) never skips, because then the tag is not a claim about any source at all.
# Cmms.Infrastructure, packed OUT OF THE LOCAL cmms CHECKOUT into this repo's build context, moments
# before the image is built. lnicoara/cmms#3026.
#
# The image needs the product's EF model and cannot reach outside its own build context: `az acr build`
# uploads this directory and builds remotely in Azure, so a project reference to a sibling checkout names
# a path the build machine does not have. The alternatives were a hosted feed, which costs a credential
# that then has to cross into a remote build without landing in the image, or vendoring the source, which
# is the fork this whole arrangement exists to avoid. Packing locally costs neither.
#
# It cannot go stale, which is the property that makes it better than a feed here rather than merely
# cheaper: the .nupkg files are produced from the checkout seconds before they are consumed.
#
# The COMMIT IS ASSERTED, not assumed. Packing whatever the checkout happens to be at, under the version
# this repo is pinned to, would put a different model behind that version number, which is precisely the
# drift the pin exists to prevent, and it would be invisible: the build would succeed and the artifact
# would fit nothing.
pack_pinned_cmms() {
  # WHICH cmms, and WHERE, are both answered by clone-cmms-from-pre-prod.sh (lnicoara/cmms#3050). It
  # resolves the commit pre-prod's schema is at, clones cmms at that commit into .cmms/<commit>/, and
  # prints the path. Already-cloned is a no-op, so this costs nothing after the first run for a pin.
  #
  # This used to pack out of $CMMS_REPO (default ~/git/cmms) and REFUSE unless that checkout happened to
  # be sitting on the pinned commit, which is a load test telling the operator to move their working tree
  # so it can build. Both the generator and the loader are pinned to pre-prod; the pin is a fact about the
  # deployed environment that the tooling reads, never something anybody maintains and never a question to
  # put to the operator.
  local clone commit
  clone=$("$SCRIPT_DIR/clone-cmms-from-pre-prod.sh" --print) \
    || die "could not obtain a cmms checkout at pre-prod's commit; see above."
  commit=$(basename "$clone")

  note "packing cmms $commit from $clone"
  rm -rf .packages && mkdir -p .packages
  local log; log=$(mktemp)

  # BUILT ONCE, then packed with --no-build. Three sequential `dotnet pack` runs are not independent:
  # Cmms.Infrastructure project-references the other two, so packing Domain rewrites output that
  # Infrastructure's incremental state was resting on, and the third pack then failed with
  #   NU5026: The file '...Cmms.Infrastructure.runtimeconfig.json' to be packed was not found on disk.
  # on a warm tree while succeeding on a cold one. An operator hits that as an intermittent failure whose
  # message names a file nobody asked about. Building the whole set first makes the packs independent.
  #
  # The version goes to BOTH commands. --no-build packs what the build produced, so a version passed only
  # to pack would name one thing and contain another.
  if ! dotnet build "$clone/src/Cmms.Infrastructure" -c Release -p:Version="1.0.0-g$commit" >"$log" 2>&1; then
    tail -20 "$log" >&2
    die "building cmms $commit from $clone failed (full log: $log)."
  fi
  for proj in Cmms.Domain Cmms.Application Cmms.Infrastructure; do
    if ! dotnet pack "$clone/src/$proj" -c Release -p:Version="1.0.0-g$commit" --no-build -o .packages >"$log" 2>&1; then
      tail -20 "$log" >&2
      die "packing $proj from $clone failed (full log: $log)."
    fi
  done
  rm -f "$log"

  # The pin the csproj files resolve against has to BE the commit just packed, or the restore asks for a
  # version that is not in .packages/ and fails naming a package rather than a stale pin. Derived from
  # pre-prod and written here, rather than maintained by hand in Directory.Build.props and able to
  # disagree with the environment it claims to describe.
  CMMS_VERSION="1.0.0-g$commit"
  ok "packed Cmms.Domain, Cmms.Application, Cmms.Infrastructure at $CMMS_VERSION"
}

FORCE_BUILD="${FORCE_BUILD:-0}"
build_if_absent() {
  local repo="$1" dockerfile="$2" found
  if [ "$FORCE_BUILD" != "1" ] && [ "$IMAGE_TAG" != "manual" ]; then
    found=$(image_digest "$repo")
    case "$found" in
      sha256:*) ok "${repo}:${IMAGE_TAG} already in $ACR, not rebuilding (FORCE_BUILD=1 to force)"; return 0 ;;
    esac
  fi
  # CAPTURED. `az acr build` streams the whole remote Docker build, hundreds of layer lines, and on a
  # success not one of them is information. On a failure every one of them is, so they go to a file the
  # die names rather than to the screen.
  pack_pinned_cmms
  note "building ${repo}:${IMAGE_TAG} (remote, a few minutes)"
  local log; log=$(mktemp)
  # CMMS_VERSION is a plain build arg, not a secret: it is a version string, and it is already visible in
  # the image's own assembly metadata, which is where TargetBuild reads it back from at run time.
  if ! az acr build --registry "$ACR" --image "${repo}:${IMAGE_TAG}" --file "$dockerfile" \
       --build-arg CMMS_VERSION="${CMMS_VERSION:-}" . >"$log" 2>&1; then
    tail -30 "$log" >&2
    die "building ${repo}:${IMAGE_TAG} failed (full log: $log)."
  fi
  rm -f "$log"
  ok "built ${repo}:${IMAGE_TAG}"
}

# The script stays with the run and polls it every 5 seconds rather than printing a command to go run.
# --no-watch returns the prompt as soon as the job is started.
WATCH=true

# Deploy and start against what is already in Azure. lnicoara/cmms-data#14.
#
# Everything it skips is preparation whose output is already sitting in Azure: the artifact upload, the
# local artifact verification, the load-test store deploy, and the image build. Everything it keeps is an
# assertion. That split is deliberate, because the fast path must not also be the path with fewer checks:
# the deployed image is still read back and compared, the deployed profile is still checked against this
# run's dataset, and the job is still confirmed to be configured to delete nothing.
#
# It REFUSES rather than proceeds when its premises are not met. Both are read out of Azure rather than
# assumed: the tag has to resolve to a digest in the registry, and the profile's blob prefix has to hold
# the dataset. Without the first, "skip the build" would silently deploy an older image, which is the
# failure build_if_absent's digest assertion exists to prevent. Without the second, the job would stage
# whatever happens to be at that address.
#
# It does NOT skip the loader's own verification. The container still opens every chunk and checks the
# numbering and row totals before writing a row, so a truncated download still fails before the load
# rather than during it. What is skipped locally is the early copy of that check, which reads all 584
# chunk files off disk and is the slowest thing in the script once the uploads are gone.
START_ONLY=false
# LOADS by default. The script is named load-pre-prod, and typing its name is the statement of intent, so
# requiring --execute on top of that was asking the same question twice. Pass --plan for the dry run.
#
# The layers BELOW this one still default to false, and deliberately: load-job.bicep's loadExecute and the
# entrypoint's LOAD_EXECUTE are what catch a hand-run `az deployment group create` plus `job start`, where
# nobody has said anything about loading. This is the layer where somebody did.
EXECUTE=true
# REMOVED: this script no longer deletes anything. Kept, commented, because the reasoning is worth having
# next to its absence.
#
# # OFF by default, and it stays off unless the operator NAMES the tenant they mean to wipe. A default of
# # true would have collapsed the loader's two-flag delete fence back into one, because --execute alone would
# # then delete, and it would have done so for whatever slug TENANT_SLUG happened to hold.
# CLEAR_TARGET=false
# CLEAR_CONFIRM=""
for arg in "$@"; do
  case "$arg" in
    --plan) EXECUTE=false ;;
    # Accepted and ignored, not rejected. It is what every existing command, note and shell history says,
    # and it asks for exactly what now happens anyway, so refusing it would be pedantry with a stack trace.
    --execute) EXECUTE=true ;;
    # REMOVED (see the header): --clear-target used to empty the target tenant before loading it.
    #
    # --clear-target=*) CLEAR_TARGET=true; CLEAR_CONFIRM="${arg#*=}" ;;
    # --clear-target) die "--clear-target needs the tenant slug you mean to wipe: --clear-target=<slug>" ;;
    #
    # REFUSED rather than ignored. An operator with the old command in their shell history would otherwise
    # have the flag swallowed by the unknown-argument case, or worse, silently accepted, and would read the
    # run that followed as a load that had cleared first.
    --clear-target|--clear-target=*)
      die "--clear-target has been removed from this script. It deletes data, and this script loads pre-prod.
    What it did: emptied every table in the artifact's foreign-key closure, which is now the whole tenant
    database, before the load and with no way to put it back if the load then failed.
    Load into an unseeded tenant instead: TENANT_SLUG=<slug> $0 --artifact-dir=... --execute" ;;
    --profile=*) PROFILE="${arg#*=}" ;;
    --profile) die "--profile needs the blob prefix you mean to stage under: --profile=small" ;;
    --no-watch) WATCH=false ;;
    # Deploy and start using what is ALREADY in Azure, skipping the preparation that puts it there.
    # lnicoara/cmms-data#14. Pair it with --no-watch for a run you can walk away from.
    --start-only) START_ONLY=true ;;
    --artifact-dir=*) ARTIFACT_DIR="${arg#*=}" ;;
    --artifact-dir) die "--artifact-dir needs the ABSOLUTE path of the dataset directory: --artifact-dir=/abs/path" ;;
    # The range is the header block above, which grew when the clear was removed from it. It was 2,30 and
    # silently truncated --help mid-sentence after that edit, which is the failure mode of a line number
    # standing in for a section.
    -h|--help) sed -n '2,40p' "$0"; exit 0 ;;
    # Through die() like every other refusal here. It used to echo and exit 2 directly, so the one message
    # an operator sees for a typo was the only unstyled, unprefixed line the script produced.
    *) die "Unknown argument: $arg" ;;
  esac
done

# WHERE THE DATASET IS, said out loud on every run. There is no discovery here and no default.
#
# The script used to find the dataset itself: /tmp/gen-<profile>, else data/<profile> in a cmms-data
# checkout beside this repo. Two copies of 'small' with different manifests then existed on one machine,
# and the run that hit that stopped to ask which was meant. A load that moves a quarter of a million rows
# into pre-prod should not open by guessing at a path, so the path is an input the operator supplies.
[ -n "${ARTIFACT_DIR:-}" ] || die "--artifact-dir=<absolute path> is required. Name the dataset directory you mean to load; nothing is searched for and there is no default. For example:
    TENANT_SLUG=loadtest scripts/pre-prod/load-pre-prod.sh --artifact-dir=\$HOME/git/cmms-data/data/small --execute"

# Absolute only. A relative path resolves against whatever directory the caller happened to be standing in,
# so the same command would name different datasets from different shells, and this script's whole subject
# is which bytes reach pre-prod.
case "$ARTIFACT_DIR" in
  /*) ;;
  *) die "--artifact-dir must be an ABSOLUTE path, and '$ARTIFACT_DIR' is not one. Absolute means the command names the same dataset from any directory." ;;
esac
[ -d "$ARTIFACT_DIR" ] || die "'$ARTIFACT_DIR' is not a directory."
[ -f "$ARTIFACT_DIR/manifest.json" ] || die "'$ARTIFACT_DIR' has no manifest.json, so it is not an artifact directory."

# The name follows the directory unless the operator overrode it. Deriving it rather than defaulting it
# means the staged prefix and the local directory always agree without a second thing to keep in sync.
[ -n "$PROFILE" ] || PROFILE="$(basename "$ARTIFACT_DIR")"

command -v az >/dev/null || die "az CLI not found."
[ -f infra/load-job.bicep ] || die "run from the repo root (infra/load-job.bicep not found)."
az account show >/dev/null 2>&1 || die "not logged in: run 'az login' first."

step "Using the '$PROFILE' dataset at $ARTIFACT_DIR"
# The seed, printed because a path is not evidence of what is at the end of it. Nothing in a dataset
# directory shows its age, so a copy left over from an older profile is indistinguishable from a current
# one until the seed is read. Best-effort: an unreadable manifest is the verifier's problem, and
# the verifier runs a few steps down regardless.
if command -v python3 >/dev/null 2>&1; then
  MANIFEST=$(python3 -c "import json,sys;d=json.load(open(sys.argv[1]));print('%s|%s' % (d['seed'], format(d['totalRows'], ',')))" \
    "$ARTIFACT_DIR/manifest.json" 2>/dev/null || true)
  [ -n "$MANIFEST" ] && { fact "seed" "${MANIFEST%%|*}"; fact "rows" "${MANIFEST##*|}"; }
fi

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
#
# Asked of several providers rather than one. This was a single call to api.ipify.org, and when that host
# became unreachable from the operator's network the whole load stopped at "cannot determine this
# machine's public IP" with everything else in working order. The answer is not the property of any one
# service, so a provider being down should cost a retry against the next one and nothing else.
OPERATOR_IP=""
for ip_source in https://api.ipify.org https://ifconfig.me/ip https://icanhazip.com https://checkip.amazonaws.com; do
  candidate=$(curl -fsS --max-time 8 "$ip_source" 2>/dev/null | tr -d '[:space:]' || true)
  # Shape-checked before it is trusted: a captive portal or an error page returns 200 with a body that is
  # not an address, and that would reach the firewall rule as a garbage value.
  case "$candidate" in
    [0-9]*.[0-9]*.[0-9]*.[0-9]*) OPERATOR_IP="$candidate"; break ;;
  esac
done
[ -n "$OPERATOR_IP" ] || die "cannot determine this machine's public IP, which the artifact upload needs to be allowed through. Tried api.ipify.org, ifconfig.me, icanhazip.com, and checkip.amazonaws.com, and none answered."
fact "registry" "$ACR"
fact "sql server" "$SQL"
fact "tenant" "$TENANT_SLUG"
fact "vnet" "$VNET ($VNET_LOCATION)"
fact "operator ip" "$OPERATOR_IP"
fact "image tag" "$IMAGE_TAG"
fact "mode" "$([ "$EXECUTE" = "true" ] && echo "LOAD" || echo "plan only")"

step "Preflight: this checkout must be the code you think is running"
# IMAGE_TAG comes from HEAD, so the tag is a claim about which source built the image. Two ways that claim
# goes wrong, and both have bitten this load already:
#   - uncommitted changes: the tag names a commit that does not contain them
#   - running from the wrong checkout: the tag names a commit that lacks the fix you meant to run
# The second one silently deployed a binary WITHOUT the LOAD_EXECUTE fix and the job reported success.
# Scoped to the paths the Dockerfile actually COPYs, which after lnicoara/cmms#3026 no longer include
# src/ (Cmms.Infrastructure is a package) and do include nuget.config and profiles/. A modified README
# cannot change the built image, so refusing to run over one is a false alarm that blocks a real load for
# no reason. It did exactly that on the first run after this guard was added.
DIRTY=$(git status --porcelain -- Directory.Build.props global.json nuget.config profiles tools 2>/dev/null)
if [ -n "$DIRTY" ]; then
  echo "$DIRTY" >&2
  # WARNS now, where it used to refuse. `az acr build ... .` uploads the working tree, so an uncommitted
  # change really does reach the image; the objection was only ever that the TAG would name a commit
  # without it. Running an uncommitted fix is the normal way to test one.
  #
  # The tag is suffixed rather than left alone, and that is the part that matters. build_if_absent SKIPS
  # the build when the registry already holds this tag, which it does after any earlier run at this commit.
  # Leaving the tag as the bare sha would therefore deploy the OLD image and report success, which is the
  # exact "ran code you are not looking at" failure the refusal existed to prevent. A distinct tag cannot
  # be already-present, so the build always runs and the image always carries this working tree.
  # Hashed on the CONTENT that goes into the image, not on `git status` output. The first version hashed
  # the porcelain listing, which changes when a file is merely staged or renamed, so an unchanged working
  # tree produced a new tag and a new five-minute remote build on every single run. Content means two runs
  # with no edit between them resolve to the same tag, build_if_absent finds it, and the build is skipped
  # exactly as it is for a clean tree. Edit anything and the tag moves, which is the property that matters.
  #
  # Tracked modifications come from the diff; untracked files have to be read directly, because a file git
  # has never seen contributes nothing to `git diff` and would otherwise be invisible to the hash while
  # being perfectly visible to the Docker build context.
  DIRTY_HASH=$( { git diff HEAD -- Directory.Build.props global.json nuget.config profiles tools
                  git ls-files --others --exclude-standard -- Directory.Build.props global.json nuget.config profiles tools \
                    | sort | while IFS= read -r f; do printf '%s\n' "$f"; cat "$f"; done
                } | shasum | cut -c1-8 )
  IMAGE_TAG="${IMAGE_TAG}-dirty-${DIRTY_HASH}"
  warn "uncommitted files above WILL go into the image; tagging it ${IMAGE_TAG}"
fi
BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo unknown)
fact "checkout" "$(pwd)"
fact "branch" "$BRANCH"
fact "head" "$(git log -1 --format='%h %s' 2>/dev/null | cut -c1-64)"

step "Preflight: tenant '$TENANT_SLUG' database must already be provisioned"
# demo-health already exists. For a different tenant, provision it first as a pre-prod platform admin:
#   POST https://<pre-prod-app>/api/admin/organizations  {"name":"Load Test","slug":"loadtest"}
if ! az sql db show -g "$RG" --server "$SQL" -n "cmms-tenant-${TENANT_SLUG}" >/dev/null 2>&1; then
  die "database cmms-tenant-${TENANT_SLUG} does not exist. Provision the tenant first (POST /api/admin/organizations), then re-run."
fi
SKU=$(az sql db show -g "$RG" --server "$SQL" -n "cmms-tenant-${TENANT_SLUG}" --query "sku.name" -o tsv)
ok "cmms-tenant-${TENANT_SLUG} exists (sku $SKU)"
# REMOVED with the flag: this guarded the clear, and there is no clear to guard.
#
# # Typing the slug is the second yes. A wrong TENANT_SLUG plus a habitual --execute must not be able to
# # empty a tenant, so the name the operator typed has to match the tenant being targeted.
# if [ "$CLEAR_TARGET" = "true" ] && [ "$CLEAR_CONFIRM" != "$TENANT_SLUG" ]; then
#   die "--clear-target=$CLEAR_CONFIRM does not match the target tenant '$TENANT_SLUG'. Clearing deletes every row the artifact owns, so it must name the tenant it is about to empty."
# fi
#
# GONE TOO, and the collision it named is fixed rather than refused. It read:
#
#   if [ "$EXECUTE" = "true" ] && [ "$TENANT_SLUG" = "demo-health" ]; then
#     die "demo-health is SEEDED and the artifact carries its own ServiceLines on the same seed Guids..."
#   fi
#
# The premise was true and the conclusion was not. The artifact really did collide, on two counts: it
# emitted ServiceLines at the hardcoded Guid a MIGRATION puts in every tenant database, and at the codes
# "CE" and "FE" that DemoDataSeeder writes, against a UNIQUE index on Code. Both are now derived from the
# dataset's seed (Generator.EmitServiceLines), so the artifact no longer claims values the schema or the
# seeder already owns, and a seeded tenant is an ordinary target.
#
# Worth keeping on the record: the old guard, the loader's ownership refusal, and this tooling's whole
# "load into a dedicated unseeded tenant" instruction were all wrong about WHERE the collision was. The
# migration-seeded Guid is in every tenant, so an unseeded one collided just the same.

step "Preflight: the artifact must match THIS checkout's schema"
# The single cheapest check in this script, and the one whose absence cost the most. The artifact carries
# whatever columns the model had when it was generated; main moves; and a column added since then makes
# every row short. Without this the mismatch was found by the JOB, after uploading 3.7 GB and building an
# image, and the error was only readable from Log Analytics because the failed replica had been reaped.
#
# --verify-artifact opens no database and needs no credentials, so it is answerable here in seconds.
#
# Skipped by --start-only, and it is the slowest thing left in the script once the uploads are gone:
# verifying the full artifact opens all 584 chunk files and reads each one's gzip trailer. The check is not
# lost, only moved. The container repeats it after staging, before it writes a row, so a mismatch still
# stops the load; what --start-only gives up is learning about it here instead of a minute later.
if [ "$START_ONLY" = "true" ]; then
  note "SKIPPED by --start-only; the job verifies the staged artifact before it writes anything"
elif [ -d "$ARTIFACT_DIR" ] && command -v dotnet >/dev/null 2>&1; then
  if ! dotnet run --project tools/Cmms.LoadDataRunner -c Release --no-launch-profile -- \
        --verify-artifact --artifact="$ARTIFACT_DIR" --tenant="$TENANT_SLUG" >/tmp/verify-artifact.$$ 2>&1; then
    tail -5 /tmp/verify-artifact.$$ >&2; rm -f /tmp/verify-artifact.$$
    die "the artifact does not match this checkout's model (see above). Regenerate it with tools/Cmms.LoadDataGenerator, or check out the commit it was generated against."
  fi
  grep -E "^  format|rows, in load order" /tmp/verify-artifact.$$ | head -2 \
    | sed 's/^ *//' | while IFS= read -r l; do note "$l"; done
  rm -f /tmp/verify-artifact.$$
  ok "artifact matches this checkout's model"
else
  warn "SKIPPED (no local artifact directory, or no dotnet); the job verifies it after staging"
fi

# THE MIGRATE JOB IS NOT RUN HERE ANY MORE (lnicoara/cmms#2978).
#
# What stood here built cmms-migrate, deployed caj-cmms-migrate, started it, and waited for it, on EVERY
# invocation. That is a load-test script deciding and then changing what schema pre-prod runs, and it is
# not entitled to. It also measurably did so: #2993 found pre-prod's tenant schema 85 commits AHEAD of the
# API image pre-prod was actually serving, because each load dragged the schema toward whatever was checked
# out while the application stayed where it had been promoted. Establishing a schema belongs to the deploy
# path, at a different time, under a deliberate decision.
#
# It was here for a real reason, and that reason is now handled by saying so rather than by acting. The
# loader refuses a database with pending migrations, and the refusal happens inside the JOB, after the
# store is deployed and the artifact is uploaded. It cannot be answered locally either: pre-prod SQL denies
# public network access, so nothing outside the VNet can ask the database what it has applied.
#
# So a load CAN still die on pending migrations. When it does, the message is unambiguous
#   Tenant '<slug>' has N pending migration(s); run the migration job first. Refusing to load.
# and the answer is to run the migration deliberately, knowing it fans out to every tenant database:
#   az containerapp job start -n caj-cmms-migrate -g rg-cmms-preprod
# then re-run this. That is one clear extra step on the runs that need it, in place of an unannounced
# schema change on all of them.

if [ "$START_ONLY" = "true" ]; then
  # The store is what HOLDS the already-staged artifact --start-only depends on, so under this flag it
  # necessarily exists and converging it again would be a deployment to reach a name. Read the name
  # instead. The account is the one load-test-store.bicep computes, so it is found by its prefix rather
  # than by a hardcoded value that could drift from the template.
  step "Reading the load-test store (--start-only: not deploying it)"
  STORE=$(az storage account list -g "$RG" \
    --query "[?starts_with(name,'stloadtest')].name | [0]" -o tsv 2>/dev/null) \
    || die "could not list storage accounts in $RG."
  [ -n "$STORE" ] && [ "$STORE" != "None" ] \
    || die "no stloadtest* storage account in $RG, so nothing has ever been staged here. Run without --start-only once to create it."
else
  step "Deploying the load-test store (infra/modules/load-test-store.bicep)"
  # Idempotent: re-deploying converges the account, its two containers, the role assignment, and the private
  # endpoint. privateLink matches pre-prod's locked posture.
  STORE=$(az deployment group create -g "$RG" -n "deploy-preprod-loadstore-${IMAGE_TAG}" \
    -f infra/modules/load-test-store.bicep \
    -p environment=preprod privateLink=true privateEndpointLocation="$VNET_LOCATION" \
       jobPrincipalId="$UAMI_PRINCIPAL" operatorPrincipalId="$OPERATOR_PRINCIPAL" operatorIpAddress="$OPERATOR_IP" \
    --query "properties.outputs.accountName.value" -o tsv) || die "load-test store deployment failed."
fi
fact "storage account" "$STORE"

# Each profile owns a prefix under the artifact container, and the job is pointed at that prefix rather
# than at the container. Before lnicoara/cmms#2979 every dataset was uploaded to the container itself, so
# "the artifact" was a single global address and --profile named a directory on the operator's laptop and
# nothing else. Uploading 'small' over 'full' therefore replaced the chunk names the two happened to share
# and left the other 379 of full's files in place, and the job loaded the union.
ARTIFACT_PREFIX="https://${STORE}.blob.core.windows.net/artifact/${PROFILE}"

if [ "$START_ONLY" = "true" ]; then
  step "Skipping artifact upload (--start-only); trusting $ARTIFACT_PREFIX to already hold '$PROFILE'"
elif [ "${SKIP_UPLOAD:-0}" = "1" ]; then
  step "Skipping artifact upload (SKIP_UPLOAD=1); trusting $ARTIFACT_PREFIX to already hold '$PROFILE'"
else
  step "Uploading the '$PROFILE' artifact from $ARTIFACT_DIR to $STORE/artifact/$PROFILE"
  [ -d "$ARTIFACT_DIR" ] || die "artifact directory '$ARTIFACT_DIR' not found. Generate it first with tools/Cmms.LoadDataGenerator, or pass ARTIFACT_DIR."
  [ -f "$ARTIFACT_DIR/manifest.json" ] || die "'$ARTIFACT_DIR' has no manifest.json, so it is not an artifact directory."
  command -v azcopy >/dev/null || die "azcopy not found. Install it (brew install azcopy) to upload the artifact, or stage it another way and re-run with SKIP_UPLOAD=1."
  SIZE=$(du -sh "$ARTIFACT_DIR" | cut -f1 || true)
  fact "uploading" "$SIZE from $ARTIFACT_DIR"
  # Managed-identity-free path: azcopy uses the operator's own az login, since the account is RBAC-only
  # (allowSharedKeyAccess is false) and there is no account key to pass.
  #
  # sync, not copy, and the two flags matter for separate reasons. --delete-destination makes the prefix
  # END UP as the artifact rather than absorb it, so a regeneration that emits fewer chunks than last time
  # cannot leave the surplus behind. --compare-hash replaces the old --overwrite=ifSourceNewer: generation
  # is deterministic and its output carries no meaningful mtime, so a byte-different regenerated chunk with
  # an older timestamp than the blob was silently skipped as "not newer".
  #
  # The hash cache is left at its default, which is an extended attribute on the file itself. The
  # HiddenFiles mode writes a .<name>.md5 sidecar next to every chunk instead, and those sidecars are
  # ordinary files: the next sync would upload them, and the blob count below would stop matching the
  # local file count for a reason that has nothing to do with the artifact.
  # CAPTURED, not streamed. azcopy prints a redrawing progress meter, a per-file scan, and an eleven-line
  # end-of-job block; none of it survives being interleaved and all of it scrolls the run's actual
  # decisions off the screen. The two numbers that matter are printed instead.
  AZ_LOG=$(mktemp)
  # --output-level is NOT set. It was 'essential', which suppresses the end-of-job summary the two lines
  # below parse, so they found nothing. Since the output is captured to a file either way, there was
  # never a reason to make azcopy quieter: quiet was the screen's problem and capture already solved it.
  if ! AZCOPY_AUTO_LOGIN_TYPE=AZCLI azcopy sync "${ARTIFACT_DIR}" "$ARTIFACT_PREFIX" \
       --recursive --delete-destination=true --compare-hash=MD5 >"$AZ_LOG" 2>&1; then
    tail -20 "$AZ_LOG" >&2
    die "artifact upload failed (full output: $AZ_LOG). If this is a permissions error, you need Storage Blob Data Contributor on $STORE."
  fi

  # `|| true` on both, and it is not defensive padding. Under `set -e` an assignment carries the exit
  # status of its command substitution, so a grep that matches nothing KILLS THE SCRIPT — with no message,
  # because grep prints nothing when it fails to match. That is exactly what happened: a completed 209-file
  # upload was followed by a bare shell prompt, and the operator had no way to tell a finished upload from
  # a crashed one. The report about the work must never be able to fail the work.
  UP_DONE=$(grep -oE '[0-9]+ Done' "$AZ_LOG" | tail -1 | cut -d' ' -f1 || true)
  UP_FAILED=$(grep -oE 'Number of File Transfers Failed: [0-9]+' "$AZ_LOG" | grep -oE '[0-9]+$' | tail -1 || true)
  ok "uploaded ${UP_DONE:-?} file(s), ${UP_FAILED:-0} failed"
  rm -f "$AZ_LOG"
fi

step "Preflight: the STAGED artifact must be the one we just uploaded"
# The local artifact was verified above; this asserts the same thing about the copy the JOB will read,
# which is a different set of bytes at a different address. Skipping it is what let a 3.4 GB stage and an
# acr build run to completion before the job announced that the container held two datasets. Counting
# blobs answers it in seconds and needs nothing but the operator's own login.
LOCAL_FILES=$(find "$ARTIFACT_DIR" -type f ! -name '.*' 2>/dev/null | wc -l | tr -d ' ')
STAGED_FILES=$(az storage blob list --account-name "$STORE" -c artifact --prefix "${PROFILE}/" \
  --auth-mode login --query "length(@)" -o tsv 2>/dev/null || echo "?")
fact "staged" "$STAGED_FILES blob(s) under artifact/${PROFILE}/"
fact "local" "$LOCAL_FILES file(s) in $ARTIFACT_DIR"
# An EMPTY prefix is its own failure, and it is the one --start-only can actually cause. The comparison
# below only fires when the two counts differ, so a prefix holding nothing against a machine holding
# nothing agrees at zero and passes. That is fine when the upload just ran; under --start-only it would
# mean starting a job whose whole premise is a dataset that is not there.
if [ "$START_ONLY" = "true" ] && { [ "$STAGED_FILES" = "?" ] || [ "$STAGED_FILES" -eq 0 ] 2>/dev/null; }; then
  die "--start-only was passed, but artifact/${PROFILE}/ holds no blobs. There is nothing staged to load. Re-run without --start-only to upload '$PROFILE' first."
fi
# Compared only when there IS a local copy to compare against. An absent directory counts as zero files,
# which differs from a staged 587 and read as "the staged copy is not this artifact", so the run died
# advising a re-run without SKIP_UPLOAD=1: the wrong fix, for a state that is not wrong. Holding 3.8 GB on
# the laptop is not a precondition for starting a job whose inputs are already in Azure, and under
# --start-only it is the normal case not to have it. The empty-prefix refusal above is what covers the
# state this one used to be relied on for.
if [ -d "$ARTIFACT_DIR" ] && [ "$STAGED_FILES" != "?" ] && [ "$STAGED_FILES" != "$LOCAL_FILES" ]; then
  die "artifact/${PROFILE}/ holds $STAGED_FILES blob(s) but '$ARTIFACT_DIR' holds $LOCAL_FILES file(s). The staged copy is not this artifact, and the job would load whatever is actually there. Re-run without SKIP_UPLOAD=1."
fi

if [ "$START_ONLY" = "true" ]; then
  # REFUSES rather than builds. Falling back to a build here would make --start-only mean "usually fast",
  # and the one run where it silently built would be the run on the connection that could not afford it.
  # The digest resolution immediately below is what proves the tag is really there; this only makes the
  # failure name the flag and the fix.
  step "Load image $ACR/cmms-load:$IMAGE_TAG (--start-only: not building)"
  FOUND=$(image_digest cmms-load)
  case "$FOUND" in
    sha256:*) ok "${IMAGE_TAG} already in $ACR" ;;
    *) die "--start-only was passed, but $ACR holds no cmms-load:$IMAGE_TAG to start. Nothing was built and nothing was deployed.
    A dirty working tree gets a -dirty-<hash> tag that has never been built, so commit first, or re-run without --start-only to build it." ;;
  esac
else
  step "Load image $ACR/cmms-load:$IMAGE_TAG"
  build_if_absent cmms-load tools/Cmms.LoadDataRunner/Dockerfile
fi

# Deploy by DIGEST, not by tag. A tag is a mutable pointer, so "the job runs cmms-load:abc123" is a
# statement about a name rather than about any particular image. Resolving it here means the thing that
# gets deployed is exactly the thing that was just built.
# `[?tags != null]` first, and it is not defensive padding. Untagged manifests accumulate in the registry
# whenever a tag is moved to a newer build, and JMESPath's contains() THROWS on a null rather than skipping
# it, so the unfiltered query failed outright the moment a second image existed:
#   In function contains(), invalid type for value: None, expected one of: ['array', 'string']
# The script then died at the || below with a message about the digest, which named the symptom and hid
# the cause. stderr is shown now so the next failure explains itself.
DIGEST=$(image_digest cmms-load)
case "$DIGEST" in
  sha256:*) ;;
  *) die "cannot resolve the digest for cmms-load:${IMAGE_TAG}. az said: ${DIGEST:-<nothing>}" ;;
esac
IMAGE_REF="${ACR}.azurecr.io/cmms-load@${DIGEST}"
fact "digest" "${DIGEST}"

step "Deploying the load job (infra/load-job.bicep) targeting '$TENANT_SLUG' with the '$PROFILE' dataset, execute=$EXECUTE"
# clearTargetSlug is passed EXPLICITLY EMPTY rather than left off the command. The deploy converges the
# job, and a previous run of the old script may have left LOAD_CLEAR_TARGET_SLUG set to a real slug on
# caj-cmms-load. Omitting the parameter would fall back to the template's '' default and converge it too,
# but only by accident of that default; saying it here means this script states, at the deployment, that
# the job it starts deletes nothing. The assertion a few lines down reads the deployed value back.
# >/dev/null because the three assertions immediately below read the deployed job back, and THEY are the
# report on this step. ARM's JSON says a deployment happened, which was never the question.
az deployment group create -g "$RG" -n "deploy-preprod-load-${IMAGE_TAG}" -f infra/load-job.bicep \
  -p environment=preprod containerImage="$IMAGE_REF" artifactProfile="$PROFILE" \
     tenantSlug="$TENANT_SLUG" loadExecute="$EXECUTE" clearTargetSlug="" timeoutHours="$TIMEOUT_HOURS" \
  >/dev/null || die "load job deployment failed."

step "Preflight: the deployed job must carry the image we just built"
# Asserted rather than assumed. A run that silently used an older image reported "Succeeded" while doing
# nothing, which is the most expensive kind of wrong: it looks like the load worked.
DEPLOYED=$(az containerapp job show -n caj-cmms-load -g "$RG" --query "properties.template.containers[0].image" -o tsv 2>/dev/null)
if [ "$DEPLOYED" != "$IMAGE_REF" ]; then
  die "the job is set to run '$DEPLOYED' but this script built '$IMAGE_REF'. Refusing to start it: a stale image would run code you are not looking at."
fi
ok "job carries the image just built"

# And that it will read the dataset this run is about. The image assertion above proves which code runs,
# not which data it opens, and the profile only reaches the job through this one variable.
DEPLOYED_URL=$(az containerapp job show -n caj-cmms-load -g "$RG" \
  --query "properties.template.containers[0].env[?name=='ARTIFACT_BLOB_URL'].value | [0]" -o tsv 2>/dev/null)
case "$DEPLOYED_URL" in
  */artifact/"$PROFILE") ok "job reads artifact/$PROFILE" ;;
  *) die "the job is set to read '$DEPLOYED_URL' but this run is about the '$PROFILE' dataset. Refusing to start it: it would load whatever is at that address." ;;
esac

# And that it will delete nothing. Removing the flag from this script removes one way to set that variable,
# not the variable: LOAD_CLEAR_TARGET_SLUG lives on the job, and a Bicep deploy, a portal edit, or an
# earlier run of the old script can all leave a slug sitting in it. This script starts the job, so it is
# answerable for what the job is configured to do, and it reads that back rather than trusting the deploy
# it just issued. Asserting the outcome, not the gate.
DEPLOYED_CLEAR=$(az containerapp job show -n caj-cmms-load -g "$RG" \
  --query "properties.template.containers[0].env[?name=='LOAD_CLEAR_TARGET_SLUG'].value | [0]" -o tsv 2>/dev/null)
if [ -n "$DEPLOYED_CLEAR" ]; then
  die "the job is set to CLEAR tenant '$DEPLOYED_CLEAR' before loading (LOAD_CLEAR_TARGET_SLUG). This script does not delete data and will not start a job that does. Clear that variable off caj-cmms-load, then re-run."
fi
ok "job is configured to delete nothing"

step "Starting caj-cmms-load"
# The execution's own name, captured rather than looked up afterwards. The script used to print the
# execution LIST and send the operator back to that same list to watch progress. The list is history,
# newest first, and this job's history holds every failure that led here, so a healthy run was read as a
# wall of Failed rows and the one row that described the current run was the only one that mattered.
EXEC=$(az containerapp job start -n caj-cmms-load -g "$RG" --query name -o tsv 2>/dev/null || true)
[ -n "$EXEC" ] || EXEC=$(az containerapp job execution list -n caj-cmms-load -g "$RG" --query "[0].name" -o tsv 2>/dev/null || echo unknown)
ok "started $EXEC"

if [ "$WATCH" = "true" ]; then
  step "Running"
  # Only THIS execution is polled and only this execution is reported. The execution LIST is history,
  # newest first, and this job's history holds every failure that led here, so a healthy run read as a
  # wall of Failed rows. Ctrl-C stops the WATCHING, never the job: the load is resumable by design.
  START_EPOCH=$(date +%s)
  LAST_STATUS=""
  LAST_TABLE=""

  # STREAMED FROM THE RUNNING REPLICA, not from Log Analytics. The status field alone is the word "Running"
  # for hours, which is how a healthy load came to be killed at 2h24m: the console had a clock and no way to
  # tell work from a hang. The runner emits a 'progress ' line per chunk for exactly this, and it has to be
  # read from the container, because LA runs a minute or two behind (see "Result" below) and a progress line
  # that reports a stale chunk number is worse than none.
  #
  # Re-entered in a loop because the stream is not durable: it ends when the replica is not up yet, and it
  # ends again whenever the connection drops mid-load. Every failure here is swallowed on purpose. This is
  # the DISPLAY, and a load that is running fine must never be reported as broken because a log tail could
  # not attach; when nothing arrives the line falls back to the status, which is what it showed before.
  STREAM=$(mktemp)
  STREAM_STOP="$STREAM.stop"
  ( while [ ! -f "$STREAM_STOP" ]; do
      az containerapp job logs show -n caj-cmms-load -g "$RG" --execution "$EXEC" \
        --container load --follow --tail 1 >>"$STREAM" 2>/dev/null || true
      sleep 2
    done ) &
  STREAM_PID=$!

  while :; do
    STATUS=$(az containerapp job execution show -n caj-cmms-load -g "$RG" \
      --job-execution-name "$EXEC" --query "properties.status" -o tsv 2>/dev/null || echo Unknown)
    NOW=$(date +%s); ELAPSED=$((NOW - START_EPOCH))
    # The newest chunk the runner has reported. Read out of the JSON envelope by taking the run of
    # characters up to the closing quote, which holds because the runner's progress line is table names and
    # digits and carries nothing that JSON would escape.
    PROGRESS=$(grep -ao 'progress [^"]*' "$STREAM" 2>/dev/null | tail -1 || true)
    PROGRESS=${PROGRESS#progress }
    # ONE line, rewritten in place, rather than a new line per heartbeat. A load is measured in hours, and
    # the old per-minute heartbeat printed hundreds of lines whose only content was that nothing had
    # changed. Falls back to a line per state change when stdout is not a terminal, because a carriage
    # return in a log file produces one unreadable smear.
    if [ -t 1 ]; then
      printf '\r  %s%02d:%02d:%02d%s  %s\033[K' "$C_DIM" \
        $((ELAPSED/3600)) $(((ELAPSED%3600)/60)) $((ELAPSED%60)) "$C_RESET" "${PROGRESS:-$STATUS}"
    elif [ "$STATUS" != "$LAST_STATUS" ] || [ "${PROGRESS%% *}" != "$LAST_TABLE" ]; then
      # A redirected run gets a line per TABLE, not per chunk. Per chunk would be 584 lines in a file whose
      # reader wants to know where the run got to, and per state change was the silence this set out to fix.
      printf '  %02d:%02d:%02d  %s\n' $((ELAPSED/3600)) $(((ELAPSED%3600)/60)) $((ELAPSED%60)) \
        "${PROGRESS:-$STATUS}"
    fi
    LAST_STATUS="$STATUS"
    LAST_TABLE="${PROGRESS%% *}"
    case "$STATUS" in
      Succeeded|Failed|Stopped|Degraded) break ;;
    esac
    sleep 5
  done

  # Children first, then the subshell. Killing the subshell alone orphans the `az` process still streaming
  # into a file that is about to be deleted.
  : >"$STREAM_STOP"
  pkill -P "$STREAM_PID" 2>/dev/null || true
  kill "$STREAM_PID" 2>/dev/null || true
  wait "$STREAM_PID" 2>/dev/null || true
  rm -f "$STREAM" "$STREAM_STOP"
  # Written as an if rather than `[ -t 1 ] && printf`, because a bare && statement that fails IS the script's
  # exit status under `set -e`, so the non-terminal path used to end the run right here (#3048).
  if [ -t 1 ]; then printf '\r\033[K'; fi
  ELAPSED=$(( $(date +%s) - START_EPOCH ))
  DURATION=$(printf '%02d:%02d:%02d' $((ELAPSED/3600)) $(((ELAPSED%3600)/60)) $((ELAPSED%60)))

  step "Result"
  # WAITED FOR, not grabbed. The replica is reaped the moment the job ends, so `job logs show` answers
  # "No replicas found" exactly when the output is wanted, and Log Analytics runs a minute or two behind.
  # The old code read whatever had arrived and printed the last 25 rows, which is how a finished run
  # reported a handful of mid-load table lines in no order and left the operator unable to tell whether it
  # was done. This polls for the runner's own terminal line instead.
  WS=$(az containerapp env show -n cae-cmms-preprod -g "$RG" \
    --query "properties.appLogsConfiguration.logAnalyticsConfiguration.customerId" -o tsv 2>/dev/null || true)
  RUNNER_OUT=$(mktemp)
  if [ -n "$WS" ]; then
    for _ in $(seq 1 24); do
      az monitor log-analytics query -w "$WS" --analytics-query \
        "ContainerAppConsoleLogs_CL | where ContainerJobName_s == 'caj-cmms-load' | where TimeGenerated > ago(6h) | order by TimeGenerated asc | project Log_s" \
        --query "[].Log_s" -o tsv 2>/dev/null > "$RUNNER_OUT" || true
      grep -qE 'rows loaded|PLAN ONLY|FAILED while|REFUSING' "$RUNNER_OUT" && break
      sleep 5
    done
  fi

  # Only the lines that say something, deduplicated, in order. Log Analytics returns each line more than
  # once and interleaves the artifact download's chatter with the loader's, which is most of what made the
  # raw tail unreadable.
  SUMMARY=$(grep -hE 'rows loaded|preflight passed|REFUSING|FAILED while|^note:|^healed:|PLAN ONLY' "$RUNNER_OUT" 2>/dev/null \
            | awk '!seen[$0]++' || true)
  if [ -n "$SUMMARY" ]; then
    printf '%s\n' "$SUMMARY" | while IFS= read -r line; do
      case "$line" in
        *REFUSING*|*"FAILED while"*) printf '  %s%s%s\n' "$C_RED"  "$line" "$C_RESET" ;;
        note:*|healed:*)             printf '  %s%s%s\n' "$C_DIM"  "$line" "$C_RESET" ;;
        *"rows loaded"*)             printf '  %s%s%s\n' "$C_BOLD" "$line" "$C_RESET" ;;
        *)                           printf '  %s\n' "$line" ;;
      esac
    done
    echo
  else
    note "the runner's output has not reached Log Analytics yet"
  fi
  rm -f "$RUNNER_OUT"

  if [ "$STATUS" = "Succeeded" ]; then
    if [ "$EXECUTE" = "true" ]; then
      printf '  %s%sLOADED %s in %s%s\n' "$C_BOLD" "$C_GREEN" "$TENANT_SLUG" "$DURATION" "$C_RESET"
    else
      printf '  %s%sPLANNED %s in %s%s\n' "$C_BOLD" "$C_GREEN" "$TENANT_SLUG" "$DURATION" "$C_RESET"
    fi
    fact "execution" "$EXEC"
    if [ "$EXECUTE" != "true" ]; then
      note "a plan: staged and verified the artifact, wrote nothing. Re-run without --plan to load."
    else
      note "resumable: each chunk is one transaction, checkpointed to $STORE/load-checkpoints"
      note "still to do (#2910): rebuild indexes, re-trust constraints, update statistics, and"
      note "re-derive the Compliance Matrix snapshots, which the bulk copy bypasses."
    fi
  else
    # The az incantations print HERE and nowhere else. On a good run they are four lines of noise; on a bad
    # one they are the first thing wanted, and hunting them out of scrollback is its own tax.
    printf '  %s%sENDED AS %s after %s%s\n' "$C_BOLD" "$C_RED" "$STATUS" "$DURATION" "$C_RESET"
    echo
    note "read the full runner output with:"
    note "  WS=\$(az containerapp env show -n cae-cmms-preprod -g $RG \\"
    note "        --query \"properties.appLogsConfiguration.logAnalyticsConfiguration.customerId\" -o tsv)"
    note "  az monitor log-analytics query -w \"\\\$WS\" --analytics-query \\"
    note "    \"ContainerAppConsoleLogs_CL | where ContainerJobName_s == 'caj-cmms-load' \\"
    note "     | where TimeGenerated > ago(1h) | order by TimeGenerated asc | project TimeGenerated, Log_s\" -o table"
    die "$EXEC ended in state '$STATUS'."
  fi
else
  # --no-watch handed the prompt back, so the run is still going and the operator needs the one command
  # that reports on it. Only that one.
  step "Started"
  fact "execution" "$EXEC"
  note "check it with:"
  note "  az containerapp job execution show -n caj-cmms-load -g $RG \\"
  note "    --job-execution-name $EXEC --query \"properties.status\" -o tsv"
fi
