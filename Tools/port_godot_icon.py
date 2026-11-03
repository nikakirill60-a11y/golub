#!/usr/bin/env python3
'''Port the Godot application icon to the Unity client.

Source of truth, blitz-legacy-godot/project.godot:

    config/icon=uid://bc5q661nysgiq  -> icon.png, 256x256
    boot_splash/bg_color=Color(...)  -> the adaptive background colour

The Godot Android preset leaves every launcher_icons/* entry empty, so its
export falls back to the project icon for all launcher densities. Unity has no
such fallback: each icon kind has to point at a project asset, so this script
writes those assets and Assets/Blitz/EditorTools/AppIconSetup.cs assigns them.

Outputs:
    Assets/Blitz/AppIcon/app_icon.png                     legacy + round
    Assets/Blitz/AppIcon/app_icon_adaptive_foreground.png 432x432
    Assets/Blitz/AppIcon/app_icon_adaptive_background.png 432x432
    Assets/Blitz/Resources/Data/app_icon_source_manifest.json
'''

import hashlib
import json
import os
import sys
from pathlib import Path

from PIL import Image

UNITY_ROOT = Path(__file__).resolve().parents[1]
GODOT_ROOT = Path(os.environ.get(
    'BLITZ_GODOT_ROOT', str(UNITY_ROOT.parent / 'blitz-legacy-godot')))
ICON_DIR = UNITY_ROOT / 'Assets' / 'Blitz' / 'AppIcon'
MANIFEST_PATH = (UNITY_ROOT / 'Assets' / 'Blitz' / 'Resources' / 'Data'
                 / 'app_icon_source_manifest.json')

# The legacy and round launcher icons are one bitmap each. 512 is what Google
# Play asks for and it is the largest density Unity generates from, so the
# 256x256 source is upscaled once here instead of once per density.
LEGACY_SIZE = 512
ADAPTIVE_SIZE = 432
# Android draws an adaptive icon on a 108dp canvas but only guarantees the
# middle 72dp is visible. Godot's own fallback blows the project icon up to the
# whole canvas and lets the launcher mask crop it, which eats the logo edges,
# so the foreground here keeps the icon inside the guaranteed area.
SAFE_ZONE = 72.0 / 108.0

# .godot/ and android/build/ hold imported copies of the same icon, and addons
# ship their own icon.png files; neither is the project icon.
SKIP_DIRS = {'.git', '.godot', 'android', 'addons', 'output'}


def fail(message):
    print('port_godot_icon: ' + message, file=sys.stderr)
    raise SystemExit(2)


def project_settings():
    '''config/icon and boot_splash/bg_color, straight out of project.godot.'''
    path = GODOT_ROOT / 'project.godot'
    if not path.is_file():
        fail('no project.godot under ' + str(GODOT_ROOT))

    icon = None
    background = None
    for line in path.read_text(encoding='utf-8').splitlines():
        if line.startswith('config/icon='):
            icon = line.split('=', 1)[1].strip().strip(chr(34))
        elif line.startswith('boot_splash/bg_color='):
            value = line.split('=', 1)[1].strip()
            inner = value[value.index('(') + 1:value.rindex(')')]
            background = [float(part) for part in inner.split(',')]

    if not icon:
        fail('project.godot carries no config/icon')
    if background is None or len(background) < 3:
        fail('project.godot carries no boot_splash/bg_color')
    return icon, background


def resolve(reference):
    '''A res:// path is literal; a uid:// one is owned by an .import file.'''
    if reference.startswith('res://'):
        return GODOT_ROOT / reference[len('res://'):]
    if not reference.startswith('uid://'):
        fail('unsupported icon reference: ' + reference)

    for root, dirs, files in os.walk(GODOT_ROOT):
        dirs[:] = [name for name in dirs if name not in SKIP_DIRS]
        for name in files:
            if not name.endswith('.import'):
                continue
            path = Path(root) / name
            if reference in path.read_text(encoding='utf-8', errors='ignore'):
                return path.with_suffix('')
    fail('cannot resolve ' + reference + ' under ' + str(GODOT_ROOT))


def rgb8(color):
    return tuple(max(0, min(255, int(round(channel * 255.0))))
                 for channel in color[:3])


def main():
    reference, background_color = project_settings()
    source = resolve(reference)
    if not source.is_file():
        fail('the project icon is missing: ' + str(source))

    raw = source.read_bytes()
    icon = Image.open(source).convert('RGBA')
    if icon.size[0] != icon.size[1]:
        fail('the project icon is not square: ' + str(icon.size))

    ICON_DIR.mkdir(parents=True, exist_ok=True)
    MANIFEST_PATH.parent.mkdir(parents=True, exist_ok=True)

    # Legacy and round share one bitmap: Godot has a single project icon and no
    # round variant, so inventing one would not come from the source.
    legacy = icon.resize((LEGACY_SIZE, LEGACY_SIZE), Image.LANCZOS)
    legacy.save(ICON_DIR / 'app_icon.png')

    inner = int(round(ADAPTIVE_SIZE * SAFE_ZONE))
    offset = (ADAPTIVE_SIZE - inner) // 2
    foreground = Image.new('RGBA', (ADAPTIVE_SIZE, ADAPTIVE_SIZE), (0, 0, 0, 0))
    foreground.paste(icon.resize((inner, inner), Image.LANCZOS), (offset, offset))
    foreground.save(ICON_DIR / 'app_icon_adaptive_foreground.png')

    fill = rgb8(background_color)
    background = Image.new('RGBA', (ADAPTIVE_SIZE, ADAPTIVE_SIZE), fill + (255,))
    background.save(ICON_DIR / 'app_icon_adaptive_background.png')

    manifest = {
        'source_project': 'blitz-legacy-godot',
        'source_reference': reference,
        'source_file': str(source.relative_to(GODOT_ROOT)),
        'source_sha256': hashlib.sha256(raw).hexdigest(),
        'source_size': list(icon.size),
        'adaptive_safe_zone': round(SAFE_ZONE, 6),
        'background_color': {
            'godot': [round(channel, 8) for channel in background_color],
            'srgb_hex': '#' + ''.join('{:02X}'.format(c) for c in fill),
            'rgb8': list(fill),
        },
        'outputs': {
            'legacy': {
                'path': 'Assets/Blitz/AppIcon/app_icon.png',
                'size': [LEGACY_SIZE, LEGACY_SIZE],
            },
            'round': {
                'path': 'Assets/Blitz/AppIcon/app_icon.png',
                'size': [LEGACY_SIZE, LEGACY_SIZE],
            },
            'adaptive_foreground': {
                'path': 'Assets/Blitz/AppIcon/app_icon_adaptive_foreground.png',
                'size': [ADAPTIVE_SIZE, ADAPTIVE_SIZE],
                'inner_size': [inner, inner],
            },
            'adaptive_background': {
                'path': 'Assets/Blitz/AppIcon/app_icon_adaptive_background.png',
                'size': [ADAPTIVE_SIZE, ADAPTIVE_SIZE],
            },
        },
    }
    MANIFEST_PATH.write_text(
        json.dumps(manifest, indent=2, sort_keys=True) + chr(10), encoding='utf-8')

    print('port_godot_icon: ' + str(source) + ' -> ' + str(ICON_DIR))
    print('port_godot_icon: adaptive background '
          + manifest['background_color']['srgb_hex'])


if __name__ == '__main__':
    main()
