#!/usr/bin/env python3
"""Check that exported map props stand on the ported landscape.

Unity negates X while importing an OBJ, so the porter pre-negates it. This
audit reproduces the import (unity_x = -obj_x) and compares every prop
footprint against the terrain height field the tanks drive on. It also
reports the mirrored alternative, which is what a map exported before the
fix looks like.

A binary client scene (sceneFormat "client") stores its node bounds in the
layout JSON in Unity axes already, so no import mirroring applies there.
A Godot-ported scene (sceneFormat "godot") uses the same BLTZMAP1 layout;
its solid nodes are the godot_object ones that stand on the ground.

Usage: python3 Tools/audit_map_ground_alignment.py <map-id> [name-substring]
"""
import json
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RES = ROOT / "Assets/Blitz/Resources"


class HeightField:
    def __init__(self, path):
        data = path.read_bytes()
        magic = data[:8].decode("ascii")
        if magic != "BLTZTER2":
            raise SystemExit("unexpected height field magic %r" % magic)
        side, step_x, step_z, min_h, max_h = struct.unpack_from("<iffff", data, 8)
        self.side = side
        self.step_x = step_x
        self.step_z = step_z
        count = side * side
        self.heights = struct.unpack_from("<%df" % count, data, 28)

    def sample(self, x, z):
        gx = min(max(x / self.step_x, 0.0), self.side - 1.0001)
        gz = min(max(z / self.step_z, 0.0), self.side - 1.0001)
        x0, z0 = int(gx), int(gz)
        fx, fz = gx - x0, gz - z0
        h = self.heights
        s = self.side
        h00 = h[z0 * s + x0]
        h10 = h[z0 * s + x0 + 1]
        h01 = h[(z0 + 1) * s + x0]
        h11 = h[(z0 + 1) * s + x0 + 1]
        return (h00 * (1 - fx) + h10 * fx) * (1 - fz) + (h01 * (1 - fx) + h11 * fx) * fz


def load_groups(obj_path):
    """Yield (name, min, max) bounds in Unity space for every OBJ group."""
    verts = []
    groups = {}
    current = None
    with obj_path.open("r", encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("v "):
                _, x, y, z = line.split()
                # Unity's OBJ importer negates X again on load.
                verts.append((-float(x), float(y), float(z)))
            elif line.startswith("g "):
                current = line[2:].strip()
            elif line.startswith("f ") and current is not None:
                box = groups.get(current)
                for token in line.split()[1:]:
                    index = int(token.split("/")[0])
                    v = verts[index - 1 if index > 0 else index]
                    if box is None:
                        box = [list(v), list(v)]
                        groups[current] = box
                    else:
                        for axis in range(3):
                            box[0][axis] = min(box[0][axis], v[axis])
                            box[1][axis] = max(box[1][axis], v[axis])
    return groups


def load_client_groups(layout_path):
    """Node bounds of a binary client scene, already in Unity space.

    Only nodes that draw solid surface geometry are checked: the sky, the
    sea, shadow hulls and the leaf cards have no foot on the landscape.
    """
    layout = json.loads(layout_path.read_text())
    materials = layout["materials"]
    groups = {}
    for node in layout["nodes"]:
        solid = [
            m for m in node["materials"]
            if materials[m]["kind"] == "surface" and not materials[m]["blend"]
            and not materials[m]["speedTree"]
        ]
        if solid:
            groups[node["name"]] = (node["min"], node["max"])
    return groups


# Roles of a Godot scene node that have a foot on the landscape. Wrecks and
# far LOD sprites start hidden, leaves hang in the air, the sea, the sky and
# the static_bg mountains lie outside the drivable field.
GODOT_GROUNDED_ROLES = ("static", "intact", "trunk")


def load_godot_groups(layout_path):
    """Grounded node bounds of a Godot-ported scene, in Unity space."""
    layout = json.loads(layout_path.read_text())
    materials = layout["materials"]
    groups = {}
    for node in layout["nodes"]:
        if node.get("hidden") or node.get("role") not in GODOT_GROUNDED_ROLES:
            continue
        if any(materials[m]["kind"] == "godot_object" for m in node["materials"]):
            groups[node["name"]] = (node["min"], node["max"])
    return groups


def main():
    map_id = sys.argv[1]
    needle = sys.argv[2] if len(sys.argv) > 2 else ""
    definition = json.loads((RES / ("Data/map_%s.json" % map_id)).read_text())
    terrain = definition["terrain"]
    origin = terrain["origin"]
    field = HeightField(RES / (terrain["resource"] + ".bytes"))
    scene = definition["externalScene"]
    if definition.get("sceneFormat") == "client":
        groups = load_client_groups(RES / (scene + "_layout.json"))
    elif definition.get("sceneFormat") == "godot":
        groups = load_godot_groups(RES / (scene + "_layout.json"))
    else:
        groups = load_groups(RES / (scene + ".obj"))

    def ground(x, z):
        return field.sample(x - origin[0], -z - origin[2])

    rows = []
    for name, (lo, hi) in sorted(groups.items()):
        if needle and needle not in name:
            continue
        if name.endswith("_wreck"):
            continue
        cx = (lo[0] + hi[0]) / 2.0
        cz = (lo[2] + hi[2]) / 2.0
        corners = [(lo[0], lo[2]), (lo[0], hi[2]), (hi[0], lo[2]), (hi[0], hi[2]), (cx, cz)]
        low = min(ground(x, z) for x, z in corners)
        centre = ground(cx, cz)
        mirrored = min(ground(-x, z) for x, z in corners)
        rows.append((name, lo[1] - centre, lo[1] - low, lo[1] - mirrored))

    floats = [r for r in rows if r[1] > 1.5]
    buried = [r for r in rows if r[2] < -2.5]
    print("checked %d groups (filter %r)" % (len(rows), needle))
    print("floating above the landscape: %d" % len(floats))
    print("buried in the landscape: %d" % len(buried))
    for name, over_centre, over_low, over_mirror in (floats + buried)[:20]:
        print("  %-46s foot-centre %+8.2f foot-lowest %+8.2f (mirrored %+8.2f)"
              % (name, over_centre, over_low, over_mirror))
    if rows:
        worst = max(abs(r[1]) for r in rows)
        as_is = sum(abs(r[1]) for r in rows) / len(rows)
        mirror = sum(abs(r[3]) for r in rows) / len(rows)
        print("mean |foot - ground|: %.2f m as exported, %.2f m if mirrored" % (as_is, mirror))
        print("worst |foot - ground|: %.2f m" % worst)


if __name__ == "__main__":
    main()
