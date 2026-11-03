using System;

namespace BlitzLegacy.Runtime.Chest
{
    /// <summary>
    /// DAVA PropertyLineKeyframes / ModifiablePropertyLine evaluation
    /// (Particles/ParticlePropertyLine.h), in float32 like the engine.
    /// A null line (no keys) is reported through <see cref="IsNull"/>; callers
    /// apply DAVA's per-property defaults exactly where the engine checks for
    /// a null pointer.
    /// </summary>
    public sealed class DavaPropertyLine
    {
        private readonly float[] _times;
        private readonly float[] _values;
        private readonly float[] _modTimes;
        private readonly float[] _modValues;
        private readonly float[] _modifier;
        private readonly float[] _scratch;

        public int Dimension { get; private set; }
        public string External { get; private set; }
        public bool IsNull { get { return _times.Length == 0; } }
        public int KeyCount { get { return _times.Length; } }

        public DavaPropertyLine(Chest480Line line)
        {
            if (line == null) throw new ArgumentNullException("line");
            if (line.dim < 1 || line.dim > 4) throw new ArgumentOutOfRangeException("line", "Unsupported dimension");
            Dimension = line.dim;
            _times = line.t ?? new float[0];
            _values = line.v ?? new float[0];
            if (_values.Length != _times.Length * Dimension) throw new ArgumentException("Property line values do not match its keys");
            External = line.external ?? string.Empty;
            _modTimes = line.mt ?? new float[0];
            _modValues = line.mv ?? new float[0];
            if (_modValues.Length != _modTimes.Length * Dimension) throw new ArgumentException("Modification line values do not match its keys");
            _modifier = new float[Dimension];
            _scratch = new float[Dimension];
            for (var i = 0; i < Dimension; i++) _modifier[i] = 1f;
        }

        public bool IsModifiable { get { return External.Length > 0; } }

        /// <summary>ModifiablePropertyLine::SetModifier: modifier = modLine(v), or one.</summary>
        public void SetExternalValue(float value)
        {
            if (!IsModifiable) return;
            if (_modTimes.Length == 0)
            {
                for (var i = 0; i < Dimension; i++) _modifier[i] = 1f;
                return;
            }
            Evaluate(_modTimes, _modValues, Dimension, value, _modifier);
        }

        /// <summary>Evaluate into <paramref name="result"/> (length >= Dimension).</summary>
        public void Evaluate(float t, float[] result)
        {
            if (IsNull)
            {
                for (var i = 0; i < Dimension; i++) result[i] = 0f;
                return;
            }
            Evaluate(_times, _values, Dimension, t, result);
            if (IsModifiable)
                for (var i = 0; i < Dimension; i++) result[i] = _modifier[i] * result[i];
        }

        public float Scalar(float t)
        {
            Evaluate(t, _scratch);
            return _scratch[0];
        }

        public DavaVector2 Vector2(float t)
        {
            Evaluate(t, _scratch);
            return new DavaVector2(_scratch[0], _scratch[1]);
        }

        public DavaVector3 Vector3(float t)
        {
            Evaluate(t, _scratch);
            return new DavaVector3(_scratch[0], _scratch[1], _scratch[2]);
        }

        public DavaColor Color(float t)
        {
            Evaluate(t, _scratch);
            return new DavaColor(_scratch[0], _scratch[1], _scratch[2], _scratch[3]);
        }

        /// <summary>PropertyLineKeyframes::GetValue with its BinaryFind segment rule.</summary>
        public static void Evaluate(float[] times, float[] values, int dim, float t, float[] result)
        {
            var count = times.Length;
            if (count == 0) throw new InvalidOperationException("Null property line");
            if (t > times[count - 1])
            {
                Copy(values, count - 1, dim, result);
                return;
            }
            if (t <= times[0] || count == 1)
            {
                Copy(values, 0, dim, result);
                return;
            }
            if (count == 2)
            {
                if (t < times[1]) Lerp(times, values, 0, dim, t, result);
                else Copy(values, 1, dim, result);
                return;
            }
            int l = 0, r = count - 1;
            while (l + 1 != r)
            {
                var m = (l + r) >> 1;
                if (t <= times[m]) r = m;
                else l = m;
            }
            Lerp(times, values, l, dim, t, result);
        }

        private static void Copy(float[] values, int key, int dim, float[] result)
        {
            for (var i = 0; i < dim; i++) result[i] = values[key * dim + i];
        }

        private static void Lerp(float[] times, float[] values, int key, int dim, float t, float[] result)
        {
            var ti = (t - times[key]) / (times[key + 1] - times[key]);
            for (var i = 0; i < dim; i++)
            {
                var a = values[key * dim + i];
                var b = values[(key + 1) * dim + i];
                result[i] = a + (b - a) * ti;
            }
        }
    }

    public struct DavaVector2
    {
        public float x, y;
        public DavaVector2(float x, float y) { this.x = x; this.y = y; }
    }

    public struct DavaColor
    {
        public float r, g, b, a;
        public DavaColor(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static readonly DavaColor White = new DavaColor(1f, 1f, 1f, 1f);
    }

    /// <summary>A DAVA-space vector (right-handed, Z up); never a Unity position.</summary>
    public struct DavaVector3
    {
        public float x, y, z;
        public DavaVector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static DavaVector3 From(float[] values, int offset = 0)
        {
            return new DavaVector3(values[offset], values[offset + 1], values[offset + 2]);
        }

        public static DavaVector3 operator +(DavaVector3 a, DavaVector3 b) { return new DavaVector3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static DavaVector3 operator -(DavaVector3 a, DavaVector3 b) { return new DavaVector3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static DavaVector3 operator -(DavaVector3 a) { return new DavaVector3(-a.x, -a.y, -a.z); }
        public static DavaVector3 operator *(DavaVector3 a, float s) { return new DavaVector3(a.x * s, a.y * s, a.z * s); }

        public float SquareLength { get { return x * x + y * y + z * z; } }
        public float Length { get { return (float)Math.Sqrt(SquareLength); } }

        public float Dot(DavaVector3 b) { return x * b.x + y * b.y + z * b.z; }

        public DavaVector3 Cross(DavaVector3 b)
        {
            return new DavaVector3(y * b.z - z * b.y, z * b.x - x * b.z, x * b.y - y * b.x);
        }

        /// <summary>Vector3::Normalize: a zero vector stays zero.</summary>
        public DavaVector3 Normalized()
        {
            var length = Length;
            return length > 0f ? this * (1f / length) : this;
        }

        /// <summary>v * M with the upper 3x3 of a row-major DAVA matrix.</summary>
        public DavaVector3 MulRotation(float[] m)
        {
            return new DavaVector3(
                x * m[0] + y * m[4] + z * m[8],
                x * m[1] + y * m[5] + z * m[9],
                x * m[2] + y * m[6] + z * m[10]);
        }

        /// <summary>v * M for a 3x3 row-major matrix.</summary>
        public DavaVector3 Mul3(float[] m)
        {
            return new DavaVector3(
                x * m[0] + y * m[3] + z * m[6],
                x * m[1] + y * m[4] + z * m[7],
                x * m[2] + y * m[5] + z * m[8]);
        }

        /// <summary>Unity axes: DAVA (x, y, z) is Unity (x, z, y).</summary>
        public UnityEngine.Vector3 ToUnity() { return new UnityEngine.Vector3(x, z, y); }
    }
}
