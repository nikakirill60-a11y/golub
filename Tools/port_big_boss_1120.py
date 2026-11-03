#!/usr/bin/env python3
"""Big Boss (WoT Blitz 11.20 "bossmode") source import.

Reads the original 11.20 client files and writes, reproducibly:

* ``Resources/Data/big_boss_1120.json`` - role, level, ability and UI style
  contract parsed from ``roles.xml``, ``consumables/bossmode.xml``,
  ``provisions/bossmode.xml`` and the inventory/battle style sheets, plus the
  reconstruction compatibility profile (see TASK_PLAN_BIG_BOSS.md section 0).
* ``Resources/BigBoss/Textures/*.png`` - original sprites rebuilt from their
  DAVA descriptors (@2x where the client ships it), never repainted.
* ``Resources/BigBoss/Sources/*.txt`` - verbatim copies of the UI layouts the
  runtime reproduces, kept next to the art for review.
* ``Resources/Data/lang.json`` - the bossmode EN/RU strings of the client.

The mode itself is a reconstruction: the client data has no server scripts,
so every behaviour the files do not define is a documented compatibility
choice in the ``profile`` block, never a claim about the original server.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
import shutil
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import yaml
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT.parent / 'wotblitz11.20.0files'
OUT = ROOT / 'Assets/Blitz/Resources/BigBoss'
DATA = ROOT / 'Assets/Blitz/Resources/Data/big_boss_1120.json'
LANG = ROOT / 'Assets/Blitz/Resources/Data/lang.json'
XML_ROOT = 'XML/item_defs/vehicles/common/'

# roles.xml element -> runtime role key, style suffix and colour class.
ROLES = (
    ('bossRole', 'boss', 'color-red_boss'),
    ('healerRole', 'healer', 'color-green_healer'),
    ('killerRole', 'killer', 'color-purple_killer'),
    ('kamikazeRole', 'kamikaze', 'color-orange_kamikaze'),
    ('gravitonRole', 'graviton', 'color-blue_graviton'),
)

# Sprites of the role HUD (RoleProgressBar.yaml) and the ability card
# (BossmodeAbility*.yaml).
HUD_SPRITES = (
    'Gfx/Battle/HUD/bossmode/bg_border_1px_r4',
    'Gfx/Battle/HUD/bossmode/bg_level',
    'Gfx/Battle/HUD/bossmode/bg_triangle',
    'Gfx/Battle/HUD/bossmode/icon_live',
    'Gfx/Battle/HUD/bossmode/icon_live-bg',
    'Gfx/Battle/HUD/bossmode/icon_wings',
    'Gfx/Battle/HUD/bossmode/role-bg',
    'Gfx/Shared/backgrounds/bg_r1',
    'Gfx/Shared/backgrounds/bg_loader-circle_shadow',
    'Gfx/UI/Hangar/GameTypes/battle-type_bossmode',
    'Gfx/Battle/icons/2020-halloween/icon_plus',
    'Gfx/Battle/icons/2020-halloween/icon_up',
)

LAYOUTS = (
    'UI/Screens3/Battle/HUD/GameModes/RoleProgressBar.yaml',
    'UI/Screens3/Battle/HUD/GameModes/RoleProgressBar.style.yaml',
    'UI/Screens/Battle/OptionalHUDControls/BossmodeAbilitiesPanel.yaml',
    'UI/Screens/Battle/OptionalHUDControls/BossmodeAbility.yaml',
    'UI/Screens/Battle/OptionalHUDControls/BossmodeAbilityType.yaml',
    'UI/Screens/Battle/VehicleUIObjectMarker/BossModeRole.style.yaml',
    'UI/Screens/Battle/VehicleUIObjectMarker/HealerIconAnimation.animation',
    'UI/Screens/Battle/VehicleUIObjectMarker/VehicleUIObjectMarker.yaml',
    'UI/Screens/Battle/OptionalHUDControls/RespawnTimer.yaml',
    'UI/Screens3/Lobby/BattleResults/StatisticsOverlay.yaml',
    'UI/Screens/Battle/Notifications/BattleActionNotification.yaml',
    XML_ROOT + 'roles.xml',
    XML_ROOT + 'consumables/bossmode.xml',
    XML_ROOT + 'provisions/bossmode.xml',
)

# Client strings the mode draws. Every key is copied verbatim from en/ru.yaml.
STRING_PREFIXES = ('#bossmode:',)
STRING_KEYS = (
    'hangar/battleType/bossmodeHeader',
    'hangar/battleType/bossmodeShortDescription',
    'battleType/bossmode',
    '#respawn/awaitReturnToBattleText',
    '#battle_results:DetailsPage/StatisticPage/HealedHP',
    '#arenas:type/bossmode/description',
)

# Rows the 11.20 string tables do not ship (the server sent them or they were
# never localised). They are authored for the reconstruction and flagged as
# such in the manifest; the wording follows the client's own messages.
# Missing client text remains absent. Do not invent labels or descriptions.
RECONSTRUCTED_STRINGS = {'battle_mode:bossmode/name': None, 'battle_mode:bossmode/desc': None}

# The 11.20 "Big Boss" hangar card reuses these client rows.
ALIASES = {
    'battle_mode:bossmode/name': 'battleType/bossmode',
    'battle_mode:bossmode/desc': 'hangar/battleType/bossmodeShortDescription',
}

# Behaviour the client files do not define. Values are compatibility choices of
# this reconstruction (TASK_PLAN_BIG_BOSS.md section 0), not original facts.
PROFILE = {
    'id': 'bigboss_reconstruction_v1',
    'teamSize': 7,
    'startPoints': 30,
    'ordinaryDeathCost': 1,
    'bossDeathCost': 5,
    'bossLives': 3,
    # Fifteen-minute battles: the ticket race needs the time to swing.
    'timeLimitSeconds': 900,
    'respawnSeconds': 7,
    'closeVictoryPoints': 5,
    'rolePointsPerDamage': 0.1,
    'rolePointsPerHeal': 0.1,
    'rolePointsPerKill': 10,
    'roleRevealSeconds': 4,
    'gravityShotRange': 400,
    'gravityShotAccelerationPerMass': 0.1,
    # Radial pull velocity cap (m/s per attractorMass unit); the client only names the mass.
    'gravityShotPullSpeedPerMass': 0.1,
    'gravityShotCoreRadius': 3,
    'gravityBurstVelocityPerForce': 0.000667,
    'gravityBurstLift': 0.25,
    'staticsDamageTypes': ['Collision', 'Environment'],
    # Requested offline balance: the boss hull carries five times the tank's HP.
    'bossMaxHealthFactor': 5,
    # Families that arm themselves when ready instead of using the mode button: the boss's
    # charger and the healer's energy shield, which the next friendly hit hands over. The
    # healing aura is the healer's button ability.
    'autoActivatedFamilies': ['shieldGiverAbility', 'railgunCharger'],
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE)
    args = parser.parse_args()
    source = args.source
    sys.path.insert(0, str(ROOT.parent / 'blitz-legacy-godot/tools'))
    import dvpl

    old_textures = set(json.loads(DATA.read_text()).get('textures', {})) if DATA.exists() else set()
    for folder in ('Sources', 'Textures'):
        target = OUT / folder
        if target.exists():
            for old in target.iterdir():
                if old.suffix == '.txt' or (old.suffix == '.png' and old.stem in old_textures):
                    old.unlink()
        target.mkdir(parents=True, exist_ok=True)
    provenance = []

    def record(path, role):
        entry = dict(source=str(path.relative_to(source)), role=role,
                     sha256=hashlib.sha256(path.read_bytes()).hexdigest())
        if entry not in provenance:
            provenance.append(entry)

    def find(relative):
        for base in (source, source / 'from_apk'):
            for suffix in ('', '.dvpl'):
                path = base / (relative + suffix)
                if path.is_file():
                    return path, bool(suffix)
        return None, False

    def read(relative):
        path, packed = find(relative)
        if path is None:
            raise FileNotFoundError(relative)
        payload = path.read_bytes()
        return (dvpl.unpack_bytes(payload) if packed else payload), path

    def descriptor_of(relative):
        """Descriptor bytes, its path and the embedded atlas for packed WEBPs."""
        try:
            data, path = read(relative + '.txt')
            return data, path, None
        except FileNotFoundError:
            payload, path = read(relative + '.packed.webp')
            atlas = Image.open(io.BytesIO(payload)).convert('RGBA')
            pos = 12
            while pos + 8 <= len(payload):
                tag, size = payload[pos:pos + 4], struct.unpack_from('<I', payload, pos + 4)[0]
                chunk = payload[pos + 8:pos + 8 + size]
                pos += 8 + size + (size & 1)
                if tag == b'extr':
                    return chunk.rstrip(b'\0'), path, atlas
            return None, path, atlas

    def sprite(relative):
        """Rebuilds one sprite; returns (image, scale) with scale 2 for @2x art."""
        for scale, name in ((2, relative + '@2x'), (1, relative)):
            try:
                descriptor, path, atlas = descriptor_of(name)
            except FileNotFoundError:
                continue
            if descriptor is None:
                record(path, 'original-sprite')
                return atlas, scale
            lines = descriptor.decode().splitlines()
            count = int(lines[0])
            names = lines[1:1 + count]
            width, height = map(int, lines[1 + count].split())
            frames = int(lines[2 + count])
            assert frames == 1, (relative, frames)
            x, y, w, h, dx, dy, index = map(int, lines[3 + count].split()[:7])
            if atlas is None:
                payload, atlas_path = read(str(Path(name).parent / names[index]))
                atlas = Image.open(io.BytesIO(payload)).convert('RGBA')
                record(atlas_path, 'original-atlas')
            image = Image.new('RGBA', (width, height))
            image.paste(atlas.crop((x, y, x + w, y + h)), (dx, dy))
            record(path, 'original-sprite-descriptor')
            return image, scale
        raise FileNotFoundError(relative)

    textures = {}

    def export(path_string):
        relative = path_string.replace('~res:/', '').removesuffix('.png')
        key = Path(relative).name
        if key in textures:
            return 'BigBoss/Textures/' + key
        image, scale = sprite(relative)
        image.save(OUT / 'Textures' / (key + '.png'))
        textures[key] = dict(source=relative, pixel_size=list(image.size),
                             logical_size=[image.width / scale, image.height / scale],
                             scale=scale, pixel_sha256=hashlib.sha256(image.tobytes()).hexdigest())
        return 'BigBoss/Textures/' + key

    def load_yaml(relative):
        data, path = read(relative)
        record(path, 'original-ui-layout')
        return yaml.safe_load(data.decode('utf-8-sig'))

    def style_sprites(relative, match):
        """selector -> bg-sprite of a DAVA style sheet, filtered by ``match``."""
        sheet = load_yaml(relative).get('StyleSheets') or []
        found = {}
        for rule in sheet:
            props = rule.get('properties') or {}
            path_string = props.get('bg-sprite') or props.get('atlasImage-imagePath')
            if not path_string:
                continue
            for selector in rule.get('selectors') or []:
                key = match(selector)
                if key and key not in found:
                    found[key] = path_string
        return found

    def style_colors(relative):
        sheet = load_yaml(relative).get('StyleSheets') or []
        colors = {}
        for rule in sheet:
            props = rule.get('properties') or {}
            for selector in rule.get('selectors') or []:
                if selector.startswith('.color-') and 'bg-color' in props:
                    colors[selector[1:]] = props['bg-color']
        return colors

    def number(text):
        text = (text or '').strip()
        if text in ('true', 'false'):
            return text == 'true'
        try:
            value = float(text)
        except ValueError:
            return text
        return int(value) if value.is_integer() and '.' not in text else value

    def flatten(element, prefix, out, duplicates):
        for child in element:
            name = child.tag if not prefix else prefix + '.' + child.tag
            if len(child):
                flatten(child, name, out, duplicates)
                continue
            if name in out:
                duplicates.append(name)
                continue
            out[name] = number(child.text)

    # ----- items ---------------------------------------------------------
    small_icons = style_sprites('UI/Styles/Lobby/Inventory/Event/InventoryNormalStyles.yaml',
        lambda s: s.split('.equipment_item.')[1].split(' ')[0]
        if s.startswith('.equipment_item.') and s.endswith('#icon.small') else None)
    big_icons = style_sprites('UI/Styles/Lobby/Inventory/Event/InventoryBigStyles.yaml',
        lambda s: s.split('.equipment_item.')[1].split(' ')[0]
        if s.startswith('.equipment_item.') and s.endswith('#icon') else None)
    status_icons = style_sprites('UI/Screens/Battle/Styles/BattleEquipmentStyles.yaml',
        lambda s: s.split('.equip_element.')[1].split(' ')[0]
        if s.startswith('.equip_element.') and s.endswith('#Icon') else None)

    abilities = {}
    for kind, relative in (('active', XML_ROOT + 'consumables/bossmode.xml'),
                           ('passive', XML_ROOT + 'provisions/bossmode.xml')):
        data, path = read(relative)
        record(path, 'original-item-definitions')
        for element in ET.fromstring(data):
            script = element.find('script')
            params, duplicates = {}, []
            wrapper = script[0].tag if len(script) == 1 and len(script[0]) else ''
            flatten(script[0] if wrapper else script, '', params, duplicates)
            icon = element.findtext('icon')
            base = element.findtext('userString').split(':')[1].split('/')[0]
            entry = dict(
                id=element.tag, itemId=int(element.findtext('id')), kind=kind,
                script=(script.text or '').strip(), icon=icon, family=base,
                nameKey=element.findtext('userString'),
                descriptionKey=element.findtext('description'),
                hiddenOnPanel=element.find('hideIconOnAbilityPanel') is not None,
                params=params, duplicateParams=duplicates,
                descrParams=[dict(nameKey=d.findtext('name'), value=d.findtext('value'))
                             for d in element.findall('descrParam')])
            if icon in small_icons:
                entry['iconSmall'] = export(small_icons[icon])
            if icon in big_icons:
                entry['iconBig'] = export(big_icons[icon])
            if icon in status_icons:
                entry['iconStatus'] = export(status_icons[icon])
            abilities[element.tag] = entry

    # ----- roles ---------------------------------------------------------
    data, path = read(XML_ROOT + 'roles.xml')
    record(path, 'original-role-definitions')
    roles_xml = ET.fromstring(data)
    thresholds = [0]
    for level in roles_xml.find('levelsSettings'):
        if level.findtext('rolePoints'):
            thresholds.append(int(level.findtext('rolePoints')))
    colors = style_colors('UI/Screens3/Battle/HUD/GameModes/RoleProgressBar.style.yaml')
    role_icons = style_sprites('UI/Screens3/Battle/HUD/GameModes/RoleProgressBar.style.yaml',
        lambda s: s[len('.icon-link_'):] if s.startswith('.icon-link_') else None)
    marker_icons = style_sprites('UI/Screens/Battle/VehicleUIObjectMarker/BossModeRole.style.yaml',
        lambda s: s[len('.role-'):] if s.startswith('.role-') else None)
    roles = []
    for tag, key, color in ROLES:
        element = roles_xml.find(tag)
        levels = []
        for level in element.find('levels'):
            slots = sorted(level.find('abilities'), key=lambda s: int(s.get('index')))
            names = [s.text.strip() for s in slots]
            assert all(n in abilities for n in names), names
            levels.append(names)
        assert len(levels) == len(thresholds), (tag, len(levels))
        roles.append(dict(
            id=int(element.findtext('id')), key=key, element=tag,
            boss=element.find('boss') is not None,
            nameKey=element.findtext('userString'),
            descriptionKey=element.findtext('description'),
            colorClass=color, color=colors[color],
            icon=export(role_icons[key]), marker=export(marker_icons[key]),
            levels=levels))

    for path_string in HUD_SPRITES:
        export(path_string)

    for relative in LAYOUTS:
        payload, path = read(relative)
        target = OUT / 'Sources' / (relative.replace('/', '_') + '.txt')
        target.write_bytes(payload)
        record(path, 'original-layout-copy')

    # ----- strings -------------------------------------------------------
    tables = {}
    for language in ('en', 'ru'):
        data, path = read('Strings/%s.yaml' % language)
        record(path, 'original-strings')
        tables[language] = yaml.safe_load(data.decode('utf-8-sig'))
    wanted = set(STRING_KEYS)
    for entry in abilities.values():
        wanted.add(entry['nameKey'])
        wanted.add(entry['descriptionKey'])
        wanted.update(d['nameKey'] for d in entry['descrParams'])
    for role in roles:
        wanted.add(role['nameKey'])
        wanted.add(role['descriptionKey'])
    for key in tables['en']:
        if key.startswith(STRING_PREFIXES):
            wanted.add(key)
    rows, reconstructed = {}, []
    for key in sorted(wanted | set(RECONSTRUCTED_STRINGS)):
        source_key = ALIASES.get(key, key)
        en, ru = tables['en'].get(source_key), tables['ru'].get(source_key)
        if en and ru:
            rows[key] = dict(en=en, ru=ru)
        elif key in RECONSTRUCTED_STRINGS and RECONSTRUCTED_STRINGS[key]:
            en, ru = RECONSTRUCTED_STRINGS[key]
            rows[key] = dict(en=en, ru=ru)
            reconstructed.append(key)
        else:
            continue  # The supplied client does not contain this text.
    lang = json.loads(LANG.read_text())
    for key in ('#artefacts:healingAura/name', '#artefacts:healingAura/descr', '#artefacts:railgunCharger/descr', '#bossmode:boss_role/descr', '#bossmode:healer_role/descr', '#bossmode:killer_role/descr', '#bossmode:kamikaze_role/descr', '#bossmode:graviton_role/descr', 'big_boss:respawn/in', 'big_boss:reconstruction'):
        if key not in tables['en']:
            lang['keys'].pop(key, None)
    lang['keys'].update(rows)
    LANG.write_text(json.dumps(lang, ensure_ascii=False, indent=2) + '\n')

    hangar_path=ROOT/'Assets/Blitz/Resources/Data/hangar_source_manifest.json'
    hangar=json.loads(hangar_path.read_text());hangar['localization']['keys']=len(lang['keys']);hangar_path.write_text(json.dumps(hangar,ensure_ascii=False,indent=2)+'\n')

    # Keep unavailable source references for audit, but never publish them as
    # drawable localization keys. No fabricated labels or test exclusions.
    unavailable_text = sorted(wanted - rows.keys())
    for entry in [*roles, *abilities.values()]:
        missing = {}
        for field in ('nameKey', 'descriptionKey'):
            if entry[field] not in rows:
                missing[field.removesuffix('Key')] = entry[field]
                entry[field] = ''
        if missing:
            entry['unavailableSourceText'] = missing

    manifest = dict(
        unavailableText=unavailable_text,
        sourceVersion='11.20.0', status='reconstruction',
        notice='Reconstruction of the WoT Blitz 11.20 Big Boss mode from client data; '
               'server-side behaviour is a documented compatibility profile.',
        levels=thresholds, profile=PROFILE, roles=roles, abilities=abilities,
        reconstructedStrings=reconstructed, textures=textures, sources=provenance)
    DATA.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps(dict(roles=len(roles), abilities=len(abilities), textures=len(textures),
                          strings=len(rows), reconstructed=len(reconstructed),
                          sources=len(provenance)), ensure_ascii=False))


if __name__ == '__main__':
    main()
