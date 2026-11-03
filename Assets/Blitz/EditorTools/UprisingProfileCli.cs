using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Lighting;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// CPU hotspot probe for a live 7x7 Uprising battle with every AI enabled:
    /// records every available profiler marker for a fixed number of frames
    /// and writes the per-frame averages, sorted by cost, to
    /// <c>Logs/uprising-profile/markers.txt</c>. Editor timings are relative
    /// (they rank hotspots); they are not player FPS. No save/account access.
    /// Run: <c>Tools/unity.sh uprising-profile</c>.
    /// </summary>
    [InitializeOnLoad]
    public static class UprisingProfileCli
    {
        private const string ActiveKey = "Blitz.UprisingProfile.Active";
        private const string ResultKey = "Blitz.UprisingProfile.Result";
        private static DateTime _deadline;
        private static bool _hooked;

        internal static string Output
        {
            get { return Environment.GetEnvironmentVariable("BLITZ_PROFILE_OUTPUT") ?? "Logs/uprising-profile"; }
        }

        static UprisingProfileCli()
        {
            if (SessionState.GetBool(ActiveKey, false))
            {
                Hook();
            }
        }

        public static void Run()
        {
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetInt(ResultKey, -1);
            Hook();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Hook()
        {
            if (_hooked)
            {
                return;
            }

            _hooked = true;
            _deadline = DateTime.UtcNow.AddMinutes(8);
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode)
                {
                    EditorApplication.delayCall += Prepare;
                }
                else if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetInt(ResultKey, -1) >= 0)
                {
                    SessionState.SetBool(ActiveKey, false);
                    EditorApplication.Exit(SessionState.GetInt(ResultKey, 1));
                }
            };
            EditorApplication.update += () =>
            {
                if (DateTime.UtcNow > _deadline)
                {
                    Complete(1);
                }
            };
        }

        private static void Prepare()
        {
            try
            {
                Directory.CreateDirectory(Output);
                Application.runInBackground = true;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                Time.captureDeltaTime = 1f / 60;
                // The phone's own graphics row: CPU cost is what this probe ranks.
                GraphicsQuality.Bind(new SettingsData
                {
                    GraphicsMsaa = SettingsData.MsaaOff,
                    GraphicsRenderScale = 0.5f,
                    GraphicsFog = false,
                    GraphicsShadows = SettingsData.ShadowsOff,
                    GraphicsTextureQuality = SettingsData.TextureQualityHalf,
                    GraphicsHdr = false,
                    GraphicsVsync = false,
                });
                UnityEngine.Random.InitState(1120);
                var session = BattleSession.Create(null, false, autoStart: true, mode: BattleMode.Uprising);
                session.gameObject.AddComponent<UprisingProfileProbe>().Begin(session);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Complete(1);
            }
        }

        internal static void Complete(int result)
        {
            Time.captureDeltaTime = 0;
            SessionState.SetInt(ResultKey, result);
            EditorApplication.isPlaying = false;
        }
    }

    [DefaultExecutionOrder(1000)]
    public sealed class UprisingProfileProbe : MonoBehaviour
    {
        // Bots meet roughly a minute in, so the warmup skips the drive to
        // the front and the sample covers the fighting itself.
        private static readonly int WarmupFrames = EnvFrames("BLITZ_PROFILE_WARMUP", 4800);
        private static readonly int SampleFrames = EnvFrames("BLITZ_PROFILE_FRAMES", 1800);

        private static int EnvFrames(string name, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
        }

        private readonly List<ProfilerRecorder> _recorders = new List<ProfilerRecorder>();
        private readonly List<string> _names = new List<string>();
        private readonly List<string> _categories = new List<string>();
        private readonly List<ProfilerMarkerDataUnit> _units = new List<ProfilerMarkerDataUnit>();
        private double[] _totals = new double[0];
        private long[] _peaks = new long[0];
        private int[] _hits = new int[0];
        private BattleSession _session;
        private int _frame;
        private long _last;
        private double _wall;
        private double _wallPeak;

        public void Begin(BattleSession session)
        {
            _session = session;
            _last = Stopwatch.GetTimestamp();
        }

        private void StartRecorders()
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (var handle in handles)
            {
                var description = ProfilerRecorderHandle.GetDescription(handle);
                if (description.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds
                    && description.UnitType != ProfilerMarkerDataUnit.Bytes
                    && description.UnitType != ProfilerMarkerDataUnit.Count)
                {
                    continue;
                }

                var recorder = new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.Default);
                recorder.Start();
                _recorders.Add(recorder);
                _names.Add(description.Name);
                _categories.Add(description.Category.Name);
                _units.Add(description.UnitType);
            }

            _totals = new double[_recorders.Count];
            _peaks = new long[_recorders.Count];
            _hits = new int[_recorders.Count];
        }

        private void LateUpdate()
        {
            if (_session == null)
            {
                return;
            }

            try
            {
                var now = Stopwatch.GetTimestamp();
                var wall = (now - _last) * 1000d / Stopwatch.Frequency;
                _last = now;
                _frame++;
                if (_frame == WarmupFrames - 60)
                {
                    // Script callback markers only exist while the profiler
                    // records, so it runs a second before the recorders bind.
                    UnityEngine.Profiling.Profiler.enabled = true;
                }

                if (_frame == WarmupFrames)
                {
                    StartRecorders();
                    return;
                }

                if (_frame <= WarmupFrames)
                {
                    return;
                }

                _wall += wall;
                _wallPeak = Math.Max(_wallPeak, wall);
                for (var i = 0; i < _recorders.Count; i++)
                {
                    var recorder = _recorders[i];
                    if (!recorder.Valid || recorder.Count == 0)
                    {
                        continue;
                    }

                    var value = recorder.LastValue;
                    if (value == 0)
                    {
                        continue;
                    }

                    _totals[i] += value;
                    _peaks[i] = Math.Max(_peaks[i], value);
                    _hits[i]++;
                }

                if (_frame >= WarmupFrames + SampleFrames)
                {
                    Write();
                    UprisingProfileCli.Complete(0);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                UprisingProfileCli.Complete(1);
            }
        }

        /// <summary>How many bodies and colliders the physics scene carries.</summary>
        private static string PhysicsCensus()
        {
            var bodies = FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
            var kinematic = 0;
            foreach (var body in bodies)
            {
                if (body.isKinematic) kinematic++;
            }

            var colliders = FindObjectsByType<Collider>(FindObjectsSortMode.None);
            var mesh = 0;
            foreach (var collider in colliders)
            {
                if (collider is MeshCollider) mesh++;
            }

            var tanks = FindObjectsByType<BlitzLegacy.Runtime.Battle.BattleTank>(FindObjectsSortMode.None);
            var plates = 0;
            var samples = 0;
            foreach (var tank in tanks)
            {
                foreach (var plate in tank.Plates)
                {
                    plates++;
                    samples += plate.AimSamples.Length;
                }
            }

            return "tanks=" + tanks.Length + " plates=" + plates + " samples=" + samples
                + " bodies=" + bodies.Length + " kinematic=" + kinematic
                + " colliders=" + colliders.Length + " meshColliders=" + mesh;
        }

        private void Write()
        {
            var order = new List<int>();
            for (var i = 0; i < _recorders.Count; i++)
            {
                if (_hits[i] > 0)
                {
                    order.Add(i);
                }
            }

            order.Sort((a, b) => _totals[b].CompareTo(_totals[a]));
            var text = new StringBuilder();
            text.AppendLine("frames=" + SampleFrames + " wallAvgMs=" + (_wall / SampleFrames).ToString("F3", CultureInfo.InvariantCulture)
                + " wallPeakMs=" + _wallPeak.ToString("F3", CultureInfo.InvariantCulture)
                + " bots=" + _session.Bots.Count + " " + PhysicsCensus());
            text.AppendLine("unit\tavg/frame\tpeak\thits\tcategory\tmarker");
            foreach (var unit in new[] { ProfilerMarkerDataUnit.TimeNanoseconds, ProfilerMarkerDataUnit.Count, ProfilerMarkerDataUnit.Bytes })
            {
                foreach (var i in order)
                {
                    if (_units[i] != unit)
                    {
                        continue;
                    }

                    var scale = unit == ProfilerMarkerDataUnit.TimeNanoseconds ? 1e-6 : 1d;
                    text.Append(unit == ProfilerMarkerDataUnit.TimeNanoseconds ? "ms" : unit == ProfilerMarkerDataUnit.Bytes ? "B" : "n")
                        .Append('\t').Append((_totals[i] * scale / SampleFrames).ToString("F4", CultureInfo.InvariantCulture))
                        .Append('\t').Append((_peaks[i] * scale).ToString("F3", CultureInfo.InvariantCulture))
                        .Append('\t').Append(_hits[i])
                        .Append('\t').Append(_categories[i])
                        .Append('\t').AppendLine(_names[i]);
                }
            }

            File.WriteAllText(Path.Combine(UprisingProfileCli.Output, "markers.txt"), text.ToString());
            Debug.Log("[Blitz] Uprising profile written: " + UprisingProfileCli.Output);
        }

        private void OnDestroy()
        {
            foreach (var recorder in _recorders)
            {
                recorder.Dispose();
            }

            _recorders.Clear();
        }
    }
}
