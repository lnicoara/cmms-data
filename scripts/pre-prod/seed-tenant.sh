#!/usr/bin/env bash
# Seed a provisioned pre-prod tenant with demo data via the in-VNet seed job (lnicoara/cmms#2435, #2442).
#
# Runs steps 3-4 of the #2435 runbook: build+push the cmms-seed image (cloud build, no local Docker),
# deploy infra/seed-job.bicep, then start caj-cmms-seed and report the execution. Step 2 (provision the
# tenant via POST /api/admin/organizations as a pre-prod platform admin) is a PREREQUISITE and is checked
# below, not performed here, because provisioning is the audited admin path, not a script.
#
# Run from the repo root, logged in to the pre-prod subscription (az login + az account set).
#   scripts/pre-prod/seed-tenant.sh                         # tenant demo-health, tag = short git sha
#   TENANT_SLUG=demo-health IMAGE_TAG=manual scripts/pre-prod/seed-tenant.sh
set -euo pipefail

# The fixed pre-prod subscription, same id deploy-pre-prod.sh pins. The script SELECTS it below rather than
# trusting the active az context, so it can never build/deploy/run against another subscription that happens
# to also have an rg-cmms-preprod. lnicoara/cmms#2435
SUBSCRIPTION="${SUBSCRIPTION:-08fd6214-7b3c-4b5e-a59b-320707791129}"
RG="${RG:-rg-cmms-preprod}"
TENANT_SLUG="${TENANT_SLUG:-demo-health}"
IMAGE_TAG="${IMAGE_TAG:-$(git rev-parse --short HEAD 2>/dev/null || echo manual)}"

die() { echo "ERROR: $*" >&2; exit 1; }
step() { echo; echo "==> $*"; }

command -v az >/dev/null || die "az CLI not found."
[ -f infra/seed-job.bicep ] || die "run from the repo root (infra/seed-job.bicep not found)."
az account show >/dev/null 2>&1 || die "not logged in: run 'az login' first."

step "Selecting the pre-prod subscription ($SUBSCRIPTION)"
az account set --subscription "$SUBSCRIPTION" || die "cannot select subscription '$SUBSCRIPTION' (are you logged in to it?)."
az group show -n "$RG" >/dev/null 2>&1 || die "resource group '$RG' not found in subscription '$SUBSCRIPTION'."

step "Discovering pre-prod resources in $RG"
ACR=$(az acr list -g "$RG" --query "[0].name" -o tsv)
SQL=$(az sql server list -g "$RG" --query "[0].name" -o tsv)
[ -n "$ACR" ] || die "no ACR in $RG."
[ -n "$SQL" ] || die "no SQL server in $RG."
echo "    ACR=$ACR  SQL=$SQL  tenant=$TENANT_SLUG  tag=$IMAGE_TAG"

step "Preflight: tenant '$TENANT_SLUG' database must already be provisioned (runbook step 2)"
# Provision it first as a pre-prod platform admin:
#   POST https://<pre-prod-app>/api/admin/organizations  {"name":"Demo Health System","slug":"demo-health"}
if ! az sql db show -g "$RG" --server "$SQL" -n "cmms-tenant-${TENANT_SLUG}" >/dev/null 2>&1; then
  die "database cmms-tenant-${TENANT_SLUG} does not exist. Provision the tenant first (runbook step 2), then re-run."
fi
SKU=$(az sql db show -g "$RG" --server "$SQL" -n "cmms-tenant-${TENANT_SLUG}" --query "sku.name" -o tsv)
echo "    cmms-tenant-${TENANT_SLUG} present, sku=$SKU"
[ "$SKU" = "GP_Gen5" ] || echo "    WARNING: expected provisioned GP_Gen5, got '$SKU' (seed still works; check #2433 wiring)."

step "Building and pushing the seed image ($ACR/cmms-seed:$IMAGE_TAG) via az acr build"
az acr build --registry "$ACR" --image "cmms-seed:${IMAGE_TAG}" \
  --file tools/Cmms.TenantSeedRunner/Dockerfile .

step "Deploying the seed job (infra/seed-job.bicep) targeting '$TENANT_SLUG'"
az deployment group create -g "$RG" -n "deploy-preprod-seed-${IMAGE_TAG}" -f infra/seed-job.bicep \
  -p environment=preprod containerImage="${ACR}.azurecr.io/cmms-seed:${IMAGE_TAG}" tenantSlug="$TENANT_SLUG"

step "Starting caj-cmms-seed"
az containerapp job start -n caj-cmms-seed -g "$RG" >/dev/null
echo "    started. Recent executions:"
az containerapp job execution list -n caj-cmms-seed -g "$RG" \
  --query "[0].{name:name, status:properties.status, start:properties.startTime}" -o table || true

cat <<NOTE

Done kicking off the seed. To watch it finish and read the runner output:
  az containerapp job execution list -n caj-cmms-seed -g $RG -o table
  az containerapp logs show -n caj-cmms-seed -g $RG --container seed --type console --follow

Verify afterward:
  az sql db show -g $RG --server $SQL -n cmms-tenant-$TENANT_SLUG --query sku
  # then promote a staging build into pre-prod and confirm it serves the "Demo Health System" tenant.
NOTE
