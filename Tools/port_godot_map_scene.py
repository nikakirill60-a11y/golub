#!/usr/bin/env python3
"""Port a Godot battle map's render scene into the binary map scene format.

The older Godot map port (Tools/port_godot_map.py) rebuilt the props from the
`.tscn` text and lit them with Unity's realtime light, so the map never looked
like Godot draws it: the LightmapGI bake, the vertex-lit trunks, the unshaded
billboard foliage and its distance LODs were all lost. This tool starts from
what the running engine renders instead. Tools/godot_map_scene_dump.gd
instantiates the scene inside Godot and records every visible instance, its
resolved materials, its lightmap rect and slice, the multimeshes and sprites,
the hterrain maps and the bake itself. This script converts that dump into
the same standard binary scene the 4.8 client maps use (BLTZMAP1 layout and
meshes, Unity axes, node bounds), so the map shares their loader and audits.

* Every node is one Godot instance named ``<object>/<path under it>``, the
  object being the gameplay entry of ``map_<id>_objects.json`` it belongs to,
  so the battle can hang the visuals under their colliders. Wrecks and far
  LOD sprites are written hidden, as Godot keeps them.
* Godot -> Unity is the port's usual mirror ``(x, y, -z)``; the mirrored
  camera keeps the screen winding, so triangle order is kept as is.
* UVs keep Godot's top-left convention; the shaders flip ``v`` on sampling.
  The lightmap UV is already placed in its atlas rect (``uv2 * size + pos``).
* Billboards keep their pivot in POSITION, the per-axis keep-scale in
  NORMAL, the owning node's origin in TANGENT and the quad corner in UV2.
* Textures are copied with a suffix the importer reads: ``_srgb`` for
  source_color samplers, ``_raw`` for raw ones, ``_nomip`` for hterrain maps
  Godot samples without mipmaps; the lightmap slices go out as EXR. A
  texture whose ``.import`` keeps it out of VRAM compression (lossless or
  lossy mode) also carries ``_lossless_`` so Unity leaves it uncompressed.

Usage: Tools/port_godot_map_scene.py rudniki [--dump DIR]
"""

from __future__ import annotations

import argparse
import re
import hashlib
import json
import math
import os
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
RES = ROOT / "Assets/Blitz/Resources"
GODOT_PROJECT = Path(os.environ.get(
    "BLITZ_GODOT_PROJECT", str(ROOT.parent / "blitz-legacy-godot")))
GODOT = os.environ.get("GODOT", "godot")
DUMPER = ROOT / "Tools/godot_map_scene_dump.gd"

MAGIC = b"BLTZMAP1"
DUMP_MAGIC = b"BLGS"

# BLTZMAP1 vertex streams (ClientMapScene.cs).
HAS_NORMAL = 1
HAS_UV1 = 2
HAS_COLOR = 4
HAS_TANGENT = 8
HAS_BILLBOARD = 16

# godot_map_scene_dump.gd surface streams.
D_NORMAL = 1
D_TANGENT = 2
D_COLOR = 4
D_UV = 8
D_UV2 = 16
D_INDEX = 32

MAP_OBJECT_SHADER = "res://maps/components/map_object.gdshader"
VEGETATION_SHADER = "res://maps/vegetation/_vegetation_shader.gdshader"
WATER_SHADER = "res://maps/components/water_shader.gdshader"

# Render queues: opaque props, alpha-tested foliage, the LOD sprites Godot
# draws transparent with render_priority -1.
QUEUE_OPAQUE = 2000
QUEUE_CUTOUT = 2450
QUEUE_SPRITE = 2999

MAPS = {
    "rudniki": {
        "scene": "res://maps/rudniki/_rudniki.tscn",
        "folder": "Rudniki",
    },
}


def srgb_to_linear(value: float) -> float:
    """Godot's source_color decode (Color.srgb_to_linear)."""
    if value < 0.04045:
        return value * (1.0 / 12.92)
    return ((value + 0.055) * (1.0 / 1.055)) ** 2.4


def linear_colour(rgba) -> list:
    return [round(srgb_to_linear(float(c)), 6) for c in rgba[:3]] + [round(float(rgba[3]) if len(rgba) > 3 else 1.0, 6)]


def godot_point(points: np.ndarray) -> np.ndarray:
    out = np.array(points, dtype=np.float64, copy=True)
    out[..., 2] *= -1.0
    return out


class Transform:
    """A Godot Transform3D as dumped: basis columns x, y, z, then origin."""

    def __init__(self, values):
        v = [float(x) for x in values]
        self.basis = np.array([v[0:3], v[3:6], v[6:9]], dtype=np.float64).T
        self.origin = np.array(v[9:12], dtype=np.float64)

    def __mul__(self, other: "Transform") -> "Transform":
        result = Transform([0.0] * 12)
        result.basis = self.basis @ other.basis
        result.origin = self.basis @ other.origin + self.origin
        return result

    def points(self, p: np.ndarray) -> np.ndarray:
        return p @ self.basis.T + self.origin

    def normals(self, n: np.ndarray) -> np.ndarray:
        inverse_t = np.linalg.inv(self.basis).T
        out = n @ inverse_t.T
        length = np.linalg.norm(out, axis=1, keepdims=True)
        length[length < 1e-12] = 1.0
        return out / length

    def scale(self) -> np.ndarray:
        return np.linalg.norm(self.basis, axis=0)


class Dump:
    def __init__(self, folder: Path):
        self.folder = folder
        self.scene = json.loads((folder / "scene.json").read_text(encoding="utf-8"))
        self.blob = (folder / "meshes.bin").read_bytes()
        if self.blob[:4] != DUMP_MAGIC or struct.unpack_from("<I", self.blob, 4)[0] != 1:
            raise SystemExit("unexpected meshes.bin header")
        self.meshes = {m["key"]: m for m in self.scene["meshes"]}
        self.materials = {m["key"]: m for m in self.scene["materials"]}

    def surface(self, surface: dict) -> dict:
        offset = surface["offset"]
        count = surface["vertex_count"]
        flags = surface["flags"]
        out = {}

        def take(width, dtype="<f4"):
            nonlocal offset
            size = count * width * 4
            data = np.frombuffer(self.blob, dtype=dtype, count=count * width, offset=offset)
            offset += size
            return data.reshape(count, width).astype(np.float64 if dtype == "<f4" else np.int64)

        out["position"] = take(3)
        out["normal"] = take(3) if flags & D_NORMAL else None
        out["tangent"] = take(4) if flags & D_TANGENT else None
        out["color"] = take(4) if flags & D_COLOR else None
        out["uv"] = take(2) if flags & D_UV else None
        out["uv2"] = take(2) if flags & D_UV2 else None
        if flags & D_INDEX:
            index_count = surface["index_count"]
            out["index"] = np.frombuffer(
                self.blob, dtype="<i4", count=index_count, offset=offset).astype(np.int64)
        else:
            out["index"] = np.arange(count, dtype=np.int64)
        if surface["primitive"] != 3:
            raise SystemExit("only triangle surfaces are supported, got %r" % surface["primitive"])
        return out


def run_dump(scene: str, out: Path) -> None:
    target = GODOT_PROJECT / "_ref_capture/scene_dump.gd"
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(DUMPER, target)
    env = dict(os.environ, BLITZ_MAP_SCENE=scene, BLITZ_SCENE_OUT=str(out))
    subprocess.run(
        [GODOT, "--path", str(GODOT_PROJECT), "--resolution", "640x360",
         "--display-driver", "x11", "-s", "res://_ref_capture/scene_dump.gd"],
        env=env, check=True, timeout=600,
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if not (out / "scene.json").exists():
        raise SystemExit("the Godot scene dump produced nothing")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


class Porter:
    def __init__(self, map_id: str, dump: Dump):
        self.map_id = map_id
        self.dump = dump
        self.folder = MAPS[map_id]["folder"]
        self.map_dir = RES / "Maps" / self.folder
        self.texture_dir = self.map_dir / "Textures"
        self.resource_root = "Maps/%s/Textures/" % self.folder
        table = json.loads((RES / ("Data/map_%s_objects.json" % map_id)).read_text())
        self.objects = {o["name"]: o for o in table["objects"]}
        self.textures: dict = {}
        self.texture_sources: list = []
        self.material_index: dict = {}
        self.material_records: list = []
        self.nodes: list = []
        self.payload: list = []
        self.counts = {"vertices": 0, "triangles": 0, "billboards": 0, "sprites": 0}
        lightmap = dump.scene.get("lightmap") or {}
        self.slices = {s["layer"]: s for s in lightmap.get("slices", []) if s["texture"] == 0}

    # -- textures ---------------------------------------------------------

    def godot_file(self, resource: str) -> Path:
        if not resource.startswith("res://"):
            raise SystemExit("texture is not a project file: %r" % resource)
        return GODOT_PROJECT / resource[len("res://"):]

    def lossless(self, source: Path) -> bool:
        """compress/mode 0 (lossless) and 1 (lossy) stay uncompressed in VRAM."""
        settings = source.with_name(source.name + ".import")
        if not settings.exists():
            return False
        match = re.search(r"^compress/mode=(\d+)", settings.read_text(encoding="utf-8"), re.M)
        return match is not None and int(match.group(1)) in (0, 1)

    def texture(self, resource: str, srgb: bool) -> str:
        key = (resource, srgb)
        if key in self.textures:
            return self.textures[key]
        source = self.godot_file(resource)
        stem = Path(resource).name.split(".")[0]
        name = "%03d_%s_%s%s" % (
            len(self.textures), stem, "lossless_" if self.lossless(source) else "",
            "srgb" if srgb else "raw")
        shutil.copyfile(source, self.texture_dir / (name + ".png"))
        self.textures[key] = self.resource_root + name
        self.texture_sources.append({"path": resource, "sha256": sha256(source)})
        return self.textures[key]

    def lightmap(self, layer: int) -> list:
        info = self.slices[layer]
        name = "lightmap_%02d_hdr" % layer
        shutil.copyfile(self.dump.folder / info["file"], self.texture_dir / (name + ".exr"))
        return [self.resource_root + name, info["width"], info["height"]]

    # -- materials --------------------------------------------------------

    def base_record(self, name: str, kind: str) -> dict:
        return {
            "name": name, "effect": "godot", "kind": kind, "queue": QUEUE_OPAQUE,
            "cull": True, "zWrite": True, "blend": False, "srcBlend": 1, "dstBlend": 0,
            "alphaTest": False, "cutoff": 0.5, "fog": True, "albedo": "", "lightmap": "",
            "speedTree": False, "color": [1.0, 1.0, 1.0, 1.0], "contrast": 1.0,
            "emission": "", "lightmapSize": [0, 0], "uvTransform": [1.0, 1.0, 0.0, 0.0],
            "unshaded": False, "billboard": False, "originOffset": [0.0, 0.0, 0.0],
            "sway": [0.0, 1.0, 1.0], "swayDirection": [0.0, 0.0, 0.0],
            "waterShift": [0.0, 0.0, 0.0, 0.0], "source": "",
        }

    def material(self, key: str, layer: int) -> int:
        lookup = (key, layer)
        if lookup in self.material_index:
            return self.material_index[lookup]
        source = self.dump.materials[key]
        shader = source.get("shader", "")
        params = source.get("params", {})
        index = len(self.material_records)
        label = "%04d" % index
        if shader == MAP_OBJECT_SHADER:
            record = self.base_record(label + "_object", "godot_object")
            texture = params.get("texture_albedo")
            record["albedo"] = self.texture(texture["resource"], True) if texture else ""
            if params.get("emissive") and params.get("texture_emissive"):
                record["emission"] = self.texture(params["texture_emissive"]["resource"], True)
            record["color"] = linear_colour(params.get("albedo", [1, 1, 1, 1]))
            record["contrast"] = float(params.get("contrast", 1.0))
            scale = params.get("uv1_scale", [1, 1, 1])
            offset = params.get("uv1_offset", [0, 0, 0])
            record["uvTransform"] = [scale[0], scale[1], offset[0], offset[1]]
            if layer >= 0:
                resource, width, height = self.lightmap(layer)
                record["lightmap"] = resource
                record["lightmapSize"] = [width, height]
        elif shader == VEGETATION_SHADER:
            record = self.base_record(label + "_vegetation", "godot_vegetation")
            record["albedo"] = self.texture(params["texture_albedo"]["resource"], True)
            record["color"] = linear_colour(params.get("albedo", [1, 1, 1, 1]))
            record["cull"] = False
            record["alphaTest"] = True
            record["cutoff"] = float(params.get("alpha_scissor_threshold", 0.5))
            record["queue"] = QUEUE_CUTOUT
            record["unshaded"] = True
            record["billboard"] = bool(params.get("billboard", True))
            offset = params.get("origin_offset", [0, 2, 0])
            record["originOffset"] = [offset[0], offset[1], -offset[2]]
            record["sway"] = [params.get("sway_amount", 0.05), params.get("sway_scale", 0.5),
                              params.get("sway_duration", 3.0)]
            record["swayDirection"] = list(params.get("sway_direction", [1, 0, 0]))
            scale = params.get("uv1_scale", [1, 1, 1])
            offset = params.get("uv1_offset", [0, 0, 0])
            record["uvTransform"] = [scale[0], scale[1], offset[0], offset[1]]
        elif shader == WATER_SHADER:
            record = self.base_record(label + "_water", "godot_water")
            # mainTexture has no hint: Godot samples it raw.
            record["albedo"] = self.texture(params["mainTexture"]["resource"], False)
            record["color"] = linear_colour(params.get("albedo_color", [1, 1, 1, 1]))
            s1 = params.get("shift1", [0.007, 0.007])
            s2 = params.get("shift2", [0.04, 0.04])
            record["waterShift"] = [s1[0], s1[1], s2[0], s2[1]]
        elif source["class"] == "StandardMaterial3D":
            p = source["properties"]
            if p.get("shading_mode") != 0:
                # Three woodenStack2 wrecks keep Godot's default material:
                # plain white, lit like any other prop (vertex lambert).
                if p.get("transparency", 0) != 0 or p.get("metallic", 0) != 0:
                    raise SystemExit("unsupported lit StandardMaterial3D: " + key)
                record = self.base_record(label + "_default", "godot_object")
                texture = p.get("albedo_texture")
                record["albedo"] = self.texture(texture["resource"], True) if texture else ""
                record["color"] = linear_colour(p.get("albedo_color", [1, 1, 1, 1]))
                record["cull"] = p.get("cull_mode", 0) == 0
                if layer >= 0:
                    raise SystemExit("lightmapped StandardMaterial3D: " + key)
                record["source"] = source["resource"]
                self.material_records.append(record)
                self.material_index[lookup] = index
                return index
            record = self.base_record(label + "_unshaded", "godot_sky")
            record["albedo"] = self.texture(p["albedo_texture"]["resource"], True)
            record["color"] = linear_colour(p.get("albedo_color", [1, 1, 1, 1]))
            record["cull"] = p.get("cull_mode", 0) == 0
            record["fog"] = not p.get("disable_fog", False)
            record["unshaded"] = True
            scale = p.get("uv1_scale", [1, 1, 1])
            offset = p.get("uv1_offset", [0, 0, 0])
            record["uvTransform"] = [scale[0], scale[1], offset[0], offset[1]]
        else:
            raise SystemExit("unsupported material %s (%s)" % (key, shader))
        record["source"] = source["resource"]
        self.material_records.append(record)
        self.material_index[lookup] = index
        return index

    def sprite_material(self, sprite: dict) -> int:
        """SpriteBase3D's generated StandardMaterial3D (get_material_for_2d)."""
        texture = sprite.get("atlas") or sprite["texture"]
        lookup = ("sprite", texture, sprite["billboard"], sprite["double_sided"],
                  sprite["transparent"], sprite["alpha_cut"], sprite["shaded"])
        if lookup in self.material_index:
            return self.material_index[lookup]
        if sprite["shaded"]:
            raise SystemExit("shaded sprites are not supported")
        index = len(self.material_records)
        record = self.base_record("%04d_sprite" % index, "godot_sprite")
        record["albedo"] = self.texture(texture, True)
        record["cull"] = not sprite["double_sided"]
        record["unshaded"] = True
        record["billboard"] = sprite["billboard"] != 0
        if sprite["alpha_cut"] == 0 and sprite["transparent"]:
            record["blend"] = True
            record["zWrite"] = False
            record["srcBlend"] = 5  # SrcAlpha
            record["dstBlend"] = 10  # OneMinusSrcAlpha
            record["queue"] = QUEUE_SPRITE
        else:
            record["alphaTest"] = True
            record["cutoff"] = float(sprite["alpha_scissor_threshold"])
            record["queue"] = QUEUE_CUTOUT
        record["source"] = texture
        self.material_records.append(record)
        self.material_index[lookup] = index
        return index

    # -- nodes ------------------------------------------------------------

    def node_name(self, path: str):
        parts = path.split("/")
        if parts[0] == "scene" and len(parts) >= 3:
            obj = parts[1]
            return obj, "/".join(parts[1:])
        if parts[0] == "static_bg" and len(parts) == 2:
            obj = "static_bg_" + parts[1]
            return obj, obj
        if parts[0] == "sky" and len(parts) == 2:
            return "sky", path
        return None, None

    def role(self, instance: dict, obj: str, name: str) -> str:
        if obj == "sky":
            return "sky"
        owner = instance.get("owner") or {}
        kind = self.objects[obj]["kind"]
        relative = name.split("/", 1)[1] if "/" in name else ""
        if instance["class"] == "Sprite3D":
            return "lod"
        if kind == "destructible":
            return "wreck" if owner.get("role") == "destroyed" else "intact"
        if kind == "tree":
            if relative == "trunk" or relative.startswith("trunk/"):
                return "trunk" if instance["class"] == "MeshInstance3D" else "leaves"
            return "leaves"
        if kind == "vegetation":
            return "leaves"
        return kind

    def emit(self, info: dict, parts: list) -> None:
        total = sum(len(p["position"]) for _, p in parts)
        flags = 0
        for key, bit in (("normal", HAS_NORMAL), ("uv1", HAS_UV1), ("color", HAS_COLOR),
                         ("tangent", HAS_TANGENT), ("billboard", HAS_BILLBOARD)):
            if any(p.get(key) is not None for _, p in parts):
                flags |= bit

        def stream(key, width, fill):
            chunks = []
            for _, p in parts:
                data = p.get(key)
                if data is None:
                    data = np.tile(np.array(fill, dtype=np.float64), (len(p["position"]), 1))
                chunks.append(np.asarray(data, dtype=np.float64).reshape(len(p["position"]), width))
            return np.concatenate(chunks).astype("<f4").tobytes()

        blob = [struct.pack("<3i", total, flags, len(parts))]
        blob.append(stream("position", 3, [0, 0, 0]))
        if flags & HAS_NORMAL:
            blob.append(stream("normal", 3, [0, 1, 0]))
        blob.append(stream("uv0", 2, [0, 0]))
        if flags & HAS_UV1:
            blob.append(stream("uv1", 2, [0, 0]))
        if flags & HAS_COLOR:
            raise SystemExit("vertex colours are not written for Godot scenes")
        if flags & HAS_TANGENT:
            blob.append(stream("tangent", 4, [0, 0, 0, 1]))
        if flags & HAS_BILLBOARD:
            blob.append(stream("billboard", 2, [0, 0]))
        base = 0
        materials = []
        for index, p in parts:
            triangles = (p["index"] + base).astype("<i4")
            blob.append(struct.pack("<2i", index, triangles.size))
            blob.append(triangles.tobytes())
            materials.append(index)
            base += len(p["position"])
            self.counts["triangles"] += triangles.size // 3
        lower = np.min([p["bounds"][0] for _, p in parts], axis=0)
        upper = np.max([p["bounds"][1] for _, p in parts], axis=0)
        self.counts["vertices"] += total
        info.update({
            "vertexCount": int(total),
            "attributes": flags,
            "materials": materials,
            "min": [round(float(x), 4) for x in lower],
            "max": [round(float(x), 4) for x in upper],
        })
        self.nodes.append(info)
        self.payload.append(b"".join(blob))

    def mesh_node(self, instance: dict, info: dict) -> None:
        transform = Transform(instance["transform"])
        mesh = self.dump.meshes[instance["mesh"]]
        user = instance.get("lightmap")
        layer = user["slice"] if user else -1
        parts = []
        for surface, material_key in zip(mesh["surfaces"], instance["surface_materials"]):
            if not material_key:
                raise SystemExit("surface without material on " + instance["path"])
            data = self.dump.surface(surface)
            position = godot_point(transform.points(data["position"]))
            part = {
                "position": position,
                "normal": godot_point(transform.normals(data["normal"])) if data["normal"] is not None else None,
                "uv0": data["uv"] if data["uv"] is not None else np.zeros((len(position), 2)),
                "index": data["index"],
                "bounds": (position.min(axis=0), position.max(axis=0)),
            }
            if user:
                if data["uv2"] is None:
                    raise SystemExit("lightmapped surface without UV2: " + instance["path"])
                rect = user["rect"]
                part["uv1"] = data["uv2"] * np.array(rect[2:4]) + np.array(rect[0:2])
            parts.append((self.material(material_key, layer), part))
        self.emit(info, parts)

    def billboard_part(self, corners: np.ndarray, uvs: np.ndarray, index: np.ndarray,
                       models: list, node_origin: np.ndarray) -> dict:
        """One quad per model matrix: pivot, keep-scale, owner origin, corner."""
        count = len(models)
        per = len(corners)
        position = np.zeros((count * per, 3))
        normal = np.zeros((count * per, 3))
        billboard = np.zeros((count * per, 2))
        uv0 = np.zeros((count * per, 2))
        indices = []
        reach_lo = []
        reach_hi = []
        for i, model in enumerate(models):
            pivot = godot_point(model.origin[None, :])[0]
            scale = model.scale()
            rows = slice(i * per, (i + 1) * per)
            position[rows] = pivot
            normal[rows] = scale
            billboard[rows] = corners[:, :2]
            uv0[rows] = uvs
            indices.append(index + i * per)
            reach = float(np.max(np.abs(corners[:, :2]) * scale[:2])) * 1.5
            reach_lo.append(pivot - reach)
            reach_hi.append(pivot + reach)
        tangent = np.tile(np.append(node_origin, 1.0), (count * per, 1))
        return {
            "position": position, "normal": normal, "uv0": uv0, "tangent": tangent,
            "billboard": billboard, "index": np.concatenate(indices),
            "bounds": (np.min(reach_lo, axis=0), np.max(reach_hi, axis=0)),
        }

    def multimesh_node(self, instance: dict, info: dict) -> None:
        node = Transform(instance["transform"])
        mesh = self.dump.meshes[instance["mesh"]]
        if len(mesh["surfaces"]) != 1:
            raise SystemExit("multimesh with several surfaces: " + instance["path"])
        data = self.dump.surface(mesh["surfaces"][0])
        if np.max(np.abs(data["position"][:, 2])) > 1e-6:
            raise SystemExit("billboard quad is not flat: " + instance["path"])
        models = [node * Transform(item["transform"]) for item in instance["multimesh"]]
        origin = godot_point(node.origin[None, :])[0]
        part = self.billboard_part(data["position"], data["uv"], data["index"], models, origin)
        self.counts["billboards"] += len(models)
        material = self.material(instance["surface_materials"][0], -1)
        if not self.material_records[material]["billboard"]:
            raise SystemExit("non-billboard vegetation is not supported: " + instance["path"])
        self.emit(info, [(material, part)])

    def sprite_node(self, instance: dict, info: dict) -> None:
        sprite = instance["sprite"]
        if sprite["axis"] != 2 or sprite["flip_h"] or sprite["flip_v"] or sprite["region_enabled"]:
            raise SystemExit("unsupported sprite layout: " + instance["path"])
        if sprite["hframes"] != 1 or sprite["vframes"] != 1 or sprite["fixed_size"]:
            raise SystemExit("unsupported sprite frames: " + instance["path"])
        size = sprite["texture_size"]
        px = sprite["pixel_size"]
        offset = sprite["offset"]
        # SpriteBase3D: a centred rect of texture pixels times pixel_size,
        # in the node's XY plane facing +Z.
        x0 = (offset[0] - size[0] * 0.5) * px if sprite["centered"] else offset[0] * px
        y0 = (offset[1] - size[1] * 0.5) * px if sprite["centered"] else offset[1] * px
        x1 = x0 + size[0] * px
        y1 = y0 + size[1] * px
        corners = np.array([[x0, y1, 0.0], [x1, y1, 0.0], [x1, y0, 0.0], [x0, y0, 0.0]])
        if sprite.get("atlas"):
            region = sprite["atlas_region"]
            atlas = sprite["atlas_size"]
            u0, v0 = region[0] / atlas[0], region[1] / atlas[1]
            u1, v1 = (region[0] + region[2]) / atlas[0], (region[1] + region[3]) / atlas[1]
        else:
            u0, v0, u1, v1 = 0.0, 0.0, 1.0, 1.0
        # Top of the rect samples the top of the image (Godot v grows down).
        uvs = np.array([[u0, v0], [u1, v0], [u1, v1], [u0, v1]])
        index = np.array([0, 1, 2, 0, 2, 3], dtype=np.int64)
        transform = Transform(instance["transform"])
        material = self.sprite_material(sprite)
        if self.material_records[material]["billboard"]:
            origin = godot_point(transform.origin[None, :])[0]
            part = self.billboard_part(corners, uvs, index, [transform], origin)
        else:
            position = godot_point(transform.points(corners))
            normal = godot_point(transform.normals(np.tile([0.0, 0.0, 1.0], (4, 1))))
            part = {"position": position, "normal": normal, "uv0": uvs, "index": index,
                    "bounds": (position.min(axis=0), position.max(axis=0))}
        modulate = sprite["modulate"]
        if any(abs(c - 1.0) > 1e-6 for c in modulate[:3]):
            raise SystemExit("tinted sprites are not supported: " + instance["path"])
        self.counts["sprites"] += 1
        self.emit(info, [(material, part)])

    def build(self) -> dict:
        if self.texture_dir.exists():
            shutil.rmtree(self.texture_dir)
        self.texture_dir.mkdir(parents=True)
        skipped = {}
        for instance in self.dump.scene["instances"]:
            obj, name = self.node_name(instance["path"])
            if obj is None:
                skipped[instance["path"]] = "not part of the battle scene"
                continue
            if obj != "sky" and obj not in self.objects:
                raise SystemExit("instance %s has no gameplay object" % instance["path"])
            role = self.role(instance, obj, name)
            visible = bool(instance["visible"])
            if not visible and role not in ("wreck", "lod"):
                skipped[instance["path"]] = "hidden"
                continue
            owner = instance.get("owner") or {}
            info = {"name": name, "object": obj, "role": role, "hidden": not visible}
            if role in ("lod", "leaves") and owner.get("script", "").endswith("map_object_vegetation.gd"):
                # map_object_vegetation.gd swaps sprites for lods by the
                # distance from its own node, which a falling tree carries.
                info["lodGroup"] = owner["path"].split("/", 1)[1]
                info["lodOrigin"] = [round(float(x), 4) for x in godot_point(np.array(owner["origin"]))]
            if instance["class"] == "MeshInstance3D":
                self.mesh_node(instance, info)
            elif instance["class"] == "MultiMeshInstance3D":
                self.multimesh_node(instance, info)
            elif instance["class"] == "Sprite3D":
                self.sprite_node(instance, info)
            else:
                raise SystemExit("unsupported instance class %s" % instance["class"])
        stem = "%s_scene" % self.map_id
        (self.map_dir / (stem + "_meshes.bytes")).write_bytes(
            MAGIC + struct.pack("<i", len(self.nodes)) + b"".join(self.payload))
        layout = {"format": MAGIC.decode("ascii"), "materials": self.material_records, "nodes": self.nodes}
        (self.map_dir / (stem + "_layout.json")).write_text(
            json.dumps(layout, indent=1) + "\n", encoding="utf-8")
        self.terrain()
        roles = {}
        for node in self.nodes:
            roles[node["role"]] = roles.get(node["role"], 0) + 1
        summary = {
            "format": "godot",
            "scene": self.dump.scene["scene"],
            "godot": self.dump.scene["godot"],
            "nodes": len(self.nodes),
            "roles": dict(sorted(roles.items())),
            "materials": len(self.material_records),
            "textures": len(self.textures),
            "lightmapSlices": len(self.slices),
            "lightmapUsers": sum(1 for n in self.nodes if n["role"] != "wreck"
                                 and any(self.material_records[m]["lightmap"] for m in n["materials"])),
            "vertices": self.counts["vertices"],
            "triangles": self.counts["triangles"],
            "billboards": self.counts["billboards"],
            "sprites": self.counts["sprites"],
            "skipped": len(skipped),
        }
        (self.map_dir / (stem + ".json")).write_text(
            json.dumps(summary, indent=2) + "\n", encoding="utf-8")
        return summary

    # -- terrain ----------------------------------------------------------

    def terrain(self) -> None:
        source = self.dump.scene["terrains"][0]
        maps = source["maps"]
        names = {}
        for channel, suffix in (("color", "raw"), ("splat", "raw_nomip"), ("normal", "raw_nomip")):
            name = "terrain_%s_%s" % (channel, suffix)
            shutil.copyfile(self.dump.folder / maps[channel]["file"], self.texture_dir / (name + ".png"))
            names[channel] = self.resource_root + name
        params = source["shader_params"]
        tile = self.godot_file(params["tile_texture"]["resource"])
        tile_name = "terrain_tile_%sraw" % ("lossless_" if self.lossless(tile) else "")
        shutil.copyfile(tile, self.texture_dir / (tile_name + ".png"))
        names["tile"] = self.resource_root + tile_name
        self.texture_sources.append({"path": params["tile_texture"]["resource"], "sha256": sha256(tile)})

        # The drivable height field stays the one the navmesh and the
        # gameplay already use; it has to be the very samples Godot shades.
        raw = (self.dump.folder / maps["height"]["file"]).read_bytes()
        width, height = struct.unpack_from("<II", raw, 0)
        godot_heights = np.frombuffer(raw, dtype="<f4", count=width * height, offset=8)
        definition_path = RES / ("Data/map_%s.json" % self.map_id)
        definition = json.loads(definition_path.read_text())
        field = (RES / (definition["terrain"]["resource"] + ".bytes")).read_bytes()
        side = struct.unpack_from("<i", field, 8)[0]
        ours = np.frombuffer(field, dtype="<f4", count=side * side, offset=28)
        if side != width or side != height or float(np.max(np.abs(ours - godot_heights))) > 1e-4:
            raise SystemExit("terrain_height.bytes no longer matches the Godot height map")

        terrain = definition["terrain"]
        terrain["origin"] = [round(float(x), 6) for x in Transform(source["transform"]).origin]
        terrain["colorMap"] = names["color"]
        terrain["tileMask"] = names["splat"]
        terrain["tileTexture"] = names["tile"]
        terrain["normalMap"] = names["normal"]
        terrain["godot"] = {
            "resolution": int(source["resolution"]),
            "mapScale": source["map_scale"],
            "tileTexScale": params["tiletex_scale"],
            "tileColours": [linear_colour(params["tile_col_%d" % i]) for i in range(4)],
            "detailStrength": params["detail_strength"],
            "detailContrast": params["detail_contrast"],
            "brightness": params["brightness"],
            "contrast": params["contrast"],
            "maskSharpness": params["mask_sharpness"],
        }
        definition["sceneFormat"] = "godot"
        definition["externalScene"] = "Maps/%s/%s_scene" % (self.folder, self.map_id)
        definition["externalSceneSource"] = self.dump.scene["scene"].replace("res://", "")
        # The five sky planes are scene nodes now, drawn by the scene.
        definition["sky"]["faces"] = []
        definition["sceneSources"] = sorted(
            {s["path"]: s for s in self.texture_sources}.values(), key=lambda s: s["path"])
        definition_path.write_text(json.dumps(definition, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("map", choices=sorted(MAPS))
    parser.add_argument("--dump", type=Path, help="reuse an existing godot_map_scene_dump.gd output")
    args = parser.parse_args(argv)
    if args.dump:
        folder = args.dump
    else:
        folder = Path(tempfile.mkdtemp(prefix="blitz_scene_dump_"))
        run_dump(MAPS[args.map]["scene"], folder)
    summary = Porter(args.map, Dump(folder)).build()
    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
