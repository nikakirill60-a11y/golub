using BlitzLegacy.Core.Ui;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class HudTuningTests
    {
        private const float Tolerance = 1e-4f;

        [Test]
        public void CrosshairSceneGeometryMatchesUiPlayer()
        {
            Assert.AreEqual(35, HudTuning.DotCount);
            Assert.AreEqual(0.555f, HudTuning.CrosshairHandleScale, Tolerance);
            Assert.AreEqual(-23.225037f, HudTuning.CrosshairHandleLeft, Tolerance);
            Assert.AreEqual(-33.225006f, HudTuning.CrosshairHandleTop, Tolerance);
            Assert.AreEqual(52.450745f, HudTuning.CrosshairHandleRight, Tolerance);
            Assert.AreEqual(78.486725f, HudTuning.CrosshairHandleBottom, Tolerance);
            Assert.AreEqual(0.5f, HudTuning.CrosshairDynScale, Tolerance);
            Assert.AreEqual(18.135f, HudTuning.CrosshairDynOffsetY, Tolerance);
        }

        [Test]
        public void FirstReloadDotStartsAtTheTopOfTheRing()
        {
            float x;
            float y;
            HudTuning.DotPosition(0, 32f, out x, out y);

            Assert.AreEqual(0f, x, Tolerance);
            Assert.AreEqual(-32f, y, Tolerance);
            Assert.AreEqual(0f, HudTuning.DotFillAngle(0), Tolerance);
        }

        [Test]
        public void ReloadRingUsesRedBeforeAndYellowAfterTheCoefficient()
        {
            Assert.AreEqual(CrosshairDot.Red, HudTuning.DotState(0, 0.5f, false));
            Assert.AreEqual(CrosshairDot.Yellow, HudTuning.DotState(18, 0.5f, false));
            Assert.AreEqual(CrosshairDot.Green, HudTuning.DotState(0, 0.5f, true));
        }

        [Test]
        public void DispersionUsesBothProjectedSides()
        {
            Assert.AreEqual(35f, HudTuning.DispersionRadius(40f, 60f), Tolerance);
            Assert.AreEqual(35f, HudTuning.ClampDispersion(35f), Tolerance);
        }

        [Test]
        public void HealthBarMovesFromGreenToRed()
        {
            float red;
            float green;
            float blue;
            HudTuning.HealthColour(750f, 1000f, out red, out green, out blue);

            Assert.AreEqual(0.25f, red, Tolerance);
            Assert.AreEqual(0.75f, green, Tolerance);
            Assert.AreEqual(0f, blue, Tolerance);
            Assert.AreEqual("750 / 1000", HudTuning.HealthText(750, 1000, "{0} / {1}"));
        }

        [Test]
        public void HealthBarOnlyCatchesUpUpwardsWithoutDamageAnimation()
        {
            Assert.AreEqual(800f, HudTuning.HealthBarValue(800f, 750f), Tolerance);
            Assert.AreEqual(900f, HudTuning.HealthBarValue(800f, 900f), Tolerance);
        }

        [Test]
        public void SpeedKeepsGodotFloatFormattingAndLocalizedUnits()
        {
            Assert.AreEqual("0.0 км/ч", HudTuning.SpeedText(0.8f, "{0} км/ч"));
            Assert.AreEqual("19.0 km/h", HudTuning.SpeedText(19.4f, "{0} km/h"));
            Assert.AreEqual("-20.0 km/h", HudTuning.SpeedText(-19.6f, "{0} km/h"));
        }

        [Test]
        public void DistanceRoundsUpAndUsesLocalizedMissText()
        {
            Assert.AreEqual(
                "34 м", HudTuning.DistanceText(33.01f, true, "{0} м", "... м"));
            Assert.AreEqual(
                "... м", HudTuning.DistanceText(999f, false, "{0} м", "... м"));
        }

        [Test]
        public void ReloadRoundsToTwoDecimalsAndLoadedMeansTotal()
        {
            Assert.AreEqual("3.46", HudTuning.ReloadText(3.456f));
            Assert.IsTrue(HudTuning.ReloadComplete(4.5f, 4.5f));
            Assert.IsFalse(HudTuning.ReloadComplete(4.49f, 4.5f));
        }

        [Test]
        public void ClockUsesWholeMinutesAndZeroPaddedSeconds()
        {
            Assert.AreEqual("2", HudTuning.ClockMinutesText(125.9f));
            Assert.AreEqual("05", HudTuning.ClockSecondsText(125.9f));
            Assert.AreEqual("00", HudTuning.ClockSecondsText(0f));
        }

        [Test]
        public void HudCadenceKeepsTheSourceIntervals()
        {
            Assert.AreEqual(0.1f, HudTuning.HudUpdateInterval, Tolerance);
            Assert.AreEqual(0.1f, HudTuning.ClockUpdateInterval, Tolerance);
            Assert.AreEqual(0.25f, HudTuning.FpsUpdateInterval, Tolerance);
            Assert.AreEqual(1f, HudTuning.FpsSampleWindow, Tolerance);
            Assert.AreEqual(0.5f, HudTuning.MinimapUpdateInterval, Tolerance);
            Assert.AreEqual(5f, HudTuning.TeamBoxUpdateInterval, Tolerance);
        }
    }
}
