using System;

namespace BlitzLegacy.Core.Ui
{
    /// <summary>
    /// Numbers behind the magazine indicator: geometry from
    /// <c>assets/gfx/ui/ui_gun_burst.tscn</c> (the <c>prefab</c> TextureRect
    /// every round is duplicated from) and behaviour from the scene script.
    ///
    /// BlitzLegacy.Core is compiled with noEngineReferences, so this type stays
    /// on primitives; callers build their own Vector2/Color values.
    /// </summary>
    public static class BurstUiTuning
    {
        /// <summary>Godot's PI in float precision.</summary>
        public const float Pi = 3.1415927f;

        /// <summary><c>angle_step = PI * .035</c>.</summary>
        public const float AngleStep = Pi * 0.035f;

        /// <summary><c>sniper_mode(true)</c> spreads the fan twice as wide.</summary>
        public const float AngleStepSniper = Pi * 0.07f;

        // prefab: anchors_preset 8 with these offsets, scale 0.21 and
        // pivot_offset (9, -300), so every round swings around a point one
        // pixel below the centre of the screen.
        public const float PrefabLeft = -8.999939f;
        public const float PrefabTop = 301.00006f;
        public const float PrefabRight = 14.285706f;
        public const float PrefabBottom = 369.00006f;
        public const float PrefabScale = 0.21f;
        public const float PrefabPivotX = 9f;
        public const float PrefabPivotY = -300f;

        /// <summary><c>Color.RED.lerp(Color.GREEN, coeff)</c> ceiling.</summary>
        public const float TintCoefficientMax = 0.75f;

        /// <summary>The source brightens the lerped tint by this much.</summary>
        public const float TintGain = 1.5f;

        /// <summary>
        /// <c>update_bursts_pos()</c>: the fan is centred on the crosshair, and
        /// the first icon of the array sits on its left-hand edge.
        /// </summary>
        public static float Rotation(int index, int burstsInit, float angleStep)
        {
            var total = angleStep * (burstsInit / 2f) - angleStep * 0.5f;
            return total - angleStep * index;
        }

        /// <summary>
        /// <c>update_bursts()</c>: a loaded round runs red to green as the clip
        /// fills, brightened by <see cref="TintGain"/>; a spent one is white.
        /// Godot clamps the over-bright channels when it paints them.
        /// </summary>
        public static void Tint(
            int newBursts, int burstsInit, out float red, out float green, out float blue)
        {
            var safeInit = burstsInit <= 0 ? 1 : burstsInit;
            var coefficient = Math.Min(newBursts / (float)safeInit, TintCoefficientMax);
            red = Clamp01((1f - coefficient) * TintGain);
            green = Clamp01(coefficient * TintGain);
            blue = 0f;
        }

        /// <summary><c>i + 1 &lt;= new_bursts</c>: this icon still holds a round.</summary>
        public static bool Loaded(int index, int newBursts)
        {
            return index + 1 <= newBursts;
        }

        private static float Clamp01(float value)
        {
            if (value < 0f) { return 0f; }

            return value > 1f ? 1f : value;
        }
    }
}
