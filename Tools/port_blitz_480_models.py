#!/usr/bin/env python3
"""Export all researchable Blitz 4.8.0 vehicle assets in resumable batches.

This configures the proven branch exporter for each nation's exact
``blitzModelPath`` and ``collisionMesh``.  It writes one complete Unity payload
per card (visible OBJ, physics OBJ, armour OBJ/table, pivots and optional
textures) and merges progress into ``tank_models_source_manifest.json``.

Examples:
  python3 Tools/port_blitz_480_models.py --nation china --limit 1 --textures
  python3 Tools/port_blitz_480_models.py --nation china --textures
  python3 Tools/port_blitz_480_models.py --cards ms-1 usa-t28 --textures
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
from xml.etree import ElementTree as ET

import port_blitz_branch_models as exporter
import blitz_480_collision
from audit_blitz_480_tanks import NATIONS, PACK, UNITY, VEHICLES, text

RESOURCES = UNITY / 'Assets/Blitz/Resources'
INVENTORY = RESOURCES / 'Data/blitz_480_tank_inventory.json'
PREMIUM_INVENTORY = RESOURCES / 'Data/blitz_480_premium_inventory.json'
CARDS = RESOURCES / 'Data/tanks.json'
MANIFEST = RESOURCES / 'Data/tank_models_source_manifest.json'

# Premium, event and collector vehicles live in the same nation folders plus
# the client's crossover folder, which has no researchable tree at all.
ALL_NATIONS = list(NATIONS) + ['other']


def inventory_tanks():
    """(nation, tank) pairs of every vehicle this port covers."""
    pairs = []
    for path in (INVENTORY, PREMIUM_INVENTORY):
        if not path.exists():
            continue
        inventory = json.loads(path.read_text(encoding='utf-8'))
        for nation in ALL_NATIONS:
            data = inventory['nations'].get(nation)
            if not data:
                continue
            for tank in data['tanks']:
                pairs.append((nation, tank))
    return pairs


def records():
    cards = json.loads(CARDS.read_text(encoding='utf-8'))['tanks']
    by_source = {Path(card['source']).stem: card for card in cards}
    result = []
    for nation, tank in inventory_tanks():
            vehicle_id = tank['id']
            card = by_source.get(vehicle_id)
            if card is None or f'/vehicles/{nation}/' not in '/' + card['source']:
                # Same raw ids may theoretically exist in two nations; use the
                # full source path rather than silently selecting the wrong card.
                wanted = f'XML/item_defs/vehicles/{nation}/{vehicle_id}.xml'
                card = next((item for item in cards if item['source'] == wanted), None)
            if card is None:
                raise RuntimeError(f'generated card missing: {nation}/{vehicle_id}')
            root = ET.parse(VEHICLES / nation / f'{vehicle_id}.xml').getroot()
            model_path = text(root.find('blitz/blitzModelPath'))
            collision_path = text(root.find('blitz/collisionMesh'))
            if not model_path or not collision_path:
                raise RuntimeError(f'source paths missing: {nation}/{vehicle_id}')
            model = PACK / '3d' / model_path
            collision = PACK / '3d/Tanks' / collision_path
            if not model.is_file() or not collision.is_file():
                raise RuntimeError(f'source asset missing: {nation}/{vehicle_id}')
            result.append({
                'nation': nation,
                'vehicle_id': vehicle_id,
                'card': card,
                'model_dir': str(Path(model_path).parent.relative_to('Tanks')),
                'model_stem': Path(model_path).stem,
                'collision_stem': Path(collision_path).stem,
                'configurations': sum(len(turret['guns']) for turret in card['turrets']),
            })
    return result


def configure(record):
    nation = record['nation']
    card_name = record['card']['name']
    exporter_id = exporter.unity_id(card_name).lower()
    exporter.VEHICLES = VEHICLES / nation
    exporter.MODEL_DIR = record['model_dir']
    exporter.IMAGES = exporter.TANKS / exporter.MODEL_DIR / 'images'
    exporter.MODEL = {exporter_id: record['model_stem']}
    exporter.COLLISION = {exporter_id: record['collision_stem']}
    exporter.COLLISION_PREFIX = ''
    exporter.GLB_CACHE = Path(__file__).resolve().parent / '_branch_glb' / nation


def write_manifest(ported):
    manifest = {
        'generator': 'Tools/port_blitz_480_models.py',
        'policy': 'Original Blitz 4.8.0 client geometry and textures only.',
        'tanks': {},
    }
    if MANIFEST.exists():
        current = json.loads(MANIFEST.read_text(encoding='utf-8'))
        manifest['tanks'] = current.get('tanks', {})
    for name, entry in ported.items():
        previous = manifest['tanks'].get(name, {})
        entry['textures'] = entry['textures'] or previous.get('textures', [])
        manifest['tanks'][name] = entry
    MANIFEST.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + '\n',
        encoding='utf-8')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--nation', choices=ALL_NATIONS)
    parser.add_argument('--cards', nargs='*')
    parser.add_argument('--limit', type=int)
    parser.add_argument('--rebuild', action='store_true')
    parser.add_argument('--textures', action='store_true')
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()

    wanted = set(args.cards or [])
    selected = [record for record in records()
                if (not args.nation or record['nation'] == args.nation)
                and (not wanted or record['card']['name'] in wanted)]
    if args.limit is not None:
        selected = selected[:max(args.limit, 0)]
    if wanted:
        found = {record['card']['name'] for record in selected}
        missing = sorted(wanted - found)
        if missing:
            raise SystemExit('unknown or filtered cards: ' + ', '.join(missing))

    print(json.dumps({
        'tanks': len(selected),
        'configurations': sum(record['configurations'] for record in selected),
        'by_nation': {
            nation: sum(record['nation'] == nation for record in selected)
            for nation in ALL_NATIONS if any(record['nation'] == nation for record in selected)
        },
    }, sort_keys=True))
    if args.dry_run:
        return

    # Armour comes from the per-face thickness the 4.8.0 collision meshes
    # author, not from normal-zone guesses (see blitz_480_collision).
    blitz_480_collision.install()
    exporter.PLATE_SCREEN = blitz_480_collision.screen_of

    ported = {}
    for index, record in enumerate(selected, 1):
        card = record['card']
        print(f"[{index}/{len(selected)}] {record['nation']}/{card['name']} "
              f"({record['configurations']} configurations)")
        configure(record)
        ported[card['name']] = exporter.port_card(
            card, record['vehicle_id'], args.rebuild, args.textures)
        write_manifest(ported)


if __name__ == '__main__':
    main()
