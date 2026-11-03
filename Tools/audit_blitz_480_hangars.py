#!/usr/bin/env python3
"""Audit source digests, exact batch payloads, UV2 and all effect dependencies."""
import hashlib,json,struct
from pathlib import Path
import numpy as np
import port_blitz_480_hangars as export

root=export.ROOT/'Assets/Blitz/Resources'
errors=[]
for name in ['hangar','hangar_prem_2018']:
 folder=root/'Hangars480'/name
 d=json.loads((folder/'scene.json').read_text());m=json.loads((folder/'manifest.json').read_text());blob=(folder/'meshes.bytes').read_bytes()
 for rel,digest in m['sources'].items():
  p=export.p.PACK/rel
  if not p.is_file() or hashlib.sha256(p.read_bytes()).hexdigest()!=digest:errors.append(name+': source changed '+rel)
 if blob[:8]!=b'BLTZHGR1' or struct.unpack_from('<i',blob,8)[0]!=len(d['batches']):errors.append(name+': invalid header')
 offset=12;vtotal=ttotal=0
 for b in d['batches']:
  vc,ic=struct.unpack_from('<ii',blob,offset);offset+=8
  floats=np.frombuffer(blob,dtype='<f4',count=vc*10,offset=offset)
  if not np.isfinite(floats).all():errors.append(name+': nonfinite mesh '+b['name'])
  offset+=vc*40
  idx=np.frombuffer(blob,dtype='<i4',count=ic,offset=offset);offset+=ic*4
  if idx.min()<0 or idx.max()>=vc:errors.append(name+': invalid indices '+b['name'])
  if vc!=b['vertexCount'] or ic!=b['triangleCount']*3:errors.append(name+': changed geometry '+b['name'])
  if b['animation']>=0:
   ji=np.frombuffer(blob,dtype='<i4',count=vc*4,offset=offset).reshape(-1,4);offset+=vc*16
   w=np.frombuffer(blob,dtype='<f4',count=vc*4,offset=offset).reshape(-1,4);offset+=vc*16
   if not np.allclose(w.sum(axis=1),1,atol=.001):errors.append(name+': invalid skin weights '+b['name'])
   if ji.min()<0 or ji.max()>=len(d['animations'][b['animation']]['joints']):errors.append(name+': invalid skin joint')
  vtotal+=vc;ttotal+=ic//3
 if offset!=len(blob):errors.append(name+': trailing payload')
 if vtotal!=m['vertices'] or ttotal!=m['triangles']:errors.append(name+': manifest totals changed')
 scene=export.p.dava.read_sc2((export.p.PACK/d['scene']).read_bytes())
 source_materials={int.from_bytes(x['#id'],'little'):x for x in scene['dataNodes'] if x.get('##name')=='NMaterial'}
 source_global=next(x for x in source_materials.values() if x.get('materialName')=='Scene_Global_Material')
 fog_flags=source_global.get('flags',{})
 environment=d['environment']
 if environment['linear']!=bool(fog_flags.get('FOG_LINEAR')):errors.append(name+': changed fog mode')
 if environment['atmosphere']!=bool(fog_flags.get('FOG_ATMOSPHERE')):errors.append(name+': changed atmosphere flag')
 for field,source_field in [('density','fogDensity'),('start','fogStart'),('end','fogEnd'),('limit','fogLimit'),('color','fogColor'),('sky','fogAtmosphereColorSky'),('sun','fogAtmosphereColorSun'),('distance','fogAtmosphereDistance'),('scattering','fogAtmosphereScattering')]:
  if source_field in source_global.get('properties',{}):
   expected=export.p.material_property(source_global,source_field)
   if isinstance(expected,list) and len(expected)==1:expected=expected[0]
   if not np.allclose(environment[field],expected,atol=1e-7,rtol=0):errors.append(name+': changed source fog '+field)
 for material in d['materials']:
  source=export.p.inherited_material(source_materials,int(material['name'].split('_',1)[0]))
  effect=source.get('fxName','')
  expected=source.get('decal') if 'Decal.' in effect else source.get('lightmap') if 'Lightmap' in effect else None
  if expected:
   if not material['secondary'].endswith(Path(expected).stem):errors.append(name+': wrong active secondary texture '+material['name'])
  elif material['secondary']:errors.append(name+': inactive secondary texture was sampled '+material['name'])
  for field in ['albedo','secondary']:
   resource=material[field]
   if resource and not (root/(resource+'.png')).is_file():errors.append(name+': missing material texture '+resource)
 for e in d['effects']:
  for layer in e['layers']:
   for frame in layer['frames']:
    if not (root/(frame+'.png')).is_file():errors.append(name+': missing particle sprite '+frame)
   if layer['life']<=0 or len(layer['size'])!=2:errors.append(name+': invalid particle layer '+layer['name'])
 for a in d['animations']:
  for i,j in enumerate(a['joints']):
   if j['parent']>=i:errors.append(name+': skeleton not ordered parent before child')
 print(name,'batches',len(d['batches']),'vertices',vtotal,'triangles',ttotal,'layers',sum(len(e['layers']) for e in d['effects']),'motions',len(d['animations']))
print('errors',len(errors))
for e in errors:print(e)
raise SystemExit(bool(errors))
