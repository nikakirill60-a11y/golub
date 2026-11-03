using System;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// screens/main/menu from ui_hangar.tscn: the burger drawer btn_menu slides
    /// in from the left edge.
    ///
    /// The source is not a pop-up. The panel always exists and always keeps its
    /// place in the tree; AnimationPlayer_menu only animates its position, so a
    /// closed drawer is simply parked one full width off-screen. This port does
    /// the same instead of toggling the GameObject, because the two differ the
    /// moment the drawer is caught mid-slide: play_backwards("show_menu")
    /// resumes from wherever the playhead is, it does not restart.
    ///
    /// Geometry, styles and captions come from
    /// Resources/Data/hangar_menu_source_manifest.json, dumped headlessly from
    /// the Godot scene by Tools/godot_hangar_menu_dump.gd; HangarMenuTests
    /// re-reads that manifest and fails if these constants drift from it.
    /// </summary>
    public sealed class HangarMenuPanel : MonoBehaviour
    {
        /// <summary>menu/anchor_top: the drawer starts under the header band.</summary>
        public const float AnchorTop = 0.16000001f;

        public const float Width = 289f;

        /// <summary>show_menu key 0: the drawer parks one full width to the left.</summary>
        public const float ClosedX = -289f;

        /// <summary>show_menu key 1.</summary>
        public const float OpenX = 0f;

        /// <summary>show_menu length, linear (Animation interpolation 1).</summary>
        public const float SlideSeconds = 0.23333333f;

        public const float ButtonHeight = 64f;

        /// <summary>btn_logout height plus the VBoxContainer separation.</summary>
        public const float SettingsTop = 68f;

        /// <summary>account_toolbox's dumped top, i.e. where the source put it.</summary>
        public const float ToolboxTop = 136f;

        /// <summary>
        /// btn_mods, the one entry the Godot drawer never had. It takes the
        /// slot right under btn_settings, in every build.
        ///
        /// It used to be pushed below the debug account toolbox instead, to
        /// leave that toolbox on its dumped position. That was the wrong trade:
        /// MODS is a game button and the toolbox is a debug tool, so the game
        /// button ended up buried under two hundred pixels of debug rows -- and
        /// under the toolbox's last row as well, because the clearance was
        /// measured against the tinted background (246 px) while the rows
        /// actually run 270 px (see HangarAccountToolbox.InnerHeight).
        /// </summary>
        public const float ModsTop = ToolboxTop;

        /// <summary>
        /// Where the debug toolbox is actually built: under MODS, since that
        /// button now owns the toolbox's dumped slot. The column still ends
        /// above the hangar footer -- 204 + 270 = 474 against the footer plate
        /// at 476 in drawer space -- so nothing has to be scaled to fit.
        /// </summary>
        public static float ToolboxRuntimeTop
        {
            get { return ModsTop + ButtonHeight + Separation; }
        }

        /// <summary>The unnamed ColorRect: a hairline down the drawer's edge.</summary>
        public const float EdgeStripWidth = 2f;

        public const int ButtonFontSize = 16;

        /// <summary>Godot's default BoxContainer separation.</summary>
        public const float Separation = 4f;

        /// <summary>SidebarIcons07/08 are authored at twice the drawer's scale.</summary>
        public const float IconScale = 0.455f;

        private static readonly Rect LogoutIcon = new Rect(22f, 10f, 30.03f, 42.77f);
        private static readonly Rect SettingsIcon = new Rect(17f, 10f, 39.13f, 42.77f);

        private HangarUiKit _kit;
        private RectTransform _rect;
        private bool _open;
        private float _playhead;

        /// <summary>btn_logout: Godot asks to confirm, then goes to the login screen.</summary>
        public Action LogoutPressed;

        /// <summary>btn_settings: Godot wires it to the settings screen.</summary>
        public Action SettingsPressed;

        /// <summary>btn_mods: opens the Godot-mod installer.</summary>
        public Action ModsPressed;

        /// <summary>btn_menu's pressed state, i.e. ui_hangar.gd's menu_shown.</summary>
        public bool IsOpen
        {
            get { return _open; }
        }

        /// <summary>Where the slide currently is, for tests and for the smoke run.</summary>
        public float PositionX
        {
            get { return SlideX(_playhead); }
        }

        /// <summary>True while any part of the drawer is on screen.</summary>
        public bool IsVisible
        {
            get { return _playhead > 0f; }
        }

        public Button Logout { get; private set; }

        public Button Settings { get; private set; }

        public Button Mods { get; private set; }

        /// <summary>The debug toolbox, or null in a release build.</summary>
        public HangarAccountToolbox Toolbox { get; private set; }

        public static HangarMenuPanel Create(RectTransform parent, HangarUiKit kit)
        {
            if (parent == null)
            {
                throw new ArgumentNullException("parent");
            }

            if (kit == null)
            {
                throw new ArgumentNullException("kit");
            }

            var rect = UiFactory.Node("menu", parent);
            var panel = rect.gameObject.AddComponent<HangarMenuPanel>();
            panel._kit = kit;
            panel._rect = rect;
            panel.Build();
            panel.SetOpenImmediate(false);
            return panel;
        }

        private void Build()
        {
            // blur.tscn: a full-panel ColorRect running ui_blur.gdshader. The
            // port approximates it the same way the tech tree and the module
            // shop do, with HangarUiKit.ScreenTint.
            var blur = _kit.Picture("blur", _rect, null, 0f, 0f, Width, Width);
            GodotRect.FullRect(blur.rectTransform);
            blur.color = HangarUiKit.ScreenTint;

            var box = _kit.Node("VBoxContainer", _rect, 0f, 0f, Width, Width);
            GodotRect.Apply(
                box, 0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f, GodotRect.TopLeftPivot);

            Logout = Entry(
                box, "btn_logout", "button_red", "LOGOUT",
                "hangar_logout", LogoutIcon, 0f, () => Raise(LogoutPressed));
            Settings = Entry(
                box, "btn_settings", "button_generic", "SETTINGS",
                "hangar_settings", SettingsIcon, SettingsTop,
                () => Raise(SettingsPressed));
            Mods = Entry(
                box, "btn_mods", "button_generic", "MODS",
                "hangar_settings", SettingsIcon, ModsTop,
                () => Raise(ModsPressed));

            var edge = _kit.Picture(
                "ColorRect", _rect, null, 0f, 0f, EdgeStripWidth, EdgeStripWidth);
            HangarUiKit.RightStretch(edge.rectTransform, -EdgeStripWidth, 0f, 0f, 0f);
            edge.color = Color.black;
        }

        /// <summary>
        /// account_toolbox, which Godot queue_free()s outside a debug build.
        /// Built after the two buttons so it keeps the source's draw order.
        /// </summary>
        public void BuildToolbox(HangarAccountToolbox toolbox)
        {
            if (toolbox == null)
            {
                throw new ArgumentNullException("toolbox");
            }

            if (!HangarAccountToolbox.Enabled)
            {
                return;
            }

            // Nothing is scaled here on purpose. Squeezing the column to make
            // MODS fit also squeezed it horizontally, while the blur backdrop
            // and the edge strip kept the full 289 px, so the drawer read as a
            // panel far wider than its own buttons.
            toolbox.Build(_rect, ToolboxRuntimeTop);
            Toolbox = toolbox;
        }

        // ------------------------------------------------------------------
        // show_menu.
        // ------------------------------------------------------------------

        /// <summary>The show_menu track sampled at <paramref name="seconds"/>.</summary>
        public static float SlideX(float seconds)
        {
            var t = Mathf.Clamp01(seconds / SlideSeconds);
            return Mathf.Lerp(ClosedX, OpenX, t);
        }

        /// <summary>btn_menu.toggled: play("show_menu") or play_backwards(it).</summary>
        public void SetOpen(bool open)
        {
            _open = open;
        }

        public void Toggle()
        {
            SetOpen(!_open);
        }

        /// <summary>Jumps to an end state, the way the RESET track does.</summary>
        public void SetOpenImmediate(bool open)
        {
            _open = open;
            _playhead = open ? SlideSeconds : 0f;
            Apply();
        }

        /// <summary>
        /// One AnimationPlayer step. Reversing mid-slide keeps the playhead, so
        /// a drawer closed halfway through opening travels back the short way.
        /// </summary>
        public void Advance(float deltaSeconds)
        {
            var target = _open ? SlideSeconds : 0f;
            _playhead = Mathf.MoveTowards(_playhead, target, Mathf.Max(deltaSeconds, 0f));
            Apply();
        }

        private void Update()
        {
            Advance(Time.unscaledDeltaTime);
        }

        private void Apply()
        {
            var x = SlideX(_playhead);
            GodotRect.Apply(
                _rect, 0f, AnchorTop, 0f, 1f,
                x, 0f, x + Width, 0f, GodotRect.TopLeftPivot);
        }

        // ------------------------------------------------------------------
        // Widgets.
        // ------------------------------------------------------------------

        private Button Entry(
            RectTransform parent,
            string name,
            string styleRegion,
            string key,
            string iconRegion,
            Rect icon,
            float top,
            Action action)
        {
            var button = _kit.StyleButton(
                name, parent, styleRegion, 0f, top, Width, ButtonHeight);
            var rect = (RectTransform)button.transform;

            _kit.Picture("icon", rect, iconRegion, icon.x, icon.y, icon.width, icon.height);
            _kit.Label(
                "label",
                rect,
                ButtonFontSize,
                TextAnchor.MiddleCenter,
                _kit.Str(key),
                _kit.Bold,
                UiTheme.ButtonFont,
                0f,
                0f,
                Width,
                ButtonHeight);
            _kit.Bind(button, action, AudioService.SoundEnter);
            return button;
        }

        private static void Raise(Action action)
        {
            if (action != null)
            {
                action();
            }
        }
    }
}
