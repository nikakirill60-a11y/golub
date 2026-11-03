#!/usr/bin/env bash
# Deploys the built Linux player (Tools/unity.sh linux) to the cloud machine
# as the own dedicated battle server and (re)starts its systemd service.
#
# usage: Tools/direct_deploy.sh [user@host]
# env:   SSH_KEY=path             (required, configured in .env)
#        BLITZ_SERVER_BUILD=dir   (default Build/Linux)
#
# The cloud firewall (AWS security group) must let UDP 7777 in; this script
# cannot change it. Logs: ssh ... journalctl -u blitz-direct -f
set -e
PROJ="$(cd "$(dirname "$0")/.." && pwd)"
# Deployment targets and SSH identities are machine-local, never source defaults.
. "$PROJ/Tools/load_env.sh"
TARGET="${1:-${BLITZ_DIRECT_DEPLOY_TARGET:-}}"
: "${TARGET:?Set BLITZ_DIRECT_DEPLOY_TARGET in .env or pass user@host}"
: "${SSH_KEY:?Set SSH_KEY in .env}"
KEY="$SSH_KEY"
SRC="${BLITZ_SERVER_BUILD:-$PROJ/Build/Linux}"
PORT="${DIRECT_PORT:-7777}"
[[ "$PORT" =~ ^[0-9]{1,5}$ ]] && ((10#$PORT >= 1 && 10#$PORT <= 65535)) || {
  echo "DIRECT_PORT must be an integer from 1 to 65535" >&2; exit 2;
}
SSH=(ssh -i "$KEY" -o StrictHostKeyChecking=accept-new)
printf -v SSH_RSH "%q " "${SSH[@]}"
[ -x "$SRC/BlitzLegacy" ] || { echo "no player build in $SRC (Tools/unity.sh linux)" >&2; exit 1; }

# rsync on the far side, and a swap file: one battle needs most of the RAM
# of a small instance, and the OOM killer must not take the VPN with it.
"${SSH[@]}" "$TARGET" 'set -e
  command -v rsync >/dev/null || { sudo apt-get update -qq && sudo apt-get install -y -qq rsync; }
  # The Unity 7 CoreCLR player aborts at startup without ICU.
  if ! /sbin/ldconfig -p | grep -q libicuuc; then
    sudo apt-get update -qq
    sudo apt-get install -y -qq "$(apt-cache search --names-only "^libicu[0-9]+\$" | sort -V | tail -n 1 | cut -d" " -f1)"
  fi
  if [ -z "$(sudo swapon --show --noheadings)" ]; then
    sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile && sudo mkswap /swapfile >/dev/null
    sudo swapon /swapfile && echo "/swapfile none swap sw 0 0" | sudo tee -a /etc/fstab >/dev/null
    echo "swap file enabled"
  fi
  mkdir -p ~/blitz-server ~/blitz-server-profile'

rsync -az --delete --info=stats1 \
  --exclude '*_BackUpThisFolder_ButDontShipItWithYourGame' --exclude 'Logs' \
  -e "$SSH_RSH" "$SRC/" "$TARGET:blitz-server/"

sed "s|@PORT@|$PORT|g" "$PROJ/Tools/blitz-direct.service" | "${SSH[@]}" "$TARGET" 'cat > /tmp/blitz-direct.service'
"${SSH[@]}" "$TARGET" 'set -e
  sed -e "s|@USER@|$(id -un)|g" -e "s|@HOME@|$HOME|g" /tmp/blitz-direct.service | sudo tee /etc/systemd/system/blitz-direct.service >/dev/null
  sudo systemctl daemon-reload
  sudo systemctl enable blitz-direct >/dev/null 2>&1
  sudo systemctl restart blitz-direct
  sleep 20
  systemctl --no-pager --lines=0 status blitz-direct | head -n 5
  journalctl -u blitz-direct --no-pager -n 200 | grep BlitzDirect | tail -n 5'
