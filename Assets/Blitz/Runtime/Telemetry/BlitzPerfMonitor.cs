using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BlitzLegacy.Core.Battle;
using UnityEngine;
using UnityEngine.LowLevel;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using Debug = UnityEngine.Debug;

namespace BlitzLegacy.Runtime.Telemetry
{
    /// <summary>
    /// Opt-in frame budget probe for debug-tools players (production-debug
    /// APK, desktop debug builds), meant for measuring a real device.
    ///
    /// It is inert unless a <c>perf_monitor</c> file exists next to the saves
    /// (<c>adb push</c> into <c>Android/data/&lt;app&gt;/files</c>). When armed
    /// it wraps every subsystem of the Unity player loop in a timestamp, so a
    /// release IL2CPP player - which has no profiler markers - still reports
    /// where the main thread spends each frame, and reads Unity's frame timing
    /// (CPU main/render thread, GPU) when the player has frame timing stats.
    /// A summary goes to the log every five seconds as <c>[BlitzPerf]</c>.
    ///
    /// A <c>perf_autobattle</c> file additionally starts one offline battle
    /// from the hangar in the mode written in it (<c>uprising</c> by default),
    /// so a device run is repeatable without touching the screen. Neither file
    /// is read in a plain release player.
    /// </summary>
    public sealed class BlitzPerfMonitor : MonoBehaviour
    {
        public const string MonitorFile = "perf_monitor";
        public const string AutoBattleFile = "perf_autobattle";
        private const float WindowSeconds = 5f;

        private struct PerfStamp { }

        /// <summary>A single call slower than this is logged as a hitch.</summary>
        private const double SlowMs = 25d;
        private static bool _armed;

        /// <summary>
        /// Times one call when the monitor is armed and logs it if it took
        /// longer than <see cref="SlowMs"/>; free otherwise. Use with a
        /// constant label so the call never allocates.
        /// </summary>
        public static SlowScope Slow(string label)
        {
            return new SlowScope(_armed ? label : null);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private struct Timespec
        {
            public long Seconds;
            public long Nanoseconds;
        }

        private const int ClockThreadCpuTime = 3;

        [DllImport("libc", EntryPoint = "clock_gettime")]
        private static extern int ClockGetTime(int clock, out Timespec time);
#endif

        /// <summary>
        /// CPU time the calling thread has consumed, in milliseconds, or a
        /// negative value where the platform does not expose it. Compared with
        /// wall time it tells a heavy call from a thread that was blocked or
        /// preempted.
        /// </summary>
        internal static double ThreadCpuMs()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return ClockGetTime(ClockThreadCpuTime, out var time) == 0
                ? time.Seconds * 1000d + time.Nanoseconds / 1000000d
                : -1d;
#else
            return -1d;
#endif
        }

        private static string CpuNote(double startCpuMs)
        {
            if (startCpuMs < 0d)
            {
                return string.Empty;
            }

            return " cpu=" + (ThreadCpuMs() - startCpuMs).ToString("F1", CultureInfo.InvariantCulture) + "ms";
        }

        public readonly struct SlowScope : IDisposable
        {
            private readonly string _label;
            private readonly long _start;
            private readonly int _collections;
            private readonly double _cpu;

            internal SlowScope(string label)
            {
                _label = label;
                _start = label != null ? Stopwatch.GetTimestamp() : 0L;
                _collections = label != null ? GC.CollectionCount(0) : 0;
                _cpu = label != null ? ThreadCpuMs() : -1d;
            }

            public void Dispose()
            {
                if (_label == null)
                {
                    return;
                }

                var ms = (Stopwatch.GetTimestamp() - _start) * 1000d / Stopwatch.Frequency;
                if (ms >= SlowMs)
                {
                    Debug.Log("[BlitzPerf] slow " + _label + " "
                        + ms.ToString("F1", CultureInfo.InvariantCulture) + "ms frame=" + Time.frameCount
                        + CpuNote(_cpu) + (GC.CollectionCount(0) != _collections ? " gc" : string.Empty));
                }
            }
        }

        private static readonly List<string> Names = new List<string>();
        private static double[] _totals = new double[0];
        private static double[] _peaks = new double[0];
        private static double[] _frame = new double[0];
        private static long _last;
        private static int _current = -1;
        private static int _fixedSteps;
        private static bool _installed;

        private readonly List<double> _frameTimes = new List<double>(1024);
        private readonly FrameTiming[] _timing = new FrameTiming[1];
        private double _cpuMain, _cpuRender, _gpu;
        private int _timedFrames;
        private float _windowStart;
        private long _frameStart;
        private int _gcStart;
        private int _fixedWindow;
        private long _heapLast;
        private double _frameCpu = -1d;
        private double _allocSum, _allocPeak;
        private int _frameCollections;

        /// <summary>A whole frame slower than this is logged on its own.</summary>
        private const double HitchFrameMs = 60d;

        // Development players only: per-script callback timings, read from
        // the profiler's own markers (they do not exist in release players).
        private readonly List<ProfilerRecorder> _scriptRecorders = new List<ProfilerRecorder>();
        private readonly List<string> _scriptNames = new List<string>();
        private readonly HashSet<string> _scriptSeen = new HashSet<string>();
        private readonly List<ProfilerRecorderHandle> _handles = new List<ProfilerRecorderHandle>();
        private double[] _scriptTotals = new double[0];
        private double[] _scriptPeaks = new double[0];

        /// <summary>Arms the monitor when the debug tools and the opt-in file are present.</summary>
        public static void AttachIfRequested(Transform parent)
        {
            if (!BlitzBuildFlags.DebugTools)
            {
                return;
            }

            string root;
            try
            {
                root = Application.persistentDataPath;
                if (string.IsNullOrEmpty(root) || !File.Exists(Path.Combine(root, MonitorFile)))
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Storage unavailable: diagnostics simply stay off.
                return;
            }

            var holder = new GameObject("PerfMonitor");
            holder.transform.SetParent(parent, false);
            var monitor = holder.AddComponent<BlitzPerfMonitor>();
            monitor.Arm(Path.Combine(root, AutoBattleFile));
        }

        private void Arm(string autoBattlePath)
        {
            Install();
            _armed = true;
            _windowStart = Time.unscaledTime;
            _frameStart = Stopwatch.GetTimestamp();
            _gcStart = GC.CollectionCount(0);
            Debug.Log("[BlitzPerf] armed: " + SystemInfo.deviceModel + " " + SystemInfo.processorType
                + " gfx=" + SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceName
                + " screen=" + Screen.width + "x" + Screen.height
                + " fps=" + Application.targetFrameRate + " vsync=" + QualitySettings.vSyncCount);
            string mode = null;
            try
            {
                if (File.Exists(autoBattlePath))
                {
                    mode = File.ReadAllText(autoBattlePath).Trim();
                }
            }
            catch (Exception)
            {
                mode = null;
            }

            if (mode != null)
            {
                StartCoroutine(AutoBattle(mode));
            }
        }

        private static IEnumerator AutoBattle(string requested)
        {
            var mode = BattleMode.Uprising;
            if (string.Equals(requested, "normal", StringComparison.OrdinalIgnoreCase)) { mode = BattleMode.Normal; }
            else if (string.Equals(requested, "gravity", StringComparison.OrdinalIgnoreCase)) { mode = BattleMode.Gravity; }

            var deadline = Time.unscaledTime + 120f;
            while (Time.unscaledTime < deadline)
            {
                var boot = BlitzBootstrap.Current;
                if (boot != null && boot.Hangar != null && boot.Battle == null && boot.Queue == null)
                {
                    yield return new WaitForSecondsRealtime(4f);
                    boot = BlitzBootstrap.Current;
                    if (boot == null || boot.Hangar == null)
                    {
                        continue;
                    }

                    boot.SelectedMode = mode;
                    boot.OnlineSelected = false;
                    Debug.Log("[BlitzPerf] auto battle: " + BattleModeCatalog.Id(mode));
                    boot.EnterBattle(boot.Hangar);
                    yield break;
                }

                yield return null;
            }

            Debug.LogWarning("[BlitzPerf] auto battle: the hangar never came up");
        }

        /// <summary>Wraps every player-loop subsystem in a timestamp.</summary>
        private static void Install()
        {
            if (_installed)
            {
                return;
            }

            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (loop.subSystemList == null)
            {
                return;
            }

            for (var p = 0; p < loop.subSystemList.Length; p++)
            {
                var phase = loop.subSystemList[p];
                var children = phase.subSystemList;
                if (children == null || children.Length == 0)
                {
                    continue;
                }

                var wrapped = new List<PlayerLoopSystem>(children.Length * 2 + 1);
                for (var c = 0; c < children.Length; c++)
                {
                    wrapped.Add(StampSystem(Register(phase.type.Name + "." + SystemName(children[c]))));
                    wrapped.Add(children[c]);
                }

                wrapped.Add(StampSystem(-1));
                phase.subSystemList = wrapped.ToArray();
                loop.subSystemList[p] = phase;
            }

            _totals = new double[Names.Count];
            _peaks = new double[Names.Count];
            _frame = new double[Names.Count];
            PlayerLoop.SetPlayerLoop(loop);
            _installed = true;
        }

        private static string SystemName(PlayerLoopSystem system)
        {
            return system.type != null ? system.type.Name : "custom";
        }

        private static int Register(string name)
        {
            Names.Add(name);
            return Names.Count - 1;
        }

        private static PlayerLoopSystem StampSystem(int next)
        {
            return new PlayerLoopSystem
            {
                type = typeof(PerfStamp),
                updateDelegate = () => Stamp(next),
            };
        }

        /// <summary>Closes the running subsystem and opens <paramref name="next"/>.</summary>
        private static void Stamp(int next)
        {
            var now = Stopwatch.GetTimestamp();
            if (_current >= 0)
            {
                _frame[_current] += (now - _last) * 1000d / Stopwatch.Frequency;
            }

            if (next >= 0 && Names[next].EndsWith("FixedUpdate.ScriptRunBehaviourFixedUpdate", StringComparison.Ordinal))
            {
                _fixedSteps++;
            }

            _current = next;
            _last = now;
        }

        private void Update()
        {
            // Once per frame, at the start of the script update: close the
            // previous frame's subsystem accumulators.
            var now = Stopwatch.GetTimestamp();
            var frameMs = (now - _frameStart) * 1000d / Stopwatch.Frequency;
            _frameStart = now;
            _frameTimes.Add(frameMs);

            // Managed heap growth since the previous frame approximates what
            // the frame allocated (the collector does not move objects; a
            // frame with a collection reports nothing).
            var heap = GC.GetTotalMemory(false);
            var collections = GC.CollectionCount(0);
            var collected = collections != _frameCollections;
            if (!collected && heap > _heapLast)
            {
                var kb = (heap - _heapLast) / 1024d;
                _allocSum += kb;
                if (kb > _allocPeak)
                {
                    _allocPeak = kb;
                }
            }

            _heapLast = heap;
            _frameCollections = collections;
            if (frameMs >= HitchFrameMs && _frameTimes.Count > 1)
            {
                Debug.Log("[BlitzPerf] hitch " + frameMs.ToString("F1", CultureInfo.InvariantCulture)
                    + "ms frame=" + Time.frameCount + CpuNote(_frameCpu) + (collected ? " gc" : string.Empty));
            }

            _frameCpu = ThreadCpuMs();
            for (var i = 0; i < _frame.Length; i++)
            {
                _totals[i] += _frame[i];
                if (_frame[i] > _peaks[i])
                {
                    _peaks[i] = _frame[i];
                }

                _frame[i] = 0d;
            }

            _fixedWindow += _fixedSteps;
            _fixedSteps = 0;

            SampleScripts();
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _timing) > 0)
            {
                _cpuMain += _timing[0].cpuMainThreadFrameTime;
                _cpuRender += _timing[0].cpuRenderThreadFrameTime;
                _gpu += _timing[0].gpuFrameTime;
                _timedFrames++;
            }

            if (Time.unscaledTime - _windowStart >= WindowSeconds)
            {
                Report();
            }
        }

        private void Report()
        {
            var frames = _frameTimes.Count;
            if (frames == 0)
            {
                return;
            }

            var seconds = Time.unscaledTime - _windowStart;
            _frameTimes.Sort();
            var sum = 0d;
            for (var i = 0; i < frames; i++)
            {
                sum += _frameTimes[i];
            }

            var invariant = CultureInfo.InvariantCulture;
            var text = new StringBuilder(1024);
            var boot = BlitzBootstrap.Current;
            var scene = boot == null ? "-" : boot.Battle != null ? "battle" : boot.Hangar != null ? "hangar" : "other";
            text.Append("[BlitzPerf] scene=").Append(scene)
                .Append(" fps=").Append((frames / Math.Max(seconds, 0.001f)).ToString("F1", invariant))
                .Append(" frame avg=").Append((sum / frames).ToString("F2", invariant))
                .Append(" p50=").Append(_frameTimes[frames / 2].ToString("F2", invariant))
                .Append(" p95=").Append(_frameTimes[Math.Min(frames - 1, frames * 95 / 100)].ToString("F2", invariant))
                .Append(" max=").Append(_frameTimes[frames - 1].ToString("F2", invariant))
                .Append(" fixed/frame=").Append(((double)_fixedWindow / frames).ToString("F2", invariant))
                .Append(" gc0=").Append(GC.CollectionCount(0) - _gcStart)
                .Append(" heapMB=").Append((GC.GetTotalMemory(false) / 1048576d).ToString("F0", invariant))
                .Append(" allocKB/frame avg=").Append((_allocSum / frames).ToString("F1", invariant))
                .Append(" max=").Append(_allocPeak.ToString("F0", invariant));
            if (_timedFrames > 0)
            {
                text.Append(" | cpuMain=").Append((_cpuMain / _timedFrames).ToString("F2", invariant))
                    .Append(" cpuRender=").Append((_cpuRender / _timedFrames).ToString("F2", invariant))
                    .Append(" gpu=").Append((_gpu / _timedFrames).ToString("F2", invariant));
            }

            var order = new List<int>(_totals.Length);
            for (var i = 0; i < _totals.Length; i++)
            {
                if (_totals[i] / frames >= 0.05d)
                {
                    order.Add(i);
                }
            }

            order.Sort((a, b) => _totals[b].CompareTo(_totals[a]));
            for (var k = 0; k < order.Count && k < 14; k++)
            {
                var i = order[k];
                text.Append("\n  ").Append(Names[i]).Append(' ')
                    .Append((_totals[i] / frames).ToString("F2", invariant)).Append("ms peak ")
                    .Append(_peaks[i].ToString("F1", invariant));
            }

            AppendScripts(text, frames);
            Debug.Log(text.ToString());

            _frameTimes.Clear();
            Array.Clear(_totals, 0, _totals.Length);
            Array.Clear(_peaks, 0, _peaks.Length);
            _cpuMain = _cpuRender = _gpu = 0d;
            _timedFrames = 0;
            _fixedWindow = 0;
            _allocSum = _allocPeak = 0d;
            _gcStart = GC.CollectionCount(0);
            _windowStart = Time.unscaledTime;
            BindScriptRecorders();
        }

        private void BindScriptRecorders()
        {
            if (!UnityEngine.Debug.isDebugBuild || !UnityEngine.Profiling.Profiler.supported)
            {
                return;
            }

            // Script callback markers appear while the profiler records, so
            // each window binds the ones that showed up since the last one.
            UnityEngine.Profiling.Profiler.enabled = true;
            _handles.Clear();
            ProfilerRecorderHandle.GetAvailable(_handles);
            foreach (var handle in _handles)
            {
                var description = ProfilerRecorderHandle.GetDescription(handle);
                if (description.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds)
                {
                    continue;
                }

                var category = description.Category.Name;
                if (category != "Scripts" && category != "Physics" && category != "Loading"
                    && category != "GC" && category != "Memory")
                {
                    continue;
                }

                if (!_scriptSeen.Add(description.Name))
                {
                    continue;
                }

                var recorder = new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.Default);
                recorder.Start();
                _scriptRecorders.Add(recorder);
                _scriptNames.Add(description.Name);
            }

            if (_scriptTotals.Length != _scriptRecorders.Count)
            {
                Array.Resize(ref _scriptTotals, _scriptRecorders.Count);
                Array.Resize(ref _scriptPeaks, _scriptRecorders.Count);
            }
        }

        private void SampleScripts()
        {
            for (var i = 0; i < _scriptRecorders.Count; i++)
            {
                var recorder = _scriptRecorders[i];
                if (recorder.Valid && recorder.Count > 0)
                {
                    var ms = recorder.LastValue / 1e6d;
                    _scriptTotals[i] += ms;
                    if (ms > _scriptPeaks[i]) { _scriptPeaks[i] = ms; }
                }
            }
        }

        private void AppendScripts(StringBuilder text, int frames)
        {
            if (_scriptRecorders.Count == 0)
            {
                return;
            }

            var invariant = CultureInfo.InvariantCulture;
            var order = new List<int>(_scriptTotals.Length);
            for (var i = 0; i < _scriptTotals.Length; i++)
            {
                if (_scriptTotals[i] / frames >= 0.03d)
                {
                    order.Add(i);
                }
            }

            order.Sort((a, b) => _scriptTotals[b].CompareTo(_scriptTotals[a]));
            for (var k = 0; k < order.Count && k < 30; k++)
            {
                var i = order[k];
                text.Append("\n  # ").Append(_scriptNames[i]).Append(' ')
                    .Append((_scriptTotals[i] / frames).ToString("F2", invariant)).Append("ms");
            }

            // Hitches: every marker that took a long single frame.
            order.Clear();
            for (var i = 0; i < _scriptPeaks.Length; i++)
            {
                if (_scriptPeaks[i] >= HitchMs)
                {
                    order.Add(i);
                }
            }

            order.Sort((a, b) => _scriptPeaks[b].CompareTo(_scriptPeaks[a]));
            for (var k = 0; k < order.Count && k < 20; k++)
            {
                var i = order[k];
                text.Append("\n  ! ").Append(_scriptNames[i]).Append(" peak ")
                    .Append(_scriptPeaks[i].ToString("F1", invariant)).Append("ms");
            }

            Array.Clear(_scriptTotals, 0, _scriptTotals.Length);
            Array.Clear(_scriptPeaks, 0, _scriptPeaks.Length);
        }

        /// <summary>A single-frame marker time worth reporting as a hitch.</summary>
        private const double HitchMs = 20d;

        private void OnDestroy()
        {
            foreach (var recorder in _scriptRecorders)
            {
                recorder.Dispose();
            }

            _scriptRecorders.Clear();
        }
    }
}
