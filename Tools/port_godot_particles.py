#!/usr/bin/env python3
"""Port the tank wreck effect, assets/particles/tanks/explosion1.tscn.

tank_particles_manager.gd hangs one instance of that scene off every battle
tank, scales it by the hull box and starts it from health_component.on_death.
The scene itself is ten GPUParticles3D emitters whose numbers live in
ParticleProcessMaterial, StandardMaterial3D and QuadMesh sub-resources, most of
them left at their engine defaults in the scene text.
Tools/godot_particles_dump.gd instantiates the scene headless and reads the
values the game actually runs; this generator copies the sprite sheets the
emitters draw and writes the manifest the port and its EditMode tests hold the
Unity effect against. Nothing is redrawn or eyeballed.
"""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
from pathlib import Path

GODOT_ROOT = Path("/home/smh01/Studio canaryProjects/Blitz Legacy")
UNITY_ROOT = Path(__file__).resolve().parents[1]
RESOURCES = UNITY_ROOT / "Assets/Blitz/Resources"
TEXTURES = RESOURCES / "Particles/Tanks"
DUMP_SCRIPT = "godot_particles_dump.gd"
TEMP_SCRIPT = "__blitz_particles_dump.gd"

# The scene, the manager that owns it and the death handler that starts it.
SOURCES = [
    "assets/particles/tanks/explosion1.tscn",
    "assets/tanks/base/tank_particles_manager.gd",
    "scripts/particle_starter.gd",
    "assets/tanks/base/base_tank.gd",
]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def record(path: Path) -> dict[str, object]:
    return {
        "source": path.relative_to(GODOT_ROOT).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def run_dump(out: Path) -> dict:
    script = UNITY_ROOT / "Tools" / DUMP_SCRIPT
    if not script.is_file():
        raise SystemExit(f"missing dump script {script}")
    godot = os.environ.get("GODOT", "godot")
    out.mkdir(parents=True, exist_ok=True)
    temp = GODOT_ROOT / TEMP_SCRIPT
    shutil.copyfile(script, temp)
    try:
        result = subprocess.run(
            [
                godot,
                "--headless",
                "--path",
                str(GODOT_ROOT),
                "--script",
                f"res://{TEMP_SCRIPT}",
            ],
            capture_output=True,
            env=dict(os.environ, BLITZ_PARTICLES_OUT=str(out)),
            text=True,
            timeout=900,
            check=False,
        )
    finally:
        temp.unlink(missing_ok=True)

    path = out / "explosion1.json"
    if not path.is_file():
        raise SystemExit(
            f"godot dump produced no explosion1.json (exit {result.returncode}):\n"
            + result.stdout[-4000:]
            + result.stderr[-4000:]
        )
    return json.loads(path.read_text(encoding="utf-8"))


def copy_textures(dump: dict) -> list[dict[str, object]]:
    """Copies every sprite sheet the emitters draw into Resources."""
    TEXTURES.mkdir(parents=True, exist_ok=True)
    copied: dict[str, dict[str, object]] = {}
    for emitter in dump["emitters"]:
        mesh = emitter.get("mesh") or {}
        material = mesh.get("material") or {}
        res = material.get("albedoTexture") or ""
        if not res.startswith("res://"):
            continue
        source = GODOT_ROOT / res[len("res://") :]
        if not source.is_file():
            raise SystemExit(f"missing particle texture {source}")
        key = source.stem
        material["texture"] = key
        if key in copied:
            continue
        shutil.copyfile(source, TEXTURES / source.name)
        entry = record(source)
        entry["resource"] = f"Particles/Tanks/{key}"
        copied[key] = entry
    return [copied[key] for key in sorted(copied)]


def main() -> None:
    dump = run_dump(Path("/tmp/blitz_particles"))
    textures = copy_textures(dump)

    manifest = {
        "generator": Path(__file__).name,
        "policy": (
            "Emitter counts, lifetimes, curves, gradients and sprite sheets "
            "come from a headless Godot dump of explosion1.tscn. Nothing is "
            "redrawn, retimed or measured by eye."
        ),
        "dump": {"script": DUMP_SCRIPT, "scene": dump["scene"]},
        "sources": [record(GODOT_ROOT / relative) for relative in SOURCES],
        "textures": textures,
        "root": dump["root"],
        "emitters": dump["emitters"],
        "placement": {
            # tank_particles_manager.setup: one wreck emitter per tank, parked
            # on the hull and scaled by the hull box.
            "parent": "tank",
            "name": "wreck_effect",
            "scaleFactor": 0.0005,
            "scaleFormula": "height * length * width * 0.0005",
            "positionZ": "length * 0.2",
            "positionY": "height * 0.5",
            "startsHidden": True,
            "startedBy": "health_component.on_death",
        },
        "death": {
            # base_tank.get_destroyed, the rest of what a kill does to a hull.
            "outlineOff": True,
            "previewArmorOff": True,
            "crashedShaderFlag": "crashed",
            "blocked": True,
            "spotted": True,
            "refreshesTracks": True,
            "plateCollisionLayerBit": 7,
        },
    }

    destination = RESOURCES / "Data/tank_explosion_source_manifest.json"
    destination.write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    print(f"wrote {destination.relative_to(UNITY_ROOT)}")
    print(f"emitters: {len(manifest['emitters'])}, textures: {len(textures)}")


if __name__ == "__main__":
    main()
