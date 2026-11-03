#!/usr/bin/env python3
"""Import and balance the Mark I Male from the Blitz 11.20 client."""
from __future__ import annotations

import argparse
import copy
import hashlib
import io
import json
from pathlib import Path
from xml.etree import ElementTree as ET

import yaml
from PIL import Image

import port_blitz_1120 as port
import port_blitz_480_data as base
import port_blitz_usa_1120 as usa
import port_blitz_other_1120 as balance
from blitz_mark_i_male_1120_registry import merge_into

ROOT, DATA, SOURCE, PACK = port.ROOT, port.DATA, port.SOURCE, port.PACK
VEHICLE = "GB86_Mark_I_Male"
NAME = "gb86_mark_i_male"
NATION = "uk"
MODEL_NATION = "GB"
OVERLAY = "mark_i_male_1120"
XML = SOURCE / "from_apk/XML/item_defs/vehicles/uk"
PARAMETER = SOURCE / "from_apk/3d/Tanks/Parameters/uk" / (VEHICLE + ".yaml")
INVENTORY = DATA / "blitz_mark_i_male_1120_inventory.json"
CARDS = DATA / "blitz_mark_i_male_1120_cards.json"
BALANCE = DATA / "blitz_mark_i_male_1120_balance.json"
PRESERVATION = DATA / "blitz_mark_i_male_1120_preservation.json"
ICON_REPORT = DATA / "blitz_mark_i_male_1120_icons.json"
GROUPS = ("turrets", "guns", "tracks", "engines", "radios")


def save(path: Path, value: object) -> None:
    port.save(path, value)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def card_hash(card: dict) -> str:
    payload = json.dumps(card, ensure_ascii=False, sort_keys=True,
                         separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def configure_importer() -> None:
    port.BRANCHES = {NATION: [VEHICLE]}
    port.MODEL_NATIONS[NATION] = MODEL_NATION
    port.NAMES = {VEHICLE: NAME}
    port.INVENTORY = INVENTORY
    port.CARD_FILE = CARDS
    port.model_stem = lambda _nation, _vehicle: VEHICLE


def preserved_baseline() -> list[dict]:
    if not (DATA / "tanks.json").is_file():
        raise FileNotFoundError(DATA / "tanks.json")
    live = json.loads((DATA / "tanks.json").read_text(encoding="utf-8"))["tanks"]
    old = [card for card in live if card.get("source_overlay") != OVERLAY]
    if any(card["name"] == NAME for card in old):
        raise RuntimeError(f"{NAME} already exists outside its own overlay")
    actual = {card["name"]: card_hash(card) for card in old}
    if PRESERVATION.exists():
        expected = json.loads(PRESERVATION.read_text(encoding="utf-8"))["cards"]
        if actual != expected:
            changed = sorted(name for name, value in expected.items()
                             if actual.get(name) != value)
            raise RuntimeError("Protected cards changed: " + ", ".join(changed[:20]))
    else:
        save(PRESERVATION, {
            "policy": "Pre-existing cards are preserved byte-for-byte at the data-record level",
            "cards": actual,
            "count": len(old),
        })
    return old


def source_parameters() -> dict:
    document = yaml.safe_load(PARAMETER.read_text(encoding="utf-8-sig"))
    resources = document["resourcesPath"]
    for key in ("blitzModelPath", "collisionMesh", "smallIconPath", "bigIconPath"):
        if not resources.get(key):
            raise ValueError(f"{VEHICLE}: missing authored {key}")
    return resources


def prepare_inventory() -> list[dict]:
    configure_importer()
    resources = source_parameters()
    records = port.prepare()
    original_xml = XML / (VEHICLE + ".xml")
    original_root = ET.parse(original_xml).getroot()
    record = records[0]
    record.update(
        parameter_source=str(PARAMETER.relative_to(SOURCE)),
        parameter_sha256=digest(PARAMETER),
        resources=resources,
        original_xml_source=str(original_xml.relative_to(SOURCE)),
        original_xml_sha256=digest(original_xml),
    )
    for label, relative in (
        ("scene", record["scene"]),
        ("geometry", record["scene"].removesuffix(".sc2") + ".scg"),
        ("collision", record["collision"]),
        ("collision_geometry", record["collision"].removesuffix(".sc2") + ".scg"),
    ):
        original = port.source_file(relative)
        record[label + "_source"] = str(original.relative_to(SOURCE))
        record[label + "_sha256"] = digest(original)
    record["modules"] = {
        "turrets": [node.tag for node in original_root.find("turrets0")],
        "guns": list(dict.fromkeys(node.tag for node in original_root.findall("turrets0/*/guns/*"))),
        "tracks": [node.tag for node in original_root.find("chassis")],
        "engines": [node.tag for node in original_root.find("engines")],
        "radios": [node.tag for node in original_root.find("radios")],
    }
    record["configurations"] = [
        {"turret": turret.tag, "gun": gun.tag,
         "turret_mesh": port.slot(turret), "gun_mesh": port.slot(gun)}
        for turret in original_root.find("turrets0")
        for gun in turret.find("guns")
    ]
    save(INVENTORY, {
        "version": "11.20",
        "source": str(SOURCE),
        "generator": "Tools/port_blitz_mark_i_male_1120.py",
        "listed": 1,
        "preserved": 0,
        "added": 1,
        "records": records,
        "totals": {kind: sum(len(item["modules"][kind]) for item in records)
                   for kind in GROUPS},
        "configurations": sum(len(item["configurations"]) for item in records),
    })
    return records


def build_card(old: list[dict], record: dict) -> tuple[dict, dict]:
    configure_importer()
    base.PACK = PACK
    base.VEHICLES = PACK / "XML/item_defs/vehicles"
    english = yaml.safe_load((SOURCE / "from_apk/Strings/en.yaml").read_text(encoding="utf-8"))
    russian = yaml.safe_load((SOURCE / "from_apk/Strings/ru.yaml").read_text(encoding="utf-8"))
    strings = dict(english)
    strings.update(russian)
    client = port.NationClient1120(NATION, strings)
    card = base.build_card(client, {"id": VEHICLE}, {VEHICLE: NAME})

    original_xml = XML / (VEHICLE + ".xml")
    listing = ET.parse(XML / "list.xml").getroot().find(VEHICLE)
    if listing is None:
        raise RuntimeError(f"{VEHICLE}: missing list.xml entry")
    tags = (listing.findtext("tags") or "").split()
    if "collectible" not in tags or "heavyTank" not in tags:
        raise RuntimeError(f"{VEHICLE}: unexpected source classification {tags}")

    # This is a non-shop collectible, not a researchable Tier I tree card.
    # Retain the source's 10-gold nominal price and sell value as metadata.
    card.update(
        name=NAME,
        lvl=1,
        nation=base.NATION_ID[NATION],
        tank_class=base.CLASS_ID["heavyTank"],
        tree_tank=False,
        tree_branch=0,
        prem=True,
        collector=True,
        shop_tank=False,
        acquisition="collectible",
        cost=0,
        cost_gold=10,
        collector_sell_gold=10,
        cost_xp=0,
        unlock_tanks={},
        hidden=False,
        model_path=record["resources"]["blitzModelPath"],
        source=str(original_xml.relative_to(SOURCE)),
        source_sha256=digest(original_xml),
        source_version="11.20",
        source_overlay=OVERLAY,
    )
    if card["display_name"] != "Mark I Male":
        raise RuntimeError(f"Unexpected source title: {card['display_name']!r}")

    peers = usa.peers_for(card, [item for item in old if item["name"] != NAME])
    report = balance.rebalance(card, peers)
    report["balance_policy"] = (
        "Two-sided same-tier/class calibration; no Tier I Heavy peers exist, "
        "so the comparator falls back to all non-premium Tier I researchable tanks."
    )
    report["source"] = {
        "vehicle_id": VEHICLE,
        "xml": str(original_xml.relative_to(SOURCE)),
        "xml_sha256": digest(original_xml),
        "parameters": str(PARAMETER.relative_to(SOURCE)),
        "parameters_sha256": digest(PARAMETER),
    }
    save(CARDS, {
        "version": "11.20",
        "generator": "Tools/port_blitz_mark_i_male_1120.py",
        "policy": "Single collectible card; source modules and geometry retained; balanced Tier I combat scalars only",
        "additional_research_links": [],
        "tanks": [card],
    })
    save(BALANCE, {
        "baseline": "Existing non-premium Tier I roster",
        "peer_policy": "same tier and class; if that set is empty, all non-premium researchable peers at the same tier",
        "policy": "Two-sided stat-budget calibration; armour plates, silhouette, shell types, ammunition capacity and module identities are unchanged",
        "limitations": "Deterministic stat calibration is not a live PvP playtest",
        "tanks": [report],
    })
    return card, report


def load_records_cards() -> tuple[list[dict], list[dict]]:
    return (
        json.loads(INVENTORY.read_text(encoding="utf-8"))["records"],
        json.loads(CARDS.read_text(encoding="utf-8"))["tanks"],
    )


def export_icons(records: list[dict]) -> None:
    atlas_path = DATA / "atlas_regions.json"
    atlas = json.loads(atlas_path.read_text(encoding="utf-8"))
    output = ROOT / "Assets/Blitz/Resources/Textures/TankIcons"
    evidence = []
    for record in records:
        relative = record["resources"]["bigIconPath"].removeprefix("~res:/")
        try:
            original = port.source_file(relative + "@2x.packed.webp")
        except FileNotFoundError:
            original = port.source_file(relative + ".packed.webp")
        payload = original.read_bytes()
        if original.suffix == ".dvpl":
            payload = port.dvpl.unpack_bytes(payload)
        image = Image.open(io.BytesIO(payload)).convert("RGBA")
        crop = image.getchannel("A").getbbox()
        if not crop:
            raise ValueError(f"{record['name']}: empty authored big icon")
        image = image.crop(crop)
        rid = port.exporter.unity_id(record["name"]).lower()
        destination = output / (rid + ".png")
        destination.parent.mkdir(parents=True, exist_ok=True)
        image.save(destination, format="PNG", optimize=False, compress_level=9)
        texture = "TankIcons/" + rid
        atlas["textures"][texture] = {"width": image.width, "height": image.height}
        atlas["regions"]["tank_icon_" + rid] = {
            "texture": texture, "x": 0, "y": 0,
            "w": image.width, "h": image.height,
        }
        evidence.append({
            "name": record["name"],
            "source": str(original.relative_to(SOURCE)),
            "sha256": digest(original),
            "crop": list(crop),
            "output_sha256": digest(destination),
        })
    save(atlas_path, atlas)
    save(ICON_REPORT, {
        "version": "11.20",
        "policy": "Authored Parameters bigIconPath; exact alpha crop; no resampling",
        "icons": evidence,
    })


def merge_live() -> None:
    path = DATA / "tanks.json"
    document = json.loads(path.read_text(encoding="utf-8"))
    document = merge_into(document)
    path.write_text(json.dumps(document, ensure_ascii=False, indent=1, sort_keys=True) + "\n",
                    encoding="utf-8")


def export_authored_gun_mask_armor(record: dict) -> None:
    """Use the authored gun_01_mask as the 10 mm plate missing from collision SC2.

    The 11.20 collision scene has an empty gun_01 donor, while the vehicle XML
    still authors <gun>10</gun> and the visible model contains the exact
    eight-triangle gun_01_mask. Reuse that source mesh in gun-local space; do
    not invent a box or enlarge the gun barrel into a collision plate.
    """
    import numpy as np
    import trimesh

    glb = port.exporter.GLB_CACHE / (
        port.exporter.unity_id(NAME).lower() + "__turret_01__gun_01.glb"
    )
    if not glb.is_file():
        raise FileNotFoundError(f"Missing converted Mark I gun scene: {glb}")
    scene = port.exporter.scene_of(glb)
    candidates = []
    for node in scene.graph.nodes_geometry:
        transform, geometry_name = scene.graph.get(node)
        mesh = scene.geometry[geometry_name]
        if str(node).startswith("gun01") and len(mesh.faces) == 8 and len(mesh.vertices) == 10:
            candidates.append((node, mesh, np.asarray(transform, dtype=float)))
    if len(candidates) != 1:
        raise RuntimeError(
            "Expected one source gun_01_mask mesh (10 vertices/8 triangles); "
            f"found {len(candidates)}"
        )

    node, mesh, node_transform = candidates[0]
    gun_transform = np.asarray(scene.graph.get("gun01")[0], dtype=float)
    local_transform = np.linalg.inv(gun_transform) @ node_transform
    vertices = trimesh.transform_points(np.asarray(mesh.vertices, dtype=float), local_transform)
    source_normals = np.asarray(mesh.vertex_normals, dtype=float)
    normal_matrix = np.linalg.inv(local_transform[:3, :3]).T
    normals = source_normals @ normal_matrix.T
    lengths = np.linalg.norm(normals, axis=1, keepdims=True)
    normals = np.divide(normals, lengths, out=np.zeros_like(normals), where=lengths > 0)
    uv = getattr(mesh.visual, "uv", None)
    if uv is None or len(uv) != len(vertices):
        raise RuntimeError("The authored gun_01_mask lost its UV stream")
    uv = np.asarray(uv, dtype=float)
    faces = np.asarray(mesh.faces, dtype=int)
    if vertices.shape != (10, 3) or uv.shape != (10, 2) or normals.shape != (10, 3):
        raise RuntimeError("Unexpected source gun_01_mask vertex streams")

    armor_obj = ROOT / "Assets/Blitz/Resources/Models/Tanks/GB86MARKIMALE/GB86MARKIMALE_armor.obj"
    armor_mtl = armor_obj.with_suffix(".mtl")
    armor_path = DATA / "gb86markimale_armor.json"
    table = json.loads(armor_path.read_text(encoding="utf-8"))
    plate_name = "gun_01_mask_10"
    if any(plate.get("name") == plate_name for plate in table.get("plates", [])):
        raise RuntimeError("Mark I gun mask plate is already appended; re-export the model first")

    lines = armor_obj.read_text(encoding="utf-8").splitlines()
    vertex_count = sum(line.startswith("v ") for line in lines)
    uv_count = sum(line.startswith("vt ") for line in lines)
    normal_count = sum(line.startswith("vn ") for line in lines)
    if vertex_count != uv_count or vertex_count != normal_count:
        raise RuntimeError("The existing armour OBJ streams do not share one index base")
    material = "GB86MARKIMALEArmor_gun_01_mask_10"
    with armor_obj.open("a", encoding="utf-8", newline="\n") as obj:
        # Keep the mask in the OBJ's default group: `g` creates a separate
        # MeshFilter in Unity, while the runtime armour table uses one shared mesh
        # with submeshes indexed by plate.
        obj.write(f"\no {plate_name}\nusemtl {material}\n")
        for x, y, z in vertices:
            obj.write(f"v {x:.7f} {y:.7f} {z:.7f}\n")
        for u, v in uv:
            obj.write(f"vt {u:.7f} {v:.7f}\n")
        for x, y, z in normals:
            obj.write(f"vn {x:.7f} {y:.7f} {z:.7f}\n")
        for face in faces:
            indices = [vertex_count + int(index) + 1 for index in face]
            obj.write("f " + " ".join(f"{index}/{index}/{index}" for index in indices) + "\n")
    with armor_mtl.open("a", encoding="utf-8", newline="\n") as mtl:
        mtl.write(
            f"\nnewmtl {material}\n"
            "Ka 0.000000 0.000000 0.000000\n"
            "Kd 1.000000 1.000000 1.000000\n"
            "Ks 0.000000 0.000000 0.000000\n"
            "Ns 1.000000\n"
            "map_Kd Textures/GB86MARKIMALE.png\n"
        )

    plate = {
        "name": plate_name,
        "mount": "gun_01",
        "required_turret": "turret_01",
        "armor": 10,
        "screen": True,
        "module": "gun",
        "submesh": len(table["plates"]),
        "material": material,
        "vertices": len(vertices),
        "triangles": len(faces),
        "source_plate_id": -1,
        "source_armor": 10,
        "vehicle_damage_factor": 0.0,
        "source_model_node": "gun_01_mask",
    }
    table["plates"].append(plate)
    table["policy"] = (
        "SCG hardJointIndex -> XML armor_<id>; unchanged source collision topology; "
        "empty gun donor uses the authored 11.20 gun_01_mask mesh"
    )
    save(armor_path, table)

    manifest_path = DATA / "tank_models_source_manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    entry = manifest["tanks"][NAME]
    for key, increment in (("objects", 1), ("vertices", len(vertices)),
                           ("triangles", len(faces)), ("plates", 1)):
        entry["armor"][key] += increment
    entry["gun_armor_fallback"] = {
        "source_model_node": "gun_01_mask",
        "source_geometry": record["geometry_source"],
        "source_geometry_sha256": record["geometry_sha256"],
        "source_plate_id": -1,
        "source_armor_mm": 10,
        "triangles": len(faces),
        "policy": "Exact source model mask mesh; collision SC2 gun_01 node has no batches",
    }
    save(manifest_path, manifest)


def export_crash_manifest(record: dict) -> None:
    """Register the source-authorized destroyed state for the playable card."""
    scene_path = PACK / record["scene"]
    raw = port.dava.read_sc2(scene_path.read_bytes())["scene"]

    def values(node: object):
        if isinstance(node, dict):
            return node.values()
        return node if isinstance(node, list) else ()

    def walk(hierarchy: object):
        for item in values(hierarchy):
            if isinstance(item, dict):
                yield item
                yield from walk(item.get("#hierarchy", {}))

    names = set()
    fire_points = {}
    for node in walk(raw.get("#hierarchy", {})):
        node_name = node.get("name", "")
        names.add(node_name)
        if node_name not in ("HP_Fire_01", "HP_Fire_02"):
            continue
        components = values(node.get("components", {}))
        render = next((component for component in components
                       if isinstance(component, dict)
                       and component.get("comp.typename") == "RenderComponent"), None)
        if render:
            batches = values(render["rc.renderObj"]["ro.batches"])
            box = next(iter(batches))["rb.aabbox"]
            point = [(box[i] + box[i + 3]) * 0.5 for i in range(3)]
            fire_points[node_name] = [point[0], point[2], -point[1]]
    if not {"chassis_track_crash_L", "chassis_track_crash_R"} <= names:
        raise RuntimeError("Mark I source model lacks its authored damaged-track meshes")

    source_xml = XML / (VEHICLE + ".xml")
    root = ET.parse(source_xml).getroot()
    manifest_path = DATA / "legacy1120_crash_manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["vehicles"][NAME] = {
        "firePoints": fire_points,
        "destroyedXmlPaths": [node.text.strip() for node in root.findall(".//models/destroyed")
                               if node.text and node.text.strip()],
        "geometryPolicy": (
            "Original installed hull/turret/gun meshes; authored "
            "chassis_track_crash_L/R. No separate deformed hull/turret SC2 exists in this scene."
        ),
        "sourceScene": record["scene_source"],
        "crashTexture": manifest["textures"]["albedo"],
    }
    manifest["provenance"][record["original_xml_source"]] = record["original_xml_sha256"]
    manifest["provenance"][record["scene_source"]] = record["scene_sha256"]
    manifest["provenance"][record["geometry_source"]] = record["geometry_sha256"]
    save(manifest_path, manifest)


def main() -> None:
    parser = argparse.ArgumentParser()
    for flag in ("prepare", "models", "icons", "merge", "rebuild"):
        parser.add_argument("--" + flag, action="store_true")
    args = parser.parse_args()
    if not any((args.prepare, args.models, args.icons, args.merge)):
        parser.error("select at least one of --prepare, --models, --icons or --merge")

    if args.prepare:
        old = preserved_baseline()
        records = prepare_inventory()
        cards = [build_card(old, records[0])[0]]
    else:
        records, cards = load_records_cards()

    if args.models:
        configure_importer()
        port.export_models(records, cards, args.rebuild)
        export_authored_gun_mask_armor(records[0])
        export_crash_manifest(records[0])
    if args.icons:
        configure_importer()
        export_icons(records)
    if args.merge:
        merge_live()
    print(json.dumps({"records": len(records), "cards": len(cards),
                      "overlay": OVERLAY}, sort_keys=True))


if __name__ == "__main__":
    main()
