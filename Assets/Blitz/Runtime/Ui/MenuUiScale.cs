using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// SaveSettings.ui_scale_menu, the menu half of <see cref="BattleUiScale"/>.
    ///
    /// The row was on the settings screen and in the save, and nothing read
    /// it, so dragging it did nothing at all. Every menu canvas is laid out
    /// against the authored Godot window, so the factor divides the reference
    /// resolution here too, and the registry lets the row reach the menus that
    /// are already on screen - including the settings screen drawing the row.
    /// </summary>
    public static class MenuUiScale
    {
        public const float Min = 0.5f;
        public const float Max = 2f;

        private struct Binding
        {
            public CanvasScaler Scaler;
            public Vector2 Authored;
        }

        private static readonly List<Binding> Bound = new List<Binding>();

        /// <summary>The clamped factor the save carries, or 1 without a save.</summary>
        public static float Factor
        {
            get
            {
                var bootstrap = BlitzBootstrap.Current;
                var settings = bootstrap == null ? null : bootstrap.Settings;
                if (settings == null)
                {
                    return 1f;
                }

                var value = settings.UiScaleMenu;
                return value <= 0f ? 1f : Mathf.Clamp(value, Min, Max);
            }
        }

        public static void Register(CanvasScaler scaler, Vector2 authored)
        {
            if (scaler == null)
            {
                return;
            }

            Unregister(scaler);
            Bound.Add(new Binding { Scaler = scaler, Authored = authored });
            ApplyTo(Bound[Bound.Count - 1], Factor);
        }

        public static void Unregister(CanvasScaler scaler)
        {
            for (var i = Bound.Count - 1; i >= 0; i--)
            {
                if (Bound[i].Scaler == null || Bound[i].Scaler == scaler)
                {
                    Bound.RemoveAt(i);
                }
            }
        }

        /// <summary>Push the current factor onto every live menu canvas.</summary>
        public static void Apply()
        {
            var factor = Factor;
            for (var i = Bound.Count - 1; i >= 0; i--)
            {
                if (Bound[i].Scaler == null)
                {
                    Bound.RemoveAt(i);
                    continue;
                }

                ApplyTo(Bound[i], factor);
            }
        }

        private static void ApplyTo(Binding binding, float factor)
        {
            // A bigger factor means bigger elements, so the canvas pretends to
            // be that much smaller than the authored window.
            binding.Scaler.referenceResolution = binding.Authored / factor;
        }
    }
}
