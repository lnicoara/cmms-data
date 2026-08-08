# Terminal output for the pre-prod operator scripts. Sourced, never executed. lnicoara/cmms#3046.
#
# The terminal is a REPORT, not a transcript. What an operator needs from a run measured in minutes or
# hours is which phase it is in, what it decided, and what it ended as. Everything a tool happens to print
# on the way there (azcopy's progress meter, a remote Docker build's layer lines, ARM's deployment JSON,
# Log Analytics rows arriving out of order) buries exactly those three things, and a run that succeeded
# then reads the same as one that failed. Capture that output to a file, name the file in the failure, and
# print the decisions.
#
# ONE definition, sourced by every script here, rather than the same twenty lines pasted into each. These
# scripts are read side by side during an incident, and a phase header that is bold blue in one and plain
# in another is a difference the reader has to spend attention on before discovering it means nothing.
#
# Colour is off unless stdout is a terminal, and honours NO_COLOR. Piping any of these to a file or a CI
# log must produce plain text, not escape sequences.

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
  C_RESET=$'\033[0m'; C_BOLD=$'\033[1m'; C_DIM=$'\033[2m'
  C_BLUE=$'\033[34m'; C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'; C_RED=$'\033[31m'
else
  C_RESET=; C_BOLD=; C_DIM=; C_BLUE=; C_GREEN=; C_YELLOW=; C_RED=
fi

# A phase of the run. One per section, so the transcript reads as a list of what happened.
step() { printf '\n%s%s> %s%s\n' "$C_BOLD" "$C_BLUE" "$1" "$C_RESET"; }
# A fact the phase established: aligned label, then value.
fact() { printf '  %s%-20s%s %s\n' "$C_DIM" "$1" "$C_RESET" "$2"; }
# A phase's outcome.
ok()   { printf '  %sok%s   %s\n' "$C_GREEN" "$C_RESET" "$1"; }
warn() { printf '  %swarn%s %s\n' "$C_YELLOW" "$C_RESET" "$1" >&2; }
note() { printf '  %s%s%s\n' "$C_DIM" "$1" "$C_RESET"; }

# die() lives here with the rest, and every script must source this BEFORE its argument loop. They used to
# define die() after the loop, so a flag with a missing value died with 'die: command not found' instead of
# its own error message.
#
# _OUTPUT_DIED marks that the failure has already been explained, so the backstop below stays quiet rather
# than printing a second, vaguer message underneath a good one.
_OUTPUT_DIED=0
die() { _OUTPUT_DIED=1; printf '\n%s%sFAILED: %s%s\n' "$C_BOLD" "$C_RED" "$*" "$C_RESET" >&2; exit 1; }

# ---- No silent exits (lnicoara/cmms#3048) -----------------------------------------------------------
# A script must never exit without saying why. Not on a die(), not on a `set -e` kill, not on a failed
# pipeline.
#
# This is a BACKSTOP, not a style preference, because the failure it prevents has already happened. Under
# `set -euo pipefail` an assignment carries the exit status of its command substitution, so
#
#   UP_DONE=$(grep -oE '[0-9]+ Done' "$LOG" | tail -1 | cut -d' ' -f1)
#
# kills the script when the grep matches nothing, and prints NOTHING, because a failed grep is silent. A
# completed 209-file upload ended at a bare shell prompt and the operator could not tell it from a crash
# (lnicoara/cmms#3047). Every `VAR=$(pipeline)` here has that shape and there are dozens; auditing them one
# at a time treats instances, and the class needs a floor under it.
#
# ON ERR, NOT ON EXIT, and that is not a stylistic choice. An EXIT trap was written first and had to be
# thrown away: on bash 3.2 (what macOS ships, which is what these run on) merely INSTALLING one changes a
# `set -u` unbound-variable death from exit 1 to exit 0, and the trap reads $? as 0 so it cannot even tell.
# A backstop that masks a failure status is worse than the silence it was added to fix. ERR reports and
# touches nothing.
#
# ERR does not fire for a command whose failure is being handled: a condition, an `if !`, a `|| true`. So
# this speaks only when something failed that nobody was prepared for, which under `set -e` is the moment
# before the script dies. die() exits explicitly and does not trigger it, so a real explanation is never
# followed by this one.
#
# `set -u` deaths are left to bash, which prints "line N: NAME: unbound variable" itself and keeps the
# non-zero status. That is already legible, and it stays that way precisely because nothing traps EXIT.
_output_on_err() {
  local rc=$?
  printf '\n%s%sFAILED: exited %d at line %s, running: %s%s\n' \
    "$C_BOLD" "$C_RED" "$rc" "${BASH_LINENO[0]:-?}" "${BASH_COMMAND:-?}" "$C_RESET" >&2
}
trap _output_on_err ERR
