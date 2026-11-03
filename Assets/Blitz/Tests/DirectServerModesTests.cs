using System.Collections.Generic;
using System.Reflection;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Backend checks of the dedicated server: a battle seated exactly as
    /// <c>DirectServer.BeginBattle</c> does (two remote humans, no local
    /// player) in every mode, driven only through <see cref="BattleRemoteDriver"/>
    /// — the path a client's input takes on the server. Every Uprising active
    /// and every purchasable upgrade is bought/used that way and its effect is
    /// checked on the authority.
    /// </summary>
    public sealed class DirectServerModesTests
    {
        private const string Tank = "ms-1";
        private const int AnchorOwner = 1;
        private const int GuestOwner = 2;

        private BattleSession _session;
        private float _timeScale;
        private byte _sequence;

        [SetUp] public void Before() { _timeScale = Time.timeScale; Time.timeScale = 1f; BattleNetHooks.PuppetWorld = false; _sequence = 0; }

        [TearDown]
        public void After()
        {
            if (_session != null) { Object.DestroyImmediate(_session.gameObject); }
            _session = null;
            Time.timeScale = _timeScale;
        }

        /// <summary>Big Boss seats tier V-X vehicles only.</summary>
        private static string TankFor(BattleMode mode) { return mode == BattleMode.BigBoss ? "t-34" : Tank; }

        /// <summary>The hangar consumables every remote crew brings in its hello.</summary>
        private static readonly string[] Loadout = { ConsumableCatalog.RepairKit, ConsumableCatalog.Afterburning, ConsumableCatalog.Berserk };

        private static BattleNetHello Hello(string name, string tankId)
        {
            var hello = new BattleNetHello { Build = "test", Name = name, TankId = tankId };
            hello.Consumables.AddRange(Loadout);
            return hello;
        }

        private static List<string> Ids(BattleTank tank)
        {
            var ids = new List<string>();
            foreach (var item in tank.ActiveConsumables) { ids.Add(item.Info.Id); }
            return ids;
        }

        private void Create(BattleMode mode)
        {
            var tankId = TankFor(mode);
            var net = new BattleNetSetup { Dedicated = true, Mode = mode, Build = "test" };
            net.Guests.Add(new BattleNetSetup.Guest { Owner = AnchorOwner, Seat = "seat-a", Hello = Hello("a", tankId) });
            net.Guests.Add(new BattleNetSetup.Guest { Owner = GuestOwner, Seat = "seat-b", Hello = Hello("b", tankId) });
            Assert.IsTrue(net.PromoteAnchor());
            var steps = BattleSession.CreateSteps(null, false, built => _session = built, BattleWorld.DefaultMapId, tankId,
                true, "a", TankSaveData.WithName(tankId), false, mode, net);
            while (steps.MoveNext()) { }
            Assert.IsNotNull(_session, "dedicated battle built");
            Assert.IsTrue(_session.Dedicated);
            Assert.AreEqual(mode, _session.Mode);
            if (!_session.Started) { _session.StartBattle(); }
            Assert.IsTrue(_session.Started);
        }

        private BattleRemoteDriver Driver(int owner)
        {
            BattleRemoteDriver driver;
            Assert.IsTrue(_session.RemoteDrivers.TryGetValue(owner, out driver), "owner " + owner + " is driven remotely");
            Assert.IsNotNull(driver);
            return driver;
        }

        private static BattleTank TankOf(BattleRemoteDriver driver) { return driver.GetComponent<BattleTank>(); }

        /// <summary>What the server does with one client input carrying a mode action.</summary>
        private void Action(BattleRemoteDriver driver, byte action)
        {
            if (_sequence == 0) { driver.ApplyUprisingAction(0, 0); } // the first input only primes
            unchecked { _sequence++; }
            if (_sequence == 0) { _sequence = 1; }
            driver.ApplyUprisingAction(_sequence, action);
        }

        private static void Step(BattleRemoteDriver driver)
        {
            typeof(BattleRemoteDriver).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, null);
        }

        private BattleTank EnemyOf(BattleTank tank)
        {
            foreach (var enemy in _session.Roster.GetEnemyTanks(tank.Team)) { if (enemy != null && !enemy.Dead) { return enemy; } }
            Assert.Fail("no enemy");
            return null;
        }

        private static void Kill(BattleTank tank)
        {
            for (var i = 0; i < 3 && !tank.Dead; i++)
            {
                tank.ReceiveDamage(new DamageInfo { Dmg = tank.Health.MaxHealth * 10, DmgBase = tank.Health.MaxHealth * 10, DmgType = DamageType.Ability });
            }
            Assert.IsTrue(tank.Dead);
        }

        private static void Revive(UprisingTank c)
        {
            Assert.IsTrue(c.State.Respawn(Time.time + 11, true));
            typeof(UprisingTank).GetMethod("Restore", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(c, null);
            Assert.IsFalse(c.Tank.Dead);
        }

        private static void SetActive(UprisingTank c, string id)
        {
            typeof(UprisingTank).GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(c, new UprisingState(c.State.Catalog, id));
        }

        private static void SetField(object target, string name, object value)
        {
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        }

        // ------------------------------------------------------------ every mode

        [TestCase(BattleMode.Normal)]
        [TestCase(BattleMode.Gravity)]
        [TestCase(BattleMode.Uprising)]
        [TestCase(BattleMode.BigBoss)]
        public void DedicatedBattleSeatsBothHumansAsRemoteTanks(BattleMode mode)
        {
            Create(mode);
            var anchor = Driver(AnchorOwner);
            var guest = Driver(GuestOwner);
            Assert.AreSame(_session.Player, TankOf(anchor), "the anchor slot is a remote human, not a local player");
            Assert.AreNotSame(TankOf(anchor), TankOf(guest));
            Assert.IsNull(_session.Player.View, "the server camera never aims a player's turret");
            foreach (var driver in new[] { anchor, guest })
            {
                var tank = TankOf(driver);
                Assert.IsFalse(tank.NetPuppet, "the server simulates every tank");
                Assert.IsFalse(tank.Blocked, "released at battle start");
                Assert.AreEqual(mode == BattleMode.Uprising, tank.GetComponent<UprisingTank>() != null);
                Assert.AreEqual(mode == BattleMode.Gravity, tank.GetComponent<GravityModeTank>() != null);
                Assert.AreEqual(mode == BattleMode.BigBoss, tank.BigBoss != null);
                Assert.GreaterOrEqual(_session.SlotOfTank(tank), 0);
            }
        }

        [TestCase(BattleMode.Normal)]
        [TestCase(BattleMode.Gravity)]
        [TestCase(BattleMode.Uprising)]
        [TestCase(BattleMode.BigBoss)]
        public void RemoteInputDrivesTheTankAndAPulseFiresOnce(BattleMode mode)
        {
            Create(mode);
            var driver = Driver(GuestOwner);
            var tank = TankOf(driver);
            driver.Forward = 1f;
            driver.Right = -1f;
            Step(driver);
            Assert.AreEqual(1f, tank.ForwardAxis, 1e-4f);
            Assert.AreEqual(-1f, tank.RightAxis, 1e-4f);

            var shots = 0;
            tank.ProjectileFired += (first, shell) => shots++;
            driver.FirePulse = true;
            Step(driver);
            Assert.AreEqual(1, shots, "a tap whose packet was lost still fires");
            Assert.IsFalse(driver.FirePulse, "consumed");
            Step(driver);
            Assert.AreEqual(1, shots, "and only once");

            driver.Clear();
            Step(driver);
            Assert.AreEqual(0f, tank.ForwardAxis, 1e-4f);
        }

        [TestCase(BattleMode.Normal)]
        [TestCase(BattleMode.Gravity)]
        [TestCase(BattleMode.Uprising)]
        [TestCase(BattleMode.BigBoss)]
        public void ServerReportsBouncesAndPenetrationsForTheClients(BattleMode mode)
        {
            Create(mode);
            var victim = TankOf(Driver(GuestOwner));
            var attacker = EnemyOf(victim);
            var heard = new List<DamageInfo>();
            System.Action<BattleTank, DamageInfo> listen = (tank, info) => { if (tank == victim) { heard.Add(info); } };
            BattleNetHooks.DamageTaken += listen;
            try
            {
                victim.ReceiveDamage(new DamageInfo { AttackerTank = attacker, Dmg = 0, DmgBase = 150, DmgType = DamageType.Shell, ShotId = 1 });
                victim.ReceiveDamage(new DamageInfo { AttackerTank = attacker, Dmg = 30, DmgBase = 30, DmgType = DamageType.Shell, ShotId = 2 });
            }
            finally { BattleNetHooks.DamageTaken -= listen; }
            Assert.AreEqual(2, heard.Count);
            Assert.IsTrue(heard[0].IsBlock(), "bounce");
            Assert.IsFalse(heard[1].IsBlock(), "penetration");
            Assert.AreSame(attacker, heard[1].AttackerTank);
        }

        [Test]
        public void PuppetPlaysTheServersHitButSimulatedTanksIgnoreIt()
        {
            Create(BattleMode.Normal);
            var tank = TankOf(Driver(GuestOwner));
            var played = 0;
            tank.NetDamageFeedback += info => played++;
            var hp = tank.Health.Health;
            tank.ApplyNetDamageFeedback(new DamageInfo { Dmg = 50, DmgBase = 50, DmgType = DamageType.Shell });
            Assert.AreEqual(0, played, "only a client puppet replays the verdict");
            Assert.AreEqual(hp, tank.Health.Health, "feedback never moves health");
        }

        // ------------------------------------------------------------ gravity

        [Test]
        public void GravityThrusterFiresOnTheServerFromARemoteRequest()
        {
            Create(BattleMode.Gravity);
            var driver = Driver(GuestOwner);
            var gravity = TankOf(driver).GetComponent<GravityModeTank>();
            Assert.IsNotNull(gravity.JetJump, "light tank has the thruster");
            Assert.IsTrue(gravity.JetJumpReady());
            Action(driver, BattleRemoteDriver.JetJumpAction);
            Assert.IsFalse(gravity.JetJumpReady(), "used: the server's cooldown runs");
            var anchor = Driver(AnchorOwner).GetComponent<GravityModeTank>();
            Assert.IsTrue(anchor.JetJumpReady(), "nobody else's thruster fired");
        }

        // ------------------------------------------------------------ big boss

        /// <summary>Every family of the seat off cooldown (the start cooldowns included).</summary>
        private static void ReadyAll(BigBossTank boss)
        {
            ((System.Collections.IDictionary)typeof(BigBossState).GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(boss.State)).Clear();
        }

        [Test]
        public void BigBossRoleAbilityFiresOnTheServerFromARemoteRequest()
        {
            Create(BattleMode.BigBoss);
            var driver = Driver(GuestOwner);
            var boss = TankOf(driver).BigBoss;
            var anchor = TankOf(Driver(AnchorOwner)).BigBoss;
            Assert.IsNotNull(boss);
            Assert.IsNotNull(anchor);
            ReadyAll(boss);
            ReadyAll(anchor);
            var id = boss.ManualAbility;
            Assert.IsNotNull(id, "every role has a button ability");
            Action(driver, BattleRemoteDriver.BigBossAbilityAction);
            Assert.Greater(boss.State.Remaining(id, Time.time), 0, "the server spent the cooldown");
            Assert.AreEqual(0, anchor.State.Remaining(anchor.ManualAbility, Time.time), "nobody else's ability fired");
        }

        // ------------------------------------------------------------ consumables

        [TestCase(BattleMode.Normal)]
        [TestCase(BattleMode.Gravity)]
        [TestCase(BattleMode.Uprising)]
        [TestCase(BattleMode.BigBoss)]
        public void RemoteHangarConsumablesAreSpentByTheServerForALiveCrewOnly(BattleMode mode)
        {
            Create(mode);
            var driver = Driver(GuestOwner);
            var tank = TankOf(driver);
            CollectionAssert.AreEqual(Loadout, Ids(tank), "the guest's own hangar loadout, in slot order");
            var boost = tank.ActiveConsumables[1];
            var power = tank.Suspension.EnginePower;
            Action(driver, (byte)(BattleRemoteDriver.ConsumableAction + 1));
            Assert.GreaterOrEqual(boost.LastUse, 0f, "spent on the server");
            Assert.Greater(tank.Suspension.EnginePower, power, "the hull runs boosted");
            var adrenaline = tank.ActiveConsumables[2];
            Action(driver, (byte)(BattleRemoteDriver.ConsumableAction + 2));
            Assert.GreaterOrEqual(adrenaline.LastUse, 0f, "the third slot reaches the server too");
            Assert.AreEqual(0.75f, tank.AdrenalineReloadMultiplier, 1e-4f, "the hull loads faster");

            Kill(tank);
            var repair = tank.ActiveConsumables[0];
            Action(driver, BattleRemoteDriver.ConsumableAction);
            Assert.Less(repair.LastUse, 0f, "a wreck spends nothing");
        }

        [Test]
        public void BigBossBotsGetTheSupportPairAndHumansTheirHangarLoadout()
        {
            Create(BattleMode.BigBoss);
            var guest = TankOf(Driver(GuestOwner));
            var bots = 0;
            foreach (var tank in _session.BattleTanks())
            {
                if (tank == null || _session.IsHumanTank(tank)) { continue; }
                bots++;
                CollectionAssert.AreEqual(new[] { ConsumableCatalog.RepairKit, ConsumableCatalog.Afterburning }, Ids(tank), tank.name);
            }
            Assert.Greater(bots, 0);
            CollectionAssert.AreEqual(Loadout, Ids(guest));
            var slot = _session.SlotOfTank(guest);
            CollectionAssert.AreEqual(Loadout, _session.NetPlan.Slots[slot].Consumables, "the plan hands the loadout to every peer");
        }

        // ------------------------------------------------------------ uprising actives

        [TestCase("moonwalk")][TestCase("returnInTime")][TestCase("pretendDead")][TestCase("vortex")]
        [TestCase("berserk")][TestCase("huntersMark")][TestCase("curse")]
        public void EveryActiveWorksThroughTheRemoteInput(string id)
        {
            Create(BattleMode.Uprising);
            foreach (var owner in new[] { AnchorOwner, GuestOwner })
            {
                _sequence = 0;
                var driver = Driver(owner);
                var tank = TankOf(driver);
                var c = tank.GetComponent<UprisingTank>();
                SetActive(c, id);
                var enemy = EnemyOf(tank);
                enemy.SetSpotted(true);
                typeof(BattleTank).GetProperty("GunTraceTank").SetValue(tank, enemy);
                if (id == "returnInTime") { typeof(UprisingTank).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(c, null); }

                Action(driver, 1);
                Assert.Greater(c.State.ReadyAt, Time.time, id + " used by owner " + owner);
                var readyAt = c.State.ReadyAt;
                Action(driver, 1);
                Assert.AreEqual(readyAt, c.State.ReadyAt, "cooldown rejects a second press");
                if (id == "huntersMark" || id == "curse") { Assert.IsTrue(enemy.GetComponent<UprisingTank>().HasStatus(id)); }
                if (id == "pretendDead") { Assert.IsTrue(c.State.ShieldArmed); }
                if (id == "berserk" || id == "moonwalk" || id == "pretendDead") { Assert.IsTrue(c.State.Running(Time.time)); }
                if (c.Rewinding) { typeof(UprisingTank).GetMethod("CancelRewind", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(c, null); }
            }
        }

        [Test]
        public void ARepeatedActionSequenceIsNotReplayed()
        {
            Create(BattleMode.Uprising);
            var driver = Driver(GuestOwner);
            var c = TankOf(driver).GetComponent<UprisingTank>();
            SetActive(c, "berserk");
            driver.ApplyUprisingAction(7, 1); // priming input after a (re)join: never executes
            Assert.IsFalse(c.State.Running(Time.time));
            driver.ApplyUprisingAction(7, 1); // the same request again (resent input)
            Assert.IsFalse(c.State.Running(Time.time));
            driver.ApplyUprisingAction(8, 1);
            Assert.IsTrue(c.State.Running(Time.time));
        }

        // ------------------------------------------------------------ uprising upgrades

        private static readonly string[] Upgrades =
        {
            "damageBoost1", "damageBoost2", "reloadBoost1", "reloadBoost2", "engineBoost1", "engineBoost2",
            "ramming1", "ramming2", "comeBackKit", "ghost", "duplet", "splash", "lightning1", "lightning2",
        };

        [Test]
        public void TheUpgradeListCoversEveryWiredCatalogUpgrade()
        {
            Create(BattleMode.Uprising);
            var catalog = _session.Player.GetComponent<UprisingTank>().State.Catalog;
            var wired = new List<string>();
            foreach (var a in catalog.Abilities)
            {
                if (a["kind"].AsInt() == 1 && a["wired"].AsBool()) { wired.Add(a["id"].AsString()); }
            }
            CollectionAssert.AreEquivalent(wired, Upgrades);
        }

        /// <summary>Death on the server, the offer bought through the client's input, respawn.</summary>
        private UprisingTank BuyThroughRemote(string id, out BattleRemoteDriver driver)
        {
            Create(BattleMode.Uprising);
            driver = Driver(GuestOwner);
            var tank = TankOf(driver);
            var c = tank.GetComponent<UprisingTank>();
            Kill(tank);
            Assert.IsTrue(c.State.Respawning);
            Assert.AreEqual(0, c.State.Pending.Count, "the server never buys for a remote human");
            c.State.Offer[0] = id;
            c.State.AddElixir(5000);
            var elixir = c.State.Elixir;
            Action(driver, 2);
            Assert.IsTrue(c.State.Pending.ContainsKey(id), id + " bought from the remote input");
            Assert.AreEqual(elixir - c.State.Catalog.Cost(id), c.State.Elixir, "paid");
            Revive(c);
            Assert.IsTrue(c.State.Has(id), id + " kept after the respawn");
            return c;
        }

        [Test]
        public void ARemoteHumanCanUndoAPurchaseBeforeTheRespawn()
        {
            Create(BattleMode.Uprising);
            var driver = Driver(GuestOwner);
            var c = TankOf(driver).GetComponent<UprisingTank>();
            Kill(c.Tank);
            c.State.Offer[0] = "damageBoost1";
            c.State.AddElixir(500);
            var elixir = c.State.Elixir;
            Action(driver, 2);
            Action(driver, 2);
            Assert.IsFalse(c.State.Pending.ContainsKey("damageBoost1"));
            Assert.AreEqual(elixir, c.State.Elixir, "refunded");
        }

        [TestCase("damageBoost1", 1.2f)][TestCase("damageBoost2", 1.4f)]
        public void DamageUpgradeRaisesOutgoingShellDamage(string id, float factor)
        {
            BattleRemoteDriver driver;
            var c = BuyThroughRemote(id, out driver);
            Assert.AreEqual(factor, c.State.Outgoing(DamageType.Shell, Time.time), 1e-4f);
            var enemy = EnemyOf(c.Tank);
            var hp = enemy.Health.Health;
            enemy.ReceiveDamage(new DamageInfo { AttackerTank = c.Tank, Dmg = 50, DmgBase = 50, DmgType = DamageType.Shell, ShotId = 5 });
            Assert.AreEqual(hp - Mathf.RoundToInt(50 * factor), enemy.Health.Health);
        }

        [TestCase("reloadBoost1", 0.8f)][TestCase("reloadBoost2", 0.65f)]
        public void ReloadUpgradeShortensTheReload(string id, float factor)
        {
            BattleRemoteDriver driver;
            var c = BuyThroughRemote(id, out driver);
            Assert.AreEqual(factor, c.State.ReloadMultiplier(Time.time), 1e-4f);
        }

        [TestCase("engineBoost1", 10)][TestCase("engineBoost2", 20)]
        public void EngineUpgradeRaisesTopSpeed(string id, int add)
        {
            BattleRemoteDriver driver;
            var c = BuyThroughRemote(id, out driver);
            // Fresh respawn: the 10 s cloak's speed bonus is on as well.
            var expected = (int)(c.BaseForwardSpeed * 1.4f) + add;
            Assert.AreEqual(expected, c.Tank.Suspension.MaxSpeedForward, 1e-3f);
        }

        [TestCase("ramming1", 1.6f)][TestCase("ramming2", 2.1f)]
        public void RammingUpgradeRaisesRamDamage(string id, float factor)
        {
            BattleRemoteDriver driver;
            var c = BuyThroughRemote(id, out driver);
            Assert.AreEqual(factor, c.State.Outgoing(DamageType.Collision, Time.time), 1e-4f);
        }

        [Test]
        public void ComeBackKitSurvivesOneLethalHit()
        {
            BattleRemoteDriver driver;
            var c = BuyThroughRemote("comeBackKit", out driver);
            var enemy = EnemyOf(c.Tank);
            c.Tank.ReceiveDamage(new DamageInfo { AttackerTank = enemy, Dmg = 99999, DmgBase = 99999, DmgType = DamageType.Shell, ShotId = 3 });
            Assert.IsFalse(c.Tank.Dead);
            Assert.AreEqual(1, c.Tank.Health.Health);
            c.Tank.ReceiveDamage(new DamageInfo { AttackerTank = enemy, Dmg = 99999, DmgBase = 99999, DmgType = DamageType.Shell, ShotId = 4 });
            Assert.IsTrue(c.Tank.Dead, "only once");
        }

        [Test]
        public void GhostIsOwnedForTheSpotter()
        {
            BattleRemoteDriver driver;
            var c = BuyThroughRemote("ghost", out driver);
            Assert.IsTrue(c.State.Has("ghost"));
        }

        [Test]
        public void DupletQueuesAnEchoForEveryPrimaryShot()
        {
            BattleRemoteDriver driver;
            var c = BuyThroughRemote("duplet", out driver);
            typeof(UprisingTank).GetMethod("OnPrimaryShot", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(c, new object[] { 0 });
            Assert.AreEqual(1, c.Tank.PendingDupletShots);
        }

        [Test]
        public void SplashHitsEnemiesAroundTheImpact()
        {
            BattleRemoteDriver driver;
            var c = BuyThroughRemote("splash", out driver);
            SetField(c, "_splashAt", 0f);
            var enemy = EnemyOf(c.Tank);
            var hp = enemy.Health.Health;
            c.Impact(enemy.transform.position, c.State.LifeSerial, 40);
            Assert.AreEqual(hp - 40, enemy.Health.Health);
            hp = enemy.Health.Health;
            c.Impact(enemy.transform.position, c.State.LifeSerial, 40);
            Assert.AreEqual(hp, enemy.Health.Health, "30 s cooldown");
        }

        [TestCase("lightning1", 0.25f)][TestCase("lightning2", 0.45f)]
        public void ChainLightningStrikesThePenetratedEnemy(string id, float factor)
        {
            BattleRemoteDriver driver;
            var c = BuyThroughRemote(id, out driver);
            SetField(c, "_chainAt", 0f);
            var enemy = EnemyOf(c.Tank);
            var hp = enemy.Health.Health;
            c.Penetration(enemy, c.State.LifeSerial, 100);
            Assert.AreEqual(hp - Mathf.RoundToInt(100 * factor), enemy.Health.Health);
        }
    }
}
