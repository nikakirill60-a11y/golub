using System;

namespace BlitzLegacy.Core.Maps
{
    /// <summary>
    /// A regular Godot terrain grid in source metres, indexed by z * Side + x.
    /// The compact payload carries both the height field and every authored
    /// normal texel, so rebuilding the runtime mesh does not approximate the
    /// source terrain or recalculate its deliberately painted shading.
    /// </summary>
    public sealed class TerrainHeightField
    {
        /// <summary>Header magic written by Tools/port_godot_map.py.</summary>
        public const string Magic = "BLTZTER2";

        /// <summary>Magic, side, two steps and the height range.</summary>
        public const int HeaderBytes = 28;

        private readonly float[] _heights;
        private readonly float[] _normals;

        private TerrainHeightField(
            int side, float stepX, float stepZ, float[] heights, float[] normals)
        {
            Side = side;
            StepX = stepX;
            StepZ = stepZ;
            _heights = heights;
            _normals = normals;
        }

        public int Side { get; private set; }

        public float StepX { get; private set; }

        public float StepZ { get; private set; }

        /// <summary>Width of the landscape in metres.</summary>
        public float ExtentX
        {
            get { return StepX * (Side - 1); }
        }

        public float ExtentZ
        {
            get { return StepZ * (Side - 1); }
        }

        public int VertexCount
        {
            get { return Side * Side; }
        }

        public int TriangleCount
        {
            get { return (Side - 1) * (Side - 1) * 2; }
        }

        public float HeightAt(int x, int z)
        {
            return _heights[SampleIndex(x, z)];
        }

        /// <summary>Returns the source-space normal decoded by the porter.</summary>
        public void NormalAt(int x, int z, out float nx, out float ny, out float nz)
        {
            var index = SampleIndex(x, z) * 3;
            nx = _normals[index];
            ny = _normals[index + 1];
            nz = _normals[index + 2];
        }

        /// <summary>
        /// Bilinear height at a source XZ position in metres. The grid is the
        /// same one the mesh is built from, so this agrees with what a physics
        /// ray would hit anywhere except across a quad's diagonal.
        /// </summary>
        public float SampleHeight(float x, float z)
        {
            var gridX = Clamp(x / StepX, 0f, Side - 1.0001f);
            var gridZ = Clamp(z / StepZ, 0f, Side - 1.0001f);
            var x0 = (int)gridX;
            var z0 = (int)gridZ;
            var tx = gridX - x0;
            var tz = gridZ - z0;

            var h00 = _heights[z0 * Side + x0];
            var h10 = _heights[z0 * Side + x0 + 1];
            var h01 = _heights[(z0 + 1) * Side + x0];
            var h11 = _heights[(z0 + 1) * Side + x0 + 1];

            var top = h00 + (h10 - h00) * tx;
            var bottom = h01 + (h11 - h01) * tx;
            return top + (bottom - top) * tz;
        }

        /// <summary>The source alternates every quad diagonal by grid parity.</summary>
        public static bool IsForwardDiagonal(int quadX, int quadZ)
        {
            return (quadX + quadZ) % 2 == 0;
        }

        public static TerrainHeightField Parse(byte[] payload)
        {
            if (payload == null || payload.Length < HeaderBytes)
            {
                throw new ArgumentException("terrain payload is truncated");
            }

            if (!BitConverter.IsLittleEndian)
            {
                throw new NotSupportedException("terrain payload is little-endian");
            }

            for (var i = 0; i < Magic.Length; i++)
            {
                if (payload[i] != (byte)Magic[i])
                {
                    throw new ArgumentException("terrain payload has the wrong magic");
                }
            }

            var side = BitConverter.ToInt32(payload, 8);
            var stepX = BitConverter.ToSingle(payload, 12);
            var stepZ = BitConverter.ToSingle(payload, 16);
            if (side < 2 || stepX <= 0f || stepZ <= 0f)
            {
                throw new ArgumentException("terrain payload has a degenerate grid");
            }

            var sampleCount = side * side;
            var heightBytes = sampleCount * 4;
            var normalBytes = sampleCount * 3 * 4;
            var expected = HeaderBytes + heightBytes + normalBytes;
            if (payload.Length != expected)
            {
                throw new ArgumentException("terrain payload size does not match its grids");
            }

            var heights = new float[sampleCount];
            var normals = new float[sampleCount * 3];
            Buffer.BlockCopy(payload, HeaderBytes, heights, 0, heightBytes);
            Buffer.BlockCopy(payload, HeaderBytes + heightBytes, normals, 0, normalBytes);
            for (var i = 0; i < normals.Length; i += 3)
            {
                var x = normals[i];
                var y = normals[i + 1];
                var z = normals[i + 2];
                var lengthSquared = x * x + y * y + z * z;
                if (float.IsNaN(lengthSquared) || float.IsInfinity(lengthSquared) ||
                    lengthSquared < 0.25f || lengthSquared > 2.25f)
                {
                    throw new ArgumentException("terrain payload contains an invalid normal");
                }
            }

            return new TerrainHeightField(side, stepX, stepZ, heights, normals);
        }

        private int SampleIndex(int x, int z)
        {
            if (x < 0 || z < 0 || x >= Side || z >= Side)
            {
                throw new ArgumentOutOfRangeException("x", "sample is outside the grid");
            }

            return z * Side + x;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }
    }
}
