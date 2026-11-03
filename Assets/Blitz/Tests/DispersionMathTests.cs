using System;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the port of Godot <c>DispersionMath</c>: the deadzones, the
    /// quadratic combine of the movement penalties and the recovery that has to
    /// read the same at any tick rate.
    /// </summary>
    public sealed class DispersionMathTests
    {
        private const float Tolerance = 1e-4f;

        // The MS-1 37 mm gun: disp 0.54, disp_factors (0.16, 4, 2).
        private const float BaseDispersion = 0.54f;
        private const float MoveFactor = 0.16f;

        [Test]
        public void MotionDispersion_IgnoresCrawlingInsideTheDeadzone()
        {
            var dispersion = DispersionMath.MotionDispersion(
                BaseDispersion,
                MoveFactor,
                MoveFactor,
                MoveFactor,
                DispersionMath.LinearDeadzoneKmh - 0.01f,
                DispersionMath.AngularDeadzoneDeg - 0.01f,
                DispersionMath.AngularDeadzoneDeg - 0.01f);

            Assert.That(dispersion, Is.EqualTo(BaseDispersion).Within(Tolerance));
        }

        [Test]
        public void MotionDispersion_CombinesThePenaltiesQuadratically()
        {
            var dispersion = DispersionMath.MotionDispersion(
                BaseDispersion, MoveFactor, 0f, 0f, 10f, 0f, 0f);
            var penalty = MoveFactor * 10f;

            Assert.That(
                dispersion,
                Is.EqualTo(BaseDispersion * (float)Math.Sqrt(1f + penalty * penalty))
                    .Within(Tolerance));
        }

        [Test]
        public void MotionDispersion_TreatsNegativeFactorsAsStanding()
        {
            var dispersion = DispersionMath.MotionDispersion(
                BaseDispersion, -2f, -2f, -2f, 30f, 20f, 20f);

            Assert.That(dispersion, Is.EqualTo(BaseDispersion).Within(Tolerance));
        }

        [Test]
        public void FromVelocity_ConvertsIntoKmhAndDegrees()
        {
            var fromVelocity = DispersionMath.FromVelocity(
                BaseDispersion, MoveFactor, 0.5f, 0.25f, 10f, 1f, 12f);
            var expected = DispersionMath.MotionDispersion(
                BaseDispersion,
                MoveFactor,
                0.5f,
                0.25f,
                36f,
                (float)(180.0 / Math.PI),
                12f);

            Assert.That(fromVelocity, Is.EqualTo(expected).Within(Tolerance));
        }

        [Test]
        public void AfterShot_BloomsByTheFactorAndNeverShrinks()
        {
            var bloomed = DispersionMath.AfterShot(BaseDispersion, BaseDispersion, 4f);
            var kept = DispersionMath.AfterShot(BaseDispersion, 2f, 0.5f);

            Assert.That(bloomed, Is.EqualTo(BaseDispersion * 4f).Within(Tolerance));
            Assert.That(kept, Is.EqualTo(2f).Within(Tolerance));
        }

        [Test]
        public void Recover_ReadsTheSameAtThirtyAndSixtyHertz()
        {
            const float aimTime = 1.9f;
            var once = DispersionMath.Recover(2f, BaseDispersion, aimTime, 1f / 30f);
            var twice = DispersionMath.Recover(
                DispersionMath.Recover(2f, BaseDispersion, aimTime, 1f / 60f),
                BaseDispersion,
                aimTime,
                1f / 60f);

            Assert.That(twice, Is.EqualTo(once).Within(Tolerance));
            Assert.That(once, Is.LessThan(2f));
        }

        [Test]
        public void Recover_FloorsAtTheBaseAndSnapsWithoutAimTime()
        {
            Assert.That(
                DispersionMath.Recover(BaseDispersion, BaseDispersion, 1.9f, 10f),
                Is.EqualTo(BaseDispersion).Within(Tolerance));
            Assert.That(
                DispersionMath.Recover(2f, BaseDispersion, 0f, 1f / 60f),
                Is.EqualTo(BaseDispersion).Within(Tolerance));
        }
    }
}
