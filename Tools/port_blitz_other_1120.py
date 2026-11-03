#!/usr/bin/env python3
"""Complete additive 11.20 roster of the mixed nation (``other``).

Every vehicle of ``vehicles/other/list.xml`` that the project does not ship
yet is imported with all of its source modules and configurations. Existing
cards stay byte-identical (preservation sidecar). Differences to the USA
overlay, on request of the project owner:

* Module research is kept for every vehicle, premiums and collectibles
  included. Battle Pass vehicles price their modules in season points
  (``prx_season_N``), a currency this game does not have; those prices are
  converted to combat XP so the whole module set costs what a same-tier,
  same-class researchable tank costs to elite.
* The AC branch of ``other_tree.yaml`` is a real researchable branch
  (credits + XP); its tier IV root is bought for the median tier IV price.
* Calibration is two-sided: a card is pulled into a band around its
  same-tier/class competitors, both down and up, instead of upper caps only.
"""
from __future__ import annotations
import argparse
import copy
import io
import json
import re
import statistics
from pathlib import Path
from xml.etree import ElementTree as ET

import yaml
from PIL import Image
import port_blitz_1120 as port
import port_blitz_480_data as base
import port_blitz_usa_1120 as usa
from blitz_other_1120_registry import card_hash, merge_into

ROOT, DATA, SOURCE, PACK = port.ROOT, port.DATA, port.SOURCE, port.PACK
NATION = 'other'
XML = SOURCE / 'from_apk/XML/item_defs/vehicles/other'
COMPAT = PACK / 'XML/item_defs/vehicles/other'
PARAMETERS = SOURCE / 'from_apk/3d/Tanks/Parameters/other'
INVENTORY = DATA / 'blitz_other_1120_inventory.json'
CARDS = DATA / 'blitz_other_1120_cards.json'
BALANCE = DATA / 'blitz_other_1120_balance.json'
PRESERVATION = DATA / 'blitz_other_1120_preservation.json'
GROUPS = usa.GROUPS
OVERLAY = 'other_1120'
LISTED, PRESERVED, ADDED = 82, 10, 72
SEASON = re.compile(r'^\s*prx_season_(\d+)\s*:\s*(\d+)\s*$')
# 11.20 ships no ru/en string for these; the short names follow the client's
# own naming of the base vehicle.
FALLBACK_NAMES = {
    'Oth36_Indien_Panzer_S1': 'Indien-Panzer S1',
    'Oth37_T26_E4_SuperPershing_S1': 'Super Pershing S1',
    'Oth38_50TP_Tyszkiewicza_S1': '50TP Tyszkiewicza S1',
    'Oth51_FD_24': 'FD-24',
    'Oth53_Rammer': 'Rammer',
    'Oth84_FVE_Defender': 'FVE Defender',
    'Oth85_Ghoul': 'Ghoul',
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
    old = [c for c in live if c.get('source_overlay') not in (OVERLAY, 'japan_1120', 'branches_1120', 'ussr_1120', 'mark_i_male_1120')]
    if PRESERVATION.exists():
        expected = json.loads(PRESERVATION.read_text())['cards']
        actual = {c['name']: card_hash(c) for c in old}
        if actual != expected:
            changed = sorted(n for n in expected if actual.get(n) != expected[n])
            raise RuntimeError('Protected baseline drift: ' + ', '.join(changed))
    elif len(old) != 438:
        raise RuntimeError(f'Expected 438 existing cards; got {len(old)}')
    return old


def is_other(card):
    return '/other/' in str(card.get('source', '')).lower()


def discover(old):
    listing = {x.tag: x for x in ET.parse(XML / 'list.xml').getroot()}
    known = {Path(c['source']).stem for c in old if is_other(c)}
    ids = [v for v in listing if v not in known]
    if len(listing) != LISTED or len(known) != PRESERVED or len(ids) != ADDED:
        raise RuntimeError('Source roster changed; re-audit the source inventory')
    return ids, listing


def configure_importer(ids):
    port.BRANCHES = {NATION: ids}
    port.MODEL_NATIONS[NATION] = 'Other'
    port.NAMES = {v: v.lower() for v in ids}
    port.INVENTORY = INVENTORY
    port.model_stem = lambda nation, vehicle: Path(
        parameters(vehicle)[1]['blitzModelPath']).stem


def peers_for(card, old):
    return usa.peers_for(card, old)


def research_total(card):
    return sum(m['cost_xp'] for g in GROUPS for m in card[g])


def season_costs(root):
    """Every <cost> priced in Battle Pass points."""
    found = []
    for cost in root.iter('cost'):
        match = SEASON.match(cost.text or '')
        if match:
            found.append((cost, int(match.group(1)), int(match.group(2))))
    return found


def convert_season_prices(ids, listing, old):
    """Rewrite season-point module prices of the compat XML into XP.

    The factor makes the complete module set cost the median total module
    XP of researchable same-tier/class competitors, so a Battle Pass vehicle
    is as long to elite as any other vehicle of its tier.
    """
    evidence = []
    for v in ids:
        path = COMPAT / (v + '.xml')
        tree = ET.parse(path)
        costs = season_costs(tree.getroot())
        if not costs:
            continue
        tags = listing[v].findtext('tags', '').split()
        probe = {'lvl': base.integer(listing[v].find('level'), 1),
                 'tank_class': next((base.CLASS_ID[t] for t in tags if t in base.CLASS_ID), 0),
                 'name': v}
        peers = [p for p in peers_for(probe, old) if p.get('tree_tank') and research_total(p) > 0]
        target = statistics.median(research_total(p) for p in peers)
        points = sum(c[2] for c in costs)
        factor = target / max(1, points)
        for node, season, value in costs:
            node.text = str(max(10, int(round(value * factor / 10.0)) * 10))
        ET.indent(tree.getroot())
        tree.write(path, encoding='utf-8')
        evidence.append({'vehicle_id': v, 'season': costs[0][1], 'season_points': points,
                         'xp_per_point': round(factor, 6), 'target_total_xp': target,
                         'competitors': [p['name'] for p in peers]})
    return evidence


def prepare(old, ids, listing):
    configure_importer(ids)
    records = port.prepare()
    for record in records:
        v = record['vehicle_id']
        path, resources = parameters(v)
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
    seasons = convert_season_prices(ids, listing, old)
    save(INVENTORY, {
        'version': '11.20', 'source': str(SOURCE),
        'generator': 'Tools/port_blitz_other_1120.py',
        'listed': LISTED, 'preserved': PRESERVED, 'added': ADDED,
        'season_price_conversion': seasons,
        'records': records,
        'totals': {k: sum(len(r['modules'][k]) for r in records) for k in GROUPS},
        'configurations': sum(len(r['configurations']) for r in records),
    })
    if not PRESERVATION.exists():
        models = json.loads((DATA / 'tank_models_source_manifest.json').read_text())['tanks']
        save(PRESERVATION, {
            'policy': 'All 438 pre-existing cards unchanged',
            'cards': {c['name']: card_hash(c) for c in old},
            'model_entries': {name: card_hash(value) for name, value in models.items()},
            'shot_bank_sha256': port.digest(DATA / 'tank_sounds.json'),
            'legacy_count': len(old), 'legacy_other_count': PRESERVED,
        })
    return records


def xml_of(vehicle):
    compat = COMPAT / (vehicle + '.xml')
    return compat if compat.exists() else XML / (vehicle + '.xml')


def vehicle_edges(client, vehicle):
    root = ET.parse(xml_of(vehicle)).getroot()
    for group, kind in [('chassis', 'chassis'), ('engines', 'engines'),
                        ('radios', 'radios'), ('turrets0', 'turrets')]:
        parent = root.find(group)
        for node in parent if parent is not None else []:
            for key, cost in base.unlock_map(client.merge(kind, node)).items():
                if key.startswith('vehicle:'):
                    yield {'from_id': vehicle, 'to_id': key.split(':', 1)[1], 'cost_xp': cost}
            if group == 'turrets0':
                for gun in node.find('guns'):
                    for key, cost in base.unlock_map(client.merge('guns', gun)).items():
                        if key.startswith('vehicle:'):
                            yield {'from_id': vehicle, 'to_id': key.split(':', 1)[1], 'cost_xp': cost}


# --------------------------------------------------------------------------
# Two-sided calibration
# --------------------------------------------------------------------------

def clamp(value, reference, low, high):
    if reference <= 0:
        return value
    return min(max(value, reference * low), reference * high)


def similar_guns(gun, pool):
    similar = [(p, g) for p, g in pool
               if abs(g['caliber'] - gun['caliber']) <= max(15, gun['caliber'] * .25)]
    return similar or pool


def scale_reload(card, gun, factor):
    gun['reload'] = round(gun['reload'] * factor, 6)
    if gun.get('autoreload_times'):
        gun['autoreload_times'] = [round(v * factor, 6) for v in gun['autoreload_times']]
    for turret in card['turrets']:
        for slot in turret['guns']:
            if slot['name'] == gun['name']:
                slot['reload'] = round(slot['reload'] * factor, 6)


def gun_dpm(gun, reload=None):
    """Sustained output; an auto-reloader may also wait for a full drum."""
    times = gun.get('autoreload_times')
    if times and gun['shells']:
        damage = gun['shells'][0]['dmg']
        gap = usa.firing_gap(gun)
        each = 60 * damage / max(times[0], gap, .01)
        drum = 60 * damage * len(times) / max(sum(times) + gap, .01)
        return max(each, drum)
    return usa.dpm(gun, reload)


def best_gun(card):
    """The strongest gun of the top turret, as mounted there."""
    mounted = usa.mounted_guns(card)
    gun = max(mounted, key=gun_dpm)
    return gun, gun_dpm(gun)


def metrics(card):
    result = usa.metrics(card)
    result['max_sustained_dpm'] = best_gun(card)[1]
    return result


def retime(card, gun, output):
    """Scale a gun's sustained output by ``output`` on every mount.

    The intraclip/intraburst gaps are authored timing and stay; only the
    magazine reload (or every auto-reload timer) changes.
    """
    if gun.get('autoreload_times'):
        scale_reload(card, gun, 1 / output)
        return
    gap = usa.firing_gap(gun)
    volley = 60 * gun['shells'][0]['dmg'] * usa.shots(gun) if gun['shells'] else 0

    def solve(reload):
        if volley <= 0:
            return reload
        current = volley / max(.01, reload + gap)
        return round(max(.5, volley / (current * output) - gap), 6)

    gun['reload'] = solve(gun['reload'])
    for turret in card['turrets']:
        for slot in turret['guns']:
            if slot['name'] == gun['name']:
                slot['reload'] = solve(slot['reload'])


def rebalance(card, peers):
    """Pull the top configuration into a band around the competitors.

    Bands (x median of same-tier/class researchable peers): total HP
    0.9-1.1, sustained DPM 0.9-1.1 with the HP x DPM product held in
    0.9-1.12, alpha/penetration 0.85-1.15 per shell type, dispersion and
    aim time 0.85-1.15, speeds 0.85-1.15, specific power 0.85-1.15, traverse
    0.85-1.15, view range 0.95-1.05, signal range 0.9-1.1. Every module of a
    group takes the factor of the top module, so the stock-to-top research
    progression the source authored stays intact. Geometry, armour plates,
    magazine sizes, shell kinds and module identities are never changed.
    """
    before = copy.deepcopy(card)
    peer_hp = usa.median([usa.total_health(p) for p in peers], usa.total_health(card))
    hp_target = clamp(usa.total_health(card), peer_hp, .9, 1.1)
    hp_factor = hp_target / max(1, usa.total_health(card))
    card['health'] = round(card['health'] * hp_factor)
    for turret in card['turrets']:
        turret['health'] = round(turret['health'] * hp_factor)

    for axis in ('x', 'y'):
        reference = usa.median([p['speed_limits'][axis] for p in peers], card['speed_limits'][axis])
        card['speed_limits'][axis] = round(clamp(card['speed_limits'][axis], reference, .85, 1.15), 6)

    for group, field, low, high in [('turrets', 'rot_speed', .85, 1.15),
                                    ('turrets', 'spot_radius', .95, 1.05),
                                    ('tracks', 'rot_speed', .85, 1.15),
                                    ('tracks', 'disp_move_mul', .85, 1.15),
                                    ('tracks', 'disp_rotation_mul', .85, 1.15),
                                    ('radios', 'distance', .9, 1.1)]:
        top = card[group][-1][field]
        reference = usa.median([p[group][-1][field] for p in peers], top)
        factor = clamp(top, reference, low, high) / top if top > 0 else 1.
        for module in card[group]:
            module[field] = round(module[field] * factor, 6)

    density = [p['engines'][-1]['power'] / usa.mass(p, p['engines'][-1]) for p in peers]
    own = card['engines'][-1]['power'] / usa.mass(card, card['engines'][-1])
    power_factor = clamp(own, usa.median(density, own), .85, 1.15) / max(1e-9, own)
    for engine in card['engines']:
        engine['power'] = int(round(engine['power'] * power_factor))

    pool = [(p, best_gun(p)[0]) for p in peers]
    top_name = best_gun(card)[0]['name']
    top = next(g for g in card['guns'] if g['name'] == top_name)
    similar = similar_guns(top, pool)
    gun_factors = {}
    for field in ('disp', 'aim_time'):
        reference = usa.median([g[field] for _, g in similar], top[field])
        gun_factors[field] = clamp(top[field], reference, .85, 1.15) / top[field] if top[field] > 0 else 1.
    shell_factors = {}
    # Alpha and penetration only compare like with like: guns of a similar
    # calibre, and single-shot against single-shot. A 57 mm magazine gun is
    # balanced through its sustained output, never by a heavy's alpha.
    calibre = [(p, g) for p, g in pool
               if abs(g['caliber'] - top['caliber']) <= max(15, top['caliber'] * .25)]
    for shell in top['shells']:
        same = [s for _, g in calibre for s in g['shells'] if s['type'] == shell['type']
                and (usa.shots(g) > 1) == (usa.shots(top) > 1)]
        for field in ('dmg', 'pen'):
            if len(same) < 2 or shell[field] <= 0:
                continue
            reference = usa.median([s[field] for s in same], shell[field])
            factor = clamp(shell[field], reference, .85, 1.15) / shell[field]
            # Never turn the gun into a different one: at most +-20 %.
            shell_factors[(shell['type'], field)] = min(1.2, max(.8, factor))
    for gun in card['guns']:
        for field, factor in gun_factors.items():
            gun[field] = round(gun[field] * factor, 6)
        for shell in gun['shells']:
            for field in ('dmg', 'pen'):
                factor = shell_factors.get((shell['type'], field))
                if factor:
                    shell[field] = max(1, int(round(shell[field] * factor)))
            if shell['speed'] <= 0 or shell['pen'] <= 0:
                raise ValueError(f"{card['name']}/{gun['name']}: invalid inherited ballistics")

    own_dpm = best_gun(card)[1]
    peer_dpm = usa.median([best_gun(p)[1] for p in peers], own_dpm)
    hp_ratio = usa.total_health(card) / max(1, peer_hp)
    dpm_target = clamp(own_dpm, peer_dpm, .9, 1.1)
    product = hp_ratio * dpm_target / peer_dpm
    if product > 1.12:
        dpm_target = peer_dpm * 1.12 / hp_ratio
    elif product < .9:
        dpm_target = peer_dpm * .9 / hp_ratio
    dpm_target = clamp(dpm_target, peer_dpm, .85, 1.15)
    # One output factor for every gun of the card keeps the stock guns behind
    # the top gun exactly as authored. Magazine and auto-reload guns trade
    # sustained output for burst, so they move less than single-shot guns.
    magazine = usa.shots(top) > 1 or bool(top.get('autoreload_times'))
    low, high = (.7, 1.25) if magazine else (.7, 1.45)
    output = min(high, max(low, dpm_target / max(.01, own_dpm)))
    for gun in card['guns']:
        retime(card, gun, output)
    reload_factor = 1 / output

    card['turret_health'] = card['turrets'][-1]['health']
    card['turret_rot_speed'] = card['turrets'][-1]['rot_speed']
    card['spot_radius'] = card['turrets'][-1]['spot_radius']
    return {'name': card['name'], 'level': card['lvl'], 'class': card['tank_class'],
            'competitors': [p['name'] for p in peers],
            'peer_medians': {'total_health': peer_hp, 'sustained_dpm': peer_dpm},
            'factors': {'health': hp_factor, 'power': power_factor, 'reload': reload_factor,
                        'gun': gun_factors,
                        'shells': {f'{t}:{f}': v for (t, f), v in shell_factors.items()}},
            'original_metrics': metrics(before), 'balanced_metrics': metrics(card),
            'original': before, 'balanced': copy.deepcopy(card)}


def reachable_modules(card):
    """Names of researchable modules no installed module leads to."""
    targets = set()
    for group in GROUPS:
        for module in card[group]:
            targets.update(module.get('unlocks', {}))
    for turret in card['turrets']:
        for slot in turret['guns']:
            targets.update(slot.get('unlocks', {}))
    kinds = {'turrets': 'turret', 'guns': 'gun', 'tracks': 'chassis',
             'engines': 'engine', 'radios': 'radio'}
    return [f'{kinds[g]}:{m["name"]}' for g in GROUPS for m in card[g]
            if m['cost_xp'] > 0 and f'{kinds[g]}:{m["name"]}' not in targets]


def build_cards(old, records, listing):
    base.PACK, base.VEHICLES = PACK, PACK / 'XML/item_defs/vehicles'
    english = yaml.safe_load((SOURCE / 'from_apk/Strings/en.yaml').read_text())
    russian = yaml.safe_load((SOURCE / 'from_apk/Strings/ru.yaml').read_text())
    strings = dict(english); strings.update(russian)
    client = port.NationClient1120(NATION, strings)
    names = {Path(c['source']).stem: c['name'] for c in old if is_other(c)}
    names.update({r['vehicle_id']: r['name'] for r in records})
    source_edges = [e for v in listing for e in vehicle_edges(client, v)]
    incoming = {}
    for edge in source_edges:
        incoming.setdefault(edge['to_id'], []).append(edge['cost_xp'])
    tree = yaml.safe_load((SOURCE / 'from_apk/Configs/TechTree/other_tree.yaml').read_text())
    tier_price = {lvl: statistics.median(c['cost'] for c in old if c.get('tree_tank')
                                         and not c.get('prem') and c['lvl'] == lvl)
                  for lvl in range(1, 11)}
    cards, balances = [], []
    for record in records:
        v = record['vehicle_id']
        card = base.build_card(client, {'id': v}, names)
        root = ET.parse(COMPAT / (v + '.xml')).getroot()
        if card['display_name'] == v:
            short = strings.get(listing[v].findtext('shortUserString', ''))
            card['display_name'] = short or FALLBACK_NAMES[v]
            card['display_name_policy'] = ('authored short name; full localization key absent'
                                           if short else '11.20 ships no string; base vehicle name')
        if card.get('display_name_short') in (None, '', v):
            card['display_name_short'] = card['display_name']
        tags = listing[v].findtext('tags', '').split()
        collector = 'collectible' in tags
        premium = listing[v].find('price/gold') is not None
        researched = v in tree['tanks'] and not premium and not collector
        seasonal = bool(season_costs(ET.parse(XML / (v + '.xml')).getroot()))
        card.update(source_version='11.20', source_overlay=OVERLAY,
                    source=record['xml_source'], source_sha256=record['xml_sha256'],
                    acquisition='collectible' if collector else ('' if researched else 'premium'),
                    prem=not researched, collector=collector, shop_tank=False, tree_tank=researched,
                    cost_gold=base.integer(listing[v].find('price')) if premium else 0,
                    collector_sell_gold=base.integer(listing[v].find('sellPrice')) if collector else 0,
                    tree_branch=tree['tanks'][v]['position'][1] - 6 if researched else 0)
        if seasonal:
            card['module_research_policy'] = 'Battle Pass points converted to combat XP'
        if not researched:
            # Bought vehicles never open a tech-tree successor, but keep their
            # authored module research (converted to XP where needed).
            card.update(cost=0, cost_xp=0, unlock_tanks={})
            for group in GROUPS:
                for module in card[group]:
                    module['unlocks'] = {k: x for k, x in module.get('unlocks', {}).items()
                                         if not k.startswith('vehicle:')}
            for turret in card['turrets']:
                for slot in turret['guns']:
                    slot['unlocks'] = {k: x for k, x in slot.get('unlocks', {}).items()
                                       if not k.startswith('vehicle:')}
        else:
            prices = incoming.get(v, [])
            if prices:
                card['cost_xp'] = min(prices)
            elif tree['tanks'][v]['position'][0] == 1:
                # The branch root: no predecessor, bought straight away.
                card['cost_xp'] = 0
            else:
                raise ValueError(f'{v}: missing source incoming research XP')
            if card['cost'] <= 0:
                card['cost'] = int(tier_price[card['lvl']])
                card['cost_policy'] = 'source price 0; median researchable price of the tier'
        stray = reachable_modules(card)
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
        balances.append(rebalance(card, peers_for(card, old)))
        cards.append(card)
    by_id = {r['vehicle_id'] for r in records}
    outside = [e for e in source_edges if e['to_id'] in by_id and e['from_id'] not in by_id]
    if outside:
        raise ValueError(f'Unexpected research link from a preserved card: {outside}')
    if sum(c['tree_tank'] for c in cards) != len(tree['tanks']):
        raise ValueError('Incomplete AC research branch')
    save(CARDS, {'version': '11.20', 'generator': 'Tools/port_blitz_other_1120.py',
                 'policy': 'every source module/configuration; module research kept for bought vehicles; season points converted to XP; two-sided same-tier/class calibration',
                 'additional_research_links': [], 'tanks': cards})
    save(BALANCE, {'baseline': '438 preserved Unity cards; non-premium same-tier/class competitors',
                   'policy': rebalance.__doc__.strip(),
                   'limitations': 'Deterministic stat-budget calibration, not a live PvP playtest; armour is source geometry and not calibrated',
                   'tanks': balances})
    print('Other cards:', len(cards), 'modules:', {k: sum(len(c[k]) for c in cards) for k in GROUPS},
          'configurations:', sum(len(t['guns']) for c in cards for t in c['turrets']), flush=True)
    return cards


def export_icons(records):
    atlas_path = DATA / 'atlas_regions.json'
    atlas = json.loads(atlas_path.read_text())
    textures = ROOT / 'Assets/Blitz/Resources/Textures'
    evidence = []

    def decode(original):
        payload = original.read_bytes()
        if original.suffix == '.dvpl':
            payload = port.dvpl.unpack_bytes(payload)
        image = Image.open(io.BytesIO(payload)).convert('RGBA')
        crop = image.getbbox()
        if not crop:
            raise ValueError(f'{original}: empty authored image')
        return image.crop(crop), crop

    for record in records:
        relative = record['resources']['bigIconPath'].removeprefix('~res:/')
        try:
            original = port.source_file(relative + '@2x.packed.webp')
        except FileNotFoundError:
            original = port.source_file(relative + '.packed.webp')
        image, crop = decode(original)
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
    # The tech tree gains the mixed nation's tab; 4.8 never had its flag.
    original = port.source_file('Gfx/Lobby/flags/flag_tank-node_other@2x.packed.webp')
    image, crop = decode(original)
    image.save(textures / 'hangar_nation8.png')
    atlas['textures']['hangar_nation8'] = {'width': image.width, 'height': image.height}
    atlas['regions']['hangar_nation8'] = {'texture': 'hangar_nation8', 'x': 0, 'y': 0,
                                          'w': image.width, 'h': image.height}
    evidence.append({'name': 'hangar_nation8', 'source': str(original.relative_to(SOURCE)),
                     'sha256': port.digest(original), 'crop': crop})
    save(atlas_path, atlas)
    save(DATA / 'blitz_other_1120_icons.json', {
        'version': '11.20', 'policy': 'authored Parameters icon paths; exact alpha crop; no resampling',
        'icons': evidence})


def export_crash(records):
    usa.XML = XML
    usa.export_crash(records)


def main():
    parser = argparse.ArgumentParser()
    for flag in ('prepare', 'models', 'icons', 'crash', 'merge', 'rebuild', 'resume'):
        parser.add_argument('--' + flag, action='store_true')
    parser.add_argument('--only', default='')
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
        pending = records
        if args.only:
            pending = [r for r in records if r['name'] in args.only.split(',')]
        if args.resume:
            manifest = json.loads((DATA / 'tank_models_source_manifest.json').read_text())['tanks']
            todo = []
            for record in pending:
                rid = port.exporter.unity_id(record['name'])
                folder = ROOT / 'Assets/Blitz/Resources/Models/Tanks' / rid
                entry = manifest.get(record['name'], {})
                complete = entry.get('source_version') == '11.20' and entry.get('source') == record['scene']
                complete = complete and all((folder / (rid + suffix + '.obj')).exists()
                                            for suffix in ('', '_armor', '_phys'))
                complete = complete and (DATA / (rid.lower() + '_nodes.json')).exists()
                if not complete:
                    todo.append(record)
            print('Resume: preserved completed models', len(pending) - len(todo), flush=True)
            pending = todo
        port.export_models(pending, cards, args.rebuild)
    if args.icons:
        export_icons(records)
    if args.crash:
        export_crash(records)
    if args.merge:
        document = merge_into(json.loads((DATA / 'tanks.json').read_text()))
        (DATA / 'tanks.json').write_text(json.dumps(document, ensure_ascii=False, indent=1, sort_keys=True) + '\n')
        print('Live cards:', len(document['tanks']), flush=True)


if __name__ == '__main__':
    main()
