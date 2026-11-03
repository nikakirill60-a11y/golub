using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.CombatFx
{
    /// <summary>
    /// A Godot <c>Transform3D</c> kept in Godot's own space: three basis
    /// columns and an origin. The combat effects are simulated in that space
    /// and only the finished vertices cross the Z mirror into Unity, so every
    /// formula below is the GLSL of the source verbatim.
    /// </summary>
    public struct GodotXform
    {
        public Vector3 X, Y, Z, Origin;

        public static readonly GodotXform Identity = new GodotXform
        {
            X = new Vector3(1f, 0f, 0f),
            Y = new Vector3(0f, 1f, 0f),
            Z = new Vector3(0f, 0f, 1f),
            Origin = Vector3.zero,
        };

        public GodotXform(Vector3 x, Vector3 y, Vector3 z, Vector3 origin)
        {
            X = x; Y = y; Z = z; Origin = origin;
        }

        /// <summary><c>xform * vec4(p, 1)</c>.</summary>
        public Vector3 Point(Vector3 p)
        {
            return X * p.x + Y * p.y + Z * p.z + Origin;
        }

        /// <summary><c>xform * vec4(v, 0)</c>.</summary>
        public Vector3 Vector(Vector3 v)
        {
            return X * v.x + Y * v.y + Z * v.z;
        }

        /// <summary><c>Basis::xform_inv</c>: the transposed basis, as Godot uses it.</summary>
        public Vector3 VectorTransposed(Vector3 v)
        {
            return new Vector3(Vector3.Dot(X, v), Vector3.Dot(Y, v), Vector3.Dot(Z, v));
        }

        /// <summary><c>a * b</c>.</summary>
        public static GodotXform operator *(GodotXform a, GodotXform b)
        {
            return new GodotXform(a.Vector(b.X), a.Vector(b.Y), a.Vector(b.Z), a.Point(b.Origin));
        }

        /// <summary>A basis scaled uniformly, origin kept.</summary>
        public GodotXform Scaled(float s)
        {
            return new GodotXform(X * s, Y * s, Z * s, Origin);
        }

        /// <summary>
        /// The Godot transform of a Unity world matrix under the project's
        /// Z mirror (<c>GodotSpace</c>): G = M U M with M = diag(1, 1, -1).
        /// </summary>
        public static GodotXform FromUnity(Matrix4x4 m)
        {
            var x = m.GetColumn(0);
            var y = m.GetColumn(1);
            var z = m.GetColumn(2);
            var o = m.GetColumn(3);
            return new GodotXform(
                new Vector3(x.x, x.y, -x.z),
                new Vector3(y.x, y.y, -y.z),
                new Vector3(-z.x, -z.y, z.z),
                new Vector3(o.x, o.y, -o.z));
        }

        /// <summary>A Unity position and rotation with a unit scale, in Godot space.</summary>
        public static GodotXform FromUnity(Vector3 position, Quaternion rotation)
        {
            return FromUnity(Matrix4x4.TRS(position, rotation, Vector3.one));
        }
    }

    /// <summary>
    /// Pure helpers shared by the particle and trail ports: the hash and the
    /// Park-Miller generator of <c>ParticleProcessMaterial</c>, the spread
    /// cone, the mirror between Godot and Unity space and the filtered lookups
    /// of the curve and gradient textures the source bakes.
    /// </summary>
    public static class GodotFxMath
    {
        /// <summary>The <c>pi</c> literal the generated shaders use.</summary>
        public const float ShaderPi = 3.14159f;

        /// <summary>Godot space to Unity space and back (the mirror is its own inverse).</summary>
        public static Vector3 Mirror(Vector3 v)
        {
            return new Vector3(v.x, v.y, -v.z);
        }

        /// <summary><c>hash(uint)</c> of particles.glsl and the process shader.</summary>
        public static uint Hash(uint x)
        {
            unchecked
            {
                x = ((x >> 16) ^ x) * 73244475u;
                x = ((x >> 16) ^ x) * 73244475u;
                x = (x >> 16) ^ x;
                return x;
            }
        }

        /// <summary><c>rand_from_seed</c>: Park-Miller on the GPU's int arithmetic.</summary>
        public static float Rand(ref uint seed)
        {
            unchecked
            {
                var s = (int)seed;
                if (s == 0)
                {
                    s = 305420679;
                }

                var k = s / 127773;
                s = 16807 * (s - k * 127773) - 2836 * k;
                if (s < 0)
                {
                    s += 2147483647;
                }

                seed = (uint)s;
                return (seed % 65536u) / 65535f;
            }
        }

        /// <summary><c>rand_from_seed_m1_p1</c>.</summary>
        public static float RandSigned(ref uint seed)
        {
            return Rand(ref seed) * 2f - 1f;
        }

        /// <summary>GLSL <c>mix</c>.</summary>
        public static float Mix(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        /// <summary>GLSL <c>sign</c>.</summary>
        public static float Sign(float v)
        {
            return v > 0f ? 1f : v < 0f ? -1f : 0f;
        }

        /// <summary>GLSL <c>normalize</c> (NaN for a zero vector, as on the GPU).</summary>
        public static Vector3 Normalize(Vector3 v)
        {
            var length = Mathf.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
            return v / length;
        }

        /// <summary><c>normalize_or_else</c> of the process shader.</summary>
        public static Vector3 NormalizeOrElse(Vector3 v, Vector3 fallback)
        {
            if (v.x == 0f && v.y == 0f && v.z == 0f)
            {
                return fallback;
            }

            return Normalize(v);
        }

        /// <summary>GLSL <c>length</c>.</summary>
        public static float Length(Vector3 v)
        {
            return Mathf.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        }

        /// <summary>
        /// <c>get_random_direction_from_spread</c> for 3D particles: two
        /// draws, a cone of <paramref name="spreadDegrees"/> squashed by
        /// <paramref name="flatness"/>, rotated onto <paramref name="direction"/>.
        /// </summary>
        public static Vector3 SpreadDirection(ref uint seed, float spreadDegrees, float flatness, Vector3 direction)
        {
            var spreadRad = spreadDegrees * (ShaderPi / 180f);
            var angle1 = RandSigned(ref seed) * spreadRad;
            var angle2 = RandSigned(ref seed) * spreadRad * (1f - flatness);
            var xz = new Vector3(Mathf.Sin(angle1), 0f, Mathf.Cos(angle1));
            var yz = new Vector3(0f, Mathf.Sin(angle2), Mathf.Cos(angle2));
            yz.z = yz.z / Mathf.Max(0.0001f, Mathf.Sqrt(Mathf.Abs(yz.z)));
            var spread = new Vector3(xz.x * yz.z, yz.y, xz.z * yz.z);
            var nrm = Length(direction) > 0f ? Normalize(direction) : new Vector3(0f, 0f, 1f);
            var binormal = Vector3.Cross(new Vector3(0f, 1f, 0f), nrm);
            if (Length(binormal) < 0.0001f)
            {
                binormal = new Vector3(0f, 0f, 1f);
            }

            binormal = Normalize(binormal);
            var normal = Vector3.Cross(binormal, nrm);
            return Normalize(binormal * spread.x + normal * spread.y + nrm * spread.z);
        }

        /// <summary>
        /// A linear, clamped lookup into a one-row texture of
        /// <paramref name="width"/> texels at <c>vec2(t)</c>, the way a
        /// <c>texture()</c> call filters a baked <c>CurveTexture</c>.
        /// </summary>
        public static float SampleRow(float[] texels, float t)
        {
            var width = texels.Length;
            var x = t * width - 0.5f;
            var i0 = Mathf.FloorToInt(x);
            var f = x - i0;
            var a = texels[Mathf.Clamp(i0, 0, width - 1)];
            var b = texels[Mathf.Clamp(i0 + 1, 0, width - 1)];
            return a + (b - a) * f;
        }

        /// <summary><see cref="SampleRow(float[], float)"/> for a gradient's RGBA texels.</summary>
        public static Vector4 SampleRow(Vector4[] texels, float t)
        {
            var width = texels.Length;
            var x = t * width - 0.5f;
            var i0 = Mathf.FloorToInt(x);
            var f = x - i0;
            var a = texels[Mathf.Clamp(i0, 0, width - 1)];
            var b = texels[Mathf.Clamp(i0 + 1, 0, width - 1)];
            return a + (b - a) * f;
        }

        /// <summary>
        /// The red channel of the 4 x 4 magenta and black checkerboard that
        /// RenderingServer binds for a <c>CurveTexture</c> without a curve,
        /// sampled at <c>vec2(t)</c>. The diagonal is magenta, so a bilinear
        /// tap between two diagonal texels reads (1 - f)^2 + f^2.
        /// </summary>
        public static float PlaceholderCurve(float t)
        {
            var x = Mathf.Clamp(t * 4f - 0.5f, 0f, 3f);
            var f = x - Mathf.Floor(x);
            return (1f - f) * (1f - f) + f * f;
        }

        /// <summary><c>srgb_to_linear</c> of one channel, as Godot's Color does it.</summary>
        public static float SrgbToLinear(float c)
        {
            return c < 0.04045f ? c * (1f / 12.92f) : Mathf.Pow((c + 0.055f) * (1f / 1.055f), 2.4f);
        }

        /// <summary><see cref="SrgbToLinear(float)"/> on rgb, alpha untouched.</summary>
        public static Vector4 SrgbToLinear(Vector4 c)
        {
            return new Vector4(SrgbToLinear(c.x), SrgbToLinear(c.y), SrgbToLinear(c.z), c.w);
        }

        /// <summary>
        /// <c>rotate_hue</c> of the process shader. GLSL builds the matrix
        /// from columns, so the product sums the columns weighted by the colour.
        /// </summary>
        public static Vector4 RotateHue(Vector4 c, float angle)
        {
            var hc = Mathf.Cos(angle);
            var hs = Mathf.Sin(angle);
            var c0 = new Vector3(0.299f, 0.587f, 0.114f) + new Vector3(0.701f, -0.587f, -0.114f) * hc + new Vector3(0.168f, 0.330f, -0.497f) * hs;
            var c1 = new Vector3(0.299f, 0.587f, 0.114f) + new Vector3(-0.299f, 0.413f, -0.114f) * hc + new Vector3(-0.328f, 0.035f, 0.292f) * hs;
            var c2 = new Vector3(0.299f, 0.587f, 0.114f) + new Vector3(-0.300f, -0.588f, 0.886f) * hc + new Vector3(1.250f, -1.050f, -0.203f) * hs;
            var rgb = c0 * c.x + c1 * c.y + c2 * c.z;
            return new Vector4(rgb.x, rgb.y, rgb.z, c.w);
        }

        /// <summary>
        /// <c>Basis::looking_at(target, Vector3.UP)</c>. An aligned up vector
        /// makes Godot refuse and keep the old basis, which for a fresh node
        /// is the identity.
        /// </summary>
        public static GodotXform LookingAt(Vector3 origin, Vector3 target)
        {
            var forward = target - origin;
            if (forward.sqrMagnitude <= 0f)
            {
                return new GodotXform(GodotXform.Identity.X, GodotXform.Identity.Y, GodotXform.Identity.Z, origin);
            }

            var vz = -forward.normalized;
            var vx = Vector3.Cross(new Vector3(0f, 1f, 0f), vz);
            if (vx.sqrMagnitude <= 1e-12f)
            {
                return new GodotXform(GodotXform.Identity.X, GodotXform.Identity.Y, GodotXform.Identity.Z, origin);
            }

            vx.Normalize();
            var vy = Vector3.Cross(vz, vx);
            return new GodotXform(vx, vy, vz, origin);
        }

        /// <summary>
        /// The determinant <c>Basis::invert</c> divides by, with its cofactor
        /// order (rows[i][j] is component i of column j).
        /// </summary>
        public static float Determinant(GodotXform t)
        {
            var co0 = t.Y.y * t.Z.z - t.Z.y * t.Y.z;
            var co1 = t.Z.y * t.X.z - t.X.y * t.Z.z;
            var co2 = t.X.y * t.Y.z - t.Y.y * t.X.z;
            return t.X.x * co0 + t.Y.x * co1 + t.Z.x * co2;
        }

        /// <summary><c>Basis(Vector3(0, 0, 1), angle)</c>: Node3D.rotate_z on an identity basis.</summary>
        public static GodotXform RotationZ(float angle)
        {
            var c = Mathf.Cos(angle);
            var s = Mathf.Sin(angle);
            return new GodotXform(new Vector3(c, s, 0f), new Vector3(-s, c, 0f), new Vector3(0f, 0f, 1f), Vector3.zero);
        }
    }
}
