using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The magazine slice: <c>gun_gomponent_burst.gd</c> and the fan of rounds
    /// in <c>ui_gun_burst.tscn</c>. Every clip gun in the port used to fire
    /// like a single-shot one, so these pin the clip, the two intra delays and
    /// the indicator that shows them.
    /// </summary>
    public sealed class BattleBurstTests
    {
        private const float Tolerance = 1e-4f;

        /// <summary>Pz.Kpfw. II: BURST, four bursts of two, 0.133 s apart.</summary>
        private const string BurstTankId = "pzii";

        private GameObject _root;
        private float _now;

        [SetUp]
        public void SetUp()
        {
            _now = 100f;
            BattleGun.Clock = () => _now;
        }

        [TearDown]
        public void TearDown()
        {
            BattleGun.Clock = null;
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        [Test]
        public void FanGeometryAndTints_MatchTheSourceScene()
        {
            // update_bursts_pos(): the fan is centred, so four rounds sit at
            // +-1.5 and +-0.5 steps around the crosshair.
            var step = BurstUiTuning.AngleStep;
            Assert.AreEqual(
                1.5f * step, BurstUiTuning.Rotation(0, 4, step), Tolerance);
            Assert.AreEqual(
                -1.5f * step, BurstUiTuning.Rotation(3, 4, step), Tolerance);
            Assert.AreEqual(
                0f,
                BurstUiTuning.Rotation(0, 4, step) + BurstUiTuning.Rotation(3, 4, step),
                Tolerance);
            Assert.AreEqual(2f * step, BurstUiTuning.AngleStepSniper, Tolerance);

            // update_bursts(): red to green, capped at 0.75 of the way and
            // brightened by 1.5; Godot clamps the channels when it paints them.
            float red;
            float green;
            float blue;
            BurstUiTuning.Tint(4, 4, out red, out green, out blue);
            Assert.AreEqual(0.375f, red, Tolerance);
            Assert.AreEqual(1f, green, Tolerance);
            Assert.AreEqual(0f, blue, Tolerance);

            BurstUiTuning.Tint(1, 4, out red, out green, out blue);
            Assert.AreEqual(1f, red, Tolerance);
            Assert.AreEqual(0.375f, green, Tolerance);

            Assert.IsTrue(BurstUiTuning.Loaded(0, 1));
            Assert.IsFalse(BurstUiTuning.Loaded(1, 1));
        }

        [Test]
        public void BurstGun_FiresOneClipRoundPerTriggerAtTheIntraBurstCadence()
        {
            var tank = SpawnBurstTank();
            var info = tank.GunInfo;
            var gun = TestMagazine(tank);

            Assert.AreEqual(4, gun.Bursts.Count, "the clip is not loaded");
            Assert.AreEqual(info.Burst.Bursts.Count, gun.MagazineSize);

            var shots = 0;
            var firstShots = 0;
            gun.Shoot += first =>
            {
                shots++;
                if (first) { firstShots++; }
            };

            gun.ShootAttempt();
            Assert.IsTrue(gun.Bursting, "the trigger did not start a burst");
            Assert.AreEqual(3, gun.Bursts.Count, "the burst did not leave the clip");
            Assert.IsFalse(gun.CanShoot(), "a live burst has to block the trigger");
            Assert.AreEqual(info.Burst.IntraBurst, gun.ReloadRemaining(), Tolerance, "a live burst must not display an already complete reload");
            Assert.AreEqual(0, shots, "a burst may not fire before its deadline");

            gun.Tick();
            Assert.AreEqual(1, shots, "the first shell of the burst is late");
            Assert.IsTrue(gun.Bursting);
            Assert.AreEqual(info.Burst.IntraBurst, gun.ReloadTotal, Tolerance);
            Assert.AreEqual(1f, gun.ReloadCoefficient(), Tolerance);
            _now += info.Burst.IntraBurst * 0.5f;
            Assert.AreEqual(0.5f, gun.ReloadCoefficient(), Tolerance);
            _now -= info.Burst.IntraBurst * 0.5f;

            // A frame never lands exactly on the deadline, and single-precision
            // rounding makes that boundary meaningless anyway.
            _now += info.Burst.IntraBurst + 0.001f;
            gun.Tick();
            Assert.AreEqual(2, shots, "the second shell of the burst is late");
            Assert.AreEqual(1, firstShots, "only the opening shell is a first shot");
            Assert.IsFalse(gun.Bursting, "the burst never ended");

            // _finish_burst reloads the clip's own gap, not the main reload.
            Assert.AreEqual(
                _now + info.Burst.IntraClip, gun.NextShoot, Tolerance);
            Assert.AreEqual(info.Burst.IntraClip, gun.ReloadTotal, Tolerance);

            _now += info.Burst.IntraClip + 0.01f;
            Assert.IsTrue(gun.CanShoot(), "the next clip round never came up");
        }

        [Test]
        public void EmptyClip_RunsTheMainReloadAndLoadsAFreshOne()
        {
            var tank = SpawnBurstTank();
            var info = tank.GunInfo;
            var gun = TestMagazine(tank);

            var seen = -1;
            gun.BurstsChanged += bursts => seen = bursts;

            for (var i = 0; i < info.Burst.Bursts.Count; i++)
            {
                FireOneBurst(gun, info);
            }

            Assert.AreEqual(0, gun.Bursts.Count, "the clip never ran dry");
            Assert.AreEqual(0, seen, "the indicator was not told about the last round");
            Assert.AreEqual(_now + info.Reload, gun.NextShoot, Tolerance);
            Assert.AreEqual(info.Reload, gun.ReloadTotal, Tolerance);

            _now += info.Reload + 0.01f;
            gun.Tick();
            Assert.AreEqual(
                info.Burst.Bursts.Count,
                gun.Bursts.Count,
                "the main reload did not load a fresh clip");
            Assert.AreEqual(info.Burst.Bursts.Count, seen);
            Assert.IsTrue(gun.CanShoot());
        }

        [Test]
        public void ReloadBurstAction_DumpsAPartialClip()
        {
            var tank = SpawnBurstTank();
            var info = tank.GunInfo;
            var gun = TestMagazine(tank);

            FireOneBurst(gun, info);
            _now += info.Burst.IntraClip + 0.01f;

            gun.ForceMainReload();
            Assert.AreEqual(0, gun.Bursts.Count, "the rest of the clip stayed in");
            Assert.AreEqual(_now + info.Reload, gun.NextShoot, Tolerance);

            // A full clip is not worth dumping, and the source says so.
            _now += info.Reload + 0.01f;
            gun.Tick();
            var deadline = gun.NextShoot;
            gun.ForceMainReload();
            Assert.AreEqual(deadline, gun.NextShoot, Tolerance);
            Assert.AreEqual(info.Burst.Bursts.Count, gun.Bursts.Count);
        }

        [Test]
        public void Hud_BuildsTheFanAndFollowsTheClipAndSniperMode()
        {
            var tank = SpawnBurstTank();
            var magazine = tank.Gun as BattleGunBurst;
            Assert.IsNotNull(magazine, "a BURST gun did not get the clip clock");

            var hud = BattleHud.Create(tank, null, true);
            try
            {
                var fan = hud.BurstIndicator;
                Assert.IsNotNull(fan, "the magazine fan was not built");
                Assert.AreEqual(magazine.Bursts.Count, fan.IconCount);
                Assert.AreEqual(magazine.Bursts.Count, fan.BurstsInit);

                var loaded = fan.Icon(0).sprite;
                Assert.IsNotNull(loaded, "the round icon has no art");
                Assert.AreNotEqual(Color.white, fan.Icon(0).color);

                var angle = fan.Icon(0).rectTransform.localEulerAngles.z;
                fan.SniperMode(true);
                Assert.AreEqual(
                    BurstUiTuning.AngleStepSniper, fan.AngleStep, Tolerance);
                Assert.AreNotEqual(
                    angle, fan.Icon(0).rectTransform.localEulerAngles.z);
                fan.SniperMode(false);
                Assert.AreEqual(
                    angle, fan.Icon(0).rectTransform.localEulerAngles.z, Tolerance);

                // force_main_reload empties the clip, and every icon goes to
                // the spent texture in white.
                magazine.ForceMainReload(true);
                for (var i = 0; i < fan.IconCount; i++)
                {
                    Assert.AreEqual(Color.white, fan.Icon(i).color);
                    Assert.AreNotSame(loaded, fan.Icon(i).sprite);
                }

                hud.SetCombatVisible(false);
                Assert.IsFalse(fan.Visible, "the fan outlived the combat HUD");
                hud.SetCombatVisible(true);
                Assert.IsTrue(fan.Visible);

                // health_component.on_death hides it for good.
                tank.Health.ReceiveDamageBasic(100000);
                Assert.IsFalse(fan.Visible, "a wreck still shows its clip");
                Assert.IsFalse(magazine.Bursting);
            }
            finally
            {
                Object.DestroyImmediate(hud.gameObject);
            }
        }

        [TestCase("f119_projet_murat")]
        [TestCase("s18_emil_1951e1")]
        public void MagazineRing_UsesTheActivePhaseIncludingTheLastLoadedRound(string tankId)
        {
            var tank = SpawnBurstTank(tankId);
            var gun = TestMagazine(tank);
            var capacity = gun.MagazineSize;
            Assert.Greater(capacity, 1);
            for (var shot = 0; shot < capacity; shot++)
            {
                FireOneBurst(gun, tank.GunInfo);
                var expected = shot == capacity - 1 ? tank.GunInfo.Reload : tank.GunInfo.Burst.IntraClip;
                Assert.AreEqual(expected, gun.ReloadTotal, Tolerance, "wrong duration after shot " + shot);
                Assert.AreEqual(1f, gun.ReloadCoefficient(), Tolerance, "a fresh phase must start red");
                Assert.IsFalse(gun.CanShoot());
                _now += expected * 0.5f;
                Assert.AreEqual(0.5f, gun.ReloadCoefficient(), Tolerance);
                _now = gun.NextShoot + 0.01f;
                if (shot == capacity - 1)
                {
                    Assert.IsFalse(gun.CanShoot(), "empty drum may not turn green before refill");
                    gun.Tick();
                    Assert.AreEqual(capacity, gun.Bursts.Count);
                }
                Assert.IsTrue(gun.CanShoot());
                Assert.AreEqual(1f, gun.ReloadCoefficient(), Tolerance);
            }
            FireOneBurst(gun, tank.GunInfo);
            _now = gun.NextShoot + 0.01f;
            gun.ForceMainReload();
            Assert.AreEqual(0, gun.Bursts.Count);
            Assert.AreEqual(tank.GunInfo.Reload, gun.ReloadTotal, Tolerance);
            Assert.AreEqual(1f, gun.ReloadCoefficient(), Tolerance);
            Assert.IsFalse(gun.CanShoot());
        }

        [Test]
        public void Autoreloader_EmptyMagazineTracksTheFirstShellThenBecomesReady()
        {
            var tank = SpawnBurstTank("s16_kranvagn");
            var gun = TestMagazine(tank);
            Assert.IsTrue(tank.GunInfo.AutoReload);
            for (var i = 0; i < gun.MagazineSize; i++)
            {
                _now = Mathf.Max(_now, gun.NextShoot) + 0.01f;
                gun.ShootAttempt();
                Assert.IsFalse(gun.CanShoot());
                Assert.AreEqual(1f, gun.ReloadCoefficient(), Tolerance);
            }
            Assert.AreEqual(0, gun.Bursts.Count);
            var duration = tank.GunInfo.AutoReloadTimes[0];
            Assert.AreEqual(duration, gun.ReloadTotal, Tolerance);
            _now += duration * 0.5f;
            Assert.AreEqual(0.5f, gun.ReloadCoefficient(), Tolerance);
            _now += duration * 0.5f + 0.01f;
            Assert.IsFalse(gun.CanShoot());
            gun.Tick();
            Assert.AreEqual(1, gun.Bursts.Count);
            Assert.IsTrue(gun.CanShoot());
            Assert.Greater(gun.NextShellSeconds, 0f, "next shell still loads in the background");
            Assert.AreEqual(1f, gun.ReloadCoefficient(), Tolerance);
            gun.ForceMainReload(true);
            Assert.AreEqual(1, gun.Bursts.Count, "manual reload may not discard an autoreloader shell");
        }

        [TestCase("f119_projet_murat")]
        [TestCase("s16_kranvagn")]
        public void DamagedAmmoRack_ReloadDurationMatchesItsDeadline(string tankId)
        {
            var tank = SpawnBurstTank(tankId);
            var bay = tank.Modules.Get(TankModuleIds.AmmoBay);
            bay.ReceiveDamage(bay.HealthComponent.MaxHealth - 1, 0f);
            Assert.Greater(tank.ReloadPenalty, 1f);
            var gun = TestMagazine(tank);
            if (tank.GunInfo.AutoReload) { gun.ShootAttempt(); }
            else { FireOneBurst(gun, tank.GunInfo); }
            Assert.AreEqual(tank.GunInfo.Burst.IntraClip * tank.ReloadPenalty, gun.ReloadTotal, Tolerance);
            Assert.AreEqual(_now + gun.ReloadTotal, gun.NextShoot, Tolerance);
            Assert.AreEqual(1f, gun.ReloadCoefficient(), Tolerance);
            if (!tank.GunInfo.AutoReload)
            {
                _now = gun.NextShoot + 0.01f;
                gun.ForceMainReload();
                Assert.AreEqual(tank.GunInfo.Reload * tank.ReloadPenalty, gun.ReloadTotal, Tolerance);
                Assert.AreEqual(_now + gun.ReloadTotal, gun.NextShoot, Tolerance);
                Assert.AreEqual(1f, gun.ReloadCoefficient(), Tolerance);
            }
        }

        [Test]
        public void Hud_FreshReloadIsRedAndOnlyReadyGunsAreGreen()
        {
            var tank = SpawnBurstTank("f119_projet_murat");
            var hud = BattleHud.Create(tank, null, true);
            try
            {
                hud.SetReload(2f, 2f, true);
                var readyLabelColour = hud.ReloadLabel.color;
                var green = hud.GetCrosshairDot(1).sprite;
                hud.SetReload(2f, 2f, false);
                var red = hud.GetCrosshairDot(1).sprite;
                Assert.AreNotSame(green, red);
                Assert.AreNotEqual(readyLabelColour, hud.ReloadLabel.color,
                    "equal remaining/total means a fresh reload, not readiness");
                for (var i = 1; i < hud.CrosshairDotCount; i++)
                    Assert.AreSame(red, hud.GetCrosshairDot(i).sprite);
                hud.SetReload(1f, 2f, false);
                Assert.AreNotSame(red, hud.GetCrosshairDot(hud.CrosshairDotCount - 1).sprite);
                Assert.AreNotSame(green, hud.GetCrosshairDot(hud.CrosshairDotCount - 1).sprite);
                hud.SetReload(2f, 2f, true);
                for (var i = 0; i < hud.CrosshairDotCount; i++)
                    Assert.AreSame(green, hud.GetCrosshairDot(i).sprite);
            }
            finally { Object.DestroyImmediate(hud.gameObject); }
        }

        /// <summary>One whole burst: pull the trigger, then run out its shells.</summary>
        private void FireOneBurst(BattleGunBurst gun, GunInfo info)
        {
            _now = Mathf.Max(_now, gun.NextShoot) + 0.01f;
            gun.ShootAttempt();
            Assert.IsTrue(gun.Bursting, "the clip refused the trigger");
            while (gun.Bursting)
            {
                gun.Tick();
                if (gun.Bursting) { _now += Mathf.Max(info.Burst.IntraBurst, 0.01f); }
            }
        }

        /// <summary>
        /// A second magazine on the same card, so the clip can be run without
        /// the hull's own <c>shoot_shell</c> launching projectiles.
        /// </summary>
        private BattleGunBurst TestMagazine(BattleTank tank)
        {
            var host = new GameObject("TestMagazine");
            host.transform.SetParent(tank.transform, false);
            var gun = host.AddComponent<BattleGunBurst>();
            gun.Setup(tank.GunInfo, tank);
            return gun;
        }

        private BattleTank SpawnBurstTank(string tankId = BurstTankId)
        {
            _root = new GameObject("BattleBurstTests");
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);

            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(tankId);
            Assert.IsNotNull(card, tankId + " is missing from tanks.json");

            return BattleTank.Spawn(
                _root.transform,
                "BurstTank",
                card,
                spawn,
                new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)),
                1);
        }
    }
}
