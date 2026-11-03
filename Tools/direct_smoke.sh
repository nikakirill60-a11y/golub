#!/bin/sh
# Local smoke of the own dedicated server: one headless server and two
# headless players on this machine, each with its own profile, all from the
# built Linux player (Tools/unity.sh linux).
#
# usage: Tools/direct_smoke.sh [seconds] [extra client args...]
# env:   BLITZ_NET_BIN=path   another player binary
#        DIRECT_PORT=N        UDP port (default 7787, away from a real server)
#        DIRECT_MODE=uprising battle mode of both players (bossmode for Big Boss)
#        DIRECT_TANK=id       tank of both players (Big Boss needs tier V-X, e.g. t-34)
#        DIRECT_LOBBY=N       server lobby seconds (default 20)
#        DIRECT_REMOTE=h:p    play against a deployed server instead of a local one
set -e
PROJ="$(cd "$(dirname "$0")/.." && pwd)"
SECS="${1:-120}"
[ $# -gt 0 ] && shift
BIN="${BLITZ_NET_BIN:-$PROJ/Build/Linux/BlitzLegacy}"
PORT="${DIRECT_PORT:-7787}"
LOBBY="${DIRECT_LOBBY:-20}"
MODE_ARGS=
if [ -n "$DIRECT_MODE" ]; then MODE_ARGS="-blitzNetMode $DIRECT_MODE"; fi
if [ -n "$DIRECT_TANK" ]; then MODE_ARGS="$MODE_ARGS -blitzNetTank $DIRECT_TANK"; fi
OUT="$PROJ/Logs/direct-smoke"
rm -rf /tmp/blitzdirect
mkdir -p "$OUT" /tmp/blitzdirect/server /tmp/blitzdirect/a /tmp/blitzdirect/b
export BLITZ_SKIP_SHADER_WARMUP=1
ADDR="${DIRECT_REMOTE:-127.0.0.1:$PORT}"
SERVER=
if [ -z "$DIRECT_REMOTE" ]; then
  XDG_CONFIG_HOME=/tmp/blitzdirect/server timeout $((SECS + 40)) "$BIN" -batchmode -nographics \
    -logFile "$OUT/server.log" -blitzServer -blitzServerPort "$PORT" -blitzServerLobby "$LOBBY" &
  SERVER=$!
  sleep 8
fi
# shellcheck disable=SC2086
BLITZ_NET_SMOKE=1 XDG_CONFIG_HOME=/tmp/blitzdirect/a timeout $((SECS + 30)) "$BIN" -batchmode -nographics \
  -logFile "$OUT/a.log" -blitzDirect -blitzServerAddress "$ADDR" -blitzNetSmoke directa \
  -blitzNetAutoDrive -blitzNetQuitAfter "$SECS" $MODE_ARGS "$@" &
A=$!
sleep 4
# shellcheck disable=SC2086
BLITZ_NET_SMOKE=1 XDG_CONFIG_HOME=/tmp/blitzdirect/b timeout $((SECS + 30)) "$BIN" -batchmode -nographics \
  -logFile "$OUT/b.log" -blitzDirect -blitzServerAddress "$ADDR" -blitzNetSmoke directb \
  -blitzNetQuitAfter "$SECS" $MODE_ARGS &
B=$!
wait $A || true
wait $B || true
if [ -n "$SERVER" ]; then
  kill $SERVER 2>/dev/null || true
  wait $SERVER 2>/dev/null || true
  grep -h 'BlitzDirect' "$OUT/server.log" | sed 's/^/server: /' | tail -n 25
fi
grep -h 'BlitzDirect' "$OUT/a.log" | sed 's/^/a: /' | tail -n 12
grep -h 'BlitzDirect' "$OUT/b.log" | sed 's/^/b: /' | tail -n 12
exit 0
