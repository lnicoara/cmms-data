#!/usr/bin/env bash
# Container entrypoint for the load-data runner (lnicoara/cmms#2917).
#
# Three steps, in this order, and the middle one is the point:
#
#   1. STAGE   azcopy the artifact from blob into the replica, by managed identity.
#   2. VERIFY  run the loader in its default PLAN mode. This is not a formality. Planning opens every
#              chunk file, reads each one's gzip trailer, checks chunk numbering runs 1..N with no gap,
#              and cross-checks the derived row total against manifest.totalRows. A download that lost,
#              truncated, or corrupted a file fails HERE, before a single row is written.
#   3. LOAD    only if LOAD_EXECUTE=true, re-run with --execute.
#
# Splitting stage from load is deliberate. Cmms.LoadDataRunner is a pure data mover that takes a
# filesystem path and knows nothing about blob, and #2908 spent real effort keeping it that way.
set -euo pipefail

# Bicep's string(true) emits "True", not "true", and comparing case-sensitively against lowercase meant
# LOAD_EXECUTE=True read as false: the job planned, exited 0, and looked exactly like a successful load in
# `az containerapp job execution list`. Every boolean-ish env var crossing the Bicep boundary is normalised
# here rather than compared ad hoc, because the next one would have made the same mistake.
truthy() {
  case "$(printf '%s' "${1:-}" | tr '[:upper:]' '[:lower:]')" in
    true|1|yes) return 0 ;;
    *) return 1 ;;
  esac
}

ARTIFACT_DIR="${ARTIFACT_DIR:-/artifact}"
: "${ARTIFACT_BLOB_URL:?ARTIFACT_BLOB_URL is required (the artifact container URL to stage from)}"
: "${LOAD_TENANT_SLUG:?LOAD_TENANT_SLUG is required (the tenant to load)}"

echo "==> Staging artifact from ${ARTIFACT_BLOB_URL} into ${ARTIFACT_DIR}"

# Managed identity, no account key. AZURE_CLIENT_ID selects the user-assigned identity, the same one the
# loader authenticates to SQL and Key Vault with.
export AZCOPY_AUTO_LOGIN_TYPE=MSI
export AZCOPY_MSI_CLIENT_ID="${AZURE_CLIENT_ID:-}"
# Keep azcopy's plan and log files on the writable scratch path; the image runs as a non-root user whose
# home is not writable.
export AZCOPY_JOB_PLAN_LOCATION="${ARTIFACT_DIR}/.azcopy-plans"
export AZCOPY_LOG_LOCATION="${ARTIFACT_DIR}/.azcopy-logs"
mkdir -p "$AZCOPY_JOB_PLAN_LOCATION" "$AZCOPY_LOG_LOCATION"

# Capacity check before pulling gigabytes into a replica whose disk is sized by its CPU and memory
# request. Failing here with a clear number beats failing mid-copy with ENOSPC.
avail_mb=$(df -Pm "$ARTIFACT_DIR" | awk 'NR==2 {print $4}')
required_mb="${ARTIFACT_REQUIRED_MB:-6144}"
if [ "$avail_mb" -lt "$required_mb" ]; then
  echo "ERROR: ${avail_mb} MB free at ${ARTIFACT_DIR}, need at least ${required_mb} MB to stage the artifact." >&2
  echo "       Raise the job's cpu/memory (ephemeral disk scales with them) or lower ARTIFACT_REQUIRED_MB." >&2
  exit 1
fi
echo "    ${avail_mb} MB free, need ${required_mb} MB"

# --recursive mirrors the <table>/<table>-NNNNN.jsonl.gz layout. --overwrite=ifSourceNewer makes a re-run
# after a restart cheap instead of re-pulling 3.7 GB, which matters because the loader is resumable and a
# retried job should not spend its first many minutes re-downloading what it already has.
# Skipped entirely for a clear-only job: there is no artifact to stage, and pulling one would be minutes
# spent fetching data the run will never open. lnicoara/cmms#3055.
if ! truthy "${LOAD_CLEAR_ONLY:-false}"; then
  azcopy copy "${ARTIFACT_BLOB_URL}/*" "${ARTIFACT_DIR}/" --recursive --overwrite=ifSourceNewer
else
  echo "clear-only: no artifact staged"
fi

# The chosen pre-prod target is the already-seeded demo-health tenant, whose ServiceLines rows carry the
# same well-known Guids the artifact ships, so the write set has to be emptied first. Destructive, needs
# its own flag on top of --execute, and it is the loader's guard set (right tenant, right database, right
# environment, migrations current) that stands between this and the wrong database.
# Carries the SLUG, not a boolean. The runner refuses unless it matches the tenant it is targeting, so a
# job env edit that changes the tenant but forgets this cannot empty the new one.
CLEAR_ARG=""
if [ -n "${LOAD_CLEAR_TARGET_SLUG:-}" ]; then
  CLEAR_ARG="--clear-target=${LOAD_CLEAR_TARGET_SLUG}"
fi

# CLEAR ONLY: empty the tenant and stop. lnicoara/cmms#3055.
#
# Handled here, before the artifact is even looked at, because that is the whole point: emptying a tenant
# must not require staging gigabytes of a dataset nobody wants. The download above is skipped for the same
# reason, so this path touches no blob at all.
#
# It still carries both yeses. LOAD_CLEAR_TARGET_SLUG names the tenant and LOAD_EXECUTE has to be true;
# the runner refuses otherwise, and refuses again if the slug does not match the tenant it resolved.
if truthy "${LOAD_CLEAR_ONLY:-false}"; then
  echo
  if ! truthy "${LOAD_EXECUTE:-false}"; then
    echo "==> Planning the clear of '${LOAD_TENANT_SLUG}' (deletes nothing)"
    exec dotnet Cmms.LoadDataRunner.dll --tenant="${LOAD_TENANT_SLUG}" ${CLEAR_ARG} --clear-only
  fi
  echo "==> CLEARING '${LOAD_TENANT_SLUG}' (LOAD_EXECUTE=${LOAD_EXECUTE}). Loading nothing."
  exec dotnet Cmms.LoadDataRunner.dll --tenant="${LOAD_TENANT_SLUG}" ${CLEAR_ARG} --clear-only --execute
fi

echo
echo "==> Verifying the staged artifact (no database contacted)"
# ARTIFACT verification, not target preflight. Those are different questions and fusing them was a bug:
# a plan run also preflights the target, and a seeded target legitimately fails preflight, so under set -e
# the run died before the clear that was about to make it loadable. This asks only whether the 3.7 GB that
# just landed is intact, and it asks BEFORE anything destructive happens.
dotnet Cmms.LoadDataRunner.dll --artifact="${ARTIFACT_DIR}" --tenant="${LOAD_TENANT_SLUG}" --verify-artifact

if ! truthy "${LOAD_EXECUTE:-false}"; then
  echo
  echo "==> Planning against the target (writes nothing)"
  # Allowed to fail: on a seeded target this legitimately refuses, and reporting that IS the plan's answer.
  dotnet Cmms.LoadDataRunner.dll --artifact="${ARTIFACT_DIR}" --tenant="${LOAD_TENANT_SLUG}" ${CLEAR_ARG} || true
fi

if ! truthy "${LOAD_EXECUTE:-false}"; then
  echo
  echo "LOAD_EXECUTE is '${LOAD_EXECUTE:-}', which is not true. Nothing written, nothing deleted."
  exit 0
fi

echo
echo "==> Loading (LOAD_EXECUTE=${LOAD_EXECUTE})"
exec dotnet Cmms.LoadDataRunner.dll --artifact="${ARTIFACT_DIR}" --tenant="${LOAD_TENANT_SLUG}" ${CLEAR_ARG} --execute
