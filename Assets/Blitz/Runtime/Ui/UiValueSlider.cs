using System;
using BlitzLegacy.Core.Ui;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The continuous HSlider of a settings row: pressing or dragging anywhere
    /// on the rect moves the value to the pointer, snapped to the row's step.
    ///
    /// <see cref="UiDragSlider"/> is the same behaviour for the whole-shell
    /// sliders of the loadout screen; this one carries the row's own min, max
    /// and step because add_slider_option gives every setting its own range.
    ///
    /// On a vertical scroll page (settings, sandbox) the slider behaves like
    /// an Android SeekBar in a ScrollView: the press only arms it, a tap sets
    /// the value on release, a sideways drag moves the value and a mostly
    /// vertical swipe scrolls the page (<see cref="UiDragHandoff"/>). Outside
    /// a scroll view the press moves the value at once, as Godot's HSlider.
    /// </summary>
    public sealed class UiValueSlider : MonoBehaviour,
        IPointerDownHandler, IPointerClickHandler, IInitializePotentialDragHandler,
        IBeginDragHandler, IDragHandler
    {
        private RectTransform _rect;
        private Action<float> _changed;
        private bool _pendingTap;

        public float MinValue { get; set; }

        public float MaxValue { get; set; }

        public float Step { get; set; }

        /// <summary>The width of the grabber icon, in the slider's own space.</summary>
        public float GrabberSize { get; set; }

        public void Configure(
            float min, float max, float step, float grabber, Action<float> changed)
        {
            MinValue = min;
            MaxValue = max;
            Step = step;
            GrabberSize = grabber;
            _changed = changed;
        }

        /// <summary>The value a pointer at this local offset would pick.</summary>
        public float ValueAt(float localX)
        {
            return GodotRangeMath.ValueAt(
                localX, Rect.rect.width, GrabberSize, MinValue, MaxValue, Step);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            _pendingTap = UiDragHandoff.Enclosing(transform, true) != null;
            if (!_pendingTap)
            {
                Apply(eventData);
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!_pendingTap)
            {
                return;
            }

            _pendingTap = false;
            Apply(eventData);
        }

        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            UiDragHandoff.StopEnclosing(transform, true, eventData);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            _pendingTap = false;
            var page = UiDragHandoff.Enclosing(transform, true);
            if (page != null && UiDragHandoff.RunsAcross(eventData, true))
            {
                UiDragHandoff.HandTo(page, eventData);
                return;
            }

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
