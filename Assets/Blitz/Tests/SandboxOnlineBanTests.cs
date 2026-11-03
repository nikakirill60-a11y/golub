using System;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Direct;
using BlitzLegacy.Runtime.Battle;
using LiteNetLib.Utils;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The hard client-side Sandbox ban: the Sandbox never travels over
    /// Photon Fusion or Direct, and anything from a peer that carries it is
    /// refused with <see cref="SandboxOnlineException"/>, which makes an
    /// honest client leave the battle on its own.
    /// </summary>
    public sealed class SandboxOnlineBanTests
    {
        private static byte[] RawPlan(int mode)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(BattleNetPlan.FormatVersion);
                writer.Write("1.0.0+1");
                writer.Write(0);
                writer.Write("rudniki");
                writer.Write(mode);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static BattleNetPlan Plan(BattleMode mode)
        {
            var plan = new BattleNetPlan { MapId = "rudniki", Mode = mode, Build = "1.0.0+1" };
            plan.Slots.Add(new BattleNetPlan.Slot { Team = 0, Owner = 0, TankId = "t-34", Name = "P0" });
            plan.Slots.Add(new BattleNetPlan.Slot { Team = 1, Owner = 1, TankId = "pziv", Name = "P1" });
            return plan;
        }

        [Test]
        public void Ban_OnlyTheSandboxViolates()
        {
            Assert.IsTrue(SandboxOnlineBan.Violates(BattleMode.Sandbox));
            foreach (var mode in BattleModeCatalog.SelectorModes)
            {
                Assert.IsFalse(SandboxOnlineBan.Violates(mode), mode.ToString());
                Assert.DoesNotThrow(() => SandboxOnlineBan.Ensure(mode, "test"));
            }

            Assert.IsTrue(SandboxOnlineBan.IsSandbox((int)BattleMode.Sandbox));
            Assert.IsFalse(SandboxOnlineBan.IsSandbox((int)BattleMode.Normal));
            Assert.Throws<SandboxOnlineException>(() => SandboxOnlineBan.Ensure(BattleMode.Sandbox, "test"));
        }

        [Test]
        public void Ban_IsRecognisedThroughWrappers()
        {
            var ban = new SandboxOnlineException("plan");
            Assert.IsInstanceOf<IOException>(ban, "transport layers treat it as a fatal read error");
            Assert.IsTrue(SandboxOnlineBan.IsBan(ban));
            Assert.IsTrue(SandboxOnlineBan.IsBan(new InvalidOperationException("outer", ban)));
            Assert.IsFalse(SandboxOnlineBan.IsBan(new InvalidDataException("other")));
            Assert.IsFalse(SandboxOnlineBan.IsBan(null));
            Assert.AreEqual("online:error/sandbox", SandboxOnlineBan.ErrorKey);
        }

        [Test]
        public void Plan_RefusesToCarryTheSandbox()
        {
            Assert.DoesNotThrow(() => Plan(BattleMode.Normal).ToBytes());
            Assert.Throws<SandboxOnlineException>(() => Plan(BattleMode.Sandbox).Validate());
            Assert.Throws<SandboxOnlineException>(() => Plan(BattleMode.Sandbox).ToBytes());
        }

        [Test]
        public void Plan_FromAHostileHostTripsTheBan()
        {
            Assert.Throws<SandboxOnlineException>(() => BattleNetPlan.FromBytes(RawPlan((int)BattleMode.Sandbox)));
            // Any other unknown mode is merely malformed.
            Assert.Throws<InvalidDataException>(() => BattleNetPlan.FromBytes(RawPlan(77)));
        }

        [Test]
        public void DirectConnect_NeverSendsOrAcceptsTheSandbox()
        {
            var hello = new BattleNetHello { Build = "1.0.0+1", Name = "Tanker", TankId = "t-34" };
            var token = new byte[DirectProtocol.TokenLength];
            Assert.Throws<SandboxOnlineException>(() =>
                DirectProtocol.WriteConnect(new NetDataWriter(), "1.0.0+1", BattleMode.Sandbox, token, hello));

            var w = new NetDataWriter();
            w.Put(DirectProtocol.ConnectKey);
            w.Put(DirectProtocol.Version);
            w.Put("1.0.0+1");
            w.Put((byte)BattleMode.Sandbox);
            w.Put(token);
            Assert.Throws<SandboxOnlineException>(() => DirectProtocol.ReadConnect(new NetDataReader(w.CopyData())));
        }
    }
}
