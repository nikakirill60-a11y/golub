#!/usr/bin/env python3
"""Port the baked lighting of the Godot scenes into deterministic Unity assets.

Both source scenes are lit by LightmapGI. Every hangar lamp and the map's sun
are STATIC, so their light and all of their shadows live in the baked atlases
rather than in a realtime rig; the map's sun does not even have shadows
enabled. The port used to approximate all of that with realtime lights and
three hand-measured calibration factors, which can only ever be a fit to a
reference screenshot. This script ports the bake itself: the atlases, the
per-instance UV rects and slices, the second UV channel those rects address,
and the exact Environment the source renders with.

Nothing is invented here. Every value is read out of the live resources by
Tools/godot_lighting_dump.gd running inside headless Godot, and is emitted
verbatim in Godot's right-handed space; the Unity runtime mirrors it through
GodotSpace like the rest of the port.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
from pathlib import Path

import numpy as np

DUMP_SCRIPT = "godot_lighting_dump.gd"
TEMP_SCRIPT = "__blitz_lighting_dump.gd"
MESH_MAGIC = b"BLTZLMS1"

# Both scenes draw their baked props with one source shader: a tint times an
# albedo texture, optionally plus an emissive texture, raised to a contrast
# exponent. The atlas carries the light alone, so a surface still needs that
# material rebuilt on the Unity side.
MAP_OBJECT_SHADER = "res://maps/components/map_object.gdshader"

# Where port_godot_hangar.py already copied the hangar textures. Nothing is
# guessed from a file name: a material reaching for anything else stops the
# port instead of rendering untextured.
HANGAR_TEXTURES = {
    "res://maps/hangar_low/1.mali_Out.png": "Models/Hangar/Textures/Hangar1",
    "res://maps/hangar_low/1.emissive.png": "Models/Hangar/Textures/Hangar1Emission",
    "res://maps/hangar_low/2.mali_Out.png": "Models/Hangar/Textures/Hangar2",
    "res://maps/hangar_low/3.mali_Out.png": "Models/Hangar/Textures/Hangar3",
}

SLICES_HORIZONTAL = re.compile(r"^slices/horizontal=(\d+)$", re.MULTILINE)
SLICES_VERTICAL = re.compile(r"^slices/vertical=(\d+)$", re.MULTILINE)

# Light3D stores its rig under these prefixes. Everything matching is carried
# over, so a source value can never be silently dropped.
LIGHT_PREFIXES = (
    "light_",
    "shadow_",
    "omni_",
    "spot_",
    "directional_",
    "distance_fade_",
)
LIGHT_KEYS = ("sky_mode",)

SCENES = {
    "hangar": {
        "scene": "maps/hangar_low.tscn",
        "atlas": "maps/hangar_low/hangar_low.exr",
        "environment": "maps/hangar_low/WE_low.tres",
        "atlas_asset": "Models/Hangar/Lightmaps/hangar_low.exr",
        "mesh_asset": "Models/Hangar/lightmap_meshes.bytes",
        "textures": HANGAR_TEXTURES,
    },
    "rudniki": {
        "scene": "maps/rudniki/_rudniki.tscn",
        "atlas": "maps/rudniki/_rudniki.exr",
        "environment": None,  # authored inline in the scene
        "atlas_asset": "Maps/Rudniki/Lightmaps/rudniki.exr",
        "mesh_asset": "Maps/Rudniki/lightmap_meshes.bytes",
        # The map props already carry their materials in the object table
        # Tools/port_godot_map.py writes.
        "textures": None,
    },
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def camel(name: str) -> str:
    head, *rest = name.split("_")
    return head + "".join(part[:1].upper() + part[1:] for part in rest)


def camel_all(properties: dict) -> dict:
    return {camel(name): value for name, value in properties.items()}


def camel_pick(properties: dict) -> dict:
    return {
        camel(name): value
        for name, value in properties.items()
        if name.startswith(LIGHT_PREFIXES) or name in LIGHT_KEYS
    }


def run_dump(godot: str, godot_root: Path, unity_root: Path, out: Path) -> None:
    """Runs the dump inside the source project and leaves it pristine again."""
    script = unity_root / "Tools" / DUMP_SCRIPT
    if not script.is_file():
        raise SystemExit(f"missing dump script {script}")
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
            env=dict(os.environ, BLITZ_LIGHTING_OUT=str(out)),
            capture_output=True,
            text=True,
            timeout=900,
            check=False,
        )
    finally:
        temp.unlink(missing_ok=True)
    for key in SCENES:
        if not (out / f"{key}.json").is_file():
            raise SystemExit(
                f"the lighting dump produced no {key}.json\n"
                f"{result.stdout[-2000:]}\n{result.stderr[-2000:]}"
            )


def surface_arrays(blob: bytes, entry: dict) -> tuple:
    """Reads one surface back out of the dump payload, streams in dump order."""
    count = entry["vertex_count"]
    for stream in ("normal_count", "uv_count", "uv2_count"):
        if entry[stream] != count:
            raise SystemExit(
                f"surface {entry['key']} has {entry[stream]} {stream} for {count} vertices"
            )
    if entry["index_count"] % 3 != 0:
        raise SystemExit(f"surface {entry['key']} is not triangulated")
    offset = entry["offset"]
    positions = np.frombuffer(blob, "<f4", 3 * count, offset).reshape(count, 3)
    offset += 12 * count
    normals = np.frombuffer(blob, "<f4", 3 * count, offset).reshape(count, 3)
    offset += 12 * count
    uv = np.frombuffer(blob, "<f4", 2 * count, offset).reshape(count, 2)
    offset += 8 * count
    uv2 = np.frombuffer(blob, "<f4", 2 * count, offset).reshape(count, 2)
    offset += 8 * count
    indices = np.frombuffer(blob, "<i4", entry["index_count"], offset)
    if entry["index_count"] and int(indices.max()) >= count:
        raise SystemExit(f"surface {entry['key']} indexes past its vertices")
    if float(uv2.min()) < 0.0 or float(uv2.max()) > 1.0:
        raise SystemExit(f"surface {entry['key']} has UV2 outside its unwrap")
    return positions, normals, uv, uv2, indices


def mesh_name(entry: dict) -> str:
    """A stable asset name: the resource name Godot baked, or its sub-resource."""
    parts = entry["resource"].split("::")
    fallback = parts[-1] if len(parts) > 1 else Path(parts[0]).stem
    label = entry["name"] or fallback
    if not label:
        raise SystemExit(f"surface {entry['key']} has no usable name")
    return f"{label}#{entry['surface']}"


def write_mesh_library(destination: Path, meshes: dict) -> dict:
    destination.parent.mkdir(parents=True, exist_ok=True)
    chunks = [MESH_MAGIC, struct.pack("<i", len(meshes))]
    vertices = triangles = 0
    for name in sorted(meshes):
        positions, normals, uv, uv2, indices = meshes[name]
        raw = name.encode("utf-8")
        chunks.append(struct.pack("<i", len(raw)) + raw)
        chunks.append(struct.pack("<ii", len(positions), len(indices)))
        chunks.extend(
            (
                positions.tobytes(),
                normals.tobytes(),
                uv.tobytes(),
                uv2.tobytes(),
                indices.tobytes(),
            )
        )
        vertices += len(positions)
        triangles += len(indices) // 3
    destination.write_bytes(b"".join(chunks))
    return {
        "count": len(meshes),
        "vertices": vertices,
        "triangles": triangles,
        "bytes": destination.stat().st_size,
        "sha256": sha256(destination),
    }


def atlas_layout(godot_root: Path, relative: str, layers: int) -> dict:
    """The authored slice grid, proven against the layer count Godot loaded."""
    config = (godot_root / f"{relative}.import").read_text(encoding="utf-8")
    horizontal = int(SLICES_HORIZONTAL.search(config).group(1))
    vertical = int(SLICES_VERTICAL.search(config).group(1))
    if horizontal <= 0 or vertical <= 0 or horizontal * vertical != layers:
        raise SystemExit(
            f"{relative} slices {horizontal}x{vertical} do not match {layers} layers"
        )
    return {"horizontal": horizontal, "vertical": vertical}


def single(items: list, what: str) -> dict:
    if len(items) != 1:
        raise SystemExit(f"expected exactly one {what}, found {len(items)}")
    return items[0]


def dumped_resource(value) -> str | None:
    """The path behind a dumped Object property, or None when it is unset."""
    if value is None:
        return None
    return value["resource"] or None


def port_material(dumped: dict, textures: dict, resources: Path) -> dict:
    """One source material, in the terms the runtime shader rebuilds it with."""
    properties = dumped["properties"]
    shader = dumped_resource(properties.get("shader"))
    if shader != MAP_OBJECT_SHADER:
        raise SystemExit(f"baked material {dumped['key']} draws with {shader}")

    def texture(name: str) -> str | None:
        source = dumped_resource(properties.get(name))
        if source is None:
            return None
        if source not in textures:
            raise SystemExit(f"{dumped['key']} draws with unported {source}")
        asset = textures[source]
        if not (resources / f"{asset}.png").is_file():
            raise SystemExit(f"{asset}.png is missing from the project")
        return asset

    albedo = texture("shader_parameter/texture_albedo")
    if albedo is None:
        raise SystemExit(f"baked material {dumped['key']} has no albedo texture")
    emissive = bool(properties["shader_parameter/emissive"])
    emission = texture("shader_parameter/texture_emissive")
    if emissive and emission is None:
        raise SystemExit(f"baked material {dumped['key']} emits without a texture")
    contrast = properties["shader_parameter/contrast"]
    return {
        "key": dumped["key"],
        "resource": dumped["resource"],
        "class": dumped["class"],
        "shader": shader,
        "albedo": properties["shader_parameter/albedo"],
        "texture": albedo,
        "emissive": emissive,
        # Godot keeps the second texture around when the flag is off and
        # never samples it, so neither may the port.
        "emissiveTexture": emission if emissive else None,
        # A .tres stores only what it changed; the uniform defaults to one.
        "contrast": 1.0 if contrast is None else contrast,
        "uv1Scale": properties["shader_parameter/uv1_scale"][:2],
        "uv1Offset": properties["shader_parameter/uv1_offset"][:2],
    }


def port_scene(key: str, godot_root: Path, resources: Path, dump_dir: Path) -> dict:
    config = SCENES[key]
    dump = json.loads((dump_dir / f"{key}.json").read_text(encoding="utf-8"))
    blob = (dump_dir / f"{key}_meshes.bin").read_bytes()
    surfaces = {entry["key"]: entry for entry in dump["meshes"]}
    materials = {entry["key"]: entry for entry in dump["materials"]}
    geometry = {entry["path"]: entry for entry in dump["geometry"]}

    lightmap = single(dump["lightmaps"], "LightmapGI")
    data = lightmap["data"]["properties"]
    texture = single(lightmap["textures"], "lightmap texture")
    layers = int(texture["layers"])

    atlas_source = godot_root / config["atlas"]
    atlas_asset = resources / config["atlas_asset"]
    atlas_asset.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(atlas_source, atlas_asset)

    meshes: dict = {}
    names: dict = {}
    ported: dict = {}
    entries = []
    stale = []
    for user in lightmap["users"]:
        if not user["path"]:
            stale.append(user["node_path"])
            continue
        instance = geometry.get(user["path"])
        if instance is None or instance["mesh"] is None:
            raise SystemExit(f"lightmap user {user['path']} has no mesh in {key}")
        lit = []
        drawn = set()
        for surface in instance["mesh"]["surfaces"]:
            entry = surfaces[surface["mesh_key"]]
            if entry["uv2_count"] == 0:
                raise SystemExit(
                    f"lightmapped {user['path']} surface {surface['index']} has no UV2"
                )
            name = mesh_name(entry)
            if names.setdefault(name, entry["key"]) != entry["key"]:
                raise SystemExit(f"mesh name {name} is not unique in {key}")
            meshes.setdefault(name, surface_arrays(blob, entry))
            lit.append(name)
            drawn.add(surface["material_key"])
        record = {
            "path": user["path"],
            "name": instance["name"],
            "slice": user["slice"],
            "rect": user["rect"],
            "meshes": lit,
            "transform": instance["transform"],
            "castShadow": instance["cast_shadow"],
            "giMode": instance["gi_mode"],
            "layers": instance["layers"],
        }
        if config["textures"] is not None:
            # One instance owns one rect of the atlas, so it draws with one
            # material: two would need two unwraps.
            if len(drawn) != 1:
                raise SystemExit(f"{user['path']} draws with {len(drawn)} materials")
            surface_material = drawn.pop()
            ported.setdefault(
                surface_material,
                port_material(
                    materials[surface_material], config["textures"], resources
                ),
            )
            record["material"] = surface_material
        entries.append(record)

    mesh_asset = resources / config["mesh_asset"]
    library = write_mesh_library(mesh_asset, meshes)
    library["resource"] = config["mesh_asset"].rsplit(".", 1)[0]

    environment = single(dump["environments"], "WorldEnvironment")
    sources = [
        {"path": config["scene"], "sha256": sha256(godot_root / config["scene"])},
        {"path": config["atlas"], "sha256": sha256(atlas_source)},
        {
            "path": f"{config['atlas']}.import",
            "sha256": sha256(godot_root / f"{config['atlas']}.import"),
        },
    ]
    for relative in (lightmap["data"]["resource"], config["environment"]):
        if not relative:
            continue
        path = godot_root / relative.removeprefix("res://")
        sources.append(
            {"path": str(path.relative_to(godot_root)), "sha256": sha256(path)}
        )

    manifest = {
        "id": key,
        "scene": config["scene"],
        "environment": {
            "path": environment["path"],
            "resource": environment["resource"],
            **camel_all(environment["environment"]),
            "cameraAttributes": (
                camel_all(environment["camera_attributes"])
                if environment["camera_attributes"]
                else None
            ),
        },
        "lights": [
            {
                "path": light["path"],
                "type": light["type"],
                "visible": light["visible"],
                "transform": light["transform"],
                **camel_pick(light["properties"]),
            }
            for light in dump["lights"]
        ],
        "lightmap": {
            "path": lightmap["path"],
            "resource": lightmap["data"]["resource"],
            "atlas": config["atlas_asset"].rsplit(".", 1)[0],
            "slices": layers,
            "sliceWidth": texture["width"],
            "sliceHeight": texture["height"],
            "layout": atlas_layout(godot_root, config["atlas"], layers),
            "bakedExposure": data["probe_data"]["baked_exposure"],
            "usesSphericalHarmonics": data["uses_spherical_harmonics"],
            "interior": data["probe_data"]["interior"],
            "probes": len(data["probe_data"]["points"]) // 3,
            "bounces": lightmap["properties"]["bounces"],
            "bias": lightmap["properties"]["bias"],
            "bytes": atlas_asset.stat().st_size,
            "sha256": sha256(atlas_asset),
        },
        "entries": sorted(entries, key=lambda entry: entry["path"]),
        "materials": [ported[name] for name in sorted(ported)],
        "staleEntries": sorted(stale),
        "meshes": library,
        "sources": sources,
    }

    out = resources / "Data" / f"lighting_{key}.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(
        f"{key}: lights={len(manifest['lights'])} entries={len(entries)}"
        f" materials={len(ported)} stale={len(stale)} slices={layers}"
        f" meshes={library['count']} vertices={library['vertices']}"
        f" triangles={library['triangles']}"
    )
    print(f"{key}: atlas={atlas_asset.name} bytes={atlas_asset.stat().st_size}")
    print(f"{key}: manifest={out} bytes={out.stat().st_size}")
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
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
    parser.add_argument("--godot", default="godot", help="Godot 4 binary")
    parser.add_argument(
        "--dump",
        type=Path,
        help="reuse an existing dump directory instead of running Godot",
    )
    args = parser.parse_args()

    godot_root = args.godot_root.expanduser().resolve()
    unity_root = args.unity_root.expanduser().resolve()
    resources = unity_root / "Assets/Blitz/Resources"
    dump_dir = (args.dump or (unity_root / "build" / "lighting_dump")).expanduser()
    if not args.dump:
        run_dump(args.godot, godot_root, unity_root, dump_dir)

    for key in SCENES:
        port_scene(key, godot_root, resources, dump_dir)


if __name__ == "__main__":
    main()
