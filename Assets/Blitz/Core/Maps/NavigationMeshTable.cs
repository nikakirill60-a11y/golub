using System;

namespace BlitzLegacy.Core.Maps
{
    /// <summary>
    /// The navigation mesh Godot bakes into a map scene: convex polygons over
    /// one shared vertex pool, in source metres. Tools/port_godot_navmesh.py
    /// packs the live NavigationRegion3D resources, so the walkable surface
    /// here is the one the source bots plan on - the lake bed and every slope
    /// past agent_max_climb are simply not part of it.
    /// </summary>
    public sealed class NavigationMeshTable
    {
        /// <summary>Header magic written by Tools/port_godot_navmesh.py.</summary>
        public const string Magic = "BLNM";

        /// <summary>Payload revision this parser understands.</summary>
        public const int Version = 1;

        private readonly Region[] _regions;

        private NavigationMeshTable(Region[] regions)
        {
            _regions = regions;
        }

        public int RegionCount
        {
            get { return _regions.Length; }
        }

        public Region RegionAt(int index)
        {
            return _regions[index];
        }

        /// <summary>One baked NavigationRegion3D, with its bake settings.</summary>
        public sealed class Region
        {
            private readonly Vec3[] _vertices;
            private readonly int[][] _polygons;

            internal Region(
                float cellSize,
                float cellHeight,
                float agentRadius,
                float agentHeight,
                float agentMaxClimb,
                float agentMaxSlope,
                Vec3[] vertices,
                int[][] polygons)
            {
                CellSize = cellSize;
                CellHeight = cellHeight;
                AgentRadius = agentRadius;
                AgentHeight = agentHeight;
                AgentMaxClimb = agentMaxClimb;
                AgentMaxSlope = agentMaxSlope;
                _vertices = vertices;
                _polygons = polygons;
            }

            public float CellSize { get; private set; }

            public float CellHeight { get; private set; }

            /// <summary>
            /// The radius the source baked with. These polygons are already
            /// inset by it, so a rebake must not inset them a second time.
            /// </summary>
            public float AgentRadius { get; private set; }

            public float AgentHeight { get; private set; }

            public float AgentMaxClimb { get; private set; }

            public float AgentMaxSlope { get; private set; }

            public int VertexCount
            {
                get { return _vertices.Length; }
            }

            /// <summary>A vertex, already mirrored into the port's space.</summary>
            public Vec3 VertexAt(int index)
            {
                return _vertices[index];
            }

            public int PolygonCount
            {
                get { return _polygons.Length; }
            }

            /// <summary>The vertex indices of one convex polygon.</summary>
            public int[] PolygonAt(int index)
            {
                return _polygons[index];
            }
        }

        /// <summary>Reads the payload written by Tools/port_godot_navmesh.py.</summary>
        public static NavigationMeshTable Parse(byte[] payload)
        {
            if (payload == null || payload.Length < Magic.Length + 8)
            {
                throw new ArgumentException("the navmesh payload is truncated");
            }

            for (var i = 0; i < Magic.Length; i++)
            {
                if (payload[i] != (byte)Magic[i])
                {
                    throw new ArgumentException("the navmesh payload has no BLNM header");
                }
            }

            var offset = Magic.Length;
            var version = ReadInt(payload, ref offset);
            if (version != Version)
            {
                throw new ArgumentException(
                    "unsupported navmesh payload version " + version);
            }

            var regionCount = ReadInt(payload, ref offset);
            if (regionCount <= 0)
            {
                throw new ArgumentException("the navmesh payload carries no region");
            }

            var regions = new Region[regionCount];
            for (var r = 0; r < regionCount; r++)
            {
                var cellSize = ReadFloat(payload, ref offset);
                var cellHeight = ReadFloat(payload, ref offset);
                var agentRadius = ReadFloat(payload, ref offset);
                var agentHeight = ReadFloat(payload, ref offset);
                var agentMaxClimb = ReadFloat(payload, ref offset);
                var agentMaxSlope = ReadFloat(payload, ref offset);

                var vertexCount = ReadInt(payload, ref offset);
                if (vertexCount <= 0)
                {
                    throw new ArgumentException("a navmesh region carries no vertex");
                }

                var vertices = new Vec3[vertexCount];
                for (var v = 0; v < vertexCount; v++)
                {
                    var x = ReadFloat(payload, ref offset);
                    var y = ReadFloat(payload, ref offset);
                    var z = ReadFloat(payload, ref offset);
                    // The porter writes source space; the mirror stays in the
                    // one place the rest of the port mirrors through.
                    vertices[v] = GodotSpace.Point(x, y, z);
                }

                var polygonCount = ReadInt(payload, ref offset);
                if (polygonCount <= 0)
                {
                    throw new ArgumentException("a navmesh region carries no polygon");
                }

                var polygons = new int[polygonCount][];
                for (var p = 0; p < polygonCount; p++)
                {
                    var indexCount = ReadInt(payload, ref offset);
                    if (indexCount < 3)
                    {
                        throw new ArgumentException("a navmesh polygon is degenerate");
                    }

                    var indices = new int[indexCount];
                    for (var i = 0; i < indexCount; i++)
                    {
                        var index = ReadInt(payload, ref offset);
                        if (index < 0 || index >= vertexCount)
                        {
                            throw new ArgumentException(
                                "a navmesh polygon indexes past its vertices");
                        }

                        indices[i] = index;
                    }

                    polygons[p] = indices;
                }

                regions[r] = new Region(
                    cellSize,
                    cellHeight,
                    agentRadius,
                    agentHeight,
                    agentMaxClimb,
                    agentMaxSlope,
                    vertices,
                    polygons);
            }

            return new NavigationMeshTable(regions);
        }

        private static int ReadInt(byte[] payload, ref int offset)
        {
            Require(payload, offset, 4);
            var value = BitConverter.ToInt32(payload, offset);
            offset += 4;
            return value;
        }

        private static float ReadFloat(byte[] payload, ref int offset)
        {
            Require(payload, offset, 4);
            var value = BitConverter.ToSingle(payload, offset);
            offset += 4;
            return value;
        }

        private static void Require(byte[] payload, int offset, int bytes)
        {
            if (offset + bytes > payload.Length)
            {
                throw new ArgumentException("the navmesh payload is truncated");
            }
        }
    }
}
