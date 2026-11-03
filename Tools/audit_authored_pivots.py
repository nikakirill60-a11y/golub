#!/usr/bin/env python3
"""Check every exported turret pivot against the client's own authored value.

The Godot converter used to recover a turret pivot by matching the bounding
box centres of the visible mesh and the collision shell. A turret whose shell
is shorter than the mesh it armours - O-47, 112 Ice - then ended up centred
instead of seated: half the shell sank into the hull and the roof was left
unarmoured, and nothing in the geometry flagged it, because the shell was not
slid to either side.

The client ships the answer: every part carries `blitz/points/normal`, the
negated pivot in DAVA axes. The port now exports exactly that, and this audit
keeps it that way.

Usage:
    python3 Tools/audit_authored_pivots.py [CARD ...]
"""
from __future__ import annotations

import json
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

import port_blitz_480_models as port  # noqa: E402
import port_blitz_branch_models as exporter  # noqa: E402
from audit_blitz_480_tanks import UNITY, VEHICLES  # noqa: E402

DATA = UNITY / 'Assets/Blitz/Resources/Data'
# The pivots travel through a printed decimal, so only rounding may differ.
TOLERANCE = 0.002


def unity_pivot(dava: tuple) -> tuple:
    """DAVA (x, forward, up) -> Unity (x, up, back)."""
    return (dava[0], dava[2], -dava[1])


def audit(wanted: set[str]) -> dict:
    rows = []
    checked = 0
    for record in port.records():
        card = record['card']['name']
        if wanted and card not in wanted:
            continue
        tank_id = exporter.unity_id(card).lower()
        table = DATA / f'{tank_id}_nodes.json'
        if not table.exists():
            continue
        nodes = json.loads(table.read_text(encoding='utf-8'))['nodes']
        root = ET.parse(
            VEHICLES / record['nation'] / f"{record['vehicle_id']}.xml").getroot()
        for turret in root.find('turrets0'):
            slot = exporter.part_slot(turret)
            if slot in nodes:
                authored = exporter.authored_pivot(turret)
                if authored is not None:
                    checked += 1
                    compare(rows, card, slot, unity_pivot(authored),
                            nodes[slot]['local'])
            # The gun authors its own trunnion in the same tank space. It is
            # the point the runtime elevates the barrel around, so a pivot
            # recovered from a bounding box instead throws the gun off the
            # hull: the Pz.Sfl. IVc Flak 41 was 3.07 m down its own barrel.
            for gun in turret.find('guns'):
                gun_slot = exporter.part_slot(gun)
                if gun_slot not in nodes:
                    continue
                authored = exporter.authored_pivot(gun)
                if authored is None:
                    continue
                checked += 1
                compare(rows, card, gun_slot, unity_pivot(authored),
                        nodes[gun_slot]['world'])
    return {'checked': checked, 'rows': rows}


def compare(rows: list, card: str, mount: str, expected, actual) -> None:
    """Record a mount whose exported pivot left the authored one."""
    delta = [round(actual[axis] - expected[axis], 4) for axis in range(3)]
    if max(abs(value) for value in delta) > TOLERANCE:
        rows.append({'card': card, 'mount': mount, 'delta': delta,
                     'exported': actual,
                     'authored': [round(value, 6) for value in expected]})


def main() -> int:
    result = audit(set(sys.argv[1:]))
    print(json.dumps({'pivots': result['checked'],
                      'suspect': len(result['rows'])}, sort_keys=True))
    for row in result['rows']:
        print(json.dumps(row, sort_keys=True))
    return 1 if result['rows'] else 0


if __name__ == '__main__':
    raise SystemExit(main())
