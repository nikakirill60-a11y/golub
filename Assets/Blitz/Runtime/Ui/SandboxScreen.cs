using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Sandbox;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Mods;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Code-first port of assets/gfx/ui/hangar/training_mode_screen.gd, the
    /// full-screen Sandbox editor.
    ///
    /// The source builds the screen from Godot containers (VBox, HBox, grid,
    /// flow and scroll containers) with the hangar's authored textures; the
    /// port mirrors that tree with uGUI layout groups so the rows keep the
    /// source's minimum sizes and follow the live canvas width. Every control
    /// edits the active preset's configuration in place, exactly like the
    /// source: CLOSE saves, APPLY selects the Sandbox mode, START launches the
    /// battle offline. The Modern menu style reuses this tree; the atlas is
    /// remapped by <see cref="MenuStyle.ApplyForMenus"/> and the colours come
    /// from SandboxScreen.Modern.cs.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class SandboxScreen : MonoBehaviour
    {
        // -- training_mode_screen.gd constants --------------------------------

        public const float ControlHeight = 54f;
        public const float FormLabelWidth = 390f;
        public const float FormRowHeight = 72f;
        public const float HeaderHeight = 84f;
        public const float NavigationHeight = 58f;
        public const float FooterHeight = 74f;
        public const float ContentMarginX = 14f;
        public const float ContentMarginY = 8f;
        public const float MapCardWidth = 240f;
        public const float MapCardHeight = 118f;
        public const int MapColumns = 4;
        public const float TankCardWidth = 196f;
        public const float TankCardHeight = 130f;
        public const float ModuleCardWidth = 140f;
        public const float ModuleCardHeight = 120f;

        /// <summary>Drawn size of SliderThumb.tres at the source's 0.5 slider scale.</summary>
        public const float SliderGrabber = 44f;
        private const float SliderBarHeight = 12f;

        // -- atlas regions -----------------------------------------------------

        /// <summary>ButtonSmallGeneric.</summary>
        public const string GenericRegion = "button_small_generic";

        /// <summary>ButtonGreen: style_button_grn.stylebox.</summary>
        public const string GreenRegion = "button_green";

        /// <summary>The armed DELETE button.</summary>
        public const string RedRegion = "button_red";

        /// <summary>battle_result/bgTable_02.tres, the source's ROW_TEXTURE.</summary>
        public const string RowRegion = "settings_row_bg";

        /// <summary>lobby_common/Header.tres.</summary>
        public const string HeaderRegion = "hangar_header";

        /// <summary>lobby_common/thinRedLine.png.</summary>
        public const string LineRegion = "hangar_thin_red_line";

        /// <summary>lobby_common/IconsSmall04.tres, the CLOSE button's back arrow.</summary>
        public const string BackIconRegion = "icon_back";

        public const string FrameRegion = "slider_frame";
        public const string ThumbRegion = "slider_thumb";

        /// <summary>The primary categories, in source order (PrimaryPage).</summary>
        public enum Page
        {
            Battle = 0,
            Allies = 1,
            Enemies = 2,
            Modifiers = 3,
            Cheats = 4,
        }

        public static readonly string[] PageKeys =
        {
            "training:category/battle",
            "training:tab/allies",
            "training:tab/enemies",
            "training:category/modifiers",
            "training:category/cheats",
        };

        public static readonly string[] BattlePageKeys =
        {
            "training:sub/scenario",
            "training:sub/match",
            "training:sub/rules",
            "training:sub/bots",
        };

        public static readonly string[] ModifierPageKeys =
        {
            "training:sub/global",
            "training:sub/player",
        };

        /// <summary>_multiplier_rows, in <see cref="SandboxStat"/> order.</summary>
        public static readonly string[] MultiplierKeys =
        {
            "training:override/hp",
            "training:override/damage",
            "training:override/penetration",
            "training:override/reload",
            "training:override/magazine",
            "training:override/intraclip",
            "training:override/speed",
            "training:override/engine",
            "training:override/aim",
            "training:override/turn_rate",
        };

        private const string OnKey = "training:value/on";
        private const string OffKey = "training:value/off";
        private const string RandomKey = "training:value/random";

        private SandboxPresetService _presets;
        private SandboxConfig _config;
        private HangarUiKit _kit;
        private bool _mobile;
        private int _sortingOrder;
        private Action _applied;
        private Action<string> _startRequested;
        private Action _closed;
        private RectTransform _canvas;
        private RectTransform _content;

        private readonly List<Button> _primaryButtons = new List<Button>();
        private readonly List<RectTransform> _tabs = new List<RectTransform>();
        private readonly Dictionary<string, Button> _mapButtons = new Dictionary<string, Button>();
        private readonly Dictionary<string, GameObject> _mapFrames = new Dictionary<string, GameObject>();
        private readonly Dictionary<SandboxBaseMode, Button> _modeButtons = new Dictionary<SandboxBaseMode, Button>();
        private readonly RectTransform[] _teamCards = new RectTransform[2];
        private readonly RectTransform[] _teamEditors = new RectTransform[2];
        private readonly Button[] _selectTanks = new Button[2];
        private readonly int[] _selectedSlots = new int[2];
        private readonly List<Sprite> _ownedSprites = new List<Sprite>();
        private readonly Dictionary<string, Sprite> _slicedArt = new Dictionary<string, Sprite>();

        private Page _page = Page.Battle;
        private int _battlePage;
        private int _modifierPage;
        private RectTransform _modeSettings;
        private Text _summary;
        private bool _launchPending;
        private bool _modern;

        /// <summary>The whole screen under the canvas.</summary>
        public RectTransform Root { get; private set; }

        /// <summary>The configuration every control edits: the active preset's.</summary>
        public SandboxConfig Config { get { return _config; } }

        public SandboxPresetService Presets { get { return _presets; } }

        public Page CurrentPage { get { return _page; } }

        public Button StartButton { get; private set; }

        public Button CloseButton { get; private set; }

        public Button ManageButton { get; private set; }

        public string SummaryText { get { return _summary == null ? string.Empty : _summary.text; } }

        public IList<Button> PrimaryButtons { get { return _primaryButtons; } }

        /// <summary>The slot whose editor is open for the team.</summary>
        public int SelectedSlot(bool enemy)
        {
            return _selectedSlots[Team(enemy)];
        }

        // ------------------------------------------------------------------
        // Lifecycle.
        // ------------------------------------------------------------------

        /// <summary>
        /// UIGlobal.display_popup(training_mode_screen): the editor over the
        /// hangar. <paramref name="applied"/> is the source's <c>applied</c>
        /// signal, <paramref name="startRequested"/> its
        /// <c>start_requested(map)</c> with the chosen map id (null for a
        /// RANDOM map), and <paramref name="closed"/> runs whenever the screen
        /// goes away.
        /// </summary>
        public static SandboxScreen Open(
            Transform parent,
            SandboxPresetService presets,
            int sortingOrder,
            bool mobile,
            Action applied,
            Action<string> startRequested,
            Action closed)
        {
            if (presets == null)
            {
                throw new ArgumentNullException("presets");
            }

            var go = new GameObject("SandboxScreen");
            go.transform.SetParent(parent, false);
            var screen = go.AddComponent<SandboxScreen>();
            screen._presets = presets;
            screen._sortingOrder = sortingOrder;
            screen._mobile = mobile;
            screen._applied = applied;
            screen._startRequested = startRequested;
            screen._closed = closed;
            screen.Build();
            return screen;
        }

        private void Build()
        {
            _modern = MenuStyle.InMenus;
            var atlas = MenuStyle.ApplyForMenus(BattleFlowUi.LoadAtlas());
            var lang = BattleFlowUi.LoadLanguage();
            var bootstrap = BlitzBootstrap.Current;
            _kit = new HangarUiKit(
                atlas,
                lang,
                BlitzResources.LoadFont(BlitzResources.RegularFontName),
                BlitzResources.LoadFont(BlitzResources.BoldFontName),
                bootstrap == null ? null : bootstrap.Audio);
            _canvas = BattleFlowUi.CreateCanvas(transform, false, _mobile, _sortingOrder);
            BuildScreen();
            Debug.Log("[Blitz] sandbox screen built: style=" + (_modern ? "modern" : "classic")
                + " presets=" + _presets.Library.Count + " maps=" + _presets.Maps.Count);
        }

        private void Update()
        {
            // _unhandled_key_input: ui_cancel closes the topmost layer.
            if (Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                return;
            }

            if (_choiceOverlay != null) { CloseChoice(); }
            else if (_exchangeOverlay != null) { CloseExchange(); }
            else if (_pickerOverlay != null) { CloseTankPicker(); }
            else if (_presetOverlay != null) { ClosePresetManager(); }
            else { Close(); }
        }

        private void OnDestroy()
        {
            foreach (var sprite in _ownedSprites)
            {
                if (sprite != null) { Destroy(sprite); }
            }

            _ownedSprites.Clear();
            _slicedArt.Clear();
        }

        /// <summary>_build_screen.</summary>
        private void BuildScreen()
        {
            _config = _presets.Active;
            _primaryButtons.Clear();
            _tabs.Clear();
            _mapButtons.Clear();
            _mapFrames.Clear();
            _modeButtons.Clear();
            _modeSettings = null;
            StartButton = null;
            _launchPending = false;
            _deleteArmed = false;
            for (var i = 0; i < 2; i++)
            {
                _teamCards[i] = null;
                _teamEditors[i] = null;
                _selectTanks[i] = null;
            }

            ResetOverlayFields();

            Root = UiFactory.Node("training_mode_screen", _canvas);
            GodotRect.FullRect(Root);

            // blur.tscn: a full-rect backdrop that swallows every click.
            var blur = UiFactory.Picture("blur", Root, null);
            GodotRect.FullRect(blur.rectTransform);
            blur.color = BackdropColor;
            blur.raycastTarget = true;

            BuildHeader();
            BuildPrimaryNavigation();

            _content = UiFactory.Node("tabs", Root);
            GodotRect.Apply(_content, 0f, 0f, 1f, 1f,
                ContentMarginX, HeaderHeight + NavigationHeight + ContentMarginY,
                -ContentMarginX, -(FooterHeight + ContentMarginY), GodotRect.TopLeftPivot);

            BuildBattleTab();
            BuildTeamTab(false);
            BuildTeamTab(true);
            BuildModifiersTab();
            BuildCheatsTab();
            SelectPage(_page);

            BuildFooter();
            RefreshAllTeams();
            RefreshSummary();
        }

        /// <summary>_rebuild_screen.</summary>
        private void RebuildScreen()
        {
            if (Root != null)
            {
                Kill(Root.gameObject);
                Root = null;
            }

            BuildScreen();
        }

        // ------------------------------------------------------------------
        // Header, navigation, footer.
        // ------------------------------------------------------------------

        private void BuildHeader()
        {
            var header = _kit.Picture("header", Root, HeaderRegion, 0f, 0f, 0f, 0f);
            GodotRect.Apply(header.rectTransform, 0f, 0f, 1f, 0f, 0f, 0f, 0f, HeaderHeight,
                GodotRect.TopLeftPivot);

            var line = _kit.Picture("thin_red_line", header.rectTransform, LineRegion, 0f, 0f, 0f, 0f);
            GodotRect.Apply(line.rectTransform, 0f, 1f, 1f, 1f, 0f, -4f, 0f, 0f, GodotRect.TopLeftPivot);

            var row = HBox("row", header.rectTransform, 9f, 0f);
            GodotRect.Apply(row, 0f, 0f, 1f, 1f, 10f, 8f, -10f, -10f, GodotRect.TopLeftPivot);
            row.GetComponent<HorizontalLayoutGroup>().childForceExpandHeight = false;

            CloseButton = Btn("btn_close", row, S("training:close"), false, 112f, 60f, 20);
            AddLeadingIcon(CloseButton, BackIconRegion);
            Bind(CloseButton, Close);

            var titleBox = VBox("title_box", row, 0f);
            Elem(titleBox).flexibleWidth = 1f;
            Bold(titleBox, S("training:title"), 28);
            var subtitle = Hint(titleBox, S("training:subtitle"));
            subtitle.fontSize = 16;
            subtitle.verticalOverflow = VerticalWrapMode.Truncate;
            var subtitleSize = Elem(subtitle);
            subtitleSize.minHeight = 22f;
            subtitleSize.preferredHeight = 22f;

            ManageButton = Btn("btn_manage", row, S("training:preset/manage"), false, 200f, 58f, 20);
            Bind(ManageButton, OpenPresetManager);
        }

        private void BuildPrimaryNavigation()
        {
            var navigation = HBox("navigation", Root, 4f, 0f);
            GodotRect.Apply(navigation, 0f, 0f, 1f, 0f, 0f, HeaderHeight, 0f,
                HeaderHeight + NavigationHeight, GodotRect.TopLeftPivot);
            for (var i = 0; i < PageKeys.Length; i++)
            {
                var page = (Page)i;
                var button = Btn("btn_" + page.ToString().ToLowerInvariant(), navigation,
                    S(PageKeys[i]), page == _page, 132f, NavigationHeight, 21);
                Elem(button).flexibleWidth = 1f;
                Bind(button, () => SelectPage(page));
                _primaryButtons.Add(button);
            }
        }

        /// <summary>_select_primary.</summary>
        public void SelectPage(Page page)
        {
            var index = Mathf.Clamp((int)page, 0, Math.Max(0, _tabs.Count - 1));
            _page = (Page)index;
            for (var i = 0; i < _tabs.Count; i++)
            {
                _tabs[i].gameObject.SetActive(i == index);
            }

            for (var i = 0; i < _primaryButtons.Count; i++)
            {
                SetGreen(_primaryButtons[i], i == index);
            }
        }

        private void BuildFooter()
        {
            var footer = _kit.Picture("footer", Root, RowRegion, 0f, 0f, 0f, 0f);
            GodotRect.Apply(footer.rectTransform, 0f, 1f, 1f, 1f, 0f, -FooterHeight, 0f, 0f,
                GodotRect.TopLeftPivot);
            footer.raycastTarget = true;
            var row = footer.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset(14, 14, 7, 7);
            row.spacing = 9f;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            row.childAlignment = TextAnchor.MiddleLeft;

            _summary = UiFactory.Label("summary", footer.transform, _kit.Regular, 19, TextColor,
                TextAnchor.MiddleLeft, string.Empty);
            _summary.horizontalOverflow = HorizontalWrapMode.Wrap;
            _summary.verticalOverflow = VerticalWrapMode.Truncate;
            var summarySize = Elem(_summary);
            summarySize.flexibleWidth = 1f;
            summarySize.minHeight = 58f;

            var reset = Btn("btn_reset", footer.transform, S("training:reset"), false, 132f, ControlHeight, 20);
            Bind(reset, ResetPreset);
            var apply = Btn("btn_apply", footer.transform, S("training:apply"), false, 132f, ControlHeight, 20);
            Bind(apply, Apply);
            StartButton = Btn("training_start", footer.transform, S("training:start"), true, 230f, 58f, 23);
            Bind(StartButton, StartBattle);
        }

        // ------------------------------------------------------------------
        // BATTLE.
        // ------------------------------------------------------------------

        private void BuildBattleTab()
        {
            var root = Tab("battle");
            var pages = SubPages(root, BattlePageKeys, _battlePage, index => _battlePage = index);
            BuildScenarioPage(pages[0]);
            BuildMatchPage(pages[1]);
            BuildRulesPage(pages[2]);
            BuildBotDefaultsPage(pages[3]);
        }

        private void BuildScenarioPage(RectTransform parent)
        {
            Section(parent, "training:section/scenario", "training:hint/scenario");
            Bold(parent, S("training:base_mode"), 21);
            var modes = HBox("mode_box", parent, 7f, 62f);
            var definitions = new[]
            {
                new KeyValuePair<SandboxBaseMode, string>(SandboxBaseMode.Standard, "battle_mode:normal/name"),
                new KeyValuePair<SandboxBaseMode, string>(SandboxBaseMode.Uprising, "battle_mode:uprising/name"),
                new KeyValuePair<SandboxBaseMode, string>(SandboxBaseMode.Gravity, "battle_mode:gravity/name"),
                new KeyValuePair<SandboxBaseMode, string>(SandboxBaseMode.BigBoss, "battle_mode:bossmode/name"),
            };
            foreach (var definition in definitions)
            {
                var mode = definition.Key;
                var button = Btn("mode_" + mode.ToString().ToLowerInvariant(), modes, S(definition.Value),
                    _config.BaseMode == mode, 132f, 62f, 20);
                Elem(button).flexibleWidth = 1f;
                Bind(button, () => SelectBaseMode(mode));
                _modeButtons[mode] = button;
            }

            _modeSettings = VBox("training_mode_settings", parent, 9f);
            RefreshModeSettings();

            Bold(parent, S("training:map"), 21);
            var grid = UiFactory.Node("map_grid", parent);
            var layout = grid.gameObject.AddComponent<GridLayoutGroup>();
            layout.cellSize = new Vector2(MapCardWidth, MapCardHeight);
            layout.spacing = new Vector2(8f, 8f);
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = MapColumns;
            layout.childAlignment = TextAnchor.UpperLeft;

            var random = Btn("map_random", grid, S(RandomKey), false, MapCardWidth, MapCardHeight, 20);
            _mapFrames[string.Empty] = SelectionFrame(random);
            Bind(random, () => SelectMap(string.Empty));
            _mapButtons[string.Empty] = random;

            var maps = new List<MapDefinition>(_presets.Maps);
            maps.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            foreach (var map in maps)
            {
                var id = map.Id;
                var card = MapCard(grid, map);
                Bind(card, () => SelectMap(id));
                _mapButtons[id] = card;
            }

            RefreshScenarioSelection();
        }

        private Button MapCard(RectTransform grid, MapDefinition map)
        {
            var button = Btn("map_" + map.Id, grid, string.Empty, false, MapCardWidth, MapCardHeight, 19);
            button.gameObject.AddComponent<RectMask2D>();
            var texture = SandboxPresetService.CardTexture(map);
            if (texture != null)
            {
                var sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f), 100f);
                _ownedSprites.Add(sprite);
                var image = UiFactory.Picture("image", button.transform, sprite);
                GodotRect.FullRect(image.rectTransform);
                image.color = MapImageTint;
                var fit = image.gameObject.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
                fit.aspectRatio = (float)texture.width / Mathf.Max(1, texture.height);
            }

            var label = UiFactory.Label("name", button.transform, _kit.Bold, 19, TextColor,
                TextAnchor.MiddleCenter, S(map.NameKey));
            GodotRect.Apply(label.rectTransform, 0f, 1f, 1f, 1f, 4f, -38f, -4f, 0f);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            var outline = label.gameObject.AddComponent<Outline>();
            outline.effectColor = Color.black;
            outline.effectDistance = new Vector2(2f, -2f);
            _mapFrames[map.Id] = SelectionFrame(button);
            return button;
        }

        /// <summary>_add_map_selection_frame: a 5 px COLOR_SELF border, hidden.</summary>
        private GameObject SelectionFrame(Button button)
        {
            var frame = UiFactory.Node("selected_frame", button.transform);
            GodotRect.FullRect(frame);
            BattleModeScreen.Border(frame, SelectionColor, 5f);
            frame.gameObject.SetActive(false);
            return frame.gameObject;
        }

        /// <summary>_select_map.</summary>
        public void SelectMap(string mapId)
        {
            _config.SelectedMapId = mapId ?? string.Empty;
            RefreshScenarioSelection();
        }

        /// <summary>_select_base_mode.</summary>
        public void SelectBaseMode(SandboxBaseMode mode)
        {
            _config.BaseMode = mode;
            RefreshScenarioSelection();
            RefreshModeSettings();
        }

        private void RefreshScenarioSelection()
        {
            foreach (var pair in _mapFrames)
            {
                pair.Value.SetActive(pair.Key == (_config.SelectedMapId ?? string.Empty));
            }

            foreach (var pair in _modeButtons)
            {
                SetGreen(pair.Value, pair.Key == _config.BaseMode);
            }

            RefreshSummary();
        }

        private void RefreshModeSettings()
        {
            if (_modeSettings == null)
            {
                return;
            }

            ClearChildren(_modeSettings);
            var c = _config;
            _modeSettings.gameObject.SetActive(c.BaseMode != SandboxBaseMode.Standard);
            if (c.BaseMode == SandboxBaseMode.Uprising)
            {
                var box = _modeSettings;
                Section(box, "training:mode/uprising/title", "training:mode/uprising/hint");
                IntSlider(box, "training:mode/uprising/lives", c.UprisingLives, 1f, 10f, 1f, v => c.UprisingLives = v);
                FloatSlider(box, "training:mode/uprising/elixir_second", c.UprisingElixirPerSecond, 0f, 100f, 0.5f, string.Empty, v => c.UprisingElixirPerSecond = v);
                IntSlider(box, "training:mode/uprising/elixir_shot", c.UprisingElixirPerDamagingShot, 0f, 100f, 1f, v => c.UprisingElixirPerDamagingShot = v);
                IntSlider(box, "training:mode/uprising/damage_step", c.UprisingElixirDamageStep, 10f, 1000f, 10f, v => c.UprisingElixirDamageStep = v);
                IntSlider(box, "training:mode/uprising/elixir_damage", c.UprisingElixirPerDamageStep, 0f, 100f, 1f, v => c.UprisingElixirPerDamageStep = v);
                IntSlider(box, "training:mode/uprising/elixir_death", c.UprisingElixirPerDeath, 0f, 1000f, 10f, v => c.UprisingElixirPerDeath = v);
                FloatSlider(box, "training:mode/uprising/upgrade_power", c.UprisingUpgradePowerMul, 0f, 10f, 0.5f, "×", v => c.UprisingUpgradePowerMul = v);
                IntSlider(box, "training:mode/uprising/cost_1", c.UprisingCostLevel1, 0f, 1000f, 50f, v => c.UprisingCostLevel1 = v);
                IntSlider(box, "training:mode/uprising/cost_2", c.UprisingCostLevel2, 0f, 1000f, 50f, v => c.UprisingCostLevel2 = v);
                IntSlider(box, "training:mode/uprising/cost_3", c.UprisingCostLevel3, 0f, 1000f, 50f, v => c.UprisingCostLevel3 = v);
            }
            else if (c.BaseMode == SandboxBaseMode.Gravity)
            {
                var box = _modeSettings;
                Section(box, "training:mode/gravity/title", "training:mode/gravity/hint");
                FloatSlider(box, "training:mode/gravity/speed", c.GravitySpeedMul, 0f, 10f, 0.5f, "×", v => c.GravitySpeedMul = v);
                FloatSlider(box, "training:mode/gravity/shot_power", c.GravityShotPowerMul, 0f, 10f, 0.5f, "×", v => c.GravityShotPowerMul = v);
                FloatSlider(box, "training:mode/gravity/jump_power", c.GravityJumpPowerMul, 0f, 10f, 0.5f, "×", v => c.GravityJumpPowerMul = v);
                FloatSlider(box, "training:mode/gravity/gravity", c.GravityGravityMul, 0f, 5f, 0.1f, "×", v => c.GravityGravityMul = v);
            }
            else if (c.BaseMode == SandboxBaseMode.BigBoss)
            {
                var box = _modeSettings;
                Section(box, "training:mode/bossmode/title", "training:mode/bossmode/hint");
                IntSlider(box, "training:mode/bossmode/lives", c.BigBossLives, 1f, 10f, 1f, v => c.BigBossLives = v);
                IntSlider(box, "training:mode/bossmode/team_points", c.BigBossTeamPoints, 1f, 200f, 1f, v => c.BigBossTeamPoints = v);
                FloatSlider(box, "training:mode/bossmode/buff_mul", c.BigBossBuffMul, 0f, 10f, 0.1f, "×", v => c.BigBossBuffMul = v);
            }
        }

        private void BuildMatchPage(RectTransform parent)
        {
            var c = _config;
            Section(parent, "training:section/matchmaker", "training:hint/match");
            OptionRow(parent, "player_team", "training:player_team", new[]
            {
                Item(RandomKey, (int)SandboxTeamChoice.Random),
                Item("training:value/team_a", (int)SandboxTeamChoice.TeamA),
                Item("training:value/team_b", (int)SandboxTeamChoice.TeamB),
            }, (int)c.PlayerTeam, v => c.PlayerTeam = (SandboxTeamChoice)v, null);
            OptionRow(parent, "balancer_mode", "training:balancer", new[]
            {
                Item("training:balancer/random", (int)SandboxBalancer.Random),
                Item("training:balancer/player_tier", (int)SandboxBalancer.PlayerTier),
                Item("training:balancer/mirror", (int)SandboxBalancer.MirrorAllies),
            }, (int)c.Balancer, v => c.Balancer = (SandboxBalancer)v, RefreshAllTeams);
        }

        private void BuildRulesPage(RectTransform parent)
        {
            var c = _config;
            Section(parent, "training:section/gameplay", "training:hint/rules");
            SpinRow(parent, "battle_time_minutes", "training:battle_time", c.BattleTimeMinutes, 0f,
                SandboxConfig.MaxBattleMinutes, 1f, S("training:minutes"), v => c.BattleTimeMinutes = v, false);
            ToggleRow(parent, "friendly_fire", "training:friendly_fire", c.FriendlyFire, v => c.FriendlyFire = v);
            SpinRow(parent, "damage_deviation", "training:damage_deviation", c.DamageDeviation, 0f, 1f, 0.05f,
                string.Empty, v => c.DamageDeviation = v, false);
            SpinRow(parent, "penetration_deviation", "training:penetration_deviation", c.PenetrationDeviation,
                0f, 1f, 0.05f, string.Empty, v => c.PenetrationDeviation = v, false);
            MultiplierRow(parent, "training:projectile_gravity", c.ProjectileGravityMul, v => c.ProjectileGravityMul = v);
        }

        private void BuildBotDefaultsPage(RectTransform parent)
        {
            var c = _config;
            Section(parent, "training:section/bot_defaults", "training:hint/bots");
            OptionRow(parent, "default_bot_skill", "training:bot/default_skill", SkillItems(),
                c.DefaultBotSkill, v => c.DefaultBotSkill = v, null);
            ToggleRow(parent, "default_bots_harmless", "training:bot/default_harmless", c.DefaultBotsHarmless,
                v => c.DefaultBotsHarmless = v);
            ToggleRow(parent, "default_bots_stationary", "training:bot/default_stationary", c.DefaultBotsStationary,
                v => c.DefaultBotsStationary = v);
        }

        private static KeyValuePair<string, int>[] SkillItems()
        {
            return new[]
            {
                Item(RandomKey, SandboxBotSlot.RandomSelection),
                Item("training:skill/idle", 0),
                Item("training:skill/white", 1),
                Item("training:skill/green", 2),
                Item("training:skill/blue", 3),
                Item("training:skill/purple", 4),
            };
        }

        // ------------------------------------------------------------------
        // ALLIES / ENEMIES.
        // ------------------------------------------------------------------

        private void BuildTeamTab(bool enemy)
        {
            var root = Tab(enemy ? "enemies" : "allies");
            var page = ScrollPage("page", root);
            Section(page, "training:sub/roster", "training:composition_hint");

            var c = _config;
            SpinRow(page, enemy ? "training_team_size_enemies" : "training_team_size_allies",
                enemy ? "training:enemy_team_size" : "training:allied_team_size",
                enemy ? c.EnemyTeamSize : c.AlliedTeamSize, 1f, SandboxConfig.MaxSlots, 1f, string.Empty,
                v =>
                {
                    if (enemy) { c.EnemyTeamSize = Mathf.RoundToInt(v); }
                    else { c.AlliedTeamSize = Mathf.RoundToInt(v); }
                },
                true);

            var actions = HBox("actions", page, 10f, 62f);
            var choose = Btn(enemy ? "select_enemy_tanks" : "select_allied_tanks", actions,
                S("training:select_tanks"), true, 132f, ControlHeight, 20);
            Elem(choose).flexibleWidth = 1f;
            Bind(choose, () => OpenTankPicker(enemy, -1));
            _selectTanks[Team(enemy)] = choose;
            var clear = Btn(enemy ? "clear_enemy_tanks" : "clear_allied_tanks", actions,
                S("training:clear_tanks"), false, 132f, ControlHeight, 20);
            Elem(clear).flexibleWidth = 1f;
            Bind(clear, () => ClearTeamTanks(enemy));

            _teamCards[Team(enemy)] = Strip(enemy ? "enemy_cards" : "allied_cards", page, 150f, 8f);
            _teamEditors[Team(enemy)] = VBox("editor", page, 9f);
        }

        private void RefreshAllTeams()
        {
            RefreshTeam(false);
            RefreshTeam(true);
        }

        /// <summary>_refresh_team.</summary>
        private void RefreshTeam(bool enemy)
        {
            var team = Team(enemy);
            var cards = _teamCards[team];
            var editor = _teamEditors[team];
            if (cards == null || editor == null)
            {
                return;
            }

            ClearChildren(cards);
            ClearChildren(editor);
            var count = SandboxEditor.ActiveSlotCount(_config, enemy);
            if (_selectTanks[team] != null)
            {
                _selectTanks[team].interactable = count > 0;
            }

            if (count <= 0)
            {
                Hint(editor, S("training:no_allied_bots"));
                return;
            }

            _selectedSlots[team] = Mathf.Clamp(_selectedSlots[team], 0, count - 1);
            var slots = _config.Slots(enemy);
            for (var i = 0; i < count; i++)
            {
                var index = i;
                var slot = slots[i];
                var chosen = i == _selectedSlots[team];
                if (SandboxEditor.IsExact(slot, _presets.Tanks))
                {
                    TankCard("selected_tank_" + i, cards, _presets.Tanks.Get(slot.TankName), chosen,
                        () => SelectSlot(enemy, index));
                }
                else
                {
                    var random = Btn("random_slot_" + i, cards, S(RandomKey) + "\n#" + (i + 1), chosen,
                        196f, 132f, 20);
                    Bind(random, () => OpenRandomSlotPicker(enemy, index));
                }
            }

            BuildSlotEditor(editor, enemy, _selectedSlots[team]);
        }

        /// <summary>_select_slot.</summary>
        public void SelectSlot(bool enemy, int index)
        {
            _selectedSlots[Team(enemy)] = index;
            RefreshTeam(enemy);
        }

        private void OpenRandomSlotPicker(bool enemy, int index)
        {
            _selectedSlots[Team(enemy)] = index;
            OpenTankPicker(enemy, index);
        }

        private void BuildSlotEditor(RectTransform parent, bool enemy, int index)
        {
            var slot = _config.Slots(enemy)[index];
            Bold(parent, SandboxEditor.Percent("%s · %s #%d", S("training:section/slot"),
                S(enemy ? "training:tab/enemies" : "training:tab/allies"), index + 1), 22);
            BuildTankPage(parent, enemy, index, slot);
            if (!SandboxEditor.IsExact(slot, _presets.Tanks))
            {
                Hint(parent, S("training:random_slot_no_upgrades"));
                return;
            }

            BuildModulePicker(parent, enemy, index, slot);
            BuildBehaviourPage(parent, slot);
            Section(parent, "training:section/slot_overrides", "training:hint/slot_modifiers");
            MultiplierRows(parent, slot.Multipliers);
        }

        private void BuildTankPage(RectTransform parent, bool enemy, int index, SandboxBotSlot slot)
        {
            var actions = HBox("tank_actions", parent, 10f, 62f);
            var choose = Btn("select_tank", actions, S("training:select_tank"), true, 132f, ControlHeight, 20);
            Elem(choose).flexibleWidth = 1f;
            Bind(choose, () => OpenTankPicker(enemy, index));
            var random = Btn("random_tank", actions, S("training:random_tank"), false, 132f, ControlHeight, 20);
            Elem(random).flexibleWidth = 1f;
            Bind(random, () => SetSlotRandom(enemy, index));
            if (!SandboxEditor.IsExact(slot, _presets.Tanks))
            {
                Hint(parent, S("training:random_slot_hint"));
            }
            else
            {
                var info = _presets.Tanks.Get(slot.TankName);
                Bold(parent, SandboxEditor.Percent("%s · %s", TankName(info), S("training:hint/exact_tank")), 20);
            }
        }

        private void BuildBehaviourPage(RectTransform parent, SandboxBotSlot slot)
        {
            Section(parent, "training:section/bot_behavior", "training:hint/behaviour");
            OptionRow(parent, "skill", "training:bot/skill", SkillItems(), slot.Skill, v => slot.Skill = v, null);
            ToggleRow(parent, "harmless", "training:bot/harmless", slot.Harmless, v => slot.Harmless = v);
            ToggleRow(parent, "stationary", "training:bot/stationary", slot.Stationary, v => slot.Stationary = v);
        }

        /// <summary>_set_slot_random.</summary>
        public void SetSlotRandom(bool enemy, int index)
        {
            if (SandboxEditor.MakeSlotRandom(_config, enemy, index))
            {
                RefreshTeam(enemy);
            }
        }

        /// <summary>_clear_team_tanks.</summary>
        public void ClearTeamTanks(bool enemy)
        {
            SandboxEditor.ClearTeam(_config, enemy);
            _selectedSlots[Team(enemy)] = 0;
            RefreshTeam(enemy);
        }

        private void BuildModulePicker(RectTransform parent, bool enemy, int index, SandboxBotSlot slot)
        {
            var info = _presets.Tanks.Get(slot.TankName);
            Section(parent, "training:modules", "training:hint/modules");
            ModuleGroup(parent, "training:module/gun", ModuleKind.Gun, Modules(info.Guns), info.Premium,
                slot.GunId, id => { slot.GunId = id; RefreshTeam(enemy); });
            ModuleGroup(parent, "training:module/engine", ModuleKind.Engine, Modules(info.Engines), info.Premium,
                slot.EngineId, id => { slot.EngineId = id; RefreshTeam(enemy); });
            ModuleGroup(parent, "training:module/tracks", ModuleKind.Tracks, Modules(info.Tracks), info.Premium,
                slot.TrackId, id => { slot.TrackId = id; RefreshTeam(enemy); });
        }

        private static List<ModuleInfo> Modules<T>(IList<T> source) where T : ModuleInfo
        {
            var list = new List<ModuleInfo>();
            if (source != null)
            {
                foreach (var module in source) { list.Add(module); }
            }

            return list;
        }

        private void ModuleGroup(RectTransform parent, string labelKey, ModuleKind kind, IList<ModuleInfo> modules,
            bool premium, int selected, Action<int> pick)
        {
            Bold(parent, S(labelKey), 20);
            var strip = Strip("modules_" + kind.ToString().ToLowerInvariant(), parent, 132f, 7f);
            var random = Btn("random", strip, S(RandomKey), selected == SandboxBotSlot.RandomSelection,
                150f, 122f, 20);
            Bind(random, () => pick(SandboxBotSlot.RandomSelection));
            for (var i = 0; i < modules.Count; i++)
            {
                var id = i;
                ModuleCard(strip, modules[i], kind, premium, selected == i, () => pick(id));
            }
        }

        // ------------------------------------------------------------------
        // MODIFIERS / CHEATS.
        // ------------------------------------------------------------------

        private void BuildModifiersTab()
        {
            var root = Tab("modifiers");
            var pages = SubPages(root, ModifierPageKeys, _modifierPage, index => _modifierPage = index);
            Section(pages[0], "training:section/global_overrides", "training:hint/global_modifiers");
            MultiplierRows(pages[0], _config.Global);
            Section(pages[1], "training:section/player_overrides", "training:hint/player_modifiers");
            MultiplierRows(pages[1], _config.Player);
        }

        private void BuildCheatsTab()
        {
            var c = _config;
            var root = Tab("cheats");
            var page = ScrollPage("page", root);
            Section(page, "training:cheats/general", "training:cheats/hint");
            ToggleRow(page, "cheat_infinite_ammo", "training:cheats/infinite_ammo", c.CheatInfiniteAmmo, v => c.CheatInfiniteAmmo = v);
            ToggleRow(page, "cheat_no_recoil", "training:cheats/no_recoil", c.CheatNoRecoil, v => c.CheatNoRecoil = v);
            ToggleRow(page, "cheat_wallhack", "training:cheats/wallhack", c.CheatWallhack, v => c.CheatWallhack = v);
            FloatSlider(page, "training:cheats/wallhack_close", c.WallhackCloseDistance, 0f, 300f, 5f, "m", v => c.WallhackCloseDistance = v);
            FloatSlider(page, "training:cheats/wallhack_range", c.WallhackMaxDistance, 50f, 720f, 10f, "m", v => c.WallhackMaxDistance = v);

            Section(page, "training:cheats/aimbot", "training:cheats/aimbot_hint");
            ToggleRow(page, "cheat_aimbot", "training:cheats/aimbot_enabled", c.CheatAimbot, v => c.CheatAimbot = v);
            OptionRow(page, "aimbot_priority", "training:cheats/aimbot_priority", new[]
            {
                Item("training:cheats/priority_crosshair", (int)SandboxAimbotPriority.Crosshair),
                Item("training:cheats/priority_distance", (int)SandboxAimbotPriority.Distance),
                Item("training:cheats/priority_health", (int)SandboxAimbotPriority.LowHealth),
            }, (int)c.AimbotPriority, v => c.AimbotPriority = (SandboxAimbotPriority)v, null);
            OptionRow(page, "aimbot_aim_point", "training:cheats/aimbot_point", new[]
            {
                Item("training:cheats/point_center", (int)SandboxAimPoint.Center),
                Item("training:cheats/point_turret", (int)SandboxAimPoint.Turret),
                Item("training:cheats/point_hull", (int)SandboxAimPoint.Hull),
                Item("training:cheats/point_tracks", (int)SandboxAimPoint.Tracks),
            }, (int)c.AimbotAimPoint, v => c.AimbotAimPoint = (SandboxAimPoint)v, null);
            FloatSlider(page, "training:cheats/aimbot_fov", c.AimbotFov, 1f, 180f, 1f, "°", v => c.AimbotFov = v);
            FloatSlider(page, "training:cheats/aimbot_range", c.AimbotMaxDistance, 10f, 720f, 10f, "m", v => c.AimbotMaxDistance = v);
            FloatSlider(page, "training:cheats/aimbot_lead", c.AimbotLead, 0f, 3f, 0.1f, "×", v => c.AimbotLead = v);
            FloatSlider(page, "training:cheats/aimbot_smoothing", c.AimbotSmoothing, 0f, 30f, 0.5f, string.Empty, v => c.AimbotSmoothing = v);
            ToggleRow(page, "aimbot_through_walls", "training:cheats/aimbot_walls", c.AimbotThroughWalls, v => c.AimbotThroughWalls = v);
            ToggleRow(page, "aimbot_require_spotted", "training:cheats/aimbot_spotted", c.AimbotRequireSpotted, v => c.AimbotRequireSpotted = v);
            ToggleRow(page, "aimbot_sticky", "training:cheats/aimbot_sticky", c.AimbotSticky, v => c.AimbotSticky = v);

            Section(page, "training:cheats/triggerbot", "training:cheats/triggerbot_hint");
            ToggleRow(page, "cheat_triggerbot", "training:cheats/triggerbot_enabled", c.CheatTriggerbot, v => c.CheatTriggerbot = v);
            FloatSlider(page, "training:cheats/triggerbot_delay", c.TriggerbotDelay, 0f, 5f, 0.05f, "s", v => c.TriggerbotDelay = v);
            FloatSlider(page, "training:cheats/triggerbot_range", c.TriggerbotMaxDistance, 10f, 720f, 10f, "m", v => c.TriggerbotMaxDistance = v);
            FloatSlider(page, "training:cheats/triggerbot_tolerance", c.TriggerbotToleranceDeg, 0.1f, 15f, 0.1f, "°", v => c.TriggerbotToleranceDeg = v);
            ToggleRow(page, "triggerbot_require_visible", "training:cheats/triggerbot_visible", c.TriggerbotRequireVisible, v => c.TriggerbotRequireVisible = v);
            ToggleRow(page, "triggerbot_require_spotted", "training:cheats/triggerbot_spotted", c.TriggerbotRequireSpotted, v => c.TriggerbotRequireSpotted = v);
            ToggleRow(page, "triggerbot_require_penetration", "training:cheats/triggerbot_penetration", c.TriggerbotRequirePenetration, v => c.TriggerbotRequirePenetration = v);
            ToggleRow(page, "triggerbot_require_direct_hit", "training:cheats/triggerbot_direct", c.TriggerbotRequireDirectHit, v => c.TriggerbotRequireDirectHit = v);
            ToggleRow(page, "triggerbot_use_aimbot", "training:cheats/triggerbot_aimbot", c.TriggerbotUseAimbot, v => c.TriggerbotUseAimbot = v);
            ToggleRow(page, "triggerbot_fire_while_moving", "training:cheats/triggerbot_move", c.TriggerbotFireWhileMoving, v => c.TriggerbotFireWhileMoving = v);
        }

        // ------------------------------------------------------------------
        // Summary, reset, apply, close, start.
        // ------------------------------------------------------------------

        /// <summary>_refresh_summary.</summary>
        private void RefreshSummary()
        {
            if (_summary == null)
            {
                return;
            }

            _summary.text = SandboxEditor.Percent(S("training:summary"), MapName(_config), ModeName(_config),
                _config.AlliedTeamSize, _config.EnemyTeamSize);
        }

        private string MapName(SandboxConfig config)
        {
            var map = config.MapIsRandom ? null : _presets.Map(config.SelectedMapId);
            return map == null ? S(RandomKey) : S(map.NameKey);
        }

        private string ModeName(SandboxConfig config)
        {
            switch (config.BaseMode)
            {
                case SandboxBaseMode.Gravity: return S("battle_mode:gravity/name");
                case SandboxBaseMode.Uprising: return S("battle_mode:uprising/name");
                case SandboxBaseMode.BigBoss: return S("battle_mode:bossmode/name");
                default: return S("battle_mode:normal/name");
            }
        }

        /// <summary>_reset_preset: defaults into the active preset, then a rebuild.</summary>
        public void ResetPreset()
        {
            var fresh = new SandboxConfig();
            fresh.Validate(_presets.MapIds);
            _presets.Library.ReplaceActiveConfig(fresh);
            _presets.Save();
            RebuildScreen();
        }

        /// <summary>_apply_and_close: select the Sandbox, save, notify the hangar.</summary>
        public void Apply()
        {
            CommitPresetName();
            _presets.Save();
            var applied = _applied;
            _applied = null;
            Dismiss();
            if (applied != null)
            {
                applied();
            }
        }

        /// <summary>_close: save and leave.</summary>
        public void Close()
        {
            CommitPresetName();
            _presets.Save();
            Dismiss();
        }

        /// <summary>
        /// _start_training: lock the button, save, hand the map to the hangar.
        /// A RANDOM map is passed as null so the launcher rolls it.
        /// </summary>
        public void StartBattle()
        {
            if (_launchPending)
            {
                return;
            }

            _launchPending = true;
            if (StartButton != null)
            {
                StartButton.interactable = false;
                Caption(StartButton).text = S("training:loading");
            }

            CommitPresetName();
            _presets.Save();
            var mapId = _config.MapIsRandom || !_presets.Contains(_config.SelectedMapId)
                ? null
                : _config.SelectedMapId;
            var start = _startRequested;
            _startRequested = null;
            Dismiss();
            if (start != null)
            {
                start(mapId);
            }
        }

        private void Dismiss()
        {
            var closed = _closed;
            _closed = null;
            Kill(gameObject);
            if (closed != null)
            {
                closed();
            }
        }

        // ------------------------------------------------------------------
        // Rows.
        // ------------------------------------------------------------------

        private static KeyValuePair<string, int> Item(string key, int id)
        {
            return new KeyValuePair<string, int>(key, id);
        }

        private void Section(RectTransform parent, string titleKey, string hintKey)
        {
            Bold(parent, S(titleKey), 24, SectionColor);
            if (!string.IsNullOrEmpty(hintKey))
            {
                Hint(parent, S(hintKey));
            }

            // HSeparator: a 4 px row with a one-pixel line through its middle.
            var separator = UiFactory.Node("separator", parent);
            var size = Elem(separator);
            size.minHeight = 4f;
            size.preferredHeight = 4f;
            var line = UiFactory.Picture("line", separator, null);
            line.color = SeparatorColor;
            GodotRect.Apply(line.rectTransform, 0f, 0.5f, 1f, 0.5f, 0f, -0.5f, 0f, 0.5f);
        }

        /// <summary>_form_row: a ROW_TEXTURE panel, 72 px tall, label then control.</summary>
        private RectTransform FormRow(RectTransform parent, string name, string labelKey)
        {
            var panel = _kit.Picture(name, parent, RowRegion, 0f, 0f, 0f, 0f);
            Elem(panel).minHeight = FormRowHeight;
            var row = panel.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset(10, 10, 10, 10);
            row.spacing = 14f;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            row.childAlignment = TextAnchor.MiddleLeft;

            var label = UiFactory.Label("label", panel.transform, _kit.Regular, 20, TextColor,
                TextAnchor.MiddleLeft, S(labelKey));
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            var size = Elem(label);
            size.minWidth = FormLabelWidth;
            size.flexibleWidth = 1f;
            return panel.rectTransform;
        }

        /// <summary>_option_row: an OptionButton opening a list of choices.</summary>
        private Button OptionRow(RectTransform parent, string name, string labelKey,
            IList<KeyValuePair<string, int>> items, int selected, Action<int> set, Action refresh)
        {
            var row = FormRow(parent, name + "_row", labelKey);
            var current = selected;
            var index = Math.Max(0, IndexOf(items, current));
            var option = Btn(name, row, S(items[index].Key), false, 290f, ControlHeight, 19);
            Elem(option).flexibleWidth = 1f;
            Bind(option, () =>
            {
                var labels = new List<string>();
                foreach (var item in items) { labels.Add(S(item.Key)); }
                OpenChoice(labelKey, labels, IndexOf(items, current), picked =>
                {
                    current = items[picked].Value;
                    set(current);
                    Caption(option).text = S(items[picked].Key);
                    if (refresh != null) { refresh(); }
                    RefreshSummary();
                });
            });
            return option;
        }

        private static int IndexOf(IList<KeyValuePair<string, int>> items, int id)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Value == id) { return i; }
            }

            return -1;
        }

        /// <summary>
        /// _spin_row: Godot's SpinBox as - value + buttons (hold to repeat),
        /// snapped to the row's step and clamped to its range.
        /// </summary>
        private void SpinRow(RectTransform parent, string name, string labelKey, float value, float min, float max,
            float step, string suffix, Action<float> set, bool refreshTeams)
        {
            var row = FormRow(parent, name + "_row", labelKey);
            var box = HBox(name, row, 4f, ControlHeight);
            var boxSize = Elem(box);
            boxSize.minWidth = 220f;
            boxSize.preferredWidth = 220f;
            var minus = Btn("minus", box, "-", false, ControlHeight, ControlHeight, 26);
            var label = UiFactory.Label("value", box, _kit.Bold, 19, TextColor, TextAnchor.MiddleCenter,
                SandboxEditor.SliderText(value, suffix));
            var labelSize = Elem(label);
            labelSize.minWidth = 100f;
            labelSize.flexibleWidth = 1f;
            var plus = Btn("plus", box, "+", false, ControlHeight, ControlHeight, 26);

            var current = SandboxEditor.Snap(value, min, max, step);
            Action<float> change = delta =>
            {
                var next = SandboxEditor.Snap(current + delta, min, max, step);
                if (Mathf.Approximately(next, current))
                {
                    return;
                }

                current = next;
                set(next);
                label.text = SandboxEditor.SliderText(next, suffix);
                if (refreshTeams) { RefreshAllTeams(); }
                RefreshSummary();
            };
            Repeat(minus, () => change(-step));
            Repeat(plus, () => change(step));
        }

        private void Repeat(Button button, Action action)
        {
            var repeat = button.gameObject.AddComponent<SandboxHoldRepeat>();
            repeat.Configure(action);
            Bind(button, () =>
            {
                if (!repeat.ConsumeRepeat()) { action(); }
            });
        }

        private void IntSlider(RectTransform parent, string labelKey, int value, float min, float max, float step,
            Action<int> set)
        {
            SliderRow(parent, labelKey, value, min, max, step,
                v => SandboxEditor.SliderText(Mathf.Round(v), string.Empty),
                v => set(Mathf.RoundToInt(v)));
        }

        private void FloatSlider(RectTransform parent, string labelKey, float value, float min, float max, float step,
            string suffix, Action<float> set)
        {
            SliderRow(parent, labelKey, value, min, max, step, v => SandboxEditor.SliderText(v, suffix), set);
        }

        /// <summary>A multiplier on the logarithmic offset scale; only ×value is shown.</summary>
        private void MultiplierRow(RectTransform parent, string labelKey, float multiplier, Action<float> set)
        {
            SliderRow(parent, labelKey, SandboxMultiplierMath.MultiplierToOffset(multiplier),
                SandboxMultiplierMath.MinOffset, SandboxMultiplierMath.MaxOffset, SandboxMultiplierMath.OffsetStep,
                SandboxEditor.MultiplierText, offset => set(SandboxMultiplierMath.OffsetToMultiplier(offset)));
        }

        private void MultiplierRows(RectTransform parent, SandboxMultipliers target)
        {
            for (var i = 0; i < SandboxMultipliers.Count; i++)
            {
                var stat = (SandboxStat)i;
                MultiplierRow(parent, MultiplierKeys[i], target[stat], v => target[stat] = v);
            }
        }

        /// <summary>
        /// _slider_row: the settings-screen slider assets (frame, track, fill,
        /// thumb) with the value label on the right.
        /// </summary>
        private void SliderRow(RectTransform parent, string labelKey, float value, float min, float max, float step,
            Func<float, string> text, Action<float> set)
        {
            var row = FormRow(parent, "slider_row", labelKey);
            var host = UiFactory.Node("slider_host", row);
            var hostSize = Elem(host);
            hostSize.minWidth = 260f;
            hostSize.minHeight = ControlHeight;
            hostSize.preferredHeight = ControlHeight;
            hostSize.flexibleWidth = 1f;

            var frame = UiFactory.Picture("slider_bg", host, SlicedArt(FrameRegion, 24f));
            frame.type = frame.sprite != null && frame.sprite.border.sqrMagnitude > 0f
                ? Image.Type.Sliced
                : Image.Type.Simple;
            GodotRect.FullRect(frame.rectTransform);

            var slider = UiFactory.Node("slider", host);
            GodotRect.Apply(slider, 0f, 0f, 1f, 1f, 6f, 0f, -6f, 0f, GodotRect.TopLeftPivot);
            var hit = slider.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            hit.raycastTarget = true;

            var bar = UiFactory.Picture("bar", slider, null);
            bar.color = SliderBarColor;
            GodotRect.Apply(bar.rectTransform, 0f, 0.5f, 1f, 0.5f, 0f, -SliderBarHeight * 0.5f, 0f,
                SliderBarHeight * 0.5f);
            var fill = UiFactory.Picture("grabber_area", slider, null);
            fill.color = SliderFillColor;
            var grabber = UiFactory.Picture("grabber", slider, _kit.Art(ThumbRegion));
            grabber.preserveAspect = true;

            var label = UiFactory.Label("value", row, _kit.Bold, 19, TextColor, TextAnchor.MiddleRight, text(value));
            var labelSize = Elem(label);
            labelSize.minWidth = 105f;
            labelSize.preferredWidth = 105f;

            var control = slider.gameObject.AddComponent<UiValueSlider>();
            var current = value;
            control.Configure(min, max, step, SliderGrabber, v =>
            {
                var next = SandboxEditor.Snap(v, min, max, step);
                if (Mathf.Approximately(next, current))
                {
                    return;
                }

                current = next;
                set(next);
                label.text = text(next);
                PlaceSlider(fill, grabber, next, min, max);
                RefreshSummary();
            });
            PlaceSlider(fill, grabber, value, min, max);
        }

        /// <summary>
        /// Godot's HSlider geometry without knowing the live width: the
        /// grabber's centre travels from half a grabber to width minus half a
        /// grabber, and the fill ends under it.
        /// </summary>
        private static void PlaceSlider(Image fill, Image grabber, float value, float min, float max)
        {
            var t = max > min ? Mathf.Clamp01((value - min) / (max - min)) : 0f;
            var shift = (SliderGrabber * 0.5f) - (t * SliderGrabber);

            var g = grabber.rectTransform;
            g.anchorMin = new Vector2(t, 0.5f);
            g.anchorMax = new Vector2(t, 0.5f);
            g.pivot = new Vector2(0.5f, 0.5f);
            g.sizeDelta = new Vector2(SliderGrabber, SliderGrabber);
            g.anchoredPosition = new Vector2(shift, 0f);

            var f = fill.rectTransform;
            f.pivot = new Vector2(0f, 0.5f);
            f.anchorMin = new Vector2(0f, 0.5f);
            f.anchorMax = new Vector2(t, 0.5f);
            f.offsetMin = new Vector2(0f, -SliderBarHeight * 0.5f);
            f.offsetMax = new Vector2(shift, SliderBarHeight * 0.5f);
        }

        /// <summary>_toggle_row: ON / OFF, green while on.</summary>
        private Button ToggleRow(RectTransform parent, string name, string labelKey, bool value, Action<bool> set)
        {
            var row = FormRow(parent, name + "_row", labelKey);
            var on = value;
            var toggle = Btn(name, row, S(on ? OnKey : OffKey), on, 220f, ControlHeight, 20);
            Bind(toggle, () =>
            {
                on = !on;
                set(on);
                Caption(toggle).text = S(on ? OnKey : OffKey);
                SetGreen(toggle, on);
                RefreshSummary();
            });
            return toggle;
        }

        // ------------------------------------------------------------------
        // Cards.
        // ------------------------------------------------------------------

        /// <summary>carousel_tank.tscn in picker mode (setup_picker).</summary>
        private RectTransform TankCard(string name, RectTransform parent, TankInfo info, bool selected, Action pick)
        {
            var card = UiFactory.Picture(name, parent, _kit.Art(HangarUiKit.CarouselNationRegion(info.Nation)));
            var root = card.rectTransform;
            root.pivot = GodotRect.TopLeftPivot;
            root.sizeDelta = new Vector2(TankCardWidth, TankCardHeight);
            var size = Elem(card);
            size.minWidth = TankCardWidth;
            size.preferredWidth = TankCardWidth;
            size.minHeight = TankCardHeight;
            size.preferredHeight = TankCardHeight;

            var iconSprite = _kit.Art("tank_icon_" + TankModelAssets.For(info).DataId);
            if (iconSprite == null && info.Mod)
            {
                iconSprite = ModRuntime.Icon(info.Name);
            }

            if (iconSprite != null)
            {
                var icon = UiFactory.Picture("icon", root, iconSprite);
                KeepCentered(icon, 0.43f, 4.5f, 195.2f, 122f, 0.61f);
            }
            else
            {
                var unknown = UiFactory.Label("icon_unknown", root, _kit.Bold, 64, HangarUiKit.TankGlyphTint(info),
                    TextAnchor.MiddleCenter, "?");
                HangarUiKit.Place(unknown.rectTransform, 40f, 14f, 115f, 86f);
            }

            var tankClass = UiFactory.Picture("class", root, _kit.Art(HangarUiKit.TankClassRegion(info)));
            tankClass.color = HangarUiKit.TankGlyphTint(info);
            KeepCentered(tankClass, 6.48f, 1.92f, 27.04f, 30.16f, 0.52f);
            var level = UiFactory.Picture("lvl", root, _kit.Art(HangarUiKit.TankLevelRegion(info)));
            level.color = HangarUiKit.TankGlyphTint(info);
            KeepCentered(level, 32.42f, 2.88f, 29.7f, 30.78f, 0.54f);

            _kit.Label("name", root, 24, TextAnchor.MiddleLeft, TankName(info), _kit.Bold,
                HangarUiKit.TankNameColor(info), 8f, 100f, 180f, 25f);

            var frame = _kit.Picture("selected", root, "tank_slot_selected", 0f, 1f, 195f, 128f);
            frame.gameObject.SetActive(selected);

            var button = _kit.StyleButton("button", root, null, 0f, 1f, 197f, 128f);
            Bind(button, pick);
            return root;
        }

        private void KeepCentered(Image image, float x, float y, float width, float height, float scale)
        {
            var rect = new Rect(x, y, width, height);
            var placed = image.sprite != null
                ? HangarScreen.KeepCenteredRect(rect, image.sprite.rect.size, scale)
                : rect;
            HangarUiKit.Place(image.rectTransform, placed.x, placed.y, placed.width, placed.height);
        }

        /// <summary>ui_tank_module.tscn in picker mode (init_picker).</summary>
        private void ModuleCard(RectTransform parent, ModuleInfo module, ModuleKind kind, bool premium, bool installed,
            Action pick)
        {
            var status = installed ? ResearchStatus.InstalledBought : ResearchStatus.Researched;
            var tile = _kit.Picture("module_" + module.Name, parent, HangarUiKit.ModuleBackgroundRegion(status),
                0f, 0f, ModuleCardWidth, ModuleCardHeight);
            var size = Elem(tile);
            size.minWidth = ModuleCardWidth;
            size.preferredWidth = ModuleCardWidth;
            size.minHeight = ModuleCardHeight;
            size.preferredHeight = ModuleCardHeight;
            var root = tile.rectTransform;

            _kit.Picture("icon", root, HangarUiKit.ModuleKindRegion(kind), 15.438f, 10.987f, 109.124f, 98.026f);
            var level = _kit.Centered("lvl", root, HangarUiKit.LevelRegion(module.Level), -9f, -2f, 76f, 57f);
            level.rectTransform.localScale = new Vector3(0.94f, 0.94f, 1f);
            level.color = premium ? PremiumLevelTint : Color.white;

            var marker = _kit.Picture("installed", root, "module_installed", 92f, 69.3158f, 58f, 58f);
            marker.gameObject.SetActive(installed);
            var gun = module as GunInfo;
            var autoloader = _kit.Picture("autoloader", root, "module_autoloader", 91f, -10f, 76f, 57f);
            autoloader.rectTransform.localScale = new Vector3(0.94f, 0.94f, 1f);
            autoloader.gameObject.SetActive(gun != null && gun.Burst != null && gun.Burst.HasBursts);
            var selected = _kit.Picture("selected", root, "tree_selector", -3f, -2.6841965f, 146f, 126f);
            selected.type = Image.Type.Sliced;
            selected.gameObject.SetActive(installed);

            var button = _kit.StyleButton("ButtonCustom", root, null, 0f, 0f, ModuleCardWidth, ModuleCardHeight);
            _kit.Bind(button, pick, AudioService.SoundSelect);
        }

        // ------------------------------------------------------------------
        // Containers.
        // ------------------------------------------------------------------

        private RectTransform Tab(string name)
        {
            var root = UiFactory.Node(name, _content);
            GodotRect.FullRect(root);
            _tabs.Add(root);
            return root;
        }

        /// <summary>
        /// _build_subnavigation + hidden TabContainer pages: a 58 px button row
        /// and one scroll page per button, the chosen one green and shown.
        /// </summary>
        private List<RectTransform> SubPages(RectTransform root, string[] keys, int selected, Action<int> chosen)
        {
            var row = HBox("subnavigation", root, 5f, 0f);
            GodotRect.Apply(row, 0f, 0f, 1f, 0f, 0f, 0f, 0f, NavigationHeight, GodotRect.TopLeftPivot);
            var area = UiFactory.Node("pages", root);
            GodotRect.Apply(area, 0f, 0f, 1f, 1f, 0f, NavigationHeight + 8f, 0f, 0f, GodotRect.TopLeftPivot);

            var buttons = new List<Button>();
            var views = new List<GameObject>();
            var pages = new List<RectTransform>();
            selected = Mathf.Clamp(selected, 0, keys.Length - 1);
            for (var i = 0; i < keys.Length; i++)
            {
                var button = Btn("sub_" + i, row, S(keys[i]), i == selected, 132f, NavigationHeight, 20);
                Elem(button).flexibleWidth = 1f;
                buttons.Add(button);
                var page = ScrollPage("page_" + i, area);
                pages.Add(page);
                views.Add(page.parent.gameObject);
                views[i].SetActive(i == selected);
            }

            for (var i = 0; i < buttons.Count; i++)
            {
                var index = i;
                Bind(buttons[i], () =>
                {
                    for (var j = 0; j < buttons.Count; j++)
                    {
                        views[j].SetActive(j == index);
                        SetGreen(buttons[j], j == index);
                    }

                    chosen(index);
                });
            }

            return pages;
        }

        /// <summary>
        /// _scroll_with_vbox: a full-rect vertical ScrollContainer around a
        /// VBox (separation 9). Returns the VBox.
        /// </summary>
        private static RectTransform ScrollPage(string name, RectTransform parent)
        {
            var view = UiFactory.Node(name, parent);
            GodotRect.FullRect(view);
            return ScrollContent(view);
        }

        private static RectTransform ScrollContent(RectTransform view)
        {
            view.gameObject.AddComponent<RectMask2D>();
            var catcher = view.gameObject.AddComponent<Image>();
            catcher.color = Color.clear;
            catcher.raycastTarget = true;

            var content = VBox("content", view, 9f);
            content.pivot = GodotRect.TopLeftPivot;
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            var scroll = view.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = view;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            return content;
        }

        /// <summary>
        /// A horizontal-only ScrollContainer around an HBox. It is a
        /// <see cref="NestedScrollRect"/>: a vertical swipe that starts on the
        /// cards or modules scrolls the page around the strip.
        /// </summary>
        private static RectTransform Strip(string name, RectTransform parent, float height, float spacing)
        {
            var view = UiFactory.Node(name, parent);
            var size = Elem(view);
            size.minHeight = height;
            size.preferredHeight = height;
            size.flexibleWidth = 1f;
            view.gameObject.AddComponent<RectMask2D>();
            var catcher = view.gameObject.AddComponent<Image>();
            catcher.color = Color.clear;
            catcher.raycastTarget = true;

            var content = UiFactory.Node("content", view);
            content.pivot = GodotRect.TopLeftPivot;
            content.anchorMin = new Vector2(0f, 0f);
            content.anchorMax = new Vector2(0f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            var row = content.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.spacing = spacing;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            row.childAlignment = TextAnchor.UpperLeft;
            content.gameObject.AddComponent<ContentSizeFitter>().horizontalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            var scroll = view.gameObject.AddComponent<NestedScrollRect>();
            scroll.viewport = view;
            scroll.content = content;
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            return content;
        }

        private static RectTransform VBox(string name, Transform parent, float spacing)
        {
            var rt = UiFactory.Node(name, parent);
            var group = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            group.spacing = spacing;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
            group.childAlignment = TextAnchor.UpperLeft;
            return rt;
        }

        private static RectTransform HBox(string name, Transform parent, float spacing, float height)
        {
            var rt = UiFactory.Node(name, parent);
            var group = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            group.spacing = spacing;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = true;
            group.childAlignment = TextAnchor.MiddleLeft;
            if (height > 0f)
            {
                var size = Elem(rt);
                size.minHeight = height;
                size.preferredHeight = height;
            }

            return rt;
        }

        private static LayoutElement Elem(Component component)
        {
            var element = component.GetComponent<LayoutElement>();
            return element != null ? element : component.gameObject.AddComponent<LayoutElement>();
        }

        // ------------------------------------------------------------------
        // Widgets.
        // ------------------------------------------------------------------

        /// <summary>
        /// _native_button: ButtonSmallGeneric (or ButtonGreen), 132x54 by
        /// default, centred text that shrinks instead of overflowing.
        /// </summary>
        private Button Btn(string name, Transform parent, string text, bool green, float width, float height,
            int fontSize)
        {
            var button = _kit.StyleButton(name, (RectTransform)parent, green ? GreenRegion : GenericRegion,
                0f, 0f, width, height);
            var label = UiFactory.Label("label", button.transform, _kit.Regular, fontSize, ButtonTextColor,
                TextAnchor.MiddleCenter, text);
            GodotRect.Apply(label.rectTransform, 0f, 0f, 1f, 1f, 6f, 2f, -6f, -2f);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = Math.Max(10, fontSize / 2);
            label.resizeTextMaxSize = fontSize;
            var size = Elem(button);
            size.minWidth = width;
            size.preferredWidth = width;
            size.minHeight = height;
            size.preferredHeight = height;
            DecorateButton(button);
            return button;
        }

        private static Text Caption(Button button)
        {
            var label = button.transform.Find("label");
            return label == null ? null : label.GetComponent<Text>();
        }

        private void SetGreen(Button button, bool green)
        {
            SetVariant(button, green ? GreenRegion : GenericRegion);
        }

        private void SetVariant(Button button, string region)
        {
            if (button == null)
            {
                return;
            }

            var image = button.GetComponent<Image>();
            image.sprite = _kit.Art(region);
            image.color = Color.white;
            image.type = image.sprite != null && image.sprite.border.sqrMagnitude > 0f
                ? Image.Type.Sliced
                : Image.Type.Simple;
            _kit.ApplyStyleStates(button, region);
        }

        private void AddLeadingIcon(Button button, string region)
        {
            var sprite = _kit.Art(region);
            if (sprite == null)
            {
                return;
            }

            var icon = UiFactory.Picture("icon", button.transform, sprite);
            icon.preserveAspect = true;
            GodotRect.Apply(icon.rectTransform, 0f, 0.5f, 0f, 0.5f, 8f, -15f, 40f, 15f);
            var label = Caption(button);
            if (label != null)
            {
                GodotRect.Apply(label.rectTransform, 0f, 0f, 1f, 1f, 42f, 2f, -6f, -2f);
            }
        }

        private Text Bold(Transform parent, string text, int size)
        {
            return Bold(parent, text, size, TextColor);
        }

        private Text Bold(Transform parent, string text, int size, Color color)
        {
            var label = UiFactory.Label("bold", parent, _kit.Bold, size, color, TextAnchor.MiddleLeft, text);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            return label;
        }

        /// <summary>_hint: 18 px, word-wrapped, modulate (0.82, 0.86, 0.9).</summary>
        private Text Hint(Transform parent, string text)
        {
            var label = UiFactory.Label("hint", parent, _kit.Regular, 18, HintColor, TextAnchor.UpperLeft, text);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            return label;
        }

        private void Bind(Button button, Action action)
        {
            _kit.Bind(button, action, AudioService.SoundEnter);
        }

        private string S(string key)
        {
            return _kit.Str(key);
        }

        private string TankName(TankInfo info)
        {
            return info == null ? string.Empty : _kit.Lang.GetTankName(info, true);
        }

        /// <summary>
        /// A nine-patch copy of an atlas region for the source's NinePatchRect
        /// patch margins; regions that already carry a border are used as is.
        /// </summary>
        private Sprite SlicedArt(string region, float border)
        {
            Sprite cached;
            if (_slicedArt.TryGetValue(region, out cached))
            {
                return cached;
            }

            var sprite = _kit.Art(region);
            if (sprite != null && sprite.border.sqrMagnitude <= 0f && sprite.texture != null)
            {
                var rect = sprite.rect;
                var b = Mathf.Min(border, Mathf.Floor(Mathf.Min(rect.width, rect.height) * 0.5f) - 1f);
                sprite = Sprite.Create(sprite.texture, rect, new Vector2(0.5f, 0.5f), sprite.pixelsPerUnit, 0,
                    SpriteMeshType.FullRect, new Vector4(b, b, b, b));
                _ownedSprites.Add(sprite);
            }

            _slicedArt[region] = sprite;
            return sprite;
        }

        private static int Team(bool enemy)
        {
            return enemy ? 1 : 0;
        }

        /// <summary>Godot's queue_free: hidden at once (layout ignores it), freed after the frame.</summary>
        private static void Kill(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            go.SetActive(false);
            if (Application.isPlaying) { Destroy(go); }
            else { DestroyImmediate(go); }
        }

        private static void ClearChildren(Transform node)
        {
            if (node == null)
            {
                return;
            }

            for (var i = node.childCount - 1; i >= 0; i--)
            {
                Kill(node.GetChild(i).gameObject);
            }
        }
    }
}
