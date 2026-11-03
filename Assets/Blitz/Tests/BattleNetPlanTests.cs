using System.IO;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The plan and the hello cross the wire from untrusted peers, so their
    /// codecs must round-trip exactly and refuse anything malformed.
    /// </summary>
    public sealed class BattleNetPlanTests
    {
        private static BattleNetPlan.Slot Slot(int team, int owner, string tank)
        {
            var slot = new BattleNetPlan.Slot
            {
                Team = team, Owner = owner, TankId = tank, Name = "P" + owner,
                Gun = 1, Engine = 2, Tracks = 0, Turret = 1, Skill = 3,
            };
            slot.Ammo.Add(10);
            slot.Ammo.Add(5);
            slot.Equipment.Add("rammer");
            return slot;
        }

        private static BattleNetPlan Plan()
        {
            var plan = new BattleNetPlan { MapId = "rudniki", Mode = BattleMode.Normal, Build = "1.0.0+1" };
            plan.Slots.Add(Slot(0, 0, "t-34"));
            plan.Slots.Add(Slot(0, BattleNetPlan.BotOwner, "kv1"));
            plan.Slots.Add(Slot(1, 1, "pziv"));
            plan.Slots.Add(Slot(1, BattleNetPlan.BotOwner, "pzvi"));
            return plan;
        }

        [Test]
        public void Plan_RoundTrips()
        {
            var back = BattleNetPlan.FromBytes(Plan().ToBytes());
            Assert.AreEqual("rudniki", back.MapId);
            Assert.AreEqual("1.0.0+1", back.Build);
            Assert.AreEqual(4, back.Slots.Count);
            Assert.AreEqual(1, back.Slots[2].Owner);
            Assert.AreEqual("pziv", back.Slots[2].TankId);
            Assert.AreEqual(BattleNetPlan.BotOwner, back.Slots[3].Owner);
            CollectionAssert.AreEqual(new[] { 10, 5 }, back.Slots[0].Ammo);
            CollectionAssert.AreEqual(new[] { "rammer" }, back.Slots[0].Equipment);
            Assert.AreEqual(2, back.SlotOf(1));
        }

        [Test]
        public void Plan_RejectsUnorderedTeams()
        {
            var plan = Plan();
            plan.Slots.Reverse();
            Assert.Throws<InvalidDataException>(plan.Validate);
        }

        [Test]
        public void Plan_RejectsDuplicateOwner()
        {
            var plan = Plan();
            plan.Slots[3].Owner = 1;
            Assert.Throws<InvalidDataException>(plan.Validate);
        }

        [Test]
        public void Plan_RejectsTruncatedAndGarbage()
        {
            var bytes = Plan().ToBytes();
            var cut = new byte[bytes.Length / 2];
            System.Array.Copy(bytes, cut, cut.Length);
            Assert.Throws<InvalidDataException>(() => BattleNetPlan.FromBytes(cut));
            Assert.Throws<InvalidDataException>(() => BattleNetPlan.FromBytes(new byte[] { 9, 9, 9, 9, 9 }));
            Assert.Throws<InvalidDataException>(() => BattleNetPlan.FromBytes(null));
        }

        [Test]
        public void Plan_RoundTripsSeats()
        {
            var plan = Plan();
            plan.Slots[0].Seat = new string('a', BattleNetPlan.MaxSeatLength);
            plan.Slots[2].Seat = "b1";
            var back = BattleNetPlan.FromBytes(plan.ToBytes());
            Assert.AreEqual(0, back.SlotOfSeat(new string('a', BattleNetPlan.MaxSeatLength)));
            Assert.AreEqual(2, back.SlotOfSeat("b1"));
            Assert.AreEqual(-1, back.SlotOfSeat("missing"));
            Assert.AreEqual(-1, back.SlotOfSeat(string.Empty), "bots have no seat to claim");
            Assert.AreEqual(-1, back.SlotOfSeat(null));
        }

        [Test]
        public void Plan_RejectsOversizeSeat()
        {
            var plan = Plan();
            plan.Slots[0].Seat = new string('a', BattleNetPlan.MaxSeatLength + 1);
            Assert.Throws<InvalidDataException>(() => BattleNetPlan.FromBytes(plan.ToBytes()));
        }

        [Test]
        public void Hello_RoundTripsAndRejectsOversize()
        {
            var hello = new BattleNetHello { Build = "1.0.0+1", Name = "Tanker", TankId = "t-34", Gun = 1, Engine = 1, Tracks = 1, Turret = 0 };
            hello.Ammo.Add(7);
            var back = BattleNetHello.FromBytes(hello.ToBytes());
            Assert.AreEqual("Tanker", back.Name);
            Assert.AreEqual("t-34", back.TankId);
            CollectionAssert.AreEqual(new[] { 7 }, back.Ammo);
            Assert.Throws<InvalidDataException>(() => BattleNetHello.FromBytes(new byte[2048]));
        }

        [Test]
        public void Plan_RoundTripsHumanConsumables()
        {
            var plan = Plan();
            plan.Slots[0].Consumables.AddRange(new[] { ConsumableCatalog.RepairKit, ConsumableCatalog.Berserk, ConsumableCatalog.Afterburning });
            plan.Slots[2].Consumables.Add(ConsumableCatalog.Fuel);
            var back = BattleNetPlan.FromBytes(plan.ToBytes());
            CollectionAssert.AreEqual(new[] { "repair_kit", "berserk", "afterburning" }, back.Slots[0].Consumables);
            CollectionAssert.AreEqual(new[] { "fuel" }, back.Slots[2].Consumables);
            CollectionAssert.IsEmpty(back.Slots[1].Consumables, "a bot brings no hangar loadout");
        }

        [Test]
        public void Plan_RejectsForgedConsumables()
        {
            var tooMany = Plan();
            tooMany.Slots[0].Consumables.AddRange(new[] { "repair_kit", "berserk", "afterburning", "fuel" });
            Assert.Throws<InvalidDataException>(tooMany.Validate);

            var unknown = Plan();
            unknown.Slots[2].Consumables.Add("golden_shell");
            Assert.Throws<InvalidDataException>(unknown.Validate);

            var twice = Plan();
            twice.Slots[0].Consumables.AddRange(new[] { "berserk", "berserk" });
            Assert.Throws<InvalidDataException>(() => twice.ToBytes());
        }

        [Test]
        public void Hello_RoundTripsConsumablesAndRefusesForgedOnes()
        {
            var hello = new BattleNetHello { Build = "1.0.0+1", Name = "Tanker", TankId = "t-34" };
            hello.Consumables.AddRange(new[] { "repair_kit", "berserk" });
            var back = BattleNetHello.FromBytes(hello.ToBytes());
            CollectionAssert.AreEqual(new[] { "repair_kit", "berserk" }, back.Consumables);

            var twice = new BattleNetHello { Build = "1.0.0+1", Name = "Tanker", TankId = "t-34" };
            twice.Consumables.AddRange(new[] { "berserk", "berserk" });
            Assert.Throws<InvalidDataException>(() => BattleNetHello.FromBytes(twice.ToBytes()));

            var tooMany = new BattleNetHello { Build = "1.0.0+1", Name = "Tanker", TankId = "t-34" };
            tooMany.Consumables.AddRange(new[] { "repair_kit", "berserk", "afterburning", "fuel" });
            Assert.Throws<InvalidDataException>(() => BattleNetHello.FromBytes(tooMany.ToBytes()));

            var unknown = new BattleNetHello { Build = "1.0.0+1", Name = "Tanker", TankId = "t-34" };
            unknown.Consumables.Add("golden_shell");
            Assert.Throws<InvalidDataException>(() => BattleNetHello.FromBytes(unknown.ToBytes()));
        }

        [Test]
        public void Consumables_TravelAsKnownDistinctIdsInSlotOrder()
        {
            CollectionAssert.AreEqual(
                new[] { "berserk", "repair_kit", "fuel" },
                BattleNetConsumables.Sanitized(new[] { "", "berserk", "nope", "berserk", "repair_kit", null, "fuel", "afterburning" }));
            CollectionAssert.IsEmpty(BattleNetConsumables.Sanitized(null));
        }
    }
}
