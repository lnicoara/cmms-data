# 01-required-tools.sh — fail unless the tools this promote needs are installed.
# Sourced by the env's deploy script; uses its die()/ok() helpers. lnicoara/cmms#838.
for _t in az curl git mktemp sed; do
  command -v "$_t" >/dev/null 2>&1 || die "'$_t' is required but not installed."
done
ok "required tools present"
