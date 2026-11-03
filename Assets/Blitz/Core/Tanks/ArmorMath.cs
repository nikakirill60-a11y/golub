using System;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// The penetration math from Godot <c>Projectile</c>, lifted out of the
    /// scene tree so the runtime and the headless tests share one copy.
    /// </summary>
    public static class ArmorMath
    {
        /// <summary>
        /// The angle a shell has to graze past to ricochet, from
        /// <c>ricoshet_angles</c>, which lists the same value for AP and APCR.
        /// </summary>
        public const float RicochetAngleDeg = 70f;

        /// <summary>Penetration a screen eats per metre of the gap behind it.</summary>
        public const float ScreenPenaltyPerMetre = 33f;

        /// <summary>Penetration a shaped charge loses per metre behind a screen.</summary>
        public const float HeatPenaltyPerMetre = 100f;

        private const float DegToRad = (float)(Math.PI / 180.0);
        private const float RadToDeg = (float)(180.0 / Math.PI);
        private const float HalfPi = (float)(Math.PI * 0.5);

        /// <summary><c>ShellInfo.get_normalization</c>, in degrees.</summary>
        public static float Normalization(ShellType type)
        {
            switch (type)
            {
                case ShellType.Ap:
                    return 5f;
                case ShellType.Apcr:
                    return 2f;
                default:
                    return 0f;
            }
        }

        /// <summary>
        /// <c>three_caliber_types</c>. Only the solid shot normalises into the
        /// plate and only it overmatches thin armour.
        /// </summary>
        public static bool UsesCaliberRule(ShellType type)
        {
            return type == ShellType.Ap || type == ShellType.Apcr;
        }

        /// <summary><c>explosives</c>.</summary>
        public static bool IsExplosive(ShellType type)
        {
            return type == ShellType.He || type == ShellType.Hesh;
        }

        /// <summary>Whether <c>ricoshet_angles</c> covers this shell at all.</summary>
        public static bool CanRicochet(ShellType type)
        {
            return type == ShellType.Ap || type == ShellType.Apcr;
        }

        /// <summary>
        /// <c>impact_angle_to_normal</c>: zero when the shell arrives straight
        /// down the plate normal, ninety degrees when it only grazes it.
        ///
        /// Core is built without engine references, so the caller hands in the
        /// dot product of the plate normal and the shell's direction, both unit
        /// length. The sign drops out, exactly as <c>absf</c> does in the
        /// source, so it makes no difference which way the normal faces.
        /// </summary>
        public static float ImpactAngle(float normalDotDirection)
        {
            var dot = Math.Abs(normalDotDirection);
            if (dot > 1f)
            {
                dot = 1f;
            }

            return HalfPi - (float)Math.Asin(dot);
        }

        /// <summary>
        /// <c>calculate_armor</c>. The source takes the shell's own
        /// normalisation off the angle here as well, on top of what
        /// <c>handle_hit</c> already subtracted, so solid shot thins the plate
        /// twice over. The port keeps that behaviour.
        /// </summary>
        public static float CalculateArmor(
            int nominalArmor, float impactAngle, ShellType type)
        {
            var angle = impactAngle - Normalization(type) * DegToRad;
            return nominalArmor / (float)Math.Cos(angle);
        }

        /// <summary>
        /// <c>handle_hit</c>: normalisation grows with the caliber-over-armour
        /// ratio once the shell is more than twice the plate.
        /// </summary>
        public static float NormalizationFor(
            ShellType type, int caliber, int nominalArmor)
        {
            if (!UsesCaliberRule(type))
            {
                return 0f;
            }

            var normalization = Normalization(type);
            if (nominalArmor > 0 && caliber > nominalArmor * 2)
            {
                normalization += normalization * 1.4f * caliber / nominalArmor;
            }

            return normalization;
        }

        /// <summary>The plate a shell actually has to chew through.</summary>
        public static float EffectiveArmor(
            ShellType type, int caliber, int nominalArmor, float impactAngle)
        {
            var normalization =
                NormalizationFor(type, caliber, nominalArmor) * DegToRad;
            return CalculateArmor(
                nominalArmor, Math.Max(impactAngle - normalization, 0f), type);
        }

        /// <summary>
        /// The three-caliber rule: a shell wider than three plates goes through
        /// whatever the angle says.
        /// </summary>
        public static bool Overmatches(ShellType type, int caliber, int nominalArmor)
        {
            return UsesCaliberRule(type) && caliber > nominalArmor * 3;
        }

        /// <summary>
        /// The graze angle this shell needs, in degrees. The Blitz cards carry
        /// <c>ricochetAngle</c> themselves - 70 on solid shot, 80/85 on every
        /// shaped charge, nothing at all on high explosive - so a card that
        /// ships one decides, and only a card without one falls back to the
        /// Godot per-type table. This is what lets the MGM-51A of the XM81
        /// launcher bounce at 85 degrees while a HEAT card without an angle
        /// keeps the old behaviour of never bouncing.
        /// </summary>
        public static float RicochetAngleFor(ShellType type, float shellAngle)
        {
            if (IsExplosive(type))
            {
                return 0f;
            }

            if (shellAngle > 0f)
            {
                return shellAngle;
            }

            return CanRicochet(type) ? RicochetAngleDeg : 0f;
        }

        /// <summary>
        /// A ricochet needs a shell with a graze angle at all, an impact past
        /// that angle and a plate the shell reached without crossing a screen
        /// first. <paramref name="shellRicochetAngle"/> is the card's own
        /// <c>ricochetAngle</c>; zero keeps the per-type default.
        /// </summary>
        public static bool Ricochets(
            ShellType type,
            float impactAngle,
            bool screenHit,
            float shellRicochetAngle = 0f)
        {
            if (screenHit)
            {
                return false;
            }

            var angle = RicochetAngleFor(type, shellRicochetAngle);
            if (angle <= 0f)
            {
                return false;
            }

            return impactAngle * RadToDeg >= angle;
        }

        /// <summary>
        /// <c>Projectile.explosion_radius_for</c>: the card's own
        /// <c>explosionRadius</c> when it ships one - 5.11 m on the MGM-51C
        /// rocket - and <c>pow(caliber * 0.02, 1.3)</c> otherwise.
        /// </summary>
        public static float ExplosionRadiusFor(float explosionRadius, int caliber)
        {
            if (explosionRadius > 0f)
            {
                return explosionRadius;
            }

            return (float)Math.Pow(Math.Max(caliber, 0) * 0.02, 1.3);
        }

        /// <summary><c>armor_effective &lt;= shell_info.pen</c>.</summary>
        public static bool Penetrates(float effectiveArmor, float penetration)
        {
            return effectiveArmor <= penetration;
        }

        /// <summary>Penetration left after a screen, over the gap behind it.</summary>
        public static float PenetrationAfterScreen(float penetration, float distance)
        {
            return Math.Max(penetration - distance * ScreenPenaltyPerMetre, 0f);
        }

        /// <summary>
        /// Penetration left after a shaped charge crossed a gap. The source does
        /// not clamp this one, so a long gap can leave it negative and the plate
        /// then stops the jet.
        /// </summary>
        public static float PenetrationAfterHeatGap(float penetration, float distance)
        {
            return penetration - distance * HeatPenaltyPerMetre;
        }
    }
}
