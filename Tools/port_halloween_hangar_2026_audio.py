#!/usr/bin/env python3
"""Port the Wwise sounds of the WoT Blitz 11.21 Halloween hangar to Unity audio clips.

The Wwise side is pw-mods-tool's `Tools/prepare_halloween_hangar_audio.py`
(which builds an FMOD bank for the garage-map port of the same hangar),
unchanged where the two ports agree. The hangar's sound emitters (`sounds` of
`Assets/Blitz/Resources/HalloweenHangar2026/scene.json`) name Wwise events;
every event that plays on its own is read from the client's banks down to its
sounds:

1. the event banks and packages are unpacked from DVPL and dumped with wwiser;
2. each event is read: its Play action, the random/sequence container and its
   playlist, the actor-mixer chain, positioning, attenuation curves,
   user auxiliary sends, RTPCs (game parameters bound to the built-in
   distance and azimuth, time modulators) and the sources;
3. the sources are taken from the banks or packages (the larger copy: banks
   hold only the prefetch of streamed sounds) and decoded with vgmstream;
4. the Wwise data is mapped to an event description (see `map_event`):
   continuous delay/trigger-rate containers become scatterers, gapless
   containers looping playlists, distance curves automation on the distance
   to the listener, the azimuth low-pass automation on the direction, time
   modulators a looping timeline;
5. `unity_events` turns the descriptions into the playback spec
   `HalloweenHangarAudio` (C#) follows and encodes the clips as Ogg Vorbis.
   Wwise voice gains above 0 dB are baked into the clip (Unity's volume stops
   at 1); the convolution reverb is rendered as a wet loop of the dry loop
   that plays in sync with it;
6. the user auxiliary bus the scatterers send to (a Wwise RoomVerb) becomes
   the settings of the one AudioReverbZone the hangar runs (`read_reverb_zone`).

Outputs: `Assets/Blitz/Resources/HalloweenHangar2026/Audio/*.ogg` and
`.../audio.json` (spec, Wwise data, notes, provenance). Intermediate files
(unpacked banks, wwiser dump, decoded WAVs) go to `.ci-cache/`. Run with the
exporter's interpreter after Tools/port_halloween_hangar_2026.py:

    .venv-texture/bin/python Tools/port_halloween_hangar_2026_audio.py
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
import shutil
import struct
import subprocess
import sys
import wave
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

import numpy as np

import port_halloween_hangar_2026 as hangar

ROOT = hangar.ROOT
AUDIO = ROOT / '.ci-cache/halloween_hangar_2026_audio'
WWISE_DIR = AUDIO / 'Wwise'
MEDIA_DIR = AUDIO / 'Media'
BAKED_DIR = AUDIO / 'Baked'
CLIPS_DIR = hangar.OUTPUT / 'Audio'
DESCRIPTION = hangar.OUTPUT / 'audio.json'
RESOURCE_PREFIX = 'HalloweenHangar2026/Audio/'
SCENE = hangar.OUTPUT / 'scene.json'
CLIENT_BANKS = Path('files/packs/WwiseSound')
INIT_BANK = Path('from_apk/WwiseSound/Init.bnk')
EVENT_BANKS = ('event_halloween_2026', 'hangar_normal')
WWISER = Path(os.environ.get('WWISER', Path.home() / 'tools/wwiser/wwiser.py'))
VGMSTREAM = Path(os.environ.get('VGMSTREAM', Path.home() / '.local/opt/vgmstream/vgmstream-cli'))
FFMPEG = os.environ.get('FFMPEG', 'ffmpeg')
EVENT_FOLDER = 'HalloweenHangar2026'
# Ogg Vorbis quality of the clips (Unity re-encodes them on import) and the peak a baked clip may reach.
VORBIS_QUALITY = '6'
HEADROOM_DBFS = -0.5
EVENT_PREFIXES = ('play_ev_hlwn_2026_', 'play_hgr_amb_', 'play_hgr_obj_', 'play_')

# Wwise
GRAPH_NEW_SCALING = 72          # bank version from which dB curves use the linear [-1, 1] encoding
VOLUME_MIN_DB = -96.3
CURVE_NAMES = ('volume', 'gameAux', 'userAux', 'lowpass', 'highpass', 'spread', 'focus')
TRANSITIONS = {0: 'none', 1: 'crossfade-amp', 2: 'crossfade-power', 3: 'delay', 4: 'sample-accurate', 5: 'trigger-rate'}
NODE_PROPS = {'Volume', 'MakeUpGain', 'Pitch', 'LPF', 'HPF', 'InitialDelay', 'AttenuationID',
              'PositioningTypeBlend', 'UserAuxSendVolume0', 'Loop'}
RANGED_PROPS = {'Volume', 'MakeUpGain', 'Pitch', 'InitialDelay'}
GAIN_PROPS = ('Volume', 'MakeUpGain')
MODULATOR_PROPS = {'Scope', 'Envelope_StopPlayback', 'Time_Duration', 'Time_Loops', 'Time_PlaybackRate'}
MODULATOR_SCOPES = {0: 'voice', 1: 'note/event', 2: 'game object', 3: 'global'}
RTPC_TARGETS = {'Volume': 'gain', 'MakeUpGain': 'gain', 'Pitch': 'pitch', 'LPF': 'lowpass', 'HPF': 'highpass',
                'UserAuxSendVolume0': 'aux'}
BUILTIN_DISTANCE, BUILTIN_AZIMUTH = 1, 2
CONVOLUTION_REVERB = 'Wwise Convolution Reverb'
# AkConvolutionReverbFX plug-in parameter IDs targeted by RTPCs in these banks. wwiser has no names for
# plug-in parameters; in all four hangar instances 30 dips slightly and 31 rises from about -13 dB near the
# emitter to 0 dB at 30 m, which (with the parameter block order dry-then-wet) identifies them as dry/wet.
CONVOLUTION_PARAMS = {30: 'dry', 31: 'wet'}
CONVOLUTION_ALGORITHMS = {0: 'downmix'}
FX_BYPASS_ALL = 0x10

# Ranges of the playback: silence floor, the gain the description may put on an
# instrument before the rest goes to its track (both are summed again for Unity),
# Unity's pitch range and its filter cut-off range. Unity has no trigger delay limit.
FMOD_SILENCE_DB = -80.0
FMOD_GAIN_MAX_DB = 10.0
FMOD_PITCH_MAX = 24.0
FMOD_DELAY_MAX_MS = math.inf
FMOD_CUTOFF_RANGE = (10.0, 22000.0)
DISTANCE_WITHOUT_ATTENUATION = 1000.0

SAMPLES_PER_SEGMENT = 64
VOLUME_TOLERANCE_DB = 0.25
CUTOFF_TOLERANCE_OCTAVES = 0.04
CUTOFF_MAX_STEP = 1.3
EXTENT_TOLERANCE_DEG = 2.0
PITCH_TOLERANCE_ST = 0.02


class AudioError(RuntimeError):
    pass


def require(condition, message: str) -> None:
    if not condition:
        raise AudioError(message)


def fnv1(name: str) -> int:
    """Wwise's ID of a name: 32-bit FNV-1 of the lower-case name."""
    h = 2166136261
    for c in name.lower().encode('utf-8'):
        h = (h * 16777619) & 0xFFFFFFFF
        h ^= c
    return h


def short_name(event: str) -> str:
    for prefix in EVENT_PREFIXES:
        if event.startswith(prefix) and len(event) > len(prefix):
            return event[len(prefix):]
    return event


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


# ------------------------------------------------------------------ curves
def interpolate(ratio: float, a: float, b: float, shape: int) -> float:
    """AkInterpolation::InterpolateNoCheck (the shapes of Wwise graph points)."""
    r = ratio
    if shape == 0:      # Log3
        return (1.0 - r) ** 3 * (a - b) + b
    if shape == 1:      # Sine
        v1 = (1.5707964 * r) ** 2
        v3 = ((v1 * -0.00018363654 + 0.0083063254) * v1 - 0.16664828) * v1 + 0.9999966
        return v3 * (1.5707964 * r) * (b - a) + a
    if shape == 2:      # Log1
        return (r - 3.0) * r * 0.5 * (a - b) + a
    if shape == 3:      # InvSCurve
        if r > 0.5:
            v1 = 3.1415927 - 3.1415927 * r
            v2 = (v1 * v1 * -0.00009181827 + 0.0041531627) * (v1 * v1) - 0.083324142
            return (1.0 - (v2 * (v1 * v1) + 0.4999983) * v1) * (b - a) + a
        v1 = (3.1415927 * r) ** 2
        v2 = (v1 * -0.00009181827 + 0.0041531627) * v1 - 0.083324142
        return (v2 * v1 + 0.4999983) * (3.1415927 * r) * (b - a) + a
    if shape == 4:      # Linear
        return (b - a) * r + a
    if shape == 5:      # SCurve
        v1 = (3.1415927 * r) ** 2
        return (((v1 * 0.00048483399 - 0.01961384) * v1 + 0.24767479) * v1 + 0.00069670216) * (b - a) + a
    if shape == 6:      # Exp1
        return (r + 1.0) * r * 0.5 * (b - a) + a
    if shape == 7:      # SineRecip
        v1 = (1.5707964 * r) ** 2
        return (((v1 * -0.0012712094 + 0.04148775) * v1 - 0.49991244) * v1 + 0.99999332) * (a - b) + b
    if shape == 8:      # Exp3
        return r ** 3 * (b - a) + a
    if shape == 9:      # Constant
        return a
    raise AudioError(f'unknown Wwise interpolation {shape}')


def scale(value: float, scaling: int, version: int) -> float:
    """CAkConversionTable::ApplyCurveScaling for banks with the new scaling."""
    require(version >= GRAPH_NEW_SCALING, f'bank version {version} predates the supported curve scaling')
    if scaling == 0:
        return value
    if scaling == 2:    # dB stored as linear [-1, 1]
        v = min(max(value, -1.0), 1.0)
        return VOLUME_MIN_DB if v == -1.0 else 20.0 * math.log10(v + 1.0)
    if scaling == 3:
        return 10.0 ** (value / 20.0)
    if scaling == 4:
        return 10.0 ** (value * 0.050000001)
    raise AudioError(f'unknown Wwise curve scaling {scaling}')


def curve_value(curve: dict, x: float, version: int) -> float:
    """CAkConversionTable::Convert: the points hold the ends, the left point's shape the segment."""
    points = curve['points']
    require(points, 'empty Wwise curve')
    if len(points) == 1:
        y = points[0][1]
    else:
        y = points[-1][1]
        for i, (x0, y0, shape) in enumerate(points):
            if x0 >= x:
                y = y0
                break
            if i + 1 == len(points):
                break
            x1, y1, _ = points[i + 1]
            if x1 > x:
                y = interpolate((x - x0) / (x1 - x0), y0, y1, shape) if shape != 4 else \
                    (y1 - y0) * ((x - x0) / (x1 - x0)) + y0
                break
    return scale(y, curve['scaling'], version)


def lowpass_hz(value: float) -> float:
    """Wwise low-pass amount (0 = open, 100 = closed) as a cutoff on a log scale from 20 kHz to 20 Hz."""
    v = min(max(value, 0.0), 100.0)
    return min(max(20.0 * 10.0 ** ((100.0 - v) * 0.03), FMOD_CUTOFF_RANGE[0]), FMOD_CUTOFF_RANGE[1])


def highpass_hz(value: float) -> float:
    """Wwise high-pass amount (0 = open, 100 = closed) as a cutoff on a log scale from 20 Hz to 20 kHz."""
    v = min(max(value, 0.0), 100.0)
    return min(max(20.0 * 10.0 ** (v * 0.03), FMOD_CUTOFF_RANGE[0]), FMOD_CUTOFF_RANGE[1])


def clamp_db(value: float) -> float:
    return min(max(value, FMOD_SILENCE_DB), FMOD_GAIN_MAX_DB)


def grid(lo: float, hi: float, breaks) -> list[float]:
    """Every curve point in [lo, hi] plus SAMPLES_PER_SEGMENT samples per segment between them."""
    knots = sorted({lo, hi} | {b for b in breaks if lo < b < hi})
    xs = []
    for a, b in zip(knots, knots[1:]):
        xs.extend(a + (b - a) * k / SAMPLES_PER_SEGMENT for k in range(SAMPLES_PER_SEGMENT))
    xs.append(knots[-1])
    return xs


def simplify(xs: list[float], ys: list[float], tolerance: float) -> list[int]:
    """Indices of a Douglas-Peucker reduction with a vertical error bound."""
    n = len(xs)
    keep = [False] * n
    keep[0] = keep[-1] = True
    stack = [(0, n - 1)]
    while stack:
        a, b = stack.pop()
        if b - a < 2:
            continue
        worst, index = 0.0, -1
        for i in range(a + 1, b):
            t = (xs[i] - xs[a]) / (xs[b] - xs[a]) if xs[b] != xs[a] else 0.0
            err = abs(ys[i] - (ys[a] + (ys[b] - ys[a]) * t))
            if err > worst:
                worst, index = err, i
        if worst > tolerance:
            keep[index] = True
            stack += [(a, index), (index, b)]
    return [i for i in range(n) if keep[i]]


def sample_curve(fn, lo: float, hi: float, breaks, kind: str) -> list[list[float]]:
    """fn sampled on [lo, hi] and reduced for FMOD automation; kind: 'db', 'hz', 'deg' or 'st'."""
    xs = grid(lo, hi, breaks)
    values = [fn(x) for x in xs]
    if kind == 'db':
        values = [clamp_db(v) for v in values]
        kept = simplify(xs, values, VOLUME_TOLERANCE_DB)
        digits = 3
    elif kind == 'hz':
        logs = [math.log2(v) for v in values]
        kept = simplify(xs, logs, CUTOFF_TOLERANCE_OCTAVES)
        kept = simplify_ratio(kept, values)
        digits = 1
    elif kind == 'deg':
        kept = simplify(xs, values, EXTENT_TOLERANCE_DEG)
        digits = 2
    elif kind == 'st':
        kept = simplify(xs, values, PITCH_TOLERANCE_ST)
        digits = 4
    else:
        raise AudioError(f'unknown curve kind {kind}')
    return [[round(xs[i], 4), round(values[i], digits)] for i in kept]


def simplify_ratio(kept: list[int], values: list[float]) -> list[int]:
    """Adds samples back until adjacent cutoffs differ by at most CUTOFF_MAX_STEP (FMOD interpolates in Hz)."""
    out = [kept[0]]
    for b in kept[1:]:
        pending = [b]
        while pending:
            a, c = out[-1], pending[-1]
            ratio = max(values[a], values[c]) / min(values[a], values[c])
            if c - a >= 2 and ratio > CUTOFF_MAX_STEP:
                pending.append((a + c) // 2)
            else:
                out.append(pending.pop())
    return out


# ------------------------------------------------------------------ wwiser dump
_ENUM = re.compile(r'\[([^\[\]]+)\]\s*$')


def enum_of(field) -> tuple[int, str]:
    require(field is not None, 'missing enum field')
    match = _ENUM.search(field.get('valuefmt', ''))
    return int(field.get('value')), (match.group(1) if match else '')


def child(elem, name: str):
    found = elem.find(f"object[@name='{name}']")
    require(found is not None, f'{elem.get("name")}: missing {name}')
    return found


def number(elem, name: str, default=None, deep: bool = True) -> float:
    found = elem.find(f".//field[@name='{name}']" if deep else f"field[@name='{name}']")
    if found is None:
        require(default is not None, f'{elem.get("name")}: missing field {name}')
        return default
    return float(found.get('value'))


def prop_bundles(elem) -> tuple[dict, dict]:
    """Values and random ranges of the AkPropBundle children of elem, by property name."""
    props, ranged = {}, {}
    for bundle in elem.findall('object'):
        kind = bundle.get('name', '')
        if not kind.startswith('AkPropBundle<'):
            continue
        for item in bundle.findall('list/object'):
            pid, name = enum_of(item.find("field[@name='pID']"))
            require(name, f'unnamed Wwise property {pid}')
            if 'RANGED_MODIFIERS' in kind:
                ranged[name] = [number(item, 'min', deep=False), number(item, 'max', deep=False)]
            else:
                props[name] = number(item, 'pValue', deep=False)
    return props, ranged


def graph_points(elem) -> list[list[float]]:
    return [[number(p, 'From', deep=False), number(p, 'To', deep=False), int(number(p, 'Interp', deep=False))]
            for p in elem.findall("list/object[@name='AkRTPCGraphPoint']")]


def read_rtpcs(elem) -> list[dict]:
    block = elem.find("object[@name='InitialRTPC']")
    out = []
    if block is None:
        return out
    for item in block.findall("list/object[@name='RTPC']"):
        source, source_name = enum_of(item.find("field[@name='rtpcType']"))
        accum, accum_name = enum_of(item.find("field[@name='rtpcAccum']"))
        param, param_name = enum_of(item.find("field[@name='ParamID']"))
        scaling, _ = enum_of(item.find("field[@name='eScaling']"))
        out.append({'id': int(number(item, 'RTPCID', deep=False)), 'source': source_name or str(source),
                    'accumulation': accum_name or str(accum), 'param': param_name or str(param), 'paramId': param,
                    'scaling': scaling, 'points': graph_points(item)})
    return out


class Dump:
    """The CAk objects and game parameters of a wwiser XML dump."""

    def __init__(self, text: str):
        self.objects: dict[int, tuple[str, str, ET.Element]] = {}
        self.game_parameters: dict[int, dict] = {}
        self.versions: set[int] = set()
        roots = re.findall(r'<root .*?</root>', text, re.S)
        require(roots, 'the wwiser dump holds no banks')
        for chunk in roots:
            root = ET.fromstring(chunk)
            bank = root.get('filename')
            self.versions.add(int(root.get('version')))
            for obj in root.iter('object'):
                name = obj.get('name', '')
                if name == 'RTPCRamping':
                    rid = obj.find("field[@name='RTPC_ID']")
                    builtin, builtin_name = enum_of(obj.find("field[@name='eBindToBuiltInParam']"))
                    self.game_parameters[int(rid.get('value'))] = {
                        'name': rid.get('hashname', ''), 'default': number(obj, 'fValue', deep=False),
                        'builtin': builtin, 'builtinName': builtin_name}
                    continue
                if not name.startswith('CAk'):
                    continue
                uid = obj.find("field[@name='ulID']")
                if uid is not None:
                    self.objects.setdefault(int(uid.get('value')), (bank, name, obj))
        require(len(self.versions) == 1, f'mixed bank versions {sorted(self.versions)}')
        self.version = self.versions.pop()

    def get(self, oid: int, kinds: tuple[str, ...], what: str):
        require(oid in self.objects, f'{what}: Wwise object {oid} is not in the banks')
        bank, kind, obj = self.objects[oid]
        require(kind in kinds, f'{what}: Wwise object {oid} is a {kind}, expected {"/".join(kinds)}')
        return bank, kind, obj


def read_fx_chunks(elem) -> dict:
    chunks = [{'index': int(number(c, 'uFXIndex', deep=False)), 'id': int(number(c, 'fxID', deep=False)),
               'shareSet': int(number(c, 'bIsShareSet', deep=False)), 'rendered': int(number(c, 'bIsRendered', deep=False))}
              for c in elem.findall("list/object[@name='FXChunk']")]
    require(len(chunks) == int(number(elem, 'uNumFx', deep=False)), 'FX chunk count mismatch')
    return {'override': int(number(elem, 'bIsOverrideParentFX', deep=False)),
            'bypass': int(number(elem, 'bitsFXBypass', 0, deep=False)), 'chunks': chunks}


def read_effect(dump: Dump, fid: int) -> dict:
    _, kind, obj = dump.get(fid, ('CAkFxCustom', 'CAkFxShareSet'), 'effect')
    init = child(obj, 'FxBaseInitialValues')
    plugin, plugin_name = enum_of(init.find("field[@name='fxID']"))
    block = next((o for o in init.findall('object') if o.get('name', '').endswith('FXParams')), None)
    params = {f.get('name'): float(f.get('value')) for f in block.findall('field')} if block is not None else {}
    values = {int(number(v, 'propertyId', deep=False)): number(v, 'fValue', deep=False)
              for v in init.findall("list[@name='propertyValues']/object[@name='PluginPropertyValue']")}
    state = child(init, 'StateChunk')
    return {'id': fid, 'kind': kind, 'plugin': plugin_name or f'{plugin:#010x}', 'params': params,
            'media': [int(number(m, 'sourceId', deep=False)) for m in init.findall("list[@name='media']/object")],
            'rtpcs': read_rtpcs(init), 'propertyValues': values,
            'stateGroups': int(number(state, 'ulNumStateGroups', 0)) + int(number(state, 'ulNumStateProps', 0))}


# RoomVerb parameters the zone has no counterpart for: algorithm tunings of the
# reverb units, surround-only levels and delays, the DC filter and the width.
ROOMVERB_UNPORTED = ('fStereoWidth', 'fRoomSize', 'fRoomShape', 'uERPattern', 'fERFrontBackDelay', 'uNumReverbUnits',
                     'fFrontLevel', 'fRearLevel', 'fCenterLevel', 'fLFELevel', 'fInputCenterLevel', 'fInputLFELevel',
                     'fDensityDelayMin', 'fDensityDelayMax', 'fDensityDelayRdmPerc', 'fRoomShapeMin', 'fRoomShapeMax',
                     'fDiffusionDelayScalePerc', 'fDiffusionDelayMax', 'fDiffusionDelayRdmPerc', 'fDCFilterCutFreq',
                     'fReverbUnitInputDelay', 'fReverbUnitInputDelayRmdPerc')
BUS_GAIN_PROPS = ('Volume', 'BusVolume', 'MakeUpGain')


def read_reverb_zone(dump: Dump, bus_id: int) -> dict:
    """The user auxiliary bus the hangar's scatterers send to, as AudioReverbZone settings.

    The bus runs one Wwise RoomVerb at 100 % wet (dry level -96.3 dB): a
    reverb zone is wet only as well, the direct sound bypasses it. Levels are
    millibels like Unity's, the late reverb starts ReverbDelay after the dry
    sound and the early reflections right away (the ER pattern timings are
    RoomVerb internals). Every source keeps its own send (reverbZoneMix)."""
    _, _, bus = dump.get(bus_id, ('CAkAuxBus',), 'reverb bus')
    values = child(bus, 'BusInitialValues')
    props, ranged = prop_bundles(child(values, 'BusInitialParams'))
    require(not ranged, f'reverb bus {bus_id}: randomised properties {sorted(ranged)}')
    unknown = set(props) - set(BUS_GAIN_PROPS) - {'CenterPCT'}
    require(not unknown, f'reverb bus {bus_id}: unsupported properties {sorted(unknown)}')
    gain = sum(props.get(name, 0.0) for name in BUS_GAIN_PROPS)
    for name in ('pRTPCMgr', 'stateProps', 'pStateChunks', 'ToDuckList'):
        found = values.find(f".//list[@name='{name}']")
        require(found is None or found.get('count') == '0', f'reverb bus {bus_id}: {name} is not empty')
    fx = child(values, 'BusInitialFxParams')
    chunks = fx.findall("list/object[@name='FXChunk']")
    require(len(chunks) == 1, f'reverb bus {bus_id}: {len(chunks)} effects, expected the one reverb')
    require(int(number(fx, 'bitsFXBypass', 0, deep=False)) == 0, f'reverb bus {bus_id}: the reverb is bypassed')
    fid = int(number(chunks[0], 'fxID', deep=False))
    effect = read_effect(dump, fid)
    require(effect['plugin'] == 'Wwise RoomVerb', f'reverb bus {bus_id}: {effect["plugin"]} instead of a RoomVerb')
    require(not effect['rtpcs'] and not effect['stateGroups'], f'reverb {fid}: RTPCs or states on the reverb')
    _, _, obj = dump.get(fid, ('CAkFxCustom', 'CAkFxShareSet'), 'reverb effect')
    init = child(obj, 'FxBaseInitialValues')
    block = next((o for o in init.findall('object') if o.get('name', '').endswith('FXParams')), None)
    require(block is not None, f'reverb {fid}: no parameter block')
    p = {f.get('name'): float(f.get('value')) for f in block.iter('field')}
    needed = ('fDecayTime', 'fHFDamping', 'fDiffusion', 'fDensity', 'fERLevel', 'fReverbLevel', 'fDryLevel',
              'fReverbDelay', 'bEnableEarlyReflections', 'bEnableToneControls')
    missing = [k for k in needed if k not in p]
    require(not missing, f'reverb {fid}: missing RoomVerb parameters {missing}')
    require(p['fDryLevel'] <= -96.0, f'reverb {fid}: dry level {p["fDryLevel"]:g} dB on an aux bus')
    require(not p['bEnableToneControls'], f'reverb {fid}: RoomVerb tone controls are not supported')

    def mb(db: float, lo: int, hi: int) -> int:
        return int(max(lo, min(hi, round(db * 100.0))))

    unity = {
        'room': mb(gain, -10000, 0), 'roomHF': 0, 'roomLF': 0,
        'decayTime': round(max(0.1, min(20.0, p['fDecayTime'])), 4),
        'decayHFRatio': round(max(0.1, min(2.0, 1.0 / max(p['fHFDamping'], 1e-3))), 4),
        'reflections': mb(p['fERLevel'], -10000, 1000) if p['bEnableEarlyReflections'] else -10000,
        'reflectionsDelay': 0.0,
        'reverb': mb(p['fReverbLevel'], -10000, 2000),
        'reverbDelay': round(max(0.0, min(0.1, p['fReverbDelay'] / 1000.0)), 4),
        'hfReference': 5000.0, 'lfReference': 250.0,
        'diffusion': round(max(0.0, min(100.0, p['fDiffusion'])), 3),
        'density': round(max(0.0, min(100.0, p['fDensity'])), 3),
    }
    notes = [f'Wwise RoomVerb {fid} on aux bus {bus_id} -> AudioReverbZone',
             f'HF damping {p["fHFDamping"]:g} -> decay HF ratio {unity["decayHFRatio"]:g} at {unity["hfReference"]:g} Hz',
             f'bus gain {gain:+g} dB -> room {unity["room"]} mB; CenterPCT only matters for surround output',
             'tone controls are off in the bank (RoomVerb filters 1-3 inactive)',
             'not reproduced (RoomVerb internals or surround only): ' +
             ', '.join(f'{k} {p[k]:g}' for k in ROOMVERB_UNPORTED if k in p)]
    return {'bus': bus_id, 'effect': fid, 'plugin': effect['plugin'],
            'wwise': {k: p[k] for k in sorted(p)}, 'unity': unity, 'notes': notes}


def read_node(dump: Dump, oid: int) -> dict:
    bank, kind, obj = dump.get(oid, ('CAkSound', 'CAkRanSeqCntr', 'CAkActorMixer', 'CAkLayerCntr', 'CAkSwitchCntr'), 'node')
    base = obj.find(".//object[@name='NodeBaseParams']")
    require(base is not None, f'{kind} {oid}: no NodeBaseParams')
    props, ranged = prop_bundles(child(base, 'NodeInitialParams'))
    pos = child(base, 'PositioningParams')
    positioning = {
        'override': int(number(pos, 'bPositioningInfoOverrideParent')),
        'routing': int(number(pos, 'bHasListenerRelativeRouting')),
        'positionType': int(number(pos, 'e3DPositionType', 0)),
        'spatialization': int(number(pos, 'eSpatializationMode', 0)),
        'attenuation': int(number(pos, 'bEnableAttenuation', 0)),
    }
    aux = child(base, 'AuxParams')
    node = {
        'id': oid, 'kind': kind, 'bank': bank,
        'parent': int(number(base, 'DirectParentID', deep=False)),
        'bus': int(number(base, 'OverrideBusId', deep=False)),
        'fx': read_fx_chunks(child(base, 'NodeInitialFxParams')),
        'props': props, 'ranged': ranged, 'positioning': positioning,
        'aux': {'override': int(number(aux, 'bOverrideUserAuxSends')), 'hasAux': int(number(aux, 'bHasAux')),
                'buses': [int(f.get('value')) for f in aux.findall("field[@name='auxID']") if int(f.get('value'))]},
        'stateGroups': int(number(child(base, 'StateChunk'), 'ulNumStateGroups', 0)),
        'rtpcs': read_rtpcs(base),
    }
    if kind == 'CAkSound':
        src = obj.find(".//object[@name='AkBankSourceData']")
        plugin = src.find("field[@name='ulPluginID']")
        plugin_type, plugin_type_name = enum_of(plugin.find("field[@name='type']"))
        stream, stream_name = enum_of(src.find("field[@name='StreamType']"))
        node['source'] = {'plugin': enum_of(plugin)[1], 'pluginType': plugin_type_name or str(plugin_type),
                          'stream': stream_name or str(stream), 'id': int(number(src, 'sourceID')),
                          'inMemory': int(number(src, 'uInMemoryMediaSize'))}
        require(plugin_type == 1, f'sound {oid}: source plugin {node["source"]["plugin"]} is not a codec')
    if kind in ('CAkRanSeqCntr', 'CAkActorMixer', 'CAkLayerCntr', 'CAkSwitchCntr'):
        children = obj.find(".//object[@name='Children']")
        node['children'] = [int(f.get('value')) for f in children.findall("field[@name='ulChildID']")] \
            if children is not None else []
    if kind == 'CAkRanSeqCntr':
        init = child(obj, 'RanSeqCntrInitialValues')
        node['container'] = {
            'loopCount': int(number(init, 'sLoopCount')), 'loopModMin': int(number(init, 'sLoopModMin')),
            'loopModMax': int(number(init, 'sLoopModMax')),
            'transitionMs': number(init, 'fTransitionTime'), 'transitionModMinMs': number(init, 'fTransitionTimeModMin'),
            'transitionModMaxMs': number(init, 'fTransitionTimeModMax'),
            'avoidRepeat': int(number(init, 'wAvoidRepeatCount')),
            'transition': int(number(init, 'eTransitionMode')), 'randomMode': int(number(init, 'eRandomMode')),
            'mode': int(number(init, 'eMode')), 'usingWeight': int(number(init, '_bIsUsingWeight')),
            'continuous': int(number(init, 'bIsContinuous')),
        }
        node['playlist'] = [[int(number(i, 'ulPlayID', deep=False)), int(number(i, 'weight', deep=False))]
                            for i in obj.iter('object') if i.get('name') == 'AkPlaylistItem']
    return node


def read_attenuation(dump: Dump, aid: int) -> dict:
    _, _, obj = dump.get(aid, ('CAkAttenuation',), 'attenuation')
    init = child(obj, 'AttenuationInitialValues')
    tables = [{'scaling': enum_of(t.find("field[@name='eScaling']"))[0], 'points': graph_points(t)}
              for t in init.findall("list[@name='curves']/object[@name='CAkConversionTable']")]
    curves = {}
    for i, name in enumerate(CURVE_NAMES):
        index = int(number(init, f'curveToUse[{i}]', deep=False))
        if index >= 0:
            require(index < len(tables), f'attenuation {aid}: curve {name} uses missing table {index}')
            curves[name] = tables[index]
    return {'id': aid, 'cone': int(number(init, 'bIsConeEnabled', deep=False)), 'curves': curves,
            'rtpcs': read_rtpcs(init)}


def read_modulator(dump: Dump, mid: int) -> dict:
    _, kind, obj = dump.get(mid, ('CAkTimeModulator', 'CAkLFOModulator', 'CAkEnvelopeModulator'), 'modulator')
    require(kind == 'CAkTimeModulator', f'modulator {mid} is a {kind}; only time modulators are ported')
    init = child(obj, 'ModulatorInitialValues')
    props, ranged = prop_bundles(init)
    unknown = (set(props) | set(ranged)) - MODULATOR_PROPS
    require(not unknown, f'time modulator {mid}: unsupported properties {sorted(unknown)}')
    require(not read_rtpcs(init), f'time modulator {mid}: RTPCs on modulators are not supported')
    lo, hi = ranged.get('Time_PlaybackRate', [0.0, 0.0])
    base = props.get('Time_PlaybackRate', 1.0)
    return {'id': mid, 'duration': props.get('Time_Duration', 1.0), 'loops': int(props.get('Time_Loops', 1)),
            'rate': base, 'rateRange': [base + lo, base + hi], 'meanRate': base + (lo + hi) / 2.0,
            'scope': MODULATOR_SCOPES.get(int(props.get('Scope', 0)), str(props.get('Scope'))),
            'stopPlayback': int(props.get('Envelope_StopPlayback', 0))}


def read_event(dump: Dump, name: str) -> dict:
    """The Play action of a Wwise event and every object it reaches."""
    eid = fnv1(name)
    bank, _, obj = dump.get(eid, ('CAkEvent',), name)
    actions = [int(f.get('value')) for f in obj.iter('field') if f.get('name') == 'ulActionID']
    require(len(actions) == 1, f'{name}: {len(actions)} actions; one Play action is supported')
    _, kind, act = dump.get(actions[0], ('CAkActionPlay',), f'{name} action')
    init = child(act, 'ActionInitialValues')
    require(not int(number(init, 'bIsBus')), f'{name}: the Play action targets a bus')
    props, ranged = prop_bundles(init)
    unknown = (set(props) - {'TransitionTime'}) | set(ranged)
    require(not unknown, f'{name}: unsupported Play action properties {sorted(unknown)}')
    target = int(number(init, 'idExt', deep=False))
    nodes, chain = {}, []
    oid = target
    while oid:
        require(oid not in nodes, f'{name}: cyclic Wwise hierarchy at {oid}')
        nodes[oid] = read_node(dump, oid)
        chain.append(oid)
        oid = nodes[oid]['parent']
    top = nodes[target]
    if top['kind'] == 'CAkSound':
        entries = [target]
    else:
        require(top['kind'] == 'CAkRanSeqCntr', f'{name}: the Play action targets a {top["kind"]}')
        entries = [item for item, _ in top['playlist']]
        require(entries and set(entries) <= set(top['children']),
                f'{name}: the playlist of container {target} does not list its children')
        for sid in entries:
            if sid not in nodes:
                nodes[sid] = read_node(dump, sid)
            require(nodes[sid]['kind'] == 'CAkSound', f'{name}: nested containers are not supported ({sid})')
    attenuations, modulators, effects = {}, {}, {}
    for node in nodes.values():
        for chunk in node['fx']['chunks']:
            if chunk['id'] not in effects:
                effects[chunk['id']] = read_effect(dump, chunk['id'])
        aid = int(node['props'].get('AttenuationID', 0))
        if aid and aid not in attenuations:
            attenuations[aid] = read_attenuation(dump, aid)
        for rtpc in node['rtpcs']:
            if rtpc['source'] == 'Modulator' and rtpc['id'] not in modulators:
                modulators[rtpc['id']] = read_modulator(dump, rtpc['id'])
    parameters = {r['id']: dump.game_parameters.get(r['id'])
                  for holder in [*nodes.values(), *effects.values()]
                  for r in holder['rtpcs'] if r['source'] == 'GameParameter'}
    missing = [rid for rid, gp in parameters.items() if gp is None]
    require(not missing, f'{name}: game parameters {missing} are not defined in Init.bnk')
    return {'event': name, 'id': eid, 'bank': bank, 'version': dump.version,
            'action': {'id': actions[0], 'kind': kind, 'target': target,
                       'fadeInMs': props.get('TransitionTime', 0.0),
                       'fadeCurve': enum_of(init.find(".//field[@name='eFadeCurve']"))[1]},
            'chain': chain, 'entries': entries, 'nodes': {str(k): v for k, v in nodes.items()},
            'attenuations': {str(k): v for k, v in attenuations.items()},
            'modulators': {str(k): v for k, v in modulators.items()},
            'effects': {str(k): v for k, v in effects.items()},
            'gameParameters': {str(k): v for k, v in parameters.items()}}


# ------------------------------------------------------------------ Wwise -> FMOD
def prop_sum(nodes, names) -> float:
    return sum(n['props'].get(p, 0.0) for n in nodes for p in names)


def ranged_sum(nodes, names) -> tuple[float, float]:
    lo = sum(n['ranged'].get(p, [0.0, 0.0])[0] for n in nodes for p in names)
    hi = sum(n['ranged'].get(p, [0.0, 0.0])[1] for n in nodes for p in names)
    return lo, hi


def deciding(path: list[dict], flag) -> dict:
    """The node whose settings apply: the nearest that overrides its parent, else the top of the chain."""
    for node in path:
        if flag(node):
            return node
    return path[-1]


def convolution_levels(name: str, effect: dict, notes: list[str]) -> dict:
    """Checks a Wwise Convolution Reverb against what the port reproduces; returns its static levels."""
    p = effect['params']
    algorithm = int(p['eAlgoType'])
    require(algorithm in CONVOLUTION_ALGORITHMS, f'{name}: convolution algorithm {algorithm} is not supported')
    require(not effect['stateGroups'], f'{name}: states on effect {effect["id"]} are not supported')
    width = p.get('fInputStereoWidth', 0.0)
    require(width in (0.0, 180.0), f'{name}: convolution input stereo width {width:g} is not supported')
    require(p['fStereoWidth'] == 180.0, f'{name}: convolution output stereo width {p["fStereoWidth"]:g} is not supported')
    for pid, value in effect['propertyValues'].items():
        key = CONVOLUTION_PARAMS.get(int(pid))  # int keys from read_effect, str keys once read back from audio.json
        require(key, f'{name}: effect {effect["id"]} sets unknown plug-in property {pid}')
        block = p['fDryLevel'] if key == 'dry' else p['fWetLevel']
        require(value == 0.0 or abs(value - block) < 1e-6,
                f'{name}: effect {effect["id"]} property {pid} = {value:g} differs from the parameter block ({block:g})')
    surround = {k: p.get(k, 0.0) for k in ('fRearLevel', 'fCenterLevel', 'fLFELevel', 'fFrontRearDelay')}
    notes.append('convolution reverb: rear/centre/LFE output levels and front-rear delay ('
                 + ', '.join(f'{k[1:]} {v:g}' for k, v in surround.items())
                 + ') only shape surround output and are ignored; input centre/LFE levels only apply to 5.1 sources')
    if p.get('unknown?') is not None:
        notes.append(f'convolution reverb: input threshold {p["unknown?"]:g} dB (CPU saving) not reproduced')
    notes.append(f'convolution reverb: pre-delay {p["fPreDelay"]:g} ms prepended to the impulse response; '
                 f'input stereo width {width:g} -> linked input channels {width == 0.0}')
    return {'wet': p['fWetLevel'] + p['fFrontLevel'], 'dry': p['fDryLevel'], 'link': width == 0.0}


def map_event(w: dict, media: dict[int, dict], impulses: dict[int, dict]) -> dict:
    """The playback description of one Wwise event read by read_event; media maps source IDs to decoded files
    ({'file', 'duration'}), impulses effect IDs to impulse responses. Returns {'fmod': ..., 'notes': [...]}."""
    version = w['version']
    nodes = {int(k): v for k, v in w['nodes'].items()}
    attenuations = {int(k): v for k, v in w['attenuations'].items()}
    modulators = {int(k): v for k, v in w['modulators'].items()}
    effects = {int(k): v for k, v in w.get('effects', {}).items()}
    parameters = {int(k): v for k, v in w['gameParameters'].items()}
    name = short_name(w['event'])
    notes: list[str] = []
    target = nodes[w['target'] if 'target' in w else w['action']['target']]
    entries = [nodes[i] for i in w['entries']]
    ancestors = [nodes[i] for i in w['chain'][1:]]
    container = target if target['kind'] == 'CAkRanSeqCntr' else None
    common = ([container] if container else []) + ancestors

    for node in common + entries:
        unknown = set(node['props']) - NODE_PROPS
        require(not unknown, f'{name}: unsupported Wwise properties {sorted(unknown)} on {node["kind"]} {node["id"]}')
        unknown = set(node['ranged']) - RANGED_PROPS
        require(not unknown, f'{name}: unsupported random ranges {sorted(unknown)} on {node["kind"]} {node["id"]}')
        if node['stateGroups']:
            notes.append(f'{node["kind"]} {node["id"]}: {node["stateGroups"]} state groups ignored (the client hangar sets no Wwise states)')
    for node in entries:
        require(not node['ranged'], f'{name}: random ranges on sound {node["id"]} are not supported')
        require(not node['rtpcs'] or len(entries) == 1, f'{name}: RTPCs on one of several sounds ({node["id"]}) are not supported')
    for prop in ('LPF', 'HPF'):
        require(len({e['props'].get(prop, 0.0) for e in entries}) == 1, f'{name}: the sounds differ in {prop}')
    # effects: the nearest node that overrides its parent's effects (else the top node) supplies them
    owners = {deciding([e] + common, lambda n: n['fx']['override'])['id'] for e in entries}
    require(len(owners) == 1, f'{name}: the sounds take their effects from different nodes {sorted(owners)}')
    fx_node = nodes[owners.pop()]
    for node in common + entries:
        if node is not fx_node and node['fx']['chunks']:
            notes.append(f'{node["kind"]} {node["id"]}: effects ignored (Wwise uses those of {fx_node["id"]})')
    convolution = None
    for chunk in sorted(fx_node['fx']['chunks'], key=lambda c: c['index']):
        effect = effects[chunk['id']]
        if fx_node['fx']['bypass'] & (FX_BYPASS_ALL | 1 << chunk['index']):
            notes.append(f'{effect["plugin"]} {chunk["id"]} is bypassed in Wwise; not ported')
            continue
        if chunk['rendered']:
            notes.append(f'{effect["plugin"]} {chunk["id"]} was rendered into the media by Wwise')
            continue
        require(effect['plugin'] == CONVOLUTION_REVERB,
                f'{name}: {effect["plugin"]} effect {chunk["id"]} on {fx_node["kind"]} {fx_node["id"]} is not supported')
        require(convolution is None, f'{name}: several convolution reverbs')
        require(chunk['id'] in impulses, f'{name}: the impulse response of effect {chunk["id"]} was not extracted')
        require(all(media[e['source']['id']]['channels'] <= 2 for e in entries),
                f'{name}: convolution of multichannel sources is not supported')
        convolution = {'effect': effect, **convolution_levels(name, effect, notes)}
        notes.append(f'{effect["plugin"]} {chunk["id"]} on {fx_node["kind"]} {fx_node["id"]} -> pre-rendered convolution loop '
                     f'({impulses[chunk["id"]]["file"]})')

    bus = next((n['bus'] for n in [target] + ancestors if n['bus']), 0)
    notes.append(f'Wwise output bus {bus} -> the SFX bus')

    # positioning (the nearest node that overrides its parent supplies the attenuation and the blend)
    deciders = {deciding([e] + common, lambda n: n['positioning']['override'])['id'] for e in entries}
    require(len(deciders) == 1, f'{name}: the sounds take their positioning from different nodes {sorted(deciders)}')
    pos_node = nodes[deciders.pop()]
    pos = pos_node['positioning']
    require(not pos['routing'] or pos['positionType'] == 0, f'{name}: only emitter positioning is supported')
    pan = bool(pos['routing'] and pos['spatialization'])
    att_id = int(pos_node['props'].get('AttenuationID', 0)) if pos['routing'] and pos['attenuation'] else 0
    attenuation = attenuations.get(att_id) if att_id else None
    require(not att_id or attenuation, f'{name}: attenuation {att_id} was not read')
    blend = pos_node['props'].get('PositioningTypeBlend', 100.0)
    if pos_node is not container and container and container['props'].get('AttenuationID'):
        notes.append(f'positioning of {pos_node["kind"]} {pos_node["id"]} overrides the container: '
                     f'attenuation {att_id or "none"} instead of {int(container["props"]["AttenuationID"])}, '
                     f'3D blend {blend:g}%')
    if attenuation:
        require(not attenuation['cone'], f'{name}: attenuation cones are not supported')
        require(not attenuation['rtpcs'], f'{name}: RTPCs on attenuation {att_id} are not supported')
        if 'focus' in attenuation['curves']:
            notes.append('attenuation focus curve ignored (Unity has no focus)')
        if 'gameAux' in attenuation['curves']:
            notes.append('attenuation game-defined aux curve ignored (the hangar has no game-defined sends)')

    # user auxiliary sends (reverb)
    aux_path = deciding([entries[0]] + common, lambda n: n['aux']['override'])
    aux_buses = set(aux_path['aux']['buses']) if aux_path['aux']['hasAux'] else set()
    require(len(aux_buses) <= 1, f'{name}: several user aux sends {sorted(aux_buses)}')
    path = [entries[0]] + common
    aux_nodes = path[:path.index(aux_path) + 1]

    # RTPCs
    by_domain: dict[str, list[tuple[str, dict]]] = {'distance': [], 'direction': [], 'time': []}
    constants = {'gain': 0.0, 'pitch': 0.0, 'lowpass': 0.0, 'highpass': 0.0, 'aux': 0.0}
    for node in common + entries[:1]:
        for r in node['rtpcs']:
            target_name = RTPC_TARGETS.get(r['param'])
            require(target_name, f'{name}: RTPC on {r["param"]} of {node["kind"]} {node["id"]} is not supported')
            require(r['accumulation'] in ('Additive', '2'), f'{name}: {r["accumulation"]} RTPC on {r["param"]} is not supported')
            if target_name == 'aux' and (node not in aux_nodes or not aux_buses):
                notes.append(f'RTPC {r["id"]} on {r["param"]} of {node["id"]} ignored (no user aux send there)')
                continue
            curve = {'scaling': r['scaling'], 'points': r['points']}
            if r['source'] == 'GameParameter':
                gp = parameters[r['id']]
                label = gp['name'] or str(r['id'])
                if gp['builtin'] == BUILTIN_DISTANCE:
                    by_domain['distance'].append((target_name, curve))
                    notes.append(f'game parameter {label} (built-in distance) on {r["param"]} -> distance automation')
                elif gp['builtin'] == BUILTIN_AZIMUTH:
                    require(target_name == 'lowpass', f'{name}: azimuth RTPC on {r["param"]} is not supported')
                    by_domain['direction'].append((target_name, curve))
                    notes.append(f'game parameter {label} (built-in azimuth) on {r["param"]} -> direction automation')
                elif gp['builtin'] == 0:
                    value = curve_value(curve, gp['default'], version)
                    constants[target_name] += value
                    notes.append(f'game parameter {label} on {r["param"]} held at its default {gp["default"]:g} ({value:+.3g})')
                else:
                    raise AudioError(f'{name}: game parameter {label} bound to built-in {gp["builtinName"]} is not supported')
            elif r['source'] == 'Modulator':
                require(target_name in ('gain', 'pitch'), f'{name}: time modulator on {r["param"]} is not supported')
                by_domain['time'].append((target_name, curve, modulators[r['id']]))
            else:
                raise AudioError(f'{name}: {r["source"]} RTPCs are not supported')

    if convolution:
        for r in convolution['effect']['rtpcs']:
            key = CONVOLUTION_PARAMS.get(r['paramId'])
            require(key, f'{name}: RTPC on convolution parameter {r["paramId"]} is not supported')
            require(r['accumulation'] in ('Additive', '2'), f'{name}: {r["accumulation"]} effect RTPC is not supported')
            gp = parameters.get(r['id']) if r['source'] == 'GameParameter' else None
            require(gp and gp['builtin'] == BUILTIN_DISTANCE,
                    f'{name}: convolution {key} RTPC {r["id"]} is not driven by the built-in distance')
            by_domain['distance'].append(('conv-' + key, {'scaling': r['scaling'], 'points': r['points']}))
            notes.append(f'convolution {key} level (plug-in parameter {r["paramId"]}) on built-in distance -> distance automation')

    # static values
    gain = prop_sum(common, GAIN_PROPS) + constants['gain']
    gain_lo, gain_hi = ranged_sum(common, GAIN_PROPS)
    pitch = prop_sum(common, ('Pitch',)) + constants['pitch']
    pitch_lo, pitch_hi = ranged_sum(common, ('Pitch',))
    delay = prop_sum(common, ('InitialDelay',))
    delay_lo, delay_hi = ranged_sum(common, ('InitialDelay',))
    lowpass = prop_sum(common + entries[:1], ('LPF',)) + constants['lowpass']
    highpass = prop_sum(common + entries[:1], ('HPF',)) + constants['highpass']
    aux_level = prop_sum(aux_nodes, ('UserAuxSendVolume0',)) + constants['aux']

    fmod: dict = {'name': name, 'path': f'event:/{EVENT_FOLDER}/{name}', 'wwise': w['event']}

    # distance-driven automation
    curves = attenuation['curves'] if attenuation else {}
    distance_curves = [c for _, c in by_domain['distance']] + list(curves.values())
    if distance_curves:
        dmax = max(c['points'][-1][0] for c in distance_curves)
        require(dmax > 0, f'{name}: distance curves end at {dmax}')
        breaks = [p[0] for c in distance_curves for p in c['points']]

        def total(kind_name: str, att_curve: str | None, base: float):
            parts = [c for t, c in by_domain['distance'] if t == kind_name]
            if att_curve and att_curve in curves:
                parts.append(curves[att_curve])
            if not parts:
                return None
            return lambda x: base + sum(curve_value(c, x, version) for c in parts)

        distance: dict = {'max': round(dmax, 4)}
        volume_fn = total('gain', 'volume', 0.0)
        if volume_fn:
            distance['volume'] = sample_curve(volume_fn, 0.0, dmax, breaks, 'db')
        lowpass_fn = total('lowpass', 'lowpass', lowpass)
        if lowpass_fn:
            distance['lowpass'] = sample_curve(lambda x: lowpass_hz(lowpass_fn(x)), 0.0, dmax, breaks, 'hz')
        highpass_fn = total('highpass', 'highpass', highpass)
        if highpass_fn:
            distance['highpass'] = sample_curve(lambda x: highpass_hz(highpass_fn(x)), 0.0, dmax, breaks, 'hz')
        if aux_buses:
            aux_fn = total('aux', 'userAux', aux_level)
            if aux_fn:
                distance['reverb'] = sample_curve(aux_fn, 0.0, dmax, breaks, 'db')
        if convolution:
            for key in ('wet', 'dry'):
                level_fn = total('conv-' + key, None, convolution[key])
                if level_fn:
                    distance['conv-' + key] = sample_curve(level_fn, 0.0, dmax, breaks, 'db')
        if 'spread' in curves:
            distance['extent'] = sample_curve(lambda x: min(max(curve_value(curves['spread'], x, version), 0.0), 100.0) * 3.6,
                                              0.0, dmax, breaks, 'deg')
        fmod['distance'] = distance
        fmod['maxDistance'] = round(dmax, 4)
    else:
        lowpass_fn = highpass_fn = None
        fmod['distance'] = None
        fmod['maxDistance'] = DISTANCE_WITHOUT_ATTENUATION if pan else None
        if pan:
            notes.append(f'3D panning without attenuation: event range set to {DISTANCE_WITHOUT_ATTENUATION:g} m')
    distance = fmod['distance'] or {}
    if 'extent' in distance and not pan:
        notes.append('spread curve ignored: the event is not panned')
        distance.pop('extent')
    fmod['spatializer'] = {'panBlend': round(min(max(blend, 0.0), 100.0) / 100.0, 4),
                           'extent': distance.pop('extent', None)} if pan else None

    # azimuth low-pass
    if by_domain['direction']:
        parts = [c for _, c in by_domain['direction']]
        base = 0.0 if lowpass_fn else lowpass
        breaks = [p[0] for c in parts for p in c['points']]
        fmod['direction'] = {'lowpass': sample_curve(
            lambda a: lowpass_hz(base + sum(curve_value(c, a, version) for c in parts)), -180.0, 180.0, breaks, 'hz')}
        if lowpass_fn:
            notes.append('distance and azimuth low-pass cascaded (Wwise adds the amounts before filtering)')
    else:
        fmod['direction'] = None
    fmod['lowpass'] = round(lowpass_hz(lowpass), 1) if lowpass > 0 and not lowpass_fn and not by_domain['direction'] else None
    fmod['highpass'] = round(highpass_hz(highpass), 1) if highpass > 0 and not highpass_fn else None
    if aux_buses:
        bus = aux_buses.pop()
        fmod['reverb'] = {'bus': bus, 'level': round(clamp_db(aux_level), 3) if 'reverb' not in distance else None,
                          'curve': distance.pop('reverb', None)}
        notes.append(f'user aux bus {bus} -> the hangar reverb zone (send {aux_level:+g} dB)')
    else:
        fmod['reverb'] = None
    if convolution:
        fx_id = convolution['effect']['id']
        fmod['convolution'] = {'ir': impulses[fx_id]['file'], 'frames': impulses[fx_id]['frames'],
                               'link': convolution['link'],
                               'wet': None if 'conv-wet' in distance else round(clamp_db(convolution['wet']), 3),
                               'wetCurve': distance.pop('conv-wet', None),
                               'dry': None if 'conv-dry' in distance else round(clamp_db(convolution['dry']), 3),
                               'dryCurve': distance.pop('conv-dry', None)}
    else:
        fmod['convolution'] = None
    fmod['fadeInMs'] = round(w['action']['fadeInMs'], 3)
    if fmod['fadeInMs'] and w['action']['fadeCurve'] not in ('Linear', ''):
        notes.append(f'{w["action"]["fadeCurve"]} fade-in approximated as linear')

    # gain budget: instrument (+10 dB at most), then the sound track
    static_gain = gain + gain_hi
    instrument_volume = clamp_db(static_gain)
    track_volume = static_gain - instrument_volume
    if static_gain < FMOD_SILENCE_DB:
        notes.append(f'gain {static_gain:g} dB below FMOD silence')
        track_volume = 0.0
    require(track_volume <= FMOD_GAIN_MAX_DB, f'{name}: gain {static_gain:g} dB exceeds the FMOD range')

    # time modulators -> looping timeline
    timed = by_domain['time']
    if timed:
        first = timed[0][2]
        require(first['duration'] > 0 and first['meanRate'] > 0, f'{name}: time modulator {first["id"]} has no period')
        period = first['duration'] / first['meanRate']
        loops = {m['loops'] for _, _, m in timed}
        require(len(loops) == 1, f'{name}: time modulators with different loop counts')
        loop_count = loops.pop()
        require(loop_count == 0, f'{name}: time modulators playing {loop_count} times are not supported '
                                 f'(the port loops the timeline)')
        length = period
        for _, _, m in timed:
            require(not m['stopPlayback'], f'{name}: time modulator {m["id"]} stops playback')
            if m['rateRange'][0] != m['rateRange'][1]:
                notes.append(f'time modulator {m["id"]}: random rate {m["rateRange"][0]:g}..{m["rateRange"][1]:g} '
                             f'played at its mean {m["meanRate"]:g}')
            if abs(m['duration'] / m['meanRate'] - period) > 1e-6:
                notes.append(f'time modulator {m["id"]}: period {m["duration"] / m["meanRate"]:g} s fitted to {period:g} s')
            if m['scope'] != 'note/event':
                notes.append(f'time modulator {m["id"]}: {m["scope"]} scope played per event instance')

        def at(kind_name: str, t: float) -> float:
            phase = min(t / period, 1.0)
            return sum(curve_value(c, phase * m['duration'], version) for k, c, m in timed if k == kind_name)

        breaks = sorted({p[0] / m['duration'] * period for _, c, m in timed for p in c['points']})
        timeline = {'length': round(length, 4), 'loop': True, 'trackVolume': None, 'pitch': None}
        if any(k == 'gain' for k, _, _ in timed):
            peak = max(track_volume + at('gain', t) for t in grid(0.0, length, breaks))
            require(peak <= FMOD_GAIN_MAX_DB + 1e-6, f'{name}: modulated track gain {peak:g} dB exceeds the FMOD range')
            timeline['trackVolume'] = sample_curve(lambda t: track_volume + at('gain', t), 0.0, length, breaks, 'db')
        if any(k == 'pitch' for k, _, _ in timed):
            timeline['pitch'] = sample_curve(lambda t: at('pitch', t) / 100.0, 0.0, length, breaks, 'st')
        fmod['timeline'] = timeline
    else:
        fmod['timeline'] = None
    fmod['trackVolume'] = round(track_volume, 3)

    # instrument
    centre = (pitch + (pitch_lo + pitch_hi) / 2.0) / 100.0
    width = (pitch_hi - pitch_lo) / 100.0
    volume_range = gain_hi - gain_lo
    require(abs(centre) <= FMOD_PITCH_MAX, f'{name}: pitch {centre:g} semitones exceeds the FMOD range')
    delay_ms = [max(delay + delay_lo, 0.0) * 1000.0, max(delay + delay_hi, 0.0) * 1000.0]
    if delay_ms[1] > FMOD_DELAY_MAX_MS:
        notes.append(f'initial delay {delay_ms[0] / 1000:g}..{delay_ms[1] / 1000:g} s clamped to the FMOD trigger delay '
                     f'limit of {FMOD_DELAY_MAX_MS / 1000:g} s')
        delay_ms = [min(v, FMOD_DELAY_MAX_MS) for v in delay_ms]

    def entry_spec(e: dict, randomize: bool) -> dict:
        sid = e['source']['id']
        require(sid in media, f'{name}: source {sid} of sound {e["id"]} was not decoded')
        loop = e['props'].get('Loop')
        require(loop in (None, 0.0, 1.0), f'{name}: sound {e["id"]} loops {loop} times; finite loops are not supported')
        entry_delay = e['props'].get('InitialDelay', 0.0) * 1000.0
        require(entry_delay <= FMOD_DELAY_MAX_MS, f'{name}: sound {e["id"]} delay exceeds the FMOD limit')
        return {'file': media[sid]['file'], 'sound': e['id'], 'source': sid,
                'volume': round(clamp_db(prop_sum([e], GAIN_PROPS)), 3),
                'pitch': round(e['props'].get('Pitch', 0.0) / 100.0, 4),
                'delay': [round(entry_delay, 3)] * 2, 'looping': loop == 0.0,
                'volumeRandom': round(volume_range, 3) if randomize else 0.0,
                'pitchRandom': round(width, 4) if randomize else 0.0}

    instrument = {'volume': round(instrument_volume, 3), 'pitch': round(centre, 4),
                  'delay': [round(v, 3) for v in delay_ms]}
    looping_sound = any(e['props'].get('Loop') == 0.0 for e in entries)
    if container is None:
        kind = 'loop' if looping_sound else 'oneshot'
    else:
        c = container['container']
        require(c['mode'] == 0, f'{name}: sequence containers are not supported')
        weights = {wt for _, wt in container['playlist']}
        require(not c['usingWeight'] or len(weights) == 1, f'{name}: weighted playlists are not supported')
        if c['continuous']:
            require(not c['loopModMin'] and not c['loopModMax'], f'{name}: random loop counts are not supported')
            transition = TRANSITIONS.get(c['transition'])
            require(transition, f'{name}: unknown transition mode {c["transition"]}')
            if transition in ('delay', 'trigger-rate'):
                kind = 'scatterer'
            else:
                kind = 'loop'
                require(c['loopCount'] == 0, f'{name}: a gapless container playing {c["loopCount"]} times is not supported')
                if transition.startswith('crossfade'):
                    notes.append(f'{transition} transitions of {c["transitionMs"] / 1000:g} s '
                                 f'(+{c["transitionModMinMs"] / 1000:g}..{c["transitionModMaxMs"] / 1000:g} s) played back to back')
        else:
            kind = 'oneshot'
        if c['avoidRepeat'] and len(entries) == 2:
            notes.append('two-sound playlist: Wwise avoids repeats (alternates), the shuffle may repeat')
        elif c['avoidRepeat'] > 1:
            notes.append(f'avoid-repeat count {c["avoidRepeat"]} approximated by a shuffle')

    if kind == 'scatterer':
        c = container['container']
        require(not looping_sound, f'{name}: looping sounds in a scatterer')
        def duration(e: dict, cents: float) -> float:
            return media[e['source']['id']]['duration'] / 2.0 ** ((cents + e['props'].get('Pitch', 0.0)) / 1200.0)

        mean = sum(duration(e, centre * 100.0) for e in entries) / len(entries)
        longest = max(duration(e, (centre - width / 2.0) * 100.0) for e in entries)
        t, lo, hi = c['transitionMs'] / 1000.0, c['transitionModMinMs'] / 1000.0, c['transitionModMaxMs'] / 1000.0
        if TRANSITIONS[c['transition']] == 'delay':
            imin = max(mean + t + lo, longest)
            imax = max(mean + t + hi, imin)
            polyphony = 1
            notes.append(f'delay container: next sound {t + lo:g}..{t + hi:g} s after the previous ends -> spawn interval '
                         f'{imin:.2f}..{imax:.2f} s (mean sound {mean:.2f} s, longest at the lowest pitch {longest:.2f} s)')
        else:
            imin = max(t + lo, 0.001)
            imax = max(t + hi, imin)
            polyphony = max(1, math.ceil(longest / imin))
        total = c['loopCount'] * len(entries) if c['loopCount'] else 0
        instrument.update({'type': 'scatterer', 'looping': False, 'volumeRandom': round(volume_range, 3),
                           'pitchRandom': round(width, 4), 'interval': [round(imin * 1000.0, 1), round(imax * 1000.0, 1)],
                           'polyphony': polyphony, 'total': total,
                           'entries': [entry_spec(e, False) for e in entries]})
    elif kind == 'loop':
        randomize = volume_range > 0 or width > 0
        if len(entries) == 1 and not randomize:
            instrument.update({'type': 'single', 'looping': True, 'volumeRandom': 0.0, 'pitchRandom': 0.0,
                               'entries': [entry_spec(entries[0], False)]})
        else:
            require(not looping_sound, f'{name}: looping sounds in a playlist')
            instrument.update({'type': 'multi', 'looping': True, 'volumeRandom': 0.0, 'pitchRandom': 0.0,
                               'entries': [entry_spec(e, randomize) for e in entries]})
            if randomize:
                notes.append('random volume/pitch drawn per playlist entry (Wwise draws per played sound)')
    else:
        instrument.update({'type': 'single' if len(entries) == 1 else 'multi', 'looping': looping_sound,
                           'volumeRandom': round(volume_range, 3), 'pitchRandom': round(width, 4),
                           'entries': [entry_spec(e, False) for e in entries]})
    for entry in instrument['entries']:
        if entry['looping'] and instrument['type'] == 'single':
            entry['looping'] = False
            instrument['looping'] = True
    fmod['instrument'] = instrument
    return {'fmod': fmod, 'notes': notes}


# ------------------------------------------------------------------ media
def bank_media(blob: bytes) -> dict[int, bytes]:
    pos, index, data = 0, [], None
    while pos + 8 <= len(blob):
        tag, size = blob[pos:pos + 4], struct.unpack_from('<I', blob, pos + 4)[0]
        body = pos + 8
        if tag == b'DIDX':
            index = [struct.unpack_from('<III', blob, body + 12 * i) for i in range(size // 12)]
        elif tag == b'DATA':
            data = body
        pos = body + size
    require(not index or data is not None, 'bank media index without data')
    return {mid: blob[data + off:data + off + length] for mid, off, length in index}


def package_media(blob: bytes) -> dict[int, bytes]:
    """Streamed files of an AKPK file package (32- and 64-bit ID tables)."""
    require(blob[:4] == b'AKPK', 'not an AKPK package')
    header_size, _, languages, banks, streams = struct.unpack_from('<5I', blob, 4)
    externals = struct.unpack_from('<I', blob, 24)[0] if header_size >= 24 else 0
    pos = (28 if header_size >= 24 else 24) + languages
    out = {}

    def table(start: int, size: int, wide: bool) -> None:
        if size < 4:
            return
        count = struct.unpack_from('<I', blob, start)[0]
        step = 24 if wide else 20
        require(count * step + 4 <= size, 'malformed AKPK file table')
        for i in range(count):
            fid, block, length, first, _ = struct.unpack_from('<QIIII' if wide else '<IIIII', blob, start + 4 + i * step)
            out[fid] = blob[first * block:first * block + length]

    table(pos + banks, streams, False)
    if externals:
        table(pos + banks + streams, externals, True)
    return out


def decode(wem: Path, wav: Path) -> dict:
    result = subprocess.run([str(VGMSTREAM), '-i', '-o', str(wav), str(wem)], capture_output=True, text=True, timeout=120)
    require(result.returncode == 0 and wav.is_file(), f'vgmstream failed on {wem.name}: {result.stderr.strip()[-300:]}')
    with wave.open(str(wav), 'rb') as f:
        frames, rate, channels = f.getnframes(), f.getframerate(), f.getnchannels()
    require(frames > 0, f'{wav.name} decoded empty')
    return {'duration': round(frames / rate, 6), 'rate': rate, 'channels': channels, 'frames': frames}


def read_float_wav(path: Path) -> tuple[int, int, list[float]]:
    """(channels, rate, interleaved samples) of a 32-bit IEEE float WAV file."""
    blob = path.read_bytes()
    require(blob[:4] == b'RIFF' and blob[8:12] == b'WAVE', f'{path.name} is not a WAV file')
    pos, fmt, samples = 12, None, None
    while pos + 8 <= len(blob):
        tag, size = blob[pos:pos + 4], struct.unpack_from('<I', blob, pos + 4)[0]
        if tag == b'fmt ':
            fmt = struct.unpack_from('<HHIIHH', blob, pos + 8)
        elif tag == b'data':
            samples = list(struct.unpack_from(f'<{size // 4}f', blob, pos + 8))
        pos += 8 + size + (size & 1)
    require(fmt and fmt[0] == 3 and fmt[5] == 32 and samples is not None, f'{path.name} is not 32-bit float PCM')
    return fmt[1], fmt[2], samples


def write_float_wav(path: Path, channels: int, rate: int, samples: list[float]) -> None:
    data = struct.pack(f'<{len(samples)}f', *samples)
    fmt = struct.pack('<HHIIHH', 3, channels, rate, rate * channels * 4, channels * 4, 32)
    fact = struct.pack('<I', len(samples) // channels)
    path.write_bytes(b'RIFF' + struct.pack('<I', 4 + 8 + len(fmt) + 8 + len(fact) + 8 + len(data)) + b'WAVE'
                     + b'fmt ' + struct.pack('<I', len(fmt)) + fmt + b'fact' + struct.pack('<I', len(fact)) + fact
                     + b'data' + struct.pack('<I', len(data)) + data)


def decode_impulse(wem: Path, wav: Path, pre_delay_ms: float) -> dict:
    """A convolution impulse response as float WAV with the effect's pre-delay prepended as silence."""
    raw = wav.with_name(wav.stem + '.decoded.wav')
    result = subprocess.run([str(VGMSTREAM), '-i', '-W', '4', '-o', str(raw), str(wem)], capture_output=True, text=True,
                            timeout=120)
    require(result.returncode == 0 and raw.is_file(), f'vgmstream failed on {wem.name}: {result.stderr.strip()[-300:]}')
    channels, rate, samples = read_float_wav(raw)
    raw.unlink()
    require(samples and len(samples) % channels == 0, f'{wem.name}: empty or ragged impulse response')
    pad = round(pre_delay_ms * rate / 1000.0)
    write_float_wav(wav, channels, rate, [0.0] * (pad * channels) + samples)
    frames = pad + len(samples) // channels
    energy = [sum(x * x for x in samples[c::channels]) for c in range(channels)]
    return {'duration': round(frames / rate, 6), 'rate': rate, 'channels': channels, 'frames': frames,
            'preDelayFrames': pad, 'peak': round(max(abs(x) for x in samples), 6),
            'energyDb': [round(10.0 * math.log10(e), 3) for e in energy]}


# ------------------------------------------------------------------ main
def scene_events(scene: dict) -> tuple[list[str], list[dict]]:
    ported, skipped = [], []
    for sound in scene['sounds']:
        name = sound['event']
        if not sound.get('autoplay', False):
            skipped.append({'wwise': name, 'node': sound['node'],
                            'reason': 'not auto-played in the scene (started by gameplay, e.g. the radio tap interaction)'})
        elif name not in ported:
            ported.append(name)
    return ported, skipped


def unpack_banks(client: Path, sources: dict) -> None:
    WWISE_DIR.mkdir(parents=True, exist_ok=True)
    for name in EVENT_BANKS:
        for ext in ('bnk', 'pck'):
            packed = client / CLIENT_BANKS / f'{name}.{ext}.dvpl'
            require(packed.is_file(), f'missing {packed}')
            blob = packed.read_bytes()
            sources[str(CLIENT_BANKS / packed.name)] = hashlib.sha256(blob).hexdigest()
            (WWISE_DIR / f'{name}.{ext}').write_bytes(hangar.dvpl.unpack_bytes(blob))
    init = client / INIT_BANK
    require(init.is_file(), f'missing {init}')
    sources[str(INIT_BANK)] = sha256(init)
    shutil.copyfile(init, WWISE_DIR / 'Init.bnk')


def wwiser_dump(names: list[str]) -> Dump:
    (WWISE_DIR / 'wwnames.txt').write_text('\n'.join(names) + '\n', encoding='utf-8')
    out = WWISE_DIR / 'dump.xml'
    out.unlink(missing_ok=True)
    result = subprocess.run([sys.executable, str(WWISER), '-d', 'xml', '-dn', 'dump', 'Init.bnk']
                            + [f'{n}.bnk' for n in EVENT_BANKS], cwd=WWISE_DIR, capture_output=True, text=True, timeout=900)
    (WWISE_DIR / 'wwiser.log').write_text(result.stdout + result.stderr, encoding='utf-8')
    require(result.returncode == 0 and out.is_file(), f'wwiser failed: {result.stderr.strip()[-300:]}')
    return Dump(out.read_text(encoding='utf-8'))


def extract_media(events: list[dict]) -> tuple[dict[int, dict], dict[int, dict], list[dict]]:
    available: dict[int, tuple[str, bytes]] = {}
    for name in EVENT_BANKS:
        for mid, blob in bank_media((WWISE_DIR / f'{name}.bnk').read_bytes()).items():
            available.setdefault(mid, (f'{name}.bnk', blob))
        for mid, blob in package_media((WWISE_DIR / f'{name}.pck').read_bytes()).items():
            if mid not in available or len(blob) > len(available[mid][1]):
                available[mid] = (f'{name}.pck', blob)
    if MEDIA_DIR.exists():
        shutil.rmtree(MEDIA_DIR)
    MEDIA_DIR.mkdir(parents=True)
    wem_dir = WWISE_DIR / 'wem'
    wem_dir.mkdir(exist_ok=True)
    media, listing = {}, []
    for w in events:
        nodes = w['nodes']
        for index, sid in enumerate(w['entries'], 1):
            source = nodes[str(sid)]['source']
            require(source['id'] in available, f'{w["event"]}: media {source["id"]} is in no bank or package')
            origin, blob = available[source['id']]
            require(len(blob) >= source['inMemory'], f'{w["event"]}: media {source["id"]} is truncated')
            wem = wem_dir / f'{source["id"]}.wem'
            wem.write_bytes(blob)
            wav_name = f'{short_name(w["event"])}_{index:02d}.wav'
            info = decode(wem, MEDIA_DIR / wav_name)
            media[source['id']] = {'file': wav_name, **info}
            listing.append({'event': w['event'], 'sound': sid, 'source': source['id'], 'from': origin,
                            'bytes': len(blob), 'sha256': hashlib.sha256(blob).hexdigest(), 'stream': source['stream'],
                            'file': wav_name, **info})
    impulses = {}
    for w in events:
        for key, effect in w['effects'].items():
            fid = int(key)
            if effect['plugin'] != CONVOLUTION_REVERB or fid in impulses:
                continue
            require(len(effect['media']) == 1, f'convolution reverb {fid} has {len(effect["media"])} media')
            sid = effect['media'][0]
            require(sid in available, f'{w["event"]}: impulse response {sid} is in no bank or package')
            origin, blob = available[sid]
            wem = wem_dir / f'{sid}.wem'
            wem.write_bytes(blob)
            wav_name = f'impulse_{fid}.wav'
            info = decode_impulse(wem, MEDIA_DIR / wav_name, effect['params'].get('fPreDelay', 0.0))
            impulses[fid] = {'file': wav_name, **info}
            listing.append({'event': w['event'], 'effect': fid, 'source': sid, 'from': origin, 'bytes': len(blob),
                            'sha256': hashlib.sha256(blob).hexdigest(), 'file': wav_name, **info})
    return media, impulses, listing


# ------------------------------------------------------------------ Unity clips
def read_wav(path: Path) -> tuple[int, np.ndarray]:
    """(rate, samples [frames, channels] float64) of a PCM 16/24/32-bit or float WAV file."""
    blob = path.read_bytes()
    require(blob[:4] == b'RIFF' and blob[8:12] == b'WAVE', f'{path.name} is not a WAV file')
    pos, fmt, data = 12, None, None
    while pos + 8 <= len(blob):
        tag, size = blob[pos:pos + 4], struct.unpack_from('<I', blob, pos + 4)[0]
        if tag == b'fmt ':
            fmt = struct.unpack_from('<HHIIHH', blob, pos + 8)
        elif tag == b'data':
            data = blob[pos + 8:pos + 8 + size]
        pos += 8 + size + (size & 1)
    require(fmt is not None and data is not None, f'{path.name}: no fmt or data chunk')
    tag, channels, rate, _, _, bits = fmt
    if tag == 0xFFFE:
        tag = struct.unpack_from('<H', blob, blob.index(b'fmt ') + 8 + 24)[0]
    if tag == 3 and bits == 32:
        samples = np.frombuffer(data, '<f4').astype(np.float64)
    elif tag == 1 and bits == 16:
        samples = np.frombuffer(data, '<i2').astype(np.float64) / 32768.0
    elif tag == 1 and bits == 24:
        raw = np.frombuffer(data, np.uint8).reshape(-1, 3).astype(np.int32)
        value = raw[:, 0] | raw[:, 1] << 8 | raw[:, 2] << 16
        samples = np.where(value >= 1 << 23, value - (1 << 24), value).astype(np.float64) / float(1 << 23)
    elif tag == 1 and bits == 32:
        samples = np.frombuffer(data, '<i4').astype(np.float64) / 2.0 ** 31
    else:
        raise AudioError(f'{path.name}: WAV format {tag} with {bits} bits is not supported')
    require(samples.size % channels == 0, f'{path.name}: ragged sample data')
    return rate, samples.reshape(-1, channels)


def write_wav(path: Path, rate: int, samples: np.ndarray) -> None:
    """32-bit float WAV."""
    frames, channels = samples.shape
    data = np.ascontiguousarray(samples, '<f4').tobytes()
    fmt = struct.pack('<HHIIHH', 3, channels, rate, rate * channels * 4, channels * 4, 32)
    fact = struct.pack('<I', frames)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(b'RIFF' + struct.pack('<I', 4 + 8 + len(fmt) + 8 + len(fact) + 8 + len(data)) + b'WAVE'
                     + b'fmt ' + struct.pack('<I', len(fmt)) + fmt + b'fact' + struct.pack('<I', len(fact)) + fact
                     + b'data' + struct.pack('<I', len(data)) + data)


def peak_dbfs(samples: np.ndarray) -> float:
    peak = float(np.abs(samples).max()) if samples.size else 0.0
    return 20.0 * math.log10(peak) if peak > 0 else -math.inf


def encode_ogg(wav: Path, ogg: Path) -> None:
    ogg.parent.mkdir(parents=True, exist_ok=True)
    result = subprocess.run([FFMPEG, '-v', 'error', '-y', '-i', str(wav), '-c:a', 'libvorbis', '-q:a', VORBIS_QUALITY,
                             '-fflags', '+bitexact', '-flags:a', '+bitexact', '-map_metadata', '-1', str(ogg)],
                            capture_output=True, text=True, timeout=300)
    require(result.returncode == 0 and ogg.is_file(), f'ffmpeg failed on {wav.name}: {result.stderr.strip()[-300:]}')


def resample(samples: np.ndarray, rate: int, target: int) -> np.ndarray:
    """Band-limited FFT resampling of a short buffer (the impulse response)."""
    if rate == target:
        return samples
    frames = samples.shape[0]
    count = int(round(frames * target / rate))
    spectrum = np.fft.rfft(samples, axis=0)
    bins = count // 2 + 1
    if bins <= spectrum.shape[0]:
        spectrum = spectrum[:bins]
    else:
        spectrum = np.concatenate([spectrum, np.zeros((bins - spectrum.shape[0], samples.shape[1]))], axis=0)
    return np.fft.irfft(spectrum, n=count, axis=0) * (count / frames)


def circular_wet(dry: np.ndarray, rate: int, impulse: Path, link: bool) -> np.ndarray:
    """The steady-state wet output of a convolution reverb fed with a seamless loop: the loop
    convolved circularly with the impulse response (resampled to the loop's rate and
    normalised to unit energy per channel), as long as the loop itself so both play in sync."""
    ir_rate, ir = read_wav(impulse)
    ir = resample(ir, ir_rate, rate)
    energy = np.sqrt((ir ** 2).sum(0))
    require((energy > 0).all(), f'{impulse.name}: silent impulse response channel')
    ir = ir / energy
    frames = dry.shape[0]
    folded = np.zeros((frames, ir.shape[1]))
    for start in range(0, ir.shape[0], frames):
        segment = ir[start:start + frames]
        folded[:segment.shape[0]] += segment
    source = dry.mean(1, keepdims=True) if link or dry.shape[1] == 1 else dry
    spectrum = np.fft.rfft(source, axis=0)
    if source.shape[1] == 1:
        spectrum = np.repeat(spectrum, ir.shape[1], axis=1)
    else:
        require(source.shape[1] == ir.shape[1], f'{impulse.name}: {ir.shape[1]} IR channels for {source.shape[1]} inputs')
    return np.fft.irfft(spectrum * np.fft.rfft(folded, axis=0), n=frames, axis=0)


def curve_peak(points, default: float = 0.0) -> float:
    return max((float(v) for _, v in points), default=default) if points else default


def unity_events(described: list[dict], media: dict[int, dict], impulses: dict[int, dict]) -> list[dict]:
    """The playback spec of every event and its clips (encoded here).

    An entry's runtime gain is gainDb + timeline volume + entry volume (- a random
    amount up to the volume randomisation) + distance volume, less the gain baked
    into its clip (`bakeDb`); the convolution's dry and wet levels add to the dry
    clip and to the wet loop."""
    if CLIPS_DIR.exists():
        for old in CLIPS_DIR.iterdir():
            if old.suffix != '.meta':
                old.unlink()
    by_file = {m['file']: m for m in media.values()}
    events = []
    for d in described:
        f = d['fmod']
        notes = d['notes']
        instrument = f['instrument']
        timeline = f['timeline']
        track_curve = timeline['trackVolume'] if timeline and timeline['trackVolume'] else None
        gain = instrument['volume'] + (0.0 if track_curve else f['trackVolume'])
        track_peak = curve_peak(track_curve) if track_curve else 0.0
        distance = f['distance'] or {}
        distance_peak = curve_peak(distance.get('volume'))
        convolution = f['convolution']
        dry_peak = 0.0
        if convolution:
            dry_peak = curve_peak(convolution['dryCurve'], convolution['dry'] or 0.0) if convolution['dryCurve'] else (convolution['dry'] or 0.0)
        entries = []
        for e in instrument['entries']:
            info = by_file[e['file']]
            rate, samples = read_wav(MEDIA_DIR / e['file'])
            wanted = gain + track_peak + e['volume'] + distance_peak + dry_peak
            headroom = HEADROOM_DBFS - peak_dbfs(samples)
            bake = min(max(wanted, 0.0), max(headroom, 0.0))
            if wanted > bake + 1e-6:
                notes.append(f'{e["file"]}: peak gain {wanted:+.2f} dB, {bake:+.2f} dB fit into the clip, the rest is lost')
            stem = Path(e['file']).stem
            baked = BAKED_DIR / f'{stem}.wav'
            write_wav(baked, rate, samples * 10.0 ** (bake / 20.0))
            encode_ogg(baked, CLIPS_DIR / f'{stem}.ogg')
            entries.append({'clip': RESOURCE_PREFIX + stem, 'bakeDb': round(bake, 3), 'volume': e['volume'],
                            'pitch': e['pitch'], 'delay': e['delay'][0], 'looping': e['looping'],
                            'volumeRandom': e['volumeRandom'], 'pitchRandom': e['pitchRandom'],
                            'duration': info['duration'], 'frames': info['frames'], 'rate': info['rate'],
                            'channels': info['channels']})
        wet = None
        if convolution:
            require(instrument['type'] == 'single' and instrument['looping'] and len(entries) == 1,
                    f'{f["name"]}: the convolution loop needs a single looping sound')
            e = instrument['entries'][0]
            rate, samples = read_wav(MEDIA_DIR / e['file'])
            rendered = circular_wet(samples, rate, MEDIA_DIR / convolution['ir'], convolution['link'])
            wet_peak = curve_peak(convolution['wetCurve'], convolution['wet'] or 0.0) if convolution['wetCurve'] else (convolution['wet'] or 0.0)
            bake = HEADROOM_DBFS - peak_dbfs(rendered)
            wanted = gain + track_peak + e['volume'] + distance_peak + wet_peak - bake
            require(wanted <= 1e-6, f'{f["name"]}: the wet loop needs {wanted:+.2f} dB above unity gain')
            stem = Path(e['file']).stem + '_wet'
            baked = BAKED_DIR / f'{stem}.wav'
            write_wav(baked, rate, rendered * 10.0 ** (bake / 20.0))
            encode_ogg(baked, CLIPS_DIR / f'{stem}.ogg')
            wet = {'clip': RESOURCE_PREFIX + stem, 'bakeDb': round(bake, 3), 'frames': rendered.shape[0], 'rate': rate,
                   'channels': rendered.shape[1], 'wet': convolution['wet'], 'wetCurve': convolution['wetCurve'],
                   'dry': convolution['dry'], 'dryCurve': convolution['dryCurve'], 'link': convolution['link'],
                   'impulse': convolution['ir'], 'normalisation': 'unit energy per channel'}
            notes.append(f'convolution reverb rendered as {stem}.ogg: the dry loop convolved circularly with '
                         f'{convolution["ir"]} (resampled to {rate} Hz, unit energy per channel)')
        spatial = f['spatializer']
        events.append({
            'name': f['name'], 'wwise': f['wwise'],
            'gainDb': round(gain, 3),
            'spatialBlend': spatial['panBlend'] if spatial else 0.0,
            'spread': spatial['extent'] if spatial else None,
            'maxDistance': f['maxDistance'],
            'distance': {k: v for k, v in distance.items() if k in ('volume', 'lowpass', 'highpass')} or None,
            'direction': f['direction'],
            'lowpass': f['lowpass'], 'highpass': f['highpass'],
            'reverb': f['reverb'],
            'convolution': wet,
            'fadeInMs': f['fadeInMs'],
            'timeline': {'length': timeline['length'], 'volume': track_curve, 'pitch': timeline['pitch']} if timeline else None,
            'instrument': {'type': instrument['type'], 'looping': instrument['looping'], 'pitch': instrument['pitch'],
                           'delay': instrument['delay'], 'volumeRandom': instrument['volumeRandom'],
                           'pitchRandom': instrument['pitchRandom'], 'interval': instrument.get('interval'),
                           'polyphony': instrument.get('polyphony', 1), 'total': instrument.get('total', 0),
                           'entries': entries},
        })
    return events


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('--source', type=Path, default=hangar.CLIENT, help='Blitz 11.21 client dump (read-only)')
    parser.add_argument('--tools', type=Path, default=hangar.DAVA_TOOLS, help='blitz-legacy-godot/tools (DVPL reader)')
    args = parser.parse_args()
    hangar.load_tools(args.tools)
    require(SCENE.is_file(), f'missing {SCENE}; run Tools/port_halloween_hangar_2026.py first')
    scene = json.loads(SCENE.read_text(encoding='utf-8'))
    require(scene.get('map') == hangar.MAP, f'{SCENE} describes {scene.get("map")}')
    names, skipped = scene_events(scene)
    sources: dict[str, str] = {}
    AUDIO.mkdir(parents=True, exist_ok=True)
    unpack_banks(args.source, sources)
    dump = wwiser_dump(names + [s['wwise'] for s in skipped])
    events = [read_event(dump, n) for n in names]
    media, impulses, listing = extract_media(events)
    described = []
    for w in events:
        mapped = map_event(w, media, impulses)
        described.append({'wwise': w, 'fmod': mapped['fmod'], 'notes': mapped['notes']})
        print(f'{mapped["fmod"]["name"]}: {mapped["fmod"]["instrument"]["type"]}, '
              f'{len(mapped["fmod"]["instrument"]["entries"])} sounds, {len(mapped["notes"])} notes')
    unity = unity_events(described, media, impulses)
    buses = sorted({d['fmod']['reverb']['bus'] for d in described if d['fmod']['reverb']})
    require(len(buses) <= 1, f'the events send to several user aux buses {buses}')
    zone = read_reverb_zone(dump, buses[0]) if buses else None
    if zone:
        print(f'reverb zone: {zone["plugin"]} {zone["effect"]} on bus {zone["bus"]}, decay {zone["unity"]["decayTime"]:g} s')
    DESCRIPTION.write_text(json.dumps({
        'map': hangar.MAP, 'events': unity, 'reverbZone': zone, 'skipped': skipped,
        'wwise': [{'event': d['wwise']['event'], 'description': d['fmod'], 'notes': d['notes']} for d in described],
        'media': listing, 'tools': {'wwiser': str(WWISER), 'vgmstream': str(VGMSTREAM), 'ffmpeg': FFMPEG},
        'source': str(args.source), 'sources': dict(sorted(sources.items()))},
        ensure_ascii=False, indent=1), encoding='utf-8')
    clips = sum(len(e['instrument']['entries']) + (1 if e['convolution'] else 0) for e in unity)
    print(f'{len(unity)} events, {clips} clips -> {CLIPS_DIR.relative_to(ROOT)}')
    print('HALLOWEEN_HANGAR_AUDIO_OK')


if __name__ == '__main__':
    try:
        main()
    except AudioError as error:
        print(f'HALLOWEEN_HANGAR_AUDIO_FAILED: {error}', file=sys.stderr)
        sys.exit(1)
