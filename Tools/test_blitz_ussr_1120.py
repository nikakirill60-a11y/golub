"""Independent source, balance, preservation and repeatability checks for Object 907."""
import copy
import hashlib
import json
import math
import statistics
import unittest
from unittest.mock import patch
from xml.etree import ElementTree as ET

import port_blitz_ussr_1120 as port
import blitz_ussr_1120_registry as registry
from blitz_object907_balance import calibrate, benchmark


def read(name):
    return json.loads((port.DATA / (name + '.json')).read_text())


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class Object907Tests(unittest.TestCase):
    def setUp(self):
        self.live = read('tanks')
        self.entry = read('blitz_ussr_1120_balance')['tanks'][0]
        self.card = next(c for c in self.live['tanks'] if c['name'] == 'object_907')
        self.peers = [c for c in self.live['tanks'] if c['name'] != 'object_907']
        self.preserved_peers = [c for c in self.peers
                                if c.get('source_overlay') != 'mark_i_male_1120']

    def test_live_card_matches_full_balanced_snapshot_and_sidecar(self):
        self.assertEqual(self.card, self.entry['balanced'])
        self.assertEqual([self.card], read('blitz_ussr_1120_cards')['tanks'])
        self.assertEqual(522, len(self.live['tanks']))
        self.assertEqual(522, len({c['name'] for c in self.live['tanks']}))

    def test_all_source_hashes_and_module_identities_are_verified(self):
        record = read('blitz_ussr_1120_inventory')['records'][0]
        for key in ('xml', 'parameter', 'scene', 'geometry', 'collision', 'collision_geometry'):
            self.assertEqual(record[key + '_sha256'], digest(port.SOURCE / record[key + '_source']), key)
        for texture in record['textures']:
            self.assertEqual(texture['sha256'], digest(port.SOURCE / texture['source']))
        source = ET.parse(port.SOURCE / self.card['source']).getroot()
        self.assertEqual(port.base.crew_roles(source), self.card['crew'])
        for group, names in record['modules'].items():
            self.assertEqual(names, [m['name'] for m in self.card[group]])
            self.assertEqual(1, len(names), group)
        self.assertEqual('turret_01', self.card['turrets'][0]['mesh'])
        self.assertEqual('gun_01', self.card['turrets'][0]['guns'][0]['mesh'])

    def test_all_520_cards_models_and_sound_bank_are_unchanged(self):
        protected = read('blitz_ussr_1120_preservation')
        self.assertEqual(520, len(protected['cards']))
        self.assertEqual(protected['cards'], {c['name']: registry.card_hash(c) for c in self.preserved_peers})
        models = read('tank_models_source_manifest')['tanks']
        self.assertEqual(set(models), {c['name'] for c in self.live['tanks']})
        for name, expected in protected['model_entries'].items():
            self.assertEqual(expected, registry.card_hash(models[name]), name)
        self.assertEqual(protected['shot_bank_sha256'], digest(port.DATA / 'tank_sounds.json'))

    def test_only_explicit_reload_and_mobility_fields_differ_from_source(self):
        source = copy.deepcopy(self.entry['original'])
        source['guns'][0]['reload'] = 7.1
        source['turrets'][0]['guns'][0]['reload'] = 7.1
        source['engines'][0]['power'] = 746
        source['speed_limits']['y'] = 23
        self.assertEqual(source, self.card)
        self.assertEqual(1800, self.card['health'] + self.card['turrets'][0]['health'])
        self.assertEqual([320, 270, 420], [s['dmg'] for s in self.card['guns'][0]['shells']])
        self.assertEqual([248, 300, 50], [s['pen'] for s in self.card['guns'][0]['shells']])

    def test_independent_mounted_single_shot_median_matches_budget(self):
        peers = [c for c in self.peers if c['lvl'] == 10 and c['tank_class'] == 1
                 and c.get('tree_tank') and not c.get('prem') and not c.get('collector')]
        values, names = [], []
        for peer in peers:
            mount = peer['turrets'][-1]['guns'][-1]
            gun = next(g for g in peer['guns'] if g['name'] == mount['name'])
            if gun.get('autoreload_times') or (sum(gun.get('burst_info', {}).get('bursts', [])) or 1) > 1:
                continue
            values.append(60 * gun['shells'][0]['dmg'] / mount.get('reload', gun['reload']))
            names.append(peer['name'])
        self.assertEqual(sorted(names), self.entry['single_shot_competitors'])
        self.assertEqual(8, len(names))
        median = statistics.median(values)
        self.assertAlmostEqual(median, self.entry['limits']['single_shot_dpm_median'])
        self.assertLessEqual(60 * 320 / 7.1, median)
        self.assertGreater(60 * 320 / 7.0, median)

    def test_independent_installed_power_and_reverse_limits(self):
        c = self.card
        mass = c['hull_weight'] + sum(c[k][-1]['weight'] for k in ('turrets', 'guns', 'tracks', 'engines', 'radios'))
        self.assertEqual(35700, mass)
        power = c['engines'][0]['power'] * 1000 / mass
        self.assertAlmostEqual(power, self.entry['balanced_metrics']['power_per_ton'])
        self.assertLessEqual(power, self.entry['limits']['max_power_per_ton'])
        self.assertEqual({'x': 60, 'y': 23}, c['speed_limits'])

    def test_calibration_is_deterministic_and_does_not_modify_competitors(self):
        peers = copy.deepcopy(self.peers)
        source = copy.deepcopy(self.entry['original'])
        result = calibrate(source, peers)
        self.assertEqual(self.entry, result)
        self.assertEqual(self.peers, peers)
        self.assertEqual(self.card, source)
        # Already-calibrated inputs never gain DPM or engine power.
        second = copy.deepcopy(source)
        calibrate(second, peers)
        self.assertEqual(source, second)

    def test_benchmark_excludes_collectibles_and_magazines_from_single_shots(self):
        peers, singles, _ = benchmark(self.peers)
        self.assertEqual(10, len(peers))
        self.assertEqual(8, len(singles))
        self.assertNotIn('f119_projet_murat', [c['name'] for c in singles])
        self.assertNotIn('it08_progetto_m40_mod65', [c['name'] for c in singles])
        self.assertTrue(all(not c.get('collector') and not c.get('prem') for c in peers))

    def test_invalid_profile_insufficient_or_nonfinite_peers_fail_closed(self):
        card = copy.deepcopy(self.card)
        card['name'] = 't62a'
        with self.assertRaises(ValueError):
            calibrate(card, self.peers)
        with self.assertRaises(ValueError):
            benchmark([])
        peers = copy.deepcopy(self.peers)
        next(c for c in peers if c['name'] == 't62a')['speed_limits']['x'] = math.nan
        with self.assertRaises(ValueError):
            benchmark(peers)

    def test_nonfinite_source_and_missing_mount_fail_without_mutating_peers(self):
        for invalid in ('reload', 'power', 'mount'):
            card = copy.deepcopy(self.entry['original'])
            if invalid == 'reload':
                card['guns'][0]['reload'] = math.nan
            elif invalid == 'power':
                card['engines'][0]['power'] = math.inf
            else:
                card['turrets'][0]['guns'] = []
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                calibrate(card, self.peers)

    def test_independent_geometry_audit_covers_full_source(self):
        audit = read('blitz_ussr_1120_geometry_audit')
        self.assertEqual([], audit['errors'])
        row = audit['tanks'][0]
        self.assertEqual('object_907', row['name'])
        self.assertEqual(6820, row['visible_triangles'])
        self.assertEqual(row['visible_triangles'], row['source_triangles'])
        self.assertEqual(22, row['wheels'])
        self.assertEqual(1082, row['collision_triangles'])
        self.assertEqual(32, row['exact_plates'])

    def test_registry_repeated_merge_is_idempotent(self):
        first = registry.merge_into(copy.deepcopy(self.live))
        self.assertEqual(self.live, first)
        self.assertEqual(first, registry.merge_into(copy.deepcopy(first)))

    def test_registry_rejects_foreign_identity_collision_and_baseline_drift(self):
        collision = copy.deepcopy(self.live)
        next(c for c in collision['tanks'] if c['name'] == 'object_907').pop('source_overlay')
        with self.assertRaises(ValueError):
            registry.merge_into(collision)
        drift = copy.deepcopy(self.live)
        drift['tanks'][0]['health'] += 1
        with self.assertRaises(RuntimeError):
            registry.merge_into(drift)

    def test_models_armour_and_textures_have_nonempty_verified_outputs(self):
        manifest = read('tank_models_source_manifest')['tanks']['object_907']
        self.assertEqual(33, manifest['armor']['plates'])
        for kind in ('model', 'physics', 'armor'):
            self.assertGreater(manifest[kind]['vertices'], 100)
            self.assertGreater(manifest[kind]['triangles'], 100)
        folder = port.ROOT / 'Assets/Blitz/Resources/Models/Tanks/OBJECT907/Textures'
        for texture in manifest['textures']:
            self.assertEqual(texture['sha256'], digest(folder / texture['destination']))


if __name__ == '__main__':
    unittest.main()
