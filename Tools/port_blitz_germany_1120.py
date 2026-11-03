#!/usr/bin/env python3
"""Complete additive German 11.20 roster; preserve the existing Unity cards.

Authored Parameters YAML supplies scene/icon aliases. All source XML modules,
research edges and XP are retained. Only the new cards receive combat caps;
existing research connections are a sidecar, not edits to old module graphs.
"""
from __future__ import annotations
import argparse
import copy
import hashlib
import io
import json
import statistics
from pathlib import Path
from xml.etree import ElementTree as ET

import yaml
from PIL import Image
import port_blitz_1120 as port
import port_blitz_480_data as base
import tech_tree_layout
from blitz_germany_1120_registry import card_hash, merge_into

ROOT, DATA, SOURCE, PACK = port.ROOT, port.DATA, port.SOURCE, port.PACK
XML = SOURCE / 'from_apk/XML/item_defs/vehicles/germany'
PARAMETERS = SOURCE / 'from_apk/3d/Tanks/Parameters/germany'
INVENTORY = DATA / 'blitz_germany_1120_inventory.json'
CARDS = DATA / 'blitz_germany_1120_cards.json'
BALANCE = DATA / 'blitz_germany_1120_balance.json'
PRESERVATION = DATA / 'blitz_germany_1120_preservation.json'
GROUPS = ('turrets', 'guns', 'tracks', 'engines', 'radios')
KIND = {'turrets': 'turret', 'guns': 'gun', 'tracks': 'chassis',
        'engines': 'engine', 'radios': 'radio'}
# Fallback rows; researchable cards take their row from tech_tree_layout.
LAYOUT = {'VK7201': -1, 'G115_VK10001P': 5, 'G110_Mauschen': 5,
          'G126_HWK_12': 4, 'G125_Spz_57_Rh': 4}
MANUAL_EXCLUSIONS = {
    'PzVI_AusfTU': 'user-removed duplicate collectible Tiger I',
    'PzVI_AusfTUR': 'user-removed duplicate collectible Tiger I',
}


def save(path, value):
    port.save(path, value)


def parameters(vehicle):
    path = PARAMETERS / (vehicle + '.yaml')
    document = yaml.safe_load(path.read_text(encoding='utf-8-sig'))
    resources = document['resourcesPath']
    for key in ('blitzModelPath', 'collisionMesh', 'smallIconPath', 'bigIconPath'):
        if not resources.get(key):
            raise ValueError(f'{vehicle}: missing authoritative {key}')
    return path, resources


def baseline():
    live = json.loads((DATA / 'tanks.json').read_text())['tanks']
    old = [c for c in live if c.get('source_overlay') not in ('germany_1120', 'usa_1120', 'other_1120',
                                                         'japan_1120', 'branches_1120', 'ussr_1120',
                                                         'mark_i_male_1120')]
    if PRESERVATION.exists():
        expected = json.loads(PRESERVATION.read_text())['cards']
        actual = {c['name']: card_hash(c) for c in old}
        if actual != expected:
            changed = sorted(n for n in expected if actual.get(n) != expected[n])
            raise RuntimeError('Protected baseline drift: ' + ', '.join(changed))
    elif len(old) != 357:
        raise RuntimeError(f'Expected 357 existing cards; got {len(old)}')
    return old


def discover(old):
    listing = {x.tag: x for x in ET.parse(XML / 'list.xml').getroot()}
    known = {Path(c['source']).stem for c in old if c['nation'] == 2}
    ids, excluded = [], []
    for vehicle_id in listing:
        if vehicle_id in known:
            continue
        if '_tutorial_bot' in vehicle_id or vehicle_id in MANUAL_EXCLUSIONS:
            excluded.append(vehicle_id)
        else:
            ids.append(vehicle_id)
    if len(listing) != 122 or len(known) != 77 or len(ids) != 39 or len(excluded) != 6:
        raise RuntimeError('Source roster changed; re-audit the source inventory')
    return ids, excluded, listing


def configure_importer(ids):
    port.BRANCHES = {'germany': ids}
    port.MODEL_NATIONS['germany'] = 'German'
    port.NAMES = {v: v.lower() for v in ids}
    port.INVENTORY = INVENTORY
    # Do not infer scene aliases from the vehicle id: Tiger 131/Skorpion and
    # several others have different authored names and some scenes are in APK.
    port.model_stem = lambda nation, vehicle: Path(
        parameters(vehicle)[1]['blitzModelPath']).stem


def prepare(old, ids, excluded):
    configure_importer(ids)
    records = port.prepare()
    for record in records:
        v = record['vehicle_id']
        path, resources = parameters(v)
        import blitz_germany_1120_models
        blitz_germany_1120_models.prepare_source_pivots(port, record, path)
        record.update(parameter_source=str(path.relative_to(SOURCE)),
                      parameter_sha256=port.digest(path), resources=resources)
        for label, relative in [('scene', record['scene']),
                                ('geometry', record['scene'].removesuffix('.sc2') + '.scg'),
                                ('collision', record['collision']),
                                ('collision_geometry', record['collision'].removesuffix('.sc2') + '.scg')]:
            original = port.source_file(relative)
            record[label + '_source'] = str(original.relative_to(SOURCE))
            record[label + '_sha256'] = port.digest(original)
        root = ET.parse(XML / (v + '.xml')).getroot()
        record['modules'] = {
            'turrets': [x.tag for x in root.find('turrets0')],
            'guns': list(dict.fromkeys(x.tag for x in root.findall('turrets0/*/guns/*'))),
            'tracks': [x.tag for x in root.find('chassis')],
            'engines': [x.tag for x in root.find('engines')],
            'radios': [x.tag for x in root.find('radios')],
        }
        record['configurations'] = [
            {'turret': t.tag, 'gun': g.tag, 'turret_mesh': port.slot(t),
             'gun_mesh': port.slot(g)}
            for t in root.find('turrets0') for g in t.find('guns')]
    save(INVENTORY, {
        'version': '11.20', 'source': str(SOURCE),
        'generator': 'Tools/port_blitz_germany_1120.py',
        'listed': 122, 'preserved': 77, 'added': 39,
        'excluded': [{'vehicle_id': v,
                      'reason': MANUAL_EXCLUSIONS.get(v, 'service-only tutorial bot'),
                      'xml_sha256': port.digest(XML / (v + '.xml'))} for v in excluded],
        'records': records,
        'totals': {k: sum(len(r['modules'][k]) for r in records) for k in GROUPS},
        'configurations': sum(len(r['configurations']) for r in records),
    })
    if not PRESERVATION.exists():
        models = json.loads((DATA / 'tank_models_source_manifest.json').read_text())['tanks']
        save(PRESERVATION, {
            'policy': 'All 357 pre-existing cards unchanged; additional links are outside their records',
            'cards': {c['name']: card_hash(c) for c in old},
            'model_entries': {name: card_hash(value) for name, value in models.items()},
            'shot_bank_sha256': port.digest(DATA / 'tank_sounds.json'),
            'legacy_count': len(old), 'legacy_german_count': 77,
        })
    return records


def vehicle_edges(client, vehicle):
    root = ET.parse(XML / (vehicle + '.xml')).getroot()
    groups = [('chassis', 'chassis'), ('engines', 'engines'),
              ('radios', 'radios'), ('turrets0', 'turrets')]
    for group, shared_kind in groups:
        parent = root.find(group)
        for node in parent if parent is not None else []:
            resolved = client.merge(shared_kind, node)
            for key, cost in base.unlock_map(resolved).items():
                if key.startswith('vehicle:'):
                    yield {'from_id': vehicle, 'to_id': key.split(':', 1)[1],
                           'cost_xp': cost, 'required_kind':
                           {'chassis': 'chassis', 'engines': 'engine', 'radios': 'radio', 'turrets': 'turret'}[shared_kind],
                           'required_module': node.tag}
            if group == 'turrets0':
                for gun in node.find('guns'):
                    for key, cost in base.unlock_map(client.merge('guns', gun)).items():
                        if key.startswith('vehicle:'):
                            yield {'from_id': vehicle, 'to_id': key.split(':', 1)[1],
                                   'cost_xp': cost, 'required_kind': 'gun',
                                   'required_module': gun.tag, 'required_turret': node.tag}


def median(values, fallback):
    positive = [float(v) for v in values if float(v) > 0]
    return statistics.median(positive) if positive else float(fallback)


def shots(gun):
    return sum(gun.get('burst_info', {}).get('bursts', [])) or 1


def firing_gap(gun):
    burst = gun.get('burst_info', {})
    groups = burst.get('bursts', []) or [1]
    return max(0, len(groups) - 1) * burst.get('intraclip', 0) + sum(
        max(0, n - 1) for n in groups) * burst.get('intraburst', 0)


def dpm(gun, reload=None):
    if not gun['shells']:
        raise ValueError(f"{gun['name']}: no ammunition")
    if gun.get('autoreload_times'):
        # Firing the replenished first available shell is the fastest sustained
        # loop. Never divide the magazine by the sum of per-shell timers.
        return 60 * gun['shells'][0]['dmg'] / max(gun['autoreload_times'][0], firing_gap(gun), .01)
    return 60 * gun['shells'][0]['dmg'] * shots(gun) / max(
        (gun['reload'] if reload is None else reload) + firing_gap(gun), .01)


def mounted_guns(card):
    if not card['turrets']:
        return card['guns']
    result = []
    for slot in card['turrets'][-1]['guns']:
        gun = copy.deepcopy(next(x for x in card['guns'] if x['name'] == slot['name']))
        gun['reload'] = slot['reload']
        result.append(gun)
    return result


def top_gun(card):
    # The authored final module is the top mount; preserve alternatives in the
    # peer pool only when comparing that caliber, not as extra duplicate votes.
    return mounted_guns(card)[-1]


def total_health(card):
    return card['health'] + card['turrets'][-1]['health']


def mass(card, engine):
    return max(1., card['hull_weight'] + card['turrets'][-1]['weight']
               + top_gun(card)['weight'] + card['tracks'][-1]['weight']
               + engine['weight'] + card['radios'][-1]['weight'])


def metrics(card):
    return {'total_health': total_health(card),
            'max_sustained_dpm': max(dpm(g) for g in mounted_guns(card)),
            'speed_forward': card['speed_limits']['x'],
            'speed_backward': card['speed_limits']['y'],
            'power_per_ton': max(e['power'] / mass(card, e) * 1000 for e in card['engines']),
            'view_range': max(t['spot_radius'] for t in card['turrets'])}


def peers_for(card, old):
    peers = [c for c in old if c['lvl'] == card['lvl']
             and c['tank_class'] == card['tank_class'] and not c.get('prem')]
    if not peers:
        peers = [c for c in old if c['lvl'] == card['lvl'] and not c.get('prem')
                 and (card['tank_class'] != 0 or c['tank_class'] in (0, 1))]
    if not peers:
        raise ValueError(f"No tier {card['lvl']} peers for {card['name']}")
    return peers


def rebalance(card, peers):
    before = copy.deepcopy(card)
    peer_hp = median([total_health(p) for p in peers], total_health(card))
    hp_cap = peer_hp * 1.10
    health_factor = min(1., hp_cap / max(1, total_health(card)))
    card['health'] = round(card['health'] * health_factor)
    for turret in card['turrets']:
        turret['health'] = round(turret['health'] * health_factor)
    # Make the rounded total obey the actual cap, not just the pre-round value.
    card['health'] = min(card['health'], int(hp_cap) - int(card['turrets'][-1]['health']))
    for axis in ('x', 'y'):
        cap = median([p['speed_limits'][axis] for p in peers], card['speed_limits'][axis]) * 1.20
        card['speed_limits'][axis] = round(min(card['speed_limits'][axis], cap), 6)
    for group, fields in [('turrets', ('rot_speed', 'spot_radius')),
                          ('tracks', ('rot_speed',)), ('radios', ('distance',))]:
        for module in card[group]:
            for field in fields:
                upper = 1.10 if field == 'spot_radius' else 1.20
                reference = median([p[group][-1][field] for p in peers], module[field])
                module[field] = round(min(module[field], reference * upper), 6)
    for track in card['tracks']:
        for field in ('disp_move_mul', 'disp_rotation_mul'):
            reference = median([p['tracks'][-1][field] for p in peers], track[field])
            track[field] = round(max(track[field], reference * .85), 6)
    density_cap = median([p['engines'][-1]['power'] / mass(p, p['engines'][-1]) for p in peers],
                         card['engines'][-1]['power'] / mass(card, card['engines'][-1])) * 1.15
    for engine in card['engines']:
        engine['power'] = min(engine['power'], int(density_cap * mass(card, engine)))
    peer_dpm = median([dpm(top_gun(p)) for p in peers], dpm(top_gun(card)))
    # HP and sustained output cannot both sit at their individual upper cap.
    output_cap = peer_dpm * min(1.10, 1.15 / max(.01, total_health(card) / peer_hp))
    pool = [(p, top_gun(p)) for p in peers]
    gun_evidence = []
    for gun in card['guns']:
        similar = [(p, g) for p, g in pool
                   if abs(g['caliber'] - gun['caliber']) <= max(15, gun['caliber'] * .25)]
        if not similar:
            similar = pool
        for field in ('disp', 'aim_time'):
            reference = median([g[field] for _, g in similar], gun[field])
            gun[field] = round(max(gun[field], reference * .85), 6)
        for shell in gun['shells']:
            same = [s for _, g in similar for s in g['shells'] if s['type'] == shell['type']]
            for field in ('dmg', 'pen'):
                reference = median([s[field] for s in same], shell[field])
                shell[field] = min(shell[field], int(reference * 1.15))
            if shell['speed'] <= 0 or shell['pen'] <= 0:
                raise ValueError(f"{card['name']}/{gun['name']}: invalid inherited ballistics")
        factor = 1.
        for turret in card['turrets']:
            for slot in turret['guns']:
                if slot['name'] != gun['name']:
                    continue
                required_reload = 60 * gun['shells'][0]['dmg'] * shots(gun) / output_cap - firing_gap(gun)
                factor = max(factor, required_reload / max(.01, slot['reload']))
        gun['reload'] = round(gun['reload'] * factor, 6)
        if gun.get('autoreload_times'):
            gun['autoreload_times'] = [round(v * factor, 6) for v in gun['autoreload_times']]
        for turret in card['turrets']:
            for slot in turret['guns']:
                if slot['name'] == gun['name']:
                    slot['reload'] = round(slot['reload'] * factor, 6)
        gun_evidence.append({'gun': gun['name'], 'competitors': [p['name'] for p, _ in similar],
                             'reload_factor': factor, 'dpm_cap': output_cap})
    card['turret_health'] = card['turrets'][-1]['health']
    card['turret_rot_speed'] = card['turrets'][-1]['rot_speed']
    card['spot_radius'] = card['turrets'][-1]['spot_radius']
    return {'name': card['name'], 'level': card['lvl'], 'class': card['tank_class'],
            'competitors': [p['name'] for p in peers],
            'peer_medians': {'total_health': peer_hp, 'sustained_dpm': peer_dpm,
                             'power_per_ton': density_cap / 1.15 * 1000},
            'caps': {'total_health': hp_cap, 'sustained_dpm': output_cap,
                     'power_per_ton': density_cap * 1000, 'hp_dpm_product_ratio': 1.15},
            'gun_evidence': gun_evidence,
            'original_metrics': metrics(before), 'balanced_metrics': metrics(card),
            'original': before, 'balanced': copy.deepcopy(card)}


def build_cards(old, records, listing):
    base.PACK, base.VEHICLES = PACK, PACK / 'XML/item_defs/vehicles'
    # Fill only missing Russian keys from the same client's English localization;
    # do not invent localized names for regional/test collectible variants.
    english = yaml.safe_load((SOURCE / 'from_apk/Strings/en.yaml').read_text())
    russian = yaml.safe_load((SOURCE / 'from_apk/Strings/ru.yaml').read_text())
    strings = dict(english); strings.update(russian)
    client = port.NationClient1120('germany', strings)
    names = {Path(c['source']).stem: c['name'] for c in old if c['nation'] == 2}
    names.update({r['vehicle_id']: r['name'] for r in records})
    source_edges = [e for v in listing for e in vehicle_edges(client, v)]
    incoming = {}
    for edge in source_edges:
        incoming.setdefault(edge['to_id'], []).append(edge['cost_xp'])
    cards, balances = [], []
    by_id = {r['vehicle_id']: r for r in records}
    tree = yaml.safe_load((SOURCE / 'from_apk/Configs/TechTree/germany_tree.yaml').read_text())
    occupied = {(c['lvl'], c['tree_branch']) for c in old if c['nation'] == 2 and c['tree_tank']}
    for record in records:
        v = record['vehicle_id']
        card = base.build_card(client, {'id': v}, names)
        root = ET.parse(PACK / 'XML/item_defs/vehicles/germany' / (v + '.xml')).getroot()
        if card['display_name'] == v:
            short = strings.get(listing[v].findtext('shortUserString', ''))
            if short:
                card['display_name'] = short
                card['display_name_policy'] = 'authored short name; full localization key absent'
            else:
                explicit = {'Waffentrager_IV_CL': 'WT Ritter'}.get(v)
                if explicit:
                    card['display_name'] = card['display_name_short'] = explicit
                    card['display_name_policy'] = 'player-facing alias override; official localized title absent in 11.20 source'
                else:
                    donor = {'PzVI_AusfTUR': ('pzvi', 'TUR')}.get(v)
                    if donor:
                        original = next(c for c in old if c['name'] == donor[0])
                        label = original['display_name'] + ' ' + donor[1]
                        card['display_name'] = card['display_name_short'] = label
                        card['display_name_policy'] = 'preserved donor title + authored variant suffix; no official localized title in source'
        tags = listing[v].findtext('tags', '').split()
        collector = 'collectible' in tags
        premium = listing[v].find('price/gold') is not None
        researched = v in tree['tanks'] and not premium and not collector
        card.update(source_version='11.20', source_overlay='germany_1120',
                    source=record['xml_source'], source_sha256=record['xml_sha256'],
                    acquisition='collectible' if collector else ('premium' if premium else ''),
                    prem=not researched, collector=collector, shop_tank=False, tree_tank=researched,
                    cost_gold=base.integer(listing[v].find('price')) if premium else 0,
                    collector_sell_gold=base.integer(listing[v].find('sellPrice')) if collector else 0,
                    tree_branch=tech_tree_layout.branch(tech_tree_layout.NATION_GERMANY, card['name'], LAYOUT.get(v, 0))
                    if researched else LAYOUT.get(v, 0))
        if not researched:
            card.update(cost=0, cost_xp=0, unlock_tanks={})
            for group in GROUPS:
                for module in card[group]:
                    module.update(cost_xp=0, unlocks={})
            for turret in card['turrets']:
                for slot in turret['guns']:
                    slot['unlocks'] = {}
        else:
            prices = incoming.get(v, [])
            if not prices or min(prices) <= 0:
                raise ValueError(f'{v}: missing source incoming research XP')
            card['cost_xp'] = min(prices)
            if (card['lvl'], card['tree_branch']) in occupied:
                raise ValueError(f'{v}: hybrid tree overlaps a preserved card')
            occupied.add((card['lvl'], card['tree_branch']))
        for track, xml in zip(card['tracks'], root.find('chassis')):
            armor = port.exporter.armor_values(client.merge('chassis', xml))
            if armor.get('leftTrack') != armor.get('rightTrack'):
                raise ValueError(f'{v}: asymmetric chassis armor needs side-specific runtime')
            track['armor'] = armor.get('leftTrack', 0)
        for turret, xml in zip(card['turrets'], root.find('turrets0')):
            turret['mesh'] = port.slot(xml)
            for slot, gun_xml in zip(turret['guns'], xml.find('guns')):
                slot['mesh'] = port.slot(gun_xml)
                resolved = client.merge('guns', gun_xml)
                gun = next(g for g in card['guns'] if g['name'] == gun_xml.tag)
                gun['max_regen_health'] = base.number(resolved.find('maxRegenHealth'))
                gun['repair_cost'] = base.number(resolved.find('repairCost'), 1.0)
                timers = base.vector(resolved.find('pumpGunReloadTimes'), ())
                if timers and base.local_text(resolved, 'pumpGunMode') == 'true':
                    gun = next(g for g in card['guns'] if g['name'] == gun_xml.tag)
                    gun.update(autoreload_times=timers, reload=sum(timers))
        for radio, xml in zip(card['radios'], root.find('radios')):
            resolved = client.merge('radios', xml)
            radio['max_regen_health'] = base.number(resolved.find('maxRegenHealth'))
            radio['repair_cost'] = base.number(resolved.find('repairCost'), 1.0)
        balances.append(rebalance(card, peers_for(card, old)))
        cards.append(card)
    links = []
    for edge in source_edges:
        if edge['to_id'] not in by_id or edge['from_id'] in by_id:
            continue
        if edge['from_id'] not in names:
            raise ValueError(f'Unknown predecessor: {edge}')
        link = {k: v for k, v in edge.items() if k not in ('from_id', 'to_id')}
        link.update({'from': names[edge['from_id']], 'to': names[edge['to_id']],
                     'preserve_legacy_elite': True,
                     'source': str((XML / (edge['from_id'] + '.xml')).relative_to(SOURCE)),
                     'source_sha256': port.digest(XML / (edge['from_id'] + '.xml'))})
        links.append(link)
    if len(links) != 3 or sum(c['tree_tank'] for c in cards) != 5:
        raise ValueError('Incomplete German research branch attachments')
    save(CARDS, {'version': '11.20', 'generator': 'Tools/port_blitz_germany_1120.py',
                 'policy': 'every source module/configuration and authored XP; new combat scalars capped against preserved same-tier/class peers',
                 'additional_research_links': links, 'tanks': cards})
    save(BALANCE, {'baseline': '357 preserved Unity cards; non-premium same-tier/class competitors',
                   'policy': 'upper-only performance caps; no buffs; correct clips/bursts and mounted reloads; original geometry/armor and module identities retained',
                   'limitations': 'Deterministic stat-budget calibration is not a live PvP playtest',
                   'tanks': balances})
    print('German cards:', len(cards), 'modules:', {k: sum(len(c[k]) for c in cards) for k in GROUPS},
          'configurations:', sum(len(t['guns']) for c in cards for t in c['turrets']), flush=True)
    return cards


def export_icons(records):
    atlas_path = DATA / 'atlas_regions.json'
    atlas = json.loads(atlas_path.read_text())
    output = ROOT / 'Assets/Blitz/Resources/Textures/TankIcons'
    evidence = []
    for record in records:
        relative = record['resources']['bigIconPath'].removeprefix('~res:/')
        try:
            original = port.source_file(relative + '@2x.packed.webp')
        except FileNotFoundError:
            original = port.source_file(relative + '.packed.webp')
        payload = original.read_bytes()
        if original.suffix == '.dvpl':
            payload = port.dvpl.unpack_bytes(payload)
        image = Image.open(io.BytesIO(payload)).convert('RGBA')
        crop = image.getbbox()
        if not crop:
            raise ValueError(f"{record['name']}: empty authored big icon")
        image = image.crop(crop)
        rid = port.exporter.unity_id(record['name']).lower()
        destination = output / (rid + '.png')
        image.save(destination)
        texture = 'TankIcons/' + rid
        atlas['textures'][texture] = {'width': image.width, 'height': image.height}
        atlas['regions']['tank_icon_' + rid] = {
            'texture': texture, 'x': 0, 'y': 0, 'w': image.width, 'h': image.height}
        evidence.append({'name': record['name'], 'source': str(original.relative_to(SOURCE)),
                         'sha256': port.digest(original), 'crop': crop,
                         'output_sha256': port.digest(destination)})
    save(atlas_path, atlas)
    save(DATA / 'blitz_germany_1120_icons.json', {
        'version': '11.20', 'policy': 'authored Parameters icon paths; exact alpha crop; no resampling',
        'icons': evidence})


def export_crash(records):
    import dava
    path = DATA / 'legacy1120_crash_manifest.json'
    manifest = json.loads(path.read_text())
    def values(value):
        return value.values() if isinstance(value, dict) else value if isinstance(value, list) else []
    def nodes(hierarchy):
        for value in values(hierarchy):
            if isinstance(value, dict):
                yield value
                yield from nodes(value.get('#hierarchy', {}))
    for record in records:
        scene = PACK / record['scene']
        raw = dava.read_sc2(scene.read_bytes())['scene']
        names, fire = set(), {}
        for node in nodes(raw['#hierarchy']):
            name = node.get('name', ''); names.add(name)
            if name not in ('HP_Fire_01', 'HP_Fire_02'):
                continue
            render = next((c for c in values(node.get('components', {}))
                           if isinstance(c, dict) and c.get('comp.typename') == 'RenderComponent'), None)
            if render:
                box = next(iter(values(render['rc.renderObj']['ro.batches'])))['rb.aabbox']
                p = [(box[i] + box[i + 3]) * .5 for i in range(3)]
                fire[name] = [p[0], p[2], -p[1]]
        if not {'chassis_track_crash_L', 'chassis_track_crash_R'} <= names:
            raise ValueError(f"{record['name']}: no authored destroyed-track meshes")
        root = ET.parse(XML / (record['vehicle_id'] + '.xml')).getroot()
        manifest['vehicles'][record['name']] = {
            'firePoints': fire,
            'destroyedXmlPaths': [n.text.strip() for n in root.findall('.//models/destroyed') if n.text],
            'geometryPolicy': 'Original installed hull/turret/gun meshes; authored chassis_track_crash_L/R. No separate deformed hull/turret SC2 exists in this scene.',
            'sourceScene': record['scene_source'], 'crashTexture': manifest['textures']['albedo']}
        for key in ('scene', 'geometry', 'xml'):
            rel = record['xml_source'] if key == 'xml' else record[key + '_source']
            manifest['provenance'][rel] = port.digest(SOURCE / rel)
    save(path, manifest)
    print('Preserved/additive crash states:', len(manifest['vehicles']), flush=True)


def main():
    parser = argparse.ArgumentParser()
    for flag in ('prepare', 'models', 'icons', 'crash', 'merge', 'rebuild', 'resume'):
        parser.add_argument('--' + flag, action='store_true')
    parser.add_argument('--only', default='')
    args = parser.parse_args()
    old = baseline()
    ids, excluded, listing = discover(old)
    configure_importer(ids)
    if args.prepare:
        records = prepare(old, ids, excluded)
        cards = build_cards(old, records, listing)
    else:
        records = json.loads(INVENTORY.read_text())['records']
        cards = json.loads(CARDS.read_text())['tanks']
    if args.models:
        import blitz_germany_1120_models
        blitz_germany_1120_models.configure(port)
        selected = [r for r in records if not args.only or r['name'] in args.only.split(',')]
        pending = selected
        if args.resume:
            manifest = json.loads((DATA / 'tank_models_source_manifest.json').read_text())['tanks']
            pending = []
            for record in selected:
                rid = port.exporter.unity_id(record['name'])
                folder = ROOT / 'Assets/Blitz/Resources/Models/Tanks' / rid
                entry = manifest.get(record['name'], {})
                complete = entry.get('source_version') == '11.20' and entry.get('source') == record['scene']
                complete = complete and all((folder / (rid + suffix + '.obj')).exists()
                                            for suffix in ('', '_armor', '_phys'))
                complete = complete and (DATA / (rid.lower() + '_nodes.json')).exists()
                if not complete:
                    pending.append(record)
            print('Resume: preserved completed models', len(records) - len(pending), flush=True)
        port.export_models(pending, cards, args.rebuild)
        blitz_germany_1120_models.ensure_source_imports(port, records)
    if args.icons:
        export_icons(records)
    if args.crash:
        export_crash(records)
    if args.merge:
        document = tech_tree_layout.apply(merge_into(json.loads((DATA / 'tanks.json').read_text())))
        (DATA / 'tanks.json').write_text(json.dumps(document, ensure_ascii=False, indent=1, sort_keys=True) + '\n')
        print('Live cards:', len(document['tanks']), flush=True)


if __name__ == '__main__':
    main()
