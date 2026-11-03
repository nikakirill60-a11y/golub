using System;
using System.Collections;
using System.Collections.Generic;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Tanks;
using BlitzLegacy.Core.Mods;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Mods;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Hangar.Halloween;
using BlitzLegacy.Runtime.Platform;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Code-first port of ui_hangar.tscn's visible main screen.
    ///
    /// Every rect in this file is the screen-space rect the Godot project
    /// actually produces, captured by rendering res://assets/gfx/ui/hangar/
    /// ui_hangar.tscn head-less and dumping every Control's global rect,
    /// theme font size and icon_max_width. The canvas is Godot's logical
    /// viewport (<see cref="UiLayout"/>), so the numbers transfer 1:1.
    ///
    /// Gameplay destinations remain explicit placeholders until their roadmap
    /// stages; the source screen has no platoon button, so neither does this.
    /// </summary>
    public sealed partial class HangarScreen : MonoBehaviour
    {
        private const float CanvasWidth = UiLayout.ReferenceWidth;
        private const float CanvasHeight = UiLayout.ReferenceHeight;

        /// <summary>Godot footer_lol: y = 593.333, height = 140.</summary>
        private const float FooterTop = 593.3333f;
        private const float FooterHeight = 140f;

        /// <summary>carousel_tank.tscn custom_minimum_size.</summary>
        private const float CardWidth = 196f;

        /// <summary>The top of the classic music panel: clear of the header button row, which ends at y 173.</summary>
        private const float ClassicMusicPanelTop = 196f;

        /// <summary>The top of btn_notifs, the control under the classic music panel; see <see cref="BuildNews"/>.</summary>
        private const float ClassicNotifsTop = 505.3333f;
        private const float CardHeight = 130f;

        /// <summary>Godot's default BoxContainer separation.</summary>
        private const float Separation = 4f;

        /// <summary>
        /// btn_filter: snd_vol = 0.33 on top of ButtonCustom's 0.7, for the
        /// pressed and the toggled sound alike.
        /// </summary>
        private const float FilterSoundVolume = 0.33f;

        /// <summary>
        /// ui_hangar.tscn filter/tank_equipment: the loadout strip sits on top
        /// of the footer, from y = 527.33 to the carousel edge.
        /// </summary>
        private const float StripTop = 527.33f;
        private const float StripHeight = 66f;
        private const int StripSlots = 3;

        /// <summary>tank_equipment.scale.</summary>
        private const float ShellRowScale = 0.33f;

        /// <summary>tank_equipment.scale * consumables_box.scale (0.9).</summary>
        private const float EquipRowScale = 0.297f;

        /// <summary>shell_icon.tscn custom_minimum_size and the 7 px spacer.</summary>
        private const float ShellSlotNativeStep = 201f;

        /// <summary>equipment_icon.tscn frame size and the 7 px spacer.</summary>
        private const float EquipSlotNativeSize = 206f;
        private const float EquipSlotNativeStep = 221f;

        /// <summary>consumables_box: x and width, the left edge of the strip's panels.</summary>
        private const float ConsumablesBoxX = 344.41f;
        private const float ConsumablesBoxWidth = 192.46f;

        /// <summary>The gap between consumables_box and shells_box.</summary>
        private const float StripPanelGap = 559.67f - (ConsumablesBoxX + ConsumablesBoxWidth);

        /// <summary>Hangar.yaml TankCamouflages, sized like one equipment slot of the strip.</summary>
        private const float CamouflageSlotSize = EquipSlotNativeSize * EquipRowScale;
        private const float CamouflageSlotX = ConsumablesBoxX - StripPanelGap - CamouflageSlotSize;
        private const float CamouflageSlotY = 1.02f;

        /// <summary>The 4.8.0 units of TankCamouflages: InventorySlot, SlotLabel and badge.</summary>
        private const float CamouflageSourceSlot = 50f;
        private const float CamouflageCaptionWidth = 150f;
        private const float CamouflageCaptionHeight = 20f;
        private const float CamouflageCaptionTop = -24f;
        private const float CamouflageCaptionSize = 14f;
        private const float CamouflageBadgeX = 36.85714f;
        private const float CamouflageBadgeY = 2.428571f;
        private const float CamouflageBadgeSize = 16f;

        private static readonly Color CamouflageBadgeColor = new Color(1f, 0f, 0.015686f, 1f);

        private static readonly Color CaptionColor =
            new Color(0.5662578f, 0.61404586f, 0.65724987f, 1f);

        private static readonly Color PremiumColor =
            new Color(0.702118f, 0.601171f, 0.162514f, 1f);

        private BlitzBootstrap _bootstrap;
        private RectTransform _uiRoot;
        private AccountData _account;
        private AtlasRegionTable _atlas;
        private LocalizationTable _lang;
        private Font _regular;
        private Font _bold;

        private readonly BattleShellSelector.ShellIcon[] _stripShells =
            new BattleShellSelector.ShellIcon[StripSlots];
        private readonly EquipmentSlotView[] _stripConsumables =
            new EquipmentSlotView[StripSlots];
        private readonly EquipmentSlotView[] _stripEquipment =
            new EquipmentSlotView[StripSlots];
        private Image _stripCamouflageFrame;
        private Image _stripCamouflageIcon;
        private Image _stripCamouflageBadge;
        private Text _stripCamouflageCaption;
        private HangarMenuPanel _menu;
        /// <summary>The Modern filter pop-up (null in Classic).</summary>
        private RectTransform _filterPanel;
        /// <summary>screens/main/filter_handle (null in Modern).</summary>
        private HangarFilterDrawer _filterDrawer;
        private Image _filterIcon;
        private RectTransform _placeholder;
        private Text _placeholderTitle;
        private Text _placeholderHint;
        private Text _filterState;
        private GodotButtonState _menuToggle;
        private GodotButtonState _filterToggle;
        private Text _premiumLabel;
        private Coroutine _placeholderRoutine;
        private RectTransform _main;
        private RectTransform _screens;

        /// <summary>
        /// The root-level band Godot draws over every screen: the header plate
        /// and the currency readout. Kept as a field so a future screen can
        /// hide it the way to_screen() does for battle_result and friends.
        /// </summary>
        private RectTransform _hud;

        /// <summary>
        /// The source's z_index = 1 nodes. Godot's z_index lifts a control
        /// above every z_index 0 node in the same canvas layer, including the
        /// root-level <see cref="_hud"/> band listed after it, and uGUI has no
        /// equivalent: draw order there is sibling order. Every screen that
        /// lifts a widget this way gets a child layer here, built after the
        /// HUD and shown and hidden with the screen it belongs to.
        /// </summary>
        private RectTransform _zTop;

        private readonly Dictionary<HangarScreenId, RectTransform> _zLayers =
            new Dictionary<HangarScreenId, RectTransform>();
        private TanksDatabase _tanks;
        private TankResearchService _research;
        private HangarUiKit _kit;
        private TechTreeScreen _techTree;
        private ModulesScreen _modules;
        private LoadoutScreen _loadout;
        private ShopScreen _shop;
        private ArmorViewerScreen _armorViewer;
        private CamouflageScreen _camouflage;
        private CamouflageService _camouflageService;
        private HangarScreenId _screen = HangarScreenId.Main;

        /// <summary>The hangar and the two overlays ported from screens/.</summary>
        public enum HangarScreenId
        {
            Main,
            TechTree,
            Modules,
            Loadout,
            Shop,
            ArmorViewer,
            Camouflage,
        }

        public HangarScreenId CurrentScreen
        {
            get { return _screen; }
        }

        public TechTreeScreen TechTree
        {
            get { return _techTree; }
        }

        public ModulesScreen Modules
        {
            get { return _modules; }
        }

        public LoadoutScreen Loadout
        {
            get { return _loadout; }
        }

        public ShopScreen Shop
        {
            get { return _shop; }
        }

        public ArmorViewerScreen ArmorViewer
        {
            get { return _armorViewer; }
        }

        /// <summary>The 4.8 camouflage screen of the selected vehicle.</summary>
        public CamouflageScreen Camouflage
        {
            get { return _camouflage; }
        }

        public HangarWorld World { get; private set; }

        /// <summary>
        /// Whether the hangar on screen plays the hangar music player: on out
        /// of the box where the catalog says so (the Miku hangar), off
        /// elsewhere, switched per hangar in the settings.
        /// </summary>
        public bool MusicPlayerOn
        {
            get { return World != null && _bootstrap.Settings.HangarMusicPlayer(World.Kind); }
        }

        /// <summary>The music player's panel in the main view, or null without a player.</summary>
        public HangarMusicPanel MusicPanel { get; private set; }
        public HangarDragSurface DragSurface { get; private set; }
        public RectTransform CarouselRoot { get; private set; }
        public int PlaceholderButtonCount { get; private set; }

        /// <summary>The LOGOUT confirmation while it is up, or null.</summary>
        public GenericPopup LogoutPopup { get; private set; }

        /// <summary>True while the carousel filter is up, i.e. btn_filter is pressed.</summary>
        public bool IsFilterOpen
        {
            get
            {
                return _modern
                    ? _modernFilterOpen
                    : _filterDrawer != null && _filterDrawer.IsOpen;
            }
        }

        /// <summary>The classic filter band (null in Modern).</summary>
        public HangarFilterDrawer FilterDrawer
        {
            get { return _filterDrawer; }
        }

        /// <summary>ui_settings.tscn, while it is up over the hangar.</summary>
        public SettingsScreen SettingsUi { get; private set; }

        /// <summary>The mod installer while it is up, or null.</summary>
        public ModsScreen ModsUi { get; private set; }

        /// <summary>Above the hangar canvas, below the loading overlay.</summary>
        public const int SettingsSortingOrder = 300;
        public int CarouselCardCount { get; private set; }
        public HangarAccountToolbox Toolbox { get; private set; }

        public static HangarScreen Create(BlitzBootstrap bootstrap, AccountData account)
        {
            if (bootstrap == null)
            {
                throw new ArgumentNullException("bootstrap");
            }

            if (account == null)
            {
                throw new ArgumentNullException("account");
            }

            var go = new GameObject("HangarScreen");
            go.transform.SetParent(bootstrap.transform, false);
            var screen = go.AddComponent<HangarScreen>();
            screen._bootstrap = bootstrap;
            screen._account = account;
            screen._filter = bootstrap.CarouselFilter;
            screen.Build();
            return screen;
        }

        /// <summary>
        /// Takes the hangar down: hidden at once, its stand's render state
        /// and loaded assets given back before anything else is built in
        /// this frame (a rebuilt hangar, the login screen), the objects
        /// destroyed at the end of it.
        /// </summary>
        public void Discard()
        {
            gameObject.SetActive(false);
            if (World != null)
            {
                World.Release();
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

        private void Build()
        {
            _atlas = AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            _lang = LocalizationTable.Parse(
                BlitzResources.LoadText(BlitzResources.LanguageData));
            _lang.Language = _bootstrap.Settings.Language;
            // The Modern style redraws only the chrome regions of this atlas;
            // every icon keeps the original artwork. See ModernSkin.
            _modern = MenuStyle.IsModern;
            MenuStyle.Apply(_atlas, _modern);
            _regular = BlitzResources.LoadFont(BlitzResources.RegularFontName);
            _bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            _tanks = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            ModRuntime.Apply(_tanks);
            ModOwnership.Grant(_account, _tanks);
            var selected = _tanks.Get(_account.SelectedTankName)
                ?? _tanks.Get(TankModelAssets.DefaultTankName);

            var now = _bootstrap.Clock.NowUnixSeconds;
            var premium = _account.HasPremium(now);
            World = HangarWorld.Create(
                transform, _bootstrap.Settings, selected, SelectedSave(),
                premium, HangarChoice.Current(_bootstrap.Settings));

            var canvas = CreateCanvas(transform);

            // Godot never queries the display safe area: ui_hangar.tscn is laid
            // out against the whole window (stretch mode canvas_items, aspect
            // expand), so a camera hole simply sits on top of the UI. Insetting
            // this root by Screen.safeArea pushed every element away from the
            // cutout edge and left a band the original build does not have.
            var root = UiFactory.Node("ui_root", canvas);
            GodotRect.FullRect(root);
            _uiRoot = root;

            BuildDragSurface(root);
            _main = UiFactory.Node("main", root);
            var main = _main;
            GodotRect.FullRect(main);

            // The sibling order below *is* the draw order, so every layer is
            // created up front and filled afterwards: main, the sub-screens,
            // the HUD band over both, and the z_index = 1 widgets over that.
            _screens = UiFactory.Node("screens", root);
            GodotRect.FullRect(_screens);

            // ui_hangar.tscn keeps panel_main_background and currencies as
            // root children listed after screens, so they draw over whichever
            // screen is open rather than belonging to screens/main. to_screen()
            // hides them only for battle_result, player_stats, battle_queue and
            // shop, so the tech tree and the module shop both keep the currency
            // readout on screen; the port keeps it in the shop as well, where
            // purchases change it. Built after BuildScreens for that draw order.
            _hud = UiFactory.Node("hud", root);
            GodotRect.FullRect(_hud);

            // The z_index = 1 layer. Last, so it sits over
            // panel_main_background exactly as the source z_index puts it;
            // see the field's remarks.
            _zTop = UiFactory.Node("z1", root);
            GodotRect.FullRect(_zTop);

            BuildScreens();
            if (_modern)
            {
                BuildModern(main);
            }
            else
            {
                BuildHeader(main, ZLayer(HangarScreenId.Main));
                BuildGlobalPanel(_hud);
                BuildCurrencies(_hud);
                BuildCenterRow(main);
                BuildFooter(main);
                BuildMenu(main);
                BuildPlaceholder(main);
            }
            BuildMusicPanel(main);
            EnsureEventSystem();
            RefreshTankCamouflage();

            Debug.Log(
                "[Blitz] hangar UI built: regions=" + _atlas.Count +
                " strings=" + _lang.Count +
                " style=" + (_modern ? "modern" : "classic") +
                " placeholders=" + PlaceholderButtonCount +
                " carousel=" + CarouselCardCount);
        }

        private void BuildDragSurface(RectTransform parent)
        {
            var image = UiFactory.Picture("HangarInspectionSurface", parent, null);
            image.color = new Color(0f, 0f, 0f, 0.001f);
            image.raycastTarget = true;
            GodotRect.FullRect(image.rectTransform);
            DragSurface = image.gameObject.AddComponent<HangarDragSurface>();
            DragSurface.Configure(World == null ? null : World.Orbit);
        }

        // ------------------------------------------------------------------
        // Header: the screens/main half of the top band.
        // ------------------------------------------------------------------

        /// <summary>Godot hides the global hangar bands while account/result screens own input.</summary>
        public void SetAccountOverlayVisible(bool visible)
        {
            if (visible) { CloseFilterImmediately(); }
            if (_main != null) _main.gameObject.SetActive(!visible);
            if (_screens != null) _screens.gameObject.SetActive(!visible);
            if (_hud != null) _hud.gameObject.SetActive(!visible);
            if (_zTop != null) _zTop.gameObject.SetActive(!visible);
            if (_carouselLayer != null)
            {
                if (visible) { SetCarouselMode(CarouselMode.Hidden, false); }
                else { ToScreen(_screen); }
            }
            var inspection = transform.Find("Canvas/ui_root/HangarInspectionSurface");
            if (inspection != null) inspection.gameObject.SetActive(!visible);
        }

        /// <summary>
        /// The source header row. <paramref name="main"/> takes the plain
        /// screens/main children; <paramref name="top"/> takes the four the
        /// source marks z_index = 1, which have to draw over the root-level
        /// panel_main_background plate instead of under it. Building them into
        /// screens/main left the burger, the nickname and the premium line
        /// tinted by that plate while the shop button and the currencies on
        /// the right, being root children after it, stayed bright.
        /// </summary>
        /// <summary>
        /// Only the nodes Godot keeps inside screens/main: blackGradTile,
        /// btn_menu, btn_acc and the nickname block. The header plate, its red
        /// line and btn_shop sit outside screens/ in the scene, so
        /// <see cref="BuildGlobalPanel"/> owns those and they survive a screen
        /// change.
        /// </summary>
        private void BuildHeader(RectTransform main, RectTransform top)
        {
            var grad = UiFactory.Picture(
                "blackGradTile", main, _atlas.Get("hangar_black_grad"));
            TopStretch(grad.rectTransform, 0f, 87f, CanvasWidth, 92f);

            var menu = StyleButton("btn_menu", top, "button_small_generic");
            TopLeft((RectTransform)menu.transform, 5f, 4f, 94f, 77f);
            AddIcon(menu, "hangar_menu", 50f);

            // btn_menu carries snd_var = 3, which button_generic.gd maps to
            // Audio.ui_slide, not to the default enter click.
            Bind(menu, ToggleMenu, AudioService.SoundFilterOpen);

            // btn_menu is toggle_mode in the source: it stays pressed for as
            // long as the drawer it opened is on screen.
            _menuToggle = GodotButtonState.Of(menu);
            _menuToggle.ToggleMode = true;

            // btn_acc is a modulate-alpha-0 hit zone over the nickname block.
            var account = StyleButton("btn_acc", top, "button_small_generic");
            account.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0f);
            TopLeft((RectTransform)account.transform, 104f, 10.77f, 208f, 39.23f);
            Bind(account, _bootstrap.OpenAccountStatistics, AudioService.SoundEnter);

            // Both labels are top-aligned: Godot's Label defaults to
            // VERTICAL_ALIGNMENT_TOP and neither node overrides it. Centring
            // them in their authored boxes dropped the nickname by half the
            // slack in its 61 px rect, which is why it sat lower than in the
            // source build.
            var nickname = Label("nickname", top, 28, TextAnchor.UpperLeft,
                _account.Name, _bold, UiTheme.Label);
            TopLeft(nickname.rectTransform, 119f, 17f, 215f, 61f);

            _premiumLabel = Label("premium_acc", top, 19, TextAnchor.UpperLeft,
                string.Empty, _regular, PremiumColor);
            TopLeft(_premiumLabel.rectTransform, 119f, 51f, 281f, 29f);
            RefreshPremium();
            RefreshLoadoutStrip();

        }

        /// <summary>
        /// panel_main_background with its ThinRedLine child, plus the shop
        /// button Godot parents to the root-level currencies node. All three
        /// are root children listed after screens in ui_hangar.tscn, so they
        /// draw over the open screen instead of vanishing with screens/main.
        ///
        /// The two plates are mouse_filter = 2 in Godot, and UiFactory.Picture
        /// already keeps images off the raycast, so the tech tree's own
        /// btn_menu underneath this band stays tappable.
        /// </summary>
        private void BuildGlobalPanel(RectTransform hud)
        {
            var header = UiFactory.Picture(
                "panel_main_background", hud, _atlas.Get("hangar_header"));
            header.type = Image.Type.Sliced;
            TopStretch(header.rectTransform, -68f, -74f, 1489.8f, 161.32f);

            var redLine = UiFactory.Picture(
                "ThinRedLine", hud, _atlas.Get("hangar_thin_red_line"));
            TopStretch(redLine.rectTransform, 0f, 87f, 1359.49f, 3f);

            var shop = StyleButton("btn_shop", hud, "button_small_green");
            TopRight((RectTransform)shop.transform, 1283.6667f, 4f, 95f, 78f);
            AddIcon(shop, "hangar_shop", 55f);
            Bind(shop, () => ToScreen(HangarScreenId.Shop), AudioService.SoundEnter);
        }

        // Drawn metrics of the currencies row: the authored Godot values
        // times the 0.7 scale of the container. The right edge is where the
        // free-XP icon ends, the separations are the 4 px HBox default and the
        // spacer that carries a divider is 50 px wide in the source.
        private const float CurrencyRowRight = 1260.97f;
        private const float CurrencySeparation = 2.8f;
        private const float CurrencyGap = 5.6f;
        private const float CurrencySpacerWidth = 35f;
        private const float CurrencyDividerInset = 19f;

        private sealed class CurrencyRow
        {
            public Text Value;
            public RectTransform Icon;
            public RectTransform Caption;
            /// <summary>Divider of the spacer left of this block, if any.</summary>
            public RectTransform Divider;
            public float IconWidth;
            public float CaptionOffset;
            public float CaptionWidth;
            public float MinWidth;
        }

        private readonly List<CurrencyRow> _currencies = new List<CurrencyRow>();

        /// <summary>
        /// The currencies HBox is scaled 0.7 in Godot, so both the rects and the
        /// font sizes below are the drawn values, not the authored ones.
        ///
        /// Parented to the HUD layer rather than to screens/main, so gold,
        /// credits and free XP stay on screen in the tech tree and the module
        /// shop exactly as they do in Godot.
        /// </summary>
        private void BuildCurrencies(RectTransform hud)
        {
            _currencies.Clear();
            _currencies.Add(Currency(
                hud, "gold", HangarUiKit.FormatNumber(_account.Gold), "hangar_gold",
                28f, -162.3f, 190.77f, 0f, "GOLD", null));
            _currencies.Add(Currency(
                hud, "credits", HangarUiKit.FormatNumber(_account.Credits),
                "hangar_credits", 28.7f, -124.4f, 154.97f, 35f, "CREDITS",
                Divider(hud, "divider")));
            _currencies.Add(Currency(
                hud, "freexp", HangarUiKit.FormatNumber(_account.FreeXp),
                "hangar_freexp", 28f, -215.3f, 243.77f, 105f, "FREE EXPERIENCE",
                Divider(hud, "divider2")));
            LayoutCurrencies();
        }

        private CurrencyRow Currency(
            RectTransform parent,
            string name,
            string amount,
            string iconRegion,
            float iconWidth,
            float captionOffset,
            float captionWidth,
            float minWidth,
            string captionKey,
            RectTransform divider)
        {
            var value = Label(name + "_amount", parent, 27, TextAnchor.MiddleRight,
                amount, _bold, UiTheme.Label);

            var icon = UiFactory.Picture(name + "_icon", parent, _atlas.Get(iconRegion));
            icon.preserveAspect = true;

            var caption = Label(name + "_caption", parent, 19, TextAnchor.MiddleCenter,
                _lang.Get(captionKey), _regular, CaptionColor);

            return new CurrencyRow
            {
                Value = value,
                Icon = icon.rectTransform,
                Caption = caption.rectTransform,
                Divider = divider,
                IconWidth = iconWidth,
                CaptionOffset = captionOffset,
                CaptionWidth = captionWidth,
                MinWidth = minWidth,
            };
        }

        private RectTransform Divider(RectTransform parent, string name)
        {
            var divider = UiFactory.Picture(name, parent, _atlas.Get("hangar_divider"));
            return divider.rectTransform;
        }

        /// <summary>
        /// The source row is an HBoxContainer anchored to the right with
        /// alignment = END, so a longer amount pushes its whole currency block
        /// and everything left of it further left instead of running into the
        /// icon next to it. uGUI has no such container here, so the run is
        /// measured and placed right to left with the same separations.
        /// </summary>
        private void LayoutCurrencies()
        {
            var right = CurrencyRowRight;
            for (var i = _currencies.Count - 1; i >= 0; i--)
            {
                var row = _currencies[i];
                var iconLeft = right - row.IconWidth;
                TopRight(row.Icon, iconLeft, 16f, row.IconWidth, 29.4f);

                var width = Mathf.Max(
                    row.MinWidth, Mathf.Ceil(row.Value.preferredWidth));
                var valueLeft = iconLeft - CurrencyGap - width;
                TopRight(row.Value.rectTransform, valueLeft, 16.7f, width, 28f);

                TopRight(row.Caption, iconLeft + row.CaptionOffset, 51.7f,
                    row.CaptionWidth, 41.3f);

                if (row.Divider == null)
                {
                    continue;
                }

                // The divider hangs off a 50 px spacer between two blocks.
                var spacerLeft =
                    valueLeft - CurrencySeparation - CurrencySpacerWidth;
                TopRight(row.Divider, spacerLeft + CurrencyDividerInset, 0f,
                    3.64f, 87.36f);
                right = spacerLeft - CurrencySeparation;
            }
        }

        // ------------------------------------------------------------------
        // Centre row. All four buttons live in the header band at y = 96,
        // anchored to the horizontal centre exactly like Godot preset 5.
        // ------------------------------------------------------------------

        /// <summary>A one-button notice over the hangar, e.g. why online play failed.</summary>
        public void ShowNotice(string headerKey, string textKey)
        {
            var parent = _uiRoot != null ? _uiRoot : _main;
            if (parent == null || _atlas == null || _lang == null) { return; }
            GenericPopup.Create(parent, _atlas, _lang, _bold, _regular, headerKey, textKey, true, null);
        }

        private void BuildCenterRow(RectTransform main)
        {
            var crew = StyleButton("btn_crew", main, "button_small_generic");
            GodotRect.CenterTop((RectTransform)crew.transform, CrewLeftDefault, 96f, CrewRightDefault, 173f);
            _crewButton = crew;
            AddIcon(crew, "hangar_crew", 70f);
            var crewPercent = Label("crew_percent", crew.transform, 28,
                TextAnchor.MiddleRight, "100%", _bold, UiTheme.Label);
            TopLeft(crewPercent.rectTransform, -73f, 15f, 64f, 29f);
            var crewCaption = Label("Label", crew.transform, 20,
                TextAnchor.MiddleRight, _lang.Get("CREW"), _regular, CaptionColor);
            TopLeft(crewCaption.rectTransform, -141f, 46f, 134f, 21f);
            BindPlaceholder(crew, "CREW");
            BuildClassicSandboxButton(main);

            var battle = StyleButton("btn_battle", main, "hangar_play");
            GodotRect.CenterTop((RectTransform)battle.transform, -145f, 96f, 77f, 173f);
            AddButtonLabel(battle, _lang.Get("BATTLE!"), 38);
            Bind(battle, () => _bootstrap.EnterBattle(this), AudioService.SoundBattle);

            var mode = StyleButton("btn_battle_mode", main, "button_small_generic");
            GodotRect.CenterTop((RectTransform)mode.transform, 80f, 96f, 254f, 173f);
            var modeName = Label("mode_name", mode.transform, 20,
                TextAnchor.MiddleCenter, _lang.Get(Core.Battle.BattleModeCatalog.NameKey(_bootstrap.SelectedMode)), _bold, UiTheme.ButtonFont);
            TopLeft(modeName.rectTransform, 6f, 6f, 162f, 40f);
            var modeCaption = Label("caption", mode.transform, 18,
                TextAnchor.UpperCenter, _lang.Get(_bootstrap.OnlineSelected ? "online:toggle/on" : "MODE"), _regular, CaptionColor);
            TopLeft(modeCaption.rectTransform, 6f, 48f, 162f, 21f);
            _modeName = modeName;
            _modeCaption = modeCaption;
            Bind(mode, () => OpenModePicker(), AudioService.SoundEnter);
            RefreshModeButton();

            var research = StyleButton("btn_research", main, "button_small_generic");
            GodotRect.CenterTop((RectTransform)research.transform, 259f, 96f, 353f, 173f);
            AddIcon(research, "hangar_research", 64f);
            var exp = Label("curtank_exp", research.transform, 29,
                TextAnchor.MiddleRight, "0", _bold, UiTheme.Label);
            TopLeft(exp.rectTransform, 104f, 13f, 64f, 30f);
            var expIcon = UiFactory.Picture(
                "exp_icon", research.transform, _atlas.Get("hangar_exp"));
            expIcon.preserveAspect = true;
            TopLeft(expIcon.rectTransform, 172f, 13f, 29f, 29f);
            Bind(research, OpenModulesForCurrentTank, AudioService.SoundEnter);

            var armor = StyleButton("btn_armor_viewer", main, "button_small_generic");
            GodotRect.CenterTop((RectTransform)armor.transform, 360f, 96f, 520f, 173f);
            AddButtonLabel(armor, _lang.Get("ARMOR_VIEWER"), 21);
            Bind(armor, OpenArmorViewer, AudioService.SoundEnter);

            // The source header row ends with btn_research: shells and
            // equipment are not header buttons there, they are the icon strip
            // above the carousel footer (see BuildLoadoutStrip).
        }

        // ------------------------------------------------------------------
        // Footer: the filter band. The carousel, btn_filter, btn_techTree, the
        // filter rows, the loadout strip and btn_notifs all ride on it.
        // ------------------------------------------------------------------

        /// <summary>
        /// screens/main/filter_handle/filter, child by child in the scene's
        /// order. Every widget is built into the band, so show_filter lifts the
        /// whole footer together with the filter rows (see
        /// <see cref="HangarFilterDrawer"/>). The band's bottom edge is the
        /// screen's while the filter is shut, so the screen-space rects below
        /// are the source's own.
        /// </summary>
        private void BuildFooter(RectTransform main)
        {
            _filterDrawer = HangarFilterDrawer.Create(main, _kit, _filter);
            _filterDrawer.Changed = RebuildCarousel;
            var band = _filterDrawer.Band;

            var footer = UiFactory.Picture(
                "footer_lol", band, _atlas.Get("hangar_footer"));
            footer.type = Image.Type.Sliced;
            BottomStretch(footer.rectTransform, 0f, FooterTop, CanvasWidth, FooterHeight);

            BuildCarousel(band);

            // btn_filter: every StyleBox override draws hangar_filter_hide as
            // it is, so the pressed button that marks the open filter does
            // not darken.
            var filter = StyleButton("btn_filter", band, "hangar_filter_hide");
            BottomLeft((RectTransform)filter.transform, 0f, FooterTop, 105f, FooterHeight);
            GodotButtonState.Of(filter).SetPressedStyle(
                filter.GetComponent<Image>().sprite, Color.white);
            // filter_icon: expand_mode = 1 with the default STRETCH_SCALE.
            _filterIcon = UiFactory.Picture(
                "filter_icon", filter.transform, _atlas.Get("hangar_filter_icon"));
            TopLeft(_filterIcon.rectTransform, 37f, 32f, 32f, 22f);
            var filterCaption = Label("Label", filter.transform, 20,
                TextAnchor.MiddleCenter, _lang.Get("FILTER:"), _regular, CaptionColor);
            TopLeft(filterCaption.rectTransform, 24f, 75f, 74f, 21f);
            _filterState = Label("filter_onoff", filter.transform, 22,
                TextAnchor.MiddleCenter, _lang.Get("OFF"), _bold, UiTheme.Label);
            TopLeft(_filterState.rectTransform, 21f, 97f, 57f, 26f);
            filter.onClick.AddListener(() =>
            {
                // ButtonCustom._pressed(): snd_var = SOUND_SLIDE on every
                // click. The toggled sound follows in SetFilterOpen.
                PlayFilterSound(AudioService.SoundFilterOpen);
                ToggleFilter();
            });

            // btn_filter is toggle_mode too, so it marks the open filter.
            _filterToggle = GodotButtonState.Of(filter);
            _filterToggle.ToggleMode = true;

            var techTree = StyleButton("btn_techTree", band, "hangar_filter_hide");
            BottomRight((RectTransform)techTree.transform,
                1281.6667f, FooterTop, 105f, FooterHeight);
            GodotButtonState.Of(techTree).SetPressedStyle(
                techTree.GetComponent<Image>().sprite, Color.white);
            var techIcon = UiFactory.Picture(
                "TextureRect", techTree.transform, _atlas.Get("hangar_techtree"));
            techIcon.preserveAspect = true;
            TopLeft(techIcon.rectTransform, 27f, 19f, 51f, 35f);
            var techLabel = Label("Label", techTree.transform, 15,
                TextAnchor.UpperCenter, _lang.Get("TECH TREE"), _regular, CaptionColor);
            techLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            TopLeft(techLabel.rectTransform, 9f, 76f, 87f, 54f);
            Bind(techTree, () => ToScreen(HangarScreenId.TechTree), AudioService.SoundEnter);

            _filterDrawer.BuildControls();
            // The filter belongs to the session and may come in set.
            _filterDrawer.Refresh();

            var grad = UiFactory.Picture(
                "blackGradTile2", band, _atlas.Get("hangar_black_grad"));
            BottomStretch(grad.rectTransform, 0f, 522.3333f, CanvasWidth, 71f);

            BuildLoadoutStrip(band);
            BuildNews(band);
        }

        /// <summary>
        /// screens/main/filter_handle/filter/tank_equipment of ui_hangar.tscn:
        /// the consumables, shells and equipment rows the source draws above
        /// the carousel at scale 0.33, and the three invisible buttons
        /// (btn_consumables, btn_shells, btn_equipment) laid over them.
        /// </summary>
        private void BuildLoadoutStrip(RectTransform main)
        {
            var strip = UiFactory.Node("tank_equipment", main);
            BottomCenter(strip, 0f, StripTop, CanvasWidth, StripHeight);

            var consumables = ScaledRow(
                strip, "consumables_box", ConsumablesBoxX, 1.02f, ConsumablesBoxWidth, 61.18f,
                EquipRowScale);
            var shells = ScaledRow(
                strip, "shells_box", 559.67f, 0.62f, 199.32f, 61.38f,
                ShellRowScale);
            var equipment = ScaledRow(
                strip, "equipment_box", 776.41f, 2.02f, 192.46f, 61.18f,
                EquipRowScale);

            for (var i = 0; i < StripSlots; i++)
            {
                _stripConsumables[i] = BuildEquipmentSlot(
                    consumables, "cons_icon" + (i + 1),
                    i * EquipSlotNativeStep, true);
                _stripEquipment[i] = BuildEquipmentSlot(
                    equipment, "eq_icon" + (i + 1),
                    i * EquipSlotNativeStep, false);
                _stripShells[i] = BattleShellSelector.ShellIcon.Create(
                    shells, "shell" + (i + 1), i * ShellSlotNativeStep,
                    _atlas, _lang, _bold);
            }

            var consumablesButton = StripButton(strip, "btn_consumables", 345.33f);
            Bind(
                consumablesButton,
                () => OpenLoadout(LoadoutScreen.TabConsumables),
                AudioService.SoundEnter);
            var shellsButton = StripButton(strip, "btn_shells", 558.33f);
            Bind(
                shellsButton,
                () => OpenLoadout(LoadoutScreen.TabAmmo),
                AudioService.SoundEnter);
            var equipmentButton = StripButton(strip, "btn_equipment", 774.33f);
            Bind(
                equipmentButton,
                () => OpenLoadout(LoadoutScreen.TabEquipment),
                AudioService.SoundEnter);

            BuildCamouflageSlot(strip);
            RefreshLoadoutStrip();
        }

        /// <summary>
        /// Hangar.yaml TankCamouflages, the first child of the 4.8 inventory
        /// row: the "Camouflage" SlotLabel 24 units over a 50 x 50
        /// InventorySlot and the red badge of a shop the vehicle has not been
        /// shown yet. .slot_empty draws ItemButton frame 0 without the icon,
        /// .slot_filled frame 1 with IconCamouflage. The slot is drawn at the
        /// equipment slot size, one panel gap left of consumables_box.
        /// </summary>
        private void BuildCamouflageSlot(RectTransform strip)
        {
            var unit = CamouflageSlotSize / CamouflageSourceSlot;
            var button = StyleButton("btn_camouflage", strip, null);
            var rt = (RectTransform)button.transform;
            TopLeft(rt, CamouflageSlotX, CamouflageSlotY, CamouflageSlotSize, CamouflageSlotSize);

            _stripCamouflageFrame = UiFactory.Picture("frame", rt, _atlas.Get("camo_hangar_slot"));
            GodotRect.FullRect(_stripCamouflageFrame.rectTransform);
            _stripCamouflageIcon = UiFactory.Picture(
                "icon", _stripCamouflageFrame.rectTransform, _atlas.Get("camo_hangar_icon"));
            _stripCamouflageIcon.preserveAspect = true;
            GodotRect.FullRect(_stripCamouflageIcon.rectTransform);

            // SlotLabel: text2-small, white with the 0.745 shadow one unit down.
            var caption = UiFactory.Label(
                "caption", rt, _regular, Mathf.RoundToInt(CamouflageCaptionSize * unit), Color.white,
                TextAnchor.MiddleCenter, _lang.Get(CamouflageScreen.TitleKey));
            UiFactory.AddShadow(caption, new Color(0f, 0f, 0f, 0.745098f), new Vector2(0f, unit));
            var captionWidth = CamouflageCaptionWidth * unit;
            TopLeft(
                caption.rectTransform, (CamouflageSlotSize - captionWidth) * 0.5f, CamouflageCaptionTop * unit,
                captionWidth, CamouflageCaptionHeight * unit);
            _stripCamouflageCaption = caption;

            _stripCamouflageBadge = UiFactory.Picture("badge", rt, _atlas.Get("camo_white_circle"));
            _stripCamouflageBadge.color = CamouflageBadgeColor;
            _stripCamouflageBadge.preserveAspect = true;
            TopLeft(
                _stripCamouflageBadge.rectTransform, CamouflageBadgeX * unit, CamouflageBadgeY * unit,
                CamouflageBadgeSize * unit, CamouflageBadgeSize * unit);

            Bind(button, OpenCamouflage, AudioService.SoundEnter);
        }

        /// <summary>The strip's camouflage slot for the carousel selection.</summary>
        private void RefreshCamouflageSlot(TankSaveData save)
        {
            if (_stripCamouflageFrame == null)
            {
                return;
            }

            var filled = !string.IsNullOrEmpty(CamouflageService.HangarCamouflage(CamouflageAssets.Catalog, save));
            _stripCamouflageFrame.sprite = _atlas.Get(filled ? "camo_hangar_slot_filled" : "camo_hangar_slot");
            _stripCamouflageIcon.gameObject.SetActive(filled);
            _stripCamouflageBadge.gameObject.SetActive(
                _camouflageService != null && save != null && _camouflageService.ShowsBadge(save));
        }

        /// <summary>Whether the strip's camouflage slot shows the camouflage icon (a slot holds one).</summary>
        public bool CamouflageSlotFilled
        {
            get { return _stripCamouflageIcon != null && _stripCamouflageIcon.gameObject.activeSelf; }
        }

        /// <summary>Whether the strip's camouflage slot wears the red badge of an unvisited shop.</summary>
        public bool CamouflageBadgeShown
        {
            get { return _stripCamouflageBadge != null && _stripCamouflageBadge.gameObject.activeSelf; }
        }

        /// <summary>
        /// One of the three HBoxContainers. The children keep the source
        /// sizes, the row carries the scale the source applies to them.
        /// </summary>
        private RectTransform ScaledRow(
            RectTransform strip,
            string name,
            float x,
            float y,
            float width,
            float height,
            float scale)
        {
            var row = UiFactory.Node(name, strip);
            TopLeft(row, x, y, width, height);
            row.localScale = new Vector3(scale, scale, 1f);
            return row;
        }

        /// <summary>The invisible hit areas of tank_equipment, 197 x 63.</summary>
        private Button StripButton(RectTransform strip, string name, float x)
        {
            var button = StyleButton(name, strip, null);
            TopLeft((RectTransform)button.transform, x, 0f, 197f, 63f);
            return button;
        }

        /// <summary>equipment_icon.tscn/CenterContainer/icon, a 76 px box.</summary>
        private const float EquipIconBox = 76f;

        private EquipmentSlotView BuildEquipmentSlot(
            RectTransform row, string name, float x, bool consumable)
        {
            var frame = UiFactory.Picture(
                name, row, _atlas.Get("equipment_slot_empty"));
            TopLeft(
                frame.rectTransform, x, 0f,
                EquipSlotNativeSize, EquipSlotNativeSize);

            var icon = UiFactory.Picture("icon", frame.rectTransform, null);
            icon.raycastTarget = false;

            var view = new EquipmentSlotView
            {
                Frame = frame,
                Icon = icon,
                Consumable = consumable,
            };
            SetEquipmentSlot(view, null);
            return view;
        }

        /// <summary>
        /// equipment_icon.init(id): the widget looks the id up in
        /// consumables_list when it is a consumable slot and in
        /// equipment_list otherwise. A hit wears BoxEquipPower_04 with the
        /// item artwork at handle 1, a miss BoxEquipPower_03 with EmptySlot at
        /// handle 1.5. The icon TextureRect keeps the default stretch mode, so
        /// the artwork is scaled into its 76 px box without keeping aspect.
        /// </summary>
        private void SetEquipmentSlot(EquipmentSlotView view, string id)
        {
            if (view == null || view.Frame == null) { return; }

            var region = HangarUiKit.SlotIconRegion(id, view.Consumable);
            var filled = !string.IsNullOrEmpty(region);
            view.Frame.sprite = _atlas.Get(
                filled ? "equipment_slot_filled" : "equipment_slot_empty");

            var sprite = _atlas.Get(filled ? region : "equipment_empty_slot");
            view.Icon.sprite = sprite;
            view.Icon.enabled = sprite != null;

            var handle = filled ? 1f : 1.5f;
            var rt = view.Icon.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            view.Icon.preserveAspect = false;
            rt.sizeDelta = sprite == null
                ? Vector2.zero
                : new Vector2(EquipIconBox * handle, EquipIconBox * handle);
        }

        /// <summary>
        /// ui_hangar.update_tank_equipment: the strip mirrors the shells and
        /// the equipment of the carousel selection.
        /// </summary>
        private void RefreshLoadoutStrip()
        {
            if (_stripShells[0] == null || _account == null) { return; }

            var save = SelectedSave();
            var info = _tanks == null ? null : _tanks.Get(_account.SelectedTankName);
            var gun = info == null || info.Guns.Count == 0
                ? null
                : info.GunFor(
                    save == null ? 0 : save.TurretId,
                    save == null ? 0 : save.GunId);

            for (var i = 0; i < StripSlots; i++)
            {
                var hasShell = gun != null && i < gun.Shells.Count;
                var amount = save != null && i < save.ShellsAmounts.Count
                    ? save.ShellsAmounts[i]
                    : 0;
                // The hangar instances of shell_icon.tscn keep the scene
                // default BgType.GENERIC, so an empty slot only loses its
                // artwork, never the BulletButton frame.
                _stripShells[i].Init(
                    BattleShellSelector.Background.Generic,
                    hasShell ? gun.Shells[i] : null,
                    amount);

                var equipped = save == null
                    ? null
                    : LoadoutService.Installed(save, i);
                SetEquipmentSlot(_stripEquipment[i], equipped);

                var consumable = save != null && i < save.Consumables.Count
                    ? save.Consumables[i]
                    : null;
                SetEquipmentSlot(_stripConsumables[i], consumable);
            }

            RefreshCamouflageSlot(save);
        }

        private TankSaveData SelectedSave()
        {
            var name = _account.SelectedTankName;
            for (var i = 0; i < _account.Tanks.Count; i++)
            {
                if (_account.Tanks[i].Name == name)
                {
                    return _account.Tanks[i];
                }
            }

            return null;
        }

        /// <summary>The frame and artwork of one equipment_icon instance.</summary>
        private sealed class EquipmentSlotView
        {
            public Image Frame;
            public Image Icon;

            /// <summary>equipment_icon.is_consumable.</summary>
            public bool Consumable;
        }

        /// <summary>
        /// Godot's ScrollContainer spans x = 105 .. 1281.667 and its HBox starts
        /// flush against the left edge, so a single tank sits at the far left.
        /// The HBox lays the cards CardWidth + Separation apart; the port
        /// keeps that row but builds only the cards over the view
        /// (<see cref="CarouselStrip"/>), so a garage of every vehicle costs
        /// what a dozen cards cost.
        /// </summary>
        private void BuildCarousel(RectTransform main)
        {
            var viewport = UiFactory.Node("ScrollContainer", main);
            BottomStretch(viewport, 105f, 595.3333f, 1176.6667f, 138f);
            viewport.gameObject.AddComponent<RectMask2D>();

            CarouselRoot = UiFactory.Node("tank_carousel", viewport);
            CarouselRoot.anchorMin = new Vector2(0f, 1f);
            CarouselRoot.anchorMax = new Vector2(0f, 1f);
            CarouselRoot.pivot = new Vector2(0f, 1f);
            CarouselRoot.anchoredPosition = new Vector2(0f, -4f);
            CarouselRoot.sizeDelta = new Vector2(0f, CardHeight);

            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = CarouselRoot;
            scroll.viewport = viewport;
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 20f;

            _strip = CarouselStrip.Attach(scroll, CardWidth, Separation, CreateTankCard, BindTankCard);
            RebuildCarousel();
        }

        private readonly List<string> _carouselNames = new List<string>();

        /// <summary>The cards the filter lets through, in carousel order: save index and tank.</summary>
        private readonly List<KeyValuePair<int, TankInfo>> _carouselCards =
            new List<KeyValuePair<int, TankInfo>>();

        /// <summary>The parts of every carousel card built so far, by the card.</summary>
        private readonly Dictionary<RectTransform, CarouselCard> _cardViews =
            new Dictionary<RectTransform, CarouselCard>();

        /// <summary>HasPlayableModel's answers by tank name, until the mods change.</summary>
        private readonly Dictionary<string, bool> _playable =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        private CarouselStrip _strip;

        /// <summary>
        /// One carousel card: the parts a bind redraws when the card moves on
        /// to another tank, and the tank it shows now.
        /// </summary>
        private sealed class CarouselCard
        {
            public Image Nation;
            public Image Icon;
            public Text Unknown;
            public Image Class;
            public Image Level;
            public Text Name;
            public GameObject Selected;
            public TankInfo Info;
            public int SaveIndex;
        }

        /// <summary>
        /// Scrolls the carousel to card <paramref name="index"/> of
        /// <see cref="CarouselCardCount"/> and returns it. Only the cards over
        /// the carousel's view exist; the rest are built as they scroll in.
        /// </summary>
        public RectTransform ShowCarouselCard(int index)
        {
            if (_strip == null)
            {
                throw new InvalidOperationException("The carousel is not built.");
            }

            return _strip.Show(index);
        }

        /// <summary>Carousel cards that exist right now, on show or kept for reuse.</summary>
        public int CarouselCardsBuilt
        {
            get { return _strip == null ? 0 : _strip.BuiltCount; }
        }

        /// <summary>
        /// A sold or removed tank must not stay selected: moves the selection
        /// to the next card of the previous carousel (or the one before it).
        /// </summary>
        private void EnsureSelection(List<KeyValuePair<int, TankInfo>> order)
        {
            if (order.Count == 0) { return; }

            var selected = _account.SelectedTankName;
            foreach (var entry in order)
            {
                if (entry.Value.Name == selected) { return; }
            }

            var pick = order[0];
            var at = _carouselNames.IndexOf(selected);
            if (at >= 0)
            {
                var found = false;
                for (var i = at + 1; i < _carouselNames.Count && !found; i++)
                {
                    found = TryFindCard(order, _carouselNames[i], ref pick);
                }

                for (var i = at - 1; i >= 0 && !found; i--)
                {
                    found = TryFindCard(order, _carouselNames[i], ref pick);
                }
            }

            _account.SelectedTankName = pick.Value.Name;
            _account.SelectedTankId = pick.Key;
            if (World != null) { World.ShowTank(pick.Value); }
        }

        private static bool TryFindCard(
            List<KeyValuePair<int, TankInfo>> order, string name,
            ref KeyValuePair<int, TankInfo> pick)
        {
            foreach (var entry in order)
            {
                if (entry.Value.Name == name) { pick = entry; return true; }
            }

            return false;
        }

        private void RebuildCarousel()
        {
            if (_strip == null || _tanks == null || _account == null)
            {
                return;
            }

            RebuildCarousel(CarouselOrder());
        }

        /// <summary>
        /// update_carousel(): the cards of <paramref name="order"/> the filter
        /// lets through. Only the list is rebuilt; the strip draws the few
        /// cards on show again and builds none it already has.
        /// </summary>
        private void RebuildCarousel(List<KeyValuePair<int, TankInfo>> order)
        {
            if (_strip == null)
            {
                return;
            }

            _carouselNames.Clear();
            _carouselCards.Clear();
            foreach (var entry in order)
            {
                // update_carousel() skips a tank the filter rejects. The
                // selection order still keeps every owned tank, so selling a
                // filtered-out tank moves the selection sensibly.
                _carouselNames.Add(entry.Value.Name);
                if (_filter.Matches(entry.Value)) { _carouselCards.Add(entry); }
            }

            // A filter that rejects every tank leaves the carousel empty, as
            // in the source; only an account without tanks gets the default.
            if (order.Count == 0)
            {
                var fallback = _tanks.Get(TankModelAssets.DefaultTankName);
                if (fallback != null)
                {
                    _carouselCards.Add(new KeyValuePair<int, TankInfo>(0, fallback));
                }
            }

            CarouselCardCount = _carouselCards.Count;
            _strip.SetCount(_carouselCards.Count);
            if (_modern)
            {
                RefreshModernCarouselChrome();
            }
        }

        /// <summary>
        /// Blitz opens the carousel on the strongest vehicles: tier 10 sits at
        /// the left edge, then tier 9, 8 and so on, with the premiums and the
        /// collector vehicles mixed into their own tier instead of being
        /// pushed behind the tech tree ones. Inside a tier the order stays
        /// stable: nation, then class, then the short name.
        /// </summary>
        private List<KeyValuePair<int, TankInfo>> CarouselOrder()
        {
            var ordered = new List<KeyValuePair<int, TankInfo>>();
            for (var i = 0; i < _account.Tanks.Count; i++)
            {
                var save = _account.Tanks[i];
                if (save == null || !save.Bought) { continue; }
                var info = _tanks.Get(save.Name);
                // update_carousel skips hidden cards: an event's own vehicle plays only there.
                if (info == null || info.Hidden || !HasPlayableModel(info)) { continue; }
                ordered.Add(new KeyValuePair<int, TankInfo>(i, info));
            }

            ordered.Sort((a, b) => CompareCarouselCards(a.Value, b.Value));
            return ordered;
        }

        private static int CompareCarouselCards(TankInfo a, TankInfo b)
        {
            var byLevel = b.Level.CompareTo(a.Level);
            if (byLevel != 0) { return byLevel; }

            var byNation = ((int)a.Nation).CompareTo((int)b.Nation);
            if (byNation != 0) { return byNation; }

            var byClass = ((int)a.Class).CompareTo((int)b.Class);
            if (byClass != 0) { return byClass; }

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// One empty carousel_tank.tscn; <see cref="BindTankCard"/> draws a
        /// tank on it. The icon, the class and the tier use stretch_mode
        /// KEEP_CENTERED: the texture keeps its source pixel size, scaled by
        /// the node scale, centred inside the node rect.
        /// </summary>
        private RectTransform CreateTankCard()
        {
            var view = new CarouselCard();
            var card = UiFactory.Picture("carousel_tank", CarouselRoot, null);
            var cardRect = card.rectTransform;
            cardRect.anchorMin = new Vector2(0f, 1f);
            cardRect.anchorMax = new Vector2(0f, 1f);
            cardRect.pivot = new Vector2(0f, 1f);
            cardRect.sizeDelta = new Vector2(CardWidth, CardHeight);
            cardRect.anchoredPosition = Vector2.zero;
            view.Nation = card;

            view.Icon = UiFactory.Picture("icon", cardRect, null);
            // No icon anywhere (a mod that shipped none): a "?" instead of
            // the white quad an empty Image draws.
            view.Unknown = Label("icon_unknown", cardRect, 64, TextAnchor.MiddleCenter,
                "?", _bold, Color.white);
            TopLeft(view.Unknown.rectTransform, 40f, 14f, 115f, 86f);
            view.Class = UiFactory.Picture("class", cardRect, null);
            view.Level = UiFactory.Picture("lvl", cardRect, null);
            view.Name = Label("name", cardRect, 24, TextAnchor.MiddleLeft,
                string.Empty, _bold, Color.white);
            TopLeft(view.Name.rectTransform, 8f, 100f, 110f, 25f);

            var selected = UiFactory.Picture(
                "selected", cardRect, _atlas.Get("tank_slot_selected"));
            TopLeft(selected.rectTransform, 0f, 1f, 195f, 128f);
            view.Selected = selected.gameObject;

            var button = StyleButton("button", cardRect, null);
            TopLeft((RectTransform)button.transform, 0f, 1f, 197f, 128f);
            Bind(button, () => SelectTank(view.Info, view.SaveIndex), AudioService.SoundEnter);
            _cardViews.Add(cardRect, view);
            return cardRect;
        }

        /// <summary>
        /// carousel_tank.setup(): draws carousel card <paramref name="index"/>
        /// on <paramref name="card"/>, in either style. A card already showing
        /// that tank only updates its highlight.
        /// </summary>
        private void BindTankCard(RectTransform card, int index)
        {
            var view = _cardViews[card];
            var entry = _carouselCards[index];
            view.SaveIndex = entry.Key;
            if (view.Info != entry.Value)
            {
                var info = entry.Value;
                view.Info = info;
                view.Nation.sprite = _atlas.Get(HangarUiKit.CarouselNationRegion(info.Nation));
                var tint = HangarUiKit.TankGlyphTint(info);
                var iconSprite = CarouselIcon(info);
                view.Icon.sprite = iconSprite;
                view.Icon.gameObject.SetActive(iconSprite != null);
                view.Unknown.gameObject.SetActive(iconSprite == null);
                view.Unknown.color = tint;
                view.Class.sprite = _atlas.Get(HangarUiKit.TankClassRegion(info));
                view.Class.color = tint;
                view.Level.sprite = _atlas.Get(HangarUiKit.TankLevelRegion(info));
                view.Level.color = tint;
                view.Name.text = _lang.GetTankName(info, true);
                view.Name.color = HangarUiKit.TankNameColor(info);
                if (!_modern)
                {
                    if (iconSprite != null)
                    {
                        KeepCentered(view.Icon, 0.43f, 4.5f, 195.2f, 122f, 0.61f);
                    }

                    KeepCentered(view.Class, 6.48f, 1.92f, 27.04f, 30.16f, 0.52f);
                    KeepCentered(view.Level, 32.42f, 2.88f, 29.7f, 30.78f, 0.54f);
                }
            }

            var selected = string.Equals(
                _account.SelectedTankName, view.Info.Name, StringComparison.OrdinalIgnoreCase);
            if (view.Selected.activeSelf != selected)
            {
                view.Selected.SetActive(selected);
            }
        }

        /// <summary>
        /// Draws every carousel card from scratch, e.g. in a language just
        /// picked; a bind otherwise skips a card that keeps its tank.
        /// </summary>
        private void RedrawCarouselCards()
        {
            foreach (var view in _cardViews.Values)
            {
                view.Info = null;
            }

            if (_strip != null) { _strip.Rebind(); }
        }

        /// <summary>The carousel icon: the tank's model data id, then the icon a mod shipped, else null.</summary>
        private Sprite CarouselIcon(TankInfo info)
        {
            var iconSprite = _atlas.Get("tank_icon_" + TankModelAssets.For(info).DataId);
            if (iconSprite == null && info.Mod)
            {
                iconSprite = ModRuntime.Icon(info.Name);
            }

            return iconSprite;
        }

        /// <summary>
        /// Points the hangar model at the installed turret and gun of the
        /// selection, taken off the save the way the loadout screen reads it.
        /// </summary>
        private void RefreshTankModules()
        {
            if (World == null || _account == null || _tanks == null) { return; }

            var info = _tanks.Get(_account.SelectedTankName);
            if (info == null) { return; }

            string turretMesh;
            string gunMesh;
            TankLoadoutMeshes.Resolve(info, SelectedSave(), out turretMesh, out gunMesh);
            World.ShowModules(turretMesh, gunMesh);
        }

        private bool HasPlayableModel(TankInfo info)
        {
            // TankAssetSource builds a mod's model on its first ask, and the
            // carousel walks the whole garage on every change: ask once per tank.
            bool playable;
            if (!_playable.TryGetValue(info.Name, out playable))
            {
                playable = TankAssetSource.HasModel(info);
                _playable[info.Name] = playable;
            }

            return playable;
        }

        private void SelectTank(TankInfo info, int saveIndex)
        {
            if (info == null || _account == null || !HasPlayableModel(info))
            {
                return;
            }

            _account.SelectedTankName = info.Name;
            _account.SelectedTankId = saveIndex;
            if (World != null) { World.ShowTank(info); }
            RefreshTankModules();
            // The row stays as it is; only the highlight moves.
            if (_strip != null) { _strip.Rebind(); }
            OnAccountChanged(false);
        }

        /// <summary>
        /// Places a source TextureRect that uses stretch_mode KEEP_CENTERED:
        /// the sprite keeps its source pixel size multiplied by the node
        /// scale and is centred inside the laid out rect.
        /// </summary>
        public static Rect KeepCenteredRect(Rect rect, Vector2 native, float scale)
        {
            var size = native * scale;
            return new Rect(
                rect.x + ((rect.width - size.x) * 0.5f),
                rect.y + ((rect.height - size.y) * 0.5f),
                size.x,
                size.y);
        }

        private void KeepCentered(
            Image image, float x, float y, float width, float height, float scale)
        {
            var rect = new Rect(x, y, width, height);
            var sprite = image.sprite;
            var placed = sprite != null
                ? KeepCenteredRect(rect, sprite.rect.size, scale)
                : rect;
            TopLeft(
                image.rectTransform, placed.x, placed.y, placed.width, placed.height);
        }

        /// <summary>
        /// btn_notifs sits above btn_techTree, flush with the right edge:
        /// x = 1304.667, y = 505.333, 76 x 73, on the filter band. Its NEWS
        /// caption is visible = false in the scene, so only the icon shows.
        /// </summary>
        private void BuildNews(RectTransform band)
        {
            var news = StyleButton("btn_notifs", band, "button_small_generic");
            BottomRight((RectTransform)news.transform, 1304.6667f, ClassicNotifsTop, 76f, 73f);
            AddIcon(news, "hangar_news", 38f);
            var label = Label("Label", news.transform, 15,
                TextAnchor.MiddleCenter, _lang.Get("NEWS"), _regular, CaptionColor);
            GodotRect.Apply(label.rectTransform, 0f, 0f, 1f, 1f, 0f, 17f, 0f, 0f);
            label.gameObject.SetActive(false);
            BindPlaceholder(news, "NEWS");
        }

        // ------------------------------------------------------------------
        // Drawers.
        // ------------------------------------------------------------------

        /// <summary>
        /// screens/main/menu. The drawer itself lives in
        /// <see cref="HangarMenuPanel"/>; this only supplies what ui_hangar.gd
        /// wires into it, which is the two button callbacks and, in a debug
        /// build, the account toolbox.
        /// </summary>
        private void BuildMenu(RectTransform parent)
        {
            _menu = HangarMenuPanel.Create(parent, _kit);
            _menu.LogoutPressed = ConfirmLogout;
            _menu.SettingsPressed = OpenSettings;
            _menu.ModsPressed = OpenMods;

            if (!HangarAccountToolbox.Enabled)
            {
                return;
            }

            Toolbox = new HangarAccountToolbox(
                _kit, _account, _tanks, _research, _bootstrap.Clock);
            Toolbox.CurrenciesChanged = RefreshCurrencies;
            Toolbox.AccountChanged = OnAccountChanged;
            _menu.BuildToolbox(Toolbox);
        }

        /// <summary>
        /// The music panel goes into the main view only, so the sub-screens,
        /// the settings and the account overlays hide it with that view; the
        /// queue and the classic filter, which lifts the footer over its
        /// place, hide it too. Until the player drags it elsewhere, Classic
        /// keeps it on the right edge under the header button row and Modern
        /// stacks it under the tank panel, and either keeps it above the
        /// controls under it, btn_notifs or the battle row: the playlist and
        /// the equalizer, taller than the player, rise to fit, which in Modern
        /// takes them over the tank panel. Built last, it stays out of the
        /// Modern chrome fade, as the tank panel does.
        /// </summary>
        private void BuildMusicPanel(RectTransform main)
        {
            var player = _bootstrap.Music;
            if (player == null)
            {
                return;
            }

            MusicPanel = HangarMusicPanel.Create(
                main, player, () => MusicPlayerOn && _bootstrap.Queue == null && (_modern || !IsFilterOpen),
                _lang, _regular, _bold, _bootstrap.Audio);
            var top = _modern
                ? ModernTheme.TopBarHeight + 16f + ModernTankPanelHeight + ModernTheme.Gap
                : ClassicMusicPanelTop;
            var floor = _modern
                ? _metrics.BarHeight + ModernTheme.Gap + ModernBattleHeight
                : CanvasHeight - ClassicNotifsTop;
            MusicPanel.SetHome(16f, top, floor);
            player.Attach(this, () => MusicPlayerOn);
        }

        private void OnDestroy()
        {
            if (_bootstrap != null && _bootstrap.Music != null)
            {
                _bootstrap.Music.Detach(this);
            }
        }

        private void BuildPlaceholder(RectTransform parent)
        {
            _placeholder = UiFactory.Node("PlaceholderPanel", parent);
            GodotRect.Centered(_placeholder, -260f, -72f, 260f, 72f);
            var background = _placeholder.gameObject.AddComponent<Image>();
            background.sprite = _atlas.Get("panel_body");
            background.type = Image.Type.Sliced;
            background.color = new Color(1f, 1f, 1f, 0.97f);

            _placeholderTitle = Label("title", _placeholder, 21,
                TextAnchor.MiddleCenter, _lang.Get("HANGAR_FEATURE_PLACEHOLDER"),
                _bold, UiTheme.Label);
            GodotRect.Apply(
                _placeholderTitle.rectTransform, 0f, 0f, 1f, 0f, 16f, 12f, -16f, 48f);

            _placeholderHint = Label("hint", _placeholder, 16,
                TextAnchor.MiddleCenter, _lang.Get("HANGAR_PLACEHOLDER_HINT"),
                _regular, UiTheme.Label);
            _placeholderHint.horizontalOverflow = HorizontalWrapMode.Wrap;
            GodotRect.Apply(
                _placeholderHint.rectTransform, 0f, 0f, 1f, 1f, 20f, 48f, -20f, -10f);
            _placeholder.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------
        // Behaviour.
        // ------------------------------------------------------------------

        /// <summary>
        /// btn_menu.toggled: the source unpresses btn_filter first, so the two
        /// drawers are never open at once.
        /// </summary>
        private void ToggleMenu()
        {
            if (IsFilterOpen)
            {
                SetFilterOpen(false);
            }
            _menu.Toggle();
            _menuToggle.Latched = _menu.IsOpen;
            SyncCarouselWithMenu();
        }

        /// <summary>Slides the drawer shut and releases btn_menu.</summary>
        private void SetMenuClosed()
        {
            _menu.SetOpen(false);
            _menuToggle.Latched = false;
            SyncCarouselWithMenu();
        }

        /// <summary>
        /// btn_filter.toggled: the source unpresses btn_menu first, so the two
        /// drawers are never open at once.
        /// </summary>
        private void ToggleFilter()
        {
            if (_menu.IsOpen)
            {
                SetMenuClosed();
            }
            SetFilterOpen(!IsFilterOpen);
        }

        /// <summary>
        /// btn_filter.toggled. Classic: ButtonCustom's toggle_sound (SOUND_SLIDE
        /// going up, its second clip going down), the caption, the icon and
        /// show_filter played forwards or backwards.
        /// </summary>
        private void SetFilterOpen(bool open)
        {
            if (_modern)
            {
                SetModernFilterOpen(open);
                return;
            }

            if (_filterDrawer == null || _filterDrawer.IsOpen == open)
            {
                return;
            }

            PlayFilterSound(open ? AudioService.SoundFilterOpen : AudioService.SoundFilterClose);
            _filterDrawer.SetOpen(open);
            ShowFilterState(open);
        }

        /// <summary>
        /// btn_filter's pressed state, l_filter_onoff and l_filter_icon. The
        /// source writes tr("ON") if !on else tr("OFF"), so the caption keeps
        /// the scene's "OFF" while the filter is up and reads "ON" once it
        /// has been shut.
        /// </summary>
        private void ShowFilterState(bool open)
        {
            _filterToggle.Latched = open;
            _filterState.text = _lang.Get(open ? "OFF" : "ON");
            _filterIcon.sprite = _atlas.Get(open ? "hangar_filter_icon_on" : "hangar_filter_icon");
        }

        /// <summary>
        /// to_main_screen() parks the band with play("show_filter", -1, -1000,
        /// true) but leaves btn_filter pressed, so the next press there
        /// replays the closing slide from the top. The port shuts the filter
        /// as the hangar leaves screens/main and releases btn_filter with it,
        /// silently, like the source's snap.
        /// </summary>
        private void CloseFilterImmediately()
        {
            if (_modern || _filterDrawer == null)
            {
                return;
            }

            if (_filterDrawer.IsOpen)
            {
                ShowFilterState(false);
            }

            _filterDrawer.SetOpenImmediate(false);
        }

        /// <summary>Audio.play_sound2d(clip, "UI", false, 0.7 * snd_vol) for btn_filter.</summary>
        private void PlayFilterSound(string clip)
        {
            if (_bootstrap != null && _bootstrap.Audio != null)
            {
                _bootstrap.Audio.PlaySound2D(
                    clip, AudioBus.Ui, false, AudioService.ButtonClickVolume * FilterSoundVolume);
            }
        }

        private void ShowPlaceholder(string featureKey)
        {
            if (_placeholderRoutine != null)
            {
                StopCoroutine(_placeholderRoutine);
            }
            _placeholderTitle.text = _lang.Get("HANGAR_FEATURE_PLACEHOLDER");
            _placeholderHint.text = _lang.Get(featureKey) + "\n" +
                _lang.Get("HANGAR_PLACEHOLDER_HINT");
            _placeholder.gameObject.SetActive(true);
            _placeholderRoutine = StartCoroutine(HidePlaceholder());
        }

        private IEnumerator HidePlaceholder()
        {
            yield return new WaitForSecondsRealtime(2.8f);
            _placeholder.gameObject.SetActive(false);
            _placeholderRoutine = null;
        }

        // ------------------------------------------------------------------
        // screens/: the tech tree and the module shop.
        // ------------------------------------------------------------------

        /// <summary>
        /// The z_index = 1 layer belonging to one screen, created on demand.
        /// It is a sibling of the screen body rather than a child of it,
        /// because uGUI can only put a widget over the HUD band by placing it
        /// later in the tree; <see cref="ToScreen"/> keeps the two in step.
        /// </summary>
        private RectTransform ZLayer(HangarScreenId id)
        {
            RectTransform layer;
            if (!_zLayers.TryGetValue(id, out layer))
            {
                layer = UiFactory.Node("z1_" + id, _zTop);
                GodotRect.FullRect(layer);
                _zLayers[id] = layer;
            }

            return layer;
        }

        /// <summary>
        /// Fills the pre-created screens layer. Each sub-screen that lifts a
        /// widget with z_index = 1 in the source is handed its own layer in
        /// <see cref="_zTop"/> for exactly those widgets.
        /// </summary>
        private void BuildScreens()
        {
            if (_tanks == null)
            {
                _tanks = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                ModRuntime.Apply(_tanks);
                ModOwnership.Grant(_account, _tanks);
            }
            _research = new TankResearchService(_account, _tanks);
            _kit = new HangarUiKit(_atlas, _lang, _regular, _bold, _bootstrap.Audio);

            _techTree = new TechTreeScreen(_kit, _tanks, _account, _research);
            _techTree.Overlay = ZLayer(HangarScreenId.TechTree);
            _techTree.Build(_screens);
            _techTree.HomePressed = () => ToScreen(HangarScreenId.Main);
            _techTree.TankPressed = OpenModules;

            _modules = new ModulesScreen(_kit, _tanks, _account, _research);
            _modules.Overlay = ZLayer(HangarScreenId.Modules);
            _modules.Build(_screens);
            _modules.HomePressed = () => ToScreen(HangarScreenId.Main);
            _modules.TreePressed = () => ToScreen(HangarScreenId.TechTree);
            _modules.Changed = OnAccountChanged;
            _modules.Message = ShowMessage;

            _loadout = new LoadoutScreen(_kit, _tanks, _account);
            _loadout.Overlay = ZLayer(HangarScreenId.Loadout);
            _loadout.Build(_screens);
            _loadout.HomePressed = () => ToScreen(HangarScreenId.Main);
            _loadout.Changed = OnAccountChanged;
            _loadout.Message = ShowMessage;

            _shop = new ShopScreen(_kit, _tanks, _account, _bootstrap.NetworkClock, _bootstrap.Clock, _modern);
            _shop.Overlay = ZLayer(HangarScreenId.Shop);
            _shop.ModalLayer = _uiRoot;
            _shop.Build(_screens);
            _shop.HomePressed = () => ToScreen(HangarScreenId.Main);
            _shop.Changed = OnAccountChanged;
            _bootstrap.NetworkClock.RequestSync();

            _armorViewer = new ArmorViewerScreen(_kit, World);
            _armorViewer.Build(_screens);
            _armorViewer.HomePressed = () => ToScreen(HangarScreenId.Main);

            _camouflage = new CamouflageScreen(_kit, _account, World, _modern);
            _camouflage.Overlay = ZLayer(HangarScreenId.Camouflage);
            _camouflage.ModalLayer = _uiRoot;
            _camouflage.Header = _hud;
            _camouflage.Build(_screens);
            _camouflage.HomePressed = () => ToScreen(HangarScreenId.Main);
            _camouflage.Changed = () => OnAccountChanged(false);
            _camouflageService = _camouflage.Service;

            _techTree.Root.gameObject.SetActive(false);
            _modules.Root.gameObject.SetActive(false);
            _loadout.Root.gameObject.SetActive(false);
            _shop.Hide();
            _armorViewer.Root.gameObject.SetActive(false);
            _camouflage.Hide();
            foreach (var layer in _zLayers)
            {
                layer.Value.gameObject.SetActive(layer.Key == HangarScreenId.Main);
            }

            _screen = HangarScreenId.Main;
        }

        private void ToScreen(HangarScreenId id)
        {
            var leavingCamouflage = _screen == HangarScreenId.Camouflage && id != HangarScreenId.Camouflage;
            _screen = id;
            if (id != HangarScreenId.Main)
            {
                CloseFilterImmediately();
            }

            if (_main != null)
            {
                _main.gameObject.SetActive(id == HangarScreenId.Main);
            }

            // A lifted widget belongs to its screen, so it comes and goes
            // with that screen even though it is drawn above the HUD band.
            foreach (var layer in _zLayers)
            {
                layer.Value.gameObject.SetActive(layer.Key == id);
            }

            if (_hud != null)
            {
                _hud.gameObject.SetActive(id != HangarScreenId.ArmorViewer);
                foreach (Transform child in _hud)
                {
                    // The shop keeps the hangar header as it is, currencies
                    // included (Godot hid them there), and only drops its own
                    // shortcut. The shop draws its title and back arrow over it.
                    child.gameObject.SetActive(id != HangarScreenId.Shop || child.name != "btn_shop");
                }
            }

            // The source only lets the hangar tank be turned from screens/main:
            // every other screen covers it with its own blur. The port keeps the
            // hit surface at the UI root for draw order, so it is switched off
            // by hand instead, otherwise a drag on a sub-screen orbits the tank
            // behind it. The 4.8 camouflage shop leaves its slot column and
            // the open stand noInput, so the tank turns there as well.
            if (DragSurface != null)
            {
                DragSurface.gameObject.SetActive(
                    id == HangarScreenId.Main || id == HangarScreenId.Camouflage);
            }

            if (_techTree != null)
            {
                _techTree.Root.gameObject.SetActive(id == HangarScreenId.TechTree);
            }

            if (_modules != null)
            {
                _modules.Root.gameObject.SetActive(id == HangarScreenId.Modules);
            }

            if (_loadout != null)
            {
                _loadout.Root.gameObject.SetActive(id == HangarScreenId.Loadout);
            }

            if (_armorViewer != null)
            {
                _armorViewer.Root.gameObject.SetActive(id == HangarScreenId.ArmorViewer);
            }

            if (_shop != null)
            {
                if (id == HangarScreenId.Shop) { _shop.Show(); }
                else { _shop.Hide(); }
            }

            if (_camouflage != null)
            {
                if (id == HangarScreenId.Camouflage) { _camouflage.Show(); }
                else { _camouflage.Hide(); }
            }

            if (id == HangarScreenId.TechTree && _techTree != null)
            {
                _techTree.Update(CurrentNation());
            }

            if (_modern)
            {
                OnModernScreen(id);
            }

            // The shop paints whatever is picked on the stand; back in the
            // hangar the vehicle wears what is installed again.
            if (leavingCamouflage)
            {
                RefreshTankCamouflage();
            }
        }

        /// <summary>btn_camouflage: the 4.8 camouflage shop of the selected vehicle.</summary>
        private void OpenCamouflage()
        {
            if (_camouflage == null)
            {
                return;
            }

            _camouflage.Init(SelectedSave());
            ToScreen(HangarScreenId.Camouflage);
        }

        /// <summary>
        /// Paints the vehicle on the stand: the shop's pick while it is open,
        /// otherwise the camouflage the hangar shows for the save.
        /// </summary>
        private void RefreshTankCamouflage()
        {
            if (World == null)
            {
                return;
            }

            if (_screen == HangarScreenId.Camouflage && _camouflage != null)
            {
                _camouflage.ShowOnTank();
                return;
            }

            World.ShowCamouflage(CamouflageService.HangarCamouflage(CamouflageAssets.Catalog, SelectedSave()));
        }

        private void OpenModules(string tank)
        {
            if (_modules == null || _tanks == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(tank) || _tanks.Get(tank) == null)
            {
                var tree = _tanks.TreeTanks(CurrentNation());
                tank = tree.Count > 0 ? tree[0].Name : null;
            }

            if (string.IsNullOrEmpty(tank))
            {
                return;
            }

            _modules.Init(tank);
            ToScreen(HangarScreenId.Modules);
        }

        private void OpenModulesForCurrentTank()
        {
            OpenModules(_account == null ? null : _account.SelectedTankName);
        }

        /// <summary>
        /// btn_shells / btn_equipment of the source hangar: both open
        /// SCREEN_EQIPMENT and only differ in the tab they select.
        /// </summary>
        private void OpenLoadout(int tab)
        {
            if (_loadout == null || _account == null)
            {
                return;
            }

            var tank = _account.SelectedTankName;
            if (string.IsNullOrEmpty(tank) || _tanks == null || _tanks.Get(tank) == null)
            {
                return;
            }

            _loadoutTab = tab;
            _loadout.Init(tank);
            _loadout.ShowTab(tab);
            ToScreen(HangarScreenId.Loadout);
        }

        private void OpenArmorViewer()
        {
            if (_armorViewer == null || _account == null || _tanks == null)
            {
                return;
            }

            var card = _tanks.Get(_account.SelectedTankName);
            if (card == null || !_armorViewer.Init(card, SelectedSave()))
            {
                ShowMessage(_lang.Get("ARMOR_VIEWER_UNAVAILABLE"));
                return;
            }

            ToScreen(HangarScreenId.ArmorViewer);
        }

        private Nation CurrentNation()
        {
            if (_account != null && _tanks != null)
            {
                var info = _tanks.Get(_account.SelectedTankName);
                if (info != null && info.Nation != Nation.None)
                {
                    return info.Nation;
                }
            }

            return Nation.Ussr;
        }

        private void OnAccountChanged()
        {
            OnAccountChanged(true);
        }

        private void OnAccountChanged(bool rebuildCarousel)
        {
            // One walk of the garage serves the selection check and the carousel.
            var order = rebuildCarousel && _tanks != null ? CarouselOrder() : null;
            if (order != null) { EnsureSelection(order); }
            RefreshCurrencies();
            RefreshPremium();

            // update_tank_equipment() runs inside ui_hangar.update(), so every
            // purchase, every mount and every carousel pick redraws the strip.
            RefreshLoadoutStrip();
            // Mounting a turret or a gun changes the vehicle on the stand, the
            // way the client swaps the researched meshes in the hangar.
            RefreshTankModules();
            RefreshTankCamouflage();
            if (order != null) { RebuildCarousel(order); }
            if (_modern) { RefreshModernTankPanel(); }
            if (_bootstrap != null)
            {
                _bootstrap.Persist();
            }
        }

        private void RefreshCurrencies()
        {
            // ui_hangar.update() pushes every readout through
            // Util.format_number, so 330000 reads "330 000".
            SetCurrency("gold_amount", HangarUiKit.FormatNumber(_account.Gold));
            SetCurrency("credits_amount", HangarUiKit.FormatNumber(_account.Credits));
            SetCurrency("freexp_amount", HangarUiKit.FormatNumber(_account.FreeXp));
            LayoutCurrencies();
        }

        public string CurrencyAmount(string node)
        {
            var found = _hud == null ? null : FindDeep(_hud, node);
            var label = found == null ? null : found.GetComponent<Text>();
            return label == null ? string.Empty : label.text;
        }

        /// <summary>Whether a header currency readout is on screen right now.</summary>
        public bool CurrencyShown(string node)
        {
            var found = _hud == null ? null : FindDeep(_hud, node);
            return found != null && found.gameObject.activeInHierarchy;
        }

        public string PremiumText
        {
            get { return _premiumLabel == null ? string.Empty : _premiumLabel.text; }
        }

        private void SetCurrency(string node, string amount)
        {
            if (_hud == null)
            {
                return;
            }

            var found = FindDeep(_hud, node);
            if (found == null)
            {
                return;
            }

            var label = found.GetComponent<Text>();
            if (label != null)
            {
                label.text = amount;
            }
        }

        private void RefreshPremium()
        {
            if (_account == null) { return; }
            var now = _bootstrap != null && _bootstrap.Clock != null
                ? _bootstrap.Clock.NowUnixSeconds : 0d;
            // The choice is polled with the schedule and the premium status, so
            // the event hangar comes up and goes away at local midnight and a
            // premium hangar falls back when the premium runs out, without a
            // reload. The Modern menus draw no premium label, so the stand is
            // updated before the label is looked at.
            RefreshHangarKind();
            if (_premiumLabel == null) { return; }
            var hours = (int)Math.Ceiling(Math.Max(
                0d, _account.PremAccountExpiration - now) / 3600d);
            _premiumLabel.text = _lang.Get("HANGAR/PREM_REM")
                .Replace("%s", HangarUiKit.FormatNumber(hours));
        }

        /// <summary>The hangar picked in the settings, judged against this screen's account.</summary>
        public HangarChoiceContext HangarChoice
        {
            get
            {
                if (_hangarChoice == null)
                {
                    _hangarChoice = new HangarChoiceContext(
                        () => _bootstrap != null && _bootstrap.Clock != null
                            ? _bootstrap.Clock.NowUnixSeconds : 0d,
                        () => _account != null && _account.HasPremium(
                            _bootstrap != null && _bootstrap.Clock != null
                                ? _bootstrap.Clock.NowUnixSeconds : 0d));
                }

                return _hangarChoice;
            }
        }

        private HangarChoiceContext _hangarChoice;

        /// <summary>Puts up the hangar the settings and the account call for, if it is not up yet.</summary>
        public void RefreshHangarKind()
        {
            if (World != null && _bootstrap != null)
            {
                World.SetKind(HangarChoice.Current(_bootstrap.Settings));
            }
        }

        /// <summary>Reuses the placeholder toast for research refusals.</summary>
        private void ShowMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            if (_placeholderRoutine != null)
            {
                StopCoroutine(_placeholderRoutine);
            }

            _placeholderTitle.text = message;
            _placeholderHint.text = string.Empty;
            _placeholder.gameObject.SetActive(true);
            _placeholderRoutine = StartCoroutine(HidePlaceholder());
        }

        private float _premiumRefreshAt;
        private void Update()
        {
            // Runs before any overlay early return: expiry cannot wait for the
            // next purchase/carousel update, even with Settings open.
            if (Time.unscaledTime >= _premiumRefreshAt)
            {
                _premiumRefreshAt = Time.unscaledTime + 1f;
                RefreshPremium();
            }

            if (_screen == HangarScreenId.Shop && _shop != null) { _shop.Tick(); }
            if (_screen == HangarScreenId.Camouflage && _camouflage != null)
            {
                _camouflage.Tick(Time.unscaledDeltaTime);
            }

            // The settings screen swallows the menu action while it is up,
            // exactly like the Godot Control that handles it in _gui_input.
            if (SettingsUi != null || SandboxUi != null)
            {
                return;
            }

            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                if (_modern && HandleModernEscape())
                {
                    return;
                }

                if (_placeholder.gameObject.activeSelf)
                {
                    _placeholder.gameObject.SetActive(false);
                }
                else if (_screen == HangarScreenId.Shop)
                {
                    if (_shop == null || !_shop.HandleEscape()) { ToScreen(HangarScreenId.Main); }
                }
                else if (_screen == HangarScreenId.Camouflage)
                {
                    if (_camouflage == null || !_camouflage.HandleEscape()) { ToScreen(HangarScreenId.Main); }
                }
                else if (_screen == HangarScreenId.Loadout)
                {
                    ToScreen(HangarScreenId.Main);
                }
                else if (_screen == HangarScreenId.Modules)
                {
                    ToScreen(HangarScreenId.TechTree);
                }
                else if (_screen == HangarScreenId.TechTree)
                {
                    ToScreen(HangarScreenId.Main);
                }
                else if (IsFilterOpen)
                {
                    SetFilterOpen(false);
                }
                else
                {
                    _menu.Toggle();
                    if (_modern)
                    {
                        // Modern keeps btn_menu and the carousel in step with
                        // the drawer; Classic stays as the source left it.
                        _menuToggle.Latched = _menu.IsOpen;
                        SyncCarouselWithMenu();
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Widget helpers.
        // ------------------------------------------------------------------

        private Button StyleButton(string name, Transform parent, string styleRegion)
        {
            var sprite = string.IsNullOrEmpty(styleRegion)
                ? null
                : _atlas.Get(styleRegion);
            var button = UiFactory.TextButton(name, parent, sprite, _bold, 0, null);
            var image = button.GetComponent<Image>();
            image.color = sprite == null ? new Color(0f, 0f, 0f, 0.001f) : Color.white;
            image.type = sprite != null && sprite.border.sqrMagnitude > 0f
                ? Image.Type.Sliced
                : Image.Type.Simple;
            var pressedSprite = string.IsNullOrEmpty(styleRegion)
                ? null
                : _atlas.Get(styleRegion + UiTheme.PressedRegionSuffix);
            if (pressedSprite != null)
            {
                GodotButtonState.Of(button)
                    .SetPressedStyle(pressedSprite, UiTheme.ButtonPressTint);
            }

            return button;
        }

        /// <summary>
        /// Godot Buttons draw their icon centred and clamp it to the theme's
        /// icon_max_width, keeping the aspect ratio. Drawing the atlas region at
        /// its native size instead is what made every hangar icon oversized.
        /// </summary>
        private void AddIcon(Button button, string region, float iconMaxWidth)
        {
            var sprite = _atlas.Get(region);
            var icon = UiFactory.Picture("icon", button.transform, sprite);
            icon.preserveAspect = true;
            var rt = icon.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = IconSize(sprite, iconMaxWidth);
        }

        public static Vector2 IconSize(Sprite sprite, float iconMaxWidth)
        {
            if (sprite == null)
            {
                return Vector2.zero;
            }

            var width = sprite.rect.width;
            var height = sprite.rect.height;
            if (iconMaxWidth > 0f && width > iconMaxWidth)
            {
                height *= iconMaxWidth / width;
                width = iconMaxWidth;
            }
            return new Vector2(width, height);
        }

        private void AddButtonLabel(Button button, string text, int fontSize)
        {
            var label = Label("label", button.transform, fontSize,
                TextAnchor.MiddleCenter, text, _bold, UiTheme.ButtonFont);
            GodotRect.FullRect(label.rectTransform);
        }

        private Text Label(
            string name,
            Transform parent,
            int size,
            TextAnchor alignment,
            string text,
            Font font,
            Color color)
        {
            var label = UiFactory.Label(name, parent, font, size, color, alignment, text);
            UiFactory.AddShadow(label, new Color(0f, 0f, 0f, 0.8f));
            return label;
        }

        /// <summary>
        /// btn_settings of the hangar drawer. ui_hangar.gd adds
        /// ui_settings.tscn on top of the hangar and the screen's own _ready()
        /// hides Global.hangar_ui; btn_menu shows it again and saves.
        /// </summary>
        private void OpenSettings()
        {
            if (SettingsUi != null)
            {
                return;
            }

            // Global.hangar_ui.hide() leaves the drawer as it was, so it is
            // still open when the settings screen hands the hangar back.
            _uiRoot.gameObject.SetActive(false);
            var style = _bootstrap.Settings.UiStyle;
            var carouselSize = _bootstrap.Settings.UiCarouselSize;
            SettingsUi = SettingsScreen.Open(
                transform,
                _bootstrap.Settings,
                SettingsSortingOrder,
                MouseLock.IsMobile,
                () =>
                {
                    SettingsUi = null;
                    _uiRoot.gameObject.SetActive(true);
                    _lang.Language = _bootstrap.Settings.Language;
                    RedrawCarouselCards();
                    if (_bootstrap != null)
                    {
                        _bootstrap.Persist();
                        // A new menu style or carousel size takes effect by
                        // rebuilding the hangar, exactly as after a battle.
                        if (_bootstrap.Settings.UiStyle != style ||
                            _bootstrap.Settings.UiCarouselSize != carouselSize)
                        {
                            _bootstrap.RebuildHangar();
                        }
                    }
                },
                HangarChoice);
            // A hangar picked in the list comes up behind the screen at once.
            SettingsUi.SettingChanged = setting =>
            {
                if (setting == "hangar")
                {
                    RefreshHangarKind();
                }
            };
        }

        /// <summary>
        /// btn_logout of the hangar drawer: Global.create_popup(tr("CONFIRM"),
        /// tr("popup:logout"), false, Global.to_login).
        /// </summary>
        private void ConfirmLogout()
        {
            var parent = _uiRoot != null ? _uiRoot : _main;
            if (LogoutPopup != null || parent == null || _bootstrap == null)
            {
                return;
            }

            LogoutPopup = GenericPopup.Create(parent, _atlas, _lang, _bold, _regular,
                "CONFIRM", "popup:logout", false, () => _bootstrap.LogOut(this));
            LogoutPopup.Dismissed = () => LogoutPopup = null;
        }

        /// <summary>
        /// btn_mods of the hangar drawer. The installer is opened the same way
        /// as the settings screen -- over a hidden hangar, on the same sorting
        /// layer -- so the two behave identically from the drawer.
        /// </summary>
        private void OpenMods()
        {
            if (ModsUi != null)
            {
                return;
            }

            _uiRoot.gameObject.SetActive(false);
            ModsUi = ModsScreen.Open(
                transform,
                SettingsSortingOrder,
                MouseLock.IsMobile,
                () =>
                {
                    ModsUi = null;
                    _uiRoot.gameObject.SetActive(true);
                    RefreshMods();
                });
        }

        /// <summary>
        /// Picks up installed, removed and toggled mods without a restart:
        /// rereads the mods folder, swaps the mod cards inside the live tank
        /// table and redraws the garage.
        /// </summary>
        private void RefreshMods()
        {
            ModRuntime.Forget();
            _tanks.RemoveMods();
            ModRuntime.Apply(_tanks);
            ModOwnership.Grant(_account, _tanks);
            _playable.Clear();
            OnAccountChanged(true);
        }

        private void BindPlaceholder(
            Button button, string featureKey, string sound = AudioService.SoundEnter)
        {
            PlaceholderButtonCount++;
            Bind(button, () => ShowPlaceholder(featureKey), sound);
        }

        private void Bind(Button button, Action action, string sound)
        {
            button.onClick.AddListener(() =>
            {
                _bootstrap.Audio.PlayButtonClick(sound);
                action();
            });
        }

        // ------------------------------------------------------------------
        // Godot global rect -> RectTransform, one helper per anchor preset.
        // ------------------------------------------------------------------

        private static void TopLeft(RectTransform rt, float x, float y, float w, float h)
        {
            GodotRect.Apply(rt, 0f, 0f, 0f, 0f, x, y, x + w, y + h, GodotRect.TopLeftPivot);
        }

        private static void TopStretch(RectTransform rt, float x, float y, float w, float h)
        {
            GodotRect.Apply(rt, 0f, 0f, 1f, 0f,
                x, y, x + w - CanvasWidth, y + h, GodotRect.TopLeftPivot);
        }

        private static void TopRight(RectTransform rt, float x, float y, float w, float h)
        {
            GodotRect.Apply(rt, 1f, 0f, 1f, 0f,
                x - CanvasWidth, y, x + w - CanvasWidth, y + h, GodotRect.TopLeftPivot);
        }

        private static void BottomLeft(RectTransform rt, float x, float y, float w, float h)
        {
            GodotRect.Apply(rt, 0f, 1f, 0f, 1f,
                x, y - CanvasHeight, x + w, y + h - CanvasHeight, GodotRect.TopLeftPivot);
        }

        private static void BottomRight(RectTransform rt, float x, float y, float w, float h)
        {
            GodotRect.Apply(rt, 1f, 1f, 1f, 1f,
                x - CanvasWidth, y - CanvasHeight,
                x + w - CanvasWidth, y + h - CanvasHeight, GodotRect.TopLeftPivot);
        }

        private static void BottomStretch(RectTransform rt, float x, float y, float w, float h)
        {
            GodotRect.Apply(rt, 0f, 1f, 1f, 1f,
                x, y - CanvasHeight, x + w - CanvasWidth, y + h - CanvasHeight,
                GodotRect.TopLeftPivot);
        }

        private static void BottomCenter(RectTransform rt, float x, float y, float w, float h)
        {
            GodotRect.Apply(rt, 0.5f, 1f, 0.5f, 1f,
                x - (CanvasWidth * 0.5f), y - CanvasHeight,
                x + w - (CanvasWidth * 0.5f), y + h - CanvasHeight,
                GodotRect.TopLeftPivot);
        }

        private static RectTransform CreateCanvas(Transform parent)
        {
            var go = new GameObject("Canvas", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.pixelPerfect = false;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution =
                new Vector2(UiLayout.ReferenceWidth, UiLayout.ReferenceHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            scaler.referencePixelsPerUnit = 100f;
            MenuUiScale.Register(scaler, scaler.referenceResolution);
            go.AddComponent<GraphicRaycaster>();
            return (RectTransform)go.transform;
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            var input = go.AddComponent<InputSystemUIInputModule>();
            input.AssignDefaultActions();
        }
    }
}
