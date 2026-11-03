using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Telemetry
{
    /// <summary>
    /// The build stamp the release script writes next to the player, ported
    /// from Godot's <c>res://telemetry_build.json</c>. It is what turns a
    /// stack trace into "this exact APK": release, dist and the revision the
    /// symbols were uploaded from.
    /// </summary>
    public sealed class TelemetryBuildInfo
    {
        public const string EditorBuildId = "editor";
        public const string WorkingTreeRevision = "working-tree";

        public string Version { get; private set; }
        public string BuildId { get; private set; }
        public string Revision { get; private set; }
        public string SdkVersion { get; private set; }

        public TelemetryBuildInfo(string version, string buildId, string revision, string sdkVersion)
        {
            Version = Fallback(version, "unknown");
            BuildId = Fallback(buildId, EditorBuildId);
            Revision = Fallback(revision, WorkingTreeRevision);
            SdkVersion = Fallback(sdkVersion, TelemetryPolicy.SdkVersion);
        }

        /// <summary>The unstamped case: running from the editor or a local player.</summary>
        public static TelemetryBuildInfo Editor(string version)
        {
            return new TelemetryBuildInfo(version, EditorBuildId, WorkingTreeRevision, TelemetryPolicy.SdkVersion);
        }

        public static TelemetryBuildInfo Parse(string json, string fallbackVersion)
        {
            JsonNode node;
            if (!JsonNode.TryParse(json, out node) || node == null || node.Kind != JsonKind.Object)
            {
                return Editor(fallbackVersion);
            }

            var version = node.Get("version").AsString();
            return new TelemetryBuildInfo(
                string.IsNullOrEmpty(version) ? fallbackVersion : version,
                node.Get("build_id").AsString(),
                node.Get("revision").AsString(),
                node.Get("sdk_version").AsString());
        }

        /// <summary>Sentry release name; identical in both engines.</summary>
        public string Release
        {
            get { return TelemetryPolicy.ReleasePrefix + Version; }
        }

        /// <summary>Sentry's dist field is short, so the build id is capped.</summary>
        public string Dist
        {
            get { return BuildId.Length > 64 ? BuildId.Substring(0, 64) : BuildId; }
        }

        public IDictionary<string, string> ToContext()
        {
            return new Dictionary<string, string>
            {
                { "version", Version },
                { "build_id", BuildId },
                { "revision", Revision },
                { "sdk_version", SdkVersion },
            };
        }

        public string ToJson()
        {
            var node = JsonNode.NewObject();
            node.Set("version", Version);
            node.Set("build_id", BuildId);
            node.Set("revision", Revision);
            node.Set("sdk_version", SdkVersion);
            return node.ToJson();
        }

        private static string Fallback(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
    }
}
