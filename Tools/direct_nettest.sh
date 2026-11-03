#!/bin/bash
# Network self-test of the deployed dedicated server, run from a player's
# machine with no help: route, raw UDP echo on the game port, then a real
# battle of two headless clients (Tools/direct_smoke.sh). Everything ends up
# in one report file to share afterwards.
#
# usage: Tools/direct_nettest.sh
# env:   DIRECT_HOST=host     DIRECT_PORT=7777
#        DIRECT_SSH_KEY=path  DIRECT_SSH_USER=user
#        DIRECT_BATTLE_SECS=150   client time of the battle check
#        DIRECT_PROBE_PORTS="7777 443" UDP ports of the raw echo (extra ones must be
#                                 open in the Security Group)
#        DIRECT_RAW=0             skip the raw UDP echo (keeps the server up)
#        DIRECT_BATTLE=0          skip the battle check
set -u
PROJ="$(cd "$(dirname "$0")/.." && pwd)"
. "$PROJ/Tools/load_env.sh"
: "${DIRECT_HOST:?Set DIRECT_HOST in .env}"
: "${DIRECT_SSH_KEY:?Set DIRECT_SSH_KEY in .env}"
: "${DIRECT_SSH_USER:?Set DIRECT_SSH_USER in .env}"
HOST="$DIRECT_HOST"
PORT="${DIRECT_PORT:-7777}"
KEY="$DIRECT_SSH_KEY"
TARGET="$DIRECT_SSH_USER@$HOST"
SECS="${DIRECT_BATTLE_SECS:-150}"
PORTS="${DIRECT_PROBE_PORTS:-$PORT 443 5056 51820 27015}"
SSH=(ssh -i "$KEY" -o ConnectTimeout=10 -o BatchMode=yes)
OUT="$PROJ/Logs/direct-nettest"
mkdir -p "$OUT"
REPORT="$OUT/report-$(date +%Y%m%d-%H%M%S).txt"
STARTED="$(date -u '+%Y-%m-%d %H:%M:%S')"
VERDICT=()

say() { echo "$*" | tee -a "$REPORT"; }

server_up() {
  "${SSH[@]}" "$TARGET" 'sudo systemctl start blitz-direct' >/dev/null 2>&1
}

say "== Blitz dedicated server network test, $(date '+%F %T %Z')"
say "server $HOST:$PORT"

say ""
say "== 1. network"
say "public ip: $(curl -s --max-time 8 ipinfo.io 2>/dev/null | tr -d '\n "{}' | sed 's/,readme.*//')"
say "route: $(ip route get "$HOST" 2>/dev/null | head -n 1)"
say "tunnels: $(ip -brief link 2>/dev/null | awk '/wg|tun|awg|proton|vpn/ {print $1}' | tr '\n' ' ')"
if "${SSH[@]}" "$TARGET" true 2>/dev/null; then
  say "ssh (tcp 22): ok"
else
  say "ssh (tcp 22): FAILED - server checks are skipped"
  DIRECT_RAW=0
fi

if [ "${DIRECT_RAW:-1}" != 0 ]; then
  say ""
  say "== 2. raw UDP echo (the game server is stopped meanwhile)"
  say "ports: $PORTS (NONE on an extra port may just mean it is closed in the Security Group)"
  # port 7777 gets every shape, extra ports the three that tell most
  PROBES=()
  for p in $PORTS; do
    if [ "$p" = "$PORT" ]; then
      PROBES+=("$p rand 30 15" "$p zero 30 10" "$p big 30 10" "$p awg 30 10" "$p quic 30 10" "$p rand 1 20")
    else
      PROBES+=("$p rand 30 10" "$p awg 30 10" "$p quic 30 10")
    fi
  done
  TOTAL=10
  for probe in "${PROBES[@]}"; do set -- $probe; TOTAL=$((TOTAL + $4)); done
  trap 'server_up; exit 130' INT TERM
  scp -q -i "$KEY" "$PROJ/Tools/direct_udp_probe.py" "$TARGET:/tmp/direct_udp_probe.py"
  "${SSH[@]}" "$TARGET" 'sudo systemctl stop blitz-direct'
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    "${SSH[@]}" "$TARGET" "sudo ss -ulpn | grep -q ':$PORT '" || break
    sleep 1
  done
  "${SSH[@]}" "$TARGET" "sudo timeout $((TOTAL + 10)) python3 /tmp/direct_udp_probe.py server $(echo $PORTS | tr ' ' ,) $TOTAL" > "$OUT/echo-server.txt" 2>&1 &
  ECHO=$!
  sleep 4
  for probe in "${PROBES[@]}"; do
    set -- $probe
    say "$(python3 "$PROJ/Tools/direct_udp_probe.py" client "$HOST" "$1" "$4" "$3" "$2")"
  done
  kill "$ECHO" 2>/dev/null
  wait "$ECHO" 2>/dev/null
  "${SSH[@]}" "$TARGET" 'sudo pkill -f "direct_udp_probe.py server"' >/dev/null 2>&1
  grep -h 'bind failed' "$OUT/echo-server.txt" | tee -a "$REPORT"
  server_up
  trap - INT TERM
  for p in $PORTS; do
    pass=$(grep -a "udp port=$p " "$REPORT" | grep -aE ' (PASS|LOSSY)$' | awk '{print $3}' | sort -u | tr '\n' ' ')
    cut=$(grep -a "udp port=$p " "$REPORT" | grep -a ' CUT$' | awk '{print $3}' | sort -u | tr '\n' ' ')
    VERDICT+=("raw UDP port $p: pass=[${pass% }] cut=[${cut% }]")
  done
fi

if [ "${DIRECT_BATTLE:-1}" != 0 ]; then
  say ""
  say "== 3. battle of two clients (~$((SECS / 60 + 2)) min)"
  if [ ! -x "$PROJ/Build/Linux/BlitzLegacy" ]; then
    say "no Build/Linux/BlitzLegacy - run Tools/unity.sh linux first"
    VERDICT+=("battle: SKIPPED (no build)")
  else
    # the restarted server needs a moment to listen again
    for _ in $(seq 1 30); do
      "${SSH[@]}" "$TARGET" "sudo ss -ulpn | grep -q ':$PORT '" 2>/dev/null && break
      sleep 2
    done
    DIRECT_REMOTE="$HOST:$PORT" "$PROJ/Tools/direct_smoke.sh" "$SECS" > "$OUT/smoke.txt" 2>&1
    LOGS="$PROJ/Logs/direct-smoke"
    ok=1
    for c in a b; do
      f="$LOGS/$c.log"
      live=$(grep -ac 'server battle is live' "$f")
      drops=$(grep -ac 'disconnected:' "$f")
      fails=$(grep -ac 'matchmaking failed' "$f")
      exc=$(grep -ac 'Exception' "$f")
      pings=$(grep -ao 'ping=[0-9]*' "$f" | cut -d= -f2 | sort -n | awk 'NR==1{min=$1} {max=$1} END{if (NR) print min "-" max " ms"; else print "-"}')
      say "client $c: live=$live disconnects=$drops matchmaking_failed=$fails exceptions=$exc ping=$pings"
      grep -a 'BlitzDirect' "$f" | grep -v 'smoke state' | sed "s/^/  $c: /" | tail -n 8 | tee -a "$REPORT" >/dev/null
      if [ "$live" = 0 ] || [ "$fails" != 0 ] || [ "$exc" != 0 ]; then ok=0; fi
    done
    say "server journal:"
    "${SSH[@]}" "$TARGET" "journalctl -u blitz-direct --no-pager --since '$STARTED UTC' | grep -a 'BlitzDirect\|Exception' | tail -n 20" 2>/dev/null \
      | sed 's/^.*\]: /  server: /' | tee -a "$REPORT" >/dev/null
    if [ "$ok" = 1 ]; then VERDICT+=("battle: PASS"); else VERDICT+=("battle: FAIL"); fi
  fi
fi

say ""
say "== verdict"
for v in "${VERDICT[@]}"; do say "$v"; done
say "report: $REPORT"
