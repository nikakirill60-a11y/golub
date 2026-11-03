#!/usr/bin/env python3
"""Inventory every non-researchable vehicle of the Blitz 4.8.0 client.

``audit_blitz_480_tanks.py`` walks the ``unlocks/vehicle`` graph and therefore
covers only the regular tree.  Everything else in ``list.xml`` is a premium,
collectible or event vehicle.  The client marks them unambiguously:

* ``<price>N<gold/></price>`` and no ``notInShop`` -> sold for gold and drawn
  inside the nation tech tree (the yellow cards).
* ``<price>N<gold/></price>`` plus ``<notInShop>true</notInShop>`` -> owned by
  players but never offered in the shop; event / reward machines.
* the ``secret`` tag marks the collector vehicles the Godot build already had.

Acquisition is derived from that, never guessed:
  tree_premium  gold price, listed in the shop, shown in the tech tree
  premium       gold price, hidden from the shop, granted by events
  collectible   ``secret`` tag, collector rack only

Tutorial bots are duplicates of regular tanks and are skipped, as is the
explicitly excluded ``Oth10_WarDuck``.

Writes:
  Assets/Blitz/Resources/Data/blitz_480_premium_inventory.json
"""
from __future__ import annotations

import argparse
import json
from collections import defaultdict
from xml.etree import ElementTree as ET

from audit_blitz_480_tanks import (
    NATIONS, PACK, UNITY, VEHICLES, client_for, integer, module_inventory,
    strings_table, text,
)

OUT = UNITY / 'Assets/Blitz/Resources/Data/blitz_480_premium_inventory.json'

# ``other`` holds the crossover machines and has no researchable tree at all.
PREMIUM_NATIONS = {
    **{key: value[0] for key, value in NATIONS.items()},
    'other': 'Прочие',
}
CLASS_TAGS = ('lightTank', 'mediumTank', 'heavyTank', 'AT-SPG')
EXCLUDED = {'Oth10_WarDuck'}


def researchable_ids(nation):
    """Vehicle ids of the regular tree, read back from the tree inventory."""
    path = UNITY / 'Assets/Blitz/Resources/Data/blitz_480_tank_inventory.json'
    inventory = json.loads(path.read_text(encoding='utf-8'))
    data = inventory['nations'].get(nation)
    return {tank['id'] for tank in data['tanks']} if data else set()


def gold_price(listing):
    price = listing.find('price')
    if price is None or price.find('gold') is None:
        return 0
    return integer(price)


def classify(listing, tags):
    """Return (acquisition, gold) for a non-researchable vehicle."""
    gold = gold_price(listing)
    hidden = text(listing.find('notInShop')).strip().lower() == 'true'
    if 'secret' in tags:
        return 'collectible', gold
    if gold and not hidden:
        return 'tree_premium', gold
    return 'premium', gold


def build_inventory():
    strings = strings_table()
    result = {'client': '4.8.0', 'source': str(PACK), 'nations': {}, 'totals': {}}
    totals = defaultdict(int)

    for nation, nation_name in PREMIUM_NATIONS.items():
        directory, shared, listed = client_for(nation)
        researchable = researchable_ids(nation)
        vehicles = []
        for vehicle_id, listing in listed.items():
            if vehicle_id in researchable or vehicle_id in EXCLUDED:
                continue
            if 'tutorial_bot' in vehicle_id:
                continue
            path = directory / f'{vehicle_id}.xml'
            if not path.is_file():
                continue
            root = ET.parse(path).getroot()
            counts, _ = module_inventory(root, shared)
            tags = text(listing.find('tags')).split()
            acquisition, gold = classify(listing, tags)
            model_rel = text(root.find('blitz/blitzModelPath'))
            collision_rel = text(root.find('blitz/collisionMesh'))
            vehicles.append({
                'id': vehicle_id,
                'display_name': strings.get(text(listing.find('userString')), vehicle_id),
                'tier': integer(listing.find('level'), 1),
                'tank_class': next((tag for tag in tags if tag in CLASS_TAGS), 'unknown'),
                'acquisition': acquisition,
                'price_gold': gold,
                'in_tech_tree': acquisition == 'tree_premium',
                'is_collectible': acquisition == 'collectible',
                'tags': tags,
                'source_xml': str(path.relative_to(PACK)),
                'model': f'3d/{model_rel}' if model_rel else '',
                'collision': f'3d/Tanks/{collision_rel}' if collision_rel else '',
                'model_exists': bool(model_rel and (PACK / '3d' / model_rel).is_file()),
                'collision_exists': bool(collision_rel and (PACK / '3d/Tanks' / collision_rel).is_file()),
                'modules': counts,
            })

        vehicles.sort(key=lambda item: (item['tier'], item['display_name'].casefold(), item['id']))
        nation_totals = defaultdict(int)
        nation_totals['tanks'] = len(vehicles)
        for vehicle in vehicles:
            nation_totals[vehicle['acquisition']] += 1
            for key, value in vehicle['modules'].items():
                nation_totals[key] += value
        result['nations'][nation] = {
            'display_name': nation_name,
            'totals': dict(nation_totals),
            'tanks': vehicles,
        }
        for key, value in nation_totals.items():
            totals[key] += value

    result['totals'] = dict(totals)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    inventory = build_inventory()
    payload = json.dumps(inventory, ensure_ascii=False, indent=2, sort_keys=True) + '\n'
    if args.check:
        current = OUT.read_text(encoding='utf-8') if OUT.exists() else ''
        if current != payload:
            raise SystemExit('premium inventory is stale')
        print('premium inventory is up to date')
        return
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(payload, encoding='utf-8')
    print(json.dumps(inventory['totals'], ensure_ascii=False, sort_keys=True))


if __name__ == '__main__':
    main()
