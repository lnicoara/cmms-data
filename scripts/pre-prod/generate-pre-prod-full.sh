#!/usr/bin/env bash
# Generate the 'full' pre-prod dataset (lnicoara/cmms#3045, per #2993, #2876 and epic #2731).
#
# 4,000,000 work orders and 1,000,000 assets: the scale epic #2731 exists to measure. Roughly 42.7 million
# rows, about 3.7 GB on disk, and around an hour. Those numbers are printed before it starts, because an
# hour of silence is indistinguishable from a hang and the natural response to a hang is Ctrl-C.
#
# It opens NO database, reads no Azure config, and needs no credentials. That is by design (#2876) and is
# why generating is separate from loading: this runs on a laptop.
#
# It WRITES the output directory, replacing whatever is there. That is what generating means, and the path
# is printed, with what is currently sitting at it, before anything is removed.
#
#   scripts/pre-prod/generate-pre-prod-full.sh                    # to the default location, printed below
#   scripts/pre-prod/generate-pre-prod-full.sh --out=/some/dir    # somewhere else
#
# A near-twin of generate-pre-prod-small.sh rather than a shared engine with a --profile flag. Same reason
# scripts/ has a self-contained directory per environment (lnicoara/cmms#838): these two differ in the
# checks that matter (disk, duration, and the seed hazard below is the OPPOSITE way round), and a flag
# would hide that behind an argument nobody reads.
set -euo pipefail

# Terminal output: colour, phase headers, and die(). Sourced BEFORE the argument loop, because the loop
# calls die() and defining it afterwards meant a flag with a missing value died with 'die: command not
# found' instead of its own message. Resolved from THIS script's location, not the working directory, so
# the script works from anywhere. lnicoara/cmms#3046.
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/output.sh"

# Runs from anywhere. The generator resolves its profile against the process base directory AND the working
# directory, so where you stood when you typed this changed which profile was found. Deriving the repo root
# removes the question instead of documenting it.
ROOT=$(git rev-parse --show-toplevel 2>/dev/null) || die "not inside a git checkout of this repo."
cd "$ROOT"
[ -f profiles/full.json ] || die "$ROOT does not look like the cmms-data repo (no profiles/full.json)."

# DERIVED, not demanded. The 8.0.423 SDK under ~/.dotnet has to come before Homebrew's 8.0.128, which
# throws two false CS8602s on this solution. A script that stopped to tell you to fix your PATH would be a
# script that failed at the one thing it could have done for you.
[ -d "$HOME/.dotnet" ] && PATH="$HOME/.dotnet:$PATH"
export PATH
command -v dotnet >/dev/null || die "dotnet not found, and $HOME/.dotnet does not hold an SDK either."

OUT="${OUT:-$HOME/git/cmms-data/data/full}"
for arg in "$@"; do
  case "$arg" in
    --out=*) OUT="${arg#*=}" ;;
    --out) die "--out needs the directory to write: --out=/abs/path" ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) die "Unknown argument: $arg" ;;
  esac
done

# Absolute only, for the same reason load-pre-prod.sh requires it of --artifact-dir: a relative path names
# a different directory from every shell, and the two scripts have to agree about one location.
case "$OUT" in
  /*) ;;
  *) die "--out must be an ABSOLUTE path, and '$OUT' is not one." ;;
esac

# ---- Free disk, BEFORE an hour of work -------------------------------------------------------------
# The artifact is about 3.7 GB and is written incrementally, so running out of space does not fail fast:
# it fails deep into the run, having produced a directory that looks like an artifact and is short some
# chunks. The manifest's totalRows would then disagree with what is on disk, which the loader catches, but
# only after a stage and an upload. Asking df first costs nothing.
NEED_GB=6   # 3.7 GB of output plus headroom for the gzip buffers and the manifest pass
AVAIL_GB=$(df -Pg "$(dirname "$OUT")" 2>/dev/null | awk 'NR==2 {print $4}')
if [ -n "${AVAIL_GB:-}" ] && [ "$AVAIL_GB" -lt "$NEED_GB" ] 2>/dev/null; then
  die "generating 'full' needs about ${NEED_GB} GB free under $(dirname "$OUT") and there is ${AVAIL_GB} GB. Free some space or pass --out= pointing somewhere with room. Refusing now rather than an hour in, with a half-written artifact that looks complete."
fi

step "Generating the 'full' dataset into $OUT"
fact "expected" "about 42.7 million rows, ~3.7 GB, roughly an hour"
fact "free disk" "${AVAIL_GB:-?} GB under $(dirname "$OUT")"
if [ -d "$OUT" ]; then
  warn "that directory EXISTS and will be replaced"
  PRIOR=$([ -f "$OUT/manifest.json" ] && command -v python3 >/dev/null 2>&1 && python3 -c \
    "import json,sys;d=json.load(open(sys.argv[1]));print('replacing: seed %s, %s rows, %s tables, generated %s' % (d['seed'], format(d['totalRows'], ','), len(d.get('generatedTables', [])), d.get('generationDate','?')))" \
    "$OUT/manifest.json" 2>/dev/null || true)
  [ -n "$PRIOR" ] && note "$PRIOR"
fi
mkdir -p "$(dirname "$OUT")"

# GENERATOR_PARAMS is what selects the profile, and losing it is WORSE here than for the small dataset,
# in the opposite direction. GeneratorOptions defaults are Seed = "cmms-loadtest-v1", WorkOrders = 5_000,
# Assets = 2_000 — and cmms-loadtest-v1 is exactly full.json's seed. So a full run that lost its profile
# would emit a 5,000-work-order artifact carrying the FULL seed, self-validate, and report success. Keys
# are a pure function of (seed, kind, ordinal), so its key space is a strict subset of the real full one's,
# which is the permanent-poisoning hazard hard rule 4 exists to prevent.
#
# For 'small' the wrong seed shows up in the manifest and anyone reading it sees the mistake. Here the seed
# is RIGHT and only the counts are wrong, so the manifest looks correct. Named explicitly, and the row
# count is asserted below rather than trusted.
# RUN IT IN THE BACKGROUND AND REPORT WHILE IT WORKS (lnicoara/cmms#3053).
#
# The generator prints a banner, then says nothing at all until it is finished. For 'small' that is
# fifteen seconds and nobody notices. For 'full' it is an hour of blank terminal, which is
# indistinguishable from a hang, and the natural response to a hang is Ctrl-C on a run that was fine.
#
# The progress is derived from the OUTPUT DIRECTORY rather than from anything the generator says, which
# means it reports work that actually landed on disk instead of a claim about work. It also needs no change
# to the generator, so this improves a run you can start today.
GEN_LOG=$(mktemp)
GENERATOR_PARAMS=full.json dotnet run --project tools/Cmms.LoadDataGenerator -c Release \
  --no-launch-profile -- --OutDir="$OUT" >"$GEN_LOG" 2>&1 &
GEN_PID=$!

GEN_START=$(date +%s)
LAST_BYTES=0
while kill -0 "$GEN_PID" 2>/dev/null; do
  sleep 10
  kill -0 "$GEN_PID" 2>/dev/null || break

  now=$(date +%s); elapsed=$((now - GEN_START))
  # || true throughout: this is the REPORT on the work, and a report must never be able to fail the work
  # (lnicoara/cmms#3047). du on a directory mid-write, or find racing a file being renamed, are both
  # ordinary and neither is a reason to kill an hour-long generation.
  bytes=$(du -sk "$OUT" 2>/dev/null | awk '{print $1}' || true); bytes=${bytes:-0}
  files=$(find "$OUT" -type f -name '*.jsonl.gz' 2>/dev/null | wc -l | tr -d ' ' || true); files=${files:-0}
  tables=$(find "$OUT" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | wc -l | tr -d ' ' || true); tables=${tables:-0}
  # Which table it is on now, taken from the most recently written chunk. More useful than a percentage
  # this cannot honestly compute: table sizes differ by four orders of magnitude, so "61 of 158 files" says
  # very little about how much work remains, while "now writing WorkOrders" says a great deal.
  newest=$(ls -t "$OUT"/*/*.jsonl.gz 2>/dev/null | head -1 || true)
  newest_table=$([ -n "$newest" ] && basename "$(dirname "$newest")" || echo "starting")

  rate=$(( (bytes - LAST_BYTES) / 10 )); LAST_BYTES=$bytes
  human=$(awk -v k="$bytes" 'BEGIN{ if (k>1048576) printf "%.1f GB", k/1048576; else if (k>1024) printf "%.0f MB", k/1024; else printf "%d KB", k }')

  # One line rewritten in place on a terminal; one line per tick when piped, because a carriage return in
  # a log file is an unreadable smear.
  if [ -t 1 ]; then
    printf '\r  %s%02d:%02d:%02d%s  %-10s %-5s files  %-22s %s KB/s   ' \
      "$C_DIM" $((elapsed/3600)) $(((elapsed%3600)/60)) $((elapsed%60)) "$C_RESET" \
      "$human" "$files" "$newest_table" "$rate"
  else
    printf '  %02d:%02d:%02d  %s across %s files in %s tables, writing %s\n' \
      $((elapsed/3600)) $(((elapsed%3600)/60)) $((elapsed%60)) "$human" "$files" "$tables" "$newest_table"
  fi
done
[ -t 1 ] && printf '\r%*s\r' 78 ''

# wait returns the generator's status. Handled with || so errexit does not fire before the log is shown:
# an hour of work failing must print WHY, not just stop (lnicoara/cmms#3048).
if ! wait "$GEN_PID"; then
  tail -30 "$GEN_LOG" >&2
  rm -f "$GEN_LOG"
  die "generation failed (output above)."
fi
# The generator's own summary: per-table row counts and the self-validation report. Printed now rather
# than streamed, so it is not interleaved with the progress line above.
cat "$GEN_LOG"
rm -f "$GEN_LOG"

# ---- The artifact must be the FULL one, not the defaults wearing its seed ---------------------------
# Asserting the outcome, not the gate. Passing GENERATOR_PARAMS is the gate; the row count is the outcome,
# and it is the one number that distinguishes a real full run from the failure described above.
step "Checking this is the full dataset and not a default run"
ROWS=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['totalRows'])" "$OUT/manifest.json" 2>/dev/null || echo 0)
WOS=$(python3 -c "import json;print(json.load(open('profiles/full.json'))['WorkOrders'])" 2>/dev/null || echo 0)
if [ "$ROWS" -lt "$WOS" ] 2>/dev/null; then
  die "the artifact holds $ROWS rows, fewer than the ${WOS} work orders full.json asks for, so this is not the full dataset. The most likely cause is that GENERATOR_PARAMS did not resolve and the run used GeneratorOptions defaults, which carry full.json's OWN seed and would therefore look correct in the manifest. Delete $OUT and re-run."
fi
ok "$(printf "%'d" "$ROWS" 2>/dev/null || echo "$ROWS") rows, which is the full dataset"

step "Verifying the artifact against the pinned model"
# The same check load-pre-prod.sh runs before it uploads anything, run here where it is free. An artifact
# generated against a model that has since moved makes every row short, and the job discovers that after a
# multi-gigabyte upload and an image build. --verify-artifact opens no database.
#
# It opens every chunk, and 'full' has hundreds, so this is not instant the way it is for 'small'.
dotnet run --project tools/Cmms.LoadDataRunner -c Release --no-launch-profile -- \
  --verify-artifact --artifact="$OUT" \
  || die "the artifact does not match the pinned model (see above). That should not happen for a dataset generated minutes ago from this checkout; read the error before loading it."

step "Done"
if command -v python3 >/dev/null 2>&1; then
  SUMMARY=$(python3 -c "import json,sys;d=json.load(open(sys.argv[1]));print('%s|%s|%s' % (d['seed'], format(d['totalRows'], ','), len(d.get('generatedTables', []))))" \
    "$OUT/manifest.json" 2>/dev/null || true)
  if [ -n "$SUMMARY" ]; then
    fact "seed" "${SUMMARY%%|*}"
    fact "rows" "$(printf '%s' "$SUMMARY" | cut -d'|' -f2)"
    fact "tables" "${SUMMARY##*|}"
  fi
fi
SIZE=$(du -sh "$OUT" 2>/dev/null | awk '{print $1}')
[ -n "$SIZE" ] && fact "on disk" "$SIZE"
fact "artifact" "$OUT"
echo
warn "loading this is separately blocked (lnicoara/cmms#2970)"
note "the bulk copy runs at roughly nine minutes per 100,000-row chunk, so the full"
note "artifact needs about 51 hours against a 12-hour job timeout. Generate it now;"
note "load it once #2910 has sorted out the index posture."
