using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Shop
{
    public enum ShopCurrency
    {
        Gold = 0,
        Credits = 1,
    }

    /// <summary>Godot <c>prem_entry(days)</c>.</summary>
    public sealed class PremiumOffer
    {
        public PremiumOffer(int days, int costGold)
        {
            Days = days;
            CostGold = costGold;
        }

        public int Days { get; private set; }

        public int CostGold { get; private set; }

        /// <summary>Atlas region of the Godot <c>ImagePremium&lt;days&gt;</c> picture.</summary>
        public string ImageRegion
        {
            get { return "shop_premium_" + Days; }
        }
    }

    /// <summary>Godot <c>container_entry(loot, cost, img, points)</c>.</summary>
    public sealed class ContainerOffer
    {
        public ContainerOffer(
            string id, ShopCurrency currency, int cost, ContainerLoot loot,
            int guaranteedRewardPoints, string imageRegion)
        {
            Id = id;
            Currency = currency;
            Cost = cost;
            Loot = loot;
            GuaranteedRewardPoints = guaranteedRewardPoints;
            ImageRegion = imageRegion;
        }

        /// <summary>Catalog id: <c>small</c>, <c>big</c> or <c>huge</c>.</summary>
        public string Id { get; private set; }

        public ShopCurrency Currency { get; private set; }

        public int Cost { get; private set; }

        public ContainerLoot Loot { get; private set; }

        /// <summary>Openings until the tank is guaranteed (Godot points); 0 means no guarantee.</summary>
        public int GuaranteedRewardPoints { get; private set; }

        /// <summary>Whether this container has a guaranteed-tank progress bar.</summary>
        public bool HasGuarantee
        {
            get { return GuaranteedRewardPoints > 0; }
        }

        public string ImageRegion { get; private set; }

        /// <summary>Separate original 4.8.0 art for the opening screen.</summary>
        public string OpeningImageRegion
        {
            get { return "chest_open_" + Id; }
        }

        public string NameKey
        {
            get { return "shop:container/" + Id; }
        }

        public string DescriptionKey
        {
            get { return "shop:container/" + Id + "/desc"; }
        }
    }

    /// <summary>
    /// Static shop content: the Godot premium-account price list and the
    /// three containers. The weekly tank list lives in
    /// <see cref="WeeklyTankRotation"/>.
    /// </summary>
    public static class ShopCatalog
    {
        /// <summary>Godot <c>prem_day_cost</c>.</summary>
        public const int PremiumDayCost = 250;

        /// <summary>Godot <c>Global.gold_price</c>: credits per gold.</summary>
        public const int GoldPrice = 400;

        public const string SmallContainerId = "small";
        public const string BigContainerId = "big";
        public const string HugeContainerId = "huge";

        private static readonly int[] PremiumDays = { 1, 3, 7, 14, 30, 90, 180, 360 };

        /// <summary>
        /// Godot loot tables name a few cards by their old Godot ids; the
        /// Unity tank list keeps the 4.8.0 client names.
        /// </summary>
        private static readonly Dictionary<string, string> GodotTankAliases =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "t2_e1", "t2_lt" },
                { "bt_7a", "r105_bt_7a" },
            };

        public static IReadOnlyList<PremiumOffer> PremiumOffers()
        {
            var offers = new List<PremiumOffer>(PremiumDays.Length);
            for (var i = 0; i < PremiumDays.Length; i++)
            {
                offers.Add(new PremiumOffer(PremiumDays[i], PremiumDays[i] * PremiumDayCost));
            }

            return offers;
        }

        public static string ResolveTankName(string godotName)
        {
            string alias;
            return godotName != null && GodotTankAliases.TryGetValue(godotName, out alias) ? alias : godotName;
        }

        /// <summary>Drop chances in percent: credits and free XP are common, gold rarer, tanks rarest.</summary>
        public const float SmallTankChance = 0.5f;
        public const float SmallGoldChance = 10f;
        public const float BigTankChance = 1f;
        public const float BigGoldChance = 20f;
        public const float HugeTankChance = 2f;
        public const float HugeGoldChance = 30f;
        public const float CreditsChance = 85f;
        public const float FreeXpChance = 65f;
        public const float HugeFreeXpChance = 70f;

        /// <summary>Godot <c>loot_pool_small</c> rows (id <c>small_container</c>) with the Unity drop chances.</summary>
        public static ContainerLoot SmallLoot()
        {
            var loot = new ContainerLoot("small_container", SmallTankChance, SmallGoldChance, CreditsChance, FreeXpChance)
                .AddTanks(ResolveTankName("t1_e6"), ResolveTankName("t2_e1"),
                    ResolveTankName("ha_go"), ResolveTankName("bt_7a"));
            AddGold(loot, 1000, 1, 750, 2, 500, 5, 200, 15, 50, 25);
            AddCredits(loot, 75000, 1, 50000, 2, 25000, 5, 15000, 15, 7500, 25);
            AddFreeXp(loot, 2000, 1, 1000, 2, 750, 5, 500, 15, 250, 25);
            return loot;
        }

        /// <summary>Godot <c>loot_pool_huge</c> rows (id <c>huge_container</c>) with the Unity drop chances.</summary>
        public static ContainerLoot BigLoot()
        {
            var loot = new ContainerLoot("huge_container", BigTankChance, BigGoldChance, CreditsChance, FreeXpChance)
                .AddTanks("t26_e4_superpershing", "t34_hvy", "ke_ni_b", "ltp");
            AddGold(loot, 10000, 1, 7500, 2, 3500, 5, 3500, 5, 3500, 5, 3500, 5,
                1500, 15, 1500, 15, 1500, 15, 750, 25);
            AddCredits(loot, 120000, 1, 80000, 2, 55000, 5, 25000, 5, 15000, 15, 15000, 15, 7500, 25);
            AddFreeXp(loot, 2000, 1, 1000, 2, 750, 5, 750, 5, 500, 15, 500, 15, 250, 25);
            return loot;
        }

        /// <summary>
        /// New Unity-only table for the 600 gold container: Tier VIII premium
        /// tanks, slightly better tank and gold chances than the big container,
        /// larger currency rows and the guaranteed tank every 10 openings.
        /// </summary>
        public static ContainerLoot HugeLoot()
        {
            var loot = new ContainerLoot("grand_container", HugeTankChance, HugeGoldChance, CreditsChance, HugeFreeXpChance)
                .AddTanks("t26_e4_superpershing", "t34_hvy", "lowe", "kv-5",
                    "is-2sh", "fcm_50t", "ch01_type59", "r112_t54_45");
            AddGold(loot, 20000, 1, 12500, 2, 7500, 5, 5000, 10, 2500, 20, 1500, 30);
            AddCredits(loot, 250000, 1, 150000, 2, 100000, 5, 60000, 10, 40000, 20, 25000, 30);
            AddFreeXp(loot, 5000, 1, 3000, 2, 2000, 5, 1500, 10, 1000, 20, 750, 30);
            return loot;
        }

        /// <summary>
        /// Small: Godot small pool for 65 000 credits. Big: Godot huge pool
        /// for 300 gold. Huge: the richer pool for 600 gold. Only the huge
        /// container keeps a guaranteed tank (progress bar).
        /// </summary>
        public static IReadOnlyList<ContainerOffer> ContainerOffers()
        {
            return new List<ContainerOffer>
            {
                new ContainerOffer(SmallContainerId, ShopCurrency.Credits, 65000, SmallLoot(), 0, "shop_chest_small"),
                new ContainerOffer(BigContainerId, ShopCurrency.Gold, 300, BigLoot(), 0, "shop_chest_big"),
                new ContainerOffer(HugeContainerId, ShopCurrency.Gold, 600, HugeLoot(), 10, "shop_chest_huge"),
            };
        }

        private static void AddGold(ContainerLoot loot, params int[] pairs)
        {
            for (var i = 0; i + 1 < pairs.Length; i += 2)
            {
                loot.Add(ContainerReward.ForGold(pairs[i], pairs[i + 1]));
            }
        }

        private static void AddCredits(ContainerLoot loot, params int[] pairs)
        {
            for (var i = 0; i + 1 < pairs.Length; i += 2)
            {
                loot.Add(ContainerReward.ForCredits(pairs[i], pairs[i + 1]));
            }
        }

        private static void AddFreeXp(ContainerLoot loot, params int[] pairs)
        {
            for (var i = 0; i + 1 < pairs.Length; i += 2)
            {
                loot.Add(ContainerReward.ForFreeXp(pairs[i], pairs[i + 1]));
            }
        }
    }
}
