#!/usr/bin/env bash
# Report how many rows every table in a pre-prod tenant holds.
#
#   scripts/pre-prod/count-records.sh preprod
#   scripts/pre-prod/count-records.sh preprod --tenant=demo-health
#   scripts/pre-prod/count-records.sh preprod --csv > census.csv
#
# WHY THIS IS A JOB AND NOT A QUERY. The obvious implementation is sqlcmd from your laptop. It cannot work:
# pre-prod SQL sets publicNetworkAccess=Disabled, so every connection from outside the VNet is refused at
# login regardless of firewall rules, and the server carries firewall rules that look like they should
# help and do not.
#   mssql: login error: Connection was denied because Deny Public Network Access is set to Yes.
# So the count runs in a container inside the VNet, which is caj-cmms-count, and this script deploys it,
# starts it, reads its output back, and formats the table.
#
# IT RUNS ALONGSIDE A LOAD, on purpose. caj-cmms-count is a SEPARATE job from caj-cmms-load rather than
# another mode of it, so starting a census never creates a second execution of the job doing the loading.
# That mattered enough to cost a day: on 2026-08-10 two overlapping load executions spent 56 minutes
# blocking each other into command timeouts. A load can now run for up to 96 hours, so "what is in there
# right now" is a question asked mostly WHILE one is in flight.
#
# IT WRITES NOTHING. No --execute, no clear slug, no artifact. The job is not even configured with the
# variables that would let the runner write.
#
# THE NUMBERS ARE READ FROM ENGINE METADATA (sys.partitions), not COUNT(*). On an idle database that is
# exact. While a load is running a table can lag the truth by a chunk, which is the right trade: a real
# COUNT(*) over 34 million AuditEvents rows would scan the largest table in the database, under a fixed
# timeout, while something else is writing to it.
#
# ENV IS PREPROD ONLY, and the argument is required rather than defaulted so the environment is always
# stated out loud. dev and staging have publicNetworkAccess=Enabled and can be queried directly with no
# job at all; prod is deliberately not wired up here, because a tool that reaches prod should be built
# when someone means to reach prod rather than inherited by a script that was aimed somewhere else.
set -euo pipefail

# Terminal output: colour, phase headers, die(), and the ERR trap that makes a silent exit impossible
# (lnicoara/cmms#3048). Sourced BEFORE the argument loop, because the loop calls die().
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
. "$SCRIPT_DIR/lib/output.sh"

SUBSCRIPTION="${SUBSCRIPTION:-08fd6214-7b3c-4b5e-a59b-320707791129}"
RG="${RG:-rg-cmms-preprod}"
TENANT_SLUG="${TENANT_SLUG:-demo-health}"

ENV_NAME=""
CSV=false
for arg in "$@"; do
  case "$arg" in
    --tenant=*) TENANT_SLUG="${arg#--tenant=}" ;;
    --csv) CSV=true ;;
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    -*) die "Unknown argument: $arg" ;;
    *)
      [ -z "$ENV_NAME" ] || die "Give one environment, not '$ENV_NAME' and '$arg'."
      ENV_NAME="$arg"
      ;;
  esac
done

[ -n "$ENV_NAME" ] || die "Which environment? Usage: count-records.sh <env>   (only 'preprod' is wired up)"

# Named individually rather than lumped into one "unsupported", because the reason differs per environment
# and the right next step differs with it.
case "$ENV_NAME" in
  preprod) ;;
  dev|staging)
    die "'$ENV_NAME' has publicNetworkAccess=Enabled, so it needs no job and this script would be the slow
       way to ask. Query it directly:
         az sql db list -g rg-cmms-$ENV_NAME -s \$(az sql server list -g rg-cmms-$ENV_NAME --query '[0].name' -o tsv) -o table" ;;
  prod)
    die "'prod' is not wired up. The job IaC supports it, but pointing a tool at production is a decision
       someone should make deliberately rather than inherit from a script aimed at pre-prod." ;;
  *) die "Unknown environment '$ENV_NAME'. Expected one of: dev, staging, preprod, prod." ;;
esac

ROOT=$(git rev-parse --show-toplevel 2>/dev/null) || die "not inside a git checkout of this repo."
cd "$ROOT"
[ -f infra/count-job.bicep ] || die "$ROOT does not look like the cmms-data repo (no infra/count-job.bicep)."
command -v az >/dev/null || die "az CLI not found."
az account show >/dev/null 2>&1 || die "not signed in: run 'az login' first."

step "Selecting the pre-prod subscription ($SUBSCRIPTION)"
az account set --subscription "$SUBSCRIPTION" || die "could not select subscription $SUBSCRIPTION."
ok "subscription selected"

step "Discovering pre-prod resources in $RG"
ACR=$(az acr list -g "$RG" --query "[0].name" -o tsv) || die "could not list container registries in $RG."
[ -n "$ACR" ] || die "no container registry found in $RG."
fact "registry" "$ACR"
fact "tenant" "$TENANT_SLUG"

# THE IMAGE THE LOAD JOB IS ALREADY DEPLOYED WITH, read back rather than built.
#
# This script does not build. A census is a read, and rebuilding a container to perform one would mean an
# `az acr build` that uploads the working tree (3.8 GB of data/, since there is no .dockerignore) every
# time somebody wants a row count. Reusing the deployed digest also means the census runs the same binary
# as the load it is reporting on, which is the useful property: if the two disagree about a table, it is
# the database that changed and not the code.
#
# The cost, stated plainly: a count-only mode added to the runner does not reach this job until a load
# deploy has carried the new image. Until then the digest below is an older build and the job fails on an
# unknown --count-only flag, which says so rather than reporting a wrong number.
step "Resolving the runner image"
IMAGE=$(az containerapp job show -n caj-cmms-load -g "$RG" \
  --query "properties.template.containers[0].image" -o tsv 2>/dev/null) \
  || die "could not read caj-cmms-load's image. Deploy the load job at least once first."
case "$IMAGE" in
  *cmms-load@sha256:*) ;;
  *) die "caj-cmms-load carries '$IMAGE', which is not a cmms-load digest. Refusing to run a census on it." ;;
esac
fact "image" "${IMAGE##*/}"

step "Deploying the census job (infra/count-job.bicep)"
az deployment group create -g "$RG" -n "deploy-preprod-count-$(date +%Y%m%d%H%M%S)" \
  -f infra/count-job.bicep \
  -p environment=preprod containerImage="$IMAGE" tenantSlug="$TENANT_SLUG" \
  >/dev/null || die "census job deployment failed."
ok "caj-cmms-count deployed"

# Asserted rather than assumed, for the same reason the load script asserts its image: a job left pointing
# at the Bicep default (the mcr quickstart sample) would run, exit 0, and print nothing that looked wrong.
DEPLOYED_TENANT=$(az containerapp job show -n caj-cmms-count -g "$RG" \
  --query "properties.template.containers[0].env[?name=='LOAD_TENANT_SLUG'].value | [0]" -o tsv 2>/dev/null)
[ "$DEPLOYED_TENANT" = "$TENANT_SLUG" ] \
  || die "the census job is set to count '$DEPLOYED_TENANT' but this run is about '$TENANT_SLUG'."
ok "job is pointed at $TENANT_SLUG"

step "Counting"
EXEC=$(az containerapp job start -n caj-cmms-count -g "$RG" --query name -o tsv 2>/dev/null || true)
[ -n "$EXEC" ] || die "could not start caj-cmms-count."
fact "execution" "$EXEC"

# Polled rather than slept on. The census is one metadata query, so this normally ends within a few
# seconds of the container starting; the ceiling exists so a job that never schedules fails with a sentence
# instead of hanging.
DEADLINE=$(( $(date +%s) + 420 ))
STATUS=""
while :; do
  STATUS=$(az containerapp job execution show -n caj-cmms-count -g "$RG" --job-execution-name "$EXEC" \
    --query "properties.status" -o tsv 2>/dev/null || echo "")
  case "$STATUS" in
    Succeeded|Failed|Stopped) break ;;
  esac
  [ "$(date +%s)" -lt "$DEADLINE" ] || die "$EXEC has not finished after 7 minutes (last status: ${STATUS:-unknown})."
  sleep 3
done
[ "$STATUS" = "Succeeded" ] || die "$EXEC ended in state '$STATUS'. Read it with:
       az containerapp job logs show -n caj-cmms-count -g $RG --container count --execution $EXEC --format text"

# Read from the job's own log stream rather than from Log Analytics. The workspace lags ingestion by
# minutes, which for a load measured in days is irrelevant and for a query measured in seconds is the whole
# runtime. This is the same reason load-pre-prod.sh polls Log Analytics and this does not: they are waiting
# for different things.
RAW=$(mktemp)
az containerapp job logs show -n caj-cmms-count -g "$RG" --container count \
  --execution "$EXEC" --format text --tail 300 >"$RAW" 2>/dev/null \
  || die "could not read the census output for $EXEC."

# The container's contract is 'COUNT<TAB>schema<TAB>table<TAB>rows'. Parsed on that prefix rather than by
# position, because the log stream also carries the entrypoint's banner and may deliver lines out of order.
ROWS=$(mktemp)
grep -a $'^COUNT\t' "$RAW" | cut -f2- | sort -t$'\t' -k2,2 >"$ROWS" || true
[ -s "$ROWS" ] || die "the census produced no COUNT lines. The image may predate --count-only. Full output:
$(cat "$RAW")"

DB=$(grep -a $'^CENSUS-DATABASE\t' "$RAW" | head -1 | cut -f2)

if [ "$CSV" = "true" ]; then
  echo "schema,table,rows"
  awk -F'\t' '{print $1 "," $2 "," $3}' "$ROWS"
  rm -f "$RAW" "$ROWS"
  exit 0
fi

# Formatted here rather than in the container, because the log transport does not preserve column
# alignment: each line arrives as its own record and padding done upstream would be laid out against a
# width nobody can see. A prefix and a delimiter survive the trip; a formatted table does not.
TABLE_W=$(awk -F'\t' '{ if (length($2) > m) m = length($2) } END { print (m < 5 ? 5 : m) }' "$ROWS")
COUNT_W=$(awk -F'\t' '{ n = sprintf("%'"'"'d", $3); if (length(n) > m) m = length(n) } END { print (m < 5 ? 5 : m) }' "$ROWS")

printf '\n%s%s  %-*s  %*s%s\n' "$C_BOLD" "$C_BLUE" "$TABLE_W" "TABLE" "$COUNT_W" "ROWS" "$C_RESET"
printf '  %s%s  %s%s\n' "$C_DIM" \
  "$(printf '%*s' "$TABLE_W" '' | tr ' ' '-')" \
  "$(printf '%*s' "$COUNT_W" '' | tr ' ' '-')" "$C_RESET"

# Empty tables dimmed rather than hidden. "Which tables are still empty" is most of what a census during a
# load is for, so dropping the zeros would remove the answer; printing them identically to the rest buries
# the numbers that moved.
while IFS=$'\t' read -r schema table rows; do
  pretty=$(printf "%'d" "$rows")
  if [ "$rows" -eq 0 ]; then
    printf '  %s%-*s  %*s%s\n' "$C_DIM" "$TABLE_W" "$table" "$COUNT_W" "$pretty" "$C_RESET"
  else
    printf '  %-*s  %*s\n' "$TABLE_W" "$table" "$COUNT_W" "$pretty"
  fi
done <"$ROWS"

TOTAL_TABLES=$(wc -l <"$ROWS" | tr -d ' ')
TOTAL_ROWS=$(awk -F'\t' '{ s += $3 } END { printf "%d", s }' "$ROWS")
NONEMPTY=$(awk -F'\t' '$3 > 0' "$ROWS" | wc -l | tr -d ' ')

printf '  %s%s  %s%s\n' "$C_DIM" \
  "$(printf '%*s' "$TABLE_W" '' | tr ' ' '-')" \
  "$(printf '%*s' "$COUNT_W" '' | tr ' ' '-')" "$C_RESET"
printf '  %s%-*s%s  %*s\n\n' "$C_BOLD" "$TABLE_W" "$TOTAL_TABLES tables" "$C_RESET" \
  "$COUNT_W" "$(printf "%'d" "$TOTAL_ROWS")"

fact "database" "${DB:-unknown}"
fact "non-empty" "$NONEMPTY of $TOTAL_TABLES"
note "counts come from engine metadata, so a table being written to right now may lag by a chunk"

rm -f "$RAW" "$ROWS"
