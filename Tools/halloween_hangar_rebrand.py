#!/usr/bin/env python3
"""The port's own art in place of the World of Tanks Blitz logos of the Halloween hangar.

This is pw-mods-tool's `Tools/halloween_hangar_rebrand.py`, so the hangar wears
the same art as the Panzer War port of it. The client hangar shows the Blitz
brand twice; the port replaces exactly these two pieces of art and leaves
everything else as the client ships it:

* `prop_icon_metal.sc2` - the Blitz shield-and-bolt emblem on the hangar wall
  (a hexagonal metal plaque with a red enamel shield and a silver bolt). It
  becomes an extruded Roman numeral VI in the emblem's finish: a polished
  silver 45 degree chamfer and rim, a recessed red enamel inlay in the strokes
  and the client's brushed metal on the side walls. The client's material
  (with its warm flat albedo tint) and the point light that makes the emblem
  glow stay as they are.
* `env_hg_board.sc2` - the billboard whose screen shows the full WORLD of TANKS
  BLITZ logo. Its screen shows `Tools/HalloweenHangarArt/screen.jpg`, whole and
  undistorted, on the image's own border colour; the bezel and the rest of the
  billboard keep the client's texture, upscaled to give the picture twice the
  client's texel density.

`port_halloween_hangar_2026.HangarExport` swaps the emblem mesh right after it
reads the scene and the textures as it decodes them. Arrays here are in the
exporter's conventions: meshes in Unity axes in the local space of their
node, texture pixels as RGBA rows in upload order (row 0 is v = 0).
"""
from __future__ import annotations

import hashlib
from functools import lru_cache
from pathlib import Path

import numpy as np
from PIL import Image, ImageOps

ROOT = Path(__file__).resolve().parents[1]
MAP = 'hangar_halloween_2026'
SIGN_PATH = '/prop_icon_metal.sc2'
SCREEN_PATH = '/env_hg_board.sc2'
SCREEN_IMAGE = 'Tools/HalloweenHangarArt/screen.jpg'
SIGN_TEXTURES = {f'{MAP}/Environment/images_pbr/t_icon_metal_{role}.astc.pvr': role
                 for role in ('bc', 'nm', 'rm', 'misc')}
SCREEN_TEXTURE = f'{MAP}/Objects/images_pbr/t_billboard_bc.astc.pvr'

# The emblem in its node's space (metres, Unity axes): x to the right and y up as
# seen from the front, which faces -z. The client's plaque spans the same depth:
# front plane, the outline where the chamfer meets the side walls, the back
# against the wall (the plaque has no back faces).
FRONT_Z, EDGE_Z, BACK_Z = -0.080972, -0.060972, 0.05015
CHAMFER = EDGE_Z - FRONT_Z
# The client's planar mapping of the plaque front (u = a + b x, v = a + b y),
# which the numeral's front and chamfer keep.
FRONT_U = (0.416, 0.4094)
FRONT_V = (0.6328, -0.3929)
# The brushed metal strip of the client texture the side walls sample: u runs
# from the outline to the back, v along a wall. Texels left of KEEP_U stay the
# client's; the new mesh samples nothing else there.
SIDE_U = (0.058, 0.015)
SIDE_V = (0.26, 0.60)
KEEP_U = 0.08

# The numeral: slab-serif V and I, cap height from Y0 to YT, serifs SERIF tall
# reaching OVERHANG past the strokes. V strokes STROKE wide (measured
# horizontally) from tops V_HALF off the letter's centre to a BOTTOM wide foot;
# the I stem STEM wide; GAP between the letters' serifs.
Y0, YT = -0.40, 1.12
SERIF, OVERHANG = 0.12, 0.07
V_HALF, STROKE, BOTTOM = 0.47, 0.27, 0.14
STEM, GAP = 0.28, 0.09

# Texture finish as a function of the depth inside the strokes (metres from the
# stroke outline; the serifs are solid metal): silver rim, a 20 mm slope down
# into the enamel that darkens through a grey-red groove, then the enamel
# deepening to the client's shield red. Colours, roughness, metalness and
# occlusion are the client's emblem's at the matching places, measured off its
# textures.
FINISH_DEPTH = (0.0, 0.035, 0.040, 0.045, 0.050, 0.058, 0.065, 0.085, 0.105, 0.125)
FINISH_RGB = ((188, 188, 188), (186, 186, 186), (150, 150, 150), (105, 100, 102), (96, 54, 64),
              (90, 48, 58), (92, 31, 50), (104, 16, 53), (118, 12, 58), (131, 10, 62))
FINISH_ROUGH = (78, 82, 120, 160, 165, 152, 142, 128, 118, 110)
FINISH_METAL = (255, 255, 200, 40, 0, 0, 0, 0, 0, 0)
FINISH_AO = (255, 250, 236, 214, 192, 200, 212, 226, 236, 240)
SLOPE = (0.036, 0.054)
SLOPE_DEPTH = 0.016
SIGN_SIZE = 512
SIGN_FORMAT = 'ASTC 6x6'

# The billboard's screen: the client's blue panel inside the bezel, rows
# [57, 500) and columns [12, 247) of the 512 texture. Rows run from the
# viewer's left to the right, columns from the bottom of the screen to its top.
SCREEN_PANEL = (57 / 512, 500 / 512, 12 / 512, 247 / 512)
SCREEN_SIZE = 1024
SCREEN_FORMAT = 'ASTC 4x4'
SCREEN_RING = 8
# Centre of the billboard's screen quad in its node's space; the screen faces -z.
SCREEN_CENTER = (-0.0021, 2.3070, -0.0197)

# Close-ups `Tools/unity.sh hangar480-render` renders of each replacement:
# (distance in front, height above the centre, horizontal field of view).
SIGN_VIEW = (6.0, -0.8, 40.0)
SCREEN_VIEW = (9.0, 0.0, 50.0)


# ------------------------------------------------------------------ outlines
def outlines() -> tuple[list[np.ndarray], list[np.ndarray]]:
    """The V and I outlines, counter-clockwise in the front view, and their strokes
    without serifs (the enamel inlays follow these)."""
    width = 2 * (V_HALF + OVERHANG) + GAP + STEM + 2 * OVERHANG
    cv = -width / 2 + V_HALF + OVERHANG
    ci = width / 2 - STEM / 2 - OVERHANG
    ys = YT - SERIF
    slope = (V_HALF - BOTTOM / 2) / (ys - Y0)
    yn = ys - (V_HALF - STROKE) / slope
    a, s, e, b, h = V_HALF, STROKE, OVERHANG, BOTTOM / 2, STEM / 2
    v = [(cv - b, Y0), (cv + b, Y0), (cv + a, ys), (cv + a + e, ys), (cv + a + e, YT), (cv + a - s - e, YT),
         (cv + a - s - e, ys), (cv + a - s, ys), (cv, yn), (cv - a + s, ys), (cv - a + s + e, ys),
         (cv - a + s + e, YT), (cv - a - e, YT), (cv - a - e, ys), (cv - a, ys)]
    i = [(ci - h - e, Y0), (ci + h + e, Y0), (ci + h + e, Y0 + SERIF), (ci + h, Y0 + SERIF), (ci + h, ys),
         (ci + h + e, ys), (ci + h + e, YT), (ci - h - e, YT), (ci - h - e, ys), (ci - h, ys),
         (ci - h, Y0 + SERIF), (ci - h - e, Y0 + SERIF)]
    v_strokes = [(cv - b, Y0), (cv + b, Y0), (cv + a, ys), (cv + a - s, ys), (cv, yn), (cv - a + s, ys), (cv - a, ys)]
    i_stem = [(ci - h, Y0 + SERIF), (ci + h, Y0 + SERIF), (ci + h, ys), (ci - h, ys)]
    return [np.array(v), np.array(i)], [np.array(v_strokes), np.array(i_stem)]


def area(poly: np.ndarray) -> float:
    x, y = poly[:, 0], poly[:, 1]
    return 0.5 * float(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1)))


def inset(poly: np.ndarray, distance: float) -> np.ndarray:
    """The outline moved inwards by `distance` with mitred corners."""
    out = []
    for k in range(len(poly)):
        d0 = poly[k] - poly[k - 1]
        d1 = poly[(k + 1) % len(poly)] - poly[k]
        n0 = np.array([-d0[1], d0[0]]) / np.linalg.norm(d0)
        n1 = np.array([-d1[1], d1[0]]) / np.linalg.norm(d1)
        out.append(poly[k] + distance * (n0 + n1) / (1.0 + np.dot(n0, n1)))
    out = np.array(out)
    for k in range(len(poly)):
        if np.dot(out[(k + 1) % len(poly)] - out[k], poly[(k + 1) % len(poly)] - poly[k]) <= 0:
            raise SystemExit(f'inset by {distance} folds edge {k} of the numeral outline')
    return out


def ear_clip(poly: np.ndarray) -> list[tuple[int, int, int]]:
    """Counter-clockwise triangles of a simple counter-clockwise polygon."""
    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    def touches(p, a, b, c):
        # Inside or on the border: a vertex on the new diagonal makes it overlap the outline.
        return cross(a, b, p) >= -1e-12 and cross(b, c, p) >= -1e-12 and cross(c, a, p) >= -1e-12

    left = list(range(len(poly)))
    triangles = []
    while len(left) > 3:
        for k in range(len(left)):
            i0, i1, i2 = left[k - 1], left[k], left[(k + 1) % len(left)]
            a, b, c = poly[i0], poly[i1], poly[i2]
            if cross(a, b, c) <= 1e-12:
                continue
            if any(touches(poly[j], a, b, c) for j in left if j not in (i0, i1, i2)):
                continue
            triangles.append((i0, i1, i2))
            left.pop(k)
            break
        else:
            raise SystemExit('the numeral outline cannot be triangulated')
    triangles.append(tuple(left))
    return triangles


def signed_distance(x: np.ndarray, y: np.ndarray, polygons) -> np.ndarray:
    """Distance to the nearest outline, positive inside one of the (disjoint) polygons."""
    result = np.full(x.shape, -np.inf)
    for poly in polygons:
        distance = np.full(x.shape, np.inf)
        inside = np.zeros(x.shape, bool)
        for k in range(len(poly)):
            (ax, ay), (bx, by) = poly[k], poly[(k + 1) % len(poly)]
            ex, ey = bx - ax, by - ay
            t = np.clip(((x - ax) * ex + (y - ay) * ey) / (ex * ex + ey * ey), 0.0, 1.0)
            distance = np.minimum(distance, np.hypot(x - ax - t * ex, y - ay - t * ey))
            if ay != by:
                inside ^= ((ay > y) != (by > y)) & (x < ax + (y - ay) * ex / ey)
        result = np.maximum(result, np.where(inside, distance, -distance))
    return result


# ------------------------------------------------------------------ mesh
def front_uv(points: np.ndarray) -> np.ndarray:
    return np.stack([FRONT_U[0] + FRONT_U[1] * points[:, 0], FRONT_V[0] + FRONT_V[1] * points[:, 1]], axis=1)


def sign_mesh() -> dict:
    """The numeral VI with flat-shaded faces: front, chamfer and side walls.

    Tangents follow the client's frames: the tangent points along +u, the
    binormal (UV2) along -v, and cross(normal, tangent) * w is the binormal."""
    streams = {k: [] for k in ('positions', 'normals', 'tangents', 'binormals', 'uv0')}
    triangles = []

    def face(points, normal, uv, tangent, binormal, fan):
        base = sum(len(p) for p in streams['positions'])
        normal = np.asarray(normal, float) / np.linalg.norm(normal)
        tangent = np.asarray(tangent, float)
        tangent = tangent - normal * np.dot(normal, tangent)
        tangent /= np.linalg.norm(tangent)
        binormal = np.asarray(binormal, float)
        binormal = binormal - normal * np.dot(normal, binormal)
        binormal /= np.linalg.norm(binormal)
        w = 1.0 if np.dot(np.cross(normal, tangent), binormal) >= 0 else -1.0
        n = len(points)
        streams['positions'].append(np.asarray(points, float))
        streams['normals'].append(np.tile(normal, (n, 1)))
        streams['tangents'].append(np.tile(np.append(tangent, w), (n, 1)))
        streams['binormals'].append(np.tile(binormal, (n, 1)))
        streams['uv0'].append(np.asarray(uv, float))
        for a, b, c in fan:
            p = np.asarray(points, float)
            if np.dot(np.cross(p[b] - p[a], p[c] - p[a]), normal) < 0:
                b, c = c, b
            triangles.append((base + a, base + b, base + c))

    shapes, _ = outlines()
    for outline in shapes:
        if area(outline) <= 0:
            raise SystemExit('numeral outline is not counter-clockwise')
        front = inset(outline, CHAMFER)
        flat = np.column_stack([front, np.full(len(front), FRONT_Z)])
        face(flat, (0, 0, -1), front_uv(front), (1, 0, 0), (0, 1, 0), ear_clip(front))
        longest = max(np.linalg.norm(outline[(k + 1) % len(outline)] - outline[k]) for k in range(len(outline)))
        for k in range(len(outline)):
            o0, o1 = outline[k], outline[(k + 1) % len(outline)]
            f0, f1 = front[k], front[(k + 1) % len(front)]
            along = (o1 - o0) / np.linalg.norm(o1 - o0)
            out = np.array([along[1], -along[0]])
            # Chamfer: from the front's edge out to the outline, 45 degrees back.
            normal = np.array([out[0] * CHAMFER, out[1] * CHAMFER, -CHAMFER])
            quad = np.array([[*o0, EDGE_Z], [*o1, EDGE_Z], [*f1, FRONT_Z], [*f0, FRONT_Z]])
            nz = normal[2] / np.linalg.norm(normal)
            n = normal / np.linalg.norm(normal)
            face(quad, n, front_uv(quad[:, :2]), (1, 0, -n[0] / nz), (0, 1, -n[1] / nz), ((0, 1, 2), (0, 2, 3)))
            # Side wall: the outline straight back to the wall, on the brushed strip.
            length = np.linalg.norm(o1 - o0)
            v0, v1 = SIDE_V[0], SIDE_V[0] + (SIDE_V[1] - SIDE_V[0]) * length / longest
            wall = np.array([[*o0, EDGE_Z], [*o1, EDGE_Z], [*o1, BACK_Z], [*o0, BACK_Z]])
            uv = np.array([[SIDE_U[0], v0], [SIDE_U[0], v1], [SIDE_U[1], v1], [SIDE_U[1], v0]])
            # +u runs towards the front (SIDE_U falls with depth); the binormal is -v.
            face(wall, (out[0], out[1], 0), uv, (0, 0, -1), (-along[0], -along[1], 0), ((0, 1, 2), (0, 2, 3)))

    mesh = {k: np.concatenate(v).astype(np.float32) for k, v in streams.items()}
    mesh['uv1'] = mesh['uv0'].copy()
    mesh['triangles'] = np.array(triangles, dtype=np.uint32).reshape(-1)
    return mesh


# ------------------------------------------------------------------ textures
def _smooth(edge0, edge1, x):
    t = np.clip((x - edge0) / (edge1 - edge0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def _noise(size: int, cells: int, seed: int) -> np.ndarray:
    """Smooth deterministic value noise in [-1, 1]."""
    rng = np.random.default_rng(seed)
    coarse = Image.fromarray(rng.uniform(-1.0, 1.0, (cells, cells)).astype(np.float32), 'F')
    return np.asarray(coarse.resize((size, size), Image.BICUBIC), dtype=np.float32)


@lru_cache(maxsize=None)
def _fields(size: int):
    rows, cols = np.mgrid[0:size, 0:size]
    u, v = (cols + 0.5) / size, (rows + 0.5) / size
    x = (u - FRONT_U[0]) / FRONT_U[1]
    y = (v - FRONT_V[0]) / FRONT_V[1]
    _, strokes = outlines()
    depth = signed_distance(x, y, strokes)
    grain = 0.6 * _noise(size, 96, 6) + 0.4 * _noise(size, 24, 21)
    return u, depth, grain


def _client(pixels: np.ndarray, size: int) -> np.ndarray:
    return np.asarray(Image.fromarray(pixels, 'RGBA').resize((size, size), Image.BICUBIC))


def _finish(values, depth):
    return np.interp(depth, FINISH_DEPTH, values)


def sign_texture(role: str, client: np.ndarray, size: int = SIGN_SIZE) -> np.ndarray:
    """One of the emblem's textures (bc, nm, rm, misc) painted for the numeral; the
    client's brushed strip the side walls sample is kept."""
    u, depth, grain = _fields(size)
    out = np.empty((size, size, 4), np.float64)
    out[:, :, 3] = 255.0
    if role == 'bc':
        for k in range(3):
            out[:, :, k] = _finish([c[k] for c in FINISH_RGB], depth)
        metal = depth < FINISH_DEPTH[3]
        out[:, :, :3] += np.where(metal, 5.0, 3.0)[:, :, None] * grain[:, :, None]
    elif role == 'nm':
        height = -SLOPE_DEPTH * _smooth(SLOPE[0], SLOPE[1], depth)
        # metres per texel along x and y; rows run down the front (y falls).
        dx, dy = 1.0 / (FRONT_U[1] * size), 1.0 / (-FRONT_V[1] * size)
        grow, gcol = np.gradient(height)
        hx, hy = gcol / dx, -grow / dy
        norm = np.sqrt(hx * hx + hy * hy + 1.0)
        out[:, :, 0] = out[:, :, 1] = out[:, :, 2] = 128.0 + 127.0 * (-hx / norm)
        out[:, :, 3] = 128.0 + 127.0 * (-hy / norm)
    elif role == 'rm':
        out[:, :, 0] = out[:, :, 1] = out[:, :, 2] = _finish(FINISH_ROUGH, depth) + 6.0 * grain
        out[:, :, 3] = _finish(FINISH_METAL, depth)
    elif role == 'misc':
        out[:, :, 0] = out[:, :, 1] = out[:, :, 2] = _finish(FINISH_AO, depth)
    else:
        raise SystemExit(f'unknown emblem texture {role}')
    out = np.clip(np.rint(out), 0, 255).astype(np.uint8)
    keep = u < KEEP_U
    out[keep] = _client(client, size)[keep]
    return out


def screen_texture(client: np.ndarray, picture: Image.Image, size: int = SCREEN_SIZE) -> np.ndarray:
    """The billboard texture upscaled to `size` with `picture` on the screen panel."""
    out = np.array(Image.fromarray(client, 'RGBA').resize((size, size), Image.LANCZOS))
    r0, r1 = round(SCREEN_PANEL[0] * size), round(SCREEN_PANEL[1] * size)
    c0, c1 = round(SCREEN_PANEL[2] * size), round(SCREEN_PANEL[3] * size)
    width, height = r1 - r0, c1 - c0
    picture = ImageOps.exif_transpose(picture).convert('RGB')
    rgb = np.asarray(picture)
    ring = np.concatenate([rgb[:SCREEN_RING].reshape(-1, 3), rgb[-SCREEN_RING:].reshape(-1, 3),
                           rgb[:, :SCREEN_RING].reshape(-1, 3), rgb[:, -SCREEN_RING:].reshape(-1, 3)])
    scale = min(width / picture.width, height / picture.height)
    w, h = round(picture.width * scale), round(picture.height * scale)
    canvas = np.empty((height, width, 3), np.uint8)
    canvas[:] = np.median(ring, axis=0).astype(np.uint8)
    x0, y0 = (width - w) // 2, (height - h) // 2
    canvas[y0:y0 + h, x0:x0 + w] = np.asarray(picture.resize((w, h), Image.LANCZOS))
    # Upright picture -> panel: its x runs down the rows, its y up the columns.
    out[r0:r1, c0:c1, :3] = np.rot90(canvas, -1)
    out[r0:r1, c0:c1, 3] = 255
    return out


def screen_picture() -> Image.Image:
    return Image.open(ROOT / SCREEN_IMAGE)


def screen_digest() -> str:
    return hashlib.sha256((ROOT / SCREEN_IMAGE).read_bytes()).hexdigest()


def replacement(source: str, pixels: np.ndarray):
    """(pixels, ASTC block the port compresses them with) of a client texture the
    port replaces, None for the client's own."""
    if source in SIGN_TEXTURES:
        return sign_texture(SIGN_TEXTURES[source], pixels), SIGN_FORMAT
    if source == SCREEN_TEXTURE:
        return screen_texture(pixels, screen_picture()), SCREEN_FORMAT
    return None


# ------------------------------------------------------------------ views
def close_up(world: np.ndarray, center, normal, distance: float, lift: float, fov: float) -> dict:
    """A camera `distance` in front of a face (node-local centre and normal, `world`
    the node's Unity world matrix), `lift` metres up, looking at the centre."""
    target = (world @ np.append(np.asarray(center, float), 1.0))[:3]
    facing = world[:3, :3] @ np.asarray(normal, float)
    facing /= np.linalg.norm(facing)
    position = target + facing * distance + np.array([0.0, lift, 0.0])
    forward = (target - position) / np.linalg.norm(target - position)
    return {'position': [round(float(v), 4) for v in position], 'forward': [round(float(v), 5) for v in forward],
            'fov': fov}
