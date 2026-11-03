using System;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// The scalar half of Godot <c>gun_component.gd</c> and of
    /// <c>base_tank._launch_single_shell</c>: the reload clock, the cone a shot
    /// leaves the barrel in, the recoil impulse and the damage and penetration
    /// rolls. Core is built without engine references, so the caller hands in
    /// the clock and the random draws.
    /// </summary>
    public static class GunMath
    {
        /// <summary><c>just_shot()</c> stays true for a quarter of a second.</summary>
        public const float JustShotWindow = 0.25f;

        /// <summary>_global.gd: <c>DMG_DEVIATION</c> at its default setting.</summary>
        public const float DamageDeviation = 0.25f;

        /// <summary>_global.gd: <c>PEN_DEVIATION</c> at its default setting.</summary>
        public const float PenetrationDeviation = 0.05f;

        /// <summary>
        /// <c>dmg_rand</c> and <c>pen_rand</c> draw <c>randfn(1, deviation * 0.4)</c>
        /// and clamp the draw into the deviation window.
        /// </summary>
        public const float DeviationSigmaFactor = 0.4f;

        /// <summary>Lower bound of <c>clampf(1 - disp / disp_temp, 0.4, 0.75)</c>.</summary>
        public const float ConeMultiplierMin = 0.4f;

        /// <summary>Upper bound of the same clamp.</summary>
        public const float ConeMultiplierMax = 0.75f;

        /// <summary><c>gun_trace</c>: <c>dist * 0.033 * effective_dispersion()</c>.</summary>
        public const float DispersionOffsetFactor = 0.033f;

        /// <summary><c>_launch_single_shell</c>: <c>impulse_mul * caliber ** 2.15</c>.</summary>
        public const float RecoilCaliberExponent = 2.15f;

        /// <summary><c>can_shoot()</c>: the clock has to be strictly past the stamp.</summary>
        public static bool CanShoot(float now, float nextShoot)
        {
            return now > nextShoot;
        }

        /// <summary>
        /// <c>get_reload_remaining()</c>. A loaded gun reports the whole reload
        /// rather than zero, which is what keeps the HUD ring full instead of
        /// empty between shots.
        /// </summary>
        public static float ReloadRemaining(float now, float nextShoot, float total)
        {
            return CanShoot(now, nextShoot) ? total : nextShoot - now;
        }

        /// <summary><c>get_reload_coeff()</c>.</summary>
        public static float ReloadCoefficient(float remaining, float total)
        {
            if (total <= 0f)
            {
                // The source divides by the reload without guarding it, and the
                // infinity that comes out of a zero reload clamps to loaded.
                return 1f;
            }

            var coefficient = remaining / total;
            if (coefficient < 0f)
            {
                return 0f;
            }

            return coefficient > 1f ? 1f : coefficient;
        }

        /// <summary><c>just_shot()</c>.</summary>
        public static bool JustShot(float now, float lastShot)
        {
            return now - lastShot <= JustShotWindow;
        }

        /// <summary>
        /// <c>_launch_single_shell</c>: a gun settled down onto its own
        /// dispersion still throws three quarters of the cone, and the worst
        /// bloom it can sit in keeps four tenths.
        /// </summary>
        public static float ConeMultiplier(float dispersion, float dispersionTemp)
        {
            if (dispersionTemp <= 0f)
            {
                // 1 - disp / 0 runs off to negative infinity, which clamps low.
                return ConeMultiplierMin;
            }

            var multiplier = 1f - dispersion / dispersionTemp;
            if (multiplier < ConeMultiplierMin)
            {
                return ConeMultiplierMin;
            }

            return multiplier > ConeMultiplierMax ? ConeMultiplierMax : multiplier;
        }

        /// <summary>
        /// <c>gun_trace</c>: how far the dispersion point sits from the aim
        /// point.
        /// </summary>
        public static float DispersionOffset(float distanceToAim, float dispersion)
        {
            return distanceToAim * DispersionOffsetFactor * dispersion;
        }

        /// <summary>
        /// <c>_launch_single_shell</c> reads the cone as the angle between the
        /// barrel and the dispersion point <c>gun_trace</c> parks beside the aim
        /// point. That offset grows with the distance to the aim point, so the
        /// distance cancels out and the angle is the arc tangent of the
        /// dispersion factor alone.
        /// </summary>
        public static float ConeAngle(float distanceToAim, float dispersion)
        {
            if (distanceToAim <= 0f)
            {
                return 0f;
            }

            return (float)Math.Atan2(
                DispersionOffset(distanceToAim, dispersion), distanceToAim);
        }

        /// <summary><c>recoil = impulse_mul * gun_info.caliber ** 2.15</c>.</summary>
        public static float Recoil(float impulseMultiplier, int caliber)
        {
            return impulseMultiplier * (float)Math.Pow(caliber, RecoilCaliberExponent);
        }

        /// <summary><c>dmg_rand</c> / <c>pen_rand</c>: the draw clamped to its window.</summary>
        public static float ClampRoll(float roll, float deviation)
        {
            var min = 1f - deviation;
            var max = 1f + deviation;
            if (roll < min)
            {
                return min;
            }

            return roll > max ? max : roll;
        }

        /// <summary><c>get_dmg_random()</c>.</summary>
        public static int DamageRoll(float damage, float roll)
        {
            return Roll(damage, roll, DamageDeviation);
        }

        /// <summary>
        /// <c>get_dmg_random()</c> with the DMG_DEVIATION the save asked for
        /// instead of the stock quarter.
        /// </summary>
        public static int DamageRoll(float damage, float roll, float deviation)
        {
            return Roll(damage, roll, deviation);
        }

        /// <summary>
        /// <c>get_pen_random()</c>. It is declared as a float in the source but
        /// runs through <c>clampi</c>, so a shell leaves the barrel with a whole
        /// number of penetration.
        /// </summary>
        public static int PenetrationRoll(float penetration, float roll)
        {
            return Roll(penetration, roll, PenetrationDeviation);
        }

        /// <summary><c>get_pen_random()</c> with a saved PEN_DEVIATION.</summary>
        public static int PenetrationRoll(
            float penetration, float roll, float deviation)
        {
            return Roll(penetration, roll, deviation);
        }

        /// <summary>projectile.gd: <c>lifetime = max_dist / absf(speed)</c>.</summary>
        public static float ShellLifetime(float maxDistance, float speed)
        {
            var travel = Math.Abs(speed);
            if (travel <= 0f)
            {
                return 0f;
            }

            return maxDistance / travel;
        }

        private static int Roll(float value, float roll, float deviation)
        {
            // clampi truncates its float argument toward zero before clamping it
            // into the floor / ceil window the source builds from the deviation.
            // Core stores source values as float32. At an exact bound such as
            // 100 * 0.95 that can land a few ulps below the whole number even
            // though Godot's 64-bit float reaches 95 before clampi truncates.
            var rolled = (int)(value * ClampRoll(roll, deviation) + 0.00001f);
            var min = (int)Math.Floor(value * (1f - deviation));
            var max = (int)Math.Ceiling(value * (1f + deviation));
            if (rolled < min)
            {
                return min;
            }

            return rolled > max ? max : rolled;
        }
    }
}
