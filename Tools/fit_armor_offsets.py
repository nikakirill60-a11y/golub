#!/usr/bin/env python3
"""Fit the armour shell of every mount onto the visible mesh it rides.

The Godot exporter recovers a part's pivot by matching the *bounding box
centres* of the visible mesh and the collision shell. A tall cupola, an
antenna or a commander's machine gun stretches the visible box upwards, so the
recovered pivot - and with it the whole armour shell - floats above the turret
it belongs to. IS-7 is half a metre out, and the armour a shell hits in battle
is then the wrong plate.

This tool measures the real offset instead of assuming one: the armour shell of
a mount is slid over an occupancy grid of the visible mesh until as much of it
as possible sits inside the part. The correction is written back into
``Data/<id>_armor.json`` as ``mountOffsets`` and applied at runtime on top of
the node pivot, so the turret keeps its exported rotation centre.

Usage:
    python3 Tools/fit_armor_offsets.py [TANK ...] [--apply] [--report PATH]
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

VOXEL = 0.04          # metres per occupancy cell
SEARCH = 0.80         # metres of travel allowed for a hull or turret shell
# A mantlet is a short cylinder wrapped around a long barrel, so it scores
# just as well half a metre down the gun as it does on the trunnion. Its
# search window stays narrow to keep that degenerate slide out.
GUN_SEARCH = 0.25
STEP = 0.02           # metres per search step
PASSES = 3            # coordinate-descent sweeps
# Travel is not free: a shell that is already seated must not be dragged
# across the tank for a fraction of a percent of containment.
TRAVEL_PENALTY = 0.15 # containment per metre of shift
# A correction is only written for a mount that is genuinely broken: a shell
# that already sits inside its part is left alone, however much a centimetre of
# travel would flatter the score.
MAX_HEALTHY = 0.75    # containment a healthy mount reaches on its own
MIN_GAIN = 0.10       # containment the correction has to win
MIN_SHIFT = 0.05      # metres; smaller corrections are noise
MAX_SHIFT = 0.60      # metres; a larger "fix" is a bad fit, not a pivot error
MIN_SEATED = 0.70     # containment the corrected shell has to reach
# Containment is blind to which way a shell leaves its part: Sturer Emil's
# cradle scored better 0.22 m to the side, and the correction slid a centred
# shell off the part it armours. A correction that moves the whole shell past
# the part's own box is a bad fit, whatever it scores.
MAX_DISPLACEMENT = 0.12


def read_obj(path: Path):
    verts: list[tuple[float, float, float]] = []
    objects: dict[str, list[tuple[int, int, int]]] = {}
    current = None
    with path.open(encoding='utf-8') as handle:
        for line in handle:
            if line.startswith('v '):
                verts.append(tuple(float(v) for v in line.split()[1:4]))
            elif line.startswith('o '):
                current = line[2:].strip()
                objects.setdefault(current, [])
            elif line.startswith('f '):
                idx = []
                for chunk in line.split()[1:]:
                    value = int(chunk.split('/')[0])
                    idx.append(value - 1 if value > 0 else len(verts) + value)
                for i in range(1, len(idx) - 1):
                    objects.setdefault(current or '', []).append(
                        (idx[0], idx[i], idx[i + 1]))
    return np.asarray(verts, dtype=np.float64), objects


def sample_triangles(tris: np.ndarray, spacing: float = VOXEL * 0.5) -> np.ndarray:
    """Points covering every triangle densely enough for an occupancy grid."""
    if len(tris) == 0:
        return np.zeros((0, 3))
    a, b, c = tris[:, 0], tris[:, 1], tris[:, 2]
    edge = np.maximum.reduce([
        np.linalg.norm(b - a, axis=1),
        np.linalg.norm(c - a, axis=1),
        np.linalg.norm(c - b, axis=1),
    ])
    steps = np.clip(np.ceil(edge / spacing).astype(int), 1, 24)
    points = [tris.reshape(-1, 3)]
    for n in np.unique(steps):
        block = tris[steps == n]
        grid = [(i / n, j / n) for i in range(n + 1) for j in range(n + 1 - i)]
        for u, v in grid:
            points.append(block[:, 0] + u * (block[:, 1] - block[:, 0])
                          + v * (block[:, 2] - block[:, 0]))
    return np.concatenate(points)


class Occupancy:
    """Voxel occupancy of the visible mesh, grown by one cell of slack."""

    def __init__(self, points: np.ndarray, pad: float = SEARCH + 0.2):
        self.origin = points.min(axis=0) - pad
        size = np.ceil((points.max(axis=0) + pad - self.origin) / VOXEL).astype(int) + 1
        self.grid = np.zeros(tuple(size), dtype=bool)
        self._mark(points)
        self.grid = self._grow(self.grid)

    def _mark(self, points: np.ndarray):
        idx = np.floor((points - self.origin) / VOXEL).astype(int)
        np.clip(idx, 0, np.asarray(self.grid.shape) - 1, out=idx)
        self.grid[idx[:, 0], idx[:, 1], idx[:, 2]] = True

    @staticmethod
    def _grow(grid: np.ndarray) -> np.ndarray:
        out = grid.copy()
        for axis in range(3):
            out |= np.roll(grid, 1, axis=axis)
            out |= np.roll(grid, -1, axis=axis)
        return out

    def contained(self, points: np.ndarray) -> float:
        idx = np.floor((points - self.origin) / VOXEL).astype(int)
        inside = np.all((idx >= 0) & (idx < np.asarray(self.grid.shape)), axis=1)
        if not inside.any():
            return 0.0
        hits = np.zeros(len(points), dtype=bool)
        sel = idx[inside]
        hits[inside] = self.grid[sel[:, 0], sel[:, 1], sel[:, 2]]
        return float(hits.mean())


def fit(occupancy: Occupancy, points: np.ndarray, reach: float = SEARCH):
    """Coordinate-descent search for the offset that seats the shell best.

    The score is containment minus a travel penalty, so the search prefers the
    smallest move that explains the mismatch.
    """
    best = np.zeros(3)
    start = occupancy.contained(points)
    score = start
    rated = start
    steps = np.arange(-reach, reach + 1e-9, STEP)
    for _ in range(PASSES):
        moved = False
        for axis in range(3):
            trial = best.copy()
            for delta in steps:
                trial[axis] = best[axis] + delta
                if abs(trial[axis]) > reach + 1e-9:
                    continue
                value = occupancy.contained(points + trial)
                value_rated = value - TRAVEL_PENALTY * float(np.abs(trial).sum())
                if value_rated > rated + 1e-6:
                    rated = value_rated
                    score = value
                    best = trial.copy()
                    moved = True
        if not moved:
            break
    return best, start, score


def displacement(shell: np.ndarray, host: np.ndarray) -> float:
    """How far the whole shell sits past the part's box, in metres.

    Both faces of the shell have to leave on the same side before this counts
    as a slide, so a smaller or coarser shell still measures zero. This is the
    metric `TankPartCollisionAlignmentTests` enforces on the shipped assets.
    """
    below = shell.min(axis=0) - host.min(axis=0)
    above = shell.max(axis=0) - host.max(axis=0)
    slide = np.where(np.sign(below) == np.sign(above),
                     np.minimum(np.abs(below), np.abs(above)), 0.0)
    return float(slide.max())


def mounts_of(plates, turret_nodes, gun_nodes):
    order = []
    for plate in plates:
        mount = plate['mount']
        if mount not in order:
            order.append(mount)
    return order


def audit_tank(tank_dir: Path, apply: bool, verify: bool = False):
    tank_id = tank_dir.name
    data_id = tank_id.lower()
    vis_path = tank_dir / f'{tank_id}.obj'
    armor_path = tank_dir / f'{tank_id}_armor.obj'
    armor_json = DATA / f'{data_id}_armor.json'
    nodes_json = DATA / f'{data_id}_nodes.json'
    for path in (vis_path, armor_path, armor_json, nodes_json):
        if not path.exists():
            return {'id': tank_id, 'error': f'missing {path.name}'}

    vis_verts, vis_objects = read_obj(vis_path)
    arm_verts, arm_objects = read_obj(armor_path)
    table = json.loads(armor_json.read_text())
    nodes = json.loads(nodes_json.read_text())
    pivots = nodes['nodes']

    def pivot(name):
        entry = pivots.get(name)
        return np.asarray(entry['world'] if entry else (0.0, 0.0, 0.0), dtype=float)

    module_nodes = set(pivots)
    hull_objects = [name for name in vis_objects if name not in module_nodes]

    results = {}
    for mount in mounts_of(table['plates'], None, None):
        names = [p['name'] for p in table['plates']
                 if p['mount'] == mount and p['name'] in arm_objects]
        if not names:
            continue
        if mount == 'hull':
            host_names = hull_objects
            offset = np.zeros(3)
        else:
            host_names = [mount] if mount in vis_objects else []
            offset = pivot(mount)
        if verify:
            # Re-measure the shipped result: the shell is placed exactly the
            # way the runtime places it, correction included.
            stored = table.get('mountOffsets', {}).get(mount)
            if stored:
                offset = offset + np.asarray(stored, dtype=float)
        host_faces = [f for name in host_names for f in vis_objects.get(name, [])]
        if not host_faces:
            continue
        host = vis_verts[np.asarray(host_faces, dtype=np.int64)]
        shell = np.concatenate([
            arm_verts[np.asarray(arm_objects[name], dtype=np.int64)] for name in names
        ]) + offset

        occupancy = Occupancy(sample_triangles(host))
        points = sample_triangles(shell)
        if len(points) > 8000:
            # The search evaluates the shell hundreds of times; a fixed,
            # deterministic subsample keeps that affordable without changing
            # where the optimum sits.
            points = points[np.linspace(0, len(points) - 1, 8000).astype(int)]
        reach = GUN_SEARCH if mount.startswith('gun') else SEARCH
        shift, before, after = fit(occupancy, points, reach)
        slid = displacement((shell + shift).reshape(-1, 3), host.reshape(-1, 3))
        keep = (before < MAX_HEALTHY
                and after >= MIN_SEATED
                and (after - before) >= MIN_GAIN
                and MIN_SHIFT <= np.abs(shift).max() <= MAX_SHIFT
                and slid <= MAX_DISPLACEMENT)
        results[mount] = {
            'before': round(before, 4),
            'after': round(after, 4),
            'shift': [round(float(v), 4) for v in shift],
            'displaced': round(slid, 4),
            'applied': bool(keep),
        }

    corrections = {
        mount: value['shift']
        for mount, value in results.items() if value['applied']
    }
    if apply:
        if corrections:
            table['mountOffsets'] = corrections
        else:
            table.pop('mountOffsets', None)
        armor_json.write_text(json.dumps(table, indent=2, sort_keys=True) + '\n')

    worst = min((v['before'] for v in results.values()), default=1.0)
    return {'id': tank_id, 'mounts': results, 'worstBefore': round(worst, 4),
            'corrections': len(corrections)}


def main():
    args = list(sys.argv[1:])
    apply = '--apply' in args
    if apply:
        args.remove('--apply')
    verify = '--verify' in args
    if verify:
        args.remove('--verify')
        apply = False
    report = ROOT / 'Logs/armor-offsets.json'
    if '--report' in args:
        i = args.index('--report')
        report = Path(args[i + 1])
        del args[i:i + 2]
    wanted = set(args)

    rows = []
    for tank_dir in sorted(p for p in MODELS.iterdir() if p.is_dir()):
        if wanted and tank_dir.name not in wanted:
            continue
        rows.append(audit_tank(tank_dir, apply, verify))
        if not wanted:
            print('.', end='', file=sys.stderr, flush=True)
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(json.dumps(rows, indent=1) + '\n')
    fixed = [r for r in rows if r.get('corrections')]
    print(json.dumps({'tanks': len(rows), 'corrected': len(fixed),
                      'report': str(report)}))
    for row in sorted(fixed, key=lambda r: r.get('worstBefore', 1.0))[:30]:
        print(json.dumps(row))


if __name__ == '__main__':
    main()
