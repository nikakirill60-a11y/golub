#!/usr/bin/env python3
"""Turn CJK-obfuscated names in a Sentry stack/log back into real C# names.

Usage: Tools/deobfuscate_stack.py [mapping.xml] < stack.txt
Default mapping: Assets/Obfuz/SymbolObfus/symbol-mapping.xml (tracked in git;
each build also copies the one it used next to the player as symbol-mapping.xml).
File:line in Sentry is already correct (IL2CPP source mapping); this only
restores type/method names.
"""
import re, sys, xml.etree.ElementTree as ET
from pathlib import Path

root = Path(__file__).resolve().parent.parent
mapping = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "Assets/Obfuz/SymbolObfus/symbol-mapping.xml"
frames, types = {}, {}
for e in ET.parse(mapping).getroot().iter():
    if e.tag == "type" and e.get("newFullName"):
        types[e.get("newFullName")] = e.get("fullName")
    new, old = e.get("newStackTraceSignature"), e.get("oldStackTraceSignature")
    if new and old:
        # "ns.Type:Method(args)" -> keyed without args; Sentry prints both : and .
        n, o = new.split("(")[0], old.split("(")[0]
        frames[n] = o
        frames[n.replace(":", ".")] = o.replace(":", ".")

han = "\u4e00-\u9fa5"
token = re.compile("[%s][%s./:]*" % (han, han))

def restore(m):
    t = m.group(0).rstrip(".:")
    tail = m.group(0)[len(t):]
    if t in frames: return frames[t] + tail
    if t in types: return types[t] + tail
    return m.group(0)

for line in sys.stdin:
    sys.stdout.write(token.sub(restore, line))
