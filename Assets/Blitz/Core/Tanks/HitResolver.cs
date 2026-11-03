using System;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>What an armour plate does to a shell that reaches it.</summary>
    public enum HitOutcome
    {
        /// <summary>The three-caliber rule waved the shell straight through.</summary>
        Overmatch = 0,

        /// <summary>The shell grazed off the plate and keeps flying.</summary>
        Ricochet = 1,

        /// <summary>The plate was thinner than the penetration left.</summary>
        Penetration = 2,

        /// <summary>The plate held, which is what scores a block.</summary>
        Bounce = 3,
    }

    /// <summary>
    /// The order <c>projectile.gd handle_hit()</c> asks its questions in. The
    /// sequence matters: overmatch beats the angle, and the angle beats the
    /// thickness, so a wide enough shell goes through a plate that would
    /// otherwise have bounced it.
    /// </summary>
    public static class HitResolver
    {
        /// <summary>
        /// The armour branch of <c>handle_hit</c>. <paramref name="screenCrossed"/>
        /// is the source's <c>last_hit_screen_tank</c> test: a shell that came
        /// through spaced armour has already used up its ricochet.
        /// </summary>
        public static HitOutcome ResolveArmor(
            ShellType type,
            int caliber,
            float penetration,
            int nominalArmor,
            float impactAngle,
            bool screenCrossed,
            float shellRicochetAngle = 0f)
        {
            if (ArmorMath.Overmatches(type, caliber, nominalArmor))
            {
                return HitOutcome.Overmatch;
            }

            if (ArmorMath.Ricochets(
                    type, impactAngle, screenCrossed, shellRicochetAngle))
            {
                return HitOutcome.Ricochet;
            }

            var effective =
                ArmorMath.EffectiveArmor(type, caliber, nominalArmor, impactAngle);
            return ArmorMath.Penetrates(effective, penetration)
                ? HitOutcome.Penetration
                : HitOutcome.Bounce;
        }

        /// <summary>
        /// The screen branch: spaced armour takes its effective thickness out of
        /// the penetration and lets whatever is left carry on to the plate
        /// behind it.
        /// </summary>
        public static float PenetrationAfterPlate(float penetration, float effectiveArmor)
        {
            var left = penetration - effectiveArmor;
            return left > 0f ? left : 0f;
        }

        /// <summary>
        /// <c>handle_destructible_collision</c>: a prop takes twice the
        /// penetration, or twice the rolled damage from an explosive shell.
        /// </summary>
        public static int DestructibleDamage(ShellType type, int damage, float penetration)
        {
            if (ArmorMath.IsExplosive(type))
            {
                return damage * 2;
            }

            return (int)penetration * 2;
        }

        /// <summary>
        /// The same call then charges the shell the health the prop had before
        /// the hit, and the shell dies once nothing is left.
        /// </summary>
        public static float PenetrationAfterDestructible(float penetration, int healthBefore)
        {
            var left = penetration - healthBefore;
            return left > 0f ? left : 0f;
        }

        /// <summary>Whether a shell with this much penetration left can keep flying.</summary>
        public static bool Continues(float penetration)
        {
            return penetration > 0f && !float.IsNaN(penetration);
        }

        /// <summary>
        /// The gap penalty a shell pays between a screen and the plate behind
        /// it. Shaped charges lose their jet three times faster.
        /// </summary>
        public static float PenetrationAfterGap(
            ShellType type, float penetration, float distance)
        {
            if (type == ShellType.Heat)
            {
                return ArmorMath.PenetrationAfterHeatGap(penetration, Math.Abs(distance));
            }

            return ArmorMath.PenetrationAfterScreen(penetration, Math.Abs(distance));
        }
    }
}
