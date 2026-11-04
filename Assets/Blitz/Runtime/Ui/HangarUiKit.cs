using System;
using System.Globalization;
using System.Text;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Widget helpers shared by the hangar sub-screens ported in stage 4.4/4.5
    /// (tech tree, tank modules).
    ///
    /// <see cref="HangarScreen"/> keeps its own private copies of these helpers
    /// because its rects were captured before this kit existed; the two new
    /// screens share one implementation instead of repeating the Godot rect
    /// maths and the atlas region naming rules three times.
    ///
    /// Every rect is expressed exactly like the Godot source: a top-left
    /// anchored x/y/w/h inside the parent Control's own space.
    /// </summary>
    public sealed class HangarUiKit
    {
        /// <summary>ui_hangar.tscn caption grey (Godot theme LabelSmall).</summary>
        public static readonly Color Caption =
            new Color(0.5662578f, 0.61404586f, 0.65724987f, 1f);

        /// <summary>Premium gold used for premium tank names.</summary>
        public static readonly Color PremiumGold =
            new Color(0.702118f, 0.601171f, 0.162514f, 1f);

        /// <summary>db_tanks.gd COLOR_TANK_PREM.</summary>
        public static readonly Color TankNamePremium =
            new Color(1f, 0.777f, 0.103f, 1f);

        /// <summary>db_tanks.gd COLOR_TANK_COLLECTOR.</summary>
        public static readonly Color TankNameCollector =
            new Color(0.309f, 0.765f, 0.969f, 1f);

        /// <summary>The mod class: the collector treatment in light green.</summary>
        public static readonly Color TankNameMod =
            new Color(0.639f, 0.890f, 0.608f, 1f);

        /// <summary>The premium filter toggle's badge: lobbyCommon_Out's gold wreath.</summary>
        public const string PremiumBadgeRegion = "hangar_premium";

        /// <summary>
        /// The premium wreath in <see cref="UiTheme.Label"/>,
        /// <see cref="TankNameCollector"/> and <see cref="TankNameMod"/>
        /// (Tools/port_hangar_status_badges.py), so the four status toggles of
        /// a filter share one size and one look.
        /// </summary>
        public const string RegularBadgeRegion = "hangar_regular_badge";

        public const string CollectorBadgeRegion = "hangar_collector_badge";

        public const string ModBadgeRegion = "hangar_mod_badge";

        /// <summary>
        /// Port of <c>TanksDataBase.get_tank_name_color</c>. Blitz paints the
        /// vehicle status onto the name: light blue for collector vehicles,
        /// gold for premiums, white for tech tree tanks. Collector wins over
        /// premium, exactly like the Godot branch order. Both carousel_tank
        /// .tscn and ui_tech_tree_tank.tscn call this one function.
        /// </summary>
        public static Color TankNameColor(TankInfo info)
        {
            if (info == null)
            {
                return UiTheme.Label;
            }

            if (info.Mod)
            {
                return TankNameMod;
            }

            if (info.Collector)
            {
                return TankNameCollector;
            }

            return info.Premium ? TankNamePremium : UiTheme.Label;
        }

        /// <summary>prems_panel StyleBoxFlat bg_color.</summary>
        public static readonly Color PremiumPanel =
            new Color(0.95748645f, 0.6037999f, 0f, 0.365f);

        /// <summary>
        /// How much of the live hangar <see cref="ScreenTint"/> hides.
        /// ui_blur.gdshader writes alpha 1, but what it writes is a blurred and
        /// brightened copy of the hangar, so in Godot the hangar still reads
        /// through the backdrop. A near-opaque flat quad removed that entirely
        /// and both screens looked like solid panels. This is the one number to
        /// turn if they should sit closer to, or further from, the hangar.
        /// </summary>
        public const float ScreenTintAlpha = 0.65f;

        /// <summary>
        /// The colour blur.tscn multiplies the blurred screen by. Kept for
        /// reference only: <see cref="ScreenTint"/> is derived from it and is
        /// deliberately not equal to it.
        /// </summary>
        public static readonly Color BlurSourceColor =
            new Color(0.32740578f, 0.41763112f, 0.4824441f, 1f);

        /// <summary>
        /// blur.tscn, the backdrop screens/tech_tree and screens/tank_modules
        /// both put behind their UI: a full-rect ColorRect running
        /// ui_blur.gdshader, which samples the screen with a 2 px blur and a
        /// small RGB shift and writes pow(screen, 0.5) *
        /// <see cref="BlurSourceColor"/>.
        ///
        /// That needs a screen grab this UI pass does not set up, so the
        /// backdrop is an alpha blend instead: lerp(screen, ScreenTint,
        /// ScreenTintAlpha). Fitting that straight line to the shader's curve
        /// across the hangar's brightness range gives this colour and alpha,
        /// which lands within roughly 0.01 per channel of what Godot draws for
        /// a mid-grey hangar while keeping the hangar visible. Multiplying by
        /// BlurSourceColor directly would crush the dark areas, because that
        /// colour is only correct after the shader's pow(screen, 0.5) lift.
        /// </summary>
        public static readonly Color ScreenTint =
            new Color(0.114f, 0.191f, 0.245f, ScreenTintAlpha);

        private static readonly Color ShadowColor = new Color(0f, 0f, 0f, 0.8f);

        public HangarUiKit(
            AtlasRegionTable atlas,
            LocalizationTable lang,
            Font regular,
            Font bold,
            AudioService audio)
        {
            Atlas = atlas;
            Lang = lang;
            Regular = regular;
            Bold = bold;
            Audio = audio;
        }

        public AtlasRegionTable Atlas { get; private set; }

        public LocalizationTable Lang { get; private set; }

        public Font Regular { get; private set; }

        public Font Bold { get; private set; }

        public AudioService Audio { get; private set; }

        // ------------------------------------------------------------------
        // Atlas + localization lookups.
        // ------------------------------------------------------------------

        /// <summary>Atlas region sprite, or null when the region is absent.</summary>
        public Sprite Art(string region)
        {
            if (string.IsNullOrEmpty(region) || Atlas == null)
            {
                return null;
            }

            return Atlas.Get(region);
        }

        /// <summary>Localized string; falls back to the key like Godot's tr().</summary>
        public string Str(string key)
        {
            if (Lang == null || string.IsNullOrEmpty(key))
            {
                return key;
            }

            return Lang.Get(key);
        }

        // ------------------------------------------------------------------
        // Widgets.
        // ------------------------------------------------------------------

        public RectTransform Node(
            string name, RectTransform parent, float x, float y, float w, float h)
        {
            var rt = UiFactory.Node(name, parent);
            Place(rt, x, y, w, h);
            return rt;
        }

        /// <summary>Godot TextureRect with the default stretch mode (scaled).</summary>
        public Image Picture(
            string name,
            RectTransform parent,
            string region,
            float x,
            float y,
            float w,
            float h)
        {
            var image = UiFactory.Picture(name, parent, Art(region));
            image.type = image.sprite != null && image.sprite.border.sqrMagnitude > 0f
                ? Image.Type.Sliced
                : Image.Type.Simple;
            Place(image.rectTransform, x, y, w, h);
            return image;
        }

        /// <summary>
        /// Godot TextureRect STRETCH_KEEP_CENTERED (stretch_mode = 3): the
        /// texture keeps its native size and is centred inside the rect. The
        /// optional scale reproduces a Control scale applied on top of it.
        /// </summary>
        public Image Centered(
            string name,
            RectTransform parent,
            string region,
            float x,
            float y,
            float w,
            float h,
            float scale = 1f)
        {
            var sprite = Art(region);
            var image = UiFactory.Picture(name, parent, sprite);
            var width = sprite == null ? w : sprite.rect.width * scale;
            var height = sprite == null ? h : sprite.rect.height * scale;
            Place(
                image.rectTransform,
                x + ((w - width) * 0.5f),
                y + ((h - height) * 0.5f),
                width,
                height);
            return image;
        }

        public Text Label(
            string name,
            RectTransform parent,
            int size,
            TextAnchor alignment,
            string text,
            Font font,
            Color color,
            float x,
            float y,
            float w,
            float h)
        {
            var label = UiFactory.Label(name, parent, font, size, color, alignment, text);
            UiFactory.AddShadow(label, ShadowColor);
            Place(label.rectTransform, x, y, w, h);
            return label;
        }

        /// <summary>Modern top-bar button geometry: the hangar burger (btn_menu).</summary>
        public const float ModernTopButtonX = 14f;
        public const float ModernTopButtonY = 10f;
        public const float ModernTopButtonWidth = 60f;
        public const float ModernTopButtonHeight = 56f;
        public const float ModernTopButtonIcon = 32f;

        /// <summary>Classic Godot btn_menu used as a back button (94x77 at 5,4).</summary>
        public const float ClassicBackButtonX = 5f;
        public const float ClassicBackButtonY = 4f;
        public const float ClassicBackButtonWidth = 94f;
        public const float ClassicBackButtonHeight = 77f;

        /// <summary>
        /// A modern top-bar button with exactly the hangar burger's size,
        /// position, icon size and press feedback.
        /// </summary>
        public Button ModernTopButton(string name, RectTransform parent, string iconRegion)
        {
            var button = StyleButton(name, parent, "button_small_generic",
                ModernTopButtonX, ModernTopButtonY, ModernTopButtonWidth, ModernTopButtonHeight);
            AddIcon(button, iconRegion, ModernTopButtonIcon);
            button.gameObject.AddComponent<PressFeedback>();
            return button;
        }

        /// <summary>The back button of a menu screen in the active skin.</summary>
        public Button BackButton(RectTransform parent, bool modern, float classicIcon)
        {
            if (modern) return ModernTopButton("btn_menu", parent, "icon_back");
            var button = StyleButton("btn_menu", parent, "button_small_generic",
                ClassicBackButtonX, ClassicBackButtonY, ClassicBackButtonWidth, ClassicBackButtonHeight);
            AddIcon(button, "icon_back", classicIcon);
            return button;
        }

        public Button StyleButton(
            string name,
            RectTransform parent,
            string styleRegion,
            float x,
            float y,
            float w,
            float h)
        {
            var sprite = Art(styleRegion);
            var button = UiFactory.TextButton(name, parent, sprite, Bold, 0, null);
            var image = button.GetComponent<Image>();
            image.color = sprite == null ? new Color(0f, 0f, 0f, 0.001f) : Color.white;
            image.type = sprite != null && sprite.border.sqrMagnitude > 0f
                ? Image.Type.Sliced
                : Image.Type.Simple;
            ApplyStyleStates(button, styleRegion);
            Place((RectTransform)button.transform, x, y, w, h);
            return button;
        }

        /// <summary>
        /// The Godot draw states of a themed button: the pressed StyleBox when
        /// the theme authored one (style_button_generic_press, exported as the
        /// "_press" region), the brighter pressed font colour either way.
        /// </summary>
        public void ApplyStyleStates(Button button, string styleRegion)
        {
            var state = GodotButtonState.Of(button);
            if (state == null || string.IsNullOrEmpty(styleRegion))
            {
                return;
            }

            var pressed = Art(styleRegion + UiTheme.PressedRegionSuffix);
            if (pressed != null)
            {
                state.SetPressedStyle(pressed, UiTheme.ButtonPressTint);
            }
        }

        /// <summary>Godot Button icon: centred and clamped to icon_max_width.</summary>
        public void AddIcon(Button button, string region, float iconMaxWidth)
        {
            var sprite = Art(region);
            var icon = UiFactory.Picture("icon", button.transform, sprite);
            icon.preserveAspect = true;
            var rt = icon.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = HangarScreen.IconSize(sprite, iconMaxWidth);
        }

        public void Bind(Button button, Action action, string sound)
        {
            button.onClick.AddListener(() =>
            {
                if (Audio != null)
                {
                    Audio.PlayButtonClick(sound);
                }

                action();
            });
        }

        /// <summary>Top-left anchored rect inside the parent's own space.</summary>
        public static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            GodotRect.Apply(rt, 0f, 0f, 0f, 0f, x, y, x + w, y + h, GodotRect.TopLeftPivot);
        }

        // ------------------------------------------------------------------
        // Anchored placement, one helper per Godot anchors_preset.
        //
        // Place() gives a node the size it had at the reference resolution and
        // pins it to the parent's top-left. That matches Godot only for the
        // nodes Godot also left on the default anchors. Everything that touches
        // a screen edge uses a stretching preset instead, and the CanvasScaler
        // runs in Expand mode, so on a larger screen the canvas grows past the
        // reference resolution while a Place()d node keeps its reference size.
        // That mismatch is what clipped the tech tree on all four sides: the
        // scroll viewports stayed 1386x733 inside a bigger canvas and their
        // RectMask2D cut off everything beyond it.
        //
        // These take the parent-relative Godot offsets exactly as they appear
        // in the .tscn, so offset_bottom = -116 is passed as -116 and no
        // reference-resolution arithmetic is needed at the call site.
        // ------------------------------------------------------------------

        /// <summary>anchors_preset 15: full rect, follows the canvas on both axes.</summary>
        public static void Stretch(
            RectTransform rt,
            float offsetLeft,
            float offsetTop,
            float offsetRight,
            float offsetBottom)
        {
            GodotRect.Apply(
                rt, 0f, 0f, 1f, 1f,
                offsetLeft, offsetTop, offsetRight, offsetBottom,
                GodotRect.TopLeftPivot);
        }

        /// <summary>anchors_preset 9: pinned left, stretches vertically.</summary>
        public static void LeftStretch(
            RectTransform rt,
            float offsetLeft,
            float offsetTop,
            float offsetRight,
            float offsetBottom)
        {
            GodotRect.Apply(
                rt, 0f, 0f, 0f, 1f,
                offsetLeft, offsetTop, offsetRight, offsetBottom,
                GodotRect.TopLeftPivot);
        }

        /// <summary>anchors_preset 11: pinned right, stretches vertically.</summary>
        public static void RightStretch(
            RectTransform rt,
            float offsetLeft,
            float offsetTop,
            float offsetRight,
            float offsetBottom)
        {
            GodotRect.Apply(
                rt, 1f, 0f, 1f, 1f,
                offsetLeft, offsetTop, offsetRight, offsetBottom,
                GodotRect.TopLeftPivot);
        }

        /// <summary>anchors_preset 12: pinned to the bottom, full parent width.</summary>
        public static void BottomStretch(
            RectTransform rt,
            float offsetLeft,
            float offsetTop,
            float offsetRight,
            float offsetBottom)
        {
            GodotRect.Apply(
                rt, 0f, 1f, 1f, 1f,
                offsetLeft, offsetTop, offsetRight, offsetBottom,
                GodotRect.TopLeftPivot);
        }

        /// <summary>anchors_preset 7: pinned to the bottom edge, centred horizontally.</summary>
        public static void BottomCenter(
            RectTransform rt,
            float offsetLeft,
            float offsetTop,
            float offsetRight,
            float offsetBottom)
        {
            GodotRect.Apply(
                rt, 0.5f, 1f, 0.5f, 1f,
                offsetLeft, offsetTop, offsetRight, offsetBottom,
                GodotRect.TopLeftPivot);
        }

        /// <summary>anchors_preset 2: pinned to the bottom-left corner.</summary>
        public static void BottomLeft(
            RectTransform rt,
            float offsetLeft,
            float offsetTop,
            float offsetRight,
            float offsetBottom)
        {
            GodotRect.Apply(
                rt, 0f, 1f, 0f, 1f,
                offsetLeft, offsetTop, offsetRight, offsetBottom,
                GodotRect.TopLeftPivot);
        }

        /// <summary>
        /// anchors_preset 5: a node the source centred horizontally with zero
        /// width, so its children keep the coordinates they have at the
        /// reference resolution while the band itself stays centred on a wider
        /// canvas.
        /// </summary>
        public static void CenterBand(RectTransform rt)
        {
            GodotRect.Apply(
                rt, 0.5f, 0f, 0.5f, 1f,
                -UiLayout.ReferenceWidth * 0.5f, 0f,
                UiLayout.ReferenceWidth * 0.5f, 0f,
                GodotRect.TopLeftPivot);
        }

        /// <summary>anchors_preset 3: pinned to the bottom-right corner.</summary>
        public static void BottomRight(
            RectTransform rt,
            float offsetLeft,
            float offsetTop,
            float offsetRight,
            float offsetBottom)
        {
            GodotRect.Apply(
                rt, 1f, 1f, 1f, 1f,
                offsetLeft, offsetTop, offsetRight, offsetBottom,
                GodotRect.TopLeftPivot);
        }

        // ------------------------------------------------------------------
        // Region naming rules for the ported tech tree art.
        // ------------------------------------------------------------------

        /// <summary>tank_icon_&lt;alphanumeric lowercase name&gt;, e.g. t-34-85 -&gt; tank_icon_t3485.</summary>
        public static string TankIconRegion(string tank)
        {
            if (string.IsNullOrEmpty(tank))
            {
                return null;
            }

            var builder = new StringBuilder("tank_icon_");
            foreach (var symbol in tank)
            {
                if (char.IsLetterOrDigit(symbol))
                {
                    builder.Append(char.ToLowerInvariant(symbol));
                }
            }

            return builder.ToString();
        }

        /// <summary>TanksDataBase.get_lvl_icon, non-premium variants.</summary>
        public static string LevelRegion(int level)
        {
            return "tank_level_" + Mathf.Clamp(level, 1, TanksDatabase.MaxLevel).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>TanksDataBase.get_class_icon (TankTypesSmall).</summary>
        public static string ClassRegion(TankClass tankClass)
        {
            switch (tankClass)
            {
                case TankClass.Medium:
                    return "tank_class_medium";
                case TankClass.Heavy:
                    return "tank_class_heavy";
                case TankClass.TankDestroyer:
                    return "tank_class_td";
                default:
                    return "tank_class_light";
            }
        }

        /// <summary>
        /// Premium status on a card is drawn twice in the source: the gold
        /// glyph variants of TanksDataBase.get_lvl_icon and get_class_icon,
        /// and the collector tint applied on top of the plain white glyph.
        /// Collector wins over premium, exactly like the name colour.
        /// </summary>
        private static bool UsesGoldGlyph(TankInfo info)
        {
            return info != null && info.Premium && !info.Collector && !info.Mod;
        }

        /// <summary>TanksDataBase.apply_tank_level_icon.</summary>
        public static string TankLevelRegion(TankInfo info)
        {
            var region = LevelRegion(info == null ? 1 : info.Level);
            return UsesGoldGlyph(info) ? region + "_prem" : region;
        }

        /// <summary>TanksDataBase.apply_tank_class_icon.</summary>
        public static string TankClassRegion(TankInfo info)
        {
            var region = ClassRegion(info == null ? TankClass.Light : info.Class);
            return UsesGoldGlyph(info) ? region + "_prem" : region;
        }

        /// <summary>
        /// The modulate apply_tank_level_icon and apply_tank_class_icon put on
        /// the glyph: collector blue, or white for everything else, because a
        /// premium already uses the gold texture.
        /// </summary>
        public static Color TankGlyphTint(TankInfo info)
        {
            if (info != null && info.Mod)
            {
                return TankNameMod;
            }

            return info != null && info.Collector ? TankNameCollector : Color.white;
        }

        public static string NationRegion(Nation nation)
        {
            var index = (int)nation;
            if (index < 1 || index > 9)
            {
                index = 1;
            }

            return "hangar_nation" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The flag of a filter's nation toggle: hangar_nation*, which
        /// ui_hangar.tscn gives nation1 .. nation5. Other and European keep
        /// the tech tree's wide node banner there, so their toggles take the
        /// carousel flag of the same set instead.
        /// </summary>
        public static string FilterNationRegion(Nation nation)
        {
            return nation == Nation.Other || nation == Nation.European
                ? CarouselNationRegion(nation)
                : NationRegion(nation);
        }

        /// <summary>TankInfoMain.nation_flags used by carousel_tank.setup.</summary>
        public static string CarouselNationRegion(Nation nation)
        {
            var index = (int)nation;
            if (index < 1 || index > 9)
            {
                index = 1;
            }

            return "tank_nation_" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>vehicleGun / vehicleEngine / vehicleChassis.</summary>
        public static string ModuleKindRegion(ModuleKind kind)
        {
            switch (kind)
            {
                case ModuleKind.Engine:
                    return "module_engine";
                case ModuleKind.Tracks:
                    return "module_chassis";
                case ModuleKind.Turret:
                    return "module_turret";
                case ModuleKind.Radio:
                    // The Godot port has no radio sprite yet; keep a stable
                    // existing module glyph until the 4.8 atlas is imported.
                    return "module_engine";
                default:
                    return "module_gun";
            }
        }

        /// <summary>
        /// ui_tank_module.tscn tile backgrounds: ModulesBackground_04 when the
        /// module is gated, _06 while it is researchable or bought but not
        /// mounted, _07 once it is installed.
        /// </summary>
        public static string ModuleBackgroundRegion(ResearchStatus status)
        {
            switch (status)
            {
                case ResearchStatus.NotAvailable:
                    return "module_bg4";
                case ResearchStatus.InstalledBought:
                    return "module_bg7";
                default:
                    return "module_bg6";
            }
        }

        /// <summary>tech_tree_tank.gd status_to_bg / status_to_bg_prem.</summary>
        public static string TreeCardRegion(ResearchStatus status, bool premium)
        {
            if (premium)
            {
                return status == ResearchStatus.NotAvailable || status == ResearchStatus.Available
                    ? "tree_tank_bg4"
                    : "tree_tank_bg3";
            }

            switch (status)
            {
                case ResearchStatus.NotAvailable:
                    return "tree_tank_bg7";
                case ResearchStatus.InstalledBought:
                    return "tree_tank_bg5";
                default:
                    return "tree_tank_bg6";
            }
        }

        /// <summary>
        /// ui_shell_icon.shell_type_to_icon and its premium / empty variants:
        /// the source keys the shell art off the shell type, not off the slot.
        /// </summary>
        public static string ShellIconRegion(ShellType type, bool premium, bool empty)
        {
            var prefix = empty ? "shell_icon_empty_" : "shell_icon_";
            switch (type)
            {
                case ShellType.Apcr:
                    return prefix + "4";
                case ShellType.Heat:
                    return prefix + "1";
                case ShellType.He:
                    return prefix + (premium && !empty ? "2" : (premium ? "2" : "3"));
                case ShellType.Hesh:
                    return prefix + "2";
                default:
                    return prefix + (premium && !empty ? "5" : "6");
            }
        }

        /// <summary>ui_shell_icon.shell_type_to_abbr.</summary>
        public static string ShellAbbreviationKey(ShellType type)
        {
            switch (type)
            {
                case ShellType.Apcr:
                    return "Abbr_ARMOR_PIERCING_CR";
                case ShellType.Heat:
                    return "Abbr_HOLLOW_CHARGE";
                case ShellType.He:
                case ShellType.Hesh:
                    return "Abbr_HIGH_EXPLOSIVE";
                default:
                    return "Abbr_ARMOR_PIERCING";
            }
        }

        /// <summary>db_tanks.shell_type_to_color: the slider fill per type.</summary>
        public static Color ShellColor(ShellType type)
        {
            switch (type)
            {
                case ShellType.Apcr:
                    return new Color(0.071f, 0.071f, 0.075f, 1f);
                case ShellType.Heat:
                    return new Color(0.851f, 0.486f, 0.012f, 1f);
                case ShellType.He:
                case ShellType.Hesh:
                    return new Color(0.729f, 0.267f, 0.004f, 1f);
                default:
                    return new Color(0.718f, 0.353f, 0.173f, 1f);
            }
        }

        /// <summary>equipment_&lt;id&gt;, with EmptySlot for an empty slot.</summary>
        public static string EquipmentIconRegion(string id)
        {
            return string.IsNullOrEmpty(id) ? "equipment_empty_slot" : "equipment_" + id;
        }

        /// <summary>
        /// equipment_icon.init(id): a consumable slot searches
        /// consumables_list first and equipment_list second, a plain slot only
        /// equipment_list. An id neither table knows leaves the slot empty,
        /// which the caller draws as the EmptySlot placeholder, so this
        /// returns null instead of a guessed region name.
        /// </summary>
        public static string SlotIconRegion(string id, bool consumable)
        {
            if (string.IsNullOrEmpty(id)) { return null; }

            if (consumable)
            {
                var item = ConsumableCatalog.Get(id);
                if (item != null) { return item.IconRegion; }
            }

            var equipment = EquipmentCatalog.Get(id);
            return equipment != null ? equipment.IconRegion : null;
        }

        /// <summary>
        /// Util.format_number: 35000 -&gt; "35 000". The price under a grid
        /// entry and the buy bar both print the grouped number, never the
        /// short form.
        /// </summary>
        public static string FormatNumber(long value)
        {
            // Strip the sign from the text rather than taking Math.Abs, which
            // overflows on long.MinValue.
            var digits = value.ToString(CultureInfo.InvariantCulture).TrimStart('-');
            var text = string.Empty;
            for (var i = 0; i < digits.Length; i++)
            {
                if (i > 0 && (digits.Length - i) % 3 == 0)
                {
                    text += " ";
                }

                text += digits[i];
            }

            return value < 0 ? "-" + text : text;
        }

        /// <summary>
        /// Util.format_number_to_k: the short form only starts at ten
        /// thousand ("35 000" stays grouped, 356700 -&gt; "356.7k"), and a
        /// million switches to "kk".
        /// </summary>
        public static string FormatNumberToK(int value)
        {
            if (value >= 1000000)
            {
                return Suffix(value / 1000000f, "kk");
            }

            if (value >= 10000)
            {
                return Suffix(value / 1000f, "k");
            }

            return FormatNumber(value);
        }

        /// <summary>Util._format_suffix: one decimal, trailing zero dropped.</summary>
        private static string Suffix(float value, string suffix)
        {
            var rounded = Mathf.Round(value * 10f) / 10f;
            return rounded.ToString("0.#", CultureInfo.InvariantCulture) + suffix;
        }
    }
}
