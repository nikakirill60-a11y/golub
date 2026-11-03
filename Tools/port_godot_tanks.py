#!/usr/bin/env python3
"""Export Godot tank cards (.tres) into a deterministic Unity data table.

The Godot project is the single source of truth: this script only reads the
cards and rewrites them, it never invents values. Sub-resources (guns, shells,
engines, tracks) are inlined in their source order so the Unity side can keep
the same module indices Godot uses for install ids.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
from pathlib import Path

SCRIPT_KINDS = {
    "module_gun.gd": "gun",
    "module_engine.gd": "engine",
    "module_tracks.gd": "tracks",
    "shell_info.gd": "shell",
    "burst_info.gd": "burst",
    "tank_info.gd": "tank",
}

HEADER = re.compile(r"^\[(?P<kind>\w+)(?P<attrs>[^\]]*)\]\s*$")
ATTR = re.compile(r'(\w+)="([^"]*)"')
VECTOR = re.compile(r"^Vector([234])i?\((.*)\)$")
SUB = re.compile(r'^SubResource\("([^"]+)"\)$')
EXT = re.compile(r'^ExtResource\("([^"]+)"\)$')
ARRAY = re.compile(r"^Array\[.*?\]\((\[.*\])\)$", re.S)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def parse_value(raw: str):
    text = " ".join(raw.split())
    if text.startswith('&"'):
        text = text[1:]
    if text.startswith('"') and text.endswith('"'):
        return text[1:-1]
    if text in ("true", "false"):
        return text == "true"
    if text == "null":
        return None
    match = ARRAY.match(text)
    if match:
        inner = match.group(1)[1:-1].strip()
        if not inner:
            return []
        return [parse_value(item) for item in split_top_level(inner)]
    match = VECTOR.match(text)
    if match:
        parts = [parse_value(item) for item in split_top_level(match.group(2))]
        return dict(zip("xyzw", parts))
    match = SUB.match(text)
    if match:
        return {"__sub__": match.group(1)}
    match = EXT.match(text)
    if match:
        return {"__ext__": match.group(1)}
    if text.startswith("{"):
        body = text[1:-1].strip()
        result = {}
        if body:
            for item in split_top_level(body):
                key, _, value = item.partition(":")
                result[parse_value(key.strip())] = parse_value(value.strip())
        return result
    if text.startswith("["):
        body = text[1:-1].strip()
        return [parse_value(item) for item in split_top_level(body)] if body else []
    try:
        return int(text)
    except ValueError:
        pass
    try:
        return float(text)
    except ValueError:
        return text


def split_top_level(text: str) -> list[str]:
    parts: list[str] = []
    depth = 0
    quoted = False
    current = ""
    for char in text:
        if char == '"':
            quoted = not quoted
        if not quoted:
            if char in "([{":
                depth += 1
            elif char in ")]}":
                depth -= 1
            elif char == "," and depth == 0:
                parts.append(current.strip())
                current = ""
                continue
        current += char
    if current.strip():
        parts.append(current.strip())
    return parts


def balanced(text: str) -> bool:
    depth = 0
    quoted = False
    for char in text:
        if char == '"':
            quoted = not quoted
        if quoted:
            continue
        if char in "([{":
            depth += 1
        elif char in ")]}":
            depth -= 1
    return depth <= 0 and not quoted


def parse_tres(path: Path) -> dict:
    sections: list[dict] = []
    current: dict | None = None
    pending_key: str | None = None
    pending_value = ""
    for line in path.read_text(encoding="utf-8").splitlines():
        if pending_key is not None:
            pending_value += "\n" + line
            if balanced(pending_value):
                current["values"][pending_key] = parse_value(pending_value)
                pending_key = None
                pending_value = ""
            continue
        header = HEADER.match(line)
        if header:
            current = {
                "kind": header.group("kind"),
                "attrs": dict(ATTR.findall(header.group("attrs"))),
                "values": {},
            }
            sections.append(current)
            continue
        if current is None or " = " not in line:
            continue
        key, _, value = line.partition(" = ")
        key = key.strip()
        if balanced(value):
            current["values"][key] = parse_value(value)
        else:
            pending_key = key
            pending_value = value
    return {"sections": sections}


def build_card(path: Path) -> dict:
    parsed = parse_tres(path)
    ext_kind: dict[str, str] = {}
    for section in parsed["sections"]:
        if section["kind"] != "ext_resource":
            continue
        source = section["attrs"].get("path", "")
        kind = SCRIPT_KINDS.get(Path(source).name)
        if kind:
            ext_kind[section["attrs"]["id"]] = kind
    subs = {
        section["attrs"]["id"]: section
        for section in parsed["sections"]
        if section["kind"] == "sub_resource"
    }

    def resolve(value):
        if isinstance(value, list):
            return [resolve(item) for item in value]
        if isinstance(value, dict):
            if "__sub__" in value:
                return resolve_sub(value["__sub__"])
            if "__ext__" in value:
                return None
            return {key: resolve(item) for key, item in value.items()}
        return value

    def resolve_sub(sub_id: str):
        section = subs.get(sub_id)
        if section is None:
            return None
        values = dict(section["values"])
        script = values.pop("script", None)
        kind = None
        if isinstance(script, dict) and "__ext__" in script:
            kind = ext_kind.get(script["__ext__"])
        resolved = {key: resolve(item) for key, item in values.items()}
        if kind:
            resolved["kind"] = kind
        return resolved

    main = next(
        section for section in parsed["sections"] if section["kind"] == "resource"
    )
    card = {key: resolve(value) for key, value in main["values"].items() if key != "script"}
    card["source"] = str(path).split("Blitz Legacy/", 1)[-1]
    card["source_sha256"] = sha256(path)
    return card


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path, help="Godot project root")
    parser.add_argument("--destination", required=True, type=Path, help="Unity project root")
    parser.add_argument("--cards", required=True, nargs="+", help="card stems under assets/tanks")
    args = parser.parse_args()

    cards = [build_card(args.source / "assets/tanks" / f"{stem}.tres") for stem in args.cards]
    cards.sort(key=lambda card: (card.get("lvl", 1), card.get("name", "")))
    table = {"tanks": cards}
    # Superseded by Tools/port_blitz_480_data.py, which owns the live
    # tanks.json for all 342 cards of client 4.8.0. Writing the old table here
    # would silently drop every premium, event and collector vehicle.
    if os.environ.get("BLITZ_ALLOW_LEGACY_TANKS", "") not in ("1", "true", "yes"):
        raise SystemExit(
            "refusing to overwrite tanks.json: run Tools/port_blitz_480_data.py "
            "instead, or set BLITZ_ALLOW_LEGACY_TANKS=1")
    out = args.destination / "Assets/Blitz/Resources/Data/tanks.json"
    out.write_text(
        json.dumps(table, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(f"tanks={len(cards)} bytes={out.stat().st_size}")
    for card in cards:
        print(
            f"  lvl={card.get('lvl', 1):>2} {card['name']:<10}"
            f" guns={len(card.get('guns') or [])}"
            f" engines={len(card.get('engines') or [])}"
            f" tracks={len(card.get('tracks') or [])}"
            f" unlocks={sorted((card.get('unlock_tanks') or {}).keys())}"
        )


if __name__ == "__main__":
    main()
