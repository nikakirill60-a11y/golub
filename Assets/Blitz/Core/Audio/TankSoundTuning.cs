namespace BlitzLegacy.Core.Audio
{
    /// <summary>
    /// The numbers TankSoundsManager (assets/tanks/base/tank_sound_manager.gd)
    /// and the Audio autoload author on their players: gains, report switch
    /// distances and the throttles that keep a looping bed from restarting on
    /// every physics tick. Spatial ranges are in <see cref="SpatialAudio"/>.
    /// </summary>
    public static class TankSoundTuning
    {
        // Per-player gains from the top of tank_sound_manager.gd.
        public const float PlayerEngineGain = 0.15f;
        public const float RemoteEngineGain = 0.15f;
        public const float PlayerTrackGain = 0.025f;
        public const float RemoteTrackGain = 0.15f;

        /// <summary>volume_mul in create_player(): the local tank is louder.</summary>
        public const float PlayerVolumeMul = 0.75f;
        public const float RemoteVolumeMul = 0.45f;

        /// <summary>Gun and hull-explosion players get their own gains.</summary>
        public const float GunVolumeMul = 1f;
        public const float GunInsideVolumeMul = 0.8f;
        public const float CollisionVolumeMul = 0.66f;
        public const float ExplosionVolumeMul = 2f;

        // create_player() polyphony. Its spatial distances live in SpatialAudio.
        public const int MaxPolyphony = 2;

        /// <summary>Beyond these metres the distant report is used instead.</summary>
        public const float GunSoundNearDistance = 300f;
        public const float ExplosionSoundNearDistance = 300f;

        /// <summary>RandomStreamPlayer3D.stream_change_delay.</summary>
        public const float StreamChangeDelay = 0.2f;

        /// <summary>change_to_stream(full_delay: true) holds 75% of the clip.</summary>
        public const float FullDelayShare = 0.75f;

        /// <summary>randomize_pitch: 1 + randf_range(-1, 1) * 0.1.</summary>
        public const float PitchRandomness = 0.1f;

        /// <summary>40% of shots start the muzzle report 25 ms in.</summary>
        public const int GunPlayFromChance = 40;
        public const float GunPlayFromOffset = 0.025f;

        /// <summary>The hull report starts later unless the player is zoomed in.</summary>
        public const float GunInsidePlayFromOffset = 0.15f;

        // on_phys_collision: impulse -> gain, with a 0.1 s retrigger guard.
        public const float CollisionImpulseScale = 0.025f;
        public const float CollisionVolumeMax = 0.4f;
        public const float CollisionVolumeExponent = 1.2f;
        public const float CollisionMinImpulse = 1f;
        public const float CollisionInterval = 0.1f;

        /// <summary>_physics_process: engine load gains this much per axis.</summary>
        public const float TurningLoadShare = 0.3f;

        /// <summary>Below this load a turning tank uses the turning bed.</summary>
        public const float TurningLoadMax = 0.25f;

        /// <summary>More than one wheel touching counts as "tracks on ground".</summary>
        public const int TracksOnGroundHits = 1;

        /// <summary>projectile.gd::_play_sound throttles impacts to 0.3 s.</summary>
        public const float ShellSoundInterval = 0.3f;

        /// <summary>
        /// Audio.safe_spatial_filter_cutoff(): the attenuation low-pass is
        /// capped so it never lands above what the device mix rate supports.
        /// </summary>
        public const float SpatialFilterMaxHz = 20500f;
        public const float SpatialFilterMixRatio = 0.45f;

        public static float SpatialFilterCutoff(float mixRate)
        {
            var scaled = mixRate * SpatialFilterMixRatio;
            return scaled < SpatialFilterMaxHz ? scaled : SpatialFilterMaxHz;
        }

        /// <summary>muffle_sfx(): SFX ducks to 85% over 0.3 s.</summary>
        public const float MuffleSfxShare = 0.85f;
        public const float MuffleFadeDuration = 0.3f;

        /// <summary>
        /// clampf(impulse * 0.025, 0, 0.4) ** 1.2 from on_phys_collision.
        /// </summary>
        public static float CollisionVolume(float impulse)
        {
            var scaled = impulse * CollisionImpulseScale;
            if (scaled < 0f) { scaled = 0f; }
            if (scaled > CollisionVolumeMax) { scaled = CollisionVolumeMax; }
            return (float)System.Math.Pow(scaled, CollisionVolumeExponent);
        }

        /// <summary>
        /// The engine load term: the share of top speed reached plus the
        /// steering axis, which is what makes a pivoting tank rev up.
        /// </summary>
        public static float EngineLoad(float speedShare, float rightAxis)
        {
            var axis = rightAxis < 0f ? -rightAxis : rightAxis;
            return speedShare + axis * TurningLoadShare;
        }
    }
}
