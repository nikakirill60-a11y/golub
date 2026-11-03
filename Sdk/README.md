# Blitz Remaster SDK

[English](README.md) | [Русский](README.ru.md)

`rempack` checks, packs and inspects **Remaster Packages** (`.rempack`), the mod
format of Blitz Remaster. It is built from the game's own package reader and
validator: a package that `rempack` accepts is accepted by the game of the same
version, and its messages are the ones the game shows.

The SDK is one self-contained executable, with no .NET installation needed:

| Archive | Executable |
| --- | --- |
| `BlitzRemasterSDK-<version>-windows-x64.zip` | `rempack.exe` |
| `BlitzRemasterSDK-<version>-linux-x64.zip` | `rempack` |

`rempack --version` prints the tool version, the package format version and the
content features the matching game loads.

## Quick start

```sh
rempack validate my_mod                         # check the folder as the game will
rempack pack my_mod                             # write <id>-<version>.rempack beside it
rempack inspect author.my_mod-1.0.0.rempack --files
```

On Windows a folder dropped onto `rempack.exe` is packed and a dropped `.rempack`
is inspected; the window stays open until Enter is pressed. A bare path on the
command line does the same.

## Commands

| Command | What it does |
| --- | --- |
| `rempack validate <folder\|file.rempack>` | Checks a mod folder or a package exactly as the game does when it installs a mod. A folder is packed into a temporary file for the check; nothing is written beside it. |
| `rempack pack <folder> [-o <file>]` | Packs a folder, then checks the written package. A refused package is not saved and an existing file of the same name stays as it was. |
| `rempack inspect <file.rempack>` | Shows id, version, name, authors, licence, content, dependencies, replaced stock content and sizes, then the check report. |
| `rempack help [<command>]` | Usage, or more about one command. |

## Options

| Option | Meaning |
| --- | --- |
| `-o`, `--output <file>` | Where `pack` writes the package. By default `<id>-<version>.rempack` beside the folder; an existing folder given here gets that default name inside it; missing folders are created. |
| `--strict` | Refuse a package with warnings too. |
| `--json` | Print one JSON document on the standard output instead of text. |
| `--files` | `inspect`: list every file with its size and packed size. |
| `-v`, `--verbose` | Show issue codes, technical details, skipped files and links. |
| `--lang <en\|ru>` | Language of the messages. Without it: `REMPACK_LANG`, then the system language (`LC_ALL`, `LC_MESSAGES` or `LANG` on Linux): Russian for a Russian system, English for any other. |
| `--no-color` | No colours. `NO_COLOR` does the same; colours are also off when the output is redirected. |
| `--version` | The tool version and what the game loads. |
| `-h`, `--help` | Usage. |

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Accepted: the game installs this package (warnings allowed unless `--strict`). |
| `1` | Refused: the report lists the errors to fix. |
| `2` | Wrong command line, or the given file or folder does not exist. |
| `3` | A file could not be written, for example the package or its temporary file. |

## What goes into a package

`pack` writes `rempack.json` at the root of the package and every other file of
the folder under its relative path. It fills the `files` field of the manifest
with the SHA-256 of each packed file; whatever the folder's `rempack.json` had
there is replaced. These stay out:

- names that start with a dot (`.git/`, `.rempackignore`, `.DS_Store`) and
  archiver junk (`Thumbs.db`, `desktop.ini`, `__MACOSX/`);
- symbolic links and junctions, which are never followed (`-v` lists them);
- editor sources and leftovers: `*.rempack`, `*.tmp`, `*.bak`, `*~`, `*.blend`,
  `*.blend1`…`*.blend9`, `*.psd`, `*.kra`, `*.xcf`, `*.spp`, `*.sbs`;
- the patterns of `.rempackignore` in the folder's root.

`.rempackignore` is written like `.gitignore`: one pattern per line, `#` starts a
comment, `*` and `?` stop at a slash while `**` does not, `[abc]` is a character
class, a trailing `/` matches folders only, a `/` anywhere else anchors the
pattern to the root, `!` takes a file back, and the last matching line wins.
Matching ignores case, like package paths do.

```gitignore
# sources and notes
source/
*.max
notes.txt
!textures/readme_textures.txt
```

Package paths may use only Latin letters, digits, `.`, `_` and `-` between forward
slashes; no name may start or end with a dot or be a reserved Windows device name
(`con`, `nul`, `com1`…). A path is at most 240 characters, one name at most 96.
Two paths that differ only in case are one path. Every such problem is reported in
one run, together with the rest of the check.

## `rempack.json`

```json
{
  "format": "rempack",
  "formatVersion": 1,
  "id": "author.my_mod",
  "version": "1.0.0",
  "name": { "en": "My mod", "ru": "Мой мод" },
  "description": { "en": "What it adds.", "ru": "Что он добавляет." },
  "authors": ["Author"],
  "license": "CC-BY-4.0",
  "homepage": "https://example.org/my_mod",
  "game": { "minVersion": "2.0.0", "features": ["tanks@1"] },
  "dependencies": [{ "id": "author.base_pack", "version": "^1.2" }],
  "conflicts": [],
  "content": { "tanks": ["t34_custom"] },
  "overrides": []
}
```

| Field | Rules |
| --- | --- |
| `format`, `formatVersion` | `"rempack"` and `1`. A newer format version needs a newer game. |
| `id` | `author.mod_name`: two parts of lowercase Latin letters and digits joined by single underscores (author up to 32 characters, name up to 48). It names the mod everywhere, so keep it across versions. |
| `version` | Semantic version: `1.0.0`, `1.2.0-beta.1`. The game compares it with the installed version of the mod to tell an update from a downgrade. |
| `name` | A string (English) or an object of language codes (`en`, `ru`, `pt-BR`); English is required. Up to 64 characters. |
| `description` | Optional, like `name`; up to 2000 characters. |
| `authors` | Required: 1 to 16 names. |
| `license`, `homepage` | Optional. `homepage` must be an `https://` address. |
| `icon` | Optional package path of a PNG or JPEG up to 1024 × 1024; `icon.png` at the root is used when the field is absent. |
| `previews` | Optional: up to 8 PNG or JPEG pictures, up to 8192 × 8192. |
| `game.minVersion` | The oldest game version the mod works with. |
| `game.features` | What the mod needs from the game, as `name@version`: `tanks@1`, `hangars@1`, `maps@1`, `overrides@1`. A game without a feature refuses the package with "update the game". |
| `dependencies`, `conflicts` | Up to 32 `{ "id", "version" }` entries. Versions are written as in npm: `*`, `1.2.3`, `>=1.2 <2`, `^1.2`, `~1.2.3`, `1.x`, alternatives joined by `\|\|`. |
| `content` | Local ids (lowercase letters and digits joined by single underscores, up to 48 characters) of the `tanks`, `hangars` and `maps` the mod adds, up to 256 of each; at least one in total. Each lives in `tanks/<id>/tank.json`, `hangars/<id>/hangar.json` or `maps/<id>/map.json`, and each kind needs its feature in `game.features`. |
| `overrides` | Stock content the mod replaces, as `stock:tank/<id>`, `stock:hangar/<id>` or `stock:map/<id>`; needs `overrides@1`. While such a mod is on, online play is off. |
| `files` | Written by `pack`; do not edit it by hand. |

A field the game does not know is a warning, so a typo does not go unnoticed.
`locale/<language>.json` may hold flat string tables of the mod (keys of
lowercase letters, digits, `_` and `.`). `LICENSE`, `README`, `CHANGELOG`,
`CREDITS` and `NOTICE` at the root (no extension, `.txt` or `.md`) travel with the
package; any other file that nothing uses is a warning.

The main limits: a package of up to 1 GiB (from 300 MB it is marked as heavy),
2 GiB unpacked, 20,000 files, 512 MiB per file.

## JSON output

With `--json` every command prints one JSON document and nothing else on the
standard output, in the language chosen for messages. Every document has `tool`,
`version`, `command`, `language` and `ok`. The checking commands add:

| Field | Meaning |
| --- | --- |
| `status` | `accepted`, `heavy` (accepted, marked as large) or `refused`. |
| `strict` | Whether `--strict` was given. |
| `counts` | `errors`, `warnings` and `notes`. |
| `issues` | Each with `severity` (`error`, `warning`, `info`), `code`, `path`, `args`, the localized `message` and an optional `detail`. |
| `dropped` | Issues left out after the first 500. |
| `needsNewerGame` | Whether only a newer game can load the package. |

`validate` adds `path`, `folder`, `skipped` and `links`; `pack` adds `path`,
`output`, `written`, `files`, `size`, `unpacked`, `skipped` and `links`; `inspect`
adds `path`, `manifest`, `sizes` (`archive`, `unpacked`, `files`) and `entries`
(`path`, `size`, `compressedSize`, `method`, `sha256`). A wrong command line or a
failed write gives `ok: false`, `status` `usage` or `failed` and an `error` with
`code` and `message`. `--version --json` lists `formatVersion`, `gameVersion` and
`features`.

## Building from source

The SDK sources are in `Sdk/` of the game repository; the package code itself is
compiled in from `Assets/Blitz/Core/Mods`, and the game's package tests run in the
SDK test project too. With a .NET 10 SDK:

```sh
dotnet test Sdk/Rempack.Sdk.slnx -c Release   # SDK and game package tests
python3 Tools/sdk_build.py                    # both archives into Build/Sdk/
```

`Tools/sdk_build.py` finds `dotnet` through `BLITZ_DOTNET`, `PATH` or
`~/.dotnet`, runs the tests, publishes trimmed single-file executables for
`win-x64` and `linux-x64` and zips each with these readmes. `--platform` limits
the build to one platform and `--skip-tests` skips the tests. See
[`BUILD.md`](../BUILD.md#sdk) for details.
