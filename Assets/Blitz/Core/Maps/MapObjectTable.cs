using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Maps
{
    /// <summary>The six source prop families placed by a battle map.</summary>
    public enum MapObjectKind
    {
        /// <summary>map_object_static.gd: a StaticBody3D that never changes.</summary>
        Static,

        /// <summary>map_object_destructible.gd: swaps to a wreck when killed.</summary>
        Destructible,

        /// <summary>map_object_tree.gd: nested trunk, crown and fall trigger.</summary>
        Tree,

        /// <summary>map_object_vegetation.gd: a shared billboard cluster.</summary>
        Vegetation,

        /// <summary>The map's animated, lit sea surface.</summary>
        Water,

        /// <summary>A visible background mesh without gameplay collision.</summary>
        Decoration,
    }

    /// <summary>The primitive shapes used by source foliage scenes.</summary>
    public enum MapPrimitiveKind
    {
        Capsule,
        Sphere,
        Box,
    }

    /// <summary>
    /// One material instance of a source map shader, shared by every visual
    /// part that resolves the same parameters.
    /// </summary>
    public sealed class MapObjectMaterial
    {
        public string Name { get; set; }

        /// <summary>"object", "vegetation" or "water".</summary>
        public string Shader { get; set; }

        /// <summary>Linear RGBA, as Godot stores a source_color uniform.</summary>
        public float[] Albedo { get; set; }

        /// <summary>The opaque object shader raises the albedo to this power.</summary>
        public float Contrast { get; set; }

        public bool Emissive { get; set; }

        public string Texture { get; set; }

        public string EmissiveTexture { get; set; }

        public float[] Uv1Scale { get; set; }

        public float[] Uv1Offset { get; set; }

        public float[] Uv2Scale { get; set; }

        public float[] Uv2Offset { get; set; }

        public float AlphaClip { get; set; }

        public bool Billboard { get; set; }

        public float[] OriginOffset { get; set; }

        public float SwayAmount { get; set; }

        public float SwayScale { get; set; }

        public float SwayDuration { get; set; }

        public float[] SwayDirection { get; set; }

        public float[] Shift1 { get; set; }

        public float[] Shift2 { get; set; }

        public bool IsWater
        {
            get { return string.Equals(Shader, "water", StringComparison.Ordinal); }
        }

        public bool IsVegetation
        {
            get { return string.Equals(Shader, "vegetation", StringComparison.Ordinal); }
        }
    }

    /// <summary>One local transform from an authored scene or MultiMesh.</summary>
    public sealed class MapLocalTransform
    {
        public float[] Basis { get; set; }

        public float[] Origin { get; set; }
    }

    /// <summary>
    /// A Godot MultiMesh shared by every matching bush/tree instance. Its
    /// authored transforms are stored once; Unity expands them into one mesh,
    /// not one GameObject per billboard.
    /// </summary>
    public sealed class MapMultiMesh
    {
        public MapMultiMesh()
        {
            Transforms = new List<MapLocalTransform>();
        }

        public string Name { get; set; }

        public string Source { get; set; }

        public int Count { get; set; }

        public float[] QuadSize { get; set; }

        public List<MapLocalTransform> Transforms { get; private set; }
    }

    /// <summary>One renderer in a nested tree or vegetation source scene.</summary>
    public sealed class MapObjectPart
    {
        public string Name { get; set; }

        public string Material { get; set; }

        /// <summary>An imported GLB mesh; mutually exclusive with MultiMesh.</summary>
        public string Mesh { get; set; }

        /// <summary>A generated billboard cluster; mutually exclusive with Mesh.</summary>
        public string MultiMesh { get; set; }

        public float[] Basis { get; set; }

        public float[] Origin { get; set; }

        /// <summary>Whether this part follows a falling tree trunk.</summary>
        public bool FollowsTrunk { get; set; }
    }

    /// <summary>One source capsule, sphere or box under a foliage body.</summary>
    public sealed class MapPrimitiveCollider
    {
        public string Name { get; set; }

        public MapPrimitiveKind Kind { get; set; }

        public float Radius { get; set; }

        public float Height { get; set; }

        public float[] Size { get; set; }

        public float[] Basis { get; set; }

        public float[] Origin { get; set; }

        public bool IsTrigger { get; set; }

        public int CollisionLayer { get; set; }

        public int CollisionMask { get; set; }

        /// <summary>Whether this shape follows a falling tree trunk.</summary>
        public bool FollowsTrunk { get; set; }
    }

    /// <summary>One prop placed on the map, still in source coordinates.</summary>
    public sealed class MapObjectInstance
    {
        public MapObjectInstance()
        {
            Parts = new List<MapObjectPart>();
            Colliders = new List<MapPrimitiveCollider>();
        }

        public string Name { get; set; }

        public MapObjectKind Kind { get; set; }

        /// <summary>The object scene this instance was resolved from.</summary>
        public string Source { get; set; }

        /// <summary>Full GeometryInstance path in the map lightmap manifest.</summary>
        public string BakedPath { get; set; }

        public float[] Basis { get; set; }

        public float[] Origin { get; set; }

        public string Material { get; set; }

        public string Mesh { get; set; }

        /// <summary>The wreck mesh, or null for a non-destructible prop.</summary>
        public string MeshDestroyed { get; set; }

        /// <summary>Null on simple props the source leaves without a collider.</summary>
        public string Shape { get; set; }

        /// <summary>The source node belongs to its navmesh_obstacle group.</summary>
        public bool NavigationObstacle { get; set; }

        /// <summary>The source node belongs to ai_nohide and cannot be cover.</summary>
        public bool AiNoHide { get; set; }

        public string ShapeDestroyed { get; set; }

        /// <summary>map_object_destructible.gd health, otherwise zero.</summary>
        public int Health { get; set; }

        /// <summary>An index into map_object_destructible.gd's Effect enum.</summary>
        public int Effect { get; set; }

        public int CollisionLayer { get; set; }

        public int CollisionMask { get; set; }

        public float FallSpeed { get; set; }

        public List<MapObjectPart> Parts { get; private set; }

        public List<MapPrimitiveCollider> Colliders { get; private set; }

        public bool IsDestructible
        {
            get { return Kind == MapObjectKind.Destructible; }
        }

        public bool IsTree
        {
            get { return Kind == MapObjectKind.Tree; }
        }

        public bool IsVegetation
        {
            get { return Kind == MapObjectKind.Vegetation; }
        }
    }

    /// <summary>A baked geometry payload and the totals it has to carry.</summary>
    public sealed class MapGeometryInfo
    {
        public string Resource { get; set; }

        public int Count { get; set; }

        public int Vertices { get; set; }

        public int Triangles { get; set; }

        public int Bytes { get; set; }

        public string Sha256 { get; set; }
    }

    /// <summary>
    /// Every prop on a map as Tools/port_godot_map.py exported it. The source
    /// spreads root objects and tree/bush internals across inherited scenes, so
    /// the porter resolves those hierarchies once and records their
    /// visual parts, shared MultiMeshes and primitive colliders here.
    /// </summary>
    public sealed class MapObjectTable
    {
        public MapObjectTable()
        {
            Materials = new Dictionary<string, MapObjectMaterial>(StringComparer.Ordinal);
            MultiMeshes = new Dictionary<string, MapMultiMesh>(StringComparer.Ordinal);
            Objects = new List<MapObjectInstance>();
            Layers = new Dictionary<string, int>(StringComparer.Ordinal);
            Skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        public string MapId { get; set; }

        public MapGeometryInfo Meshes { get; set; }

        public MapGeometryInfo Shapes { get; set; }

        public Dictionary<string, MapObjectMaterial> Materials { get; private set; }

        public Dictionary<string, MapMultiMesh> MultiMeshes { get; private set; }

        public List<MapObjectInstance> Objects { get; private set; }

        /// <summary>The source collision layers, by prop family.</summary>
        public Dictionary<string, int> Layers { get; private set; }

        /// <summary>Prop families the porter has not ported yet, by count.</summary>
        public Dictionary<string, int> Skipped { get; private set; }

        public MapObjectMaterial GetMaterial(string name)
        {
            MapObjectMaterial found;
            if (name == null || !Materials.TryGetValue(name, out found))
            {
                throw new ArgumentException("the object table has no material '" + name + "'");
            }

            return found;
        }

        public MapMultiMesh GetMultiMesh(string name)
        {
            MapMultiMesh found;
            if (name == null || !MultiMeshes.TryGetValue(name, out found))
            {
                throw new ArgumentException("the object table has no MultiMesh '" + name + "'");
            }

            return found;
        }

        public int CountOf(MapObjectKind kind)
        {
            var count = 0;
            for (var i = 0; i < Objects.Count; i++)
            {
                if (Objects[i].Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }

        public int SkippedCount(string kind)
        {
            int found;
            return Skipped.TryGetValue(kind, out found) ? found : 0;
        }

        public static MapObjectTable Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentException("object table is empty");
            }

            var root = JsonNode.Parse(json);
            var table = new MapObjectTable
            {
                MapId = root["map"].AsString(),
                Meshes = ReadGeometry(root["meshes"]),
                Shapes = ReadGeometry(root["shapes"]),
            };

            foreach (var field in root["layers"].Fields)
            {
                table.Layers[field.Key] = field.Value.AsInt();
            }

            foreach (var field in root["skipped"].Fields)
            {
                table.Skipped[field.Key] = field.Value.AsInt();
            }

            foreach (var field in root["materials"].Fields)
            {
                table.Materials[field.Key] = ReadMaterial(field.Key, field.Value);
            }

            foreach (var field in root["multiMeshes"].Fields)
            {
                table.MultiMeshes[field.Key] = ReadMultiMesh(field.Key, field.Value);
            }

            foreach (var item in root["objects"].Items)
            {
                table.Objects.Add(ReadObject(item));
            }

            if (table.Objects.Count == 0)
            {
                throw new ArgumentException("object table has no objects");
            }

            return table;
        }

        private static MapObjectInstance ReadObject(JsonNode node)
        {
            var kind = ReadKind(node["kind"].AsString());
            var instance = new MapObjectInstance
            {
                Name = node["name"].AsString(),
                Kind = kind,
                Source = node["source"].AsString(),
                BakedPath = Text(node["bakedPath"]),
                Basis = Floats(node["basis"], 9),
                Origin = Floats(node["origin"], 3),
                Material = Text(node["material"]),
                Mesh = Text(node["mesh"]),
                MeshDestroyed = Text(node["meshDestroyed"]),
                Shape = Text(node["shape"]),
                NavigationObstacle = node["navigationObstacle"].AsBool(),
                AiNoHide = node["aiNoHide"].AsBool(),
                ShapeDestroyed = Text(node["shapeDestroyed"]),
                Health = node["health"].AsInt(),
                Effect = node["effect"].AsInt(),
                CollisionLayer = node["collisionLayer"].AsInt(),
                CollisionMask = node["collisionMask"].AsInt(),
                FallSpeed = node["fallSpeed"].AsFloat(),
            };

            foreach (var item in node["parts"].Items)
            {
                instance.Parts.Add(ReadPart(item));
            }

            foreach (var item in node["colliders"].Items)
            {
                instance.Colliders.Add(ReadCollider(item));
            }

            if (string.IsNullOrEmpty(instance.Name))
            {
                throw new ArgumentException("an object is missing its name");
            }

            if (kind == MapObjectKind.Static || kind == MapObjectKind.Destructible ||
                kind == MapObjectKind.Water || kind == MapObjectKind.Decoration)
            {
                if (string.IsNullOrEmpty(instance.Mesh) ||
                    string.IsNullOrEmpty(instance.Material))
                {
                    throw new ArgumentException(
                        "simple object '" + instance.Name + "' is missing its mesh or material");
                }
            }
            else if (instance.Parts.Count == 0)
            {
                throw new ArgumentException(
                    "nested object '" + instance.Name + "' has no visual parts");
            }

            if (instance.IsDestructible && string.IsNullOrEmpty(instance.MeshDestroyed))
            {
                throw new ArgumentException(
                    "destructible '" + instance.Name + "' has no wreck mesh");
            }

            if (instance.IsTree && instance.FallSpeed <= 0f)
            {
                throw new ArgumentException("tree '" + instance.Name + "' cannot fall");
            }

            return instance;
        }

        private static MapObjectPart ReadPart(JsonNode node)
        {
            var part = new MapObjectPart
            {
                Name = node["name"].AsString(),
                Material = node["material"].AsString(),
                Mesh = Text(node["mesh"]),
                MultiMesh = Text(node["multiMesh"]),
                Basis = Floats(node["basis"], 9),
                Origin = Floats(node["origin"], 3),
                FollowsTrunk = node["followsTrunk"].AsBool(),
            };
            if (string.IsNullOrEmpty(part.Name) || string.IsNullOrEmpty(part.Material) ||
                string.IsNullOrEmpty(part.Mesh) == string.IsNullOrEmpty(part.MultiMesh))
            {
                throw new ArgumentException("an object part has invalid geometry references");
            }

            return part;
        }

        private static MapPrimitiveCollider ReadCollider(JsonNode node)
        {
            var kind = ReadPrimitiveKind(node["type"].AsString());
            var collider = new MapPrimitiveCollider
            {
                Name = node["name"].AsString(),
                Kind = kind,
                Radius = node["radius"].AsFloat(),
                Height = node["height"].AsFloat(),
                Size = OptionalFloats(node["size"], 3),
                Basis = Floats(node["basis"], 9),
                Origin = Floats(node["origin"], 3),
                IsTrigger = node["isTrigger"].AsBool(),
                CollisionLayer = node["collisionLayer"].AsInt(),
                CollisionMask = node["collisionMask"].AsInt(),
                FollowsTrunk = node["followsTrunk"].AsBool(),
            };
            if (string.IsNullOrEmpty(collider.Name))
            {
                throw new ArgumentException("a primitive collider has no name");
            }

            if ((kind == MapPrimitiveKind.Capsule || kind == MapPrimitiveKind.Sphere) &&
                collider.Radius <= 0f)
            {
                throw new ArgumentException("a primitive collider has no radius");
            }

            return collider;
        }

        private static MapMultiMesh ReadMultiMesh(string name, JsonNode node)
        {
            var result = new MapMultiMesh
            {
                Name = name,
                Source = node["source"].AsString(),
                Count = node["count"].AsInt(),
                QuadSize = Floats(node["quadSize"], 2),
            };
            foreach (var item in node["transforms"].Items)
            {
                result.Transforms.Add(new MapLocalTransform
                {
                    Basis = Floats(item["basis"], 9),
                    Origin = Floats(item["origin"], 3),
                });
            }

            if (string.IsNullOrEmpty(result.Source) || result.Count <= 0 ||
                result.Transforms.Count != result.Count)
            {
                throw new ArgumentException("MultiMesh '" + name + "' is incomplete");
            }

            return result;
        }

        private static MapObjectKind ReadKind(string kind)
        {
            if (string.Equals(kind, "static", StringComparison.Ordinal))
            {
                return MapObjectKind.Static;
            }

            if (string.Equals(kind, "destructible", StringComparison.Ordinal))
            {
                return MapObjectKind.Destructible;
            }

            if (string.Equals(kind, "tree", StringComparison.Ordinal))
            {
                return MapObjectKind.Tree;
            }

            if (string.Equals(kind, "vegetation", StringComparison.Ordinal))
            {
                return MapObjectKind.Vegetation;
            }

            if (string.Equals(kind, "water", StringComparison.Ordinal))
            {
                return MapObjectKind.Water;
            }

            if (string.Equals(kind, "decoration", StringComparison.Ordinal))
            {
                return MapObjectKind.Decoration;
            }

            throw new ArgumentException("unknown object kind '" + kind + "'");
        }

        private static MapPrimitiveKind ReadPrimitiveKind(string kind)
        {
            if (string.Equals(kind, "capsule", StringComparison.Ordinal))
            {
                return MapPrimitiveKind.Capsule;
            }

            if (string.Equals(kind, "sphere", StringComparison.Ordinal))
            {
                return MapPrimitiveKind.Sphere;
            }

            if (string.Equals(kind, "box", StringComparison.Ordinal))
            {
                return MapPrimitiveKind.Box;
            }

            throw new ArgumentException("unknown primitive collider '" + kind + "'");
        }

        private static MapObjectMaterial ReadMaterial(string name, JsonNode node)
        {
            var vegetation = string.Equals(
                node["shader"].AsString("object"), "vegetation", StringComparison.Ordinal);
            return new MapObjectMaterial
            {
                Name = name,
                Shader = node["shader"].AsString("object"),
                Albedo = Floats(node["albedo"], 4),
                Contrast = node["contrast"].AsFloat(1f),
                Emissive = node["emissive"].AsBool(),
                Texture = node["texture"].AsString(),
                EmissiveTexture = Text(node["emissiveTexture"]),
                Uv1Scale = Floats(node["uv1Scale"], 3),
                Uv1Offset = Floats(node["uv1Offset"], 3),
                Uv2Scale = Floats(node["uv2Scale"], 3),
                Uv2Offset = Floats(node["uv2Offset"], 3),
                AlphaClip = node["alphaClip"].AsFloat(0.5f),
                Billboard = node["billboard"].AsBool(vegetation),
                OriginOffset = OptionalFloats(
                    node["originOffset"], 3, new[] { 0f, 0f, 0f }),
                SwayAmount = node["swayAmount"].AsFloat(),
                SwayScale = node["swayScale"].AsFloat(1f),
                SwayDuration = node["swayDuration"].AsFloat(1f),
                SwayDirection = OptionalFloats(
                    node["swayDirection"], 3, new[] { 1f, 0f, 0f }),
                Shift1 = OptionalFloats(node["shift1"], 2, new[] { 0f, 0f }),
                Shift2 = OptionalFloats(node["shift2"], 2, new[] { 0f, 0f }),
            };
        }

        private static MapGeometryInfo ReadGeometry(JsonNode node)
        {
            var info = new MapGeometryInfo
            {
                Resource = node["resource"].AsString(),
                Count = node["count"].AsInt(),
                Vertices = node["vertices"].AsInt(),
                Triangles = node["triangles"].AsInt(),
                Bytes = node["bytes"].AsInt(),
                Sha256 = node["sha256"].AsString(),
            };

            if (string.IsNullOrEmpty(info.Resource) || info.Count <= 0)
            {
                throw new ArgumentException("an object payload is missing from the table");
            }

            return info;
        }

        /// <summary>Optional strings arrive as JSON null or a missing field.</summary>
        private static string Text(JsonNode node)
        {
            return node == null || node.Kind != JsonKind.String ? null : node.AsString();
        }

        private static float[] OptionalFloats(
            JsonNode node, int expected, float[] fallback = null)
        {
            if (node == null || node.Kind == JsonKind.Null)
            {
                return fallback;
            }

            return Floats(node, expected);
        }

        private static float[] Floats(JsonNode node, int expected)
        {
            if (node == null || node.Count != expected)
            {
                throw new ArgumentException("expected " + expected + " components");
            }

            var values = new float[expected];
            for (var i = 0; i < expected; i++)
            {
                values[i] = node[i].AsFloat();
            }

            return values;
        }
    }
}
