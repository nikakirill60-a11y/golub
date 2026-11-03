using BlitzLegacy.Core.Tanks;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the battle camera tuning against Godot <c>input_camera.gd</c> and
    /// the camera defaults in <c>save_settings.gd</c>.
    /// </summary>
    public sealed class CameraTuningTests
    {
        private const float Tolerance = 1e-4f;

        [Test]
        public void ArcadeFraming_ReadsTheClientDefaults()
        {
            // 4.8.0 options*.yaml: "Camera fixed positions / 1) Arcade" 19 on a
            // 0..40 slider, "Camera / 10) Default fov" 54, and the precise
            // camera pitch limits -30..50. 11.20.0 ships the same numbers.
            Assert.That(CameraTuning.LengthStart, Is.EqualTo(19f).Within(Tolerance));
            Assert.That(CameraTuning.LengthMax, Is.EqualTo(40f).Within(Tolerance));
            Assert.That(CameraTuning.Fov, Is.EqualTo(54f).Within(Tolerance));
            Assert.That(CameraTuning.AngleMin, Is.EqualTo(-30f).Within(Tolerance));
            Assert.That(CameraTuning.AngleMax, Is.EqualTo(50f).Within(Tolerance));

            // The arm the player actually starts on has to stay inside the
            // range, or the client distance would be clamped away.
            Assert.That(
                CameraTuning.ClampLength(
                    CameraTuning.LengthStart, CameraTuning.LengthMin, CameraTuning.LengthMax),
                Is.EqualTo(CameraTuning.LengthStart).Within(Tolerance));
        }

        [Test]
        public void HeightCoefficient_ClampsIntoTheSourceBand()
        {
            // A short arm would fall under the band, a long one over it.
            Assert.That(
                CameraTuning.HeightCoefficient(5f, CameraTuning.LengthMax),
                Is.EqualTo(0.75f).Within(Tolerance));
            Assert.That(
                CameraTuning.HeightCoefficient(20f, CameraTuning.LengthMax),
                Is.EqualTo(1f).Within(Tolerance));
            Assert.That(
                CameraTuning.HeightCoefficient(16f, CameraTuning.LengthMax),
                Is.EqualTo(0.8f).Within(Tolerance));
        }

        [Test]
        public void HeightCoefficient_JumpsWhenTheArmIsFullyExtended()
        {
            Assert.That(
                CameraTuning.HeightCoefficient(CameraTuning.LengthMax, CameraTuning.LengthMax),
                Is.EqualTo(2.5f).Within(Tolerance));
        }

        [Test]
        public void HeightTarget_RidesOnTopOfTheHull()
        {
            // hull_height measured off the MS-1 hull donor.
            Assert.That(
                CameraTuning.HeightTarget(2.0922f, CameraTuning.Height, 1f),
                Is.EqualTo(4.8922f).Within(Tolerance));
        }

        [Test]
        public void ClampLength_PinsIntoTheSavedZoomRange()
        {
            Assert.That(
                CameraTuning.ClampLength(45f, CameraTuning.LengthMin, CameraTuning.LengthMax),
                Is.EqualTo(40f).Within(Tolerance));
            Assert.That(
                CameraTuning.ClampLength(0.5f, CameraTuning.LengthMin, CameraTuning.LengthMax),
                Is.EqualTo(1f).Within(Tolerance));
        }

        [Test]
        public void ZoomStepIndex_WalksAndClampsTheLadder()
        {
            // Negative wheel deltas zoom in, so they advance down the ladder.
            Assert.That(CameraTuning.ZoomStepIndex(0, -1f), Is.EqualTo(1));
            Assert.That(CameraTuning.ZoomStepIndex(0, 1f), Is.EqualTo(0));
            Assert.That(CameraTuning.ZoomStepIndex(4, -1f), Is.EqualTo(4));
        }

        [Test]
        public void SniperFov_ReadsTheSourceLadder()
        {
            Assert.That(CameraTuning.SniperFov(0), Is.EqualTo(40f).Within(Tolerance));
            Assert.That(CameraTuning.SniperFov(4), Is.EqualTo(2f).Within(Tolerance));
            Assert.That(CameraTuning.SniperFov(9), Is.EqualTo(2f).Within(Tolerance));
        }

        [Test]
        public void SniperSensitivity_SlowsDownAsTheViewNarrows()
        {
            Assert.That(
                CameraTuning.SniperSensitivity(CameraTuning.SniperFovMax),
                Is.EqualTo(0.3f).Within(Tolerance));
            Assert.That(
                CameraTuning.SniperSensitivity(CameraTuning.SniperFovMin),
                Is.EqualTo(0.015f).Within(Tolerance));
        }

        [Test]
        public void LeavesSniper_OnlyWhenZoomingOutPastTheWidestStep()
        {
            Assert.That(CameraTuning.LeavesSniper(40f, 1f), Is.True);
            Assert.That(CameraTuning.LeavesSniper(20f, 1f), Is.False);
            Assert.That(CameraTuning.LeavesSniper(40f, -1f), Is.False);
        }

        [Test]
        public void EntersSniper_WhenZoomingInPastTheShortestArm()
        {
            Assert.That(
                CameraTuning.EntersSniper(2f, CameraTuning.LengthStep, -1f, CameraTuning.LengthMin),
                Is.True);
            Assert.That(
                CameraTuning.EntersSniper(10f, CameraTuning.LengthStep, -1f, CameraTuning.LengthMin),
                Is.False);
        }

        [Test]
        public void IsEqualApprox_ScalesTheToleranceWithMagnitude()
        {
            Assert.That(CameraTuning.IsEqualApprox(30f, 30.0001f), Is.True);
            Assert.That(CameraTuning.IsEqualApprox(1f, 1.001f), Is.False);
        }
    }
}
