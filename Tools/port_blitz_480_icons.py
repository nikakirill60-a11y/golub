#!/usr/bin/env python3
"""Export the exact Blitz 4.8 carousel icon for every researchable tank.

The client stores one high-resolution sprite per vehicle in
Gfx2/UI/BigTankIcons.  Each descriptor supplies the authored frame rectangle;
only that rectangle is copied to the Unity PNG so HangarScreen can keep using
the original carousel_tank.tscn KEEP_CENTERED layout.
"""
from __future__ import annotations

import difflib
import hashlib
import json
import re
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
PACK = Path.home() / "Studio canaryProjects" / "BlitzPACKSold"
SOURCE = PACK / "Gfx2/UI/BigTankIcons"
DATA = ROOT / "Assets/Blitz/Resources/Data"
OUTPUT = ROOT / "Assets/Blitz/Resources/Textures/TankIcons"
TANKS = DATA / "tanks.json"
ATLAS = DATA / "atlas_regions.json"
MANIFEST = DATA / "tank_icons_480_manifest.json"
HANGAR_MANIFEST = DATA / "hangar_source_manifest.json"

PREFIXES = {
    1: ("ussr-",),
    2: ("germany-", "germany_"),
    3: ("usa-",),
    4: ("britsh-", "british-"),
    5: ("japan-",),
    6: ("france-",),
    7: ("china-",),
    8: ("other-",),
}

# Historical icon names predate several XML IDs and contain client typos.
# TankInfoMain.nation_flags: the four original branches use dedicated
# lobbyCommon plates; Japan/France/China use the exact TankSlotFlags frames.
CAROUSEL_NATION_REGIONS = {
    1: ("lobbyCommon_Out", 1131, 269, 354, 231),
    2: ("lobbyCommon_Out", 1591, 251, 354, 231),
    3: ("lobbyCommon_Out", 1, 515, 354, 231),
    4: ("lobbyCommon_Out", 1, 748, 354, 228),
    5: ("flags", 356, 0, 354, 231),
    6: ("flags", 356, 466, 354, 231),
    7: ("flags", 356, 233, 354, 231),
    # Nation 8 is the client's "other" bucket (Sega, Warhammer, Hellsing,
    # Dracula, the loose one-offs). Godot loaded it as nation_flags[0] ->
    # flags/TankSlotFlags.tres, the only unclaimed 354x231 frame in flags.png.
    8: ("flags", 356, 699, 354, 231),
}

ALIASES = {
    "indien_panzer": "germany-Indian_Pz",
    "t1_cunningham": "usa-T1",
    "t2_med": "usa-T2_MT",
    "m2_med": "usa-M2_MT",
    "t1_hvy": "usa-T1Heavy",
    "m18_hellcat": "usa-Hellcat",
    "a100_t49": "usa-A100_T49",
    # The tier 1 tree card is the Vickers Medium Mk I; `britsh-Mark_I` is the
    # sprite of the premium Mark I that shares the shortened client name.
    "gb01_medium_mark_i": "britsh-Medium_I",
    "gb_mark_i": "britsh-Mark_I",
    "gb_churchill_mkvi": "britsh_Churchill_MkVI",
    "gb76_mk_vic": "british-Mk_VIc",
    "gb05_vickers_medium_mk_ii": "britsh-Medium_II",
    "gb06_vickers_medium_mk_iii": "britsh-Medium_III",
    "gb40_gun_carrier_churchill": "britsh-Churchill_GC",
    "gb24_centurion_mk3": "britsh-Centurion_7-1",
    "gb70_fv4202_105": "britsh-FV4202",
    "gb58_cruiser_mk_iii": "britsh-Cruiser_III",
    "gb59_cruiser_mk_iv": "britsh-Cruiser_IV",
    # Crossover and event machines: the icon names follow the marketing
    # spelling, not the vehicle id, and the Warhammer tanks ship one sprite per
    # chapter. The port keeps the default chapter, matching the scene texture.
    "oth08_wh_vindicator": "other-Oth08_Vindicator",
    "oth09_wh_predator": "other-Oth09_WH_Predator_Ultramarines",
    "oth06_sega_lupus": "other-Sega_Lupus",
    "strv_74a2": "other-Strv_74_A2",
    # Lend-lease, event and crossover sprites the client names after the
    # historical vehicle instead of the XML id (some with client typos).
    "matilda_ii_ll": "ussr-Matilda-IV",
    "churchill_ll": "ussr-Curchill_III",
    "s01_frankentank": "Frankentank_event",
    "t7_combat_car": "usa-T7-cc",
    "gb68_matilda_black_prince": "britsh-MatildaBP",
    "f68_amx_chasseur_de_char_46": "france-CDC",
    "ch23_112": "china-112_event",
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def normalize(value: str) -> str:
    return re.sub(r"[^a-z0-9]", "", value.lower())


def resource_id(name: str) -> str:
    return "".join(char.lower() for char in name if char.isalnum()) or "ms1"


def match_names(card: dict) -> set[str]:
    values = [str(card["name"]), Path(str(card.get("model_path", ""))).stem]
    for value in list(values):
        values.append(re.sub(r"^(gb|ch|a|g|r|s|oth|f|h|t)\d+[_-]*", "", value, flags=re.I))
    # Lend-lease and captured machines reuse the sprite of the donor vehicle,
    # so the icon name carries no `_LL` / `_captured` suffix.
    for value in list(values):
        values.append(re.sub(r"_(ll|captured)$", "", value, flags=re.I))
    return {normalize(value) for value in values if value}


def source_stem(card: dict, stems: list[str]) -> str:
    name = str(card["name"])
    if name in ALIASES:
        return ALIASES[name]

    prefixes = PREFIXES[int(card["nation"])]
    candidates = [stem for stem in stems if stem.lower().startswith(prefixes)]
    expected = match_names(card)
    ranked: list[tuple[float, str]] = []
    for stem in candidates:
        tail = stem.split("-", 1)[-1] if "-" in stem else stem.split("_", 1)[-1]
        candidate = normalize(tail)
        score = max(
            difflib.SequenceMatcher(None, value, candidate).ratio()
            for value in expected
        )
        if candidate in expected:
            score = 1.0
        ranked.append((score, stem))
    ranked.sort(reverse=True)
    if not ranked or ranked[0][0] < 0.9:
        raise RuntimeError(f"No unambiguous carousel icon for {name}: {ranked[:3]}")
    return ranked[0][1]


def frame(descriptor: Path) -> tuple[int, int, int, int]:
    lines = [line.strip() for line in descriptor.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    if len(lines) < 5 or int(lines[3]) < 1:
        raise RuntimeError(f"Invalid icon descriptor: {descriptor}")
    values = lines[4].split()
    if len(values) < 4:
        raise RuntimeError(f"Invalid icon frame: {descriptor}")
    return tuple(int(value) for value in values[:4])


def main() -> None:
    cards = json.loads(TANKS.read_text(encoding="utf-8"))["tanks"]
    stems = sorted(path.stem for path in SOURCE.glob("*.txt"))
    output_names: set[str] = set()
    source_names: set[str] = set()
    entries = []
    atlas = json.loads(ATLAS.read_text(encoding="utf-8"))
    OUTPUT.mkdir(parents=True, exist_ok=True)

    for nation, (texture, x, y, width, height) in CAROUSEL_NATION_REGIONS.items():
        atlas["regions"][f"tank_nation_{nation}"] = {
            "texture": texture, "x": x, "y": y, "w": width, "h": height,
        }
    # The original atlas already carries selector flags 1..5. The complete
    # roster adds the two source frames that were never reachable before.
    for nation in (6, 7):
        texture, x, y, width, height = CAROUSEL_NATION_REGIONS[nation]
        atlas["regions"][f"hangar_nation{nation}"] = {
            "texture": texture, "x": x, "y": y, "w": width, "h": height,
        }

    for card in cards:
        stem = source_stem(card, stems)
        if stem in source_names:
            raise RuntimeError(f"Carousel icon reused unexpectedly: {stem}")
        source_names.add(stem)

        descriptor = SOURCE / f"{stem}.txt"
        image_source = SOURCE / f"{stem}0.mali.webp"
        if not descriptor.exists() or not image_source.exists():
            raise RuntimeError(f"Missing client icon payload: {stem}")

        x, y, width, height = frame(descriptor)
        data_id = resource_id(str(card["name"]))
        if data_id in output_names:
            raise RuntimeError(f"Duplicate Unity icon id: {data_id}")
        output_names.add(data_id)

        destination = OUTPUT / f"{data_id}.png"
        with Image.open(image_source) as image:
            crop = image.convert("RGBA").crop((x, y, x + width, y + height))
            crop.save(destination, format="PNG", optimize=False)

        texture_name = f"TankIcons/{data_id}"
        region_name = f"tank_icon_{data_id}"
        atlas["textures"][texture_name] = {"width": width, "height": height}
        atlas["regions"][region_name] = {
            "texture": texture_name,
            "x": 0,
            "y": 0,
            "w": width,
            "h": height,
        }
        entries.append({
            "tank": card["name"],
            "dataId": data_id,
            "descriptor": str(descriptor.relative_to(PACK)),
            "descriptorSha256": sha256(descriptor),
            "source": str(image_source.relative_to(PACK)),
            "sourceSha256": sha256(image_source),
            "width": width,
            "height": height,
        })

    expected = len(cards)
    if len(entries) != expected:
        raise RuntimeError(f"Expected {expected} carousel icons, exported {len(entries)}")

    ATLAS.write_text(json.dumps(atlas, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    MANIFEST.write_text(json.dumps({
        "version": "4.8.0",
        "source": str(SOURCE.relative_to(PACK)),
        "icons": entries,
    }, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    # HangarSliceTests intentionally compare the shared table with the manifest
    # that first created it. Keep those aggregate counts current when this
    # second source-backed exporter extends the same atlas registry.
    if HANGAR_MANIFEST.exists():
        hangar_manifest = json.loads(HANGAR_MANIFEST.read_text(encoding="utf-8"))
        hangar_manifest["atlas"] = {
            "textures": len(atlas["textures"]),
            "regions": len(atlas["regions"]),
        }
        HANGAR_MANIFEST.write_text(
            json.dumps(hangar_manifest, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )

    print(json.dumps({"icons": len(entries), "atlasRegions": len(atlas["regions"])}, sort_keys=True))


if __name__ == "__main__":
    main()
