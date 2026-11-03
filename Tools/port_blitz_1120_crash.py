#!/usr/bin/env python3
"""Import the authored crash state of the 15 additive 11.20 vehicles.
SC2 has common hull/turret/gun geometry plus dedicated broken-track meshes;
PC-style XML crash/lod0 paths are not separate Blitz hull scene files.
"""
import hashlib,json,sys
from pathlib import Path
from xml.etree import ElementTree as ET
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT.parent/'blitz-legacy-godot/tools'))
import dava,pvr2png
SRC=ROOT.parent/'wotblitz11.20.0files';PACK=ROOT/'Tools/_branch_glb/1120/compat'
OUT=ROOT/'Assets/Blitz/Resources/Legacy1120Crash';OUT.mkdir(parents=True,exist_ok=True)
provenance={}
def record(p):provenance[str(p.relative_to(SRC))]=hashlib.sha256(p.read_bytes()).hexdigest()
def items(n):return n.values() if isinstance(n,dict) else n if isinstance(n,list) else []
def nodes(n):
 for x in items(n):
  if isinstance(x,dict):yield x;yield from nodes(x.get('#hierarchy',{}))
textures={}
for name,rel in {'albedo':'crash.astc.pvr','baseColor':'images_pbr/crash_BC.astc.pvr','normal':'images_pbr/crash_NM.astc.pvr','roughMetal':'images_pbr/crash_RM.astc.pvr'}.items():
 p=SRC/'from_apk/3d/Tanks/Camouflages/common/crash'/rel;record(p)
 if pvr2png.main([str(p),'--out',str(OUT/(name+'.png'))]):raise RuntimeError(p)
 textures[name]='Legacy1120Crash/'+name
vehicles={}
cards=json.loads((ROOT/'Assets/Blitz/Resources/Data/blitz_1120_cards.json').read_text())['tanks']
for card in cards:
 xml=SRC/card['source'];record(xml);r=ET.parse(PACK/'XML/item_defs/vehicles'/xml.parent.name/xml.name).getroot()
 stem=Path(r.findtext('blitz/blitzModelPath')).stem
 nation={'china':'China','european':'European','france':'France'}[xml.parent.name]
 scene=PACK/'3d/Tanks'/nation/(stem+'.sc2')
 if not scene.exists():
  matches=list((PACK/'3d/Tanks'/nation).glob('*.sc2'))
  scene=next(p for p in matches if p.stem.lower().replace('project','projet')==stem.lower().replace('project','projet'))
 original=SRC/'3d/Tanks'/nation/(scene.name+'.dvpl');record(original);record(original.with_name(scene.stem+'.scg.dvpl'))
 raw=dava.read_sc2(scene.read_bytes())['scene'];points={};names=set()
 for n in nodes(raw['#hierarchy']):
  names.add(n.get('name',''))
  if n.get('name') in ['HP_Fire_01','HP_Fire_02']:
   comps=list(items(n.get('components',{})));ren=next((x for x in comps if isinstance(x,dict) and x.get('comp.typename')=='RenderComponent'),None)
   if ren:
    box=next(iter(items(ren['rc.renderObj']['ro.batches'])))['rb.aabbox'];v=[(box[i]+box[i+3])*.5 for i in range(3)];points[n['name']]=[v[0],v[2],-v[1]]
 if not {'chassis_track_crash_L','chassis_track_crash_R'}<=names:raise ValueError('Missing authored crashed tracks: '+card['name'])
 paths=[x.text.strip() for x in r.findall('.//models/destroyed') if x.text]
 vehicles[card['name']]={'firePoints':points,'destroyedXmlPaths':paths,'geometryPolicy':'Original installed hull/turret/gun meshes; authored chassis_track_crash_L/R. No separate deformed hull/turret SC2 exists in this scene.','sourceScene':str(original.relative_to(SRC)),'crashTexture':textures['albedo']}
manifest={'generator':'Tools/port_blitz_1120_crash.py','textures':textures,'vehicles':vehicles,'provenance':provenance}
p=ROOT/'Assets/Blitz/Resources/Data/legacy1120_crash_manifest.json';p.write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n');print('11.20 crash states:',len(vehicles),'original textures:',len(textures))
