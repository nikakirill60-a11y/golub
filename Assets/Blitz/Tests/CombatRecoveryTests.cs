using System.Reflection;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    public sealed class CombatRecoveryTests
    {
        private GameObject _root;
        private BattleRoster _roster;
        private float _now;
        private bool _stationary;
        private static readonly Vector3 Origin = new Vector3(18000f, 20f, 0f);

        [SetUp]
        public void SetUp()
        {
            _now = 100f;
            BattleGun.Clock = () => _now;
            _stationary = AiTankController.BotsStationary;
            AiTankController.BotsStationary = false;
            _root = new GameObject("CombatRecoveryTests");
            // Other fixtures may leave a completed roster in the static slot.
            // Burst scheduling must be exercised in an explicitly ongoing battle.
            _roster = BattleRoster.Begin(null, 0);
            _roster.StartBattle();
        }

        [TearDown]
        public void TearDown()
        {
            BattleGun.Clock = null;
            _roster?.End();
            AiTankController.BotsStationary = _stationary;
            foreach (var projectile in Object.FindObjectsByType<BattleProjectile>(FindObjectsSortMode.None))
                if (projectile.transform.position.x > Origin.x - 100f)
                    Object.DestroyImmediate(projectile.gameObject);
            Object.DestroyImmediate(_root);
        }

        private BattleTank Spawn(string id = "ms-1")
        {
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform);
            spawn.position = Origin;
            var tank = BattleTank.Spawn(_root.transform, "Tank", database.Get(id), spawn,
                new Bounds(Origin, new Vector3(1000f, 100f, 1000f)), 0);
            tank.Blocked = false;
            return tank;
        }

        [TestCase("object_907")]
        [TestCase("ms-1")]
        [TestCase("pzii")]
        [TestCase("f119_projet_murat")]
        [TestCase("s16_kranvagn")]
        public void LastRoundStopsAllLoadingWithoutFalseCompletion(string id)
        {
            var tank = Spawn(id);
            tank.SetShellAmounts(new[] { 1, 0, 0 });
            var completed = 0;
            var shots = 0;
            tank.Gun.Reloaded += () => completed++;
            tank.ProjectileFired += (first, slot) => shots++;
            tank.Gun.ShootAttempt();
            tank.Gun.Tick();
            _now += 100f;
            tank.Gun.Tick();
            Assert.AreEqual(1, shots);
            Assert.AreEqual(0, tank.AvailableShellCount);
            Assert.AreEqual(0f, tank.Gun.ReloadRemaining());
            Assert.IsFalse(tank.Gun.CanShoot());
            Assert.AreEqual(0, completed, "no empty reload-complete cue");
            if (tank.Gun is BattleGunBurst burst)
            {
                burst.ForceMainReload(true);
                Assert.IsFalse(burst.Bursting);
                Assert.IsEmpty(burst.Bursts);
                Assert.AreEqual(0f, burst.NextShellSeconds);
            }
        }

        [Test]
        public void ExhaustionCyclesToStockedTypeAndKeepsFiredSlotNotification()
        {
            var tank = Spawn();
            tank.SetShellAmounts(new[] { 1, 2, 0 });
            var fired = -1;
            tank.PrimaryProjectileFired += slot => fired = slot;
            tank.Gun.ShootAttempt();
            Assert.AreEqual(0, fired);
            Assert.AreEqual(1, tank.ShellId);
            Assert.Greater(tank.Gun.ReloadRemaining(), 0f);
            _now = tank.Gun.NextShoot + 0.01f;
            tank.Gun.Tick();
            Assert.IsTrue(tank.Gun.CanShoot());
            tank.Gun.ShootAttempt();
            Assert.AreEqual(1, fired);
            Assert.AreEqual(1, tank.AvailableShellCount);
        }

        [Test]
        public void StockSelectionWrapsAndIgnoresBattleLocks()
        {
            var tank = Spawn();
            tank.SelectShell(1);
            tank.Blocked = true;
            tank.SetShellAmounts(new[] { 2, 0, 0 });
            Assert.AreEqual(0, tank.ShellId);
            Assert.IsFalse(tank.Gun.CanShoot());
            tank.Blocked = false;
            Assert.IsTrue(tank.Gun.CanShoot());
            tank.SetShellAmounts(null);
            Assert.AreEqual(int.MaxValue, tank.AvailableShellCount);
        }

        [Test]
        public void EmptyRackCancelsPendingReloadWithoutSignal()
        {
            var tank = Spawn();
            tank.SetShellAmounts(new[] { 2, 0, 0 });
            tank.Gun.Reload(10f);
            var completed = 0;
            tank.Gun.Reloaded += () => completed++;
            tank.SetShellAmounts(new[] { 0, 0, 0 });
            _now += 20f;
            tank.Gun.Tick();
            Assert.AreEqual(0, completed);
            Assert.AreEqual(0f, tank.Gun.ReloadRemaining());
        }

        [TestCase("ms-1")]
        [TestCase("f119_projet_murat")]
        public void AdrenalinePreservesMainReloadProgressOnStartAndStop(string id)
        {
            var tank = Spawn(id);
            tank.SetShellAmounts(new[] { 20, 0, 0 });
            var active = tank.AddConsumable(ConsumableCatalog.Berserk);
            var tuning = tank.GunInfo.Reload;
            if (tank.Gun is BattleGunBurst burst) burst.ForceMainReload(true);
            else tank.Gun.Reload(80f);
            var remaining = tank.Gun.NextShoot - _now;
            Assert.AreEqual(ConsumableUse.Used, active.UseTry(_now, false));
            Assert.That(tank.Gun.NextShoot - _now, Is.EqualTo(remaining * 0.75f).Within(0.001f));
            Assert.AreEqual(tuning, tank.GunInfo.Reload);
            // Stop can happen at any phase boundary; the completed portion stays completed.
            _now += remaining * 0.1f;
            remaining = tank.Gun.NextShoot - _now;
            active.Stop();
            Assert.That(tank.Gun.NextShoot - _now, Is.EqualTo(remaining / 0.75f).Within(0.001f));
            var completed = 0;
            tank.Gun.Reloaded += () => completed++;
            _now = tank.Gun.NextShoot + 0.01f;
            tank.Gun.Tick();
            tank.Gun.Tick();
            Assert.IsTrue(tank.Gun.CanShoot());
            Assert.AreEqual(1, completed);
        }

        [TestCase(-0.001f)]
        [TestCase(0f)]
        [TestCase(0.001f)]
        public void AdrenalineAtReloadBoundaryNeverLosesCompletion(float offset)
        {
            var tank = Spawn();
            tank.Gun.Reload(10f);
            _now = tank.Gun.NextShoot + offset;
            var active = tank.AddConsumable(ConsumableCatalog.Berserk);
            active.UseTry(_now, false);
            active.Stop();
            _now += 0.1f;
            var completed = 0;
            tank.Gun.Reloaded += () => completed++;
            tank.Gun.Tick();
            tank.Gun.Tick();
            Assert.IsTrue(tank.Gun.CanShoot());
            Assert.AreEqual(1, completed);
        }

        [Test]
        public void AutoreloaderAdrenalineChangesLoadingButNotFiringGap()
        {
            var tank = Spawn("s16_kranvagn");
            tank.SetShellAmounts(new[] { 20, 0, 0 });
            var gun = (BattleGunBurst)tank.Gun;
            gun.ShootAttempt();
            var firingDeadline = gun.NextShoot;
            var remaining = gun.NextShellSeconds;
            var active = tank.AddConsumable(ConsumableCatalog.Berserk);
            active.UseTry(_now, false);
            Assert.That(gun.NextShellSeconds, Is.EqualTo(remaining * 0.75f).Within(0.001f));
            Assert.AreEqual(firingDeadline, gun.NextShoot);
            active.Stop();
            Assert.That(gun.NextShellSeconds, Is.EqualTo(remaining).Within(0.001f));
            _now += remaining + 0.01f;
            gun.Tick();
            Assert.AreEqual(gun.MagazineSize, gun.Bursts.Count);
        }

        [Test]
        public void AutoreloaderCannotLoadMoreRoundsThanCarried()
        {
            var tank = Spawn("s16_kranvagn");
            tank.SetShellAmounts(new[] { 2, 0, 0 });
            var gun = (BattleGunBurst)tank.Gun;
            Assert.AreEqual(2, gun.Bursts.Count);
            gun.ShootAttempt();
            _now += 200f;
            gun.Tick();
            Assert.AreEqual(1, gun.Bursts.Count);
            Assert.AreEqual(0f, gun.NextShellSeconds);
        }

        private static void Set(AiTankController ai, string field, object value)
        {
            typeof(AiTankController).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(ai, value);
        }

        private AiTankController FacingBot(out BattleTank tank)
        {
            tank = Spawn();
            tank.transform.rotation = Quaternion.identity;
            var ai = AiTankController.Attach(tank, AiSkillLevel.GreenArmy);
            ai.FaceTowards(tank.Pos() + Vector3.right * 50f, 0f);
            return ai;
        }

        [Test]
        public void BlockedPivotRecoversEvenWhenForwardProbeIsClear()
        {
            var ai = FacingBot(out var tank);
            Set(ai, "_stuckSince", Time.time - AiTankController.StuckTime - 0.01f);
            ai.FaceTowards(tank.Pos() + Vector3.right * 50f, 0f);
            Assert.Less(tank.ForwardAxis, 0f, "move clear of the side wall before turning");
            Assert.AreNotEqual(0f, tank.RightAxis);
        }

        [Test]
        public void HealthyPivotResetsStuckTimerOnAngularProgress()
        {
            var ai = FacingBot(out var tank);
            Set(ai, "_stuckSince", Time.time - AiTankController.StuckTime - 0.01f);
            tank.transform.rotation = Quaternion.Euler(0f, 20f, 0f);
            ai.FaceTowards(tank.Pos() + Vector3.right * 50f, 0f);
            Assert.AreEqual(0f, tank.ForwardAxis, "a turning hull is not stuck");
        }

        [Test]
        public void RecoveryChoosesForwardWhenRearIsBlocked()
        {
            var ai = FacingBot(out var tank);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.SetParent(_root.transform);
            wall.layer = BattleLayers.Static;
            wall.transform.position = tank.Pos() - Vector3.forward * (tank.HullLength * 0.6f);
            wall.transform.localScale = new Vector3(20f, 20f, 0.5f);
            Physics.SyncTransforms();
            Set(ai, "_stuckSince", Time.time - AiTankController.StuckTime - 0.01f);
            ai.FaceTowards(tank.Pos() + Vector3.right * 50f, 0f);
            Assert.Greater(tank.ForwardAxis, 0f, "do not reverse into the house behind the tank");
        }

        [Test]
        public void RecoveryDoesNotOverrideHoldPositionOrContinueWhenIdle()
        {
            var ai = FacingBot(out var tank);
            Set(ai, "_stuckSince", Time.time - AiTankController.StuckTime - 0.01f);
            AiTankController.BotsStationary = true;
            ai.FaceTowards(tank.Pos() + Vector3.right * 50f, 0f);
            Assert.AreEqual(0f, tank.ForwardAxis);
            AiTankController.BotsStationary = false;
            ai.FaceTowards(tank.Pos() + Vector3.forward * 50f, 0f);
            Assert.AreEqual(0f, tank.ForwardAxis);
            Assert.AreEqual(0f, tank.RightAxis);
        }
    }
}
