#!/usr/bin/env python3
"""Rebuild the Soviet MS-1 -> T-62A branch from the Blitz 4.8.0 client files.

Source of truth for the *structure* of the branch:

    /home/smh01/Studio canaryProjects/BlitzPACKSold/XML/item_defs/vehicles/ussr

The client keeps every gun **inside** the turret that can mount it
(`turrets0/<turret>/guns/<gun>`), which is exactly the compatibility rule the
owner asked for: the stock turret cannot carry the top gun, and the same gun
reloads slower in the stock turret (`reloadTime` and `maxAmmo` are authored per
turret). Research is the `unlocks` graph: a module unlocks other modules and
the next vehicles, each edge carrying its XP cost.

What this script takes from where:

* turrets, their guns, engines, chassis, radios, the unlock graph, the XP
  costs and the module -> mesh mapping (`blitz/blitzPartName`): client XML;
* turret rotation speed, health, weight, view range, yaw limits: client XML
  (the port had no turret module at all, so these values are new);
* per-turret reload and ammo: client XML;
* every other gun, shell, engine and chassis number: the card that already
  ships in `Assets/Blitz/Resources/Data/tanks.json`, so the balance the branch
  already has does not move. Only modules the cards never had fall back to the
  client numbers;
* Russian module names: `from_apk/Data/Strings/ru.yaml` of the same client.

Run:  python3 Tools/port_blitz_branch.py [--check]
"""

from __future__ import annotations

import argparse
import json
import os
import re
from pathlib import Path
from xml.etree import ElementTree as ET

from gun_shot_sounds import shot_sound_of

# The client pack has lived under two roots on this machine. Keep the path
# overridable instead of hardcoding one of them: `BLITZ_PACK` wins, otherwise
# the first root that actually exists is used.
def _pack_root() -> Path:
    override = os.environ.get("BLITZ_PACK")
    if override:
        return Path(override)
    candidates = (
        Path.home() / "Studio canaryProjects" / "BlitzPACKSold",
        Path.home() / "projects" / "wotblitz4.8.0files",
    )
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return candidates[0]


PACK = _pack_root()
VEHICLES = PACK / "XML/item_defs/vehicles/ussr"
STRINGS = PACK / "from_apk/Data/Strings/ru.yaml"
UNITY = Path(__file__).resolve().parent.parent
TANKS_JSON = UNITY / "Assets/Blitz/Resources/Data/tanks.json"

# card name in the port -> vehicle file in the client
BRANCH = [
    ("ms-1", "MS-1"),
    ("bt-2", "BT-2"),
    ("bt-7", "BT-7"),
    ("a-20", "A-20"),
    ("t-34", "T-34"),
    ("t-34-85", "T-34-85"),
    ("t-43", "T-43"),
    ("t-44", "T-44"),
    ("t-54", "T-54"),
    ("t62a", "T62A"),
]

SHELL_KIND = {
    "ARMOR_PIERCING": 0,
    "ARMOR_PIERCING_CR": 1,
    "HOLLOW_CHARGE": 2,
    "HIGH_EXPLOSIVE": 3,
}

# ModuleGun.ShootSound: the report the gun's <effects> entry names in
# gun_effects.xml, the calibre guess only when the port has no such report.
def shot_sound(gun, caliber: int, magazine: bool) -> int:
    sound = shot_sound_of(gun, VEHICLES.parent)
    if sound is not None:
        return sound
    if magazine:
        return 0
    if caliber <= 45:
        return 1
    if caliber <= 76:
        return 2
    if caliber <= 100:
        return 3
    if caliber <= 130:
        return 4
    return 5


def text(node, default=None):
    if node is None:
        return default
    value = " ".join("".join(node.itertext()).split())
    return value or default


def number(node, default=0.0):
    value = text(node)
    if value is None:
        return default
    try:
        return float(value.split()[0])
    except ValueError:
        return default


def load_strings() -> dict:
    table = {}
    pattern = re.compile(r'^"([^"]+)"\s*:\s*"(.*)"\s*$')
    for line in STRINGS.read_text(encoding="utf-8", errors="replace").splitlines():
        match = pattern.match(line.strip())
        if match:
            table[match.group(1)] = match.group(2)
    return table


class Client:
    """The client XML of one nation, resolved the way the game resolves it."""

    def __init__(self):
        self.strings = load_strings()
        self.shared = {}
        for kind in ("guns", "turrets", "chassis", "engines", "radios"):
            root = ET.parse(VEHICLES / "components" / (kind + ".xml")).getroot()
            shared = root.find("shared")
            self.shared[kind] = {c.tag: c for c in (shared if shared is not None else [])}
        self.shells = {
            c.tag: c for c in ET.parse(VEHICLES / "components/shells.xml").getroot()
        }

    def name_of(self, node, fallback: str) -> str:
        key = text(node.find("userString"))
        if key and key in self.strings:
            return self.strings[key]
        return fallback

    def merged(self, kind: str, node):
        """Vehicle-local overrides on top of the shared component definition."""
        base = self.shared[kind].get(node.tag)
        if base is None:
            return node
        merged = ET.Element(node.tag)
        for child in list(base) + list(node):
            existing = merged.find(child.tag)
            if existing is not None:
                merged.remove(existing)
            merged.append(child)
        return merged


def unlocks_of(node) -> dict:
    """`unlocks` of one module: {"turret:T-18_mod": 170, "vehicle:BT-2": 550}."""
    out = {}
    block = node.find("unlocks")
    if block is None:
        return out
    for entry in block:
        target = (entry.text or "").strip()
        if not target:
            continue
        cost = int(round(number(entry.find("cost"))))
        out["%s:%s" % (entry.tag, target)] = cost
    return out


def card_index(cards: list) -> dict:
    """Every gun/engine/chassis the current cards ship, by module name."""
    index = {"gun": {}, "engine": {}, "tracks": {}}
    for card in cards:
        for key, kind in (("guns", "gun"), ("engines", "engine"), ("tracks", "tracks")):
            for module in card.get(key, []):
                index[kind].setdefault(module["name"], module)
    return index


def client_shells(client: Client, gun) -> list:
    shells = []
    shots = gun.find("shots")
    for shot in shots if shots is not None else []:
        info = client.shells.get(shot.tag)
        if info is None:
            continue
        price = info.find("price")
        gold = price is not None and price.find("gold") is not None
        cost = int(round(number(price)))
        caliber = int(round(number(info.find("caliber"))))
        entry = {
            "kind": "shell",
            "name": client.name_of(info, shot.tag),
            "caliber": caliber,
            "dmg": int(round(number(info.find("damage/armor")))),
            "pen": int(round(number(shot.find("piercingPower")))),
            "speed": int(round(number(shot.find("speed")))),
            "cost": cost * 400 if gold else cost,
        }
        shell_type = SHELL_KIND.get(text(info.find("kind"), ""), 0)
        if shell_type:
            entry["type"] = shell_type
        if gold:
            entry["premium"] = True
        max_distance = number(shot.find("maxDistance"), 720.0)
        if abs(max_distance - 720.0) > 0.01:
            entry["max_dist"] = max_distance
        shells.append(entry)
    return shells


def client_gun(client: Client, gun, card) -> dict:
    """A gun the current cards never had: build it from the client alone."""
    pitch = (text(gun.find("pitchLimits"), "-20 8") or "-20 8").split()
    caliber = 0
    match = re.match(r"_?(\d+)", gun.tag)
    if match:
        caliber = int(match.group(1))
    clip = gun.find("clip")
    burst = {"kind": "burst"}
    fire_mode = 0
    if clip is not None:
        count = int(round(number(clip.find("count"), 1)))
        rate = number(clip.find("rate"), 1.0)
        burst = {
            "kind": "burst",
            "bursts": [count],
            "intraclip": round(60.0 / rate, 6) if rate else 0.12,
            "intraburst": round(60.0 / rate, 6) if rate else 0.12,
        }
        fire_mode = 2
    entry = {
        "kind": "gun",
        "name": gun.tag,
        "display_name": client.name_of(gun, gun.tag),
        "level": int(round(number(gun.find("level"), 1))),
        "caliber": caliber,
        "weight": number(gun.find("weight")),
        "health": number(gun.find("maxHealth")),
        "reload": round(number(gun.find("reloadTime")), 3),
        "aim_time": round(number(gun.find("aimingTime"), 2.0), 3),
        "disp": round(number(gun.find("shotDispersionRadius")), 3),
        "rot_speed": number(gun.find("rotationSpeed")),
        "max_ammo": int(round(number(gun.find("maxAmmo")))),
        "deviation_up": {
            "x": abs(float(pitch[0])),
            "y": abs(float(pitch[1])),
        },
        "disp_factors": {
            "x": number(gun.find("shotDispersionFactors/turretRotation"), 0.1),
            "y": number(gun.find("shotDispersionFactors/afterShot"), 3.0),
            "z": number(gun.find("shotDispersionFactors/whileGunDamaged"), 2.0),
        },
        "impulse_mul": number(gun.find("impulse"), 1.0),
        "fire_mode": fire_mode,
        "burst_info": burst,
        "shot_sound": shot_sound(gun, caliber, fire_mode != 0),
        "shells": client_shells(client, gun),
    }
    if not entry["fire_mode"]:
        del entry["fire_mode"]
    return entry


def build(check: bool) -> int:
    client = Client()
    document = json.loads(TANKS_JSON.read_text(encoding="utf-8"))
    cards = document["tanks"]
    by_name = {card["name"]: card for card in cards}
    known = card_index(cards)

    report = []
    for card_name, file_name in BRANCH:
        card = by_name[card_name]
        root = ET.parse(VEHICLES / (file_name + ".xml")).getroot()

        # ---- guns: the union of every gun the turrets of this tank mount
        guns = {}
        turrets = []
        for turret in root.find("turrets0"):
            merged_turret = client.merged("turrets", turret)
            turret_guns = []
            for gun in turret.find("guns"):
                merged_gun = client.merged("guns", gun)
                if gun.tag not in guns:
                    base = known["gun"].get(gun.tag)
                    if base is None:
                        base = client_gun(client, merged_gun, card)
                        report.append(
                            "%s: gun %s taken from the client (no card had it)"
                            % (card_name, gun.tag)
                        )
                    guns[gun.tag] = json.loads(json.dumps(base))
                turret_guns.append(
                    {
                        "name": gun.tag,
                        # reloadTime and maxAmmo are authored per turret.
                        "reload": round(number(merged_gun.find("reloadTime")), 3),
                        "max_ammo": int(round(number(merged_gun.find("maxAmmo")))),
                        "mesh": text(gun.find("blitz/blitzPartName"), ""),
                        "unlocks": unlocks_of(gun),
                    }
                )
            yaw = (text(merged_turret.find("yawLimits"), "-180 180") or "").split()
            turrets.append(
                {
                    "kind": "turret",
                    "name": turret.tag,
                    "display_name": client.name_of(merged_turret, turret.tag),
                    "level": int(round(number(merged_turret.find("level"), 1))),
                    "health": number(merged_turret.find("maxHealth")),
                    "weight": number(merged_turret.find("weight")),
                    "rot_speed": number(merged_turret.find("rotationSpeed")),
                    "spot_radius": number(merged_turret.find("circularVisionRadius")),
                    "yaw_limits": {
                        "x": abs(float(yaw[0])) if len(yaw) == 2 else 180.0,
                        "y": abs(float(yaw[1])) if len(yaw) == 2 else 180.0,
                    },
                    "mesh": text(turret.find("blitz/blitzPartName"), ""),
                    "unlocks": unlocks_of(turret),
                    "guns": turret_guns,
                }
            )

        # ---- chassis (the port calls them tracks), engines and radios
        tracks = []
        for chassis in root.find("chassis"):
            merged = client.merged("chassis", chassis)
            base = known["tracks"].get(chassis.tag)
            entry = json.loads(json.dumps(base)) if base else {
                "kind": "tracks",
                "name": chassis.tag,
                "display_name": client.name_of(merged, chassis.tag),
                "health": number(merged.find("maxHealth")),
                "weight": number(merged.find("weight")),
                "rot_speed": number(merged.find("rotationSpeed")),
                "disp_move_mul": number(
                    merged.find("shotDispersionFactors/vehicleMovement"), 0.2
                ),
                "disp_rotation_mul": number(
                    merged.find("shotDispersionFactors/vehicleRotation"), 0.2
                ),
            }
            if base is None:
                report.append(
                    "%s: chassis %s taken from the client" % (card_name, chassis.tag)
                )
            entry["level"] = int(round(number(merged.find("level"), 1)))
            entry["unlocks"] = unlocks_of(chassis)
            tracks.append(entry)

        engines = []
        for engine in root.find("engines"):
            merged = client.merged("engines", engine)
            base = known["engine"].get(engine.tag)
            entry = json.loads(json.dumps(base)) if base else {
                "kind": "engine",
                "name": engine.tag,
                "display_name": client.name_of(merged, engine.tag),
                "power": number(merged.find("power")),
                "weight": number(merged.find("weight")),
                "health": number(merged.find("maxHealth")),
            }
            if base is None:
                report.append(
                    "%s: engine %s taken from the client" % (card_name, engine.tag)
                )
            entry["level"] = int(round(number(merged.find("level"), 1)))
            entry["unlocks"] = unlocks_of(engine)
            engines.append(entry)

        radios = []
        for radio in root.find("radios"):
            merged = client.merged("radios", radio)
            radios.append(
                {
                    "kind": "radio",
                    "name": radio.tag,
                    "display_name": client.name_of(merged, radio.tag),
                    "level": int(round(number(merged.find("level"), 1))),
                    "weight": number(merged.find("weight")),
                    "health": number(merged.find("maxHealth")),
                    "distance": number(merged.find("distance")),
                    "unlocks": unlocks_of(radio),
                }
            )

        # ---- XP price of every module: the edge that unlocks it
        edges = {}
        for group in (turrets, tracks, engines, radios):
            for module in group:
                edges.update(module.get("unlocks", {}))
        for turret in turrets:
            for gun in turret["guns"]:
                edges.update(gun.get("unlocks", {}))

        def price(kind: str, name: str) -> int:
            return int(edges.get("%s:%s" % (kind, name), 0))

        for turret in turrets:
            turret["cost_xp"] = price("turret", turret["name"])
        for track in tracks:
            track["cost_xp"] = price("chassis", track["name"])
        for engine in engines:
            engine["cost_xp"] = price("engine", engine["name"])
        for radio in radios:
            radio["cost_xp"] = price("radio", radio["name"])
        for name, gun in guns.items():
            gun["cost_xp"] = price("gun", name)

        unlock_tanks = {}
        for key, cost in edges.items():
            if key.startswith("vehicle:"):
                unlock_tanks[key.split(":", 1)[1]] = int(cost)

        # ---- write the card back
        ordered = []
        for turret in turrets:
            for gun in turret["guns"]:
                if gun["name"] not in ordered:
                    ordered.append(gun["name"])
        card["guns"] = [guns[name] for name in ordered]
        card["turrets"] = turrets
        card["tracks"] = tracks
        card["engines"] = engines
        card["radios"] = radios
        card["unlock_tanks"] = unlock_tanks
        # The hull card keeps a copy of the top turret so nothing that still
        # reads the old fields regresses.
        card["turret_rot_speed"] = turrets[-1]["rot_speed"]
        card["turret_health"] = turrets[-1]["health"]
        card["spot_radius"] = turrets[-1]["spot_radius"]
        card["source"] = "BlitzPACKSold/XML/item_defs/vehicles/ussr/%s.xml" % file_name

    text_out = json.dumps(document, ensure_ascii=False, indent=1, sort_keys=True)
    text_out += "\n"
    if check:
        current = TANKS_JSON.read_text(encoding="utf-8")
        print("up to date" if current == text_out else "tanks.json would change")
    else:
        # Superseded by Tools/port_blitz_480_data.py, which owns the live
        # tanks.json for all 342 cards of client 4.8.0. Writing the old table
        # here would silently drop every premium, event and collector vehicle.
        if os.environ.get("BLITZ_ALLOW_LEGACY_TANKS", "") not in ("1", "true", "yes"):
            raise SystemExit(
                "refusing to overwrite tanks.json: run Tools/port_blitz_480_data.py "
                "instead, or set BLITZ_ALLOW_LEGACY_TANKS=1")
        TANKS_JSON.write_text(text_out, encoding="utf-8")
        print("wrote %s" % TANKS_JSON)
    for line in report:
        print("  " + line)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    return build(args.check)


if __name__ == "__main__":
    raise SystemExit(main())
