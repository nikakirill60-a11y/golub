#!/usr/bin/env python3
"""Compare the Godot reference frames of the combat effects with the Unity port.

Godot:  Tools/godot_combat_fx_render.gd  -> $GODOT_DIR (default /tmp/blitz_combat_fx_render)
Unity:  Tools/unity.sh combat-fx-render  -> Logs/combat-fx-render

Prints, per frame, the mean absolute difference (0-255), the share of pixels
that differ by more than the threshold and the bounding boxes of what each
renderer drew over the background; writes side-by-side sheets (Godot left,
Unity right) to Logs/combat-fx-compare/.
"""
import os
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageStat

ROOT = Path(__file__).resolve().parent.parent
GODOT_DIR = Path(os.environ.get("GODOT_DIR", "/tmp/blitz_combat_fx_render"))
UNITY_DIR = ROOT / "Logs" / "combat-fx-render"
OUT_DIR = ROOT / "Logs" / "combat-fx-compare"
BACKGROUND = (89, 102, 115)
THRESHOLD = int(os.environ.get("THRESHOLD", "12"))


def drawn_box(image):
    background = Image.new("RGB", image.size, BACKGROUND)
    mask = ImageChops.difference(image, background).convert("L").point(lambda v: 255 if v > 4 else 0)
    return mask.getbbox()


def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    names = sorted(p.name for p in GODOT_DIR.glob("*.png"))
    if not names:
        print(f"no Godot frames in {GODOT_DIR}", file=sys.stderr)
        return 1

    worst = 0.0
    for name in names:
        unity_path = UNITY_DIR / name
        if not unity_path.exists():
            print(f"{name}: missing in Unity output")
            worst = max(worst, 255.0)
            continue

        godot = Image.open(GODOT_DIR / name).convert("RGB")
        unity = Image.open(unity_path).convert("RGB")
        if unity.size != godot.size:
            unity = unity.resize(godot.size)
        diff = ImageChops.difference(godot, unity)
        mean = sum(ImageStat.Stat(diff).mean) / 3.0
        gray = diff.convert("L")
        hist = gray.histogram()
        changed = sum(hist[THRESHOLD + 1:]) / float(godot.size[0] * godot.size[1])
        worst = max(worst, mean)
        print(f"{name}: mean {mean:6.3f}  >{THRESHOLD}: {changed * 100:6.3f}%  "
              f"godot {drawn_box(godot)}  unity {drawn_box(unity)}")

        sheet = Image.new("RGB", (godot.size[0] * 2, godot.size[1]))
        sheet.paste(godot, (0, 0))
        sheet.paste(unity, (godot.size[0], 0))
        sheet.save(OUT_DIR / name)

    print(f"worst mean difference: {worst:.3f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
