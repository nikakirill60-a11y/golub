#!/usr/bin/env bash
# Export the model payload of every premium, event and collector vehicle.
#
# The researchable cards are exported by nation batches already, so this picks
# exactly the cards flagged "prem" in the generated table and feeds them to the
# regular exporter. The run takes hours; start it detached and follow the log.
#
#   nohup Tools/export_blitz_480_premiums.sh > /tmp/premium_export.log 2>&1 &
set -euo pipefail
cd "$(dirname "$0")"

mapfile -t cards < <(python3 - <<'PY'
import json, pathlib
path = pathlib.Path('../Assets/Blitz/Resources/Data/tanks.json')
for card in json.loads(path.read_text(encoding='utf-8'))['tanks']:
    if card.get('prem'):
        print(card['name'])
PY
)

echo "premium cards: ${#cards[@]}"
python3 port_blitz_480_models.py --textures --cards "${cards[@]}"
