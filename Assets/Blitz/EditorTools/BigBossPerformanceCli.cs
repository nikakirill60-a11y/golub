using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace BlitzLegacy.EditorTools
{
    /// <summary>CPU and managed-allocation probe of the original Big Boss scenes: every source
    /// effect alone, then a 7v7 battle's worth of sustained role effects. Numbers describe this
    /// port on this machine; they are not original-client measurements.</summary>
    public static class BigBossPerformanceCli
    {
        private const string Output = "Logs/bigboss-perf";
        private const int Warmup = 30, Frames = 240;
        private const float Delta = 1f / 60;
        [Serializable] public sealed class Fixture
        {
            public string name;
            public int copies, peakParticles, peakVertices;
            public double simulationMs, meshMs, worstFrameMs;
            public long allocatedBytesPerFrame, worstFrameBytes;
        }
        [Serializable] public sealed class Report
        {
            public string unity;
            public bool threadAllocationCounterSupported;
            public List<Fixture> fixtures = new List<Fixture>();
        }
        public static void Run()
        {
            var errors = 0;
            Application.logMessageReceived += (_, __, type) => { if (type == LogType.Error || type == LogType.Exception) errors++; };
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); Directory.CreateDirectory(Output);
                var camera = new GameObject("perf-camera").AddComponent<Camera>();
                camera.transform.position = new Vector3(0, 6, -18); camera.transform.LookAt(new Vector3(0, 1, 0)); camera.nearClipPlane = .01f;
                var probe = GC.GetAllocatedBytesForCurrentThread(); GC.KeepAlive(new byte[1024]);
                var report = new Report { unity = Application.unityVersion, threadAllocationCounterSupported = GC.GetAllocatedBytesForCurrentThread() > probe };
                var manifest = JsonResources.Load("Data/big_boss_fx_1120");
                foreach (var pair in manifest["effects"].Fields)
                {
                    var name = pair.Value["resource"].AsString().Replace("BigBoss/Sources/", "");
                    Measure(report, camera, name, new[] { name }, 1);
                }
                // Sustained role effects of a full 7v7 battle plus one burst of each active family.
                Measure(report, camera, "7x7-sustained", new[]
                {
                    "Boss_mode_BossAura", "Boss_mode_GravitonPassive", "Boss_mode_GravitonPassive", "Boss_mode_GravitonPassive",
                    "Boss_mode_HealerShield", "Boss_mode_HealerShield", "Boss_mode_Laser_Ready", "Boss_mode_Dissolve",
                    "Boss_mode_Dissolve_gun", "Boss_mode_MassHealAura", "Boss_mode_Graviton", "Boss_mode_Repulsion_Explosion",
                    "Boss_mode_Kamikaze_Explosion", "Boss_mode_Minion_LvlUP",
                }, 2);
                File.WriteAllText(Path.Combine(Output, "fx.json"), JsonUtility.ToJson(report, true));
                if (errors != 0) throw new InvalidOperationException("Big Boss performance probe errors: " + errors);
                Debug.Log("[Blitz] Big Boss FX probe complete: " + report.fixtures.Count + " fixtures; " + Output);
                EditorApplication.Exit(0);
            }
            catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
        }
        private static void Measure(Report report, Camera camera, string fixture, string[] sources, int copies)
        {
            var root = new GameObject("perf-" + fixture); var effects = new List<DavaFx>();
            for (var copy = 0; copy < copies; copy++) for (var i = 0; i < sources.Length; i++)
            {
                var mount = new GameObject("mount").transform; mount.SetParent(root.transform, false);
                var index = copy * sources.Length + i; mount.localPosition = new Vector3((index % 7 - 3) * 4f, 0, (index / 7) * 4f);
                var fx = DavaFx.CreateBigBoss(mount, sources[i], 60, true); fx.ViewCamera = camera; effects.Add(fx);
            }
            for (var frame = 0; frame < Warmup; frame++) foreach (var fx in effects) { fx.Simulate(Delta); fx.Render(camera); }
            GC.Collect();
            var result = new Fixture { name = fixture, copies = copies }; long bytes = 0; double simulation = 0, mesh = 0;
            var filters = new List<MeshFilter>(256);
            for (var frame = 0; frame < Frames; frame++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread(); var begin = Stopwatch.GetTimestamp();
                foreach (var fx in effects) fx.Simulate(Delta);
                var simulated = Stopwatch.GetTimestamp();
                foreach (var fx in effects) fx.Render(camera);
                var rendered = Stopwatch.GetTimestamp(); var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                var s = (simulated - begin) * 1000d / Stopwatch.Frequency; var m = (rendered - simulated) * 1000d / Stopwatch.Frequency;
                simulation += s; mesh += m; bytes += allocated; result.worstFrameMs = Math.Max(result.worstFrameMs, s + m); result.worstFrameBytes = Math.Max(result.worstFrameBytes, allocated);
                var live = 0; foreach (var fx in effects) live += fx.LiveParticles; result.peakParticles = Math.Max(result.peakParticles, live);
                root.GetComponentsInChildren(true, filters); var vertices = 0; foreach (var f in filters) if (f.sharedMesh != null) vertices += f.sharedMesh.vertexCount;
                result.peakVertices = Math.Max(result.peakVertices, vertices);
            }
            result.simulationMs = simulation / Frames; result.meshMs = mesh / Frames; result.allocatedBytesPerFrame = bytes / Frames;
            report.fixtures.Add(result); Object.DestroyImmediate(root);
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[Blitz] BigBoss FX perf {0}: particles={1} vertices={2} sim={3:F3}ms mesh={4:F3}ms worst={5:F3}ms alloc={6}B/frame worstAlloc={7}B",
                fixture, result.peakParticles, result.peakVertices, result.simulationMs, result.meshMs, result.worstFrameMs, result.allocatedBytesPerFrame, result.worstFrameBytes));
        }
    }
}
