using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// The roster the matchmaker settles before anything spawns, mirroring the
    /// local variables of Godot <c>Battle.start_battle()</c>.
    /// </summary>
    public sealed class MatchmakerRoster
    {
        /// <summary>t_player_team: the tier deviation of every slot.</summary>
        public readonly List<int> Template = new List<int>();

        /// <summary>slot_tiers: player tier plus the slot's deviation.</summary>
        public readonly List<int> SlotTiers = new List<int>();

        /// <summary>roster_player, including the player's own card.</summary>
        public readonly List<string> Allies = new List<string>();

        /// <summary>roster_enemy.</summary>
        public readonly List<string> Enemies = new List<string>();

        /// <summary>player_id: the ally slot the player drives.</summary>
        public int PlayerSlot;

        /// <summary>
        /// True when the template had no slot at the player's own tier, which
        /// the source reports as "couldn't find vacant place for player" before
        /// falling back to the last slot.
        /// </summary>
        public bool PlayerSlotFallback;

        public int TeamSize
        {
            get { return Template.Count; }
        }
    }

    /// <summary>
    /// Port of the Godot matchmaker: <c>random_team_template()</c>,
    /// <c>roll_bot_name()</c> and the roster assembly of
    /// <c>start_battle()</c> in scripts/_battle.gd, plus
    /// <c>TanksDataBase.get_by_lvl_random()</c> from assets/data/db_tanks.gd.
    ///
    /// Everything here is pure so an EditMode test can drive a full battle
    /// roster from one seed; the runtime supplies the live tank table and RNG.
    /// </summary>
    public sealed class Matchmaker
    {
        /// <summary>
        /// Battle.MIRRORED_TANKS (the source's "tog_ii" and the T49 ATGM) plus
        /// the port's TOG II*, Sheridan and Kampfpanzer 70 missile carriers:
        /// both teams field each of these cards equally often.
        /// </summary>
        public static readonly string[] MirroredTanks =
        {
            "tog_ii", "t49_rocket", "gb63_tog_ii", "a116_xm551_exp", "g14_kpfpz_70r",
        };

        /// <summary>
        /// Mirrored cards whose parity outranks the tier layout: a copy that
        /// has no same-tier counterpart slot still gets one in another slot.
        /// </summary>
        public static readonly string[] StrictlyMirroredTanks =
        {
            "t49_rocket", "gb63_tog_ii", "a116_xm551_exp", "g14_kpfpz_70r",
        };

        /// <summary>The loyal_treshold literal of random_team_template().</summary>
        public const int LoyalThreshold = 4;

        /// <summary>roll_bot_name_except() gives up after this many rolls.</summary>
        public const int RerollTries = 12;

        private readonly TanksDatabase _tanks;
        private readonly IRandomSource _random;

        public Matchmaker(TanksDatabase tanks, IRandomSource random)
        {
            if (tanks == null)
            {
                throw new ArgumentNullException("tanks");
            }

            if (random == null)
            {
                throw new ArgumentNullException("random");
            }

            _tanks = tanks;
            _random = random;
        }

        /// <summary>
        /// random_team_template(): the tier deviation of every slot, relative to
        /// the player's own tier. The source clamps the size to
        /// Global.MAX_TEAM_TANKS and finishes with <c>team.resize()</c>, so an
        /// overfilled template is truncated and an underfilled one is padded
        /// with same-tier slots.
        /// </summary>
        public List<int> RandomTeamTemplate(int playerLevel, bool superLoyal, int maxTeamTanks)
        {
            var teamSize = maxTeamTanks < 1 ? 1 : maxTeamTanks;
            var teamSizeHalf = (int)(teamSize * 0.5f) + 1;
            var team = new List<int>();

            var offsetRand = _random.RangeInclusive(-1, 1);
            var offset = superLoyal ? -1 : 0;

            var topLevels = _random.RangeInclusive(offsetRand < 0 ? 1 : 0, teamSizeHalf);
            var midLevels = _random.RangeInclusive(1, teamSizeHalf);
            var lowLevels = teamSize - topLevels - midLevels;

            var loyal = playerLevel <= LoyalThreshold
                || (playerLevel + offsetRand) <= LoyalThreshold
                || ((playerLevel - 1 + offsetRand) <= LoyalThreshold && lowLevels > 0);

            if (superLoyal)
            {
                var moreTops = _random.Chance(50f);
                Fill(team, topLevels, 0);
                Fill(team, midLevels, moreTops ? 0 : -1);
                Fill(team, lowLevels, -1);
            }
            else if (loyal)
            {
                var top = _random.Chance(50f);
                Fill(team, topLevels, top ? 0 : 1);
                Fill(team, Math.Min(midLevels, teamSizeHalf - 1), top ? 0 : 1);
                Fill(team, lowLevels + 2, top ? -1 : 0);
            }
            else
            {
                Fill(team, topLevels, 1 + offset + offsetRand);
                Fill(team, midLevels, offset + offsetRand);
                Fill(team, lowLevels, -1 + offset + offsetRand);
            }

            Resize(team, teamSize);
            return team;
        }

        /// <summary>
        /// TanksDataBase.get_by_lvl_random(): a card of that tier, weighted by
        /// matchmaker_chance. The matchmaker pass skips cards that opt out and
        /// cards with a zero chance; when no roll passes, the source still picks
        /// any card of the tier, opt-out included. A hidden card (an event's own
        /// vehicle, which the source never registers) is never picked at all.
        /// </summary>
        public string GetByLevelRandom(int level, bool matchmakerOnly)
        {
            var bucket = _tanks.ByLevel(level);
            if (bucket.Count == 0)
            {
                return string.Empty;
            }

            var candidates = new List<TankInfo>();
            var visible = new List<TankInfo>(bucket.Count);
            for (var i = 0; i < bucket.Count; i++)
            {
                var info = bucket[i];
                if (info.Hidden) { continue; }
                visible.Add(info);
                if (!matchmakerOnly || (!info.IgnoreMatchmaker && info.MatchmakerChance > 0f))
                {
                    candidates.Add(info);
                }
            }

            _random.Shuffle(candidates);
            for (var i = 0; i < candidates.Count; i++)
            {
                if (_random.Chance(candidates[i].MatchmakerChance * 100f))
                {
                    return candidates[i].Name;
                }
            }

            var fallback = _random.PickRandom(visible);
            return fallback == null ? string.Empty : fallback.Name;
        }

        /// <summary>
        /// roll_bot_name(): one reroll when a super loyal card lands below the
        /// player's own tier, so those tanks stay rare in a downtiered slot.
        /// </summary>
        public string RollBotName(int playerLevel, int botLevel)
        {
            var name = GetByLevelRandom(botLevel, true);
            var info = _tanks.Get(name);
            if (info != null && info.MatchmakerSuperLoyal && botLevel < playerLevel)
            {
                name = GetByLevelRandom(botLevel, true);
            }

            return name;
        }

        /// <summary>
        /// roll_bot_name_except(): an empty name when the tier has nothing else
        /// to offer, so the caller can leave the slot as it is.
        /// </summary>
        public string RollBotNameExcept(string name, int playerLevel, int botLevel, int tries = RerollTries)
        {
            for (var i = 0; i < tries; i++)
            {
                var rolled = RollBotName(playerLevel, botLevel);
                if (!string.Equals(rolled, name, StringComparison.OrdinalIgnoreCase))
                {
                    return rolled;
                }
            }

            return string.Empty;
        }

        /// <summary>True for every card of <see cref="MirroredTanks"/>.</summary>
        public static bool IsMirrored(string name)
        {
            return Contains(MirroredTanks, name);
        }

        /// <summary>True for every card of <see cref="StrictlyMirroredTanks"/>.</summary>
        public static bool IsStrictlyMirrored(string name)
        {
            return Contains(StrictlyMirroredTanks, name);
        }

        /// <summary>
        /// <see cref="RollBotName"/> that never lands on a mirrored card: the
        /// replacement for a surplus copy must not unsettle another mirrored
        /// card. An empty name when the tier has nothing else to offer.
        /// </summary>
        public string RollUnmirroredBotName(int playerLevel, int botLevel, int tries = RerollTries)
        {
            for (var i = 0; i < tries; i++)
            {
                var rolled = RollBotName(playerLevel, botLevel);
                if (!string.IsNullOrEmpty(rolled) && !IsMirrored(rolled))
                {
                    return rolled;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// The roster half of start_battle(): both teams run the same template,
        /// the player takes a random same-tier slot, every other slot is rolled,
        /// and the mirrored cards are evened out while the roster is still just
        /// a list of names.
        /// </summary>
        /// <param name="playerCard">The card the player drives.</param>
        /// <param name="maxTeamTanks">Global.MAX_TEAM_TANKS.</param>
        /// <param name="mapTeamCapacity">
        /// How many slots the map can actually spawn per side; the template is
        /// truncated to it exactly as the source does.
        /// </param>
        public MatchmakerRoster BuildRoster(TankInfo playerCard, int maxTeamTanks, int mapTeamCapacity)
        {
            if (playerCard == null)
            {
                throw new ArgumentNullException("playerCard");
            }

            var roster = new MatchmakerRoster();
            var playerLevel = playerCard.Level;
            var template = RandomTeamTemplate(playerLevel, playerCard.MatchmakerSuperLoyal, maxTeamTanks);
            if (mapTeamCapacity > 0 && template.Count > mapTeamCapacity)
            {
                Resize(template, mapTeamCapacity);
            }

            roster.Template.AddRange(template);

            var playerPlaces = new List<int>();
            for (var i = 0; i < template.Count; i++)
            {
                if (template[i] == 0)
                {
                    playerPlaces.Add(i);
                }
            }

            if (playerPlaces.Count == 0)
            {
                roster.PlayerSlotFallback = true;
                roster.PlayerSlot = template.Count - 1;
            }
            else
            {
                roster.PlayerSlot = _random.PickRandom(playerPlaces);
            }

            for (var i = 0; i < template.Count; i++)
            {
                roster.SlotTiers.Add(playerLevel + template[i]);
            }

            for (var i = 0; i < template.Count; i++)
            {
                roster.Allies.Add(i == roster.PlayerSlot
                    ? playerCard.Name
                    : RollBotName(playerLevel, roster.SlotTiers[i]));
            }

            for (var i = 0; i < template.Count; i++)
            {
                roster.Enemies.Add(RollBotName(playerLevel, roster.SlotTiers[i]));
            }

            for (var i = 0; i < MirroredTanks.Length; i++)
            {
                MirrorTankCount(MirroredTanks[i], roster, playerLevel);
            }

            return roster;
        }

        /// <summary>mirror_tank_count(): the tank's own tier plus the reroll.</summary>
        public int MirrorTankCount(string name, MatchmakerRoster roster, int playerLevel)
        {
            if (roster == null)
            {
                throw new ArgumentNullException("roster");
            }

            var playerSlot = roster.PlayerSlot;
            return MirrorTankCount(name, roster, playerLevel, slot => slot == playerSlot, null);
        }

        /// <summary>
        /// <see cref="MirrorTankCount(string, MatchmakerRoster, int)"/> with any
        /// number of locked seats on either side (the humans of an online
        /// battle). The other mirrored cards are never overwritten, so evening
        /// one card out cannot unsettle another.
        /// </summary>
        public int MirrorTankCount(
            string name, MatchmakerRoster roster, int playerLevel,
            Predicate<int> lockedAllies, Predicate<int> lockedEnemies)
        {
            if (roster == null)
            {
                throw new ArgumentNullException("roster");
            }

            var info = _tanks.Get(name);
            if (info == null)
            {
                return 0;
            }

            return TeamMirror.Apply(
                name,
                roster.Allies,
                roster.Enemies,
                roster.SlotTiers,
                info.Level,
                lockedAllies,
                lockedEnemies,
                delegate(int botLevel) { return RollUnmirroredBotName(playerLevel, botLevel); },
                _random,
                IsStrictlyMirrored(name),
                card => IsMirrored(card) && !string.Equals(card, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Evens every mirrored card out again after the roster was rewritten
        /// (the Big Boss tier clamp, the humans of an online battle written
        /// into their seats). Locked seats are counted but never rewritten.
        /// Returns the copies that could not be evened out.
        /// </summary>
        public int MirrorRoster(
            MatchmakerRoster roster, int playerLevel,
            Predicate<int> lockedAllies, Predicate<int> lockedEnemies)
        {
            var remainder = 0;
            for (var i = 0; i < MirroredTanks.Length; i++)
            {
                remainder += MirrorTankCount(MirroredTanks[i], roster, playerLevel, lockedAllies, lockedEnemies);
            }

            return remainder;
        }

        private static bool Contains(string[] names, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            for (var i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Fill(List<int> team, int count, int value)
        {
            for (var i = 0; i < count; i++)
            {
                team.Add(value);
            }
        }

        /// <summary>Godot <c>Array.resize()</c>: truncate, or pad with zeroes.</summary>
        private static void Resize(List<int> team, int size)
        {
            if (size < 0)
            {
                return;
            }

            while (team.Count > size)
            {
                team.RemoveAt(team.Count - 1);
            }

            while (team.Count < size)
            {
                team.Add(0);
            }
        }
    }
}
