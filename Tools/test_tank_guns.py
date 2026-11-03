#!/usr/bin/env python3
"""Every gun module of every card renders a barrel that reaches its muzzle."""
import json
import unittest

import audit_tank_guns as audit


class TankGunTests(unittest.TestCase):
    def test_every_gun_module_has_a_barrel_at_its_muzzle(self):
        self.assertEqual([], audit.audit())

    def test_every_barrel_is_continuous_from_turret_to_muzzle(self):
        holes = [p for p in audit.audit() if p['issue'] == 'barrel not continuous']
        self.assertEqual([], holes)

    def test_bore_holes_finds_a_missing_tube(self):
        def ring(z0, z1):
            # A square tube of 0.1 m around the -Z axis between z0 and z1.
            return [((x, y, z0), (x, y, z1), (x2, y2, z1)) for (x, y), (x2, y2) in
                    (((-.1, -.1), (.1, -.1)), ((.1, -.1), (.1, .1)),
                     ((.1, .1), (-.1, .1)), ((-.1, .1), (-.1, -.1)))]
        whole = ring(-5.0, -1.0)
        self.assertEqual([], audit.bore_holes([whole, [], []], [0.0, 0.0, -5.0], -1.0))
        split = ring(-5.0, -3.5) + ring(-2.0, -1.0)
        holes = audit.bore_holes([split, [], []], [0.0, 0.0, -5.0], -1.0)
        self.assertEqual(1, len(holes))
        self.assertAlmostEqual(-2.0, holes[0][0], delta=0.06)
        self.assertAlmostEqual(-3.5, holes[0][1], delta=0.06)

    def test_silencer_top_guns_carry_their_obves_barrel(self):
        groups = audit.obj_groups(audit.MODELS / 'OTH46CHARIOTEERS3/OTH46CHARIOTEERS3.obj')
        albedos = json.loads((audit.DATA / 'oth46charioteers3_nodes.json').read_text())['albedos']
        for gun in ('gun_03', 'gun_04'):
            self.assertIn(gun + '_sheet1', groups)
            self.assertEqual('OTH46_CHARIOTEER_S3_OBVES', albedos[gun + '_sheet1'])
        self.assertEqual([], audit.audit({'oth46_charioteer_s3'}))

    def test_annihilator_gatling_is_a_separate_rotor(self):
        groups = audit.obj_groups(audit.MODELS / 'OTH25HWT20FALCONFIRE/OTH25HWT20FALCONFIRE.obj')
        self.assertIn('gun_01_rotor', groups)
        nodes = json.loads((audit.DATA / 'oth25hwt20falconfire_nodes.json').read_text())['nodes']
        self.assertEqual('gun_01', nodes['gun_01_rotor']['parent'])
        # The cylinder spins around the bore: its pivot is the gun trunnion.
        self.assertEqual(nodes['gun_01']['world'], nodes['gun_01_rotor']['world'])
        muzzle = nodes['gun_01_shoot_pos']['world'][2]
        self.assertLess(groups['gun_01_rotor']['low'][2], muzzle)

    def test_annihilator_bakes_the_client_gatling_clip(self):
        groups = audit.obj_groups(audit.MODELS / 'OTH25HWT20FALCONFIRE/OTH25HWT20FALCONFIRE.obj')
        table = json.loads((audit.DATA / 'oth25hwt20falconfire_nodes.json').read_text())
        clips = table['animations']['gun_01']
        self.assertEqual({'shot', 'reload'}, set(clips))
        self.assertEqual('burst', clips['shot']['trigger'])
        self.assertEqual('reloaded', clips['reload']['trigger'])
        self.assertEqual(['gun_01_anim_reload_drum'], list(clips['reload']['tracks']))
        shot = clips['shot']['tracks']
        barrels = ['gun_01_anim_gatling_gun_0%d' % i for i in (1, 2, 3)]
        levers = ['gun_01_anim_rec_sys_helper_0%d' % i for i in (1, 2, 3)]
        for name in ['gun_01_rotor'] + barrels + levers:
            self.assertIn(name, shot)
        for name in list(shot) + ['gun_01_anim_reload_drum']:
            # Every animated part is its own object riding the gun slot.
            self.assertIn(name, groups)
            self.assertEqual('gun_01', table['nodes'][name]['parent'])
        for track in list(shot.values()) + [clips['reload']['tracks']['gun_01_anim_reload_drum']]:
            # Rest pose at both ends of the clip.
            for frame in (track[0], track[-1]):
                self.assertLess(max(abs(v) for v in frame[:3]), 0.01)
                self.assertGreater(abs(frame[6]), 0.999)
        # Barrel n recoils 0.36 m backwards (+Z in the nodes frame, the muzzle
        # is at -Z) one third of a second after barrel n-1.
        fps = clips['shot']['fps']
        for index, name in enumerate(barrels):
            frame = shot[name][round((0.1 + index / 3) * fps)]
            rotor = shot['gun_01_rotor'][round((0.1 + index / 3) * fps)]
            self.assertAlmostEqual(0.36, frame[2] - rotor[2], delta=0.03)

    def test_annihilator_fires_three_shells_per_trigger(self):
        tanks = json.loads((audit.DATA / 'tanks.json').read_text())['tanks']
        card = next(t for t in tanks if t['name'] == 'oth25_hwt20_falconfire')
        gun = card['guns'][0]
        self.assertEqual(1, gun['fire_mode'])
        self.assertEqual([3], gun['burst_info']['bursts'])
        self.assertAlmostEqual(1 / 3, gun['burst_info']['intraburst'], places=3)


if __name__ == '__main__':
    unittest.main()
