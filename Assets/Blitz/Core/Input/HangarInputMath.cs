using System;

namespace BlitzLegacy.Core.Input
{
    /// <summary>Pure input math copied from the Godot hangar camera script.</summary>
    public static class HangarInputMath
    {
        public const float DefaultSensitivity = 0.6f;
        public const float MinSensitivity = 0.1f;
        public const float MaxSensitivity = 2f;
        public const float DragScale = 0.3f * 0.15f;
        public const float MinPitch = -60f;
        public const float MaxPitch = 5f;

        public static float NormalizeSensitivity(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return DefaultSensitivity;
            }

            return Math.Max(MinSensitivity, Math.Min(MaxSensitivity, value));
        }

        public static float DegreesForPixels(float pixelDelta, float sensitivity)
        {
            return pixelDelta * NormalizeSensitivity(sensitivity) * DragScale;
        }

        public static float ClampPitch(float degrees)
        {
            return Math.Max(MinPitch, Math.Min(MaxPitch, degrees));
        }
    }
}
