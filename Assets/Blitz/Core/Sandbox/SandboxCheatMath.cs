using System;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Sandbox
{
    /// <summary>
    /// The engine-free arithmetic of training_cheat_controller.gd: target
    /// priority, aim smoothing, the triggerbot penetration estimate and the
    /// wallhack band. The runtime controller feeds it positions and angles.
    /// </summary>
    public static class SandboxCheatMath
    {
        /// <summary>WALLHACK_REFRESH: the outline pass runs at most every 50 ms.</summary>
        public const float WallhackRefresh = 0.05f;

        /// <summary>WALLHACK_OUTLINE_WIDTH.</summary>
        public const float WallhackOutlineWidth = 9f;

        /// <summary>The width <c>_clear_wallhack_target</c> restores.</summary>
        public const float NormalOutlineWidth = 4f;

        /// <summary>A target closer than this many metres has no distance (aim sanity check).</summary>
        public const float MinTargetDistance = 0.001f;

        /// <summary>The triggerbot holds fire while the hull is faster than this (|speed|).</summary>
        public const float StillSpeed = 0.5f;

        /// <summary>
        /// <c>_target_score</c>: lower is better. Crosshair priority prefers the
        /// smallest angle, distance the nearest tank, low health the weakest.
        /// </summary>
        public static float TargetScore(SandboxAimbotPriority priority, float angleDegrees,
            float distance, float health, float maxHealth)
        {
            switch (priority)
            {
                case SandboxAimbotPriority.Distance:
                    return distance + angleDegrees * 0.01f;
                case SandboxAimbotPriority.LowHealth:
                    var ratio = health / Math.Max(maxHealth, 1f);
                    return ratio * 10000f + angleDegrees * 10f + distance * 0.01f;
                default:
                    return angleDegrees * 1000f + distance;
            }
        }

        /// <summary>
        /// <c>aim_override</c>'s exponential smoothing factor: zero strength
        /// snaps instantly, otherwise <c>1 - e^(-strength * delta)</c>.
        /// </summary>
        public static float SmoothingAlpha(float strength, float delta)
        {
            if (strength <= 0f) { return 1f; }
            var alpha = 1f - (float)Math.Exp(-strength * Math.Max(delta, 0f));
            return Clamp01(alpha);
        }

        /// <summary>Whether a target sits inside the aimbot cone (full FOV angle).</summary>
        public static bool InsideFov(float angleDegrees, float fovDegrees)
        {
            return angleDegrees <= fovDegrees * 0.5f;
        }

        /// <summary>
        /// Shell flight time used for the lead: distance over muzzle speed,
        /// or zero when the lead is off or the shell does not travel.
        /// </summary>
        public static float LeadTime(float distance, float shellSpeed, float lead)
        {
            if (lead <= 0f || shellSpeed <= 0f) { return 0f; }
            return distance / shellSpeed * lead;
        }

        /// <summary>
        /// The wallhack band: tanks nearer than the close distance are left to
        /// the ordinary spotting, tanks beyond the max distance are ignored.
        /// </summary>
        public static bool InWallhackBand(float distance, float close, float max)
        {
            return distance > close && distance <= max;
        }

        /// <summary>
        /// <c>trace_can_penetrate</c>: a deliberately simple estimate - the
        /// triggerbot only has to avoid obviously bouncing shots. No plate,
        /// a high-explosive shell, no armour or a three-calibre overmatch all
        /// pass; otherwise penetration has to beat the line-of-sight armour
        /// after the shell's normalisation.
        /// </summary>
        /// <param name="incidenceRadians">Angle between the shot and the plate normal,
        /// or a negative value when the trace has no normal.</param>
        public static bool CanPenetrate(ShellInfo shell, bool hasPlate, float armor, float incidenceRadians)
        {
            if (!hasPlate || shell == null || ArmorMath.IsExplosive(shell.Type)) { return true; }
            if (armor <= 0f) { return true; }
            if (shell.Caliber > armor * 3f) { return true; }
            if (incidenceRadians < 0f) { return shell.Penetration >= armor; }
            var normalized = Math.Max(0f, incidenceRadians
                - ArmorMath.Normalization(shell.Type) * (float)(Math.PI / 180.0));
            var effective = armor / Math.Max((float)Math.Cos(normalized), 0.01f);
            return shell.Penetration >= effective;
        }

        /// <summary>
        /// The incidence the penetration check uses: <c>acos</c> of the cosine
        /// between the reversed shot and the normal, clamped to [0.001, 1].
        /// </summary>
        public static float Incidence(float cosine)
        {
            var clamped = Math.Min(1f, Math.Max(0.001f, cosine));
            return (float)Math.Acos(clamped);
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }
    }
}
