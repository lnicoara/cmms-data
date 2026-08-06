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
#
# Exit 1 = a real assertion failure.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

SCRIPT=scripts/pre-prod/load-tenant.sh
JOB=infra/load-job.bicep
STORE=infra/modules/load-test-store.bicep
ENTRY=tools/Cmms.LoadDataRunner/docker-entrypoint.sh
RUNNER=tools/Cmms.LoadDataRunner/Program.cs

fail() { echo "FAIL: $*" >&2; exit 1; }
ok()   { echo "  ok: $*"; }

for f in "$SCRIPT" "$JOB" "$STORE" "$ENTRY" "$RUNNER"; do
  [ -f "$f" ] || fail "$f is missing. The delivery chain is incomplete."
done

echo "== plan-only by default =="

# The operator script. A load has to be asked for at every layer, and this is the outermost one.
grep -q '^EXECUTE=false' "$SCRIPT" \
  || fail "$SCRIPT must default EXECUTE=false, so running it with no arguments plans rather than loads."
ok "$SCRIPT defaults to plan"

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

# demo-health is only a valid target BECAUSE its write set gets cleared. Loading into it with clearing off
# would fail on a primary key collision after doing real work, so the script refuses that combination up
# front and says why.
grep -q 'demo-health' "$SCRIPT" \
  || fail "$SCRIPT must name demo-health explicitly, either as the target or in its guard."
ok "$SCRIPT names demo-health explicitly"

# The fence is only real if the OPERATOR PATH keeps it. A CLEAR_TARGET=true default would collapse two
# flags into one, because --execute alone would then delete, for whatever slug TENANT_SLUG happened to hold.
grep -q '^CLEAR_TARGET=false' "$SCRIPT" \
  || fail "$SCRIPT must default CLEAR_TARGET=false. A true default collapses the loader's two-flag delete fence into one."
ok "$SCRIPT does not collapse the delete fence"

# Typing the slug is the second yes, and it is what binds a wipe to ONE tenant. Without it, a stale
# TENANT_SLUG plus a habitual --execute empties whatever it happens to point at.
grep -q 'CLEAR_CONFIRM' "$SCRIPT" \
  || fail "$SCRIPT must require the operator to name the tenant being cleared, so a wrong TENANT_SLUG cannot empty it."
grep -qE 'CLEAR_CONFIRM.*!=.*TENANT_SLUG' "$SCRIPT" \
  || fail "$SCRIPT must check the named tenant matches the target tenant."
ok "$SCRIPT binds a wipe to a tenant the operator named"

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
grep -q 'git status --porcelain -- Directory.Build.props global.json src tools' "$SCRIPT" \
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

# The closing NOTE is an unquoted heredoc, so anything shaped like $VAR in the ADVICE it prints gets
# expanded at print time. Under `set -u` an unset one aborts the script AFTER the job has started, which
# reads as a failed run while the load is actually under way. Behavioural, not a grep: the heredoc is
# extracted and rendered with the same strict flags the script runs under.
NOTE_BODY=$(sed -n '/^cat <<NOTE$/,/^NOTE$/p' "$SCRIPT" | sed '1d;$d')
if ! printf '%s\n' "$NOTE_BODY" | (set -euo pipefail; RG=rg EXECUTE=false STORE=st; cat <<EOF_RENDER >/dev/null
$(cat)
EOF_RENDER
) 2>/dev/null; then
  fail "$SCRIPT's closing NOTE dies under set -u; escape any \$VAR in the advice it prints (use \\\$VAR)."
fi
ok "the closing NOTE renders under set -u"

echo "== the job is sized for a load measured in hours =="

# The seed job allows 1800s. This one moves 42.7M rows into a General Purpose database whose log
# throughput caps around 4.5 MB/s, so a 30-minute timeout would guarantee a killed replica every run.
grep -q 'replicaTimeout: timeoutHours \* 3600' "$JOB" \
  || fail "$JOB must express replicaTimeout in hours; the seed job's 1800s is far too short for this load."
ok "$JOB expresses its timeout in hours"

echo
echo "PASS: load delivery conformance"
