"""Checks of the binary client map scene exporter and the committed Mirage export.

Run from Tools/: ../.venv/bin/python -m unittest -q test_client_map_scene
"""
import json
import math
import struct
import tempfile
import types
import unittest
from pathlib import Path

import numpy as np

import client_map_1120 as v1120
import client_map_scene as cms
import port_blitz_client_map as port

ROOT = Path(__file__).resolve().parents[1]
LAKE = ROOT / "Assets/Blitz/Resources/Maps/Lake"


class VertexDecoderTests(unittest.TestCase):
    def test_streams_are_read_in_bit_order(self):
        # position (bit 0), uv0 (bit 3), uv1 (bit 4) and pivot (bit 10).
        vertex_format = 1 | 1 << 3 | 1 << 4 | 1 << 10
        record = struct.pack("<3f2f2f4f", 1, 2, 3, 0.25, 0.5, 0.75, 1.0, 9, 8, 7, 0.5)
        decoded = cms.decode_vertices({
            "vertexFormat": vertex_format,
            "vertexCount": 2,
            "vertices": record * 2,
        })
        self.assertEqual(sorted(decoded), ["pivot", "position", "uv0", "uv1"])
        np.testing.assert_allclose(decoded["position"][1], [1, 2, 3])
        np.testing.assert_allclose(decoded["uv1"][0], [0.75, 1.0])
        np.testing.assert_allclose(decoded["pivot"][0], [9, 8, 7, 0.5])

    def test_colour_is_four_bytes(self):
        record = struct.pack("<3f4B", 0, 0, 0, 10, 20, 30, 40)
        decoded = cms.decode_vertices({
            "vertexFormat": 1 | 1 << 2, "vertexCount": 1, "vertices": record})
        self.assertEqual(decoded["color"].tolist(), [[10, 20, 30, 40]])

    def test_stride_mismatch_is_rejected(self):
        with self.assertRaises(SystemExit):
            cms.decode_vertices({
                "vertexFormat": 1, "vertexCount": 2, "vertices": b"\0" * 20})

    def test_unknown_bits_are_rejected(self):
        with self.assertRaises(SystemExit):
            cms.decode_vertices({
                "vertexFormat": 1 | 1 << 16, "vertexCount": 0, "vertices": b""})


@unittest.skipUnless((LAKE / "lake_scene_layout.json").exists(), "Mirage is not exported")
class MirageExportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.layout = json.loads((LAKE / "lake_scene_layout.json").read_text())
        cls.payload = (LAKE / "lake_scene_meshes.bytes").read_bytes()

    def test_payload_walks_exactly_through_every_node(self):
        data = self.payload
        self.assertEqual(data[:8], cms.MAGIC)
        (count,) = struct.unpack_from("<i", data, 8)
        self.assertEqual(count, len(self.layout["nodes"]))
        offset = 12
        triangles = 0
        for node in self.layout["nodes"]:
            vertices, flags, submeshes = struct.unpack_from("<3i", data, offset)
            offset += 12
            self.assertEqual(vertices, node["vertexCount"], node["name"])
            self.assertEqual(flags, node["attributes"], node["name"])
            stride = 12 + 8
            stride += 12 if flags & cms.HAS_NORMAL else 0
            stride += 8 if flags & cms.HAS_UV1 else 0
            stride += 4 if flags & cms.HAS_COLOR else 0
            stride += 16 if flags & cms.HAS_TANGENT else 0
            stride += 8 if flags & cms.HAS_BILLBOARD else 0
            offset += stride * vertices
            for s in range(submeshes):
                material, indices = struct.unpack_from("<2i", data, offset)
                offset += 8
                self.assertEqual(material, node["materials"][s], node["name"])
                self.assertEqual(indices % 3, 0, node["name"])
                block = np.frombuffer(data, "<i4", indices, offset)
                offset += indices * 4
                self.assertTrue(block.size == 0 or block.max() < vertices, node["name"])
                triangles += indices // 3
        self.assertEqual(offset, len(data))
        # 53 switch-only breakable props now also ship their wreck state.
        self.assertEqual(triangles, 654939)

    def test_every_material_texture_is_exported(self):
        resources = ROOT / "Assets/Blitz/Resources"
        for material in self.layout["materials"]:
            for key in ("albedo", "lightmap", "alphamask", "flowmap", "normalmap", "cubemap"):
                reference = material.get(key)
                if reference:
                    self.assertTrue(
                        (resources / (reference + ".png")).exists(),
                        material["name"] + " " + key + " " + reference)

    def test_destructibles_point_at_exported_nodes(self):
        manifest = json.loads(
            (ROOT / "Assets/Blitz/Resources/Data/map_lake.json").read_text())
        names = {node["name"] for node in self.layout["nodes"]}
        destructibles = manifest["objects"]["destructibles"]
        # 413 props with authored Health plus 53 the client breaks by
        # CollisionType 3 and a wreck switch state alone (military tents,
        # ammo boxes, concrete blocks).
        self.assertEqual(len(destructibles), 466)
        switch_only = [item for item in destructibles if item["healthFromClient"] == 0]
        self.assertEqual(len(switch_only), 53)
        for item in destructibles:
            self.assertIn(item["intactGroup"], names)
            if item.get("wreckGroup"):
                self.assertIn(item["wreckGroup"], names)
        for item in switch_only:
            self.assertEqual(item["collisionType"], 3, item["name"])
            self.assertGreater(item["health"], 0, item["name"])
            self.assertTrue(item["wreckGroup"], item["name"])

    def test_trees_fall_and_bushes_do_not(self):
        manifest = json.loads(
            (ROOT / "Assets/Blitz/Resources/Data/map_lake.json").read_text())
        names = {node["name"] for node in self.layout["nodes"]}
        trees = manifest["objects"]["trees"]
        # 115 palm01 (FallType 1) and 9 akacia01 (FallType 0); the 125
        # bushes (FallType 3) stay drive-through foliage.
        self.assertEqual(len(trees), 124)
        self.assertEqual(sum(t["name"].startswith("palm01") for t in trees), 115)
        self.assertEqual(sum(t["name"].startswith("akacia01") for t in trees), 9)
        for tree in trees:
            self.assertIn(tree["group"], names)
            self.assertNotIn("bush", tree["name"])
            self.assertNotEqual(tree["fallType"], 3)
            self.assertEqual(len(tree["origin"]), 3)


class FallingTreeTests(unittest.TestCase):
    def test_vegetation_falls_unless_it_is_a_bush(self):
        self.assertTrue(cms.is_falling_tree({"CollisionType": 8, "FallType": 1}))
        self.assertTrue(cms.is_falling_tree({"CollisionType": 8, "FallingType": 0}))
        self.assertFalse(cms.is_falling_tree({"CollisionType": 8, "FallType": 3}))
        self.assertFalse(cms.is_falling_tree({"CollisionType": 8}))

    def test_map_config_topples_extra_collision_types(self):
        # Karieri's cacti are CollisionType 5 / FallType 2 without Health.
        cactus = {"CollisionType": "5", "FallType": "2"}
        self.assertFalse(cms.is_falling_tree(cactus))
        self.assertTrue(cms.is_falling_tree(cactus, (5,)))
        self.assertFalse(cms.is_falling_tree({"CollisionType": 6}, (5,)))
        self.assertFalse(cms.is_falling_tree({}, (5,)))


class KarieriExportTests(unittest.TestCase):
    def test_cacti_fall_and_props_keep_their_wrecks(self):
        manifest = json.loads(
            (ROOT / "Assets/Blitz/Resources/Data/map_karieri.json").read_text())
        layout = json.loads((ROOT / "Assets/Blitz/Resources/Maps/Karieri/"
                             "karieri_scene_layout.json").read_text())
        names = {node["name"] for node in layout["nodes"]}
        trees = manifest["objects"]["trees"]
        self.assertEqual(len(trees), 108)
        for tree in trees:
            self.assertIn("cactus", tree["name"])
            self.assertIn(tree["group"], names)
        destructibles = manifest["objects"]["destructibles"]
        self.assertEqual(len(destructibles), 109)
        for item in destructibles:
            self.assertIn(item["wreckGroup"], names, item["name"])
            self.assertGreater(item["health"], 0, item["name"])


class OrdealExportTests(unittest.TestCase):
    def test_binary_scene_drops_the_tutorial_props_and_poles_fall(self):
        manifest = json.loads(
            (ROOT / "Assets/Blitz/Resources/Data/map_ordeal.json").read_text())
        layout = json.loads((ROOT / "Assets/Blitz/Resources/Maps/Ordeal/"
                             "ordeal_scene_layout.json").read_text())
        self.assertEqual(manifest["sceneFormat"], "client")
        self.assertEqual(manifest["orientation"], "dava")
        names = {node["name"] for node in layout["nodes"]}
        # MapConfig.dropped_props and the `tags: tutorial` props stay out:
        # the lesson's decals, pocket fences, glowing brick fences and
        # Invisible_wall planes. The other 28 border panels are dropped at
        # runtime instead.
        self.assertFalse(any("select01" in name for name in names))
        for fragment in ("brick_fence", "brick_glow", "Invisible_wall", "Tut_arrow"):
            self.assertFalse(any(fragment in name for name in names), fragment)
        self.assertEqual(sum("env_ord_restriction" in name for name in names), 28)
        trees = manifest["objects"]["trees"]
        self.assertEqual(len(trees), 111)
        poles = [tree for tree in trees
                 if "stolb" in tree["name"] or "Loudspeaker" in tree["name"]]
        self.assertEqual(len(poles), 16)
        for tree in trees:
            self.assertIn(tree["group"], names)
        destructibles = manifest["objects"]["destructibles"]
        self.assertEqual(len(destructibles), 224)
        for item in destructibles:
            self.assertIn(item["wreckGroup"], names, item["name"])
            self.assertGreater(item["health"], 0, item["name"])
        # The sun is the -Y axis of `Light beast direct`, which points the
        # way the positional `Light dynamic` at (1043, -1414, 1121) shines.
        sun = manifest["environment"]["sunDirection"]
        self.assertAlmostEqual(sun[0], -0.5067, places=3)
        self.assertAlmostEqual(sun[1], -0.5361, places=3)
        self.assertAlmostEqual(sun[2], 0.6752, places=3)
        self.assertTrue(manifest["environment"]["fog"]["halfspace"]["linear"])


class HiddenRenderObjectTests(unittest.TestCase):
    class Port:
        @staticmethod
        def components(entity, kind):
            return [c for c in entity if c.get("##name") == kind]

    def test_render_object_without_the_visible_flag_is_hidden(self):
        def entity(flags):
            return [{"##name": "RenderComponent", "rc.renderObj": {"ro.flags": flags}}]

        self.assertTrue(cms.is_hidden(self.Port, entity(0)))
        self.assertTrue(cms.is_hidden(self.Port, entity(6)))
        self.assertFalse(cms.is_hidden(self.Port, entity(1)))
        self.assertFalse(cms.is_hidden(self.Port, entity(7)))
        # No flags at all means the DAVA default, which draws.
        self.assertFalse(cms.is_hidden(
            self.Port, [{"##name": "RenderComponent", "rc.renderObj": {}}]))
        self.assertFalse(cms.is_hidden(self.Port, []))


class AsiaExportTests(unittest.TestCase):
    def test_invisible_walls_are_hidden_and_pines_fall(self):
        manifest = json.loads(
            (ROOT / "Assets/Blitz/Resources/Data/map_asia.json").read_text())
        layout = json.loads((ROOT / "Assets/Blitz/Resources/Maps/Asia/"
                             "asia_scene_layout.json").read_text())
        self.assertEqual(manifest["sceneFormat"], "client")
        self.assertEqual(manifest["orientation"], "dava")
        hidden = [node["name"] for node in layout["nodes"] if node.get("hidden")]
        self.assertEqual(len(hidden), 22)
        self.assertEqual(manifest["objects"]["hidden"], 22)
        for name in hidden:
            self.assertIn("invisiblewall", name)
        names = {node["name"] for node in layout["nodes"]}
        trees = manifest["objects"]["trees"]
        self.assertEqual(len(trees), 113)
        for tree in trees:
            self.assertIn("asia_sosna", tree["name"])
            self.assertIn(tree["group"], names)
        destructibles = manifest["objects"]["destructibles"]
        self.assertEqual(len(destructibles), 543)
        for item in destructibles:
            self.assertIn(item["wreckGroup"], names, item["name"])
            self.assertGreater(item["health"], 0, item["name"])


class TriangleStripTests(unittest.TestCase):
    def test_strip_alternates_winding_and_drops_stitches(self):
        triangles = cms.strip_triangles(np.array([0, 1, 2, 3, 3, 4, 4, 5, 6]))
        self.assertEqual(triangles.tolist(), [0, 1, 2, 2, 1, 3, 4, 5, 6])

    def test_strip_groups_decode_to_lists(self):
        group = {"indexCount": 4, "indexFormat": 0,
                 "indices": struct.pack("<4H", 0, 1, 2, 3),
                 "rhi_primitiveType": cms.PRIMITIVE_TRIANGLE_STRIP}
        self.assertEqual(cms.decode_indices(group).tolist(), [0, 1, 2, 2, 1, 3])
        group["rhi_primitiveType"] = 1
        self.assertEqual(cms.decode_indices(group).tolist(), [0, 1, 2, 3])


class MaterialTemplateTests(unittest.TestCase):
    DOCUMENT = """Material:
  Layers: [OpaqueRenderLayer]
  Shader: "~res:/Materials/Shaders/Default/materials"
  UniqueDefines: [MATERIAL_TEXTURE]
  QualityDependentUniqueDefines:
    HIGH: [PIXEL_LIT]
    LOW: [VERTEX_LIT]
  RenderState:
    depthWrite: true
    cullMode: FACE_BACK
  Presets:
    Additive:
      Layers: [TranslucentRenderLayer]
      UniqueDefines: [[BLENDING, 3]]
      RenderState: {blend: true, depthWrite: false}
    TwoSided:
      RenderState: {cullMode: FACE_NONE}
"""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        materials = Path(self.directory.name) / v1120.DATA_ROOT / "Materials"
        materials.mkdir(parents=True)
        (materials / "Test.material").write_text(self.DOCUMENT, encoding="utf-8")
        self.templates = v1120.MaterialTemplates(Path(self.directory.name))

    def tearDown(self):
        self.directory.cleanup()

    def test_high_quality_defaults(self):
        template = self.templates.load("~res:/Materials/Test.material")
        self.assertEqual(template["layer"], "OpaqueRenderLayer")
        self.assertEqual(template["kind"], "surface")
        self.assertIn("PIXEL_LIT", template["defines"])
        self.assertNotIn("VERTEX_LIT", template["defines"])
        self.assertFalse(template["blend"])
        self.assertEqual((template["src"], template["dst"]), (1, 0))
        self.assertTrue(template["cull"])
        self.assertTrue(template["depthWrite"])

    def test_presets_switch_layer_blending_and_state(self):
        template = self.templates.load("~res:/Materials/Test.material", presets=("Additive", "TwoSided"))
        self.assertEqual(template["layer"], "TranslucentRenderLayer")
        self.assertIn("BLENDING", template["defines"])
        self.assertTrue(template["blend"])
        self.assertEqual((template["src"], template["dst"]), (5, 1))
        self.assertFalse(template["depthWrite"])
        self.assertFalse(template["cull"])

    def test_unknown_preset_is_rejected(self):
        with self.assertRaises(SystemExit):
            self.templates.load("Test.material", presets=("Glow",))


class ClientTransformTests(unittest.TestCase):
    QUARTER_TURN_Z = (0.0, 0.0, math.sqrt(0.5), math.sqrt(0.5))

    def test_11_20_transform_parts_build_a_trs_matrix(self):
        entity = {"components": [{
            "comp.typename": "TransformComponent",
            "tc.worldTranslation": (1.0, 2.0, 3.0),
            "tc.worldRotation": self.QUARTER_TURN_Z,
            "tc.worldScale": (2.0, 2.0, 2.0),
        }]}
        m = np.array(port.matrix_of(entity)).reshape(4, 4)
        np.testing.assert_allclose(m @ [1.0, 0.0, 0.0, 1.0], [1.0, 4.0, 3.0, 1.0], atol=1e-9)

    def test_quaternions_follow_the_axis_swap(self):
        rng = np.random.default_rng(1120)
        q = rng.normal(size=4)
        q /= np.linalg.norm(q)
        point = rng.normal(size=3)
        dava = v1120.quaternion_matrix(q) @ point
        unity = v1120.quaternion_matrix(v1120.unity_quaternion(q)) @ v1120.unity_point(point)
        np.testing.assert_allclose(unity, v1120.unity_point(dava), atol=1e-9)

    def test_dava_slerp_stays_unit_and_takes_the_short_way(self):
        q = v1120.dava_slerp([0, 0, 0, 1], [0, 0, -math.sqrt(0.5), -math.sqrt(0.5)], 0.5)
        self.assertAlmostEqual(math.sqrt(sum(v * v for v in q)), 1.0)
        self.assertGreater(q[3], 0.9)


class CustomPropertyTests(unittest.TestCase):
    def entity(self, collision_type: int) -> dict:
        return {"components": [
            {"comp.typename": "CustomPropertiesComponent",
             "cpc.properties.archive": {"editor.referenceToOwner": "x.sc2"}},
            {"comp.typename": "CollisionTypeComponent", "CollisionType": collision_type,
             "FallingType": 1, "MaterialKind": 2, "Health": 50},
        ]}

    def test_archive_and_collision_component_fold_together(self):
        found = port.custom_properties(self.entity(3))
        self.assertEqual(found["editor.referenceToOwner"], "x.sc2")
        self.assertEqual((found["CollisionType"], found["FallType"], found["MaterialKind"]), (3, 1, 2))
        self.assertEqual(found["Health"], 50)

    def test_only_breakable_types_keep_their_health(self):
        self.assertNotIn("Health", port.custom_properties(self.entity(8)))


class MapBorderTests(unittest.TestCase):
    def test_raw_rect_bytes_are_min_and_max(self):
        rect = struct.pack("<4f", -249.0, -243.8, 249.1, 240.0)
        entity = {"components": [{"comp.typename": "MapBorderComponent", "mbc.rect": rect}]}
        stub = types.SimpleNamespace(
            named_entities=lambda scene: [("MapBorder", entity)],
            components=port.components)
        config = types.SimpleNamespace(client="40_moon_mn", orientation="dava")
        border = v1120.map_border(stub, config, {}, (-300, -300, 0, 300, 300, 70))
        np.testing.assert_allclose(border["min"], [-249.0, 0.0, -240.0], atol=1e-4)
        np.testing.assert_allclose(border["max"], [249.1, 70.0, 243.8], atol=1e-4)


class VegetationPatternTests(unittest.TestCase):
    def test_resolution_ids_reach_up_one_resolution(self):
        self.assertEqual(v1120.prepare_resolution_id(0, 0, 0), 1)
        self.assertEqual(v1120.prepare_resolution_id(0, 1, 0), 0)
        self.assertEqual(v1120.prepare_resolution_id(1, 2, 2), 1)
        self.assertEqual(v1120.prepare_resolution_id(1, 4, 0), 2)
        self.assertEqual(v1120.prepare_resolution_id(2, 4, 4), 2)

    def test_each_resolution_cell_holds_the_same_share(self):
        clusters = v1120.generate_clusters([2, 1], [30, 0], [0.5, 0], 4.6875)
        self.assertEqual(len(clusters), 80)
        for resolution, (cells, each) in enumerate(((16, 5), (4, 5), (1, 5))):
            grouped = v1120.resolution_cells(clusters, resolution)
            self.assertEqual(len(grouped), cells)
            self.assertEqual({len(cell) for cell in grouped}, {each})
        again = v1120.generate_clusters([2, 1], [30, 0], [0.5, 0], 4.6875)
        self.assertEqual([c["position"] for c in clusters], [c["position"] for c in again])

    def test_ranges_are_squared_cell_bands(self):
        self.assertEqual(v1120.vegetation_ranges([0, 2, 6], 4.6875, 2500.0),
                         [0.0, 87.890625, 87.890625, 791.015625, 791.015625, 6250000.0])


class MoonExportTests(unittest.TestCase):
    MOON = ROOT / "Assets/Blitz/Resources/Maps/Moon"

    def setUp(self):
        self.manifest = json.loads((ROOT / "Assets/Blitz/Resources/Data/map_moon.json").read_text())
        self.layout = json.loads((self.MOON / "moon_scene_layout.json").read_text())

    def test_scene_counts_and_collision(self):
        objects = self.manifest["objects"]
        self.assertEqual((objects["entities"], objects["nodes"]), (170, 172))
        self.assertEqual((objects["hidden"], objects["noCollision"], objects["skinned"]), (5, 10, 2))
        self.assertEqual(self.manifest["directionalLight"]["shadows"], False)
        self.assertEqual(self.manifest["introMusic"], "map_intro_moon")
        passable = [n["name"] for n in self.layout["nodes"] if n.get("noCollision")]
        self.assertEqual(len(passable), 10)
        self.assertIn("entity_164_MapBorder", passable)
        kinds = {m["kind"] for m in self.layout["materials"]}
        self.assertEqual(kinds, {"surface", "sky", "lit"})

    def test_skeleton_parts_and_clips_line_up(self):
        names = {n["name"] for n in self.layout["nodes"]}
        clips = read_clips((self.MOON / "moon_scene_animations.bytes").read_bytes())
        self.assertEqual(len(self.layout["skeletons"]), 2)
        for skeleton in self.layout["skeletons"]:
            for part in skeleton["parts"]:
                self.assertIn(part["node"], names)
                self.assertLess(part["joint"], len(skeleton["joints"]))
            duration, channels = clips[skeleton["clip"]]
            self.assertAlmostEqual(duration, skeleton["duration"], places=4)
            for joint, _target, _interpolation, _dimension, _keys in channels:
                self.assertLess(joint, len(skeleton["joints"]))
        ufo = self.layout["skeletons"][0]
        self.assertEqual((ufo["delay"], ufo["delayVariation"], ufo["repeats"], ufo["playbackRate"]),
                         (120.0, 60.0, 1, 2.0))

    def test_vegetation_patterns_hold_the_client_cluster_counts(self):
        self.assertEqual(self.manifest["objects"]["vegetationClusters"], [304, 76, 19])
        data = (self.MOON / "moon_scene_vegetation.bytes").read_bytes()
        self.assertEqual(data[:8], v1120.VEGETATION_MAGIC)
        side = struct.unpack_from("<i", data, 8)[0]
        self.assertEqual(side, self.layout["vegetation"]["densitySide"])
        offset = 12 + side * side
        resolutions = struct.unpack_from("<i", data, offset)[0]
        offset += 4
        counts = []
        for _ in range(resolutions):
            vertices = struct.unpack_from("<i", data, offset)[0]
            offset += 4 + vertices * (12 + 8 + 16)
            submeshes = struct.unpack_from("<i", data, offset)[0]
            offset += 4
            indices = 0
            for _ in range(submeshes):
                count = struct.unpack_from("<i", data, offset)[0]
                offset += 4 + count * 4
                indices += count
            counts.append((vertices, submeshes, indices))
        self.assertEqual(offset, len(data))
        self.assertEqual(counts[0], (9088, 16, 27936))
        self.assertEqual([c[1] for c in counts], [16, 4, 1])


def read_clips(data: bytes) -> list:
    """`BLTZANM1` as (duration, [(joint, target, interpolation, dimension, keys)])."""
    if data[:8] != v1120.ANIMATION_MAGIC:
        raise AssertionError("not a clip payload")
    total = struct.unpack_from("<i", data, 8)[0]
    offset = 12
    clips = []
    for _ in range(total):
        duration, count = struct.unpack_from("<fi", data, offset)
        offset += 8
        channels = []
        for _ in range(count):
            channel = struct.unpack_from("<5i", data, offset)
            offset += 20 + 4 * channel[4] * (1 + channel[3])
            channels.append(channel)
        clips.append((duration, channels))
    if offset != len(data):
        raise AssertionError("trailing clip bytes")
    return clips

if __name__ == "__main__":
    unittest.main()
