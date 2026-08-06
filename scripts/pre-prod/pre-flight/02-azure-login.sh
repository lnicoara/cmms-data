# 02-azure-login.sh — fail unless signed in to Azure, and select the target subscription.
# Sourced by the env's deploy script; uses its die()/ok()/azt helpers and SUBSCRIPTION. lnicoara/cmms#838.
az account show >/dev/null 2>&1 || die "not signed in to Azure. Run: az login"
azt account set --subscription "$SUBSCRIPTION" >/dev/null || die "cannot select subscription $SUBSCRIPTION."
ok "signed in to Azure"
