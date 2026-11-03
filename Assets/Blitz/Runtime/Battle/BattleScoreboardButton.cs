using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The scoreboard slides its own hit area. Act on touch-down, not on a
    /// release raycast at a now different location. Click/submit must not
    /// toggle a second time after the panel has moved under the finger.
    /// </summary>
    public sealed class BattleScoreboardButton : Button
    {
        private int? _pointer;
        public override void OnPointerDown(PointerEventData eventData)
        {
            base.OnPointerDown(eventData);
            if (eventData.button != PointerEventData.InputButton.Left ||
                !IsActive() || !IsInteractable() || _pointer.HasValue) return;
            _pointer = eventData.pointerId;
            onClick.Invoke();
        }
        public override void OnPointerUp(PointerEventData eventData)
        {
            base.OnPointerUp(eventData);
            if (_pointer == eventData.pointerId) _pointer = null;
        }
        public override void OnPointerClick(PointerEventData eventData)
        {
            // Already handled on down. Button.OnSubmit still invokes onClick.
        }
        protected override void OnDisable()
        {
            _pointer = null;
            base.OnDisable();
        }
    }
}
