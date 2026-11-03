"""Preserve the one German multi-albedo hull detail without atlasing or decimation.

Barkhan's palm is a second hull batch with its own alpha-test material. Split
that batch into a stationary hull detail, retaining the original vertices,
indices, UVs and normals; do not paint its UVs with the main hull sheet.
All single-sheet vehicles use the existing exporter unchanged.
"""
import copy
import hashlib
import re
import json
import struct
from pathlib import Path


def install(port):
    exporter, converter = port.exporter, port.blitz2glb
    state = {'details': {}, 'materials': {}}
    # Its palm detail sheet is exported here, with its own render state.
    port.SHEET_SKIP.add('G_VK3502H_BP')
    original_albedos = exporter.client_albedos
    original_mesh = converter.MeshMaker.mesh_from
    original_node = converter.Gltf.add_node
    raw_write = exporter.write_objects
    original_card = exporter.port_card

    import blitz_source_primitive_order
    blitz_source_primitive_order.install(exporter)

    def original_write(items, destination, material_for, materials, groups=False, record_objects=None):
        result = raw_write(items, destination, material_for, materials, groups, record_objects)
        value = destination.read_text()
        value = value.replace('# Generated from the original Blitz 4.8.0 client geometry.',
                              '# Generated from the original Blitz 11.20 client LOD0 geometry.', 1)
        destination.write_text(value)
        return result

    def albedos(stem):
        state['details'], state['materials'] = {}, {}
        if stem != 'G_VK3502H_BP':
            return original_albedos(stem)
        scene = exporter.TANKS / exporter.MODEL_DIR / (stem + '.sc2')
        materials = port.materials(scene)
        body = exporter.client_textures(stem)[0].name.removesuffix('.mali.pvr')
        result = {}
        for node in converter.read_scene(str(scene), 0):
            sheets = {}
            for batch in node['batches']:
                texture = (port.inherited(materials, batch[1], 'textures') or {}).get('albedo')
                if texture:
                    sheets.setdefault(Path(texture).stem, []).append(batch[1])
            if not sheets:
                continue
            if len(sheets) == 1:
                result[node['name']] = next(iter(sheets))
                continue
            if node['name'] != 'hull' or body not in sheets:
                raise ValueError(f'{stem}/{node["name"]}: unexpected multi-sheet part')
            result[node['name']] = body
            for sheet, material_ids in sorted(sheets.items()):
                if sheet == body:
                    continue
                detail = 'hull_detail_' + exporter.texture_id(sheet)
                result[detail] = sheet
                settings = None
                for material in set(material_ids):
                    presets = port.inherited(materials, material, 'enabledPresets') or {}
                    properties = port.inherited(materials, material, 'properties') or {}
                    threshold = properties.get('alphatestThreshold')
                    cutoff = struct.unpack('<f', bytes(threshold)[-4:])[0] if threshold and presets.get('AlphaTest') else 0.
                    cull = port.inherited(materials, material, 'customCullMode')
                    current = {'alphaCutoff': cutoff, 'cullMode': 0 if cull == 0 else 2}
                    if settings is not None and settings != current:
                        raise ValueError('One detail sheet has conflicting source render states')
                    settings = current
                    state['materials'][material] = detail
                state['details'][detail] = {'sheet': sheet, 'settings': settings,
                                             'material_ids': sorted(set(material_ids))}
        if len(state['details']) != 1:
            raise ValueError('Barkhan must retain its one authored palm batch')
        return result
    exporter.client_albedos = albedos

    def mesh_from(self, name, sources, pivot=(0., 0., 0.)):
        if name != 'hull_vis' or not state['details']:
            return original_mesh(self, name, sources, pivot)
        main, details = [], {}
        for source in sources:
            main_batches = []
            for batch in source['batches']:
                detail = state['materials'].get(batch[1])
                if detail is None:
                    main_batches.append(batch)
                else:
                    copied = dict(source); copied['batches'] = [batch]
                    details.setdefault(detail, []).append(copied)
            if main_batches:
                copied = dict(source); copied['batches'] = main_batches
                main.append(copied)
        self.gltf._german_hull_details = []
        for detail, nodes in sorted(details.items()):
            mesh = original_mesh(self, detail, nodes, pivot)
            if mesh is None:
                raise ValueError('The source palm batch exported no geometry')
            self.gltf._german_hull_details.append((detail, mesh))
        return original_mesh(self, name, main, pivot)
    converter.MeshMaker.mesh_from = mesh_from

    def add_node(self, name, translation=None, mesh=None, children=None):
        if name == 'hull_vis' and getattr(self, '_german_hull_details', None):
            children = list(children or [])
            for detail, detail_mesh in self._german_hull_details:
                children.append(original_node(self, detail, mesh=detail_mesh))
            self._german_hull_details = []
        return original_node(self, name, translation=translation, mesh=mesh, children=children)
    converter.Gltf.add_node = add_node

    def write_objects(items, destination, material_for, materials, groups=False, record_objects=None):
        if not groups or not state['details']:
            return original_write(items, destination, material_for, materials, groups, record_objects)
        expanded = list(items)
        sheets, extra_materials = {}, dict(materials)
        for source, node, _name, *unused in items:
            if node != 'hull_vis':
                continue
            scene = exporter.scene_of(source)
            for detail, info in sorted(state['details'].items()):
                if detail not in scene.graph.nodes or not exporter.part_nodes(scene, detail):
                    raise ValueError('Cached GLB lost source palm; rebuild the configuration')
                expanded.append((source, detail, detail))
                material = destination.stem + '_' + detail
                sheets[detail] = material
                extra_materials[material] = 'Textures/' + exporter.texture_id(info['sheet']) + '.png'
        return original_write(expanded, destination,
            lambda name: sheets.get(name) or material_for(name), extra_materials, groups, record_objects)
    exporter.write_objects = write_objects

    def port_card(card, file_name, rebuild, textures):
        result = original_card(card, file_name, rebuild, textures)
        if state['details']:
            path = exporter.RESOURCES / 'Data' / (exporter.unity_id(card['name']).lower() + '_nodes.json')
            nodes = json.loads(path.read_text())
            for detail, info in sorted(state['details'].items()):
                nodes['albedos'][detail] = exporter.texture_id(info['sheet'])
                nodes.setdefault('albedoMaterialSettings', {})[detail] = info['settings']
            path.write_text(json.dumps(nodes, ensure_ascii=False, indent=2, sort_keys=True) + '\n')
            result['source_hull_details'] = copy.deepcopy(state['details'])
        if file_name == 'G165_Erich_Koncept':
            # The source turret_01 is only an 11-mm yaw-carrier plane; the
            # actual armoured body is in gun_*_mask and elevates with the gun.
            # The stationary pivot shell is inside that complete assembly,
            # not displaced down onto the visually empty carrier plane.
            scene_path = exporter.TANKS / exporter.MODEL_DIR / (file_name + '.sc2')
            source_nodes = converter.read_scene(str(scene_path), 0)
            groups = converter.read_groups(str(scene_path))
            carrier = converter.pick(source_nodes, 'turret_01')
            box = converter.nodes_bbox(groups, carrier)
            if box is None or box[1][2] - box[0][2] > .02:
                raise ValueError('Erich source yaw-carrier geometry changed; re-audit articulation')
            for gun in ('gun_01', 'gun_02'):
                if not converter.pick(source_nodes, gun + '_mask'):
                    raise ValueError('Erich source lost its oscillating upper turret')
            hosts = {'turret_01': ['turret_01', 'gun_01', 'gun_02']}
            path = exporter.RESOURCES / 'Data' / (exporter.unity_id(card['name']).lower() + '_nodes.json')
            nodes = json.loads(path.read_text())
            nodes['collisionVisualHosts'] = hosts
            path.write_text(json.dumps(nodes, ensure_ascii=False, indent=2, sort_keys=True) + '\n')
            result['source_articulation'] = {
                'policy': 'Yaw-carrier plane plus authored oscillating gun-mask body; keep XML pivots and collision unchanged',
                'collision_visual_hosts': hosts,
                'carrier_height': box[1][2] - box[0][2],
            }
        result['source_primitive_order_policy'] = 'Stable exact glTF primitive attributes; retain all source vertices/normals/UVs/indices'
        if file_name == 'G136_Tiger_131':
            result['source_pivot_policy'] = 'Parameters collision/*/points; obsolete PC XML positions do not match this client mesh'
        return result
    exporter.port_card = port_card


def configure(port):
    original = port.setup_exporter
    def setup():
        original()
        install(port)
    port.setup_exporter = setup


def prepare_source_pivots(port, record, parameters_path):
    """Use client-authored DAVA pivots for Tiger 131's older XML convention.

    Other German cards use their XML positions, which match their meshes.
    Tiger 131's XML places the trunnion a metre below the source barrel;
    its Parameters YAML contains the actual scene/collision frame.
    """
    if record['vehicle_id'] != 'G136_Tiger_131':
        return
    import yaml
    from xml.etree import ElementTree as ET
    config = yaml.safe_load(parameters_path.read_text())
    path = port.PACK / 'XML/item_defs/vehicles/germany' / (record['vehicle_id'] + '.xml')
    root = ET.parse(path).getroot()
    pivots = {}
    for turret in root.find('turrets0'):
        for node in [turret] + list(turret.find('guns')):
            name = port.slot(node)
            values = config['collision'][name]['points']
            if len(values) != 3:
                raise ValueError('Invalid authored Tiger 131 pivot')
            port.set_text(node, 'blitz/points/normal', ' '.join(str(v) for v in values))
            pivots[name] = values
    ET.indent(root)
    ET.ElementTree(root).write(path, encoding='utf-8')
    record['pivot_policy'] = 'Parameters YAML collision/*/points (DAVA); preserve source barrel axis'
    record['source_pivots'] = pivots


def ensure_source_imports(port, records):
    """Emit the source policy before Unity's very first import, not only after.

    Meshes stay uncompressed and every texture is tagged with the ASTC block
    of its client texture (port.source_tag). Existing GUIDs/settings remain
    intact. A fresh checkout gets stable GUIDs; the project's asset
    postprocessor fills all normal importer defaults.
    """
    manifest = json.loads((port.DATA / 'tank_models_source_manifest.json').read_text())['tanks']
    touched = 0
    for record in records:
        rid = port.exporter.unity_id(record['name'])
        folder = port.ROOT / 'Assets/Blitz/Resources/Models/Tanks' / rid
        assets = sorted(folder.glob('*.obj')) + sorted((folder / 'Textures').glob('*.png'))
        if len(list(folder.glob('*.obj'))) != 3:
            raise ValueError(f'{rid}: missing visible/armour/physics model')
        tags = {texture['destination']: port.source_tag(texture)
                for texture in manifest.get(record['name'], {}).get('textures', [])}
        for asset in assets:
            meta = asset.with_name(asset.name + '.meta')
            importer = 'ModelImporter' if asset.suffix == '.obj' else 'TextureImporter'
            tag = tags.get(asset.name, port.SOURCE_TAG)
            if meta.exists():
                text = meta.read_text()
                if re.search(r'(?m)^  userData:', text):
                    text = re.sub(r'(?m)^  userData:.*$', '  userData: ' + tag, text)
                else:
                    text = text.replace(importer + ':\n', importer + ':\n  userData: ' + tag + '\n', 1)
            else:
                guid = hashlib.sha256((port.SOURCE_TAG + '/' + str(asset.relative_to(port.ROOT))).encode()).hexdigest()[:32]
                version = 24600 if importer == 'ModelImporter' else 13
                text = f'fileFormatVersion: 2\nguid: {guid}\n{importer}:\n  serializedVersion: {version}\n  userData: {tag}\n'
            if '  userData: ' + tag + '\n' not in text:
                raise ValueError(f'Cannot mark importer metadata: {meta}')
            if asset.suffix == '.obj':
                text = re.sub(r'(?m)^    meshCompression:.*$', '    meshCompression: 0', text)
            if not meta.exists() or meta.read_text() != text:
                meta.write_text(text)
                touched += 1
    print('Source import metadata updated:', touched, flush=True)
