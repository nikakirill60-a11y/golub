# AGENTS.md — Blitz Legacy (Unity)

Rules for AI coding agents working in this repository. Read this file before
taking any action and follow it strictly. Project overview and commands:
`README.md`; optional local porting journal: `PORTING_NOTES.md` (ignored); roadmap: `ROADMAP.md`;
production-debug release details: `NATIVE_RELEASE.md`.

## Role

Act as a senior engineer producing production-ready, maintainable and correct
code. This project is an Android-first port of Blitz Legacy from Godot
(`../blitz-legacy-godot`) to Unity. Scenes and UI are built from code; assets
come from the original Godot project.

## Language

- **All code comments, commit messages, docs inside code and log messages are
  written in English.**
- User-facing strings are never hardcoded: put them into the localization
  table (`Assets/Blitz/Resources/Data/lang.json`, read via
  `Assets/Blitz/Core/Localization/LocalizationTable.cs`; tank names in
  `tank_name_localization.json`). Keep `LocalizationCoverageTests` green.

## Work to completion

- **Always implement a feature fully, end to end. Do not stop at intermediate
  stages** ("first step done", "skeleton ready", "continue next time") and do
  not hand back half-done work for confirmation. Keep going through
  implementation, tests, build and commit until the task is actually done.
- No mock or incomplete code (`// TODO: implement later`, stubs, pseudocode)
  unless explicitly requested.
- Work maximally autonomously: use code, scripts, `Tools/unity.sh`, Python
  tools in `Tools/` and CLI automation before asking the user for anything.
- Ask clarifying questions only when requirements are genuinely ambiguous or a
  critical architectural choice is unspecified.

## Workflow

1. **Discovery** — inspect the existing code and relevant notes/plans before
   proposing changes. Match the existing style; prefer minimal, modular,
   idiomatic changes.
2. **Planning** — keep a task plan with checkboxes: `TASK_PLAN.md` in the
   project root, or a topic file `TASK_PLAN_<TOPIC>.md` (existing convention).
   Use `[ ]` pending, `[/]` in progress, `[x]` done, and update it as work
   progresses or scope changes. Plans and porting journals stay local and ignored;
   never add them to a commit.
3. **Implementation** — prioritize compile-time correctness and runtime
   stability; handle edge cases, network disruptions and resource exhaustion
   gracefully.
4. **Verification** — run/extend tests and build:
   - `Tools/unity.sh compile` — compilation check.
   - `Tools/unity.sh test` — EditMode tests (`Logs/editmode-results.xml`).
     Add tests to `Assets/Blitz/Tests/` for new behavior and edge cases.
   - Relevant smoke/render commands (`Tools/unity.sh` without args lists all).
   - If a build or test fails, diagnose the root cause and fix it before
     finishing.
5. **Device testing** — if a device is connected via `adb`, install the APK
   (`adb install -r ...`) and test on the real device.
6. **Commit** — see the Git section.

## Android APK builds

- **Always build the `productiondebug` APK, and always sign it with the
  release keystore — even when no device is connected via adb.** Never ship
  or hand over an APK signed with Unity's debug certificate (it cannot update
  an installed build).
- The keystore settings live in `.env` (git-ignored:
  `BLITZ_ANDROID_KEYSTORE`, `BLITZ_ANDROID_KEYSTORE_PASSWORD`,
  `BLITZ_ANDROID_KEY_ALIAS`, `BLITZ_ANDROID_KEY_PASSWORD`). `Tools/unity.sh`
  loads its literal values through the portable `Tools/unity.py` launcher:

  ```sh
  Tools/unity.sh apk-production-debug
  # -> Build/Android/BlitzLegacy-productiondebug.apk
  ```

  Or use the full pipeline (checks the keystore, collects
  `productiondebug-assets/`):

  ```sh
  Tools/local_release.sh --production-debug --android-only --allow-untagged
  ```

  Add `--allow-dirty` / `--no-sentry` only when intentionally needed.
- Check the build log for `[Blitz] android signing: keystore ...`. If the log
  says the keystore is unset or the keystore check fails, stop and fix it —
  do not fall back to the debug certificate.
- Never print, log, copy or commit keystore files, passwords, Sentry tokens or
  `.env` contents.

## Git

- Atomic commits following Conventional Commits, in English. Prefixes:
  `fix:`, `feat:`, `chore:`, `ui:`, `ci:`, `test:`, `build:`, `docs:`,
  optional scope, e.g. `fix(audio): ...`.
- Do not commit build artifacts or temporary files (`Build/`, `build/`,
  `Library/`, `Logs/`, `Temp/`, `UserSettings/`, `productiondebug-assets/`,
  `release-assets/`, `debug-assets/`, `mono_crash.*.blob`, `.venv*`,
  `.ci-cache/`, `*.keystore`, `*.jks`, `.env`).
- Do not commit partial or broken changes.

## Unity editor hand-off

If a step truly cannot be done programmatically and needs the Unity editor or
manual UI action, give detailed step-by-step instructions: exact menu paths,
inspector panels, scene hierarchy positions, setting names, where the related
assets/scripts live in the project, and how to verify the result.

## Quality & security

- Secure defaults: safe network/IPC configuration, strict input validation,
  standard cryptography. Call out security-sensitive code in comments and in
  the summary.
- Explain non-obvious trade-offs and architectural decisions concisely.

## Failure protocol

If a step cannot be completed (missing tool, permissions, device, keystore,
Unity license, etc.):

1. Stop before committing partial or broken changes.
2. Give a clear diagnosis of what failed and why.
3. Give step-by-step instructions for what the user must do to unblock it.
