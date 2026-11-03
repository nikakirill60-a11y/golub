using System;

namespace BlitzLegacy.Core.Audio
{
    /// <summary>
    /// How a spatial battle voice is heard. Godot listened from the current
    /// camera with an inverse-distance law (<c>unit_size</c>) that stopped dead
    /// at <c>max_distance</c>. This keeps the law but eases it to silence over
    /// the last <see cref="FadeShare"/> of the range, rolls the top end off
    /// with distance (air absorption) and muffles voices behind the static
    /// world: quieter and darker the more of it lies in between.
    /// </summary>
    public static class SpatialAudio
    {
        /// <summary>The last share of a range over which a voice fades out instead of a hard cut.</summary>
        public const float FadeShare = 0.3f;

        /// <summary>A clear voice: the attenuation low-pass of the source, i.e. no filtering.</summary>
        public const float OpenCutoffHz = TankSoundTuning.SpatialFilterMaxHz;

        /// <summary>Air absorption: the low-pass corner a voice reaches at its maximum distance.</summary>
        public const float AirCutoffHz = 6000f;

        /// <summary>Behind a full obstacle a voice keeps this low-pass corner and gain.</summary>
        public const float OccludedCutoffHz = 600f;
        public const float OccludedGain = 0.2f;

        /// <summary>
        /// Any obstacle on the line muffles at least this much (sound still bends
        /// round a thin wall); <see cref="FullOcclusionThickness"/> metres of it
        /// muffle fully.
        /// </summary>
        public const float MinOcclusion = 0.4f;
        public const float FullOcclusionThickness = 15f;

        /// <summary>Time constant of the occlusion glide: a tank slides behind a house, it does not switch.</summary>
        public const float SmoothingSeconds = 0.15f;

        /// <summary>
        /// Remote engine, track and roughness beds. create_player(): unit_size 50
        /// with a 50 m cut; they now ease out instead of stopping dead at 50 m.
        /// </summary>
        public static readonly SpatialRange TankBeds = new SpatialRange(25f, 120f);

        /// <summary>
        /// The muzzle report inside gun_sound_near_dist (300 m): gun_player
        /// unit_size 100, max_distance 400. Near and distant reports share the
        /// reference so a shot sounds no louder for crossing the 300 m switch.
        /// </summary>
        public static readonly SpatialRange GunNear = new SpatialRange(80f, 450f);

        /// <summary>The distant report: max_distance gun_sound_near_dist * 10, cut at the largest maps' reach.</summary>
        public static readonly SpatialRange GunFar = new SpatialRange(80f, 1200f);

        /// <summary>
        /// The destruction blast inside exp_sound_near_dist (300 m):
        /// explosion_player unit_size 150, max_distance 250, which left blasts
        /// between 250 and 300 m silent in the source.
        /// </summary>
        public static readonly SpatialRange ExplosionNear = new SpatialRange(100f, 450f);

        /// <summary>The distant blast: max_distance exp_sound_near_dist * 10, cut at the largest maps' reach.</summary>
        public static readonly SpatialRange ExplosionFar = new SpatialRange(100f, 1200f);

        /// <summary>Hull collisions: collision_player unit_size 100, max_distance 30.</summary>
        public static readonly SpatialRange Collision = new SpatialRange(15f, 60f);

        /// <summary>
        /// play_sound3d() one-shots (impacts, rams): AudioStreamPlayer3D defaults,
        /// unit_size 10 and no cut at all.
        /// </summary>
        public static readonly SpatialRange OneShot = new SpatialRange(10f, 500f);

        /// <summary>
        /// The rocket's tracer bed rides the rocket on play_sound3d defaults; it is
        /// cut short so a rocket across the map does not hum at the listener.
        /// </summary>
        public static readonly SpatialRange AtgmFlight = new SpatialRange(8f, 180f);

        /// <summary>Uprising cues: the decoded Wwise media ship without attenuation data.</summary>
        public static readonly SpatialRange UprisingCue = new SpatialRange(10f, 250f);

        /// <summary>A Big Boss cue reaches its event's Wwise maxAttenuation; full level within 10 m.</summary>
        public static SpatialRange Cue(float maxAttenuation)
        {
            return new SpatialRange(10f, maxAttenuation);
        }

        /// <summary>
        /// The level at <paramref name="distance"/> metres: 1 within the
        /// reference, then reference / distance, eased to 0 at the maximum.
        /// </summary>
        public static float DistanceGain(float distance, SpatialRange range)
        {
            if (range.IsEmpty || !(distance > range.Reference)) { return 1f; }
            if (distance >= range.Max) { return 0f; }

            var gain = range.Reference / distance;
            var fadeStart = range.Max * (1f - FadeShare);
            if (distance > fadeStart)
            {
                gain *= SmoothStep((range.Max - distance) / (range.Max - fadeStart));
            }

            return gain;
        }

        /// <summary>
        /// The air absorption low-pass corner at <paramref name="distance"/>:
        /// open within the reference, sliding (in octaves) to
        /// <see cref="AirCutoffHz"/> at the maximum.
        /// </summary>
        public static float AirCutoff(float distance, SpatialRange range)
        {
            if (range.IsEmpty || !(distance > range.Reference)) { return OpenCutoffHz; }
            var share = (distance - range.Reference) / (range.Max - range.Reference);
            return LogLerp(OpenCutoffHz, AirCutoffHz, Clamp01(share));
        }

        /// <summary>
        /// How muffled a voice is, 0 (clear) to 1, when the line to the listener
        /// is <paramref name="blocked"/> by <paramref name="thickness"/> metres
        /// of static world.
        /// </summary>
        public static float Occlusion(bool blocked, float thickness)
        {
            if (!blocked) { return 0f; }
            var share = thickness > 0f ? thickness / FullOcclusionThickness : 0f;
            return MinOcclusion + (1f - MinOcclusion) * Clamp01(share);
        }

        /// <summary>The gain an occlusion amount leaves.</summary>
        public static float OcclusionGain(float occlusion)
        {
            return 1f + (OccludedGain - 1f) * Clamp01(occlusion);
        }

        /// <summary>The low-pass corner an occlusion amount leaves.</summary>
        public static float OcclusionCutoff(float occlusion)
        {
            return LogLerp(OpenCutoffHz, OccludedCutoffHz, Clamp01(occlusion));
        }

        /// <summary>One exponential glide step toward <paramref name="target"/>.</summary>
        public static float Smooth(
            float current, float target, float deltaTime, float seconds = SmoothingSeconds)
        {
            if (!(deltaTime > 0f)) { return current; }
            if (!(seconds > 0f)) { return target; }
            return target + (current - target) * (float)Math.Exp(-deltaTime / seconds);
        }

        /// <summary>The coefficient of one one-pole low-pass stage at this corner and mix rate.</summary>
        public static float LowPassCoefficient(float cutoffHz, int sampleRate)
        {
            var rate = sampleRate > 0 ? sampleRate : AudioOutput.SampleRate;
            var corner = cutoffHz > 1f ? cutoffHz : 1f;
            return 1f - (float)Math.Exp(-2.0 * Math.PI * corner / rate);
        }

        private static float SmoothStep(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private static float LogLerp(float from, float to, float t)
        {
            return (float)Math.Exp(Math.Log(from) + (Math.Log(to) - Math.Log(from)) * t);
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }
    }
}
