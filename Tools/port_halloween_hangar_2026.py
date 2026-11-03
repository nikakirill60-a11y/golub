#!/usr/bin/env python3
"""Export the WoT Blitz 11.21 Halloween hangar (`hangar_halloween_2026`) for the client hangar.

The decoding is pw-mods-tool's `Tools/prepare_halloween_hangar.py` (the garage-map
port of the same hangar), unchanged where the two ports agree. The Blitz client
dump is read-only: the map's DVPL-packed scene (`.sc2` and its geometry `.scg`),
ASTC textures, skeletal clips and the client's `.material` documents are decoded
in memory and written under `Assets/Blitz/Resources/HalloweenHangar2026/` as data
`HalloweenHangarScene` builds at run time:

* `scene.json` - nodes (one per entity and skeleton joint, with Unity local
  TRS), renderers, materials as the client resolves them (template, presets,
  defines, render state, textures, properties), textures, point lights, clips,
  state switchers, sounds, particle effects, the spawn point, the editor
  camera and the scene environment (sun, shadows, IBL, fog, the vehicle's
  ambient light);
* `meshes.bytes` - vertex streams, submesh indices and bind poses;
* `clips.bytes` - joint curves of every skeletal clip;
* `Textures/*.png` - decoded textures; rows are flipped so the first row the
  client uploads is Unity's v = 0 and the authored UVs stay valid;
* `Sprites/*.png` - particle sprites rebuilt as Unity texture sheets in the
  image's own orientation (particle meshes get v flipped to match);
* `Cubemaps/<name>.bytes` - the IBL cube maps as raw RGBA8 in
  `Cubemap.SetPixelData` order: mip after mip, the six faces of a mip in the
  client's (GL) order, which is Unity's `CubemapFace` order, each face from its
  first uploaded row (t = 0) on. `ibl_specular_unity.bytes` is the specular map
  resampled so that a Unity world direction shows what the client's environment
  lookup shows; it is the vehicle's reflection;
* `dfg_lut.bytes` - the split-sum DFG table the client renders at start-up, as
  RG half floats in `Texture2D.SetPixelData` order;
* `import.json` - importer settings of every texture and sprite (sampler state,
  sRGB, mips, ASTC block) for the editor's `HalloweenHangarImport`;
* `report.json` - counts and checks of this export, `provenance.json` - every
  source file read with its SHA-256.

Like the garage-map port, the hangar shows the port's own art in place of the
client's two World of Tanks Blitz logos (`halloween_hangar_rebrand`): the wall
emblem becomes a Roman numeral VI and the billboard screen shows
`Tools/HalloweenHangarArt/screen.jpg`. The records say so: `rebrand` in
scene.json and report.json, `replaced` on the texture records (whose `format`
is then the ASTC block the port compresses its art with) and `art` in
provenance.json with the picture's SHA-256.

Coordinates: DAVA is right-handed with Z up, Unity left-handed with Y up, so a
DAVA point (x, y, z) is the Unity point (x, z, y). The swap is a reflection:
triangles are rewound, quaternions become (-x, -z, -y, w) and a local scale
(sx, sy, sz) becomes (sx, sz, sy).
"""
from __future__ import annotations

import argparse
import collections
import hashlib
import json
import math
import posixpath
import shutil
import struct
import sys
import tempfile
from pathlib import Path

import numpy as np
import yaml
from PIL import Image

import halloween_hangar_rebrand as rebrand

ROOT = Path(__file__).resolve().parents[1]
MAP = 'hangar_halloween_2026'
OUTPUT = ROOT / 'Assets/Blitz/Resources/HalloweenHangar2026'
CLIENT = ROOT.parent / 'blitz1121files'
DAVA_TOOLS = ROOT.parent / 'blitz-legacy-godot/tools'

# Subtrees the garage never shows: the reward slot with its cameras, the
# battle-result counter sounds and the click of the radio's tap interaction.
SKIPPED_SUBTREES = {'glory_corner', 'parking_place', 'Sound_counters', 'Klick'}
SELF = '*** Self ***'
ACTION_PARTICLE, ACTION_SOUND, ACTION_STOP, ACTION_MOTION = 1, 2, 5, 6
EVENT_ADDED = 1
RO_VISIBLE = 1
RO_CAST_SHADOW = 1 << 13
NO_PARENT_JOINT = 16777215
MAX_POINT_LIGHTS = 2
LIGHT_POINT = 2
# A batch whose material skins nothing is drawn in the entity's space: the port
# binds it to the renderer's own transform.
ENTITY_BONE = -2
# The port renders what the client renders on its best material quality.
QUALITY = 'ULTRA'
DFG_SIZE = 256
DFG_SAMPLES = 4096
# The client samples a texture with no authored sampler state with wrap,
# linear min/mag/mip filtering and no anisotropy.
DEFAULT_SAMPLER = 2103616

# DAVA eVertexFormat bits and the record each one adds to a vertex.
VERTEX_STREAMS = (
    (0, 'position', '<f4', 3),
    (1, 'normal', '<f4', 3),
    (2, 'color', 'u1', 4),
    (3, 'uv0', '<f4', 2),
    (4, 'uv1', '<f4', 2),
    (5, 'uv2', '<f4', 2),
    (6, 'uv3', '<f4', 2),
    (7, 'tangent', '<f4', 3),
    (8, 'binormal', '<f4', 3),
    (9, 'hard_joint', '<f4', 1),
    (10, 'pivot', '<f4', 4),
    (11, 'pivot_deprecated', '<f4', 3),
    (12, 'flexibility', '<f4', 1),
    (13, 'angle_sin_cos', '<f4', 2),
    (14, 'joint_index', '<f4', 4),
    (15, 'joint_weight', '<f4', 4),
)
PRIMITIVE_TRIANGLE_STRIP = 2

# meshes.bytes stream bits (position is always present).
MESH_NORMAL, MESH_TANGENT, MESH_COLOR, MESH_UV0, MESH_UV1 = 1, 2, 4, 8, 16
MESH_BINORMAL, MESH_UV3, MESH_UV4, MESH_SKIN, MESH_BINDFRAME = 32, 64, 128, 256, 512

# Particle emitters: the variant the client picks on its best quality, the layer
# kinds the port renders and the textures a layer samples
# (path field, enabling flag, sampler field, slot).
PARTICLE_QUALITY = ('Quality: [HIGH,HIGHEST]', 'Default')
PARTICLE_LAYER_TYPES = {'particles', 'mesh'}
PARTICLE_TEXTURES = (
    ('spritePath', None, 'spriteSampler', 'sprite'),
    ('maskSpritePath', 'enableMask', 'maskSampler', 'mask'),
    ('flowmapPath', 'enableFlow', 'flowmapSampler', 'flowmap'),
    ('noisePath', 'enableNoise', 'noiseSampler', 'noise'),
    ('alphaRemapPath', 'enableAlphaRemap', 'alphaRemapSampler', 'alphaRemap'),
    ('vertexAnimationPath', 'enableVertexAnimation', 'vertexAnimationSampler', 'vertexAnimation'),
)
PARTICLE_STRUCTURE = {'layers', 'forces', 'simplifiedForces', 'innerEmitter'}
UINT32_MAX = 0xFFFFFFFF

# Client shader -> port shader and the material samplers the shader declares
# (pbr-fp.sl, materials-fp.sl, skyobject-materials-fp.sl with their headers).
# Global samplers (specularBRDF, blueNoise64x64, perlinNoise, shadow maps,
# IBL cubemaps) are bound by the renderer, not by materials.
SHADERS = {
    'pbr': ('PBR', {
        'baseColorMap', 'baseNormalMap', 'baseRMMap', 'maskMap', 'miscMap', 'pbrLightmap',
        'pbrDetailColorMap', 'pbrDetailNormalMap', 'pbrDetailRoughnessAOMap',
        'pbrDecalColorRoughnessMap', 'pbrDecalLightmap', 'dirtHeightMap', 'dirtNormalMap',
        'outlandAlbedoBlendMap', 'outlandNormalMap'}),
    'materials': ('Textured', {
        'albedo', 'alphamask', 'decal', 'decalmask', 'decaltexture', 'detail', 'envReflectionMask',
        'flowmap', 'lightmap', 'outlandDiffuseBlendMap', 'outlandTileTexture'}),
    'skyobject-materials': ('Sky', {'albedo', 'flowmap'}),
}
DEFAULT_STATE = {'depthTest': True, 'depthWrite': True, 'cullMode': 'FACE_BACK',
                 'depthFunc': 'CMP_LESS', 'blend': False}

dava = dvpl = pvr2png = dava_anim = texture2ddecoder = None


def load_tools(path: Path) -> None:
    """The DAVA readers of blitz-legacy-godot (sc2/scg archives, DVPL, PVR, clips)."""
    global dava, dvpl, pvr2png, dava_anim, texture2ddecoder
    sys.path.insert(0, str(path))
    import dava as _dava
    import dvpl as _dvpl
    import pvr2png as _pvr2png
    import dava_anim as _dava_anim
    import texture2ddecoder as _t2d
    dava, dvpl, pvr2png, dava_anim, texture2ddecoder = _dava, _dvpl, _pvr2png, _dava_anim, _t2d


# ------------------------------------------------------------------ source
class Source:
    """Read-only access to the client dump; every file read is fingerprinted."""

    def __init__(self, root: Path):
        self.root = root.resolve()
        self.packs = self.root / 'files/packs'
        self.maps = self.packs / '3d/Maps'
        self.map_dir = self.maps / MAP
        self.apk = self.root / 'from_apk'
        self.files: dict[str, str] = {}

    def exists(self, path: Path) -> bool:
        return path.with_name(path.name + '.dvpl').is_file() or path.is_file()

    def read(self, path: Path) -> bytes:
        packed = path.with_name(path.name + '.dvpl')
        source = packed if packed.is_file() else path
        blob = source.read_bytes()
        self.files[source.relative_to(self.root).as_posix()] = hashlib.sha256(blob).hexdigest()
        if blob[-4:] == b'DVPL':
            return dvpl.unpack_bytes(blob)
        return blob

    def relative(self, path: Path) -> str:
        """A source path as the map tree names it (`<map>/...`, `00_shared_content/...`)."""
        for base in (self.maps, self.root):
            try:
                return path.relative_to(base).as_posix()
            except ValueError:
                continue
        return path.as_posix()

    def map_path(self, reference: str) -> Path:
        """A scene reference: relative to the map directory or `~res:/` rooted."""
        if reference.startswith('~res:/'):
            relative = reference[len('~res:/'):]
            for base in (self.packs, self.apk):
                if self.exists(base / relative):
                    return base / relative
            return self.packs / relative
        return Path(posixpath.normpath(posixpath.join(self.map_dir.as_posix(), reference)))


# ------------------------------------------------------------------ archives
def items(value) -> list:
    if value is None:
        return []
    if isinstance(value, list):
        return value
    if isinstance(value, dict):
        return [value[key] for key in sorted(value)]
    return [value]


def components(entity: dict, typename: str | None = None) -> list[dict]:
    return [c for c in items(entity.get('components')) if isinstance(c, dict)
            and (typename is None or c.get('comp.typename') == typename)]


def component(entity: dict, typename: str) -> dict | None:
    found = components(entity, typename)
    return found[0] if found else None


def children(entity: dict) -> list[dict]:
    return [c for c in items(entity.get('#hierarchy')) if isinstance(c, dict)]


def actions(entity: dict) -> list[dict]:
    return [a for c in components(entity, 'ActionComponent') for a in items(c) if isinstance(a, dict)]


def find_by_name(entity: dict, name: str) -> dict | None:
    """Entity::FindByName: the entity itself or the first descendant of that name."""
    if str(entity.get('name')) == name:
        return entity
    for child in children(entity):
        found = find_by_name(child, name)
        if found is not None:
            return found
    return None


def floats(blob) -> list[float]:
    return [float(v) for v in blob] if isinstance(blob, (list, tuple)) else \
        list(struct.unpack('<%df' % (len(blob) // 4), bytes(blob)))


def material_property(node: dict, name: str) -> list[float]:
    """One NMaterial property blob: a type byte (0..3: float..vec4), a count and the floats."""
    blob = (node.get('properties') or {}).get(name)
    if not isinstance(blob, (bytes, bytearray)) or len(blob) < 9:
        return []
    size = (blob[0] + 1) if blob[0] <= 3 else 0
    count = int.from_bytes(blob[1:5], 'little')
    total = size * count
    if not total or len(blob) < 5 + 4 * total:
        return []
    return list(struct.unpack('<%df' % total, blob[5:5 + 4 * total]))


def color(light: dict, prefix: str) -> list[float]:
    return [float(light.get(f'{prefix}.{k}', 0.0)) for k in 'rgb']


# ------------------------------------------------------------------ geometry
def decode_vertices(group: dict) -> dict[str, np.ndarray]:
    """Every stream of a PolygonGroup; the interleaved record follows the format bits."""
    vertex_format = int(group['vertexFormat'])
    count = int(group['vertexCount'])
    blob = bytes(group['vertices'])
    fields = [(name, dtype, (width,)) for bit, name, dtype, width in VERTEX_STREAMS if vertex_format >> bit & 1]
    if vertex_format >> 16:
        raise SystemExit(f'unknown vertex format bits {vertex_format:#x}')
    record = np.dtype(fields)
    if count and len(blob) != record.itemsize * count:
        raise SystemExit(f'vertex format {vertex_format} expects {record.itemsize} bytes a vertex')
    data = np.frombuffer(blob, dtype=record, count=count)
    return {name: np.array(data[name]) for name, _, _ in fields}


def strip_triangles(strip: np.ndarray) -> np.ndarray:
    if strip.size < 3:
        return np.zeros(0, dtype=np.int64)
    a, b, c = strip[:-2], strip[1:-1], strip[2:]
    odd = (np.arange(a.size) % 2) == 1
    triangles = np.stack([np.where(odd, b, a), np.where(odd, a, b), c], axis=1)
    keep = (triangles[:, 0] != triangles[:, 1]) & (triangles[:, 1] != triangles[:, 2]) & (triangles[:, 0] != triangles[:, 2])
    return triangles[keep].reshape(-1)


def decode_indices(group: dict) -> np.ndarray:
    count = int(group['indexCount'])
    dtype = '<u2' if int(group.get('indexFormat', 0)) == 0 else '<u4'
    indices = np.frombuffer(bytes(group['indices']), dtype=dtype, count=count).astype(np.int64)
    if int(group.get('rhi_primitiveType', 1)) == PRIMITIVE_TRIANGLE_STRIP:
        return strip_triangles(indices)
    return indices


def unity_streams(v: dict) -> dict:
    """A polygon group's streams in Unity axes; absent streams get neutral values.

    The tangent's w is the handedness Unity rebuilds the binormal with:
    cross(normal, tangent) * w points along the authored binormal.
    """
    n = len(v['position'])

    def swap(a):
        return np.asarray(a, dtype=np.float32)[:, [0, 2, 1]]

    if 'tangent' in v:
        if 'binormal' in v and 'normal' in v:
            handed = np.einsum('ij,ij->i', np.cross(v['normal'].astype(np.float64), v['tangent'].astype(np.float64)),
                               v['binormal'].astype(np.float64))
            w = np.where(handed >= 0.0, -1.0, 1.0).astype(np.float32)
        else:
            w = np.full(n, -1.0, np.float32)
        tangents = np.concatenate([swap(v['tangent']), w[:, None]], axis=1)
    else:
        tangents = np.tile(np.float32([1, 0, 0, 1]), (n, 1))
    return {
        'count': n, 'present': set(v),
        'positions': swap(v['position']),
        'normals': swap(v['normal']) if 'normal' in v else np.tile(np.float32([0, 1, 0]), (n, 1)),
        'tangents': tangents,
        'colors': v['color'].astype(np.uint8) if 'color' in v else np.full((n, 4), 255, np.uint8),
        'uv0': v['uv0'].astype(np.float32) if 'uv0' in v else np.zeros((n, 2), np.float32),
        'uv1': v['uv1'].astype(np.float32) if 'uv1' in v else np.zeros((n, 2), np.float32),
        'binormals': swap(v['binormal']) if 'binormal' in v else np.zeros((n, 3), np.float32),
        'uv3': v['uv2'].astype(np.float32) if 'uv2' in v else np.zeros((n, 2), np.float32),
        'uv4': v['uv3'].astype(np.float32) if 'uv3' in v else np.zeros((n, 2), np.float32),
    }


def stream_bits(present: set) -> int:
    bits = MESH_NORMAL | MESH_UV0
    for name, bit in (('tangent', MESH_TANGENT), ('color', MESH_COLOR), ('uv1', MESH_UV1),
                      ('binormal', MESH_BINORMAL), ('uv2', MESH_UV3), ('uv3', MESH_UV4)):
        if name in present:
            bits |= bit
    return bits


# ------------------------------------------------------------------ particle values
def node_id(value) -> int:
    return int.from_bytes(bytes(value), 'little') if isinstance(value, (bytes, bytearray)) else int(value)


def line_key(interp, time, value, in_slope=0.0, out_slope=0.0) -> dict:
    return {'interp': int(interp), 'time': float(time), 'value': float(value),
            'inSlope': float(in_slope), 'outSlope': float(out_slope)}


def constant_line(name: str, values) -> dict:
    return {'name': name, 'dims': len(values), 'parameter': '', 'fallback': [],
            'channels': [{'keys': [line_key(1, 0.0, v)]} for v in values]}


def is_keyframed(value) -> bool:
    return isinstance(value, list) and len(value) >= 2 and value[0] == 2 and \
        all(isinstance(channel, list) for channel in value[1:])


def property_line(name: str, value) -> dict | None:
    """A particle PropertyLine: `[2, keys of channel 0, keys of channel 1, ...]`, a key
    being `[interpolation, value, time(, in slope, out slope)]` (0 step, 1 linear,
    2 Hermite), or `[0, parameter, keyframed line, fallback]` for a line the client
    evaluates at an external parameter instead of time."""
    if is_keyframed(value):
        channels = []
        for channel in value[1:]:
            keys = []
            for key in channel:
                if len(key) not in (3, 5):
                    raise SystemExit(f'particle line {name}: unexpected key {key}')
                keys.append(line_key(key[0], key[2], key[1], *(key[3:5] if len(key) == 5 else ())))
            channels.append({'keys': keys})
        return {'name': name, 'dims': len(channels), 'parameter': '', 'fallback': [], 'channels': channels}
    if isinstance(value, list) and len(value) == 4 and value[0] == 0 and isinstance(value[1], str) \
            and is_keyframed(value[2]):
        line = property_line(name, value[2])
        fallback = value[3] if isinstance(value[3], (list, tuple)) else [value[3]]
        line.update(parameter=value[1], fallback=[float(x) for x in fallback])
        return line
    return None


def packed_color_line(name: str, value: int) -> dict:
    """A constant colour packed as R << 24 | G << 16 | B << 8 | A."""
    v = int(value) & UINT32_MAX
    return constant_line(name, [(v >> shift & 255) / 255.0 for shift in (24, 16, 8, 0)])


def parse_sprite_descriptor(text: str) -> dict:
    """A DAVA sprite descriptor: page count, page names, the frame size, the frame
    count and per frame `x y w h offsetX offsetY page name`."""
    lines = text.replace('\r', '').split('\n')
    pages = int(lines[0])
    names = lines[1:1 + pages]
    width, height = (int(float(x)) for x in lines[1 + pages].split()[:2])
    count = int(lines[2 + pages])
    frames = []
    for row in lines[3 + pages:3 + pages + count]:
        parts = row.split()
        frames.append({'rect': [int(float(x)) for x in parts[:4]], 'offset': [int(float(x)) for x in parts[4:6]],
                       'page': int(parts[6]) if len(parts) > 6 else 0})
    if len(frames) != count:
        raise SystemExit('truncated sprite descriptor')
    return {'pages': names, 'size': (width, height), 'frames': frames}


def riff_chunks(blob: bytes):
    if blob[:4] != b'RIFF' or blob[8:12] != b'WEBP':
        raise SystemExit('a packed sprite is not a WebP')
    offset = 12
    while offset + 8 <= len(blob):
        tag = blob[offset:offset + 4]
        size = struct.unpack('<I', blob[offset + 4:offset + 8])[0]
        yield tag, blob[offset + 8:offset + 8 + size]
        offset += 8 + size + (size & 1)


def sheet_layout(count: int) -> tuple[int, int]:
    """Columns and rows of the Unity texture sheet: an exact grid when the frame
    count allows a reasonably square one, else the smallest square-ish grid."""
    best = None
    for columns in range(1, count + 1):
        if count % columns == 0:
            rows = count // columns
            if columns >= rows and (best is None or columns - rows < best[0] - best[1]):
                best = (columns, rows)
    if best and best[0] <= 4 * best[1]:
        return best
    columns = math.ceil(math.sqrt(count))
    return columns, math.ceil(count / columns)


# ------------------------------------------------------------------ rotations
def quaternion_matrix(q) -> np.ndarray:
    """The column-vector rotation of a DAVA quaternion (x, y, z, w)."""
    x, y, z, w = (float(v) for v in q)
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def matrix_quaternion(m: np.ndarray) -> tuple[float, float, float, float]:
    """The unit quaternion (x, y, z, w) of a column-vector rotation."""
    trace = m[0, 0] + m[1, 1] + m[2, 2]
    if trace > 0:
        s = math.sqrt(trace + 1.0) * 2
        w, x = 0.25 * s, (m[2, 1] - m[1, 2]) / s
        y, z = (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w, x = (m[2, 1] - m[1, 2]) / s, 0.25 * s
        y, z = (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w, x = (m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s
        y, z = 0.25 * s, (m[1, 2] + m[2, 1]) / s
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w, x = (m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s
        y, z = (m[1, 2] + m[2, 1]) / s, 0.25 * s
    length = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    return (x / length, y / length, z / length, w / length)


def unity_point(p) -> list[float]:
    return [float(p[0]), float(p[2]), float(p[1])]


def unity_quaternion(q) -> list[float]:
    """The rotation seen through the (x, z, y) reflection: (-x, -z, -y, w)."""
    return [-float(q[0]), -float(q[2]), -float(q[1]), float(q[3])]


def unity_scale(s) -> list[float]:
    return [float(s[0]), float(s[2]), float(s[1])]


SWAP = np.array([[1, 0, 0, 0], [0, 0, 1, 0], [0, 1, 0, 0], [0, 0, 0, 1]], dtype=np.float64)


def trs(t, q, s) -> np.ndarray:
    """A DAVA local transform as a column-vector matrix T * R * S."""
    m = np.eye(4)
    m[:3, :3] = quaternion_matrix(q) * np.asarray(s, dtype=np.float64)[None, :]
    m[:3, 3] = np.asarray(t, dtype=np.float64)
    return m


def row_matrix(values) -> np.ndarray:
    """A DAVA (row-vector) Matrix4 as the column-vector matrix it applies."""
    return np.array([float(v) for v in values], dtype=np.float64).reshape(4, 4).T


def joint_local(values):
    """JointTransform::Construct: translation, rotation and the uniform scale |row 0|."""
    m = np.array([float(v) for v in values], dtype=np.float64).reshape(4, 4)
    rows = m[:3, :3]
    scales = np.linalg.norm(rows, axis=1)
    scales[scales == 0] = 1.0
    return m[3, :3].copy(), matrix_quaternion((rows / scales[:, None]).T), float(scales[0])


def rounded(values, digits: int = 6) -> list[float]:
    return [round(float(v), digits) + 0.0 for v in values]


# ------------------------------------------------------------------ textures
def decode_sampler(value: int) -> dict:
    """rhi::SamplerState::Descriptor::Sampler packed by the material."""
    value = int(value)
    return {'wrapU': value & 3, 'wrapV': value >> 2 & 3, 'minFilter': value >> 6 & 3,
            'magFilter': value >> 8 & 3, 'mipFilter': value >> 10 & 3, 'anisotropy': value >> 12 & 0xFF}


def decode_pvr(blob: bytes, level: int = 0, face: int = 0):
    """One mip/face of an ASTC PVRv3 as RGBA rows in upload order (row 0 first)."""
    header = pvr2png.PvrHeader(blob)
    block = pvr2png.ASTC_BLOCKS.get(header.pixel_format)
    if block is None:
        raise SystemExit(f'unsupported PVR pixel format {header.pixel_format}')
    width, height = pvr2png.mip_dimensions(header.width, header.height, level)
    size = pvr2png.block_mip_size(width, height, block, 16)
    start = pvr2png.mip_offset(header, block, level, 16) + face * size
    payload = blob[start:start + size]
    if len(payload) != size:
        raise SystemExit('truncated PVR payload')
    rgba = pvr2png.bgra_to_rgba(texture2ddecoder.decode_astc(payload, width, height, block[0], block[1]))
    return header, width, height, np.frombuffer(bytes(rgba), dtype=np.uint8).reshape(height, width, 4)


def write_png(path: Path, pixels: np.ndarray, keep_alpha: bool) -> None:
    """Rows flipped: the first uploaded row becomes the bottom row, Unity's v = 0."""
    flipped = pixels[::-1]
    image = Image.fromarray(np.ascontiguousarray(flipped if keep_alpha else flipped[:, :, :3]),
                            'RGBA' if keep_alpha else 'RGB')
    image.save(path, optimize=False, compress_level=6)


def dfg_lut(size: int, samples: int) -> np.ndarray:
    """Materials/Shaders/Utilities/specular-brdf-fp.sl: the split-sum (scale, bias) table.

    Texel (x, y) holds NdotV = (x + 0.5) / size and linear roughness
    (y + 0.5) / size, row 0 first, Hammersley-sampled with ImportanceSampleGGX
    and G_SmithGGX exactly as the client integrates it at start-up.
    """
    coords = (np.arange(size, dtype=np.float64) + 0.5) / size
    n_dot_v, rough = np.meshgrid(coords, coords)  # [y, x]
    n_dot_v, rough = n_dot_v.ravel(), rough.ravel()
    v = np.stack([np.sqrt(1.0 - n_dot_v ** 2), np.zeros_like(n_dot_v), n_dot_v], axis=1)
    a = rough * rough
    a2 = (rough ** 4)
    result = np.zeros((n_dot_v.size, 2))

    def g1(nv, k):
        return 2.0 * nv / (nv + np.sqrt(k + (1.0 - k) * nv * nv))

    index = np.arange(samples, dtype=np.uint64)
    bits = index.astype(np.uint32)
    bits = (bits << np.uint32(16)) | (bits >> np.uint32(16))
    bits = ((bits & np.uint32(0x55555555)) << np.uint32(1)) | ((bits & np.uint32(0xAAAAAAAA)) >> np.uint32(1))
    bits = ((bits & np.uint32(0x33333333)) << np.uint32(2)) | ((bits & np.uint32(0xCCCCCCCC)) >> np.uint32(2))
    bits = ((bits & np.uint32(0x0F0F0F0F)) << np.uint32(4)) | ((bits & np.uint32(0xF0F0F0F0)) >> np.uint32(4))
    bits = ((bits & np.uint32(0x00FF00FF)) << np.uint32(8)) | ((bits & np.uint32(0xFF00FF00)) >> np.uint32(8))
    xi_x = index.astype(np.float64) / samples
    xi_y = bits.astype(np.float64) / 2.0 ** 32
    # N = +Z: ImportanceSampleGGX picks UpVector +X, TangentX = (0,-1,0), TangentY = (1,0,0).
    chunk = 16
    for start in range(0, samples, chunk):
        sx = xi_x[None, start:start + chunk]
        sy = xi_y[None, start:start + chunk]
        phi = 2.0 * math.pi * sx
        cos_t = np.abs(np.sqrt((1.0 - sy) / (1.0 + (a[:, None] ** 2 - 1.0) * sy)))
        sin_t = np.sqrt(np.maximum(0.0, 1.0 - cos_t * cos_t))
        hx, hy, hz = sin_t * np.cos(phi), sin_t * np.sin(phi), cos_t
        h = np.stack([hy, -hx, hz], axis=-1)
        v_dot_h = np.einsum('nk,nsk->ns', v, h)
        l = 2.0 * v_dot_h[..., None] * h - v[:, None, :]
        n_dot_l = l[..., 2]
        n_dot_h = h[..., 2]
        nv = n_dot_v[:, None]
        with np.errstate(divide='ignore', invalid='ignore'):
            g = g1(n_dot_l, a2[:, None]) * g1(nv, a2[:, None])
            gv = np.where(n_dot_l > 0.0, g * v_dot_h / (nv * n_dot_h), 0.0)
        fc = (1.0 - v_dot_h) ** 5
        result[:, 0] += np.nansum(gv * (1.0 - fc), axis=1)
        result[:, 1] += np.nansum(gv * fc, axis=1)
    return (result / samples).reshape(size, size, 2).astype(np.float32)


# ------------------------------------------------------------------ Unity bakes
CUBE_FACES = 6


def write_cube(path: Path, mips: list[list[np.ndarray]]) -> None:
    """Raw RGBA8, mip-major, faces in CubemapFace order, rows from t = 0 on."""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(b''.join(np.ascontiguousarray(face, np.uint8).tobytes() for faces in mips for face in faces))


def environment_rotation(dava_quaternion) -> float:
    """The IBL entity turns about the client's up axis only; the client's
    environmentRotation is that angle in turns."""
    x, y, z, w = dava_quaternion
    if abs(x) > 1e-4 or abs(y) > 1e-4:
        raise SystemExit(f'the IBL rotation {dava_quaternion} is not a turn about the client up axis')
    turns = 2.0 * math.atan2(z, w) / (2.0 * math.pi)
    return turns - round(turns)


def dava_lookup(unity: np.ndarray, rotation: float) -> np.ndarray:
    """Shader-side HHEnvironmentDirection: Unity world -> client world (x, z, y), turned about z."""
    vx, vy, vz = unity[:, 0], unity[:, 2], unity[:, 1]
    a = 2.0 * math.pi * rotation
    s, c = math.sin(a), math.cos(a)
    return np.stack([vx * c + vy * s, -vx * s + vy * c, vz], 1)


def gl_direction(face: int, u: np.ndarray, v: np.ndarray) -> np.ndarray:
    """OpenGL's cube map table read backwards: the direction of face
    coordinates (s, t) in [0, 1], t = 0 being the face's first row."""
    s, t = 2.0 * u - 1.0, 2.0 * v - 1.0
    one = np.ones_like(s)
    d = {0: (one, -t, -s), 1: (-one, -t, s), 2: (s, one, t), 3: (s, -one, -t), 4: (s, -t, one), 5: (-s, -t, -one)}[face]
    d = np.stack(d, 1)
    return d / np.linalg.norm(d, axis=1, keepdims=True)


def sample_cube(faces: list[np.ndarray], d: np.ndarray) -> np.ndarray:
    """Bilinear, clamped to the face, like the GPU without seamless filtering."""
    size = faces[0].shape[0]
    ax, ay, az = np.abs(d[:, 0]), np.abs(d[:, 1]), np.abs(d[:, 2])
    x_major = (ax >= ay) & (ax >= az)
    y_major = ~x_major & (ay >= az)
    z_major = ~x_major & ~y_major
    face = np.zeros(len(d), np.int64)
    s = np.zeros(len(d))
    t = np.zeros(len(d))
    m = np.where(x_major, ax, np.where(y_major, ay, az))
    sel = x_major & (d[:, 0] > 0); face[sel] = 0; s[sel] = -d[sel, 2]; t[sel] = -d[sel, 1]
    sel = x_major & ~(d[:, 0] > 0); face[sel] = 1; s[sel] = d[sel, 2]; t[sel] = -d[sel, 1]
    sel = y_major & (d[:, 1] > 0); face[sel] = 2; s[sel] = d[sel, 0]; t[sel] = d[sel, 2]
    sel = y_major & ~(d[:, 1] > 0); face[sel] = 3; s[sel] = d[sel, 0]; t[sel] = -d[sel, 2]
    sel = z_major & (d[:, 2] > 0); face[sel] = 4; s[sel] = d[sel, 0]; t[sel] = -d[sel, 1]
    sel = z_major & ~(d[:, 2] > 0); face[sel] = 5; s[sel] = -d[sel, 0]; t[sel] = -d[sel, 1]
    u = (s / m + 1.0) * 0.5 * size - 0.5
    v = (t / m + 1.0) * 0.5 * size - 0.5
    x0 = np.clip(np.floor(u).astype(np.int64), 0, size - 1)
    y0 = np.clip(np.floor(v).astype(np.int64), 0, size - 1)
    x1 = np.minimum(x0 + 1, size - 1)
    y1 = np.minimum(y0 + 1, size - 1)
    fx = np.clip(u - x0, 0.0, 1.0)[:, None]
    fy = np.clip(v - y0, 0.0, 1.0)[:, None]
    stack = np.stack(faces)  # [face, row, column, channel]
    a = stack[face, y0, x0] * (1 - fx) + stack[face, y0, x1] * fx
    b = stack[face, y1, x0] * (1 - fx) + stack[face, y1, x1] * fx
    return a * (1 - fy) + b * fy


def unity_oriented(faces: list[np.ndarray], rotation: float) -> list[np.ndarray]:
    """One mip of a client cube map resampled so that texel direction d (Unity
    world) shows what the client's lookup HHEnvironmentDirection(d) shows."""
    size = faces[0].shape[0]
    source = [face.astype(np.float64) / 255.0 for face in faces]
    y, x = np.mgrid[0:size, 0:size]
    u = ((x + 0.5) / size).ravel()
    v = ((y + 0.5) / size).ravel()
    result = []
    for face in range(CUBE_FACES):
        colours = sample_cube(source, dava_lookup(gl_direction(face, u, v), rotation))
        result.append(np.clip(np.round(colours * 255.0), 0, 255).astype(np.uint8).reshape(size, size, 4))
    return result


WRAP_MODES = {0: 'Repeat', 1: 'Clamp', 2: 'Mirror'}
# ASTC block of the client texture -> nearest square block Unity offers, never
# coarser than the client's bit rate.
ASTC_SQUARE = {'ASTC 12x12': '12x12', 'ASTC 12x10': '10x10', 'ASTC 10x10': '10x10', 'ASTC 10x8': '8x8',
               'ASTC 8x8': '8x8', 'ASTC 10x6': '6x6', 'ASTC 10x5': '6x6', 'ASTC 8x6': '6x6', 'ASTC 8x5': '6x6',
               'ASTC 6x6': '6x6', 'ASTC 6x5': '5x5', 'ASTC 5x5': '5x5', 'ASTC 5x4': '4x4', 'ASTC 4x4': '4x4'}


def import_filter(mag_filter: int, mip_filter: int) -> str:
    if mag_filter == 0 and mip_filter == 0:
        return 'Point'
    return 'Trilinear' if mip_filter == 2 else 'Bilinear'


def import_max_size(width: int, height: int) -> int:
    size = 32
    while size < max(width, height) and size < 8192:
        size *= 2
    return size


def write_import(path: Path, textures: list[dict], sprites: list[dict], output: Path) -> None:
    """Importer settings per image: the first sampler state that references it wins."""
    specs: dict[str, dict] = {}
    conflicts = []

    def add(file, spec):
        old = specs.get(file)
        if old is None:
            specs[file] = spec
        elif any(old[k] != spec[k] for k in ('srgb', 'wrapU', 'wrapV', 'mips')):
            conflicts.append(file)

    for t in textures:
        if t['format'] not in ASTC_SQUARE:
            raise SystemExit(f"{t['file']}: unknown client texture format {t['format']}")
        add(t['file'], {'srgb': bool(t['srgb']), 'mips': t.get('mipFilter', 0) != 0 and t.get('mips', 1) > 1,
                        'filter': import_filter(t.get('magFilter', 1), t.get('mipFilter', 0)),
                        'wrapU': WRAP_MODES[t.get('wrapU', 0)], 'wrapV': WRAP_MODES[t.get('wrapV', 0)],
                        'aniso': max(1, t.get('anisotropy', 1)), 'maxSize': import_max_size(t['width'], t['height']),
                        'astc': ASTC_SQUARE[t['format']]})
    for s in sprites:
        with Image.open(output / s['file']) as image:
            width, height = image.size
        add(s['file'], {'srgb': False, 'mips': s.get('mipFilter', 0) != 0,
                        'filter': import_filter(s.get('magFilter', 1), s.get('mipFilter', 0)),
                        'wrapU': WRAP_MODES[s.get('wrapU', 0)], 'wrapV': WRAP_MODES[s.get('wrapV', 0)],
                        'aniso': max(1, s.get('anisotropy', 1)), 'maxSize': import_max_size(width, height),
                        'astc': '4x4'})
    path.write_text(json.dumps({'images': dict(sorted(specs.items())), 'conflicts': sorted(set(conflicts))},
                               indent=1), encoding='utf-8')


class Templates:
    """The client's `.material` documents, resolved to the port quality."""

    def __init__(self, src: Source):
        self.src = src
        self.cache: dict[str, tuple[str, dict]] = {}

    def load(self, reference: str) -> tuple[str, dict]:
        name = reference.split('/')[-1]
        if name not in self.cache:
            text = self.src.read(self.src.apk / 'Materials' / name).decode('utf-8').replace('\r', '').replace('\t', '    ')
            document = yaml.safe_load(text)
            template = document.get('MaterialTemplate')
            self.cache[name] = self.load(template[QUALITY]) if template else (name, document['Material'])
        return self.cache[name]


# ------------------------------------------------------------------ export
class HangarExport:
    def __init__(self, src: Source):
        self.src = src
        archive = dava.read_sc2(src.read(src.map_dir / f'{MAP}.sc2'))
        self.scene = archive['scene']
        self.groups = {dava.group_id(g): g for g in dava.read_scg(src.read(src.map_dir / f'{MAP}.scg'))}
        self.data_nodes = items(self.scene.get('#dataNodes'))
        self.nmaterials = {int.from_bytes(bytes(d['#id']), 'little'): d for d in self.data_nodes
                           if d.get('##name') == 'NMaterial' and d.get('#id') is not None}
        self.scene_sets = {name: items(value) for name, value in (self.scene.get('#sceneComponentSets') or {}).items()}
        self.scene_components = [c for c in items(self.scene.get('#sceneComponents')) if isinstance(c, dict)]
        self.templates = Templates(src)
        self.notes = collections.Counter()
        self.warnings: list[str] = []
        self.stats = collections.defaultdict(collections.Counter)
        self.nodes: list[dict] = []
        self.world: dict[int, np.ndarray] = {}
        self.renderers: list[dict] = []
        self.meshes: list[dict] = []
        self.mesh_keys: dict = {}
        self.materials: list[dict] = []
        self.material_index: dict[int, int] = {}
        self.textures: list[dict] = []
        self.texture_index: dict = {}
        self.lights: list[dict] = []
        self.clips: list[dict] = []
        self.clip_keys: dict = {}
        self.animations: list[dict] = []
        self.states: list[dict] = []
        self.sounds: list[dict] = []
        self.effects: list[dict] = []
        self.particle_starts: dict[int, dict] = {}
        self.particle_meshes: dict[int, int] = {}
        self.sprites: list[dict] = []
        self.sprite_index: dict = {}
        self.sprite_sources: dict[str, dict] = {}
        self.skipped: list[str] = []
        self.camera = None
        self.spawn = None
        self.skeleton_joints: dict[int, list[int]] = {}
        self.max_world_error = 0.0
        self.max_bind_error = 0.0
        self.rebrands: list[dict] = []

    # -------------------------------------------------------------- nodes
    def add_node(self, name, parent, position, rotation, scale, path, kind, world) -> int:
        self.nodes.append({'name': name, 'parent': parent, 'position': rounded(position),
                           'rotation': rounded(rotation, 7), 'scale': rounded(scale), 'active': True,
                           'path': path, 'kind': kind})
        self.world[len(self.nodes) - 1] = world
        return len(self.nodes) - 1

    def walk(self) -> None:
        for entity in children(self.scene):
            self.visit(entity, -1, np.eye(4), '', None)

    def check_world(self, tc: dict, world: np.ndarray, path: str) -> None:
        if not tc or 'tc.worldTranslation' not in tc:
            return
        stored = trs(tc['tc.worldTranslation'], tc['tc.worldRotation'], tc['tc.worldScale'])
        error = float(np.abs(stored - world).max() / max(1.0, np.abs(world[:3, 3]).max()))
        self.max_world_error = max(self.max_world_error, error)
        if error > 1e-3:
            self.warnings.append(f'{path}: composed world differs from the stored one by {error:.2g}')

    def visit(self, entity: dict, parent: int, parent_world: np.ndarray, parent_path: str,
              state_root: str | None, socket_world: np.ndarray | None = None) -> None:
        name = str(entity.get('name', ''))
        path = f'{parent_path}/{name}'
        if name in SKIPPED_SUBTREES:
            self.skipped.append(path)
            return
        tc = component(entity, 'TransformComponent') or {}
        t = tc.get('tc.localTranslation', [0, 0, 0])
        q = tc.get('tc.localRotation', [0, 0, 0, 1])
        s = tc.get('tc.localScale', [1, 1, 1])
        if parent == -1 and component(entity, 'CameraComponent'):
            self.read_camera(entity)
            return
        custom = component(entity, 'CustomPropertiesComponent')
        if custom and (custom.get('cpc.properties.archive') or {}).get('type') == 'spawnpoint':
            self.spawn = {'position': rounded(unity_point(tc['tc.worldTranslation'])),
                          'rotation': rounded(unity_quaternion(tc['tc.worldRotation']), 7),
                          'positionDava': rounded(tc['tc.worldTranslation'])}
            return
        if socket_world is not None:
            # SkeletonJointBindingSystem drives the socket with the joint's object-space transform.
            world = socket_world
            index = self.add_node(name, parent, [0, 0, 0], [0, 0, 0, 1], [1, 1, 1], path, 'socket', world)
            stored = parent_world @ trs(t, q, s)
            error = float(np.abs(stored - world).max())
            self.notes['socket pose error x1000'] = max(self.notes['socket pose error x1000'], int(error * 1000))
        else:
            world = parent_world @ trs(t, q, s)
            self.check_world(tc, world, path)
            index = self.add_node(name, parent, unity_point(t), unity_quaternion(q), unity_scale(s), path, 'entity', world)
            if np.linalg.det(world[:3, :3]) < 0:
                self.notes['mirrored entities'] += 1
        visibility = int(entity.get('visibility', 0))
        if visibility and state_root is None:
            self.warnings.append(f'{path}: visibility {visibility} outside a state switcher')
            self.nodes[index]['active'] = False
        self.stats['visibility'][visibility] += 1

        skeleton = self.read_skeleton(entity, index, world, path)
        self.render_object(entity, index, world, path, skeleton, state_root is not None)
        self.read_light(entity, index, world)
        self.read_motion(entity, index, skeleton, path)
        self.read_sounds(entity, index, world)
        self.read_effect(entity, index, path)
        self.read_particle_actions(entity, name, path)

        sockets = {}
        binding = component(entity, 'SkeletonJointBindingComponent')
        if binding:
            for item in items(binding.get('map')):
                if isinstance(item, dict):
                    sockets[str(item['entityUid'])] = str(item['jointUid'])
        switcher = component(entity, 'StateSwitcherComponent')
        state_names = []
        if switcher:
            state_names = [str(switcher.get(f'ssc.state{k}')) for k in range(int(switcher.get('ssc.statesCount', 0)))]
        child_nodes = {}
        for child in children(entity):
            child_name = str(child.get('name', ''))
            child_root = state_root or (path if child_name in state_names else None)
            if child_name in sockets and skeleton:
                joint = skeleton['by_uid'].get(sockets[child_name])
                if joint is None:
                    raise SystemExit(f'{path}: socket {child_name} binds unknown joint {sockets[child_name]}')
                joint_node = skeleton['nodes'][joint]
                child_nodes[child_name] = len(self.nodes)
                self.visit(child, joint_node, world, path, child_root, socket_world=self.world[joint_node])
            else:
                child_nodes[child_name] = len(self.nodes)
                self.visit(child, index, world, path, child_root)
        if switcher:
            self.read_states(entity, index, switcher, state_names, child_nodes, path)

    # -------------------------------------------------------------- skeleton
    def read_skeleton(self, entity, index, world, path):
        sk = component(entity, 'SkeletonComponent')
        if not sk:
            return None
        joints = [j for j in items(sk.get('joints')) if isinstance(j, dict)]
        nodes, names, local, model = [], [], [], []
        by_uid = {}
        for k, joint in enumerate(joints):
            parent = int(joint.get('joint.parentIndex', NO_PARENT_JOINT))
            t, q, s = joint_local(joint['joint.bindPose'])
            m = trs(t, q, [s, s, s])
            if parent == NO_PARENT_JOINT:
                parent_node, parent_model, joint_path = index, np.eye(4), str(joint['joint.name'])
            else:
                if parent >= k:
                    raise SystemExit(f'{path}: joint {k} precedes its parent {parent}')
                parent_node, parent_model = nodes[parent], model[parent]
                joint_path = names[parent] + '/' + str(joint['joint.name'])
            model.append(parent_model @ m)
            nodes.append(self.add_node(str(joint['joint.name']), parent_node, unity_point(t), unity_quaternion(q),
                                       [s, s, s], path + '#' + joint_path, 'joint', world @ model[-1]))
            names.append(joint_path)
            by_uid[str(joint.get('joint.uid'))] = k
            inverse = row_matrix(joint['joint.invBindPose'])
            if not np.allclose(inverse, np.eye(4)):
                error = float(np.abs(inverse @ model[-1] - np.eye(4)).max())
                self.max_bind_error = max(self.max_bind_error, error)
        if len(set(names)) != len(names):
            raise SystemExit(f'{path}: duplicate joint paths')
        child_names = {str(c.get('name')) for c in children(entity)}
        clash = child_names & {n.split('/')[0] for n in names}
        if clash:
            raise SystemExit(f'{path}: child entities share root joint names {clash}')
        skeleton = {'joints': joints, 'nodes': nodes, 'paths': names, 'by_uid': by_uid, 'model': model}
        self.skeleton_joints[index] = nodes
        return skeleton

    # -------------------------------------------------------------- render objects
    def render_object(self, entity, index, world, path, skeleton, in_state) -> None:
        rc = component(entity, 'RenderComponent')
        if not rc:
            return
        ro = rc['rc.renderObj']
        kind = ro.get('##name')
        if kind not in ('Mesh', 'SkinnedMesh'):
            raise SystemExit(f'{path}: unsupported render object {kind}')
        flags = int(ro.get('ro.flags', 0))
        self.stats['ro.flags'][flags] += 1
        if not flags & RO_VISIBLE and not in_state:
            self.notes['hidden render objects skipped'] += 1
            self.warnings.append(f'{path}: render object without VISIBLE flag skipped')
            return
        batches = []
        for i, batch in enumerate(items(ro.get('ro.batches'))):
            lod = int(ro.get(f'rb{i}.lodIndex', -1))
            switch = int(ro.get(f'rb{i}.switchIndex', -1))
            if lod > 0 or switch > 0:
                self.notes['batches of farther LODs / switch states'] += 1
                continue
            batches.append((i, batch))
        if not batches:
            return
        skinned = kind == 'SkinnedMesh'
        if skinned and skeleton is None:
            raise SystemExit(f'{path}: skinned mesh without skeleton')
        materials = [self.material(int(b['rb.nmatname'])) for _, b in batches]
        modes = [self.skin_mode(m) for m in materials]
        if not skinned and any(mode[0] != 'none' for mode in modes):
            self.warnings.append(f'{path}: skinning material on a static mesh')
        mesh_index, bones, bounds = self.build_mesh(entity, ro, batches, skeleton if skinned else None,
                                                    modes if skinned else None, path)
        lo, hi = None, None
        for _, b in batches:
            box = [float(v) for v in b['rb.aabbox']]
            corners = np.array([[box[i], box[j], box[k], 1.0] for i in (0, 3) for j in (1, 4) for k in (2, 5)])
            corners = (world @ corners.T).T[:, :3]
            lo = corners.min(0) if lo is None else np.minimum(lo, corners.min(0))
            hi = corners.max(0) if hi is None else np.maximum(hi, corners.max(0))
        not_shadow_only = bool(ro.get('ro.notShadowOnly', True))
        shadows = 3 if not not_shadow_only else (1 if flags & RO_CAST_SHADOW else 0)
        self.renderers.append({
            'node': index, 'mesh': mesh_index, 'materials': materials, 'skinned': skinned,
            'bones': [index if j == ENTITY_BONE else skeleton['nodes'][j] for j in bones],
            'shadowCasting': shadows, 'lights': [-1, -1], 'boundsMin': rounded(bounds[0]), 'boundsMax': rounded(bounds[1]),
            'sortingKeys': [int(b.get('rb.sortingKey', 0)) for _, b in batches], 'path': path,
            '_lo': lo, '_hi': hi})

    def skin_mode(self, material: int) -> tuple[str, int]:
        """What the client's vertex shader skins a batch with: SOFT_SKINNING n reads
        the first n joint slots, HARD_SKINNING one joint, neither leaves it static."""
        defines = {d['name']: d['value'] for d in self.materials[material]['defines']}
        if defines.get('SOFT_SKINNING'):
            return 'soft', int(defines['SOFT_SKINNING'])
        if defines.get('HARD_SKINNING'):
            return 'hard', 1
        return 'none', 0

    def build_mesh(self, entity, ro, batches, skeleton, modes, path):
        parts = []
        for (i, batch), mode in zip(batches, modes or [None] * len(batches)):
            gid = int(batch['rb.datasource'])
            group = self.groups.get(gid)
            if group is None:
                raise SystemExit(f'{path}: polygon group {gid} is missing')
            v = decode_vertices(group)
            triangles = decode_indices(group)
            if triangles.size % 3:
                raise SystemExit(f'{path}: polygon group {gid} is not a triangle list')
            self.stats['vertex formats'][int(group['vertexFormat'])] += 1
            targets = None
            if skeleton is not None:
                count = int(ro.get(f'skinnedObject.batch{i}.targetsCount', 0))
                blob = ro.get(f'skinnedObject.batch{i}.targetsData')
                targets = np.frombuffer(bytes(blob), dtype='<u4', count=count).astype(np.int64) if count else None
                if targets is None and mode[0] != 'none':
                    raise SystemExit(f'{path}: skinned batch {i} without joint targets')
                self.stats['skinning modes'][f'{mode[0]}{mode[1] if mode[0] == "soft" else ""}'] += 1
            parts.append((gid, v, triangles, targets, mode))
        bindposes = None
        bones = []
        if skeleton is not None:
            bone_set = set()
            for gid, v, _, targets, mode in parts:
                local, weights = self.skin_streams(v, mode, path)
                if local is None:
                    bone_set.add(ENTITY_BONE)
                    continue
                used = np.unique(local[weights != 0])
                if used.size and (used.max() >= targets.size or used.min() < 0):
                    raise SystemExit(f'{path}: joint index {used.max()} outside {targets.size} targets')
                bone_set |= set(int(targets[k]) for k in used)
            bones = sorted(bone_set)
            bindposes = np.stack([np.eye(4) if j == ENTITY_BONE else
                                  SWAP @ row_matrix(skeleton['joints'][j]['joint.invBindPose']) @ SWAP for j in bones])
        key = (tuple(p[0] for p in parts), tuple(tuple(p[3].tolist()) if p[3] is not None else () for p in parts),
               tuple(p[4] for p in parts) if skeleton is not None else (),
               hashlib.sha1(bindposes.tobytes()).hexdigest() if bindposes is not None else '')
        box_lo = np.min([[float(b['rb.aabbox'][k]) for k in (0, 1, 2)] for _, b in batches], axis=0)
        box_hi = np.max([[float(b['rb.aabbox'][k]) for k in (3, 4, 5)] for _, b in batches], axis=0)
        bounds = (unity_point(box_lo), unity_point(box_hi))
        if key in self.mesh_keys:
            self.notes['shared meshes'] += 1
            return self.mesh_keys[key], bones, bounds

        converted, joint_rows, weight_rows, frame_rows, submeshes = [], [], [], [], []
        base = 0
        bone_of = {j: k for k, j in enumerate(bones)}
        for gid, v, triangles, targets, mode in parts:
            s = unity_streams(v)
            n = s['count']
            converted.append(s)
            if skeleton is not None:
                joints, weights, mix = self.skin_rows(v, mode, targets, bone_of, path)
                joint_rows.append(joints)
                weight_rows.append(weights)
                frame_rows.append(np.concatenate([s['normals'], mix[:, :1], s['tangents'][:, :3], mix[:, 1:]], axis=1))
                if {'normal', 'tangent', 'binormal'} <= s['present']:
                    rebuilt = np.cross(s['normals'], s['tangents'][:, :3]) * s['tangents'][:, 3:]
                    length = np.linalg.norm(rebuilt, axis=1, keepdims=True)
                    authored = s['binormals'] / np.maximum(np.linalg.norm(s['binormals'], axis=1, keepdims=True), 1e-12)
                    error = float(np.abs(rebuilt / np.maximum(length, 1e-12) - authored).max()) if n else 0.0
                    # Authored frames are not orthogonal, so the shader rotates the bind
                    # binormal with the joint rotation it recovers from the skinned
                    # normal and tangent instead of rebuilding it from their cross product.
                    self.notes['skinned binormal off cross(normal, tangent) x1000'] = max(
                        self.notes['skinned binormal off cross(normal, tangent) x1000'], int(error * 1000))
            tri = triangles.reshape(-1, 3)[:, [0, 2, 1]] + base
            if tri.size and (tri.max() >= base + n or tri.min() < base):
                raise SystemExit(f'{path}: index outside polygon group {gid}')
            submeshes.append(tri.reshape(-1).astype(np.uint32))
            base += n
        present = set().union(*[s['present'] for s in converted])
        streams = stream_bits(present)
        if skeleton is not None:
            streams |= MESH_SKIN | MESH_BINDFRAME
        for name in ('pivot', 'pivot_deprecated', 'flexibility', 'angle_sin_cos'):
            if name in present:
                self.warnings.append(f'{path}: vertex stream {name} is not exported')
        mesh = {'name': f'{entity.get("name")}_{len(self.meshes)}', 'vertexCount': base, 'streams': streams,
                'submeshes': submeshes, 'bindposes': bindposes, 'groups': [p[0] for p in parts], 'particle': False,
                'joints': np.concatenate(joint_rows) if joint_rows else None,
                'weights': np.concatenate(weight_rows) if weight_rows else None,
                'bindframe': np.concatenate(frame_rows).astype(np.float32) if frame_rows else None}
        for name in ('positions', 'normals', 'tangents', 'colors', 'uv0', 'uv1', 'binormals', 'uv3', 'uv4'):
            mesh[name] = np.concatenate([s[name] for s in converted])
        self.meshes.append(mesh)
        self.mesh_keys[key] = len(self.meshes) - 1
        return len(self.meshes) - 1, bones, bounds

    def skin_streams(self, v, mode, path):
        """The joint slots and raw weights the client's vertex shader reads, or
        (None, None) for a batch it leaves in the entity's space."""
        kind, slots = mode
        if kind == 'soft':
            if 'joint_index' not in v or 'joint_weight' not in v:
                raise SystemExit(f'{path}: SOFT_SKINNING batch without joint index/weight streams')
            local = np.rint(v['joint_index']).astype(np.int64)
            weights = v['joint_weight'].astype(np.float64)
            return local[:, :slots], weights[:, :slots]
        if kind == 'hard':
            if 'hard_joint' not in v:
                raise SystemExit(f'{path}: HARD_SKINNING batch without the hard joint stream')
            n = len(v['hard_joint'])
            return np.rint(v['hard_joint'][:, :1]).astype(np.int64), np.ones((n, 1))
        return None, None

    def skin_rows(self, v, mode, targets, bone_of, path):
        """Unity bone slots and weights of one batch plus, per vertex, the weights
        of the bind-pose and the skinned vector in the client's normal frame.

        SoftSkinnedPosition divides by the weight sum S of the slots it reads, so
        Unity gets the normalised weights. SoftSkinnedNormal/TBN instead sum
        lerp(v, R_i v, w_i) over all n slots: (n - S) * v + S * (sum of w_i/S R_i v),
        which the port's shader rebuilds from the bind vectors in UV5/UV6.
        """
        n = len(v['position'])
        joints = np.zeros((n, 4), np.int32)
        weights_out = np.zeros((n, 4), np.float32)
        mix = np.zeros((n, 2), np.float32)
        local, weights = self.skin_streams(v, mode, path)
        if local is None:
            joints[:, 0] = bone_of[ENTITY_BONE]
            weights_out[:, 0] = 1.0
            mix[:, 1] = 1.0
            self.notes['vertices of unskinned batches on skinned meshes'] += n
            return joints, weights_out, mix
        if mode[0] == 'soft':
            full = v['joint_weight'].astype(np.float64)
            dropped = int((full[:, mode[1]:] != 0).any(axis=1).sum())
            if dropped:
                self.notes['vertices weighting slots past SOFT_SKINNING'] += dropped
        if (weights < 0).any():
            raise SystemExit(f'{path}: negative joint weights')
        total = weights.sum(axis=1)
        if (total <= 0).any():
            raise SystemExit(f'{path}: {int((total <= 0).sum())} vertices without joint weight')
        lookup = np.array([bone_of.get(int(t), -1) for t in targets], dtype=np.int64)
        mapped = np.where(weights != 0, lookup[np.clip(local, 0, targets.size - 1)], 0)
        if (mapped < 0).any():
            raise SystemExit(f'{path}: weighted joint outside the bone list')
        normalized = weights / total[:, None]
        order = np.argsort(-normalized, axis=1, kind='stable')
        slots = normalized.shape[1]
        joints[:, :slots] = np.take_along_axis(mapped, order, axis=1)
        weights_out[:, :slots] = np.take_along_axis(normalized, order, axis=1)
        if mode[0] == 'soft':
            mix[:, 0] = mode[1] - total
            mix[:, 1] = total
            for value in np.unique(np.round(total, 1)):
                self.stats['soft skinning weight sums'][f'n={mode[1]} S={value:.1f}'] += int((np.round(total, 1) == value).sum())
        else:
            mix[:, 1] = 1.0
        return joints, weights_out, mix

    # -------------------------------------------------------------- materials
    def material(self, mid: int) -> int:
        if mid in self.material_index:
            return self.material_index[mid]
        chain, seen, current = [], set(), mid
        while current is not None and current not in seen:
            seen.add(current)
            node = self.nmaterials.get(int(current))
            if node is None:
                break
            chain.append(node)
            current = node.get('parentMaterialKey')
        if not chain:
            raise SystemExit(f'material {mid} is missing')
        flags, props, textures, samplers, presets = {}, {}, {}, {}, {}
        effect = quality_group = None
        for node in chain:  # nearest wins; a zero flag on a child switches the parent's off
            for k, v in (node.get('flags') or {}).items():
                flags.setdefault(str(k), int(v))
            for k, v in (node.get('enabledPresets') or {}).items():
                presets.setdefault(str(k), str(v).strip().lower() not in ('', '0', 'false') if isinstance(v, str) else bool(v))
            for k in (node.get('properties') or {}):
                props.setdefault(str(k), material_property(node, k))
            for k, v in (node.get('textures') or {}).items():
                textures.setdefault(str(k), str(v))
            for k, v in (node.get('textureSampleStates') or {}).items():
                samplers.setdefault(str(k), int(v))
            if effect is None and node.get('fxName'):
                effect = str(node['fxName'])
            if quality_group is None and node.get('qualityGroup'):
                quality_group = str(node['qualityGroup'])
        if effect is None:
            raise SystemExit(f'material {mid} has no effect')
        document_name, document = self.templates.load(effect)
        shader_ref = str(document['Shader'])
        layers = list(document.get('Layers') or [])
        defines: dict[str, int] = {}

        def add(values):
            for d in values or []:
                if isinstance(d, list):
                    defines[str(d[0])] = int(d[1])
                else:
                    defines[str(d)] = 1

        add(document.get('UniqueDefines'))
        ignore = set(document.get('IgnoreDefines') or [])
        state = dict(DEFAULT_STATE)
        state.update(document.get('RenderState') or {})
        passes = {k: dict(v or {}) for k, v in (document.get('Passes') or {}).items()}
        enabled = [name for name, on in presets.items() if on]
        applied = []
        for preset_name, preset in (document.get('Presets') or {}).items():  # declaration order
            if preset_name not in enabled:
                continue
            applied.append(preset_name)
            preset = preset or {}
            if preset.get('Layers'):
                layers = list(preset['Layers'])
            if preset.get('Shader'):
                shader_ref = str(preset['Shader'])
            add(preset.get('UniqueDefines'))
            ignore |= set(preset.get('IgnoreDefines') or [])
            state.update(preset.get('RenderState') or {})
            for pass_name, override in (preset.get('Passes') or {}).items():
                merged = passes.setdefault(pass_name, {})
                for k, v in (override or {}).items():
                    if k == 'RenderState':
                        merged['RenderState'] = dict(merged.get('RenderState') or {}, **v)
                    else:
                        merged[k] = v
        for name in enabled:
            if name not in applied:
                self.stats['presets without effect'][f'{document_name}:{name}'] += 1
        for k, v in flags.items():
            if v:
                defines[k] = v
        for k in ignore:
            defines.pop(k, None)
        consider = document.get('ConsiderOnlyDefines')
        if consider:
            defines = {k: v for k, v in defines.items() if k in set(consider)}
        shader_key = shader_ref.rstrip('/').split('/')[-1]
        if shader_key not in SHADERS:
            raise SystemExit(f'material {mid}: unsupported shader {shader_ref}')
        shader, samplers_used = SHADERS[shader_key]
        shadow = passes.get('ShadowMap')
        shadow_on = shadow is not None and shadow.get('enabled', True) is not False
        bindings = []
        for slot, ref in sorted(textures.items()):
            if slot not in samplers_used:
                self.stats['texture slots unused by shader'][f'{shader}:{slot}'] += 1
                continue
            if not ref:
                continue
            sampler = samplers.get(slot)
            if sampler is None:
                self.notes['texture bindings with default sampler'] += 1
                sampler = DEFAULT_SAMPLER
            self.stats['samplers'][sampler] += 1
            bindings.append({'slot': slot, 'texture': self.texture(ref, sampler)})
        name = f'{chain[0].get("materialName", "Material")}_{mid}'
        record = {
            'name': name, 'id': mid, 'shader': shader, 'template': document_name, 'layer': layers[0] if layers else 'OpaqueRenderLayer',
            'presets': applied, 'qualityGroup': quality_group or '',
            'defines': [{'name': k, 'value': v} for k, v in sorted(defines.items())],
            'depthTest': bool(state.get('depthTest', True)), 'depthWrite': bool(state.get('depthWrite', True)),
            'cullMode': str(state.get('cullMode', 'FACE_BACK')), 'depthFunc': str(state.get('depthFunc', 'CMP_LESS')),
            'blend': bool(state.get('blend', False)),
            'castsShadow': bool(shadow_on),
            'shadowAlphaTest': bool(shadow_on and 'alphatest' in str(shadow.get('Shader', ''))),
            'textures': bindings,
            'properties': [{'name': k, 'values': rounded(v, 7)} for k, v in sorted(props.items()) if v]}
        for k, v in sorted(defines.items()):
            self.stats['defines'][f'{shader}:{k}={v}'] += 1
        self.stats['material kinds'][f'{shader} {document_name} {"+".join(applied)} layer={record["layer"]}'] += 1
        self.materials.append(record)
        self.material_index[mid] = len(self.materials) - 1
        return self.material_index[mid]

    def texture_path(self, reference: str) -> Path:
        base = self.src.map_path(reference)
        stem = base.name[:-4] if base.name.endswith('.tex') else base.name
        candidate = base.with_name(stem + '.astc.pvr')
        if self.src.exists(candidate):
            return candidate
        raise SystemExit(f'texture {reference} has no ASTC PVR in the client')

    def texture(self, reference: str, sampler: int) -> int:
        path = self.texture_path(reference)
        state = decode_sampler(sampler)
        key = (path, state['wrapU'], state['wrapV'], state['minFilter'], state['magFilter'], state['mipFilter'], state['anisotropy'])
        if key in self.texture_index:
            return self.texture_index[key]
        relative = self.src.relative(path)
        stem = relative[:-len('.astc.pvr')]
        stem = stem[len(MAP) + 1:] if stem.startswith(MAP + '/') else stem.replace('00_shared_content', 'shared')
        name = ''.join(ch if ch.isalnum() or ch in '-_' else '_' for ch in stem.replace('/', '_'))
        taken = {t['name'] for t in self.textures}
        unique, n = name, 1
        while unique in taken:
            unique, n = f'{name}_s{n}', n + 1
        self.textures.append({'name': unique, 'file': f'Textures/{unique}.png', 'source': relative, 'sampler': sampler,
                              'wrapU': state['wrapU'], 'wrapV': state['wrapV'], 'minFilter': state['minFilter'],
                              'magFilter': state['magFilter'], 'mipFilter': state['mipFilter'],
                              'anisotropy': state['anisotropy'], '_path': path})
        self.texture_index[key] = len(self.textures) - 1
        return len(self.textures) - 1

    # -------------------------------------------------------------- lights, clips, states, sounds
    def read_light(self, entity, index, world) -> None:
        lc = component(entity, 'LightComponent')
        if not lc:
            return
        light = lc.get('lc.light') or {}
        position = world[:3, 3]
        self.lights.append({'node': index, 'type': int(light.get('type', 0)), 'flags': int(light.get('flags', 0)),
                            'color': rounded(color(light, 'color')), 'ambient': rounded(color(light, 'ambColor')),
                            'intensity': float(light.get('intensity', 1.0)), 'radius': float(light.get('radius', 0.0)),
                            'falloff': float(light.get('falloffExponent', 0.0)),
                            'position': rounded(unity_point(position)), 'positionDava': rounded(position)})

    def read_clip(self, reference: str) -> dict:
        blob = self.src.read(self.src.map_path(reference))
        with tempfile.NamedTemporaryFile(suffix='.anim') as handle:
            handle.write(blob)
            handle.flush()
            return dava_anim.read_anim(handle.name)

    def read_motion(self, entity, index, skeleton, path) -> None:
        motion = component(entity, 'MotionComponent')
        if not motion:
            return
        if skeleton is None:
            raise SystemExit(f'{path}: motion without skeleton')
        reference = str(motion.get('motion.filepath') or '')
        rate = float(motion.get('motion.playbackRate', 1.0)) or 1.0
        autoplay, loops = False, 0
        for action in actions(entity):
            if int(action.get('act.type', -1)) == ACTION_MOTION and int(action.get('act.event', -1)) == EVENT_ADDED \
                    and str(action.get('act.entityName')) in (SELF, str(entity.get('name'))):
                autoplay = True
                repeats = int(action.get('act.stopAfterNRepeats', -1))
                loops = 0 if repeats < 0 else repeats
                if float(action.get('act.delay', 0)) or float(action.get('act.delayVariation', 0)):
                    self.warnings.append(f'{path}: delayed motion start is not exported')
        repeats_count = int(motion.get('simpleMotion.repeatsCount', 0))
        key = (reference, rate, tuple(skeleton['paths']))
        if key not in self.clip_keys:
            clip = self.read_clip(reference)
            length = float(clip['duration']) / rate
            curves = []
            unbound = 0
            for node in clip['nodes']:
                joint = skeleton['by_uid'].get(str(node['uid']))
                if joint is None:
                    unbound += 1
                    continue
                for channel in node['channels']:
                    curves.append(self.curve(skeleton['paths'][joint], channel, rate, length))
            if unbound:
                self.notes['clip tracks without joint'] += unbound
            stem = Path(reference).stem
            name = stem if stem not in {c['name'] for c in self.clips} else f'{stem}_{len(self.clips)}'
            self.clips.append({'name': name, 'source': reference, 'length': round(length, 6), 'rate': rate,
                               'curveCount': len(curves), '_curves': curves})
            self.clip_keys[key] = len(self.clips) - 1
        self.animations.append({'node': index, 'clip': self.clip_keys[key], 'autoplay': autoplay,
                                'loop': loops == 0 and repeats_count == 0, 'repeats': loops, 'path': path})

    def curve(self, joint_path, channel, rate, length) -> dict:
        keys = channel['keys']
        target = channel['target']
        times = [float(k[0]) / rate for k in keys]
        if target == 'translation':
            prop, values = 0, [unity_point(k[1]) for k in keys]
        elif target == 'rotation':
            prop, values = 1, []
            for k in keys:
                q = unity_quaternion(k[1])
                if values and sum(a * b for a, b in zip(q, values[-1])) < 0:
                    q = [-c for c in q]
                values.append(q)
        elif target == 'scale':
            prop = 2
            values = [[float(k[1][0])] * 3 if len(k[1]) == 1 else unity_scale(k[1]) for k in keys]
        else:
            raise SystemExit(f'unknown clip channel {target}')
        if times and times[0] > 0:
            times.insert(0, 0.0)
            values.insert(0, values[0])
        if times and times[-1] < length:
            times.append(length)
            values.append(values[-1])
        self.stats['clip channel interpolation'][f'{target}:{channel["flags"]}'] += 1
        return {'path': joint_path, 'property': prop, 'times': times, 'values': values}

    def scenario_random(self, script_id: str):
        """The `n` of a Random -> Entity.ActivateState scenario, as authored."""
        singleton = next((c for c in self.scene_components if c.get('comp.typename') == 'ScenarioSingletonComponent'), None)
        script = ((singleton or {}).get('ssc.scripts') or {}).get(script_id) or {}
        random_n, activates = None, False
        for entry in (script.get('nodes') or {}).values():
            node = {k.split('.')[-1]: v for k, v in ((entry or {}).get('node') or {}).items()}
            kind = (node.get('className'), node.get('functionName'))
            if kind == ('Random', 'Random'):
                random_n = int(((node.get('defaults') or {}).get('n') or {}).get('any.value'))
            elif kind == ('Entity', 'ActivateState'):
                activates = True
        return random_n, activates

    def read_states(self, entity, index, switcher, names, child_nodes, path) -> None:
        states = [child_nodes.get(n, -1) for n in names]
        if -1 in states:
            raise SystemExit(f'{path}: state switcher names a missing child')
        random_n, activates, script = None, False, ''
        scenario = component(entity, 'ScenarioComponent')
        if scenario:
            script = str(scenario.get('scenario.scriptId'))
            random_n, activates = self.scenario_random(script)
        active = int(switcher.get('ssc.activeState', -1))
        default = active if active >= 0 else 0
        for k, node in enumerate(states):
            self.nodes[node]['active'] = k == default
        self.states.append({'node': index, 'states': states, 'defaultState': default,
                            'randomN': -1 if random_n is None else random_n, 'scriptId': script,
                            'activatesState': activates, 'path': path})

    def read_sounds(self, entity, index, world) -> None:
        sc = component(entity, 'SoundComponent')
        if not sc:
            return
        autoplay = any(int(a.get('act.type', -1)) == ACTION_SOUND and int(a.get('act.event', -1)) == EVENT_ADDED
                       and str(a.get('act.entityName')) in (SELF, str(entity.get('name'))) for a in actions(entity))
        for element in items(sc):
            if not isinstance(element, dict) or 'wwise' not in element:
                continue
            wwise = element.get('wwise') or {}
            self.sounds.append({'node': index, 'event': str(wwise.get('eventName', '')), 'group': str(wwise.get('groupName', '')),
                                'flags': int(element.get('sce.flags', 0)),
                                'direction': rounded(unity_point(element.get('sce.localDirection', [1, 0, 0]))),
                                'autoplay': autoplay, 'position': rounded(unity_point(world[:3, 3]))})

    def read_effect(self, entity, index, path) -> None:
        pe = component(entity, 'ParticleEffectComponent')
        if pe:
            self.effects.append({'node': index, 'path': path, '_component': pe, '_entity': entity})

    def read_particle_actions(self, entity, name, path) -> None:
        """ActionComponent 'start particle effect' on the entity's addition to the
        scene, aimed at itself or at a descendant named by the action."""
        for action in actions(entity):
            if int(action.get('act.type', -1)) != ACTION_PARTICLE or int(action.get('act.event', -1)) != EVENT_ADDED:
                continue
            target_name = str(action.get('act.entityName'))
            target = entity if target_name in (SELF, name) else find_by_name(entity, target_name)
            if target is None:
                self.warnings.append(f'{path}: particle action names a missing entity {target_name}')
                continue
            self.particle_starts[id(target)] = {
                'delay': float(action.get('act.delay', 0.0)), 'delayVariation': float(action.get('act.delayVariation', 0.0)),
                'repeats': int(action.get('act.stopAfterNRepeats', -1)), 'stopWhenEmpty': bool(action.get('act.stopWhenEmpty'))}

    # -------------------------------------------------------------- particles
    def particle_fields(self, record: dict, path: str) -> dict:
        """A particle archive as JsonUtility-friendly lists: integers (flags, enums,
        ids; booleans as 0/1, UINT32_MAX as -1), floats, strings and property lines
        (keyframed, parametrised, constant vectors and packed colours)."""
        ints, values, strings, lines = [], [], [], []
        for key in sorted(record):
            value = record[key]
            if key.startswith('#') or key in PARTICLE_STRUCTURE:
                continue
            if isinstance(value, bool):
                ints.append({'name': key, 'value': int(value)})
            elif isinstance(value, int):
                if 'olor' in key and not key.endswith('Behavior'):
                    lines.append(packed_color_line(key, value))
                    self.stats['packed particle colours'][key] += 1
                    continue
                value = -1 if value == UINT32_MAX else value
                if not -2 ** 31 <= value < 2 ** 31:
                    raise SystemExit(f'{path}: particle field {key} = {value} overflows int32')
                ints.append({'name': key, 'value': value})
            elif isinstance(value, float):
                values.append({'name': key, 'value': value})
            elif isinstance(value, str):
                strings.append({'name': key, 'value': value})
            elif isinstance(value, (list, tuple)) and value and all(isinstance(x, bool) for x in value):
                ints.extend({'name': f'{key}{k}', 'value': int(x)} for k, x in enumerate(value))
            elif isinstance(value, (list, tuple)) and value and all(
                    isinstance(x, (int, float)) and not isinstance(x, bool) for x in value):
                lines.append(constant_line(key, value))
            else:
                line = property_line(key, value)
                if line is None:
                    self.stats['particle fields not exported'][key] += 1
                    continue
                if line['parameter']:
                    self.warnings.append(f'{path}: {key} follows the external parameter {line["parameter"]}')
                lines.append(line)
        return {'ints': ints, 'values': values, 'strings': strings, 'lines': lines}

    def particle_effects(self) -> None:
        """Every effect with its emitters on the best quality (clones followed),
        their layers, the meshes mesh layers draw and the sprites layers sample."""
        emitters = {node_id(d['#id']): d for d in self.data_nodes
                    if d.get('##name') == 'ParticleEmitterNode' and d.get('#id') is not None}

        def resolve(eid, path):
            node, seen = emitters.get(eid), set()
            while node is not None and node.get('clone'):
                if eid in seen:
                    raise SystemExit(f'{path}: emitter clone cycle at {eid}')
                seen.add(eid)
                eid = node_id(node['reference.id'])
                node = emitters.get(eid)
            if node is None:
                raise SystemExit(f'{path}: emitter {eid} is missing')
            return eid, node

        for effect in self.effects:
            pe = effect.pop('_component')
            entity = effect.pop('_entity')
            path = effect['path']
            start = self.particle_starts.get(id(entity))
            repeats = int(pe.get('pe.repeatsCount', 0))
            effect.update({
                'autoplay': start is not None, 'startDelay': start['delay'] if start else 0.0,
                'startDelayVariation': start['delayVariation'] if start else 0.0,
                'duration': float(pe.get('pe.effectDuration', 0.0)), 'repeats': -1 if repeats == UINT32_MAX else repeats,
                'startFromTime': float(pe.get('pe.startFromTime', 0.0)),
                'clearOnRestart': bool(pe.get('pe.clearOnRestart', True)), 'stopWhenEmpty': bool(pe.get('pe.stopWhenEmpty')),
                'inheritScale': bool(pe.get('pe.inheritScale')), 'emitters': []})
            for instance in items(pe.get('pe.emitters')):
                variants = {str(x.get('emitter.quality')): x for x in items(instance.get('emitter.data')) if isinstance(x, dict)}
                quality = next((q for q in PARTICLE_QUALITY if q in variants), None)
                if quality is None:
                    raise SystemExit(f'{path}: emitter without a Default variant')
                eid, node = resolve(node_id(variants[quality]['emitter.id']), path)
                emitter = {'name': str(node.get('name', '')), 'id': eid, 'quality': quality,
                           'position': rounded(unity_point(instance.get('emitter.position', (0, 0, 0)))),
                           'rotation': rounded(unity_quaternion(instance.get('emitter.rotation', (0, 0, 0, 1))), 7),
                           'scale': rounded(unity_scale(instance.get('emitter.scale', (1, 1, 1)))),
                           'fields': self.particle_fields(node, path), 'layers': []}
                for layer in items(node.get('layers')):
                    kind = str(layer.get('layerType', ''))
                    label = f'{path}/{emitter["name"]}/{layer.get("name")}'
                    if kind not in PARTICLE_LAYER_TYPES:
                        self.warnings.append(f'{label}: {kind} layer is not exported')
                        continue
                    if items(layer.get('simplifiedForces')):
                        self.warnings.append(f'{label}: simplified forces are not exported')
                    record = {'name': str(layer.get('name', '')), 'type': kind, 'mesh': -1,
                              'fields': self.particle_fields(layer, label),
                              'forces': [self.particle_fields(f, label) for f in items(layer.get('forces')) if isinstance(f, dict)],
                              'textures': []}
                    if kind == 'mesh':
                        record['mesh'] = self.particle_mesh(int(layer['meshGeometryId']), label)
                    for field, flag, sampler, slot in PARTICLE_TEXTURES:
                        reference = str(layer.get(field) or '')
                        if not reference or (flag is not None and not layer.get(flag)):
                            continue
                        record['textures'].append({'slot': slot, 'sprite': self.sprite(reference, int(layer.get(sampler, DEFAULT_SAMPLER)), slot)})
                    self.stats['particle layers'][f'{kind} blending={layer.get("blending")} orientation={layer.get("particleOrientation")}'] += 1
                    emitter['layers'].append(record)
                effect['emitters'].append(emitter)
            self.stats['particle effects'][f'autoplay={effect["autoplay"]}'] += 1

    def particle_mesh(self, gid: int, label: str) -> int:
        """The polygon group a mesh layer draws; v is flipped because sprite sheets
        keep the image's orientation (Unity v = 1 is the sheet's top row)."""
        if gid in self.particle_meshes:
            return self.particle_meshes[gid]
        group = self.groups.get(gid)
        if group is None:
            raise SystemExit(f'{label}: particle mesh {gid} is missing')
        v = decode_vertices(group)
        triangles = decode_indices(group)
        if triangles.size % 3:
            raise SystemExit(f'{label}: particle mesh {gid} is not a triangle list')
        s = unity_streams(v)
        s['uv0'] = np.stack([s['uv0'][:, 0], 1.0 - s['uv0'][:, 1]], axis=1).astype(np.float32)
        tri = triangles.reshape(-1, 3)[:, [0, 2, 1]]
        if tri.size and tri.max() >= s['count']:
            raise SystemExit(f'{label}: index outside particle mesh {gid}')
        mesh = {'name': f'particle_{gid}', 'vertexCount': s['count'], 'streams': stream_bits(s['present']),
                'submeshes': [tri.reshape(-1).astype(np.uint32)], 'bindposes': None, 'groups': [gid], 'particle': True,
                'joints': None, 'weights': None, 'bindframe': None}
        for name in ('positions', 'normals', 'tangents', 'colors', 'uv0', 'uv1', 'binormals', 'uv3', 'uv4'):
            mesh[name] = s[name]
        self.meshes.append(mesh)
        self.particle_meshes[gid] = len(self.meshes) - 1
        return self.particle_meshes[gid]

    def sprite_source(self, reference: str) -> dict:
        """A particle sprite: a WebP whose `extr` chunk holds the descriptor, or a
        `.txt` descriptor naming its atlas pages."""
        if reference in self.sprite_sources:
            return self.sprite_sources[reference]
        relative = reference[len('~res:/'):] if reference.startswith('~res:/') else reference
        packed = self.src.packs / (relative + '.packed.webp')
        if self.src.exists(packed):
            blob = self.src.read(packed)
            extra = [data for tag, data in riff_chunks(blob) if tag == b'extr']
            if not extra:
                raise SystemExit(f'{reference}: packed sprite without a descriptor')
            descriptor = parse_sprite_descriptor(extra[0].decode('utf-8'))
            pages = [blob]
            files = [self.src.relative(packed)]
        else:
            text = next((base / (relative + '.txt') for base in (self.src.apk, self.src.packs)
                         if self.src.exists(base / (relative + '.txt'))), None)
            if text is None:
                raise SystemExit(f'sprite {reference} is missing in the client')
            descriptor = parse_sprite_descriptor(self.src.read(text).decode('utf-8'))
            pages = []
            files = [self.src.relative(text)]
            for name in descriptor['pages']:
                page = text.parent / name
                if not self.src.exists(page):
                    raise SystemExit(f'{reference}: atlas page {name} is missing')
                pages.append(self.src.read(page))
                files.append(self.src.relative(page))
        source = {'descriptor': descriptor, 'pages': pages, 'files': files}
        self.sprite_sources[reference] = source
        return source

    def sprite(self, reference: str, sampler: int, slot: str) -> int:
        state = decode_sampler(sampler)
        key = (reference, state['wrapU'], state['wrapV'], state['minFilter'], state['magFilter'], state['mipFilter'],
               state['anisotropy'], slot in ('flowmap', 'noise', 'vertexAnimation'))
        if key in self.sprite_index:
            return self.sprite_index[key]
        source = self.sprite_source(reference)
        descriptor = source['descriptor']
        frames = descriptor['frames']
        columns, rows = sheet_layout(len(frames))
        width, height = descriptor['size']
        trimmed = sum(1 for f in frames if f['rect'][2:] != [width, height] or f['offset'] != [0, 0])
        stem = reference[len('~res:/Gfx/Particles/'):] if reference.startswith('~res:/Gfx/Particles/') else reference
        name = ''.join(ch if ch.isalnum() or ch in '-_' else '_' for ch in stem.replace('/', '_'))
        taken = {s['name'] for s in self.sprites}
        unique, n = name, 1
        while unique in taken:
            unique, n = f'{name}_s{n}', n + 1
        self.sprites.append({'name': unique, 'file': f'Sprites/{unique}.png', 'source': reference, 'files': source['files'],
                             'frames': len(frames), 'columns': columns, 'rows': rows,
                             'frameWidth': width, 'frameHeight': height, 'trimmedFrames': trimmed,
                             'data': key[-1], 'sampler': sampler, 'wrapU': state['wrapU'], 'wrapV': state['wrapV'],
                             'minFilter': state['minFilter'], 'magFilter': state['magFilter'],
                             'mipFilter': state['mipFilter'], 'anisotropy': state['anisotropy'], '_reference': reference})
        self.sprite_index[key] = len(self.sprites) - 1
        return self.sprite_index[key]

    def write_sprites(self, output: Path) -> None:
        """Texture sheets in the image's own orientation, frame k at column k % columns
        of row k // columns from the top-left like Unity's texture sheet animation.
        A frame covers its whole cell: the client maps a frame's atlas rectangle onto
        the full particle quad, so trimmed frames are stretched as well."""
        import io
        folder = output / 'Sprites'
        folder.mkdir(parents=True, exist_ok=True)
        decoded = {}
        for record in self.sprites:
            reference = record.pop('_reference')
            source = self.sprite_sources[reference]
            if reference not in decoded:
                decoded[reference] = [Image.open(io.BytesIO(page)).convert('RGBA') for page in source['pages']]
            pages = decoded[reference]
            width, height = record['frameWidth'], record['frameHeight']
            sheet = Image.new('RGBA', (width * record['columns'], height * record['rows']), (0, 0, 0, 0))
            for k, frame in enumerate(source['descriptor']['frames']):
                x, y, w, h = frame['rect']
                page = pages[frame['page']]
                if x < 0 or y < 0 or x + w > page.width or y + h > page.height:
                    raise SystemExit(f'{reference}: frame {k} outside its atlas page')
                region = page.crop((x, y, x + w, y + h))
                if (w, h) != (width, height):
                    region = region.resize((width, height), Image.BILINEAR)
                sheet.paste(region, ((k % record['columns']) * width, (k // record['columns']) * height))
            pixels = np.asarray(sheet)
            record['alpha'] = bool(pixels[:, :, 3].min() < 255)
            (sheet if record['alpha'] else sheet.convert('RGB')).save(folder / Path(record['file']).name,
                                                                     optimize=False, compress_level=6)

    def read_camera(self, entity) -> None:
        cam = (component(entity, 'CameraComponent') or {}).get('cc.camera') or {}
        position = floats(cam['cam.position'])
        target = floats(cam['cam.target'])
        up = floats(cam['cam.up'])
        forward = np.subtract(target, position)
        forward = forward / (np.linalg.norm(forward) or 1.0)
        self.camera = {'position': rounded(unity_point(position)), 'forward': rounded(unity_point(forward)),
                       'up': rounded(unity_point(up)), 'fov': float(cam.get('cam.fov', 70.0)),
                       'near': float(cam.get('cam.znear', 1.0)), 'far': float(cam.get('cam.zfar', 5000.0))}

    # -------------------------------------------------------------- lights per render object
    def assign_lights(self) -> None:
        """Up to MAX_POINT_LIGHTS point lights whose radius reaches the render object's
        world bounding box, nearest to the box centre first."""
        points = [(k, np.array(l['positionDava']), l['radius']) for k, l in enumerate(self.lights)
                  if l['type'] == LIGHT_POINT and l['radius'] > 0]
        for r in self.renderers:
            lo, hi = r['_lo'], r['_hi']
            center = (lo + hi) / 2
            candidates = []
            for k, p, radius in points:
                gap = np.linalg.norm(np.maximum(0.0, np.maximum(lo - p, p - hi)))
                if gap < radius:
                    candidates.append((float(np.linalg.norm(p - center)), k))
            candidates.sort()
            chosen = [k for _, k in candidates[:MAX_POINT_LIGHTS]]
            r['lights'] = chosen + [-1] * (MAX_POINT_LIGHTS - len(chosen))
            self.stats['point light candidates'][len(candidates)] += 1

    def tank_lights(self) -> list[int]:
        """The lights the client would pick for a tank-sized box on the spawn point."""
        p = np.array(self.spawn['positionDava'])
        lo, hi = p + [-3.5, -1.8, 0.0], p + [3.5, 1.8, 3.0]
        center = (lo + hi) / 2
        candidates = sorted((float(np.linalg.norm(np.array(l['positionDava']) - center)), k) for k, l in enumerate(self.lights)
                            if l['type'] == LIGHT_POINT and np.linalg.norm(np.maximum(0, np.maximum(lo - l['positionDava'], np.array(l['positionDava']) - hi))) < l['radius'])
        return [k for _, k in candidates[:MAX_POINT_LIGHTS]]

    # -------------------------------------------------------------- environment
    def environment(self) -> dict:
        sets = self.scene_sets.get('Default') or []
        by_type = {c.get('comp.typename'): c for c in sets if isinstance(c, dict)}
        sun = by_type['DirectionalLightComponent']
        light = sun['light']
        rotation = sun['transformNode']['transform']['rotation']
        r = quaternion_matrix(rotation)
        axes = {name: rounded(unity_point(r @ axis)) for name, axis in
                (('x', [1, 0, 0]), ('y', [0, 1, 0]), ('z', [0, 0, 1]))}
        shadow = by_type['DirectionalShadowComponent']['shadowConfigHigh']['directionalShadowConfig']
        flat = {}

        def flatten(prefix, value):
            if isinstance(value, dict):
                for k, v in value.items():
                    flatten(f'{prefix}{k}.', v)
            elif isinstance(value, (int, float)) and not isinstance(value, bool):
                flat[prefix[:-1]] = float(value)

        flatten('', shadow)
        ibl = by_type['IBLComponent']
        render = by_type['SceneRenderConfigComponent']
        config = next((d for d in self.data_nodes if d.get('##name') == 'SceneRenderConfig'), {})
        globals_ = [{'name': k, 'values': rounded(material_property(config, k), 7)} for k in sorted(config.get('properties') or {})]
        return {
            'sun': {'color': rounded(color(light, 'color')), 'ambient': rounded(color(light, 'ambColor')),
                    'intensity': float(light['intensity']), 'flags': int(light.get('flags', 0)),
                    'rotationDava': rounded(rotation, 7), 'rotation': rounded(unity_quaternion(rotation), 7),
                    'axisX': axes['x'], 'axisY': axes['y'], 'axisZ': axes['z'],
                    # The light shines along -Y of its rotation: ray-casting the
                    # lightmapped vertices towards +Y agrees with the baked shadow of
                    # pbrLightmap.x for 96.5 % of them, the other axes for 70 % at best.
                    'toLight': axes['y']},
            'shadow': [{'name': k, 'values': [round(v, 6)]} for k, v in sorted(flat.items())],
            'ibl': {'diffuse': 'ibl_diffuse', 'specular': 'ibl_specular',
                    'multiplier': float(ibl.get('ibl.environmentMultiplier', 1.0)),
                    'gamma': float(ibl.get('ibl.environmentGamma', 1.0)),
                    'groundFactor': float(ibl.get('ibl.environmentGroundFactor', 0.0)),
                    'enabled': bool(ibl.get('ibl.enableIBL', True)),
                    'rotationDava': rounded(ibl['transformNode']['transform']['rotation'], 7),
                    'rotation': rounded(unity_quaternion(ibl['transformNode']['transform']['rotation']), 7)},
            'fog': {'color': rounded(render['fogColor'][:3]), 'density': float(render['fogDensity']),
                    'end': float(render['fogEnd']), 'vertexFog': bool(render.get('VERTEX_FOG')),
                    'atmosphere': bool(render.get('FOG_ATMOSPHERE')), 'pointLights': bool(render.get('ALLOW_POINT_LIGHTS'))},
            'globals': globals_,
            'dfgLut': {'file': 'dfg_lut.bytes', 'size': DFG_SIZE, 'samples': DFG_SAMPLES, 'format': 'RGHalf'},
        }

    # -------------------------------------------------------------- prune
    def prune(self) -> None:
        needed = set()
        for r in self.renderers:
            needed.add(r['node'])
            needed.update(r['bones'])
        for group in (self.lights, self.sounds, self.effects, self.animations):
            needed.update(x['node'] for x in group)
        for s in self.states:
            needed.add(s['node'])
            needed.update(s['states'])
        for owner, joints in self.skeleton_joints.items():
            if owner in needed:
                needed.update(joints)
        for k, n in enumerate(self.nodes):
            if n['kind'] == 'socket':
                needed.add(k)
        keep = set()
        for k in needed:
            while k != -1 and k not in keep:
                keep.add(k)
                k = self.nodes[k]['parent']
        order = sorted(keep)
        remap = {old: new for new, old in enumerate(order)}
        remap[-1] = -1
        self.notes['pruned nodes'] = len(self.nodes) - len(order)
        self.nodes = [dict(self.nodes[k], parent=remap[self.nodes[k]['parent']]) for k in order]
        for r in self.renderers:
            r['node'] = remap[r['node']]
            r['bones'] = [remap[b] for b in r['bones']]
        for group in (self.lights, self.sounds, self.effects, self.animations):
            for x in group:
                x['node'] = remap[x['node']]
        for s in self.states:
            s['node'] = remap[s['node']]
            s['states'] = [remap[k] for k in s['states']]
        self.remap = remap

    # -------------------------------------------------------------- rebrand
    def rebrand(self) -> None:
        """The port's art in place of the client's two WoT Blitz logos
        (halloween_hangar_rebrand): the wall emblem's mesh is swapped here, its
        textures and the billboard screen's as write_textures decodes them."""
        def renderer(path):
            found = [k for k, r in enumerate(self.renderers) if r['path'] == path]
            if len(found) != 1:
                raise SystemExit(f'{path}: {len(found)} render objects, the rebrand expects one')
            return found[0]

        def own_textures(index, sources):
            # The replaced textures belong to this render object alone.
            sources = set(sources)
            for k, r in enumerate(self.renderers):
                sampled = {self.textures[t['texture']]['source'] for m in r['materials'] for t in self.materials[m]['textures']}
                if k == index and not sources <= sampled:
                    raise SystemExit(f'{r["path"]} does not sample {sorted(sources - sampled)}')
                if k != index and sampled & sources:
                    raise SystemExit(f'{r["path"]} also samples {sorted(sampled & sources)}')
            return [t for t, record in enumerate(self.textures) if record['source'] in sources]

        index = renderer(rebrand.SIGN_PATH)
        r = self.renderers[index]
        mesh = self.meshes[r['mesh']]
        if sum(x['mesh'] == r['mesh'] for x in self.renderers) != 1 or r['skinned'] or len(r['materials']) != 1:
            raise SystemExit(f'{r["path"]}: the emblem mesh is shared, skinned or has several materials')
        new = rebrand.sign_mesh()
        lo, hi = new['positions'].min(0), new['positions'].max(0)
        # The point lights were picked for the client emblem's box, which holds the numeral.
        if (lo < np.array(r['boundsMin']) - 1e-4).any() or (hi > np.array(r['boundsMax']) + 1e-4).any():
            raise SystemExit(f"{r['path']}: the numeral leaves the client emblem's bounds")
        n = len(new['positions'])
        before = (mesh['vertexCount'], int(sum(s.size for s in mesh['submeshes']) // 3))
        mesh.update({'vertexCount': n, 'streams': MESH_NORMAL | MESH_TANGENT | MESH_UV0 | MESH_UV1 | MESH_BINORMAL,
                     'submeshes': [new['triangles']], 'groups': [],
                     'positions': new['positions'], 'normals': new['normals'], 'tangents': new['tangents'],
                     'colors': np.full((n, 4), 255, np.uint8), 'uv0': new['uv0'], 'uv1': new['uv1'],
                     'binormals': new['binormals'], 'uv3': np.zeros((n, 2), np.float32),
                     'uv4': np.zeros((n, 2), np.float32)})
        r['boundsMin'], r['boundsMax'] = rounded(lo), rounded(hi)
        center = ((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, rebrand.FRONT_Z)
        self.rebrands.append({
            'name': 'sign', 'path': r['path'], 'renderer': index, 'mesh': r['mesh'],
            'textures': own_textures(index, rebrand.SIGN_TEXTURES),
            'client': 'WoT Blitz shield-and-bolt emblem', 'port': 'Roman numeral VI',
            'vertices': [before[0], n], 'triangles': [before[1], int(new['triangles'].size // 3)],
            'view': rebrand.close_up(SWAP @ self.world[r['node']] @ SWAP, center, (0, 0, -1), *rebrand.SIGN_VIEW)})
        index = renderer(rebrand.SCREEN_PATH)
        r = self.renderers[index]
        self.rebrands.append({
            'name': 'screen', 'path': r['path'], 'renderer': index, 'mesh': r['mesh'],
            'textures': own_textures(index, [rebrand.SCREEN_TEXTURE]),
            'client': 'WORLD of TANKS BLITZ logo on the billboard screen', 'port': rebrand.SCREEN_IMAGE,
            'view': rebrand.close_up(SWAP @ self.world[r['node']] @ SWAP, rebrand.SCREEN_CENTER, (0, 0, -1),
                                     *rebrand.SCREEN_VIEW)})

    # -------------------------------------------------------------- writers
    def write_meshes(self, path: Path) -> None:
        """`HWMESH01`, u32 mesh count; per mesh u16 name length + UTF-8 name, u32
        vertex count, stream bits, submesh count and bone count, then the streams
        (f32 unless noted): positions xyz, normals xyz, [tangents xyzw], [colors
        RGBA u8], uv0, [uv1], [binormals xyz -> UV2], [DAVA uv2 -> UV3], [DAVA uv3 -> UV4],
        [bone slots i32 x4, weights x4], [bind frame x8: UV5 = (bind normal, bind
        weight), UV6 = (bind tangent, skinned weight)]; per submesh u32 index count
        and u32 indices; bind poses as 16 floats row-major m[r][c]."""
        out = [b'HWMESH01', struct.pack('<I', len(self.meshes))]
        for mesh in self.meshes:
            name = mesh['name'].encode('utf-8')
            bones = 0 if mesh['bindposes'] is None else len(mesh['bindposes'])
            out.append(struct.pack('<H', len(name)) + name)
            out.append(struct.pack('<4I', mesh['vertexCount'], mesh['streams'], len(mesh['submeshes']), bones))
            s = mesh['streams']
            out.append(mesh['positions'].astype('<f4').tobytes())
            out.append(mesh['normals'].astype('<f4').tobytes())
            if s & MESH_TANGENT:
                out.append(mesh['tangents'].astype('<f4').tobytes())
            if s & MESH_COLOR:
                out.append(mesh['colors'].astype(np.uint8).tobytes())
            out.append(mesh['uv0'].astype('<f4').tobytes())
            if s & MESH_UV1:
                out.append(mesh['uv1'].astype('<f4').tobytes())
            if s & MESH_BINORMAL:
                out.append(mesh['binormals'].astype('<f4').tobytes())
            if s & MESH_UV3:
                out.append(mesh['uv3'].astype('<f4').tobytes())
            if s & MESH_UV4:
                out.append(mesh['uv4'].astype('<f4').tobytes())
            if s & MESH_SKIN:
                out.append(mesh['joints'].astype('<i4').tobytes())
                out.append(mesh['weights'].astype('<f4').tobytes())
            if s & MESH_BINDFRAME:
                out.append(mesh['bindframe'].astype('<f4').tobytes())
            for indices in mesh['submeshes']:
                out.append(struct.pack('<I', indices.size) + indices.astype('<u4').tobytes())
            if bones:
                out.append(mesh['bindposes'].astype('<f4').tobytes())  # row-major m[r][c]
        path.write_bytes(b''.join(out))

    def write_clips(self, path: Path) -> None:
        out = [b'HWCLIP01', struct.pack('<I', len(self.clips))]
        for clip in self.clips:
            name = clip['name'].encode('utf-8')
            out.append(struct.pack('<H', len(name)) + name + struct.pack('<fI', clip['length'], len(clip['_curves'])))
            for curve in clip['_curves']:
                p = curve['path'].encode('utf-8')
                dims = len(curve['values'][0])
                out.append(struct.pack('<H', len(p)) + p + struct.pack('<BBI', curve['property'], dims, len(curve['times'])))
                out.append(np.asarray(curve['times'], '<f4').tobytes())
                out.append(np.asarray(curve['values'], '<f4').reshape(-1).tobytes())
        path.write_bytes(b''.join(out))

    def write_textures(self, output: Path) -> None:
        folder = output / 'Textures'
        folder.mkdir(parents=True, exist_ok=True)
        decoded = {}
        for record in self.textures:
            path = record.pop('_path')
            if path not in decoded:
                header, width, height, pixels = decode_pvr(self.src.read(path))
                block = 'ASTC %dx%d' % pvr2png.ASTC_BLOCKS[header.pixel_format]
                mips = int(header.mip_count)
                art = rebrand.replacement(record['source'], pixels)
                if art is not None:
                    # The port's art: its own size, a full mip chain and a finer block than the client's.
                    pixels, block = art
                    height, width = pixels.shape[:2]
                    mips = max(width, height).bit_length() if mips > 1 else 1
                decoded[path] = (header, width, height, pixels, block, mips, art is not None)
            header, width, height, pixels, block, mips, replaced = decoded[path]
            alpha = bool(pixels[:, :, 3].min() < 255)
            two_channel = bool(np.array_equal(pixels[:, :, 0], pixels[:, :, 1]) and np.array_equal(pixels[:, :, 1], pixels[:, :, 2]))
            record.update({'width': width, 'height': height, 'srgb': bool(header.colour_space), 'alpha': alpha,
                           'grey': two_channel, 'mips': mips, 'format': block})
            if replaced:
                record['replaced'] = True
            write_png(output / record['file'], pixels, alpha)
            self.stats['texture formats'][f'{record["format"]} srgb={record["srgb"]}'] += 1

    def write_cubemaps(self, output: Path) -> list[dict]:
        folder = output / 'Cubemaps'
        folder.mkdir(parents=True, exist_ok=True)
        sets = {c.get('comp.typename'): c for c in self.scene_sets.get('Default') or []}
        ibl = sets['IBLComponent']
        records = []
        self.cube_pixels = {}
        for name, key in (('ibl_diffuse', 'ibl.diffuse'), ('ibl_specular', 'ibl.specular')):
            path = self.texture_path(str(ibl[key]))
            blob = self.src.read(path)
            header = pvr2png.PvrHeader(blob)
            if header.faces != 6:
                raise SystemExit(f'{path} is not a cubemap')
            mips = []
            for level in range(max(1, header.mip_count)):
                faces = []
                for face in range(6):
                    _, width, height, pixels = decode_pvr(blob, level, face)
                    if width != height:
                        raise SystemExit(f'{path}: face {face} of mip {level} is {width}x{height}')
                    faces.append(np.ascontiguousarray(pixels))
                mips.append(faces)
            self.cube_pixels[name] = mips
            write_cube(folder / f'{name}.bytes', mips)
            records.append({'name': name, 'file': f'Cubemaps/{name}.bytes', 'size': int(header.width),
                            'mips': int(max(1, header.mip_count)),
                            'srgb': bool(header.colour_space), 'source': self.src.relative(path),
                            'format': 'ASTC %dx%d' % pvr2png.ASTC_BLOCKS[header.pixel_format]})
        return records

    def vehicle_lighting(self, output: Path, environment: dict, cubemaps: list[dict]) -> None:
        """What Unity's own lighting needs for the player's vehicle, which the
        hangar shaders do not draw: the specular map turned to Unity directions
        (the custom reflection) and the irradiance map at the zenith, the horizon
        and the nadir (trilight ambient), both through the client's environment
        lookup."""
        ibl = environment['ibl']
        rotation = environment_rotation(ibl['rotationDava'])
        specular = self.cube_pixels[ibl['specular']]
        oriented = [unity_oriented(faces, rotation) for faces in specular]
        write_cube(output / 'Cubemaps' / f"{ibl['specular']}_unity.bytes", oriented)
        base = next(c for c in cubemaps if c['name'] == ibl['specular'])
        cubemaps.append(dict(base, name=f"{ibl['specular']}_unity", file=f"Cubemaps/{ibl['specular']}_unity.bytes",
                             source=base['source'] + ' (Unity oriented)'))
        diffuse = [face.astype(np.float64) / 255.0 for face in self.cube_pixels[ibl['diffuse']][0]]

        def irradiance(direction):
            d = np.asarray(direction, np.float64)[None, :]
            d = d / np.linalg.norm(d, axis=1, keepdims=True)
            return sample_cube(diffuse, dava_lookup(d, rotation))[0]

        angles = np.arange(16) * math.pi / 8.0
        horizon = sample_cube(diffuse, dava_lookup(np.stack([np.cos(angles), np.zeros(16), np.sin(angles)], 1), rotation)).mean(0)
        environment['vehicle'] = {
            'reflection': f"{ibl['specular']}_unity",
            'environmentRotation': round(rotation, 7),
            # Linear RGBA; Unity's RenderSettings take them as gamma colours.
            'ambientSky': rounded(irradiance([0, 1, 0]), 7),
            'ambientEquator': rounded(horizon, 7),
            'ambientGround': rounded(irradiance([0, -1, 0]), 7),
        }

    def run(self, output: Path, textures: bool) -> dict:
        self.walk()
        if self.spawn is None or self.camera is None:
            raise SystemExit('the hangar has no spawn point or camera')
        self.rebrand()
        self.particle_effects()
        self.assign_lights()
        tank_lights = self.tank_lights()
        environment = self.environment()
        self.prune()
        for r in self.renderers:
            self.stats['shadow casting'][r['shadowCasting']] += 1
        output.mkdir(parents=True, exist_ok=True)
        for sub in ('Textures', 'Cubemaps', 'Sprites'):
            if (output / sub).exists() and textures:
                for old in (output / sub).iterdir():
                    if old.suffix != '.meta':
                        old.unlink()
        self.write_meshes(output / 'meshes.bytes')
        self.write_clips(output / 'clips.bytes')
        cubemaps = []
        if textures:
            self.write_textures(output)
            cubemaps = self.write_cubemaps(output)
            self.vehicle_lighting(output, environment, cubemaps)
            self.write_sprites(output)
            (output / 'dfg_lut.bytes').write_bytes(dfg_lut(DFG_SIZE, DFG_SAMPLES).astype('<f2').tobytes())
            write_import(output / 'import.json', self.textures, self.sprites, output)
        else:
            for record in self.textures:
                record.pop('_path', None)
            for record in self.sprites:
                record.pop('_reference', None)
        environment['tankLights'] = tank_lights
        scene = {
            'map': MAP, 'nodes': self.nodes,
            'meshes': [{'name': m['name'], 'vertexCount': m['vertexCount'], 'submeshCount': len(m['submeshes']),
                        'streams': m['streams'], 'boneCount': 0 if m['bindposes'] is None else len(m['bindposes']),
                        'groups': m['groups'], 'particle': m['particle']} for m in self.meshes],
            'materials': self.materials, 'textures': self.textures, 'cubemaps': cubemaps,
            'renderers': [{k: v for k, v in r.items() if not k.startswith('_')} for r in self.renderers],
            'lights': self.lights,
            'clips': [{k: v for k, v in c.items() if not k.startswith('_')} for c in self.clips],
            'animations': self.animations, 'states': self.states, 'sounds': self.sounds,
            'effects': self.effects, 'sprites': self.sprites,
            'spawn': self.spawn, 'camera': self.camera, 'environment': environment,
            'rebrand': self.rebrands,
        }
        return scene

    def report(self) -> dict:
        return {
            'counts': {'nodes': len(self.nodes), 'renderers': len(self.renderers), 'meshes': len(self.meshes),
                       'vertices': int(sum(m['vertexCount'] for m in self.meshes)),
                       'triangles': int(sum(s.size for m in self.meshes for s in m['submeshes']) // 3),
                       'materials': len(self.materials), 'textures': len(self.textures), 'lights': len(self.lights),
                       'clips': len(self.clips), 'animations': len(self.animations), 'states': len(self.states),
                       'sounds': len(self.sounds), 'effects': len(self.effects),
                       'emitters': sum(len(e.get('emitters', [])) for e in self.effects),
                       'layers': sum(len(m['layers']) for e in self.effects for m in e.get('emitters', [])),
                       'particleMeshes': len(self.particle_meshes), 'sprites': len(self.sprites)},
            'maxWorldError': self.max_world_error, 'maxBindError': self.max_bind_error,
            'rebrand': self.rebrands, 'notes': dict(self.notes), 'skipped': self.skipped, 'warnings': self.warnings,
            'stats': {k: {str(a): b for a, b in sorted(v.items(), key=lambda x: str(x[0]))} for k, v in self.stats.items()},
        }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('--source', type=Path, default=CLIENT, help='Blitz 11.21 client dump (read-only)')
    parser.add_argument('--tools', type=Path, default=DAVA_TOOLS, help='blitz-legacy-godot/tools with the DAVA readers')
    parser.add_argument('--output', type=Path, default=OUTPUT)
    parser.add_argument('--no-textures', action='store_true', help='skip texture decoding (quick structural run)')
    args = parser.parse_args()
    load_tools(args.tools)
    src = Source(args.source)
    export = HangarExport(src)
    scene = export.run(args.output, not args.no_textures)
    (args.output / 'scene.json').write_text(json.dumps(scene, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
    report = export.report()
    (args.output / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding='utf-8')
    (args.output / 'provenance.json').write_text(json.dumps(
        {'source': str(src.root), 'map': MAP, 'files': dict(sorted(src.files.items())),
         'art': {} if args.no_textures else {rebrand.SCREEN_IMAGE: rebrand.screen_digest()}}, indent=1), encoding='utf-8')
    print(json.dumps(report['counts']), 'worldErr', f'{report["maxWorldError"]:.2g}', 'bindErr', f'{report["maxBindError"]:.2g}')
    print('notes', json.dumps(report['notes']))
    print('warnings', len(report['warnings']))


if __name__ == '__main__':
    main()
