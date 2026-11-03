using System;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Platform;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Battle chat: the 3-line feed under the clock and the team counters,
    /// plus the input strip in the style of the 4.8.0 ChatDialog (icons and
    /// background come from the ported BattleScreenHUD art).
    ///
    /// Each chat line is: channel icon, nick, ": ", text - icon and every
    /// character in one tint (green ally, red enemy, blue team-killer seen by
    /// allies). System notices are yellow with the nick in its own tint.
    ///
    /// SECURITY: every incoming string is re-sanitized here right before it
    /// is shown, and every Text is built with rich text disabled.
    /// </summary>
    public sealed class BattleChatView : MonoBehaviour
    {
        public const int FontSize = 19;
        public const float RowHeight = 25f;
        /// <summary>Just under the clock and the team boxes of total_tanks.</summary>
        public const float FeedTop = 56f;
        public const float MaxRowWidth = 560f;
        public const float IconSize = 20f;
        public const float IconGap = 5f;

        // Input strip: edge to edge at the very bottom (on Android it rides
        // on top of the soft keyboard), three times the old 36 px height.
        public const float InputHeight = 108f;
        public const float InputPad = 10f;
        /// <summary>The channel buttons are square: the strip height minus its padding.</summary>
        public const float ChannelButtonSize = InputHeight - InputPad * 2f;
        public const float ChannelIconSize = 60f;
        public const int InputFontSize = 36;
        /// <summary>
        /// The HUD canvas has no raycaster and the controls canvas (HUD + 1)
        /// is covered by the full screen touch surface, so the strip needs a
        /// canvas of its own above both, like the scoreboard buttons.
        /// </summary>
        public const int InputSortingOrder = BattleHud.SortingOrder + 5;

        public static readonly Color Green = new Color(0.0667f, 0.953f, 0.0627f, 1f);
        public static readonly Color Red = new Color(0.816f, 0.0706f, 0.0706f, 1f);
        public static readonly Color Yellow = new Color(1f, 0.85f, 0.2f, 1f);
        public static Color Blue { get { return BattleTankPlate.TeamKillerColour; } }

        // 4.8.0 ChatDialog.yaml: field fill and text colour.
        private static readonly Color FieldFill = new Color(0.9216f, 0.9647f, 1f, 0.5882f);
        private static readonly Color FieldText = new Color(0.1294f, 0.149f, 0.2078f, 1f);
        private static readonly Color FieldPlaceholder = new Color(0.1294f, 0.149f, 0.2078f, 0.55f);
        private static readonly Color ShadowColour = new Color(0f, 0f, 0f, 0.85f);

        /// <summary>The chat of the running battle, or null.</summary>
        public static BattleChatView Current { get; private set; }

        /// <summary>The saved "BATTLE CHAT" row (on / team only / off).</summary>
        public static int Mode
        {
            get
            {
                var boot = BlitzBootstrap.Current;
                return boot != null && boot.Settings != null ? boot.Settings.BattleChat : SettingsData.ChatOn;
            }
        }

        private double _lastSound = -1000.0;

        /// <summary>
        /// Set by the network layer: delivers an accepted local message to the
        /// other players. Null offline, where the line is only shown locally.
        /// </summary>
        public static Action<ChatChannel, string> NetSend;

        private sealed class Row
        {
            public RectTransform Root;
            public Image Icon;
            public Text Main;
            public Text Nick;
            public Text After;
        }

        private readonly ChatFeed _feed = new ChatFeed();
        private readonly ChatSpamGuard _guard = new ChatSpamGuard();
        private readonly Row[] _rows = new Row[ChatFeed.MaxVisible];

        private BattleSession _session;
        private BattleTank _player;
        private bool _mobile;
        private Font _font;
        private AtlasRegionTable _atlas;
        private LocalizationTable _lang;
        private RectTransform _feedRoot;
        private RectTransform _inputRoot;
        private InputField _field;
        private Canvas _inputCanvas;
        private bool _open;
        private int _openedFrame = -1;
        private bool _restoreLock;

        public ChatFeed Feed { get { return _feed; } }
        public ChatSpamGuard Guard { get { return _guard; } }
        public bool InputOpen { get { return _open; } }
        public RectTransform FeedRoot { get { return _feedRoot; } }
        public RectTransform InputRoot { get { return _inputRoot; } }
        public Canvas InputCanvas { get { return _inputCanvas; } }

        public static BattleChatView Create(RectTransform hudRoot, BattleSession session, BattleTank player, bool mobile)
        {
            if (hudRoot == null) { return null; }
            var node = UiFactory.Node("battle_chat", hudRoot);
            GodotRect.FullRect(node);
            var view = node.gameObject.AddComponent<BattleChatView>();
            view._session = session;
            view._player = player;
            view._mobile = mobile;
            view._atlas = AtlasRegionTable.Parse(BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            view._lang = BattleFlowUi.LoadLanguage();
            view._font = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            view.Build();
            Current = view;
            return view;
        }

        private void OnDestroy()
        {
            if (_open) { Close(); }
            if (Current == this) { Current = null; }
        }

        private void OnDisable()
        {
            if (_open) { Close(); }
        }

        private string Str(string key)
        {
            return _lang != null ? _lang.Get(key) : key;
        }

        private Sprite Art(string name)
        {
            return _atlas != null ? _atlas.Get(name) : null;
        }

        public static Color ColorOf(ChatTint tint)
        {
            switch (tint)
            {
                case ChatTint.Red: return Red;
                case ChatTint.Blue: return Blue;
                case ChatTint.Yellow: return Yellow;
                default: return Green;
            }
        }

        // ------------------------------------------------------------ build

        private void Build()
        {
            _feedRoot = UiFactory.Node("chat_feed", transform);
            GodotRect.Apply(_feedRoot, 0.5f, 0f, 0.5f, 0f,
                -MaxRowWidth * 0.5f, FeedTop, MaxRowWidth * 0.5f, FeedTop + ChatFeed.MaxVisible * RowHeight);
            for (var i = 0; i < _rows.Length; i++)
            {
                _rows[i] = BuildRow(i);
            }
            BuildInput();
        }

        private Text RowLabel(string name, RectTransform parent)
        {
            var label = UiFactory.Label(name, parent, _font, FontSize, Color.white, TextAnchor.MiddleLeft, string.Empty);
            label.supportRichText = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            UiFactory.AddShadow(label, ShadowColour, new Vector2(1f, 1f));
            var rt = label.rectTransform;
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(10f, RowHeight);
            return label;
        }

        private Row BuildRow(int index)
        {
            var row = new Row();
            row.Root = UiFactory.Node("line_" + index, _feedRoot);
            GodotRect.Apply(row.Root, 0f, 0f, 1f, 0f, 0f, index * RowHeight, 0f, (index + 1) * RowHeight);
            row.Icon = UiFactory.Picture("icon", row.Root, Art("chat_icon_team"));
            row.Icon.preserveAspect = true;
            var irt = row.Icon.rectTransform;
            irt.anchorMin = new Vector2(0f, 0.5f);
            irt.anchorMax = new Vector2(0f, 0.5f);
            irt.pivot = new Vector2(0f, 0.5f);
            irt.sizeDelta = new Vector2(IconSize, IconSize);
            row.Main = RowLabel("text", row.Root);
            row.Nick = RowLabel("nick", row.Root);
            row.After = RowLabel("after", row.Root);
            row.Root.gameObject.SetActive(false);
            return row;
        }

        private void BuildInput()
        {
            _inputRoot = UiFactory.Node("chat_input", transform);
            _inputRoot.anchorMin = new Vector2(0f, 0f);
            _inputRoot.anchorMax = new Vector2(1f, 0f);
            _inputRoot.pivot = new Vector2(0.5f, 0f);
            _inputRoot.sizeDelta = new Vector2(0f, InputHeight);
            _inputRoot.anchoredPosition = Vector2.zero;

            _inputCanvas = _inputRoot.gameObject.AddComponent<Canvas>();
            _inputCanvas.overrideSorting = true;
            _inputCanvas.sortingOrder = InputSortingOrder;
            _inputRoot.gameObject.AddComponent<GraphicRaycaster>();

            var bg = _inputRoot.gameObject.AddComponent<Image>();
            bg.sprite = Art("chat_input_bg");
            bg.type = Image.Type.Sliced;
            if (bg.sprite == null || bg.sprite.border == Vector4.zero) { bg.type = Image.Type.Simple; }
            bg.color = new Color(1f, 1f, 1f, 0.9f);
            bg.raycastTarget = true;

            // Text field, left; the two square channel buttons, right (ChatDialog order).
            var buttons = ChannelButtonSize * 2f + InputPad * 3f;
            var fieldRt = UiFactory.Node("field", _inputRoot);
            GodotRect.Apply(fieldRt, 0f, 0f, 1f, 1f, InputPad, InputPad, -buttons, -InputPad);
            var fill = fieldRt.gameObject.AddComponent<Image>();
            fill.color = FieldFill;
            _field = fieldRt.gameObject.AddComponent<InputField>();
            _field.targetGraphic = fill;
            _field.characterLimit = ChatSanitizer.MaxTextLength;
            _field.lineType = InputField.LineType.SingleLine;
            _field.contentType = InputField.ContentType.Standard;
            // Android: no native text strip over the keyboard; our strip sits on it.
            _field.shouldHideMobileInput = true;

            var placeholder = UiFactory.Label("placeholder", fieldRt, _font, InputFontSize, FieldPlaceholder,
                TextAnchor.MiddleLeft, Str("battle:chat/placeholder"));
            GodotRect.Apply(placeholder.rectTransform, 0f, 0f, 1f, 1f, 16f, 0f, -16f, 0f);
            var text = UiFactory.Label("text", fieldRt, _font, InputFontSize, FieldText, TextAnchor.MiddleLeft, string.Empty);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            GodotRect.Apply(text.rectTransform, 0f, 0f, 1f, 1f, 16f, 0f, -16f, 0f);
            _field.textComponent = text;
            _field.placeholder = placeholder;

            TeamButton = ChannelButton("btn_team", "chat_icon_team", Green,
                -(ChannelButtonSize * 2f + InputPad * 2f), ChatChannel.Team);
            AllButton = ChannelButton("btn_all", "chat_icon_all", Color.white,
                -(ChannelButtonSize + InputPad), ChatChannel.All);

            _inputRoot.gameObject.SetActive(false);
        }

        public Button TeamButton { get; private set; }
        public Button AllButton { get; private set; }

        private Button ChannelButton(string name, string icon, Color tint, float left, ChatChannel channel)
        {
            var rt = UiFactory.Node(name, _inputRoot);
            GodotRect.Apply(rt, 1f, 0f, 1f, 1f, left, InputPad, left + ChannelButtonSize, -InputPad);
            var image = rt.gameObject.AddComponent<Image>();
            image.color = new Color(0.12f, 0.15f, 0.2f, 0.75f);
            image.raycastTarget = true;
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var pic = UiFactory.Picture("icon", rt, Art(icon));
            pic.preserveAspect = true;
            pic.color = tint;
            pic.raycastTarget = false;
            var half = ChannelIconSize * 0.5f;
            GodotRect.Apply(pic.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f, -half, -half, half, half);
            button.onClick.AddListener(() => Send(channel));
            return button;
        }

        /// <summary>Soft keyboard height in strip (canvas) units, 0 on desktop.</summary>
        private float KeyboardLift()
        {
            if (!_mobile || !TouchScreenKeyboard.isSupported || !TouchScreenKeyboard.visible) { return 0f; }
            var height = TouchScreenKeyboard.area.height;
            if (height <= 0f || _inputCanvas == null) { return 0f; }
            var scale = _inputCanvas.rootCanvas != null ? _inputCanvas.rootCanvas.scaleFactor : 1f;
            return scale > 0f ? height / scale : height;
        }

        // ------------------------------------------------------------ input

        /// <summary>The mobile chat button.</summary>
        public void Toggle()
        {
            if (_open) { Close(); } else { Open(); }
        }

        public void Open()
        {
            if (_open || _inputRoot == null) { return; }
            if (!isActiveAndEnabled) { return; }
            _open = true;
            _openedFrame = Time.frameCount;
            _restoreLock = MouseLock.Captured;
            // The pointer is free while typing, so the "all" button can be clicked.
            MouseLock.Set(false);
            PcControlInput.Suppressed = true;
            _field.text = string.Empty;
            _inputRoot.gameObject.SetActive(true);
            _inputRoot.anchoredPosition = Vector2.zero;
            if (_inputCanvas != null)
            {
                _inputCanvas.overrideSorting = true;
                _inputCanvas.sortingOrder = InputSortingOrder;
            }
            if (EventSystem.current != null) { EventSystem.current.SetSelectedGameObject(_field.gameObject); }
            _field.ActivateInputField();
        }

        public void Close()
        {
            if (!_open) { return; }
            _open = false;
            PcControlInput.Suppressed = false;
            if (_field != null)
            {
                _field.text = string.Empty;
                _field.DeactivateInputField();
            }
            if (EventSystem.current != null && _field != null
                && EventSystem.current.currentSelectedGameObject == _field.gameObject)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }
            if (_inputRoot != null) { _inputRoot.gameObject.SetActive(false); }
            if (_restoreLock) { MouseLock.Set(true); }
        }

        /// <summary>Sends what is typed: Enter = team, the "all" button = everyone.</summary>
        public void Send(ChatChannel channel)
        {
            var raw = _field != null ? _field.text : string.Empty;
            Close();
            SendText(channel, raw);
        }

        /// <summary>
        /// Shows the line locally and, unless the spam guard muted the player,
        /// hands it to the network. A muted player still sees their own lines
        /// appear and fade, the others never receive them.
        /// </summary>
        public bool SendText(ChatChannel channel, string raw)
        {
            if (!ChatPreferences.Accepts(Mode, channel)) { return false; }
            var clean = ChatSanitizer.CleanText(raw);
            if (clean.Length == 0 || _player == null) { return false; }
            var now = Now();
            var accepted = _guard.Accept(now);
            var tint = ChatColors.For(_player.Team, _player.Team, _player.TeamKiller);
            _feed.AddChat(channel, tint, NickOf(_player), clean, now);
            if (accepted && NetSend != null) { NetSend(channel, clean); }
            return accepted;
        }

        private static double Now()
        {
            return Time.unscaledTimeAsDouble;
        }

        private void Update()
        {
            // The row can flip from the pause menu settings mid-battle.
            if (_open && !ChatPreferences.CanType(Mode)) { Close(); }
            if (_open) { _inputRoot.anchoredPosition = new Vector2(0f, KeyboardLift()); }
            PollKeys();
            Render(Now());
        }

        private void PollKeys()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) { return; }
            var enter = keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame;
            if (!_open)
            {
                if (enter && !_mobile && CanOpen()) { Open(); }
                return;
            }
            if (keyboard.escapeKey.wasPressedThisFrame) { Close(); return; }
            if (enter && Time.frameCount != _openedFrame)
            {
                if (string.IsNullOrEmpty(_field.text)) { Close(); }
                else { Send(ChatChannel.Team); }
            }
        }

        private bool CanOpen()
        {
            // Not over the escape menu or another popup that owns the pointer.
            if (!ChatPreferences.CanType(Mode)) { return false; }
            if (_session != null && _session.PauseMenu != null && _session.PauseMenu.Visible) { return false; }
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected == null || selected.GetComponent<InputField>() == null;
        }

        // ------------------------------------------------------------ incoming

        public string NickOf(BattleTank tank)
        {
            if (tank == null) { return string.Empty; }
            var stats = _session != null ? _session.StatsFor(tank) : null;
            var nick = stats != null ? stats.Nickname : null;
            if (string.IsNullOrEmpty(nick) && tank.Card != null && _lang != null) { nick = _lang.GetTankName(tank.Card, true); }
            return ChatSanitizer.CleanNick(nick);
        }

        /// <summary>A remote player's line. Colours are decided by this reader.</summary>
        public void Receive(BattleTank sender, ChatChannel channel, string text)
        {
            if (sender == null || _player == null || sender == _player) { return; }
            if (!ChatColors.Receives(channel, sender.Team, _player.Team)) { return; }
            if (!ChatPreferences.Accepts(Mode, channel)) { return; }
            var tint = ChatColors.For(sender.Team, _player.Team, sender.TeamKiller);
            _feed.AddChat(channel, tint, NickOf(sender), text, Now());
            Ring(ChatPreferences.SoundFor(channel));
        }

        /// <summary>A yellow system notice that names <paramref name="subject"/>.</summary>
        public void Notice(ChatNotice kind, BattleTank subject, bool blue)
        {
            if (subject == null || _player == null) { return; }
            string key;
            switch (kind)
            {
                case ChatNotice.TeamKillKick: key = "battle:chat/notice_teamkill_kick"; break;
                case ChatNotice.TurnedBlue: key = "battle:chat/notice_turned_blue"; break;
                default: return;
            }
            // Allies see the status colour, the other team sees a plain enemy.
            var nickTint = ChatColors.For(subject.Team, _player.Team, blue || subject.TeamKiller);
            _feed.AddSystem(Str(key), NickOf(subject), nickTint, Now());
            Ring(ChatPreferences.SoundNotice);
        }

        /// <summary>The 4.8.0 chat ping on the UI bus, at most once per cooldown.</summary>
        private void Ring(string clip)
        {
            var now = Now();
            if (now - _lastSound < ChatPreferences.SoundCooldown) { return; }
            _lastSound = now;
            var boot = BlitzBootstrap.Current;
            if (boot == null || boot.Audio == null) { return; }
            boot.Audio.PlaySound2D(clip, AudioBus.Ui, false, 1f);
        }

        // ------------------------------------------------------------ render

        private void Render(double now)
        {
            var visible = _feed.Visible(now);
            for (var i = 0; i < _rows.Length; i++)
            {
                var row = _rows[i];
                if (i >= visible.Count)
                {
                    if (row.Root.gameObject.activeSelf) { row.Root.gameObject.SetActive(false); }
                    continue;
                }
                if (!row.Root.gameObject.activeSelf) { row.Root.gameObject.SetActive(true); }
                Draw(row, visible[i], ChatFeed.Alpha(visible[i], now));
            }
        }

        private static Color WithAlpha(Color c, float a)
        {
            c.a *= a;
            return c;
        }

        private static void SetShadowAlpha(Text label, float a)
        {
            var shadow = label.GetComponent<Shadow>();
            if (shadow != null) { shadow.effectColor = WithAlpha(ShadowColour, a); }
        }

        private static float Width(Text label)
        {
            return string.IsNullOrEmpty(label.text) ? 0f : label.preferredWidth;
        }

        /// <summary>Shortens the label with an ellipsis until it fits.</summary>
        private static void Fit(Text label, float max)
        {
            if (max <= 0f) { label.text = string.Empty; return; }
            if (Width(label) <= max) { return; }
            var s = label.text;
            while (s.Length > 1)
            {
                s = s.Substring(0, s.Length - 1);
                label.text = s + "\u2026";
                if (label.preferredWidth <= max) { return; }
            }
        }

        private static void Place(Text label, float x)
        {
            var rt = label.rectTransform;
            rt.anchoredPosition = new Vector2(x, 0f);
            rt.sizeDelta = new Vector2(Mathf.Ceil(Width(label)) + 2f, RowHeight);
        }

        private void Draw(Row row, ChatLine line, float alpha)
        {
            // Defence in depth: whatever got into the feed is cleaned again here.
            var nick = ChatSanitizer.CleanNick(line.Nick);
            float total;
            if (line.System)
            {
                row.Icon.enabled = false;
                row.Main.text = ChatSanitizer.Clean(line.Text, ChatSanitizer.MaxRawLength);
                row.Nick.text = nick;
                row.After.text = ChatSanitizer.Clean(line.Suffix, ChatSanitizer.MaxRawLength);
                var yellow = WithAlpha(ColorOf(ChatTint.Yellow), alpha);
                row.Main.color = yellow;
                row.After.color = yellow;
                row.Nick.color = WithAlpha(ColorOf(line.NickTint), alpha);
                Fit(row.After, MaxRowWidth - Width(row.Main) - Width(row.Nick));
                total = Width(row.Main) + Width(row.Nick) + Width(row.After);
                var x = (MaxRowWidth - total) * 0.5f;
                Place(row.Main, x); x += Width(row.Main);
                Place(row.Nick, x); x += Width(row.Nick);
                Place(row.After, x);
                row.Nick.enabled = true;
                row.After.enabled = true;
            }
            else
            {
                var color = WithAlpha(ColorOf(line.Tint), alpha);
                row.Icon.enabled = true;
                row.Icon.sprite = Art(line.Channel == ChatChannel.Team ? "chat_icon_team" : "chat_icon_all");
                row.Icon.color = color;
                row.Main.text = nick + ": " + ChatSanitizer.CleanText(line.Text);
                row.Main.color = color;
                row.Nick.text = string.Empty;
                row.After.text = string.Empty;
                row.Nick.enabled = false;
                row.After.enabled = false;
                Fit(row.Main, MaxRowWidth - IconSize - IconGap);
                total = IconSize + IconGap + Width(row.Main);
                var x = (MaxRowWidth - total) * 0.5f;
                row.Icon.rectTransform.anchoredPosition = new Vector2(x, 0f);
                Place(row.Main, x + IconSize + IconGap);
            }
            SetShadowAlpha(row.Main, alpha);
            SetShadowAlpha(row.Nick, alpha);
            SetShadowAlpha(row.After, alpha);
        }
    }
}
