using System;
using System.Collections.Generic;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Port of scripts/ui_elements/touch_surface.gd plus the finger tracking
    /// half of input_camera.gd's _unhandled_input.
    ///
    /// The source is a Control with mouse_filter STOP sitting on a canvas layer
    /// below the HUD, so the engine arbitrates: a finger that lands on a button
    /// or on the stick never reaches the surface, and every finger has exactly
    /// one owner. A full rect graphic placed as the first sibling reproduces
    /// that, because Unity hit tests later siblings first.
    ///
    /// input_camera.gd then decides what a drag means: with two or more fingers
    /// down it measures the distance between the first two and feeds the zoom,
    /// and it returns early so the camera does not also rotate. Any touch going
    /// down or up clears the pinch baseline.
    ///
    /// The source additionally drops events whose device is DEVICE_ID_EMULATION,
    /// because Android emulates a mouse from every touch. Unity keeps touches
    /// and the mouse on separate pointer ids and does not synthesise one from
    /// the other, so that filter has no counterpart here.
    /// </summary>
    public sealed class BattleTouchSurface : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IDragHandler
    {
        private readonly List<int> _pointers = new List<int>();
        private readonly List<Vector2> _positions = new List<Vector2>();

        /// <summary>Camera rotation, in Godot's screen convention (Y down).</summary>
        public Action<Vector2> Looked;

        /// <summary>Current distance between the first two fingers.</summary>
        public Action<float> Pinched;

        /// <summary>A finger went down or up, so the pinch baseline is stale.</summary>
        public Action PinchEnded;

        public int PointerCount { get { return _pointers.Count; } }
        public bool InputEnabled { get; private set; } = true;

        public void SetInputEnabled(bool enabled)
        {
            if (InputEnabled == enabled) return;
            InputEnabled = enabled;
            var image = GetComponent<Image>();
            if (image != null) image.raycastTarget = enabled;
            if (!enabled) { _pointers.Clear(); _positions.Clear(); ResetPinch(); }
        }

        public static BattleTouchSurface Create(RectTransform parent)
        {
            var go = new GameObject("touch_surface", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            GodotRect.FullRect(rect);

            var image = go.AddComponent<Image>();

            // BACKDROP_LAYER in the source: invisible, but it does take input.
            image.color = new Color(0f, 0f, 0f, 0f);

            // Behind the stick and the buttons, which are added afterwards.
            rect.SetAsFirstSibling();
            return go.AddComponent<BattleTouchSurface>();
        }

        /// <summary>touch_began: remember the finger and drop the pinch baseline.</summary>
        public void Press(int pointer, Vector2 position)
        {
            if (!InputEnabled) return;
            Store(pointer, position);
            ResetPinch();
        }

        /// <summary>
        /// dragged: with two fingers this is a pinch and nothing else, otherwise
        /// it rotates the camera. <paramref name="relative"/> is the per event
        /// delta in Godot's screen convention.
        /// </summary>
        public void Move(int pointer, Vector2 position, Vector2 relative)
        {
            if (!InputEnabled) return;
            Store(pointer, position);
            DropStalePointers(pointer);

            if (_pointers.Count >= 2)
            {
                var distance = Vector2.Distance(_positions[0], _positions[1]);
                if (Pinched != null)
                {
                    Pinched(distance);
                }

                return;
            }

            if (Looked != null)
            {
                Looked(relative);
            }
        }

        /// <summary>touch_ended: forget the finger and drop the pinch baseline.</summary>
        public void Release(int pointer)
        {
            var index = _pointers.IndexOf(pointer);
            if (index >= 0)
            {
                _pointers.RemoveAt(index);
                _positions.RemoveAt(index);
            }

            ResetPinch();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            Press(eventData.pointerId, eventData.position);
        }

        public void OnDrag(PointerEventData eventData)
        {
            // Unity's screen Y points up while the ported camera expects Godot's
            // relative, so the vertical delta flips exactly once, here.
            var relative = new Vector2(eventData.delta.x, -eventData.delta.y);
            Move(eventData.pointerId, eventData.position, relative);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            Release(eventData.pointerId);
        }

        private void OnDisable()
        {
            _pointers.Clear();
            _positions.Clear();
            ResetPinch();
        }

        private void Store(int pointer, Vector2 position)
        {
            var index = _pointers.IndexOf(pointer);
            if (index < 0)
            {
                // Press order, which is the order the source's Dictionary keys
                // come back in and therefore which two fingers pinch.
                _pointers.Add(pointer);
                _positions.Add(position);
                return;
            }

            _positions[index] = position;
        }

        /// <summary>
        /// A finger whose pointer-up never arrived (released during a pause,
        /// over a panel that opened under it, or cancelled by the OS) would stay
        /// in the list and turn every later drag into a pinch. The touchscreen
        /// knows how many fingers are really down, so trim the list to it.
        /// </summary>
        private void DropStalePointers(int moving)
        {
            if (_pointers.Count < 2) return;
            var screen = Touchscreen.current;
            if (screen == null) return;

            var down = 0;
            var touches = screen.touches;
            for (var i = 0; i < touches.Count; i++)
            {
                if (touches[i].press.isPressed) down++;
            }

            if (down >= _pointers.Count) return;

            // Keep the finger that is moving right now and drop the oldest
            // entries until the list matches the fingers actually down.
            for (var i = 0; i < _pointers.Count && _pointers.Count > Mathf.Max(down, 1); )
            {
                if (_pointers[i] == moving) { i++; continue; }
                _pointers.RemoveAt(i);
                _positions.RemoveAt(i);
            }

            ResetPinch();
        }

        private void ResetPinch()
        {
            if (PinchEnded != null)
            {
                PinchEnded();
            }
        }
    }
}
