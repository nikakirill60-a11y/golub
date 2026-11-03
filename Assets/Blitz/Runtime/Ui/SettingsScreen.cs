using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Input;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Lighting;
using BlitzLegacy.Runtime.Platform;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Code-first port of assets/gfx/ui/ui_settings.tscn and ui_settings.gd.
    ///
    /// The source hides three row templates inside the first tab and
    /// duplicates them into two columns per tab, one row per setting, then
    /// writes every change straight into Global.save_settings and saves when
    /// the back button closes the screen. The port does the same: the row list
    /// is data (Resources/Data/settings_source_manifest.json), the widgets are
    /// built from the templates' captured rects, and the values are read and
    /// written through <see cref="SettingsData"/>'s option-name accessors,
    /// which are the counterpart of the source's <c>get(name)</c> /
    /// <c>set(name, value)</c>.
    ///
    /// The controls tab is the port of assets/gfx/ui/pc_controls_settings.gd:
    /// it lists the rebindable battle actions of PcControlBindings grouped by
    /// category, captures the next key or mouse button for the slot that was
    /// clicked and saves at once. The source hides it on mobile, and so does
    /// the port.
    ///
    /// The debug tab is still not built: it only exists in a build carrying
    /// the sentry_qa feature.
    ///
    /// Two rows are the port's own: the hangar picker (the name of the hangar
    /// on screen; a click opens <see cref="HangarPickerPopup"/>) and the music
    /// player switch of that hangar. Both judge the choice through a
    /// <see cref="HangarChoiceContext"/>, the same one the hangar screen uses.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SettingsScreen : MonoBehaviour
    {
        // -- header ---------------------------------------------------------

        /// <summary>btn_menu, variation ButtonSmallGeneric.</summary>
        private const float BackX = 5f;
        private const float BackY = 4f;
        private const float BackWidth = 94f;
        private const float BackHeight = 77f;
        private const float BackIconMaxWidth = 50f;

        /// <summary>panel_main_background: preset 10, drawn at 0.96.</summary>
        private const float HeaderLeft = -68f;
        private const float HeaderTop = -74f;
        private const float HeaderRight = 97.2084f;
        private const float HeaderBottom = 94.0417f;
        private const float HeaderScale = 0.96f;

        private const float LineLeft = 70.8333f;
        private const float LineTop = 167.7083f;
        private const float LineRight = -5.9027f;
        private const float LineBottom = 170.9636f;

        /// <summary>tank_name.</summary>
        private const float TitleX = 128f;
        private const float TitleY = 29f;
        private const float TitleWidth = 329f;
        private const float TitleHeight = 40f;
        private const int TitleFontSize = 30;

        /// <summary>HBoxContainer, the tab strip above the pages.</summary>
        private const float TabBarTop = 92f;
        private const float TabBarBottom = 158f;
        private const int TabFontSize = 25;

        /// <summary>tabs: preset 15 with offset_top = 164.</summary>
        private const float ContentTop = 164f;

        /// <summary>Godot's default BoxContainer separation.</summary>
        private const float Separation = 4f;

        // -- row templates ----------------------------------------------------

        /// <summary>*_template custom_minimum_size.</summary>
        public const float RowHeight = 91.625f;

        /// <summary>NinePatchRect: preset 15 with offset_left/right -2 / +2.</summary>
        private const float RowBackgroundBleed = 2f;

        /// <summary>name: preset 4, offsets 16 / -16 / 95 / 16.</summary>
        private const float LabelX = 16f;
        private const float LabelWidth = 79f;
        private const float LabelHeight = 32f;
        private const int LabelFontSize = 21;

        /// <summary>checkbox: preset 6, offsets -50.99994 / -11 / -34.99994 / 5.</summary>
        private const float CheckboxRight = 50.9999f;
        private const float CheckboxTop = -11f;
        private const float CheckboxSize = 16f;
        private const float CheckboxScale = 3f;

        /// <summary>btns: preset 11, offsets -487 / 10 / -6 / -9.625, alignment END.</summary>
        private const float OptionsRight = 6f;
        private const float OptionsTop = 10f;
        private const float OptionsBottom = 9.625f;

        /// <summary>btn2 custom_minimum_size.</summary>
        private const float OptionWidth = 77.29f;
        private const float OptionHeight = 71.625f;
        private const int OptionFontSize = 25;

        /// <summary>name/value: preset 6 off the label, offsets 8 / -10.5 / 48 / 10.5.</summary>
        private const float ValueGap = 8f;
        private const float ValueWidth = 40f;
        private const float ValueHeight = 21f;
        private const int ValueFontSize = 20;

        /// <summary>slider_bg: right-centred, drawn at 0.33.</summary>
        private const float FrameRight = 328f;
        private const float FrameTop = -37.8125f;
        private const float FrameWidth = 981.8177f;
        private const float FrameHeight = 218.1819f;
        private const float FrameScale = 0.33f;

        /// <summary>slider: right-centred, drawn at 0.5.</summary>
        private const float SliderRight = 324f;
        private const float SliderTop = -34.8125f;
        private const float SliderWidth = 632f;
        private const float SliderHeight = 132f;
        private const float SliderScale = 0.5f;

        /// <summary>SliderThumb.tres, the grabber the slider draws.</summary>
        private const float GrabberSize = 132f;

        /// <summary>
        /// StyleBoxFlat_uuqtk: border_width_top and _bottom are 12 each and
        /// there is no content margin, so Godot draws the bar 24 px tall,
        /// centred in the widget, and the border colour covers all of it.
        /// </summary>
        private const float ClassicBarHeight = 24f;

        /// <summary>Modern: a thin hairline track (6 px at the 0.5 slider scale).</summary>
        private const float ModernBarHeight = 6f;

        private static readonly Color ClassicBarColor =
            new Color(0.19445327f, 0.21491614f, 0.21491715f, 1f);

        private static readonly Color ClassicFillColor =
            new Color(0.21176471f, 0.21176471f, 0.21176471f, 1f);

        private static readonly Color ModernBarColor = new Color(1f, 1f, 1f, 0.14f);

        private float BarHeight { get { return MenuStyle.InMenus ? ModernBarHeight : ClassicBarHeight; } }

        private Color BarColor { get { return MenuStyle.InMenus ? ModernBarColor : ClassicBarColor; } }

        private Color FillColor { get { return MenuStyle.InMenus ? ModernTheme.Accent : ClassicFillColor; } }

        private static readonly Color ValueColor =
            new Color(1f, 0.8039216f, 0.50980395f, 1f);

        internal static readonly Color OptionNormal =
            new Color(0.09411765f, 0.12941177f, 0.16078432f, 0.34901962f);

        internal static readonly Color OptionPressed =
            new Color(0.2901961f, 0.39607844f, 0.49019608f, 0.92156863f);

        /// <summary>The hangar button keeps at least the width of three option buttons.</summary>
        private const float HangarButtonMinWidth = (OptionWidth * 3f) + (Separation * 2f);
        private const float HangarArrowWidth = 19f;
        private const float HangarArrowHeight = 13f;
        private const float HangarButtonPadding = 18f;

        // -- atlas regions -----------------------------------------------------

        public const string BackStyleRegion = "button_small_generic";
        public const string BackIconRegion = "settings_back_icon";
        public const string HeaderRegion = "hangar_header";
        public const string LineRegion = "hangar_thin_red_line";
        public const string RowRegion = "settings_row_bg";
        public const string FrameRegion = "slider_frame";
        public const string ThumbRegion = "slider_thumb";
        public const string CheckedRegion = "settings_checkbox_checked";
        public const string UncheckedRegion = "settings_checkbox_unchecked";

        /// <summary>pc_controls_settings, the tab the source hides on mobile.</summary>
        public const string ControlsTab = "controls";

        /// <summary>The tabs the port builds, in source order.</summary>
        public static readonly string[] TabNames =
            { "main", "graphics", "matchmaker", "gameplay", ControlsTab };

        /// <summary>Same order as <see cref="TabNames"/>; the bar pairs them by index.</summary>
        public static readonly string[] TabKeys =
            { "MAIN", "GRAPHICS", "MATCHMAKER", "GAMEPLAY", "controls:tab" };

        // -- controls tab -----------------------------------------------------

        /// <summary>pc_controls_settings: the action rows are 58 px tall.</summary>
        private const float ControlRowHeight = 58f;

        /// <summary>The label of a row, custom_minimum_size = (420, 0).</summary>
        private const float ControlLabelWidth = 420f;

        /// <summary>The slot button, custom_minimum_size = (360, 48).</summary>
        private const float ControlButtonWidth = 360f;
        private const float ControlButtonHeight = 48f;

        /// <summary>The reset button, custom_minimum_size = (320, 58).</summary>
        private const float ControlResetWidth = 320f;

        private const float ControlMargin = 16f;
        private const int ControlIntroFontSize = 18;
        private const int ControlCategoryFontSize = 24;
        private const int ControlRowFontSize = 18;

        /// <summary>The hint label's modulate.</summary>
        private static readonly Color ControlHintColor =
            new Color(0.78f, 0.82f, 0.86f, 1f);

        /// <summary>The status label's modulate.</summary>
        private static readonly Color ControlStatusColor =
            new Color(1f, 0.82f, 0.5f, 1f);

        private HangarUiKit _kit;
        private SettingsData _settings;
        private Action _closed;
        private HangarChoiceContext _hangarChoice;
        private readonly List<Row> _hangarRows = new List<Row>();
        private readonly List<Button> _tabButtons = new List<Button>();
        private readonly List<RectTransform> _pages = new List<RectTransform>();
        private readonly List<Row> _rows = new List<Row>();

        /// <summary>The tabs this build shows; controls is desktop only.</summary>
        private readonly List<string> _tabs = new List<string>();

        private readonly List<ControlRow> _controlRows = new List<ControlRow>();
        private Text _controlStatus;
        private Button _controlReset;

        /// <summary>pc_controls_settings._capturing_action.</summary>
        private string _capturing;

        /// <summary>The whole screen, i.e. the Godot ui_settings Control.</summary>
        public RectTransform Root { get; private set; }

        /// <summary>The open hangar list, or null.</summary>
        public HangarPickerPopup HangarPicker { get; private set; }

        /// <summary>
        /// Raised with the setting name after the hangar rows changed it, so the
        /// hangar behind the screen can follow at once.
        /// </summary>
        public Action<string> SettingChanged;

        public Button Back { get; private set; }

        public Text Title { get; private set; }

        public int CurrentTab { get; private set; }

        public IList<Button> TabButtons
        {
            get { return _tabButtons; }
        }

        public IList<Row> Rows
        {
            get { return _rows; }
        }

        /// <summary>One action row of pc_controls_settings.</summary>
        public sealed class ControlRow
        {
            public string Action;
            public Text Label;
            public Button Slot;
            public Text SlotLabel;
        }

        /// <summary>The rebindable rows the controls tab built.</summary>
        public IList<ControlRow> ControlRows
        {
            get { return _controlRows; }
        }

        /// <summary>The action waiting for an input, or null.</summary>
        public string CapturingAction
        {
            get { return _capturing; }
        }

        /// <summary>One duplicated template: the widgets a setting owns.</summary>
        public sealed class Row
        {
            public SettingRow Source;
            public RectTransform Node;
            public Text Label;
            public Text Value;
            public Image Checkbox;
            public Image Fill;
            public Image Grabber;
            public UiValueSlider Slider;
            public readonly List<Button> Options = new List<Button>();

            /// <summary>The hangar row's button, which opens the list.</summary>
            public Button Picker;
        }

        // ------------------------------------------------------------------
        // Lifecycle.
        // ------------------------------------------------------------------

        /// <summary>
        /// ui_settings.tscn added on top of whatever raised it. The caller
        /// hides its own UI first, exactly like _ready()'s
        /// Global.hangar_ui.hide(), and <paramref name="closed"/> is the
        /// btn_menu chain that shows it again.
        /// </summary>
        public static SettingsScreen Open(
            Transform parent,
            SettingsData settings,
            int sortingOrder,
            bool mobile,
            Action closed,
            HangarChoiceContext hangarChoice = null)
        {
            var go = new GameObject("SettingsScreen");
            go.transform.SetParent(parent, false);
            var screen = go.AddComponent<SettingsScreen>();
            screen._settings = settings ?? new SettingsData();
            screen._closed = closed;
            screen._hangarChoice = hangarChoice ?? HangarChoiceContext.Live();
            screen.Build(mobile, sortingOrder);
            return screen;
        }

        /// <summary>btn_menu.pressed: queue_free, Global.save, hangar_ui.show.</summary>
        public void Close()
        {
            var closed = _closed;
            _closed = null;
            Destroy(gameObject);
            if (closed != null)
            {
                closed();
            }
        }

        private void Update()
        {
            // pc_controls_settings._input() runs with set_input_as_handled(),
            // so while a slot waits for its new binding nothing else, the
            // close shortcut included, sees the key.
            if (_capturing != null)
            {
                PollCapture();
                return;
            }

            // The hangar list sits on top of the screen: Escape closes the list
            // alone, like a click beside it.
            if (HangarPicker != null)
            {
                if (Keyboard.current != null
                    && Keyboard.current.escapeKey.wasPressedThisFrame)
                {
                    HangarPicker.Close();
                }

                return;
            }

            // _gui_input: the "menu" action closes the screen. The screen that
            // opened it stops answering that key while it is up, exactly like
            // the Godot screen swallowing the input event.
            if (Keyboard.current != null
                && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                Close();
            }
        }

        /// <summary>
        /// _input(): Escape cancels, Backspace and Delete clear the slot, and
        /// any other supported key or mouse button becomes the new binding.
        /// </summary>
        private void PollCapture()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.escapeKey.wasPressedThisFrame)
                {
                    CancelCapture();
                    return;
                }

                if (keyboard.backspaceKey.wasPressedThisFrame
                    || keyboard.deleteKey.wasPressedThisFrame)
                {
                    ClearCapture();
                    return;
                }
            }

            var token = PcControlInput.Capture();
            if (token != null)
            {
                FinishCapture(token);
            }
        }

        // ------------------------------------------------------------------
        // Build.
        // ------------------------------------------------------------------

        private void Build(bool mobile, int sortingOrder)
        {
            var atlas = MenuStyle.ApplyForMenus(BattleFlowUi.LoadAtlas());
            var lang = BattleFlowUi.LoadLanguage();
            lang.Language = _settings.Language;
            _kit = new HangarUiKit(
                atlas,
                lang,
                BlitzResources.LoadFont(BlitzResources.RegularFontName),
                BlitzResources.LoadFont(BlitzResources.BoldFontName),
                BlitzBootstrap.Current == null ? null : BlitzBootstrap.Current.Audio);

            // The menu canvas: ui_settings is laid out against the same
            // 1040x550 / 0.75 viewport as every other lobby screen.
            var canvas = BattleFlowUi.CreateCanvas(
                transform, false, mobile, sortingOrder);

            Root = UiFactory.Node("ui_settings", canvas);
            GodotRect.FullRect(Root);

            // pc_controls_settings._ready() disables itself on a phone, i.e.
            // the whole tab disappears from a mobile build.
            for (var i = 0; i < TabNames.Length; i++)
            {
                if (mobile && TabNames[i] == ControlsTab)
                {
                    continue;
                }

                _tabs.Add(TabNames[i]);
            }

            BuildBackdrop();
            BuildHeader();
            BuildTabBar();
            BuildPages();
            SelectTab(0);
        }

        private void BuildBackdrop()
        {
            // blur: a full-rect ColorRect running ui_blur.gdshader. The port
            // approximates it the same way the other lobby screens do.
            var blur = UiFactory.Picture("blur", Root, null);
            GodotRect.FullRect(blur.rectTransform);
            blur.color = HangarUiKit.ScreenTint;
            // Unlike a decorative backdrop this one has to swallow clicks, so
            // the screen behind it cannot be operated while settings are up.
            blur.raycastTarget = true;
        }

        private void BuildHeader()
        {
            var header = _kit.Picture(
                "panel_main_background", Root, HeaderRegion, 0f, 0f, 0f, 0f);
            GodotRect.Apply(
                header.rectTransform, 0f, 0f, 1f, 0f,
                HeaderLeft, HeaderTop, HeaderRight, HeaderBottom,
                GodotRect.TopLeftPivot);
            header.rectTransform.localScale =
                new Vector3(HeaderScale, HeaderScale, 1f);
            header.raycastTarget = false;

            var line = _kit.Picture(
                "ThinRedLine", header.rectTransform, LineRegion, 0f, 0f, 0f, 0f);
            GodotRect.Apply(
                line.rectTransform, 0f, 0f, 1f, 0f,
                LineLeft, LineTop, LineRight, LineBottom,
                GodotRect.TopLeftPivot);
            line.raycastTarget = false;

            Title = _kit.Label(
                "tank_name", Root, TitleFontSize, TextAnchor.UpperLeft,
                _kit.Str("SETTINGS"), _kit.Bold, Color.white,
                TitleX, TitleY, TitleWidth, TitleHeight);

            Back = _kit.StyleButton(
                "btn_menu", Root, BackStyleRegion,
                BackX, BackY, BackWidth, BackHeight);
            _kit.AddIcon(Back, BackIconRegion, BackIconMaxWidth);
            _kit.Bind(Back, Close, AudioService.SoundEnter);
        }

        private void BuildTabBar()
        {
            var bar = UiFactory.Node("HBoxContainer", Root);
            GodotRect.Apply(
                bar, 0f, 0f, 1f, 0f, 0f, TabBarTop, 0f, TabBarBottom,
                GodotRect.TopLeftPivot);

            // Every tab button is SIZE_EXPAND_FILL, so the strip splits the
            // canvas evenly and the 4 px separations come out of the shares.
            var count = _tabs.Count;
            for (var i = 0; i < count; i++)
            {
                var index = i;
                var button = _kit.StyleButton(
                    "btn_" + _tabs[i], bar, null, 0f, 0f, 0f, 0f);
                var rt = (RectTransform)button.transform;
                rt.pivot = GodotRect.TopLeftPivot;
                rt.anchorMin = new Vector2((float)i / count, 0f);
                rt.anchorMax = new Vector2((float)(i + 1) / count, 1f);
                rt.offsetMin = new Vector2(i * Separation / count, 0f);
                rt.offsetMax = new Vector2((i + 1 - count) * Separation / count, 0f);

                var keyIndex = System.Array.IndexOf(TabNames, _tabs[i]);
                var label = UiFactory.Label(
                    "label", rt, _kit.Regular, TabFontSize, UiTheme.ButtonFont,
                    TextAnchor.MiddleCenter, _kit.Str(TabKeys[keyIndex]));
                GodotRect.FullRect(label.rectTransform);

                _kit.Bind(button, () => SelectTab(index), AudioService.SoundEnter);
                _tabButtons.Add(button);
            }
        }

        private void BuildPages()
        {
            var rows = SettingRow.Load();
            var width = UiLayout.ReferenceWidth;
            var columnWidth = (width - Separation) * 0.5f;

            for (var tab = 0; tab < _tabs.Count; tab++)
            {
                var page = UiFactory.Node(_tabs[tab], Root);
                GodotRect.Apply(
                    page, 0f, 0f, 1f, 1f, 0f, ContentTop, 0f, 0f,
                    GodotRect.TopLeftPivot);
                page.gameObject.AddComponent<RectMask2D>();

                // ScrollContainer's mouse_filter is STOP, so the scroll area
                // answers the wheel and the drag itself. Every graphic this
                // page builds leaves UiFactory with raycastTarget off, so
                // without a target of its own the ScrollRect was never handed
                // a pointer event: the page did not scroll at all and the
                // wheel fell through to whatever sat behind it, which in a
                // battle is the camera zoom.
                var catcher = page.gameObject.AddComponent<Image>();
                catcher.color = Color.clear;
                catcher.raycastTarget = true;

                var content = UiFactory.Node("VBoxContainer", page);
                content.pivot = GodotRect.TopLeftPivot;
                content.anchorMin = new Vector2(0f, 1f);
                content.anchorMax = new Vector2(1f, 1f);
                content.anchoredPosition = Vector2.zero;

                var scroll = page.gameObject.AddComponent<ScrollRect>();
                scroll.viewport = page;
                scroll.content = content;
                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 40f;

                if (_tabs[tab] == ControlsTab)
                {
                    content.sizeDelta = new Vector2(
                        0f, BuildControlsPage(content, width));
                    _pages.Add(page);
                    continue;
                }

                var left = 0;
                var right = 0;
                foreach (var row in rows)
                {
                    if (row.Tab != _tabs[tab])
                    {
                        continue;
                    }

                    var isLeft = row.Column == "left";
                    var index = isLeft ? left++ : right++;
                    var y = index * (RowHeight + Separation);
                    _rows.Add(BuildRow(row, content, isLeft, y, columnWidth));
                }

                var tallest = left > right ? left : right;
                content.sizeDelta = new Vector2(
                    0f,
                    tallest <= 0 ? 0f : (tallest * (RowHeight + Separation)) - Separation);
                _pages.Add(page);
            }
        }

        // ------------------------------------------------------------------
        // Controls tab: pc_controls_settings.gd.
        // ------------------------------------------------------------------

        /// <summary>
        /// _build_interface(): the intro and hint labels, one heading per
        /// category with its action rows, then the status line and the reset
        /// button. Returns the height the page content needs.
        /// </summary>
        private float BuildControlsPage(RectTransform content, float width)
        {
            var inner = width - (ControlMargin * 2f);
            var y = ControlMargin;

            y += ControlsText(
                content, "intro", "controls:intro", inner, y,
                ControlIntroFontSize, Color.white);
            y += ControlsText(
                content, "hint", "controls:hint", inner, y,
                ControlIntroFontSize, ControlHintColor);

            var category = string.Empty;
            var definitions = PcControlBindings.Definitions;
            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                if (definition.CategoryKey != category)
                {
                    category = definition.CategoryKey;
                    y += ControlsText(
                        content, category, category, inner, y,
                        ControlCategoryFontSize, Color.white, true);
                }

                _controlRows.Add(BuildControlRow(definition, content, inner, y));
                y += ControlRowHeight + Separation;
            }

            _controlStatus = _kit.Label(
                "status", content, ControlRowFontSize, TextAnchor.UpperLeft,
                _kit.Str("controls:status_ready"), _kit.Regular, ControlStatusColor,
                ControlMargin, y, inner, ControlRowHeight);
            y += ControlRowHeight + Separation;

            // The footer box is alignment END, i.e. the button hugs the right.
            _controlReset = _kit.StyleButton(
                "btn_reset", content, null,
                width - ControlMargin - ControlResetWidth, y,
                ControlResetWidth, ControlRowHeight);
            var resetLabel = UiFactory.Label(
                "label", (RectTransform)_controlReset.transform, _kit.Regular,
                ControlRowFontSize, UiTheme.ButtonFont, TextAnchor.MiddleCenter,
                _kit.Str("controls:reset"));
            GodotRect.FullRect(resetLabel.rectTransform);
            _controlReset.GetComponent<Image>().color = OptionNormal;
            _kit.Bind(_controlReset, ConfirmResetControls, AudioService.SoundEnter);

            RefreshControls();
            return y + ControlRowHeight + ControlMargin;
        }

        /// <summary>The intro, hint and category labels: autowrapped text.</summary>
        private float ControlsText(
            RectTransform parent,
            string name,
            string key,
            float width,
            float y,
            int fontSize,
            Color color,
            bool bold)
        {
            var label = _kit.Label(
                name, parent, fontSize, TextAnchor.UpperLeft, _kit.Str(key),
                bold ? _kit.Bold : _kit.Regular, color,
                ControlMargin, y, width, ControlRowHeight);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            return ControlRowHeight + Separation;
        }

        private float ControlsText(
            RectTransform parent,
            string name,
            string key,
            float width,
            float y,
            int fontSize,
            Color color)
        {
            return ControlsText(parent, name, key, width, y, fontSize, color, false);
        }

        /// <summary>_add_action_row(): the label plus the slot button.</summary>
        private ControlRow BuildControlRow(
            PcControlBindings.ActionDefinition definition,
            RectTransform parent,
            float width,
            float y)
        {
            var row = new ControlRow { Action = definition.Action };

            row.Label = _kit.Label(
                definition.Action, parent, ControlRowFontSize,
                TextAnchor.MiddleLeft, _kit.Str(definition.LabelKey),
                _kit.Regular, Color.white,
                ControlMargin, y, ControlLabelWidth, ControlRowHeight);

            row.Slot = _kit.StyleButton(
                definition.Action + "_slot", parent, null,
                ControlMargin + width - ControlButtonWidth,
                y + ((ControlRowHeight - ControlButtonHeight) * 0.5f),
                ControlButtonWidth, ControlButtonHeight);
            row.Slot.GetComponent<Image>().color = OptionNormal;

            row.SlotLabel = UiFactory.Label(
                "label", (RectTransform)row.Slot.transform, _kit.Regular,
                ControlRowFontSize, UiTheme.ButtonFont, TextAnchor.MiddleCenter,
                string.Empty);
            GodotRect.FullRect(row.SlotLabel.rectTransform);

            var action = definition.Action;
            _kit.Bind(row.Slot, () => BeginCapture(action), AudioService.SoundEnter);
            return row;
        }

        /// <summary>_refresh_bindings().</summary>
        private void RefreshControls()
        {
            for (var i = 0; i < _controlRows.Count; i++)
            {
                var row = _controlRows[i];
                row.SlotLabel.text = row.Action == _capturing
                    ? _kit.Str("controls:press_input")
                    : PcControlInput.Text(
                        _kit.Lang, _settings.PcControls.Get(row.Action));
            }
        }

        /// <summary>_begin_capture(): the next supported event lands here.</summary>
        public void BeginCapture(string action)
        {
            _capturing = action;
            if (_controlReset != null)
            {
                _controlReset.interactable = false;
            }

            var definition = PcControlBindings.Find(action);
            SetControlStatus(
                string.Format(
                    _kit.Str("controls:status_waiting"),
                    definition == null ? action : _kit.Str(definition.LabelKey)));
            RefreshControls();
        }

        /// <summary>_finish_capture(): set_binding, then save at once.</summary>
        public void FinishCapture(string token)
        {
            if (_capturing == null)
            {
                return;
            }

            var action = _capturing;
            _capturing = null;
            var conflict = _settings.PcControls.SetBinding(action, token);
            SaveControls();

            if (conflict == null)
            {
                SetControlStatus(_kit.Str("controls:status_saved"));
            }
            else
            {
                var definition = PcControlBindings.Find(conflict);
                SetControlStatus(
                    string.Format(
                        _kit.Str("controls:status_moved"),
                        definition == null
                            ? conflict : _kit.Str(definition.LabelKey)));
            }

            EndCapture();
        }

        /// <summary>_cancel_capture(): Escape leaves the binding alone.</summary>
        public void CancelCapture()
        {
            if (_capturing == null)
            {
                return;
            }

            _capturing = null;
            SetControlStatus(_kit.Str("controls:status_cancelled"));
            EndCapture();
        }

        /// <summary>_clear_binding(): Backspace and Delete unbind the slot.</summary>
        public void ClearCapture()
        {
            if (_capturing == null)
            {
                return;
            }

            var action = _capturing;
            _capturing = null;
            _settings.PcControls.ClearBinding(action);
            SaveControls();
            SetControlStatus(_kit.Str("controls:status_cleared"));
            EndCapture();
        }

        private void EndCapture()
        {
            if (_controlReset != null)
            {
                _controlReset.interactable = true;
            }

            RefreshControls();
        }

        /// <summary>_confirm_reset(): Global.create_popup with an OK button.</summary>
        private void ConfirmResetControls()
        {
            GenericPopup.Create(
                Root, _kit.Atlas, _kit.Lang, _kit.Bold, _kit.Regular,
                "controls:reset_title", "controls:reset_body", false,
                ResetControls);
        }

        /// <summary>_reset_defaults().</summary>
        public void ResetControls()
        {
            _settings.PcControls.ResetToDefaults();
            SaveControls();
            SetControlStatus(_kit.Str("controls:status_reset"));
            RefreshControls();
        }

        private void SetControlStatus(string text)
        {
            if (_controlStatus != null)
            {
                _controlStatus.text = text;
            }
        }

        /// <summary>The source calls Global.save() after every change.</summary>
        private void SaveControls()
        {
            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap != null && bootstrap.Saves != null)
            {
                bootstrap.Saves.SaveSettings(_settings);
            }
        }

        private Row BuildRow(
            SettingRow source, RectTransform parent, bool isLeft, float y, float width)
        {
            var row = new Row { Source = source };
            row.Node = _kit.Node(source.Setting, parent, 0f, y, width, RowHeight);

            // The source hands each column half of the page width, so a row
            // owns half of the live canvas instead of half of the authored
            // 1386 px and its contents follow the edge they were pinned to.
            const float half = Separation * 0.5f;
            GodotRect.Apply(
                row.Node,
                isLeft ? 0f : 0.5f, 0f, isLeft ? 0.5f : 1f, 0f,
                isLeft ? 0f : half, y,
                isLeft ? -half : 0f, y + RowHeight,
                GodotRect.TopLeftPivot);

            var background = _kit.Picture(
                "NinePatchRect", row.Node, RowRegion,
                -RowBackgroundBleed, 0f, width + (RowBackgroundBleed * 2f), RowHeight);
            HangarUiKit.Stretch(
                background.rectTransform,
                -RowBackgroundBleed, 0f, RowBackgroundBleed, 0f);
            background.raycastTarget = false;

            row.Label = _kit.Label(
                "name", row.Node, LabelFontSize, TextAnchor.MiddleLeft,
                _kit.Str(source.LabelKey), _kit.Bold, Color.white,
                LabelX, (RowHeight - LabelHeight) * 0.5f, LabelWidth, LabelHeight);
            // A Godot Label draws its caption on one line past its rect, so
            // the port must not wrap the captured 79 px rect either.
            row.Label.horizontalOverflow = HorizontalWrapMode.Overflow;

            switch (source.Kind)
            {
                case "boolean":
                    BuildBooleanRow(row, width);
                    break;
                case "enum":
                    BuildEnumRow(row, width);
                    break;
                case "slider":
                    BuildSliderRow(row, width);
                    break;
                case "hangar":
                    BuildHangarRow(row);
                    break;
                case "hangar_music":
                    BuildHangarMusicRow(row);
                    break;
            }

            return row;
        }

        // ------------------------------------------------------------------
        // The hangar rows.
        // ------------------------------------------------------------------

        /// <summary>The hangar on screen for the current choice.</summary>
        public HangarKind CurrentHangar
        {
            get { return _hangarChoice.Current(_settings); }
        }

        /// <summary>
        /// One option-styled button against the right edge: the name of the hangar
        /// on screen and the dropdown arrow. It is as wide as the longest name, so
        /// the button does not resize when the choice changes.
        /// </summary>
        private void BuildHangarRow(Row row)
        {
            var height = RowHeight - OptionTopBottom();
            row.Picker = _kit.StyleButton("btn_hangar", row.Node, null, 0f, OptionsTop, 0f, height);
            row.Picker.GetComponent<Image>().color = OptionNormal;
            var button = (RectTransform)row.Picker.transform;

            row.Value = UiFactory.Label(
                "label", button, _kit.Regular, OptionFontSize, UiTheme.ButtonFont,
                TextAnchor.MiddleLeft, string.Empty);
            var widest = 0f;
            foreach (var info in HangarCatalog.All)
            {
                row.Value.text = _kit.Str(info.NameKey);
                widest = Mathf.Max(widest, Mathf.Ceil(row.Value.preferredWidth));
            }

            var width = Mathf.Max(
                HangarButtonMinWidth,
                widest + HangarArrowWidth + (HangarButtonPadding * 3f));
            GodotRect.Apply(
                button, 1f, 0f, 1f, 0f,
                -OptionsRight - width, OptionsTop, -OptionsRight, OptionsTop + height,
                GodotRect.TopLeftPivot);
            GodotRect.Apply(
                row.Value.rectTransform, 0f, 0f, 1f, 1f,
                HangarButtonPadding, 0f, -(HangarArrowWidth + (HangarButtonPadding * 2f)), 0f,
                GodotRect.TopLeftPivot);
            row.Value.horizontalOverflow = HorizontalWrapMode.Overflow;

            var arrow = UiFactory.Picture("arrow", button, _kit.Art("camo_dropdown_arrow"));
            GodotRect.Apply(
                arrow.rectTransform, 1f, 0.5f, 1f, 0.5f,
                -HangarButtonPadding - HangarArrowWidth, -HangarArrowHeight * 0.5f,
                -HangarButtonPadding, HangarArrowHeight * 0.5f,
                GodotRect.TopLeftPivot);
            arrow.raycastTarget = false;

            _kit.Bind(row.Picker, OpenHangarPicker, AudioService.SoundEnter);
            _hangarRows.Add(row);
            RefreshHangarRow(row);
        }

        /// <summary>The music player switch of the hangar on screen.</summary>
        private void BuildHangarMusicRow(Row row)
        {
            var drawn = CheckboxSize * CheckboxScale;
            var centreX = (CheckboxSize * 0.5f) - CheckboxRight;
            var centreY = (RowHeight * 0.5f) + CheckboxTop + (CheckboxSize * 0.5f);
            row.Checkbox = _kit.Picture(
                "checkbox", row.Node, UncheckedRegion,
                centreX - (drawn * 0.5f), centreY - (drawn * 0.5f), drawn, drawn);
            GodotRect.Apply(
                row.Checkbox.rectTransform, 1f, 0f, 1f, 0f,
                centreX - (drawn * 0.5f), centreY - (drawn * 0.5f),
                centreX + (drawn * 0.5f), centreY + (drawn * 0.5f),
                GodotRect.TopLeftPivot);
            row.Checkbox.raycastTarget = true;

            var button = row.Checkbox.gameObject.AddComponent<Button>();
            button.targetGraphic = row.Checkbox;
            _kit.Bind(button, () => ToggleHangarMusic(row), AudioService.SoundEnter);
            _hangarRows.Add(row);
            RefreshHangarRow(row);
        }

        /// <summary>Flips the music player of the hangar on screen.</summary>
        public void ToggleHangarMusic(Row row)
        {
            var kind = CurrentHangar;
            _settings.SetHangarMusicPlayer(kind, !_settings.HangarMusicPlayer(kind));
            RefreshHangarRow(row);
            RaiseChanged("hangar_music");
        }

        /// <summary>The hangar button's action: the list over the screen.</summary>
        public void OpenHangarPicker()
        {
            if (HangarPicker != null)
            {
                return;
            }

            HangarPicker = HangarPickerPopup.Create(
                Root, _kit, CurrentHangar, _hangarChoice.IsAvailable, OptionNormal, OptionPressed);
            HangarPicker.Picked = PickHangar;
            HangarPicker.Dismissed = () => HangarPicker = null;
        }

        /// <summary>
        /// Stores the choice. Picking the hangar already on screen while no
        /// choice is stored keeps it that way, so the schedule still decides.
        /// </summary>
        public void PickHangar(HangarInfo info)
        {
            if (info == null || !_hangarChoice.IsAvailable(info))
            {
                return;
            }

            if (string.IsNullOrEmpty(_settings.Hangar) && info.Kind == CurrentHangar)
            {
                return;
            }

            _settings.SetString("hangar", info.Id);
            for (var i = 0; i < _hangarRows.Count; i++)
            {
                RefreshHangarRow(_hangarRows[i]);
            }

            RaiseChanged("hangar");
        }

        private void RefreshHangarRow(Row row)
        {
            var kind = CurrentHangar;
            if (row.Picker != null)
            {
                row.Value.text = _kit.Str(HangarCatalog.Get(kind).NameKey);
            }

            if (row.Checkbox != null)
            {
                row.Checkbox.sprite = _kit.Art(
                    _settings.HangarMusicPlayer(kind) ? CheckedRegion : UncheckedRegion);
            }
        }

        private void RaiseChanged(string setting)
        {
            var changed = SettingChanged;
            if (changed != null)
            {
                changed(setting);
            }
        }

        private void BuildBooleanRow(Row row, float width)
        {
            // The 16x16 control is drawn at scale 3 around its own centre, so
            // the 48x48 glyph is centred on that point.
            var drawn = CheckboxSize * CheckboxScale;
            var centreX = (CheckboxSize * 0.5f) - CheckboxRight;
            var centreY = (RowHeight * 0.5f) + CheckboxTop + (CheckboxSize * 0.5f);
            row.Checkbox = _kit.Picture(
                "checkbox", row.Node, UncheckedRegion,
                centreX - (drawn * 0.5f), centreY - (drawn * 0.5f), drawn, drawn);
            GodotRect.Apply(
                row.Checkbox.rectTransform, 1f, 0f, 1f, 0f,
                centreX - (drawn * 0.5f), centreY - (drawn * 0.5f),
                centreX + (drawn * 0.5f), centreY + (drawn * 0.5f),
                GodotRect.TopLeftPivot);
            row.Checkbox.raycastTarget = true;

            var button = row.Checkbox.gameObject.AddComponent<Button>();
            button.targetGraphic = row.Checkbox;
            _kit.Bind(
                button,
                () =>
                {
                    _settings.SetBool(
                        row.Source.Setting, !_settings.GetBool(row.Source.Setting));
                    ApplyRuntimeSetting(row.Source.Setting);
                    RefreshBoolean(row);
                },
                AudioService.SoundEnter);

            RefreshBoolean(row);
        }

        /// <summary>
        /// The source applies a chosen limit straight away instead of waiting
        /// for the next launch (SaveSettings.apply_fps_limit). The graphics
        /// tab works the same way: the screen sits on top of the hangar, so a
        /// row that did not take effect until a restart would be impossible to
        /// judge against the very scene the player is looking at.
        /// </summary>
        private static void ApplyRuntimeSetting(string setting)
        {
            if (setting != null && setting.StartsWith("graphics_"))
            {
                GraphicsQuality.Apply();
                return;
            }

            if (setting == "ui_scale_battle")
            {
                BattleUiScale.Apply();
                MenuUiScale.Apply();
                return;
            }

            if (setting != "fps_limit")
            {
                return;
            }

            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap != null)
            {
                bootstrap.ApplyFpsLimit();
            }
        }

        private void RefreshBoolean(Row row)
        {
            var on = _settings.GetBool(row.Source.Setting);
            row.Checkbox.sprite = _kit.Art(on ? CheckedRegion : UncheckedRegion);
        }

        private void BuildEnumRow(Row row, float width)
        {
            var count = row.Source.Options.Count;
            var height = RowHeight - OptionTopBottom();
            var top = OptionsTop;
            // alignment = END: the buttons keep their minimum width and are
            // packed against the right edge of the box.
            var totalWidth = (count * OptionWidth) + ((count - 1) * Separation);
            var startX = -OptionsRight - totalWidth;

            for (var i = 0; i < count; i++)
            {
                var option = row.Source.Options[i];
                var button = _kit.StyleButton(
                    "btn" + i, row.Node, null,
                    startX + (i * (OptionWidth + Separation)), top,
                    OptionWidth, height);
                var buttonLeft = startX + (i * (OptionWidth + Separation));
                GodotRect.Apply(
                    (RectTransform)button.transform, 1f, 0f, 1f, 0f,
                    buttonLeft, top, buttonLeft + OptionWidth, top + height,
                    GodotRect.TopLeftPivot);

                var label = UiFactory.Label(
                    "label", (RectTransform)button.transform, _kit.Regular,
                    OptionFontSize, UiTheme.ButtonFont, TextAnchor.MiddleCenter,
                    _kit.Str(option.TextKey));
                GodotRect.FullRect(label.rectTransform);

                var value = option.Value;
                var text = option.Text;
                _kit.Bind(
                    button,
                    () =>
                    {
                        if (row.Source.IsString)
                        {
                            _settings.SetString(row.Source.Setting, text);
                        }
                        else
                        {
                            _settings.SetInt(row.Source.Setting, value);
                            ApplyRuntimeSetting(row.Source.Setting);
                        }

                        RefreshEnum(row);
                    },
                    AudioService.SoundEnter);

                row.Options.Add(button);
            }

            if (IsInterfaceRow(row))
            {
                FitOptions(row, top, height);
            }

            RefreshEnum(row);
        }

        /// <summary>The port's own interface rows (menu style, carousel size).</summary>
        private static bool IsInterfaceRow(Row row)
        {
            var setting = row.Source.Setting;
            return setting == "ui_style" || setting == "ui_carousel_size";
        }

        /// <summary>
        /// The interface rows carry longer captions than the source's short
        /// option words, so their buttons grow to the text instead of keeping
        /// the fixed source width. The source rows are left exactly as they are.
        /// </summary>
        private void FitOptions(Row row, float top, float height)
        {
            var widths = new float[row.Options.Count];
            var total = 0f;
            for (var i = 0; i < row.Options.Count; i++)
            {
                var label = row.Options[i].transform.Find("label");
                var text = label == null ? null : label.GetComponent<Text>();
                widths[i] = Mathf.Max(OptionWidth, text == null ? 0f : Mathf.Ceil(text.preferredWidth) + 18f);
                total += widths[i] + (i > 0 ? Separation : 0f);
            }

            var left = -OptionsRight - total;
            for (var i = 0; i < row.Options.Count; i++)
            {
                GodotRect.Apply(
                    (RectTransform)row.Options[i].transform, 1f, 0f, 1f, 0f,
                    left, top, left + widths[i], top + height, GodotRect.TopLeftPivot);
                left += widths[i] + Separation;
            }
        }

        private static float OptionTopBottom()
        {
            return OptionsTop + OptionsBottom;
        }

        private void RefreshEnum(Row row)
        {
            for (var i = 0; i < row.Options.Count; i++)
            {
                var option = row.Source.Options[i];
                var pressed = row.Source.IsString
                    ? _settings.GetString(row.Source.Setting) == option.Text
                    : _settings.GetInt(row.Source.Setting) == option.Value;
                var image = row.Options[i].GetComponent<Image>();
                image.color = pressed ? OptionPressed : OptionNormal;
            }
        }

        private void BuildSliderRow(Row row, float width)
        {
            // ui_settings.tscn parents `value` to `name` and pins it to the
            // label's right edge. The captured label rect is 79 px wide while
            // the real caption is longer, so following the rect alone dropped
            // the number in the middle of the caption. Follow the rendered
            // text and stop short of the slider frame.
            var caption = Mathf.Max(LabelWidth, row.Label.preferredWidth);
            var valueX = Mathf.Min(
                LabelX + caption + ValueGap,
                width - FrameRight - ValueWidth - ValueGap);
            row.Value = _kit.Label(
                "value", row.Node, ValueFontSize, TextAnchor.UpperLeft,
                string.Empty, _kit.Bold, ValueColor,
                valueX,
                (RowHeight * 0.5f) - (ValueHeight * 0.5f),
                ValueWidth, ValueHeight);

            var frame = _kit.Picture(
                "slider_bg", row.Node, FrameRegion,
                -FrameRight, (RowHeight * 0.5f) + FrameTop,
                FrameWidth, FrameHeight);
            GodotRect.Apply(
                frame.rectTransform, 1f, 0f, 1f, 0f,
                -FrameRight, (RowHeight * 0.5f) + FrameTop,
                FrameWidth - FrameRight,
                (RowHeight * 0.5f) + FrameTop + FrameHeight,
                GodotRect.TopLeftPivot);
            frame.rectTransform.localScale = new Vector3(FrameScale, FrameScale, 1f);
            frame.raycastTarget = false;

            var slider = _kit.Node(
                "slider", row.Node,
                -SliderRight, (RowHeight * 0.5f) + SliderTop,
                SliderWidth, SliderHeight);
            GodotRect.Apply(
                slider, 1f, 0f, 1f, 0f,
                -SliderRight, (RowHeight * 0.5f) + SliderTop,
                SliderWidth - SliderRight,
                (RowHeight * 0.5f) + SliderTop + SliderHeight,
                GodotRect.TopLeftPivot);
            slider.localScale = new Vector3(SliderScale, SliderScale, 1f);

            var barTop = GodotRangeMath.CenteredTop(SliderHeight, BarHeight);
            var bar = _kit.Picture(
                "bar", slider, null, 0f, barTop, SliderWidth, BarHeight);
            bar.color = BarColor;
            bar.raycastTarget = true;
            if (MenuStyle.InMenus)
            {
                // The modern track is a hairline; keep the classic bar's touch
                // height with an invisible hit area behind it.
                var hit = _kit.Picture(
                    "hit", slider, null, 0f, GodotRangeMath.CenteredTop(SliderHeight, ClassicBarHeight * 2f),
                    SliderWidth, ClassicBarHeight * 2f);
                hit.color = Color.clear;
                hit.raycastTarget = true;
                hit.transform.SetSiblingIndex(bar.transform.GetSiblingIndex());
            }

            row.Fill = _kit.Picture(
                "grabber_area", slider, null, 0f, barTop, 0f, BarHeight);
            row.Fill.color = FillColor;
            row.Fill.raycastTarget = false;

            row.Grabber = _kit.Picture(
                "grabber", slider, ThumbRegion, 0f, 0f, GrabberSize, GrabberSize);
            row.Grabber.raycastTarget = false;

            row.Slider = slider.gameObject.AddComponent<UiValueSlider>();
            row.Slider.Configure(
                row.Source.Min, row.Source.Max, row.Source.Step, GrabberSize,
                value =>
                {
                    _settings.SetFloat(row.Source.Setting, value);
                    ApplyRuntimeSetting(row.Source.Setting);
                    RefreshSlider(row);
                });

            RefreshSlider(row);
        }

        private void RefreshSlider(Row row)
        {
            var value = _settings.GetFloat(row.Source.Setting);
            var min = row.Source.Min;
            var max = row.Source.Max;

            row.Value.text = GodotRangeMath.Text(value);

            var fill = row.Fill.rectTransform;
            fill.sizeDelta = new Vector2(
                GodotRangeMath.FillWidth(SliderWidth, GrabberSize, value, min, max),
                BarHeight);

            var grabber = row.Grabber.rectTransform;
            grabber.anchoredPosition = new Vector2(
                GodotRangeMath.GrabberLeft(SliderWidth, GrabberSize, value, min, max),
                0f);
        }

        /// <summary>tab_container_custom.gd: the button row drives current_tab.</summary>
        public void SelectTab(int index)
        {
            if (index < 0 || index >= _pages.Count)
            {
                return;
            }

            CurrentTab = index;
            for (var i = 0; i < _pages.Count; i++)
            {
                _pages[i].gameObject.SetActive(i == index);
                _tabButtons[i].GetComponent<Image>().color =
                    i == index ? OptionPressed : OptionNormal;
            }
        }

        // ------------------------------------------------------------------
        // The row list, straight out of the manifest.
        // ------------------------------------------------------------------

        /// <summary>One add_boolean_option / add_enum_option / add_slider_option call.</summary>
        public sealed class SettingRow
        {
            public string Kind;
            public string LabelKey;
            public string Setting;
            public string Tab;
            public string Column;
            public float Min;
            public float Max;
            public float Step;
            public readonly List<SettingOption> Options = new List<SettingOption>();

            /// <summary>The language row is the only one holding a string.</summary>
            public bool IsString
            {
                get { return Setting == "language"; }
            }

            public static IList<SettingRow> Load()
            {
                var list = new List<SettingRow>();
                var text = BlitzResources.LoadText(
                    BlitzResources.SettingsManifestData);
                if (string.IsNullOrEmpty(text))
                {
                    return list;
                }

                var manifest = JsonNode.Parse(text);

                var rows = manifest["screen"]["rows"];
                for (var i = 0; i < rows.Count; i++)
                {
                    var node = rows[i];
                    var row = new SettingRow
                    {
                        Kind = node["kind"].AsString(),
                        LabelKey = node["labelKey"].AsString(),
                        Setting = node["setting"].AsString(),
                        Tab = node["tab"].AsString(),
                        Column = node["column"].AsString(),
                        Min = node["min"].AsFloat(),
                        Max = node["max"].AsFloat(),
                        Step = node["step"].AsFloat(),
                    };

                    var options = node["options"];
                    for (var o = 0; o < options.Count; o++)
                    {
                        var key = options[o]["textKey"].AsString();
                        // Every option but the locale carries a SaveSettings
                        // enum member; the LANGUAGE row carries "en" / "ru".
                        var value = options[o]["value"];
                        row.Options.Add(new SettingOption
                        {
                            TextKey = key,
                            Text = value.Kind == JsonKind.String
                                ? value.AsString()
                                : key,
                            Value = value.AsInt(),
                        });
                    }

                    list.Add(row);
                }

                return list;
            }
        }

        /// <summary>enum_option(name, option, value).</summary>
        public sealed class SettingOption
        {
            public string TextKey;

            /// <summary>The value a string setting stores, i.e. the locale.</summary>
            public string Text;

            public int Value;
        }
    }
}
