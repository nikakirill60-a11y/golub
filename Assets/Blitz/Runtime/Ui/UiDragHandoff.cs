using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Hands a drag that started on a nested control to the scroll view
    /// around it.
    ///
    /// uGUI gives a drag to the innermost <see cref="IDragHandler"/> under
    /// the press, so a horizontal card strip or a slider on a vertical page
    /// swallowed every vertical swipe that started on it. Android's nested
    /// scrolling decides by the direction of the first movement instead: the
    /// control keeps a drag that runs along its own axis and the page gets
    /// one that runs across it. <see cref="NestedScrollRect"/> and
    /// <see cref="UiValueSlider"/> make that decision when the drag begins.
    /// </summary>
    public static class UiDragHandoff
    {
        /// <summary>
        /// The nearest active scroll view above <paramref name="from"/> that
        /// scrolls along the given axis, or null.
        /// </summary>
        public static ScrollRect Enclosing(Transform from, bool vertical)
        {
            for (var node = from == null ? null : from.parent; node != null; node = node.parent)
            {
                var view = node.GetComponent<ScrollRect>();
                if (view != null && view.content != null && view.IsActive()
                    && (vertical ? view.vertical : view.horizontal))
                {
                    return view;
                }
            }

            return null;
        }

        /// <summary>
        /// True when the pointer has travelled further across the owner's
        /// axis than along it since the press; a diagonal stays with the owner.
        /// </summary>
        public static bool RunsAcross(PointerEventData eventData, bool horizontalOwner)
        {
            if (eventData == null)
            {
                return false;
            }

            var travel = eventData.position - eventData.pressPosition;
            var along = Mathf.Abs(horizontalOwner ? travel.x : travel.y);
            var across = Mathf.Abs(horizontalOwner ? travel.y : travel.x);
            return across > along;
        }

        /// <summary>
        /// Makes <paramref name="target"/> the receiver of the rest of the
        /// drag. The input module sends OnDrag and OnEndDrag to
        /// <see cref="PointerEventData.pointerDrag"/>, so that is switched
        /// before the target starts: a target that hands the drag on again
        /// keeps the last word.
        /// </summary>
        public static void HandTo(ScrollRect target, PointerEventData eventData)
        {
            if (target == null || eventData == null)
            {
                return;
            }

            eventData.pointerDrag = target.gameObject;
            target.OnInitializePotentialDrag(eventData);
            target.OnBeginDrag(eventData);
        }

        /// <summary>
        /// A press on a nested control stops the fling of the view around it,
        /// as a press on the view itself does.
        /// </summary>
        public static void StopEnclosing(Transform from, bool vertical, PointerEventData eventData)
        {
            var view = Enclosing(from, vertical);
            if (view != null && eventData != null)
            {
                view.OnInitializePotentialDrag(eventData);
            }
        }
    }
}
