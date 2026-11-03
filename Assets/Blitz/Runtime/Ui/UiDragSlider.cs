using System;
using BlitzLegacy.Core.Ui;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The grabber area of a Godot HSlider: pressing or dragging anywhere on
    /// the rect moves the value to the pointer, snapped to the slider step.
    ///
    /// Unity's own Slider would need a prefab hierarchy and its own
    /// interactable graphics, while every hangar widget in this port is built
    /// from code against captured Godot rects, so the row keeps its own rects
    /// and only borrows the pointer handling.
    /// </summary>
    public sealed class UiDragSlider : MonoBehaviour,
        IPointerDownHandler, IDragHandler
    {
        private RectTransform _rect;
        private Action<int> _changed;

        /// <summary>Range.max_value, the rack capacity of the installed gun.</summary>
        public int MaxValue { get; set; }

        /// <summary>Range.step, the burst of the installed gun.</summary>
        public int Step { get; set; }

        /// <summary>
        /// The width of the grabber icon. Godot keeps the grabber inside the
        /// frame and maps the pointer over its centre travel, so the value a
        /// press picks lands on the grabber the row already draws.
        /// </summary>
        public float GrabberSize { get; set; }

        public UiDragSlider()
        {
            Step = 1;
        }

        public void Configure(int max, int step, Action<int> changed)
        {
            MaxValue = max;
            Step = step;
            _changed = changed;
        }

        /// <summary>The value a pointer at this local offset would pick.</summary>
        public int ValueAt(float localX)
        {
            return ShellSliderMath.ValueAt(
                localX, Rect.rect.width, GrabberSize, MaxValue, Step);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            Apply(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            Apply(eventData);
        }

        private RectTransform Rect
        {
            get
            {
                if (_rect == null)
                {
                    _rect = transform as RectTransform;
                }

                return _rect;
            }
        }

        private void Apply(PointerEventData eventData)
        {
            if (_changed == null || eventData == null || Rect == null)
            {
                return;
            }

            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    Rect, eventData.position, eventData.pressEventCamera, out local))
            {
                return;
            }

            _changed(ValueAt(local.x - Rect.rect.xMin));
        }
    }
}
