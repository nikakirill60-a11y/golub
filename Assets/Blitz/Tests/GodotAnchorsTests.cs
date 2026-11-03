using BlitzLegacy.Core.Ui;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class GodotAnchorsTests
    {
        [Test]
        public void FullRect_StaysFullRect()
        {
            var r = GodotAnchors.FullRect();
            Assert.AreEqual(0f, r.AnchorMinX);
            Assert.AreEqual(0f, r.AnchorMinY);
            Assert.AreEqual(1f, r.AnchorMaxX);
            Assert.AreEqual(1f, r.AnchorMaxY);
        }

        [Test]
        public void TopWidePreset_AnchorsToTheTopEdge()
        {
            // anchors_preset 10, used by the login background container.
            var r = GodotAnchors.Convert(0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f);
            Assert.AreEqual(1f, r.AnchorMinY);
            Assert.AreEqual(1f, r.AnchorMaxY);
        }

        [Test]
        public void BottomLeftPreset_MatchesVersionLabel()
        {
            // t_version: anchor top/bottom 1.0, offsets 7 / -25 / 64 / -2.
            var r = GodotAnchors.Convert(0f, 1f, 0f, 1f, 7f, -25f, 64f, -2f);
            Assert.AreEqual(0f, r.AnchorMinY);
            Assert.AreEqual(0f, r.AnchorMaxY);
            Assert.AreEqual(7f, r.OffsetMinX);
            Assert.AreEqual(64f, r.OffsetMaxX);
            // 23 px tall, sitting 2 px above the bottom edge.
            Assert.AreEqual(2f, r.OffsetMinY);
            Assert.AreEqual(25f, r.OffsetMaxY);
        }

        [Test]
        public void CenteredPreset_MatchesPlayButton()
        {
            // button_login2 on the play tab: offsets -108 / 130 / 108 / 190.
            var r = GodotAnchors.Centered(-108f, 130f, 108f, 190f);
            Assert.AreEqual(0.5f, r.AnchorMinY);
            Assert.AreEqual(0.5f, r.AnchorMaxY);
            // Godot puts it below the centre, so both Unity offsets are negative.
            Assert.AreEqual(-190f, r.OffsetMinY);
            Assert.AreEqual(-130f, r.OffsetMaxY);
            Assert.AreEqual(60f, r.OffsetMaxY - r.OffsetMinY);
        }

        [Test]
        public void AnchorsBeyondOne_SurviveTheConversion()
        {
            // backEffect8 deliberately overflows the viewport.
            var r = GodotAnchors.Convert(
                0f, 0f, 1.709f, 0f, -810.2566f, -5.1282053f, -1903.6166f, 1532.8718f);
            Assert.AreEqual(1.709f, r.AnchorMaxX, 1e-6f);
            Assert.AreEqual(-1532.8718f, r.OffsetMinY, 1e-4f);
            Assert.AreEqual(5.1282053f, r.OffsetMaxY, 1e-4f);
        }
    }

    public sealed class AtlasRectTests
    {
        [Test]
        public void FlipY_TopRegionEndsUpAtTheTop()
        {
            // back_login1: Rect2(0, 0, 2048, 1191) on a 2048 tall atlas.
            Assert.AreEqual(857f, AtlasRect.FlipY(2048f, 0f, 1191f));
        }

        [Test]
        public void FlipY_LowerRegionEndsUpNearTheBottom()
        {
            // back_login2: Rect2(0, 1192, 2048, 743).
            Assert.AreEqual(113f, AtlasRect.FlipY(2048f, 1192f, 743f));
        }

        [Test]
        public void FlipY_FullHeightRegionStartsAtZero()
        {
            Assert.AreEqual(0f, AtlasRect.FlipY(512f, 0f, 512f));
        }
    }
}
