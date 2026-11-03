#!/usr/bin/env python3
"""Fills the lang.json rows the port asked for but never shipped.

Every screen that reads one of these keys used to print the raw key, because
Godot's tr() and this port's LocalizationTable both echo an unknown key back.
The rows come from three sources, in this order of authority:

1. The Godot project's own CSV (`.assets/lang.csv`), copied verbatim - that is
   the same source `port_godot_controls_lang.py` reads.
2. The client's `Data/Strings/<lang>.yaml`, for the names Godot never
   translated (it dumped them as `<!MissingKey:...>` rows with the Russian
   column shifted, so the CSV cannot be used for them). `port_blitz_client_map.py`
   already treats these files as authoritative for map names.
3. Authored text, for the handful of strings that exist in neither source. The
   client writes its equipment numbers with `%(placeholder)` runs it fills at
   runtime; this port has no such formatter, so the authored rows keep the
   client's phrasing and spell out the factor that
   EquipmentCatalog/ConsumableCatalog actually applies.
"""
import csv
import json
import os
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
LANG = ROOT / "Assets/Blitz/Resources/Data/lang.json"
GODOT = pathlib.Path(
    os.environ.get("BLITZ_GODOT", pathlib.Path.home() / "projects/blitz-legacy-godot"))
PACK = pathlib.Path(
    os.environ.get("BLITZ_PACK", pathlib.Path.home() / "projects/wotblitz4.8.0files"))

# 1. Rows the Godot CSV already carries.
FROM_GODOT = [
    "LOGIN_CURRENT_AKK",
    "popup:NotEnoughGold",
    "popup:logout",
]

# 2. Rows the client string tables carry, as <lang.json key>: <client key>.
FROM_CLIENT = {
    "map:desert_train/name": "#maps:desert_train:02_desert_train_dt/02_desert_train_dt.sc2",
    "equipment:vent/name": "optionalDevices/ImprovedVentilation/Name",
    "equipment:aimStab/name": "optionalDevices/AimingStabilizer/Name",
    "equipment:aimDrive/name": "optionalDevices/EnhancedAimDrives/Name",
    "equipment:toolbox/name": "#artefacts:toolbox/name",
    "equipment:optics/name": "#artefacts:coatedOptics/name",
    "equipment:camonet/name": "#artefacts:camouflageNet/name",
    "equipment:stereoscope/name": "#artefacts:stereoscope/name",
    "consumables:repair_kit/name": "#artefacts:largeRepairkit/name",
    "consumables:repair_kit/desc": "#artefacts:largeRepairkit/descr",
    "consumables:berserk/name": "#artefacts:berserk/name",
    "consumables:afterburning/name": "#artefacts:afterburning/name",
    "consumables:fuel/name": "#artefacts:improved_fuel/name",
    "consumables:ration_ussr/name": "#artefacts:ration/name",
}

# 3. Authored rows. The equipment factors are the ones the port applies in
# EquipmentCatalog.Modifiers and ConsumableCatalog.Apply.
AUTHORED = {
    "hangar:autoreload_times": {"en": "Shell loading times (s)", "ru": "Дозарядка снарядов (с)"},
    # maps/_login_screen.tscn falls back to the untranslated literal "None";
    # the port shows the same slot in both languages instead.
    "LOGIN_NO_ACCOUNT": {"en": "None", "ru": "Нет"},
    # Godot had no turrets, so no source string exists for a gun that the
    # mounted turret cannot carry.
    "hangar:mod_needs_turret": {
        "en": "Install a turret that carries this gun first",
        "ru": "Сначала установите башню, в которую ставится это орудие",
    },
    "equipment:vent/desc": {
        "en": "-5% from gun reload time\n-5% from aiming time",
        "ru": "-5% от времени заряжания\n-5% от времени сведения",
    },
    "equipment:aimStab/desc": {
        "en": "-20% from dispersion during movement\nand turret traverse",
        "ru": "-20% от разброса при движении\nи повороте башни",
    },
    "equipment:aimDrive/desc": {
        "en": "-10% from aiming time",
        "ru": "-10% от времени сведения",
    },
    "equipment:optics/desc": {
        "en": "+10% to view range,\nup to a range of 500 m",
        "ru": "+10% к радиусу обзора,\nно не более 500 м",
    },
    # toolbox, camonet, stereoscope and spallliner are mounted but inert in
    # the source too, so their rows describe the item without promising a
    # number the game never applies.
    "equipment:toolbox/desc": {
        "en": "Speeds up the repair of damaged modules",
        "ru": "Ускоряет ремонт повреждённых модулей",
    },
    "equipment:camonet/desc": {
        "en": "Lowers the visibility of a stationary vehicle",
        "ru": "Снижает заметность неподвижной техники",
    },
    "equipment:stereoscope/desc": {
        "en": "Raises the view range of a stationary vehicle,\nup to a range of 500 m",
        "ru": "Увеличивает радиус обзора неподвижной техники,\nно не более 500 м",
    },
    "equipment:spallliner/name": {
        "en": "Spall Liner",
        "ru": "Противоосколочный подбой",
    },
    "equipment:spallliner/desc": {
        "en": "Raises armour protection from ramming\nand explosions",
        "ru": "Повышает защиту от повреждений\nпри взрывах и таране",
    },
    "consumables:berserk/desc": {
        "en": "+25% to loading speed for 30 seconds",
        "ru": "+25% к скорости перезарядки на 30 секунд",
    },
    "consumables:afterburning/desc": {
        "en": "+25% to engine power and +5 km/h\nto top speed for 30 seconds",
        "ru": "+25% к мощности двигателя и +5 км/ч\nк максимальной скорости на 30 секунд",
    },
    "consumables:fuel/desc": {
        "en": "+10% to engine power, hull traverse\nand turret traverse speed.\n"
              "Activated automatically.",
        "ru": "+10% к мощности двигателя, скорости поворота\nкорпуса и башни.\n"
              "Автоматически срабатывает в бою.",
    },
    # ration_ussr shares the generic ration wording, which the source keeps in
    # consumables:ration/desc for the copied national rations.
    "consumables:ration_ussr/desc": {
        "en": "+10% to crew skill.",
        "ru": "+10% к мастерству экипажа.",
    },
}


def godot_rows():
    path = GODOT / ".assets/lang.csv"
    with open(path, newline="", encoding="utf-8-sig") as handle:
        rows = list(csv.reader(handle))
    header = [column.strip().lower() for column in rows[0]]
    en, ru = header.index("en"), header.index("ru")
    table = {}
    for row in rows[1:]:
        if row and row[0] and row[0] not in table:
            table[row[0]] = {"en": row[en], "ru": row[ru]}
    return table


def client_rows(language):
    """Data/Strings/<lang>.yaml is a flat list of quoted "key": "value" pairs."""
    path = PACK / "from_apk/Data/Strings" / f"{language}.yaml"
    table = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line.startswith('"'):
            continue
        key, separator, value = line.partition('": ')
        if not separator:
            continue
        try:
            table[json.loads(key + '"')] = json.loads(value)
        except ValueError:
            continue
    return table


def main():
    table = json.loads(LANG.read_text(encoding="utf-8"))
    languages = table["languages"]
    keys = table["keys"]

    godot = godot_rows()
    client = {language: client_rows(language) for language in languages}

    added = {}
    for key in FROM_GODOT:
        if key not in godot:
            raise SystemExit(f"{key} is missing from the Godot CSV")
        added[key] = {language: godot[key][language] for language in languages}

    for key, source in FROM_CLIENT.items():
        entry = {}
        for language in languages:
            if source not in client[language]:
                raise SystemExit(f"client string {source} is missing from {language}.yaml")
            entry[language] = client[language][source]
        added[key] = entry

    for key, entry in AUTHORED.items():
        missing = [language for language in languages if not entry.get(language)]
        if missing:
            raise SystemExit(f"authored row {key} has no {missing} text")
        added[key] = {language: entry[language] for language in languages}

    # lang.json keeps the order every previous porter appended in, so only the
    # new rows move: each one lands in front of the first key that sorts after
    # it, which puts it beside its own family.
    written = 0
    for key, entry in sorted(added.items()):
        if keys.get(key) == entry:
            continue
        rebuilt = {}
        for existing, value in keys.items():
            if key not in rebuilt and key < existing:
                rebuilt[key] = entry
            rebuilt[existing] = value
        rebuilt.setdefault(key, entry)
        keys = rebuilt
        written += 1

    table["keys"] = keys
    # The resource is indented by two spaces, as every porter writes it, so it
    # is rewritten exactly that way.
    LANG.write_text(
        json.dumps(table, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {written} localization rows, {len(table['keys'])} total")
    return 0


if __name__ == "__main__":
    sys.exit(main())
