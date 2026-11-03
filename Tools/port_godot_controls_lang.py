#!/usr/bin/env python3
"""Copies the controls:* rows of the source lang_extra.csv into lang.json.

The Godot project keeps its translations in CSV files that Godot imports as
.translation resources; the port reads Resources/Data/lang.json instead. Only
the keys the controls tab needs are copied, with the source text untouched.
"""
import csv
import json
import os
import sys

SOURCE = "/home/smh01/Studio canaryProjects/Blitz Legacy/lang_extra.csv"
TARGET = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "Assets/Blitz/Resources/Data/lang.json",
)
PREFIX = "controls:"


def main():
    with open(SOURCE, newline="", encoding="utf-8") as handle:
        rows = list(csv.reader(handle))

    header = rows[0]
    en = header.index("en")
    ru = header.index("ru")

    with open(TARGET, encoding="utf-8") as handle:
        lang = json.load(handle)

    keys = lang["keys"]
    added = 0
    for row in rows[1:]:
        if not row or not row[0].startswith(PREFIX):
            continue
        keys[row[0]] = {"en": row[en], "ru": row[ru] or row[en]}
        added += 1

    lang["keys"] = dict(sorted(keys.items()))
    with open(TARGET, "w", encoding="utf-8") as handle:
        json.dump(lang, handle, ensure_ascii=False, indent=2)
        handle.write("\n")

    print("copied %d controls keys" % added)
    return 0


if __name__ == "__main__":
    sys.exit(main())
