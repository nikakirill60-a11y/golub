using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Real map, real water, real PhysX: on every map with water the deepest
    /// bed a tank can stand on, a fording point and the deepest point under a deck
    /// (a bridge) are found by casting down from the water surface onto the
    /// map, and a tank is seated at each. Where the water closes over the
    /// roof the deep one must flood, show the 4.8.0 countdown and drown after
    /// it; where it does not, the HUD must still show it in water. The
    /// fording one only slows down.
    /// </summary>
    [InitializeOnLoad]
    public static class WaterSmokeCli
    {
        private const string Key = "Blitz.WaterSmoke";
        private const string TankId = "renaultue57";
        private static bool _hooked;
        private static DateTime _deadline;
        private static int _errors, _map, _stage, _failures;
        private static float _at;
        private static TanksDatabase _db;
        private static BattleWorld _world;
        private static BattleTank _deep, _ford, _bridge;
        private static float _bridgeDepth, _nextTrace;
        private static readonly StringBuilder _trace = new StringBuilder();
        private static BattleWaterIndicator _hud, _bridgeHud;
        private static GameObject _canvas;
        private static float _deepDepth, _fordDepth;
        private static bool _hudShown;
        private static readonly List<string> Maps = new List<string>();
        private static readonly StringBuilder Report = new StringBuilder();

        static WaterSmokeCli() { if (SessionState.GetBool(Key, false)) Hook(); }

        public static void Run()
        {
            SessionState.SetBool(Key, true); SessionState.SetInt(Key + ".result", -1); Hook();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Hook()
        {
            if (_hooked) return; _hooked = true; _deadline = DateTime.UtcNow.AddMinutes(20);
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
                Maps.AddRange(string.IsNullOrEmpty(only) ? new[] { "asia", "rudniki", "lake" } : only.Split(','));
                _map = 0; LoadMap();
            }
            catch (Exception e) { Fail(e); }
        }

        private static void Clear()
        {
            if (_deep != null) UnityEngine.Object.DestroyImmediate(_deep.gameObject);
            if (_ford != null) UnityEngine.Object.DestroyImmediate(_ford.gameObject);
            if (_bridge != null) UnityEngine.Object.DestroyImmediate(_bridge.gameObject);
            if (_canvas != null) UnityEngine.Object.DestroyImmediate(_canvas);
            _deep = _ford = _bridge = null; _hud = _bridgeHud = null; _canvas = null;
        }

        private static void LoadMap()
        {
            Clear();
            if (_world != null) UnityEngine.Object.DestroyImmediate(_world.gameObject);
            TankSuspension.ResetWheelQueue();
            var id = Maps[_map];
            _world = BattleWorld.Create(null, id);
            var water = _world.Water;
            if (water == null) throw new InvalidOperationException(id + ": no water field was built");
            var f = water.Field;
            Report.AppendLine(string.Format("{0}: water renderers={1} triangles={2} x={3:F0}..{4:F0} z={5:F0}..{6:F0} active={7}",
                id, water.RendererCount, water.TriangleCount, f.MinX, f.MaxX, f.MinZ, f.MaxZ, BattleWater.Active == water));

            // Depth = surface minus the map under it; the cast starts just
            // above the surface so a bridge over the river is not the bottom.
            var area = _world.PlayArea;
            var mask = BattleLayers.WheelMask;
            var cells = new Dictionary<long, Vector2>();
            var decks = new HashSet<long>();
            var wet = 0;
            var banks = 0;
            for (var x = f.MinX; x <= f.MaxX; x += 2f)
            {
                for (var z = f.MinZ; z <= f.MaxZ; z += 2f)
                {
                    if (x < area.min.x + 15f || x > area.max.x - 15f || z < area.min.z + 15f || z > area.max.z - 15f) continue;
                    float h;
                    if (!f.TrySample(x, z, out h)) continue;
                    RaycastHit hit;
                    if (!Physics.Raycast(new Vector3(x, h + 0.3f, z), Vector3.down, out hit, 60f, mask, QueryTriggerInteraction.Ignore)) continue;
                    var depth = h - hit.point.y;
                    if (depth <= 0.05f) continue;
                    // Something stands above the water here. A deck (a bridge)
                    // shows its underside to a cast up from the bed; a bank the
                    // water mesh runs under does not, and the "bed" found under
                    // it is scenery buried in the bank.
                    var deck = false;
                    RaycastHit above;
                    if (Physics.Raycast(new Vector3(x, h + 40f, z), Vector3.down, out above, 39.7f, mask, QueryTriggerInteraction.Ignore))
                    {
                        RaycastHit under;
                        deck = Physics.Raycast(hit.point + Vector3.up * 0.2f, Vector3.up, out under, h + 40f - hit.point.y, mask, QueryTriggerInteraction.Ignore)
                            && under.point.y > h + 1f;
                        if (!deck) { banks++; continue; }
                    }

                    wet++;
                    var key = Cell(x, z, f);
                    cells[key] = new Vector2(depth, hit.point.y);
                    if (deck) decks.Add(key);
                }
            }

            // Seat tanks only where the bed around them is wet and no steeper
            // than a tank can stand on.
            Vector3 deep = Vector3.zero, ford = Vector3.zero, bridge = Vector3.zero;
            _deepDepth = _fordDepth = _bridgeDepth = 0f;
            var fordError = float.MaxValue;
            foreach (var pair in cells)
            {
                var cx = (int)(pair.Key >> 32); var cz = (int)(pair.Key & 0xffffffffL);
                if (!Standable(cells, cx, cz, pair.Value.y)) continue;
                var point = new Vector3(f.MinX + cx * 2f, pair.Value.y, f.MinZ + cz * 2f);
                var depth = pair.Value.x;
                if (depth > _deepDepth) { _deepDepth = depth; deep = point; }
                var err = Mathf.Abs(depth - 0.55f);
                if (err < fordError) { fordError = err; _fordDepth = depth; ford = point; }
                if (decks.Contains(pair.Key) && depth > _bridgeDepth) { _bridgeDepth = depth; bridge = point; }
            }

            Report.AppendLine(string.Format("  wet samples={0} (banks skipped={1}) deepest={2:F2} m at {3} ford={4:F2} m at {5} under a deck={6} deepest {7:F2} m at {8}",
                wet, banks, _deepDepth, deep.ToString("F1"), _fordDepth, ford.ToString("F1"), decks.Count, _bridgeDepth, bridge.ToString("F1")));
            if (wet == 0 || _deepDepth <= 0f) throw new InvalidOperationException(id + ": the water field lies nowhere above a bed a tank can stand on");
            RaycastHit bed;
            if (Physics.Raycast(deep + Vector3.up * 1f, Vector3.down, out bed, 3f, BattleLayers.WheelMask, QueryTriggerInteraction.Ignore))
            {
                Report.AppendLine("  deep bed collider=" + bed.collider.name + " (" + bed.collider.GetType().Name + ", layer " + bed.collider.gameObject.layer + ")");
            }

            var card = _db.Get(TankId);
            if (card == null) throw new InvalidOperationException("missing tank " + TankId);
            _deep = Seat(card, deep, "deep");
            _ford = Seat(card, ford, "ford");
            _bridge = _bridgeDepth > 0f ? Seat(card, bridge, "bridge") : null;
            _trace.Length = 0; _nextTrace = 0f;
            _canvas = new GameObject("water smoke canvas", typeof(RectTransform), typeof(Canvas));
            var atlas = AtlasRegionTable.Parse(BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            var lang = LocalizationTable.Parse(BlitzResources.LoadText(BlitzResources.LanguageData));
            var font = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            _hud = BattleWaterIndicator.Create((RectTransform)_canvas.transform, atlas, lang, font, _deep);
            _bridgeHud = _bridge != null ? BattleWaterIndicator.Create((RectTransform)_canvas.transform, atlas, lang, font, _bridge) : null;
            _hudShown = false;
            Physics.SyncTransforms();
            _stage = 0; _at = Time.time;
        }

        private static long Cell(float x, float z, WaterSurfaceField f)
        {
            return ((long)Mathf.RoundToInt((x - f.MinX) / 2f) << 32) | (uint)Mathf.RoundToInt((z - f.MinZ) / 2f);
        }

        private static bool Standable(Dictionary<long, Vector2> cells, int cx, int cz, float y)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                for (var dz = -2; dz <= 2; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    Vector2 v;
                    if (!cells.TryGetValue(((long)(cx + dx) << 32) | (uint)(cz + dz), out v)) return false;
                    // About 17 degrees between the 2 m samples.
                    if (Mathf.Abs(v.y - y) > 0.3f * 2f * Mathf.Sqrt(dx * dx + dz * dz)) return false;
                }
            }

            return true;
        }

        private static BattleTank Seat(TankInfo card, Vector3 point, string label)
        {
            var marker = new GameObject("water smoke " + label).transform;
            marker.position = point + Vector3.up * 0.4f;
            var tank = BattleTank.Spawn(null, Maps[_map] + "/" + label + "/" + card.Name, card, marker, _world.PlayArea, 0);
            UnityEngine.Object.DestroyImmediate(marker.gameObject);
            tank.GetComponent<TankContactPhysics>().ResetState();
            return tank;
        }

        private static string State(BattleTank t)
        {
            var w = t.Water;
            return string.Format("pos={0} hull=[{1:F2}..{2:F2}] depth={3:F2} level={4:F2} sub={5:F2} inWater={6} flooded={7} indicator={8} timer={9:F1} running={10} gunUnder={11} speedFactor={12:F2} dead={13}",
                t.transform.position.ToString("F2"), t.Hull.TracksBottom, t.Hull.Height, w.Depth, w.Level, w.Submersion, w.InWater, w.Flooded,
                w.IndicatorVisible, w.Timer.Remaining, w.Timer.Running, w.GunUnderWater, t.Suspension.EnvironmentSpeedFactor, t.Dead);
        }

        private static void Require(bool ok, string what)
        {
            if (ok) return;
            _failures++;
            Report.AppendLine("  FAIL " + what);
        }

        private static void Tick()
        {
            if (DateTime.UtcNow > _deadline) { Fail(new TimeoutException("Water smoke timeout")); return; }
            if (!EditorApplication.isPlaying || _db == null || _world == null || _deep == null) return;
            try
            {
                var elapsed = Time.time - _at;
                if (_hud != null && _hud.TimerShown) _hudShown = true;
                if (_stage == 0 && Time.time >= _nextTrace)
                {
                    _nextTrace = Time.time + 0.25f;
                    _trace.Append(_deep.transform.position.y.ToString("F2")).Append(' ');
                }
                if (_stage == 0 && elapsed > 2f)
                {
                    Report.AppendLine("  deep @2s: " + State(_deep));
                    Report.AppendLine("  ford @2s: " + State(_ford));
                    Report.AppendLine("  deep y trace: " + _trace);
                    if (_bridge != null) Report.AppendLine("  bridge @2s: " + State(_bridge));
                    var deepEnough = _deepDepth > _deep.Hull.Height - _deep.Hull.TracksBottom + 0.3f;
                    Require(_deep.Water.InWater, "deep tank is not in water");
                    if (deepEnough)
                    {
                        Require(_deep.Water.Flooded && _deep.Water.Timer.Running, "deep tank is not flooded");
                        Require(_hudShown, "the countdown is not on the HUD");
                    }
                    else if (_deep.Water.Level >= WaterRules.IndicatorLevel)
                    {
                        Require(_hud.WadingShown, "the deep tank is not shown in water on the HUD");
                    }

                    // The crossing under a bridge, where the report came from.
                    if (_bridge != null && _bridge.Water.Level >= WaterRules.IndicatorLevel)
                    {
                        Require(_bridgeHud.Shown, "the tank under the bridge is not shown in water on the HUD");
                    }
                    Require(_ford.Water.InWater && !_ford.Water.Flooded, "ford tank is not fording");
                    Require(_ford.Suspension.EnvironmentSpeedFactor < 1f, "fording does not slow the tank");
                    _stage = deepEnough ? 1 : 2; _at = Time.time;
                }
                else if (_stage == 1 && elapsed > WaterRules.DrownSeconds)
                {
                    Report.AppendLine("  deep @drown: " + State(_deep));
                    var last = _deep.Health.DamageInfosTaken.LastOrDefault();
                    Require(_deep.Dead && last != null && last.DmgType == DamageType.Drowning, "deep tank did not drown");
                    Require(!_ford.Dead, "the fording tank drowned");
                    _stage = 2; _at = Time.time;
                }
                else if (_stage == 2)
                {
                    _map++;
                    if (_map < Maps.Count) { LoadMap(); return; }
                    Debug.Log("[Blitz] Water smoke report\n" + Report);
                    if (_errors != 0) throw new InvalidOperationException("Runtime errors: " + _errors);
                    if (_failures != 0) throw new InvalidOperationException("Water smoke failures: " + _failures);
                    Debug.Log("[Blitz] Water smoke Play Mode passed on " + Maps.Count + " maps; errors=0");
                    SessionState.SetInt(Key + ".result", 0); EditorApplication.isPlaying = false; _db = null;
                }
            }
            catch (Exception e) { Fail(e); }
        }

        private static void Fail(Exception e)
        {
            if (Report.Length > 0) Debug.Log("[Blitz] Water smoke report\n" + Report);
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
