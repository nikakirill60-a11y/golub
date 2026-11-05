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
    /// The account warehouse: spare equipment and consumables the player
    /// owns but has not mounted. Two shelves (equipment / consumables) of
    /// item tiles with stock counts; buying stocks the shelf at the level
    /// price, selling pays half back, and LoadoutService mounts straight
    /// from the shelf for free. Prices follow the selected tank's level,
    /// like everywhere else in the hangar economy.
    /// </summary>
    public sealed class InventoryScreen
    {
        public const string TitleKey = "inventory:title";
        public const string TabEquipmentKey = "inventory:tab_equipment";
        public const string TabConsumablesKey = "inventory:tab_consumables";
        public const string BuyKey = "inventory:buy";
        public const string SellKey = "inventory:sell";
        public const string EmptyKey = "inventory:empty";

        private const float TileWidth = 160f;
        private const float TileHeight = 168f;
        private const float TileGap = 14f;
        private const float GridX = 32f;
        private const float GridY = 150f;
        private const float DetailX = 580f;

        private sealed class Tile
        {
            public InventoryItem Item;
            public GameObject Root;
            public Text Count;
        }

        private readonly HangarUiKit _kit;
        private readonly TanksDatabase _tanks;
        private readonly AccountData _account;
        private readonly InventoryService _service;
        private readonly bool _modern;
        private readonly List<Tile> _tiles = new List<Tile>();

        private RectTransform _root;
        private Text _credits;
        private Text _detailName;
        private Text _detailDesc;
        private Text _detailCount;
        private Text _buyCaption;
        private Text _sellCaption;
        private Text _status;
        private Text _empty;
        private Button _buy;
        private Button _sell;

        private InventoryCategory _category = InventoryCategory.Equipment;
        private string _selected = "rammer";

        public InventoryScreen(
            HangarUiKit kit, TanksDatabase tanks, AccountData account, bool modern)
        {
            if (kit == null) { throw new ArgumentNullException("kit"); }
            if (account == null) { throw new ArgumentNullException("account"); }
            _kit = kit;
            _tanks = tanks;
            _account = account;
            _modern = modern;
            _service = new InventoryService(account);
        }

        /// <summary>btn_menu, back to the hangar.</summary>
        public Action HomePressed;

        /// <summary>Raised after a buy or sell changed the save.</summary>
        public Action Changed;

        public RectTransform Root { get { return _root; } }

        /// <summary>The hangar layer for the back button.</summary>
        public RectTransform Overlay { get; set; }

        public InventoryCategory Category { get { return _category; } }

        public string SelectedItem { get { return _selected; } }

        /// <summary>Stock of the selected item, for tests and the detail pane.</summary>
        public int SelectedCount { get { return _service.Count(_selected); } }

        // ------------------------------------------------------------------
        // Build.
        // ------------------------------------------------------------------

        public RectTransform Build(RectTransform parent)
        {
            _root = UiFactory.Node("inventory", parent);
            GodotRect.FullRect(_root);

            var blur = _kit.Picture(
                "blur", _root, null, 0f, 0f,
                UiLayout.ReferenceWidth, UiLayout.ReferenceHeight);
            HangarUiKit.Stretch(blur.rectTransform, 0f, 6f, 0f, 6f);
            blur.color = HangarUiKit.ScreenTint;
            blur.raycastTarget = false;

            var top = Overlay ?? _root;
            var menu = _kit.BackButton(top, _modern, 50f);
            _kit.Bind(menu, RaiseHome, AudioService.SoundEnter);

            _kit.Label(
                "title", _root, 30, TextAnchor.MiddleCenter,
                _kit.Str(TitleKey), _kit.Bold, Color.white,
                200f, 8f, 800f, 48f);
            _credits = _kit.Label(
                "credits", _root, 24, TextAnchor.MiddleRight,
                string.Empty, _kit.Bold, HangarUiKit.PremiumGold,
                UiLayout.ReferenceWidth - 360f, 14f, 320f, 36f);

            BuildTab(InventoryCategory.Equipment, TabEquipmentKey, 32f);
            BuildTab(InventoryCategory.Consumable, TabConsumablesKey, 290f);

            _empty = _kit.Label(
                "empty", _root, 24, TextAnchor.MiddleCenter,
                _kit.Str(EmptyKey), _kit.Regular, HangarUiKit.Caption,
                GridX, GridY + 200f, 508f, 40f);

            BuildTiles();
            BuildDetail();
            Refresh();
            return _root;
        }

        private void BuildTab(InventoryCategory category, string key, float x)
        {
            var button = _kit.StyleButton(
                "tab_" + category, _root, "button_small_generic",
                x, 92f, 240f, 48f);
            var caption = _kit.Label(
                "tab_caption", button.transform as RectTransform, 22,
                TextAnchor.MiddleCenter, _kit.Str(key), _kit.Bold, Color.white,
                0f, 0f, 240f, 48f);
            caption.raycastTarget = false;
            _kit.Bind(button, () => ShowCategory(category), AudioService.SoundEnter);
        }

        private void BuildTiles()
        {
            var items = InventoryCatalog.All;
            var perCategory = new Dictionary<InventoryCategory, int>();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                int index;
                perCategory.TryGetValue(item.Category, out index);
                perCategory[item.Category] = index + 1;

                var x = GridX + (index % 3) * (TileWidth + TileGap);
                var y = GridY + (index / 3) * (TileHeight + TileGap);
                var button = _kit.StyleButton(
                    "tile_" + item.Id, _root, "button_small_generic",
                    x, y, TileWidth, TileHeight);
                var tileRoot = button.gameObject;

                var icon = _kit.Centered(
                    "icon", tileRoot.transform as RectTransform,
                    item.IconRegion, 28f, 10f, 104f, 92f);
                icon.raycastTarget = false;

                var name = _kit.Label(
                    "name", tileRoot.transform as RectTransform, 17,
                    TextAnchor.MiddleCenter, _kit.Str(item.NameKey),
                    _kit.Regular, Color.white, 4f, 102f, TileWidth - 8f, 40f);
                name.raycastTarget = false;

                var count = _kit.Label(
                    "count", tileRoot.transform as RectTransform, 20,
                    TextAnchor.MiddleCenter, string.Empty,
                    _kit.Bold, HangarUiKit.PremiumGold,
                    4f, 138f, TileWidth - 8f, 26f);
                count.raycastTarget = false;

                var captured = item;
                _kit.Bind(
                    button, () => Select(captured.Id), AudioService.SoundEnter);

                _tiles.Add(new Tile { Item = item, Root = tileRoot, Count = count });
            }
        }

        private void BuildDetail()
        {
            _detailName = _kit.Label(
                "detail_name", _root, 28, TextAnchor.MiddleLeft,
                string.Empty, _kit.Bold, Color.white,
                DetailX, GridY, 700f, 40f);
            _detailDesc = _kit.Label(
                "detail_desc", _root, 20, TextAnchor.UpperLeft,
                string.Empty, _kit.Regular, HangarUiKit.Caption,
                DetailX, GridY + 48f, 700f, 110f);
            _detailCount = _kit.Label(
                "detail_count", _root, 22, TextAnchor.MiddleLeft,
                string.Empty, _kit.Regular, Color.white,
                DetailX, GridY + 158f, 700f, 32f);

            _buy = _kit.StyleButton(
                "btn_buy_inventory", _root, "button_small_green",
                DetailX, GridY + 210f, 300f, 60f);
            _buyCaption = _kit.Label(
                "buy_caption", _buy.transform as RectTransform, 22,
                TextAnchor.MiddleCenter, string.Empty, _kit.Bold, Color.white,
                0f, 0f, 300f, 60f);
            _buyCaption.raycastTarget = false;
            _kit.Bind(_buy, BuySelected, AudioService.SoundEnter);

            _sell = _kit.StyleButton(
                "btn_sell_inventory", _root, "button_small_generic",
                DetailX + 320f, GridY + 210f, 300f, 60f);
            _sellCaption = _kit.Label(
                "sell_caption", _sell.transform as RectTransform, 22,
                TextAnchor.MiddleCenter, string.Empty, _kit.Bold, Color.white,
                0f, 0f, 300f, 60f);
            _sellCaption.raycastTarget = false;
            _kit.Bind(_sell, SellSelected, AudioService.SoundEnter);

            _status = _kit.Label(
                "status", _root, 20, TextAnchor.MiddleLeft,
                string.Empty, _kit.Regular, new Color(1f, 0.45f, 0.4f),
                DetailX, GridY + 284f, 700f, 32f);
        }

        // ------------------------------------------------------------------
        // State.
        // ------------------------------------------------------------------

        public void Show()
        {
            _root.gameObject.SetActive(true);
            Refresh();
        }

        public void Hide()
        {
            _root.gameObject.SetActive(false);
        }

        public void ShowCategory(InventoryCategory category)
        {
            _category = category;
            var items = InventoryCatalog.For(category);
            if (items.Count > 0) { _selected = items[0].Id; }
            Refresh();
        }

        public void Select(string itemId)
        {
            var item = InventoryCatalog.Get(itemId);
            if (item == null) { return; }
            _selected = item.Id;
            _category = item.Category;
            Refresh();
        }

        public void BuySelected()
        {
            var result = _service.Buy(_selected, SelectedLevel(), 1);
            AfterTrade(result);
        }

        public void SellSelected()
        {
            var result = _service.Sell(_selected, SelectedLevel(), 1);
            AfterTrade(result);
        }

        private void AfterTrade(TankActionResult result)
        {
            _status.text = result.Ok ? string.Empty : _kit.Str(result.ErrorKey);
            if (result.Ok && Changed != null) { Changed(); }
            Refresh();
        }

        private int SelectedLevel()
        {
            var info = _tanks == null ? null : _tanks.Get(_account.SelectedTankName);
            return info == null ? 1 : info.Level;
        }

        private void Refresh()
        {
            if (_root == null) { return; }
            _credits.text = _account.Credits.ToString();

            var shelfTotal = 0;
            for (var i = 0; i < _tiles.Count; i++)
            {
                var tile = _tiles[i];
                var owned = _service.Count(tile.Item.Id);
                tile.Root.SetActive(tile.Item.Category == _category);
                tile.Count.text = owned > 0 ? "×" + owned : string.Empty;
                if (tile.Item.Category == _category) { shelfTotal += owned; }
            }

            _empty.gameObject.SetActive(shelfTotal <= 0);

            var item = InventoryCatalog.Get(_selected);
            if (item == null) { return; }
            var level = SelectedLevel();
            _detailName.text = _kit.Str(item.NameKey);
            _detailDesc.text = _kit.Str(item.DescriptionKey);
            _detailCount.text = "×" + _service.Count(item.Id);
            _buyCaption.text = _kit.Str(BuyKey) + "  " + item.Cost(level);
            _sellCaption.text = _kit.Str(SellKey) + "  " + item.SellCost(level);
            _sell.interactable = _service.Count(item.Id) > 0;
        }

        private void RaiseHome()
        {
            if (HomePressed != null) { HomePressed(); }
        }
    }
}
