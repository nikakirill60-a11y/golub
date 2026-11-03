using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Shop;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Chest;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Container opening exactly like the Blitz 4.8.0 client: the original 3D
    /// chest scene (3d/Maps/chest/Box_anim.sc2, ported to Resources/Chest480)
    /// is replayed by <see cref="Chest480View"/> with its box animation,
    /// particles and sounds, and the rewards fly out of the HP_gui locator into
    /// the ChestUsage.yaml layout. Rewards have already been granted before
    /// this presentation starts; skipping, closing or buying again cannot roll
    /// or grant them twice.
    /// </summary>
    public sealed class ContainerOpenScreen : MonoBehaviour, IPointerClickHandler
    {
        public enum Phase { Intro, Charge, Reveal, Settled }

        /// <summary>The lid unlocks at 0.58 s of the box animation.</summary>
        public const float IntroEnd = 0.58f;
        /// <summary>The box animation ends; rewards start flying out.</summary>
        public const float RevealAt = 2.5f;
        public const float TileFlyTime = 0.45f;
        public const float FadeTime = 0.6f;
        /// <summary>ChestUsage darkens the scene to 0.498 behind the rewards.</summary>
        public const float SettledDarkness = 0.498f;
        public const string OpeningSound = "box_prize";
        public const string RewardSound = "whoosh_prize";
        private const float TileWidth = 138f;
        private const float TileHeight = 230f;
        private const float TileSpacing = 40f;
        private const float RowSpacing = 20f;
        private const float FlowMaxWidth = 900f;
        private const float HeaderHeight = 40f;
        private const float CommentHeight = 28f;
        private const float HeaderGap = 30f;
        private const float FooterHeight = 94f;
        private static readonly Color Blue = new Color(0.627f, 0.726f, 0.8f, 1f);
        private static readonly Color ProgressColor = new Color(1f, 1f, 0.501961f, 1f);
        private static uint _seedCounter;

        private sealed class Tile
        {
            public RectTransform Root;
            public CanvasGroup Group;
            public Text Value;
            public Text Type;
            public Text Note;
            public bool SoundPlayed;
        }

        private HangarUiKit _kit;
        private TanksDatabase _tanks;
        private AccountData _account;
        private bool _modern;
        private RectTransform _root;
        private RectTransform _content;
        private Chest480View _scene;
        private Image _covered;
        private RectTransform _progress;
        private Image _progressFill;
        private Text _header;
        private Text _comment;
        private Text _progressTitle;
        private Text _remaining;
        private Text _progressCaption;
        private Text _cost;
        private Text _gold;
        private Text _credits;
        private Image _currency;
        private Button _back;
        private Button _openMore;
        private CanvasGroup _textGroup;
        private CanvasGroup _footerGroup;
        private readonly List<Tile> _tiles = new List<Tile>();
        private ContainerOffer _offer;
        private Phase _phase;
        private float _time;
        private bool _closing;

        public Action Closed;
        public Action OpenMorePressed;
        public Phase CurrentPhase { get { return _phase; } }
        public ContainerOffer Offer { get { return _offer; } }
        public int RewardCount { get { return _tiles.Count; } }
        public string HeaderText { get { return _header.text; } }
        public string ProgressTitle { get { return _progressTitle.text; } }
        public string ProgressCaption { get { return _progressCaption.text; } }
        public string OpenMoreCost { get { return _cost.text; } }
        public Color OpenMoreCostColor { get { return _cost.color; } }
        public Button OpenMoreButton { get { return _openMore; } }
        /// <summary>The top-left back arrow: the only way out, back to the shop.</summary>
        public Button BackButton { get { return _back; } }
        public bool CoveredTankShown { get { return _covered.enabled; } }
        /// <summary>The guaranteed-tank progress bar; only containers with a guarantee show it.</summary>
        public bool ProgressShown { get { return _progress.gameObject.activeSelf; } }
        public string RewardValue(int index) { return _tiles[index].Value.text; }
        public string RewardType(int index) { return _tiles[index].Type.text; }
        public string RewardNote(int index) { return _tiles[index].Note.text; }
        /// <summary>Presentation time since the opening started.</summary>
        public float PresentationTime { get { return _time; } }
        /// <summary>The 3D chest scene (its own clock runs on after a skip).</summary>
        public Chest480View Scene { get { return _scene; } }
        public float HeaderAlpha { get { return _textGroup.alpha; } }
        public RectTransform RewardTile(int index) { return _tiles[index].Root; }
        public float RewardAlpha(int index) { return _tiles[index].Group.alpha; }

        /// <summary>
        /// The colorRandom external value "grade_box" per container: it picks
        /// the glow, sparks and confetti colour (white, blue, pink or gold).
        /// </summary>
        public static float Grade(ContainerOffer offer)
        {
            if (offer == null) return 1f;
            switch (offer.Id)
            {
                case "big": return 0.3f;
                case "huge": return 0.6f;
                case "gold":
                case "legendary": return 0f;
                default: return 1f;
            }
        }

        public float SettleTime { get { return RevealAt + _tiles.Count * TileFlyTime + FadeTime; } }

        public static ContainerOpenScreen Create(
            RectTransform parent, HangarUiKit kit, TanksDatabase tanks,
            AccountData account, bool modern)
        {
            if (kit == null) throw new ArgumentNullException("kit");
            if (account == null) throw new ArgumentNullException("account");
            var root = UiFactory.Node("container_open", parent);
            GodotRect.FullRect(root);
            var screen = root.gameObject.AddComponent<ContainerOpenScreen>();
            screen._root = root;
            screen._kit = kit;
            screen._tanks = tanks;
            screen._account = account;
            screen._modern = modern;
            screen.Build();
            return screen;
        }

        private void Build()
        {
            // ModalInput in ChestUsage: no pointer may reach the hangar.
            var blocker = UiFactory.Picture("blocker", _root, null);
            blocker.color = Color.black;
            blocker.raycastTarget = true;
            GodotRect.FullRect(blocker.rectTransform);
            // UI3DView, PercentOfParent 100% x 100%.
            _scene = Chest480View.Create("chest_scene", _root);

            // Everything above the footer; the reward block is centred in it.
            _content = UiFactory.Node("content", _root);
            _content.anchorMin = Vector2.zero;
            _content.anchorMax = Vector2.one;
            _content.offsetMin = new Vector2(0f, FooterHeight);
            _content.offsetMax = Vector2.zero;
            var text = UiFactory.Node("text_container", _content);
            GodotRect.FullRect(text);
            _textGroup = text.gameObject.AddComponent<CanvasGroup>();
            _textGroup.blocksRaycasts = false;
            _header = Label("header", text, 30, Color.white, string.Empty);
            Center(_header.rectTransform, 0f, 0f, 1000f, HeaderHeight);
            _comment = Label("comment", text, 21, Blue, _kit.Str("shop:received"));
            Center(_comment.rectTransform, 0f, 0f, 1000f, CommentHeight);

            BuildFooter();
            BuildHeaderButtons();
        }

        private void BuildHeaderButtons()
        {
            _back = _kit.BackButton(_root, _modern, 42f);
            _kit.Bind(_back, Close, AudioService.SoundEnter);
            var balances = UiFactory.Node("balances", _root);
            ModernUi.TopRight(balances, 22f, 20f, 360f, 52f);
            if (_modern)
            {
                var panel = ModernUi.Panel("glass", balances);
                GodotRect.FullRect(panel.rectTransform);
            }
            var goldIcon = _kit.Picture("gold_icon", balances, "hangar_gold", 0f, 8f, 32f, 32f);
            goldIcon.preserveAspect = true;
            _gold = _kit.Label("gold", balances, 22, TextAnchor.MiddleLeft, string.Empty,
                _kit.Bold, Color.white, 36f, 5f, 120f, 38f);
            var creditIcon = _kit.Picture("credits_icon", balances, "hangar_credits", 162f, 8f, 32f, 32f);
            creditIcon.preserveAspect = true;
            _credits = _kit.Label("credits", balances, 22, TextAnchor.MiddleLeft, string.Empty,
                _kit.Bold, Color.white, 198f, 5f, 154f, 38f);
            Fit(_gold, 15);
            Fit(_credits, 15);
        }

        private void BuildFooter()
        {
            var footerImage = _modern
                ? ModernUi.Panel("footer", _root, ModernTheme.PanelRadius, new Color(0.035f, 0.055f, 0.075f, 0.94f), true)
                : UiFactory.Picture("footer", _root, _kit.Art("hangar_footer"));
            footerImage.type = Image.Type.Sliced;
            var footer = footerImage.rectTransform;
            footer.anchorMin = new Vector2(0f, 0f);
            footer.anchorMax = new Vector2(1f, 0f);
            footer.pivot = new Vector2(0.5f, 0f);
            footer.anchoredPosition = Vector2.zero;
            footer.sizeDelta = new Vector2(0f, FooterHeight);
            _footerGroup = footer.gameObject.AddComponent<CanvasGroup>();

            // No CLOSE button here: the top back arrow is the one way out.
            _openMore = MakeButton("btn_open_more", footer, _kit.Str("shop:open_more"));
            ModernUi.BottomRight((RectTransform)_openMore.transform, 26f, 16f, 280f, 66f);
            _kit.Bind(_openMore, PressOpenMore, AudioService.SoundEnter);
            var moreText = _openMore.transform.Find("label").GetComponent<Text>();
            HangarUiKit.Place(moreText.rectTransform, 4f, 3f, 272f, 29f);
            Fit(moreText, 15);
            _currency = UiFactory.Picture("currency", _openMore.transform, null);
            _currency.preserveAspect = true;
            TopCenter(_currency.rectTransform, -62f, 47f, 24f, 24f);
            _cost = Label("cost", (RectTransform)_openMore.transform, 21, Color.white, string.Empty);
            TopCenter(_cost.rectTransform, 17f, 47f, 126f, 27f);
            Fit(_cost, 16);

            var progress = UiFactory.Node("progress_bar_container", footer);
            _progress = progress;
            TopCenter(progress, 0f, 47f, 500f, 94f);
            _progressTitle = Label("title", progress, 17, Blue, _kit.Str("shop:guaranteed_reward"));
            TopCenter(_progressTitle.rectTransform, 0f, 18f, 480f, 24f);
            Fit(_progressTitle, 14);
            _remaining = Label("remaining", progress, 14, Blue, string.Empty);
            TopCenter(_remaining.rectTransform, 0f, 40f, 480f, 22f);
            var track = ModernUi.Strip("bar_track", progress, new Color(0f, 0f, 0f, 0.65f));
            TopCenter(track.rectTransform, 0f, 58f, 420f, 8f);
            _progressFill = ModernUi.Strip("progress_growth", track.rectTransform, ProgressColor);
            _progressFill.rectTransform.anchorMin = new Vector2(0f, 0f);
            _progressFill.rectTransform.anchorMax = new Vector2(0f, 1f);
            _progressFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            _progressFill.rectTransform.anchoredPosition = Vector2.zero;
            _progressFill.rectTransform.sizeDelta = Vector2.zero;
            _progressCaption = Label("caption", progress, 15, Color.white, string.Empty);
            TopCenter(_progressCaption.rectTransform, 0f, 78f, 420f, 22f);

            _covered = UiFactory.Picture("covered_tank", footer, null);
            _covered.preserveAspect = true;
            _covered.color = new Color(0.04f, 0.08f, 0.13f, 0.85f);
            TopCenter(_covered.rectTransform, -350f, 46f, 120f, 94f);
            var outline = _covered.gameObject.AddComponent<Outline>();
            outline.effectColor = Blue;
            outline.effectDistance = new Vector2(1f, -1f);
        }

        private Button MakeButton(string name, RectTransform parent, string text)
        {
            if (_modern)
                return ModernUi.Button(name, parent, ModernUi.Tone.Green, _kit.Bold, 20, text);
            var button = _kit.StyleButton(name, parent, "button_green", 0f, 0f, 280f, 66f);
            var label = Label("label", (RectTransform)button.transform, 20, UiTheme.ButtonFont, text);
            GodotRect.FullRect(label.rectTransform);
            return button;
        }

        /// <summary>Present a successful, already committed purchase.</summary>
        public void Play(ContainerOffer offer, ShopResult result, int points, string coveredTank)
        {
            if (offer == null) throw new ArgumentNullException("offer");
            if (result == null || !result.Ok) throw new ArgumentException("A successful purchase is required.", "result");
            if (_closing) return;
            _offer = offer;
            _time = 0f;
            _phase = Phase.Intro;
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            _header.text = _kit.Str(offer.NameKey);
            _scene.Play(NextSeed(), Grade(offer));
            _scene.Darkness = 0f;
            // CHEST_OPENING plays with the scene; real time only.
            if (Application.isPlaying) PlaySound(OpeningSound);
            _covered.sprite = string.IsNullOrEmpty(coveredTank) ? null : _kit.Art(HangarUiKit.TankIconRegion(coveredTank));
            _covered.enabled = offer.HasGuarantee && _covered.sprite != null;
            _progress.gameObject.SetActive(offer.HasGuarantee);
            _currency.sprite = _kit.Art(offer.Currency == ShopCurrency.Gold ? "hangar_gold" : "hangar_credits");
            var maximum = Math.Max(1, offer.GuaranteedRewardPoints);
            points = Math.Max(0, Math.Min(points, maximum));
            _progressCaption.text = points.ToString(CultureInfo.InvariantCulture) + " / " + maximum.ToString(CultureInfo.InvariantCulture);
            _progressFill.rectTransform.sizeDelta = new Vector2(420f * points / maximum, 0f);
            _remaining.text = ShopText.Fill(_kit.Str("shop:guaranteed_left"),
                Math.Max(1, maximum - points).ToString(CultureInfo.InvariantCulture));
            ClearTiles();
            foreach (var reward in result.Rewards) BuildTile(reward);
            RefreshBalances();
            ApplyFrame(false);
        }

        private void BuildTile(GrantedReward granted)
        {
            var reward = granted.Reward;
            var tile = new Tile { Root = UiFactory.Node("reward_" + _tiles.Count, _content) };
            Center(tile.Root, 0f, 0f, TileWidth, TileHeight);
            tile.Group = tile.Root.gameObject.AddComponent<CanvasGroup>();
            var slot = _modern
                ? ModernUi.Panel("slot", tile.Root, ModernTheme.Radius, ModernTheme.Glass, true)
                : UiFactory.Picture("slot", tile.Root, _kit.Art("shop_reward_slot"));
            HangarUiKit.Place(slot.rectTransform, 0f, 0f, TileWidth, TileWidth);

            var tank = reward.Type == ContainerRewardType.Tank;
            string region;
            string typeKey;
            switch (reward.Type)
            {
                case ContainerRewardType.Tank: region = HangarUiKit.TankIconRegion(reward.Tank); typeKey = "shop:reward/tank"; break;
                case ContainerRewardType.Gold: region = "flow_result_currency_gold"; typeKey = "shop:reward/gold"; break;
                case ContainerRewardType.Credits: region = "flow_result_currency_credits"; typeKey = "shop:reward/credits"; break;
                default: region = "shop_reward_freexp"; typeKey = "shop:reward/freexp"; break;
            }
            var sprite = _kit.Art(region);
            var icon = UiFactory.Picture("icon", slot.rectTransform, sprite);
            icon.preserveAspect = true;
            var size = sprite == null ? Vector2.zero : new Vector2(sprite.rect.width, sprite.rect.height) * (tank ? 0.4f : 1.3f);
            Center(icon.rectTransform, 0f, 0f, Mathf.Min(size.x, 130f), Mathf.Min(size.y, 120f));
            var info = tank && _tanks != null ? _tanks.Get(reward.Tank) : null;
            var value = tank ? TankName(reward.Tank, info) : HangarUiKit.FormatNumber(reward.Amount);
            tile.Value = Label("value", tile.Root, tank ? 20 : 22,
                tank ? HangarUiKit.TankNameColor(info) : Color.white, value);
            HangarUiKit.Place(tile.Value.rectTransform, -16f, 142f, 170f, 28f);
            Fit(tile.Value, 14);
            tile.Type = Label("type", tile.Root, 17, Blue, _kit.Str(typeKey));
            HangarUiKit.Place(tile.Type.rectTransform, -12f, 172f, 162f, 23f);
            Fit(tile.Type, 14);
            tile.Note = Label("compensation", tile.Root, 14, ProgressColor,
                granted.Duplicate ? ShopText.Fill(_kit.Str("shop:compensation"), HangarUiKit.FormatNumber(granted.CompensationCredits)) : string.Empty);
            tile.Note.horizontalOverflow = HorizontalWrapMode.Wrap;
            tile.Note.verticalOverflow = VerticalWrapMode.Truncate;
            HangarUiKit.Place(tile.Note.rectTransform, -16f, 196f, 170f, 34f);
            _tiles.Add(tile);
        }

        private string TankName(string id, TankInfo info)
        {
            if (_kit.Lang != null) return _kit.Lang.GetTankName(id, info == null ? id : info.DisplayNameShort, true);
            return info == null || string.IsNullOrEmpty(info.DisplayNameShort) ? id : info.DisplayNameShort;
        }

        public void RefreshBalances()
        {
            _gold.text = HangarUiKit.FormatNumber(_account.Gold);
            _credits.text = HangarUiKit.FormatNumber(_account.Credits);
            if (_offer == null) return;
            _cost.text = HangarUiKit.FormatNumber(_offer.Cost);
            var balance = _offer.Currency == ShopCurrency.Gold ? _account.Gold : _account.Credits;
            _cost.color = balance >= _offer.Cost ? Color.white : UiTheme.ErrorText;
        }

        private void Update()
        {
            if (_offer == null || _closing) return;
            Step(Mathf.Min(UnityEngine.Time.unscaledDeltaTime, 0.05f), true);
        }

        /// <summary>Advance the same presentation timeline without real-time audio.</summary>
        public void AdvanceAnimation(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f)
                throw new ArgumentOutOfRangeException("seconds");
            if (_offer != null && !_closing) Step(seconds, false);
        }

        private void Step(float seconds, bool sounds)
        {
            _time += seconds;
            _scene.Advance(seconds);
            ApplyFrame(sounds);
        }

        private void ApplyFrame(bool sounds)
        {
            var revealEnd = RevealAt + _tiles.Count * TileFlyTime;
            var settleAt = revealEnd + FadeTime;
            _phase = _time < IntroEnd ? Phase.Intro : _time < RevealAt ? Phase.Charge
                : _time < settleAt ? Phase.Reveal : Phase.Settled;

            var fade = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((_time - revealEnd) / FadeTime));
            _textGroup.alpha = fade;
            _footerGroup.alpha = fade;
            _footerGroup.blocksRaycasts = fade > 0f;
            _footerGroup.interactable = fade > 0f;
            _scene.Darkness = SettledDarkness * fade;
            _openMore.interactable = _phase == Phase.Settled;

            Layout(out var targets);
            var origin = HardpointInContent();
            for (var i = 0; i < _tiles.Count; i++)
            {
                var tile = _tiles[i];
                var start = RevealAt + i * TileFlyTime;
                var p = Mathf.Clamp01((_time - start) / TileFlyTime);
                var eased = Mathf.SmoothStep(0f, 1f, p);
                tile.Root.anchoredPosition = Vector2.LerpUnclamped(origin, targets[i], eased);
                tile.Root.localScale = Vector3.one * Mathf.Lerp(0.01f, 1f, p);
                tile.Group.alpha = p;
                tile.Root.gameObject.SetActive(p > 0f);
                if (_time >= start && !tile.SoundPlayed)
                {
                    tile.SoundPlayed = true;
                    if (sounds) PlaySound(RewardSound);
                }
            }
        }

        /// <summary>
        /// ChestUsage layout: header, comment, 30 px, then the flow row
        /// (tiles 138 wide, 40 apart, at most 900 wide, wrapping, each line
        /// centred). The whole block is centred above the footer.
        /// </summary>
        private void Layout(out Vector2[] targets)
        {
            targets = new Vector2[_tiles.Count];
            var perLine = Mathf.Max(1, Mathf.FloorToInt((FlowMaxWidth + TileSpacing) / (TileWidth + TileSpacing)));
            var lines = Mathf.Max(1, (_tiles.Count + perLine - 1) / perLine);
            var height = HeaderHeight + CommentHeight + HeaderGap + lines * TileHeight + (lines - 1) * RowSpacing;
            var top = height * 0.5f;
            _header.rectTransform.anchoredPosition = new Vector2(0f, top - HeaderHeight * 0.5f);
            _comment.rectTransform.anchoredPosition = new Vector2(0f, top - HeaderHeight - CommentHeight * 0.5f);
            var rowTop = top - HeaderHeight - CommentHeight - HeaderGap;
            for (var i = 0; i < _tiles.Count; i++)
            {
                var line = i / perLine;
                var inLine = Mathf.Min(perLine, _tiles.Count - line * perLine);
                var width = inLine * TileWidth + (inLine - 1) * TileSpacing;
                var column = i - line * perLine;
                targets[i] = new Vector2(
                    -width * 0.5f + TileWidth * 0.5f + column * (TileWidth + TileSpacing),
                    rowTop - line * (TileHeight + RowSpacing) - TileHeight * 0.5f);
            }
        }

        /// <summary>The HP_gui locator projected into content coordinates.</summary>
        private Vector2 HardpointInContent()
        {
            var rect = _root.rect;
            if (rect.width < 1f || rect.height < 1f) return Vector2.zero;
            var point = _scene.ProjectHardpoint(rect.width / rect.height);
            // The content is the root minus the footer strip at the bottom.
            return new Vector2((point.x - 0.5f) * rect.width, (point.y - 0.5f) * rect.height - FooterHeight * 0.5f);
        }

        public void SkipToRewards()
        {
            if (_offer == null || _closing) return;
            _scene.AdvanceTo(RevealAt);
            _time = Mathf.Max(_time, SettleTime);
            foreach (var tile in _tiles) tile.SoundPlayed = true;
            ApplyFrame(false);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_phase != Phase.Settled) SkipToRewards();
        }

        public void PressOpenMore()
        {
            if (_phase == Phase.Settled && !_closing && OpenMorePressed != null)
                OpenMorePressed();
        }

        public void Close()
        {
            if (_closing) return;
            _closing = true;
            _root.gameObject.SetActive(false);
            var closed = Closed;
            Closed = null;
            try { if (closed != null) closed(); }
            finally { Discard(_root.gameObject); }
        }

        private void ClearTiles()
        {
            foreach (var tile in _tiles) Discard(tile.Root.gameObject);
            _tiles.Clear();
        }

        private void PlaySound(string sound)
        {
            if (_kit.Audio != null) _kit.Audio.PlaySound2D(sound, AudioBus.Ui, false, 1f);
        }

        private static uint NextSeed()
        {
            unchecked
            {
                _seedCounter++;
                return (uint)Environment.TickCount * 2654435761u ^ (_seedCounter * 40503u) | 1u;
            }
        }

        private Text Label(string name, RectTransform parent, int size, Color color, string text)
        {
            var label = UiFactory.Label(name, parent, size >= 22 ? _kit.Bold : _kit.Regular,
                size, color, TextAnchor.MiddleCenter, text);
            UiFactory.AddShadow(label, new Color(0f, 0f, 0f, 0.65f));
            return label;
        }

        private static void TopCenter(RectTransform rect, float x, float y, float w, float h)
        {
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = rect.anchorMin;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
        }

        private static void Center(RectTransform rect, float x, float y, float w, float h)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = rect.anchorMin;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
        }

        private static void Fit(Text text, int minSize)
        {
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = minSize;
            text.resizeTextMaxSize = text.fontSize;
        }

        private static void Alpha(Image image, float alpha)
        {
            var color = image.color;
            color.a = Mathf.Clamp01(alpha);
            image.color = color;
        }

        private static void Discard(GameObject node)
        {
            node.SetActive(false);
            if (Application.isPlaying) Destroy(node);
            else DestroyImmediate(node);
        }
    }
}
