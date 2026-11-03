#!/usr/bin/env python3
"""Offline Panzer War archive inspector/converter. Never executes source scripts."""

from __future__ import annotations
import argparse
import collections
import hashlib
import io
import json
import math
import os
from pathlib import Path, PurePosixPath

try:
    import resource
except ImportError:
    resource = None
import stat
import struct
import sys
import tempfile
import uuid
import zipfile

MAX_INPUT = 256 * 1024 * 1024
MAX_OUTPUT = 512 * 1024 * 1024
MAX_ENTRIES = 4096
ADAPTER = "panzerwar-typetree-ground-v1"


class ConversionError(ValueError):
    """A safe, actionable conversion failure."""


def require(test, message):
    if not test:
        raise ConversionError(message)


def normalized(name):
    require(
        isinstance(name, str)
        and name
        and "\\" not in name
        and ":" not in name
        and "\x00" not in name,
        "Unsafe archive path",
    )
    p = PurePosixPath(name)
    require(
        not p.is_absolute() and all(a not in ("", ".", "..") for a in name.split("/")),
        "Unsafe archive path: " + name,
    )
    return p.as_posix()


def checked_json(data):
    def unique(pairs):
        result = {}
        for k, v in pairs:
            require(k not in result, "Duplicate JSON property: " + k)
            result[k] = v
        return result

    def constant(value):
        raise ConversionError("Non-finite JSON number: " + value)

    return json.loads(data, object_pairs_hook=unique, parse_constant=constant)


def encode(value):
    return json.dumps(
        value,
        ensure_ascii=False,
        allow_nan=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")


def safe_archive(path):
    require(path.stat().st_size <= MAX_INPUT, "Input exceeds 256 MiB")
    result = {}
    total = 0
    with zipfile.ZipFile(path) as z:
        require(len(z.infolist()) <= MAX_ENTRIES, "Too many archive entries")
        seen = set()
        for entry in z.infolist():
            name = normalized(
                entry.filename.rstrip("/") if entry.is_dir() else entry.filename
            )
            require(
                name.casefold() not in seen, "Duplicate/case-colliding path: " + name
            )
            seen.add(name.casefold())
            kind = stat.S_IFMT(entry.external_attr >> 16)
            require(
                kind in (0, stat.S_IFREG, stat.S_IFDIR),
                "Links/special entries are forbidden",
            )
            require(not (entry.flag_bits & 1), "Encrypted ZIP is unsupported")
            if entry.is_dir():
                continue
            total += entry.file_size
            require(total <= MAX_OUTPUT, "Extracted size exceeds 512 MiB")
            require(entry.file_size <= MAX_INPUT, "Entry exceeds 256 MiB")
            if name == "package.json":
                require(entry.file_size <= 1024 * 1024, "Metadata exceeds 1 MiB")
            with z.open(entry) as stream:
                data = stream.read(entry.file_size + 1)
                require(len(data) == entry.file_size, "Entry size mismatch")
            result[name] = data
    require("package.json" in result, "Not a Panzer War package: package.json missing")
    return result


class Archive:
    def __init__(self, path):
        self.path = Path(path)
        self.files = safe_archive(self.path)
        self.meta = checked_json(self.files["package.json"])
        self.guid = str(uuid.UUID(self.meta["guid"]))
        self.sha256 = hashlib.sha256(self.path.read_bytes()).hexdigest()
        for entry in self.meta.get("fileManifests", []):
            require(entry["n"] in self.files, "Missing manifest file: " + entry["n"])
            for dep in entry.get("d", []):
                require(dep in self.files, "Missing bundle dependency: " + dep)
        require(
            any(b.startswith(b"UnityFS\0") for b in self.files.values()),
            "No supported UnityFS bundle (encrypted/legacy package?)",
        )


class Decoder:
    """Serialized object graph only; no Unity runtime or source DLL loading."""

    def __init__(self, archives):
        import UnityPy
        from UnityPy.classes import PPtr

        self.pptr = PPtr
        self.env = UnityPy.Environment()
        self.cache = {}
        self.owners = {}
        for archive in archives:
            for name, data in archive.files.items():
                if data.startswith(b"UnityFS\0"):
                    bundle = self.env.load_file(data, name=archive.guid + "/" + name)
                    for f in bundle.files.values():
                        if hasattr(f, "objects"):
                            self.owners[id(f)] = archive.guid
        self.objects = list(self.env.objects)
        require(len(self.objects) <= 100000, "Serialized object limit exceeded")

    def tree(self, obj):
        key = (id(obj.assets_file), obj.path_id)
        if key not in self.cache:
            try:
                self.cache[key] = obj.read_typetree()
            except Exception as exc:
                raise ConversionError(
                    "Unavailable/stripped type tree: " + obj.type.name
                ) from exc
        return self.cache[key]

    def ref(self, obj, pointer):
        require(
            pointer and pointer.get("m_PathID"), "Missing required object reference"
        )
        try:
            return self.pptr(**pointer, assetsfile=obj.assets_file).deref()
        except Exception as exc:
            raise ConversionError("Unresolved serialized dependency") from exc

    def cls(self, obj):
        if obj.type.name != "MonoBehaviour":
            return obj.type.name
        return self.tree(self.ref(obj, self.tree(obj)["m_Script"]))["m_ClassName"]

    def records(self, cls, owner=None):
        return [
            o
            for o in self.objects
            if (owner is None or self.owners.get(id(o.assets_file)) == owner)
            and o.type.name == "MonoBehaviour"
            and self.cls(o) == cls
        ]

    def components(self, go):
        return [self.ref(go, p["component"]) for p in self.tree(go)["m_Component"]]

    def transform(self, go):
        if go.type.name == "Transform":
            return go
        return next(c for c in self.components(go) if c.type.name == "Transform")

    def world(self, transform, visited=None):
        import numpy as np

        visited = set() if visited is None else visited
        key = (id(transform.assets_file), transform.path_id)
        require(
            key not in visited and len(visited) < 128, "Cyclic/deep transform hierarchy"
        )
        visited.add(key)
        d = self.tree(transform)
        q = d["m_LocalRotation"]
        x, y, z, w = (q[k] for k in "xyzw")
        m = np.array(
            [
                [
                    1 - 2 * y * y - 2 * z * z,
                    2 * x * y - 2 * z * w,
                    2 * x * z + 2 * y * w,
                    0,
                ],
                [
                    2 * x * y + 2 * z * w,
                    1 - 2 * x * x - 2 * z * z,
                    2 * y * z - 2 * x * w,
                    0,
                ],
                [
                    2 * x * z - 2 * y * w,
                    2 * y * z + 2 * x * w,
                    1 - 2 * x * x - 2 * y * y,
                    0,
                ],
                [0, 0, 0, 1],
            ],
            dtype=float,
        )
        for i, a in enumerate("xyz"):
            m[:3, i] *= d["m_LocalScale"][a]
            m[i, 3] = d["m_LocalPosition"][a]
        if d["m_Father"]["m_PathID"]:
            m = self.world(self.ref(transform, d["m_Father"]), visited) @ m
        require(
            np.isfinite(m).all() and abs(np.linalg.det(m[:3, :3])) > 1e-9,
            "Invalid/singular transform",
        )
        return m

    def descendants(self, root):
        todo = [self.transform(root)]
        result = []
        seen = set()
        while todo:
            t = todo.pop()
            key = (id(t.assets_file), t.path_id)
            require(key not in seen and len(seen) < 4096, "Cyclic/oversized hierarchy")
            seen.add(key)
            result.append(self.ref(t, self.tree(t)["m_GameObject"]))
            todo.extend(self.ref(t, p) for p in reversed(self.tree(t)["m_Children"]))
        return result


class Glb:
    def __init__(self):
        self.data = bytearray()
        self.doc = {
            "asset": {"version": "2.0", "generator": ADAPTER},
            "buffers": [],
            "bufferViews": [],
            "accessors": [],
            "meshes": [],
            "nodes": [],
            "scenes": [{"nodes": []}],
            "scene": 0,
        }

    def accessor(self, values, width, integer=False):
        import numpy as np

        values = np.asarray(values, dtype="<u4" if integer else "<f4").reshape(
            -1, width
        )
        require(
            len(values) > 0 and np.isfinite(values).all(),
            "Empty/non-finite mesh attribute",
        )
        while len(self.data) % 4:
            self.data.append(0)
        offset = len(self.data)
        raw = values.tobytes()
        self.data.extend(raw)
        view = len(self.doc["bufferViews"])
        self.doc["bufferViews"].append(
            {"buffer": 0, "byteOffset": offset, "byteLength": len(raw)}
        )
        a = {
            "bufferView": view,
            "componentType": 5125 if integer else 5126,
            "count": len(values),
            "type": {1: "SCALAR", 2: "VEC2", 3: "VEC3"}[width],
        }
        if width == 3:
            a.update(min=values.min(axis=0).tolist(), max=values.max(axis=0).tolist())
        self.doc["accessors"].append(a)
        return len(self.doc["accessors"]) - 1

    def mesh(self, name, vertices, normals, uv, triangles):
        attrs = {
            "POSITION": self.accessor(vertices, 3),
            "NORMAL": self.accessor(normals, 3),
            "TEXCOORD_0": self.accessor(uv, 2),
        }
        mesh = len(self.doc["meshes"])
        self.doc["meshes"].append(
            {
                "name": name,
                "primitives": [
                    {
                        "attributes": attrs,
                        "indices": self.accessor(triangles, 1, True),
                        "mode": 4,
                    }
                ],
            }
        )
        node = len(self.doc["nodes"])
        self.doc["nodes"].append({"name": name, "mesh": mesh})
        self.doc["scenes"][0]["nodes"].append(node)

    def bytes(self):
        self.doc["buffers"] = [{"byteLength": len(self.data)}]
        text = encode(self.doc)
        text += b" " * ((-len(text)) % 4)
        self.data.extend(b"\0" * ((-len(self.data)) % 4))
        size = 12 + 8 + len(text) + 8 + len(self.data)
        return (
            struct.pack("<III", 0x46546C67, 2, size)
            + struct.pack("<II", len(text), 0x4E4F534A)
            + text
            + struct.pack("<II", len(self.data), 0x004E4942)
            + self.data
        )


def positive(value, label, zero=False):
    require(
        isinstance(value, (int, float))
        and math.isfinite(value)
        and (value >= 0 if zero else value > 0),
        "Invalid/missing " + label,
    )
    return value


def shell_kind(cls, data):
    """SDK Core.dll warhead constants; never infer shell semantics from display names."""
    if cls == "PenetrateBullet":
        warhead = data.get("warhead")
        require(
            warhead in (1, 2, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14),
            "Unknown penetrating warhead",
        )
        return 1 if warhead in (6, 10, 12, 13) else 0
    if cls == "HEATBullet":
        require(data.get("heatBulletWarhead") in (1000, 1001), "Unknown HEAT warhead")
        return 2
    if cls == "ExplosionBullet":
        require(
            data.get("explosionWarhead") in (3001, 3002),
            "Unsupported explosive warhead (nuclear/unknown)",
        )
        return 4 if data["explosionWarhead"] == 3002 else 3
    raise ConversionError("Unsupported shell: " + cls)


def build_vehicle(d, info, manager, archive, overrides, approximations):
    import numpy as np
    from UnityPy.helpers.MeshHelper import MeshHandler

    inf = d.tree(info)
    mgr = d.tree(manager)
    vid = str(uuid.UUID(inf["assetGuid"]))
    name = "pw_" + uuid.UUID(archive.guid).hex + "_" + uuid.UUID(vid).hex
    matches = [
        o
        for o in d.records("VehicleTextData", archive.guid)
        if d.tree(o).get("AssetName") == inf.get("vehicleName")
    ]
    require(len(matches) == 1, "Ambiguous vehicle text data")
    dynamics = matches[0]
    text = d.tree(dynamics)
    require("PSParameter" in text, "Unsupported vehicle dynamics schema")
    ps, ptc = text["PSParameter"], text["PTCParameter"]
    tf, mt = text["TFParameter"], text["MTParameter"]
    tf_owner = mt_owner = dynamics
    for pointer_key, kind in [
        ("sharedGunData", "TFParameter"),
        ("sharedTurretData", "MTParameter"),
    ]:
        ptr = text.get(pointer_key, {})
        if ptr.get("m_PathID"):
            shared_owner = d.ref(dynamics, ptr)
            shared = d.tree(shared_owner)
            if kind == "TFParameter":
                tf = shared.get(kind, shared)
                tf_owner = shared_owner
            else:
                mt = shared.get(kind, shared)
                mt_owner = shared_owner
    if ptc.get("sharedEngineData", {}).get("m_PathID"):
        ptc = dict(
            ptc,
            **{
                k: v
                for k, v in d.tree(d.ref(dynamics, ptc["sharedEngineData"])).items()
                if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")
            },
        )
    blocked = []
    for k in (
        "MultiFireSystemData",
        "simpleVehicleFireSystemParameters",
        "tankFireTransformParameters",
        "vehicleLuaBehaviors",
        "buffRefs",
    ):
        if text.get(k):
            blocked.append(k)
    for label, container in [("fire", tf), ("turret", mt)]:
        if container.get("vehicleLuaBehaviors") or container.get("buffRefs"):
            blocked.append(label + " behaviors")
    for k in (
        "MechParameter",
        "CarControllerParameter",
        "LuaVehicleControllerParameter",
        "ShipControllerParameter",
    ):
        flags = {
            a: v
            for a, v in text.get(k, {}).items()
            if a in ("isMech", "isCar", "isShip", "isLua", "isLuaControllerEnabled")
        }
        if any(flags.values()):
            blocked.append(k)
    if tf.get("fireState", 0) != 0:
        blocked.append("non-simple fireState")
    if mt.get("isNonSymmetricOverrideRanges") or mt.get("overrideRanges"):
        blocked.append("dynamic gun ranges")
    require(not blocked, "Unsupported active gameplay: " + ", ".join(blocked))
    for obj, ptr, label in [
        (mt_owner, mt.get("actionData", {}), "turret action"),
        (tf_owner, tf.get("fireActionData", {}), "fire action"),
    ]:
        if ptr.get("m_PathID"):
            require(d.cls(d.ref(obj, ptr)) == "TDTurretAction", "Unsupported " + label)
    require(
        approximations,
        "Native physics/material/audio/module adaptations require --allow-approximations",
    )
    power = overrides.get(vid, {}).get("enginePower")
    require(
        power is not None,
        "Missing horsepower: provide --overrides JSON with "
        + vid
        + " / enginePower; engine torque is not horsepower",
    )
    shells = []
    for ptr in tf["bulletData"]:
        obj = d.ref(tf_owner, ptr)
        b = d.tree(obj)
        cls = d.cls(obj)
        require(
            cls in ("PenetrateBullet", "HEATBullet", "ExplosionBullet"),
            "Unsupported shell: " + cls,
        )
        shells.append(
            {
                "name": b["m_Name"],
                "type": shell_kind(cls, b),
                "dmg": positive(b["damage"], "damage"),
                "pen": positive(b["penetration"], "penetration", True),
                "speed": positive(b["velocity"], "velocity"),
                "caliber": positive(b["caliber"], "caliber"),
                "explosion_radius": b.get("ExplosionRange", 0),
            }
        )
    require(0 < len(shells) <= 16, "Unsupported shell count")
    gun = {
        "name": name + "_gun",
        "reload": positive(tf["ReloadTime"], "reload"),
        "max_ammo": positive(tf["AmmoCount"], "ammo"),
        "caliber": shells[0]["caliber"],
        "disp": positive(tf["fireSpread"]["dispersion"], "dispersion", True),
        "aim_time": positive(tf["fireSpread"]["aimTime"], "aim time"),
        "rot_speed": positive(mt["gunDegreesPerSecond"], "gun speed"),
        "deviation_up": {"x": abs(mt["UpMaxDegree"]), "y": abs(mt["DownMaxDegree"])},
        "deviation_r": {
            "x": abs(mt.get("maxTurretPositiveAngle", mt["maxTurretAngle"]))
            if mt.get("isNonSymmetricTurret")
            else abs(mt["maxTurretAngle"]),
            "y": abs(mt.get("maxTurretNegativeAngle", mt["maxTurretAngle"]))
            if mt.get("isNonSymmetricTurret")
            else abs(mt["maxTurretAngle"]),
        },
        "shells": shells,
    }
    card = {
        "name": name,
        "display_name": inf["defaultName"],
        "display_name_short": inf["defaultName"],
        "localized_names": {
            "ru": next(
                (
                    a["localizedName"]
                    for a in inf.get("localizedNames", [])
                    if a["lang"] == 2
                ),
                inf["defaultName"],
            )
        },
        "health": positive(ps["Health"], "health"),
        "turret_health": 0,
        "lvl": max(1, min(10, inf["rank"])),
        "tank_class": overrides.get(vid, {}).get("tankClass", ps["vehicleType"]),
        "spot_radius": ps["viewRange"],
        "hull_weight": positive(ptc["Mass"], "mass"),
        "speed_limits": {
            "x": positive(ptc["maxSpeed"], "speed"),
            "y": abs(positive(abs(ptc["minSpeed"]), "reverse speed", True)),
        },
        "turret_rot_speed": positive(mt["turretDegreesPerSecond"], "traverse"),
        "turret_speed_radians": math.radians(
            positive(mt["turretDegreesPerSecond"], "traverse")
        ),
        "guns": [gun],
        "engines": [
            {"name": name + "_engine", "power": positive(power, "override enginePower")}
        ],
        "tracks": [
            {
                "name": name + "_tracks",
                "rot_speed": positive(ptc["maxAngularVelocity"], "hull traverse"),
            }
        ],
    }
    require(card["tank_class"] in (0, 1, 2, 3), "Invalid tankClass override")
    glb = Glb()
    nodes = {}
    plates = []
    textures = {}
    vertices_total = 0
    # SDK +Z-forward -> legacy OBJ-space: a 180-degree Y rotation.
    target = np.diag([-1.0, 1.0, -1.0, 1.0])
    to_glb = np.diag([1.0, 1.0, -1.0, 1.0])  # runtime mirrors X and reverses winding
    root = d.ref(manager, mgr["m_GameObject"])
    root_inverse = np.linalg.inv(d.world(d.transform(root)))

    def world(go):
        return root_inverse @ d.world(d.transform(go))

    def point(go):
        return (target @ world(go))[:3, 3].tolist()

    for key, n, parent in [
        ("TurretGameObject", "turret_01", "hull"),
        ("GunGameObject", "gun_01", "turret_01"),
        ("FFPoint", "gun_01_shoot_pos", "gun_01"),
    ]:
        go = d.ref(manager, mgr[key])
        pos = point(go)
        nodes[n] = {"parent": parent, "world": pos, "local": pos}
    nodes["hull"] = {"parent": "", "world": [0, 0, 0], "local": [0, 0, 0]}
    for n in ("turret_01", "gun_01", "gun_01_shoot_pos"):
        parent = nodes[n]["parent"]
        nodes[n]["local"] = [
            a - b for a, b in zip(nodes[n]["world"], nodes[parent]["world"])
        ]
    rig = {
        "turret": "turret_01",
        "gun": "gun_01",
        "muzzle": "gun_01_shoot_pos",
        "turrets": {
            "turret_01": {"guns": ["gun_01"], "muzzles": {"gun_01": "gun_01_shoot_pos"}}
        },
    }
    mounts = {
        d.ref(manager, mgr["TurretGameObject"]).path_id: "turret_01",
        d.ref(manager, mgr["GunGameObject"]).path_id: "gun_01",
    }

    def mount(go):
        t = d.transform(go)
        guard = set()
        while True:
            go_obj = d.ref(t, d.tree(t)["m_GameObject"])
            if go_obj.path_id in mounts:
                return mounts[go_obj.path_id]
            if not d.tree(t)["m_Father"]["m_PathID"]:
                return "hull"
            require(t.path_id not in guard, "Cyclic mount")
            guard.add(t.path_id)
            t = d.ref(t, d.tree(t)["m_Father"])

    def mesh(go, mesh_obj, n):
        nonlocal vertices_total
        m = mesh_obj.read()
        h = MeshHandler(m)
        h.process()
        v = np.asarray(h.m_Vertices, dtype=float)[:, :3]
        vertices_total += len(v)
        require(vertices_total <= 250000, "Vertex budget exceeded")
        matrix = to_glb @ world(go)
        v = (np.c_[v, np.ones(len(v))] @ matrix.T)[:, :3]
        normal = np.asarray(h.m_Normals, dtype=float)[:, :3]
        normal = normal @ np.linalg.inv(matrix[:3, :3])
        lengths = np.linalg.norm(normal, axis=1)
        normal /= np.maximum(lengths[:, None], 1e-20)
        uv = np.asarray(h.m_UV0, dtype=float)[:, :2]
        uv[:, 1] = 1 - uv[:, 1]
        tri = np.asarray([t for group in h.get_triangles() for t in group], dtype=int)
        require(
            tri.ndim == 2
            and tri.shape[1] == 3
            and tri.min() >= 0
            and tri.max() < len(v),
            "Invalid triangles",
        )
        if np.linalg.det(matrix[:3, :3]) < 0:
            tri = tri[:, ::-1]
        glb.mesh(n, v, normal, uv, tri)
        return int(len(tri))

    def component(go, kind):
        return next((c for c in d.components(go) if c.type.name == kind), None)

    renderer_ids = {p["m_PathID"] for p in mgr["renderers"]}
    visual_count = 0
    physics_count = 0
    moving_counts = collections.Counter()
    for go in d.descendants(root):
        for c in d.components(go):
            if c.type.name == "MonoBehaviour":
                require(
                    d.cls(c)
                    in (
                        "VehicleComponentsReferenceManager",
                        "HitBox",
                        "InternalModule",
                    ),
                    "Unsupported attached behavior: " + d.cls(c),
                )
            require(
                c.type.name != "SkinnedMeshRenderer",
                "Skinned geometry needs a skinning adapter",
            )
        renderer = component(go, "MeshRenderer")
        mf = component(go, "MeshFilter")
        if (
            renderer
            and mf
            and renderer.path_id in renderer_ids
            and not any(
                d.cls(c) in ("HitBox", "InternalModule")
                for c in d.components(go)
                if c.type.name == "MonoBehaviour"
            )
        ):
            n = "visible_" + str(visual_count) + "_" + d.tree(go)["m_Name"]
            n = n.replace("armor", "plate").replace("gun_mask", "mask")
            original = d.tree(go)["m_Name"]
            if original in ("LeftTrack", "RightTrack"):
                n = {"LeftTrack": "trackL", "RightTrack": "trackR"}[original]
            elif original.startswith("wheel"):
                n = original
            moving = mount(go)
            if moving in ("gun_01", "turret_01"):
                index = moving_counts[moving]
                moving_counts[moving] += 1
                n = moving if index == 0 else moving + "_sheet" + str(index)
            # Name controls body/track material selection in the native renderer.
            mat_refs = d.tree(renderer)["m_Materials"]
            require(len(mat_refs) == 1, "Multi-material renderer needs an adapter")
            mat = d.ref(renderer, mat_refs[0])
            md = d.tree(mat)
            saved = md["m_SavedProperties"]["m_TexEnvs"]
            texprops = dict(saved)
            tp = next(
                (
                    texprops[k]["m_Texture"]
                    for k in ("_BaseMap", "_MainTex")
                    if k in texprops and texprops[k]["m_Texture"]["m_PathID"]
                ),
                None,
            )
            require(tp is not None, "Missing diffuse texture")
            tex = d.ref(mat, tp)
            image = tex.read()
            require(
                image.m_Width <= 4096 and image.m_Height <= 4096,
                "Texture dimension limit exceeded",
            )
            texture_key = (id(tex.assets_file), tex.path_id)
            if texture_key not in textures:
                require(
                    sum(isinstance(k, tuple) for k in textures) < 2,
                    "More than two diffuse materials need an adapter",
                )
                buffer = io.BytesIO()
                image.image.save(buffer, format="PNG")
                textures[texture_key] = buffer.getvalue()
            role = "tracks" if "track" in d.tree(go)["m_Name"].lower() else "body"
            require(
                role not in textures or textures[role] == textures[texture_key],
                "Different diffuse textures for one native material role need an adapter",
            )
            textures[role] = textures[texture_key]
            mesh(go, d.ref(mf, d.tree(mf)["m_Mesh"]), n)
            pos = point(go)
            if n not in nodes:
                nodes[n] = {"parent": mount(go), "local": pos, "world": pos}
            visual_count += 1
        collider = component(go, "MeshCollider")
        if collider and d.tree(collider).get("m_Convex"):
            mesh(
                go,
                d.ref(collider, d.tree(collider)["m_Mesh"]),
                "physics_" + str(physics_count) + "_hull_phys",
            )
            physics_count += 1
    for i, p in enumerate(mgr["HitBoxs"]):
        hb = d.ref(manager, p)
        hd = d.tree(hb)
        go = d.ref(hb, hd["m_GameObject"])
        collider = component(go, "MeshCollider")
        require(collider is not None, "Non-mesh armor needs an adapter")
        require(
            not hd.get("IsOverrideAntiHEATArmor")
            and hd.get("hitBoxType", 0) in (0, 1, 2),
            "Unsupported reactive/special armor",
        )
        n = "pw_armor_" + str(i) + "_" + str(hd["Armor"])
        count = mesh(go, d.ref(collider, d.tree(collider)["m_Mesh"]), n)
        plate_mount = {1: "hull", 2: "turret_01", 4: "gun_01"}.get(hd["armorPart"])
        require(plate_mount is not None, "Unsupported armor mount")
        plates.append(
            {
                "name": n,
                "mount": plate_mount,
                "armor": positive(hd["Armor"], "armor", True),
                "screen": hd["hitBoxType"] in (1, 2),
                "module": "tracks" if hd["hitBoxType"] == 2 else "",
                "submesh": i,
                "triangles": count,
            }
        )
    require(
        visual_count > 0 and physics_count > 0 and 0 < len(plates) <= 512,
        "Incomplete combat geometry",
    )
    require("body" in textures and "tracks" in textures, "Body/track textures missing")
    # Prefer the actual thumbnail bundle, never an unrelated projectile sprite.
    sprites = [
        o
        for o in d.objects
        if o.type.name == "Sprite"
        and vid + "_thumbnail.sprite" in str(o.assets_file.parent.name)
        and d.owners.get(id(o.assets_file)) == archive.guid
    ]
    require(len(sprites) == 1, "Ambiguous/missing vehicle thumbnail")
    icon = io.BytesIO()
    sprites[0].read().image.save(icon, format="PNG")
    files = {
        "tank.json": encode(card),
        "model.glb": glb.bytes(),
        "nodes.json": encode({"rig": rig, "nodes": nodes, "albedos": {}}),
        "armor.json": encode({"plates": plates}),
        "body.png": textures["body"],
        "tracks.png": textures["tracks"],
        "icon.png": icon.getvalue(),
    }
    report = {
        "vehicleGuid": vid,
        "id": name,
        "status": "converted_with_approximations",
        "visualParts": visual_count,
        "armorPlates": len(plates),
        "vertices": vertices_total,
        "sourceType": d.cls(dynamics),
        "sourceStats": {"health": card["health"], "massKg": card["hull_weight"]},
        "overrides": overrides.get(vid, {}),
        "approximations": [
            "native_blitz_physics_and_ballistics",
            "native_materials_audio_effects",
            "internal_modules_use_native_blitz_rules",
            "sdk_shell_subtypes_use_native_blitz_ballistics",
        ],
        "localizedNames": inf.get("localizedNames", []),
    }
    return vid, files, report


def convert(
    path,
    output,
    overrides,
    allow_approximations=False,
    dependencies=(),
    inspect_only=False,
):
    root = Archive(Path(path))
    archives = [root]
    catalog = {}
    for p in dependencies:
        a = Archive(Path(p))
        require(a.guid not in catalog, "Duplicate dependency GUID")
        catalog[a.guid] = a
    visiting = set()
    visited = {root.guid}

    def visit(a):
        require(a.guid not in visiting, "Dependency cycle")
        visiting.add(a.guid)
        for dependency in a.meta.get("dependencies", []):
            if isinstance(dependency, dict):
                require(
                    set(dependency) <= {"guid"},
                    "Dependency version/schema constraints need an adapter",
                )
            guid = dependency if isinstance(dependency, str) else dependency.get("guid")
            require(guid in catalog, "Missing package dependency: " + str(guid))
            child = catalog[guid]
            require(child.guid not in visiting, "Dependency cycle")
            if child.guid not in visited:
                visited.add(child.guid)
                archives.append(child)
                visit(child)
        visiting.remove(a.guid)

    visit(root)
    require(
        len(archives) <= 64
        and sum(sum(map(len, a.files.values())) for a in archives) <= MAX_OUTPUT,
        "Dependency budget exceeded",
    )
    d = Decoder(archives)
    classes = collections.Counter(
        d.cls(o) for o in d.objects if o.type.name == "MonoBehaviour"
    )
    report = {
        "format": "blitz-modpack-report",
        "version": 1,
        "adapter": ADAPTER,
        "source": str(path),
        "sourceSha256": root.sha256,
        "packageGuid": root.guid,
        "platform": root.meta.get("buildTarget"),
        "engine": root.meta.get("buildEngineVersion"),
        "classes": dict(classes),
        "dependencies": [{"guid": a.guid, "sha256": a.sha256} for a in archives[1:]],
        "vehicles": [],
    }
    if inspect_only:
        report["status"] = "inspected_not_converted"
        return report
    infos = d.records("VehicleInfo", root.guid)
    managers = d.records("VehicleComponentsReferenceManager", root.guid)
    require(
        0 < len(infos) <= 64, "No supported ground VehicleInfo or too many vehicles"
    )
    require(
        len(managers) >= len(infos), "Cannot associate vehicle info and cooked roots"
    )
    payload = {}
    vehicles = []
    for info in infos:
        inf = d.tree(info)
        # The cooked root name/VehicleTextData AssetName is the SDK vehicle identity.
        candidates = []
        for manager in managers:
            root_go = d.ref(manager, d.tree(manager)["m_GameObject"])
            if d.tree(root_go)["m_Name"] in (
                inf["assetGuid"] + "_pre",
                inf["assetGuid"] + "_lq_pre",
            ):
                candidates.append(manager)
        high = [
            m
            for m in candidates
            if d.tree(d.ref(m, d.tree(m)["m_GameObject"]))["m_Name"]
            == inf["assetGuid"] + "_pre"
        ]
        if len(high) == 1:
            candidates = high
        require(len(candidates) == 1, "Ambiguous vehicle identity/root association")
        try:
            vid, files, vr = build_vehicle(
                d, info, candidates[0], root, overrides, allow_approximations
            )
        except Exception as exc:
            report["status"] = "blocked"
            report["vehicles"].append(
                {
                    "vehicleGuid": inf.get("assetGuid"),
                    "status": "blocked",
                    "reason": str(exc),
                }
            )
            error = ConversionError(str(exc))
            error.report = report
            raise error from exc
        prefix = "vehicles/" + vid + "/"
        require(
            not any(k.startswith(prefix) for k in payload), "Duplicate vehicle GUID"
        )
        payload.update({prefix + k: v for k, v in files.items()})
        vehicles.append({"guid": vid, "id": vr["id"], "path": prefix.rstrip("/")})
        report["vehicles"].append(vr)
    report["status"] = "converted_with_approximations"
    payload["conversion-report.json"] = encode(report)
    manifest = {
        "format": "blitz-portable-mod",
        "schemaVersion": 1,
        "guid": root.guid,
        "version": root.meta["modVersion"],
        "displayName": root.meta["modName"],
        "localizedName": root.meta.get("localizedModName", {}),
        "vehicles": vehicles,
        "sourceSha256": root.sha256,
        "adapter": ADAPTER,
        "status": report["status"],
        "files": {n: hashlib.sha256(v).hexdigest() for n, v in payload.items()},
    }
    payload["manifest.json"] = encode(manifest)
    require(sum(map(len, payload.values())) <= MAX_OUTPUT, "Output budget exceeded")
    output = Path(output)
    require(output.resolve() != Path(path).resolve(), "Cannot overwrite source archive")
    output.parent.mkdir(parents=True, exist_ok=True)
    fd, temp = tempfile.mkstemp(prefix="." + output.name, dir=output.parent)
    os.close(fd)
    try:
        with zipfile.ZipFile(temp, "w", zipfile.ZIP_DEFLATED) as z:
            for n, b in sorted(payload.items()):
                entry = zipfile.ZipInfo(n, (2026, 1, 1, 0, 0, 0))
                entry.compress_type = zipfile.ZIP_DEFLATED
                entry.external_attr = 0o100644 << 16
                z.writestr(entry, b)
        os.replace(temp, output)
    finally:
        if os.path.exists(temp):
            os.unlink(temp)
    report["output"] = str(output)
    return report


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("archives", type=Path, nargs="+")
    p.add_argument("--output", type=Path, help="Output file for one source archive")
    p.add_argument("--output-dir", type=Path, help="Batch output directory")
    p.add_argument("--report", type=Path, required=True)
    p.add_argument("--inspect", action="store_true")
    p.add_argument("--overrides", type=Path)
    p.add_argument("--dependency", type=Path, action="append", default=[])
    p.add_argument("--allow-approximations", action="store_true")
    args = p.parse_args()
    if not args.inspect and not (args.output or args.output_dir):
        p.error("--output or --output-dir is required")
    if args.output and (len(args.archives) != 1 or args.output_dir):
        p.error("--output is for a single archive only")
    protected = {
        x.resolve()
        for x in args.archives
        + args.dependency
        + ([args.overrides] if args.overrides else [])
    }
    outputs = [
        args.output
        or (args.output_dir / (a.stem + ".blitzmod") if args.output_dir else None)
        for a in args.archives
    ]
    destinations = [x.resolve() for x in outputs if x is not None] + [
        args.report.resolve()
    ]
    if len(destinations) != len(set(destinations)) or protected.intersection(
        destinations
    ):
        p.error(
            "Output/report destinations must be unique and must not overwrite inputs"
        )
    # No Unity runtime/source DLL is loaded. POSIX resource limits bound decoder allocation/time.
    if resource is not None:
        resource.setrlimit(resource.RLIMIT_AS, (3 * 1024**3, 3 * 1024**3))
        resource.setrlimit(resource.RLIMIT_CPU, (180, 180))
    results = []
    code = 0
    for archive, output in zip(args.archives, outputs):
        try:
            overrides = (
                checked_json(args.overrides.read_bytes()) if args.overrides else {}
            )
            report = convert(
                archive,
                output,
                overrides,
                args.allow_approximations,
                args.dependency,
                args.inspect,
            )
        except Exception as exc:
            report = getattr(
                exc,
                "report",
                {"status": "blocked", "adapter": ADAPTER, "source": str(archive)},
            )
            report.update(error=type(exc).__name__, reason=str(exc))
            code = 2
        results.append(report)
        print(
            json.dumps(
                {
                    "source": str(archive),
                    "status": report["status"],
                    "report": str(args.report),
                    "reason": report.get("reason", ""),
                },
                ensure_ascii=False,
            )
        )
    result = (
        results[0]
        if len(results) == 1
        else {
            "format": "blitz-modpack-batch-report",
            "version": 1,
            "packages": results,
            "status": "blocked" if code else "converted_with_approximations",
        }
    )
    args.report.parent.mkdir(parents=True, exist_ok=True)
    fd, temp = tempfile.mkstemp(prefix="." + args.report.name, dir=args.report.parent)
    os.close(fd)
    try:
        Path(temp).write_bytes(encode(result) + b"\n")
        os.replace(temp, args.report)
    finally:
        if os.path.exists(temp):
            os.unlink(temp)
    return code


if __name__ == "__main__":
    sys.exit(main())
