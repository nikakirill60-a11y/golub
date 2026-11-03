using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Save;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>account.gd get_avg_battle_stats/get_wr/get_rating, evaluated from history.</summary>
    public sealed class AccountBattleStatistics
    {
        public int Battles, Wins, MaxXp, MaxKills, Rating;
        public double WinRate, AverageDamage, AverageSpots, AverageKills, AverageBlocked;
        public double AverageHitRatio, AverageXp;

        public static AccountBattleStatistics Calculate(AccountData account)
        {
            var stats = new AccountBattleStatistics();
            if (account == null || account.BattleResults.Count == 0) return stats;
            stats.Battles = account.BattleResults.Count;
            var survived = 0;
            foreach (var result in account.BattleResults)
            {
                if (result.Outcome == BattleOutcome.Victory) stats.Wins++;
                var player = result.Player;
                if (player == null) continue;
                stats.AverageDamage += player.Damage;
                stats.AverageSpots += player.Spots;
                stats.AverageKills += player.Kills;
                // Source get_def() sums log_damage_received, despite its name.
                stats.AverageBlocked += player.DamageTaken;
                stats.AverageHitRatio += player.Shots > 0 ? Math.Min(1d, (double)player.Hits / player.Shots) : 0;
                stats.AverageXp += player.XpRaw;
                stats.MaxXp = Math.Max(stats.MaxXp, player.XpRaw);
                stats.MaxKills = Math.Max(stats.MaxKills, player.Kills);
                if (player.Survived) survived++;
            }
            var count = (double)stats.Battles;
            stats.WinRate = 100d * stats.Wins / count;
            stats.AverageDamage /= count; stats.AverageSpots /= count;
            stats.AverageKills /= count; stats.AverageBlocked /= count;
            stats.AverageHitRatio /= count; stats.AverageXp /= count;
            // Preserve the recovered source formula, including its existing quirks:
            // get_wr returns PERCENT and rating's local xp accumulator stays zero.
            // Do not silently replace this with a different rating model.
            var power = Math.Pow(count, .37);
            stats.Rating = (int)(540 * power * Math.Tanh(.00163 / power *
                (3500 / (1 + Math.Exp(16 - 31 * stats.WinRate))
                    + 1400 / (1 + Math.Exp(8 - 27 * survived / count)))));
            return stats;
        }

        public static IReadOnlyList<BattleResultData> RecentPage(AccountData account, int page)
        {
            var rows = new List<BattleResultData>();
            if (account == null) return rows;
            page = Math.Max(1, Math.Min(PageCount(account), page));
            var from = account.BattleResults.Count - 1 - (page - 1) * 10;
            for (var index = from; index >= 0 && rows.Count < 10; index--)
                rows.Add(account.BattleResults[index]);
            return rows;
        }

        public static int PageCount(AccountData account)
        {
            return Math.Max(1, ((account == null ? 0 : account.BattleResults.Count) + 9) / 10);
        }
    }
}
