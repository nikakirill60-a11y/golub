using System;
using System.IO;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Direct;
using BlitzLegacy.Runtime.Battle;
using LiteNetLib.Utils;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The hard mod tank ban: online play seats stock tanks only. A mod tank
    /// (or any id this build does not ship) is refused in the hello, the
    /// connect request and every plan slot with <see cref="ModTankOnlineException"/>,
    /// which makes an honest client leave the battle on its own.
    /// </summary>
    public sealed class ModTankOnlineBanTests
    {
        private const string ModTank = "zz-test-mod-tank";

        private Func<string, bool> _saved;

        [SetUp]
        public void SaveStockList()
        {
            _saved = ModTankOnlineBan.StockResolver;
        }

        [TearDown]
        public void RestoreStockList()
        {
            ModTankOnlineBan.StockResolver = _saved;
        }

        private static BattleNetPlan Plan(string guestTank)
        {
            var plan = new BattleNetPlan { MapId = "rudniki", Mode = BattleMode.Normal, Build = "1.0.0+1" };
            plan.Slots.Add(new BattleNetPlan.Slot { Team = 0, Owner = 0, TankId = "ms-1", Name = "P0" });
            plan.Slots.Add(new BattleNetPlan.Slot { Team = 1, Owner = 1, TankId = guestTank, Name = "P1" });
            return plan;
        }

        private static BattleNetHello Hello(string tank)
        {
            return new BattleNetHello { Build = "1.0.0+1", Name = "Tanker", TankId = tank };
        }

        /// <summary>Writes bytes with every tank allowed, the way a tampered peer would.</summary>
        private static T Tampered<T>(Func<T> write)
        {
            var real = ModTankOnlineBan.StockResolver;
            ModTankOnlineBan.StockResolver = id => true;
            try { return write(); }
            finally { ModTankOnlineBan.StockResolver = real; }
        }

        [Test]
        public void StockList_IsRegisteredAndKnowsOnlyShippedTanks()
        {
            Assert.IsNotNull(ModTankOnlineBan.StockResolver);
            Assert.IsTrue(ModTankOnlineBan.IsStock("ms-1"));
            Assert.IsTrue(ModTankOnlineBan.IsStock("KV1"));
            Assert.IsTrue(StockTanks.IsStock(BattleSession.DefaultTankId));
            Assert.IsTrue(DirectWorld.IsKnownTank("kv1"));
            Assert.IsFalse(ModTankOnlineBan.IsStock(ModTank));
            Assert.IsFalse(DirectWorld.IsKnownTank(ModTank));
            Assert.IsFalse(ModTankOnlineBan.IsStock(string.Empty));
            Assert.IsFalse(ModTankOnlineBan.IsStock(null));
            Assert.IsFalse(ModTankOnlineBan.IsStock("   "));
        }

        [Test]
        public void StockList_FailsClosed()
        {
            ModTankOnlineBan.StockResolver = null;
            Assert.IsFalse(ModTankOnlineBan.IsStock("ms-1"));
            ModTankOnlineBan.StockResolver = id => { throw new InvalidOperationException("broken"); };
            Assert.IsFalse(ModTankOnlineBan.IsStock("ms-1"));
            Assert.Throws<ModTankOnlineException>(() => Hello("ms-1").ToBytes());
        }

        [Test]
        public void Plan_WithAModTank_IsRefused()
        {
            Assert.DoesNotThrow(() => Plan("kv1").Validate());
            var error = Assert.Throws<ModTankOnlineException>(() => Plan(ModTank).Validate());
            Assert.AreEqual(ModTankOnlineBan.BattleErrorKey, error.Key);
            Assert.AreEqual(ModTank, error.TankId);
            Assert.IsInstanceOf<IOException>(error);
            Assert.Throws<ModTankOnlineException>(() => Plan(ModTank).ToBytes());
            Assert.AreEqual(1, ModTankOnlineBan.FirstViolation(Plan(ModTank)));
            Assert.AreEqual(-1, ModTankOnlineBan.FirstViolation(Plan("kv1")));
        }

        [Test]
        public void Plan_FromTheWire_WithAModTank_IsRefused()
        {
            var bytes = Tampered(() => Plan(ModTank).ToBytes());
            var error = Assert.Throws<ModTankOnlineException>(() => BattleNetPlan.FromBytes(bytes));
            Assert.AreEqual(ModTankOnlineBan.BattleErrorKey, OnlineBan.KeyOf(error));
            Assert.IsFalse(SandboxOnlineBan.IsBan(error));
            Assert.AreEqual("kv1", BattleNetPlan.FromBytes(Plan("kv1").ToBytes()).Slots[1].TankId);
        }

        [Test]
        public void Hello_WithAModTank_NeverLeavesTheDevice()
        {
            Assert.DoesNotThrow(() => Hello("ms-1").ToBytes());
            var error = Assert.Throws<ModTankOnlineException>(() => Hello(ModTank).ToBytes());
            Assert.AreEqual(ModTankOnlineBan.ErrorKey, error.Key);
        }

        [Test]
        public void Hello_FromTheWire_WithAModTank_IsRefused()
        {
            var bytes = Tampered(() => Hello(ModTank).ToBytes());
            var error = Assert.Throws<ModTankOnlineException>(() => BattleNetHello.FromBytes(bytes));
            Assert.AreEqual(ModTankOnlineBan.ErrorKey, OnlineBan.KeyOf(error));
            Assert.AreEqual("ms-1", BattleNetHello.FromBytes(Hello("ms-1").ToBytes()).TankId);
        }

        [Test]
        public void DirectConnect_NeverSendsOrAcceptsAModTank()
        {
            var token = new byte[DirectProtocol.TokenLength];
            Assert.Throws<ModTankOnlineException>(() =>
                DirectProtocol.WriteConnect(new NetDataWriter(), "1.0.0+1", BattleMode.Normal, token, Hello(ModTank)));

            var w = new NetDataWriter();
            Tampered(() =>
            {
                DirectProtocol.WriteConnect(w, "1.0.0+1", BattleMode.Normal, token, Hello(ModTank));
                return true;
            });
            var error = Assert.Throws<ModTankOnlineException>(() => DirectProtocol.ReadConnect(new NetDataReader(w.CopyData())));
            Assert.AreEqual(ModTankOnlineBan.ErrorKey, OnlineBan.KeyOf(error));
        }

        [Test]
        public void OnlineBan_TellsTheReasonsApart()
        {
            var own = new ModTankOnlineException(ModTank, "test", null);
            Assert.AreEqual(ModTankOnlineBan.ErrorKey, own.Key);
            Assert.AreEqual(SandboxOnlineBan.ErrorKey, OnlineBan.KeyOf(new SandboxOnlineException("test")));
            Assert.AreEqual(ModTankOnlineBan.BattleErrorKey, OnlineBan.KeyOf(new InvalidOperationException("wrapped",
                new ModTankOnlineException(ModTank, "test", ModTankOnlineBan.BattleErrorKey))));
            Assert.IsNull(OnlineBan.KeyOf(new IOException("plain")));
            Assert.IsNull(OnlineBan.KeyOf(null));
            Assert.IsTrue(ModTankOnlineBan.IsBan(own));
            Assert.IsFalse(SandboxOnlineBan.IsBan(own));
            Assert.IsFalse(ModTankOnlineBan.IsBan(new SandboxOnlineException("test")));
            Assert.AreEqual("online:error/mod_tank", ModTankOnlineBan.ErrorKey);
            Assert.AreEqual("online:error/mod_tank_battle", ModTankOnlineBan.BattleErrorKey);
        }
    }
}
