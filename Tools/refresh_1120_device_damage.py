#!/usr/bin/env python3
"""Refresh only device damage in additive 11.20 cards, preserving balancing/XP."""
import json
from xml.etree import ElementTree as ET
import port_blitz_1120 as port
port.base.VEHICLES = port.PACK / 'XML/item_defs/vehicles'
clients = {}
updated = 0
for path in [port.DATA / 'tanks.json', port.CARD_FILE]:
    doc = json.loads(path.read_text())
    for card in doc['tanks']:
        if card.get('source_version') != '11.20': continue
        nation = card['source'].split('/')[-2]
        if nation not in clients: clients[nation] = port.NationClient1120(nation, {})
        client = clients[nation]
        root = ET.parse(client.directory / (card['source'].split('/')[-1])).getroot()
        guns = {}
        for turret in root.find('turrets0'):
            for gun in turret.find('guns'):
                guns[gun.tag] = client.shell_cards(client.merge('guns', gun))
        for gun in card['guns']:
            source = guns[gun['name']]
            assert len(source) == len(gun['shells'])
            for shell, authored in zip(gun['shells'], source):
                assert shell['caliber'] == authored['caliber'] and shell['type'] == authored['type']
                shell['dmg_devices'] = authored['dmg_devices']
                updated += 1
    path.write_text(json.dumps(doc, ensure_ascii=False, indent=(1 if path.name == "tanks.json" else 2), sort_keys=(path.name == "tanks.json")) + "\n")
print('Device-damage fields refreshed:', updated)
