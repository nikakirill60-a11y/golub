using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// Online matchmaking rules. There is no server: every searching player
    /// sees the open battles of its build (Photon lobby) and picks one with
    /// these rules, or opens a new battle at its own tier. The host then
    /// splits the humans it got into two even teams.
    /// </summary>
    public static class OnlineBalancer
    {
        /// <summary>Fresh searches only take tanks one tier either way.</summary>
        public const int InitialTierSpread = 1;

        /// <summary>After a long wait, matchmaking widens to two tiers either way.</summary>
        public const int ExpandedTierSpread = 2;

        /// <summary>Seconds in queue before the wider tier range is used.</summary>
        public const float ExpandAfterSeconds = 45f;

        /// <summary>Joining a battle about to start is pointless: it may already be gone.</summary>
        public const float MinSecondsToJoin = 3f;

        /// <summary>One open battle, as the lobby lists it.</summary>
        public struct Room
        {
            public string Name;
            public int Tier;
            public int Players;
            public int Capacity;
            /// <summary>Seconds until start, negative while waiting for a second player.</summary>
            public float StartsIn;
        }

        public static int TierSpreadFor(float waitedSeconds)
        {
            return waitedSeconds >= ExpandAfterSeconds ? ExpandedTierSpread : InitialTierSpread;
        }

        public static bool Accepts(int battleTier, int tankTier, int tierSpread = InitialTierSpread)
        {
            return battleTier >= 1 && tierSpread >= 0 && Math.Abs(battleTier - tankTier) <= tierSpread;
        }

        /// <summary>
        /// Higher is better, <see cref="float.NegativeInfinity"/> when the room
        /// cannot take this tank. Same tier first, then the fuller room (it
        /// starts sooner and fills the battle with people instead of bots).
        /// </summary>
        public static float Score(Room room, int tankTier, int tierSpread = InitialTierSpread)
        {
            if (!Accepts(room.Tier, tankTier, tierSpread)) { return float.NegativeInfinity; }
            if (room.Capacity > 0 && room.Players >= room.Capacity) { return float.NegativeInfinity; }
            if (room.StartsIn >= 0f && room.StartsIn < MinSecondsToJoin) { return float.NegativeInfinity; }
            return room.Players * 4f - Math.Abs(room.Tier - tankTier) * 10f;
        }

        /// <summary>Index of the best room for this tank, or -1 to open a new battle.</summary>
        public static int Pick(IList<Room> rooms, int tankTier, ICollection<string> skip = null)
        {
            return Pick(rooms, tankTier, InitialTierSpread, skip);
        }

        /// <summary>Index of the best room within an explicit tier range.</summary>
        public static int Pick(IList<Room> rooms, int tankTier, int tierSpread, ICollection<string> skip = null)
        {
            var best = -1;
            var bestScore = float.NegativeInfinity;
            for (var i = 0; rooms != null && i < rooms.Count; i++)
            {
                if (skip != null && rooms[i].Name != null && skip.Contains(rooms[i].Name)) { continue; }
                var score = Score(rooms[i], tankTier, tierSpread);
                if (score == float.NegativeInfinity) { continue; }
                // Ties go to the lower name so two searchers make the same choice.
                if (best < 0 || score > bestScore ||
                    (score == bestScore && string.CompareOrdinal(rooms[i].Name, rooms[best].Name) < 0))
                {
                    best = i;
                    bestScore = score;
                }
            }
            return best;
        }

        /// <summary>
        /// Seconds a lone host keeps seeing a fitting lone battle that should
        /// come to it before it moves there itself (that battle's searcher may
        /// still use a narrower tier range and never pick this one).
        /// </summary>
        public const float MergePatienceSeconds = 10f;

        /// <summary>A fresh battle stays put this long so the lobby listing can settle.</summary>
        public const float MergeSettleSeconds = 3f;

        /// <summary>
        /// Lone host deciding whether to close its own battle and move into
        /// another one seen in the lobby. Returns the index of that battle or
        /// -1 to keep waiting where it is. A battle that already gathers people
        /// is always worth moving to. Between two lone hosts exactly one moves:
        /// the one whose battle has the greater name, so they never swap past
        /// each other; the other moves too only after
        /// <see cref="MergePatienceSeconds"/> of seeing the same target.
        /// The own battle is never a target.
        /// </summary>
        public static int MergeTarget(
            IList<Room> rooms, string ownRoom, int tankTier, int tierSpread,
            float hostedSeconds, float targetSeenSeconds, ICollection<string> skip = null)
        {
            if (rooms == null || hostedSeconds < MergeSettleSeconds) { return -1; }
            var best = -1;
            var bestScore = float.NegativeInfinity;
            for (var i = 0; i < rooms.Count; i++)
            {
                var name = rooms[i].Name;
                if (string.IsNullOrEmpty(name) || name == ownRoom) { continue; }
                if (skip != null && skip.Contains(name)) { continue; }
                var score = Score(rooms[i], tankTier, tierSpread);
                if (score == float.NegativeInfinity) { continue; }
                if (best < 0 || score > bestScore ||
                    (score == bestScore && string.CompareOrdinal(name, rooms[best].Name) < 0))
                {
                    best = i;
                    bestScore = score;
                }
            }
            if (best < 0) { return -1; }
            if (rooms[best].Players >= 2) { return best; }
            if (string.IsNullOrEmpty(ownRoom) || string.CompareOrdinal(ownRoom, rooms[best].Name) > 0) { return best; }
            return targetSeenSeconds >= MergePatienceSeconds ? best : -1;
        }

        /// <summary>Countdown of a battle the normal balancer can even out.</summary>
        public const float NormalStartSeconds = 45f;

        /// <summary>Countdown after which the lenient balancer starts any battle of two or more humans.</summary>
        public const float LenientStartSeconds = 60f;

        /// <summary>
        /// Countdown seconds so far: from the first guest the lobby took (a
        /// second human makes a battle possible), 0 while the host is alone.
        /// Time spent alone in the queue never shortens the countdown, so late
        /// joiners always get the same window to fill the battle.
        /// </summary>
        public static float CountdownElapsed(float now, float firstGuestAt)
        {
            return firstGuestAt < 0f ? 0f : Math.Max(0f, now - firstGuestAt);
        }

        /// <summary>
        /// Largest team strength gap the normal balancer accepts: below one
        /// tier step (10), so only the class bonus may differ between sides.
        /// </summary>
        public const int NormalMaxStrengthGap = 5;

        /// <summary>What the host lobby does this frame.</summary>
        public struct LobbyDecision
        {
            public bool Start;

            /// <summary>Seconds until start for the queue UI; negative while waiting for players.</summary>
            public float StartsIn;

            /// <summary>True when the start fell to the lenient balancer.</summary>
            public bool Lenient;
        }

        /// <summary>
        /// Whether the normal balancer can make a fair battle out of these
        /// humans: an even head count split into equal teams whose strength
        /// differs by less than one tier step.
        /// </summary>
        public static bool NormalBalanceOk(int hostWeight, IList<int> guestWeights, int teamSize)
        {
            var guests = guestWeights != null ? guestWeights.Count : 0;
            var humans = guests + 1;
            if (humans < 2 || humans % 2 != 0) { return false; }
            var teams = SplitTeams(hostWeight, guestWeights, teamSize);
            var strength = new long[2];
            var count = new int[2];
            strength[0] += hostWeight;
            count[0]++;
            for (var i = 0; i < guests; i++)
            {
                if (teams[i] < 0) { return false; }
                strength[teams[i]] += guestWeights[i];
                count[teams[i]]++;
            }
            return count[0] == count[1] && Math.Abs(strength[0] - strength[1]) <= NormalMaxStrengthGap;
        }

        /// <summary>
        /// Matchmaking start rule. A full battle starts at once. With fewer
        /// than two humans the lobby waits for players however long it takes.
        /// Otherwise a battle the normal balancer can even out starts
        /// <paramref name="normalAt"/> seconds into the countdown (see
        /// <see cref="CountdownElapsed"/>); one it cannot keeps counting to
        /// <paramref name="lenientAt"/> and then starts with the lenient split
        /// (head counts differ by one at most).
        /// </summary>
        public static LobbyDecision Decide(
            float waited, int humans, int capacity, bool normalOk, float normalAt, float lenientAt)
        {
            var decision = new LobbyDecision();
            if (capacity > 0 && humans >= capacity)
            {
                decision.Start = true;
                return decision;
            }
            if (humans < 2)
            {
                decision.StartsIn = -1f;
                return decision;
            }
            if (normalOk)
            {
                decision.StartsIn = Math.Max(0f, normalAt - waited);
                decision.Start = waited >= normalAt;
                return decision;
            }
            decision.StartsIn = Math.Max(0f, lenientAt - waited);
            decision.Start = waited >= lenientAt;
            decision.Lenient = decision.Start;
            return decision;
        }

        /// <summary>Strength of one tank for team balance: tier dominates, class breaks ties.</summary>
        public static int Weight(int tier, int classColumn)
        {
            // 0 heavy, 1 medium, 2 light, 3 tank destroyer.
            int bonus;
            switch (classColumn)
            {
                case 0: bonus = 3; break;
                case 3: bonus = 2; break;
                case 1: bonus = 1; break;
                default: bonus = 0; break;
            }
            return tier * 10 + bonus;
        }

        /// <summary>
        /// Team (0 or 1, -1 when both are full) of every guest; the host is on
        /// team 0. Everybody is sorted by strength and taken in pairs, and the
        /// two tanks of a pair always land on opposite sides, the stronger one
        /// with the weaker team. So equal tanks face each other and the human
        /// counts differ by one at most. Bots fill the rest of both teams.
        /// </summary>
        public static int[] SplitTeams(int hostWeight, IList<int> guestWeights, int teamSize)
        {
            return SplitTeams(hostWeight, guestWeights, teamSize, null, null);
        }

        /// <summary>
        /// <see cref="SplitTeams(int, IList{int}, int)"/> that first pairs up
        /// the humans driving the same mirrored card (equal non-empty keys) and
        /// seats every pair on opposite sides, the host's partner on team 1.
        /// Everybody else is split as usual, so a mirrored card only needs a
        /// bot counterpart for an odd copy.
        /// </summary>
        public static int[] SplitTeams(
            int hostWeight, IList<int> guestWeights, int teamSize,
            string hostKey, IList<string> guestKeys)
        {
            var count = guestWeights != null ? guestWeights.Count : 0;
            var result = new int[count];
            if (count == 0) { return result; }

            // -1 stands for the host.
            var order = new List<int>(count + 1) { -1 };
            for (var i = 0; i < count; i++) { order.Add(i); }
            Func<int, int> weight = who => who < 0 ? hostWeight : guestWeights[who];
            Func<int, string> key = who => who < 0 ? hostKey
                : guestKeys != null && who < guestKeys.Count ? guestKeys[who] : null;

            var strength = new long[2];
            var humans = new int[2];
            var size = Math.Max(1, teamSize);
            Action<int, int> place = (who, team) =>
            {
                if (humans[team] >= size) { team = 1 - team; }
                if (humans[team] >= size)
                {
                    if (who >= 0) { result[who] = -1; }
                    return;
                }
                if (who >= 0) { result[who] = team; }
                strength[team] += weight(who);
                humans[team]++;
            };
            Func<int> weaker = () =>
                strength[0] != strength[1] ? (strength[0] < strength[1] ? 0 : 1)
                : humans[0] != humans[1] ? (humans[0] < humans[1] ? 0 : 1)
                : 1;

            // Same mirrored card: the pair faces each other before anybody
            // else is placed. Equal cards weigh the same, so the strength of
            // both sides grows evenly.
            var paired = new bool[count + 1];
            for (var a = 0; a < order.Count; a++)
            {
                var first = order[a];
                if (paired[first + 1] || string.IsNullOrEmpty(key(first))) { continue; }
                for (var b = a + 1; b < order.Count; b++)
                {
                    var second = order[b];
                    if (paired[second + 1]
                        || !string.Equals(key(first), key(second), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    paired[first + 1] = true;
                    paired[second + 1] = true;
                    if (first < 0)
                    {
                        place(-1, 0);
                        place(second, 1);
                    }
                    else
                    {
                        var side = weaker();
                        place(first, side);
                        place(second, 1 - side);
                    }
                    break;
                }
            }
            order.RemoveAll(who => paired[who + 1]);
            order.Sort((a, b) =>
            {
                var byWeight = weight(b).CompareTo(weight(a));
                return byWeight != 0 ? byWeight : a.CompareTo(b);
            });

            for (var k = 0; k < order.Count; k += 2)
            {
                var a = order[k];
                if (k + 1 >= order.Count)
                {
                    // The odd one out joins the side with fewer people.
                    var team = humans[0] != humans[1] ? (humans[0] < humans[1] ? 0 : 1) : weaker();
                    place(a, a < 0 ? 0 : team);
                    break;
                }
                var b = order[k + 1];
                if (a < 0 || b < 0)
                {
                    place(-1, 0);
                    place(a < 0 ? b : a, 1);
                    continue;
                }
                var side = weaker();
                place(a, side);
                place(b, 1 - side);
            }
            return result;
        }
    }
}
