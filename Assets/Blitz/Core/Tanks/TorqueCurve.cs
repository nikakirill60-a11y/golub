using System;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// How much torque the engine delivers at a given fraction of top speed is
    /// a hand-drawn curve in the source, stored as a Curve resource inside
    /// base_tank.tscn (Curve_pgggk). It is the difference between a tank that
    /// pulls away hard and then settles, and one that accelerates like a car,
    /// so it is ported as data with the source's own interpolation rather than
    /// approximated with a lerp.
    ///
    /// Godot samples a Curve as a cubic bezier between neighbouring points,
    /// using each point's tangents as control handles. The source calls
    /// <c>bake()</c> on load, but that only fills the cache used by
    /// <c>sample_baked()</c>; the drivetrain calls <c>sample()</c>, which
    /// always evaluates the bezier directly, so baking is deliberately not
    /// reproduced here.
    /// </summary>
    public sealed class TorqueCurve
    {
        /// <summary>Godot's CMP_EPSILON, used for its degenerate-span branch.</summary>
        private const float Epsilon = 1e-05f;

        public readonly struct Point
        {
            public readonly float X;
            public readonly float Y;
            public readonly float LeftTangent;
            public readonly float RightTangent;

            public Point(float x, float y, float leftTangent, float rightTangent)
            {
                X = x;
                Y = y;
                LeftTangent = leftTangent;
                RightTangent = rightTangent;
            }
        }

        private readonly Point[] _points;

        public TorqueCurve(Point[] points)
        {
            if (points == null || points.Length == 0)
            {
                throw new ArgumentException("a torque curve needs at least one point");
            }

            _points = points;
        }

        public int Count
        {
            get { return _points.Length; }
        }

        /// <summary>
        /// The curve exactly as authored in base_tank.tscn: five points, with
        /// tangents only on the first two, which is what gives the steep drop
        /// off the line and the long flat middle.
        /// </summary>
        public static TorqueCurve CreateDefault()
        {
            return new TorqueCurve(new[]
            {
                new Point(0f, 0.84435797f, 0f, -1.5917423f),
                new Point(0.14210527f, 0.6264592f, -2.2613864f, -2.2613864f),
                new Point(0.37368423f, 0.14396894f, 0f, 0f),
                new Point(0.7626811f, 0.10296488f, 0f, 0f),
                new Point(1f, 0.14501333f, 0f, 0f),
            });
        }

        /// <summary>
        /// Reads the curve at <paramref name="offset"/>. Offsets outside the
        /// authored range clamp to the end values, matching the source.
        /// </summary>
        public float Sample(float offset)
        {
            if (_points.Length == 1)
            {
                return _points[0].Y;
            }

            int index = IndexOf(offset);

            if (index == _points.Length - 1)
            {
                return _points[index].Y;
            }

            float local = offset - _points[index].X;

            if (index == 0 && local <= 0f)
            {
                return _points[0].Y;
            }

            return SampleLocal(index, local);
        }

        /// <summary>
        /// The last point at or before the offset. The source binary-searches;
        /// with a handful of authored points a scan gives the same answer and
        /// reads as what it is.
        /// </summary>
        private int IndexOf(float offset)
        {
            int index = 0;

            for (int i = 1; i < _points.Length; i++)
            {
                if (_points[i].X > offset)
                {
                    break;
                }

                index = i;
            }

            return index;
        }

        private float SampleLocal(int index, float localOffset)
        {
            Point a = _points[index];
            Point b = _points[index + 1];

            float span = b.X - a.X;

            if (span > -Epsilon && span < Epsilon)
            {
                return b.Y;
            }

            float t = localOffset / span;
            float third = span / 3f;

            float control1 = a.Y + third * a.RightTangent;
            float control2 = b.Y - third * b.LeftTangent;

            return Bezier(a.Y, control1, control2, b.Y, t);
        }

        private static float Bezier(float start, float control1, float control2, float end, float t)
        {
            float omt = 1f - t;
            float omt2 = omt * omt;
            float omt3 = omt2 * omt;
            float t2 = t * t;
            float t3 = t2 * t;

            return start * omt3 + control1 * omt2 * t * 3f + control2 * omt * t2 * 3f + end * t3;
        }
    }
}
