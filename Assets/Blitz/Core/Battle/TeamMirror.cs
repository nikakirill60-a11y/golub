using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Rng;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// Port of Godot <c>TeamMirror</c> (scripts/util/team_mirror.gd): roster
    /// maths for tanks that must appear the same number of times in both teams.
    /// The source keeps this free of autoloads so it can be unit tested, and
    /// the port keeps it free of UnityEngine for the same reason.
    /// </summary>
    public static class TeamMirror
    {
        /// <summary>
        /// Rewrites as few slots as possible so both teams field
        /// <paramref name="name"/> equally often, and returns how many copies
        /// could not be evened out (zero means the teams are mirrored).
        ///
        /// Only a slot whose tier already equals <paramref name="tier"/> is
        /// eligible: moving the tank across tiers would trade a vehicle
        /// imbalance for a tier imbalance. <paramref name="playerSlot"/> is
        /// counted but never rewritten; pass -1 when no slot is locked.
        /// <paramref name="reroll"/> may return an empty name when the tier has
        /// nothing else to offer. With <paramref name="strict"/> enabled, a
        /// remaining copy is mirrored into another-tier slot rather than left
        /// asymmetric once no same-tier move is left; this is reserved for the
        /// event vehicles that require parity.
        /// </summary>
        public static int Apply(
            string name,
            IList<string> teamPlayer,
            IList<string> teamEnemy,
            IList<int> slotTiers,
            int tier,
            int playerSlot,
            Func<int, string> reroll,
            IRandomSource random,
            bool strict = false)
        {
            Predicate<int> locked = null;
            if (playerSlot >= 0) { locked = slot => slot == playerSlot; }
            return Apply(name, teamPlayer, teamEnemy, slotTiers, tier, locked, null, reroll, random, strict, null);
        }

        /// <summary>
        /// <see cref="Apply(string, IList{string}, IList{string}, IList{int}, int, int, Func{int, string}, IRandomSource, bool)"/>
        /// with any number of locked slots on either team, e.g. every human of
        /// an online battle: a locked slot is counted but never rewritten.
        /// <paramref name="keep"/> names cards that must not be overwritten
        /// either (the other mirrored vehicles, whose parity is already settled).
        /// </summary>
        public static int Apply(
            string name,
            IList<string> teamPlayer,
            IList<string> teamEnemy,
            IList<int> slotTiers,
            int tier,
            Predicate<int> lockedPlayer,
            Predicate<int> lockedEnemy,
            Func<int, string> reroll,
            IRandomSource random,
            bool strict,
            Predicate<string> keep)
        {
            if (teamPlayer == null)
            {
                throw new ArgumentNullException("teamPlayer");
            }

            if (teamEnemy == null)
            {
                throw new ArgumentNullException("teamEnemy");
            }

            if (random == null)
            {
                throw new ArgumentNullException("random");
            }

            var countPlayer = Count(teamPlayer, name);
            var countEnemy = Count(teamEnemy, name);
            if (countPlayer == countEnemy)
            {
                return 0;
            }

            var playerShort = countPlayer < countEnemy;
            var shortTeam = playerShort ? teamPlayer : teamEnemy;
            var longTeam = playerShort ? teamEnemy : teamPlayer;
            var shortLocked = playerShort ? lockedPlayer : lockedEnemy;
            var longLocked = playerShort ? lockedEnemy : lockedPlayer;
            var diff = Math.Abs(countPlayer - countEnemy);

            // Top the short team up first: that keeps every tank the matchmaker
            // already rolled in the battle instead of throwing rolls away.
            var vacant = new List<int>();
            for (var i = 0; i < shortTeam.Count; i++)
            {
                if (Locked(shortLocked, i) || Same(shortTeam[i], name) || Kept(keep, shortTeam[i]))
                {
                    continue;
                }

                if (slotTiers == null || i >= slotTiers.Count || slotTiers[i] != tier)
                {
                    continue;
                }

                vacant.Add(i);
            }

            random.Shuffle(vacant);
            while (diff > 0 && vacant.Count > 0)
            {
                var slot = vacant[vacant.Count - 1];
                vacant.RemoveAt(vacant.Count - 1);
                shortTeam[slot] = name;
                diff--;
            }

            if (diff == 0)
            {
                return 0;
            }

            // The short team can only run out of same-tier slots when the long
            // team filled every one of them, or when they hold locked humans and
            // the other mirrored cards. The surplus then has to come off the long
            // team instead, which keeps every slot at its own tier.
            var surplus = new List<int>();
            for (var i = 0; i < longTeam.Count; i++)
            {
                if (Locked(longLocked, i) || !Same(longTeam[i], name))
                {
                    continue;
                }

                surplus.Add(i);
            }

            random.Shuffle(surplus);
            while (diff > 0 && surplus.Count > 0)
            {
                if (reroll == null)
                {
                    break;
                }

                var slot = surplus[surplus.Count - 1];
                surplus.RemoveAt(surplus.Count - 1);
                var slotTier = slotTiers != null && slot < slotTiers.Count ? slotTiers[slot] : tier;
                var replacement = reroll(slotTier);

                // An empty name means the tier had nothing else to offer.
                // Leaving the tank in place beats forcing one of the wrong tier
                // into the slot.
                if (string.IsNullOrEmpty(replacement) || Same(replacement, name) || Kept(keep, replacement))
                {
                    break;
                }

                longTeam[slot] = replacement;
                diff--;
            }

            if (diff == 0 || !strict)
            {
                return diff;
            }

            // Strict vehicles must stay numerically mirrored even when neither
            // move above is possible: the player was seated through the
            // fallback slot, or locked humans hold every surplus copy. Another
            // slot of the short team then takes the missing copy, whatever its
            // tier.
            vacant.Clear();
            for (var i = 0; i < shortTeam.Count; i++)
            {
                if (!Locked(shortLocked, i) && !Same(shortTeam[i], name) && !Kept(keep, shortTeam[i]))
                {
                    vacant.Add(i);
                }
            }

            random.Shuffle(vacant);
            while (diff > 0 && vacant.Count > 0)
            {
                var slot = vacant[vacant.Count - 1];
                vacant.RemoveAt(vacant.Count - 1);
                shortTeam[slot] = name;
                diff--;
            }

            return diff;
        }

        private static bool Locked(Predicate<int> locked, int slot)
        {
            return locked != null && locked(slot);
        }

        private static bool Kept(Predicate<string> keep, string card)
        {
            return keep != null && !string.IsNullOrEmpty(card) && keep(card);
        }

        private static int Count(IList<string> team, string name)
        {
            var count = 0;
            for (var i = 0; i < team.Count; i++)
            {
                if (Same(team[i], name))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
