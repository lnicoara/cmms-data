# 04-no-active-operation.sh — refuse to start if a deploy is already in flight on this env's API, so the
# script stops with a clear message instead of failing the rollout with ContainerAppOperationInProgress (a
# CI deploy or an earlier run still settling). Sourced by the env's deploy script; uses its die()/ok()/warn()
# helpers and RG/APP/SUBSCRIPTION. Reads via the core CLI (az resource), not `az containerapp`, which needs
# the extension and a selected subscription. lnicoara/cmms#838.
_state="$(az resource show -g "$RG" -n "$APP" --resource-type Microsoft.App/containerApps \
  --subscription "$SUBSCRIPTION" --query 'properties.provisioningState' -o tsv 2>/dev/null)" \
  || die "could not read container app '$APP' in '$RG'. Is $RG stood up, and are you signed in?"
case "$_state" in
  Succeeded) ok "no deploy in progress on $APP" ;;
  Failed|Canceled) warn "the last operation on $APP ended '$_state'; not in progress, continuing." ;;
  *) die "an Azure operation is already in progress on '$APP' (state: $_state). Wait for it to finish, then re-run." ;;
esac
