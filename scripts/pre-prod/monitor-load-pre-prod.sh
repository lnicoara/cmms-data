#!/usr/bin/env bash
# Report where the pre-prod load has got to. lnicoara/cmms-data#14.
#
#   scripts/pre-prod/monitor-load-pre-prod.sh              # one report, then exit
#   scripts/pre-prod/monitor-load-pre-prod.sh --follow     # re-report every 60s until the job ends
#   scripts/pre-prod/monitor-load-pre-prod.sh --log        # the runner's recent output, verbatim
#
# READ-ONLY, and that is the whole contract. It starts nothing, deploys nothing, stops nothing and writes
# nothing. Every command it runs is a query. A load now runs for up to 96 hours and is meant to be left
# alone, so the tool you reach for while it is running must not be one that can disturb it, and the way to
# guarantee that is for it to have no code that could.
#
# It exists because the alternative was a remembered `az containerapp job execution list` incantation that
# answers only "Running", which is the least useful true thing to know about a run measured in days. What
# an operator actually wants is how far in, how fast, and how long is left before the deadline kills it.
#
# The progress lines come from the runner's own output through Log Analytics, which lags ingestion by a
# few minutes. That is why the execution's status and elapsed time are read from ARM instead: those are
# current, and a report whose every number was minutes stale would be read as a stalled load.
set -euo pipefail

# Terminal output: colour, phase headers, die(), and the ERR trap that makes a silent exit impossible
# (lnicoara/cmms#3048). Sourced BEFORE the argument loop, because the loop calls die().
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
. "$SCRIPT_DIR/lib/output.sh"

SUBSCRIPTION="${SUBSCRIPTION:-08fd6214-7b3c-4b5e-a59b-320707791129}"
RG="${RG:-rg-cmms-preprod}"
JOB="${JOB:-caj-cmms-load}"
INTERVAL="${INTERVAL:-60}"

FOLLOW=false
SHOW_LOG=false
for arg in "$@"; do
  case "$arg" in
    --follow) FOLLOW=true ;;
    --log) SHOW_LOG=true ;;
    -h|--help) sed -n '2,18p' "$0"; exit 0 ;;
    *) die "Unknown argument: $arg" ;;
  esac
done

command -v az >/dev/null || die "az CLI not found."
az account show >/dev/null 2>&1 || die "not signed in: run 'az login' first."
az account set --subscription "$SUBSCRIPTION" >/dev/null 2>&1 || die "could not select subscription $SUBSCRIPTION."

# Resolved once. The workspace id does not change between polls, and asking for it every 60 seconds would
# turn a --follow into a stream of identical control-plane calls.
WS=$(az containerapp env show -n "cae-cmms-preprod" -g "$RG" \
  --query "properties.appLogsConfiguration.logAnalyticsConfiguration.customerId" -o tsv 2>/dev/null || true)

# The deadline the job is CONFIGURED with, read from the job rather than assumed to be the script's
# default. They disagree whenever somebody deployed with a different TIMEOUT_HOURS, and the number that
# decides when the replica is killed is this one.
TIMEOUT_S=$(az containerapp job show -n "$JOB" -g "$RG" \
  --query "properties.configuration.replicaTimeout" -o tsv 2>/dev/null || echo "")

# Seconds to something a person can read at a glance. A load is reported in days and hours, never in the
# 347,821 seconds that are technically the same answer.
human() {
  local s=${1:-0} d h m
  d=$(( s / 86400 )); h=$(( (s % 86400) / 3600 )); m=$(( (s % 3600) / 60 ))
  if [ "$d" -gt 0 ]; then printf '%dd %dh %dm' "$d" "$h" "$m"
  elif [ "$h" -gt 0 ]; then printf '%dh %dm' "$h" "$m"
  else printf '%dm' "$m"; fi
}

# ISO8601 to epoch. macOS ships BSD date, which does not take -d, so the GNU form is tried second rather
# than first and both failures fall back to an empty string the caller checks. A monitor that died on a
# date format would be reporting nothing at the moment it is most wanted.
epoch_of() {
  local ts=${1:-}
  [ -n "$ts" ] || { echo ""; return 0; }
  ts=${ts%%.*}; ts=${ts//Z/}; ts=${ts%%+*}
  date -j -f "%Y-%m-%dT%H:%M:%S" "$ts" +%s 2>/dev/null \
    || date -d "$ts" +%s 2>/dev/null \
    || echo ""
}

report_once() {
  local name status start
  # The MOST RECENT execution, which is the one being asked about. The list is history, newest first, and
  # this job's history holds every failure that led here.
  # The DICT projection, not the list one, and IFS is set to a tab. `[0].[a,b,c]` with -o tsv prints each
  # value on its own LINE, so a single `read` took the name and left the status and start time empty: the
  # report then said a running load had no status. `{n:...,s:...,t:...}` prints one tab-separated row.
  IFS=$'\t' read -r name status start <<<"$(az containerapp job execution list -n "$JOB" -g "$RG" \
    --query "[0].{n:name,s:properties.status,t:properties.startTime}" -o tsv 2>/dev/null || echo "")"

  [ -n "${name:-}" ] || die "no executions found for $JOB in $RG."

  step "$name"
  fact "status" "$status"
  fact "started" "${start:-unknown}"

  local started now elapsed
  started=$(epoch_of "${start:-}")
  now=$(date +%s)
  if [ -n "$started" ]; then
    elapsed=$(( now - started ))
    fact "elapsed" "$(human "$elapsed")"
    # Only meaningful while it is running: for an execution that already ended, the time left on a deadline
    # it is no longer subject to is a number about nothing.
    if [ "$status" = "Running" ] && [ -n "$TIMEOUT_S" ]; then
      local left=$(( TIMEOUT_S - elapsed ))
      if [ "$left" -gt 0 ]; then
        fact "deadline in" "$(human "$left")"
      else
        warn "past its $(human "$TIMEOUT_S") deadline; Container Apps should have killed it"
      fi
    fi
  fi

  # A deadline kill is reported as 'Failed', which reads as an error and is not one. Said here because the
  # status field alone has sent an operator hunting for a fault in a run that did exactly what it was
  # configured to do.
  if [ "$status" = "Failed" ] && [ -n "$started" ] && [ -n "$TIMEOUT_S" ] \
     && [ "$(( now - started ))" -ge "$TIMEOUT_S" ]; then
    note "ran the full $(human "$TIMEOUT_S") and was killed on the deadline. That is reported as Failed; it is not an error, and the load resumes from its checkpoints."
  fi

  [ -n "$WS" ] || { warn "no Log Analytics workspace on cae-cmms-preprod; progress is unavailable"; return 0; }

  # The runner prefixes its progress lines with 'progress ', which is the handle this picks them out by.
  # Ingestion lags by minutes, so the newest line here is behind the elapsed time above rather than
  # matching it.
  local latest
  latest=$(az monitor log-analytics query -w "$WS" --analytics-query \
    "ContainerAppConsoleLogs_CL | where ContainerJobName_s == '$JOB' | where TimeGenerated > ago(2h) | where Log_s startswith 'progress ' | order by TimeGenerated desc | take 1 | project Log_s" \
    -o tsv 2>/dev/null | head -1 || true)
  if [ -n "$latest" ]; then
    note "${latest#progress }"
  else
    note "no progress line in the last 2h (the runner prints one per chunk; staging and preflight print none)"
  fi
}

if [ "$SHOW_LOG" = "true" ]; then
  [ -n "$WS" ] || die "no Log Analytics workspace on cae-cmms-preprod, so there is no output to read."
  step "Recent runner output"
  az monitor log-analytics query -w "$WS" --analytics-query \
    "ContainerAppConsoleLogs_CL | where ContainerJobName_s == '$JOB' | where TimeGenerated > ago(2h) | order by TimeGenerated asc | project Log_s" \
    -o tsv 2>/dev/null | tail -40
  exit 0
fi

report_once
[ "$FOLLOW" = "true" ] || exit 0

# Polls the STATUS from ARM, which is current, rather than waiting on log lines that arrive minutes late.
# Ends when the execution does, so --follow left in a window does not poll a finished job forever.
while :; do
  sleep "$INTERVAL"
  CUR=$(az containerapp job execution list -n "$JOB" -g "$RG" \
    --query "[0].properties.status" -o tsv 2>/dev/null || echo "")
  report_once
  case "$CUR" in
    Succeeded|Failed|Stopped) exit 0 ;;
  esac
done
