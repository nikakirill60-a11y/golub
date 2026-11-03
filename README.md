# Blitz Legacy: Remastered Deluxe Edition

[English](README.md) | [Русский](README.ru.md)

An Android-first tank combat game ported from **Blitz Legacy (Godot)** to Unity.
The project includes a 3D garage, vehicle research and loadouts, battles with
bots, online play, and touch and keyboard/mouse controls. Scenes and UI are built
from C#; converted game data, models, textures and audio are included in this
repository through Git and Git LFS.

**Status:** alpha; gameplay, online services and mod compatibility are still
being developed. The release version is defined in
[`BlitzVersion.cs`](Assets/Blitz/Core/BlitzVersion.cs), not in this document.

## Game features

- Garage, vehicle selection, tech trees, equipment, consumables and account progress.
- Tank movement, aiming, shooting, armor penetration, damage and battle results.
- Maps and special modes, including Uprising, Big Boss and World of Ducks.
- Local battles with bots; online battles using a dedicated server or Photon Fusion.
- Russian/English localization, configurable controls, graphics and audio settings.
- A mod manager and an offline converter for supported Panzer War vehicle
  `.modpack` files. Conversion has explicit compatibility limits; see
  [`Tools/modpack/README.md`](Tools/modpack/README.md).

## Playing

Use an APK or desktop player produced by the release pipeline. Build outputs
are not committed to Git, and this repository does not promise a public download
location. On Android, install the APK; on desktop, extract the **whole** player
archive, not just its executable.

1. Start the game and create or select a local account.
2. Choose a tank and its loadout in the garage.
3. Select a battle mode and enter a battle. Online play requires a reachable
   service and matching game versions; it is not provided by cloning the source.
4. Adjust language, graphics, sound and input in Settings.

### Default desktop controls

| Action | Binding |
| --- | --- |
| Move | W / A / S / D |
| Fire / hold turret | Left / right mouse button |
| Sniper mode | Left Shift |
| Reload magazine | C |
| Zoom | Mouse wheel |
| Shells | 1 / 2 / 3 |
| Consumables | 4 / 5 / 6 |
| Mode ability | 7 |
| Scoreboard / menu / fullscreen | Tab / Esc / F11 |

Bindings can be changed in Settings. Android uses on-screen controls.
The Russian online guide is [`Онлайн_как_играть.txt`](Онлайн_как_играть.txt);
mod installation details are in [`tutorial_mods.txt`](tutorial_mods.txt).

## Getting started with the source

Install **Git LFS** before cloning, and Unity **7000.0.0a8** with the required
platform modules and a valid editor license. The exact editor revision is in
[`ProjectSettings/ProjectVersion.txt`](ProjectSettings/ProjectVersion.txt).

```sh
git lfs install
git clone <repository-url> blitz-legacy-unity
cd blitz-legacy-unity
git lfs pull
git lfs fsck
python3 Tools/bootstrap_sentry_sdk.py
Tools/unity.sh import
Tools/unity.sh compile
Tools/unity.sh test
```

These shell commands target Linux; on Windows use `py -3 Tools/unity.py <command>`.
Copy `.env.example` to ignored `.env` to configure online services and signing.
See **[BUILD.md](BUILD.md)** for prerequisites,
signed Android builds, Linux/Windows builds, optional services, troubleshooting
and the distinction between development and productiondebug players.

The original Godot project and original client archives are **not required for
normal builds**: converted runtime assets are already committed. They are only
needed when re-running the corresponding asset-porting tools.

## Repository layout

| Path | Purpose |
| --- | --- |
| `Assets/Blitz/Core/` | Game rules and data models |
| `Assets/Blitz/Runtime/` | Runtime screens, garage, battles and diagnostics |
| `Assets/Blitz/Network/`, `Assets/Blitz/Direct/` | Photon and direct-server networking |
| `Assets/Blitz/Resources/` | Runtime game data, models, textures, audio and localization |
| `Assets/Blitz/EditorTools/`, `Assets/Blitz/Tests/` | Setup/build automation and EditMode tests |
| `Assets/Photon/`, `Assets/ThirdParty/` | Imported third-party runtime dependencies |
| `Packages/`, `ProjectSettings/` | Package pins, embedded Obfuz and Unity settings |
| `Tools/` | Build/release scripts, asset converters and Python tests |
| `Sdk/` | Mod SDK: the `rempack` package tool and its tests ([`Sdk/README.md`](Sdk/README.md)) |

## Repository boundaries

Keep all build inputs and their Unity `.meta` files under version control.
Git LFS stores binary game assets; a ZIP of pointer files is not a usable checkout.
Unity caches, APKs/players, logs, local virtual environments, generated telemetry
settings, credentials and signing keys are deliberately excluded.

Sentry Unity SDK **4.10.3** is installed from a checksummed archive by
`Tools/bootstrap_sentry_sdk.py`; it is not vendored into Git. Photon Fusion and
Obfuz **are** already included. Do not reimport the local `.unitypackage` over them.

## Development notes

- [Build guide](BUILD.md) / [Руководство по сборке](BUILD.ru.md)
- [Agent and contribution rules](AGENTS.md)
- [Native release notes](NATIVE_RELEASE.md)
- [Original roadmap](ROADMAP.md) — historical planning, not a current feature matrix

Retain third-party license notices. Inclusion of third-party assets/SDKs does not
by itself grant redistribution rights; follow their respective license terms.

Local `TASK_PLAN*` and `PORTING_NOTES*` journals are intentionally ignored;
`AGENTS.md` remains versioned. Deployment addresses, public Sentry DSN and Photon
App ID belong in local `.env`, not in source. Only public connection settings
are embedded in a player; release signing/upload secrets are never embedded.
