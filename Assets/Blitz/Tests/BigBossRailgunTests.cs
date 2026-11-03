using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    public sealed class BigBossRailgunTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const float Step = 1f / 60f;
        private const float ShellSpeed = 800f;
        private const float Reach = 90f;

        private readonly List<GameObject> _shots = new List<GameObject>();
        private GameObject _root;
        private Camera _camera;
        private BattleSession _session;
        private float _scale;

        [SetUp]
        public void Setup()
        {
            _scale = Time.timeScale;
            Time.timeScale = 1f;
            _root = new GameObject("BigBossRailgunTests");
            var host = new GameObject("Camera");
            host.transform.SetParent(_root.transform);
            _camera = host.AddComponent<Camera>();
            _camera.transform.position = new Vector3(0f, 5f, -14f);
            _camera.transform.LookAt(Vector3.up);
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var shot in _shots) { if (shot != null) { Object.DestroyImmediate(shot); } }
            _shots.Clear();
            if (_session != null) { Object.DestroyImmediate(_session.gameObject); }
            _session = null;
            Object.DestroyImmediate(_root);
            Time.timeScale = _scale;
        }

        [Test]
        public void DischargeLaysItsDavaDownAxisAlongTheShot()
        {
            var shots = new[]
            {
                Vector3.forward, new Vector3(1f, 0.2f, 0.3f), Vector3.up, Vector3.down, new Vector3(-3f, -1f, 0f),
            };
            foreach (var shot in shots)
            {
                var pose = BigBossVfx.RailgunPose(shot);
                Assert.Less((pose * Vector3.back - shot.normalized).magnitude, 1e-4f, shot.ToString());
            }

            Assert.Less((BigBossVfx.RailgunPose(Vector3.zero) * Vector3.back - Vector3.forward).magnitude, 1e-4f);
        }

        [Test]
        public void BeamGrowsFromTheMuzzleToTheShellAndItsBoltsStopWhereItEnds()
        {
            var fx = Railgun();
            var muzzle = new Vector3(3f, 2f, -1f);
            var axis = new Vector3(0.2f, -0.05f, 1f).normalized;
            BigBossVfx.AimRailgun(fx, muzzle, axis, muzzle, false);
            Near(muzzle, fx.transform.position);
            Assert.Less((fx.transform.rotation * Vector3.back - axis).magnitude, 1e-4f);

            var travelled = 0f;
            var ribbons = 0;
            var carriers = 0;
            var regrown = false;
            for (var frame = 0; frame < 90; frame++)
            {
                travelled = Mathf.Min(Reach, travelled + ShellSpeed * Step);
                var end = muzzle + axis * travelled;
                BigBossVfx.StretchRailgun(fx, muzzle, axis, end);
                foreach (var g in Groups(fx))
                {
                    if (!Ribbon(g)) { continue; }
                    ribbons++;
                    var nodes = Stripe(g);
                    Near(muzzle, Position(nodes[0]), "the ribbon starts on the muzzle");
                    Near(end, Position(nodes[nodes.Count - 1]), "the ribbon ends on the shell");
                    if (regrown || travelled > Reach - 30f) { continue; }

                    // The shell moves on: the ribbon keeps the nodes it owns, ages and all.
                    regrown = true;
                    var first = nodes[0];
                    var count = nodes.Count;
                    var age = (float)Field(first, "Age");
                    var further = muzzle + axis * (travelled + 25f);
                    BigBossVfx.StretchRailgun(fx, muzzle, axis, further);
                    Assert.AreSame(first, nodes[0]);
                    Assert.GreaterOrEqual(nodes.Count, count);
                    Assert.AreEqual(age, (float)Field(first, "Age"));
                    Near(further, Position(nodes[nodes.Count - 1]));
                    BigBossVfx.StretchRailgun(fx, muzzle, axis, end);
                    Assert.AreSame(first, nodes[0]);
                    Assert.AreEqual(count, nodes.Count);
                }

                fx.Simulate(Step);
                // The clip runs before a carrier moves: a sliver of a step settles the frame.
                fx.Simulate(1e-5f);
                foreach (var g in Groups(fx))
                {
                    if (!Carrier(g)) { continue; }
                    foreach (var p in Particles(g))
                    {
                        carriers++;
                        Assert.LessOrEqual(Vector3.Dot(Position(p) - muzzle, axis), travelled + 0.05f,
                            "a bolt carrier outran the beam at frame " + frame);
                    }
                }
            }

            Assert.True(regrown, "a ribbon was laid while the beam grew");
            Assert.Greater(ribbons, 0, "the beam ribbons drew");
            Assert.Greater(carriers, 0, "the bolt carriers flew");
        }

        [Test]
        public void OwnScopeDropsTheMuzzleFlashAndStartsTheBeamPastTheBarrel()
        {
            var fx = Railgun();
            var muzzle = new Vector3(0f, 2f, 0f);
            var axis = Vector3.forward;
            var clearance = BigBossVfx.RailgunScopeClearance;
            BigBossVfx.AimRailgun(fx, muzzle, axis, muzzle, true);
            var dropped = 0;
            foreach (var g in Groups(fx))
            {
                if (!Dropped(g)) { continue; }
                dropped++;
                Assert.True((bool)Field(g, "Finished"));
                Assert.AreEqual(0, Particles(g).Count);
            }

            Assert.Greater(dropped, 0, "the scene carries the flash parts the scope drops");

            var travelled = 0f;
            var ribbons = 0;
            var bolts = 0;
            for (var frame = 0; frame < 60; frame++)
            {
                travelled = Mathf.Min(Reach, travelled + ShellSpeed * Step);
                var end = muzzle + axis * travelled;
                BigBossVfx.StretchRailgun(fx, muzzle, axis, end, clearance);
                var from = muzzle + axis * Mathf.Min(clearance, travelled);
                foreach (var g in Groups(fx))
                {
                    if (Dropped(g))
                    {
                        Assert.AreEqual(0, Particles(g).Count, "a dropped part came back");
                        Assert.AreEqual(0, Stripe(g).Count);
                    }

                    if (Ribbon(g))
                    {
                        ribbons++;
                        var nodes = Stripe(g);
                        Near(from, Position(nodes[0]), "the beam starts past the barrel");
                        Near(travelled > clearance ? end : from, Position(nodes[nodes.Count - 1]));
                    }

                    var carrier = Field(g, "Parent");
                    if (carrier == null || (bool)Field(g, "Finished")) { continue; }
                    if (Vector3.Dot(Position(carrier) - muzzle, axis) < clearance)
                    {
                        Assert.AreEqual(0, Particles(g).Count, "no bolt inside the clearance");
                    }
                    else
                    {
                        bolts += Particles(g).Count;
                    }
                }

                fx.Simulate(Step);
            }

            Assert.Greater(ribbons, 0, "the beam still draws in the scope");
            Assert.Greater(bolts, 0, "so do the bolts down range");
        }

        [Test]
        public void ArmedShotLaysTheDischargeAlongItsFlightAndTheScopeHidesTheGunCharge()
        {
            _session = BattleSession.Create(null, false, tankId: "t-34", autoStart: true, mode: BattleMode.BigBoss);
            var tank = _session.Player;
            var vfx = tank.GetComponent<BigBossVfx>();
            var held = (IDictionary)Field(vfx, "_held");
            var beams = (IList)Field(vfx, "_beams");

            Arm(tank);
            Invoke(vfx, "LateUpdate");
            Assert.True(((DavaFx)held["railgun"]).Visible, "the charge shows on the gun");
            _session.View.SetSniperMode(true);
            Invoke(vfx, "LateUpdate");
            Assert.False(((DavaFx)held["railgun"]).Visible, "but not inside the own scope");
            _session.View.SetSniperMode(false);
            Invoke(vfx, "LateUpdate");
            Assert.True(((DavaFx)held["railgun"]).Visible);

            var muzzle = tank.GunShootPoint();
            var aim = tank.Turret.GunPivot.forward;
            var shot = Fire(tank, muzzle, aim);
            Assert.True(shot.BigBossRailgun);
            Assert.AreEqual(1, beams.Count);
            var fx = (DavaFx)Field(beams[0], "Fx");
            var axis = shot.Velocity.normalized;
            Assert.AreSame(_session.transform, fx.transform.parent, "anchored in the world, not on the turret");
            Near(muzzle, fx.transform.position);
            Assert.Less((fx.transform.rotation * Vector3.back - axis).magnitude, 1e-3f);
            Assert.AreEqual(0f, (float)Field(beams[0], "Start"));

            var ribbons = 0;
            for (var frame = 1; frame <= 30; frame++)
            {
                shot.transform.position = muzzle + axis * frame * 3f;
                fx.Simulate(Step);
                Invoke(vfx, "LateUpdate");
                foreach (var g in Groups(fx))
                {
                    if (!Ribbon(g)) { continue; }
                    ribbons++;
                    var nodes = Stripe(g);
                    Near(muzzle, Position(nodes[0]));
                    Near(shot.transform.position, Position(nodes[nodes.Count - 1]), "the beam follows the shell");
                }
            }

            Assert.Greater(ribbons, 0);

            Fire(tank, muzzle, aim);
            Assert.AreEqual(1, beams.Count, "an unarmed shot has no discharge");

            Arm(tank);
            _session.View.SetSniperMode(true);
            Invoke(vfx, "LateUpdate");
            Fire(tank, muzzle, aim);
            Assert.AreEqual(2, beams.Count);
            Assert.AreEqual(BigBossVfx.RailgunScopeClearance, (float)Field(beams[1], "Start"));
            var scoped = 0;
            foreach (var g in Groups((DavaFx)Field(beams[1], "Fx")))
            {
                if (!Dropped(g)) { continue; }
                scoped++;
                Assert.True((bool)Field(g, "Finished"));
            }

            Assert.Greater(scoped, 0, "the own scope drops the muzzle flash");
        }

        private DavaFx Railgun()
        {
            var fx = DavaFx.CreateBigBoss(_root.transform, BigBossVfx.RailgunScene, 2f);
            fx.ViewCamera = _camera;
            return fx;
        }

        private void Arm(BattleTank tank)
        {
            var boss = tank.BigBoss;
            typeof(BigBossTank).GetProperty("State").SetValue(boss, new BigBossState(_session.BigBoss.Rules, 1));
            Invoke(boss, "ApplyModifiers", true);
            boss.Begin();
            boss.State.StartLife(Time.time - 100f);
            Invoke(boss, "AutoAbilities");
            Assert.True(boss.RailgunArmed);
        }

        private BattleProjectile Fire(BattleTank tank, Vector3 muzzle, Vector3 direction)
        {
            var shot = BattleProjectile.Create(tank.CurrentShell(), tank, muzzle, muzzle, direction, false);
            _shots.Add(shot.gameObject);
            return shot;
        }

        private static object Invoke(object instance, string method, params object[] args)
        {
            return instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, args);
        }

        private static object Field(object value, string name)
        {
            return value.GetType().GetField(name, Any).GetValue(value);
        }

        private static List<object> Groups(DavaFx fx)
        {
            var groups = new List<object>();
            foreach (var g in (IEnumerable)Field(fx, "_groups")) { groups.Add(g); }
            return groups;
        }

        private static IList Particles(object group) { return (IList)Field(group, "Particles"); }

        private static IList Stripe(object group) { return (IList)Field(group, "Stripe"); }

        private static Vector3 Position(object node) { return (Vector3)Field(node, "Position"); }

        private static bool Info(object group, string flag) { return (bool)Field(Field(group, "Info"), flag); }

        /// <summary>A laid beam ribbon: a stripe layer with its carrier particle and nodes.</summary>
        private static bool Ribbon(object group)
        {
            return Info(group, "Stripe") && Particles(group).Count > 0 && Stripe(group).Count > 0;
        }

        /// <summary>A top-level bolt carrier: a super emitter that flies down the shot.</summary>
        private static bool Carrier(object group)
        {
            return Info(group, "Super") && Field(group, "Parent") == null;
        }

        /// <summary>A top-level part the shooter's own scope drops.</summary>
        private static bool Dropped(object group)
        {
            if (Field(group, "Parent") != null) { return false; }
            var emitter = ((JsonNode)Field(group, "Emitter"))["name"].AsString();
            var layer = ((JsonNode)Field(group, "Layer"))["name"].AsString();
            return Array.IndexOf(BigBossVfx.RailgunScopeDrops, emitter) >= 0
                || Array.IndexOf(BigBossVfx.RailgunScopeDrops, layer) >= 0;
        }

        private static void Near(Vector3 expected, Vector3 actual, string message = null)
        {
            Assert.Less(Vector3.Distance(expected, actual), 1e-3f, (message ?? string.Empty) + " " + expected + " vs " + actual);
        }
    }
}
