using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlitzLegacy.Core.Time;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class NetworkTimeTests
    {
        private const double Now = 1750032000d;

        private sealed class Source : INetworkTimeSource
        {
            public readonly List<string> Hosts = new List<string>();
            public bool Fail;
            public bool ThrowFirst;
            public double Time = Now;
            public bool TryQuery(string host, int timeout, out double unix)
            {
                Hosts.Add(host);
                unix = Time;
                if (ThrowFirst && Hosts.Count == 1) throw new InvalidOperationException("first host failed");
                return !Fail && host != "bad";
            }
        }

        private sealed class BlockingSource : INetworkTimeSource, IDisposable
        {
            public int Calls;
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
            public readonly ManualResetEventSlim Release = new ManualResetEventSlim();
            public bool TryQuery(string host, int timeout, out double unix)
            {
                Interlocked.Increment(ref Calls);
                Entered.Set();
                Release.Wait(2000);
                unix = Now;
                return true;
            }
            public void Dispose() { Entered.Dispose(); Release.Dispose(); }
        }

        private static byte[] Reply(double received = Now, double transmitted = Now + 0.1d)
        {
            var reply = new byte[48];
            reply[0] = (4 << 3) | 4;
            reply[1] = 2;
            NetworkTimeProtocol.WriteTimestamp(reply, 32, received);
            NetworkTimeProtocol.WriteTimestamp(reply, 40, transmitted);
            return reply;
        }

        [Test]
        public void Request_IsVersionFourClientAndHasANonceInsteadOfDeviceTime()
        {
            var first = NetworkTimeProtocol.BuildRequest();
            var second = NetworkTimeProtocol.BuildRequest();
            Assert.AreEqual(48, first.Length);
            Assert.AreEqual(0x23, first[0]);
            var different = false;
            for (var i = 40; i < 48; i++) different |= first[i] != second[i];
            Assert.IsTrue(different, "independent requests must not share a stale-reply token");
        }

        [TestCase(1750032000.25d)]
        [TestCase(2208988800.5d)]
        public void Timestamp_RoundTripsFractionsAndThe2036Era(double timestamp)
        {
            var packet = new byte[48];
            NetworkTimeProtocol.WriteTimestamp(packet, 40, timestamp);
            Assert.AreEqual(timestamp, NetworkTimeProtocol.ReadTimestamp(packet, 40), 0.000002d);
        }

        [Test]
        public void Response_RemovesServerProcessingThenAddsHalfNetworkDelay()
        {
            var reply = Reply();
            double unix;
            Assert.IsTrue(NetworkTimeProtocol.TryParseResponse(reply, reply.Length, 0.3d, out unix));
            Assert.AreEqual(Now + 0.2d, unix, 0.000002d);
        }

        [Test]
        public void Response_RequiresOurOriginNonceAtRuntime()
        {
            var request = NetworkTimeProtocol.BuildRequest();
            var reply = Reply();
            Array.Copy(request, 40, reply, 24, 8);
            double unix;
            Assert.IsTrue(NetworkTimeProtocol.TryParseResponse(reply, 48, 0.3d, request, out unix));
            reply[25] ^= 1;
            Assert.IsFalse(NetworkTimeProtocol.TryParseResponse(reply, 48, 0.3d, request, out unix));
        }

        [Test]
        public void Response_RejectsMalformedUnsyncedKissOfDeathAndInvalidDelays()
        {
            double unix;
            Assert.IsFalse(NetworkTimeProtocol.TryParseResponse(null, 48, 0.3d, out unix));
            Assert.IsFalse(NetworkTimeProtocol.TryParseResponse(new byte[8], 48, 0.3d, out unix));
            Assert.IsFalse(NetworkTimeProtocol.TryParseResponse(Reply(), 47, 0.3d, out unix));
            Assert.IsFalse(NetworkTimeProtocol.TryParseResponse(Reply(), 49, 0.3d, out unix));
            foreach (var delay in new[] { -1d, double.NaN, double.PositiveInfinity, 20d })
                Assert.IsFalse(NetworkTimeProtocol.TryParseResponse(Reply(), 48, delay, out unix));
            foreach (var first in new byte[] { 0x23, 0xE4, 0x14 })
            {
                var reply = Reply(); reply[0] = first;
                Assert.IsFalse(NetworkTimeProtocol.TryParseResponse(reply, 48, 0.3d, out unix));
            }
            foreach (var stratum in new byte[] { 0, 16, 255 })
            {
                var reply = Reply(); reply[1] = stratum;
                Assert.IsFalse(NetworkTimeProtocol.TryParseResponse(reply, 48, 0.3d, out unix));
            }
            Assert.IsFalse(NetworkTimeProtocol.TryParseResponse(Reply(Now, Now - 1d), 48, 0.3d, out unix));
            Assert.IsFalse(NetworkTimeProtocol.TryParseResponse(Reply(Now, Now + 20d), 48, 0.3d, out unix));
            Assert.IsFalse(NetworkTimeProtocol.TryParseResponse(Reply(1600000000d, 1600000000d), 48, 0.3d, out unix));
        }

        [Test]
        public void Clock_FallsBackBetweenHostsAndAdvancesOnlyMonotonically()
        {
            var source = new Source();
            var elapsed = 100d;
            var clock = new OnlineClock(source, new[] { "bad", "good" }, () => elapsed);
            double unix;
            Assert.IsFalse(clock.TryGetNow(out unix));
            Assert.IsTrue(clock.SyncBlocking());
            CollectionAssert.AreEqual(new[] { "bad", "good" }, source.Hosts);
            elapsed += 12.5d;
            Assert.IsTrue(clock.TryGetNow(out unix));
            Assert.AreEqual(Now + 12.5d, unix);
            elapsed += OnlineClock.RefreshAfterSeconds;
            Assert.IsTrue(clock.NeedsRefresh);
            elapsed += OnlineClock.ExpireAfterSeconds;
            Assert.AreEqual(OnlineClockState.Offline, clock.State);
            Assert.IsFalse(clock.TryGetNow(out unix));
        }

        [Test]
        public void Clock_FailedRefreshClearsCachedOnlineTime()
        {
            var source = new Source();
            var clock = new OnlineClock(source, new[] { "good" }, () => 1d);
            Assert.IsTrue(clock.SyncBlocking());
            source.Fail = true;
            Assert.IsFalse(clock.SyncBlocking());
            double unix;
            Assert.AreEqual(OnlineClockState.Offline, clock.State);
            Assert.IsFalse(clock.TryGetNow(out unix));
            Assert.IsTrue(clock.NeedsRefresh);
        }

        [Test]
        public void Clock_OneThrowingHostDoesNotBlockTheFallback()
        {
            var source = new Source { ThrowFirst = true };
            var clock = new OnlineClock(source, new[] { "one", "two" }, () => 1d);
            Assert.IsTrue(clock.SyncBlocking());
            CollectionAssert.AreEqual(new[] { "one", "two" }, source.Hosts);
        }

        [Test]
        public void Clock_RejectsNonFiniteTimeAndOldSyncCannotUndoOfflineState()
        {
            foreach (var invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
            {
                var bad = new OnlineClock(new Source { Time = invalid }, new[] { "good" });
                Assert.IsFalse(bad.SyncBlocking());
                Assert.Throws<ArgumentOutOfRangeException>(() => bad.SetSynced(invalid));
            }
            using (var source = new BlockingSource())
            {
                var clock = new OnlineClock(source, new[] { "good" });
                clock.SetSynced(Now);
                var task = Task.Run(() => clock.SyncBlocking());
                Assert.IsTrue(source.Entered.Wait(1000));
                double unix;
                Assert.IsFalse(clock.TryGetNow(out unix), "purchases wait for an in-flight online check");
                clock.SetOffline();
                clock.RequestSync(true);
                Thread.Sleep(20);
                Assert.AreEqual(1, source.Calls, "retry does not create overlapping network workers");
                source.Release.Set();
                Assert.IsTrue(task.Wait(2000));
                Assert.IsFalse(task.Result, "cancelled sync must not report an accepted clock");
                Assert.AreEqual(OnlineClockState.Offline, clock.State);
                Assert.IsFalse(clock.TryGetNow(out unix));
            }
        }
    }
}
