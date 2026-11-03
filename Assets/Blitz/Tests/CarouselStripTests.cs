using System.Collections.Generic;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The hangar carousel builds only the cards over its view. The row
    /// keeps the classic geometry: 196 px cards 4 px apart, a 1000 px view.
    /// </summary>
    public sealed class CarouselStripTests
    {
        private const float CardWidth = 196f;
        private const float Spacing = 4f;
        private const float Pitch = CardWidth + Spacing;
        private const float ViewWidth = 1000f;

        private GameObject _root;
        private RectTransform _content;
        private CarouselStrip _strip;
        private Dictionary<RectTransform, int> _bound;
        private int _created;
        private int _binds;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("ScrollContainer", typeof(RectTransform));
            var viewport = (RectTransform)_root.transform;
            viewport.sizeDelta = new Vector2(ViewWidth, 130f);

            _content = new GameObject("tank_carousel", typeof(RectTransform)).GetComponent<RectTransform>();
            _content.SetParent(viewport, false);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(0f, 1f);
            _content.pivot = new Vector2(0f, 1f);
            _content.sizeDelta = new Vector2(0f, 130f);

            var scroll = _root.AddComponent<ScrollRect>();
            scroll.content = _content;
            scroll.viewport = viewport;
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            _bound = new Dictionary<RectTransform, int>();
            _created = 0;
            _binds = 0;
            _strip = CarouselStrip.Attach(scroll, CardWidth, Spacing, Create, Bind);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
        }

        [Test]
        public void RowWidth_IsTheCardsAndTheGapsBetweenThem()
        {
            Assert.AreEqual(0f, CarouselStrip.RowWidth(0, CardWidth, Spacing));
            Assert.AreEqual(196f, CarouselStrip.RowWidth(1, CardWidth, Spacing));
            Assert.AreEqual(596f, CarouselStrip.RowWidth(3, CardWidth, Spacing));
        }

        [Test]
        public void SetCount_BuildsOnlyTheCardsOverTheView()
        {
            _strip.SetCount(500);

            // 1000 px show cards 0..4, card 5 starts on the right edge and
            // one more is kept past it.
            Assert.AreEqual(CarouselStrip.RowWidth(500, CardWidth, Spacing), _content.sizeDelta.x, 0.001f);
            Assert.AreEqual(7, _strip.ShownCount);
            Assert.AreEqual(7, _strip.BuiltCount);
            Assert.AreEqual(7, _created);
            AssertShown(0, 6);
            Assert.IsNull(_strip.Shown(7));
        }

        [Test]
        public void Show_ScrollsTheLeastDistanceAndHandsOverTheCardsThatLeft()
        {
            _strip.SetCount(500);

            var card = _strip.Show(100);
            // Card 100 ends at 20196 px: the view's right edge moves there.
            Assert.AreEqual(-(100 * Pitch + CardWidth - ViewWidth), _content.anchoredPosition.x, 0.001f);
            Assert.AreSame(card, _strip.Shown(100));
            AssertShown(94, 101);
            Assert.IsNull(_strip.Shown(0));
            Assert.AreEqual(8, _created, "the cards that scrolled away were not reused");
            Assert.AreEqual(8, _strip.BuiltCount);

            // Card 99 is already whole on screen: nothing moves.
            var position = _content.anchoredPosition;
            _strip.Show(99);
            Assert.AreEqual(position, _content.anchoredPosition);

            // Back to the start: the view's left edge goes to card 0.
            _strip.Show(0);
            Assert.AreEqual(0f, _content.anchoredPosition.x, 0.001f);
            AssertShown(0, 6);
            Assert.AreEqual(8, _created);
        }

        [Test]
        public void SetCount_KeepsTheViewInsideAShorterRow()
        {
            _strip.SetCount(500);
            _strip.Show(400);

            _strip.SetCount(10);
            // 1996 px of row behind a 1000 px view: scrolled 996 px at most.
            Assert.AreEqual(-996f, _content.anchoredPosition.x, 0.001f);
            AssertShown(3, 9);
            Assert.AreEqual(7, _strip.ShownCount);

            _strip.SetCount(0);
            Assert.AreEqual(0, _strip.ShownCount);
            Assert.AreEqual(0f, _content.sizeDelta.x);
            Assert.AreEqual(0f, _content.anchoredPosition.x);
            foreach (var built in _bound.Keys)
            {
                Assert.IsFalse(built.gameObject.activeSelf, "a card of the empty row is still up");
            }

            // The kept cards come back before any new one is built.
            var created = _created;
            _strip.SetCount(3);
            AssertShown(0, 2);
            Assert.AreEqual(created, _created);
        }

        [Test]
        public void RebindAndSetCount_RedrawTheCardsOnShowWithoutBuildingAny()
        {
            _strip.SetCount(500);
            var created = _created;

            _binds = 0;
            _strip.Rebind();
            Assert.AreEqual(_strip.ShownCount, _binds);

            // The same count may stand for other tanks: every card is bound again.
            _binds = 0;
            _strip.SetCount(500);
            Assert.AreEqual(_strip.ShownCount, _binds);
            Assert.AreEqual(created, _created);
        }

        [Test]
        public void Show_RejectsAnIndexOutsideTheRow()
        {
            _strip.SetCount(5);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => _strip.Show(5));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => _strip.Show(-1));
        }

        private RectTransform Create()
        {
            var card = new GameObject("carousel_tank", typeof(RectTransform)).GetComponent<RectTransform>();
            card.SetParent(_content, false);
            card.anchorMin = new Vector2(0f, 1f);
            card.anchorMax = new Vector2(0f, 1f);
            card.pivot = new Vector2(0f, 1f);
            card.sizeDelta = new Vector2(CardWidth, 130f);
            _created++;
            return card;
        }

        private void Bind(RectTransform card, int index)
        {
            _bound[card] = index;
            _binds++;
        }

        /// <summary>Cards <paramref name="first"/>..<paramref name="last"/> are up, bound to their index and in place.</summary>
        private void AssertShown(int first, int last)
        {
            Assert.AreEqual(last - first + 1, _strip.ShownCount);
            for (var index = first; index <= last; index++)
            {
                var card = _strip.Shown(index);
                Assert.IsNotNull(card, "card " + index + " is not on show");
                Assert.IsTrue(card.gameObject.activeSelf, "card " + index + " is switched off");
                Assert.AreEqual(index, _bound[card], "card " + index + " draws another index");
                Assert.AreEqual(index * Pitch, card.anchoredPosition.x, 0.001f);
            }
        }
    }
}
