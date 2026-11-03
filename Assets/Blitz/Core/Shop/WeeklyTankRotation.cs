using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Shop
{
    /// <summary>One tank card in the weekly shop list.</summary>
    public sealed class TankOffer
    {
        public TankOffer(TankInfo tank, int costGold, bool collector)
        {
            Tank = tank;
            CostGold = costGold;
            Collector = collector;
        }

        public TankInfo Tank { get; private set; }

        public string Name
        {
            get { return Tank != null ? Tank.Name : string.Empty; }
        }

        public int Level
        {
            get { return Tank != null ? Tank.Level : 0; }
        }

        public int CostGold { get; private set; }

        public bool Collector { get; private set; }
    }

    /// <summary>
    /// Shop prices for the weekly tanks. The 4.8.0 <c>cost_gold</c> values
    /// are uneven (Blitz Pass collectibles are priced at 500), so the shop
    /// uses one table per tier instead: premium vehicles are always cheaper
    /// than collector vehicles of the same tier.
    /// </summary>
    public static class ShopPricing
    {
        public const int MinLevel = 6;
        public const int MaxLevel = 10;

        // Index = tier - MinLevel.
        private static readonly int[] PremiumPrices = { 2500, 4000, 6000, 9000, 13000 };
        private static readonly int[] CollectorPrices = { 3500, 5500, 8500, 12500, 18000 };

        public static bool IsCollector(TankInfo tank)
        {
            return tank != null && (tank.Collector ||
                string.Equals(tank.Acquisition, "collectible", StringComparison.Ordinal));
        }

        public static int TankPrice(int level, bool collector)
        {
            var index = Math.Min(Math.Max(level, MinLevel), MaxLevel) - MinLevel;
            return collector ? CollectorPrices[index] : PremiumPrices[index];
        }

        public static int TankPrice(TankInfo tank)
        {
            return tank == null ? 0 : TankPrice(tank.Level, IsCollector(tank));
        }
    }

    /// <summary>
    /// Client-side weekly tank list. Every client computes the same list from
    /// the week number alone, so no server is needed; the week number must
    /// come from network time (<c>OnlineClock</c>), never from the device
    /// clock, or moving the clock would reroll the shop.
    /// </summary>
    public static class WeeklyTankRotation
    {
        public const long WeekSeconds = 7L * 24L * 3600L;

        /// <summary>Monday 1970-01-05 00:00:00 UTC.</summary>
        public const long EpochMondayUnix = 4L * 24L * 3600L;

        public const int PremiumsPerLevel = 2;
        public const int CollectorsPerLevel = 1;

        private const ulong Salt = 0x5B11A7C0FFEE2025UL;

        public static long WeekIndex(double unixSeconds)
        {
            return (long)Math.Floor((unixSeconds - EpochMondayUnix) / WeekSeconds);
        }

        public static double WeekStartUnix(long week)
        {
            return EpochMondayUnix + (week * (double)WeekSeconds);
        }

        /// <summary>Unix time of the next Monday 00:00 UTC.</summary>
        public static double NextRotationUnix(double unixSeconds)
        {
            return WeekStartUnix(WeekIndex(unixSeconds) + 1);
        }

        public static TimeSpan TimeLeft(double unixSeconds)
        {
            return TimeSpan.FromSeconds(Math.Max(0d, NextRotationUnix(unixSeconds) - unixSeconds));
        }

        /// <summary>
        /// Cards the rotation may pick: Tier VI-X premium vehicles that are
        /// visible, not mods and not 4.8.0 event specials (<c>oth*</c>), and
        /// that <paramref name="available"/> accepts (icon and model present).
        /// </summary>
        public static bool IsCandidate(TankInfo tank, Func<TankInfo, bool> available)
        {
            if (tank == null || (!tank.Premium && !ShopPricing.IsCollector(tank)) || tank.Hidden || tank.Mod)
            {
                return false;
            }

            if (tank.Level < ShopPricing.MinLevel || tank.Level > ShopPricing.MaxLevel)
            {
                return false;
            }

            if (tank.Name.StartsWith("oth", StringComparison.Ordinal))
            {
                return false;
            }

            return available == null || available(tank);
        }

        /// <summary>The list for one week, ordered by tier, premiums first.</summary>
        public static List<TankOffer> ForWeek(
            IEnumerable<TankInfo> tanks, long week, Func<TankInfo, bool> available = null)
        {
            var premiums = new List<TankInfo>[ShopPricing.MaxLevel + 1];
            var collectors = new List<TankInfo>[ShopPricing.MaxLevel + 1];
            for (var level = ShopPricing.MinLevel; level <= ShopPricing.MaxLevel; level++)
            {
                premiums[level] = new List<TankInfo>();
                collectors[level] = new List<TankInfo>();
            }

            if (tanks != null)
            {
                foreach (var tank in tanks)
                {
                    if (!IsCandidate(tank, available))
                    {
                        continue;
                    }

                    (ShopPricing.IsCollector(tank) ? collectors : premiums)[tank.Level].Add(tank);
                }
            }

            var state = Mix((ulong)week ^ Salt);
            var result = new List<TankOffer>();
            for (var level = ShopPricing.MinLevel; level <= ShopPricing.MaxLevel; level++)
            {
                Pick(premiums[level], PremiumsPerLevel, false, ref state, result);
                Pick(collectors[level], CollectorsPerLevel, true, ref state, result);
            }

            return result;
        }

        public static List<TankOffer> ForTime(
            IEnumerable<TankInfo> tanks, double unixSeconds, Func<TankInfo, bool> available = null)
        {
            return ForWeek(tanks, WeekIndex(unixSeconds), available);
        }

        /// <summary>
        /// Partial Fisher-Yates over the name-sorted pool, so the pick only
        /// depends on the week and the set of candidate names, not on the
        /// order the database was loaded in.
        /// </summary>
        private static void Pick(
            List<TankInfo> pool, int count, bool collector, ref ulong state, List<TankOffer> result)
        {
            pool.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            var take = Math.Min(count, pool.Count);
            for (var i = 0; i < take; i++)
            {
                state = Mix(state + 0x9E3779B97F4A7C15UL);
                var j = i + (int)(state % (ulong)(pool.Count - i));
                var swap = pool[i];
                pool[i] = pool[j];
                pool[j] = swap;
            }

            var picked = pool.GetRange(0, take);
            picked.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            for (var i = 0; i < picked.Count; i++)
            {
                result.Add(new TankOffer(picked[i], ShopPricing.TankPrice(picked[i].Level, collector), collector));
            }
        }

        /// <summary>SplitMix64 finaliser.</summary>
        private static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
