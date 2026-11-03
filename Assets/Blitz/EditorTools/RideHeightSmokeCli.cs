using System;
using System.Collections.Generic;
using System.Text;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Real PhysX ride-height check for every tank card: after settling on flat
    /// ground the visible track bottom must stay on the surface (not sink into
    /// it), the hull shell must stay clear of the ground and every wheel ray must
    /// report support. Batches stay below the per-step wheel budget.
    /// </summary>
    [InitializeOnLoad]
    public static class RideHeightSmokeCli
    {
        private const string Key = "Blitz.RideHeightSmoke";
        /// <summary>Visible track may sink at most this far below the surface.</summary>
        public const float MaxTrackSink = .08f;
        /// <summary>Maximum float of the visible track above the surface.</summary>
        public const float MaxTrackFloat = .08f;
        private const int BatchSize = 12;
        private const float SettleSeconds = 3f;
        private static bool _hooked;
        private static DateTime _deadline;
        private static int _errors, _batch, _failures, _measured;
        private static float _at;
        private static TanksDatabase _db;
        private static readonly List<string> Ids = new List<string>();
        private static readonly List<BattleTank> Live = new List<BattleTank>();
        private static readonly StringBuilder Report = new StringBuilder();
        private const float GroundTop = 20f;

        static RideHeightSmokeCli() { if (SessionState.GetBool(Key, false)) Hook(); }

        public static void Run()
        {
            SessionState.SetBool(Key, true); SessionState.SetInt(Key + ".result", -1); Hook();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Hook()
        {
            if (_hooked) return; _hooked = true; _deadline = DateTime.UtcNow.AddMinutes(10);
            EditorApplication.update += Tick; EditorApplication.playModeStateChanged += Changed;
            Application.logMessageReceived += Message;
        }

        private static void Message(string m, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception) _errors++;
        }

        private static void Changed(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.delayCall += Prepare;
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                var r = SessionState.GetInt(Key + ".result", -1);
                if (r >= 0) Finish(r);
            }
        }

        private static void Prepare()
        {
            try
            {
                TankSuspension.ApplySourcePhysicsDefaults();
                _db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                var only = Environment.GetEnvironmentVariable("BLITZ_RIDE_TANKS");
                Ids.Clear();
                if (!string.IsNullOrEmpty(only)) Ids.AddRange(only.Split(','));
                else
                    for (var level = 1; level <= 10; level++)
                        foreach (var card in _db.ByLevel(level))
                            if (!Ids.Contains(card.Name)) Ids.Add(card.Name);
                var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ground.name = "ride height ground"; ground.layer = BattleLayers.Static;
                ground.transform.position = new Vector3(1000, GroundTop - .5f, 1000);
                ground.transform.localScale = new Vector3(400, 1, 400);
                _batch = 0; SpawnBatch();
            }
            catch (Exception e) { Fail(e); }
        }

        private static void SpawnBatch()
        {
            Live.Clear();
            for (var i = 0; i < BatchSize; i++)
            {
                var index = _batch * BatchSize + i;
                if (index >= Ids.Count) break;
                var id = Ids[index];
                var card = _db.Get(id);
                if (card == null) { Report.AppendLine(id + ": no card"); continue; }
                var pos = new Vector3(880 + i * 20, GroundTop + .25f, 1000);
                var t = BattleTank.Spawn(null, id, card, null, new Bounds(), 0);
                t.GetComponent<Rigidbody>().position = pos; t.transform.position = pos;
                foreach (var p in t.Plates) p.SyncToMount();
                t.GetComponent<TankContactPhysics>().ResetState();
                Live.Add(t);
            }
            Physics.SyncTransforms();
            _at = Time.time;
        }

        private static void Tick()
        {
            if (DateTime.UtcNow > _deadline) { Fail(new TimeoutException("Ride height smoke timeout")); return; }
            if (!EditorApplication.isPlaying || _db == null) return;
            try
            {
                if (Time.time - _at < SettleSeconds) return;
                foreach (var t in Live) Measure(t);
                foreach (var t in Live) if (t != null) UnityEngine.Object.Destroy(t.gameObject);
                _batch++;
                if (_batch * BatchSize < Ids.Count) { SpawnBatch(); return; }
                Debug.Log("[Blitz] Ride height report\n" + Report);
                if (_errors != 0) throw new InvalidOperationException("Runtime errors: " + _errors);
                if (_failures != 0) throw new InvalidOperationException("Ride height failures: " + _failures + "/" + _measured);
                Debug.Log("[Blitz] Ride height Play Mode passed: " + _measured + " tanks; errors=0");
                SessionState.SetInt(Key + ".result", 0); EditorApplication.isPlaying = false; _db = null;
            }
            catch (Exception e) { Fail(e); }
        }

        private static void Measure(BattleTank t)
        {
            if (t == null) return;
            _measured++;
            var trackMin = float.PositiveInfinity;
            foreach (var r in t.Model.GetComponentsInChildren<MeshRenderer>(false))
            {
                var name = r.gameObject.name.Replace("(Clone)", "").Trim();
                if (name == "trackL" || name == "trackR") trackMin = Mathf.Min(trackMin, r.bounds.min.y);
            }
            var hull = t.transform.Find("hull_phys_col").GetComponent<Collider>();
            var track = trackMin - GroundTop;
            var hullGap = hull.bounds.min.y - GroundTop;
            var hits = t.Suspension.WheelHits;
            var all = t.Suspension.WheelCount * 2;
            var ok = track >= -MaxTrackSink && track <= MaxTrackFloat && hits == all && hullGap > -.01f;
            if (!ok) _failures++;
            Report.AppendLine(string.Format("{0,-24} track={1,7:F3} hullGap={2,7:F3} wheels={3}/{4} {5}",
                t.Card != null ? t.Card.Name : "?", track, hullGap, hits, all, ok ? "ok" : "FAIL"));
        }

        private static void Fail(Exception e)
        {
            if (Report.Length > 0) Debug.Log("[Blitz] Ride height report\n" + Report);
            Debug.LogException(e); SessionState.SetInt(Key + ".result", 1);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Finish(1);
        }

        private static void Finish(int code)
        {
            SessionState.SetBool(Key, false); EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= Changed; Application.logMessageReceived -= Message;
            _hooked = false; EditorApplication.Exit(code);
        }
    }
}
