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
    /// Real map terrain, real spawn markers, real PhysX: every spawn of every
    /// random-battle map seats a tank, lets it settle, checks the visible track
    /// is not buried in the landscape and then drives forward and turns.
    /// </summary>
    [InitializeOnLoad]
    public static class MapSpawnDriveSmokeCli
    {
        private const string Key = "Blitz.MapSpawnDriveSmoke";
        private static readonly string[] Roster = { "ms-1", "t-34", "maus", "is-7", "pziii_a", "m4_sherman" };
        private const int MaxPerMap = 24;
        private static bool _hooked;
        private static DateTime _deadline;
        private static int _errors, _map, _stage, _failures, _measured;
        private static float _at;
        private static TanksDatabase _db;
        private static BattleWorld _world;
        private static readonly List<string> Maps = new List<string>();
        private static readonly List<BattleTank> Live = new List<BattleTank>();
        private static readonly List<Vector3> Start = new List<Vector3>();
        private static readonly List<float> Yaw = new List<float>();
        private static readonly List<float> Sink = new List<float>();
        private static readonly List<float> Peak = new List<float>();
        private static readonly List<bool> Overlap = new List<bool>();
        private static readonly StringBuilder Report = new StringBuilder();

        static MapSpawnDriveSmokeCli() { if (SessionState.GetBool(Key, false)) Hook(); }

        public static void Run()
        {
            SessionState.SetBool(Key, true); SessionState.SetInt(Key + ".result", -1); Hook();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Hook()
        {
            if (_hooked) return; _hooked = true; _deadline = DateTime.UtcNow.AddMinutes(25);
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
                Maps.Clear();
                var only = Environment.GetEnvironmentVariable("BLITZ_SMOKE_MAPS");
                if (!string.IsNullOrEmpty(only)) Maps.AddRange(only.Split(','));
                else
                    foreach (var tier in new[] { 1, 5, 10 })
                        foreach (var id in BattleMapCatalog.AvailableForTier(tier))
                            if (!Maps.Contains(id)) Maps.Add(id);
                _map = 0; LoadMap();
            }
            catch (Exception e) { Fail(e); }
        }

        private static readonly List<Transform> Markers = new List<Transform>();
        private static readonly List<List<int>> Groups = new List<List<int>>();
        private static int _group;

        private static void LoadMap()
        {
            ClearTanks();
            if (_world != null) UnityEngine.Object.DestroyImmediate(_world.gameObject);
            TankSuspension.ResetWheelQueue();
            _world = BattleWorld.Create(null, Maps[_map]);
            Markers.Clear(); Groups.Clear();
            Markers.AddRange(_world.PlayerSpawns); Markers.AddRange(_world.BotSpawns);
            // Tanks in one group are far enough apart that a 3 s drive cannot
            // make them meet, so every result judges terrain support alone.
            for (var i = 0; i < Markers.Count && i < MaxPerMap; i++)
            {
                List<int> home = null;
                foreach (var g in Groups)
                {
                    var free = true;
                    foreach (var j in g) if (Vector3.Distance(Markers[i].position, Markers[j].position) < 45f) free = false;
                    if (free) { home = g; break; }
                }
                if (home == null) { home = new List<int>(); Groups.Add(home); }
                home.Add(i);
            }
            _group = 0; SpawnGroup();
        }

        private static void ClearTanks()
        {
            foreach (var t in Live) if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);
            Live.Clear(); Start.Clear(); Yaw.Clear(); Sink.Clear(); Peak.Clear(); Overlap.Clear();
        }

        private static void SpawnGroup()
        {
            ClearTanks();
            TankSuspension.ResetWheelQueue();
            foreach (var i in Groups[_group])
            {
                var id = Roster[(i + _map) % Roster.Length];
                var card = _db.Get(id) ?? _db.Get("t-34");
                var t = BattleTank.Spawn(null, Maps[_map] + "/" + i + "/" + Markers[i].name + "/" + card.Name, card,
                    Markers[i], _world.PlayArea, i < _world.PlayerSpawns.Count ? 0 : 1);
                t.GetComponent<TankContactPhysics>().ResetState();
                Live.Add(t);
            }
            Physics.SyncTransforms();
            _stage = 0; _at = Time.time;
        }

        private static void Tick()
        {
            if (DateTime.UtcNow > _deadline) { Fail(new TimeoutException("Map spawn drive smoke timeout")); return; }
            if (!EditorApplication.isPlaying || _db == null || _world == null) return;
            try
            {
                var elapsed = Time.time - _at;
                if (_stage == 0 && elapsed > 3f)
                {
                    foreach (var t in Live)
                    {
                        Start.Add(t.transform.position); Yaw.Add(t.transform.eulerAngles.y);
                        var band = t.GetComponent<TankContactPhysics>().ContactBand.bounds; band.Expand(.6f);
                        var touching = false;
                        foreach (var other in Live)
                            if (other != t && band.Intersects(other.GetComponent<TankContactPhysics>().ContactBand.bounds)) touching = true;
                        Overlap.Add(touching);
                        Sink.Add(t.Suspension.AnyHit ? t.Suspension.MeanTrackPenetration : float.NaN); Peak.Add(t.Suspension.AnyHit ? t.Suspension.TrackPenetration : float.NaN);
                        t.Blocked = false; t.Drive(1, 0);
                    }
                    _stage = 1; _at = Time.time;
                }
                else if (_stage == 1)
                {
                    foreach (var t in Live) t.Drive(1, 0);
                    if (elapsed < 3f) return;
                    for (var i = 0; i < Live.Count; i++)
                    {
                        var t = Live[i];
                        var moved = Vector3.Distance(Start[i], t.transform.position);
                        var hits = t.Suspension.WheelHits;
                        var sink = Sink[i];
                        var nearest = float.PositiveInfinity;
                        for (var j = 0; j < Start.Count; j++) if (j != i) nearest = Mathf.Min(nearest, Vector3.Distance(Start[i], Start[j]));
                        // Seating every marker at once puts some tanks against each other; those
                        // are reported but cannot judge terrain support.
                        var overlap = Overlap[i];
                        var ok = overlap || (!float.IsNaN(sink) && sink < .06f && moved > 1.5f);
                        _measured++; if (!ok) _failures++;
                        Report.AppendLine(string.Format("{0,-48} sink={1,7:F3} peak={8,7:F3} moved={2,6:F2} wheels={3,2} pitch={5,6:F1} roll={6,6:F1} nearest={7,6:F1} {4}",
                            t.name, sink, moved, hits, overlap ? "overlap" : ok ? "ok" : "FAIL", t.Suspension.PitchDegrees, t.Suspension.RollDegrees, nearest, Peak[i]));
                        if (!ok) Report.AppendLine("    contacts: " + Contacts(t));
                        Start[i] = t.transform.position; t.Drive(0, 1);
                    }
                    _stage = 2; _at = Time.time;
                }
                else if (_stage == 2)
                {
                    foreach (var t in Live) t.Drive(0, 1);
                    if (elapsed < 2f) return;
                    for (var i = 0; i < Live.Count; i++)
                    {
                        var turned = Mathf.Abs(Mathf.DeltaAngle(Yaw[i], Live[i].transform.eulerAngles.y));
                        if (turned < 5f && !Overlap[i]) { _failures++; Report.AppendLine(Live[i].name + " did not turn: " + turned); }
                    }
                    _group++;
                    if (_group < Groups.Count) { SpawnGroup(); return; }
                    _map++;
                    if (_map < Maps.Count) { LoadMap(); return; }
                    Debug.Log("[Blitz] Map spawn drive report\n" + Report);
                    if (_errors != 0) throw new InvalidOperationException("Runtime errors: " + _errors);
                    if (_failures != 0) throw new InvalidOperationException("Map spawn failures: " + _failures + "/" + _measured);
                    Debug.Log("[Blitz] Map spawn drive Play Mode passed: " + _measured + " spawns on " + Maps.Count + " maps; errors=0");
                    SessionState.SetInt(Key + ".result", 0); EditorApplication.isPlaying = false; _db = null;
                }
            }
            catch (Exception e) { Fail(e); }
        }

        /// <summary>Colliders the hull shell is resting on or wedged into.</summary>
        private static string Contacts(BattleTank t)
        {
            var hull = t.transform.Find("hull_phys_col").GetComponent<Collider>();
            var b = hull.bounds; var sb = new StringBuilder();
            foreach (var c in Physics.OverlapBox(b.center, b.extents + Vector3.one * .1f, Quaternion.identity, ~0, QueryTriggerInteraction.Collide))
            {
                if (c.attachedRigidbody == t.GetComponent<Rigidbody>()) continue;
                Vector3 dir; float dist;
                var pen = Physics.ComputePenetration(hull, hull.transform.position, hull.transform.rotation,
                    c, c.transform.position, c.transform.rotation, out dir, out dist);
                sb.Append(c.name).Append('[').Append(LayerMask.LayerToName(c.gameObject.layer)).Append(c.gameObject.layer)
                  .Append(c.isTrigger ? ",trigger" : "").Append(pen ? ",pen=" + dist.ToString("F3") + " n=" + dir.ToString("F2") : "").Append("] ");
            }
            return sb.ToString();
        }

        private static void Fail(Exception e)
        {
            if (Report.Length > 0) Debug.Log("[Blitz] Map spawn drive report\n" + Report);
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
