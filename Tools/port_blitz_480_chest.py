#!/usr/bin/env python3
"""Port the 4.8 container-opening scene (3d/Maps/chest/Box_anim.sc2).

The Unity runtime replays this scene the way the 4.8 client did: the six box
parts follow their AnimationData keys, every particle effect is simulated by a
port of DAVA's ParticleEffectSystem and drawn with the authored blending,
camera, light and sorting. Therefore this porter exports source data, not a
pre-baked approximation:

* meshes.bytes holds the background plane and the box parts. Positions,
  normals, tangents and binormals are reflected once from DAVA right-handed
  Z-up into Unity axes (x, z, y) and the triangle winding is flipped to keep the
  authored front faces. The background is baked into world space; box parts
  stay in their local space because their transform is animated.
* scene.json keeps every matrix, the camera, the light, the hardpoint and all
  particle data in DAVA space (row-vector matrices, Z-up). Particle property
  lines are stored exactly as DAVA builds them from YAML, including the
  load-time trimming of layer lines to [startTime, endTime].
* Textures/ and Sprites/ hold the decoded scene textures and one PNG per
  particle sprite frame (the packed atlas rectangle, which is what DAVA maps on
  a particle quad).

Run from the project root:
  BLITZ_PACK=../wotblitz4.8.0files .venv/bin/python Tools/port_blitz_480_chest.py
"""
import contextlib
import hashlib
import io
import json
import math
import shutil
import struct
import sys
from pathlib import Path

import numpy as np
import yaml
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import port_blitz_client_map as p  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / 'Assets/Blitz/Resources/Chest480'
AUDIO_OUT = ROOT / 'Assets/Blitz/Resources/Audio'
# The 4.8 pack ships no UI sound bank. These are the CHEST_OPENING and
# CHEST_REWARD sounds (GUI/buttons/box_prize, GUI/buttons/whoosh_prize)
# already extracted for the Godot port.
AUDIO_SOURCE = ROOT.parent / 'blitz-legacy-godot/maps/components/chest'
AUDIO = ['box_prize.wav', 'whoosh_prize.wav']
PREFIX = 'Chest480/'
SCENE = '3d/Maps/chest/Box_anim.sc2'

# Action types from DAVA ActionComponent::Action::eType.
ACTION_PARTICLE_START = 1
ACTION_ANIMATION_START = 6
ACTION_EVENT_ADDED_TO_SCENE = 1

# ParticleLayer lines validated by UpdatePropertyLineOnLoad().
TRIMMED_LINES = ['life', 'lifeVariation', 'number', 'numberVariation', 'size', 'sizeVariation',
                 'velocity', 'velocityVariation', 'spin', 'spinVariation', 'angle', 'angleVariation']
EMITTER_TYPES = {'point': 0, 'line': 1, 'rect': 1, 'oncircle': 2, 'oncircle_edges': 3, 'shockwave': 3, 'sphere': 4}
ORIENTATIONS = {'camera': 1}

sources = {}


def record(path):
    path = Path(path).resolve()
    try:
        key = str(path.relative_to(p.PACK))
    except ValueError:
        key = str(path.relative_to(ROOT.parent))
    sources[key] = hashlib.sha256(path.read_bytes()).hexdigest()


def floats(value, count):
    if isinstance(value, (bytes, bytearray)):
        return [float(x) for x in struct.unpack('<%df' % (len(value) // 4), value)[:count]]
    return [float(x) for x in list(value)[:count]]


def row_matrix(value):
    return np.array(floats(value, 16), dtype=float).reshape(4, 4)


def flat(matrix):
    return [float(x) for x in np.asarray(matrix, dtype=float).reshape(-1)]


def truth(value):
    return value is True or str(value).lower() == 'true'


# --------------------------------------------------------------------------
# DAVA property lines (Particles/ParticlePropertyLine.cpp)
# --------------------------------------------------------------------------

def is_scalar(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def line_keys(node, dim):
    """Return [(t, [values])] for one YAML property node, or None."""
    if node is None:
        return None
    if dim == 1:
        if is_scalar(node):
            return [(0.0, [float(node)])]
        if isinstance(node, list):
            return [(float(node[2 * k]), [float(node[2 * k + 1])]) for k in range(len(node) // 2)]
        return None
    if dim == 2:
        if is_scalar(node):
            return [(0.0, [float(node)] * 2)]
        if isinstance(node, list):
            if len(node) == 2:
                if not all(is_scalar(x) for x in node):
                    raise RuntimeError('Vector2 line with one keyframe is not supported: %r' % (node,))
                return [(0.0, [float(node[0]), float(node[1])])]
            keys = []
            for k in range(len(node) // 2):
                value = node[2 * k + 1]
                keys.append((float(node[2 * k]), [float(x) for x in value] if isinstance(value, list) else [float(value)] * 2))
            return keys
        return None
    if dim == 3:
        if is_scalar(node):
            return [(0.0, [float(node)] * 3)]
        if isinstance(node, list):
            if len(node) == 2:
                return [(0.0, [float(node[0]), float(node[1]), 0.0])]
            if len(node) == 3:
                return [(0.0, [float(x) for x in node])]
            keys = []
            for k in range(len(node) // 2):
                value = node[2 * k + 1]
                if not isinstance(value, list):
                    raise RuntimeError('Scalar Vector3 keyframe is not supported: %r' % (node,))
                keys.append((float(node[2 * k]), [float(x) for x in value]))
            return keys
        return None
    if dim == 4:
        if isinstance(node, list):
            if len(node) == 4 and all(is_scalar(x) for x in node):
                return [(0.0, [float(x) / 255.0 for x in node])]
            keys = []
            for k in range(len(node) // 2):
                value = node[2 * k + 1]
                if isinstance(value, list):
                    keys.append((float(node[2 * k]), [float(x) / 255.0 for x in value]))
            return keys
        return None
    raise ValueError(dim)


def evaluate(keys, t):
    """PropertyLineKeyframes::GetValue, including its BinaryFind segment rule."""
    if t > keys[-1][0]:
        return list(keys[-1][1])
    if t <= keys[0][0]:
        return list(keys[0][1])
    if len(keys) == 2:
        if t < keys[1][0]:
            ti = (t - keys[0][0]) / (keys[1][0] - keys[0][0])
            return [a + (b - a) * ti for a, b in zip(keys[0][1], keys[1][1])]
        return list(keys[1][1])
    if len(keys) == 1:
        return list(keys[0][1])
    lo, hi = 0, len(keys) - 1
    while lo + 1 != hi:
        mid = (lo + hi) >> 1
        if t <= keys[mid][0]:
            hi = mid
        else:
            lo = mid
    ti = (t - keys[lo][0]) / (keys[lo + 1][0] - keys[lo][0])
    return [a + (b - a) * ti for a, b in zip(keys[lo][1], keys[lo + 1][1])]


def trim_on_load(keys, start, end):
    """UpdatePropertyLineOnLoad(): clamp a layer line to [start, end]."""
    keys = list(keys)
    i = 0
    while i < len(keys) and keys[i][0] < start:
        i += 1
    if i != 0:
        v0 = evaluate(keys, start)
        keys = [(start, v0)] + keys[i:]
    i = 0
    while i < len(keys) and keys[i][0] <= end:
        i += 1
    if i != len(keys):
        v1 = evaluate(keys, end)
        keys = keys[:i] + [(end, v1)]
    return keys


def line(node, dim, start=None, end=None):
    """Serialise one property line; empty arrays mean DAVA keeps a null line."""
    result = {'dim': dim, 't': [], 'v': [], 'external': '', 'mt': [], 'mv': []}
    if isinstance(node, dict) and 'externalVariable' in node:
        values = line_keys(node.get('value_line'), dim)
        modification = line_keys(node.get('modification_line'), dim)
        if not values:
            raise RuntimeError('Modifiable line without value_line')
        result['external'] = str(node['externalVariable'])
        result['t'] = [k[0] for k in values]
        result['v'] = [x for k in values for x in k[1]]
        if modification:
            result['mt'] = [k[0] for k in modification]
            result['mv'] = [x for k in modification for x in k[1]]
        return result
    keys = line_keys(node, dim)
    if keys is None:
        return result
    if not keys:
        raise RuntimeError('Empty keyframe line %r' % (node,))
    if start is not None:
        keys = trim_on_load(keys, start, end)
    for t, value in keys:
        if len(value) != dim:
            raise RuntimeError('Property value of %d components, expected %d: %r' % (len(value), dim, node))
    result['t'] = [k[0] for k in keys]
    result['v'] = [x for k in keys for x in k[1]]
    return result


# --------------------------------------------------------------------------
# Textures and sprites
# --------------------------------------------------------------------------

def texture_settings(descriptor):
    data = descriptor.read_bytes()
    if data[:4] != b'\x00\xee\xee\x00' or data[4] != 13:
        raise RuntimeError('Unsupported texture descriptor ' + str(descriptor))
    wrap_s, wrap_t, min_filter, mag_filter, mip_filter = data[5:10]
    return {'wrap': int(wrap_s), 'wrapT': int(wrap_t), 'minFilter': int(min_filter),
            'magFilter': int(mag_filter), 'mipFilter': int(mip_filter)}


def scene_texture(name, folder):
    descriptor = folder / name
    record(descriptor)
    settings = texture_settings(descriptor)
    source = descriptor.with_suffix('.mali.pvr')
    if not source.is_file():
        raise RuntimeError('Missing texture ' + str(source))
    record(source)
    dest = OUT / 'Textures' / (descriptor.stem + '.png')
    dest.parent.mkdir(parents=True, exist_ok=True)
    with contextlib.redirect_stdout(io.StringIO()):
        if not p.decode_texture(source, dest):
            raise RuntimeError('Cannot decode ' + str(source))
    return {'resource': PREFIX + 'Textures/' + descriptor.stem, **settings}


sprite_cache = {}


def sprite(ref):
    if ref in sprite_cache:
        return sprite_cache[ref]
    suffix = ref.split('/Gfx/', 1)[1]
    descriptor = p.PACK / 'Gfx' / (suffix + '.txt')
    record(descriptor)
    lines = [x.strip() for x in descriptor.read_text().splitlines() if x.strip()]
    sheets = int(lines[0])
    if sheets != 1:
        raise RuntimeError('Multi-sheet sprite ' + ref)
    atlas_descriptor = descriptor.parent / lines[1]
    settings = texture_settings(atlas_descriptor)
    atlas = atlas_descriptor.with_suffix('.mali.webp')
    record(atlas_descriptor)
    record(atlas)
    image = Image.open(atlas).convert('RGBA')
    frame_count = int(lines[2 + sheets])
    frames = []
    for index, row in enumerate(lines[3 + sheets:3 + sheets + frame_count]):
        x, y, w, h, _ox, _oy, page = map(int, row.split()[:7])
        if page != 0:
            raise RuntimeError('Multi-page sprite ' + ref)
        name = suffix.replace('Particles/', '').replace('/', '_') + ('' if frame_count == 1 else '_%d' % index)
        dest = OUT / 'Sprites' / (name + '.png')
        dest.parent.mkdir(parents=True, exist_ok=True)
        image.crop((x, y, x + w, y + h)).save(dest)
        frames.append(PREFIX + 'Sprites/' + name)
    entry = {'name': suffix.replace('Particles/', ''), 'frames': frames, **settings}
    sprite_cache[ref] = len(sprite_list)
    sprite_list.append(entry)
    return sprite_cache[ref]


sprite_list = []


# --------------------------------------------------------------------------
# Emitters (Particles/ParticleEmitter.cpp, Particles/ParticleLayer.cpp)
# --------------------------------------------------------------------------

LAYER_KEYS = {
    'name', 'enableFog', 'sprite', 'type', 'isLong', 'layerType', 'blending', 'degradeStrategy', 'pivotPoint',
    'loopVariation', 'angle', 'enableFrameBlend', 'scaleVelocityBase', 'scaleVelocityFactor', 'life', 'number',
    'size', 'randomSpinDirection', 'colorRandom', 'alphaOverLife', 'frameOverLifeEnabled', 'frameOverLifeFPS',
    'randomFrameOnStart', 'loopSpriteAnimation', 'deltaTime', 'startTime', 'endTime', 'isLooped', 'deltaVariation',
    'loopEndTime', 'inheritPosition', 'particleOrientation', 'activeLODS', 'effectFormat', 'colorOverLife',
    'sizeOverLifeXY', 'lifeVariation', 'forceCount', 'spinVariation', 'sizeVariation', 'velocityVariation',
    'velocity', 'velocityOverLife', 'spin', 'angleVariation', 'force0', 'forceOverLife0'}
EMITTER_KEYS = {'size', 'name', 'type', 'emissionVectorInverted', 'shortEffect', 'emissionAngle', 'emissionRange',
                'emissionVector', 'radius'}

emitter_cache = {}
emitter_list = []


def emitter(path):
    path = path.resolve()
    key = str(path)
    if key in emitter_cache:
        return emitter_cache[key]
    record(path)
    root = yaml.safe_load(path.read_text())
    node = root.get('emitter') or {}
    unknown = set(node) - EMITTER_KEYS
    if unknown:
        raise RuntimeError('Unported emitter keys %s in %s' % (sorted(unknown), path.name))
    if not truth(node.get('emissionVectorInverted', False)):
        raise RuntimeError('Old non-inverted emission vector in ' + path.name)
    if truth(node.get('shortEffect', False)):
        raise RuntimeError('shortEffect emitters are not ported: ' + path.name)
    kind = str(node.get('type', 'point'))
    result = {
        'name': str(node.get('name', 'Particle Emitter')),
        'source': str(path.relative_to(p.PACK)),
        'type': EMITTER_TYPES.get(kind, 0),
        'shockwave': 1 if kind == 'shockwave' else 0,
        # ParticleEmitter::Cleanup() defaults: vector (1,0,0), range 0.
        'emissionVector': line(node['emissionVector'], 3) if 'emissionVector' in node else line([1.0, 0.0, 0.0], 3),
        'emissionRange': line(node['emissionRange'], 1) if 'emissionRange' in node else line(0.0, 1),
        'emissionAngle': line(node.get('emissionAngle'), 1),
        'radius': line(node.get('radius'), 1),
        'size': line(node['size'], 3) if 'size' in node else line([0.0, 0.0, 0.0], 3),
        'colorOverLife': line(node.get('colorOverLife'), 4),
        'layers': [],
    }
    for name, layer in root.items():
        if not isinstance(layer, dict) or layer.get('type') != 'layer':
            continue
        unknown = set(layer) - LAYER_KEYS
        if unknown:
            raise RuntimeError('Unported layer keys %s in %s/%s' % (sorted(unknown), path.name, name))
        if int(layer.get('effectFormat', 0)) != 1:
            raise RuntimeError('Unexpected effectFormat in %s/%s' % (path.name, name))
        if truth(layer.get('frameOverLifeEnabled', False)) or truth(layer.get('randomFrameOnStart', False)):
            pass  # Validated below: every chest sprite has exactly one frame.
        start = float(layer.get('startTime', 0.0))
        end = float(layer.get('endTime', 100000000.0))
        pivot = layer.get('pivotPoint', [0.0, 0.0])
        sprite_index = sprite(layer['sprite'])
        if len(sprite_list[sprite_index]['frames']) != 1:
            raise RuntimeError('Animated particle sprite is not ported: ' + layer['sprite'])
        forces = []
        for index in range(int(layer.get('forceCount', 0))):
            forces.append({'force': line(layer.get('force%d' % index), 3),
                           'overLife': line(layer.get('forceOverLife%d' % index), 1)})
        lods = [int(x) for x in layer.get('activeLODS', [1, 1, 1, 1])]
        result['layers'].append({
            'name': str(layer.get('name', name)),
            'sprite': sprite_index,
            'single': 1 if layer.get('layerType') == 'single' else 0,
            'blending': int(layer.get('blending', 1)),
            'orientation': int(layer.get('particleOrientation', 1)),
            'isLong': 1 if truth(layer.get('isLong', False)) else 0,
            'scaleVelocityBase': float(layer.get('scaleVelocityBase', 1.0)),
            'scaleVelocityFactor': float(layer.get('scaleVelocityFactor', 0.0)),
            'pivot': [float(pivot[0]), float(pivot[1])],
            'startTime': start,
            'endTime': end,
            'isLooped': 1 if truth(layer.get('isLooped', False)) else 0,
            'deltaTime': float(layer.get('deltaTime', 0.0)),
            'deltaVariation': float(layer.get('deltaVariation', 0.0)),
            'loopVariation': float(layer.get('loopVariation', 0.0)),
            'loopEndTime': float(layer.get('loopEndTime', 0.0)),
            'inheritPosition': 1 if truth(layer.get('inheritPosition', False)) else 0,
            'randomSpinDirection': 1 if truth(layer.get('randomSpinDirection', False)) else 0,
            'activeLods': (lods + [1, 1, 1, 1])[:4],
            'degradeStrategy': int(layer.get('degradeStrategy', 0)),
            'life': line(layer.get('life'), 1, start, end),
            'lifeVariation': line(layer.get('lifeVariation'), 1, start, end),
            'number': line(layer.get('number'), 1, start, end),
            'numberVariation': line(layer.get('numberVariation'), 1, start, end),
            'size': line(layer.get('size'), 2, start, end),
            'sizeVariation': line(layer.get('sizeVariation'), 2, start, end),
            'sizeOverLife': line(layer.get('sizeOverLifeXY'), 2),
            'velocity': line(layer.get('velocity'), 1, start, end),
            'velocityVariation': line(layer.get('velocityVariation'), 1, start, end),
            'velocityOverLife': line(layer.get('velocityOverLife'), 1),
            'spin': line(layer.get('spin'), 1, start, end),
            'spinVariation': line(layer.get('spinVariation'), 1, start, end),
            'angle': line(layer.get('angle'), 1, start, end),
            'angleVariation': line(layer.get('angleVariation'), 1, start, end),
            'colorRandom': line(layer.get('colorRandom'), 4),
            'colorOverLife': line(layer.get('colorOverLife'), 4),
            'alphaOverLife': line(layer.get('alphaOverLife'), 1),
            'forces': forces,
        })
    emitter_cache[key] = len(emitter_list)
    emitter_list.append(result)
    return emitter_cache[key]


# --------------------------------------------------------------------------
# Scene
# --------------------------------------------------------------------------

def quat_matrix(q):
    """DAVA Quaternion::GetMatrix() for a row-vector engine."""
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w)],
                     [2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w)],
                     [2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y)]])


def slerp(a, b, t):
    """DAVA Quaternion::Slerp (shortest arc, linear fallback near identity)."""
    a = np.asarray(a, dtype=float)
    b = np.asarray(b, dtype=float)
    cosine = float(np.dot(a, b))
    if cosine < 0:
        cosine = -cosine
        b = -b
    if 1.0 - cosine > 0.05:
        angle = math.acos(cosine)
        sine = math.sin(angle)
        ka = math.sin((1 - t) * angle) / sine
        kb = math.sin(t * angle) / sine
    else:
        ka = 1 - t
        kb = t
    return ka * a + kb * b


def animation_world(part, t):
    times = part['times']
    count = len(times)
    if t >= times[-1]:
        i, j, ti = count - 1, count - 1, 0.0
    else:
        i = 0
        while i + 1 < count and times[i + 1] <= t:
            i += 1
        j = i + 1
        ti = (t - times[i]) / (times[j] - times[i])
    tr = np.array(part['translations'][3 * i:3 * i + 3]) * (1 - ti) + np.array(part['translations'][3 * j:3 * j + 3]) * ti
    sc = np.array(part['scales'][3 * i:3 * i + 3]) * (1 - ti) + np.array(part['scales'][3 * j:3 * j + 3]) * ti
    q = slerp(part['rotations'][4 * i:4 * i + 4], part['rotations'][4 * j:4 * j + 4], ti)
    key = np.eye(4)
    key[:3, :3] = quat_matrix(q) @ np.diag(sc)
    key[3, :3] = tr
    animated = np.array(part['invPose']).reshape(4, 4) @ key
    return animated @ np.array(part['local']).reshape(4, 4) @ np.array(part['parentWorld']).reshape(4, 4)


def walk(entity, parent_world, out):
    local = np.eye(4)
    world = parent_world
    for component in p.components(entity, 'TransformComponent'):
        local = row_matrix(component['tc.localMatrix'])
        world = row_matrix(component['tc.worldMatrix'])
    out.append({'name': str(entity.get('name', '')), 'entity': entity, 'local': local, 'world': world,
                'parentWorld': parent_world})
    for child in p.items(entity.get('#hierarchy')):
        if isinstance(child, dict):
            walk(child, world, out)


def convert(vectors):
    v = np.asarray(vectors, dtype=float)
    return v[:, [0, 2, 1]]


def main():
    if OUT.exists():
        for sub in ['Textures', 'Sprites']:
            folder = OUT / sub
            if folder.exists():
                for path in folder.glob('*.png'):
                    path.unlink()
    OUT.mkdir(parents=True, exist_ok=True)
    folder = p.PACK / '3d/Maps/chest'
    scene_path = p.PACK / SCENE
    record(scene_path)
    scene = p.dava.read_sc2(scene_path.read_bytes())
    groups = {p.dava.group_id(x): x for x in scene['geometry']}
    materials = {int.from_bytes(x['#id'], 'little'): x for x in scene['dataNodes'] if x.get('##name') == 'NMaterial'}
    animations = {int.from_bytes(x['#id'], 'little'): x for x in scene['dataNodes'] if x.get('##name') == 'AnimationData'}
    entities = []
    for root in p.items(scene['scene'].get('#hierarchy')):
        if isinstance(root, dict):
            walk(root, np.eye(4), entities)
    by_name = {}
    for item in entities:
        by_name.setdefault(item['name'], []).append(item)

    # Camera, light and the reward hardpoint.
    camera = next(c['cc.camera'] for e in entities for c in p.components(e['entity'], 'CameraComponent'))
    camera_def = {'position': floats(camera['cam.position'], 3), 'target': floats(camera['cam.target'], 3),
                  'up': floats(camera['cam.up'], 3), 'fovX': float(camera['cam.fov']),
                  'znear': float(camera['cam.znear']), 'zfar': float(camera['cam.zfar'])}
    if camera.get('cam.isOrtho'):
        raise RuntimeError('Orthographic chest camera is not ported')
    light_entity = next(e for e in entities if list(p.components(e['entity'], 'LightComponent')))
    light = next(p.components(light_entity['entity'], 'LightComponent'))['lc.light']
    light_def = {'position': flat(light_entity['world'][3, :3]),
                 'color': [float(light['color.' + x]) for x in 'rgb'],
                 'ambient': [float(light['ambColor.' + x]) for x in 'rgb'],
                 'type': int(light['type'])}
    hardpoint = by_name['HP_gui'][0]['world'][3, :3]

    meshes = []

    def add_mesh(label, positions, normals, tangents, binormals, uv, indices):
        positions = np.asarray(positions, dtype='<f4')
        count = len(positions)
        def channel(values, width):
            if values is None:
                return np.zeros((count, width), dtype='<f4')
            return np.asarray(values, dtype='<f4').reshape(count, width)
        indices = np.asarray(indices, dtype='<i4')
        if len(indices) % 3 or indices.min() < 0 or indices.max() >= count:
            raise RuntimeError('Invalid indices in ' + label)
        payload = struct.pack('<ii', count, len(indices))
        for values, width in [(positions, 3), (normals, 3), (tangents, 3), (binormals, 3), (uv, 2)]:
            payload += channel(values, width).tobytes()
        payload += indices.tobytes()
        meshes.append({'name': label, 'payload': payload, 'vertices': count, 'triangles': len(indices) // 3,
                       'min': flat(positions.min(axis=0)), 'max': flat(positions.max(axis=0))})
        return len(meshes) - 1

    def geometry(item):
        batches = list(p.batches_of(item['entity']))
        if len(batches) != 1:
            raise RuntimeError('Expected one batch in ' + item['name'])
        gid, mid, _kind = batches[0]
        stream = p.dava.decode_vertices(groups[gid])
        indices = np.array(p.dava.decode_indices(groups[gid]), dtype=int).reshape(-1, 3)
        return stream, indices, mid

    # Background plane: unlit, baked into Unity world space.
    plane = by_name['Plain .sc2'][0]
    stream, indices, mid = geometry(plane)
    plane_material = materials[mid]
    while plane_material.get('fxName') is None and plane_material.get('parentMaterialKey') in materials:
        plane_material = materials[plane_material['parentMaterialKey']]
    if 'Textured.Opaque' not in plane_material.get('fxName', ''):
        raise RuntimeError('Unexpected background material ' + str(plane_material.get('fxName')))
    background_texture = scene_texture(plane_material['textures']['albedo'], folder)
    positions = np.c_[np.array(stream['position'], dtype=float), np.ones(len(stream['position']))] @ plane['world']
    uv = np.array(stream['uv0'], dtype=float)
    uv[:, 1] = 1 - uv[:, 1]
    world_conversion = np.linalg.det(plane['world'][:3, :3]) * -1.0  # determinant of C * W
    if world_conversion < 0:
        indices = indices[:, [0, 2, 1]]
    background_mesh = add_mesh('background', convert(positions[:, :3]), None, None, None, uv, indices.reshape(-1))

    # Box parts: local space, animated at runtime.
    box_material = None
    parts = []
    for name in ['box01', 'box02', 'box03', 'box04', 'box05', 'box06']:
        item = by_name[name][0]
        stream, indices, mid = geometry(item)
        material = materials[mid]
        while not any(k.startswith('configArchive') for k in material) and material.get('parentMaterialKey') in materials:
            material = materials[material['parentMaterialKey']]
        config = material['configArchive_0']
        if 'NormalizedBlinnPhongAllQualities.Opaque' not in config.get('fxName', ''):
            raise RuntimeError('Unexpected box material ' + str(config.get('fxName')))
        if box_material is None:
            props = {k: p.material_property(config, k) for k in config.get('properties', {})}
            box_material = {
                'albedo': scene_texture(config['textures']['albedo'], folder),
                'normal': scene_texture(config['textures']['normalmap'], folder),
                'glossiness': float(props['inGlossiness'][0]),
                'specularity': float(props['inSpecularity'][0]),
                'fresnel': [float(x) for x in props['metalFresnelReflectance'][:3]],
                'normalScale': float(props['normalScale'][0]),
                'flags': {k: int(v) for k, v in (config.get('flags') or {}).items()},
            }
        uv = np.array(stream['uv0'], dtype=float)
        uv[:, 1] = 1 - uv[:, 1]
        mesh = add_mesh(name, convert(stream['position']), convert(stream['normal']), convert(stream['tangent']),
                        convert(stream['binormal']), uv, indices[:, [0, 2, 1]].reshape(-1))
        component = next(p.components(item['entity'], 'AnimationComponent'))
        reference = component['animation']
        animation = animations[reference if isinstance(reference, int) else int.from_bytes(reference, 'little')]
        count = int(animation['keyCount'])
        part = {
            'name': name, 'mesh': mesh,
            'local': flat(item['local']), 'parentWorld': flat(item['parentWorld']),
            'invPose': floats(animation['invPose'], 16), 'duration': float(animation['duration']),
            'times': [float(animation['key_%d_time' % i]) for i in range(count)],
            'translations': [x for i in range(count) for x in floats(animation['key_%d_translation' % i], 3)],
            'rotations': [x for i in range(count) for x in floats(animation['key_%d_rotation' % i], 4)],
            'scales': [x for i in range(count) for x in floats(animation['key_%d_scale' % i], 3)],
        }
        if part['times'] != sorted(part['times']):
            raise RuntimeError('Unsorted animation keys in ' + name)
        for t in part['times']:
            if np.linalg.det(animation_world(part, t)[:3, :3]) <= 0:
                raise RuntimeError('Mirrored animation key in ' + name)
        parts.append(part)

    # Reference poses evaluated here (float64) to validate the runtime.
    samples = []
    for index, part in enumerate(parts):
        for t in [0.0, 0.37, 0.58, 1.13, 1.6, 2.2, 2.5]:
            samples.append({'part': index, 'time': t, 'world': flat(animation_world(part, t))})

    # Timeline: what starts each effect and animation.
    starts = {}
    animation_delays = {}
    for item in entities:
        for component in p.components(item['entity'], 'ActionComponent'):
            for action in p.items(component.get('actions') or component):
                if not isinstance(action, dict) or 'act.type' not in action:
                    continue
                if int(action.get('act.event', 0)) != ACTION_EVENT_ADDED_TO_SCENE:
                    raise RuntimeError('Unexpected action event on ' + item['name'])
                if float(action.get('act.delayVariation', 0.0)) != 0.0:
                    raise RuntimeError('Random action delays are not ported')
                target_name = str(action.get('act.entityName', ''))
                if target_name == '*** Self ***' or target_name == item['name']:
                    target = item
                else:
                    target = None
                    stack = [item['entity']]
                    while stack and target is None:
                        node = stack.pop(0)
                        for child in p.items(node.get('#hierarchy')):
                            if isinstance(child, dict):
                                if str(child.get('name', '')) == target_name:
                                    target = next(e for e in entities if e['entity'] is child)
                                    break
                                stack.append(child)
                    if target is None:
                        raise RuntimeError('Action target %s not found under %s' % (target_name, item['name']))
                kind = int(action['act.type'])
                delay = float(action.get('act.delay', 0.0))
                if kind == ACTION_PARTICLE_START:
                    starts[id(target['entity'])] = {'delay': delay,
                                                    'repeats': int(action.get('act.stopAfterNRepeats', -1)),
                                                    'stopWhenEmpty': truth(action.get('act.stopWhenEmpty', False))}
                elif kind == ACTION_ANIMATION_START:
                    animation_delays[target['name']] = delay
                else:
                    raise RuntimeError('Unported action type %d on %s' % (kind, item['name']))
    for part in parts:
        if animation_delays.get(part['name'], None) != 0.0:
            raise RuntimeError('Box part %s is not started with the scene' % part['name'])

    camera_position = np.array(camera_def['position'])
    tan_half = math.tan(math.radians(camera_def['fovX']) * 0.5)
    effects = []
    for item in entities:
        for component in p.components(item['entity'], 'ParticleEffectComponent'):
            if int(component.get('ro.flags', 1)) & 1 == 0:
                continue  # Hidden helper copies (animation_inclusion).
            if float(component.get('pe.startFromTime', 0.0)) > 1e-6:
                raise RuntimeError('startFromTime is not ported')
            translation = item['world'][3, :3]
            # LodSystem: distance scaled by the horizontal field of view.
            lod = 1
            lods = list(p.components(item['entity'], 'LodComponent'))
            if lods:
                distances = [float(lods[0]['lc.loddist']['distance%d' % i]) for i in range(4)]
                visible = float(np.linalg.norm(camera_position - translation)) * tan_half
                lod = -1
                for i in range(4):
                    far = distances[i] * 1.05
                    if visible < far:
                        lod = i
                        break
                if lod < 0:
                    raise RuntimeError('Effect %s is beyond its last LOD' % item['name'])
            start = starts.get(id(item['entity']))
            instances = []
            for instance in p.items(component.get('pe.emitters')):
                config = (scene_path.parent / instance['emitter.filename']).resolve()
                instances.append({'emitter': emitter(config), 'position': floats(instance.get('emitter.position', [0, 0, 0]), 3)})
            repeats = int(component.get('pe.repeatsCount', 0))
            effects.append({
                'name': item['name'],
                'world': flat(item['world']),
                'delay': start['delay'] if start else 0.0,
                'byAction': 1 if start else 0,
                # StopAfterNRepeats(-1) stores 0xFFFFFFFF; 0 means infinite too.
                'repeats': float((start['repeats'] & 0xFFFFFFFF) if start else repeats),
                'stopWhenEmpty': 1 if (start['stopWhenEmpty'] if start else truth(component.get('pe.stopWhenEmpty', False))) else 0,
                'duration': float(component.get('pe.effectDuration', 100.0)),
                'clearOnRestart': 1 if truth(component.get('pe.clearOnRestart', True)) else 0,
                'lod': lod,
                'instances': instances,
            })

    definition = {
        'id': 'chest', 'scene': SCENE, 'meshResource': PREFIX + 'meshes',
        'camera': camera_def, 'light': light_def, 'hardpoint': flat(hardpoint),
        'background': {'mesh': background_mesh, 'texture': background_texture},
        'box': box_material, 'parts': parts, 'samples': samples,
        'boxDuration': max(part['duration'] for part in parts),
        'gradeVariable': 'grade_box',
        'sprites': sprite_list, 'emitters': emitter_list, 'effects': effects,
    }
    (OUT / 'scene.json').write_text(json.dumps(definition, separators=(',', ':')) + '\n')
    (OUT / 'meshes.bytes').write_bytes(b'BLTZCHS1' + struct.pack('<i', len(meshes)) + b''.join(m['payload'] for m in meshes))
    AUDIO_OUT.mkdir(parents=True, exist_ok=True)
    for name in AUDIO:
        source = AUDIO_SOURCE / name
        record(source)
        shutil.copyfile(source, AUDIO_OUT / name)
    manifest = {
        'id': 'chest', 'sourceScene': SCENE, 'sources': dict(sorted(sources.items())),
        'meshes': [{k: v for k, v in m.items() if k != 'payload'} for m in meshes],
        'effects': len(effects), 'emitters': len(emitter_list),
        'layers': sum(len(e['layers']) for e in emitter_list), 'sprites': len(sprite_list),
        'audio': AUDIO,
        'omissions': [
            {'entity': 'HP_gui', 'reason': 'reward spawn locator; exported as the hardpoint, not drawn'},
            {'entity': 'animation_inclusion', 'reason': 'hidden effect copies (render object flags 0)'},
        ],
    }
    (OUT / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print('chest', {k: v for k, v in manifest.items() if k in ('effects', 'emitters', 'layers', 'sprites')},
          'meshes', len(meshes), flush=True)


if __name__ == '__main__':
    main()
