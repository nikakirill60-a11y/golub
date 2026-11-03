using System;
using BlitzLegacy.Core.Maps;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// The splash math of Godot <c>Projectile.explode</c>, lifted out of the
    /// scene tree so the runtime and the headless tests share one copy.
    ///
    /// A high-explosive round does not carry its damage through the plate it
    /// hit. It stands where it landed and fires a fan of 8 x 8 rays over the
    /// hemisphere around the surface normal, up to its own splash radius. Every
    /// ray that reaches armour asks <see cref="DmgOverDist"/> what is left of
    /// the warhead at that distance through that thickness, and each tank keeps
    /// the best ray that found it. That is why an HE shell rewards hitting a
    /// thin roof near a crowd instead of the thickest plate on one hull.
    /// </summary>
    public static class ExplosionMath
    {
        /// <summary><c>exp_steps</c>: rays per ring, and rings per hemisphere.</summary>
        public const int Steps = 8;

        /// <summary><c>exp_angle_step</c>: <c>PI / exp_steps</c>.</summary>
        public const float AngleStep = (float)(Math.PI / Steps);

        /// <summary>
        /// <c>spall_liner_coeff</c> with the liner fitted: the source adds 0.15
        /// to the armour term, which is what makes a liner eat splash rather
        /// than direct penetration.
        /// </summary>
        public const float SpallLinerCoefficient = 1.15f;

        /// <summary>Share of the nominal damage splash starts from.</summary>
        public const float DamageFactor = 0.5f;

        /// <summary>How much of a millimetre of armour the splash pays for.</summary>
        public const float ArmorFactor = 1.1f;

        /// <summary>
        /// <c>origin += normal * 0.02</c>: the burst stands just off the
        /// surface, so its own rays do not start inside the plate it hit.
        /// </summary>
        public const float OriginOffset = 0.02f;

        private const float Epsilon = 1e-06f;

        /// <summary>
        /// <c>1 - dist / splash_rad</c>, and 1 at the burst itself. A shell
        /// without a radius at all has nothing to reach past its own impact
        /// point, so everything at a distance from it falls to zero.
        /// </summary>
        public static float DistanceCoefficient(float distance, float splashRadius)
        {
            if (distance <= 0f)
            {
                return 1f;
            }

            if (splashRadius <= 0f)
            {
                return 0f;
            }

            return 1f - distance / splashRadius;
        }

        /// <summary>
        /// <c>dmg_over_dist</c>. Half the nominal damage, faded out over the
        /// splash radius, less 1.1 mm-for-point of the armour the ray met, then
        /// rounded up and multiplied by the shell's own damage roll. The armour
        /// term is flat, which is why a 50 mm plate stops small-calibre splash
        /// outright instead of merely thinning it.
        /// </summary>
        public static int DmgOverDist(
            float nominalDmg,
            float distance,
            float splashRadius,
            int armor,
            float spallCoefficient,
            float roll)
        {
            var fade = DistanceCoefficient(distance, splashRadius);
            var left = DamageFactor * nominalDmg * fade
                - ArmorFactor * armor * spallCoefficient;
            if (!(left > 0f))
            {
                return 0;
            }

            var dmg = (float)Math.Ceiling(left) * roll;
            return dmg > 0f ? (int)dmg : 0;
        }

        /// <summary>
        /// Rays the fan spends: one hemisphere around the impact normal, or the
        /// whole sphere when the source passes <c>half_sphere = false</c>, which
        /// is what a burst on spaced armour does.
        /// </summary>
        public static int RingCount(bool halfSphere)
        {
            return halfSphere ? Steps : Steps * 2;
        }

        /// <summary>
        /// One ray of the fan. The source builds its first spoke as
        /// <c>normal.cross(vel.normalized())</c>, spins it <c>j</c> steps about
        /// the normal and then <c>i</c> steps about that same first spoke, which
        /// it keeps as <c>rotator</c> for the whole fan.
        ///
        /// A round that arrives exactly down the normal has no cross product to
        /// start from. The source jitters the normal by a random hair; the port
        /// picks a fixed perpendicular instead, so the fan stays deterministic
        /// and a replayed shot lands the same damage twice.
        /// </summary>
        public static Vec3 RayDirection(
            Vec3 normal, Vec3 velocityDirection, int ring, int spoke)
        {
            var axis = Normalized(normal);
            var spokeZero = Normalized(Cross(axis, Normalized(velocityDirection)));
            if (Length(spokeZero) <= Epsilon)
            {
                spokeZero = Perpendicular(axis);
            }

            var rotated = Rotated(spokeZero, axis, AngleStep * spoke);
            return Rotated(rotated, spokeZero, AngleStep * ring);
        }

        /// <summary>Any unit vector at right angles to <paramref name="v"/>.</summary>
        public static Vec3 Perpendicular(Vec3 v)
        {
            var axis = Math.Abs(v.X) < 0.9f
                ? new Vec3(1f, 0f, 0f)
                : new Vec3(0f, 1f, 0f);
            return Normalized(Cross(v, axis));
        }

        public static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        public static float Dot(Vec3 a, Vec3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        public static float Length(Vec3 v)
        {
            return (float)Math.Sqrt(Dot(v, v));
        }

        public static Vec3 Normalized(Vec3 v)
        {
            var length = Length(v);
            if (length <= Epsilon)
            {
                return new Vec3(0f, 0f, 0f);
            }

            return new Vec3(v.X / length, v.Y / length, v.Z / length);
        }

        /// <summary>
        /// <c>Vector3.rotated</c>: Rodrigues' rotation about a unit axis.
        /// </summary>
        public static Vec3 Rotated(Vec3 v, Vec3 axis, float angle)
        {
            var unit = Normalized(axis);
            if (Length(unit) <= Epsilon)
            {
                return v;
            }

            var cos = (float)Math.Cos(angle);
            var sin = (float)Math.Sin(angle);
            var cross = Cross(unit, v);
            var dot = Dot(unit, v);
            return new Vec3(
                v.X * cos + cross.X * sin + unit.X * dot * (1f - cos),
                v.Y * cos + cross.Y * sin + unit.Y * dot * (1f - cos),
                v.Z * cos + cross.Z * sin + unit.Z * dot * (1f - cos));
        }
    }
}
