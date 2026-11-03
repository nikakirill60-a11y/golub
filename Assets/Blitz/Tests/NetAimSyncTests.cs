using System.Reflection;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Client aim, arty and ATGM parity with the host: the own turret is
    /// predicted but bounded by the host's angles, and a replayed rocket
    /// flies the host's samples instead of steering on its own.
    /// </summary>
    public sealed class NetAimSyncTests
    {
        private const float Tolerance = 1e-4f;

        [TearDown]
        public void TearDown()
        {
            BattleNetHooks.HostRockets.Clear();
            foreach (var projectile in Object.FindObjectsByType<BattleProjectile>(FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(projectile.gameObject);
            }
        }

        [Test]
        public void PredictedTurretKeepsItsLeadInsideTheLatencyBound()
        {
            var speed = 0.5f;
            var bound = speed * TankTurret.NetLeadSeconds + TankTurret.NetSlackRadians;
            Assert.AreEqual(0.1f, TankTurret.Reconcile(0.1f, 0f, speed), Tolerance);
            Assert.AreEqual(bound, TankTurret.Reconcile(1f, 0f, speed), Tolerance);
            Assert.AreEqual(-bound, TankTurret.Reconcile(-1f, 0f, speed), Tolerance);
        }

        [Test]
        public void ReconcileTakesTheShortWayAroundTheTurret()
        {
            // 179 deg predicted against a host at -179 deg is 2 deg apart, not 358.
            var predicted = 179f * Mathf.Deg2Rad;
            var host = -179f * Mathf.Deg2Rad;
            Assert.AreEqual(predicted, TankTurret.Reconcile(predicted, host, 0.5f), Tolerance);
        }

        private static BattleProjectile Rocket()
        {
            var shell = new ShellInfo { Type = ShellType.He, Atgm = true, Speed = 75f, TurnSpeed = 0.5f, MaxDistance = 720f };
            return BattleProjectile.Create(shell, null, Vector3.zero, Vector3.zero, Vector3.forward, false);
        }

        private static void Step(BattleProjectile projectile)
        {
            typeof(BattleProjectile).GetMethod("FixedUpdate", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(projectile, null);
        }

        [Test]
        public void GuidedRocketsAreRegisteredWhileInFlight()
        {
            var rocket = Rocket();
            CollectionAssert.Contains(BattleProjectile.LiveGuided, rocket);
            // Edit mode runs no OnDestroy: end the flight the way a hit does.
            typeof(BattleProjectile).GetMethod("Despawn", BindingFlags.NonPublic | BindingFlags.Instance,
                null, System.Type.EmptyTypes, null).Invoke(rocket, null);
            CollectionAssert.DoesNotContain(BattleProjectile.LiveGuided, rocket);
        }

        [Test]
        public void ReplayWaitsAtTheMuzzleForTheHostsFirstSample()
        {
            var rocket = Rocket();
            rocket.FollowHost(42);
            Step(rocket);
            Assert.AreEqual(Vector3.zero, rocket.transform.position);
            Assert.IsTrue(rocket.FollowsHost);
        }

        [Test]
        public void ReplayRidesTheHostsPathThenFinishesOnItsOwn()
        {
            var rocket = Rocket();
            rocket.FollowHost(42);
            var position = new Vector3(3f, 40f, 20f);
            var velocity = new Vector3(0f, 10f, 70f);
            BattleNetHooks.HostRockets[42] = new BattleNetHooks.RocketSample { Position = position, Velocity = velocity };
            Step(rocket);
            Assert.AreEqual(position, rocket.transform.position);
            Assert.AreEqual(velocity, rocket.Velocity);

            // The host's rocket is gone: keep its heading, steer no more onto the host.
            BattleNetHooks.HostRockets.Clear();
            Step(rocket);
            Assert.IsFalse(rocket.FollowsHost);
            Assert.Greater(rocket.transform.position.z, position.z);
        }

        [Test]
        public void LocalShellsNeverFollowTheHost()
        {
            var shell = new ShellInfo { Type = ShellType.Ap, Speed = 442f, MaxDistance = 720f };
            var round = BattleProjectile.Create(shell, null, Vector3.zero, Vector3.zero, Vector3.forward, false);
            round.FollowHost(7);
            Assert.IsFalse(round.FollowsHost);
            CollectionAssert.DoesNotContain(BattleProjectile.LiveGuided, round);
        }
    }
}
