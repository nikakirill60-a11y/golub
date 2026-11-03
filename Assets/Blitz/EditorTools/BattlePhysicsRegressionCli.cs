using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Live projectile/armour and drive/reverse against the actual Rudniki collision meshes.</summary>
    [InitializeOnLoad]
    public static class BattlePhysicsRegressionCli
    {
        private const string Key = "Blitz.BattlePhysicsRegression";
        private static bool _hooked;
        private static int _stage, _case, _errors, _failures, _hp;
        private static float _at;
        private static DateTime _deadline;
        private static TanksDatabase _db;
        private static BattleObjects _objects;
        private static GameObject _host, _rock;
        private static readonly List<MeshCollider> Rocks = new List<MeshCollider>();
        private static BattleTank _tank, _target;
        private static BattleProjectile _rocket;
        private static Vector3 _start;
        private static bool _delayChecked;
        static BattlePhysicsRegressionCli() { if (SessionState.GetBool(Key, false)) Hook(); }
        public static void Run()
        {
            SessionState.SetBool(Key, true); SessionState.SetInt(Key + ".result", -1); Hook();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorApplication.isPlaying = true;
        }
        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true; _deadline = DateTime.UtcNow.AddMinutes(5);
            EditorApplication.update += Tick; EditorApplication.playModeStateChanged += Changed;
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
            Debug.Log("[Physics regression] " + (ok ? "PASS " : "FAIL ") + message);
        }
        private static BattleTank Spawn(string id, Vector3 position, bool frozen)
        {
            var tank = BattleTank.Spawn(null, id, _db.Get(id), null, new Bounds(), 0);
            tank.transform.position = position; tank.GetComponent<Rigidbody>().position = position;
            tank.GetComponent<Rigidbody>().isKinematic = frozen;
            foreach (var plate in tank.Plates) plate.SyncToMount();
            tank.GetComponent<TankContactPhysics>().ResetState();
            Physics.SyncTransforms();
            return tank;
        }
        private static void Prepare()
        {
            try
            {
                TankSuspension.ApplySourcePhysicsDefaults();
                _db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                _case = 0;
                if (Environment.GetEnvironmentVariable("BLITZ_SMOKE_HIGHCENTER_ONLY") == "1") BeginHighCenter();
                else BeginFlight();
            }
            catch (Exception e) { Debug.LogException(e); Finish(); }
        }
        private static void BeginFlight()
        {
            if (_tank != null) UnityEngine.Object.DestroyImmediate(_tank.gameObject);
            if (_target != null) UnityEngine.Object.DestroyImmediate(_target.gameObject);
            _tank = Spawn("t49_rocket", new Vector3(1000, 50, 1000), true);
            _tank.enabled = false;
            _target = Spawn("t-34", new Vector3(1000, 50, _case < 2 ? 1100 : 1400), true);
            _target.enabled = false;
            var shell = _tank.GunInfo.Shells[_case % 2];
            // Probe the actual roof surface which the overhead camera can select.
            RaycastHit roof;
            var point = _target.transform.position;
            if (!Physics.Raycast(point + Vector3.up * 20, Vector3.down, out roof, 40,
                (1 << BattleLayers.Armor), QueryTriggerInteraction.Ignore))
                throw new InvalidOperationException("No real roof armour beneath crosshair");
            var aim = roof.point;
            _tank.Turret.LobAim = true;
            _tank.Turret.AimTarget = _tank.Turret.AimTargetLerp = aim;
            var muzzle = _tank.Turret.Muzzle.position;
            var diff = aim - muzzle;
            var flat = new Vector3(diff.x, 0, diff.z);
            var pitch = ArtyMath.GuidedLaunchPitch(flat.magnitude, 12, shell.Speed, shell.TurnSpeed, diff.y);
            var direction = Quaternion.AngleAxis(-pitch, Vector3.Cross(Vector3.up, flat.normalized)) * flat.normalized;
            _rocket = BattleProjectile.Create(shell, _tank, muzzle, muzzle, direction, false);
            _hp = _target.Health.Health;
            _at = Time.time; _stage = 1;
        }
        private static void BeginRocks()
        {
            if (_tank != null) UnityEngine.Object.DestroyImmediate(_tank.gameObject);
            if (_target != null) UnityEngine.Object.DestroyImmediate(_target.gameObject);
            _target = null;
            var table = BattleObjects.LoadTable("rudniki");
            _host = new GameObject("retained rock meshes");
            _objects = BattleObjects.Build(_host.transform, table, BattleObjects.LoadMeshes(table), BattleObjects.LoadShapes(table), null, false);
            foreach (var prefix in new[] { "stone_ss_01_", "stone_ss_04_", "stone_ss_05_" })
                foreach (var collider in _objects.Root.GetComponentsInChildren<MeshCollider>())
                    if (collider.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { Rocks.Add(collider); break; }
            if (Rocks.Count != 3) throw new InvalidOperationException("Missing Rudniki rock family");
            _host.SetActive(false);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "rock test ground"; ground.layer = BattleLayers.Static;
            ground.transform.position = new Vector3(1000, 19.5f, 1000);
            ground.transform.localScale = new Vector3(100, 1, 100);
            _case = 0; BeginRock();
        }
        private static void BeginRock()
        {
            if (_tank != null) UnityEngine.Object.DestroyImmediate(_tank.gameObject);
            if (_rock != null) UnityEngine.Object.DestroyImmediate(_rock);
            var source = Rocks[_case % 3];
            _rock = new GameObject(source.name); _rock.layer = BattleLayers.Static;
            _rock.AddComponent<BattleRockSupport>();
            var collider = _rock.AddComponent<MeshCollider>();
            collider.sharedMesh = source.sharedMesh; collider.convex = source.convex;
            _rock.transform.rotation = source.transform.rotation;
            _rock.transform.localScale = source.transform.lossyScale;
            Physics.SyncTransforms();
            // A low edge is driveable; preserve the actual boulder's shape and
            // rotation, while isolating it from unrelated map traffic/slopes.
            _rock.transform.localScale *= 2.5f / collider.bounds.size.y;
            Physics.SyncTransforms();
            _rock.transform.position += new Vector3(1000, 20, 1000)
                - new Vector3(collider.bounds.center.x, collider.bounds.min.y, collider.bounds.center.z);
            Physics.SyncTransforms();
            var position = new Vector3(1000, 20.4f, collider.bounds.min.z - 7);
            _tank = Spawn(_case < 3 ? "t49_rocket" : "maus", position, false);
            _at = Time.time; _stage = 2;
        }
        private static void BeginHighCenter()
        {
            if (_tank != null) UnityEngine.Object.DestroyImmediate(_tank.gameObject);
            if (_rock != null) UnityEngine.Object.DestroyImmediate(_rock);
            if (_case == 0)
            {
                var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ground.layer = BattleLayers.Static;
                ground.transform.position = new Vector3(1000, 19.5f, 1000);
                ground.transform.localScale = new Vector3(100, 1, 100);
            }
            _rock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _rock.name = "stone_ss_high_center_support";
            if (_case != 2) _rock.AddComponent<BattleRockSupport>();
            _rock.layer = BattleLayers.Static;
            _rock.transform.position = new Vector3(1000, 21, 1000);
            _rock.transform.localScale = new Vector3(.8f, 2f, .8f);
            _tank = Spawn(_case == 1 ? "maus" : "t49_rocket", new Vector3(1000, 20, 1000), false);
            var body = _tank.GetComponent<Rigidbody>();
            var hull = _tank.transform.Find("hull_phys_col").GetComponent<Collider>();
            body.position += Vector3.up * (22.05f - hull.bounds.min.y);
            Physics.SyncTransforms();
            if (_case == 4)
            {
                body.position += Vector3.up * 20;
                body.useGravity = false;
                _tank.Suspension.AirControlEnabled = false;
            }
            var centre = body.worldCenterOfMass;
            _rock.transform.position = new Vector3(centre.x, 21, centre.z);
            Physics.SyncTransforms();
            _at = Time.time; _stage = 5;
        }
        private static void Tick()
        {
            if (DateTime.UtcNow > _deadline) { _failures++; Finish(); return; }
            if (!EditorApplication.isPlaying || _tank == null || _stage < 0) return;
            try
            {
                var elapsed = Time.time - _at;
                if (_stage == 1 && (_rocket == null || !_rocket.Alive || elapsed > 9))
                {
                    Check(_target.Health.Health < _hp, "real " + (_case % 2 == 0 ? "MGM-51A" : "MGM-51C")
                        + " roof hit range=" + (_case < 2 ? 100 : 400) + " damage=" + (_hp - _target.Health.Health));
                    if (_rocket != null) UnityEngine.Object.DestroyImmediate(_rocket.gameObject);
                    if (++_case < 4) BeginFlight(); else BeginRocks();
                }
                else if (_stage == 5 && elapsed > 2)
                {
                    Check(_tank.Suspension.WheelHits == 0, "high-centre fixture rests on belly, not tracks: wheels=" + _tank.Suspension.WheelHits);
                    _start = _tank.transform.position;
                    _tank.Blocked = _case == 3; _tank.Drive(-1, 0);
                    _delayChecked = false; _at = Time.time; _stage = 6;
                }
                else if (_stage == 6)
                {
                    _tank.Drive(-1, 0);
                    if (!_delayChecked && elapsed > .5f)
                    {
                        Check(!_tank.Suspension.RockRecoveryActive &&
                            Vector3.ProjectOnPlane(_tank.transform.position - _start, Vector3.up).magnitude < .05f,
                            "no instantaneous recovery or teleport case=" + _case);
                        _delayChecked = true;
                    }
                    if (elapsed < 7) return;
                    var moved = Vector3.ProjectOnPlane(_tank.transform.position - _start, Vector3.up).magnitude;
                    var expectedRecovery = _case < 2;
                    Check(expectedRecovery ? moved > 2 : moved < .25f,
                        _tank.Card.Name + " high-centre case=" + _case + " moved=" + moved + " wheels=" + _tank.Suspension.WheelHits);
                    if (!expectedRecovery) Check(!_tank.Suspension.RockRecoveryActive, "no recovery on unmarked ground / blocked tank / airborne hull case=" + _case);
                    if (++_case < 5) BeginHighCenter(); else Finish();
                }
                else if (_stage == 2 && elapsed > 2)
                { _tank.Blocked = false; _tank.Drive(1, 0); _at = Time.time; _stage = 3; }
                else if (_stage == 3)
                {
                    _tank.Drive(1, 0);
                    if (elapsed < 5) return;
                    _start = _tank.transform.position; _tank.Drive(-1, 0); _at = Time.time; _stage = 4;
                }
                else if (_stage == 4)
                {
                    _tank.Drive(-1, 0);
                    if (elapsed < 5) return;
                    var moved = _start.z - _tank.transform.position.z;
                    Check(moved > 2 && _tank.transform.position.y > 19,
                        _tank.Card.Name + " reverse from " + _rock.name + " moved=" + moved + " y=" + _tank.transform.position.y);
                    if (++_case < 6) BeginRock(); else Finish();
                }
            }
            catch (Exception e) { Debug.LogException(e); Finish(); }
        }
        private static void Finish()
        {
            _stage = -1;
            if (_objects != null) { _objects.Release(); _objects = null; }
            Debug.Log("[Physics regression] finished failures=" + _failures + " errors=" + _errors);
            SessionState.SetInt(Key + ".result", _failures == 0 && _errors == 0 ? 0 : 1);
            EditorApplication.isPlaying = false;
        }
    }
}
