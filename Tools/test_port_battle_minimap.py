"""Checks of the minimap porters.

Run from Tools/: ../.venv/bin/python -m unittest -q test_port_battle_minimap
"""
import json
import tempfile
import unittest
from pathlib import Path

import port_battle_minimap as minimap
import port_blitz_client_map as client

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "Assets/Blitz/Resources/Data"


class SceneParserTests(unittest.TestCase):
    def test_nodes_are_keyed_by_path_and_keep_their_properties(self):
        text = "\n".join([
            '[node name="UI" type="Control"]',
            '[node name="handle" type="Control" parent="."]',
            "offset_left = 126.0",
            '[node name="map" type="TextureRect" parent="handle"]',
            "scale = Vector2(0.66, 0.66)",
            '[sub_resource type="Animation" id="x"]',
            "length = 2.0",
        ])
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "scene.tscn"
            path.write_text(text, encoding="utf-8")
            nodes = minimap.scene_nodes(path)
        self.assertEqual(nodes["handle"]["offset_left"], "126.0")
        self.assertNotIn("length", nodes["handle/map"])
        self.assertEqual(minimap.control(nodes["handle/map"])["scale"], [0.66, 0.66])
        self.assertEqual(minimap.control(nodes["handle"])["offsets"], [126.0, 0.0, 0.0, 0.0])

    def test_vector_literals_drop_the_type_digit(self):
        self.assertEqual(minimap.floats("Vector2(0.41, 0.41)"), [0.41, 0.41])
        self.assertEqual(minimap.floats("Color(0.96, 0, 0, 1)"), [0.96, 0.0, 0.0, 1.0])


class RespawnNumberTests(unittest.TestCase):
    @staticmethod
    def marker(name, x, y):
        matrix = [0.0] * 16
        matrix[3], matrix[7] = x, y
        return name, matrix

    def test_used_groups_are_numbered_from_one_in_authored_order(self):
        groups = {
            2: [self.marker("a", 0, 0), self.marker("b", 1, 0)],
            3: [self.marker("c", 100, 0)],
        }
        members = [groups[2][0], groups[3][0], groups[2][1]]
        self.assertEqual(client.respawn_numbers(members, groups), [1, 2, 1])

    def test_added_spawns_join_the_nearest_group(self):
        groups = {1: [self.marker("a", 0, 0)], 2: [self.marker("b", 100, 0)]}
        members = [groups[1][0], groups[2][0], self.marker("extra", 90, 5)]
        self.assertEqual(client.respawn_numbers(members, groups), [1, 2, 2])


class RetinaMinimapTests(unittest.TestCase):
    def test_a_retina_frame_is_drawn_whole_at_the_480_side(self):
        import client_map_1120
        terrain = {"origin": [-300.0, 0.0, -300.0], "extent": 600.0}
        area = client_map_1120.minimap_area(client, terrain, [0, 0, 505, 505])
        self.assertEqual(area["frame"], [0, 0, 505, 505])
        self.assertEqual(area["min"], [-300.0, -300.0])
        self.assertEqual(area["max"], [300.0, 300.0])
        self.assertEqual(area["size"], [326, 326])
        wide = client_map_1120.minimap_area(client, terrain, [0, 0, 500, 250])
        self.assertEqual(wide["size"], [326, 163])


class ManifestTests(unittest.TestCase):
    def test_every_map_carries_a_minimap_area_and_numbered_spawns(self):
        for map_id in ["rudniki", "ordeal", "karieri", "lake", "asia", "moon"]:
            manifest = json.loads((DATA / f"map_{map_id}.json").read_text(encoding="utf-8"))
            area = manifest["minimapArea"]
            self.assertEqual(len(area["frame"]), 4, map_id)
            self.assertLess(area["min"][0], area["max"][0], map_id)
            self.assertLess(area["min"][1], area["max"][1], map_id)
            for team in ("teamA", "teamB"):
                numbers = {spawn["respawn"] for spawn in manifest["spawns"][team]}
                self.assertIn(1, numbers, map_id)
                self.assertEqual(numbers, set(range(1, len(numbers) + 1)), map_id)


if __name__ == "__main__":
    unittest.main()
