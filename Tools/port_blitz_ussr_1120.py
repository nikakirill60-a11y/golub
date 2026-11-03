#!/usr/bin/env python3
"""Additive 11.20 port of the Soviet collectible Object 907.

``vehicles/ussr/list.xml`` of 11.20 lists ``Object_907`` as a tier X
collectible medium (tags ``mediumTank collectible``, ``notInShop``, 15000
gold, sold for 7500). The 4.8.0 roster has no such vehicle, so it comes on
its own: every source module and configuration, the original LOD0 model,
collision/armour, textures, icons and destroyed-track meshes. Existing cards
stay byte-identical (preservation sidecar).

Acquisition follows the collectible convention of the earlier overlays
(A178 SPHT, Type 5 Heavy): no tree slot, no successor, the list price as
``cost_gold`` and the sell price as ``collector_sell_gold``.

Balance is calibrated against the CURRENT Unity tier X medium roster, not
ratios from another client version. See blitz_object907_balance.py. Re-running
--all reconstructs every Object 907 asset from source and refreshes UI manifests.
"""
from __future__ import annotations
import argparse
import json
from pathlib import Path
from xml.etree import ElementTree as ET

import yaml
import port_blitz_1120 as port
import port_blitz_480_data as base
import port_blitz_usa_1120 as usa
import port_blitz_other_1120 as other
import port_blitz_japan_1120 as japan
from blitz_object907_balance import calibrate
from blitz_ussr_1120_registry import OVERLAY, card_hash, merge_into

ROOT, DATA, SOURCE, PACK = port.ROOT, port.DATA, port.SOURCE, port.PACK
NATION = 'ussr'
VEHICLES = ('Object_907',)
XML = SOURCE / 'from_apk/XML/item_defs/vehicles/ussr'
COMPAT = PACK / 'XML/item_defs/vehicles/ussr'
PARAMETERS = SOURCE / 'from_apk/3d/Tanks/Parameters/ussr'
GUN_EFFECTS = 'XML/item_defs/vehicles/common/gun_effects.xml'
INVENTORY = DATA / 'blitz_ussr_1120_inventory.json'
CARDS = DATA / 'blitz_ussr_1120_cards.json'
BALANCE = DATA / 'blitz_ussr_1120_balance.json'
PRESERVATION = DATA / 'blitz_ussr_1120_preservation.json'
ICONS = DATA / 'blitz_ussr_1120_icons.json'
GENERATOR = 'Tools/port_blitz_ussr_1120.py'
GROUPS = usa.GROUPS
LEGACY = 520


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


def is_ussr(card):
    return card.get('nation') == base.NATION_ID[NATION]


def baseline():
    live = json.loads((DATA / 'tanks.json').read_text())['tanks']
    old = [c for c in live if c.get('source_overlay') not in (OVERLAY, 'mark_i_male_1120')]
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
    known = {Path(c['source']).stem for c in old if is_ussr(c)}
    taken = {c['name'] for c in old}
    for v in VEHICLES:
        entry = listing.get(v)
        if entry is None or v in known or v.lower() in taken:
            raise RuntimeError(f'{v}: not an additive 11.20 vehicle')
        tags = set(entry.findtext('tags', '').split())
        if base.integer(entry.find('level'), 1) != 10 or not {'mediumTank', 'collectible'} <= tags:
            raise RuntimeError(f'{v}: source listing changed; re-audit')
        if entry.find('price/gold') is None:
            raise RuntimeError(f'{v}: collectible without a gold list price')
    # TankModelAssets.ResourceId drops punctuation; two cards must never
    # share one model folder.
    ids = {port.exporter.unity_id(c['name']).lower() for c in old}
    for v in VEHICLES:
        if port.exporter.unity_id(v.lower()).lower() in ids:
            raise RuntimeError(f'{v}: resource id collides with an existing card')
    return list(VEHICLES), listing


def configure_importer(ids):
    port.BRANCHES = {NATION: ids}
    port.MODEL_NATIONS[NATION] = 'USSR'
    port.NAMES = {v: v.lower() for v in ids}
    port.INVENTORY = INVENTORY
    port.model_stem = lambda nation, vehicle: Path(
        parameters(vehicle)[1]['blitzModelPath']).stem
    # Split source materials get random glTF node suffixes in Trimesh.
    # Reuse the proven German export policy without any German-specific meshes.
    import blitz_source_primitive_order
    original_setup = port.setup_exporter

    def setup():
        original_setup()
        blitz_source_primitive_order.install(port.exporter)
    port.setup_exporter = setup


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
            'legacy_count': len(old), 'legacy_ussr_count': sum(map(is_ussr, old)),
        })
    return records


def build_cards(old, records, listing):
    base.PACK, base.VEHICLES = PACK, PACK / 'XML/item_defs/vehicles'
    english = yaml.safe_load((SOURCE / 'from_apk/Strings/en.yaml').read_text())
    russian = yaml.safe_load((SOURCE / 'from_apk/Strings/ru.yaml').read_text())
    strings = dict(english); strings.update(russian)
    client = port.NationClient1120(NATION, strings)
    names = {Path(c['source']).stem: c['name'] for c in old if is_ussr(c)}
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
        for radio, xml in zip(card['radios'], root.find('radios')):
            resolved = client.merge('radios', xml)
            radio['max_regen_health'] = base.number(resolved.find('maxRegenHealth'))
            radio['repair_cost'] = base.number(resolved.find('repairCost'), 1.0)
        balances.append(calibrate(card, old))
        cards.append(card)
    save(CARDS, {'version': '11.20', 'generator': GENERATOR,
                 'policy': 'every source module/configuration; collectible (list price, sell price); source identity with explicit current-tier balance',
                 'additional_research_links': [], 'tanks': cards})
    save(BALANCE, {'baseline': f'{len(old)} preserved Unity cards; same-tier/class researchable competitors',
                   'policy': calibrate.__doc__.strip(),
                   'limitations': 'Static equipped-stock comparisons without equipment or crew modifiers; '
                                  'not live PvP win-rate validation. Source armour and projectile geometry unchanged.',
                   'tanks': balances})
    print('USSR cards:', len(cards), 'modules:', {k: sum(len(c[k]) for c in cards) for k in GROUPS},
          'configurations:', sum(len(t['guns']) for c in cards for t in c['turrets']), flush=True)
    return cards


def export_icons(records):
    saved = japan.ICONS
    japan.ICONS = ICONS
    try:
        japan.export_icons(records)
    finally:
        japan.ICONS = saved


def export_crash(records):
    usa.XML = XML
    usa.export_crash(records)



def retain_key_order(value, previous):
    """Keep existing UI JSON easy to review; append only genuinely new keys."""
    if isinstance(value, dict) and isinstance(previous, dict):
        keys = [key for key in previous if key in value]
        keys.extend(sorted(set(value) - set(previous)))
        return {key: retain_key_order(value[key], previous.get(key)) for key in keys}
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--all', action='store_true', help='Rebuild assets and all UI manifests from source')
    for flag in ('prepare', 'models', 'icons', 'crash', 'merge', 'rebuild'):
        parser.add_argument('--' + flag, action='store_true')
    args = parser.parse_args()
    if args.all:
        args.prepare = args.models = args.icons = args.crash = args.merge = args.rebuild = True
    if not any((args.prepare, args.models, args.icons, args.crash, args.merge)):
        parser.error('choose --all or at least one import stage')
    ui_before = {name: json.loads((DATA / name).read_text())
                 for name in ('atlas_regions.json', 'lang.json')} if args.all else {}
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
    if args.all:
        import subprocess
        import sys
        for script in ('port_tank_name_localization.py', 'port_blitz_small_tank_icons.py'):
            subprocess.run([sys.executable, str(ROOT / 'Tools' / script)], cwd=ROOT, check=True)
        for name, before in ui_before.items():
            path = DATA / name
            ordered = retain_key_order(json.loads(path.read_text()), before)
            path.write_text(json.dumps(ordered, ensure_ascii=False, indent=2) + '\n')


if __name__ == '__main__':
    main()
