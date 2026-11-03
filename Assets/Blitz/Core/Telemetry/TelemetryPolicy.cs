using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BlitzLegacy.Core.Telemetry
{
    /// <summary>
    /// Port of the Godot project's <c>scripts/telemetry/telemetry_config.gd</c>
    /// decisions, with every engine call removed so the rules themselves can be
    /// unit tested. The Unity/SDK glue lives in
    /// <c>BlitzLegacy.Runtime.Telemetry.BlitzTelemetry</c> and only supplies the
    /// flags and strings these functions judge.
    /// </summary>
    public static class TelemetryPolicy
    {
        /// <summary>Only the public ingestion DSN may enter a player build.</summary>
        public const string DsnEnvironmentVariable = "BLITZ_SENTRY_DSN";
        public const string DsnResource = "Data/sentry_dsn";

        /// <summary>Pinned by <c>Tools/bootstrap_sentry_sdk.py</c>.</summary>
        public const string SdkVersion = "4.10.3";

        /// <summary>Sentry release/dist prefix, so both engines report as one product.</summary>
        public const string ReleasePrefix = "blitz-legacy@";

        /// <summary>
        /// Opt-in file under the save root (internal app storage, Godot's
        /// <c>user://</c>). Deliberately separate from the encrypted account and
        /// settings saves so a wiped save never flips diagnostics back on.
        /// </summary>
        public const string PreferencePath = "diagnostics.json";

        /// <summary>Resources path of the build stamp written by the release scripts.</summary>
        public const string BuildResource = "Data/telemetry_build";

        /// <summary>Upper bound for any text handed to the SDK.</summary>
        public const int MaxTextLength = 8192;

        public const string AndroidSurfaceLost = "VK_ERROR_SURFACE_LOST_KHR";

        private static readonly Regex BearerPattern = new Regex(
            "Bearer\\s+[A-Za-z0-9._~+/-]+=*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex SecretPattern = new Regex(
            "(password|passwd|access_token|refresh_token|token|authorization|cookie|secret)" +
            "(\\s*[=:]\\s*)(\"[^\"]*\"|'[^']*'|[^\\s,;]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex EmailPattern = new Regex(
            "[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Defense in depth for unexpected engine error text. Only thread-safe
        /// value operations: the SDK hooks can run off the main thread.
        /// </summary>
        public static string Redact(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            var result = value.Length > MaxTextLength
                ? value.Substring(0, MaxTextLength)
                : value;
            result = BearerPattern.Replace(result, "Bearer [REDACTED]");
            result = SecretPattern.Replace(result, "$1$2[REDACTED]");
            return EmailPattern.Replace(result, "[REDACTED_EMAIL]");
        }

        /// <summary>
        /// Every observed surface-loss report follows an Android pause/stop or
        /// an orientation teardown, where the window really is gone. Genuine
        /// in-foreground Vulkan failures stay visible.
        /// </summary>
        public static bool IsPausedAndroidSurfaceLoss(string value, string runtimeOs, bool applicationPaused)
        {
            return applicationPaused
                && string.Equals(runtimeOs, "Android", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(value)
                && value.IndexOf(AndroidSurfaceLost, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Vulkan device faults make the driver dump its own state next to the
        /// failure the engine has already reported. The dumps carry no
        /// independent signal, yet each one used to open its own issue - and
        /// its own alert mail - for the very same fault.
        /// </summary>
        public static readonly string[] DriverLogDumps =
        {
            "=== Driver Memory Report ===",
            "Printing last known breadcrumbs",
            "Report from VK_EXT_device_fault",
        };

        /// <summary>
        /// Text fragments of a rendering backend that has stopped being able to
        /// build shaders or pipelines. These come from Vulkan itself or from
        /// Unity's graphics backend, so the first occurrence is the cause.
        /// </summary>
        public static readonly string[] PipelineFailureMarkers =
        {
            "VK_ERROR_INITIALIZATION_FAILED",
            "VK_ERROR_DEVICE_LOST",
            "Failed to create graphics pipeline",
            "Failed to create pipeline",
            "Shader is not supported on this GPU",
        };

        /// <summary>
        /// Once pipelines can no longer be created, every dependent draw call
        /// reports its own missing shader or material. Those are consequences,
        /// not faults, and they only get dropped after a real failure above was
        /// already reported for this session.
        /// </summary>
        public static readonly string[] PipelineCascadeMarkers =
        {
            "Trying to use a shader that failed to compile",
            "Trying to use an invalid material",
            "Material doesn't have a shader",
            "CommandBuffer: invalid pass",
            "Failed to create render texture",
        };

        private static bool _graphicsPipelineBroken;

        public static bool GraphicsPipelineBroken
        {
            get { return _graphicsPipelineBroken; }
        }

        public static bool IsDriverLogDump(string value)
        {
            return ContainsAny(value, DriverLogDumps);
        }

        /// <summary>
        /// Latched for the session: a device that starts failing to build
        /// pipelines keeps failing until the game is restarted.
        /// </summary>
        public static void NotePipelineState(string value)
        {
            if (_graphicsPipelineBroken)
            {
                return;
            }

            if (ContainsAny(value, PipelineFailureMarkers))
            {
                _graphicsPipelineBroken = true;
            }
        }

        public static void ResetPipelineState()
        {
            _graphicsPipelineBroken = false;
        }

        public static bool IsPipelineCascade(string value)
        {
            return _graphicsPipelineBroken && ContainsAny(value, PipelineCascadeMarkers);
        }

        /// <summary>
        /// The one place that decides whether a message is engine noise. Call
        /// it with every text of an event, so the latch sees the cause before
        /// it judges the consequences.
        /// </summary>
        /// <summary>
        /// Photon / Fusion socket exceptions the SDK raises on tasks nobody
        /// awaits (its own receive loop, DisconnectFromCloud). They only say
        /// that a connection went away, which NetSession already handles.
        /// </summary>
        public static readonly string[] UnobservedNetworkExceptions =
        {
            "Photon.Realtime.DisconnectException",
            "Photon.Realtime.OperationStartException",
            "System.Net.WebSockets.WebSocketException",
        };

        /// <summary>
        /// Fusion logs every failed StartGame as an error of its own, although
        /// the result goes back to NetSession, which retries, searches again
        /// or tells the player; the outcome stays in the breadcrumbs.
        /// </summary>
        public const string FusionStartFailed = "[Fusion]</color> StartGame Failed";

        /// <summary>
        /// True for online failures the game handles itself: an unobserved
        /// Photon socket exception (<paramref name="mechanism"/> is the Sentry
        /// mechanism of the event) or Fusion's duplicate StartGame error.
        /// </summary>
        public static bool IsHandledNetworkNoise(string mechanism, IEnumerable<string> exceptionTypes, IEnumerable<string> texts)
        {
            if (string.Equals(mechanism, "UnobservedTaskException", StringComparison.Ordinal) && exceptionTypes != null)
            {
                foreach (var type in exceptionTypes)
                {
                    if (Array.IndexOf(UnobservedNetworkExceptions, type) >= 0) { return true; }
                }
            }
            if (texts != null)
            {
                foreach (var text in texts)
                {
                    if (text != null && text.IndexOf(FusionStartFailed, StringComparison.Ordinal) >= 0) { return true; }
                }
            }
            return false;
        }

        public static bool IsEngineNoise(IEnumerable<string> values)
        {
            if (values == null)
            {
                return false;
            }

            var texts = new List<string>();
            foreach (var value in values)
            {
                texts.Add(value);
                NotePipelineState(value);
            }

            foreach (var value in texts)
            {
                if (IsDriverLogDump(value) || IsPipelineCascade(value))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Godot: qa / development / beta / production. The game is still in
        /// development, so a development build is "beta" rather than a
        /// throwaway environment.
        /// </summary>
        public static string EnvironmentName(bool isQa, bool isEditor, bool isDevelopmentBuild)
        {
            if (isQa)
            {
                return "qa";
            }

            if (isEditor)
            {
                return "development";
            }

            return isDevelopmentBuild ? "beta" : "production";
        }

        /// <summary>
        /// Headless editor stages (Tools/unity.sh) and an explicit
        /// <c>--telemetry-off</c> must never report, and nothing reports before
        /// the player has opted in.
        /// </summary>
        public static bool ShouldInitialize(
            bool isEditor,
            bool isHeadless,
            bool isQa,
            bool telemetryOff,
            bool collectionEnabled)
        {
            if (telemetryOff || !collectionEnabled)
            {
                return false;
            }

            if (isQa)
            {
                return true;
            }

            return !isEditor && !isHeadless;
        }

        private static bool ContainsAny(string value, string[] markers)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (var index = 0; index < markers.Length; index++)
            {
                if (value.IndexOf(markers[index], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
