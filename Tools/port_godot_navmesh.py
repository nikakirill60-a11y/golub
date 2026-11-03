#!/usr/bin/env python3
"""Port the baked navigation mesh of the Godot maps into a Unity resource.

The source drives its bots with NavigationServer3D against a navmesh baked
into the map scene: maps/rudniki/_rudniki.tscn carries a NavigationRegion3D
holding maps/rudniki/navmesh.res, baked from the navmesh_obstacle group with
cell_size 0.5, agent_radius 3.5, agent_max_climb 0.5 and agent_max_slope 25.
The port had none of that data, so AiTankController clamped its goals to the
play area and steered straight at them - which is why bots drove into the lake
and then ground against slopes the source navmesh never contained.

Nothing is invented here. Tools/godot_navmesh_dump.gd reads the live
NavigationMesh resources inside headless Godot, and this script packs them
verbatim, in Godot's right-handed space, into a blob the runtime mirrors
through GodotSpace like every other ported surface.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import struct
import subprocess
from pathlib import Path

DUMP_SCRIPT = "godot_navmesh_dump.gd"
TEMP_SCRIPT = "__blitz_navmesh_dump.gd"
MAGIC = b"BLNM"
VERSION = 1
MAPS = ("rudniki",)

DEFAULT_GODOT = "/usr/bin/godot"
DEFAULT_SOURCE = Path("/home/smh01/Studio canaryProjects/Blitz Legacy")


def run_dump(godot: str, godot_root: Path, unity_root: Path, out: Path) -> None:
    """Runs the dump inside the source project and leaves it pristine again."""
    script = unity_root / "Tools" / DUMP_SCRIPT
    if not script.is_file():
        raise SystemExit(f"missing dump script {script}")
    if not godot_root.is_dir():
        raise SystemExit(f"missing source project {godot_root}")
    out.mkdir(parents=True, exist_ok=True)
    temp = godot_root / TEMP_SCRIPT
    shutil.copyfile(script, temp)
    try:
        result = subprocess.run(
            [
                godot,
                "--headless",
                "--path",
                str(godot_root),
                "--script",
                f"res://{TEMP_SCRIPT}",
            ],
            env=dict(os.environ, BLITZ_NAVMESH_OUT=str(out)),
            capture_output=True,
            text=True,
            timeout=900,
            check=False,
        )
    finally:
        temp.unlink(missing_ok=True)
    for key in MAPS:
        if not (out / f"navmesh_{key}.json").is_file():
            raise SystemExit(
                f"the navmesh dump produced no navmesh_{key}.json\n"
                f"{result.stdout[-2000:]}\n{result.stderr[-2000:]}"
            )


def pack(payload: dict) -> tuple[bytes, int, int]:
    """Packs every enabled region as vertices plus convex polygon indices."""
    regions = [r for r in payload["regions"] if r.get("enabled", True)]
    if not regions:
        raise SystemExit(f"{payload['map']} has no enabled navigation region")
    blob = bytearray()
    blob += MAGIC
    blob += struct.pack("<ii", VERSION, len(regions))
    total_vertices = 0
    total_polygons = 0
    for region in regions:
        vertices = region["vertices"]
        if len(vertices) % 3:
            raise SystemExit(f"region {region['name']} has a ragged vertex stream")
        count = len(vertices) // 3
        polygons = region["polygons"]
        if not count or not polygons:
            raise SystemExit(f"region {region['name']} is empty")
        blob += struct.pack(
            "<6f",
            region["cell_size"],
            region["cell_height"],
            region["agent_radius"],
            region["agent_height"],
            region["agent_max_climb"],
            region["agent_max_slope"],
        )
        blob += struct.pack("<i", count)
        blob += struct.pack(f"<{len(vertices)}f", *vertices)
        blob += struct.pack("<i", len(polygons))
        for polygon in polygons:
            if len(polygon) < 3:
                raise SystemExit(f"region {region['name']} has a degenerate polygon")
            if min(polygon) < 0 or max(polygon) >= count:
                raise SystemExit(f"region {region['name']} indexes past its vertices")
            blob += struct.pack("<i", len(polygon))
            blob += struct.pack(f"<{len(polygon)}i", *polygon)
        total_vertices += count
        total_polygons += len(polygons)
    return bytes(blob), total_vertices, total_polygons


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=DEFAULT_GODOT)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--dump", type=Path, default=Path("/tmp/blitz_navmesh"))
    parser.add_argument(
        "--skip-dump",
        action="store_true",
        help="reuse an existing dump instead of running headless Godot",
    )
    args = parser.parse_args()
    unity_root = Path(__file__).resolve().parents[1]
    if not args.skip_dump:
        run_dump(args.godot, args.source, unity_root, args.dump)
    target_dir = unity_root / "Assets" / "Blitz" / "Resources" / "Data"
    target_dir.mkdir(parents=True, exist_ok=True)
    for key in MAPS:
        source = args.dump / f"navmesh_{key}.json"
        payload = json.loads(source.read_text(encoding="utf-8"))
        blob, vertices, polygons = pack(payload)
        target = target_dir / f"navmesh_{key}.bytes"
        target.write_bytes(blob)
        print(
            f"{target.relative_to(unity_root)}: {vertices} vertices, "
            f"{polygons} polygons, {len(blob)} bytes"
        )


if __name__ == "__main__":
    main()
