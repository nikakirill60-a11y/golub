using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Mods;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Mods;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The mod installer, opened from the hangar menu next to settings.
    ///
    /// The Godot build had no installer at all: a mod was a folder somebody
    /// dropped next to the game. The port keeps that folder (see <see
    /// cref="ModRuntime.RootPath"/>) and puts a screen in front of it, so the
    /// player picks the .rar / .zip / .7z / .tar the mod was shared as and the
    /// game unpacks it into that same layout.
    ///
    /// The screen is built out of the equipment screen's parts
    /// (<see cref="LoadoutScreen"/>): the same tinted backdrop, the same
    /// button_small_generic back button with icon_back, the same bg_main panel
    /// and the same row of header tabs, so it reads like one more hangar
    /// screen instead of a foreign panel. Every region used here exists in
    /// atlas_regions.json; a missing region renders as a white quad.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ModsScreen : MonoBehaviour
    {
        public const int TabInstalled = 0;
        public const int TabAvailable = 1;

        private const string ButtonRegion = "button_small_generic";
        private const string ConfirmRegion = "button_small_green";
        private const string DangerRegion = "button_red";
        private const string BackIconRegion = "icon_back";

        // Metrics shared with LoadoutScreen (canvas 1386.67 x 733.33).
        private const float BackX = 5f;
        private const float BackY = 4f;
        private const float BackWidth = 94f;
        private const float BackHeight = 77f;
        private const float BackIconMaxWidth = 50f;

        private const float TitleX = 128f;
        private const float TitleY = 29f;
        private const float TitleWidth = 600f;
        private const float TitleHeight = 40f;
        private const int TitleFontSize = 30;

        private const float BackdropSide = -5f;
        private const float BackdropTop = 90f;

        private const float TabTop = 92f;
        private const float TabHeight = 66f;
        private const float TabSeparation = 4f;
        private const int TabFontSize = 26;

        private const float ListTop = 166f;
        private const float ListSide = 8f;
        private const float FooterHeight = 52f;
        private const float ListPadding = 8f;

        private const float RowHeight = 92f;
        private const float RowGap = 6f;
        private const float RowPadding = 18f;
        private const int RowFontSize = 22;
        private const int NoteFontSize = 16;
        private const float ActionWidth = 190f;
        private const float ActionHeight = 62f;
        private const float ActionGap = 8f;
        private const int ActionFontSize = 20;
        private const float ThumbWidth = 120f;
        private const float ThumbHeight = 72f;
        private const float ThumbGap = 14f;

        /// <summary>LoadoutScreen.PanelFill, screens/tank_equipment/bg_main.</summary>
        private static readonly Color PanelFill =
            new Color(0.06310915f, 0.10855677f, 0.13752648f, 0.658f);

        /// <summary>LoadoutScreen.DescFill, the bg_desc / bg_slots ColorRect.</summary>
        private static readonly Color ListFill = new Color(0f, 0f, 0f, 0.3882353f);

        private static readonly Color RowFill = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color RowFillDisabled = new Color(0f, 0f, 0f, 0.25f);

        /// <summary>ui_equipment_list.col_greenish.</summary>
        private static readonly Color Greenish = new Color32(0xA3, 0xE3, 0x9B, 0xFF);

        private static readonly Color Muted = new Color(0.62f, 0.66f, 0.68f, 1f);

        private HangarUiKit _kit;
        private Action _closed;
        private ModLibrary _library;
        private Text _status;
        private Text _footer;
        private Button _installedTab;
        private Button _availableTab;
        private Text _installedTabLabel;
        private Text _availableTabLabel;
        private RectTransform _installed;
        private RectTransform _archives;
        private ScrollRect _installedScroll;
        private ScrollRect _archivesScroll;
        private string _statusKey = string.Empty;
        private bool _statusError;
        private int _tab = TabInstalled;
        private readonly Dictionary<string, Sprite> _icons =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        public RectTransform Root { get; private set; }

        public Button Back { get; private set; }

        public int Tab
        {
            get { return _tab; }
        }

        /// <summary>The message in the footer, exposed for the tests.</summary>
        public string Status
        {
            get { return _status == null ? string.Empty : _status.text; }
        }

        public static ModsScreen Open(
            Transform parent,
            int sortingOrder,
            bool mobile,
            Action closed)
        {
            var go = new GameObject("ModsScreen");
            go.transform.SetParent(parent, false);
            var screen = go.AddComponent<ModsScreen>();
            screen._closed = closed;
            screen.Build(mobile, sortingOrder);
            return screen;
        }

        public void Close()
        {
            var closed = _closed;
            _closed = null;
            Destroy(gameObject);
            if (closed != null)
            {
                closed();
            }
        }

        private void Update()
        {
            if (Keyboard.current != null
                && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                Close();
            }
        }

        private void Build(bool mobile, int sortingOrder)
        {
            var atlas = MenuStyle.ApplyForMenus(BattleFlowUi.LoadAtlas());
            var lang = BattleFlowUi.LoadLanguage();
            _kit = new HangarUiKit(
                atlas,
                lang,
                BlitzResources.LoadFont(BlitzResources.RegularFontName),
                BlitzResources.LoadFont(BlitzResources.BoldFontName),
                BlitzBootstrap.Current == null ? null : BlitzBootstrap.Current.Audio);

            _library = ModRuntime.Library;
            _library.Refresh();

            var canvas = BattleFlowUi.CreateCanvas(transform, false, mobile, sortingOrder);
            Root = UiFactory.Node("ui_mods", canvas);
            GodotRect.FullRect(Root);

            BuildBackdrop();
            BuildHeader();
            BuildTabs();
            BuildLists();
            BuildFooter();
            ShowTab(TabInstalled);
        }

        private void BuildBackdrop()
        {
            var blur = UiFactory.Picture("blur", Root, null);
            GodotRect.FullRect(blur.rectTransform);
            blur.color = HangarUiKit.ScreenTint;
            blur.raycastTarget = true;

            var backdrop = UiFactory.Picture("bg_main", Root, null);
            HangarUiKit.Stretch(
                backdrop.rectTransform, BackdropSide, BackdropTop, -BackdropSide, 0f);
            backdrop.color = PanelFill;
            backdrop.raycastTarget = false;
        }

        private void BuildHeader()
        {
            Back = _kit.StyleButton(
                "btn_menu", Root, ButtonRegion, BackX, BackY, BackWidth, BackHeight);
            _kit.AddIcon(Back, BackIconRegion, BackIconMaxWidth);
            _kit.Bind(Back, Close, AudioService.SoundEnter);

            _kit.Label(
                "tank_name", Root, TitleFontSize, TextAnchor.MiddleLeft,
                _kit.Str("MODS"), _kit.Bold, UiTheme.Label,
                TitleX, TitleY, TitleWidth, TitleHeight);
        }

        /// <summary>Two tabs, each owning half of the live canvas width.</summary>
        private void BuildTabs()
        {
            _installedTab = MakeTab(0, "btn_installed", out _installedTabLabel);
            _kit.Bind(_installedTab, () => ShowTab(TabInstalled), AudioService.SoundEnter);

            _availableTab = MakeTab(1, "btn_available", out _availableTabLabel);
            _kit.Bind(_availableTab, () => ShowTab(TabAvailable), AudioService.SoundEnter);
        }

        private Button MakeTab(int index, string name, out Text label)
        {
            const int count = 2;
            const float half = TabSeparation * 0.5f;
            var button = _kit.StyleButton(name, Root, ButtonRegion, 0f, TabTop, 100f, TabHeight);
            GodotRect.Apply(
                (RectTransform)button.transform,
                index / (float)count, 0f, (index + 1) / (float)count, 0f,
                index == 0 ? 0f : half, TabTop,
                index == count - 1 ? 0f : -half, TabTop + TabHeight,
                GodotRect.TopLeftPivot);

            label = _kit.Label(
                "label", (RectTransform)button.transform, TabFontSize,
                TextAnchor.MiddleCenter, string.Empty, _kit.Bold, UiTheme.ButtonFont,
                0f, 0f, 100f, TabHeight);
            HangarUiKit.Stretch(label.rectTransform, 0f, 0f, 0f, 0f);
            return button;
        }

        private void BuildLists()
        {
            _installed = List("installed", out _installedScroll);
            _archives = List("archives", out _archivesScroll);
        }

        private RectTransform List(string name, out ScrollRect scroll)
        {
            var page = UiFactory.Node(name, Root);
            HangarUiKit.Stretch(page, ListSide, ListTop, -ListSide, -FooterHeight);

            var fill = page.gameObject.AddComponent<Image>();
            fill.color = ListFill;
            fill.raycastTarget = true;

            var viewport = UiFactory.Node("viewport", page);
            HangarUiKit.Stretch(viewport, ListPadding, ListPadding, -ListPadding, -ListPadding);
            viewport.gameObject.AddComponent<RectMask2D>();
            var catcher = viewport.gameObject.AddComponent<Image>();
            catcher.color = Color.clear;
            catcher.raycastTarget = true;

            var content = UiFactory.Node("VBoxContainer", viewport);
            content.pivot = GodotRect.TopLeftPivot;
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;

            scroll = page.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            return content;
        }

        /// <summary>Mods folder on the left, the last action's result on the right.</summary>
        private void BuildFooter()
        {
            _footer = _kit.Label(
                "path", Root, NoteFontSize, TextAnchor.MiddleLeft,
                ModRuntime.RootPath, _kit.Regular, Muted, 0f, 0f, 100f, FooterHeight);
            GodotRect.Apply(
                _footer.rectTransform, 0f, 1f, 0.5f, 1f,
                ListSide + RowPadding, -FooterHeight, 0f, 0f,
                GodotRect.TopLeftPivot);

            _status = _kit.Label(
                "status", Root, NoteFontSize, TextAnchor.MiddleRight,
                string.Empty, _kit.Bold, Greenish, 0f, 0f, 100f, FooterHeight);
            GodotRect.Apply(
                _status.rectTransform, 0.5f, 1f, 1f, 1f,
                0f, -FooterHeight, -(ListSide + RowPadding), 0f,
                GodotRect.TopLeftPivot);
        }

        public void ShowTab(int tab)
        {
            _tab = tab;
            _installedScroll.gameObject.SetActive(tab == TabInstalled);
            _archivesScroll.gameObject.SetActive(tab == TabAvailable);
            SetTabPressed(_installedTab, _installedTabLabel, tab == TabInstalled);
            SetTabPressed(_availableTab, _availableTabLabel, tab == TabAvailable);
            Populate();
        }

        private static void SetTabPressed(Button tab, Text label, bool pressed)
        {
            label.color = pressed ? UiTheme.ButtonFontPressed : UiTheme.ButtonFont;
            var state = GodotButtonState.Of(tab);
            if (state == null)
            {
                return;
            }

            state.ToggleMode = true;
            state.Latched = pressed;
        }

        /// <summary>Rebuilds both lists. Called on open and after every change.</summary>
        public void Populate()
        {
            Clear(_installed);
            Clear(_archives);

            var mods = _library.Mods;
            var top = 0f;
            if (mods.Count == 0)
            {
                top = Empty(_installed, _kit.Str("MODS_NONE_INSTALLED"));
            }

            for (var i = 0; i < mods.Count; i++)
            {
                top = InstalledRow(mods[i], top);
            }

            _installed.sizeDelta = new Vector2(0f, top);

            var archives = ModRuntime.FindArchives();
            var archiveTop = 0f;
            if (archives.Count == 0)
            {
                archiveTop = Empty(_archives, _kit.Str("MODS_NO_ARCHIVES"));
            }

            for (var i = 0; i < archives.Count; i++)
            {
                archiveTop = ArchiveRow(archives[i], archiveTop);
            }

            _archives.sizeDelta = new Vector2(0f, archiveTop);

            _installedTabLabel.text = _kit.Str("MODS_INSTALLED") + "  " + mods.Count;
            _availableTabLabel.text = _kit.Str("MODS_AVAILABLE") + "  " + archives.Count;

            _status.text = string.IsNullOrEmpty(_statusKey) ? string.Empty : _kit.Str(_statusKey);
            _status.color = _statusError ? UiTheme.ErrorText : Greenish;
        }

        private float InstalledRow(InstalledMod mod, float top)
        {
            var row = Row(_installed, mod.Id, top, mod.Enabled);
            var actions = (ActionWidth * 2f) + ActionGap + (RowPadding * 2f);

            Thumb(row, ModIcon(mod), mod.Enabled);
            RowText(row, RowPadding + ThumbWidth + ThumbGap,
                mod.DisplayName, mod.Enabled ? UiTheme.Label : Muted, actions,
                mod.TankCount + " " + _kit.Str("MODS_TANKS") + "   \u2022   "
                + _kit.Str(mod.Enabled ? "ON" : "OFF")
                + (mod.Portable ? "   \u2022   " + _kit.Str("MODS_PORTABLE_INFO") : string.Empty),
                mod.Enabled ? Greenish : Muted);

            var toggle = ActionButton(
                row, "btn_toggle", mod.Enabled ? ButtonRegion : ConfirmRegion,
                _kit.Str(mod.Enabled ? "MODS_DISABLE" : "MODS_ENABLE"), 1);
            var toggled = mod;
            _kit.Bind(
                toggle,
                () =>
                {
                    _library.SetEnabled(toggled.Id, !toggled.Enabled);
                    SetStatus("MODS_RESTART_HINT", false);
                },
                AudioService.SoundEnter);

            var remove = ActionButton(row, "btn_remove", DangerRegion, _kit.Str("MODS_REMOVE"), 0);
            var removed = mod;
            _kit.Bind(
                remove,
                () =>
                {
                    _library.Remove(removed.Id);
                    SetStatus("MODS_RESTART_HINT", false);
                },
                AudioService.SoundEnter);

            return top + RowHeight + RowGap;
        }

        private float ArchiveRow(string path, float top)
        {
            var row = Row(_archives, Path.GetFileName(path), top, true);
            var actions = ActionWidth + (RowPadding * 2f);

            RowText(row, RowPadding, Path.GetFileName(path), UiTheme.Label, actions, Folder(path), Muted);

            var install = ActionButton(row, "btn_install", ConfirmRegion, _kit.Str("MODS_INSTALL"), 0);
            var archive = path;
            _kit.Bind(install, () => Install(archive), AudioService.SoundEnter);
            return top + RowHeight + RowGap;
        }

        /// <summary>
        /// Unpacks one archive. A RAR or 7z that stores its files compressed
        /// cannot be read without a decoder the game does not ship, so the
        /// failure is reported as a plain "repack it as zip" message instead
        /// of a silent no-op.
        /// </summary>
        public void Install(string archivePath)
        {
            var result = _library.Install(archivePath);
            if (result.Ok)
            {
                _tab = TabInstalled;
                SetStatus("MODS_INSTALLED_OK", false);
                ShowTab(TabInstalled);
                return;
            }

            SetStatus(result.ErrorKey, true);
        }

        private void SetStatus(string key, bool error)
        {
            _statusKey = key ?? string.Empty;
            _statusError = error;
            Populate();
        }

        private RectTransform Row(RectTransform parent, string name, float top, bool active)
        {
            var row = UiFactory.Node(name, parent);
            GodotRect.Apply(
                row, 0f, 0f, 1f, 0f, 0f, top, 0f, top + RowHeight, GodotRect.TopLeftPivot);
            var background = row.gameObject.AddComponent<Image>();
            background.color = active ? RowFill : RowFillDisabled;
            background.raycastTarget = false;
            return row;
        }

        private void RowText(
            RectTransform row, float left, string title, Color titleColor, float reserved,
            string note, Color noteColor)
        {
            var name = _kit.Label(
                "name", row, RowFontSize, TextAnchor.LowerLeft, title,
                _kit.Bold, titleColor, 0f, 0f, 100f, 30f);
            GodotRect.Apply(
                name.rectTransform, 0f, 0f, 1f, 0f,
                left, 14f, -reserved, 46f, GodotRect.TopLeftPivot);
            name.horizontalOverflow = HorizontalWrapMode.Wrap;

            var detail = _kit.Label(
                "note", row, NoteFontSize, TextAnchor.UpperLeft, note,
                _kit.Regular, noteColor, 0f, 0f, 100f, 24f);
            GodotRect.Apply(
                detail.rectTransform, 0f, 0f, 1f, 0f,
                left, 52f, -reserved, 76f, GodotRect.TopLeftPivot);
        }

        /// <summary>
        /// The mod's first tank icon, or null. A mod without an icon (or
        /// without a card at all) is shown with a "?" in the same box.
        /// </summary>
        private Sprite ModIcon(InstalledMod mod)
        {
            for (var i = 0; i < mod.Tanks.Count; i++)
            {
                var path = mod.Tanks[i].IconPath;
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                Sprite sprite;
                if (!_icons.TryGetValue(path, out sprite))
                {
                    sprite = ModRuntime.LoadSprite(path);
                    _icons[path] = sprite;
                }

                if (sprite != null)
                {
                    return sprite;
                }
            }

            return null;
        }

        private void Thumb(RectTransform row, Sprite sprite, bool active)
        {
            var y = (RowHeight - ThumbHeight) * 0.5f;
            var box = UiFactory.Picture("thumb", row, null);
            GodotRect.Apply(
                box.rectTransform, 0f, 0f, 0f, 0f,
                RowPadding, y, RowPadding + ThumbWidth, y + ThumbHeight,
                GodotRect.TopLeftPivot);
            box.color = ListFill;
            box.raycastTarget = false;

            if (sprite != null)
            {
                var icon = UiFactory.Picture("icon", box.rectTransform, sprite);
                GodotRect.FullRect(icon.rectTransform);
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                icon.color = active ? Color.white : new Color(1f, 1f, 1f, 0.45f);
                return;
            }

            var unknown = _kit.Label(
                "unknown", box.rectTransform, 44, TextAnchor.MiddleCenter, "?",
                _kit.Bold, HangarUiKit.TankNameMod, 0f, 0f, ThumbWidth, ThumbHeight);
            HangarUiKit.Stretch(unknown.rectTransform, 0f, 0f, 0f, 0f);
        }

        /// <summary>A right-aligned row button; slot 0 is the rightmost.</summary>
        private Button ActionButton(RectTransform row, string name, string region, string text, int slot)
        {
            var button = _kit.StyleButton(name, row, region, 0f, 0f, ActionWidth, ActionHeight);
            var right = RowPadding + (slot * (ActionWidth + ActionGap));
            var y = (RowHeight - ActionHeight) * 0.5f;
            GodotRect.Apply(
                (RectTransform)button.transform, 1f, 0f, 1f, 0f,
                -right - ActionWidth, y, -right, y + ActionHeight,
                GodotRect.TopLeftPivot);

            var label = _kit.Label(
                "label", (RectTransform)button.transform, ActionFontSize,
                TextAnchor.MiddleCenter, text, _kit.Bold, UiTheme.ButtonFont,
                0f, 0f, ActionWidth, ActionHeight);
            HangarUiKit.Stretch(label.rectTransform, 0f, 0f, 0f, 0f);
            return button;
        }

        /// <summary>Centred placeholder for an empty list.</summary>
        private float Empty(RectTransform parent, string text)
        {
            var label = _kit.Label(
                "empty", parent, RowFontSize, TextAnchor.MiddleCenter, text,
                _kit.Regular, Muted, 0f, 0f, 100f, RowHeight * 2f);
            GodotRect.Apply(
                label.rectTransform, 0f, 0f, 1f, 0f,
                RowPadding, RowHeight, -RowPadding, RowHeight * 3f,
                GodotRect.TopLeftPivot);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            return RowHeight * 3f;
        }

        private static string Folder(string path)
        {
            var folder = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(folder) ? string.Empty : folder;
        }

        private static void Clear(RectTransform parent)
        {
            if (parent == null)
            {
                return;
            }

            var children = new List<GameObject>();
            for (var i = 0; i < parent.childCount; i++)
            {
                children.Add(parent.GetChild(i).gameObject);
            }

            for (var i = 0; i < children.Count; i++)
            {
                DestroyImmediate(children[i]);
            }
        }
    }
}
