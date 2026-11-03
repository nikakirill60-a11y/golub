using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// Hidden online reliability of an account. Never shown in statistics;
    /// it only decides who may host an online battle.
    /// </summary>
    public sealed class ReputationRecord
    {
        /// <summary>Online battles this account entered.</summary>
        public int Started;
        /// <summary>Battles played to the end, or left only after the tank was out for good.</summary>
        public int Clean;
        /// <summary>Battles left alive, or lost to a crash / killed app.</summary>
        public int Abandoned;
        /// <summary>Sum of the per-battle mean round trips, milliseconds.</summary>
        public double PingSum;
        public int PingBattles;
        /// <summary>
        /// A battle is open: set when it starts, cleared when it is settled.
        /// Still set on the next launch means the app died mid-battle.
        /// </summary>
        public bool Open;

        public ReputationRecord Clone() { return (ReputationRecord)MemberwiseClone(); }
    }

    /// <summary>Pure rules of the hidden reputation and of the host choice.</summary>
    public static class OnlineReputation
    {
        public const int Max = 1000;
        /// <summary>Below this a peer is never given the host while a cleaner human is present.</summary>
        public const int BadThreshold = 400;
        /// <summary>Score a new host must beat the current one by before a planned hand-over is worth its hiccup.</summary>
        public const int HandoverMargin = 120;

        /// <summary>Planned: the chosen successor starts at once.</summary>
        public const float PlannedTargetStart = 0.1f;
        /// <summary>Planned: the others start after the target had time to create the room.</summary>
        public const float PlannedFollowStart = 1.2f;
        /// <summary>Unplanned: start of the first survivor in the successor order.</summary>
        public const float SurvivorStart = 0.15f;
        /// <summary>Extra wait per place in the successor order.</summary>
        public const float SurvivorStep = 0.35f;
        /// <summary>Extra wait of a bad-reputation peer.</summary>
        public const float BadHostDelay = 1.5f;

        /// <summary>
        /// Seconds until a survivor first tries the hand-over room. Every try
        /// is a join-or-create, so whoever gets in first hosts: the target of
        /// a planned hand-over, otherwise the first clean survivor in the
        /// successor order; a bad-reputation peer comes last.
        /// </summary>
        public static float HandoverStartDelay(bool planned, bool isTarget, int rank, bool bad)
        {
            var penalty = bad ? BadHostDelay : 0f;
            var place = Math.Max(0, rank);
            if (planned && isTarget) { return PlannedTargetStart; }
            return (planned ? PlannedFollowStart : SurvivorStart) + place * SurvivorStep + penalty;
        }
        /// <summary>Unknown peers (nothing recorded yet) sit in the middle.</summary>
        public const int Neutral = 500;

        private const double GoodPingMs = 60;
        private const double BadPingMs = 300;
        private const double SeasonedHours = 20;

        /// <summary>The app died with a battle open: count it as abandoned.</summary>
        public static bool SettleCrash(ReputationRecord record)
        {
            if (record == null || !record.Open) { return false; }
            record.Open = false;
            record.Abandoned++;
            return true;
        }

        public static void BattleStarted(ReputationRecord record)
        {
            if (record == null) { return; }
            SettleCrash(record);
            record.Started++;
            record.Open = true;
        }

        /// <summary>Settles the open battle; <paramref name="meanPingMs"/> below zero means unknown.</summary>
        public static void BattleSettled(ReputationRecord record, bool clean, int meanPingMs)
        {
            if (record == null || !record.Open) { return; }
            record.Open = false;
            if (clean) { record.Clean++; } else { record.Abandoned++; }
            if (meanPingMs >= 0)
            {
                record.PingSum += Math.Min(meanPingMs, 2000);
                record.PingBattles++;
            }
        }

        /// <summary>
        /// 0..1000. The share of finished battles (smoothed, so one early leave
        /// does not sink a newcomer) multiplies everything else: a frequent
        /// quitter stays bad whatever its ping or time in game. The rest is
        /// 60% base, 20% mean ping, 20% total time in game.
        /// </summary>
        public static int Score(ReputationRecord record, double secondsInGame)
        {
            if (record == null) { return Neutral; }
            var reliability = (record.Clean + 3.0) / (record.Clean + record.Abandoned + 4.0);
            double ping = 0.5;
            if (record.PingBattles > 0)
            {
                var mean = record.PingSum / record.PingBattles;
                ping = 1.0 - Clamp01((mean - GoodPingMs) / (BadPingMs - GoodPingMs));
            }
            var seasoned = Clamp01(Math.Max(0, secondsInGame) / 3600.0 / SeasonedHours);
            var score = Max * reliability * (0.6 + 0.2 * ping + 0.2 * seasoned);
            return (int)Math.Round(Math.Max(0, Math.Min(Max, score)));
        }

        public static bool IsBad(int score) { return score < BadThreshold; }

        public static int Clamp(int score) { return Math.Max(0, Math.Min(Max, score)); }

        /// <summary>
        /// Who should host next, best first: connected humans only (never
        /// <paramref name="exclude"/>), alive tanks before tanks that are out
        /// for good, then reputation, then the lower PlayerId so every peer
        /// computes the same order.
        /// </summary>
        public static List<int> SuccessorOrder(BattleNetPlan plan, int exclude, Func<int, bool> aliveSlot)
        {
            var order = new List<int>();
            if (plan == null) { return order; }
            for (var i = 0; i < plan.Slots.Count; i++)
            {
                var slot = plan.Slots[i];
                if (!slot.IsHuman || slot.Owner < 0 || slot.Owner == exclude) { continue; }
                order.Add(i);
            }
            order.Sort((a, b) =>
            {
                var sa = plan.Slots[a];
                var sb = plan.Slots[b];
                var aliveA = aliveSlot == null || aliveSlot(a);
                var aliveB = aliveSlot == null || aliveSlot(b);
                if (aliveA != aliveB) { return aliveA ? -1 : 1; }
                var badA = IsBad(sa.Reputation);
                var badB = IsBad(sb.Reputation);
                if (badA != badB) { return badA ? 1 : -1; }
                if (sa.Reputation != sb.Reputation) { return sb.Reputation.CompareTo(sa.Reputation); }
                return sa.Owner.CompareTo(sb.Owner);
            });
            return order;
        }

        /// <summary>
        /// Host at the start of a battle: the slot that should take the host
        /// over, or -1 to keep it. A bad host always yields to a clean human;
        /// a clean host yields only to someone clearly better.
        /// </summary>
        public static int BetterHost(BattleNetPlan plan, int hostOwner)
        {
            if (plan == null) { return -1; }
            var own = plan.SlotOf(hostOwner);
            if (own < 0) { return -1; }
            var mine = plan.Slots[own].Reputation;
            var order = SuccessorOrder(plan, hostOwner, null);
            if (order.Count == 0) { return -1; }
            var best = plan.Slots[order[0]].Reputation;
            if (IsBad(best)) { return -1; }
            if (IsBad(mine)) { return order[0]; }
            return best - mine >= HandoverMargin ? order[0] : -1;
        }

        private static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
    }
}
