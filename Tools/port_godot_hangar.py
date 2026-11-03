#!/usr/bin/env python3
"""Deterministically port the current Godot hangar/MS-1 source assets.

The converter does not create or repaint game art. It reads the original GLB
geometry with trimesh, bakes the authored node transforms into an OBJ that
Unity can import without a glTF package, and copies the original PNG payloads
byte-for-byte. Run from any directory; both roots may be overridden.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import re
import shutil
from pathlib import Path
from typing import Callable

import numpy as np
import trimesh


HANGAR_SECONDARY = {
    "1_0_sc2", "1_1_sc2", "2_0_sc2", "3_01_sc2", "3_05_sc2",
    "3_12_sc2", "3_14_sc2_001", "3_21_sc2", "3_32_sc2",
    "3_34_sc2_001", "4_1_sc2_001", "5_0_sc2", "5_1_sc2",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def clean_name(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", value)


def write_obj(
    source: Path,
    destination: Path,
    include: Callable[[str], bool],
    material_for: Callable[[str], str],
    materials: dict[str, str],
    record_objects: list[dict[str, object]] | None = None,
    groups: bool = False,
) -> dict[str, int]:
    scene = trimesh.load(source, force="scene", process=False)
    if not isinstance(scene, trimesh.Scene):
        raise RuntimeError(f"Expected a scene in {source}")

    destination.parent.mkdir(parents=True, exist_ok=True)
    mtl_path = destination.with_suffix(".mtl")

    vertex_offset = 1
    vertex_count = 0
    triangle_count = 0
    object_count = 0

    with destination.open("w", encoding="utf-8", newline="\n") as obj:
        obj.write("# Generated from the original Blitz Legacy Godot GLB.\n")
        obj.write(f"mtllib {mtl_path.name}\n")

        for node_name in sorted(scene.graph.nodes_geometry):
            if not include(node_name):
                continue

            transform, geometry_name = scene.graph.get(node_name)
            mesh = scene.geometry[geometry_name].copy()

            # Trimesh clears its cached imported normals when apply_transform is
            # called and would then recalculate them. Capture and transform the
            # original GLB NORMAL accessor instead: this preserves authored
            # shading and keeps SciPy out of the generated-file contract.
            source_normals = np.asarray(mesh.vertex_normals, dtype=np.float64).copy()
            mesh.apply_transform(transform)

            vertices = np.asarray(mesh.vertices, dtype=np.float64)
            faces = np.asarray(mesh.faces, dtype=np.int64)
            normal_matrix = np.linalg.inv(np.asarray(transform)[:3, :3]).T
            normals = source_normals @ normal_matrix.T
            lengths = np.linalg.norm(normals, axis=1, keepdims=True)
            normals = np.divide(normals, lengths, out=np.zeros_like(normals), where=lengths > 0)
            visual_uv = getattr(mesh.visual, "uv", None)
            if visual_uv is None or len(visual_uv) != len(vertices):
                uv = np.zeros((len(vertices), 2), dtype=np.float64)
            else:
                uv = np.asarray(visual_uv, dtype=np.float64)

            if len(vertices) == 0 or len(faces) == 0:
                continue

            obj.write(f"\no {clean_name(node_name)}\n")
            if groups:
                # Unity's importer gives every group its own child object,
                # which is how TankTurret.Rig finds the turret and the
                # installed gun by name. The collision and armour OBJs stay
                # one merged mesh the importer splits per material.
                obj.write(f"g {clean_name(node_name)}\n")
            obj.write(f"usemtl {material_for(node_name)}\n")
            for x, y, z in vertices:
                obj.write(f"v {x:.7f} {y:.7f} {z:.7f}\n")
            for u, v in uv:
                obj.write(f"vt {u:.7f} {v:.7f}\n")
            for x, y, z in normals:
                obj.write(f"vn {x:.7f} {y:.7f} {z:.7f}\n")
            for face in faces:
                indices = [vertex_offset + int(index) for index in face]
                obj.write("f " + " ".join(f"{i}/{i}/{i}" for i in indices) + "\n")

            if record_objects is not None:
                record_objects.append(
                    {
                        "name": clean_name(node_name),
                        "material": material_for(node_name),
                        "vertices": len(vertices),
                        "triangles": len(faces),
                    }
                )

            vertex_offset += len(vertices)
            vertex_count += len(vertices)
            triangle_count += len(faces)
            object_count += 1

    with mtl_path.open("w", encoding="utf-8", newline="\n") as mtl:
        mtl.write("# Source-backed material bindings; runtime recreates URP materials.\n")
        for material_name, texture_path in materials.items():
            mtl.write(f"\nnewmtl {material_name}\n")
            mtl.write("Ka 0.000000 0.000000 0.000000\n")
            mtl.write("Kd 1.000000 1.000000 1.000000\n")
            mtl.write("Ks 0.000000 0.000000 0.000000\n")
            mtl.write("Ns 1.000000\n")
            mtl.write(f"map_Kd {texture_path}\n")

    return {
        "objects": object_count,
        "vertices": vertex_count,
        "triangles": triangle_count,
    }


def copy_exact(source_root: Path, relative: str, destination: Path) -> dict[str, object]:
    source = source_root / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)
    return {
        "source": relative,
        "destination": destination.name,
        "bytes": source.stat().st_size,
        "sha256": sha256(source),
    }


def convert_exact(
    source_root: Path, relative: str, destination: Path
) -> dict[str, object]:
    """Re-encode a source WebP icon as the PNG Unity's importer reads."""
    from PIL import Image

    source = source_root / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    with Image.open(source) as image:
        image.convert("RGBA").save(destination, format="PNG")
    return {
        "source": relative,
        "destination": destination.name,
        "bytes": source.stat().st_size,
        "sha256": sha256(source),
    }


def write_language_table(
    source: Path, destination: Path, additions_source: Path
) -> dict[str, object]:
    """Convert Godot's CSV and scripted additions into runtime JSON."""
    with source.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        languages = [name for name in (reader.fieldnames or [])[1:] if name]
        keys: dict[str, dict[str, str]] = {}
        for row in reader:
            key = (row.get("keys") or "").strip()
            if not key:
                continue
            keys[key] = {language: row.get(language, "") for language in languages}

    # _lang.gd registers map names at startup rather than in lang_dump.csv.
    # Read those calls from the source so map labels never become C#/Python
    # literals and new authored translations flow into a repeat export.
    pattern = re.compile(
        r'add_string\("([^"\\]+)",\s*"([^"\\]+)",\s*"((?:\\.|[^"\\])*)"\)'
    )
    for language, key, encoded_value in pattern.findall(
        additions_source.read_text(encoding="utf-8")
    ):
        if language not in languages:
            raise RuntimeError(
                f"{additions_source} adds unknown language {language!r}"
            )
        values = keys.setdefault(key, {item: "" for item in languages})
        values[language] = json.loads(f'"{encoded_value}"')

    # This text describes the deliberately inactive buttons in the current
    # vertical slice. It is port metadata, not replacement visual/audio art.
    keys["HANGAR_FEATURE_PLACEHOLDER"] = {
        "en": "THIS SECTION IS NOT PORTED YET",
        "ru": "ЭТОТ РАЗДЕЛ ЕЩЁ НЕ ПЕРЕНЕСЁН",
    }
    keys["HANGAR_PLACEHOLDER_HINT"] = {
        "en": "The button is preserved as a placeholder for a later milestone.",
        "ru": "Кнопка сохранена как плейсхолдер для следующего этапа.",
    }
    keys["TANK_MS1"] = {"en": "MS-1", "ru": "МС-1"}
    # btn_battle_mode/caption and filter_onoff render these two strings in the
    # Godot hangar, but lang_dump.csv only carries the "OFF" half of the pair.
    keys["MODE"] = {"en": "MODE", "ru": "РЕЖИМ"}
    keys["ON"] = {"en": "ON", "ru": "ВКЛ."}
    # The Godot HUD hardcodes these units and separators in GDScript. The Unity
    # port keeps visible text out of C#, so they become ordinary locale keys.
    keys["battle:hud/speed"] = {"en": "{0} km/h", "ru": "{0} км/ч"}
    keys["battle:hud/distance"] = {"en": "{0} m", "ru": "{0} м"}
    keys["battle:hud/distance_unknown"] = {"en": "... m", "ru": "... м"}
    keys["battle:hud/health"] = {"en": "{0} / {1}", "ru": "{0} / {1}"}
    keys["battle:hud/fps"] = {"en": "FPS: {0}", "ru": "FPS: {0}"}
    keys.pop("LOGIN_OK_STUB", None)

    # Retain generated stock-tank localization when the base CSV is re-exported.
    tank_titles = destination.with_name("tank_name_localization.json")
    if tank_titles.is_file():
        keys.update(json.loads(tank_titles.read_text(encoding="utf-8"))["keys"])

    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(
        json.dumps({"keys": keys, "languages": languages}, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    return {
        "source": source.name,
        "sha256": sha256(source),
        "scriptSource": "scripts/_lang.gd",
        "scriptSha256": sha256(additions_source),
        "keys": len(keys),
        "languages": len(languages),
    }


def extend_atlas_table(destination: Path) -> dict[str, int]:
    """Append exact source AtlasTexture rects needed by the hangar main screen."""
    data = json.loads(destination.read_text(encoding="utf-8"))
    data["textures"].update({
        "hangar_Out": {"width": 2048, "height": 1024},
        "bigTankIcons1": {"width": 2048, "height": 2048},
        "flags": {"width": 2048, "height": 1024},
        "thinRedLine": {"width": 1024, "height": 22},
        "blackGradTile": {"width": 872, "height": 6},
        "bigTankIcons0": {"width": 2048, "height": 2048},
        "modulesTechTree_Out": {"width": 2048, "height": 1024},
        "tankTechTree_Out": {"width": 1024, "height": 512},
    })
    data["textures"].update({
        "hangar_collector": {"width": 128, "height": 64},
    })
    data["regions"].update({
        "hangar_collector": {"texture": "hangar_collector", "x": 0, "y": 0, "w": 128, "h": 64},
        "hangar_footer": {"texture": "hangar_Out", "x": 572, "y": 209, "w": 4, "h": 250, "border": [2, 0, 2, 0]},
        "hangar_filter_hide": {"texture": "hangar_Out", "x": 1, "y": 541, "w": 188, "h": 252},
        "hangar_play": {"texture": "hangar_Out", "x": 1, "y": 411, "w": 380, "h": 128},
        "hangar_filter_icon": {"texture": "lobbyCommon_Out", "x": 1777, "y": 633, "w": 52, "h": 36},
        # IconsSmall010.tres, ui_hangar.gd t_filter_on: btn_filter's chevron while the filter is up.
        "hangar_filter_icon_on": {"texture": "lobbyCommon_Out", "x": 1031, "y": 474, "w": 52, "h": 36},
        "hangar_red_button": {"texture": "hangar_Out", "x": 131, "y": 795, "w": 124, "h": 124},
        "hangar_news": {"texture": "hangar_Out", "x": 1429, "y": 1, "w": 74, "h": 60},
        "hangar_logout": {"texture": "hangar_Out", "x": 327, "y": 795, "w": 66, "h": 94},
        "hangar_settings": {"texture": "hangar_Out", "x": 476, "y": 309, "w": 86, "h": 86},
        "hangar_header": {"texture": "lobbyCommon_Out", "x": 2033, "y": 1, "w": 4, "h": 152, "border": [2, 0, 2, 0]},
        "hangar_menu": {"texture": "lobbyCommon_Out", "x": 928, "y": 1274, "w": 88, "h": 62},
        "hangar_gold": {"texture": "lobbyCommon_Out", "x": 1831, "y": 633, "w": 40, "h": 40},
        "hangar_credits": {"texture": "lobbyCommon_Out", "x": 1151, "y": 803, "w": 41, "h": 42},
        "hangar_freexp": {"texture": "lobbyCommon_Out", "x": 1915, "y": 633, "w": 40, "h": 38},
        "hangar_crew": {"texture": "lobbyCommon_Out", "x": 581, "y": 1307, "w": 124, "h": 79},
        "hangar_research": {"texture": "lobbyCommon_Out", "x": 529, "y": 1232, "w": 114, "h": 72},
        "hangar_techtree": {"texture": "lobbyCommon_Out", "x": 1487, "y": 449, "w": 88, "h": 62},
        "hangar_gray_button": {"texture": "commonUiElements_Out", "x": 1, "y": 1, "w": 380, "h": 92},
        "hangar_bottom_layer": {"texture": "commonUiElements_Out", "x": 109, "y": 966, "w": 56, "h": 56},
        "tank_slot": {"texture": "lobbyCommon_Out", "x": 671, "y": 269, "w": 358, "h": 236},
        "tank_slot_selected": {"texture": "lobbyCommon_Out", "x": 1, "y": 978, "w": 346, "h": 224},
        "tank_level_1": {"texture": "lobbyCommon_Out", "x": 2004, "y": 745, "w": 35, "h": 57},
        "tank_class_light": {"texture": "lobbyCommon_Out", "x": 1999, "y": 917, "w": 48, "h": 54},
        "tank_icon_ms1": {"texture": "bigTankIcons1", "x": 1482, "y": 203, "w": 218, "h": 193},
        "hangar_shop": {"texture": "lobbyCommon_Out", "x": 1947, "y": 431, "w": 96, "h": 74},
        "hangar_exp": {"texture": "lobbyCommon_Out", "x": 1873, "y": 633, "w": 40, "h": 38},
        "hangar_divider": {"texture": "hangar_Out", "x": 476, "y": 709, "w": 8, "h": 192},
        "hangar_black_grad": {"texture": "blackGradTile", "x": 0, "y": 0, "w": 872, "h": 6},
        "hangar_thin_red_line": {"texture": "thinRedLine", "x": 0, "y": 0, "w": 1024, "h": 22},
        "hangar_select_tile": {"texture": "lobbyCommon_Out", "x": 659, "y": 1611, "w": 46, "h": 46, "border": [14, 14, 14, 14]},
        "hangar_premium": {"texture": "lobbyCommon_Out", "x": 1187, "y": 849, "w": 127, "h": 106},
        "hangar_lvl1": {"texture": "hangar_Out", "x": 2018, "y": 1, "w": 18, "h": 48},
        "hangar_lvl2": {"texture": "hangar_Out", "x": 1219, "y": 77, "w": 31, "h": 48},
        "hangar_lvl3": {"texture": "hangar_Out", "x": 1850, "y": 1, "w": 43, "h": 48},
        "hangar_lvl4": {"texture": "hangar_Out", "x": 1753, "y": 1, "w": 47, "h": 48},
        "hangar_lvl5": {"texture": "hangar_Out", "x": 1981, "y": 1, "w": 35, "h": 48},
        "hangar_lvl6": {"texture": "hangar_Out", "x": 1802, "y": 1, "w": 46, "h": 48},
        "hangar_lvl7": {"texture": "hangar_Out", "x": 1640, "y": 1, "w": 60, "h": 48},
        "hangar_lvl8": {"texture": "hangar_Out", "x": 1566, "y": 1, "w": 72, "h": 48},
        "hangar_lvl9": {"texture": "hangar_Out", "x": 1702, "y": 1, "w": 49, "h": 48},
        "hangar_lvl10": {"texture": "hangar_Out", "x": 1895, "y": 1, "w": 37, "h": 48},
        "hangar_class_lt": {"texture": "hangar_Out", "x": 333, "y": 541, "w": 56, "h": 70},
        "hangar_class_mt": {"texture": "hangar_Out", "x": 395, "y": 823, "w": 64, "h": 80},
        "hangar_class_ht": {"texture": "hangar_Out", "x": 395, "y": 731, "w": 72, "h": 90},
        "hangar_class_td": {"texture": "hangar_Out", "x": 1505, "y": 1, "w": 59, "h": 59},
        "hangar_nation1": {"texture": "flags", "x": 0, "y": 466, "w": 354, "h": 231},
        "hangar_nation2": {"texture": "flags", "x": 0, "y": 233, "w": 354, "h": 231},
        "hangar_nation3": {"texture": "flags", "x": 0, "y": 0, "w": 354, "h": 231},
        "hangar_nation4": {"texture": "flags", "x": 0, "y": 699, "w": 354, "h": 231},
        "hangar_nation5": {"texture": "flags", "x": 356, "y": 0, "w": 354, "h": 231},
        "tank_nation_ussr": {"texture": "lobbyCommon_Out", "x": 1131, "y": 269, "w": 354, "h": 231},
        "tree_tank_bg3": {"texture": "tankTechTree_Out", "x": 355, "y": 143, "w": 352, "h": 140},
        "tree_tank_bg4": {"texture": "tankTechTree_Out", "x": 355, "y": 1, "w": 352, "h": 140},
        "tree_tank_bg5": {"texture": "tankTechTree_Out", "x": 1, "y": 285, "w": 352, "h": 140},
        "tree_tank_bg6": {"texture": "tankTechTree_Out", "x": 1, "y": 143, "w": 352, "h": 140},
        "tree_tank_bg7": {"texture": "tankTechTree_Out", "x": 1, "y": 1, "w": 352, "h": 140},
        "tree_arrowhead": {"texture": "tankTechTree_Out", "x": 0, "y": 492, "w": 13, "h": 14},
        "tree_selector": {"texture": "lobbyCommon_Out", "x": 323, "y": 1204, "w": 24, "h": 128, "border": [9, 9, 9, 9]},
        "module_bg4": {"texture": "modulesTechTree_Out", "x": 999, "y": 107, "w": 132, "h": 132},
        "module_bg6": {"texture": "modulesTechTree_Out", "x": 832, "y": 418, "w": 148, "h": 128},
        "module_bg7": {"texture": "modulesTechTree_Out", "x": 832, "y": 554, "w": 148, "h": 128},
        "module_gun": {"texture": "modulesTechTree_Out", "x": 999, "y": 760, "w": 118, "h": 106},
        "module_engine": {"texture": "modulesTechTree_Out", "x": 999, "y": 643, "w": 118, "h": 115},
        "module_chassis": {"texture": "modulesTechTree_Out", "x": 999, "y": 868, "w": 118, "h": 105},
        # vehicleTurret.tres; the Godot modules screen never showed it because
        # its cards carried no turret module.
        "module_turret": {"texture": "modulesTechTree_Out", "x": 1293, "y": 107, "w": 101, "h": 98},
        "module_installed": {"texture": "modulesTechTree_Out", "x": 751, "y": 569, "w": 58, "h": 58},
        "module_autoloader": {"texture": "modulesTechTree_Out", "x": 327, "y": 935, "w": 56, "h": 55},
        "tank_level_2": {"texture": "lobbyCommon_Out", "x": 879, "y": 1218, "w": 45, "h": 57},
        "tank_level_3": {"texture": "lobbyCommon_Out", "x": 1018, "y": 1179, "w": 55, "h": 57},
        "tank_level_4": {"texture": "lobbyCommon_Out", "x": 1891, "y": 980, "w": 57, "h": 57},
        "tank_level_5": {"texture": "lobbyCommon_Out", "x": 879, "y": 1159, "w": 47, "h": 57},
        "tank_level_6": {"texture": "lobbyCommon_Out", "x": 1018, "y": 1120, "w": 56, "h": 57},
        "tank_level_7": {"texture": "lobbyCommon_Out", "x": 1588, "y": 980, "w": 66, "h": 57},
        "tank_level_8": {"texture": "lobbyCommon_Out", "x": 1244, "y": 980, "w": 76, "h": 57},
        "tank_level_9": {"texture": "lobbyCommon_Out", "x": 1950, "y": 980, "w": 57, "h": 57},
        "tank_level_10": {"texture": "lobbyCommon_Out", "x": 879, "y": 1100, "w": 47, "h": 57},
        "tank_icon_bt2": {"texture": "bigTankIcons1", "x": 1192, "y": 1201, "w": 268, "h": 200},
        "tank_icon_bt7": {"texture": "bigTankIcons1", "x": 907, "y": 1809, "w": 270, "h": 200},
        "tank_icon_a20": {"texture": "bigTankIcons1", "x": 614, "y": 1607, "w": 288, "h": 200},
        "tank_icon_t34": {"texture": "bigTankIcons1", "x": 1, "y": 403, "w": 306, "h": 200},
        "tank_icon_t3485": {"texture": "bigTankIcons0", "x": 967, "y": 1213, "w": 318, "h": 200},
        "tank_icon_t43": {"texture": "bigTankIcons0", "x": 967, "y": 1819, "w": 317, "h": 200},
        "tank_icon_t44": {"texture": "bigTankIcons1", "x": 1572, "y": 1, "w": 310, "h": 200},
        "tank_icon_t54": {"texture": "bigTankIcons0", "x": 1287, "y": 1819, "w": 313, "h": 200},
        "tank_icon_t62a": {"texture": "bigTankIcons0", "x": 645, "y": 1819, "w": 320, "h": 200},
        "tank_class_medium": {"texture": "lobbyCommon_Out", "x": 1018, "y": 1238, "w": 52, "h": 58},
        "tank_class_heavy": {"texture": "lobbyCommon_Out", "x": 1718, "y": 980, "w": 55, "h": 63},
        "tank_class_td": {"texture": "lobbyCommon_Out", "x": 1018, "y": 1458, "w": 50, "h": 51},
        "tank_class_light_prem": {"texture": "lobbyCommon_Out", "x": 1272, "y": 803, "w": 34, "h": 42},
        "tank_class_medium_prem": {"texture": "lobbyCommon_Out", "x": 879, "y": 1385, "w": 38, "h": 46},
        "tank_class_heavy_prem": {"texture": "lobbyCommon_Out", "x": 2005, "y": 675, "w": 41, "h": 52},
        "tank_class_td_prem": {"texture": "lobbyCommon_Out", "x": 1957, "y": 633, "w": 38, "h": 38},
        "tank_level_1_prem": {"texture": "lobbyCommon_Out", "x": 673, "y": 1425, "w": 20, "h": 43},
        "tank_level_2_prem": {"texture": "lobbyCommon_Out", "x": 323, "y": 1334, "w": 30, "h": 43},
        "tank_level_3_prem": {"texture": "lobbyCommon_Out", "x": 1194, "y": 803, "w": 40, "h": 43},
        "tank_level_4_prem": {"texture": "lobbyCommon_Out", "x": 1107, "y": 803, "w": 42, "h": 43},
        "tank_level_5_prem": {"texture": "lobbyCommon_Out", "x": 1308, "y": 803, "w": 33, "h": 43},
        "tank_level_6_prem": {"texture": "lobbyCommon_Out", "x": 1063, "y": 803, "w": 42, "h": 43},
        "tank_level_7_prem": {"texture": "lobbyCommon_Out", "x": 184, "y": 2000, "w": 52, "h": 43},
        "tank_level_8_prem": {"texture": "lobbyCommon_Out", "x": 707, "y": 2000, "w": 62, "h": 43},
        "tank_level_9_prem": {"texture": "lobbyCommon_Out", "x": 771, "y": 2000, "w": 43, "h": 43},
        "tank_level_10_prem": {"texture": "lobbyCommon_Out", "x": 1236, "y": 803, "w": 34, "h": 43},
    })
    used = {region["texture"] for region in data["regions"].values()}
    for name in [name for name in data["textures"] if name not in used]:
        del data["textures"][name]
    destination.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {"textures": len(data["textures"]), "regions": len(data["regions"])}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--godot-root",
        type=Path,
        default=Path.home() / "Studio canaryProjects" / "Blitz Legacy",
    )
    parser.add_argument(
        "--unity-root",
        type=Path,
        default=Path.home() / "projects" / "blitz-legacy-unity",
    )
    args = parser.parse_args()

    godot_root = args.godot_root.expanduser().resolve()
    unity_root = args.unity_root.expanduser().resolve()
    resources = unity_root / "Assets/Blitz/Resources"
    hangar_root = resources / "Models/Hangar"
    tank_root = resources / "Models/Tanks/MS1"

    copied: list[dict[str, object]] = []
    copies = {
        "maps/hangar_low/1.mali_Out.png": hangar_root / "Textures/Hangar1.png",
        "maps/hangar_low/1.emissive.png": hangar_root / "Textures/Hangar1Emission.png",
        "maps/hangar_low/2.mali_Out.png": hangar_root / "Textures/Hangar2.png",
        "maps/hangar_low/3.mali_Out.png": hangar_root / "Textures/Hangar3.png",
        "assets/particles/resources/explosion_smoke_white_stitched.png": hangar_root / "Textures/Smoke.png",
        "assets/tanks/ussr1-MS-1.png": tank_root / "Textures/MS1.png",
        "assets/tanks/icons/bigTankIcons1.png": resources / "Textures/bigTankIcons1.png",
        "assets/gfx/ui/hangar/hangar_Out.png": resources / "Textures/hangar_Out.png",
        "assets/gfx/ui/hangar/blackGradTile.png": resources / "Textures/blackGradTile.png",
        "assets/gfx/ui/lobby_common/thinRedLine.png": resources / "Textures/thinRedLine.png",
        "assets/gfx/ui/lobby_common/flags.png": resources / "Textures/flags.png",
        "assets/tanks/icons/bigTankIcons0.png": resources / "Textures/bigTankIcons0.png",
        "assets/gfx/ui/hangar/tech_tree/modules/modulesTechTree_Out.png":
            resources / "Textures/modulesTechTree_Out.png",
        "assets/gfx/ui/hangar/tech_tree/tank/tankTechTree_Out.png":
            resources / "Textures/tankTechTree_Out.png",
    }
    # The collector badge is the one hangar icon the source keeps outside an
    # atlas, so it travels as its own texture and gets a full-image region.
    converted = [
        convert_exact(
            godot_root,
            "assets/gfx/ui/hangar/icon_tank-collectible.webp",
            resources / "Textures/hangar_collector.png",
        )
    ]
    for relative, destination in copies.items():
        copied.append(copy_exact(godot_root, relative, destination))

    language_stats = write_language_table(
        godot_root / "lang_dump.csv",
        resources / "Data/lang.json",
        godot_root / "scripts/_lang.gd",
    )
    atlas_stats = extend_atlas_table(resources / "Data/atlas_regions.json")

    hangar_stats = write_obj(
        godot_root / "maps/hangar_low/hangar_low_batch.glb",
        hangar_root / "HangarLow.obj",
        include=lambda _name: True,
        material_for=lambda name: (
            "HangarEmissive" if name == "obj_sc2"
            else "HangarSecondary" if name in HANGAR_SECONDARY
            else "HangarMain"
        ),
        materials={
            "HangarMain": "Textures/Hangar1.png",
            "HangarSecondary": "Textures/Hangar2.png",
            "HangarEmissive": "Textures/Hangar3.png",
        },
    )

    # The tanks themselves no longer come from the decompiled Godot build:
    # that export only ever carried the top turret with a few of its guns
    # under the converter names turret01/gun01, which no card can address.
    # Tools/port_blitz_branch_models.py re-exports every researchable
    # turret and gun of the branch from the Blitz client pack instead.

    manifest = {
        "generator": "Tools/port_godot_hangar.py",
        "policy": "Original Godot geometry and PNG bytes only; no authored replacement art.",
        "sources": copied + converted,
        "localization": language_stats,
        "atlas": atlas_stats,
        "models": {
            "hangar": {
                "source": "maps/hangar_low/hangar_low_batch.glb",
                "sha256": sha256(godot_root / "maps/hangar_low/hangar_low_batch.glb"),
                **hangar_stats,
            },
        },
    }
    manifest_path = resources / "Data/hangar_source_manifest.json"
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(json.dumps(manifest["models"], ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
