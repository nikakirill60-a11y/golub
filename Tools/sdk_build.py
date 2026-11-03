#!/usr/bin/env python3
"""Builds the Blitz Remaster SDK: the rempack tool for Windows x64 and Linux x64.

Runs the SDK tests, publishes self-contained single-file executables and packs
each one with the SDK readme into Build/Sdk/BlitzRemasterSDK-<version>-<platform>.zip.
Needs a .NET 10 SDK: BLITZ_DOTNET, else dotnet on PATH, else ~/.dotnet/dotnet.
Works the same on Linux and Windows; either one builds both platforms.
"""
import argparse
import os
import platform
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SDK = ROOT / 'Sdk'
CLI = SDK / 'src' / 'Rempack.Cli' / 'Rempack.Cli.csproj'
TESTS = SDK / 'tests' / 'Rempack.Sdk.Tests' / 'Rempack.Sdk.Tests.csproj'
OUT = ROOT / 'Build' / 'Sdk'
# .NET runtime identifier -> the platform word in the archive name.
PLATFORMS = {'linux-x64': 'linux-x64', 'win-x64': 'windows-x64'}
DOCS = ('README.md', 'README.ru.md')
# Archive entries carry this time, so the same build gives the same archive.
STAMP = (2020, 1, 1, 0, 0, 0)


def find_dotnet():
    name = 'dotnet.exe' if os.name == 'nt' else 'dotnet'
    for candidate in (os.environ.get('BLITZ_DOTNET'), shutil.which('dotnet'), str(Path.home() / '.dotnet' / name)):
        if not candidate or not Path(candidate).is_file():
            continue
        result = subprocess.run([candidate, '--list-sdks'], capture_output=True, text=True)
        majors = [int(m) for m in re.findall(r'^(\d+)\.', result.stdout, re.M)]
        if majors and max(majors) >= 10:
            return candidate
    sys.exit('No .NET 10 SDK found: install one or set BLITZ_DOTNET to its dotnet executable.')


def game_version():
    text = (ROOT / 'Assets' / 'Blitz' / 'Core' / 'BlitzVersion.cs').read_text(encoding='utf-8')
    return re.search(r'Current = "([^"]+)"', text).group(1)


def host_rid():
    machine = platform.machine().lower()
    if machine not in ('x86_64', 'amd64'):
        return None
    if sys.platform.startswith('linux'):
        return 'linux-x64'
    return 'win-x64' if os.name == 'nt' else None


def run(command, env):
    print('+ ' + ' '.join(str(part) for part in command), flush=True)
    subprocess.run([str(part) for part in command], check=True, env=env)


def add(archive, source, name, executable):
    info = zipfile.ZipInfo(name, STAMP)
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3  # Unix, so the mode below survives unzip
    info.external_attr = (0o100755 if executable else 0o100644) << 16
    archive.writestr(info, source.read_bytes())


def build(dotnet, env, rid, version):
    publish = OUT / 'publish' / rid
    shutil.rmtree(publish, ignore_errors=True)
    run([dotnet, 'publish', CLI, '-c', 'Release', '-r', rid, '--self-contained',
         '-p:PublishSingleFile=true', '-p:PublishTrimmed=true', '-p:EnableCompressionInSingleFile=true',
         '-p:DebugType=none', '-p:InformationalVersion=' + version, '-o', publish], env)
    binary = publish / ('rempack.exe' if rid.startswith('win') else 'rempack')
    if not binary.is_file():
        sys.exit(f'{binary} was not produced')
    if rid == host_rid():
        run([binary, '--version'], env)

    folder = f'BlitzRemasterSDK-{version}-{PLATFORMS[rid]}'
    path = OUT / (folder + '.zip')
    with zipfile.ZipFile(path, 'w') as archive:
        for file in sorted(publish.iterdir()):
            if file.is_file():
                add(archive, file, f'{folder}/{file.name}', file == binary)
        for doc in DOCS:
            add(archive, SDK / doc, f'{folder}/{doc}', False)
    print(f'{path} ({path.stat().st_size // 1024} KiB)', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--platform', action='append', choices=sorted(PLATFORMS),
                        help='build only this platform (may repeat); default: all')
    parser.add_argument('--skip-tests', action='store_true', help='publish without running the SDK tests')
    args = parser.parse_args()

    dotnet = find_dotnet()
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1',
               DOTNET_ROOT=str(Path(dotnet).resolve().parent))
    if not args.skip_tests:
        run([dotnet, 'test', TESTS, '-c', 'Release'], env)

    version = game_version()
    OUT.mkdir(parents=True, exist_ok=True)
    for rid in args.platform or sorted(PLATFORMS):
        build(dotnet, env, rid, version)


if __name__ == '__main__':
    main()
