using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Rng;

namespace BlitzLegacy.Core.Shop
{
    /// <summary>Godot <c>ContainerLoot.LootType</c>, same order.</summary>
    public enum ContainerRewardType
    {
        Tank = 0,
        Gold = 1,
        Credits = 2,
        FreeXp = 3,
    }

    /// <summary>One Godot loot dictionary: <c>{ value, weight, type }</c>.</summary>
    public sealed class ContainerReward
    {
        public ContainerReward(ContainerRewardType type, int amount, string tank, int weight)
        {
            Type = type;
            Amount = amount;
            Tank = tank ?? string.Empty;
            // Godot entry(): weight = maxi(weight, 1).
            Weight = Math.Max(weight, 1);
        }

        public ContainerRewardType Type { get; private set; }

        /// <summary>Currency amount; zero for tank rewards.</summary>
        public int Amount { get; private set; }

        /// <summary>Tank card name; empty for currency rewards.</summary>
        public string Tank { get; private set; }

        public int Weight { get; private set; }

        public static ContainerReward ForTank(string tank, int weight)
        {
            return new ContainerReward(ContainerRewardType.Tank, 0, tank, weight);
        }

        public static ContainerReward ForGold(int amount, int weight)
        {
            return new ContainerReward(ContainerRewardType.Gold, amount, null, weight);
        }

        public static ContainerReward ForCredits(int amount, int weight)
        {
            return new ContainerReward(ContainerRewardType.Credits, amount, null, weight);
        }

        public static ContainerReward ForFreeXp(int amount, int weight)
        {
            return new ContainerReward(ContainerRewardType.FreeXp, amount, null, weight);
        }

        public override string ToString()
        {
            return Type == ContainerRewardType.Tank ? "Tank:" + Tank : Type + ":" + Amount;
        }
    }

    /// <summary>
    /// Based on Godot <c>shop.gd ContainerLoot</c>: a weighted table per
    /// reward type. Every type drops on its own percent chance: credits
    /// (<see cref="CreditsChance"/>), free XP (<see cref="FreeXpChance"/>),
    /// a tank (<see cref="TankChance"/>, or for sure when the guaranteed
    /// counter fires) and gold (<see cref="GoldChance"/>). An opening is never
    /// empty: when no row dropped, the credits row (or the free XP row) is
    /// granted. RNG order: four <c>randi_range</c> rolls (tank, gold,
    /// credits, free XP), then the credits and free XP chances (skipped at
    /// 100 %, which keeps the Godot sequence), the tank chance (skipped when
    /// guaranteed or when no tank is left) and the gold chance.
    /// </summary>
    public sealed class ContainerLoot
    {
        private readonly List<ContainerReward> _rewards = new List<ContainerReward>();

        public ContainerLoot(string guaranteedRewardId, float tankChance, float goldChance)
            : this(guaranteedRewardId, tankChance, goldChance, 100f, 100f)
        {
        }

        public ContainerLoot(
            string guaranteedRewardId, float tankChance, float goldChance,
            float creditsChance, float freeXpChance)
        {
            GuaranteedRewardId = guaranteedRewardId ?? string.Empty;
            TankChance = tankChance;
            GoldChance = goldChance;
            CreditsChance = creditsChance;
            FreeXpChance = freeXpChance;
        }

        /// <summary>Key in <c>AccountData.ShopGuaranteedRewards</c>.</summary>
        public string GuaranteedRewardId { get; private set; }

        /// <summary>Percent, Godot <c>tank_chance</c>.</summary>
        public float TankChance { get; private set; }

        /// <summary>Percent, Godot <c>gold_chance</c>.</summary>
        public float GoldChance { get; private set; }

        /// <summary>Percent; 100 keeps the Godot "always" behaviour.</summary>
        public float CreditsChance { get; private set; }

        /// <summary>Percent; 100 keeps the Godot "always" behaviour.</summary>
        public float FreeXpChance { get; private set; }

        public IReadOnlyList<ContainerReward> Rewards
        {
            get { return _rewards; }
        }

        public ContainerLoot Add(ContainerReward reward)
        {
            if (reward != null)
            {
                _rewards.Add(reward);
            }

            return this;
        }

        public ContainerLoot AddTanks(params string[] tanks)
        {
            for (var i = 0; i < tanks.Length; i++)
            {
                Add(ContainerReward.ForTank(tanks[i], 1));
            }

            return this;
        }

        /// <summary>Godot <c>get_tanks_amount()</c>.</summary>
        public int TanksAmount
        {
            get
            {
                var count = 0;
                for (var i = 0; i < _rewards.Count; i++)
                {
                    if (_rewards[i].Type == ContainerRewardType.Tank)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public IEnumerable<string> Tanks
        {
            get
            {
                for (var i = 0; i < _rewards.Count; i++)
                {
                    if (_rewards[i].Type == ContainerRewardType.Tank)
                    {
                        yield return _rewards[i].Tank;
                    }
                }
            }
        }

        /// <summary>
        /// Copy without the tank rows <paramref name="keep"/> rejects, so a
        /// pool never hands out a card the database does not know.
        /// </summary>
        public ContainerLoot Where(Func<string, bool> keep)
        {
            var copy = new ContainerLoot(GuaranteedRewardId, TankChance, GoldChance, CreditsChance, FreeXpChance);
            for (var i = 0; i < _rewards.Count; i++)
            {
                var reward = _rewards[i];
                if (reward.Type != ContainerRewardType.Tank || keep == null || keep(reward.Tank))
                {
                    copy._rewards.Add(reward);
                }
            }

            return copy;
        }

        /// <summary>Godot <c>get_random_rewards(guaranteed_tank, exclude)</c>.</summary>
        public List<ContainerReward> GetRandomRewards(
            bool guaranteedTank, ICollection<string> excludedTanks, IRandomSource random)
        {
            if (random == null)
            {
                throw new ArgumentNullException("random");
            }

            var tanks = new List<ContainerReward>();
            var gold = new List<ContainerReward>();
            var credits = new List<ContainerReward>();
            var freeXp = new List<ContainerReward>();
            for (var i = 0; i < _rewards.Count; i++)
            {
                var reward = _rewards[i];
                switch (reward.Type)
                {
                    case ContainerRewardType.Tank:
                        // exclude.has(r.value) only ever matches tank names.
                        if (excludedTanks == null || !excludedTanks.Contains(reward.Tank))
                        {
                            tanks.Add(reward);
                        }

                        break;
                    case ContainerRewardType.Gold:
                        gold.Add(reward);
                        break;
                    case ContainerRewardType.Credits:
                        credits.Add(reward);
                        break;
                    default:
                        freeXp.Add(reward);
                        break;
                }
            }

            var tank = GetWeighted(tanks, random);
            var goldReward = GetWeighted(gold, random);
            var creditsReward = GetWeighted(credits, random);
            var freeXpReward = GetWeighted(freeXp, random);

            var result = new List<ContainerReward>(4);
            var creditsDrop = Rolls(random, CreditsChance);
            var freeXpDrop = Rolls(random, FreeXpChance);
            if (creditsReward != null && creditsDrop)
            {
                result.Add(creditsReward);
            }

            if (freeXpReward != null && freeXpDrop)
            {
                result.Add(freeXpReward);
            }

            if (tank != null && (guaranteedTank || random.Chance(TankChance)))
            {
                result.Add(tank);
            }

            if (random.Chance(GoldChance) && goldReward != null)
            {
                result.Add(goldReward);
            }

            // A container is never empty.
            if (result.Count == 0)
            {
                var fallback = creditsReward ?? freeXpReward;
                if (fallback != null)
                {
                    result.Add(fallback);
                }
            }

            return result;
        }

        private static bool Rolls(IRandomSource random, float percent)
        {
            return percent >= 100f || random.Chance(percent);
        }

        /// <summary>
        /// Chance in percent that one opening contains a row of
        /// <paramref name="type"/>, without the guaranteed counter.
        /// </summary>
        public float DropChance(ContainerRewardType type)
        {
            switch (type)
            {
                case ContainerRewardType.Tank: return TankChance;
                case ContainerRewardType.Gold: return GoldChance;
                case ContainerRewardType.Credits: return CreditsChance;
                default: return FreeXpChance;
            }
        }

        /// <summary>Godot <c>get_weighted()</c>: one roll even for an empty table.</summary>
        public static ContainerReward GetWeighted(IList<ContainerReward> table, IRandomSource random)
        {
            var total = 0;
            for (var i = 0; i < table.Count; i++)
            {
                total += table[i].Weight;
            }

            var roll = total > 0 ? random.RangeInclusive(1, total) : random.RangeInclusive(0, 0);
            for (var i = 0; i < table.Count; i++)
            {
                roll -= table[i].Weight;
                if (roll <= 0)
                {
                    return table[i];
                }
            }

            return null;
        }

        /// <summary>Probability of one table row inside its type, for the UI and tests.</summary>
        public float RowShare(ContainerReward reward)
        {
            if (reward == null)
            {
                return 0f;
            }

            var total = 0;
            for (var i = 0; i < _rewards.Count; i++)
            {
                if (_rewards[i].Type == reward.Type)
                {
                    total += _rewards[i].Weight;
                }
            }

            return total > 0 ? reward.Weight / (float)total : 0f;
        }
    }
}
