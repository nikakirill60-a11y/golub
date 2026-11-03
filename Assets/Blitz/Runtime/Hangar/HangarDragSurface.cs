using UnityEngine;
using UnityEngine.EventSystems;

namespace BlitzLegacy.Runtime.Hangar
{
    /// <summary>
    /// Transparent hit surface below the hangar buttons. Because uGUI dispatches
    /// to the topmost raycast target, dragging a button never leaks into orbit.
    /// </summary>
    public sealed class HangarDragSurface : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerUpHandler
    {
        private HangarOrbitController _orbit;
        private int _pointerId = int.MinValue;

        public void Configure(HangarOrbitController orbit)
        {
            _orbit = orbit;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (_pointerId != int.MinValue)
            {
                return;
            }

            _pointerId = eventData.pointerId;
            if (_orbit != null)
            {
                _orbit.BeginDrag();
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_pointerId == eventData.pointerId && _orbit != null)
            {
                _orbit.Drag(eventData.delta);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            Release(eventData.pointerId);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            Release(eventData.pointerId);
        }

        private void Release(int pointerId)
        {
            if (_pointerId != pointerId)
            {
                return;
            }

            _pointerId = int.MinValue;
            if (_orbit != null)
            {
                _orbit.EndDrag();
            }
        }

        private void OnDisable()
        {
            if (_orbit != null && _pointerId != int.MinValue)
            {
                _orbit.EndDrag();
            }

            _pointerId = int.MinValue;
        }
    }
}
