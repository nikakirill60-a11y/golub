"""Hermetic deployment settings and native Windows command regression tests."""
import tempfile
import shutil
import subprocess
import os
from pathlib import Path
import unittest

from build_env import PUBLIC_RESOURCES, load_environment, require_android_signing, write_runtime_settings
from unity import windows_command


class EnvironmentTests(unittest.TestCase):
    def test_literals_comments_and_explicit_environment_precedence(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / '.env').write_text(
                "# comment\nexport A='space # value'\nB=from-file\nC=\"$HOME\"\nEMPTY=''\n")
            env = load_environment(root, {'B': 'explicit'})
            self.assertEqual(env['A'], 'space # value')
            self.assertEqual(env['B'], 'explicit')
            self.assertEqual(env['C'], '$HOME')
            self.assertEqual(env['EMPTY'], '')

    def test_legacy_fallback_and_canonical_file_selection(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / '.env.release.local').write_text('A=legacy\n')
            self.assertEqual(load_environment(root, {})['A'], 'legacy')
            (root / '.env').write_text('A=canonical\n')
            self.assertEqual(load_environment(root, {})['A'], 'canonical')
            (root / 'custom.env').write_text('A=custom\n')
            self.assertEqual(load_environment(root, {'BLITZ_ENV_FILE': 'custom.env'})['A'], 'custom')

    def test_invalid_config_error_does_not_echo_value(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / '.env').write_text('PASSWORD=private-value extra-token\n')
            with self.assertRaises(ValueError) as result:
                load_environment(root, {})
            self.assertNotIn('private-value', str(result.exception))
            self.assertIn('value withheld', str(result.exception))
            with self.assertRaises(ValueError):
                load_environment(root, {'BLITZ_ENV_FILE': 'missing.env'})

    def test_absent_configuration_is_safe(self):
        with tempfile.TemporaryDirectory() as folder:
            self.assertEqual(load_environment(Path(folder), {}), {})

    def test_only_whitelisted_public_values_are_embedded(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            env = {
                'BLITZ_SENTRY_DSN': 'https://public@example.invalid/1',
                'BLITZ_DIRECT_SERVER': '[::1]:7777',
                'BLITZ_PHOTON_APP_ID': '00000000-0000-0000-0000-000000000001',
                'SENTRY_AUTH_TOKEN': 'private-upload-token',
                'BLITZ_ANDROID_KEYSTORE_PASSWORD': 'private-signing-password',
            }
            write_runtime_settings(root, env)
            files = [p for p in root.rglob('*') if p.is_file()]
            self.assertEqual(len(files), 3)
            for file in files:
                text = file.read_text()
                self.assertNotIn('private-', text)
            for relative in PUBLIC_RESOURCES.values():
                Path(str(root / relative) + '.meta').write_text('old metadata')
            write_runtime_settings(root, {})
            self.assertFalse(any(p.is_file() for p in root.rglob('*')))

    def test_invalid_settings_fail_before_any_resource_is_written(self):
        invalid = [
            {'BLITZ_SENTRY_DSN': 'http://public@example.invalid/1'},
            {'BLITZ_SENTRY_DSN': 'https://public:secret@example.invalid/1'},
            {'BLITZ_DIRECT_SERVER': 'https://example.invalid'},
            {'BLITZ_DIRECT_SERVER': 'example.invalid:70000'},
            {'BLITZ_DIRECT_SERVER': 'user@example.invalid:7777'},
            {'BLITZ_PHOTON_APP_ID': 'not-a-guid'},
        ]
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for env in invalid:
                with self.subTest(key=next(iter(env))), self.assertRaises(ValueError):
                    write_runtime_settings(root, env)
            self.assertFalse(any(root.rglob('*')))

    def test_explicit_empty_value_disables_local_integration(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / '.env').write_text('BLITZ_DIRECT_SERVER=example.invalid:7777\n')
            env = load_environment(root, {'BLITZ_DIRECT_SERVER': ''})
            write_runtime_settings(root, env)
            self.assertFalse((root / PUBLIC_RESOURCES['BLITZ_DIRECT_SERVER']).exists())

    def test_android_signing_fails_closed_without_credentials(self):
        with self.assertRaises(ValueError):
            require_android_signing({})
        with tempfile.TemporaryDirectory() as folder:
            key = Path(folder) / 'release key.keystore'
            key.touch()
            env = dict(BLITZ_ANDROID_KEYSTORE=str(key),
                       BLITZ_ANDROID_KEYSTORE_PASSWORD='private',
                       BLITZ_ANDROID_KEY_ALIAS='release', BLITZ_ANDROID_KEY_PASSWORD='private')
            require_android_signing(env)
            key.unlink()
            with self.assertRaises(ValueError) as result:
                require_android_signing(env)
            self.assertNotIn('private', str(result.exception))

    @unittest.skipIf(os.name == 'nt', 'POSIX compatibility helper')
    def test_legacy_photon_helper_updates_local_env_without_committing_a_resource(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'Tools').mkdir()
            helper = root / 'Tools/photon_app_id.sh'
            shutil.copyfile(Path(__file__).with_name('photon_app_id.sh'), helper)
            (root / '.env').write_text("OTHER='untouched'\n")
            app_id = '00000000-0000-0000-0000-000000000001'
            subprocess.run(['sh', str(helper), app_id], check=True, capture_output=True)
            env = load_environment(root, {})
            self.assertEqual(env['OTHER'], 'untouched')
            self.assertEqual(env['BLITZ_PHOTON_APP_ID'], app_id)
            self.assertFalse((root / 'Assets').exists())

    @unittest.skipIf(os.name == 'nt', 'POSIX deployment tooling')
    def test_deployment_uses_env_port_and_preserves_ssh_key_path(self):
        with tempfile.TemporaryDirectory(prefix='blitz deploy ') as folder:
            root = Path(folder)
            (root / 'Tools').mkdir()
            for name in ('direct_deploy.sh', 'load_env.sh', 'blitz-direct.service'):
                shutil.copyfile(Path(__file__).with_name(name), root / 'Tools' / name)
            player = root / 'Build/Linux/BlitzLegacy'
            player.parent.mkdir(parents=True)
            player.write_text('#!/bin/sh\nexit 0\n')
            player.chmod(0o700)
            fakebin = root / 'fakebin'
            fakebin.mkdir()
            ssh = fakebin / 'ssh'
            ssh.write_text('#!/bin/sh\nprintf "%s\\n" "$@" >> "$CAPTURE_SSH"\ncase "$*" in *"cat > /tmp/blitz-direct.service"*) cat > "$CAPTURE_SERVICE";; esac\n')
            ssh.chmod(0o700)
            rsync = fakebin / 'rsync'
            rsync.write_text('#!/bin/sh\nexit 0\n')
            rsync.chmod(0o700)
            (root / '.env').write_text("BLITZ_DIRECT_DEPLOY_TARGET='user@example.invalid'\nSSH_KEY='/tmp/key with spaces'\nDIRECT_PORT=8123\n")
            capture = root / 'service'
            env = dict(os.environ, PATH=str(fakebin) + os.pathsep + os.environ['PATH'], CAPTURE_SERVICE=str(capture), CAPTURE_SSH=str(root / "ssh-args"))
            subprocess.run(['bash', str(root / 'Tools/direct_deploy.sh')], env=env,
                           capture_output=True, check=True, timeout=10)
            self.assertIn('/tmp/key with spaces', (root / 'ssh-args').read_text().splitlines())
            service = capture.read_text()
            self.assertIn('-blitzServerPort 8123', service)
            self.assertNotIn('@PORT@', service)
            self.assertIn('@USER@', service)  # Expanded on the remote host, not locally.
            (root / '.env').write_text("BLITZ_DIRECT_DEPLOY_TARGET='user@example.invalid'\nSSH_KEY='key'\nDIRECT_PORT=70000\n")
            result = subprocess.run(['bash', str(root / 'Tools/direct_deploy.sh')], env=env,
                                    capture_output=True, timeout=10)
            self.assertNotEqual(result.returncode, 0)


class WindowsCommandTests(unittest.TestCase):
    def test_native_build_uses_quoted_argument_vector_and_win64(self):
        with tempfile.TemporaryDirectory(prefix='blitz windows path ') as folder:
            root = Path(folder)
            (root / 'ProjectSettings').mkdir()
            (root / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 7000.0.0a8\n')
            editor = root / 'editor with spaces/Unity.exe'
            editor.parent.mkdir()
            editor.touch()
            env = {'UNITY': str(editor), 'BLITZ_TEST_FILTER': 'Some Test'}
            args = windows_command(root, env, 'windows-production-debug')
            self.assertEqual(args[0], str(editor))
            self.assertEqual(args[args.index('-projectPath') + 1], str(root))
            self.assertEqual(args[args.index('-buildTarget') + 1], 'Win64')
            self.assertLess(args.index('-buildTarget'), args.index('-executeMethod'))
            args = windows_command(root, env, 'test')
            self.assertNotIn('-quit', args)
            self.assertEqual(args[args.index('-testFilter') + 1], 'Some Test')


if __name__ == '__main__':
    unittest.main()
