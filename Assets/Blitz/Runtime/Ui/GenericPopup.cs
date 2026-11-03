using System;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Port of assets/gfx/ui/popup_generic.tscn and its embedded script, the
    /// window Global.create_popup(header, text, simple, on_accept) hands to
    /// UIGlobal.display_popup.
    ///
    /// The source is a full-rect Control with mouse_filter STOP, a grey_panel
    /// instance scaled to 0.5, a bold header, a centred body label and an
    /// HBoxContainer with btn_ok (ButtonGreen) and btn_cancel. init() wires
    /// the accept callback and frees the window afterwards, init_simple()
    /// additionally frees btn_ok, cancel only frees the window, ui_cancel
    /// closes it and ui_accept presses the leading button.
    ///
    /// Geometry and captions come from
    /// Resources/Data/battle_menu_source_manifest.json, dumped headlessly by
    /// Tools/godot_battle_menu_dump.gd; BattlePauseMenuTests re-reads that
    /// manifest and fails if these constants drift from it.
    /// </summary>
    public sealed class GenericPopup : MonoBehaviour
    {
        public const int HeaderFontSize = 20;
        public const int TextFontSize = 18;
        public const int ButtonFontSize = 16;

        /// <summary>grey_panel keeps its authored 0.5 scale here too.</summary>
        public const float PanelScale = 0.5f;

        private Action _accept;
        private bool _closed;

        /// <summary>Raised once the window frees itself, for its owner.</summary>
        public Action Dismissed;

        public RectTransform Root { get; private set; }

        public Image Panel { get; private set; }

        public Text Header { get; private set; }

        public Text Body { get; private set; }

        /// <summary>btn_ok, or null after init_simple freed it.</summary>
        public Button Ok { get; private set; }

        public Button Cancel { get; private set; }

        /// <summary>
        /// Global.create_popup: <paramref name="simple"/> drops btn_ok, and
        /// <paramref name="onAccept"/> runs before the window frees itself.
        /// </summary>
        public static GenericPopup Create(
            RectTransform parent,
            AtlasRegionTable atlas,
            LocalizationTable lang,
            Font bold,
            Font regular,
            string headerKey,
            string textKey,
            bool simple,
            Action onAccept)
        {
            if (parent == null) { throw new ArgumentNullException("parent"); }
            if (atlas == null) { throw new ArgumentNullException("atlas"); }
            if (lang == null) { throw new ArgumentNullException("lang"); }

            var root = UiFactory.Node("popup", parent);
            GodotRect.FullRect(root);
            var popup = root.gameObject.AddComponent<GenericPopup>();
            popup.Root = root;
            popup._accept = onAccept;

            // mouse_filter STOP on the popup root: the window swallows every
            // click that misses its buttons instead of letting it through to
            // the battle underneath.
            var blocker = root.gameObject.AddComponent<Image>();
            blocker.color = new Color(0f, 0f, 0f, 0f);

            popup.Build(atlas, lang, bold, regular, headerKey, textKey, simple);
            return popup;
        }

        private void Build(
            AtlasRegionTable atlas,
            LocalizationTable lang,
            Font bold,
            Font regular,
            string headerKey,
            string textKey,
            bool simple)
        {
            // panel: the grey_panel instance, body plus its header strip.
            Panel = Sliced("panel", Root, atlas, "panel_body");
            GodotRect.Apply(
                Panel.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f,
                -194f, -102f, 576f, 338f, GodotRect.TopLeftPivot);
            Panel.rectTransform.localScale =
                new Vector3(PanelScale, PanelScale, 1f);

            var strip = Sliced("header", Panel.transform, atlas, "panel_header");
            GodotRect.Apply(
                strip.rectTransform, 0f, 0f, 1f, 0f,
                0f, -84f, 0f, 2f, GodotRect.TopLeftPivot);

            Header = UiFactory.Label(
                "header", Root, bold, HeaderFontSize, UiTheme.BoldLabel,
                TextAnchor.UpperCenter, lang.Get(headerKey));
            UiFactory.AddShadow(Header, UiTheme.BoldLabelShadow);
            GodotRect.Centered(Header.rectTransform, -24f, -131f, 24f, -115f);

            Body = UiFactory.Label(
                "main_text", Root, regular, TextFontSize, UiTheme.Label,
                TextAnchor.MiddleCenter, lang.Get(textKey));
            GodotRect.Centered(Body.rectTransform, -104.5f, -51f, 104.5f, 3f);

            var row = UiFactory.Node("HBoxContainer", Root);
            GodotRect.Centered(row, -172f, 46f, 172f, 106f);

            if (!simple)
            {
                Ok = MakeButton(
                    "btn_ok", row, atlas, "button_green", bold, lang.Get("OK"),
                    0f, 0f, 170f, 60f, Accept);
            }

            Cancel = MakeButton(
                "btn_cancel", row, atlas, "button_generic", bold,
                lang.Get("CANCEL"), 174f, 0f, 344f, 60f, Close);
        }

        private static Image Sliced(
            string name, Transform parent, AtlasRegionTable atlas, string region)
        {
            var image = UiFactory.Picture(name, parent, atlas.Get(region));
            image.type = Image.Type.Sliced;
            return image;
        }

        private static Button MakeButton(
            string name,
            RectTransform parent,
            AtlasRegionTable atlas,
            string region,
            Font bold,
            string text,
            float left,
            float top,
            float right,
            float bottom,
            Action pressed)
        {
            var button = UiFactory.TextButton(
                name, parent, atlas.Get(region), bold, ButtonFontSize, text);
            var image = button.GetComponent<Image>();
            image.type = Image.Type.Sliced;
            GodotRect.TopLeft((RectTransform)button.transform, left, top, right, bottom);
            button.onClick.AddListener(() =>
            {
                // button_generic.gd plays Audio.ui_enter at 0.7 on every press.
                var audio = BlitzBootstrap.Current != null
                    ? BlitzBootstrap.Current.Audio : null;
                if (audio != null)
                {
                    audio.PlayButtonClick(AudioService.SoundEnter);
                }

                pressed();
            });
            return button;
        }

        /// <summary>btn_ok: the callback runs and the window frees itself.</summary>
        public void Accept()
        {
            var accept = _accept;
            _accept = null;
            if (accept != null)
            {
                accept();
            }

            // The callback may have taken the window down with its owner,
            // at once outside play mode.
            if (this != null)
            {
                Close();
            }
        }

        /// <summary>queue_free(), from btn_cancel and from ui_cancel.</summary>
        public void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            // queue_free() takes the window out of the tree at once as far as
            // everything else is concerned, while Unity's Destroy only lands
            // at the end of the frame.
            gameObject.SetActive(false);
            if (Dismissed != null)
            {
                var dismissed = Dismissed;
                Dismissed = null;
                dismissed();
            }

            if (Application.isPlaying)
            {
                Destroy(gameObject);
            }
            else
            {
                DestroyImmediate(gameObject);
            }
        }

        /// <summary>
        /// _unhandled_key_input: ui_cancel closes the window and ui_accept
        /// presses btn_ok, or btn_cancel once init_simple has freed it.
        /// </summary>
        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                Close();
                return;
            }

            if (keyboard.enterKey.wasPressedThisFrame
                || keyboard.numpadEnterKey.wasPressedThisFrame
                || keyboard.spaceKey.wasPressedThisFrame)
            {
                PressDefault();
            }
        }

        /// <summary>ui_accept: btn_ok when it still exists, btn_cancel otherwise.</summary>
        public void PressDefault()
        {
            if (Ok != null)
            {
                Ok.onClick.Invoke();
                return;
            }

            if (Cancel != null)
            {
                Cancel.onClick.Invoke();
            }
        }
    }
}
