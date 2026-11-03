#!/usr/bin/env python3
"""Port the shop and container-opening art and strings.

Sources:

* Godot ``assets/gfx/ui/shop`` - the card art of ``screens/shop`` in
  ``maps/hangar_low.tscn``: ``Slot_04`` (card backdrop), ``Slot_03`` (card
  frame), ``TextExchangeBack`` (price plate), ``ImagePremium<days>`` and the
  container pictures of ``chests.webp``. The regions are read from the
  ``.tres`` AtlasTexture files, so a moved region in Godot moves here too.
* Blitz 4.8.0 ``Gfx/Particles/chest`` - the sprites the opened container
  glows with in ``3d/Maps/chest/Box_anim.sc2`` (``glow_anim_low``,
  ``box_light``, ``spot_light``, ``sparks1``, ``shadow1``, ``camm``).
* Blitz 4.8.0 ``Gfx/UI/Quests/DailyChests`` - separate opening-screen
  chest sprites; shop cards still match the Godot artwork.
* Godot ``maps/components/chest/background2.dx11_Out.png`` (the 4.8.0
  ``3d/Maps/chest/background2`` backdrop of the container scene).

The Godot container pictures follow the 4.8.0 daily container naming:
``daily_chest_small`` / ``daily_chest_medium`` / ``daily_chest_big`` are the
small, big and huge containers.
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
MANIFEST = DATA / "shop_ui_source_manifest.json"

CARDS_TEXTURE = "shop_cards"
CARDS_SIZE = (2048, 2048)
FX_TEXTURE = "chest_fx"
FX_SIZE = (2048, 1024)
BACKGROUND_TEXTURE = "chest_background"
# background2 is a square texture; the lit wall and floor of the container
# scene sit in its upper half, which is what the camera of Box_anim.sc2 sees.
BACKGROUND_CROP = (0, 64, 2048, 1088)
PADDING = 2

# region name -> Godot .tres (relative to assets/gfx/ui/shop)
GODOT_CARD_ART = {
    "shop_slot_back": "Slot_04.tres",
    "shop_slot_frame": "Slot_03.tres",
    "shop_price_back": "TextExchangeBack.tres",
    "shop_chest_small": "daily_chest_small.tres",
    "shop_chest_big": "daily_chest_medium.tres",
    "shop_chest_huge": "daily_chest_big.tres",
}
PREMIUM_DAYS = (1, 3, 7, 14, 30, 90, 180, 360)
for _days in PREMIUM_DAYS:
    GODOT_CARD_ART[f"shop_premium_{_days}"] = f"ImagePremium{_days}.tres"

# Regions that already live in ported Godot sheets.
EXISTING_REGIONS = {
    # lobby_common/CurrencyIconsBig03.tres - the Godot chest free XP icon.
    "shop_reward_freexp": {"texture": "lobbyCommon_Out", "x": 1018, "y": 1511, "w": 52, "h": 49},
    # common/ButtonTiny.tres - the Godot chest reward slot backdrop.
    "shop_reward_slot": {"texture": "commonUiElements_Out", "x": 1, "y": 283, "w": 138, "h": 138},
}

# 4.8.0 chest particle frames, trimmed to their frame rect, with a fixed
# layout (the big light rays do not shelf-pack into 2048x1024).
FX_FRAMES = {
    "chest_fx_spot_light": ("spot_light.txt", (0, 0)),
    "chest_fx_box_light": ("box_light.txt", (716, 0)),
    "chest_fx_glow": ("glow_anim_low.txt", (716, 370)),
    "chest_fx_shadow": ("shadow1.txt", (974, 370)),
    "chest_fx_camm": ("camm.txt", (1138, 370)),
    "chest_fx_sparks": ("sparks1.txt", (1241, 370)),
}

OWN_STRINGS = {
    "shop:premium/desc": {"en": "More XP and credits in every battle", "ru": "Больше опыта и кредитов в каждом бою"},
    "shop:guaranteed_left": {"en": "Guaranteed tank: %d left", "ru": "Гарантированный танк: осталось %d"},
    "shop:container/small": {"en": "Small container", "ru": "Малый контейнер"},
    "shop:container/big": {"en": "Big container", "ru": "Большой контейнер"},
    "shop:container/huge": {"en": "Huge container", "ru": "Огромный контейнер"},
    "shop:container/small/desc": {
        "en": "Credits, free XP, a chance for gold and a Tier II-III tank",
        "ru": "Кредиты, свободный опыт, шанс на золото и танк II-III уровня",
    },
    "shop:container/big/desc": {
        "en": "More gold and credits, a chance for a Tier III-VIII premium tank",
        "ru": "Больше золота и кредитов, шанс на премиум танк III-VIII уровня",
    },
    "shop:container/huge/desc": {
        "en": "Gold in every container and the best chance for a Tier VIII premium tank",
        "ru": "Золото в каждом контейнере и лучший шанс на премиум танк VIII уровня",
    },
    "shop:reward/tank": {"en": "Tank", "ru": "Танк"},
    "shop:reward/gold": {"en": "Gold", "ru": "Золото"},
    "shop:reward/credits": {"en": "Credits", "ru": "Кредиты"},
    "shop:reward/freexp": {"en": "Free XP", "ru": "Свободный опыт"},
    "shop:received": {"en": "You received:", "ru": "Вы получили:"},
    "shop:compensation": {"en": "Already in the garage: +%s credits", "ru": "Уже в ангаре: +%s кредитов"},
    "shop:rotation": {"en": "New tanks in: %s", "ru": "Новые танки через: %s"},
    "shop:rotation/hint": {
        "en": "The tank list changes every Monday at 00:00 UTC",
        "ru": "Список танков меняется каждый понедельник в 00:00 UTC",
    },
    "shop:time/dh": {"en": "%d d %d h", "ru": "%d д. %d ч."},
    "shop:time/hm": {"en": "%d h %d min", "ru": "%d ч. %d мин."},
    "shop:offline/title": {"en": "No connection", "ru": "Нет соединения"},
    "shop:offline/text": {
        "en": "The shop works online only. Check your internet connection and try again.",
        "ru": "Магазин работает только онлайн. Проверьте подключение к интернету и повторите попытку.",
    },
    "shop:connecting": {"en": "Connecting...", "ru": "Подключение..."},
    "shop:retry": {"en": "RETRY", "ru": "ПОВТОРИТЬ"},
    "shop:owned": {"en": "IN GARAGE", "ru": "В АНГАРЕ"},
    "shop:owned_error": {
        "en": "This vehicle is already in your garage.",
        "ru": "Эта машина уже есть в вашем ангаре.",
    },
    "shop:premium_tank": {"en": "Premium vehicle", "ru": "Премиум техника"},
    "shop:collector_tank": {"en": "Collector vehicle", "ru": "Коллекционная техника"},
}
LANGUAGES = ("en", "ru")


def godot_root() -> Path:
    env = os.environ.get("BLITZ_GODOT")
    candidates = [Path(env)] if env else []
    candidates += [Path.home() / "projects" / "blitz-legacy-godot"]
    for path in candidates:
        if path.is_dir():
            return path
    raise SystemExit("Godot project not found; set BLITZ_GODOT")


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


def read_tres(path: Path):
    text = path.read_text(encoding="utf-8")
    source = re.search(r'path="res://([^"]+)"', text)
    region = re.search(r"region = Rect2\(([^)]*)\)", text)
    if not source or not region:
        raise SystemExit(f"{path}: not an AtlasTexture with a region")
    x, y, w, h = (int(round(float(v))) for v in region.group(1).split(","))
    return source.group(1), (x, y, w, h)


def read_descriptor(path: Path):
    lines = [line.strip() for line in path.read_text().splitlines() if line.strip()]
    count = int(lines[3])
    frames = [tuple(int(v) for v in line.split()[:6]) for line in lines[4 : 4 + count]]
    if len(frames) != count:
        raise SystemExit(f"{path.name}: expected {count} frames, found {len(frames)}")
    return frames


def shelf_pack(images, size, texture):
    sheet = Image.new("RGBA", size, (0, 0, 0, 0))
    regions = {}
    pen_x = pen_y = shelf_h = 0
    for name in sorted(images, key=lambda n: (-images[n].height, n)):
        image = images[name]
        if pen_x + image.width > size[0]:
            pen_x = 0
            pen_y += shelf_h + PADDING
            shelf_h = 0
        if pen_y + image.height > size[1]:
            raise SystemExit(f"{texture} overflow; raise its size")
        sheet.paste(image, (pen_x, pen_y))
        regions[name] = {"texture": texture, "x": pen_x, "y": pen_y, "w": image.width, "h": image.height}
        pen_x += image.width + PADDING
        shelf_h = max(shelf_h, image.height)
    return sheet, regions


def collect_cards(godot: Path):
    shop = godot / "assets/gfx/ui/shop"
    sources = []
    images = {}
    sheets = {}
    for name, tres in GODOT_CARD_ART.items():
        path = shop / tres
        sheet_path, (x, y, w, h) = read_tres(path)
        sources.append(path)
        if sheet_path not in sheets:
            sheet_file = godot / sheet_path
            sources.append(sheet_file)
            sheets[sheet_path] = Image.open(sheet_file).convert("RGBA")
        images[name] = sheets[sheet_path].crop((x, y, x + w, y + h))
    return images, sources


def collect_opening_chests(pack: Path):
    """Use original 4.8.0 daily-chest sprites, not Godot opening artwork."""
    folder = pack / "Gfx/UI/Quests/DailyChests"
    texture = folder / "texture0.mali.webp"
    sheet = Image.open(texture).convert("RGBA")
    images = {}
    sources = [texture]
    for shop_id, client_id in (("small", "small"), ("big", "medium"), ("huge", "big")):
        descriptor = folder / f"daily_chest_{client_id}.txt"
        sources.append(descriptor)
        x, y, w, h, _xoff, _yoff = read_descriptor(descriptor)[0]
        images[f"chest_open_{shop_id}"] = sheet.crop((x, y, x + w, y + h))
    return images, sources


def collect_fx(pack: Path):
    folder = pack / "Gfx/Particles/chest"
    texture = folder / "texture0.mali.webp"
    atlas = Image.open(texture).convert("RGBA")
    sheet = Image.new("RGBA", FX_SIZE, (0, 0, 0, 0))
    regions = {}
    sources = [texture]
    for name, (descriptor, (px, py)) in FX_FRAMES.items():
        path = folder / descriptor
        sources.append(path)
        x, y, w, h, _xoff, _yoff = read_descriptor(path)[0]
        if px + w > FX_SIZE[0] or py + h > FX_SIZE[1]:
            raise SystemExit(f"{name} does not fit the fx layout")
        frame = atlas.crop((x, y, x + w, y + h))
        if name == "chest_fx_glow":
            # The compressed client glow has an alpha floor of 3/255. Remove
            # that floor to avoid a rectangular halo in Unity's linear UI.
            frame.putalpha(frame.getchannel("A").point(lambda a: max(0, round((a - 3) * 255 / 252))))
        sheet.paste(frame, (px, py))
        regions[name] = {"texture": FX_TEXTURE, "x": px, "y": py, "w": w, "h": h}
    names = sorted(regions)
    for i, a in enumerate(names):
        for b in names[i + 1 :]:
            ra, rb = regions[a], regions[b]
            if (ra["x"] < rb["x"] + rb["w"] and rb["x"] < ra["x"] + ra["w"] and
                    ra["y"] < rb["y"] + rb["h"] and rb["y"] < ra["y"] + ra["h"]):
                raise SystemExit(f"fx layout overlap: {a} / {b}")
    return sheet, regions, sources


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
    godot = godot_root()
    pack = pack_root()
    TEXTURES.mkdir(parents=True, exist_ok=True)

    card_images, card_sources = collect_cards(godot)
    opening_images, opening_sources = collect_opening_chests(pack)
    card_images.update(opening_images)
    cards, card_regions = shelf_pack(card_images, CARDS_SIZE, CARDS_TEXTURE)
    cards.save(TEXTURES / f"{CARDS_TEXTURE}.png")

    fx, fx_regions, fx_sources = collect_fx(pack)
    fx.save(TEXTURES / f"{FX_TEXTURE}.png")

    background_source = godot / "maps/components/chest/background2.dx11_Out.png"
    background = Image.open(background_source).convert("RGB").crop(BACKGROUND_CROP)
    background.save(TEXTURES / f"{BACKGROUND_TEXTURE}.png")

    atlas = json.loads(ATLAS.read_text(encoding="utf-8"))
    owned = {CARDS_TEXTURE, FX_TEXTURE, BACKGROUND_TEXTURE}
    atlas["regions"] = {
        n: r for n, r in atlas["regions"].items()
        if r.get("texture") not in owned and n not in EXISTING_REGIONS
    }
    atlas["regions"].update(card_regions)
    atlas["regions"].update(fx_regions)
    atlas["regions"].update(EXISTING_REGIONS)
    atlas["textures"][CARDS_TEXTURE] = {"width": CARDS_SIZE[0], "height": CARDS_SIZE[1]}
    atlas["textures"][FX_TEXTURE] = {"width": FX_SIZE[0], "height": FX_SIZE[1]}
    atlas["regions"][BACKGROUND_TEXTURE] = {
        "texture": BACKGROUND_TEXTURE, "x": 0, "y": 0, "w": background.width, "h": background.height}
    atlas["textures"][BACKGROUND_TEXTURE] = {"width": background.width, "height": background.height}
    for region in EXISTING_REGIONS.values():
        if region["texture"] not in atlas["textures"]:
            raise SystemExit(f"missing base texture {region['texture']}")
    ATLAS.write_text(json.dumps(atlas, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    lang = json.loads(LANG.read_text(encoding="utf-8"))
    for key, values in OWN_STRINGS.items():
        missing = [code for code in LANGUAGES if not values.get(code)]
        if missing:
            raise SystemExit(f"string {key} lacks {missing}")
        lang["keys"].setdefault(key, {}).update(values)
    LANG.write_text(json.dumps(lang, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    update_hangar_manifest(atlas, lang)

    sources = []
    for path in card_sources + [background_source]:
        sources.append({"path": "godot/" + str(path.relative_to(godot)), "sha256": sha256(path)})
    opening_sources.append(pack / "from_apk/Data/UI/Screens/Lobby/Storage/ChestUsage.yaml")
    for path in fx_sources + opening_sources:
        sources.append({"path": "blitz480/" + str(path.relative_to(pack)), "sha256": sha256(path)})
    MANIFEST.write_text(json.dumps({
        "sources": sources,
        "textures": {
            CARDS_TEXTURE: {"width": CARDS_SIZE[0], "height": CARDS_SIZE[1], "regions": sorted(card_regions)},
            FX_TEXTURE: {"width": FX_SIZE[0], "height": FX_SIZE[1], "regions": sorted(fx_regions)},
            BACKGROUND_TEXTURE: {"crop": list(BACKGROUND_CROP)},
        },
        "existing_regions": sorted(EXISTING_REGIONS),
        "fx_transforms": {"chest_fx_glow": {"alpha_floor_removed": 3}},
        "strings": sorted(OWN_STRINGS),
    }, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"cards: {len(card_regions)}  fx: {len(fx_regions)}  strings: {len(OWN_STRINGS)}")


if __name__ == "__main__":
    main()
