using System.Reflection;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The module half of <c>base_tank.gd</c>: the three states of
    /// <c>Module.get_status</c>, the delayed half repair it schedules on a
    /// crit, and the 4.8.0 health the cards now carry for every module and
    /// crew role.
    /// </summary>
    public sealed class BattleTankModulesTests
    {
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        private static TankModule NewModule(int health, bool dead = false)
        {
            return new TankModule(
                TankModuleIds.Tracks, health, () => ModuleTuning.RepairTime, () => dead);
        }

        [Test]
        public void ModuleWalksNormalDamagedDestroyedLikeTheSource()
        {
            var module = NewModule(100);
            Assert.AreEqual(TankModule.Status.Normal, module.GetStatus());

            // health < max_health * 0.5 is the source's damaged edge.
            module.ReceiveDamage(60, 0f);
            Assert.AreEqual(TankModule.Status.Damaged, module.GetStatus());

            module.ReceiveDamage(40, 0f);
            Assert.AreEqual(TankModule.Status.Destroyed, module.GetStatus());
        }

        [Test]
        public void CritSchedulesTheHalfRepairAndRestoresTheRegenHealth()
        {
            var module = NewModule(100);
            module.RegenHealth = 40f;

            var crits = 0;
            var repairs = 0;
            module.OnCrit += id => crits++;
            module.OnRepaired += id => repairs++;

            module.ReceiveDamage(100, 0f);
            Assert.AreEqual(1, crits);
            Assert.IsTrue(module.RepairPending);

            // Nothing happens before get_repair_time() has passed.
            module.Tick(ModuleTuning.RepairTime - 0.1f);
            Assert.AreEqual(TankModule.Status.Destroyed, module.GetStatus());

            module.Tick(ModuleTuning.RepairTime + 0.1f);
            Assert.AreEqual(1, repairs);
            Assert.IsFalse(module.RepairPending);
            Assert.AreEqual(40, module.HealthComponent.Health);
            Assert.AreEqual(TankModule.Status.Damaged, module.GetStatus());
        }

        [Test]
        public void RepairEverythingFillsThePoolAndRefusesOnAnIntactModule()
        {
            var set = new TankModuleSet();
            var module = set.Register(NewModule(100));
            Assert.IsFalse(set.RepairEverything(), "an intact module cannot be repaired");

            module.ReceiveDamage(70, 0f);
            Assert.IsTrue(set.RepairEverything());
            Assert.AreEqual(100, module.HealthComponent.Health);
            Assert.AreEqual(TankModule.Status.Normal, set.StatusOf(TankModuleIds.Tracks));
        }

        [Test]
        public void ToolboxCutsAQuarterOffTheRepairTime()
        {
            _root = new GameObject("BattleTankModulesTests");
            var host = new GameObject("tank", typeof(Rigidbody));
            host.transform.SetParent(_root.transform, false);
            var tank = host.AddComponent<BattleTank>();

            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get("ms-1");
            Assert.IsNotNull(card);

            tank.Initialize(card, 0, 0, 0, new[] { ModuleTuning.ToolboxId });
            Assert.AreEqual(
                ModuleTuning.RepairTime * ModuleTuning.ToolboxRepairFactor,
                tank.GetRepairTime(),
                1e-4f);
            Assert.IsTrue(tank.HasEquipment(ModuleTuning.ToolboxId));

            // init_modules registers the tracks, and the 4.8.0 card adds the
            // ammo rack the client authors as hull/ammoBayHealth.
            Assert.IsNotNull(tank.Modules.Get(TankModuleIds.Tracks));
            Assert.IsNotNull(tank.Modules.Get(TankModuleIds.AmmoBay));
            Assert.AreEqual(TankModule.Status.Normal, tank.ModuleStatus(TankModuleIds.Tracks));
            Assert.IsFalse(tank.TracksCrashed);

            // module_tracks is exported with the armour, so the hull knows the
            // length module_collide divides by.
            Assert.Greater(tank.TracksLength, 0f);

            tank.DamageModule(TankModuleIds.Tracks, 10000);
            Assert.IsTrue(tank.TracksCrashed);
        }

        [Test]
        public void TrackVisualsFollowDestroyedDamagedAndRestoredStates()
        {
            _root = new GameObject("BattleTankModulesTests");
            var host = new GameObject("tank", typeof(Rigidbody));
            host.transform.SetParent(_root.transform, false);
            var tank = host.AddComponent<BattleTank>();

            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            tank.Initialize(database.Get(BattleSession.DefaultTankId));

            var tracks = tank.Modules.Get(TankModuleIds.Tracks);
            Assert.IsNotNull(tracks);

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var left = (GameObject)typeof(BattleTank).GetField("_trackObjectLeft", flags).GetValue(tank);
            var right = (GameObject)typeof(BattleTank).GetField("_trackObjectRight", flags).GetValue(tank);
            var crash = (GameObject)typeof(BattleTank).GetField("_crashedTracks", flags).GetValue(tank);
            Assert.IsNotNull(left);
            Assert.IsNotNull(right);
            Assert.IsNotNull(crash);
            Assert.IsTrue(left.activeSelf);
            Assert.IsTrue(right.activeSelf);
            Assert.IsFalse(crash.activeSelf);

            tracks.ReceiveDamage(Mathf.Max(1, Mathf.CeilToInt(tracks.HealthComponent.MaxHealth * 0.6f)), 0f);
            Assert.AreEqual(TankModule.Status.Damaged, tank.ModuleStatus(TankModuleIds.Tracks));
            Assert.IsTrue(left.activeSelf, "damaged tracks keep the intact visual");
            Assert.IsTrue(right.activeSelf, "damaged tracks keep the intact visual");
            Assert.IsFalse(crash.activeSelf, "damaged tracks do not show the broken mesh");

            tracks.ReceiveDamage(10000, 0f);
            Assert.AreEqual(TankModule.Status.Destroyed, tank.ModuleStatus(TankModuleIds.Tracks));
            Assert.IsFalse(left.activeSelf);
            Assert.IsFalse(right.activeSelf);
            Assert.IsTrue(crash.activeSelf);

            tracks.Tick(tank.GetRepairTime() + 0.1f);
            Assert.AreEqual(TankModule.Status.Damaged, tank.ModuleStatus(TankModuleIds.Tracks), "auto-repair should restore only to yellow");
            Assert.IsTrue(left.activeSelf);
            Assert.IsTrue(right.activeSelf);
            Assert.IsFalse(crash.activeSelf);

            tank.RepairEverything();
            Assert.AreEqual(TankModule.Status.Normal, tank.ModuleStatus(TankModuleIds.Tracks), "repair kit fully restores the track");
            Assert.IsTrue(left.activeSelf);
            Assert.IsTrue(right.activeSelf);
            Assert.IsFalse(crash.activeSelf);

            tracks.ReceiveDamage(10000, 0f);
            tank.RestoreLife(null);
            Assert.AreEqual(TankModule.Status.Normal, tank.ModuleStatus(TankModuleIds.Tracks), "respawn restores the live track mesh");
            Assert.IsTrue(left.activeSelf);
            Assert.IsTrue(right.activeSelf);
            Assert.IsFalse(crash.activeSelf);
        }

        [Test]
        public void DamagedAmmoBaySlowsTheReloadDown()
        {
            _root = new GameObject("BattleTankModulesTests");
            var host = new GameObject("tank", typeof(Rigidbody));
            host.transform.SetParent(_root.transform, false);
            var tank = host.AddComponent<BattleTank>();

            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            tank.Initialize(database.Get("ms-1"));

            var bay = tank.Modules.Get(TankModuleIds.AmmoBay);
            Assert.IsNotNull(bay);
            Assert.AreEqual(1f, tank.ReloadPenalty, 1e-4f);

            bay.ReceiveDamage(bay.HealthComponent.MaxHealth - 1, 0f);
            Assert.AreEqual(TankModule.Status.Damaged, tank.ModuleStatus(TankModuleIds.AmmoBay));
            Assert.AreEqual(
                ModuleTuning.AmmoBayDamagedReload, tank.ReloadPenalty, 1e-4f);
            Assert.AreEqual(
                tank.GunInfo.Reload * ModuleTuning.AmmoBayDamagedReload,
                tank.Gun.ReloadTotal,
                1e-3f);
        }

        [Test]
        public void CardsCarryTheClientModuleAndCrewHealth()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get("ms-1");
            Assert.IsNotNull(card);

            // hull/ammoBayHealth and fuelTanks of the 4.8.0 XML.
            Assert.IsNotNull(card.AmmoBay);
            Assert.Greater(card.AmmoBay.Health, 0f);
            Assert.IsNotNull(card.FuelTank);
            Assert.Greater(card.FuelTank.Health, 0f);

            // <crew> lists the five roles the damage panel draws.
            Assert.AreEqual(TankCrewRoles.All.Length, card.Crew.Count);
            for (var i = 0; i < TankCrewRoles.All.Length; i++)
            {
                Assert.Contains(TankCrewRoles.All[i], (System.Collections.ICollection)card.Crew);
            }

            // The track card carries the client's regen pool as well.
            var tracks = card.Tracks.Count > 0 ? card.Tracks[0] : null;
            Assert.IsNotNull(tracks);
            Assert.Greater(tracks.Health, 0f);
            Assert.Greater(tracks.MaxRegenHealth, 0f);
        }

        [Test]
        public void EveryCardKnowsItsModulesAndCrew()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            foreach (var card in database.Tanks)
            {
                Assert.IsNotNull(card.AmmoBay, card.Name + ": no ammo rack");
                Assert.IsNotNull(card.FuelTank, card.Name + ": no fuel tank");
                Assert.IsNotEmpty(card.Crew, card.Name + ": crew");
                CollectionAssert.IsSubsetOf(card.Crew, TankCrewRoles.All, card.Name + ": unknown crew role");
                CollectionAssert.AllItemsAreUnique(card.Crew, card.Name + ": duplicate crew role");
                CollectionAssert.IsSubsetOf(
                    new[] { TankCrewRoles.Commander, TankCrewRoles.Driver, TankCrewRoles.Gunner },
                    card.Crew, card.Name + ": essential crew roles");
            }
        }

        [Test]
        public void ModuleHealthInfoReadsTheExportedKeys()
        {
            var node = JsonNode.Parse(
                "{\"health\":60,\"max_regen_health\":40,\"repair_cost\":1.5}");
            var info = ModuleHealthInfo.FromJson(node);
            Assert.IsNotNull(info);
            Assert.AreEqual(60f, info.Health, 1e-4f);
            Assert.AreEqual(40f, info.MaxRegenHealth, 1e-4f);
            Assert.AreEqual(1.5f, info.RepairCost, 1e-4f);
        }
    }
}
