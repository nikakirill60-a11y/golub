#!/usr/bin/env python3
"""Independent client/port contracts. No public web figures or other client packs."""
import hashlib, io, json, struct, sys, unittest, xml.etree.ElementTree as ET
from pathlib import Path
from PIL import Image
import yaml
ROOT=Path(__file__).resolve().parents[1]
SOURCE=ROOT.parent/'wotblitz11.20.0files'
sys.path.insert(0,str(ROOT.parent/'blitz-legacy-godot/tools'))
import dvpl
RES=ROOT/'Assets/Blitz/Resources'
def load(name):return json.loads((RES/'Data'/name).read_text())
def original(relative):
    for base in [SOURCE,SOURCE/'from_apk']:
        for suffix in ['','.dvpl']:
            p=base/(relative+suffix)
            if p.exists():return dvpl.unpack_bytes(p.read_bytes()) if suffix else p.read_bytes()
    raise FileNotFoundError(relative)
def sprite(relative,scale):
    name=relative+('@2x' if scale==2 else '')
    atlas=None
    try:descriptor=original(name+'.txt')
    except FileNotFoundError:
        raw=original(name+'.packed.webp');atlas=Image.open(io.BytesIO(raw)).convert('RGBA');descriptor=None;pos=12
        while pos+8<=len(raw):
            tag=raw[pos:pos+4];size=struct.unpack_from('<I',raw,pos+4)[0];chunk=raw[pos+8:pos+8+size];pos+=8+size+(size&1)
            if tag==b'extr':descriptor=chunk.rstrip(b'\0')
        if descriptor is None:return atlas
    lines=descriptor.decode().splitlines();count=int(lines[0]);w,h=map(int,lines[count+1].split());assert int(lines[count+2])==1
    x,y,cw,ch,dx,dy,index=map(int,lines[count+3].split()[:7]);assert index<count
    if atlas is None:atlas=Image.open(io.BytesIO(original(str(Path(name).parent/lines[1+index])))).convert('RGBA')
    out=Image.new('RGBA',(w,h));out.paste(atlas.crop((x,y,x+cw,y+ch)),(dx,dy));return out
class BigBossSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):cls.data=load('big_boss_1120.json')
    def test_source_hashes_still_identify_supplied_client(self):
        for name in ['big_boss_1120.json','big_boss_fx_1120.json','big_boss_audio_1120.json']:
            for entry in load(name)['sources']:
                with self.subTest(source=entry['source']):self.assertEqual(entry['sha256'],hashlib.sha256((SOURCE/entry['source']).read_bytes()).hexdigest())
    def test_every_imported_ui_pixel_matches_independent_descriptor_rebuild(self):
        self.assertEqual(42,len(self.data['textures']))
        for name,entry in self.data['textures'].items():
            with self.subTest(sprite=name):
                expected=sprite(entry['source'],entry['scale']);actual=Image.open(RES/'BigBoss/Textures'/(name+'.png')).convert('RGBA')
                self.assertEqual(expected.size,actual.size);self.assertEqual(expected.tobytes(),actual.tobytes());self.assertEqual([actual.width/entry['scale'],actual.height/entry['scale']],entry['logical_size'])
    def test_role_ids_slots_and_thresholds_match_xml(self):
        xml=ET.fromstring(original('XML/item_defs/vehicles/common/roles.xml'))
        self.assertEqual([0]+[int(x.findtext('rolePoints')) for x in xml.find('levelsSettings') if x.findtext('rolePoints')],self.data['levels'])
        for role in self.data['roles']:
            source=xml.find(role['element']);self.assertEqual(int(source.findtext('id')),role['id'])
            for index,level in enumerate(source.find('levels')):
                self.assertEqual([x.text.strip() for x in sorted(level.find('abilities'),key=lambda x:int(x.get('index')))],role['levels'][index])
    def test_original_consumable_and_provision_sources_are_not_overwritten(self):
        copies=list((RES/'BigBoss/Sources').glob('*bossmode.xml.txt'));self.assertEqual(2,len(copies))
        for p in copies:
            relative=p.name.removesuffix('.txt').replace('XML_item_defs_vehicles_common_','XML/item_defs/vehicles/common/').replace('consumables_bossmode','consumables/bossmode').replace('provisions_bossmode','provisions/bossmode')
            self.assertEqual(original(relative),p.read_bytes())
    def test_missing_textures_stay_absent_without_substitutes(self):
        data=load('big_boss_fx_1120.json');self.assertEqual(4,len(data['missing']))
        for path in data['missing']:
            self.assertTrue(data['textures'][path]['missing']);self.assertNotIn('resource',data['textures'][path])
    def test_generated_strings_are_exact_client_rows(self):
        lang=load('lang.json')['keys'];self.assertEqual([],self.data['reconstructedStrings'])
        for code in ['en','ru']:
            strings=yaml.safe_load(original('Strings/'+code+'.yaml').decode('utf-8-sig'))
            for key in lang:
                if key.startswith('#bossmode:'):self.assertEqual(strings[key],lang[key][code])
            self.assertNotIn('#bossmode:boss_role/descr',lang)
    def test_fixed_source_role_widget_geometry(self):
        source=yaml.safe_load(original('UI/Screens3/Battle/HUD/GameModes/RoleProgressBar.yaml').decode('utf-8-sig'))['Prototypes'][0]
        size=source['components']['SizePolicy'];self.assertEqual(205,size['horizontalValue']);self.assertEqual(96,size['verticalValue'])
        bar=next(x for x in source['children'] if x['name']=='ProgressBar');self.assertEqual([54,72],bar['position']);self.assertEqual(136,bar['components']['SizePolicy']['horizontalValue'])
    def test_original_audio_output_hashes(self):
        data=load('big_boss_audio_1120.json');self.assertEqual(13,len(data['events']));self.assertEqual(25,len(data['clips']))
        for clip in data['clips'].values():self.assertEqual(clip['outputSha256'],hashlib.sha256((RES/(clip['resource']+'.ogg')).read_bytes()).hexdigest())
    def test_audio_cue_trees_use_only_event_media(self):
        data=load('big_boss_audio_1120.json')
        def leaves(cue):return [cue['media']] if 'media' in cue else [m for child in cue['children'] for m in leaves(child)]
        for name,event in data['events'].items():
            with self.subTest(event=name):
                self.assertEqual(sorted(set(leaves(event['cue']))),sorted(event['media']))
                for media in event['media']:self.assertIn(str(media),data['clips'])
        self.assertEqual(['play_hlwn_2021_abl_heal_aura_on'],data['events']['play_hlwn_2021_abl_heal_aura_off']['stops'])
        self.assertEqual('layer',data['events']['play_hlwn_2021_abl_kamikaze']['cue']['type'])
if __name__=='__main__':unittest.main()
