using BlitzLegacy.Core.Ui;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>Where the dragged hangar music panel may stand on the screen.</summary>
    public sealed class HangarMusicPlacementTests
    {
        private const float Margin = HangarMusicPlacement.Margin;
        private const float CanvasWidth = 1600f;
        private const float CanvasHeight = 733f;
        private const float Width = 340f;

        [Test]
        public void APlaceInsideTheScreenStaysAsItIs()
        {
            var right = 120f;
            var top = 200f;
            Clamp(0f, 0f, 0f, 0f, 160f, ref right, ref top);
            Assert.AreEqual(120f, right);
            Assert.AreEqual(200f, top);
        }

        [Test]
        public void ThePanelKeepsTheMarginToEveryEdge()
        {
            var right = -50f;
            var top = -50f;
            Clamp(0f, 0f, 0f, 0f, 160f, ref right, ref top);
            Assert.AreEqual(Margin, right);
            Assert.AreEqual(Margin, top);

            right = 5000f;
            top = 5000f;
            Clamp(0f, 0f, 0f, 0f, 160f, ref right, ref top);
            Assert.AreEqual(CanvasWidth - Margin - Width, right);
            Assert.AreEqual(CanvasHeight - Margin - 160f, top);
        }

        [Test]
        public void TheSafeAreaKeepsThePanelOutOfTheCutouts()
        {
            var right = 0f;
            var top = 0f;
            Clamp(40f, 10f, 60f, 20f, 160f, ref right, ref top);
            Assert.AreEqual(60f + Margin, right);
            Assert.AreEqual(10f + Margin, top);

            right = 5000f;
            top = 5000f;
            Clamp(40f, 10f, 60f, 20f, 160f, ref right, ref top);
            Assert.AreEqual(CanvasWidth - 40f - Margin - Width, right);
            Assert.AreEqual(CanvasHeight - 20f - Margin - 160f, top);
        }

        [Test]
        public void AShortPanelKeepsItsTopAndATallOneRisesOnlyToFit()
        {
            var low = CanvasHeight - Margin - 36f;
            var right = 16f;
            var top = low;
            Clamp(0f, 0f, 0f, 0f, 36f, ref right, ref top);
            Assert.AreEqual(low, top, "the folded header at the bottom");

            top = low;
            Clamp(0f, 0f, 0f, 0f, 328f, ref right, ref top);
            Assert.AreEqual(CanvasHeight - Margin - 328f, top, "the tall view rises to fit");

            top = 300f;
            Clamp(0f, 0f, 0f, 0f, 328f, ref right, ref top);
            Assert.AreEqual(300f, top, "room enough under it");
        }

        [Test]
        public void APanelLargerThanTheRoomKeepsItsTopRightCornerIn()
        {
            var right = 100f;
            var top = 100f;
            HangarMusicPlacement.Clamp(300f, 200f, 0f, 0f, 0f, 0f, Width, 328f, ref right, ref top);
            Assert.AreEqual(Margin, right);
            Assert.AreEqual(Margin, top);
        }

        [Test]
        public void NotANumberStandsInTheTopRightCorner()
        {
            var right = float.NaN;
            var top = float.NaN;
            Clamp(0f, 30f, 50f, 0f, 160f, ref right, ref top);
            Assert.AreEqual(50f + Margin, right);
            Assert.AreEqual(30f + Margin, top);
        }

        [Test]
        public void TheSafeAreaIsMeasuredInCanvasUnits()
        {
            float left;
            float top;
            float right;
            float bottom;

            // A 2400x1080 phone with its camera hole on the left, on a canvas
            // two thirds of the screen's size.
            HangarMusicPlacement.Insets(
                2400f, 1080f, 90f, 0f, 2310f, 1080f, 1600f, 720f, out left, out top, out right, out bottom);
            Assert.AreEqual(60f, left, 1e-3f);
            Assert.AreEqual(0f, top);
            Assert.AreEqual(0f, right);
            Assert.AreEqual(0f, bottom);

            // The hole on the right and rounded corners at the bottom; Unity's y is up.
            HangarMusicPlacement.Insets(
                2400f, 1080f, 0f, 30f, 2300f, 1050f, 2400f, 1080f, out left, out top, out right, out bottom);
            Assert.AreEqual(0f, left);
            Assert.AreEqual(0f, top);
            Assert.AreEqual(100f, right, 1e-3f);
            Assert.AreEqual(30f, bottom, 1e-3f);

            HangarMusicPlacement.Insets(
                0f, 0f, 10f, 10f, 0f, 0f, 1600f, 720f, out left, out top, out right, out bottom);
            Assert.AreEqual(0f, left + top + right + bottom, "no screen, no insets");
        }

        private static void Clamp(
            float insetLeft, float insetTop, float insetRight, float insetBottom, float height,
            ref float right, ref float top)
        {
            HangarMusicPlacement.Clamp(
                CanvasWidth, CanvasHeight, insetLeft, insetTop, insetRight, insetBottom, Width, height,
                ref right, ref top);
        }
    }
}
