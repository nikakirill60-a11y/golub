#!/usr/bin/env python3
"""Import original 4.8 SC2/YAML destruction, explosion and flaming, not Godot FX.
Normalize legacy PropertyLines into the shared DAVA evaluator; keep raw hashes.
"""
import hashlib, json, math, struct, sys
from pathlib import Path
from xml.etree import ElementTree as ET
import yaml
from PIL import Image
ROOT=Path(__file__).resolve().parents[1]; SRC=ROOT.parent/'wotblitz4.8.0files'
sys.path.insert(0,str(ROOT.parent/'blitz-legacy-godot/tools'))
import dava, pvr2png
OUT=ROOT/'Assets/Blitz/Resources/Legacy480'; DATA=ROOT/'Assets/Blitz/Resources/Data'
for folder in ['Sources','Textures']: (OUT/folder).mkdir(parents=True,exist_ok=True)
provenance={}; textures={}
def record(p):provenance[str(p.relative_to(SRC))]=hashlib.sha256(p.read_bytes()).hexdigest()
def save(p,v):p.write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n')
def find(rel):
 for base in [SRC,SRC/'from_apk/Data']:
  p=base/rel
  if p.exists():return p
 raise FileNotFoundError(rel)
def tex(sprite):
 rel=sprite.split('Data/')[-1].replace('~res:/','')
 if sprite in textures:return
 desc=find(rel+'.txt');record(desc);lines=desc.read_text().splitlines();n=int(lines[0]);atlases=lines[1:1+n];w,h=map(int,lines[1+n].split());frames=int(lines[2+n]);cols=math.ceil(math.sqrt(frames));rows=math.ceil(frames/cols);image=Image.new('RGBA',(w*cols,h*rows));cache={}
 for i,line in enumerate(lines[3+n:3+n+frames]):
  x,y,cw,ch,dx,dy,idx=map(int,line.split()[:7])
  if idx not in cache:
   atlas=find(str(Path(rel).parent/Path(atlases[idx]).with_suffix('.mali.webp')));record(atlas);cache[idx]=Image.open(atlas).convert('RGBA')
  image.paste(cache[idx].crop((x,y,x+cw,y+ch)),(i%cols*w+dx,i//cols*h+dy))
 name=Path(rel).name+'_'+hashlib.sha256(rel.encode()).hexdigest()[:10];image.save(OUT/'Textures'/(name+'.png'));textures[sprite]=dict(resource='Legacy480/Textures/'+name,frames=frames,cols=cols,rows=rows,usage=['spritePath'])
def line(v,dimensions=1,color=False):
 if not isinstance(v,list):return v
 if dimensions==1:return [2,[[1,v[i+1],v[i]] for i in range(0,len(v),2)]]
 def vec(a):
  a=[x/255 if color else x for x in a]
  return a if color else ([a[0],a[1],1] if len(a)==2 else a)
 if not any(isinstance(x,list) for x in v):return vec(v)
 result=[2]
 for channel in range(dimensions):result.append([[1,vec(v[i+1])[channel],v[i]] for i in range(0,len(v),2)])
 return result
def emitter(p):
 record(p);raw=yaml.safe_load(p.read_text());e=dict(raw['emitter']);e['type']={'point':0,'rect':1,'oncircle_volume':2,'oncircle':3,'sphere':4}.get(e['type'],0)
 for k in ['emissionAngle','emissionRange','radius']:e[k]=line(e.get(k,0))
 e['emissionVector']=line(e['emissionVector'],3);e['size']=line(e['size'],3);e['layers']=[]
 for key,l in raw.items():
  if not key.startswith('layer'):continue
  l=dict(l)
  l['legacyLod']=1 # Full authored 4.8 particle tier, not disabled/distant duplicates.
  if l.get('layerType')=='single':l['layerType']='particles';l['isSingle']=True
  if 'pivotPoint' in l:l['legacyPivot']=l['pivotPoint']
  l['legacySpinDirection']=True
  for k in ['alphaOverLife','life','lifeVariation','number','numberVariation','velocity','velocityVariation','velocityOverLife','animSpeedOverLife']:
   if k in l:l[k]=line(l[k])
  for old,new in [('size','size3d'),('sizeVariation','sizeVariation3d'),('sizeOverLifeXY','sizeOverLife3ds')]:
   if old in l:l[new]=line(l[old],3)
  for old,new in [('angle','angle3d'),('angleVariation','angleVariation3d'),('spin','spin3d'),('spinVariation','spinVariation3d')]:
   if old in l:l[new]=[0,0,l[old]] # DAVA Z -> Unity billboard roll
  for k in ['colorOverLife','colorRandom']:
   if k in l:l[k]=line(l[k],4,True)
  l['legacyForces']=[dict(force=line(l['force'+str(i)],3),overLife=line(l.get('forceOverLife'+str(i),1))) for i in range(l.get('forceCount',0)) if 'force'+str(i) in l]
  if l.get('sprite'):l['spritePath']=l['sprite'];tex(l['sprite'])
  if l.get('innerEmitterPath'):l['innerEmitter']=emitter(p.parent/l['innerEmitterPath'])
  e['layers'].append(l)
 return e
scenes={}
for name in ['explosion_tank_1','explosion_tank_m','explosion_rush_1','explosion_rush_m','explosion_AMX_Dracula','Fire_and_Smoke','Fire_and_Smoke_small','SmokeFire_tank_movement_new',
 # Water entry splash and the hull bow wave of tanks fording water (chassis_effects.yaml water row).
 'water_tank_splash','water_tank_splash_low','chassis_effects/wave_water_s','chassis_effects/wave_water_m','chassis_effects/wave_water_l']:
 p=SRC/'3d/FX'/(name+'.sc2');record(p);s=dava.read_sc2(p.read_bytes())['scene'];nodes=[]
 for n in s['#hierarchy'].values():
  for c in n['components'].values():
   if isinstance(c,dict) and c.get('comp.typename')=='ParticleEffectComponent':
    for item in c['pe.emitters'].values():nodes.append(dict(position=item['emitter.position'],emitter=emitter((p.parent/item['emitter.filename']).resolve())))
 save(OUT/'Sources'/(Path(name).name+'.json'),dict(legacyEmitters=nodes));scenes[Path(name).name]=len(nodes)
crash=SRC/'3d/Tanks/Camouflages/common/crash/crash.mali.pvr';record(crash)
# Decoder is the same proven original PVR decoder used for all client tanks.
header,w,h,pixels=pvr2png.decode(str(crash),0,False)
Image.frombytes('RGBA',(w,h),pixels).save(OUT/'Textures/crash.png')
common=SRC/'XML/item_defs/vehicles/common/vehicle.xml';record(common);r=ET.parse(common).getroot()
materials=r.find('materialGroups')
if materials is None:
 materials=next(x for x in r.iter() if x.find('ammoBay/chanceToHitByProjectile') is not None)
module_rules={n:dict(projectile=float(materials.find(n+'/chanceToHitByProjectile').text),explosion=float(materials.find(n+'/chanceToHitByExplosion').text)) for n in ['ammoBay','leftTrack','rightTrack']}
def fire_points(p):
 record(p);scene=dava.read_sc2(p.read_bytes())['scene'];points={}
 def walk(nodes):
  for node in nodes.values():
   if not isinstance(node,dict):continue
   name=node.get('name','')
   if name in ['HP_Fire_01','HP_Fire_02']:
    components=[c for c in node['components'].values() if isinstance(c,dict)]
    render=next((c for c in components if c.get('comp.typename')=='RenderComponent'),None)
    transform=next((c for c in components if c.get('comp.typename')=='TransformComponent'),None)
    if render:
     batch=next(iter(render['rc.renderObj']['ro.batches'].values()));box=batch['rb.aabbox'];v=[(box[i]+box[i+3])*.5 for i in range(3)]
     m=transform['tc.worldMatrix'] if transform else [1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]
     xyz=[sum(m[j*4+i]*v[j] for j in range(3))+m[12+i] for i in range(3)]
     points[name]=[xyz[0],xyz[2],-xyz[1]] # Tank importer axes + runtime Z mirror.
   if node.get('#hierarchy'):walk(node['#hierarchy'])
 walk(scene['#hierarchy']);return points
effect_map=yaml.safe_load((SRC/'XML/explosion_effects.yaml').read_text())['keyedArchive'];record(SRC/'XML/explosion_effects.yaml');cards=json.loads((DATA/'tanks.json').read_text())['tanks'];vehicles={}
for card in cards:
 p=SRC/card['source']
 if not p.exists():continue
 record(p);r=ET.parse(p).getroot()
 defaults={'destruction':'mediumDestruction','explosion':'mediumExplosion'}
 v={}
 for k in defaults:
  value=(r.findtext('effects/'+k) or defaults[k]).strip();v[k]=Path(effect_map.get(value,effect_map[defaults[k]])['string']).stem
  if value not in effect_map:v[k+'MissingAlias']=value
 v['flaming']=Path(r.findtext('blitz/fireEffectPathDead') or 'Fire_and_Smoke.sc2').stem
 if v['flaming'] not in scenes:raise ValueError(v['flaming'])
 model=SRC/'3d'/r.findtext('blitz/blitzModelPath')
 if model.exists():v['firePoints']=fire_points(model)
 v['ammoGeometryAvailable']=False # Client SC2 contains only outer armor; PC hitTester paths are absent.
 vehicles[card['name']]=v
save(DATA/'legacy480_wreck_manifest.json',dict(generator='Tools/port_blitz_480_wreck.py',scenes=scenes,textures=textures,vehicles=vehicles,moduleRules=module_rules,provenance=provenance,ammoGeometryPolicy='Original internal geometry unavailable; separately versioned ammo_zone_reconstruction.json is a user-authorized reconstruction, never the entire hull.'))
print('Original 4.8 scenes',scenes,'textures',len(textures),'vehicle mappings',len(vehicles),'rules',module_rules)
