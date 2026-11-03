using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Maps
{
    /// <summary>
    /// One lightmapped mesh, still in source coordinates and source winding:
    /// the mirror into Unity space happens once, in the runtime builder,
    /// through <see cref="GodotSpace"/>, exactly as <see cref="MapMesh"/> does.
    ///
    /// These meshes come out of the source scenes rather than the GLB or OBJ
    /// files because the bake is addressed by a second UV channel that only
    /// the authored ArrayMesh carries.
    /// </summary>
    public sealed class LightmapMesh
    {
        internal LightmapMesh(
            string name,
            float[] positions,
            float[] normals,
            float[] uv,
            float[] uv2,
            int[] indices)
        {
            Name = name;
            Positions = positions;
            Normals = normals;
            Uv = uv;
            Uv2 = uv2;
            Indices = indices;
        }

        /// <summary>Keyed by source resource and surface, as the porter writes it.</summary>
        public string Name { get; private set; }

        /// <summary>Three components per vertex.</summary>
        public float[] Positions { get; private set; }

        /// <summary>Three components per vertex, as the artists authored them.</summary>
        public float[] Normals { get; private set; }

        /// <summary>Two components per vertex: the albedo unwrap.</summary>
        public float[] Uv { get; private set; }

        /// <summary>
        /// Two components per vertex: the lightmap unwrap. Every value sits
        /// inside 0..1 of the instance's atlas rect, which
        /// <see cref="LightingEntry"/> maps onto the baked strip.
        /// </summary>
        public float[] Uv2 { get; private set; }

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
    /// The unwrapped meshes the baked instances draw, lifted out of the source
    /// scenes by Tools/port_godot_lighting.py: 76 for the hangar and 45 shared
    /// by the map's 455 baked instances.
    /// </summary>
    public sealed class LightmapMeshLibrary
    {
        /// <summary>Header magic written by Tools/port_godot_lighting.py.</summary>
        public const string Magic = "BLTZLMS1";

        private readonly Dictionary<string, LightmapMesh> _meshes;

        private LightmapMeshLibrary(Dictionary<string, LightmapMesh> meshes)
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

        public IEnumerable<LightmapMesh> Meshes
        {
            get { return _meshes.Values; }
        }

        public bool Has(string name)
        {
            return name != null && _meshes.ContainsKey(name);
        }

        public LightmapMesh Get(string name)
        {
            LightmapMesh found;
            if (name == null || !_meshes.TryGetValue(name, out found))
            {
                throw new ArgumentException("the lightmap mesh library has no '" + name + "'");
            }

            return found;
        }

        public static LightmapMeshLibrary Parse(byte[] payload)
        {
            var reader = new BakedPayload(payload, "lightmap mesh", Magic);
            var count = reader.ReadInt();
            if (count <= 0)
            {
                throw new ArgumentException("lightmap mesh payload has no meshes");
            }

            var meshes = new Dictionary<string, LightmapMesh>(count, StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var name = reader.ReadName();
                var vertexCount = reader.ReadInt();
                var indexCount = reader.ReadInt();
                if (vertexCount <= 0 || indexCount <= 0 || indexCount % 3 != 0)
                {
                    throw new ArgumentException("lightmap mesh '" + name + "' is degenerate");
                }

                var positions = reader.ReadFloats(vertexCount * 3);
                var normals = reader.ReadFloats(vertexCount * 3);
                var uv = reader.ReadFloats(vertexCount * 2);
                var uv2 = reader.ReadFloats(vertexCount * 2);
                var indices = reader.ReadIndices(indexCount);
                for (var j = 0; j < indices.Length; j++)
                {
                    if (indices[j] < 0 || indices[j] >= vertexCount)
                    {
                        throw new ArgumentException(
                            "lightmap mesh '" + name + "' indexes a vertex it does not have");
                    }
                }

                // A value outside the unit square would sample a neighbouring
                // instance once the rect is folded into the strip.
                for (var j = 0; j < uv2.Length; j++)
                {
                    if (uv2[j] < -0.001f || uv2[j] > 1.001f)
                    {
                        throw new ArgumentException(
                            "lightmap mesh '" + name + "' unwraps outside its atlas rect");
                    }
                }

                if (meshes.ContainsKey(name))
                {
                    throw new ArgumentException("lightmap mesh '" + name + "' is baked twice");
                }

                meshes.Add(name, new LightmapMesh(name, positions, normals, uv, uv2, indices));
            }

            reader.ExpectEnd();
            return new LightmapMeshLibrary(meshes);
        }
    }
}
