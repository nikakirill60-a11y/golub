using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Mods
{
    /// <summary>
    /// A binary glTF reader, cut down to what a Godot tank mod ships.
    ///
    /// The shipped vehicles come through Tools/port_godot_hangar.py, which
    /// bakes every authored transform into an OBJ offline. A mod arrives as a
    /// .glb the player dropped in at runtime, so the same bake has to happen
    /// here: positions land in model space, and the caller turns them into
    /// meshes. The mods only ever use triangles with POSITION, NORMAL and
    /// TEXCOORD_0 and carry no skins or animations, so nothing else is read.
    /// </summary>
    public sealed class GlbException : Exception
    {
        public GlbException(string message) : base(message) { }
    }

    /// <summary>One triangle list of one node, already in model space.</summary>
    public sealed class GlbPrimitive
    {
        public float[] Positions = new float[0];
        public float[] Normals = new float[0];
        public float[] Uvs = new float[0];
        public int[] Indices = new int[0];

        public int VertexCount { get { return Positions.Length / 3; } }
        public int TriangleCount { get { return Indices.Length / 3; } }
    }

    /// <summary>One scene node, flattened out of the glTF hierarchy.</summary>
    public sealed class GlbNode
    {
        public string Name = string.Empty;
        public int Parent = -1;
        public float[] World = GlbFile.Identity();
        public readonly List<GlbPrimitive> Primitives = new List<GlbPrimitive>();

        public bool HasGeometry
        {
            get
            {
                for (var i = 0; i < Primitives.Count; i++)
                {
                    if (Primitives[i].Indices.Length > 0) { return true; }
                }

                return false;
            }
        }

        /// <summary>The node origin in model space, in source glTF axes.</summary>
        public float[] WorldOrigin
        {
            get { return new[] { World[12], World[13], World[14] }; }
        }
    }

    public sealed class GlbFile
    {
        private const uint Magic = 0x46546C67;
        private const uint JsonChunk = 0x4E4F534A;
        private const uint BinChunk = 0x004E4942;

        public readonly List<GlbNode> Nodes = new List<GlbNode>();

        public GlbNode Find(string name)
        {
            for (var i = 0; i < Nodes.Count; i++)
            {
                if (string.Equals(Nodes[i].Name, name, StringComparison.Ordinal))
                {
                    return Nodes[i];
                }
            }

            return null;
        }

        /// <summary>The node origin relative to its parent, in source axes.</summary>
        public float[] LocalOrigin(GlbNode node)
        {
            var world = node.WorldOrigin;
            if (node.Parent < 0) { return world; }

            var parent = Nodes[node.Parent].WorldOrigin;
            return new[]
            {
                world[0] - parent[0], world[1] - parent[1], world[2] - parent[2],
            };
        }

        public static GlbFile Load(string path)
        {
            return Parse(File.ReadAllBytes(path), RootScaleOf(path));
        }

        /// <summary>
        /// Godot imports a mod's glb with nodes/root_scale from the sidecar
        /// .import file (0.1 for every mod so far: they are authored in
        /// decimetres). Without applying it every mod tank is ten times too big.
        /// </summary>
        public static float RootScaleOf(string path)
        {
            try
            {
                var sidecar = path + ".import";
                if (!File.Exists(sidecar)) { return 1f; }

                var match = System.Text.RegularExpressions.Regex.Match(
                    File.ReadAllText(sidecar), @"nodes/root_scale\s*=\s*([-+0-9.eE]+)");
                float value;
                if (match.Success && float.TryParse(
                        match.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out value) && value > 0f)
                {
                    return value;
                }
            }
            catch (Exception)
            {
            }

            return 1f;
        }

        public static GlbFile Parse(byte[] bytes)
        {
            return Parse(bytes, 1f);
        }

        public static GlbFile Parse(byte[] bytes, float rootScale)
        {
            if (bytes == null || bytes.Length < 20)
            {
                throw new GlbException("not a glb file");
            }

            if (BitConverter.ToUInt32(bytes, 0) != Magic)
            {
                throw new GlbException("not a glb file");
            }

            string json = null;
            byte[] bin = null;
            var offset = 12;
            while (offset + 8 <= bytes.Length)
            {
                var length = (int)BitConverter.ToUInt32(bytes, offset);
                var kind = BitConverter.ToUInt32(bytes, offset + 4);
                var start = offset + 8;
                if (length < 0 || start + length > bytes.Length)
                {
                    break;
                }

                if (kind == JsonChunk)
                {
                    json = Encoding.UTF8.GetString(bytes, start, length);
                }
                else if (kind == BinChunk)
                {
                    bin = new byte[length];
                    Buffer.BlockCopy(bytes, start, bin, 0, length);
                }

                offset = start + length + ((4 - (length % 4)) % 4);
            }

            if (string.IsNullOrEmpty(json))
            {
                throw new GlbException("glb without a json chunk");
            }

            return Build(JsonNode.Parse(json), bin ?? new byte[0], rootScale);
        }

        // ------------------------------------------------------------------

        private static GlbFile Build(JsonNode root, byte[] bin, float rootScale)
        {
            var file = new GlbFile();
            var nodes = root["nodes"];
            var count = nodes == null ? 0 : nodes.Count;
            var locals = new float[count][];
            var parents = new int[count];
            for (var i = 0; i < count; i++)
            {
                parents[i] = -1;
                locals[i] = LocalMatrix(nodes[i]);
                file.Nodes.Add(new GlbNode
                {
                    Name = nodes[i]["name"].AsString("node" + i),
                });
            }

            for (var i = 0; i < count; i++)
            {
                var children = nodes[i]["children"];
                for (var c = 0; children != null && c < children.Count; c++)
                {
                    var child = children[c].AsInt(-1);
                    if (child >= 0 && child < count) { parents[child] = i; }
                }
            }

            if (Math.Abs(rootScale - 1f) > 1e-6f)
            {
                var scale = Identity();
                scale[0] = rootScale;
                scale[5] = rootScale;
                scale[10] = rootScale;
                for (var i = 0; i < count; i++)
                {
                    if (parents[i] < 0) { locals[i] = Multiply(scale, locals[i]); }
                }
            }

            for (var i = 0; i < count; i++)
            {
                file.Nodes[i].Parent = parents[i];
                file.Nodes[i].World = World(i, parents, locals);
            }

            var meshes = root["meshes"];
            for (var i = 0; i < count; i++)
            {
                var meshIndex = nodes[i].Has("mesh") ? nodes[i]["mesh"].AsInt(-1) : -1;
                if (meshIndex < 0 || meshes == null || meshIndex >= meshes.Count)
                {
                    continue;
                }

                var primitives = meshes[meshIndex]["primitives"];
                for (var p = 0; primitives != null && p < primitives.Count; p++)
                {
                    var primitive = ReadPrimitive(
                        root, bin, primitives[p], file.Nodes[i].World);
                    if (primitive != null) { file.Nodes[i].Primitives.Add(primitive); }
                }
            }

            return file;
        }

        private static GlbPrimitive ReadPrimitive(
            JsonNode root, byte[] bin, JsonNode primitive, float[] world)
        {
            if (primitive.Has("mode") && primitive["mode"].AsInt(4) != 4)
            {
                return null;
            }

            var attributes = primitive["attributes"];
            if (attributes == null || !attributes.Has("POSITION"))
            {
                return null;
            }

            var positions = ReadFloats(
                root, bin, attributes["POSITION"].AsInt(-1), 3);
            if (positions == null) { return null; }

            var normals = attributes.Has("NORMAL")
                ? ReadFloats(root, bin, attributes["NORMAL"].AsInt(-1), 3)
                : null;
            var uvs = attributes.Has("TEXCOORD_0")
                ? ReadFloats(root, bin, attributes["TEXCOORD_0"].AsInt(-1), 2)
                : null;

            var vertices = positions.Length / 3;
            int[] indices;
            if (primitive.Has("indices"))
            {
                indices = ReadIndices(root, bin, primitive["indices"].AsInt(-1));
            }
            else
            {
                indices = new int[vertices];
                for (var i = 0; i < vertices; i++) { indices[i] = i; }
            }

            if (indices == null || indices.Length < 3) { return null; }

            Transform(positions, world, true);
            if (normals != null) { Transform(normals, world, false); }

            return new GlbPrimitive
            {
                Positions = positions,
                Normals = normals ?? new float[0],
                Uvs = uvs ?? new float[0],
                Indices = indices,
            };
        }

        /// <summary>Bakes the authored transform into the vertices.</summary>
        private static void Transform(float[] values, float[] m, bool translate)
        {
            for (var i = 0; i + 2 < values.Length; i += 3)
            {
                var x = values[i];
                var y = values[i + 1];
                var z = values[i + 2];
                values[i] = (m[0] * x) + (m[4] * y) + (m[8] * z) + (translate ? m[12] : 0f);
                values[i + 1] = (m[1] * x) + (m[5] * y) + (m[9] * z) + (translate ? m[13] : 0f);
                values[i + 2] = (m[2] * x) + (m[6] * y) + (m[10] * z) + (translate ? m[14] : 0f);
                if (!translate)
                {
                    // A scaled node must not shrink its normals: lighting
                    // expects unit vectors.
                    var length = (float)Math.Sqrt(
                        (values[i] * values[i]) + (values[i + 1] * values[i + 1]) +
                        (values[i + 2] * values[i + 2]));
                    if (length > 1e-8f)
                    {
                        values[i] /= length;
                        values[i + 1] /= length;
                        values[i + 2] /= length;
                    }
                }
            }
        }

        private static float[] ReadFloats(
            JsonNode root, byte[] bin, int accessorIndex, int components)
        {
            var accessors = root["accessors"];
            if (accessorIndex < 0 || accessors == null || accessorIndex >= accessors.Count)
            {
                return null;
            }

            var accessor = accessors[accessorIndex];
            if (accessor["componentType"].AsInt() != 5126)
            {
                return null;
            }

            var count = accessor["count"].AsInt();
            var stride = 0;
            var start = 0;
            if (!Locate(root, accessor, components * 4, out start, out stride))
            {
                return null;
            }

            var values = new float[count * components];
            for (var i = 0; i < count; i++)
            {
                var at = start + (i * stride);
                for (var c = 0; c < components; c++)
                {
                    var offset = at + (c * 4);
                    if (offset + 4 > bin.Length) { return null; }
                    values[(i * components) + c] = BitConverter.ToSingle(bin, offset);
                }
            }

            return values;
        }

        private static int[] ReadIndices(JsonNode root, byte[] bin, int accessorIndex)
        {
            var accessors = root["accessors"];
            if (accessorIndex < 0 || accessors == null || accessorIndex >= accessors.Count)
            {
                return null;
            }

            var accessor = accessors[accessorIndex];
            var componentType = accessor["componentType"].AsInt();
            var size = componentType == 5125 ? 4 : componentType == 5123 ? 2 : 1;
            var count = accessor["count"].AsInt();
            int start;
            int stride;
            if (!Locate(root, accessor, size, out start, out stride))
            {
                return null;
            }

            var indices = new int[count];
            for (var i = 0; i < count; i++)
            {
                var at = start + (i * stride);
                if (at + size > bin.Length) { return null; }
                indices[i] = size == 4
                    ? (int)BitConverter.ToUInt32(bin, at)
                    : size == 2 ? BitConverter.ToUInt16(bin, at) : bin[at];
            }

            return indices;
        }

        private static bool Locate(
            JsonNode root, JsonNode accessor, int elementSize, out int start, out int stride)
        {
            start = 0;
            stride = elementSize;
            var views = root["bufferViews"];
            var viewIndex = accessor.Has("bufferView") ? accessor["bufferView"].AsInt(-1) : -1;
            if (viewIndex < 0 || views == null || viewIndex >= views.Count)
            {
                return false;
            }

            var view = views[viewIndex];
            start = view["byteOffset"].AsInt() + accessor["byteOffset"].AsInt();
            var declared = view["byteStride"].AsInt();
            stride = declared > 0 ? declared : elementSize;
            return true;
        }

        private static float[] LocalMatrix(JsonNode node)
        {
            if (node.Has("matrix"))
            {
                var source = node["matrix"];
                var matrix = Identity();
                for (var i = 0; i < 16 && i < source.Count; i++)
                {
                    matrix[i] = source[i].AsFloat();
                }

                return matrix;
            }

            var t = Triple(node, "translation", 0f);
            var s = Triple(node, "scale", 1f);
            var r = node.Has("rotation")
                ? new[]
                {
                    node["rotation"][0].AsFloat(), node["rotation"][1].AsFloat(),
                    node["rotation"][2].AsFloat(), node["rotation"][3].AsFloat(),
                }
                : new[] { 0f, 0f, 0f, 1f };

            var x = r[0];
            var y = r[1];
            var z = r[2];
            var w = r[3];
            var result = Identity();
            result[0] = (1f - (2f * ((y * y) + (z * z)))) * s[0];
            result[1] = (2f * ((x * y) + (z * w))) * s[0];
            result[2] = (2f * ((x * z) - (y * w))) * s[0];
            result[4] = (2f * ((x * y) - (z * w))) * s[1];
            result[5] = (1f - (2f * ((x * x) + (z * z)))) * s[1];
            result[6] = (2f * ((y * z) + (x * w))) * s[1];
            result[8] = (2f * ((x * z) + (y * w))) * s[2];
            result[9] = (2f * ((y * z) - (x * w))) * s[2];
            result[10] = (1f - (2f * ((x * x) + (y * y)))) * s[2];
            result[12] = t[0];
            result[13] = t[1];
            result[14] = t[2];
            return result;
        }

        private static float[] Triple(JsonNode node, string key, float fallback)
        {
            var values = new[] { fallback, fallback, fallback };
            if (!node.Has(key)) { return values; }

            var source = node[key];
            for (var i = 0; i < 3 && i < source.Count; i++)
            {
                values[i] = source[i].AsFloat(fallback);
            }

            return values;
        }

        private static float[] World(int index, int[] parents, float[][] locals)
        {
            var matrix = locals[index];
            var parent = parents[index];
            var guard = 0;
            while (parent >= 0 && guard++ < parents.Length)
            {
                matrix = Multiply(locals[parent], matrix);
                parent = parents[parent];
            }

            return matrix;
        }

        private static float[] Multiply(float[] a, float[] b)
        {
            var result = new float[16];
            for (var column = 0; column < 4; column++)
            {
                for (var row = 0; row < 4; row++)
                {
                    var sum = 0f;
                    for (var k = 0; k < 4; k++)
                    {
                        sum += a[(k * 4) + row] * b[(column * 4) + k];
                    }

                    result[(column * 4) + row] = sum;
                }
            }

            return result;
        }

        public static float[] Identity()
        {
            return new[]
            {
                1f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f,
            };
        }
    }
}

