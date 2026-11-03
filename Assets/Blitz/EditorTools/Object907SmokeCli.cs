using System;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Live Object 907 spawn, armour, drive, traverse and complete three-type firing cycle.</summary>
    [InitializeOnLoad]
    public static class Object907SmokeCli
    {
        private const string Key = "Blitz.Object907Smoke";
        private static BattleTank _tank;
        private static BattleRoster _roster;
        private static int _shots, _completed;
        private static bool _initialised;
        private static Vector3 _start;
        private static float _at, _travel, _turn;
        private static int _errors;
        private static bool _hooked;
        private static DateTime _deadline;

        static Object907SmokeCli() { if (SessionState.GetBool(Key, false)) Hook(); }

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

        private static void Message(string text, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception) _errors++;
        }

        private static void Changed(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.delayCall += Prepare;
            if (state != PlayModeStateChange.EnteredEditMode) return;
            var result = SessionState.GetInt(Key + ".result", -1);
            if (result < 0) return;
            SessionState.SetBool(Key, false);
            EditorApplication.Exit(result);
        }

        private static void Box(string name, Vector3 position, Vector3 size)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.layer = BattleLayers.Static;
            box.transform.position = position;
            box.transform.localScale = size;
        }

        private static void Prepare()
        {
            try
            {
                TankSuspension.ApplySourcePhysicsDefaults();
                _roster = BattleRoster.Begin(null, 0);
                _roster.StartBattle();
                var db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                var origin = new Vector3(1000f, 20f, 1000f);
                Box("907 ground", origin - Vector3.up * 0.5f, new Vector3(400f, 1f, 400f));
                var spawn = new GameObject("907 spawn").transform;
                spawn.position = origin + Vector3.up * 0.5f;
                _tank = BattleTank.Spawn(null, "Object 907 smoke", db.Get("object_907"), spawn,
                    new Bounds(origin, new Vector3(400f, 100f, 400f)), 0);
                if (_tank.Health.MaxHealth != 1800 || _tank.Plates.Count != 33
                    || Mathf.Abs(_tank.GunInfo.Reload - 7.1f) > .001f
                    || _tank.Gun is BattleGunBurst || _tank.GunInfo.Shells.Count != 3)
                    throw new InvalidOperationException("Object 907 runtime assembly does not match imported source");
                _tank.Blocked = false;
                _tank.SetShellAmounts(new[] { 1, 1, 1 });
                _tank.PrimaryProjectileFired += slot => {
                    if (slot != _shots) _errors++;
                    _shots++;
                };
                _tank.Gun.Reloaded += () => _completed++;
                _start = _tank.Pos();
                _initialised = true;
                Physics.SyncTransforms();
                _at = Time.time;
            }
            catch (Exception e) { Debug.LogException(e); Finish(false); }
        }

        private static void Tick()
        {
            if (DateTime.UtcNow > _deadline) { Finish(false); return; }
            if (!EditorApplication.isPlaying || !_initialised || _tank == null) return;
            var elapsed = Time.time - _at;
            // Settle suspension, then drive a real rigid body while the source gun reloads.
            _tank.Drive(elapsed > 1f && elapsed < 14f ? .7f : 0f, elapsed > 3f && elapsed < 10f ? .3f : 0f);
            if (elapsed > 1f && _tank.Gun.CanShoot()) _tank.Gun.ShootAttempt();
            _travel = Mathf.Max(_travel, Vector3.ProjectOnPlane(_tank.Pos() - _start, Vector3.up).magnitude);
            _turn = Mathf.Max(_turn, Vector3.Angle(Vector3.forward,
                Vector3.ProjectOnPlane(_tank.transform.forward, Vector3.up)));
            if (elapsed < 23f) return;
            var empty = _tank.AvailableShellCount == 0 && !_tank.Gun.CanShoot()
                && _tank.Gun.ReloadRemaining() == 0f;
            Debug.Log($"[Object 907] shots={_shots} reloads={_completed} empty={empty} plates={_tank.Plates.Count} travel={_travel:F2} turn={_turn:F1} errors={_errors}");
            Finish(_shots == 3 && _completed == 2 && empty && _travel > 3f && _turn > 10f
                && !_tank.Dead && _errors == 0);
        }

        private static void Finish(bool passed)
        {
            EditorApplication.update -= Tick;
            _roster?.End();
            SessionState.SetInt(Key + ".result", passed ? 0 : 1);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            else { SessionState.SetBool(Key, false); EditorApplication.Exit(passed ? 0 : 1); }
        }
    }
}
