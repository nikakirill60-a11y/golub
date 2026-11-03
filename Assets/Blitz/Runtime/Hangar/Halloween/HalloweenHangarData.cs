// Readers for the binary geometry (meshes.bytes) and animation (clips.bytes)
// streams written by Tools/port_halloween_hangar_2026.py. Everything is already
// in Unity coordinates (x, z, y), triangles rewound, skin weights normalized.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Hangar.Halloween
{
    public sealed class MeshRecord
    {
        public string name;
        public int vertexCount, streams, boneCount;
        public Vector3[] positions, normals, binormals;
        public Vector4[] tangents, bindNormal, bindTangent;
        public Color32[] colors;
        public Vector2[] uv0, uv1, uv3, uv4;
        public int[] joints;
        public float[] weights;
        public List<int[]> submeshes = new List<int[]>();
        public Matrix4x4[] bindposes = new Matrix4x4[0];

        public bool Has(int stream) => (streams & stream) != 0;
    }

    public sealed class CurveRecord
    {
        public string path;
        public int property, dims;
        public float[] times, values;
    }

    public sealed class ClipRecord
    {
        public string name;
        public float length;
        public List<CurveRecord> curves = new List<CurveRecord>();
    }

    public static class HalloweenHangarData
    {
        public const int StreamNormal = 1, StreamTangent = 2, StreamColor = 4, StreamUv0 = 8, StreamUv1 = 16;
        public const int StreamBinormal = 32, StreamUv3 = 64, StreamUv4 = 128, StreamSkin = 256, StreamBindFrame = 512;

        static string Magic(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(8));

        static string Name(BinaryReader reader)
        {
            int length = reader.ReadUInt16();
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        static float[] Floats(BinaryReader reader, int count)
        {
            var bytes = reader.ReadBytes(count * 4);
            if (bytes.Length != count * 4)
                throw new EndOfStreamException("truncated float array");
            var result = new float[count];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        static int[] Ints(BinaryReader reader, int count)
        {
            var bytes = reader.ReadBytes(count * 4);
            if (bytes.Length != count * 4)
                throw new EndOfStreamException("truncated int array");
            var result = new int[count];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        static Vector3[] Vec3(float[] f)
        {
            var result = new Vector3[f.Length / 3];
            for (int i = 0; i < result.Length; i++)
                result[i] = new Vector3(f[3 * i], f[3 * i + 1], f[3 * i + 2]);
            return result;
        }

        static Vector2[] Vec2(float[] f)
        {
            var result = new Vector2[f.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = new Vector2(f[2 * i], f[2 * i + 1]);
            return result;
        }

        static Vector4[] Vec4(float[] f, int stride, int offset)
        {
            var result = new Vector4[f.Length / stride];
            for (int i = 0; i < result.Length; i++)
            {
                int b = i * stride + offset;
                result[i] = new Vector4(f[b], f[b + 1], f[b + 2], f[b + 3]);
            }
            return result;
        }

        public static List<MeshRecord> ReadMeshes(byte[] data, string path)
        {
            var list = new List<MeshRecord>();
            using (var reader = new BinaryReader(new MemoryStream(data, false)))
            {
                if (Magic(reader) != "HWMESH01")
                    throw new InvalidDataException(path + ": not a HWMESH01 stream");
                uint count = reader.ReadUInt32();
                for (uint m = 0; m < count; m++)
                {
                    var r = new MeshRecord { name = Name(reader) };
                    r.vertexCount = (int)reader.ReadUInt32();
                    r.streams = (int)reader.ReadUInt32();
                    int submeshCount = (int)reader.ReadUInt32();
                    r.boneCount = (int)reader.ReadUInt32();
                    int n = r.vertexCount;
                    r.positions = Vec3(Floats(reader, n * 3));
                    r.normals = Vec3(Floats(reader, n * 3));
                    if (r.Has(StreamTangent))
                        r.tangents = Vec4(Floats(reader, n * 4), 4, 0);
                    if (r.Has(StreamColor))
                    {
                        var bytes = reader.ReadBytes(n * 4);
                        r.colors = new Color32[n];
                        for (int i = 0; i < n; i++)
                            r.colors[i] = new Color32(bytes[4 * i], bytes[4 * i + 1], bytes[4 * i + 2], bytes[4 * i + 3]);
                    }
                    r.uv0 = Vec2(Floats(reader, n * 2));
                    if (r.Has(StreamUv1))
                        r.uv1 = Vec2(Floats(reader, n * 2));
                    if (r.Has(StreamBinormal))
                        r.binormals = Vec3(Floats(reader, n * 3));
                    if (r.Has(StreamUv3))
                        r.uv3 = Vec2(Floats(reader, n * 2));
                    if (r.Has(StreamUv4))
                        r.uv4 = Vec2(Floats(reader, n * 2));
                    if (r.Has(StreamSkin))
                    {
                        r.joints = Ints(reader, n * 4);
                        r.weights = Floats(reader, n * 4);
                    }
                    if (r.Has(StreamBindFrame))
                    {
                        var frame = Floats(reader, n * 8);
                        r.bindNormal = Vec4(frame, 8, 0);
                        r.bindTangent = Vec4(frame, 8, 4);
                    }
                    for (int s = 0; s < submeshCount; s++)
                    {
                        int indexCount = (int)reader.ReadUInt32();
                        var indices = Ints(reader, indexCount);
                        foreach (int index in indices)
                            if (index < 0 || index >= n)
                                throw new InvalidDataException(r.name + ": index " + index + " outside " + n + " vertices");
                        r.submeshes.Add(indices);
                    }
                    if (r.boneCount > 0)
                    {
                        var f = Floats(reader, r.boneCount * 16);
                        r.bindposes = new Matrix4x4[r.boneCount];
                        for (int b = 0; b < r.boneCount; b++)
                        {
                            var matrix = new Matrix4x4();
                            for (int row = 0; row < 4; row++)
                                for (int column = 0; column < 4; column++)
                                    matrix[row, column] = f[b * 16 + row * 4 + column];
                            r.bindposes[b] = matrix;
                        }
                    }
                    list.Add(r);
                }
                if (reader.BaseStream.Position != reader.BaseStream.Length)
                    throw new InvalidDataException(path + ": trailing bytes after " + count + " meshes");
            }
            return list;
        }

        public static Mesh ToMesh(MeshRecord r)
        {
            var mesh = new Mesh { name = r.name };
            mesh.indexFormat = r.vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(r.positions);
            mesh.SetNormals(r.normals);
            if (r.tangents != null) mesh.SetTangents(r.tangents);
            if (r.colors != null) mesh.SetColors(r.colors);
            mesh.SetUVs(0, r.uv0);
            if (r.uv1 != null) mesh.SetUVs(1, r.uv1);
            if (r.binormals != null) mesh.SetUVs(2, r.binormals);
            if (r.uv3 != null) mesh.SetUVs(3, r.uv3);
            if (r.uv4 != null) mesh.SetUVs(4, r.uv4);
            if (r.bindNormal != null) mesh.SetUVs(5, r.bindNormal);
            if (r.bindTangent != null) mesh.SetUVs(6, r.bindTangent);
            mesh.subMeshCount = Math.Max(1, r.submeshes.Count);
            for (int s = 0; s < r.submeshes.Count; s++)
                mesh.SetIndices(r.submeshes[s], MeshTopology.Triangles, s, false);
            if (r.joints != null)
            {
                var weights = new BoneWeight[r.vertexCount];
                for (int v = 0; v < r.vertexCount; v++)
                {
                    int b = v * 4;
                    weights[v] = new BoneWeight
                    {
                        boneIndex0 = Joint(r, r.joints[b]), weight0 = r.weights[b],
                        boneIndex1 = Joint(r, r.joints[b + 1]), weight1 = r.weights[b + 1],
                        boneIndex2 = Joint(r, r.joints[b + 2]), weight2 = r.weights[b + 2],
                        boneIndex3 = Joint(r, r.joints[b + 3]), weight3 = r.weights[b + 3],
                    };
                }
                mesh.boneWeights = weights;
                mesh.bindposes = r.bindposes;
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        static int Joint(MeshRecord r, int joint)
        {
            if (joint < 0) return 0;
            if (joint >= r.boneCount)
                throw new InvalidDataException(r.name + ": joint " + joint + " outside " + r.boneCount + " bones");
            return joint;
        }

        public static List<ClipRecord> ReadClips(byte[] data, string path)
        {
            var list = new List<ClipRecord>();
            using (var reader = new BinaryReader(new MemoryStream(data, false)))
            {
                if (Magic(reader) != "HWCLIP01")
                    throw new InvalidDataException(path + ": not a HWCLIP01 stream");
                uint count = reader.ReadUInt32();
                for (uint c = 0; c < count; c++)
                {
                    var clip = new ClipRecord { name = Name(reader), length = reader.ReadSingle() };
                    uint curveCount = reader.ReadUInt32();
                    for (uint k = 0; k < curveCount; k++)
                    {
                        var curve = new CurveRecord { path = Name(reader), property = reader.ReadByte(), dims = reader.ReadByte() };
                        int keys = (int)reader.ReadUInt32();
                        curve.times = Floats(reader, keys);
                        curve.values = Floats(reader, keys * curve.dims);
                        clip.curves.Add(curve);
                    }
                    list.Add(clip);
                }
                if (reader.BaseStream.Position != reader.BaseStream.Length)
                    throw new InvalidDataException(path + ": trailing bytes after " + count + " clips");
            }
            return list;
        }

        static readonly string[][] CurveProperties =
        {
            new[] { "localPosition.x", "localPosition.y", "localPosition.z" },
            new[] { "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w" },
            new[] { "localScale.x", "localScale.y", "localScale.z" },
        };

        // Legacy clip with linear keys: the client interpolates translation and
        // scale linearly and rotations per component before normalizing.
        public static AnimationClip ToClip(ClipRecord record)
        {
            var clip = new AnimationClip { name = record.name, legacy = true, wrapMode = WrapMode.Loop, frameRate = 60 };
            foreach (var curve in record.curves)
            {
                if (curve.property < 0 || curve.property > 2)
                    throw new InvalidDataException(record.name + ": unknown curve property " + curve.property);
                var names = CurveProperties[curve.property];
                if (curve.dims != names.Length)
                    throw new InvalidDataException(record.name + ": curve " + curve.path + " has " + curve.dims + " components");
                for (int d = 0; d < curve.dims; d++)
                    clip.SetCurve(curve.path, typeof(Transform), names[d], Linear(curve.times, curve.values, curve.dims, d));
            }
            return clip;
        }

        static AnimationCurve Linear(float[] times, float[] values, int dims, int component)
        {
            var t = new List<float>();
            var v = new List<float>();
            for (int k = 0; k < times.Length; k++)
            {
                if (t.Count > 0 && times[k] <= t[t.Count - 1] + 1e-6f)
                {
                    // Coincident key: the later value wins, as in the client's key search.
                    v[v.Count - 1] = values[k * dims + component];
                    continue;
                }
                t.Add(times[k]);
                v.Add(values[k * dims + component]);
            }
            var keys = new Keyframe[t.Count];
            for (int k = 0; k < t.Count; k++)
            {
                float outSlope = k + 1 < t.Count ? (v[k + 1] - v[k]) / (t[k + 1] - t[k]) : 0f;
                float inSlope = k > 0 ? (v[k] - v[k - 1]) / (t[k] - t[k - 1]) : 0f;
                if (k == 0) inSlope = outSlope;
                if (k == t.Count - 1) outSlope = inSlope;
                keys[k] = new Keyframe(t[k], v[k], inSlope, outSlope) { weightedMode = WeightedMode.None };
            }
            return new AnimationCurve(keys);
        }
    }
}
