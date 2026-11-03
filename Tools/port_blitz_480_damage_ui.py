#!/usr/bin/env python3
"""Port the Blitz 4.8 damaged-module UI art and strings.

The client draws three panels:

* ``DamageTankPartsContainer`` - the player's own module row.  Icons come from
  ``Gfx2/UI/DamagedModules/DamagedModulesDamageTankIcon``: 21 frames, seven
  modules times three states (intact / damaged / destroyed).
* ``DamageCrewContainer`` - the player's crew row.  Icons come from
  ``DamagedModulesDamageCrew``: 15 frames, five roles times three states.
* ``EnemyDamagedModules`` - the white pictograms shown above an enemy marker,
  ``Gfx2/UI/BattleScreenHUD/ModuleDamaged``: 12 frames.

Every frame is baked into the authored virtual frame size (the descriptor's
size line plus the per-frame offsets) so the Unity layout can use the exact
4.8 rectangles, then shelf-packed into one texture.
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
MANIFEST = DATA / "damage_ui_source_manifest.json"

TEXTURE_NAME = "damaged_modules"
ATLAS_SIZE = (1024, 1024)
PADDING = 2

# Frame order inside DamagedModulesDamageTankIcon: seven modules repeated for
# the grey / yellow / red state blocks.
MODULE_ORDER = [
    "ammo_bay",
    "fuel_tank",
    "gun",
    "tracks",
    "turret_rotator",
    "engine",
    "surveying_device",
]
# Frame order inside DamagedModulesDamageCrew.
CREW_ORDER = ["loader", "radioman", "driver", "gunner", "commander"]
STATES = ["ok", "damaged", "destroyed"]
# Frame order inside BattleScreenHUD/ModuleDamaged.
ENEMY_ORDER = [
    "engine",
    "ammo_bay",
    "fuel_tank",
    "tracks",
    "gun",
    "surveying_device",
    "turret_rotator",
    "commander",
    "driver",
    "radioman",
    "gunner",
    "loader",
]

MODULE_LABEL_KEYS = [
    "EXT_ENGINE",
    "EXT_AMMO_BAY",
    "EXT_FUEL_TANK",
    "EXT_RADIO",
    "EXT_LEFT_TRACK",
    "EXT_RIGHT_TRACK",
    "EXT_GUN",
    "EXT_TURRET_ROTATOR",
    "EXT_SURVEYING_DEVICE",
    "EXT_COMMANDER",
    "EXT_DRIVER",
    "EXT_RADIOMAN",
    "EXT_GUNNER",
    "EXT_LOADER",
    "EXT_REPAIR_ALL",
    "EXT_HEAL_ALL",
]
MESSAGE_KEYS = [
    "engine_destroyed",
    "track_destroyed",
    "gun_destroyed",
    "turret_destroyed",
    "surveying_destroyed",
    "vehicle_exploded",
    "vehicle_burned",
    "vehicle_on_fire",
    "engine_damaged",
    "track_damaged",
    "gun_damaged",
    "turret_damaged",
    "surveying_damaged",
    "fuel_tank_damaged",
    "ammo_bay_damaged",
    "commander_contused",
    "gunner_contused",
    "loader_contused",
    "driver_contused",
    "cant_repair_all",
    "cant_heal_all",
    # Water: the drowning countdown and the under-water gun.
    "vehicle_drown",
    "cant_shoot_gun_under_water",
]
LANGUAGES = ("en", "ru")


def pack_root() -> Path:
    env = os.environ.get("BLITZ_PACK")
    candidates = [Path(env)] if env else []
    candidates += [
        Path.home() / "Studio canaryProjects" / "BlitzPACKSold",
        Path.home() / "projects" / "wotblitz4.8.0files",
    ]
    for path in candidates:
        if path.is_dir():
            return path
    raise SystemExit("Blitz 4.8 pack not found; set BLITZ_PACK")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_descriptor(path: Path) -> tuple[tuple[int, int], list[tuple[int, int, int, int, int, int]]]:
    """Return the authored frame size and every ``x y w h xoff yoff`` frame."""
    lines = [line.strip() for line in path.read_text().splitlines() if line.strip()]
    width, height = (int(v) for v in lines[2].split()[:2])
    count = int(lines[3])
    frames = []
    for line in lines[4 : 4 + count]:
        parts = line.split()
        frames.append(tuple(int(v) for v in parts[:6]))
    if len(frames) != count:
        raise SystemExit(f"{path.name}: expected {count} frames, found {len(frames)}")
    return (width, height), frames


def bake(atlas: Image.Image, size: tuple[int, int], frame) -> Image.Image:
    """Copy one atlas rectangle into its authored frame canvas."""
    x, y, w, h, xoff, yoff = frame
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    canvas.paste(atlas.crop((x, y, x + w, y + h)), (xoff, yoff))
    return canvas


def collect(pack: Path) -> tuple[dict[str, Image.Image], list[Path]]:
    damaged = pack / "Gfx2/UI/DamagedModules"
    hud = pack / "Gfx2/UI/BattleScreenHUD"
    sources = [
        damaged / "texture0.mali.webp",
        damaged / "DamagedModulesDamageTankIcon.txt",
        damaged / "DamagedModulesDamageCrew.txt",
        hud / "texture0.mali.webp",
        hud / "ModuleDamaged.txt",
        hud / "DamagedModulesDamageTankBg.txt",
        hud / "DamagedModulesDamageCrewBg.txt",
        hud / "DamagedModulesdmFire.txt",
        hud / "DamagedModulesExtinguisher.txt",
        hud / "WaterIndicator.txt",
    ]
    for path in sources:
        if not path.is_file():
            raise SystemExit(f"missing pack source: {path}")

    damaged_atlas = Image.open(sources[0]).convert("RGBA")
    hud_atlas = Image.open(sources[3]).convert("RGBA")
    images: dict[str, Image.Image] = {}

    size, frames = read_descriptor(sources[1])
    for index, frame in enumerate(frames):
        state = STATES[index // len(MODULE_ORDER)]
        module = MODULE_ORDER[index % len(MODULE_ORDER)]
        images[f"damage_module_{module}_{state}"] = bake(damaged_atlas, size, frame)

    size, frames = read_descriptor(sources[2])
    for index, frame in enumerate(frames):
        state = STATES[index // len(CREW_ORDER)]
        role = CREW_ORDER[index % len(CREW_ORDER)]
        images[f"damage_crew_{role}_{state}"] = bake(damaged_atlas, size, frame)

    size, frames = read_descriptor(sources[4])
    for index, frame in enumerate(frames):
        images[f"damage_enemy_{ENEMY_ORDER[index]}"] = bake(hud_atlas, size, frame)

    size, frames = read_descriptor(sources[5])
    images["damage_panel_tank_bg"] = bake(hud_atlas, size, frames[0])
    size, frames = read_descriptor(sources[6])
    images["damage_panel_crew_bg"] = bake(hud_atlas, size, frames[0])
    # HUDLayer.yaml drives FireIcon with frame 1, the small burning marker.
    size, frames = read_descriptor(sources[7])
    images["damage_fire_icon"] = bake(hud_atlas, size, frames[1])
    size, frames = read_descriptor(sources[8])
    images["damage_extinguisher_icon"] = bake(hud_atlas, size, frames[0])
    # HUDLayer.yaml DestroyTimer / VehicleDestroyTimer.yaml: the 130x130
    # water indicator, frame 0 the sinking tank, frames 1 and 2 its rings.
    size, frames = read_descriptor(sources[9])
    for index, frame in enumerate(frames):
        images[f"water_indicator_{index}"] = bake(hud_atlas, size, frame)
    return images, sources


def shelf_pack(images: dict[str, Image.Image]) -> tuple[Image.Image, dict[str, dict[str, int]]]:
    sheet = Image.new("RGBA", ATLAS_SIZE, (0, 0, 0, 0))
    regions: dict[str, dict[str, int]] = {}
    pen_x = pen_y = shelf_h = 0
    for name in sorted(images, key=lambda n: (-images[n].height, n)):
        image = images[name]
        if pen_x + image.width > ATLAS_SIZE[0]:
            pen_x = 0
            pen_y += shelf_h + PADDING
            shelf_h = 0
        if pen_y + image.height > ATLAS_SIZE[1]:
            raise SystemExit("damaged module atlas overflow; raise ATLAS_SIZE")
        sheet.paste(image, (pen_x, pen_y))
        regions[name] = {
            "texture": TEXTURE_NAME,
            "x": pen_x,
            "y": pen_y,
            "w": image.width,
            "h": image.height,
        }
        pen_x += image.width + PADDING
        shelf_h = max(shelf_h, image.height)
    return sheet, regions


def read_strings(path: Path) -> dict[str, str]:
    pattern = re.compile(r'^"([^"]+)"\s*:\s*"(.*)"\s*$')
    values: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        match = pattern.match(line.strip())
        if match:
            values[match.group(1)] = match.group(2).replace("\\n", "\n")
    return values


def port_strings(pack: Path) -> tuple[int, list[Path]]:
    strings_dir = pack / "from_apk/Data/Strings"
    sources = [strings_dir / f"{code}.yaml" for code in LANGUAGES]
    tables = {code: read_strings(path) for code, path in zip(LANGUAGES, sources)}
    lang = json.loads(LANG.read_text(encoding="utf-8"))
    keys = lang["keys"]
    added = 0
    for key in MODULE_LABEL_KEYS + MESSAGE_KEYS:
        entry = keys.setdefault(key, {})
        for code in LANGUAGES:
            value = tables[code].get(key)
            if value is None:
                raise SystemExit(f"missing string {key} in {code}.yaml")
            entry[code] = value
        added += 1
    LANG.write_text(json.dumps(lang, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return added, sources


def main() -> None:
    pack = pack_root()
    images, art_sources = collect(pack)
    sheet, regions = shelf_pack(images)
    TEXTURES.mkdir(parents=True, exist_ok=True)
    sheet.save(TEXTURES / f"{TEXTURE_NAME}.png")

    atlas = json.loads(ATLAS.read_text(encoding="utf-8"))
    atlas["regions"] = {
        name: rect
        for name, rect in atlas["regions"].items()
        if rect.get("texture") != TEXTURE_NAME
    }
    atlas["regions"].update(regions)
    atlas["textures"][TEXTURE_NAME] = {"width": ATLAS_SIZE[0], "height": ATLAS_SIZE[1]}
    ATLAS.write_text(json.dumps(atlas, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    # The atlas table is shared; keep the hangar manifest aggregate counts
    # current because its tests compare them with the final table.
    hangar_manifest_path = DATA / "hangar_source_manifest.json"
    if hangar_manifest_path.exists():
        hangar_manifest = json.loads(hangar_manifest_path.read_text(encoding="utf-8"))
        hangar_atlas = hangar_manifest.get("atlas")
        if isinstance(hangar_atlas, dict):
            hangar_atlas["textures"] = len(atlas["textures"])
            hangar_atlas["regions"] = len(atlas["regions"])
            hangar_manifest_path.write_text(
                json.dumps(hangar_manifest, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )

    strings_added, string_sources = port_strings(pack)

    # Same contract for the shared localization table.
    if hangar_manifest_path.exists():
        hangar_manifest = json.loads(hangar_manifest_path.read_text(encoding="utf-8"))
        localization = hangar_manifest.get("localization")
        if isinstance(localization, dict):
            lang = json.loads(LANG.read_text(encoding="utf-8"))
            localization["keys"] = len(lang["keys"])
            localization["languages"] = len(lang["languages"])
            hangar_manifest_path.write_text(
                json.dumps(hangar_manifest, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )

    MANIFEST.write_text(
        json.dumps(
            {
                "sources": [
                    {"path": str(path.relative_to(pack)), "sha256": sha256(path)}
                    for path in art_sources + string_sources
                ],
                "texture": {"name": TEXTURE_NAME, "width": ATLAS_SIZE[0], "height": ATLAS_SIZE[1]},
                "regions": len(regions),
                "strings": strings_added,
            },
            ensure_ascii=False,
            indent=2,
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )
    print(f"regions: {len(regions)}  strings: {strings_added}  texture: {TEXTURE_NAME}.png")


if __name__ == "__main__":
    main()
