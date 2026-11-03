using System;

namespace BlitzLegacy.Core.Maps
{
    /// <summary>
    /// The source drivetrain reads <c>base.rotation_degrees.x</c> and
    /// <c>.z</c> to decide how much grip, torque and lean a tilted hull gets,
    /// so those two angles feed the friction, the engine torque and the brake.
    ///
    /// Unity's <c>eulerAngles</c> answers a different question and cannot be
    /// substituted: it decomposes in ZXY order rather than Godot's default
    /// YXZ, it reports 0..360 instead of a signed angle, and it describes the
    /// mirrored hull, so pitch comes back with the opposite sign. Feeding that
    /// into the source formulas would grip slightly wrong on every slope -
    /// wrong in a way that looks like tuning rather than a bug, which is the
    /// worst kind of port error to chase later.
    ///
    /// So the source basis is rebuilt from the mirrored one and decomposed the
    /// way Godot does it, branch for branch.
    /// </summary>
    public static class GodotEuler
    {
        /// <summary>Godot's CMP_EPSILON, used for its gimbal-lock branches.</summary>
        private const float Epsilon = 1e-05f;

        private const float HalfPi = 1.5707963267948966f;
        private const float RadToDeg = 57.29577951308232f;

        /// <summary>
        /// Undoes the mirror, recovering the source basis columns from the
        /// axes of a mirrored transform. This is exactly the inverse of
        /// <see cref="GodotSpace.Basis"/>, and the mirror is its own inverse,
        /// which is why the same sign pattern appears in both directions.
        /// </summary>
        public static void SourceBasis(
            Vec3 right,
            Vec3 up,
            Vec3 forward,
            out Vec3 sourceX,
            out Vec3 sourceY,
            out Vec3 sourceZ)
        {
            sourceX = new Vec3(right.X, right.Y, -right.Z);
            sourceY = new Vec3(up.X, up.Y, -up.Z);
            sourceZ = new Vec3(-forward.X, -forward.Y, forward.Z);
        }

        /// <summary>
        /// Decomposes a source basis, given as its three column vectors, into
        /// degrees in Godot's default YXZ order.
        ///
        /// Godot stores a basis by rows, so column j holds the image of axis j
        /// and <c>rows[i][j]</c> is component i of column j. The branches below
        /// follow Basis::get_euler for EULER_ORDER_YXZ, including its
        /// pure-pitch shortcut, so a hull that only pitches reports no yaw
        /// instead of a numerically equivalent pair of large angles.
        /// </summary>
        public static void Yxz(
            Vec3 sourceX,
            Vec3 sourceY,
            Vec3 sourceZ,
            out float pitchDegrees,
            out float yawDegrees,
            out float rollDegrees)
        {
            float m12 = sourceZ.Y;

            float pitch;
            float yaw;
            float roll;

            if (m12 < 1f - Epsilon)
            {
                if (m12 > -(1f - Epsilon))
                {
                    bool pureRotationAboutX =
                        sourceX.Y == 0f &&
                        sourceY.X == 0f &&
                        sourceZ.X == 0f &&
                        sourceX.Z == 0f &&
                        sourceX.X == 1f;

                    if (pureRotationAboutX)
                    {
                        pitch = (float)Math.Atan2(-m12, sourceY.Y);
                        yaw = 0f;
                        roll = 0f;
                    }
                    else
                    {
                        pitch = Asin(-m12);
                        yaw = (float)Math.Atan2(sourceZ.X, sourceZ.Z);
                        roll = (float)Math.Atan2(sourceX.Y, sourceY.Y);
                    }
                }
                else
                {
                    // Nose straight up: yaw and roll describe the same turn.
                    pitch = HalfPi;
                    yaw = (float)Math.Atan2(sourceY.X, sourceX.X);
                    roll = 0f;
                }
            }
            else
            {
                // Nose straight down.
                pitch = -HalfPi;
                yaw = -(float)Math.Atan2(sourceY.X, sourceX.X);
                roll = 0f;
            }

            pitchDegrees = pitch * RadToDeg;
            yawDegrees = yaw * RadToDeg;
            rollDegrees = roll * RadToDeg;
        }

        /// <summary>
        /// Godot clamps the argument instead of returning NaN, and a hull
        /// basis that drifts a hair past one after a collision must not take
        /// the whole drivetrain down with it.
        /// </summary>
        private static float Asin(float value)
        {
            if (value <= -1f)
            {
                return -HalfPi;
            }

            if (value >= 1f)
            {
                return HalfPi;
            }

            return (float)Math.Asin(value);
        }
    }
}
