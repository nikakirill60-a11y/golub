#!/usr/bin/env python3
import hashlib,json,re,unittest
from pathlib import Path
from port_tank_name_localization import DATA,OUTPUT,generate,serialized

class TankNameTests(unittest.TestCase):
    def test_source_rows_are_exact_and_repeat_export_is_deterministic(self):
        self.assertEqual(OUTPUT.read_text(),serialized(generate()))
        authored=json.loads(OUTPUT.read_text())
        self.assertEqual(522,len(authored['tanks']))
        policies={name for name,t in authored['tanks'].items() if 'policy' in t['name']['source']['en']}
        self.assertEqual({'waffentrager_iv_cl','m36_bp','a130_super_hellcat',
            'oth36_indien_panzer_s1','oth37_t26_e4_superpershing_s1','oth38_50tp_tyszkiewicza_s1',
            'oth51_fd_24','oth53_rammer','oth84_fve_defender','oth85_ghoul'},policies)
    def test_complete_live_coverage_and_no_cyrillic_english(self):
        authored=json.loads(OUTPUT.read_text());lang=json.loads((DATA/'lang.json').read_text())['keys']
        for t in json.loads((DATA/'tanks.json').read_text())['tanks']:
            for field in ['name','short']:
                key='tank:'+t['name']+'/'+field;row=authored['tanks'][t['name']][field]['translations']
                self.assertEqual(row,lang[key]);self.assertFalse(re.search('[А-Яа-яЁё]',row['en']))
    def test_base_csv_reexport_retains_generated_tank_rows(self):
        import tempfile
        from port_godot_hangar import write_language_table
        with tempfile.TemporaryDirectory() as directory:
            p=Path(directory)
            (p/'source.csv').write_text('keys,en,ru\nLOGIN,Login,Войти\n')
            (p/'additions.gd').write_text('')
            (p/'tank_name_localization.json').write_text(OUTPUT.read_text())
            write_language_table(p/'source.csv',p/'lang.json',p/'additions.gd')
            got=json.loads((p/'lang.json').read_text())['keys']
            for key,value in json.loads(OUTPUT.read_text())['keys'].items():self.assertEqual(value,got[key])
    def test_ambiguities_do_not_override_identity_and_cards_are_untouched(self):
        import io,shutil,tempfile
        from contextlib import redirect_stdout
        from unittest.mock import patch
        import port_tank_name_localization as exporter
        # Exercise the real write path on the current roster, without depending
        # on Git history or allowing a test to rewrite the working tree.
        with tempfile.TemporaryDirectory() as directory:
            data=Path(directory)
            for name in ('tanks.json','lang.json','hangar_source_manifest.json','tank_name_localization.json'):
                shutil.copyfile(DATA/name,data/name)
            before=(data/'tanks.json').read_bytes()
            with patch.object(exporter,'DATA',data),patch.object(exporter,'OUTPUT',data/OUTPUT.name),\
                 patch('sys.argv',['port_tank_name_localization.py']),redirect_stdout(io.StringIO()):
                exporter.main()
            self.assertEqual(before,(data/'tanks.json').read_bytes())
            authored=json.loads((data/OUTPUT.name).read_text())
            self.assertIn('T-54 ltwt.',authored['ambiguousAliases'])
            for alias in authored['ambiguousAliases']:
                self.assertNotIn(alias,authored['keys'])
            self.assertEqual({c['name'] for c in json.loads(before)['tanks']},set(authored['tanks']))
            self.assertEqual('Т-54 обл.',authored['tanks']['r109_t54s']['short']['translations']['ru'])

if __name__=='__main__':unittest.main()
