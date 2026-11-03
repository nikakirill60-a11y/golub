"""Exported 4.8.0 armour must be the authored per-face collision thickness.

Run from the repository root:

    python3 -m unittest Tools/test_blitz_480_collision.py

The pack checks skip when the Blitz 4.8.0 dump is not on this machine.
"""
from __future__ import annotations

import collections
import json
import re
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "Tools"))

import port_blitz_branch_models as exporter  # noqa: E402

PACK = exporter.ROOT.parent / "wotblitz4.8.0files" if hasattr(exporter, "ROOT") else None
PACK = Path("/home/smh01/projects/wotblitz4.8.0files") if PACK is None or not PACK.exists() else PACK
DATA = ROOT / "Assets/Blitz/Resources/Data"
MANIFEST = DATA / "tank_models_source_manifest.json"
NAME = re.compile(r"^(armor_hull|armor_turret_\d+|gun_\d+_mask_turret_\d+)_plate(\d+)(s?)_(\d+)$")

try:
    import blitz_480_collision as exact  # noqa: E402
    import dava  # noqa: E402
except Exception:  # pragma: no cover - the Godot tools are missing
    exact = None


def tanks_480() -> dict:
    tanks = json.loads(MANIFEST.read_text())["tanks"]
    return {name: entry for name, entry in tanks.items() if not entry.get("original_pack")}


def exported(entry) -> dict:
    """(thickness, screen) triangle counts per exported mount."""
    table = json.loads((DATA / f"{entry['id'].lower()}_armor.json").read_text())
    mounts = collections.defaultdict(collections.Counter)
    for plate in table["plates"]:
        if plate.get("module"):
            continue
        match = NAME.match(plate["name"])
        if not match:
            raise AssertionError(f"{entry['id']}: {plate['name']} is not an exact panel")
        key = (int(match.group(2)), match.group(3) == "s")
        mounts[plate["mount"]][key] += plate["triangles"]
    return mounts


def authored(path: Path) -> collections.Counter:
    scene = dava.read_sc2(path.read_bytes())
    faces = collections.Counter()
    for group in scene["scene"]["#dataNodes"].values():
        if group.get("##name") == "PolygonGroup":
            faces.update(exact.face_plates(group))
    return faces


class PanelNames(unittest.TestCase):
    @unittest.skipIf(exact is None, "Godot tools missing")
    def test_screen_flag_comes_from_the_panel_name(self):
        self.assertIs(exact.screen_of("armor_hull_plate180_180"), False)
        self.assertIs(exact.screen_of("gun_02_mask_turret_01_plate120s_120"), True)
        self.assertIsNone(exact.screen_of("armor_hull_front_180"))
        self.assertIsNone(exact.screen_of("module_tracks_20"))

    def test_every_480_table_uses_exact_panels(self):
        tanks = tanks_480()
        self.assertGreaterEqual(len(tanks), 342)
        for name, entry in tanks.items():
            with self.subTest(tank=name):
                table = json.loads((DATA / f"{entry['id'].lower()}_armor.json").read_text())
                for plate in table["plates"]:
                    if plate.get("module"):
                        continue
                    match = NAME.match(plate["name"])
                    self.assertIsNotNone(match, plate["name"])
                    self.assertEqual(int(match.group(2)), plate["armor"], plate["name"])
                    self.assertEqual(int(match.group(4)), plate["armor"], plate["name"])
                    self.assertEqual(match.group(3) == "s", bool(plate["screen"]), plate["name"])


@unittest.skipIf(exact is None or not (PACK / "3d/Tanks/CollisionMeshes").is_dir(),
                 "Blitz 4.8.0 pack missing")
class AuthoredFaces(unittest.TestCase):
    def test_exported_faces_match_the_collision_texcoords(self):
        for name, entry in sorted(tanks_480().items()):
            with self.subTest(tank=name):
                source = authored(PACK / entry["collision"])
                # Every exported triangle carries a (thickness, screen) pair the
                # collision mesh authored, never a zone number. Alternative
                # guns share one donor, so each mount is held to the source
                # alone; a hull or turret may leave groups out, never add
                # faces. The gun donor can repeat a source group, so a gun
                # mount is held to the authored pairs only.
                for mount, shipped in exported(entry).items():
                    for key, count in shipped.items():
                        self.assertIn(key, source, f"{name} {mount} {key}")
                        if not mount.startswith("gun"):
                            self.assertLessEqual(count, source[key], f"{name} {mount} {key}")


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--stats":
        equal = differ = 0
        for name, entry in sorted(tanks_480().items()):
            source = authored(PACK / entry["collision"])
            shipped = sum(exported(entry).values(), collections.Counter())
            if source == shipped:
                equal += 1
            else:
                differ += 1
                print(name, "unused", dict(source - shipped), "shared", dict(shipped - source))
        print("equal", equal, "differ", differ)
    else:
        unittest.main()
