using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// A one-axis ScrollRect that sits inside a scroll view of the other axis
    /// (the horizontal card and module strips on the vertical sandbox pages).
    ///
    /// A plain ScrollRect takes every drag that starts on it, so a vertical
    /// swipe over a strip did nothing. This one keeps a drag that runs along
    /// its axis and hands one that runs across it to the enclosing view
    /// (<see cref="UiDragHandoff"/>). A wheel turn it cannot use (the strip is
    /// at its end or does not overflow) scrolls the enclosing view instead.
    /// Without an enclosing view it is a plain ScrollRect.
    /// </summary>
    [ExecuteAlways]
    public sealed class NestedScrollRect : ScrollRect
    {
        public override void OnInitializePotentialDrag(PointerEventData eventData)
        {
            base.OnInitializePotentialDrag(eventData);
            var outer = Outer();
            if (outer != null)
            {
                outer.OnInitializePotentialDrag(eventData);
            }
        }

        public override void OnBeginDrag(PointerEventData eventData)
        {
            var outer = Outer();
            if (outer != null && UiDragHandoff.RunsAcross(eventData, horizontal))
            {
                UiDragHandoff.HandTo(outer, eventData);
                return;
            }

            base.OnBeginDrag(eventData);
        }

        public override void OnScroll(PointerEventData data)
        {
            var before = content == null ? Vector2.zero : content.anchoredPosition;
            base.OnScroll(data);
            var outer = Outer();
            if (outer != null && (content == null || content.anchoredPosition == before))
            {
                outer.OnScroll(data);
            }
        }

        /// <summary>The enclosing view of the other axis, for a one-axis strip.</summary>
        private ScrollRect Outer()
        {
            return horizontal != vertical ? UiDragHandoff.Enclosing(transform, horizontal) : null;
        }
    }
}
