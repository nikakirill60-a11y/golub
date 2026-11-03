using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Style split of the Sandbox editor. The classic palette is lifted from
    /// training_mode_screen.gd (hint/section/slider colours, map image
    /// modulate, Global.COLOR_SELF selection frame, overlay dims); the Modern
    /// palette maps the same roles onto <see cref="ModernTheme"/>. The layout
    /// is identical in both styles; sprites come from the menu atlas, which
    /// <see cref="MenuStyle.ApplyForMenus"/> already swaps for Modern.
    /// </summary>
    public sealed partial class SandboxScreen
    {
        // training_mode_screen.gd colours.
        private static readonly Color ClassicHint = new Color(0.82f, 0.86f, 0.9f, 1f);
        private static readonly Color ClassicSection = new Color(0.95f, 0.76f, 0.31f, 1f);
        private static readonly Color ClassicSliderBar = new Color(0.19445327f, 0.21491614f, 0.21491715f, 1f);
        private static readonly Color ClassicSliderFill = new Color(0.21176471f, 0.21176471f, 0.21176471f, 1f);
        private static readonly Color ClassicSeparator = new Color(1f, 1f, 1f, 0.18f);
        // Global.COLOR_SELF: the selected map frame.
        private static readonly Color ClassicSelection = new Color(0.897f, 0.427f, 0f, 1f);
        // image.modulate of every map card picture.
        private static readonly Color CardImageTint = new Color(0.82f, 0.82f, 0.82f, 0.8f);
        // The gold tint the hangar gives premium tank levels.
        private static readonly Color PremiumTint = new Color(0.702f, 0.601f, 0.163f, 1f);

        private Color BackdropColor { get { return _modern ? ModernTheme.Scrim : HangarUiKit.ScreenTint; } }

        private Color TextColor { get { return _modern ? ModernTheme.TextPrimary : Color.white; } }

        private Color HintColor { get { return _modern ? ModernTheme.TextSecondary : ClassicHint; } }

        private Color SectionColor { get { return _modern ? ModernTheme.Accent : ClassicSection; } }

        private Color SeparatorColor { get { return _modern ? ModernTheme.Stroke : ClassicSeparator; } }

        private Color ButtonTextColor { get { return _modern ? ModernTheme.TextPrimary : Color.white; } }

        private Color SelectionColor { get { return _modern ? ModernTheme.Accent : ClassicSelection; } }

        private Color MapImageTint { get { return _modern ? new Color(1f, 1f, 1f, 0.9f) : CardImageTint; } }

        private Color SliderBarColor { get { return _modern ? new Color(1f, 1f, 1f, 0.14f) : ClassicSliderBar; } }

        private Color SliderFillColor { get { return _modern ? ModernTheme.Accent : ClassicSliderFill; } }

        private Color PremiumLevelTint { get { return _modern ? ModernTheme.Accent : PremiumTint; } }

        private Color OverlayPanelColor { get { return Color.white; } }

        /// <summary>The full-rect ColorRect dim behind an overlay.</summary>
        private Color DimColor(float alpha)
        {
            // The project renders in linear space, so black overlays blend lighter than in Godot (sRGB).
            // Compensate the coverage to match the perceived darkness of the original dims.
            var linear = 1f - Mathf.Pow(1f - Mathf.Clamp01(alpha), 2.2f);
            if (!_modern) { return new Color(0f, 0f, 0f, linear); }
            var scrim = ModernTheme.Scrim;
            return new Color(scrim.r, scrim.g, scrim.b, Mathf.Max(linear, scrim.a));
        }

        /// <summary>Modern buttons shrink a little while held.</summary>
        private void DecorateButton(Button button)
        {
            if (!_modern || button == null || button.GetComponent<PressFeedback>() != null) { return; }
            button.gameObject.AddComponent<PressFeedback>();
        }
    }

    /// <summary>
    /// Hold-to-repeat for the +/- buttons of the spin rows (the source's
    /// SpinBox arrows auto-repeat while held). Repeats run on unscaled time so
    /// they work in a paused hangar; the click that ends a hold that already
    /// repeated is swallowed via <see cref="ConsumeRepeat"/>.
    /// </summary>
    public sealed class SandboxHoldRepeat : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public const float InitialDelay = 0.45f;
        public const float Interval = 0.06f;

        private Action _action;
        private bool _held;
        private bool _repeated;
        private float _next;

        public void Configure(Action action)
        {
            _action = action;
        }

        /// <summary>True (once) when the current press already fired repeats.</summary>
        public bool ConsumeRepeat()
        {
            var repeated = _repeated;
            _repeated = false;
            return repeated;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            var selectable = GetComponent<Selectable>();
            if (selectable != null && !selectable.interactable) { return; }
            _held = true;
            _repeated = false;
            _next = Time.unscaledTime + InitialDelay;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _held = false;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _held = false;
        }

        private void OnDisable()
        {
            _held = false;
            _repeated = false;
        }

        private void Update()
        {
            if (!_held || _action == null || Time.unscaledTime < _next) { return; }
            _repeated = true;
            _next = Time.unscaledTime + Interval;
            _action();
        }
    }
}
