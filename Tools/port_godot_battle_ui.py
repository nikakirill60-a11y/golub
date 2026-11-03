#!/usr/bin/env python3
"""Port source-backed Godot battle HUD art into the Unity project.

Every battle HUD sprite in Godot is an AtlasTexture rect over a single sheet,
``assets/gfx/ui/battle/battleScreen.png`` (2048x2048). The ``.tres`` files still
point at a stale ``res://gfx/...`` path -- Godot resolves them by UID -- so this
script reads the rects straight out of the resources instead of hardcoding
them. A manifest records every input hash and source rect.

Region names are prefixed with ``battle_`` and stay in Godot (top-left)
coordinates, exactly like the hangar regions, because ``AtlasRegionTable`` flips
Y when it builds the sprite.

Usage:
    python3 Tools/port_godot_battle_ui.py
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
from pathlib import Path

from PIL import Image

GODOT_ROOT = Path("/home/smh01/Studio canaryProjects/Blitz Legacy")
REGION_RE = re.compile(r"^region\s*=\s*Rect2\(([^)]*)\)", re.M)

SHEET = "battleScreen"

# Unity region name -> Godot .tres path, relative to assets/gfx/ui/battle.
SHEET_REGIONS = {
    # Crosshairs: ui_player.tscn crosshair_static / crosshair_static_lit /
    # crosshair_dynamic plus the three dot tints of the reload ring.
    "battle_crosshair_static": "crosshair_static.tres",
    "battle_crosshair_static_lit": "crosshair_static_lit.tres",
    "battle_crosshair_dyn": "crosshair_dyn_unlit.tres",
    "battle_crosshair_dyn_lit": "crosshair_dyn_lit.tres",
    "battle_dot_green": "crosshair_dyn_dot_grn.tres",
    "battle_dot_yellow": "crosshair_dyn_dot_ylw.tres",
    "battle_dot_red": "crosshair_dyn_dot_red.tres",
    # The source scene deliberately puts hp_bar.tres on hp_back and
    # hp_bar_background.tres on the progress control; names describe files so
    # that this otherwise surprising swap remains visible at the call site.
    "battle_hp_bar": "hp_bar.tres",
    "battle_hp_back": "hp_bar_background.tres",
    "battle_health_icon": "parsed/Health.tres",
    # Header: team score plates, clock plate and tank-class slots.
    "battle_tanks_bg": "parsed/TanksBackground.tres",
    "battle_clock_bg": "parsed/ClockBG.tres",
    "battle_tank_icon_02": "parsed/CountingTank02.tres",
    "battle_tank_icon_03": "parsed/CountingTank03.tres",
    "battle_tank_icon_04": "parsed/CountingTank04.tres",
    "battle_tank_icon_05": "parsed/CountingTank05.tres",
    "battle_tank_icon_06": "parsed/CountingTank06.tres",
    "battle_tank_icon_07": "parsed/CountingTank07.tres",
    "battle_tank_icon_08": "parsed/CountingTank08.tres",
    "battle_tank_icon": "parsed/CountingTank.tres",
    "battle_tank_icon_013": "parsed/CountingTank013.tres",
    "battle_tank_icon_014": "parsed/CountingTank014.tres",
    "battle_tank_icon_015": "parsed/CountingTank015.tres",
    "battle_tank_icon_016": "parsed/CountingTank016.tres",
    # Killfeed, world markers, spotted lamp and battle countdown.
    "battle_killfeed_bg": "parsed/ChatKills.tres",
    "battle_enemy_marker": "parsed/EnemyMarker.tres",
    "battle_damage_ind": "parsed/DamageInd.tres",
    "battle_six_sense": "parsed/SixSense02.tres",
    "battle_six_sense_shine": "parsed/SixSense.tres",
    "battle_six_sense_glow": "parsed/SixSense02.tres",
    "battle_countdown_bg": "parsed/CountdownBeforeBattleBg.tres",
    "battle_countdown_glow": "parsed/CountdownBeforeBattleGlow.tres",
    "battle_tube": "parsed/tube.tres",
    # Shell / consumable panel.
    "battle_ammo_type": "parsed/AmmoType.tres",
    # ui_gun_burst.tscn: one round of the magazine fan, loaded and spent.
    "battle_burst_bullet": "parsed/BulletBig02.tres",
    "battle_burst_bullet_empty": "parsed/BulletBig.tres",
    # Mobile controls (skins for the layer ported in step 5.5).
    "battle_joypad_base": "parsed/Joypad.tres",
    "battle_joypad_knob": "parsed/Joypad02.tres",
    "battle_fire_button": "parsed/FireButton02.tres",
    "battle_sniper_button": "parsed/SmartZoomButton.tres",
    "battle_autoaim_button": "parsed/FollowAim03.tres",
    "battle_touch": "parsed/Touch.tres",
}

# The world-space tank plate lives outside the battle directory, but all of its
# art still comes from battleScreen.png. Keep the ten source level regions and
# all four class icons even though the current Rudniki slice seats MS-1s only.
EXTERNAL_SHEET_REGIONS = {
    "battle_otm_hp_back": "assets/gfx/ui/tank_external/hp_bar_back.tres",
    "battle_otm_hp_under": "assets/gfx/ui/battle/parsed/OTM_HealthBar03.tres",
    "battle_otm_hp_top": "assets/gfx/ui/battle/parsed/OTM_HealthBar02.tres",
    "battle_otm_class_light": "assets/gfx/ui/battle/parsed/OTM_IconLight.tres",
    "battle_otm_class_medium": "assets/gfx/ui/battle/parsed/OTM_IconMedium.tres",
    "battle_otm_class_heavy": "assets/gfx/ui/battle/parsed/OTM_IconHeavy.tres",
    "battle_otm_class_td": "assets/gfx/ui/battle/parsed/OTM_IconAtSPG.tres",
    "battle_otm_class_shadow": "assets/gfx/ui/battle/parsed/OTM_IconShadow.tres",
    "battle_otm_level_1": "assets/gfx/ui/battle/parsed/Levels.tres",
    "battle_otm_level_2": "assets/gfx/ui/battle/parsed/Levels03.tres",
    "battle_otm_level_3": "assets/gfx/ui/battle/parsed/Levels05.tres",
    "battle_otm_level_4": "assets/gfx/ui/battle/parsed/Levels07.tres",
    "battle_otm_level_5": "assets/gfx/ui/battle/parsed/Levels09.tres",
    "battle_otm_level_6": "assets/gfx/ui/battle/parsed/Levels011.tres",
    "battle_otm_level_7": "assets/gfx/ui/battle/parsed/Levels013.tres",
    "battle_otm_level_8": "assets/gfx/ui/battle/parsed/Levels015.tres",
    "battle_otm_level_9": "assets/gfx/ui/battle/parsed/Levels017.tres",
    "battle_otm_level_10": "assets/gfx/ui/battle/parsed/Levels019.tres",
    "battle_otm_name_shadow": "assets/gfx/ui/battle/parsed/OTM_NameShadow.tres",
}

# Standalone PNGs that are not part of the sheet; copied as their own texture
# with a full-texture region so callers use the same atlas lookup.
STANDALONE_TEXTURES = {
    "battle_dmg": "dmg.png",
    "battle_dmg_block": "dmg_block.png",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def source_record(path: Path, root: Path) -> dict[str, object]:
    return {
        "source": path.relative_to(root).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def read_rect(path: Path) -> list[float]:
    """Return the AtlasTexture rect of a Godot resource in source pixels."""
    match = REGION_RE.search(path.read_text(encoding="utf-8"))
    if match is None:
        raise ValueError("no region rect in " + str(path))
    parts = [float(value.strip()) for value in match.group(1).split(",")]
    if len(parts) != 4:
        raise ValueError("bad region rect in " + str(path))
    return parts


def number(value: float) -> int | float:
    """Keep whole pixels as ints so the table matches the hangar entries."""
    return int(value) if float(value).is_integer() else value


def port(godot_root: Path, unity_root: Path) -> dict[str, object]:
    battle = godot_root / "assets/gfx/ui/battle"
    textures = unity_root / "Assets/Blitz/Resources/Textures"
    data_dir = unity_root / "Assets/Blitz/Resources/Data"
    textures.mkdir(parents=True, exist_ok=True)
    data_dir.mkdir(parents=True, exist_ok=True)

    sheet_sources = {
        name: battle / relative for name, relative in SHEET_REGIONS.items()
    }
    sheet_sources.update(
        {
            name: godot_root / relative
            for name, relative in EXTERNAL_SHEET_REGIONS.items()
        }
    )

    required = [battle / "battleScreen.png"]
    required.extend(sheet_sources.values())
    required.extend(battle / relative for relative in STANDALONE_TEXTURES.values())
    required.extend(
        [
            godot_root / "assets/gfx/ui/ui_player.tscn",
            battle / "ui_player_battle.gd",
            godot_root / "assets/gfx/ui/ui_external.tscn",
            battle / "ui_tank_external.gd",
        ]
    )
    missing = [path.relative_to(godot_root).as_posix() for path in required if not path.exists()]
    if missing:
        raise FileNotFoundError("missing battle HUD sources: " + ", ".join(missing))

    table_path = data_dir / "atlas_regions.json"
    data = json.loads(table_path.read_text(encoding="utf-8"))

    # Refresh only owned regions; other battle exporters share the same prefix.
    for name in list(data["regions"]):
        if name in SHEET_REGIONS:
            del data["regions"][name]
    for name in [SHEET, *STANDALONE_TEXTURES]:
        data["textures"].pop(name, None)

    sheet_source = battle / "battleScreen.png"
    shutil.copyfile(sheet_source, textures / (SHEET + ".png"))
    with Image.open(sheet_source) as image:
        sheet_width, sheet_height = image.size
    if (sheet_width, sheet_height) != (2048, 2048):
        raise ValueError(f"unexpected battle sheet size: {sheet_width}x{sheet_height}")
    data["textures"][SHEET] = {"width": sheet_width, "height": sheet_height}

    copied = [SHEET + ".png"]
    added: dict[str, dict[str, object]] = {}
    region_manifest: dict[str, dict[str, object]] = {}

    for name, source in sheet_sources.items():
        x, y, width, height = read_rect(source)
        if x < 0 or y < 0 or width <= 0 or height <= 0:
            raise ValueError("invalid region rect in " + str(source))
        if x + width > sheet_width or y + height > sheet_height:
            raise ValueError("region leaves battle sheet in " + str(source))

        rect = [number(value) for value in (x, y, width, height)]
        added[name] = {
            "texture": SHEET,
            "x": rect[0],
            "y": rect[1],
            "w": rect[2],
            "h": rect[3],
        }
        region_manifest[name] = {
            **source_record(source, godot_root),
            "rect": rect,
        }

    standalone_manifest: dict[str, dict[str, object]] = {}
    for name, file_name in STANDALONE_TEXTURES.items():
        source = battle / file_name
        destination = textures / (name + ".png")
        shutil.copyfile(source, destination)
        copied.append(destination.name)
        with Image.open(source) as image:
            width, height = image.size
        data["textures"][name] = {"width": width, "height": height}
        added[name] = {
            "texture": name,
            "x": 0,
            "y": 0,
            "w": width,
            "h": height,
        }
        standalone_manifest[name] = {
            **source_record(source, godot_root),
            "destination": destination.name,
            "width": width,
            "height": height,
        }

    data["regions"].update(added)
    table_path.write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )

    # The atlas is shared by all slices. Keep the earlier hangar manifest's
    # aggregate counts current after this porter adds battle regions; its tests
    # deliberately compare those values with the final shared table.
    hangar_manifest_path = data_dir / "hangar_source_manifest.json"
    if hangar_manifest_path.exists():
        hangar_manifest = json.loads(hangar_manifest_path.read_text(encoding="utf-8"))
        hangar_atlas = hangar_manifest.get("atlas")
        if isinstance(hangar_atlas, dict):
            hangar_atlas["textures"] = len(data["textures"])
            hangar_atlas["regions"] = len(data["regions"])
            hangar_manifest_path.write_text(
                json.dumps(hangar_manifest, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )

    manifest = {
        "scene": source_record(godot_root / "assets/gfx/ui/ui_player.tscn", godot_root),
        "script": source_record(battle / "ui_player_battle.gd", godot_root),
        "external_scene": source_record(
            godot_root / "assets/gfx/ui/ui_external.tscn", godot_root
        ),
        "external_script": source_record(battle / "ui_tank_external.gd", godot_root),
        "sheet": {
            **source_record(sheet_source, godot_root),
            "destination": SHEET + ".png",
            "width": sheet_width,
            "height": sheet_height,
        },
        "regions": region_manifest,
        "standalone_textures": standalone_manifest,
        "atlas": {
            "regions_added": len(added),
            "regions_total": len(data["regions"]),
            "textures_total": len(data["textures"]),
        },
    }
    manifest_path = data_dir / "battle_ui_source_manifest.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    return {
        "copied": copied,
        "regions_added": len(added),
        "regions_total": len(data["regions"]),
        "textures_total": len(data["textures"]),
        "manifest": manifest_path.name,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Port the Godot battle HUD art")
    parser.add_argument("--godot-root", type=Path, default=GODOT_ROOT)
    parser.add_argument(
        "--unity-root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
    )
    args = parser.parse_args()
    result = port(args.godot_root.resolve(), args.unity_root.resolve())
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
