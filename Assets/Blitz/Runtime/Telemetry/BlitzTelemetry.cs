using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Telemetry;
using BlitzLegacy.Runtime.Save;
using UnityEngine;
#if BLITZ_SENTRY
using Sentry;
using Sentry.Unity;
#endif

namespace BlitzLegacy.Runtime.Telemetry
{
    /// <summary>
    /// Unity/SDK glue for <see cref="TelemetryPolicy"/>, ported from the Godot
    /// project's <c>scripts/telemetry/telemetry_config.gd</c> and
    /// <c>runtime.gd</c>. Everything SDK-shaped sits behind
    /// <c>BLITZ_SENTRY</c>, which the asmdef defines only while the pinned
    /// <c>io.sentry.unity</c> package is installed by
    /// <c>Tools/bootstrap_sentry_sdk.py</c>; the project still compiles and
    /// runs without it.
    /// </summary>
    public static class BlitzTelemetry
    {
        public const string Game = "blitz-legacy";

        private static TelemetryPreferences _preferences;
        private static TelemetryBuildInfo _build;
        private static bool _applicationPaused;
        private static bool _qa;
        private static string _stage = "startup";
        private static string _screen = string.Empty;

        /// <summary>True once the SDK is installed and reporting.</summary>
        public static bool Reporting { get; private set; }

        public static bool SdkInstalled
        {
            get
            {
#if BLITZ_SENTRY
                return true;
#else
                return false;
#endif
            }
        }

        public static string Stage
        {
            get { return _stage; }
        }

        public static TelemetryBuildInfo BuildInfo
        {
            get
            {
                if (_build == null)
                {
                    _build = LoadBuildInfo();
                }

                return _build;
            }
        }

        /// <summary>
        /// Collection follows the same preference as the Godot client: on
        /// until a player turns it off, kept in its own file beside the saves
        /// so wiping an account cannot flip the answer back.
        /// </summary>
        public static bool CollectionEnabled
        {
            get { return _preferences != null && _preferences.Enabled; }
        }

        /// <summary>
        /// Called once from <c>BlitzBootstrap.Awake</c> with the same storage
        /// the saves use, before any screen exists. The SDK normally started
        /// itself long before this, at subsystem registration, so this mostly
        /// adopts that session and describes the game on it.
        /// </summary>
        public static void Initialize(ISaveStorage storage)
        {
            if (storage != null)
            {
                _preferences = new TelemetryPreferences(storage);
            }

            EnsurePreferences();
            _qa = IsQa();
            Start();
        }

        /// <summary>
        /// The SDK asks for its options at subsystem registration, before any
        /// save service exists, so the preference is read straight from the
        /// player's own save root in that case.
        /// </summary>
        private static void EnsurePreferences()
        {
            if (_preferences == null)
            {
                _preferences = new TelemetryPreferences(SecureSaves.Preferences);
            }
        }

        public static void SetCollectionEnabled(bool value)
        {
            if (_preferences == null)
            {
                return;
            }

            _preferences.Set(value);
            if (value)
            {
                Start();
            }
            else
            {
                Close();
            }
        }

        public static void SetApplicationPaused(bool value)
        {
            _applicationPaused = value;
        }

        public static string RuntimeOs()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.Android:
                    return "Android";
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                    return "Linux";
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return "Windows";
                default:
                    return Application.platform.ToString();
            }
        }

        /// <summary>
        /// QA builds may report from the editor and from headless stages, which
        /// is how the ported known-issue checks are exercised at all. A command
        /// line flag alone cannot unlock it in a shipped player.
        /// </summary>
        public static bool IsQa()
        {
            if (Environment.GetEnvironmentVariable("BLITZ_SENTRY_QA") == "1")
            {
                return true;
            }

            return BlitzBuildFlags.DebugTools && HasArgument("--sentry-qa");
        }

        /// <summary>
        /// The same question for both entry points: the SDK asking for its
        /// options at startup, and the diagnostics toggle later on.
        /// </summary>
        public static bool ShouldReport()
        {
            EnsurePreferences();
            return TelemetryPolicy.ShouldInitialize(
                Application.isEditor,
                Application.isBatchMode,
                _qa,
                HasArgument("--telemetry-off"),
                CollectionEnabled);
        }

        public static string EnvironmentName()
        {
            // The production-debug APK is not a development player, but it is
            // still a test build, so it reports as "beta" rather than claiming
            // to be production traffic.
            return TelemetryPolicy.EnvironmentName(
                _qa, Application.isEditor, BlitzBuildFlags.DebugTools);
        }

        /// <summary>Public deployment settings are generated from the local .env.</summary>
        public static string DeploymentDsn()
        {
            var value = Environment.GetEnvironmentVariable(TelemetryPolicy.DsnEnvironmentVariable);
            if (value == null)
            {
                var resource = Resources.Load<TextAsset>(TelemetryPolicy.DsnResource);
                value = resource != null ? resource.text : string.Empty;
            }
            value = (value ?? string.Empty).Trim();
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)
                || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host)
                || string.IsNullOrEmpty(uri.UserInfo) || uri.UserInfo.Contains(":")
                || string.IsNullOrEmpty(uri.AbsolutePath.Trim('/'))
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                return string.Empty;
            return value;
        }

        private static void Start()
        {
            if (Reporting)
            {
                return;
            }

            if (!ShouldReport() || string.IsNullOrEmpty(DeploymentDsn()))
            {
                return;
            }

#if BLITZ_SENTRY
            var build = BuildInfo;
            if (!SentrySdk.IsEnabled)
            {
                // The SDK self-initializes from its options asset, so this is
                // only reached when the player turned collection back on in
                // the same session and the SDK was closed at the time.
                var options = new SentryUnityOptions();
                Configure(options);
                SentrySdk.Init(options);
            }

            if (!SentrySdk.IsEnabled)
            {
                return;
            }

            Reporting = true;
            SentrySdk.ConfigureScope(scope =>
            {
                // The SDK's own installation id is the only identity; the
                // fields that could carry a player's identity stay empty.
                scope.User = new SentryUser
                {
                    Username = string.Empty,
                    Email = string.Empty,
                    IpAddress = string.Empty,
                };
                scope.SetTag("game", Game);
                scope.SetTag("runtime_os", RuntimeOs());
                scope.SetTag("diagnostics_test", _qa ? "true" : "false");
                scope.Contexts["game_build"] = build.ToContext();
            });
            Debug.Log("[Blitz] diagnostics initialized: sdk=" + build.SdkVersion
                + " release=" + build.Release + " dist=" + build.Dist
                + " environment=" + EnvironmentName());
#endif
        }

        public static void Close()
        {
            if (!Reporting)
            {
                return;
            }

            Reporting = false;
#if BLITZ_SENTRY
            SentrySdk.Close();
#endif
        }

        /// <summary>
        /// Godot's <c>mark_stage</c>: one breadcrumb per lifecycle step, never
        /// per frame, per tank or per particle.
        /// </summary>
        public static void MarkStage(string stage, string screen = null, IDictionary<string, string> details = null)
        {
            _stage = stage ?? string.Empty;
            if (!string.IsNullOrEmpty(screen))
            {
                _screen = screen;
            }

            if (!Reporting)
            {
                return;
            }

#if BLITZ_SENTRY
            var data = new Dictionary<string, string>();
            if (details != null)
            {
                foreach (var pair in details)
                {
                    data[pair.Key] = pair.Value;
                }
            }

            data["stage"] = _stage;
            data["screen"] = _screen;
            SentrySdk.ConfigureScope(scope =>
            {
                scope.SetTag("game_stage", _stage);
                scope.SetTag("game_screen", _screen);
            });
            SentrySdk.AddBreadcrumb(_stage, "game.lifecycle", "navigation", data);
#endif
        }

        /// <summary>
        /// Anti-cheat issue: one Sentry issue per player and edited value.
        /// PRIVACY: unlike every other event, this one carries the in-game
        /// nickname, because a caught cheater is exactly who it is about; it is
        /// set on this event's own scope only and never on the global scope.
        /// The player's diagnostics opt-out is still honoured.
        /// </summary>
        public static void ReportCheat(string nickname, string what, IDictionary<string, string> details)
        {
            if (!Reporting)
            {
                Debug.LogWarning("[Blitz] anti-cheat: diagnostics are off, issue not sent");
                return;
            }

#if BLITZ_SENTRY
            SentrySdk.CaptureMessage("Anti-cheat: '" + nickname + "' edited memory value " + what, scope =>
            {
                scope.User = new SentryUser { Username = nickname ?? string.Empty };
                scope.SetTag("anticheat", "memory_edit");
                scope.SetTag("anticheat_value", what ?? string.Empty);
                scope.SetTag("anticheat_player", nickname ?? string.Empty);
                if (details != null)
                {
                    foreach (var pair in details) { scope.SetExtra(pair.Key, pair.Value); }
                    scope.Contexts["anticheat"] = new Dictionary<string, string>(details);
                }
                scope.SetFingerprint(new[] { "anticheat", nickname ?? string.Empty, what ?? string.Empty });
            }, SentryLevel.Error);
#endif
        }

        /// <summary>Block until queued events are sent, for a deliberate crash.</summary>
        public static void Flush(float seconds)
        {
            if (!Reporting)
            {
                return;
            }

#if BLITZ_SENTRY
            SentrySdk.Flush(TimeSpan.FromSeconds(seconds));
#endif
        }

        /// <summary>Periodic scope refresh; see <see cref="TelemetryRuntime"/>.</summary>
        public static void UpdateContext(IDictionary<string, string> runtime)
        {
            if (!Reporting || runtime == null)
            {
                return;
            }

#if BLITZ_SENTRY
            SentrySdk.ConfigureScope(scope => scope.Contexts["game_runtime"] = runtime);
#endif
        }

        private static TelemetryBuildInfo LoadBuildInfo()
        {
            var stamp = Resources.Load<TextAsset>(TelemetryPolicy.BuildResource);
            return stamp == null || string.IsNullOrEmpty(stamp.text)
                ? TelemetryBuildInfo.Editor(Application.version)
                : TelemetryBuildInfo.Parse(stamp.text, Application.version);
        }

        private static bool HasArgument(string argument)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index < arguments.Length; index++)
            {
                if (string.Equals(arguments[index], argument, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

#if BLITZ_SENTRY
        /// <summary>
        /// Called by <see cref="BlitzSentryOptions"/>: once per player build
        /// while the Gradle project is generated, and once at subsystem
        /// registration in the running game.
        /// </summary>
        public static void Configure(SentryUnityOptions options)
        {
            var build = BuildInfo;
            _qa = IsQa();
            // In the editor this runs as part of building the player, where
            // the build machine's own preference file means nothing: refusing
            // here would strip the native layer out of the APK. The player
            // process is the one that has to honour the choice.
            if (!Application.isEditor && !ShouldReport())
            {
                options.Enabled = false;
                return;
            }

            var dsn = DeploymentDsn();
            if (string.IsNullOrEmpty(dsn))
            {
                options.Enabled = false;
                return;
            }
            options.Dsn = dsn;
            options.Release = build.Release;
            options.Distribution = build.Dist;
            options.Environment = EnvironmentName();
            options.Debug = _qa;
            options.SampleRate = 1.0f;
            options.MaxBreadcrumbs = 100;
            options.ShutdownTimeout = TimeSpan.FromSeconds(2);
            options.SendDefaultPii = false;
            options.AutoSessionTracking = true;
            options.AttachStacktrace = true;
            // The existing log output may contain account and save dumps, and a
            // frame can show a player's own name, so neither the screenshot nor
            // the view hierarchy is ever attached.
            options.AttachScreenshot = false;
            options.AttachViewHierarchy = false;
            options.CaptureInEditor = _qa;
            // The APK is IL2CPP, so without this a managed stack trace arrives
            // as native addresses even with the symbols uploaded.
            options.Il2CppLineNumberSupportEnabled = true;
            // Godot reported ANRs but never the thread dump, which can contain
            // arbitrary strings from every running thread.
            options.AndroidNativeSupportEnabled = true;
            options.AndroidNativeAnrEnabled = true;
            options.AndroidAttachAnrThreadDump = false;
            // Runtime, not build time: the DSN then stays out of the Android
            // manifest and the native layer comes up inside the SDK's own
            // startup init, which is where the opt-out is already honoured.
            options.AndroidNativeInitializationType = NativeInitializationType.Runtime;
            options.LinuxNativeSupportEnabled = true;
            // An error storm must not consume the mobile frame budget or fill
            // the queue; Godot capped the same case per frame and per window.
            options.EnableLogDebouncing = true;
            options.DebounceTimeError = TimeSpan.FromSeconds(1);
            // Unity's structured logs are still experimental and would carry
            // ordinary Debug.Log text, which is exactly what may hold save and
            // account dumps, so only errors and breadcrumbs are sent.
            options.EnableLogs = false;
            options.SetBeforeSend(FilterEvent);
            options.SetBeforeBreadcrumb(FilterBreadcrumb);
        }

        private static SentryEvent FilterEvent(SentryEvent sentryEvent, SentryHint hint)
        {
            var texts = new List<string>();
            if (sentryEvent.Message != null)
            {
                texts.Add(sentryEvent.Message.Formatted ?? sentryEvent.Message.Message);
            }

            if (sentryEvent.SentryExceptions != null)
            {
                foreach (var exception in sentryEvent.SentryExceptions)
                {
                    texts.Add(exception.Value);
                }
            }

            var runtimeOs = RuntimeOs();
            foreach (var text in texts)
            {
                if (TelemetryPolicy.IsPausedAndroidSurfaceLoss(text, runtimeOs, _applicationPaused))
                {
                    return null;
                }
            }

            if (TelemetryPolicy.IsEngineNoise(texts))
            {
                return null;
            }

            string mechanism = null;
            var types = new List<string>();
            if (sentryEvent.SentryExceptions != null)
            {
                foreach (var exception in sentryEvent.SentryExceptions)
                {
                    types.Add(exception.Type);
                    if (mechanism == null && exception.Mechanism != null) { mechanism = exception.Mechanism.Type; }
                }
            }
            if (TelemetryPolicy.IsHandledNetworkNoise(mechanism, types, texts))
            {
                return null;
            }

            if (sentryEvent.Message != null)
            {
                sentryEvent.Message = new SentryMessage
                {
                    Message = TelemetryPolicy.Redact(sentryEvent.Message.Message),
                    Formatted = TelemetryPolicy.Redact(sentryEvent.Message.Formatted),
                };
            }

            if (sentryEvent.SentryExceptions != null)
            {
                foreach (var exception in sentryEvent.SentryExceptions)
                {
                    exception.Value = TelemetryPolicy.Redact(exception.Value);
                }
            }

            return sentryEvent;
        }

        private static Breadcrumb FilterBreadcrumb(Breadcrumb breadcrumb, SentryHint hint)
        {
            var message = breadcrumb.Message;
            if (TelemetryPolicy.IsPausedAndroidSurfaceLoss(message, RuntimeOs(), _applicationPaused))
            {
                return null;
            }

            TelemetryPolicy.NotePipelineState(message);
            if (TelemetryPolicy.IsDriverLogDump(message) || TelemetryPolicy.IsPipelineCascade(message))
            {
                return null;
            }

            var redacted = TelemetryPolicy.Redact(message);
            if (string.Equals(redacted, message, StringComparison.Ordinal))
            {
                return breadcrumb;
            }

            // Breadcrumbs are immutable, so a redacted one has to be rebuilt.
            return new Breadcrumb(redacted, breadcrumb.Type, breadcrumb.Data, breadcrumb.Category, breadcrumb.Level);
        }
#endif
    }
}
