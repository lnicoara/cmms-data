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
die() { printf '\n%s%sFAILED: %s%s\n' "$C_BOLD" "$C_RED" "$*" "$C_RESET" >&2; exit 1; }
