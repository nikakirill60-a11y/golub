using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Code-first port of the two loadout screens of the source hangar: the
    /// shell sliders of ui_hangar.tscn and
    /// ui_equipment_selection_screen.tscn / ui_equipment_list.gd.
    ///
    /// Both live on one root with a tab pair, because the source reaches them
    /// from the same hangar footer and they share the credits readout and the
    /// confirm popup.
    ///
    /// The shell row is the source HSlider: it can be dragged, it snaps to the
    /// burst step, it wears the SliderFrame / SliderThumb art and it ticks
    /// Audio.ui_drag on every notch, exactly like ui_shell_slider.tscn. The
    /// minus / plus buttons move the same value by one step and stay silent
    /// themselves, because the source marks them no_sound and lets the slider
    /// make the sound.
    /// </summary>
    public sealed class LoadoutScreen
    {
        public const int TabAmmo = 0;
        public const int TabEquipment = 1;

        /// <summary>btn_consumables, the third page of the source TabContainer.</summary>
        public const int TabConsumables = 2;

        // ------------------------------------------------------------------
        // Every rect below is the screen rect the Godot project produces for
        // ui_equipment_selection_screen.tscn / ui_shell_slider.tscn, captured
        // by rendering the source hangar head-less and dumping the global rect
        // of every Control (canvas 1386.67 x 733.33).
        // ------------------------------------------------------------------

        /// <summary>screens/tank_equipment/bg_main.</summary>
        private const float BackdropLeft = -5f;
        private const float BackdropTop = 90f;
        private const float BackdropWidth = 1396.67f;
        private const float BackdropHeight = 545.33f;

        /// <summary>The three header tabs, HBoxContainer at y = 92, h = 66.</summary>
        private const float TabTop = 92f;
        private const float TabHeight = 66f;
        private const float TabWidth = 459f;
        private const float TabSeparation = 4f;

        /// <summary>bg_desc, the item description column.</summary>
        private const float DescLeft = 971.67f;
        private const float DescTop = 166f;
        private const float DescWidth = 415f;
        private const float DescHeight = 456.33f;

        /// <summary>bg_slots and the three ui_selectable_list_entry slots.</summary>
        private const float SlotsWidth = 136f;
        private const float SlotsTop = 167f;
        private const float SlotsHeight = 455.33f;
        private const float SlotLeft = 4f;
        private const float SlotSize = 120f;
        private const float SlotStep = 162f;
        private const float SlotFirstTop = 172.67f;

        /// <summary>ScrollContainer/elements: a five column grid of 160 px entries.</summary>
        private const float GridLeft = 176f;
        private const float GridTop = 175f;
        private const float GridCell = 160f;
        private const float GridStep = 164f;
        private const int GridColumns = 5;

        /// <summary>
        /// The authored cell of ui_selectable_list_entry.tscn and the two node
        /// scales it applies to the KEEP_CENTERED artwork inside it.
        /// </summary>
        private const float EntryNativeCell = 160f;
        private const float EntryBgScale = 0.635f;
        private const float EntryIconScale = 0.66f;
        private const float EntrySelectedInset = 15f / 160f;
        private const float EntryBadgeSize = 42.24f / 160f;
        private const float EntryInstalledLeft = 115f / 160f;
        private const float EntryInstalledTop = 111f / 160f;
        private const float EntryCreditsLeft = 107f / 160f;
        private const float EntryCreditsTop = 143f / 160f;

        /// <summary>ui_shell_slider.tscn, one row per shell of the gun.</summary>
        private static readonly float[] ShellRowTops = { 200f, 336f, 480f };

        /// <summary>ui_shell_slider/slider, the HSlider grabber area.</summary>
        private const float SliderLeft = 372.33f;
        private const float SliderWidth = 649f;
        private const float SliderHeight = 66f;

        /// <summary>
        /// Godot draws the slider and grabber_area styleboxes at the stylebox
        /// minimum height (border_width_top + border_width_bottom = 24 in the
        /// row's own space, 12 px on screen) centred in the widget, which is
        /// what puts the coloured fill on the middle line of the row.
        /// </summary>
        private const float SliderBarHeight = 12f;

        /// <summary>SliderThumb.tres, 132 px drawn at the row's 0.5 scale.</summary>
        private const float SliderThumbSize = 66f;

        /// <summary>slider/amount, the count that follows the grabber.</summary>
        private const float AmountWidth = 44f;

        private static readonly Color PanelFill =
            new Color(0.06310915f, 0.10855677f, 0.13752648f, 0.658f);

        private static readonly Color PanelEdge =
            new Color(0.09803922f, 0.105882354f, 0.09803922f, 1f);

        /// <summary>ui_equipment_list.col_greenish, the credit-back colour.</summary>
        private static readonly Color Greenish = new Color32(0xA3, 0xE3, 0x9B, 0xFF);

        private static readonly Color BarTrack = new Color(0f, 0f, 0f, 0.45f);

        /// <summary>shells/col, the rack bar behind shells_colors.</summary>
        private static readonly Color RackFill = new Color(0f, 0f, 0f, 0.34901962f);

        /// <summary>shells_colors/shellN/col, one per shell type.</summary>
        private static readonly Color[] RackColors =
        {
            new Color(0.780462f, 0.44250005f, 0f, 1f),
            new Color(0.0924715f, 0.652484f, 0f, 1f),
            new Color(5.7756904e-07f, 0.61038584f, 0.6529039f, 1f),
        };

        /// <summary>l_price colour of ui_shell_slider.tscn.</summary>
        private static readonly Color PriceLabel =
            new Color(0.8235294f, 0.8235294f, 0.8235294f, 1f);

        /// <summary>The rack bar is 500 px wide at max_shells in the source.</summary>
        private const float RackWidth = 500f;

        /// <summary>bg_desc / bg_slots ColorRect colour.</summary>
        private static readonly Color DescFill = new Color(0f, 0f, 0f, 0.3882353f);

        /// <summary>current_name colour.</summary>
        private static readonly Color DescTitle =
            new Color(0.73333335f, 0.74509805f, 0.74509805f, 1f);

        /// <summary>current_desc colour.</summary>
        private static readonly Color DescBody =
            new Color(0.5805762f, 0.61521584f, 0.61518586f, 1f);

        /// <summary>l_action_cost colour.</summary>
        private static readonly Color ActionCostColor =
            new Color(0.85f, 0.2635f, 0.27327487f, 1f);

        private sealed class ShellRow
        {
            public RectTransform Root;
            public Image Icon;
            public Text Type;
            public Text Name;
            public Text Amount;
            public Text Penetration;
            public Text Damage;
            public Image Fill;
            public Image Thumb;
            public RectTransform Track;
            public UiDragSlider Slider;

            /// <summary>price_icon/l_price and price_icon/price_text.</summary>
            public Text PriceCaption;
            public Text PriceValue;
        }

        private sealed class EquipmentTile
        {
            public string Id;
            public Image Selected;
            public Image Installed;
            public Text Cost;
            public RectTransform Root;

            /// <summary>entry.enable(false): modulate.a = 0.5 on the entry.</summary>
            public CanvasGroup Group;
        }

        /// <summary>
        /// One instance of ui_equipment_selection_screen.tscn. The source
        /// scene is used twice inside the TabContainer, once with
        /// consumables = true and once with consumables = false, and
        /// ui_equipment_list.gd only swaps the table it reads. The port keeps
        /// that: one builder, one refresh, two pages.
        /// </summary>
        private sealed class ItemPage
        {
            public bool Consumables;
            public RectTransform Root;
            public readonly Image[] SlotIcons = new Image[LoadoutService.SlotCount];
            public readonly Image[] SlotSelected = new Image[LoadoutService.SlotCount];
            public readonly List<EquipmentTile> Tiles = new List<EquipmentTile>();
            public Image ItemIcon;
            public Text ItemName;
            public Text ItemDesc;
            public Text ActionText;
            public Text ActionCost;
            public Image ActionCurrency;
            public Button ActionButton;
            public Text ActionLabel;
            public Text Credits;
            public Text CrewValue;
            public int Slot = -1;
            public string Item = string.Empty;
        }

        private readonly HangarUiKit _kit;
        private readonly TanksDatabase _database;
        private readonly AccountData _account;
        private readonly LoadoutService _loadout;

        private readonly ShellRow[] _shellRows = new ShellRow[3];
        private readonly Image[] _shellColors = new Image[3];

        /// <summary>
        /// The sliders of the source screen are pending values: they only
        /// reach curtank.shells_amounts when btn_apply is pressed.
        /// </summary>
        private readonly List<int> _pending = new List<int> { 0, 0, 0 };

        private RectTransform _root;
        private RectTransform _ammoPage;
        private ItemPage _equipment;
        private ItemPage _consumables;
        private Button _ammoTab;
        private Button _equipmentTab;
        private Button _consumablesTab;
        private Text _title;
        private Text _shellTotal;
        private Button _applyButton;
        private Text _applyLabel;
        private CanvasGroup _applyGroup;

        private TankSaveData _save;
        private TankInfo _info;
        private GunInfo _gun;
        private int _tab = TabAmmo;
        private AudioSource _dragVoice;

        public LoadoutScreen(
            HangarUiKit kit, TanksDatabase database, AccountData account)
        {
            _kit = kit;
            _database = database;
            _account = account;
            _loadout = new LoadoutService(account, database);
        }

        /// <summary>btn_menu, back to the hangar.</summary>
        public Action HomePressed;

        /// <summary>Raised after the save changed, so the hangar can refresh.</summary>
        public Action Changed;

        /// <summary>Localized refusal text, e.g. popup:NotEnoughCredits.</summary>
        public Action<string> Message;

        public RectTransform Root
        {
            get { return _root; }
        }

        /// <summary>
        /// Where the source's z_index = 1 children are drawn. Godot lifts them
        /// over the root-level header plate; uGUI has no z_index, so the owner
        /// supplies a layer that sits after the HUD in the tree. Null keeps
        /// everything inside the screen, which is what isolated callers want.
        /// </summary>
        public RectTransform Overlay { get; set; }

        public int Tab
        {
            get { return _tab; }
        }

        public int SelectedSlot
        {
            get { return Page.Slot; }
        }

        public string SelectedItem
        {
            get { return Page.Item; }
        }

        public int TileCount
        {
            get { return Page.Tiles.Count; }
        }

        /// <summary>The list widget the open tab belongs to.</summary>
        private ItemPage Page
        {
            get { return _tab == TabConsumables ? _consumables : _equipment; }
        }

        public string CurrentTank
        {
            get { return _save == null ? string.Empty : _save.Name; }
        }

        // ------------------------------------------------------------------
        // Build.
        // ------------------------------------------------------------------

        public RectTransform Build(RectTransform parent)
        {
            _root = UiFactory.Node("tank_loadout", parent);
            GodotRect.FullRect(_root);

            var blur = _kit.Picture(
                "blur", _root, null, 0f, 0f,
                UiLayout.ReferenceWidth, UiLayout.ReferenceHeight);
            HangarUiKit.Stretch(blur.rectTransform, 0f, 6f, 0f, 6f);
            blur.color = HangarUiKit.ScreenTint;
            blur.raycastTarget = false;

            // btn_menu and tank_name are z_index = 1 in the source: they sit
            // over the root-level header plate, not under it.
            var top = Overlay ?? _root;
            var menu = _kit.StyleButton(
                "btn_menu", top, "button_small_generic", 5f, 4f, 94f, 77f);
            _kit.AddIcon(menu, "icon_back", 50f);
            _kit.Bind(menu, RaiseHome, AudioService.SoundEnter);

            var backdrop = _kit.Picture(
                "bg_main", _root, null,
                BackdropLeft, BackdropTop, BackdropWidth, BackdropHeight);
            HangarUiKit.Stretch(
                backdrop.rectTransform, BackdropLeft, BackdropTop,
                -BackdropLeft, BackdropTop + BackdropHeight - UiLayout.ReferenceHeight);
            backdrop.color = PanelFill;
            backdrop.raycastTarget = false;

            _title = _kit.Label(
                "tank_name", top, 30, TextAnchor.MiddleLeft, string.Empty,
                _kit.Bold, UiTheme.Label, 128f, 29f, 329f, 40f);

            // HBoxContainer/btn_consumables, btn_shells, btn_equipment.
            _consumablesTab = _kit.StyleButton(
                "btn_consumables", _root, "button_small_generic",
                0f, TabTop, TabWidth, TabHeight);
            TabAnchors(_consumablesTab, 0);
            AddTabLabel(_consumablesTab, _kit.Str("CONSUMABLES"), TabWidth);
            _kit.Bind(
                _consumablesTab, () => ShowTab(TabConsumables), AudioService.SoundEnter);

            _ammoTab = _kit.StyleButton(
                "btn_shells", _root, "button_small_generic",
                463f, TabTop, TabWidth, TabHeight);
            TabAnchors(_ammoTab, 1);
            AddTabLabel(_ammoTab, _kit.Str("SHELLS"), TabWidth);
            _kit.Bind(_ammoTab, () => ShowTab(TabAmmo), AudioService.SoundEnter);

            _equipmentTab = _kit.StyleButton(
                "btn_equipment", _root, "button_small_generic",
                926f, TabTop, 460f, TabHeight);
            TabAnchors(_equipmentTab, 2);
            AddTabLabel(_equipmentTab, _kit.Str("EQUIPMENT"), 460f);
            _kit.Bind(
                _equipmentTab, () => ShowTab(TabEquipment), AudioService.SoundEnter);

            BuildAmmoPage();
            _equipment = BuildItemPage("equipment", false);
            _consumables = BuildItemPage("consumables", true);
            ShowTab(TabAmmo);
            return _root;
        }

        private void AddTabLabel(Button button, string text, float width)
        {
            var label = _kit.Label(
                "label", (RectTransform)button.transform, 26, TextAnchor.MiddleCenter,
                text, _kit.Bold, UiTheme.ButtonFont, 0f, 0f, width, TabHeight);
            HangarUiKit.Stretch(label.rectTransform, 0f, 0f, 0f, 0f);
        }

        /// <summary>
        /// The HBoxContainer of the source screen fills the whole width and
        /// its three buttons carry size_flags_horizontal = 3, so each tab owns
        /// a third of the live canvas instead of the authored 459 px.
        /// </summary>
        private static void TabAnchors(Button button, int index)
        {
            const int count = 3;
            const float half = TabSeparation * 0.5f;
            GodotRect.Apply(
                (RectTransform)button.transform,
                index / (float)count, 0f, (index + 1) / (float)count, 0f,
                index == 0 ? 0f : half, TabTop,
                index == count - 1 ? 0f : -half, TabTop + TabHeight,
                GodotRect.TopLeftPivot);
        }

        /// <summary>
        /// A node the source pinned to the right edge, placed from the
        /// coordinates it has at the reference resolution.
        /// </summary>
        private static void RightAligned(
            RectTransform rt, float x, float y, float w, float h)
        {
            GodotRect.Apply(
                rt, 1f, 0f, 1f, 0f,
                x - UiLayout.ReferenceWidth, y,
                x + w - UiLayout.ReferenceWidth, y + h,
                GodotRect.TopLeftPivot);
        }

        /// <summary>
        /// bg_desc of ui_equipment_selection_screen.tscn: the description
        /// column and the buy bar under it, both owned by the equipment page.
        /// </summary>
        private void BuildPanel(ItemPage page)
        {
            var host = page.Root;
            var fill = _kit.Picture(
                "bg_desc", host, null, DescLeft, DescTop, DescWidth, DescHeight);
            HangarUiKit.RightStretch(
                fill.rectTransform, -DescWidth, DescTop, 0f,
                DescTop + DescHeight - UiLayout.ReferenceHeight);
            fill.color = DescFill;
            fill.raycastTarget = false;

            page.ItemIcon = _kit.Picture(
                "current_icon", host, null, 996.69f, 169.02f, 102.3f, 112.2f);
            RightAligned(page.ItemIcon.rectTransform, 996.69f, 169.02f, 102.3f, 112.2f);
            page.ItemIcon.preserveAspect = true;

            page.ItemName = _kit.Label(
                "current_name", host, 25, TextAnchor.UpperLeft, string.Empty,
                _kit.Bold, DescTitle, 1122.67f, 206f, 276f, 92f);
            RightAligned(page.ItemName.rectTransform, 1122.67f, 206f, 276f, 92f);
            page.ItemName.horizontalOverflow = HorizontalWrapMode.Wrap;

            page.ItemDesc = _kit.Label(
                "current_desc", host, 21, TextAnchor.UpperLeft, string.Empty,
                _kit.Regular, DescBody, 991.67f, 302f, 322f, 92f);
            page.ItemDesc.horizontalOverflow = HorizontalWrapMode.Wrap;
            RightAligned(page.ItemDesc.rectTransform, 991.67f, 302f, 322f, 92f);
            page.ItemDesc.verticalOverflow = VerticalWrapMode.Truncate;

            page.ActionText = _kit.Label(
                "l_action", host, 22, TextAnchor.MiddleRight, string.Empty,
                _kit.Regular, HangarUiKit.Caption, 755.67f, 655.33f, 143f, 23f);
            HangarUiKit.BottomRight(
                page.ActionText.rectTransform, -631f, -78f, -488f, -55f);

            page.ActionCost = _kit.Label(
                "l_action_cost", host, 33, TextAnchor.MiddleRight, string.Empty,
                _kit.Bold, ActionCostColor, 885.67f, 652.33f, 191f, 34f);
            HangarUiKit.BottomRight(
                page.ActionCost.rectTransform, -501f, -81f, -310f, -47f);

            page.ActionCurrency = _kit.Picture(
                "credits", host, "hangar_credits", 1087.67f, 647.33f, 41f, 42f);
            HangarUiKit.BottomRight(
                page.ActionCurrency.rectTransform, -299f, -86f, -258f, -44f);
            page.ActionCurrency.preserveAspect = true;

            page.ActionButton = _kit.StyleButton(
                "action_button", host, "button_generic",
                1141.67f, 639.33f, 222f, 76f);
            HangarUiKit.BottomRight(
                (RectTransform)page.ActionButton.transform, -245f, -94f, -23f, -18f);
            page.ActionLabel = _kit.Label(
                "label", (RectTransform)page.ActionButton.transform, 28,
                TextAnchor.MiddleCenter, _kit.Str("BUY"), _kit.Bold,
                UiTheme.ButtonFont, 0f, 0f, 222f, 76f);
            HangarUiKit.Stretch(page.ActionLabel.rectTransform, 0f, 0f, 0f, 0f);
            _kit.Bind(page.ActionButton, ActionPressed, AudioService.SoundEnter);

            page.Credits = _kit.Label(
                "credits_amount", host, 22, TextAnchor.MiddleRight, string.Empty,
                _kit.Bold, UiTheme.Label, 1087.67f, 600f, 276f, 30f);
            RightAligned(page.Credits.rectTransform, 1087.67f, 600f, 276f, 30f);

            // The crew readout of the source hangar is authored art with a
            // fixed 100 %, so the port shows the same value.
            page.CrewValue = _kit.Label(
                "crew_value", host, 22, TextAnchor.MiddleRight, "100%",
                _kit.Bold, UiTheme.Label, 1087.67f, 566f, 276f, 30f);
            RightAligned(page.CrewValue.rectTransform, 1087.67f, 566f, 276f, 30f);
        }

        // ------------------------------------------------------------------
        // Ammunition page: three ui_shell_slider rows.
        // ------------------------------------------------------------------

        private void BuildAmmoPage()
        {
            _ammoPage = _kit.Node(
                "shells", _root, 0f, 0f,
                UiLayout.ReferenceWidth, UiLayout.ReferenceHeight);
            HangarUiKit.Stretch(_ammoPage, 0f, 0f, 0f, 0f);

            for (var i = 0; i < _shellRows.Length; i++)
            {
                _shellRows[i] = BuildShellRow(i);
            }

            // total_shells: "hangar:shell_total" under the rows.
            _shellTotal = _kit.Label(
                "total_shells", _ammoPage, 25, TextAnchor.MiddleLeft, string.Empty,
                _kit.Bold, UiTheme.Label, 48f, 649.33f, 200f, 26f);
            HangarUiKit.BottomLeft(_shellTotal.rectTransform, 48f, -84f, 248f, -58f);

            // "col": the dark rack bar, with one coloured segment per shell.
            var rack = _kit.Picture("col", _ammoPage, null, 50f, 687.33f, 499f, 20f);
            HangarUiKit.BottomLeft(rack.rectTransform, 50f, -46f, 549f, -26f);
            rack.color = RackFill;
            rack.raycastTarget = false;

            var colors = _kit.Node(
                "shells_colors", _ammoPage, 50f, 687.33f, 572f, 20f);
            HangarUiKit.BottomLeft(colors, 50f, -46f, 622f, -26f);
            for (var i = 0; i < _shellColors.Length; i++)
            {
                var segment = _kit.Picture(
                    "shell" + (i + 1), colors, null, 0f, 0f, 0f, 20f);
                segment.raycastTarget = false;
                _shellColors[i] = segment;
            }

            // btn_apply: the source commits the rack in one press and the
            // button stays disabled while nothing changed.
            _applyButton = _kit.StyleButton(
                "btn_apply", _ammoPage, "button_green",
                1119.67f, 642.33f, 245f, 76f);
            HangarUiKit.BottomRight(
                (RectTransform)_applyButton.transform, -267f, -91f, -22f, -15f);
            _applyLabel = _kit.Label(
                "label", (RectTransform)_applyButton.transform, 25,
                TextAnchor.MiddleCenter, _kit.Str("APPLY"),
                _kit.Bold, UiTheme.Label, 0f, 0f, 245f, 76f);
            _applyLabel.raycastTarget = false;
            _applyGroup = _applyButton.gameObject.AddComponent<CanvasGroup>();
            _kit.Bind(_applyButton, ApplyShells, AudioService.SoundEnter);
        }

        /// <summary>One shell_slider_N instance of the source shells screen.</summary>
        private ShellRow BuildShellRow(int index)
        {
            var top = ShellRowTops[index];
            var slot = index;
            var result = new ShellRow();

            // One node per shell_slider instance, so a gun with fewer shells
            // hides the whole row instead of leaving an empty slider behind.
            var page = _kit.Node(
                "shell_slider_" + (index + 1), _ammoPage, 0f, 0f,
                UiLayout.ReferenceWidth, UiLayout.ReferenceHeight);
            HangarUiKit.CenterBand(page);
            result.Root = page;

            var sliderBg = _kit.Picture(
                "slider_bg", page, "slider_frame", 306.33f, top + 11f, 782f, 72f);
            if (sliderBg.sprite == null)
            {
                sliderBg.color = PanelFill;
            }

            sliderBg.raycastTarget = false;

            var frame = _kit.Picture(
                "shell_bg1", page, "shell_bg_none", 35.33f, top, 93f, 93f);
            result.Icon = _kit.Centered(
                "shell_ico", frame.rectTransform, null, 1.5f, 2f, 90f, 88f);
            result.Icon.preserveAspect = true;
            result.Type = _kit.Label(
                "type", frame.rectTransform, 20, TextAnchor.UpperLeft, string.Empty,
                _kit.Bold, UiTheme.Label, 5.5f, 5.5f, 44.5f, 30.5f);
            result.Amount = _kit.Label(
                "amount", page, 22, TextAnchor.MiddleCenter, "0",
                _kit.Bold, UiTheme.Label, SliderLeft, top - 22f, AmountWidth, 26f);

            result.Name = _kit.Label(
                "l_shell_name", page, 18, TextAnchor.MiddleLeft, string.Empty,
                _kit.Regular, UiTheme.Label, 152.33f, top + 6f, 145f, 19f);

            var dmgIcon = _kit.Picture(
                "dmg_range", page, "shell_param_damage",
                157.33f, top + 32f, 23f, 25f);
            dmgIcon.preserveAspect = true;
            result.Damage = _kit.Label(
                "l_dmg_range", page, 17, TextAnchor.MiddleLeft, string.Empty,
                _kit.Bold, UiTheme.Label, 186.33f, top + 36.5f, 79f, 18.5f);

            var penIcon = _kit.Picture(
                "pen_range", page, "shell_param_penetration",
                157.33f, top + 64f, 23f, 24f);
            penIcon.preserveAspect = true;
            result.Penetration = _kit.Label(
                "l_pen_range", page, 17, TextAnchor.MiddleLeft, string.Empty,
                _kit.Bold, UiTheme.Label, 186.33f, top + 68f, 79f, 18.5f);

            // The grabber area itself: invisible but pressable, because the
            // source HSlider draws nothing over the full widget height.
            var area = _kit.Picture(
                "slider", page, null,
                SliderLeft, top + 14f, SliderWidth, SliderHeight);
            area.color = new Color(0f, 0f, 0f, 0.001f);

            // UiFactory.Picture builds decoration, so it switches raycasting
            // off. This one rect is the grabber area itself and has to take
            // the press, which is why the slider did nothing before.
            area.raycastTarget = true;
            result.Track = area.rectTransform;

            var barTop = ShellSliderMath.CenteredTop(SliderHeight, SliderBarHeight);
            var bar = _kit.Picture(
                "bar", area.rectTransform, null,
                0f, barTop, SliderWidth, SliderBarHeight);
            bar.color = BarTrack;
            bar.raycastTarget = false;

            var barFill = _kit.Picture(
                "fill", area.rectTransform, null, 0f, barTop, 0f, SliderBarHeight);
            barFill.raycastTarget = false;
            result.Fill = barFill;

            result.Thumb = _kit.Picture(
                "grabber", area.rectTransform, "slider_thumb",
                ShellSliderMath.GrabberLeft(SliderWidth, SliderThumbSize, 0, 1),
                ShellSliderMath.CenteredTop(SliderHeight, SliderThumbSize),
                SliderThumbSize,
                SliderThumbSize);
            result.Thumb.preserveAspect = true;
            result.Thumb.raycastTarget = false;

            result.Slider = area.gameObject.AddComponent<UiDragSlider>();
            result.Slider.GrabberSize = SliderThumbSize;
            result.Slider.Configure(0, 1, value => SetShells(slot, value));

            // btn_sub / btn_add carry no_sound in the source: the value change
            // they cause is what plays Audio.ui_drag.
            var minus = _kit.StyleButton(
                "btn_sub", page, "button_small_generic",
                309.33f, top + 14f, 65f, 65f);
            _kit.AddIcon(minus, "slider_minus", 40f);
            minus.onClick.AddListener(() => StepShells(slot, -1));

            var plus = _kit.StyleButton(
                "btn_add", page, "button_small_generic",
                1019.33f, top + 14f, 65f, 65f);
            _kit.AddIcon(plus, "slider_plus", 40f);
            plus.onClick.AddListener(() => StepShells(slot, 1));

            var price = _kit.Picture(
                "price_icon", page, "hangar_credits",
                1269.33f, top + 48f, 27.06f, 27.72f);
            price.preserveAspect = true;

            // price_icon/l_price and price_icon/price_text: the per-row cost
            // of one full rack of this shell, both anchored on the icon.
            result.PriceCaption = _kit.Label(
                "l_price", page, 21, TextAnchor.MiddleLeft,
                _kit.Str("hangar:shell_cost"), _kit.Regular, PriceLabel,
                1180.61f, top + 17.3f, 182.82f, 24.42f);

            result.PriceValue = _kit.Label(
                "price_text", page, 24, TextAnchor.MiddleRight, string.Empty,
                _kit.Bold, UiTheme.Label,
                1156.61f, top + 50.3f, 104.28f, 24.42f);

            return result;
        }

        // ------------------------------------------------------------------
        // Equipment page: bg_slots, the five column grid and bg_desc.
        // ------------------------------------------------------------------

        private ItemPage BuildItemPage(string name, bool consumables)
        {
            var page = new ItemPage { Consumables = consumables };
            page.Root = _kit.Node(
                name, _root, 0f, 0f,
                UiLayout.ReferenceWidth, UiLayout.ReferenceHeight);
            HangarUiKit.Stretch(page.Root, 0f, 0f, 0f, 0f);

            var slotsBg = _kit.Picture(
                "bg_slots", page.Root, null,
                0f, SlotsTop, SlotsWidth, SlotsHeight);
            HangarUiKit.LeftStretch(
                slotsBg.rectTransform, 0f, SlotsTop, SlotsWidth,
                SlotsTop + SlotsHeight - UiLayout.ReferenceHeight);
            slotsBg.color = DescFill;
            slotsBg.raycastTarget = false;

            for (var i = 0; i < LoadoutService.SlotCount; i++)
            {
                var index = i;
                var slot = _kit.Node(
                    "slot" + (i + 1), page.Root,
                    SlotLeft, SlotFirstTop + (i * SlotStep), SlotSize, SlotSize);
                // slot = true in the source scene: the frame is
                // CheckBoxEquipPower and an empty slot draws no icon, no
                // price and no installed badge at all.
                BuildEntry(
                    slot, SlotSize, "equipment_entry_slot_bg", null,
                    out page.SlotIcons[i], out page.SlotSelected[i]);

                var button = _kit.StyleButton(
                    "button", slot, null, 0f, 0f, SlotSize, SlotSize);
                _kit.Bind(
                    button, () => ChooseSlot(page, index), AudioService.SoundSelect);
            }

            // The grid is built for every nation the catalogue allows and the
            // nation filter of update() only hides what the tank cannot take.
            if (consumables)
            {
                for (var i = 0; i < ConsumableCatalog.All.Length; i++)
                {
                    var item = ConsumableCatalog.All[i];
                    BuildItemTile(page, item.Id, item.IconRegion, i);
                }
            }
            else
            {
                for (var i = 0; i < EquipmentCatalog.All.Length; i++)
                {
                    var item = EquipmentCatalog.All[i];
                    BuildItemTile(
                        page, item.Id, HangarUiKit.EquipmentIconRegion(item.Id), i);
                }
            }

            BuildPanel(page);
            return page;
        }

        /// <summary>
        /// ui_selectable_list_entry.tscn: the frame, the artwork drawn at its
        /// authored size and the BoxEquipPower_02 selection border.
        /// </summary>
        private void BuildEntry(
            RectTransform entry,
            float size,
            string background,
            string iconRegion,
            out Image icon,
            out Image selected)
        {
            // Both TextureRects carry stretch_mode 3 (KEEP_CENTERED): the
            // artwork keeps its authored pixel size and is only multiplied by
            // the node scale, so a 132 x 170 icon never grows to the frame.
            var bg = _kit.Centered(
                "bg", entry, background, 0f, 0f, size, size,
                EntryBgScale * (size / EntryNativeCell));
            bg.raycastTarget = false;

            icon = _kit.Centered(
                "icon", entry, iconRegion, 0f, 0f, size, size,
                EntryIconScale * (size / EntryNativeCell));
            icon.raycastTarget = false;
            icon.enabled = icon.sprite != null;
            SizeCentered(icon, entry, size);

            selected = _kit.Picture(
                "selected", entry, "equipment_entry_selected",
                size * EntrySelectedInset, size * EntrySelectedInset,
                size * (1f - (2f * EntrySelectedInset)),
                size * (1f - (2f * EntrySelectedInset)));
            selected.type = Image.Type.Sliced;
            selected.raycastTarget = false;
            selected.gameObject.SetActive(false);
        }

        /// <summary>
        /// Re-applies the KEEP_CENTERED rect after the sprite changed: the
        /// artwork keeps its authored size times the entry scale and stays
        /// centred in the cell. A slot without an item draws nothing at all,
        /// exactly like init("", "", null) in ui_selectable_list_entry.gd.
        /// </summary>
        private static void SizeCentered(Image icon, RectTransform entry, float size)
        {
            if (icon == null) { return; }

            var sprite = icon.sprite;
            icon.enabled = sprite != null;
            if (sprite == null)
            {
                HangarUiKit.Place(icon.rectTransform, 0f, 0f, 0f, 0f);
                return;
            }

            var scale = EntryIconScale * (size / EntryNativeCell);
            var width = sprite.rect.width * scale;
            var height = sprite.rect.height * scale;
            HangarUiKit.Place(
                icon.rectTransform,
                (size - width) * 0.5f,
                (size - height) * 0.5f,
                width,
                height);
        }

        /// <summary>
        /// One entry of the five column grid. Every rect is the authored one:
        /// the price label hangs 73 px below the 160 px cell and is drawn
        /// top-aligned, which is what keeps it clear of the row underneath at
        /// the 164 px grid pitch.
        /// </summary>
        private void BuildItemTile(
            ItemPage page, string id, string iconRegion, int index)
        {
            var column = index % GridColumns;
            var line = index / GridColumns;
            var tile = _kit.Node(
                "item_" + id, page.Root,
                GridLeft + (column * GridStep), GridTop + (line * GridStep),
                GridCell, GridCell);

            var group = tile.gameObject.AddComponent<CanvasGroup>();

            Image icon;
            Image selected;
            BuildEntry(
                tile, GridCell, "equipment_entry_bg", iconRegion,
                out icon, out selected);

            var installed = _kit.Picture(
                "installed", tile, "equipment_entry_installed",
                GridCell * EntryInstalledLeft, GridCell * EntryInstalledTop,
                GridCell * EntryBadgeSize, GridCell * EntryBadgeSize);
            installed.raycastTarget = false;
            installed.gameObject.SetActive(false);

            var credits = _kit.Picture(
                "credits", tile, "hangar_credits",
                GridCell * EntryCreditsLeft, GridCell * EntryCreditsTop,
                GridCell * EntryBadgeSize, GridCell * EntryBadgeSize);
            credits.preserveAspect = true;
            credits.raycastTarget = false;

            // "cost": anchors_preset 5 with offsets -206/149/24/233 and the
            // Godot default vertical alignment, which is TOP.
            var cost = _kit.Label(
                "cost", tile, 31, TextAnchor.UpperRight, string.Empty,
                _kit.Bold, UiTheme.Label, -126f, 149f, 230f, 84f);

            var button = _kit.StyleButton(
                "button", tile, null, 0f, 0f, GridCell, GridCell);
            _kit.Bind(button, () => ChooseItem(page, id), AudioService.SoundSelect);

            page.Tiles.Add(new EquipmentTile
            {
                Id = id,
                Selected = selected,
                Installed = installed,
                Cost = cost,
                Group = group,
                Root = tile,
            });
        }

        // ------------------------------------------------------------------
        // init(SaveTank).
        // ------------------------------------------------------------------

        public void Init(string tank)
        {
            if (string.IsNullOrEmpty(tank))
            {
                return;
            }

            var save = _account != null && _account.HasTank(tank, true)
                ? _account.GetTank(tank)
                : TankSaveData.WithName(tank);
            Init(save);
        }

        public void Init(TankSaveData save)
        {
            if (save == null)
            {
                return;
            }

            var info = _database == null ? null : _database.Get(save.Name);
            if (info == null)
            {
                return;
            }

            _save = save;
            _info = info;
            LoadoutService.Fill(save);
            while (save.ShellsAmounts.Count < _shellRows.Length)
            {
                save.ShellsAmounts.Add(0);
            }

            _gun = Gun(info, save.TurretId, save.GunId);
            LoadoutService.NormalizeShells(_gun, save.ShellsAmounts);

            // update(curtank, ...): the sliders start on the saved rack and
            // hold the pending values until btn_apply is pressed.
            for (var i = 0; i < _pending.Count; i++)
            {
                _pending[i] = i < save.ShellsAmounts.Count ? save.ShellsAmounts[i] : 0;
            }

            // update(): both lists drop their selection and land on the
            // first free slot, so a single tap buys.
            ResetPage(_equipment);
            ResetPage(_consumables);
            _title.text = _kit.Lang.GetTankName(info);
            Refresh();

            Debug.Log(
                "[Blitz] loadout screen: tank=" + info.Name +
                " capacity=" + LoadoutService.Capacity(_gun) +
                " shells=" + LoadoutService.Total(_save.ShellsAmounts) +
                " equipment=" + _equipment.Tiles.Count +
                " consumables=" + _consumables.Tiles.Count);
        }

        /// <summary>
        /// The tail of update(): the selection is dropped and the first free
        /// slot is picked through select_slot_int, which is what moves the
        /// description card onto the next item that is not mounted yet.
        /// </summary>
        private void ResetPage(ItemPage page)
        {
            if (page == null)
            {
                return;
            }

            page.Item = string.Empty;
            page.Slot = -1;

            // last_free_slot defaults to 0 in the source.
            var free = 0;
            for (var i = 0; i < LoadoutService.SlotCount; i++)
            {
                if (string.IsNullOrEmpty(
                        LoadoutService.Installed(_save, i, page.Consumables)))
                {
                    free = i;
                    break;
                }
            }

            SelectSlotInt(page, free);
        }

        /// <summary>
        /// The barrel as the installed turret mounts it: the client authors the
        /// reload and the ammo capacity per turret, so the stock turret does
        /// not hold the rack of the top one.
        /// </summary>
        private static GunInfo Gun(TankInfo info, int turretId, int id)
        {
            if (info == null || info.Guns.Count == 0)
            {
                return null;
            }

            return info.GunFor(turretId, id);
        }

        public void ShowTab(int tab)
        {
            _tab = tab;
            if (_ammoPage != null)
            {
                _ammoPage.gameObject.SetActive(tab == TabAmmo);
            }

            if (_equipment != null)
            {
                _equipment.Root.gameObject.SetActive(tab == TabEquipment);
            }

            if (_consumables != null)
            {
                _consumables.Root.gameObject.SetActive(tab == TabConsumables);
            }

            // tab_container_custom.gd keeps the button of the open tab pressed.
            SetTabPressed(_ammoTab, tab == TabAmmo);
            SetTabPressed(_equipmentTab, tab == TabEquipment);
            SetTabPressed(_consumablesTab, tab == TabConsumables);

            Refresh();
        }

        private static void SetTabPressed(Button tab, bool pressed)
        {
            var state = GodotButtonState.Of(tab);
            if (state == null)
            {
                return;
            }

            state.ToggleMode = true;
            state.Latched = pressed;
        }

        // ------------------------------------------------------------------
        // Shell rows.
        // ------------------------------------------------------------------

        /// <summary>One slider notch: the burst step of the installed gun.</summary>
        public void StepShells(int slot, int direction)
        {
            if (_save == null || slot < 0 || slot >= _pending.Count)
            {
                return;
            }

            var step = LoadoutService.Step(_gun);
            SetShells(slot, _pending[slot] + (direction * step));
        }

        /// <summary>
        /// Range.set_value: the dragged or stepped amount, clamped against
        /// what is left of the rack. Only a real change ticks Audio.ui_drag,
        /// exactly like check_value_changed() comparing against last_value.
        /// </summary>
        public void SetShells(int slot, int amount)
        {
            if (_save == null || slot < 0 || slot >= _pending.Count)
            {
                return;
            }

            if (_gun == null || slot >= _gun.Shells.Count)
            {
                return;
            }

            var clamped = LoadoutService.ClampShellAmount(
                _gun, _pending, slot, amount);
            if (clamped == _pending[slot])
            {
                return;
            }

            // The slider only moves the pending rack; the save is written by
            // btn_apply, exactly like ui_shell_selection.gd.
            _pending[slot] = clamped;
            PlayDrag();
            Refresh();
        }

        /// <summary>
        /// btn_apply.pressed: the pending rack is written to the tank, the
        /// install sound plays and the hangar is refreshed.
        /// </summary>
        public void ApplyShells()
        {
            if (_save == null || !ShellsChanged())
            {
                return;
            }

            for (var i = 0; i < _save.ShellsAmounts.Count && i < _pending.Count; i++)
            {
                _save.ShellsAmounts[i] = _pending[i];
            }

            if (_kit != null && _kit.Audio != null)
            {
                _kit.Audio.StopSound2D(_dragVoice);
                _kit.Audio.PlaySound2D(
                    AudioService.SoundInstall, AudioBus.Ui, false, 1f);
            }

            Refresh();
            RaiseChanged();
        }

        /// <summary>btn_apply.enable(diff): any slider differs from the save.</summary>
        public bool ShellsChanged()
        {
            if (_save == null)
            {
                return false;
            }

            for (var i = 0; i < _pending.Count; i++)
            {
                var saved = i < _save.ShellsAmounts.Count ? _save.ShellsAmounts[i] : 0;
                if (saved != _pending[i])
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Audio.stop_sound2d(ui_drag) then play_sound2d(ui_drag).</summary>
        private void PlayDrag()
        {
            if (_kit == null || _kit.Audio == null)
            {
                return;
            }

            _kit.Audio.StopSound2D(_dragVoice);
            _dragVoice = _kit.Audio.PlaySound2D(
                AudioService.SoundDrag, AudioBus.Ui, false, AudioService.DragVolume);
        }

        /// <summary>The whole rack in one press, used by the max button and tests.</summary>
        public void FillShells(int slot)
        {
            if (_save == null || slot < 0 || slot >= _pending.Count)
            {
                return;
            }

            _pending[slot] = LoadoutService.ClampShellAmount(
                _gun, _pending, slot, LoadoutService.Capacity(_gun));
            Refresh();
        }

        // ------------------------------------------------------------------
        // Equipment selection and the four action states.
        // ------------------------------------------------------------------

        public void ChooseSlot(int slot)
        {
            ChooseSlot(Page, slot);
        }

        /// <summary>
        /// select_slot_int: a filled slot also selects the item it holds, and
        /// picking an empty slot while the selection is already mounted moves
        /// the selection to the first item that is not mounted yet.
        /// </summary>
        private void ChooseSlot(ItemPage page, int slot)
        {
            SelectSlotInt(page, slot);
            Refresh();
        }

        /// <summary>
        /// select_slot_int(id) itself, without the redraw. The source compares
        /// the selection against the raw slot ids, so an empty selection also
        /// counts as "installed" while any slot is free: that is what makes
        /// the description card jump to the next unmounted item right after a
        /// module was installed.
        /// </summary>
        private void SelectSlotInt(ItemPage page, int slot)
        {
            var installed = LoadoutService.Installed(_save, slot, page.Consumables);
            if (!string.IsNullOrEmpty(installed))
            {
                page.Item = installed;
            }

            var selectedIsInstalled = false;
            for (var i = 0; i < LoadoutService.SlotCount; i++)
            {
                var mounted = LoadoutService.Installed(_save, i, page.Consumables)
                    ?? string.Empty;
                if (string.Equals(
                        mounted, page.Item ?? string.Empty, StringComparison.Ordinal))
                {
                    selectedIsInstalled = true;
                }
            }

            if (string.IsNullOrEmpty(installed) && selectedIsInstalled)
            {
                for (var i = 0; i < page.Tiles.Count; i++)
                {
                    var tile = page.Tiles[i];
                    if (!ItemAllowed(page, tile.Id)) { continue; }
                    if (IsMounted(page, tile.Id)) { continue; }
                    page.Item = tile.Id;
                    break;
                }
            }

            page.Slot = slot;
        }

        public void ChooseItem(string id)
        {
            ChooseItem(Page, id);
        }

        /// <summary>select_item, which chains into select_slot.</summary>
        private void ChooseItem(ItemPage page, string id)
        {
            page.Item = id;

            // select_slot(id): a mounted item selects the slot it sits in.
            for (var i = 0; i < LoadoutService.SlotCount; i++)
            {
                if (string.Equals(
                        LoadoutService.Installed(_save, i, page.Consumables), id,
                        StringComparison.Ordinal))
                {
                    page.Slot = i;
                    Refresh();
                    return;
                }
            }

            if (page.Slot < 0)
            {
                // update() leaves the first free slot selected, so one tap buys.
                for (var i = 0; i < LoadoutService.SlotCount; i++)
                {
                    if (string.IsNullOrEmpty(
                            LoadoutService.Installed(_save, i, page.Consumables)))
                    {
                        page.Slot = i;
                        break;
                    }
                }
            }

            Refresh();
        }

        public EquipmentAction CurrentAction()
        {
            var page = Page;
            return _loadout.Action(_save, page.Slot, page.Item, page.Consumables);
        }

        /// <summary>ui_equipment_list.action_pressed.</summary>
        public void ActionPressed()
        {
            if (_save == null)
            {
                return;
            }

            var page = Page;
            var action = _loadout.Action(
                _save, page.Slot, page.Item, page.Consumables);
            var result = _loadout.Apply(
                _save, page.Slot, page.Item, page.Consumables);
            if (!result.Ok)
            {
                if (Message != null && !string.IsNullOrEmpty(result.ErrorKey))
                {
                    Message(_kit.Str(result.ErrorKey));
                }

                return;
            }

            // Audio.play_sound2d(Audio.ui_sell) inside the sell branch, and
            // Audio.ui_install for the two branches that mount an item.
            if (_kit != null && _kit.Audio != null)
            {
                _kit.Audio.PlaySound2D(
                    action == EquipmentAction.Sell
                        ? AudioService.SoundSell
                        : AudioService.SoundInstall,
                    AudioBus.Ui, false, 1f);
            }

            // action_pressed() finishes with update(curtank), so the slots,
            // the grid and the description card are all rebuilt: the card
            // lands on the next item that is not mounted yet.
            ResetPage(page);
            Refresh();
            RaiseChanged();
        }

        private bool IsMounted(ItemPage page, string id)
        {
            if (string.IsNullOrEmpty(id) || _save == null) { return false; }
            return LoadoutService.Mounted(_save, page.Consumables).Contains(id);
        }

        // ------------------------------------------------------------------
        // Redraw.
        // ------------------------------------------------------------------

        public void Refresh()
        {
            RefreshShells();
            RefreshPage(_equipment);
            RefreshPage(_consumables);
        }

        /// <summary>One UIEquipmentList.update() pass.</summary>
        private void RefreshPage(ItemPage page)
        {
            if (page == null)
            {
                return;
            }

            if (page.Credits != null)
            {
                page.Credits.text = _account == null
                    ? string.Empty
                    : HangarUiKit.FormatNumber(_account.Credits);
            }

            RefreshItems(page);
            RefreshAction(page);
        }

        /// <summary>equipment_&lt;id&gt; or the consumable artwork of the catalogue.</summary>
        private string ItemIconRegion(ItemPage page, string id)
        {
            if (!page.Consumables)
            {
                return HangarUiKit.EquipmentIconRegion(id);
            }

            var item = ConsumableCatalog.Get(id);
            return item == null ? "equipment_empty_slot" : item.IconRegion;
        }

        /// <summary>
        /// eq.can_be_used_on_nation(tinfo.nation): the source never creates
        /// the entry at all, so the grid closes the gap.
        /// </summary>
        private bool ItemAllowed(ItemPage page, string id)
        {
            if (!page.Consumables)
            {
                return true;
            }

            var item = ConsumableCatalog.Get(id);
            if (item == null)
            {
                return false;
            }

            return item.CanBeUsedOn(_info == null ? Nation.None : _info.Nation);
        }

        private void RefreshShells()
        {
            var capacity = LoadoutService.Capacity(_gun);

            // update_total_shells(): the readout, the rack bar and btn_apply
            // all follow the pending slider values, not the saved rack.
            var total = LoadoutService.Total(_pending);

            if (_shellTotal != null)
            {
                _shellTotal.text = Format(
                    _kit.Str("hangar:shell_total"), total, capacity);
            }

            var changed = ShellsChanged();
            if (_applyButton != null)
            {
                _applyButton.interactable = changed;
            }

            if (_applyGroup != null)
            {
                _applyGroup.alpha = changed ? 1f : 0.5f;
            }

            for (var i = 0; i < _shellColors.Length; i++)
            {
                var segment = _shellColors[i];
                if (segment == null)
                {
                    continue;
                }

                var shellOfSegment = _gun != null && i < _gun.Shells.Count
                    ? _gun.Shells[i]
                    : null;
                var share = capacity <= 0
                    ? 0f
                    : _pending[i] * (RackWidth / capacity);
                segment.color = RackColors[i];
                segment.gameObject.SetActive(shellOfSegment != null && share > 0f);
                HangarUiKit.Place(
                    segment.rectTransform, RackOffset(capacity, i), 0f, share, 20f);
            }

            for (var i = 0; i < _shellRows.Length; i++)
            {
                var row = _shellRows[i];
                var shell = _gun != null && i < _gun.Shells.Count ? _gun.Shells[i] : null;
                if (row.Root != null)
                {
                    row.Root.gameObject.SetActive(shell != null);
                }

                if (shell == null)
                {
                    continue;
                }

                var amount = i < _pending.Count ? _pending[i] : 0;

                // price_text: Util.format_number(shell.cost).
                if (row.PriceValue != null)
                {
                    row.PriceValue.text = HangarUiKit.FormatNumber(
                        Mathf.RoundToInt(shell.Cost));
                }

                row.Icon.sprite = _kit.Art(
                    HangarUiKit.ShellIconRegion(shell.Type, shell.Premium, amount <= 0));
                row.Type.text = _kit.Str(HangarUiKit.ShellAbbreviationKey(shell.Type));
                row.Name.text = shell.Name;
                row.Penetration.text = Mathf.RoundToInt(shell.Penetration)
                    .ToString(CultureInfo.InvariantCulture);
                row.Damage.text = Mathf.RoundToInt(shell.Damage)
                    .ToString(CultureInfo.InvariantCulture);
                row.Amount.text = amount.ToString(CultureInfo.InvariantCulture);

                if (row.Slider != null)
                {
                    // slider.max_value is the rack capacity and never moves;
                    // the source keeps the room left in the rack in a separate
                    // max_capacity and only clamps the value with it, which
                    // SetShells does through LoadoutService.ClampShellAmount.
                    // Folding the two together shrank the pointer scale to the
                    // free room while the fill kept using the capacity, so on a
                    // full rack every touch dropped the row to a smaller count.
                    row.Slider.MaxValue = capacity;
                    row.Slider.Step = LoadoutService.Step(_gun);
                }

                var barTop = ShellSliderMath.CenteredTop(SliderHeight, SliderBarHeight);
                var width = ShellSliderMath.FillWidth(
                    SliderWidth, SliderThumbSize, amount, capacity);
                HangarUiKit.Place(
                    row.Fill.rectTransform, 0f, barTop, width, SliderBarHeight);
                row.Fill.color = HangarUiKit.ShellColor(shell.Type);

                if (row.Thumb != null)
                {
                    HangarUiKit.Place(
                        row.Thumb.rectTransform,
                        ShellSliderMath.GrabberLeft(
                            SliderWidth, SliderThumbSize, amount, capacity),
                        ShellSliderMath.CenteredTop(SliderHeight, SliderThumbSize),
                        SliderThumbSize,
                        SliderThumbSize);
                }

                // slider/amount rides the grabber and hides at zero.
                row.Amount.gameObject.SetActive(amount > 0);
                HangarUiKit.Place(
                    row.Amount.rectTransform,
                    SliderLeft + width - (AmountWidth * 0.5f),
                    ShellRowTops[i] - 22f,
                    AmountWidth,
                    26f);
            }
        }

        /// <summary>
        /// shells_colors is an HBoxContainer, so a segment starts where the
        /// segments before it end.
        /// </summary>
        private float RackOffset(int capacity, int index)
        {
            if (capacity <= 0)
            {
                return 0f;
            }

            var offset = 0f;
            for (var i = 0; i < index && i < _pending.Count; i++)
            {
                offset += _pending[i] * (RackWidth / capacity);
            }

            return offset;
        }

        private void RefreshItems(ItemPage page)
        {
            var taken = 0;
            for (var i = 0; i < page.SlotIcons.Length; i++)
            {
                var id = LoadoutService.Installed(_save, i, page.Consumables);
                if (!string.IsNullOrEmpty(id)) { taken++; }

                // init("", "", null): an empty slot keeps its frame and
                // draws nothing inside it.
                page.SlotIcons[i].sprite = string.IsNullOrEmpty(id)
                    ? null
                    : _kit.Art(ItemIconRegion(page, id));
                SizeCentered(
                    page.SlotIcons[i],
                    (RectTransform)page.SlotIcons[i].rectTransform.parent,
                    SlotSize);
                page.SlotSelected[i].gameObject.SetActive(i == page.Slot);
            }

            // entry.enable(false) when every slot is busy and the entry is
            // not one of the mounted items.
            var full = taken >= LoadoutService.SlotCount;

            var visible = 0;
            for (var i = 0; i < page.Tiles.Count; i++)
            {
                var tile = page.Tiles[i];
                var allowed = ItemAllowed(page, tile.Id);
                tile.Root.gameObject.SetActive(allowed);
                if (!allowed)
                {
                    continue;
                }

                // The nation filter removes entries in the source, so the
                // remaining ones close ranks on the five column grid.
                HangarUiKit.Place(
                    tile.Root,
                    GridLeft + ((visible % GridColumns) * GridStep),
                    GridTop + ((visible / GridColumns) * GridStep),
                    GridCell,
                    GridCell);
                visible++;

                tile.Selected.gameObject.SetActive(
                    string.Equals(tile.Id, page.Item, StringComparison.Ordinal));

                var owned = IsMounted(page, tile.Id);
                if (tile.Installed != null)
                {
                    tile.Installed.gameObject.SetActive(owned);
                }

                // The source keeps the price under an installed entry too.
                if (tile.Cost != null)
                {
                    tile.Cost.text = _save == null
                        ? string.Empty
                        : HangarUiKit.FormatNumber(
                            _loadout.ItemCost(_save.Name, tile.Id, page.Consumables));
                }

                if (tile.Group != null)
                {
                    tile.Group.alpha = !owned && full ? 0.5f : 1f;
                }
            }

            string nameKey = null;
            string descKey = null;
            string icon = null;
            if (!string.IsNullOrEmpty(page.Item))
            {
                if (page.Consumables)
                {
                    var item = ConsumableCatalog.Get(page.Item);
                    if (item != null)
                    {
                        nameKey = item.NameKey;
                        descKey = item.DescriptionKey;
                        icon = item.IconRegion;
                    }
                }
                else
                {
                    var item = EquipmentCatalog.Get(page.Item);
                    if (item != null)
                    {
                        nameKey = item.NameKey;
                        descKey = item.DescriptionKey;
                        icon = HangarUiKit.EquipmentIconRegion(item.Id);
                    }
                }
            }

            page.ItemIcon.gameObject.SetActive(nameKey != null);
            if (nameKey != null)
            {
                page.ItemIcon.sprite = _kit.Art(icon);
                page.ItemName.text = _kit.Str(nameKey);
                page.ItemDesc.text = _kit.Str(descKey);
            }
            else
            {
                page.ItemName.text = string.Empty;
                page.ItemDesc.text = string.Empty;
            }

            if (page.CrewValue != null)
            {
                page.CrewValue.text = "100%";
            }
        }

        /// <summary>update_action_info() of ui_equipment_list.gd.</summary>
        private void RefreshAction(ItemPage page)
        {
            var action = _loadout.Action(
                _save, page.Slot, page.Item, page.Consumables);
            var show = action != EquipmentAction.None;

            page.ActionButton.gameObject.SetActive(show);
            page.ActionText.gameObject.SetActive(show);
            page.ActionCost.gameObject.SetActive(show);
            page.ActionCurrency.gameObject.SetActive(show);
            if (!show)
            {
                return;
            }

            // get_current_item_cost() is what the account pays; the source
            // prints the credit-back as diff = 0.5 * cost_slot - cost_selected
            // and only turns it green when that difference is positive.
            var cost = _loadout.ActionCost(
                _save, page.Slot, page.Item, page.Consumables);
            var back = cost < 0;
            page.ActionText.text =
                _kit.Str(back ? "hangar:return_cost" : "hangar:cost");
            page.ActionCost.color = back ? Greenish : UiTheme.Label;
            page.ActionCost.text = (back ? string.Empty : "-") +
                HangarUiKit.FormatNumber(Mathf.Abs(cost));

            var style = "button_generic";
            switch (action)
            {
                case EquipmentAction.Buy:
                    page.ActionLabel.text = _kit.Str("BUY");
                    style = "button_green";
                    break;
                case EquipmentAction.Sell:
                    page.ActionLabel.text = _kit.Str("SELL");
                    style = "hangar_red_button";
                    break;
                default:
                    page.ActionLabel.text = _kit.Str("SWAP");
                    break;
            }

            if (page.ActionButton.image != null)
            {
                var sprite = _kit.Art(style);
                page.ActionButton.image.sprite = sprite;
                page.ActionButton.image.type =
                    sprite != null && sprite.border.sqrMagnitude > 0f
                        ? Image.Type.Sliced
                        : Image.Type.Simple;
            }
        }

        /// <summary>
        /// hangar:shell_total is a Godot format string ("%d/%d"), so the port
        /// substitutes the same two numbers rather than reformatting the text.
        /// </summary>
        private static string Format(string pattern, int first, int second)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return first + "/" + second;
            }

            var index = pattern.IndexOf("%d", StringComparison.Ordinal);
            if (index < 0)
            {
                return pattern + " " + first + "/" + second;
            }

            var once = pattern.Substring(0, index)
                + first.ToString(CultureInfo.InvariantCulture)
                + pattern.Substring(index + 2);
            index = once.IndexOf("%d", StringComparison.Ordinal);
            if (index < 0)
            {
                return once;
            }

            return once.Substring(0, index)
                + second.ToString(CultureInfo.InvariantCulture)
                + once.Substring(index + 2);
        }

        private void RaiseHome()
        {
            if (HomePressed != null)
            {
                HomePressed();
            }
        }

        private void RaiseChanged()
        {
            if (Changed != null)
            {
                Changed();
            }
        }
    }
}
