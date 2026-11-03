using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>Wheel spring calibration: a supported tank rests with its visible
    /// track on the surface instead of sinking into the terrain.</summary>
    public sealed class TankRideHeightTests
    {
        private const int Wheels = 12;

        /// <summary>Static compression of one wheel carrying mass/supporting.</summary>
        private static float RestCompression(float mass, float sag, int supporting, float g = 1f)
        {
            var weight = g * mass * TankSuspension.SourceGravity / supporting;
            float low = 0, high = 5;
            for (var i = 0; i < 80; i++)
            {
                var mid = (low + high) * .5f;
                if (TankSuspension.WheelSpringForce(mass, Wheels, sag, mid, 0) < weight) low = mid; else high = mid;
            }
            return (low + high) * .5f;
        }

        /// <summary>The ray ends at the track bottom when compression equals the sag,
        /// so compression beyond the sag is how deep the track is buried.</summary>
        [TestCase(5000f, .6f)][TestCase(38000f, .94442f)][TestCase(188000f, 1.2f)]
        public void FullSupportHoldsTrackBottomOnSurface(float mass, float tracksHeight)
        {
            var sag = (tracksHeight + .05f) * .22f;
            Assert.That(RestCompression(mass, sag, Wheels) - sag, Is.EqualTo(0f).Within(.001f));
        }

        [TestCase(38000f, .94442f)][TestCase(188000f, 1.2f)][TestCase(5000f, .3f)]
        public void HalfSupportStaysNearSurface(float mass, float tracksHeight)
        {
            var sag = Mathf.Max(.04f, (tracksHeight + .05f) * .22f);
            var sink = RestCompression(mass, sag, Wheels / 2) - sag;
            // Without the bump stop half support would bury the track by a full sag.
            Assert.Greater(sink, 0f);
            Assert.Less(sink, .08f);
            Assert.Less(sink, sag * .45f);
        }

        [Test]
        public void BumpStopLimitsBurialOnThreeGLoad()
        {
            const float mass = 60000f, sag = .22f;
            var sink = RestCompression(mass, sag, Wheels, 3f) - sag;
            // A plain spring would compress two extra sags under 3 g.
            Assert.Less(sink, sag * .5f);
        }

        [Test]
        public void UnloadedDroopKeepsContactWithoutForce()
        {
            // Lifting the hull shortens compression: force falls smoothly to zero
            // rather than stepping, and a ray at its end carries nothing.
            var rest = TankSuspension.WheelSpringForce(38000, Wheels, .22f, .22f, 0);
            var lifted = TankSuspension.WheelSpringForce(38000, Wheels, .22f, .11f, 0);
            Assert.That(lifted, Is.EqualTo(rest * .5f).Within(rest * .01f));
            Assert.That(TankSuspension.WheelSpringForce(38000, Wheels, .22f, 0f, 0), Is.EqualTo(0f));
        }

        [Test]
        public void DamperOpposesCompressionAndNeverPulls()
        {
            var still = TankSuspension.WheelSpringForce(38000, Wheels, .2f, .2f, 0);
            Assert.Greater(TankSuspension.WheelSpringForce(38000, Wheels, .2f, .2f, -1), still);
            Assert.Less(TankSuspension.WheelSpringForce(38000, Wheels, .2f, .2f, 1), still);
            Assert.That(TankSuspension.WheelSpringForce(38000, Wheels, .2f, .2f, 50), Is.EqualTo(0f));
        }

        [Test]
        public void LoadSharesBalanceAnOffsetCentreOfMass()
        {
            var wheels = new Vector3[12];
            for (var i = 0; i < 6; i++)
            {
                wheels[i] = new Vector3(-1, .9f, 2 - i * .8f);
                wheels[i + 6] = new Vector3(1, .9f, 2 - i * .8f);
            }
            var com = new Vector3(.1f, .5f, -.3f);
            var shares = TankSuspension.StaticLoadShares(wheels, com);
            float sum = 0, momentZ = 0, momentX = 0;
            for (var i = 0; i < 12; i++) { sum += shares[i]; momentZ += shares[i] * (wheels[i].z - (-0f)); momentX += shares[i] * wheels[i].x; }
            Assert.That(sum, Is.EqualTo(12f).Within(1e-4f));
            // Mean wheel z is 0: the load moment must sit on the centre of mass.
            Assert.That(momentZ / sum, Is.EqualTo(com.z).Within(1e-3f));
            Assert.That(momentX / sum, Is.EqualTo(com.x).Within(1e-3f));
            foreach (var share in shares) Assert.Greater(share, 0f);
        }

        [Test]
        public void LoadSharesAreEvenForCentredMass()
        {
            var wheels = new[] { new Vector3(-1, 0, 1), new Vector3(1, 0, 1), new Vector3(-1, 0, -1), new Vector3(1, 0, -1) };
            foreach (var share in TankSuspension.StaticLoadShares(wheels, Vector3.zero))
                Assert.That(share, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(TankSuspension.StaticLoadShares(null, Vector3.zero), Is.Empty);
        }

        [Test]
        public void InvalidInputsProduceNoForce()
        {
            Assert.That(TankSuspension.WheelSpringForce(0, Wheels, .2f, .1f, 0), Is.EqualTo(0f));
            Assert.That(TankSuspension.WheelSpringForce(1000, 0, .2f, .1f, 0), Is.EqualTo(0f));
            Assert.That(TankSuspension.WheelSpringForce(1000, Wheels, 0, .1f, 0), Is.EqualTo(0f));
        }
    }
}
