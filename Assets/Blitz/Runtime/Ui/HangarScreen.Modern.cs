using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Mods;
using BlitzLegacy.Runtime.Tanks;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The Modern layout of the hangar. It reuses every behaviour of the
    /// classic screen (selection, sub-screens, loadout strip, drawers,
    /// account wiring) and only builds a different front: a slim top bar,
    /// a navigation rail, a tank panel, an action row and a carousel that
    /// always stays on the bottom edge. Sub-screens fold the carousel into a
    /// handle that can be pulled up to switch tanks without leaving them.
    /// </summary>
    public sealed partial class HangarScreen
    {
        private bool _modern;
        private int _loadoutTab = LoadoutScreen.TabAmmo;
        private CarouselMetrics _metrics;
        private CarouselMode _carouselMode = CarouselMode.Normal;

        /// <summary>
        /// The bootstrap's carousel filter. It belongs to the session, so the
        /// hangar rebuilt after a battle or a style switch keeps it.
        /// </summary>
        private CarouselFilter _filter;

        private RectTransform _carouselLayer;
        private RectTransform _carouselBar;
        private Canvas _carouselCanvas;
        private GraphicRaycaster _carouselRaycaster;
        private CanvasGroup _carouselGroup;
        /// <summary>Where the bar is now: 0 = folded below the edge, 1 = fully up.</summary>
        private float _carouselShown = 1f;
        private RectTransform _carouselHandle;
        private Image _handleChevron;
        private Vector2 _barHome;
        private Vector2 _handleHome;
        private Coroutine _carouselTween;
        private Text _carouselEmpty;
        private Button _modernFilterButton;
        private bool _modernFilterOpen;
        private ModernFade _filterPanelFade;
        private ModernFade _chromeFade;

        /// <summary>One filter toggle: its button, its outline and what it reads.</summary>
        private sealed class ModernFilterToggle
        {
            public Button Button;
            public GameObject Selected;
            public Func<bool> IsOn;
        }

        private readonly List<ModernFilterToggle> _filterButtons = new List<ModernFilterToggle>();

        private Image _panelLevel;
        private Image _panelClass;
        private Image _panelNation;
        private Text _panelName;
        private Text _researchExp;
        private readonly Text[] _panelStats = new Text[5];

        private static readonly string[] StatKeys =
        {
            "ui:stat/hp", "ui:stat/damage", "ui:stat/penetration", "ui:stat/reload", "ui:stat/speed",
        };

        /// <summary>True when this hangar was built with the Modern layout.</summary>
        public bool IsModern
        {
            get { return _modern; }
        }

        /// <summary>How the Modern carousel is drawn right now.</summary>
        public CarouselMode CarouselState
        {
            get { return _carouselMode; }
        }

        /// <summary>The carousel filter, in either style.</summary>
        public CarouselFilter Filter
        {
            get { return _filter; }
        }

        /// <summary>
        /// True while the carousel bar is drawn. A folded or hidden bar keeps
        /// its GameObject active and only switches its canvas off.
        /// </summary>
        public bool CarouselBarDrawn
        {
            get { return _carouselCanvas != null && _carouselCanvas.enabled; }
        }

        /// <summary>The carousel bar node itself (null in Classic).</summary>
        public RectTransform CarouselBar
        {
            get { return _carouselBar; }
        }

        /// <summary>The Modern filter pop-up's fade (null in Classic).</summary>
        public ModernFade FilterPanelFade
        {
            get { return _filterPanelFade; }
        }

        /// <summary>
        /// The Modern hangar chrome that fades out while the filter is up
        /// over the main screen (null in Classic).
        /// </summary>
        public ModernFade ChromeFade
        {
            get { return _chromeFade; }
        }

        /// <summary>True while the burger drawer is open or sliding open.</summary>
        public bool IsMenuOpen
        {
            get { return _menu != null && _menu.IsOpen; }
        }

        // ------------------------------------------------------------------
        // Build.
        // ------------------------------------------------------------------

        private void BuildModern(RectTransform main)
        {
            var aspect = MenuStyle.ScreenAspect;
            _metrics = MenuStyleRules.Metrics(_bootstrap.Settings.UiCarouselSize, aspect);

            BuildModernTopBar(_hud);
            BuildModernHeader(ZLayer(HangarScreenId.Main));
            BuildModernRail(main);
            BuildModernTankPanel(main);
            BuildModernActions(main);
            BuildMenu(main);
            BuildPlaceholder(main);
            BuildModernCarousel(_uiRoot);
            BuildChromeFade(main);
            RefreshModernTankPanel();
            SetCarouselMode(CarouselMode.Normal, false);
        }

        private void BuildModernTopBar(RectTransform hud)
        {
            var bar = UiFactory.Picture("panel_main_background", hud, ModernSprites.VerticalGradient(
                new Color(0.02f, 0.025f, 0.035f, 0.94f), new Color(0.02f, 0.025f, 0.035f, 0.62f)));
            ModernUi.TopBand(bar.rectTransform, 0f, ModernTheme.TopBarHeight);

            var accent = ModernTheme.Accent;
            var line = ModernUi.Strip("ThinRedLine", hud, new Color(accent.r, accent.g, accent.b, 0.55f));
            ModernUi.TopBand(line.rectTransform, ModernTheme.TopBarHeight, 1.5f);

            var shop = StyleButton("btn_shop", hud, "button_small_green");
            ModernUi.TopRight((RectTransform)shop.transform, 14f, 10f, 64f, 56f);
            AddIcon(shop, "hangar_shop", 40f);
            shop.gameObject.AddComponent<PressFeedback>();
            Bind(shop, () => ToScreen(HangarScreenId.Shop), AudioService.SoundEnter);

            var row = UiFactory.Node("currencies", hud);
            row.anchorMin = new Vector2(1f, 1f);
            row.anchorMax = new Vector2(1f, 1f);
            row.pivot = new Vector2(1f, 1f);
            row.anchoredPosition = new Vector2(-(14f + 64f + 10f), -14f);
            row.sizeDelta = new Vector2(0f, 48f);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.MiddleRight;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            var fitter = row.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

            CurrencyPill(row, "gold", "hangar_gold", HangarUiKit.FormatNumber(_account.Gold));
            CurrencyPill(row, "credits", "hangar_credits", HangarUiKit.FormatNumber(_account.Credits));
            CurrencyPill(row, "freexp", "hangar_freexp", HangarUiKit.FormatNumber(_account.FreeXp));
        }

        /// <summary>One currency: the original icon and the amount in a glass pill.</summary>
        private void CurrencyPill(RectTransform row, string name, string iconRegion, string amount)
        {
            var pill = ModernUi.Panel(name, row, 12, new Color(1f, 1f, 1f, 0.05f), true);
            var layout = pill.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(10, 14, 6, 6);
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var sprite = _atlas.Get(iconRegion);
            var icon = UiFactory.Picture(name + "_icon", pill.transform, sprite);
            icon.preserveAspect = true;
            var size = ModernUi.Fit(sprite, 30f, 30f);
            var element = icon.gameObject.AddComponent<LayoutElement>();
            element.preferredWidth = size.x;
            element.preferredHeight = size.y;

            var value = ModernUi.Label(name + "_amount", pill.transform, _bold, 22,
                ModernTheme.TextPrimary, TextAnchor.MiddleRight, amount);
            value.gameObject.AddComponent<LayoutElement>().minWidth = 36f;
        }

        private void BuildModernHeader(RectTransform top)
        {
            var menu = StyleButton("btn_menu", top, "button_small_generic");
            ModernUi.TopLeft((RectTransform)menu.transform, HangarUiKit.ModernTopButtonX, HangarUiKit.ModernTopButtonY,
                HangarUiKit.ModernTopButtonWidth, HangarUiKit.ModernTopButtonHeight);
            AddIcon(menu, "hangar_menu", HangarUiKit.ModernTopButtonIcon);
            menu.gameObject.AddComponent<PressFeedback>();
            Bind(menu, ToggleMenu, AudioService.SoundFilterOpen);
            _menuToggle = GodotButtonState.Of(menu);
            _menuToggle.ToggleMode = true;

            var account = StyleButton("btn_acc", top, null);
            ModernUi.TopLeft((RectTransform)account.transform, 84f, 8f, 300f, 60f);
            Bind(account, _bootstrap.OpenAccountStatistics, AudioService.SoundEnter);

            var nickname = ModernUi.Label("nickname", top, _bold, 24, ModernTheme.TextPrimary,
                TextAnchor.UpperLeft, _account.Name);
            ModernUi.TopLeft(nickname.rectTransform, 88f, 11f, 290f, 30f);

            _premiumLabel = ModernUi.Label("premium_acc", top, _regular, 16, PremiumColor,
                TextAnchor.UpperLeft, string.Empty);
            ModernUi.TopLeft(_premiumLabel.rectTransform, 88f, 43f, 300f, 22f);
            RefreshPremium();
        }

        private void BuildModernRail(RectTransform main)
        {
            var y = ModernTheme.TopBarHeight + 16f;
            const float step = 64f;

            RailButton(main, "btn_techTree", "hangar_techtree", _lang.Get("TECH TREE"), y,
                () => ToScreen(HangarScreenId.TechTree));
            y += step;

            var research = RailButton(main, "btn_research", "hangar_research", _lang.Get("RESEARCH"), y,
                OpenModulesForCurrentTank);
            var label = research.transform.Find("label");
            GodotRect.Apply((RectTransform)label, 0f, 0f, 1f, 0f, 62f, 6f, -8f, 30f);
            var expIcon = UiFactory.Picture("exp_icon", research.transform, _atlas.Get("hangar_exp"));
            expIcon.preserveAspect = true;
            ModernUi.TopLeft(expIcon.rectTransform, 62f, 32f, 18f, 18f);
            _researchExp = ModernUi.Label("curtank_exp", research.transform, _bold, 15,
                ModernTheme.TextSecondary, TextAnchor.MiddleLeft, "0");
            ModernUi.TopLeft(_researchExp.rectTransform, 84f, 31f, 110f, 20f);
            y += step;

            // No loadout entry here: the shell/equipment strip above the
            // carousel already opens the loadout screen on each of its tabs.
            RailButton(main, "btn_armor_viewer", "equipment_spallliner", _lang.Get("ARMOR_VIEWER"), y, OpenArmorViewer);
            y += step;

            var crew = RailButton(main, "btn_crew", "hangar_crew", _lang.Get("CREW"), y, null);
            BindPlaceholder(crew, "CREW");
            y += step;
            var news = RailButton(main, "btn_notifs", "hangar_news", _lang.Get("NEWS"), y, null);
            BindPlaceholder(news, "NEWS");
        }

        private Button RailButton(
            RectTransform parent, string name, string iconRegion, string text, float y, Action action)
        {
            var button = StyleButton(name, parent, "button_small_generic");
            ModernUi.TopLeft((RectTransform)button.transform, 14f, y, 200f, 56f);
            button.gameObject.AddComponent<PressFeedback>();

            if (!string.IsNullOrEmpty(iconRegion))
            {
                var icon = ModernUi.Icon(button.transform, _atlas.Get(iconRegion), 38f, 30f);
                icon.rectTransform.anchorMin = new Vector2(0f, 0.5f);
                icon.rectTransform.anchorMax = new Vector2(0f, 0.5f);
                icon.rectTransform.anchoredPosition = new Vector2(32f, 0f);
            }
            else
            {
                var dot = ModernUi.Strip("marker", button.transform, ModernTheme.Accent);
                GodotRect.Apply(dot.rectTransform, 0f, 0.5f, 0f, 0.5f, 28f, -9f, 32f, 9f);
            }

            var label = ModernUi.Label("label", button.transform, _bold, 17, ModernTheme.TextPrimary,
                TextAnchor.MiddleLeft, text);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 11;
            label.resizeTextMaxSize = 17;
            GodotRect.Apply(label.rectTransform, 0f, 0f, 1f, 1f, 62f, 4f, -8f, -4f);

            if (action != null)
            {
                Bind(button, action, AudioService.SoundEnter);
            }

            return button;
        }

        /// <summary>The tank panel's height; the music panel stacks under it.</summary>
        private const float ModernTankPanelHeight = 244f;

        private void BuildModernTankPanel(RectTransform main)
        {
            var panel = ModernUi.Panel("tank_panel", main);
            var rt = panel.rectTransform;
            ModernUi.TopRight(rt, 16f, ModernTheme.TopBarHeight + 16f, 340f, ModernTankPanelHeight);

            _panelLevel = UiFactory.Picture("lvl", rt, null);
            _panelLevel.preserveAspect = true;
            ModernUi.TopLeft(_panelLevel.rectTransform, 16f, 16f, 30f, 26f);
            _panelClass = UiFactory.Picture("class", rt, null);
            _panelClass.preserveAspect = true;
            ModernUi.TopLeft(_panelClass.rectTransform, 50f, 16f, 26f, 26f);
            _panelNation = UiFactory.Picture("nation", rt, null);
            _panelNation.preserveAspect = true;
            ModernUi.TopRight(_panelNation.rectTransform, 14f, 12f, 56f, 34f);

            _panelName = ModernUi.Label("name", rt, _bold, 28, ModernTheme.TextPrimary,
                TextAnchor.MiddleLeft, string.Empty);
            _panelName.horizontalOverflow = HorizontalWrapMode.Wrap;
            _panelName.resizeTextForBestFit = true;
            _panelName.resizeTextMinSize = 16;
            _panelName.resizeTextMaxSize = 28;
            ModernUi.TopLeft(_panelName.rectTransform, 16f, 48f, 308f, 36f);

            var divider = ModernUi.Strip("divider", rt, ModernTheme.Stroke);
            ModernUi.TopLeft(divider.rectTransform, 16f, 92f, 308f, 1f);

            for (var i = 0; i < StatKeys.Length; i++)
            {
                var y = 102f + i * 27f;
                var caption = ModernUi.Label("stat_caption" + i, rt, _regular, 17,
                    ModernTheme.TextSecondary, TextAnchor.MiddleLeft, _lang.Get(StatKeys[i]));
                ModernUi.TopLeft(caption.rectTransform, 16f, y, 190f, 25f);
                _panelStats[i] = ModernUi.Label("stat_value" + i, rt, _bold, 18,
                    ModernTheme.TextPrimary, TextAnchor.MiddleRight, "-");
                ModernUi.TopLeft(_panelStats[i].rectTransform, 190f, y, 134f, 25f);
            }
        }

        /// <summary>The strip's first plate sits this far from the left edge.</summary>
        private const float ModernStripInset = 8f;

        /// <summary>The glass around the camouflage slot, on either side.</summary>
        private const float ModernCamouflagePlateMargin = 7f;

        /// <summary>The height of the battle row, the controls under the Modern music panel.</summary>
        private const float ModernBattleHeight = 77f;

        private void BuildModernActions(RectTransform main)
        {
            var bottom = _metrics.BarHeight + ModernTheme.Gap;

            // The classic strip is reused as is (shell and equipment widgets,
            // the three hit areas, RefreshLoadoutStrip); only its frame moves
            // to the left of the action row and gets glass plates.
            BuildLoadoutStrip(main);
            var strip = (RectTransform)main.Find("tank_equipment");
            if (strip != null)
            {
                // The camouflage slot leads the row, so the strip starts one
                // plate further left and that slot gets a narrow plate of its own.
                var camouflagePlate = CamouflageSlotX - ModernCamouflagePlateMargin;
                ModernUi.BottomLeft(strip, ModernStripInset - camouflagePlate, bottom, CanvasWidth, StripHeight);
                var groups = new[] { camouflagePlate, 340f, 555f, 770f };
                var widths = new[] { CamouflageSlotSize + (2f * ModernCamouflagePlateMargin), 205f, 205f, 205f };
                for (var i = groups.Length - 1; i >= 0; i--)
                {
                    var plate = ModernUi.Panel("plate" + i, strip, ModernTheme.Radius, ModernTheme.Glass, true);
                    ModernUi.TopLeft(plate.rectTransform, groups[i], -2f, widths[i], 68f);
                    plate.rectTransform.SetSiblingIndex(0);
                }
            }

            if (_stripCamouflageCaption != null)
            {
                // The slot's plate is narrower than its caption, so the caption
                // titles the plate from its left edge instead of hanging past
                // both sides of it at the screen edge.
                var unit = CamouflageSlotSize / CamouflageSourceSlot;
                _stripCamouflageCaption.alignment = TextAnchor.MiddleLeft;
                TopLeft(
                    _stripCamouflageCaption.rectTransform, -ModernCamouflagePlateMargin, CamouflageCaptionTop * unit,
                    CamouflageCaptionWidth * unit, CamouflageCaptionHeight * unit);
            }

            // The battle button keeps its classic plate, label, size and sound;
            // only its place changes.
            var battle = StyleButton("btn_battle", main, "hangar_play");
            ModernUi.BottomRight((RectTransform)battle.transform, 16f, bottom, 222f, ModernBattleHeight);
            AddButtonLabel(battle, _lang.Get("BATTLE!"), 38);
            Bind(battle, () => _bootstrap.EnterBattle(this), AudioService.SoundBattle);

            var mode = StyleButton("btn_battle_mode", main, "button_small_generic");
            ModernUi.BottomRight((RectTransform)mode.transform, 16f + 222f + 10f, bottom, 210f, ModernBattleHeight);
            mode.gameObject.AddComponent<PressFeedback>();
            var modeName = ModernUi.Label("mode_name", mode.transform, _bold, 20, ModernTheme.TextPrimary,
                TextAnchor.MiddleCenter, _lang.Get(Core.Battle.BattleModeCatalog.NameKey(_bootstrap.SelectedMode)));
            ModernUi.TopLeft(modeName.rectTransform, 8f, 10f, 194f, 34f);
            var modeCaption = ModernUi.Label("caption", mode.transform, _regular, 16, ModernTheme.Accent,
                TextAnchor.MiddleCenter, _lang.Get(_bootstrap.OnlineSelected ? "online:toggle/on" : "MODE"));
            ModernUi.TopLeft(modeCaption.rectTransform, 8f, 44f, 194f, 22f);
            _modeName = modeName;
            _modeCaption = modeCaption;
            Bind(mode, () => OpenModePicker(), AudioService.SoundEnter);
            BuildModernSandboxButton(main, 16f + 222f + 10f + 210f + 10f, bottom);
            RefreshModeButton();
        }

        // ------------------------------------------------------------------
        // Carousel.
        // ------------------------------------------------------------------

        private void BuildModernCarousel(RectTransform root)
        {
            _carouselLayer = UiFactory.Node("modern_carousel", root);
            GodotRect.FullRect(_carouselLayer);

            _carouselBar = UiFactory.Node("carousel_bar", _carouselLayer);
            ModernUi.BottomBand(_carouselBar, 0f, _metrics.BarHeight);
            // The bar is its own canvas and stays active while folded: only
            // the canvas stops drawing. Deactivating it made the pull-up
            // re-enable every card (layout, text meshes, masks) in the frame
            // the slide started, and that hitch jumped the slide to its end.
            _carouselCanvas = _carouselBar.gameObject.AddComponent<Canvas>();
            _carouselRaycaster = _carouselBar.gameObject.AddComponent<GraphicRaycaster>();
            _carouselGroup = _carouselBar.gameObject.AddComponent<CanvasGroup>();

            var background = UiFactory.Picture("footer_lol", _carouselBar, ModernSprites.VerticalGradient(
                new Color(0.03f, 0.035f, 0.045f, 0.84f), new Color(0.015f, 0.02f, 0.025f, 0.96f)));
            background.raycastTarget = true;
            GodotRect.FullRect(background.rectTransform);
            var line = ModernUi.Strip("line", _carouselBar, ModernTheme.Stroke);
            ModernUi.TopBand(line.rectTransform, 0f, 1f);

            var cardTop = (_metrics.BarHeight - _metrics.CardHeight) * 0.5f;
            var filter = StyleButton("btn_filter", _carouselBar, "button_small_generic");
            ModernUi.TopLeft((RectTransform)filter.transform, 10f, cardTop, 70f, _metrics.CardHeight);
            filter.gameObject.AddComponent<PressFeedback>();
            _filterIcon = ModernUi.Icon(filter.transform, _atlas.Get("hangar_filter_icon"), 34f, 24f);
            _filterIcon.rectTransform.anchoredPosition = new Vector2(0f, 10f);
            var filterCaption = ModernUi.Label("Label", filter.transform, _regular, 14,
                ModernTheme.TextSecondary, TextAnchor.MiddleCenter, _lang.Get("ui:carousel/filter"));
            GodotRect.Apply(filterCaption.rectTransform, 0f, 1f, 1f, 1f, 2f, -30f, -2f, -8f);
            _filterState = ModernUi.Label("filter_onoff", filter.transform, _bold, 15,
                ModernTheme.Accent, TextAnchor.UpperRight, string.Empty);
            GodotRect.Apply(_filterState.rectTransform, 0f, 0f, 1f, 0f, 2f, 3f, -7f, 22f);
            Bind(filter, ToggleFilter, AudioService.SoundFilterOpen);
            _filterToggle = GodotButtonState.Of(filter);
            _filterToggle.ToggleMode = true;
            _modernFilterButton = filter;

            var viewport = UiFactory.Node("ScrollContainer", _carouselBar);
            GodotRect.Apply(viewport, 0f, 0f, 1f, 1f, 90f, 0f, -10f, 0f);
            viewport.gameObject.AddComponent<RectMask2D>();
            var hit = viewport.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0.001f);

            CarouselRoot = UiFactory.Node("tank_carousel", viewport);
            CarouselRoot.anchorMin = new Vector2(0f, 0.5f);
            CarouselRoot.anchorMax = new Vector2(0f, 0.5f);
            CarouselRoot.pivot = new Vector2(0f, 0.5f);
            CarouselRoot.anchoredPosition = Vector2.zero;
            CarouselRoot.sizeDelta = new Vector2(0f, _metrics.CardHeight);

            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = CarouselRoot;
            scroll.viewport = viewport;
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 20f;
            _strip = CarouselStrip.Attach(
                scroll, _metrics.CardWidth, _metrics.Spacing, CreateModernTankCard, BindTankCard);

            _carouselEmpty = ModernUi.Label("empty", viewport, _regular, 18, ModernTheme.TextSecondary,
                TextAnchor.MiddleCenter, _lang.Get("ui:carousel/empty"));
            GodotRect.FullRect(_carouselEmpty.rectTransform);
            _carouselEmpty.gameObject.SetActive(false);

            BuildCarouselHandle();
            BuildModernFilterPanel();

            _barHome = _carouselBar.anchoredPosition;
            _handleHome = _carouselHandle.anchoredPosition;
            RebuildCarousel();
        }

        private void BuildCarouselHandle()
        {
            var sprite = ModernSprites.RoundedRect(ModernTheme.Radius, ModernTheme.GlassTop,
                ModernTheme.GlassStrong, ModernTheme.StrokeStrong, 1.5f);
            var handle = UiFactory.TextButton("carousel_handle", _carouselLayer, sprite, _bold, 0, null);
            handle.GetComponent<Image>().type = Image.Type.Sliced;
            handle.gameObject.AddComponent<PressFeedback>();
            _carouselHandle = (RectTransform)handle.transform;
            var half = MenuStyleRules.HandleWidth * 0.5f;
            GodotRect.Apply(_carouselHandle, 0.5f, 1f, 0.5f, 1f,
                -half, -MenuStyleRules.HandleHeight, half, ModernTheme.Radius, GodotRect.TopLeftPivot);

            _handleChevron = UiFactory.Picture("chevron", _carouselHandle,
                ModernSprites.Chevron(true, ModernTheme.Accent));
            GodotRect.Apply(_handleChevron.rectTransform, 0f, 0f, 0f, 0f, 16f, 8f, 40f, 23f);

            var label = ModernUi.Label("label", _carouselHandle, _bold, 16, ModernTheme.TextPrimary,
                TextAnchor.MiddleCenter, _lang.Get("ui:carousel/vehicles"));
            GodotRect.Apply(label.rectTransform, 0f, 0f, 1f, 0f, 40f, 0f, -16f, MenuStyleRules.HandleHeight);

            Bind(handle, ToggleCarouselExpanded, AudioService.SoundFilterOpen);
        }

        /// <summary>The Modern filter's outer padding.</summary>
        private const float FilterPad = 10f;

        /// <summary>The gap between a frame and the toggles in it.</summary>
        private const float FilterFramePad = 5f;

        /// <summary>The gap between two frames, across and down.</summary>
        private const float FilterGroupGap = 8f;

        /// <summary>The gap between two toggles of a frame (the classic HBox separation).</summary>
        private const float FilterSpacing = 4f;

        /// <summary>The toggle size: the classic 60 px buttons, smaller.</summary>
        private const float FilterCell = 44f;

        private const float FilterStatusWidth = 52f;
        private const float FilterNationWidth = 56f;

        /// <summary>
        /// The classic filter's layout on a glass panel, at a smaller scale:
        /// the levels and the vehicle status on top, the classes, the nations
        /// and the reset below, each group in a faint frame. A toggle that is
        /// on draws an outline, as in the classic filter, instead of a
        /// darker button.
        /// </summary>
        private void BuildModernFilterPanel()
        {
            _filterButtons.Clear();
            var panel = ModernUi.Panel("filter", _carouselLayer, ModernTheme.PanelRadius,
                ModernTheme.GlassStrong, true);
            panel.raycastTarget = true;
            _filterPanel = panel.rectTransform;

            var nations = new List<int>();
            for (var n = 1; n <= 9; n++)
            {
                if (_atlas.Contains(HangarUiKit.FilterNationRegion((Nation)n)))
                {
                    nations.Add(n);
                }
            }

            // Top row: filter_levels and the tech tree / premium / collector /
            // mod toggles.
            var x = FilterPad;
            var y = FilterPad;
            var levels = FilterFrame("filter_levels", ref x, y, CarouselFilter.MaxLevel, FilterCell);
            for (var level = CarouselFilter.MinLevel; level <= CarouselFilter.MaxLevel; level++)
            {
                var value = level;
                FilterToggle(levels, "lvl" + level, "hangar_lvl" + level, level - 1, FilterCell,
                    FilterCell - 8f, 20f,
                    () => _filter.ToggleLevel(value), () => _filter.HasLevel(value));
            }

            var status = FilterFrame("filter_status", ref x, y, 4, FilterStatusWidth);
            FilterToggle(status, "filter_regulars", HangarUiKit.RegularBadgeRegion, 0, FilterStatusWidth, 31f, 26f,
                _filter.ToggleRegular, () => _filter.Regular);
            FilterToggle(status, "filter_premiums", HangarUiKit.PremiumBadgeRegion, 1, FilterStatusWidth, 31f, 26f,
                _filter.TogglePremium, () => _filter.Premium);
            FilterToggle(status, "filter_collectors", HangarUiKit.CollectorBadgeRegion, 2, FilterStatusWidth, 31f, 26f,
                _filter.ToggleCollector, () => _filter.Collector);
            FilterToggle(status, "filter_mods", HangarUiKit.ModBadgeRegion, 3, FilterStatusWidth, 31f, 26f,
                _filter.ToggleMods, () => _filter.Mods);
            var width = x;

            // Bottom row: filter_classes, filter_nations and reset_filter.
            x = FilterPad;
            y += FilterCell + (2f * FilterFramePad) + FilterGroupGap;
            var classes = FilterFrame("filter_classes", ref x, y, 4, FilterCell);
            var classNodes = new[] { "lt", "mt", "ht", "td" };
            var classValues = new[] { TankClass.Light, TankClass.Medium, TankClass.Heavy, TankClass.TankDestroyer };
            for (var i = 0; i < classNodes.Length; i++)
            {
                var value = classValues[i];
                FilterToggle(classes, classNodes[i], "hangar_class_" + classNodes[i], i, FilterCell, 19f, 24f,
                    () => _filter.ToggleClass(value), () => _filter.HasClass(value));
            }

            var nationFrame = FilterFrame("filter_nations", ref x, y, nations.Count, FilterNationWidth);
            for (var i = 0; i < nations.Count; i++)
            {
                var nation = (Nation)nations[i];
                var region = HangarUiKit.FilterNationRegion(nation);
                FilterToggle(nationFrame, "nation" + nations[i].ToString(CultureInfo.InvariantCulture), region, i,
                    FilterNationWidth, 48f, 32f,
                    () => _filter.ToggleNation(nation), () => _filter.HasNation(nation));
            }

            var resetFrame = FilterFrame("filter_reset", ref x, y, 1, FilterCell);
            var reset = StyleButton("reset_filter", resetFrame, "button_small_generic");
            ModernUi.TopLeft((RectTransform)reset.transform, FilterFramePad, FilterFramePad, FilterCell, FilterCell);
            ModernUi.Icon(reset.transform, _atlas.Get("hangar_red_button"), 32f, 32f);
            reset.gameObject.AddComponent<PressFeedback>();
            Bind(reset, ResetModernFilter, AudioService.SoundSelect);
            width = Mathf.Max(width, x);

            var height = y + FilterCell + (2f * FilterFramePad) + FilterPad;
            ModernUi.BottomLeft(_filterPanel, 10f, _metrics.BarHeight + 8f, width - FilterGroupGap + FilterPad, height);

            _filterPanelFade = gameObject.AddComponent<ModernFade>();
            _filterPanelFade.DeactivateWhenHidden = true;
            _filterPanelFade.Add(_filterPanel);
            _filterPanelFade.ShowImmediate(false);
        }

        /// <summary>
        /// A faint frame for <paramref name="cells"/> toggles, the classic
        /// border*. Moves <paramref name="x"/> past the frame and the gap.
        /// </summary>
        private RectTransform FilterFrame(string name, ref float x, float y, int cells, float cellWidth)
        {
            var width = (cells * cellWidth) + (Mathf.Max(0, cells - 1) * FilterSpacing) + (2f * FilterFramePad);
            var frame = ModernUi.Panel(name, _filterPanel, 10, new Color(1f, 1f, 1f, 0.035f), true);
            ModernUi.TopLeft(frame.rectTransform, x, y, width, FilterCell + (2f * FilterFramePad));
            x += width + FilterGroupGap;
            return frame.rectTransform;
        }

        private void FilterToggle(
            RectTransform frame, string name, string iconRegion, int slot, float width,
            float iconWidth, float iconHeight, Action toggle, Func<bool> active)
        {
            var button = StyleButton(name, frame, "button_small_generic");
            ModernUi.TopLeft((RectTransform)button.transform,
                FilterFramePad + (slot * (width + FilterSpacing)), FilterFramePad, width, FilterCell);
            var state = GodotButtonState.Of(button);
            state.SetPressedStyle(button.GetComponent<Image>().sprite, Color.white);
            state.ToggleMode = true;

            // Under the icon, over the button: the outline of a toggle that is on.
            var selected = UiFactory.Picture("selected", button.transform, ModernSprites.RoundedRect(
                8, ModernTheme.AccentSoft, ModernTheme.AccentSoft, ModernTheme.Accent, 2f));
            selected.type = Image.Type.Sliced;
            selected.raycastTarget = false;
            GodotRect.FullRect(selected.rectTransform);
            selected.gameObject.SetActive(false);

            ModernUi.Icon(button.transform, _atlas.Get(iconRegion), iconWidth, iconHeight);
            button.gameObject.AddComponent<PressFeedback>();
            Bind(button, () =>
            {
                toggle();
                RebuildCarousel();
            }, AudioService.SoundSelect);
            _filterButtons.Add(new ModernFilterToggle
            {
                Button = button,
                Selected = selected.gameObject,
                IsOn = active,
            });
        }

        /// <summary>btn_reset_filter: every toggle is released; an empty filter stays as it is.</summary>
        private void ResetModernFilter()
        {
            if (!_filter.IsActive)
            {
                return;
            }

            _filter.Clear();
            RebuildCarousel();
        }

        private void RefreshFilterUi()
        {
            foreach (var toggle in _filterButtons)
            {
                var on = toggle.IsOn();
                var state = GodotButtonState.Of(toggle.Button);
                if (state != null) { state.Latched = on; }
                if (toggle.Selected.activeSelf != on) { toggle.Selected.SetActive(on); }
            }

            if (_filterState != null)
            {
                _filterState.text = _filter.ActiveCount > 0
                    ? _filter.ActiveCount.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
            }
        }

        private void SetModernFilterOpen(bool open)
        {
            if (open && !MenuStyleRules.AcceptsSelection(_carouselMode))
            {
                open = false;
            }

            _modernFilterOpen = open;
            _filterToggle.Latched = open;
            if (_filterIcon != null)
            {
                _filterIcon.sprite = _atlas.Get(open ? "hangar_filter_icon_on" : "hangar_filter_icon");
            }

            RefreshFilterUi();
            SyncFilterFade(_carouselLayer != null && _carouselLayer.gameObject.activeInHierarchy);
        }

        /// <summary>
        /// Everything the open filter fades out on the main screen: the top
        /// bar, the header, the rail, the loadout strip and the action row.
        /// The tank panel stays, so the characteristics of the tank picked
        /// from the filtered carousel remain on screen; the burger drawer and
        /// the placeholder pop-up are never up together with the filter.
        /// </summary>
        private void BuildChromeFade(RectTransform main)
        {
            _chromeFade = gameObject.AddComponent<ModernFade>();
            foreach (Transform child in _hud)
            {
                _chromeFade.Add(child);
            }

            foreach (Transform child in ZLayer(HangarScreenId.Main))
            {
                _chromeFade.Add(child);
            }

            foreach (Transform child in main)
            {
                if (child.name == "tank_panel" || child.name == "menu" || child == _placeholder)
                {
                    continue;
                }

                _chromeFade.Add(child);
            }

            _chromeFade.ShowImmediate(true);
        }

        /// <summary>
        /// The filter pop-up follows btn_filter; the chrome steps aside only
        /// while the filter is up over the main screen.
        /// </summary>
        private void SyncFilterFade(bool animate)
        {
            Fade(_filterPanelFade, _modernFilterOpen, animate);
            Fade(_chromeFade, !(_modernFilterOpen && _screen == HangarScreenId.Main), animate);
        }

        private static void Fade(ModernFade fade, bool shown, bool animate)
        {
            if (fade == null)
            {
                return;
            }

            if (animate)
            {
                fade.Show(shown);
            }
            else
            {
                fade.ShowImmediate(shown);
            }
        }

        /// <summary>
        /// The Modern bar around the cards: the line an empty carousel shows
        /// and the filter toggles.
        /// </summary>
        private void RefreshModernCarouselChrome()
        {
            if (_carouselEmpty != null)
            {
                _carouselEmpty.gameObject.SetActive(CarouselCardCount == 0);
            }

            RefreshFilterUi();
        }

        /// <summary>The nation backdrop's alpha on a Modern carousel card.</summary>
        private const float ModernCardNationAlpha = 0.9f;

        /// <summary>
        /// One empty Modern carousel card; <see cref="BindTankCard"/> draws a
        /// tank on it.
        /// </summary>
        private RectTransform CreateModernTankCard()
        {
            var w = _metrics.CardWidth;
            var h = _metrics.CardHeight;
            var view = new CarouselCard();
            var card = UiFactory.Picture("carousel_tank", CarouselRoot, ModernSprites.RoundedRect(10,
                new Color(0.13f, 0.15f, 0.18f, 0.95f), new Color(0.07f, 0.08f, 0.1f, 0.95f),
                ModernTheme.Stroke, 1.5f));
            card.type = Image.Type.Sliced;
            var cardRect = card.rectTransform;
            cardRect.anchorMin = new Vector2(0f, 0.5f);
            cardRect.anchorMax = new Vector2(0f, 0.5f);
            cardRect.pivot = new Vector2(0f, 0.5f);
            cardRect.sizeDelta = new Vector2(w, h);
            cardRect.anchoredPosition = Vector2.zero;

            // The original nation backdrop inside the rounded frame, nearly
            // at full strength so the flag tells the nation at a glance; a
            // dark scrim along the bottom keeps the name readable over it.
            view.Nation = UiFactory.Picture("nation", cardRect, null);
            view.Nation.color = new Color(1f, 1f, 1f, ModernCardNationAlpha);
            ModernUi.Fill(view.Nation.rectTransform, 4f);
            var scrim = UiFactory.Picture("scrim", cardRect, ModernSprites.VerticalGradient(
                new Color(0f, 0f, 0f, 0f), new Color(0f, 0f, 0f, 0.6f)));
            GodotRect.Apply(scrim.rectTransform, 0f, 1f, 1f, 1f, 4f, -h * 0.45f, -4f, -4f);

            view.Icon = UiFactory.Picture("icon", cardRect, null);
            view.Icon.preserveAspect = true;
            GodotRect.Apply(view.Icon.rectTransform, 0f, 0f, 1f, 1f, 8f, 4f, -8f, -h * 0.22f);
            view.Unknown = ModernUi.Label("icon_unknown", cardRect, _bold, (int)(h * 0.5f),
                Color.white, TextAnchor.MiddleCenter, "?");
            GodotRect.Apply(view.Unknown.rectTransform, 0f, 0f, 1f, 1f, 8f, 4f, -8f, -h * 0.22f);

            var glyph = Mathf.Round(h * 0.2f);
            view.Level = UiFactory.Picture("lvl", cardRect, null);
            view.Level.preserveAspect = true;
            ModernUi.TopLeft(view.Level.rectTransform, 6f, 5f, glyph * 1.2f, glyph);
            view.Class = UiFactory.Picture("class", cardRect, null);
            view.Class.preserveAspect = true;
            ModernUi.TopLeft(view.Class.rectTransform, 8f + glyph * 1.2f, 5f, glyph, glyph);

            view.Name = ModernUi.Label("name", cardRect, _bold, _metrics.NameFontSize,
                Color.white, TextAnchor.MiddleLeft, string.Empty);
            view.Name.horizontalOverflow = HorizontalWrapMode.Wrap;
            view.Name.resizeTextForBestFit = true;
            view.Name.resizeTextMinSize = 10;
            view.Name.resizeTextMaxSize = _metrics.NameFontSize;
            GodotRect.Apply(view.Name.rectTransform, 0f, 1f, 1f, 1f, 8f, -_metrics.NameFontSize - 8f, -6f, -3f);

            var selected = UiFactory.Picture("selected", cardRect, ModernSprites.RoundedRect(10,
                new Color(1f, 0.69f, 0.125f, 0.08f), new Color(1f, 0.69f, 0.125f, 0.16f), ModernTheme.Accent, 2.5f));
            selected.type = Image.Type.Sliced;
            GodotRect.FullRect(selected.rectTransform);
            selected.gameObject.SetActive(false);
            view.Selected = selected.gameObject;

            var button = StyleButton("button", cardRect, null);
            GodotRect.FullRect((RectTransform)button.transform);
            button.gameObject.AddComponent<PressFeedback>().PressedScale = 0.97f;
            Bind(button, () => OnModernCardPressed(view.Info, view.SaveIndex), AudioService.SoundEnter);
            _cardViews.Add(cardRect, view);
            return cardRect;
        }

        private void OnModernCardPressed(TankInfo info, int saveIndex)
        {
            if (!MenuStyleRules.AcceptsSelection(_carouselMode))
            {
                return;
            }

            var expanded = _carouselMode == CarouselMode.Expanded;
            SelectTank(info, saveIndex);
            if (!expanded)
            {
                return;
            }

            ReinitSubScreen();
            SetCarouselMode(CarouselMode.Collapsed, true);
        }

        /// <summary>
        /// A tank picked from the pulled-up carousel re-opens the current
        /// sub-screen for that tank, the way the screen would open from the
        /// hangar with it selected.
        /// </summary>
        private void ReinitSubScreen()
        {
            var tank = _account.SelectedTankName;
            switch (_screen)
            {
                case HangarScreenId.TechTree:
                    if (_techTree != null) { _techTree.Update(CurrentNation()); }
                    break;
                case HangarScreenId.Modules:
                    if (_modules != null && _tanks.Get(tank) != null) { _modules.Init(tank); }
                    break;
                case HangarScreenId.Loadout:
                    if (_loadout != null && _tanks.Get(tank) != null)
                    {
                        var tab = _loadout.Tab;
                        _loadout.Init(tank);
                        _loadout.ShowTab(tab);
                    }
                    break;
                case HangarScreenId.ArmorViewer:
                    var card = _tanks.Get(tank);
                    if (_armorViewer == null || card == null || !_armorViewer.Init(card, SelectedSave()))
                    {
                        ToScreen(HangarScreenId.Main);
                    }
                    break;
            }
        }

        private void ToggleCarouselExpanded()
        {
            if (_screen == HangarScreenId.Main && IsMenuOpen)
            {
                // On the main screen the handle only shows because the open
                // drawer folded the carousel, so pulling it closes the drawer
                // and the carousel comes back as the normal bar.
                SetMenuClosed();
                return;
            }

            if (_carouselMode == CarouselMode.Collapsed)
            {
                SetCarouselMode(CarouselMode.Expanded, true);
            }
            else if (_carouselMode == CarouselMode.Expanded)
            {
                SetCarouselMode(CarouselMode.Collapsed, true);
            }
        }

        /// <summary>Switches the carousel between its bottom-edge states.</summary>
        public void SetCarouselMode(CarouselMode mode, bool animate)
        {
            if (_carouselLayer == null)
            {
                return;
            }

            _carouselMode = mode;
            var showHandle = mode == CarouselMode.Collapsed || mode == CarouselMode.Expanded;
            _carouselHandle.gameObject.SetActive(showHandle);
            _handleChevron.sprite = ModernSprites.Chevron(mode != CarouselMode.Expanded, ModernTheme.Accent);

            _carouselGroup.interactable = MenuStyleRules.AcceptsSelection(mode);
            _carouselGroup.alpha = mode == CarouselMode.Locked ? 0.6f : 1f;
            if (_modernFilterButton != null)
            {
                _modernFilterButton.interactable = MenuStyleRules.AcceptsSelection(mode);
            }

            if (!MenuStyleRules.AcceptsSelection(mode) || mode == CarouselMode.Collapsed)
            {
                if (_modernFilterOpen) { SetModernFilterOpen(false); }
            }

            if (_carouselTween != null)
            {
                StopCoroutine(_carouselTween);
                _carouselTween = null;
            }

            // The slide starts where the bar is, so a pull-up that interrupts
            // a fold (or the other way round) turns around instead of jumping.
            var from = _carouselShown;
            var to = MenuStyleRules.CarouselShown(mode);
            var drawn = to > 0f;
            if (mode == CarouselMode.Hidden || !animate || Mathf.Approximately(from, to))
            {
                PlaceCarousel(to);
                SetCarouselBarDrawn(drawn);
                return;
            }

            // A rising bar draws from the first frame of its slide; a folding
            // one stops drawing once it is below the edge.
            SetCarouselBarDrawn(true);
            _carouselTween = ModernTween.Run(this,
                MenuStyleRules.CarouselTweenTime(from, to, ModernTheme.TweenTime),
                t => PlaceCarousel(Mathf.Lerp(from, to, t)),
                () =>
                {
                    _carouselTween = null;
                    SetCarouselBarDrawn(drawn);
                });
        }

        /// <summary>
        /// Folded and hidden bars keep their GameObject active: the canvas
        /// keeps its built geometry and simply stops drawing, and the group
        /// stops it from taking taps.
        /// </summary>
        private void SetCarouselBarDrawn(bool drawn)
        {
            _carouselCanvas.enabled = drawn;
            _carouselRaycaster.enabled = drawn;
            _carouselGroup.blocksRaycasts = drawn;
        }

        /// <summary>0 = folded below the edge, 1 = fully up.</summary>
        private void PlaceCarousel(float shown)
        {
            _carouselShown = shown;
            var drop = _metrics.BarHeight * (1f - shown);
            _carouselBar.anchoredPosition = _barHome - new Vector2(0f, drop);
            _carouselHandle.anchoredPosition = _handleHome + new Vector2(0f, _metrics.BarHeight * shown);
        }

        private void OnModernScreen(HangarScreenId id)
        {
            // The drawer keeps its state while another screen is up, so a
            // return to the main screen with it still open stays folded.
            SetCarouselMode(id == HangarScreenId.Main ? MenuStyleRules.MainCarouselMode(IsMenuOpen) :
                id == HangarScreenId.Shop || id == HangarScreenId.Camouflage
                    ? CarouselMode.Hidden
                    : CarouselMode.Collapsed,
                _carouselLayer != null && _carouselLayer.gameObject.activeInHierarchy);
            if (id == HangarScreenId.Main)
            {
                RefreshModernTankPanel();
            }

            SyncFilterFade(false);
        }

        /// <summary>
        /// The burger drawer runs from under the header to the bottom edge and
        /// is drawn below the carousel layer, so on the main screen the
        /// carousel folds into its handle while the drawer is open and comes
        /// back when it closes. Classic hangars keep the source behaviour.
        /// </summary>
        private void SyncCarouselWithMenu()
        {
            if (!_modern || _screen != HangarScreenId.Main || _carouselLayer == null)
            {
                return;
            }

            // The queue lock and the full-screen overlays own the carousel.
            if (_carouselMode == CarouselMode.Locked || _carouselMode == CarouselMode.Hidden)
            {
                return;
            }

            var target = MenuStyleRules.MainCarouselMode(IsMenuOpen);
            if (target != _carouselMode)
            {
                SetCarouselMode(target, _carouselLayer.gameObject.activeInHierarchy);
            }
        }

        /// <summary>Escape steps of the Modern carousel; true when one was taken.</summary>
        private bool HandleModernEscape()
        {
            if (_modernFilterOpen)
            {
                SetFilterOpen(false);
                return true;
            }

            if (_carouselMode == CarouselMode.Expanded)
            {
                SetCarouselMode(CarouselMode.Collapsed, true);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The battle queue in the Modern style keeps the hangar tank and the
        /// carousel on screen, dimmed and locked, under the queue panel.
        /// Classic hangars ignore it: their queue covers the whole screen.
        /// </summary>
        public void SetQueueMode(bool queued)
        {
            if (!_modern)
            {
                return;
            }

            if (queued)
            {
                if (_menu != null && _menu.IsOpen)
                {
                    _menu.SetOpen(false);
                    _menuToggle.Latched = false;
                }

                SetFilterOpen(false);
                if (_main != null) { _main.gameObject.SetActive(false); }
                if (_hud != null) { _hud.gameObject.SetActive(false); }
                if (_zTop != null) { _zTop.gameObject.SetActive(false); }
                if (DragSurface != null) { DragSurface.gameObject.SetActive(false); }
                SetCarouselMode(CarouselMode.Locked, false);
                return;
            }

            if (_zTop != null) { _zTop.gameObject.SetActive(true); }
            ToScreen(_screen);
        }

        // ------------------------------------------------------------------
        // Tank panel.
        // ------------------------------------------------------------------

        private void RefreshModernTankPanel()
        {
            if (_panelName == null || _account == null || _tanks == null)
            {
                return;
            }

            var info = _tanks.Get(_account.SelectedTankName);
            if (info == null)
            {
                return;
            }

            var save = SelectedSave();
            _panelLevel.sprite = _atlas.Get(HangarUiKit.TankLevelRegion(info));
            _panelLevel.color = HangarUiKit.TankGlyphTint(info);
            _panelClass.sprite = _atlas.Get(HangarUiKit.TankClassRegion(info));
            _panelClass.color = HangarUiKit.TankGlyphTint(info);
            _panelNation.sprite = _atlas.Get(HangarUiKit.NationRegion(info.Nation));
            _panelNation.enabled = _panelNation.sprite != null && info.Nation != Nation.None;
            _panelName.text = _lang.GetTankName(info);
            _panelName.color = HangarUiKit.TankNameColor(info);

            var turretId = save == null ? 0 : save.TurretId;
            var gunId = save == null ? 0 : save.GunId;
            var gun = info.Guns.Count == 0 ? null : info.GunFor(turretId, gunId);
            var shell = gun != null && gun.Shells.Count > 0 ? gun.Shells[0] : null;
            var hp = info.TotalHealthWith(info.Turret(turretId));

            _panelStats[0].text = HangarUiKit.FormatNumber(hp);
            _panelStats[1].text = shell == null ? "-" : HangarUiKit.FormatNumber(Mathf.RoundToInt(shell.Damage));
            _panelStats[2].text = shell == null
                ? "-"
                : string.Format(_lang.Get("ui:unit/mm"), Mathf.RoundToInt(shell.Penetration));
            _panelStats[3].text = gun == null
                ? "-"
                : string.Format(_lang.Get("ui:unit/seconds"),
                    gun.Reload.ToString("0.0", CultureInfo.InvariantCulture));
            _panelStats[4].text = string.Format(_lang.Get("ui:unit/kmh"), Mathf.RoundToInt(info.SpeedForward));

            if (_researchExp != null)
            {
                _researchExp.text = HangarUiKit.FormatNumber(save == null ? 0 : save.Exp);
            }
        }

        /// <summary>Finds a descendant by name: the Modern HUD nests its readouts in pills.</summary>
        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null)
            {
                return null;
            }

            var direct = root.Find(name);
            if (direct != null)
            {
                return direct;
            }

            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
