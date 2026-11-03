using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Direct;
using BlitzLegacy.Runtime.Battle;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class DirectProtocolTests
    {
        private static NetDataReader ReaderOf(NetDataWriter w)
        {
            return new NetDataReader(w.CopyData());
        }

        private static byte[] Token(byte fill)
        {
            var token = new byte[DirectProtocol.TokenLength];
            for (var i = 0; i < token.Length; i++) { token[i] = (byte)(fill + i); }
            return token;
        }

        [Test]
        public void ConnectRequestRoundTrips()
        {
            var hello = new BattleNetHello { Build = "2.0a2/202", Name = "Tanker", TankId = "t-34", Gun = 2 };
            hello.Ammo.Add(30);
            var w = new NetDataWriter();
            DirectProtocol.WriteConnect(w, "2.0a2/202", BattleMode.Uprising, Token(7), hello);

            var request = DirectProtocol.ReadConnect(ReaderOf(w));

            Assert.AreEqual(DirectProtocol.Version, request.Version);
            Assert.AreEqual("2.0a2/202", request.Build);
            Assert.AreEqual(BattleMode.Uprising, request.Mode);
            CollectionAssert.AreEqual(Token(7), request.Token);
            Assert.AreEqual("Tanker", request.Hello.Name);
            Assert.AreEqual("t-34", request.Hello.TankId);
            Assert.AreEqual(2, request.Hello.Gun);
            CollectionAssert.AreEqual(new[] { 30 }, request.Hello.Ammo);
        }

        [Test]
        public void ConnectRequestRejectsGarbage()
        {
            var w = new NetDataWriter();
            w.Put("someone-else");
            Assert.Throws<FormatException>(() => DirectProtocol.ReadConnect(ReaderOf(w)));

            // A truncated request (no token, no hello) must not read past the packet.
            w = new NetDataWriter();
            w.Put(DirectProtocol.ConnectKey);
            w.Put(DirectProtocol.Version);
            w.Put("2.0a2/202");
            w.Put((byte)BattleMode.Normal);
            w.Put(new byte[5]);
            Assert.Throws<FormatException>(() => DirectProtocol.ReadConnect(ReaderOf(w)));

            // A hello length beyond the packet.
            w = new NetDataWriter();
            w.Put(DirectProtocol.ConnectKey);
            w.Put(DirectProtocol.Version);
            w.Put("2.0a2/202");
            w.Put((byte)BattleMode.Normal);
            w.Put(Token(1));
            w.Put(900);
            w.Put(new byte[10]);
            Assert.Throws<FormatException>(() => DirectProtocol.ReadConnect(ReaderOf(w)));
        }

        [Test]
        public void OtherProtocolVersionOnlyReportsTheVersion()
        {
            var w = new NetDataWriter();
            w.Put(DirectProtocol.ConnectKey);
            w.Put(DirectProtocol.Version + 1);
            var request = DirectProtocol.ReadConnect(ReaderOf(w));
            Assert.AreEqual(DirectProtocol.Version + 1, request.Version);
            Assert.IsNull(request.Hello);
        }

        [Test]
        public void SafeStringRefusesLengthsPastThePacket()
        {
            var w = new NetDataWriter();
            w.Put((ushort)500);
            w.Put(new byte[4]);
            Assert.Throws<FormatException>(() => DirectProtocol.GetSafeString(ReaderOf(w), 64));

            w = new NetDataWriter();
            w.Put(new string('x', 70));
            Assert.Throws<FormatException>(() => DirectProtocol.GetSafeString(ReaderOf(w), 64));

            w = new NetDataWriter();
            w.Put("привет");
            Assert.AreEqual("привет", DirectProtocol.GetSafeString(ReaderOf(w), 64));
        }

        [Test]
        public void InputRoundTripsWithItsDeclaredSize()
        {
            var input = new DirectInput
            {
                Sequence = 42,
                Forward = 1f,
                Right = -0.5f,
                Aim = new Vector3(1f, 2f, 3f),
                Buttons = DirectButtons.Fire | DirectButtons.HasAim,
                ShellId = 2,
                UprisingAction = 3,
                UprisingSequence = 9,
            };
            var w = new NetDataWriter();
            input.Write(w);
            var r = ReaderOf(w);
            Assert.AreEqual((byte)DirectMessage.Input, r.GetByte());
            Assert.AreEqual(DirectInput.Size, r.AvailableBytes);
            var back = DirectInput.Read(r);
            Assert.AreEqual(42u, back.Sequence);
            Assert.AreEqual(-0.5f, back.Right);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), back.Aim);
            Assert.IsTrue(back.Has(DirectButtons.Fire));
            Assert.IsFalse(back.Has(DirectButtons.Arty));
            Assert.AreEqual(9, back.UprisingSequence);
        }

        [Test]
        public void InputGuardClampsAndFlagsImpossibleAxes()
        {
            var input = new DirectInput { Forward = 50f, Right = float.NaN, Aim = Vector3.one, Buttons = DirectButtons.HasAim };
            bool hard;
            Assert.IsFalse(DirectInputGuard.Sanitize(ref input, out hard));
            Assert.IsTrue(hard);
            Assert.AreEqual(1f, input.Forward);
            Assert.AreEqual(0f, input.Right);

            input = new DirectInput { Forward = 0.5f, Aim = new Vector3(float.PositiveInfinity, 0f, 0f), Buttons = DirectButtons.HasAim };
            Assert.IsFalse(DirectInputGuard.Sanitize(ref input, out hard));
            Assert.IsFalse(hard);
            Assert.IsFalse(input.Has(DirectButtons.HasAim));

            input = new DirectInput { Forward = -1f, Right = 1f, Aim = new Vector3(10f, 0f, 10f) };
            Assert.IsTrue(DirectInputGuard.Sanitize(ref input, out hard));
        }

        [Test]
        public void SnapshotChunkRoundTrips()
        {
            var state = new DirectTankState
            {
                Position = new Vector3(1f, 2f, 3f),
                Rotation = Quaternion.Euler(0f, 90f, 0f),
                Yaw = 0.5f,
                Pitch = -0.1f,
                Health = 777,
                Forward = 1f,
                Right = -1f,
                SpeedMs = 9f,
                WheelHits = 6,
                Flags = 5,
                Aim = new Vector3(4f, 5f, 6f),
                Dispersion = 0.3f,
                TeamDamage = 120,
            };
            var uprising = new UprisingSnapshot { Owned = 3, Elixir = 40, RespawnIn = 2.5f, RespawnsLeft = 2 };
            var w = new NetDataWriter();
            DirectSnapshot.WriteHeader(w, 10, 123.5, 14, DirectSnapshot.ModeUprising, 4, 2);
            for (var i = 0; i < 2; i++) { state.Write(w); DirectUprising.Write(w, uprising); }
            Assert.AreEqual(DirectSnapshot.HeaderSize + 2 * DirectSnapshot.SlotSize(DirectSnapshot.ModeUprising), w.Length);

            var r = ReaderOf(w);
            Assert.AreEqual((byte)DirectMessage.Snapshot, r.GetByte());
            var h = DirectSnapshot.ReadHeader(r);
            Assert.AreEqual(10u, h.Tick);
            Assert.AreEqual(123.5, h.Time);
            Assert.AreEqual(4, h.First);
            Assert.AreEqual(2, h.Count);
            Assert.IsTrue(h.Uprising);
            var back = DirectTankState.Read(r);
            var up = DirectUprising.Read(r);
            Assert.AreEqual(777, back.Health);
            Assert.AreEqual(120, back.TeamDamage);
            Assert.AreEqual(1f, back.Forward);
            Assert.AreEqual(5, back.Flags);
            Assert.AreEqual(40, up.Elixir);
            Assert.AreEqual(2, up.RespawnsLeft);
        }

        [Test]
        public void SnapshotHeaderRejectsSlotsOutOfRange()
        {
            var w = new NetDataWriter();
            DirectSnapshot.WriteHeader(w, 1, 0, 14, DirectSnapshot.ModeNone, 12, 5);
            var r = ReaderOf(w);
            r.GetByte();
            Assert.Throws<FormatException>(() => DirectSnapshot.ReadHeader(r));
        }

        [Test]
        public void SnapshotsFitTheSmallestPathMtu()
        {
            // LiteNetLib starts at ~500 bytes before MTU discovery.
            foreach (var mode in new[] { DirectSnapshot.ModeNone, DirectSnapshot.ModeUprising, DirectSnapshot.ModeBigBoss })
            {
                var per = DirectSnapshot.SlotsPerPacket(500, mode);
                Assert.GreaterOrEqual(per, 1);
                Assert.LessOrEqual(DirectSnapshot.HeaderBytes(mode) + per * DirectSnapshot.SlotSize(mode), 500, "mode " + mode);
            }
        }

        [Test]
        public void BigBossSnapshotCarriesTheTicketsAndEverySeatField()
        {
            var state = new DirectTankState
            {
                Position = new Vector3(1f, 2f, 3f),
                Rotation = Quaternion.identity,
                Health = 900,
                Flags = DirectWorld.BigBossValidFlag,
            };
            var boss = new BigBossSnapshot
            {
                RoleId = 6, Flags = BigBossSnapshot.FlagRailgun | BigBossSnapshot.FlagShield, NextRoleId = -1, Lives = 2,
                LifeSerial = 3, Points = 212.5f, RespawnIn = 4f, Ready0 = 1f, Ready1 = 2f, Ready2 = 3f,
                ShieldIn = 5f, ShieldDuration = 10f, PoisonCastIn = 6f, AuraIn = 7f, PoisonedIn = 8f, PoisonedTotal = 9f,
            };
            var w = new NetDataWriter();
            DirectSnapshot.WriteHeader(w, 7, 50.25, 14, DirectSnapshot.ModeBigBoss, 13, 1, 27, 40000);
            Assert.AreEqual(DirectSnapshot.HeaderBytes(DirectSnapshot.ModeBigBoss), w.Length);
            state.Write(w);
            DirectBigBoss.Write(w, boss);
            Assert.AreEqual(DirectSnapshot.HeaderBytes(DirectSnapshot.ModeBigBoss) + DirectSnapshot.SlotSize(DirectSnapshot.ModeBigBoss), w.Length);

            var r = ReaderOf(w);
            Assert.AreEqual((byte)DirectMessage.Snapshot, r.GetByte());
            var h = DirectSnapshot.ReadHeader(r);
            Assert.IsTrue(h.BigBoss);
            Assert.IsFalse(h.Uprising);
            Assert.AreEqual(13, h.First);
            Assert.AreEqual(27, h.TeamA);
            Assert.AreEqual(short.MaxValue, h.TeamB, "clamped to the wire range");
            Assert.AreEqual(DirectWorld.BigBossValidFlag, DirectTankState.Read(r).Flags);
            var back = DirectBigBoss.Read(r);
            Assert.AreEqual(0, r.AvailableBytes, "the declared size is what goes on the wire");
            Assert.AreEqual(6, back.RoleId);
            Assert.AreEqual(boss.Flags, back.Flags);
            Assert.AreEqual(-1, back.NextRoleId);
            Assert.AreEqual(2, back.Lives);
            Assert.AreEqual(3, back.LifeSerial);
            Assert.AreEqual(212.5f, back.Points);
            Assert.AreEqual(4f, back.RespawnIn);
            for (var i = 0; i < 3; i++) { Assert.AreEqual(i + 1f, back.Ready(i)); }
            Assert.AreEqual(5f, back.ShieldIn);
            Assert.AreEqual(10f, back.ShieldDuration);
            Assert.AreEqual(6f, back.PoisonCastIn);
            Assert.AreEqual(7f, back.AuraIn);
            Assert.AreEqual(8f, back.PoisonedIn);
            Assert.AreEqual(9f, back.PoisonedTotal);
        }

        [Test]
        public void SnapshotHeaderRejectsUnknownModesAndBrokenTickets()
        {
            var w = new NetDataWriter();
            DirectSnapshot.WriteHeader(w, 1, 0, 14, 3, 0, 1);
            var r = ReaderOf(w);
            r.GetByte();
            Assert.Throws<FormatException>(() => DirectSnapshot.ReadHeader(r), "unknown mode");

            // A Big Boss header whose tickets are negative or cut off.
            const int modeByte = 1 + 4 + 8 + 1;
            w = new NetDataWriter();
            DirectSnapshot.WriteHeader(w, 1, 0, 14, DirectSnapshot.ModeNone, 0, 1);
            w.Put((short)5);
            w.Put((short)-1);
            var bytes = w.CopyData();
            bytes[modeByte] = DirectSnapshot.ModeBigBoss;
            r = new NetDataReader(bytes);
            r.GetByte();
            Assert.Throws<FormatException>(() => DirectSnapshot.ReadHeader(r), "negative tickets");

            w = new NetDataWriter();
            DirectSnapshot.WriteHeader(w, 1, 0, 14, DirectSnapshot.ModeNone, 0, 1);
            bytes = w.CopyData();
            bytes[modeByte] = DirectSnapshot.ModeBigBoss;
            r = new NetDataReader(bytes);
            r.GetByte();
            Assert.Throws<FormatException>(() => DirectSnapshot.ReadHeader(r), "missing tickets");

            Assert.AreEqual(DirectSnapshot.ModeBigBoss, DirectSnapshot.ModeOf(BattleMode.BigBoss));
            Assert.AreEqual(DirectSnapshot.ModeUprising, DirectSnapshot.ModeOf(BattleMode.Uprising));
            Assert.AreEqual(DirectSnapshot.ModeNone, DirectSnapshot.ModeOf(BattleMode.Gravity));
            Assert.AreEqual(DirectSnapshot.HeaderSize, DirectSnapshot.HeaderBytes(DirectSnapshot.ModeUprising));
        }

        [Test]
        public void RocketsRoundTrip()
        {
            var rockets = new List<DirectRocket>
            {
                new DirectRocket { Id = 5, Position = Vector3.one, Velocity = Vector3.up },
                new DirectRocket { Id = 6, Position = Vector3.zero, Velocity = Vector3.forward },
            };
            var w = new NetDataWriter();
            DirectSnapshot.WriteRockets(w, 3, 9.0, rockets, 1, 1);
            var r = ReaderOf(w);
            Assert.AreEqual((byte)DirectMessage.Rockets, r.GetByte());
            var back = new List<DirectRocket>();
            uint tick;
            double time;
            DirectSnapshot.ReadRockets(r, out tick, out time, back);
            Assert.AreEqual(1, back.Count);
            Assert.AreEqual(6, back[0].Id);
            Assert.AreEqual(Vector3.forward, back[0].Velocity);
        }

        [Test]
        public void LobbyRoundTrips()
        {
            var lobby = new DirectLobby
            {
                Players = 3, Capacity = 14, StartsIn = 12.5f, Waiting = 4, Busy = true,
                Tiers = new[] { 0, 1, 2, 0, 0, 0, 0, 0, 0, 0 },
                Classes = new[] { 1, 1, 1, 0 },
            };
            var w = new NetDataWriter();
            lobby.Write(w);
            var r = ReaderOf(w);
            Assert.AreEqual((byte)DirectMessage.Lobby, r.GetByte());
            var back = DirectLobby.Read(r);
            Assert.AreEqual(3, back.Players);
            Assert.AreEqual(12.5f, back.StartsIn);
            Assert.IsTrue(back.Busy);
            CollectionAssert.AreEqual(lobby.Tiers, back.Tiers);
            CollectionAssert.AreEqual(new int[OnlineQueue.Tiers], back.QueueTiers);
        }

        [Test]
        public void SeatIsAStableHashOfTheToken()
        {
            var seat = DirectProtocol.SeatOf(Token(1));
            Assert.AreEqual(64, seat.Length);
            Assert.AreEqual(seat, DirectProtocol.SeatOf(Token(1)));
            Assert.AreNotEqual(seat, DirectProtocol.SeatOf(Token(2)));
            Assert.IsNull(DirectProtocol.SeatOf(new byte[3]));
        }

        [Test]
        public void ServerAddressParses()
        {
            string host;
            int port;
            Assert.IsTrue(DirectConfig.TryParse("16.59.91.192:7777", out host, out port));
            Assert.AreEqual("16.59.91.192", host);
            Assert.AreEqual(7777, port);
            Assert.IsTrue(DirectConfig.TryParse(" example.org ", out host, out port));
            Assert.AreEqual("example.org", host);
            Assert.AreEqual(DirectProtocol.DefaultPort, port);
            Assert.IsTrue(DirectConfig.TryParse("[::1]:9000", out host, out port));
            Assert.AreEqual("::1", host);
            Assert.AreEqual(9000, port);
            Assert.IsFalse(DirectConfig.TryParse("host:99999", out host, out port));
            Assert.IsFalse(DirectConfig.TryParse("", out host, out port));
            Assert.IsFalse(DirectConfig.TryParse(":7777", out host, out port));
        }

        [Test]
        public void DedicatedSetupPromotesTheFirstGuest()
        {
            var net = new BattleNetSetup { Dedicated = true };
            net.Guests.Add(new BattleNetSetup.Guest { Owner = 4, Seat = "a", Hello = new BattleNetHello() });
            net.Guests.Add(new BattleNetSetup.Guest { Owner = 7, Seat = "b", Hello = new BattleNetHello() });
            Assert.IsTrue(net.PromoteAnchor());
            Assert.AreEqual(4, net.Anchor.Owner);
            Assert.AreEqual(4, net.LocalOwner);
            Assert.AreEqual("a", net.LocalSeat);
            Assert.AreEqual(1, net.Guests.Count);
            Assert.AreEqual(7, net.Guests[0].Owner);

            Assert.IsFalse(new BattleNetSetup { Dedicated = false }.PromoteAnchor());
            Assert.IsFalse(new BattleNetSetup { Dedicated = true }.PromoteAnchor());
        }

        private sealed class FakeService : IBattleOnlineService
        {
            public void Begin(IBattleOnlineSink sink, BattleMode mode) { }
            public void Cancel() { }
        }

        [Test]
        public void OnlineServicePicksTheSelectedServerOrTheAvailableOne()
        {
            var photon = new FakeService();
            var direct = new FakeService();
            Assert.AreSame(direct, BattleOnline.Pick(SettingsData.OnlineServerDirect, photon, direct));
            Assert.AreSame(photon, BattleOnline.Pick(SettingsData.OnlineServerPhoton, photon, direct));
            Assert.AreSame(photon, BattleOnline.Pick(SettingsData.OnlineServerDirect, photon, null));
            Assert.AreSame(direct, BattleOnline.Pick(SettingsData.OnlineServerPhoton, null, direct));
            Assert.IsNull(BattleOnline.Pick(SettingsData.OnlineServerDirect, null, null));
        }

        [Test]
        public void OnlineServerSettingPersistsAndClamps()
        {
            var settings = new SettingsData();
            Assert.AreEqual(SettingsData.OnlineServerDirect, settings.OnlineServer);
            settings.SetInt("online_server", 9);
            Assert.AreEqual(SettingsData.OnlineServerPhoton, settings.OnlineServer);
            var back = SettingsData.FromJson(settings.ToJson());
            Assert.AreEqual(SettingsData.OnlineServerPhoton, back.GetInt("online_server"));
        }
    }
}
