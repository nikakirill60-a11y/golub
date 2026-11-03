using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Platform;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Actual camera transforms, rocket integration, cooked map and touch input.</summary>
    [InitializeOnLoad]
    public static class BattleRegressionSmokeCli
    {
        private const string Key = "Blitz.BattleRegressionSmoke";
        private static bool _hooked;
        private static int _stage, _failures, _errors, _touchCase;
        private static float _at;
        private static DateTime _deadline;
        private static BattleTank _tank;
        private static BattleCamera _view;
        private static BattleControls _controls;
        private static BattleScoreboard _board;
        private static Touchscreen _touch;
        private static InputSettings _savedInput, _smokeInput;
        private static bool _savedBackground;
        private static Vector2 _pointer;
        static BattleRegressionSmokeCli() { if (SessionState.GetBool(Key, false)) Hook(); }
        public static void Run()
        {
            SessionState.SetBool(Key, true);
            SessionState.SetInt(Key + ".result", -1);
            Hook();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorApplication.isPlaying = true;
        }
        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            _deadline = DateTime.UtcNow.AddMinutes(3);
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += Changed;
            Application.logMessageReceived += Message;
        }
        private static void Message(string message, string stack, LogType type)
        { if (type == LogType.Error || type == LogType.Exception) _errors++; }
        private static void Changed(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.delayCall += Prepare;
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                var result = SessionState.GetInt(Key + ".result", -1);
                if (result >= 0) { SessionState.SetBool(Key, false); EditorApplication.Exit(result); }
            }
        }
        private static void Check(bool ok, string message)
        {
            if (!ok) _failures++;
            Debug.Log("[Battle regression] " + (ok ? "PASS " : "FAIL ") + message);
        }
        private static void Prepare()
        {
            try
            {
                _savedInput = InputSystem.settings;
                _smokeInput = UnityEngine.Object.Instantiate(_savedInput);
                InputSystem.settings = _smokeInput;
                _smokeInput.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                _smokeInput.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                _savedBackground = Application.runInBackground; Application.runInBackground = true;
                var db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                _tank = BattleTank.Spawn(null, "regression", db.Get("t49_rocket"), null, new Bounds(), 0);
                _tank.GetComponent<Rigidbody>().isKinematic = true;
                _view = BattleCamera.Create(_tank, true);
                _view.SetSniperMode(true);
                _view.Follow(0);
                var pivot = _tank.Turret.GunPivot.position;
                var direction = _view.Sniper.transform.forward;
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = "scope wall"; wall.layer = BattleLayers.Static;
                wall.transform.position = pivot + direction * 1.2f;
                wall.transform.localScale = new Vector3(10, 10, .5f);
                wall.transform.rotation = Quaternion.LookRotation(direction);
                Physics.SyncTransforms(); _view.Follow(0);
                Check(Vector3.Dot(_view.Sniper.transform.position - pivot, direction) < .95f,
                    "scope lens stays before the wall: distance=" + Vector3.Dot(_view.Sniper.transform.position - pivot, direction));
                UnityEngine.Object.DestroyImmediate(wall);
                CheckFlights(db);
                var table = BattleObjects.LoadTable("rudniki");
                var host = new GameObject("rock audit");
                var objects = BattleObjects.Build(host.transform, table, BattleObjects.LoadMeshes(table), BattleObjects.LoadShapes(table), null, false);
                int rocks = 0, concave = 0;
                foreach (var c in objects.Root.GetComponentsInChildren<MeshCollider>())
                    if (c.name.StartsWith("stone_", StringComparison.OrdinalIgnoreCase))
                    { rocks++; if (!c.convex) concave++; }
                Check(rocks > 0 && concave == 0, "Rudniki individual rocks convex: rocks=" + rocks + " concave=" + concave);
                Check(objects.Root.GetComponentsInChildren<BattleRockSupport>().Length == rocks,
                    "every collidable boulder marked; render-only stone groups and buildings remain unmarked");
                objects.Release(); UnityEngine.Object.DestroyImmediate(host);
                _view.SetSniperMode(false);
                _controls = BattleControls.Create(_tank, _view, true);
                var hud = BattleHud.Create(_tank, _view, true);
                _board = BattleScoreboard.Create(hud.Root);
                var roster = new List<BattleParticipantStats>();
                for (int i = 0; i < 20; i++)
                {
                    var stats = new BattleParticipantStats();
                    typeof(BattleParticipantStats).GetProperty("Team").SetValue(stats, i % 2);
                    typeof(BattleParticipantStats).GetProperty("Nickname").SetValue(stats, "regression");
                    typeof(BattleParticipantStats).GetProperty("Tank").SetValue(stats, _tank);
                    roster.Add(stats);
                }
                _board.SetRoster(roster, 0); _board.Toggle(); _board.Advance(BattleScoreboard.SlideSeconds);
                MouseLock.Set(false);
                _touch = InputSystem.AddDevice<Touchscreen>();
                InputSystem.EnableDevice(_touch);
                _touchCase = 0;
                _pointer = new Vector2(Screen.width * .5f, Screen.height * .5f);
                QueueTouch(UnityEngine.InputSystem.TouchPhase.Began, 11);
                _at = Time.time; _stage = 0;
            }
            catch (Exception e) { Debug.LogException(e); Finish(); }
        }
        private static void CheckFlights(TanksDatabase db)
        {
            foreach (var gun in db.Get("t49_rocket").Guns)
            foreach (var shell in gun.Shells)
            {
                if (!shell.Atgm) continue;
                foreach (float range in new[] { 20f, 50f, 100f, 200f, 400f, 600f })
                {
                    var pos = new Vector3(0, 2, 0);
                    var target = new Vector3(0, 2, range);
                    var velocity = Quaternion.Euler(-ArtyMath.GuidedLaunchPitch(range, 12, shell.Speed, shell.TurnSpeed), 0, 0) * Vector3.forward * shell.Speed;
                    var origin = pos;
                    var launchVelocity = velocity;
                    float miss = float.PositiveInfinity;
                    for (int i = 0; i < 600 && pos.z < range + 15; i++)
                    {
                        velocity = BattleProjectile.GuidedVelocity(velocity, pos, target, shell.TurnSpeed, .02f, true, origin, launchVelocity);
                        var next = pos + velocity * .02f;
                        var segment = next - pos;
                        var t = Mathf.Clamp01(Vector3.Dot(target - pos, segment) / segment.sqrMagnitude);
                        miss = Mathf.Min(miss, Vector3.Distance(target, pos + segment * t)); pos = next;
                    }
                    Check(miss < .6f, shell.Name + " overhead range=" + range + " closest=" + miss);
                }
            }
        }
        private static void QueueTouch(UnityEngine.InputSystem.TouchPhase phase, int id = 21)
        {
            InputSystem.QueueStateEvent(_touch, new TouchState { touchId = id, phase = phase, position = _pointer, pressure = phase == UnityEngine.InputSystem.TouchPhase.Ended ? 0 : 1 });
        }
        private static void Tick()
        {
            if (DateTime.UtcNow > _deadline) { _failures++; Finish(); return; }
            if (!EditorApplication.isPlaying || _board == null) return;
            try
            {
                var elapsed = Time.time - _at;
                if (_stage == 0 && elapsed > .2f)
                {
                    Check(_controls.Surface.PointerCount == 1, "first finger remains on camera surface");
                    _at = Time.time; _stage = 1;
                }
                else if (_stage == 1 && elapsed > .4f)
                {
                    if (!_board.IsOpen) _board.Toggle();
                    _board.Advance(BattleScoreboard.SlideSeconds);
                    var enemy = _touchCase % 2 != 0;
                    var button = enemy ? _board.EnemyButton : _board.AllyButton;
                    // The second six cases deliberately have no rendered handle.
                    // Input must not depend on ForceUpdateCanvases/graphic.depth.
                    button.targetGraphic.enabled = _touchCase < 6;
                    button.targetGraphic.canvasRenderer.cull = _touchCase >= 6;
                    var box = (RectTransform)(enemy ? _board.EnemyRoot : _board.AllyRoot).Find("team_box");
                    var rowY = new[] { 23f, 223f, 473f }[(_touchCase / 2) % 3];
                    _pointer = RectTransformUtility.WorldToScreenPoint(null, box.TransformPoint(new Vector3(enemy ? 100 : 400, -rowY, 0)));
                    var hits = new List<RaycastResult>();
                    EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = _pointer }, hits);
                    Check(hits.Count > 0 && hits[0].gameObject == button.gameObject,
                        "case=" + _touchCase + " visible " + (enemy ? "enemy" : "ally") + " row y=" + rowY +
                        " culled=" + button.targetGraphic.canvasRenderer.cull + " hits=" + (hits.Count == 0 ? "none" : hits[0].gameObject.name));
                    QueueTouch(UnityEngine.InputSystem.TouchPhase.Began); _at = Time.time; _stage = 2;
                }
                else if (_stage == 2 && elapsed > .15f)
                {
                    Check(!_board.IsOpen, "case=" + _touchCase + " second-finger touch-down closes scoreboard before release");
                    _pointer += new Vector2(35, -10);
                    QueueTouch(UnityEngine.InputSystem.TouchPhase.Moved); _at = Time.time; _stage = 4;
                }
                else if (_stage == 4 && elapsed > .15f)
                { QueueTouch(UnityEngine.InputSystem.TouchPhase.Ended); _at = Time.time; _stage = 3; }
                else if (_stage == 3 && elapsed > .8f)
                {
                    Check(!_board.IsOpen && _controls.Surface.PointerCount == 1,
                        "case=" + _touchCase + " release does not toggle again or steal held camera finger; camera pointers=" + _controls.Surface.PointerCount);
                    _touchCase++;
                    if (_touchCase < 12) { _at = Time.time; _stage = 1; }
                    else
                    {
                        _pointer = new Vector2(Screen.width * .5f, Screen.height * .5f);
                        QueueTouch(UnityEngine.InputSystem.TouchPhase.Ended, 11);
                        _at = Time.time; _stage = 5;
                    }
                }
                else if (_stage == 5 && elapsed > .2f)
                {
                    Check(_controls.Surface.PointerCount == 0, "held camera finger releases normally");
                    Finish();
                }
            }
            catch (Exception e) { Debug.LogException(e); Finish(); }
        }
        private static void Finish()
        {
            _stage = -1;
            if (_touch != null) { InputSystem.RemoveDevice(_touch); _touch = null; }
            if (_savedInput != null) { InputSystem.settings = _savedInput; UnityEngine.Object.DestroyImmediate(_smokeInput); }
            Application.runInBackground = _savedBackground;
            var result = _failures == 0 && _errors == 0 ? 0 : 1;
            Debug.Log("[Battle regression] finished failures=" + _failures + " errors=" + _errors);
            SessionState.SetInt(Key + ".result", result);
            EditorApplication.isPlaying = false;
        }
    }
}
