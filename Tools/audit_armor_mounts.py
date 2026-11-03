#!/usr/bin/env python3
"""Per-mount armour alignment audit.

The visible OBJ bakes every authored transform into tank space. The armour OBJ
keeps turret/gun plates in the local frame of ``turret01``/``gun01``, and the
runtime re-attaches them with translation only. Compare each mount separately:
if an authored mount carries rotation, the plates land rotated against the
visible part and only a per-mount comparison shows it.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RES = ROOT / 'Assets/Blitz/Resources'
MODELS = RES / 'Models/Tanks'
DATA = RES / 'Data'


def read_obj(path: Path):
    objects = {}
    current = None
    with path.open(encoding='utf-8') as handle:
        for line in handle:
            if line.startswith('o '):
                current = line[2:].strip()
                objects[current] = []
            elif line.startswith('v ') and current is not None:
                x, y, z = (float(v) for v in line.split()[1:4])
                objects[current].append((x, y, z))
    return objects


def bounds(points):
    return (
        tuple(min(p[i] for p in points) for i in range(3)),
        tuple(max(p[i] for p in points) for i in range(3)),
    )


def audit(tank_dir: Path):
    tank_id = tank_dir.name
    data_id = tank_id.lower()
    try:
        armor = read_obj(tank_dir / f'{tank_id}_armor.obj')
        visible = read_obj(tank_dir / f'{tank_id}.obj')
        plates = json.loads((DATA / f'{data_id}_armor.json').read_text())['plates']
        nodes = json.loads((DATA / f'{data_id}_nodes.json').read_text())
    except FileNotFoundError as error:
        return {'id': tank_id, 'error': str(error)}

    pivots = nodes['nodes']
    report = {'id': tank_id, 'mounts': {}}
    for turret_slot, turret in nodes['rig']['turrets'].items():
        for gun_slot in turret['guns']:
            for mount, part in ((turret_slot, turret_slot), (gun_slot, gun_slot)):
                points = []
                for plate in plates:
                    if plate['mount'] != mount:
                        continue
                    points.extend(armor.get(plate['name'], []))
                if not points or part not in visible:
                    continue
                offset = pivots.get(mount, {}).get('world', [0, 0, 0])
                ab = bounds([(p[0] + offset[0], p[1] + offset[1], p[2] + offset[2])
                             for p in points])
                vb = bounds(visible[part])
                # plates must sit inside the visible part, allowing for the
                # mantlet/screen slack the client authors
                outside = max(
                    max(vb[0][i] - ab[0][i] for i in range(3)),
                    max(ab[1][i] - vb[1][i] for i in range(3)),
                )
                report['mounts'][mount] = {
                    'outside': round(outside, 3),
                    'armor': [[round(v, 3) for v in ab[0]], [round(v, 3) for v in ab[1]]],
                    'visible': [[round(v, 3) for v in vb[0]], [round(v, 3) for v in vb[1]]],
                }
    return report


def main():
    wanted = set(sys.argv[1:])
    worst = []
    for tank_dir in sorted(MODELS.iterdir()):
        if not tank_dir.is_dir() or (wanted and tank_dir.name not in wanted):
            continue
        report = audit(tank_dir)
        if report.get('error'):
            print(json.dumps(report))
            continue
        for mount, entry in report['mounts'].items():
            worst.append((entry['outside'], tank_dir.name, mount, entry))
    worst.sort(reverse=True)
    print(json.dumps({'mounts': len(worst),
                      'over_0_2': sum(1 for w in worst if w[0] > 0.2),
                      'over_0_5': sum(1 for w in worst if w[0] > 0.5)}))
    for outside, tank, mount, entry in worst[:25]:
        print(tank, mount, outside, entry['armor'], entry['visible'])


if __name__ == '__main__':
    main()
