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
    /// <summary>Live hull steering beside a house-sized collider, not a mocked drive axis.</summary>
    [InitializeOnLoad]
    public static class CombatRecoverySmokeCli
    {
        private const string Key = "Blitz.CombatRecoverySmoke";
        private static BattleTank _tank;
        private static AiTankController _ai;
        private static Vector3 _start, _threat;
        private static float _at, _travel, _turn;
        private static int _errors;
        private static bool _hooked, _escape;
        private static DateTime _deadline;

        static CombatRecoverySmokeCli() { if (SessionState.GetBool(Key, false)) Hook(); }

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
                AiTankController.BotsStationary = false;
                var db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                var origin = new Vector3(1000f, 20f, 1000f);
                Box("recovery ground", origin - Vector3.up * 0.5f, new Vector3(200f, 1f, 200f));
                var spawn = new GameObject("recovery spawn").transform;
                spawn.position = origin + Vector3.up * 0.5f;
                _tank = BattleTank.Spawn(null, "wall recovery", db.Get("ms-1"), spawn,
                    new Bounds(origin, new Vector3(200f, 100f, 200f)), 0);
                _tank.Blocked = false;
                _ai = AiTankController.Attach(_tank, AiSkillLevel.GreenArmy);
                // Isolate the combat facing request from strategic state transitions.
                _ai.enabled = false;
                _start = _tank.Pos();
                _threat = origin + Vector3.right * 50f;
                Box("house beside hull", origin + Vector3.right * (_tank.HullWidth * 0.5f + 0.55f)
                    + Vector3.up * 4f, new Vector3(1f, 8f, 8f));
                Physics.SyncTransforms();
                _at = Time.time;
            }
            catch (Exception e) { Debug.LogException(e); Finish(false); }
        }

        private static void Tick()
        {
            if (DateTime.UtcNow > _deadline) { Finish(false); return; }
            if (!EditorApplication.isPlaying || _ai == null) return;
            _ai.FaceTowards(_threat, 0f);
            _escape |= Mathf.Abs(_tank.ForwardAxis) > 0.1f;
            _travel = Mathf.Max(_travel, Vector3.ProjectOnPlane(_tank.Pos() - _start, Vector3.up).magnitude);
            _turn = Mathf.Max(_turn, Vector3.Angle(Vector3.forward,
                Vector3.ProjectOnPlane(_tank.transform.forward, Vector3.up)));
            if (Time.time - _at < 25f) return;
            Debug.Log($"[Combat recovery] escape={_escape} travel={_travel:F2} turn={_turn:F1} errors={_errors}");
            Finish(_escape && _travel > 1f && _turn > 45f && _errors == 0);
        }

        private static void Finish(bool passed)
        {
            EditorApplication.update -= Tick;
            SessionState.SetInt(Key + ".result", passed ? 0 : 1);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            else { SessionState.SetBool(Key, false); EditorApplication.Exit(passed ? 0 : 1); }
        }
    }
}
