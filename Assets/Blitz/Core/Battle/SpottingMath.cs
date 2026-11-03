namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// Port of the spotting arithmetic in <c>base_tank.try_spot_tank</c>: a
    /// target inside 50 m is always seen, and camouflage pulls the view range
    /// down towards that floor.
    /// </summary>
    public static class SpottingMath
    {
        /// <summary>The unconditional grant of <c>distance_squared_to &lt; 50 ** 2</c>.</summary>
        public const float AlwaysSpotDistance = 50f;

        /// <summary>The floor camouflage can never push the range below.</summary>
        public const float MinimumSpotDistance = 50f;

        /// <summary><c>unspot_time = curtime() + 10</c>.</summary>
        public const float UnspotSeconds = 10f;

        /// <summary><c>TanksDataBase.eq_camonet.name_id</c>.</summary>
        public const string CamonetId = "camonet";

        /// <summary>The camonet grant of get_spot_radius / get_camo_factor.</summary>
        public const float CamonetFactor = 1.25f;

        /// <summary><c>Util.curtime() - last_movement > 3</c>.</summary>
        public const float CamonetParkedSeconds = 3f;

        /// <summary><c>Global.bush_active_dist</c>.</summary>
        public const float BushActiveDistance = 8f;

        /// <summary>
        /// map_object_vegetation.density. The port carries no per-bush value in
        /// its map tables yet, so every bush attenuates with the source default.
        /// </summary>
        public const float BushDensity = 0.45f;

        /// <summary>The <c>for j in range(5)</c> bush walk of try_spot_tank.</summary>
        public const int BushPasses = 5;

        /// <summary><c>cf -= (gun_info.caliber * 0.0015) ** 0.8</c>.</summary>
        public const float ShotCamoCaliberFactor = 0.0015f;
        public const float ShotCamoExponent = 0.8f;

        /// <summary>A moving non-light loses a quarter of its camouflage.</summary>
        public const float MovingCamoFactor = 0.75f;

        /// <summary><c>spot_radius - (spot_radius - 50.0) * camo_factor</c>.</summary>
        public static float SpotDistance(float spotRadius, float camoFactor)
        {
            var camo = camoFactor < 0f ? 0f : (camoFactor > 1f ? 1f : camoFactor);
            return spotRadius - ((spotRadius - MinimumSpotDistance) * camo);
        }
    }
}
