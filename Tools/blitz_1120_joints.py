"""Rigid skeletal animation of 11.20 gun assemblies.

Some 11.20 guns animate below their slot node: the Annihilator's Gatling
(`Oth25_HWT20_FalconFire`) spins its three-barrel cylinder, recoils each
barrel in turn, swings the recoil-mechanism levers, feeds the ammunition belt
and turns the reload drum. Every one of those meshes is a *rigid* skin: each
vertex carries one `hardJointIndex` into its batch's joint list. Unity has no
DAVA skeleton, so the porter

* splits a skinned mesh by joint, one OBJ object per animated joint, and
* bakes the clip into per-object rigid transforms sampled at a fixed rate,
  expressed in the nodes-table axes around the object's pivot.

DAVA matrices are row-vector (translation in elements 12..14). The skin of a
joint is `World(t) * InvBind`; joint locals default to the authored bind
local and a clip channel replaces translation, rotation or scale.
"""
from __future__ import annotations

import math
import struct

import numpy as np

import blitz2glb
import dava

# DAVA (x, y, z) -> the porter's OBJ / nodes-table frame (x, z, -y). The
# runtime mirrors clip frames into battle space exactly like node pivots.
DAVA_TO_OBJ = np.array([[1.0, 0.0, 0.0], [0.0, 0.0, 1.0], [0.0, -1.0, 0.0]])


def _matrix(values) -> np.ndarray:
    """DAVA row-vector 4x4 -> column-vector numpy matrix."""
    return np.array([float(v) for v in values], dtype=float).reshape(4, 4).T


def _walk(entity, out):
    out.append(entity)
    for child in blitz2glb.as_items(entity.get("#hierarchy")):
        if isinstance(child, dict):
            _walk(child, out)


def entities(scene) -> list:
    out = []
    for top in blitz2glb.as_items(scene["scene"].get("#hierarchy")):
        if isinstance(top, dict):
            _walk(top, out)
    return out


def skeletons(scene) -> dict:
    """`{entity name: {'joints': [...], 'groups': {group: [joint name per hard index]}}}`."""
    result = {}
    for entity in entities(scene):
        skeleton = next(blitz2glb.components(entity, "SkeletonComponent"), None)
        if skeleton is None:
            continue
        joints = []
        for joint in blitz2glb.as_items(skeleton.get("joints")):
            parent = int(joint.get("joint.parentIndex", 0xFFFFFF))
            joints.append({
                "name": joint["joint.name"],
                "parent": None if parent >= 0xFFFFFF else parent,
                "bind": _matrix(joint["joint.bindPose"]),
                "inv_bind": _matrix(joint["joint.invBindPose"]),
            })
        groups = {}
        for comp in blitz2glb.components(entity, "RenderComponent"):
            obj = comp.get("rc.renderObj") or {}
            for index, batch in enumerate(blitz2glb.as_items(obj.get("ro.batches"))):
                if not isinstance(batch, dict) or batch.get("rb.datasource") is None:
                    continue
                data = obj.get(f"skinnedObject.batch{index}.targetsData")
                if not data:
                    continue
                targets = struct.unpack("<%dI" % (len(data) // 4), bytes(data))
                groups[int(batch["rb.datasource"])] = [joints[t]["name"] for t in targets]
        result[entity.get("name") or ""] = {"joints": joints, "groups": groups}
    return result


def triangle_joints(group, names) -> list:
    """The joint every triangle of one rigid skinned group follows."""
    hard = dava.decode_vertices(group).get("hardJointIndex") or []
    indices = dava.decode_indices(group)
    out = []
    for i in range(0, len(indices) - 2, 3):
        value = hard[indices[i]]
        if isinstance(value, (list, tuple)):
            value = value[0]
        out.append(names[int(round(value))])
    return out


# ----------------------------------------------------------------- sampling


def _quat_matrix(q) -> np.ndarray:
    x, y, z, w = q
    n = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    x, y, z, w = x / n, y / n, z / n, w / n
    # DAVA rotates row vectors by the transpose of the usual matrix; the clip
    # quaternion still means "rotate by angle about axis" in column form.
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def _slerp(a, b, f):
    a = np.array(a, dtype=float)
    b = np.array(b, dtype=float)
    dot = float(np.dot(a, b))
    if dot < 0.0:
        b, dot = -b, -dot
    if dot > 0.9995:
        out = a + (b - a) * f
    else:
        theta = math.acos(min(1.0, dot))
        out = (math.sin((1 - f) * theta) * a + math.sin(f * theta) * b) / math.sin(theta)
    return out / (np.linalg.norm(out) or 1.0)


def _sample(channel, t):
    keys = channel["keys"]
    if t <= keys[0][0]:
        return keys[0][1]
    for (t0, v0), (t1, v1) in zip(keys, keys[1:]):
        if t <= t1:
            f = 0.0 if t1 <= t0 else (t - t0) / (t1 - t0)
            if channel["target"] == "rotation":
                return list(_slerp(v0, v1, f))
            return [a + (b - a) * f for a, b in zip(v0, v1)]
    return keys[-1][1]


def _local(joint, channels, t) -> np.ndarray:
    bind = joint["bind"]
    translation = bind[:3, 3].copy()
    rotation = bind[:3, :3].copy()
    scale = np.linalg.norm(rotation, axis=0)
    rotation = rotation / np.where(scale > 0, scale, 1.0)
    for channel in channels:
        value = _sample(channel, t)
        if channel["target"] == "translation":
            translation = np.array(value, dtype=float)
        elif channel["target"] == "rotation":
            rotation = _quat_matrix(value)
        elif channel["target"] == "scale":
            scale = np.array(value * 3 if len(value) == 1 else value, dtype=float)
    out = np.eye(4)
    out[:3, :3] = rotation * scale
    out[:3, 3] = translation
    return out


def skin_matrices(skeleton, clip, t) -> dict:
    """`{joint name: World(t) * InvBind}` for one skeleton at clip time t."""
    channels = {node["name"]: node["channels"] for node in clip["nodes"]}
    world = []
    out = {}
    for joint in skeleton["joints"]:
        local = _local(joint, channels.get(joint["name"], ()), t)
        parent = joint["parent"]
        matrix = world[parent] @ local if parent is not None else local
        world.append(matrix)
        out[joint["name"]] = matrix @ joint["inv_bind"]
    return out


def animated(skeleton, clip, frames: int, tolerance: float = 1e-4) -> set:
    """Joints whose skin moves at any sampled time."""
    duration = float(clip["duration"])
    rest = skin_matrices(skeleton, clip, 0.0)
    moving = set()
    for i in range(frames + 1):
        sample = skin_matrices(skeleton, clip, duration * i / frames)
        for name, matrix in sample.items():
            if np.abs(matrix - np.eye(4)).max() > tolerance or np.abs(matrix - rest[name]).max() > tolerance:
                moving.add(name)
    return moving


def _unity_quat(m) -> list:
    trace = m[0, 0] + m[1, 1] + m[2, 2]
    if trace > 0:
        s = math.sqrt(trace + 1.0) * 2
        w, x, y, z = 0.25 * s, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w, x, y, z = (m[2, 1] - m[1, 2]) / s, 0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w, x, y, z = (m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w, x, y, z = (m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s
    return [x, y, z, w]


def pivot_frame(skin, pivot_obj) -> list:
    """`[tx, ty, tz, qx, qy, qz, qw]` of one skin around a pivot, in OBJ axes.

    `pivot_obj` is the object's pivot in the porter's OBJ frame. The result is
    the rigid motion the runtime applies to that pivot on top of its rest
    placement: A * T(-p) * Skin * T(p) * A^-1.
    """
    x, y, z = (float(v) for v in pivot_obj)
    pivot = np.array([x, -z, y])  # OBJ (x, z, -y) back to DAVA
    to_pivot = np.eye(4)
    to_pivot[:3, 3] = -pivot
    back = np.eye(4)
    back[:3, 3] = pivot
    local = to_pivot @ skin @ back
    a = np.eye(4)
    a[:3, :3] = DAVA_TO_OBJ
    unity = a @ local @ np.linalg.inv(a)
    rotation = unity[:3, :3]
    rotation = rotation / np.linalg.norm(rotation, axis=0)
    return [round(float(v), 6) for v in list(unity[:3, 3]) + _unity_quat(rotation)]


def bake(skeleton_list, clip, objects: dict, pivots: dict, fps: int = 30) -> dict:
    """Sample `objects` (`{object name: joint name}`) into `{object: [frame...]}`."""
    duration = float(clip["duration"])
    frames = max(1, int(math.ceil(duration * fps)))
    tracks = {name: [] for name in objects}
    for i in range(frames + 1):
        t = min(duration, i / fps)
        skins = {}
        for skeleton in skeleton_list:
            skins.update(skin_matrices(skeleton, clip, t))
        for name, joint in objects.items():
            tracks[name].append(pivot_frame(skins[joint], pivots[name]))
    return {"duration": round(duration, 6), "fps": fps, "tracks": tracks}
