#!/usr/bin/env bash
# Static conformance test for the load-data delivery chain (lnicoara/cmms#2917, per #2908).
#
# Azure-free and CI-safe: it reads the scripts and templates rather than talking to Azure, in the style of
# oidc-conformance.test.sh and mission-deploy-conformance.test.sh.
#
# What it guards is a set of properties that are cheap to assert and expensive to lose. Every one of them
# was a deliberate decision on #2908 or #2917, and every one of them is the kind of thing a later edit can
# quietly reverse while leaving something that still deploys and still runs:
#
#   - the load is PLAN-ONLY unless someone opts in, in all three places that can start one
#   - checkpoints never land in the work-order photo store
#   - the load-test account never becomes the repo's first shared-key exception
#   - the staged artifact is verified before any row is written
#   - deleting rows needs two independent yeses, and the load path cannot reach the delete path
#   - the OPERATOR SCRIPT deletes nothing at all, and refuses to start a job configured to
#
# Exit 1 = a real assertion failure.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

# Renamed from load-tenant.sh: this tooling only ever addresses pre-prod, and the old name also carried a
# clear that emptied the tenant it named. Both are gone; the name says the one environment it acts on.
SCRIPT=scripts/pre-prod/load-pre-prod.sh
JOB=infra/load-job.bicep
STORE=infra/modules/load-test-store.bicep
ENTRY=tools/Cmms.LoadDataRunner/docker-entrypoint.sh
RUNNER=tools/Cmms.LoadDataRunner/Program.cs
DOCKERFILE=tools/Cmms.LoadDataRunner/Dockerfile
LIB=scripts/pre-prod/lib/output.sh

fail() { echo "FAIL: $*" >&2; exit 1; }
ok()   { echo "  ok: $*"; }

for f in "$SCRIPT" "$JOB" "$STORE" "$ENTRY" "$RUNNER" "$DOCKERFILE" "$LIB"; do
  [ -f "$f" ] || fail "$f is missing. The delivery chain is incomplete."
done

echo "== plan-only by default =="

# The OPERATOR SCRIPT is the exception, since lnicoara/cmms#2993, and it is the only one. It is named
# load-pre-prod, so typing its name IS the request to load; a second --execute asked the same question
# twice. What it must still offer is the dry run, under its own flag.
grep -q '^EXECUTE=true' "$SCRIPT" \
  || fail "$SCRIPT must default EXECUTE=true. It is named for loading, and the layers below it are what catch a load nobody asked for."
grep -qE '^\s*--plan\) EXECUTE=false' "$SCRIPT" \
  || fail "$SCRIPT must offer --plan. Making the load the default removes the dry run only if nothing replaces it, and staging 3.4 GB to find out what would happen is worth keeping."
# --execute was the old spelling and is all over the shell history and the notes. It asks for what now
# happens anyway, so it must keep working rather than dying on an unknown argument.
grep -qE '^\s*--execute\) EXECUTE=true' "$SCRIPT" \
  || fail "$SCRIPT must still accept --execute. Every existing command says it, and refusing it breaks them for no gain."
ok "$SCRIPT loads by name, offers --plan, and still accepts --execute"

# The job template. Even a hand-run `az deployment group create` plus `job start`, bypassing the script,
# must not load. This is the layer that catches a deploy nobody meant to be a load.
grep -qE 'param loadExecute bool = false' "$JOB" \
  || fail "$JOB must default loadExecute to false, or a deploy-and-start could move 42.7 million rows."
ok "$JOB defaults loadExecute to false"

# The container. Same property one layer further in, for anyone who runs the image directly.
grep -q 'LOAD_EXECUTE:-false' "$ENTRY" \
  || fail "$ENTRY must default LOAD_EXECUTE to false."
ok "$ENTRY defaults LOAD_EXECUTE to false"

# Bicep's string(true) emits "True", not "true". A case-SENSITIVE comparison against lowercase made
# LOAD_EXECUTE=True read as false, so the job planned, exited 0, and was indistinguishable from a
# successful load in the execution list. The value must be normalised, never compared raw.
grep -q 'truthy()' "$ENTRY" \
  || fail "$ENTRY must normalise boolean env vars; Bicep sends 'True' and a raw != \"true\" test silently plans instead of loading."
if grep -qE '\[ *"\$\{LOAD_EXECUTE[^]]*\}" *!= *"true" *\]' "$ENTRY"; then
  fail "$ENTRY compares LOAD_EXECUTE case-sensitively against \"true\"; Bicep sends \"True\" and this silently plans."
fi
ok "$ENTRY normalises LOAD_EXECUTE rather than comparing it raw"

echo "== the staged artifact is verified before anything is written =="

# The entrypoint must run the loader in plan mode BEFORE the execute run. Plan opens every chunk, reads
# each gzip trailer, checks the numbering has no gap, and cross-checks the derived total against
# manifest.totalRows, so a download that lost or truncated a file fails before a row is written.
plan_line=$(grep -n 'Cmms.LoadDataRunner.dll --artifact' "$ENTRY" | head -1 | cut -d: -f1)
exec_line=$(grep -n 'Cmms.LoadDataRunner.dll --artifact.*--execute' "$ENTRY" | head -1 | cut -d: -f1)
[ -n "$plan_line" ] && [ -n "$exec_line" ] || fail "$ENTRY must invoke the loader for both a plan and an execute."
[ "$plan_line" -lt "$exec_line" ] \
  || fail "$ENTRY must run the loader in PLAN mode before the --execute run, so a corrupt download is caught first."
ok "$ENTRY verifies (plan) before it loads (execute)"

echo "== checkpoints stay out of the product photo store =="

# The loader reaches checkpoints by its OWN key. Storage__BlobUrl is the work-order photo store holding
# real uploaded product data, and a bulk job must not be handed write access to it. #2917 round 2.
grep -qE "name: 'Storage__LoadCheckpointBlobUrl'" "$JOB" \
  || fail "$JOB must set Storage__LoadCheckpointBlobUrl for the checkpoint store."
# Matched as an env ENTRY, not as any mention: the header comment names the key it is deliberately not
# setting, and a bare substring search would flag that as the violation it is explaining.
if grep -qE "name: 'Storage__BlobUrl'" "$JOB"; then
  fail "$JOB must NOT set Storage__BlobUrl: that key is the work-order photo store, and overriding it would point one key at two accounts."
fi
ok "$JOB uses a dedicated checkpoint key and never overrides Storage__BlobUrl"

grep -q 'Storage:LoadCheckpointBlobUrl' "$RUNNER" \
  || fail "$RUNNER must read Storage:LoadCheckpointBlobUrl rather than resolving the shared IBlobStore."
ok "$RUNNER binds checkpoints to the dedicated key"

echo "== the load-test account is not a security exception =="

# Mounting an SMB Azure Files share would have required a storage account key. Every storage account in
# this repo sets allowSharedKeyAccess: false, and this one must not become the first exception. If a later
# change wants the mount back, it has to change this test on purpose. #2917 round 3.
grep -q 'allowSharedKeyAccess: false' "$STORE" \
  || fail "$STORE must set allowSharedKeyAccess: false, matching every other storage account in this repo."
ok "$STORE is RBAC-only"

grep -qE 'azureFile|AzureFile' "$JOB" \
  && fail "$JOB must not mount an Azure Files share: Container Apps requires an account key for SMB, which would force allowSharedKeyAccess on."
ok "$JOB mounts no Azure Files share"

echo "== deleting needs two independent yeses =="

# The target is the SEEDED demo-health tenant, so its write set has to be emptied before a load: the
# artifact ships ServiceLines rows on the same well-known Guids DemoDataSeeder writes, and they collide on
# the primary key. That makes the loader capable of deleting, which #2908 round 3 deliberately kept it
# from being, so the capability is fenced: it needs --clear-target AND --execute, and neither alone does
# anything. These assertions are what stop a later edit collapsing that into one flag.
CLEANER=tools/Cmms.LoadDataRunner/TargetCleaner.cs
[ -f "$CLEANER" ] || fail "$CLEANER is missing."
grep -q 'options.ClearTargetSlug' "$RUNNER" \
  || fail "$RUNNER must gate clearing behind an explicit ClearTargetSlug."
grep -qE 'if \(!execute\)' "$CLEANER" \
  || fail "$CLEANER must require execute in addition to the clear flag before it deletes anything."
ok "clearing needs both --clear-target and --execute"

echo "== the named-tenant fence reaches the thing that actually deletes =="

# The operator script is NOT the only way to start this job. A Bicep deploy, a portal edit, or a change to
# the job's environment all reach the container directly, so a confirmation that lives only in the script
# is not a fence at all. The slug has to travel the whole way down, and every layer has to carry a NAME
# rather than a boolean, or the fence collapses back to "two booleans and a wrong slug".
grep -q 'param clearTargetSlug string' "$JOB" \
  || fail "$JOB must take clearTargetSlug as a STRING. A bool lets a deploy with a wrong tenantSlug empty the wrong tenant."
if grep -qE 'param clearTarget bool' "$JOB"; then
  fail "$JOB must not carry a boolean clear flag: the confirmation has to name the tenant."
fi
ok "$JOB carries the tenant name, not a boolean"

grep -q 'LOAD_CLEAR_TARGET_SLUG' "$ENTRY" \
  || fail "$ENTRY must pass the clear TARGET SLUG through, not a boolean."
grep -q -- '--clear-target=' "$ENTRY" \
  || fail "$ENTRY must invoke --clear-target=<slug>."
ok "$ENTRY passes the tenant name through"

# And the comparison itself, at the binary, which is the only place that is downstream of every path.
grep -qE 'ClearTargetSlug.*slug|slug.*ClearTargetSlug' "$RUNNER" \
  || fail "$RUNNER must compare the named clear target against the tenant it resolved, and refuse on mismatch."
ok "$RUNNER refuses when the named tenant is not the target"

# Clearing must never be reachable from the load path itself. If ClearTableAsync ever gets called from
# Loader.cs, a plain load could start deleting rows.
if grep -q 'ClearTableAsync' tools/Cmms.LoadDataRunner/Loader.cs; then
  fail "Loader.cs must never call ClearTableAsync: the loader loads, and deleting is a separate operation."
fi
ok "the load path cannot reach the delete path"

# demo-health is a SEEDED tenant, so a load into it collides on the primary key with the ServiceLines rows
# the artifact ships. The script must refuse that up front and say why, rather than doing real work first.
grep -q 'demo-health' "$SCRIPT" \
  || fail "$SCRIPT must name demo-health explicitly, either as the target or in its guard."
ok "$SCRIPT names demo-health explicitly"

echo "== the load does not decide or change pre-prod's schema (lnicoara/cmms#2978) =="

# The script used to build cmms-migrate, deploy caj-cmms-migrate, start it and wait for it, on every run.
# That is a load-test script changing what schema pre-prod runs, and it did so measurably: #2993 found
# pre-prod's tenant schema 85 commits ahead of the API image pre-prod was serving, because each load
# dragged the schema toward whatever was checked out while the app stayed where it had been promoted.
#
# Matched against uncommitted lines only. The removal is explained in a comment block that necessarily
# names the job and the command an operator runs deliberately, and a bare search would read that
# explanation as the violation it describes.
if grep -vE '^[[:space:]]*#' "$SCRIPT" | grep -qE 'build_if_absent cmms-migrate'; then
  fail "$SCRIPT builds the migrate image again. Establishing pre-prod's schema belongs to the deploy path, not to a load test."
fi
if grep -vE '^[[:space:]]*#' "$SCRIPT" | grep -qE 'job start -n caj-cmms-migrate'; then
  fail "$SCRIPT starts the migration job. A load must not apply migrations to every tenant database as a side effect of loading one."
fi
if grep -vE '^[[:space:]]*#' "$SCRIPT" | grep -qE 'migrate-job\.bicep'; then
  fail "$SCRIPT deploys the migrate job template. It has no business establishing the schema it is about to load into."
fi
ok "$SCRIPT neither builds, deploys, nor starts the migration job"

# Exactly one image, and it is the loader's. The migrate build coming back would show up here too, but this
# says the positive thing: a load builds the thing that loads, and nothing else.
# Leading whitespace allowed, because the call sits inside a conditional since lnicoara/cmms-data#14
# (--start-only refuses instead of building). Anchoring it to column zero made an indentation change read
# as "zero images are built", and `|| true` is why you can read that sentence at all: grep -c exits 1 when
# it counts nothing, and under `set -euo pipefail` the assignment carried that status and killed this suite
# with no output at all, three sections before the assertion that would have explained it (#3047, #3048).
IMAGE_BUILDS=$(grep -vE '^[[:space:]]*#' "$SCRIPT" | grep -cE '^[[:space:]]*build_if_absent ' || true)
[ "$IMAGE_BUILDS" = "1" ] \
  || fail "$SCRIPT builds $IMAGE_BUILDS images; expected exactly 1 (cmms-load). The load runs in-VNet, so it needs its own image and nothing else's."
ok "$SCRIPT builds one image, the loader's"

echo "== the image carries the PINNED model, and no credential (lnicoara/cmms#3026) =="

# The image needs the product's EF model and cannot reach outside its own build context, so the model
# arrives as a package in .packages/, packed out of a cmms checkout by this script moments before the
# build. There is no feed and therefore nothing to authenticate, which is the strongest version of "the
# credential does not leak": there is no credential.
grep -qE '^COPY \.packages/' "$DOCKERFILE" \
  || fail "$DOCKERFILE must copy .packages/ into the build context. That is where Cmms.Infrastructure comes from; without it the restore has no source for the model and the image cannot be built at all."
ok "$DOCKERFILE takes the model from the packed context"

# No credential handling may creep back. A token in a build arg is recorded in image history and
# `docker history` prints it, and a secret mount means a hosted feed has returned along with the
# credential distribution problem this arrangement avoids.
if grep -qE '^ARG NUGET_TOKEN|mount=type=secret' "$DOCKERFILE"; then
  fail "$DOCKERFILE handles a build credential. The model comes from .packages/ in the context and nothing authenticates; a token here means a hosted feed came back."
fi
# CREDENTIAL-shaped build args only. This once banned every --build-arg and started failing the moment a
# version string was passed through one (lnicoara/cmms#3050), which is not a secret and is already visible
# in the built image's assembly metadata. An assertion that flags correct code is one people route around,
# so it names what it actually objects to.
if grep -vE '^[[:space:]]*#' "$SCRIPT" | grep -qiE -- '--secret-build-arg|--build-arg[= ]*"?[A-Za-z_]*(TOKEN|SECRET|PASSWORD|PAT|KEY)'; then
  fail "$SCRIPT passes a credential to the image build. Packing the pinned commit into the context needs none, and a --build-arg is recorded in image history."
fi
ok "nothing in the chain handles a feed credential"

# The product's source must NOT be in the image. Cmms.Infrastructure is a pinned package, and a stray
# COPY src/ would mean the image compiles whatever is lying around instead of the pinned model, which is
# the drift the pin exists to prevent.
if grep -qE '^COPY src/' "$DOCKERFILE"; then
  fail "$DOCKERFILE copies the product source. Cmms.Infrastructure is a pinned package now, and compiling a local copy instead would reintroduce exactly the model drift the pin prevents."
fi
ok "$DOCKERFILE builds against the pinned package, not a source copy"

# THE ASSERTION THAT MAKES THE PIN MEAN ANYTHING. Packing whatever the checkout happens to be at, under
# the version this repo declares, puts a different model behind that version number invisibly: the build
# succeeds and the artifact fits nothing. The commit has to be checked before the pack, not after.
# Superseded by lnicoara/cmms#3050 and rewritten rather than deleted, because the PROPERTY still matters:
# the packed version must be the commit that was actually packed. It used to be enforced by comparing the
# operator's checkout against a pin in Directory.Build.props and refusing on a mismatch. There is nothing
# left to mismatch: the clone IS the pinned commit, and the version is derived from the directory it came
# out of, so the two cannot disagree by construction. Better than a check is not needing one.
grep -q 'CMMS_VERSION="1.0.0-g$commit"' "$SCRIPT" \
  || fail "$SCRIPT must derive the package version from the commit it just packed. A version from anywhere else can name a build the packages are not."
ok "$SCRIPT derives the version from the commit it packed"

echo "== everything the image COPYs actually exists (lnicoara/cmms#3052) =="

# A COPY naming a path the build context does not have fails REMOTELY, after `az acr build` has uploaded
# the context and pulled the SDK image. Roughly two minutes, and the message names a filename with no hint
# that the file was never in this repository at all:
#
#   COPY failed: file not found in build context: stat global.json: file does not exist
#
# That is what happened: global.json pins the SDK band and stayed behind in cmms when the tooling moved,
# while the Dockerfile kept copying it. Every image build died the same way. Checkable here in
# milliseconds, so there is no reason for it to be discovered in Azure.
#
# .packages/ is exempt, and only that: the load script creates it immediately before the build by packing
# cmms at pre-prod's commit, so it is legitimately absent from a clean checkout.
while IFS= read -r src; do
  case "$src" in
    .packages/|.packages) continue ;;
  esac
  [ -e "$src" ] \
    || fail "$DOCKERFILE copies '$src', which does not exist in this repository. The build would upload the context, pull the SDK image, and only then fail with a message naming the file but not the reason."
done < <(grep -E '^COPY ' "$DOCKERFILE" | grep -v -- '--from=' | awk '{print $2}')
ok "every path $DOCKERFILE copies is present"

# The SDK pin specifically, because losing it is silent rather than loud. The model arrives as a package
# built elsewhere, so this file is the only thing pinning the compiler that consumes it, and a newer SDK
# with LangVersion=latest changes overload resolution (lnicoara/cmms#1434).
[ -f global.json ] \
  || fail "global.json is missing. It pins the SDK band for both a laptop and the image; without it a machine's newer preinstalled SDK wins and compiles different IL from the same source."
grep -q 'rollForward' global.json \
  || fail "global.json does not set rollForward, so it pins one exact patch and fails on any other 8.0 feature band rather than accepting it."
ok "global.json pins the SDK band"

echo "== the pin is read from pre-prod, never from the operator (lnicoara/cmms#3050) =="

CLONER=scripts/pre-prod/clone-cmms-from-pre-prod.sh
[ -f "$CLONER" ] || fail "$CLONER is missing; it is how this repo obtains cmms source at the commit pre-prod runs."

# The commit is a FACT ABOUT PRE-PROD. It is read off the caj-cmms-migrate image, the same source
# TargetBuild uses for the generator, so the two halves of the tooling cannot disagree about which build
# they are for. Anything else -- a value in a checked-in file, an env var, whatever is checked out -- is a
# copy of that fact that can drift from it.
grep -q 'caj-cmms-migrate' "$CLONER" \
  || fail "$CLONER must resolve the commit from pre-prod's caj-cmms-migrate image. A pin that comes from anywhere else is a second copy of a fact pre-prod already holds."
ok "$CLONER resolves the commit from pre-prod itself"

# And the load script must not go back to reading the operator's checkout. It packed out of $CMMS_REPO
# (default ~/git/cmms) and REFUSED unless that checkout sat on the pinned commit, which is a load test
# telling somebody to move their working tree so it can build.
if grep -vE '^[[:space:]]*#' "$SCRIPT" | grep -qE 'CMMS_REPO'; then
  fail "$SCRIPT reads CMMS_REPO again. Both the generator and the loader are pinned to pre-prod; where the operator's own checkout happens to be is not an input."
fi
grep -q 'clone-cmms-from-pre-prod.sh' "$SCRIPT" \
  || fail "$SCRIPT must obtain its cmms source from $CLONER, not from a path the operator maintains."
ok "$SCRIPT never reads the operator's checkout"

# A clone, not a worktree. A worktree's .git is a file pointing back at the repository it came from, so it
# is permanently tethered to that path and breaks when it moves. A clone is self-contained, which is the
# whole property being bought here.
if grep -vE '^[[:space:]]*#' "$CLONER" | grep -qE 'git .*worktree'; then
  fail "$CLONER creates a worktree. A worktree stays tethered to the repository it came from; only a real clone makes this repo self-contained for a pin."
fi
ok "$CLONER makes a self-contained clone"

# An interrupted clone must not leave something a later run trusts. It clones to a temporary path and
# renames into place, so .cmms/<commit>/ is either a good checkout or absent, never a convincing-looking
# directory that builds nothing.
grep -q 'mv "$TMP" "$CLONE"' "$CLONER" \
  || fail "$CLONER must clone to a temporary path and move it into place. A clone interrupted halfway would otherwise leave .cmms/<commit>/ present and incomplete, and every later run would have to be clever about it."
ok "$CLONER cannot leave a half-finished clone behind"

echo "== a failed lookup is not reported as a missing tag (lnicoara/cmms#3056) =="

CLONE=scripts/pre-prod/clone-cmms-from-pre-prod.sh
[ -f "$CLONE" ] || fail "$CLONE is missing; it is how both the generator and the loader learn which commit pre-prod runs."

# The clone script asks Azure two questions, and the answers to "az failed" and "az returned nothing" go to
# completely different places: one is Azure being briefly unavailable, the other is an untagged manifest.
# Reporting the first as the second sent an operator to investigate a registry problem that did not exist,
# while the tag was present the whole time. `2>/dev/null || true` on those calls is what made them
# indistinguishable.
if grep -nE 'az (containerapp|acr) [^|]*2>/dev/null \|\| true' "$CLONE" | grep -q .; then
  grep -nE 'az (containerapp|acr) [^|]*2>/dev/null \|\| true' "$CLONE" >&2
  fail "$CLONE discards az's stderr and exit code (lines above). A transient failure then arrives as an empty string and gets reported as a missing tag, which is a confident, wrong answer."
fi
grep -q 'az_read()' "$CLONE" \
  || fail "$CLONE must route its az calls through a helper that keeps the exit code and the error text, so a failure can be told apart from an empty result."
ok "$CLONE keeps az's exit code and error text"

# The error must survive the SUBSHELL. az_read is always called inside a command substitution, so anything
# it assigns to a variable is discarded on return: the first version captured the error faithfully into
# AZ_LAST_ERROR and printed "az said: <nothing>", which is the same useless message it was meant to fix.
if grep -qE 'AZ_LAST_ERROR=' "$CLONE"; then
  fail "$CLONE captures az's error into a shell variable set inside a command substitution. That is a subshell; the assignment is discarded and the message reads 'az said: <nothing>'. Write it to a file whose path is fixed before the call."
fi
grep -q 'az_error()' "$CLONE" \
  || fail "$CLONE must read az's error from a file, not from a variable set in a subshell."
ok "$CLONE's error text survives the subshell it was produced in"

# And there has to be a way through when Azure genuinely cannot be reached, or a transient outage blocks a
# load entirely. Explicit and never a default: an unreachable pre-prod must not quietly become "use main".
grep -q 'CMMS_COMMIT' "$CLONE" \
  || fail "$CLONE must accept an explicit CMMS_COMMIT override for when Azure is unreachable. Without one, an outage blocks every load with no way past it."
ok "$CLONE can be told the commit when pre-prod cannot be asked"

echo "== no script may exit silently (lnicoara/cmms#3048) =="

# The rule: a non-zero exit always says why. A silent death already cost a run that had SUCCEEDED
# (#3047), so this is a floor under the whole class rather than a fix for one line.
#
# BEHAVIOURAL, in a separate process. A subshell inside `if !` has errexit suppressed and cannot observe
# what it is testing, which is how the first guard for #3047 passed while the bug was live.
grep -q 'trap _output_on_err ERR' "$LIB" \
  || fail "$LIB must install an ERR trap. Without it, a failing command substitution under set -e kills a script and prints nothing, because a failed grep is silent."

# It must be ERR and NOT EXIT. On bash 3.2, which is what macOS ships and what these run on, merely
# installing an EXIT trap turns a `set -u` unbound-variable death from exit 1 into exit 0, and the trap
# reads $? as 0 so it cannot even detect the failure it is masking. A backstop that hides a failure status
# is worse than the silence it replaced.
if grep -qE '^trap .* EXIT' "$LIB"; then
  fail "$LIB installs an EXIT trap. On bash 3.2 that masks a set -u failure as exit 0. Report from ERR, which changes no status."
fi
ok "$LIB reports failures from ERR without touching the exit status"

silent=$(mktemp); errout=$(mktemp)
{ echo '#!/usr/bin/env bash'
  echo 'set -euo pipefail'
  echo ". \"$PWD/$LIB\""
  # The exact shape that shipped: a grep matching nothing, inside a command substitution.
  echo 'L=$(mktemp); echo irrelevant > "$L"'
  echo 'X=$(grep -oE "[0-9]+ Done" "$L" | tail -1 | cut -d" " -f1)'
  echo 'echo SHOULD_NOT_REACH'
} > "$silent"
bash "$silent" >/dev/null 2>"$errout" && fail "the probe script was supposed to die and did not; this assertion is testing nothing."
[ -s "$errout" ] \
  || fail "a script sourcing $LIB died with an EMPTY stderr. That is the silent exit this rule forbids: an operator cannot tell it from a completed run."
grep -q 'FAILED' "$errout" \
  || fail "a script sourcing $LIB died without a FAILED line. It wrote: $(head -c 200 "$errout")"
rm -f "$silent" "$errout"
ok "a set -e death reports which command failed, on which line"

# And a clean run must stay quiet, or the backstop becomes noise people learn to skip.
quiet=$(mktemp); qout=$(mktemp)
{ echo '#!/usr/bin/env bash'; echo 'set -euo pipefail'; echo ". \"$PWD/$LIB\""; echo 'ok "worked"'; } > "$quiet"
bash "$quiet" >/dev/null 2>"$qout" || fail "a clean script sourcing $LIB exited non-zero."
[ -s "$qout" ] && fail "a successful run wrote to stderr: $(head -c 200 "$qout"). The backstop must be silent when nothing failed."
rm -f "$quiet" "$qout"
ok "a clean run stays quiet"

echo "== reporting on the work cannot fail the work (lnicoara/cmms#3047) =="

# Under `set -e` an assignment carries the exit status of its command substitution, so
#
#   UP_DONE=$(grep -oE '[0-9]+ Done' "$LOG" | ...)
#
# kills the script when the grep matches nothing — silently, because a failed grep prints nothing. That
# shipped, and a completed 209-file upload ended at a bare shell prompt with no error, no summary, and no
# way for the operator to tell a finished upload from a crash.
#
# BEHAVIOURAL, not a grep for `|| true`. The extraction lines are pulled out of the script and run under
# the same strict flags against a log that does NOT contain what they look for, which is the condition
# that broke. A source-text check would pass the moment someone wrote the guard differently, and fail the
# moment someone wrote it correctly in a way the pattern did not anticipate.
probe=$(mktemp)
printf 'azcopy said something else entirely\nno summary line here\n' > "$probe"
extract=$(grep -E '^[[:space:]]*(UP_DONE|UP_FAILED|SIZE)=' "$SCRIPT" | sed 's/^[[:space:]]*//')
[ -n "$extract" ] || fail "$SCRIPT no longer extracts an upload summary; if the reporting moved, move this assertion with it."

# Run in a SEPARATE bash process, not a subshell inside the `if !` condition. Bash suppresses errexit for
# any command whose failure is being tested, including a `( ... )` used as a condition, so the obvious
# spelling of this check silently tests nothing. The first version of this assertion did exactly that: the
# bug was reintroduced and it passed. A separate process keeps set -e live and reports through $?.
runner=$(mktemp)
{ echo 'set -euo pipefail'
  echo 'AZ_LOG="$1"; ARTIFACT_DIR="$2"'
  printf '%s\n' "$extract"
  echo 'exit 0'
} > "$runner"
scratch=$(mktemp -d)
if ! bash "$runner" "$probe" "$scratch" >/dev/null 2>&1; then
  fail "$SCRIPT's upload-summary extraction exits non-zero when the log lacks the lines it parses, and under set -e that kills the run AFTER the upload has already succeeded. It reports on the work; it must not be able to fail it."
fi
rm -rf "$probe" "$runner" "$scratch"
ok "the upload summary survives a log that does not contain it"

echo "== the operator scripts share one output style (lnicoara/cmms#3046) =="

# These are read side by side during an incident. A phase header that is bold blue in one script and plain
# in another is a difference the reader spends attention on before discovering it means nothing.
#
# One definition in lib/output.sh, sourced by each, rather than the same twenty lines pasted three times
# and drifting. The sourcing resolves from the script's own location so it survives being run from any
# directory, which a `. lib/output.sh` relative to the working directory would not.
[ -f "$LIB" ] || fail "$LIB is missing; every operator script sources it for colour, phase headers and die()."
for op in scripts/pre-prod/*.sh; do
  [ -f "$op" ] || continue
  grep -q 'lib/output.sh' "$op" \
    || fail "$op does not source $LIB. Pasting the helpers in again is how three scripts end up with three styles."
  grep -q 'BASH_SOURCE' "$op" \
    || fail "$op sources $LIB by a path relative to the working directory, so it breaks when run from anywhere else. Resolve it from \$BASH_SOURCE."
done
ok "every script in scripts/pre-prod sources $LIB from its own location"

# No script may define its own copy of the helpers or the colour tokens. THAT is the drift worth catching:
# a second definition silently wins over the sourced one and the two versions diverge from then on.
#
# Deliberately NOT a hunt for raw echo/printf. load-pre-prod.sh formats a carriage-return progress clock
# and its result banners with the tokens directly, because no helper covers those, and it dumps captured
# git output verbatim. All of that is correct, and an assertion that flagged it would be an assertion
# people route around.
for op in scripts/pre-prod/*.sh; do
  [ -f "$op" ] || continue
  if grep -qE '^(step|fact|ok|warn|note|die)\(\) *\{' "$op"; then
    grep -nE '^(step|fact|ok|warn|note|die)\(\) *\{' "$op" >&2
    fail "$op defines its own output helper (lines above). It sources $LIB; a second definition wins over the shared one and the two drift apart from that moment."
  fi
  if grep -qE '^ *C_(RESET|BOLD|DIM|BLUE|GREEN|YELLOW|RED)=' "$op"; then
    fail "$op defines its own colour tokens. They come from $LIB, which is also what makes NO_COLOR and the not-a-terminal case work the same way everywhere."
  fi
done
ok "no script redefines a helper or a colour token"

# die() must be available BEFORE the argument loop runs, or a flag with a missing value dies with
# 'die: command not found' instead of its own message. That happened, which is why it is asserted.
for op in scripts/pre-prod/*.sh; do
  [ -f "$op" ] || continue
  src=$(grep -n 'lib/output.sh' "$op" | head -1 | cut -d: -f1)
  loop=$(grep -nE '^for arg in "\$@"' "$op" | head -1 | cut -d: -f1)
  [ -n "$loop" ] || continue
  [ "$src" -lt "$loop" ] \
    || fail "$op sources $LIB AFTER its argument loop, so a bad flag dies with 'die: command not found' instead of its own error."
done
ok "die() is defined before every argument loop"

echo "== every operator script's repo guard names a path that exists (lnicoara/cmms#3045) =="

# Each generate script opens by confirming it is standing in the right repository, with a guard shaped
#   [ -f <some-path> ] || die "... does not look like the ... repo ..."
#
# generate-pre-prod-small.sh guarded on tools/Cmms.LoadDataGenerator/small.json, and #3026 deleted that
# path when the profiles were single-sourced into profiles/. The script then died on EVERY invocation,
# telling the operator that cmms-data was not cmms-data. A guard pointing at a path nobody maintains is
# worse than no guard: it fails closed, constantly, for a reason that has nothing to do with what it
# claims to be checking.
#
# BEHAVIOURAL, not a grep for the right string. The path is extracted from each script and tested, so this
# tracks whatever the guard actually says rather than whatever it said the day it was written.
for gen in scripts/pre-prod/generate-pre-prod-*.sh; do
  [ -f "$gen" ] || continue
  guard=$(grep -oE '^\[ -f [^]]+ \]' "$gen" | head -1 | sed 's/^\[ -f //; s/ \]$//')
  [ -n "$guard" ] \
    || fail "$gen has no repo guard. Every generate script must confirm it is in the right checkout before it writes gigabytes somewhere."
  [ -f "$guard" ] \
    || fail "$gen guards on '$guard', which does not exist. The script therefore refuses to run at all, and blames the repository for it. Point the guard at a path this repo actually has."
  ok "$(basename "$gen") guards on $guard, which exists"
done

# The full run in particular must not be able to silently produce the defaults. GeneratorOptions defaults
# carry full.json's OWN seed, so a full run that lost GENERATOR_PARAMS emits a 5,000-work-order artifact
# stamped with the full seed: the manifest looks right and the key space is a strict subset of the real
# one's, which is the poisoning hazard the seed rule exists to prevent. The row count is the only thing
# that tells the two apart, so it is asserted after the run rather than inferred from the flag being passed.
FULLGEN=scripts/pre-prod/generate-pre-prod-full.sh
if [ -f "$FULLGEN" ]; then
  grep -q 'this is not the full dataset' "$FULLGEN" \
    || fail "$FULLGEN must assert the generated row count against full.json's WorkOrders. Passing GENERATOR_PARAMS is the gate; the row count is the outcome, and a defaults run wears the full seed so nothing else distinguishes it."
  ok "$FULLGEN proves the artifact is the full one, not the defaults wearing its seed"
fi

echo "== the operator script deletes nothing =="

# The rule this section pins is the script's NAME. It loads pre-prod, so it loads; emptying a database is a
# different act and belongs under a different name. The clear it used to carry emptied the artifact's whole
# foreign-key closure, which under lnicoara/cmms#2993 is every table in the tenant model, and it ran BEFORE
# the load with each DELETE committing on its own. Every failed load attempt therefore left the tenant
# emptied with nothing to put back, which is the case a repeatedly-failing load hits hardest.
#
# These assertions are the inverse of the ones that used to live here. The delete fence itself is NOT
# relaxed: it still stands at the job, the entrypoint, and the binary, asserted above. What is asserted
# here is that this operator path cannot reach it.

# The deployment must state an EMPTY clear target, not interpolate a variable holding one.
grep -vE '^[[:space:]]*#' "$SCRIPT" | grep -qE 'clearTargetSlug=""' \
  || fail "$SCRIPT must pass clearTargetSlug=\"\" explicitly at the deployment, so a job left carrying a slug by an earlier run or a portal edit is converged back to deleting nothing."
if grep -vE '^[[:space:]]*#' "$SCRIPT" | grep -qE 'clearTargetSlug="\$'; then
  fail "$SCRIPT interpolates a variable into clearTargetSlug. This script deletes nothing, so the value is a constant empty string and never something a flag can set."
fi
ok "$SCRIPT deploys the job with an empty clear target"

# No live assignment can bring the flag back by the back door. Matched against uncommented lines only: the
# removed code is deliberately kept in place as comments, and a bare search would certify that history as
# the violation it is explaining.
if grep -vE '^[[:space:]]*#' "$SCRIPT" | grep -qE '^[[:space:]]*(CLEAR_TARGET|CLEAR_CONFIRM)='; then
  fail "$SCRIPT assigns CLEAR_TARGET or CLEAR_CONFIRM. The clear was removed from this script; a live assignment is it coming back."
fi
ok "$SCRIPT has no live clear variables"

# REFUSED, not ignored. An operator with the old command in their shell history must be told the flag is
# gone, or they read the run that follows as a load that cleared first.
grep -q 'clear-target has been removed from this script' "$SCRIPT" \
  || fail "$SCRIPT must refuse --clear-target with an explanation. Silently dropping it means the old command still appears to work."
ok "$SCRIPT refuses the removed flag instead of ignoring it"

# Assert the outcome, not the gate. Removing the flag removes one way to SET the variable, not the variable:
# LOAD_CLEAR_TARGET_SLUG lives on the job, and a Bicep deploy, a portal edit, or an earlier run of the old
# script can all leave a slug in it. This script starts the job, so it reads the value back off the deployed
# job and refuses rather than trusting the deployment it just issued.
grep -q "LOAD_CLEAR_TARGET_SLUG'\].value" "$SCRIPT" \
  || fail "$SCRIPT must read LOAD_CLEAR_TARGET_SLUG back off the deployed job before starting it. Issuing a deploy that sets it empty is not the same as the job being empty."
grep -q 'will not start a job that does' "$SCRIPT" \
  || fail "$SCRIPT must refuse to start caj-cmms-load when the deployed job is configured to clear a tenant."
ok "$SCRIPT reads the deployed clear target back and refuses a job that would delete"

echo "== artifact verification does not depend on the target =="

# These are different questions, and fusing them was a real bug: a plan run preflights the TARGET, a seeded
# target legitimately fails preflight, and under set -e that killed the run before the clear that was about
# to make it loadable. Verification must be answerable with no database at all.
grep -q 'verify-artifact' "$ENTRY" \
  || fail "$ENTRY must verify the staged artifact with --verify-artifact, which contacts no database."
# Matched on the argument, not on the LoaderOptions property: verification is handled from args directly,
# because it runs BEFORE the options are bound (binding happens after the host builder, which needs a
# database connection string that a verifying machine does not have).
grep -q -- '"--verify-artifact"' "$RUNNER" \
  || fail "$RUNNER must support artifact-only verification."
verify_line=$(grep -n -- '--verify-artifact' "$ENTRY" | head -1 | cut -d: -f1)
clear_line=$(grep -n -- '--execute' "$ENTRY" | tail -1 | cut -d: -f1)
[ "$verify_line" -lt "$clear_line" ] \
  || fail "$ENTRY must verify the artifact BEFORE the destructive run, or a corrupt download would be discovered after the tenant was emptied."
ok "the artifact is verified before anything is emptied"

echo "== the checkpoint container the job writes is the one that gets provisioned =="

# These disagreed once (checkpoints vs load-checkpoints) and the job would have written its resume record
# into a container nobody created.
CONTAINER=$(grep -oE "CheckpointContainer \{ get; set; \} = \"[a-z-]+\"" tools/Cmms.LoadDataRunner/LoaderOptions.cs | grep -oE '"[a-z-]+"' | tr -d '"')
[ -n "$CONTAINER" ] || fail "cannot read the default checkpoint container name from LoaderOptions.cs."
grep -q "name: '${CONTAINER}'" "$STORE" \
  || fail "$STORE must provision a container named '${CONTAINER}', matching LoaderOptions.CheckpointContainer."
ok "checkpoint container '${CONTAINER}' is provisioned and used"

echo "== LOAD_EXECUTE is actually READ, not merely passed =="

# BEHAVIOURAL, not a grep. Every other assertion here checks that a value is PASSED between layers, and
# that is precisely how "True" vs "true" survived review: the wiring was perfect and the far end could not
# read it. This extracts the real truthy() out of the entrypoint and runs it, so the question asked is
# "does the container treat what Bicep actually sends as true", not "does the file mention a variable".
eval "$(sed -n '/^truthy() {/,/^}/p' "$ENTRY")"
type truthy >/dev/null 2>&1 || fail "could not extract truthy() from $ENTRY."

# Bicep's string(true) emits exactly this. It is the value that broke.
truthy "True"  || fail "truthy() rejects 'True', which is exactly what Bicep's string(true) emits. This is the bug that made the job plan while reporting success."
truthy "true"  || fail "truthy() rejects 'true'."
truthy "TRUE"  || fail "truthy() rejects 'TRUE'."
ok "truthy() accepts True, true, TRUE"

# And must not be so loose that anything enables a destructive load.
for no in "False" "false" "" "no" "0" "nonsense"; do
  if truthy "$no"; then fail "truthy() accepts '$no'; a load must not start on an ambiguous value."; fi
done
ok "truthy() rejects False, false, empty, no, 0, and junk"

# The artifact carries the columns the model had when it was generated, and main moves. A column added
# since then makes every row short. That mismatch was found by the JOB, after a 3.7 GB upload and an image
# build, and was only readable from Log Analytics because the failed replica had been reaped. It is
# answerable locally in seconds because --verify-artifact opens no database.
# The verify path must be handled BEFORE the host builder. AddCmmsInfrastructure demands
# ConnectionStrings:Catalog and throws without it, so anything below it is unreachable on a machine with no
# database credentials, which is exactly the machine an operator verifies an artifact on. It shipped below
# the host once and died on a missing connection string while being asked about a directory of files.
# Both anchored to CODE, not to any mention. The comment above the verify block explains why it sits above
# AddCmmsInfrastructure, so matching the bare name found the explanation first and reported the opposite of
# the truth. That is the third assertion on this chain to certify a comment instead of the code.
VERIFY_LINE=$(grep -nE 'string.Equals\(a, "--verify-artifact"' "$RUNNER" | head -1 | cut -d: -f1)
HOST_LINE=$(grep -nE '^builder\.Services\.AddCmmsInfrastructure' "$RUNNER" | head -1 | cut -d: -f1)
[ -n "$VERIFY_LINE" ] && [ -n "$HOST_LINE" ] || fail "$RUNNER must handle --verify-artifact and build the host."
[ "$VERIFY_LINE" -lt "$HOST_LINE" ] \
  || fail "$RUNNER handles --verify-artifact AFTER AddCmmsInfrastructure, which throws without a catalog connection string, so verification is unreachable without database credentials."
ok "$RUNNER verifies artifacts before it needs any credentials"

grep -q -- '--verify-artifact --artifact=' "$SCRIPT" \
  || fail "$SCRIPT must verify the artifact against this checkout's model BEFORE uploading it and building an image."
ok "$SCRIPT verifies the artifact against the model before doing any work"

echo "== the script cannot deploy code you are not looking at =="

# IMAGE_TAG is derived from HEAD, so it is a CLAIM about which source built the image. That claim broke
# twice: once by running from a checkout whose HEAD lacked the fix, which deployed a binary without it and
# reported "Succeeded" while doing nothing. These assertions are the difference between a tag that names a
# commit and an image that IS that commit.
# SCOPED to the image's build context. An unscoped check blocked a real load over an edited CLAUDE.md,
# which cannot affect the built image at all.
grep -q 'git status --porcelain -- Directory.Build.props global.json nuget.config profiles tools' "$SCRIPT" \
  || fail "$SCRIPT must check dirtiness ONLY for the paths the Dockerfile copies; an unscoped check blocks a load over an unrelated edited file."
ok "$SCRIPT refuses a dirty tree only for files that reach the image"

grep -q 'cmms-load@\${DIGEST}' "$SCRIPT" \
  || fail "$SCRIPT must deploy the image by DIGEST, not by mutable tag."
ok "$SCRIPT deploys by digest, not by tag"

# Untagged manifests accumulate whenever a tag moves to a newer build, and JMESPath contains() THROWS on a
# null instead of skipping it. Without this filter the digest lookup fails outright the moment a second
# image exists in the repository, which is to say: on every run after the first.
# Matched on the QUERY line, not anywhere in the file: the comment above it explains the null filter, and
# a bare substring search happily matches that explanation while the query itself is unguarded.
grep -qE -- '--query "\[\?tags != null\] \| \[\?contains\(tags' "$SCRIPT" \
  || fail "$SCRIPT must filter out null tags IN THE QUERY before contains(); JMESPath contains() throws on null and the digest lookup dies once the registry holds an untagged manifest."
ok "$SCRIPT tolerates untagged manifests in the registry"

# The die must report what az actually said. The first version swallowed stderr and reported only that the
# digest was unresolvable, which named the symptom and hid the JMESPath error underneath it.
grep -q 'az said:' "$SCRIPT" \
  || fail "$SCRIPT must surface az's own error when the digest cannot be resolved."
ok "$SCRIPT surfaces the underlying error on a failed digest lookup"

grep -q 'Refusing to start it: a stale image' "$SCRIPT" \
  || fail "$SCRIPT must verify the deployed job carries the image it just built, before starting it."
ok "$SCRIPT verifies the deployed image before starting the job"

# The advice the script prints must not die on the variables INSIDE it. The failure it guards: a run that
# printed an az incantation containing $WS expanded it at print time, and under `set -u` an unset name
# aborted the script AFTER the job had started, so a healthy load read as a failed run.
#
# This used to extract a `cat <<NOTE` heredoc and render it. The heredocs are gone, so that assertion had
# quietly become a test of an empty string, passing because there was nothing left to check. Rewritten to
# assert the two things that are actually true now, both anchored to code that exists.
if grep -qE '^cat <<NOTE$' "$SCRIPT"; then
  fail "$SCRIPT is back to printing advice through an unquoted heredoc. That is the construct that expanded \$WS at print time and aborted a healthy run under set -u."
fi
# Every remaining line of printed advice goes through note(), whose argument is an ordinary double-quoted
# string, so an unescaped $NAME is expanded when the line is printed. That is FINE for a name the script
# has set, and it is what puts the real resource group into the command it hands you. The bug is a name
# the script never sets: under set -u that aborts the run, and it aborts it after the job has started.
#
# So the assertion is about unset names specifically, not about interpolation. An earlier version of this
# check flagged every $VAR and failed on $RG and $STORE, which are assigned twenty lines up. A test that
# cannot tell a working line from a broken one is worse than no test, because someone will "fix" the code
# to satisfy it.
UNSET_REFS=""
while IFS= read -r name; do
  grep -qE "^[[:space:]]*(local )?${name}=|^${name}=|read -r ${name}\$|for ${name} in " "$SCRIPT" || UNSET_REFS="$UNSET_REFS $name"
done < <(grep -E '^[[:space:]]*note "' "$SCRIPT" \
         | grep -oE '[^\\]\$\{?[A-Za-z_][A-Za-z_0-9]*' \
         | grep -oE '[A-Za-z_][A-Za-z_0-9]*$' | sort -u)
if [ -n "$UNSET_REFS" ]; then
  fail "$SCRIPT prints advice referencing variable(s) it never assigns:$UNSET_REFS. Under set -u that aborts the run after the job has started, so a healthy load reads as a failure. Escape them (\\\$VAR) or set them."
fi
ok "the printed advice references only variables the script sets"

# The runner's output must be WAITED for, not grabbed. Log Analytics lags a minute or two behind a job
# ending, so reading it once at the moment the job finishes returns whatever happened to have arrived:
# mid-load table lines, out of order, with the loader's actual verdict still in flight. That is precisely
# what made a completed run unreadable.
grep -qE "grep -qE 'rows loaded\|PLAN ONLY\|FAILED while\|REFUSING'" "$SCRIPT" \
  || fail "$SCRIPT must poll Log Analytics until the runner's TERMINAL line has landed. Reading once and printing the tail reports a finished load as a handful of unordered fragments."
ok "$SCRIPT waits for the runner's verdict before reporting it"

echo "== the job is sized for a load measured in days =="

# The seed job allows 1800s. This one moves 42.7M rows, so a 30-minute timeout would guarantee a killed
# replica every run. The original figure here reasoned from a General Purpose database's ~4.5 MB/s log
# throughput; a full run on 2026-08-10 retired that estimate, because nothing on the database was
# saturated (log write 4%) and the real rate was ~100k rows per ~11 minutes. See the timeoutHours block
# in load-job.bicep for the measurement and for why the ceiling is now 96 hours.
grep -q 'replicaTimeout: timeoutHours \* 3600' "$JOB" \
  || fail "$JOB must express replicaTimeout in hours; the seed job's 1800s is far too short for this load."
ok "$JOB expresses its timeout in hours"

echo "== a dataset has an identity the job can act on (lnicoara/cmms#2979) =="

# The defect this pins: 'the artifact' was one global blob address, so a profile named a directory on the
# operator's laptop and nothing beyond it. Uploading the 250k 'small' dataset over the 4M 'full' one
# replaced the chunk names they shared, left full's other 379 files sitting there, and the job loaded the
# union: 2,268,899 chunk rows against a manifest declaring 250,869. The job cannot be asked for a dataset
# it has no way to name, so the name has to reach it.
grep -q 'param artifactProfile string' "$JOB" \
  || fail "$JOB must take the dataset name as a parameter. Without it the job has no way to be told which dataset to load, and it reads whatever sits at the one artifact address."
ok "$JOB takes the dataset name as a parameter"

grep -qE "ARTIFACT_BLOB_URL', value: '\\\$\{loadBlobUrl\}artifact/\\\$\{artifactProfile\}'" "$JOB" \
  || fail "$JOB must address the artifact per profile (artifact/\${artifactProfile}). Pointing the job at the container itself makes every dataset share one address, and two of them mix."
ok "$JOB addresses the artifact per profile"

grep -q 'artifactProfile="\$PROFILE"' "$SCRIPT" \
  || fail "$SCRIPT must pass the profile through to the job deployment, or the flag stops at the upload and the job loads the wrong dataset."
ok "$SCRIPT passes the profile through to the job"

# copy adds and overwrites; only sync makes the destination MATCH the source. A regeneration that emits
# fewer chunks than the last one leaves the surplus behind under copy, and the surplus is indistinguishable
# from the dataset it is polluting.
if grep -qE 'azcopy copy "\$\{ARTIFACT_DIR\}' "$SCRIPT"; then
  fail "$SCRIPT uploads with 'azcopy copy', which never deletes. Use 'azcopy sync --delete-destination=true' so the staged prefix ends up as the artifact instead of absorbing it."
fi
grep -q 'delete-destination=true' "$SCRIPT" \
  || fail "$SCRIPT must upload with --delete-destination=true, or a stale chunk from an earlier dataset survives the upload that was meant to replace it."
ok "$SCRIPT uploads with sync semantics, not copy"

# Generation is deterministic, so a regenerated chunk carries no meaningful mtime and a byte-different file
# can be older than the blob it must replace. --overwrite=ifSourceNewer skipped exactly that file.
if grep -v '^[[:space:]]*#' "$SCRIPT" | grep -q 'overwrite=ifSourceNewer'; then
  fail "$SCRIPT compares modification times to decide what to upload. Artifact generation is deterministic, so a regenerated chunk can be older than the blob it replaces and get skipped without a word. Compare content."
fi
ok "$SCRIPT does not decide what to upload by timestamp"

# Assert the outcome, not the gate: the local artifact being valid says nothing about the bytes the job
# will open, and the job announcing the mismatch costs a 3.4 GB stage and an image build first.
grep -q 'The staged copy is not this artifact' "$SCRIPT" \
  || fail "$SCRIPT must check the STAGED artifact, not only the local one. The local directory is a different set of bytes from the copy the job reads."
ok "$SCRIPT checks the staged copy, not just the local directory"

grep -q "this run is about the '\$PROFILE' dataset" "$SCRIPT" \
  || fail "$SCRIPT must verify the deployed job reads this run's profile before starting it. The image assertion proves which code runs, not which data it opens."
ok "$SCRIPT verifies the deployed job reads this run's dataset"

echo "== the operator says which dataset, and the script builds only what is missing =="

# Discovery is gone on purpose. Looking in /tmp/gen-<profile> and in a cmms-data checkout meant two copies
# of one profile could both exist with different manifests, and the script's best move at that point was to
# stop and ask. A path the operator supplies has no ambiguity to resolve.
grep -q 'artifact-dir=<absolute path> is required' "$SCRIPT" \
  || fail "$SCRIPT must require --artifact-dir. A load of this size should not open by guessing which directory was meant."
ok "$SCRIPT requires the dataset location"

if grep -vE '^[[:space:]]*#' "$SCRIPT" | grep -qE '/tmp/gen-|CMMS_DATA_DIR|\$CMMS_DATA'; then
  fail "$SCRIPT still searches for a dataset (a /tmp/gen path or a cmms-data checkout). The location is an input now; searching for it is what produced two candidates and a stalled run."
fi
ok "$SCRIPT does not search for a dataset"

# Relative paths resolve against the caller's working directory, so the same command names different bytes
# from different shells.
grep -q 'must be an ABSOLUTE path' "$SCRIPT" \
  || fail "$SCRIPT must reject a relative --artifact-dir; the same command would otherwise mean different datasets from different directories."
ok "$SCRIPT requires an absolute path"

# The tag is HEAD's short sha and the dirty-tree preflight refuses to run while any file the Dockerfile
# COPYs is uncommitted, so a tag already in the registry was built from this source. Two remote builds at
# roughly five minutes each ran on every repeat invocation and changed nothing about what got deployed.
grep -q 'build_if_absent' "$SCRIPT" \
  || fail "$SCRIPT must skip a build when the registry already holds this commit's tag."
# One build call in the whole script, the one inside the helper. A second call naming a repo directly is a
# build that bypasses the check, which is how the unconditional rebuild would come back.
if grep -vE '^[[:space:]]*#' "$SCRIPT" | grep -qE 'az acr build .*--image "cmms-'; then
  fail "$SCRIPT calls 'az acr build' with a hardcoded image name, bypassing build_if_absent. Route it through the helper so a repeat run of the same commit does not rebuild an identical image."
fi
BUILD_CALLS=$(grep -vE '^[[:space:]]*#' "$SCRIPT" | grep -cE 'az acr build')
[ "$BUILD_CALLS" = "1" ] \
  || fail "$SCRIPT has $BUILD_CALLS 'az acr build' calls; expected exactly one, inside build_if_absent."
ok "$SCRIPT builds an image only when the registry lacks it"

# The saving must not reach the assertion. What gets deployed is still decided by a digest read back from
# the registry, and the job is still checked against it before anything starts.
grep -q 'DIGEST=$(image_digest cmms-load)' "$SCRIPT" \
  || fail "$SCRIPT must still resolve the load image's digest from the registry. Skipping the build must never mean deploying something nobody read back."
ok "$SCRIPT still deploys by a digest read back from the registry"

echo "== a load can be started and walked away from (lnicoara/cmms-data#14) =="

# The build context is an ALLOWLIST. `az acr build` builds remotely, so anything not excluded is uploaded
# before the build starts, and without this file that was the entire working tree: 4.2 GB to deliver the
# ~3.5 MB the Dockerfile copies. A denylist is correct the day it is written and silently wrong after it,
# because the next large directory somebody adds is uploaded again and a slow upload looks exactly like a
# slow connection.
[ -f .dockerignore ] || fail ".dockerignore is missing. Without it 'az acr build' uploads the whole working tree, including the multi-gigabyte data/ directory the image never copies."
grep -qE '^\*[[:space:]]*$' .dockerignore \
  || fail ".dockerignore must start from '*' and re-include what the Dockerfile COPYs. A list of exclusions regrows the moment a new directory is added, which is the failure it exists to prevent."
ok ".dockerignore excludes everything by default"

# Every path the Dockerfile COPYs has to be re-included, or the build fails on a missing file. Derived from
# the Dockerfile rather than hardcoded here, so a new COPY is caught by this test instead of by an operator
# waiting on a build.
while IFS= read -r p; do
  grep -qE "^!${p%%/*}$" .dockerignore \
    || fail ".dockerignore does not re-include '$p', which $DOCKERFILE COPYs. The image build would fail on a missing file."
done < <(grep -E '^COPY ' "$DOCKERFILE" | awk '{print $2}' | grep -vE '^--from' | grep -vE '^\./?$')
ok ".dockerignore re-includes every path the Dockerfile copies"

# data/ is the whole point: gitignored, copied by no layer, and the same artifact the job downloads from
# blob storage inside Azure minutes later.
if grep -qE '^!data' .dockerignore; then
  fail ".dockerignore re-includes data/. That is the 3.8 GB the operator was waiting on; the job reads the artifact from blob storage, not from the image."
fi
ok ".dockerignore keeps the artifact out of the build context"

# --start-only must REFUSE on a missing premise rather than fall back. A fallback would make the flag mean
# "usually fast", and the one run that silently built would be the run on the connection that could not
# afford it.
grep -q '\-\-start-only) START_ONLY=true' "$SCRIPT" \
  || fail "$SCRIPT must accept --start-only, so a load can be started without the uploads that put its inputs in Azure."
grep -q 'start-only was passed, but \$ACR holds no cmms-load' "$SCRIPT" \
  || fail "$SCRIPT must REFUSE --start-only when the registry lacks the tag. Falling back to a build defeats the flag on exactly the connection that needed it."
grep -q 'start-only was passed, but artifact/\${PROFILE}/ holds no blobs' "$SCRIPT" \
  || fail "$SCRIPT must REFUSE --start-only when nothing is staged under the profile's prefix. An empty prefix matches an empty local directory at zero and would otherwise pass."
ok "$SCRIPT refuses --start-only rather than falling back to a build or an empty prefix"

# The staged-versus-local comparison must not fire when there is no local copy. An absent directory counts
# as zero files, which differs from a staged 587, so the run died claiming the staged copy was not this
# artifact and advising a re-run without SKIP_UPLOAD=1. That is the wrong fix for a state that is not
# wrong: --start-only exists so a load can be started from inputs already in Azure, and keeping 3.8 GB on
# the laptop is not a precondition for that.
grep -q 'if \[ -d "\$ARTIFACT_DIR" \] && \[ "\$STAGED_FILES" != "?" \]' "$SCRIPT" \
  || fail "$SCRIPT compares staged blobs against a local artifact directory without first checking the directory exists. With --start-only there may be no local copy, and a missing one counts as zero files, which refuses the run for the state it was designed to support."
ok "$SCRIPT compares against the local artifact only when there is one"

# The fast path must not be the path with fewer checks. These three assertions run on every route through
# the script, and --start-only skips preparation rather than verification.
for guard in 'Refusing to start it: a stale image would run code you are not looking at' \
             'Refusing to start it: it would load whatever is at that address' \
             'This script does not delete data and will not start a job that does'; do
  grep -qF "$guard" "$SCRIPT" \
    || fail "$SCRIPT lost the assertion: $guard. --start-only skips work that is already done, never a check."
done
ok "$SCRIPT keeps the image, profile and delete assertions on every path"

echo
echo "PASS: load delivery conformance"
