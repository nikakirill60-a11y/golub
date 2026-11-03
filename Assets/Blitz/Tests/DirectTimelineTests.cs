using System;
using System.Collections.Generic;
using BlitzLegacy.Direct;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The client's snapshot playback clock: just behind the newest snapshot
    /// on a calm link, later on a jittery one, and a server hitch is forgotten
    /// within the window instead of leaving the playback ahead of the data.
    /// </summary>
    public sealed class DirectTimelineTests
    {
        private const double ClockDiff = 1000.0;
        private const double Frame = 1.0 / 60.0;
        private const double Interval = 1.0 / DirectProtocol.SnapshotRate;

        private struct Packet
        {
            public double ServerTime;
            public double Arrival;
        }

        private sealed class Run
        {
            public int Frames;
            public int Starved;
            public int Backwards;
            public double LastRender;
            public double Newest;
        }

        /// <summary>Plays the packets frame by frame; counts frames past the newest snapshot.</summary>
        private static Run Play(DirectTimeline timeline, List<Packet> packets, double from, double until, Run run = null)
        {
            run = run ?? new Run { Newest = double.MinValue, LastRender = double.MinValue };
            packets.Sort((x, y) => x.Arrival.CompareTo(y.Arrival));
            var next = 0;
            while (next < packets.Count && packets[next].Arrival < from) { next++; }
            for (var t = from; t < until; t += Frame)
            {
                while (next < packets.Count && packets[next].Arrival <= t)
                {
                    timeline.OnPacket(packets[next].ServerTime, packets[next].Arrival);
                    run.Newest = Math.Max(run.Newest, packets[next].ServerTime);
                    next++;
                }
                if (!timeline.HasClock) { continue; }
                var render = timeline.Advance(t);
                run.Frames++;
                if (render > run.Newest) { run.Starved++; }
                if (render < run.LastRender) { run.Backwards++; }
                run.LastRender = render;
            }
            return run;
        }

        private static List<Packet> Link(double until, double latency, double jitter, int seed, double slipAt = -1, double slip = 0)
        {
            var random = new Random(seed);
            var packets = new List<Packet>();
            for (var send = 0.0; send < until; send += Interval)
            {
                var server = send + ClockDiff - (slipAt >= 0 && send >= slipAt ? slip : 0);
                packets.Add(new Packet { ServerTime = server, Arrival = send + latency + random.NextDouble() * jitter });
            }
            return packets;
        }

        [Test]
        public void CalmLinkPlaysJustBehindTheNewestSnapshot()
        {
            var timeline = new DirectTimeline();
            var run = Play(timeline, Link(6, 0.09, 0.0, 1), 0, 6);
            Assert.AreEqual(0, run.Starved, "never past the newest snapshot");
            Assert.AreEqual(0, run.Backwards);
            Assert.LessOrEqual(run.Newest - run.LastRender, 0.12, "not needlessly late");
            Assert.AreEqual(DirectTimeline.MinDelay, timeline.Delay, 1e-6);
        }

        [Test]
        public void JitteryLinkPlaysLaterInsteadOfFreezing()
        {
            var timeline = new DirectTimeline();
            var packets = Link(20, 0.09, 0.15, 7);
            Play(timeline, packets, 0, 4); // learns the link
            var run = Play(timeline, packets, 4, 20);
            Assert.GreaterOrEqual(timeline.Delay, 0.15);
            Assert.Less(run.Starved, run.Frames / 50, "at most 2% of the frames past the data");
            Assert.AreEqual(0, run.Backwards, "the playback never runs backwards");
            Assert.AreEqual(0, timeline.Snaps);
        }

        [Test]
        public void ServerHitchIsForgottenWithinTheWindow()
        {
            var timeline = new DirectTimeline();
            // The server simulation loses 0.5 s at t=5 (a long frame).
            var packets = Link(14, 0.09, 0.0, 3, 5, 0.5);
            var run = Play(timeline, packets, 0, 9);
            var after = Play(timeline, packets, 9, 14, new Run { Newest = run.Newest, LastRender = double.MinValue });
            Assert.AreEqual(0, after.Starved, "caught up with the slipped clock");
        }

        [Test]
        public void RestartedServerClockStartsOver()
        {
            var timeline = new DirectTimeline();
            timeline.OnPacket(ClockDiff + 50, 50);
            timeline.Advance(50);
            // A new server process: its clock starts near zero.
            timeline.OnPacket(3, 51);
            var render = timeline.Advance(51);
            Assert.Less(render, 3);
            Assert.Greater(render, 2.5);
        }
    }
}
