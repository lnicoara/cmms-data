# 03-containerapp-extension.sh — ensure the 'containerapp' az CLI extension is present (`az containerapp`
# is an extension, not built in), so the deploy never stalls on an install prompt. Sourced by the env's
# deploy script; uses its run()/ok() helpers. lnicoara/cmms#838.
if az extension show --name containerapp >/dev/null 2>&1; then
  ok "containerapp CLI extension present"
else
  run "installing the containerapp CLI extension" az extension add --name containerapp --only-show-errors
fi
