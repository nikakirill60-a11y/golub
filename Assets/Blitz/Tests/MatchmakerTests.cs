using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Pins the ported matchmaker to the Godot source: random_team_template(),
    /// get_by_lvl_random(), roll_bot_name() and TeamMirror.apply().
    /// </summary>
    public sealed class MatchmakerTests
    {
        [Test]
        public void Template_HighTierRunsTheUnloyalBranch()
        {
            // offset_rand = 0, top = 2, mid = 3 -> low = 2, and tier 8 is above
            // the loyal threshold, so the slots keep their own deviations.
            var random = new ScriptedRandom(new[] { 0, 2, 3 }, new float[0]);
            var template = new Matchmaker(Database(), random).RandomTeamTemplate(8, false, 7);

            Assert.AreEqual(new[] { 1, 1, 0, 0, 0, -1, -1 }, template);
            Assert.AreEqual(0, random.RemainingInts, "unused randi_range() rolls");
        }

        [Test]
        public void Template_LowTierRunsTheLoyalBranchAndResizes()
        {
            // offset_rand = 1, top = 2, mid = 4 -> low = 1. The loyal branch caps
            // the middle at team_size_half - 1 and pads the bottom with two more
            // slots, so the 8 slots it builds are cut back to the team size.
            var random = new ScriptedRandom(new[] { 1, 2, 4 }, new[] { 0.1f });
            var template = new Matchmaker(Database(), random).RandomTeamTemplate(3, false, 7);

            Assert.AreEqual(new[] { 0, 0, 0, 0, 0, -1, -1 }, template);
        }

        [Test]
        public void Template_SuperLoyalCardNeverSeesATopTier()
        {
            // offset_rand = -1 forces at least one top slot, and the coin flip for
            // "more tops" comes up tails, so the middle drops a tier as well.
            var random = new ScriptedRandom(new[] { -1, 1, 2 }, new[] { 0.9f });
            var template = new Matchmaker(Database(), random).RandomTeamTemplate(6, true, 7);

            Assert.AreEqual(new[] { 0, -1, -1, -1, -1, -1, -1 }, template);
        }

        [Test]
        public void Template_TruncatesWhenTheTopAndMiddleFillTheTeam()
        {
            // top = 4 and mid = 4 leave low = -1, so the template overflows and
            // Array.resize() cuts it: the source never spawns more than the team.
            var random = new ScriptedRandom(new[] { 0, 4, 4 }, new float[0]);
            var template = new Matchmaker(Database(), random).RandomTeamTemplate(8, false, 7);

            Assert.AreEqual(7, template.Count);
            Assert.AreEqual(new[] { 1, 1, 1, 1, 0, 0, 0 }, template);
        }

        [Test]
        public void LevelRoll_SkipsHiddenOptedOutAndZeroChanceCards()
        {
            var database = new TanksDatabase();
            database.Add(Card("hidden_one", 5, hidden: true));
            database.Add(Card("shop_one", 5, ignoreMatchmaker: true));
            database.Add(Card("zero_one", 5, chance: 0f));
            database.Add(Card("t-34", 5, nation: Nation.Ussr));

            var random = new ScriptedRandom(new int[0], new[] { 0.1f });
            Assert.AreEqual("t-34", new Matchmaker(database, random).GetByLevelRandom(5, true));
        }

        [Test]
        public void LevelRoll_FallsBackToAnyCardOfTheTierWhenNoRollPasses()
        {
            var database = new TanksDatabase();
            database.Add(Card("hidden_one", 5, hidden: true));
            database.Add(Card("shop_one", 5, ignoreMatchmaker: true));

            // No candidate survives the filter, so the source still picks one of
            // the tier's visible cards at random rather than leaving the slot empty.
            var random = new ScriptedRandom(new[] { 0 }, new float[0]);
            Assert.AreEqual("shop_one", new Matchmaker(database, random).GetByLevelRandom(5, true));
            Assert.AreEqual(0, random.RemainingInts);
        }

        [Test]
        public void LevelRoll_NeverPicksAHiddenEventVehicle()
        {
            var database = new TanksDatabase();
            database.Add(Card("hidden_one", 5, chance: 1f, hidden: true));

            // An event's own vehicle is not even the fallback of its tier.
            var random = new ScriptedRandom(new int[0], new float[0]);
            var matchmaker = new Matchmaker(database, random);
            Assert.AreEqual(string.Empty, matchmaker.GetByLevelRandom(5, true));
            Assert.AreEqual(string.Empty, matchmaker.GetByLevelRandom(5, false));
        }

        [Test]
        public void LevelRoll_ClampsTiersOntoTheNearestBucket()
        {
            var database = new TanksDatabase();
            database.Add(Card("t62a", 10));

            var random = new ScriptedRandom(new int[0], new[] { 0.1f });
            Assert.AreEqual("t62a", new Matchmaker(database, random).GetByLevelRandom(12, true));
        }

        [Test]
        public void BotRoll_RerollsASuperLoyalCardBelowThePlayerTier()
        {
            var database = new TanksDatabase();
            database.Add(Card("pz_v_iv", 4, chance: 1f, superLoyal: true));
            database.Add(Card("vk3001p", 4, chance: 1f));

            // Shuffle keeps the super loyal card first, it passes its chance roll,
            // and the single reroll then lands on the regular card.
            var random = new ScriptedRandom(new[] { 1, 0 }, new[] { 0f, 0f });
            Assert.AreEqual("vk3001p", new Matchmaker(database, random).RollBotName(6, 4));
        }

        [Test]
        public void BotRoll_ReportsNoAlternativeWhenTheTierHasOneCard()
        {
            var database = new TanksDatabase();
            database.Add(Card("tog_ii", 8, chance: 1f));

            var matchmaker = new Matchmaker(database, new SeededRandomSource(7));
            Assert.AreEqual(string.Empty, matchmaker.RollBotNameExcept("tog_ii", 8, 8));
        }

        [Test]
        public void Mirror_FillsAFreeSlotOfTheSameTierFirst()
        {
            var allies = new List<string> { "t-44", "is-3", "tog_ii" };
            var enemies = new List<string> { "kv-4", "is-3", "pershing" };
            var tiers = new List<int> { 8, 8, 8 };

            var remainder = TeamMirror.Apply(
                "tog_ii", allies, enemies, tiers, 8, 0,
                delegate { return "is-3"; }, new ScriptedRandom(new[] { 2, 1 }, new float[0]));

            Assert.AreEqual(0, remainder);
            Assert.AreEqual(new[] { "t-44", "is-3", "tog_ii" }, allies, "the rolled roster is kept");
            CollectionAssert.Contains(enemies, "tog_ii");
        }

        [Test]
        public void Mirror_NeverMovesTheTankAcrossTiers()
        {
            var allies = new List<string> { "tog_ii", "tog_ii" };
            var enemies = new List<string> { "tog_ii", "bt-7" };
            var tiers = new List<int> { 8, 3 };

            // The enemy's only free slot is tier 3, so the surplus has to come off
            // the ally team instead, and the player slot stays untouched.
            var remainder = TeamMirror.Apply(
                "tog_ii", allies, enemies, tiers, 8, 0,
                delegate { return "is-3"; }, new ScriptedRandom(new int[0], new float[0]));

            Assert.AreEqual(0, remainder);
            Assert.AreEqual(new[] { "tog_ii", "is-3" }, allies);
            Assert.AreEqual(new[] { "tog_ii", "bt-7" }, enemies);
        }

        [Test]
        public void Mirror_ReportsTheRemainderWhenTheTierIsOutOfCards()
        {
            var allies = new List<string> { "tog_ii", "tog_ii" };
            var enemies = new List<string> { "tog_ii", "bt-7" };
            var tiers = new List<int> { 8, 3 };

            var remainder = TeamMirror.Apply(
                "tog_ii", allies, enemies, tiers, 8, 0,
                delegate { return string.Empty; }, new ScriptedRandom(new int[0], new float[0]));

            Assert.AreEqual(1, remainder);
            Assert.AreEqual(new[] { "tog_ii", "tog_ii" }, allies, "an unfillable slot is left alone");
        }

        [Test]
        public void Roster_SeatsThePlayerAtOwnTierAndRollsEveryOtherSeat()
        {
            var database = Database();
            var player = database.Get("t-44");

            for (var seed = 0; seed < 200; seed++)
            {
                var matchmaker = new Matchmaker(database, new SeededRandomSource(seed));
                var roster = matchmaker.BuildRoster(player, 7, 7);

                Assert.AreEqual(7, roster.TeamSize, "seed " + seed);
                Assert.AreEqual(roster.TeamSize, roster.Allies.Count);
                Assert.AreEqual(roster.TeamSize, roster.Enemies.Count);
                Assert.AreEqual(player.Name, roster.Allies[roster.PlayerSlot], "seed " + seed);
                if (roster.PlayerSlotFallback)
                {
                    // A template really can come out without a slot at the
                    // player tier; the source reports "couldn't find vacant place
                    // for player" and drops them into the last one.
                    CollectionAssert.DoesNotContain(roster.Template, 0, "seed " + seed);
                    Assert.AreEqual(roster.TeamSize - 1, roster.PlayerSlot, "seed " + seed);
                }
                else
                {
                    Assert.AreEqual(0, roster.Template[roster.PlayerSlot], "seed " + seed);
                }

                for (var slot = 0; slot < roster.TeamSize; slot++)
                {
                    Assert.AreEqual(player.Level + roster.Template[slot], roster.SlotTiers[slot]);

                    // The player keeps their own card even in a slot of another
                    // tier, so only the rolled seats are checked against it.
                    if (slot != roster.PlayerSlot)
                    {
                        AssertSlot(database, roster.Allies[slot], roster.SlotTiers[slot], seed);
                    }

                    AssertSlot(database, roster.Enemies[slot], roster.SlotTiers[slot], seed);
                }

                Assert.AreEqual(
                    Count(roster.Allies, "tog_ii"),
                    Count(roster.Enemies, "tog_ii"),
                    "mirrored tank, seed " + seed);
                Assert.AreEqual(
                    Count(roster.Allies, "t49_rocket"),
                    Count(roster.Enemies, "t49_rocket"),
                    "mirrored T49 ATGM, seed " + seed);
            }
        }

        [Test]
        public void RocketT49_IsStrictlyMirroredEvenWhenPlayerUsesFallbackSlot()
        {
            var database = Database();
            var player = database.Get("t49_rocket");
            CollectionAssert.Contains(Matchmaker.MirroredTanks, "t49_rocket");
            var sawFallback = false;
            for (var seed = 0; seed < 200; seed++)
            {
                var roster = new Matchmaker(database, new SeededRandomSource(seed))
                    .BuildRoster(player, 7, 7);
                sawFallback |= roster.PlayerSlotFallback;
                Assert.AreEqual(
                    Count(roster.Allies, "t49_rocket"),
                    Count(roster.Enemies, "t49_rocket"),
                    "seed " + seed + ", fallback=" + roster.PlayerSlotFallback);
            }
            Assert.IsTrue(sawFallback, "the strict fallback branch was not exercised");
        }

        [Test]
        public void Roster_MirrorsEveryEventCardWhateverThePlayerDrives()
        {
            var database = EventDatabase();
            CollectionAssert.IsSubsetOf(
                new[] { "t49_rocket", "gb63_tog_ii", "a116_xm551_exp", "g14_kpfpz_70r" },
                Matchmaker.StrictlyMirroredTanks);
            CollectionAssert.IsSubsetOf(Matchmaker.StrictlyMirroredTanks, Matchmaker.MirroredTanks);
            var players = new[] { "t-44", "t49_rocket", "gb63_tog_ii", "a116_xm551_exp", "g14_kpfpz_70r" };
            foreach (var player in players)
            {
                for (var seed = 0; seed < 150; seed++)
                {
                    var roster = new Matchmaker(database, new SeededRandomSource(seed))
                        .BuildRoster(database.Get(player), 7, 7);
                    Assert.AreEqual(player, roster.Allies[roster.PlayerSlot], "seed " + seed);
                    foreach (var name in Matchmaker.MirroredTanks)
                    {
                        Assert.AreEqual(
                            Count(roster.Allies, name),
                            Count(roster.Enemies, name),
                            name + " with " + player + ", seed " + seed);
                    }
                }
            }
        }

        [Test]
        public void MirrorRoster_GivesLockedHumansBotCounterparts()
        {
            var database = EventDatabase();
            var roster = Tier10Roster(
                new[] { "tier10_0", "a116_xm551_exp", "tier10_1", "tier10_2" },
                new[] { "g14_kpfpz_70r", "tier10_3", "tier10_0", "tier10_1" });
            var lockedAllies = new HashSet<int> { 0, 1 };
            var lockedEnemies = new HashSet<int> { 0 };

            var uneven = new Matchmaker(database, new SeededRandomSource(5))
                .MirrorRoster(roster, 10, lockedAllies.Contains, lockedEnemies.Contains);

            Assert.AreEqual(0, uneven);
            Assert.AreEqual("tier10_0", roster.Allies[0], "the player's seat is locked");
            Assert.AreEqual("a116_xm551_exp", roster.Allies[1], "a human's seat is locked");
            Assert.AreEqual("g14_kpfpz_70r", roster.Enemies[0], "a human's seat is locked");
            Assert.AreEqual(1, Count(roster.Enemies, "a116_xm551_exp"), "a bot Sheridan faces the human one");
            Assert.AreEqual(1, Count(roster.Allies, "g14_kpfpz_70r"), "a bot Kampfpanzer faces the human one");
        }

        [Test]
        public void MirrorRoster_RerollsSurplusBotsRatherThanOverwritingOtherEventCards()
        {
            var database = EventDatabase();
            var roster = Tier10Roster(
                new[] { "tier10_0", "a116_xm551_exp", "a116_xm551_exp" },
                new[] { "g14_kpfpz_70r", "g14_kpfpz_70r", "tier10_1" });

            for (var seed = 0; seed < 50; seed++)
            {
                var allies = new List<string>(roster.Allies);
                var enemies = new List<string>(roster.Enemies);
                var copy = Tier10Roster(allies.ToArray(), enemies.ToArray());
                var uneven = new Matchmaker(database, new SeededRandomSource(seed))
                    .MirrorRoster(copy, 10, slot => slot == 0, null);

                Assert.AreEqual(0, uneven, "seed " + seed);
                Assert.AreEqual("tier10_0", copy.Allies[0], "seed " + seed);
                foreach (var name in Matchmaker.MirroredTanks)
                {
                    Assert.AreEqual(Count(copy.Allies, name), Count(copy.Enemies, name), name + ", seed " + seed);
                }
                for (var slot = 0; slot < copy.TeamSize; slot++)
                {
                    AssertSlot(database, copy.Allies[slot], 10, seed);
                    AssertSlot(database, copy.Enemies[slot], 10, seed);
                }
            }
        }

        [Test]
        public void Roster_TruncatesToWhatTheMapCanSpawn()
        {
            var database = Database();
            var roster = new Matchmaker(database, new SeededRandomSource(3))
                .BuildRoster(database.Get("t-44"), 7, 3);

            Assert.AreEqual(3, roster.TeamSize);
            Assert.AreEqual(3, roster.Allies.Count);
            Assert.AreEqual(3, roster.Enemies.Count);
        }

        [Test]
        public void Card_BiasesTheSovietTreeAfterClampingTheChance()
        {
            var database = new TanksDatabase();
            database.Add(Card("T-34", 5, nation: Nation.Ussr));
            database.Add(Card("pz_iii", 5, nation: Nation.Germany));
            database.Add(Card("is-7", 42, nation: Nation.Ussr, chance: 4f));

            var soviet = database.Get("t-34");
            Assert.IsNotNull(soviet, "cleanup() lower cases the card name");
            Assert.AreEqual(0.625f, soviet.MatchmakerChance, 0.0001f);
            Assert.AreEqual(0.5f, database.Get("pz_iii").MatchmakerChance, 0.0001f);

            var clamped = database.Get("is-7");
            Assert.AreEqual(10, clamped.Level, "cleanup() clamps the tier");
            Assert.AreEqual(1.25f, clamped.MatchmakerChance, 0.0001f);
        }

        private static void AssertSlot(TanksDatabase database, string name, int tier, int seed)
        {
            var card = database.Get(name);
            Assert.IsNotNull(card, "unknown card " + name + ", seed " + seed);
            Assert.AreEqual(tier, card.Level, name + " in a tier " + tier + " slot, seed " + seed);
        }

        private static int Count(List<string> team, string name)
        {
            var count = 0;
            for (var i = 0; i < team.Count; i++)
            {
                if (team[i] == name)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary><see cref="Database"/> plus the port's mirrored event cards.</summary>
        private static TanksDatabase EventDatabase()
        {
            var database = Database();
            database.Add(Card("gb63_tog_ii", 6, nation: Nation.Gb));
            database.Add(Card("a116_xm551_exp", 10, nation: Nation.Usa));
            database.Add(Card("g14_kpfpz_70r", 10, nation: Nation.Germany));
            return database;
        }

        /// <summary>A same-size tier 10 roster with the player in slot 0.</summary>
        private static MatchmakerRoster Tier10Roster(string[] allies, string[] enemies)
        {
            var roster = new MatchmakerRoster { PlayerSlot = 0 };
            for (var i = 0; i < allies.Length; i++)
            {
                roster.Template.Add(0);
                roster.SlotTiers.Add(10);
            }

            roster.Allies.AddRange(allies);
            roster.Enemies.AddRange(enemies);
            return roster;
        }

        /// <summary>Four cards per tier, so every rolled slot has alternatives.</summary>
        private static TanksDatabase Database()
        {
            var database = new TanksDatabase();
            for (var level = 1; level <= TanksDatabase.MaxLevel; level++)
            {
                for (var index = 0; index < 4; index++)
                {
                    database.Add(Card("tier" + level + "_" + index, level, nation: Nation.Ussr));
                }
            }

            database.Add(Card("t49_rocket", 7, nation: Nation.Usa));
            database.Add(Card("t-44", 8, nation: Nation.Ussr));
            database.Add(Card("tog_ii", 8, nation: Nation.Gb));
            return database;
        }

        private static TankInfo Card(
            string name,
            int level,
            Nation nation = Nation.None,
            float chance = 0.5f,
            bool hidden = false,
            bool ignoreMatchmaker = false,
            bool superLoyal = false)
        {
            return new TankInfo
            {
                Name = name,
                Level = level,
                Nation = nation,
                MatchmakerChance = chance,
                Hidden = hidden,
                IgnoreMatchmaker = ignoreMatchmaker,
                MatchmakerSuperLoyal = superLoyal,
            };
        }

        /// <summary>Godot randi_range()/randf() replaced by a scripted tape.</summary>
        private sealed class ScriptedRandom : IRandomSource
        {
            private readonly Queue<int> _ints;
            private readonly Queue<float> _floats;

            public ScriptedRandom(IEnumerable<int> ints, IEnumerable<float> floats)
            {
                _ints = new Queue<int>(ints);
                _floats = new Queue<float>(floats);
            }

            public int RemainingInts
            {
                get { return _ints.Count; }
            }

            public int RangeInclusive(int from, int to)
            {
                Assert.Greater(_ints.Count, 0, "randi_range() ran out of scripted rolls");
                var value = _ints.Dequeue();
                Assert.GreaterOrEqual(value, from, "scripted roll below randi_range()");
                Assert.LessOrEqual(value, to, "scripted roll above randi_range()");
                return value;
            }

            public float Value()
            {
                Assert.Greater(_floats.Count, 0, "randf() ran out of scripted rolls");
                return _floats.Dequeue();
            }
        }
    }
}
