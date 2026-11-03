#!/usr/bin/env python3
"""Port every authored small tank silhouette used by battle team lists.

Blitz 4.8 is authoritative for the legacy roster.  Its per-tank descriptors
point into the shared Gfx2 SmallTankIcons sheet, so this exporter crops those
exact authored rectangles without resizing.  The additive 11.20 vehicles do
not exist in 4.8; only those fall back to their authored packed WEBP payloads.

Outputs are individual Resources textures.  Loading only the fourteen icons in
a battle is substantially cheaper than uploading the client's complete atlas,
and the shared atlas table keeps the runtime lookup identical for the map intro
and the in-battle scoreboard.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import re
import sys
from pathlib import Path, PurePosixPath
from xml.etree import ElementTree as ET

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "Assets/Blitz/Resources/Data"
TEXTURES = ROOT / "Assets/Blitz/Resources/Textures"
OUTPUT = TEXTURES / "SmallTankIcons"
TANKS = DATA / "tanks.json"
ATLAS = DATA / "atlas_regions.json"
MANIFEST = DATA / "small_tank_icons_manifest.json"
HANGAR_MANIFEST = DATA / "hangar_source_manifest.json"

PACK_480 = Path(os.environ.get(
    "BLITZ_480_PACK", str(Path.home() / "projects/wotblitz4.8.0files")))
PACK_1120 = Path(os.environ.get(
    "BLITZ_1120_PACK", str(Path.home() / "projects/wotblitz11.20.0files")))
GODOT = Path(os.environ.get(
    "BLITZ_GODOT_ROOT", str(ROOT.parent / "blitz-legacy-godot")))
sys.path.insert(0, str(GODOT / "tools"))
import dvpl  # type: ignore  # noqa: E402

SMALL_480 = PACK_480 / "Gfx2/UI/BattleScreenHUD/SmallTankIcons"
SMALL_1120 = Path("Gfx/UI/BattleScreenHUD/SmallTankIcons")
INVENTORY_1120 = DATA / "blitz_1120_inventory.json"
META_TEMPLATE = TEXTURES / "flow_small_tanks.png.meta"


def sha256_bytes(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def sha256(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


def data_id(name: str) -> str:
    value = "".join(symbol.lower() for symbol in name if symbol.isalnum())
    return value or "ms1"


def stable_guid(relative: str) -> str:
    return hashlib.md5(
        ("blitz-legacy-small-tank-icon:" + relative).encode("utf-8"),
        usedforsecurity=False,
    ).hexdigest()


def source_file_1120(relative: Path) -> Path:
    direct = PACK_1120 / relative
    candidates = (
        direct.with_name(direct.name + ".dvpl"),
        direct,
        PACK_1120 / "from_apk" / relative,
        (PACK_1120 / "from_apk" / relative).with_name(relative.name + ".dvpl"),
    )
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise FileNotFoundError(str(relative))


def small_icon_stem_480(card: dict) -> str:
    source = PACK_480 / str(card.get("source", ""))
    if not source.is_file():
        raise FileNotFoundError(f"{card['name']}: missing 4.8 XML {source}")
    root = ET.parse(source).getroot()
    value = root.findtext(".//smallIconPath")
    if not value:
        raise RuntimeError(f"{card['name']}: 4.8 XML has no smallIconPath")
    return PurePosixPath(value.strip()).name


def descriptor_frame(path: Path) -> tuple[Path, tuple[int, int, int, int]]:
    lines = [
        line.strip()
        for line in path.read_text(encoding="utf-8-sig").splitlines()
        if line.strip()
    ]
    if len(lines) < 5 or int(lines[3]) != 1:
        raise RuntimeError(f"invalid single-frame descriptor: {path}")
    values = lines[4].split()
    if len(values) < 4:
        raise RuntimeError(f"invalid descriptor frame: {path}")
    x, y, width, height = (int(value) for value in values[:4])
    texture = Path(lines[1]).with_suffix(".mali.webp").name
    return path.parent / texture, (x, y, width, height)


def export_480(card: dict) -> tuple[Image.Image, dict] | None:
    # Additive 11.20 cards deliberately point at modern XML that does not carry
    # the old smallIconPath field.  They are the only legal fallback below.
    if str(card.get("source", "")).startswith("from_apk/"):
        return None
    stem = small_icon_stem_480(card)
    descriptor = SMALL_480 / (stem + ".txt")
    if not descriptor.is_file():
        return None
    sheet, crop = descriptor_frame(descriptor)
    if not sheet.is_file():
        raise FileNotFoundError(str(sheet))
    x, y, width, height = crop
    with Image.open(sheet) as source:
        image = source.convert("RGBA").crop((x, y, x + width, y + height))
    if image.getchannel("A").getbbox() is None:
        raise RuntimeError(f"{card['name']}: empty 4.8 icon crop")
    evidence = {
        "version": "4.8.0",
        "smallIconPath": stem,
        "descriptor": str(descriptor.relative_to(PACK_480)),
        "descriptorSha256": sha256(descriptor),
        "source": str(sheet.relative_to(PACK_480)),
        "sourceSha256": sha256(sheet),
        "crop": {"x": x, "y": y, "w": width, "h": height},
    }
    return image, evidence


def inventory_1120() -> dict[str, dict]:
    records = json.loads(INVENTORY_1120.read_text(encoding="utf-8"))["records"]
    german = DATA / "blitz_germany_1120_inventory.json"
    if german.exists():
        records += json.loads(german.read_text(encoding="utf-8"))["records"]
    usa = DATA / "blitz_usa_1120_inventory.json"
    if usa.exists():
        records += json.loads(usa.read_text(encoding="utf-8"))["records"]
    other = DATA / "blitz_other_1120_inventory.json"
    if other.exists():
        records += json.loads(other.read_text(encoding="utf-8"))["records"]
    japan = DATA / "blitz_japan_1120_inventory.json"
    if japan.exists():
        records += json.loads(japan.read_text(encoding="utf-8"))["records"]
    branches = DATA / "blitz_branches_1120_inventory.json"
    if branches.exists():
        records += json.loads(branches.read_text(encoding="utf-8"))["records"]
    ussr = DATA / "blitz_ussr_1120_inventory.json"
    if ussr.exists():
        records += json.loads(ussr.read_text(encoding="utf-8"))["records"]
    mark_i_male = DATA / "blitz_mark_i_male_1120_inventory.json"
    if mark_i_male.exists():
        records += json.loads(mark_i_male.read_text(encoding="utf-8"))["records"]
    result = {str(record["name"]): record for record in records}
    if len(result) != len(records):
        raise RuntimeError("duplicate additive 11.20 icon record")
    return result


def export_1120(card: dict, records: dict[str, dict]) -> tuple[Image.Image, dict]:
    record = records.get(str(card["name"]))
    if record is None:
        raise RuntimeError(
            f"{card['name']}: absent from 4.8 and not an additive 11.20 vehicle")
    vehicle = str(record["vehicle_id"]).replace("S04_Lago-I", "S04_Lago_I")
    stem = f"{record['nation']}-{vehicle}"
    authored = record.get("resources", {}).get("smallIconPath")
    base_path = Path(authored.removeprefix("~res:/")) if authored else SMALL_1120 / stem
    if authored:
        stem = base_path.name
    try:
        source = source_file_1120(Path(str(base_path) + "@2x.packed.webp"))
    except FileNotFoundError:
        source = source_file_1120(Path(str(base_path) + ".packed.webp"))
    payload = source.read_bytes()
    decoded = dvpl.unpack_bytes(payload) if source.suffix == ".dvpl" else payload
    with Image.open(io.BytesIO(decoded)) as packed:
        rgba = packed.convert("RGBA")
    bbox = rgba.getchannel("A").getbbox()
    if bbox is None:
        raise RuntimeError(f"{card['name']}: empty 11.20 icon")
    image = rgba.crop(bbox)
    evidence = {
        "version": "11.20",
        "smallIconPath": stem,
        "source": str(source.relative_to(PACK_1120)),
        "sourceSha256": sha256(source),
        "crop": {
            "x": bbox[0], "y": bbox[1],
            "w": bbox[2] - bbox[0], "h": bbox[3] - bbox[1],
        },
    }
    return image, evidence


def write_texture_meta(path: Path) -> None:
    if path.exists():
        return
    template = META_TEMPLATE.read_text(encoding="utf-8")
    relative = str(path.relative_to(ROOT)).replace(os.sep, "/")
    template = re.sub(
        r"(?m)^guid: [0-9a-f]+$", "guid: " + stable_guid(relative), template, count=1)
    path.write_text(template, encoding="utf-8")


def write_folder_meta() -> None:
    path = OUTPUT.with_suffix(".meta")
    if path.exists():
        return
    relative = str(OUTPUT.relative_to(ROOT)).replace(os.sep, "/")
    path.write_text(
        "fileFormatVersion: 2\n"
        f"guid: {stable_guid(relative)}\n"
        "folderAsset: yes\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n",
        encoding="utf-8",
    )


def update_atlas(entries: list[dict]) -> None:
    table = json.loads(ATLAS.read_text(encoding="utf-8"))
    table["textures"] = {
        key: value for key, value in table["textures"].items()
        if not key.startswith("SmallTankIcons/")
    }
    # Only this tool's regions: an event card's own icon (World of Ducks,
    # port_world_of_ducks.py atlas) lives outside SmallTankIcons and stays.
    table["regions"] = {
        key: value for key, value in table["regions"].items()
        if not (key.startswith("flow_tank_") and key.endswith("_small")
                and value.get("texture", "").startswith("SmallTankIcons/"))
    }
    for entry in entries:
        texture = "SmallTankIcons/" + entry["dataId"]
        region = "flow_tank_" + entry["dataId"] + "_small"
        table["textures"][texture] = {
            "width": entry["width"], "height": entry["height"]}
        table["regions"][region] = {
            "texture": texture, "x": 0, "y": 0,
            "w": entry["width"], "h": entry["height"],
        }
    table["textures"] = dict(sorted(table["textures"].items()))
    table["regions"] = dict(sorted(table["regions"].items()))
    ATLAS.write_text(
        json.dumps(table, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    if HANGAR_MANIFEST.exists():
        manifest = json.loads(HANGAR_MANIFEST.read_text(encoding="utf-8"))
        manifest["atlas"]["textures"] = len(table["textures"])
        manifest["atlas"]["regions"] = len(table["regions"])
        HANGAR_MANIFEST.write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )


def main() -> None:
    cards = json.loads(TANKS.read_text(encoding="utf-8"))["tanks"]
    modern = inventory_1120()
    OUTPUT.mkdir(parents=True, exist_ok=True)
    write_folder_meta()

    entries: list[dict] = []
    expected_files: set[str] = set()
    ids: set[str] = set()
    for card in cards:
        icon_id = data_id(str(card["name"]))
        if icon_id in ids:
            raise RuntimeError(f"duplicate icon id: {icon_id}")
        ids.add(icon_id)

        exported = export_480(card)
        if exported is None:
            image, evidence = export_1120(card, modern)
        else:
            image, evidence = exported

        destination = OUTPUT / (icon_id + ".png")
        image.save(destination, format="PNG", optimize=False, compress_level=9)
        write_texture_meta(destination.with_suffix(".png.meta"))
        expected_files.update((destination.name, destination.name + ".meta"))

        entry = {
            "tank": card["name"],
            "dataId": icon_id,
            "width": image.width,
            "height": image.height,
            "outputSha256": sha256(destination),
        }
        entry.update(evidence)
        entries.append(entry)

    for stale in OUTPUT.iterdir():
        if stale.is_file() and stale.name not in expected_files:
            stale.unlink()

    counts = {
        "4.8.0": sum(entry["version"] == "4.8.0" for entry in entries),
        "11.20": sum(entry["version"] == "11.20" for entry in entries),
    }
    if len(entries) != len(cards) or counts != {"4.8.0": 342, "11.20": len(modern)}:
        raise RuntimeError(
            f"unexpected coverage: cards={len(cards)} entries={len(entries)} {counts}")

    update_atlas(entries)
    MANIFEST.write_text(
        json.dumps({
            "generator": "Tools/port_blitz_small_tank_icons.py",
            "policy": (
                "Blitz 4.8 Gfx2 descriptor crops first; exact Blitz 11.20 "
                "@2x alpha crop only when the vehicle is absent from 4.8; no resampling"
            ),
            "counts": counts,
            "icons": entries,
        }, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(json.dumps({
        "icons": len(entries),
        "from480": counts["4.8.0"],
        "from1120": counts["11.20"],
    }, sort_keys=True))


if __name__ == "__main__":
    main()
