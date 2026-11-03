using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BlitzLegacy.Core.Telemetry;
using UnityEditor;
using UnityEngine;
#if BLITZ_SENTRY
using BlitzLegacy.Runtime.Telemetry;
#endif

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Generates the SDK's own options asset, the way the "Tools -> Sentry"
    /// window would. The asset is what the Android build integration reads
    /// while it generates the Gradle project, so without it the APK ships
    /// without the native crash handler and Unity says so:
    /// "Android native support disabled because Sentry has not been
    /// configured". It is generated rather than committed because it holds
    /// public deployment values generated from the local .env - and because
    /// the SDK package itself is
    /// installed by Tools/bootstrap_sentry_sdk.py instead of being tracked.
    /// </summary>
    public static class SentryProjectSetup
    {
        public const string OptionsAsset = "Assets/Resources/Sentry/SentryOptions.asset";
        public const string ConfigurationAsset = "Assets/Resources/Sentry/BlitzSentryOptions.asset";

        /// <summary>Entry point for <c>Tools/unity.sh sentry-setup</c>.</summary>
        public static void Run()
        {
            var ok = Ensure();
            EditorApplication.Exit(ok ? 0 : 1);
        }

        public static bool Ensure()
        {
#if !BLITZ_SENTRY
            Debug.LogWarning("[Blitz] sentry setup skipped: run Tools/bootstrap_sentry_sdk.py first");
            return false;
#else
            var dsn = BlitzTelemetry.DeploymentDsn();
            if (string.IsNullOrEmpty(dsn))
            {
                // Never reuse a previous deployment's native SDK options.
                AssetDatabase.DeleteAsset(OptionsAsset);
                AssetDatabase.DeleteAsset(ConfigurationAsset);
                AssetDatabase.SaveAssets();
                Debug.Log("[Blitz] Sentry is disabled: BLITZ_SENTRY_DSN is not configured");
                return false;
            }
            var optionsType = Type.GetType("Sentry.Unity.ScriptableSentryUnityOptions, Sentry.Unity");
            if (optionsType == null)
            {
                Debug.LogError("[Blitz] sentry setup failed: ScriptableSentryUnityOptions is missing");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(OptionsAsset));
            AssetDatabase.Refresh();

            var configuration = AssetDatabase.LoadAssetAtPath<BlitzSentryOptions>(ConfigurationAsset);
            if (configuration == null)
            {
                configuration = ScriptableObject.CreateInstance<BlitzSentryOptions>();
                AssetDatabase.CreateAsset(configuration, ConfigurationAsset);
            }

            var options = AssetDatabase.LoadAssetAtPath<ScriptableObject>(OptionsAsset);
            if (options == null || options.GetType() != optionsType)
            {
                options = ScriptableObject.CreateInstance(optionsType);
                AssetDatabase.CreateAsset(options, OptionsAsset);
            }

            // Only the handful of values the native layer and the build
            // integration need; everything else is decided in code by
            // BlitzTelemetry.Configure, which this asset points at.
            var values = new Dictionary<string, object>
            {
                { "Enabled", true },
                { "Dsn", dsn },
                { "OptionsConfiguration", configuration },
                { "CaptureInEditor", false },
                { "Debug", false },
                { "SendDefaultPii", false },
                { "AttachScreenshot", false },
                { "AttachViewHierarchy", false },
                { "AttachStacktrace", true },
                { "AutoSessionTracking", true },
                { "MaxBreadcrumbs", 100 },
                { "EnableStructuredLogging", false },
                { "AnrDetectionEnabled", true },
                { "AndroidNativeSupportEnabled", true },
                { "NdkIntegrationEnabled", true },
                { "NdkScopeSyncEnabled", true },
                { "LinuxNativeSupportEnabled", true },
                { "Il2CppLineNumberSupportEnabled", true },
            };

            foreach (var pair in values)
            {
                if (!Assign(options, optionsType, pair.Key, pair.Value))
                {
                    return false;
                }
            }

            EditorUtility.SetDirty(configuration);
            EditorUtility.SetDirty(options);
            AssetDatabase.SaveAssets();
            Debug.Log("[Blitz] sentry options asset ready: " + OptionsAsset
                + " (android native + ndk on, structured logs off)");
            return true;
#endif
        }

        /// <summary>
        /// The SDK stores its options in serialized auto-properties, so the
        /// backing fields are the only way in from a script.
        /// </summary>
        private static bool Assign(ScriptableObject target, Type type, string property, object value)
        {
            var field = type.GetField(
                "<" + property + ">k__BackingField",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                Debug.LogError("[Blitz] sentry setup failed: option '" + property + "' no longer exists");
                return false;
            }

            field.SetValue(target, value);
            return true;
        }
    }
}
