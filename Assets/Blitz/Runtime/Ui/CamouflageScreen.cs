using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Shop;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Lighting;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The Blitz 4.8.0 camouflage shop over the live hangar:
    /// UI/Screens/Lobby/Camouflages/CamouflagesShop.yaml with the slot,
    /// group and element prototypes of CamouflagesShopProto.yaml and the
    /// Lobby/Common/ShopFooter.yaml footer. The Summer, Winter and Desert
    /// slots and the scrolled camouflage groups fill the left 512 units; the
    /// info plate sits on the right above the footer with the price and the
    /// PURCHASE / INSTALL / REMOVE button; the preview toggle slides every
    /// panel away (0.16 s, linear, the preview_on / preview_off style
    /// sheets) to leave the vehicle alone on the screen. The picked
    /// camouflage is painted on the stand at once.
    ///
    /// The hangar canvas keeps the 4.8.0 virtual units 1:1, so every size
    /// below is the source's. Intentional differences: the header is the
    /// hangar's own (Classic or Modern) with the screen title next to the
    /// back arrow, like the other hangar screens, and the confirmation is
    /// the hangar's generic popup with the 4.8.0 texts and captions. The
    /// 4.8.0 camouflages were disposable; here one is bought once per
    /// vehicle (<see cref="CamouflageService"/>), so the footer has no
    /// "Resupply automatically" box, INSTALL and REMOVE are free and an
    /// owned tile hides its price (the row keeps its place) and wears a
    /// selection-color-green frame like the selection one.
    /// </summary>
    public sealed class CamouflageScreen
    {
        public const string TitleKey = "#camouflages:Shop/Title";
        public const string NoAvailableKey = "#camouflages:Slots/NoAvailable";
        public const string BuyKey = "#common:DialogButton/Buy";
        public const string InstallKey = "#common:DialogButton/SetUp";
        public const string RemoveKey = "#common:DialogButton/Remove";
        public const string CostKey = "#camouflages:Shop/Cost";
        public const string BuyConfirmKey = "#camouflages:Shop/BuyCamouflage/Confirmation/Cost";
        public const string HiddenInBattleKey = "#camouflages:Shop/BuyCamouflage/Confirmation";
        public const string ConfirmBuyKey = "#camouflages:Shop/BuyCamouflage/OK";
        public const string ConfirmCancelKey = "#camouflages:Shop/BuyCamouflage/Cancel";

        /// <summary>content: safeAreaInsets.left + 512, the slots and the list.</summary>
        public const float ContentWidth = 512f;

        /// <summary>footer: 94 high along the bottom edge.</summary>
        public const float FooterHeight = 94f;

        /// <summary>transitionTime of every preview_on / preview_off property.</summary>
        public const float SlideSeconds = 0.16f;

        /// <summary>The bottom of the classic hangar header: the ThinRedLine ends at 90.</summary>
        public const float ClassicHeaderBottom = 90f;

        // SlotsPanel and its CamouflageSlot prototypes.
        private const float SlotsWidth = 140f;
        private const float SlotSize = 100f;
        private const float SlotSpacing = 40f;
        private const float SlotLeft = 20f;
        private const float SlotTitleTop = -15f;
        private const float SlotTitleHeight = 26f;
        private const float EmptySlotIcon = 80f;

        // scrollView, its CamouflageGroup and CamouflageGroupElement prototypes.
        private const float ListWidth = 372f;
        private const float ScrollbarWidth = 10f;
        private const float ScrollHandleWidth = 6f;
        private const float ScrollCap = 10f;
        private const float GroupHeaderHeight = 50f;
        private const float GroupSpacing = 1f;
        private const float GroupTitleX = 20f;
        private const float GroupArrowWidth = 30f;
        private const float GroupArrowRight = 5f;
        private const float ElementWidth = 120f;
        private const float ElementHeight = 140f;
        private const int ElementsPerRow = 3;
        private const float BadgeSize = 16f;

        /// <summary>CamouflageGroupElement/button: the 92 x 90 tile.</summary>
        private const float TileWidth = 92f;
        private const float TileHeight = 90f;

        /// <summary>The Selection band: 4 texels of the 2x art, 2 units wide.</summary>
        private const float SelectionBand = 2f;

        // info, previewButton and footer.
        private const float PreviewButtonSize = 64f;
        private const float PreviewButtonInset = 7f;

        /// <summary>preview_on anchor-topAnchor -76 under the 80 unit header.</summary>
        private const float PreviewButtonTopInPreview = 4f;

        /// <summary>preview_on anchor-leftAnchor of content.</summary>
        private const float ContentSlide = 712f;

        /// <summary>preview_on moves info from 512 to 1024.</summary>
        private const float InfoSlide = 512f;

        private const float ApplyWidth = 222f;
        private const float ApplyHeight = 64f;
        private const float ApplyInset = 5f;
        private const float PriceIconRight = 242f;
        private const float PriceIconSize = 30f;
        private const float PriceValueRight = 272f;
        private const float PriceLabelGap = 30f;
        private const float PriceTextWidth = 320f;

        // Fonts.yaml classes in 4.8.0 units.
        private const int Header1Size = 25;
        private const int Header2Size = 23;
        private const int Header3Size = 21;
        private const int Text1Size = 21;
        private const int MinimumPopupText = 11;

        private const float DimmedAlpha = 0.498f;

        /// <summary>text-color-blue.</summary>
        public static readonly Color TextBlue = new Color(0.627f, 0.725f, 0.8f, 1f);

        /// <summary>selection-color-orange.</summary>
        public static readonly Color SelectionOrange = new Color(1f, 0.588f, 0f, 1f);

        /// <summary>selection-color-green of Selection.yaml: the frame of an owned camouflage.</summary>
        public static readonly Color SelectionGreen = new Color(0.169f, 0.592f, 0.294f, 1f);

        private static readonly Color BadgeRed = new Color(0.768f, 0.0235f, 0.043f, 1f);
        private static readonly Color SlotsFill = new Color(0f, 0f, 0f, 0.784f);
        private static readonly Color ListFill = new Color(0.0588f, 0.1098f, 0.1412f, 0.894f);
        private static readonly Color GroupFill = new Color(0.227f, 0.388f, 0.51f, 0.502f);
        private static readonly Color GroupPressedFill = new Color(0.227f, 0.388f, 0.51f, 0.251f);
        private static readonly Color InfoFill = new Color(0f, 0f, 0f, 0.498f);
        private static readonly Color FooterFill = new Color(0f, 0f, 0f, 0.525f);
        private static readonly Color TextShadow = new Color(0f, 0f, 0f, 0.745f);

        private sealed class SlotView
        {
            public CamouflageKind Kind;
            public RectTransform Root;
            public Button Button;
            public Image Empty;
            public Image Filled;
            public Image Selection;
            public Image Badge;
        }

        private sealed class ElementView
        {
            public CamouflageInfo Info;
            public RectTransform Root;
            public CanvasGroup Alpha;
            public Button Button;
            public Image Selection;
            public Image Owned;
            public Image Installed;
            public Image Badge;
            public CanvasGroup PriceAlpha;
            public Text Price;
        }

        private sealed class GroupView
        {
            public CamouflageGroupInfo Info;
            public RectTransform Root;
            public Button Header;
            public Image Badge;
            public RectTransform Arrow;
            public RectTransform Contents;
            public bool Collapsed;
            public readonly List<ElementView> Elements = new List<ElementView>();
        }

        private readonly HangarUiKit _kit;
        private readonly AccountData _account;
        private readonly HangarWorld _world;
        private readonly bool _modern;
        private readonly CamouflageCatalog _catalog;
        private readonly CamouflageService _service;
        private readonly SlotView[] _slots = new SlotView[CamouflageCatalog.SlotCount];
        private readonly List<GroupView> _groups = new List<GroupView>();
        private readonly HashSet<string> _unseen = new HashSet<string>(StringComparer.Ordinal);

        private RectTransform _content;
        private RectTransform _listContent;
        private ScrollRect _scroll;
        private Text _empty;
        private RectTransform _info;
        private CanvasGroup _infoAlpha;
        private Text _infoTitle;
        private Text _infoText;
        private Button _previewButton;
        private Image _previewIcon;
        private RectTransform _footer;
        private Button _apply;
        private Image _applyImage;
        private Text _applyLabel;
        private Image _priceIcon;
        private Text _priceValue;
        private Text _priceLabel;
        private GenericPopup _popup;
        private int _popupClosedFrame = -1;

        private TankSaveData _save;
        private string _tankName;
        private bool _supported;
        private bool _seenChanged;
        private CamouflageKind _slot = CamouflageKind.Summer;
        private string _selected = string.Empty;
        private bool _preview;
        private float _progress;

        public CamouflageScreen(
            HangarUiKit kit,
            AccountData account,
            HangarWorld world,
            bool modern,
            CamouflageCatalog catalog = null)
        {
            if (kit == null) { throw new ArgumentNullException("kit"); }
            if (account == null) { throw new ArgumentNullException("account"); }

            _kit = kit;
            _account = account;
            _world = world;
            _modern = modern;
            _catalog = catalog ?? CamouflageAssets.Catalog;
            _service = new CamouflageService(account, _catalog);
        }

        /// <summary>btn_menu, back to the hangar.</summary>
        public Action HomePressed;

        /// <summary>Raised after the screen changed the save: a purchase, an installation, a removal or the seen mark.</summary>
        public Action Changed;

        public RectTransform Root { get; private set; }

        /// <summary>The hangar layer for the z_index = 1 back arrow and title.</summary>
        public RectTransform Overlay { get; set; }

        /// <summary>Where popups go (the UI root).</summary>
        public RectTransform ModalLayer { get; set; }

        /// <summary>The hangar header band; preview_on slides it away with the screen's #Header.</summary>
        public RectTransform Header { get; set; }

        /// <summary>
        /// The settings whose Camouflage option the purchase note reads; the
        /// settings bound to <see cref="GraphicsQuality"/> when unset.
        /// </summary>
        public SettingsData Settings { get; set; }

        public CamouflageService Service
        {
            get { return _service; }
        }

        /// <summary>Whether the vehicle takes any camouflage at all.</summary>
        public bool Supported
        {
            get { return _supported; }
        }

        public string TankName
        {
            get { return _tankName; }
        }

        public CamouflageKind SelectedSlot
        {
            get { return _slot; }
        }

        /// <summary>The camouflage picked in the list, empty when there is none to pick.</summary>
        public string SelectedCamouflage
        {
            get { return _selected; }
        }

        public bool IsPreview
        {
            get { return _preview; }
        }

        /// <summary>0 with every panel in place, 1 once the preview slide has finished.</summary>
        public float PreviewProgress
        {
            get { return _progress; }
        }

        public GenericPopup ActivePopup
        {
            get { return _popup; }
        }

        /// <summary>What the footer button does for the picked camouflage and slot.</summary>
        public CamouflageAction CurrentAction
        {
            get { return _supported ? _service.Action(_save, _slot, _selected) : CamouflageAction.None; }
        }

        public bool ApplyShown
        {
            get { return _apply != null && _apply.gameObject.activeSelf; }
        }

        public string ApplyCaption
        {
            get { return _applyLabel == null ? string.Empty : _applyLabel.text; }
        }

        public string PriceCaption
        {
            get { return _priceLabel == null ? string.Empty : _priceLabel.text; }
        }

        public string PriceText
        {
            get { return _priceValue == null ? string.Empty : _priceValue.text; }
        }

        /// <summary>Whether the footer shows the price: only a purchase costs anything.</summary>
        public bool PriceShown
        {
            get { return _priceValue != null && _priceValue.gameObject.activeSelf; }
        }

        public string InfoTitle
        {
            get { return _infoTitle == null ? string.Empty : _infoTitle.text; }
        }

        public string InfoText
        {
            get { return _infoText == null ? string.Empty : _infoText.text; }
        }

        /// <summary>Whether the list shows #camouflages:Slots/NoAvailable instead of groups.</summary>
        public bool NoneAvailableShown
        {
            get { return _empty != null && _empty.gameObject.activeSelf; }
        }

        public int ElementCount
        {
            get
            {
                var count = 0;
                for (var i = 0; i < _groups.Count; i++) { count += _groups[i].Elements.Count; }
                return count;
            }
        }

        public bool SlotBadgeShown(CamouflageKind kind)
        {
            var view = _slots[(int)kind];
            return view != null && view.Badge.gameObject.activeSelf;
        }

        public bool ElementBadgeShown(string camouflage)
        {
            var view = Element(camouflage);
            return view != null && view.Badge.gameObject.activeSelf;
        }

        public bool ElementInstalledShown(string camouflage)
        {
            var view = Element(camouflage);
            return view != null && view.Installed.gameObject.activeSelf;
        }

        /// <summary>Whether a tile wears the green frame of an owned camouflage.</summary>
        public bool ElementOwnedShown(string camouflage)
        {
            var view = Element(camouflage);
            return view != null && view.Owned.gameObject.activeSelf;
        }

        /// <summary>The green frame's rect inside the tile: the whole tile, or inside the selection.</summary>
        public Rect ElementOwnedFrame(string camouflage)
        {
            var view = Element(camouflage);
            if (view == null) { return Rect.zero; }
            var rt = view.Owned.rectTransform;
            return new Rect(rt.anchoredPosition.x, -rt.anchoredPosition.y, rt.sizeDelta.x, rt.sizeDelta.y);
        }

        /// <summary>Whether a tile's price row is visible; an owned camouflage hides it.</summary>
        public bool ElementPriceShown(string camouflage)
        {
            var view = Element(camouflage);
            return view != null && view.PriceAlpha.alpha > 0f;
        }

        /// <summary>The camouflage icon a slot shows, empty for the bare "+" slot.</summary>
        public string SlotIcon(CamouflageKind kind)
        {
            var view = _slots[(int)kind];
            return view == null || !view.Filled.gameObject.activeSelf || view.Filled.sprite == null
                ? string.Empty
                : view.Filled.sprite.name;
        }

        // ------------------------------------------------------------------
        // Build.
        // ------------------------------------------------------------------

        public RectTransform Build(RectTransform parent)
        {
            Root = UiFactory.Node("camouflages", parent);
            GodotRect.FullRect(Root);

            // btn_menu and the title ride the screen's z_index = 1 layer, over
            // the hangar header band, the way the shop draws them.
            var top = Overlay ?? Root;
            var back = _kit.BackButton(top, _modern, 50f);
            _kit.Bind(back, RaiseHome, AudioService.SoundEnter);
            BuildTitle(top);

            BuildContent();
            BuildInfo();
            BuildPreviewButton();
            BuildFooter();
            Place(0f);
            Root.gameObject.SetActive(false);
            return Root;
        }

        private float HeaderBottom
        {
            get { return _modern ? ShopScreen.ModernTopBarHeight : ClassicHeaderBottom; }
        }

        /// <summary>How far preview_on lifts the header: its whole height and the line under it.</summary>
        private float HeaderLift
        {
            get { return HeaderBottom + 4f; }
        }

        private void BuildTitle(RectTransform top)
        {
            if (_modern)
            {
                _kit.Label(
                    "title", top, 30, TextAnchor.MiddleLeft, _kit.Str(TitleKey), _kit.Bold, UiTheme.Label,
                    ShopScreen.ModernTitleX, ShopScreen.ModernTitleY, 329f, ShopScreen.ModernTitleHeight);
                return;
            }

            _kit.Label(
                "title", top, ShopScreen.ClassicTitleSize, TextAnchor.MiddleLeft, _kit.Str(TitleKey),
                _kit.Bold, UiTheme.Label, ShopScreen.ClassicTitleX, HangarUiKit.ClassicBackButtonY, 400f,
                HangarUiKit.ClassicBackButtonHeight);
        }

        /// <summary>content: SlotsPanel (0..140) and scrollView (140..512) with its scrollbar.</summary>
        private void BuildContent()
        {
            _content = UiFactory.Node("content", Root);

            // SlotsPanel and Content are noInput: a drag there turns the tank.
            var panel = UiFactory.Picture("SlotsPanel", _content, null);
            panel.color = SlotsFill;
            HangarUiKit.LeftStretch(panel.rectTransform, 0f, 0f, SlotsWidth, 0f);

            var stack = UiFactory.Node("Slots", panel.rectTransform);
            var height = (CamouflageCatalog.SlotCount * SlotSize)
                + ((CamouflageCatalog.SlotCount - 1) * SlotSpacing);
            GodotRect.Apply(
                stack, 0f, 0.5f, 0f, 0.5f,
                SlotLeft, -height * 0.5f, SlotLeft + SlotSize, height * 0.5f,
                GodotRect.TopLeftPivot);
            for (var i = 0; i < _slots.Length; i++)
            {
                _slots[i] = BuildSlot(stack, CamouflageCatalog.KindOfSlot(i), i * (SlotSize + SlotSpacing));
            }

            BuildList();
        }

        /// <summary>CamouflageSlot: title, gray button, slot icon or the camouflage, selection, badge.</summary>
        private SlotView BuildSlot(RectTransform stack, CamouflageKind kind, float y)
        {
            var view = new SlotView { Kind = kind };
            view.Root = _kit.Node(kind.ToString(), stack, 0f, y, SlotSize, SlotSize);
            var title = _kit.Label(
                "title", view.Root, Text1Size, TextAnchor.MiddleCenter,
                _kit.Str(CamouflageCatalog.SlotNameKey(kind)), _kit.Regular, TextBlue,
                -SlotLeft, SlotTitleTop - (SlotTitleHeight * 0.5f), SlotSize + (SlotLeft * 2f), SlotTitleHeight);
            FitOneLine(title, 14);

            view.Button = MakeButton("button", view.Root, "camo_slot_button", 0f, 0f, SlotSize, SlotSize);
            var button = (RectTransform)view.Button.transform;
            var inset = (SlotSize - EmptySlotIcon) * 0.5f;
            view.Empty = _kit.Picture("icon", button, "camo_empty_slot", inset, inset, EmptySlotIcon, EmptySlotIcon);
            view.Filled = _kit.Picture("frame", button, null, 0f, 0f, SlotSize, SlotSize);
            _kit.Picture("gradient", view.Filled.rectTransform, "camo_group_gradient", -1f, -1f, 102f, 99f);
            view.Selection = _kit.Picture("selection", button, "camo_selection_slot", 0f, 0f, SlotSize, SlotSize);
            view.Selection.color = SelectionOrange;
            view.Badge = Badge(view.Root, SlotSize - BadgeSize, 0f);

            _kit.Bind(view.Button, () => SelectSlot(kind), AudioService.SoundEnter);
            return view;
        }

        private void BuildList()
        {
            var list = UiFactory.Picture("scrollView", _content, null);
            list.color = ListFill;
            list.raycastTarget = true;
            HangarUiKit.LeftStretch(list.rectTransform, SlotsWidth, 0f, SlotsWidth + ListWidth, 0f);
            list.gameObject.AddComponent<RectMask2D>();

            _listContent = UiFactory.Node("scrollContainerControl", list.rectTransform);
            GodotRect.Apply(_listContent, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, GodotRect.TopLeftPivot);

            _scroll = list.gameObject.AddComponent<ScrollRect>();
            _scroll.viewport = list.rectTransform;
            _scroll.content = _listContent;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 20f;

            // scrollbar: 10 wide on the content's right edge, its slider
            // VerticalScroll stretched vertically.
            var bar = UiFactory.Node("scrollbar", _content);
            HangarUiKit.LeftStretch(bar, ContentWidth - ScrollbarWidth, 0f, ContentWidth, 0f);
            var slider = UiFactory.Picture("slider", bar, _kit.Atlas.GetNinePatch("camo_scroll", ScrollCap));
            slider.type = Image.Type.Sliced;
            slider.pixelsPerUnitMultiplier = 2f;
            var inset = (ScrollbarWidth - ScrollHandleWidth) * 0.5f;
            slider.rectTransform.anchorMin = Vector2.zero;
            slider.rectTransform.anchorMax = Vector2.one;
            slider.rectTransform.offsetMin = new Vector2(inset, 0f);
            slider.rectTransform.offsetMax = new Vector2(-inset, 0f);
            var scrollbar = bar.gameObject.AddComponent<Scrollbar>();
            scrollbar.transition = Selectable.Transition.None;
            scrollbar.interactable = false;
            scrollbar.handleRect = slider.rectTransform;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            _scroll.verticalScrollbar = scrollbar;
            _scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            _empty = _kit.Label(
                "NoAvailable", list.rectTransform, Text1Size, TextAnchor.MiddleCenter, _kit.Str(NoAvailableKey),
                _kit.Regular, TextBlue, 0f, 0f, ListWidth, 0f);
            HangarUiKit.Stretch(_empty.rectTransform, 20f, 20f, -20f, -20f);
            _empty.horizontalOverflow = HorizontalWrapMode.Wrap;
            _empty.gameObject.SetActive(false);
        }

        /// <summary>info: the camouflage name over its description, grown to the text.</summary>
        private void BuildInfo()
        {
            _info = UiFactory.Node("info", Root);
            _info.anchorMin = new Vector2(0f, 0f);
            _info.anchorMax = new Vector2(1f, 0f);
            _info.pivot = new Vector2(0f, 0f);
            _infoAlpha = _info.gameObject.AddComponent<CanvasGroup>();
            _infoAlpha.blocksRaycasts = false;
            _infoAlpha.interactable = false;

            var layout = _info.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(15, 10, 10, 10);
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = _info.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var fill = UiFactory.Picture("content", _info, null);
            fill.color = InfoFill;
            fill.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            GodotRect.FullRect(fill.rectTransform);

            _infoTitle = UiFactory.Label(
                "title", _info, _kit.Bold, Header2Size, Color.white, TextAnchor.MiddleLeft, string.Empty);
            _infoTitle.horizontalOverflow = HorizontalWrapMode.Wrap;
            UiFactory.AddShadow(_infoTitle, TextShadow);
            _infoText = UiFactory.Label(
                "description", _info, _kit.Regular, Text1Size, TextBlue, TextAnchor.UpperLeft, string.Empty);
            _infoText.horizontalOverflow = HorizontalWrapMode.Wrap;
            UiFactory.AddShadow(_infoText, TextShadow);
        }

        /// <summary>previewButton: gray, 64 x 64, 7 off the right edge under the header.</summary>
        private void BuildPreviewButton()
        {
            _previewButton = MakeButton("previewButton", Root, "camo_button_gray", 0f, 0f,
                PreviewButtonSize, PreviewButtonSize);
            _previewIcon = UiFactory.Picture("icon", _previewButton.transform, _kit.Art("camo_preview_icon"));
            GodotRect.FullRect(_previewIcon.rectTransform);
            _kit.Bind(_previewButton, () => SetPreview(!_preview), AudioService.SoundEnter);
        }

        /// <summary>footer: the ShopFooter FooterPriceControl and StyledButton.</summary>
        private void BuildFooter()
        {
            _footer = UiFactory.Node("footer", Root);
            var back = UiFactory.Picture("back", _footer, null);
            back.color = FooterFill;
            GodotRect.FullRect(back.rectTransform);

            _apply = UiFactory.TextButton(
                "btn_apply", _footer, _kit.Art("camo_button_payment"), _kit.Bold, 0, null);
            GodotRect.Apply(
                (RectTransform)_apply.transform, 1f, 0.5f, 1f, 0.5f,
                -ApplyInset - ApplyWidth, -ApplyHeight * 0.5f, -ApplyInset, ApplyHeight * 0.5f,
                GodotRect.TopLeftPivot);
            _applyImage = _apply.GetComponent<Image>();
            _applyLabel = _kit.Label(
                "caption", (RectTransform)_apply.transform, Header3Size, TextAnchor.MiddleCenter, string.Empty,
                _kit.Bold, Color.white, 6f, 0f, ApplyWidth - 12f, ApplyHeight);
            FitOneLine(_applyLabel, 12);
            _kit.Bind(_apply, PressApply, AudioService.SoundEnter);

            _priceIcon = UiFactory.Picture("type", _footer, _kit.Art("camo_credits_big"));
            GodotRect.Apply(
                _priceIcon.rectTransform, 1f, 0.5f, 1f, 0.5f,
                -PriceIconRight - PriceIconSize, -PriceIconSize * 0.5f, -PriceIconRight, PriceIconSize * 0.5f,
                GodotRect.TopLeftPivot);
            _priceValue = _kit.Label(
                "value", _footer, Header1Size, TextAnchor.MiddleRight, string.Empty, _kit.Bold, Color.white,
                0f, 0f, PriceTextWidth, 36f);
            GodotRect.Apply(
                _priceValue.rectTransform, 1f, 0.5f, 1f, 0.5f,
                -PriceValueRight - PriceTextWidth, -18f, -PriceValueRight, 18f, GodotRect.TopLeftPivot);
            _priceLabel = _kit.Label(
                "label", _footer, Text1Size, TextAnchor.MiddleRight, string.Empty, _kit.Regular, TextBlue,
                0f, 0f, PriceTextWidth, 36f);
        }

        private GroupView BuildGroup(CamouflageGroupInfo info, List<CamouflageInfo> items)
        {
            var view = new GroupView { Info = info };
            view.Root = _kit.Node("group_" + info.Id, _listContent, 0f, 0f, ListWidth, GroupHeaderHeight);

            // section-header1: a flat band, fainter while held.
            view.Header = MakeButton("header", view.Root, null, 0f, 0f, ListWidth, GroupHeaderHeight);
            view.Header.GetComponent<Image>().color = GroupFill;
            GodotButtonState.Of(view.Header).SetPressedStyle(null, GroupPressedFill);
            var header = (RectTransform)view.Header.transform;
            var titleWidth = ListWidth - GroupTitleX - GroupArrowWidth - GroupArrowRight - BadgeSize - 20f;
            var title = _kit.Label(
                "title", header, Header2Size, TextAnchor.MiddleLeft, _kit.Str(info.NameKey), _kit.Bold, Color.white,
                GroupTitleX, 0f, titleWidth, GroupHeaderHeight);
            FitOneLine(title, 14);
            var drawn = Mathf.Min(title.preferredWidth, titleWidth);
            view.Badge = Badge(header, GroupTitleX + drawn + 10f, (GroupHeaderHeight - BadgeSize) * 0.5f);

            var icon = _kit.Node("icon", header, ListWidth - GroupArrowRight - GroupArrowWidth, 0f,
                GroupArrowWidth, GroupHeaderHeight);
            var arrow = UiFactory.Picture("arrow", icon, _kit.Art("camo_dropdown_arrow"));
            Center(arrow.rectTransform, 19f, 13f);
            view.Arrow = arrow.rectTransform;

            var rows = (items.Count + ElementsPerRow - 1) / ElementsPerRow;
            view.Contents = _kit.Node("contents", view.Root, 0f, GroupHeaderHeight, ListWidth, rows * ElementHeight);
            for (var i = 0; i < items.Count; i++)
            {
                view.Elements.Add(BuildElement(view.Contents, items[i], i));
            }

            _kit.Bind(view.Header, () => ToggleGroup(view), AudioService.SoundEnter);
            return view;
        }

        /// <summary>
        /// CamouflageGroupElement: price row, the tile button with its
        /// selection, the owned frame, gradient and check mark.
        /// </summary>
        private ElementView BuildElement(RectTransform contents, CamouflageInfo info, int index)
        {
            var view = new ElementView { Info = info };
            view.Root = _kit.Node(
                info.Id, contents, (index % ElementsPerRow) * ElementWidth, (index / ElementsPerRow) * ElementHeight,
                ElementWidth, ElementHeight);
            view.Alpha = view.Root.gameObject.AddComponent<CanvasGroup>();

            var price = _kit.Node("price", view.Root, 10f, 110f, 100f, 25f);
            view.PriceAlpha = price.gameObject.AddComponent<CanvasGroup>();
            view.Price = _kit.Label(
                "value", price, Header3Size, TextAnchor.MiddleRight, string.Empty, _kit.Bold, Color.white,
                -2f, 0f, 72f, 25f);
            _kit.Picture("type", price, "camo_credits_small", 74f, 1.5f, 22f, 22f);

            view.Button = MakeButton("button", view.Root, info.TileRegion, 14f, 15f, TileWidth, TileHeight);
            var button = (RectTransform)view.Button.transform;
            view.Selection = _kit.Picture("selection", button, "camo_selection_tile", 0f, 0f, TileWidth, TileHeight);
            view.Selection.color = SelectionOrange;
            view.Owned = _kit.Picture("owned", button, "camo_selection_tile", 0f, 0f, TileWidth, TileHeight);
            view.Owned.color = SelectionGreen;
            _kit.Picture("gradient", button, "camo_group_gradient", -1f, -1f, 94f, 92f);
            view.Installed = _kit.Picture("installed", button, "camo_check_mark", 66f, 66f, 32f, 32f);
            view.Badge = Badge(view.Root, ElementWidth - 10f - BadgeSize, 10f);

            var id = info.Id;
            _kit.Bind(view.Button, () => SelectCamouflage(id), AudioService.SoundEnter);
            return view;
        }

        private Button MakeButton(string name, RectTransform parent, string region, float x, float y, float w, float h)
        {
            var button = UiFactory.TextButton(name, parent, _kit.Art(region), _kit.Bold, 0, null);
            var image = button.GetComponent<Image>();
            if (image.sprite == null)
            {
                image.color = new Color(0f, 0f, 0f, 0.001f);
            }

            HangarUiKit.Place((RectTransform)button.transform, x, y, w, h);
            return button;
        }

        /// <summary>badge: the red WhiteCircle of the 4.8.0 lists.</summary>
        private Image Badge(RectTransform parent, float x, float y)
        {
            var badge = _kit.Picture("badge", parent, "camo_white_circle", x, y, BadgeSize, BadgeSize);
            badge.color = BadgeRed;
            badge.gameObject.SetActive(false);
            return badge;
        }

        private static void Center(RectTransform rt, float width, float height)
        {
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(width, height);
        }

        private static void FitOneLine(Text label, int minSize)
        {
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = minSize;
            label.resizeTextMaxSize = label.fontSize;
        }

        // ------------------------------------------------------------------
        // State.
        // ------------------------------------------------------------------

        /// <summary>
        /// Dresses the screen for a vehicle: its groups, the Summer slot and
        /// the camouflage installed there or the first one it takes. The
        /// first visit marks the shop seen, which drops the hangar badge;
        /// every camouflage keeps its own badge until it is picked.
        /// </summary>
        public void Init(TankSaveData save)
        {
            _save = save;
            _tankName = save != null ? save.Name : null;
            _supported = save != null && _catalog.Supports(_tankName);
            _unseen.Clear();
            if (_supported && !save.CamouflageSeen)
            {
                var tank = _catalog.Tank(_tankName);
                foreach (var info in _catalog.All)
                {
                    if (tank.Has(info.Id)) { _unseen.Add(info.Id); }
                }

                save.CamouflageSeen = true;
                _seenChanged = true;
            }

            RebuildList();
            _slot = CamouflageKind.Summer;
            _selected = DefaultPick(_slot);
            Refresh();
        }

        /// <summary>Entering the screen: panels in place, the stand moved right of them.</summary>
        public void Show()
        {
            Root.gameObject.SetActive(true);
            _preview = false;
            _progress = 0f;
            _previewIcon.sprite = _kit.Art("camo_preview_icon");
            if (_scroll != null) { _scroll.verticalNormalizedPosition = 1f; }
            Place(0f);
            ApplyViewShift(SlideSeconds);
            ShowOnTank();
            if (_seenChanged)
            {
                _seenChanged = false;
                RaiseChanged();
            }
        }

        /// <summary>
        /// Leaving the screen: nothing modal survives, the header comes back
        /// and the stand slides back to the middle the way it slid aside on
        /// entry.
        /// </summary>
        public void Hide()
        {
            try
            {
                ClosePopup();
            }
            finally
            {
                _preview = false;
                _progress = 0f;
                if (Root != null)
                {
                    Place(0f);
                    Root.gameObject.SetActive(false);
                }

                // A stand already on its way home (never shifted, the
                // preview, an earlier Hide) keeps its slide.
                if (_world != null && _world.Orbit != null
                    && !Mathf.Approximately(_world.Orbit.ViewShiftTarget, 0f))
                {
                    _world.Orbit.SetViewShift(0f, SlideSeconds);
                }
            }
        }

        /// <summary>Per frame while the screen is open: the preview slide.</summary>
        public void Tick(float deltaTime)
        {
            var target = _preview ? 1f : 0f;
            if (Mathf.Approximately(_progress, target))
            {
                return;
            }

            _progress = SlideSeconds > 0f
                ? Mathf.MoveTowards(_progress, target, Mathf.Max(0f, deltaTime) / SlideSeconds)
                : target;
            Place(_progress);
        }

        /// <summary>
        /// Escape while the screen is open: true when the screen used it (a
        /// popup that closes itself this frame, or the preview).
        /// </summary>
        public bool HandleEscape()
        {
            if (_popup != null || _popupClosedFrame == Time.frameCount)
            {
                return true;
            }

            if (_preview)
            {
                SetPreview(false);
                return true;
            }

            return false;
        }

        /// <summary>previewButton: #Screen.preview_on / preview_off.</summary>
        public void SetPreview(bool on)
        {
            if (_preview == on)
            {
                return;
            }

            _preview = on;
            _previewIcon.sprite = _kit.Art(on ? "camo_preview_icon_on" : "camo_preview_icon");
            ApplyViewShift(SlideSeconds);
        }

        /// <summary>A slot button: its camouflage, or the first one it takes.</summary>
        public void SelectSlot(CamouflageKind kind)
        {
            _slot = kind;
            _selected = DefaultPick(kind);
            Refresh();
            ShowOnTank();
        }

        /// <summary>An element of the list: picked, previewed on the stand, no longer new.</summary>
        public void SelectCamouflage(string camouflage)
        {
            if (!_supported || !_catalog.CanInstall(_tankName, _slot, camouflage))
            {
                return;
            }

            _selected = camouflage;
            _unseen.Remove(camouflage);
            Refresh();
            ShowOnTank();
        }

        /// <summary>
        /// The footer button. PURCHASE asks first, with the note about the
        /// hidden battle camouflage while the setting is off; INSTALL and
        /// REMOVE of an owned camouflage are free and happen at once.
        /// </summary>
        public void PressApply()
        {
            if (_popup != null)
            {
                return;
            }

            var action = CurrentAction;
            if (action == CamouflageAction.None)
            {
                return;
            }

            if (action != CamouflageAction.Buy)
            {
                Execute();
                return;
            }

            var cost = _service.ActionCost(_save, _slot, _selected);
            if (cost > 0 && _account.Credits < cost)
            {
                NotEnoughCredits(cost);
                return;
            }

            var body = ShopText.Fill(_kit.Str(BuyConfirmKey), HangarUiKit.FormatNumber(cost));
            if (CamouflageHiddenInBattle())
            {
                body += "\n" + _kit.Str(HiddenInBattleKey);
            }

            OpenPopup("CONFIRM", body, false, Execute, ConfirmBuyKey);
        }

        /// <summary>Paints the picked camouflage on the stand, plain when there is none.</summary>
        public void ShowOnTank()
        {
            if (_world != null)
            {
                _world.ShowCamouflage(_supported ? _selected : string.Empty);
            }
        }

        private void Execute()
        {
            var cost = _service.ActionCost(_save, _slot, _selected);
            var result = _service.Apply(_save, _slot, _selected);
            if (!result.Ok)
            {
                if (result.ErrorKey == CamouflageService.ErrorNotEnoughCredits)
                {
                    NotEnoughCredits(cost);
                }

                return;
            }

            Refresh();
            ShowOnTank();
            RaiseChanged();
        }

        private void NotEnoughCredits(int cost)
        {
            OpenPopup(
                "ERROR", ShopText.Fill(_kit.Str(CamouflageService.ErrorNotEnoughCredits), HangarUiKit.FormatNumber(cost)),
                true, null, null);
        }

        private bool CamouflageHiddenInBattle()
        {
            var settings = Settings ?? GraphicsQuality.Settings;
            return settings != null && settings.GraphicsCamouflage == SettingsData.CamouflageOff;
        }

        private string DefaultPick(CamouflageKind kind)
        {
            if (!_supported)
            {
                return string.Empty;
            }

            var installed = CamouflageService.Installed(_save, kind);
            if (_catalog.CanInstall(_tankName, kind, installed))
            {
                return installed;
            }

            var available = _catalog.Available(_tankName, kind);
            return available.Count > 0 ? available[0].Id : string.Empty;
        }

        private void ToggleGroup(GroupView view)
        {
            view.Collapsed = !view.Collapsed;
            LayoutList();
        }

        private void RebuildList()
        {
            for (var i = 0; i < _groups.Count; i++)
            {
                Discard(_groups[i].Root.gameObject);
            }

            _groups.Clear();
            var tank = _supported ? _catalog.Tank(_tankName) : null;
            if (tank != null)
            {
                foreach (var group in _catalog.Groups)
                {
                    var items = new List<CamouflageInfo>();
                    foreach (var info in _catalog.All)
                    {
                        if (string.Equals(info.Group, group.Id, StringComparison.Ordinal) && tank.Has(info.Id))
                        {
                            items.Add(info);
                        }
                    }

                    if (items.Count > 0)
                    {
                        _groups.Add(BuildGroup(group, items));
                    }
                }
            }

            _empty.gameObject.SetActive(_groups.Count == 0);
            LayoutList();
        }

        /// <summary>scrollContainerControl: the groups top-down, 1 unit apart.</summary>
        private void LayoutList()
        {
            var y = 0f;
            for (var i = 0; i < _groups.Count; i++)
            {
                if (i > 0) { y += GroupSpacing; }
                var group = _groups[i];
                var rows = group.Collapsed
                    ? 0
                    : (group.Elements.Count + ElementsPerRow - 1) / ElementsPerRow;
                var height = GroupHeaderHeight + (rows * ElementHeight);
                HangarUiKit.Place(group.Root, 0f, y, ListWidth, height);
                group.Contents.gameObject.SetActive(!group.Collapsed);
                group.Arrow.localRotation = Quaternion.Euler(0f, 0f, group.Collapsed ? 90f : 0f);
                y += height;
            }

            _listContent.sizeDelta = new Vector2(_listContent.sizeDelta.x, y);
        }

        /// <summary>Redraws the slots, the list, the info plate and the footer from the save.</summary>
        private void Refresh()
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                var view = _slots[i];
                var installed = InstalledIn(view.Kind);
                var info = _catalog.Get(installed);
                var filled = info != null;
                view.Empty.gameObject.SetActive(!filled);
                view.Filled.gameObject.SetActive(filled);
                view.Filled.sprite = filled ? _kit.Art(info.SlotIconRegion) : null;
                view.Selection.gameObject.SetActive(view.Kind == _slot);
                view.Badge.gameObject.SetActive(SlotHasUnseen(view.Kind));
            }

            var selectedInstalled = InstalledIn(_slot);
            for (var g = 0; g < _groups.Count; g++)
            {
                var group = _groups[g];
                var groupUnseen = false;
                for (var e = 0; e < group.Elements.Count; e++)
                {
                    var element = group.Elements[e];
                    var id = element.Info.Id;
                    var fits = element.Info.Fits(_slot);
                    var installed = string.Equals(id, selectedInstalled, StringComparison.Ordinal);
                    var picked = string.Equals(id, _selected, StringComparison.Ordinal);
                    var owned = CamouflageService.Owns(_save, id);
                    var unseen = _unseen.Contains(id);
                    groupUnseen |= unseen;
                    element.Alpha.alpha = fits ? 1f : DimmedAlpha;
                    element.Button.interactable = fits;
                    element.Selection.gameObject.SetActive(picked);
                    element.Owned.gameObject.SetActive(owned);
                    PlaceOwnedFrame(element.Owned.rectTransform, picked);
                    element.Installed.gameObject.SetActive(installed);
                    element.Badge.gameObject.SetActive(unseen);
                    element.Price.text = HangarUiKit.FormatNumber(_catalog.Price(_tankName, id));

                    // A bought camouflage has no price; its row keeps its place.
                    element.PriceAlpha.alpha = owned ? 0f : fits ? 1f : DimmedAlpha;
                }

                group.Badge.gameObject.SetActive(groupUnseen);
            }

            var selected = _supported ? _catalog.Get(_selected) : null;
            _info.gameObject.SetActive(selected != null);
            if (selected != null)
            {
                _infoTitle.text = _kit.Str(selected.NameKey);
                _infoText.text = ShopText.Fill(_kit.Str(selected.DescriptionKey), BonusPercent());
            }

            RefreshFooter();
        }

        /// <summary>
        /// The orange selection keeps the tile's edge; an owned camouflage
        /// that is also picked shows its green frame just inside it.
        /// </summary>
        private static void PlaceOwnedFrame(RectTransform frame, bool picked)
        {
            var inset = picked ? SelectionBand : 0f;
            HangarUiKit.Place(frame, inset, inset, TileWidth - (inset * 2f), TileHeight - (inset * 2f));
        }

        private void RefreshFooter()
        {
            var action = CurrentAction;
            var shown = action != CamouflageAction.None;
            var priced = action == CamouflageAction.Buy;
            _apply.gameObject.SetActive(shown);
            _priceIcon.gameObject.SetActive(priced);
            _priceValue.gameObject.SetActive(priced);
            _priceLabel.gameObject.SetActive(priced);
            if (!shown)
            {
                return;
            }

            _applyImage.sprite = _kit.Art(action == CamouflageAction.Remove ? "camo_button_sell" : "camo_button_payment");
            _applyLabel.text = _kit.Str(
                action == CamouflageAction.Remove ? RemoveKey
                : action == CamouflageAction.Install ? InstallKey
                : BuyKey);
            if (!priced)
            {
                return;
            }

            _priceValue.text = HangarUiKit.FormatNumber(_service.ActionCost(_save, _slot, _selected));
            _priceLabel.text = _kit.Str(CostKey);

            // FooterPriceControl: the caption ends 30 units left of the value.
            var right = -PriceValueRight - Mathf.Min(_priceValue.preferredWidth, PriceTextWidth) - PriceLabelGap;
            GodotRect.Apply(
                _priceLabel.rectTransform, 1f, 0.5f, 1f, 0.5f,
                right - PriceTextWidth, -18f, right, 18f, GodotRect.TopLeftPivot);
        }

        private string InstalledIn(CamouflageKind kind)
        {
            if (!_supported)
            {
                return string.Empty;
            }

            var installed = CamouflageService.Installed(_save, kind);
            return _catalog.CanInstall(_tankName, kind, installed) ? installed : string.Empty;
        }

        private bool SlotHasUnseen(CamouflageKind kind)
        {
            if (!_supported || _unseen.Count == 0)
            {
                return false;
            }

            foreach (var info in _catalog.Available(_tankName, kind))
            {
                if (_unseen.Contains(info.Id)) { return true; }
            }

            return false;
        }

        private string BonusPercent()
        {
            return (_catalog.ConcealmentBonus * 100f).ToString("0.##", CultureInfo.InvariantCulture);
        }

        private ElementView Element(string camouflage)
        {
            for (var g = 0; g < _groups.Count; g++)
            {
                var elements = _groups[g].Elements;
                for (var e = 0; e < elements.Count; e++)
                {
                    if (string.Equals(elements[e].Info.Id, camouflage, StringComparison.Ordinal))
                    {
                        return elements[e];
                    }
                }
            }

            return null;
        }

        // ------------------------------------------------------------------
        // Preview.
        // ------------------------------------------------------------------

        /// <summary>
        /// Lays the animated panels out for a preview progress: content to
        /// the left, footer down, info right (fading), the header up and the
        /// preview toggle into the header's place.
        /// </summary>
        private void Place(float progress)
        {
            var headerBottom = HeaderBottom;
            HangarUiKit.LeftStretch(
                _content, -ContentSlide * progress, headerBottom,
                ContentWidth - (ContentSlide * progress), -FooterHeight);
            HangarUiKit.BottomStretch(
                _footer, 0f, -FooterHeight + (FooterHeight * progress), 0f, FooterHeight * progress);
            _info.anchoredPosition = new Vector2(ContentWidth + (InfoSlide * progress), FooterHeight);
            _info.sizeDelta = new Vector2(-ContentWidth, _info.sizeDelta.y);
            _infoAlpha.alpha = 1f - progress;

            var top = Mathf.Lerp(headerBottom + PreviewButtonInset, PreviewButtonTopInPreview, progress);
            GodotRect.Apply(
                (RectTransform)_previewButton.transform, 1f, 0f, 1f, 0f,
                -PreviewButtonInset - PreviewButtonSize, top, -PreviewButtonInset, top + PreviewButtonSize,
                GodotRect.TopLeftPivot);

            var lift = new Vector2(0f, HeaderLift * progress);
            if (Header != null) { Header.anchoredPosition = lift; }
            if (Overlay != null) { Overlay.anchoredPosition = lift; }
        }

        /// <summary>
        /// The stand sits centred right of the content while the panels are
        /// up and in the middle of the screen in the preview.
        /// </summary>
        private void ApplyViewShift(float seconds)
        {
            if (_world == null || _world.Orbit == null)
            {
                return;
            }

            var width = Root != null ? Root.rect.width : 0f;
            if (width <= 1f) { width = UiLayout.ReferenceWidth; }
            _world.Orbit.SetViewShift(_preview ? 0f : ContentWidth / width, seconds);
        }

        // ------------------------------------------------------------------
        // Popups.
        // ------------------------------------------------------------------

        private void OpenPopup(string headerKey, string body, bool simple, Action accept, string okKey)
        {
            ClosePopup();
            var popup = GenericPopup.Create(
                ModalLayer ?? Root, _kit.Atlas, _kit.Lang, _kit.Bold, _kit.Regular,
                headerKey, string.Empty, simple, accept);
            popup.Body.text = body;
            popup.Body.horizontalOverflow = HorizontalWrapMode.Wrap;
            popup.Body.verticalOverflow = VerticalWrapMode.Truncate;
            popup.Body.resizeTextForBestFit = true;
            popup.Body.resizeTextMinSize = MinimumPopupText;
            popup.Body.resizeTextMaxSize = popup.Body.fontSize;
            if (!simple)
            {
                SetCaption(popup.Ok, _kit.Str(okKey));
                SetCaption(popup.Cancel, _kit.Str(ConfirmCancelKey));
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
        }

        private static void SetCaption(Button button, string text)
        {
            var label = button == null ? null : button.transform.Find("label");
            var caption = label == null ? null : label.GetComponent<Text>();
            if (caption != null && !string.IsNullOrEmpty(text))
            {
                caption.text = text;
            }
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

        private void RaiseChanged()
        {
            if (Changed != null)
            {
                Changed();
            }
        }
    }
}
