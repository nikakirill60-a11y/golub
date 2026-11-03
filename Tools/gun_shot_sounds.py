#!/usr/bin/env python3
"""Give every gun card the shot report its client actually fires.

The porters used to guess ``shot_sound`` from the calibre (and forced every
gun with a single guided round onto the launcher report).  The client never
guesses: a gun names an ``<effects>`` entry - in the vehicle's own turret
block, otherwise in the shared ``components/guns.xml`` record - and
``common/gun_effects.xml`` maps that entry to a sound folder.  This module
resolves exactly that chain.

A card is checked against the 4.8.0 client first and against 11.20 only when
4.8.0 does not have the vehicle, so legacy tanks keep their legacy report.
Only the class is taken from the client: the audio itself stays the port's.

Run: python3 Tools/gun_shot_sounds.py [--check]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
from xml.etree import ElementTree as ET

UNITY = Path(__file__).resolve().parent.parent
DATA = UNITY / 'Assets/Blitz/Resources/Data'
PACK_480 = Path(os.environ.get(
    'BLITZ_PACK', Path.home() / 'projects/wotblitz4.8.0files'))
PACK_1120 = Path(os.environ.get(
    'BLITZ_1120_PACK', Path.home() / 'projects/wotblitz11.20.0files'))
VEHICLE_ROOTS = ('XML/item_defs/vehicles', 'from_apk/XML/item_defs/vehicles')

# ShootSound in Assets/Blitz/Core/Audio/TankSoundTable.cs.
NAMES = ('auto', 'small', 'medium', 'main', 'large', 'huge', 'atgm')
# 4.8.0 soundPC: guns/<folder>/<event>.
FOLDERS = {'gun_automatic': 0, 'gun_small': 1, 'gun_medium': 2,
           'gun_main': 3, 'gun_large': 4, 'gun_huge': 5, 'tow': 6}
# 11.20 soundPC: play_wpn_<size>[_extra]_<view>.
WWISE = {'auto': 0, 'small': 1, 'mid': 2, 'main': 3, 'large': 4,
         'huge': 5, 'huge_extra': 5}
CARD_FILES = ('tanks.json', 'blitz_1120_cards.json',
              'blitz_germany_1120_cards.json', 'blitz_other_1120_cards.json',
              'blitz_usa_1120_cards.json', 'blitz_japan_1120_cards.json',
              'blitz_branches_1120_cards.json', 'blitz_ussr_1120_cards.json')
# The 11.20 overlays pin every earlier card by hash. A corrected report is a
# deliberate change to such a card, so its pin moves with it.
PRESERVATION_FILES = ('blitz_germany_1120_preservation.json',
                      'blitz_other_1120_preservation.json',
                      'blitz_usa_1120_preservation.json',
                      'blitz_japan_1120_preservation.json',
                      'blitz_branches_1120_preservation.json',
                      'blitz_ussr_1120_preservation.json')


def card_hash(card: dict) -> str:
    # Same digest as the blitz_*_1120_registry.py preservation checks.
    # The tree row belongs to tech_tree_layout.py, so a re-layout is not a
    # card change.
    card = {key: value for key, value in card.items() if key != 'tree_branch'}
    payload = json.dumps(card, ensure_ascii=False, sort_keys=True,
                         separators=(',', ':')).encode('utf-8')
    return hashlib.sha256(payload).hexdigest()


def effect_class(sound_pc: str):
    parts = sound_pc.split('/')
    if len(parts) >= 3 and parts[1] in FOLDERS:
        return FOLDERS[parts[1]]
    if sound_pc.startswith('play_wpn_'):
        return WWISE.get(sound_pc[len('play_wpn_'):].rsplit('_', 1)[0])
    return None


_tables: dict = {}


def effect_table(vehicles: Path) -> dict:
    """gun_effects.xml entry -> ShootSound, None for reports the port lacks."""
    vehicles = Path(vehicles)
    if vehicles not in _tables:
        root = ET.fromstring((vehicles / 'common/gun_effects.xml').read_bytes())
        _tables[vehicles] = {
            entry.tag: effect_class((entry.findtext('.//soundPC') or '').strip())
            for entry in root}
    return _tables[vehicles]


def effects_key(gun) -> str:
    return ((gun.findtext('effects') if gun is not None else '') or '').strip()


def shot_sound_of(gun, vehicles: Path):
    """ShootSound of a merged (shared + vehicle override) gun node."""
    key = effects_key(gun)
    return effect_table(vehicles).get(key) if key else None


_shared: dict = {}


def shared_guns(nation: Path) -> dict:
    if nation not in _shared:
        root = ET.fromstring((nation / 'components/guns.xml').read_bytes())
        group = root.find('shared')
        _shared[nation] = {c.tag: c for c in (group if group is not None else [])}
    return _shared[nation]


def vehicle_gun_keys(path: Path) -> dict:
    """gun tag -> effects entry the vehicle fires it with.

    Like the porters, the last turret that mounts a gun wins (the stock and
    the top turret of a pzv disagree; the top one is the one that is played).
    An explicit empty ``<effects/>`` override names no report at all.
    """
    shared = shared_guns(path.parent)
    keys = {}
    for block in ET.fromstring(path.read_bytes()).iter():
        if not block.tag.startswith('turrets'):
            continue
        for turret in block:
            guns = turret.find('guns')
            for gun in (guns if guns is not None else []):
                local = gun.find('effects')
                keys[gun.tag] = (effects_key(gun) if local is not None
                                 else effects_key(shared.get(gun.tag)))
    return keys


def sources(card: dict):
    """(vehicles root, vehicle xml) of a card, 4.8.0 before 11.20."""
    tail = card.get('source', '').split('item_defs/vehicles/', 1)[-1]
    for pack in (PACK_480, PACK_1120):
        for prefix in VEHICLE_ROOTS:
            path = pack / prefix / tail
            if tail and path.is_file():
                yield pack / prefix, path


def source_shot_sound(card: dict, gun_name: str):
    """The client's report for one gun of a card, None when it has none.

    None (left to the card) also covers reports the port has no set for,
    like the duck's water guns and torpedo.
    """
    for vehicles, path in sources(card):
        keys = vehicle_gun_keys(path)
        if gun_name in keys:
            key = keys[gun_name]
            return effect_table(vehicles).get(key) if key else None
    return None


def retune(doc: dict, pins: dict) -> list:
    changes = []
    for card in doc['tanks']:
        before = card_hash(card)
        for gun in card.get('guns', []):
            sound = source_shot_sound(card, gun['name'])
            if sound is not None and sound != gun.get('shot_sound'):
                changes.append((card['name'], gun['name'],
                                NAMES[gun.get('shot_sound', 0)], NAMES[sound]))
                gun['shot_sound'] = sound
        after = card_hash(card)
        if after != before:
            pins[before] = after
    return changes


def repin(pins: dict, write: bool) -> None:
    for name in PRESERVATION_FILES:
        path = DATA / name
        if not path.exists():
            continue
        doc = json.loads(path.read_text(encoding='utf-8'))
        moved = 0
        for tank, digest in doc['cards'].items():
            if digest in pins:
                doc['cards'][tank] = pins[digest]
                moved += 1
        if moved:
            print(f'{name}: {moved} pinned card(s) follow the new reports')
            if write:
                path.write_text(json.dumps(doc, indent=2, ensure_ascii=False)
                                + '\n', encoding='utf-8')


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--check', action='store_true',
                        help='fail when a card differs instead of rewriting it')
    args = parser.parse_args()
    total = 0
    pins: dict = {}
    for name in CARD_FILES:
        path = DATA / name
        doc = json.loads(path.read_text(encoding='utf-8'))
        changes = retune(doc, pins)
        total += len(changes)
        for tank, gun, old, new in changes:
            print(f'{name}: {tank} {gun} {old} -> {new}')
        if changes and not args.check:
            # tanks.json is the 4.8.0 porter's one-space dump, the 11.20
            # tables are two-space dumps; keep each byte-compatible.
            indent = 1 if name == 'tanks.json' else 2
            path.write_text(json.dumps(doc, indent=indent, ensure_ascii=False)
                            + '\n', encoding='utf-8')
    repin(pins, not args.check)
    print(f'{total} gun report(s) {"differ" if args.check else "retuned"}')
    return 1 if args.check and total else 0


if __name__ == '__main__':
    raise SystemExit(main())
