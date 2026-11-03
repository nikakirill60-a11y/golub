#if BLITZ_SENTRY
using Sentry.Unity;

namespace BlitzLegacy.Runtime.Telemetry
{
    /// <summary>
    /// The options hook the SDK itself calls, both while building the player
    /// and at <c>SubsystemRegistration</c> in the running game. Going through
    /// the SDK's own asset is what enables the Android native layer: the
    /// editor integration reads it while generating the Gradle project, and
    /// without it Unity logs "Android native support disabled because Sentry
    /// has not been configured" and ships an APK that can only report managed
    /// C# errors - not the native and driver crashes that the ported noise
    /// filters exist for.
    ///
    /// All decisions stay in <see cref="BlitzTelemetry"/> so there is one
    /// policy, whether the SDK starts itself or the diagnostics toggle does.
    /// </summary>
    public sealed class BlitzSentryOptions : SentryOptionsConfiguration
    {
        public override void Configure(SentryUnityOptions options)
        {
            BlitzTelemetry.Configure(options);
        }
    }
}
#endif
