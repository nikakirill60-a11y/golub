#!/usr/bin/env python3
"""Port the settings screen.

assets/gfx/ui/ui_settings.tscn authors a header, a five button tab strip and
three hidden row templates that ui_settings.gd duplicates into two columns per
tab. None of that can be read off the scene text alone: the rows live in box
containers, the row background is an atlas cut of an atlas cut, and the check
box draws Godot's built-in glyph because the theme styles every one of its
states empty. Tools/godot_settings_dump.gd instantiates the scene headless;
this generator turns that dump into the missing atlas regions, the two check
box glyphs, the captions the screen shows and a manifest the EditMode tests
hold the Unity screen against. Nothing is redrawn or measured by eye.

The row list itself is transcribed from ui_settings.gd's _ready(), because the
source builds it in code rather than in the scene.
"""

from __future__ import annotations

import csv
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
DUMP_SCRIPT = "godot_settings_dump.gd"
TEMP_SCRIPT = "__blitz_settings_dump.gd"

# Source PNG behind an AtlasTexture -> the texture key Unity already ships.
SHEETS = {
    "res://assets/gfx/ui/lobby_common/lobbyCommon_Out.png": "lobbyCommon_Out",
    "res://assets/gfx/ui/common/commonUiElements_Out.png": "commonUiElements_Out",
    "res://assets/gfx/ui/battle_result/battleResult_atlas.png": "flow_battle_result",
    "res://assets/gfx/ui/lobby_common/thinRedLine.png": "thinRedLine",
}

# Names for cuts the atlas table does not have yet. Anything that resolves to
# a rect the table already carries keeps the name it has there instead.
NEW_REGION_NAMES = {
    "row_background": "settings_row_bg",
    "header": "settings_header",
    "back_icon": "settings_back_icon",
}

# The two glyphs the check box rows draw, saved out of Godot's default theme.
CHECKBOX_TEXTURES = {
    "checked": ("settings_checkbox_checked", "ui_checkbox_checked"),
    "unchecked": ("settings_checkbox_unchecked", "ui_checkbox_unchecked"),
}

SOURCES = [
    "assets/gfx/ui/ui_settings.tscn",
    "assets/gfx/ui/ui_settings.gd",
    "assets/data/save_settings.gd",
    "assets/fonts/ui_theme_blitz.tres",
    "scripts/ui_elements/tab_container_custom.gd",
    "scripts/ui_elements/button_generic.gd",
]

# Captions the screen shows. Godot translates Button.text and Label.text
# through Control.auto_translate, including the option captions ui_settings.gd
# assigns at runtime, so every one of them is a translation key.
REQUIRED_KEYS = ("SETTINGS",)
TAB_KEYS = ("MAIN", "MATCHMAKER", "GAMEPLAY")
LANGUAGE_CSVS = ("lang_dump.csv", "lang_extra.csv", ".assets/lang.csv")

# SaveSettings' anonymous enum, in declaration order.
ENUM = {
    "CAM_VANILLA": 0,
    "CAM_MODERN": 1,
    "CAMS_BELOW": 2,
    "CAMS_ACCURATE": 3,
    "CAMS_ABOVE": 4,
    "MM_VANILLA_1_5": 5,
    "MM_ALL_LOYAL_3_8": 6,
    "TEAMSIZE_1X1": 7,
    "TEAMSIZE_2X2": 8,
    "TEAMSIZE_5X5": 9,
    "TEAMSIZE_7X7": 10,
    "TEAMSIZE_10X10": 11,
    "TIME_5": 12,
    "TIME_7": 13,
    "TIME_15": 14,
    "TIME_INF": 15,
    "DEV_25": 16,
    "DEV_15": 17,
    "DEV_5": 18,
    "DEV_0": 19,
}


def boolean(label: str, setting: str, tab: str, column: str) -> dict:
    return {"kind": "boolean", "labelKey": label, "setting": setting,
            "tab": tab, "column": column}


def slider(label: str, setting: str, minimum: float, maximum: float,
           step: float, tab: str, column: str) -> dict:
    return {"kind": "slider", "labelKey": label, "setting": setting,
            "tab": tab, "column": column,
            "min": minimum, "max": maximum, "step": step}


def options(label: str, setting: str, entries, tab: str, column: str) -> dict:
    return {
        "kind": "enum",
        "labelKey": label,
        "setting": setting,
        "tab": tab,
        "column": column,
        "options": [{"textKey": text, "value": value} for text, value in entries],
    }


# ui_settings.gd _ready(), in the order it adds the rows. add_*_option appends
# to the named column, so the reading order is per column, not per call.
#
# The desktop FPS list is the one the port builds: SaveSettings
# .is_mobile_platform() only pushes the uncapped entry in front on desktop.
ROWS = [
    boolean("SKIP LOGIN SCREEN", "skip_login", "main", "right"),
    boolean("FULLSCREEN", "fullscreen", "main", "right"),
    options("LANGUAGE", "language", [("EN", "en"), ("RU", "ru")], "main", "left"),
    options("FPS LIMIT", "fps_limit", [
        ("\u221e", 0), ("30", 30), ("60", 60), ("90", 90), ("120", 120),
    ], "main", "right"),
    # Not in the Godot source: the battle chat row (SettingsData.Chat*).
    options("BATTLE CHAT", "battle_chat", [
        ("ON", 0), ("CHAT TEAM ONLY", 1), ("OFF", 2),
    ], "main", "right"),
    slider("CAMERA SENSETIVITY", "mouse_sens", 0.01, 10, 0.01, "main", "left"),
    slider("CAMERA SENSETIVITY SNIPER", "mouse_sens_sniper", 0.01, 10, 0.01,
           "main", "left"),
    slider("MAP INTRO TIME MUL", "map_loading_screen_timer_mul", 0.2, 3, 0.1,
           "main", "left"),
    boolean("CAMERA SMOOTHING", "camera_smoothing", "main", "right"),
    boolean("AUTO CAPTURE TARGET", "auto_capture_target", "main", "right"),
    boolean("SNIPER AUTO ZOOM", "sniper_auto_zoom", "main", "right"),
    slider("CAMERA START DISTANCE", "camera_len_start", 1, 30, 0.5, "main", "left"),
    slider("BATTLE UI SCALE", "ui_scale_battle", 0.5, 2, 0.05, "main", "left"),
    slider("MENU UI SCALE", "ui_scale_menu", 0.5, 2, 0.05, "main", "left"),
    slider("CAMERA HEIGHT", "camera_height_mul", 0.2, 2, 0.05, "main", "left"),
    boolean("AUTO AIM CAMERA STICK", "auto_aim_soft", "main", "right"),
    slider("AUTO AIM STICK STRENGTH", "auto_aim_stick_strength", 0, 20, 0.5,
           "main", "left"),
    slider("AUTO AIM SENS MUL", "auto_aim_sens_mul", 0.1, 1, 0.05, "main", "left"),
    slider("AUTO AIM RELEASE ANGLE", "auto_aim_release_angle", 5, 90, 1,
           "main", "left"),
    slider("AUTO AIM SNIPER STRENGTH", "auto_aim_stick_strength_sniper", 0, 20,
           0.5, "main", "left"),
    slider("AUTO AIM LEAD", "auto_aim_lead_mul", 0, 3, 0.05, "main", "left"),
    slider("AUTO AIM RECAPTURE DELAY", "auto_aim_recapture_delay", 0, 3, 0.1,
           "main", "left"),
    slider("AUTO AIM COVER GRACE", "auto_aim_cover_grace", 0, 5, 0.1,
           "main", "left"),
    options("MAX BATTLE TIME", "battle_max_time", [
        ("5", ENUM["TIME_5"]), ("7", ENUM["TIME_7"]),
        ("15", ENUM["TIME_15"]), ("INF", ENUM["TIME_INF"]),
    ], "matchmaker", "left"),
    options("MAX TEAM SIZE", "matchmaker_team_size", [
        ("1x1", ENUM["TEAMSIZE_1X1"]), ("2x2", ENUM["TEAMSIZE_2X2"]),
        ("5x5", ENUM["TEAMSIZE_5X5"]), ("7x7", ENUM["TEAMSIZE_7X7"]),
        ("10x10", ENUM["TEAMSIZE_10X10"]),
    ], "matchmaker", "left"),
    options("DMG DEVIATION", "dmg_deviation", [
        ("0%", ENUM["DEV_0"]), ("5%", ENUM["DEV_5"]),
        ("15%", ENUM["DEV_15"]), ("25%", ENUM["DEV_25"]),
    ], "matchmaker", "right"),
    options("PEN DEVIATION", "pen_deviation", [
        ("0%", ENUM["DEV_0"]), ("5%", ENUM["DEV_5"]),
        ("15%", ENUM["DEV_15"]), ("25%", ENUM["DEV_25"]),
    ], "matchmaker", "right"),
    slider("SPOTTING DELAY", "battle_spotting_delay", 0.1, 0.5, 0.1,
           "gameplay", "left"),
    slider("SHELL GRAVITY MUL", "battle_mechanics_projectiles_gravity_mul",
           0, 5, 0.1, "gameplay", "left"),
    boolean("NO AIM TIME", "battle_mechanics_no_aim_time", "gameplay", "right"),
    boolean("NO SHOT DISPERSION", "battle_mechanics_no_disp", "gameplay", "right"),
    boolean("HARMLESS BOTS", "battle_bots_harmless", "gameplay", "right"),
    boolean("BOTS HOLD POSITION", "battle_bots_stationary", "gameplay", "right"),
]


def caption_keys() -> list[str]:
    keys = list(TAB_KEYS)
    for row in ROWS:
        keys.append(row["labelKey"])
        for option in row.get("options", ()):
            keys.append(option["textKey"])
    unique = []
    for key in keys:
        if key not in unique:
            unique.append(key)
    return unique


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
            [godot, "--headless", "--path", str(GODOT_ROOT),
             "--script", f"res://{TEMP_SCRIPT}"],
            capture_output=True,
            env=dict(os.environ, BLITZ_SETTINGS_OUT=str(out)),
            text=True,
            timeout=900,
            check=False,
        )
    finally:
        temp.unlink(missing_ok=True)

    paths = [out / name for name in ("theme.json", "settings.json")]
    for path in paths:
        if not path.is_file():
            raise SystemExit(
                f"godot dump produced no {path.name} (exit {result.returncode}):\n"
                + result.stdout[-4000:]
                + result.stderr[-4000:]
            )
    return tuple(json.loads(path.read_text(encoding="utf-8")) for path in paths)


def node(dump: dict, path: str) -> dict:
    for entry in dump["nodes"]:
        if entry["path"] == path:
            return entry
    raise SystemExit(f"the source dump has no node {path!r}")


def box(dump: dict, path: str) -> dict[str, object]:
    """Anchors, offsets and scale: everything the Unity rect needs."""
    entry = node(dump, path)
    data = {
        "path": path,
        "class": entry["class"],
        "anchors": entry["anchors"],
        "offsets": [round(value, 4) for value in entry["offsets"]],
        "scale": entry["scale"],
        "size": [round(value, 4) for value in entry["size"]],
        "rect": [round(value, 4) for value in entry["rect"]],
        "mouseFilter": entry["mouse_filter"],
        "visible": entry["visible"],
    }
    for key, name in (
        ("text", "text"),
        ("font_size", "fontSize"),
        ("font_color", "fontColor"),
        ("horizontal_alignment", "horizontalAlignment"),
        ("vertical_alignment", "verticalAlignment"),
        ("custom_minimum_size", "minimumSize"),
        ("separation", "separation"),
        ("alignment", "alignment"),
        ("columns", "columns"),
        ("patch_margins", "patchMargins"),
        ("icon_max_width", "iconMaxWidth"),
        ("size_flags", "sizeFlags"),
    ):
        if key in entry:
            data[name] = entry[key]
    variation = entry.get("theme_type_variation")
    if variation is not None:
        data["variation"] = variation or entry["class"]
    if "snd_var" in entry:
        # button_generic.gd plays Audio.ui_enter at 0.7 * snd_vol on SOUND_ENTER.
        data["sndVar"] = entry["snd_var"]
        data["volume"] = round(0.7 * entry["snd_vol"], 4)
    return data


class Atlas:
    """The port's atlas table, plus the cuts this screen still needs.

    Most of what the settings screen draws is art other screens already cut,
    so a rect that matches an existing entry keeps that entry's name and the
    table does not grow. Only genuinely new cuts are added.
    """

    def __init__(self, path: Path):
        self.path = path
        self.data = json.loads(path.read_text(encoding="utf-8"))
        self.added: dict[str, dict] = {}

    def sheet(self, texture: dict) -> str:
        atlas = texture.get("atlas") or {}
        source = atlas.get("resource_path") or texture.get("resource_path", "")
        if source not in SHEETS:
            raise SystemExit(f"unported sheet: {source}")
        return SHEETS[source]

    def name_for(
        self,
        texture: dict,
        wanted: str,
        sub_region=None,
        border=None,
    ) -> str:
        sheet = self.sheet(texture)
        region = texture.get("region")
        if region is None:
            size = texture["size"]
            region = [0.0, 0.0, float(size[0]), float(size[1])]
        if sub_region is not None:
            # A NinePatchRect region_rect cuts again inside the AtlasTexture.
            region = [
                region[0] + sub_region[0],
                region[1] + sub_region[1],
                sub_region[2],
                sub_region[3],
            ]
        entry = {
            "texture": sheet,
            "x": int(round(region[0])),
            "y": int(round(region[1])),
            "w": int(round(region[2])),
            "h": int(round(region[3])),
        }
        if border:
            entry["border"] = list(border)

        for name, existing in self.data["regions"].items():
            if all(existing.get(key) == value for key, value in entry.items()):
                return name

        self.data["regions"][wanted] = entry
        self.added[wanted] = entry
        return wanted

    def add_texture(self, name: str, region: str, width: int, height: int) -> str:
        self.data["textures"][name] = {"width": width, "height": height}
        entry = {"texture": name, "x": 0, "y": 0, "w": width, "h": height}
        self.data["regions"][region] = entry
        self.added[region] = entry
        return region

    def save(self) -> dict[str, object]:
        self.path.write_text(
            json.dumps(self.data, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        return {
            "destination": self.path.name,
            "added": self.added,
            "regions": len(self.data["regions"]),
            "textures": len(self.data["textures"]),
        }


def csv_rows(path: Path) -> dict[str, dict[str, str]]:
    with path.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        fields = reader.fieldnames or []
        key_field = fields[0] if fields else "keys"
        languages = [field for field in fields[1:] if field]
        rows: dict[str, dict[str, str]] = {}
        for row in reader:
            key = (row.get(key_field) or "").strip()
            if key:
                rows[key] = {language: row.get(language, "") for language in languages}
    return rows


def extend_localization(path: Path) -> dict[str, object]:
    data = json.loads(path.read_text(encoding="utf-8"))
    languages = data["languages"]
    missing = [key for key in REQUIRED_KEYS if key not in data["keys"]]
    if missing:
        raise SystemExit(f"lang.json is missing authored captions: {missing}")

    rows: dict[str, dict[str, str]] = {}
    sources = []
    for name in LANGUAGE_CSVS:
        source = GODOT_ROOT / name
        rows.update(csv_rows(source))
        sources.append(record(source))

    added = {}
    literal = []
    for key in caption_keys():
        if key in rows:
            values = {language: rows[key].get(language, "") for language in languages}
        else:
            # No locale translates it, so Godot's atr() shows the literal.
            # The port keeps visible text out of C#, so the literal becomes a
            # key carrying the same text in every language.
            values = {language: key for language in languages}
            literal.append(key)
        data["keys"][key] = values
        added[key] = values

    path.write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    return {
        "destination": path.name,
        "sources": sources,
        "added": added,
        "literal": literal,
        "required": list(REQUIRED_KEYS),
        "keys": len(data["keys"]),
        "languages": len(languages),
    }


def sync_shared_manifest(path: Path, atlas: Path, language: Path) -> None:
    data = json.loads(path.read_text(encoding="utf-8"))
    table = json.loads(atlas.read_text(encoding="utf-8"))
    strings = json.loads(language.read_text(encoding="utf-8"))
    if "localization" in data:
        data["localization"]["keys"] = len(strings["keys"])
        data["localization"]["languages"] = len(strings["languages"])
    if "atlas" in data:
        data["atlas"]["regions"] = len(table["regions"])
        data["atlas"]["textures"] = len(table["textures"])
    # json.load keeps the file's own key order, so re-dumping only touches
    # the counters these manifests carry.
    path.write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )


def main() -> None:
    out = Path(tempfile.mkdtemp(prefix="blitz-settings-"))
    theme, settings = run_dump(out)

    atlas = Atlas(RESOURCES / "Data/atlas_regions.json")

    back = node(settings, "btn_menu")
    header = node(settings, "panel_main_background")
    line = node(settings, "panel_main_background/ThinRedLine")
    templates = "tabs/main/ScrollContainer/VBoxContainer/templates"
    row_patch = node(settings, f"{templates}/boolean_template/NinePatchRect")
    slider_bg = node(settings, f"{templates}/slider_template/slider_bg")

    button_style = theme["types"]["ButtonSmallGeneric"]["styles"]["normal"]
    regions = {
        "buttonSmallGeneric": atlas.name_for(
            button_style["texture"], "settings_button_small"),
        "backIcon": atlas.name_for(back["icon"], NEW_REGION_NAMES["back_icon"]),
        "header": atlas.name_for(header["texture"], NEW_REGION_NAMES["header"]),
        "thinRedLine": atlas.name_for(line["texture"], "settings_thin_red_line"),
        "rowBackground": atlas.name_for(
            row_patch["texture"],
            NEW_REGION_NAMES["row_background"],
            sub_region=row_patch["region_rect"],
            border=row_patch["patch_margins"],
        ),
        "sliderFrame": atlas.name_for(
            slider_bg["texture"], "settings_slider_frame",
            border=slider_bg["patch_margins"]),
        "sliderThumb": atlas.name_for(
            settings["slider_theme"]["grabber"], "settings_slider_thumb"),
    }

    icons = {}
    for icon in settings["checkbox_icons"]:
        region, texture = CHECKBOX_TEXTURES[icon["state"]]
        width, height = icon["size"]
        destination = RESOURCES / "Textures" / f"{texture}.png"
        shutil.copyfile(out / icon["file"], destination)
        icons[icon["state"]] = {
            "region": atlas.add_texture(texture, region, width, height),
            "texture": f"Textures/{texture}.png",
            "size": [width, height],
        }
    regions["checkbox"] = icons

    atlas_stats = atlas.save()
    language_stats = extend_localization(RESOURCES / "Data/lang.json")

    manifest = {
        "generator": Path(__file__).name,
        "policy": (
            "Source geometry, styles and captions only. The layout comes from "
            "a headless Godot dump of ui_settings.tscn and the row list is "
            "transcribed from ui_settings.gd; nothing is measured by eye and "
            "no art is redrawn."
        ),
        "dump": {
            "script": DUMP_SCRIPT,
            "uiSize": settings["ui_size"],
            "note": (
                "Anchors, offsets and scale are resolution independent, so "
                "the headless window size does not leak into the port."
            ),
        },
        "sources": [record(GODOT_ROOT / relative) for relative in SOURCES],
        "atlas": atlas_stats,
        "localization": language_stats,
        "regions": regions,
        "screen": {
            "root": box(settings, "."),
            "blur": box(settings, "blur"),
            "backButton": box(settings, "btn_menu"),
            "header": box(settings, "panel_main_background"),
            "thinRedLine": box(settings, "panel_main_background/ThinRedLine"),
            "title": box(settings, "tank_name"),
            "tabBar": box(settings, "HBoxContainer"),
            "tabButton": box(settings, "HBoxContainer/btn_MAIN"),
            "tabs": [
                {"name": "main", "button": "btn_MAIN", "textKey": "MAIN"},
                {"name": "matchmaker", "button": "btn_matchmaker",
                 "textKey": "MATCHMAKER"},
                {"name": "gameplay", "button": "btn_gameplay",
                 "textKey": "GAMEPLAY"},
            ],
            # tab_container_custom.gd hides the TabContainer's own tab bar and
            # drives current_tab from the button row above it.
            "tabsVisible": settings["tabs"]["tabs_visible"],
            "content": {
                "tab": box(settings, "tabs/main"),
                "scroll": box(settings, "tabs/main/ScrollContainer"),
                "vbox": box(settings, "tabs/main/ScrollContainer/VBoxContainer"),
                "hbox": box(
                    settings,
                    "tabs/main/ScrollContainer/VBoxContainer/HBoxContainer"),
                "column": box(
                    settings,
                    "tabs/main/ScrollContainer/VBoxContainer/HBoxContainer"
                    "/vbox_main_left"),
            },
            "templates": {
                "boolean": {
                    "root": box(settings, f"{templates}/boolean_template"),
                    "background": box(
                        settings, f"{templates}/boolean_template/NinePatchRect"),
                    "label": box(settings, f"{templates}/boolean_template/name"),
                    "checkbox": box(
                        settings, f"{templates}/boolean_template/checkbox"),
                },
                "enum": {
                    "root": box(settings, f"{templates}/enum_template"),
                    "background": box(
                        settings, f"{templates}/enum_template/NinePatchRect"),
                    "label": box(settings, f"{templates}/enum_template/name"),
                    "buttons": box(settings, f"{templates}/enum_template/btns"),
                    "button": box(settings, f"{templates}/enum_template/btns/btn2"),
                    "buttonStyles": {
                        state: node(
                            settings,
                            f"{templates}/enum_template/btns/btn2",
                        )[f"style_{state}"]["bg_color"]
                        for state in ("normal", "pressed")
                    },
                },
                "slider": {
                    "root": box(settings, f"{templates}/slider_template"),
                    "background": box(
                        settings, f"{templates}/slider_template/NinePatchRect"),
                    "label": box(settings, f"{templates}/slider_template/name"),
                    "value": box(
                        settings, f"{templates}/slider_template/name/value"),
                    "frame": box(settings, f"{templates}/slider_template/slider_bg"),
                    "slider": box(settings, f"{templates}/slider_template/slider"),
                    "theme": settings["slider_theme"],
                },
            },
            "checkboxScale": node(
                settings, f"{templates}/boolean_template/checkbox")["scale"][0],
            "rows": ROWS,
            "behaviour": {
                # ui_settings.gd _ready / _gui_input.
                "hidesHangarUi": True,
                "closeAction": "menu",
                "savesOnClose": True,
                "restoresHangarUiOnClose": True,
                # _setup_platform_tabs hides the controls tab on mobile and
                # _setup_diagnostics_options hides the debug tab unless the
                # build carries the sentry_qa feature. The port has neither
                # screen yet, so it builds the three tabs above.
                "controlsTabDesktopOnly": True,
                "debugTabRequiresFeature": "sentry_qa",
            },
        },
        "theme": {
            "defaultFontSize": theme["default_font_size"],
            "separations": theme["separations"],
            "types": {
                name: {
                    "fontSize": entry.get("font_size"),
                    "fontColor": entry.get("font_color"),
                    "iconMaxWidth": entry.get("icon_max_width"),
                }
                for name, entry in theme["types"].items()
            },
        },
    }

    destination = RESOURCES / "Data/settings_source_manifest.json"
    destination.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    # The three manifests whose EditMode tests compare these counters against
    # the live tables. The others record the counts of their own milestone.
    for name in (
        "battle_menu_source_manifest.json",
        "battle_ui_source_manifest.json",
        "hangar_source_manifest.json",
    ):
        shared = RESOURCES / "Data" / name
        if shared.is_file():
            sync_shared_manifest(
                shared,
                RESOURCES / "Data/atlas_regions.json",
                RESOURCES / "Data/lang.json",
            )

    print(f"wrote {destination}")
    print(f"atlas: +{len(atlas_stats['added'])} regions -> {atlas_stats['regions']}")
    print(
        f"lang: +{len(language_stats['added'])} keys "
        f"({len(language_stats['literal'])} literal) -> {language_stats['keys']}"
    )


if __name__ == "__main__":
    main()
