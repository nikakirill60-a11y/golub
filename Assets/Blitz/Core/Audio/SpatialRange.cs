using System;

namespace BlitzLegacy.Core.Audio
{
    /// <summary>
    /// The two distances a spatial voice is authored with: full level within
    /// <see cref="Reference"/> metres (Godot's <c>unit_size</c>), the
    /// inverse-distance law beyond it, and silence from <see cref="Max"/>
    /// metres on (Godot's <c>max_distance</c>, a plain metre cut).
    /// </summary>
    public readonly struct SpatialRange : IEquatable<SpatialRange>
    {
        /// <summary>The shortest accepted reference distance, in metres.</summary>
        public const float MinReference = 0.1f;

        /// <summary>The cut is kept at least this many references out, so the fade has room.</summary>
        public const float MinSpan = 2f;

        public readonly float Reference;
        public readonly float Max;

        public SpatialRange(float reference, float max)
        {
            Reference = reference >= MinReference ? reference : MinReference;
            Max = max >= Reference * MinSpan ? max : Reference * MinSpan;
        }

        /// <summary>A default-constructed range carries no distances at all.</summary>
        public bool IsEmpty { get { return Max <= 0f; } }

        public bool Equals(SpatialRange other)
        {
            return Reference.Equals(other.Reference) && Max.Equals(other.Max);
        }

        public override bool Equals(object obj)
        {
            return obj is SpatialRange other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (Reference.GetHashCode() * 397) ^ Max.GetHashCode();
        }

        public override string ToString()
        {
            return Reference + ".." + Max + " m";
        }
    }
}
