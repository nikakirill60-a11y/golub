#!/usr/bin/env python3
"""Reproducible, additive Blitz 11.20 branches; never rewrites 4.8 cards.

The original XML and scene bytes are immutable. A derived compatibility pack
adds only the slot/pivot metadata expected by the established Unity exporter.
Research XP is a documented 4.8-derived policy, NOT claimed to be client XP.
"""
from __future__ import annotations
import argparse
import copy
import hashlib
import json
import os
import re
import statistics
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'Assets/Blitz/Resources/Data'
SOURCE = Path(os.environ.get('BLITZ_1120_PACK', str(Path.home() / 'projects/wotblitz11.20.0files')))
CACHE = ROOT / 'Tools/_branch_glb/1120'
PACK = CACHE / 'compat'
GODOT = Path.home() / 'projects/blitz-legacy-godot'
sys.path.insert(0, str(GODOT / 'tools'))
import dava
import dvpl
import blitz2glb
import port_blitz_480_data as base
import port_blitz_branch_models as exporter

BRANCHES = {
    'european': ['F01_Vickers_MkF', 'Pl06_10TP', 'Pl07_14TP', 'S04_Lago-I',
                 'S02_StrvM42', 'S07_Strv_74', 'S13_Leo', 'S18_EMIL_1951E1',
                 'S17_EMIL_1952E2', 'S16_Kranvagn'],
    'france': ['F126_AMX_13_M24', 'F123_ERAC_105_Proto', 'F120_Project_Louis', 'F119_Projet_Murat'],
    'china': ['Ch57_BZT_70'],
}
MODEL_NATIONS = {'european': 'European', 'france': 'France', 'china': 'China'}
NAMES = {v: v.lower() for ids in BRANCHES.values() for v in ids}
REPORT = DATA / 'blitz_1120_balance.json'
CARD_FILE = DATA / 'blitz_1120_cards.json'
INVENTORY = DATA / 'blitz_1120_inventory.json'


# The importer tag of the decoded assets; BlitzAssetPostprocessor.Source1120Tag.
SOURCE_TAG = 'blitz-source-11.20'
# PVR v3 pixel formats of the ASTC blocks the client ships.
ASTC_BLOCKS = {27: '4x4', 28: '5x4', 29: '5x5', 30: '6x5', 31: '6x6', 32: '8x5', 33: '8x6', 34: '8x8',
               35: '10x5', 36: '10x6', 37: '10x8', 38: '10x10', 39: '12x10', 40: '12x12'}


def source_tag(texture):
    """The importer tag of a decoded texture, from its manifest entry.

    It names the ASTC block of the client texture, at whose rate the asset
    postprocessor encodes the Android texture again; a texture the client did
    not ship as ASTC keeps the bare tag and stays RGBA32.
    """
    header = (PACK / texture['source']).read_bytes()[:16]
    if header[:4] != b'PVR\x03':
        raise ValueError(f"not a PVR v3 texture: {texture['source']}")
    block = ASTC_BLOCKS.get(int.from_bytes(header[8:16], 'little'))
    return SOURCE_TAG + (' astc=' + block if block else '')


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def unpack(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    payload = source.read_bytes()
    if source.suffix == '.dvpl':
        payload = dvpl.unpack_bytes(payload)
    if not destination.exists() or destination.read_bytes() != payload:
        destination.write_bytes(payload)
    return destination


def source_file(relative):
    # Scenes reach shared sheets through '../GunMarks/...'; the parent of
    # such a reference need not exist in the APK half of the pack.
    relative = os.path.normpath(str(relative))
    path = SOURCE / relative
    for p in (path.with_name(path.name + '.dvpl'), path, SOURCE / 'from_apk' / relative):
        if p.is_file():
            return p
    raise FileNotFoundError(relative)


def model_stem(nation, vehicle):
    directory = SOURCE / '3d/Tanks' / MODEL_NATIONS[nation]
    def norm(s):
        return re.sub('[^a-z0-9]', '', s.lower().replace('projet', 'project'))
    candidates = [p.name.removesuffix('.dvpl').removesuffix('.sc2') for p in directory.glob('*.sc2*')]
    exact = sorted(set(x for x in candidates if norm(x) == norm(vehicle)))
    if len(exact) != 1:
        raise RuntimeError(f'{vehicle}: ambiguous model: {exact}')
    return exact[0]


def xml_vector(node):
    if node is None or not node.text:
        return None
    values = [float(x) for x in node.text.replace(',', ' ').split()]
    if len(values) != 3:
        raise ValueError(node.text)
    # XML model coordinates: x, up, forward; DAVA: x, forward, up.
    return (values[0], values[2], values[1])


def set_text(parent, path, value):
    current = parent
    for key in path.split('/'):
        child = current.find(key)
        if child is None:
            child = ET.SubElement(current, key)
        current = child
    current.text = value


def slot(node):
    name = Path(base.local_text(node, 'models/undamaged')).stem.lower()
    if not re.fullmatch(r'(turret|gun)_\d+', name):
        raise RuntimeError(f'No exact slot for {node.tag}: {name}')
    return name


def materials(scene_path):
    raw = dava.read_sc2(scene_path.read_bytes())
    nodes = raw.get('dataNodes') or raw['scene'].get('#dataNodes', [])
    return {int.from_bytes(bytes(m['#id']), 'little'): m for m in nodes if m.get('##name') == 'NMaterial'}


def inherited(mat, key, field):
    seen = set()
    while key is not None and key not in seen:
        seen.add(key)
        m = mat.get(int(key), {})
        cfg = m.get('configArchive_0', m.get('configArchive', {}))
        if field in cfg:
            return cfg[field]
        if field in m:
            return m[field]
        key = m.get('parentMaterialKey')
    return None


def is_shadow(mat, key):
    seen = set()
    while key is not None and key not in seen:
        seen.add(key)
        m = mat.get(int(key), {})
        if 'shadow' in str(m.get('materialName', '')).lower():
            return True
        key = m.get('parentMaterialKey')
    return False


def prepare():
    records = []
    for nation, ids in BRANCHES.items():
        vehicle_dir = PACK / 'XML/item_defs/vehicles' / nation
        original_dir = SOURCE / 'from_apk/XML/item_defs/vehicles' / nation
        for src in sorted((original_dir / 'components').glob('*.xml')) + [original_dir / 'list.xml']:
            unpack(src, vehicle_dir / src.relative_to(original_dir))
        for vehicle in ids:
            stem = {'S04_Lago-I': 'Lago_I'}.get(vehicle) or model_stem(nation, vehicle)
            scene_rel = f'3d/Tanks/{MODEL_NATIONS[nation]}/{stem}.sc2'
            scene = unpack(source_file(scene_rel), PACK / scene_rel)
            unpack(source_file(scene_rel.removesuffix('.sc2') + '.scg'), scene.with_suffix('.scg'))
            collision_rel = f'3d/Tanks/CollisionMeshes/{nation}-{vehicle}.sc2'
            collision = unpack(source_file(collision_rel), PACK / collision_rel)
            unpack(source_file(collision_rel.removesuffix('.sc2') + '.scg'), collision.with_suffix('.scg'))
            mat = materials(scene)
            texture_refs = set()
            for key in mat:
                textures = inherited(mat, key, 'textures') or {}
                texture_refs.update(textures.values())
            texture_sources = []
            for ref in sorted(texture_refs):
                if not ref.endswith('.tex'):
                    continue
                rel = f'3d/Tanks/{MODEL_NATIONS[nation]}/' + ref.removesuffix('.tex') + '.astc.pvr'
                try:
                    src = source_file(rel)
                except FileNotFoundError:
                    suffix = str(Path(ref).with_suffix('.astc.pvr'))
                    matches = sorted(SOURCE.glob('3d/Tanks/*/' + suffix + '*'))
                    if not matches:
                        raise RuntimeError(f'{vehicle}: missing texture {ref}')
                    if len({digest(p) for p in matches}) > 1:
                        raise RuntimeError(f'{vehicle}: ambiguous shared texture {ref}')
                    src = matches[0]
                dst = PACK / rel
                unpack(src, dst)
                texture_sources.append({'reference': ref, 'source': str(src.relative_to(SOURCE)), 'sha256': digest(src)})
            original = original_dir / (vehicle + '.xml')
            root = ET.parse(original).getroot()
            turret_pos = xml_vector(root.find('hull/turretPositions/turret'))
            for turret in root.find('turrets0'):
                set_text(turret, 'blitz/blitzPartName', slot(turret))
                if turret_pos is not None:
                    set_text(turret, 'blitz/points/normal', ' '.join(str(-x) for x in turret_pos))
                gun_offset = xml_vector(turret.find('gunPosition'))
                for gun in turret.find('guns'):
                    set_text(gun, 'blitz/blitzPartName', slot(gun))
                    if turret_pos is not None and gun_offset is not None:
                        pivot = [a+b for a,b in zip(turret_pos, gun_offset)]
                        set_text(gun, 'blitz/points/normal', ' '.join(str(-x) for x in pivot))
            set_text(root, 'blitz/blitzModelPath', str(Path(scene_rel).relative_to('3d')))
            set_text(root, 'blitz/collisionMesh', str(Path(collision_rel).relative_to('3d/Tanks')))
            xml = vehicle_dir / (vehicle + '.xml')
            ET.indent(root)
            ET.ElementTree(root).write(xml, encoding='utf-8')
            records.append({'nation': nation, 'vehicle_id': vehicle, 'name': NAMES[vehicle],
                            'model_stem': stem, 'scene': scene_rel, 'collision': collision_rel,
                            'xml_source': str(original.relative_to(SOURCE)), 'xml_sha256': digest(original),
                            'textures': texture_sources})
    save(INVENTORY, {'version': '11.20', 'source': str(SOURCE), 'records': records})
    return records


def median(values, fallback):
    values = [float(x) for x in values if float(x) > 0]
    return statistics.median(values) if values else fallback


def bounded(value, peers, low=0.8, high=1.2):
    reference = median(peers, value)
    return round(min(max(value, reference * low), reference * high), 6) if reference > 0 else value


def research(card, peers, successor=None):
    if card.get('collector'):
        return
    groups = [('turrets', 'turret'), ('guns', 'gun'), ('tracks', 'chassis'), ('engines', 'engine'), ('radios', 'radio')]
    for group, kind in groups:
        mods = card[group]
        for i, module in enumerate(mods):
            module['unlocks'] = {}
            module['cost_xp'] = 0 if i == 0 else max(1, round(median(
                [m['cost_xp'] for c in peers for m in c[group] if m['cost_xp'] > 0], 250 * card['lvl'] ** 2) * (0.75 + 0.25*i)))
            if i + 1 < len(mods):
                target = mods[i+1]
                module['unlocks'][kind + ':' + target['name']] = max(1, round(median(
                    [m['cost_xp'] for c in peers for m in c[group] if m['cost_xp'] > 0], 250*card['lvl']**2) * (0.75+0.25*(i+1))))
        if group == 'guns':
            # A gun exclusive to the upgraded turret must be reachable through
            # that turret, never require an incompatible stock mount.
            for turret in card['turrets']:
                for entry in turret['guns']:
                    gun = next(m for m in mods if m['name'] == entry['name'])
                    entry['unlocks'] = copy.deepcopy(gun['unlocks'])
                    if gun['cost_xp'] > 0:
                        turret['unlocks']['gun:' + gun['name']] = gun['cost_xp']
    if successor:
        card['unlock_tanks'] = {successor['name']: successor['cost_xp']}
        card['guns'][-1]['unlocks']['vehicle:' + successor['name']] = successor['cost_xp']
        for turret in card['turrets']:
            for entry in turret['guns']:
                if entry['name'] == card['guns'][-1]['name']:
                    entry['unlocks']['vehicle:' + successor['name']] = successor['cost_xp']


def rebalance(card, peers):
    before = copy.deepcopy(card)
    # Only combat/economy scalars are calibrated. Geometry, plate topology,
    # shell kinds, magazine capacity and module compatibility never change.
    card['health'] = round(bounded(card['health'], [c['health'] for c in peers], .85, 1.15))
    for axis in ('x', 'y'):
        card['speed_limits'][axis] = bounded(card['speed_limits'][axis], [c['speed_limits'][axis] for c in peers], .65, 1.3)
    for group, fields in [('turrets', ['health', 'rot_speed', 'spot_radius']), ('tracks', ['rot_speed', 'disp_move_mul', 'disp_rotation_mul']), ('radios', ['distance'])]:
        for m in card[group]:
            candidates = [p for c in peers for p in c[group] if abs(p['level']-m['level']) <= 1]
            for field in fields:
                m[field] = bounded(m[field], [p[field] for p in candidates], .75, 1.25)
    mass = card['hull_weight'] + card['tracks'][-1]['weight'] + card['guns'][-1]['weight'] + card['engines'][-1]['weight']
    for engine in card['engines']:
        densities = [m['power'] / max(1, c['hull_weight']+c['tracks'][-1]['weight']+c['guns'][-1]['weight']+m['weight']) for c in peers for m in c['engines']]
        engine['power'] = round(bounded(engine['power']/mass, densities, .75, 1.3)*mass)
    for gun in card['guns']:
        candidates = [p for c in peers for p in c['guns'] if abs(p['level']-gun['level']) <= 1 and abs(p['caliber']-gun['caliber']) <= max(15,gun['caliber']*.25)]
        if not candidates:
            candidates = [p for c in peers for p in c['guns']]
        for f in ('disp','aim_time'):
            gun[f] = bounded(gun[f], [p[f] for p in candidates], .75,1.25)
        for s in gun['shells']:
            same = [x for p in candidates for x in p['shells'] if x['type']==s['type']]
            s['dmg'] = round(bounded(s['dmg'], [x['dmg'] for x in same], .8,1.2))
            s['pen'] = round(bounded(s['pen'], [x['pen'] for x in same], .8,1.2))
        # Sustained output counts every shell and the intraclips, not alpha /
        # whole-magazine reload. Auto-reload uses its fastest sustainable shot.
        if gun['shells']:
            def dpm(g):
                count = sum(g['burst_info'].get('bursts', [])) or 1
                gap = g['burst_info'].get('intraclip',0) * max(0,count-1)
                return 60*g['shells'][0]['dmg']*count/max(.01,g['reload']+gap)
            target = bounded(dpm(gun),[dpm(g) for g in candidates if g['shells']],.85,1.15)
            count = sum(gun['burst_info'].get('bursts', [])) or 1
            gap = gun['burst_info'].get('intraclip',0)*max(0,count-1)
            old_reload = gun['reload']
            gun['reload'] = round(max(.5,60*gun['shells'][0]['dmg']*count/target-gap),6)
            if gun.get('autoreload_times'):
                factor = gun['reload']/max(.01,old_reload)
                gun['autoreload_times'] = [round(t*factor,6) for t in gun['autoreload_times']]
                gun['reload'] = round(sum(gun['autoreload_times']),6)
    for turret in card['turrets']:
        for entry in turret['guns']:
            original = next(g for g in before['guns'] if g['name']==entry['name'])
            adjusted = next(g for g in card['guns'] if g['name']==entry['name'])
            entry['reload'] = round(entry['reload']*adjusted['reload']/max(.01,original['reload']),6)
    card['spot_radius'] = card['turrets'][-1]['spot_radius']
    card['turret_health'] = card['turrets'][-1]['health']
    card['turret_rot_speed'] = card['turrets'][-1]['rot_speed']
    return {'name': card['name'], 'level': card['lvl'], 'class': card['tank_class'],
            'competitors': [p['name'] for p in peers], 'original': before, 'balanced': copy.deepcopy(card)}


class NationClient1120(base.NationClient):
    """11.20 local shot entries override metadata, not the whole shared shot.

    Vehicle XML can list only <shell>shared<price>...</price></shell>. The
    inherited guns.xml entry still owns speed, gravity and piercingPower.
    The 4.8 shallow block merger discarded those fields when replacing shots.
    Keep this version-specific resolution out of the legacy 4.8 data path.
    """
    def merge(self, kind, node):
        result = super().merge(kind, node)
        if kind != 'guns':
            return result
        shared = self.shared[kind].get(node.tag)
        inherited = shared.find('shots') if shared is not None else None
        local = node.find('shots')
        if inherited is None or local is None:
            return result
        shots = ET.Element('shots')
        for override in local:
            resolved = copy.deepcopy(base.merged(inherited.find(override.tag), override))
            # Fail closed instead of shipping a gun trace that ends at its pivot.
            if base.number(resolved.find('speed')) <= 0:
                raise ValueError(f'{self.nation}/{node.tag}/{override.tag}: missing inherited projectile speed')
            shots.append(resolved)
        previous = result.find('shots')
        if previous is not None:
            result.remove(previous)
        result.append(shots)
        return result


def build_cards(records):
    document = json.loads((DATA / 'tanks.json').read_text())
    old = [c for c in document['tanks'] if c.get('source_version') != '11.20']
    base.PACK = PACK
    base.VEHICLES = PACK / 'XML/item_defs/vehicles'
    base.NATION_ID['european'] = 9
    import yaml
    strings = yaml.safe_load((SOURCE / 'from_apk/Strings/ru.yaml').read_text())
    clients = {n: NationClient1120(n,strings) for n in BRANCHES}
    cards, balances = [], []
    for r in records:
        client = clients[r['nation']]
        card = base.build_card(client, {'id':r['vehicle_id']}, NAMES)
        root = ET.parse(client.directory / (r['vehicle_id']+'.xml')).getroot()
        # This mechanic is not a conventional autoloader. Preserve its complete
        # shell-by-shell timers for the runtime rather than flattening to a drum.
        for turret in root.find('turrets0'):
            for local in turret.find('guns'):
                gun_xml = client.merge('guns',local)
                times = base.vector(gun_xml.find('pumpGunReloadTimes'), ())
                if times and base.local_text(gun_xml,'pumpGunMode') == 'true':
                    gun = next(g for g in card['guns'] if g['name']==local.tag)
                    gun['autoreload_times'] = times
                    gun['reload'] = sum(times)
        for module,xml in zip(card['tracks'],root.find('chassis')):
            armor = exporter.armor_values(xml)
            if armor.get('leftTrack') != armor.get('rightTrack'):
                raise RuntimeError('Asymmetric track armour requires side-specific modules')
            module['armor'] = armor.get('leftTrack',0)
        for i,turret in enumerate(root.find('turrets0')):
            card['turrets'][i]['mesh'] = slot(turret)
            for entry,gun in zip(card['turrets'][i]['guns'],turret.find('guns')):
                entry['mesh'] = slot(gun)
        card.update(source_version='11.20',source=r['xml_source'],source_sha256=r['xml_sha256'])
        tree = yaml.safe_load((SOURCE / f"from_apk/Configs/TechTree/{r['nation']}_tree.yaml").read_text())
        card['tree_branch'] = tree['tanks'].get(r['vehicle_id'], {'position':[card['lvl'],6]})['position'][1]-6
        # The 4.8 AMX light branch already occupies the newer Murat row. Keep
        # both branches; a free hybrid-layout row is not a change of research.
        if r['nation']=='france':
            card['tree_branch'] = 0
        card['acquisition'] = ''
        if r['nation']=='china':
            card.update(tree_tank=False,prem=True,collector=True,shop_tank=False,
                        acquisition='collectible',cost=0,cost_gold=0,cost_xp=0,tree_branch=0)
        peers = [c for c in old if c['lvl']==card['lvl'] and c['tank_class']==card['tank_class'] and not c.get('prem')]
        if not peers:
            peers = [c for c in old if c['lvl']==card['lvl'] and not c.get('prem')]
        card['cost_xp'] = 0 if card.get('collector') or card['lvl']==1 else round(median([c['cost_xp'] for c in peers],1000*card['lvl']**2))
        balances.append(rebalance(card,peers))
        cards.append(card)
    by_name = {c['name']:c for c in cards}
    for nation,ids in BRANCHES.items():
        for i,vehicle in enumerate(ids):
            card = by_name[NAMES[vehicle]]
            peers = [c for c in old if c['lvl']==card['lvl'] and c['tank_class']==card['tank_class'] and not c.get('prem')]
            if not peers:
                peers = [c for c in old if c['lvl']==card['lvl'] and not c.get('prem')]
            successor = by_name[NAMES[ids[i+1]]] if i+1<len(ids) else None
            research(card,peers,successor)
    save(CARD_FILE,{'version':'11.20','policy':'complete source modules; 4.8 peer calibration; synthetic research XP','tanks':cards})
    save(REPORT,{'baseline':'4.8.0 researchable same-tier same-class; same module level/calibre where available',
                 'policy':'bounded median calibration; preserved mesh, capacity, compatibility, weight, module damage pools, angles and shell types',
                 'tanks':balances})
    print('cards',len(cards),'modules',{k:sum(len(c[k]) for c in cards) for k in ('turrets','guns','tracks','engines','radios')})
    return cards


def configure(r):
    exporter.PACK = PACK
    exporter.TANKS = PACK / '3d/Tanks'
    exporter.VEHICLES = PACK / 'XML/item_defs/vehicles' / r['nation']
    exporter.MODEL_DIR = MODEL_NATIONS[r['nation']]
    exporter.IMAGES = exporter.TANKS / exporter.MODEL_DIR / 'images'
    rid = exporter.unity_id(r['name']).lower()
    exporter.MODEL = {rid:r['model_stem']}
    exporter.COLLISION = {rid:Path(r['collision']).stem}
    exporter.COLLISION_PREFIX = ''
    root = ET.parse(exporter.VEHICLES/(r['vehicle_id']+'.xml')).getroot()
    exporter.TRACK_ARMOR = exporter.armor_values(root.find('chassis')[0])['leftTrack']
    exporter.GLB_CACHE = CACHE / r['nation']


def bleed_clipped(path, cutoff=None):
    """Fill the RGB under clipped track texels with the nearest drawn colour.

    Strv m/42 ships light grey/white RGB under alpha < _Cutoff. The alpha test
    hides those texels, but bilinear filtering and Unity's mip chain blend them
    into the neighbouring links and show up as white teeth along the track.
    Alpha and every texel that survives the clip stay byte-identical to the
    decoded source; only invisible RGB changes.
    """
    import numpy as np
    from PIL import Image
    cutoff = exporter.TRACK_CUTOFF if cutoff is None else cutoff
    pixels = np.array(Image.open(path).convert('RGBA'))
    colour = pixels[:, :, :3].astype(np.int32)
    filled = pixels[:, :, 3] >= cutoff
    if filled.all() or not filled.any():
        return
    while not filled.all():
        total = np.zeros(colour.shape, dtype=np.int32)
        count = np.zeros(filled.shape, dtype=np.int32)
        for axis, shift in ((0, 1), (0, -1), (1, 1), (1, -1)):
            neighbour = np.roll(filled, shift, axis=axis)
            total += np.where(neighbour[:, :, None], np.roll(colour, shift, axis=axis), 0)
            count += neighbour
        grow = ~filled & (count > 0)
        if not grow.any():
            break
        colour = np.where(grow[:, :, None], total // np.maximum(count, 1)[:, :, None], colour)
        filled |= grow
    pixels[:, :, :3] = colour.astype(np.uint8)
    Image.fromarray(pixels, 'RGBA').save(path)


# Gun assemblies whose visible parts hang below the slot node instead of
# being the slot itself. The Annihilator's `gun_01` is a 10 cm stub: the three
# barrels are the skinned `gatling_draw_silhouette` cylinder, and the mantlet
# carries the skinned recoil rig (levers and ammunition belt) and the reload
# drum. Rigid skins are authored in model space, so their bind pose exports
# as is. `children` join the gun mesh; `rotor` becomes the separate
# `<gun>_rotor` (the triangles of `rotor_joint`). With a `clip`, every other
# joint the clip moves becomes its own `<gun>_anim_<joint>` object and the
# clip is baked into the nodes table: the client plays it on the first shell
# of a burst (`OnShootFirstInBurst`), and the `reload` skeletons on
# `ReloadFinished`.
GUN_ASSEMBLIES = {
    'oth25hwt20falconfire': {
        'gun_01': {
            'children': ('recoil_system', 'reload_drum'),
            'rotor': ('gatling_draw_silhouette',),
            'rotor_joint': 'gatling_rotate',
            'clip': 'Oth25_HWT20_FalconFire',
            'reload': ('reload_drum',),
        },
    },
}
ANIM_FPS = 30
_OBJECTS = {}
_ASSEMBLY = {'card': None, 'file': None, 'rotor': None, 'joints': None, 'skeletons': {}}


def assembly_skeletons(file_name):
    """Rigid skeletons of one scene, cached per scene file."""
    cache = _ASSEMBLY['skeletons']
    if file_name not in cache:
        import blitz_1120_joints
        path = exporter.TANKS / exporter.MODEL_DIR / (file_name + '.sc2')
        cache[file_name] = blitz_1120_joints.skeletons(dava.read_sc2(path.read_bytes()))
    return cache[file_name]


def assembly_clip(spec):
    import dava_anim
    relative = '3d/Tanks/Animations/%s.anim' % spec['clip']
    return dava_anim.read_anim(str(unpack(source_file(relative), PACK / relative)))


def assembly_objects(card_id, file_name, gun_slot):
    """`{object name: (entity, joint)}` of every moving joint that draws triangles."""
    key = (card_id, file_name, gun_slot)
    if key not in _OBJECTS:
        _OBJECTS[key] = _assembly_objects(card_id, file_name, gun_slot)
    return _OBJECTS[key]


def _assembly_objects(card_id, file_name, gun_slot):
    import blitz_1120_joints
    spec = GUN_ASSEMBLIES.get(card_id, {}).get(gun_slot)
    if not spec or not spec.get('clip'):
        return {}
    clip = assembly_clip(spec)
    skeletons = assembly_skeletons(file_name)
    objects = {}
    for entity in spec.get('children', ()) + spec.get('rotor', ()):
        skeleton = skeletons[entity]
        drawn = {name for names in skeleton['groups'].values() for name in names}
        for joint in sorted(blitz_1120_joints.animated(skeleton, clip, ANIM_FPS * 2) & drawn):
            name = (gun_slot + '_rotor' if joint == spec.get('rotor_joint')
                    else '%s_anim_%s' % (gun_slot, joint.lower()))
            objects[name] = (entity, joint)
    return objects


def assembly_filter(group_id):
    """Joints whose triangles of `group_id` the current export keeps, or None."""
    spec = GUN_ASSEMBLIES.get(_ASSEMBLY['card'] or '', {})
    if not spec or not _ASSEMBLY['file']:
        return None
    skeletons = assembly_skeletons(_ASSEMBLY['file'])
    for gun_slot, gun in spec.items():
        for entity in gun.get('children', ()) + gun.get('rotor', ()):
            names = skeletons.get(entity, {}).get('groups', {}).get(group_id)
            if names is None:
                continue
            if _ASSEMBLY['joints'] is not None:
                return set(_ASSEMBLY['joints'])
            moving = {joint for _e, joint in assembly_objects(
                _ASSEMBLY['card'], _ASSEMBLY['file'], gun_slot).values()}
            return set(names) - moving
    # Rigid geometry outside the skins belongs to the base gun export only.
    return set() if _ASSEMBLY['joints'] is not None else None


def assembly_animations(card_id, file_name, gun_slot, pivots):
    """Baked `{clip: {...}}` of one gun slot for the nodes table, or {}."""
    import blitz_1120_joints
    spec = GUN_ASSEMBLIES.get(card_id, {}).get(gun_slot)
    objects = assembly_objects(card_id, file_name, gun_slot)
    if not objects:
        return {}
    clip = assembly_clip(spec)
    skeletons = assembly_skeletons(file_name)
    reload = set(spec.get('reload', ()))
    result = {}
    for label, trigger, chosen in (
        ('shot', 'burst', {n: v for n, v in objects.items() if v[0] not in reload}),
        ('reload', 'reloaded', {n: v for n, v in objects.items() if v[0] in reload}),
    ):
        if not chosen:
            continue
        baked = blitz_1120_joints.bake(
            [skeletons[e] for e in sorted({e for e, _j in chosen.values()})], clip,
            {n: j for n, (_e, j) in chosen.items()},
            {n: pivots[n]['world'] for n in chosen}, ANIM_FPS)
        baked['trigger'] = trigger
        result[label] = baked
    return result


# Visible decor the client hangs *below* a part node instead of naming it
# `<part>_nc`: whatever the default state of a skin's `*_hide_elements_switch`
# shows (TS-60 `hull_hide_elements`, M-6-Y `gun_01_mask_hide_elements` ...;
# camouflages.yaml swaps them for the 3D style's `Skin_*` parts), and a few
# fittings drawn in their rest pose (Hellcat's rammer `pusher`, engine fans,
# radar, antenna). Ability or transformation parts (`*_anim`, `*_Open`,
# shields) and the inactive skin states stay out. Only decor drawn with its
# part's own opaque sheet joins it: another sheet or an alpha-tested net would
# need a material the merged part does not have.
DECOR_PART = re.compile(r'hull|turret_\d+|gun_\d+(_mask)?')
DECOR_FITTINGS = frozenset({
    'pusher', 'antenna', 'gearwheel_cooler', 'fan', 'radar', 'gun_01_lamp',
    'gun_01_mask_cylinders',
})
DECOR_SKIP = re.compile(r'gun_marks|^HP_|^Socket', re.I)
_DECOR = {}


def _sheets(mat, batches):
    out = set()
    for _group, material, _lod in batches:
        if is_shadow(mat, material):
            continue
        presets = inherited(mat, material, 'enabledPresets') or {}
        if any(value and ('Alpha' in key or 'Transparent' in key) for key, value in presets.items()):
            out.add(None)
        out.add(Path((inherited(mat, material, 'textures') or {}).get('albedo', '')).stem)
    return out


def decor_owners(path, mat):
    """Per flattened node (read_scene order): the part a decor node joins, or None."""
    key = str(path)
    if key in _DECOR:
        return _DECOR[key]
    raw = dava.read_sc2(Path(path).read_bytes())
    owners = []
    def walk(entity, part, part_sheets, state):
        name = entity.get('name') or ''
        owner = None
        if part is None and DECOR_PART.fullmatch(name):
            part = name
            part_sheets = _sheets(mat, blitz2glb.batches_of(entity, 0)) - {None, ''}
        elif part is not None and state != 'hidden':
            batches = blitz2glb.batches_of(entity, 0)
            sheets = _sheets(mat, batches)
            if (batches and not DECOR_SKIP.search(name)
                    and not blitz2glb.DECOR_SUFFIX.search(name)
                    and not name.endswith('_cap')
                    and (state == 'active' or name in DECOR_FITTINGS)
                    and sheets and None not in sheets and sheets <= part_sheets):
                owner = part
        owners.append(owner)
        switch = next(blitz2glb.components(entity, 'StateSwitcherComponent'), None)
        states, active = set(), None
        if switch is not None:
            states = {switch.get('ssc.state%d' % i) for i in range(int(switch.get('ssc.statesCount') or 0))}
            active = switch.get('ssc.state%d' % int(switch.get('ssc.activeState') or 0))
        for child in blitz2glb.as_items(entity.get('#hierarchy')):
            if not isinstance(child, dict):
                continue
            child_state = state
            child_name = child.get('name') or ''
            if child_name in states:
                child_state = 'active' if child_name == active and state != 'hidden' else 'hidden'
            walk(child, part, part_sheets, child_state)
    for entity in blitz2glb.as_items(raw['scene'].get('#hierarchy')):
        if isinstance(entity, dict):
            walk(entity, None, set(), None)
    _DECOR[key] = owners
    return owners


# Bolt-on kit ("obves") nodes of a part: `turret_01_obves_02`,
# `gun_03_mask_obves`, `gun_03_mask_obves_nc_01`. They are drawn with the part
# and are not decor of another node, so `pick_part` never matched them: the
# Silencer keeps the whole barrel tube of its top guns in `gun_0X_mask_obves`
# and shipped a gun that ended at the mantlet.
OBVES = re.compile(r'_obves(?:_\d+)?(?=(?:_nc(?:_\d+)?)?$)')
SHEET_PART = re.compile(r'hull|turret_\d+|gun_\d+')
_SHEET = {'root': None, 'sheet': None, 'path': None, 'scenes': {}}
# Scenes whose extra sheets a nation module exports itself.
SHEET_SKIP = set()


def scene_path(card_id, file_name):
    return exporter.TANKS / exporter.MODEL_DIR / (exporter.MODEL.get(card_id, file_name) + '.sc2')


def primary_sheet(sheets):
    """The sheet a multi-sheet node is drawn with; glass and laser come last."""
    return sorted(sheets, key=lambda value: (
        'laser' in value.lower() or 'glass' in value.lower(), value.lower()))[0]


def sheet_root(node):
    """The exported part (`hull`, `turret_NN`, `gun_NN`) a scene node draws into."""
    name = node.get('decor_of') or node['name']
    name = OBVES.sub('', name)
    name = blitz2glb.DECOR_SUFFIX.sub('', name)
    name = name.removesuffix('_cap')
    name = re.sub(r'^(gun_\d+)_mask$', r'\1', name)
    if name in ('chassis_chassis_L', 'chassis_chassis_R'):
        name = 'hull'
    return name if SHEET_PART.fullmatch(name) else None


def scene_sheets(path):
    """`{'groups': {group: {(part, sheet)}}, 'extras': {part: [sheet ...]}}`.

    One OBJ object takes one albedo, so a part whose batches bind several
    opaque sheets drew the foreign ones with its own: the Silencer muzzle
    brake and every obves sheet batch came out with the hull's texels. Such
    batches leave the part object and are exported as `<object>_sheetN`, one
    object per extra sheet (glass and laser sheets stay with the part).
    Interchangeable modules share one polygon group (the Silencer's
    `gun_03_mask_obves` and `gun_04_mask_obves`), so a group can draw into
    several parts.
    """
    key = str(path)
    if key in _SHEET['scenes']:
        return _SHEET['scenes'][key]
    if Path(path).stem in SHEET_SKIP:
        _SHEET['scenes'][key] = {'groups': {}, 'extras': {}}
        return _SHEET['scenes'][key]
    mat = materials(Path(path))
    body = exporter.client_textures(Path(path).stem)[0].name.removesuffix('.mali.pvr')
    groups, sheets, own = {}, {}, {}
    for node in blitz2glb.read_scene(key, 0):
        root = sheet_root(node)
        if root is None:
            continue
        for group_id, material, _lod in node['batches']:
            sheet = Path((inherited(mat, material, 'textures') or {}).get('albedo', '')).stem
            groups.setdefault(group_id, set()).add((root, sheet))
            if sheet:
                sheets.setdefault(root, set()).add(sheet)
                if node['name'] == root:
                    own.setdefault(root, set()).add(sheet)
    extras = {}
    for root, found in sheets.items():
        # The runtime binds the part object to the primary sheet of the part
        # node itself (`client_albedos`) and the hull to the body sheet. A
        # hull drawn on another sheet altogether (M4A3E8 Thunderbolt VII on
        # `M4A3E8TU`) keeps the material it has today.
        mine = own.get(root, set())
        if not mine or (root == 'hull' and body in mine):
            primary = body
        else:
            primary = primary_sheet(mine)
        extra = sorted(s for s in found if s != primary
                       and 'laser' not in s.lower() and 'glass' not in s.lower())
        if extra:
            extras[root] = extra
    _SHEET['scenes'][key] = {'groups': groups, 'extras': extras}
    return _SHEET['scenes'][key]


def install_sheets():
    inner_primitive = blitz2glb.MeshMaker.primitive
    def primitive(self, group_id, matrix):
        # Only the visual maker; collision scenes number their groups apart.
        if self.require_uv and _SHEET['path'] is not None:
            scene = scene_sheets(_SHEET['path'])
            entries = scene['groups'].get(group_id, ())
            if _SHEET['root'] is None:
                if any(sheet in scene['extras'].get(root, ()) for root, sheet in entries):
                    return None
            elif (_SHEET['root'], _SHEET['sheet']) not in entries:
                return None
        return inner_primitive(self, group_id, matrix)
    blitz2glb.MeshMaker.primitive = primitive
    inner_part = blitz2glb.pick_part
    def pick_part(nodes, *names):
        selected = inner_part(nodes, *names)
        if _ASSEMBLY['rotor']:
            return selected
        roots = {sheet_root({'name': name}) for name in names} - {None}
        for node in nodes:
            if node not in selected and OBVES.search(node['name']) and sheet_root(node) in roots:
                node['obves'] = True
                selected.append(node)
        return selected
    blitz2glb.pick_part = pick_part
    # Pivots stay those of the part itself: a kit bolted on the hull must not
    # move the validated hull pivot, nor a turret kit the turret ring.
    for name in ('nodes_bbox', 'rendered_nodes_bbox', 'nodes_core_bbox'):
        def bbox(groups, nodes, *args, _inner=getattr(blitz2glb, name), **kwargs):
            return _inner(groups, [n for n in nodes if not n.get('obves')], *args, **kwargs)
        setattr(blitz2glb, name, bbox)
    def sheet_extras(card_id, file_name, parts, turret_slot, gun_slot, rebuild, root):
        extras = scene_sheets(scene_path(card_id, file_name))['extras'].get(root, ())
        cache = exporter.GLB_CACHE
        out = []
        try:
            for index, sheet in enumerate(extras, 1):
                exporter.GLB_CACHE = cache / 'sheet' / f'{root}_sheet{index}'
                _SHEET['root'], _SHEET['sheet'] = root, sheet
                glb = exporter.convert(card_id, file_name, parts, turret_slot, gun_slot, rebuild)
                out.append((glb, index, sheet))
        finally:
            _SHEET['root'] = _SHEET['sheet'] = None
            exporter.GLB_CACHE = cache
        return out
    exporter.SHEET_EXTRAS = sheet_extras


def setup_exporter():
    # Old scenes inline SCG; modern scenes use a separate lossless sidecar.
    original_convert = exporter.convert
    def convert(card_id, file_name, parts, turret_slot, gun_slot, rebuild):
        _ASSEMBLY['card'] = card_id
        _ASSEMBLY['file'] = file_name
        _SHEET['path'] = scene_path(card_id, file_name)
        try:
            return original_convert(card_id, file_name, parts, turret_slot, gun_slot, rebuild)
        finally:
            _ASSEMBLY['card'] = None
            _ASSEMBLY['file'] = None
            _SHEET['path'] = None
    exporter.convert = convert
    def gun_extras(card_id, file_name, parts, turret_slot, gun_slot, rebuild):
        spec = GUN_ASSEMBLIES.get(card_id, {}).get(gun_slot)
        if not spec or not spec.get('rotor'):
            return []
        objects = assembly_objects(card_id, file_name, gun_slot)
        if not objects:
            objects = {gun_slot + '_rotor': (spec['rotor'][0], None)}
        cache = exporter.GLB_CACHE
        out = []
        try:
            for name, (entity, joint) in sorted(objects.items()):
                exporter.GLB_CACHE = cache / 'rotor' / name
                _ASSEMBLY['rotor'] = (entity,)
                _ASSEMBLY['joints'] = None if joint is None else (joint,)
                out.append((convert(card_id, file_name, parts, turret_slot, gun_slot, rebuild), name))
        finally:
            _ASSEMBLY['rotor'] = None
            _ASSEMBLY['joints'] = None
            exporter.GLB_CACHE = cache
        return out
    exporter.GUN_EXTRAS = gun_extras
    exporter.GUN_ANIMATIONS = assembly_animations
    original_primitive = blitz2glb.MeshMaker.primitive
    def primitive(self, group_id, matrix):
        keep = assembly_filter(group_id)
        group = self.groups.get(group_id)
        if keep is None or group is None:
            return original_primitive(self, group_id, matrix)
        if not keep:
            return None
        import blitz_1120_joints
        skeletons = assembly_skeletons(_ASSEMBLY['file'])
        names = next((s['groups'][group_id] for s in skeletons.values() if group_id in s['groups']), None)
        if names is None:
            return original_primitive(self, group_id, matrix)
        streams = dava.decode_vertices(group)
        indices = dava.decode_indices(group)
        joints = blitz_1120_joints.triangle_joints(group, names)
        kept = [indices[3 * i + k] for i, joint in enumerate(joints) if joint in keep for k in range(3)]
        if not kept:
            return None
        used = sorted(set(kept))
        remap = {old: new for new, old in enumerate(used)}
        filtered = {key: [values[i] for i in used] for key, values in streams.items()
                    if isinstance(values, list) and len(values) == len(streams['position'])}
        filtered_indices = [remap[i] for i in kept]
        saved = (dava.decode_vertices, dava.decode_indices)
        dava.decode_vertices = lambda g: filtered if g is group else saved[0](g)
        dava.decode_indices = lambda g: filtered_indices if g is group else saved[1](g)
        try:
            return original_primitive(self, group_id, matrix)
        finally:
            dava.decode_vertices, dava.decode_indices = saved
    blitz2glb.MeshMaker.primitive = primitive
    original_decode_texture = exporter.decode_texture
    def decode_texture(source, destination, bleed=False):
        # 11.20 sheets keep their decoded source alpha and every drawn texel.
        # Legacy paint dilation would also repaint opaque near-white pixels, so
        # track sheets only get their clipped texels filled (see bleed_clipped).
        result = original_decode_texture(source, destination, bleed=False)
        if bleed:
            bleed_clipped(Path(destination))
            # Provenance describes the final shipped PNG, not the intermediate
            # decode before alpha-edge paint dilation.
            result['bytes'] = Path(destination).stat().st_size
            result['sha256'] = digest(Path(destination))
        return result
    exporter.decode_texture = decode_texture
    original_read_groups = blitz2glb.read_groups
    def read_groups(path):
        p = Path(path)
        return original_read_groups(str(p.with_suffix('.scg')) if p.with_suffix('.scg').exists() else path)
    blitz2glb.read_groups = read_groups
    original_pick = blitz2glb.pick
    def pick(nodes,*names):
        selected = original_pick(nodes,*names)
        # New French models split one track into two independently authored
        # strips. Both are the same moving side, not lower-detail substitutes.
        for node in nodes:
            if node in selected:
                continue
            if any(name in ('chassis_track_L','chassis_track_R') and re.fullmatch(re.escape(name)+r'_\d+',node['name']) for name in names):
                selected.append(node)
        return selected
    blitz2glb.pick = pick
    original_part = blitz2glb.pick_part
    def pick_part(nodes,*names):
        selected = original_part(nodes,*names)
        for node in nodes:
            if node not in selected and any(node['name']==name+'_cap' for name in names):
                selected.append(node)
        spec = GUN_ASSEMBLIES.get(_ASSEMBLY['card'] or '', {})
        slot = next((s for s in spec if s in names), None)
        if slot is not None:
            if _ASSEMBLY['rotor']:
                selected = [n for n in nodes if n['name'] in _ASSEMBLY['rotor']]
            else:
                selected += [n for n in nodes if n['name'] in spec[slot].get('children', ())
                             and n not in selected]
            if not selected:
                raise RuntimeError(f"{_ASSEMBLY['card']}: {slot} assembly matched no node")
        if not _ASSEMBLY['rotor']:
            # Decor below the part node (see DECOR_FITTINGS); never the
            # skinned assembly parts exported on their own.
            assembled = {e for gun in spec.values() for e in gun.get('children', ()) + gun.get('rotor', ())}
            selected += [n for n in nodes if n.get('decor_of') in names
                         and n['name'] not in assembled and n not in selected]
        return selected
    blitz2glb.pick_part = pick_part
    original_scene = blitz2glb.read_scene
    def read_scene(path,lod):
        nodes = original_scene(path,lod)
        mat = materials(Path(path))
        owners = decor_owners(path, mat)
        if len(owners) == len(nodes):
            for node, owner in zip(nodes, owners):
                if owner:
                    node['decor_of'] = owner
        for node in nodes:
            # Shadow-only batches are rendering helpers, not opaque tank
            # geometry. Exporting them adds overlapping black wheel cards.
            # Hardpoints (`HP_gunFire_01` ...) only mark a position: VK 30.02
            # (D) BP binds its muzzle marker to a shadow material, and
            # stripping it lost the muzzle, so shells left from the trunnion.
            if node['name'].startswith('HP_'):
                continue
            node['batches'] = [b for b in node['batches'] if not is_shadow(mat,b[1])]
        return nodes
    blitz2glb.read_scene = read_scene
    install_sheets()
    def albedos(stem):
        path = exporter.TANKS / exporter.MODEL_DIR / (stem+'.sc2')
        mat = materials(path)
        result = {}
        for node in read_scene(str(path),0):
            sheets = {Path((inherited(mat,b[1],'textures') or {}).get('albedo','')).stem for b in node['batches']}
            sheets.discard('')
            if len(sheets)>1:
                primary = primary_sheet(sheets)
                result[node['name']] = primary
                print(f"  {stem} {node['name']}: (using primary albedo {primary} from {sorted(sheets)})", flush=True)
                continue
            if sheets:
                result[node['name']] = next(iter(sheets))
        return result
    exporter.client_albedos = albedos
    def resolve(name):
        p = exporter.IMAGES / (name+'.astc.pvr')
        if not p.exists():
            alt = p.with_name(p.name + '.dvpl')
            if alt.exists():
                p = alt
            else:
                matches = list(exporter.TANKS.glob('*/images/'+name+'.astc.pvr'))
                matches += list(exporter.TANKS.glob('*/images/'+name+'.astc.pvr.dvpl'))
                matches += list((SOURCE / '3d/Tanks').glob('*/images/'+name+'.astc.pvr'))
                matches += list((SOURCE / '3d/Tanks').glob('*/images/'+name+'.astc.pvr.dvpl'))
                matches = sorted(dict.fromkeys(matches))
                local = [m for m in matches if str(m).startswith(str(exporter.TANKS))]
                if len(local) == 1:
                    p = local[0]
                elif len(matches) == 1:
                    p = matches[0]
                else:
                    raise RuntimeError(f'Unresolved albedo {name}: {matches}')
        # Keep the stable exporter naming; the bytes remain ASTC and the decoder
        # dispatches by the header, not by this compatibility extension.
        target_dir = p.parent if str(p).startswith(str(exporter.TANKS)) else exporter.IMAGES
        target = target_dir / (name+'.mali.pvr')
        if not target.exists():
            payload = p.read_bytes()
            if p.suffix == '.dvpl':
                payload = dvpl.unpack_bytes(payload)
            target.write_bytes(payload)
        return target
    exporter.resolve_client_texture = resolve
    import blitz_1120_collision
    blitz_1120_collision.install(exporter)


def export_models(records,cards,rebuild=False):
    setup_exporter()
    manifest_path = DATA / 'tank_models_source_manifest.json'
    manifest = json.loads(manifest_path.read_text())
    manifest['policy'] = 'Original 4.8.0 and additive 11.20 client LOD0 geometry; no decimation.'
    by_name = {c['name']:c for c in cards}
    for i,r in enumerate(records,1):
        print(f'[{i}/{len(records)}] {r["name"]}',flush=True)
        configure(r)
        result = exporter.port_card(by_name[r['name']],r['vehicle_id'],rebuild,True)
        # Persist the source import policy alongside the new assets only:
        # meshes uncompressed, textures tagged with their client block.
        model_dir = ROOT / 'Assets/Blitz/Resources/Models/Tanks' / exporter.unity_id(r['name'])
        tags = {texture['destination']: source_tag(texture) for texture in result.get('textures', [])}
        for meta in list(model_dir.glob('*.obj.meta')) + list((model_dir/'Textures').glob('*.png.meta')):
            value = meta.read_text()
            tag = tags.get(meta.name.removesuffix('.meta'), SOURCE_TAG)
            value = re.sub(r'(?m)^  userData:.*$', '  userData: ' + tag, value)
            if meta.name.endswith('.obj.meta'):
                value = re.sub(r'(?m)^    meshCompression:.*$', '    meshCompression: 0', value)
            meta.write_text(value)
        import blitz_1120_collision
        armor_path = DATA / (exporter.unity_id(r['name']).lower() + '_armor.json')
        root = ET.parse(exporter.VEHICLES / (r['vehicle_id']+'.xml')).getroot()
        save(armor_path,blitz_1120_collision.annotate(json.loads(armor_path.read_text()),root))
        result['source_version'] = '11.20'
        result['original_pack'] = str(SOURCE)
        manifest['tanks'][r['name']] = result
        save(manifest_path,manifest)


def export_icons(records):
    import io
    from PIL import Image
    atlas_path = DATA / 'atlas_regions.json'
    atlas = json.loads(atlas_path.read_text())
    root = ROOT / 'Assets/Blitz/Resources/Textures'
    evidence = []
    for r in records:
        icon_id = r['vehicle_id'].replace('S04_Lago-I','S04_Lago_I')
        relative = f"Gfx/UI/BigTankIcons/{r['nation']}-{icon_id}@2x.packed.webp"
        src = source_file(relative)
        payload = dvpl.unpack_bytes(src.read_bytes()) if src.suffix=='.dvpl' else src.read_bytes()
        image = Image.open(io.BytesIO(payload)).convert('RGBA')
        bbox = image.getbbox()
        if bbox is None:
            raise RuntimeError(f'Empty source icon {relative}')
        image = image.crop(bbox)
        rid = exporter.unity_id(r['name']).lower()
        dst = root / 'TankIcons' / (rid+'.png')
        dst.parent.mkdir(parents=True,exist_ok=True)
        image.save(dst)
        texture = 'TankIcons/'+rid
        atlas['textures'][texture] = {'width':image.width,'height':image.height}
        atlas['regions']['tank_icon_'+rid] = {'texture':texture,'x':0,'y':0,'w':image.width,'h':image.height}
        evidence.append({'name':r['name'],'source':str(src.relative_to(SOURCE)),'sha256':digest(src),'crop':bbox})
    for region,stem in [('hangar_nation9','flag_tank-node_european'),('tank_nation_9','flag_carousel_european')]:
        src = source_file(f'Gfx/Lobby/flags/{stem}.packed.webp')
        payload = dvpl.unpack_bytes(src.read_bytes()) if src.suffix=='.dvpl' else src.read_bytes()
        image = Image.open(io.BytesIO(payload)).convert('RGBA')
        image = image.crop(image.getbbox())
        image.save(root / (region+'.png'))
        atlas['textures'][region]={'width':image.width,'height':image.height}
        atlas['regions'][region]={'texture':region,'x':0,'y':0,'w':image.width,'h':image.height}
    save(atlas_path,atlas)
    save(DATA/'blitz_1120_icons.json',{'version':'11.20','policy':'source pixels, alpha-bounds crop, no resampling','icons':evidence})


from blitz_1120_registry import merge_into


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--prepare',action='store_true')
    ap.add_argument('--models',action='store_true')
    ap.add_argument('--merge',action='store_true')
    ap.add_argument('--icons',action='store_true')
    ap.add_argument('--rebuild',action='store_true')
    args = ap.parse_args()
    if args.prepare:
        records = prepare()
        cards = build_cards(records)
    else:
        records = json.loads(INVENTORY.read_text())['records']
        cards = json.loads(CARD_FILE.read_text())['tanks']
    if args.models:
        export_models(records,cards,args.rebuild)
    if args.icons:
        export_icons(records)
    if args.merge:
        document = merge_into(json.loads((DATA/'tanks.json').read_text()))
        (DATA/'tanks.json').write_text(json.dumps(document,ensure_ascii=False,indent=1,sort_keys=True)+'\n',encoding='utf-8')
        print('live cards',len(document['tanks']))

if __name__=='__main__':
    main()
