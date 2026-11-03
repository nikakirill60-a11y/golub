using System;
using BlitzLegacy.Core.Save;

namespace BlitzLegacy.Core.Ui
{
    /// <summary>
    /// How the modern hangar carousel is drawn. It always sits on the bottom
    /// edge of the screen; only its height and interactivity change.
    /// </summary>
    public enum CarouselMode
    {
        /// <summary>Main hangar: full cards.</summary>
        Normal = 0,

        /// <summary>Folded into a handle that can be pulled up (sub-screens).</summary>
        Collapsed = 1,

        /// <summary>A sub-screen pulled the handle up: full cards over the screen.</summary>
        Expanded = 2,

        /// <summary>Battle queue: visible, dimmed, no selection changes.</summary>
        Locked = 3,

        /// <summary>Full-screen overlays (settings, statistics, results).</summary>
        Hidden = 4,
    }

    /// <summary>Geometry of the modern carousel for one card-size setting.</summary>
    public struct CarouselMetrics
    {
        public float CardWidth;
        public float CardHeight;
        public float Spacing;

        /// <summary>Height of the whole bottom bar, cards plus padding.</summary>
        public float BarHeight;

        /// <summary>Font size of the tank name on a card.</summary>
        public int NameFontSize;
    }

    /// <summary>
    /// Engine-free rules of the menu style switch: whether the first-run
    /// picker is due, which style is active and how big the carousel is.
    /// Kept in Core so the decisions are unit tested without a scene.
    /// </summary>
    public static class MenuStyleRules
    {
        /// <summary>Environment override used by smoke and render tools.</summary>
        public const string OverrideVariable = "BLITZ_UI_STYLE";

        /// <summary>Height of the folded carousel handle.</summary>
        public const float HandleHeight = 34f;

        /// <summary>Width of the folded carousel handle.</summary>
        public const float HandleWidth = 232f;

        /// <summary>
        /// Below this aspect ratio (4:3 and 3:2 tablets) the hangar has too
        /// little height for large cards, so the carousel drops to compact.
        /// </summary>
        public const float NarrowAspect = 1.55f;

        /// <summary>The first-run picker is shown while no style was chosen.</summary>
        public static bool NeedsPicker(SettingsData settings)
        {
            return settings != null && settings.UiStyle == SettingsData.UiStyleUnset;
        }

        /// <summary>
        /// Whether the modern menus are drawn. An unset style draws classic:
        /// that is what a player saw before the update, and the picker opens
        /// over it.
        /// </summary>
        public static bool IsModern(SettingsData settings, string environmentOverride)
        {
            var forced = ParseOverride(environmentOverride);
            if (forced != SettingsData.UiStyleUnset)
            {
                return forced == SettingsData.UiStyleModern;
            }

            return settings != null && settings.UiStyle == SettingsData.UiStyleModern;
        }

        /// <summary>"modern" / "classic" (any case) or unset.</summary>
        public static int ParseOverride(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return SettingsData.UiStyleUnset;
            }

            var trimmed = value.Trim();
            if (string.Equals(trimmed, "modern", StringComparison.OrdinalIgnoreCase))
            {
                return SettingsData.UiStyleModern;
            }

            return string.Equals(trimmed, "classic", StringComparison.OrdinalIgnoreCase)
                ? SettingsData.UiStyleClassic
                : SettingsData.UiStyleUnset;
        }

        /// <summary>
        /// Card size for the setting, dropping to compact on narrow screens.
        /// Unknown values fall back to normal.
        /// </summary>
        public static CarouselMetrics Metrics(int size, float aspect)
        {
            if (aspect > 0f && aspect < NarrowAspect)
            {
                size = SettingsData.CarouselSizeCompact;
            }

            switch (size)
            {
                case SettingsData.CarouselSizeCompact:
                    return new CarouselMetrics
                    {
                        CardWidth = 120f, CardHeight = 72f, Spacing = 6f,
                        BarHeight = 92f, NameFontSize = 15,
                    };
                case SettingsData.CarouselSizeLarge:
                    return new CarouselMetrics
                    {
                        CardWidth = 168f, CardHeight = 100f, Spacing = 8f,
                        BarHeight = 124f, NameFontSize = 19,
                    };
                default:
                    return new CarouselMetrics
                    {
                        CardWidth = 144f, CardHeight = 86f, Spacing = 8f,
                        BarHeight = 108f, NameFontSize = 17,
                    };
            }
        }

        /// <summary>Height the carousel takes from the bottom of the screen.</summary>
        public static float OccupiedHeight(CarouselMode mode, CarouselMetrics metrics)
        {
            switch (mode)
            {
                case CarouselMode.Normal:
                case CarouselMode.Locked:
                    return metrics.BarHeight;
                case CarouselMode.Collapsed:
                case CarouselMode.Expanded:
                    // The expanded bar floats over the screen; the screen
                    // itself only ever reserves the handle.
                    return HandleHeight;
                default:
                    return 0f;
            }
        }

        /// <summary>
        /// Carousel state on the main hangar screen. The burger drawer runs
        /// from under the header down to the bottom edge, so while it is open
        /// the carousel folds into its handle instead of covering the drawer's
        /// lower half.
        /// </summary>
        public static CarouselMode MainCarouselMode(bool menuOpen)
        {
            return menuOpen ? CarouselMode.Collapsed : CarouselMode.Normal;
        }

        /// <summary>Whether a card tap changes the selected tank.</summary>
        public static bool AcceptsSelection(CarouselMode mode)
        {
            return mode == CarouselMode.Normal || mode == CarouselMode.Expanded;
        }

        /// <summary>
        /// How far up the carousel bar is drawn: 1 = the whole bar above the
        /// bottom edge, 0 = folded below it (only the handle, or nothing).
        /// </summary>
        public static float CarouselShown(CarouselMode mode)
        {
            switch (mode)
            {
                case CarouselMode.Normal:
                case CarouselMode.Expanded:
                case CarouselMode.Locked:
                    return 1f;
                default:
                    return 0f;
            }
        }

        /// <summary>
        /// Length of a carousel slide that starts part of the way: a slide
        /// reversed halfway takes half the time instead of restarting.
        /// </summary>
        public static float CarouselTweenTime(float from, float to, float fullTime)
        {
            var span = Math.Abs(to - from);
            return fullTime * Math.Min(1f, span);
        }

        /// <summary>Longest frame a menu tween advances by (one 30 fps frame).</summary>
        public const float MaxTweenStep = 1f / 30f;

        /// <summary>
        /// Time a menu tween advances by in one frame. A hitch (a long frame
        /// while the UI rebuilds) would otherwise jump the tween straight to
        /// its end, so the slide reads as a snap instead of a motion.
        /// </summary>
        public static float TweenStep(float deltaTime)
        {
            if (deltaTime <= 0f || float.IsNaN(deltaTime))
            {
                return 0f;
            }

            return Math.Min(deltaTime, MaxTweenStep);
        }
    }
}
