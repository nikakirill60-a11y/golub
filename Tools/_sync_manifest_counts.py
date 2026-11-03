#!/usr/bin/env python3
"""Re-sync the hangar source manifest counters with the tables it describes.

Every porter appends to atlas_regions.json / lang.json, and
HangarSliceTests.SourceManifest_DescribesExactCurrentModelSlice compares the
manifest counters against those tables. Running the full hangar porter only to
refresh three integers would also rewrite unrelated artwork, so this keeps the
counters honest on their own.
"""

from __future__ import annotations

import json
from pathlib import Path

RESOURCES = Path(__file__).resolve().parents[1] / "Assets/Blitz/Resources/Data"


def main() -> None:
    manifest_path = RESOURCES / "hangar_source_manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    atlas = json.loads((RESOURCES / "atlas_regions.json").read_text(encoding="utf-8"))
    lang = json.loads((RESOURCES / "lang.json").read_text(encoding="utf-8"))

    manifest["atlas"]["regions"] = len(atlas["regions"])
    manifest["atlas"]["textures"] = len(atlas["textures"])
    manifest["localization"]["keys"] = len(lang["keys"])
    manifest["localization"]["languages"] = len(lang["languages"])

    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(
        "regions={regions} textures={textures} keys={keys}".format(
            regions=manifest["atlas"]["regions"],
            textures=manifest["atlas"]["textures"],
            keys=manifest["localization"]["keys"],
        )
    )


if __name__ == "__main__":
    main()
