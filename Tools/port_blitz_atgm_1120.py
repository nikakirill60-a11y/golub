#!/usr/bin/env python3
"""Original Blitz 11.20 modern-ATGM artwork/SC2 extraction. No painted substitutes."""
from __future__ import annotations
import argparse, hashlib, io, json, math, re, shutil, struct, sys
from pathlib import Path
from PIL import Image
import yaml

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT.parent / 'wotblitz11.20.0files'
OUT = ROOT / 'Assets/Blitz/Resources/Atgm1120'
DATA = ROOT / 'Assets/Blitz/Resources/Data/atgm_1120_source_manifest.json'
PROFILE = 'blitz1120_marker'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE)
    args = parser.parse_args()
    source = args.source
    sys.path.insert(0, str(ROOT.parent / 'blitz-legacy-godot/tools'))
    import dava, dvpl
    for folder in ('Sources', 'Textures'):
        (OUT / folder).mkdir(parents=True, exist_ok=True)
    provenance, textures, effects = [], {}, {}

    def record(path, role):
        entry = dict(source=str(path.relative_to(source)), role=role,
                     sha256=hashlib.sha256(path.read_bytes()).hexdigest())
        if entry not in provenance:
            provenance.append(entry)

    def read(relative):
        for root in (source, source / 'from_apk'):
            for suffix in ('', '.dvpl'):
                path = root / (relative + suffix)
                if path.is_file():
                    payload = path.read_bytes()
                    return (dvpl.unpack_bytes(payload) if suffix else payload), path
        raise FileNotFoundError(relative)

    def save(path, value):
        path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')

    def clean(value):
        if isinstance(value, bytes):
            return {'hex': value.hex()}
        if isinstance(value, dict):
            return {k: clean(v) for k, v in value.items()}
        if isinstance(value, (list, tuple)):
            return [clean(v) for v in value]
        return value

    def walk(value):
        if isinstance(value, dict):
            yield value
            for v in value.values():
                yield from walk(v)
        elif isinstance(value, (list, tuple)):
            for v in value:
                yield from walk(v)

    def sprite(relative, hd=False):
        descriptor = None
        embedded = False
        if hd:
            try:
                descriptor, path = read(relative + '@2x.txt')
            except FileNotFoundError:
                pass
        if descriptor is None:
            try:
                descriptor, path = read(relative + '.txt')
            except FileNotFoundError:
                payload, path = read(relative + '.packed.webp')
                atlas = Image.open(io.BytesIO(payload)).convert('RGBA')
                embedded = True
                pos = 12
                while pos + 8 <= len(payload):
                    tag, size = payload[pos:pos+4], struct.unpack_from('<I', payload, pos+4)[0]
                    chunk = payload[pos+8:pos+8+size]
                    pos += 8 + size + (size & 1)
                    if tag == b'extr':
                        descriptor = chunk.rstrip(b'\0')
                        break
                if descriptor is None:
                    record(path, 'original-sprite')
                    return atlas, 1, 1, 1
        lines = descriptor.decode().splitlines()
        count = int(lines[0]); names = lines[1:1+count]
        width, height = map(int, lines[1+count].split())
        frames = int(lines[2+count]); cols = math.ceil(math.sqrt(frames)); rows = math.ceil(frames/cols)
        image = Image.new('RGBA', (width*cols, height*rows))
        loaded = {}
        for i, line in enumerate(lines[3+count:3+count+frames]):
            x, y, w, h, dx, dy, index = map(int, line.split()[:7])
            if index not in loaded:
                if embedded:
                    assert index == 0, 'Packed WEBP references an external atlas'
                    loaded[index] = atlas
                else:
                    payload, atlas_path = read(str(Path(relative).parent / names[index]))
                    loaded[index] = Image.open(io.BytesIO(payload)).convert('RGBA')
                    record(atlas_path, 'original-atlas')
            image.paste(loaded[index].crop((x,y,x+w,y+h)), (i%cols*width+dx, i//cols*height+dy))
        record(path, 'original-sprite-descriptor')
        return image, frames, cols, rows

    marker_path = source / 'from_apk/UI/Screens/Battle/AtgmMarker.yaml'
    marker = yaml.safe_load(marker_path.read_text())['Prototypes'][0]
    content = marker['children'][0]
    policy = marker['components']['SizePolicy']
    width, height = policy['horizontalValue'], policy['verticalValue']
    sprite_path = content['components']['UITextureComponent']['spritePath']
    image, frames, _, _ = sprite(sprite_path.replace('~res:/', ''), hd=True)
    assert frames == 1 and image.size == (int(width*2), int(height*2)), image.size
    image.save(OUT / 'Textures/aim_rocket_marker.png')
    animation_path = source / 'from_apk/UI/Screens/Battle/AtgmMarkerExplosionAnimation.animation'
    animation = animation_path.read_text()
    duration = float(re.search(r'Opacity\("MarkerContent",\s*0\.0,\s*([\d.]+)\)', animation)[1])
    multiplier = float(re.search(r'"horizontalValue",\s*([\d.]+),', animation)[1])/100
    assert (width, height, marker['pivot'], duration, multiplier) == (46,34,[.5,.4],.75,1.75)
    for path in (marker_path, animation_path):
        shutil.copyfile(path, OUT / 'Sources' / (path.name + '.txt'))
        record(path, 'original-marker-layout' if path == marker_path else 'original-marker-animation')

    for name in ('AGTM_raketa', 'AGTM_Tracer_trail', 'AGTM_Explosion'):
        path = source / 'from_apk/3d/FX/ATGM_fx' / (name + '.sc2')
        raw = dava.read_sc2(path.read_bytes())['scene']
        save(OUT / 'Sources' / (name + '.json'), clean(raw))
        record(path, 'original-atgm-fx')
        layers = [v for v in walk(raw) if 'layerType' in v]
        for layer in layers:
            for field in ('spritePath', 'noisePath', 'maskSpritePath', 'flowmapPath', 'alphaRemapPath'):
                path_string = layer.get(field)
                if not path_string or path_string in textures:
                    continue
                frame_image, frames, cols, rows = sprite(path_string.replace('~res:/',''))
                key = Path(path_string).name + '_' + hashlib.sha256(path_string.encode()).hexdigest()[:10]
                frame_image.save(OUT / 'Textures' / (key + '.png'))
                textures[path_string] = dict(resource='Atgm1120/Textures/'+key,
                    width=frame_image.width, height=frame_image.height, frames=frames, cols=cols, rows=rows,
                    pixel_sha256=hashlib.sha256(frame_image.tobytes()).hexdigest())
        effects[name] = dict(resource='Atgm1120/Sources/'+name, layers=len(layers),
                             types=sorted({v['layerType'] for v in layers}))
    save(DATA, dict(sourceVersion='11.20.0', profile=PROFILE,
        policy='Modern ATGM shell icon atgm_heat; legacy T49 Rocket hc/he_premium retain their 4.8 trail',
        marker=dict(resource='Atgm1120/Textures/aim_rocket_marker', logical_size=[width,height],
            native_pivot=marker['pivot'], pixel_size=list(image.size),
            pixel_sha256=hashlib.sha256(image.tobytes()).hexdigest(),
            explosion=dict(seconds=duration, scale_to=multiplier, alpha_from=1, alpha_to=0)),
        effects=effects, textures=textures, sources=provenance))
    print(json.dumps(dict(marker=list(image.size), logical_size=[width,height], effects=len(effects),
                         textures=len(textures), source_files=len(provenance)), ensure_ascii=False))

if __name__ == '__main__':
    main()
