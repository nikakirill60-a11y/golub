#!/usr/bin/env python3
"""Rebuild Object 907 and fail if any shipped JSON/model/texture bytes drift."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def snapshot():
    resources = ROOT / 'Assets/Blitz/Resources'
    files = list((resources / 'Data').glob('*.json'))
    for suffix in ('*.obj', '*.mtl', '*.png'):
        files.extend((resources / 'Models/Tanks/OBJECT907').rglob(suffix))
    files.extend((resources / 'Textures').rglob('*object907*.png'))
    return {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in files}


def main():
    before = snapshot()
    subprocess.run([sys.executable, str(ROOT / 'Tools/port_blitz_ussr_1120.py'), '--all'],
                   cwd=ROOT, check=True)
    after = snapshot()
    changed = sorted(path for path in before.keys() | after.keys()
                     if before.get(path) != after.get(path))
    print(json.dumps({'files_checked': len(before), 'changed': changed}))
    return bool(changed)


if __name__ == '__main__':
    raise SystemExit(main())
