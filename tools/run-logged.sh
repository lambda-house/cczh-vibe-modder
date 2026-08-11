#!/usr/bin/env bash
# run-logged.sh — launch Zero Hour with EVERYTHING the engine says captured to a file.
#
# WHY THIS EXISTS
#   The shipped GeneralsXZH binary already logs a great deal — [INI], [CSF], [SUBSYS],
#   [Skirmish], [GeneralsX], [SHUTDOWN] — but it writes to stdout, and run.sh execs the
#   binary without redirecting, so all of it is lost the moment you launch from Finder or
#   from a terminal you then close.
#
#   It is NOT the case that this build has no logging. EA's DEBUG_LOG machinery (the one
#   that writes DebugLogFile.txt) is compiled out — `strings` on the binary finds
#   ReleaseCrashInfo.txt but no DebugLogFile — and looking for that file and not finding it
#   is what makes it look as though nothing is logged. GeneralsX's own logging is separate
#   and is always on.
#
#   Rebuilding with -DRTS_DEBUG_LOGGING=ON would add EA's layer on top, but on this machine
#   that build stops in DXVK for want of glslangValidator, and it is not needed for anything
#   we have wanted to see so far.
#
# USAGE
#   ./run-logged.sh [-win] [...]        log -> logs/run-<timestamp>.log, symlinked as latest
#
# The log is line-buffered through `script` so a crashed or killed run still leaves a
# complete file — plain redirection loses the last block when the process dies.

set -uo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
LOGDIR="$DIR/logs"
mkdir -p "$LOGDIR"
STAMP="$(date +%Y%m%d-%H%M%S)"
LOG="$LOGDIR/run-$STAMP.log"
ln -sfn "$LOG" "$LOGDIR/latest.log"

echo "logging to $LOG"

# A PTY, not plain redirection: stdout to a file is block-buffered, so a run that crashes or
# is killed loses its last 4KB — which is exactly the part you wanted. BSD `script` would do
# this but calls tcgetattr on ITS stdin, so it fails outright when launched from anything
# without a terminal (a CI step, an agent, a background job). pty.spawn has no such
# requirement and works either way.
LOG="$LOG" python3 - "$DIR/run.sh" "$@" <<'PY'
import os, pty, sys
log = open(os.environ["LOG"], "wb", buffering=0)
def read(fd):
    data = os.read(fd, 4096)
    log.write(data)
    return data
sys.exit(os.waitstatus_to_exitcode(pty.spawn(sys.argv[1:], read)))
PY
status=$?

echo "exit=$status  log=$LOG  ($(wc -l < "$LOG" | tr -d ' ') lines)"
exit $status
