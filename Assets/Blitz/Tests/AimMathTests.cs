using BlitzLegacy.Core.Tanks;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the aim math against Godot <c>base_tank.gd</c> and <c>projectile.gd</c>.
    /// </summary>
    public sealed class AimMathTests
    {
        private const float Tolerance = 1e-4f;

        [Test]
        public void FposMod_CarriesTheDivisorSign()
        {
            Assert.That(AimMath.FposMod(-1f, 3f), Is.EqualTo(2f).Within(Tolerance));
            Assert.That(AimMath.FposMod(7f, 3f), Is.EqualTo(1f).Within(Tolerance));
            Assert.That(AimMath.FposMod(5f, 0f), Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void AngleDifference_TakesTheShortWayAroundTheWrap()
        {
            Assert.That(AimMath.AngleDifference(3f, -3f), Is.EqualTo(0.2831853f).Within(Tolerance));
            Assert.That(AimMath.AngleDifference(0f, 1.5707963f), Is.EqualTo(1.5707963f).Within(Tolerance));
            Assert.That(AimMath.AngleDifference(0.7f, 0.7f), Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void RotateTowards_LandsExactlyWhenTheStepCoversTheDifference()
        {
            Assert.That(AimMath.RotateTowards(0f, 0.05f, 0.1f), Is.EqualTo(0.05f).Within(Tolerance));
        }

        [Test]
        public void RotateTowards_StepsByTheBudgetInBothDirections()
        {
            Assert.That(AimMath.RotateTowards(0f, 1f, 0.25f), Is.EqualTo(0.25f).Within(Tolerance));
            Assert.That(AimMath.RotateTowards(0f, -1f, 0.25f), Is.EqualTo(-0.25f).Within(Tolerance));
        }

        [Test]
        public void LeadGravityY_MatchesTheSourceDrop()
        {
            // 100 m at 500 m/s is a 0.2 s flight, so the shell falls 0.5 * 10 * 0.04.
            Assert.That(
                AimMath.LeadGravityY(100f, 500f, AimMath.DefaultGravity),
                Is.EqualTo(0.2f).Within(Tolerance));
        }

        [Test]
        public void LeadGravityY_ClampsAtTheShellRange()
        {
            // Past max_dist the lead stops growing: 720 / 900 = 0.8 s of drop.
            Assert.That(
                AimMath.LeadGravityY(900f, 900f, AimMath.DefaultGravity),
                Is.EqualTo(3.2f).Within(Tolerance));
        }

        [Test]
        public void ShellGravity_ZeroesForHomingShells()
        {
            Assert.That(AimMath.ShellGravity(1.5f, false), Is.EqualTo(15f).Within(Tolerance));
            Assert.That(AimMath.ShellGravity(1.5f, true), Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void TurretAngularVelocity_ReportsDegreesPerSecond()
        {
            Assert.That(
                AimMath.TurretAngularVelocity(0f, 0.5f, 0.5f),
                Is.EqualTo(57.29578f).Within(1e-3f));
            Assert.That(AimMath.TurretAngularVelocity(0.4f, 0.4f, 0f), Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void GunTraceSteps_UsesFourSamplesForCheapAiTicks()
        {
            Assert.That(AimMath.GunTraceSteps(1f, AimMath.DefaultGravity, true, true), Is.EqualTo(4));
        }

        [Test]
        public void GunTraceSteps_PaysForTheFullSweepOnDesktop()
        {
            Assert.That(AimMath.GunTraceSteps(0.1f, AimMath.DefaultGravity, false, false), Is.EqualTo(48));
        }

        [Test]
        public void GunTraceSteps_BuysMobileSamplesByFlightTime()
        {
            // sqrt(10 / (8 * 0.01)) = 11.18034 samples per second of flight.
            Assert.That(AimMath.GunTraceSteps(1f, AimMath.DefaultGravity, false, true), Is.EqualTo(12));
            Assert.That(AimMath.GunTraceSteps(0.001f, AimMath.DefaultGravity, false, true), Is.EqualTo(1));
            Assert.That(AimMath.GunTraceSteps(10f, AimMath.DefaultGravity, false, true), Is.EqualTo(48));
        }
    }
}
