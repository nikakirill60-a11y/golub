#!/usr/bin/env python3
"""Install the pinned Sentry Unity SDK as an embedded package.

Port of the Godot project's scripts/release/bootstrap_sentry_sdk.py. The Unity
release ships one archive that already is the UPM package, so it is unpacked
into Packages/io.sentry.unity/, which Unity picks up without a manifest entry.
The archive is pinned by SHA-256: a telemetry SDK is never resolved from a
moving registry tag.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import stat
import tempfile
import urllib.request
import zipfile
from pathlib import Path

SDK_VERSION = "4.10.3"
SDK_NAME = f"sentry-unity-{SDK_VERSION}-package-release.zip"
SDK_URL = (
    "https://github.com/getsentry/sentry-unity/releases/download/"
    f"{SDK_VERSION}/package-release.zip"
)
SDK_SHA256 = "754476e914ae898d644500944b4478a773777401c7066387ae81f8ab63ca4b45"
PACKAGE_NAME = "io.sentry.unity"
MARKER_NAME = ".blitz-sentry-sdk"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(url: str, destination: Path, expected_sha256: str) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.is_file() and sha256(destination) == expected_sha256:
        print(f"using cached {destination.name}")
        return
    temporary = destination.with_suffix(destination.suffix + ".part")
    temporary.unlink(missing_ok=True)
    request = urllib.request.Request(
        url, headers={"User-Agent": "BlitzLegacy-release/1"})
    print(f"downloading {destination.name}")
    try:
        with urllib.request.urlopen(request, timeout=600) as response, \
                temporary.open("wb") as output:
            shutil.copyfileobj(response, output, 1024 * 1024)
        actual = sha256(temporary)
        if actual != expected_sha256:
            raise RuntimeError(
                f"SHA-256 mismatch for {destination.name}: {actual} != {expected_sha256}")
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)


def safe_extract(archive: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    root = destination.resolve()
    with zipfile.ZipFile(archive) as bundle:
        for member in bundle.infolist():
            target = (destination / member.filename).resolve()
            if target != root and root not in target.parents:
                raise RuntimeError(f"unsafe archive path: {member.filename}")
            if stat.S_ISLNK(member.external_attr >> 16):
                raise RuntimeError(f"archive symlink is not allowed: {member.filename}")
        bundle.extractall(destination)
        # The bundled sentry-cli binaries lose their executable bit through
        # zipfile, and the editor integration shells out to them.
        for member in bundle.infolist():
            mode = member.external_attr >> 16
            if mode & stat.S_IXUSR:
                extracted = destination / member.filename
                extracted.chmod(extracted.stat().st_mode | 0o111)


def install(archive: Path, destination: Path, force: bool) -> None:
    marker = destination / MARKER_NAME
    expected_marker = f"version={SDK_VERSION}\nsha256={SDK_SHA256}\n"
    if (destination / "package.json").is_file():
        if marker.is_file() and marker.read_text(encoding="utf-8") == expected_marker:
            print(f"Sentry Unity SDK {SDK_VERSION} already installed")
            return
        if not force:
            raise RuntimeError(
                f"{destination} holds an unmanaged SDK; pass --force to replace it")

    with tempfile.TemporaryDirectory(prefix="blitz-sentry-") as temp_name:
        unpacked = Path(temp_name)
        safe_extract(archive, unpacked)
        candidates = list(unpacked.rglob("package.json"))
        roots = [path.parent for path in candidates
                 if (path.parent / "Runtime").is_dir()]
        if len(roots) != 1:
            raise RuntimeError(
                f"expected one UPM package root in the archive, found {len(roots)}")
        if destination.exists():
            shutil.rmtree(destination)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(roots[0], destination)
    marker.write_text(expected_marker, encoding="utf-8")
    print(f"installed Sentry Unity SDK {SDK_VERSION} at {destination}")


def main() -> int:
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=root)
    parser.add_argument("--cache", type=Path)
    parser.add_argument("--destination", type=Path)
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args()

    project_root = args.root.resolve()
    cache = (args.cache or project_root / ".ci-cache" / "downloads").resolve()
    destination = (args.destination or project_root / "Packages" / PACKAGE_NAME).resolve()
    archive = cache / SDK_NAME
    download(SDK_URL, archive, SDK_SHA256)
    install(archive, destination, args.force)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
