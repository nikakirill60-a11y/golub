using System;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// Port of Godot <c>DispersionMath</c>: the deterministic gun-dispersion
    /// math shared by the runtime and the headless tests.
    /// </summary>
    public static class DispersionMath
    {
        /// <summary><c>LINEAR_DEADZONE_KMH</c>.</summary>
        public const float LinearDeadzoneKmh = 0.5f;

        /// <summary><c>ANGULAR_DEADZONE_DEG</c>.</summary>
        public const float AngularDeadzoneDeg = 0.5f;

        /// <summary>
        /// The decay constant of <c>recover_dispersion</c>. Exponential decay
        /// gives the same result at 30, 60, 90 and 120 Hz, unlike a per-frame
        /// subtraction, and 1.8 is what preserves the established aim feel.
        /// </summary>
        public const float RecoveryRate = 1.8f;

        /// <summary>The <c>* 3.6</c> the source uses to read m/s as km/h.</summary>
        public const float KmhPerMetrePerSecond = 3.6f;

        private const float RadToDeg = (float)(180.0 / Math.PI);

        /// <summary>
        /// <c>calculate_motion_dispersion</c>. The Blitz movement factors are
        /// coefficients: each is multiplied by the actual linear or angular
        /// speed, and independent penalties combine quadratically.
        /// </summary>
        public static float MotionDispersion(
            float baseDispersion,
            float movementFactor,
            float hullRotationFactor,
            float turretRotationFactor,
            float linearSpeedKmh,
            float hullRotationDegPerSecond,
            float turretRotationDegPerSecond)
        {
            var speed = Math.Abs(linearSpeedKmh);
            var hullSpeed = Math.Abs(hullRotationDegPerSecond);
            var turretSpeed = Math.Abs(turretRotationDegPerSecond);
            if (speed < LinearDeadzoneKmh) { speed = 0f; }
            if (hullSpeed < AngularDeadzoneDeg) { hullSpeed = 0f; }
            if (turretSpeed < AngularDeadzoneDeg) { turretSpeed = 0f; }

            var movementPenalty = Math.Max(movementFactor, 0f) * speed;
            var hullPenalty = Math.Max(hullRotationFactor, 0f) * hullSpeed;
            var turretPenalty = Math.Max(turretRotationFactor, 0f) * turretSpeed;
            var combinedSquared =
                movementPenalty * movementPenalty
                + hullPenalty * hullPenalty
                + turretPenalty * turretPenalty;
            return Math.Max(
                baseDispersion,
                baseDispersion * (float)Math.Sqrt(1f + combinedSquared));
        }

        /// <summary>
        /// <c>calculate_from_velocity</c>. Core carries no engine references,
        /// so the caller passes the two magnitudes the source reads off the
        /// body: the linear speed in m/s and the hull's angular speed in rad/s.
        /// </summary>
        public static float FromVelocity(
            float baseDispersion,
            float movementFactor,
            float hullRotationFactor,
            float turretRotationFactor,
            float linearSpeedMetresPerSecond,
            float hullAngularSpeedRadPerSecond,
            float turretRotationDegPerSecond)
        {
            return MotionDispersion(
                baseDispersion,
                movementFactor,
                hullRotationFactor,
                turretRotationFactor,
                linearSpeedMetresPerSecond * KmhPerMetrePerSecond,
                hullAngularSpeedRadPerSecond * RadToDeg,
                turretRotationDegPerSecond);
        }

        /// <summary><c>calculate_after_shot_dispersion</c>.</summary>
        public static float AfterShot(
            float baseDispersion, float currentDispersion, float afterShotFactor)
        {
            return Math.Max(
                currentDispersion, baseDispersion * Math.Max(afterShotFactor, 1f));
        }

        /// <summary><c>recover_dispersion</c>.</summary>
        public static float Recover(
            float currentDispersion, float baseDispersion, float aimTime, float delta)
        {
            if (aimTime <= 0f)
            {
                return baseDispersion;
            }

            var recovered = currentDispersion
                * (float)Math.Exp(-RecoveryRate * Math.Max(delta, 0f) / aimTime);
            return Math.Max(baseDispersion, recovered);
        }

        /// <summary><c>disp_manager</c>'s legacy branch: <c>turret_ang_vel * 15</c>.</summary>
        public const float LegacyTraverseScale = 15f;

        /// <summary>The same branch clamps the traverse term at two.</summary>
        public const float LegacyTraverseMax = 2f;

        /// <summary><c>absf(suspension.speed) * disp_move_mul * 0.075</c>.</summary>
        public const float LegacySpeedFactor = 0.075f;

        /// <summary><c>newdisp *= 1200 * gun_info.disp * dt</c>.</summary>
        public const float LegacyGrowthScale = 1200f;

        /// <summary><c>disp_bloom</c>'s legacy branch: <c>disp_temp += coeff * 0.33</c>.</summary>
        public const float LegacyBloomFactor = 0.33f;

        /// <summary>
        /// The legacy dispersion tick, which is what every vehicle whose
        /// movement coefficients were never audited still runs, the MS-1
        /// included: its tracks ship no <c>disp_rotation_mul</c>.
        ///
        /// The source grows the dispersion straight to the movement value when
        /// that value is the larger one and otherwise decays what is there, then
        /// floors the result at the gun's own dispersion.
        /// </summary>
        public static float LegacyStep(
            float baseDispersion,
            float currentDispersion,
            float aimTime,
            float turretAngularVelocity,
            float turretFactor,
            float moveMultiplier,
            int rightAxis,
            float speed,
            bool anyWheelHit,
            float delta)
        {
            var traverse = Math.Abs(turretAngularVelocity * LegacyTraverseScale);
            if (traverse > LegacyTraverseMax)
            {
                traverse = LegacyTraverseMax;
            }

            var grown = traverse * turretFactor;
            if (anyWheelHit)
            {
                var move = Math.Abs(rightAxis) * moveMultiplier;
                move += Math.Abs(speed) * moveMultiplier * LegacySpeedFactor;
                grown += move;
            }

            grown *= LegacyGrowthScale * baseDispersion * Math.Max(delta, 0f);

            // A zero aim time divides by zero in the source and the negative
            // infinity that comes out lands on the floor below anyway.
            var decayed = aimTime > 0f
                ? currentDispersion
                    - (1f / aimTime * RecoveryRate) * currentDispersion * Math.Max(delta, 0f)
                : baseDispersion;

            var next = grown > currentDispersion ? grown : decayed;

            // clampf(disp_temp, gun disp, absf(disp_temp)) is a floor at the
            // gun's own dispersion: Godot's clamp returns the minimum first.
            return Math.Max(next, baseDispersion);
        }

        /// <summary><c>disp_bloom</c>'s legacy branch.</summary>
        public static float LegacyBloom(float currentDispersion, float factor)
        {
            return currentDispersion + factor * LegacyBloomFactor;
        }
    }
}
