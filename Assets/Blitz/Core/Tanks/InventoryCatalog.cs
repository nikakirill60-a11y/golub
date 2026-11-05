using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>The two warehouse shelves: mounted hardware and consumables.</summary>
    public enum InventoryCategory
    {
        Equipment = 0,
        Consumable = 1,
    }

    /// <summary>
    /// One warehouse item: a unified view over EquipmentCatalog and
    /// ConsumableCatalog entries, so the inventory screen, the inventory
    /// service and the loadout stock logic never branch on which of the two
    /// legacy catalogs an id came from. Prices stay level based, exactly
    /// like LoadoutService prices them today.
    /// </summary>
    public sealed class InventoryItem
    {
        public InventoryItem(
            string id, InventoryCategory category, string nameKey,
            string descriptionKey, string iconRegion)
        {
            Id = id;
            Category = category;
            NameKey = nameKey;
            DescriptionKey = descriptionKey;
            IconRegion = iconRegion;
        }

        public string Id { get; private set; }

        public InventoryCategory Category { get; private set; }

        public string NameKey { get; private set; }

        public string DescriptionKey { get; private set; }

        /// <summary>Atlas region of the item artwork (already packed).</summary>
        public string IconRegion { get; private set; }

        public bool IsEquipment
        {
            get { return Category == InventoryCategory.Equipment; }
        }

        /// <summary>Full purchase price in credits for a tank of this level.</summary>
        public int Cost(int level)
        {
            return IsEquipment
                ? EquipmentCatalog.Cost(Id, level)
                : ConsumableCatalog.Cost(Id, level);
        }

        /// <summary>The half price every sale in the game pays back.</summary>
        public int SellCost(int level)
        {
            return (int)(Cost(level) * 0.5f);
        }
    }

    /// <summary>All warehouse items in catalog order: equipment, then consumables.</summary>
    public static class InventoryCatalog
    {
        private static readonly List<InventoryItem> Items = Build();

        public static IReadOnlyList<InventoryItem> All
        {
            get { return Items; }
        }

        public static InventoryItem Get(string id)
        {
            if (string.IsNullOrEmpty(id)) { return null; }
            for (var i = 0; i < Items.Count; i++)
            {
                if (string.Equals(Items[i].Id, id, StringComparison.Ordinal))
                {
                    return Items[i];
                }
            }

            return null;
        }

        public static bool IsKnown(string id)
        {
            return Get(id) != null;
        }

        /// <summary>The items of one shelf, in catalog order.</summary>
        public static List<InventoryItem> For(InventoryCategory category)
        {
            var list = new List<InventoryItem>();
            for (var i = 0; i < Items.Count; i++)
            {
                if (Items[i].Category == category) { list.Add(Items[i]); }
            }

            return list;
        }

        private static List<InventoryItem> Build()
        {
            var list = new List<InventoryItem>();
            for (var i = 0; i < EquipmentCatalog.All.Length; i++)
            {
                var item = EquipmentCatalog.All[i];
                list.Add(new InventoryItem(
                    item.Id, InventoryCategory.Equipment,
                    item.NameKey, item.DescriptionKey, item.IconRegion));
            }

            for (var i = 0; i < ConsumableCatalog.All.Length; i++)
            {
                var item = ConsumableCatalog.All[i];
                list.Add(new InventoryItem(
                    item.Id, InventoryCategory.Consumable,
                    item.NameKey, item.DescriptionKey, item.IconRegion));
            }

            return list;
        }
    }
}
