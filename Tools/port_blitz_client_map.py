#!/usr/bin/env python3
"""Port a Blitz 4.8.0 or 11.20 client map into Unity resources.

A client map is a DAVA `.sc2` scene rather than a Godot scene. This porter
keeps the client geometry and textures intact, mirrors DAVA coordinates through
the same conversion used by the tank exporter, and emits the compact terrain
payload consumed by BattleTerrain plus an imported OBJ for static map geometry.

Everything the client spells per map - the heightmap, the landscape textures,
the two sun nodes, the way spawns and bot goals are authored - lives in MAPS,
so porting the next map is a table entry rather than a second copy of this
file. A map of the 11.20 client (MapConfig.client_version) is read through
client_map_1120.py, which unpacks its `.dvpl` pack and feeds the same helpers.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent.parent
PACK = Path(os.environ.get("BLITZ_PACK", Path.home() / "projects" / "wotblitz4.8.0files"))
PACK_1120 = Path(os.environ.get("BLITZ_PACK_1120", Path.home() / "projects" / "wotblitz11.20.0files"))
GODOT = Path(os.environ.get("GODOT_PROJECT", Path.home() / "projects" / "blitz-legacy-godot"))


@dataclass(frozen=True)
class MapConfig:
    """One client map, named the way the client names it.

    `spawn_layout` is `opposing-corners` for a scene that authors alternate
    corners per team and `teams` for the ordinary two-corner map.
    `ai_source` is `paths` for a scene that ships PathComponents and `markers`
    for one that ships `botspawn` and `strategicpoint` entities instead.
    """

    id: str
    directory: str
    client: str
    heightmap: str
    colormap: str
    tilemask: str
    tiletextures: str
    bake_light: str
    runtime_light: str
    spawn_layout: str
    ai_source: str
    random_tiers: list[int] = field(default_factory=list)
    # Authored respawn sets this port refuses to use, as (team, group). The
    # client picks one set per battle; a set that is walled in is a death trap
    # in a random battle, where nothing guides the enemy away from it.
    dropped_spawn_groups: tuple[tuple[int, int], ...] = ()
    # Replacement ally slots as (x, y) in source metres, seated on the
    # landscape and facing the way the surviving authored set faces. They keep
    # a team at seven tanks once a respawn set is dropped.
    extra_ally_spawns: tuple[tuple[float, float], ...] = ()
    # Props the port leaves out, as (name fragment, x0, x1, y0, y1) in source
    # metres: the no-entry fences that seal a dropped spawn off from the map
    # and the tutorial decals that belong to it.
    dropped_props: tuple[tuple[str, float, float, float, float], ...] = ()
    # Leave out every entity whose CustomProperties carry `tags: tutorial`.
    # Ordeal doubles as the 4.8.0 tutorial: its brick fences, glowing
    # no-entry cards, Invisible_wall planes, arrows and path decals belong to
    # the scripted lesson and are not part of a random battle.
    drop_tutorial_props: bool = False
    # A trim on the shared exposure, for a map whose albedo reads brighter in
    # Unity than it does in the client. 1.0 leaves the common lighting exactly
    # as it is, which is what every map ported so far runs on.
    exposure: float = 1.0
    # `obj` exports the scene as the imported OBJ that BattleObjects shades
    # with the port's own map shader; `client` writes the binary scene of
    # client_map_scene.py, which keeps every DAVA material and is drawn with
    # the client's own formulas.
    scene_format: str = "obj"
    # `mirrored` is how Ordeal and Karieri were first ported: the manifest
    # stores DAVA y as Godot z, which lands the map flipped north-south in
    # Unity. `dava` stores -y, so Unity sees the client's own layout.
    orientation: str = "mirrored"
    # The light the sun direction is read from; empty means `bake_light`.
    sun_light: str = ""
    # Draw the landscape with the client's tile mask formula and write its
    # tile colours, tiling and the global shadow colour into the manifest.
    client_shading: bool = False
    # The axis of the sun node the light travels along. DAVA's
    # Light::SetPositionDirectionFromMatrix uses the node's -Y; the maps
    # ported first were tuned on -Z, which suits their authored nodes.
    sun_axis: str = "-z"
    # CustomPropertiesComponent collision types besides vegetation (8) whose
    # entities topple under a tank instead of stopping it, e.g. Karieri's
    # cacti (CollisionType 5, FallType 2). Only the binary client scene
    # reads it; empty keeps a map's tree list exactly as it was.
    toppling_collision_types: tuple[int, ...] = ()
    # Hand-placed bot goals on top of the authored markers, as
    # (name, x, y, priority, radius, type) in DAVA ground metres; the height
    # is sampled from the landscape. They keep the port's own route set
    # reproducible instead of living as hand edits in the manifest.
    route_markers: tuple[tuple[str, float, float, int, float, int], ...] = ()
    # The client release the map ships in: "4.8", the SmartDLC this porter
    # began with, or "11.20", whose `.dvpl` pack client_map_1120.py reads.
    client_version: str = "4.8"
    # The loading music the Godot project already cut for this map, relative
    # to its root; empty keeps the shared Rudniki intro.
    intro_music: str = ""
    # Two authored spawn groups whose centres are this close stand on the
    # same corner and are merged; further apart they are alternatives.
    spawn_zone_radius: float = 100.0
    # The id the client itself files this map under in maps.yaml, the string
    # table and the minimap/loading screen folders, when it differs from the
    # port's id: World of Ducks ships as `test` in 4.8.0. Empty means `id`.
    client_id: str = ""
    # Keep the map out of the random battle rotation: it exists only for a
    # mode that loads it by id.
    mode_only: bool = False

    @property
    def client_key(self) -> str:
        return self.client_id or self.id


MAPS = {
    "ordeal": MapConfig(
        id="ordeal",
        directory="Ordeal",
        client="15_ordeal_ord",
        heightmap="landscape/OrdialHM3.heightmap",
        colormap="landscape/ordeal_CM.mali.pvr",
        tilemask="landscape/ordeal_tilemask.mali.pvr",
        tiletextures="landscape/TileTextures.mali.pvr",
        bake_light="Light beast direct",
        runtime_light="Light dynamic",
        spawn_layout="opposing-corners",
        ai_source="paths",
        # The client marks this map as tutorial only; the port deliberately
        # opens it to low tier random battles instead.
        random_tiers=[1, 2, 3, 4],
        # Ally set 1 is the walled pocket the tutorial starts in and set 3 is
        # the yard inside the fort; both are single-exit traps in a random
        # battle. Only set 2, on the open north slope, is kept.
        dropped_spawn_groups=((0, 1), (0, 3)),
        # Three more slots on that slope, so a team still fields seven tanks.
        extra_ally_spawns=((-118.0, 80.0), (-118.0, 68.0), (-112.0, 60.0)),
        # The no-entry fences that sealed the pocket off, and the tutorial's
        # green circles, which point at spawns this port no longer uses.
        dropped_props=(
            ("restriction", -100.0, -45.0, 138.0, 150.0),
            ("restriction", -34.0, -24.0, 118.0, 148.0),
            ("select01", -300.0, 300.0, -300.0, 300.0),
        ),
        # Re-ported "1 in 1" like Karieri: the binary scene keeps the baked
        # lightmaps and the SpeedTree billboards the OBJ path flattened.
        drop_tutorial_props=True,
        scene_format="client",
        orientation="dava",
        client_shading=True,
        # `Light dynamic` is a positional light rotated only around Z; it
        # sits at (1043, -1414, 1121), so it shines along
        # (-0.505, 0.685, -0.543) - the -Y axis of `Light beast direct`,
        # 32 degrees above the horizon. The bake light carries that
        # rotation, so the sun is read off it.
        sun_axis="-y",
        # The 8 telegraph poles and 8 loudspeaker masts author
        # CollisionType 5 with FallType 2 and no Health, like Karieri's
        # cacti: the client knocks them over.
        toppling_collision_types=(5,),
    ),
    "karieri": MapConfig(
        id="karieri",
        directory="Karieri",
        client="23_karieri_kr",
        heightmap="landscape/height_map_edited.heightmap",
        colormap="landscape/landscape_kariery2.mali.pvr",
        tilemask="landscape/tilemask_tga.mali.pvr",
        tiletextures="landscape/karieri_tiletex2.mali.pvr",
        bake_light="Direct_beast_light",
        runtime_light="Dynamic_light",
        spawn_layout="teams",
        ai_source="markers",
        # `from_apk/Data/maps.yaml` authors `levels: [2, 3, 4, 5, 6, 7]`,
        # but the port unlocks the quarry for every tier: besides Rudniki it
        # is the only map in the roster, so a tier 8-10 random battle had no
        # choice at all and always landed on Rudniki.
        random_tiers=[],
        # Ported "1 in 1" like Mirage: the binary scene keeps the lightmaps
        # 352 of the 450 LOD-0 batches are drawn with (the OBJ path dropped
        # them, which left every house flat grey) and the SpeedTree bushes
        # are billboarded by the client's own formula. The exposure trim of
        # the OBJ port is gone with the port's own map shader.
        scene_format="client",
        orientation="dava",
        sun_light="Dynamic_light",
        client_shading=True,
        # Like every 4.8 light, Dynamic_light shines along its node's -Y:
        # 29 degrees above the horizon towards DAVA (+x, -y), which is where
        # the lightmaps and the colour map throw the houses' shadows.
        sun_axis="-y",
        # 108 cacti author CollisionType 5 with FallType 2 and no Health:
        # the client knocks them over like trees.
        toppling_collision_types=(5,),
        # The client's own goals all sit in a narrow band around the middle
        # of the quarry; these add the centre and the two hills with their
        # approaches, so the bots take ground instead of milling at spawn.
        route_markers=(
            ("route_center_1", 16.0, 4.0, 3, 28.0, 0),
            ("route_center_2", 50.0, 0.0, 2, 20.0, 0),
            ("route_center_3", -50.0, 0.0, 2, 20.0, 0),
            ("route_hill_south_1", 0.0, -100.0, 3, 26.0, 1),
            ("route_hill_south_2", 50.0, -100.0, 1, 20.0, 3),
            ("route_hill_south_3", -50.0, -100.0, 1, 20.0, 3),
            ("route_hill_north_1", 0.0, 149.5, 3, 26.0, 1),
            ("route_hill_north_2", 50.0, 150.0, 1, 20.0, 3),
            ("route_hill_north_3", -50.0, 150.0, 1, 20.0, 3),
            ("route_approach_east_1", 150.0, 100.0, 1, 22.0, 2),
            ("route_approach_east_2", 150.0, -50.0, 1, 22.0, 2),
            ("route_approach_west_1", -150.0, 100.0, 1, 22.0, 2),
            ("route_approach_west_2", -150.0, -50.0, 1, 22.0, 2),
        ),
    ),
    "lake": MapConfig(
        id="lake",
        directory="Lake",
        client="20_lake_lk",
        heightmap="landscape/lake_Heightmap.heightmap",
        colormap="landscape/lake_colormap.mali.pvr",
        tilemask="landscape/lake_tilemask.mali.pvr",
        tiletextures="landscape/lake_tiletextures.mali.pvr",
        bake_light="Light Beast Direct",
        runtime_light="Light Dynamic",
        # Mirage authors no spawn entities; its respawn sets are the
        # `assaultRespawnPoints` of maps.yaml.
        spawn_layout="yaml",
        ai_source="markers",
        random_tiers=list(range(1, 11)),
        scene_format="client",
        orientation="dava",
        # Unlike Ordeal's, this scene's runtime light carries the full
        # rotation the client shades and casts shadow volumes with.
        sun_light="Light Dynamic",
        client_shading=True,
        # Light Dynamic's -Z points 31 degrees up; its -Y points 37 degrees
        # down towards the north-east, which is where every palm trunk throws
        # its shadow in the baked colour map.
        sun_axis="-y",
    ),
    "sea_of_ducks": MapConfig(
        id="sea_of_ducks",
        directory="SeaOfDucks",
        client="sea_of_ducks",
        # 4.8.0 files the map as `test` (maps.yaml id 14, the string
        # `#maps:test:...` and the minimap/loading screen folders).
        client_id="test",
        heightmap="landscape/bottom600_hm.heightmap",
        colormap="landscape/bottom600_color.mali.pvr",
        tilemask="landscape/tilemask600.mali.pvr",
        # The landscape tiles one detail texture, `tileTexture0`.
        tiletextures="landscape/bottom_tile.mali.pvr",
        bake_light="sun",
        runtime_light="dynamic_light",
        # Fourteen `Spawn1NN` and fourteen `Spawn2NN` markers on the north-east
        # and south-west beaches, one corner per team.
        spawn_layout="teams",
        ai_source="markers",
        # The World of Ducks mode loads the map by id; it never joins the
        # random battle rotation.
        random_tiers=[],
        mode_only=True,
        scene_format="client",
        orientation="dava",
        sun_light="dynamic_light",
        client_shading=True,
        sun_axis="-y",
    ),
    "asia": MapConfig(
        id="asia",
        directory="Asia",
        client="10_asia_as",
        heightmap="landscape/asia_heightmap.heightmap",
        colormap="landscape/asia_colormap.mali.pvr",
        tilemask="landscape/asia_tilemask.mali.pvr",
        tiletextures="landscape/asia_tiletex.mali.pvr",
        bake_light="Light beast direct",
        # Sic: the client names it with two spaces.
        runtime_light="Light  dinamic",
        # Every team authors three spawn sets in its own corner (north-east
        # against south-west) and the client seats one battle across all of
        # them by SpawnPreferredVehicleType, so the sets are not
        # alternatives: every marker of a team belongs to the battle.
        spawn_layout="teams",
        ai_source="markers",
        random_tiers=list(range(1, 11)),
        scene_format="client",
        orientation="dava",
        # Like Mirage's, the runtime light carries the full rotation the
        # client shades with; its -Y points 38 degrees down towards DAVA +y,
        # the azimuth of the bake light as well.
        sun_light="Light  dinamic",
        client_shading=True,
        sun_axis="-y",
    ),
    "moon": MapConfig(
        id="moon",
        directory="Moon",
        client="40_moon_mn",
        heightmap="landscape/moon_hm.heightmap.heightmap",
        colormap="landscape/moon_colormap.astc.pvr",
        tilemask="landscape/tilemaskMoon_2k.astc.pvr",
        tiletextures="landscape/moonTiles.astc.pvr",
        # An 11.20 scene has no light entities: its sun is the scene's
        # DirectionalLightComponent.
        bake_light="",
        runtime_light="",
        # Each team authors two alternative sets of seven markers
        # (pointNumber 1 and 2) about 65 metres apart, and a battle starts
        # from one of them; the widest separated pair is kept.
        spawn_layout="opposing-corners",
        spawn_zone_radius=40.0,
        ai_source="markers",
        # maps.yaml limits the map to no tiers.
        random_tiers=list(range(1, 11)),
        scene_format="client",
        orientation="dava",
        client_shading=True,
        sun_axis="-y",
        client_version="11.20",
        intro_music="assets/sfx/music/Luna.mp3",
    ),
}

# The map being ported. main() selects it before anything else runs.
MAP = MAPS["ordeal"]

sys.path.insert(0, str(GODOT / "tools"))
import dava  # noqa: E402
# This Unity exporter reads the shipped SC2 scene itself. Keep only the raw
# DAVA archive reader from the old tooling; axis conversion and transforms live
# here so no Godot/tank exporter owns the Unity output path.
DAVA_TO_UNITY = (
    1.0, 0.0, 0.0, 0.0,
    0.0, 0.0, 1.0, 0.0,
    0.0, -1.0, 0.0, 0.0,
    0.0, 0.0, 0.0, 1.0,
)


def mat_mul(a: tuple, b: tuple) -> tuple:
    return tuple(
        sum(a[row * 4 + k] * b[k * 4 + col] for k in range(4))
        for row in range(4) for col in range(4)
    )


def xform_point(matrix: tuple, point) -> tuple:
    x, y, z = point[:3]
    return (
        matrix[0] * x + matrix[1] * y + matrix[2] * z + matrix[3],
        matrix[4] * x + matrix[5] * y + matrix[6] * z + matrix[7],
        matrix[8] * x + matrix[9] * y + matrix[10] * z + matrix[11],
    )


def xform_dir(matrix: tuple, direction) -> tuple:
    x, y, z = direction[:3]
    return (
        matrix[0] * x + matrix[1] * y + matrix[2] * z,
        matrix[4] * x + matrix[5] * y + matrix[6] * z,
        matrix[8] * x + matrix[9] * y + matrix[10] * z,
    )

TERRAIN_MAGIC = b"BLTZTER2"


def items(value):
    if value is None:
        return []
    if isinstance(value, list):
        return value
    if isinstance(value, dict):
        return [value[key] for key in sorted(value)]
    return [value]


def components(entity, typename):
    for component in items(entity.get("components")):
        if isinstance(component, dict) and component.get("comp.typename") == typename:
            yield component


IDENTITY = (1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0)


def transposed(matrix):
    """DAVA stores row-vector matrices; blitz2glb transforms column vectors.

    A DAVA `tc.worldMatrix` keeps its translation in the last row, because the
    engine multiplies `p * M`. `xform_point` reads the translation from the
    last column, so feeding the serialised order straight through silently
    drops every placement and piles the whole map onto the origin.
    """
    return tuple(matrix[column * 4 + row] for row in range(4) for column in range(4))


def trs_matrix(translation, rotation, scale) -> tuple:
    """T * R * S as a column-vector matrix, from a quaternion (x, y, z, w)."""
    x, y, z, w = (float(value) for value in rotation)
    sx, sy, sz = (float(value) for value in scale)
    r = (
        (1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
        (2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
        (2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)),
    )
    tx, ty, tz = (float(value) for value in translation)
    return (
        r[0][0] * sx, r[0][1] * sy, r[0][2] * sz, tx,
        r[1][0] * sx, r[1][1] * sy, r[1][2] * sz, ty,
        r[2][0] * sx, r[2][1] * sy, r[2][2] * sz, tz,
        0.0, 0.0, 0.0, 1.0,
    )


def matrix_of(entity):
    for component in components(entity, "TransformComponent"):
        matrix = component.get("tc.worldMatrix") or component.get("tc.localMatrix")
        if matrix and len(matrix) == 16:
            return transposed(tuple(float(value) for value in matrix))
        # An 11.20 scene stores the world transform as its parts.
        if component.get("tc.worldTranslation") is not None:
            return trs_matrix(component["tc.worldTranslation"],
                              component.get("tc.worldRotation") or (0.0, 0.0, 0.0, 1.0),
                              component.get("tc.worldScale") or (1.0, 1.0, 1.0))
    return IDENTITY


def batches_of(entity, switch_state: int = 0):
    """Yield LOD-0 batches for exactly one DAVA SwitchComponent state.

    A destructible SC2 keeps intact and wreck batches side-by-side; exporting
    both as ordinary geometry is what made collapsed props appear shifted or
    as dark slabs before the game had hit them.
    """
    for component in components(entity, "RenderComponent"):
        obj = component.get("rc.renderObj") or {}
        kind = str(obj.get("##name") or "")
        for index, batch in enumerate(items(obj.get("ro.batches"))):
            if not isinstance(batch, dict):
                continue
            try:
                lod = int(obj.get(f"rb{index}.lodIndex", -1))
                state = int(obj.get(f"rb{index}.switchIndex", -1))
            except (TypeError, ValueError):
                continue
            if lod not in (-1, 0) or state not in (-1, switch_state):
                continue
            source = batch.get("rb.datasource")
            material = batch.get("rb.nmatname")
            if source is not None and material is not None:
                yield int(source), int(material), kind


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def resolve_source(map_dir: Path, reference: str) -> Path | None:
    if not reference:
        return None
    path = (map_dir / reference).resolve()
    if path.suffix == ".tex":
        # 4.8 ships Mali (ETC) textures, 11.20 ASTC ones.
        for suffix in (".mali.pvr", ".astc.pvr"):
            pvr = path.with_suffix(suffix)
            if pvr.is_file():
                return pvr
    if path.is_file():
        return path
    return None


def inherited_material(materials: dict[int, dict], material_id: int) -> dict:
    seen = set()
    current = material_id
    merged = {}
    while current is not None and current not in seen:
        seen.add(current)
        node = materials.get(int(current))
        if node is None:
            break
        for key, value in (node.get("textures") or {}).items():
            merged.setdefault(key, value)
        merged.setdefault("materialName", node.get("materialName", "material"))
        # Only ninety-eight of the scene's 2920 materials carry `fxName`, the
        # effect the client renders with, e.g.
        # `~res:/Materials/Textured.Opaque.NoCull.material`. Every other
        # material inherits it from an ancestor, so the nearest one wins.
        if node.get("fxName"):
            merged.setdefault("fxName", str(node.get("fxName")))
        current = node.get("parentMaterialKey")
    return merged


# MTL cannot express back-face culling or alpha testing, so the effect the
# client authored is carried in the material name and read back by
# BattleObjects when it builds the scene.
TWO_SIDED_SUFFIX = "_nocull"
CUTOUT_SUFFIX = "_cutout"
BLEND_SUFFIX = "_blend"

# `ShadowVolume.material` is not artwork. DAVA extrudes a silhouette of the
# building behind it and renders it into the stencil buffer, so exporting it as
# ordinary geometry puts an untextured, half-open copy of the house next to the
# house - which is exactly what these looked like in the port.
INVISIBLE_EFFECTS = ("ShadowVolume",)


def is_invisible(material: dict) -> bool:
    fx = str(material.get("fxName") or "")
    return any(effect in fx for effect in INVISIBLE_EFFECTS)


# A SpeedTreeObject does not ship renderable geometry. Its leaf cards are
# stored as 2D offsets around a pivot - `position.xy` is the corner inside the
# card and `position.z` equals the pivot, so the raw vertices form a flat
# horizontal quad - and the client's tree shader spans them across the camera
# axes every frame. Exported verbatim they land on the ground as the dark
# rectangles the port showed: a 7x7 m leaf sheet lying face up, lit edge on.
SPEEDTREE_KIND = "SpeedTreeObject"

# The shared client-map exposure: one sun intensity and one ambient luminance
# for every ported client map, mirrored from BattleWorld.ClientAmbientLuminance.
# A map only departs from them through MapConfig.exposure.
CLIENT_SUN_ENERGY = 1.25
CLIENT_AMBIENT_LUMINANCE = 0.371622

# The card's own up axis. A static port cannot billboard, so the cards stand
# up instead: the offset's second axis goes to world up (DAVA is Z up) and the
# first one to the horizontal direction the card authored in `angleSinCos`,
# which keeps the sheets of one bush crossing each other rather than parallel.
SPEEDTREE_UP = (0.0, 0.0, 1.0)


def speedtree_card(streams) -> list[tuple[float, float, float]] | None:
    positions = streams.get("position") or []
    pivots = streams.get("pivot") or []
    angles = streams.get("angleSinCos") or []
    if not positions or len(pivots) != len(positions):
        return None

    rebuilt = []
    for index, point in enumerate(positions):
        pivot = pivots[index]
        across = point[0] - pivot[0]
        along = point[1] - pivot[1]
        sin, cos = angles[index][:2] if index < len(angles) else (0.0, 1.0)
        length = math.hypot(sin, cos) or 1.0
        sin, cos = sin / length, cos / length
        rebuilt.append((
            pivot[0] + (across * cos),
            pivot[1] + (across * sin),
            pivot[2] + along,
        ))
    return rebuilt


def material_suffix(material: dict) -> str:
    fx = str(material.get("fxName") or "")
    suffix = ""
    # A leaf card is a sheet: culled by face it disappears from one side, and
    # the client never culls it either - the tree shader draws it both ways.
    if "NoCull" in fx or "SpeedTreeLeaf" in fx:
        suffix += TWO_SIDED_SUFFIX
    if "Alphatest" in fx:
        suffix += CUTOUT_SUFFIX
    if "Alphablend" in fx:
        suffix += BLEND_SUFFIX
    return suffix


def material_name(material_id: int, material: dict, texture_name: str | None) -> str:
    stem = Path(texture_name or "flat").stem
    return "m_%d_%s%s" % (
        material_id,
        re.sub(r"[^A-Za-z0-9_]+", "_", stem),
        material_suffix(material),
    )


def decode_rgb565(source: Path, destination: Path) -> bool:
    """Decode the raw RGB565 PVRv3 maps missing from the legacy helper."""
    blob = source.read_bytes()
    if len(blob) < 52 or blob[:4] != b"PVR\x03":
        return False
    pixel_format = struct.unpack_from("<Q", blob, 8)[0]
    # PVR's channel names are little-endian ('rgb\0'), followed by 5/6/5 bits.
    if pixel_format != 0x0005060500626772:
        return False
    metadata_size = struct.unpack_from("<I", blob, 48)[0]
    data_offset = 52 + metadata_size
    height, width = struct.unpack_from("<II", blob, 24)
    size = width * height * 2
    payload = blob[data_offset:data_offset + size]
    if width <= 0 or height <= 0 or len(payload) != size:
        return False

    packed = np.frombuffer(payload, dtype="<u2").reshape((height, width))
    rgba = np.empty((height, width, 4), dtype=np.uint8)
    rgba[:, :, 0] = (((packed >> 11) & 0x1F) * 255 + 15) // 31
    rgba[:, :, 1] = (((packed >> 5) & 0x3F) * 255 + 31) // 63
    rgba[:, :, 2] = ((packed & 0x1F) * 255 + 15) // 31
    rgba[:, :, 3] = 255
    from PIL import Image
    destination.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(rgba, "RGBA").save(destination)
    return True


def decode_texture(source: Path, destination: Path) -> bool:
    destination.parent.mkdir(parents=True, exist_ok=True)
    try:
        if decode_rgb565(source, destination):
            return True
        import pvr2png
        result = pvr2png.main([str(source), "--out", str(destination)])
        # pvr2png follows the CLI convention: zero means success. Keep the
        # generated file as the source of truth because some decoders emit
        # diagnostics while still completing a valid PNG.
        return result == 0 and destination.is_file()
    except Exception as error:  # pragma: no cover - depends on the external decoder
        print(f"warning: texture decode failed for {source}: {error}", file=sys.stderr)
    return False


# DAVA normalises a heightmap sample against this fixed maximum, then maps it
# into the landscape bounding box, so the box is the only scale that exists.
HEIGHTMAP_MAX = 65535.0

# The samples are not stored row by row: the file holds 16x16 blocks, each one
# row-major inside itself, laid out row-major across the grid. Reading it as a
# plain raster produces a striped field whose vertical variation is 27 times
# the horizontal one, which is how this layout was identified.
HEIGHTMAP_BLOCK = 16


def untile(samples: np.ndarray, side: int, block: int = HEIGHTMAP_BLOCK) -> np.ndarray:
    if side % block:
        raise SystemExit(f"heightmap side {side} is not a multiple of {block}")
    per_row = side // block
    blocks = samples.reshape((per_row, per_row, block, block))
    return blocks.transpose(0, 2, 1, 3).reshape((side, side))


def landscape_bbox(scene) -> tuple:
    """The DAVA Landscape bbox: (minX, minY, minZ, maxX, maxY, maxZ)."""
    found = []

    def walk(entity):
        for component in components(entity, "RenderComponent"):
            obj = component.get("rc.renderObj") or {}
            if obj.get("##name") != "Landscape":
                continue
            box = obj.get("bbox")
            if isinstance(box, (bytes, bytearray)) and len(box) == 24:
                found.append(struct.unpack("<6f", box))
        for child in items(entity.get("#hierarchy")):
            if isinstance(child, dict):
                walk(child)

    for root in items(scene["scene"].get("#hierarchy")):
        if isinstance(root, dict):
            walk(root)

    if len(found) != 1:
        raise SystemExit(f"expected exactly one landscape bbox, found {len(found)}")
    return found[0]


def material_property(node: dict, name: str) -> list[float]:
    """Decode one DAVA NMaterial property blob into its floats.

    The blob is a type byte, a little-endian count and then that many values;
    types 0..3 are float, vec2, vec3 and vec4.
    """
    blob = (node.get("properties") or {}).get(name)
    if not isinstance(blob, (bytes, bytearray)) or len(blob) < 9:
        return []
    size = (blob[0] + 1) if blob[0] <= 3 else 0
    count = int.from_bytes(blob[1:5], "little")
    total = size * count
    if not total or len(blob) < 5 + 4 * total:
        return []
    return list(struct.unpack("<%df" % total, blob[5:5 + 4 * total]))


def scene_lights(scene) -> dict:
    """The authored light nodes of the scene, keyed by entity name."""
    found = {}

    def walk(entity):
        light = None
        matrix = IDENTITY
        for component in components(entity, "LightComponent"):
            light = component.get("lc.light") or {}
        if light is not None:
            matrix = matrix_of(entity)
            found[str(entity.get("name", ""))] = (light, matrix)
        for child in items(entity.get("#hierarchy")):
            if isinstance(child, dict):
                walk(child)

    for root in items(scene["scene"].get("#hierarchy")):
        if isinstance(root, dict):
            walk(root)
    return found


# The client keeps two suns: the runtime one is what the engine shades with,
# while the bake one is what the offline baker used and the only node that
# carries a real three-axis rotation. The runtime node is only spun around Z,
# so its -Z axis is straight down and would light the map flat. Both are named
# per map, so the names live in MAPS.


def sun_basis(direction: tuple) -> list[float]:
    """A source-space basis whose GodotSpace forward is `direction`.

    `GodotSpace.Basis` reads the Z column as -forward and mirrors Z, so the
    manifest stores the light the same way every other transform is stored.
    """
    dx, dy, dz = direction
    rx, ry, rz = (dz, 0.0, -dx)
    length = math.hypot(rx, rz) or 1.0
    rx, rz = rx / length, rz / length
    return [rx, 0.0, -dx, ry, 1.0, -dy, -rz, 0.0, dz]


def scene_environment(scene) -> dict:
    """Fog and lighting exactly as the client scene authored them."""
    global_material = None
    for node in scene.get("dataNodes", []):
        if node.get("materialName") == "Scene_Global_Material":
            global_material = node
            break
    if global_material is None:
        raise SystemExit("the client scene has no Scene_Global_Material")

    fog_colour = material_property(global_material, "fogColor")[:3]
    fog_start = material_property(global_material, "fogStart")
    fog_end = material_property(global_material, "fogEnd")
    fog_limit = material_property(global_material, "fogLimit")
    if len(fog_colour) != 3 or not fog_start or not fog_end:
        raise SystemExit("the client scene has no authored fog")

    lights = scene_lights(scene)
    if MAP.bake_light not in lights or MAP.runtime_light not in lights:
        raise SystemExit("the client scene is missing its authored lights")

    sun_name = MAP.sun_light or MAP.bake_light
    if sun_name not in lights:
        raise SystemExit(f"the client scene has no light named {sun_name}")
    _bake, bake_matrix = lights[sun_name]
    runtime, _runtime_matrix = lights[MAP.runtime_light]

    # A DAVA light shines along the -Z axis of its node, which is the third
    # column of the transposed matrix. DAVA_TO_GLTF then turns (x, y, z) into
    # (x, z, -y), the space the exported scene and the runtime both live in.
    column = 1 if MAP.sun_axis == "-y" else 2
    dava = (-bake_matrix[column], -bake_matrix[4 + column], -bake_matrix[8 + column])
    length = math.sqrt(sum(v * v for v in dava)) or 1.0
    dava = tuple(v / length for v in dava)
    # The maps ported first live mirrored north-south in Unity, and their sun
    # with them; a `dava` map is the client's own handedness, (x, z, y).
    if MAP.orientation == "dava":
        direction = (dava[0], dava[2], dava[1])
    else:
        direction = (dava[0], dava[2], -dava[1])

    # `FOG_ATMOSPHERE` is on in this scene, and with it DAVA stops using the
    # flat `fogColor` far from the camera: the haze becomes
    # `fogAtmosphereColorSky` away from the sun and `fogAtmosphereColorSun`
    # towards it, faded in over `fogAtmosphereDistance`. That is what makes
    # the client's horizon blue-grey instead of the near-white `fogColor`.
    atmosphere = bool((global_material.get("flags") or {}).get("FOG_ATMOSPHERE"))
    sky_colour = material_property(global_material, "fogAtmosphereColorSky")[:3]
    sun_colour = material_property(global_material, "fogAtmosphereColorSun")[:3]
    atmosphere_distance = material_property(global_material, "fogAtmosphereDistance")
    scattering = material_property(global_material, "fogAtmosphereScattering")
    if atmosphere and (len(sky_colour) != 3 or len(sun_colour) != 3):
        raise SystemExit("the client scene enables atmosphere fog without its colours")

    return {
        "fog": {
            "color": [round(v, 6) for v in fog_colour],
            "start": fog_start[0],
            "end": fog_end[0],
            "limit": fog_limit[0] if fog_limit else 1.0,
            "atmosphere": atmosphere,
            # Atmosphere-free scenes (sea_of_ducks) carry no haze colours: leave
            # the keys out so MapDefinition falls back to white instead of
            # rejecting an empty vector.
            **({"skyColor": [round(v, 6) for v in sky_colour]} if len(sky_colour) == 3 else {}),
            **({"sunColor": [round(v, 6) for v in sun_colour]} if len(sun_colour) == 3 else {}),
            "atmosphereDistance": atmosphere_distance[0] if atmosphere_distance else 0.0,
            "scattering": scattering[0] if scattering else 1.0,
            **exponential_fog(global_material),
            **(halfspace_fog(global_material) if MAP.client_shading else {}),
        },
        **(
            {"shadowColor": [round(v, 6) for v in
                             material_property(global_material, "shadowColor")[:4]]}
            if MAP.client_shading
            else {}
        ),
        "ambient": [
            runtime.get("ambColor.r", 1.0),
            runtime.get("ambColor.g", 1.0),
            runtime.get("ambColor.b", 1.0),
        ],
        # The client authors an ambient hue, not a level, so the runtime
        # normalises it to one shared luminance. A trimmed map carries the
        # level it wants here; absent, the runtime keeps its own default.
        **(
            {"ambientLuminance": round(CLIENT_AMBIENT_LUMINANCE * MAP.exposure, 6)}
            if MAP.exposure != 1.0
            else {}
        ),
        "sunColor": [
            runtime.get("color.r", 1.0),
            runtime.get("color.g", 1.0),
            runtime.get("color.b", 1.0),
        ],
        "sunBasis": sun_basis(direction),
        "sunDirection": [round(v, 6) for v in direction],
    }


def exponential_fog(global_material: dict) -> dict:
    """The density of a scene that switches `FOG_LINEAR` off.

    vp-fog-math.slh then fades by `1 - exp(-fogDensity * distance)` instead
    of between fogStart and fogEnd. Linear scenes write nothing, so the
    manifests of the maps ported before stay as they are.
    """
    flags = global_material.get("flags") or {}
    if "FOG_LINEAR" not in flags or int(flags["FOG_LINEAR"]):
        return {}
    density = material_property(global_material, "fogDensity")
    if not density:
        raise SystemExit("the client scene uses exponential fog without fogDensity")
    return {"linear": False, "density": round(density[0], 6)}


def halfspace_fog(global_material: dict) -> dict:
    """FOG_HALFSPACE of vp-fog-math.slh: extra haze in the low ground.

    The client adds `clamp(amount, 0, fogHalfspaceLimit)` on top of the
    distance fog before the `fogLimit` clamp. The exponential variant (no
    FOG_HALFSPACE_LINEAR) is a height-falloff integral along the view ray:
    density * exp(-falloff * (eye.z - height)) * (1 - exp(-falloff * k * d)) / k
    with k the ray's vertical sine. Karieri fills its quarry with it; Mirage
    switches it off, so its manifest gains nothing.
    """
    flags = global_material.get("flags") or {}
    if not int(flags.get("FOG_HALFSPACE", 0)):
        return {}
    values = {}
    for key in ("Density", "Falloff", "Height", "Limit"):
        found = material_property(global_material, f"fogHalfspace{key}")
        if not found:
            raise SystemExit(f"the client scene enables half-space fog without fogHalfspace{key}")
        values[key.lower()] = round(found[0], 6)
    values["linear"] = bool(int(flags.get("FOG_HALFSPACE_LINEAR", 0)))
    return {"halfspace": values}


def landscape_material(scene) -> dict:
    """The tile mask parameters of the landscape's own material.

    TileMaskSpecular (the HIGH variant of TileMaskAllQualities) tiles the
    four-channel detail `textureTiling` times across the landscape and tints
    each channel with its `tileColor`; the specular term reads the colour
    map's alpha as glossiness.
    """
    materials = {
        int.from_bytes(node["#id"], "little"): node
        for node in scene.get("dataNodes", [])
        if node.get("##name") == "NMaterial" and node.get("#id")
    }
    for _, entity in named_entities(scene):
        for component in components(entity, "RenderComponent"):
            obj = component.get("rc.renderObj") or {}
            if obj.get("##name") != "Landscape":
                continue
            props: dict[str, list[float]] = {}
            current, seen = obj.get("matname"), set()
            while current is not None and current not in seen:
                seen.add(current)
                node = materials.get(int(current))
                if node is None:
                    break
                for key in (node.get("properties") or {}):
                    props.setdefault(key, material_property(node, key))
                current = node.get("parentMaterialKey")
            tiling = props.get("textureTiling") or []
            # A scene that tiles a single channel (World of Ducks authors only
            # tileColor0) leaves the others at the material's white default.
            colours = [props.get(f"tileColor{i}") or ([1.0, 1.0, 1.0] if i else [])
                       for i in range(4)]
            if len(tiling) != 2 or any(len(c) < 3 for c in colours):
                raise SystemExit("the landscape material has no tile colours or tiling")
            return {
                "tiling": [round(v, 6) for v in tiling],
                "colours": [[round(v, 6) for v in c[:3]] + [1.0] for c in colours],
                # tilemask-fp.sl's defaults; the scene authors neither.
                "glossiness": (props.get("inGlossiness") or [0.5])[0],
                "specularity": (props.get("inSpecularity") or [1.0])[0],
                "metalFresnelReflectance": (props.get("metalFresnelReflectance") or [0.5])[0],
            }
    raise SystemExit("the client scene has no landscape material")


def write_terrain(source: Path, destination: Path, bbox: tuple) -> dict:
    """Writes the terrain payload and returns its manifest block.

    The decoded grid rides along under `__grid__` so the spawn markers can be
    seated on exactly the surface that was just written; main() pops it before
    the manifest is serialised.
    """
    blob = source.read_bytes()
    side, bits = struct.unpack_from("<2I", blob, 0)
    if bits != 16:
        raise SystemExit(f"unexpected heightmap sample width {bits} in {source}")
    values = np.frombuffer(blob, dtype="<u2", count=side * side, offset=8).astype(np.float32)
    if values.size != side * side:
        raise SystemExit(f"{MAP.client} heightmap has an incomplete sample grid")

    # Landscape::GetPoint spreads the grid across the bbox and normalises the
    # sample against HEIGHTMAP_MAX, so the step and the height scale both come
    # from the scene instead of being guessed on the Unity side.
    step_x = (bbox[3] - bbox[0]) / (side - 1)
    step_z = (bbox[4] - bbox[1]) / (side - 1)
    if abs(step_x - step_z) > 1e-6:
        raise SystemExit(f"the {MAP.id} landscape is not square")
    step = float(step_x)
    source_grid = untile(
        bbox[2] + (bbox[5] - bbox[2]) * values / HEIGHTMAP_MAX, side).astype("<f4")
    # Row j of the grid is DAVA y = minY + j * step. A map in `dava`
    # orientation stores Godot z = -y, so its rows run from maxY down and
    # the payload is the grid upside down; spawns keep sampling the source.
    height_grid = source_grid[::-1].copy() if MAP.orientation == "dava" else source_grid
    heights = height_grid.reshape(-1)
    dx = np.zeros_like(height_grid)
    dz = np.zeros_like(height_grid)
    dx[:, 1:-1] = (height_grid[:, 2:] - height_grid[:, :-2]) / (2.0 * step)
    dx[:, 0] = (height_grid[:, 1] - height_grid[:, 0]) / step
    dx[:, -1] = (height_grid[:, -1] - height_grid[:, -2]) / step
    dz[1:-1, :] = (height_grid[2:, :] - height_grid[:-2, :]) / (2.0 * step)
    dz[0, :] = (height_grid[1, :] - height_grid[0, :]) / step
    dz[-1, :] = (height_grid[-1, :] - height_grid[-2, :]) / step
    normals = np.stack((-dx, np.ones_like(height_grid), -dz), axis=2)
    normals /= np.linalg.norm(normals, axis=2, keepdims=True)
    normals = normals.astype("<f4").reshape((-1, 3))

    header = TERRAIN_MAGIC + struct.pack(
        "<iffff", side, step, step, float(heights.min()), float(heights.max())
    )
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(header + heights.tobytes() + normals.tobytes())
    corner_z = -float(bbox[4]) if MAP.orientation == "dava" else float(bbox[1])
    return {
        "__grid__": source_grid,
        "resource": f"Maps/{MAP.directory}/terrain_height",
        "side": side,
        "step": step,
        "extent": step * (side - 1),
        # The landscape is centred on the client origin, while the port builds
        # every terrain from its first sample. This is the authored corner.
        "origin": [float(bbox[0]), float(bbox[2]), corner_z],
        "minHeight": float(heights.min()),
        "maxHeight": float(heights.max()),
        "mapScale": [1.0, 1.0, 1.0],
        "normalSource": f"3d/Maps/{MAP.client}/{MAP.heightmap}",
        "surfaceNormal": normals[0].tolist(),
        "collisionEnabled": True,
        "castShadow": 0,
        "tileTexScale": 20.0,
        "tileColour0": [1.0, 1.0, 1.0, 1.0],
        "tileColour1": [1.0, 1.0, 1.0, 1.0],
        "tileColour2": [1.0, 1.0, 1.0, 1.0],
        "tileColour3": [1.0, 1.0, 1.0, 1.0],
        # DAVA's landscape multiplies the colour map by the tile detail and
        # doubles the result; there is no partial blend in the client, so the
        # port's blend factor has to stay at one or the bare colour map (which
        # is a strong yellow here) shows through.
        "detailStrength": 1.0,
        "detailContrast": 1.0,
        "brightness": 1.0,
        "contrast": 1.0,
        "maskSharpness": 1.0,
        "colorMap": f"Maps/{MAP.directory}/colormap",
        "tileMask": f"Maps/{MAP.directory}/tilemask",
        "tileTexture": f"Maps/{MAP.directory}/tiletextures",
    }


def export_ui(pack: Path, map_dir: Path) -> list[dict]:
    """Decode the client's own minimap and battle splash for this map.

    Both live outside the .sc2 scene, in the flat Gfx sprite folders, as a
    single full-frame `texture0`. Gfx2 holds the high-resolution minimap the
    client uses on retina devices, so it is preferred where it exists.
    """
    exported = []
    for candidates, destination_name in [
        ([f"Gfx2/UI/BattleScreenHUD/minimap/{MAP.client_key}/texture0.mali.webp",
          f"Gfx/UI/BattleScreenHUD/minimap/{MAP.client_key}/texture0.mali.webp"],
         "minimap.png"),
        ([f"Gfx/UI/BattleLoadingScreen/{MAP.client_key}/texture0.mali.webp"],
         "loading_screen.png"),
    ]:
        source = next((pack / name for name in candidates if (pack / name).is_file()), None)
        if source is None:
            raise SystemExit(f"cannot find client sprite for {destination_name}")
        from PIL import Image
        with Image.open(source) as image:
            image.save(map_dir / destination_name, format="PNG")
        exported.append({"path": str(source.relative_to(pack)), "sha256": sha(source)})
    return exported


def minimap_frame(pack: Path) -> tuple[list[int], dict]:
    """The frame of the minimap sprite the client actually draws.

    `MiniMapSmall.txt` is a DAVA sprite descriptor: a texture count, the
    texture names, the sprite size, a frame count and one
    `x y w h offsetX offsetY textureIndex name` row per frame. The minimap
    texture is padded to a power of two, and only this frame shows the map.
    """
    candidates = [f"Gfx2/UI/BattleScreenHUD/minimap/{MAP.client_key}/MiniMapSmall.txt",
                  f"Gfx/UI/BattleScreenHUD/minimap/{MAP.client_key}/MiniMapSmall.txt"]
    source = next((pack / name for name in candidates if (pack / name).is_file()), None)
    if source is None:
        raise SystemExit(f"cannot find the minimap sprite descriptor of {MAP.client_key}")
    lines = [line.strip() for line in source.read_text(encoding="utf-8").splitlines() if line.strip()]
    textures = int(lines[0])
    frame_row = lines[1 + textures + 2].split()
    frame = [int(value) for value in frame_row[:4]]
    return frame, {"path": str(source.relative_to(pack)), "sha256": sha(source)}


def minimap_area(terrain: dict, frame: list[int]) -> dict:
    """Where the minimap frame lies on the ground, in manifest space.

    The client renders its minimap over the whole landscape square: x grows to
    the right and the top edge of the frame is the manifest's smallest z,
    which is what Godot's minimap projects with as well (u from x, v from z).
    """
    ox, _, oz = terrain["origin"]
    extent = terrain["extent"]
    return {
        "frame": frame,
        "min": [ox, oz],
        "max": [ox + extent, oz + extent],
    }


def respawn_numbers(members: list, groups: dict[int, list]) -> list[int]:
    """The minimap number of each chosen spawn: its group's place in battle.

    The client marks every spawn area with a numbered sprite. A battle of the
    port only uses some of the authored groups (Ordeal drops two corners,
    the `teams` layout keeps the first ten markers), so the groups that remain
    are numbered from one in their authored order. A spawn the port adds
    itself belongs to the group of the nearest authored marker.
    """
    group_of = {name: key for key, entries in groups.items() for name, _ in entries}
    positions = {name: (matrix[3], matrix[7]) for key, entries in groups.items()
                 for name, matrix in entries}
    resolved = []
    for name, matrix in members:
        if name in group_of:
            resolved.append(group_of[name])
        else:
            nearest = min(positions, key=lambda other: math.dist(positions[other], (matrix[3], matrix[7])))
            resolved.append(group_of[nearest])
    order = {key: index + 1 for index, key in enumerate(sorted(set(resolved)))}
    return [order[key] for key in resolved]


def data_root() -> str:
    """Where the client keeps Strings/ and maps.yaml: 4.8 nests them in Data."""
    return "from_apk" if MAP.client_version == "11.20" else "from_apk/Data"


def client_name_key() -> str:
    """The row of the client string table that names this map."""
    return f"#maps:{MAP.client_key}:{MAP.client}/{MAP.client}.sc2"


def client_string(pack: Path, language: str, key: str) -> str:
    """Reads one row of the client's own Strings/<lang>.yaml.

    The files are flat `"key": "value"` pairs with CRLF endings, so a plain
    line scan is exact and needs no YAML dependency.
    """
    path = pack / data_root() / "Strings" / f"{language}.yaml"
    needle = json.dumps(key, ensure_ascii=False) + ": "
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith(needle):
            return json.loads(line[len(needle):].strip())
    raise SystemExit(f"client string {key} is missing from {path}")


def write_localization(pack: Path, resources: Path) -> dict:
    """Merges the client's authored map name into the port's lang.json."""
    path = resources / "Data/lang.json"
    table = json.loads(path.read_text(encoding="utf-8"))
    entry = {
        language: client_string(pack, language, client_name_key())
        for language in table["languages"]
    }
    # lang.json is appended to by every porter, so its order is authored
    # history rather than a sort. Only the one new row may move.
    name_key = f"map:{MAP.id}/name"
    keys = {}
    for existing, value in table["keys"].items():
        if existing == name_key:
            continue
        if existing.startswith("map:") and name_key < existing and name_key not in keys:
            keys[name_key] = entry
        keys[existing] = value
    keys.setdefault(name_key, entry)
    table["keys"] = keys
    path.write_text(json.dumps(table, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return entry


def basis_towards(source_x: float, source_y: float, target_x: float, target_y: float) -> list[float]:
    """A Godot-serialised yaw basis that looks from one DAVA point at another.

    The inputs are DAVA ground coordinates, where +Y runs north. The manifest
    stores Godot rows and GodotSpace.Basis rebuilds the axes as
    right = (b0, b3, -b6), up = (b1, b4, -b7), forward = (-b2, -b5, b8), so the
    north axis has to be negated once, here, to land on Unity's +Z.
    """
    dx = target_x - source_x
    dz = source_y - target_y
    length = math.hypot(dx, dz) or 1.0
    fx, fz = dx / length, dz / length
    # right = up x forward for a left-handed frame, which is (fz, 0, -fx).
    return [fz, 0.0, -fx, 0.0, 1.0, 0.0, fx, 0.0, fz]


def is_tutorial_prop(entity) -> bool:
    """Whether the client tags the entity as part of the scripted tutorial."""
    tags = str(custom_properties(entity).get("tags", ""))
    return "tutorial" in re.split(r"[\s,;]+", tags.lower())


def is_dropped_prop(entity, config) -> bool:
    """MapConfig.dropped_props and, if asked, the tutorial-tagged props.

    Every exporter and audit applies this before it numbers entities, so the
    names they give the rest of the scene stay comparable.
    """
    if getattr(config, "drop_tutorial_props", False) and is_tutorial_prop(entity):
        return True
    name = str(entity.get("name", ""))
    matrix = matrix_of(entity)
    x, y = matrix[3], matrix[7]
    return any(fragment in name and x0 <= x <= x1 and y0 <= y <= y1
               for fragment, x0, x1, y0, y1
               in getattr(config, "dropped_props", ()))


# CollisionTypeComponent.CollisionType values whose Health means anything:
# 11.20 writes a default Health of 5 on every entity it gives the component.
BREAKABLE_COLLISION_TYPES = (3, 4)


def custom_properties(entity) -> dict:
    """The KeyedArchive an entity's CustomPropertiesComponent carries.

    4.8 serialises it as a blob, 11.20 as the parsed archive. 11.20 also
    moved the collision keys out of it into a CollisionTypeComponent, which
    is folded back in under the 4.8 names so every exporter reads one shape.
    """
    found = {}
    for component in components(entity, "CustomPropertiesComponent"):
        archive = component.get("cpc.properties.archive")
        if isinstance(archive, dict):
            found = dict(archive)
            break
        blob = component.get("cpc.properties")
        if blob is None:
            continue
        found = dava.read_archive(dava.Reader(bytes(blob)))
        break
    for component in components(entity, "CollisionTypeComponent"):
        collision_type = int(component.get("CollisionType", 0))
        found.setdefault("CollisionType", collision_type)
        found.setdefault("FallType", int(component.get("FallingType", 0)))
        found.setdefault("MaterialKind", int(component.get("MaterialKind", 0)))
        if "Density" in component:
            found.setdefault("Density", float(component["Density"]))
        if collision_type in BREAKABLE_COLLISION_TYPES and "Health" in component:
            found.setdefault("Health", int(component["Health"]))
    return found


def client_border(scene, bbox: tuple) -> dict:
    """The authored play area of the landscape.

    DAVA keeps the red no-entry boundary in the landscape entity's custom
    properties (`borderBotX/Y`, `borderTopX/Y`) instead of in the scene
    geometry, and it is a good deal tighter than the landscape bbox. Falling
    back to the bbox lets a tank drive straight through the fence the client
    draws, because BattleTank clamps the hull to this rectangle.
    """
    for _, entity in named_entities(scene):
        properties = custom_properties(entity)
        if "borderBotX" not in properties:
            continue

        xs = [float(properties["borderTopX"]), float(properties["borderBotX"])]
        ys = [float(properties["borderTopY"]), float(properties["borderBotY"])]
        if MAP.orientation == "dava":
            ys = [-y for y in ys]
        return {
            "min": [min(xs), float(bbox[2]), min(ys)],
            "max": [max(xs), float(bbox[5]), max(ys)],
        }

    if MAP.orientation == "dava":
        return {
            "min": [float(bbox[0]), float(bbox[2]), -float(bbox[4])],
            "max": [float(bbox[3]), float(bbox[5]), -float(bbox[1])],
        }
    return {
        "min": [float(bbox[0]), float(bbox[2]), float(bbox[1])],
        "max": [float(bbox[3]), float(bbox[5]), float(bbox[4])],
    }


def named_entities(scene) -> list[tuple[str, dict]]:
    """Every entity of the scene as (name, entity), in authored order."""
    found: list[tuple[str, dict]] = []

    def walk(entity):
        found.append((str(entity.get("name", "")), entity))
        for child in items(entity.get("#hierarchy")):
            if isinstance(child, dict):
                walk(child)

    for root in items(scene["scene"].get("#hierarchy")):
        if isinstance(root, dict):
            walk(root)
    return found


# Two spawn groups whose centres are this close describe the same corner of the
# map; anything further apart is one of the alternate corners the client owns.
# MapConfig.spawn_zone_radius carries it per map.
SPAWN_ZONE_RADIUS = 100.0

# Ordeal capitalises its markers (`Spawn11`), Karieri does not (`spawn11`).
# 11.20 maps separate the number (`Spawn1_01`).
SPAWN_NAME = re.compile(r"spawn([12])_?(\d+)", re.IGNORECASE)


def centroid(points: list[tuple[float, float]]) -> tuple[float, float]:
    return (sum(x for x, _ in points) / len(points),
            sum(y for _, y in points) / len(points))


def spawn_groups(scene) -> dict[int, dict[int, list]]:
    """The authored `Spawn<team><n>` entities, keyed by team and group.

    The client keeps its respawn markers in the scene rather than only as the
    flat `assaultRespawnPoints` table of maps.yaml, and the scene version also
    carries the authored facing. Markers tagged `tutorial`, and the two that
    name a scripted `botname`, belong to the tutorial script and are not battle
    spawns, so they are skipped.
    """
    teams: dict[int, dict[int, list]] = {0: {}, 1: {}}
    for name, entity in named_entities(scene):
        match = SPAWN_NAME.fullmatch(name)
        if not match:
            continue
        props = custom_properties(entity)
        if props.get("tags") == "tutorial" or "botname" in props:
            continue
        # Team 1 numbers its alternatives with `group`, team 2 with
        # `pointNumber`; both count from one.
        group = int(props.get("group") or props.get("pointNumber") or 1)
        team = int(match.group(1)) - 1
        if (team, group) in MAP.dropped_spawn_groups:
            continue
        teams[team].setdefault(group, []).append((name, matrix_of(entity)))
    for team, groups in teams.items():
        if not groups:
            raise SystemExit(f"{MAP.client} has no spawn markers for team {team}")
    return teams


def spawn_zones(groups: dict[int, list]) -> list[dict]:
    """Merges the authored groups that stand on the same corner."""
    zones: list[dict] = []
    for key in sorted(groups):
        members = groups[key]
        centre = centroid([(m[3], m[7]) for _, m in members])
        for zone in zones:
            if math.dist(centre, zone["centre"]) <= MAP.spawn_zone_radius:
                zone["members"].extend(members)
                zone["centre"] = centroid(
                    [(m[3], m[7]) for _, m in zone["members"]])
                break
        else:
            zones.append({"centre": centre, "members": list(members)})
    return zones


def opposing_zones(scene) -> tuple[list, list]:
    """The markers each team starts a battle from.

    Ordeal authors three ally corners and two enemy ones, and a battle uses one
    of each. Random battles there are a deliberate deviation from the client's
    tutorial-only use, so the port picks the widest separated pair, which is
    the north corner against the south one, and both sides drive at the
    control point in the middle.

    An ordinary map such as Karieri authors exactly one corner per team, so
    every marker of a team belongs to the same battle and merging by distance
    would only reorder them.
    """
    teams = spawn_groups(scene)
    if MAP.spawn_layout == "teams":
        def authored(team: dict[int, list]) -> list:
            members = [member for key in sorted(team) for member in team[key]]
            # `spawn17` must follow `spawn12`, so order by the authored number
            # rather than by the name, which sorts `spawn110` before it.
            return sorted(members, key=lambda item: int(SPAWN_NAME.fullmatch(item[0]).group(2)))

        return authored(teams[0]), authored(teams[1])
    allies = spawn_zones(teams[0])
    enemies = spawn_zones(teams[1])
    best = max(((a, b) for a in allies for b in enemies),
               key=lambda pair: math.dist(pair[0]["centre"], pair[1]["centre"]))
    return best[0]["members"], best[1]["members"]


def godot_z(y: float) -> float:
    """The manifest's Godot z of a DAVA y in this map's orientation."""
    return -y if MAP.orientation == "dava" else y


def facing(source_x: float, source_y: float, target_x: float, target_y: float) -> list[float]:
    """basis_towards for DAVA ground points in this map's orientation."""
    if MAP.orientation == "dava":
        return basis_towards(source_x, -source_y, target_x, -target_y)
    return basis_towards(source_x, source_y, target_x, target_y)


def map_entry(pack: Path) -> dict:
    """This map's row of the client's `maps.yaml`."""
    import yaml
    document = yaml.safe_load(
        (pack / data_root() / "maps.yaml").read_text(encoding="utf-8").replace("\r", ""))
    local_name = f"{MAP.client}/{MAP.client}.sc2"

    def find(node):
        if isinstance(node, dict):
            entry = node.get(MAP.client_key)
            if isinstance(entry, dict) and entry.get("localName") == local_name:
                return entry
            for value in node.values():
                found = find(value)
                if found is not None:
                    return found
        return None

    entry = find(document)
    if entry is None:
        raise SystemExit(f"maps.yaml has no {MAP.client_key} entry for {local_name}")
    return entry


def yaml_spawn_points(scene, pack: Path, ground) -> tuple[list[dict], list[dict]]:
    """Spawns from the `assaultRespawnPoints` of maps.yaml.

    A scene without spawn entities leaves the client nothing but these
    flat (x, y) sets, two per team. As on Ordeal the port picks the widest
    separated pair of sets; the points carry no facing, so every tank looks
    at the control point both teams fight over.
    """
    sets = map_entry(pack).get("assaultRespawnPoints") or {}
    allies = [[tuple(map(float, point)) for point in group["points"]]
              for group in sets.get("allies") or []]
    enemies = [[tuple(map(float, point)) for point in group["points"]]
               for group in sets.get("enemies") or []]
    if not allies or not enemies:
        raise SystemExit(f"maps.yaml authors no respawn sets for {MAP.id}")
    ally, enemy = max(((a, b) for a in allies for b in enemies),
                      key=lambda pair: math.dist(centroid(pair[0]), centroid(pair[1])))
    target = None
    for _, entity in named_entities(scene):
        if custom_properties(entity).get("type") == "controlpoint":
            matrix = matrix_of(entity)
            target = (matrix[3], matrix[7])
            break
    if target is None:
        raise SystemExit(f"{MAP.client} has no control point for the spawns to face")

    def make(points, team):
        return [{
            "name": "TankSpawner" if index == 0 else f"TankSpawner{index + 1}",
            "team": team,
            "basis": facing(x, y, target[0], target[1]),
            "origin": [x, ground(x, y), godot_z(y)],
            # One `assaultRespawnPoints` set per team takes part in a battle.
            "respawn": 1,
        } for index, (x, y) in enumerate(points)]

    return make(ally, 0), make(enemy, 1)


def ground_sampler(heights: np.ndarray, bbox: tuple, step: float):
    side = heights.shape[0]

    def ground(x: float, y: float) -> float:
        # Bilinear, exactly like TerrainHeightField.SampleHeight, so the baked
        # Y matches the surface the runtime raycasts against.
        gx = min(max((x - bbox[0]) / step, 0.0), side - 1.0001)
        gy = min(max((y - bbox[1]) / step, 0.0), side - 1.0001)
        i, j = int(gx), int(gy)
        tx, ty = gx - i, gy - j
        top = heights[j, i] + (heights[j, i + 1] - heights[j, i]) * tx
        bottom = heights[j + 1, i] + (heights[j + 1, i + 1] - heights[j + 1, i]) * tx
        return float(top + (bottom - top) * ty)

    return ground


def spawn_points(scene, heights: np.ndarray, bbox: tuple, step: float,
                 limit: int = 10) -> tuple[list[dict], list[dict]]:
    """Markers on the authored spawn transforms, seated on the terrain.

    The client drops a vehicle onto the landscape instead of trusting the
    marker's own Z, so the height is sampled from the same field the runtime
    raycasts against and the manifest stays a complete transform. The facing is
    the marker's own: a DAVA spawn looks along its local +Y.
    """
    allies, enemies = opposing_zones(scene)
    for x, y in MAP.extra_ally_spawns:
        # Face the way the nearest authored slot faces: the replacement stands
        # in the same corner and has to leave it the same way.
        _, nearest = min(allies, key=lambda item: math.dist((x, y), (item[1][3], item[1][7])))
        matrix = list(nearest)
        matrix[3], matrix[7] = x, y
        allies.append((f"SpawnExtra{len(allies) + 1}", matrix))
    allies = allies[:limit]
    enemies = enemies[:limit]
    groups = spawn_groups(scene)
    numbers = {0: respawn_numbers(allies, groups[0]), 1: respawn_numbers(enemies, groups[1])}
    side = heights.shape[0]

    def ground(x: float, y: float) -> float:
        # Bilinear, exactly like TerrainHeightField.SampleHeight, so the baked
        # Y matches the surface the runtime raycasts against.
        gx = min(max((x - bbox[0]) / step, 0.0), side - 1.0001)
        gy = min(max((y - bbox[1]) / step, 0.0), side - 1.0001)
        i, j = int(gx), int(gy)
        tx, ty = gx - i, gy - j
        top = heights[j, i] + (heights[j, i + 1] - heights[j, i]) * tx
        bottom = heights[j + 1, i] + (heights[j + 1, i + 1] - heights[j + 1, i]) * tx
        return float(top + (bottom - top) * ty)

    def make(members, team):
        result = []
        for index, (name, matrix) in enumerate(members):
            x, y = matrix[3], matrix[7]
            # matrix_of has already transposed DAVA's row-vector matrix, so
            # the marker's local +Y axis - the way a DAVA spawn looks - is
            # column 1.
            result.append({
                "name": "TankSpawner" if index == 0 else f"TankSpawner{index + 1}",
                "team": team,
                "basis": facing(x, y, x + matrix[1], y + matrix[5]),
                "origin": [x, ground(x, y), godot_z(y)],
                "respawn": numbers[team][index],
            })
        return result

    return make(allies, 0), make(enemies, 1)


# A bot roams this far around the marker it picked. The client steers along its
# authored polylines instead of scoring markers, so there is no authored radius
# to copy; this is one tank length either side of the route's end.
AI_MARKER_RADIUS = 15.0

# Two route ends closer than this describe the same position on the ground.
AI_MARKER_SPACING = 20.0


def ai_markers(scene, ground=None) -> list[dict]:
    """Roaming goals derived from whatever the scene authors for its bots.

    Ordeal ships PathComponents: sixteen waypoint graphs leading from a spawn
    corner into the contested middle and on across the map. The port keeps
    every waypoint of every route, thinned to one goal per AI_MARKER_SPACING.
    Keeping only the route ends - the waypoints no edge leaves - left thirteen
    goals bunched in the middle, and a bot team with that few goals drove the
    same line in single file.

    Karieri ships none of those. It authors fourteen `botspawn` entities, each
    the position and vehicle of one scripted bot, and three `strategicpoint`
    bases with their own capture radius. Those are the positions the client
    itself sends a bot to, so they take the place of the route leaves.

    Either way the authored control point is added first: it is the one place
    both sides are meant to fight over.
    """
    markers: list[dict] = []

    def push(name: str, x: float, y: float, z: float, priority: int, radius: float):
        for existing in markers:
            ox, _, oz = existing["origin"]
            if math.dist((x, y), (ox, godot_z(oz))) <= AI_MARKER_SPACING:
                return
        markers.append({
            "name": name,
            "origin": [x, z, godot_z(y)],
            "priority": priority,
            "radius": radius,
            "type": 0,
        })

    for name, entity in named_entities(scene):
        props = custom_properties(entity)
        if props.get("type") != "controlpoint":
            continue
        matrix = matrix_of(entity)
        # The objective outranks a route end, which is what a non-zero
        # ai_marker priority means to the tactical scoring.
        push(name, matrix[3], matrix[7], matrix[11], 1, AI_MARKER_RADIUS * 2.0)

    if MAP.ai_source == "markers":
        for name, entity in named_entities(scene):
            props = custom_properties(entity)
            kind = props.get("type")
            if kind not in ("strategicpoint", "botspawn"):
                continue
            matrix = matrix_of(entity)
            # A base is an objective like the control point; a bot spawn is
            # only a position the client is willing to park a tank on.
            strategic = kind == "strategicpoint"
            radius = float(props.get("radius") or AI_MARKER_RADIUS) if strategic \
                else AI_MARKER_RADIUS
            push(f"{kind}_{name}", matrix[3], matrix[7], matrix[11],
                 1 if strategic else 0, radius)
    else:
        for name, entity in named_entities(scene):
            for component in components(entity, "PathComponent"):
                count = int(component.get("waypointCount") or 0)
                for index in range(count):
                    waypoint = component.get(f"waypoint_{index}") or {}
                    x, y, z = waypoint["position"]
                    push(f"{name}_{waypoint.get('name', index)}", x, y, z, 0,
                         AI_MARKER_RADIUS)
    if not markers:
        raise SystemExit(f"{MAP.client} has no authored goals to derive ai markers from")
    if MAP.route_markers and ground is None:
        raise SystemExit(f"{MAP.id} route markers need the landscape to stand on")
    for name, x, y, priority, radius, kind in MAP.route_markers:
        # Appended as authored, not thinned: a route goal next to the
        # control point is a different job (hold the centre) than the point.
        markers.append({
            "name": name,
            "origin": [x, round(ground(x, y), 2), godot_z(y)],
            "priority": priority,
            "radius": radius,
            "type": kind,
        })
    return markers

def export_scene(scene, map_dir: Path, output_obj: Path, output_mtl: Path) -> dict:
    groups = {dava.group_id(group): group for group in scene["geometry"]}
    materials = {
        int.from_bytes(node["#id"], "little"): node
        for node in scene.get("dataNodes", [])
        if node.get("##name") == "NMaterial" and node.get("#id")
    }

    texture_dir = map_dir / "Textures"
    material_defs = {}
    used_textures = {}
    failed_textures = set()
    obj_lines = ["# Generated from the original Blitz 4.8.0 client map.", f"mtllib {MAP.id}_scene.mtl"]
    vertex_count = uv_count = normal_count = face_count = batch_count = 0
    entity_count = 0

    def add_texture(reference: str | None) -> str | None:
        source = resolve_source(PACK / "3d/Maps" / MAP.client, reference or "")
        if source is None:
            return None
        key = str(source.relative_to(PACK))
        if key in used_textures:
            return used_textures[key]
        if key in failed_textures:
            return None
        stem = re.sub(r"[^A-Za-z0-9_]+", "_", source.stem)
        name = f"{len(used_textures):03d}_{stem}.png"
        destination = texture_dir / name
        if not decode_texture(source, destination):
            failed_textures.add(key)
            return None
        used_textures[key] = f"Textures/{name}"
        return used_textures[key]

    def add_material(material_id: int) -> str:
        if material_id in material_defs:
            return material_defs[material_id]["name"]
        source = inherited_material(materials, material_id)
        texture_reference = source.get("albedo") or source.get("colorTexture")
        texture = add_texture(texture_reference)
        name = material_name(material_id, source, texture_reference)
        material_defs[material_id] = {"name": name, "texture": texture, "source": texture_reference}
        return name

    destructibles = []

    def write_batches(entity, batches, group_name):
        nonlocal vertex_count, uv_count, normal_count, face_count, batch_count
        if not batches:
            return 0
        matrix = mat_mul(DAVA_TO_UNITY, matrix_of(entity))
        obj_lines.append(f"g {group_name}")
        written = 0
        for group_id, material_id, kind in batches:
            group = groups.get(group_id)
            if group is None:
                continue
            streams = dava.decode_vertices(group)
            indices = dava.decode_indices(group)
            positions = streams.get("position") or []
            uvs = streams.get("uv0") or [(0.0, 0.0)] * len(positions)
            normals = streams.get("normal") or [(0.0, 0.0, 1.0)] * len(positions)
            if not positions or len(uvs) != len(positions):
                continue
            if kind == SPEEDTREE_KIND:
                card = speedtree_card(streams)
                if card is None:
                    continue
                positions = card
                # Static billboards stand upright; foliage is lit from above.
                normals = [SPEEDTREE_UP] * len(card)
            obj_lines.append(f"usemtl {add_material(material_id)}")
            base_v, base_t, base_n = vertex_count, uv_count, normal_count
            for point in positions:
                x, y, z = xform_point(matrix, point)
                # Unity's OBJ importer negates X on import (it treats OBJ as
                # right-handed). Pre-negate so the imported mesh lands on the
                # same Unity X as BattleTerrain; without this every prop was
                # mirrored across the map and floated over / sank into the
                # wrong patch of landscape.
                obj_lines.append(f"v {-x:.7g} {y:.7g} {z:.7g}")
            for u, v in uvs:
                obj_lines.append(f"vt {u:.7g} {1.0 - v:.7g}")
            for normal in normals:
                x, y, z = xform_dir(matrix, normal)
                length = math.sqrt(x * x + y * y + z * z) or 1.0
                obj_lines.append(f"vn {-x / length:.7g} {y / length:.7g} {z / length:.7g}")
            for offset in range(0, len(indices) - 2, 3):
                # The file is mirrored in X for the importer, so the winding
                # is reversed too; Unity reverses it again while un-mirroring.
                a, c, b = (indices[offset + i] + 1 for i in (0, 1, 2))
                obj_lines.append(
                    f"f {base_v + a}/{base_t + a}/{base_n + a} "
                    f"{base_v + b}/{base_t + b}/{base_n + b} "
                    f"{base_v + c}/{base_t + c}/{base_n + c}"
                )
            vertex_count += len(positions)
            uv_count += len(positions)
            normal_count += len(positions)
            face_count += len(indices) // 3
            batch_count += 1
            written += 1
        return written

    def as_int(value, fallback=0):
        try:
            return int(float(value))
        except (TypeError, ValueError):
            return fallback

    def is_dropped(entity) -> bool:
        return is_dropped_prop(entity, MAP)

    def walk(entity):
        nonlocal entity_count
        if is_dropped(entity):
            return
        props = custom_properties(entity)
        health = max(0, as_int(props.get("Health")))
        collision_type = as_int(props.get("CollisionType"))
        fall_type = as_int(props.get("FallType", props.get("FallingType", 0)))
        # Karieri's 4.8.0 scene marks every cactus CollisionType=5/FallType=2
        # but supplies no Health or wreck switch. Give those client-authored
        # falling props the same small baseline health so shells can destroy them.
        cactus_override = health <= 0 and collision_type == 5 and fall_type > 0
        is_destructible = health > 0 or cactus_override
        health = health if health > 0 else (10 if cactus_override else 0)

        switch = next(components(entity, "SwitchComponent"), None)
        intact_state = as_int(switch.get("sc.switchindex"), 0) if switch else 0
        active_batches = [
            (group_id, material_id, kind)
            for group_id, material_id, kind in batches_of(entity, intact_state)
            if not is_invisible(inherited_material(materials, material_id))
        ]
        if active_batches:
            entity_count += 1
            entity_name = re.sub(
                r"[^A-Za-z0-9_]+", "_", str(entity.get("name", "mesh")))
            prefix = f"entity_{entity_count}_{entity_name}"
            intact_group = prefix + "_intact" if is_destructible else prefix
            if write_batches(entity, active_batches, intact_group) and is_destructible:
                wreck_state = 1 if intact_state == 0 else 0
                wreck_batches = [
                    (group_id, material_id, kind)
                    for group_id, material_id, kind in batches_of(entity, wreck_state)
                    if not is_invisible(inherited_material(materials, material_id))
                ]
                active_signature = [(group, material) for group, material, _ in active_batches]
                wreck_signature = [(group, material) for group, material, _ in wreck_batches]
                wreck_group = None
                if wreck_batches and wreck_signature != active_signature:
                    candidate = prefix + "_wreck"
                    if write_batches(entity, wreck_batches, candidate):
                        wreck_group = candidate
                destructibles.append({
                    "name": str(entity.get("name", "map prop")),
                    "intactGroup": intact_group,
                    "wreckGroup": wreck_group,
                    "health": health,
                    "collisionType": collision_type,
                    "materialKind": as_int(props.get("MaterialKind")),
                    "fallType": fall_type,
                    "collisionTypeCrashed": as_int(props.get("CollisionTypeCrashed")),
                    "healthFromClient": as_int(props.get("Health")),
                })
        for child in items(entity.get("#hierarchy")):
            if isinstance(child, dict):
                walk(child)

    for root in items(scene["scene"].get("#hierarchy")):
        if isinstance(root, dict):
            walk(root)

    output_obj.parent.mkdir(parents=True, exist_ok=True)
    output_obj.write_text("\n".join(obj_lines) + "\n", encoding="utf-8")
    mtl_lines = ["# Generated from the original Blitz 4.8.0 client materials."]
    for entry in material_defs.values():
        mtl_lines.extend([
            f"newmtl {entry['name']}",
            "Ka 0.15 0.15 0.15",
            "Kd 1.0 1.0 1.0",
            "Ks 0.0 0.0 0.0",
            "Ns 1.0",
            "illum 1",
        ])
        if entry["texture"]:
            mtl_lines.append(f"map_Kd {entry['texture']}")
        mtl_lines.append("")
    output_mtl.write_text("\n".join(mtl_lines), encoding="utf-8")
    return {
        "entities": entity_count,
        "batches": batch_count,
        "vertices": vertex_count,
        "triangles": face_count,
        "materials": len(material_defs),
        "textures": len(used_textures),
        "destructibles": destructibles,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--map", choices=sorted(MAPS), default="ordeal",
                        help="which client map of the MAPS table to port")
    parser.add_argument("--pack", type=Path, default=None,
                        help="the client pack; defaults to the one of the map's release")
    parser.add_argument("--destination", type=Path, default=ROOT)
    parser.add_argument(
        "--skip-scene",
        action="store_true",
        help="reuse the already exported <map>_scene.obj and its recorded counts",
    )
    args = parser.parse_args()

    # Every helper below reads the map it is porting off this module global,
    # so it has to be chosen before any of them run.
    global MAP
    MAP = MAPS[args.map]
    if MAP.client_version == "11.20":
        import client_map_1120
        return client_map_1120.main(sys.modules[__name__], MAP, args)

    pack = args.pack or PACK
    destination = args.destination
    source_dir = pack / "3d/Maps" / MAP.client
    resources = destination / "Assets/Blitz/Resources"
    map_dir = resources / "Maps" / MAP.directory
    map_dir.mkdir(parents=True, exist_ok=True)

    scene_stem = f"{MAP.id}_scene"
    stats_path = map_dir / f"{scene_stem}.json"
    if args.skip_scene and not stats_path.is_file():
        raise SystemExit(f"--skip-scene needs a previously exported {scene_stem}.json")

    scene = dava.read_sc2((source_dir / f"{MAP.client}.sc2").read_bytes())
    bbox = landscape_bbox(scene)
    terrain = write_terrain(
        source_dir / MAP.heightmap,
        map_dir / "terrain_height.bytes",
        bbox,
    )
    landscape_textures = [
        (MAP.colormap, "colormap.png"),
        (MAP.tilemask, "tilemask.png"),
        (MAP.tiletextures, "tiletextures.png"),
    ]
    for source_name, destination_name in landscape_textures:
        source = source_dir / source_name
        if not decode_texture(source, map_dir / destination_name):
            raise SystemExit(f"cannot decode required map texture: {source}")

    if args.skip_scene:
        obj_stats = json.loads(stats_path.read_text(encoding="utf-8"))
    elif MAP.scene_format == "client":
        import client_map_scene
        obj_stats = client_map_scene.export(
            sys.modules[__name__], MAP, scene, pack, map_dir, scene_stem)
        stats_path.write_text(json.dumps(obj_stats, indent=2) + "\n", encoding="utf-8")
    else:
        obj_stats = export_scene(
            scene,
            map_dir,
            map_dir / f"{scene_stem}.obj",
            map_dir / f"{scene_stem}.mtl",
        )
        stats_path.write_text(json.dumps(obj_stats, indent=2) + "\n", encoding="utf-8")

    environment = scene_environment(scene)
    ui_sources = export_ui(pack, map_dir)
    frame, frame_source = minimap_frame(pack)
    ui_sources.append(frame_source)
    name = write_localization(pack, resources)

    height_grid = terrain.pop("__grid__")
    if MAP.spawn_layout == "yaml":
        team_a, team_b = yaml_spawn_points(
            scene, pack, ground_sampler(height_grid, bbox, terrain["step"]))
    else:
        team_a, team_b = spawn_points(scene, height_grid, bbox, terrain["step"])
    if MAP.client_shading:
        landscape = landscape_material(scene)
        terrain["tileTexScale"] = landscape["tiling"][0]
        for index, colour in enumerate(landscape["colours"]):
            terrain[f"tileColour{index}"] = colour
        terrain["client"] = landscape
    markers = ai_markers(scene, ground_sampler(height_grid, bbox, terrain["step"]))
    # This client map draws its own sky: `SkyFlattenSphere` is an ordinary
    # render entity inside the .sc2 and is exported with the rest of the
    # scene, so the port must not also build the substitute sky planes the
    # Godot maps need.
    sky = []

    manifest = {
        "id": MAP.id,
        "nameKey": f"map:{MAP.id}/name",
        "scene": f"3d/Maps/{MAP.client}/{MAP.client}.sc2",
        "externalScene": f"Maps/{MAP.directory}/{scene_stem}",
        "externalSceneSource": f"3d/Maps/{MAP.client}/{MAP.client}.sc2",
        "randomTiers": list(MAP.random_tiers),
        **({"modeOnly": True} if MAP.mode_only else {}),
        **({"orientation": MAP.orientation} if MAP.orientation != "mirrored" else {}),
        **({"sceneFormat": MAP.scene_format} if MAP.scene_format != "obj" else {}),
        "minimap": f"Maps/{MAP.directory}/minimap",
        "minimapArea": minimap_area(terrain, frame),
        "loadingScreen": f"Maps/{MAP.directory}/loading_screen",
        "sky": {"basis": [1, 0, 0, 0, 1, 0, 0, 0, 1], "origin": [0, 0, 0], "faces": sky},
        "border": client_border(scene, bbox),
        "terrain": terrain,
        "environment": environment,
        "directionalLight": {
            "basis": environment["sunBasis"],
            "origin": [0, 64, 0],
            "color": environment["sunColor"],
            # DAVA measures a light in its own units (this scene authors 300),
            # which is not a Unity intensity, so the port keeps one exposure
            # for every map instead of inventing a conversion. MapConfig.exposure
            # only trims it where a map's albedo needs it.
            "energy": round(CLIENT_SUN_ENERGY * MAP.exposure, 6),
        },
        "spawns": {"teamA": team_a, "teamB": team_b},
        "aiMarkers": markers,
        "objects": obj_stats,
        "sources": [
            {"path": f"3d/Maps/{MAP.client}/{MAP.client}.sc2", "sha256": sha(source_dir / f"{MAP.client}.sc2")},
        ] + [
            {"path": f"3d/Maps/{MAP.client}/{reference}", "sha256": sha(source_dir / reference)}
            for reference in [MAP.heightmap, MAP.colormap, MAP.tilemask, MAP.tiletextures]
        ] + [
            {"path": "from_apk/Data/Strings/en.yaml", "sha256": sha(pack / "from_apk/Data/Strings/en.yaml")},
            {"path": "from_apk/Data/Strings/ru.yaml", "sha256": sha(pack / "from_apk/Data/Strings/ru.yaml")},
        ] + ([
            {"path": "from_apk/Data/maps.yaml", "sha256": sha(pack / "from_apk/Data/maps.yaml")},
        ] if MAP.spawn_layout == "yaml" else []) + ui_sources,
    }
    (resources / "Data").mkdir(parents=True, exist_ok=True)
    (resources / f"Data/map_{MAP.id}.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(
        {"map": MAP.id, "name": name, "terrain": terrain, "objects": obj_stats},
        indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
