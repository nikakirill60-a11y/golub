#!/usr/bin/env python3
"""Lossless 11.20 Big Boss presentation port. SC2/SCG decoding follows port_uprising.py.
Only scenes/media present in the supplied client are imported; missing resources fail closed.
"""
import argparse,hashlib,io,json,math,struct,sys
from pathlib import Path
from PIL import Image
ROOT=Path(__file__).resolve().parents[1]
def main():
    ap=argparse.ArgumentParser();ap.add_argument('--source',type=Path,default=ROOT.parent/'wotblitz11.20.0files');args=ap.parse_args();src=args.source
    sys.path.insert(0,str(ROOT.parent/'blitz-legacy-godot/tools'));import dava,dvpl
    out=ROOT/'Assets/Blitz/Resources/BigBoss';data=ROOT/'Assets/Blitz/Resources/Data'
    for f in ['Sources','Textures']: (out/f).mkdir(parents=True,exist_ok=True)
    provenance=[]
    def save(p,v): p.write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n')
    def record(p,root,role):
        item=dict(source=str(p.relative_to(root)),role=role,sha256=hashlib.sha256(p.read_bytes()).hexdigest())
        if item not in provenance:provenance.append(item)
    effects={};textures={};types={};features={}
    def clean(v):
        if isinstance(v,bytes):return {'hex':v.hex()}
        if isinstance(v,dict):return {k:clean(x) for k,x in v.items()}
        if isinstance(v,(tuple,list)):return [clean(x) for x in v]
        return v
    def read(rel):
        for root in [src,src/'from_apk']:
            for suffix in ['', '.dvpl']:
                p=root/(rel+suffix)
                if p.exists():return (dvpl.unpack_bytes(p.read_bytes()) if suffix else p.read_bytes()),p
        raise FileNotFoundError(rel)
    def walk(v):
        if isinstance(v,dict):
            yield v
            for x in v.values():yield from walk(x)
        elif isinstance(v,(list,tuple)):
            for x in v:yield from walk(x)
    for directory in ['Boss_mode','UI/Bossmode','halloween_skills']:
        for p in sorted((src/'from_apk/3d/FX'/directory).glob('*.sc2')):
            if directory == 'halloween_skills' and p.stem != 'heal': continue
            decoded=dava.read_sc2(p.read_bytes());raw=decoded['scene']; key=directory+'/'+p.stem
            geometry=p.with_suffix('.scg')
            if geometry.exists():
                save(out/'Sources'/(directory.replace('/', '_')+'_'+p.stem+'_geometry.json'),clean(dava.read_scg(geometry.read_bytes())))
                record(geometry,src,'original-fx-geometry')
            save(out/'Sources'/(directory.replace('/', '_')+'_'+p.stem+'.json'),clean(raw));record(p,src,'original-fx')
            layers=[x for x in walk(raw) if 'layerType' in x]
            for layer in layers:
                typ=layer['layerType'];types[typ]=types.get(typ,0)+1
                for feature,value in layer.items():
                    if feature.startswith(('enable','use')) and value is True:features[feature]=features.get(feature,0)+1
                for field in ['spritePath','noisePath','maskSpritePath','flowmapPath','alphaRemapPath']:
                    path=layer.get(field)
                    if not path:continue
                    if path in textures:
                        if field not in textures[path].setdefault("usage",[]):textures[path]["usage"].append(field)
                        continue
                    rel=path.replace('~res:/','')
                    try:
                        try:
                            descriptor,tp=read(rel+'.txt')
                            lines=descriptor.decode().splitlines(); n=int(lines[0]); atlases=lines[1:1+n]
                            w,h=map(int,lines[1+n].split()); frames=int(lines[2+n]); cols=__import__('math').ceil(__import__('math').sqrt(frames)); rows=__import__('math').ceil(frames/cols)
                            image=Image.new('RGBA',(w*cols,h*rows)); loaded={}
                            for i,line in enumerate(lines[3+n:3+n+frames]):
                                x,y,cw,ch,dx,dy,idx=map(int,line.split()[:7])
                                if idx not in loaded:
                                    b,atlas=read(str(Path(rel).parent/atlases[idx]));loaded[idx]=Image.open(io.BytesIO(b)).convert('RGBA');record(atlas,src,'original-fx-atlas')
                                image.paste(loaded[idx].crop((x,y,x+cw,y+ch)),(i%cols*w+dx,i//cols*h+dy))
                        except FileNotFoundError:
                            image_bytes,tp=read(rel+'.packed.webp')
                            atlas_image=Image.open(io.BytesIO(image_bytes)).convert('RGBA');image=atlas_image;frames=cols=rows=1
                            # Original packed WEBP contains a RIFF `extr` chunk: the sprite
                            # descriptor is embedded in the asset, not inferred from its name.
                            pos=12
                            while pos+8<=len(image_bytes):
                                chunk=image_bytes[pos:pos+4];size=struct.unpack_from('<I',image_bytes,pos+4)[0]
                                payload=image_bytes[pos+8:pos+8+size];pos+=8+size+(size&1)
                                if chunk!=b'extr':continue
                                lines=payload.rstrip(b'\0').decode('utf-8').splitlines();n=int(lines[0]);w,h=map(int,lines[1+n].split());frames=int(lines[2+n]);cols=__import__('math').ceil(__import__('math').sqrt(frames));rows=__import__('math').ceil(frames/cols)
                                image=Image.new('RGBA',(w*cols,h*rows))
                                for i,line in enumerate(lines[3+n:3+n+frames]):
                                    x,y,cw,ch,dx,dy,idx=map(int,line.split()[:7])
                                    if idx!=0:raise ValueError('Packed WEBP references an external atlas: '+rel)
                                    image.paste(atlas_image.crop((x,y,x+cw,y+ch)),(i%cols*w+dx,i//cols*h+dy))
                                break
                        name=Path(rel).name+'_'+hashlib.sha256(path.encode()).hexdigest()[:10]
                        image.save(out/'Textures'/(name+'.png'))
                        textures[path]=dict(resource='BigBoss/Textures/'+name,width=image.width,height=image.height,frames=frames,cols=cols,rows=rows,usage=[field])
                        record(tp,src,'original-fx-texture')
                    except FileNotFoundError:
                        textures[path]=dict(missing=True)
            effects[key]=dict(resource='BigBoss/Sources/'+directory.replace('/', '_')+'_'+p.stem,layers=len(layers),types=sorted(set(x['layerType'] for x in layers)))
    missing=[k for k,v in textures.items() if v.get('missing')]
    save(data/'big_boss_fx_1120.json',dict(sourceVersion='11.20.0',effects=effects,textures=textures,layerTypes=types,features=features,sources=provenance,missing=missing))
    print(json.dumps(dict(effects=len(effects),textures=len(textures),missing=missing,features=features)))
    # Absent test textures stay absent. Runtime omits only layers that require them;
    # the manifest records the gap instead of painting or borrowing a substitute.
if __name__=='__main__':main()
