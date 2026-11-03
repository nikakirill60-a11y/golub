#!/usr/bin/env python3
"""Port the battle pause menu and the generic popup it opens.

The menu lives in ui_player.tscn as a scaled grey panel with three buttons, and
the confirmation it raises is popup_generic.tscn. Neither can be measured from
the scene text alone: the panels are instanced scenes drawn at half scale, the
popup buttons sit inside an HBoxContainer, and ui_theme_blitz.tres reaches the
ButtonOrange, ButtonRed and ButtonGreen styles through separate stylebox
resources. Tools/godot_battle_menu_dump.gd instantiates both scenes headless;
this generator turns that dump into the missing atlas region, the captions the
menu shows and a manifest the EditMode tests hold the Unity screen against.
Nothing is redrawn, measured by eye or invented.
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
DUMP_SCRIPT = "godot_battle_menu_dump.gd"
TEMP_SCRIPT = "__blitz_battle_menu_dump.gd"

# Source PNG behind an AtlasTexture -> the texture key Unity already ships.
SHEETS = {
    "res://assets/gfx/ui/lobby_common/lobbyCommon_Out.png": "lobbyCommon_Out",
    "res://assets/gfx/ui/battle/battleScreen.png": "battleScreen",
}

# The two row backgrounds ui_player_scoreboard_team.gd swaps between: the
# plain one for everybody and the highlighted one for the local player.
ROW_TEXTURES = {
    "battle_scoreboard_row": "team_ally/team_box/scoreboard_tank_template",
    "battle_scoreboard_row_me": "team_ally/team_box/scoreboard_tank_template_re",
}

# The scene the menu lives in, the script that drives it, the popup it raises,
# the controller that toggles it, the panel art both share and the theme that
# styles their buttons.
SOURCES = [
    "assets/gfx/ui/ui_player.tscn",
    "assets/gfx/ui/battle/ui_player_battle.gd",
    # popup_generic.tscn carries its script inline.
    "assets/gfx/ui/popup_generic.tscn",
    "assets/gfx/ui/grey_panel.tscn",
    "assets/gfx/ui/ui_player_scoreboard_team.tscn",
    "assets/gfx/ui/ui_player_scoreboard_team.gd",
    "assets/tanks/base/base_tank.tscn",
    "assets/fonts/ui_theme_blitz.tres",
    "scripts/ui_elements/button_generic.gd",
    "scripts/_global.gd",
    "scripts/_util.gd",
]

# Captions the menu and the popup show. The first four are authored
# translations the port must find rather than add; popup:to_hangar only
# survives in the recovered lang.csv, so the CSV list below includes it.
REQUIRED_KEYS = ("MENU", "RESUME", "SETTINGS", "TO HANGAR")
REQUESTED_KEYS = (
    "popup:to_hangar",
    "CANCEL",
    "CONFIRM",
    # ui_player_battle.gd builds the observer buttons in code and reads their
    # captions and tooltips straight out of the translation table.
    "spectator:previous/short",
    "spectator:next/short",
    "spectator:previous/hint",
    "spectator:next/hint",
)
LANGUAGE_CSVS = ("lang_dump.csv", "lang_extra.csv", ".assets/lang.csv")

# popup_generic.tscn spells the accept caption out as "OK" and no locale
# translates it, so Godot shows that literal everywhere. The port keeps visible
# text out of C#, so the literal becomes a key carrying the same text.
LITERAL_KEYS = {"OK": "OK"}


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


def run_dump(out: Path) -> tuple[dict, dict, dict]:
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

    paths = [out / name for name in ("theme.json", "battle.json", "popup.json")]
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
        "mouseFilter": entry["mouse_filter"],
        "visible": entry["visible"],
    }
    for key, name in (
        ("text", "text"),
        ("font_size", "fontSize"),
        ("horizontal_alignment", "horizontalAlignment"),
        ("vertical_alignment", "verticalAlignment"),
        ("custom_minimum_size", "minimumSize"),
    ):
        if key in entry:
            data[name] = entry[key]
    variation = entry.get("theme_type_variation")
    if variation is not None:
        data["variation"] = variation or "Button"
    if "snd_var" in entry:
        # button_generic.gd plays Audio.ui_enter at 0.7 * snd_vol on SOUND_ENTER.
        data["sndVar"] = entry["snd_var"]
        data["volume"] = round(0.7 * entry["snd_vol"], 4)
    return data


def row(dump: dict, base: str) -> dict[str, object]:
    """One scoreboard row template with every control it carries."""
    data = {"root": box(dump, base)}
    for name, relative in (
        ("vbox", "vbox"),
        ("hbox", "vbox/hbox"),
        ("spacer", "vbox/hbox/Control"),
        ("class", "vbox/hbox/class"),
        ("level", "lvl"),
        ("player", "name_ply"),
        ("tank", "name_tank"),
        ("kills", "kills"),
    ):
        data[name] = box(dump, f"{base}/{relative}")
    return data


def texture_region(dump: dict, path: str) -> dict:
    """The atlas cut a dumped TextureRect draws, as an atlas-table entry."""
    entry = node(dump, path)
    texture = entry.get("texture")
    if not texture or "atlas" in texture and texture["atlas"] is None:
        raise SystemExit(f"{path} draws no atlas texture")
    sheet = texture["atlas"]["resource_path"]
    if sheet not in SHEETS:
        raise SystemExit(f"{path} is drawn from an unported sheet: {sheet}")
    return {
        "texture": texture["resource_path"],
        "sheet": SHEETS[sheet],
        "region": [int(value) for value in texture["region"]],
    }


def animation(dump: dict, player: str, name: str) -> dict:
    """One AnimationPlayer track set, straight out of the dump."""
    library = dump["animations"][player]["animations"]
    if name not in library:
        raise SystemExit(f"{player} has no animation {name!r}")
    clip = library[name]
    return {
        "name": name,
        "length": clip["length"],
        "step": clip["step"],
        "tracks": {
            track["path"]: {
                "interpolation": track["interpolation"],
                "keys": [
                    {
                        "time": key["time"],
                        "transition": key["transition"],
                        "value": key["value"],
                    }
                    for key in track["keys"]
                ],
            }
            for track in clip["tracks"]
        },
    }


def button_style(theme: dict, type_name: str) -> dict:
    style = theme["types"][type_name]["styles"]["normal"]
    texture = style["texture"]
    sheet = texture["atlas"]["resource_path"]
    if sheet not in SHEETS:
        raise SystemExit(f"{type_name} is drawn from an unported sheet: {sheet}")
    return {
        "stylebox": style["resource_path"],
        "texture": texture["resource_path"],
        "sheet": SHEETS[sheet],
        "region": [int(value) for value in texture["region"]],
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
        if name in data["regions"] and data["regions"][name] != entry:
            raise SystemExit(f"atlas region {name} already differs from the source")
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
        raise SystemExit(f"lang.json is missing authored menu captions: {missing}")

    rows: dict[str, dict[str, str]] = {}
    sources = []
    for name in LANGUAGE_CSVS:
        source = GODOT_ROOT / name
        rows.update(csv_rows(source))
        sources.append(record(source))

    added = {}
    for key in REQUESTED_KEYS:
        if key not in rows:
            raise SystemExit(f"no source translation for {key!r}")
        values = {language: rows[key].get(language, "") for language in languages}
        data["keys"][key] = values
        added[key] = values

    for key, literal in LITERAL_KEYS.items():
        values = {language: literal for language in languages}
        data["keys"][key] = values
        added[key] = values

    path.write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    return {
        "destination": path.name,
        "sources": sources,
        "added": added,
        "required": list(REQUIRED_KEYS),
        "keys": len(data["keys"]),
        "languages": len(languages),
    }


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


def main() -> None:
    out = Path(tempfile.mkdtemp(prefix="blitz-battle-menu-"))
    theme, battle, popup = run_dump(out)

    regions = {"button_orange": button_style(theme, "ButtonOrange")}
    regions.update(
        {name: texture_region(battle, path) for name, path in ROW_TEXTURES.items()}
    )
    atlas_stats = extend_atlas_table(RESOURCES / "Data/atlas_regions.json", regions)
    language_stats = extend_localization(RESOURCES / "Data/lang.json")

    manifest = {
        "generator": Path(__file__).name,
        "policy": (
            "Source geometry, styles and captions only. The layout comes from a "
            "headless Godot dump of ui_player.tscn and popup_generic.tscn; "
            "nothing is measured by eye and no art is redrawn."
        ),
        "dump": {
            "script": DUMP_SCRIPT,
            "uiSize": battle["ui_size"],
            "note": (
                "Anchors, offsets and scale are resolution independent, so the "
                "headless window size does not leak into the port."
            ),
        },
        "sources": [record(GODOT_ROOT / relative) for relative in SOURCES],
        "menu": {
            "background": box(battle, "menu_bg"),
            "panel": box(battle, "menu_bg/menu"),
            "header": box(battle, "menu_bg/menu/header"),
            "title": box(battle, "menu_bg/menu/header2"),
            "buttons": {
                "hangar": box(battle, "menu_bg/menu/btn_hangar"),
                "settings": box(battle, "menu_bg/menu/btn_settings"),
                "resume": box(battle, "menu_bg/menu/btn_resume"),
            },
            "behaviour": {
                # base_tank.tscn, player_controller._unhandled_input: the menu
                # toggles on the "menu" action, releases the mouse while it is
                # open and frees a popup that is still up. It never pauses the
                # tree, so the battle keeps running behind it.
                "action": "menu",
                "togglesVisibility": True,
                "releasesMouseWhileOpen": True,
                "closesOpenPopup": True,
                "pausesTree": False,
                # ui_player_battle.gd: b_resume hides the menu and re-locks the
                # mouse, b_hangar raises the confirmation popup and b_settings
                # displays the settings screen.
                "resumeRelocksMouse": True,
                "hangarConfirms": True,
            },
        },
        "scoreboard": {
            # ui_player.tscn instances ui_player_scoreboard_team.tscn twice:
            # once directly as team_ally and once inside team_enemy_handle,
            # the second one with reversed = true.
            "ally": box(battle, "team_ally"),
            "allyBox": box(battle, "team_ally/team_box"),
            "allyButton": box(battle, "team_ally/ButtonCustom"),
            "enemyHandle": box(battle, "team_enemy_handle"),
            "enemy": box(battle, "team_enemy_handle/team_enemy"),
            "enemyBox": box(battle, "team_enemy_handle/team_enemy/team_box"),
            "enemyButton": box(battle, "team_enemy_handle/team_enemy/ButtonCustom"),
            "row": row(battle, "team_ally/team_box/scoreboard_tank_template"),
            "rowReversed": row(
                battle, "team_ally/team_box/scoreboard_tank_template_re"),
            "textures": {
                name: texture_region(battle, path)
                for name, path in ROW_TEXTURES.items()
            },
            "animation": animation(
                battle, "anims_scoreboard", "anims/scoreboard_slide"),
            "reset": animation(battle, "anims_scoreboard", "RESET"),
            "behaviour": {
                # ui_player_battle.gd: the "scoreboard" action (Tab) and both
                # team buttons call toggle_scoreboard(), which plays the slide
                # forward when the board is off and backwards when it is on.
                # Setup calls it once, so the battle opens with one play.
                "action": "scoreboard",
                "toggledByTeamButtons": True,
                "toggledOnceDuringSetup": True,
                # ui_player_scoreboard_team.gd: a dead row fades to half alpha
                # and is moved to the end of the box, a kill count only shows
                # up once the player has one, and the local player's row uses
                # the highlighted background.
                "deadRowAlpha": 0.5,
                "deadRowMovesLast": True,
                "killsHiddenUntilFirst": True,
                "nameLimit": 15,
            },
        },
        # ui_player_battle.gd builds the observer strip in code, so its numbers
        # are transcribed from _ensure_spectator_controls and the death half of
        # player_controller._physics_process rather than dumped from a scene.
        "spectator": {
            "path": "mobile_shit/spectator_controls",
            "class": "HBoxContainer",
            "preset": "PRESET_CENTER_BOTTOM",
            "anchors": [0.5, 1.0, 0.5, 1.0],
            "offsets": [-66.0, -78.0, 66.0, -22.0],
            "separation": 16,
            "button": {
                "minimumSize": [56.0, 52.0],
                "fontSize": 24,
                "sizeFlagsHorizontal": "SIZE_EXPAND_FILL",
            },
            "buttons": [
                {
                    "name": "previous",
                    "direction": -1,
                    "textKey": "spectator:previous/short",
                    "hintKey": "spectator:previous/hint",
                },
                {
                    "name": "next",
                    "direction": 1,
                    "textKey": "spectator:next/short",
                    "hintKey": "spectator:next/hint",
                },
            ],
            "behaviour": {
                # set_combat_hud_visible(false) on player death: the strip is
                # shown exactly while the combat HUD is hidden.
                "visibleWhileCombatHudHidden": True,
                # The strip hangs off mobile_shit, whose visibility is
                # SaveSettings.is_mobile_platform(), so a desktop battle
                # cycles the camera with attack1 / block_turret only.
                "touchLayerOnly": True,
                # cycle_spectator only answers a dead tank and walks the player
                # team with posmod, skipping candidates that are gone.
                "requiresDeadTank": True,
                "startIndex": -1,
                "wrap": "posmod",
                "skipsDeadCandidates": True,
                # The death branch of player_controller._physics_process.
                "attack1Direction": 1,
                "blockTurretDirection": -1,
                # The ported half of the node list set_combat_hud_visible walks.
                "hiddenNodes": [
                    "crosshair_dynamic",
                    "crosshair_static_handle",
                    "hp",
                    "shells_equipment",
                    "ui_speed",
                    "ui_dist",
                    "reload",
                    "virtual_joystick",
                    "btn_fire",
                    "btn_sniper",
                    "btn_autttoaim",
                ],
            },
        },
        "popup": {
            "root": box(popup, "."),
            "panel": box(popup, "panel"),
            "header": box(popup, "header"),
            "text": box(popup, "main_text"),
            "buttons": {
                "row": box(popup, "HBoxContainer"),
                "ok": box(popup, "HBoxContainer/btn_ok"),
                "cancel": box(popup, "HBoxContainer/btn_cancel"),
            },
            "behaviour": {
                # popup_generic.gd: accept runs the callback and frees the
                # popup, cancel only frees it, ui_cancel closes it and
                # ui_accept presses OK.
                "acceptClosesPopup": True,
                "cancelKeyCloses": True,
                "acceptKeyPressesOk": True,
                "headerText": "CONFIRM",
                "bodyKey": "popup:to_hangar",
            },
        },
        "styles": {
            name: button_style(theme, name)
            for name in ("Button", "ButtonRed", "ButtonOrange", "ButtonGreen")
        },
        "atlas": atlas_stats,
        "localization": language_stats,
    }

    destination = RESOURCES / "Data/battle_menu_source_manifest.json"
    destination.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(f"wrote {destination.relative_to(UNITY_ROOT)}")

    for name in (
        "hangar_source_manifest.json",
        "battle_ui_source_manifest.json",
    ):
        shared = RESOURCES / "Data" / name
        if shared.is_file():
            stats = sync_shared_manifest(
                shared,
                RESOURCES / "Data/atlas_regions.json",
                RESOURCES / "Data/lang.json",
            )
            print(f"synced {name}: {stats}")


if __name__ == "__main__":
    main()
