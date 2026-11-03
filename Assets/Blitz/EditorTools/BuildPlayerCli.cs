using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Telemetry;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Build;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Headless player builds.</summary>
    public static class BuildPlayerCli
    {
        public const string AndroidOutput = "Build/Android/BlitzLegacy.apk";

        /// <summary>
        /// The development APK. Godot ships the account toolbox behind
        /// <c>OS.is_debug_build()</c>, which is a debug export template there
        /// and <c>Debug.isDebugBuild</c> here, so the hangar drawer only shows
        /// its debug rows in this build. Written beside the release APK rather
        /// than over it, so both can sit on a device at once.
        /// </summary>
        public const string AndroidDebugOutput = "Build/Android/BlitzLegacy-debug.apk";

        /// <summary>
        /// The debug APK the alpha testers get: same tools, no development
        /// player over the game. Written beside the other two so all three can
        /// sit on a device at once.
        /// </summary>
        public const string AndroidProductionDebugOutput =
            "Build/Android/BlitzLegacy-productiondebug.apk";

        /// <summary>Production-debug desktop players use IL2CPP. Windows
        /// requires a Windows editor and the native C++ toolchain.</summary>
        public const string LinuxOutput = "Build/Linux/BlitzLegacy";

        public const string LinuxDebugOutput = "Build/Linux-debug/BlitzLegacy";
        public const string LinuxProductionDebugOutput = "Build/Linux-productiondebug/BlitzLegacy";
        public const string WindowsProductionDebugOutput = "Build/Windows-productiondebug/BlitzLegacy.exe";


        /// <summary>
        /// Where the build stamp the player reports with is written. It is
        /// generated per build and git-ignored, so an unstamped working tree
        /// reports as the editor build it is.
        /// </summary>
        public const string BuildStampAsset = "Assets/Blitz/Resources/Data/telemetry_build.json";

        public static void Android()
        {
            Build(BuildTarget.Android, AndroidOutput, BuildOptions.None);
        }

        public static void AndroidDebug()
        {
            Build(BuildTarget.Android, AndroidDebugOutput, BuildOptions.Development);
        }

        /// <summary>
        /// The production-debug APK: the debug tools of the development build
        /// without Unity's development player on top of them.
        ///
        /// Godot's debug export template only adds the debug tools, whereas
        /// <c>BuildOptions.Development</c> also turns on the engine's on-screen
        /// log console and its watermark, which sit over the game for the
        /// testers. This build therefore drops that option and defines
        /// <c>BLITZ_DEBUG_TOOLS</c> instead, so <c>BlitzBuildFlags.DebugTools</c>
        /// still reports true and the hangar keeps its account toolbox. The
        /// native symbols are emitted exactly as for the other Android builds.
        /// </summary>
        public static void AndroidProductionDebug()
        {
            Build(
                BuildTarget.Android,
                AndroidProductionDebugOutput,
                BuildOptions.None,
                new[] { "BLITZ_DEBUG_TOOLS" });
        }

        public static void Linux()
        {
            Build(BuildTarget.StandaloneLinux64, LinuxOutput, BuildOptions.None);
        }

        public static void LinuxDebug()
        {
            Build(BuildTarget.StandaloneLinux64, LinuxDebugOutput, BuildOptions.Development);
        }

        public static void LinuxProductionDebug()
        {
            Build(BuildTarget.StandaloneLinux64, LinuxProductionDebugOutput,
                BuildOptions.None, new[] { "BLITZ_DEBUG_TOOLS" });
        }

        public static void WindowsProductionDebug()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Debug.LogError("[Blitz] Windows productionDebug requires Windows IL2CPP; refusing Mono fallback. Run Tools/windows_il2cpp.ps1 in Windows.");
                EditorApplication.Exit(2);
                return;
            }
            Build(BuildTarget.StandaloneWindows64, WindowsProductionDebugOutput,
                BuildOptions.None, new[] { "BLITZ_DEBUG_TOOLS" });
        }

        /// <summary>
        /// Stamps the build the diagnostics report with, from the values the
        /// release script exports. Without them the stamp is deleted rather
        /// than filled with a guess, so a local build cannot claim a revision
        /// it was not built from.
        /// </summary>
        public static void WriteBuildStamp()
        {
            var version = Environment.GetEnvironmentVariable("BLITZ_RELEASE_VERSION");
            var buildId = Environment.GetEnvironmentVariable("BLITZ_BUILD_ID");
            var revision = Environment.GetEnvironmentVariable("BLITZ_REVISION");

            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(buildId))
            {
                if (File.Exists(BuildStampAsset))
                {
                    AssetDatabase.DeleteAsset(BuildStampAsset);
                }

                Debug.Log("[Blitz] telemetry build stamp: none (unstamped editor build)");
                return;
            }

            var info = new TelemetryBuildInfo(version, buildId, revision, TelemetryPolicy.SdkVersion);
            Directory.CreateDirectory(Path.GetDirectoryName(BuildStampAsset));
            File.WriteAllText(BuildStampAsset, info.ToJson());
            AssetDatabase.ImportAsset(BuildStampAsset, ImportAssetOptions.ForceUpdate);
            Debug.Log("[Blitz] telemetry build stamp: release=" + info.Release
                + " dist=" + info.Dist + " revision=" + info.Revision);
        }

        /// <summary>
        /// The keystore the release script exported, applied for one build and
        /// then put back. The values live only in the local, git-ignored
        /// release config - never in the committed project settings - and a
        /// build without them must fail rather than use Unity's debug certificate,
        /// which cannot update a release-signed installed APK.
        /// </summary>
        private struct AndroidSigning
        {
            private bool _custom;
            private string _keystore;
            private string _keystorePass;
            private string _alias;
            private string _aliasPass;

            public static AndroidSigning Capture()
            {
                return new AndroidSigning
                {
                    _custom = PlayerSettings.Android.useCustomKeystore,
                    _keystore = PlayerSettings.Android.keystoreName,
                    _keystorePass = PlayerSettings.Android.keystorePass,
                    _alias = PlayerSettings.Android.keyaliasName,
                    _aliasPass = PlayerSettings.Android.keyaliasPass,
                };
            }

            public void Restore()
            {
                PlayerSettings.Android.useCustomKeystore = _custom;
                PlayerSettings.Android.keystoreName = _keystore;
                PlayerSettings.Android.keystorePass = _keystorePass;
                PlayerSettings.Android.keyaliasName = _alias;
                PlayerSettings.Android.keyaliasPass = _aliasPass;
            }

            public static void ApplyFromEnvironment()
            {
                var keystore = Environment.GetEnvironmentVariable("BLITZ_ANDROID_KEYSTORE");
                if (string.IsNullOrEmpty(keystore) || !File.Exists(keystore)
                    || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BLITZ_ANDROID_KEYSTORE_PASSWORD"))
                    || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BLITZ_ANDROID_KEY_ALIAS"))
                    || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BLITZ_ANDROID_KEY_PASSWORD")))
                {
                    throw new BuildFailedException("Release Android signing is required; configure BLITZ_ANDROID_KEYSTORE and key credentials in the local .env");
                }

                PlayerSettings.Android.useCustomKeystore = true;
                PlayerSettings.Android.keystoreName = keystore;
                PlayerSettings.Android.keystorePass =
                    Environment.GetEnvironmentVariable("BLITZ_ANDROID_KEYSTORE_PASSWORD");
                PlayerSettings.Android.keyaliasName =
                    Environment.GetEnvironmentVariable("BLITZ_ANDROID_KEY_ALIAS");
                PlayerSettings.Android.keyaliasPass =
                    Environment.GetEnvironmentVariable("BLITZ_ANDROID_KEY_PASSWORD");
                Debug.Log("[Blitz] android signing: keystore " + Path.GetFileName(keystore)
                    + " alias " + PlayerSettings.Android.keyaliasName);
            }
        }

        private static void Build(
            BuildTarget target, string output, BuildOptions options, string[] extraDefines = null)
        {
            if (!ProjectBootstrap.EnsureRuntimeShaders())
            {
                Debug.LogError("[Blitz] build stopped: runtime shaders are missing");
                EditorApplication.Exit(1);
                return;
            }

            // Without the generated material asset the variant stripper drops
            // the transparent additive smoke variant and the player renders
            // the hangar smoke as solid white quads.
            if (!ProjectBootstrap.EnsureRuntimeMaterials())
            {
                Debug.LogError("[Blitz] build stopped: runtime materials are missing");
                EditorApplication.Exit(1);
                return;
            }

            WriteBuildStamp();
            NetPrefabSetup.EnsureAppIdResource();

            // The SDK's options asset is what turns the native crash handler
            // on while the Gradle project is generated, so it is refreshed for
            // every build rather than trusted to exist.
            if (!SentryProjectSetup.Ensure())
            {
                Debug.LogWarning("[Blitz] building without Sentry: diagnostics will be inert");
            }

            AssetDatabase.SaveAssets();

            var signing = AndroidSigning.Capture();
            if (target == BuildTarget.Android)
            {
                // Godot exports the launcher icon straight from
                // project.godot's config/icon; Unity keeps a texture per icon
                // kind in the project settings, so the ported bitmaps are
                // wired up here and no APK leaves with Unity's placeholder.
                if (!AppIconSetup.Ensure())
                {
                    Debug.LogError("[Blitz] build stopped: the ported app icon is missing");
                    EditorApplication.Exit(1);
                    return;
                }

                // A stripped IL2CPP library gives Sentry nothing to
                // symbolicate, so every Android build emits the symbol archive
                // that Tools/local_release.sh uploads.
#if UNITY_ANDROID_API
                UnityEditor.Android.UserBuildSettings.DebugSymbols.level =
                    Unity.Android.Types.DebugSymbolLevel.Full;
                UnityEditor.Android.UserBuildSettings.DebugSymbols.format =
                    Unity.Android.Types.DebugSymbolFormat.Zip;
                AndroidSigning.ApplyFromEnvironment();
#else
                Debug.LogError("[Blitz] Android API is inactive or missing; install Android Build Support and launch this build with -buildTarget Android");
                EditorApplication.Exit(2);
                return;
#endif
            }

            var scenes = new List<string>();
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled)
                {
                    scenes.Add(scene.path);
                }
            }

            if (scenes.Count == 0)
            {
                Debug.LogError("[Blitz] no enabled scenes; run Tools/unity.sh scenes first");
                EditorApplication.Exit(1);
                return;
            }

            var directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var standaloneBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            var nativeDesktop = (target == BuildTarget.StandaloneWindows64
                || target == BuildTarget.StandaloneLinux64)
                && extraDefines != null && Array.IndexOf(extraDefines, "BLITZ_DEBUG_TOOLS") >= 0;
            if (nativeDesktop)
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);

            // Debug-tools players carry the opt-in BlitzPerfMonitor, which reads
            // CPU/GPU frame timings; release players keep the stats off.
            var frameTimingStats = PlayerSettings.enableFrameTimingStats;
            var debugTools = extraDefines != null && Array.IndexOf(extraDefines, "BLITZ_DEBUG_TOOLS") >= 0;
            PlayerSettings.enableFrameTimingStats = frameTimingStats || debugTools
                || (options & BuildOptions.Development) != 0;

            // Anti-dump: every non-development player (release and the
            // production-debug alpha builds) ships with CJK-renamed metadata.
            // BLITZ_OBFUSCATE=0 opts out for bisecting an obfuscation bug.
            var obfuscate = (options & BuildOptions.Development) == 0
                && Environment.GetEnvironmentVariable("BLITZ_OBFUSCATE") != "0";
            ObfuscationSetup.Apply(obfuscate, target);
            bool? obfuscated = null;
            Action<Obfuz.Unity.ObfuscationEndEventArgs> onObfuscated = e => obfuscated = e.success;
            Obfuz.Unity.ObfuscationProcess.OnObfuscationEnd += onObfuscated;

            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes.ToArray(),
                    locationPathName = output,
                    target = target,
                    targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                    options = options,
                    extraScriptingDefines = extraDefines ?? Array.Empty<string>(),
                });
            }
            finally
            {
                Obfuz.Unity.ObfuscationProcess.OnObfuscationEnd -= onObfuscated;
                ObfuscationSetup.Apply(false);
                signing.Restore();
                PlayerSettings.enableFrameTimingStats = frameTimingStats;
                if (nativeDesktop)
                    PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, standaloneBackend);
            }

            var summary = report.summary;
            if (obfuscate && summary.result == BuildResult.Succeeded)
            {
                // Obfuz logs and swallows its own failures, which would ship
                // readable metadata; a release must never do that silently.
                if (obfuscated != true)
                {
                    Debug.LogError("[Blitz] build stopped: symbol obfuscation "
                        + (obfuscated == null ? "did not run" : "failed"));
                    EditorApplication.Exit(1);
                    return;
                }
                // Keep the mapping beside the build: it is the only way to
                // read obfuscated names in Sentry stacks of this exact build.
                var mappingCopy = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(output)), "symbol-mapping.xml");
                File.Copy(ObfuscationSetup.MappingFile, mappingCopy, true);
                Debug.Log("[Blitz] obfuscation: CJK symbols, mapping " + mappingCopy);
            }
            Debug.Log(
                "[Blitz] " + target + " build " + summary.result +
                " backend=" + (nativeDesktop || target == BuildTarget.Android ? "IL2CPP" : standaloneBackend.ToString()) +
                " development=" + ((options & BuildOptions.Development) != 0) +
                " defines=" + (extraDefines == null || extraDefines.Length == 0
                    ? "-" : string.Join(";", extraDefines)) +
                " size=" + summary.totalSize +
                " errors=" + summary.totalErrors);

            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
