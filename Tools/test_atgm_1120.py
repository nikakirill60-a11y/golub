#!/usr/bin/env python3
"""Source-backed checks: original artwork, modern ammo, legacy protection."""
import hashlib
import json
import unittest
from pathlib import Path
from xml.etree import ElementTree as ET
from PIL import Image
import port_blitz_480_data as base
from port_blitz_1120 import NationClient1120

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT.parent / 'wotblitz11.20.0files'
DATA = ROOT / 'Assets/Blitz/Resources/Data'

class Atgm1120Tests(unittest.TestCase):
    def test_original_pixels_and_provenance(self):
        manifest = json.loads((DATA/'atgm_1120_source_manifest.json').read_text())
        for entry in manifest['sources']:
            original = (SOURCE/entry['source']).read_bytes()
            self.assertEqual(entry['sha256'], hashlib.sha256(original).hexdigest())
            if entry['role'] in ('original-marker-layout','original-marker-animation'):
                copied = ROOT/'Assets/Blitz/Resources/Atgm1120/Sources'/(Path(entry['source']).name+'.txt')
                self.assertEqual(original, copied.read_bytes(), 'Authored reference bytes must not be normalized')
        for item in [manifest['marker'], *manifest['textures'].values()]:
            image = Image.open(ROOT/'Assets/Blitz/Resources'/(item['resource']+'.png')).convert('RGBA')
            self.assertEqual(item['pixel_sha256'], hashlib.sha256(image.tobytes()).hexdigest())
        self.assertEqual([46,34], manifest['marker']['logical_size'])
        self.assertEqual(.75, manifest['marker']['explosion']['seconds'])
        self.assertEqual(1.75, manifest['marker']['explosion']['scale_to'])
        self.assertEqual(3, len(manifest['effects']))

    def test_original_kpz_and_sheridan_ammunition_select_modern_profile(self):
        previous = base.VEHICLES
        try:
            base.VEHICLES = SOURCE/'from_apk/XML/item_defs/vehicles'
            for nation, tag in [('germany','_152mm_XM578E1_R'),('usa','_152mm_MGM_51A1E2')]:
                client = NationClient1120(nation,{})
                # This is the actual source shell record, not a guessed tank name.
                self.assertEqual('atgm_heat', client.shells[tag].findtext('icon'))
                shared = client.shared['guns']
                gun = next(g for g in shared.values() if g.find('shots/'+tag) is not None)
                shell = next(s for s in client.shell_cards(gun) if s['name']==tag)
                self.assertTrue(shell['atgm'])
                self.assertEqual('blitz1120_marker', shell['atgm_visual'])
        finally:
            base.VEHICLES = previous

    def test_live_physics_and_legacy_cards_unchanged(self):
        # Baseline authored cards before this presentation-only feature.
        # Fixed digests keep this regression valid after the feature is committed.
        # The KPz 70 R digest moved once, when its gun took the shot_huge
        # report gun_effects.xml names (gun_shot_sounds.py) instead of shot_atgm.
        expected = {'g14_kpfpz_70r':'eca8d0f5a05d3da26735529ad341ad10909fa7ee97a8d6ce8cd0b6b9d8a10497',
                    't49_rocket':'ef4dc226fb2f2a9fa531de5205d0b0ab5d77dc66be28414bc2ab42d77dde863d'}
        current = json.loads((DATA/'tanks.json').read_text())
        marked = []
        for t in current['tanks']:
            for gun in t['guns']:
                for s in gun['shells']:
                    if s.pop('atgm_visual',None): marked.append(t['name'])
            if t['name'] in expected:
                digest = hashlib.sha256(json.dumps(t,sort_keys=True,separators=(',',':'),ensure_ascii=False).encode()).hexdigest()
                self.assertEqual(expected[t['name']],digest,t['name'])
        self.assertEqual(['g14_kpfpz_70r','a116_xm551_exp'],marked)

if __name__=='__main__': unittest.main()
