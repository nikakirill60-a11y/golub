#!/usr/bin/env python3
"""Port the Godot Rudniki map source into deterministic Unity assets.

The Godot project stays the single source of truth. This script only reads the
authored scene and resources and rewrites them; it never invents a value and it
never converts coordinates. Everything is emitted verbatim in Godot's
right-handed space, and the Unity runtime mirrors it through one shared helper
so the whole port keeps a single handedness convention.

The terrain is stored as a height field instead of a mesh: the authored
ArrayMesh is a strictly regular 513x513 grid with a constant step and a
checkerboard diagonal, so the 12.6 MB of baked vertices and indices carry no
information that the heights do not already hold. The script proves those
invariants before writing, and fails loudly when the source stops matching.
The heights themselves come from the heightmap the terrain node loads, scaled
the way hterrain.gd lays the ground out. The baked mesh only proves the grid:
it was exported with an older vertical scale and stands 20% too high.

Map objects follow the same rule. Their geometry is read back from the GLB
files the artists authored instead of the base64 surfaces baked into the
scene, matched by the names Godot gave each imported mesh and checked against
the AABB the scene recorded. Meshes and collision soups land in two compact
binaries, and the object table records every instance with the properties
Godot resolves through scene inheritance.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import shutil
import struct
from pathlib import Path

import numpy as np
import trimesh
from PIL import Image

MAP_ID = "rudniki"

# maps/components/ai_marker.gd leaves an unset marker at PRIORITY_TERTIARY with a
# ten metre radius and no priority, so the porter resolves the same defaults
# instead of writing a zero the source never meant.
AI_MARKER_DEFAULT_TYPE = 2
AI_MARKER_DEFAULT_RADIUS = 10.0
MAP_DIRECTORY = "Rudniki"
OBJECT_ROOT = "scene"
TERRAIN_MAGIC = b"BLTZTER2"
MESH_MAGIC = b"BLTZMSH1"
SHAPE_MAGIC = b"BLTZCOL1"

BLOCK_HEADER = re.compile(r"^\[(\w+)\s*(.*)\]\s*$")
BLOCK_ATTR = re.compile(r'(\w+)=("[^"]*"|[^\s\]]+)')
GROUPS_ATTR = re.compile(r"groups=\[([^\]]*)\]")
EXT_RESOURCE = re.compile(r'^ExtResource\("([^"]+)"\)$')
SUB_RESOURCE = re.compile(r'^SubResource\("([^"]+)"\)$')
PACKED_VECTOR3 = re.compile(r"^PackedVector3Array\((.*)\)$")
PACKED_FLOAT32 = re.compile(r"^PackedFloat32Array\((.*)\)$")
VECTOR_ANY = re.compile(r"^Vector([234])i?\((.*)\)$")
TRANSFORM3D = re.compile(r"^Transform3D\((.*)\)$")
VECTOR = re.compile(r"^Vector([234])\((.*)\)$")
COLOR = re.compile(r"^Color\((.*)\)$")

# Godot source -> Unity resource stem. Payloads are copied byte for byte.
TEXTURES = {
    "maps/rudniki/terrain/color.png": "colormap",
    "maps/rudniki/terrain/splat.png": "tilemask",
    "maps/rudniki/terrain/src/TileTxRudniki.mali_Out.png": "tiletextures",
    "maps/rudniki/terrain/normal.png": "normal",
    "maps/rudniki/terrain/global_albedo.png": "global_albedo",
    "assets/gfx/ui/map_minimaps/rudniki_Out.png": "minimap",
    "assets/gfx/ui/map_loading_screeens/rudniki_Out.png": "loading_screen",
    "maps/rudniki/objects/_resources/sky_rud.PowerVR_iOS_Out.png": "Sky/base",
    "maps/rudniki/objects/_resources/sky_rud.PowerVR_iOS_Out-Face_1.png": "Sky/face1",
    "maps/rudniki/objects/_resources/sky_rud.PowerVR_iOS_Out-Face_2.png": "Sky/face2",
    "maps/rudniki/objects/_resources/sky_rud.PowerVR_iOS_Out-Face_3.png": "Sky/face3",
    "maps/rudniki/objects/_resources/sky_rud.PowerVR_iOS_Out-Face_4.png": "Sky/face4",
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def parse_numbers(text: str) -> list[float]:
    return [float(part) for part in text.split(",")]


def parse_transform(text: str) -> dict:
    match = TRANSFORM3D.match(text.strip())
    if match is None:
        raise SystemExit(f"not a Transform3D: {text!r}")
    numbers = parse_numbers(match.group(1))
    if len(numbers) != 12:
        raise SystemExit(f"Transform3D needs 12 components, got {len(numbers)}")
    return {"basis": numbers[:9], "origin": numbers[9:]}


def parse_vector(text: str) -> list[float]:
    match = VECTOR.match(text.strip())
    if match is None:
        raise SystemExit(f"not a Vector: {text!r}")
    return parse_numbers(match.group(2))


def parse_color(text: str) -> list[float]:
    match = COLOR.match(text.strip())
    if match is None:
        raise SystemExit(f"not a Color: {text!r}")
    return parse_numbers(match.group(1))


class Scene:
    """One parsed Godot text resource: its resource tables and its nodes."""

    def __init__(self, path: Path, relative: str) -> None:
        self.path = path
        self.relative = relative
        self.ext: dict[str, dict[str, str]] = {}
        self.sub: dict[str, dict] = {}
        self.nodes: list[dict] = []
        self.resource: dict[str, str] = {}

    def root(self) -> dict:
        for node in self.nodes:
            if not node["parent"]:
                return node
        raise SystemExit(f"{self.relative} has no root node")

    def ext_relative(self, value: str) -> str:
        match = EXT_RESOURCE.match(value.strip())
        if match is None:
            raise SystemExit(f"not an ExtResource: {value!r} in {self.relative}")
        entry = self.ext.get(match.group(1))
        if entry is None:
            raise SystemExit(f"unknown ExtResource {match.group(1)} in {self.relative}")
        return entry["path"].removeprefix("res://")

    def sub_block(self, value: str) -> dict:
        match = SUB_RESOURCE.match(value.strip())
        if match is None:
            raise SystemExit(f"not a SubResource: {value!r} in {self.relative}")
        entry = self.sub.get(match.group(1))
        if entry is None:
            raise SystemExit(f"unknown SubResource {match.group(1)} in {self.relative}")
        return entry


def read_scene(path: Path, relative: str) -> Scene:
    """Parse a .tscn/.tres file into resource tables and node blocks."""
    scene = Scene(path, relative)
    kind = ""
    attrs: dict[str, str] = {}
    groups: list[str] = []
    body: list[str] = []

    def flush() -> None:
        if not kind:
            return
        props: dict[str, str] = {}
        for line in body:
            if " = " not in line or line[:1].isspace() or line.startswith('"'):
                continue
            key, _, value = line.partition(" = ")
            props[key.strip()] = value.strip()
        if kind == "ext_resource":
            scene.ext[attrs["id"]] = attrs
        elif kind == "sub_resource":
            scene.sub[attrs["id"]] = {
                "type": attrs.get("type", ""),
                "props": props,
                "body": "\n".join(body),
            }
        elif kind == "resource":
            scene.resource = props
        elif kind == "node":
            scene.nodes.append(
                {
                    "name": attrs.get("name", ""),
                    "type": attrs.get("type", ""),
                    "parent": attrs.get("parent", ""),
                    "instance": attrs.get("instance", ""),
                    "groups": list(groups),
                    "props": props,
                }
            )

    for line in path.read_text(encoding="utf-8").splitlines():
        header = BLOCK_HEADER.match(line)
        if header is None:
            body.append(line)
            continue
        flush()
        kind = header.group(1)
        header_attrs = header.group(2)
        groups_match = GROUPS_ATTR.search(header_attrs)
        groups = (
            re.findall(r'"([^"]+)"', groups_match.group(1))
            if groups_match is not None
            else []
        )
        attrs = {
            key: value.strip('"')
            for key, value in BLOCK_ATTR.findall(GROUPS_ATTR.sub("", header_attrs))
        }
        body = []
    flush()
    return scene


def parse_scene(path: Path) -> list[dict]:
    """Collect every [node] block with its scalar properties."""
    return read_scene(path, path.name).nodes


def find_node(nodes: list[dict], name: str, parent: str) -> dict:
    for node in nodes:
        if node["name"] == name and node["parent"] == parent:
            return node
    raise SystemExit(f"missing node {parent}/{name}")


IDENTITY_BASIS = [1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0]


def compose(outer: dict, inner: dict) -> dict:
    """Applies the outer transform to the inner one. Godot stores rows."""
    basis = [
        sum(outer["basis"][row * 3 + k] * inner["basis"][k * 3 + col] for k in range(3))
        for row in range(3)
        for col in range(3)
    ]
    origin = [
        outer["origin"][row]
        + sum(outer["basis"][row * 3 + k] * inner["origin"][k] for k in range(3))
        for row in range(3)
    ]
    return {"basis": basis, "origin": origin}


def ancestors(nodes: list[dict], parent: str) -> list[dict]:
    """Resolves a Godot parent path such as "objects/static" into its nodes."""
    if parent in ("", "."):
        return []
    names = parent.split("/")
    return [
        find_node(nodes, name, "." if index == 0 else "/".join(names[:index]))
        for index, name in enumerate(names)
    ]


def node_transform(nodes: list[dict], node: dict) -> dict:
    """Composes a node transform with every transform above it in the scene.

    Godot writes each transform relative to the parent, and this map parks
    every prop under an "objects" node that is itself moved onto the middle of
    the terrain by (299.75, 0.001, 299.48). Reading the local transform alone
    dropped that move and buried 415 of the 456 props, a median of 9.3 metres
    deep, which is how the houses ended up inside the hills.
    """
    result = {"basis": list(IDENTITY_BASIS), "origin": [0.0, 0.0, 0.0]}
    for step in ancestors(nodes, node["parent"]) + [node]:
        text = step["props"].get("transform")
        if text is not None:
            result = compose(result, parse_transform(text))
    return result


def read_surface(path: Path) -> dict:
    text = path.read_text(encoding="utf-8")

    def payload(key: str) -> bytes:
        match = re.search(r'"%s":\s*PackedByteArray\("([A-Za-z0-9+/=]*)"\)' % key, text)
        if match is None:
            raise SystemExit(f"missing {key} in {path}")
        return base64.b64decode(match.group(1))

    def number(key: str) -> int:
        return int(re.search(r'"%s":\s*(\d+)' % key, text).group(1))

    return {
        "format": number("format"),
        "vertex_count": number("vertex_count"),
        "index_count": number("index_count"),
        "vertex_data": payload("vertex_data"),
        "index_data": payload("index_data"),
    }


def extract_height_field(surface: dict) -> tuple[np.ndarray, int, float]:
    """Return (heights[side, side] indexed by z*side + x, side, step)."""
    # ARRAY_FLAG_FORMAT_VERSION_2 | ARRAY_FORMAT_INDEX | ARRAY_FORMAT_VERTEX.
    expected_format = (1 << 35) | (1 << 12) | (1 << 0)
    if surface["format"] != expected_format:
        raise SystemExit(
            f"unexpected surface format {surface['format']}, want {expected_format}"
        )

    count = surface["vertex_count"]
    side = int(round(count ** 0.5))
    if side * side != count:
        raise SystemExit(f"vertex_count {count} is not a square grid")
    if (side - 1) ** 2 * 6 != surface["index_count"]:
        raise SystemExit("index_count does not match a quad grid")
    if len(surface["vertex_data"]) != count * 12:
        raise SystemExit("vertex_data is not 3 float32 per vertex")
    if len(surface["index_data"]) != surface["index_count"] * 4:
        raise SystemExit("index_data is not uint32 per index")

    verts = np.frombuffer(surface["vertex_data"], dtype="<f4").reshape(count, 3)
    indices = np.frombuffer(surface["index_data"], dtype="<u4")

    step = float(verts[side - 1, 0]) / (side - 1)
    columns = np.arange(count) % side
    rows = np.arange(count) // side
    tolerance = step * 1e-4
    if np.abs(verts[:, 0] - columns * step).max() > tolerance:
        raise SystemExit("terrain X is not a regular grid")
    if np.abs(verts[:, 2] - rows * step).max() > tolerance:
        raise SystemExit("terrain Z is not a regular grid, or rows are transposed")

    quads = side - 1
    quad = np.arange(quads * quads)
    base = (quad // quads) * side + (quad % quads)
    v00, v10, v01, v11 = base, base + 1, base + side, base + side + 1
    even = np.stack([np.stack([v00, v11, v01], 1), np.stack([v00, v10, v11], 1)], 1)
    odd = np.stack([np.stack([v00, v10, v01], 1), np.stack([v10, v11, v01], 1)], 1)
    checker = (((quad % quads) + (quad // quads)) % 2 == 0)[:, None, None]
    expected = np.where(checker, even, odd).astype(indices.dtype)
    if not np.array_equal(indices.reshape(quads * quads, 2, 3), expected):
        raise SystemExit("terrain triangulation is not the checkerboard grid")

    return verts[:, 1].astype("<f4").copy(), side, step


def write_height_field(
    destination: Path,
    heights: np.ndarray,
    normals: np.ndarray,
    side: int,
    step: float,
) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    if heights.shape != (side * side,) or normals.shape != (side * side, 3):
        raise SystemExit("terrain height and normal grids do not match")
    header = TERRAIN_MAGIC + struct.pack(
        "<iffff", side, step, step, float(heights.min()), float(heights.max())
    )
    destination.write_bytes(header + heights.tobytes() + normals.tobytes())


def read_height_map(path: Path, side: int) -> np.ndarray:
    """Raw heights from the hterrain heightmap, before map_scale."""
    blob = path.read_bytes()
    if blob[:4] != b"RSRC":
        raise SystemExit(f"{path} is not a Godot binary resource")
    want = side * side * 4
    offset = None
    for cursor in range(0, min(len(blob) - 4, 4096)):
        if struct.unpack_from("<i", blob, cursor)[0] == want:
            offset = cursor + 4
            break
    if offset is None or offset + want > len(blob):
        raise SystemExit(f"{path} does not hold a {side}x{side} float grid")
    return np.frombuffer(blob, dtype="<f4", count=side * side, offset=offset).copy()


def ground_heights(
    baked: np.ndarray, raw: np.ndarray, map_scale: list[float], step: float
) -> np.ndarray:
    """Heights in world metres, the way the terrain node lays the ground out.

    hterrain.gd:731 renders and collides through
    Transform3D(basis.scaled(map_scale), origin), so the authored heightmap
    becomes the ground once map_scale is applied. terrain_mesh.tres holds the
    same grid scaled by an older vertical scale, which is why it may only
    prove the layout: trusting its heights lifts the landscape above the
    spawn markers and drops a spawning tank straight through it.
    """
    if raw.shape != baked.shape:
        raise SystemExit("the heightmap and the baked mesh disagree on the grid")
    if abs(step - map_scale[0]) > 1e-4 or abs(step - map_scale[2]) > 1e-4:
        raise SystemExit(f"mesh step {step} does not match map_scale {map_scale}")

    ridge = raw > 1.0
    ratios = baked[ridge].astype("f8") / raw[ridge].astype("f8")
    spread = float(ratios.max() - ratios.min())
    if spread > 1e-3:
        raise SystemExit(f"the baked mesh is not the heightmap scaled ({spread})")
    print(
        f"heightmap scale {map_scale[1]}"
        f" (terrain_mesh.tres was baked at {float(np.median(ratios)):.5f})"
    )
    return (raw.astype("f8") * map_scale[1]).astype("<f4")


def surface_normals(
    path: Path, map_scale: list[float], expected_side: int
) -> np.ndarray:
    """Decode every authored terrain normal into source-space unit vectors."""
    pixels = np.asarray(Image.open(path).convert("RGB"), dtype=np.uint8)
    if pixels.shape != (expected_side, expected_side, 3):
        raise SystemExit(
            f"terrain normal map {path} is {pixels.shape[:2]}, "
            f"expected {expected_side}x{expected_side}"
        )

    # landscape.gdshader's unpack_normal reads RGB as XZY and negates the
    # resulting Z. HTerrain then applies inverse-transpose map scale.
    unit = pixels.astype(np.float64) / 255.0 * 2.0 - 1.0
    local = np.stack((unit[:, :, 0], unit[:, :, 2], -unit[:, :, 1]), axis=2)
    scale = np.asarray(map_scale, dtype=np.float64)
    if scale.shape != (3,) or np.any(scale == 0.0):
        raise SystemExit(f"invalid terrain map scale {map_scale}")
    normals = local / scale
    lengths = np.linalg.norm(normals, axis=2, keepdims=True)
    if np.any(lengths <= 1e-12):
        raise SystemExit(f"terrain normal map {path} encodes a zero normal")
    return np.asarray(normals / lengths, dtype="<f4").reshape(-1, 3)


def collect_spawners(nodes: list[dict], parent: str) -> list[dict]:
    spawners = []
    for node in nodes:
        if node["parent"] != parent:
            continue
        transform = node_transform(nodes, node)
        spawners.append(
            {
                "name": node["name"],
                "team": int(node["props"].get("team", 0)),
                "basis": transform["basis"],
                "origin": transform["origin"],
                # ui_player_battle.gd averages every spawner of a team into one
                # MinimapRespawn marker, the numbered "1" sprite.
                "respawn": 1,
            }
        )
    return spawners


def minimap_area(minimap: Path, border_min: list[float], border_max: list[float]) -> dict:
    """The ground rectangle under the painted part of the minimap image.

    fit_minimap_to_image() crops the 512px texture to Image.get_used_rect(),
    the bounds of every pixel with a non-zero alpha, and
    init_minimap_projection() spreads map_border_min..map_border_max (x, z)
    over that rectangle.
    """
    with Image.open(minimap) as image:
        box = image.convert("RGBA").getchannel("A").getbbox()
    if box is None:
        raise SystemExit(f"minimap {minimap} paints nothing")
    left, top, right, bottom = box
    return {
        "frame": [left, top, right - left, bottom - top],
        "min": [border_min[0], border_min[2]],
        "max": [border_max[0], border_max[2]],
    }


def patch_minimap(source: Path, destination: Path) -> None:
    """Adds the minimap fields to an already ported manifest in place.

    A full port needs the live lighting dump; the minimap only depends on the
    scene's spawners, its border markers and the copied minimap image.
    """
    scene_path = source / "maps" / MAP_ID / f"_{MAP_ID}.tscn"
    nodes = read_scene(scene_path, str(scene_path.relative_to(source))).nodes
    out = destination / "Assets/Blitz/Resources/Data" / f"map_{MAP_ID}.json"
    manifest = json.loads(out.read_text(encoding="utf-8"))
    border_min = node_transform(nodes, find_node(nodes, "map_border_min", "."))["origin"]
    border_max = node_transform(nodes, find_node(nodes, "map_border_max", "."))["origin"]
    manifest["minimapArea"] = minimap_area(
        source / "assets/gfx/ui/map_minimaps/rudniki_Out.png", border_min, border_max)
    manifest["spawns"] = {
        "teamA": collect_spawners(nodes, "teamA"),
        "teamB": collect_spawners(nodes, "teamB"),
    }
    out.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )


def collect_ai_markers(nodes: list[dict], parent: str) -> list[dict]:
    """Every AIMarker of the map, still in source coordinates.

    ai_marker.gd exports the marker type, the radius a bot roams inside and the
    tactical priority, and ai_controller.gd reads all three when it picks where
    to drive, so the manifest records them next to the marker position.
    """
    markers = []
    for node in nodes:
        if node["parent"] != parent:
            continue
        props = node["props"]
        transform = node_transform(nodes, node)
        markers.append(
            {
                "name": node["name"],
                "type": int(float(props.get("type", AI_MARKER_DEFAULT_TYPE))),
                "radius": float(props.get("radius", AI_MARKER_DEFAULT_RADIUS)),
                "priority": int(float(props.get("priority", 0))),
                "origin": transform["origin"],
            }
        )
    return markers


# --- map objects ---------------------------------------------------------
#
# Every object is one flat child of the scene's "objects" node, and Godot leans
# on scene inheritance for the rest: an instance may override a property in the
# map, inherit it from the object scene, or fall back to the shared template.
# The porter walks that same chain, so the table records the values Godot would
# resolve at load time instead of a guess.
#
# Geometry is read back from the authored GLB files rather than the base64
# surfaces baked into the scene: Godot names each imported ArrayMesh
# "<glb stem>_<mesh name>", which is enough to find the source triangles, and
# every export is verified against the AABB the scene recorded.

OBJECT_SCRIPTS = {
    "maps/components/map_object_static.gd": "static",
    "maps/components/map_object_destructible.gd": "destructible",
    "maps/components/map_object_vegetation.gd": "vegetation",
    "maps/components/map_object_tree.gd": "tree",
}

# All four source families are materialised. Trees and vegetation keep their
# nested visual parts and primitive collision shapes rather than being flattened
# into thousands of scene objects.
PORTED_KINDS = ("static", "destructible", "tree", "vegetation")

OBJECT_SHADER = "maps/components/map_object.gdshader"
VEGETATION_SHADER = "maps/vegetation/_vegetation_shader.gdshader"
WATER_SHADER = "maps/components/water_shader.gdshader"
WATER_SCENE = "maps/rudniki/objects/static/seaSurface.tscn"

# map_object_static.gd, map_object_destructible.gd and map_object_vegetation.gd
# collision layers, kept as the bit values Godot writes.
OBJECT_LAYERS = {"static": 1, "physics": 2, "destructible": 256, "vegetation": 32}

# map_object_destructible.gd defaults for instances that do not override them.
DESTRUCTIBLE_HEALTH = 10
DESTRUCTIBLE_EFFECT = 0

def discover_glb_sources(source: Path, root: Scene) -> tuple[str, ...]:
    """Find the donor GLBs in this map's own object source directory."""
    object_root = source / "maps" / MAP_ID / "objects"
    glbs = tuple(
        sorted(str(path.relative_to(source)) for path in object_root.rglob("*.glb"))
    )
    if not glbs:
        raise SystemExit(f"{root.relative} has no object GLB sources")
    return glbs


def texture_stem(relative: str) -> str:
    """Unity resource stem for a Godot texture, unique across the borrowed maps."""
    parts = Path(relative).parts
    name = Path(relative).name
    for suffix in (".mali_Out.png", ".png"):
        if name.endswith(suffix):
            name = name[: -len(suffix)]
            break
    if len(parts) > 2 and parts[0] == "maps" and parts[1] != MAP_ID:
        return f"{parts[1]}_{name}"
    return name


def packed_vector3(text: str) -> list[float]:
    match = PACKED_VECTOR3.match(text.strip())
    if match is None:
        raise SystemExit(f"not a PackedVector3Array: {text[:60]!r}")
    body = match.group(1).strip()
    return parse_numbers(body) if body else []


def shader_value(scene: Scene, raw: str):
    text = raw.strip()
    if text in ("true", "false"):
        return text == "true"
    if text.startswith("Color("):
        return parse_color(text)
    vector = VECTOR_ANY.match(text)
    if vector is not None:
        return parse_numbers(vector.group(2))
    if text.startswith("ExtResource("):
        return {"resource": scene.ext_relative(text)}
    if text.startswith('"'):
        return text.strip('"')
    return float(text)


def material_definition(scene: Scene, value: str) -> dict:
    """Read one source ShaderMaterial into a runtime material family."""
    block = scene.sub_block(value)
    if block["type"] != "ShaderMaterial":
        raise SystemExit(f"unexpected material {block['type']} in {scene.relative}")
    shader = scene.ext_relative(block["props"]["shader"])
    if shader not in (OBJECT_SHADER, VEGETATION_SHADER, WATER_SHADER):
        raise SystemExit(f"unexpected object shader {shader} in {scene.relative}")
    params = {
        key.split("/", 1)[1]: shader_value(scene, raw)
        for key, raw in block["props"].items()
        if key.startswith("shader_parameter/")
    }
    if shader == WATER_SHADER:
        texture = params.get("mainTexture")
        return {
            "shader": "water",
            "albedo": params.get("albedo_color", [1.0, 1.0, 1.0, 1.0]),
            "contrast": 1.0,
            "emissive": False,
            "uv1Scale": [1.0, 1.0, 1.0],
            "uv1Offset": [0.0, 0.0, 0.0],
            "uv2Scale": [1.0, 1.0, 1.0],
            "uv2Offset": [0.0, 0.0, 0.0],
            "textureSource": texture["resource"] if texture else None,
            "emissiveSource": None,
            "shift1": params.get("shift1", [0.007, 0.007]),
            "shift2": params.get("shift2", [0.04, 0.04]),
        }

    albedo = params.get("texture_albedo")
    emissive = params.get("texture_emissive")
    definition = {
        "shader": "vegetation" if shader == VEGETATION_SHADER else "object",
        "albedo": params.get("albedo", [1.0, 1.0, 1.0, 1.0]),
        "contrast": float(params.get("contrast", 1.0)),
        "emissive": bool(params.get("emissive", False)),
        "uv1Scale": params.get("uv1_scale", [1.0, 1.0, 1.0]),
        "uv1Offset": params.get("uv1_offset", [0.0, 0.0, 0.0]),
        "uv2Scale": params.get("uv2_scale", [1.0, 1.0, 1.0]),
        "uv2Offset": params.get("uv2_offset", [0.0, 0.0, 0.0]),
        "textureSource": albedo["resource"] if albedo else None,
        "emissiveSource": emissive["resource"] if emissive else None,
    }
    if shader == VEGETATION_SHADER:
        definition.update(
            {
                "alphaClip": float(params.get("alpha_scissor_threshold", 0.5)),
                "billboard": bool(params.get("billboard", True)),
                "originOffset": params.get("origin_offset", [0.0, 0.0, 0.0]),
                "swayAmount": float(params.get("sway_amount", 0.0)),
                "swayScale": float(params.get("sway_scale", 1.0)),
                "swayDuration": float(params.get("sway_duration", 1.0)),
                "swayDirection": params.get("sway_direction", [1.0, 0.0, 0.0]),
            }
        )
    return definition


def mesh_reference(scene: Scene, value: str) -> tuple[str, list[float]]:
    """Return the imported mesh name and the AABB the scene baked for it."""
    block = scene.sub_block(value)
    if block["type"] != "ArrayMesh":
        raise SystemExit(f"unexpected mesh {block['type']} in {scene.relative}")
    name = block["props"].get("resource_name")
    if name is None:
        raise SystemExit(f"ArrayMesh without resource_name in {scene.relative}")
    aabb = re.search(r'"aabb": AABB\(([^)]*)\)', block["body"])
    if aabb is None:
        raise SystemExit(f"ArrayMesh {name} has no aabb in {scene.relative}")
    return name.strip('"'), parse_numbers(aabb.group(1))


def shape_definition(scene: Scene, value: str) -> dict:
    """Read a collision shape as the triangle soup Unity can collide against."""
    block = scene.sub_block(value)
    kind = block["type"]
    if kind == "ConcavePolygonShape3D":
        return {"type": "concave", "points": packed_vector3(block["props"]["data"])}
    if kind == "ConvexPolygonShape3D":
        points = np.asarray(packed_vector3(block["props"]["points"])).reshape(-1, 3)
        hull = trimesh.PointCloud(points).convex_hull
        return {"type": "convex", "points": hull.triangles.reshape(-1).tolist()}
    raise SystemExit(f"unsupported shape {kind} in {scene.relative}")


def load_glb_meshes(
    source: Path, relatives: tuple[str, ...]
) -> tuple[dict, list[dict]]:
    """Every reachable GLB mesh keyed as Godot named it, plus source digests."""
    meshes: dict = {}
    digests: list[dict] = []
    for relative in relatives:
        path = source / relative
        if not path.is_file():
            raise SystemExit(f"missing GLB source {path}")
        loaded = trimesh.load(path, force="scene", process=False)
        for name, geometry in loaded.geometry.items():
            meshes[f"{path.stem}_{name}"] = geometry
        digests.append({"path": relative, "sha256": sha256(path)})
    return meshes, digests


def mesh_payload(
    name: str, geometry, aabb: list[float], reverse_gltf_winding: bool
) -> tuple:
    """Positions, normals, UVs and indices, proven against the authored AABB."""
    positions = np.asarray(geometry.vertices, dtype="<f4")
    normals = np.asarray(geometry.vertex_normals, dtype="<f4")
    uv = getattr(geometry.visual, "uv", None)
    if uv is None:
        raise SystemExit(f"mesh {name} has no UV channel")
    uv = np.asarray(uv, dtype="<f4")
    faces = np.asarray(geometry.faces, dtype="<u4")
    # glTF winds front faces counter-clockwise. Godot's glTF importer swaps
    # those indices to its clockwise convention, so GLB donors need the same
    # conversion here. Live ArrayMeshes have already passed through Godot and
    # must remain untouched. GodotSpace performs the one final flip required
    # by Unity's mirrored coordinate system at runtime.
    if reverse_gltf_winding:
        faces = faces[:, ::-1]
    indices = np.ascontiguousarray(faces).reshape(-1)
    if normals.shape != positions.shape or uv.shape[0] != positions.shape[0]:
        raise SystemExit(f"mesh {name} vertex streams disagree")
    lower = positions.min(axis=0)
    size = positions.max(axis=0) - lower
    delta = max(
        float(np.abs(lower - np.asarray(aabb[:3], dtype="<f4")).max()),
        float(np.abs(size - np.asarray(aabb[3:], dtype="<f4")).max()),
    )
    if delta > 1e-3:
        raise SystemExit(f"mesh {name} does not match the authored AABB ({delta})")
    return positions, normals, uv, indices


def write_mesh_library(destination: Path, meshes: dict) -> dict:
    destination.parent.mkdir(parents=True, exist_ok=True)
    chunks = [MESH_MAGIC, struct.pack("<i", len(meshes))]
    vertices = triangles = 0
    for name in sorted(meshes):
        positions, normals, uv, indices = meshes[name]
        raw = name.encode("utf-8")
        chunks.append(struct.pack("<i", len(raw)) + raw)
        chunks.append(struct.pack("<ii", len(positions), len(indices)))
        chunks.extend(
            (positions.tobytes(), normals.tobytes(), uv.tobytes(), indices.tobytes())
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


def write_shape_library(destination: Path, shapes: dict) -> dict:
    destination.parent.mkdir(parents=True, exist_ok=True)
    chunks = [SHAPE_MAGIC, struct.pack("<i", len(shapes))]
    vertices = 0
    for name in sorted(shapes):
        points = shapes[name]
        raw = name.encode("utf-8")
        chunks.append(struct.pack("<i", len(raw)) + raw)
        chunks.append(struct.pack("<i", len(points) // 3))
        chunks.append(points.tobytes())
        vertices += len(points) // 3
    destination.write_bytes(b"".join(chunks))
    return {
        "count": len(shapes),
        "vertices": vertices,
        "triangles": vertices // 3,
        "bytes": destination.stat().st_size,
        "sha256": sha256(destination),
    }


def load_scene(cache: dict, source: Path, relative: str) -> Scene:
    scene = cache.get(relative)
    if scene is None:
        path = source / relative
        if not path.is_file():
            raise SystemExit(f"missing scene {path}")
        scene = read_scene(path, relative)
        cache[relative] = scene
    return scene


def inheritance(cache: dict, source: Path, scene: Scene, node: dict) -> list[tuple]:
    """The node itself, then every scene root it inherits properties from."""
    entries = [(scene, node["props"])]
    holder, current = scene, node
    while current["instance"]:
        holder = load_scene(cache, source, holder.ext_relative(current["instance"]))
        current = holder.root()
        entries.append((holder, current["props"]))
    return entries


def resolve(entries: list[tuple], key: str) -> tuple:
    for holder, props in entries:
        if key in props:
            return holder, props[key]
    return None, None



def _copy_materialized(nodes: dict) -> dict:
    return {
        path: {
            "name": node["name"],
            "type": node["type"],
            "parent": node["parent"],
            "props": dict(node["props"]),
        }
        for path, node in nodes.items()
    }


def materialize_scene(
    cache: dict, materialized: dict, source: Path, relative: str
) -> dict:
    """Expand inherited and nested PackedScenes while retaining property owners.

    A property may refer to an ExtResource/SubResource table in any scene in the
    chain, so each resolved scalar is kept as ``(holder, raw_value)``. This is
    the same ownership rule ``inheritance`` uses for flat map objects, extended
    to the tree and bush node hierarchies.
    """
    known = materialized.get(relative)
    if known is not None:
        return _copy_materialized(known)

    scene = load_scene(cache, source, relative)
    root = scene.root()
    if root["instance"]:
        base = scene.ext_relative(root["instance"])
        nodes = materialize_scene(cache, materialized, source, base)
    else:
        nodes = {
            ".": {
                "name": root["name"],
                "type": root["type"],
                "parent": "",
                "props": {},
            }
        }

    def overlay(target: dict, holder: Scene, raw: dict) -> None:
        if raw["name"]:
            target["name"] = raw["name"]
        if raw["type"]:
            target["type"] = raw["type"]
        for key, value in raw["props"].items():
            target["props"][key] = (holder, value)

    overlay(nodes["."], scene, root)
    for raw in scene.nodes:
        if raw is root:
            continue
        parent = raw["parent"]
        node_path = raw["name"] if parent == "." else f"{parent}/{raw['name']}"
        if node_path not in nodes and raw["instance"]:
            nested_relative = scene.ext_relative(raw["instance"])
            nested = materialize_scene(cache, materialized, source, nested_relative)
            for child_path, child in nested.items():
                full_path = node_path if child_path == "." else f"{node_path}/{child_path}"
                if child_path == ".":
                    full_parent = parent
                elif child["parent"] == ".":
                    full_parent = node_path
                else:
                    full_parent = f"{node_path}/{child['parent']}"
                nodes[full_path] = {
                    "name": child["name"],
                    "type": child["type"],
                    "parent": full_parent,
                    "props": dict(child["props"]),
                }
        elif node_path not in nodes:
            nodes[node_path] = {
                "name": raw["name"],
                "type": raw["type"],
                "parent": parent,
                "props": {},
            }
        elif raw["instance"]:
            raise SystemExit(
                f"{relative} replaces inherited node {node_path} with a PackedScene"
            )
        overlay(nodes[node_path], scene, raw)

    materialized[relative] = _copy_materialized(nodes)
    return nodes


def held_property(node: dict, key: str, default=None) -> tuple:
    return node["props"].get(key, (None, default))


def materialized_transform(nodes: dict, node_path: str) -> dict:
    paths = ["."]
    if node_path != ".":
        parts = node_path.split("/")
        paths.extend("/".join(parts[: index + 1]) for index in range(len(parts)))
    result = {"basis": list(IDENTITY_BASIS), "origin": [0.0, 0.0, 0.0]}
    for path in paths:
        _, raw = held_property(nodes[path], "transform")
        if raw is not None:
            result = compose(result, parse_transform(raw))
    return result


def materialized_visible(nodes: dict, node_path: str) -> bool:
    current = node_path
    while current:
        _, raw = held_property(nodes[current], "visible")
        if raw == "false":
            return False
        current = nodes[current]["parent"]
    return True


def primitive_shape_definition(scene: Scene, value: str) -> dict:
    block = scene.sub_block(value)
    props = block["props"]
    if block["type"] == "CapsuleShape3D":
        return {
            "type": "capsule",
            "radius": float(props.get("radius", 0.5)),
            "height": float(props.get("height", 2.0)),
        }
    if block["type"] == "SphereShape3D":
        return {"type": "sphere", "radius": float(props.get("radius", 1.0))}
    if block["type"] == "BoxShape3D":
        raw = props.get("size", "Vector3(1, 1, 1)")
        return {"type": "box", "size": parse_vector(raw)}
    raise SystemExit(
        f"unsupported primitive shape {block['type']} in {scene.relative}"
    )


def read_live_meshes(dump_dir: Path) -> dict:
    """Read ArrayMesh surfaces that only exist in binary or embedded resources."""
    manifest_path = dump_dir / f"{MAP_ID}.json"
    blob_path = dump_dir / f"{MAP_ID}_meshes.bin"
    if not manifest_path.is_file() or not blob_path.is_file():
        raise SystemExit(
            f"missing live mesh dump in {dump_dir}; run port_godot_lighting.py first"
        )
    payload = json.loads(manifest_path.read_text(encoding="utf-8"))
    blob = blob_path.read_bytes()
    result = {}
    for entry in payload["meshes"]:
        count = entry["vertex_count"]
        if entry["normal_count"] != count or entry["uv_count"] != count:
            continue
        if entry["index_count"] <= 0 or entry["index_count"] % 3:
            continue
        offset = entry["offset"]
        positions = np.frombuffer(blob, "<f4", 3 * count, offset).reshape(count, 3)
        offset += 12 * count
        normals = np.frombuffer(blob, "<f4", 3 * count, offset).reshape(count, 3)
        offset += 12 * count
        uv = np.frombuffer(blob, "<f4", 2 * count, offset).reshape(count, 2)
        offset += 8 * count
        offset += 8 * entry["uv2_count"]
        indices = np.frombuffer(blob, "<i4", entry["index_count"], offset)
        if int(indices.min()) < 0 or int(indices.max()) >= count:
            raise SystemExit(f"live mesh {entry['key']} has invalid indices")
        geometry = trimesh.Trimesh(
            vertices=positions.copy(),
            faces=indices.reshape(-1, 3).copy(),
            vertex_normals=normals.copy(),
            visual=trimesh.visual.texture.TextureVisuals(uv=uv.copy()),
            process=False,
            validate=False,
        )
        name = entry["name"]
        if not name:
            continue
        known = result.setdefault(name, geometry)
        if known is not geometry and (
            known.vertices.shape != geometry.vertices.shape
            or not np.array_equal(known.vertices, geometry.vertices)
        ):
            raise SystemExit(f"live mesh name {name} is ambiguous")
    return result


def read_live_multimeshes(dump_dir: Path) -> dict:
    """Read binary MultiMesh resources through the live headless-Godot dump."""
    path = dump_dir / f"{MAP_ID}.json"
    if not path.is_file():
        raise SystemExit(
            f"missing {path}; run Tools/port_godot_lighting.py before the map port"
        )
    payload = json.loads(path.read_text(encoding="utf-8"))
    result = {}
    for geometry in payload["geometry"]:
        multi = geometry.get("multimesh")
        if multi is None:
            continue
        relative = multi["resource"].removeprefix("res://")
        if multi["transform_format"] != 1:
            raise SystemExit(f"{relative} is not a 3D MultiMesh")
        if multi["visible_instance_count"] not in (-1, multi["instance_count"]):
            raise SystemExit(f"{relative} hides part of its MultiMesh instances")
        quad_size = multi.get("quad_size", [])
        if len(quad_size) != 2:
            raise SystemExit(f"{relative} does not use a QuadMesh")
        definition = {
            "source": relative,
            "count": multi["instance_count"],
            "quadSize": quad_size,
            "transforms": multi["transforms"],
        }
        known = result.setdefault(relative, definition)
        if known != definition:
            raise SystemExit(f"instances disagree on shared MultiMesh {relative}")
    if not result:
        raise SystemExit(f"the live {MAP_ID} dump contains no MultiMeshes")
    return result


def multimesh_definition(
    cache: dict, source: Path, scene: Scene, value: str, live: dict
) -> dict:
    relative = scene.ext_relative(value)
    known = live.get(relative)
    if known is not None:
        return known
    resource = load_scene(cache, source, relative)
    props = resource.resource
    if int(props.get("transform_format", 0)) != 1:
        raise SystemExit(f"{relative} is not a 3D MultiMesh")
    count = int(props.get("instance_count", 0))
    if count <= 0:
        raise SystemExit(f"{relative} has no MultiMesh instances")

    mesh = resource.sub_block(props["mesh"])
    if mesh["type"] != "QuadMesh":
        raise SystemExit(f"{relative} uses unsupported {mesh['type']}")
    size = parse_vector(mesh["props"].get("size", "Vector2(1, 1)"))
    if len(size) != 2:
        raise SystemExit(f"{relative} has an invalid QuadMesh size")

    packed = PACKED_FLOAT32.match(props.get("buffer", ""))
    if packed is None:
        raise SystemExit(f"{relative} has no PackedFloat32Array buffer")
    values = parse_numbers(packed.group(1)) if packed.group(1).strip() else []
    if len(values) != count * 12:
        raise SystemExit(
            f"{relative} has {len(values)} transform floats, expected {count * 12}"
        )

    transforms = []
    for index in range(count):
        data = values[index * 12 : index * 12 + 12]
        transforms.append(
            {
                "basis": [
                    data[0], data[1], data[2],
                    data[4], data[5], data[6],
                    data[8], data[9], data[10],
                ],
                "origin": [data[3], data[7], data[11]],
            }
        )
    return {
        "source": relative,
        "count": count,
        "quadSize": size,
        "transforms": transforms,
    }


def export_objects(
    source: Path, scene: Scene, resources: Path, dump_dir: Path
) -> dict:
    """Write every map prop, including nested tree and bush source scenes."""
    cache: dict = {scene.relative: scene}
    materialized: dict = {}
    live_multimeshes = read_live_multimeshes(dump_dir)
    live_meshes = read_live_meshes(dump_dir)
    glb_sources = discover_glb_sources(source, scene)
    glb_meshes, sources = load_glb_meshes(source, glb_sources)

    meshes: dict = {}
    shapes: dict = {}
    materials: dict = {}
    multi_meshes: dict = {}
    textures: dict = {}
    shader_sources: set[str] = set()
    objects: list[dict] = []
    skipped: dict = {}
    prototypes: dict = {}

    def use_mesh(holder: Scene, value: str) -> str:
        name, aabb = mesh_reference(holder, value)
        if name not in meshes:
            geometry = glb_meshes.get(name)
            reverse_gltf_winding = geometry is not None
            if geometry is None:
                geometry = live_meshes.get(name)
            if geometry is None:
                raise SystemExit(f"no source dump holds mesh {name}")
            meshes[name] = mesh_payload(
                name, geometry, aabb, reverse_gltf_winding
            )
        return name

    def use_shape(holder: Scene, value: str) -> str:
        definition = shape_definition(holder, value)
        points = np.asarray(definition["points"], dtype="<f4")
        if len(points) % 9:
            raise SystemExit(f"collision soup in {holder.relative} is not triangles")
        digest = hashlib.sha1(points.tobytes()).hexdigest()[:12]
        name = f"{definition['type']}_{digest}"
        shapes[name] = points
        return name

    def use_material(holder: Scene, value: str) -> str:
        block = holder.sub_block(value)
        shader_sources.add(holder.ext_relative(block["props"]["shader"]))
        definition = material_definition(holder, value)
        payload = json.dumps(definition, sort_keys=True).encode("utf-8")
        name = f"material_{hashlib.sha1(payload).hexdigest()[:12]}"
        if name not in materials:
            entry = dict(definition)
            for key, field in (
                ("textureSource", "texture"),
                ("emissiveSource", "emissiveTexture"),
            ):
                relative = entry.pop(key)
                if not relative:
                    continue
                stem = texture_stem(relative)
                family = {
                    "object": "Opaque",
                    "vegetation": "Vegetation",
                    "water": "Water",
                }[definition["shader"]]
                key_name = (family, stem)
                known = textures.setdefault(key_name, relative)
                if known != relative:
                    raise SystemExit(
                        f"texture stem {family}/{stem} clashes: {known} vs {relative}"
                    )
                entry[field] = f"Maps/Rudniki/Objects/{family}/{stem}"
            materials[name] = entry
        return name

    def use_multimesh(holder: Scene, value: str) -> str:
        definition = multimesh_definition(
            cache, source, holder, value, live_multimeshes
        )
        payload = json.dumps(definition, sort_keys=True).encode("utf-8")
        name = f"multimesh_{hashlib.sha1(payload).hexdigest()[:12]}"
        multi_meshes.setdefault(name, definition)
        return name

    def required(entries: list[tuple], key: str, owner: str) -> tuple:
        holder, value = resolve(entries, key)
        if value is None:
            raise SystemExit(f"object {owner} resolves no {key}")
        return holder, value

    def optional_shape(entries: list[tuple], key: str) -> str | None:
        holder, value = resolve(entries, key)
        return use_shape(holder, value) if value else None

    def mesh_surface_material(holder: Scene, value: str) -> tuple:
        block = holder.sub_block(value)
        match = re.search(
            r'"material"\s*:\s*(SubResource\("[^"]+"\))', block["body"]
        )
        if not match:
            raise SystemExit(f"mesh {value} in {holder.relative} has no material")
        return holder, match.group(1)

    def standalone_visual(relative: str) -> dict:
        nodes = materialize_scene(cache, materialized, source, relative)
        candidates = [
            (path, node)
            for path, node in nodes.items()
            if node["type"] == "MeshInstance3D"
            and materialized_visible(nodes, path)
        ]
        if len(candidates) != 1:
            raise SystemExit(
                f"{relative} has {len(candidates)} standalone visual meshes"
            )
        node_path, node = candidates[0]
        mesh_holder, mesh = held_property(node, "mesh")
        if mesh is None:
            raise SystemExit(f"{relative}:{node_path} has no mesh")
        material_holder, material = held_property(node, "material_override")
        if material is None:
            material_holder, material = mesh_surface_material(mesh_holder, mesh)
        return {
            "basis": materialized_transform(nodes, node_path)["basis"],
            "origin": materialized_transform(nodes, node_path)["origin"],
            "mesh": use_mesh(mesh_holder, mesh),
            "material": use_material(material_holder, material),
        }

    def ancestor_body(nodes: dict, node_path: str) -> tuple:
        parent = nodes[node_path]["parent"]
        while parent:
            candidate = nodes[parent]
            if candidate["type"] in ("Area3D", "StaticBody3D"):
                return parent, candidate
            parent = candidate["parent"]
        raise SystemExit(f"collision node {node_path} has no physics body")

    def extract_prototype(relative: str, kind: str) -> dict:
        key = (relative, kind)
        known = prototypes.get(key)
        if known is not None:
            return known
        nodes = materialize_scene(cache, materialized, source, relative)
        parts = []
        colliders = []
        for node_path, node in nodes.items():
            if not materialized_visible(nodes, node_path):
                continue
            if node["type"] in ("MeshInstance3D", "MultiMeshInstance3D"):
                material_holder, material = held_property(node, "material_override")
                if material is None:
                    raise SystemExit(
                        f"visual {relative}:{node_path} has no material_override"
                    )
                transform = materialized_transform(nodes, node_path)
                part = {
                    "name": node_path.replace("/", "_"),
                    "basis": transform["basis"],
                    "origin": transform["origin"],
                    "material": use_material(material_holder, material),
                    "followsTrunk": kind == "tree" and (
                        node_path == "trunk" or node_path.startswith("trunk/")
                    ),
                }
                mesh_holder, mesh = held_property(node, "mesh")
                multi_holder, multi = held_property(node, "multimesh")
                if mesh is not None and multi is None:
                    part["mesh"] = use_mesh(mesh_holder, mesh)
                elif multi is not None and mesh is None:
                    part["multiMesh"] = use_multimesh(multi_holder, multi)
                else:
                    raise SystemExit(
                        f"visual {relative}:{node_path} must have one geometry source"
                    )
                parts.append(part)

            if node["type"] != "CollisionShape3D":
                continue
            shape_holder, shape = held_property(node, "shape")
            if shape is None:
                continue
            _, body = ancestor_body(nodes, node_path)
            _, layer = held_property(body, "collision_layer", "1")
            _, mask = held_property(body, "collision_mask", "1")
            transform = materialized_transform(nodes, node_path)
            colliders.append(
                {
                    "name": node_path.replace("/", "_"),
                    **primitive_shape_definition(shape_holder, shape),
                    "basis": transform["basis"],
                    "origin": transform["origin"],
                    "isTrigger": body["type"] == "Area3D",
                    "collisionLayer": int(layer),
                    "collisionMask": int(mask),
                    "followsTrunk": kind == "tree" and node_path.startswith("trunk/"),
                }
            )

        if not parts:
            raise SystemExit(f"{relative} resolves no visible parts")
        if not colliders:
            raise SystemExit(f"{relative} resolves no primitive colliders")
        result = {"parts": parts, "colliders": colliders}
        if kind == "tree":
            _, fall_speed = held_property(nodes["."], "fall_speed", "0.2")
            result["fallSpeed"] = float(fall_speed)
        prototypes[key] = result
        return result

    for node in scene.nodes:
        if node["parent"] != OBJECT_ROOT:
            continue
        entries = inheritance(cache, source, scene, node)
        name = node["name"]
        holder, script = resolve(entries, "script")
        relative = entries[1][0].relative if len(entries) > 1 else scene.relative
        root_transform = node_transform(scene.nodes, node)
        if script is None:
            if relative != WATER_SCENE:
                raise SystemExit(f"object {name} resolves no script")
            visual = standalone_visual(relative)
            transform = compose(root_transform, visual)
            objects.append(
                {
                    "name": name,
                    "kind": "water",
                    "source": relative,
                    "basis": transform["basis"],
                    "origin": transform["origin"],
                    "material": visual["material"],
                    "mesh": visual["mesh"],
                    "collisionLayer": 0,
                    "collisionMask": 0,
                }
            )
            continue

        kind = OBJECT_SCRIPTS.get(holder.ext_relative(script))
        if kind is None:
            raise SystemExit(f"object {name} runs an unknown script")
        if kind not in PORTED_KINDS:
            skipped[kind] = skipped.get(kind, 0) + 1
            continue
        entry = {
            "name": name,
            "kind": kind,
            "source": relative,
            "basis": root_transform["basis"],
            "origin": root_transform["origin"],
        }
        node_groups = node.get("groups", [])
        if "navmesh_obstacle" in node_groups:
            entry["navigationObstacle"] = True
        if "ai_nohide" in node_groups:
            entry["aiNoHide"] = True
        if kind == "static":
            entry["material"] = use_material(*required(entries, "material", name))
            entry["mesh"] = use_mesh(*required(entries, "mesh", name))
            entry["shape"] = optional_shape(entries, "colshape")
            entry["collisionLayer"] = OBJECT_LAYERS["static"]
            entry["collisionMask"] = 0
            entry["bakedPath"] = f"{OBJECT_ROOT}/{name}/mesh_instance"
        elif kind == "destructible":
            entry["material"] = use_material(*required(entries, "material", name))
            entry["mesh"] = use_mesh(*required(entries, "mesh_normal", name))
            entry["meshDestroyed"] = use_mesh(
                *required(entries, "mesh_destroyed", name)
            )
            entry["shape"] = optional_shape(entries, "colshape_normal")
            entry["shapeDestroyed"] = optional_shape(entries, "colshape_destroyed")
            _, health = resolve(entries, "health")
            _, effect = resolve(entries, "effect")
            _, layer = resolve(entries, "collision_layer")
            _, mask = resolve(entries, "collision_mask")
            entry["health"] = int(health) if health is not None else DESTRUCTIBLE_HEALTH
            entry["effect"] = int(effect) if effect is not None else DESTRUCTIBLE_EFFECT
            entry["collisionLayer"] = int(layer) if layer is not None else 0
            entry["collisionMask"] = int(mask) if mask is not None else 0
            entry["bakedPath"] = f"{OBJECT_ROOT}/{name}/mesh_instance_normal"
        else:
            prototype = extract_prototype(relative, kind)
            entry["parts"] = prototype["parts"]
            entry["colliders"] = prototype["colliders"]
            if kind == "tree":
                entry["fallSpeed"] = prototype["fallSpeed"]
        objects.append(entry)

    for node in scene.nodes:
        if node["parent"] != "static_bg" or node["type"] != "MeshInstance3D":
            continue
        name = node["name"]
        material = node["props"].get("material_override")
        mesh = node["props"].get("mesh")
        if material is None or mesh is None:
            raise SystemExit(f"background {name} is missing geometry or material")
        transform = node_transform(scene.nodes, node)
        objects.append(
            {
                "name": f"static_bg_{name}",
                "kind": "decoration",
                "source": scene.relative,
                "basis": transform["basis"],
                "origin": transform["origin"],
                "material": use_material(scene, material),
                "mesh": use_mesh(scene, mesh),
                "collisionLayer": 0,
                "collisionMask": 0,
                "bakedPath": f"static_bg/{name}",
            }
        )

    map_dir = resources / "Maps" / MAP_DIRECTORY
    mesh_asset = map_dir / "object_meshes.bytes"
    shape_asset = map_dir / "object_shapes.bytes"
    mesh_info = write_mesh_library(mesh_asset, meshes)
    shape_info = write_shape_library(shape_asset, shapes)

    texture_dir = map_dir / "Objects"
    if texture_dir.exists():
        shutil.rmtree(texture_dir)
    texture_dir.mkdir(parents=True)
    for (family, stem), relative in sorted(textures.items()):
        origin = source / relative
        if not origin.is_file():
            raise SystemExit(f"missing texture source {origin}")
        target = texture_dir / family / f"{stem}.png"
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(origin, target)
        sources.append({"path": relative, "sha256": sha256(origin)})
    for relative in sorted(cache):
        sources.append({"path": relative, "sha256": sha256(source / relative)})
    for relative in sorted(shader_sources):
        sources.append({"path": relative, "sha256": sha256(source / relative)})
    sources_by_path = {item["path"]: item for item in sources}
    sources = [sources_by_path[path] for path in sorted(sources_by_path)]

    manifest = {
        "map": MAP_ID,
        "layers": OBJECT_LAYERS,
        "meshes": {"resource": "Maps/Rudniki/object_meshes", **mesh_info},
        "shapes": {"resource": "Maps/Rudniki/object_shapes", **shape_info},
        "materials": materials,
        "multiMeshes": multi_meshes,
        "objects": objects,
        "skipped": skipped,
        "sources": sources,
    }
    out = resources / "Data" / f"map_{MAP_ID}_objects.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    return {
        "resource": f"Data/map_{MAP_ID}_objects",
        "count": len(objects),
        "skipped": skipped,
        "meshes": mesh_info["count"],
        "shapes": shape_info["count"],
        "materials": len(materials),
        "multiMeshes": len(multi_meshes),
        "billboards": sum(item["count"] for item in multi_meshes.values()),
        "textures": len(textures),
        "bytes": out.stat().st_size,
        "sha256": sha256(out),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path, help="Godot project root")
    parser.add_argument("--destination", required=True, type=Path, help="Unity project root")
    parser.add_argument(
        "--dump",
        type=Path,
        help="live dump written by Tools/port_godot_lighting.py",
    )
    parser.add_argument(
        "--minimap-only",
        action="store_true",
        help="only refresh minimapArea and the spawn respawn numbers",
    )
    args = parser.parse_args()
    if args.minimap_only:
        patch_minimap(args.source, args.destination)
        return
    dump_dir = (
        args.dump or args.destination / "build" / "lighting_dump"
    ).expanduser().resolve()

    scene_path = args.source / "maps" / MAP_ID / f"_{MAP_ID}.tscn"
    mesh_path = args.source / "maps" / MAP_ID / "terrain" / "terrain_mesh.res"
    scene = read_scene(scene_path, str(scene_path.relative_to(args.source)))
    nodes = scene.nodes

    terrain = find_node(nodes, "terrain", ".")
    map_scale = parse_vector(terrain["props"]["map_scale"])
    height_path = args.source / "maps" / MAP_ID / "terrain" / "height.res"
    normal_path = args.source / "maps" / MAP_ID / "terrain" / "normal.png"

    normal_image = Image.open(normal_path)
    if normal_image.width != normal_image.height:
        raise SystemExit(f"terrain normal map is not square: {normal_image.size}")
    side = normal_image.width
    step = map_scale[0]
    if side < 2 or abs(step - map_scale[2]) > 1e-4:
        raise SystemExit(f"terrain grid does not match map_scale {map_scale}")
    raw_heights = read_height_map(height_path, side)
    heights = np.asarray(raw_heights * map_scale[1], dtype="<f4")
    normals = surface_normals(normal_path, map_scale, side)
    surface_normal = normals[0].tolist()  # compatibility summary; payload is per vertex
    resources = args.destination / "Assets/Blitz/Resources"
    map_dir = resources / "Maps" / MAP_DIRECTORY
    height_asset = map_dir / "terrain_height.bytes"
    write_height_field(height_asset, heights, normals, side, step)

    landscape_shader_path = args.source / "maps" / "components" / "landscape.gdshader"
    sources = [
        {"path": str(mesh_path.relative_to(args.source)), "sha256": sha256(mesh_path)},
        {"path": str(height_path.relative_to(args.source)), "sha256": sha256(height_path)},
        {"path": str(normal_path.relative_to(args.source)), "sha256": sha256(normal_path)},
        {
            "path": str(landscape_shader_path.relative_to(args.source)),
            "sha256": sha256(landscape_shader_path),
        },
    ]
    for relative, stem in sorted(TEXTURES.items()):
        origin = args.source / relative
        if not origin.is_file():
            raise SystemExit(f"missing texture source {origin}")
        target = map_dir / f"{stem}.png"
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(origin, target)
        sources.append({"path": relative, "sha256": sha256(origin)})
    sources.append(
        {"path": str(scene_path.relative_to(args.source)), "sha256": sha256(scene_path)}
    )

    objects = export_objects(args.source, scene, resources, dump_dir)

    light = find_node(nodes, "DirectionalLight3D", ".")
    sky = find_node(nodes, "sky", ".")
    border_min = find_node(nodes, "map_border_min", ".")
    border_max = find_node(nodes, "map_border_max", ".")

    shader = {
        key.split("/", 1)[1]: value
        for key, value in terrain["props"].items()
        if key.startswith("shader_params/")
    }

    sky_transform = node_transform(nodes, sky)
    sky_origin = np.asarray(sky_transform["origin"], dtype=np.float64)
    sky_faces = []
    sky_slots = set()
    for face in nodes:
        if face["parent"] != "sky" or face["type"] != "MeshInstance3D":
            continue
        material = scene.sub_block(face["props"]["material_override"])
        texture_value = material["props"].get("albedo_texture")
        if texture_value is None:
            raise SystemExit(f"sky face {face['name']} has no texture")
        relative = scene.ext_relative(texture_value)
        stem = TEXTURES.get(relative)
        if stem is None:
            raise SystemExit(f"sky texture is not exported: {relative}")
        transform = node_transform(nodes, face)
        direction = np.asarray(transform["origin"], dtype=np.float64) - sky_origin
        direction[2] *= -1.0
        axis = int(np.argmax(np.abs(direction)))
        positive = direction[axis] >= 0.0
        slot = (
            ("right" if positive else "left") if axis == 0 else
            ("up" if positive else "down") if axis == 1 else
            ("front" if positive else "back")
        )
        if slot in sky_slots:
            raise SystemExit(f"sky has two {slot} faces")
        sky_slots.add(slot)
        sky_faces.append(
            {
                "name": face["name"],
                "slot": slot,
                "resource": f"Maps/{MAP_DIRECTORY}/{stem}",
                "source": relative,
                "basis": transform["basis"],
                "origin": transform["origin"],
            }
        )
    expected_sky = {"left", "right", "up", "front", "back"}
    if sky_slots != expected_sky:
        raise SystemExit(f"sky faces are {sky_slots}, expected {expected_sky}")
    sky_faces.sort(key=lambda item: item["name"])

    manifest = {
        "id": MAP_ID,
        "nameKey": f"map:{MAP_ID}/name",
        "scene": str(scene_path.relative_to(args.source)),
        "terrain": {
            "resource": "Maps/Rudniki/terrain_height",
            "colorMap": "Maps/Rudniki/colormap",
            "tileMask": "Maps/Rudniki/tilemask",
            "tileTexture": "Maps/Rudniki/tiletextures",
            "side": side,
            "step": step,
            "minHeight": float(heights.min()),
            "maxHeight": float(heights.max()),
            "extent": step * (side - 1),
            "mapScale": parse_vector(terrain["props"]["map_scale"]),
            "normalSource": str(normal_path.relative_to(args.source)),
            "surfaceNormal": surface_normal,
            "collisionEnabled": terrain["props"].get("collision_enabled") == "true",
            "castShadow": int(terrain["props"].get("cast_shadow", 1)),
            "colorMapSize": parse_vector(shader["color_map_size"]),
            "groundUvScale": parse_vector(shader["u_ground_uv_scale_per_texture"]),
            "tileTexScale": float(shader["tiletex_scale"]),
            "tileColour0": parse_color(shader["tile_col_0"]),
            "tileColour1": parse_color(shader["tile_col_1"]),
            "tileColour2": parse_color(shader["tile_col_2"]),
            "tileColour3": parse_color(shader["tile_col_3"]),
            "detailStrength": float(shader["detail_strength"]),
            "detailContrast": float(shader["detail_contrast"]),
            "brightness": float(shader["brightness"]),
            "contrast": float(shader.get("contrast", 1.0)),
            "maskSharpness": float(shader["mask_sharpness"]),
            "sha256": sha256(height_asset),
            "bytes": height_asset.stat().st_size,
        },
        "border": {
            "min": node_transform(nodes, border_min)["origin"],
            "max": node_transform(nodes, border_max)["origin"],
        },
        "directionalLight": {
            **node_transform(nodes, light),
            "energy": float(light["props"].get("light_energy", 1.0)),
        },
        "sky": {
            "basis": sky_transform["basis"],
            "origin": sky_origin.tolist(),
            "faces": sky_faces,
        },
        "minimap": "Maps/Rudniki/minimap",
        "minimapArea": minimap_area(
            args.source / "assets/gfx/ui/map_minimaps/rudniki_Out.png",
            node_transform(nodes, border_min)["origin"],
            node_transform(nodes, border_max)["origin"],
        ),
        "loadingScreen": "Maps/Rudniki/loading_screen",
        "spawns": {
            "teamA": collect_spawners(nodes, "teamA"),
            "teamB": collect_spawners(nodes, "teamB"),
        },
        "aiMarkers": collect_ai_markers(nodes, "ai_markers"),
        "objects": objects,
        "sources": sources,
    }

    out = resources / "Data" / f"map_{MAP_ID}.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    print(f"terrain side={side} step={step} bytes={height_asset.stat().st_size}")
    print(f"height range {float(heights.min())} .. {float(heights.max())}")
    print(
        f"spawns teamA={len(manifest['spawns']['teamA'])}"
        f" teamB={len(manifest['spawns']['teamB'])}"
        f" aiMarkers={len(manifest['aiMarkers'])}"
    )
    print(f"manifest={out} bytes={out.stat().st_size}")
    print(
        f"objects={objects['count']} skipped={objects['skipped']}"
        f" meshes={objects['meshes']} shapes={objects['shapes']}"
        f" materials={objects['materials']} multimeshes={objects['multiMeshes']}"
        f" billboards={objects['billboards']} textures={objects['textures']}"
    )


if __name__ == "__main__":
    main()
