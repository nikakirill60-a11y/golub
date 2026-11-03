namespace BlitzLegacy.Core.Ui
{
    /// <summary>
    /// The viewport contract the Godot menus were laid out against, kept in one
    /// place so the Unity CanvasScaler derives from the same numbers instead of
    /// a magic constant.
    ///
    /// project.godot: window/size = 1040x550, stretch/mode = canvas_items,
    /// stretch/aspect = expand, stretch/scale = 0.75.
    /// </summary>
    public static class UiLayout
    {
        public const float GodotWindowWidth = 1040f;
        public const float GodotWindowHeight = 550f;

        /// <summary>Godot's stretch/scale, i.e. SaveSettings.ui_scale_menu.</summary>
        public const float GodotStretchScale = 0.75f;

        /// <summary>
        /// Godot draws the menus at <see cref="GodotStretchScale"/>, so the
        /// logical canvas is correspondingly larger. Feeding these two numbers
        /// to a CanvasScaler in Expand mode is what removes the need for a
        /// menu-UI-size setting: one reference resolution covers every screen.
        /// </summary>
        public const float ReferenceWidth = GodotWindowWidth / GodotStretchScale;

        public const float ReferenceHeight = GodotWindowHeight / GodotStretchScale;

        /// <summary>
        /// SaveSettings.ui_scale_battle. The source keeps a second factor for
        /// the battle screens and re-applies it whenever the UI context flips:
        /// a phone renders the HUD at 1.0, so the logical canvas is the
        /// authored 1040x550 instead of the ~1387x733 the menu factor stretches
        /// it to, because the thumb buttons and readouts have to stay reachable.
        /// Desktop keeps the menu factor and is unchanged.
        /// </summary>
        public const float GodotBattleScaleMobile = 1f;

        /// <inheritdoc cref="GodotBattleScaleMobile"/>
        public const float GodotBattleScaleDesktop = GodotStretchScale;

        /// <summary>The logical canvas the battle HUD is laid out against.</summary>
        public static float BattleScale(bool mobile)
        {
            return mobile ? GodotBattleScaleMobile : GodotBattleScaleDesktop;
        }

        /// <inheritdoc cref="BattleScale"/>
        public static float BattleReferenceWidth(bool mobile)
        {
            return GodotWindowWidth / BattleScale(mobile);
        }

        /// <inheritdoc cref="BattleScale"/>
        public static float BattleReferenceHeight(bool mobile)
        {
            return GodotWindowHeight / BattleScale(mobile);
        }
    }
}
