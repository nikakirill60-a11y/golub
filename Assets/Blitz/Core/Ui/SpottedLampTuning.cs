using System;

namespace BlitzLegacy.Core.Ui
{
    /// <summary>
    /// Timers and the "anims/lamp" animation of the sixth-sense bulb, from
    /// <c>ui_player_battle.gd</c> (<c>_on_player_spotted</c>,
    /// <c>_show_spotted_lamp</c>) and <c>assets/gfx/ui/battle/anims.res</c>.
    ///
    /// The animation has two linear value tracks: <c>lamp:modulate</c> keeps
    /// the whole lamp opaque and fades it out with an eased last segment, and
    /// <c>lamp/lamp_shine:modulate</c> pulses the glow behind the bulb twice.
    /// </summary>
    public static class SpottedLampTuning
    {
        /// <summary><c>Util.timer(3, _show_spotted_lamp)</c>.</summary>
        public const float ShowDelay = 3f;

        /// <summary><c>Util.timer(5.2, _hide_spotted_lamp)</c>.</summary>
        public const float HideDelay = 5.2f;

        /// <summary>Length of "anims/lamp" (no loop).</summary>
        public const float AnimationLength = 5f;

        /// <summary><c>Audio.play_sound2d(Audio.ui_lamp, "UI", false, 0.66)</c>.</summary>
        public const float SoundVolume = 0.66f;

        /// <summary>Sound table entry behind <c>Audio.ui_lamp</c>.</summary>
        public const string SoundName = "ui_lamp";

        // lamp:modulate alpha keys; the transition sits on the middle key, so
        // it shapes the segment that fades the lamp out.
        private const float FadeStart = 4.633333206f;
        private const float FadeTransition = 1.148698449f;

        private static readonly float[] ShineTimes =
        {
            0f, 1f, 1.966666698f, 2.966666698f, 5f,
        };

        private static readonly float[] ShineAlphas =
        {
            0f, 1f, 0f, 1f, 0f,
        };

        /// <summary>Alpha of <c>lamp:modulate</c> at animation time <paramref name="t"/>.</summary>
        public static float LampAlpha(float t)
        {
            if (t <= FadeStart)
            {
                return 1f;
            }

            if (t >= AnimationLength)
            {
                return 0f;
            }

            var x = (t - FadeStart) / (AnimationLength - FadeStart);
            return 1f - Ease(x, FadeTransition);
        }

        /// <summary>Alpha of <c>lamp/lamp_shine:modulate</c> at animation time <paramref name="t"/>.</summary>
        public static float ShineAlpha(float t)
        {
            if (t <= ShineTimes[0])
            {
                return ShineAlphas[0];
            }

            for (var i = 1; i < ShineTimes.Length; i++)
            {
                if (t < ShineTimes[i])
                {
                    var a = ShineTimes[i - 1];
                    var x = (t - a) / (ShineTimes[i] - a);
                    return ShineAlphas[i - 1] + (ShineAlphas[i] - ShineAlphas[i - 1]) * x;
                }
            }

            return ShineAlphas[ShineAlphas.Length - 1];
        }

        /// <summary>
        /// Godot's <c>ease(x, curve)</c>, which Animation applies to a key's
        /// transition value.
        /// </summary>
        public static float Ease(float x, float curve)
        {
            if (x < 0f) { x = 0f; }
            if (x > 1f) { x = 1f; }

            if (curve > 0f)
            {
                return curve < 1f
                    ? 1f - (float)Math.Pow(1f - x, 1f / curve)
                    : (float)Math.Pow(x, curve);
            }

            if (curve < 0f)
            {
                return x < 0.5f
                    ? (float)Math.Pow(x * 2f, -curve) * 0.5f
                    : (1f - (float)Math.Pow(1f - (x - 0.5f) * 2f, -curve)) * 0.5f + 0.5f;
            }

            return 0f;
        }
    }
}
