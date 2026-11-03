using System.Collections.Generic;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Telemetry;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The rules the Godot client learned the hard way, ported one for one.
    /// Each of these used to be a real problem in the Godot project's Sentry
    /// inbox: player secrets inside engine error text, a paused Android app
    /// reporting the surface it legitimately lost, and one broken graphics
    /// pipeline opening dozens of issues for the draw calls that failed
    /// because of it. The rules are engine-free precisely so they can be
    /// checked here instead of by reading the inbox again.
    /// </summary>
    public sealed class TelemetryPolicyTests
    {
        [SetUp]
        public void ResetLatch()
        {
            // The pipeline latch is session state by design, so each test has
            // to start from a healthy renderer.
            TelemetryPolicy.ResetPipelineState();
        }

        [Test]
        public void Redact_RemovesTokensSecretsAndEmails()
        {
            Assert.AreEqual(
                "GET /api failed: Bearer [REDACTED]",
                TelemetryPolicy.Redact("GET /api failed: Bearer abc.def-123_456=="));
            Assert.AreEqual(
                "password: [REDACTED]",
                TelemetryPolicy.Redact("password: hunter2"));
            Assert.AreEqual(
                "access_token=[REDACTED]",
                TelemetryPolicy.Redact("access_token=eyJhbGciOi"));
            Assert.AreEqual(
                "login failed for [REDACTED_EMAIL]",
                TelemetryPolicy.Redact("login failed for player@example.com"));
        }

        [Test]
        public void Redact_KeepsOrdinaryErrorTextAndCapsLength()
        {
            const string message = "NullReferenceException in BattleSession.Fire";
            Assert.AreEqual(message, TelemetryPolicy.Redact(message));
            Assert.AreEqual(string.Empty, TelemetryPolicy.Redact(null));

            var flood = new string('x', TelemetryPolicy.MaxTextLength * 3);
            Assert.AreEqual(TelemetryPolicy.MaxTextLength, TelemetryPolicy.Redact(flood).Length);
        }

        [Test]
        public void SurfaceLoss_IsOnlyNoiseWhileAndroidIsPaused()
        {
            const string message = "Vulkan: vkQueuePresentKHR: VK_ERROR_SURFACE_LOST_KHR";
            Assert.IsTrue(TelemetryPolicy.IsPausedAndroidSurfaceLoss(message, "Android", true));
            // A foreground failure is a real one, and the desktop player never
            // loses its surface to a pause at all.
            Assert.IsFalse(TelemetryPolicy.IsPausedAndroidSurfaceLoss(message, "Android", false));
            Assert.IsFalse(TelemetryPolicy.IsPausedAndroidSurfaceLoss(message, "Linux", true));
            Assert.IsFalse(TelemetryPolicy.IsPausedAndroidSurfaceLoss(
                "battle ended", "Android", true));
        }

        [Test]
        public void DriverLogDumps_AreNoiseOnTheirOwn()
        {
            Assert.IsTrue(TelemetryPolicy.IsDriverLogDump(
                "=== Driver Memory Report ===\nheap 0: 512 MiB"));
            Assert.IsFalse(TelemetryPolicy.IsDriverLogDump("hangar loaded"));
        }

        [Test]
        public void PipelineCascade_IsSuppressedOnlyAfterTheCauseWasReported()
        {
            const string cascade = "Trying to use a shader that failed to compile";

            // Nothing has failed yet, so this text is a fault of its own.
            Assert.IsFalse(TelemetryPolicy.IsPipelineCascade(cascade));

            Assert.IsFalse(TelemetryPolicy.IsEngineNoise(new List<string>
            {
                "Vulkan: vkCreateGraphicsPipelines: VK_ERROR_INITIALIZATION_FAILED",
            }), "the first pipeline failure has to reach Sentry");
            Assert.IsTrue(TelemetryPolicy.GraphicsPipelineBroken);

            // Latched for the session: the device keeps failing until restart.
            Assert.IsTrue(TelemetryPolicy.IsPipelineCascade(cascade));
            Assert.IsTrue(TelemetryPolicy.IsEngineNoise(new List<string> { cascade }));

            TelemetryPolicy.ResetPipelineState();
            Assert.IsFalse(TelemetryPolicy.IsPipelineCascade(cascade));
        }

        [Test]
        public void HandledNetworkNoise_OnlyTheFailuresNetSessionHandles()
        {
            var aggregate = "System.AggregateException";
            Assert.IsTrue(TelemetryPolicy.IsHandledNetworkNoise("UnobservedTaskException",
                new[] { aggregate, "System.Net.WebSockets.WebSocketException" }, null),
                "Photon's own receive loop after a cancelled queue");
            Assert.IsTrue(TelemetryPolicy.IsHandledNetworkNoise("UnobservedTaskException",
                new[] { aggregate, "Photon.Realtime.DisconnectException" }, null));
            Assert.IsFalse(TelemetryPolicy.IsHandledNetworkNoise("Unity.LogException",
                new[] { "Photon.Realtime.DisconnectException" }, null),
                "a socket exception the game itself let through is still a bug");
            Assert.IsFalse(TelemetryPolicy.IsHandledNetworkNoise("UnobservedTaskException",
                new[] { aggregate, "System.NullReferenceException" }, null),
                "an unobserved NullReference is never noise");
            Assert.IsTrue(TelemetryPolicy.IsHandledNetworkNoise(null, null, new[]
            {
                "<color=#144078>[Fusion]</color> StartGame Failed: [StartGameResult: Ok:False, ShutdownReason: PhotonCloudTimeout",
            }));
            Assert.IsFalse(TelemetryPolicy.IsHandledNetworkNoise(null, null, new[]
            {
                "System.NullReferenceException: Object reference not set to an instance of an object.",
            }));
        }

        [Test]
        public void EnvironmentName_MatchesTheGodotNames()
        {
            Assert.AreEqual("qa", TelemetryPolicy.EnvironmentName(true, false, false));
            Assert.AreEqual("development", TelemetryPolicy.EnvironmentName(false, true, true));
            Assert.AreEqual("beta", TelemetryPolicy.EnvironmentName(false, false, true));
            Assert.AreEqual("production", TelemetryPolicy.EnvironmentName(false, false, false));
        }

        [Test]
        public void ShouldInitialize_NeedsConsentAndARealPlayer()
        {
            Assert.IsTrue(TelemetryPolicy.ShouldInitialize(false, false, false, false, true));
            Assert.IsFalse(TelemetryPolicy.ShouldInitialize(false, false, false, false, false),
                "a player who turned collection off is never reported for");
            Assert.IsFalse(TelemetryPolicy.ShouldInitialize(false, false, false, true, true),
                "--telemetry-off wins over the opt-in");
            Assert.IsFalse(TelemetryPolicy.ShouldInitialize(true, false, false, false, true),
                "the editor is not a player");
            Assert.IsFalse(TelemetryPolicy.ShouldInitialize(false, true, false, false, true),
                "the headless Tools/unity.sh stages are not a player");
            Assert.IsTrue(TelemetryPolicy.ShouldInitialize(true, true, true, false, true),
                "a QA run is exactly the case that has to report from batchmode");
        }

        [Test]
        public void Preferences_FollowTheSourceClientAndFailClosed()
        {
            var storage = new MemorySaveStorage();
            Assert.IsTrue(new TelemetryPreferences(storage).Enabled,
                "an unanswered preference collects, exactly as the Godot client does");

            var preferences = new TelemetryPreferences(storage);
            preferences.Set(false);
            Assert.IsFalse(preferences.Enabled);
            Assert.IsFalse(new TelemetryPreferences(storage).Enabled,
                "the refusal survives a restart");

            preferences.Set(true);
            Assert.IsTrue(new TelemetryPreferences(storage).Enabled);

            // A half-written or damaged file must never revive collection for
            // a player who had turned it off.
            storage.WriteAllText(TelemetryPolicy.PreferencePath, "{\"privacy\": {\"diagnos");
            Assert.IsFalse(new TelemetryPreferences(storage).Enabled);
            storage.WriteAllText(TelemetryPolicy.PreferencePath, "{}");
            Assert.IsFalse(new TelemetryPreferences(storage).Enabled);
            storage.WriteAllText(TelemetryPolicy.PreferencePath,
                "{\"privacy\": {\"diagnostics_enabled\": \"true\"}}");
            Assert.IsFalse(new TelemetryPreferences(storage).Enabled,
                "only a real boolean is an answer");
        }

        [Test]
        public void BuildStamp_NamesTheReleaseAndFallsBackToTheEditor()
        {
            var stamped = TelemetryBuildInfo.Parse(
                "{\"version\": \"1.2.1\", \"build_id\": \"20250101T000000Z-abcdef123456\","
                + " \"revision\": \"abcdef1234567890\", \"sdk_version\": \"4.10.3\"}",
                "0.0.0");
            Assert.AreEqual("blitz-legacy@1.2.1", stamped.Release);
            Assert.AreEqual("20250101T000000Z-abcdef123456", stamped.Dist);
            Assert.AreEqual("abcdef1234567890", stamped.Revision);

            var missing = TelemetryBuildInfo.Parse("not json", "1.2.1");
            Assert.AreEqual("blitz-legacy@1.2.1", missing.Release);
            Assert.AreEqual(TelemetryBuildInfo.EditorBuildId, missing.BuildId);
            Assert.AreEqual(TelemetryBuildInfo.WorkingTreeRevision, missing.Revision);
            Assert.AreEqual(TelemetryPolicy.SdkVersion, missing.SdkVersion);

            // Sentry's dist field is short, so an over-long build id is cut
            // rather than rejected by the ingest endpoint.
            var long_id = new TelemetryBuildInfo("1.2.1", new string('b', 200), "rev", "4.10.3");
            Assert.AreEqual(64, long_id.Dist.Length);

            // The stamp the build writes has to be readable by the player.
            var round_trip = TelemetryBuildInfo.Parse(stamped.ToJson(), "0.0.0");
            Assert.AreEqual(stamped.Release, round_trip.Release);
            Assert.AreEqual(stamped.Dist, round_trip.Dist);
            Assert.AreEqual(stamped.Revision, round_trip.Revision);
        }
    }
}
