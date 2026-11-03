#!/bin/sh
# Live two-player smoke over Photon Cloud: a host and a client on this
# machine, each with its own profile. Needs the App ID (Tools/photon_app_id.sh
# before the build, or BLITZ_PHOTON_APP_ID) and a built Linux player.
#
# usage: Tools/net_smoke.sh [seconds] [extra client args...]
# env:   NET_MODE=online   both players go through the hangar's Online mode
#        HOST_ARGS="..."   extra host args (e.g. -blitzNetQuitAfter 50)
#        KILL_HOST_AT=N    kill -9 the host N seconds after it started
#        SECOND_CLIENT=1   a third player (log client2.log), 6 s after the first
#        CLIENT2_ARGS="..." extra args of that third player
#        BLITZ_NET_BIN=path another player binary (e.g. Build/Linux-productiondebug)
set -e
PROJ="$(cd "$(dirname "$0")/.." && pwd)"
SECS="${1:-90}"
[ $# -gt 0 ] && shift
ROOM="smoke-$(date +%s)"
BIN="${BLITZ_NET_BIN:-$PROJ/Build/Linux/BlitzLegacy}"
OUT="$PROJ/Logs/net-smoke"
rm -rf /tmp/blitznet
mkdir -p "$OUT" /tmp/blitznet/host /tmp/blitznet/client /tmp/blitznet/client2
rm -f "$OUT/client2.log"
export BLITZ_SKIP_SHADER_WARMUP=1 BLITZ_NET_SMOKE=1
if [ "$NET_MODE" = "online" ]; then
  HOST_MODE="-blitzNetOnline -blitzNetLobby 60"
  CLIENT_MODE="-blitzNetOnline -blitzNetLobby 60"
else
  HOST_MODE="-blitzNetHost $ROOM -blitzNetGuests 1 -blitzNetLobby 60"
  CLIENT_MODE="-blitzNetJoin $ROOM"
fi
# shellcheck disable=SC2086
XDG_CONFIG_HOME=/tmp/blitznet/host timeout $((SECS + 30)) "$BIN" -batchmode -nographics \
  -logFile "$OUT/host.log" -blitzNetSmoke smokehost $HOST_MODE \
  -blitzNetQuitAfter "$SECS" $HOST_ARGS &
HOST=$!
if [ -n "$KILL_HOST_AT" ]; then
  (sleep "$KILL_HOST_AT"; kill -9 "$HOST" 2>/dev/null && echo "killed host at ${KILL_HOST_AT}s") &
fi
sleep 12
# shellcheck disable=SC2086
XDG_CONFIG_HOME=/tmp/blitznet/client timeout $((SECS + 30)) "$BIN" -batchmode -nographics \
  -logFile "$OUT/client.log" -blitzNetSmoke smokeclient $CLIENT_MODE \
  -blitzNetAutoDrive -blitzNetQuitAfter "$SECS" "$@" &
CLIENT=$!
CLIENT2=
if [ -n "$SECOND_CLIENT" ]; then
  sleep 6
  # shellcheck disable=SC2086
  XDG_CONFIG_HOME=/tmp/blitznet/client2 timeout $((SECS + 30)) "$BIN" -batchmode -nographics \
    -logFile "$OUT/client2.log" -blitzNetSmoke smokeclient2 $CLIENT_MODE \
    -blitzNetAutoDrive -blitzNetQuitAfter "$SECS" $CLIENT2_ARGS &
  CLIENT2=$!
fi
wait $HOST || true
wait $CLIENT || true
[ -n "$CLIENT2" ] && { wait $CLIENT2 || true; }
grep -h 'BlitzNet' "$OUT/host.log" | sed 's/^/host: /' | tail -n 15
grep -h 'BlitzNet' "$OUT/client.log" | sed 's/^/client: /' | tail -n 15
[ -f "$OUT/client2.log" ] && grep -h 'BlitzNet' "$OUT/client2.log" | sed 's/^/client2: /' | tail -n 15
exit 0
