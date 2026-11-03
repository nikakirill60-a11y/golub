#!/usr/bin/env python3
"""Import the authored Blitz 11.20 tank-tread decals into Unity.

Run with ``.venv-texture/bin/python Tools/port_blitz_1120_treads.py``;
the shared texture virtualenv provides the ASTC decoder used by pvr2png.

The client stores the national 256x256 textures as ASTC PVR (normally wrapped
in DVPL).  Its deferred decal shader supplies DECAL_TREAD and
FADE_OUT_WITH_TIME; this importer records those source files as provenance and
converts only the original pixels.  Runtime mesh generation lives in Unity.
"""
from __future__ import annotations

import hashlib
import json
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT.parent / "wotblitz11.20.0files"
GODOT_TOOLS = ROOT.parent / "blitz-legacy-godot" / "tools"
sys.path.insert(0, str(GODOT_TOOLS))

import dvpl  # noqa: E402
import pvr2png  # noqa: E402

OUTPUT = ROOT / "Assets/Blitz/Resources/TankTreads1120"
MANIFEST = ROOT / "Assets/Blitz/Resources/Data/tank_treads_1120_manifest.json"

# The DLC carries the current national art. USSR is part of the base APK; the
# special STT image is retained even though the present vehicle roster does not
# require it, so the imported 11.20 set remains complete.
TEXTURES = {
    "Ussr": "from_apk/3d/Tanks/Common/images/USSR_01_tread.astc.pvr",
    "Germany": "3d/Tanks/Common/images/German_01_double_tread.astc.pvr.dvpl",
    "Usa": "3d/Tanks/Common/images/USA_01_double_tread.astc.pvr.dvpl",
    "Gb": "3d/Tanks/Common/images/GB_01_tread.astc.pvr.dvpl",
    "Japan": "3d/Tanks/Common/images/Japan_01_tread.astc.pvr.dvpl",
    "France": "3d/Tanks/Common/images/France_01_tread.astc.pvr.dvpl",
    "China": "3d/Tanks/Common/images/China_01_tread.astc.pvr.dvpl",
    "Other": "3d/Tanks/Common/images/Other_01_tread.astc.pvr.dvpl",
    "European": "3d/Tanks/Common/images/European_01_tread.astc.pvr.dvpl",
    "Other_Oth50_STT": "3d/Tanks/Common/images/Other_Oth50_STT_tread.astc.pvr.dvpl",
}

SOURCE_BEHAVIOUR = [
    "from_apk/GraphicsPresets.yaml",
    "from_apk/UI/Screens/Options/GraphicsOptions/GraphicsOptionsPage.yaml",
    "from_apk/Strings/en.yaml",
    "from_apk/Strings/ru.yaml",
    "from_apk/Materials/Shaders/common.slh",
    "from_apk/Materials/Shaders/Default/deferred-decal-vp.sl",
    "from_apk/Materials/Shaders/Default/deferred-decal-fp.sl",
]


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def pvr_dimensions(payload: bytes) -> tuple[int, int, int, int]:
    if len(payload) < 52 or payload[:4] != b"PVR\x03":
        raise ValueError("not a PVR v3 texture")
    # PVR v3: pixel format starts at 8, height at 24, width at 28 and mip count
    # at 44. Values are little-endian uint32, except pixel format is uint64.
    pixel_format = int.from_bytes(payload[8:16], "little")
    height = int.from_bytes(payload[24:28], "little")
    width = int.from_bytes(payload[28:32], "little")
    mip_count = int.from_bytes(payload[44:48], "little")
    return width, height, pixel_format, mip_count


def main() -> int:
    if not SOURCE.is_dir():
        raise FileNotFoundError(SOURCE)
    OUTPUT.mkdir(parents=True, exist_ok=True)

    provenance: dict[str, str] = {}
    imported: dict[str, dict[str, object]] = {}
    with tempfile.TemporaryDirectory(prefix="blitz1120-treads-") as temporary:
        temporary = Path(temporary)
        for key, relative in TEXTURES.items():
            source = SOURCE / relative
            blob = source.read_bytes()
            provenance[relative] = hashlib.sha256(blob).hexdigest()
            payload = dvpl.unpack_bytes(blob) if source.suffix == ".dvpl" else blob
            width, height, pixel_format, mip_count = pvr_dimensions(payload)
            if (width, height) != (256, 256):
                raise ValueError(f"unexpected tread texture size {source}: {width}x{height}")

            raw = temporary / source.name.removesuffix(".dvpl")
            raw.write_bytes(payload)
            destination = OUTPUT / f"{key}.png"
            if pvr2png.main([str(raw), "--out", str(destination)]):
                raise RuntimeError(f"pvr2png failed: {source}")
            imported[key] = {
                "resource": f"TankTreads1120/{key}",
                "source": relative,
                "sourceSha256": provenance[relative],
                "pvrSha256": hashlib.sha256(payload).hexdigest(),
                "pngSha256": sha256(destination),
                "width": width,
                "height": height,
                "pixelFormat": pixel_format,
                "mipCount": mip_count,
            }

    for relative in SOURCE_BEHAVIOUR:
        source = SOURCE / relative
        provenance[relative] = sha256(source)

    manifest = {
        "generator": "Tools/port_blitz_1120_treads.py",
        "client": "Blitz 11.20.0",
        "textures": imported,
        "sourceBehaviour": {
            "option": "TankTreads",
            "english": "TRACK TRACES",
            "russian": "СЛЕДЫ ГУСЕНИЦ",
            "shaderDefines": ["DECAL_TREAD", "FADE_OUT_WITH_TIME"],
            "distanceUniforms": ["treadsNearFadeDistance", "treadsFarFadeDistance"],
            "timeUniform": "fadeOutTimeStartEnd",
            "note": (
                "The shipped shader exposes distance/time fading, while its runtime "
                "constant values live in native client code rather than these assets."
            ),
        },
        "portPolicy": {
            "sampleDistanceMetres": 0.30,
            "distanceFadeStartMetres": 80.0,
            "maximumTrailDistanceMetres": 96.0,
            "timeFadeStartSeconds": 24.0,
            "timeFadeEndSeconds": 30.0,
            "maximumSamplesPerSide": 384,
            "allocation": "one reusable mesh per tank; no object per mark",
        },
        "provenance": provenance,
    }
    MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
    print(f"Blitz 11.20 treads: {len(imported)} textures -> {OUTPUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
