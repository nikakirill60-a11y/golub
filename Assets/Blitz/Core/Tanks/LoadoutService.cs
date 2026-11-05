using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Save;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// The hangar half of the source loadout screens: the shell sliders of
    /// ui_hangar.tscn and the BUY / SELL / SWAP economy of
    /// scripts/ui_elements/ui_equipment_list.gd.
    /// </summary>
    public enum EquipmentAction
    {
        None,
        Buy,
        Sell,
        Swap,
    }

    public sealed class LoadoutService
    {
        public const string ErrorNotEnoughCredits = "popup:NotEnoughCredits";

        /// <summary>slot1 / slot2 / slot3 of ui_equipment_list.gd.</summary>
        public const int SlotCount = 3;

        private readonly AccountData _account;
        private readonly TanksDatabase _database;

        public LoadoutService(AccountData account, TanksDatabase database)
        {
            _account = account;
            _database = database;
        }

        // ------------------------------------------------------------------
        // Shells.
        // ------------------------------------------------------------------

        /// <summary><c>gun.max_ammo</c>: the shared capacity of all three types.</summary>
        public static int Capacity(GunInfo gun)
        {
            return gun == null ? 0 : gun.MaxAmmo;
        }

        /// <summary>
        /// A burst gun loads whole clips, so its sliders step by the largest
        /// burst instead of by one shell.
        /// </summary>
        public static int Step(GunInfo gun)
        {
            if (gun == null || !gun.Burst.HasBursts) { return 1; }

            var step = 1;
            for (var i = 0; i < gun.Burst.Bursts.Count; i++)
            {
                step = Math.Max(step, gun.Burst.Bursts[i]);
            }
            return step;
        }

        public static int Total(IReadOnlyList<int> amounts)
        {
            if (amounts == null) { return 0; }
            var total = 0;
            for (var i = 0; i < amounts.Count; i++) { total += amounts[i]; }
            return total;
        }

        /// <summary>
        /// Setting one slider: the source clamps the new value so the three
        /// types together never pass the gun's capacity, and keeps it on the
        /// burst step.
        /// </summary>
        public static int ClampShellAmount(
            GunInfo gun, IReadOnlyList<int> amounts, int slot, int wanted)
        {
            if (amounts == null || slot < 0 || slot >= amounts.Count) { return 0; }

            var step = Step(gun);
            var others = Total(amounts) - amounts[slot];
            var room = Math.Max(0, Capacity(gun) - others);
            var value = Math.Max(0, Math.Min(wanted, room));
            return (value / step) * step;
        }

        /// <summary>
        /// <c>update_shells</c>: a layout saved for a smaller gun is dropped
        /// when the installed gun can no longer carry it.
        /// </summary>
        public static void NormalizeShells(GunInfo gun, IList<int> amounts)
        {
            if (amounts == null) { return; }
            var total = 0;
            for (var i = 0; i < amounts.Count; i++) { total += amounts[i]; }
            if (total <= Capacity(gun)) { return; }
            for (var i = 0; i < amounts.Count; i++) { amounts[i] = 0; }
        }

        /// <summary><c>popup:no_shells</c>: entering battle with an empty rack.</summary>
        public static bool HasNoShells(IReadOnlyList<int> amounts)
        {
            return Total(amounts) <= 0;
        }

        /// <summary><c>hangar:shell_cost</c> for one full rack.</summary>
        public static int ShellsCost(GunInfo gun, IReadOnlyList<int> amounts)
        {
            if (gun == null || amounts == null) { return 0; }
            var cost = 0;
            for (var i = 0; i < amounts.Count && i < gun.Shells.Count; i++)
            {
                cost += gun.Shells[i].Cost * amounts[i];
            }
            return cost;
        }

        // ------------------------------------------------------------------
        // Equipment.
        // ------------------------------------------------------------------

        /// <summary><c>get_equipment_cost_by_id</c>: level price x cost_factor.</summary>
        public int EquipmentCost(string tank, string id)
        {
            if (string.IsNullOrEmpty(id)) { return 0; }
            var info = _database == null ? null : _database.Get(tank);
            return EquipmentCatalog.Cost(id, info == null ? 1 : info.Level);
        }

        /// <summary>
        /// The same getter for the consumables half of the widget: one
        /// UIEquipmentList serves both lists and only swaps the table it reads,
        /// cost_by_lvl_consumables instead of cost_by_lvl_equipment.
        /// </summary>
        public int ConsumableCost(string tank, string id)
        {
            if (string.IsNullOrEmpty(id)) { return 0; }
            var info = _database == null ? null : _database.Get(tank);
            return ConsumableCatalog.Cost(id, info == null ? 1 : info.Level);
        }

        public int ItemCost(string tank, string id, bool consumables)
        {
            return consumables ? ConsumableCost(tank, id) : EquipmentCost(tank, id);
        }

        /// <summary>
        /// <c>get_action_status</c>: what pressing the action button would do
        /// for the currently selected slot and grid item.
        /// </summary>
        public EquipmentAction Action(TankSaveData save, int slot, string item)
        {
            return Action(save, slot, item, false);
        }

        public EquipmentAction Action(
            TankSaveData save, int slot, string item, bool consumables)
        {
            if (save == null || slot < 0 || slot >= SlotCount
                || string.IsNullOrEmpty(item))
            {
                return EquipmentAction.None;
            }

            var mounted = Mounted(save, consumables);
            var installed = Installed(save, slot, consumables);
            if (string.Equals(installed, item, StringComparison.Ordinal))
            {
                return EquipmentAction.Sell;
            }

            if (string.IsNullOrEmpty(installed))
            {
                return mounted.Contains(item)
                    ? EquipmentAction.None
                    : EquipmentAction.Buy;
            }

            return mounted.Contains(item)
                ? EquipmentAction.None
                : EquipmentAction.Swap;
        }

        /// <summary>
        /// <c>get_current_item_cost</c>. Negative means the account is paid.
        /// </summary>
        public int ActionCost(TankSaveData save, int slot, string item)
        {
            return ActionCost(save, slot, item, false);
        }

        public int ActionCost(
            TankSaveData save, int slot, string item, bool consumables)
        {
            switch (Action(save, slot, item, consumables))
            {
                case EquipmentAction.Sell:
                    // Demounting is free: the item goes back to the
                    // warehouse instead of being sold for credits. Selling
                    // for credits happens on the inventory screen.
                    return 0;
                case EquipmentAction.Swap:
                case EquipmentAction.Buy:
                    // A spare copy in the warehouse mounts for free; only a
                    // genuinely new item is charged. The swapped-out item
                    // returns to the warehouse, so no sell-back is paid.
                    return InStock(item) ? 0 : ItemCost(save.Name, item, consumables);
            }
            return 0;
        }

        /// <summary>Whether the warehouse holds a spare copy of the item.</summary>
        private bool InStock(string item)
        {
            return _account != null && _account.InventoryCount(item) > 0;
        }

        /// <summary>The id in a slot, or the empty string like the source's slots.</summary>
        public static string Installed(TankSaveData save, int slot)
        {
            return Installed(save, slot, false);
        }

        public static string Installed(TankSaveData save, int slot, bool consumables)
        {
            if (save == null || slot < 0 || slot >= SlotCount) { return string.Empty; }
            var list = Mounted(save, consumables);
            return slot < list.Count ? list[slot] : string.Empty;
        }

        /// <summary>curtank.consumables or curtank.equipment.</summary>
        public static List<string> Mounted(TankSaveData save, bool consumables)
        {
            if (save == null) { return new List<string>(); }
            return consumables ? save.Consumables : save.Equipment;
        }

        /// <summary><c>action_pressed</c>: run the action and move the credits.</summary>
        public TankActionResult Apply(TankSaveData save, int slot, string item)
        {
            return Apply(save, slot, item, false);
        }

        public TankActionResult Apply(
            TankSaveData save, int slot, string item, bool consumables)
        {
            var action = Action(save, slot, item, consumables);
            if (action == EquipmentAction.None)
            {
                return TankActionResult.Fail("invalid equipment action");
            }

            var cost = ActionCost(save, slot, item, consumables);
            if (cost > 0 && (_account == null || _account.Credits < cost))
            {
                return TankActionResult.Fail(ErrorNotEnoughCredits);
            }

            if (_account != null)
            {
                _account.Credits -= cost;
            }

            // Warehouse moves: demounted items go back on the shelf, and a
            // mount that ActionCost priced as free consumes a spare copy.
            var warehouse = _account == null ? null : new InventoryService(_account);
            var demounted = Installed(save, slot, consumables);
            if (warehouse != null && action != EquipmentAction.Buy
                && !string.IsNullOrEmpty(demounted))
            {
                warehouse.Add(demounted, 1);
            }

            if (warehouse != null && action != EquipmentAction.Sell && InStock(item))
            {
                warehouse.Take(item, 1);
            }

            Fill(save);
            Mounted(save, consumables)[slot] =
                action == EquipmentAction.Sell ? string.Empty : item;
            return TankActionResult.Success(cost);
        }

        /// <summary>The three slots the source always draws, empty ones included.</summary>
        public static void Fill(TankSaveData save)
        {
            if (save == null) { return; }
            while (save.Equipment.Count < SlotCount) { save.Equipment.Add(string.Empty); }
            while (save.Consumables.Count < SlotCount) { save.Consumables.Add(string.Empty); }
        }

        /// <summary>Installed ids without the empty slots, as the battle reads them.</summary>
        public static List<string> ActiveEquipment(TankSaveData save)
        {
            return Active(save, false);
        }

        /// <summary>
        /// The ids _battle.gd feeds to add_consumable() after setup_completed,
        /// in slot order and without the empty slots.
        /// </summary>
        public static List<string> ActiveConsumables(TankSaveData save)
        {
            return Active(save, true);
        }

        private static List<string> Active(TankSaveData save, bool consumables)
        {
            var active = new List<string>();
            if (save == null) { return active; }
            var list = Mounted(save, consumables);
            for (var i = 0; i < list.Count; i++)
            {
                if (!string.IsNullOrEmpty(list[i]))
                {
                    active.Add(list[i]);
                }
            }
            return active;
        }
    }
}
