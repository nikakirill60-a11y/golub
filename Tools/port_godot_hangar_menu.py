#!/usr/bin/env python3
"""Port the Godot hangar burger menu: its style, its geometry and its toolbox text.

Everything here comes out of the source project. ui_theme_blitz.tres reaches the
ButtonRed style through a compressed .stylebox resource and every widget inside
screens/main/menu is laid out by a VBoxContainer, so neither the rect of the red
button sheet nor the row geometry can be read from the scene text the way the
rest of the hangar was. Tools/godot_hangar_menu_dump.gd loads the live theme and
instantiates ui_hangar.tscn headless; this generator turns that dump into one
atlas region, the localized toolbox captions and a manifest the EditMode tests
hold the Unity screen against. No artwork is redrawn and no geometry is guessed.
"""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import tempfile
from pathlib import Path

GODOT_ROOT = Path("/home/smh01/Studio canaryProjects/Blitz Legacy")
UNITY_ROOT = Path(__file__).resolve().parents[1]
RESOURCES = UNITY_ROOT / "Assets/Blitz/Resources"
DUMP_SCRIPT = "godot_hangar_menu_dump.gd"
TEMP_SCRIPT = "__blitz_hangar_menu_dump.gd"

# Source PNG behind an AtlasTexture -> the texture key Unity already ships.
# A sheet that is not here stops the port instead of being copied silently.
SHEETS = {
    "res://assets/gfx/ui/lobby_common/lobbyCommon_Out.png": "lobbyCommon_Out",
    "res://assets/gfx/ui/hangar/hangar_Out.png": "hangar_Out",
}

# Provenance for the whole slice: the scene the menu lives in, the toolbox
# script that drives it, the button script that picks its sound, the blur it
# uses as a backdrop and the theme its buttons are styled by.
SOURCES = [
    "assets/gfx/ui/hangar/ui_hangar.tscn",
    "assets/gfx/ui/hangar/ui_account_toolbox.gd",
    "assets/gfx/ui/hangar/blur.tscn",
    "assets/gfx/ui/hangar/ui_blur.gdshader",
    "assets/fonts/ui_theme_blitz.tres",
    "scripts/ui_elements/button_generic.gd",
    "scripts/account.gd",
    "scripts/_audio.gd",
]

# The account toolbox is a debug tool: ui_account_toolbox.gd spells its captions
# out in English and Godot shows those literals in every locale. The port keeps
# visible text out of C#, so each literal becomes a key carrying that exact
# source text in both languages instead of an invented translation.
TOOLBOX_KEYS = {
    "hangar:toolbox/title": "title",
    "hangar:toolbox/erase_rewards": "btn_erase_rewards",
    "hangar:toolbox/erase_battles": "btn_erase_battles",
    "hangar:toolbox/set_credits": "btn_set_credits",
    "hangar:toolbox/set_gold": "btn_set_gold",
    "hangar:toolbox/set_freexp": "btn_set_freexp",
    "hangar:toolbox/add_prem_hours": "btn_set_prem",
    "hangar:toolbox/unlock_all_tanks": "btn_unlock_tanks",
    "hangar:toolbox/full_upgrades": "btn_full_upgrades",
}

# Captions the menu itself uses. Both are authored translations already present
# in lang_dump.csv, so the port must find them rather than add them.
REQUIRED_KEYS = ("LOGOUT", "SETTINGS")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def record(path: Path) -> dict[str, object]:
    return {
        "source": path.relative_to(GODOT_ROOT).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def run_dump(out: Path) -> tuple[dict, dict]:
    """Runs the dump inside the source project and leaves it pristine again."""
    script = UNITY_ROOT / "Tools" / DUMP_SCRIPT
    if not script.is_file():
        raise SystemExit(f"missing dump script {script}")
    godot = os.environ.get("GODOT", "godot")
    out.mkdir(parents=True, exist_ok=True)
    temp = GODOT_ROOT / TEMP_SCRIPT
    shutil.copyfile(script, temp)
    try:
        result = subprocess.run(
            [
                godot,
                "--headless",
                "--path",
                str(GODOT_ROOT),
                "--script",
                f"res://{TEMP_SCRIPT}",
            ],
            capture_output=True,
            env=dict(os.environ, BLITZ_MENU_OUT=str(out)),
            text=True,
            timeout=900,
            check=False,
        )
    finally:
        temp.unlink(missing_ok=True)

    theme_path = out / "theme.json"
    menu_path = out / "menu.json"
    for path in (theme_path, menu_path):
        if not path.is_file():
            raise SystemExit(
                f"godot dump produced no {path.name} (exit {result.returncode}):\n"
                + result.stdout[-4000:]
                + result.stderr[-4000:]
            )
    return (
        json.loads(theme_path.read_text(encoding="utf-8")),
        json.loads(menu_path.read_text(encoding="utf-8")),
    )


def node(menu: dict, path: str) -> dict:
    for entry in menu["nodes"]:
        if entry["path"] == path:
            return entry
    raise SystemExit(f"the source dump has no node {path!r}")


def panel_rect(menu: dict, path: str) -> list[float]:
    """Rect of a menu descendant relative to the panel's own top-left corner.

    The dump runs in a headless window, so absolute y values belong to that
    window's height. Everything the port needs is the panel-relative layout the
    VBoxContainers produced, which is resolution independent.
    """
    origin = node(menu, "menu")["rect"]
    rect = node(menu, path)["rect"]
    return [
        round(rect[0] - origin[0], 4),
        round(rect[1] - origin[1], 4),
        round(rect[2], 4),
        round(rect[3], 4),
    ]


def button_style(theme: dict, type_name: str) -> dict:
    style = theme["types"][type_name]["styles"]["normal"]
    texture = style["texture"]
    sheet = texture["atlas"]["resource_path"]
    if sheet not in SHEETS:
        raise SystemExit(f"{type_name} is drawn from an unported sheet: {sheet}")
    region = [int(value) for value in texture["region"]]
    return {
        "stylebox": style["resource_path"],
        "texture": texture["resource_path"],
        "sheet": SHEETS[sheet],
        "region": region,
        "textureMargins": style["texture_margins"],
        "fontSize": theme["types"][type_name]["font_size"],
    }


def extend_atlas_table(destination: Path, regions: dict[str, dict]) -> dict[str, object]:
    data = json.loads(destination.read_text(encoding="utf-8"))
    added = {}
    for name, style in regions.items():
        if style["sheet"] not in data["textures"]:
            raise SystemExit(f"required Unity texture is absent: {style['sheet']}")
        x, y, width, height = style["region"]
        entry = {"texture": style["sheet"], "x": x, "y": y, "w": width, "h": height}
        data["regions"][name] = entry
        added[name] = entry
    destination.write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    return {
        "destination": destination.name,
        "added": added,
        "regions": len(data["regions"]),
        "textures": len(data["textures"]),
    }


def extend_localization(path: Path, menu: dict) -> dict[str, object]:
    data = json.loads(path.read_text(encoding="utf-8"))
    languages = data["languages"]
    missing = [key for key in REQUIRED_KEYS if key not in data["keys"]]
    if missing:
        raise SystemExit(f"lang.json is missing authored menu captions: {missing}")

    added = {}
    for key, node_name in TOOLBOX_KEYS.items():
        source_text = toolbox_text(menu, node_name)
        # Godot draws a Button caption as a single line, so the trailing newline
        # three of these literals carry has no effect on screen.
        caption = source_text.strip("\n")
        values = {language: caption for language in languages}
        data["keys"][key] = values
        added[key] = {"sourceText": source_text, "caption": caption}

    path.write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    return {
        "destination": path.name,
        "added": added,
        "required": list(REQUIRED_KEYS),
        "keys": len(data["keys"]),
        "languages": len(languages),
    }


def toolbox_text(menu: dict, node_name: str) -> str:
    for entry in menu["nodes"]:
        if entry["name"] == node_name and "account_toolbox" in entry["path"]:
            return entry.get("text", "")
    raise SystemExit(f"the source toolbox has no node {node_name!r}")


def sync_shared_manifest(path: Path, atlas: Path, language: Path) -> dict[str, object]:
    data = json.loads(path.read_text(encoding="utf-8"))
    table = json.loads(atlas.read_text(encoding="utf-8"))
    strings = json.loads(language.read_text(encoding="utf-8"))
    if "localization" in data:
        data["localization"]["keys"] = len(strings["keys"])
        data["localization"]["languages"] = len(strings["languages"])
    if "atlas" in data:
        data["atlas"]["regions"] = len(table["regions"])
        data["atlas"]["textures"] = len(table["textures"])
    path.write_text(
        json.dumps(data, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    return {
        "destination": path.name,
        "localizationKeys": len(strings["keys"]),
        "atlasRegions": len(table["regions"]),
    }


def animation(menu: dict) -> dict[str, object]:
    animations = menu["animation"]["animations"]
    show = animations["show_menu"]
    track = next(
        item for item in show["tracks"] if item["path"].endswith("menu:position")
    )
    reset = next(
        item
        for item in animations["RESET"]["tracks"]
        if item["path"].endswith("menu:position")
    )
    return {
        "name": "show_menu",
        "seconds": show["length"],
        # Animation.INTERPOLATION_LINEAR == 1; closing replays the same track
        # backwards through AnimationPlayer.play_backwards.
        "interpolation": track["interpolation"],
        "keys": [[key["time"], key["value"], key["transition"]] for key in track["keys"]],
        "reset": reset["keys"][0]["value"],
        "closesBackwards": True,
    }


def toolbox(menu: dict) -> dict[str, object]:
    rows = []
    for entry in menu["nodes"]:
        path = entry["path"]
        if not path.startswith("menu/VBoxContainer/account_toolbox/VBoxContainer/"):
            continue
        row = {
            "path": path[len("menu/VBoxContainer/account_toolbox/") :],
            "name": entry["name"],
            "class": entry["class"],
            "rect": panel_rect(menu, path),
        }
        for key in ("text", "placeholder_text", "font_size", "color", "horizontal_alignment"):
            if key in entry:
                row[key] = entry[key]
        rows.append(row)

    holder = node(menu, "menu/VBoxContainer/account_toolbox")
    background = node(menu, "menu/VBoxContainer/account_toolbox/bg")
    inner = node(menu, "menu/VBoxContainer/account_toolbox/VBoxContainer")
    return {
        "script": "assets/gfx/ui/hangar/ui_account_toolbox.gd",
        "debugBuildOnly": True,
        "rect": panel_rect(menu, "menu/VBoxContainer/account_toolbox"),
        "height": holder["custom_minimum_size"][1],
        "backgroundColor": background["color"],
        "innerRect": panel_rect(menu, "menu/VBoxContainer/account_toolbox/VBoxContainer"),
        "innerOffsetBottom": inner["offsets"][3],
        "rows": rows,
    }


def buttons(menu: dict, theme: dict) -> dict[str, object]:
    data = {}
    for name, path, variation in (
        ("logout", "menu/VBoxContainer/btn_logout", "ButtonRed"),
        ("settings", "menu/VBoxContainer/btn_settings", "Button"),
    ):
        entry = node(menu, path)
        icon = next(
            item
            for item in menu["nodes"]
            if item["path"].startswith(path + "/") and item["class"] == "TextureRect"
        )
        sheet = icon["texture"]["atlas"]["resource_path"]
        if sheet not in SHEETS:
            raise SystemExit(f"{name} icon is drawn from an unported sheet: {sheet}")
        data[name] = {
            "rect": panel_rect(menu, path),
            "text": entry["text"],
            "variation": variation,
            "fontSize": entry["font_size"],
            "style": button_style(theme, variation),
            "icon": {
                "texture": icon["texture"]["resource_path"],
                "sheet": SHEETS[sheet],
                "region": [int(value) for value in icon["texture"]["region"]],
                "scale": icon["scale"][0],
                "rect": panel_rect(menu, icon["path"]),
            },
        }
    return data


def main() -> None:
    out = Path(tempfile.mkdtemp(prefix="blitz-hangar-menu-"))
    try:
        theme, menu = run_dump(out)
    finally:
        pass

    panel = node(menu, "menu")
    strip = node(menu, "menu/ColorRect")
    blur = node(menu, "menu/blur")
    open_button = node(menu, "btn_menu")

    regions = {"button_red": button_style(theme, "ButtonRed")}
    atlas_stats = extend_atlas_table(RESOURCES / "Data/atlas_regions.json", regions)
    language_stats = extend_localization(RESOURCES / "Data/lang.json", menu)

    manifest = {
        "generator": Path(__file__).name,
        "policy": (
            "Source geometry, styles and captions only. The layout comes from a "
            "headless Godot dump of ui_hangar.tscn; nothing is measured by eye "
            "and no art is redrawn."
        ),
        "dump": {
            "script": DUMP_SCRIPT,
            "uiSize": menu["ui_size"],
            "note": (
                "Rects are relative to the panel's top-left corner, so they do "
                "not depend on the headless window size."
            ),
        },
        "sources": [record(GODOT_ROOT / relative) for relative in SOURCES],
        "openButton": {
            "path": "screens/main/btn_menu",
            "rect": [
                open_button["offsets"][0],
                open_button["offsets"][1],
                open_button["size"][0],
                open_button["size"][1],
            ],
            "toggleMode": open_button["toggle_mode"],
            "iconMaxWidth": open_button["icon_max_width"],
            # button_generic.gd: Snd.SOUND_SLIDE plays Audio.ui_slide at
            # 0.7 * snd_vol, and _audio.gd loads ui_slide from filter_open.wav.
            "sndVar": open_button["snd_var"],
            "sound": "assets/sfx/ui/filter_open.wav",
            "volume": 0.7 * open_button["snd_vol"],
            "toggleSound": bool(open_button["toggle_sound"]),
        },
        "panel": {
            "path": "screens/main/menu",
            "anchors": panel["anchors"],
            "width": panel["size"][0],
            "closedX": panel["offsets"][0],
            "openX": 0.0,
            "mouseFilter": panel["mouse_filter"],
            "separation": theme["separations"]["VBoxContainer"]["value"],
            "edgeStrip": {
                "rect": panel_rect(menu, "menu/ColorRect"),
                "anchors": strip["anchors"],
                "offsets": strip["offsets"],
                "color": strip["color"],
            },
            "blur": {
                "scene": "assets/gfx/ui/hangar/blur.tscn",
                "rect": panel_rect(menu, "menu/blur"),
                "shader": blur["shader"]["path"],
                "parameters": blur["shader"]["parameters"],
            },
        },
        "animation": animation(menu),
        "buttons": buttons(menu, theme),
        "toolbox": toolbox(menu),
        "textEdit": {
            "fontSize": theme["types"]["TextEdit"]["font_size"],
            "style": node(
                menu,
                "menu/VBoxContainer/account_toolbox/VBoxContainer/set_credits/tedit_set_credits",
            )["style_normal"],
        },
        "localization": language_stats,
        "atlas": atlas_stats,
    }

    destination = RESOURCES / "Data/hangar_menu_source_manifest.json"
    destination.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    shared = sync_shared_manifest(
        RESOURCES / "Data/hangar_source_manifest.json",
        RESOURCES / "Data/atlas_regions.json",
        RESOURCES / "Data/lang.json",
    )
    shutil.rmtree(out, ignore_errors=True)

    print(
        json.dumps(
            {
                "manifest": destination.name,
                "atlas": atlas_stats,
                "localization": {
                    "keys": language_stats["keys"],
                    "added": sorted(language_stats["added"]),
                },
                "shared": shared,
            },
            ensure_ascii=False,
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
