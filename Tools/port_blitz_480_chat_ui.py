#!/usr/bin/env python3
"""Port the Blitz 4.8 battle chat art and strings.

Sources (``Gfx2/UI/BattleScreenHUD``):

* ``ChatIcons`` - three channel pictograms used by ``ChatDialog.yaml``:
  frame0 ``sendAll`` (general chat), frame1 ``sendTeam``, frame2 ``sendSquad``.
* ``TextChatBg`` - the stretchable 72x72 backdrop of the input strip.
* ``ChatMenu`` - frame0 is the battle HUD chat button pictogram.

Every frame is baked into its authored frame canvas (descriptor size plus the
per-frame offsets) and shelf-packed into one texture, like the damage UI port.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "Assets/Blitz/Resources/Data"
TEXTURES = ROOT / "Assets/Blitz/Resources/Textures"
ATLAS = DATA / "atlas_regions.json"
LANG = DATA / "lang.json"
MANIFEST = DATA / "chat_ui_source_manifest.json"

TEXTURE_NAME = "battle_chat"
ATLAS_SIZE = (512, 256)
PADDING = 2

# 4.8 source strings (keys kept as in the client).
SOURCE_STRING_KEYS = [
    "BATTLE_TEXT_CHAT",
    "#settings:Controls/GlobalChat/Title",
    "#settings:Controls/TeamChat/Title",
]
# Strings the 4.8 client had on its server, written here for en/ru.
OWN_STRINGS = {
    "battle:chat/placeholder": {
        "en": "Enter - to team, button - to everyone",
        "ru": "Enter - команде, кнопка - всем",
    },
    "battle:chat/muted": {
        "en": "Too many messages. Chat is paused for {0} s.",
        "ru": "Слишком много сообщений. Чат приостановлен на {0} с.",
    },
    "battle:chat/notice_teamkill_kick": {
        "en": "Player {0} exceeded the team damage limit and was removed from the battle",
        "ru": "Игрок {0} превысил лимит урона союзникам и был исключён из боя",
    },
    "battle:chat/notice_turned_blue": {
        "en": "Player {0} has dealt too much damage to allies",
        "ru": "Игрок {0} нанёс слишком много урона союзникам",
    },
}
LANGUAGES = ("en", "ru")


def pack_root() -> Path:
    env = os.environ.get("BLITZ_PACK")
    candidates = [Path(env)] if env else []
    candidates += [Path.home() / "projects" / "wotblitz4.8.0files"]
    for path in candidates:
        if path.is_dir():
            return path
    raise SystemExit("Blitz 4.8 pack not found; set BLITZ_PACK")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_descriptor(path: Path):
    lines = [line.strip() for line in path.read_text().splitlines() if line.strip()]
    width, height = (int(v) for v in lines[2].split()[:2])
    count = int(lines[3])
    frames = [tuple(int(v) for v in line.split()[:6]) for line in lines[4 : 4 + count]]
    if len(frames) != count:
        raise SystemExit(f"{path.name}: expected {count} frames, found {len(frames)}")
    return (width, height), frames


def bake(atlas: Image.Image, size, frame) -> Image.Image:
    x, y, w, h, xoff, yoff = frame
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    canvas.paste(atlas.crop((x, y, x + w, y + h)), (xoff, yoff))
    return canvas


def collect(pack: Path):
    hud = pack / "Gfx2/UI/BattleScreenHUD"
    sources = [hud / "texture0.mali.webp", hud / "ChatIcons.txt", hud / "TextChatBg.txt", hud / "ChatMenu.txt"]
    for path in sources:
        if not path.is_file():
            raise SystemExit(f"missing pack source: {path}")
    atlas = Image.open(sources[0]).convert("RGBA")
    images = {}
    size, frames = read_descriptor(sources[1])
    for index, name in enumerate(["chat_icon_all", "chat_icon_team", "chat_icon_squad"]):
        images[name] = bake(atlas, size, frames[index])
    size, frames = read_descriptor(sources[2])
    images["chat_input_bg"] = bake(atlas, size, frames[0])
    size, frames = read_descriptor(sources[3])
    images["chat_button_icon"] = bake(atlas, size, frames[0])
    images["chat_button_icon_alt"] = bake(atlas, size, frames[1])
    return images, sources


def shelf_pack(images):
    sheet = Image.new("RGBA", ATLAS_SIZE, (0, 0, 0, 0))
    regions = {}
    pen_x = pen_y = shelf_h = 0
    for name in sorted(images, key=lambda n: (-images[n].height, n)):
        image = images[name]
        if pen_x + image.width > ATLAS_SIZE[0]:
            pen_x = 0
            pen_y += shelf_h + PADDING
            shelf_h = 0
        if pen_y + image.height > ATLAS_SIZE[1]:
            raise SystemExit("chat atlas overflow; raise ATLAS_SIZE")
        sheet.paste(image, (pen_x, pen_y))
        regions[name] = {"texture": TEXTURE_NAME, "x": pen_x, "y": pen_y, "w": image.width, "h": image.height}
        pen_x += image.width + PADDING
        shelf_h = max(shelf_h, image.height)
    return sheet, regions


def read_strings(path: Path):
    pattern = re.compile(r'^"([^"]+)"\s*:\s*"(.*)"\s*$')
    values = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        match = pattern.match(line.strip())
        if match:
            values[match.group(1)] = match.group(2).replace("\\n", "\n")
    return values


def update_hangar_manifest(atlas, lang):
    path = DATA / "hangar_source_manifest.json"
    if not path.exists():
        return
    manifest = json.loads(path.read_text(encoding="utf-8"))
    if isinstance(manifest.get("atlas"), dict):
        manifest["atlas"]["textures"] = len(atlas["textures"])
        manifest["atlas"]["regions"] = len(atlas["regions"])
    if isinstance(manifest.get("localization"), dict):
        manifest["localization"]["keys"] = len(lang["keys"])
        manifest["localization"]["languages"] = len(lang["languages"])
    path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    pack = pack_root()
    images, art_sources = collect(pack)
    sheet, regions = shelf_pack(images)
    TEXTURES.mkdir(parents=True, exist_ok=True)
    sheet.save(TEXTURES / f"{TEXTURE_NAME}.png")

    atlas = json.loads(ATLAS.read_text(encoding="utf-8"))
    atlas["regions"] = {n: r for n, r in atlas["regions"].items() if r.get("texture") != TEXTURE_NAME}
    atlas["regions"].update(regions)
    atlas["textures"][TEXTURE_NAME] = {"width": ATLAS_SIZE[0], "height": ATLAS_SIZE[1]}
    ATLAS.write_text(json.dumps(atlas, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    strings_dir = pack / "from_apk/Data/Strings"
    string_sources = [strings_dir / f"{code}.yaml" for code in LANGUAGES]
    tables = {code: read_strings(path) for code, path in zip(LANGUAGES, string_sources)}
    lang = json.loads(LANG.read_text(encoding="utf-8"))
    keys = lang["keys"]
    for key in SOURCE_STRING_KEYS:
        entry = keys.setdefault(key, {})
        for code in LANGUAGES:
            value = tables[code].get(key)
            if value is None:
                raise SystemExit(f"missing string {key} in {code}.yaml")
            entry[code] = value
    for key, values in OWN_STRINGS.items():
        keys.setdefault(key, {}).update(values)
    LANG.write_text(json.dumps(lang, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    update_hangar_manifest(atlas, lang)

    MANIFEST.write_text(json.dumps({
        "sources": [{"path": str(p.relative_to(pack)), "sha256": sha256(p)} for p in art_sources + string_sources],
        "texture": {"name": TEXTURE_NAME, "width": ATLAS_SIZE[0], "height": ATLAS_SIZE[1]},
        "regions": len(regions),
        "strings": len(SOURCE_STRING_KEYS) + len(OWN_STRINGS),
    }, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"regions: {len(regions)}  texture: {TEXTURE_NAME}.png")


if __name__ == "__main__":
    main()
