#!/usr/bin/env python3
"""Port the source ammo/equipment icons and their strings.

The hangar loadout screens draw every shell, every equipment item and the crew
button out of AtlasTextures over two sheets. This generator copies those sheets
and turns each .tres region into an entry of atlas_regions.json, so the Unity
screens reuse the authored artwork instead of redrawing it. The only strings it
touches are the ones the source project actually translates.
"""

from __future__ import annotations

import csv
import json
import re
import shutil
from pathlib import Path

GODOT_ROOT = Path("/home/smh01/Studio canaryProjects/Blitz Legacy")
UNITY_ROOT = Path(__file__).resolve().parents[1]
RESOURCES = UNITY_ROOT / "Assets/Blitz/Resources"
ATLAS = RESOURCES / "Data/atlas_regions.json"
LANG = RESOURCES / "Data/lang.json"
TEXTURES = RESOURCES / "Textures"

SHEETS = {
    "inventory_15.png": "inventory_15",
    # The equipment slot frames of equipment_icon.tscn and
    # ui_selectable_list_entry.tscn all live on this sheet.
    "inventoryScreen_Out.png": "inventoryScreen_Out",
    # Fuel and onigiri are the two consumables the source keeps on the
    # provisions sheet instead of inventory_15.
    "provisions/provisions.png": "provisions",
}

# Region key in Unity -> the .tres the source loads for that widget.
ICONS = {
    "equipment_rammer": "inventory/rammer.tres",
    "equipment_vent": "inventory/improvedVentilation.tres",
    "equipment_aimStab": "inventory/aimingStabilizer.tres",
    "equipment_aimDrive": "inventory/enhancedAimDrives.tres",
    "equipment_toolbox": "inventory/toolbox.tres",
    "equipment_spallliner": "inventory/antifragmentationLining.tres",
    "equipment_optics": "inventory/coatedOptics.tres",
    "equipment_camonet": "inventory/camouflageNet.tres",
    "equipment_stereoscope": "inventory/stereoscope.tres",
    "equipment_empty_slot": "inventory/EmptySlot.tres",
    "shell_icon_1": "inventory/ammo.tres",
    "shell_icon_2": "inventory/ammo_02.tres",
    "shell_icon_3": "inventory/ammo_03.tres",
    "shell_icon_4": "inventory/ammo_04.tres",
    "shell_icon_5": "inventory/ammo_05.tres",
    "shell_icon_6": "inventory/ammo_06.tres",
    "shell_icon_empty_1": "inventory/ammo_no.tres",
    "shell_icon_empty_2": "inventory/ammo_no_02.tres",
    "shell_icon_empty_3": "inventory/ammo_no_03.tres",
    "shell_icon_empty_4": "inventory/ammo_no_04.tres",
    "shell_icon_empty_5": "inventory/ammo_no_05.tres",
    "shell_icon_empty_6": "inventory/ammo_no_06.tres",
    # ui_shell_icon.bgtype_to_bg: the three frames a shell button can wear.
    "shell_bg_none": "battle/parsed/AmmoKeys03.tres",
    "shell_bg_battle": "battle/parsed/AmmoKeys04.tres",
    # BgType.GENERIC, the frame the hangar strip and the shell slider wear.
    "shell_bg_generic": "inventory/BulletButton.tres",
    "shell_bg_selected": "battle/parsed/AmmoKeys08.tres",
    # equipment_icon.tscn: filled slot frame, empty slot frame.
    "equipment_slot_filled": "inventory/BoxEquipPower_04.tres",
    "equipment_slot_empty": "inventory/BoxEquipPower_03.tres",
    # ui_selectable_list_entry.tscn: grid entry frame, slot frame, the
    # installed badge and the selection border.
    "equipment_entry_bg": "inventory/BoxEquipPower_03.tres",
    "equipment_entry_slot_bg": "inventory/CheckBoxEquipPower.tres",
    "equipment_entry_installed": "inventory/BoxEquipPower.tres",
    "equipment_entry_selected": "inventory/BoxEquipPower_02.tres",
    "shell_param_damage": "inventory/ammoParam_dmg.tres",
    "shell_param_penetration": "inventory/ammoParam_pen.tres",
    "hangar_crew_icon": "lobby_common/CrewSkills.tres",
    # ui_shell_slider.tscn: the NinePatch frame behind the row, the HSlider
    # grabber and the icons of btn_sub / btn_add.
    "slider_frame": "common/SliderFrame.tres",
    "slider_thumb": "common/SliderThumb.tres",
    "slider_plus": "common/IconsSmall.tres",
    "slider_minus": "common/IconsSmall02.tres",
    # consumable.gd icons, in the order db_consumables registers them.
    "consumable_repair_kit": "inventory/largeRepairkit.tres",
    "consumable_berserk": "inventory/berserk.tres",
    "consumable_afterburning": "inventory/afterburner.tres",
    "consumable_fuel": "inventory/provisions/regular_fuel.tres",
    "consumable_ration_ussr": "inventory/russ-food.tres",
    "consumable_choc_bar": "inventory/chocolate.tres",
    "consumable_coca_cola": "inventory/cocacola.tres",
    "consumable_teapudding": "inventory/teapudding.tres",
    "consumable_onigiri": "inventory/provisions/onigiri.tres",
    # ui_consumable_icon.gd: the four states of a battle consumable key.
    "consumable_key_empty": "battle/parsed/EquipmentKeys.tres",
    "consumable_key_cooling": "battle/parsed/EquipmentKeys02.tres",
    "consumable_key_ready": "battle/parsed/EquipmentKeys03.tres",
    "consumable_key_active": "battle/parsed/EquipmentKeys04.tres",
}

# Godot patch margins (left, top, right, bottom) of the NinePatchRects above.
BORDERS = {
    "slider_frame": [24, 24, 24, 24],
}

# Samples _audio.gd loads directly, keyed by the Resources/Audio path they get.
SOUNDS = {
    # Audio.ui_drag, played by ui_shell_slider.check_value_changed().
    "drag_01.wav": "ui/drag_01.wav",
    # consumable.gd apply_sound / stop_sound.
    "repair_06.wav": "ui/repair_06.wav",
    "adrenalin.wav": "ui/adrenalin.wav",
    "adrenalin_off.wav": "ui/adrenalin_off.wav",
    "furious.wav": "ui/furious.wav",
    "furious_off.wav": "ui/furious_off.wav",
    # Audio.ui_install / Audio.ui_sell, the two loadout confirmations.
    "module_install_01.wav": "ui/module_install_01.wav",
    "sell_01.wav": "ui/sell_01.wav",
}

# Keys the source project really carries in its translation table. Anything the
# source leaves untranslated stays untranslated here too.
STRING_KEYS = (
    "hangar:shell_total",
    "hangar:shell_cost",
    "hangar:cost",
    "hangar:return_cost",
    "popup:no_shells",
    "popup:confirm_buy",
    "popup:confirm_sell",
    "equipment:rammer/name",
    "equipment:rammer/desc",
    "CREW",
    "EQUIPMENT",
    "SHELLS",
    "CONFIRM",
    # ui_shell_icon.shell_type_to_abbr: the badge under every shell icon.
    "Abbr_ARMOR_PIERCING",
    "Abbr_ARMOR_PIERCING_CR",
    "Abbr_HOLLOW_CHARGE",
    "Abbr_HIGH_EXPLOSIVE",
    # The only consumable strings the source table really carries: the names
    # of the four national rations, their shared description and the three
    # battle refusals. Everything else stays a raw key, as in the source.
    "consumables:generic/cant_use",
    "consumables:generic/already_in_use",
    "consumables:generic/on_cooldown",
    "consumables:ration/desc",
    "consumables:ration_ger/name",
    "consumables:ration_usa/name",
    "consumables:ration_gb/name",
    "consumables:ration_jp/name",
)

REGION_RE = re.compile(r"region\s*=\s*Rect2\(([^)]*)\)")
SHEET_RE = re.compile(r'path="res://([^"]+\.png)"')


def read_icon(rel: str) -> dict[str, object]:
    path = GODOT_ROOT / "assets/gfx/ui" / rel
    text = path.read_text(encoding="utf-8")
    region = REGION_RE.search(text)
    sheet = SHEET_RE.search(text)
    if not region or not sheet:
        raise SystemExit(f"{rel}: not an AtlasTexture with a region")
    x, y, w, h = (round(float(v)) for v in region.group(1).split(","))
    name = Path(sheet.group(1)).name
    return {"texture": Path(name).stem, "x": x, "y": y, "w": w, "h": h}


def png_size(path: Path) -> tuple[int, int]:
    """Width and height out of the IHDR chunk, no Pillow needed."""
    header = path.read_bytes()[:24]
    if header[:8] != b"\x89PNG\r\n\x1a\n":
        raise SystemExit(f"{path}: not a png")
    return (
        int.from_bytes(header[16:20], "big"),
        int.from_bytes(header[20:24], "big"),
    )


def copy_sheets() -> dict[str, dict[str, int]]:
    sizes: dict[str, dict[str, int]] = {}
    for name, key in SHEETS.items():
        source = GODOT_ROOT / "assets/gfx/ui/inventory" / name
        if not source.is_file():
            raise SystemExit(f"missing source sheet {source}")
        target = TEXTURES / f"{key}.png"
        shutil.copyfile(source, target)
        width, height = png_size(target)
        sizes[key] = {"height": height, "width": width}
    return sizes


def copy_sounds() -> None:
    for name, rel in SOUNDS.items():
        source = GODOT_ROOT / "assets/sfx" / rel
        if not source.is_file():
            raise SystemExit(f"missing source sample {source}")
        shutil.copyfile(source, RESOURCES / "Audio" / name)


def load_strings() -> dict[str, tuple[str, str]]:
    table: dict[str, tuple[str, str]] = {}
    with (GODOT_ROOT / ".assets/lang.csv").open(encoding="utf-8", newline="") as raw:
        for row in csv.reader(raw):
            if len(row) >= 3 and row[0] in STRING_KEYS:
                table[row[0]] = (row[1], row[2])
    missing = [key for key in STRING_KEYS if key not in table]
    if missing:
        raise SystemExit(f"source translations missing: {missing}")
    return table


def main() -> None:
    sheets = copy_sheets()
    copy_sounds()

    atlas = json.loads(ATLAS.read_text(encoding="utf-8"))
    for key, size in sheets.items():
        atlas["textures"][key] = size
    atlas["textures"] = dict(sorted(atlas["textures"].items()))
    for key, rel in ICONS.items():
        entry = read_icon(rel)
        if key in BORDERS:
            entry["border"] = BORDERS[key]
        atlas["regions"][key] = entry
    atlas["regions"] = dict(sorted(atlas["regions"].items()))
    ATLAS.write_text(
        json.dumps(atlas, indent=2, ensure_ascii=False, sort_keys=False) + "\n",
        encoding="utf-8",
    )

    lang = json.loads(LANG.read_text(encoding="utf-8"))
    for key, (en, ru) in load_strings().items():
        lang["keys"][key] = {"en": en, "ru": ru}
    lang["keys"] = dict(sorted(lang["keys"].items()))
    LANG.write_text(
        json.dumps(lang, indent=2, ensure_ascii=False, sort_keys=False) + "\n",
        encoding="utf-8",
    )

    print(
        f"ported {len(ICONS)} icons, {len(SOUNDS)} samples "
        f"and {len(STRING_KEYS)} strings"
    )


if __name__ == "__main__":
    main()
