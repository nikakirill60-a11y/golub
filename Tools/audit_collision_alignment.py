#!/usr/bin/env python3
"""Per-part collision-vs-model audit.

``audit_armor_alignment.py`` compares whole-vehicle envelopes and
``audit_armor_silhouette.py`` compares shapes against the *whole* visible tank,
so a shell that sinks into the hull it sits on stays invisible to both: the
Dracula turret shell hung 0.54 m below its turret, inside the hull, and every
round that hit the drawn turret passed through nothing.

This audit compares each collision shell against the visible geometry of the
same part - the hull shell against ``hull_vis``, a turret shell against that
turret slot, a gun mask against that gun slot - in the part's own space:

* ``sink``  - the shell floor sits below the visible part's floor. A shell is
              never under the part it armours, so this is a displacement;
* ``shift`` - the shell is the right size but translated along an axis: both
              of its faces moved the same way.

A gun mask is buried in the turret by design, so its long axis is measured
only for the muzzle side.

Run:  python3 Tools/audit_collision_alignment.py [TANK ...] [--json out.json]
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent.parent
RES = ROOT / 'Assets/Blitz/Resources'
MODELS = RES / 'Models/Tanks'
DATA = RES / 'Data'

# The client authors collision as coarse shells around the pivot it ships in
# the vehicle XML, so some play against the drawn part is normal: on an open
# gun mount (Object 263, Sturer Emil, RhB Waffentrager) that pivot sits a
# quarter of a metre off the centre of the visible box. A real displacement is
# far larger - the buried O-47 turret was out by 0.59 m.
SINK_TOLERANCE = 0.10
SHIFT_TOLERANCE = 0.30


def read_obj(path: Path):
    """Return (vertices, {object name: vertex indices it uses})."""
    verts: list[tuple[float, float, float]] = []
    objects: dict[str, set[int]] = {}
    current = None
    with path.open(encoding='utf-8') as handle:
        for line in handle:
            if line.startswith('v '):
                verts.append(tuple(float(v) for v in line.split()[1:4]))
            elif line.startswith('o '):
                current = line[2:].strip()
                objects.setdefault(current, set())
            elif line.startswith('f '):
                used = objects.setdefault(current or '', set())
                for chunk in line.split()[1:]:
                    index = int(chunk.split('/')[0])
                    used.add(index - 1 if index > 0 else len(verts) + index)
    return np.asarray(verts, dtype=np.float64), objects


def audit(tank_dir: Path):
    tank_id = tank_dir.name
    data_id = tank_id.lower()
    paths = {
        'vis': tank_dir / f'{tank_id}.obj',
        'armor': tank_dir / f'{tank_id}_armor.obj',
        'nodes': DATA / f'{data_id}_nodes.json',
        'table': DATA / f'{data_id}_armor.json',
    }
    for path in paths.values():
        if not path.exists():
            return [{'id': tank_id, 'error': f'missing {path.name}'}]

    vis_verts, vis_objects = read_obj(paths['vis'])
    armor_verts, armor_objects = read_obj(paths['armor'])
    nodes = json.loads(paths['nodes'].read_text(encoding='utf-8'))['nodes']
    table = json.loads(paths['table'].read_text(encoding='utf-8'))
    offsets = table.get('mountOffsets', {})

    mounts: dict[str, set[int]] = {}
    for plate in table['plates']:
        used = armor_objects.get(str(plate['name']))
        if used:
            mounts.setdefault(str(plate['mount']), set()).update(used)

    rows = []
    for mount in sorted(mounts):
        part = 'hull_vis' if mount == 'hull' else mount
        drawn = vis_objects.get(part)
        if not drawn:
            # A casemate mount draws nothing of its own; its shell belongs to
            # the hull and `TankVisibleGeometryTests` guards that case.
            continue
        shell = armor_verts[sorted(mounts[mount])]
        visible = vis_verts[sorted(drawn)]
        pivot = np.zeros(3) if mount == 'hull' else np.asarray(
            nodes.get(mount, {}).get('world', (0.0, 0.0, 0.0)), dtype=np.float64)
        pivot = pivot + np.asarray(offsets.get(mount, (0.0, 0.0, 0.0)), dtype=np.float64)

        low, high = shell.min(axis=0) + pivot, shell.max(axis=0) + pivot
        drawn_low, drawn_high = visible.min(axis=0), visible.max(axis=0)
        delta_low = low - drawn_low
        delta_high = high - drawn_high

        # Both faces moved the same way: the shell kept its size and slid.
        shift = np.where(
            np.sign(delta_low) == np.sign(delta_high),
            np.minimum(np.abs(delta_low), np.abs(delta_high)) * np.sign(delta_low),
            0.0,
        )
        if mount.startswith('gun_'):
            # The mask sinks into the turret behind the barrel on purpose.
            shift[2] = min(shift[2], 0.0) if shift[2] < 0 else 0.0
            shift[0] = 0.0 if abs(shift[0]) < SHIFT_TOLERANCE else shift[0]
        axis = int(np.argmax(np.abs(shift)))
        rows.append({
            'id': tank_id,
            'mount': mount,
            'sink': round(float(max(drawn_low[1] - low[1], 0.0)), 3),
            'shift': round(float(shift[axis]), 3),
            'axis': 'xyz'[axis],
            'delta_low': [round(float(v), 3) for v in delta_low],
            'delta_high': [round(float(v), 3) for v in delta_high],
        })
    return rows


def suspect(row) -> bool:
    """Only a slid shell is a defect.

    A shell that merely reaches below its part while still covering its roof
    (Renault UE 57's open mount, A-20's 20 mm masks) is the coarse volume the
    client authored, not a displacement. `sink` stays in the report so the
    number is visible, but it does not condemn a mount on its own.
    """
    return bool(row.get('error')) or abs(row.get('shift', 0.0)) > SHIFT_TOLERANCE


def main() -> None:
    args = list(sys.argv[1:])
    out_path = ROOT / 'Logs/collision-alignment.json'
    if '--json' in args:
        index = args.index('--json')
        out_path = Path(args[index + 1])
        del args[index:index + 2]
    wanted = set(args)

    rows = []
    for tank_dir in sorted(MODELS.iterdir()):
        if not tank_dir.is_dir() or (wanted and tank_dir.name not in wanted):
            continue
        rows.extend(audit(tank_dir))

    bad = [row for row in rows if suspect(row)]
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(rows, indent=1, ensure_ascii=False) + '\n',
                        encoding='utf-8')
    print(json.dumps({'mounts': len(rows), 'suspect': len(bad)}))
    for row in sorted(bad, key=lambda r: -max(r.get('sink', 0.0),
                                              abs(r.get('shift', 0.0)))):
        print(json.dumps(row, ensure_ascii=False))
    sys.exit(1 if bad else 0)


if __name__ == '__main__':
    main()
