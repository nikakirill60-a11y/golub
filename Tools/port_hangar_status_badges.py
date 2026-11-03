#!/usr/bin/env python3
"""Derive the tech tree, collector and mod status badges from the premium badge.

ui_hangar.tscn draws the premium filter toggle with hangar_premium, the gold
wreath of lobbyCommon_Out, but the collector toggle with Blitz's flat
icon_tank-collectible, a smaller picture of another kind, and 4.8 had neither
a toggle for the tech tree's own vehicles nor mod vehicles at all. The port
gives the tech tree, collector and mod toggles the premium wreath itself in
the colour their vehicles' names are painted in (UiTheme.Label's white,
db_tanks.gd COLOR_TANK_COLLECTOR and HangarUiKit.TankNameMod), so the four
status toggles share one size and one look.

The recolour is a luminance gradient map: a pixel exactly as bright as the
premium gold takes the target colour, darker pixels scale it towards black
and brighter ones blend it towards white. Alpha is copied unchanged. Nothing
is painted by hand, and rerunning the tool reproduces the same bytes.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "Assets/Blitz/Resources/Data"
TEXTURES = ROOT / "Assets/Blitz/Resources/Textures"
ATLAS = DATA / "atlas_regions.json"
MANIFEST = DATA / "hangar_status_badges_manifest.json"
HANGAR_MANIFEST = DATA / "hangar_source_manifest.json"

SOURCE_REGION = "hangar_premium"

# Rec. 601 luma, the weights the gradient map ranks pixels by.
LUMA = np.array([0.299, 0.587, 0.114])

# db_tanks.gd COLOR_TANK_PREM: the gold the wreath's midtones are drawn in.
PREMIUM = (1.0, 0.777, 0.103)

BADGES = {
    # UiTheme.Label: the white a tech tree vehicle's name is painted in.
    "hangar_regular_badge": (1.0, 1.0, 1.0),
    # db_tanks.gd COLOR_TANK_COLLECTOR.
    "hangar_collector_badge": (0.309, 0.765, 0.969),
    # HangarUiKit.TankNameMod: the collector treatment in light green.
    "hangar_mod_badge": (0.639, 0.890, 0.608),
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def gradient_map(pixels: np.ndarray, target: tuple[float, float, float]) -> np.ndarray:
    """Recolour an RGBA array so the premium gold becomes <target>."""
    rgb = pixels[..., :3].astype(np.float64) / 255.0
    luma = (rgb @ LUMA)[..., None]
    anchor = float(np.array(PREMIUM) @ LUMA)
    colour = np.array(target, dtype=np.float64)
    darker = colour * (luma / anchor)
    lighter = colour + ((1.0 - colour) * ((luma - anchor) / (1.0 - anchor)))
    mapped = np.where(luma <= anchor, darker, lighter)
    mapped = np.rint(np.clip(mapped, 0.0, 1.0) * 255.0).astype(np.uint8)
    return np.concatenate([mapped, pixels[..., 3:4]], axis=-1)


def main() -> None:
    atlas = json.loads(ATLAS.read_text(encoding="utf-8"))
    region = atlas["regions"][SOURCE_REGION]
    sheet = TEXTURES / (region["texture"] + ".png")
    x, y, w, h = region["x"], region["y"], region["w"], region["h"]
    with Image.open(sheet) as image:
        pixels = np.asarray(image.convert("RGBA").crop((x, y, x + w, y + h)))

    outputs = []
    for name, colour in BADGES.items():
        destination = TEXTURES / (name + ".png")
        Image.fromarray(gradient_map(pixels, colour), "RGBA").save(destination, format="PNG")
        atlas["textures"][name] = {"width": w, "height": h}
        atlas["regions"][name] = {"texture": name, "x": 0, "y": 0, "w": w, "h": h}
        outputs.append({
            "region": name,
            "destination": destination.name,
            "colour": list(colour),
            "sha256": sha256(destination),
        })

    ATLAS.write_text(json.dumps(atlas, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    manifest = {
        "generator": "Tools/port_hangar_status_badges.py",
        "policy": "Derived from the premium badge by a luminance gradient map; alpha unchanged; "
                  "no hand-painted art.",
        "source": {
            "region": SOURCE_REGION,
            "texture": sheet.name,
            "rect": [x, y, w, h],
            "sha256": sha256(sheet),
            "anchor": list(PREMIUM),
        },
        "badges": outputs,
    }
    MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    # The hangar manifest pins the totals of the shared atlas table.
    hangar = json.loads(HANGAR_MANIFEST.read_text(encoding="utf-8"))
    hangar["atlas"]["textures"] = len(atlas["textures"])
    hangar["atlas"]["regions"] = len(atlas["regions"])
    HANGAR_MANIFEST.write_text(json.dumps(hangar, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(json.dumps(outputs, indent=2))


if __name__ == "__main__":
    main()
