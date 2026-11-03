using System.Collections.Generic;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The Modern replacement for the menu chrome: button plates, panels,
    /// header and footer bands, rows, sliders and check boxes. Only the
    /// regions listed in <see cref="Chrome"/> are remapped; every icon
    /// (currencies, XP, tank class/tier/nation, modules, shells, equipment,
    /// glyphs) and the orange "BATTLE!" plate stay the original artwork.
    /// </summary>
    public sealed class ModernSkin : IAtlasSkin
    {
        public static readonly ModernSkin Instance = new ModernSkin();

        private enum Look
        {
            Glass,
            Green,
            Red,
            Panel,
            PanelHeader,
            Band,
            Hairline,
            Footer,
            Row,
            Track,
            Thumb,
            Checked,
            Unchecked,
        }

        private static readonly Dictionary<string, Look> Chrome = new Dictionary<string, Look>
        {
            { "button_generic", Look.Glass },
            { "button_small_generic", Look.Glass },
            { "button_flat", Look.Glass },
            { "hangar_gray_button", Look.Glass },
            { "hangar_filter_hide", Look.Glass },
            { "button_green", Look.Green },
            { "button_small_green", Look.Green },
            { "button_red", Look.Red },
            { "panel_body", Look.Panel },
            { "hangar_select_tile", Look.Panel },
            { "panel_header", Look.PanelHeader },
            { "hangar_header", Look.Band },
            { "hangar_thin_red_line", Look.Hairline },
            { "hangar_footer", Look.Footer },
            { "settings_row_bg", Look.Row },
            { "slider_frame", Look.Track },
            { "slider_thumb", Look.Thumb },
            { "settings_checkbox_checked", Look.Checked },
            { "settings_checkbox_unchecked", Look.Unchecked },
        };

        /// <summary>Regions the skin redraws, for tests and tooling.</summary>
        public static IEnumerable<string> ChromeRegions
        {
            get { return Chrome.Keys; }
        }

        /// <summary>True when <paramref name="region"/> (or its pressed variant) is redrawn.</summary>
        public static bool Remaps(string region)
        {
            if (string.IsNullOrEmpty(region))
            {
                return false;
            }

            bool pressed;
            return Chrome.ContainsKey(BaseRegion(region, out pressed));
        }

        public Sprite Resolve(string region, AtlasRegionTable source)
        {
            bool pressed;
            Look look;
            if (!Chrome.TryGetValue(BaseRegion(region, out pressed), out look))
            {
                return null;
            }

            return Sprite(look, pressed);
        }

        private static string BaseRegion(string region, out bool pressed)
        {
            pressed = region.EndsWith(UiTheme.PressedRegionSuffix, System.StringComparison.Ordinal);
            return pressed
                ? region.Substring(0, region.Length - UiTheme.PressedRegionSuffix.Length)
                : region;
        }

        private static Sprite Sprite(Look look, bool pressed)
        {
            switch (look)
            {
                case Look.Glass:
                    return pressed
                        ? ModernSprites.RoundedRect(ModernTheme.Radius, ModernTheme.GlassPressedTop,
                            ModernTheme.GlassPressedBottom, ModernTheme.Accent, 2f)
                        : ModernSprites.RoundedRect(ModernTheme.Radius, ModernTheme.GlassTop,
                            ModernTheme.GlassBottom, ModernTheme.Stroke, 1.5f);
                case Look.Green:
                    return pressed
                        ? ModernSprites.RoundedRect(ModernTheme.Radius, ModernTheme.GreenBottom,
                            ModernTheme.GreenBottom, new Color(0.6f, 1f, 0.6f, 0.6f), 2f)
                        : ModernSprites.RoundedRect(ModernTheme.Radius, ModernTheme.GreenTop,
                            ModernTheme.GreenBottom, new Color(0.6f, 1f, 0.6f, 0.3f), 1.5f);
                case Look.Red:
                    return pressed
                        ? ModernSprites.RoundedRect(ModernTheme.Radius, ModernTheme.RedBottom,
                            ModernTheme.RedBottom, new Color(1f, 0.6f, 0.55f, 0.6f), 2f)
                        : ModernSprites.RoundedRect(ModernTheme.Radius, ModernTheme.RedTop,
                            ModernTheme.RedBottom, new Color(1f, 0.6f, 0.55f, 0.3f), 1.5f);
                case Look.Panel:
                    return ModernSprites.RoundedRect(ModernTheme.PanelRadius, ModernTheme.GlassStrong,
                        ModernTheme.GlassStrong, ModernTheme.Stroke, 1.5f);
                case Look.PanelHeader:
                    return ModernSprites.RoundedRect(10, ModernTheme.GlassTop,
                        ModernTheme.GlassBottom, ModernTheme.Stroke, 1f);
                case Look.Band:
                    return ModernSprites.RoundedRect(1, ModernTheme.GlassStrong);
                case Look.Hairline:
                    return ModernSprites.RoundedRect(1, ModernTheme.Accent);
                case Look.Footer:
                    return ModernSprites.RoundedRect(1, ModernTheme.Glass);
                case Look.Row:
                    return ModernSprites.RoundedRect(8, new Color(1f, 1f, 1f, 0.045f),
                        new Color(1f, 1f, 1f, 0.025f), new Color(1f, 1f, 1f, 0.07f), 1f);
                case Look.Track:
                    // Minimal slider: no frame behind the line.
                    return ModernSprites.RoundedRect(1, Color.clear);
                case Look.Thumb:
                    // SliderThumb.tres is a 132 px grabber drawn at 0.5; keep the
                    // footprint but draw only a small dot in its centre.
                    return ModernSprites.Dot(132, 40f, ModernTheme.TextPrimary, Color.clear, 0f);
                case Look.Checked:
                    return ModernSprites.CheckBox(true);
                default:
                    return ModernSprites.CheckBox(false);
            }
        }
    }
}
