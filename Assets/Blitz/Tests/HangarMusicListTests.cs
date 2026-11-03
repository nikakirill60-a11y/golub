using BlitzLegacy.Core.Ui;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>The scrolling of the hangar music playlist and the ring of rows it hands on.</summary>
    public sealed class HangarMusicListTests
    {
        private const float Row = 40f;
        private const float View = 200f;
        private const int Tracks = 95;

        [Test]
        public void TheRingHoldsEveryRowTheViewportShowsAPartOf()
        {
            Assert.AreEqual(6, HangarMusicList.RingSize(Row, View));
            Assert.AreEqual(7, HangarMusicList.RingSize(Row, 210f));
            var ring = HangarMusicList.RingSize(Row, View);
            for (var offset = 0f; offset <= 400f; offset += 0.5f)
            {
                int first;
                int last;
                HangarMusicList.Window(Tracks, Row, View, offset, out first, out last);
                Assert.LessOrEqual(last - first + 1, ring, "at " + offset);
            }
        }

        [Test]
        public void TheWindowCoversTheRowsInSightHeldBetweenTheEnds()
        {
            int first;
            int last;
            HangarMusicList.Window(Tracks, Row, View, 0f, out first, out last);
            Assert.AreEqual(new[] { 0, 4 }, new[] { first, last });
            HangarMusicList.Window(Tracks, Row, View, 20f, out first, out last);
            Assert.AreEqual(new[] { 0, 5 }, new[] { first, last }, "a row cut at either end");
            HangarMusicList.Window(Tracks, Row, View, 40f, out first, out last);
            Assert.AreEqual(new[] { 1, 5 }, new[] { first, last });
            HangarMusicList.Window(Tracks, Row, View, 1e6f, out first, out last);
            Assert.AreEqual(new[] { 90, 94 }, new[] { first, last }, "past the end");
            HangarMusicList.Window(Tracks, Row, View, -50f, out first, out last);
            Assert.AreEqual(new[] { 0, 4 }, new[] { first, last }, "over the top");
            Assert.AreEqual(Tracks * Row - View, HangarMusicList.MaxOffset(Tracks, Row, View));
        }

        [Test]
        public void ShortAndEmptyListsDoNotScroll()
        {
            int first;
            int last;
            HangarMusicList.Window(3, Row, View, 100f, out first, out last);
            Assert.AreEqual(new[] { 0, 2 }, new[] { first, last });
            Assert.AreEqual(0f, HangarMusicList.MaxOffset(3, Row, View));
            Assert.AreEqual(0f, HangarMusicList.Clamp(100f, 3, Row, View));
            HangarMusicList.Window(0, Row, View, 0f, out first, out last);
            Assert.Less(last, first, "no rows");
            Assert.AreEqual(0f, HangarMusicList.Clamp(float.NaN, Tracks, Row, View));
        }

        [Test]
        public void ARowIsCentredAsFarAsTheEndsAllow()
        {
            Assert.AreEqual(320f, HangarMusicList.Centered(10, Tracks, Row, View));
            Assert.AreEqual(0f, HangarMusicList.Centered(1, Tracks, Row, View), "near the top");
            Assert.AreEqual(Tracks * Row - View, HangarMusicList.Centered(Tracks - 1, Tracks, Row, View), "the last row");
            Assert.AreEqual(0f, HangarMusicList.Centered(2, 3, Row, View), "a list shorter than its view");
        }

        [Test]
        public void OnlyAWholeRowCountsAsShown()
        {
            Assert.IsTrue(HangarMusicList.Shows(0, Row, View, 0f));
            Assert.IsTrue(HangarMusicList.Shows(4, Row, View, 0f));
            Assert.IsFalse(HangarMusicList.Shows(5, Row, View, 0f), "under the viewport");
            Assert.IsFalse(HangarMusicList.Shows(0, Row, View, 20f), "cut at the top");
            Assert.IsTrue(HangarMusicList.Shows(5, Row, View, 40f));
        }
    }
}
