#!/usr/bin/env python3
"""Rebuild the reviewed thin-armour island list from a Unity test log.

`TankArmorPlausibilityTests.Armor_HasNoUnreviewedThinIslands` logs every
island it finds on a tank that fails, as `[ArmorIslands] <tank> {...} {...}`.
After an export changed and the new islands were checked against the client
data, feed that log here to refresh the list the test reads:

    python3 Tools/armor_islands_review.py Logs/unity-test.log

Islands of tanks the log does not mention keep their entries. A reason given
by hand survives as long as the island stays on the same face.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REVIEWED = ROOT / "Assets/Blitz/Tests/Data/armor_thin_islands_reviewed.json"
MANIFEST = ROOT / "Assets/Blitz/Resources/Data/tank_models_source_manifest.json"
LINE = re.compile(r"\[ArmorIslands\] (\S+) (.*)$")
ISLAND = re.compile(r"\{\"config\".*?\}")


def reason(tank: str, island: dict, packs: dict) -> str:
    source = "11.20 XML plate" if packs.get(tank) else "4.8.0 collision face"
    if island["min"] == 0:
        return f"authored 0 mm {source}"
    return f"authored thin {source}"


def main(log: Path) -> None:
    current = json.loads(REVIEWED.read_text()) if REVIEWED.exists() else {"tanks": {}}
    packs = {name: entry.get("original_pack")
             for name, entry in json.loads(MANIFEST.read_text())["tanks"].items()}
    found = {}
    for line in log.read_text(errors="replace").splitlines():
        match = LINE.search(line)
        if match:
            found[match.group(1)] = [json.loads(text) for text in ISLAND.findall(match.group(2))]
    tanks = dict(current.get("tanks", {}))
    for tank, islands in found.items():
        old = {(e["config"], e["dir"], tuple(e["plates"])): e.get("reason") for e in tanks.get(tank, [])}
        entries = []
        for island in islands:
            key = (island["config"], island["dir"], tuple(island["plates"]))
            island["reason"] = old.get(key) or reason(tank, island, packs)
            entries.append(island)
        if entries:
            tanks[tank] = entries
        else:
            tanks.pop(tank, None)
    current["tanks"] = dict(sorted(tanks.items()))
    lines = ["{", '  "tanks": {']
    names = list(current["tanks"])
    for i, tank in enumerate(names):
        lines.append(f'    "{tank}": [')
        entries = current["tanks"][tank]
        for j, entry in enumerate(entries):
            text = json.dumps(entry, separators=(", ", ": "))
            lines.append(f"      {text}{',' if j + 1 < len(entries) else ''}")
        lines.append(f"    ]{',' if i + 1 < len(names) else ''}")
    lines += ["  }", "}"]
    REVIEWED.write_text("\n".join(lines) + "\n")
    print(f"{sum(len(v) for v in current['tanks'].values())} islands on {len(names)} tanks")


if __name__ == "__main__":
    main(Path(sys.argv[1]))
