#!/usr/bin/env python3
"""Generate complete researchable tank cards from the Blitz 4.8.0 client.

This is the all-nation successor to ``port_blitz_branch.py``.  It consumes the
canonical reachability inventory produced by ``audit_blitz_480_tanks.py`` and
writes the live ``tanks.json`` the game loads.  It used to write a staging
``tanks_480.json`` instead, which is why the 113 premium, event and collector
vehicles were invisible in the hangar long after they were generated: every
asset existed, but the table the runtime reads still held the 229 researchable
cards only.  There is no staging table any more - this porter owns the live
one, and it is the only writer of it.

Run: python3 Tools/port_blitz_480_data.py [--check]
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections import defaultdict
from pathlib import Path
from xml.etree import ElementTree as ET

import yaml

from audit_blitz_480_tanks import (
    NATIONS, PACK, STRINGS, UNITY, VEHICLES, merged, own_text, strings_table, text,
)
from gun_shot_sounds import shot_sound_of

INVENTORY = UNITY / 'Assets/Blitz/Resources/Data/blitz_480_tank_inventory.json'
PREMIUM_INVENTORY = UNITY / 'Assets/Blitz/Resources/Data/blitz_480_premium_inventory.json'
OUTPUT = UNITY / 'Assets/Blitz/Resources/Data/tanks.json'
TECH_TREE = PACK / 'from_apk/Data/UI/TechTree'

NATION_ID = {'ussr': 1, 'germany': 2, 'usa': 3, 'uk': 4, 'japan': 5, 'france': 6, 'china': 7, 'other': 8}
# Branches -5..+5 belong to the researchable grid. The client keeps premiums in
# separate rows under it (``premium_rows`` in the nation tech-tree YAML), so
# premium cards start right above the regular range.
FIRST_PREMIUM_BRANCH = 6
CLASS_ID = {'lightTank': 0, 'mediumTank': 1, 'heavyTank': 2, 'AT-SPG': 3}
SHELL_ID = {
    'ARMOR_PIERCING': 0,
    'ARMOR_PIERCING_CR': 1,
    'HOLLOW_CHARGE': 2,
    'HIGH_EXPLOSIVE': 3,
}


def number(node, default=0.0):
    value = text(node, '')
    try:
        return float(value.split()[0])
    except (ValueError, IndexError):
        return default


def integer(node, default=0):
    return int(round(number(node, default)))


# Crew roles of `<crew>`: the client nests the jobs one tankman doubles up on
# inside the tag of his main job, and `EXT_*`/`*_contused` name them in this
# order.  The damage panel of HUDLayer.yaml shows exactly these five.
CREW_ROLES = ('commander', 'driver', 'gunner', 'radioman', 'loader')


def crew_roles(root):
    """Every role the vehicle's crew covers, in client order.

    `<crew><commander>gunner radioman loader</commander><driver/></crew>`
    means one commander who is also the gunner, the radio operator and the
    loader, plus a dedicated driver.  The damage panel lights a slot per role,
    not per tankman, so both the tag names and the nested jobs count.
    """
    group = root.find('crew')
    found = []
    for member in (group if group is not None else []):
        for role in [member.tag] + own_text(member).split():
            role = role.strip().lower()
            if role in CREW_ROLES and role not in found:
                found.append(role)
    return [role for role in CREW_ROLES if role in found]


def health_block(node, tag=None):
    """`<maxHealth>/<maxRegenHealth>/<repairCost>` of one damageable module.

    The client hangs the block either straight on the module (`chassis`,
    `engines`) or in a named child of it (`hull/ammoBayHealth`,
    `turrets0/*/turretRotatorHealth`).  A module with no `maxHealth` at all
    returns None so the card does not grow an empty slot.
    """
    if node is None:
        return None
    source = node if tag is None else node.find(tag)
    if source is None or source.find('maxHealth') is None:
        return None
    return {
        'health': number(source.find('maxHealth')),
        'max_regen_health': number(source.find('maxRegenHealth')),
        'repair_cost': number(source.find('repairCost'), 1.0),
    }


def vector(node, default=(0.0, 0.0)):
    raw = text(node, '').split()
    try:
        values = [float(item) for item in raw]
    except ValueError:
        values = []
    return values if values else list(default)


def local_text(node, path, default=''):
    return text(node.find(path) if node is not None else None, default)


def unlock_map(node):
    result = {}
    block = node.find('unlocks') if node is not None else None
    for entry in (block if block is not None else []):
        target = own_text(entry)
        if not target:
            continue
        result[f'{entry.tag}:{target}'] = integer(entry.find('cost'))
    return result


def shot_sound(gun, vehicles, caliber, magazine):
    # The client names the report in the gun's <effects> entry; the calibre
    # guess is only for a gun whose entry the port has no report set for.
    # A guided round never retunes the whole gun: TankSoundManager fires it
    # with the launcher report on its own.
    sound = shot_sound_of(gun, vehicles)
    if sound is not None:
        return sound
    if magazine:
        return 0
    if caliber <= 45:
        return 1
    if caliber <= 76:
        return 2
    if caliber <= 100:
        return 3
    if caliber <= 130:
        return 4
    return 5


def resource_id(name):
    return ''.join(char.upper() for char in name if char.isalnum()) or 'MS1'


class NationClient:
    def __init__(self, nation, strings):
        self.nation = nation
        self.directory = VEHICLES / nation
        self.strings = strings
        self.shared = {}
        for kind in ('guns', 'turrets', 'chassis', 'engines', 'radios', 'fuelTanks'):
            root = ET.parse(self.directory / 'components' / f'{kind}.xml').getroot()
            group = root.find('shared')
            self.shared[kind] = {
                child.tag: child for child in (group if group is not None else [])
            }
        shell_root = ET.parse(self.directory / 'components/shells.xml').getroot()
        self.shells = {child.tag: child for child in shell_root}
        list_root = ET.parse(self.directory / 'list.xml').getroot()
        self.listing = {child.tag: child for child in list_root}

    def merge(self, kind, node):
        return merged(self.shared[kind].get(node.tag), node)

    def display(self, node, fallback):
        key = local_text(node, 'userString')
        return self.strings.get(key, fallback)

    def shell_cards(self, gun):
        result = []
        shots = gun.find('shots')
        for shot in (shots if shots is not None else []):
            info = self.shells.get(shot.tag)
            if info is None:
                continue
            price = info.find('price')
            premium = price is not None and price.find('gold') is not None
            cost = integer(price)
            caliber = integer(info.find('caliber'))
            card = {
                'kind': 'shell',
                'name': self.display(info, shot.tag),
                'caliber': caliber,
                'dmg': integer(info.find('damage/armor')),
                'dmg_devices': integer(info.find('damage/devices')),
                'pen': integer(shot.find('piercingPower')),
                'speed': integer(shot.find('speed')),
                'cost': cost * 400 if premium else cost,
                'type': SHELL_ID.get(local_text(info, 'kind'), 0),
            }
            max_distance = number(shot.find('maxDistance'), 720.0)
            if abs(max_distance - 720.0) > 0.001:
                card['max_dist'] = max_distance
            # Guided rockets replace ballistics with a near-zero gravity and a
            # turn rate the player steers with the crosshair.
            if local_text(shot, 'isATGM') == 'true':
                card['atgm'] = True
                card['turn_speed'] = number(shot.find('turnSpeed'), 0.0)
                card['gravity'] = number(shot.find('gravity'), 0.0)
                card['max_dist'] = max_distance
                # Native modern ATGM ammunition opts into the 11.20 marker/FX.
                # Legacy T49 Rocket hc/he_premium shells deliberately keep their trail.
                if local_text(info, 'icon') == 'atgm_heat':
                    card['atgm_visual'] = 'blitz1120_marker'
            ricochet = number(info.find('ricochetAngle'), 0.0)
            if ricochet > 0.0:
                card['ricochet_angle'] = ricochet
            # Every HIGH_EXPLOSIVE card of the pack authors its own blast
            # radius (the MGM-51C rocket carries 5.11 m); the client derives
            # the rest from the calibre, which is what the runtime falls back
            # to when the key is absent.
            explosion = number(info.find('explosionRadius'), 0.0)
            if explosion > 0.0:
                card['explosion_radius'] = explosion
            if premium:
                card['premium'] = True
            result.append(card)
        return result

    def gun_card(self, gun, yaw_limits=(-180.0, 180.0)):
        shells = self.shell_cards(gun)
        pitch = vector(gun.find('pitchLimits'), (-20.0, 8.0))
        # Blitz authors horizontal traverse on the turret, including the
        # fixed casemate used by TDs. Guns usually have no yawLimits at all;
        # treating that absence as ±180 made T82/JagdPz E 100 spin 360°.
        yaw = list(yaw_limits)
        while len(pitch) < 2:
            pitch.append(pitch[-1])
        while len(yaw) < 2:
            yaw.append(yaw[-1])
        clip = gun.find('clip')
        burst = gun.find('burst')
        magazine = clip is not None or burst is not None
        burst_card = {'kind': 'burst'}
        fire_mode = 0
        if clip is not None and burst is not None:
            clip_count = max(integer(clip.find('count'), 1), 1)
            burst_count = max(integer(burst.find('count'), 1), 1)
            clip_rate = number(clip.find('rate'), 1.0)
            burst_rate = number(burst.find('rate'), 1.0)
            groups, remainder = divmod(clip_count, burst_count)
            bursts = [burst_count] * groups
            if remainder:
                bursts.append(remainder)
            burst_card.update({
                'bursts': bursts,
                'intraclip': round(60.0 / clip_rate, 6) if clip_rate else 0.0,
                'intraburst': round(60.0 / burst_rate, 6) if burst_rate else 0.0,
            })
            fire_mode = 1
        elif clip is not None:
            count = max(integer(clip.find('count'), 1), 1)
            rate = number(clip.find('rate'), 1.0)
            burst_card.update({
                'bursts': [1] * count,
                'intraclip': round(60.0 / rate, 6) if rate else 0.0,
                'intraburst': 0.0,
            })
            fire_mode = 2
        elif burst is not None:
            count = max(integer(burst.find('count'), 1), 1)
            rate = number(burst.find('rate'), 1.0)
            burst_card.update({
                'bursts': [count],
                'intraclip': 0.0,
                'intraburst': round(60.0 / rate, 6) if rate else 0.0,
            })
            fire_mode = 1
        caliber = shells[0]['caliber'] if shells else 0
        return {
            'kind': 'gun',
            'name': gun.tag,
            'display_name': self.display(gun, gun.tag),
            'level': integer(gun.find('level'), 1),
            'cost_xp': 0,
            'health': number(gun.find('maxHealth')),
            'weight': number(gun.find('weight')),
            'reload': round(number(gun.find('reloadTime')), 6),
            'aim_time': round(number(gun.find('aimingTime'), 2.0), 6),
            'disp': round(number(gun.find('shotDispersionRadius')), 6),
            'rot_speed': number(gun.find('rotationSpeed')),
            'max_ammo': integer(gun.find('maxAmmo')),
            'caliber': caliber,
            'deviation_up': {'x': abs(pitch[0]), 'y': abs(pitch[1])},
            'deviation_r': {'x': abs(yaw[0]), 'y': abs(yaw[1])},
            'disp_factors': {
                'x': number(gun.find('shotDispersionFactors/turretRotation'), 0.1),
                'y': number(gun.find('shotDispersionFactors/afterShot'), 3.0),
                'z': number(gun.find('shotDispersionFactors/whileGunDamaged'), 2.0),
            },
            'impulse_mul': number(gun.find('impulse'), 1.0),
            'fire_mode': fire_mode,
            'burst_info': burst_card,
            'shot_sound': shot_sound(
                gun, self.directory.parent, caliber, magazine),
            'shells': shells,
        }


def module_price(edges, kind, name):
    values = [cost for key, cost in edges.items() if key == f'{kind}:{name}']
    return min(values) if values else 0


def build_card(client, tank, card_names, overrides=None):
    vehicle_id = tank['id']
    root = ET.parse(client.directory / f'{vehicle_id}.xml').getroot()
    listing = client.listing[vehicle_id]
    edges = {}
    guns = {}
    turrets = []

    turret_group = root.find('turrets0')
    for local_turret in (turret_group if turret_group is not None else []):
        turret = client.merge('turrets', local_turret)
        edges.update(unlock_map(turret))
        yaw = vector(turret.find('yawLimits'), (-180.0, 180.0))
        while len(yaw) < 2:
            yaw.append(yaw[-1])
        slots = []
        local_guns = local_turret.find('guns')
        for local_gun in (local_guns if local_guns is not None else []):
            gun = client.merge('guns', local_gun)
            edges.update(unlock_map(gun))
            gun_card = client.gun_card(gun, yaw)
            if local_gun.tag in guns:
                # A shared gun must obey every compatible turret. Keep the
                # narrowest authored arc; a full turret never widens a casemate.
                previous = guns[local_gun.tag]['deviation_r']
                gun_card['deviation_r']['x'] = min(
                    previous['x'], gun_card['deviation_r']['x'])
                gun_card['deviation_r']['y'] = min(
                    previous['y'], gun_card['deviation_r']['y'])
            guns[local_gun.tag] = gun_card
            slots.append({
                'name': local_gun.tag,
                'reload': round(number(gun.find('reloadTime')), 6),
                'max_ammo': integer(gun.find('maxAmmo')),
                'mesh': local_text(local_gun, 'blitz/blitzPartName'),
                'unlocks': unlock_map(gun),
            })
        turrets.append({
            'kind': 'turret',
            'name': local_turret.tag,
            'display_name': client.display(turret, local_turret.tag),
            'level': integer(turret.find('level'), 1),
            'cost_xp': 0,
            'health': number(turret.find('maxHealth')),
            'weight': number(turret.find('weight')),
            'rot_speed': number(turret.find('rotationSpeed')),
            'spot_radius': number(turret.find('circularVisionRadius')),
            'yaw_limits': {'x': abs(yaw[0]), 'y': abs(yaw[1])},
            # Two damageable sub-modules live inside the turret record: the
            # traverse gear and the observation device.
            'turret_rotator': health_block(turret, 'turretRotatorHealth'),
            'surveying_device': health_block(turret, 'surveyingDeviceHealth'),
            'mesh': local_text(local_turret, 'blitz/blitzPartName'),
            'unlocks': unlock_map(turret),
            'guns': slots,
        })

    tracks = []
    group = root.find('chassis')
    for local in (group if group is not None else []):
        node = client.merge('chassis', local)
        edges.update(unlock_map(node))
        tracks.append({
            'kind': 'tracks', 'name': local.tag,
            'display_name': client.display(node, local.tag),
            'level': integer(node.find('level'), 1), 'cost_xp': 0,
            'health': number(node.find('maxHealth')), 'weight': number(node.find('weight')),
            'rot_speed': number(node.find('rotationSpeed')),
            'disp_move_mul': number(node.find('shotDispersionFactors/vehicleMovement'), 0.2),
            'disp_rotation_mul': number(node.find('shotDispersionFactors/vehicleRotation'), 0.2),
            # The track module repairs itself back to `maxRegenHealth`, not to
            # full, and `bulkHealthFactor` is what a ram spends against it.
            'max_regen_health': number(node.find('maxRegenHealth')),
            'repair_cost': number(node.find('repairCost'), 1.0),
            'bulk_health_factor': number(node.find('bulkHealthFactor'), 1.0),
            'unlocks': unlock_map(node),
        })

    engines = []
    group = root.find('engines')
    for local in (group if group is not None else []):
        node = client.merge('engines', local)
        edges.update(unlock_map(node))
        engines.append({
            'kind': 'engine', 'name': local.tag,
            'display_name': client.display(node, local.tag),
            'level': integer(node.find('level'), 1), 'cost_xp': 0,
            'health': number(node.find('maxHealth')), 'weight': number(node.find('weight')),
            'power': number(node.find('power')),
            'fireStartingChance': number(node.find('fireStartingChance')),
            'max_regen_health': number(node.find('maxRegenHealth')),
            'repair_cost': number(node.find('repairCost'), 1.0),
            'unlocks': unlock_map(node),
        })

    # Fuel tanks are not researchable in Blitz - the hull mounts exactly one
    # shared record - but the tank carries its own damageable module.
    fuel_tank = None
    group = root.find('fuelTanks')
    for local in (group if group is not None else []):
        fuel_tank = health_block(client.merge('fuelTanks', local))
        break

    radios = []
    group = root.find('radios')
    for local in (group if group is not None else []):
        node = client.merge('radios', local)
        edges.update(unlock_map(node))
        radios.append({
            'kind': 'radio', 'name': local.tag,
            'display_name': client.display(node, local.tag),
            'level': integer(node.find('level'), 1), 'cost_xp': 0,
            'health': number(node.find('maxHealth')), 'weight': number(node.find('weight')),
            'distance': number(node.find('distance')),
            'unlocks': unlock_map(node),
        })

    for module in turrets:
        module['cost_xp'] = module_price(edges, 'turret', module['name'])
    for module in tracks:
        module['cost_xp'] = module_price(edges, 'chassis', module['name'])
    for module in engines:
        module['cost_xp'] = module_price(edges, 'engine', module['name'])
    for module in radios:
        module['cost_xp'] = module_price(edges, 'radio', module['name'])
    for module in guns.values():
        module['cost_xp'] = module_price(edges, 'gun', module['name'])

    ordered_guns = []
    for turret in turrets:
        for slot in turret['guns']:
            if slot['name'] not in ordered_guns:
                ordered_guns.append(slot['name'])

    successors = {}
    for key, cost in edges.items():
        if not key.startswith('vehicle:'):
            continue
        target = key.split(':', 1)[1]
        if target in card_names:
            successors[card_names[target]] = cost

    speed = root.find('speedLimits')
    top_turret = turrets[-1] if turrets else {}
    tags = text(listing.find('tags')).split()
    tank_class = next((CLASS_ID[tag] for tag in tags if tag in CLASS_ID), 0)
    source = client.directory / f'{vehicle_id}.xml'
    name = card_names[vehicle_id]
    card = {
        'name': name,
        'display_name': client.display(listing, vehicle_id),
        'display_name_short': client.strings.get(local_text(listing, 'shortUserString'), client.display(listing, vehicle_id)),
        'health': integer(root.find('hull/maxHealth')),
        'lvl': integer(listing.find('level'), 1),
        'tank_class': tank_class,
        'nation': NATION_ID[client.nation],
        'spot_radius': top_turret.get('spot_radius', 0.0),
        'speed_limits': {
            'x': number(speed.find('forward') if speed is not None else None),
            'y': number(speed.find('backward') if speed is not None else None),
        },
        'turret_rot_speed': top_turret.get('rot_speed', 0.0),
        'turret_health': top_turret.get('health', 0.0),
        'repair_cost': number(root.find('repairCost')),
        # The ammo rack hangs off the hull, so it is the only damageable module
        # that is not researchable and lives on the card itself.
        'ammo_bay': health_block(root.find('hull'), 'ammoBayHealth'),
        'fuel_tank': fuel_tank,
        'crew': crew_roles(root),
        'crew_xp_factor': number(root.find('crewXpFactor'), 1.0),
        'farm_coeff': 1.0,
        'tree_tank': True,
        'cost': integer(listing.find('price')),
        'cost_xp': 0,
        'hull_weight': number(root.find('hull/weight')),
        'model_path': local_text(root, 'blitz/blitzModelPath'),
        'source': str(source.relative_to(PACK)),
        'source_sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
        'unlock_tanks': successors,
        'guns': [guns[name] for name in ordered_guns],
        'turrets': turrets,
        'tracks': tracks,
        'engines': engines,
        'radios': radios,
    }
    if overrides:
        card.update(overrides)
    return card


def tree_positions(nation):
    path = TECH_TREE / f'{nation}_tree.yaml'
    document = yaml.safe_load(path.read_text(encoding='utf-8-sig'))
    tanks = document.get('tanks', {}) if document else {}
    result = {}
    for vehicle_id, entry in tanks.items():
        position = entry.get('position', [])
        if len(position) != 2:
            raise RuntimeError(f'Invalid tech-tree position for {nation}/{vehicle_id}')
        result[str(vehicle_id)] = (int(position[0]), int(position[1]))
    return result


def build_premiums(strings, seen_resources, tree_cards):
    """Cards for premium, event and collector vehicles.

    They have no research graph: every module ships installed, nothing costs
    XP and nothing unlocks a successor. ``tree_premium`` machines still belong
    to the nation tech tree and get a slot in the premium rows below it.
    """
    inventory = json.loads(PREMIUM_INVENTORY.read_text(encoding='utf-8'))
    occupied = {(card['nation'], card['lvl'], card['tree_branch']) for card in tree_cards}
    result = []

    for nation, data in inventory['nations'].items():
        if not data['tanks']:
            continue
        client = NationClient(nation, strings)
        ids = [tank['id'] for tank in data['tanks']]
        card_names = {vehicle_id: vehicle_id.lower() for vehicle_id in ids}
        for vehicle_id in ids:
            if resource_id(card_names[vehicle_id]) in seen_resources:
                card_names[vehicle_id] = f'{nation}-{vehicle_id.lower()}'
            rid = resource_id(card_names[vehicle_id])
            if rid in seen_resources:
                raise RuntimeError(f'duplicate resource id: {nation}/{vehicle_id}')
            seen_resources[rid] = (nation, vehicle_id)

        for tank in data['tanks']:
            acquisition = tank['acquisition']
            # The card table already speaks the Godot vocabulary: ``prem``,
            # ``shop_tank`` and ``collector`` are what TankInfo reads, so the
            # acquisition class is expressed through those flags instead of a
            # parallel set of fields.
            overrides = {
                'tree_tank': acquisition == 'tree_premium',
                'acquisition': acquisition,
                'prem': True,
                'shop_tank': acquisition == 'tree_premium',
                'collector': acquisition == 'collectible',
                'cost': 0,
                'cost_gold': tank['price_gold'],
                'cost_xp': 0,
                'unlock_tanks': {},
                'tree_branch': 0,
            }
            card = build_card(client, tank, card_names, overrides)
            if acquisition == 'tree_premium':
                branch = FIRST_PREMIUM_BRANCH
                while (card['nation'], card['lvl'], branch) in occupied:
                    branch += 1
                occupied.add((card['nation'], card['lvl'], branch))
                card['tree_branch'] = branch
            result.append(card)

    result.sort(key=lambda card: (card['nation'], card['lvl'], card['display_name'].casefold()))
    return result


def build():
    inventory = json.loads(INVENTORY.read_text(encoding='utf-8'))
    strings = strings_table()
    document = {'version': '4.8.0', 'source': str(PACK), 'tanks': []}
    seen_resources = {}

    for nation in NATIONS:
        data = inventory['nations'][nation]
        ids = [tank['id'] for tank in data['tanks']]
        card_names = {vehicle_id: vehicle_id.lower() for vehicle_id in ids}
        for vehicle_id in ids:
            rid = resource_id(card_names[vehicle_id])
            if rid in seen_resources:
                card_names[vehicle_id] = f'{nation}-{vehicle_id.lower()}'
            seen_resources[resource_id(card_names[vehicle_id])] = (nation, vehicle_id)
        client = NationClient(nation, strings)
        cards = [build_card(client, tank, card_names) for tank in data['tanks']]
        positions = tree_positions(nation)
        for tank, card in zip(data['tanks'], cards):
            vehicle_id = tank['id']
            if vehicle_id not in positions:
                raise RuntimeError(f'Missing 4.8 tech-tree position: {nation}/{vehicle_id}')
            level, row = positions[vehicle_id]
            if level != card['lvl'] or row < 1 or row > 11:
                raise RuntimeError(
                    f'Invalid 4.8 tech-tree position: {nation}/{vehicle_id} '
                    f'{positions[vehicle_id]} level={card["lvl"]}')
            # Godot's eleven slots are branches -5..+5. The client YAML uses
            # one-based rows 1..11, so row six is the centred branch zero.
            card['tree_branch'] = row - 6
        cards.sort(key=lambda card: (card['lvl'], card['display_name'].casefold(), card['name']))
        document['tanks'].extend(cards)

    document['tanks'].extend(build_premiums(strings, seen_resources, document['tanks']))

    incoming = defaultdict(list)
    for card in document['tanks']:
        for target, cost in card['unlock_tanks'].items():
            incoming[target].append(cost)
    for card in document['tanks']:
        values = incoming.get(card['name'], [])
        card['cost_xp'] = min(values) if values else 0

    document['tanks'].sort(key=lambda card: (card['nation'], card['lvl'], card['display_name'].casefold()))
    return document


def validate(document):
    tree_totals = json.loads(INVENTORY.read_text(encoding='utf-8'))['totals']
    premium_totals = json.loads(PREMIUM_INVENTORY.read_text(encoding='utf-8'))['totals']
    expected = {
        key: tree_totals.get(key, 0) + premium_totals.get(key, 0)
        for key in ('tanks', 'turrets', 'guns', 'chassis', 'engines', 'radios')
    }
    cards = document['tanks']
    assert len(cards) == expected['tanks'], (len(cards), expected['tanks'])
    premiums = [card for card in cards if card.get('prem')]
    assert len(premiums) == premium_totals['tanks'], (len(premiums), premium_totals['tanks'])
    by_acquisition = defaultdict(int)
    for card in premiums:
        assert card['acquisition'] in ('tree_premium', 'premium', 'collectible'), card['name']
        assert card['cost_xp'] == 0 and not card['unlock_tanks'], card['name']
        assert card['collector'] == (card['acquisition'] == 'collectible'), card['name']
        assert card['shop_tank'] == (card['acquisition'] == 'tree_premium'), card['name']
        assert card['tree_tank'] == (card['acquisition'] == 'tree_premium'), card['name']
        by_acquisition[card['acquisition']] += 1
    for key, value in by_acquisition.items():
        assert value == premium_totals[key], (key, value, premium_totals[key])
    assert len({card['name'] for card in cards}) == len(cards)
    assert len({resource_id(card['name']) for card in cards}) == len(cards)
    totals = {
        'turrets': sum(len(card['turrets']) for card in cards),
        'guns': sum(len(card['guns']) for card in cards),
        'chassis': sum(len(card['tracks']) for card in cards),
        'engines': sum(len(card['engines']) for card in cards),
        'radios': sum(len(card['radios']) for card in cards),
    }
    for key, value in totals.items():
        assert value == expected[key], (key, value, expected[key])
    for card in cards:
        assert card['turrets'] and card['guns'] and card['tracks'] and card['engines'] and card['radios']
        for turret in card['turrets']:
            assert turret['mesh'], (card['name'], turret['name'])
            assert turret['guns'], (card['name'], turret['name'])
            for slot in turret['guns']:
                assert slot['mesh'], (card['name'], turret['name'], slot['name'])
    occupied = set()
    for card in cards:
        # Event and collector vehicles never appear in the tech-tree grid, so
        # their branch index carries no meaning and cannot collide.
        if not card['tree_tank']:
            continue
        key = (card['nation'], card['lvl'], card['tree_branch'])
        assert key not in occupied, ('duplicate tech-tree slot', key, card['name'])
        occupied.add(key)
    return totals


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    document = build()
    totals = validate(document)
    from blitz_1120_registry import merge_into
    document = merge_into(document)
    import tech_tree_layout
    document = tech_tree_layout.apply(document)
    totals = {key: sum(len(card[field]) for card in document['tanks'])
              for key, field in [('turrets', 'turrets'), ('guns', 'guns'),
                                 ('chassis', 'tracks'), ('engines', 'engines'), ('radios', 'radios')]}
    payload = json.dumps(document, ensure_ascii=False, indent=1, sort_keys=True) + '\n'
    if args.check:
        current = OUTPUT.read_text(encoding='utf-8') if OUTPUT.exists() else ''
        if current != payload:
            raise SystemExit('tanks.json is stale')
        print('tanks.json is up to date')
    else:
        OUTPUT.write_text(payload, encoding='utf-8')
        print(f'wrote {OUTPUT}')
    print(json.dumps({'tanks': len(document['tanks']), **totals}, sort_keys=True))


if __name__ == '__main__':
    main()
