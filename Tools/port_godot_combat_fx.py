#!/usr/bin/env python3
"""Port the battle effects of the Godot client: muzzle shots, shell impacts,
the GPUTrail3D tracer and the Sprite3D shell.

Tools/godot_combat_fx_dump.gd instantiates every scene headless and reads the
values the game actually runs (engine defaults, curves and gradients baked the
way CurveTexture/GradientTexture1D bake them). This generator copies every
texture those scenes sample, pins the numbers that only live in GDScript code
(sizes, scales, timers) by matching the exact source lines, and writes
Resources/Data/combat_fx_manifest.json, which the Unity port and its EditMode
tests are held against. Nothing is redrawn, retimed or eyeballed.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import uuid
from pathlib import Path

GODOT_ROOT = Path(os.environ.get("BLITZ_GODOT_ROOT", "/home/smh01/projects/blitz-legacy-godot"))
UNITY_ROOT = Path(__file__).resolve().parents[1]
RESOURCES = UNITY_ROOT / "Assets/Blitz/Resources"
TEXTURE_FOLDER = "Particles/Combat"
TEXTURES = RESOURCES / TEXTURE_FOLDER
MANIFEST = RESOURCES / "Data/combat_fx_manifest.json"
DUMP_SCRIPT = "godot_combat_fx_dump.gd"
TEMP_SCRIPT = "__blitz_combat_fx_dump.gd"

PROJECTILE = "assets/tanks/base/projectile/projectile.gd"
BASE_TANK = "assets/tanks/base/base_tank.gd"
STARTER = "scripts/particle_starter.gd"
MANAGER = "assets/tanks/base/tank_particles_manager.gd"
TRAIL_PROCESS = "addons/gputrail/shaders/trail.gdshader"
TRAIL_DRAW = "addons/gputrail/shaders/trail_draw_pass.gdshader"

SOURCES = [PROJECTILE, BASE_TANK, STARTER, MANAGER, TRAIL_PROCESS, TRAIL_DRAW,
           "assets/tanks/base/projectile/trail_preset.tscn"]

NUMBER = r"([0-9]+(?:\.[0-9]+)?)"

# Numbers that live in GDScript code rather than in a resource. Each pattern
# must match exactly one source line; the generator stops otherwise.
CODE_CONSTANTS = [
    ("shellPixelSize", PROJECTILE,
     r"shell\.pixel_size = clampi\(info\.caliber, " + NUMBER + ", " + NUMBER + r"\) \* " + NUMBER,
     ("caliberMin", "caliberMax", "factor")),
    ("trailScale", PROJECTILE,
     r"add_trail_emitter_to_node\(shell\.trail, info, clampi\(info\.caliber, " + NUMBER + ", " + NUMBER
     + r"\) \* " + NUMBER,
     ("caliberMin", "caliberMax", "factor")),
    ("ricochetTrailScale", PROJECTILE,
     r"add_trail_emitter_to_node\(trail, shell_info, self\.pixel_size \* " + NUMBER + r"\)",
     ("pixelSizeFactor",)),
    ("trailAngles", PROJECTILE,
     r"var angles: Array\[float\] = \[" + NUMBER + r", PI \* " + NUMBER + r"\]",
     ("first", "secondTimesPi")),
    ("trailFreeDelay", PROJECTILE, r"Util\.timer\(" + NUMBER + r", tr\.queue_free\)", ("seconds",)),
    ("ricochetTrailFreeDelay", PROJECTILE, r"Util\.timer\(" + NUMBER + r", c\.queue_free\)", ("seconds",)),
    ("impactScale", PROJECTILE,
     r"particle\.scale = Vector3\.ONE \* shell_info\.caliber \* " + NUMBER, ("factor",)),
    ("trailLead", BASE_TANK,
     r"randf_range\(" + NUMBER + ", " + NUMBER + r"\) \* current_shell\(\)\.speed \* " + NUMBER,
     ("randomMin", "randomMax", "speedFactor")),
    ("starterAabb", STARTER, r"var bigg: float = " + NUMBER, ("extent",)),
    ("starterSelfDestroy", STARTER,
     r"Util\.timer\(max_lifetime \+ " + NUMBER + r", self\.queue_free\)", ("extraSeconds",)),
]

# Lines whose presence the port relies on without taking a number from them.
CODE_FACTS = [
    (PROJECTILE, r"shell\.modulate = Color\.BISQUE if info\.type == ShellInfo\.Type\.HEAT else Color\.BEIGE"),
    (PROJECTILE, r"var heat: bool = info\.type == ShellInfo\.Type\.HEAT"),
    (PROJECTILE, r"shell\.billboard = BaseMaterial3D\.BILLBOARD_ENABLED"),
    (PROJECTILE, r"trail\.global_position = self\.global_position"),
    (PROJECTILE, r"if last_hit_dict:"),
    (STARTER, r"if normal != Vector3\.UP: self\.global_basis\.y = normal\.normalized\(\)"),
    (MANAGER, r"if tank\.is_enemy and !tank\.spotted:"),
    (MANAGER, r"particle\.position = tank\.gun_shoot_point\.position"),
    (TRAIL_DRAW, r"UV\.x = \(UV\.x \+ INSTANCE_CUSTOM\.w-1\.0 - 2\.0\)/\(INSTANCE_CUSTOM\.z-1\.0\);"),
    (TRAIL_DRAW, r"VERTEX = mix\(a,b,\(UV\.y-0\.5\)\*h \+ 0\.5\);"),
    (TRAIL_PROCESS, r"vec4 a = EMISSION_TRANSFORM \* vec4\(0,1,0,1\);"),
    (TRAIL_PROCESS, r"vec4 b = EMISSION_TRANSFORM \* vec4\(0,-1,0,1\);"),
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
    godot = os.environ.get("GODOT", "godot")
    out.mkdir(parents=True, exist_ok=True)
    target = out / "combat_fx.json"
    target.unlink(missing_ok=True)
    temp = GODOT_ROOT / TEMP_SCRIPT
    shutil.copyfile(script, temp)
    try:
        result = subprocess.run(
            [godot, "--headless", "--path", str(GODOT_ROOT), "--script", f"res://{TEMP_SCRIPT}"],
            capture_output=True,
            env=dict(os.environ, BLITZ_COMBAT_FX_OUT=str(out)),
            text=True,
            timeout=900,
            check=False,
        )
    finally:
        temp.unlink(missing_ok=True)
    if not target.is_file():
        raise SystemExit(
            f"godot dump produced no combat_fx.json (exit {result.returncode}):\n"
            + result.stdout[-4000:] + result.stderr[-4000:])
    return json.loads(target.read_text(encoding="utf-8"))


def code_constants() -> dict[str, dict[str, object]]:
    found: dict[str, dict[str, object]] = {}
    for key, relative, pattern, names in CODE_CONSTANTS:
        lines = (GODOT_ROOT / relative).read_text(encoding="utf-8").splitlines()
        matches = [(n + 1, line, re.search(pattern, line)) for n, line in enumerate(lines)]
        matches = [m for m in matches if m[2]]
        if len(matches) != 1:
            raise SystemExit(f"{relative}: expected one line for {key}, found {len(matches)}")
        number, line, match = matches[0]
        entry: dict[str, object] = {"source": f"{relative}:{number}", "line": line.strip()}
        for name, value in zip(names, match.groups()):
            entry[name] = float(value)
        found[key] = entry
    for relative, pattern in CODE_FACTS:
        text = (GODOT_ROOT / relative).read_text(encoding="utf-8")
        if not re.search(pattern, text):
            raise SystemExit(f"{relative}: the port relies on a line matching {pattern!r}")
    return found


def texture_meta(path: Path) -> None:
    """Import settings that reproduce Godot's sampling of these PNGs.

    Godot imports them with mipmaps, without premultiplying or bleeding colour
    into transparent texels (fix_alpha_border off) and at full size. Materials
    pick the filter; the shader reads mip 0 where Godot's filter has no
    mipmaps, so one trilinear, repeating import serves every material.
    """
    meta = path.with_name(path.name + ".meta")
    guid = uuid.uuid5(uuid.NAMESPACE_URL, "blitz-legacy/" + TEXTURE_FOLDER + "/" + path.name).hex
    if meta.is_file():
        match = re.search(r"^guid: ([0-9a-f]{32})$", meta.read_text(encoding="utf-8"), re.M)
        if match:
            guid = match.group(1)
    platforms = []
    for target, fmt, compression, quality, overridden in (
            ("DefaultTexturePlatform", -1, 1, 50, 0),
            ("Standalone", -1, 1, 50, 0),
            ("Android", 48, 1, 100, 1)):
        platforms.append(
            f"""  - serializedVersion: 4
    buildTarget: {target}
    maxTextureSize: 4096
    resizeAlgorithm: 0
    textureFormat: {fmt}
    textureCompression: {compression}
    compressionQuality: {quality}
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: {overridden}
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
""")
    meta.write_text(
        f"""fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: 1
    sRGBTexture: 1
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMipmapLimit: 1
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 4096
  textureSettings:
    serializedVersion: 2
    filterMode: 2
    aniso: 0
    mipBias: 0
    wrapU: 0
    wrapV: 0
    wrapW: 0
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 50
  spriteMode: 0
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: 1
  alphaIsTransparency: 0
  spriteTessellationMethod: 0
  spriteTessellationDetail: -1
  spriteGeometrySubdivision: -1
  textureType: 0
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  swizzle: 50462976
  cookieLightType: 0
  platformSettings:
{''.join(platforms)}  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    customData: 
    physicsShape: []
    bones: []
    spriteID: 
    internalID: 0
    vertices: []
    indices: 
    edges: []
    weights: []
    secondaryTextures: []
    spriteCustomMetadata:
      entries: []
    nameFileIdTable: {{}}
  mipmapLimitGroupName: 
  pSDRemoveMatte: 0
  userData: 
  assetBundleName: 
  assetBundleVariant: 
""",
        encoding="utf-8")


def import_settings(source: Path) -> dict[str, object]:
    text = source.with_name(source.name + ".import").read_text(encoding="utf-8")
    def value(key: str) -> str:
        match = re.search(r"^" + re.escape(key) + r"=(.*)$", text, re.M)
        return match.group(1).strip() if match else ""
    settings = {
        "mipmaps": value("mipmaps/generate") == "true",
        "compressMode": int(value("compress/mode") or 0),
        "premultAlpha": value("process/premult_alpha") == "true",
        "fixAlphaBorder": value("process/fix_alpha_border") == "true",
    }
    if not settings["mipmaps"] or settings["premultAlpha"] or settings["fixAlphaBorder"]:
        raise SystemExit(f"{source}: import settings the Unity import does not reproduce: {settings}")
    return settings


def copy_texture(res: str, copied: dict[str, dict[str, object]]) -> str:
    if not res.startswith("res://"):
        raise SystemExit(f"unexpected texture path {res!r}")
    source = GODOT_ROOT / res[len("res://"):]
    if not source.is_file():
        raise SystemExit(f"missing texture {source}")
    key = source.stem
    if key not in copied:
        TEXTURES.mkdir(parents=True, exist_ok=True)
        target = TEXTURES / source.name
        shutil.copyfile(source, target)
        texture_meta(target)
        entry = record(source)
        entry["resource"] = f"{TEXTURE_FOLDER}/{key}"
        entry["import"] = import_settings(source)
        copied[key] = entry
    return key


def main() -> None:
    dump = run_dump(Path("/tmp/blitz_combat_fx"))
    if dump["warnings"]:
        raise SystemExit("the scenes use features the port does not carry:\n" + "\n".join(dump["warnings"]))

    copied: dict[str, dict[str, object]] = {}
    scenes = {}
    for scene in dump["scenes"]:
        for emitter in scene["emitters"]:
            material = emitter["mesh"]["material"]
            material["texture"] = copy_texture(material["albedoTexture"], copied)
        scenes[scene["name"]] = scene
    trail = dump["trail"]
    trail["textureId"] = copy_texture(trail["texture"], copied)
    bindings = dump["bindings"]
    bindings["shellTextureId"] = copy_texture(bindings["shellTexture"], copied)

    manifest = {
        "generator": Path(__file__).name,
        "policy": (
            "Emitters, curves, gradients, textures and the tracer preset come from a "
            "headless Godot dump of the scenes the client spawns; numbers that only "
            "live in GDScript are matched on their exact source lines. Nothing is "
            "redrawn, retimed or measured by eye."
        ),
        "godotVersion": dump["godotVersion"],
        "renderingMethod": dump["renderingMethod"],
        "dump": {"script": DUMP_SCRIPT},
        "sources": [record(GODOT_ROOT / relative) for relative in SOURCES],
        "textures": [copied[key] for key in sorted(copied)],
        "code": code_constants(),
        "bindings": bindings,
        "trail": trail,
        "scenes": scenes,
    }
    text = json.dumps(manifest, indent=1, ensure_ascii=False)
    # Keep number arrays (vectors, texels) on one line each.
    text = re.sub(r"\[\s*(-?[0-9][-0-9.eE+,\s]*?)\s*\]",
                  lambda m: "[" + ",".join(x.strip() for x in m.group(1).split(",")) + "]", text)
    MANIFEST.write_text(text + "\n", encoding="utf-8")
    print(f"wrote {MANIFEST.relative_to(UNITY_ROOT)}")
    print(f"scenes: {len(scenes)}, textures: {len(copied)}")


if __name__ == "__main__":
    main()
