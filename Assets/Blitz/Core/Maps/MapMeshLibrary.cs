using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Maps
{
    /// <summary>
    /// One object mesh, still in source coordinates and source winding. The
    /// mirror into Unity space happens once, in the runtime builder, through
    /// <see cref="GodotSpace"/>.
    /// </summary>
    public sealed class MapMesh
    {
        internal MapMesh(string name, float[] positions, float[] normals, float[] uv, int[] indices)
        {
            Name = name;
            Positions = positions;
            Normals = normals;
            Uv = uv;
            Indices = indices;
        }

        public string Name { get; private set; }

        /// <summary>Three components per vertex.</summary>
        public float[] Positions { get; private set; }

        /// <summary>Three components per vertex, as the artists authored them.</summary>
        public float[] Normals { get; private set; }

        /// <summary>Two components per vertex.</summary>
        public float[] Uv { get; private set; }

        public int[] Indices { get; private set; }

        public int VertexCount
        {
            get { return Positions.Length / 3; }
        }

        public int TriangleCount
        {
            get { return Indices.Length / 3; }
        }
    }

    /// <summary>
    /// The object meshes the map draws, lifted out of source GLBs and live meshes
    /// by Tools/port_godot_map.py and baked into one payload.
    ///
    /// The scene inlines every mesh as a compressed ArrayMesh instead of
    /// pointing at the GLB, so the porter reads the authored geometry and
    /// proves it against the bounding box the scene recorded. One payload
    /// rather than dozens of model files keeps the import deterministic and lets
    /// repeated prop families share one mesh at runtime.
    /// </summary>
    public sealed class MapMeshLibrary
    {
        /// <summary>Header magic written by Tools/port_godot_map.py.</summary>
        public const string Magic = "BLTZMSH1";

        private readonly Dictionary<string, MapMesh> _meshes;

        private MapMeshLibrary(Dictionary<string, MapMesh> meshes)
        {
            _meshes = meshes;
            foreach (var mesh in meshes.Values)
            {
                VertexCount += mesh.VertexCount;
                TriangleCount += mesh.TriangleCount;
            }
        }

        public int Count
        {
            get { return _meshes.Count; }
        }

        /// <summary>Vertices across the whole library, for manifest checks.</summary>
        public int VertexCount { get; private set; }

        public int TriangleCount { get; private set; }

        public IEnumerable<MapMesh> Meshes
        {
            get { return _meshes.Values; }
        }

        public bool Has(string name)
        {
            return name != null && _meshes.ContainsKey(name);
        }

        public MapMesh Get(string name)
        {
            MapMesh found;
            if (name == null || !_meshes.TryGetValue(name, out found))
            {
                throw new ArgumentException("the object mesh library has no '" + name + "'");
            }

            return found;
        }

        public static MapMeshLibrary Parse(byte[] payload)
        {
            var reader = new BakedPayload(payload, "object mesh", Magic);
            var count = reader.ReadInt();
            if (count <= 0)
            {
                throw new ArgumentException("object mesh payload has no meshes");
            }

            var meshes = new Dictionary<string, MapMesh>(count, StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var name = reader.ReadName();
                var vertexCount = reader.ReadInt();
                var indexCount = reader.ReadInt();
                if (vertexCount <= 0 || indexCount <= 0 || indexCount % 3 != 0)
                {
                    throw new ArgumentException("object mesh '" + name + "' is degenerate");
                }

                var positions = reader.ReadFloats(vertexCount * 3);
                var normals = reader.ReadFloats(vertexCount * 3);
                var uv = reader.ReadFloats(vertexCount * 2);
                var indices = reader.ReadIndices(indexCount);
                for (var j = 0; j < indices.Length; j++)
                {
                    if (indices[j] < 0 || indices[j] >= vertexCount)
                    {
                        throw new ArgumentException(
                            "object mesh '" + name + "' indexes a vertex it does not have");
                    }
                }

                if (meshes.ContainsKey(name))
                {
                    throw new ArgumentException("object mesh '" + name + "' is baked twice");
                }

                meshes.Add(name, new MapMesh(name, positions, normals, uv, indices));
            }

            reader.ExpectEnd();
            return new MapMeshLibrary(meshes);
        }
    }
}
