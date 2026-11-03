using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Lighting;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using Unity.Profiling;
using Debug = UnityEngine.Debug;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Repeatable real-device probes, never player FPS estimates. No save/account access.
    /// Run: original SC2 simulation/mesh CPU/GC plus lossless mesh/image checkpoints.
    /// RunBattle: full 7x7 Uprising, all AI enabled, maximum graphics, 1440p offscreen rendering.
    /// Outputs belong in ignored Logs/, not Assets. GPU timing stays unavailable if the driver omits it.</summary>
    [InitializeOnLoad]
    public static class UprisingPerformanceCli
    {
        private const string ActiveKey = "Blitz.UprisingPerformance.Active";
        private const string ResultKey = "Blitz.UprisingPerformance.Result";
        private static DateTime _deadline;
        private static bool _hooked;
        internal static string Output => Environment.GetEnvironmentVariable("BLITZ_PERF_OUTPUT") ?? "Logs/uprising-perf/latest";
        [Serializable] public sealed class Sample
        {
            public string fixture; public int repetition, frame, particles, vertices;
            public double simulationMs, meshMs; public long allocatedBytes;
        }
        [Serializable] public sealed class Checkpoint
        {
            public string fixture, meshSha256; public int repetition, frame, particles, vertices;
        }
        [Serializable] public sealed class FxReport
        {
            public string protocol = "dava-fx-cpu-v2", unity, device;
            public bool threadAllocationCounterSupported;
            public int width = 2560, height = 1440, repetitions = 3, warmupFrames = 30, sampleFrames = 180;
            public float simulationDelta = 1f / 60;
            public List<Sample> samples = new List<Sample>();
            public List<Checkpoint> checkpoints = new List<Checkpoint>();
        }
        static UprisingPerformanceCli() { if (SessionState.GetBool(ActiveKey, false)) Hook(); }
        public static void Run()
        {
            var errors = 0;
            Application.logMessageReceived += (_, __, type) => { if (type == LogType.Error || type == LogType.Exception) errors++; };
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Directory.CreateDirectory(Output);
                var camera = new GameObject("perf-camera").AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.12f, .16f, .2f);
                camera.transform.position = new Vector3(0, 5, -14); camera.transform.LookAt(new Vector3(0, 1, 0)); camera.nearClipPlane = .01f;
                var allocationBefore = GC.GetAllocatedBytesForCurrentThread(); var allocationProbe = new byte[1024]; GC.KeepAlive(allocationProbe);
                var report = new FxReport { unity = Application.unityVersion, device = SystemInfo.graphicsDeviceName,
                    threadAllocationCounterSupported = GC.GetAllocatedBytesForCurrentThread() > allocationBefore };
                // Exercise 12 official effects, including geometry, stripes, frame/noise animation.
                foreach (var pair in UprisingVfx.Sources)
                    Measure(report, camera, pair.Key, new[] { pair.Value }, 1);
                // 14 tanks x (damage + reload + two engine tracks); identical source counts/RNG for A/B.
                Measure(report, camera, "7x7-passives", new[] { UprisingVfx.Sources["damageBoost"], UprisingVfx.Sources["reloadBoost"], UprisingVfx.Sources["engineBoost"], UprisingVfx.Sources["engineBoost"] }, 14);
                File.WriteAllText(Path.Combine(Output, "fx.json"), JsonUtility.ToJson(report));
                if (errors != 0) throw new InvalidOperationException("Performance probe errors: " + errors);
                Debug.Log("[Blitz] FX CPU probe complete: " + report.samples.Count + " samples; " + Output);
                EditorApplication.Exit(0);
            }
            catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
        }
        private static void Measure(FxReport report, Camera camera, string fixture, string[] sources, int copies)
        {
            for (var repetition = 0; repetition < report.repetitions; repetition++)
            {
                var root = new GameObject("perf-" + fixture);
                var effects = new List<DavaFx>();
                for (var copy = 0; copy < copies; copy++) foreach (var source in sources)
                {
                    var mount = new GameObject("mount"); mount.transform.SetParent(root.transform, false);
                    mount.transform.localPosition = new Vector3((copy % 7 - 3) * .6f, 0, (copy / 7) * .6f);
                    var fx = DavaFx.Create(mount.transform, source, 20, true); fx.SetVariable("Velosity", 1);
                    if (source == UprisingVfx.Sources["lightning"]) fx.SetStripeEndpoints(new Vector3(-5, 1, 0), new Vector3(5, 1, 0));
                    effects.Add(fx);
                }
                var filters = new List<MeshFilter>(64);
                for (var frame = 0; frame < report.warmupFrames; frame++) Step(effects, camera, report.simulationDelta);
                GC.Collect();
                for (var frame = 0; frame < report.sampleFrames; frame++)
                {
                    // Probe bookkeeping lies outside both CPU timers and the managed-allocation interval.
                    var beforeBytes = GC.GetAllocatedBytesForCurrentThread(); var begin = Stopwatch.GetTimestamp();
                    foreach (var fx in effects) fx.Simulate(report.simulationDelta);
                    var simulated = Stopwatch.GetTimestamp();
                    foreach (var fx in effects) fx.Render(camera);
                    var rendered = Stopwatch.GetTimestamp(); var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                    var live = 0; foreach (var fx in effects) live += fx.LiveParticles;
                    root.GetComponentsInChildren<MeshFilter>(true, filters);
                    var vertices = 0; foreach (var filter in filters) vertices += filter.sharedMesh.vertexCount;
                    report.samples.Add(new Sample { fixture = fixture, repetition = repetition, frame = frame, particles = live, vertices = vertices,
                        simulationMs = (simulated - begin) * 1000d / Stopwatch.Frequency, meshMs = (rendered - simulated) * 1000d / Stopwatch.Frequency, allocatedBytes = report.threadAllocationCounterSupported ? allocated : -1 });
                    if (frame == 89 || frame == 179)
                    {
                        report.checkpoints.Add(new Checkpoint { fixture = fixture, repetition = repetition, frame = frame, particles = live, vertices = vertices, meshSha256 = MeshDigest(filters) });
                        if (repetition == 0 && frame == 89) Capture(camera, Path.Combine(Output, fixture + ".png"));
                    }
                }
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
        private static void Step(List<DavaFx> effects, Camera camera, float dt) { foreach (var fx in effects) { fx.Simulate(dt); fx.Render(camera); } }
        private static string MeshDigest(List<MeshFilter> filters)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                foreach (var f in filters)
                {
                    var mesh = f.sharedMesh;
                    foreach (var v in mesh.vertices) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
                    foreach (var v in mesh.normals) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
                    foreach (var c in mesh.colors) { writer.Write(c.r); writer.Write(c.g); writer.Write(c.b); writer.Write(c.a); }
                    var uv = new List<Vector3>();
                    for (var channel = 0; channel < 3; channel++) { mesh.GetUVs(channel, uv); foreach (var v in uv) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); } }
                    foreach (var index in mesh.triangles) writer.Write(index);
                    var b = mesh.bounds; writer.Write(b.center.x); writer.Write(b.center.y); writer.Write(b.center.z); writer.Write(b.size.x); writer.Write(b.size.y); writer.Write(b.size.z);
                }
                return BitConverter.ToString(SHA256.Create().ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        internal static void Capture(Camera camera, string path)
        {
            var previousTarget = camera.targetTexture; var previousActive = RenderTexture.active;
            var target = previousTarget != null ? previousTarget : new RenderTexture(2560, 1440, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            try { camera.targetTexture = target; camera.Render(); RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply(); File.WriteAllBytes(path, image.EncodeToPNG()); }
            finally { camera.targetTexture = previousTarget; RenderTexture.active = previousActive; UnityEngine.Object.DestroyImmediate(image); if (previousTarget == null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); } }
        }
        public static void RunBattle()
        {
            SessionState.SetBool(ActiveKey, true); SessionState.SetInt(ResultKey, -1); Hook();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); EditorApplication.isPlaying = true;
        }
        private static void Hook()
        {
            if (_hooked) return; _hooked = true; _deadline = DateTime.UtcNow.AddMinutes(5);
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.delayCall += PrepareBattle;
                else if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetInt(ResultKey, -1) >= 0)
                { SessionState.SetBool(ActiveKey, false); EditorApplication.Exit(SessionState.GetInt(ResultKey, 1)); }
            };
            EditorApplication.update += () => { if (DateTime.UtcNow > _deadline) CompleteBattle(1); };
        }
        private static void PrepareBattle()
        {
            try
            {
                Directory.CreateDirectory(Output); Application.runInBackground = true;
                QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; Time.captureDeltaTime = 1f / 60;
                GraphicsQuality.Bind(new SettingsData { GraphicsMsaa = SettingsData.Msaa8X, GraphicsRenderScale = 2,
                    GraphicsFog = true, GraphicsDrawDistance = SettingsData.DrawDistanceMax, GraphicsShadows = SettingsData.ShadowsSoft,
                    GraphicsShadowDistance = SettingsData.ShadowDistanceMax, GraphicsTextureQuality = SettingsData.TextureQualityFull,
                    GraphicsAnisotropic = SettingsData.AnisoForced, GraphicsVsync = false, GraphicsHdr = true, GraphicsTankTreads = true });
                UnityEngine.Random.InitState(1120);
                var session = BattleSession.Create(null, false, autoStart: true, mode: BattleMode.Uprising);
                if (session.Bots.Count != 13) throw new InvalidOperationException("Expected 13 enabled AI controllers; got " + session.Bots.Count);
                foreach (var brain in session.Bots) if (!brain.enabled) throw new InvalidOperationException("A bot was disabled");
                foreach (var tank in session.GetComponentsInChildren<UprisingTank>())
                    foreach (var passive in new[] { "damageBoost2", "reloadBoost2", "engineBoost2" }) tank.State.Grant(passive);
                session.gameObject.AddComponent<UprisingPerformanceProbe>().Begin(session);
            }
            catch (Exception ex) { Debug.LogException(ex); CompleteBattle(1); }
        }
        internal static void CompleteBattle(int result)
        { Time.captureDeltaTime = 0; SessionState.SetInt(ResultKey, result); EditorApplication.isPlaying = false; }
    }
    [DefaultExecutionOrder(1000)]
    public sealed class UprisingPerformanceProbe : MonoBehaviour
    {
        [Serializable] public sealed class Frame
        { public int frame, bots, activeBots, particles, effects; public double wallFrameMs, submitMs, mainThreadMs, renderThreadMs, gpuMs; public long gcBytes, batches; }
        [Serializable] public sealed class Report
        { public string protocol = "uprising-editor-offscreen-v1", device, unity, caveat = "Editor/offscreen wall timings are not native player FPS. GPU unavailable is -1. VSync/FPS cap disabled only for measurement; no bots/effects/logic disabled.";
          public int width = 2560, height = 1440, seed = 1120, warmupFrames = 180, sampleFrames = 600, msaa = 8; public float renderScale = 2; public bool hdr = true, softShadows = true; public List<Frame> frames = new List<Frame>(); }
        private BattleSession _session; private Camera _camera; private RenderTexture _target; private Report _report;
        private ProfilerRecorder _main, _render, _gc, _batches; private readonly FrameTiming[] _timing = new FrameTiming[1];
        private int _frame, _errors; private long _last; private readonly List<DavaFx> _effects = new List<DavaFx>(128);
        public void Begin(BattleSession session)
        {
            _session = session; _camera = session.View.Active; _camera.enabled = false;
            _target = new RenderTexture(2560, 1440, 24, RenderTextureFormat.ARGBHalf); _camera.targetTexture = _target;
            foreach (var canvas in session.GetComponentsInChildren<Canvas>(true)) if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = _camera; canvas.planeDistance = 1; }
            _report = new Report { device = SystemInfo.graphicsDeviceName, unity = Application.unityVersion };
            for (var i = 0; i < _report.sampleFrames; i++) _report.frames.Add(new Frame());
            _main = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
            _render = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Render Thread", 1);
            _gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count", 1);
            Application.logMessageReceived += Message; _last = Stopwatch.GetTimestamp();
        }
        private void Message(string message, string stack, LogType type) { if (type == LogType.Error || type == LogType.Exception) _errors++; }
        private void LateUpdate()
        {
            if (_session == null) return;
            try
            {
                var at = Stopwatch.GetTimestamp(); var wall = (at - _last) * 1000d / Stopwatch.Frequency; _last = at;
                FrameTimingManager.CaptureFrameTimings(); _camera.Render(); var submitted = Stopwatch.GetTimestamp();
                var gpu = FrameTimingManager.GetLatestTimings(1, _timing) > 0 && _timing[0].gpuFrameTime > 0 ? _timing[0].gpuFrameTime : -1;
                if (_frame >= _report.warmupFrames)
                {
                    var active = 0; foreach (var bot in _session.Bots) if (bot.enabled) active++;
                    var particles = 0; _session.GetComponentsInChildren<DavaFx>(true, _effects); foreach (var fx in _effects) particles += fx.LiveParticles;
                    var sample = _report.frames[_frame - _report.warmupFrames]; sample.frame = _frame; sample.bots = _session.Bots.Count; sample.activeBots = active;
                    sample.particles = particles; sample.effects = _effects.Count; sample.wallFrameMs = wall; sample.submitMs = (submitted - at) * 1000d / Stopwatch.Frequency;
                    sample.mainThreadMs = _main.Valid ? _main.LastValue / 1e6 : -1; sample.renderThreadMs = _render.Valid ? _render.LastValue / 1e6 : -1;
                    sample.gpuMs = gpu; sample.gcBytes = _gc.Valid ? _gc.LastValue : -1; sample.batches = _batches.Valid ? _batches.LastValue : -1;
                }
                _frame++;
                if (_frame == _report.warmupFrames) UprisingPerformanceCli.Capture(_camera, Path.Combine(UprisingPerformanceCli.Output, "battle.png"));
                if (_frame >= _report.warmupFrames + _report.sampleFrames)
                { File.WriteAllText(Path.Combine(UprisingPerformanceCli.Output, "battle.json"), JsonUtility.ToJson(_report)); Debug.Log("[Blitz] Full 7x7 Uprising probe complete: " + _errors + " errors"); UprisingPerformanceCli.CompleteBattle(_errors == 0 ? 0 : 1); }
            }
            catch (Exception ex) { Debug.LogException(ex); UprisingPerformanceCli.CompleteBattle(1); }
        }
        private void OnDestroy()
        { Application.logMessageReceived -= Message; _main.Dispose(); _render.Dispose(); _gc.Dispose(); _batches.Dispose(); if (_camera != null) _camera.targetTexture = null; if (_target != null) { _target.Release(); Destroy(_target); } }
    }
}
