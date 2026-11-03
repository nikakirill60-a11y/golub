using System.Collections.Generic;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Covers the ported research rules from Godot scripts/account.gd against the
    /// real exported USSR line, so prices and gating cannot silently drift.
    /// </summary>
    public sealed class TankResearchTests
    {
        private AccountData _account;
        private TanksDatabase _database;
        private TankResearchService _service;

        [SetUp]
        public void SetUp()
        {
            var text = Resources.Load<TextAsset>("Data/tanks");
            Assert.IsNotNull(text, "Data/tanks.json is missing from Resources");
            _database = TanksDatabase.Parse(text.text);
            _account = new AccountData();
            _service = new TankResearchService(_account, _database);
            _account.AddTank("ms-1", true);
        }

        [Test]
        public void ClientRadio_IsLoadedAndParticipatesInModuleResearch()
        {
            var info = _database.Get("ms-1");
            Assert.AreEqual(1, info.Radios.Count);
            Assert.AreEqual("Alarm_flags", info.Radios[0].Name);
            Assert.Greater(info.Radios[0].Distance, 0f);
            Assert.AreEqual(
                ResearchStatus.InstalledBought,
                _service.ModuleStatus("ms-1", ModuleKind.Radio, 0));
        }

        /// <summary>
        /// The modules screen appends a turret row and a radio row the source
        /// screen never had, and it only shows one when the client data gives
        /// it something to choose. This pins that data down: no card carries a
        /// second radio or a radio anything unlocks, and among the tank
        /// destroyers only the turreted ones research a second turret, so a
        /// casemate one like the AT 2 has no turret row at all.
        /// </summary>
        [Test]
        public void AppendedModuleRows_FollowTheClientModuleCounts()
        {
            var turreted = new List<string>();
            for (var i = 0; i < _database.Tanks.Count; i++)
            {
                var info = _database.Tanks[i];
                Assert.AreEqual(1, info.Radios.Count, info.Name + " radios");
                Assert.AreEqual(
                    0, info.Radios[0].Unlocks.Count, info.Name + " radio unlocks");
                if (info.Class == TankClass.TankDestroyer && info.Turrets.Count > 1)
                {
                    turreted.Add(info.Name);
                }
            }

            CollectionAssert.AreEquivalent(
                new[]
                {
                    "pz_sfl_ivc", "waffentrager_iv", "m8a1", "m10_wolverine",
                    "t49", "m18_hellcat", "m36_slagger", "t25_2", "gb41_challenger",
                    "oth46_charioteer_s3", "oth67_fv215b_183ann_s4",
                },
                turreted);
            Assert.AreEqual(1, _database.Get("gb73_at2").Turrets.Count);
        }

        /// <summary>
        /// The client hangs the BT-2 edge off the MS-1's 23 mm VJA, which only
        /// the researched T-18 mod turret carries, so every tank test has to
        /// walk that path first.
        /// </summary>
        private void OpenBt2Edge()
        {
            var save = _account.GetTank("ms-1");
            var info = _database.Get("ms-1");
            save.Exp = info.Turrets[1].CostXp + info.Guns[3].CostXp;
            Assert.IsTrue(_service.ResearchModule("ms-1", ModuleKind.Turret, 1).Ok);
            Assert.IsTrue(_service.ResearchModule("ms-1", ModuleKind.Gun, 3).Ok);
            save.Exp = 0;
        }

        [Test]
        public void Statuses_FollowTheGodotResearchGates()
        {
            Assert.AreEqual(ResearchStatus.InstalledBought, _service.TankStatus("ms-1"));

            // Stock MS-1: the vehicle edge of BT-2 is still behind two modules.
            Assert.AreEqual(ResearchStatus.NotAvailable, _service.TankStatus("bt-2"));
            Assert.AreEqual(ResearchStatus.NotAvailable, _service.TankStatus("bt-7"));
            Assert.AreEqual(ResearchStatus.NotAvailable, _service.TankStatus("t62a"));
            Assert.AreEqual(ResearchStatus.NotAvailable, _service.TankStatus("no-such-tank"));

            OpenBt2Edge();
            Assert.AreEqual(ResearchStatus.Available, _service.TankStatus("bt-2"));

            var blocked = _service.ResearchTank("bt-7");
            Assert.IsFalse(blocked.Ok);
            Assert.AreEqual(TankResearchService.ErrorResearchPrevious, blocked.ErrorKey);
        }

        [Test]
        public void ResearchTank_SpendsTankExpThenFreeExp()
        {
            OpenBt2Edge();
            var poor = _service.ResearchTank("bt-2");
            Assert.IsFalse(poor.Ok);
            Assert.AreEqual(TankResearchService.ErrorNotEnoughExp, poor.ErrorKey);

            _account.GetTank("ms-1").Exp = 200;
            _account.FreeXp = 400;

            var researched = _service.ResearchTank("bt-2");
            Assert.IsTrue(researched.Ok);
            Assert.AreEqual(200, researched.Paid);
            Assert.AreEqual(350, researched.PaidFreeXp);
            Assert.AreEqual(0, _account.GetTank("ms-1").Exp);
            Assert.AreEqual(50, _account.FreeXp);

            // Researched but not owned yet. BT-7 stays shut: its edge hangs off
            // the BT-2's top turret gun, which nobody has researched yet.
            Assert.AreEqual(ResearchStatus.Researched, _service.TankStatus("bt-2"));
            Assert.AreEqual(ResearchStatus.NotAvailable, _service.TankStatus("bt-7"));
            Assert.IsFalse(_service.ResearchTank("bt-2").Ok);
        }

        [Test]
        public void BuyAndSellTank_MoveCreditsAndOwnership()
        {
            OpenBt2Edge();
            _account.GetTank("ms-1").Exp = 550;
            Assert.IsTrue(_service.ResearchTank("bt-2").Ok);

            var credits = _account.Credits;
            var bought = _service.BuyTank("bt-2");
            Assert.IsTrue(bought.Ok);
            Assert.AreEqual(3500, bought.Paid);
            Assert.AreEqual(credits - 3500, _account.Credits);
            Assert.AreEqual(ResearchStatus.InstalledBought, _service.TankStatus("bt-2"));

            var again = _service.BuyTank("bt-2");
            Assert.IsFalse(again.Ok);
            Assert.AreEqual(TankResearchService.ErrorTankAlreadyBought, again.ErrorKey);

            var sold = _service.SellTank("bt-2");
            Assert.IsTrue(sold.Ok);
            Assert.AreEqual(1750, sold.Paid);
            Assert.AreEqual(credits - 3500 + 1750, _account.Credits);

            // Selling keeps the card researched, exactly like Godot sell_tank.
            Assert.AreEqual(ResearchStatus.Researched, _service.TankStatus("bt-2"));
            Assert.IsFalse(_service.SellTank("bt-2").Ok);
        }

        [Test]
        public void Modules_GateOnTheResearchGraphAndInstallOnResearch()
        {
            var save = _account.GetTank("ms-1");
            var info = _database.Get("ms-1");

            Assert.AreEqual(ResearchStatus.InstalledBought,
                _service.ModuleStatus("ms-1", ModuleKind.Turret, 0));
            Assert.AreEqual(ResearchStatus.Available,
                _service.ModuleStatus("ms-1", ModuleKind.Turret, 1));

            // The 23 mm VJA hangs off the researched turret, so the stock tank
            // cannot reach it yet.
            Assert.AreEqual(ResearchStatus.NotAvailable,
                _service.ModuleStatus("ms-1", ModuleKind.Gun, 3));

            var upgrade = info.Turrets[1];
            save.Exp = upgrade.CostXp;
            var researched = _service.ResearchModule("ms-1", ModuleKind.Turret, 1);
            Assert.IsTrue(researched.Ok);
            Assert.AreEqual(upgrade.CostXp, researched.Paid);
            Assert.AreEqual(0, save.Exp);

            // Researching installs the module right away, as Godot does.
            Assert.AreEqual(1, save.TurretId);
            Assert.IsTrue(_service.ModuleResearched(upgrade.Name));
            Assert.AreEqual(ResearchStatus.InstalledBought,
                _service.ModuleStatus("ms-1", ModuleKind.Turret, 1));
            Assert.AreEqual(ResearchStatus.Researched,
                _service.ModuleStatus("ms-1", ModuleKind.Turret, 0));
            Assert.AreEqual(ResearchStatus.Available,
                _service.ModuleStatus("ms-1", ModuleKind.Gun, 3));

            var repeated = _service.ResearchModule("ms-1", ModuleKind.Turret, 1);
            Assert.IsFalse(repeated.Ok);
            Assert.AreEqual(TankResearchService.ErrorModuleAlreadyInstalled, repeated.ErrorKey);
        }

        [Test]
        public void InstallGun_EmptiesTheAmmoRackAndRespectsGating()
        {
            var save = _account.GetTank("ms-1");
            for (var i = 0; i < save.ShellsAmounts.Count; i++)
            {
                save.ShellsAmounts[i] = 20;
            }

            // The stock T-18 turret carries the 37 mm Gochkins only, so any
            // other barrel needs the researched turret first.
            var blocked = _service.InstallModule("ms-1", ModuleKind.Gun, 1);
            Assert.IsFalse(blocked.Ok);
            Assert.AreEqual(TankResearchService.ErrorGunNeedsTurret, blocked.ErrorKey);
            Assert.AreEqual(20, save.ShellsAmounts[0]);

            var info = _database.Get("ms-1");
            save.Exp = info.Turrets[1].CostXp;
            Assert.IsTrue(_service.ResearchModule("ms-1", ModuleKind.Turret, 1).Ok);

            var gun = info.Guns[1];
            save.Exp = gun.CostXp;
            Assert.IsTrue(_service.InstallModule("ms-1", ModuleKind.Gun, 1).Ok);
            Assert.AreEqual(1, save.GunId);
            for (var i = 0; i < save.ShellsAmounts.Count; i++)
            {
                Assert.AreEqual(0, save.ShellsAmounts[i]);
            }
        }

        [Test]
        public void ValidateModules_MarksStockAndInstalledAsResearched()
        {
            var save = _account.GetTank("ms-1");
            var info = _database.Get("ms-1");

            // A save that points at the top turret counts it as researched,
            // and the gun is clamped onto something that turret can carry.
            save.TurretId = 1;
            save.GunId = 4;
            _service.ValidateModules("ms-1");

            Assert.IsTrue(_service.ModuleResearched(info.Turrets[1].Name));
            Assert.AreEqual(4, save.GunId);
            Assert.IsFalse(_service.ModuleResearched(info.Guns[3].Name));
            Assert.IsFalse(_service.IsTankElite("ms-1"));

            // A turret id from an older save is clamped, and the gun of the
            // dropped turret is swapped for one the stock turret carries.
            save.TurretId = 9;
            save.GunId = 4;
            _service.ValidateModules("ms-1");
            Assert.AreEqual(1, save.TurretId);

            save.TurretId = 0;
            save.GunId = 4;
            _service.ValidateModules("ms-1");
            Assert.AreEqual(0, save.TurretId);
            Assert.AreEqual(0, save.GunId);
        }
    }
}
