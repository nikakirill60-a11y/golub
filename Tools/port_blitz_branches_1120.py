#!/usr/bin/env python3
"""Additive 11.20 port of two complete tech-tree branches.

* UK tank destroyers VII-X: Challenger -> Charioteer -> FV4004 Conway ->
  FV4005 Stage II, researched from the preserved Sherman Firefly (VI), as in
  ``uk/GB19_Sherman_Firefly.xml`` of 11.20.
* Italian mediums VI-X: P.43 bis -> P.43 ter -> P.44 Pantera -> Prototipo
  Standard B -> Progetto M40 mod. 65, researched from the preserved
  Strv m/42 (V), as in ``european/S02_StrvM42.xml``.

Every source module and turret/gun configuration, the authored research XP
(modules and vehicles) and credit prices, the original LOD0 models, collision
and armour, textures, icons and destroyed-track meshes come from the 11.20
client. The Italian auto-reloaders (``pumpGunMode``) keep their shell-by-shell
timers. Existing cards stay byte-identical (preservation sidecar); the two
attachments are ``additional_research_links`` of the preserved parents.

Calibration: see ``rebalance``.
"""
from __future__ import annotations
import argparse
import copy
import io
import json
from pathlib import Path
from xml.etree import ElementTree as ET

import yaml
from PIL import Image
import port_blitz_1120 as port
import port_blitz_480_data as base
import port_blitz_usa_1120 as usa
import port_blitz_other_1120 as other
import port_blitz_japan_1120 as japan
from blitz_branches_1120_registry import OVERLAY, card_hash, merge_into

ROOT, DATA, SOURCE, PACK = port.ROOT, port.DATA, port.SOURCE, port.PACK
VEHICLES = SOURCE / 'from_apk/XML/item_defs/vehicles'
PARAMETERS = SOURCE / 'from_apk/3d/Tanks/Parameters'
GUN_EFFECTS = 'XML/item_defs/vehicles/common/gun_effects.xml'
INVENTORY = DATA / 'blitz_branches_1120_inventory.json'
CARDS = DATA / 'blitz_branches_1120_cards.json'
BALANCE = DATA / 'blitz_branches_1120_balance.json'
PRESERVATION = DATA / 'blitz_branches_1120_preservation.json'
ICONS = DATA / 'blitz_branches_1120_icons.json'
GENERATOR = 'Tools/port_blitz_branches_1120.py'
GROUPS = usa.GROUPS
KIND = {'turrets': 'turret', 'guns': 'gun', 'tracks': 'chassis',
        'engines': 'engine', 'radios': 'radio'}
BRANCHES = {
    'uk': ['GB41_Challenger', 'GB80_Charioteer', 'GB81_FV4004', 'GB83_4005'],
    'european': ['It10_P43_bis', 'It09_P43_ter', 'It14_P44_Pantera',
                 'It12_Prototipo_Standard_B', 'It08_Progetto_M40_mod65'],
}
# Source predecessor of each branch's first vehicle; a preserved tree card.
PARENTS = {'uk': 'GB19_Sherman_Firefly', 'european': 'S02_StrvM42'}
MODEL_NATIONS = {'uk': 'GB', 'european': 'European'}
NATION_ID = {'uk': 4, 'european': 9}
# Tree rows: each line continues straight on from its parent's neighbourhood.
# UK row 1 is the Sherman V -> Firefly line, which ended at tier VI; European
# row -1 is free next to the Kranvagn line (-2) the Strv m/42 belongs to.
ROWS = {'uk': 1, 'european': -1}
LEGACY = 511
# One calibration step never moves HP or output by more than this share.
MAX_STEP = .15
# HP x sustained DPM never above this multiple of the competitors' medians.
BUDGET = 1.15


def save(path, value):
    port.save(path, value)


def all_ids():
    return [v for ids in BRANCHES.values() for v in ids]


def nation_of(vehicle):
    return next(n for n, ids in BRANCHES.items() if vehicle in ids)


def parameters(nation, vehicle):
    path = PARAMETERS / nation / (vehicle + '.yaml')
    document = yaml.safe_load(path.read_text(encoding='utf-8-sig'))
    resources = document['resourcesPath']
    for key in ('blitzModelPath', 'collisionMesh', 'smallIconPath', 'bigIconPath'):
        if not resources.get(key):
            raise ValueError(f'{vehicle}: missing authoritative {key}')
    return path, resources


def stem(card):
    return Path(str(card.get('source', ''))).stem


def baseline():
    live = json.loads((DATA / 'tanks.json').read_text())['tanks']
    old = [c for c in live if c.get('source_overlay') not in (OVERLAY, 'ussr_1120', 'mark_i_male_1120')]
    if PRESERVATION.exists():
        expected = json.loads(PRESERVATION.read_text())['cards']
        actual = {c['name']: card_hash(c) for c in old}
        if actual != expected:
            changed = sorted((set(expected) ^ set(actual))
                             | {n for n in expected if actual.get(n) != expected[n]})
            raise RuntimeError('Protected baseline drift: ' + ', '.join(changed))
    elif len(old) != LEGACY:
        raise RuntimeError(f'Expected {LEGACY} existing cards; got {len(old)}')
    return old


def discover(old):
    listings = {}
    for nation, ids in BRANCHES.items():
        listing = {x.tag: x for x in ET.parse(VEHICLES / nation / 'list.xml').getroot()}
        tree = yaml.safe_load((SOURCE / f'from_apk/Configs/TechTree/{nation}_tree.yaml').read_text())
        known = {stem(c) for c in old if c['nation'] == NATION_ID[nation]}
        for v in ids:
            entry = listing.get(v)
            if entry is None or v in known:
                raise RuntimeError(f'{v}: not an additive 11.20 vehicle')
            tags = set(entry.findtext('tags', '').split())
            if (entry.find('price/gold') is not None or {'collectible', 'noRandom'} & tags
                    or v not in tree['tanks']):
                raise RuntimeError(f'{v}: no longer a researchable tree vehicle; re-audit')
        parent = next((c for c in old if c['nation'] == NATION_ID[nation]
                       and stem(c) == PARENTS[nation]), None)
        if parent is None or not parent.get('tree_tank') or parent.get('prem'):
            raise RuntimeError(f'{nation}: preserved parent {PARENTS[nation]} missing')
        listings[nation] = listing
    return listings


def configure_importer():
    port.BRANCHES = copy.deepcopy(BRANCHES)
    port.MODEL_NATIONS.update(MODEL_NATIONS)
    port.NAMES = {v: v.lower() for v in all_ids()}
    port.INVENTORY = INVENTORY
    # Scene names are authored aliases (Challenger.sc2, P43_ter.sc2, ...).
    port.model_stem = lambda nation, vehicle: Path(
        parameters(nation, vehicle)[1]['blitzModelPath']).stem


def prepare(old, listings):
    configure_importer()
    # base.build_card resolves every gun's shot report through this table.
    port.unpack(port.source_file(GUN_EFFECTS), PACK / GUN_EFFECTS)
    records = port.prepare()
    for record in records:
        v, nation = record['vehicle_id'], record['nation']
        path, resources = parameters(nation, v)
        if Path(record['scene']).relative_to('3d') != Path(resources['blitzModelPath']):
            raise ValueError(f"{v}: model {record['scene']} is not {resources['blitzModelPath']}")
        if Path(record['collision']).relative_to('3d/Tanks') != Path(resources['collisionMesh']):
            raise ValueError(f"{v}: collision {record['collision']} is not {resources['collisionMesh']}")
        record.update(parameter_source=str(path.relative_to(SOURCE)),
                      parameter_sha256=port.digest(path), resources=resources)
        for label, relative in [('scene', record['scene']),
                                ('geometry', record['scene'].removesuffix('.sc2') + '.scg'),
                                ('collision', record['collision']),
                                ('collision_geometry', record['collision'].removesuffix('.sc2') + '.scg')]:
            original = port.source_file(relative)
            record[label + '_source'] = str(original.relative_to(SOURCE))
            record[label + '_sha256'] = port.digest(original)
        root = ET.parse(VEHICLES / nation / (v + '.xml')).getroot()
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
        entry = listings[nation][v]
        record['listing'] = {'id': base.integer(entry.find('id')),
                             'level': base.integer(entry.find('level'), 1),
                             'tags': entry.findtext('tags', '').split(),
                             'price_credits': base.integer(entry.find('price'))}
    save(INVENTORY, {
        'version': '11.20', 'source': str(SOURCE), 'generator': GENERATOR,
        'branches': BRANCHES, 'parents': PARENTS,
        'records': records,
        'totals': {k: sum(len(r['modules'][k]) for r in records) for k in GROUPS},
        'configurations': sum(len(r['configurations']) for r in records),
    })
    if not PRESERVATION.exists():
        models = json.loads((DATA / 'tank_models_source_manifest.json').read_text())['tanks']
        save(PRESERVATION, {
            'policy': f'All {len(old)} pre-existing cards unchanged; branch attachments are additional links outside their records',
            'cards': {c['name']: card_hash(c) for c in old},
            'model_entries': {name: card_hash(value) for name, value in models.items()},
            'shot_bank_sha256': port.digest(DATA / 'tank_sounds.json'),
            'legacy_count': len(old),
        })
    return records


def vehicle_edges(client, nation, vehicle):
    """Every authored vehicle unlock of one source vehicle, with its module."""
    path = VEHICLES / nation / (vehicle + '.xml')
    if not path.is_file():
        return
    root = ET.parse(path).getroot()
    groups = [('chassis', 'chassis', 'chassis'), ('engines', 'engines', 'engine'),
              ('radios', 'radios', 'radio'), ('turrets0', 'turrets', 'turret')]
    for group, shared_kind, kind in groups:
        parent = root.find(group)
        for node in parent if parent is not None else []:
            for key, cost in base.unlock_map(client.merge(shared_kind, node)).items():
                if key.startswith('vehicle:'):
                    yield {'from_id': vehicle, 'to_id': key.split(':', 1)[1], 'cost_xp': cost,
                           'required_kind': kind, 'required_module': node.tag}
            if group == 'turrets0':
                for gun in node.find('guns'):
                    # Unlocks only: the shell resolution of NationClient1120
                    # would fail closed on unrelated, unported vehicles.
                    for key, cost in base.unlock_map(base.NationClient.merge(client, 'guns', gun)).items():
                        if key.startswith('vehicle:'):
                            yield {'from_id': vehicle, 'to_id': key.split(':', 1)[1],
                                   'cost_xp': cost, 'required_kind': 'gun',
                                   'required_module': gun.tag, 'required_turret': node.tag}


def autoreload(client, root, card):
    """Shell-by-shell timers of every pumpGunMode gun, as in the client."""
    for turret in root.find('turrets0'):
        for gun_xml in turret.find('guns'):
            resolved = client.merge('guns', gun_xml)
            timers = base.vector(resolved.find('pumpGunReloadTimes'), ())
            if timers and base.local_text(resolved, 'pumpGunMode') == 'true':
                gun = next(g for g in card['guns'] if g['name'] == gun_xml.tag)
                gun.update(autoreload_times=timers, reload=sum(timers))


# --------------------------------------------------------------------------
# Calibration: carry the 11.20 standing over to this game's tier
# --------------------------------------------------------------------------

def field_1120(level, tank_class, exclude):
    """Same-tier/class tech-tree vehicles of the 11.20 client.

    As ``japan.field_1120``, but every auto-reloader of the field gets its
    authored timers too, so that the standing compares like with like.
    """
    english = yaml.safe_load((SOURCE / 'from_apk/Strings/en.yaml').read_text())
    saved = base.PACK, base.VEHICLES, base.NATION_ID
    base.PACK, base.VEHICLES = SOURCE / 'from_apk', VEHICLES
    # The nation id plays no part in the statistics.
    base.NATION_ID = dict(saved[2], european=0)
    found = []
    try:
        for listing in sorted(VEHICLES.glob('*/list.xml')):
            nation = listing.parent.name
            client = port.NationClient1120(nation, english)
            for entry in ET.parse(listing).getroot():
                tags = entry.findtext('tags', '').split()
                kind = next((base.CLASS_ID[t] for t in tags if t in base.CLASS_ID), -1)
                if (entry.tag in exclude or kind != tank_class
                        or base.integer(entry.find('level'), 1) != level
                        or entry.find('price/gold') is not None
                        or {'collectible', 'noRandom'} & set(tags)):
                    continue
                card = base.build_card(client, {'id': entry.tag}, {entry.tag: entry.tag.lower()})
                autoreload(client, ET.parse(VEHICLES / nation / (entry.tag + '.xml')).getroot(), card)
                found.append((nation + '/' + entry.tag, card))
    finally:
        base.PACK, base.VEHICLES, base.NATION_ID = saved
    if len(found) < 5:
        raise RuntimeError(f'Only {len(found)} 11.20 tier {level} competitors')
    return found


def rebalance(card, peers, field):
    """Keep the vehicle's 11.20 standing among its tier and class.

    Total HP and sustained DPM are the budget of a tank. For each, the
    vehicle's ratio to the median of the 11.20 same-tier/class tech-tree
    vehicles is carried over to the median of this game's same-tier/class
    researchable competitors (target = 11.20 ratio x median here). One step
    moves a value by at most 15 % either way, so a tank that the older
    competitors outclass is lifted as well as an over-strong one is cut, and
    the HP x DPM product never exceeds 1.15x the competitors' medians. HP
    keeps the source hull/turret split; DPM moves through the reload of
    every mount (every auto-reload timer of the Italian guns), so alpha,
    penetration, shell types and speeds, magazines and the guns themselves
    stay as authored. All other stats keep their source values inside the
    overlays' upper caps (speeds and traverse 1.20x, view range 1.10x,
    specific power 1.15x, gun dispersion/aim time and track dispersion
    factors not below 0.85x the median). Armour, geometry, modules,
    research and identities are never changed.
    """
    before = copy.deepcopy(card)
    applied = japan.standard_caps(card, peers)
    own_hp, own_dpm = usa.total_health(card), japan.sustained(card)
    peer_hp = usa.median([usa.total_health(p) for p in peers], own_hp)
    peer_dpm = usa.median([japan.sustained(p) for p in peers], own_dpm)
    field_hp = usa.median([usa.total_health(c) for _, c in field], own_hp)
    field_dpm = usa.median([japan.sustained(c) for _, c in field], own_dpm)
    standing = {'total_health': own_hp / field_hp, 'sustained_dpm': own_dpm / field_dpm}

    def step(target, own):
        return min(1 + MAX_STEP, max(1 - MAX_STEP, target / own))

    hp_target = standing['total_health'] * peer_hp
    hp_factor = step(hp_target, own_hp)
    card['health'] = round(card['health'] * hp_factor)
    for turret in card['turrets']:
        turret['health'] = round(turret['health'] * hp_factor)

    dpm_target = standing['sustained_dpm'] * peer_dpm
    budget = peer_dpm * BUDGET * peer_hp / usa.total_health(card)
    output = min(step(dpm_target, own_dpm), budget / own_dpm)
    for gun in card['guns']:
        other.retime(card, gun, output)

    card['turret_health'] = card['turrets'][-1]['health']
    card['turret_rot_speed'] = card['turrets'][-1]['rot_speed']
    card['spot_radius'] = card['turrets'][-1]['spot_radius']
    balanced = japan.metrics(card)
    return {'name': card['name'], 'level': card['lvl'], 'class': card['tank_class'],
            'competitors': [p['name'] for p in peers],
            'competitors_1120': [name for name, _ in field],
            'peer_medians': {'total_health': peer_hp, 'sustained_dpm': peer_dpm},
            'medians_1120': {'total_health': field_hp, 'sustained_dpm': field_dpm},
            'standing_1120': standing,
            'standing_balanced': {'total_health': balanced['total_health'] / peer_hp,
                                  'sustained_dpm': balanced['max_sustained_dpm'] / peer_dpm},
            'targets': {'total_health': hp_target, 'sustained_dpm': dpm_target,
                        'hp_dpm_budget_dpm': budget},
            'factors': {'health': hp_factor, 'output': output, 'reload': 1 / output},
            'caps_applied': applied,
            'original_metrics': japan.metrics(before), 'balanced_metrics': balanced,
            'original': before, 'balanced': copy.deepcopy(card)}


def module_names(card, kind):
    group = {v: k for k, v in KIND.items()}[kind]
    return {m['name'] for m in card[group]}


def build_cards(old, records, listings):
    base.PACK, base.VEHICLES = PACK, PACK / 'XML/item_defs/vehicles'
    base.NATION_ID['european'] = NATION_ID['european']
    english = yaml.safe_load((SOURCE / 'from_apk/Strings/en.yaml').read_text())
    russian = yaml.safe_load((SOURCE / 'from_apk/Strings/ru.yaml').read_text())
    strings = dict(english); strings.update(russian)
    occupied = {(c['nation'], c['lvl'], c['tree_branch']) for c in old if c.get('tree_tank')}
    by_id = {r['vehicle_id']: r for r in records}
    cards, balances, links, dropped = [], [], [], []
    for nation, ids in BRANCHES.items():
        client = port.NationClient1120(nation, strings)
        preserved = {stem(c): c for c in old if c['nation'] == NATION_ID[nation]}
        names = {v: c['name'] for v, c in preserved.items()}
        names.update({v: by_id[v]['name'] for v in ids})
        edges = [e for v in listings[nation] for e in vehicle_edges(client, nation, v)
                 if e['to_id'] in ids and e['from_id'] in names]
        for v in ids:
            record = by_id[v]
            card = base.build_card(client, {'id': v}, names)
            root = ET.parse(PACK / 'XML/item_defs/vehicles' / nation / (v + '.xml')).getroot()
            if card['display_name'] in ('', v):
                raise ValueError(f'{v}: no authored name')
            if card.get('display_name_short') in (None, '', v):
                card['display_name_short'] = card['display_name']
            incoming = [e['cost_xp'] for e in edges if e['to_id'] == v]
            if not incoming or min(incoming) <= 0:
                raise ValueError(f'{v}: missing source incoming research XP')
            card.update(source_version='11.20', source_overlay=OVERLAY,
                        source=record['xml_source'], source_sha256=record['xml_sha256'],
                        acquisition='', prem=False, collector=False, shop_tank=False,
                        tree_tank=True, tree_branch=ROWS[nation], cost_gold=0,
                        collector_sell_gold=0, cost_xp=min(incoming))
            slot_key = (card['nation'], card['lvl'], card['tree_branch'])
            if slot_key in occupied:
                raise ValueError(f'{v}: tree slot {slot_key} overlaps a preserved card')
            occupied.add(slot_key)
            # Research towards vehicles this port does not carry (P.43 bis ->
            # Carro d'assalto P.88) has no target card; keep only real edges.
            for holder in ([m for g in GROUPS for m in card[g]]
                           + [s for t in card['turrets'] for s in t['guns']]):
                for key in [k for k in holder.get('unlocks', {}) if k.startswith('vehicle:')]:
                    if key.split(':', 1)[1] not in names:
                        dropped.append({'vehicle': v, 'module': holder['name'], 'unlock': key,
                                        'cost_xp': holder['unlocks'].pop(key)})
            stray = other.reachable_modules(card)
            if stray:
                raise ValueError(f'{v}: unreachable researchable modules {stray}')
            for track, xml in zip(card['tracks'], root.find('chassis')):
                resolved = client.merge('chassis', xml)
                armor = port.exporter.armor_values(resolved)
                if armor.get('leftTrack') != armor.get('rightTrack'):
                    raise ValueError(f'{v}: asymmetric chassis armor needs side-specific runtime')
                track['armor'] = armor.get('leftTrack', 0)
                internal = usa.parse_internal_track(resolved)
                if internal is not None:
                    track['internal_track'] = internal
            for turret, xml in zip(card['turrets'], root.find('turrets0')):
                turret['mesh'] = port.slot(xml)
                for slot, gun_xml in zip(turret['guns'], xml.find('guns')):
                    slot['mesh'] = port.slot(gun_xml)
                    resolved = client.merge('guns', gun_xml)
                    gun = next(g for g in card['guns'] if g['name'] == gun_xml.tag)
                    gun['max_regen_health'] = base.number(resolved.find('maxRegenHealth'))
                    gun['repair_cost'] = base.number(resolved.find('repairCost'), 1.0)
            autoreload(client, root, card)
            for radio, xml in zip(card['radios'], root.find('radios')):
                resolved = client.merge('radios', xml)
                radio['max_regen_health'] = base.number(resolved.find('maxRegenHealth'))
                radio['repair_cost'] = base.number(resolved.find('repairCost'), 1.0)
            field = field_1120(card['lvl'], card['tank_class'], {v})
            balances.append(rebalance(card, usa.peers_for(card, old), field))
            cards.append(card)
        attached = 0
        for edge in edges:
            if edge['from_id'] in ids:
                continue
            parent = preserved[edge['from_id']]
            required = edge['required_module']
            if required not in module_names(parent, edge['required_kind']):
                raise ValueError(f"{parent['name']}: no module {required} for {edge}")
            if edge.get('required_turret') and edge['required_turret'] not in module_names(parent, 'turret'):
                raise ValueError(f"{parent['name']}: no turret for {edge}")
            link = {k: x for k, x in edge.items() if k not in ('from_id', 'to_id')}
            link.update({'from': parent['name'], 'to': names[edge['to_id']],
                         'preserve_legacy_elite': True, 'source_overlay': OVERLAY,
                         'source': str((VEHICLES / nation / (edge['from_id'] + '.xml')).relative_to(SOURCE)),
                         'source_sha256': port.digest(VEHICLES / nation / (edge['from_id'] + '.xml'))})
            links.append(link)
            attached += edge['from_id'] == PARENTS[nation] and edge['to_id'] == ids[0]
        if attached != 1:
            raise ValueError(f'{nation}: branch not attached to {PARENTS[nation]}')
    save(CARDS, {'version': '11.20', 'generator': GENERATOR,
                 'policy': 'every source module/configuration, authored research XP and credit prices; 11.20 tier standing carried over',
                 'unported_successors': dropped,
                 'additional_research_links': links, 'tanks': cards})
    save(BALANCE, {'baseline': f'{LEGACY} preserved Unity cards: non-premium same-tier/class competitors; '
                               '11.20 client: same-tier/class tech-tree vehicles',
                   'policy': rebalance.__doc__.strip(),
                   'limitations': 'Deterministic stat-budget calibration, not a live PvP playtest; '
                                  'armour is source geometry and not calibrated',
                   'tanks': balances})
    print('Branch cards:', len(cards), 'modules:', {k: sum(len(c[k]) for c in cards) for k in GROUPS},
          'configurations:', sum(len(t['guns']) for c in cards for t in c['turrets']),
          'links:', [(x['from'], x['to'], x['cost_xp']) for x in links], flush=True)
    return cards


def export_icons(records):
    atlas_path = DATA / 'atlas_regions.json'
    atlas = json.loads(atlas_path.read_text())
    textures = ROOT / 'Assets/Blitz/Resources/Textures'
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
            raise ValueError(f'{original}: empty authored image')
        image = image.crop(crop)
        rid = port.exporter.unity_id(record['name']).lower()
        destination = textures / 'TankIcons' / (rid + '.png')
        image.save(destination)
        texture = 'TankIcons/' + rid
        atlas['textures'][texture] = {'width': image.width, 'height': image.height}
        atlas['regions']['tank_icon_' + rid] = {
            'texture': texture, 'x': 0, 'y': 0, 'w': image.width, 'h': image.height}
        evidence.append({'name': record['name'], 'source': str(original.relative_to(SOURCE)),
                         'sha256': port.digest(original), 'crop': crop,
                         'output_sha256': port.digest(destination)})
    save(atlas_path, atlas)
    save(ICONS, {'version': '11.20',
                 'policy': 'authored Parameters icon paths; exact alpha crop; no resampling',
                 'icons': evidence})


def export_crash(records):
    for nation in BRANCHES:
        usa.XML = VEHICLES / nation
        usa.export_crash([r for r in records if r['nation'] == nation])


def main():
    parser = argparse.ArgumentParser()
    for flag in ('prepare', 'models', 'icons', 'crash', 'merge', 'rebuild'):
        parser.add_argument('--' + flag, action='store_true')
    args = parser.parse_args()
    old = baseline()
    listings = discover(old)
    configure_importer()
    if args.prepare:
        records = prepare(old, listings)
        cards = build_cards(old, records, listings)
    else:
        records = json.loads(INVENTORY.read_text())['records']
        cards = json.loads(CARDS.read_text())['tanks']
    if args.models:
        port.export_models(records, cards, args.rebuild)
        # Source importer policy before Unity's first import (shared with the
        # German overlay): meshes uncompressed, decoded PNGs tagged with the
        # ASTC block of their client texture.
        import blitz_germany_1120_models
        blitz_germany_1120_models.ensure_source_imports(port, records)
    if args.icons:
        export_icons(records)
    if args.crash:
        export_crash(records)
    if args.merge:
        document = merge_into(json.loads((DATA / 'tanks.json').read_text()))
        (DATA / 'tanks.json').write_text(
            json.dumps(document, ensure_ascii=False, indent=1, sort_keys=True) + '\n')
        print('Live cards:', len(document['tanks']), flush=True)


if __name__ == '__main__':
    main()
