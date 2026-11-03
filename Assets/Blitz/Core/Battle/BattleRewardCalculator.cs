using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Save;

namespace BlitzLegacy.Core.Battle
{
    public sealed class BattleXpInteraction
    {
        public int TargetLevel;
        public int Damage;
        public bool Spotted;
        public bool Killed;
    }

    public sealed class BattleRewardInput
    {
        public string Id = string.Empty;
        public string MapId = string.Empty;
        public string TankId = string.Empty;
        public string PlayerName = string.Empty;
        public string TankName = string.Empty;
        public double FinishedAt;
        public float DurationSeconds;
        public BattleOutcome Outcome;
        public BattleFinishReason FinishReason;
        public int TankLevel = 1;
        public float FarmCoefficient = 1f;
        public bool PremiumTank;
        public bool PremiumAccount;
        public bool CourageousResistance;
        public bool HadFirstVictory;
        public bool HadFirstBlood;
        public bool HadWarrior;
        public int HighestAllyKills;
        public int PlayerTankClass = -1;
        public int MaxTeamTanks = 7;
        public int PiercerStreaks, KolobanovKills, TarczayKills, BaseTicksDefendedMax;
        public bool SourceBetaBuild, HadBetaTester;
        public int PreviousTankBattles, MasteryReferenceXp;

        public int Spots;
        public int BaseTicks;
        public int Damage;
        public int DamageTaken;
        public int DamageBlocked;
        public int Kills;
        public int Shots;
        public int Hits;
        public readonly List<BattleXpInteraction> Interactions = new List<BattleXpInteraction>();
        public int AmmoCost;
        public int ConsumableCost;
        public int RepairCost;
        public bool Survived;
        public readonly List<BattleParticipantResult> Participants =
            new List<BattleParticipantResult>();
    }

    /// <summary>
    /// Formula port from player.gd, _battle.gd, account.gd and _player_data.gd.
    /// Conversion to int intentionally truncates the source floating totals.
    /// </summary>
    public static class BattleRewardCalculator
    {
        public const float SpotXp = 90f;
        public const float DamageXp = 0.25f;
        public const float KillXp = 50f;
        public const float BaseTickXp = 20f;
        public const float BlockedXp = 0.35f;
        public const float PremiumTankXpMultiplier = 1.5f;
        public const float VictoryXpMultiplier = 1.5f;
        public const float VictoryCreditsMultiplier = 1.85f;
        public const float CourageousResistanceMultiplier = 1.5f;
        public const float PremiumAccountCreditsMultiplier = 1.5f;
        public const float FreeXpRatio = 0.05f;

        public static BattleResultData Calculate(BattleRewardInput input)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            var result = new BattleResultData
            {
                Id = input.Id ?? string.Empty,
                MapId = input.MapId ?? string.Empty,
                TankId = input.TankId ?? string.Empty,
                FinishedAt = input.FinishedAt,
                DurationSeconds = Math.Max(0f, input.DurationSeconds),
                Outcome = input.Outcome,
                FinishReason = input.FinishReason,
                PremiumAccount = input.PremiumAccount,
            };

            var rawXp = RawXp(input.TankLevel, input.Spots, input.Damage,
                input.Kills, input.BaseTicks, input.DamageBlocked, input.Interactions);
            var xp = rawXp;
            if (input.PremiumTank)
            {
                xp *= PremiumTankXpMultiplier;
            }
            if (input.Outcome == BattleOutcome.Victory)
            {
                xp *= VictoryXpMultiplier;
            }
            result.Xp = Math.Max(0, (int)xp);
            result.FreeXp = Math.Max(0, (int)(result.Xp * FreeXpRatio));

            var tier = Math.Max(1, input.TankLevel);
            var baseCredits = 800f * tier;
            if (input.Outcome == BattleOutcome.Victory)
            {
                baseCredits *= VictoryCreditsMultiplier;
            }
            else if (input.CourageousResistance)
            {
                baseCredits *= CourageousResistanceMultiplier;
            }
            var gross = (baseCredits
                + input.Spots * 800f
                + input.BaseTicks * 250f
                + input.Damage * 10f
                + input.DamageBlocked * 20f)
                * Math.Max(0f, input.FarmCoefficient);
            result.CreditsEarned = Math.Max(0, (int)gross);
            result.CreditCosts = Math.Max(0,
                input.AmmoCost + input.ConsumableCost + input.RepairCost);
            result.Credits = result.CreditsEarned - result.CreditCosts;
            result.Gold = result.Credits > 0
                ? Math.Max(0, (int)Math.Floor(result.CreditsEarned * 0.001d))
                : 0;

            // account.gd multiplies POSITIVE NET credits, after expenses;
            // credits_raw and gold's 0.1% calculation remain pre-premium.
            if (input.PremiumAccount && result.Credits > 0)
                result.Credits = (int)(result.Credits * PremiumAccountCreditsMultiplier);

            if (input.Outcome == BattleOutcome.Victory && !input.HadFirstVictory)
            {
                AddReward(result, "first_victory", 250, 3);
            }
            if (input.Kills >= 4 && input.Kills >= input.HighestAllyKills)
            {
                AddReward(result, "warrior", 150, 4);
            }

            // _player_data.gd registry validators. Inactive source validators
            // (fadin/bombardier) stay inactive; do not invent award conditions.
            if (input.SourceBetaBuild && !input.HadBetaTester) AddReward(result, "beta_tester", 500, 1);
            if (input.Kills > 0 && !input.HadFirstBlood)
            {
                AddReward(result, "first_blood", 250, 2);
            }
            if (input.BaseTicksDefendedMax >= 70) AddReward(result, "defender", 300, 1);
            for (var series = 0; series < input.PiercerStreaks; series++) AddReward(result, "armor_piercer", 20, 0);
            if (input.PreviousTankBattles > 10)
            {
                // The source skips resolved history. Zero reference follows its
                // IEEE float division (positive/0 => infinity), NOT percentiles.
                var mastery = xp / (double)input.MasteryReferenceXp - 1;
                if (mastery > .99) AddReward(result, "mastery_mark_m", 400, 1);
                else if (mastery > .95) AddReward(result, "mastery_mark_1", 300, 1);
                else if (mastery > .8) AddReward(result, "mastery_mark_2", 200, 1);
                else if (mastery > .5) AddReward(result, "mastery_mark_3", 125, 1);
            }
            if (input.KolobanovKills >= 3) AddReward(result, "kolobanov", 500, 1);
            var teamMax = Math.Max(input.MaxTeamTanks, 7);
            if (input.Kills == teamMax) AddReward(result, "rassenay", teamMax * 75, 1);
            if (input.Kills == 6) AddReward(result, "lafayette_pool", 200, 1);
            if (input.Kills == 5) AddReward(result, "radley_walters", 150, 1);
            if (input.TarczayKills > 5) AddReward(result, "tarzcay", 2500, 1);
            else if (input.TarczayKills > 3) AddReward(result, "bruno_pietro", 1700, 1);
            else if (input.TarczayKills > 2) AddReward(result, "billotte", 750, 1);
            if (input.PlayerTankClass == (int)BlitzLegacy.Core.Tanks.TankClass.Medium)
            {
                if (input.Kills == 2) AddReward(result, "lehvaslaiho", 50, 0);
                if (input.Kills == 3) AddReward(result, "oskin", 75, 0);
                if (input.Kills >= 4) AddReward(result, "nikolas", 150, 1);
            }
            if (input.PlayerTankClass == (int)BlitzLegacy.Core.Tanks.TankClass.Light && input.Kills >= 3)
                AddReward(result, "orlik", 100, 0);

            for (var i = 0; i < input.Participants.Count; i++)
            {
                result.Participants.Add(input.Participants[i]);
            }
            if (result.Player == null)
            {
                result.Participants.Add(new BattleParticipantResult
                {
                    Name = input.PlayerName ?? string.Empty,
                    TankId = input.TankId ?? string.Empty,
                    TankName = input.TankName ?? string.Empty,
                    IsPlayer = true,
                    Team = 0,
                    Survived = input.Survived,
                    Spots = input.Spots,
                    BaseTicks = input.BaseTicks,
                    Damage = input.Damage,
                    DamageTaken = input.DamageTaken,
                    DamageBlocked = input.DamageBlocked,
                    Kills = input.Kills,
                    Shots = input.Shots,
                    Hits = input.Hits,
                    Xp = input.PremiumAccount ? (int)(result.Xp * 1.5f) : result.Xp,
                    XpRaw = (int)rawXp,
                });
            }
            else
            {
                result.Player.Xp = input.PremiumAccount ? (int)(result.Xp * 1.5f) : result.Xp;
                result.Player.XpRaw = (int)rawXp;
            }
            return result;
        }

        public static float RawXp(int level, int spots, int damage, int kills,
            int baseTicks, int blocked, IEnumerable<BattleXpInteraction> interactions)
        {
            var xp = spots * SpotXp + damage * DamageXp + kills * KillXp
                + baseTicks * BaseTickXp + blocked * BlockedXp;
            if (interactions != null)
                foreach (var target in interactions)
                {
                    var diff = level - target.TargetLevel;
                    // Godot explicitly uses maxi: fractional coefficients truncate.
                    if (target.Spotted) xp += SpotXp * (Math.Max(1, (int)(1 + diff * .4f)) - 1);
                    xp += target.Damage * DamageXp * (Math.Max(1, (int)(1 + diff * .2f)) - 1);
                    if (target.Killed) xp += KillXp * (Math.Max(1, (int)(1 + diff * .1f)) - 1);
                }
            return xp;
        }

        private static void AddReward(BattleResultData result, string id, int gold, int rarity)
        {
            result.Rewards.Add(new BattleRewardData { Id = id, Gold = gold, Rarity = rarity });
            result.Gold += gold;
        }
    }

    /// <summary>Applies a composed result to a save exactly once.</summary>
    public static class BattleResultApplier
    {
        public static bool Apply(AccountData account, BattleResultData result)
        {
            if (account == null || result == null || string.IsNullOrEmpty(result.Id))
            {
                return false;
            }
            for (var i = 0; i < account.BattleResults.Count; i++)
            {
                if (string.Equals(account.BattleResults[i].Id, result.Id,
                    StringComparison.Ordinal))
                {
                    return false;
                }
            }

            account.Credits = AccountData.AddCurrency(account.Credits, result.Credits);
            account.Gold = AccountData.AddCurrency(account.Gold, result.Gold);
            account.FreeXp = AccountData.AddCurrency(account.FreeXp, result.FreeXp);
            account.StatBattles++;
            if (result.Outcome == BattleOutcome.Victory)
            {
                account.StatWins++;
            }

            var player = result.Player;
            if (player != null)
            {
                account.StatKills += player.Kills;
                account.StatMaxKills = Math.Max(account.StatMaxKills, player.Kills);
                account.StatDamageDealt += player.Damage;
                account.StatDamageTaken += player.DamageTaken;
                account.StatShellsShot += player.Shots;
                account.StatShellsHits += player.Hits;
            }

            var tank = account.GetTank(result.TankId);
            if (tank != null)
            {
                tank.Exp += result.Xp;
                tank.StatBattles++;
                if (player != null)
                {
                    tank.StatKills += player.Kills;
                    tank.StatMaxKills = Math.Max(tank.StatMaxKills, player.Kills);
                    tank.StatMaxXp = Math.Max(tank.StatMaxXp, player.Xp);
                    tank.StatDamageDealt += player.Damage;
                    tank.StatDamageTaken += player.DamageTaken;
                    tank.StatShellsShot += player.Shots;
                    tank.StatShellsHits += player.Hits;
                }
            }

            for (var i = 0; i < result.Rewards.Count; i++)
            {
                var reward = result.Rewards[i];
                Increment(account.Rewards, reward.Id);
                if (reward.Id == "beta_tester") account.AddTank("kv-220", true);
                if (tank != null)
                {
                    Increment(tank.Rewards, reward.Id);
                }
                // Rewards and progressive achievements are separate source registries.
            }
            account.BattleResults.Add(result);
            return true;
        }

        private static void Increment(Dictionary<string, int> values, string key)
        {
            int count;
            values[key] = values.TryGetValue(key, out count) ? count + 1 : 1;
        }
    }
}
