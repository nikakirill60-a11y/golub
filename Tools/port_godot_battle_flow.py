#!/usr/bin/env python3
"""Port the Godot queue, map intro, loading spinner and result presentation.

The runtime remains code-first; this generator only copies original artwork/audio,
records source provenance and exposes AtlasTexture rectangles to Unity.  WebP
sources are deterministically decoded to PNG because Unity 6000 does not import
WebP as Texture2D on every target used by this project.
"""

from __future__ import annotations

import csv
import hashlib
import json
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

from PIL import Image

GODOT_ROOT = next(
    (
        candidate
        for candidate in (
            Path("/home/smh01/Studio canaryProjects/Blitz Legacy"),
            Path("/home/smh01/projects/blitz-legacy-godot"),
        )
        if candidate.is_dir()
    ),
    Path("/home/smh01/Studio canaryProjects/Blitz Legacy"),
)
UNITY_ROOT = Path(__file__).resolve().parents[1]
RECT_RE = re.compile(r"^region\s*=\s*Rect2\(([^)]*)\)", re.MULTILINE)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def record(path: Path, root: Path = GODOT_ROOT) -> dict[str, object]:
    return {
        "source": path.relative_to(root).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def rect(path: Path) -> list[int | float]:
    match = RECT_RE.search(path.read_text(encoding="utf-8"))
    if match is None:
        raise ValueError(f"AtlasTexture has no region: {path}")
    values = [float(item.strip()) for item in match.group(1).split(",")]
    if len(values) != 4:
        raise ValueError(f"Bad AtlasTexture region: {path}")
    return [int(value) if value.is_integer() else value for value in values]


def copy_image(source: Path, destination: Path) -> dict[str, object]:
    destination.parent.mkdir(parents=True, exist_ok=True)
    if source.suffix.lower() == ".png":
        shutil.copyfile(source, destination)
    else:
        with Image.open(source) as image:
            image.convert("RGBA").save(destination, format="PNG", compress_level=9)
    with Image.open(destination) as image:
        width, height = image.size
    return {
        **record(source),
        "destination": destination.name,
        "destinationSha256": sha256(destination),
        "width": width,
        "height": height,
    }


def copy_audio(source: Path, destination: Path) -> dict[str, object]:
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)
    return {
        **record(source),
        "destination": destination.name,
        "destinationSha256": sha256(destination),
    }


def add_sheet(
    table: dict[str, object],
    manifest: dict[str, object],
    texture_key: str,
    source_relative: str,
    destination_name: str,
    regions: dict[str, str],
) -> None:
    source = GODOT_ROOT / source_relative
    destination = UNITY_ROOT / "Assets/Blitz/Resources/Textures" / destination_name
    copied = copy_image(source, destination)
    table["textures"][texture_key] = {
        "width": copied["width"],
        "height": copied["height"],
    }
    sheet_regions: dict[str, object] = {}
    for name, resource_relative in regions.items():
        resource = GODOT_ROOT / resource_relative
        x, y, width, height = rect(resource)
        if x < 0 or y < 0 or width <= 0 or height <= 0:
            raise ValueError(f"Invalid region in {resource}")
        if x + width > copied["width"] or y + height > copied["height"]:
            raise ValueError(f"Region leaves source sheet in {resource}")
        table["regions"][name] = {
            "texture": texture_key,
            "x": x,
            "y": y,
            "w": width,
            "h": height,
        }
        sheet_regions[name] = {**record(resource), "rect": [x, y, width, height]}
    manifest[texture_key] = {**copied, "regions": sheet_regions}


def add_existing_region(
    table: dict[str, object],
    manifest: dict[str, object],
    name: str,
    texture_key: str,
    resource_relative: str,
) -> None:
    if texture_key not in table["textures"]:
        raise KeyError(f"Required existing Unity texture is absent: {texture_key}")
    resource = GODOT_ROOT / resource_relative
    x, y, width, height = rect(resource)
    table["regions"][name] = {
        "texture": texture_key,
        "x": x,
        "y": y,
        "w": width,
        "h": height,
    }
    manifest[name] = {
        **record(resource),
        "texture": texture_key,
        "rect": [x, y, width, height],
    }


def csv_rows(path: Path) -> tuple[list[str], dict[str, dict[str, str]]]:
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
    return languages, rows


def extend_localization(path: Path) -> dict[str, object]:
    data = json.loads(path.read_text(encoding="utf-8"))
    all_rows: dict[str, dict[str, str]] = {}
    sources: list[dict[str, object]] = []
    for file_name in ("lang_dump.csv", "lang_extra.csv"):
        source = GODOT_ROOT / file_name
        _languages, rows = csv_rows(source)
        all_rows.update(rows)
        sources.append(record(source))

    requested = {
        "Survived",
        "battle/startsIn",
        "battle_notif:battle",
        "battle_notif:details",
        "battle_notif:received",
        "battle_notif:tank",
        "bresult/DETAILS",
        "bresult/PERSONAL",
        "bresult/TEAM",
        "map_intro:enc_battle",
        "PLEASE_WAIT",
        "queue.ht",
        "queue.lt",
        "queue.mt",
        "queue.players",
        "queue.please_wait",
        "queue.tanks_by_lvl",
        "queue.td",
        "queue.wait_time",
        "randomBattle",
        "ALLIES",
        "ENEMIES",
        "TO HANGAR",
    }
    for key in requested:
        if key in all_rows:
            data["keys"][key] = all_rows[key]

    # These keys are referenced by the source result scripts but are absent from
    # the recovered CSV.  Godot therefore displays English literals/keys.  Keep
    # the Unity code string-free while supplying the corresponding Russian UI.
    additions = {
        "battle_notif:victory": {"en": "Victory!", "ru": "Победа!"},
        "battle_notif:defeat": {"en": "Defeat", "ru": "Поражение"},
        "battle_notif:draw": {"en": "Draw", "ru": "Ничья"},
        "battleResult:prem/has": {"en": "With Premium Account", "ru": "С премиум аккаунтом"},
        "battleResult:prem/hasnt": {"en": "Without Premium Account", "ru": "Без премиум аккаунта"},
        "battleResult:reason/extermination": {"en": "All enemy vehicles destroyed", "ru": "Вся техника противника уничтожена"},
        "battleResult:reason/timeout": {"en": "Battle time expired", "ru": "Время боя истекло"},
        "battleResult:destroyed": {"en": "Destroyed", "ru": "Уничтожен"},
        "battleResult:earned": {"en": "Earned", "ru": "Получено"},
        "battleResult:damage": {"en": "Damage", "ru": "Урон"},
        "battleResult:kills": {"en": "Destroyed", "ru": "Уничтожено"},
        "battleResult:xp": {"en": "Experience", "ru": "Опыт"},
        "battleResult:credits": {"en": "Credits", "ru": "Кредиты"},
        "battleResult:gold": {"en": "Gold", "ru": "Золото"},
        "battleResult:free_xp": {"en": "Free XP", "ru": "Свободный опыт"},
        "battleResult:shots": {"en": "Shots fired", "ru": "Выстрелов"},
        "battleResult:hits": {"en": "Hits", "ru": "Попаданий"},
        "battleResult:blocked": {"en": "Damage blocked", "ru": "Заблокировано урона"},
        "battleResult:taken": {"en": "Damage received", "ru": "Получено урона"},
        "battleResult:player": {"en": "PLAYER", "ru": "ИГРОК"},
        "battleResult:tank": {"en": "TANK", "ru": "ТАНК"},
        "battleResult:status": {"en": "STATUS", "ru": "СТАТУС"},
        "battleResult:no_rewards": {"en": "No awards earned", "ru": "Наград нет"},
        "rewards:first_blood/name": {"en": "First Blood", "ru": "Первая кровь"},
        "rewards:first_victory/name": {"en": "First Victory", "ru": "Первая победа"},
        "rewards:warrior/name": {"en": "Top Gun", "ru": "Воин"},
    }
    data["keys"].update(additions)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {"sources": sources, "added": sorted(additions), "required": sorted(requested)}


def sync_shared_manifest(path: Path, table: dict[str, object], language_path: Path) -> dict[str, object]:
    data = json.loads(path.read_text(encoding="utf-8"))
    language = json.loads(language_path.read_text(encoding="utf-8"))
    if "localization" in data:
        data["localization"]["keys"] = len(language["keys"])
        data["localization"]["languages"] = len(language["languages"])
    if "atlas" in data:
        data["atlas"]["regions"] = len(table["regions"])
        data["atlas"]["textures"] = len(table["textures"])
    path.write_text(
        json.dumps(data, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    return {
        "destination": path.name,
        "localizationKeys": len(language["keys"]),
        "atlasRegions": len(table["regions"]),
        "atlasTextures": len(table["textures"]),
    }


def write_flow_data(path: Path) -> dict[str, object]:
    source = GODOT_ROOT / "scripts/_battle.gd"
    text = source.read_text(encoding="utf-8")
    match = re.search(
        r"static var names_main: Array\[String\] = \[(.*?)\n\]",
        text,
        re.DOTALL,
    )
    if match is None:
        raise RuntimeError("could not recover names_main from scripts/_battle.gd")
    names = re.findall(r'"([^"\\]*(?:\\.[^"\\]*)*)"', match.group(1))
    if len(names) < 3:
        raise RuntimeError("source bot nickname list is unexpectedly short")
    data = {
        "botNames": names,
        "queueMatchDelayMin": 1.0,
        "queueMatchDelayMax": 3.0,
        "queueRefreshSeconds": 5.0,
        "introFallbackSeconds": 6.0,
        "introMusicFadeSeconds": 0.8,
        "countdownSeconds": 5.0,
        "resultDelaySeconds": 5.0,
    }
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {**record(source), "botNames": len(names), "destination": path.name}


def port_reward_definitions(table, sheets):
    # Godot resolves UID-only reward icons and executes the source registry.
    with tempfile.TemporaryDirectory() as temp:
        path = Path(temp) / 'rewards.json'
        run = subprocess.run(['godot', '--headless', '--path', str(GODOT_ROOT),
            '--script', str(UNITY_ROOT / 'Tools/godot_result_rewards_dump.gd'), '--', str(path)],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=120)
        if run.returncode or not path.exists():
            raise RuntimeError('Reward source dump failed: ' + run.stdout[-2000:])
        definitions = json.loads(path.read_text())
    if len(definitions) != 23:
        raise RuntimeError('Unexpected source reward registry size')
    for reward, info in definitions.items():
        x, y, w, h = info['rect']
        table['regions']['reward_' + reward] = dict(texture='flow_achievements', x=x, y=y, w=w, h=h)
    data = {'source': record(GODOT_ROOT / 'scripts/_player_data.gd'), 'definitions': definitions}
    target = UNITY_ROOT / 'Assets/Blitz/Resources/Data/battle_reward_definitions.json'
    target.write_text(json.dumps(data, ensure_ascii=False, indent=2, sort_keys=True) + '\n')


def main() -> None:
    resources = UNITY_ROOT / "Assets/Blitz/Resources"
    table_path = resources / "Data/atlas_regions.json"
    table = json.loads(table_path.read_text(encoding="utf-8"))
    for name in list(table["regions"]):
        # SmallTankIcons are generated from the original Blitz clients, not the
        # Godot fallback sheet. Keep their complete lookup on flow-only reruns.
        is_small_tank_icon = (
            name.startswith("flow_tank_") and name.endswith("_small")
        )
        if name.startswith("flow_") and not is_small_tank_icon:
            del table["regions"][name]
    for name in list(table["textures"]):
        if name.startswith("flow_"):
            del table["textures"][name]

    sheets: dict[str, object] = {}
    add_sheet(
        table, sheets, "flow_common", "assets/gfx/ui/common/commonUiElements_Out.png",
        "flow_common.png", {
            "flow_loading_spinner": "assets/gfx/ui/common/LoadingProcess.tres",
        },
    )
    add_sheet(
        table, sheets, "flow_queue_screen", "assets/gfx/ui/queue/queueScreen_Out.png",
        "flow_queue_screen.png", {
            "flow_queue_pattern": "assets/gfx/ui/queue/BackgroundPattern.tres",
            "flow_queue_clock": "assets/gfx/ui/queue/CounterIcons.tres",
            "flow_queue_players": "assets/gfx/ui/queue/CounterIcons_02.tres",
            "flow_queue_line": "assets/gfx/ui/queue/HorizontalLine.tres",
            "flow_queue_yellow_line": "assets/gfx/ui/queue/HorizontalYellowLine.tres",
        },
    )
    add_sheet(
        table, sheets, "flow_queue_bg", "assets/gfx/ui/queue/bg_Out.png",
        "flow_queue_bg.png", {"flow_queue_bg": "assets/gfx/ui/queue/bg.tres"},
    )
    add_sheet(
        table, sheets, "flow_battle_loading", "assets/gfx/ui/battle/battleLoading_Out.png",
        "flow_battle_loading.png", {
            "flow_intro_table": "assets/gfx/ui/battle/TableBackground.tres",
            "flow_intro_logo": "assets/gfx/ui/battle/LogotypeIcons_02.tres",
            "flow_intro_player": "assets/gfx/ui/battle/YourTank.tres",
        },
    )
    # Keep the historical Godot sheet as source evidence, but do not register
    # its lone MS-1 cut here. port_blitz_small_tank_icons.py owns the complete
    # 357-vehicle lookup and rerunning this porter must not replace that exact
    # 4.8 crop with the old one-off fallback.
    add_sheet(
        table, sheets, "flow_small_tanks", "assets/tanks/icons_small/icons_small_2.6_Out.webp",
        "flow_small_tanks.png", {},
    )
    result_regions = {
        "flow_result_ribbon_left_draw": "assets/gfx/ui/battle_result/bgMedalsRight_02.tres",
        "flow_result_ribbon_right_draw": "assets/gfx/ui/battle_result/bgMedalsLeft_02.tres",
        "flow_result_ribbon_left_win": "assets/gfx/ui/battle_result/bgMedalsRight_03.tres",
        "flow_result_ribbon_right_win": "assets/gfx/ui/battle_result/bgMedalsLeft_03.tres",
        "flow_result_ribbon_left_lose": "assets/gfx/ui/battle_result/bgMedalsRight.tres",
        "flow_result_ribbon_right_lose": "assets/gfx/ui/battle_result/bgMedalsLeft.tres",
        "flow_result_table": "assets/gfx/ui/battle_result/bgTable_02.tres",
        "flow_result_line": "assets/gfx/ui/battle_result/TableLine.tres",
        "flow_result_stat_player": "assets/gfx/ui/battle_result/statIcon.tres",
        "flow_result_stat_tank": "assets/gfx/ui/battle_result/statIcon_02.tres",
        "flow_result_stat_damage": "assets/gfx/ui/battle_result/statIcon_03.tres",
        "flow_result_stat_kills": "assets/gfx/ui/battle_result/statIcon_04.tres",
        "flow_result_stat_xp": "assets/gfx/ui/battle_result/statIcon_05.tres",
        # ui_bresult_player row divider, plus the frames the main tab draws.
        "flow_result_divider": "assets/gfx/ui/battle_result/bgTable.tres",
        "flow_result_tank_select": "assets/gfx/ui/battle_result/tankSelect.tres",
        "flow_result_white_line": "assets/gfx/ui/battle_result/whiteramka.tres",
    }
    add_sheet(
        table, sheets, "flow_battle_result", "assets/gfx/ui/battle_result/battleResult_atlas.png",
        "flow_battle_result.png", result_regions,
    )
    add_existing_region(table, sheets, "result_menu_home", "lobbyCommon_Out",
        "assets/gfx/ui/lobby_common/IconsSmall031.tres")
    add_existing_region(table, sheets, "result_elite", "lobbyCommon_Out",
        "assets/gfx/ui/lobby_common/TankTypesBig.tres")
    add_existing_region(table, sheets, "result_elite_prem", "lobbyCommon_Out",
        "assets/gfx/ui/lobby_common/TankTypesBig06.tres")
    table["regions"]["flow_result_table"]["border"] = [8, 8, 8, 8]
    add_sheet(
        table, sheets, "flow_result_win", "assets/gfx/ui/battle_result/bg/win.webp",
        "flow_result_win.png", {
            "flow_result_win_top": "assets/gfx/ui/battle_result/bg/HeadBackgroundVictory_03.tres",
            "flow_result_win_bottom": "assets/gfx/ui/battle_result/bg/HeadBackgroundVictory_02.tres",
        },
    )
    add_sheet(
        table, sheets, "flow_result_draw", "assets/gfx/ui/battle_result/bg/draw.webp",
        "flow_result_draw.png", {
            "flow_result_draw_top": "assets/gfx/ui/battle_result/bg/HeadBackgroundDraw_02.tres",
            "flow_result_draw_detail": "assets/gfx/ui/battle_result/bg/HeadBackgroundDraw.tres",
        },
    )
    lose_regions = {
        f"flow_result_lose_{index}": f"assets/gfx/ui/battle_result/bg/{name}"
        for index, name in enumerate([
            "HeadBackgroundDefeat1_07.tres", "HeadBackgroundDefeat1_06.tres",
            "HeadBackgroundDefeat1_05.tres", "HeadBackgroundDefeat1_04.tres",
            "HeadBackgroundDefeat1_03.tres", "HeadBackgroundDefeat1_02.tres",
            "HeadBackgroundDefeat1.tres",
        ], start=1)
    }
    add_sheet(
        table, sheets, "flow_result_lose", "assets/gfx/ui/battle_result/bg/lose.webp",
        "flow_result_lose.png", lose_regions,
    )
    add_sheet(
        table, sheets, "flow_profile", "assets/gfx/ui/profile/profile.png",
        "flow_profile.png", {
            # ui_medal.tscn: duplicate counter badge and its accumulated variant.
            "flow_result_medal_series": "assets/gfx/ui/profile/SeriesCounter.tres",
            "flow_result_medal_number": "assets/gfx/ui/profile/Number.tres",
            # btn_menu_battleres2 icon (ui_hangar.tscn ext_resource 99_k5k6c).
            "flow_result_menu_profile": "assets/gfx/ui/profile/tab-profile_04.tres",
        },
    )
    add_sheet(
        table, sheets, "flow_achievements",
        "assets/gfx/ui/lobby_common/achievements/achievements.png",
        "flow_achievements.png", {
            "flow_reward_first_blood": "assets/gfx/ui/lobby_common/achievements/firstBlood.tres",
            "flow_reward_first_victory": "assets/gfx/ui/lobby_common/achievements/firstVictory.tres",
            "flow_reward_warrior": "assets/gfx/ui/lobby_common/achievements/warrior.tres",
        },
    )

    port_reward_definitions(table, sheets)

    existing: dict[str, object] = {}
    for name, texture, resource in (
        ("flow_queue_mode", "lobbyCommon_Out", "assets/gfx/ui/lobby_common/LogotypeIcons.tres"),
        ("flow_result_currency_xp", "lobbyCommon_Out", "assets/gfx/ui/lobby_common/CurrencyIconsBig04.tres"),
        ("flow_result_currency_credits", "lobbyCommon_Out", "assets/gfx/ui/lobby_common/CurrencyIconsBig02.tres"),
        ("flow_result_currency_gold", "lobbyCommon_Out", "assets/gfx/ui/lobby_common/CurrencyIconsBig.tres"),
        ("flow_result_tab_personal", "hangar_Out", "assets/gfx/ui/hangar/SidebarIcons03.tres"),
        ("flow_result_tab_team", "hangar_Out", "assets/gfx/ui/hangar/SidebarIcons014.tres"),
        ("flow_result_tab_details", "hangar_Out", "assets/gfx/ui/hangar/SidebarIcons05.tres"),
        # ui_scoreboard_loading_team.tscn: platoon badge and the neutral tier
        # glyphs returned by TanksDataBase.get_lvl_icon(lvl, false, true).
        ("flow_intro_platoon", "battleScreen", "assets/gfx/ui/battle/parsed/vzvods.tres"),
        ("flow_intro_tier_1", "battleScreen", "assets/gfx/ui/battle/parsed/Levels.tres"),
        ("flow_intro_tier_2", "battleScreen", "assets/gfx/ui/battle/parsed/Levels03.tres"),
        ("flow_intro_tier_3", "battleScreen", "assets/gfx/ui/battle/parsed/Levels05.tres"),
        ("flow_intro_tier_4", "battleScreen", "assets/gfx/ui/battle/parsed/Levels07.tres"),
        ("flow_intro_tier_5", "battleScreen", "assets/gfx/ui/battle/parsed/Levels09.tres"),
        ("flow_intro_tier_6", "battleScreen", "assets/gfx/ui/battle/parsed/Levels011.tres"),
        ("flow_intro_tier_7", "battleScreen", "assets/gfx/ui/battle/parsed/Levels013.tres"),
        ("flow_intro_tier_8", "battleScreen", "assets/gfx/ui/battle/parsed/Levels015.tres"),
        ("flow_intro_tier_9", "battleScreen", "assets/gfx/ui/battle/parsed/Levels017.tres"),
        ("flow_intro_tier_10", "battleScreen", "assets/gfx/ui/battle/parsed/Levels019.tres"),
    ):
        add_existing_region(table, existing, name, texture, resource)

    table_path.write_text(json.dumps(table, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    audio_dir = resources / "Audio"
    audio = {
        "map_intro_rudniki": copy_audio(
            GODOT_ROOT / "assets/sfx/music/The_Urge_to_Win.mp3",
            audio_dir / "map_intro_rudniki.mp3",
        ),
        "result_victory": copy_audio(
            GODOT_ROOT / "assets/sfx/music/aftershot/victory.mp3",
            audio_dir / "result_victory.mp3",
        ),
        "result_defeat": copy_audio(
            GODOT_ROOT / "assets/sfx/music/aftershot/defeat.mp3",
            audio_dir / "result_defeat.mp3",
        ),
        "result_draw": copy_audio(
            GODOT_ROOT / "assets/sfx/music/aftershot/draw.mp3",
            audio_dir / "result_draw.mp3",
        ),
    }

    localization = extend_localization(resources / "Data/lang.json")
    flow_data = write_flow_data(resources / "Data/battle_flow.json")
    shared_manifests = [
        sync_shared_manifest(resources / "Data/hangar_source_manifest.json", table,
            resources / "Data/lang.json"),
        sync_shared_manifest(resources / "Data/battle_ui_source_manifest.json", table,
            resources / "Data/lang.json"),
    ]
    source_scenes = [
        "assets/gfx/ui/hangar/ui_hangar.tscn",
        "assets/gfx/ui/battle/ui_battle_intro.tscn",
        "assets/gfx/ui/battle/ui_scoreboard_loading_team.tscn",
        "assets/gfx/ui/hangar/ui_battle_result_team.tscn",
        "assets/gfx/ui/hangar/ui_battle_result_team_player.tscn",
        "assets/gfx/ui/hangar/ui_reward_info.tscn",
        "scripts/_battle.gd",
        "scripts/battle_result_res.gd",
        "scripts/player.gd",
        "scripts/account.gd",
        "scripts/_player_data.gd",
    ]
    manifest = {
        "generator": "Tools/port_godot_battle_flow.py",
        "policy": "Source Godot art/audio and exact AtlasTexture rectangles; no replacement artwork.",
        "scenesAndMechanics": [record(GODOT_ROOT / item) for item in source_scenes],
        "sheets": sheets,
        "existingAtlasRegions": existing,
        "audio": audio,
        "localization": localization,
        "flowData": flow_data,
        "queue": {
            "matchDelaySeconds": [1, 3],
            "refreshSeconds": 5,
            "tierRatios": [9, 6, 12, 4, 5, 7, 8, 12, 9, 2],
            "tierRandomInclusive": [134, 354],
            "typeShareRange": [0.05, 0.25],
        },
        "intro": {"musicFadeSeconds": 0.8, "screenTrackRange": [0.4, 0.75], "countdownSeconds": 5},
        "result": {"postBattleDelaySeconds": 5, "returnDelaySeconds": 2},
        "atlas": {"regions": len(table["regions"]), "textures": len(table["textures"])},
    }
    manifest_path = resources / "Data/battle_flow_source_manifest.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(json.dumps({
        "manifest": manifest_path.name,
        "regions": len(table["regions"]),
        "textures": len(table["textures"]),
        "audio": len(audio),
    }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
