#!/bin/sh
# Headless driver for the Blitz Legacy Unity project. No GUI is ever needed.
set -e
if [ "${BLITZ_ENV_READY:-}" != 1 ]; then
  exec python3 "$(dirname "$0")/unity.py" "$@"
fi
# Follow the version the project is actually opened with, then fall back to
# whatever editor the Hub has installed.
HUB="${UNITY_HUB_EDITORS:-$HOME/Unity/Hub/Editor}"
PROJ_VERSION="$(sed -n 's/^m_EditorVersion: //p' \
  "$(cd "$(dirname "$0")/.." && pwd)/ProjectSettings/ProjectVersion.txt" 2>/dev/null)"
if [ -z "${UNITY:-}" ] && [ -n "$PROJ_VERSION" ] && [ -x "$HUB/$PROJ_VERSION/Editor/Unity" ]; then
  UNITY="$HUB/$PROJ_VERSION/Editor/Unity"
fi
if [ -z "${UNITY:-}" ]; then
  UNITY="$(ls -d "$HUB"/*/Editor/Unity 2>/dev/null | sort -V | tail -n 1)"
fi
PROJ="$(cd "$(dirname "$0")/.." && pwd)"
# Unity's Linux IL2CPP Clang needs non-wide ncurses on Arch-based hosts.
# Optional distro libraries are isolated in the CI cache, never game assets.
COMPAT="$PROJ/.ci-cache/linux-compat/usr/lib/x86_64-linux-gnu"
if [ -f "$COMPAT/libncurses.so.6" ]; then
  export LD_LIBRARY_PATH="$COMPAT${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
fi


# Batchmode occasionally leaves lock files after a crash/forced timeout. Remove
# only stale locks: never interfere with a genuinely running editor.
if ! pgrep -x Unity >/dev/null 2>&1; then
  rm -f "$PROJ/Temp/UnityLockfile" \
        "$PROJ/Library/ArtifactDB-lock" \
        "$PROJ/Library/SourceAssetDB-lock"
fi

cmd="${1:-}"
set -- -batchmode -nographics -projectPath "$PROJ" -logFile -
case "$cmd" in
  import)  exec "$UNITY" "$@" -quit ;;
  compile) exec "$UNITY" "$@" -quit -executeMethod BlitzLegacy.EditorTools.Ci.CompileOnly ;;
  setup)   exec "$UNITY" "$@" -quit -executeMethod BlitzLegacy.EditorTools.ProjectBootstrap.ApplyAll ;;
  net-setup) exec "$UNITY" "$@" -quit -executeMethod BlitzLegacy.EditorTools.NetPrefabSetup.Run ;;
  scenes)  exec "$UNITY" "$@" -quit -executeMethod BlitzLegacy.EditorTools.SceneGen.GenerateAll ;;
  test)    exec "$UNITY" "$@" -runTests -testPlatform EditMode \
                 -testResults "$PROJ/Logs/editmode-results.xml" \
                 ${BLITZ_TEST_FILTER:+-testFilter "$BLITZ_TEST_FILTER"} ;;
  smoke)        exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.LoginScreenSmoke.Run ;;
  hangar-smoke) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.HangarScreenSmoke.Run ;;
  hangar-perf) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.HangarPerformanceCli.Run ;;
  uprising-perf) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile Logs/uprising-perf.log -executeMethod BlitzLegacy.EditorTools.UprisingPerformanceCli.Run ;;
  uprising-perf-battle) exec env SDL_HIDAPI_JOYSTICK_DISABLE_UDEV=1 DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile Logs/uprising-perf-battle.log -executeMethod BlitzLegacy.EditorTools.UprisingPerformanceCli.RunBattle ;;
  uprising-profile) exec env SDL_HIDAPI_JOYSTICK_DISABLE_UDEV=1 DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile Logs/uprising-profile.log -executeMethod BlitzLegacy.EditorTools.UprisingProfileCli.Run ;;
  duplet-smoke) exec env SDL_HIDAPI_JOYSTICK_DISABLE_UDEV=1 "$UNITY" "$@" -forceAudio -executeMethod BlitzLegacy.EditorTools.UprisingDupletCli.Run ;;
  rollover-smoke) exec env SDL_HIDAPI_JOYSTICK_DISABLE_UDEV=1 "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.TankRolloverCli.Run ;;
  bigboss-smoke) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.BigBossCli.Smoke ;;
  bigboss-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile Logs/bigboss-render.log -executeMethod BlitzLegacy.EditorTools.BigBossCli.Render ;;
  bigboss-perf) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile Logs/bigboss-perf.log -executeMethod BlitzLegacy.EditorTools.BigBossPerformanceCli.Run ;;
  uprising-smoke) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.UprisingSmokeCli.Run ;;
  bot-probe) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.BotActivityProbeCli.Run ;;
  object907-smoke) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.Object907SmokeCli.Run ;;
  combat-recovery-smoke) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.CombatRecoverySmokeCli.Run ;;
  battle-regression-smoke) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.BattleRegressionSmokeCli.Run ;;
  battle-physics-regression) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.BattlePhysicsRegressionCli.Run ;;
  contact-physics-smoke) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.ContactPhysicsSmokeCli.Run ;;
  ride-height-smoke) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.RideHeightSmokeCli.Run ;;
  map-spawn-drive-smoke) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.MapSpawnDriveSmokeCli.Run ;;
  water-smoke) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.WaterSmokeCli.Run ;;
  wreck1120-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile - -executeMethod BlitzLegacy.EditorTools.WreckRenderCli.Render1120 ;;
  combat-fx-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile - -executeMethod BlitzLegacy.EditorTools.CombatFxRenderCli.Render ;;
  modules480-smoke) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.Modules480SmokeCli.Run ;;
  uprising-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile Logs/uprising-render.log -executeMethod BlitzLegacy.EditorTools.UprisingRenderCli.Render ;;
  battle-smoke) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.BattleScreenSmoke.Run ;;
  battle-smoke-ducks) exec env BLITZ_SMOKE_MODE=world_of_ducks "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.BattleScreenSmoke.Run ;;
  # The joystick and the thumb buttons only exist on a phone, so the mobile
  # control layer needs its own stage instead of never being walked at all.
  battle-smoke-mobile) exec env BLITZ_SMOKE_MOBILE=1 "$UNITY" "$@" \
                 -executeMethod BlitzLegacy.EditorTools.BattleScreenSmoke.Run ;;
  flow-smoke) exec env BLITZ_FLOW_FAST=1 "$UNITY" "$@" \
                 -executeMethod BlitzLegacy.EditorTools.BattleFlowSmoke.Run ;;
  # The offscreen hangar render needs a real graphics device, so this is
  # the one stage that cannot pass -nographics.
  # Every redesigned menu screen in the Classic and the Modern style.
  menu-ui-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile Logs/menu-ui-render.log \
                 -executeMethod BlitzLegacy.EditorTools.MenuUiRenderCli.Render ;;
  hangar-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile - \
                 -executeMethod BlitzLegacy.EditorTools.HangarRenderCli.Render ;;
  hangar480-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile - \
                 -executeMethod BlitzLegacy.EditorTools.ClientHangarRenderCli.Render ;;
  # The Gatling and drum clips only show on a real graphics device.
  gun-clip-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile - \
                 -executeMethod BlitzLegacy.EditorTools.GunClipRenderCli.Render ;;
  hangar-calibrate) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile - \
                 -executeMethod BlitzLegacy.EditorTools.HangarRenderCli.Calibrate ;;
  # The battle frame is the only cheap way to see the sun, the sky and the
  # props without flashing an APK, so it needs a graphics device too.
  # The destroyed tank is the only effect the smoke tests cannot read, so
  # its frames come from a graphics device too.
  wreck-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile - \
                 -executeMethod BlitzLegacy.EditorTools.WreckRenderCli.Render ;;
  battle-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile - \
                 -executeMethod BlitzLegacy.EditorTools.BattleRenderCli.Render ;;
  # The result screen is pure uGUI, but a canvas still needs a graphics
  # device to be rasterised into a comparable frame.
  result-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile - \
                 -executeMethod BlitzLegacy.EditorTools.BattleResultRenderCli.Render ;;
  # The Tab scoreboard and the map intro lists, held against the Godot rows.
  lists-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile - \
                 -executeMethod BlitzLegacy.EditorTools.BattleListsRenderCli.Render ;;
  # The minimap of every ported map with tanks on its spawns.
  minimap-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile - \
                 -executeMethod BlitzLegacy.EditorTools.BattleMinimapRenderCli.Render ;;
  # The hit direction markers and the sixth-sense lamp round the crosshair.
  hit-markers-render) exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile - \
                 -executeMethod BlitzLegacy.EditorTools.BattleHitMarkersRenderCli.Render ;;
  # A map ported straight from the client ships no source navmesh, so the
  # port bakes one with the agent the source bake of Rudniki uses.
  navmesh-bake) exec "$UNITY" "$@" -executeMethod BlitzLegacy.EditorTools.NavmeshBakeCli.Bake ;;
  # The scope overlay is geometry, so the only honest check is a frame of
  # every tank with its armour plates rendered on their own.
  armor-sight)  exec env DISPLAY="${DISPLAY:-:0}" "$UNITY" -batchmode -projectPath "$PROJ" -logFile - \
                 -executeMethod BlitzLegacy.EditorTools.ArmorSightRenderCli.Render ;;
  # The Sentry SDK is installed by Tools/bootstrap_sentry_sdk.py and
  # configured by this stage; the player builds run it themselves.
  obfuz-setup)  exec "$UNITY" "$@" -quit -executeMethod BlitzLegacy.EditorTools.ObfuscationSetup.Configure ;;
  sentry-setup) exec "$UNITY" "$@" -quit -executeMethod BlitzLegacy.EditorTools.SentryProjectSetup.Run ;;
  # The launcher icon ported from project.godot's config/icon by
  # Tools/port_godot_icon.py. The player builds apply it themselves; this
  # stage only re-applies it after the port has been re-run.
  icon)         exec "$UNITY" "$@" -quit -executeMethod BlitzLegacy.EditorTools.AppIconSetup.Run ;;
  apk)          exec "$UNITY" "$@" -buildTarget Android -quit -executeMethod BlitzLegacy.EditorTools.BuildPlayerCli.Android ;;
  # The account toolbox in the hangar drawer is debug-build only, exactly as
  # OS.is_debug_build() gates it in the source, so it needs this APK.
  apk-debug)    exec "$UNITY" "$@" -buildTarget Android -quit -executeMethod BlitzLegacy.EditorTools.BuildPlayerCli.AndroidDebug ;;
  # The same debug tools without BuildOptions.Development, so the engine
  # draws no log console or watermark over the game. This is the APK the
  # open alpha is handed; the native symbols are emitted either way.
  apk-production-debug) exec "$UNITY" "$@" -buildTarget Android -quit -executeMethod BlitzLegacy.EditorTools.BuildPlayerCli.AndroidProductionDebug ;;
  # The desktop player this machine can build and run itself, which is what
  # makes the diagnostics reachable without flashing a phone. A Windows
  # IL2CPP player cannot be cross-compiled from Linux, so there is no target
  # for it here.
  # Select the target before assemblies load: sysroot providers compiled for
  # Android do not implement the Linux API required by IL2CPP discovery.
  linux)        exec "$UNITY" "$@" -buildTarget Linux64 -quit -executeMethod BlitzLegacy.EditorTools.BuildPlayerCli.Linux ;;
  linux-debug)  exec "$UNITY" "$@" -buildTarget Linux64 -quit -executeMethod BlitzLegacy.EditorTools.BuildPlayerCli.LinuxDebug ;;
  linux-production-debug) exec "$UNITY" "$@" -buildTarget Linux64 -quit -executeMethod BlitzLegacy.EditorTools.BuildPlayerCli.LinuxProductionDebug ;;
  windows-production-debug) exec "$UNITY" "$@" -quit -executeMethod BlitzLegacy.EditorTools.BuildPlayerCli.WindowsProductionDebug ;;
  *) echo "usage: Tools/unity.sh {import|compile|setup|scenes|icon|test|smoke|hangar-smoke|hangar-perf|battle-smoke|battle-smoke-ducks|battle-smoke-mobile|battle-regression-smoke|combat-recovery-smoke|object907-smoke|battle-physics-regression|duplet-smoke|rollover-smoke|uprising-smoke|bot-probe|modules480-smoke|contact-physics-smoke|wreck1120-render|combat-fx-render|uprising-render|flow-smoke|hangar-render|menu-ui-render|hangar480-render|hangar-calibrate|battle-render|wreck-render|result-render|lists-render|minimap-render|hit-markers-render|navmesh-bake|armor-sight|apk|apk-debug|apk-production-debug|linux|linux-debug|sentry-setup}" >&2; exit 2 ;;
esac
