using System;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// A battle button that reports whether it is being held, which is the only
    /// thing ui_player_battle.gd asks of the mobile buttons: it binds
    /// button_down and button_up and turns them into pressed and released
    /// actions, or into its own fire_held flag.
    ///
    /// The source also clears fire_held whenever the button stops being visible
    /// or stops reporting itself as pressed, so the held state is dropped on
    /// disable here as well.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class BattleHoldButton : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler
    {
        /// <summary>Unity numbers the mouse buttons -1..-3, so -1 cannot mean "none".</summary>
        private const int NoPointer = int.MinValue;

        private int _pointer = NoPointer;

        /// <summary>Raised with true on button_down and false on button_up.</summary>
        public Action<bool> Changed;

        public bool Held { get; private set; }

        public Image Skin { get; private set; }

        public Image Icon { get; private set; }

        public static BattleHoldButton Create(
            RectTransform parent,
            string name,
            float anchorLeft,
            float anchorTop,
            float anchorRight,
            float anchorBottom,
            float offsetLeft,
            float offsetTop,
            float offsetRight,
            float offsetBottom,
            Sprite backgroundSprite = null,
            Sprite iconSprite = null,
            float iconScale = 1f)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            GodotRect.Apply(
                rect, anchorLeft, anchorTop, anchorRight, anchorBottom,
                offsetLeft, offsetTop, offsetRight, offsetBottom);

            var image = go.AddComponent<Image>();
            image.sprite = backgroundSprite;
            image.type = Image.Type.Simple;
            image.color = backgroundSprite == null
                ? new Color(1f, 1f, 1f, 0.2f)
                : Color.white;

            Image icon = null;
            if (iconSprite != null)
            {
                icon = UiFactory.Picture("icon", rect, iconSprite);
                var iconRect = icon.rectTransform;
                iconRect.anchorMin = new Vector2(0.5f, 0.5f);
                iconRect.anchorMax = new Vector2(0.5f, 0.5f);
                iconRect.pivot = new Vector2(0.5f, 0.5f);
                iconRect.anchoredPosition = Vector2.zero;
                iconRect.sizeDelta = new Vector2(
                    iconSprite.rect.width * iconScale,
                    iconSprite.rect.height * iconScale);
                icon.raycastTarget = false;
            }

            var button = go.AddComponent<BattleHoldButton>();
            button.Skin = image;
            button.Icon = icon;
            return button;
        }

        public void Press()
        {
            if (Held)
            {
                return;
            }

            Held = true;
            if (Changed != null)
            {
                Changed(true);
            }
        }

        public void Release()
        {
            if (!Held)
            {
                return;
            }

            Held = false;
            if (Changed != null)
            {
                Changed(false);
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_pointer != NoPointer)
            {
                return;
            }

            _pointer = eventData.pointerId;
            Press();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.pointerId != _pointer)
            {
                return;
            }

            _pointer = NoPointer;
            Release();
        }

        private void OnDisable()
        {
            _pointer = NoPointer;
            Release();
        }
    }
}
