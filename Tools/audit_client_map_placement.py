#!/usr/bin/env python3
"""Compare exported map props with the placements the client authors.

The Unity OBJ importer negates X, so the porter pre-negates it. This audit
reloads the client .sc2, recomputes each visible entity's world origin in
Unity axes, and compares it with the origin of the matching group inside the
exported OBJ after the importer's negation. A porter that forgets to
pre-mirror shows up here as a sign flip on X.

A binary client scene (`sceneFormat: client`, the Mirage and Karieri ports)
is written in Unity axes directly, so its layout node bounds are compared
instead; there a handedness bug shows up as a sign flip on X or on Z.

Usage: python3 Tools/audit_client_map_placement.py <map-id>
"""
import importlib.util
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def load_porter():
    spec = importlib.util.spec_from_file_location(
        "port_blitz_client_map", ROOT / "Tools/port_blitz_client_map.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def client_origins(pm, map_id):
    """Entity world origins in Unity axes, keyed by exported group prefix."""
    pm.MAP = pm.MAPS[map_id]
    if pm.MAP.client_version == "11.20":
        # The 11.20 scene lives in a `.dvpl` pack; client_map_1120 unpacks
        # it the way the porter itself does.
        sys.path.insert(0, str(ROOT / "Tools"))
        import client_map_1120
        pack = client_map_1120.ClientPack(pm.PACK_1120)
        scene = client_map_1120.load_scene(pack, pm.MAP.client)
    else:
        source = pm.PACK / "3d/Maps" / pm.MAP.client / (pm.MAP.client + ".sc2")
        scene = pm.dava.read_sc2(source.read_bytes())
    materials = {
        int.from_bytes(node["#id"], "little"): node
        for node in scene.get("dataNodes", [])
        if node.get("##name") == "NMaterial" and node.get("#id")
    }
    origins = {}
    count = 0
    import re

    def dropped(entity):
        # The porters leave MapConfig.dropped_props out before numbering, so
        # the audit has to skip them too or every later name shifts.
        return pm.is_dropped_prop(entity, pm.MAP)

    def walk(entity):
        nonlocal count
        if dropped(entity):
            return
        switch = next(pm.components(entity, "SwitchComponent"), None)
        state = 0
        if switch:
            try:
                state = int(float(switch.get("sc.switchindex", 0)))
            except (TypeError, ValueError):
                state = 0
        visible = [
            batch for batch in pm.batches_of(entity, state)
            if not pm.is_invisible(pm.inherited_material(materials, batch[1]))
        ]
        if visible:
            count += 1
            name = re.sub(r"[^A-Za-z0-9_]+", "_", str(entity.get("name", "mesh")))
            matrix = pm.mat_mul(pm.DAVA_TO_UNITY, pm.matrix_of(entity))
            origins["entity_%d_%s" % (count, name)] = pm.xform_point(matrix, (0.0, 0.0, 0.0))
        for child in pm.items(entity.get("#hierarchy")):
            if isinstance(child, dict):
                walk(child)

    for root in pm.items(scene["scene"].get("#hierarchy")):
        if isinstance(root, dict):
            walk(root)
    return origins


def exported_origins(obj_path):
    """Per-group vertex centroid in Unity space (the importer negates X)."""
    verts = []
    sums = {}
    current = None
    with obj_path.open("r", encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("v "):
                _, x, y, z = line.split()
                verts.append((-float(x), float(y), float(z)))
            elif line.startswith("g "):
                current = line[2:].strip()
            elif line.startswith("f ") and current is not None:
                entry = sums.setdefault(current, [0.0, 0.0, 0.0, 0])
                for token in line.split()[1:]:
                    v = verts[int(token.split("/")[0]) - 1]
                    entry[0] += v[0]
                    entry[1] += v[1]
                    entry[2] += v[2]
                    entry[3] += 1
    return {
        name: (s[0] / s[3], s[1] / s[3], s[2] / s[3])
        for name, s in sums.items() if s[3]
    }


def layout_origins(layout_path):
    """Per-node bounds centre of a binary client scene, already Unity space."""
    import json
    layout = json.loads(layout_path.read_text())
    return {
        node["name"]: tuple((node["min"][i] + node["max"][i]) * 0.5 for i in range(3))
        for node in layout["nodes"]
    }


def audit_client_scene(map_id, definition, authored):
    """Sign check on both ground axes; the manifest's Godot z is -Unity z."""
    layout = ROOT / "Assets/Blitz/Resources" / (definition["externalScene"] + "_layout.json")
    exported = layout_origins(layout)
    matched = mirrored = 0
    worst = (0.0, "")
    for group, centre in exported.items():
        prefix = group
        for suffix in ("_intact", "_wreck"):
            if prefix.endswith(suffix):
                prefix = prefix[: -len(suffix)]
        origin = authored.get(prefix)
        if origin is None:
            continue
        expected = (origin[0], -origin[2])
        actual = (centre[0], centre[2])
        direct = max(abs(actual[0] - expected[0]), abs(actual[1] - expected[1]))
        flipped = max(abs(actual[0] + expected[0]), abs(actual[1] + expected[1]))
        if direct <= flipped:
            matched += 1
            if direct > worst[0]:
                worst = (direct, group)
        else:
            mirrored += 1
            if mirrored <= 10:
                print("  MIRRORED %-44s client xz %+9.2f %+9.2f exported xz %+9.2f %+9.2f"
                      % (group, expected[0], expected[1], actual[0], actual[1]))
    print("%s: %d nodes match the client placement, %d are mirrored"
          % (map_id, matched, mirrored))
    print("largest XZ gap among matching nodes: %.2f m (%s)" % worst)


def main():
    map_id = sys.argv[1]
    pm = load_porter()
    import json
    definition = json.loads(
        (ROOT / ("Assets/Blitz/Resources/Data/map_%s.json" % map_id)).read_text())
    authored = client_origins(pm, map_id)
    if definition.get("sceneFormat") == "client":
        audit_client_scene(map_id, definition, authored)
        return
    obj = ROOT / "Assets/Blitz/Resources" / (definition["externalScene"] + ".obj")
    exported = exported_origins(obj)

    matched = mirrored = 0
    worst = (0.0, "")
    for group, centroid in exported.items():
        prefix = group
        for suffix in ("_intact", "_wreck"):
            if prefix.endswith(suffix):
                prefix = prefix[: -len(suffix)]
        origin = authored.get(prefix)
        if origin is None:
            continue
        # The centroid is not the origin, but a sign flip on X is far larger
        # than any prop's own half width plus the placement offset.
        direct = abs(centroid[0] - origin[0])
        flipped = abs(centroid[0] + origin[0])
        if direct <= flipped:
            matched += 1
            if direct > worst[0]:
                worst = (direct, group)
        else:
            mirrored += 1
            if mirrored <= 10:
                print("  MIRRORED %-44s client x %+9.2f exported x %+9.2f"
                      % (group, origin[0], centroid[0]))
    print("%s: %d groups match the client placement, %d are mirrored"
          % (map_id, matched, mirrored))
    print("largest X gap among matching groups: %.2f m (%s)" % worst)


if __name__ == "__main__":
    main()
