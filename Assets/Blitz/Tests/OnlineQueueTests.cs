using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class OnlineQueueTests
    {
        private static int[] Tiers(params KeyValuePair<int, int>[] pairs)
        {
            var tiers = new int[10];
            foreach (var pair in pairs) { tiers[10 - pair.Key] = pair.Value; }
            return tiers;
        }

        [Test]
        public void EncodeDecode_RoundTrips()
        {
            var tiers = Tiers(new KeyValuePair<int, int>(10, 2), new KeyValuePair<int, int>(6, 1));
            var classes = new[] { 1, 1, 0, 1 };
            var mix = OnlineQueue.Encode(tiers, classes);
            Assert.AreEqual("2,0,0,0,1,0,0,0,0,0,1,1,0,1", mix);
            var t = new int[10];
            var c = new int[4];
            Assert.IsTrue(OnlineQueue.TryDecode(mix, t, c));
            CollectionAssert.AreEqual(tiers, t);
            CollectionAssert.AreEqual(classes, c);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("1,0,0")]
        [TestCase("1,0,0,0,0,0,0,0,0,0,1,0,0,0,5")]
        [TestCase("<b>1</b>,0,0,0,0,0,0,0,0,0,1,0,0,0")]
        [TestCase("-1,0,0,0,0,0,0,0,0,0,1,0,0,0")]
        [TestCase("99,0,0,0,0,0,0,0,0,0,1,0,0,0")]
        [TestCase("1,,0,0,0,0,0,0,0,0,1,0,0,0")]
        [TestCase("1 ,0,0,0,0,0,0,0,0,0,1,0,0,0")]
        public void Decode_RejectsGarbage(string mix)
        {
            Assert.IsFalse(OnlineQueue.TryDecode(mix, new int[10], new int[4]));
        }

        [Test]
        public void Aggregate_SumsAllRoomsOnEveryTier()
        {
            var rooms = new List<OnlineQueue.Room>
            {
                new OnlineQueue.Room { Players = 2, Tier = 10, Mix = OnlineQueue.Encode(Tiers(new KeyValuePair<int, int>(10, 1), new KeyValuePair<int, int>(9, 1)), new[] { 1, 1, 0, 0 }) },
                new OnlineQueue.Room { Players = 1, Tier = 6, Mix = OnlineQueue.Encode(Tiers(new KeyValuePair<int, int>(6, 1)), new[] { 0, 0, 1, 0 }) },
                new OnlineQueue.Room { Players = 0, Tier = 5 },
            };
            var s = OnlineQueue.Aggregate(rooms);
            Assert.AreEqual(3, s.Waiting);
            Assert.AreEqual(2, s.Rooms);
            Assert.AreEqual(1, s.TierCounts[0]);
            Assert.AreEqual(1, s.TierCounts[1]);
            Assert.AreEqual(1, s.TierCounts[4]);
            CollectionAssert.AreEqual(new[] { 1, 1, 1, 0 }, s.ClassCounts);
        }

        [Test]
        public void Aggregate_FallsBackToHostTierWhenMixMissingOrForged()
        {
            var rooms = new List<OnlineQueue.Room>
            {
                new OnlineQueue.Room { Players = 3, Tier = 8, Mix = null },
                // Claims 5 humans in a room of 1: forged, counted at the host tier.
                new OnlineQueue.Room { Players = 1, Tier = 4, Mix = "5,0,0,0,0,0,0,0,0,0,5,0,0,0" },
                new OnlineQueue.Room { Players = 2, Tier = 99, Mix = null },
            };
            var s = OnlineQueue.Aggregate(rooms);
            Assert.AreEqual(6, s.Waiting);
            Assert.AreEqual(3, s.TierCounts[2]);
            Assert.AreEqual(1, s.TierCounts[6]);
            Assert.AreEqual(0, s.TierCounts[0]);
            CollectionAssert.AreEqual(new[] { 0, 0, 0, 0 }, s.ClassCounts);
        }

        [Test]
        public void Merge_NeverUndercountsOwnRoom()
        {
            var into = new[] { 0, 2, 1 };
            OnlineQueue.Merge(into, new[] { 1, 1, 1 });
            CollectionAssert.AreEqual(new[] { 1, 2, 1 }, into);
        }
    }
}
