"""Exercise build command routing without launching Unity or touching its caches."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


class UnityDriverTests(unittest.TestCase):
    def test_player_builds_select_target_before_execute_method(self):
        routes = {
            "linux": ("Linux64", "Linux"),
            "linux-debug": ("Linux64", "LinuxDebug"),
            "linux-production-debug": ("Linux64", "LinuxProductionDebug"),
            "apk": ("Android", "Android"),
            "apk-debug": ("Android", "AndroidDebug"),
            "apk-production-debug": ("Android", "AndroidProductionDebug"),
        }
        with tempfile.TemporaryDirectory(prefix="blitz unity driver ") as folder:
            root = Path(folder)
            (root / "Tools").mkdir()
            (root / "ProjectSettings").mkdir()
            (root / "ProjectSettings" / "ProjectVersion.txt").write_text(
                "m_EditorVersion: 7000.0.0a8\n"
            )
            driver = root / "Tools" / "unity.sh"
            shutil.copyfile(Path(__file__).with_name("unity.sh"), driver)
            for helper in ("unity.py", "build_env.py"):
                shutil.copyfile(Path(__file__).with_name(helper), root / "Tools" / helper)
            capture = root / "arguments.json"
            fake = root / "fake unity"
            fake.write_text(
                "#!/bin/sh\n"
                "python3 -c 'import json,os,sys; "
                'open(os.environ["UNITY_CAPTURE"],"w").write(json.dumps(sys.argv[1:]))\' "$@"\n'
            )
            fake.chmod(0o700)
            key = root / "test key.keystore"
            key.touch()
            env = dict(os.environ, UNITY=str(fake), UNITY_CAPTURE=str(capture),
                       BLITZ_ANDROID_KEYSTORE=str(key), BLITZ_ANDROID_KEYSTORE_PASSWORD="test",
                       BLITZ_ANDROID_KEY_ALIAS="test", BLITZ_ANDROID_KEY_PASSWORD="test")
            for route, (target, method) in routes.items():
                with self.subTest(route=route):
                    subprocess.run(
                        ["sh", str(driver), route],
                        env=env,
                        check=True,
                        capture_output=True,
                        text=True,
                        timeout=10,
                    )
                    args = json.loads(capture.read_text())
                    self.assertIn("-buildTarget", args)
                    self.assertEqual(args[args.index("-buildTarget") + 1], target)
                    self.assertEqual(args.count("-buildTarget"), 1)
                    self.assertLess(
                        args.index("-buildTarget"), args.index("-executeMethod")
                    )
                    self.assertEqual(
                        args[args.index("-executeMethod") + 1],
                        "BlitzLegacy.EditorTools.BuildPlayerCli." + method,
                    )
                    self.assertEqual(args[args.index("-projectPath") + 1], str(root))


if __name__ == "__main__":
    unittest.main()
