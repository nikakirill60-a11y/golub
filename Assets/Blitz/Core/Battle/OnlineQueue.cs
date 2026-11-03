using System;
using System.Collections.Generic;
using System.Text;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// The shared matchmaking queue: every open online battle advertises its
    /// make-up (ten tier counts, tier X first, then four class counts) in the
    /// lobby, and the queue screen sums all of them so a player sees who waits
    /// on every tier, not only inside the own room.
    /// </summary>
    public static class OnlineQueue
    {
        public const int Tiers = 10;
        public const int Classes = 4;
        public const int MixLength = Tiers + Classes;

        /// <summary>Upper bound of any single count read from the lobby (one room holds 14 humans).</summary>
        public const int MaxPerRoom = 14;

        /// <summary>One open battle as a lobby watcher sees it.</summary>
        public struct Room
        {
            public int Players;
            /// <summary>Host tier 1..10; used when the room has no make-up string.</summary>
            public int Tier;
            /// <summary>The advertised make-up (<see cref="Encode"/>), may be null or garbage.</summary>
            public string Mix;
        }

        public sealed class Snapshot
        {
            public readonly int[] TierCounts = new int[Tiers];
            public readonly int[] ClassCounts = new int[Classes];
            public int Waiting;
            public int Rooms;
        }

        /// <summary>"t10,...,t1,ht,mt,lt,td" as a short comma separated string.</summary>
        public static string Encode(IReadOnlyList<int> tiers, IReadOnlyList<int> classes)
        {
            var builder = new StringBuilder(40);
            for (var i = 0; i < MixLength; i++)
            {
                if (i > 0) { builder.Append(','); }
                int value;
                if (i < Tiers) { value = tiers != null && i < tiers.Count ? tiers[i] : 0; }
                else { value = classes != null && i - Tiers < classes.Count ? classes[i - Tiers] : 0; }
                builder.Append(Clamp(value));
            }
            return builder.ToString();
        }

        /// <summary>
        /// SECURITY: the string comes from any client in the lobby. Only plain
        /// digits and commas are accepted, exactly 14 fields, each 0..14;
        /// anything else is rejected as a whole.
        /// </summary>
        public static bool TryDecode(string mix, int[] tiers, int[] classes)
        {
            if (string.IsNullOrEmpty(mix) || mix.Length > MixLength * 3 + 2) { return false; }
            var values = new int[MixLength];
            var field = 0;
            var value = 0;
            var digits = 0;
            for (var i = 0; i <= mix.Length; i++)
            {
                var end = i == mix.Length;
                var c = end ? ',' : mix[i];
                if (c == ',')
                {
                    if (digits == 0 || field >= MixLength) { return false; }
                    values[field++] = value;
                    value = 0;
                    digits = 0;
                    continue;
                }
                if (c < '0' || c > '9' || digits >= 2) { return false; }
                value = value * 10 + (c - '0');
                digits++;
                if (value > MaxPerRoom) { return false; }
            }
            if (field != MixLength) { return false; }
            for (var i = 0; i < Tiers; i++) { tiers[i] = values[i]; }
            for (var i = 0; i < Classes; i++) { classes[i] = values[Tiers + i]; }
            return true;
        }

        /// <summary>Sums the open battles into one queue view.</summary>
        public static Snapshot Aggregate(IEnumerable<Room> rooms)
        {
            var snapshot = new Snapshot();
            if (rooms == null) { return snapshot; }
            var tiers = new int[Tiers];
            var classes = new int[Classes];
            foreach (var room in rooms)
            {
                var players = Math.Max(0, Math.Min(MaxPerRoom, room.Players));
                if (players == 0) { continue; }
                snapshot.Rooms++;
                snapshot.Waiting += players;
                if (TryDecode(room.Mix, tiers, classes) && Sum(tiers) == players)
                {
                    for (var i = 0; i < Tiers; i++) { snapshot.TierCounts[i] += tiers[i]; }
                    for (var i = 0; i < Classes; i++) { snapshot.ClassCounts[i] += classes[i]; }
                }
                else if (room.Tier >= 1 && room.Tier <= Tiers)
                {
                    // Make-up missing, stale or forged: count the seats at the host tier.
                    snapshot.TierCounts[Tiers - room.Tier] += players;
                }
            }
            return snapshot;
        }

        /// <summary>
        /// The lobby list lags a few seconds behind the own room; the screen
        /// shows the larger of both per cell so it never undercounts the room.
        /// </summary>
        public static void Merge(int[] into, IReadOnlyList<int> room)
        {
            for (var i = 0; into != null && room != null && i < into.Length && i < room.Count; i++)
            {
                into[i] = Math.Max(into[i], room[i]);
            }
        }

        private static int Sum(int[] values)
        {
            var total = 0;
            for (var i = 0; i < values.Length; i++) { total += values[i]; }
            return total;
        }

        private static int Clamp(int value)
        {
            return value < 0 ? 0 : value > MaxPerRoom ? MaxPerRoom : value;
        }
    }
}
