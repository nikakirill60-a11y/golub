using System;

namespace BlitzLegacy.Core.Sandbox
{
    /// <summary>
    /// Port of assets/data/training_multiplier_math.gd: logarithmic conversion
    /// between a Sandbox slider position and a real multiplier.
    ///
    /// The slider stores a symmetric position measured in sixths of a doubling,
    /// a preset always keeps the resulting positive multiplier:
    ///     0 -> x1, +-1 -> x1.122 / x0.891, +-6 -> x2 / x0.5, +-20 -> x10 / x0.1.
    /// Equal distances from the centre are exact reciprocals, so a reduction
    /// can never reach zero or change sign.
    /// </summary>
    public static class SandboxMultiplierMath
    {
        /// <summary>Slider steps per doubling: keeps x2/x4/x8 exactly on the grid.</summary>
        public const float StepsPerDoubling = 6f;
        public const float OffsetStep = 1f;
        public const float MinOffset = -20f;
        public const float MaxOffset = 20f;
        public const float MinMultiplier = 0.1f;
        public const float MaxMultiplier = 10f;

        // Godot is_zero_approx / is_equal_approx tolerance (CMP_EPSILON).
        private const double Epsilon = 0.00001d;

        public static float OffsetToMultiplier(float value)
        {
            if (!IsFinite(value))
            {
                return 1f;
            }

            var offset = Clamp(value, MinOffset, MaxOffset);
            if (Math.Abs(offset) < Epsilon)
            {
                return 1f;
            }

            return Clamp((float)Math.Pow(2d, offset / StepsPerDoubling), MinMultiplier, MaxMultiplier);
        }

        /// <summary>
        /// Repairs presets written by the older signed sliders, where zero meant
        /// "unchanged" and a negative value meant "divide by its magnitude".
        /// </summary>
        public static float SanitizeMultiplier(float value)
        {
            if (!IsFinite(value))
            {
                return 1f;
            }

            var multiplier = value;
            if (Math.Abs(multiplier) < Epsilon)
            {
                return 1f;
            }

            if (multiplier < 0f)
            {
                multiplier = 1f / Math.Abs(multiplier);
            }

            return Clamp(multiplier, MinMultiplier, MaxMultiplier);
        }

        public static float MultiplierToOffset(float value)
        {
            var multiplier = SanitizeMultiplier(value);
            if (IsEqualApprox(multiplier, 1f))
            {
                return 0f;
            }

            return Clamp((float)(StepsPerDoubling * Math.Log(multiplier) / Math.Log(2d)), MinOffset, MaxOffset);
        }

        /// <summary>Quantizes a value onto the whole-step grid the slider can stop on.</summary>
        public static float SnapMultiplier(float value)
        {
            return OffsetToMultiplier((float)Math.Round(MultiplierToOffset(value), MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// Two layers may not leave the range a single layer can reach, so a
        /// global x10 stacked with a player x10 stays x10.
        /// </summary>
        public static float Combine(float globalValue, float localValue)
        {
            var combined = SanitizeMultiplier(globalValue) * SanitizeMultiplier(localValue);
            return Clamp(combined, MinMultiplier, MaxMultiplier);
        }

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static float Clamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        public static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        /// <summary>Godot is_equal_approx: relative tolerance with an absolute floor.</summary>
        public static bool IsEqualApprox(float a, float b)
        {
            if (a == b)
            {
                return true;
            }

            var tolerance = Epsilon * Math.Abs(a);
            if (tolerance < Epsilon)
            {
                tolerance = Epsilon;
            }

            return Math.Abs(a - b) < tolerance;
        }
    }
}
