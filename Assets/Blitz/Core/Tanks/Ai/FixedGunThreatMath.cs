using System;

namespace BlitzLegacy.Core.Tanks.Ai
{
    /// <summary>
    /// ai/fixed_gun_threat_math.gd. A casemate tank destroyer has to turn its
    /// hull before a point-blank threat outside the gun arc can be answered,
    /// and the source keeps that decision stateless so it can be tested
    /// without a tank. The port keeps it in Core for the same reason.
    /// </summary>
    public static class FixedGunThreatMath
    {
        /// <summary>TD_CLOSE_HULL_RESPONSE_MIN_DISTANCE.</summary>
        public const float CloseHullResponseMinDistance = 45f;

        /// <summary>TD_CLOSE_HULL_RESPONSE_MAX_DISTANCE.</summary>
        public const float CloseHullResponseMaxDistance = 70f;

        /// <summary>TD_GUN_ARC_MARGIN_DEGREES.</summary>
        public const float GunArcMarginDegrees = 2f;

        /// <summary>
        /// close_threat_turn, in the port's space. The source works in a hull
        /// whose forward is -Z and answers a threat past the left limit with
        /// -1; Unity's hull looks along +Z because every ported transform is
        /// mirrored across Z, so the same threat is a positive angle here and
        /// the returned turn is the tank's right axis: 1 turns right, -1 turns
        /// left, 0 leaves the hull alone.
        /// </summary>
        public static int CloseThreatTurn(
            float localX,
            float localZ,
            float deviationLeft,
            float deviationRight,
            bool limitedTraverse,
            float responseDistance,
            float arcMarginDegrees = GunArcMarginDegrees)
        {
            if (!limitedTraverse)
            {
                return 0;
            }

            var flatLengthSquared = (localX * localX) + (localZ * localZ);
            if (flatLengthSquared <= 0.0001f
                || flatLengthSquared > responseDistance * responseDistance)
            {
                return 0;
            }

            var desiredAngle = (float)Math.Atan2(localX, localZ);
            var leftLimit = Radians(Math.Max(Math.Abs(deviationLeft) - arcMarginDegrees, 0f));
            var rightLimit = Radians(Math.Max(Math.Abs(deviationRight) - arcMarginDegrees, 0f));
            if (desiredAngle > rightLimit)
            {
                return 1;
            }

            if (desiredAngle < -leftLimit)
            {
                return -1;
            }

            return 0;
        }

        /// <summary>The response distance clampf of answer_close_fixed_gun_threat.</summary>
        public static float ResponseDistance(float hullLength)
        {
            var distance = hullLength * 8f;
            if (distance < CloseHullResponseMinDistance)
            {
                return CloseHullResponseMinDistance;
            }

            return distance > CloseHullResponseMaxDistance
                ? CloseHullResponseMaxDistance
                : distance;
        }

        private static float Radians(float degrees)
        {
            return degrees * (float)Math.PI / 180f;
        }
    }
}
