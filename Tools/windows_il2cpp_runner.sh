#!/usr/bin/env bash
# Native Windows IL2CPP in the user's Docker/KVM guest; no Wine or Mono fallback.
set -Eeuo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$ROOT"
. "$ROOT/Tools/load_env.sh"
# Refresh only public connection resources before transferring the snapshot.
python3 - "$ROOT" <<'PYENV'
from pathlib import Path
import sys
root = Path(sys.argv[1])
sys.path.insert(0, str(root / 'Tools'))
from build_env import load_environment, write_runtime_settings
write_runtime_settings(root, load_environment(root))
PYENV
BUILDER="${BLITZ_WINDOWS_BUILDER_DIR:-$ROOT/../blitz-windows-builder}"
CONTAINER="${BLITZ_WINDOWS_CONTAINER:-blitz-windows-builder}"
: "${BLITZ_BUILD_ID:?Local CI must provide BLITZ_BUILD_ID}"
: "${BLITZ_REVISION:?Local CI must provide BLITZ_REVISION}"
[[ "$BLITZ_BUILD_ID" =~ ^[a-zA-Z0-9._-]+$ && "$BLITZ_REVISION" =~ ^[a-zA-Z0-9._-]+$ ]] || exit 2
[[ -s "$BUILDER/automation/build_key" ]] || { echo 'Missing Windows CI SSH key' >&2; exit 2; }
# The host key stays pinned under one alias, whichever address reaches it.
SSH=(ssh -i "$BUILDER/automation/build_key" -o IdentitiesOnly=yes -o BatchMode=yes
    -o StrictHostKeyChecking=accept-new -o UserKnownHostsFile="$BUILDER/automation/known_hosts"
    -o HostKeyAlias="${BLITZ_WINDOWS_SSH_HOST_KEY_ALIAS:-blitz-windows-builder}"
    -o ConnectTimeout=20 -o ServerAliveInterval=30)
case "${BLITZ_WINDOWS_SSH_MODE:-forward}" in
    forward)
        # QEMU user-mode (SLIRP) networking keeps the guest online behind a
        # host VPN; the guest is then reachable only through QEMU's loopback
        # host forward (127.0.0.1:2222 -> guest port 22).
        GUEST="${BLITZ_WINDOWS_SSH_HOST:-127.0.0.1}"
        SSH+=(-p "${BLITZ_WINDOWS_SSH_PORT:-2222}")
        ;;
    container)
        # Bridge networking: reach the guest address from inside the container.
        GUEST="$(docker exec "$CONTAINER" cat /dev/shm/qemu.ip | tr -d '\r\n')"
        SSH+=(-o "ProxyCommand=docker exec -i $CONTAINER nc %h %p")
        ;;
    *) echo "Unknown BLITZ_WINDOWS_SSH_MODE: $BLITZ_WINDOWS_SSH_MODE" >&2; exit 2 ;;
esac
[[ "$GUEST" =~ ^[0-9A-Za-z.-]+$ ]] || { echo 'Invalid guest address' >&2; exit 2; }
TARGET="${BLITZ_WINDOWS_SSH_USER:-blitz}@$GUEST"
# Fail fast instead of hanging in the SSH banner exchange for the whole build.
if ! "${SSH[@]}" "$TARGET" 'exit 0'; then
    echo "Windows guest SSH is unreachable via $GUEST: check that $CONTAINER is running and that the guest firewall admits TCP 22 from the SLIRP host address (rule Blitz-CI-SSH)." >&2
    exit 2
fi
RCEDIT="$BUILDER/shared/rcedit-x64.exe"
if [[ ! -f "$RCEDIT" ]]; then
    curl -fsSL https://github.com/electron/rcedit/releases/download/v2.0.0/rcedit-x64.exe -o "$RCEDIT"
fi
echo "3e7801db1a5edbec91b49a24a094aad776cb4515488ea5a4ca2289c400eade2a  $RCEDIT" | sha256sum -c -
# Never share .env files, keystores, the host private key or saves.
mkdir -p "$BUILDER/shared/project"
for folder in Assets Packages ProjectSettings Tools; do
    rsync -aL --delete --exclude "*.keystore" --exclude "*.jks" \
        --exclude ".env" --exclude ".env.*" --exclude "__pycache__" --exclude "*.pyc" \
        --exclude "TASK_PLAN*" --exclude "PORTING_NOTES*" "$ROOT/$folder/" "$BUILDER/shared/project/$folder/"
done
cp "$ROOT/tutorial_mods.txt" "$BUILDER/shared/project/tutorial_mods.txt"
COMMAND="powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"\\\\host.lan\\Data\\project\\Tools\\windows_il2cpp.ps1\" -BuildId \"$BLITZ_BUILD_ID\" -Revision \"$BLITZ_REVISION\""
"${SSH[@]}" "$TARGET" "$COMMAND"
python3 - "$BUILDER/shared/windows-output/build-result.json" <<'PY'
import json,os,sys
r=json.load(open(sys.argv[1],encoding='utf-8-sig'))
assert r['version']==os.environ['BLITZ_RELEASE_VERSION']
assert r['buildId']==os.environ['BLITZ_BUILD_ID'] and r['revision']==os.environ['BLITZ_REVISION']
assert r['backend']=='IL2CPP' and r['development'] is False and r['debugTools'] is True
print('Windows native build identity verified')
PY
mkdir -p "$ROOT/Build/Windows-productiondebug"
rsync -a --delete "$BUILDER/shared/windows-output/player/" "$ROOT/Build/Windows-productiondebug/"
cp "$BUILDER/shared/windows-output/windows-il2cpp.log" "$ROOT/Logs/windows-il2cpp.log"
