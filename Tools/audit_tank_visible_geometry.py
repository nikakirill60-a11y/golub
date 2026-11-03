#!/usr/bin/env python3
"""Find vehicles whose visible model lost geometry the client scene still has.

SU-152 shipped as tracks, wheels and a floating barrel: its superstructure is
painted from two materials, the converter split it into `hull_vis_<hex>` mesh
siblings, and the exporter only ever read the addressed `hull_vis` node, which
by then carried nothing but a transform.  Collision, armour and the bots kept
using the hull, so the vehicle was invisible and still took hits.

The audit reads every card, the cached client scenes of every (turret, gun)
configuration and the exported OBJ, and reports:

* `lost`     - a part with client geometry that is missing from the OBJ, or an
               OBJ object with no triangles: the SU-152 defect;
* `uncovered`- a part whose collision/armour volume is not backed by visible
               geometry near it;
* `mount`    - a casemate turret slot that has no geometry in the client scene
               either (SU-152, ISU-152, Hetzer ...); reported for information.

Run:  python3 Tools/audit_tank_visible_geometry.py [--nation ussr] [--json out]
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))

import port_blitz_branch_models as exporter  # noqa: E402
from port_blitz_480_models import records  # noqa: E402

RESOURCES = exporter.RESOURCES


def obj_objects(path: Path) -> dict[str, int]:
    """OBJ object name -> triangle count."""
    objects: dict[str, int] = {}
    current = None
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("o "):
            current = line[2:].strip()
            objects.setdefault(current, 0)
        elif line.startswith("f ") and current is not None:
            objects[current] += 1
    return objects


def obj_duplicates(path: Path) -> dict[str, int]:
    """Slot names written more than once into one OBJ."""
    counts: dict[str, int] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("o "):
            name = line[2:].strip()
            counts[name] = counts.get(name, 0) + 1
    return {name: count for name, count in counts.items() if count > 1}


def obj_bounds(path: Path, wanted: set[str] | None = None):
    """Axis-aligned bounds of the named objects, or of the whole file."""
    low = np.full(3, np.inf)
    high = np.full(3, -np.inf)
    current = None
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("o "):
            current = line[2:].strip()
        elif line.startswith("v "):
            if wanted is not None and current not in wanted:
                continue
            point = np.asarray([float(value) for value in line.split()[1:4]])
            low = np.minimum(low, point)
            high = np.maximum(high, point)
    if not np.all(np.isfinite(low)):
        return None
    return low, high


def coverage(inner, outer) -> float:
    """Share of the `inner` box volume that `outer` contains."""
    if inner is None or outer is None:
        return 0.0
    overlap = np.maximum(
        0.0, np.minimum(inner[1], outer[1]) - np.maximum(inner[0], outer[0])
    )
    size = np.maximum(inner[1] - inner[0], 1e-6)
    return float(np.prod(overlap) / np.prod(size))


def audit_card(record) -> dict:
    card = record["card"]
    tank_id = exporter.unity_id(str(card["name"]))
    root = RESOURCES / f"Models/Tanks/{tank_id}"
    model = root / f"{tank_id}.obj"
    physics = root / f"{tank_id}_phys.obj"
    report = {
        "tank": card["name"],
        "id": tank_id,
        "nation": record["nation"],
        "lost": [],
        "uncovered": [],
        "warnings": [],
        "mount": [],
    }
    if not model.is_file():
        report["lost"].append("no visible model")
        return report

    objects = obj_objects(model)
    empty = sorted(name for name, faces in objects.items() if faces == 0)
    report["lost"].extend(f"{name}: no triangles" for name in empty)

    # The runtime shows a part by its slot name, so a slot written twice draws
    # both copies at once: they z-fight, each adds its own outline pass, and
    # the part reads as torn paint.
    for name, count in sorted(obj_duplicates(model).items()):
        report["lost"].append(f"{name}: written {count} times into the model")

    exporter_id = exporter.unity_id(str(card["name"])).lower()
    cache = Path(__file__).resolve().parent / "_branch_glb" / record["nation"]
    expected = {"hull_vis": "hull_vis", "trackL": "trackL", "trackR": "trackR"}
    for turret in card.get("turrets", []):
        turret_slot = str(turret["mesh"])
        for gun in turret.get("guns", []):
            gun_slot = str(gun["mesh"])
            glb = cache / f"{exporter_id}__{turret_slot}__{gun_slot}.glb"
            if not glb.is_file():
                report["lost"].append(f"{turret_slot}/{gun_slot}: no client scene")
                continue
            scene = exporter.scene_of(glb)
            for node, slot in (
                ("hull_vis", "hull_vis"),
                ("trackL", "trackL"),
                ("trackR", "trackR"),
                ("turret01", turret_slot),
                ("gun01", gun_slot),
            ):
                names = {str(name) for name in scene.graph.nodes}
                if node not in names:
                    report["lost"].append(f"{slot}: missing from the client scene")
                    continue
                has_geometry = bool(exporter.part_nodes(scene, node))
                if not has_geometry:
                    if slot not in objects and slot not in report["mount"]:
                        report["mount"].append(slot)
                    continue
                expected[slot] = node
                if objects.get(slot, 0) == 0:
                    message = f"{slot}: client geometry missing from the OBJ"
                    if message not in report["lost"]:
                        report["lost"].append(message)

    # A part can also "disappear" by landing nowhere near the vehicle, so the
    # visible hull has to back the collision hull it is shot through.
    if physics.is_file() and "hull_vis" in objects:
        hull = obj_bounds(model, {"hull_vis"})
        collision = obj_bounds(physics)
        covered = coverage(collision, hull)
        report["hull_coverage"] = round(covered, 3)
        if covered < 0.4:
            report["uncovered"].append(
                f"hull_vis covers only {covered:.0%} of the collision hull")
        elif covered < 0.6:
            # The client authors a few collision hulls around the vehicle
            # centre instead of the visible hull, so the shell box hangs below
            # the tank. That is a seating defect, not a missing mesh.
            report["warnings"].append(
                f"hull_vis covers {covered:.0%} of the collision hull; "
                f"collision y [{collision[0][1]:.2f}, {collision[1][1]:.2f}] "
                f"vs hull y [{hull[0][1]:.2f}, {hull[1][1]:.2f}]")

    return report


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--nation")
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()

    selected = [record for record in records()
                if not args.nation or record["nation"] == args.nation]
    reports = []
    for index, record in enumerate(selected, 1):
        report = audit_card(record)
        reports.append(report)
        flag = "FAIL" if report["lost"] or report["uncovered"] else "ok"
        print(f"[{index}/{len(selected)}] {report['nation']}/{report['tank']}: {flag}")
        for problem in report["lost"] + report["uncovered"]:
            print(f"    {problem}")
        for problem in report["warnings"]:
            print(f"    warning: {problem}")

    broken = [report for report in reports if report["lost"] or report["uncovered"]]
    summary = {
        "tanks": len(reports),
        "broken": [report["tank"] for report in broken],
        "warnings": {
            report["tank"]: report["warnings"] for report in reports if report["warnings"]
        },
        "casemate_mounts": {
            report["tank"]: report["mount"] for report in reports if report["mount"]
        },
    }
    print(json.dumps(summary, ensure_ascii=False, indent=2, sort_keys=True))
    if args.json:
        args.json.write_text(
            json.dumps(reports, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
            encoding="utf-8")
    return 1 if broken else 0


if __name__ == "__main__":
    raise SystemExit(main())
