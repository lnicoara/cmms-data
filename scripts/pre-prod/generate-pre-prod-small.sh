#!/usr/bin/env bash
# Generate the 'small' pre-prod dataset (lnicoara/cmms#2993, per #2876 and epic #2731).
#
# The command this replaces was four things an operator had to remember and get right together: a PATH
# that puts the right .NET SDK first, an env var naming the profile, --no-launch-profile, and an absolute
# --OutDir. Every one of them fails quietly rather than loudly. A missing GENERATOR_PARAMS in particular
# runs on GeneratorOptions defaults, which carry the FULL profile's seed, and produces a small artifact
# whose key space is a subset of the full one's. That poisons the artifact against a database permanently.
#
# It opens NO database, reads no Azure config, and needs no credentials. That is by design (#2876) and is
# why generating is separate from loading: this runs on a laptop.
#
# It WRITES the output directory, replacing whatever is there. That is what generating means, and the path
# is printed before it happens.
#
#   scripts/pre-prod/generate-pre-prod-small.sh                    # to the default location, printed below
#   scripts/pre-prod/generate-pre-prod-small.sh --out=/some/dir    # somewhere else
#
# Then load it:
#   scripts/pre-prod/load-pre-prod.sh --artifact-dir=<the path this prints> --execute
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
# The profiles are in profiles/, one copy (lnicoara/cmms#3026). This guard named
# tools/Cmms.LoadDataGenerator/small.json, the path that copy used to sit at, so single-sourcing them
# broke this script on every invocation and the message blamed the repo it was standing in for being the
# wrong repo. A guard that names a path nobody maintains is worse than no guard.
[ -f profiles/small.json ] || die "$ROOT does not look like the cmms-data repo (no profiles/small.json)."

# DERIVED, not demanded. The 8.0.423 SDK under ~/.dotnet has to come before Homebrew's 8.0.128, which
# throws two false CS8602s on this solution. A script that stopped to tell you to fix your PATH would be a
# script that failed at the one thing it could have done for you.
[ -d "$HOME/.dotnet" ] && PATH="$HOME/.dotnet:$PATH"
export PATH
command -v dotnet >/dev/null || die "dotnet not found, and $HOME/.dotnet does not hold an SDK either."

# The dataset's home. Overridable, but it has a default so the common case is no arguments at all.
OUT="${OUT:-$HOME/git/cmms-data/data/small}"
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

step "Generating the 'small' dataset into $OUT"
if [ -d "$OUT" ]; then
  warn "that directory EXISTS and will be replaced"
  PRIOR=$([ -f "$OUT/manifest.json" ] && command -v python3 >/dev/null 2>&1 && python3 -c \
    "import json,sys;d=json.load(open(sys.argv[1]));print('replacing: seed %s, %s rows, generated %s' % (d['seed'], format(d['totalRows'], ','), d.get('generationDate','?')))" \
    "$OUT/manifest.json" 2>/dev/null || true)
  [ -n "$PRIOR" ] && note "$PRIOR"
fi
mkdir -p "$(dirname "$OUT")"

# GENERATOR_PARAMS is what selects the profile, and its absence is the silent failure described in the
# header. Named here so it cannot be forgotten.
GENERATOR_PARAMS=small.json dotnet run --project tools/Cmms.LoadDataGenerator -c Release \
  --no-launch-profile -- --OutDir="$OUT" \
  || die "generation failed."

step "Verifying the artifact against THIS checkout's model"
# The same check load-pre-prod.sh runs before it uploads anything, run here where it is free. An artifact
# generated against a model that has since moved makes every row short, and the job discovers that after a
# multi-gigabyte upload and an image build. --verify-artifact opens no database.
dotnet run --project tools/Cmms.LoadDataRunner -c Release --no-launch-profile -- \
  --verify-artifact --artifact="$OUT" \
  || die "the artifact does not match this checkout's model. That should not happen for a dataset generated seconds ago from the same tree; read the error above before loading it."

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
fact "artifact" "$OUT"
echo
note "load it with:"
note "  scripts/pre-prod/load-pre-prod.sh --artifact-dir=$OUT"
