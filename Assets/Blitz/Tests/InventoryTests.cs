using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The account warehouse: stacking, the buy/sell economy and the save
    /// round trip of the spare equipment and consumables.
    /// </summary>
    public sealed class InventoryTests
    {
        private AccountData _account;
        private InventoryService _warehouse;

        [SetUp]
        public void SetUp()
        {
            _account = new AccountData();
            _warehouse = new InventoryService(_account);
        }

        [Test]
        public void Catalog_CarriesEveryEquipmentAndConsumable()
        {
            Assert.AreEqual(18, InventoryCatalog.All.Count);
            Assert.AreEqual(
                9, InventoryCatalog.For(InventoryCategory.Equipment).Count);
            Assert.AreEqual(
                9, InventoryCatalog.For(InventoryCategory.Consumable).Count);

            var rammer = InventoryCatalog.Get("rammer");
            Assert.IsNotNull(rammer);
            Assert.AreEqual(InventoryCategory.Equipment, rammer.Category);
            Assert.AreEqual("equipment_rammer", rammer.IconRegion);

            var repair = InventoryCatalog.Get("repair_kit");
            Assert.IsNotNull(repair);
            Assert.AreEqual(InventoryCategory.Consumable, repair.Category);

            Assert.IsNull(InventoryCatalog.Get("no_such_item"));
            Assert.IsFalse(InventoryCatalog.IsKnown(string.Empty));
        }

        [Test]
        public void Warehouse_AddsTakesAndTotalsStacks()
        {
            Assert.IsTrue(_warehouse.Add("rammer", 2));
            Assert.IsTrue(_warehouse.Add("repair_kit", 3));
            Assert.AreEqual(2, _warehouse.Count("rammer"));
            Assert.AreEqual(5, _warehouse.TotalCount);

            // Unknown ids never reach the shelf.
            Assert.IsFalse(_warehouse.Add("no_such_item", 1));
            Assert.AreEqual(0, _warehouse.Count("no_such_item"));

            // Taking is all or nothing.
            Assert.IsFalse(_warehouse.Take("rammer", 3));
            Assert.AreEqual(2, _warehouse.Count("rammer"));
            Assert.IsTrue(_warehouse.Take("rammer", 2));
            Assert.AreEqual(0, _warehouse.Count("rammer"));
            Assert.IsFalse(_account.Inventory.ContainsKey("rammer"));
        }

        [Test]
        public void Warehouse_BuyAndSellUseTheLevelEconomy()
        {
            const int level = 5;
            var cost = InventoryCatalog.Get("vent").Cost(level);

            var credits = _account.Credits;
            var bought = _warehouse.Buy("vent", level, 2);
            Assert.IsTrue(bought.Ok);
            Assert.AreEqual(cost * 2, bought.Paid);
            Assert.AreEqual(credits - cost * 2, _account.Credits);
            Assert.AreEqual(2, _warehouse.Count("vent"));

            // Selling pays half, like every other sale in the game.
            var sold = _warehouse.Sell("vent", level, 1);
            Assert.IsTrue(sold.Ok);
            Assert.AreEqual(credits - cost * 2 + cost / 2, _account.Credits);
            Assert.AreEqual(1, _warehouse.Count("vent"));

            var empty = _warehouse.Sell("optics", level, 1);
            Assert.IsFalse(empty.Ok);
            Assert.AreEqual(InventoryService.ErrorEmptyStock, empty.ErrorKey);

            var unknown = _warehouse.Buy("no_such_item", level, 1);
            Assert.IsFalse(unknown.Ok);
            Assert.AreEqual(InventoryService.ErrorUnknownItem, unknown.ErrorKey);

            _account.Credits = 10;
            var broke = _warehouse.Buy("vent", level, 1);
            Assert.IsFalse(broke.Ok);
            Assert.AreEqual(
                InventoryService.ErrorNotEnoughCredits, broke.ErrorKey);
            Assert.AreEqual(1, _warehouse.Count("vent"));
        }

        [Test]
        public void Save_RoundTripKeepsTheWarehouse()
        {
            _warehouse.Add("rammer", 2);
            _warehouse.Add("fuel", 1);
            _account.Inventory["vent"] = 0;

            var restored = AccountData.FromJson(_account.ToJson());

            Assert.AreEqual(2, restored.InventoryCount("rammer"));
            Assert.AreEqual(1, restored.InventoryCount("fuel"));
            // Empty stacks are pruned on load, credits ride along.
            Assert.IsFalse(restored.Inventory.ContainsKey("vent"));
            Assert.AreEqual(_account.Credits, restored.Credits);
        }
    }
}
