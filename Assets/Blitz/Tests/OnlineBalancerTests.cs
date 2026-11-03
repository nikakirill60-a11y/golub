using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class OnlineBalancerTests
    {
        private static OnlineBalancer.Room Room(string name, int tier, int players, float startsIn = -1f)
        {
            return new OnlineBalancer.Room { Name = name, Tier = tier, Players = players, Capacity = 14, StartsIn = startsIn };
        }

        [Test]
        public void Accepts_OnlyOneTierEitherWayAtFirst()
        {
            Assert.IsTrue(OnlineBalancer.Accepts(5, 5));
            Assert.IsTrue(OnlineBalancer.Accepts(5, 4));
            Assert.IsTrue(OnlineBalancer.Accepts(5, 6));
            Assert.IsFalse(OnlineBalancer.Accepts(5, 3));
            Assert.IsFalse(OnlineBalancer.Accepts(1, 10));
            Assert.IsFalse(OnlineBalancer.Accepts(0, 1), "a room without a tier takes nobody");
        }

        [Test]
        public void Pick_WidensToTwoTiersAfterFortyFiveSeconds()
        {
            var rooms = new List<OnlineBalancer.Room> { Room("wide", 3, 1) };
            var before = OnlineBalancer.TierSpreadFor(OnlineBalancer.ExpandAfterSeconds - 0.01f);
            var after = OnlineBalancer.TierSpreadFor(OnlineBalancer.ExpandAfterSeconds);
            Assert.AreEqual(OnlineBalancer.InitialTierSpread, before);
            Assert.AreEqual(OnlineBalancer.ExpandedTierSpread, after);
            Assert.AreEqual(-1, OnlineBalancer.Pick(rooms, 5, before));
            Assert.AreEqual(0, OnlineBalancer.Pick(rooms, 5, after));
            Assert.IsFalse(OnlineBalancer.Accepts(5, 2, after), "the widened search is still bounded");
        }

        [Test]
        public void Pick_NeverPutsTierOneAgainstTierTen()
        {
            var rooms = new List<OnlineBalancer.Room> { Room("x", 10, 13) };
            Assert.AreEqual(-1, OnlineBalancer.Pick(rooms, 1));
        }

        [Test]
        public void Pick_PrefersSameTierThenFullerRoom()
        {
            var rooms = new List<OnlineBalancer.Room> { Room("a", 6, 3), Room("b", 5, 2), Room("c", 5, 5) };
            Assert.AreEqual(2, OnlineBalancer.Pick(rooms, 5));
            rooms = new List<OnlineBalancer.Room> { Room("a", 6, 2), Room("b", 5, 1) };
            Assert.AreEqual(1, OnlineBalancer.Pick(rooms, 5), "same tier beats one more player");
        }

        [Test]
        public void Pick_SkipsFullStartingAndRefusedRooms()
        {
            var rooms = new List<OnlineBalancer.Room> { Room("full", 5, 14), Room("now", 5, 4, 1f), Room("no", 5, 3), Room("ok", 5, 1, 10f) };
            Assert.AreEqual(3, OnlineBalancer.Pick(rooms, 5, new HashSet<string> { "no" }));
        }

        [Test]
        public void Pick_TiesGoToTheSameRoomForEverySearcher()
        {
            var one = new List<OnlineBalancer.Room> { Room("bl-b", 5, 1), Room("bl-a", 5, 1) };
            var two = new List<OnlineBalancer.Room> { Room("bl-a", 5, 1), Room("bl-b", 5, 1) };
            Assert.AreEqual("bl-a", one[OnlineBalancer.Pick(one, 5)].Name);
            Assert.AreEqual("bl-a", two[OnlineBalancer.Pick(two, 5)].Name);
        }

        [Test]
        public void SplitTeams_PutsEqualTanksOnOppositeSides()
        {
            // Host: tier 6 heavy. Guests: heavy 6, medium 5, heavy 6, medium 5.
            var host = OnlineBalancer.Weight(6, 0);
            var guests = new List<int>
            {
                OnlineBalancer.Weight(6, 0), OnlineBalancer.Weight(5, 1),
                OnlineBalancer.Weight(6, 0), OnlineBalancer.Weight(5, 1),
            };
            var teams = OnlineBalancer.SplitTeams(host, guests, 7);
            Assert.AreEqual(1, teams[0], "the host's twin faces the host");
            Assert.AreNotEqual(teams[2], teams[1], "the next pair is split");
            int[] count = { 1, 0 };
            foreach (var team in teams) { count[team]++; }
            Assert.LessOrEqual(System.Math.Abs(count[0] - count[1]), 1);
        }

        [Test]
        public void SplitTeams_TwoTensAndTwoOnesNeverStackOneSide()
        {
            var guests = new List<int> { OnlineBalancer.Weight(10, 0), OnlineBalancer.Weight(1, 2), OnlineBalancer.Weight(1, 2) };
            var teams = OnlineBalancer.SplitTeams(OnlineBalancer.Weight(10, 0), guests, 7);
            Assert.AreEqual(1, teams[0]);
            Assert.AreNotEqual(teams[1], teams[2]);
        }

        [Test]
        public void SplitTeams_SeatsTwoHumansInTheSameMirroredCardApart()
        {
            // Host and the second guest drive the TOG II*; the medium tier 6
            // guest outweighs both, so the plain split pairs it with the host
            // and the second TOG would join the host's side.
            var guests = new List<int> { OnlineBalancer.Weight(6, 1), OnlineBalancer.Weight(6, 2) };
            var keys = new List<string> { null, "gb63_tog_ii" };
            var teams = OnlineBalancer.SplitTeams(OnlineBalancer.Weight(6, 2), guests, 7, "GB63_TOG_II", keys);
            Assert.AreEqual(1, teams[1], "the host's mirrored twin faces the host");
            Assert.That(teams[0], Is.EqualTo(0).Or.EqualTo(1));
        }

        [Test]
        public void SplitTeams_PairsEveryMirroredCardAndSplitsTheRestAsUsual()
        {
            var guests = new List<int>
            {
                OnlineBalancer.Weight(10, 2), OnlineBalancer.Weight(9, 0), OnlineBalancer.Weight(10, 2),
                OnlineBalancer.Weight(10, 2), OnlineBalancer.Weight(10, 2), OnlineBalancer.Weight(10, 2),
            };
            var keys = new List<string>
            {
                "a116_xm551_exp", null, "g14_kpfpz_70r", "a116_xm551_exp", "g14_kpfpz_70r", "a116_xm551_exp",
            };
            var teams = OnlineBalancer.SplitTeams(OnlineBalancer.Weight(9, 0), guests, 7, null, keys);
            Assert.AreNotEqual(teams[0], teams[3], "Sheridan pair");
            Assert.AreNotEqual(teams[2], teams[4], "Kampfpanzer 70 pair");
            int[] count = { 1, 0 };
            foreach (var team in teams)
            {
                Assert.That(team, Is.EqualTo(0).Or.EqualTo(1));
                count[team]++;
            }
            Assert.LessOrEqual(System.Math.Abs(count[0] - count[1]), 1);
        }

        [Test]
        public void SplitTeams_WithoutSharedKeysMatchesThePlainSplit()
        {
            var random = new System.Random(11);
            for (var round = 0; round < 50; round++)
            {
                var guests = new List<int>();
                var keys = new List<string>();
                var size = random.Next(1, 14);
                for (var i = 0; i < size; i++)
                {
                    guests.Add(OnlineBalancer.Weight(random.Next(1, 11), random.Next(0, 4)));
                    keys.Add(i % 3 == 0 ? null : "card" + i);
                }
                var host = OnlineBalancer.Weight(random.Next(1, 11), random.Next(0, 4));
                Assert.AreEqual(
                    OnlineBalancer.SplitTeams(host, guests, 7),
                    OnlineBalancer.SplitTeams(host, guests, 7, "host", keys),
                    "round " + round);
            }
        }

        [Test]
        public void SplitTeams_RespectsTeamSize()
        {
            var guests = new List<int>();
            for (var i = 0; i < 14; i++) { guests.Add(OnlineBalancer.Weight(5, 1)); }
            var teams = OnlineBalancer.SplitTeams(OnlineBalancer.Weight(5, 1), guests, 7);
            int[] count = { 1, 0 };
            var unseated = 0;
            foreach (var team in teams)
            {
                if (team < 0) { unseated++; } else { count[team]++; }
            }
            Assert.AreEqual(7, count[0]);
            Assert.AreEqual(7, count[1]);
            Assert.AreEqual(1, unseated);
        }
    
        [Test]
        public void TwoHumansDoNotStartAtOnce()
        {
            var d = OnlineBalancer.Decide(5f, 2, 14, true, 45f, 60f);
            Assert.IsFalse(d.Start);
            Assert.AreEqual(40f, d.StartsIn, 1e-4f);
        }

        [Test]
        public void BalancedLobbyStartsAt45Seconds()
        {
            Assert.IsFalse(OnlineBalancer.Decide(44.9f, 4, 14, true, 45f, 60f).Start);
            var d = OnlineBalancer.Decide(45f, 4, 14, true, 45f, 60f);
            Assert.IsTrue(d.Start);
            Assert.IsFalse(d.Lenient);
        }

        [Test]
        public void UnbalancedLobbyWaitsFor60SecondsThenStartsLeniently()
        {
            var d = OnlineBalancer.Decide(50f, 3, 14, false, 45f, 60f);
            Assert.IsFalse(d.Start);
            Assert.AreEqual(10f, d.StartsIn, 1e-4f);
            d = OnlineBalancer.Decide(60f, 3, 14, false, 45f, 60f);
            Assert.IsTrue(d.Start);
            Assert.IsTrue(d.Lenient);
        }

        [Test]
        public void LoneHumanWaitsPatiently()
        {
            var d = OnlineBalancer.Decide(600f, 1, 14, false, 45f, 60f);
            Assert.IsFalse(d.Start);
            Assert.Less(d.StartsIn, 0f);
        }

        [Test]
        public void CountdownStartsWithTheFirstGuestNotTheQueue()
        {
            Assert.AreEqual(0f, OnlineBalancer.CountdownElapsed(600f, -1f));
            // Ten minutes alone, then a guest: the full 45 s countdown.
            var d = OnlineBalancer.Decide(OnlineBalancer.CountdownElapsed(600f, 600f), 2, 14, true, 45f, 60f);
            Assert.IsFalse(d.Start);
            Assert.AreEqual(45f, d.StartsIn, 1e-4f);
            d = OnlineBalancer.Decide(OnlineBalancer.CountdownElapsed(620f, 600f), 2, 14, false, 45f, 60f);
            Assert.AreEqual(40f, d.StartsIn, 1e-4f);
            Assert.AreEqual(0f, OnlineBalancer.CountdownElapsed(10f, 20f), "clock skew never goes negative");
        }

        [Test]
        public void FullBattleStartsImmediately()
        {
            Assert.IsTrue(OnlineBalancer.Decide(1f, 14, 14, false, 45f, 60f).Start);
        }

        [Test]
        public void NormalBalanceNeedsEvenMatchedHumans()
        {
            var t5heavy = OnlineBalancer.Weight(5, 0);
            var t5medium = OnlineBalancer.Weight(5, 1);
            var t7heavy = OnlineBalancer.Weight(7, 0);
            Assert.IsTrue(OnlineBalancer.NormalBalanceOk(t5heavy, new List<int> { t5medium }, 7));
            Assert.IsFalse(OnlineBalancer.NormalBalanceOk(t5heavy, new List<int> { t7heavy }, 7), "two tiers apart");
            Assert.IsFalse(OnlineBalancer.NormalBalanceOk(t5heavy, new List<int> { t5heavy, t5heavy }, 7), "odd head count");
            Assert.IsFalse(OnlineBalancer.NormalBalanceOk(t5heavy, new List<int>(), 7), "alone");
            Assert.IsTrue(OnlineBalancer.NormalBalanceOk(t7heavy, new List<int> { t5heavy, t7heavy, t5medium }, 7));
        }
    
        [Test]
        public void MergeTarget_AloneInTheLobby_StaysPut()
        {
            var rooms = new List<OnlineBalancer.Room> { Room("bl-own", 5, 1) };
            Assert.AreEqual(-1, OnlineBalancer.MergeTarget(rooms, "bl-own", 5, 1, 120f, 120f),
                "a lone host with nobody else in the lobby must not reopen its battle");
            Assert.AreEqual(-1, OnlineBalancer.MergeTarget(new List<OnlineBalancer.Room>(), "bl-own", 5, 1, 120f, 120f));
        }

        [Test]
        public void MergeTarget_TwoLoneHosts_OnlyTheGreaterNameMoves()
        {
            var rooms = new List<OnlineBalancer.Room> { Room("bl-a", 5, 1), Room("bl-b", 5, 1) };
            Assert.AreEqual(0, OnlineBalancer.MergeTarget(rooms, "bl-b", 5, 1, 5f, 0f));
            Assert.AreEqual(-1, OnlineBalancer.MergeTarget(rooms, "bl-a", 5, 1, 5f, 0f),
                "the lower name waits for the other host to come");
        }

        [Test]
        public void MergeTarget_LowerNameMovesAfterPatience()
        {
            var rooms = new List<OnlineBalancer.Room> { Room("bl-a", 5, 1), Room("bl-b", 6, 1) };
            Assert.AreEqual(-1, OnlineBalancer.MergeTarget(rooms, "bl-a", 5, 1, 30f, OnlineBalancer.MergePatienceSeconds - 0.1f));
            Assert.AreEqual(1, OnlineBalancer.MergeTarget(rooms, "bl-a", 5, 1, 30f, OnlineBalancer.MergePatienceSeconds));
        }

        [Test]
        public void MergeTarget_GatheringBattleIsJoinedAtOnce()
        {
            var rooms = new List<OnlineBalancer.Room> { Room("bl-a", 5, 1), Room("bl-z", 5, 3, 30f) };
            Assert.AreEqual(1, OnlineBalancer.MergeTarget(rooms, "bl-a", 5, 1, 5f, 0f));
        }

        [Test]
        public void MergeTarget_RespectsSettleTierSkipAndStartingBattles()
        {
            var rooms = new List<OnlineBalancer.Room> { Room("bl-a", 5, 1), Room("bl-b", 5, 1) };
            Assert.AreEqual(-1, OnlineBalancer.MergeTarget(rooms, "bl-b", 5, 1, OnlineBalancer.MergeSettleSeconds - 0.1f, 0f),
                "a battle just opened waits for the lobby listing to settle");
            Assert.AreEqual(-1, OnlineBalancer.MergeTarget(rooms, "bl-b", 5, 1, 30f, 30f, new HashSet<string> { "bl-a" }));
            var far = new List<OnlineBalancer.Room> { Room("bl-a", 8, 1), Room("bl-b", 5, 1) };
            Assert.AreEqual(-1, OnlineBalancer.MergeTarget(far, "bl-b", 5, 1, 30f, 30f));
            Assert.AreEqual(-1, OnlineBalancer.MergeTarget(far, "bl-b", 5, 2, 30f, 30f));
            var closing = new List<OnlineBalancer.Room> { Room("bl-a", 5, 4, 1f), Room("bl-b", 5, 1) };
            Assert.AreEqual(-1, OnlineBalancer.MergeTarget(closing, "bl-b", 5, 1, 30f, 30f));
        }
    }
}
