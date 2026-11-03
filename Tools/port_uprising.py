#!/usr/bin/env python3
"""Reproducible Uprising catalogue/UI and ORIGINAL 11.20 FX extraction.
Never imports Godot's particles. Raw DAVA fields are retained losslessly.
"""
import argparse, ast, csv, hashlib, io, json, os, re, shutil, struct, subprocess, sys, tempfile
from pathlib import Path
from PIL import Image
ROOT = Path(__file__).resolve().parents[1]

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--godot', type=Path, default=ROOT.parent/'blitz-legacy-godot')
    ap.add_argument('--source', type=Path, default=ROOT.parent/'wotblitz11.20.0files')
    args = ap.parse_args(); g, src = args.godot, args.source
    sys.path.insert(0, str(g/'tools')); import dava, dvpl
    res = ROOT/'Assets/Blitz/Resources'; data = res/'Data'; out = res/'Uprising'
    out.mkdir(exist_ok=True);
    shutil.copyfile(g/'assets/fonts/arialmb.ttf',out/'arialmb.ttf'); (out/'Textures').mkdir(exist_ok=True); (out/'Sources').mkdir(exist_ok=True)
    def save(p,v): p.write_text(json.dumps(v,ensure_ascii=False,indent="\t")+'\n')
    provenance=[]
    def record(p,root,role):
        provenance.append(dict(source=str(p.relative_to(root)),role=role,sha256=hashlib.sha256(p.read_bytes()).hexdigest()))
    audio_out=out/'Audio';audio_out.mkdir(exist_ok=True)
    for audio in sorted((g/'assets/sfx/uprising').glob('*.ogg')):
        shutil.copyfile(audio,audio_out/audio.name);record(audio,g,'decoded-original-audio')
    shutil.copyfile(g/'assets/sfx/uprising/manifest.json',out/'Sources/audio_manifest.json')
    catalog = g/'assets/data/uprising_abilities.gd'; text=catalog.read_text()
    records=[]
    for part,kind in [('ACTIVES',0),('ELIXIRS',1)]:
        s=text.split('const '+part+': Array = [',1)[1].split('\n]\n',1)[0]
        s=re.sub(r'#[^\n]*','',s).replace('Kind.ACTIVE','0').replace('Kind.ELIXIR','1').replace('true','True').replace('false','False')
        records.extend(ast.literal_eval('['+s+']'))
    save(data/'uprising_abilities.json',dict(abilities=records));record(catalog,g,'logic')
    for folder,dest in [('assets/gfx/ui/inventory/uprising','Icons'),('assets/gfx/ui/hangar/battle_mode_cards','Modes')]:
        target=out/dest;target.mkdir(exist_ok=True)
        for p in sorted((g/folder).glob('*.png')):
            if dest=='Modes' and p.stem not in ['normal','uprising']: continue
            shutil.copyfile(p,target/p.name);record(p,g,'ui')
    lang=json.loads((data/'lang.json').read_text())
    for p in [g/'.assets/lang.csv',g/'lang_extra.csv',g/'lang_dump.csv']:
        if not p.exists():continue
        with p.open(newline='', encoding='utf-8-sig') as f:
            reader=csv.DictReader(f)
            for row in reader:
                key=row.get('keys',row.get('key',row.get('', '')))
                if key.startswith(('uprising:','battle_mode:')):
                    lang['keys'][key]={k:v for k,v in row.items() if k in ('en','ru')}
    import yaml
    for language in ['en','ru']:
        original=src/'from_apk/Strings'/(language+'.yaml')
        strings=yaml.safe_load(original.read_text())
        for suffix,source_suffix in [('name','Name'),('desc','Description')]:
            lang['keys'].setdefault('uprising:ability/enemyReloadTime/'+suffix,{})[language]=strings['provisions/enemyReloadTime/'+source_suffix]
        record(original,src,'original-localization')
    save(data/'lang.json',lang)
    manifest_file=data/'hangar_source_manifest.json'
    manifest=json.loads(manifest_file.read_text());manifest['localization']['keys']=len(lang['keys'])
    manifest_file.write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n')
    for p in [g/'assets/gfx/ui/hangar/battle_mode_menu.tscn',g/'assets/gfx/ui/battle/uprising_ability_panel.tscn']:
        shutil.copyfile(p,out/'Sources'/(p.stem+'.txt'));record(p,g,'ui-layout')
    with tempfile.TemporaryDirectory(prefix='blitz-uprising-ui-') as temporary:
        temporary=Path(temporary)
        (temporary/'assets').symlink_to(g/'assets',target_is_directory=True)
        (temporary/'.godot').mkdir()
        shutil.copyfile(g/'.godot/uid_cache.bin',temporary/'.godot/uid_cache.bin')
        (temporary/'.godot/imported').symlink_to(g/'.godot/imported',target_is_directory=True)
        (temporary/'project.godot').write_text('[application]\nconfig/name="Uprising UI Export"\n[rendering]\nrenderer/rendering_method="gl_compatibility"\n')
        shutil.copyfile(ROOT/'Tools/godot_uprising_ui_dump.gd',temporary/'dump.gd')
        try:
            result=subprocess.run([os.environ.get('GODOT','godot'),'--headless','--path',str(temporary),'--script','res://dump.gd'],capture_output=True,text=True,timeout=60,env=dict(os.environ,BLITZ_UPRISING_UI_OUT=str(out/'Icons')))
        except subprocess.TimeoutExpired as ex:
            raise RuntimeError((ex.stdout or b'').decode(errors='replace')[-2500:]+(ex.stderr or b'').decode(errors='replace')[-6000:]) from ex
        if result.returncode or 'UPRISING_UI_STYLES_EXPORTED' not in result.stdout:
            raise RuntimeError(result.stdout[-2000:]+result.stderr[-2000:])
    font_path=out/'menu_default.ttf'
    if font_path.read_bytes()[:4] in [b'wOFF',b'wOF2']:
        from fontTools.ttLib import TTFont
        font=TTFont(font_path);font.flavor=None;font.save(font_path)
    styles=json.loads((out/'Icons/style_manifest.json').read_text())
    for rel in ['assets/gfx/ui/common/style_button_generic.stylebox','assets/gfx/ui/common/style_button_generic_press.stylebox','assets/gfx/ui/battle/uprising_selected_style.tres','assets/fonts/arialmb.ttf','assets/fonts/ui_theme_blitz.tres','assets/fonts/WarHeliosCondC.ttf','assets/fonts/WarHeliosCondCBold.ttf','scripts/ui_global.gd','project.godot']:
        record(g/rel,g,'ui-style')
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
    for directory in ['halloween2019','halloween_skills']:
        for p in sorted((src/'from_apk/3d/FX'/directory).glob('*.sc2')):
            decoded=dava.read_sc2(p.read_bytes());raw=decoded['scene']; key=directory+'/'+p.stem
            geometry=p.with_suffix('.scg')
            if geometry.exists():
                save(out/'Sources'/(directory+'_'+p.stem+'_geometry.json'),clean(dava.read_scg(geometry.read_bytes())))
                record(geometry,src,'original-fx-geometry')
            save(out/'Sources'/(directory+'_'+p.stem+'.json'),clean(raw));record(p,src,'original-fx')
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
                        textures[path]=dict(resource='Uprising/Textures/'+name,width=image.width,height=image.height,frames=frames,cols=cols,rows=rows,usage=[field])
                        record(tp,src,'original-fx-texture')
                    except FileNotFoundError:
                        textures[path]=dict(missing=True)
            effects[key]=dict(resource='Uprising/Sources/'+directory+'_'+p.stem,layers=len(layers),types=sorted(set(x['layerType'] for x in layers)))
    shaders=src/'from_apk/Materials/Shaders'
    shader_out=out/'Sources/Shaders';shader_out.mkdir(exist_ok=True)
    for relative in ['Default/particle-materials-fp.sl','Default/particle-materials-vp.sl','Default/mesh-particle-materials-fp.sl','Default/mesh-particle-materials-vp.sl','common.slh','blending.slh','fresnel-shlick.slh']:
        original=shaders/relative
        if original.exists():
            shutil.copyfile(original,shader_out/(original.name+'.txt'));record(original,src,'original-fx-shader')
    menu_theme=dict(font='Uprising/menu_default',fontSha256=hashlib.sha256((out/'menu_default.ttf').read_bytes()).hexdigest(),styleResource='Uprising/Icons/style_manifest',defaultThemeSource='project.godot gui/theme/custom: assets/fonts/ui_theme_blitz.tres')
    save(data/'uprising_source_manifest.json',dict(menuTheme=menu_theme,sourceVersion='11.20.0',effects=effects,textures=textures,layerTypes=types,features=features,sources=provenance))
    print(json.dumps(dict(abilities=len(records),effects=len(effects),textures=len(textures),missing=[k for k,v in textures.items() if v.get('missing')],layerTypes=types,features=features),ensure_ascii=False))
if __name__=='__main__':main()
