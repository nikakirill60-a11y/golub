#!/usr/bin/env python3
"""Export the geometry of every researchable turret and gun of the branch.

The cards already carry the research graph of the Blitz 4.8.0 client
(`Tools/port_blitz_branch.py`), and every turret and gun names the client mesh
slot it installs (`blitzPartName`: `turret_01`, `gun_05` ...). The models that
shipped with the port came out of the decompiled Godot build, which only ever
exported the *top* turret with a handful of guns and renamed them `turret01` /
`gun01..gun04`, so nothing in them can be addressed by slot.

This script re-exports the visible model, the physics hull, the armour shells
and the pivot table straight from the client pack with the Godot project's own
converter, `tools/blitz2glb.py`, so the geometry, the pivots and the armour
zones all come from one source:

* one converter run per (turret, gun) pair, because the converter mounts one
  configuration at a time and only the mounted gun gets its mantlet cut out of
  the collision scene;
* the hull, the tracks and the wheels are taken from the first run of a card,
  the turret shell from the first run of each turret, and the gun, its muzzle
  marker and its mantlet from every run;
* the converter names whatever it mounts `turret01` / `gun01`, so every node is
  renamed back to the client slot the card asks for, which is what lets the
  runtime show the installed module and hide the rest;
* armour thickness stays in the client's own numbers: `primaryArmor` names the
  front, side and rear plates of a part, the roof takes the thinnest plate of
  the same block, and the mantlet takes the gun's own first plate.

`--textures` additionally re-exports the albedo and the track sheet of a card
out of the same pack with Godot's `tools/pvr2png.py`. The meshes are UV-mapped
against the client atlases, so a card whose sheet came from anywhere else can
sample empty texels instead of paint.

Run:  python3 Tools/port_blitz_branch_models.py [--cards ms-1 ...] [--rebuild]
                                                [--textures]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))

from port_godot_hangar import clean_name, sha256  # noqa: E402
from port_blitz_branch import BRANCH, PACK, VEHICLES, text  # noqa: E402

# The exporter imports the Godot project's `tools/dava.py` and `blitz2glb.py`.
# The checkout has moved between machines, so honour an explicit override and
# fall back to the known locations instead of failing on a hardcoded path.
GODOT = Path(
    os.environ.get("GODOT_PROJECT")
    or next(
        (
            str(candidate)
            for candidate in (
                Path.home() / "projects" / "blitz-legacy-godot",
                Path.home() / "Studio canaryProjects" / "Blitz Legacy",
            )
            if (candidate / "tools" / "dava.py").is_file()
        ),
        str(Path.home() / "Studio canaryProjects" / "Blitz Legacy"),
    )
)
UNITY = Path(__file__).resolve().parent.parent
RESOURCES = UNITY / "Assets/Blitz/Resources"
GLB_CACHE = Path(__file__).resolve().parent / "_branch_glb"
TANKS = PACK / "3d/Tanks"
MODEL_DIR = "USSR"
COLLISION_PREFIX = "ussr-"

# The client ships one collision scene per vehicle under a nation prefix, and
# it is not always spelled like the model.
COLLISION = {"t62a": "ussr-T62A"}
MODEL = {"t62a": "T-62A"}

# The client keeps the albedo sheets next to the scene and the scene binds them
# by name. The meshes are UV-mapped against exactly those atlases, so a vehicle
# whose sheet comes from anywhere else can sample empty texels: the Godot build
# shipped a newer, differently packed MS-1 and BT-2 albedo (1024x512 instead of
# the 1024x1024 the client scene maps), which is why those two hulls rendered as
# black and white blocks once the geometry came from the pack.
IMAGES = TANKS / "USSR/images"
TEXTURE_REF = re.compile(rb"images/[A-Za-z0-9_ .\-]+\.tex")

ZONES = ("front", "side", "rear", "roof")
TRACK_ARMOR = 20


# Set by an exporter that names exact armour panels (`blitz_480_collision`):
# maps a plate name to its authored screen flag, or None when the name carries
# none and the caller's default stands.
PLATE_SCREEN = None


def unity_id(card_name: str) -> str:
    return "".join(c for c in card_name if c.isalnum()).upper()


def vehicle_root(file_name: str):
    return ET.parse(VEHICLES / (file_name + ".xml")).getroot()


def part_slot(node) -> str:
    blitz = node.find("blitz")
    return text(blitz.find("blitzPartName")) if blitz is not None else None


def armor_values(node) -> dict[str, int]:
    block = node.find("armor")
    if block is None:
        return {}
    values = {}
    for plate in block:
        # A plate can carry a <vehicleDamageFactor> child, and its text then
        # runs straight into the thickness ("18" + "0.0"), so only the node's
        # own text is read.
        own = (plate.text or "").split()
        if not own:
            continue
        try:
            values[plate.tag] = int(round(float(own[0])))
        except ValueError:
            continue
    return values


def armor_zones(node) -> dict[str, int]:
    """front/side/rear from `primaryArmor`, roof from the thinnest plate."""
    values = armor_values(node)
    if not values:
        return {zone: 0 for zone in ZONES}

    primary = (text(node.find("primaryArmor")) or "").split()
    ordered = [values.get(name, 0) for name in primary[:3]]
    while len(ordered) < 3:
        ordered.append(max(values.values()) if values else 0)

    positive = [value for value in values.values() if value > 0]
    roof = min(positive) if positive else 0
    return {
        "front": ordered[0],
        "side": ordered[1],
        "rear": ordered[2],
        "roof": roof,
    }


def mask_thickness(gun_node) -> int:
    """The mantlet: the gun's own first plate, the way the client lists it."""
    values = armor_values(gun_node)
    if not values:
        return 0
    if "armor_1" in values:
        return values["armor_1"]
    return max(values.values())


SKIN_SHEET = re.compile(r"_skin\d*$")


def client_textures(file_name: str) -> tuple[Path, Path]:
    """The albedo and the track sheet the client scene of a vehicle binds."""
    blob = (TANKS / MODEL_DIR / (file_name + ".sc2")).read_bytes()
    names: list[str] = []
    for match in TEXTURE_REF.findall(blob):
        name = match.decode("ascii")[len("images/"):-len(".tex")]
        # Normal maps and paint skins are not part of the ported material.
        lowered = name.casefold()
        if lowered.endswith(("_nm", "_skin")) or name in names:
            continue
        names.append(name)
    tracks = [name for name in names
              if "track" in name.lower() or "tarck" in name.lower()]
    body = [name for name in names if name not in tracks]
    # Numbered paint skins (`GB83_4005_skin2` next to `GB83_FV4005`) have a
    # UV layout of their own: binding one as the hull sheet painted black
    # smears over the FV4005 hull while its turret, bound per node, was fine.
    plain = [name for name in body if not SKIN_SHEET.search(name.casefold())]
    if plain:
        body = plain
    # Event repaints bind the base sheet of the donor vehicle next to their own
    # skin, and the two spellings differ only in punctuation (`IS_2_Berlin.sc2`
    # binds `IS-2_Berlin`), so the match ignores separators.
    def key(name: str) -> str:
        return "".join(c for c in name.casefold() if c.isalnum())

    if len(body) > 1:
        exact = [name for name in body if key(name) == key(file_name)]
        if len(exact) == 1:
            body = exact
    if len(tracks) > 1:
        exact = [name for name in tracks if key(name).startswith(key(file_name))]
        if len(exact) == 1:
            tracks = exact
    if len(body) > 1:
        # A scene can bind a part sheet next to the hull sheet, and the part
        # sheet only covers that part's UVs: `Oth08_Vindicator.sc2` binds the
        # 256px `Oth10_Vindicator_gun` before the 1024px `Oth10_Vindicator`,
        # and the card id (`Oth08`) matches neither, so the exact match above
        # cannot pick the hull sheet. A part sheet always extends the name of
        # the hull sheet it belongs to, so drop every candidate that does.
        base = [
            name
            for name in body
            if not any(
                other != name and key(name).startswith(key(other))
                for other in body
            )
        ]
        if len(base) == 1:
            body = base
    if len(body) > 1:
        # Crossover vehicles ship several faction repaints in one scene
        # (Predator: Ultramarines, Dark Angels, Imperial Fists). The client
        # renders the first bound sheet unless a skin is selected, and skins
        # are out of scope for the port.
        body = body[:1]
    if len(body) != 1 or len(tracks) != 1:
        raise RuntimeError(f"{file_name}: ambiguous texture set {names}")

    def resolve(name: str) -> Path:
        try:
            return resolve_client_texture(name)
        except RuntimeError as error:
            raise RuntimeError(f"{file_name}: {error}") from error

    return resolve(body[0]), resolve(tracks[0])


def texture_id(name: str) -> str:
    """Stable Unity resource id for one SC2-bound albedo sheet."""
    return "".join(c for c in name if c.isalnum() or c == "_").upper()


def resolve_client_texture(name: str) -> Path:
    """The pack file behind an `images/...` reference of a client scene.

    Lend-lease, captured and crossover vehicles reuse the sheets of the nation
    that designed them while the scene still spells the reference as a local
    resource. The pack keeps those shared bytes in the donor folder, and for a
    few sheets it keeps byte-identical copies in several folders - any of them
    is the same texture, so a duplicate is not an ambiguity.
    """
    local = IMAGES / (name + ".mali.pvr")
    if local.is_file():
        return local
    matches = sorted(TANKS.glob(f"*/images/{name}.mali.pvr"))
    if not matches:
        raise RuntimeError(f"texture {name} resolved to {matches}")
    if len(matches) > 1:
        digests = {hashlib.sha256(match.read_bytes()).hexdigest() for match in matches}
        if len(digests) != 1:
            raise RuntimeError(f"texture {name} resolved to {matches}")
    return matches[0]


def client_albedos(file_name: str) -> dict[str, str]:
    """Resolve each original SC2 render node to its inherited albedo sheet."""
    sys.path.insert(0, str(GODOT / "tools"))
    import dava
    import blitz2glb

    path = TANKS / MODEL_DIR / (file_name + ".sc2")
    raw = dava.read_sc2(path.read_bytes())
    materials = {
        int.from_bytes(bytes(node["#id"]), "little"): node
        for node in raw.get("dataNodes", [])
        if node.get("##name") == "NMaterial" and "#id" in node
    }

    def inherited(material_id):
        seen = set()
        while material_id is not None and material_id not in seen:
            seen.add(material_id)
            material = materials.get(int(material_id))
            if material is None:
                return None
            for key in ("configArchive_0", "configArchive"):
                albedo = (material.get(key) or {}).get("textures", {}).get("albedo")
                if albedo:
                    return Path(albedo).stem
            albedo = (material.get("textures") or {}).get("albedo")
            if albedo:
                return Path(albedo).stem
            material_id = material.get("parentMaterialKey")
        return None

    result = {}
    for node in blitz2glb.read_scene(str(path), 0):
        for _group, material_id, _lod in node.get("batches", []):
            albedo = inherited(material_id)
            if albedo:
                result[str(node.get("name") or "")] = albedo
                break
    return result


def decode_rgba5551(source: Path, destination: Path) -> bool:
    """Decode the single RGBA5551 sheet of the pack (Other/AMX_Dracula).

    The shared Godot decoder covers every format the pack uses except this one
    sheet, so the fallback stays here instead of forking that tool.
    """
    import struct

    from PIL import Image

    blob = source.read_bytes()
    if len(blob) < 52 or blob[:4] != b"PVR\x03":
        return False
    if blob[8:16] != b"rgba\x05\x05\x05\x01":
        return False
    height, width = struct.unpack_from("<II", blob, 24)
    offset = 52 + struct.unpack_from("<I", blob, 48)[0]
    size = width * height * 2
    payload = blob[offset:offset + size]
    if len(payload) != size:
        return False
    pixels = bytearray(width * height * 4)
    for index in range(width * height):
        packed = payload[index * 2] | (payload[index * 2 + 1] << 8)
        # Channels are packed from the top bit down, matching the RGBA4444
        # path of the shared decoder.
        pixels[index * 4 + 0] = round(((packed >> 11) & 0x1F) * 255 / 31)
        pixels[index * 4 + 1] = round(((packed >> 6) & 0x1F) * 255 / 31)
        pixels[index * 4 + 2] = round(((packed >> 1) & 0x1F) * 255 / 31)
        pixels[index * 4 + 3] = 255 if packed & 1 else 0
    destination.parent.mkdir(parents=True, exist_ok=True)
    Image.frombytes("RGBA", (width, height), bytes(pixels)).save(destination)
    return True


def decode_texture(source: Path, destination: Path, bleed: bool = False) -> dict:
    """Godot's own PVR decoder, so the PNG keeps the client's own pixels."""
    sys.path.insert(0, str(GODOT / "tools"))
    import pvr2png

    destination.parent.mkdir(parents=True, exist_ok=True)
    if pvr2png.main([str(source), "--out", str(destination)]):
        if not decode_rgba5551(source, destination):
            raise RuntimeError(f"pvr2png failed on {source}")
    if bleed:
        bleed_cleared(destination)
    return {
        "source": str(source.relative_to(PACK)),
        "destination": destination.name,
        "bytes": destination.stat().st_size,
        "sha256": sha256(destination),
    }


# Matches `_Cutoff` of Resources/Materials/MS1BattleTracks, the material both
# the hangar and the battle clone for a track sheet.
TRACK_CUTOFF = 26
TRACK_WHITE = 230


def bleed_cleared(path: Path, cutoff: int = TRACK_CUTOFF) -> None:
    """Push track paint through clipped and accidentally white texels.

    The client stores white RGB both under the alpha the shader clips away and
    in a few opaque texels at the edge of the A-20/BT-2 track islands. The
    latter are not removed by the alpha test, so they show up as white teeth
    even after the transparent texels have been dilated. Bilinear filtering
    and the mip chain make either kind visible on the neighbouring links.

    Treat near-white texels as holes in the paint, then dilate the nearest
    non-white track colour into every hole. The alpha channel stays untouched:
    the clip still cuts exactly the same texels and only the sampled RGB is
    corrected. This also makes the result deterministic at the texture border
    where the mesh uses repeat wrapping.
    """
    import numpy as np
    from PIL import Image

    pixels = np.array(Image.open(path).convert("RGBA"))
    # Run two dilation passes. The second pass makes the invariant hold for
    # the newly painted texels at the edge of a hole as well as for the raw
    # source texels, without changing alpha or the texture's dimensions.
    for _ in range(2):
        colour = pixels[:, :, :3].astype(np.int16)
        painted = pixels[:, :, 3] >= cutoff
        near_white = colour.min(axis=2) >= TRACK_WHITE
        source = painted & ~near_white
        if source.all() or not source.any():
            break

        filled = source.copy()
        while not filled.all():
            total = np.zeros(colour.shape, dtype=np.int32)
            count = np.zeros(filled.shape, dtype=np.int32)
            for axis, shift in ((0, 1), (0, -1), (1, 1), (1, -1)):
                neighbour = np.roll(filled, shift, axis=axis)
                values = np.roll(colour, shift, axis=axis)
                total += np.where(neighbour[:, :, None], values, 0)
                count += neighbour
            grow = ~filled & (count > 0)
            if not grow.any():
                break
            averaged = total // np.maximum(count, 1)[:, :, None]
            colour = np.where(grow[:, :, None], averaged, colour)
            filled |= grow

        pixels[:, :, :3] = colour.astype(np.uint8)

    Image.fromarray(pixels, "RGBA").save(path)


def port_textures(
    tank_id: str, file_name: str, albedos: dict[str, str] | None = None
) -> list[dict]:
    """Re-export every SC2-bound albedo plus the alpha-tested track sheet."""
    body, tracks = client_textures(file_name)
    root = RESOURCES / f"Models/Tanks/{tank_id}/Textures"
    result = [
        decode_texture(body, root / f"{tank_id}.png"),
        decode_texture(tracks, root / f"{tank_id}_tracks.png", bleed=True),
    ]
    body_name = body.name.removesuffix(".mali.pvr")
    track_name = tracks.name.removesuffix(".mali.pvr")
    for name in sorted(set((albedos or {}).values())):
        if name in (body_name, track_name):
            continue
        result.append(
            decode_texture(
                resolve_client_texture(name), root / f"{texture_id(name)}.png"
            )
        )
    return result


def authored_pivot(node) -> tuple | None:
    """A part's own pivot in tank space, from the client's `blitz` block.

    Every hull, turret and gun carries `blitz/points/normal`, the negated pivot
    in DAVA axes - the same value the models write into their `*_POINT` nodes,
    and per slot, so two turrets of one vehicle keep their own rings.  Bounding
    box inference instead centres the collision shell inside the visible part,
    which buried half of the O-47 turret shell inside the hull.
    """
    blitz = node.find("blitz") if node is not None else None
    normal = blitz.find("points/normal") if blitz is not None else None
    if normal is None:
        return None
    # A few vehicles write the vector comma separated.
    values = [float(value) for value in text(normal).replace(",", " ").split()]
    if len(values) != 3 or not any(values):
        # A casemate tank destroyer mounts its gun on the hull and authors no
        # ring at all; its "turret" slot rides the gun instead.
        return None
    return tuple(-value for value in values)


def client_parts(file_name: str) -> dict:
    """Slot -> XML node for the hull, every turret and every gun."""
    root = vehicle_root(file_name)
    hull = root.find("hull")
    turrets = {}
    for turret in root.find("turrets0"):
        slot = part_slot(turret)
        if not slot:
            continue
        guns = {}
        for gun in turret.find("guns"):
            gun_slot = part_slot(gun)
            if gun_slot:
                guns[gun_slot] = gun
        turrets[slot] = {"node": turret, "guns": guns}
    return {"hull": hull, "turrets": turrets}


def convert(
    card_id: str,
    file_name: str,
    parts: dict,
    turret_slot: str,
    gun_slot: str,
    rebuild: bool,
) -> Path:
    """Run the Godot converter for one (turret, gun) configuration."""
    out = GLB_CACHE / f"{card_id}__{turret_slot}__{gun_slot}.glb"
    if out.exists() and not rebuild:
        return out

    model = TANKS / MODEL_DIR / (MODEL.get(card_id, file_name) + ".sc2")
    collision = TANKS / "CollisionMeshes" / (
        COLLISION.get(card_id, COLLISION_PREFIX + file_name) + ".sc2"
    )
    turret = parts["turrets"][turret_slot]
    hull_armor = armor_zones(parts["hull"])
    turret_armor = armor_zones(turret["node"])

    argv = [
        str(model),
        # The pack ships the geometry inside the scene instead of a sidecar.
        "--scg", str(model),
        "--collision", str(collision),
        "--collision-scg", str(collision),
        "--out", str(out),
        "--name", card_id,
        "--turret-slot", turret_slot,
        "--gun-slot", gun_slot,
        "--armor-mask", str(mask_thickness(turret["guns"][gun_slot])),
        "--armor-tracks", str(TRACK_ARMOR),
    ]
    turret_pivot = authored_pivot(turret["node"])
    if turret_pivot is not None:
        argv.append("--turret-position")
        argv += ["%.6f" % value for value in turret_pivot]
    # The gun carries its own `blitz/points/normal`, the trunnion the client
    # elevates the barrel around. Inference takes the centre of the gun-mask
    # or collision box instead, which on an open mount lands metres down the
    # barrel - the Pz.Sfl. IVc Flak 41 by 3.07 m, so the gun flew off the hull
    # the moment it elevated.
    gun_pivot = authored_pivot(turret["guns"][gun_slot])
    if gun_pivot is not None:
        argv.append("--gun-position")
        argv += ["%.6f" % value for value in gun_pivot]
    for zone in ZONES:
        argv += [f"--armor-hull-{zone}", str(hull_armor[zone])]
        argv += [f"--armor-turret-{zone}", str(turret_armor[zone])]

    out.parent.mkdir(parents=True, exist_ok=True)
    sys.path.insert(0, str(GODOT / "tools"))
    import blitz2glb

    import io
    import contextlib

    log = io.StringIO()
    with contextlib.redirect_stdout(log):
        code = blitz2glb.main(argv)
    if code:
        raise RuntimeError(f"{card_id}: blitz2glb failed on {turret_slot}/{gun_slot}")
    for line in log.getvalue().splitlines():
        if "WARNING" in line or "ignored" in line:
            print(f"  {card_id} {turret_slot}/{gun_slot}: {line.strip()}")
    return out


def GUN_EXTRAS(card_id, file_name, parts, turret_slot, gun_slot, rebuild):
    """Extra `(glb, object name)` exports of one gun slot; none by default."""
    return []


def SHEET_EXTRAS(card_id, file_name, parts, turret_slot, gun_slot, rebuild, root):
    """`(glb, index, sheet)` exports of the batches of part `root` (`hull`,
    `turret_NN`, `gun_NN`) bound to a sheet other than the part's own; none by
    default."""
    return []


def GUN_ANIMATIONS(card_id, file_name, gun_slot, pivots):
    """Baked clips (`{label: {...}}`) of one gun slot's extras; none by default."""
    return {}


def scene_of(path: Path):
    import trimesh

    return trimesh.load(path, force="scene", process=False)


def node_named(scene, prefix: str) -> str:
    """The single node whose name starts with `prefix`, or None."""
    found = [str(name) for name in scene.graph.nodes if str(name).startswith(prefix)]
    return found[0] if len(found) == 1 else (found[0] if found else None)


# glTF primitives carry exactly one material, so the converter splits a client
# part that paints itself from several materials into sibling nodes named
# `<part>_<6 hex>`; the node the card addresses then keeps the transform and no
# geometry at all. SU-152's superstructure is such a part: the visible OBJ lost
# its hull while the physics hull, the armour shells and the bots kept using it,
# so the vehicle rendered as tracks and a floating gun. Resolve every part to
# the mesh nodes that actually belong to it.
SPLIT_SUFFIX = re.compile(r"^(?P<base>.+)_[0-9a-f]{6}$")


def split_base(node_name: str, names) -> str | None:
    """The part a converter split belongs to, or None for an authored node."""
    match = SPLIT_SUFFIX.match(node_name)
    if match is None:
        return None
    base = match.group("base")
    return base if base in names else None


def authored_nodes(scene) -> set[str]:
    """Scene node names without the converter's per-material splits."""
    names = {str(node) for node in scene.graph.nodes}
    return {name for name in names if split_base(name, names) is None}


def part_nodes(scene, node_name: str) -> list[str]:
    """Every mesh node of one client part: the node itself and its splits."""
    names = {str(node) for node in scene.graph.nodes}
    resolved = []
    for candidate in [node_name] + sorted(names):
        if candidate != node_name and split_base(candidate, names) != node_name:
            continue
        if candidate in resolved or candidate not in names:
            continue
        if scene.graph.get(candidate)[1] in scene.geometry:
            resolved.append(candidate)
    return resolved


# A collision shell the converter leaves in part-local space sinks below the
# vehicle it stands for, so the hit box and the sniper armour overlay end up
# under the tracks (Churchill Mk VI, Jagdtiger 8.8, SU-100, Sturer Emil ...).
# `blitz2glb.validated_hull_pivot` recovers that translation from the bounding
# boxes, but it measures the whole visible hull, and a centimetre-wide antenna
# stretches that box by metres: the shell then looks like a short lower-hull
# proxy and keeps the part-local origin the client authored it around.
#
# Measure the displacement instead of the shape. An armoured hull never floats
# below the hull it armours: on every correctly placed card the collision floor
# stays within 8 cm under the lowest rendered hull vertex, so a deeper shell is
# displaced and is grounded on that vertex.
SUNKEN_HULL_MIN_DEPTH = 0.10


def obj_y_bounds(path: Path, only: str | None = None):
    current = None
    low, high = None, None
    for line in path.read_text(encoding="utf-8").splitlines():
        if line[:2] in ("o ", "g "):
            current = line[2:].strip()
        elif line[:2] == "v ":
            if only is not None and current != only:
                continue
            y = float(line.split()[2])
            low = y if low is None else min(low, y)
            high = y if high is None else max(high, y)
    return None if low is None else (low, high)


def lift_obj(path: Path, lift: float, only: set[str] | None = None) -> None:
    """Raise every vertex of an OBJ, or only the listed objects, by `lift`."""
    lines = path.read_text(encoding="utf-8").splitlines(keepends=True)
    current = None
    for index, line in enumerate(lines):
        if line[:2] in ("o ", "g "):
            current = line[2:].strip()
        elif line[:2] == "v ":
            if only is not None and current not in only:
                continue
            x, y, z = (float(value) for value in line.split()[1:4])
            lines[index] = f"v {x:.7f} {y + lift:.7f} {z:.7f}\n"
    path.write_text("".join(lines), encoding="utf-8")


def ground_sunken_collision(
    visible: Path, physics: Path, armor: Path, hull_plates: set[str], card: str
) -> None:
    """Ground a collision shell the converter left under the vehicle."""
    hull = obj_y_bounds(visible, "hull_vis")
    shell = obj_y_bounds(physics)
    if hull is None or shell is None:
        return
    lift = hull[0] - shell[0]
    if lift < SUNKEN_HULL_MIN_DEPTH:
        return
    # The physics hull and the hull armour plates are cut from the same
    # collision shell, so both carry the same displacement. Turret and gun
    # plates stay untouched: they are stored in their own part space and the
    # runtime mounts them on the pivots.
    lift_obj(physics, lift)
    lift_obj(armor, lift, hull_plates)
    print(f"  {card}: grounded the sunken collision shell by {lift:.3f} m")


# The same displacement happens one level down. A turret collision shell is
# authored around the turret's own mounting plane, but a few cards (Dracula,
# D2, E 50 `turret_02`, Krupp 38D ...) put the turret pivot well below that
# plane: the shell then hangs inside the hull, the visible turret keeps no
# collision of its own and the hull grows a lump where nothing is drawn.
#
# Measure the displacement the same way as for the hull: a part's collision
# shell never floats below the part it armours, so a shell whose floor sits
# more than `SUNKEN_MOUNT_MIN_DEPTH` under the lowest visible vertex of that
# part is displaced, and is grounded on that vertex.
SUNKEN_MOUNT_MIN_DEPTH = 0.10


def ground_sunken_mounts(
    visible: Path,
    armor: Path,
    mount_plates: dict,
    pivots: dict,
    card: str,
) -> None:
    """Ground every turret/gun collision shell left under its own part."""
    for mount in sorted(mount_plates):
        plates = mount_plates[mount]
        if not plates:
            continue
        part = obj_y_bounds(visible, mount)
        shell = None
        for plate in sorted(plates):
            plate_bounds = obj_y_bounds(armor, plate)
            if plate_bounds is None:
                continue
            shell = plate_bounds if shell is None else (
                min(shell[0], plate_bounds[0]),
                max(shell[1], plate_bounds[1]),
            )
        if part is None or shell is None:
            continue
        # The plates live in the mount's own space and the visible part in tank
        # space, so the comparison goes through the exported pivot.
        pivot = pivots.get(mount)
        pivot_y = float(pivot["world"][1]) if pivot else 0.0
        floor_gap = (part[0] - pivot_y) - shell[0]
        roof_gap = (part[1] - pivot_y) - shell[1]
        # Only a shell that is *entirely* under its part is displaced. A
        # mantlet mask that merely reaches a little below the barrel it wraps
        # (A-20's 20 mm guns) is the authored shape and stays where it is.
        lift = min(floor_gap, roof_gap)
        if lift < SUNKEN_MOUNT_MIN_DEPTH:
            continue
        lift = floor_gap
        lift_obj(armor, lift, plates)
        print(f"  {card}: grounded the sunken {mount} shell by {lift:.3f} m")


def write_objects(
    items: list[tuple[Path, str, str]],
    destination: Path,
    material_for,
    materials: dict[str, str],
    groups: bool = False,
    record_objects: list | None = None,
) -> dict[str, int]:
    """`port_godot_hangar.write_obj`, but merging several converter runs.

    Every mesh is baked with its authored transform, so parts exported in
    different runs still land in the one tank space the runtime expects.
    """
    destination.parent.mkdir(parents=True, exist_ok=True)
    mtl_path = destination.with_suffix(".mtl")

    vertex_offset = 1
    totals = {"objects": 0, "vertices": 0, "triangles": 0}
    scenes: dict[Path, object] = {}

    with destination.open("w", encoding="utf-8", newline="\n") as obj:
        obj.write("# Generated from the original Blitz 4.8.0 client geometry.\n")
        obj.write(f"mtllib {mtl_path.name}\n")

        for item in items:
            source, node_name, out_name = item[:3]
            relative_to = item[3] if len(item) > 3 else None
            scene = scenes.get(source)
            if scene is None:
                scene = scenes[source] = scene_of(source)
            mount_transform = None
            if relative_to:
                mount_transform = np.asarray(scene.graph.get(relative_to)[0], dtype=float)

            # Some SC2 scenes expose transform-only wheel/group nodes whose
            # names match the visible-part prefixes. They carry no geometry and
            # must not become empty OBJ objects. A part split per material does
            # carry geometry, just in its sibling nodes, and is merged back into
            # the one object the runtime addresses by slot.
            vertex_blocks: list[np.ndarray] = []
            uv_blocks: list[np.ndarray] = []
            normal_blocks: list[np.ndarray] = []
            face_blocks: list[np.ndarray] = []
            part_offset = 0
            for mesh_node in part_nodes(scene, node_name):
                transform, geometry_name = scene.graph.get(mesh_node)
                if relative_to:
                    transform = np.linalg.inv(mount_transform) @ np.asarray(
                        transform, dtype=float
                    )
                source_geometry = scene.geometry[geometry_name]
                # Trimesh.copy() drops its normal cache by default. Capture the
                # authored glTF normals before copying, not recomputed smoothing.
                source_normals = np.asarray(source_geometry.vertex_normals, dtype=np.float64).copy()
                mesh = source_geometry.copy()
                mesh.apply_transform(transform)
                part_vertices = np.asarray(mesh.vertices, dtype=np.float64)
                part_faces = np.asarray(mesh.faces, dtype=np.int64)
                normal_matrix = np.linalg.inv(np.asarray(transform)[:3, :3]).T
                part_normals = source_normals @ normal_matrix.T
                lengths = np.linalg.norm(part_normals, axis=1, keepdims=True)
                part_normals = np.divide(
                    part_normals, lengths, out=np.zeros_like(part_normals), where=lengths > 0
                )
                visual_uv = getattr(mesh.visual, "uv", None)
                if visual_uv is None or len(visual_uv) != len(part_vertices):
                    part_uv = np.zeros((len(part_vertices), 2), dtype=np.float64)
                else:
                    part_uv = np.asarray(visual_uv, dtype=np.float64)

                if len(part_vertices) == 0 or len(part_faces) == 0:
                    continue

                vertex_blocks.append(part_vertices)
                uv_blocks.append(part_uv)
                normal_blocks.append(part_normals)
                face_blocks.append(part_faces + part_offset)
                part_offset += len(part_vertices)

            if not vertex_blocks:
                continue

            vertices = np.concatenate(vertex_blocks)
            uv = np.concatenate(uv_blocks)
            normals = np.concatenate(normal_blocks)
            faces = np.concatenate(face_blocks)

            name = clean_name(out_name)
            obj.write(f"\no {name}\n")
            if groups:
                obj.write(f"g {name}\n")
            obj.write(f"usemtl {material_for(out_name)}\n")
            for x, y, z in vertices:
                obj.write(f"v {x:.7f} {y:.7f} {z:.7f}\n")
            for u, v in uv:
                obj.write(f"vt {u:.7f} {v:.7f}\n")
            for x, y, z in normals:
                obj.write(f"vn {x:.7f} {y:.7f} {z:.7f}\n")
            for face in faces:
                indices = [vertex_offset + int(index) for index in face]
                obj.write("f " + " ".join(f"{i}/{i}/{i}" for i in indices) + "\n")

            if record_objects is not None:
                record_objects.append(
                    {
                        "name": name,
                        "material": material_for(out_name),
                        "vertices": len(vertices),
                        "triangles": len(faces),
                    }
                )

            vertex_offset += len(vertices)
            totals["objects"] += 1
            totals["vertices"] += len(vertices)
            totals["triangles"] += len(faces)

    with mtl_path.open("w", encoding="utf-8", newline="\n") as mtl:
        mtl.write("# Source-backed material bindings; runtime recreates URP materials.\n")
        for material_name, texture_path in materials.items():
            mtl.write(f"\nnewmtl {material_name}\n")
            mtl.write("Ka 0.000000 0.000000 0.000000\n")
            mtl.write("Kd 1.000000 1.000000 1.000000\n")
            mtl.write("Ks 0.000000 0.000000 0.000000\n")
            mtl.write("Ns 1.000000\n")
            mtl.write(f"map_Kd {texture_path}\n")

    return totals


def local_of(scene, node_name: str) -> list[float]:
    matrix = np.asarray(scene.graph.get(node_name)[0], dtype=float)
    parents = {str(edge[1]): str(edge[0]) for edge in scene.graph.transforms.edge_data}
    parent = parents.get(node_name)
    if parent:
        matrix = np.linalg.inv(
            np.asarray(scene.graph.get(parent)[0], dtype=float)
        ) @ matrix
    return [round(float(value), 6) for value in matrix[:3, 3]]


def world_of(scene, node_name: str) -> list[float]:
    matrix = np.asarray(scene.graph.get(node_name)[0], dtype=float)
    return [round(float(value), 6) for value in matrix[:3, 3]]


def port_card(card: dict, file_name: str, rebuild: bool, textures: bool) -> dict:
    name = str(card["name"])
    tank_id = unity_id(name)
    data_id = tank_id.lower()
    card_id = data_id
    parts = client_parts(file_name)
    tank_root = RESOURCES / f"Models/Tanks/{tank_id}"

    body_material = f"{tank_id}Body"
    track_material = f"{tank_id}Tracks"

    source_model = MODEL.get(card_id, file_name)
    source_albedos = client_albedos(source_model)
    body_source, _ = client_textures(source_model)
    body_albedo = body_source.name.removesuffix(".mali.pvr")
    node_albedos: dict[str, str] = {}
    visible: list[tuple[Path, str, str]] = []
    visible_names: set[str] = set()

    def add_visible(source: Path, node: str, out_name: str):
        # The runtime shows and hides parts by their slot name, so a slot must
        # reach the model exactly once. Two card guns can name one client mesh
        # (IS-3 mounts the D-25T with either shutter as `gun_03`) and a slot can
        # be mounted by several turrets, which used to write the same mesh two
        # or four times: the copies z-fought, each dragged its own outline pass,
        # and the barrel read as torn paint with black patches on the turret.
        if out_name in visible_names:
            return
        visible_names.add(out_name)
        visible.append((source, node, out_name))

    def add_sheets(turret_slot: str, gun_slot: str, root: str, node: str, out_name: str):
        # Batches of a part on a sheet of their own (the Silencer barrel and
        # muzzle brake) become `<object>_sheetN`, drawn with that sheet and
        # moved, shown and hidden with the object they belong to.
        for extra_glb, index, sheet in SHEET_EXTRAS(
            card_id, file_name, parts, turret_slot, gun_slot, rebuild, root
        ):
            extra_name = f"{out_name}_sheet{index}"
            if not part_nodes(scene_of(extra_glb), node):
                raise RuntimeError(f"{name}: {extra_name} ({sheet}) exported no geometry")
            add_visible(extra_glb, node, extra_name)
            source_albedos[extra_name] = sheet
            if sheet != body_albedo:
                node_albedos[extra_name] = texture_id(sheet)
    armor_items: list[tuple] = []
    plates: list[dict] = []
    armor_names: set[str] = set()
    pivots: dict[str, dict] = {}
    animations: dict[str, dict] = {}
    rig_turrets: dict[str, dict] = {}
    hull_source: Path | None = None
    physics_item: tuple[Path, str, str] | None = None

    def add_armor(
        source: Path,
        node: str,
        renamed: str,
        mount: str,
        screen: bool,
        module: str = "",
    ):
        # Several client module records can reuse the same Blitz mesh slot (for
        # example, one gun mask shared by both M10 turrets). Unity's OBJ importer
        # also merges repeated material names into one submesh, so emitting the
        # duplicate would make every following armor-table index wrong. The
        # runtime addresses plates by their installed mesh slot; keep that slot's
        # first source-backed mesh exactly once.
        if renamed in armor_names:
            return
        armor_names.add(renamed)
        if PLATE_SCREEN is not None:
            # Exact 4.8.0 panels name their own screen flag.
            authored = PLATE_SCREEN(renamed)
            if authored is not None:
                screen = authored
        # Collision meshes are authored in the local space of the part they
        # belong to. Preserve that frame in the OBJ instead of baking tank-space
        # vertices and asking the runtime to subtract the pivot again.
        relative_to = None
        if mount.startswith("turret_"):
            relative_to = "turret01"
        elif mount.startswith("gun_"):
            relative_to = "gun01"
        armor_items.append((source, node, renamed, relative_to))
        plate: dict = {"name": renamed, "mount": mount, "screen": screen}
        if module:
            # `base_tank.module_collide` reads the body's "module" metadata, so
            # a plate that stands for a damageable module carries its id here.
            plate["module"] = module
        plates.append(plate)

    for turret_index, turret in enumerate(card.get("turrets", [])):
        turret_slot = str(turret["mesh"])
        gun_slots = [str(gun["mesh"]) for gun in turret.get("guns", [])]
        rig_turrets[turret_slot] = {"guns": gun_slots, "muzzles": {}}

        for gun_index, gun_slot in enumerate(gun_slots):
            glb = convert(card_id, file_name, parts, turret_slot, gun_slot, rebuild)
            scene = scene_of(glb)
            names = authored_nodes(scene)

            if hull_source is None:
                hull_source = glb
                for node in sorted(names):
                    # `tracks_crash` is the client's broken-track mesh, the
                    # one `base_tank.show_hide_tracks` swaps the running pair
                    # for on a dead tank or a destroyed track module. It is
                    # exported next to them and left hidden at spawn.
                    if node in ("hull_vis", "trackL", "trackR", "tracks_crash") \
                            or node.startswith(("wheelL", "wheelR")):
                        add_visible(glb, node, node)
                    elif node == "_hull_phys":
                        physics_item = (glb, node, "hull_phys")
                    elif node.startswith("armor_hull_"):
                        add_armor(glb, node, node, "hull", False)
                    elif node.startswith("_module_tracks_"):
                        # `blitz2glb` welds both track meshes into one body and
                        # puts the chassis armour into the suffix. The source
                        # parks it on the screen layer, so a shell spends the
                        # plate and carries on into the hull behind it.
                        add_armor(
                            glb, node, node[1:], "hull", True, module="tracks"
                        )
                add_sheets(turret_slot, gun_slot, "hull", "hull_vis", "hull_vis")

            if gun_index == 0:
                if "turret01" not in names:
                    raise RuntimeError(f"{name}: {turret_slot} exported no turret mesh")
                add_visible(glb, "turret01", turret_slot)
                if (
                    turret_slot in source_albedos
                    and source_albedos[turret_slot] != body_albedo
                ):
                    node_albedos[turret_slot] = texture_id(source_albedos[turret_slot])
                add_sheets(turret_slot, gun_slot, turret_slot, "turret01", turret_slot)
                pivots[turret_slot] = {
                    "parent": "",
                    "local": world_of(scene, "turret01"),
                    "world": world_of(scene, "turret01"),
                }
                for node in sorted(names):
                    if not node.startswith("armor_turret_"):
                        continue
                    renamed = node.replace("armor_turret_", f"armor_{turret_slot}_", 1)
                    add_armor(glb, node, renamed, turret_slot, False)

            if "gun01" not in names:
                raise RuntimeError(f"{name}: {turret_slot}/{gun_slot} exported no gun")
            add_visible(glb, "gun01", gun_slot)
            if (
                gun_slot in source_albedos
                and source_albedos[gun_slot] != body_albedo
            ):
                node_albedos[gun_slot] = texture_id(source_albedos[gun_slot])
            add_sheets(turret_slot, gun_slot, gun_slot, "gun01", gun_slot)
            pivots[gun_slot] = {
                "parent": turret_slot,
                "local": local_of(scene, "gun01"),
                "world": world_of(scene, "gun01"),
            }
            if "gun01_shoot_pos" in names:
                muzzle = f"{gun_slot}_shoot_pos"
                pivots[muzzle] = {
                    "parent": gun_slot,
                    "local": local_of(scene, "gun01_shoot_pos"),
                    "world": world_of(scene, "gun01_shoot_pos"),
                }
                rig_turrets[turret_slot]["muzzles"][gun_slot] = muzzle

            # Moving sub-assemblies of the gun (the Gatling cylinder of the
            # Annihilator) are exported as their own `<gun>_rotor` and
            # `<gun>_anim_<joint>` objects the battle rig moves around the gun.
            for extra_glb, extra_name in GUN_EXTRAS(
                card_id, file_name, parts, turret_slot, gun_slot, rebuild
            ):
                extra_scene = scene_of(extra_glb)
                if "gun01" not in authored_nodes(extra_scene):
                    raise RuntimeError(f"{name}: {extra_name} exported no geometry")
                add_visible(extra_glb, "gun01", extra_name)
                pivots[extra_name] = {
                    "parent": gun_slot,
                    "local": [0.0, 0.0, 0.0],
                    "world": world_of(extra_scene, "gun01"),
                }
            clips = GUN_ANIMATIONS(card_id, file_name, gun_slot, pivots)
            if clips:
                animations[gun_slot] = clips

            for node in sorted(names):
                if not node.startswith("_gun_mask_"):
                    continue
                thickness = node.rsplit("_", 1)[1]
                renamed = (node.replace("_gun_mask_", f"{gun_slot}_mask_{turret_slot}_", 1)
                           if "_plate" in node else f"{gun_slot}_mask_{thickness}")
                add_armor(glb, node, renamed, gun_slot, True)

    if hull_source is None or physics_item is None:
        raise RuntimeError(f"{name}: the client pack exported no hull")

    model_records: list[dict] = []
    model_stats = write_objects(
        visible,
        tank_root / f"{tank_id}.obj",
        material_for=lambda node: track_material
        if node in ("trackL", "trackR", "tracks_crash")
        else body_material,
        materials={
            body_material: f"Textures/{tank_id}.png",
            track_material: f"Textures/{tank_id}_tracks.png",
        },
        groups=True,
        record_objects=model_records,
    )

    # A part whose client geometry does not reach the OBJ leaves an invisible
    # vehicle that still blocks shots, so the export fails instead of shipping
    # one. A casemate turret slot (SU-152, ISU-152 ...) is a bare mount whose
    # shell belongs to the hull: it has no geometry in the client scene either,
    # and stays allowed.
    written = {str(record["name"]) for record in model_records}
    lost = []
    mount_only = []
    for source, node_name, out_name in visible:
        if clean_name(out_name) in written:
            continue
        if part_nodes(scene_of(source), node_name):
            lost.append(out_name)
        else:
            mount_only.append(out_name)
    if "hull_vis" not in written:
        lost.append("hull_vis")
    if lost:
        raise RuntimeError(
            f"{name}: the visible model lost " + ", ".join(sorted(set(lost)))
        )
    if mount_only:
        print(f"  {name}: mount-only slots without own geometry: "
              + ", ".join(sorted(set(mount_only))))

    physics_stats = write_objects(
        [physics_item],
        tank_root / f"{tank_id}_phys.obj",
        material_for=lambda _node: body_material,
        materials={body_material: f"Textures/{tank_id}.png"},
    )

    records: list[dict] = []
    armor_stats = write_objects(
        armor_items,
        tank_root / f"{tank_id}_armor.obj",
        material_for=lambda node: f"{tank_id}Armor_" + clean_name(node),
        materials={
            f"{tank_id}Armor_" + clean_name(str(plate["name"])): f"Textures/{tank_id}.png"
            for plate in plates
        },
        record_objects=records,
    )

    if [str(record["name"]) for record in records] != [
        clean_name(str(plate["name"])) for plate in plates
    ]:
        raise RuntimeError(f"{name}: armour plate order diverged from the OBJ objects")
    for index, (plate, record) in enumerate(zip(plates, records)):
        plate["name"] = record["name"]
        plate["armor"] = int(str(plate["name"]).rsplit("_", 1)[1])
        plate["submesh"] = index
        plate["material"] = record["material"]
        plate["vertices"] = record["vertices"]
        plate["triangles"] = record["triangles"]

    ground_sunken_collision(
        tank_root / f"{tank_id}.obj",
        tank_root / f"{tank_id}_phys.obj",
        tank_root / f"{tank_id}_armor.obj",
        # The track module deliberately reaches the ground: it must not be
        # lifted with the sunken hull plates.
        {
            str(plate["name"])
            for plate in plates
            if plate["mount"] == "hull" and not plate.get("module")
        },
        name,
    )

    mount_plates: dict[str, set[str]] = {}
    for plate in plates:
        mount = str(plate["mount"])
        if mount == "hull":
            continue
        mount_plates.setdefault(mount, set()).add(str(plate["name"]))
    ground_sunken_mounts(
        tank_root / f"{tank_id}.obj",
        tank_root / f"{tank_id}_armor.obj",
        mount_plates,
        pivots,
        name,
    )

    armor_table: dict = {"plates": plates}
    # `Tools/fit_armor_offsets.py` measures per-mount corrections against the
    # exported geometry and stores them next to the plates. A re-export of the
    # same card must not silently drop them.
    armor_path = RESOURCES / f"Data/{data_id}_armor.json"
    if armor_path.exists():
        offsets = json.loads(armor_path.read_text(encoding="utf-8")).get("mountOffsets")
        if offsets:
            armor_table["mountOffsets"] = offsets
    armor_path.write_text(
        json.dumps(armor_table, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    stock_turret = str(card["turrets"][0]["mesh"])
    stock_gun = str(card["turrets"][0]["guns"][0]["mesh"])
    rig = {
        "turret": stock_turret,
        "gun": stock_gun,
        "muzzle": rig_turrets[stock_turret]["muzzles"].get(stock_gun, stock_gun),
        "guns": rig_turrets[stock_turret]["guns"],
        "muzzles": rig_turrets[stock_turret]["muzzles"],
        "turrets": rig_turrets,
    }
    (RESOURCES / f"Data/{data_id}_nodes.json").write_text(
        json.dumps(
            {"albedos": node_albedos, "nodes": pivots, "rig": rig,
             **({"animations": animations} if animations else {})},
            ensure_ascii=False,
            indent=2,
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )

    model = TANKS / MODEL_DIR / (MODEL.get(card_id, file_name) + ".sc2")
    collision = TANKS / "CollisionMeshes" / (
        COLLISION.get(card_id, COLLISION_PREFIX + file_name) + ".sc2"
    )
    return {
        "id": tank_id,
        "source": str(model.relative_to(PACK)),
        "sha256": sha256(model),
        "collision": str(collision.relative_to(PACK)),
        "collision_sha256": sha256(collision),
        "model": model_stats,
        "physics": physics_stats,
        "armor": {**armor_stats, "plates": len(plates)},
        "nodes": len(pivots),
        "rig": rig,
        # The scene of a card is not always spelled like its data file, and
        # the sheets are bound by the scene.
        "textures": port_textures(
            tank_id, MODEL.get(card_id, file_name), source_albedos
        )
        if textures
        else None,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cards", nargs="*", default=None)
    parser.add_argument(
        "--rebuild",
        action="store_true",
        help="re-run the converter even when the cached GLB exists",
    )
    parser.add_argument(
        "--textures",
        action="store_true",
        help="also re-export the albedo and track sheets from the client pack",
    )
    args = parser.parse_args()

    cards = json.loads(
        (RESOURCES / "Data/tanks.json").read_text(encoding="utf-8")
    )["tanks"]
    by_name = {str(card["name"]): card for card in cards}
    wanted = set(args.cards) if args.cards else None

    ported = {}
    for card_name, file_name in BRANCH:
        if wanted is not None and card_name not in wanted:
            continue
        card = by_name.get(card_name)
        if card is None:
            raise SystemExit(f"unknown card: {card_name}")
        print(f"porting {card_name}")
        ported[card_name] = port_card(
            card, file_name, args.rebuild, args.textures
        )

    manifest_path = RESOURCES / "Data/tank_models_source_manifest.json"
    manifest = {
        "generator": "Tools/port_blitz_branch_models.py",
        "policy": "Original Blitz client geometry and PNG bytes only; no authored replacement art.",
        "tanks": {},
    }
    if manifest_path.exists():
        existing = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["tanks"] = existing.get("tanks", {})
    for card_name, entry in ported.items():
        previous = manifest["tanks"].get(card_name, {})
        entry["textures"] = entry["textures"] or previous.get("textures", [])
        manifest["tanks"][card_name] = entry
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(
        json.dumps(
            {name: entry["model"] for name, entry in ported.items()},
            ensure_ascii=False,
            indent=2,
            sort_keys=True,
        )
    )


if __name__ == "__main__":
    main()
