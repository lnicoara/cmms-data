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
# An epoch rendered in the OPERATOR'S timezone, with the zone named.
#
# Azure speaks UTC everywhere: ARM returns 2026-08-11T22:33:26+00:00 and Log Analytics stamps rows the same
# way. An operator reading a load at 5:42pm Central has to subtract five hours in their head to answer "is
# this stalled", and gets it wrong in the direction that looks like a stall. The conversion is the system's
# own tz database rather than a hardcoded offset, so it is right across a DST boundary, which a load
# measured in days can cross.
local_of() {
  local e=${1:-}
  [ -n "$e" ] || { echo ""; return 0; }
  # 12-hour with AM/PM, and the leading zero stripped so it reads like a clock rather than a log format.
  date -r "$e" "+%Y-%m-%d %l:%M:%S %p %Z" 2>/dev/null | tr -s ' ' \
    || date -d "@$e" "+%Y-%m-%d %l:%M:%S %p %Z" 2>/dev/null | tr -s ' ' \
    || echo ""
}

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
#
# -u ON BOTH, and it is the difference between a number and a wrong number. ARM returns UTC; BSD date
# without -u parses the string as LOCAL time, so on a machine in CDT every timestamp came back five hours
# out and a job started ninety seconds ago reported a NEGATIVE elapsed time. Nothing failed, nothing warned,
# and the deadline arithmetic underneath was wrong by the same five hours.
epoch_of() {
  local ts=${1:-}
  [ -n "$ts" ] || { echo ""; return 0; }
  ts=${ts%%.*}; ts=${ts//Z/}; ts=${ts%%+*}
  date -j -u -f "%Y-%m-%dT%H:%M:%S" "$ts" +%s 2>/dev/null \
    || date -u -d "${ts}Z" +%s 2>/dev/null \
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

  local started now elapsed
  started=$(epoch_of "${start:-}")
  now=$(date +%s)
  # Local first, because it is the one being compared against the clock on the wall. UTC is kept beside it
  # rather than dropped: every other tool in this chain reports UTC, and a report that silently switched
  # would make two correct readings look like a disagreement.
  if [ -n "$started" ]; then
    fact "started" "$(local_of "$started")  (${start})"
  else
    fact "started" "${start:-unknown}"
  fi
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
  # Same JSON handling as --log, and for the same reason: -o tsv injects the result-table name as a column,
  # so the "latest progress line" arrived with a PrimaryResult glued to it. The timestamp is printed too,
  # because Log Analytics lags ingestion by minutes and a progress figure with no time on it reads as
  # current when it is not.
  local latest
  latest=$(az monitor log-analytics query -w "$WS" --analytics-query \
    "ContainerAppConsoleLogs_CL | where ContainerJobName_s == '$JOB' | where TimeGenerated > ago(2h) | where Log_s startswith 'progress ' or Log_s contains ' done ' | order by TimeGenerated desc | take 1 | project TimeGenerated, Log_s" \
    -o json 2>/dev/null \
    | python3 -c '
import json, re, sys
from datetime import datetime, timezone

def parse(ts):
    if not ts:
        return None
    ts = re.sub(r"(\.\d{6})\d+", r"\1", ts.strip()).replace("Z", "+00:00")
    try:
        return datetime.fromisoformat(ts)
    except Exception:
        return None

# HOW OLD, not just when. Log Analytics lags ingestion by minutes, so a progress figure printed on its own
# reads as current when it is not, and "is this load stalled" is answered by the gap rather than by the
# number. Said in whole minutes because the ingestion lag makes anything finer a false precision.
def ago(dt):
    secs = int((datetime.now(timezone.utc) - dt).total_seconds())
    if secs < 60:
        return "just now"
    mins = secs // 60
    if mins < 60:
        # Plural computed OUTSIDE the f-string. A quoted conditional inside the expression needs escaped
        # quotes here (this is single-quoted shell), and a backslash in an f-string expression is a syntax
        # error before Python 3.12, which fails the whole monitor rather than one line of it.
        plural = "" if mins == 1 else "s"
        return f"{mins} minute{plural} ago"
    hrs, mins = divmod(mins, 60)
    return f"{hrs}h {mins}m ago"

try:
    rows = json.load(sys.stdin)
except Exception:
    rows = []
if rows:
    r = rows[0]
    line = (r.get("Log_s") or "").strip()
    if line.startswith("progress "):
        line = line[len("progress "):]
    dt = parse(r.get("TimeGenerated"))
    when = dt.astimezone().strftime("%l:%M:%S %p %Z").strip() if dt else ""
    print(when + "  " + line)
    if dt:
        print(ago(dt))
' || true)
  if [ -n "$latest" ]; then
    fact "latest" "$(printf '%s' "$latest" | head -1)"
    local age; age=$(printf '%s' "$latest" | sed -n '2p')
    # Indented under the value rather than given its own label, because it qualifies the line above it.
    [ -n "$age" ] && printf '  %s%-20s %s%s\n' "$C_DIM" "" "$age" "$C_RESET"
  else
    note "no progress line in the last 2h (staging and preflight print none; log ingestion also lags by minutes)"
  fi
}

if [ "$SHOW_LOG" = "true" ]; then
  [ -n "$WS" ] || die "no Log Analytics workspace on cae-cmms-preprod, so there is no output to read."
  step "Recent runner output (times are UTC)"
  # JSON, not -o tsv, and every line carries its timestamp.
  #
  # The first version projected Log_s alone and printed it with -o tsv, which produced output with no time
  # on it and a stray 'PrimaryResult' column: the CLI injects the result-table name as a field, and its
  # position in the row is not something to parse around. Undated log lines are close to useless here,
  # because the question being asked is nearly always about RATE, and a run measured in days is read by
  # comparing two timestamps.
  #
  # Ordered ascending so it reads top to bottom like a transcript, and tailed to the last 60 because a load
  # emits one line per chunk and 584 of them do not belong on a terminal.
  az monitor log-analytics query -w "$WS" --analytics-query \
    "ContainerAppConsoleLogs_CL | where ContainerJobName_s == '$JOB' | where TimeGenerated > ago(${LOG_WINDOW:-2h}) | order by TimeGenerated asc | project TimeGenerated, Log_s" \
    -o json 2>/dev/null \
  | python3 -c '
import json, re, sys
from datetime import datetime

# Log Analytics stamps rows in UTC with SEVEN fractional digits, which fromisoformat rejects on Python
# versions before 3.11, so the fraction is trimmed to six before parsing. astimezone() with no argument
# converts to the machine tz rather than a hardcoded offset, so this stays right across a DST change.
def local(ts):
    if not ts:
        return ""
    ts = re.sub(r"(\.\d{6})\d+", r"\1", ts.strip()).replace("Z", "+00:00")
    try:
        return datetime.fromisoformat(ts).astimezone().strftime("%l:%M:%S %p").strip()
    except Exception:
        return ts[11:19]

try:
    rows = json.load(sys.stdin)
except Exception:
    sys.exit(0)
for r in rows[-60:]:
    line = (r.get("Log_s") or "").rstrip()
    if line:
        # Resolved before the f-string: a backslash in an f-string expression is a syntax error before
        # Python 3.12, and the escaped quotes are unavoidable inside single-quoted shell.
        when = local(r.get("TimeGenerated"))
        print(f"  {when:>11}  {line}")
'
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
