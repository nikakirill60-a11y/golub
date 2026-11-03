namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// Port of <c>Global.calculate_ram_damage</c> and the constants
    /// <c>request_ram_damage</c> / <c>_resolve_ram_damage</c> apply around it.
    /// </summary>
    public static class RamMath
    {
        /// <summary><c>wcoeff</c>: kilograms into the source weight unit.</summary>
        public const float WeightCoefficient = 0.00125f;

        /// <summary><c>maxf(0.0, closing_speed) * 0.25</c>.</summary>
        public const float ClosingSpeedFactor = 0.25f;

        /// <summary>A spall liner takes 15 percent off the ram.</summary>
        public const float SpallLinerFactor = 0.85f;

        /// <summary><c>Util.timer_curtime(0.03, ...)</c>: both notifications land first.</summary>
        public const float ResolveDelay = 0.03f;

        /// <summary>
        /// <c>(0.5 * weight_combined * relative_speed ** 2) * (1 - (weight_individual
        /// / weight_combined))</c>, halved again by a spall liner.
        /// </summary>
        public static float Damage(
            float weightCombined,
            float weightIndividual,
            float relativeSpeed,
            bool spallLiner)
        {
            if (weightCombined <= 0f)
            {
                return 0f;
            }

            return (0.5f * weightCombined * relativeSpeed * relativeSpeed)
                * (1f - (weightIndividual / weightCombined))
                * (spallLiner ? SpallLinerFactor : 1f);
        }
    }
}
