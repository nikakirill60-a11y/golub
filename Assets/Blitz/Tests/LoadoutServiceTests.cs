using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The ammo rack of ui_hangar.tscn and the BUY / SELL / SWAP economy of
    /// scripts/ui_elements/ui_equipment_list.gd, held against the exported
    /// source tables.
    /// </summary>
    public sealed class LoadoutServiceTests
    {
        private AccountData _account;
        private TanksDatabase _database;
        private LoadoutService _service;
        private TankSaveData _save;

        [SetUp]
        public void SetUp()
        {
            var text = Resources.Load<TextAsset>("Data/tanks");
            Assert.IsNotNull(text, "Data/tanks.json is missing from Resources");
            _database = TanksDatabase.Parse(text.text);
            _account = new AccountData();
            _service = new LoadoutService(_account, _database);
            _save = _account.AddTank("ms-1", true);
            LoadoutService.Fill(_save);
        }

        private GunInfo Gun()
        {
            return _database.Get("ms-1").Guns[0];
        }

        [Test]
        public void EquipmentCatalog_CarriesTheSourceTable()
        {
            Assert.AreEqual(9, EquipmentCatalog.All.Length);
            Assert.AreEqual(2f, EquipmentCatalog.Get("rammer").CostFactor);
            Assert.AreEqual(2f, EquipmentCatalog.Get("vent").CostFactor);
            Assert.AreEqual(1f, EquipmentCatalog.Get("optics").CostFactor);
            Assert.AreEqual(200f, EquipmentCatalog.Get("optics").Weight);
            Assert.AreEqual("equipment:optics/name", EquipmentCatalog.Get("optics").NameKey);

            // cost_by_lvl_equipment x cost_factor.
            Assert.AreEqual(3000, EquipmentCatalog.Cost("rammer", 1));
            Assert.AreEqual(10000, EquipmentCatalog.Cost("vent", 2));
            Assert.AreEqual(35000, EquipmentCatalog.Cost("optics", 3));
            Assert.AreEqual(180000, EquipmentCatalog.Cost("camonet", 10));
        }

        [Test]
        public void EquipmentModifiers_MatchTheSourceCallbacks()
        {
            var none = EquipmentCatalog.Modifiers(new string[0]);
            Assert.AreEqual(1f, none.Reload);
            Assert.AreEqual(0f, none.AddedWeight);

            var all = EquipmentCatalog.Modifiers(
                new[] { "rammer", "vent", "aimStab", "aimDrive", "optics" });
            Assert.AreEqual(0.9f * 0.95f, all.Reload, 1e-5f);
            Assert.AreEqual(0.95f * 0.9f, all.AimTime, 1e-5f);
            Assert.AreEqual(0.8f, all.Dispersion, 1e-5f);
            Assert.AreEqual(1.1f, all.ViewRange, 1e-5f);
            Assert.AreEqual(1000f, all.AddedWeight);

            // The source callbacks of these four only add their weight.
            var inert = EquipmentCatalog.Modifiers(
                new[] { "toolbox", "spallliner", "camonet", "stereoscope" });
            Assert.AreEqual(1f, inert.Reload);
            Assert.AreEqual(1f, inert.AimTime);
            Assert.AreEqual(1f, inert.Dispersion);
            Assert.AreEqual(1f, inert.ViewRange);
            Assert.AreEqual(800f, inert.AddedWeight);
        }

        [Test]
        public void Shells_KeepTheGunCapacityAndBurstStep()
        {
            var gun = Gun();
            Assert.Greater(LoadoutService.Capacity(gun), 0);
            Assert.AreEqual(1, LoadoutService.Step(gun));

            var amounts = _save.ShellsAmounts;
            amounts[0] = LoadoutService.ClampShellAmount(gun, amounts, 0, 1000);
            Assert.AreEqual(LoadoutService.Capacity(gun), amounts[0]);

            // The rack is full, so the second type cannot take anything.
            Assert.AreEqual(0, LoadoutService.ClampShellAmount(gun, amounts, 1, 5));

            amounts[0] = LoadoutService.ClampShellAmount(gun, amounts, 0, 4);
            Assert.AreEqual(4, amounts[0]);
            Assert.AreEqual(
                LoadoutService.Capacity(gun) - 4,
                LoadoutService.ClampShellAmount(gun, amounts, 1, 1000));

            Assert.IsFalse(LoadoutService.HasNoShells(amounts));
            amounts[0] = 0;
            Assert.IsTrue(LoadoutService.HasNoShells(amounts));

            var burst = new GunInfo { MaxAmmo = 30 };
            burst.Burst.Bursts.Add(3);
            burst.Burst.Bursts.Add(5);
            Assert.AreEqual(5, LoadoutService.Step(burst));
            Assert.AreEqual(
                5, LoadoutService.ClampShellAmount(burst, new[] { 0, 0, 0 }, 0, 9));
        }

        [Test]
        public void Shells_AreDroppedWhenTheRackNoLongerFits()
        {
            var gun = Gun();
            var amounts = _save.ShellsAmounts;
            amounts[0] = LoadoutService.Capacity(gun) + 10;
            LoadoutService.NormalizeShells(gun, amounts);
            Assert.AreEqual(0, LoadoutService.Total(amounts));

            amounts[0] = 5;
            LoadoutService.NormalizeShells(gun, amounts);
            Assert.AreEqual(5, amounts[0]);

            Assert.AreEqual(gun.Shells[0].Cost * 5, LoadoutService.ShellsCost(gun, amounts));
        }

        [Test]
        public void Equipment_BuysSellsAndSwapsLikeTheSource()
        {
            var level = _database.Get("ms-1").Level;
            var rammer = EquipmentCatalog.Cost("rammer", level);
            var optics = EquipmentCatalog.Cost("optics", level);

            Assert.AreEqual(EquipmentAction.Buy, _service.Action(_save, 0, "rammer"));
            Assert.AreEqual(rammer, _service.ActionCost(_save, 0, "rammer"));

            var credits = _account.Credits;
            Assert.IsTrue(_service.Apply(_save, 0, "rammer").Ok);
            Assert.AreEqual(credits - rammer, _account.Credits);
            Assert.AreEqual("rammer", LoadoutService.Installed(_save, 0));

            // The same item cannot be installed twice.
            Assert.AreEqual(EquipmentAction.None, _service.Action(_save, 1, "rammer"));

            // Demounting is free: the item returns to the warehouse
            // instead of being sold for half price.
            Assert.AreEqual(EquipmentAction.Sell, _service.Action(_save, 0, "rammer"));
            Assert.AreEqual(0, _service.ActionCost(_save, 0, "rammer"));

            credits = _account.Credits;
            Assert.IsTrue(_service.Apply(_save, 0, "rammer").Ok);
            Assert.AreEqual(credits, _account.Credits);
            Assert.AreEqual(1, _account.InventoryCount("rammer"));
            Assert.AreEqual(string.Empty, LoadoutService.Installed(_save, 0));

            // A spare copy in the warehouse mounts for free.
            Assert.AreEqual(EquipmentAction.Buy, _service.Action(_save, 0, "rammer"));
            Assert.AreEqual(0, _service.ActionCost(_save, 0, "rammer"));
            Assert.IsTrue(_service.Apply(_save, 0, "rammer").Ok);
            Assert.AreEqual(credits, _account.Credits);
            Assert.AreEqual(0, _account.InventoryCount("rammer"));

            // Swapping charges the full new price (the warehouse has no
            // optics) and shelves the swapped-out item.
            Assert.AreEqual(EquipmentAction.Swap, _service.Action(_save, 0, "optics"));
            Assert.AreEqual(optics, _service.ActionCost(_save, 0, "optics"));

            credits = _account.Credits;
            Assert.IsTrue(_service.Apply(_save, 0, "optics").Ok);
            Assert.AreEqual(credits - optics, _account.Credits);
            Assert.AreEqual("optics", LoadoutService.Installed(_save, 0));
            Assert.AreEqual(1, _account.InventoryCount("rammer"));

            // Demounting the optics shelves it too, credits untouched.
            credits = _account.Credits;
            Assert.IsTrue(_service.Apply(_save, 0, "optics").Ok);
            Assert.AreEqual(credits, _account.Credits);
            Assert.AreEqual(string.Empty, LoadoutService.Installed(_save, 0));
            Assert.AreEqual(1, _account.InventoryCount("optics"));
        }

        [Test]
        public void Equipment_RefusesAPurchaseWithoutCredits()
        {
            _account.Credits = 1;
            var result = _service.Apply(_save, 0, "rammer");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LoadoutService.ErrorNotEnoughCredits, result.ErrorKey);
            Assert.AreEqual(1, _account.Credits);
            Assert.AreEqual(string.Empty, LoadoutService.Installed(_save, 0));
        }

        [Test]
        public void ActiveEquipment_SkipsEmptySlots()
        {
            Assert.IsTrue(_service.Apply(_save, 1, "vent").Ok);
            var active = LoadoutService.ActiveEquipment(_save);
            Assert.AreEqual(1, active.Count);
            Assert.AreEqual("vent", active[0]);
        }
    }
}
