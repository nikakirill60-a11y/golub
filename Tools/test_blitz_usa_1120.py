#!/usr/bin/env python3
"""Source fidelity, preservation and safe emergency-track import for USA 11.20."""
import copy
import hashlib
import json
import unittest
from unittest.mock import patch
from xml.etree import ElementTree as ET

import port_blitz_usa_1120 as usa
from blitz_usa_1120_registry import card_hash, merge_into


def read(name):
    return json.loads((usa.DATA / name).read_text())


def chassis(forward='0', backward='0.47'):
    return ET.fromstring(f'''<chassis><internalTrack>
        <limitation><track>left</track><forwardSpeedLimit>{forward}</forwardSpeedLimit>
            <backwardSpeedLimit>{backward}</backwardSpeedLimit></limitation>
        <limitation><track>right</track><forwardSpeedLimit>{forward}</forwardSpeedLimit>
            <backwardSpeedLimit>{backward}</backwardSpeedLimit></limitation>
    </internalTrack></chassis>''')


class Usa1120SourceTests(unittest.TestCase):
    def test_authored_modules_crews_and_internal_track_limits_are_exact(self):
        cards = {c['name']: c for c in read('blitz_usa_1120_cards.json')['tanks']}
        inventory = read('blitz_usa_1120_inventory.json')
        with patch.object(usa.base, 'PACK', usa.PACK), patch.object(
                usa.base, 'VEHICLES', usa.PACK / 'XML/item_defs/vehicles'):
            client = usa.port.NationClient1120('usa', {})
        emergencies = {}
        for record in inventory['records']:
            card = cards[record['name']]
            with self.subTest(tank=card['name']):
                source = usa.SOURCE / card['source']
                self.assertEqual(card['source_sha256'], hashlib.sha256(source.read_bytes()).hexdigest())
                root = ET.parse(source).getroot()
                self.assertEqual(usa.base.crew_roles(root), card['crew'])
                groups = {
                    'turrets': [x.tag for x in root.find('turrets0')],
                    'guns': list(dict.fromkeys(x.tag for x in root.findall('turrets0/*/guns/*'))),
                    'tracks': [x.tag for x in root.find('chassis')],
                    'engines': [x.tag for x in root.find('engines')],
                    'radios': [x.tag for x in root.find('radios')],
                }
                for group, names in groups.items():
                    self.assertEqual(names, record['modules'][group])
                    self.assertEqual(names, [m['name'] for m in card[group]])
                for node, track in zip(root.find('chassis'), card['tracks']):
                    authored = usa.parse_internal_track(client.merge('chassis', node))
                    self.assertEqual(authored, track.get('internal_track'))
                    if authored:
                        self.assertEqual(0, authored['forward_speed_limit'])
                        emergencies[card['name']] = authored['backward_speed_limit']
        self.assertEqual({'m-6-y': .47, 'm-5-y': .58, 'a141_m-4-y': .389,
                          'm-3-y-midas': .438, 'a125_aep_1': .47,
                          'a128_concept_1b': .438, 'a153_xm66f': .4}, emergencies)

    def test_all_396_previous_cards_models_and_the_sound_bank_are_preserved(self):
        protected = read('blitz_usa_1120_preservation.json')
        old = usa.baseline()
        self.assertEqual(396, len(old))
        self.assertEqual(protected['cards'], {c['name']: card_hash(c) for c in old})
        models = read('tank_models_source_manifest.json')['tanks']
        for name in protected['cards']:
            with self.subTest(tank=name):
                self.assertEqual(protected['model_entries'][name], card_hash(models[name]))
        self.assertEqual(protected['shot_bank_sha256'],
                         hashlib.sha256((usa.DATA / 'tank_sounds.json').read_bytes()).hexdigest())

    def test_repeated_overlay_merge_is_idempotent(self):
        live = read('tanks.json')
        first = merge_into(copy.deepcopy(live))
        self.assertEqual(live, first)
        self.assertEqual(first, merge_into(copy.deepcopy(first)))

    def test_live_manifests_and_hangar_atlas_counts_match_the_roster(self):
        cards = read('tanks.json')['tanks']
        live = {c['name'] for c in cards}
        self.assertEqual(live, set(read('tank_models_source_manifest.json')['tanks']))
        self.assertEqual({c['name'] for c in cards if c.get('source_version') == '11.20'},
                         set(read('legacy1120_crash_manifest.json')['vehicles']))
        atlas = read('atlas_regions.json')
        hangar = read('hangar_source_manifest.json')['atlas']
        self.assertEqual(len(atlas['regions']), hangar['regions'])
        self.assertEqual(len(atlas['textures']), hangar['textures'])
        self.assertTrue({'pzvi_ausftu', 'pzvi_ausftur'}.isdisjoint(live))
        self.assertIn('pzvi', live)

    def test_sheridan_missile_is_the_only_guided_new_usa_card(self):
        guided = [(c['name'], s) for c in read('blitz_usa_1120_cards.json')['tanks']
                  for g in c['guns'] for s in g['shells'] if s.get('atgm')]
        self.assertEqual(1, len(guided))
        name, shell = guided[0]
        self.assertEqual('a116_xm551_exp', name)
        self.assertEqual('blitz1120_marker', shell['atgm_visual'])
        self.assertAlmostEqual(.6, shell['turn_speed'])
        self.assertAlmostEqual(.01, shell['gravity'])

    def test_symmetric_emergency_limits_keep_both_source_sides(self):
        result = usa.parse_internal_track(chassis())
        self.assertEqual(0, result['forward_speed_limit'])
        self.assertEqual(.47, result['backward_speed_limit'])
        self.assertEqual(['left', 'right'], [r['track'] for r in result['limitations']])

    def test_absent_or_empty_internal_track_does_not_invent_an_emergency_track(self):
        self.assertIsNone(usa.parse_internal_track(ET.fromstring('<chassis/>')))
        self.assertIsNone(usa.parse_internal_track(ET.fromstring('<chassis><internalTrack/></chassis>')))

    def test_invalid_limits_are_rejected_instead_of_silently_clamped(self):
        for value in ('-0.1', '1.01', 'nan', 'inf', '-inf'):
            for direction in ('forward', 'backward'):
                with self.subTest(value=value, direction=direction):
                    node = chassis(forward=value) if direction == 'forward' else chassis(backward=value)
                    with self.assertRaisesRegex(ValueError, 'finite multipliers'):
                        usa.parse_internal_track(node)

    def test_asymmetric_and_one_sided_tracks_keep_exact_authored_limits(self):
        asymmetric = chassis()
        asymmetric.find('internalTrack/limitation/backwardSpeedLimit').text = '.58'
        result = usa.parse_internal_track(asymmetric)
        self.assertEqual(.58, result['limitations'][0]['backward_speed_limit'])
        self.assertEqual(.47, result['limitations'][1]['backward_speed_limit'])
        self.assertEqual(.47, result['backward_speed_limit'])
        one_sided = chassis()
        group = one_sided.find('internalTrack')
        group.remove(group[1])
        result = usa.parse_internal_track(one_sided)
        self.assertEqual(['left'], [r['track'] for r in result['limitations']])
        self.assertEqual(0, result['backward_speed_limit'])

    def test_unknown_duplicate_and_incomplete_track_sides_are_rejected(self):
        for side in ('left', 'middle', ''):
            node = chassis()
            node.find('internalTrack')[1].find('track').text = side
            with self.subTest(side=side), self.assertRaisesRegex(ValueError, 'unique left/right'):
                usa.parse_internal_track(node)
        missing = chassis()
        left = missing.find('internalTrack/limitation')
        left.remove(left.find('backwardSpeedLimit'))
        with self.assertRaisesRegex(ValueError, 'missing speed limit'):
            usa.parse_internal_track(missing)


if __name__ == '__main__':
    unittest.main()
