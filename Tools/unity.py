#!/usr/bin/env python3
"""Cross-platform Unity CLI. Load local deployment settings without logging them."""
from __future__ import annotations

import os
from pathlib import Path
import subprocess
import sys

from build_env import load_environment, require_android_signing, write_runtime_settings

# Native Windows routes; Linux delegates the complete smoke/render matrix to sh.
ROUTES = {
    'import': [],
    'compile': ['-quit', '-executeMethod', 'BlitzLegacy.EditorTools.Ci.CompileOnly'],
    'setup': ['-quit', '-executeMethod', 'BlitzLegacy.EditorTools.ProjectBootstrap.ApplyAll'],
    'scenes': ['-quit', '-executeMethod', 'BlitzLegacy.EditorTools.SceneGen.GenerateAll'],
    'net-setup': ['-quit', '-executeMethod', 'BlitzLegacy.EditorTools.NetPrefabSetup.Run'],
    'sentry-setup': ['-quit', '-executeMethod', 'BlitzLegacy.EditorTools.SentryProjectSetup.Run'],
}
for route, target, method in [
    ('apk', 'Android', 'Android'), ('apk-debug', 'Android', 'AndroidDebug'),
    ('apk-production-debug', 'Android', 'AndroidProductionDebug'),
    ('linux', 'Linux64', 'Linux'), ('linux-debug', 'Linux64', 'LinuxDebug'),
    ('linux-production-debug', 'Linux64', 'LinuxProductionDebug'),
    ('windows-production-debug', 'Win64', 'WindowsProductionDebug'),
]:
    ROUTES[route] = ['-buildTarget', target, '-quit', '-executeMethod',
                     'BlitzLegacy.EditorTools.BuildPlayerCli.' + method]
ROUTES['import'] = ['-quit']


def windows_command(root: Path, env: dict[str, str], route: str) -> list[str]:
    version = next(line.split(': ', 1)[1].strip() for line in
                   (root / 'ProjectSettings/ProjectVersion.txt').read_text().splitlines()
                   if line.startswith('m_EditorVersion: '))
    hub = Path(env.get('UNITY_HUB_EDITORS', str(Path(env.get('ProgramFiles', r'C:\Program Files')) / 'Unity/Hub/Editor')))
    editor = Path(env.get('UNITY', str(hub / version / 'Editor/Unity.exe')))
    if not editor.is_file():
        raise ValueError('Matching Unity editor not found; install it or set UNITY')
    command = [str(editor), '-batchmode', '-nographics', '-projectPath', str(root),
               '-logFile', str(root / 'Logs' / (route + '.log'))]
    if route == 'test':
        command += ['-runTests', '-testPlatform', 'EditMode', '-testResults',
                    str(root / 'Logs/editmode-results.xml')]
        if env.get('BLITZ_TEST_FILTER'):
            command += ['-testFilter', env['BLITZ_TEST_FILTER']]
    elif route in ROUTES:
        command += ROUTES[route]
    else:
        raise ValueError('Unsupported native Windows route; use import, compile, test or a player target')
    return command


def main() -> int:
    root = Path(__file__).resolve().parents[1]
    if len(sys.argv) != 2:
        if os.name != 'nt':
            return subprocess.run(['sh', str(root / 'Tools/unity.sh')],
                                  env=dict(os.environ, BLITZ_ENV_READY='1')).returncode
        print('Usage: python Tools/unity.py <import|compile|test|player-target>', file=sys.stderr)
        return 2
    try:
        env = load_environment(root)
        if sys.argv[1].startswith("apk"):
            require_android_signing(env)
        write_runtime_settings(root, env)
        (root / 'Logs').mkdir(exist_ok=True)
        if os.name == 'nt':
            # Wait for Unity.exe explicitly; do not rely on shell GUI-app waiting.
            return subprocess.run(windows_command(root, env, sys.argv[1]), cwd=root, env=env).returncode
        env['BLITZ_ENV_READY'] = '1'
        os.execvpe('sh', ['sh', str(root / 'Tools/unity.sh'), sys.argv[1]], env)
    except (ValueError, OSError) as error:
        print(f'Build configuration error: {error}', file=sys.stderr)
        return 2
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
