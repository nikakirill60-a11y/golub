#!/usr/bin/env python3
"""Export the Blitz 4.8.0 key art, the backdrop the client's login/loading
screen draws behind the progress bar.

The client ships it in ``Data/Gfx2/UI/KeyArt`` as a 2048x2048 WebP page with
the authored frames listed in ``keyart.txt``:

    x y w h xoff yoff texture_index name

``frame0`` is the full 2048x1536 artwork; the other two are the cropped
variants the client uses for narrow phones, which the port does not need
because LoginScreen envelopes a single image over the canvas.

The frame is written to Resources/Textures/login_keyart.png and registered in
atlas_regions.json as ``login_keyart``, so LoginScreen keeps going through
AtlasRegionTable like every other sprite.
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
CLIENT = Path.home() / "projects" / "wotblitz4.8.0files"
SOURCE = CLIENT / "from_apk/Data/Gfx2/UI/KeyArt"
DATA = ROOT / "Assets/Blitz/Resources/Data"
OUTPUT = ROOT / "Assets/Blitz/Resources/Textures/login_keyart.png"
ATLAS = DATA / "atlas_regions.json"
REGION = "login_keyart"
TEXTURE = "login_keyart"
FRAME = "frame0"


def read_frames(descriptor: Path):
    """The client's .txt sprite descriptor: header, then one line per frame."""
    lines = [line.strip() for line in descriptor.read_text().splitlines()]
    lines = [line for line in lines if line]
    count = int(lines[0])
    pages = lines[1 : 1 + count]
    frames = {}
    for line in lines[3 + count :]:
        parts = line.split()
        if len(parts) < 8:
            continue
        x, y, w, h, _xoff, _yoff, page = (int(p) for p in parts[:7])
        frames[parts[7]] = (pages[page], x, y, w, h)
    return frames


def main() -> None:
    descriptor = SOURCE / "keyart.txt"
    if not descriptor.exists():
        raise SystemExit("4.8.0 client not found at " + str(SOURCE))

    frames = read_frames(descriptor)
    if FRAME not in frames:
        raise SystemExit("keyart.txt has no " + FRAME)

    page, x, y, w, h = frames[FRAME]
    image = Image.open(SOURCE / page.replace(".tex", ".mali.webp"))
    # The page carries an alpha channel the artwork never uses; a backdrop
    # that covers the whole canvas is opaque, and dropping it saves both the
    # PNG and a quarter of the runtime texture.
    art = image.convert("RGB").crop((x, y, x + w, y + h))

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    art.save(OUTPUT, optimize=True)

    atlas = json.loads(ATLAS.read_text())
    atlas["regions"][REGION] = {
        "h": art.height,
        "texture": TEXTURE,
        "w": art.width,
        "x": 0,
        "y": 0,
    }
    atlas["regions"] = dict(sorted(atlas["regions"].items()))
    atlas["textures"][TEXTURE] = {"height": art.height, "width": art.width}
    atlas["textures"] = dict(sorted(atlas["textures"].items()))
    ATLAS.write_text(json.dumps(atlas, indent=2, sort_keys=True) + "\n")

    # HangarSliceTests compares the manifest counters against these tables.
    subprocess.run(
        [sys.executable, str(Path(__file__).with_name("_sync_manifest_counts.py"))],
        check=True,
    )

    print("login_keyart: {}x{} from {}".format(art.width, art.height, page))


if __name__ == "__main__":
    main()
