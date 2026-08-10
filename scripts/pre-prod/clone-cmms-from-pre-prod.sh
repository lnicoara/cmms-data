#!/usr/bin/env bash
# Put a cmms checkout at PRE-PROD'S commit inside this repo (lnicoara/cmms#3050).
#
# The load tooling needs the product's EF model compiled from one specific commit: the one pre-prod's
# schema is actually at. It used to pack that out of the operator's own ~/git/cmms and refuse unless that
# checkout happened to be sitting on the right commit, which is the tooling telling the operator to move
# their working tree so a load test can build. Backwards, and none of its business.
#
# This gets its own copy instead. A real CLONE, not a worktree: a worktree's .git is a file pointing back
# at the repository it came from, so it stays tethered to ~/git/cmms forever and breaks if that moves or
# goes away. A clone is self-contained. Once .cmms/<commit>/ exists, this repo needs nothing from anywhere
# to build for that pin.
#
# Idempotent. Run it as often as you like; it does network work only when the answer changes, which is when
# pre-prod has been promoted to a commit nothing here has seen before.
#
#   scripts/pre-prod/clone-cmms-from-pre-prod.sh            # ensure the clone exists, print where
#   scripts/pre-prod/clone-cmms-from-pre-prod.sh --print    # print the path only, clone if needed
#
# One clone per pinned commit, accumulating as pre-prod is promoted. That is the intended trade; disk is
# not the constraint here and a stale clone is never wrong, it is simply for a commit nobody targets now.
set -euo pipefail

# Terminal output: colour, phase headers, die(), and the ERR trap that makes a silent exit impossible
# (lnicoara/cmms#3048). Sourced BEFORE the argument loop, because the loop calls die(). Resolved from THIS
# script's location, not the working directory, so it works from anywhere.
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/output.sh"

ROOT=$(git rev-parse --show-toplevel 2>/dev/null) || die "not inside a git checkout of this repo."
cd "$ROOT"
[ -f profiles/full.json ] || die "$ROOT does not look like the cmms-data repo (no profiles/full.json)."

PRINT_ONLY=false
for arg in "$@"; do
  case "$arg" in
    --print) PRINT_ONLY=true ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) die "Unknown argument: $arg" ;;
  esac
done

# Where clones live, one directory per commit. Named by commit rather than "current" or "cmms", so
# "is this the right source?" is answered by the directory existing rather than by a comparison that can
# be stale. Two pins coexist without either being wrong.
CLONE_ROOT="$ROOT/.cmms"
# Cloned over HTTPS. SSH to GitHub times out from this machine and fetch then fails in a way that looks
# like an empty repository rather than a network problem.
CMMS_URL="${CMMS_URL:-https://github.com/lnicoara/cmms.git}"

SUBSCRIPTION="${SUBSCRIPTION:-08fd6214-7b3c-4b5e-a59b-320707791129}"
RG="${RG:-rg-cmms-preprod}"

# ---- 1. Which commit is pre-prod's SCHEMA at ---------------------------------------------------------
# The SCHEMA, not the application, and they are routinely different. A bulk copy has to satisfy the
# database's columns; the application commit only decides what can be read afterwards. TargetBuild.cs
# resolves it exactly this way for the generator, and this is the loader's half of the same question.
#
# Read by DIGEST rather than by tag. A tag is a mutable pointer, so "the job runs cmms-migrate:abc123" is a
# statement about a name; the digest identifies the image, and its tag is then the commit that built it.
$PRINT_ONLY || step "Resolving the commit pre-prod's schema is at"

command -v az >/dev/null || die "az CLI not found, and pre-prod is the only thing that can say which commit to clone."
az account show >/dev/null 2>&1 || die "not signed in to Azure: run 'az login'. The commit to clone is a fact about pre-prod, so it cannot be resolved offline."
az account set --subscription "$SUBSCRIPTION" >/dev/null 2>&1 \
  || die "cannot select subscription '$SUBSCRIPTION'."

# An explicit override, for when Azure cannot be reached at all and you know the commit. Deliberately not
# a default and never a fallback: an unreachable pre-prod must not quietly become "use main", which is how
# a dataset built from the wrong model ends up pointed at pre-prod.
if [ -n "${CMMS_COMMIT:-}" ]; then
  $PRINT_ONLY || warn "CMMS_COMMIT=$CMMS_COMMIT was supplied; pre-prod is NOT being consulted"
  COMMIT="$CMMS_COMMIT"
else

# az, with its stderr KEPT. lnicoara/cmms#3056.
#
# These were `2>/dev/null || true`, which threw away the real error and the exit code, so a transient
# failure arrived here as an empty string and was then reported as "the image resolves to no tag". The tag
# existed the whole time. An operator sent to investigate a registry problem that does not exist is worse
# off than one told nothing, because the message sounds authoritative.
#
# Retried, because `az acr manifest list-metadata` is a preview command and this is a read: a throttle or a
# token refresh is an ordinary event and not a reason to stop a load.
# The error goes to a FILE at a path fixed before the call, not to a variable. az_read is always invoked
# inside a command substitution, which is a subshell, so anything it assigns is discarded the moment it
# returns: the first version set AZ_LAST_ERROR faithfully and the die printed "az said: <nothing>", which
# is the same uninformative failure this whole change exists to remove.
AZ_ERR=$(mktemp)
trap 'rm -f "$AZ_ERR"' EXIT
az_read() {
  local out rc attempt=1
  while :; do
    out=$(az "$@" -o tsv 2>"$AZ_ERR"); rc=$?
    # Preview-command noise on stderr is not failure; only the exit code is.
    if [ $rc -eq 0 ]; then printf '%s' "$out"; return 0; fi
    [ $attempt -ge 3 ] && return 1
    attempt=$((attempt + 1)); sleep 3
  done
}
az_error() { tail -3 "$AZ_ERR" 2>/dev/null | sed 's/^/    /' || true; }
MIGRATE_IMAGE=$(az_read containerapp job show -n caj-cmms-migrate -g "$RG" \
  --query "properties.template.containers[0].image") \
  || die "could not read the caj-cmms-migrate image in $RG after 3 attempts, so which commit pre-prod's schema is at is unknown. az said:
$(az_error)
  This never falls back to main or to whatever is checked out. If Azure is simply unreachable and you know the commit, pass CMMS_COMMIT=<sha>."
[ -n "$MIGRATE_IMAGE" ] \
  || die "caj-cmms-migrate exists in $RG but reports no image, which should not happen. Check the job in the portal."

REGISTRY="${MIGRATE_IMAGE%%.*}"
case "$MIGRATE_IMAGE" in
  *@*) DIGEST="${MIGRATE_IMAGE##*@}"
       # The failure and the empty answer are now DIFFERENT outcomes with different messages, because they
       # send the reader to different places: one is Azure being briefly unavailable, the other is an
       # untagged manifest, and reporting the first as the second wasted a real debugging session.
       COMMIT=$(az_read acr manifest list-metadata --registry "$REGISTRY" --name cmms-migrate \
         --query "[?digest=='$DIGEST'].tags | [0] | [0]") \
         || die "could not ask $REGISTRY which commit built the image pre-prod is running, after 3 attempts. az said:
$(az_error)
  The image itself is fine; this is the lookup failing. Retry, or pass CMMS_COMMIT=<sha> if you know it."
       [ -n "$COMMIT" ] \
         || die "the manifest $DIGEST really does carry no tag in $REGISTRY, so nothing records which commit built it. An image is left untagged when a later build takes its tag; the commit cannot be recovered from the registry. Redeploy caj-cmms-migrate from a tagged build, or pass CMMS_COMMIT=<sha>." ;;
  *)   COMMIT="${MIGRATE_IMAGE##*:}" ;;
esac
fi

# A '-dirty-<hash>' suffix means that image was built from a tree with uncommitted files, so the tag names
# a real commit plus a delta nothing recorded. The commit is the strongest thing still available and it is
# what can be cloned; the delta is gone and no amount of care recovers it. Said out loud rather than
# silently trimmed, because it is a real gap in what this clone can prove.
case "$COMMIT" in
  *-dirty-*) BARE="${COMMIT%%-dirty-*}"
             $PRINT_ONLY || warn "pre-prod's schema image is tagged '$COMMIT', built from $BARE plus uncommitted files"
             $PRINT_ONLY || note "cloning $BARE; a model change that was uncommitted when that image was built cannot be recovered"
             COMMIT="$BARE" ;;
esac

CLONE="$CLONE_ROOT/$COMMIT"
$PRINT_ONLY || fact "pre-prod schema" "$COMMIT"
$PRINT_ONLY || fact "clone" "$CLONE"

# ---- 2. Already have it? -----------------------------------------------------------------------------
# Verified by asking git what the checkout is AT, not by the directory existing. An interrupted clone
# leaves a directory that looks entirely convincing and builds nothing, and the failure surfaces later as
# a restore error naming a package rather than a half-finished clone.
if [ -d "$CLONE/.git" ] && [ "$(git -C "$CLONE" rev-parse --short HEAD 2>/dev/null || true)" = "$COMMIT" ]; then
  if $PRINT_ONLY; then printf '%s\n' "$CLONE"; exit 0; fi
  ok "cmms $COMMIT is already cloned; nothing to do"
  exit 0
fi

# ---- 3. Clone it ------------------------------------------------------------------------------------
if [ -e "$CLONE" ]; then
  $PRINT_ONLY || warn "$CLONE exists but is not a complete checkout of $COMMIT; replacing it"
  rm -rf "$CLONE"
fi

$PRINT_ONLY || step "Cloning cmms at $COMMIT"
mkdir -p "$CLONE_ROOT"

# Cloned to a TEMPORARY path and moved into place only once the checkout is confirmed. A clone interrupted
# halfway would otherwise leave .cmms/<commit>/ present and incomplete, and step 2 above would then have to
# be clever about it forever. An atomic rename means the directory either is a good checkout or is absent.
TMP="$CLONE_ROOT/.incoming-$COMMIT.$$"
rm -rf "$TMP"
LOG=$(mktemp)
# The whole history, not --depth 1. A shallow clone cannot check out an arbitrary older commit, and an
# older commit is precisely what this exists to fetch: pre-prod runs behind main by design.
# Tags come with it, and that is load-bearing rather than incidental: a commit orphaned by a squash-merge
# is reachable ONLY through a tag someone pushed to rescue it. `git clone` fetches tags by default; it is
# named here so nobody "optimises" it away with --single-branch, which would not.
if ! git clone --quiet --tags "$CMMS_URL" "$TMP" >"$LOG" 2>&1; then
  tail -20 "$LOG" >&2; rm -rf "$TMP" "$LOG"
  die "cloning $CMMS_URL failed (output above)."
fi
# Resolved to an object BEFORE checkout, so a commit that is not present fails with a message about the
# commit rather than git's "--detach does not take a path argument", which is what it says when it decides
# the argument must be a filename because it is not a ref it knows.
#
# A squash-merge plus a deleted branch makes the branch's own commits unreachable, and the remote collects
# them. Pre-prod's schema image is tagged with exactly such a commit, so this is not a hypothetical: the
# tag named a commit that no longer existed upstream and survived only in one operator's local repo. The
# recovery is to push it back as a tag, and the message says so, because "not in it" without a reason
# sends the reader looking for a network problem.
if ! git -C "$TMP" rev-parse --verify --quiet "${COMMIT}^{commit}" >/dev/null 2>&1; then
  rm -rf "$TMP" "$LOG"
  die "cloned cmms, but commit $COMMIT is not in it. A squash-merged branch that was then deleted leaves its own commits unreachable, and the remote eventually collects them, so an image tagged with one of those names a commit nobody can fetch. If it still exists in a local checkout, push it back so it is reachable:
    git push origin $COMMIT:refs/tags/preprod-schema-$COMMIT
Then re-run this."
fi
if ! git -C "$TMP" checkout --quiet --detach "${COMMIT}^{commit}" >"$LOG" 2>&1; then
  tail -20 "$LOG" >&2; rm -rf "$TMP" "$LOG"
  die "cloned cmms but could not check out $COMMIT (output above)."
fi
rm -f "$LOG"
mv "$TMP" "$CLONE"

# Asserted after the move, not assumed from the commands succeeding. This directory is about to decide
# which model every generated row is shaped by.
ACTUAL=$(git -C "$CLONE" rev-parse --short HEAD 2>/dev/null || true)
[ "$ACTUAL" = "$COMMIT" ] \
  || die "cloned to $CLONE but it is at '${ACTUAL:-unknown}', not $COMMIT. Refusing to leave a clone that misrepresents which build it holds."
[ -f "$CLONE/src/Cmms.Infrastructure/Cmms.Infrastructure.csproj" ] \
  || die "$CLONE has no src/Cmms.Infrastructure, so it is not a cmms checkout. Is CMMS_URL right?"

if $PRINT_ONLY; then printf '%s\n' "$CLONE"; exit 0; fi
ok "cloned cmms $COMMIT"
fact "path" "$CLONE"
note "nothing here references your own checkout; this clone is self-contained."
