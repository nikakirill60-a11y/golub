#!/usr/bin/env python3
"""Audit that every gun module of every card renders a real barrel.

For each card, each turret and each gun slot the visible OBJ must carry a
group for that slot whose geometry reaches the authored muzzle
(`<slot>_shoot_pos`). A gun whose barrel lives in an unexported child node
(skinned Gatling cylinders, recoil rigs) shows up as a stub far behind it.

The barrel must also be continuous from the turret face to the muzzle: a
section the client keeps in an unexported node (the Silencer's `gun_04_mask_obves`
tube on the `_obves` sheet) leaves the muzzle end floating ahead of the turret.
Every cross-section along the bore must be wrapped by gun geometry above and
below the axis; a fitting that only rides on top of the barrel does not count.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'Assets/Blitz/Resources/Data'
MODELS = ROOT / 'Assets/Blitz/Resources/Models/Tanks'
MAX_GAP = 0.6
# Uncovered bore allowed between turret face and muzzle (muzzle-brake slots,
# recoil-sleeve seams). The Silencer's missing tube is 1.2 m.
MAX_HOLE = 0.25
# Gun slots whose source scene itself is not continuous along the bore, with
# the reason. Anything else with a hole is an export bug.
AUTHORED_HOLES = {
    # The water gun is a cartoon nozzle on a hose: its muzzle marker floats
    # 0.43 m above the gun body, the client model has no tube there.
    ('oth10_warduck', 'gun_03'): 'muzzle marker above the nozzle',
    # The client authors a 0.45 m open gap between `gun_01_mask` (ends at
    # y 3.59) and the `gun_01` light emitter (starts at y 4.04).
    ('oth70_lightbringer', 'gun_01'): 'open emitter gap in the client scene',
}
BORE_RADIUS = 0.6
STEP = 0.05


def unity_id(name: str) -> str:
    return ''.join(c for c in name if c.isalnum()).upper()


def obj_triangles(path: Path) -> dict:
    """Group -> list of triangles (three vertex tuples) of a visible OBJ."""
    verts, groups, current = [], {}, None
    with path.open() as handle:
        for line in handle:
            if line.startswith('v '):
                verts.append(tuple(map(float, line.split()[1:4])))
            elif line.startswith('o '):
                current = line[2:].strip()
            elif line.startswith('f '):
                ids = [int(part.split('/')[0]) for part in line.split()[1:]]
                tris = groups.setdefault(current, [])
                for i in range(1, len(ids) - 1):
                    tris.append((verts[ids[0] - 1], verts[ids[i] - 1], verts[ids[i + 1] - 1]))
    return groups


def bore_holes(sources: list, muzzle: list, start_z: float) -> list:
    """Uncovered bore intervals (z_from, z_to) between start_z and the muzzle.

    `sources` holds one triangle list per part (the gun, the turret, the hull).
    The OBJ faces -Z. A section counts as covered when triangles of one and the
    same part near the bore cross it both above and below the axis, so a
    fitting on top of the gun and the hull roof under it never pass as a
    barrel together.
    """
    end_z = muzzle[2] + STEP
    if start_z <= end_z:
        return []
    # At zero elevation the barrel runs along -Z. The trunnion often sits
    # below or behind the bore, older muzzle markers sit on the rim of the
    # tube (VK 36.01 H, Centurion) and a muzzle brake skews the centre of the
    # tip (Super Pershing), so a section passes around either line: the
    # muzzle marker's or the centre of the barrel's last half metre.
    axes = [(muzzle[0], muzzle[1])]
    tip = [v for tri in sources[0] for v in tri if muzzle[2] - 0.05 <= v[2] <= muzzle[2] + 0.5
           and abs(v[0] - muzzle[0]) < BORE_RADIUS and abs(v[1] - muzzle[1]) < BORE_RADIUS]
    if tip:
        axes.append(((min(v[0] for v in tip) + max(v[0] for v in tip)) / 2,
                     (min(v[1] for v in tip) + max(v[1] for v in tip)) / 2))
    parts = []
    for ax, ay in axes:
        for triangles in sources:
            spans = []
            for tri in triangles:
                lo = min(v[2] for v in tri)
                hi = max(v[2] for v in tri)
                if hi < end_z or lo > start_z:
                    continue
                # A mantlet face is often one large triangle across the bore,
                # so a triangle counts when its box reaches into the tube.
                if (min(v[0] for v in tri) > ax + BORE_RADIUS or max(v[0] for v in tri) < ax - BORE_RADIUS
                        or min(v[1] for v in tri) > ay + BORE_RADIUS or max(v[1] for v in tri) < ay - BORE_RADIUS):
                    continue
                spans.append((lo, hi, min(v[1] for v in tri) - ay, max(v[1] for v in tri) - ay))
            parts.append(spans)

    def covered(z):
        for spans in parts:
            below = above = False
            for lo, hi, ylo, yhi in spans:
                if lo - 1e-4 <= z <= hi + 1e-4:
                    below = below or ylo < -0.005
                    above = above or yhi > 0.005
                    if below and above:
                        return True
        return False

    holes, hole_start = [], None
    steps = int((start_z - end_z) / STEP) + 1
    for k in range(steps + 1):
        z = start_z - k * STEP
        if covered(z):
            if hole_start is not None:
                holes.append((hole_start, z))
                hole_start = None
        elif hole_start is None:
            hole_start = z
    # A barrel that merely stops short of the muzzle is the MAX_GAP check's.
    if hole_start is not None and hole_start - end_z > MAX_GAP:
        holes.append((hole_start, end_z))
    return [(a, b) for a, b in holes if a - b > MAX_HOLE]


def obj_groups(path: Path) -> dict:
    verts, groups, current = [], {}, None
    with path.open() as handle:
        for line in handle:
            if line.startswith('v '):
                verts.append(tuple(map(float, line.split()[1:4])))
            elif line.startswith('o '):
                current = line[2:].strip()
            elif line.startswith('f '):
                entry = groups.setdefault(current, [0, set()])
                entry[0] += 1
                for part in line.split()[1:]:
                    entry[1].add(int(part.split('/')[0]))
    result = {}
    for name, (faces, indices) in groups.items():
        points = [verts[i - 1] for i in indices]
        low = [min(p[a] for p in points) for a in range(3)]
        high = [max(p[a] for p in points) for a in range(3)]
        result[name] = {'faces': faces, 'low': low, 'high': high}
    return result


def audit(names: set[str] | None = None) -> list[dict]:
    tanks = json.loads((DATA / 'tanks.json').read_text())['tanks']
    problems = []
    for card in tanks:
        if names and card['name'] not in names:
            continue
        uid = unity_id(card['name'])
        obj = MODELS / uid / (uid + '.obj')
        nodes_path = DATA / (uid.lower() + '_nodes.json')
        if not obj.exists() or not nodes_path.exists():
            problems.append({'tank': card['name'], 'issue': 'no model'})
            continue
        groups = obj_groups(obj)
        triangles = None
        nodes = json.loads(nodes_path.read_text()).get('nodes', {})
        for turret in card['turrets']:
            for slot in turret.get('guns', []):
                mesh = slot['mesh']
                group = groups.get(mesh)
                row = {'tank': card['name'], 'turret': turret['name'], 'gun': slot['name'], 'mesh': mesh}
                if group is None:
                    problems.append(dict(row, issue='no gun group'))
                    continue
                muzzle = nodes.get(mesh + '_shoot_pos')
                if muzzle is None:
                    problems.append(dict(row, issue='no muzzle node'))
                    continue
                # A spinning sub-assembly (`gun_01_rotor`, the Gatling
                # cylinder) is part of the barrel the player sees.
                rotor = groups.get(mesh + '_rotor')
                front = min(group['low'][2], rotor['low'][2] if rotor else group['low'][2])
                gap = front - muzzle['world'][2]
                length = group['high'][2] - front
                if gap > MAX_GAP or group['faces'] < 24:
                    problems.append(dict(row, issue='barrel short of muzzle', gap=round(gap, 2),
                                         length=round(length, 2), faces=group['faces']))
                    continue
                pivot = nodes.get(mesh)
                body = groups.get(turret.get('mesh') or '')
                if pivot is None:
                    continue
                if triangles is None:
                    triangles = obj_triangles(obj)
                # Rotors and baked clip parts (`gun_01_rotor`, `gun_01_anim_*`)
                # are pieces of the same barrel.
                parts = [t for name, tris in triangles.items()
                         if name == mesh or (name or '').startswith(mesh + '_') for t in tris]
                # A casemate keeps the ball mantlet the barrel passes through
                # in the superstructure or the hull (Hetzer): that is barrel too.
                sources = [parts, triangles.get(turret.get('mesh'), []), triangles.get('hull_vis', [])]
                # From the turret face (or the trunnion if the gun sits in
                # front of it) to the muzzle.
                start = min(body['low'][2] if body else pivot['world'][2], pivot['world'][2])
                holes = bore_holes(sources, muzzle['world'], start)
                if holes and (card['name'], mesh) not in AUTHORED_HOLES:
                    problems.append(dict(row, issue='barrel not continuous',
                                         holes=[[round(a, 2), round(b, 2)] for a, b in holes]))
    return problems


def main() -> int:
    names = set(sys.argv[1:]) or None
    problems = audit(names)
    for p in problems:
        print(json.dumps(p, ensure_ascii=False))
    print(f'{len(problems)} problem gun slots', file=sys.stderr)
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
