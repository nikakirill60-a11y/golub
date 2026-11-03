#!/usr/bin/env python3
"""Port the Godot battle minimap widget into the Unity project.

The minimap of ui_player.tscn is `minimap_handle`, a top-left Control that
anims/scoreboard_slide moves in and out, holding the `dmg_log` damage icons
and the `minimap` TextureRect. ui_player_battle.gd fills that TextureRect with
icons cut from battleScreen.png: one per tank, the averaged spawn of each team,
the player's arrow, the camera's view cone and the player's own guided
missiles.

This script reads every number it needs from those sources instead of
hardcoding it:

- the AtlasTexture rects of the icon `.tres` files become `battle_minimap_*`
  regions in Resources/Data/atlas_regions.json;
- rocket_marker.png is copied next to battleScreen.png;
- the geometry of minimap_handle, dmg_log, dico, bico and minimap and the
  behaviour constants of ui_player_battle.gd land in
  Resources/Data/battle_minimap_source_manifest.json, with the hash of every
  input so BattleMinimapTests notice when the Godot project moves on.

The numbered spawn sprites MinimapRespawn02..08 are ported as well: Godot
only ever draws the "1" pair, while the 4.8.0 client numbers every spawn area
of a team, which the client maps of the port carry as `respawn` groups.

Usage:
    python3 Tools/port_battle_minimap.py [--godot-root ../blitz-legacy-godot]
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
from port_godot_battle_ui import number, read_rect, source_record  # noqa: E402

UNITY_ROOT = Path(__file__).resolve().parents[1]
GODOT_ROOT = Path(os.environ.get("BLITZ_GODOT_ROOT", UNITY_ROOT.parent / "blitz-legacy-godot"))

SHEET = "battleScreen"

# Unity region name -> Godot .tres path, relative to assets/gfx/ui/battle.
REGIONS = {
    "battle_minimap_player": "parsed/MinimapPlayer.tres",
    "battle_minimap_look_direction": "parsed/MinimapLookDirection.tres",
    "battle_minimap_tank_light": "parsed/MinimapTank02.tres",
    "battle_minimap_tank_medium": "parsed/MinimapTank03.tres",
    "battle_minimap_tank_heavy": "parsed/MinimapTank04.tres",
    "battle_minimap_tank_destroyer": "parsed/MinimapTank.tres",
    # Green 1..4 for the player's team, red 1..4 for the other one.
    "battle_minimap_respawn_ally_1": "parsed/MinimapRespawn.tres",
    "battle_minimap_respawn_ally_2": "parsed/MinimapRespawn02.tres",
    "battle_minimap_respawn_ally_3": "parsed/MinimapRespawn03.tres",
    "battle_minimap_respawn_ally_4": "parsed/MinimapRespawn04.tres",
    "battle_minimap_respawn_enemy_1": "parsed/MinimapRespawn05.tres",
    "battle_minimap_respawn_enemy_2": "parsed/MinimapRespawn06.tres",
    "battle_minimap_respawn_enemy_3": "parsed/MinimapRespawn07.tres",
    "battle_minimap_respawn_enemy_4": "parsed/MinimapRespawn08.tres",
}

# Whole-file textures, Unity texture name -> path in assets/gfx/ui/battle.
STANDALONE = {
    "battle_rocket_marker": "rocket_marker.png",
}

# The script's own references, so a renamed icon fails the port.
SCRIPT_PATHS = {
    "minimap_player": "parsed/MinimapPlayer.tres",
    "minimap_player_lookdir": "parsed/MinimapLookDirection.tres",
    "minimap_spawner_ally": "parsed/MinimapRespawn.tres",
    "minimap_spawner_enemy": "parsed/MinimapRespawn05.tres",
}

NODE_RE = re.compile(r'^\[node name="([^"]+)" type="([^"]+)"(?: parent="([^"]*)")?')


def scene_nodes(path: Path) -> dict[str, dict[str, str]]:
    """`parent/name` -> raw properties of every node block of a .tscn."""
    nodes: dict[str, dict[str, str]] = {}
    current: dict[str, str] | None = None
    for line in path.read_text(encoding="utf-8").splitlines():
        match = NODE_RE.match(line)
        if match:
            name, kind, parent = match.groups()
            key = name if parent in (None, ".") else f"{parent}/{name}"
            current = {"type": kind}
            nodes[key] = current
            continue
        if line.startswith("["):
            current = None
            continue
        if current is not None and " = " in line:
            prop, value = line.split(" = ", 1)
            current[prop.strip()] = value.strip()
    return nodes


def floats(text: str) -> list[float]:
    """The numbers of a literal such as `Vector2(0.66, 0.66)` or `0.1, 2`."""
    if "(" in text:
        text = text[text.index("(") + 1:text.rindex(")")]
    return [float(value) for value in re.findall(r"-?\d+(?:\.\d+)?(?:e-?\d+)?", text)]


def control(node: dict[str, str]) -> dict[str, object]:
    """The layout fields of a Control block, with Godot's defaults filled in."""
    result: dict[str, object] = {
        "anchors": [float(node.get(key, 0.0)) for key in
                    ("anchor_left", "anchor_top", "anchor_right", "anchor_bottom")],
        "offsets": [float(node.get(key, 0.0)) for key in
                    ("offset_left", "offset_top", "offset_right", "offset_bottom")],
        "scale": floats(node["scale"]) if "scale" in node else [1.0, 1.0],
    }
    return result


def script_constant(text: str, pattern: str, label: str) -> list[float]:
    match = re.search(pattern, text)
    if match is None:
        raise SystemExit(f"ui_player_battle.gd no longer contains {label}")
    return [float(value) for value in match.groups()]


def port(godot_root: Path, unity_root: Path) -> dict[str, object]:
    battle = godot_root / "assets/gfx/ui/battle"
    scene_path = godot_root / "assets/gfx/ui/ui_player.tscn"
    script_path = battle / "ui_player_battle.gd"
    global_path = godot_root / "scripts/_global.gd"
    textures = unity_root / "Assets/Blitz/Resources/Textures"
    data_dir = unity_root / "Assets/Blitz/Resources/Data"

    script = script_path.read_text(encoding="utf-8")
    for variable, relative in SCRIPT_PATHS.items():
        if f'static var {variable}: String = "res://assets/gfx/ui/battle/{relative}"' not in script:
            raise SystemExit(f"ui_player_battle.gd: {variable} no longer loads {relative}")
    for region, relative in REGIONS.items():
        if region.startswith("battle_minimap_tank_") and f'"res://assets/gfx/ui/battle/{relative}"' not in script:
            raise SystemExit(f"ui_player_battle.gd no longer maps a class to {relative}")
    if 'MISSILE_MARKER_TEX: String = "res://assets/gfx/ui/battle/rocket_marker.png"' not in script:
        raise SystemExit("ui_player_battle.gd no longer marks missiles with rocket_marker.png")

    nodes = scene_nodes(scene_path)
    handle = nodes["minimap_handle"]
    minimap = nodes["minimap_handle/minimap"]
    if not minimap.get("texture", "").startswith("ExtResource"):
        raise SystemExit("minimap_handle/minimap lost its authored texture")

    table_path = data_dir / "atlas_regions.json"
    table = json.loads(table_path.read_text(encoding="utf-8"))
    sheet = table["textures"][SHEET]
    regions: dict[str, object] = {}
    for name, relative in REGIONS.items():
        source = battle / relative
        x, y, w, h = read_rect(source)
        if x + w > sheet["width"] or y + h > sheet["height"]:
            raise SystemExit(f"{relative} leaves the battle sheet")
        rect = [number(value) for value in (x, y, w, h)]
        table["regions"][name] = {"texture": SHEET, "x": rect[0], "y": rect[1], "w": rect[2], "h": rect[3]}
        regions[name] = {**source_record(source, godot_root), "rect": rect}
    for name, relative in STANDALONE.items():
        source = battle / relative
        shutil.copyfile(source, textures / f"{name}.png")
        with Image.open(source) as image:
            width, height = image.size
        table["textures"][name] = {"width": width, "height": height}
        table["regions"][name] = {"texture": name, "x": 0, "y": 0, "w": width, "h": height}
        regions[name] = {**source_record(source, godot_root), "rect": [0, 0, width, height]}
    table_path.write_text(json.dumps(table, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    # HangarSliceTests compare these aggregate counts with the shared table.
    hangar_path = data_dir / "hangar_source_manifest.json"
    if hangar_path.exists():
        hangar = json.loads(hangar_path.read_text(encoding="utf-8"))
        if isinstance(hangar.get("atlas"), dict):
            hangar["atlas"]["textures"] = len(table["textures"])
            hangar["atlas"]["regions"] = len(table["regions"])
            hangar_path.write_text(json.dumps(hangar, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    colours = {}
    global_text = global_path.read_text(encoding="utf-8")
    for name in ("COLOR_ALLY", "COLOR_ENEMY"):
        match = re.search(rf"const {name}: Color = Color\(([^)]*)\)", global_text)
        if match is None:
            raise SystemExit(f"_global.gd no longer defines {name}")
        colours[name] = floats(match.group(1))

    behaviour = {
        "missileScale": script_constant(
            script, r"mini\.scale = Vector2\(([\d.]+), ([\d.]+)\)", "the missile marker scale")[0],
        "destroyerScale": script_constant(
            script, r"tonk\.size \*= ([\d.]+)", "the tank destroyer icon scale")[0],
        "ghostLighten": script_constant(
            script, r"\.lightened\(([\d.]+)\)", "the unspotted marker tint")[0],
        "ghostDim": script_constant(
            script, r"tonk_unspotted\.modulate \*= ([\d.]+)", "the unspotted marker dimming")[0],
        "deathDim": script_constant(
            script, r"tonk\.modulate \*= ([\d.]+)", "the destroyed marker dimming")[0],
        "deathFadeSeconds": script_constant(
            script, r", 1\.0, 0\.0, ([\d.]+)\)\.finished\.connect\(tonk\.hide\)", "the destroyed marker fade")[0],
        "playerRotationOffset": -0.5 * 3.141592653589793 if "Vector2.ZERO, - PI * 0.5)" in script else None,
        "updateSeconds": script_constant(
            script, r'delay_curtime_elapsed_set\("ui_player_update_minimap", ([\d.]+)\)', "the update cadence")[0],
    }
    if behaviour["playerRotationOffset"] is None:
        raise SystemExit("ui_player_battle.gd no longer turns the player arrow by -PI/2")

    manifest = {
        "scene": source_record(scene_path, godot_root),
        "script": source_record(script_path, godot_root),
        "global": source_record(global_path, godot_root),
        "handle": control(handle),
        "dmgLog": control(nodes["minimap_handle/dmg_log"]),
        "dmgIcon": control(nodes["minimap_handle/dmg_log/dico"]),
        "blockIcon": control(nodes["minimap_handle/dmg_log/bico"]),
        "minimap": control(minimap),
        "colours": {"ally": colours["COLOR_ALLY"], "enemy": colours["COLOR_ENEMY"]},
        "behaviour": behaviour,
        "regions": regions,
    }
    manifest_path = data_dir / "battle_minimap_source_manifest.json"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return {"regions": len(regions), "manifest": manifest_path.name}


def main() -> None:
    parser = argparse.ArgumentParser(description="Port the Godot battle minimap")
    parser.add_argument("--godot-root", type=Path, default=GODOT_ROOT)
    parser.add_argument("--unity-root", type=Path, default=UNITY_ROOT)
    args = parser.parse_args()
    print(json.dumps(port(args.godot_root.resolve(), args.unity_root.resolve()), indent=2))


if __name__ == "__main__":
    main()
