using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace BlitzLegacy.Runtime.Telemetry
{
    /// <summary>
    /// Port of Godot's <c>scripts/telemetry/runtime.gd</c>: the main-thread
    /// facade that refreshes the runtime context every five seconds and tells
    /// the policy when Android has paused the app. There is deliberately no
    /// per-frame, per-tank or per-particle reporting.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TelemetryRuntime : MonoBehaviour
    {
        public const float ContextIntervalSeconds = 5f;

        private float _nextContext;

        public static TelemetryRuntime Attach(Transform parent)
        {
            var holder = new GameObject("Diagnostics");
            holder.transform.SetParent(parent, false);
            return holder.AddComponent<TelemetryRuntime>();
        }

        /// <summary>
        /// Every observed Vulkan surface loss happens while the app is paused,
        /// so the filter needs to know the state before the report arrives.
        /// </summary>
        private void OnApplicationPause(bool paused)
        {
            BlitzTelemetry.SetApplicationPaused(paused);
            BlitzTelemetry.MarkStage(paused ? "application_paused" : "application_resumed");
        }

        private void OnApplicationQuit()
        {
            // Flush whatever is queued instead of losing the last event of a
            // session to the process going away.
            BlitzTelemetry.Close();
        }

        private void Update()
        {
            if (!BlitzTelemetry.Reporting)
            {
                return;
            }

            if (UnityEngine.Time.unscaledTime < _nextContext)
            {
                return;
            }

            _nextContext = UnityEngine.Time.unscaledTime + ContextIntervalSeconds;
            BlitzTelemetry.UpdateContext(Snapshot());
        }

        private static IDictionary<string, string> Snapshot()
        {
            var delta = UnityEngine.Time.unscaledDeltaTime;
            var fps = delta > 0f ? 1f / delta : 0f;
            return new Dictionary<string, string>
            {
                { "stage", BlitzTelemetry.Stage },
                { "fps", fps.ToString("F1", CultureInfo.InvariantCulture) },
                { "frame_ms", (delta * 1000f).ToString("F1", CultureInfo.InvariantCulture) },
                { "graphics_device", SystemInfo.graphicsDeviceType.ToString() },
                { "quality_level", QualitySettings.GetQualityLevel().ToString(CultureInfo.InvariantCulture) },
                { "target_frame_rate", Application.targetFrameRate.ToString(CultureInfo.InvariantCulture) },
                { "managed_memory_bytes", System.GC.GetTotalMemory(false).ToString(CultureInfo.InvariantCulture) },
            };
        }
    }
}
