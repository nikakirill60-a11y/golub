"""Exact Blitz 4.8.0 collision armour, not normal-zone guesses.

Every 4.8.0 collision mesh (all 344 in the pack) stores its armour per face in
the first texture coordinate, constant on all three corners of a triangle:

* ``uv0.y`` is the plate thickness in millimetres;
* ``uv0.x`` is 1 for hit-point bearing armour and 0 for a spaced screen (the
  XML ``vehicleDamageFactor 0.0`` plates: tracks guards, gun mantlet shells).

The Godot converter, ``blitz2glb.zoned_meshes``, ignores that stream. It
buckets triangles by their normal into front/side/rear/roof and paints every
bucket with one XML number, so a lower glacis that faces down and forward
lands in ``roof`` - the AMX 50 Foch (155) had a 20 mm triangle under its
180 mm nose that a 30 mm shell went straight through. This module replaces
the bucketing with the authored per-face numbers, one panel per distinct
(thickness, screen) pair:

    armor_hull_plate180_180      main armour, 180 mm
    armor_hull_plate120s_120     spaced screen, 120 mm

The trailing number stays the thickness the runtime reads, and ``s`` marks a
screen; ``screen_of`` decodes it for the armour table.
"""
from __future__ import annotations

import collections
import re
import sys

from port_blitz_branch_models import GODOT

# The converter and the scene reader live in the Godot project's tools.
sys.path.insert(0, str(GODOT / "tools"))

import blitz2glb as b  # noqa: E402
import dava  # noqa: E402

PREFIXES = ("armor_hull", "armor_turret", "_gun_mask")

# Smallest total panel area (m^2) worth a collider.
MIN_PANEL_AREA = 1e-6
_PLATE = re.compile(r"_plate(\d+)(s?)_(\d+)$")


def screen_of(name: str) -> bool | None:
    """True for a screen panel, False for main armour, None if not exact."""
    match = _PLATE.search(name)
    if not match:
        return None
    return match.group(2) == "s"


def face_plates(group) -> list[tuple[int, bool]]:
    """(thickness mm, screen) of every triangle of a collision group."""
    streams = dava.decode_vertices(group)
    indices = dava.decode_indices(group)
    uvs = streams.get("uv0")
    if not uvs:
        raise RuntimeError("4.8.0 collision group carries no uv0 armour stream")
    plates = []
    for offset in range(0, len(indices) - 2, 3):
        corners = {tuple(uvs[i]) for i in indices[offset:offset + 3]}
        if len(corners) != 1:
            raise RuntimeError(f"mixed armour on one collision face: {sorted(corners)}")
        flag, millimeters = next(iter(corners))
        if flag not in (0.0, 1.0) or millimeters < 0:
            raise RuntimeError(f"unexpected armour texcoord ({flag}, {millimeters})")
        plates.append((int(round(millimeters)), flag == 0.0))
    return plates


def _area(points) -> float:
    """Total area of a flat triangle list."""
    total = 0.0
    for a, b, c in zip(points[0::3], points[1::3], points[2::3]):
        u = [b[i] - a[i] for i in range(3)]
        v = [c[i] - a[i] for i in range(3)]
        n = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
        total += 0.5 * (n[0] ** 2 + n[1] ** 2 + n[2] ** 2) ** 0.5
    return total


def install() -> None:
    """Patch blitz2glb so 4.8.0 armour panels follow the authored faces."""
    if getattr(b.MeshMaker, "_blitz_480_exact", False):
        return
    original_zones = b.MeshMaker.zoned_meshes

    def exact(self, prefix, sources, thickness, pivot=(0.0, 0.0, 0.0),
              source_vertical_limit=None):
        if prefix not in PREFIXES:
            return original_zones(self, prefix, sources, thickness, pivot,
                                  source_vertical_limit)
        matrix = b.mat_mul(b.DAVA_TO_GLTF,
                           b.translation_matrix(tuple(-x for x in pivot)))
        buckets = collections.defaultdict(lambda: ([], []))
        for node in sources:
            for group_id, _material, _lod in node["batches"]:
                group = self.groups.get(group_id)
                if group is None:
                    raise RuntimeError(f"missing collision group {group_id}")
                streams = dava.decode_vertices(group)
                indices = dava.decode_indices(group)
                positions = streams["position"]
                normals = streams.get("normal")
                for face, key in enumerate(face_plates(group)):
                    ids = indices[face * 3:face * 3 + 3]
                    points = [b.xform_point(matrix, positions[i]) for i in ids]
                    face_normal = b.normalized(b.triangle_normal(*points))
                    out_points, out_normals = buckets[key]
                    for i, point in zip(ids, points):
                        out_points.append(point)
                        out_normals.append(b.xform_dir(matrix, normals[i])
                                           if normals else face_normal)
        panels = []
        # Thickest first, main armour before a screen of the same thickness.
        for (millimeters, screen), (points, normals) in sorted(
                buckets.items(), key=lambda item: (-item[0][0], item[0][1])):
            # A thickness the mesh only gives to degenerate slivers (the T32
            # hull's two 170 mm faces) has no area a shell could hit, and
            # PhysX refuses to cook a collider out of it. The threshold is
            # 1 mm^2: the OBJ writer rounds the vertices, so a sliver that is
            # barely non-zero here collapses to exactly zero in Unity.
            if _area(points) < MIN_PANEL_AREA:
                continue
            label = f"{millimeters}{'s' if screen else ''}"
            name = f"{prefix}_plate{label}_{millimeters}"
            primitive = {
                "attributes": {
                    "POSITION": self.gltf.vec3(points, with_bounds=True),
                    "NORMAL": self.gltf.vec3(normals),
                },
                "indices": self.gltf.indices(list(range(len(points)))),
                "material": 0,
                "mode": 4,
            }
            self.vertices += len(points)
            self.triangles += len(points) // 3
            panels.append((name, self.gltf.add_mesh(name, [primitive])))
        return panels

    b.MeshMaker.zoned_meshes = exact

    # The converter cuts the whole gun collision donor into one mantlet mesh
    # named `_gun_mask_<mm>`. Hand it back as a transform-only group of exact
    # panels instead, the way the 11.20 exporter does.
    pending = {}
    original_mesh = b.MeshMaker.mesh_from

    def mesh_from(self, name, sources, pivot=(0.0, 0.0, 0.0)):
        if name.startswith("_gun_mask_"):
            panels = exact(self, "_gun_mask", sources, {}, pivot)
            if not panels:
                if any(node["batches"] for node in sources):
                    raise RuntimeError("non-empty gun donor lost its panels")
                return None
            pending[(id(self.gltf), name)] = panels
            return panels[0][1]
        return original_mesh(self, name, sources, pivot)

    b.MeshMaker.mesh_from = mesh_from
    original_node = b.Gltf.add_node

    def add_node(self, name, **kwargs):
        panels = pending.pop((id(self), name), None)
        if panels is None:
            return original_node(self, name, **kwargs)
        children = [original_node(self, panel, mesh=mesh) for panel, mesh in panels]
        return original_node(self, "_collision_gun_panels",
                             translation=kwargs.get("translation"),
                             children=children)

    b.Gltf.add_node = add_node
    b.MeshMaker._blitz_480_exact = True
