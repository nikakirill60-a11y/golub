using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Shop;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Time;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>What the shop can show right now.</summary>
    public enum ShopAvailability
    {
        /// <summary>No trusted time yet and a sync is running.</summary>
        Connecting,

        /// <summary>The last sync failed or expired: the shop stays closed.</summary>
        Offline,

        /// <summary>Synced with a time server: the cards are up.</summary>
        Online,
    }

    /// <summary>
    /// screens/shop of ui_hangar.tscn with scripts/shop.gd behind it: three
    /// tabs (premium account, premium tanks, containers) of horizontally
    /// scrolled ui_shop_entry_template cards, the CONFIRM / not enough gold
    /// popups and the container window.
    ///
    /// Intentional differences from Godot:
    /// * the tank tab is a weekly rotation (<see cref="WeeklyTankRotation"/>)
    ///   of Tier VI-X premium and collector vehicles instead of a fixed list,
    ///   and a tank already in the garage is not sold again;
    /// * the shop works online only: it needs the time of a network time
    ///   server (<see cref="OnlineClock"/>) and shows a connection state
    ///   instead of cards while that is missing, so the device clock cannot
    ///   move the rotation;
    /// * three containers: small for credits, big and huge for gold;
    /// * a Modern style card set (Godot only had the classic one).
    /// </summary>
    public sealed partial class ShopScreen
    {
        public const int TabPremium = 0;
        public const int TabTanks = 1;
        public const int TabContainers = 2;
        public const int TabCount = 3;

        // screens/shop/HBoxContainer: the tab row.
        private const float TabTop = 92f;
        private const float TabHeight = 66f;
        private const float TabSeparation = 4f;
        private const int TabFontSize = 25;

        // screens/shop/TabContainerCustom offset_top.
        private const float ContentTop = 158f;

        // ui_shop_entry_template.tscn: custom_minimum_size.x, the default
        // HBoxContainer separation and the Control scales of its children.
        private const float EntryWidth = 340f;
        private const float EntrySeparation = 4f;
        private const float SlotScale = 0.6f;
        private const float PriceBackScale = 0.425f;
        private const float CostScale = 0.525f;

        // scripts/shop.gd image_size_factor of each entry kind.
        private const float PremiumImageScale = 0.57f;
        private const float TankImageScale = 0.8f;
        private const float ContainerImageScale = 1f;

        // The connection state panel.
        private const float StatusWidth = 640f;
        private const float StatusHeight = 260f;

        /// <summary>How often <see cref="Tick"/> re-reads the clock.</summary>
        private const float TickSeconds = 0.25f;

        private static readonly Color OwnedTint = new Color(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color GuaranteeColor = new Color(1f, 0.85f, 0.4f, 1f);

        private enum CardKind
        {
            Premium,
            Tank,
            Container,
        }

        /// <summary>What one card shows; the style decides how.</summary>
        private sealed class CardSpec
        {
            public CardKind Kind;
            public string Name;
            public string Title;
            public Color TitleColor;
            public string Image;
            public float ImageScale;
            public int Cost;
            public ShopCurrency Currency;
            public string Description;
            public string Tag;
            public Color TagColor;
            public TankInfo Tank;
        }

        private sealed class Card
        {
            public CardKind Kind;
            public PremiumOffer Premium;
            public TankOffer Tank;
            public ContainerOffer Container;
            public RectTransform Root;
            public Text Title;
            public Text Cost;
            public Text Note;
            public Button Buy;
            public Image BuyImage;
            public Text BuyLabel;
            public bool Owned;
        }

        private sealed class Page
        {
            public RectTransform Root;
            public ScrollRect Scroll;
            public RectTransform Box;
            public readonly List<Card> Cards = new List<Card>();
        }

        private readonly HangarUiKit _kit;
        private readonly TanksDatabase _tanks;
        private readonly AccountData _account;
        private readonly OnlineClock _network;
        private readonly IClock _device;
        private readonly bool _modern;
        private readonly ShopService _service;
        private readonly IReadOnlyList<PremiumOffer> _premiumOffers = ShopCatalog.PremiumOffers();
        private readonly IReadOnlyList<ContainerOffer> _containerOffers = ShopCatalog.ContainerOffers();
        private readonly Button[] _tabs = new Button[TabCount];
        private readonly Page[] _pages = new Page[TabCount];
        private List<TankOffer> _tankOffers = new List<TankOffer>();
        private long _week = long.MinValue;

        private RectTransform _root;
        private RectTransform _tabRow;
        private RectTransform _content;
        private RectTransform _status;
        private Text _statusTitle;
        private Text _statusText;
        private Button _retry;
        private Text _rotation;
        private Text _rotationHint;
        private int _tab = TabPremium;
        private ShopAvailability _availability = ShopAvailability.Connecting;
        private bool _availabilityShown;
        private float _nextTick;
        private GenericPopup _popup;
        private int _popupClosedFrame = -1;
        private ContainerOpenScreen _opened;
        private ContainerOffer _openedOffer;

        /// <param name="network">Trusted time; the shop is closed without it.</param>
        /// <param name="device">
        /// The clock premium time is counted against, the same one the hangar
        /// premium readout uses.
        /// </param>
        public ShopScreen(
            HangarUiKit kit,
            TanksDatabase tanks,
            AccountData account,
            OnlineClock network,
            IClock device,
            bool modern,
            IRandomSource random = null)
        {
            if (kit == null)
            {
                throw new ArgumentNullException("kit");
            }

            if (account == null)
            {
                throw new ArgumentNullException("account");
            }

            _kit = kit;
            _tanks = tanks;
            _account = account;
            _network = network;
            _device = device ?? SystemClock.Instance;
            _modern = modern;
            _service = new ShopService(
                account, tanks, random ?? new SeededRandomSource(Environment.TickCount));
        }

        /// <summary>btn_menu, back to the hangar.</summary>
        public Action HomePressed;

        /// <summary>
        /// Raised after a purchase changed the save; true when a tank was
        /// added, so the hangar rebuilds its carousel.
        /// </summary>
        public Action<bool> Changed;

        public RectTransform Root
        {
            get { return _root; }
        }

        /// <summary>The hangar layer for the source's z_index = 1 btn_menu.</summary>
        public RectTransform Overlay { get; set; }

        /// <summary>Where popups and the container window go (the UI root).</summary>
        public RectTransform ModalLayer { get; set; }

        public bool IsModern
        {
            get { return _modern; }
        }

        public int Tab
        {
            get { return _tab; }
        }

        public ShopAvailability Availability
        {
            get { return _availability; }
        }

        /// <summary>This week's tank cards, in display order.</summary>
        public IReadOnlyList<TankOffer> TankOffers
        {
            get { return _tankOffers; }
        }

        /// <summary>The rotation week the tank tab was built for.</summary>
        public long Week
        {
            get { return _week; }
        }

        public string RotationText
        {
            get { return _rotation == null ? string.Empty : _rotation.text; }
        }

        public string StatusTitle
        {
            get { return _statusTitle == null ? string.Empty : _statusTitle.text; }
        }

        public bool RetryVisible
        {
            get { return _retry != null && _retry.gameObject.activeSelf; }
        }

        public GenericPopup ActivePopup
        {
            get { return _popup; }
        }

        /// <summary>The container window while it is up, or null.</summary>
        public ContainerOpenScreen OpenedContainer
        {
            get { return _opened; }
        }

        public ShopService Service
        {
            get { return _service; }
        }

        public int CardCount(int tab)
        {
            var page = PageAt(tab);
            return page == null ? 0 : page.Cards.Count;
        }

        public string CardTitle(int tab, int index)
        {
            var card = CardAt(tab, index);
            return card == null || card.Title == null ? string.Empty : card.Title.text;
        }

        public string CardCost(int tab, int index)
        {
            var card = CardAt(tab, index);
            return card == null || card.Cost == null ? string.Empty : card.Cost.text;
        }

        public bool CardOwned(int tab, int index)
        {
            var card = CardAt(tab, index);
            return card != null && card.Owned;
        }

        public Button CardButton(int tab, int index)
        {
            var card = CardAt(tab, index);
            return card == null ? null : card.Buy;
        }

        /// <summary>The BUY button of one card, as a tap would press it.</summary>
        public void PressBuy(int tab, int index)
        {
            var card = CardAt(tab, index);
            if (card != null && card.Buy != null && card.Buy.interactable)
            {
                OnBuyPressed(card);
            }
        }

        // ------------------------------------------------------------------
        // Build.
        // ------------------------------------------------------------------

        public RectTransform Build(RectTransform parent)
        {
            _root = UiFactory.Node("shop", parent);
            GodotRect.FullRect(_root);

            var blur = _kit.Picture(
                "blur", _root, null, 0f, 0f,
                UiLayout.ReferenceWidth, UiLayout.ReferenceHeight);
            HangarUiKit.Stretch(blur.rectTransform, 0f, 6f, 0f, 6f);
            blur.color = HangarUiKit.ScreenTint;
            blur.raycastTarget = false;

            // btn_menu is z_index = 1: it sits over the header plate.
            var top = Overlay ?? _root;
            var menu = _kit.BackButton(top, _modern, 50f);
            _kit.Bind(menu, RaiseHome, AudioService.SoundEnter);
            if (_modern)
            {
                BuildModernTitle(top);
            }
            else
            {
                BuildClassicTitle(top);
            }

            _tabRow = UiFactory.Node("HBoxContainer", _root);
            GodotRect.Apply(
                _tabRow, 0f, 0f, 1f, 0f, 0f, TabTop, 0f, TabTop + TabHeight,
                GodotRect.TopLeftPivot);
            _tabs[TabPremium] = BuildTab("btn_premium", TabPremium, "shop:premacc");
            _tabs[TabTanks] = BuildTab("btn_tanks", TabTanks, "shop:tanks");
            _tabs[TabContainers] = BuildTab("btn_ludka", TabContainers, "shop:containers");

            _content = UiFactory.Node("TabContainerCustom", _root);
            HangarUiKit.Stretch(_content, 0f, ContentTop, 0f, 0f);
            _pages[TabPremium] = BuildPage("prem");
            _pages[TabTanks] = BuildPage("tanks");
            _pages[TabContainers] = BuildPage("containers");
            if (_modern)
            {
                BuildModernRotation(_pages[TabTanks].Root);
            }
            else
            {
                BuildClassicRotation(_pages[TabTanks].Root);
            }

            BuildStatus();

            for (var i = 0; i < _premiumOffers.Count; i++)
            {
                AddPremiumCard(_premiumOffers[i], i);
            }

            for (var i = 0; i < _containerOffers.Count; i++)
            {
                AddContainerCard(_containerOffers[i], i);
            }

            ShowTab(TabPremium);
            Refresh(true);
            return _root;
        }

        /// <summary>Classic title: the hangar nickname's left edge and font.</summary>
        public const float ClassicTitleX = 119f;
        public const int ClassicTitleSize = 28;

        /// <summary>
        /// The classic header stays the hangar header 1:1 (the plate, the
        /// currencies row, the back arrow on the burger's spot); the title
        /// takes the nickname and premium block, centred on the arrow.
        /// </summary>
        private void BuildClassicTitle(RectTransform top)
        {
            _kit.Label(
                "title", top, ClassicTitleSize, TextAnchor.MiddleLeft, _kit.Str("shop:title"),
                _kit.Bold, UiTheme.Label, ClassicTitleX, HangarUiKit.ClassicBackButtonY, 400f,
                HangarUiKit.ClassicBackButtonHeight);
        }

        /// <summary>
        /// One HBoxContainer button: size_flags_horizontal = 3 gives each a
        /// third of the row. Classic draws StyleBoxFlat_7wvoe (solid black)
        /// and StyleBoxFlat_hfboq (transparent) while it is the open tab;
        /// Modern uses the glass tab of its other sub-screens.
        /// </summary>
        private Button BuildTab(string name, int index, string key)
        {
            var button = _kit.StyleButton(
                name, _tabRow, _modern ? "button_small_generic" : null, 0f, 0f, 0f, TabHeight);
            const float half = TabSeparation * 0.5f;
            GodotRect.Apply(
                (RectTransform)button.transform,
                index / (float)TabCount, 0f, (index + 1) / (float)TabCount, 1f,
                index == 0 ? 0f : half, 0f,
                index == TabCount - 1 ? 0f : -half, 0f,
                GodotRect.TopLeftPivot);
            var state = GodotButtonState.Of(button);
            if (!_modern)
            {
                button.GetComponent<Image>().color = Color.black;
                state.SetPressedStyle(null, new Color(0f, 0f, 0f, 0f));
            }

            state.SetPressedFont(UiTheme.ButtonFontPressed);
            var label = _kit.Label(
                "label", (RectTransform)button.transform, TabFontSize, TextAnchor.MiddleCenter,
                _kit.Str(key), _kit.Bold, UiTheme.ButtonFont, 0f, 0f, 0f, 0f);
            HangarUiKit.Stretch(label.rectTransform, 0f, 0f, 0f, 0f);
            var tab = index;
            _kit.Bind(button, () => ShowTab(tab), AudioService.SoundEnter);
            return button;
        }

        /// <summary>ScrollContainer + HBoxContainer of one tab, scrolled sideways.</summary>
        private Page BuildPage(string name)
        {
            var page = new Page();
            page.Root = UiFactory.Node(name, _content);
            GodotRect.FullRect(page.Root);

            var viewport = UiFactory.Node("ScrollContainer", page.Root);
            GodotRect.FullRect(viewport);
            // A clear hit area, so a drag that starts between cards scrolls too.
            var hit = viewport.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
            viewport.gameObject.AddComponent<RectMask2D>();

            var box = UiFactory.Node(name + "_box", viewport);
            box.anchorMin = new Vector2(0f, 0f);
            box.anchorMax = new Vector2(0f, 1f);
            box.pivot = new Vector2(0f, 1f);
            box.anchoredPosition = Vector2.zero;
            box.sizeDelta = Vector2.zero;
            var layout = box.gameObject.AddComponent<HorizontalLayoutGroup>();
            var padding = _modern ? ModernPadding : 0;
            layout.padding = new RectOffset(padding, padding, 0, 0);
            layout.spacing = _modern ? ModernSpacing : EntrySeparation;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            var fitter = box.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = box;
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 20f;

            page.Scroll = scroll;
            page.Box = box;
            return page;
        }

        private RectTransform EntryNode(Page page, string name, float width)
        {
            var entry = UiFactory.Node(name, page.Box);
            var element = entry.gameObject.AddComponent<LayoutElement>();
            element.minWidth = width;
            element.preferredWidth = width;
            return entry;
        }

        private void BuildClassicRotation(RectTransform page)
        {
            _rotation = _kit.Label(
                "rotation", page, 22, TextAnchor.MiddleLeft, string.Empty,
                _kit.Bold, UiTheme.BoldLabel, 0f, 0f, 0f, 0f);
            HangarUiKit.BottomLeft(_rotation.rectTransform, 20f, -78f, 900f, -50f);
            _rotationHint = _kit.Label(
                "rotation_hint", page, 17, TextAnchor.MiddleLeft, _kit.Str("shop:rotation/hint"),
                _kit.Regular, HangarUiKit.Caption, 0f, 0f, 0f, 0f);
            HangarUiKit.BottomLeft(_rotationHint.rectTransform, 20f, -48f, 900f, -24f);
        }

        /// <summary>
        /// Shown instead of the tabs while there is no trusted time. The
        /// panel_body and button_green regions take the Modern look by
        /// themselves, so one layout serves both styles.
        /// </summary>
        private void BuildStatus()
        {
            _status = UiFactory.Node("status", _root);
            GodotRect.Apply(
                _status, 0.5f, 0.5f, 0.5f, 0.5f,
                -StatusWidth * 0.5f, -StatusHeight * 0.5f,
                StatusWidth * 0.5f, StatusHeight * 0.5f,
                GodotRect.TopLeftPivot);
            var panel = _kit.Picture("panel", _status, "panel_body", 0f, 0f, StatusWidth, StatusHeight);
            HangarUiKit.Stretch(panel.rectTransform, 0f, 0f, 0f, 0f);
            panel.raycastTarget = true;

            _statusTitle = _kit.Label(
                "title", _status, 28, TextAnchor.MiddleCenter, string.Empty,
                _kit.Bold, UiTheme.BoldLabel, 20f, 24f, StatusWidth - 40f, 40f);
            _statusText = _kit.Label(
                "text", _status, 20, TextAnchor.UpperCenter, string.Empty,
                _kit.Regular, UiTheme.Label, 36f, 80f, StatusWidth - 72f, 76f);
            _statusText.horizontalOverflow = HorizontalWrapMode.Wrap;

            _retry = _kit.StyleButton(
                "btn_retry", _status, "button_green",
                (StatusWidth - 220f) * 0.5f, StatusHeight - 86f, 220f, 62f);
            var label = _kit.Label(
                "label", (RectTransform)_retry.transform, 22, TextAnchor.MiddleCenter,
                _kit.Str("shop:retry"), _kit.Bold, UiTheme.ButtonFont, 0f, 0f, 220f, 62f);
            HangarUiKit.Stretch(label.rectTransform, 0f, 0f, 0f, 0f);
            _kit.Bind(_retry, Retry, AudioService.SoundEnter);
        }

        // ------------------------------------------------------------------
        // Cards.
        // ------------------------------------------------------------------

        /// <summary>scripts/shop.gd prem_entry(days).</summary>
        private void AddPremiumCard(PremiumOffer offer, int index)
        {
            var card = BuildCard(_pages[TabPremium], new CardSpec
            {
                Kind = CardKind.Premium,
                Name = "shop_entry_" + index.ToString(CultureInfo.InvariantCulture),
                Title = ShopText.Fill(
                    _kit.Str("shop:entry_prem"), offer.Days.ToString(CultureInfo.InvariantCulture)),
                TitleColor = _modern ? ModernTitleColor : UiTheme.BoldLabel,
                Image = offer.ImageRegion,
                ImageScale = PremiumImageScale,
                Cost = offer.CostGold,
                Currency = ShopCurrency.Gold,
                Description = _kit.Str("shop:premium/desc"),
            });
            card.Premium = offer;
        }

        /// <summary>scripts/shop.gd tank_entry(tank, cost, icon_scale).</summary>
        private void AddTankCard(TankOffer offer, int index)
        {
            var info = offer.Tank;
            var card = BuildCard(_pages[TabTanks], new CardSpec
            {
                Kind = CardKind.Tank,
                Name = "shop_entry_" + index.ToString(CultureInfo.InvariantCulture),
                Title = TankName(info),
                TitleColor = HangarUiKit.TankNameColor(info),
                Image = HangarUiKit.TankIconRegion(info.Name),
                ImageScale = TankImageScale,
                Cost = offer.CostGold,
                Currency = ShopCurrency.Gold,
                Tag = _kit.Str(offer.Collector ? "shop:collector_tank" : "shop:premium_tank"),
                TagColor = offer.Collector ? HangarUiKit.TankNameCollector : HangarUiKit.TankNamePremium,
                Tank = info,
            });
            card.Tank = offer;
        }

        /// <summary>scripts/shop.gd container_entry(loot, cost, img, points).</summary>
        private void AddContainerCard(ContainerOffer offer, int index)
        {
            var card = BuildCard(_pages[TabContainers], new CardSpec
            {
                Kind = CardKind.Container,
                Name = "shop_entry_" + index.ToString(CultureInfo.InvariantCulture),
                Title = _kit.Str(offer.NameKey),
                TitleColor = _modern ? ModernTitleColor : UiTheme.BoldLabel,
                Image = offer.ImageRegion,
                ImageScale = ContainerImageScale,
                Cost = offer.Cost,
                Currency = offer.Currency,
                Description = _kit.Str(offer.DescriptionKey),
            });
            card.Container = offer;
        }

        private Card BuildCard(Page page, CardSpec spec)
        {
            var card = _modern ? ModernCard(page, spec) : ClassicCard(page, spec);
            card.Kind = spec.Kind;
            _kit.Bind(card.Buy, () => OnBuyPressed(card), AudioService.SoundEnter);
            page.Cards.Add(card);
            return card;
        }

        /// <summary>
        /// ui_shop_entry_template.tscn, rect for rect: Slot_04 and Slot_03 at
        /// scale 0.6, the icon kept centred at its image_size_factor, the
        /// TextExchangeBack plate, the 0.525-scaled cost row, ButtonGreen BUY
        /// and the LabelBold title above the frame. Tank tier, class and kind
        /// and the container description fill the empty top of the slot.
        /// </summary>
        private Card ClassicCard(Page page, CardSpec spec)
        {
            var root = EntryNode(page, spec.Name, EntryWidth);
            const float cx = EntryWidth * 0.5f;
            _kit.Picture(
                "TextureRect", root, "shop_slot_back",
                cx - 140.2251f, 67.80812f, 462f * SlotScale, 528f * SlotScale);
            _kit.Picture(
                "TextureRect2", root, "shop_slot_frame",
                cx - 145.03506f, 61.90406f, 479f * SlotScale, 545f * SlotScale);
            _kit.Centered(
                "icon", root, spec.Image, cx - 239.50003f, -44.369003f, 479f, 545f, spec.ImageScale);
            _kit.Picture(
                "TextureRect3", root, "shop_price_back",
                cx - 108.092285f, 339.39478f, 566f * PriceBackScale, 92f * PriceBackScale);

            var card = new Card { Root = root };

            // HBoxContainer, alignment END: cost (min 286.08) and the currency
            // icon (min 57.84) 4 px apart, 1.08 px in from the left edge.
            var hx = cx - 56.418854f;
            const float hy = 348.25092f;
            card.Cost = _kit.Label(
                "cost", root, Mathf.RoundToInt(42f * CostScale), TextAnchor.MiddleRight,
                HangarUiKit.FormatNumber(spec.Cost), _kit.Bold, UiTheme.BoldLabel,
                hx + (1.08f * CostScale), hy, 286.08f * CostScale, 49f * CostScale);
            _kit.Centered(
                "currency", root, CurrencyRegion(spec.Currency),
                hx + (291.16f * CostScale), hy, 57.84f * CostScale, 49f * CostScale, CostScale);

            card.Buy = _kit.StyleButton("ButtonCustom", root, "button_green", cx - 90f, 401f, 180f, 62f);
            card.BuyImage = card.Buy.GetComponent<Image>();
            card.BuyLabel = _kit.Label(
                "label", (RectTransform)card.Buy.transform, 22, TextAnchor.MiddleCenter,
                _kit.Str("BUY"), _kit.Bold, UiTheme.ButtonFont, 0f, 0f, 180f, 62f);
            HangarUiKit.Stretch(card.BuyLabel.rectTransform, 0f, 0f, 0f, 0f);

            card.Title = _kit.Label(
                "title", root, 27, TextAnchor.MiddleCenter, spec.Title, _kit.Bold, spec.TitleColor,
                cx - 143.04001f, 24.531311f, 286.08f, 31f);
            FitOneLine(card.Title, 18);

            if (spec.Tank != null)
            {
                var tint = HangarUiKit.TankGlyphTint(spec.Tank);
                var classGlyph = _kit.Centered(
                    "class", root, HangarUiKit.TankClassRegion(spec.Tank),
                    cx - 136f, 70f, 30f, 32f, 0.5f);
                classGlyph.color = tint;
                var levelGlyph = _kit.Centered(
                    "lvl", root, HangarUiKit.TankLevelRegion(spec.Tank),
                    cx - 106f, 70f, 40f, 32f, 0.5f);
                levelGlyph.color = tint;
                _kit.Label(
                    "tag", root, 17, TextAnchor.MiddleRight, spec.Tag, _kit.Bold, spec.TagColor,
                    cx - 64f, 70f, 194f, 32f);
            }

            if (spec.Kind == CardKind.Container)
            {
                var description = _kit.Label(
                    "desc", root, 15, TextAnchor.UpperCenter, spec.Description,
                    _kit.Regular, HangarUiKit.Caption, cx - 130f, 72f, 260f, 72f);
                description.horizontalOverflow = HorizontalWrapMode.Wrap;
                card.Note = _kit.Label(
                    "guaranteed", root, 16, TextAnchor.MiddleCenter, string.Empty,
                    _kit.Bold, GuaranteeColor, cx - 130f, 300f, 260f, 30f);
            }

            return card;
        }

        private void SetOwned(Card card, bool owned)
        {
            card.Owned = owned;
            card.Buy.interactable = !owned;
            var state = GodotButtonState.Of(card.Buy);
            if (state != null)
            {
                state.enabled = !owned;
            }

            var feedback = card.Buy.GetComponent<PressFeedback>();
            if (feedback != null)
            {
                feedback.enabled = !owned;
            }

            card.BuyLabel.text = _kit.Str(owned ? "shop:owned" : "BUY");
            if (_modern)
            {
                ModernOwned(card, owned);
            }
            else
            {
                card.BuyImage.color = owned ? OwnedTint : Color.white;
                card.BuyLabel.color = owned ? HangarUiKit.Caption : UiTheme.ButtonFont;
            }
        }

        private void RefreshCards()
        {
            for (var p = 0; p < _pages.Length; p++)
            {
                var page = _pages[p];
                if (page == null)
                {
                    continue;
                }

                for (var i = 0; i < page.Cards.Count; i++)
                {
                    var card = page.Cards[i];
                    if (card.Kind == CardKind.Tank && card.Tank != null)
                    {
                        SetOwned(card, _service.Owns(card.Tank.Name));
                    }
                    else if (card.Kind == CardKind.Container && card.Note != null)
                    {
                        // Only containers with a guaranteed tank show its counter.
                        var guaranteed = card.Container != null && card.Container.HasGuarantee;
                        card.Note.gameObject.SetActive(guaranteed);
                        card.Note.text = guaranteed ? GuaranteeLine(card.Container) : string.Empty;
                    }
                }
            }
        }

        private void RebuildTanks(long week)
        {
            _week = week;
            _tankOffers = WeeklyTankRotation.ForWeek(
                _tanks == null ? null : _tanks.Tanks, week, IsAvailable);
            var page = _pages[TabTanks];
            for (var i = 0; i < page.Cards.Count; i++)
            {
                Discard(page.Cards[i].Root.gameObject);
            }

            page.Cards.Clear();
            for (var i = 0; i < _tankOffers.Count; i++)
            {
                AddTankCard(_tankOffers[i], i);
            }

            page.Box.anchoredPosition = Vector2.zero;
            RefreshCards();
        }

        /// <summary>
        /// Only cards the hangar can draw: the carousel icon must exist. Every
        /// candidate also ships a model (checked by the EditMode tests), so
        /// the runtime does not load prefabs just to decide.
        /// </summary>
        private bool IsAvailable(TankInfo tank)
        {
            return tank != null && _kit.Atlas != null &&
                _kit.Atlas.Contains(HangarUiKit.TankIconRegion(tank.Name));
        }

        // ------------------------------------------------------------------
        // State.
        // ------------------------------------------------------------------

        /// <summary>Entering the screen: refresh the time if it is stale.</summary>
        public void Show()
        {
            _root.gameObject.SetActive(true);
            if (_network != null && (_network.NeedsRefresh || !_network.IsOnline))
            {
                _network.RequestSync();
            }

            _nextTick = 0f;
            Refresh(true);
        }

        /// <summary>Leaving the screen: nothing modal survives it.</summary>
        public void Hide()
        {
            try
            {
                ClosePopup();
                CloseOpened();
            }
            finally
            {
                _root.gameObject.SetActive(false);
            }
        }

        /// <summary>Per frame while the shop is open; works four times a second.</summary>
        public void Tick()
        {
            var now = Time.unscaledTime;
            if (now < _nextTick)
            {
                return;
            }

            _nextTick = now + TickSeconds;
            Refresh(false);
        }

        /// <summary>
        /// Re-reads the trusted clock: connection state, the tank week (cards
        /// are rebuilt the moment Monday 00:00 UTC passes) and the countdown.
        /// </summary>
        public void Refresh(bool force)
        {
            var availability = ComputeAvailability();
            if (force || !_availabilityShown || availability != _availability)
            {
                ApplyAvailability(availability);
            }

            double now;
            if (availability == ShopAvailability.Online && _network.TryGetNow(out now))
            {
                var week = WeeklyTankRotation.WeekIndex(now);
                if (week != _week)
                {
                    RebuildTanks(week);
                }

                if (_rotation != null)
                {
                    _rotation.text = RotationLine(now);
                }

                if (_network.NeedsRefresh)
                {
                    _network.RequestSync();
                }
            }

            if (force)
            {
                RefreshCards();
            }
        }

        public void ShowTab(int tab)
        {
            _tab = Mathf.Clamp(tab, 0, TabCount - 1);
            for (var i = 0; i < TabCount; i++)
            {
                if (_pages[i] != null)
                {
                    _pages[i].Root.gameObject.SetActive(i == _tab);
                }
            }

            // tab_container_custom.gd keeps the button of the open tab pressed.
            GodotButtonState.Select(_tabs, _tab);
        }

        /// <summary>btn_retry: a forced sync.</summary>
        public void Retry()
        {
            if (_network != null)
            {
                _network.RequestSync(true);
            }

            Refresh(true);
        }

        /// <summary>
        /// Escape while the shop is open: true when the shop used it (a popup
        /// that closes itself this frame, or the container window).
        /// </summary>
        public bool HandleEscape()
        {
            if (_popup != null || _popupClosedFrame == Time.frameCount)
            {
                return true;
            }

            if (_opened != null)
            {
                CloseOpened();
                return true;
            }

            return false;
        }

        private bool NetworkUnavailable()
        {
            if (!Application.isPlaying || Application.internetReachability != NetworkReachability.NotReachable)
                return false;
            if (_network != null) _network.SetOffline();
            return true;
        }

        private ShopAvailability ComputeAvailability()
        {
            if (_network == null || NetworkUnavailable())
            {
                return ShopAvailability.Offline;
            }

            double now;
            if (_network.TryGetNow(out now))
            {
                return ShopAvailability.Online;
            }

            var state = _network.State;
            return state == OnlineClockState.Syncing || state == OnlineClockState.Unknown
                ? ShopAvailability.Connecting
                : ShopAvailability.Offline;
        }

        private void ApplyAvailability(ShopAvailability availability)
        {
            _availability = availability;
            _availabilityShown = true;
            var online = availability == ShopAvailability.Online;
            _tabRow.gameObject.SetActive(online);
            _content.gameObject.SetActive(online);
            _status.gameObject.SetActive(!online);
            var connecting = availability == ShopAvailability.Connecting;
            _statusTitle.text = _kit.Str(connecting ? "shop:connecting" : "shop:offline/title");
            _statusText.text = connecting ? string.Empty : _kit.Str("shop:offline/text");
            _retry.gameObject.SetActive(availability == ShopAvailability.Offline);
        }

        /// <summary>
        /// Every purchase re-reads the trusted clock first; without it the
        /// screen switches to the connection state instead.
        /// </summary>
        private bool EnsureOnline(out double now)
        {
            now = 0d;
            if (_network != null && !NetworkUnavailable() && _network.TryGetNow(out now))
            {
                return true;
            }

            Refresh(true);
            return false;
        }

        // ------------------------------------------------------------------
        // Purchases.
        // ------------------------------------------------------------------

        /// <summary>
        /// ui_shop_entry_template btn.pressed: not enough gold opens the ERROR
        /// popup, anything else the CONFIRM popup that buys on OK.
        /// </summary>
        private void OnBuyPressed(Card card)
        {
            if (card == null || card.Owned)
            {
                return;
            }

            double now;
            if (!EnsureOnline(out now))
            {
                return;
            }

            switch (card.Kind)
            {
                case CardKind.Premium:
                    Confirm(card.Premium.CostGold, ShopCurrency.Gold, () => BuyPremium(card.Premium));
                    break;
                case CardKind.Tank:
                    Confirm(card.Tank.CostGold, ShopCurrency.Gold, () => BuyTank(card.Tank));
                    break;
                default:
                    Confirm(card.Container.Cost, card.Container.Currency, () => OpenContainer(card.Container));
                    break;
            }
        }

        private void Confirm(int cost, ShopCurrency currency, Action buy)
        {
            var text = HangarUiKit.FormatNumber(cost);
            if (!_service.CanAfford(currency, cost))
            {
                OpenPopup("ERROR", ShopService.NotEnoughKey(currency), text, true, null);
                return;
            }

            OpenPopup("CONFIRM", "popup:confirm_buy", text, false, buy);
        }

        private void BuyPremium(PremiumOffer offer)
        {
            double now;
            if (!EnsureOnline(out now))
            {
                return;
            }

            var result = _service.BuyPremium(offer, _device.NowUnixSeconds);
            if (!result.Ok)
            {
                ShowFailure(result);
                return;
            }

            RaiseChanged(false);
            RefreshCards();
        }

        private void BuyTank(TankOffer offer)
        {
            double now;
            if (!EnsureOnline(out now))
            {
                return;
            }

            // A popup left open over Monday 00:00 UTC must not sell last week's card.
            if (WeeklyTankRotation.WeekIndex(now) != _week || !_tankOffers.Contains(offer))
            {
                Refresh(true);
                return;
            }

            var result = _service.BuyTank(offer);
            if (!result.Ok)
            {
                ShowFailure(result);
                return;
            }

            RaiseChanged(true);
            RefreshCards();
        }

        /// <summary>The container entry's on_buy: pay, roll, grant, show.</summary>
        private void OpenContainer(ContainerOffer offer)
        {
            double now;
            if (!EnsureOnline(out now))
            {
                return;
            }

            var result = _service.OpenContainer(offer);
            if (!result.Ok)
            {
                ShowFailure(result);
                return;
            }

            RaiseChanged(GrantedNewTank(result));
            RefreshCards();
            ShowOpened(offer, result);
        }

        /// <summary>
        /// hangar_low.tscn open_more: not enough money closes the window (the
        /// port then names the shortfall in the ERROR popup), otherwise the
        /// same container is bought again without a CONFIRM popup.
        /// </summary>
        private void OpenMore()
        {
            var offer = _openedOffer;
            if (offer == null)
            {
                return;
            }

            double now;
            if (_network == null || NetworkUnavailable() || !_network.TryGetNow(out now))
            {
                CloseOpened();
                Refresh(true);
                return;
            }

            if (!_service.CanAfford(offer.Currency, offer.Cost))
            {
                CloseOpened();
                OpenPopup(
                    "ERROR", ShopService.NotEnoughKey(offer.Currency),
                    HangarUiKit.FormatNumber(offer.Cost), true, null);
                return;
            }

            var result = _service.OpenContainer(offer);
            if (!result.Ok)
            {
                CloseOpened();
                ShowFailure(result);
                return;
            }

            RaiseChanged(GrantedNewTank(result));
            RefreshCards();
            ShowOpened(offer, result);
        }

        private void ShowOpened(ContainerOffer offer, ShopResult result)
        {
            if (_opened == null)
            {
                _opened = ContainerOpenScreen.Create(
                    ModalLayer ?? _root, _kit, _tanks, _account, _modern);
                _opened.Closed = OnOpenedClosed;
                _opened.OpenMorePressed = OpenMore;
            }

            _openedOffer = offer;
            _opened.Play(offer, result, _service.GuaranteedPoints(offer), CoveredTank(offer));
        }

        private void OnOpenedClosed()
        {
            _opened = null;
            _openedOffer = null;
            RefreshCards();
            // The results close back onto the shop tab the container was
            // bought from (Godot's hangar_chest.close() went to the hangar).
            // Only the shop's own back button leaves for the hangar.
        }

        private void CloseOpened()
        {
            if (_opened != null)
            {
                _opened.Close();
            }
        }

        /// <summary>4.8.0 covered_tank: the first pool tank still to be won.</summary>
        private string CoveredTank(ContainerOffer offer)
        {
            var loot = _service.UsableLoot(offer);
            if (loot == null)
            {
                return null;
            }

            string first = null;
            foreach (var tank in loot.Tanks)
            {
                if (first == null)
                {
                    first = tank;
                }

                if (!_service.Owns(tank))
                {
                    return tank;
                }
            }

            return first;
        }

        private void ShowFailure(ShopResult result)
        {
            if (result.ErrorKey == ShopService.ErrorNotEnoughGold ||
                result.ErrorKey == ShopService.ErrorNotEnoughCredits)
            {
                OpenPopup("ERROR", result.ErrorKey, HangarUiKit.FormatNumber(result.Price), true, null);
                return;
            }

            if (result.ErrorKey == ShopService.ErrorTankOwned)
            {
                OpenPopup("ERROR", result.ErrorKey, null, true, null);
                RefreshCards();
                return;
            }

            Debug.LogWarning("[Blitz] shop purchase refused: " + result.ErrorKey);
            Refresh(true);
        }

        private static bool GrantedNewTank(ShopResult result)
        {
            if (result == null || result.Rewards == null)
            {
                return false;
            }

            for (var i = 0; i < result.Rewards.Count; i++)
            {
                var granted = result.Rewards[i];
                if (granted.Reward.Type == ContainerRewardType.Tank && !granted.Duplicate)
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Popups.
        // ------------------------------------------------------------------

        /// <summary>Global.create_popup with the cost spliced into %s.</summary>
        private GenericPopup OpenPopup(
            string headerKey, string textKey, string value, bool simple, Action accept)
        {
            ClosePopup();
            var popup = GenericPopup.Create(
                ModalLayer ?? _root, _kit.Atlas, _kit.Lang, _kit.Bold, _kit.Regular,
                headerKey, textKey, simple, accept);
            if (value != null && popup.Body != null)
            {
                popup.Body.text = ShopText.Fill(_kit.Str(textKey), value);
            }

            popup.Dismissed = () =>
            {
                if (_popup == popup)
                {
                    _popup = null;
                }

                _popupClosedFrame = Time.frameCount;
            };
            _popup = popup;
            return popup;
        }

        private void ClosePopup()
        {
            if (_popup == null)
            {
                return;
            }

            var popup = _popup;
            _popup = null;
            popup.Close();
        }

        // ------------------------------------------------------------------
        // Helpers.
        // ------------------------------------------------------------------

        private string RotationLine(double now)
        {
            var left = ShopText.Countdown(
                WeeklyTankRotation.TimeLeft(now), _kit.Str("shop:time/dh"), _kit.Str("shop:time/hm"));
            return ShopText.Fill(_kit.Str("shop:rotation"), left);
        }

        private string GuaranteeLine(ContainerOffer offer)
        {
            var left = Math.Max(1, offer.GuaranteedRewardPoints - _service.GuaranteedPoints(offer));
            return ShopText.Fill(
                _kit.Str("shop:guaranteed_left"), left.ToString(CultureInfo.InvariantCulture));
        }

        private string TankName(TankInfo info)
        {
            if (_kit.Lang != null)
            {
                return _kit.Lang.GetTankName(info, false);
            }

            return string.IsNullOrEmpty(info.DisplayName) ? info.Name : info.DisplayName;
        }

        /// <summary>CurrencyIconsSmall_gold of the template, or credits.</summary>
        private static string CurrencyRegion(ShopCurrency currency)
        {
            return currency == ShopCurrency.Gold ? "hangar_gold" : "hangar_credits";
        }

        /// <summary>One line that shrinks rather than spilling over a neighbour.</summary>
        private static void FitOneLine(Text label, int minSize)
        {
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = minSize;
            label.resizeTextMaxSize = label.fontSize;
        }

        private Page PageAt(int tab)
        {
            return tab < 0 || tab >= TabCount ? null : _pages[tab];
        }

        private Card CardAt(int tab, int index)
        {
            var page = PageAt(tab);
            return page == null || index < 0 || index >= page.Cards.Count ? null : page.Cards[index];
        }

        private static void Discard(GameObject node)
        {
            node.SetActive(false);
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(node);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(node);
            }
        }

        private void RaiseHome()
        {
            if (HomePressed != null)
            {
                HomePressed();
            }
        }

        private void RaiseChanged(bool tankAdded)
        {
            if (Changed != null)
            {
                Changed(tankAdded);
            }
        }
    }
}
