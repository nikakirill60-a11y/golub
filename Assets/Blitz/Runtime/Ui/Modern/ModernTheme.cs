using UnityEngine;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Palette and metrics of the Modern menu style: dark translucent glass,
    /// thin light strokes and a single amber accent. Classic screens never
    /// read this; every value here only feeds the Modern builders and the
    /// procedural chrome in <see cref="ModernSprites"/>.
    /// </summary>
    public static class ModernTheme
    {
        public static readonly Color Glass = new Color(0.055f, 0.067f, 0.086f, 0.82f);
        public static readonly Color GlassStrong = new Color(0.045f, 0.055f, 0.07f, 0.94f);
        public static readonly Color Solid = new Color(0.045f, 0.055f, 0.07f, 1f);
        public static readonly Color GlassTop = new Color(0.14f, 0.16f, 0.19f, 0.92f);
        public static readonly Color GlassBottom = new Color(0.075f, 0.085f, 0.105f, 0.92f);
        public static readonly Color GlassPressedTop = new Color(0.22f, 0.24f, 0.28f, 0.96f);
        public static readonly Color GlassPressedBottom = new Color(0.13f, 0.145f, 0.17f, 0.96f);
        public static readonly Color Stroke = new Color(1f, 1f, 1f, 0.12f);
        public static readonly Color StrokeStrong = new Color(1f, 1f, 1f, 0.22f);

        /// <summary>#FFB020, the only accent of the style.</summary>
        public static readonly Color Accent = new Color(1f, 0.69f, 0.125f, 1f);
        public static readonly Color AccentSoft = new Color(1f, 0.69f, 0.125f, 0.18f);

        public static readonly Color TextPrimary = new Color(0.95f, 0.96f, 0.97f, 1f);
        public static readonly Color TextSecondary = new Color(0.62f, 0.67f, 0.72f, 1f);
        public static readonly Color TextMuted = new Color(0.45f, 0.5f, 0.55f, 1f);
        public static readonly Color Positive = new Color(0.45f, 0.85f, 0.42f, 1f);
        public static readonly Color Negative = new Color(0.95f, 0.33f, 0.28f, 1f);
        public static readonly Color Scrim = new Color(0.01f, 0.015f, 0.02f, 0.72f);
        public static readonly Color Shadow = new Color(0f, 0f, 0f, 0.55f);

        public static readonly Color GreenTop = new Color(0.22f, 0.6f, 0.29f, 0.96f);
        public static readonly Color GreenBottom = new Color(0.12f, 0.4f, 0.18f, 0.96f);
        public static readonly Color RedTop = new Color(0.66f, 0.19f, 0.16f, 0.96f);
        public static readonly Color RedBottom = new Color(0.44f, 0.1f, 0.09f, 0.96f);

        /// <summary>Corner radius of buttons and cards, in canvas units.</summary>
        public const int Radius = 12;
        public const int PanelRadius = 16;

        /// <summary>Height of the Modern top bar.</summary>
        public const float TopBarHeight = 76f;

        /// <summary>Gap kept between the action row and the carousel.</summary>
        public const float Gap = 12f;

        /// <summary>Duration of the carousel and drawer tweens, seconds.</summary>
        public const float TweenTime = 0.22f;
    }
}
