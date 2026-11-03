using System;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// The numbers behind the arty view. A guided rocket carries no gravity of
    /// its own - projectile.gd zeroes it - so the arc it flies is not a
    /// ballistic solution but the path its guidance is walked along: the gun
    /// lifts by the range, the rocket is steered at a point above the target,
    /// and that point sinks as the rocket closes until it is the target.
    /// </summary>
    public static class ArtyMath
    {
        /// <summary>A target next to the hull needs almost no lift.</summary>
        public const float LaunchPitchMin = 6f;

        /// <summary>The lift a far target asks for, before the gun's own stop.</summary>
        public const float LaunchPitchMax = 55f;

        /// <summary>Range at which the gun is asked for all of that lift.</summary>
        public const float LaunchPitchRange = 300f;

        /// <summary>How high over the target the rocket is steered, per metre left.</summary>
        public const float LoftSlope = 0.55f;

        /// <summary>The ceiling on that climb, in metres.</summary>
        public const float LoftCeiling = 70f;

        /// <summary>The view always reaches this far, even on a short shell.</summary>
        public const float RangeMin = 300f;

        /// <summary>And never further than this.</summary>
        public const float RangeMax = 2000f;

        /// <summary>
        /// The barrel elevation the arty view holds, in degrees, never past the
        /// gun's own stop.
        /// </summary>
        public static float LaunchPitch(float distance, float deviationUp)
        {
            var limit = Math.Abs(deviationUp);
            if (distance <= 0f)
            {
                return LaunchPitchMin < limit ? LaunchPitchMin : limit;
            }

            var t = distance / LaunchPitchRange;
            if (t > 1f) { t = 1f; }

            // Square root so the near half of the range spends most of the
            // lift: that is where a flat shot misses over the hull.
            var pitch = LaunchPitchMin
                + ((LaunchPitchMax - LaunchPitchMin) * (float)Math.Sqrt(t));
            return pitch > limit ? limit : pitch;
        }

        /// <summary>How far above the target the rocket is steered right now.</summary>
        public static float LoftHeight(float remaining)
        {
            if (remaining <= 0f) { return 0f; }

            var height = remaining * LoftSlope;
            return height > LoftCeiling ? LoftCeiling : height;
        }

        /// <summary>Near shots cannot spend more elevation than they can turn off.</summary>
        public static float GuidedLaunchPitch(float distance, float deviationUp, float speed, float turnSpeed, float height = 0f)
        {
            if (distance <= 0f || speed <= 0f || turnSpeed <= 0f) return 0f;
            var reachable = 0.25f * distance * turnSpeed / speed * (180f / (float)Math.PI);
            var elevation = (float)Math.Atan2(height, distance) * (180f / (float)Math.PI);
            return Math.Min(Math.Abs(deviationUp), elevation + Math.Min(LaunchPitch(distance, deviationUp), reachable));
        }

        /// <summary>The drawn arc: a bow of that height over the straight line.</summary>
        public static float ArcHeight(float loft, float t)
        {
            if (t <= 0f || t >= 1f) { return 0f; }

            return loft * 4f * t * (1f - t);
        }

        /// <summary>How far from the hull the aim point may be walked.</summary>
        public static float Range(float shellMaxDistance)
        {
            if (shellMaxDistance < RangeMin) { return RangeMin; }
            return shellMaxDistance > RangeMax ? RangeMax : shellMaxDistance;
        }
    }
}
