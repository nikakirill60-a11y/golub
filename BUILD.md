# Build guide

[English](BUILD.md) | [Русский](BUILD.ru.md) · [Game](README.md)

This guide separates **Linux** and **Windows** workflows. Commands are run from
the checkout root. Close other Unity instances using the same project first.

## Requirements shared by both systems

- Unity **7000.0.0a8**, revision **c93e369fcf1c**, with your own activated license.
  The authoritative pin is `ProjectSettings/ProjectVersion.txt`.
- Git and **Git LFS**, installed before cloning; Python 3.
- For Android: Android Build Support, SDK/NDK Tools and OpenJDK installed with
  this exact editor. Player target: ARM64, Android 8.0 / API 26 or newer.
- For Linux players: Linux Build Support **IL2CPP**, using the Linux editor.
- For Windows players: native Windows editor, Windows Build Support **IL2CPP**,
  Visual Studio C++ Build Tools and Windows SDK. Wine/Mono are not substitutes.
- Access to your Git/LFS remote, Unity package registry and GitHub for Sentry.

Package pins live in `Packages/manifest.json`, `Packages/packages-lock.json` and
embedded `Packages/`. Converted game assets, Photon Fusion, LiteNetLib, Obfuz,
obfuscation rules/VM source, bootstrap scene and the .NET audit tool are already
tracked. A Godot checkout, original client archives and the root Photon
`.unitypackage` are **not** required for a normal build. Asset regeneration tools
may require those external sources; do not run them just to prepare a build.

## Local configuration: `.env`

Copy `.env.example` to **`.env`** and fill only the integrations you need.
The Python launcher loads it on both OSes. It accepts literal `KEY=value`, optional
`export`, comments and single/double quotes, **not shell expansions or commands**.
Use absolute paths; Windows paths with forward slashes are simplest. Explicit
process environment variables override file values, including explicit empty
values. `BLITZ_ENV_FILE` selects another file; when `.env` is absent, the old
`.env.release.local` is supported as a migration fallback.

| Variables | Purpose / destination |
| --- | --- |
| `BLITZ_SENTRY_DSN` | Public HTTPS ingestion DSN; empty disables telemetry |
| `BLITZ_DIRECT_SERVER` | Dedicated-server `host[:port]` or `[IPv6]:port`; empty disables this configured endpoint |
| `BLITZ_PHOTON_APP_ID` | Your Fusion application GUID; empty disables Photon |
| `BLITZ_ANDROID_KEYSTORE`, `BLITZ_ANDROID_KEYSTORE_PASSWORD`, `BLITZ_ANDROID_KEY_ALIAS`, `BLITZ_ANDROID_KEY_PASSWORD` | Local Android signing only |
| `SENTRY_ORG`, `SENTRY_PROJECT`, `SENTRY_AUTH_TOKEN` | Local native-symbol upload only; an empty token falls back to `sentry-cli login` (`~/.sentryclirc`) |
| `BLITZ_DIRECT_DEPLOY_TARGET`, `SSH_KEY`, `DIRECT_HOST`, `DIRECT_SSH_USER`, `DIRECT_SSH_KEY`, `DIRECT_PORT` | Optional dedicated-server operations, not player compilation |
| `UNITY`, `UNITY_HUB_EDITORS`, `BLITZ_DOTNET` | Optional editor/SDK path overrides |

Only the **three public runtime values** are written to ignored Unity resources.
They are readable from a shipped player; `.env` does not make a DSN/address/App ID
secret. Passwords, signing keys, Sentry auth tokens and the complete environment
are **never** serialized into runtime resources. Unset values remove stale
generated resources; telemetry setup also removes stale native Sentry options.
Players do not load your workstation's `.env`; Android receives public settings
at build time. Desktop runtime overrides are available through the corresponding
environment variables; direct-server CLI override: `-blitzServerAddress host:port`.

Do not commit `.env`, local credentials, Unity license files or keystores.
The original release key is required to update an already installed APK. All
Android build entry points fail when release signing is not configured; there is
no silent Unity debug-certificate fallback. For an independent private build you
may create your own release keystore with `keytool -genkeypair`; do not replace
an existing distribution key or put passwords on the command line.

## Linux: clone, configure and verify

Install the exact editor with Android/Linux modules via Unity Hub, plus Git LFS,
Python 3 and Bash. Then:

```sh
git lfs install
git clone <repository-url> blitz-legacy-unity
cd blitz-legacy-unity
git lfs pull
git lfs fsck
cp .env.example .env
chmod 600 .env
# Edit .env locally. Leave optional services empty for an offline build.
export UNITY="$HOME/Unity/Hub/Editor/7000.0.0a8/Editor/Unity"
python3 Tools/bootstrap_sentry_sdk.py
Tools/unity.sh import
Tools/unity.sh compile
Tools/unity.sh test
```

`Tools/unity.sh` delegates environment loading to `Tools/unity.py` and keeps the
complete Linux smoke/render command matrix. `python3 Tools/unity.py <command>`
is also supported. Paths containing spaces are passed as individual arguments.
Without `UNITY`, the Linux shell driver searches `~/Unity/Hub/Editor`; install
and select the exact project version rather than relying on its fallback editor.

EditMode results: `Logs/editmode-results.xml`. Additional checks:

```sh
python3 -m unittest discover -s Tools -p test_build_env.py -v
python3 -m unittest discover -s Tools -p test_unity_driver.py -v
BLITZ_TEST_FILTER=LocalizationCoverageTests Tools/unity.sh test
Tools/unity.sh smoke
Tools/unity.sh hangar-smoke
Tools/unity.sh battle-smoke
Tools/unity.sh battle-smoke-mobile
```

Render/performance routes need a graphics session; a headless check does not prove
visual correctness. Do not blindly run all Python asset-audit tests: some need
external Godot/client archives. Mod converter dependencies and tests are described
separately in `Tools/modpack/README.md`.

### Linux: build Android and Linux players

After configuring the release signing variables in `.env`:

```sh
Tools/unity.sh apk-production-debug > /tmp/blitz-apk-build.log 2>&1
Tools/unity.sh linux-production-debug > /tmp/blitz-linux-build.log 2>&1
```

Outputs:

- `Build/Android/BlitzLegacy-productiondebug.apk`
- `Build/Linux-productiondebug/BlitzLegacy`, alongside its complete data/library folder.

Check the Android log for `[Blitz] android signing: keystore ...`. Use the SDK's
`apksigner verify --verbose --print-certs` to verify the APK and compare its
certificate with your intended release key. Review logs before sharing them.
With a connected device, install and test using:

```sh
adb devices
adb install -r Build/Android/BlitzLegacy-productiondebug.apk
```

Run the Linux game from its complete output directory:

```sh
chmod +x Build/Linux-productiondebug/BlitzLegacy
./Build/Linux-productiondebug/BlitzLegacy
```

If Unity Clang lacks `libncurses.so.6` (for example on Arch), run
`Tools/bootstrap_linux_il2cpp_compat.sh`. It installs checksummed libraries only
into `.ci-cache/`; system libraries are not replaced. Pinned Linux SDK/toolchain
packages are restored by UPM, not by copying another checkout's `Library/`.

## Windows: clone, configure and verify

Install Git for Windows with Git LFS, Python 3, and the exact Unity editor/modules.
For Windows IL2CPP, install Visual Studio C++ Build Tools and a Windows SDK;
activate your own Unity license. For Android, install Android Build Support with
its SDK/NDK/OpenJDK. Use **PowerShell**, not Linux shell commands:

```powershell
git lfs install
git clone <repository-url> blitz-legacy-unity
Set-Location blitz-legacy-unity
git lfs pull
git lfs fsck
Copy-Item .env.example .env
# Edit .env locally; use quoted absolute paths (C:/... is portable).
$env:UNITY = 'C:/Program Files/Unity/Hub/Editor/7000.0.0a8/Editor/Unity.exe'
py -3 Tools/bootstrap_sentry_sdk.py
py -3 Tools/unity.py import
py -3 Tools/unity.py compile
py -3 Tools/unity.py test
if ($LASTEXITCODE -ne 0) { throw 'Unity tests failed; inspect Logs' }
```

The Python launcher waits for `Unity.exe`, propagates its exit code and writes
logs to `Logs/<command>.log`. Test results: `Logs/editmode-results.xml`.
Set `$env:BLITZ_TEST_FILTER = 'LocalizationCoverageTests'` for a filtered test;
remove it with `Remove-Item Env:BLITZ_TEST_FILTER` afterwards.

### Windows: build Windows and Android players

```powershell
py -3 Tools/unity.py windows-production-debug
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed; inspect Logs/windows-production-debug.log' }
# Requires Android modules and the four signing variables in .env:
py -3 Tools/unity.py apk-production-debug
if ($LASTEXITCODE -ne 0) { throw 'Android build failed; inspect Logs/apk-production-debug.log' }
```

Outputs:

- `Build/Windows-productiondebug/BlitzLegacy.exe` and adjacent data/DLL files.
- `Build/Android/BlitzLegacy-productiondebug.apk`.

Run the Windows player with its **entire** folder present:

```powershell
& './Build/Windows-productiondebug/BlitzLegacy.exe'
```

Verify Android signing with your SDK's `apksigner.bat`; use `adb install -r` if a
phone is connected. Native Windows builds do not require Docker, SSH, a Windows
VM, Bash, or a Godot project. Linux IL2CPP players are built using the Linux
workflow, not by substituting a Windows Mono player.

The native launcher builds the player. Distribution packaging, build-identity
stamping, Windows PE resource-version stamping and native-symbol upload belong
to the release pipeline/runner described below, not the basic build command.

## Build variants

**productiondebug** is the alpha distribution variant: IL2CPP, game debug tools
(`BLITZ_DEBUG_TOOLS`) and no Unity Development Build overlay. It is not the same
as a development build. Non-development builds use Obfuz by default.

| Command | Variant / output |
| --- | --- |
| `apk-production-debug` | `Build/Android/BlitzLegacy-productiondebug.apk` |
| `apk` / `apk-debug` | Android release / development, `BlitzLegacy.apk` / `BlitzLegacy-debug.apk` |
| `linux-production-debug` | `Build/Linux-productiondebug/BlitzLegacy` |
| `linux` / `linux-debug` | Linux release / development, `Build/Linux/` / `Build/Linux-debug/` |
| `windows-production-debug` | `Build/Windows-productiondebug/BlitzLegacy.exe` on Windows |

Pass a command to `Tools/unity.sh` on Linux or `py -3 Tools/unity.py` on Windows.
For intentional scene/settings regeneration, use `setup`, `scenes`, `net-setup`
and review the Git diff. Normal clones already include the scene/settings.

## SDK

The mod SDK, the `rempack` tool described in [`Sdk/README.md`](Sdk/README.md), is a
.NET 10 console program and needs no Unity. It compiles the game's package code
from `Assets/Blitz/Core/Mods` and runs the game's package tests as well, so the
game and the SDK always agree on what a valid package is. With a .NET 10 SDK
(found through `BLITZ_DOTNET`, `PATH` or `~/.dotnet`), on either system:

```sh
python3 Tools/sdk_build.py                # tests, then both archives into Build/Sdk/
python3 Tools/sdk_build.py --platform win-x64 --skip-tests
dotnet test Sdk/Rempack.Sdk.slnx -c Release
```

On Windows run the script with `py -3`. The first build downloads the .NET runtime
packs from NuGet. Each archive, `BlitzRemasterSDK-<version>-windows-x64.zip` or
`-linux-x64.zip`, holds a self-contained, trimmed single-file executable and the
SDK readmes; `<version>` is `BlitzVersion.Current`. The script runs the Linux
executable on a Linux host and the Windows one on a Windows host as a smoke test.

## Linux release pipeline and optional Windows VM runner

For packaging install `sha256sum`, `zip`, `unzip`, `file`, `keytool` and a .NET SDK
(the editor's bundled `DotNetSdk/dotnet` is discovered, or set `BLITZ_DOTNET`).
`Tools/local_release.sh` sources trusted `.env` as shell code; keep it compatible
with the portable literal syntax above. It expects a clean tree and a tag that
matches `bundleVersion` by default:

```sh
Tools/local_release.sh --production-debug --android-only
Tools/local_release.sh --production-debug --unix-only
# Untagged local verification, without symbol upload:
Tools/local_release.sh --production-debug --android-only --allow-untagged --no-sentry
```

`--allow-dirty` is only for deliberate local verification and stamps a dirty
revision. Symbol upload requires `sentry-cli`, `SENTRY_ORG`/`SENTRY_PROJECT` of
the intended project and an upload token: `SENTRY_AUTH_TOKEN` or, when it is
empty, the token saved by `sentry-cli login` (`~/.sentryclirc`). Authentication
is checked before any build starts. `--no-sentry` skips upload, **not** SDK
installation: the pipeline always bootstraps checksummed Sentry Unity **4.10.3**.
Direct builds without this optional SDK compile with telemetry disabled.

All three platforms can be packaged with an **already provisioned** native
Windows Docker/KVM guest:

```sh
export BLITZ_WINDOWS_IL2CPP_COMMAND="$PWD/Tools/windows_il2cpp_runner.sh"
Tools/local_release.sh --production-debug
```

The VM, activated editor/C++ SDK, SSH identity and SMB share are external
infrastructure, not missing game source files. Configure the optional
`BLITZ_WINDOWS_*` values from `.env.example`. The runner expects sibling
`blitz-windows-builder` by default, with `automation/build_key`,
`automation/known_hosts`, `shared/project` and `shared/windows-output`; see
`Tools/windows_il2cpp_runner.sh` / `Tools/windows_il2cpp.ps1`. SSH goes through
the VM's loopback forward `127.0.0.1:2222` (QEMU user-mode networking, which
keeps the guest online behind a host VPN); `BLITZ_WINDOWS_SSH_MODE=container`
reaches the guest address from inside the container on bridge networking. It downloads
checksummed `rcedit` 2.0.0 and verifies build identity and IL2CPP outputs. It does
not copy local `.env`/keystores/private SSH keys. Basic native Windows builds
above do not require this runner.

Distribute intended files from **`productiondebug-assets/`**, not arbitrary
`Build/*`: the pipeline packages players and guides, creates `BUILD_INFO.txt` and
`SHA256SUMS`, audits obfuscation and optionally uploads native symbols.

```sh
(cd productiondebug-assets && sha256sum -c SHA256SUMS)
```

Do not publish `*_BackUpThisFolder_ButDontShipItWithYourGame`,
`BurstDebugInformation`, generated C++, obfuscation mappings or debug symbols
inside public player archives. Release ZIP filtering handles these exclusions.

## Source completeness and troubleshooting

Keep `Assets/` and matching `.meta` files, `Packages/` pins/embedded Obfuz,
`ProjectSettings/`, `Tools/` (including `ObfuscationAudit.csproj` and its props),
player guides, `.env.example`, `AGENTS.md` and Git rules. No submodules are needed.
Sentry is restored by `bootstrap_sentry_sdk.py` using a pinned SHA-256;
`.ci-cache/downloads/` may be pre-populated with that exact ZIP for restricted
networks. Git LFS content must be restored, not left as pointer text.

Ignored: Unity caches, logs, builds, virtual environments, generated public
connection settings/Sentry options, local `.env` files, signing keys and local
`TASK_PLAN*` / `PORTING_NOTES*` journals. Journals are not part of public history.

| Problem | Resolution |
| --- | --- |
| Assets are text pointers | `git lfs pull`, then `git lfs fsck`; verify remote access |
| Wrong editor / missing Android API | Install exact modules; use the launcher with `-buildTarget Android` |
| Linux sysroot provider missing | Restore pinned UPM packages; launcher selects `-buildTarget Linux64` |
| License/lock error | Activate your license / close other editor instances; do not delete live locks |
| APK update rejected | Check application ID and original signing key, not just version number |
| No online / telemetry | Set public integration variables in `.env`, rebuild, and check service reachability |
| Windows VM unavailable | Use a native Windows editor or provision the external guest; no Mono fallback |

The default direct-server port is UDP 7777; building a player does not provision
a server. Inspect `Tools/direct_deploy.sh` and related operations scripts before
using them: network tests may intentionally stop/restart a deployed server.
No deployment is needed for local player compilation.

A clean-source audit restores LFS, bootstraps Sentry, and runs import/compile/test
and target builds in a separate checkout **without copying Library/UserSettings
or local secrets**. Supply credentials through environment variables. This proves
source completeness, not byte-for-byte reproducibility of Unity binaries.
