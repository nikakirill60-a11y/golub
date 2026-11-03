using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Code-first port of screens/tank_modules and HangarScreenModules
    /// (scripts/ui_elements/ui_hangar_screen_modulestres.gd).
    ///
    /// Godot lays the tiles out with an HBoxContainer that holds a 50 px arrow
    /// spacer between neighbours, so the resolved step is
    /// 140 + 4 + 50 + 4 = 198 px inside a row scaled by 0.52.
    ///
    /// Two deliberate differences from Godot, both documented in TASK_PLAN:
    /// - Godot passes the de-duplicated slot index to install_module while the
    ///   account indexes the raw module list. This port keeps the raw index as
    ///   the module id, which is what TankResearchService expects.
    /// - Audio.ui_install is not ported yet, so installing reuses the existing
    ///   select click.
    /// </summary>
    public sealed class ModulesScreen
    {
        private const float ScrollTop = 90f;
        private const float ContentWidth = 1200f;
        private const float AddedRowsHeight = 208f;
        private const float RowStep = 104f;
        private const int PortedRows = 3;
        private const float ArrowTrunkHeight = 430.34045f;
        private const float BaseTrunkHeight = ArrowTrunkHeight - AddedRowsHeight;
        private const float RowLeft = 302f;
        private const float RowScale = 0.52f;
        private const float TileWidth = 140f;
        private const float TileHeight = 120f;
        private const float SpacerWidth = 50f;
        private const float Separation = 4f;
        private const float TileStep = TileWidth + Separation + SpacerWidth + Separation;
        private const float PanelWidth = 425f;
        private const float PanelTop = 91f;
        private const float StatRowHeight = 36f;
        private const float StatRowStep = StatRowHeight + Separation;

        private static readonly float ScrollWidth = UiLayout.ReferenceWidth - 528.3334f;
        private static readonly float ScrollHeight =
            (UiLayout.ReferenceHeight * 1.167f) - 91.85004f - ScrollTop;
        private static readonly float PanelHeight = UiLayout.ReferenceHeight - PanelTop;
        // Godot has three rows because its cards carried neither turret nor
        // radio modules. The two client rows are appended below the ported
        // ones, which stay at their authored positions.
        private static readonly float[] RowTops = { 110f, 214f, 318f, 422f, 526f };

        private static readonly ModuleKind[] Kinds =
        {
            ModuleKind.Gun, ModuleKind.Engine, ModuleKind.Tracks, ModuleKind.Turret, ModuleKind.Radio,
        };

        /// <summary>Gradient_g642o, flattened: a hard edge then a flat fill.</summary>
        private static readonly Color PanelFill =
            new Color(0.06310915f, 0.10855677f, 0.13752648f, 0.658f);

        /// <summary>Tint of a gun tile the installed turret cannot carry.</summary>
        private static readonly Color IncompatibleTint =
            new Color(0.45f, 0.45f, 0.45f, 1f);

        private static readonly Color PanelEdge =
            new Color(0.09803922f, 0.105882354f, 0.09803922f, 1f);

        private sealed class Tile
        {
            public Image Selected;
            public int Id;
            public ResearchStatus Status;
            public ModuleInfo Module;
        }

        private readonly HangarUiKit _kit;
        private readonly TanksDatabase _database;
        private readonly AccountData _account;
        private readonly TankResearchService _research;

        private readonly RectTransform[] _rows = new RectTransform[Kinds.Length];

        private readonly RectTransform[] _arrows = new RectTransform[Kinds.Length];

        private readonly List<Tile>[] _tiles = NewTileRows();

        private static List<Tile>[] NewTileRows()
        {
            var rows = new List<Tile>[Kinds.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                rows[i] = new List<Tile>();
            }

            return rows;
        }

        private RectTransform _root;
        private RectTransform _content;
        private Image _trunk;
        private int _appendedRows;
        private RectTransform _statsContent;
        private Text _tankName;
        private Text _selectedName;
        private Text _notAvailable;
        private Text _buyCostText;
        private Text _buyCost;
        private Image _buyCurrency;
        private Button _actionButton;
        private Text _actionLabel;

        private TechTreeCard _tankCard;
        private TankSaveData _save;
        private TankInfo _info;

        private int _selectedKind = -1;
        private int _selectedId = -1;
        private ResearchStatus _selectedModStatus = ResearchStatus.NotAvailable;
        private string _selectedTankName = string.Empty;
        private ResearchStatus _selectedTankStatus = ResearchStatus.NotAvailable;

        public ModulesScreen(
            HangarUiKit kit,
            TanksDatabase database,
            AccountData account,
            TankResearchService research)
        {
            _kit = kit;
            _database = database;
            _account = account;
            _research = research;
        }

        /// <summary>btn_menu, back to the hangar.</summary>
        public Action HomePressed;

        /// <summary>btn_to_tree, back to the tech tree.</summary>
        public Action TreePressed;

        /// <summary>Raised after a successful research, buy, install or sell.</summary>
        public Action Changed;

        /// <summary>Localized refusal text, e.g. popup:NotEnoughExp.</summary>
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

        public int TileCount { get; private set; }

        public string CurrentTank
        {
            get { return _save == null ? string.Empty : _save.Name; }
        }

        // ------------------------------------------------------------------
        // Build.
        // ------------------------------------------------------------------

        public RectTransform Build(RectTransform parent)
        {
            _root = UiFactory.Node("tank_modules", parent);
            GodotRect.FullRect(_root);

            BuildBackdrop();
            BuildScroll();
            BuildStaticArrows();
            BuildInfoPanel();
            BuildTopButtons();
            return _root;
        }

        /// <summary>
        /// screens/tank_modules/blur: the tinted backdrop that separates the
        /// module shop from the live hangar behind it.
        /// </summary>
        private void BuildBackdrop()
        {
            var blur = _kit.Picture(
                "blur",
                _root,
                null,
                0f,
                0f,
                UiLayout.ReferenceWidth,
                UiLayout.ReferenceHeight);

            // blur.tscn: anchors_preset 15 with offset_top = 6, offset_bottom = 6.
            HangarUiKit.Stretch(blur.rectTransform, 0f, 6f, 0f, 6f);
            blur.color = HangarUiKit.ScreenTint;
            blur.raycastTarget = false;
        }

        private void BuildScroll()
        {
            var scrollRoot = _kit.Node(
                "ScrollContainer", _root, 0f, ScrollTop, ScrollWidth, ScrollHeight);

            // Godot: anchors 0, 0, 1, 1.167 with offsets 0, 90, -528.3334 and
            // -91.85004. The out-of-range anchor_bottom is what the scene has,
            // so it is kept verbatim rather than clamped.
            GodotRect.Apply(
                scrollRoot,
                0f,
                0f,
                1f,
                1.167f,
                0f,
                ScrollTop,
                -528.3334f,
                -91.85004f,
                GodotRect.TopLeftPivot);
            scrollRoot.localScale = new Vector3(1.2f, 1.2f, 1f);

            var viewport = UiFactory.Node("viewport", scrollRoot);
            GodotRect.FullRect(viewport);
            var surface = UiFactory.Picture("drag_surface", viewport, null);
            surface.color = new Color(0f, 0f, 0f, 0.001f);
            surface.raycastTarget = true;
            GodotRect.FullRect(surface.rectTransform);

            // Godot clips on the content, not on the scroll area: the
            // ScrollContainer sets clip_contents = false and "modules" sets
            // clip_contents = true. Masking the viewport instead is what cut the
            // tile rows off on a large screen, so the mask moves to the content
            // and tank_info keeps covering the overflow (it has z_index = 1 in
            // Godot and is built after this row here).
            _content = _kit.Node(
                "modules", viewport, 0f, 0f, ContentWidth, ScrollHeight + AddedRowsHeight);
            // The source has three rows. Turret and radio add two authored
            // 104-pixel row steps, so the content must extend below the viewport
            // and the ScrollRect must accept vertical input.
            HangarUiKit.LeftStretch(_content, 0f, 0f, ContentWidth, AddedRowsHeight);
            _content.gameObject.AddComponent<RectMask2D>();

            var scroll = scrollRoot.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = _content;
            scroll.horizontal = true;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = true;
            scroll.decelerationRate = 0.135f;
            scroll.scrollSensitivity = 24f;

            for (var i = 0; i < _rows.Length; i++)
            {
                _rows[i] = _kit.Node(
                    Kinds[i].ToString(), _content, RowLeft, RowTops[i], TileWidth, TileHeight);
                _rows[i].localScale = new Vector3(RowScale, RowScale, 1f);
            }
        }

        /// <summary>arrowbody2 plus arrow1..arrow3 from the scene.</summary>
        private void BuildStaticArrows()
        {
            var trunk = _kit.Picture(
                "arrowbody2", _content, null, 272f, 142f, 2.5368f, ArrowTrunkHeight);
            trunk.rectTransform.localScale = new Vector3(0.94f, 0.94f, 1f);
            _trunk = trunk;

            var bodies = new[]
            {
                new Vector4(-7.86f, 57.72f, 49.717002f, 5.319185f),
                new Vector4(-59.999996f, 57.72f, 105.185081f, 5.319185f),
                new Vector4(-7.86f, 57.72f, 49.717002f, 5.319185f),
            };

            for (var i = 0; i < RowTops.Length; i++)
            {
                // The scene ships three arrow bodies; appended client-module
                // rows reuse the last one so the column keeps its look.
                var shape = bodies[Mathf.Min(i, bodies.Length - 1)];
                var group = _kit.Node(
                    "arrow" + (i + 1), _content, 277f, RowTops[i] + 2f, SpacerWidth, TileHeight);
                group.localScale = new Vector3(0.5f, 0.5f, 1f);
                _arrows[i] = group;

                var body = _kit.Picture(
                    "arrowbody", group, null, shape.x, shape.y, shape.z, shape.w);
                body.rectTransform.localScale = new Vector3(0.94f, 0.94f, 1f);

                var head = _kit.Picture(
                    "arrowhead", group, "tree_arrowhead", 39.076904f, 53.538513f, 13f, 14f);
                head.rectTransform.localScale = new Vector3(0.94f, 0.94f, 1f);
            }
        }

        /// <summary>
        /// btn_menu and btn_to_tree, both z_index = 1 in the source, so both
        /// belong over the root-level header plate rather than under it.
        /// </summary>
        private void BuildTopButtons()
        {
            var top = Overlay ?? _root;
            var menu = _kit.StyleButton(
                "btn_menu", top, "button_small_generic", 5f, 4f, 94f, 77f);
            _kit.AddIcon(menu, "icon_back", 50f);
            _kit.Bind(menu, RaiseHome, AudioService.SoundEnter);

            var tree = _kit.StyleButton(
                "btn_to_tree", top, "button_small_generic", 113f, 4f, 94f, 77f);
            _kit.AddIcon(tree, "hangar_techtree", 50f);
            _kit.Bind(tree, RaiseTree, AudioService.SoundEnter);
        }

        private void BuildInfoPanel()
        {
            var panel = _kit.Node(
                "tank_info",
                _root,
                UiLayout.ReferenceWidth - PanelWidth,
                PanelTop,
                PanelWidth,
                PanelHeight);

            // Godot: anchors_preset 11 with offset_left = -425 and offset_top = 91,
            // so the panel keeps its 425 px width and grows down with the canvas
            // instead of ending 91 px short of the bottom on a tall screen.
            HangarUiKit.RightStretch(panel, -PanelWidth, PanelTop, 0f, 0f);

            var fill = _kit.Picture("bg", panel, null, 0f, 0f, PanelWidth, PanelHeight);
            fill.color = PanelFill;
            HangarUiKit.Stretch(fill.rectTransform, 0f, 0f, 0f, 0f);
            var edge = _kit.Picture("bg_edge", panel, null, 0f, 0f, 3.4f, PanelHeight);
            edge.color = PanelEdge;
            HangarUiKit.LeftStretch(edge.rectTransform, 0f, 0f, 3.4f, 0f);

            _tankName = _kit.Label(
                "tank_name",
                panel,
                26,
                TextAnchor.MiddleLeft,
                string.Empty,
                _kit.Bold,
                UiTheme.Label,
                10f,
                5f,
                294f,
                31f);

            BuildStatsScroll(panel);

            var shade = _kit.Picture(
                "header_shade", panel, "panel_header", 0f, PanelHeight - 166f, PanelWidth, 32f);
            shade.type = Image.Type.Sliced;

            // Godot: anchors_preset 7 with offsets -212.5, -166, 212.5, -134.
            // Everything from here down is pinned to the bottom of the panel, so
            // the buy/install block stays put as the panel grows.
            HangarUiKit.BottomCenter(shade.rectTransform, -212.5f, -166f, 212.5f, -134f);
            _selectedName = _kit.Label(
                "selected_name",
                shade.rectTransform,
                19,
                TextAnchor.MiddleLeft,
                string.Empty,
                _kit.Bold,
                UiTheme.Label,
                8f,
                6f,
                409f,
                20f);

            _actionButton = _kit.StyleButton(
                "moduleActionButton",
                panel,
                "button_generic",
                125f,
                PanelHeight - 71f,
                175f,
                70f);

            // Godot: anchors_preset 7 with offsets -87.5, -71, 87.5, -1.
            HangarUiKit.BottomCenter(
                (RectTransform)_actionButton.transform, -87.5f, -71f, 87.5f, -1f);
            _actionLabel = _kit.Label(
                "label",
                (RectTransform)_actionButton.transform,
                20,
                TextAnchor.MiddleCenter,
                _kit.Str("INSTALL"),
                _kit.Bold,
                UiTheme.ButtonFont,
                0f,
                0f,
                175f,
                70f);
            _kit.Bind(_actionButton, ButtonAction, AudioService.SoundEnter);

            _notAvailable = _kit.Label(
                "module_not_available",
                panel,
                21,
                TextAnchor.MiddleCenter,
                string.Empty,
                _kit.Regular,
                UiTheme.Label,
                66f,
                PanelHeight - 114f,
                294f,
                31f);

            // Godot: anchors_preset 7 with offsets -146.5, -114, 147.5, -83.
            HangarUiKit.BottomCenter(
                _notAvailable.rectTransform, -146.5f, -114f, 147.5f, -83f);

            _buyCostText = _kit.Label(
                "buy_cost_t",
                panel,
                21,
                TextAnchor.MiddleLeft,
                string.Empty,
                _kit.Regular,
                UiTheme.Label,
                26f,
                PanelHeight - 118f,
                294f,
                31f);

            // Godot: anchors_preset 7 with offsets -186.5, -118, 107.5, -87.
            HangarUiKit.BottomCenter(
                _buyCostText.rectTransform, -186.5f, -118f, 107.5f, -87f);

            _buyCost = _kit.Label(
                "buy_cost",
                panel,
                25,
                TextAnchor.MiddleRight,
                string.Empty,
                _kit.Bold,
                UiTheme.Label,
                251f,
                PanelHeight - 121f,
                133f,
                31f);

            // Godot: anchors_preset 7 with offsets 38.5, -121, 171.5, -90.
            HangarUiKit.BottomCenter(_buyCost.rectTransform, 38.5f, -121f, 171.5f, -90f);

            _buyCurrency = _kit.Picture(
                "buy_currency_icon",
                panel,
                "hangar_exp",
                387.5789f,
                PanelHeight - 124f,
                28.421f,
                27f);

            // Godot: anchors_preset 3 with offsets -37.421143, -124, -3.9846497
            // and -92.23532, then scale 0.85. The scale used to be baked into the
            // size; keeping it separate is what lets the icon follow the corner.
            HangarUiKit.BottomRight(
                _buyCurrency.rectTransform, -37.421143f, -124f, -3.9846497f, -92.23532f);
            _buyCurrency.rectTransform.localScale = new Vector3(0.85f, 0.85f, 1f);

            ChangeBuyPanel(-1);
        }

        private void BuildStatsScroll(RectTransform panel)
        {
            var scrollRoot = _kit.Node("ScrollContainer", panel, 1f, 47f, 423f, PanelHeight - 214f);

            // Godot: anchors_preset 15 with offsets 1, 47, -1, -167, so the stat
            // list absorbs the extra panel height instead of being clipped.
            HangarUiKit.Stretch(scrollRoot, 1f, 47f, -1f, -167f);

            var viewport = UiFactory.Node("viewport", scrollRoot);
            GodotRect.FullRect(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();
            var surface = UiFactory.Picture("drag_surface", viewport, null);
            surface.color = new Color(0f, 0f, 0f, 0.001f);
            surface.raycastTarget = true;
            GodotRect.FullRect(surface.rectTransform);

            _statsContent = _kit.Node("main_info", viewport, 0f, 0f, 415f, 0f);

            var scroll = scrollRoot.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = _statsContent;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;
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

            TankSaveData save = null;
            if (_account != null && _account.HasTank(tank, true))
            {
                save = _account.GetTank(tank);
            }

            if (save == null)
            {
                save = TankSaveData.WithName(tank);
            }

            Init(save);
        }

        public void Init(TankSaveData save)
        {
            if (save == null)
            {
                return;
            }

            var info = _database.Get(save.Name);
            if (info == null)
            {
                return;
            }

            _save = save;
            _info = info;
            TileCount = 0;

            _tankName.text = _kit.Lang.GetTankName(info);
            _selectedName.text = string.Empty;
            ChangeBuyPanel(-1);

            BuildTankCard(info);

            _appendedRows = 0;
            for (var i = 0; i < _rows.Length; i++)
            {
                PopulateRow(i, info);
            }

            ApplyRowLayout();

            TankSelect(info.Name, _research.TankStatus(info.Name));
            Debug.Log(
                "[Blitz] modules screen: tank=" + info.Name +
                " tiles=" + TileCount +
                " status=" + _selectedTankStatus);
        }

        private void BuildTankCard(TankInfo info)
        {
            if (_tankCard != null && _tankCard.Root != null)
            {
                var old = _tankCard.Root;
                old.SetParent(null, false);
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(old.gameObject);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(old.gameObject);
                }
            }

            _tankCard = TechTreeCard.Build(
                _kit,
                _content,
                72f,
                211f,
                347f,
                136f,
                0.5f,
                info,
                _research.TankStatus(info.Name),
                _account == null ? null : _account.GetTank(info.Name),
                OnTankCardPressed);
        }

        private void PopulateRow(int rowIndex, TankInfo info)
        {
            var kind = Kinds[rowIndex];
            Clear(_rows[rowIndex]);
            _tiles[rowIndex].Clear();

            var modules = TankResearchService.Modules(info, kind);
            var installed = _save == null ? 0 : _research.InstalledModuleId(_save, kind);
            var bought = _account != null && _account.HasTank(info.Name);

            var seen = new List<string>();
            var ids = new List<int>();
            for (var id = 0; id < modules.Count; id++)
            {
                var module = modules[id];
                if (module == null || seen.Contains(module.Name))
                {
                    continue;
                }

                seen.Add(module.Name);
                ids.Add(id);
            }

            // The source screen has three rows. A row this port appends only
            // earns its space when it has something to choose: every card in
            // the client data ships a single radio whose unlocks are empty,
            // and a casemate tank destroyer carries a single turret, so those
            // rows would sit under the ported ones as dead tiles. The eight
            // turreted tank destroyers of the client, M10 Wolverine and
            // M18 Hellcat among them, do research a second turret and keep it.
            var appended = rowIndex >= PortedRows;
            var visible = !appended || ids.Count > 1;
            _rows[rowIndex].gameObject.SetActive(visible);
            if (_arrows[rowIndex] != null)
            {
                _arrows[rowIndex].gameObject.SetActive(visible);
            }

            if (!visible)
            {
                return;
            }

            var top = appended ? RowTops[PortedRows + _appendedRows] : RowTops[rowIndex];
            if (appended)
            {
                _appendedRows++;
            }

            for (var slot = 0; slot < ids.Count; slot++)
            {
                var id = ids[slot];
                BuildTile(
                    rowIndex, kind, info, modules[id], id, slot,
                    bought && id == installed);
            }

            var width = ids.Count <= 1
                ? TileWidth
                : TileWidth + ((ids.Count - 1) * TileStep);
            HangarUiKit.Place(_rows[rowIndex], RowLeft, top, width, TileHeight);
            if (_arrows[rowIndex] != null)
            {
                HangarUiKit.Place(
                    _arrows[rowIndex], 277f, top + 2f, SpacerWidth, TileHeight);
            }
        }

        /// <summary>
        /// The scroll content and the arrow trunk grow with the appended rows
        /// that survived, so a card without them keeps the authored height.
        /// </summary>
        private void ApplyRowLayout()
        {
            var added = _appendedRows * RowStep;
            HangarUiKit.LeftStretch(_content, 0f, 0f, ContentWidth, added);
            if (_trunk != null)
            {
                HangarUiKit.Place(
                    _trunk.rectTransform, 272f, 142f, 2.5368f, BaseTrunkHeight + added);
            }
        }

        // ------------------------------------------------------------------
        // ui_tank_module.tscn.
        // ------------------------------------------------------------------

        private void BuildTile(
            int rowIndex,
            ModuleKind kind,
            TankInfo info,
            ModuleInfo module,
            int id,
            int slot,
            bool installed)
        {
            var status = _research.ModuleStatus(info.Name, kind, id);
            var tile = _kit.Picture(
                "module_" + module.Name,
                _rows[rowIndex],
                HangarUiKit.ModuleBackgroundRegion(status),
                slot * TileStep,
                0f,
                TileWidth,
                TileHeight);
            var root = tile.rectTransform;

            var icon = _kit.Picture(
                "icon",
                root,
                HangarUiKit.ModuleKindRegion(kind),
                15.438f,
                10.987f,
                109.124f,
                98.026f);

            // A gun the installed turret does not carry cannot be mounted, so
            // the tile is dimmed the way the client greys such a gun out.
            var mounted = _save == null ? null : info.Turret(_save.TurretId);
            if (kind == ModuleKind.Gun && mounted != null && !mounted.CanMount(module.Name))
            {
                icon.color = IncompatibleTint;
                tile.color = IncompatibleTint;
            }

            var level = _kit.Centered(
                "lvl", root, HangarUiKit.LevelRegion(module.Level), -9f, -2f, 76f, 57f);
            level.rectTransform.localScale = new Vector3(0.94f, 0.94f, 1f);

            var cost = _kit.Label(
                "cost",
                root,
                34,
                TextAnchor.MiddleRight,
                HangarUiKit.FormatNumberToK(module.CostXp),
                _kit.Regular,
                UiTheme.Label,
                41f,
                87.316f,
                64f,
                36f);
            cost.rectTransform.localScale = new Vector3(0.9f, 0.9f, 1f);

            var currency = _kit.Centered(
                "currency_icon", root, "hangar_exp", 92f, 68.3158f, 52f, 58f);
            currency.rectTransform.localScale = new Vector3(0.9f, 0.9f, 1f);

            var showCost = module.CostXp > 0
                && (status == ResearchStatus.NotAvailable || status == ResearchStatus.Available);
            cost.gameObject.SetActive(showCost);
            currency.gameObject.SetActive(showCost);

            var marker = _kit.Picture(
                "installed", root, "module_installed", 92f, 69.3158f, 58f, 58f);
            marker.gameObject.SetActive(installed);

            var gun = module as GunInfo;
            var autoloader = _kit.Picture(
                "autoloader", root, "module_autoloader", 91f, -10f, 76f, 57f);
            autoloader.rectTransform.localScale = new Vector3(0.94f, 0.94f, 1f);
            autoloader.gameObject.SetActive(
                gun != null && gun.Burst != null && gun.Burst.HasBursts);

            var selected = _kit.Picture(
                "selected", root, "tree_selector", -3f, -2.6841965f, 146f, 126f);
            selected.type = Image.Type.Sliced;
            selected.gameObject.SetActive(false);

            var button = _kit.StyleButton(
                "ButtonCustom", root, null, 0f, 0f, TileWidth, TileHeight);
            var row = rowIndex;
            var moduleId = id;
            var captured = module;
            var capturedStatus = status;
            _kit.Bind(
                button,
                () => ChooseModule(row, moduleId, captured, capturedStatus),
                AudioService.SoundSelect);

            _tiles[rowIndex].Add(
                new Tile
                {
                    Selected = selected,
                    Id = id,
                    Status = status,
                    Module = module,
                });
            TileCount++;
        }

        // ------------------------------------------------------------------
        // Selection and the six action-panel states.
        // ------------------------------------------------------------------

        private void ChooseModule(
            int rowIndex, int id, ModuleInfo module, ResearchStatus status)
        {
            _selectedKind = rowIndex;
            _selectedId = id;
            _selectedModStatus = status;

            for (var r = 0; r < _tiles.Length; r++)
            {
                for (var i = 0; i < _tiles[r].Count; i++)
                {
                    _tiles[r][i].Selected.gameObject.SetActive(
                        r == rowIndex && _tiles[r][i].Id == id);
                }
            }

            if (_tankCard != null)
            {
                _tankCard.Deselect();
            }

            var kind = Kinds[rowIndex];
            var kindLabel = KindLabel(kind);
            var display = string.IsNullOrEmpty(module.DisplayName)
                ? module.Name
                : module.DisplayName;
            _selectedName.text = kindLabel + " " + display;

            if (_info != null && _save != null)
            {
                var gunId = _save.GunId;
                var engineId = _save.EngineId;
                var trackId = _save.TrackId;
                var turretId = _save.TurretId;
                var radioId = _save.RadioId;
                switch (kind)
                {
                    case ModuleKind.Gun:
                        gunId = id;
                        break;
                    case ModuleKind.Engine:
                        engineId = id;
                        break;
                    case ModuleKind.Tracks:
                        trackId = id;
                        break;
                    case ModuleKind.Turret:
                        turretId = id;
                        break;
                    case ModuleKind.Radio:
                        radioId = id;
                        break;
                }

                SetupStats(_info, gunId, engineId, trackId, turretId, radioId);
            }

            switch (status)
            {
                case ResearchStatus.NotAvailable:
                    ChangeBuyPanel(0);
                    break;
                case ResearchStatus.Available:
                    // ui_hangar_screen_modulestres.gd prints the module
                    // research price with Util.format_number, not the short
                    // form the grid tiles use.
                    _buyCost.text = HangarUiKit.FormatNumber(module.CostXp);
                    ChangeBuyPanel(1);
                    break;
                case ResearchStatus.Researched:
                    ChangeBuyPanel(3);
                    break;
                case ResearchStatus.InstalledBought:
                    ChangeBuyPanel(5);
                    break;
            }
        }

        private void OnTankCardPressed(string tank, ResearchStatus status)
        {
            TankSelect(tank, status);
        }

        private void TankSelect(string tank, ResearchStatus status)
        {
            _selectedKind = -1;
            _selectedId = -1;

            var info = _database.Get(tank);
            if (info == null)
            {
                return;
            }

            var gunId = 0;
            var engineId = 0;
            var trackId = 0;
            var turretId = 0;
            var radioId = 0;
            if (_account != null && _account.HasTank(tank))
            {
                var save = _account.GetTank(tank);
                if (save != null)
                {
                    gunId = save.GunId;
                    engineId = save.EngineId;
                    trackId = save.TrackId;
                    turretId = save.TurretId;
                    radioId = save.RadioId;
                }
            }

            SetupStats(info, gunId, engineId, trackId, turretId, radioId);
            _selectedName.text = string.Empty;

            for (var r = 0; r < _tiles.Length; r++)
            {
                for (var i = 0; i < _tiles[r].Count; i++)
                {
                    _tiles[r][i].Selected.gameObject.SetActive(false);
                }
            }

            if (_tankCard != null)
            {
                _tankCard.Select();
            }

            _selectedTankName = info.Name;
            _selectedTankStatus = status;

            var display = _kit.Lang.GetTankName(info);
            _tankName.text = _kit.Str("Tank") + " " + display;

            switch (status)
            {
                case ResearchStatus.NotAvailable:
                    ChangeBuyPanel(0, info.Premium);
                    break;
                case ResearchStatus.Available:
                    _buyCost.text = HangarUiKit.FormatNumberToK(info.CostXp);
                    ChangeBuyPanel(1, info.Premium);
                    break;
                case ResearchStatus.Researched:
                    _buyCost.text = HangarUiKit.FormatNumberToK(info.Cost);
                    ChangeBuyPanel(2, info.Premium);
                    break;
                case ResearchStatus.InstalledBought:
                    ChangeBuyPanel(4, info.Premium);
                    break;
            }
        }

        private void ChangeBuyPanel(int variant, bool premium = false)
        {
            var showAction = false;
            var showHint = false;
            var showCost = false;
            var hint = string.Empty;
            var action = string.Empty;
            var style = "button_generic";
            var currency = "hangar_exp";
            var costText = string.Empty;

            switch (variant)
            {
                case 0:
                    showHint = true;
                    hint = _kit.Str("hangar:research_prev_mods");
                    break;
                case 1:
                    showAction = true;
                    showCost = true;
                    action = _kit.Str("RESEARCH");
                    costText = _kit.Str("hangar:reserch_cost");
                    break;
                case 2:
                    showAction = true;
                    showCost = true;
                    style = "button_green";
                    action = _kit.Str("BUY");
                    currency = premium ? "hangar_gold" : "hangar_credits";
                    costText = _kit.Str("hangar:buy_cost");
                    break;
                case 3:
                    showAction = true;
                    showHint = true;
                    style = "button_green";
                    action = _kit.Str("INSTALL");
                    hint = _kit.Str("hangar:mod_alr_researched");
                    break;
                case 4:
                    showAction = true;
                    showHint = true;
                    style = "hangar_red_button";
                    action = _kit.Str("SELL");
                    hint = _kit.Str("hangar:tank_alr_bought");
                    break;
                case 5:
                    showHint = true;
                    hint = _kit.Str("hangar:mod_alr_installed");
                    break;
            }

            _actionButton.gameObject.SetActive(showAction);
            if (showAction)
            {
                _actionLabel.text = action;
                if (_actionButton.image != null)
                {
                    var sprite = _kit.Art(style);
                    _actionButton.image.sprite = sprite;
                    _actionButton.image.type =
                        sprite != null && sprite.border.sqrMagnitude > 0f
                            ? Image.Type.Sliced
                            : Image.Type.Simple;
                }
            }

            _notAvailable.gameObject.SetActive(showHint);
            _notAvailable.text = hint;

            _buyCost.gameObject.SetActive(showCost);
            _buyCostText.gameObject.SetActive(showCost);
            _buyCurrency.gameObject.SetActive(showCost);
            if (showCost)
            {
                _buyCostText.text = costText;
                _buyCurrency.sprite = _kit.Art(currency);
            }
        }

        // ------------------------------------------------------------------
        // Actions.
        // ------------------------------------------------------------------

        private void ButtonAction()
        {
            if (_selectedKind >= 0 || _selectedId >= 0)
            {
                ModuleAction();
            }
            else
            {
                TankAction();
            }
        }

        private void ModuleAction()
        {
            if (_save == null || _selectedKind < 0 || _selectedId < 0)
            {
                return;
            }

            var kind = Kinds[_selectedKind];
            if (_selectedModStatus == ResearchStatus.Available)
            {
                Apply(_research.ResearchModule(_save.Name, kind, _selectedId));
            }
            else if (_selectedModStatus == ResearchStatus.Researched)
            {
                Apply(_research.InstallModule(_save.Name, kind, _selectedId));
            }
        }

        private void TankAction()
        {
            if (string.IsNullOrEmpty(_selectedTankName))
            {
                return;
            }

            switch (_selectedTankStatus)
            {
                case ResearchStatus.Available:
                    Apply(_research.ResearchTank(_selectedTankName));
                    break;
                case ResearchStatus.Researched:
                    Apply(_research.BuyTank(_selectedTankName));
                    break;
                case ResearchStatus.InstalledBought:
                    Apply(_research.SellTank(_selectedTankName));
                    break;
            }
        }

        private void Apply(TankActionResult result)
        {
            var tank = _save == null ? _selectedTankName : _save.Name;
            if (!result.Ok)
            {
                Debug.Log("[Blitz] modules action refused: " + result.ErrorKey);
                if (Message != null)
                {
                    Message(_kit.Str(result.ErrorKey));
                }

                return;
            }

            if (Changed != null)
            {
                Changed();
            }

            Init(tank);
        }

        private void RaiseHome()
        {
            if (HomePressed != null)
            {
                HomePressed();
            }
        }

        private void RaiseTree()
        {
            if (TreePressed != null)
            {
                TreePressed();
            }
        }

        // ------------------------------------------------------------------
        // tank_info_panel.setup_from_tank: the stat list. Godot builds it from
        // its own template rows; the labels here are the ported subset that
        // TankInfo and ModuleInfo actually carry.
        // ------------------------------------------------------------------

        private void SetupStats(
            TankInfo info, int gunId, int engineId, int trackId, int turretId = 0,
            int radioId = 0)
        {
            if (_statsContent == null || info == null)
            {
                return;
            }

            Clear(_statsContent);
            var row = 0;

            // The client authors the traverse speed and the view range on the
            // turret, so a rebuilt card reads both off the selected turret.
            var turret = Module(info, ModuleKind.Turret, turretId) as TurretInfo;
            // ui_hangar.tscn prints rad_to_deg(info.turret_rot_speed): the
            // cards keep the traverse in radians per second, the stat sheet
            // shows degrees.
            var turretRotation = Mathf.Rad2Deg * (turret != null
                ? turret.RotationSpeed : info.TurretRotationSpeed);
            var viewRange = turret != null ? turret.SpotRadius : info.SpotRadius;

            AddStatHeader("BASE PARAMETERS", ref row);
            AddStat("Health", HangarUiKit.FormatNumber(info.Health), ref row);
            AddStat("Hull weight", Number(info.HullWeight), ref row);
            AddStat("Speed forward", Number(info.SpeedForward), ref row);
            AddStat("Speed backward", Number(info.SpeedBackward), ref row);
            AddStat("Turret rotation", Number(turretRotation), ref row);
            AddStat("View range", Number(viewRange), ref row);
            AddStat("Repair cost", Number(info.RepairCost), ref row);

            if (turret != null)
            {
                AddStatHeader(KindLabel(ModuleKind.Turret), ref row);
                AddStat("Name", Display(turret), ref row);
                AddStat("Rotation speed", Number(turret.RotationSpeed), ref row);
                AddStat("View range", Number(turret.SpotRadius), ref row);
                AddStat("Durability", Number(turret.Health), ref row);
                AddStat("Weight", Number(turret.Weight), ref row);
            }

            // The same barrel reloads slower and holds fewer rounds in the
            // stock turret, so the selected gun is shown as the selected
            // turret would mount it; a gun that turret cannot carry keeps its
            // own card values and the tile is greyed out instead.
            var gun = Module(info, ModuleKind.Gun, gunId) as GunInfo;
            var slot = turret == null || gun == null ? null : turret.Slot(gun.Name);
            if (slot != null)
            {
                gun = gun.WithTurretSlot(slot);
            }

            if (gun != null)
            {
                AddStatHeader(_kit.Str("Gun"), ref row);
                AddStat("Name", Display(gun), ref row);
                AddStat("Reload", Number(gun.Reload), ref row);
                if (gun.AutoReload)
                {
                    var timers = new List<string>();
                    foreach (var seconds in gun.AutoReloadTimes) { timers.Add(Number(seconds)); }
                    AddStat("hangar:autoreload_times", string.Join(" / ", timers), ref row);
                }
                AddStat("Aim time", Number(gun.AimTime), ref row);
                AddStat("Dispersion", Number(gun.Dispersion), ref row);
                AddStat("Caliber", HangarUiKit.FormatNumber(gun.Caliber), ref row);
                AddStat("Ammo", HangarUiKit.FormatNumber(gun.MaxAmmo), ref row);
            }

            var engine = Module(info, ModuleKind.Engine, engineId) as EngineInfo;
            if (engine != null)
            {
                AddStatHeader(_kit.Str("Engine"), ref row);
                AddStat("Name", Display(engine), ref row);
                AddStat("Power", Number(engine.Power), ref row);
                AddStat("Fire chance", Number(engine.FireStartingChance), ref row);
            }

            var tracks = Module(info, ModuleKind.Tracks, trackId) as TracksInfo;
            if (tracks != null)
            {
                AddStatHeader(_kit.Str("Chassis"), ref row);
                AddStat("Name", Display(tracks), ref row);
                AddStat("Rotation speed", Number(tracks.RotationSpeed), ref row);
                AddStat("Weight", Number(tracks.Weight), ref row);
            }

            var radio = Module(info, ModuleKind.Radio, radioId) as RadioInfo;
            if (radio != null)
            {
                AddStatHeader(_kit.Str("Radio"), ref row);
                AddStat("Name", Display(radio), ref row);
                AddStat(_kit.Str("Signal range"), Number(radio.Distance), ref row);
                AddStat("Weight", Number(radio.Weight), ref row);
            }

            HangarUiKit.Place(_statsContent, 0f, 0f, 415f, row * StatRowStep);
        }

        /// <summary>
        /// Row caption. "Turret" has no Godot string because the source has no
        /// turret research, so the key falls back to the raw word, exactly like
        /// the missing consumable keys do.
        /// </summary>
        private string KindLabel(ModuleKind kind)
        {
            switch (kind)
            {
                case ModuleKind.Gun:
                    return _kit.Str("Gun");
                case ModuleKind.Engine:
                    return _kit.Str("Engine");
                case ModuleKind.Tracks:
                    return _kit.Str("Chassis");
                case ModuleKind.Turret:
                    return _kit.Str("Turret");
                case ModuleKind.Radio:
                    return _kit.Str("Radio");
                default:
                    return string.Empty;
            }
        }

        private static ModuleInfo Module(TankInfo info, ModuleKind kind, int id)
        {
            var modules = TankResearchService.Modules(info, kind);
            if (modules == null || modules.Count <= 0)
            {
                return null;
            }

            return modules[Mathf.Clamp(id, 0, modules.Count - 1)];
        }

        private static string Display(ModuleInfo module)
        {
            return string.IsNullOrEmpty(module.DisplayName) ? module.Name : module.DisplayName;
        }

        private static string Number(float value)
        {
            return Mathf.Approximately(value, Mathf.Round(value))
                ? HangarUiKit.FormatNumber(Mathf.RoundToInt(value))
                : value.ToString("0.0");
        }

        private void AddStatHeader(string title, ref int row)
        {
            var header = _kit.Picture(
                "header", _statsContent, "panel_header", 0f, row * StatRowStep, 415f, StatRowHeight);
            header.type = Image.Type.Sliced;
            _kit.Label(
                "name",
                header.rectTransform,
                22,
                TextAnchor.MiddleLeft,
                title,
                _kit.Bold,
                UiTheme.Label,
                8f,
                7f,
                300f,
                23f);
            row++;
        }

        private void AddStat(string title, string value, ref int row)
        {
            var entry = _kit.Node(
                "entry", _statsContent, 0f, row * StatRowStep, 415f, StatRowHeight);
            _kit.Label(
                "name",
                entry,
                22,
                TextAnchor.MiddleLeft,
                title,
                _kit.Regular,
                HangarUiKit.Caption,
                5f,
                5f,
                250f,
                26f);
            _kit.Label(
                "value",
                entry,
                22,
                TextAnchor.MiddleRight,
                value,
                _kit.Bold,
                UiTheme.Label,
                299f,
                5f,
                100f,
                26f);
            _kit.Picture("divider", entry, "hangar_divider", 5f, 34f, 405f, 2f);
            row++;
        }

        private static void Clear(RectTransform parent)
        {
            if (parent == null)
            {
                return;
            }

            while (parent.childCount > 0)
            {
                var child = parent.GetChild(0);
                child.SetParent(null, false);
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                }
            }
        }
    }
}
