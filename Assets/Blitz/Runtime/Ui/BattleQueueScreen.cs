using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Mods;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Battle;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// screens/battle_queue from assets/gfx/ui/hangar/ui_hangar.tscn, node for
    /// node: the clipped backdrop strip, the mirrored horizontal line carrying
    /// the wait-time and player counters, the mode logotype, the "please wait"
    /// caption, the two half-scale tables (tanks by tier, tanks by class) and
    /// the carousel card for the account tank. The behaviour is ui_queue.gd:
    /// the counters roll every five seconds and the timer counts up from the
    /// moment the screen opened.
    ///
    /// Every offset, scale, font size and colour below is the authored value.
    /// The screen lives in the hangar's logical canvas (1040x550 at Godot's
    /// stretch scale of 0.75, so 1386.67x733.33), which is why the tables are
    /// authored down to y = 1061 and drawn at half scale.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class BattleQueueScreen : MonoBehaviour, IBattleOnlineSink
    {
        /// <summary>ui_theme_blitz.tres leaves BoxContainer separation at 4.</summary>
        private const float BoxSeparation = 4f;

        /// <summary>carousel_tank.tscn custom_minimum_size.</summary>
        private const float CardWidth = 196f;
        private const float CardHeight = 130f;

        private static readonly Color Background =
            new Color(0.07058824f, 0.09411765f, 0.1254902f, 1f);

        /// <summary>The counters, the caption and every amount cell.</summary>
        private static readonly Color Bright =
            new Color(0.78211224f, 0.7821123f, 0.7821123f, 1f);

        /// <summary>The tier and class captions in both tables.</summary>
        private static readonly Color Dim =
            new Color(0.45193964f, 0.45193982f, 0.45193946f, 1f);

        private static readonly Color Shadow = new Color(0f, 0f, 0f, 1f);

        private static readonly string[] TypeKeys =
        {
            "queue.ht", "queue.mt", "queue.lt", "queue.td",
        };

        private readonly BattleQueueModel _model = new BattleQueueModel();
        private readonly Text[] _tierValues = new Text[TanksDatabase.MaxLevel];
        private readonly Text[] _typeValues = new Text[4];
        private Action _matched;
        private Action _cancelled;
        private Text _waitValue;
        private Text _totalValue;
        private float _openedAt;
        private float _nextRefreshAt;
        private float _matchAt;
        private bool _finished;
        private BattleMode _mode;
        private Text _modeLabel;

        // Online: the counters are the real room, nothing is rolled and the
        // battle starts when the network layer says so, not on a timer.
        private bool _online;
        private Action<string> _failed;
        private Text _caption;
        private LocalizationTable _lang;
        private string _statusKey;
        private float _startsIn = -1f;
        private float _startsInAt;

        public bool Online { get { return _online; } }
        public string ModeText { get { return _modeLabel == null ? string.Empty : _modeLabel.text; } }

        /// <summary>
        /// Key of the name printed over the mode logotype. ui_hangar.tscn
        /// rewrites randomBattle/Label with Global.battle_mode_name() every
        /// time the queue opens, so the picked mode is what the player waits
        /// for. Online keeps its own caption for a standard battle, where the
        /// mode name would hide that the room is a real one.
        /// </summary>
        public static string ModeLabelKey(BattleMode mode, bool online)
        {
            return online && mode == BattleMode.Normal
                ? BattleOnline.NameKey
                : BattleModeCatalog.NameKey(mode);
        }
        public string CaptionText { get { return _caption == null ? string.Empty : _caption.text; } }

        public IReadOnlyList<int> TierCounts { get { return _model.Tiers; } }
        public IReadOnlyList<int> TypeCounts { get { return _model.Types; } }
        public int TotalPlayers { get { return _model.TotalPlayers; } }
        public float MatchDelay { get { return _matchAt - _openedAt; } }
        public string WaitText { get { return _waitValue == null ? string.Empty : _waitValue.text; } }

        public static BattleQueueScreen Create(
            Transform parent,
            AccountData account,
            Action matched,
            Action cancelled,
            BattleMode mode = BattleMode.Normal)
        {
            var go = new GameObject("BattleQueueScreen");
            go.transform.SetParent(parent, false);
            var screen = go.AddComponent<BattleQueueScreen>();
            screen._matched = matched;
            screen._cancelled = cancelled;
            screen._mode = mode;
            screen.Build(account);
            return screen;
        }

        /// <summary>
        /// The same screen for real matchmaking: <paramref name="failed"/> gets
        /// the localization key of the reason when the network gives up.
        /// </summary>
        public static BattleQueueScreen CreateOnline(
            Transform parent,
            AccountData account,
            Action cancelled,
            Action<string> failed,
            BattleMode mode = BattleMode.Normal)
        {
            var go = new GameObject("BattleQueueScreen");
            go.transform.SetParent(parent, false);
            var screen = go.AddComponent<BattleQueueScreen>();
            screen._online = true;
            screen._cancelled = cancelled;
            screen._failed = failed;
            screen._mode = mode;
            screen.Build(account);
            return screen;
        }

        // Online: the own room (exact, live) and the shared queue (every open
        // battle, from the lobby list, a few seconds behind).
        private int _roomPlayers;
        private int _roomCapacity;
        private readonly int[] _roomTiers = new int[TanksDatabase.MaxLevel];
        private readonly int[] _roomClasses = new int[4];
        private bool _queueSeen;
        private int _queueWaiting;
        private readonly int[] _queueTiers = new int[TanksDatabase.MaxLevel];
        private readonly int[] _queueClasses = new int[4];

        /// <summary>Humans the queue screen reports as waiting right now.</summary>
        public int OnlineWaiting { get; private set; }
        public int[] OnlineTierCounts { get; } = new int[TanksDatabase.MaxLevel];
        public int[] OnlineClassCounts { get; } = new int[4];

        public void OnlineLobby(int players, int capacity, float startsIn,
            IReadOnlyList<int> tiers, IReadOnlyList<int> classes)
        {
            // A destroyed screen can still be called through the interface.
            if (this == null || !_online || _finished) { return; }
            _roomPlayers = players;
            _roomCapacity = capacity;
            Copy(tiers, _roomTiers);
            Copy(classes, _roomClasses);
            _startsIn = startsIn;
            _startsInAt = Time.unscaledTime;
            RenderOnline();
            RefreshCaption();
        }

        public void OnlineQueue(int waiting, IReadOnlyList<int> tiers, IReadOnlyList<int> classes)
        {
            // A destroyed screen can still be called through the interface.
            if (this == null || !_online || _finished) { return; }
            _queueSeen = true;
            _queueWaiting = Mathf.Max(0, waiting);
            Copy(tiers, _queueTiers);
            Copy(classes, _queueClasses);
            RenderOnline();
        }

        private static void Copy(IReadOnlyList<int> from, int[] into)
        {
            for (var i = 0; i < into.Length; i++) { into[i] = from != null && i < from.Count ? Mathf.Max(0, from[i]) : 0; }
        }

        private void RenderOnline()
        {
            for (var i = 0; i < OnlineTierCounts.Length; i++) { OnlineTierCounts[i] = _queueSeen ? _queueTiers[i] : 0; }
            for (var i = 0; i < OnlineClassCounts.Length; i++) { OnlineClassCounts[i] = _queueSeen ? _queueClasses[i] : 0; }
            BlitzLegacy.Core.Battle.OnlineQueue.Merge(OnlineTierCounts, _roomTiers);
            BlitzLegacy.Core.Battle.OnlineQueue.Merge(OnlineClassCounts, _roomClasses);
            OnlineWaiting = Mathf.Max(_queueSeen ? _queueWaiting : 0, _roomPlayers);

            // Everyone waiting on every tier; the own battle's fill in brackets.
            _totalValue.text = _queueSeen && OnlineWaiting > _roomPlayers
                ? OnlineWaiting + " (" + _roomPlayers + "/" + _roomCapacity + ")"
                : _roomPlayers + "/" + _roomCapacity;
            for (var i = 0; i < _tierValues.Length; i++) { _tierValues[i].text = OnlineTierCounts[i].ToString(); }
            for (var i = 0; i < _typeValues.Length; i++) { _typeValues[i].text = OnlineClassCounts[i].ToString(); }
        }

        public void OnlineStatus(string key)
        {
            // A destroyed screen can still be called through the interface.
            if (this == null || !_online || _finished) { return; }
            _statusKey = key;
            RefreshCaption();
        }

        public void OnlineFailed(string key)
        {
            // A destroyed screen can still be called through the interface.
            if (this == null || !_online || _finished) { return; }
            _finished = true;
            var callback = _failed;
            if (callback != null) { callback(key); }
        }

        private void RefreshCaption()
        {
            if (_caption == null || _lang == null) { return; }
            if (_startsIn >= 0f)
            {
                var left = Mathf.Max(0f, _startsIn - (Time.unscaledTime - _startsInAt));
                _caption.text = _lang.Get("online:queue/starts_in")
                    .Replace("{0}", Mathf.CeilToInt(left).ToString());
                return;
            }
            _caption.text = _lang.Get(string.IsNullOrEmpty(_statusKey) ? "queue.please_wait" : _statusKey);
        }

        private void Build(AccountData account)
        {
            var atlas = BattleFlowUi.LoadAtlas();
            var lang = BattleFlowUi.LoadLanguage();
            _lang = lang;
            var bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            if (MenuStyle.InMenus)
            {
                BuildModern(atlas, lang, bold, BlitzResources.LoadFont(BlitzResources.RegularFontName));
            }
            else
            {
                BuildClassic(account, atlas, lang, bold);
            }

            _openedAt = Time.unscaledTime;
            StartTimers();
        }

        private void BuildClassic(
            AccountData account, AtlasRegionTable atlas, LocalizationTable lang, Font bold)
        {
            var canvas = BattleFlowUi.CreateCanvas(transform, false, false, 80);
            var root = UiFactory.Node("battle_queue", canvas);
            GodotRect.FullRect(root);

            var colour = BattleFlowUi.Solid("ColorRect", root, Background);
            GodotRect.FullRect(colour.rectTransform);

            BuildBackdrop(root, atlas);
            BuildHorizontalLine(root, atlas, lang, bold);
            BuildMenuButton(root, atlas);
            _modeLabel = BuildModeLogo(root, atlas, lang, bold, ModeLabelKey(_mode, _online));
            BuildTierTable(root, atlas, lang, bold);
            BuildTypeTable(root, atlas, lang, bold);
            BuildCarouselTank(root, atlas, lang, bold, account);
        }

        private void StartTimers()
        {
            _nextRefreshAt = _openedAt + BattleQueueModel.RefreshIntervalSeconds;
            _matchAt = _openedAt + UnityEngine.Random.Range(
                BattleQueueModel.MatchDelayMinSeconds,
                BattleQueueModel.MatchDelayMaxSeconds);
            if (_online)
            {
                _matchAt = float.PositiveInfinity;
                OnlineLobby(1, 1, -1f, null, null);
                OnlineStatus("online:queue/connecting");
            }
            else
            {
                RefreshPlayers();
            }
        }

        /// <summary>
        /// bg: a 205 px strip across the top with clip_contents on, holding one
        /// oversized backdrop drawn at 76%. The clipping is the point - the
        /// texture is deliberately taller and wider than the strip.
        /// </summary>
        private static void BuildBackdrop(RectTransform root, AtlasRegionTable atlas)
        {
            var bg = UiFactory.Node("bg", root);
            GodotRect.Apply(bg, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 205f);
            bg.gameObject.AddComponent<RectMask2D>();

            var holder = UiFactory.Node("TextureRect", bg);
            GodotRect.Apply(holder, 0f, 0f, 1f, 0f, -504f, -60.5f, 504f, 451.5f);
            holder.localScale = new Vector3(0.76f, 0.76f, 1f);
            // stretch_mode = KEEP_CENTERED.
            UiFactory.KeepCentered(holder, atlas.Get("flow_queue_bg"));
        }

        /// <summary>
        /// HorizontalLine: a zero-height control at y = 204 drawn at half scale
        /// about the middle of its top edge. Both halves of the line, the two
        /// counters and the "please wait" caption hang off it, which is why
        /// their font sizes are authored at 35 rather than 17.
        /// </summary>
        private void BuildHorizontalLine(
            RectTransform root, AtlasRegionTable atlas, LocalizationTable lang, Font bold)
        {
            var line = UiFactory.Node("HorizontalLine", root);
            GodotRect.Apply(line, 0f, 0f, 1f, 0f, 0f, 204f, 0f, 204f,
                new Vector2(0.5f, 1f));
            line.localScale = new Vector3(0.5f, 0.5f, 1f);

            var left = UiFactory.Picture("1", line, atlas.Get("flow_queue_line"));
            GodotRect.Apply(left.rectTransform, 0.5f, 0f, 0.5f, 0f,
                -1136f, 0f, 0f, 72f, new Vector2(1f, 1f));
            left.rectTransform.localScale = new Vector3(1.46f, 1f, 1f);

            var right = UiFactory.Node("2", line);
            GodotRect.Apply(right, 0.5f, 0f, 0.5f, 0f, 0f, 0f, 1136f, 72f,
                new Vector2(0f, 1f));
            right.localScale = new Vector3(1.46f, 1f, 1f);
            // flip_h mirrors the texture inside the same rect, so the mirror
            // lives on a child that fills it.
            var mirrored = UiFactory.Picture("texture", right, atlas.Get("flow_queue_line"));
            GodotRect.FullRect(mirrored.rectTransform);
            mirrored.rectTransform.localScale = new Vector3(-1f, 1f, 1f);

            var wtime = Row("wtime", line, -616.5f, 16f, -215.5f, 56f, TextAnchor.MiddleLeft);
            Icon(wtime, atlas.Get("flow_queue_clock"));
            Spacer(wtime, 8.89f);
            Counter(wtime, bold, lang.Get("queue.wait_time"));
            _waitValue = Counter(wtime, bold, "00:00");

            var players = Row("players", line, 207f, 16f, 593f, 56f, TextAnchor.MiddleLeft);
            Icon(players, atlas.Get("flow_queue_players"));
            Spacer(players, 8.89f);
            Counter(players, bold, lang.Get("queue.players"));
            _totalValue = Counter(players, bold, "57");

            var wait = UiFactory.Label("wait", line, bold, 35, Bright,
                TextAnchor.MiddleCenter, lang.Get("queue.please_wait"));
            GodotRect.Apply(wait.rectTransform, 0.5f, 0f, 0.5f, 0f,
                -266.5f, 120f, 272.5f, 156f);
            UiFactory.AddShadow(wait, Shadow, new Vector2(1f, 2f));
            _caption = wait;
        }

        /// <summary>btn_menu: ButtonSmallGeneric with a 50 px wide icon.</summary>
        private void BuildMenuButton(RectTransform root, AtlasRegionTable atlas)
        {
            var button = UiFactory.IconButton(
                "btn_menu", root, atlas.Get("hangar_gray_button"), atlas.Get("icon_back"));
            GodotRect.Apply((RectTransform)button.transform, 0f, 0f, 0f, 0f,
                5f, 4f, 99f, 81f, GodotRect.TopLeftPivot);
            var icon = button.transform.Find("icon") as RectTransform;
            if (icon != null)
            {
                // icon_max_width = 50, icon_alignment centred.
                var sprite = atlas.Get("icon_back");
                var height = sprite == null || sprite.rect.width <= 0f
                    ? 50f
                    : 50f * sprite.rect.height / sprite.rect.width;
                icon.sizeDelta = new Vector2(50f, height);
            }
            button.onClick.AddListener(Cancel);
        }

        /// <summary>
        /// randomBattle: the mode logotype at 68% with the mode name printed
        /// over its lower edge.
        /// </summary>
        private static Text BuildModeLogo(
            RectTransform root, AtlasRegionTable atlas, LocalizationTable lang, Font bold, string nameKey)
        {
            var logo = UiFactory.Picture("randomBattle", root, atlas.Get("flow_queue_mode"));
            GodotRect.Apply(logo.rectTransform, 0.5f, 0f, 0.5f, 0f,
                -78.50003f, 0f, 78.5f, 157f);
            logo.rectTransform.localScale = new Vector3(0.68f, 0.68f, 1f);
            logo.preserveAspect = true;

            var label = UiFactory.Label("Label", logo.transform, bold, 41, Color.white,
                TextAnchor.MiddleCenter, lang.Get(nameKey));
            GodotRect.Apply(label.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f,
                -46f, 110.51851f, 46f, 126.51851f);
            UiFactory.AddShadow(label, Shadow, new Vector2(0f, 2f));
            return label;
        }

        /// <summary>tank_by_lvl: tier rows, top tier first, at half scale.</summary>
        private void BuildTierTable(
            RectTransform root, AtlasRegionTable atlas, LocalizationTable lang, Font bold)
        {
            var table = Column("tank_by_lvl", root, -126.5f, 344f, 318.5f, 1061f);
            Title(table, bold, lang.Get("queue.tanks_by_lvl"));
            for (var index = 0; index < _tierValues.Length; index++)
            {
                Separator(table, atlas, "line" + (index + 1));
                var row = Row("lvl" + (TanksDatabase.MaxLevel - index), table, TextAnchor.MiddleCenter);
                Cell(row, bold, Dim, TextAnchor.MiddleCenter,
                    BattleFlowUi.RomanTier(index), 94.21f);
                _tierValues[index] = Cell(
                    row, bold, Bright, TextAnchor.MiddleCenter, "0", 94.21f);
            }
        }

        /// <summary>
        /// tank_by_type: the same table for the four classes. The source reuses
        /// queue.tanks_by_lvl as the title and right-aligns the class captions.
        /// </summary>
        private void BuildTypeTable(
            RectTransform root, AtlasRegionTable atlas, LocalizationTable lang, Font bold)
        {
            var table = Column("tank_by_type", root, 169.5f, 344f, 614.5f, 1061f);
            Title(table, bold, lang.Get("queue.tanks_by_lvl"));
            var names = new[] { "ht", "mt", "lt", "td" };
            for (var index = 0; index < _typeValues.Length; index++)
            {
                Separator(table, atlas, index == 0 ? "line" : "line" + (index + 1));
                var row = Row(names[index], table, TextAnchor.MiddleRight);
                Cell(row, bold, Dim, TextAnchor.MiddleRight,
                    lang.Get(TypeKeys[index]), 94.21f);
                _typeValues[index] = Cell(
                    row, bold, Bright, TextAnchor.MiddleCenter, "0", 162f);
            }
        }

        /// <summary>
        /// carousel_tank.tscn showing the account tank. ui_queue runs the
        /// card's own setup(), so it reads like the tank's card in the hangar
        /// carousel: the nation plate, the icon at 61%, the class badge at 52%
        /// and the tier plate at 54% in the vehicle's glyph tint, all drawn
        /// with stretch_mode KEEP_CENTERED, and the name in its status colour.
        /// ui_queue then calls disable(), so the card is display only.
        /// </summary>
        private static void BuildCarouselTank(
            RectTransform root, AtlasRegionTable atlas, LocalizationTable lang,
            Font bold, AccountData account)
        {
            var selected = account == null || string.IsNullOrEmpty(account.SelectedTankName)
                ? BattleSession.DefaultTankId
                : account.SelectedTankName;
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            ModRuntime.Apply(database);
            var tank = database.Get(selected) ?? database.Get(BattleSession.DefaultTankId);

            var card = UiFactory.Picture("carousel_tank", root, atlas.Get(
                tank == null ? "tank_slot" : HangarUiKit.CarouselNationRegion(tank.Nation)));
            GodotRect.Apply(card.rectTransform, 0.5f, 0f, 0.5f, 0f,
                -367f, 368f, -171f, 498f);

            var tint = HangarUiKit.TankGlyphTint(tank);
            var iconSprite = IconSprite(atlas, tank);
            if (iconSprite != null)
            {
                KeepCentered(card.rectTransform, "icon", iconSprite,
                    0.43f, 4.5f, 195.2f, 122f, 0.61f);
            }
            else
            {
                var unknown = UiFactory.Label("icon_unknown", card.transform, bold, 64,
                    tint, TextAnchor.MiddleCenter, "?");
                GodotRect.Apply(unknown.rectTransform, 0f, 0f, 0f, 0f, 40f, 14f, 115f, 86f);
            }
            var tankClass = KeepCentered(card.rectTransform, "class",
                atlas.Get(HangarUiKit.TankClassRegion(tank)), 6.48f, 1.92f, 27.04f, 30.16f, 0.52f);
            tankClass.color = tint;
            var level = KeepCentered(card.rectTransform, "lvl",
                atlas.Get(HangarUiKit.TankLevelRegion(tank)), 32.42f, 2.88f, 29.7f, 30.78f, 0.54f);
            level.color = tint;

            var display = tank == null
                ? lang.Get("TANK_MS1")
                : lang.GetTankName(tank, true);
            var name = UiFactory.Label("name", card.transform, bold, 24,
                HangarUiKit.TankNameColor(tank), TextAnchor.MiddleLeft, display);
            GodotRect.Apply(name.rectTransform, 0f, 0f, 0f, 0f,
                8f, 100f, 118f, 125f, GodotRect.TopLeftPivot);
            UiFactory.AddShadow(name, Shadow, new Vector2(1f, 1f));
        }

        /// <summary>
        /// The hangar carousel's icon lookup: the tank's model data id, then
        /// the icon a mod package shipped.
        /// </summary>
        private static Sprite IconSprite(AtlasRegionTable atlas, TankInfo tank)
        {
            var sprite = atlas.Get("tank_icon_" + TankModelAssets.For(tank).DataId);
            if (sprite == null && tank != null && tank.Mod)
            {
                sprite = ModRuntime.Icon(tank.Name);
            }

            return sprite;
        }

        private static Image KeepCentered(
            RectTransform parent, string name, Sprite sprite,
            float x, float y, float width, float height, float scale)
        {
            var image = UiFactory.Picture(name, parent, sprite);
            var rect = new Rect(x, y, width, height);
            var placed = sprite == null
                ? rect
                : HangarScreen.KeepCenteredRect(rect, sprite.rect.size, scale);
            var rt = image.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(placed.width, placed.height);
            rt.anchoredPosition = new Vector2(placed.x, -placed.y);
            return image;
        }

        /// <summary>A VBoxContainer drawn at half scale from its top-left corner.</summary>
        private static RectTransform Column(
            string name, RectTransform parent,
            float left, float top, float right, float bottom)
        {
            var column = UiFactory.Node(name, parent);
            GodotRect.Apply(column, 0.5f, 0f, 0.5f, 0f, left, top, right, bottom,
                GodotRect.TopLeftPivot);
            column.localScale = new Vector3(0.5f, 0.5f, 1f);
            var layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = BoxSeparation;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return column;
        }

        private static void Title(RectTransform column, Font bold, string text)
        {
            var label = UiFactory.Label(
                "label", column, bold, 37, Color.white, TextAnchor.MiddleCenter, text);
            UiFactory.AddShadow(label, Shadow, new Vector2(1f, 2f));
        }

        /// <summary>
        /// HorizontalYellowLine is a two-pixel atlas strip inside a 24-pixel
        /// VBox slot. Giving the image itself the slot height stretches it into
        /// the thick bars seen in the port, so layout and drawing stay separate.
        /// </summary>
        private static void Separator(
            RectTransform column, AtlasRegionTable atlas, string name)
        {
            var slot = UiFactory.Node(name, column);
            var element = slot.gameObject.AddComponent<LayoutElement>();
            element.minHeight = 24f;
            element.preferredHeight = 24f;

            var line = UiFactory.Picture(
                "strip", slot, atlas.Get("flow_queue_yellow_line"));
            GodotRect.Apply(
                line.rectTransform,
                0f,
                0.5f,
                1f,
                0.5f,
                0f,
                -1f,
                0f,
                1f,
                new Vector2(0.5f, 0.5f));
            line.raycastTarget = false;
        }

        /// <summary>An HBoxContainer row inside one of the two tables.</summary>
        private static RectTransform Row(
            string name, RectTransform column, TextAnchor alignment)
        {
            var row = UiFactory.Node(name, column);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = BoxSeparation;
            layout.childAlignment = alignment;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            return row;
        }

        /// <summary>An HBoxContainer placed by its own authored rect.</summary>
        private static RectTransform Row(
            string name, RectTransform parent,
            float left, float top, float right, float bottom, TextAnchor alignment)
        {
            var row = UiFactory.Node(name, parent);
            GodotRect.Apply(row, 0.5f, 0f, 0.5f, 0f, left, top, right, bottom);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = BoxSeparation;
            layout.childAlignment = alignment;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            return row;
        }

        private static Text Cell(
            RectTransform row, Font bold, Color colour, TextAnchor alignment,
            string text, float minimumWidth)
        {
            var label = UiFactory.Label(
                "label", row, bold, 35, colour, alignment, text);
            UiFactory.AddShadow(label, Shadow, new Vector2(1f, 2f));
            var element = label.gameObject.AddComponent<LayoutElement>();
            element.minWidth = minimumWidth;
            element.preferredWidth = minimumWidth;
            return label;
        }

        private static Text Counter(RectTransform row, Font bold, string text)
        {
            var label = UiFactory.Label(
                "label", row, bold, 35, Bright, TextAnchor.MiddleCenter, text);
            UiFactory.AddShadow(label, Shadow, new Vector2(1f, 2f));
            return label;
        }

        private static void Icon(RectTransform row, Sprite sprite)
        {
            var image = UiFactory.Picture("TextureRect", row, sprite);
            image.preserveAspect = true;
            var element = image.gameObject.AddComponent<LayoutElement>();
            var width = sprite == null ? 40f : sprite.rect.width;
            element.minWidth = width;
            element.preferredWidth = width;
        }

        private static void Spacer(RectTransform row, float width)
        {
            var spacer = UiFactory.Node("spacer", row);
            var element = spacer.gameObject.AddComponent<LayoutElement>();
            element.minWidth = width;
            element.preferredWidth = width;
        }

        private void Update()
        {
            if (_finished)
            {
                return;
            }
            var now = Time.unscaledTime;
            if (_modern)
            {
                UpdateModern();
            }
            if (_waitValue != null)
            {
                _waitValue.text = BattleQueueModel.FormatElapsed(now - _openedAt);
            }
            if (_online)
            {
                if (_startsIn >= 0f) { RefreshCaption(); }
            }
            else if (now >= _nextRefreshAt)
            {
                _nextRefreshAt = now + BattleQueueModel.RefreshIntervalSeconds;
                RefreshPlayers();
            }
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                Cancel();
                return;
            }
            if (now >= _matchAt)
            {
                Match();
            }
        }

        private void RefreshPlayers()
        {
            _model.Refresh(
                (min, max) => UnityEngine.Random.Range(min, max + 1),
                (min, max) => UnityEngine.Random.Range(min, max));
            for (var index = 0; index < _tierValues.Length; index++)
            {
                _tierValues[index].text = _model.Tiers[index].ToString();
            }
            for (var index = 0; index < _typeValues.Length; index++)
            {
                _typeValues[index].text = _model.Types[index].ToString();
            }
            _totalValue.text = _model.TotalPlayers.ToString();
        }

        public void Match()
        {
            if (_finished)
            {
                return;
            }
            _finished = true;
            var callback = _matched;
            if (callback != null)
            {
                callback();
            }
        }

        public void Cancel()
        {
            if (_finished)
            {
                return;
            }
            _finished = true;
            if (BlitzBootstrap.Current != null && BlitzBootstrap.Current.Audio != null)
            {
                BlitzBootstrap.Current.Audio.PlayButtonClick(AudioService.SoundSelect);
            }
            var callback = _cancelled;
            if (callback != null)
            {
                callback();
            }
        }
    }
}
