#!/usr/bin/env bash
# Local build + Sentry symbol upload for the Blitz Legacy Unity client.
#
# Ported from the Godot project's scripts/release/local_release.sh. Everything
# happens on this machine: no GitHub Actions, no self-hosted runner, no GitHub
# Release and no Telegram step. The Godot script also had to build its own
# engine export templates first; Unity ships its player binaries with the
# editor, so that whole stage is gone and only the parts that guard a release
# remain: the version matches the tag at HEAD, the tree has to be clean, the
# APK is signed with the release keystore and the native symbols reach Sentry
# before the artifacts are handed out.
set -Eeuo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG_FILE="${BLITZ_ENV_FILE:-$ROOT/.env}"
if [[ ! -f "$CONFIG_FILE" && -z "${BLITZ_ENV_FILE:-}" ]]; then
    CONFIG_FILE="$ROOT/.env.release.local"
fi
ONLINE_GUIDE="Онлайн_как_играть.txt"
ALLOW_DIRTY=false
ALLOW_UNTAGGED=false
UPLOAD_SENTRY=true
MODE=release
TARGETS="android linux"
EXPLICIT_TARGETS=false

usage() {
	cat <<'EOF'
Usage: Tools/local_release.sh [options]

Build the Android arm64 APK and Linux x86_64 player locally. Production-debug
also builds Windows x86_64 IL2CPP using a Windows runner (Windows IL2CPP cannot
be cross-compiled by the Linux editor). Collect fresh artifacts, instructions, checksums and native
symbols for Sentry.

Options:
  --debug             Build the development matrix into debug-assets/ instead
                      of a release. The release keystore signature and the
                      symbol upload still apply, because during the alpha the
                      debug build is what the testers actually run.
  --production-debug  Build the alpha matrix into productiondebug-assets/:
                      all three platforms use debug tools without Unity's
                      development player. Signing and symbol upload remain.
  --android-only      Build only the APK.
  --linux-only        Build only the Linux desktop player.
  --windows-only      Build only Windows IL2CPP (requires --production-debug).
  --unix-only         Build Android + Linux; do not request Windows.
  --allow-dirty       Build an intentionally dirty working tree.
  --allow-untagged    Build a HEAD that carries no matching version tag.
  --no-sentry         Build artifacts but do not upload debug symbols.
  --config PATH       Environment file (default: .env, then legacy .env.release.local).
  -h, --help          Show this help.

.env (git-ignored) is sourced when present and may set:
  SENTRY_ORG, SENTRY_PROJECT, SENTRY_AUTH_TOKEN (empty: the sentry-cli
  login token from ~/.sentryclirc)
  BLITZ_ANDROID_KEYSTORE, BLITZ_ANDROID_KEYSTORE_PASSWORD,
  BLITZ_ANDROID_KEY_ALIAS, BLITZ_ANDROID_KEY_PASSWORD
  BLITZ_WINDOWS_IL2CPP_COMMAND (Windows runner, no Mono fallback)
EOF
}

while (($#)); do
	case "$1" in
		--debug) MODE=debug ;;
		--production-debug) MODE=production-debug ;;
		--android-only) TARGETS="android"; EXPLICIT_TARGETS=true ;;
		--linux-only) TARGETS="linux"; EXPLICIT_TARGETS=true ;;
		--windows-only) TARGETS="windows"; EXPLICIT_TARGETS=true ;;
		--unix-only) TARGETS="android linux"; EXPLICIT_TARGETS=true ;;
		--allow-dirty) ALLOW_DIRTY=true ;;
		--allow-untagged) ALLOW_UNTAGGED=true ;;
		--no-sentry) UPLOAD_SENTRY=false ;;
		--config) shift; [[ $# -gt 0 ]] || { echo "--config requires a path" >&2; exit 2; }; CONFIG_FILE="$1" ;;
		-h|--help) usage; exit 0 ;;
		*) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
	esac
	shift
done

if [[ "$MODE" == production-debug && "$EXPLICIT_TARGETS" == false ]]; then
    TARGETS="android linux windows"
fi
if [[ " $TARGETS " == *" windows "* && "$MODE" != production-debug ]]; then
    echo "Windows IL2CPP currently requires --production-debug" >&2
    exit 2
fi

if [[ -f "$CONFIG_FILE" ]]; then
	set -a
	# This file is local and git-ignored. It holds the keystore passwords and
	# the Sentry auth token, so it must never be committed.
	# shellcheck disable=SC1090
	source "$CONFIG_FILE"
	set +a
fi

# The command must invoke a Windows editor/VM and copy the fresh IL2CPP
# output back to Build/Windows-productiondebug. Never substitute Mono.
if [[ " $TARGETS " == *" windows "* && -z "${BLITZ_WINDOWS_IL2CPP_COMMAND:-}" ]]; then
    echo "Windows IL2CPP runner is not configured. Set BLITZ_WINDOWS_IL2CPP_COMMAND to run Tools/windows_il2cpp.ps1 on Windows and copy the output back; no Mono fallback is allowed." >&2
    exit 2
fi

if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
	echo "Local release refuses to run inside GitHub Actions" >&2
	exit 2
fi

for command in git python3 sha256sum zip unzip file; do
	command -v "$command" >/dev/null || { echo "Missing local release dependency: $command" >&2; exit 2; }
done
if [[ "$UPLOAD_SENTRY" == true ]]; then
	command -v sentry-cli >/dev/null || { echo "Missing local release dependency: sentry-cli (or pass --no-sentry)" >&2; exit 2; }
fi

# The version is the project's own bundle version, and the tag at HEAD has to
# agree with it, so no artifact can claim a version nobody tagged.
VERSION="$(sed -n 's/^  bundleVersion: //p' "$ROOT/ProjectSettings/ProjectSettings.asset" | head -n 1)"
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+(\.[0-9]+|[ab][1-9][0-9]*(f[1-9][0-9]*)?)$ ]]; then
    echo "Unexpected bundleVersion: '$VERSION'" >&2; exit 3
fi
ANDROID_CODE="$(sed -n 's/^  AndroidBundleVersionCode: //p' "$ROOT/ProjectSettings/ProjectSettings.asset" | head -n 1)"
# The release code is assigned, not derived from the label (2.0a2f1 -> 203,
# 2.0b1 -> 205): it only has to grow. The client and the dedicated server
# compare Current/Code from BlitzVersion.cs, so the player metadata must match
# that pair exactly.
VERSION_SOURCE="$ROOT/Assets/Blitz/Core/BlitzVersion.cs"
SOURCE_VERSION="$(sed -n 's/^ *public const string Current = "\(.*\)";/\1/p' "$VERSION_SOURCE" | head -n 1)"
SOURCE_CODE="$(sed -n 's/^ *public const int Code = \([0-9]*\);/\1/p' "$VERSION_SOURCE" | head -n 1)"
if [[ "$VERSION" != "$SOURCE_VERSION" || "$ANDROID_CODE" != "$SOURCE_CODE" ]]; then
	echo "Player metadata $VERSION/$ANDROID_CODE does not match BlitzVersion $SOURCE_VERSION/$SOURCE_CODE" >&2
	exit 3
fi

if [[ "$ALLOW_UNTAGGED" != true ]] && ! git -C "$ROOT" tag --points-at HEAD | grep -qx "v\?$VERSION"; then
	echo "HEAD carries no tag $VERSION (or v$VERSION). Tag the release or pass --allow-untagged." >&2
	exit 3
fi

if [[ "$ALLOW_DIRTY" != true ]] && [[ -n "$(git -C "$ROOT" status --porcelain --untracked-files=normal)" ]]; then
	echo "Working tree is dirty. Commit/stash it or pass --allow-dirty intentionally." >&2
	git -C "$ROOT" status --short >&2
	exit 3
fi

REVISION="$(git -C "$ROOT" rev-parse HEAD)"
if [[ -n "$(git -C "$ROOT" status --porcelain --untracked-files=normal)" ]]; then
	REVISION="$REVISION-dirty"
fi
BUILD_ID="$(date -u +%Y%m%dT%H%M%SZ)-${REVISION:0:12}"

# The SDK is pinned and git-ignored, so a fresh clone installs it here instead
# of quietly producing a player with no diagnostics in it.
python3 "$ROOT/Tools/bootstrap_sentry_sdk.py"

# The project behind this client's DSN, i.e. where its symbols have to land.
# It is a different project from the Godot client's, and symbols uploaded to
# the wrong one silently symbolicate nothing.
SENTRY_ORG="${SENTRY_ORG:-}"
SENTRY_PROJECT="${SENTRY_PROJECT:-}"
if [[ "$UPLOAD_SENTRY" == true ]]; then
    : "${SENTRY_ORG:?Set SENTRY_ORG in .env}"
    : "${SENTRY_PROJECT:?Set SENTRY_PROJECT in .env}"
    # The upload token comes from .env or, when that is empty, from the
    # sentry-cli login (~/.sentryclirc). An empty variable would still win
    # over the login and authenticate with an empty token, so drop it.
    if [[ -z "${SENTRY_AUTH_TOKEN:-}" ]]; then
        unset SENTRY_AUTH_TOKEN
    fi
    # Fail before any build starts rather than after the whole matrix.
    if ! sentry-cli info >/dev/null; then
        echo "Sentry authentication failed: set SENTRY_AUTH_TOKEN in .env or run 'sentry-cli login', or pass --no-sentry" >&2
        exit 2
    fi
fi

if [[ -n "${BLITZ_ANDROID_KEYSTORE:-}" ]]; then
	command -v keytool >/dev/null || { echo "Missing local release dependency: keytool" >&2; exit 2; }
	[[ -s "$BLITZ_ANDROID_KEYSTORE" ]] || { echo "Android keystore does not exist: $BLITZ_ANDROID_KEYSTORE" >&2; exit 2; }
	if ! keystore_check="$(keytool -list -keystore "$BLITZ_ANDROID_KEYSTORE" \
		-storepass:env BLITZ_ANDROID_KEYSTORE_PASSWORD -alias "${BLITZ_ANDROID_KEY_ALIAS:-}" 2>&1)"; then
		# Never echo the password itself, only keytool's own diagnosis.
		if [[ -n "${BLITZ_ANDROID_KEYSTORE_PASSWORD:-}" ]]; then
			keystore_check="${keystore_check//$BLITZ_ANDROID_KEYSTORE_PASSWORD/<redacted>}"
		fi
		echo "Android keystore check failed: $BLITZ_ANDROID_KEYSTORE (alias '${BLITZ_ANDROID_KEY_ALIAS:-}')" >&2
		echo "$keystore_check" >&2
		exit 2
	fi
elif [[ " $TARGETS " == *" android "* ]]; then
    echo "Release signing is required: configure BLITZ_ANDROID_KEYSTORE in .env" >&2
    exit 2
fi

export BLITZ_RELEASE_VERSION="$VERSION"
export BLITZ_BUILD_ID="$BUILD_ID"
export BLITZ_REVISION="$REVISION"

printf 'Local %s build %s (%s)\n' "$MODE" "$VERSION" "$BUILD_ID"

if [[ "$MODE" == debug ]]; then
	suffix="-debug"
	asset_dir="$ROOT/debug-assets"
	android_target=apk-debug
	android_player="$ROOT/Build/Android/BlitzLegacy-debug.apk"
	linux_target=linux-debug
	linux_dir="$ROOT/Build/Linux-debug"
elif [[ "$MODE" == production-debug ]]; then
	# Consistent debug-tool-only players on all three platforms.
	suffix="-productiondebug"
	asset_dir="$ROOT/productiondebug-assets"
	android_target=apk-production-debug
	android_player="$ROOT/Build/Android/BlitzLegacy-productiondebug.apk"
	linux_target=linux-production-debug
	linux_dir="$ROOT/Build/Linux-productiondebug"
	windows_target=windows-production-debug
	windows_dir="$ROOT/Build/Windows-productiondebug"
else
	suffix=""
	asset_dir="$ROOT/release-assets"
	android_target=apk
	android_player="$ROOT/Build/Android/BlitzLegacy.apk"
	linux_target=linux
	linux_dir="$ROOT/Build/Linux"
fi
work_dir="$ROOT/.ci-cache/local-$MODE/$VERSION"
rm -rf "$asset_dir" "$work_dir"
mkdir -p "$asset_dir" "$work_dir"

for target in $TARGETS; do
	case "$target" in
		android)
			rm -f "$android_player"
			rm -f "$ROOT"/Build/Android/*symbols*.zip
			"$ROOT/Tools/unity.sh" "$android_target"
			[[ -s "$android_player" ]] || { echo "Missing build artifact: $android_player" >&2; exit 4; }
			cp -p "$android_player" "$asset_dir/BlitzLegacy-$VERSION-Android-arm64$suffix.apk"
			;;
		linux)
			rm -rf "$linux_dir"
			"$ROOT/Tools/unity.sh" "$linux_target"
			[[ -s "$linux_dir/BlitzLegacy" ]] || { echo "Missing build artifact: $linux_dir/BlitzLegacy" >&2; exit 4; }
			cp "$ROOT/tutorial_mods.txt" "$linux_dir/tutorial_mods.txt"
			(cd "$linux_dir" && zip -qr "$asset_dir/BlitzLegacy-$VERSION-Linux-x86_64$suffix.zip" . -x '*.debug' '*.pdb' '*_BackUpThisFolder_ButDontShipItWithYourGame/*' '*_BurstDebugInformation_DoNotShip/*')
			;;
        windows)
            rm -rf "$windows_dir"
            (cd "$ROOT" && bash -c "$BLITZ_WINDOWS_IL2CPP_COMMAND")
            [[ -s "$windows_dir/GameAssembly.dll" ]] || { echo "Missing Windows IL2CPP GameAssembly.dll" >&2; exit 4; }
            [[ -s "$windows_dir/BlitzLegacy_Data/il2cpp_data/Metadata/global-metadata.dat" ]] || { echo "Missing Windows IL2CPP metadata" >&2; exit 4; }
            [[ ! -f "$windows_dir/BlitzLegacy_Data/Managed/BlitzLegacy.Runtime.dll" ]] || { echo "Refusing managed Windows runtime" >&2; exit 4; }
            [[ -s "$windows_dir/BlitzLegacy.exe" ]] || { echo "Missing Windows player" >&2; exit 4; }
            cp "$ROOT/tutorial_mods.txt" "$windows_dir/tutorial_mods.txt"
            (cd "$windows_dir" && zip -qr "$asset_dir/BlitzLegacy-$VERSION-Windows-x86_64$suffix.zip" . -x '*.pdb' '*_BackUpThisFolder_ButDontShipItWithYourGame/*' '*_BurstDebugInformation_DoNotShip/*')
            ;;
	esac
done

# Symbol obfuscation audit: the managed assemblies IL2CPP was fed carry the
# exact names global-metadata.dat ships. A build whose project assemblies are
# not renamed (or that leaves a security-sensitive symbol readable) is refused.
if [[ "$MODE" != debug && "${BLITZ_OBFUSCATE:-1}" != 0 ]]; then
	# The SDK of the editor the project is opened with, then the newest one installed.
	project_unity="$(sed -n 's/^m_EditorVersion: //p' "$ROOT/ProjectSettings/ProjectVersion.txt" 2>/dev/null)"
	dotnet_sdk="${BLITZ_DOTNET:-}"
	if [[ -z "$dotnet_sdk" && -n "$project_unity" && -x "$HOME/Unity/Hub/Editor/$project_unity/Editor/Data/DotNetSdk/dotnet" ]]; then
		dotnet_sdk="$HOME/Unity/Hub/Editor/$project_unity/Editor/Data/DotNetSdk/dotnet"
	fi
	[[ -n "$dotnet_sdk" ]] || dotnet_sdk="$(ls -d "$HOME"/Unity/Hub/Editor/*/Editor/Data/DotNetSdk/dotnet 2>/dev/null | sort -V | tail -n 1)"
	[[ -x "$dotnet_sdk" ]] || { echo "No .NET SDK for the obfuscation audit (set BLITZ_DOTNET)" >&2; exit 6; }
	audit_out="$ROOT/.ci-cache/obfuscation-audit/out"
	DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$dotnet_sdk" build "$ROOT/Tools/ObfuscationAudit" -c Release -o "$audit_out" -v quiet -nologo >/dev/null
	for target in $TARGETS; do
		case "$target" in
			android) managed="${android_player%.apk}_BackUpThisFolder_ButDontShipItWithYourGame/Managed" ;;
			linux) managed="$linux_dir/BlitzLegacy_BackUpThisFolder_ButDontShipItWithYourGame/Managed" ;;
			windows) managed="$windows_dir/BlitzLegacy_BackUpThisFolder_ButDontShipItWithYourGame/Managed" ;;
		esac
		echo "Obfuscation audit: $target"
		"$dotnet_sdk" "$audit_out/ObfuscationAudit.dll" "$managed" | tee "$work_dir/obfuscation-$target.txt"
	done
fi

cp "$ROOT/tutorial_mods.txt" "$asset_dir/tutorial_mods.txt"
# Player-facing guide to online play (matchmaking, host, reconnects, blocks).
cp "$ROOT/$ONLINE_GUIDE" "$asset_dir/$ONLINE_GUIDE"
cat > "$asset_dir/BUILD_INFO.txt" <<EOF
Version: $VERSION
Android versionCode: $ANDROID_CODE
Mode: $MODE
Targets: $TARGETS
Build ID: $BUILD_ID
Revision: $REVISION
Desktop production-debug backend: IL2CPP
EOF

(
	cd "$asset_dir"
	sha256sum BlitzLegacy-* tutorial_mods.txt "$ONLINE_GUIDE" BUILD_INFO.txt >"SHA256SUMS-$VERSION$suffix.txt"
	sha256sum -c "SHA256SUMS-$VERSION$suffix.txt"
)

if [[ "$UPLOAD_SENTRY" == true ]]; then
	symbol_dir="$work_dir/symbols"
	mkdir -p "$symbol_dir"

	if [[ " $TARGETS " == *" android "* ]]; then
		# Unity writes one symbol archive per build next to the APK. Sentry
		# wants the unstripped ELF objects, so the archive is expanded first.
		mapfile -t archives < <(find "$ROOT/Build/Android" -maxdepth 1 -name '*symbols*.zip' -print | sort)
		if ((${#archives[@]} == 0)); then
			echo "No Android symbol archive under $ROOT/Build/Android: a crash from this APK could not be symbolicated" >&2
			exit 5
		fi
		mkdir -p "$symbol_dir/android"
		for archive in "${archives[@]}"; do
			unzip -qo "$archive" -d "$symbol_dir/android"
		done
	fi

	if [[ " $TARGETS " == *" linux "* ]]; then
		# The Linux IL2CPP player keeps its symbols inside the shipped binaries.
		mkdir -p "$symbol_dir/linux"
		find "$linux_dir" -type f \( -name 'BlitzLegacy' -o -name '*.so' -o -name '*.debug' -o -name '*.sym' \) \
			-exec cp -p {} "$symbol_dir/linux/" \;
	fi

    if [[ " $TARGETS " == *" windows "* ]]; then
        mkdir -p "$symbol_dir/windows"
        (cd "$windows_dir" && find . -type f \( -name '*.exe' -o -name '*.dll' -o -name '*.pdb' \) ! -path '*/Managed/*' \
            -exec cp --parents -p {} "$symbol_dir/windows/" \;)
    fi

	SENTRY_LOG_LEVEL=info sentry-cli debug-files upload \
		--no-zips \
		--type elf --type pe --type pdb \
		--wait-for 600 \
		--org "$SENTRY_ORG" \
		--project "$SENTRY_PROJECT" \
		"$symbol_dir"
fi

printf '\nLOCAL %s BUILD COMPLETE: %s\n' "$(echo "$MODE" | tr '[:lower:]' '[:upper:]')" "$VERSION"
printf 'Files ready for manual distribution: %s\n' "$asset_dir"
printf 'Checksums: %s/SHA256SUMS-%s%s.txt\n' "$asset_dir" "$VERSION" "$suffix"
printf 'Sentry release: blitz-legacy@%s dist %s\n' "$VERSION" "$BUILD_ID"
printf 'Native symbols: %s\n' "$([[ "$UPLOAD_SENTRY" == true ]] && echo "uploaded to $SENTRY_ORG/$SENTRY_PROJECT" || echo skipped)"
