using System;
using BlitzLegacy.Core.Save;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// The warehouse rules on top of AccountData.Inventory: stacking,
    /// taking spares out for mounting and the same credit economy the
    /// loadout screen already uses (buy at full price, sell back at half).
    /// Levels price the trade because item costs are level based everywhere
    /// else in the game; the caller passes the selected tank's level.
    /// </summary>
    public sealed class InventoryService
    {
        public const string ErrorNotEnoughCredits = LoadoutService.ErrorNotEnoughCredits;
        public const string ErrorUnknownItem = "inventory:unknown_item";
        public const string ErrorEmptyStock = "inventory:empty_stock";

        private readonly AccountData _account;

        public InventoryService(AccountData account)
        {
            _account = account;
        }

        public int Count(string itemId)
        {
            return _account == null ? 0 : _account.InventoryCount(itemId);
        }

        /// <summary>Total spare copies across every known item.</summary>
        public int TotalCount
        {
            get
            {
                if (_account == null) { return 0; }
                var total = 0;
                foreach (var pair in _account.Inventory)
                {
                    if (pair.Value > 0) { total += pair.Value; }
                }

                return total;
            }
        }

        /// <summary>Puts copies on the shelf. Unknown ids are refused.</summary>
        public bool Add(string itemId, int count)
        {
            if (_account == null || count <= 0 || !InventoryCatalog.IsKnown(itemId))
            {
                return false;
            }

            int current;
            _account.Inventory.TryGetValue(itemId, out current);
            _account.Inventory[itemId] = current + count;
            return true;
        }

        /// <summary>Takes copies off the shelf; all or nothing.</summary>
        public bool Take(string itemId, int count)
        {
            if (_account == null || count <= 0 || Count(itemId) < count)
            {
                return false;
            }

            var left = Count(itemId) - count;
            if (left <= 0)
            {
                _account.Inventory.Remove(itemId);
            }
            else
            {
                _account.Inventory[itemId] = left;
            }

            return true;
        }

        /// <summary>
        /// Buys copies into the warehouse at the level price. Returns the
        /// credits spent, or a failure key like the loadout service does.
        /// </summary>
        public TankActionResult Buy(string itemId, int level, int count)
        {
            var item = InventoryCatalog.Get(itemId);
            if (item == null || count <= 0)
            {
                return TankActionResult.Fail(ErrorUnknownItem);
            }

            if (_account == null)
            {
                return TankActionResult.Fail(ErrorNotEnoughCredits);
            }

            var cost = item.Cost(level) * count;
            if (_account.Credits < cost)
            {
                return TankActionResult.Fail(ErrorNotEnoughCredits);
            }

            _account.Credits -= cost;
            Add(itemId, count);
            return TankActionResult.Success(cost);
        }

        /// <summary>Sells copies from the warehouse at half the level price.</summary>
        public TankActionResult Sell(string itemId, int level, int count)
        {
            var item = InventoryCatalog.Get(itemId);
            if (item == null || count <= 0)
            {
                return TankActionResult.Fail(ErrorUnknownItem);
            }

            if (!Take(itemId, count))
            {
                return TankActionResult.Fail(ErrorEmptyStock);
            }

            var gain = item.SellCost(level) * count;
            if (_account != null)
            {
                _account.Credits = AccountData.AddCurrency(_account.Credits, gain);
            }

            return TankActionResult.Success(-gain);
        }
    }
}
