#!/usr/bin/env python3
"""Ports the Godot battle sound sets into Unity Resources.

Godot keeps its battle audio in typed Resources (assets/sfx/resources/*.gd):
StreamRandom holds a pool of interchangeable samples plus a gain, and
EngineSounds / TrackSounds / ExplosionSound / ShellSounds group those pools
under named slots. Unity has no equivalent Resource graph, so the .tres files
are flattened into one JSON table (Resources/Data/tank_sounds.json) that the
runtime reads, and every referenced sample is copied under Resources/Audio
keeping its source-relative path so a clip name stays traceable to the Godot
asset it came from.

The sets that Audio (scripts/_audio.gd) builds in code instead of a .tres -
collision, roughness, the track repair transients, the battle UI stingers and
the crew voice lines - are mirrored in SIMPLE_SETS below.
"""

import json
import os
import re
import shutil
import sys

import port_blitz_sfx

GODOT = os.environ.get(
    "GODOT_PROJECT", "/home/smh01/Studio canaryProjects/Blitz Legacy")
UNITY = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
AUDIO_OUT = os.path.join(UNITY, "Assets/Blitz/Resources/Audio")
DATA_OUT = os.path.join(UNITY, "Assets/Blitz/Resources/Data/tank_sounds.json")

# scripts/_audio.gd: info_to_gun_sound, info_to_engine_snd, info_to_track_snd.
# The keys are the ModuleGun.ShootSound / TankInfoMain.EngineSound /
# TankInfoMain.TrackSound enum names the card table stores as integers.
GUN_SETS = {
    "shot_auto": "tanks/guns/shot_auto.tres",
    "shot_small": "tanks/guns/shot_small.tres",
    "shot_medium": "tanks/guns/shot_medium.tres",
    "shot_main": "tanks/guns/shot_main.tres",
    "shot_large": "tanks/guns/shot_large.tres",
    "shot_huge": "tanks/guns/shot_huge.tres",
}

ENGINE_SETS = {
    name: "tanks/engine/%s.tres" % name
    for name in (
        "de_ht", "de_lt", "de_mt", "de_traktor",
        "uk_ht", "uk_lt", "uk_mt", "uk_traktor",
        "usa_ht", "usa_lt", "usa_mt", "usa_traktor",
        "ussr_ht", "ussr_lt", "ussr_mt", "ussr_traktor",
    )
}

TRACK_SETS = {
    name: "tanks/tracks/%s.tres" % name
    for name in ("ht", "lt", "mt", "traktor")
}

SHELL_SETS = {
    name: "tanks/shells/%s.tres" % name
    for name in ("ap", "apcr", "he", "heat")
}

EXPLOSION_SETS = {
    "tank_explosion": "tanks/explosion/exp_destruction.tres",
}

# StreamRandom-only .tres files.
RANDOM_SETS = {
    "inside_pen": "tanks/shells/inside/_pierce.tres",
    "inside_bounce": "tanks/shells/inside/_bounce.tres",
}

# Sets that _audio.gd assembles with StreamRandom.new().with_streams(...) or
# holds as a single stream. Paths are relative to assets/sfx.
SIMPLE_SETS = {
    "tank_collision_light": [
        "tanks/collision/tanks_tanks_physics_common_tank_light_hit_01.wav",
        "tanks/collision/tanks_tanks_physics_common_tank_light_hit_02.wav",
        "tanks/collision/tanks_tanks_physics_common_tank_light_hit_03.wav",
        "tanks/collision/tanks_tanks_physics_common_tank_light_hit_05.wav",
        "tanks/collision/tanks_tanks_physics_common_tank_light_hit_07.wav",
        "tanks/collision/tanks_tanks_physics_common_tank_light_hit_10.wav",
    ],
    "tank_collision_heavy": [
        "tanks/collision/tanks_tanks_physics_common_tank_heavy_hit_01.wav",
        "tanks/collision/tanks_tanks_physics_common_tank_heavy_hit_02.wav",
        "tanks/collision/tanks_tanks_physics_common_tank_heavy_hit_03.wav",
        "tanks/collision/tanks_tanks_physics_common_tank_heavy_hit_04.wav",
        "tanks/collision/tanks_tanks_physics_common_tank_heavy_hit_05.wav",
    ],
    "roughness_light": ["tanks/roughness/roughness_light_tank.wav"],
    "roughness_medium": ["tanks/roughness/roughness_medium_tank.wav"],
    "roughness_heavy": ["tanks/roughness/roughness_heavy_tank.wav"],
    "roughness_light_inside": [
        "tanks/roughness/rougness_inside_light_tank.wav"],
    "roughness_medium_inside": [
        "tanks/roughness/rougness_inside_medium_tank.wav"],
    # _audio.gd points the heavy inside bed at the medium sample too.
    "roughness_heavy_inside": [
        "tanks/roughness/rougness_inside_medium_tank.wav"],
    "tracks_destroy": ["tanks/tracks/hit_treads_02.wav"],
    "tracks_fix": ["tanks/tracks/tread_fixed_01.wav"],
    "ui_sniper_in": ["ui/snipermode_on.wav"],
    "ui_sniper_out": ["ui/snipermode_off.wav"],
    "ui_autoaim_on": ["ui/auto_target_on.wav"],
    "ui_autoaim_off": ["ui/auto_target_off.wav"],
    "ui_shell_select": ["ui/shell_choose_01.wav"],
    "ui_lamp": ["ui/lamp_01.wav"],
    "ui_countdown": ["ui/timer.wav"],
}

# Audio.crew_msg_to_stream, keyed by the CrewMessage enum name.
CREW_SETS = {
    "battle_start": ["ui/crew/start_battle_0%d.wav" % i for i in range(1, 9)],
    "ally_killed": [
        "ui/crew/ally_killed_by_player_01.wav",
        "ui/crew/ally_killed_by_player_02.wav",
    ],
    "armor_not_pierce": [
        "ui/crew/armor_not_pierced_by_player_0%d.wav" % i
        for i in range(1, 6)
    ],
    "armor_pierced": [
        "ui/crew/armor_pierced_by_player_%02d.wav" % i
        for i in range(1, 13)
    ],
    "armor_not_hit": [
        "ui/crew/armor_pierced_crit_by_player_0%d.wav" % i
        for i in range(1, 10)
    ],
    "ricoshet": [
        "ui/crew/armor_ricochet_by_player_0%d.wav" % i
        for i in range(1, 8)
    ],
    "enemy_ignited": [
        "ui/crew/enemy_fire_started_by_player_0%d.wav" % i
        for i in range(1, 5)
    ],
    "enemy_killed": [
        "ui/crew/enemy_killed_by_player_0%d.wav" % i
        for i in range(1, 10)
    ],
    "fire_started": [
        "ui/crew/fire_started_01.wav",
        "ui/crew/fire_started_02.wav",
    ],
    # _audio.gd reuses the fire_started lines for FIRE_STOPPED.
    "fire_stopped": [
        "ui/crew/fire_started_01.wav",
        "ui/crew/fire_started_02.wav",
    ],
    "module_track_crit": [
        "ui/crew/track_damaged_0%d.wav" % i for i in range(1, 5)],
    "module_track_destroyed": [
        "ui/crew/track_destroyed_0%d.wav" % i for i in range(1, 5)],
    "module_track_fixed": [
        "ui/crew/track_functional_0%d.wav" % i for i in range(1, 5)],
    "module_ammo_bay_crit": [
        "ui/crew/ammo_bay_damaged_0%d.wav" % i for i in range(1, 4)],
    "tank_destroyed": [
        "ui/crew/vehicle_destroyed_0%d.wav" % i for i in range(1, 4)],
}

EXT_RE = re.compile(
    r'^\[ext_resource type="(?P<type>[^"]+)"[^\]]*?path="res://(?P<path>[^"]+)"'
    r'[^\]]*?id="(?P<id>[^"]+)"\]')
SUB_RE = re.compile(r'^\[sub_resource type="[^"]+" id="(?P<id>[^"]+)"\]')
RES_RE = re.compile(r'^\[resource\]')
ASSIGN_RE = re.compile(r'^(?P<key>[A-Za-z_][A-Za-z0-9_]*) = (?P<value>.*)$')
REF_RE = re.compile(r'(?P<kind>SubResource|ExtResource)\("(?P<id>[^"]+)"\)')

copied = {}


def clip_name(godot_path):
    """assets/sfx/<rel>.wav -> Audio/<rel>, the Resources.Load name."""
    rel = godot_path
    prefix = "assets/sfx/"
    if not rel.startswith(prefix):
        raise SystemExit("sound outside assets/sfx: " + godot_path)
    rel = rel[len(prefix):]
    return os.path.splitext(rel)[0]


def copy_clip(godot_path):
    name = clip_name(godot_path)
    if name in copied:
        return name

    source = os.path.join(GODOT, godot_path)
    if not os.path.isfile(source):
        raise SystemExit("missing sound: " + source)

    destination = os.path.join(
        AUDIO_OUT, name + os.path.splitext(godot_path)[1])
    os.makedirs(os.path.dirname(destination), exist_ok=True)
    if not os.path.isfile(destination) or \
            os.path.getsize(destination) != os.path.getsize(source):
        shutil.copyfile(source, destination)

    copied[name] = destination
    return name


class Tres(object):
    """Just enough of the .tres text format to read a Resource graph."""

    def __init__(self, path):
        self.ext = {}
        self.sub = {}
        self.resource = {}

        section = None
        with open(path, "r", encoding="utf-8") as handle:
            for line in handle:
                line = line.rstrip("\n")
                ext = EXT_RE.match(line)
                if ext:
                    self.ext[ext.group("id")] = ext.group("path")
                    section = None
                    continue

                sub = SUB_RE.match(line)
                if sub:
                    section = {}
                    self.sub[sub.group("id")] = section
                    continue

                if RES_RE.match(line):
                    section = self.resource
                    continue

                assign = ASSIGN_RE.match(line)
                if assign is not None and section is not None:
                    section[assign.group("key")] = assign.group("value")

    def stream_random(self, value):
        """Resolves a StreamRandom reference into {volume, streams}."""
        if value is None:
            return None

        ref = REF_RE.search(value)
        if not ref:
            return None

        if ref.group("kind") == "ExtResource":
            # A StreamRandom saved as its own file, e.g. shells/inside.
            return read_random(self.ext[ref.group("id")])

        body = self.sub.get(ref.group("id"))
        if body is None:
            return None

        streams = []
        for match in REF_RE.finditer(body.get("streams", "")):
            if match.group("kind") != "ExtResource":
                continue
            streams.append(copy_clip(self.ext[match.group("id")]))

        if not streams:
            return None

        return {
            "volume": float(body.get("volume", "1")),
            "streams": streams,
        }

    def slots(self, names):
        out = {}
        for name in names:
            pool = self.stream_random(self.resource.get(name))
            if pool is not None:
                out[name] = pool
        return out


def read_random(godot_path):
    tres = Tres(os.path.join(GODOT, godot_path))
    streams = []
    for match in REF_RE.finditer(tres.resource.get("streams", "")):
        if match.group("kind") != "ExtResource":
            continue
        streams.append(copy_clip(tres.ext[match.group("id")]))

    return {
        "volume": float(tres.resource.get("volume", "1")),
        "streams": streams,
    }


ENGINE_SLOTS = (
    "accel", "deccel", "start", "stop",
    "idle", "low_rpm", "mid_rpm", "high_rpm", "turning",
)
TRACK_SLOTS = (
    "start", "stop",
    "speed_10", "speed_20", "speed_30",
    "speed_40", "speed_50", "speed_60", "speed_70",
)
EXPLOSION_SLOTS = ("sound_near", "sound_dist", "sound_inside")
SHELL_SLOTS = tuple(
    "%s_%s" % (kind, size)
    for kind in ("pen", "bounce", "ricoshet", "crit", "impact")
    for size in ("small", "medium", "main", "large", "huge")
)


def read_group(paths, slots):
    out = {}
    for name, relative in sorted(paths.items()):
        tres = Tres(os.path.join(GODOT, "assets/sfx", relative))
        out[name] = tres.slots(slots)
    return out


def read_simple(groups):
    out = {}
    for name, streams in sorted(groups.items()):
        out[name] = {
            "volume": 1.0,
            "streams": [copy_clip("assets/sfx/" + path) for path in streams],
        }
    return out


def main():
    table = {
        "guns": read_group(GUN_SETS, EXPLOSION_SLOTS),
        "engines": read_group(ENGINE_SETS, ENGINE_SLOTS),
        "tracks": read_group(TRACK_SETS, TRACK_SLOTS),
        "shells": read_group(SHELL_SETS, SHELL_SLOTS),
        "explosions": read_group(EXPLOSION_SETS, EXPLOSION_SLOTS),
        "sounds": {},
        "crew": read_simple(CREW_SETS),
    }

    for name, relative in sorted(RANDOM_SETS.items()):
        table["sounds"][name] = read_random("assets/sfx/" + relative)
    table["sounds"].update(read_simple(SIMPLE_SETS))

    # The XM81 rockets have no Godot counterpart, so their launch report,
    # flight bed and impacts come straight out of the Blitz FMOD banks.
    port_blitz_sfx.extend(table)

    os.makedirs(os.path.dirname(DATA_OUT), exist_ok=True)
    with open(DATA_OUT, "w", encoding="utf-8") as handle:
        json.dump(table, handle, indent=1, sort_keys=True)
        handle.write("\n")

    total = sum(os.path.getsize(path) for path in copied.values())
    print("sound sets: guns=%d engines=%d tracks=%d shells=%d crew=%d misc=%d"
          % (len(table["guns"]), len(table["engines"]), len(table["tracks"]),
             len(table["shells"]), len(table["crew"]), len(table["sounds"])))
    print("clips copied: %d (%.1f MiB) -> %s"
          % (len(copied), total / (1024.0 * 1024.0), AUDIO_OUT))


if __name__ == "__main__":
    sys.exit(main())
