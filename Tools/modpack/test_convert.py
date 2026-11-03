"""Archive security, binary conversion and cross-platform regression tests."""

import hashlib
import json
import os
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile
import sys

sys.path.insert(0, str(Path(__file__).parent))
from convert import (
    ConversionError,
    checked_json,
    convert,
    normalized,
    safe_archive,
    shell_kind,
)

SOURCE = Path(os.environ.get("BLITZ_PW_DIST", "/home/smh01/projects/pw-mods-tool/dist"))
GUID = "c4e19f4e-ce52-4d25-88b5-81a2f5ff602a"
OVERRIDES = {GUID: {"enginePower": 1020, "tankClass": 3}}


class SecurityTests(unittest.TestCase):
    def test_shell_mapping_uses_sdk_enum_not_name(self):
        for warhead, expected in [(1, 0), (6, 1), (10, 1), (12, 1)]:
            self.assertEqual(
                shell_kind("PenetrateBullet", {"warhead": warhead}), expected
            )
        self.assertEqual(shell_kind("ExplosionBullet", {"explosionWarhead": 3002}), 4)
        self.assertEqual(shell_kind("HEATBullet", {"heatBulletWarhead": 1001}), 2)
        with self.assertRaises(ConversionError):
            shell_kind("ExplosionBullet", {"explosionWarhead": 3003})

    def test_paths(self):
        for name in (
            "../bad",
            "/bad",
            "a/../b",
            "a//b",
            "./x",
            "C:/bad",
            "a\\b",
            "a/./b",
            "",
        ):
            with self.subTest(name=name), self.assertRaises(ConversionError):
                normalized(name)
        self.assertEqual(normalized("vehicles/abc/model.glb"), "vehicles/abc/model.glb")

    def test_duplicate_json(self):
        with self.assertRaises(ConversionError):
            checked_json('{"x":1,"x":2}')

    def test_nonfinite_json(self):
        with self.assertRaises(ConversionError):
            checked_json('{"x":NaN}')

    def test_symlink(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "a.zip"
            with zipfile.ZipFile(p, "w") as z:
                i = zipfile.ZipInfo("link")
                i.external_attr = 0o120777 << 16
                z.writestr(i, "/etc/passwd")
            with self.assertRaises(ConversionError):
                safe_archive(p)

    def test_case_collision(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "a.zip"
            with zipfile.ZipFile(p, "w") as z:
                z.writestr("X", "a")
                z.writestr("x", "b")
            with self.assertRaises(ConversionError):
                safe_archive(p)

    def test_missing_manifest(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "a.zip"
            with zipfile.ZipFile(p, "w") as z:
                z.writestr("x", "a")
            with self.assertRaises(ConversionError):
                safe_archive(p)

    def test_traversal_never_extracts(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "a.zip"
            with zipfile.ZipFile(p, "w") as z:
                z.writestr("../escape", "a")
            with self.assertRaises(ConversionError):
                safe_archive(p)
            self.assertFalse((Path(td).parent / "escape").exists())


@unittest.skipUnless(
    (SOURCE / "StandaloneWindows64_Vehicle-Argonaut.modpack").exists(),
    "Local delivered fixture unavailable",
)
class PackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.tmp.name)
        cls.results = {}
        cls.payloads = {}
        for platform in ("StandaloneWindows64", "Android"):
            p = cls.root / (platform + ".blitzmod")
            r = convert(
                SOURCE / (platform + "_Vehicle-Argonaut.modpack"), p, OVERRIDES, True
            )
            with zipfile.ZipFile(p) as z:
                cls.payloads[platform] = {n: z.read(n) for n in z.namelist()}
            cls.results[platform] = r

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def vehicle_file(self, platform, name):
        return self.payloads[platform]["vehicles/" + GUID + "/" + name]

    def test_both_platforms(self):
        for r in self.results.values():
            self.assertEqual(r["vehicles"][0]["visualParts"], 28)
            self.assertEqual(r["vehicles"][0]["armorPlates"], 19)

    def test_native_stats(self):
        c = json.loads(self.vehicle_file("Android", "tank.json"))
        g = c["guns"][0]
        self.assertEqual(c["health"], 1650)
        self.assertEqual(c["turret_health"], 0)
        self.assertEqual(c["hull_weight"], 65487)
        self.assertAlmostEqual(g["reload"], 8.6, places=4)
        self.assertEqual(g["rot_speed"], 40)
        self.assertAlmostEqual(c["tracks"][0]["rot_speed"], 28.175, places=3)
        self.assertEqual(g["max_ammo"], 57)
        self.assertEqual(g["deviation_up"], {"x": 12, "y": 6})
        self.assertEqual([s["type"] for s in g["shells"]], [0, 2, 3])
        self.assertTrue(c["name"].startswith("pw_"))

    def test_platform_semantics_match(self):
        for file in ("tank.json", "nodes.json", "armor.json"):
            self.assertEqual(
                json.loads(self.vehicle_file("Android", file)),
                json.loads(self.vehicle_file("StandaloneWindows64", file)),
                file,
            )

    def test_integrity_manifest(self):
        for files in self.payloads.values():
            m = json.loads(files["manifest.json"])
            for n, h in m["files"].items():
                self.assertEqual(hashlib.sha256(files[n]).hexdigest(), h)
            self.assertEqual(set(files), set(m["files"]) | {"manifest.json"})

    def test_zero_armor_retained(self):
        a = json.loads(self.vehicle_file("Android", "armor.json"))["plates"]
        self.assertTrue(any(p["armor"] == 0 for p in a))
        self.assertTrue(all(p["screen"] for p in a if p["module"] == "tracks"))
        self.assertEqual([p["submesh"] for p in a], list(range(19)))

    def test_binary_geometry_and_rig(self):
        data = self.vehicle_file("Android", "model.glb")
        magic, version, length = struct.unpack_from("<III", data)
        self.assertEqual((magic, version, length), (0x46546C67, 2, len(data)))
        size = struct.unpack_from("<I", data, 12)[0]
        doc = json.loads(data[20 : 20 + size])
        names = {n["name"] for n in doc["nodes"]}
        self.assertIn("gun_01", names)
        self.assertIn("trackL", names)
        self.assertIn("trackR", names)
        self.assertEqual(len(doc["meshes"]), 48)
        rig = json.loads(self.vehicle_file("Android", "nodes.json"))
        self.assertLess(rig["nodes"]["gun_01_shoot_pos"]["world"][2], 0)

    def test_modified_binary_card_is_not_replaced_by_stock(self):
        import UnityPy

        source = SOURCE / "StandaloneWindows64_Vehicle-Argonaut.modpack"
        with zipfile.ZipFile(source) as z:
            payload = {n: z.read(n) for n in z.namelist()}
        bundle_name = GUID + ".data"
        env = UnityPy.load(payload[bundle_name])
        changed = False
        for obj in env.objects:
            if obj.type.name == "MonoBehaviour":
                tree = obj.read_typetree()
                if "PSParameter" in tree:
                    raw = obj.get_raw_data()
                    old = struct.pack("<i", 1650)
                    self.assertEqual(raw.count(old), 1)
                    obj.set_raw_data(raw.replace(old, struct.pack("<i", 1801), 1))
                    changed = True
        self.assertTrue(changed)
        payload[bundle_name] = env.file.save()
        archive = self.root / "changed.modpack"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
            for n, b in payload.items():
                z.writestr(n, b)
        out = self.root / "changed.blitzmod"
        convert(archive, out, OVERRIDES, True)
        with zipfile.ZipFile(out) as z:
            card = json.loads(z.read("vehicles/" + GUID + "/tank.json"))
        self.assertEqual(card["health"], 1801)

    def test_modified_binary_mesh_is_not_replaced_by_stock(self):
        import UnityPy

        source = SOURCE / "StandaloneWindows64_Vehicle-Argonaut.modpack"
        with zipfile.ZipFile(source) as z:
            payload = {n: z.read(n) for n in z.namelist()}
        bundle_name = GUID + "_pre.clientextramesh"
        env = UnityPy.load(payload[bundle_name])
        changed = False
        for obj in env.objects:
            if obj.type.name != "Mesh":
                continue
            tree = obj.read_typetree()
            if not tree["m_Name"].startswith("Visual-"):
                continue
            vertex = tree["m_VertexData"]
            channel = vertex["m_Channels"][0]
            self.assertEqual(channel["format"], 0)
            self.assertEqual(channel["stream"], 0)
            raw = bytearray(vertex["m_DataSize"])
            at = channel["offset"]
            x = struct.unpack_from("<f", raw, at)[0]
            struct.pack_into("<f", raw, at, x + 0.125)
            vertex["m_DataSize"] = bytes(raw)
            obj.save_typetree(tree)
            changed = True
            break
        self.assertTrue(changed)
        payload[bundle_name] = env.file.save()
        archive = self.root / "changed-mesh.modpack"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
            for n, b in payload.items():
                z.writestr(n, b)
        out = self.root / "changed-mesh.blitzmod"
        convert(archive, out, OVERRIDES, True)
        with zipfile.ZipFile(out) as z:
            model = z.read("vehicles/" + GUID + "/model.glb")
        self.assertNotEqual(
            model, self.vehicle_file("StandaloneWindows64", "model.glb")
        )

    def test_deterministic(self):
        p = self.root / "again.blitzmod"
        convert(
            SOURCE / "StandaloneWindows64_Vehicle-Argonaut.modpack", p, OVERRIDES, True
        )
        self.assertEqual(
            p.read_bytes(), (self.root / "StandaloneWindows64.blitzmod").read_bytes()
        )

    def test_requires_approximation_optin(self):
        with self.assertRaises(ConversionError):
            convert(
                SOURCE / "Android_Vehicle-Argonaut.modpack",
                self.root / "no.blitzmod",
                OVERRIDES,
            )

    def test_missing_horsepower_blocks_without_touching_output(self):
        p = self.root / "preserve.blitzmod"
        p.write_bytes(b"old")
        with self.assertRaises(ConversionError):
            convert(SOURCE / "Android_Vehicle-Argonaut.modpack", p, {}, True)
        self.assertEqual(p.read_bytes(), b"old")


if __name__ == "__main__":
    unittest.main()
