using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// SaveSettings.ui_scale_battle.
    ///
    /// Both battle canvases are laid out against the authored Godot window, so
    /// the player factor cannot be a Canvas scaleFactor: ScaleWithScreenSize
    /// ignores that field. It divides the reference resolution instead, which
    /// is what the source does when it switches UI context before a battle.
    /// The registry is what lets the settings screen push a new factor onto a
    /// HUD that is already built, so the row takes effect without a restart.
    /// </summary>
    public static class BattleUiScale
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

                var value = settings.UiScaleBattle;
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

        /// <summary>Push the current factor onto every live battle canvas.</summary>
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
