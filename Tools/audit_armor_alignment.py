#!/usr/bin/env python3
"""Compare exported armour plate geometry against the visible tank geometry.

For every ported tank this reads ``<Id>.obj`` (visible), ``<Id>_phys.obj``
(hull shell) and ``<Id>_armor.obj`` (collision plates) plus ``<id>_nodes.json``
and ``<id>_armor.json``, lifts every armour plate into tank space using the
exported pivots and reports how far the armour envelope is from the visible
one.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RES = ROOT / 'Assets/Blitz/Resources'
MODELS = RES / 'Models/Tanks'
DATA = RES / 'Data'


def read_obj(path: Path) -> dict[str, list[tuple[float, float, float]]]:
    objects: dict[str, list[tuple[float, float, float]]] = {}
    current = None
    verts: list[tuple[float, float, float]] = []
    with path.open(encoding='utf-8') as handle:
        for line in handle:
            if line.startswith('o '):
                current = line[2:].strip()
                objects[current] = []
            elif line.startswith('v '):
                x, y, z = (float(value) for value in line.split()[1:4])
                verts.append((x, y, z))
                if current is not None:
                    objects[current].append((x, y, z))
    return objects


def bounds(points):
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    zs = [p[2] for p in points]
    return (min(xs), min(ys), min(zs)), (max(xs), max(ys), max(zs))


def fmt(b):
    (a, c) = b
    return '[%.2f %.2f %.2f]..[%.2f %.2f %.2f]' % (a[0], a[1], a[2], c[0], c[1], c[2])


def audit(tank_dir: Path):
    tank_id = tank_dir.name
    data_id = tank_id.lower()
    armor_obj = tank_dir / f'{tank_id}_armor.obj'
    vis_obj = tank_dir / f'{tank_id}.obj'
    phys_obj = tank_dir / f'{tank_id}_phys.obj'
    armor_json = DATA / f'{data_id}_armor.json'
    nodes_json = DATA / f'{data_id}_nodes.json'
    for path in (armor_obj, vis_obj, phys_obj, armor_json, nodes_json):
        if not path.exists():
            return {'id': tank_id, 'error': f'missing {path.name}'}

    armor = read_obj(armor_obj)
    visible = read_obj(vis_obj)
    phys = read_obj(phys_obj)
    plates = json.loads(armor_json.read_text())['plates']
    nodes = json.loads(nodes_json.read_text())
    pivots = nodes['nodes']
    rig = nodes['rig']
    turret = rig['turret']
    gun = rig['gun']

    def pivot_world(name):
        entry = pivots.get(name)
        return tuple(entry['world']) if entry else (0.0, 0.0, 0.0)

    # tank-space envelope of every installed plate
    world_points = []
    per_mount = {}
    for plate in plates:
        name = plate['name']
        mount = plate['mount']
        if mount not in ('hull', turret, gun):
            continue
        points = armor.get(name)
        if not points:
            per_mount.setdefault('__missing__', []).append(name)
            continue
        if mount == 'hull':
            offset = (0.0, 0.0, 0.0)
        elif mount == turret:
            offset = pivot_world(turret)
        else:
            offset = pivot_world(gun)
        moved = [(p[0] + offset[0], p[1] + offset[1], p[2] + offset[2]) for p in points]
        world_points.extend(moved)
        per_mount.setdefault(mount, []).extend(moved)

    vis_points = [p for name, pts in visible.items() for p in pts]
    phys_points = [p for pts in phys.values() for p in pts]
    if not world_points or not vis_points:
        return {'id': tank_id, 'error': 'no geometry'}

    ab = bounds(world_points)
    vb = bounds(vis_points)
    pb = bounds(phys_points) if phys_points else vb

    # how far outside the visible envelope does the armour reach
    slack = max(
        max(vb[0][i] - ab[0][i] for i in range(3)),
        max(ab[1][i] - vb[1][i] for i in range(3)),
    )
    size_a = [ab[1][i] - ab[0][i] for i in range(3)]
    size_v = [vb[1][i] - vb[0][i] for i in range(3)]
    return {
        'id': tank_id,
        'plates': len(plates),
        'armor': fmt(ab),
        'visible': fmt(vb),
        'phys': fmt(pb),
        'overhang': round(slack, 3),
        'size_ratio': [round(size_a[i] / size_v[i], 3) if size_v[i] else None for i in range(3)],
        'missing': per_mount.get('__missing__', []),
    }


def main():
    wanted = set(sys.argv[1:])
    rows = []
    for tank_dir in sorted(MODELS.iterdir()):
        if not tank_dir.is_dir():
            continue
        if wanted and tank_dir.name not in wanted:
            continue
        rows.append(audit(tank_dir))
    bad = [row for row in rows if row.get('error') or row.get('overhang', 0) > 0.35 or row.get('missing')]
    print(json.dumps({'tanks': len(rows), 'suspect': len(bad)}))
    for row in sorted(bad, key=lambda r: -(r.get('overhang') or 0)):
        print(json.dumps(row, ensure_ascii=False))


if __name__ == '__main__':
    main()
