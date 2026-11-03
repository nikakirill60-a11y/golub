#!/usr/bin/env python3
"""Port both shipped 4.8 hangars directly from SC2, not from the Godot rebuild.

Carries both UV channels, inherited material/lightmap bindings, source particle
curves/sprite padding, heightmaps and DVAF skeleton motion. No mesh reduction.
DAVA matrices are row-vector; the payload is already placed in Unity metres.
"""
import contextlib, hashlib, io, json, math, os, struct, subprocess
from pathlib import Path
import numpy as np
from PIL import Image
import yaml
import port_blitz_client_map as p
import dava_anim

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT/'Assets/Blitz/Resources/Hangars480'
# DAVA is right-handed Z-up; Unity is left-handed Y-up. The map
# converter's rotation (x,z,-y) is a Godot frame, NOT Unity handedness.
# Reflecting once here keeps signs/text readable, and the face winding below
# compensates for this determinant -1 transform.
C = np.array([[1,0,0,0],[0,0,1,0],[0,1,0,0],[0,0,0,1]],dtype=float)

def flat(a): return np.asarray(a).reshape(-1).tolist()
def scalar(v, default=0):
    if isinstance(v,(int,float)): return float(v)
    if isinstance(v,list) and v: return scalar(v[1] if len(v)>1 else v[0],default)
    return default
def truth(v): return v is True or str(v).lower()=='true'
def keys(v,dim,default):
    if v is None: v=default
    if not isinstance(v,list): return [{'time':0.,'value':[float(v)]}]
    if len(v)==dim and all(isinstance(x,(int,float)) for x in v): return [{'time':0.,'value':[float(x) for x in v]}]
    return [{'time':float(v[i]),'value':([float(x) for x in v[i+1]] if isinstance(v[i+1],list) else [float(v[i+1])])} for i in range(0,len(v)-1,2)]

def port(name):
    src=p.PACK/'3d/Maps'/name
    target=OUT/name;target.mkdir(parents=True,exist_ok=True)
    prefix='Hangars480/'+name+'/'
    sources={};textures={}; sprites={}
    def record(path):
        path=path.resolve();sources[str(path.relative_to(p.PACK))]=hashlib.sha256(path.read_bytes()).hexdigest()
    def texture(ref,base=src):
        if not ref:return ''
        path=(base/ref).resolve() if not str(ref).startswith('~res:/') else p.PACK/str(ref)[6:]
        if path.suffix=='.tex':
            candidates=[path.with_suffix('.mali.pvr'),path.with_suffix('.mali.webp'),path.with_suffix('.png')]
            path=next((x for x in candidates if x.is_file()),path)
        if not path.is_file(): raise RuntimeError('Missing texture '+str(path))
        key=str(path.relative_to(p.PACK))
        if key in textures:return textures[key]
        record(path)
        dest=target/'Textures'/('%03d_'%len(textures)+path.stem.replace('.mali','')+'.png')
        dest.parent.mkdir(exist_ok=True)
        if path.suffix in ['.webp','.png']:Image.open(path).save(dest)
        else:
            with contextlib.redirect_stdout(io.StringIO()):ok=p.decode_texture(path,dest)
            if not ok:raise RuntimeError('Cannot decode '+key)
        resource=prefix+str(dest.relative_to(target).with_suffix(''))
        textures[key]=resource
        return resource
    def sprite(ref):
        # Android descriptors still use ../Data/Gfx, while this extracted pack
        # keeps the payload under Gfx. Restore trimmed frame offsets, not a crop
        # stretched to the source size (rays and sparks would change shape).
        suffix=ref.split('/Gfx/',1)[1] if '/Gfx/' in ref else ref.split('Gfx/',1)[1]
        descriptor=p.PACK/'Gfx'/ (suffix+'.txt');record(descriptor)
        if ref in sprites:return sprites[ref]
        lines=[x.strip() for x in descriptor.read_text().splitlines() if x.strip()]
        sheets=int(lines[0]);atlas=descriptor.parent/lines[1]
        atlas_resource=texture(str(atlas),descriptor.parent)
        atlas_path=OUT/(atlas_resource[len('Hangars480/'):]+'.png')
        canvas_size=tuple(map(int,lines[1+sheets].split()))
        frame_count=int(lines[2+sheets]);frames=[]
        for line in lines[3+sheets:3+sheets+frame_count]:
            vals=line.split();x,y,w,h,ox,oy,page=map(int,vals[:7])
            if page!=0:raise RuntimeError('multi-sheet particle '+ref)
            img=Image.new('RGBA',canvas_size);img.paste(Image.open(atlas_path).convert('RGBA').crop((x,y,x+w,y+h)),(ox,oy))
            dest=target/'Particles'/('%03d_'%len(sprites)+descriptor.stem+'_'+str(len(frames))+'.png');dest.parent.mkdir(exist_ok=True);img.save(dest)
            frames.append(prefix+str(dest.relative_to(target).with_suffix('')))
        sprites[ref]=frames;return frames
    scene_path=src/'hv1.sc2';record(scene_path);scene=p.dava.read_sc2(scene_path.read_bytes())
    entities=p.named_entities(scene);groups={p.dava.group_id(x):x for x in scene['geometry']}
    mats={int.from_bytes(x['#id'],'little'):x for x in scene['dataNodes'] if x.get('##name')=='NMaterial'}
    def inherited(k):
        result={'textures':{},'properties':{},'flags':{}};seen=set()
        while k in mats and k not in seen:
            seen.add(k);m=mats[k]
            for field in ['textures','flags']:
                for key,v in m.get(field,{}).items():result[field].setdefault(key,v)
            for key in m.get('properties',{}):result['properties'].setdefault(key,p.material_property(m,key))
            if m.get('fxName'):result.setdefault('fx',m['fxName'])
            result.setdefault('name',m.get('materialName',''));k=m.get('parentMaterialKey')
        return result
    material_list=[];material_ids={}
    def material(k):
        if k in material_ids:return material_ids[k]
        m=inherited(k);tx=m['textures'];fx=m.get('fx','');props=m['properties']
        secondary=tx.get('decal') if 'Decal.' in fx else tx.get('lightmap') if 'Lightmap' in fx else None
        item={'name':str(k)+'_'+m['name'],'effect':fx,'albedo':texture(tx.get('albedo') or tx.get('colorTexture')),
              'secondary':texture(secondary),'secondaryMultiplier':2. if secondary else 1.,
              'uvScale':props.get('uvScale',[1,1]),'uvOffset':props.get('uvOffset',[0,0]),
              'tint':props.get('flatColor',[1,1,1,1]) if m['flags'].get('FLATCOLOR',0) else [1,1,1,1],'cutoff':scalar(props.get('alphatestThreshold'),0.3),
              'alphaBlend':'Alphablend' in fx,'alphaTest':'Alphatest' in fx,'noCull':'NoCull' in fx}
        if 'Lightmap' not in fx:item['uvScale']=[1,1];item['uvOffset']=[0,0]
        material_ids[k]=len(material_list);material_list.append(item);return material_ids[k]
    batches=[];payloads=[];animations=[];effects=[];lights=[];sounds=[];omissions=[]
    stand_height=0.0
    def add_batch(label,positions,normals,uv,uv2,indices,mid,skin=None,animation=-1):
        pos=np.asarray(positions,dtype='<f4');nor=np.asarray(normals,dtype='<f4');uv=np.asarray(uv,dtype='<f4');uv2=np.asarray(uv2,dtype='<f4');idx=np.asarray(indices,dtype='<i4')
        if len(idx)%3 or len(pos)==0 or idx.min()<0 or idx.max()>=len(pos):raise RuntimeError('Invalid indices '+label)
        b={'name':label,'material':mid,'animation':animation,'vertexCount':len(pos),'triangleCount':len(idx)//3,'min':pos.min(axis=0).tolist(),'max':pos.max(axis=0).tolist()}
        data=struct.pack('<ii',len(pos),len(idx))+pos.tobytes()+nor.tobytes()+uv.tobytes()+uv2.tobytes()+idx.tobytes()
        if skin is not None:data+=np.asarray(skin[0],dtype='<i4').tobytes()+np.asarray(skin[1],dtype='<f4').tobytes()
        batches.append(b);payloads.append(data)
    for en,(label,e) in enumerate(entities):
        world=np.array(p.matrix_of(e)).reshape(4,4);transform=C@world
        sk=list(p.components(e,'SkeletonComponent'));motion=list(p.components(e,'MotionComponent'));animation=-1
        if sk and motion:
            joints=p.items(sk[0]['joints']);clip_path=src/motion[0]['motion.filepath'];record(clip_path);clip=dava_anim.read_anim(clip_path)
            actions=[a for c in p.components(e,'ActionComponent') for a in p.items(c) if isinstance(a,dict) and a.get('act.type')==6 and a.get('act.event')==1]
            animations.append({'name':label,'matrix':flat(transform),'duration':clip['duration'],'rate':motion[0].get('motion.playbackRate',1),
                               'delay':actions[0].get('act.delay',0) if actions else 0,
                               'joints':[{'uid':j['joint.uid'],'parent':j['joint.parentIndex'] if j['joint.parentIndex']<len(joints) else -1,'bind':flat(p.transposed(j['joint.bindPose'])),'inverse':flat(p.transposed(j['joint.invBindPose']))} for j in joints],
                               'tracks':[{'uid':n['uid'],'channels':[{'target':c['target'],'keys':[{'time':t,'value':v} for t,v in c['keys']]} for c in n['channels']]} for n in clip['nodes']]})
            animation=len(animations)-1
        for bi,(gid,mid,kind) in enumerate(p.batches_of(e)):
            m=inherited(mid)
            if 'ShadowVolume' in m.get('fx',''):
                omissions.append({'entity':label,'reason':'DAVA stencil-only shadow volume, not visible geometry'});continue
            st=p.dava.decode_vertices(groups[gid]);v=np.array(st['position']);n=np.array(st.get('normal',[(0,0,1)]*len(v)))
            uv=np.array(st.get('uv0',[(0,0)]*len(v)));uv[:,1]=1-uv[:,1]
            uv2=np.array(st.get('uv1',[(0,0)]*len(v)))
            # Secondary UVs remain in the authored top-down space. Shader flips
            # AFTER uvScale/uvOffset, exactly once, when sampling the PNG.
            skin=None
            if animation>=0:
                obj=next(p.components(e,'RenderComponent'))['rc.renderObj']
                target_ids=struct.unpack('<%di'%(len(obj['skinnedObject.batch%d.targetsData'%bi])//4),obj['skinnedObject.batch%d.targetsData'%bi])
                if 'hardJointIndex' in st:
                    ji=np.zeros((len(v),4),dtype=int);jw=np.zeros((len(v),4));jw[:,0]=1
                    ji[:,0]=[target_ids[int(x[0])] for x in st['hardJointIndex']]
                else:
                    raw=st.get('jointIndex',st.get('jointWeight'));jw=np.array(st['legacySkinning'] if 'legacySkinning' in st else st['jointWeight']);ji=np.array([[target_ids[int(x)] if float(w)>0 else 0 for x,w in zip(row,weights)] for row,weights in zip(raw,jw)])
                skin=(ji,jw)
                # Animated vertices remain in DAVA bind space; the motion
                # builder applies source inverse-bind, animated skeleton,
                # entity placement and axis conversion every frame.
            else:
                v=(np.c_[v,np.ones(len(v))]@transform.T)[:,:3]
                n=n@np.linalg.inv(transform[:3,:3]);n/=np.maximum(np.linalg.norm(n,axis=1)[:,None],1e-12)
            if name=='hangar_prem_2018' and label=='env_hg_hn5Low.sc2':
                # Seat the tank on the authored concrete floor at scene origin.
                # Its source mesh is translated up by ~8.374 m, and its local
                # floor is at -2.169 m. Derive the resulting height from the
                # actual triangles rather than hardcoding either number.
                triangles=v[np.array(p.dava.decode_indices(groups[gid])).reshape(-1,3)]
                a=triangles[:,0][:,[0,2]];ab=triangles[:,1][:,[0,2]]-a;ac=triangles[:,2][:,[0,2]]-a
                determinant=ab[:,0]*ac[:,1]-ab[:,1]*ac[:,0];valid=np.abs(determinant)>1e-10
                u=np.divide(-a[:,0]*ac[:,1]+a[:,1]*ac[:,0],determinant,out=np.zeros(len(a)),where=valid)
                w=np.divide(-ab[:,0]*a[:,1]+ab[:,1]*a[:,0],determinant,out=np.zeros(len(a)),where=valid)
                hit=valid&(u>=0)&(w>=0)&(u+w<=1)
                heights=triangles[:,0,1]+u*(triangles[:,1,1]-triangles[:,0,1])+w*(triangles[:,2,1]-triangles[:,0,1])
                if not np.any(hit):raise RuntimeError('Premium stand has no source floor')
                stand_height=float(heights[hit].max())
            indices=np.array(p.dava.decode_indices(groups[gid])).reshape(-1,3)
            # Preserve the authored front face through the handedness change.
            # Authored mirrored transforms cancel our reflection if present.
            if np.linalg.det(transform[:3,:3])<0:indices=indices[:,[0,2,1]]
            indices=indices.reshape(-1)
            add_batch('%03d_%s_%d'%(en,label,bi),v,n,uv,uv2,indices,material(mid),skin,animation)
        for c in p.components(e,'RenderComponent'):
            obj=c['rc.renderObj']
            if obj['##name']!='Landscape':continue
            hmap=src/obj['hmap'];record(hmap);data=hmap.read_bytes();side=struct.unpack('<I',data[:4])[0]
            box=struct.unpack('<6f',obj['bbox']);grid=p.untile(np.frombuffer(data[8:],dtype='<u2').astype(float),side)/65535*(box[5]-box[2])+box[2]
            ys,xs=np.mgrid[0:side,0:side];x=box[0]+xs/(side-1)*(box[3]-box[0]);y=box[1]+ys/(side-1)*(box[4]-box[1])
            v=np.stack((x,grid,y),axis=-1).reshape(-1,3);gy,gx=np.gradient(grid,(box[4]-box[1])/(side-1),(box[3]-box[0])/(side-1));n=np.stack((-gx,np.ones_like(grid),-gy),axis=-1).reshape(-1,3);n/=np.linalg.norm(n,axis=1)[:,None]
            uv=np.stack((xs/(side-1),1-ys/(side-1)),axis=-1).reshape(-1,2)
            a=(ys[:-1,:-1]*side+xs[:-1,:-1]).reshape(-1);idx=np.stack((a,a+side,a+1,a+1,a+side,a+side+1),axis=-1).reshape(-1)
            add_batch('Landscape',v,n,uv,uv.copy(),idx,material(obj['matname']))
        for c in p.components(e,'LightComponent'):
            l=c['lc.light'];pos=transform[:3,3];direction=-transform[:3,2];direction/=max(np.linalg.norm(direction),1e-9)
            lights.append({'name':label,'type':l['type'],'flags':l['flags'],'position':pos.tolist(),'direction':direction.tolist(),'color':[l.get('color.'+x,1) for x in 'rgb'],'ambient':[l.get('ambColor.'+x,0) for x in 'rgb'],'intensity':l['intensity']})
        for c in p.components(e,'ParticleEffectComponent'):
            for em in p.items(c.get('pe.emitters')):
                config=(src/em['emitter.filename']).resolve();record(config);y=yaml.safe_load(config.read_text());emitter=y.get('emitter',{})
                point=np.r_[em['emitter.position'],1];pos=(transform@point)[:3]
                vector=np.array(emitter.get('emissionVector',[0,0,1]));direction=transform[:3,:3]@vector;direction/=max(np.linalg.norm(direction),1e-9)
                layers=[]
                for key,l in y.items():
                    if not key.startswith('layer') or truth(l.get('isDisabled',False)):continue
                    raw_color=l.get('colorOverLife',[255]*4)
                    colorkeys=keys(raw_color,4,[255]*4)
                    for k in colorkeys:k['value']=[x/255 for x in k['value']]
                    # Preserve each force's lifetime multiplier before summing.
                    # A constant sum erases the authored smoke drift reversals.
                    forces=[]
                    for fi in range(int(l.get('forceCount',0))):
                        f=keys(l.get('force'+str(fi)),3,[0,0,0])
                        multipliers=keys(l.get('forceOverLife'+str(fi)),1,1)
                        times=sorted({0.,1.}|{k['time'] for k in f}|{k['time'] for k in multipliers})
                        def evaluate(curve,t):
                            return np.array([np.interp(t,[k['time'] for k in curve],[k['value'][axis] for k in curve]) for axis in range(len(curve[0]['value']))])
                        forces.append([{'time':t,'value':(transform[:3,:3]@evaluate(f,t)*evaluate(multipliers,t)[0]).tolist()} for t in times])
                    force_times=sorted({0.,1.}|{k['time'] for curve in forces for k in curve})
                    forcekeys=[{'time':t,'value':sum((evaluate(curve,t) for curve in forces),np.zeros(3)).tolist()} for t in force_times]
                    # Native Unity equivalents for the new force descriptors.
                    # Welding authors constant drag, downward gravity and one
                    # emitter-relative collision plane; inactive wind is skipped.
                    drag=0.; gravity=np.zeros(3); planes=[]
                    for fi in range(int(l.get('dragForceCount',0))):
                        suffix=str(fi)
                        if not truth(l.get('forceIsActive'+suffix,True)):continue
                        kind=l.get('forceType'+suffix);power=l.get('dragForcePower'+suffix,[0,0,0])
                        normal=transform[:3,:3]@np.array(l.get('forceDirection'+suffix,[0,0,1]))
                        normal/=max(np.linalg.norm(normal),1e-9)
                        if kind=='drag':drag+=sum(power)/3
                        elif kind=='grav':gravity-=normal*power[0]
                        elif kind=='plncoll':
                            offset=transform[:3,:3]@np.array(l.get('dragForcePosition'+suffix,[0,0,0]))
                            randomized=truth(l.get('randomizeReflectionForce'+suffix))
                            planes.append({'position':(pos+offset).tolist(),'normal':normal.tolist(),
                                'bounceMin':power[0]*(l.get('rndReflectionForceMin'+suffix,1) if randomized else 1),
                                'bounceMax':power[0]*(l.get('rndReflectionForceMax'+suffix,1) if randomized else 1)})
                    for k in forcekeys:k['value']=(np.array(k['value'])+gravity).tolist()
                    layers.append({'name':l.get('name',key),'frames':sprite(l['sprite']), 'single':l.get('layerType')=='single',
                                   'life':scalar(l.get('life'),1),'lifeVariation':scalar(l.get('lifeVariation')),'number':scalar(l.get('number'),1),
                                   'velocity':scalar(l.get('velocity')),'velocityVariation':scalar(l.get('velocityVariation')),
                                   'size':l.get('size',[1,1]),'sizeKeys':keys(l.get('sizeOverLifeXY'),2,[1,1]),'alphaKeys':keys(l.get('alphaOverLife'),1,1),
                                   'colorKeys':colorkeys,'spin':scalar(l.get('spin')),'spinVariation':scalar(l.get('spinVariation')),
                                   'angle':scalar(l.get('angle')),'angleVariation':scalar(l.get('angleVariation')),
                                   'start':scalar(l.get('startTime')),'end':scalar(l.get('endTime'),1000000),'orientation':int(l.get('particleOrientation',1)),
                                   'pivot':l.get('pivotPoint',[0,0]),'fresnel':truth(l.get('useFresToAlpha')),'fresnelPower':l.get('fresToAlphaPower',1),'fresnelBias':l.get('fresToAlphaBias',0),
                                   'additive':l.get('dstBlendFactor')=='BLEND_ONE' or l.get('blending')==3,'fog':truth(l.get('enableFog',True)),
                                   'frameFps':scalar(l.get('frameOverLifeFPS')),'randomFrame':truth(l.get('randomFrameOnStart')),'forceKeys':forcekeys,'drag':drag,'planes':planes,
                                   'source':str(config.relative_to(p.PACK)), 'sourceLayer':key})
                effects.append({'name':label+'_'+str(len(effects)),'position':pos.tolist(),'matrix':flat(transform), 'direction':direction.tolist(),
                                'box':emitter.get('size',[0,0,0]),'emissionAngle':scalar(emitter.get('emissionRange',emitter.get('emissionAngle',0))),
                                'duration':c.get('pe.effectDuration',100),'layers':layers})
        for c in p.components(e,'SoundComponent'):
            for sound in p.items(c):
                if isinstance(sound,dict) and sound.get('eventName'):sounds.append({'name':sound['eventName'],'position':transform[:3,3].tolist()})
    global_mat=next(x for x in mats.values() if x.get('materialName')=='Scene_Global_Material')
    props={k:p.material_property(global_mat,k) for k in global_mat.get('properties',{})}
    env={'linear':bool(global_mat.get('flags',{}).get('FOG_LINEAR')),'density':scalar(props.get('fogDensity')),'color':props.get('fogColor',[0,0,0]),'start':scalar(props.get('fogStart')),'end':scalar(props.get('fogEnd'),100),'limit':scalar(props.get('fogLimit'),1),
         'sky':props.get('fogAtmosphereColorSky',[0,0,0]),'sun':props.get('fogAtmosphereColorSun',[0,0,0]),'distance':scalar(props.get('fogAtmosphereDistance'),500),'scattering':scalar(props.get('fogAtmosphereScattering'),1),'atmosphere':bool(global_mat.get('flags',{}).get('FOG_ATMOSPHERE'))}
    definition={'standHeight':stand_height,'id':name,'scene':str(scene_path.relative_to(p.PACK)),'meshResource':prefix+'meshes','batches':batches,'materials':material_list,'effects':effects,'lights':lights,'animations':animations,'sounds':sounds,'environment':env}
    (target/'scene.json').write_text(json.dumps(definition,separators=(',',':'))+'\n')
    (target/'meshes.bytes').write_bytes(b'BLTZHGR1'+struct.pack('<i',len(payloads))+b''.join(payloads))
    manifest={'id':name,'sources':sources,'sourceScene':definition['scene'],'batches':len(batches),'vertices':sum(b['vertexCount'] for b in batches),'triangles':sum(b['triangleCount'] for b in batches),'materials':len(material_list),'effects':len(effects),'layers':sum(len(e['layers']) for e in effects),'animations':len(animations),'omissions':omissions}
    (target/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    # This generated folder is owned by this porter. Drop obsolete exported
    # names after a material acquires a previously unused source texture.
    expected={resource+'.png' for resource in textures.values()}
    expected.update(frame+'.png' for frames in sprites.values() for frame in frames)
    for sub in ['Textures','Particles']:
        for path in (target/sub).glob('*.png'):
            resource=prefix+str(path.relative_to(target))
            if resource not in expected:
                path.unlink();path.with_suffix('.png.meta').unlink(missing_ok=True)
    print(name, {k:v for k,v in manifest.items() if k not in ['sources','omissions']},flush=True)

if __name__=='__main__':
    for name in ['hangar','hangar_prem_2018']:port(name)
