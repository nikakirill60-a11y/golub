#!/usr/bin/env python3
"""Ports Blitz 4.8.0 FMOD samples the Godot client never had.

The Godot rebuild ships no guided rocket, so its sound bank carries nothing
for the XM81 launcher of the T49.  The Blitz pack does: ``Sfx/guns_tow.fsb``
holds the launch report, ``Sfx/guns_tracer.fsb`` the looped rocket bed and
``Sfx/hits_tow.fsb`` the rocket impacts.  Those banks are FSB5 containers with
IMA ADPCM subsongs, which ``vgmstream-cli`` decodes to plain WAV.

This module is imported by ``port_godot_sounds.py`` and extends the sound
table it writes, so one run of that porter produces the whole of
``Resources/Data/tank_sounds.json``.  Set ``BLITZ_SFX_SKIP=1`` to leave the
Blitz sets out when the pack or the decoder is unavailable; the sets simply
disappear from the table then, which the sound tests catch.

vgmstream-cli is looked up in ``VGMSTREAM_CLI``, then on PATH, then in
``~/.local/opt/vgmstream``.  Get a build from
https://github.com/vgmstream/vgmstream/releases (``vgmstream-linux.zip``).
"""

import os
import shutil
import subprocess

PACK = os.environ.get(
    "BLITZ_PACK", "/home/smh01/Studio canaryProjects/BlitzPACKSold")
UNITY = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
AUDIO_OUT = os.path.join(UNITY, "Assets/Blitz/Resources/Audio")

# Resources-relative folder every extracted Blitz sample lands in, so a clip
# name still says where it came from.
PREFIX = "blitz"

# The samples this port needs, keyed by bank.
SAMPLES = {
    "guns_tow": (
        "gun_tow_01", "gun_tow_02",
        "gun_tow_inside_01", "gun_tow_inside_02",
    ),
    "guns_tracer": ("tracer_tow_01", "tracer_tow_02"),
    "hits_tow": (
        "hc_armor_pierce_tow_01",
        "he_armor_pierce_tow_01",
        "hit_inside_pierce_tow_01",
    ),
}


def enabled():
    return os.environ.get("BLITZ_SFX_SKIP", "") not in ("1", "true", "yes")


def decoder():
    """The vgmstream-cli binary, or None when it is not installed."""
    explicit = os.environ.get("VGMSTREAM_CLI", "")
    if explicit and os.path.isfile(explicit) and os.access(explicit, os.X_OK):
        return explicit

    found = shutil.which("vgmstream-cli")
    if found:
        return found

    local = os.path.expanduser("~/.local/opt/vgmstream/vgmstream-cli")
    return local if os.access(local, os.X_OK) else None


def clip_name(bank, sample):
    return "%s/%s/%s" % (PREFIX, bank, sample)


def extract(cli, bank, sample):
    """Decodes one named subsong and returns its Resources clip name."""
    name = clip_name(bank, sample)
    destination = os.path.join(AUDIO_OUT, name + ".wav")
    if os.path.isfile(destination):
        return name

    source = os.path.join(PACK, "Sfx", bank + ".fsb")
    if not os.path.isfile(source):
        raise SystemExit("missing Blitz sound bank: " + source)

    os.makedirs(os.path.dirname(destination), exist_ok=True)
    # -S 0 walks every subsong and ?n expands to the sample's own name, which
    # is the only way to address a subsong by name rather than by index.
    staging = os.path.join(AUDIO_OUT, PREFIX, bank, "?n.wav")
    subprocess.run(
        [cli, "-S", "0", "-o", staging, source],
        check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    # Only the wanted samples stay; the banks carry rounds this port never asks
    # for and a Resources folder loads everything it holds.
    keep = set(SAMPLES[bank])
    folder = os.path.join(AUDIO_OUT, PREFIX, bank)
    for entry in os.listdir(folder):
        stem, extension = os.path.splitext(entry)
        if extension == ".wav" and stem not in keep:
            os.remove(os.path.join(folder, entry))

    if not os.path.isfile(destination):
        raise SystemExit("bank %s carries no sample %s" % (bank, sample))

    return name


def pool(cli, bank, samples, volume=1.0):
    return {
        "volume": volume,
        "streams": [extract(cli, bank, sample) for sample in samples],
    }


def extend(table):
    """Adds the Blitz-only sets to the sound table built from Godot."""
    if not enabled():
        return table

    cli = decoder()
    if cli is None:
        raise SystemExit(
            "vgmstream-cli not found; install it or set BLITZ_SFX_SKIP=1 "
            "(see the module docstring)")

    # gun_effects.xml: shot_atgm plays guns/tow/tow_pc for the shooter and
    # tow_npc for everybody else. The pack has no distant tow report, so the
    # launcher borrows the heavy gun's distant layer.
    table["guns"]["shot_atgm"] = {
        "sound_near": pool(cli, "guns_tow", ("gun_tow_01", "gun_tow_02")),
        "sound_inside": pool(
            cli, "guns_tow", ("gun_tow_inside_01", "gun_tow_inside_02")),
        "sound_dist": table["guns"]["shot_huge"]["sound_dist"],
    }

    # The rocket loops its own tracer bed for the whole flight.
    table["sounds"]["shell_flight"] = pool(
        cli, "guns_tracer", ("tracer_tow_01", "tracer_tow_02"))

    # hits_tow: what a rocket sounds like when it lands.
    table["sounds"]["atgm_pen_heat"] = pool(
        cli, "hits_tow", ("hc_armor_pierce_tow_01",))
    table["sounds"]["atgm_pen_he"] = pool(
        cli, "hits_tow", ("he_armor_pierce_tow_01",))
    table["sounds"]["atgm_pen_inside"] = pool(
        cli, "hits_tow", ("hit_inside_pierce_tow_01",))
    return table


if __name__ == "__main__":
    print("run Tools/port_godot_sounds.py; this module only extends its table")
