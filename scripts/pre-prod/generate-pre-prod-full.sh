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

die() { echo "ERROR: $*" >&2; exit 1; }
step() { echo; echo "==> $*"; }

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
echo "    about 42.7 million rows, ~3.7 GB, and roughly an hour"
echo "    ${AVAIL_GB:-?} GB free under $(dirname "$OUT")"
if [ -d "$OUT" ]; then
  echo "    that directory EXISTS and will be replaced"
  [ -f "$OUT/manifest.json" ] && command -v python3 >/dev/null 2>&1 && python3 -c \
    "import json,sys;d=json.load(open(sys.argv[1]));print('    replacing: seed %s, %s rows, %s tables, generated %s' % (d['seed'], format(d['totalRows'], ','), len(d.get('generatedTables', [])), d.get('generationDate','?')))" \
    "$OUT/manifest.json" 2>/dev/null || true
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
GENERATOR_PARAMS=full.json dotnet run --project tools/Cmms.LoadDataGenerator -c Release \
  --no-launch-profile -- --OutDir="$OUT" \
  || die "generation failed."

# ---- The artifact must be the FULL one, not the defaults wearing its seed ---------------------------
# Asserting the outcome, not the gate. Passing GENERATOR_PARAMS is the gate; the row count is the outcome,
# and it is the one number that distinguishes a real full run from the failure described above.
step "Checking this is the full dataset and not a default run"
ROWS=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['totalRows'])" "$OUT/manifest.json" 2>/dev/null || echo 0)
WOS=$(python3 -c "import json;print(json.load(open('profiles/full.json'))['WorkOrders'])" 2>/dev/null || echo 0)
if [ "$ROWS" -lt "$WOS" ] 2>/dev/null; then
  die "the artifact holds $ROWS rows, fewer than the ${WOS} work orders full.json asks for, so this is not the full dataset. The most likely cause is that GENERATOR_PARAMS did not resolve and the run used GeneratorOptions defaults, which carry full.json's OWN seed and would therefore look correct in the manifest. Delete $OUT and re-run."
fi
echo "    $(printf "%'d" "$ROWS" 2>/dev/null || echo "$ROWS") rows"

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
  python3 -c "import json,sys;d=json.load(open(sys.argv[1]));print('    seed %s, %s rows across %s tables' % (d['seed'], format(d['totalRows'], ','), len(d.get('generatedTables', []))))" \
    "$OUT/manifest.json" 2>/dev/null || true
fi
du -sh "$OUT" 2>/dev/null | awk '{print "    " $1 " on disk"}'
echo "    $OUT"
echo
echo "Loading this is separately blocked (lnicoara/cmms#2970): the bulk copy runs at roughly nine minutes"
echo "per 100,000-row chunk, so the full artifact needs about 51 hours against a 12-hour job timeout."
echo "Generate it now; load it once #2910 has sorted out the index posture."
