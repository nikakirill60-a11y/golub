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
    /// Code-first port of ui_hangar.tscn's screens/tech_tree node and its
    /// GDScript (ui_hangar_tech_tree), plus the ui_tech_tree_tank.tscn card.
    ///
    /// The Godot screen is built from BoxContainers, so every rect below is the
    /// resolved layout: a 50 px leading spacer, ten 260x110 level columns with
    /// the theme's 4 px BoxContainer separation, and eleven branch rows per
    /// column (-5 .. +5) of which only the used branch range is shown.
    /// </summary>
    public sealed class TechTreeScreen
    {
        public const int LevelCount = 10;

        /// <summary>Godot max_slots_vertical: branches -5 .. +5.</summary>
        public const int BranchMin = -5;
        public const int BranchMax = 5;

        /// <summary>Godot tank_scale.</summary>
        private const float SlotWidth = 260f;
        private const float SlotHeight = 110f;

        /// <summary>Godot's default BoxContainer separation.</summary>
        private const float Separation = 4f;

        /// <summary>The 50 px spacer pushed in front of every row.</summary>
        private const float LeadSpacer = 50f;

        private const float CardScale = 0.65f;
        private const float CardOffsetX = 15f;
        private const float CardOffsetY = 10f;
        private const float CardWidth = 400f;
        private const float CardHeight = 170f;

        private const float RulerTop = 90f;
        private const float RulerHeight = 38.64f;

        /// <summary>lvls_scroll/ColorRect custom_minimum_size.x.</summary>
        private const float RulerContentWidth = 3000f;

        private const float TreeTop = 129f;
        private const float BottomBarHeight = 116f;

        private const float PremiumSpacer = 10f;

        private const float ArrowThickness = 3f;

        /// <summary>
        /// One branch row: a slot plus the separation. Godot's 95 px step came
        /// from a smaller row pitch, so here it left every bent arrow pointing
        /// past its target card.
        /// </summary>
        private const float ArrowBranchStep = SlotHeight + Separation;

        private static readonly string[] LevelNumerals =
        {
            "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X",
        };

        private static readonly Nation[] NationOrder =
        {
            Nation.Ussr, Nation.Germany, Nation.Usa, Nation.Gb,
            Nation.Japan, Nation.France, Nation.China, Nation.European,
            Nation.Other,
        };

        private readonly HangarUiKit _kit;
        private readonly TanksDatabase _database;
        private readonly AccountData _account;
        private readonly TankResearchService _research;
        private readonly List<Image> _nationFrames = new List<Image>();

        private RectTransform _root;
        private RectTransform _treeContent;
        private RectTransform _rulerContent;
        private RectTransform _tanksMain;
        private RectTransform _premiumRow;
        private ScrollRect _treeScroll;

        public TechTreeScreen(
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

        /// <summary>Raised with the tank name when a tree card is pressed.</summary>
        public Action<string> TankPressed;

        /// <summary>Raised when the back button is pressed.</summary>
        public Action HomePressed;

        public RectTransform Root
        {
            get { return _root; }
        }

        public Nation CurrentNation { get; private set; }

        public int CardCount { get; private set; }

        public int ArrowCount { get; private set; }

        public int SlotCount { get; private set; }

        // ------------------------------------------------------------------
        // Build.
        // ------------------------------------------------------------------

        /// <summary>
        /// Where the source's z_index = 1 children are drawn. Godot lifts them
        /// over the root-level header plate; uGUI has no z_index, so the owner
        /// supplies a layer that sits after the HUD in the tree. Null keeps
        /// everything inside the screen, which is what isolated callers want.
        /// </summary>
        public RectTransform Overlay { get; set; }

        public RectTransform Build(RectTransform parent)
        {
            _root = UiFactory.Node("tech_tree", parent);
            GodotRect.FullRect(_root);

            BuildBackdrop();
            BuildRuler();
            BuildTree();
            BuildBottomBar();
            BuildBackButton();

            Update(Nation.Ussr);
            return _root;
        }

        /// <summary>
        /// screens/tech_tree/blur, which the Godot _ready() shows: the tinted
        /// backdrop that separates the tree from the live hangar behind it.
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

        private void BuildRuler()
        {
            var viewport = _kit.Node(
                "lvls_scroll", _root, 0f, RulerTop, UiLayout.ReferenceWidth, RulerHeight);

            // Godot: anchors_preset 15 with offset_top = 90, offset_bottom = -116,
            // so the ruler spans the canvas however wide the screen is.
            HangarUiKit.Stretch(viewport, 0f, RulerTop, 0f, -BottomBarHeight);
            viewport.gameObject.AddComponent<RectMask2D>();

            _rulerContent = _kit.Node(
                "ColorRect", viewport, 0f, 0f, RulerContentWidth, RulerHeight);
            var background = _kit.Picture(
                "bg", _rulerContent, null, 0f, 0f, RulerContentWidth, RulerHeight);
            background.color = Color.black;

            for (var i = 0; i < LevelCount; i++)
            {
                _kit.Label(
                    "lvl_" + LevelNumerals[i],
                    _rulerContent,
                    18,
                    TextAnchor.MiddleCenter,
                    LevelNumerals[i],
                    _kit.Regular,
                    UiTheme.Label,
                    ColumnX(i + 1),
                    0f,
                    SlotWidth,
                    40f);
            }
        }

        private void BuildTree()
        {
            var height = UiLayout.ReferenceHeight - BottomBarHeight - TreeTop;
            var scrollRoot = _kit.Node(
                "tree_scroll", _root, 0f, TreeTop, UiLayout.ReferenceWidth, height);

            // Godot: anchors_preset 15 with offset_top = 129, offset_bottom = -116.
            // Anchoring the viewport instead of freezing it at the reference size
            // is what stops the mask below from cutting the branch off on all
            // four sides once the canvas is bigger than 1386x733.
            HangarUiKit.Stretch(scrollRoot, 0f, TreeTop, 0f, -BottomBarHeight);

            var viewport = UiFactory.Node("viewport", scrollRoot);
            GodotRect.FullRect(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();
            var surface = UiFactory.Picture("drag_surface", viewport, null);
            surface.color = new Color(0f, 0f, 0f, 0.001f);
            surface.raycastTarget = true;
            GodotRect.FullRect(surface.rectTransform);

            _treeContent = _kit.Node(
                "tree_box_main", viewport, 0f, 0f, ContentWidth(), SlotHeight);

            // Godot's PanelContainer asks for 2600 px, but it shares the tree's
            // VBoxContainer, so it actually ends up as wide as the tree rows.
            var premiumPanel = _kit.Picture(
                "prems_panel", _treeContent, null, 0f, 0f, ContentWidth(), SlotHeight);
            premiumPanel.color = HangarUiKit.PremiumPanel;
            _premiumRow = _kit.Node(
                "tanks_prem", _treeContent, 0f, 0f, ContentWidth(), SlotHeight);

            _tanksMain = _kit.Node(
                "tanks_main", _treeContent, 0f, TanksTop(), ContentWidth(), SlotHeight);

            _treeScroll = scrollRoot.gameObject.AddComponent<ScrollRect>();
            _treeScroll.viewport = viewport;
            _treeScroll.content = _treeContent;
            _treeScroll.horizontal = true;
            _treeScroll.vertical = true;
            _treeScroll.movementType = ScrollRect.MovementType.Clamped;
            _treeScroll.inertia = true;
            _treeScroll.decelerationRate = 0.135f;
            _treeScroll.scrollSensitivity = 24f;
            _treeScroll.onValueChanged.AddListener(OnTreeScrolled);
        }

        /// <summary>blackpanel and the nation row.</summary>
        private void BuildBottomBar()
        {
            var top = UiLayout.ReferenceHeight - BottomBarHeight;
            var panel = _kit.Picture(
                "blackpanel", _root, null, 0f, top, UiLayout.ReferenceWidth, BottomBarHeight);
            panel.color = Color.black;

            // Godot: anchors_preset 12 with offset_top = -116, so the bar hugs
            // the bottom of the canvas at any screen size.
            HangarUiKit.BottomStretch(panel.rectTransform, 0f, -BottomBarHeight, 0f, 0f);

            // screens/tech_tree/border, the shared NinePatchRect that framed the
            // whole nation row at modulate alpha 0.3, is deliberately not ported:
            // only the per-nation "selected" frame built below marks a nation now.

            var nations = _kit.Node("nations", _root, 387.3333f, 633.3333f, 916f, 116f);

            // Godot: anchors_preset 7 with offsets -306, -100, 610, 16.
            HangarUiKit.BottomCenter(nations, -306f, -100f, 610f, 16f);
            nations.localScale = new Vector3(0.67f, 0.67f, 1f);

            const float buttonWidth = 168f;
            const float buttonHeight = 116f;
            const float margin = 2f;
            var step = buttonWidth + (margin * 2f) + Separation;
            var total = (step * NationOrder.Length) - Separation;
            var left = (916f - total) * 0.5f;

            _nationFrames.Clear();
            for (var i = 0; i < NationOrder.Length; i++)
            {
                var nation = NationOrder[i];
                var x = left + (step * i) + margin;
                var button = _kit.StyleButton(
                    "nation_" + nation, nations, null, x, 0f, buttonWidth, buttonHeight);
                var icon = _kit.Picture(
                    "nation_icon",
                    (RectTransform)button.transform,
                    HangarUiKit.NationRegion(nation),
                    0f,
                    0f,
                    buttonWidth,
                    buttonHeight);
                icon.preserveAspect = true;
                var frame = _kit.Picture(
                    "selected",
                    (RectTransform)button.transform,
                    "hangar_select_tile",
                    0f,
                    0f,
                    buttonWidth,
                    buttonHeight);
                frame.type = Image.Type.Sliced;
                _nationFrames.Add(frame);

                var selected = nation;
                _kit.Bind(button, () => Update(selected), AudioService.SoundSelect);
            }
        }

        private void BuildBackButton()
        {
            var button = _kit.StyleButton(
                "btn_menu", Overlay ?? _root, "button_small_generic", 5f, 4f, 94f, 77f);
            _kit.AddIcon(button, "icon_back", 50f);
            _kit.Bind(
                button,
                () =>
                {
                    if (HomePressed != null)
                    {
                        HomePressed();
                    }
                },
                AudioService.SoundEnter);
        }

        // ------------------------------------------------------------------
        // Population, a verbatim port of ui_hangar_tech_tree.update().
        // ------------------------------------------------------------------

        public void Update(Nation nation)
        {
            CurrentNation = nation;
            CardCount = 0;
            ArrowCount = 0;
            SlotCount = 0;

            for (var i = 0; i < _nationFrames.Count; i++)
            {
                _nationFrames[i].gameObject.SetActive(NationOrder[i] == nation);
            }

            Clear(_tanksMain);
            Clear(_premiumRow);

            var tanks = _database.TreeTanks(nation);
            var branchMin = 0;
            var branchMax = 0;
            for (var i = 0; i < tanks.Count; i++)
            {
                // Premium cards sit in their own row above the tree; their
                // branch only orders them and must not add empty tree rows.
                if (tanks[i].Premium) { continue; }
                var branch = tanks[i].TreeBranch;
                if (branch < branchMin)
                {
                    branchMin = branch;
                }

                if (branch > branchMax)
                {
                    branchMax = branch;
                }
            }

            var rows = (branchMax - branchMin) + 1;
            var slots = new Dictionary<Vector2, RectTransform>();
            for (var level = 1; level <= LevelCount; level++)
            {
                for (var branch = branchMin; branch <= branchMax; branch++)
                {
                    var slot = _kit.Node(
                        "slot_" + level + "_" + branch,
                        _tanksMain,
                        ColumnX(level),
                        (branch - branchMin) * (SlotHeight + Separation),
                        SlotWidth,
                        SlotHeight);
                    slots[new Vector2(level, branch)] = slot;
                    SlotCount++;
                }
            }

            for (var i = 0; i < tanks.Count; i++)
            {
                AddTank(tanks[i], slots);
            }

            var tanksHeight = (rows * SlotHeight) + ((rows - 1) * Separation);
            HangarUiKit.Place(_tanksMain, 0f, TanksTop(), ContentWidth(), tanksHeight);
            HangarUiKit.Place(
                _treeContent, 0f, 0f, ContentWidth(), TanksTop() + tanksHeight);

            ScrollToLevel(1);
            Debug.Log(
                "[Blitz] tech tree built: nation=" + nation +
                " cards=" + CardCount +
                " arrows=" + ArrowCount +
                " slots=" + SlotCount);
        }

        private void AddTank(TankInfo info, Dictionary<Vector2, RectTransform> slots)
        {
            if (info == null || string.IsNullOrEmpty(info.Name))
            {
                return;
            }

            if (info.Premium)
            {
                var column = _kit.Node(
                    "prem_" + info.Name,
                    _premiumRow,
                    ColumnX(info.Level),
                    0f,
                    SlotWidth,
                    SlotHeight);
                BuildCard(column, info);
                return;
            }

            RectTransform slot;
            if (!slots.TryGetValue(new Vector2(info.Level, info.TreeBranch), out slot))
            {
                return;
            }

            BuildCard(slot, info);

            foreach (var unlock in info.UnlockTanks.Keys)
            {
                var key = unlock == null ? null : unlock.ToLowerInvariant();
                var unlocked = _database.Get(key);
                if (unlocked == null)
                {
                    continue;
                }

                AddArrow(
                    slot,
                    unlocked.Level - info.Level,
                    info.TreeBranch - unlocked.TreeBranch,
                    _research.TankStatus(key) == ResearchStatus.NotAvailable);
            }
        }

        // ------------------------------------------------------------------
        // ui_tech_tree_tank.tscn, resolved for the 400x170 tree instance.
        // ------------------------------------------------------------------

        private void BuildCard(RectTransform slot, TankInfo info)
        {
            TechTreeCard.Build(
                _kit,
                slot,
                CardOffsetX,
                CardOffsetY,
                CardWidth,
                CardHeight,
                CardScale,
                info,
                _research.TankStatus(info.Name),
                _account == null ? null : _account.GetTank(info.Name),
                OnCardPressed);

            CardCount++;
        }

        private void OnCardPressed(string tank, ResearchStatus status)
        {
            if (TankPressed != null)
            {
                TankPressed(tank);
            }
        }

        // ------------------------------------------------------------------
        // add_arrow_to_control, ported verbatim.
        // ------------------------------------------------------------------

        private void AddArrow(RectTransform slot, int lvlOffset, int branchDiff, bool dim)
        {
            if (lvlOffset < 0 || Mathf.Abs(lvlOffset) > 1)
            {
                return;
            }

            var sign = branchDiff > 0 ? 1 : (branchDiff < 0 ? -1 : 0);
            var span = Mathf.Abs(branchDiff);
            var alpha = dim ? 0.5f : 1f;
            var tint = new Color(1f, 1f, 1f, alpha);

            if (lvlOffset == 0)
            {
                var px = SlotWidth * 0.5f;
                var py = sign > 0 ? -8f : SlotHeight * 0.9f;
                if (sign > 0 && branchDiff > 1)
                {
                    py -= SlotHeight * span * 0.5f;
                }

                var height = (20f + (span > 1 ? 45f : 0f)) * span;
                var bar = _kit.Picture(
                    "arrow_base", slot, null, px, py, ArrowThickness, height);
                bar.color = tint;

                var headX = px + ((-4.5f + (1.5f * sign)) * sign);
                var headY = py + (sign > 0 ? 0f : height);
                var head = _kit.Picture(
                    "arrow_head", slot, "tree_arrowhead", headX, headY, 9f, 9f);
                head.color = tint;
                head.rectTransform.localRotation =
                    Quaternion.Euler(0f, 0f, 90f * sign);
            }
            else
            {
                var px = SlotWidth * 0.93f;
                var py = SlotHeight * 0.5f;
                var targetY = py - (branchDiff * ArrowBranchStep);

                var bar = _kit.Picture(
                    "arrow_base", slot, null, px, py, 15f, ArrowThickness);
                bar.color = tint;
                var bar2 = _kit.Picture(
                    "arrow_base2", slot, null, px + 15f, targetY, 12f, ArrowThickness);
                bar2.color = tint;
                bar2.gameObject.SetActive(true);

                if (span > 0)
                {
                    var connector = _kit.Picture(
                        "arrow_connector",
                        slot,
                        null,
                        px + 15f,
                        Mathf.Min(py, targetY),
                        ArrowThickness,
                        Mathf.Abs(targetY - py));
                    connector.color = tint;
                }

                var head = _kit.Picture(
                    "arrow_head", slot, "tree_arrowhead", px + 27f, targetY - 3f, 9f, 9f);
                head.color = tint;
            }

            ArrowCount++;
        }

        // ------------------------------------------------------------------
        // Scrolling: Godot syncs the level ruler to the tree every frame.
        // ------------------------------------------------------------------

        public void ScrollToLevel(int level)
        {
            if (_treeContent == null)
            {
                return;
            }

            var offset = SlotWidth * Mathf.Max(0, level - 1);
            _treeContent.anchoredPosition = new Vector2(-offset, _treeContent.anchoredPosition.y);
            SyncRuler();
        }

        /// <summary>Scrolls the tree down by whole branch rows (render tools).</summary>
        public void ScrollToRow(int rows)
        {
            if (_treeContent == null)
            {
                return;
            }

            _treeContent.anchoredPosition = new Vector2(
                _treeContent.anchoredPosition.x, Mathf.Max(0, rows) * (SlotHeight + Separation));
            SyncRuler();
        }

        private void OnTreeScrolled(Vector2 value)
        {
            SyncRuler();
        }

        private void SyncRuler()
        {
            if (_rulerContent == null || _treeContent == null)
            {
                return;
            }

            _rulerContent.anchoredPosition = new Vector2(
                _treeContent.anchoredPosition.x, _rulerContent.anchoredPosition.y);
        }

        // ------------------------------------------------------------------
        // Helpers.
        // ------------------------------------------------------------------

        private static float ContentWidth()
        {
            return LeadSpacer + Separation + (LevelCount * SlotWidth)
                + ((LevelCount - 1) * Separation);
        }

        private static float ColumnX(int level)
        {
            return LeadSpacer + Separation + ((level - 1) * (SlotWidth + Separation));
        }

        private static float TanksTop()
        {
            return SlotHeight + Separation + PremiumSpacer + Separation;
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
