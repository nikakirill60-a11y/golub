#!/bin/sh
# Persist a public Fusion App ID in the ignored .env, never in tracked assets.
set -e
PROJ="$(cd "$(dirname "$0")/.." && pwd)"
ID="${1:-}"
if ! printf '%s' "$ID" | grep -Eq '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'; then
  echo "usage: Tools/photon_app_id.sh <photon-fusion-app-id-guid>" >&2
  exit 2
fi
python3 - "$PROJ" "$ID" <<'PY'
from pathlib import Path
import os
import re
import shlex
import sys
root = Path(sys.argv[1])
path = root / '.env'
legacy = root / '.env.release.local'
text = path.read_text() if path.exists() else (legacy.read_text() if legacy.exists() else '')
line = 'BLITZ_PHOTON_APP_ID=' + shlex.quote(sys.argv[2])
pattern = r'(?m)^(?:export\s+)?BLITZ_PHOTON_APP_ID=.*$'
if re.search(pattern, text):
    text = re.sub(pattern, lambda match: line, text)
else:
    text = text.rstrip('\n') + '\n' + line + '\n'
path.write_text(text)
os.chmod(path, 0o600)
print('Photon App ID saved in local .env; the next launcher build refreshes its resource.')
PY
