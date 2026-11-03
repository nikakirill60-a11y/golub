#!/usr/bin/env python3
"""Regression coverage for the Blitz 11.20 Mark I Male overlay."""
from __future__ import annotations

import copy
import hashlib
import json
import re
import unittest
from pathlib import Path
from xml.etree import ElementTree as ET

import port_blitz_mark_i_male_1120 as task
import blitz_mark_i_male_1120_registry as registry


class MarkIMale1120Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.live = json.loads((task.DATA / "tanks.json").read_text(encoding="utf-8"))
        cls.card = next(c for c in cls.live["tanks"] if c["name"] == task.NAME)
        cls.inventory = json.loads(task.INVENTORY.read_text(encoding="utf-8"))
        cls.record = cls.inventory["records"][0]
        cls.balance = json.loads(task.BALANCE.read_text(encoding="utf-8"))["tanks"][0]
        cls.models = json.loads((task.DATA / "tank_models_source_manifest.json").read_text())

    @staticmethod
    def digest(path: Path) -> str:
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def test_live_collectible_is_isolated_and_existing_cards_are_unchanged(self):
        self.assertEqual(522, len(self.live["tanks"]))
        self.assertEqual(522, len({c["name"] for c in self.live["tanks"]}))
        self.assertEqual(1, sum(c["name"] == task.NAME for c in self.live["tanks"]))
        self.assertTrue(self.card["collector"])
        self.assertTrue(self.card["prem"])
        self.assertFalse(self.card["tree_tank"])
        self.assertFalse(self.card["shop_tank"])
        self.assertEqual("collectible", self.card["acquisition"])
        self.assertEqual({}, self.card["unlock_tanks"])

        preservation = json.loads(task.PRESERVATION.read_text(encoding="utf-8"))
        old = [c for c in self.live["tanks"] if c["name"] != task.NAME]
        hashes = {c["name"]: task.card_hash(c) for c in old}
        self.assertEqual(preservation["count"], len(old))
        self.assertEqual(preservation["cards"], hashes)

        baseline = {c["name"]: task.card_hash(c) for c in old}
        document = {"tanks": copy.deepcopy(old)}
        document = registry.merge_into(document)
        first = [task.card_hash(c) for c in document["tanks"]]
        document = registry.merge_into(document)
        self.assertEqual(first, [task.card_hash(c) for c in document["tanks"]])
        self.assertEqual(baseline, {
            c["name"]: task.card_hash(c) for c in document["tanks"] if c["name"] != task.NAME
        })

    def test_source_hashes_module_identity_and_balance_budget(self):
        for key in ("original_xml", "parameter", "scene", "geometry", "collision", "collision_geometry"):
            self.assertEqual(self.record[key + "_sha256"],
                             self.digest(task.SOURCE / self.record[key + "_source"]), key)

        xml = ET.parse(task.SOURCE / self.record["original_xml_source"]).getroot()
        gun_armor = xml.find("turrets0/*/guns/*/armor/gun")
        self.assertIsNotNone(gun_armor)
        self.assertEqual(10, int(gun_armor.text))
        self.assertEqual(["Turret_1_GB86_Mark_I_Male"], self.record["modules"]["turrets"])
        self.assertEqual(["_57mm_Hotchkiss_6_pdr_QF"], self.record["modules"]["guns"])
        self.assertEqual(self.card, self.balance["balanced"])

        metrics = self.balance["balanced_metrics"]
        self.assertEqual(275, metrics["total_health"])
        self.assertAlmostEqual(591.89187056, metrics["max_sustained_dpm"], places=5)
        self.assertEqual(8, len(self.balance["competitors"]))
        self.assertEqual(250.0, self.balance["peer_medians"]["total_health"])
        self.assertAlmostEqual(657.65765766, self.balance["peer_medians"]["sustained_dpm"], places=5)
        self.assertAlmostEqual(60 * self.card["guns"][0]["shells"][0]["dmg"] /
                               self.card["guns"][0]["reload"], metrics["max_sustained_dpm"], places=5)

    def test_authored_gun_mask_supplies_the_missing_ten_mm_collision_plate(self):
        armor = json.loads((task.DATA / "gb86markimale_armor.json").read_text())
        plates = [p for p in armor["plates"] if p["name"] == "gun_01_mask_10"]
        self.assertEqual(1, len(plates))
        plate = plates[0]
        self.assertEqual("gun_01", plate["mount"])
        self.assertEqual("turret_01", plate["required_turret"])
        self.assertEqual(10, plate["armor"])
        self.assertTrue(plate["screen"])
        self.assertEqual("gun", plate["module"])
        self.assertEqual(-1, plate["source_plate_id"])
        self.assertEqual("gun_01_mask", plate["source_model_node"])
        self.assertEqual(8, plate["triangles"])

        entry = self.models["tanks"][task.NAME]
        self.assertEqual(18, entry["armor"]["plates"])
        self.assertEqual(18, entry["armor"]["objects"])
        self.assertEqual("gun_01_mask", entry["gun_armor_fallback"]["source_model_node"])
        self.assertEqual(self.record["geometry_sha256"],
                         entry["gun_armor_fallback"]["source_geometry_sha256"])
        obj = (task.ROOT / "Assets/Blitz/Resources/Models/Tanks/GB86MARKIMALE/GB86MARKIMALE_armor.obj")
        text = obj.read_text(encoding="utf-8")
        self.assertEqual(1, text.count("o gun_01_mask_10\n"))
        self.assertEqual(8, len(re.findall(r"^f ", text.split("o gun_01_mask_10\n", 1)[1], re.M)))

    def test_destroyed_state_is_registered_with_source_provenance(self):
        crash = json.loads((task.DATA / "legacy1120_crash_manifest.json").read_text())
        self.assertIn(task.NAME, crash["vehicles"])
        entry = crash["vehicles"][task.NAME]
        self.assertEqual(self.record["scene_source"], entry["sourceScene"])
        self.assertTrue(entry["destroyedXmlPaths"])
        self.assertIn("chassis_track_crash_L", entry["geometryPolicy"])
        self.assertEqual(self.record["scene_sha256"], crash["provenance"][self.record["scene_source"]])

    def test_icons_localization_and_atlas_regions_are_complete(self):
        names = json.loads((task.DATA / "tank_name_localization.json").read_text())["tanks"]
        lang = json.loads((task.DATA / "lang.json").read_text())["keys"]
        for field in ("name", "short"):
            expected = names[task.NAME][field]["translations"]
            self.assertEqual(expected, lang[f"tank:{task.NAME}/{field}"])
            self.assertEqual("Mark I Male", expected["en"])
            self.assertTrue(expected["ru"])

        big = json.loads((task.DATA / "blitz_mark_i_male_1120_icons.json").read_text())["icons"][0]
        small_manifest = json.loads((task.DATA / "small_tank_icons_manifest.json").read_text())
        small = next(icon for icon in small_manifest["icons"] if icon["tank"] == task.NAME)
        self.assertEqual(task.NAME, big["name"])
        self.assertEqual(task.NAME, small["tank"])
        self.assertEqual(180, small_manifest["counts"]["11.20"])
        self.assertTrue((task.ROOT / "Assets/Blitz/Resources/Textures/TankIcons/gb86markimale.png").is_file())
        self.assertTrue((task.ROOT / "Assets/Blitz/Resources/Textures/SmallTankIcons/gb86markimale.png").is_file())

        atlas = json.loads((task.DATA / "atlas_regions.json").read_text())
        self.assertEqual("TankIcons/gb86markimale", atlas["regions"]["tank_icon_gb86markimale"]["texture"])
        self.assertEqual("SmallTankIcons/gb86markimale", atlas["regions"]["flow_tank_gb86markimale_small"]["texture"])


if __name__ == "__main__":
    unittest.main()
