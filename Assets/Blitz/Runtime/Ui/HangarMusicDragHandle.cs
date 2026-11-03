using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The header of the hangar music panel as a handle: a drag that starts
    /// on it, on its buttons too, moves the panel. It reports where the
    /// pointer is in <see cref="Area"/>'s space, so the panel works the move
    /// out in canvas units. A press that does not travel past the event
    /// system's drag threshold stays a press of the button under it.
    /// </summary>
    public sealed class HangarMusicDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        /// <summary>The rect the pointer is measured in: the panel's parent.</summary>
        public RectTransform Area { get; set; }

        /// <summary>The pointer went down here and started to move.</summary>
        public event Action<Vector2> Began;

        /// <summary>The pointer moved on.</summary>
        public event Action<Vector2> Moved;

        /// <summary>The pointer let go, or the handle went away under it.</summary>
        public event Action Ended;

        /// <summary>True from the start of a drag to its end.</summary>
        public bool Dragging { get; private set; }

        public void OnBeginDrag(PointerEventData eventData)
        {
            Vector2 local;
            if (eventData.button != PointerEventData.InputButton.Left || !Locate(eventData, out local))
            {
                return;
            }

            Dragging = true;
            var began = Began;
            if (began != null)
            {
                began(local);
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            Vector2 local;
            if (!Dragging || !Locate(eventData, out local))
            {
                return;
            }

            var moved = Moved;
            if (moved != null)
            {
                moved(local);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!Dragging)
            {
                return;
            }

            Vector2 local;
            if (Locate(eventData, out local))
            {
                var moved = Moved;
                if (moved != null)
                {
                    moved(local);
                }
            }

            Finish();
        }

        private void OnDisable()
        {
            // A panel hidden under a finger still ends the move where it is.
            if (Dragging)
            {
                Finish();
            }
        }

        private void Finish()
        {
            Dragging = false;
            var ended = Ended;
            if (ended != null)
            {
                ended();
            }
        }

        private bool Locate(PointerEventData eventData, out Vector2 local)
        {
            local = Vector2.zero;
            return Area != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                Area, eventData.position, eventData.pressEventCamera, out local);
        }
    }
}
