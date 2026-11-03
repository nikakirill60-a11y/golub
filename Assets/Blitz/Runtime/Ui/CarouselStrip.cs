using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// A horizontal carousel that only builds the cards over its viewport.
    ///
    /// The content keeps the width of the whole row with card i at
    /// i * <see cref="Pitch"/>, so dragging, inertia and clamping behave as
    /// if every card existed. Only the cards on screen and
    /// <see cref="Margin"/> past either edge are live, though: a card that
    /// scrolls away is handed to the index scrolling in and bound to it. A
    /// garage with the whole roster owned (500+ vehicles) then filters, picks
    /// and redraws in the time of a dozen cards, and the icon of a vehicle
    /// nobody scrolls to is never loaded.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CarouselStrip : MonoBehaviour
    {
        /// <summary>Cards kept live past each edge of the viewport.</summary>
        public const int Margin = 1;

        private readonly Dictionary<int, RectTransform> _shown = new Dictionary<int, RectTransform>();
        private readonly List<RectTransform> _spare = new List<RectTransform>();
        private readonly List<RectTransform> _free = new List<RectTransform>();
        private readonly List<int> _leaving = new List<int>();

        private ScrollRect _scroll;
        private RectTransform _viewport;
        private RectTransform _content;
        private Func<RectTransform> _create;
        private Action<RectTransform, int> _bind;

        public float CardWidth { get; private set; }

        /// <summary>The gap between two cards.</summary>
        public float Spacing { get; private set; }

        /// <summary>From one card's left edge to the next one's.</summary>
        public float Pitch
        {
            get { return CardWidth + Spacing; }
        }

        /// <summary>Cards in the row, built or not.</summary>
        public int Count { get; private set; }

        /// <summary>Card objects that exist: on show or waiting to be reused.</summary>
        public int BuiltCount
        {
            get { return _shown.Count + _spare.Count; }
        }

        /// <summary>Cards bound to an index right now.</summary>
        public int ShownCount
        {
            get { return _shown.Count; }
        }

        /// <summary>
        /// Drives <paramref name="scroll"/>'s content as a row of
        /// <paramref name="cardWidth"/> cards <paramref name="spacing"/>
        /// apart. <paramref name="create"/> builds one card under the content
        /// and <paramref name="bind"/> draws a card for an index; the strip
        /// places the card and never looks inside it.
        /// </summary>
        public static CarouselStrip Attach(
            ScrollRect scroll, float cardWidth, float spacing,
            Func<RectTransform> create, Action<RectTransform, int> bind)
        {
            if (scroll == null)
            {
                throw new ArgumentNullException("scroll");
            }

            if (scroll.content == null)
            {
                throw new ArgumentException("The ScrollRect has no content.", "scroll");
            }

            if (create == null)
            {
                throw new ArgumentNullException("create");
            }

            if (bind == null)
            {
                throw new ArgumentNullException("bind");
            }

            if (cardWidth <= 0f)
            {
                throw new ArgumentOutOfRangeException("cardWidth");
            }

            var strip = scroll.gameObject.AddComponent<CarouselStrip>();
            strip._scroll = scroll;
            strip._viewport = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
            strip._content = scroll.content;
            strip._create = create;
            strip._bind = bind;
            strip.CardWidth = cardWidth;
            strip.Spacing = Mathf.Max(0f, spacing);
            scroll.onValueChanged.AddListener(strip.OnScrolled);
            return strip;
        }

        /// <summary>
        /// The width a box container gives <paramref name="count"/> cards:
        /// every card and the gaps between them, none outside.
        /// </summary>
        public static float RowWidth(int count, float cardWidth, float spacing)
        {
            return count <= 0 ? 0f : (count * cardWidth) + ((count - 1) * spacing);
        }

        /// <summary>
        /// The row now holds <paramref name="count"/> cards: the content is
        /// resized, kept inside the viewport, and every card on show is bound
        /// again, as whatever stands behind the indices may have changed.
        /// </summary>
        public void SetCount(int count)
        {
            Count = Mathf.Max(0, count);
            var size = _content.sizeDelta;
            _content.sizeDelta = new Vector2(RowWidth(Count, CardWidth, Spacing), size.y);
            ScrollTo(-_content.anchoredPosition.x);
            Layout(true);
        }

        /// <summary>Binds every card on show again, for a change that keeps the row, e.g. a new selection.</summary>
        public void Rebind()
        {
            foreach (var pair in _shown)
            {
                _bind(pair.Value, pair.Key);
            }
        }

        /// <summary>
        /// Scrolls the least distance that puts card <paramref name="index"/>
        /// wholly on screen, as far as the row allows, and returns the card.
        /// </summary>
        public RectTransform Show(int index)
        {
            if (index < 0 || index >= Count)
            {
                throw new ArgumentOutOfRangeException("index");
            }

            _scroll.StopMovement();
            var view = _viewport.rect.width;
            var left = -_content.anchoredPosition.x;
            var cardLeft = index * Pitch;
            var cardRight = cardLeft + CardWidth;
            if (cardLeft < left)
            {
                left = cardLeft;
            }
            else if (cardRight > left + view)
            {
                left = cardRight - view;
            }

            ScrollTo(left);
            Layout(false);
            return _shown[index];
        }

        /// <summary>The card bound to <paramref name="index"/>, or null while it is scrolled away.</summary>
        public RectTransform Shown(int index)
        {
            RectTransform card;
            return _shown.TryGetValue(index, out card) ? card : null;
        }

        private void OnScrolled(Vector2 position)
        {
            Layout(false);
        }

        /// <summary>A resized screen widens or narrows the view.</summary>
        private void OnRectTransformDimensionsChange()
        {
            if (_content != null)
            {
                Layout(false);
            }
        }

        private void OnDestroy()
        {
            if (_scroll != null)
            {
                _scroll.onValueChanged.RemoveListener(OnScrolled);
            }
        }

        /// <summary>Puts the view's left edge <paramref name="left"/> px into the row, clamped to it.</summary>
        private void ScrollTo(float left)
        {
            var max = Mathf.Max(0f, _content.rect.width - _viewport.rect.width);
            left = Mathf.Clamp(left, 0f, max);
            var position = _content.anchoredPosition;
            if (!Mathf.Approximately(position.x, -left))
            {
                _content.anchoredPosition = new Vector2(-left, position.y);
            }
        }

        private void Layout(bool rebindAll)
        {
            int first;
            int last;
            Range(out first, out last);

            // A card that left the view goes straight to one coming in and
            // stays active, so scrolling never switches cards off and on.
            _leaving.Clear();
            foreach (var pair in _shown)
            {
                if (pair.Key < first || pair.Key > last)
                {
                    _leaving.Add(pair.Key);
                }
            }

            _free.Clear();
            for (var i = 0; i < _leaving.Count; i++)
            {
                _free.Add(_shown[_leaving[i]]);
                _shown.Remove(_leaving[i]);
            }

            for (var index = first; index <= last; index++)
            {
                RectTransform card;
                if (_shown.TryGetValue(index, out card))
                {
                    if (rebindAll)
                    {
                        _bind(card, index);
                    }

                    continue;
                }

                card = Take();
                card.anchoredPosition = new Vector2(index * Pitch, card.anchoredPosition.y);
                _shown.Add(index, card);
                _bind(card, index);
            }

            for (var i = 0; i < _free.Count; i++)
            {
                _free[i].gameObject.SetActive(false);
                _spare.Add(_free[i]);
            }

            _free.Clear();
        }

        private RectTransform Take()
        {
            RectTransform card;
            if (_free.Count > 0)
            {
                card = _free[_free.Count - 1];
                _free.RemoveAt(_free.Count - 1);
                return card;
            }

            if (_spare.Count > 0)
            {
                card = _spare[_spare.Count - 1];
                _spare.RemoveAt(_spare.Count - 1);
                card.gameObject.SetActive(true);
                return card;
            }

            card = _create();
            if (card.parent != _content)
            {
                card.SetParent(_content, false);
            }

            return card;
        }

        /// <summary>The indices over the view, <see cref="Margin"/> more on either side.</summary>
        private void Range(out int first, out int last)
        {
            if (Count == 0)
            {
                first = 0;
                last = -1;
                return;
            }

            var left = Mathf.Max(0f, -_content.anchoredPosition.x);
            var view = Mathf.Max(0f, _viewport.rect.width);
            first = Mathf.Clamp(Mathf.FloorToInt(left / Pitch) - Margin, 0, Count - 1);
            last = Mathf.Clamp(Mathf.FloorToInt((left + view) / Pitch) + Margin, first, Count - 1);
        }
    }
}
