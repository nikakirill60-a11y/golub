using System;
using UnityEngine;

namespace BlitzLegacy.Runtime.Chest
{
    /// <summary>
    /// DAVA AnimationSystem playback of the six box parts. A part's world
    /// matrix is invPose * Key(t) * local * parentWorld (row vectors), where
    /// Key = QuaternionMatrix * Scale with the translation in row 3. Keys are
    /// sampled like AnimationData::Interpolate: the last key holds from its
    /// time on, and Quaternion::Slerp falls back to an un-normalised lerp for
    /// nearly equal rotations (replicated on purpose).
    /// </summary>
    public static class Chest480Animation
    {
        /// <summary>Row-major DAVA world matrix of <paramref name="part"/> at <paramref name="time"/>.</summary>
        public static float[] World(Chest480Part part, float time)
        {
            var result = new float[16];
            World(part, time, result);
            return result;
        }

        public static void World(Chest480Part part, float time, float[] result)
        {
            if (part == null) throw new ArgumentNullException("part");
            var times = part.times;
            var count = times.Length;
            int i, j;
            double ti;
            if (time >= times[count - 1] || count == 1)
            {
                i = j = count - 1;
                ti = 0d;
            }
            else
            {
                i = 0;
                while (i + 1 < count && times[i + 1] <= time) i++;
                j = i + 1;
                ti = (time - times[i]) / (double)(times[j] - times[i]);
                if (ti < 0d) ti = 0d;
            }

            var key = new double[16];
            var q = Slerp(part.rotations, i, j, ti);
            var rotation = QuaternionMatrix(q[0], q[1], q[2], q[3]);
            for (var c = 0; c < 3; c++)
            {
                var scale = Lerp(part.scales[i * 3 + c], part.scales[j * 3 + c], ti);
                for (var r = 0; r < 3; r++) key[r * 4 + c] = rotation[r * 3 + c] * scale;
                key[12 + c] = Lerp(part.translations[i * 3 + c], part.translations[j * 3 + c], ti);
            }
            key[15] = 1d;

            var animated = Multiply(ToDouble(part.invPose), key);
            var world = Multiply(Multiply(animated, ToDouble(part.local)), ToDouble(part.parentWorld));
            for (var k = 0; k < 16; k++) result[k] = (float)world[k];
        }

        /// <summary>DAVA Quaternion::GetMatrix for a row-vector engine (3x3, row-major).</summary>
        private static double[] QuaternionMatrix(double x, double y, double z, double w)
        {
            return new[]
            {
                1d - 2d * (y * y + z * z), 2d * (x * y + z * w), 2d * (x * z - y * w),
                2d * (x * y - z * w), 1d - 2d * (x * x + z * z), 2d * (y * z + x * w),
                2d * (x * z + y * w), 2d * (y * z - x * w), 1d - 2d * (x * x + y * y),
            };
        }

        private static double[] Slerp(float[] rotations, int i, int j, double t)
        {
            double ax = rotations[i * 4], ay = rotations[i * 4 + 1], az = rotations[i * 4 + 2], aw = rotations[i * 4 + 3];
            double bx = rotations[j * 4], by = rotations[j * 4 + 1], bz = rotations[j * 4 + 2], bw = rotations[j * 4 + 3];
            var cosine = ax * bx + ay * by + az * bz + aw * bw;
            if (cosine < 0d)
            {
                cosine = -cosine;
                bx = -bx; by = -by; bz = -bz; bw = -bw;
            }
            double ka, kb;
            if (1d - cosine > 0.05d)
            {
                var angle = Math.Acos(Math.Min(1d, cosine));
                var sine = Math.Sin(angle);
                ka = Math.Sin((1d - t) * angle) / sine;
                kb = Math.Sin(t * angle) / sine;
            }
            else
            {
                ka = 1d - t;
                kb = t;
            }
            return new[] { ka * ax + kb * bx, ka * ay + kb * by, ka * az + kb * bz, ka * aw + kb * bw };
        }

        private static double Lerp(float a, float b, double t)
        {
            return a * (1d - t) + b * t;
        }

        private static double[] ToDouble(float[] m)
        {
            var result = new double[16];
            for (var k = 0; k < 16; k++) result[k] = m[k];
            return result;
        }

        private static double[] Multiply(double[] a, double[] b)
        {
            var result = new double[16];
            for (var r = 0; r < 4; r++)
                for (var c = 0; c < 4; c++)
                {
                    double sum = 0d;
                    for (var k = 0; k < 4; k++) sum += a[r * 4 + k] * b[k * 4 + c];
                    result[r * 4 + c] = sum;
                }
            return result;
        }

        /// <summary>
        /// Convert a row-major DAVA matrix into the Unity column-vector matrix
        /// acting on Unity axes: U[r,c] = W[s(c), s(r)], where s swaps 1 and 2.
        /// </summary>
        public static Matrix4x4 ToUnity(float[] w)
        {
            var result = new Matrix4x4();
            for (var r = 0; r < 4; r++)
                for (var c = 0; c < 4; c++)
                    result[r, c] = w[Swap(c) * 4 + Swap(r)];
            return result;
        }

        private static int Swap(int axis)
        {
            return axis == 1 ? 2 : axis == 2 ? 1 : axis;
        }
    }
}
