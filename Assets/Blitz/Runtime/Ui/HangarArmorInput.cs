using System;
using BlitzLegacy.Runtime.Hangar;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>Transparent native uGUI viewport: orbit on drag, fire on click.</summary>
    public sealed class HangarArmorInput : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        private HangarOrbitController _orbit;
        private HangarArmorViewer _viewer;
        private bool _dragged;

        public Action<HangarArmorViewer.HitInfo> Hit;
        public Action Miss;

        public void Configure(HangarOrbitController orbit, HangarArmorViewer viewer)
        {
            _orbit = orbit;
            _viewer = viewer;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            _dragged = false;
            if (_orbit != null) { _orbit.BeginDrag(); }
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.delta.sqrMagnitude > 4f) { _dragged = true; }
            if (_orbit != null) { _orbit.Drag(eventData.delta); }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (_orbit != null) { _orbit.EndDrag(); }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_dragged || _viewer == null) { return; }
            HangarArmorViewer.HitInfo info;
            if (_viewer.TryShoot(eventData.position, out info))
            {
                if (Hit != null) { Hit(info); }
            }
            else if (Miss != null)
            {
                Miss();
            }
        }
    }
}
