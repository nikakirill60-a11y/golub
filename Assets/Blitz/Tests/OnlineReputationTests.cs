using BlitzLegacy.Core.Battle;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The hidden reputation decides who hosts an online battle: players who
    /// keep leaving or crashing must lose the host to cleaner players.
    /// </summary>
    public sealed class OnlineReputationTests
    {
        private static ReputationRecord Record(int clean, int abandoned, int ping)
        {
            var record = new ReputationRecord();
            for (var i = 0; i < clean; i++)
            {
                OnlineReputation.BattleStarted(record);
                OnlineReputation.BattleSettled(record, true, ping);
            }
            for (var i = 0; i < abandoned; i++)
            {
                OnlineReputation.BattleStarted(record);
                OnlineReputation.BattleSettled(record, false, ping);
            }
            return record;
        }

        private static BattleNetPlan.Slot Human(int owner, int reputation)
        {
            return new BattleNetPlan.Slot { Owner = owner, TankId = "t-34", Name = "P" + owner, Seat = "s" + owner, Reputation = reputation };
        }

        private static BattleNetPlan TwoHumans(int hostRep, int guestRep)
        {
            var plan = new BattleNetPlan { MapId = "rudniki", Mode = BattleMode.Normal, Build = "1" };
            plan.Slots.Add(Human(0, hostRep));
            plan.Slots.Add(new BattleNetPlan.Slot { Owner = BattleNetPlan.BotOwner, TankId = "kv1", Name = "Bot" });
            plan.Slots.Add(Human(1, guestRep));
            plan.Slots[2].Team = 1;
            plan.HostOwner = 0;
            return plan;
        }

        [Test]
        public void CleanLowPingVeteranOutscoresQuitter()
        {
            var hours = 30d * 3600d;
            var clean = OnlineReputation.Score(Record(20, 0, 40), hours);
            var quitter = OnlineReputation.Score(Record(5, 15, 40), hours);
            Assert.Greater(clean, quitter);
            Assert.IsFalse(OnlineReputation.IsBad(clean));
            Assert.IsTrue(OnlineReputation.IsBad(quitter));
            Assert.LessOrEqual(clean, OnlineReputation.Max);
        }

        [Test]
        public void HighPingAndNoPlaytimeLowerTheScore()
        {
            var low = OnlineReputation.Score(Record(10, 0, 40), 0d);
            var high = OnlineReputation.Score(Record(10, 0, 400), 0d);
            var seasoned = OnlineReputation.Score(Record(10, 0, 40), 40d * 3600d);
            Assert.Greater(low, high);
            Assert.Greater(seasoned, low);
        }

        [Test]
        public void FreshAccountIsNotBad()
        {
            Assert.IsFalse(OnlineReputation.IsBad(OnlineReputation.Score(new ReputationRecord(), 0d)));
        }

        [Test]
        public void BattleLeftOpenCountsAsCrash()
        {
            var record = new ReputationRecord();
            OnlineReputation.BattleStarted(record);
            Assert.IsTrue(record.Open);
            OnlineReputation.SettleCrash(record);
            Assert.AreEqual(1, record.Abandoned);
            Assert.IsFalse(record.Open);
            OnlineReputation.SettleCrash(record);
            Assert.AreEqual(1, record.Abandoned, "a crash is counted once");
            OnlineReputation.BattleStarted(record);
            OnlineReputation.BattleStarted(record);
            Assert.AreEqual(2, record.Abandoned, "a new battle settles the previous open one");
        }

        [Test]
        public void BadHostGivesTheHostAwayEvenWithTwoHumans()
        {
            var plan = TwoHumans(200, 800);
            Assert.AreEqual(2, OnlineReputation.BetterHost(plan, 0));
        }

        [Test]
        public void CleanHostKeepsTheHostWithinTheMargin()
        {
            var plan = TwoHumans(700, 700 + OnlineReputation.HandoverMargin - 1);
            Assert.AreEqual(-1, OnlineReputation.BetterHost(plan, 0));
            plan.Slots[2].Reputation = 700 + OnlineReputation.HandoverMargin;
            Assert.AreEqual(2, OnlineReputation.BetterHost(plan, 0));
        }

        [Test]
        public void BadGuestNeverTakesTheHostFromBadHost()
        {
            var plan = TwoHumans(200, 300);
            Assert.AreEqual(-1, OnlineReputation.BetterHost(plan, 0));
        }

        [Test]
        public void SuccessorOrderPrefersAliveThenCleanThenReputation()
        {
            var plan = TwoHumans(900, 400);
            plan.Slots.Add(Human(5, 600));
            plan.Slots.Add(Human(3, 800));
            plan.Slots.Add(Human(BattleSession_Detached(6), 999));
            var order = OnlineReputation.SuccessorOrder(plan, 0, slot => slot != 4);
            CollectionAssert.AreEqual(new[] { 3, 2, 4 }, order, "host and detached seats are skipped, dead seat goes last");
        }

        private static int BattleSession_Detached(int slot) { return -100 - slot; }

        [Test]
        public void PlanCarriesReputationAndHostOwner()
        {
            var plan = TwoHumans(321, 876);
            plan.HostOwner = 1;
            var copy = BattleNetPlan.FromBytes(plan.ToBytes());
            Assert.AreEqual(1, copy.HostOwner);
            Assert.AreEqual(321, copy.Slots[0].Reputation);
            Assert.AreEqual(876, copy.Slots[2].Reputation);
        }

        [Test]
        public void HelloCarriesClampedReputation()
        {
            var hello = new BattleNetHello { Build = "1.0.0+1", Name = "Tanker", TankId = "t-34", Gun = 1, Engine = 1, Tracks = 1, Turret = 0, Reputation = 5000 };
            hello.Ammo.Add(7);
            var copy = BattleNetHello.FromBytes(hello.ToBytes());
            Assert.AreEqual(OnlineReputation.Max, copy.Reputation);
        }

        [Test]
        public void PlannedTargetStartsFirstOthersAfterItCreatedTheRoom()
        {
            var target = OnlineReputation.HandoverStartDelay(true, true, 0, false);
            Assert.AreEqual(OnlineReputation.PlannedTargetStart, target);
            Assert.GreaterOrEqual(OnlineReputation.HandoverStartDelay(true, false, 1, false) - target, 1f);
            // Even a bad target is not delayed: it was chosen.
            Assert.AreEqual(target, OnlineReputation.HandoverStartDelay(true, true, 0, true));
        }

        [Test]
        public void UnplannedSurvivorsStartInSuccessorOrder()
        {
            var first = OnlineReputation.HandoverStartDelay(false, false, 0, false);
            var second = OnlineReputation.HandoverStartDelay(false, false, 1, false);
            var third = OnlineReputation.HandoverStartDelay(false, false, 2, false);
            Assert.Less(first, 0.5f);
            Assert.Greater(second, first);
            Assert.Greater(third, second);
        }

        [Test]
        public void BadSurvivorNeverStartsFirst()
        {
            Assert.Greater(OnlineReputation.HandoverStartDelay(false, false, 0, true), OnlineReputation.HandoverStartDelay(false, false, 2, false));
            Assert.Greater(OnlineReputation.HandoverStartDelay(true, false, 0, true), OnlineReputation.HandoverStartDelay(true, false, 0, false));
        }
    }
}
