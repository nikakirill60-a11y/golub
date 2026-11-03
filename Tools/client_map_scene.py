"""Binary export of a Blitz client map scene (4.8.0 or 11.20) with its materials.

The OBJ path of port_blitz_client_map.py keeps only positions, the first UV
set and one albedo per batch, which is enough for maps that the port shades
with its own lighting. A map ported "1 in 1" needs what the client renders
with: the second UV set its lightmaps are baked into, vertex colours and
SpeedTree pivots, tangents for the water, and the effect of every material -
lightmap scale and offset, flat colour, UV animation, alpha test, blending,
culling and fog - exactly as the `.material` templates and the material
hierarchy of the scene author them.

The output is three resources next to each other:

* `<id>_scene.json` - the counts and destructibles, as the OBJ path writes;
* `<id>_scene_layout.json` - the materials and the named nodes;
* `<id>_scene_meshes.bytes` - the vertex streams and indices of every node.

Nodes keep the OBJ group names (`entity_<n>_<name>[_intact|_wreck]`), so the
runtime binds destructibles and colliders to them the same way.

Coordinates: DAVA is right-handed with Z up, Unity left-handed with Y up, so a
DAVA point (x, y, z) is the Unity point (x, z, y). That swap is a reflection,
hence every triangle whose transform keeps its handedness is rewound.

An 11.20 scene adds what the 4.8 maps never author, each written only when a
scene has it so the 4.8 layouts stay byte for byte:

* materials from the 11.20 `.material` documents with their presets, lit
  (Blinn-Phong) and decal effects, vertex colour and a per-material fog
  colour, and the client-built material of the map border ribbon;
* `noCollision` on nodes whose entity has no CollisionTypeComponent - the
  11.20 client collides only with what that component tags;
* `skeletons`: hard-skinned props split into one node per joint, with the
  joints' bind pose and the clips of `<id>_scene_animations.bytes`;
* `vegetation`: the HIGH VegetationRenderObject as the pattern meshes of
  `<id>_scene_vegetation.bytes` the client would generate at load time.
"""
from __future__ import annotations

import json
import math
import posixpath
import re
import struct
from pathlib import Path

import numpy as np
import yaml

MAGIC = b"BLTZMAP1"

# DAVA eVertexFormat bits and the bytes each one occupies in a vertex.
VERTEX_STREAMS = (
    (0, "position", "<f4", 3),
    (1, "normal", "<f4", 3),
    (2, "color", "u1", 4),
    (3, "uv0", "<f4", 2),
    (4, "uv1", "<f4", 2),
    (5, "uv2", "<f4", 2),
    (6, "uv3", "<f4", 2),
    (7, "tangent", "<f4", 3),
    (8, "binormal", "<f4", 3),
    (9, "hard_joint", "<f4", 1),
    (10, "pivot", "<f4", 4),
    (11, "pivot_deprecated", "<f4", 3),
    (12, "flexibility", "<f4", 1),
    (13, "angle_sin_cos", "<f4", 2),
    (14, "joint_index", "<f4", 4),
    (15, "joint_weight", "<f4", 4),
)

# CustomPropertiesComponent `CollisionType` of breakable props (3 house/prop
# with a wreck switch state, 4 small prop) and of vegetation (8).
BREAKABLE_COLLISION_TYPES = (3, 4)
VEGETATION_COLLISION_TYPE = 8
# Vegetation `FallType` 3 is a bush a tank drives through; every other value
# the 4.8.0 maps author on CollisionType 8 (0 trees and firs, 1 palms) is a
# tree that topples when a tank drives into it.
BUSH_FALL_TYPE = 3
# The baseline the port gives a client-breakable prop without authored Health.
DEFAULT_PROP_HEALTH = 10


# RenderObject::VISIBLE. A render object authored without it is never drawn
# by the client, but its geometry still collides: Lost Temple's 22
# `invisiblewall` planes (CollisionType 6) keep tanks off the cliff edges.
RENDER_OBJECT_VISIBLE = 1


def is_hidden(port, entity) -> bool:
    """Whether the client never draws the entity's render object."""
    for component in port.components(entity, "RenderComponent"):
        render_object = component.get("rc.renderObj")
        if isinstance(render_object, dict) and "ro.flags" in render_object:
            return not int(render_object["ro.flags"]) & RENDER_OBJECT_VISIBLE
    return False


def is_falling_tree(props: dict, toppling: tuple[int, ...] = ()) -> bool:
    """Whether the client topples this entity under a tank.

    Vegetation (CollisionType 8) falls unless it is a bush; `toppling` names
    the other collision types a map's config knocks over whole, such as
    Karieri's cacti (CollisionType 5).
    """
    try:
        collision_type = int(float(props.get("CollisionType")))
    except (TypeError, ValueError):
        return False
    if collision_type in toppling:
        return True
    fall = props.get("FallType", props.get("FallingType"))
    if collision_type != VEGETATION_COLLISION_TYPE or fall is None:
        return False
    try:
        return int(float(fall)) != BUSH_FALL_TYPE
    except (TypeError, ValueError):
        return False


# Attribute flags of a node in the bytes payload.
HAS_NORMAL = 1
HAS_UV1 = 2
HAS_COLOR = 4
HAS_TANGENT = 8
HAS_BILLBOARD = 16
# Four joint indices (int32) and four weights (float32) a vertex: a
# soft-skinned prop the runtime draws with a SkinnedMeshRenderer.
HAS_SKIN = 32

# Unity render queues for the DAVA render layers the scene uses.
LAYER_QUEUES = {
    "OpaqueRenderLayer": 2000,
    "AfterOpaqueRenderLayer": 2499,
    "AlphaTestLayer": 2450,
    "TransclucentRenderLayer": 3000,
    "ShadowVolumeRenderLayer": 2000,
    # 11.20 names.
    "AlphaTestRenderLayer": 2450,
    "VegetationRenderLayer": 2000,
}

# The render object kinds an 11.20 scene draws in code rather than as batches.
MAP_BORDER_KIND = "MapBorderRenderObject"
SKINNED_KIND = "SkinnedMesh"
VEGETATION_KIND = "VegetationRenderObject"

# rhi::PrimitiveType of a PolygonGroup: 1 a triangle list, 2 a strip.
PRIMITIVE_TRIANGLE_STRIP = 2

# The quality an 11.20 port draws, as QualitySettingsComponent names it.
PORT_QUALITY = "HIGH"

DAVA_BLEND = {
    "BLEND_ZERO": 0,
    "BLEND_ONE": 1,
    "BLEND_SRC_ALPHA": 5,
    "BLEND_ONE_MINUS_SRC_ALPHA": 10,
}


def decode_vertices(group: dict) -> dict[str, np.ndarray]:
    """Every stream of a DAVA PolygonGroup as numpy arrays.

    The streams are interleaved in bit order, so the vertex format alone
    spells the record; the stride check catches a format this table does not
    know rather than silently reading garbage.
    """
    vertex_format = int(group["vertexFormat"])
    count = int(group["vertexCount"])
    blob = bytes(group["vertices"])
    fields = []
    for bit, name, dtype, width in VERTEX_STREAMS:
        if vertex_format >> bit & 1:
            fields.append((name, dtype, (width,)))
    unknown = vertex_format >> 16
    if unknown:
        raise SystemExit(f"unknown vertex format bits {vertex_format:#x}")
    record = np.dtype(fields)
    if count and len(blob) != record.itemsize * count:
        raise SystemExit(
            f"vertex format {vertex_format} expects {record.itemsize} bytes a vertex, "
            f"the group holds {len(blob) / max(count, 1):.2f}")
    data = np.frombuffer(blob, dtype=record, count=count)
    return {name: np.array(data[name]) for name, _, _ in fields}


def strip_triangles(strip: np.ndarray) -> np.ndarray:
    """A triangle strip as a list: every other triangle turns the other way.

    The degenerate triangles that stitch strips together are dropped.
    """
    if strip.size < 3:
        return np.zeros(0, dtype=np.int64)
    a, b, c = strip[:-2], strip[1:-1], strip[2:]
    odd = (np.arange(a.size) % 2) == 1
    triangles = np.stack([np.where(odd, b, a), np.where(odd, a, b), c], axis=1)
    keep = ((triangles[:, 0] != triangles[:, 1]) & (triangles[:, 1] != triangles[:, 2])
            & (triangles[:, 0] != triangles[:, 2]))
    return triangles[keep].reshape(-1)


def decode_indices(group: dict) -> np.ndarray:
    count = int(group["indexCount"])
    dtype = "<u2" if int(group.get("indexFormat", 0)) == 0 else "<u4"
    indices = np.frombuffer(bytes(group["indices"]), dtype=dtype, count=count).astype(np.int64)
    if int(group.get("rhi_primitiveType", 1)) == PRIMITIVE_TRIANGLE_STRIP:
        return strip_triangles(indices)
    return indices


class MaterialTemplates:
    """The `.material` effects of the client, resolved to the HIGH quality."""

    def __init__(self, pack: Path):
        self.root = pack / "from_apk/Data/Materials"
        self.cache: dict[str, dict] = {}

    def load(self, reference: str) -> dict:
        name = reference.split("/")[-1]
        if name in self.cache:
            return self.cache[name]
        text = (self.root / name).read_text(encoding="utf-8").replace("\r", "")
        document = yaml.safe_load(text)
        template = document.get("MaterialTemplate")
        if template:
            # The client renders maps at HIGH; ULTRA_HIGH is the real
            # reflection water the 4.8 mobile client never enables.
            resolved = self.load(template["HIGH"])
            self.cache[name] = resolved
            return resolved
        technique = document["RenderTechnique"]
        render_pass = technique["RenderPass"]
        state = render_pass.get("RenderState") or {}
        flags = {flag.strip() for flag in str(state.get("state", "")).split("|")}
        resolved = {
            "effect": name,
            "layer": technique["Layers"][0],
            "shader": render_pass.get("Shader", ""),
            "defines": list(render_pass.get("UniqueDefines") or []),
            "blend": "STATE_BLEND" in flags,
            "src": DAVA_BLEND.get(state.get("blendSrc"), 1),
            "dst": DAVA_BLEND.get(state.get("blendDest"), 0),
            "depthWrite": "STATE_DEPTH_WRITE" in flags,
            "cull": "STATE_CULL" in flags and state.get("cullMode") == "FACE_BACK",
        }
        self.cache[name] = resolved
        return resolved


def material_chain(materials: dict[int, dict], material_id: int) -> list[dict]:
    """The material, its parents and the global material, nearest first."""
    chain = []
    seen = set()
    current = material_id
    while current is not None and current not in seen:
        seen.add(current)
        node = materials.get(int(current))
        if node is None:
            break
        chain.append(node)
        current = node.get("parentMaterialKey")
    return chain


def merged_material(materials: dict[int, dict], material_id: int, prop) -> dict:
    """Flags, properties and textures as DAVA resolves them: nearest wins.

    A flag set to zero on a child switches the parent's flag off, so flags
    are merged by presence rather than by truth.
    """
    flags: dict[str, int] = {}
    props: dict[str, list[float]] = {}
    textures: dict[str, str] = {}
    presets: dict[str, bool] = {}
    effect = None
    for node in material_chain(materials, material_id):
        for key, value in (node.get("flags") or {}).items():
            flags.setdefault(key, int(value))
        # 11.20: presets of the material document an NMaterial switches on.
        for key, value in (node.get("enabledPresets") or {}).items():
            presets.setdefault(str(key), is_enabled(value))
        for key in (node.get("properties") or {}):
            props.setdefault(key, prop(node, key))
        for key, value in (node.get("textures") or {}).items():
            textures.setdefault(key, str(value))
        if effect is None and node.get("fxName"):
            effect = str(node["fxName"])
    return {"flags": flags, "props": props, "textures": textures, "effect": effect,
            "presets": sorted(name for name, enabled in presets.items() if enabled)}


def is_enabled(value) -> bool:
    if isinstance(value, str):
        return value.strip().lower() not in ("", "0", "false")
    return bool(value)


def rounded(values, digits: int = 6) -> list[float]:
    return [round(float(v), digits) for v in values]


class SceneExporter:
    def __init__(self, port, config, scene, pack: Path, map_dir: Path):
        self.p = port
        self.config = config
        self.scene = scene
        self.pack = pack
        self.map_dir = map_dir
        self.source_dir = pack / "3d/Maps" / config.client
        # An 11.20 pack reads the presets of its material documents, fogs
        # every material the scene's FogComponent reaches unless it opts
        # out, and tags what collides.
        self.v1120 = getattr(config, "client_version", "4.8") == "11.20"
        if self.v1120:
            import client_map_1120
            self.templates = client_map_1120.MaterialTemplates(pack)
        else:
            self.templates = MaterialTemplates(pack)
        self.groups = {port.dava.group_id(group): group for group in scene["geometry"]}
        self.materials = {
            int.from_bytes(node["#id"], "little"): node
            for node in scene.get("dataNodes", [])
            if node.get("##name") == "NMaterial" and node.get("#id")
        }
        self.material_records: list[dict] = []
        self.material_index: dict[int, int] = {}
        self.record_index: dict[str, int] = {}
        self.textures: dict[str, str] = {}
        self.nodes: list[dict] = []
        self.payload: list[bytes] = []
        self.counts = {"batches": 0, "vertices": 0, "triangles": 0}
        self.decode_cache: dict[int, tuple] = {}
        self.skeletons: list[dict] = []
        self.clips: list[tuple[float, list[dict]]] = []
        self.vegetation: dict | None = None
        self.vegetation_clusters: list[int] = []

    # ---------------------------------------------------------------- textures
    def texture(self, reference: str | None, role: str) -> str:
        if not reference:
            return ""
        source = self.p.resolve_source(self.source_dir, reference)
        if source is None:
            raise SystemExit(f"{self.config.client} references missing texture {reference}")
        key = f"{role}:{source.relative_to(self.pack)}"
        if key in self.textures:
            return self.textures[key]
        stem = re.sub(r"[^A-Za-z0-9_]+", "_", source.name.split(".")[0])
        suffix = {"cubemap": "_cube", "alphamask": "_mask"}.get(role, "")
        name = f"{len(self.textures):03d}_{stem}{suffix}"
        destination = self.map_dir / "Textures" / f"{name}.png"
        if role == "cubemap":
            write_cubemap(source, destination)
        else:
            if not self.p.decode_texture(source, destination):
                raise SystemExit(f"cannot decode client texture {source}")
            if role == "alphamask":
                write_mask(destination)
        resource = f"Maps/{self.config.directory}/Textures/{name}"
        self.textures[key] = resource
        return resource

    # --------------------------------------------------------------- materials
    def material(self, material_id: int) -> int:
        if material_id in self.material_index:
            return self.material_index[material_id]
        merged = merged_material(self.materials, material_id, self.p.material_property)
        if not merged["effect"]:
            raise SystemExit(f"material {material_id} has no effect in its hierarchy")
        if self.v1120:
            template = self.templates.load(merged["effect"], presets=merged["presets"])
        else:
            template = self.templates.load(merged["effect"])
        flags, props, textures = merged["flags"], merged["props"], merged["textures"]
        defines = set(template["defines"])
        shader = template["shader"].split("/")[-1]

        def value(name, width, fallback):
            found = props.get(name) or []
            if len(found) < width:
                return list(fallback)
            return rounded(found[:width])

        if self.v1120:
            kind = template.get("kind")
            if kind is None:
                raise SystemExit(f"material {material_id} uses the shader {template['shader']}, "
                                 "which the 11.20 port does not draw")
        elif shader == "shadowvolume":
            kind = "shadow"
        elif "SKYOBJECT" in defines:
            kind = "sky"
        elif shader == "water":
            kind = "water"
        else:
            kind = "surface"

        # A decal multiplies the albedo by a second texture on the second UV
        # set, as a lightmap does, without a scale or an offset of its own.
        decal = self.v1120 and "MATERIAL_DECAL" in defines
        lightmap = "MATERIAL_LIGHTMAP" in defines
        flat = bool(flags.get("FLATCOLOR"))
        # 4.8 fogs what VERTEX_FOG flags; 11.20 fogs every material but the
        # sky unless a material switches VERTEX_FOG off.
        fogged = bool(flags.get("VERTEX_FOG", self.v1120)) and not (self.v1120 and kind == "sky")
        record = {
            "name": f"{len(self.material_records):03d}_{merged['effect'].split('/')[-1].replace('.material', '')}",
            "effect": template["effect"].replace(".material", ""),
            "kind": kind,
            "queue": LAYER_QUEUES[template["layer"]],
            "cull": template["cull"],
            "zWrite": template["depthWrite"],
            "blend": template["blend"],
            "srcBlend": template["src"] if template["blend"] else 1,
            "dstBlend": template["dst"] if template["blend"] else 0,
            "alphaTest": "ALPHATEST" in defines,
            "cutoff": value("alphatestThreshold", 1, [0.5])[0],
            "fog": fogged and kind != "shadow",
            "albedo": self.texture(textures.get("albedo"), "albedo") if kind != "shadow" else "",
            "lightmap": self.texture(textures.get("lightmap"), "lightmap") if lightmap else "",
            "lightmapST": (value("uvScale", 2, [1, 1]) + value("uvOffset", 2, [0, 0])) if lightmap else [1, 1, 0, 0],
            "alphamask": self.texture(textures.get("alphamask"), "alphamask") if "ALPHA_MASK" in defines else "",
            "flatColor": value("flatColor", 4, [1, 1, 1, 1]) if flat else [1, 1, 1, 1],
            "texShift": value("texture0Shift", 2, [0, 0]) if flags.get("TEXTURE0_SHIFT_ENABLED") else [0, 0],
            "texShiftSpeed": value("tex0ShiftPerSecond", 2, [0, 0]) if flags.get("TEXTURE0_ANIMATION_SHIFT") else [0, 0],
            "speedTree": "SPEED_TREE_OBJECT" in defines,
            "leafColorMul": value("treeLeafColorMul", 4, [0.5, 0.5, 0.5, 0.5]),
            "leafOcclusion": value("treeLeafOcclusionMul", 1, [0.5]) + value("treeLeafOcclusionOffset", 1, [0.0]),
            "wind": bool(flags.get("WIND_ANIMATION")),
            "flowmap": self.texture(textures.get("flowmap"), "flowmap") if "FLOWMAP_SKY" in defines else "",
            "flow": value("flowAnimSpeed", 1, [0.0]) + value("flowAnimOffset", 1, [0.0]),
            "normalmap": "",
            "cubemap": "",
            "normalScale": [1, 1],
            "normal0Shift": [0, 0],
            "normal1Shift": [0, 0],
            "fresnel": [0, 1],
            "reflectionTint": [1, 1, 1],
        }
        if kind == "water":
            record.update({
                "albedo": "",
                "normalmap": self.texture(textures.get("normalmap"), "normalmap"),
                "cubemap": self.texture(textures.get("cubemap"), "cubemap"),
                "normalScale": value("normal0Scale", 1, [1]) + value("normal1Scale", 1, [1]),
                "normal0Shift": value("normal0ShiftPerSecond", 2, [0, 0]),
                "normal1Shift": value("normal1ShiftPerSecond", 2, [0, 0]),
                "fresnel": value("fresnelBias", 1, [0]) + value("fresnelPow", 1, [1]),
                "reflectionTint": value("reflectionTintColor", 3, [1, 1, 1]),
            })
        if decal:
            record["lightmap"] = self.texture(textures.get("decal"), "lightmap")
        if self.v1120:
            # Written only when set, so the record keeps the 4.8 shape.
            if flags.get("VERTEX_COLOR") or "VERTEX_COLOR" in defines:
                record["vertexColor"] = True
            if len(props.get("fogColor") or []) >= 3:
                record["fogColor"] = value("fogColor", 3, [1, 1, 1])
            if kind == "lit":
                record["glossiness"] = value("inGlossiness", 1, [0.5])[0]
                record["specularity"] = (value("inSpecularity", 1, [1.0])[0]
                                         if flags.get("SPECULAR") else 0.0)
        if flat and record["flatColor"] == [1.0, 0.0, 0.0, 1.0]:
            # The global material's placeholder; a real flat colour is never
            # pure red in this scene, so reaching it means a broken chain.
            raise SystemExit(f"material {material_id} inherits the global flatColor placeholder")
        if lightmap and record["lightmapST"][:2] == [0.0, 0.0]:
            raise SystemExit(f"lightmap material {material_id} has no uvScale of its own")
        index = self.register(record)
        self.material_index[material_id] = index
        return index

    def register(self, record: dict) -> int:
        # Every lightmapped instance owns an NMaterial of its own, most of
        # them with the same values; one Unity material per distinct record
        # keeps the batching the client's shared materials would give.
        key = json.dumps({k: v for k, v in record.items() if k != "name"}, sort_keys=True)
        index = self.record_index.get(key)
        if index is None:
            index = len(self.material_records)
            record["name"] = f"{index:04d}_{record['effect']}"
            self.material_records.append(record)
            self.record_index[key] = index
        return index

    def batch_material(self, material_id: int, kind: str) -> int:
        if kind == MAP_BORDER_KIND:
            import client_map_1120
            return self.register(dict(client_map_1120.BORDER_MATERIAL))
        return self.material(material_id)

    # ----------------------------------------------------------------- geometry
    def batch_geometry(self, matrix, group_id: int, material: dict):
        group = self.groups.get(group_id)
        if group is None:
            return None
        streams = decode_vertices(group)
        indices = decode_indices(group)
        if "position" not in streams or indices.size < 3:
            return None
        count = streams["position"].shape[0]
        m = np.array(matrix, dtype=np.float64).reshape(4, 4)
        linear = m[:3, :3]
        translation = m[:3, 3]
        local = streams["position"].astype(np.float64)
        billboard = None
        if "pivot" in streams:
            pivot = streams["pivot"].astype(np.float64)
            weight = pivot[:, 3:4]
            anchored = local + (pivot[:, :3] - local) * weight
            offset = local - anchored
            scale = np.linalg.norm(linear, axis=0)
            billboard = (offset[:, :2] * scale[:2]).astype(np.float32)
            local = anchored
        if material["kind"] == "sky":
            # The client strips the translation from the sky's matrix and
            # pins it to the far plane, so it is kept around the origin.
            world = local @ linear.T
        else:
            world = local @ linear.T + translation
        positions = world[:, [0, 2, 1]].astype(np.float32)

        normal_matrix = np.linalg.inv(linear).T if abs(np.linalg.det(linear)) > 1e-12 else linear

        def direction(values):
            turned = values.astype(np.float64) @ normal_matrix.T
            length = np.linalg.norm(turned, axis=1, keepdims=True)
            length[length == 0] = 1.0
            return (turned / length)[:, [0, 2, 1]].astype(np.float32)

        normals = direction(streams["normal"]) if "normal" in streams else None
        tangents = None
        if "tangent" in streams:
            turned = streams["tangent"].astype(np.float64) @ linear.T
            length = np.linalg.norm(turned, axis=1, keepdims=True)
            length[length == 0] = 1.0
            tangents = np.concatenate(
                [(turned / length)[:, [0, 2, 1]], np.ones((count, 1))], axis=1).astype(np.float32)
        triangles = indices[: indices.size - indices.size % 3].reshape(-1, 3)
        # DAVA -> Unity swaps two axes; a transform that keeps handedness
        # therefore mirrors the triangle and its winding has to be reversed.
        if np.linalg.det(linear) > 0:
            triangles = triangles[:, [0, 2, 1]]
        return {
            "count": count,
            "position": positions,
            "normal": normals,
            "uv0": streams.get("uv0"),
            "uv1": streams.get("uv1"),
            "color": streams.get("color"),
            "tangent": tangents,
            "billboard": billboard,
            "triangles": triangles,
        }

    def entity_origin(self, entity) -> list[float]:
        """The Unity position of an entity's own origin - a tree's foot."""
        m = np.array(self.p.matrix_of(entity), dtype=np.float64).reshape(4, 4)
        return [float(m[0, 3]), float(m[2, 3]), float(m[1, 3])]

    def add_node(self, name: str, entity, batches, hidden: bool = False,
                 no_collision: bool = False) -> bool:
        matrix = self.p.matrix_of(entity)
        parts = []
        for group_id, material_id, kind in batches:
            index = self.batch_material(material_id, kind)
            geometry = self.batch_geometry(matrix, group_id, self.material_records[index])
            if geometry is not None:
                parts.append((index, geometry))
        return self.emit_node(name, parts, hidden, no_collision)

    def emit_node(self, name: str, parts, hidden: bool = False, no_collision: bool = False) -> bool:
        """Write one node of (material index, geometry) parts."""
        if not parts:
            return False
        total = sum(g["count"] for _, g in parts)
        flags = 0
        for key, bit in (("normal", HAS_NORMAL), ("uv1", HAS_UV1), ("color", HAS_COLOR),
                         ("tangent", HAS_TANGENT), ("billboard", HAS_BILLBOARD),
                         ("bones", HAS_SKIN)):
            if any(g.get(key) is not None for _, g in parts):
                flags |= bit

        def stream(key, width, fill, dtype=np.float32):
            chunks = []
            for _, g in parts:
                data = g.get(key)
                if data is None:
                    data = np.tile(np.array(fill, dtype=dtype), (g["count"], 1))
                chunks.append(np.asarray(data, dtype=dtype).reshape(g["count"], width))
            return np.concatenate(chunks).astype(dtype).tobytes()

        blob = [struct.pack("<3i", total, flags, len(parts))]
        blob.append(stream("position", 3, [0, 0, 0]))
        if flags & HAS_NORMAL:
            blob.append(stream("normal", 3, [0, 1, 0]))
        blob.append(stream("uv0", 2, [0, 0]))
        if flags & HAS_UV1:
            blob.append(stream("uv1", 2, [0, 0]))
        if flags & HAS_COLOR:
            blob.append(stream("color", 4, [255, 255, 255, 255], np.uint8))
        if flags & HAS_TANGENT:
            blob.append(stream("tangent", 4, [1, 0, 0, 1]))
        if flags & HAS_BILLBOARD:
            blob.append(stream("billboard", 2, [0, 0]))
        if flags & HAS_SKIN:
            blob.append(stream("bones", 4, [0, 0, 0, 0], np.int32))
            blob.append(stream("weights", 4, [1, 0, 0, 0]))
        base = 0
        submeshes = []
        lower = np.full(3, np.inf)
        upper = np.full(3, -np.inf)
        for index, g in parts:
            triangles = (g["triangles"] + base).astype("<i4")
            blob.append(struct.pack("<2i", index, triangles.size))
            blob.append(triangles.tobytes())
            submeshes.append(index)
            base += g["count"]
            if self.material_records[index]["kind"] != "sky":
                lower = np.minimum(lower, g["position"].min(axis=0))
                upper = np.maximum(upper, g["position"].max(axis=0))
            self.counts["batches"] += 1
            self.counts["vertices"] += g["count"]
            self.counts["triangles"] += int(g["triangles"].shape[0])
        self.payload.append(b"".join(blob))
        self.nodes.append({
            "name": name,
            "vertexCount": total,
            "attributes": flags,
            "materials": submeshes,
            "min": rounded(lower if np.isfinite(lower).all() else [0, 0, 0], 4),
            "max": rounded(upper if np.isfinite(upper).all() else [0, 0, 0], 4),
        })
        if hidden:
            # Only written when set, so every map without such a node keeps
            # its layout byte for byte.
            self.nodes[-1]["hidden"] = True
            self.counts["hidden"] = self.counts.get("hidden", 0) + 1
        if no_collision:
            self.nodes[-1]["noCollision"] = True
            self.counts["noCollision"] = self.counts.get("noCollision", 0) + 1
        return True

    # ------------------------------------------------------------------ skinned
    def soft_geometry(self, streams: dict, group: dict, targets: np.ndarray) -> dict:
        """A soft-skinned batch in the skeleton root's (Unity-swapped) space.

        SkinnedMesh::joint_index addresses the batch's `targetsData`, which
        names the skeleton joint; the weights are normalised the way the
        client's skinning shader sums them.
        """
        count = int(group["vertexCount"])
        local = streams["joint_index"].astype(np.int64)
        local = np.clip(local, 0, max(targets.size - 1, 0))
        bones = targets[local].astype(np.int32)
        weights = streams["joint_weight"].astype(np.float64)
        weights[weights < 0] = 0.0
        total = weights.sum(axis=1, keepdims=True)
        total[total == 0] = 1.0
        weights = (weights / total).astype(np.float32)

        def direction(values):
            length = np.linalg.norm(values, axis=1, keepdims=True)
            length[length == 0] = 1.0
            return (values / length)[:, [0, 2, 1]].astype(np.float32)

        indices = decode_indices(group)
        triangles = indices[: indices.size - indices.size % 3].reshape(-1, 3)
        tangents = None
        if "tangent" in streams:
            tangents = np.concatenate(
                [direction(streams["tangent"]), np.ones((count, 1), np.float32)], axis=1)
        return {
            "count": count,
            "position": streams["position"][:, [0, 2, 1]].astype(np.float32),
            "normal": direction(streams["normal"]) if "normal" in streams else None,
            "uv0": streams.get("uv0"),
            "uv1": streams.get("uv1"),
            "color": streams.get("color"),
            "tangent": tangents,
            "billboard": None,
            "bones": bones,
            "weights": weights,
            "triangles": triangles[:, [0, 2, 1]],
        }

    def add_skinned(self, prefix: str, entity, hidden: bool, no_collision: bool) -> bool:
        """A hard-skinned prop: one node per joint and a skeleton record.

        DAVA moves a hard-skinned vertex by its joint's object-space
        transform after the joint's inverse bind pose. The port bakes the
        inverse bind pose into the vertices, so a node only has to follow
        its joint, and the runtime rebuilds the joints as a hierarchy of
        transforms under the entity and plays the clip on them.
        """
        import client_map_1120 as v
        p = self.p
        skeleton = next(p.components(entity, "SkeletonComponent"), None)
        if skeleton is None:
            raise SystemExit(f"{entity.get('name')} is a SkinnedMesh without a SkeletonComponent")
        joints = []
        for position, node in enumerate(v.items(skeleton.get("joints"))):
            parent = int(node.get("joint.parentIndex", v.NO_PARENT))
            parent = -1 if parent == v.NO_PARENT else parent
            if parent >= position:
                raise SystemExit(f"{entity.get('name')} lists joint {position} before its parent")
            joints.append({
                "name": str(node.get("joint.name") or f"joint{position}"),
                "uid": str(node.get("joint.uid") or ""),
                "parent": parent,
                "bind": v.JointTransform.from_matrix(node["joint.bindPose"]),
                "inverse": v.JointTransform.from_matrix(node["joint.invBindPose"]),
            })
        if not joints:
            raise SystemExit(f"{entity.get('name')} has a skeleton without joints")

        render_object = next(
            (c.get("rc.renderObj") for c in p.components(entity, "RenderComponent")
             if (c.get("rc.renderObj") or {}).get("##name") == SKINNED_KIND), None)
        by_joint: dict[int, list] = {}
        soft: list = []
        spanning = 0
        for index, batch in enumerate(v.items(render_object.get("ro.batches"))):
            if not isinstance(batch, dict) or int(render_object.get(f"rb{index}.lodIndex", -1)) not in (-1, 0):
                continue
            group = self.groups.get(int(batch.get("rb.datasource", -1)))
            if group is None:
                continue
            material = self.material(int(batch["rb.nmatname"]))
            streams = decode_vertices(group)
            targets = np.frombuffer(bytes(render_object[f"skinnedObject.batch{index}.targetsData"]),
                                    dtype="<u4").astype(np.int64)
            if "hard_joint" not in streams:
                if "joint_index" not in streams or "joint_weight" not in streams:
                    raise SystemExit(f"{entity.get('name')} batch {index} has no joint streams")
                soft.append((material, self.soft_geometry(streams, group, targets)))
                continue
            vertex_joint = targets[streams["hard_joint"][:, 0].astype(np.int64)]
            indices = decode_indices(group)
            triangles = indices[: indices.size - indices.size % 3].reshape(-1, 3)
            triangle_joint = vertex_joint[triangles[:, 0]]
            across = int(np.count_nonzero(
                (vertex_joint[triangles[:, 1]] != triangle_joint)
                | (vertex_joint[triangles[:, 2]] != triangle_joint)))
            if across:
                # A hard-skinned batch whose triangles straddle joints (the
                # World of Ducks buoy's rope) cannot be cut into rigid
                # nodes; it is drawn skinned with one full weight a vertex.
                hard = streams["hard_joint"][:, 0]
                streams = dict(streams)
                streams["joint_index"] = np.stack(
                    [hard, np.zeros_like(hard), np.zeros_like(hard), np.zeros_like(hard)], axis=1)
                streams["joint_weight"] = np.tile(
                    np.array([1, 0, 0, 0], np.float32), (hard.size, 1))
                soft.append((material, self.soft_geometry(streams, group, targets)))
                continue
            for joint in sorted(set(triangle_joint.tolist())):
                chosen = triangles[triangle_joint == joint]
                used, remapped = np.unique(chosen, return_inverse=True)
                transform = joints[joint]["inverse"]
                local = transform.apply(streams["position"][used].astype(np.float64))

                def direction(values, transform=transform, used=used):
                    turned = transform.rotate(values[used].astype(np.float64))
                    length = np.linalg.norm(turned, axis=1, keepdims=True)
                    length[length == 0] = 1.0
                    return (turned / length)[:, [0, 2, 1]].astype(np.float32)

                count = used.size
                tangents = None
                if "tangent" in streams:
                    tangents = np.concatenate(
                        [direction(streams["tangent"]), np.ones((count, 1), np.float32)], axis=1)
                # The (x, z, y) swap mirrors the joint space; the joint
                # transforms above it keep their handedness.
                by_joint.setdefault(joint, []).append((material, {
                    "count": count,
                    "position": local[:, [0, 2, 1]].astype(np.float32),
                    "normal": direction(streams["normal"]) if "normal" in streams else None,
                    "uv0": streams["uv0"][used] if "uv0" in streams else None,
                    "uv1": streams["uv1"][used] if "uv1" in streams else None,
                    "color": streams["color"][used] if "color" in streams else None,
                    "tangent": tangents,
                    "billboard": None,
                    "triangles": remapped.reshape(-1, 3)[:, [0, 2, 1]],
                }))
        if spanning:
            raise SystemExit(f"{entity.get('name')} has {spanning} triangles across joints")
        parts = []
        for joint in sorted(by_joint):
            node = f"{prefix}_{re.sub(r'[^A-Za-z0-9_]+', '_', joints[joint]['name'])}"
            if self.emit_node(node, by_joint[joint], hidden, no_collision):
                parts.append({"node": node, "joint": joint})
        skins = []
        # A soft-skinned mesh bends between joints, so a pose-bound collider
        # would be wrong the moment it plays; the client gives these props
        # (sea kale, buoys, fins, ducks) no CollisionType either.
        if soft and self.emit_node(f"{prefix}_skin", soft, hidden, True):
            skins.append(f"{prefix}_skin")
        if not parts and not skins:
            return False

        timing = v.motion_timing(p, entity)
        clip = -1
        duration = 0.0
        if timing["clip"]:
            relative = posixpath.normpath(f"3d/Maps/{self.config.client}/{timing['clip']}")
            loaded = v.read_clip(self.pack / relative)
            duration = float(loaded["duration"])
            clip = len(self.clips)
            self.clips.append((duration, v.clip_channels(loaded, joints)))

        m = np.array(p.matrix_of(entity), dtype=np.float64).reshape(4, 4)
        linear = m[:3, :3]
        if np.linalg.det(linear) <= 0:
            raise SystemExit(f"{entity.get('name')} is a mirrored skinned prop")
        scale = np.linalg.norm(linear, axis=0)
        quaternion = v.matrix_quaternion(linear / scale)
        self.skeletons.append({
            "name": prefix,
            "origin": rounded(v.unity_point(m[:3, 3]), 5),
            "rotation": rounded(v.unity_quaternion(quaternion), 7),
            "scale": rounded([scale[0], scale[2], scale[1]], 6),
            "joints": [{
                "name": joint["name"],
                "uid": joint["uid"],
                "parent": joint["parent"],
                "position": rounded(v.unity_point(joint["bind"].position), 6),
                "rotation": rounded(v.unity_quaternion(joint["bind"].orientation), 7),
                "scale": round(joint["bind"].scale, 6),
                **({"inverseBind": unity_matrix(joint["inverse"])} if skins else {}),
            } for joint in joints],
            "parts": parts,
            **({"skins": skins} if skins else {}),
            "clip": clip,
            "duration": round(duration, 6),
            "playbackRate": round(timing["playbackRate"], 6),
            "autoplay": timing["autoplay"],
            "delay": round(timing["delay"], 6),
            "delayVariation": round(timing["delayVariation"], 6),
            "repeats": timing["repeats"],
        })
        return True

    # --------------------------------------------------------------- vegetation
    def add_vegetation(self, entity, render_object: dict, stem: str) -> None:
        """The small clusters of a VegetationRenderObject, as the client lays them.

        The client generates one pattern of clusters per resolution when the
        map loads and repeats it over every density cell; the port writes
        those pattern meshes with a fixed seed, and the runtime places,
        lifts and fades them the way grass-vp does.
        """
        import client_map_1120 as v
        if self.vegetation is not None:
            raise SystemExit(f"{entity.get('name')} is a second vegetation at {PORT_QUALITY}")
        chunk_set = (render_object.get("vro.geometryData") or {}).get("cgd.chunkSet") or {}
        if "cgd.materialId" not in chunk_set:
            raise SystemExit(f"{entity.get('name')} has no vegetation geometry")
        merged = merged_material(self.materials, int(chunk_set["cgd.materialId"]),
                                 self.p.material_property)
        template = self.templates.load(merged["effect"], presets=merged["presets"])
        world = rounded(merged["props"].get("worldSize") or [], 6)
        if len(world) < 3 or world[0] <= 0:
            raise SystemExit(f"{entity.get('name')} has no worldSize")
        unit = world[0] / v.DENSITY_MAP_SIZE
        layers = v.vegetation_layers(chunk_set)
        limits = [int(limit) for limit in render_object["vro.clusterLayerLimit"]]
        if any(limit > 0 for limit in limits[len(layers):]):
            raise SystemExit(f"{entity.get('name')} limits more layers than it has variations")
        clusters = v.generate_clusters(limits, render_object["vro.rotationVariation"],
                                       render_object["vro.scaleVariation"], unit)
        meshes = []
        for resolution in range(len(v.RESOLUTION_SCALE)):
            cells = v.resolution_cells(clusters, resolution)
            self.vegetation_clusters.append(sum(len(cell) for cell in cells))
            meshes.append(v.resolution_mesh(layers, cells))
        density = np.frombuffer(bytes(render_object["vro.flippedDensityMap"]), dtype=np.uint8)
        side = int(round(math.sqrt(density.size)))
        if side * side != density.size or side != v.DENSITY_MAP_SIZE:
            raise SystemExit(f"{entity.get('name')} has a {density.size}-cell density map")
        # Row y of the density map is DAVA cell row y counted from the south
        # edge (-worldSize / 2), which is the Unity -Z edge.
        density = (density.reshape(side, side) != 0).astype(np.uint8)
        (self.map_dir / f"{stem}_vegetation.bytes").write_bytes(v.encode_vegetation(density, meshes))
        visibility = rounded(render_object["vro.visibilityDistance"], 6)
        flags = merged["flags"]
        self.vegetation = {
            "payload": f"Maps/{self.config.directory}/{stem}_vegetation",
            "albedo": self.texture(merged["textures"].get("albedo"), "albedo"),
            "colorMap": self.texture(str(render_object.get("vro.lightmap") or "")
                                     or merged["textures"].get("vegetationmap"), "colormap"),
            "queue": LAYER_QUEUES[template["layer"]],
            "cull": template["cull"],
            "worldSize": world[:3],
            "unit": round(unit, 6),
            "pattern": round(unit * v.PATTERN_CELLS, 6),
            "cameraBias": round(float(render_object.get("vro.cameraBias", 0.0)), 6),
            "ranges": rounded(v.vegetation_ranges(render_object["vro.lodRanges"], unit, visibility[0]), 6),
            "visibility": visibility[:2],
            "baseColorMult": round(float(render_object.get("vro.baseColorMultilplier", 1.0)), 6),
            "fog": bool(flags.get("VERTEX_FOG", True)),
            "densitySide": side,
        }

    # -------------------------------------------------------------------- walk
    def export(self, stem: str) -> dict:
        p = self.p
        destructibles = []
        trees = []
        entity_count = 0

        def as_int(value, fallback=0):
            try:
                return int(float(value))
            except (TypeError, ValueError):
                return fallback

        def kept(batches):
            return [b for b in batches if self.groups.get(b[0]) is not None]

        def visible(batches):
            return [b for b in batches
                    if not p.is_invisible(p.inherited_material(self.materials, b[1]))]

        def is_dropped(entity) -> bool:
            # MapConfig.dropped_props and the tutorial-tagged props, exactly
            # as the OBJ path applies them, before entities are numbered.
            return p.is_dropped_prop(entity, self.config)

        def render_kinds(entity) -> dict:
            return {str((c.get("rc.renderObj") or {}).get("##name") or ""): c.get("rc.renderObj")
                    for c in p.components(entity, "RenderComponent")}

        def skipped_quality(entity) -> bool:
            # QualitySettingsSystem shows an entity only at the quality its
            # group is set to: the 11.20 port draws HIGH.
            setting = next(p.components(entity, "QualitySettingsComponent"), None)
            return (self.v1120 and setting is not None
                    and str(setting.get("requiredQuality")) != PORT_QUALITY)

        def walk(entity):
            nonlocal entity_count
            if is_dropped(entity) or skipped_quality(entity):
                return
            kinds = render_kinds(entity)
            # The 4.8 grass is generated from a mask the port never reads;
            # 11.20 ships the geometry of its clusters with the scene.
            if self.v1120 and VEGETATION_KIND in kinds:
                self.add_vegetation(entity, kinds[VEGETATION_KIND], stem)
            # 11.20 collides only with what a CollisionTypeComponent tags.
            no_collision = (self.v1120
                            and next(p.components(entity, "CollisionTypeComponent"), None) is None)
            props = p.custom_properties(entity)
            health = max(0, as_int(props.get("Health")))
            collision_type = as_int(props.get("CollisionType"))
            fall_type = as_int(props.get("FallType", props.get("FallingType", 0)))
            switch = next(p.components(entity, "SwitchComponent"), None)
            # A breakable prop the client authors only by its collision type
            # and a wreck switch state, without Health (Mirage's military
            # tents, ammo boxes and concrete blocks), still breaks in 4.8.0.
            # It gets a small baseline health, enough for one shell.
            switch_breakable = (health <= 0 and switch is not None
                                and collision_type in BREAKABLE_COLLISION_TYPES)
            if switch_breakable:
                health = DEFAULT_PROP_HEALTH
            is_destructible = health > 0
            is_tree = is_falling_tree(
                props, tuple(getattr(self.config, "toppling_collision_types", ())))
            intact_state = as_int(switch.get("sc.switchindex"), 0) if switch else 0
            active = kept(list(p.batches_of(entity, intact_state)))
            # Entity numbering follows the OBJ path, which counts only
            # entities with something visible, so names stay comparable.
            if visible(active):
                entity_count += 1
                entity_name = re.sub(r"[^A-Za-z0-9_]+", "_", str(entity.get("name", "mesh")))
                prefix = f"entity_{entity_count}_{entity_name}"
                intact_group = prefix + "_intact" if is_destructible else prefix
                if SKINNED_KIND in kinds:
                    if is_destructible:
                        raise SystemExit(f"{entity.get('name')} is a destructible skinned prop")
                    self.counts["skinned"] = self.counts.get("skinned", 0) + 1
                    added = self.add_skinned(prefix, entity, is_hidden(p, entity), no_collision)
                else:
                    added = self.add_node(intact_group, entity, active,
                                          hidden=is_hidden(p, entity), no_collision=no_collision)
                if added and is_tree and not is_destructible:
                    trees.append({
                        "name": str(entity.get("name", "tree")),
                        "group": intact_group,
                        "origin": rounded(self.entity_origin(entity), 4),
                        "fallType": fall_type,
                    })
                if added and is_destructible:
                    wreck_state = 1 if intact_state == 0 else 0
                    wreck = kept(list(p.batches_of(entity, wreck_state)))
                    wreck_group = None
                    if visible(wreck) and [b[:2] for b in wreck] != [b[:2] for b in active]:
                        if self.add_node(prefix + "_wreck", entity, wreck, no_collision=no_collision):
                            wreck_group = prefix + "_wreck"
                    destructibles.append({
                        "name": str(entity.get("name", "map prop")),
                        "intactGroup": intact_group,
                        "wreckGroup": wreck_group,
                        "health": health,
                        "collisionType": collision_type,
                        "materialKind": as_int(props.get("MaterialKind")),
                        "fallType": fall_type,
                        "collisionTypeCrashed": as_int(props.get("CollisionTypeCrashed")),
                        "healthFromClient": as_int(props.get("Health")),
                    })
            for child in p.items(entity.get("#hierarchy")):
                if isinstance(child, dict):
                    walk(child)

        for root in p.items(self.scene["scene"].get("#hierarchy")):
            if isinstance(root, dict):
                walk(root)

        meshes = self.map_dir / f"{stem}_meshes.bytes"
        meshes.write_bytes(MAGIC + struct.pack("<i", len(self.nodes)) + b"".join(self.payload))
        layout = {
            "format": MAGIC.decode("ascii"),
            "materials": self.material_records,
            "nodes": self.nodes,
        }
        if self.skeletons:
            import client_map_1120
            (self.map_dir / f"{stem}_animations.bytes").write_bytes(
                client_map_1120.encode_clips(self.clips))
            layout["animations"] = f"Maps/{self.config.directory}/{stem}_animations"
            layout["skeletons"] = self.skeletons
        if self.vegetation is not None:
            layout["vegetation"] = self.vegetation
        (self.map_dir / f"{stem}_layout.json").write_text(
            json.dumps(layout, indent=1) + "\n", encoding="utf-8")
        return {
            "format": "client",
            "entities": entity_count,
            "nodes": len(self.nodes),
            "batches": self.counts["batches"],
            "vertices": self.counts["vertices"],
            "triangles": self.counts["triangles"],
            "materials": len(self.material_records),
            "textures": len(self.textures),
            "destructibles": destructibles,
            "trees": trees,
            **({"hidden": self.counts["hidden"]} if self.counts.get("hidden") else {}),
            **({"noCollision": self.counts["noCollision"]} if self.counts.get("noCollision") else {}),
            **({"skinned": self.counts["skinned"]} if self.counts.get("skinned") else {}),
            **({"vegetationClusters": self.vegetation_clusters} if self.vegetation_clusters else {}),
        }


# ------------------------------------------------------------------ textures
def unity_matrix(transform) -> list[float]:
    """A JointTransform as a row-major Unity 4x4 seen through (x, z, y).

    ApplyToPoint is position + R * (point * scale); swapping the y and z rows
    and columns gives the matrix that maps the swapped mesh space the same
    way, which is what a SkinnedMeshRenderer bind pose holds.
    """
    import client_map_1120 as v
    m = np.eye(4)
    m[:3, :3] = v.quaternion_matrix(transform.orientation) * transform.scale
    m[:3, 3] = transform.position
    order = [0, 2, 1, 3]
    return [round(float(x), 7) for x in m[np.ix_(order, order)].reshape(-1)]


def write_mask(path: Path) -> None:
    """Store a DAVA alpha mask as a grey RGBA image with the mask in alpha.

    `FP_A8` reads the one channel of an A8 texture; a decoded A8 PVR lands
    as luminance, an RGBA one keeps it in alpha.
    """
    from PIL import Image
    with Image.open(path) as image:
        if image.mode in ("L", "P"):
            mask = np.array(image.convert("L"))
        else:
            rgba = np.array(image.convert("RGBA"))
            alpha = rgba[:, :, 3]
            mask = alpha if alpha.min() < 255 else rgba[:, :, 0]
    rgba = np.repeat(mask[:, :, None], 4, axis=2)
    Image.fromarray(rgba.astype(np.uint8), "RGBA").save(path)


def write_cubemap(source: Path, destination: Path) -> None:
    """Decode a six-face PVR cubemap into a horizontal +X,-X,+Y,-Y,+Z,-Z strip."""
    import texture2ddecoder
    from PIL import Image
    blob = source.read_bytes()
    if blob[:4] != b"PVR\x03":
        raise SystemExit(f"{source} is not a PVR v3 cubemap")
    (_version, _flags, pixel_format, _space, _channel, height, width, depth,
     surfaces, faces, mips, meta) = struct.unpack_from("<IIQIIIIIIIII", blob, 0)
    if faces != 6:
        raise SystemExit(f"{source} has {faces} faces, not a cubemap")
    offset = 52 + meta
    images = []
    for _face in range(6):
        if pixel_format == 6:  # ETC1
            size = ((width + 3) // 4) * ((height + 3) // 4) * 8
            raw = texture2ddecoder.decode_etc1(blob[offset:offset + size], width, height)
        elif pixel_format == 0x0808080861626772:  # RGBA8888
            size = width * height * 4
            raw = None
        elif pixel_format == 0x0008080800626772:  # RGB888 (World of Ducks)
            size = width * height * 3
            raw = Image.frombytes("RGB", (width, height),
                                  blob[offset:offset + size]).convert("RGBA").tobytes()
        else:
            raise SystemExit(f"unsupported cubemap pixel format {pixel_format:#x} in {source}")
        if raw is None:
            face = Image.frombytes("RGBA", (width, height), blob[offset:offset + size])
        elif pixel_format != 6:
            face = Image.frombytes("RGBA", (width, height), raw)
        else:
            face = Image.frombytes("RGBA", (width, height), raw, "raw", "BGRA")
        images.append(face)
        offset += size
    strip = Image.new("RGBA", (width * 6, height))
    for index, face in enumerate(images):
        strip.paste(face, (index * width, 0))
    destination.parent.mkdir(parents=True, exist_ok=True)
    strip.save(destination)


def export(port, config, scene, pack: Path, map_dir: Path, stem: str) -> dict:
    return SceneExporter(port, config, scene, pack, map_dir).export(stem)
