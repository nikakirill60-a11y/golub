using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The draw states a Godot Button has and uGUI's Button does not.
    ///
    /// Godot picks one StyleBox per state and redraws at once: no tween, no
    /// scale, no fade. In assets/fonts/ui_theme_blitz.tres only the base Button
    /// type carries a pressed StyleBox of its own
    /// (style_button_generic_press.stylebox: its own atlas region, multiplied
    /// by <see cref="UiTheme.ButtonPressTint"/>), and every type brightens the
    /// caption to <see cref="UiTheme.ButtonFontPressed"/>. Buttons without an
    /// authored pressed background reuse that same modulate over the normal
    /// one, which is the darkening the theme means by "pressed".
    ///
    /// A Godot Button with toggle_mode keeps drawing the pressed style while
    /// button_pressed stays true; that is how the source marks "you are on this
    /// screen" (scripts/ui_elements/tab_container_custom.gd presses the button
    /// of the open tab and unpresses the rest). <see cref="Latched"/> is that
    /// flag and <see cref="Select"/> is that helper.
    ///
    /// The normal look is snapshotted when the button enters its pressed state
    /// and restored when it leaves, so screens are free to keep recolouring
    /// their buttons (settings tabs, enum options) without fighting this
    /// component.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public sealed class GodotButtonState : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        private Image _background;
        private Text _caption;
        private Sprite _pressedSprite;
        private bool _hasPressedTint;
        private Color _pressedTint;
        private Color _pressedFont = UiTheme.ButtonFontPressed;
        private GameObject _selectedRect;

        private bool _drawn;
        private Sprite _restoreSprite;
        private Color _restoreTint;
        private Color _restoreFont;

        private bool _held;
        private bool _latched;

        /// <summary>Godot Button.toggle_mode.</summary>
        public bool ToggleMode { get; set; }

        /// <summary>Godot Button.button_pressed, for toggle_mode buttons.</summary>
        public bool Latched
        {
            get { return _latched; }
            set
            {
                if (_latched == value)
                {
                    return;
                }

                _latched = value;
                Apply();
            }
        }

        /// <summary>True while the button draws its pressed StyleBox.</summary>
        public bool DrawsPressed
        {
            get { return _held || (ToggleMode && _latched); }
        }

        /// <summary>theme_override_styles/pressed, when the theme has one.</summary>
        public void SetPressedStyle(Sprite sprite, Color tint)
        {
            _pressedSprite = sprite;
            _pressedTint = tint;
            _hasPressedTint = true;
            Redraw();
        }

        /// <summary>Button/colors/font_pressed_color.</summary>
        public void SetPressedFont(Color color)
        {
            _pressedFont = color;
            Redraw();
        }

        /// <summary>
        /// ButtonCustom.toggle_rect from scripts/ui_elements/button_generic.gd:
        /// a node the source shows only while the button stays pressed.
        /// </summary>
        public void SetSelectedRect(GameObject rect)
        {
            _selectedRect = rect;
            if (_selectedRect != null)
            {
                _selectedRect.SetActive(DrawsPressed);
            }
        }

        /// <summary>
        /// tab_container_custom.gd: press the button of the open tab and
        /// unpress every other button of the group.
        /// </summary>
        public static void Select(IList<Button> buttons, int index)
        {
            if (buttons == null)
            {
                return;
            }

            for (var i = 0; i < buttons.Count; i++)
            {
                var state = Of(buttons[i]);
                if (state == null)
                {
                    continue;
                }

                state.ToggleMode = true;
                state.Latched = i == index;
            }
        }

        public static GodotButtonState Of(Button button)
        {
            return button == null ? null : button.GetComponent<GodotButtonState>();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            _held = true;
            Apply();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _held = false;
            Apply();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _held = false;
            Apply();
        }

        private void OnDisable()
        {
            _held = false;
            Apply();
        }

        /// <summary>Redraw only if the button is already showing a state.</summary>
        private void Redraw()
        {
            if (_drawn)
            {
                Restore();
                Apply();
            }
        }

        private void Apply()
        {
            var pressed = DrawsPressed;
            if (pressed == _drawn)
            {
                return;
            }

            if (!pressed)
            {
                Restore();
                return;
            }

            _background = GetComponent<Image>();
            _caption = Caption();
            if (_background != null)
            {
                _restoreSprite = _background.sprite;
                _restoreTint = _background.color;
                _background.sprite = _pressedSprite != null
                    ? _pressedSprite
                    : _restoreSprite;
                _background.color = _hasPressedTint
                    ? _pressedTint
                    : _restoreTint * UiTheme.ButtonPressTint;
            }

            if (_caption != null)
            {
                _restoreFont = _caption.color;
                _caption.color = _pressedFont;
            }

            if (_selectedRect != null)
            {
                _selectedRect.SetActive(true);
            }

            _drawn = true;
        }

        private void Restore()
        {
            if (!_drawn)
            {
                return;
            }

            _drawn = false;
            if (_background != null)
            {
                _background.sprite = _restoreSprite;
                _background.color = _restoreTint;
            }

            if (_caption != null)
            {
                _caption.color = _restoreFont;
            }

            if (_selectedRect != null)
            {
                _selectedRect.SetActive(false);
            }
        }

        /// <summary>
        /// The caption is added after this component, so it is looked up when
        /// a state is first drawn rather than on Awake.
        /// </summary>
        private Text Caption()
        {
            var label = transform.Find("label");
            return label == null ? null : label.GetComponent<Text>();
        }
    }
}
