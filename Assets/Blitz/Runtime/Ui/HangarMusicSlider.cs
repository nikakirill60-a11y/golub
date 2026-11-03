using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// A 0..1 slider of the hangar music panel, in the Panzer War player's
    /// look: a thin track, its fill and a square handle, with the whole box as
    /// the touch target. It runs across, or up for an equalizer band, which
    /// also snaps to whole steps and fills from its middle, the 0 dB line.
    /// It reports the value while it is dragged and once more when it is let
    /// go, so the progress bar seeks once instead of on every frame of the
    /// drag.
    /// </summary>
    public sealed class HangarMusicSlider : MonoBehaviour,
        IInitializePotentialDragHandler, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public const float TrackHeight = 4f;
        public const float HandleSize = 12f;

        private RectTransform _area;
        private RectTransform _fill;
        private RectTransform _handle;
        private bool _vertical;
        private int _steps;
        private bool _fromMiddle;
        private float _value;

        /// <summary>The value while the slider is dragged.</summary>
        public event Action<float> Changed;

        /// <summary>The value the slider was let go at.</summary>
        public event Action<float> Committed;

        /// <summary>True from the press to the release.</summary>
        public bool Dragging { get; private set; }

        public float Value
        {
            get { return _value; }
        }

        public static HangarMusicSlider Create(
            string name, Transform parent, Color track, Color fill, Color handle)
        {
            return Create(name, parent, track, fill, handle, false, 0, false);
        }

        /// <summary>
        /// A slider that runs up when <paramref name="vertical"/>, stops only
        /// at <paramref name="steps"/> even steps when there are any, and fills
        /// from its middle rather than its start when <paramref name="fromMiddle"/>.
        /// </summary>
        public static HangarMusicSlider Create(
            string name, Transform parent, Color track, Color fill, Color handle,
            bool vertical, int steps, bool fromMiddle)
        {
            var area = UiFactory.Node(name, parent);
            var hit = area.gameObject.AddComponent<Image>();
            hit.color = new Color(1f, 1f, 1f, 0f);
            hit.raycastTarget = true;

            var bar = UiFactory.Picture("Track", area, null);
            bar.color = track;
            Band(bar.rectTransform, vertical);

            var fillImage = UiFactory.Picture("Fill", area, null);
            fillImage.color = fill;
            var fillRect = fillImage.rectTransform;
            Band(fillRect, vertical);

            var knob = UiFactory.Picture("Handle", area, null);
            knob.color = handle;
            var handleRect = knob.rectTransform;
            var start = vertical ? new Vector2(0.5f, 0f) : new Vector2(0f, 0.5f);
            handleRect.anchorMin = start;
            handleRect.anchorMax = start;
            handleRect.pivot = new Vector2(0.5f, 0.5f);
            handleRect.sizeDelta = new Vector2(HandleSize, HandleSize);

            var slider = area.gameObject.AddComponent<HangarMusicSlider>();
            slider._area = area;
            slider._fill = fillRect;
            slider._handle = handleRect;
            slider._vertical = vertical;
            slider._steps = Math.Max(0, steps);
            slider._fromMiddle = fromMiddle;
            slider.Place();
            return slider;
        }

        /// <summary>Moves the slider without reporting it; a drag in progress keeps its own value.</summary>
        public void SetValue(float value)
        {
            if (Dragging)
            {
                return;
            }

            value = Snap(Mathf.Clamp01(value));
            if (Mathf.Approximately(value, _value))
            {
                return;
            }

            _value = value;
            Place();
        }

        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            // The handle follows the finger from the first pixel.
            eventData.useDragThreshold = false;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            Dragging = true;
            Follow(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Dragging)
            {
                Follow(eventData);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!Dragging)
            {
                return;
            }

            Follow(eventData);
            Release();
        }

        private void OnDisable()
        {
            // A panel hidden under a finger still lets go of its value.
            if (Dragging)
            {
                Release();
            }
        }

        private void Release()
        {
            Dragging = false;
            var committed = Committed;
            if (committed != null)
            {
                committed(_value);
            }
        }

        private void Follow(PointerEventData eventData)
        {
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _area, eventData.position, eventData.pressEventCamera, out local))
            {
                return;
            }

            var rect = _area.rect;
            var along = _vertical ? local.y - rect.yMin : local.x - rect.xMin;
            var span = Mathf.Max(1f, Length(rect) - HandleSize);
            _value = Snap(Mathf.Clamp01((along - (HandleSize / 2f)) / span));
            Place();
            var changed = Changed;
            if (changed != null)
            {
                changed(_value);
            }
        }

        private float Snap(float value)
        {
            return _steps > 0 ? Mathf.Round(value * _steps) / _steps : value;
        }

        private float Length(Rect rect)
        {
            return _vertical ? rect.height : rect.width;
        }

        /// <summary>The handle travels between half a handle in from either end.</summary>
        private void Place()
        {
            var length = Length(_area.rect);
            var at = (HandleSize / 2f) + (_value * Mathf.Max(0f, length - HandleSize));
            var from = _fromMiddle ? length / 2f : 0f;
            var low = Mathf.Min(from, at);
            var high = Mathf.Max(from, at);
            if (_vertical)
            {
                _handle.anchoredPosition = new Vector2(0f, at);
                _fill.offsetMin = new Vector2(_fill.offsetMin.x, low);
                _fill.offsetMax = new Vector2(_fill.offsetMax.x, high - length);
            }
            else
            {
                _handle.anchoredPosition = new Vector2(at, 0f);
                _fill.offsetMin = new Vector2(low, _fill.offsetMin.y);
                _fill.offsetMax = new Vector2(high - length, _fill.offsetMax.y);
            }
        }

        private void OnRectTransformDimensionsChange()
        {
            if (_area != null)
            {
                Place();
            }
        }

        /// <summary>A track-thick band across the middle of the box, or up it.</summary>
        private static void Band(RectTransform rt, bool vertical)
        {
            rt.pivot = new Vector2(0.5f, 0.5f);
            if (vertical)
            {
                rt.anchorMin = new Vector2(0.5f, 0f);
                rt.anchorMax = new Vector2(0.5f, 1f);
                rt.offsetMin = new Vector2(-TrackHeight / 2f, 0f);
                rt.offsetMax = new Vector2(TrackHeight / 2f, 0f);
            }
            else
            {
                rt.anchorMin = new Vector2(0f, 0.5f);
                rt.anchorMax = new Vector2(1f, 0.5f);
                rt.offsetMin = new Vector2(0f, -TrackHeight / 2f);
                rt.offsetMax = new Vector2(0f, TrackHeight / 2f);
            }
        }
    }
}
