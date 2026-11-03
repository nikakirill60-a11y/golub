namespace BlitzLegacy.Core.Ui
{
    /// <summary>
    /// Geometry, colours and timings of the plate the source draws above every
    /// tank except the local player's: <c>ui_external.tscn</c> laid out by
    /// <c>ui_tank_external.gd</c>. The offsets below are copied from the source
    /// Control nodes and are read by the same Godot-to-uGUI conversion the rest
    /// of the battle UI uses.
    /// </summary>
    public static class TankPlateTuning
    {
        /// <summary>TankExternalUI.scale (ui_external.tscn:61).</summary>
        public const float RootScale = 0.8f;

        /// <summary><c>hull_height + 0.5</c> (ui_tank_external.gd:135).</summary>
        public const float HeightOffset = 0.5f;

        /// <summary>clampi bounds of the z_index term (ui_tank_external.gd:145).</summary>
        public const int DepthMin = -999;

        /// <inheritdoc cref="DepthMin"/>
        public const int DepthMax = 999;

        // hp_back (ui_external.tscn:78)
        public const float BackLeft = -57.499996f;
        public const float BackTop = -92.5f;
        public const float BackRight = 56.500004f;
        public const float BackBottom = -62.5f;

        // hp_bar_under (ui_external.tscn:87)
        public const float BarUnderLeft = -53.75f;
        public const float BarUnderTop = -88.74997f;
        public const float BarUnderRight = 54.25f;
        public const float BarUnderBottom = -64.74997f;

        // hp_bar_top (ui_external.tscn:98)
        public const float BarTopLeft = -53.75f;
        public const float BarTopTop = -88.75003f;
        public const float BarTopRight = 54.25f;
        public const float BarTopBottom = -64.75003f;

        // class (ui_external.tscn:122)
        public const float ClassLeft = -20.000002f;
        public const float ClassTop = -52.5f;
        public const float ClassRight = 23.750002f;
        public const float ClassBottom = -1.3031921f;
        public const float ClassScale = 0.9f;

        // hp (ui_external.tscn:147)
        public const float HealthTextLeft = -46.250008f;
        public const float HealthTextTop = -86.249985f;
        public const float HealthTextRight = 47.749992f;
        public const float HealthTextBottom = -63.249985f;
        public const int HealthTextFontSize = 20;

        // dmg (ui_external.tscn:165)
        public const float DamageLeft = 32.25f;
        public const float DamageTop = -142.49997f;
        public const float DamageRight = 72.25f;
        public const float DamageBottom = -119.49997f;
        public const int DamageFontSize = 22;
        public const float DamageColourR = 0.8745098f;
        public const float DamageColourG = 0f;
        public const float DamageColourB = 0f;

        /// <summary>theme_override_constants/shadow_offset_y (ui_external.tscn:183).</summary>
        public const float DamageShadowY = -1f;

        // tank_name (ui_external.tscn:222)
        public const float NameLeft = -47.50001f;
        public const float NameTop = -118.75f;
        public const float NameRight = 46.49999f;
        public const float NameBottom = -89.75f;
        public const int NameFontSize = 26;

        // class_shadow (ui_external.tscn:107)
        public const float ClassShadowLeft = -20.000002f;
        public const float ClassShadowTop = -51.25f;
        public const float ClassShadowRight = 23.750002f;
        public const float ClassShadowBottom = -0.05319214f;
        public const float ClassShadowScale = 0.9f;

        // level (ui_external.tscn:135)
        public const float LevelLeft = -18.750008f;
        public const float LevelTop = -164.99997f;
        public const float LevelRight = 24.999996f;
        public const float LevelBottom = -113.80316f;
        public const float LevelScale = 0.9f;

        // name_shadow (ui_external.tscn:201)
        public const float NameShadowLeft = -82.5f;
        public const float NameShadowTop = -127.500015f;
        public const float NameShadowRight = 81.5f;
        public const float NameShadowBottom = -83.500015f;
        public const float NameShadowScaleX = 0.4f;

        /// <summary><c>hp_u.value -= max((hp_u-hp_t)*delta*2, 1)</c>.</summary>
        public const float UnderBarCatchupRate = 2f;
        public const float UnderBarMinimumStep = 1f;

        /// <summary><c>accum_fade_delay</c>.</summary>
        public const float DamageAccumulationWindow = 2.5f;

        /// <summary>The take_dmg animation holds until 1.6 s, then fades by 2.0 s.</summary>
        public const float DamageVisibleTime = 1.6f;
        public const float DamageFadeTime = 0.4f;
    }
}
