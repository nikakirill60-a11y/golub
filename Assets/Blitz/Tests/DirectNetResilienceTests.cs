using System;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Direct;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Own-server packet-loss handling: the server's coast of a silent player,
    /// the lossless fire counter, the hit message and the client's smoothing.
    /// </summary>
    public sealed class DirectNetResilienceTests
    {
        [Test]
        public void CoastHoldsTheLastInputThenFadesToAStop()
        {
            Assert.AreEqual(1f, DirectInputCoast.DriveScale(0f));
            Assert.AreEqual(1f, DirectInputCoast.DriveScale(DirectInputCoast.HoldSeconds));
            Assert.AreEqual(0.5f, DirectInputCoast.DriveScale(DirectInputCoast.HoldSeconds + DirectInputCoast.FadeSeconds * 0.5f), 1e-4f);
            Assert.AreEqual(0f, DirectInputCoast.DriveScale(DirectInputCoast.HoldSeconds + DirectInputCoast.FadeSeconds));
            Assert.AreEqual(0f, DirectInputCoast.DriveScale(30f));
            Assert.AreEqual(1f, DirectInputCoast.DriveScale(float.NaN), "a broken clock must not stop the tank");
            var previous = 1f;
            for (var t = 0f; t < 2f; t += 0.01f)
            {
                var scale = DirectInputCoast.DriveScale(t);
                Assert.LessOrEqual(scale, previous + 1e-6f, "monotonic");
                previous = scale;
            }
            Assert.IsFalse(DirectInputCoast.Expired(DirectInputCoast.HoldSeconds));
            Assert.IsTrue(DirectInputCoast.Expired(DirectInputCoast.HoldSeconds + DirectInputCoast.FadeSeconds));
            // The coast covers several lost snapshots of a typical loss burst.
            Assert.GreaterOrEqual(DirectInputCoast.HoldSeconds, 0.5f);
        }

        [Test]
        public void HeldFireIsReleasedSoonAfterTheInputStops()
        {
            Assert.IsTrue(DirectInputCoast.KeepsFire(0f));
            Assert.IsTrue(DirectInputCoast.KeepsFire(DirectInputCoast.FireSeconds));
            Assert.IsFalse(DirectInputCoast.KeepsFire(DirectInputCoast.FireSeconds + 0.01f));
            Assert.Less(DirectInputCoast.FireSeconds, DirectInputCoast.HoldSeconds);
        }

        [Test]
        public void InputCarriesTheFireCounter()
        {
            var w = new NetDataWriter();
            new DirectInput { Sequence = 9, Forward = 1f, Right = -0.5f, Aim = new Vector3(1, 2, 3), FireSequence = 200, UprisingAction = 5, UprisingSequence = 3 }.Write(w);
            Assert.AreEqual(1 + DirectInput.Size, w.Length);
            var r = new NetDataReader(w.Data, 1, w.Length);
            var input = DirectInput.Read(r);
            Assert.AreEqual(200, input.FireSequence);
            Assert.AreEqual(5, input.UprisingAction);
            Assert.AreEqual(0, r.AvailableBytes);
            Assert.GreaterOrEqual(DirectProtocol.Version, 2, "the input layout changed");
        }

        [Test]
        public void HitRoundTripsAndKeepsTheVerdict()
        {
            var info = new DamageInfo { Dmg = 0, DmgBase = 240, DmgType = DamageType.Shell, Shell = ShellType.Apcr, ShotId = 77, Pos = new BlitzLegacy.Core.Maps.Vec3(1, 2, 3) };
            var w = new NetDataWriter();
            DirectHit.From(4, 11, info).Write(w);
            Assert.AreEqual(1 + DirectHit.Size, w.Length);
            Assert.AreEqual((byte)DirectMessage.Hit, w.Data[0]);
            var hit = DirectHit.Read(new NetDataReader(w.Data, 1, w.Length));
            Assert.AreEqual(4, hit.Victim);
            Assert.AreEqual(11, hit.Attacker);
            var copy = hit.ToInfo(null);
            Assert.IsTrue(copy.IsBlock(), "a bounce stays a bounce");
            Assert.AreEqual(240, copy.DmgBase);
            Assert.AreEqual(ShellType.Apcr, copy.Shell);
            Assert.AreEqual(77, copy.ShotId);
            Assert.AreEqual(3f, copy.Pos.Z);

            var pen = DirectHit.From(4, -1, new DamageInfo { Dmg = 180, DmgBase = 180, DmgType = DamageType.Shell }).ToInfo(null);
            Assert.IsFalse(pen.IsBlock());
            Assert.IsTrue(pen.ShouldHaveMarker());
            Assert.AreEqual(DirectHit.NoSlot, DirectHit.From(4, -1, info).Attacker);
            Assert.IsTrue(DirectHit.From(1, 2, new DamageInfo { Dmg = 3, DmgType = DamageType.Ability, SuppressFeedback = true }).ToInfo(null).SuppressFeedback);
        }

        [Test]
        public void MalformedHitsAreRejected()
        {
            var w = new NetDataWriter();
            DirectHit.From(1, 2, new DamageInfo { Dmg = 5, DmgBase = 5 }).Write(w);
            var bytes = (byte[])w.Data.Clone();
            bytes[1 + 1 + 1 + 4 + 4] = 99; // damage type
            Assert.Throws<FormatException>(() => DirectHit.Read(new NetDataReader(bytes, 1, w.Length)));
            Assert.Throws<FormatException>(() => DirectHit.Read(new NetDataReader(w.Data, 1, 6)), "short");
        }

        private static void Run(ref DirectSmoothing s, Vector3 target, Vector3 velocity, out Vector3 shown, out bool corrected)
        {
            Quaternion rotation;
            corrected = s.Step(target, Quaternion.identity, velocity, 1f / 60f, out shown, out rotation);
        }

        [Test]
        public void SteadyMotionIsNeverCorrected()
        {
            var s = new DirectSmoothing();
            var v = new Vector3(15f, 0f, 0f);
            for (var i = 0; i < 120; i++)
            {
                Vector3 shown;
                bool corrected;
                var target = v * (i / 60f);
                Run(ref s, target, v, out shown, out corrected);
                Assert.IsFalse(corrected, "frame " + i);
                Assert.AreEqual(target, shown);
            }
        }

        [Test]
        public void LateDataGlidesInsteadOfTeleporting()
        {
            var s = new DirectSmoothing();
            Vector3 shown;
            bool corrected;
            Run(ref s, Vector3.zero, Vector3.zero, out shown, out corrected);
            // The dead-reckoned guess was 2 m off: the server's pose lands at once.
            Run(ref s, new Vector3(2f, 0f, 0f), Vector3.zero, out shown, out corrected);
            Assert.IsTrue(corrected);
            Assert.Less(shown.x, 0.5f, "no visible jump on the frame the data lands");
            var last = shown.x;
            for (var i = 0; i < 30; i++)
            {
                Run(ref s, new Vector3(2f, 0f, 0f), Vector3.zero, out shown, out corrected);
                Assert.GreaterOrEqual(shown.x, last - 1e-5f, "glides one way");
                Assert.Less(shown.x - last, 0.4f, "no single-frame jump");
                last = shown.x;
            }
            Assert.AreEqual(2f, shown.x, 0.05f, "converged on the server pose within half a second");
        }

        [Test]
        public void RespawnTeleportIsTakenAsIs()
        {
            var s = new DirectSmoothing();
            Vector3 shown;
            bool corrected;
            Run(ref s, Vector3.zero, Vector3.zero, out shown, out corrected);
            Run(ref s, new Vector3(150f, 0f, 40f), Vector3.zero, out shown, out corrected);
            Assert.AreEqual(new Vector3(150f, 0f, 40f), shown);
            Assert.IsFalse(s.Active);
        }

        [Test]
        public void RotationSnapIsSmoothedToo()
        {
            var s = new DirectSmoothing();
            Vector3 p;
            Quaternion r;
            s.Step(Vector3.zero, Quaternion.identity, Vector3.zero, 1f / 60f, out p, out r);
            var turned = Quaternion.Euler(0f, 40f, 0f);
            Assert.IsTrue(s.Step(Vector3.zero, turned, Vector3.zero, 1f / 60f, out p, out r));
            Assert.Greater(Quaternion.Angle(r, turned), 30f);
            for (var i = 0; i < 40; i++) { s.Step(Vector3.zero, turned, Vector3.zero, 1f / 60f, out p, out r); }
            Assert.Less(Quaternion.Angle(r, turned), 0.5f);
        }
    }
}
