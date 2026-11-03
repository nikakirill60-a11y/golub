"""Checks of the Godot map scene porter and the committed Rudniki export.

Run from Tools/: ../.venv/bin/python -m unittest -q test_port_godot_map_scene
"""
import json
import tempfile
import unittest
from pathlib import Path

import numpy as np

import port_godot_map_scene as pgs

ROOT = Path(__file__).resolve().parents[1]
RES = ROOT / "Assets/Blitz/Resources"
RUDNIKI = RES / "Maps/Rudniki"


class ConversionTests(unittest.TestCase):
    def test_source_colour_decodes_like_godot(self):
        self.assertAlmostEqual(pgs.srgb_to_linear(0.0), 0.0)
        self.assertAlmostEqual(pgs.srgb_to_linear(1.0), 1.0, places=6)
        self.assertAlmostEqual(pgs.srgb_to_linear(0.5), 0.214041, places=5)
        self.assertEqual(pgs.linear_colour([1, 1, 1]), [1.0, 1.0, 1.0, 1.0])

    def test_godot_points_mirror_across_z(self):
        points = np.array([[1.0, 2.0, 3.0], [-4.0, 5.0, -6.0]])
        np.testing.assert_allclose(pgs.godot_point(points), [[1, 2, -3], [-4, 5, 6]])
        # The source array is left untouched.
        self.assertEqual(points[0, 2], 3.0)

    def test_transforms_compose_parent_first(self):
        # Parent: translate (10, 0, 0). Child: 90 degrees about Y, scale 2.
        parent = pgs.Transform([1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 0, 0])
        child = pgs.Transform([0, 0, -2, 0, 2, 0, 2, 0, 0, 0, 0, 0])
        world = parent * child
        np.testing.assert_allclose(world.points(np.array([[1.0, 0.0, 0.0]])), [[10, 0, -2]])
        np.testing.assert_allclose(world.scale(), [2, 2, 2])
        normal = world.normals(np.array([[0.0, 1.0, 0.0]]))
        np.testing.assert_allclose(normal, [[0, 1, 0]], atol=1e-9)


class NamingTests(unittest.TestCase):
    def test_lossless_follows_the_godot_import(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            for name, mode in (("a.png", 0), ("b.png", 1), ("c.png", 2)):
                (base / name).write_bytes(b"")
                (base / (name + ".import")).write_text(
                    "[params]\n\ncompress/mode=%d\ncompress/high_quality=false\n" % mode)
            (base / "d.png").write_bytes(b"")
            self.assertTrue(pgs.Porter.lossless(None, base / "a.png"))
            self.assertTrue(pgs.Porter.lossless(None, base / "b.png"))
            self.assertFalse(pgs.Porter.lossless(None, base / "c.png"))
            self.assertFalse(pgs.Porter.lossless(None, base / "d.png"))

    def test_node_names_keep_the_gameplay_object(self):
        name = pgs.Porter.node_name
        self.assertEqual(name(None, "scene/tree_01/trunk/leaves"), ("tree_01", "tree_01/trunk/leaves"))
        self.assertEqual(name(None, "static_bg/mountains_01_batch_0"),
                         ("static_bg_mountains_01_batch_0", "static_bg_mountains_01_batch_0"))
        self.assertEqual(name(None, "sky/face2"), ("sky", "sky/face2"))
        self.assertEqual(name(None, "LightmapGI"), (None, None))


class RudnikiExportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.manifest = json.loads((RES / "Data/map_rudniki.json").read_text(encoding="utf-8"))
        cls.layout = json.loads((RUDNIKI / "rudniki_scene_layout.json").read_text(encoding="utf-8"))
        cls.summary = json.loads((RUDNIKI / "rudniki_scene.json").read_text(encoding="utf-8"))

    def test_manifest_points_at_the_scene(self):
        self.assertEqual(self.manifest["sceneFormat"], "godot")
        self.assertEqual(self.manifest["externalScene"], "Maps/Rudniki/rudniki_scene")
        self.assertEqual(self.manifest["terrain"]["origin"], [0.0, 0.0, 0.0])
        self.assertEqual(self.manifest["sky"]["faces"], [])
        for key in ("colorMap", "tileMask", "tileTexture", "normalMap"):
            resource = self.manifest["terrain"][key]
            self.assertTrue((RES / (resource + ".png")).exists(), resource)

    def test_meshes_are_a_bltzmap1_payload(self):
        with (RUDNIKI / "rudniki_scene_meshes.bytes").open("rb") as handle:
            self.assertEqual(handle.read(8), b"BLTZMAP1")

    def test_every_node_names_its_object_and_role(self):
        nodes = self.layout["nodes"]
        self.assertEqual(len(nodes), 1503)
        roles = {}
        for node in nodes:
            self.assertTrue(node["object"], node["name"])
            roles[node["role"]] = roles.get(node["role"], 0) + 1
            for index in node["materials"]:
                self.assertTrue(self.layout["materials"][index]["kind"].startswith("godot_"))
            if node["role"] in ("wreck", "lod"):
                self.assertTrue(node["hidden"], node["name"])
        self.assertEqual(roles["sky"], 5)
        self.assertEqual(roles["intact"], roles["wreck"])

    def test_lightmapped_materials_reference_exr_slices(self):
        lightmapped = 0
        for material in self.layout["materials"]:
            if material["lightmap"]:
                lightmapped += 1
                self.assertTrue(material["lightmap"].endswith("_hdr"), material["name"])
                self.assertTrue((RES / (material["lightmap"] + ".exr")).exists(), material["lightmap"])
        self.assertGreater(lightmapped, 0)
        self.assertEqual(len(list((RUDNIKI / "Textures").glob("lightmap_*_hdr.exr"))), 16)


if __name__ == "__main__":
    unittest.main()
