#!/usr/bin/env python3
"""Export "Miku hangar in the void" (`hangar_miku_void`) for the client hangar.

The source is the mod archive `miku-hangar-in-the-void_f0_pc_lesta.zip`
(Tanks Blitz / Lesta f0, PC): the DAVA scene `hangar_main_2024/hv_main_24.sc2`
with its geometry embedded as PolygonGroup data nodes, DX11 DDS textures
(DXT1, DXT5, RGBA8) and the prefiltered IBL cube maps. The archive stays
read-only and is never extracted; every member read is fingerprinted. The
material templates the scene references (`~res:/Materials/*.material`) are not in
the archive and come from the Blitz 11.21 client dump, as for the Halloween hangar.

The decoding is pw-mods-tool's `Tools/prepare_miku_hangar.py` (the garage-map
port of the same hangar) on top of `port_halloween_hangar_2026.HangarExport`,
which walks the scene, resolves the materials, converts the meshes (DAVA Z-up
right-handed -> Unity Y-up left-handed) and reads the lights and the
environment. This module adds what differs:

* the archive source and the DDS decoder;
* the spawn point with `pointNumber` 1 (`UserNode_main`) and the client hangar
  camera `camera_default` (both nested in `usernode.sc2`);
* the material instances' `customCullMode` (rhi::CullMode: 0 none, 1 cull
  counter-clockwise = DAVA front faces, 2 cull clockwise = back faces);
* the 15 scenario graphs (clock digits, rotating disc and rays, day/night
  colour of the Earth), compiled by `miku_hangar_scenario` into programs
  `PortedHangarScenario` runs;
* the Wwise music emitter of `sound.sc2` (`play_music_hng_main`) is not ported:
  the hangar's music player plays its own playlist.

Output (`Assets/Blitz/Resources/MikuHangar`): `scene.json` (the Halloween
hangar's layout plus `scenarios`; no clips, particles, states or sounds),
`meshes.bytes`, `Textures/*.png`, `Cubemaps/ibl_specular_unity.bytes` (the
vehicle's reflection; the hangar has no PBR material, so the client-oriented
IBL maps and the DFG table are not needed), `import.json`, `report.json` and
`provenance.json`. Textures are compressed no coarser than the source's colour
bit rate: DXT5 with alpha and RGBA8 as ASTC 4x4, DXT1 and alpha-less DXT5 (whose
colour is a DXT1 block) as ASTC 5x5, and no larger than 2048 texels a side (the
PC source's one 4096 albedo would take a fifth of the hangar's size on a phone).
"""
from __future__ import annotations

import argparse
import collections
import hashlib
import json
import posixpath
import struct
import zipfile
from pathlib import Path, PurePosixPath

import numpy as np

import miku_hangar_scenario as scenario
import port_halloween_hangar_2026 as hh

MAP = 'hangar_miku_void'
ARCHIVE = Path.home() / 'Загрузки' / 'miku-hangar-in-the-void_f0_pc_lesta.zip'
MAP_DIR = PurePosixPath('Data/3d/Maps/hangar_main_2024')
SCENE = MAP_DIR / 'hv_main_24.sc2'
# The archive carries the same scene under the names of the other hangars it can replace.
SCENE_COPIES = (PurePosixPath('Data/3d/Maps/forblitz_hangar_folder/forblitz_hangar_filename.sc2'),
                PurePosixPath('Data/3d/Maps/hangar_halloween_24/hv_halloween_24.sc2'))
OUTPUT = hh.ROOT / 'Assets/Blitz/Resources/MikuHangar'
SPAWN_POINT = 1
CAMERA = 'camera_default'
CULL_MODES = {0: 'NONE', 1: 'FACE_FRONT', 2: 'FACE_BACK'}
# Largest texture side the client imports: the Halloween hangar's (mobile) maximum.
MAX_TEXTURE_SIZE = 2048

DDS_MAGIC = b'DDS '
DDPF_FOURCC, DDPF_RGB = 0x4, 0x40
DDSCAPS2_CUBEMAP, DDSCAPS2_ALL_FACES = 0x200, 0xFC00
DDS_BLOCKS = {b'DXT1': ('DXT1', 8), b'DXT5': ('DXT5', 16)}


def astc_block(fmt: str, alpha: bool) -> str:
    """The square ASTC block no coarser than the source's colour bit rate: DXT1
    colour is 4 bpp (ASTC 5x5 is 5.12), DXT5 adds an 8 bpp alpha block."""
    if fmt == 'RGBA8' or (fmt == 'DXT5' and alpha):
        return '4x4'
    if fmt in ('DXT1', 'DXT5'):
        return '5x5'
    raise SystemExit(f'no ASTC block for the source format {fmt}')


# ------------------------------------------------------------------ DAVA readers
def load_tools(path: Path) -> None:
    hh.load_tools(path)
    original = hh.dava.read_archive

    def read_archive(reader, table=None):
        # KeyedArchive::Save writes an empty archive as the bare header 'KA' + version 0xFF02.
        if reader.data[reader.pos:reader.pos + 4] == b'KA\x02\xff':
            reader.pos += 4
            return {}
        return original(reader, table)

    hh.dava.read_archive = read_archive
    # The Halloween exporter's skipped subtrees belong to its hangar only.
    hh.SKIPPED_SUBTREES = frozenset()


# ------------------------------------------------------------------ source
class ArchiveSource:
    """The mod archive (zip members are PurePosixPath) and the Blitz client dump
    (filesystem Path) for the material templates; every file read is fingerprinted."""

    def __init__(self, archive: Path, client: Path):
        self.archive = archive.resolve()
        self.root = self.archive
        self.zip = zipfile.ZipFile(self.archive)
        self.names = set(self.zip.namelist())
        self.client = client.resolve()
        self.apk = self.client / 'from_apk'
        self.map_dir = MAP_DIR
        self.files: dict[str, str] = {}
        self.archive_sha256 = hashlib.sha256(self.archive.read_bytes()).hexdigest()

    def member(self, path: PurePosixPath) -> str | None:
        name = path.as_posix()
        for candidate in (name + '.dvpl', name):
            if candidate in self.names:
                return candidate
        return None

    def exists(self, path) -> bool:
        if isinstance(path, Path):
            return path.with_name(path.name + '.dvpl').is_file() or path.is_file()
        return self.member(path) is not None

    def read(self, path) -> bytes:
        if isinstance(path, Path):
            packed = path.with_name(path.name + '.dvpl')
            source = packed if packed.is_file() else path
            blob = source.read_bytes()
            key = 'client/' + source.relative_to(self.client).as_posix()
        else:
            member = self.member(path)
            if member is None:
                raise SystemExit(f'{path} is not in {self.archive.name}')
            blob = self.zip.read(member)
            key = 'archive/' + member
        self.files[key] = hashlib.sha256(blob).hexdigest()
        if blob[-4:] == b'DVPL':
            return hh.dvpl.unpack_bytes(blob)
        return blob

    def relative(self, path) -> str:
        if isinstance(path, Path):
            try:
                return 'client/' + path.relative_to(self.client).as_posix()
            except ValueError:
                return path.as_posix()
        return posixpath.relpath(path.as_posix(), 'Data/3d/Maps')

    def map_path(self, reference: str):
        if reference.startswith('~res:/'):
            relative = reference[len('~res:/'):]
            member = PurePosixPath('Data') / relative
            return member if self.exists(member) else self.apk / relative
        return PurePosixPath(posixpath.normpath(posixpath.join(self.map_dir.as_posix(), reference)))


# ------------------------------------------------------------------ DDS
class Dds:
    """A DX9-header DDS: 2D or cube map (faces +X -X +Y -Y +Z -Z, each with its
    mip chain, the client's and Unity's CubemapFace order), DXT1 / DXT5 blocks or
    32-bit masked RGBA."""

    def __init__(self, blob: bytes, name: str):
        if blob[:4] != DDS_MAGIC or len(blob) < 128:
            raise SystemExit(f'{name} is not a DDS file')
        _, _, height, width, _, _, mips = struct.unpack_from('<7I', blob, 4)
        _, pf_flags, fourcc, bits, *masks = struct.unpack_from('<2I4s5I', blob, 76)
        _, caps2 = struct.unpack_from('<2I', blob, 108)
        self.name, self.width, self.height, self.mips = name, width, height, max(1, mips)
        self.cube = bool(caps2 & DDSCAPS2_CUBEMAP)
        if self.cube and caps2 & DDSCAPS2_ALL_FACES != DDSCAPS2_ALL_FACES:
            raise SystemExit(f'{name}: partial cube map')
        self.faces = 6 if self.cube else 1
        if pf_flags & DDPF_FOURCC:
            if fourcc not in DDS_BLOCKS:
                raise SystemExit(f'{name}: unsupported DDS format {fourcc!r}')
            self.format, self.block = DDS_BLOCKS[fourcc]
            self.masks = None
        elif pf_flags & DDPF_RGB and bits == 32:
            self.format, self.block, self.masks = 'RGBA8', 0, masks
        else:
            raise SystemExit(f'{name}: unsupported DDS pixel format flags {pf_flags:#x} bits {bits}')
        self.levels = {}
        offset = 128
        for face in range(self.faces):
            for level in range(self.mips):
                w, h = max(1, width >> level), max(1, height >> level)
                size = self.level_size(w, h)
                if offset + size > len(blob):
                    raise SystemExit(f'{name}: truncated at face {face} level {level}')
                self.levels[(face, level)] = (w, h, blob[offset:offset + size])
                offset += size
        if offset != len(blob):
            raise SystemExit(f'{name}: {len(blob) - offset} bytes after the last level')

    def level_size(self, w: int, h: int) -> int:
        if self.block:
            return max(1, (w + 3) // 4) * max(1, (h + 3) // 4) * self.block
        return w * h * 4

    def pixels(self, face: int = 0, level: int = 0) -> np.ndarray:
        """RGBA8 rows in file order (the first row is the one the client uploads first)."""
        w, h, data = self.levels[(face, level)]
        if self.format == 'DXT1':
            raw = hh.texture2ddecoder.decode_bc1(data, w, h)
        elif self.format == 'DXT5':
            raw = hh.texture2ddecoder.decode_bc3(data, w, h)
        else:
            words = np.frombuffer(data, dtype='<u4').reshape(h, w)
            channels = []
            for mask in self.masks:
                if not mask:
                    channels.append(np.full((h, w), 255, np.uint8))
                    continue
                shift = (mask & -mask).bit_length() - 1
                top = mask >> shift
                values = (words & mask) >> shift
                channels.append((values * 255 // top).astype(np.uint8) if top != 255 else values.astype(np.uint8))
            return np.ascontiguousarray(np.dstack(channels))
        return np.frombuffer(raw, np.uint8).reshape(h, w, 4)[:, :, [2, 1, 0, 3]].copy()


# ------------------------------------------------------------------ export
class MikuExport(hh.HangarExport):
    def __init__(self, src: ArchiveSource):  # noqa: the base constructor reads a .scg the archive has none of
        self.src = src
        self.scene = hh.dava.read_sc2(src.read(SCENE))['scene']
        self.data_nodes = hh.items(self.scene.get('#dataNodes'))
        self.groups = {hh.dava.group_id(d): d for d in self.data_nodes if d.get('##name') == 'PolygonGroup'}
        self.nmaterials = {int.from_bytes(bytes(d['#id']), 'little'): d for d in self.data_nodes
                           if d.get('##name') == 'NMaterial' and d.get('#id') is not None}
        self.scene_sets = {name: hh.items(value) for name, value in (self.scene.get('#sceneComponentSets') or {}).items()}
        self.scene_components = [c for c in hh.items(self.scene.get('#sceneComponents')) if isinstance(c, dict)]
        singleton = next((c for c in self.scene_components if c.get('comp.typename') == 'ScenarioSingletonComponent'), None)
        self.scripts = {str(k): v for k, v in ((singleton or {}).get('ssc.scripts') or {}).items()}
        self.templates = hh.Templates(src)
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
        self.scenarios: list[dict] = []
        self.spawn_points: dict[int, str] = {}
        self.cameras: list[str] = []
        self.replaced_sounds: list[dict] = []
        self.cube_pixels: dict[str, list[list[np.ndarray]]] = {}

    # -------------------------------------------------------------- scene walk
    def visit(self, entity, parent, parent_world, parent_path, state_root, socket_world=None) -> None:
        name = str(entity.get('name', ''))
        path = f'{parent_path}/{name}'
        tc = hh.component(entity, 'TransformComponent') or {}
        if hh.component(entity, 'CameraComponent'):
            self.cameras.append(path)
            if name == CAMERA:
                self.read_camera(entity)
            return
        custom = (hh.component(entity, 'CustomPropertiesComponent') or {}).get('cpc.properties.archive') or {}
        if custom.get('type') == 'spawnpoint':
            number = int(custom.get('pointNumber', 0))
            if number in self.spawn_points:
                raise SystemExit(f'{path}: spawn point {number} is also {self.spawn_points[number]}')
            self.spawn_points[number] = path
            if number == SPAWN_POINT:
                self.spawn = {'position': hh.rounded(hh.unity_point(tc['tc.worldTranslation'])),
                              'rotation': hh.rounded(hh.unity_quaternion(tc['tc.worldRotation']), 7),
                              'positionDava': hh.rounded(tc['tc.worldTranslation']), 'path': path}
            return
        index = len(self.nodes)
        super().visit(entity, parent, parent_world, parent_path, state_root, socket_world)
        owner = hh.component(entity, 'ScenarioComponent')
        if owner:
            self.scenarios.append({'node': index, 'scriptId': str(owner.get('scenario.scriptId')), 'path': path,
                                   'name': name, '_entity': entity})

    def read_sounds(self, entity, index, world) -> None:
        sc = hh.component(entity, 'SoundComponent')
        if not sc:
            return
        for element in hh.items(sc):
            if isinstance(element, dict) and 'wwise' in element:
                wwise = element.get('wwise') or {}
                self.replaced_sounds.append({'path': self.nodes[index]['path'], 'event': str(wwise.get('eventName', '')),
                                             'group': str(wwise.get('groupName', ''))})

    # -------------------------------------------------------------- materials
    def material(self, mid: int) -> int:
        if mid in self.material_index:
            return self.material_index[mid]
        index = super().material(mid)
        current, seen = mid, set()
        while current is not None and current not in seen:
            seen.add(current)
            node = self.nmaterials.get(int(current))
            if node is None:
                break
            if 'customCullMode' in node:
                mode = int(node['customCullMode'])
                if mode not in CULL_MODES:
                    raise SystemExit(f'material {mid}: unknown customCullMode {mode}')
                record = self.materials[index]
                record['templateCullMode'] = record['cullMode']
                record['cullMode'] = CULL_MODES[mode]
                self.stats['custom cull modes'][f'{mode} ({record["templateCullMode"]} -> {CULL_MODES[mode]})'] += 1
                break
            current = node.get('parentMaterialKey')
        return index

    def texture_path(self, reference: str):
        base = self.src.map_path(reference)
        stem = base.name[:-4] if base.name.endswith('.tex') else base.name
        candidate = base.with_name(stem + '.dx11.dds')
        if self.src.exists(candidate):
            return candidate
        raise SystemExit(f'texture {reference} has no DX11 DDS in {self.src.archive.name}')

    def texture(self, reference: str, sampler: int) -> int:
        path = self.texture_path(reference)
        state = hh.decode_sampler(sampler)
        key = (path, state['wrapU'], state['wrapV'], state['minFilter'], state['magFilter'], state['mipFilter'], state['anisotropy'])
        if key in self.texture_index:
            return self.texture_index[key]
        relative = self.src.relative(path)
        stem = relative[:-len('.dx11.dds')]
        stem = stem.split('/', 1)[1] if stem.startswith(MAP_DIR.name + '/') else stem
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

    # -------------------------------------------------------------- scenarios
    def compile_scenarios(self) -> None:
        by_path = collections.defaultdict(list)
        for k, n in enumerate(self.nodes):
            by_path[n['path']].append(k)
        renderer_of = {r['node']: r for r in self.renderers}
        for record in self.scenarios:
            entity = record.pop('_entity')
            script = self.scripts.get(record['scriptId'])
            if script is None:
                raise SystemExit(f'{record["path"]}: scenario {record["scriptId"]} is not in the scene')

            def resolve(child: str, entity=entity, owner=record['path']) -> str:
                found = []

                def search(e, relative):
                    for c in hh.children(e):
                        name = str(c.get('name', ''))
                        path = f'{relative}/{name}' if relative else name
                        if name == child:
                            found.append(path)
                        search(c, path)

                search(entity, '')
                if len(found) != 1:
                    raise SystemExit(f'{owner}: FindChildByName({child}) matches {len(found)} entities')
                return found[0]

            program = scenario.compile_graph(record['scriptId'], script, record['path'], resolve)
            targets = []
            for relative in program.targets:
                nodes = by_path.get(f'{record["path"]}/{relative}', [])
                if len(nodes) != 1:
                    raise SystemExit(f'{record["path"]}: target {relative} is {len(nodes)} nodes')
                targets.append(nodes[0])
            if program.material_properties:
                renderer = renderer_of.get(record['node'])
                if renderer is None:
                    raise SystemExit(f'{record["path"]}: material parameters set on an entity without a render object')
                for m in renderer['materials']:
                    defines = {d['name'] for d in self.materials[m]['defines']}
                    if 'flatColor' in program.material_properties and 'FLATCOLOR' not in defines:
                        raise SystemExit(f'{record["path"]}: flatColor is set on {self.materials[m]["name"]} without FLATCOLOR')
            record.update({'targets': targets, 'targetPaths': program.targets,
                           'materialProperties': program.material_properties, 'rotations': program.rotations,
                           'clock': program.uses_clock, 'graphNodes': program.nodes, 'program': program.record()})
            self.stats['scenario kinds']['clock' if program.uses_clock and program.targets else
                                         'rotation' if program.rotations else
                                         'material' if program.material_properties else 'other'] += 1
        if len(self.scenarios) != len(self.scripts):
            self.warnings.append(f'{len(self.scripts)} scenario graphs, {len(self.scenarios)} attached to entities')

    # -------------------------------------------------------------- environment
    def environment(self) -> dict:
        """The Halloween environment record; this scene's render config sets no fog,
        so the client draws none (`fog.enabled` false, density 0). Without a PBR
        material the DFG table is not needed."""
        sets = self.scene_sets.get('Default') or []
        render = next((c for c in sets if c.get('comp.typename') == 'SceneRenderConfigComponent'), None)
        if render is None:
            raise SystemExit('the scene has no SceneRenderConfigComponent')
        authored = 'fogColor' in render
        if not authored:
            patched = dict(render, fogColor=[0.0, 0.0, 0.0, 1.0], fogDensity=0.0, fogEnd=0.0)
            self.scene_sets['Default'] = [patched if c is render else c for c in sets]
        try:
            environment = super().environment()
        finally:
            self.scene_sets['Default'] = sets
        environment['fog']['enabled'] = authored
        if not self.pbr():
            environment.pop('dfgLut')
        return environment

    def pbr(self) -> bool:
        return any(m['shader'] == 'PBR' for m in self.materials)

    # -------------------------------------------------------------- prune
    def prune(self) -> None:
        needed = set()
        for r in self.renderers:
            needed.add(r['node'])
        for light in self.lights:
            needed.add(light['node'])
        for s in self.scenarios:
            needed.add(s['node'])
            needed.update(s['targets'])
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
        for light in self.lights:
            light['node'] = remap[light['node']]
        for s in self.scenarios:
            s['node'] = remap[s['node']]
            s['targets'] = [remap[k] for k in s['targets']]
        self.remap = remap

    # -------------------------------------------------------------- textures
    def write_textures(self, output: Path) -> None:
        folder = output / 'Textures'
        folder.mkdir(parents=True, exist_ok=True)
        decoded = {}
        for record in self.textures:
            path = record.pop('_path')
            if path not in decoded:
                dds = Dds(self.src.read(path), self.src.relative(path))
                if dds.cube:
                    raise SystemExit(f'{record["source"]}: cube map bound as a 2D texture')
                decoded[path] = (dds, dds.pixels())
            dds, pixels = decoded[path]
            alpha = bool(pixels[:, :, 3].min() < 255)
            grey = bool(np.array_equal(pixels[:, :, 0], pixels[:, :, 1]) and np.array_equal(pixels[:, :, 1], pixels[:, :, 2]))
            # The Textured shader samples albedo without sRGB decoding (gamma maths, as in the client).
            record.update({'width': dds.width, 'height': dds.height, 'srgb': False, 'alpha': alpha, 'grey': grey,
                           'mips': dds.mips, 'format': dds.format, 'astc': astc_block(dds.format, alpha)})
            hh.write_png(output / record['file'], pixels, alpha)
            self.stats['texture formats'][f'{dds.format} alpha={alpha} -> ASTC {record["astc"]}'] += 1

    def write_cubemaps(self, output: Path) -> list[dict]:
        """Decodes the IBL cube maps for the vehicle's lighting; the client-oriented
        maps themselves are written only for PBR materials, which sample them."""
        folder = output / 'Cubemaps'
        folder.mkdir(parents=True, exist_ok=True)
        sets = {c.get('comp.typename'): c for c in self.scene_sets.get('Default') or []}
        ibl = sets['IBLComponent']
        records = []
        for name, key in (('ibl_diffuse', 'ibl.diffuse'), ('ibl_specular', 'ibl.specular')):
            path = self.texture_path(str(ibl[key]))
            dds = Dds(self.src.read(path), self.src.relative(path))
            if not dds.cube or dds.width != dds.height:
                raise SystemExit(f'{path} is not a square cube map')
            mips = [[dds.pixels(face, level) for face in range(6)] for level in range(dds.mips)]
            self.cube_pixels[name] = mips
            record = {'name': name, 'file': f'Cubemaps/{name}.bytes', 'size': dds.width, 'mips': dds.mips,
                      'srgb': False, 'source': self.src.relative(path), 'format': dds.format}
            if self.pbr():
                hh.write_cube(output / record['file'], mips)
            records.append(record)
        return records

    # -------------------------------------------------------------- run
    def run(self, output: Path, textures: bool) -> dict:
        self.walk()
        if self.camera is None:
            raise SystemExit(f'the scene has no camera {CAMERA} (cameras: {self.cameras})')
        if self.spawn is None:
            raise SystemExit(f'the scene has no spawn point {SPAWN_POINT} ({self.spawn_points})')
        if not self.groups:
            raise SystemExit('the scene embeds no PolygonGroup')
        for kind, found in (('clips', self.clips), ('states', self.states), ('effects', self.effects)):
            if found:
                raise SystemExit(f'the scene has {len(found)} {kind}, which this port does not export')
        self.compile_scenarios()
        self.assign_lights()
        tank_lights = self.tank_lights()
        environment = self.environment()
        self.prune()
        for r in self.renderers:
            self.stats['shadow casting'][r['shadowCasting']] += 1
        output.mkdir(parents=True, exist_ok=True)
        for sub in ('Textures', 'Cubemaps'):
            if (output / sub).exists() and textures:
                for old in (output / sub).iterdir():
                    if old.suffix != '.meta':
                        old.unlink()
        self.write_meshes(output / 'meshes.bytes')
        cubemaps = []
        if textures:
            self.write_textures(output)
            cubemaps = self.write_cubemaps(output)
            self.vehicle_lighting(output, environment, cubemaps)
            if not self.pbr():
                cubemaps = [c for c in cubemaps if c['name'] == environment['vehicle']['reflection']]
            write_import(output / 'import.json', self.textures)
        else:
            for record in self.textures:
                record.pop('_path', None)
        environment['tankLights'] = tank_lights
        return {
            'map': MAP, 'nodes': self.nodes,
            'meshes': [{'name': m['name'], 'vertexCount': m['vertexCount'], 'submeshCount': len(m['submeshes']),
                        'streams': m['streams'], 'boneCount': 0, 'groups': m['groups'], 'particle': False}
                       for m in self.meshes],
            'materials': self.materials, 'textures': self.textures, 'cubemaps': cubemaps,
            'renderers': [{k: v for k, v in r.items() if not k.startswith('_')} for r in self.renderers],
            'lights': self.lights, 'clips': [], 'animations': [], 'states': [], 'sounds': [], 'effects': [],
            'sprites': [], 'rebrand': [], 'scenarios': self.scenarios,
            'spawn': self.spawn, 'camera': self.camera, 'environment': environment,
            'replacedSounds': self.replaced_sounds,
        }

    def report(self) -> dict:
        return {
            'counts': {'nodes': len(self.nodes), 'renderers': len(self.renderers), 'meshes': len(self.meshes),
                       'vertices': int(sum(m['vertexCount'] for m in self.meshes)),
                       'triangles': int(sum(s.size for m in self.meshes for s in m['submeshes']) // 3),
                       'materials': len(self.materials), 'textures': len(self.textures), 'lights': len(self.lights),
                       'scenarios': len(self.scenarios), 'polygonGroups': len(self.groups)},
            'spawnPoints': {str(k): v for k, v in sorted(self.spawn_points.items())}, 'spawn': self.spawn['path'],
            'cameras': self.cameras, 'camera': CAMERA,
            'scenarios': [{k: s[k] for k in ('path', 'scriptId', 'targetPaths', 'rotations', 'materialProperties', 'clock')}
                          for s in self.scenarios],
            'replacedSounds': self.replaced_sounds,
            'maxWorldError': self.max_world_error, 'notes': dict(self.notes), 'warnings': self.warnings,
            'stats': {k: {str(a): b for a, b in sorted(v.items(), key=lambda x: str(x[0]))} for k, v in self.stats.items()},
        }


def write_import(path: Path, textures: list[dict]) -> None:
    """Importer settings per image (HalloweenHangarImport): the first sampler state that references it wins."""
    specs: dict[str, dict] = {}
    conflicts = []
    for t in textures:
        spec = {'srgb': bool(t['srgb']), 'mips': t.get('mipFilter', 0) != 0 and t.get('mips', 1) > 1,
                'filter': hh.import_filter(t.get('magFilter', 1), t.get('mipFilter', 0)),
                'wrapU': hh.WRAP_MODES[t.get('wrapU', 0)], 'wrapV': hh.WRAP_MODES[t.get('wrapV', 0)],
                'aniso': max(1, t.get('anisotropy', 1)), 'maxSize': min(MAX_TEXTURE_SIZE, hh.import_max_size(t['width'], t['height'])),
                'astc': t['astc']}
        old = specs.get(t['file'])
        if old is None:
            specs[t['file']] = spec
        elif any(old[k] != spec[k] for k in ('srgb', 'wrapU', 'wrapV', 'mips')):
            conflicts.append(t['file'])
    path.write_text(json.dumps({'images': dict(sorted(specs.items())), 'conflicts': sorted(set(conflicts))},
                               indent=1), encoding='utf-8')


def scene_copies(src: ArchiveSource) -> dict:
    """SHA-256 of the scene and of its copies under the other hangar names."""
    result = {}
    for path in (SCENE,) + SCENE_COPIES:
        member = src.member(path)
        result[path.as_posix()] = hashlib.sha256(src.zip.read(member)).hexdigest() if member else None
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('--archive', type=Path, default=ARCHIVE, help='the mod archive (read-only)')
    parser.add_argument('--client', type=Path, default=hh.CLIENT, help='Blitz 11.21 client dump with from_apk/Materials')
    parser.add_argument('--tools', type=Path, default=hh.DAVA_TOOLS, help='blitz-legacy-godot/tools with the DAVA readers')
    parser.add_argument('--output', type=Path, default=OUTPUT)
    parser.add_argument('--no-textures', action='store_true', help='skip texture decoding (quick structural run)')
    args = parser.parse_args()
    load_tools(args.tools)
    src = ArchiveSource(args.archive, args.client)
    export = MikuExport(src)
    scene = export.run(args.output, not args.no_textures)
    (args.output / 'scene.json').write_text(json.dumps(scene, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
    report = export.report()
    copies = scene_copies(src)
    report['sceneCopiesIdentical'] = len(set(copies.values())) == 1
    (args.output / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding='utf-8')
    (args.output / 'provenance.json').write_text(json.dumps(
        {'archive': str(src.archive), 'archiveSha256': src.archive_sha256, 'client': str(src.client), 'map': MAP,
         'scene': SCENE.as_posix(), 'sceneCopies': copies, 'files': dict(sorted(src.files.items()))}, indent=1),
        encoding='utf-8')
    print(json.dumps(report['counts']), 'worldErr', f'{report["maxWorldError"]:.2g}')
    print('notes', json.dumps(report['notes']))
    print('warnings', len(report['warnings']))


if __name__ == '__main__':
    main()
