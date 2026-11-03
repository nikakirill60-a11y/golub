#!/usr/bin/env python3
"""Rasterised armour-vs-model audit.

Projects the visible tank mesh and the armour plates orthographically and
measures how much of the armour falls outside the visible silhouette. Unlike
``audit_armor_alignment.py`` (bounding boxes only) this catches armour shells
that are the right size but the wrong *shape* - e.g. a box-shaped collision
hull standing in for the real hull armour.

Usage:
    python3 Tools/audit_armor_silhouette.py [TANK ...] [--png OUTDIR]
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

RESOLUTION = 256
VIEWS = {
    'front': (0, 1),   # x, y
    'side': (2, 1),    # z, y
    'top': (0, 2),     # x, z
}


def read_obj(path: Path):
    """Return {object_name: (vertices ndarray, faces list[tuple])}."""
    verts: list[tuple[float, float, float]] = []
    objects: dict[str, list[tuple[int, int, int]]] = {}
    current = None
    with path.open(encoding='utf-8') as handle:
        for line in handle:
            if line.startswith('v '):
                x, y, z = (float(v) for v in line.split()[1:4])
                verts.append((x, y, z))
            elif line.startswith('o '):
                current = line[2:].strip()
                objects.setdefault(current, [])
            elif line.startswith('f '):
                idx = []
                for chunk in line.split()[1:]:
                    value = int(chunk.split('/')[0])
                    idx.append(value - 1 if value > 0 else len(verts) + value)
                for i in range(1, len(idx) - 1):
                    objects.setdefault(current or '', []).append((idx[0], idx[i], idx[i + 1]))
    array = np.asarray(verts, dtype=np.float64)
    return array, objects


def raster(tris: np.ndarray, axes, origin, scale, size=RESOLUTION) -> np.ndarray:
    """Fill 2D triangles into a boolean mask (simple scanline barycentric)."""
    mask = np.zeros((size, size), dtype=bool)
    if len(tris) == 0:
        return mask
    u = (tris[:, :, axes[0]] - origin[0]) * scale
    v = (tris[:, :, axes[1]] - origin[1]) * scale
    pts = np.stack([u, size - 1 - v], axis=2)  # (n, 3, 2)
    xs = np.arange(size)
    ys = np.arange(size)
    for tri in pts:
        x0 = max(int(np.floor(tri[:, 0].min())), 0)
        x1 = min(int(np.ceil(tri[:, 0].max())) + 1, size)
        y0 = max(int(np.floor(tri[:, 1].min())), 0)
        y1 = min(int(np.ceil(tri[:, 1].max())) + 1, size)
        if x1 <= x0 or y1 <= y0:
            continue
        gx, gy = np.meshgrid(xs[x0:x1] + 0.5, ys[y0:y1] + 0.5)
        ax, ay = tri[0]
        bx, by = tri[1]
        cx, cy = tri[2]
        det = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(det) < 1e-12:
            continue
        w0 = ((by - cy) * (gx - cx) + (cx - bx) * (gy - cy)) / det
        w1 = ((cy - ay) * (gx - cx) + (ax - cx) * (gy - cy)) / det
        w2 = 1.0 - w0 - w1
        inside = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
        mask[y0:y1, x0:x1] |= inside
    return mask


def dilate(mask: np.ndarray, radius: int) -> np.ndarray:
    out = mask.copy()
    for _ in range(radius):
        grown = out.copy()
        grown[1:, :] |= out[:-1, :]
        grown[:-1, :] |= out[1:, :]
        grown[:, 1:] |= out[:, :-1]
        grown[:, :-1] |= out[:, 1:]
        out = grown
    return out


def load_tank(tank_dir: Path):
    tank_id = tank_dir.name
    data_id = tank_id.lower()
    paths = {
        'vis': tank_dir / f'{tank_id}.obj',
        'armor': tank_dir / f'{tank_id}_armor.obj',
        'phys': tank_dir / f'{tank_id}_phys.obj',
        'armor_json': DATA / f'{data_id}_armor.json',
        'nodes_json': DATA / f'{data_id}_nodes.json',
    }
    for path in paths.values():
        if not path.exists():
            return None, f'missing {path.name}'
    return paths, None


def plate_triangles(paths):
    vis_verts, vis_objs = read_obj(paths['vis'])
    arm_verts, arm_objs = read_obj(paths['armor'])
    plates = json.loads(paths['armor_json'].read_text())['plates']
    nodes = json.loads(paths['nodes_json'].read_text())
    pivots = nodes['nodes']
    rig = nodes['rig']
    turret, gun = rig['turret'], rig['gun']

    def pivot(name):
        entry = pivots.get(name)
        return np.asarray(entry['world'] if entry else (0.0, 0.0, 0.0), dtype=np.float64)

    vis_tris = np.concatenate([
        vis_verts[np.asarray(faces, dtype=np.int64)]
        for faces in vis_objs.values() if faces
    ]) if vis_objs else np.zeros((0, 3, 3))

    per_plate = {}
    for plate in plates:
        name = plate['name']
        mount = plate['mount']
        faces = arm_objs.get(name)
        if not faces:
            continue
        offset = np.zeros(3) if mount == 'hull' else pivot(mount)
        per_plate[name] = arm_verts[np.asarray(faces, dtype=np.int64)] + offset
    return vis_tris, per_plate


def audit(tank_dir: Path, png_dir: Path | None = None):
    tank_id = tank_dir.name
    paths, error = load_tank(tank_dir)
    if error:
        return {'id': tank_id, 'error': error}
    vis_tris, per_plate = plate_triangles(paths)
    if len(vis_tris) == 0 or not per_plate:
        return {'id': tank_id, 'error': 'no geometry'}
    armor_tris = np.concatenate(list(per_plate.values()))

    allpts = np.concatenate([vis_tris.reshape(-1, 3), armor_tris.reshape(-1, 3)])
    lo, hi = allpts.min(axis=0), allpts.max(axis=0)
    span = float((hi - lo).max()) or 1.0
    scale = (RESOLUTION - 8) / span

    result = {'id': tank_id, 'views': {}, 'worst_plate': None}
    worst = (0.0, None, None)
    for view, axes in VIEWS.items():
        origin = (lo[axes[0]] - 4 / scale, lo[axes[1]] - 4 / scale)
        vis_mask = raster(vis_tris, axes, origin, scale)
        tolerant = dilate(vis_mask, 2)
        arm_mask = raster(armor_tris, axes, origin, scale)
        leak_mask = arm_mask & ~tolerant
        arm_px = int(arm_mask.sum())
        leak = float(leak_mask.sum()) / arm_px if arm_px else 0.0
        cover = float((arm_mask & vis_mask).sum()) / float(vis_mask.sum() or 1)
        result['views'][view] = {'leak': round(leak, 4), 'coverage': round(cover, 4)}
        for name, tris in per_plate.items():
            pm = raster(tris, axes, origin, scale)
            px = int(pm.sum())
            if px < 16:
                continue
            pleak = float((pm & ~tolerant).sum()) / px
            if pleak > worst[0]:
                worst = (pleak, name, view)
        if png_dir is not None:
            from PIL import Image
            rgb = np.zeros((RESOLUTION, RESOLUTION, 3), dtype=np.uint8)
            rgb[vis_mask] = (60, 60, 60)
            rgb[arm_mask] = (0, 170, 0)
            rgb[leak_mask] = (230, 0, 0)
            png_dir.mkdir(parents=True, exist_ok=True)
            Image.fromarray(rgb).save(png_dir / f'{tank_id}-{view}.png')
    result['worst_plate'] = {'name': worst[1], 'view': worst[2], 'leak': round(worst[0], 4)}
    result['leak'] = round(max(v['leak'] for v in result['views'].values()), 4)
    return result


def main():
    args = [a for a in sys.argv[1:]]
    png_dir = None
    if '--png' in args:
        i = args.index('--png')
        png_dir = Path(args[i + 1])
        del args[i:i + 2]
    wanted = set(args)
    rows = []
    for tank_dir in sorted(MODELS.iterdir()):
        if not tank_dir.is_dir() or (wanted and tank_dir.name not in wanted):
            continue
        rows.append(audit(tank_dir, png_dir))
        if not wanted:
            print('.', end='', file=sys.stderr, flush=True)
    out = ROOT / 'Logs/armor-silhouette.json'
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(rows, indent=1, ensure_ascii=False))
    bad = [r for r in rows if r.get('error') or r.get('leak', 0) > 0.05]
    print(json.dumps({'tanks': len(rows), 'suspect': len(bad)}))
    for row in sorted(bad, key=lambda r: -(r.get('leak') or 0))[:40]:
        print(json.dumps(row, ensure_ascii=False))


if __name__ == '__main__':
    main()
