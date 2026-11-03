#!/usr/bin/env python3
"""Source XML and synthetic regression gates for inherited 11.20 shots.

Run with .venv-texture/bin/python Tools/audit_blitz_1120_ballistics.py.
No source pack or shipped data is written by this audit.
"""
import json
import subprocess
from xml.etree import ElementTree as ET
import port_blitz_1120 as p


def merger_regressions():
    client = object.__new__(p.NationClient1120)
    client.nation = 'test'
    shared = ET.fromstring('<gun><shots><ap><speed>890</speed><piercingPower>250 200</piercingPower><maxDistance>720</maxDistance></ap><he><speed>730</speed></he></shots></gun>')
    client.shared = {'guns': {'gun': shared}}
    original = ET.tostring(shared)
    local = ET.fromstring('<gun><shots><he>shared<price>20</price></he><ap>shared<price>30</price></ap></shots></gun>')
    result = client.merge('guns', local)
    assert [n.tag for n in result.find('shots')] == ['he', 'ap']
    assert result.findtext('shots/ap/speed') == '890'
    assert result.findtext('shots/ap/piercingPower') == '250 200'
    assert result.findtext('shots/ap/price') == '30'
    assert result.findtext('shots/he/speed') == '730'
    local.find('shots/ap').append(ET.fromstring('<speed>920</speed>'))
    assert client.merge('guns', local).findtext('shots/ap/speed') == '920'
    assert ET.tostring(shared) == original, 'merging mutated shared XML'
    missing = ET.fromstring('<gun><shots><unknown>shared</unknown></shots></gun>')
    try:
        client.merge('guns', missing)
    except ValueError:
        pass
    else:
        raise AssertionError('missing ballistics must fail closed')


def audit():
    merger_regressions()
    records = json.loads(p.INVENTORY.read_text())['records']
    cards = {c['name']: c for c in json.loads(p.CARD_FILE.read_text())['tanks']}
    live_document = json.loads((p.DATA / 'tanks.json').read_text())
    live = {c['name']: c for c in live_document['tanks']}
    before = json.loads(subprocess.check_output(['git', 'show', 'HEAD:Assets/Blitz/Resources/Data/tanks.json']))
    assert [c for c in before['tanks'] if c.get('source_version') != '11.20'] == [c for c in live_document['tanks'] if c.get('source_version') != '11.20'], 'legacy cards changed'
    balance = {c['name']: c for c in json.loads(p.REPORT.read_text())['tanks']}
    count = 0
    mounted_count = 0
    for record in records:
        original = p.SOURCE / record['xml_source']
        assert p.digest(original) == record['xml_sha256'], record['name'] + ': source XML modified'
        root = ET.parse(original).getroot()
        shared_root = ET.parse(original.parent / 'components/guns.xml').getroot().find('shared')
        card = cards[record['name']]
        seen = set()
        for turret in root.find('turrets0'):
            for gun in turret.find('guns'):
                inherited = shared_root.find(gun.tag)
                local_shots = gun.find('shots')
                source_shots = local_shots if local_shots is not None else inherited.find('shots')
                shipped = next(g for g in card['guns'] if g['name'] == gun.tag)
                runtime = next(g for g in live[record['name']]['guns'] if g['name'] == gun.tag)
                source_card = next(g for g in balance[record['name']]['original']['guns'] if g['name'] == gun.tag)
                assert len(source_shots) == len(shipped['shells'])
                assert shipped['shells'] == runtime['shells']
                for index, shot in enumerate(source_shots):
                    # Independent read: local scalar wins, otherwise shared scalar.
                    speed = shot.find('speed')
                    if speed is None:
                        speed = inherited.find('shots/' + shot.tag + '/speed')
                    expected = int(float(speed.text.split()[0]))
                    assert expected > 0
                    assert shipped['shells'][index]['speed'] == expected, (record['name'], gun.tag, index)
                    for field, key, default in [('piercingPower', 'pen', 0), ('maxDistance', 'max_dist', 720)]:
                        scalar = shot.find(field)
                        if scalar is None:
                            scalar = inherited.find('shots/' + shot.tag + '/' + field)
                        value = p.base.number(scalar, default)
                        assert source_card['shells'][index].get(key, default) == value, (record['name'], gun.tag, key)
                    mounted_count += 1
                if gun.tag not in seen:
                    count += len(source_shots)
                    seen.add(gun.tag)
    assert count == 71, count
    print(f'PASS: synthetic inheritance/override/order/immutability/missing-speed gates; {len(cards)} tanks, {count} unique shell entries, {mounted_count} mounted shell configurations; source speeds match overlay + runtime; legacy cards unchanged.')


if __name__ == '__main__':
    audit()
