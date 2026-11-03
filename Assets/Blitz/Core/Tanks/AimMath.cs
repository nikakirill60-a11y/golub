using System;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// Aim math ported from Godot <c>base_tank.gd</c> (<c>rotate_gun</c>,
    /// <c>rotate_turret</c>, <c>adjust_aim</c>, <c>_gun_trace_steps</c>) and from
    /// <c>projectile.gd</c> (<c>lead_gravity_y</c>, <c>get_drop_over_delta</c>).
    /// </summary>
    public static class AimMath
    {
        /// <summary>project.godot: <c>physics/3d/default_gravity = 10.0</c>.</summary>
        public const float DefaultGravity = 10f;

        /// <summary>_global.gd:223 declares <c>PROJECTILES_GRAVITY_MUL: int = 1</c>.</summary>
        public const int ProjectilesGravityMultiplier = 1;

        /// <summary>Both the camera trace and the gun trace run 720 m.</summary>
        public const float TraceDistance = 720f;

        /// <summary>base_tank.gd: the camera ray starts 0.25 m ahead of the lens.</summary>
        public const float CameraTraceOffset = 0.25f;

        /// <summary>base_tank.gd: MOBILE_BALLISTIC_MAX_ERROR_M.</summary>
        public const float MobileBallisticMaxError = 0.01f;

        /// <summary>base_tank.gd: MOBILE_AI_GUN_TRACE_INTERVAL.</summary>
        public const float MobileAiGunTraceInterval = 0.05f;

        /// <summary>Upper bound of <c>_gun_trace_steps</c>, and the desktop count.</summary>
        public const int TraceStepsMax = 48;

        /// <summary>A cheap intermediate AI trace samples four points.</summary>
        public const int TraceStepsCheap = 4;

        /// <summary>
        /// adjust_aim snaps instead of lerping past this squared distance. The
        /// source writes the threshold as <c>50 * 2</c>, not as a squared radius.
        /// </summary>
        public const float AimSnapDistanceSquared = 100f;

        /// <summary>adjust_aim: <c>aim_target_lerp + (aim_target - aim_target_lerp) * delta * 10</c>.</summary>
        public const float AimLerpSpeed = 10f;

        private const float Tau = (float)(2.0 * Math.PI);
        private const float Pi = (float)Math.PI;

        /// <summary>Godot's <c>fposmod</c>: the result carries the divisor's sign.</summary>
        public static float FposMod(float value, float modulus)
        {
            if (modulus == 0f)
            {
                return 0f;
            }

            var result = value % modulus;
            if (result != 0f && (result < 0f) != (modulus < 0f))
            {
                result += modulus;
            }

            return result;
        }

        /// <summary>
        /// Godot's <c>angle_difference</c>, the shortest signed way from one angle
        /// to another. rotate_gun and rotate_turret both spell it out inline as
        /// <c>fposmod(target - current + PI, TAU) - PI</c>.
        /// </summary>
        public static float AngleDifference(float from, float to)
        {
            return FposMod(to - from + Pi, Tau) - Pi;
        }

        /// <summary>
        /// One rotation step toward <paramref name="target"/>. Mirrors the shared
        /// tail of rotate_gun and rotate_turret: land exactly on the target when
        /// the remaining difference fits inside this frame's budget, otherwise
        /// step by the full budget. <c>sign(0)</c> is 0 in Godot, so a zero
        /// difference never nudges the barrel.
        /// </summary>
        public static float RotateTowards(float current, float target, float maxStep)
        {
            var difference = AngleDifference(current, target);
            if (Math.Abs(difference) <= maxStep)
            {
                return target;
            }

            return current + Math.Sign(difference) * maxStep;
        }

        /// <summary>projectile.gd: <c>0.5 * gravity * delta * delta</c>.</summary>
        public static float DropOverDelta(float gravity, float delta)
        {
            return 0.5f * gravity * delta * delta;
        }

        /// <summary>
        /// projectile.gd <c>lead_gravity_y</c>: how far a shell falls on its way to
        /// the aim point, which adjust_aim adds straight onto <c>aim_target.y</c>.
        /// </summary>
        public static float LeadGravityY(
            float distance,
            float projectileSpeed,
            float gravity,
            float maxDistance = TraceDistance)
        {
            if (projectileSpeed == 0f)
            {
                return 0f;
            }

            var clamped = distance;
            if (clamped < 0f)
            {
                clamped = 0f;
            }
            else if (clamped > maxDistance)
            {
                clamped = maxDistance;
            }

            return DropOverDelta(gravity, clamped / projectileSpeed);
        }

        /// <summary>
        /// The shell gravity <c>adjust_aim</c> and <c>gun_trace</c> both build:
        /// <c>PROJECTILES_GRAVITY_MUL * shell.gravity_mul * default_gravity</c>.
        /// Homing shells fly straight, so their trace gravity is zeroed.
        /// </summary>
        public static float ShellGravity(float gravityMultiplier, bool homing)
        {
            if (homing)
            {
                return 0f;
            }

            return ProjectilesGravityMultiplier * gravityMultiplier * DefaultGravity;
        }

        /// <summary>
        /// rotate_turret reports the traverse rate in degrees per second, guarding
        /// the division the same way the source does with <c>maxf(delta, 1e-06)</c>.
        /// </summary>
        public static float TurretAngularVelocity(float previousRotation, float rotation, float delta)
        {
            var difference = AngleDifference(previousRotation, rotation);
            var guarded = delta > 0.000001f ? delta : 0.000001f;
            return Math.Abs(difference * (180f / Pi)) / guarded;
        }

        /// <summary>
        /// base_tank.gd <c>_gun_trace_due</c>. On mobile the cheap AI trace is
        /// cached for <see cref="MobileAiGunTraceInterval"/>, the tanks spread
        /// over that window by their instance id; the player's trace and every
        /// desktop trace keep the per-tick cadence. <paramref name="next"/> is
        /// the tank's <c>_mobile_ai_gun_trace_next</c>, negative before the
        /// first call.
        /// </summary>
        public static bool GunTraceDue(bool mobile, bool cheap, float now, int instanceId, ref float next)
        {
            if (!mobile || !cheap)
            {
                return true;
            }

            if (next < 0f)
            {
                var phase = Math.Abs(instanceId % 1000) * 0.001f * MobileAiGunTraceInterval;
                next = now + phase;
            }

            if (now < next)
            {
                return false;
            }

            next = now + MobileAiGunTraceInterval;
            return true;
        }

        /// <summary>
        /// base_tank.gd <c>_gun_trace_steps</c>. Desktop always pays for the full
        /// sweep; mobile buys just enough samples to keep the ballistic error
        /// under a centimetre.
        /// </summary>
        public static int GunTraceSteps(float flightTime, float gravity, bool cheap, bool mobile)
        {
            if (cheap)
            {
                return TraceStepsCheap;
            }

            if (!mobile)
            {
                return TraceStepsMax;
            }

            var scale = Math.Sqrt(Math.Abs(gravity) / (8.0 * MobileBallisticMaxError));
            var steps = (int)Math.Ceiling(flightTime * scale);
            if (steps < 1)
            {
                return 1;
            }

            return steps > TraceStepsMax ? TraceStepsMax : steps;
        }
    }
}
