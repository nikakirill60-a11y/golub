# Panzer War `.modpack` converter

An offline, data-only archive converter. It reads serialized Unity type trees,
meshes, object references and textures through UnityPy; it does not start Unity,
load the SDK DLLs or execute source Lua/C#/native code. Source Unity projects are
not required. Android and Windows *source* bundles are decoded on the Linux host;
the resulting `.blitzmod` contains no Unity AssetBundles and is platform-neutral.

## Install and run

From the Blitz Legacy repository root:

```sh
python3 -m venv .venv-modpack
.venv-modpack/bin/pip install -r Tools/modpack/requirements.txt
OPENBLAS_NUM_THREADS=1 .venv-modpack/bin/python Tools/modpack/convert.py \
  /path/to/Android_Vehicle-Example.modpack \
  --inspect --report build/mods/inspection.json
```

Inspection lists package/engine/platform, source SHA-256 and serialized component
classes. It does **not** claim that the listed features can be converted.

Actual conversion:

```sh
OPENBLAS_NUM_THREADS=1 .venv-modpack/bin/python Tools/modpack/convert.py \
  /path/to/Android_Vehicle-Example.modpack \
  --output build/mods/Example.blitzmod \
  --report build/mods/Example-report.json \
  --overrides /path/to/overrides.json --allow-approximations
```

`--allow-approximations` explicitly accepts native Blitz physics/ballistics,
materials, sounds/effects and internal-module rules. It does not permit arbitrary
script execution or make unsupported gameplay features supported. Without this
flag conversion is strict and blocks. Missing horsepower also blocks: SDK torque
is not horsepower and must never be silently treated as such.

The override format is keyed by **vehicle** GUID, not package GUID:

```json
{
  "c4e19f4e-ce52-4d25-88b5-81a2f5ff602a": {
    "enginePower": 1020,
    "tankClass": 3
  }
}
```

Power is horsepower. Classes are native Blitz enum values: light 0, medium 1,
heavy 2, tank destroyer 3. Use values supplied by the author, not guessed numbers.
If class is omitted, the SDK LT/MT/HT/TD enum is mapped directly. Other SDK
classes require a valid explicit class override. Horsepower has no automatic fallback. The Argonaut
example power/class were independently checked against its authoring provenance;
no source geometry/card is substituted into the converter.

Repeat `--dependency /path/to/Dependency.modpack` for explicitly provided local
dependencies. Dependency GUIDs must resolve; cycles/ambiguities block. Currently
only GUID dependencies without a separate version expression are supported;
new dependency schema/constraint types require an adapter, not blind acceptance.

Batch mode isolates ordinary per-package conversion errors and continues:

```sh
OPENBLAS_NUM_THREADS=1 .venv-modpack/bin/python Tools/modpack/convert.py \
  /path/to/First.modpack /path/to/Second.modpack \
  --output-dir build/mods/converted --report build/mods/batch-report.json \
  --overrides /path/to/overrides.json --allow-approximations
```

Exit code 0 means conversion/inspection succeeded; 2 means at least one package
blocked. Technical diagnostics and per-vehicle adaptation notes are in the JSON
report and the output package's `conversion-report.json`. Outputs are written
atomically; failed conversions preserve a prior output. Never install both source
platform variants as separate tanks: stable package/vehicle GUIDs identify one mod.

## Installing in Blitz

Copy `.blitzmod` into the game's `mods` directory shown on the existing MODS
screen. Reopen the screen, install, and restart. Enable/disable/remove uses the
existing manager. Imported IDs start with `pw_` and cannot replace stock cards;
the existing non-stock tank online ban remains in effect. Updating the same GUID
is supported; downgrades are rejected until the user explicitly removes the newer
installation. A staging/backup directory protects the previous install on failure.
Raw `.modpack` is shown with an instruction to convert it, not passed to Godot's
card scanner. Android file-manager/scoped-storage limits still apply; this change
does not add a SAF picker or broad storage permissions.

## Actual compatibility, not a promise of all mods

Current adapter: `panzerwar-typetree-ground-v1`. Version handling is structural:
required SDK fields, reference-manager layout and object types must be present.
Unknown/stripped/encrypted serialization is rejected, not reconstructed from the
stock game's assets. This is an extensible ground-vehicle adapter, not yet a
universal map/weapon/UI/script importer.

| Feature | Current behavior |
| --- | --- |
| ZIP container + readable package JSON + UnityFS type trees | Supported with size/path/object limits |
| Ground vehicle identity, high/low cooked root, vehicle text data | Supported with explicit GUID/root associations |
| Simple single gun, AP/APCR/HEAT/HE/HESH, yaw/elevation/reload/ammo | Converted to native Blitz data |
| Shared engine/gun/turret object references | Resolved in their originating serialized file context |
| Static visible meshes and baked hierarchy transforms | Supported; winding, normals and UVs converted |
| Mesh armor, screens, track hitboxes, convex physics hull | Supported; plate order and zero thickness retained |
| One body and one track diffuse sheet, PNG icon | Supported; native materials replace SDK shaders |
| Multiple vehicles | Supported when each identity/root/thumbnail resolves unambiguously |
| Local dependencies | GUID closure supported; version-constrained schemas block |
| Source Android/Windows bundles | Both decoded without target-platform Unity runtime |
| SDK physics/FX/audio/internal-module behavior | Explicit native-rule approximation, opt-in required |
| Lua/attached unknown scripts, buffs, multi-fire/non-simple fire | Blocked when active; unused template objects are inventoried, not executed |
| Skinned geometry, extra material roles, non-mesh/reactive armor | Blocked pending dedicated adapters |
| Maps, ships, aircraft, equipment-only packs, UI/sights | Not delivered by this ground adapter; inspectable where type trees exist |
| Encrypted/entitlement packages or stripped type trees | Blocked; no bypass |

The two Argonaut and two Jagbashen delivered platform variants were converted
from their actual binary archives. These are **two vehicles from the same local
SDK pipeline**, not an independent-author/SDK-version compatibility corpus.
Do not infer all-mod compatibility from them. Source-specific bugs and unsupported
features require adapters plus fixtures; native Blitz feature work is needed for
content categories the target engine does not currently expose as mod providers.

## Format and security

Schema 1: `manifest.json`, `conversion-report.json`, and
`vehicles/<guid>/{tank.json,model.glb,nodes.json,armor.json,body.png,tracks.png,icon.png}`.
Every payload is SHA-256 listed. Manifest hashes prove internal integrity, not
publisher authenticity. Package identity and mod version are separate from schema.
The normalized GLB has flat, world-baked mesh nodes. Sidecar nodes carry native
legacy OBJ-space pivots; the runtime's X/winding/UV conversion must remain paired
with the converter. Native battle code subsequently applies its normal mirror.

Limits include 256 MiB input/entry, 512 MiB unpacked closure/output, 4096 archive
entries, 100k serialized objects, 4096 hierarchy nodes, 250k vertices/vehicle and
4096-pixel textures. CLI decoding on POSIX has 3 GiB address-space and 180 CPU-second
limits (aggregate for the batch). Windows does not provide these POSIX limits:
use an OS-constrained process/container for untrusted inputs. No claim of a full
OS/network sandbox is made. Parsing third-party binary data can have decoder bugs;
use an unprivileged isolated environment without credentials for untrusted mods.
No automatic downloads or entitlement bypasses exist. Preserve redistribution
rights/provenance; conversion grants no new rights to the source assets.

## Tests

```sh
OPENBLAS_NUM_THREADS=1 .venv-modpack/bin/python -m unittest discover \
  -s Tools/modpack -p 'test_*.py' -v
Tools/unity.sh compile
Tools/unity.sh test
```

Python's real-package tests use `BLITZ_PW_DIST` or the local sibling `pw-mods-tool/dist`.
Unity's optional real-package tests consume `build/mods/Argonaut.blitzmod` generated
with the example override above; portable path/security tests do not need the
fixture. Generated delivery packages belong under ignored `build/mods`, not Git.
