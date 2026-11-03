#!/usr/bin/env python3
"""Port the World of Ducks event of the Blitz 4.8.0 client.

The map itself goes through the ordinary client map porter
(``port_blitz_client_map.py --map sea_of_ducks``). This tool ports what only
the event uses, every piece straight from the 4.8.0 pack:

* ``card``      the WarDuck card (``XML/item_defs/vehicles/other/Oth10_WarDuck``)
                as the mode's own ``wod_warduck``, written into
                ``Data/world_of_ducks.json``; it never joins ``tanks.json``.
* ``model``     ``Oth_10_war_duck.sc2`` (hull, the three gun "hats", the
                collision scene) through the 4.8 branch model exporter into
                ``Models/Tanks/WODWARDUCK``.
* ``sounds``    ``Sfx/world_of_ducks_bank.fsb`` and the map's ambient banks
                decoded with vgmstream, cross-checked against the Godot cut.
* ``particles`` the DAVA particle yamls of the map and the duck effects plus
                their sprite sheets, see ``dava_particles.py``.
* ``ui``        the event's hangar card art and vehicle icons.
* ``atlas``     registers the event card's battle icons (``ui``) in the shared
                atlas table; ``ui`` runs it too.
* ``catalog``   marks the 11.20 WarDuck of ``tanks.json`` as the event's own
                vehicle (``world_of_ducks_registry.py``, also run by the base
                card porters), hidden everywhere outside the event.

Run:  python3 Tools/port_world_of_ducks.py [card] [model] [sounds] [particles] [ui] [atlas] [catalog]
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

ROOT = Path(__file__).resolve().parent.parent
RESOURCES = ROOT / "Assets/Blitz/Resources"
MODE_DATA = RESOURCES / "Data/world_of_ducks.json"
ATLAS = RESOURCES / "Data/atlas_regions.json"
TANKS = RESOURCES / "Data/tanks.json"
HANGAR_MANIFEST = RESOURCES / "Data/hangar_source_manifest.json"

VEHICLE_ID = "Oth10_WarDuck"
NATION = "other"
CARD_NAME = "wod_warduck"
MAP_ID = "sea_of_ducks"
# _battle.gd WORLD_OF_DUCKS_TEAM_SIZE and Global.prepare_battle_rules: the
# 4.8 client ships the arena and the vehicle, not the event server's queue,
# so the port keeps Blitz's 7 v 7, seven minute format.
TEAM_SIZE = 7
TIME_LIMIT_SECONDS = 420


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_mode() -> dict:
    if MODE_DATA.is_file():
        return json.loads(MODE_DATA.read_text(encoding="utf-8"))
    return {}


def save_mode(document: dict) -> None:
    MODE_DATA.parent.mkdir(parents=True, exist_ok=True)
    MODE_DATA.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


# ------------------------------------------------------------------- card
def build_card() -> dict:
    from xml.etree import ElementTree as ET
    import port_blitz_480_data as data
    from audit_blitz_480_tanks import PACK, client_for, module_inventory, strings_table, text

    strings = strings_table()
    directory, shared, listed = client_for(NATION)
    listing = listed[VEHICLE_ID]
    root = ET.parse(directory / f"{VEHICLE_ID}.xml").getroot()
    counts, _ = module_inventory(root, shared)
    tank = {"id": VEHICLE_ID, "modules": counts}
    client = data.NationClient(NATION, strings)
    # An event vehicle: every module ships installed, it costs nothing, it
    # unlocks nothing and no shop, tree or collector rack lists it.
    overrides = {
        "tree_tank": False,
        "acquisition": "event",
        "prem": True,
        "shop_tank": False,
        "collector": False,
        "cost": 0,
        "cost_gold": 0,
        "cost_xp": 0,
        "unlock_tanks": {},
        "tree_branch": 0,
    }
    card = data.build_card(client, tank, {VEHICLE_ID: CARD_NAME}, overrides)
    tags = text(listing.find("tags")).split()
    card["source_tags"] = tags
    apply_shot_gravity(card)
    return card


# The 4.8 client's standard shell drop: guns.xml authors 9.81 on every shot
# the ordinary 4.8 cards carry, which the runtime reads as multiplier 1.
STANDARD_SHOT_GRAVITY = 9.81


def apply_shot_gravity(card: dict) -> None:
    """Carry each duck shot's own ``gravity`` as the runtime multiplier.

    The shared 4.8 card builder keeps gravity for guided rockets only, but
    the WarDuck's torpedo (``Waterspit_2``) is authored with ``0.001`` so it
    runs flat along the water instead of dropping like a shell.
    """
    from xml.etree import ElementTree as ET
    from audit_blitz_480_tanks import PACK

    root = ET.parse(PACK / "XML/item_defs/vehicles/other/components/guns.xml").getroot()
    shared = root.find("shared")
    for gun in card.get("guns", []):
        node = shared.find(gun["name"]) if shared is not None else None
        shots = node.find("shots") if node is not None else None
        if shots is None:
            continue
        for shell, shot in zip(gun.get("shells", []), list(shots)):
            found = shot.find("gravity")
            if found is None or not (found.text or "").strip():
                continue
            gravity = float(found.text.strip())
            shell["source_shot"] = shot.tag
            if abs(gravity - STANDARD_SHOT_GRAVITY) > 1e-3:
                shell["gravity"] = round(gravity / STANDARD_SHOT_GRAVITY, 8)


def port_card_data() -> dict:
    card = build_card()
    document = load_mode()
    document.update({
        "id": "world_of_ducks",
        "map": MAP_ID,
        "teamSize": TEAM_SIZE,
        "timeLimitSeconds": TIME_LIMIT_SECONDS,
        "friendlyFire": False,
        "baseCapture": False,
        "card": card,
    })
    save_mode(document)
    guns = [(g["name"], g.get("shells", [{}])[0].get("name") if g.get("shells") else None) for g in card["guns"]]
    print(json.dumps({"card": card["name"], "display": card["display_name"], "hp": card["health"],
                      "guns": guns, "turrets": [t["name"] for t in card["turrets"]]}, ensure_ascii=False))
    return card


# ------------------------------------------------------------------ model
def port_model(rebuild: bool) -> dict:
    from xml.etree import ElementTree as ET
    import blitz_480_collision
    import port_blitz_480_models as models
    import port_blitz_branch_models as exporter
    from audit_blitz_480_tanks import PACK, VEHICLES, text

    card = load_mode().get("card")
    if not card:
        raise SystemExit("run `card` first: Data/world_of_ducks.json has no card")
    root = ET.parse(VEHICLES / NATION / f"{VEHICLE_ID}.xml").getroot()
    model_path = text(root.find("blitz/blitzModelPath"))
    collision_path = text(root.find("blitz/collisionMesh"))
    record = {
        "nation": NATION,
        "vehicle_id": VEHICLE_ID,
        "card": card,
        "model_dir": str(Path(model_path).parent.relative_to("Tanks")),
        "model_stem": Path(model_path).stem,
        "collision_stem": Path(collision_path).stem,
    }
    blitz_480_collision.install()
    exporter.PLATE_SCREEN = blitz_480_collision.screen_of
    models.configure(record)
    entry = exporter.port_card(card, VEHICLE_ID, rebuild, True)
    document = load_mode()
    document["model"] = {
        "resource": f"Models/Tanks/{exporter.unity_id(CARD_NAME)}",
        "source": f"3d/{model_path}",
        "source_sha256": sha(PACK / "3d" / model_path),
        "collision": f"3d/Tanks/{collision_path}",
        "collision_sha256": sha(PACK / "3d/Tanks" / collision_path),
    }
    save_mode(document)
    print(json.dumps({"model": document["model"], "entry_keys": sorted(entry)}, ensure_ascii=False))
    return entry


# ----------------------------------------------------------------- sounds
VGMSTREAM = Path.home() / ".local/opt/vgmstream/vgmstream-cli"
GODOT = ROOT.parent / "blitz-legacy-godot"
GODOT_EVENT = GODOT / "assets/events/world_of_ducks"
AUDIO_OUT = RESOURCES / "Audio/world_of_ducks"
SOUND_BANKS = {
    # world_of_ducks.fev: the WarDuck's guns, hits, voice and chassis bed. The
    # transmission_duck_* motor layer of treds_surf has no runtime route (the
    # chassis plays the /surface bed only), so it stays in the bank.
    "world_of_ducks_bank": None,
    # ambient.fev events the map's SoundComponents name: ambient/water/
    # river_weak, ambient/water/lake_weak and ambient/birds/seagull.
    "ambient_river": ("river_01", "river_02", "river_03"),
    "ambient_lake": ("lake_01",),
    "ambient_birds": ("seagull_01", "seagull_02", "seagull_03"),
}
EXCLUDED_SAMPLES = {"transmission_duck_01", "transmission_duck_02", "transmission_duck_03",
                    "transmission_duck_04", "transmission_duck_05"}


def wav_pcm(path: Path) -> tuple[int, int, bytes]:
    import wave
    with wave.open(str(path)) as handle:
        return handle.getframerate(), handle.getnchannels(), handle.readframes(handle.getnframes())


def compare_with_godot(name: str, rate: int, channels: int, pcm: bytes) -> dict:
    """The Godot cut decodes the same FSB with its own IMA reader; record how
    close it is, the port itself always ships the vgmstream decode."""
    import numpy as np
    other = GODOT_EVENT / "sfx" / f"{name}.wav"
    if not other.is_file():
        return {"present": False}
    o_rate, o_channels, o_pcm = wav_pcm(other)
    result = {"present": True, "pcmIdentical": o_pcm == pcm and o_rate == rate and o_channels == channels}
    if not result["pcmIdentical"] and o_rate == rate and o_channels == channels and len(o_pcm) == len(pcm):
        a = np.frombuffer(pcm, dtype="<i2").astype(np.float64)
        b = np.frombuffer(o_pcm, dtype="<i2").astype(np.float64)
        if a.std() > 0 and b.std() > 0:
            result["correlation"] = round(float(np.corrcoef(a, b)[0, 1]), 4)
        result["rmsRatio"] = round(float(np.sqrt((b * b).mean()) / max(1e-9, np.sqrt((a * a).mean()))), 4)
    return result


def port_sounds() -> dict:
    import shutil
    import subprocess
    import tempfile
    import numpy as np
    from audit_blitz_480_tanks import PACK

    if not VGMSTREAM.is_file():
        raise SystemExit(f"vgmstream-cli missing: {VGMSTREAM}")
    if AUDIO_OUT.is_dir():
        for stale in AUDIO_OUT.glob("*.wav"):
            stale.unlink()
    AUDIO_OUT.mkdir(parents=True, exist_ok=True)
    samples = {}
    banks = {}
    for bank, wanted in SOUND_BANKS.items():
        source = PACK / "Sfx" / f"{bank}.fsb"
        banks[f"Sfx/{bank}.fsb"] = sha(source)
        with tempfile.TemporaryDirectory() as staging:
            subprocess.run([str(VGMSTREAM), "-S", "0", "-o", f"{staging}/?n.wav", str(source)],
                           check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            for decoded in sorted(Path(staging).glob("*.wav")):
                name = decoded.stem
                if name in EXCLUDED_SAMPLES or (wanted is not None and name not in wanted):
                    continue
                rate, channels, pcm = wav_pcm(decoded)
                peak = int(np.abs(np.frombuffer(pcm, dtype="<i2")).max()) if pcm else 0
                if peak == 0:
                    continue  # duck_hits_squek_05 is authored silence.
                target = AUDIO_OUT / f"{name}.wav"
                shutil.copyfile(decoded, target)
                samples[name] = {
                    "clip": f"Audio/world_of_ducks/{name}",
                    "bank": f"Sfx/{bank}.fsb",
                    "frequency": rate,
                    "channels": channels,
                    "frames": len(pcm) // (2 * channels),
                    "sha256": sha(target),
                    "godot": compare_with_godot(name, rate, channels, pcm),
                }
        if wanted is not None:
            missing = set(wanted) - set(samples)
            if missing:
                raise SystemExit(f"{bank} lacks {sorted(missing)}")
    document = load_mode()
    document["sounds"] = {
        "decoder": "vgmstream-cli (FSB5 IMA ADPCM)",
        "banks": banks,
        "fev": {"Sfx/world_of_ducks.fev": sha(PACK / "Sfx/world_of_ducks.fev"),
                "Sfx/ambient.fev": sha(PACK / "Sfx/ambient.fev")},
        "samples": dict(sorted(samples.items())),
    }
    save_mode(document)
    identical = sum(1 for s in samples.values() if s["godot"].get("pcmIdentical"))
    print(json.dumps({"samples": len(samples), "godotIdentical": identical}, ensure_ascii=False))
    return document["sounds"]


# -------------------------------------------------------------- particles
FX_ROOT = "Particles/WorldOfDucks"
FX_OUT = RESOURCES / FX_ROOT
FX_MANIFEST = RESOURCES / "Data/world_of_ducks_fx_manifest.json"
# The event ships its own FX tree; the client looks a vehicle effect up there
# first (3d/FX/<path> -> 3d/FX/sea_of_ducks/<path>).
EVENT_FX = "3d/FX/sea_of_ducks"


class FxPorter:
    """4.8 YAML emitters in the shared DavaFx source layout, like
    port_blitz_480_wreck.py builds Resources/Legacy480."""

    def __init__(self, pack: Path):
        self.pack = pack
        self.textures: dict = {}
        self.provenance: dict = {}
        self.emitters: dict = {}

    def find(self, rel: str) -> Path:
        for base in (self.pack, self.pack / "from_apk/Data"):
            candidate = base / rel
            if candidate.exists():
                return candidate
        raise FileNotFoundError(rel)

    def record(self, path: Path) -> None:
        try:
            key = str(path.resolve().relative_to(self.pack.resolve()))
        except ValueError:
            key = str(path)
        self.provenance[key] = sha(path)

    def texture(self, sprite: str) -> None:
        import math
        from PIL import Image
        if sprite in self.textures:
            return
        rel = sprite.split("Data/")[-1].replace("~res:/", "")
        descriptor = self.find(rel + ".txt")
        self.record(descriptor)
        lines = descriptor.read_text().splitlines()
        sheets = int(lines[0])
        atlases = lines[1:1 + sheets]
        width, height = map(int, lines[1 + sheets].split())
        frames = int(lines[2 + sheets])
        cols = math.ceil(math.sqrt(frames))
        rows = math.ceil(frames / cols)
        image = Image.new("RGBA", (width * cols, height * rows))
        cache = {}
        for index, row in enumerate(lines[3 + sheets:3 + sheets + frames]):
            x, y, cw, ch, dx, dy, page = map(int, row.split()[:7])
            if page not in cache:
                atlas = self.find(str(Path(rel).parent / Path(atlases[page]).with_suffix(".mali.webp")))
                self.record(atlas)
                cache[page] = Image.open(atlas).convert("RGBA")
            image.paste(cache[page].crop((x, y, x + cw, y + ch)),
                        (index % cols * width + dx, index // cols * height + dy))
        name = Path(rel).name + "_" + hashlib.sha256(rel.encode()).hexdigest()[:10]
        (FX_OUT / "Textures").mkdir(parents=True, exist_ok=True)
        image.save(FX_OUT / "Textures" / f"{name}.png")
        self.textures[sprite] = {"resource": f"{FX_ROOT}/Textures/{name}", "frames": frames,
                                 "cols": cols, "rows": rows, "usage": ["spritePath"]}

    @staticmethod
    def line(value, dimensions=1, color=False):
        if not isinstance(value, list):
            return value
        if dimensions == 1:
            return [2, [[1, value[i + 1], value[i]] for i in range(0, len(value), 2)]]

        def vec(a):
            a = [x / 255 for x in a] if color else a
            return a if color else ([a[0], a[1], 1] if len(a) == 2 else a)

        if not any(isinstance(x, list) for x in value):
            return vec(value)
        result = [2]
        for channel in range(dimensions):
            result.append([[1, vec(value[i + 1])[channel], value[i]] for i in range(0, len(value), 2)])
        return result

    # ParticleForce (DAVA 4.8 "dragForce" family) -> the 11.20 force record
    # DavaFx evaluates; the YAML suffixes every key with the force index.
    FORCE_KEYS = ("forceType", "forceName", "forceIsActive", "worldAlign", "dragForcePosition",
                  "dragForceRotation", "dragForceShape", "dragForceBoxSize", "dragForceRadius",
                  "dragForceInfinityRange", "dragForceTimingType", "forceDirection", "forceWindFreq",
                  "forceWindBias", "windTurbulenceFrequency", "backwardTurbulenceProbability",
                  "pointGravityRadius", "killParticles", "isGlobal", "startTime", "endTime")

    def forces(self, layer: dict) -> list:
        result = []
        for index in range(int(layer.get("dragForceCount", 0) or 0)):
            force = {key: layer[f"{key}{index}"] for key in self.FORCE_KEYS if f"{key}{index}" in layer}
            if f"dragForcePower{index}" in layer:
                force["dragForceLine"] = self.line(layer[f"dragForcePower{index}"], 3)
            if f"forceWindTurb{index}" in layer:
                force["turbulenceLine"] = self.line(layer[f"forceWindTurb{index}"])
            result.append(force)
        return result

    def emitter(self, path: Path) -> dict:
        import yaml
        path = path.resolve()
        key = str(path)
        if key in self.emitters:
            return self.emitters[key]
        self.record(path)
        raw = yaml.safe_load(path.read_text())
        node = dict(raw["emitter"])
        node["type"] = {"point": 0, "rect": 1, "oncircle_volume": 2, "oncircle": 3, "sphere": 4,
                        "oncircle_edges": 3, "shockwave": 3}.get(node.get("type", "point"), 0)
        if raw["emitter"].get("type") == "shockwave":
            node["shockwaveMode"] = 1
        for name in ("emissionAngle", "emissionAngleVariation", "emissionRange", "radius"):
            if name in node:
                node[name] = self.line(node[name])
        node["emissionAngle"] = node.get("emissionAngle", 0)
        node["emissionRange"] = node.get("emissionRange", 0)
        node["radius"] = node.get("radius", 0)
        node["emissionVector"] = self.line(node.get("emissionVector", [0.0, 0.0, 1.0]), 3)
        node["size"] = self.line(node.get("size", [0.0, 0.0, 0.0]), 3)
        node["layers"] = []
        for name, value in raw.items():
            if not name.startswith("layer"):
                continue
            layer = dict(value)
            layer["legacyLod"] = 1  # The full authored tier, not the distant duplicates.
            if layer.get("layerType") == "single":
                layer["layerType"] = "particles"
                layer["isSingle"] = True
            if "pivotPoint" in layer:
                layer["legacyPivot"] = layer["pivotPoint"]
            layer["legacySpinDirection"] = True
            for key in ("alphaOverLife", "life", "lifeVariation", "number", "numberVariation", "velocity",
                        "velocityVariation", "velocityOverLife", "animSpeedOverLife", "stripeLifetime"):
                if key in layer:
                    layer[key] = self.line(layer[key])
            for old, new in (("size", "size3d"), ("sizeVariation", "sizeVariation3d"),
                             ("sizeOverLifeXY", "sizeOverLife3ds")):
                if old in layer:
                    layer[new] = self.line(layer[old], 3)
            for old, new in (("angle", "angle3d"), ("angleVariation", "angleVariation3d"),
                             ("spin", "spin3d"), ("spinVariation", "spinVariation3d")):
                if old in layer:
                    layer[new] = [0, 0, layer[old]]  # DAVA Z -> Unity billboard roll.
            for key in ("colorOverLife", "colorRandom"):
                if key in layer:
                    layer[key] = self.line(layer[key], 4, True)
            # The stripe (torpedo wake) and noise curves are keyframe lists too:
            # left raw, DavaFx read the colour as (0, 1, 0, 1) - a pure green
            # ribbon - and the width envelope as a constant 1.
            if "stripeColorOverLife" in layer:
                layer["stripeColorOverLife"] = self.line(layer["stripeColorOverLife"], 4, True)
            for key in ("stripeSizeOverLifeProp", "stripeTextureTileOverLife", "noiseScaleOverLife",
                        "noiseUScrollSpeedOverLife", "noiseVScrollSpeedOverLife"):
                if key in layer:
                    layer[key] = self.line(layer[key])
            layer["legacyForces"] = [
                {"force": self.line(layer[f"force{i}"], 3), "overLife": self.line(layer.get(f"forceOverLife{i}", 1))}
                for i in range(int(layer.get("forceCount", 0) or 0)) if f"force{i}" in layer]
            layer["forces"] = self.forces(layer)
            if layer.get("sprite"):
                layer["spritePath"] = layer["sprite"]
                self.texture(layer["sprite"])
            if layer.get("innerEmitterPath"):
                layer["innerEmitter"] = self.emitter(path.parent / layer["innerEmitterPath"])
            node["layers"].append(layer)
        self.emitters[key] = node
        return node

    def write(self, name: str, instances: list) -> int:
        target = FX_OUT / "Sources" / f"{name}.json"
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps({"legacyEmitters": instances}, ensure_ascii=False, indent=1) + "\n",
                          encoding="utf-8")
        return len(instances)

    def scene(self, path: Path) -> list:
        import dava
        from port_blitz_client_map import components, items
        self.record(path)
        scene = dava.read_sc2(path.read_bytes())["scene"]
        found = []

        def walk(entity):
            for component in components(entity, "ParticleEffectComponent"):
                for instance in items(component.get("pe.emitters")):
                    found.append({"position": instance.get("emitter.position", [0.0, 0.0, 0.0]),
                                  "emitter": self.emitter(path.parent / instance["emitter.filename"])})
            for child in items(entity.get("#hierarchy")):
                if isinstance(child, dict):
                    walk(child)

        for root in items(scene.get("#hierarchy")):
            if isinstance(root, dict):
                walk(root)
        return found


def dava_to_unity_basis(matrix) -> dict:
    """A DAVA column-vector world matrix as Unity position + look basis.

    Unity = S * M * S with S swapping y and z, which keeps it a rotation."""
    import numpy as np
    m = np.array(matrix, dtype=np.float64).reshape(4, 4)
    swap = np.array([[1, 0, 0], [0, 0, 1], [0, 1, 0]], dtype=np.float64)
    rotation = swap @ m[:3, :3] @ swap
    scale = np.linalg.norm(rotation, axis=0)
    rotation = rotation / np.where(scale > 1e-9, scale, 1.0)
    position = swap @ m[:3, 3]
    return {"position": [round(float(v), 4) for v in position],
            "forward": [round(float(v), 5) for v in rotation[:, 2]],
            "up": [round(float(v), 5) for v in rotation[:, 1]]}


def map_scene():
    import dava
    from audit_blitz_480_tanks import PACK
    path = PACK / "3d/Maps/sea_of_ducks/sea_of_ducks.sc2"
    return path, dava.read_sc2(path.read_bytes())


def port_particles() -> dict:
    import shutil
    from audit_blitz_480_tanks import PACK
    from port_blitz_client_map import components, items, matrix_of, named_entities

    sys.path.insert(0, str(GODOT / "tools"))
    if FX_OUT.is_dir():
        shutil.rmtree(FX_OUT)
    porter = FxPorter(PACK)
    scenes = {}
    for path in sorted((PACK / EVENT_FX).rglob("*.sc2")):
        name = str(path.relative_to(PACK / EVENT_FX).with_suffix(""))
        scenes[name] = porter.write(name, porter.scene(path))

    # The map's own ParticleEffectComponents (fish, duck, seagull, fire)
    # reference their YAML straight from the scene; each becomes map/<name>.
    scene_path, scene = map_scene()
    porter.record(scene_path)
    effects = []
    sounds = []
    for index, (name, entity) in enumerate(named_entities(scene)):
        placement = None
        for component in components(entity, "ParticleEffectComponent"):
            instances = [{"position": instance.get("emitter.position", [0.0, 0.0, 0.0]),
                          "emitter": porter.emitter(scene_path.parent / instance["emitter.filename"])}
                         for instance in items(component.get("pe.emitters"))]
            source = f"map/{name}"
            if source not in scenes:
                scenes[source] = porter.write(source, instances)
            actions = [a for action in components(entity, "ActionComponent") for a in items(action)
                       if isinstance(a, dict) and "act.type" in a]
            start = actions[0] if actions else {}
            placement = placement or dava_to_unity_basis(matrix_of(entity))
            repeats = int(start.get("act.stopAfterNRepeats", component.get("pe.repeatsCount", -1)))
            effects.append({
                "name": name,
                "source": source,
                **placement,
                "duration": float(component.get("pe.effectDuration", 100.0)),
                "repeats": -1 if repeats < 0 or repeats >= 0xFFFFFFFF else repeats,
                "delay": float(start.get("act.delay", 0.0)),
                "delayVariation": float(start.get("act.delayVariation", 0.0)),
                "clearOnRestart": bool(component.get("pe.clearOnRestart", False)),
                "startFromTime": float(component.get("pe.startFromTime", 0.0)),
            })
        for component in components(entity, "SoundComponent"):
            for event in items(component):
                if not isinstance(event, dict) or "eventName" not in event:
                    continue
                placement = placement or dava_to_unity_basis(matrix_of(entity))
                sounds.append({"name": name, "event": event["eventName"],
                               "flags": int(event.get("sce.flags", 0)),
                               "position": placement["position"]})

    FX_MANIFEST.write_text(json.dumps({
        "generator": "Tools/port_world_of_ducks.py particles",
        "scenes": scenes,
        "textures": porter.textures,
        "provenance": dict(sorted(porter.provenance.items())),
    }, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    document = load_mode()
    document["fx"] = {"manifest": "Data/world_of_ducks_fx_manifest", "root": FX_ROOT,
                      "scenes": sorted(scenes)}
    document["mapEffects"] = effects
    document["mapSounds"] = sounds
    document["effects"] = vehicle_effects(PACK)
    save_mode(document)
    print(json.dumps({"scenes": len(scenes), "textures": len(porter.textures), "mapEffects": len(effects),
                      "mapSounds": len(sounds)}, ensure_ascii=False))
    return document["fx"]


def event_fx(pack: Path, reference: str) -> str | None:
    """`~res:/3d/FX/<path>.sc2` -> the event source name the runtime loads."""
    if not reference:
        return None
    rel = reference.replace("~res:/", "").replace("3d/FX/", "", 1)
    rel = rel.replace("sea_of_ducks/", "", 1) if rel.startswith("sea_of_ducks/") else rel
    if (pack / EVENT_FX / rel).is_file():
        return str(Path(rel).with_suffix(""))
    return None


def vehicle_effects(pack: Path) -> dict:
    """gun_effects.xml / shot_effects.xml rows of the three duck guns."""
    from xml.etree import ElementTree as ET

    def text(node, path):
        found = node.find(path)
        return (found.text or "").strip() if found is not None and found.text else ""

    common = pack / "XML/item_defs/vehicles/common"
    guns_xml = ET.parse(pack / "XML/item_defs/vehicles/other/components/guns.xml").getroot()
    shells_xml = ET.parse(pack / "XML/item_defs/vehicles/other/components/shells.xml").getroot()
    gun_fx = ET.parse(common / "gun_effects.xml").getroot()
    shot_fx = ET.parse(common / "shot_effects.xml").getroot()
    guns = {}
    for gun in ("_Duck_Watergun_1", "_Duck_Watergun_2", "_Duck_Watergun_3"):
        node = [n for n in guns_xml.find("shared") if n.tag == gun][0] if guns_xml.find("shared") is not None \
            else [n for n in guns_xml.iter(gun) if len(n)][0]
        key = text(node, "effects")
        row = gun_fx.find(key)
        shell = list(node.find("shots"))[0].tag
        guns[gun] = {
            "effects": key,
            "shot": event_fx(pack, text(row, "blitz/effect")),
            "soundPC": text(row, "blitz/soundPC"),
            "soundNPC": text(row, "blitz/soundNPC"),
            "shell": shell,
        }
    shells = {}
    for shell in sorted({g["shell"] for g in guns.values()}):
        node = shells_xml.find(shell)
        key = text(node, "effects")
        row = shot_fx.find(key)
        hits = {}
        for hit in row if row is not None else []:
            blitz = hit.find("blitz")
            if blitz is None:
                continue
            hits[hit.tag] = {k: v for k, v in {
                "effect": event_fx(pack, text(blitz, "effect")),
                "objectEffect": event_fx(pack, text(blitz, "objectEffect")),
                "sound": text(blitz, "sound"),
                "soundPC": text(blitz, "soundPC"),
                "soundNPC": text(blitz, "soundNPC"),
            }.items() if v}
        shells[shell] = {"effects": key, "hits": hits}
    vehicle = ET.parse(pack / "XML/item_defs/vehicles/other" / f"{VEHICLE_ID}.xml").getroot()
    return {
        "guns": guns,
        "shells": shells,
        "smoke": event_fx(pack, "3d/" + text(vehicle, "blitz/smokeEffectPath")),
        "flamingDead": event_fx(pack, "3d/" + text(vehicle, "blitz/fireEffectPathDead")),
        "flamingAlive": event_fx(pack, "3d/" + text(vehicle, "blitz/fireEffectPathAlive")),
        "explosion": event_fx(pack, "3d/FX/explosion_tank_1.sc2"),
        "collision": event_fx(pack, "3d/FX/sea_of_ducks/collision.sc2"),
        "ramming": event_fx(pack, "3d/FX/sea_of_ducks/ramming_effect.sc2"),
        "chassisMud": event_fx(pack, "3d/FX/sea_of_ducks/chassis_effects/dirt_water_m.sc2"),
        "chassisDust": event_fx(pack, "3d/FX/sea_of_ducks/chassis_effects/dust_water_m.sc2"),
        "chassisSound": text(vehicle, "chassis/Chassis_WarDuck/soundPC") or "world_of_ducks/treds_surf/treds_surf",
    }


# --------------------------------------------------------------------- ui
UI_OUT = RESOURCES / "Textures/WorldOfDucks"
UI_SPRITES = {
    "hangar_btn": "Gfx/UI/Story/world_of_ducks/hangar_btn.txt",
    "btn_back": "Gfx/UI/Story/world_of_ducks/btn_back.txt",
    "warduck_big": "Gfx2/UI/BigTankIcons/WarDuck.txt",
    "warduck_small": "Gfx2/UI/BattleScreenHUD/SmallTankIcons/WarDuck.txt",
    "warduck_storage": "Gfx2/UI/StorageItemIcons/WarDuck/WarDuck.txt",
}


def sprite_frame(pack: Path, descriptor: Path):
    """Frame 0 of a DAVA sprite descriptor, cut out of its atlas page."""
    from PIL import Image
    lines = [line.strip() for line in descriptor.read_text().splitlines() if line.strip()]
    sheets = int(lines[0])
    atlases = lines[1:1 + sheets]
    frames = int(lines[2 + sheets])
    if frames < 1:
        raise SystemExit(f"{descriptor} has no frame")
    x, y, w, h, _ox, _oy, page = map(int, lines[3 + sheets].split()[:7])
    atlas_stem = Path(atlases[page])
    atlas = descriptor.parent / atlas_stem.with_suffix(".mali.webp")
    if not atlas.is_file():
        # Small icons share one page with every vehicle of the folder.
        candidates = list(descriptor.parent.glob(atlas_stem.stem + "*.mali.webp"))
        if not candidates:
            raise SystemExit(f"atlas missing for {descriptor}")
        atlas = candidates[0]
    image = Image.open(atlas).convert("RGBA").crop((x, y, x + w, y + h))
    return image, atlas


def port_ui() -> dict:
    from audit_blitz_480_tanks import PACK
    UI_OUT.mkdir(parents=True, exist_ok=True)
    entries = {}
    for name, rel in UI_SPRITES.items():
        descriptor = PACK / rel
        image, atlas = sprite_frame(PACK, descriptor)
        target = UI_OUT / f"{name}.png"
        image.save(target)
        entries[name] = {
            "texture": f"Textures/WorldOfDucks/{name}",
            "source": rel,
            "source_sha256": sha(descriptor),
            "atlas": str(atlas.relative_to(PACK)),
            "atlas_sha256": sha(atlas),
            "width": image.width,
            "height": image.height,
        }
    entries["mode_card"] = compose_mode_card()
    document = load_mode()
    document["ui"] = entries
    save_mode(document)
    print(json.dumps({name: [e["width"], e["height"]] for name, e in entries.items()}))
    port_atlas()
    return entries


# The battle flow names a card's icons after its lowercase alphanumeric name:
# the team lists and the scoreboard take flow_tank_<id>_small
# (BattleFlowUi.SmallTankIconRegion), the queue tank_icon_<id>. The event card
# wod_warduck shows the 4.8 WarDuck art of its own pack, cut by ``ui``.
ATLAS_ICONS = {
    "flow_tank_wodwarduck_small": "warduck_small",
    "tank_icon_wodwarduck": "warduck_big",
}


def port_atlas() -> dict:
    """Adds the event card's icons to the shared atlas table in place: every
    other region keeps its position, so the table diff stays the event's own."""
    entries = load_mode().get("ui")
    if not entries:
        raise SystemExit("port the ui step first")
    table = json.loads(ATLAS.read_text(encoding="utf-8"))
    added = {}
    for region, name in ATLAS_ICONS.items():
        entry = entries[name]
        texture = entry["texture"].removeprefix("Textures/")
        if not (RESOURCES / "Textures" / (texture + ".png")).is_file():
            raise SystemExit(f"missing {texture}.png: port the ui step first")
        table["textures"][texture] = {"width": entry["width"], "height": entry["height"]}
        table["regions"][region] = {
            "texture": texture, "x": 0, "y": 0, "w": entry["width"], "h": entry["height"],
        }
        added[region] = [entry["width"], entry["height"]]
    ATLAS.write_text(json.dumps(table, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    # HangarSliceTests compare the hangar manifest with the shared table.
    manifest = json.loads(HANGAR_MANIFEST.read_text(encoding="utf-8"))
    manifest["atlas"]["textures"] = len(table["textures"])
    manifest["atlas"]["regions"] = len(table["regions"])
    HANGAR_MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(added))
    return added


# Mode picker cards are 512 x 1024 portraits (Resources/Uprising/Modes/*).
MODE_CARD_SIZE = (512, 1024)
MAP_LOADING_SCREEN = RESOURCES / "Maps/SeaOfDucks/loading_screen.png"


def compose_mode_card() -> dict:
    """The mode picker card: the 4.8 arena's own loading screen with the
    4.8 big WarDuck icon on it. The event shipped no picker art of its own
    (``Gfx/UI/Story/world_of_ducks`` holds two buttons), so the card is
    composed from those two 4.8 images only - nothing is painted."""
    from PIL import Image

    if not MAP_LOADING_SCREEN.is_file():
        raise SystemExit("port the map first: " + str(MAP_LOADING_SCREEN))
    width, height = MODE_CARD_SIZE
    base = Image.open(MAP_LOADING_SCREEN).convert("RGB")
    scale = max(width / base.width, height / base.height)
    resized = base.resize((round(base.width * scale), round(base.height * scale)), Image.LANCZOS)
    left = (resized.width - width) // 2
    top = (resized.height - height) // 2
    card = resized.crop((left, top, left + width, top + height)).convert("RGBA")
    duck = Image.open(UI_OUT / "warduck_big.png").convert("RGBA")
    duck_scale = 400 / duck.width
    duck = duck.resize((round(duck.width * duck_scale), round(duck.height * duck_scale)), Image.LANCZOS)
    card.alpha_composite(duck, ((width - duck.width) // 2, 150))
    target = UI_OUT / "mode_card.png"
    card.convert("RGB").save(target)
    return {
        "texture": "Textures/WorldOfDucks/mode_card",
        "composedFrom": ["Maps/SeaOfDucks/loading_screen", "Textures/WorldOfDucks/warduck_big"],
        "loading_screen_sha256": sha(MAP_LOADING_SCREEN),
        "width": width,
        "height": height,
    }


def port_catalog() -> dict:
    """Applies world_of_ducks_registry to tanks.json in the base porters' format."""
    from world_of_ducks_registry import merge_into

    document = merge_into(json.loads(TANKS.read_text(encoding="utf-8")))
    TANKS.write_text(json.dumps(document, ensure_ascii=False, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    marked = document.get("event_vehicles", {})
    print(json.dumps(marked))
    return marked


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("steps", nargs="*", default=["card", "model"])
    parser.add_argument("--rebuild", action="store_true")
    args = parser.parse_args()
    for step in args.steps:
        if step == "card":
            port_card_data()
        elif step == "model":
            port_model(args.rebuild)
        elif step == "sounds":
            port_sounds()
        elif step == "particles":
            port_particles()
        elif step == "ui":
            port_ui()
        elif step == "atlas":
            port_atlas()
        elif step == "catalog":
            port_catalog()
        else:
            raise SystemExit(f"unknown step {step}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
