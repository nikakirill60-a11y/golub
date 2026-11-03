using UnityEngine;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Values read out of assets/fonts/ui_theme_blitz.tres and the two
    /// LabelSettings resources the login screen uses. Godot's Theme resource is
    /// not ported wholesale; only what this screen actually reads.
    /// </summary>
    public static class UiTheme
    {
        public static readonly Color ButtonFont = new Color(0.87451f, 0.87451f, 0.87451f, 1f);

        /// <summary>Button/colors/font_pressed_color.</summary>
        public static readonly Color ButtonFontPressed = Color.white;

        /// <summary>
        /// style_button_generic_press.stylebox modulate_color: what Godot
        /// multiplies the pressed background by. Variations of the theme reuse
        /// their normal StyleBox for every state, so this is also the only
        /// pressed look the source ever draws.
        /// </summary>
        public static readonly Color ButtonPressTint =
            new Color(0.49411765f, 0.49411765f, 0.49411765f, 1f);

        /// <summary>Atlas region suffix of a pressed background.</summary>
        public const string PressedRegionSuffix = "_press";
        public static readonly Color Label = Color.white;
        public static readonly Color BoldLabel =
            new Color(0.9013075f, 0.8850439f, 0.85249746f, 1f);
        public static readonly Color BoldLabelShadow =
            new Color(0.2f, 0.18039216f, 0.11764706f, 1f);
        public static readonly Color ErrorText = new Color(1f, 0.29f, 0.29f, 1f);
        public static readonly Color LineEditFont = new Color(0.875f, 0.875f, 0.875f, 1f);
        public static readonly Color LineEditPlaceholder =
            new Color(0.875f, 0.875f, 0.875f, 0.6f);
        public static readonly Color LineEditBackground =
            new Color(0.333333f, 0.345098f, 0.364706f, 1f);
        public static readonly Color LineEditSelection = new Color(0.5f, 0.5f, 0.5f, 1f);

        public const int LabelFontSize = 15;

        /// <summary>LabelSettings default size, used by warhelios_label.tres.</summary>
        public const int SettingsLabelFontSize = 16;
        public const int BoldLabelFontSize = 21;
        public const int StatFontSize = 18;
        public const int ButtonFontSize = 16;
        public const int PlayButtonFontSize = 24;
        public const int LineEditFontSize = 19;

        /// <summary>Godot's default BoxContainer separation.</summary>
        public const float Separation = 4f;

        /// <summary>
        /// LineEdit minimum height for this theme. Derived, not guessed: the
        /// account creator's VBoxContainer is 152 px tall and its margins plus
        /// separations leave exactly 3 * 28 px for the fields.
        /// </summary>
        public const float LineEditHeight = 28f;

        /// <summary>Godot LineEdit content margins, from StyleBoxFlat_ssw50.</summary>
        public const float LineEditPadding = 4f;

        /// <summary>How long an error message stays up: Util.delay(5).</summary>
        public const float ErrorVisibleSeconds = 5f;
    }
}
