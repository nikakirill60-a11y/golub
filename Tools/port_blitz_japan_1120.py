#!/usr/bin/env python3
"""Additive 11.20 port of the Japanese collectible Type 5 Heavy.

``vehicles/japan/list.xml`` of 11.20 lists ``J20_Type_2605`` as a tier X
collectible heavy (tags ``heavyTank collectible``, ``notInShop``, 15000 gold,
sold for 7500). The 4.8.0 roster has no Japanese heavy, so the vehicle comes
on its own: every source module and configuration, the original LOD0 model,
collision/armour, textures, icons and destroyed-track meshes. Existing cards
stay byte-identical (preservation sidecar).

Acquisition follows the collectible convention of the USA/other overlays
(A178 SPHT): no tree slot, no successor, the list price as ``cost_gold`` and
the sell price as ``collector_sell_gold``.

Calibration keeps the vehicle's 11.20 standing among the tier X heavies (see
``rebalance``). The per-shell caps of the USA overlay are not used: against
the 120-130 mm guns of the tier they would cut the 152 mm gun's alpha by a
third and make it a different gun.
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
from blitz_japan_1120_registry import card_hash, merge_into

ROOT, DATA, SOURCE, PACK = port.ROOT, port.DATA, port.SOURCE, port.PACK
NATION = 'japan'
VEHICLES = ('J20_Type_2605',)
XML = SOURCE / 'from_apk/XML/item_defs/vehicles/japan'
COMPAT = PACK / 'XML/item_defs/vehicles/japan'
PARAMETERS = SOURCE / 'from_apk/3d/Tanks/Parameters/japan'
GUN_EFFECTS = 'XML/item_defs/vehicles/common/gun_effects.xml'
INVENTORY = DATA / 'blitz_japan_1120_inventory.json'
CARDS = DATA / 'blitz_japan_1120_cards.json'
BALANCE = DATA / 'blitz_japan_1120_balance.json'
PRESERVATION = DATA / 'blitz_japan_1120_preservation.json'
ICONS = DATA / 'blitz_japan_1120_icons.json'
GENERATOR = 'Tools/port_blitz_japan_1120.py'
GROUPS = usa.GROUPS
OVERLAY = 'japan_1120'
LEGACY = 510
# One calibration step never removes more than this share of a stat.
MAX_CUT = .15


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


def is_japan(card):
    return '/japan/' in str(card.get('source', '')).lower()


def baseline():
    live = json.loads((DATA / 'tanks.json').read_text())['tanks']
    old = [c for c in live if c.get('source_overlay') not in (OVERLAY, 'branches_1120', 'ussr_1120', 'mark_i_male_1120')]
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
    listing = {x.tag: x for x in ET.parse(XML / 'list.xml').getroot()}
    known = {Path(c['source']).stem for c in old if is_japan(c)}
    for v in VEHICLES:
        entry = listing.get(v)
        if entry is None or v in known:
            raise RuntimeError(f'{v}: not an additive 11.20 vehicle')
        tags = set(entry.findtext('tags', '').split())
        if base.integer(entry.find('level'), 1) != 10 or not {'heavyTank', 'collectible'} <= tags:
            raise RuntimeError(f'{v}: source listing changed; re-audit')
        if entry.find('price/gold') is None:
            raise RuntimeError(f'{v}: collectible without a gold list price')
    return list(VEHICLES), listing


def configure_importer(ids):
    port.BRANCHES = {NATION: ids}
    port.MODEL_NATIONS[NATION] = 'Japan'
    port.NAMES = {v: v.lower() for v in ids}
    port.INVENTORY = INVENTORY
    port.model_stem = lambda nation, vehicle: Path(
        parameters(vehicle)[1]['blitzModelPath']).stem


def prepare(old, ids, listing):
    configure_importer(ids)
    # base.build_card resolves every gun's shot report through this table.
    port.unpack(port.source_file(GUN_EFFECTS), PACK / GUN_EFFECTS)
    records = port.prepare()
    for record in records:
        v = record['vehicle_id']
        path, resources = parameters(v)
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
        entry = listing[v]
        record['listing'] = {'id': base.integer(entry.find('id')),
                             'level': base.integer(entry.find('level'), 1),
                             'tags': entry.findtext('tags', '').split(),
                             'not_in_shop': entry.find('notInShop') is not None,
                             'price_gold': base.integer(entry.find('price')),
                             'sell_price_gold': base.integer(entry.find('sellPrice'))}
    save(INVENTORY, {
        'version': '11.20', 'source': str(SOURCE), 'generator': GENERATOR,
        'vehicles': list(ids),
        'records': records,
        'totals': {k: sum(len(r['modules'][k]) for r in records) for k in GROUPS},
        'configurations': sum(len(r['configurations']) for r in records),
    })
    if not PRESERVATION.exists():
        models = json.loads((DATA / 'tank_models_source_manifest.json').read_text())['tanks']
        save(PRESERVATION, {
            'policy': f'All {len(old)} pre-existing cards unchanged',
            'cards': {c['name']: card_hash(c) for c in old},
            'model_entries': {name: card_hash(value) for name, value in models.items()},
            'shot_bank_sha256': port.digest(DATA / 'tank_sounds.json'),
            'legacy_count': len(old), 'legacy_japan_count': sum(map(is_japan, old)),
        })
    return records


# --------------------------------------------------------------------------
# Calibration: carry the 11.20 standing over to this game's tier
# --------------------------------------------------------------------------

def sustained(card):
    """Best sustained output of the top turret's guns, as mounted there."""
    return max(other.gun_dpm(g) for g in usa.mounted_guns(card))


def metrics(card):
    result = usa.metrics(card)
    result['max_sustained_dpm'] = sustained(card)
    return result


def field_1120(level, tank_class, exclude):
    """Same-tier/class tech-tree vehicles of the 11.20 client.

    Gold-priced, collectible and non-random vehicles are left out, like the
    premium cards are left out of this game's competitors.
    """
    vehicles = SOURCE / 'from_apk/XML/item_defs/vehicles'
    english = yaml.safe_load((SOURCE / 'from_apk/Strings/en.yaml').read_text())
    saved = base.PACK, base.VEHICLES, base.NATION_ID
    base.PACK, base.VEHICLES = SOURCE / 'from_apk', vehicles
    # 4.8.0 has no European nation; the id plays no part in the statistics.
    base.NATION_ID = dict(saved[2], european=0)
    found = []
    try:
        for listing in sorted(vehicles.glob('*/list.xml')):
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
                found.append((nation + '/' + entry.tag, card))
    finally:
        base.PACK, base.VEHICLES, base.NATION_ID = saved
    if len(found) < 5:
        raise RuntimeError(f'Only {len(found)} 11.20 tier {level} competitors')
    return found


def standard_caps(card, peers):
    """The overlays' upper caps for everything outside the HP/DPM budget."""
    applied = {}

    def cap(key, value, limit, lower=False):
        if (value < limit) if lower else (value > limit):
            applied[key] = {'source': value, 'calibrated': round(limit, 6)}
            return round(limit, 6)
        return value

    for axis in ('x', 'y'):
        reference = usa.median([p['speed_limits'][axis] for p in peers], card['speed_limits'][axis])
        card['speed_limits'][axis] = cap('speed_' + axis, card['speed_limits'][axis], reference * 1.20)
    for group, field, upper in [('turrets', 'rot_speed', 1.20), ('turrets', 'spot_radius', 1.10),
                                ('tracks', 'rot_speed', 1.20), ('radios', 'distance', 1.20)]:
        reference = usa.median([p[group][-1][field] for p in peers], card[group][-1][field])
        for module in card[group]:
            module[field] = cap(f"{group}/{module['name']}/{field}", module[field], reference * upper)
    for track in card['tracks']:
        for field in ('disp_move_mul', 'disp_rotation_mul'):
            reference = usa.median([p['tracks'][-1][field] for p in peers], track[field])
            track[field] = cap(f"tracks/{track['name']}/{field}", track[field], reference * .85, True)
    density = usa.median([p['engines'][-1]['power'] / usa.mass(p, p['engines'][-1]) for p in peers],
                         card['engines'][-1]['power'] / usa.mass(card, card['engines'][-1]))
    for engine in card['engines']:
        limit = int(density * 1.15 * usa.mass(card, engine))
        if engine['power'] > limit:
            applied[f"engines/{engine['name']}/power"] = {'source': engine['power'], 'calibrated': limit}
            engine['power'] = limit
    pool = [(p, other.best_gun(p)[0]) for p in peers]
    for gun in card['guns']:
        similar = other.similar_guns(gun, pool)
        for field in ('disp', 'aim_time'):
            reference = usa.median([g[field] for _, g in similar], gun[field])
            gun[field] = cap(f"guns/{gun['name']}/{field}", gun[field], reference * .85, True)
    return applied


def rebalance(card, peers, field):
    """Keep the vehicle's 11.20 standing among its tier and class.

    Total HP and sustained DPM are a heavy's budget. For each, the vehicle's
    ratio to the median of the 11.20 same-tier/class tech-tree vehicles is
    carried over to the median of this game's same-tier/class researchable
    competitors (target = 11.20 ratio x median here). A value is only ever
    lowered, never raised, by at most 15 %, and the HP x DPM product stays
    within the overlays' 1.15x budget. HP keeps the source hull/turret split;
    DPM moves through the reload of every mount, so alpha, penetration,
    shell types and speeds, magazine and the gun itself stay as authored.
    All other stats keep their source values inside the overlays' upper caps
    (speeds and traverse 1.20x, view range 1.10x, specific power 1.15x, gun
    dispersion/aim time and track dispersion factors not below 0.85x the
    median). Armour, geometry, modules and identities are never changed.
    """
    before = copy.deepcopy(card)
    applied = standard_caps(card, peers)
    own_hp, own_dpm = usa.total_health(card), sustained(card)
    peer_hp = usa.median([usa.total_health(p) for p in peers], own_hp)
    peer_dpm = usa.median([sustained(p) for p in peers], own_dpm)
    field_hp = usa.median([usa.total_health(c) for _, c in field], own_hp)
    field_dpm = usa.median([sustained(c) for _, c in field], own_dpm)
    standing = {'total_health': own_hp / field_hp, 'sustained_dpm': own_dpm / field_dpm}

    hp_target = standing['total_health'] * peer_hp
    hp_factor = min(1., max(1 - MAX_CUT, hp_target / own_hp))
    card['health'] = round(card['health'] * hp_factor)
    for turret in card['turrets']:
        turret['health'] = round(turret['health'] * hp_factor)

    dpm_target = standing['sustained_dpm'] * peer_dpm
    budget = peer_dpm * 1.15 * peer_hp / usa.total_health(card)
    output = min(1., max(1 - MAX_CUT, min(dpm_target, budget) / own_dpm))
    for gun in card['guns']:
        other.retime(card, gun, output)

    card['turret_health'] = card['turrets'][-1]['health']
    card['turret_rot_speed'] = card['turrets'][-1]['rot_speed']
    card['spot_radius'] = card['turrets'][-1]['spot_radius']
    balanced = metrics(card)
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
            'original_metrics': metrics(before), 'balanced_metrics': balanced,
            'original': before, 'balanced': copy.deepcopy(card)}


def build_cards(old, records, listing):
    base.PACK, base.VEHICLES = PACK, PACK / 'XML/item_defs/vehicles'
    english = yaml.safe_load((SOURCE / 'from_apk/Strings/en.yaml').read_text())
    russian = yaml.safe_load((SOURCE / 'from_apk/Strings/ru.yaml').read_text())
    strings = dict(english); strings.update(russian)
    client = port.NationClient1120(NATION, strings)
    names = {Path(c['source']).stem: c['name'] for c in old if is_japan(c)}
    names.update({r['vehicle_id']: r['name'] for r in records})
    cards, balances = [], []
    for record in records:
        v = record['vehicle_id']
        card = base.build_card(client, {'id': v}, names)
        root = ET.parse(COMPAT / (v + '.xml')).getroot()
        if card['display_name'] in ('', v):
            raise ValueError(f'{v}: no authored name')
        if card.get('display_name_short') in (None, '', v):
            card['display_name_short'] = card['display_name']
        entry = listing[v]
        card.update(source_version='11.20', source_overlay=OVERLAY,
                    source=record['xml_source'], source_sha256=record['xml_sha256'],
                    acquisition='collectible', prem=True, collector=True,
                    shop_tank=False, tree_tank=False, tree_branch=0,
                    cost=0, cost_xp=0, unlock_tanks={},
                    cost_gold=base.integer(entry.find('price')),
                    collector_sell_gold=base.integer(entry.find('sellPrice')))
        # A collectible never opens a tech-tree successor; its own module
        # research stays as authored.
        for group in GROUPS:
            for module in card[group]:
                module['unlocks'] = {k: x for k, x in module.get('unlocks', {}).items()
                                     if not k.startswith('vehicle:')}
        for turret in card['turrets']:
            for slot in turret['guns']:
                slot['unlocks'] = {k: x for k, x in slot.get('unlocks', {}).items()
                                   if not k.startswith('vehicle:')}
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
                timers = base.vector(resolved.find('pumpGunReloadTimes'), ())
                if timers and base.local_text(resolved, 'pumpGunMode') == 'true':
                    gun.update(autoreload_times=timers, reload=sum(timers))
        for radio, xml in zip(card['radios'], root.find('radios')):
            resolved = client.merge('radios', xml)
            radio['max_regen_health'] = base.number(resolved.find('maxRegenHealth'))
            radio['repair_cost'] = base.number(resolved.find('repairCost'), 1.0)
        field = field_1120(card['lvl'], card['tank_class'], set(VEHICLES))
        balances.append(rebalance(card, usa.peers_for(card, old), field))
        cards.append(card)
    save(CARDS, {'version': '11.20', 'generator': GENERATOR,
                 'policy': 'every source module/configuration; collectible (list price, sell price); 11.20 tier standing carried over',
                 'additional_research_links': [], 'tanks': cards})
    save(BALANCE, {'baseline': f'{LEGACY} preserved Unity cards: non-premium same-tier/class competitors; '
                               '11.20 client: same-tier/class tech-tree vehicles',
                   'policy': rebalance.__doc__.strip(),
                   'limitations': 'Deterministic stat-budget calibration, not a live PvP playtest; '
                                  'armour is source geometry and not calibrated',
                   'tanks': balances})
    print('Japan cards:', len(cards), 'modules:', {k: sum(len(c[k]) for c in cards) for k in GROUPS},
          'configurations:', sum(len(t['guns']) for c in cards for t in c['turrets']), flush=True)
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
    usa.XML = XML
    usa.export_crash(records)


def main():
    parser = argparse.ArgumentParser()
    for flag in ('prepare', 'models', 'icons', 'crash', 'merge', 'rebuild'):
        parser.add_argument('--' + flag, action='store_true')
    args = parser.parse_args()
    old = baseline()
    ids, listing = discover(old)
    configure_importer(ids)
    if args.prepare:
        records = prepare(old, ids, listing)
        cards = build_cards(old, records, listing)
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
