using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Maps
{
    /// <summary>
    /// One collision shape as a triangle soup in source coordinates: three
    /// points per triangle, no index buffer, exactly how Godot stores a
    /// ConcavePolygonShape3D.
    /// </summary>
    public sealed class MapShape
    {
        internal MapShape(string name, float[] points)
        {
            Name = name;
            Points = points;
        }

        public string Name { get; private set; }

        /// <summary>Three components per vertex, three vertices per triangle.</summary>
        public float[] Points { get; private set; }

        public int VertexCount
        {
            get { return Points.Length / 3; }
        }

        public int TriangleCount
        {
            get { return VertexCount / 3; }
        }
    }

    /// <summary>
    /// The collision shapes the map's props are built from.
    ///
    /// The source shapes are hand-authored donor meshes rather than copies of
    /// the visual mesh, so they cannot be derived at runtime. The one
    /// ConvexPolygonShape3D among them is triangulated while baking, because
    /// every prop that carries a shape is immovable and therefore needs no
    /// convex collider in Unity either.
    /// </summary>
    public sealed class MapShapeLibrary
    {
        /// <summary>Header magic written by Tools/port_godot_map.py.</summary>
        public const string Magic = "BLTZCOL1";

        private readonly Dictionary<string, MapShape> _shapes;

        private MapShapeLibrary(Dictionary<string, MapShape> shapes)
        {
            _shapes = shapes;
            foreach (var shape in shapes.Values)
            {
                VertexCount += shape.VertexCount;
                TriangleCount += shape.TriangleCount;
            }
        }

        public int Count
        {
            get { return _shapes.Count; }
        }

        /// <summary>Vertices across the whole library, for manifest checks.</summary>
        public int VertexCount { get; private set; }

        public int TriangleCount { get; private set; }

        public IEnumerable<MapShape> Shapes
        {
            get { return _shapes.Values; }
        }

        public bool Has(string name)
        {
            return name != null && _shapes.ContainsKey(name);
        }

        public MapShape Get(string name)
        {
            MapShape found;
            if (name == null || !_shapes.TryGetValue(name, out found))
            {
                throw new ArgumentException("the object shape library has no '" + name + "'");
            }

            return found;
        }

        public static MapShapeLibrary Parse(byte[] payload)
        {
            var reader = new BakedPayload(payload, "object shape", Magic);
            var count = reader.ReadInt();
            if (count <= 0)
            {
                throw new ArgumentException("object shape payload has no shapes");
            }

            var shapes = new Dictionary<string, MapShape>(count, StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var name = reader.ReadName();
                var vertexCount = reader.ReadInt();
                if (vertexCount <= 0 || vertexCount % 3 != 0)
                {
                    throw new ArgumentException(
                        "object shape '" + name + "' is not a triangle soup");
                }

                var points = reader.ReadFloats(vertexCount * 3);
                if (shapes.ContainsKey(name))
                {
                    throw new ArgumentException("object shape '" + name + "' is baked twice");
                }

                shapes.Add(name, new MapShape(name, points));
            }

            reader.ExpectEnd();
            return new MapShapeLibrary(shapes);
        }
    }
}
