using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Chest
{
    /// <summary>
    /// DAVA ParticleRenderObject::AppendParticleGroup for the chest: one quad
    /// per particle and facing basis, built in DAVA space and stored in Unity
    /// axes. Effects are drawn back to front with RenderBatchArray's
    /// SORT_BY_DISTANCE_BACK_TO_FRONT key (stable), groups in effect order and
    /// particles newest first, like DAVA's prepend-only particle list.
    /// </summary>
    public sealed class Chest480ParticleMesh
    {
        /// <summary>Default render batch sorting offset (31 - 15).</summary>
        private const uint SortingBias = 16u;

        public struct BatchInfo
        {
            public int Sprite;
            public int Blending;
            public int FirstIndex;
            public int IndexCount;
        }

        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector2> _uv = new List<Vector2>();
        private readonly List<Color32> _colors = new List<Color32>();
        private readonly List<int> _indices = new List<int>();
        private readonly List<BatchInfo> _batches = new List<BatchInfo>();
        private readonly List<KeyValuePair<uint, Chest480Particles.Effect>> _order = new List<KeyValuePair<uint, Chest480Particles.Effect>>();
        private readonly DavaVector3[] _basis = new DavaVector3[14];
        private readonly int[] _faces = new int[4];

        public int BatchCount { get { return _batches.Count; } }
        public int QuadCount { get { return _vertices.Count / 4; } }
        public BatchInfo Batch(int index) { return _batches[index]; }
        public IList<Vector3> Vertices { get { return _vertices; } }
        public IList<Color32> Colors { get { return _colors; } }

        public void Build(Chest480Particles particles, Chest480Camera camera)
        {
            _vertices.Clear();
            _uv.Clear();
            _colors.Clear();
            _indices.Clear();
            _batches.Clear();

            var position = DavaVector3.From(camera.position);
            var target = DavaVector3.From(camera.target);
            var up = DavaVector3.From(camera.up);
            var direction = (target - position).Normalized();
            // Matrix4::BuildLookAtMatrix: columns 0 and 1 of the view matrix.
            var vz = (position - target).Normalized();
            var vx = up.Cross(vz).Normalized();
            var vy = vz.Cross(vx).Normalized();
            _basis[0] = vx;
            _basis[1] = vy;
            _basis[8] = new DavaVector3(0f, 1f, 0f); _basis[9] = new DavaVector3(0f, 0f, 1f);
            _basis[10] = new DavaVector3(1f, 0f, 0f); _basis[11] = new DavaVector3(0f, 0f, 1f);
            _basis[12] = new DavaVector3(0f, 1f, 0f); _basis[13] = new DavaVector3(1f, 0f, 0f);

            _order.Clear();
            foreach (var effect in particles.Effects)
            {
                if (effect.Groups.Count == 0) continue;
                var delta = effect.Position - position;
                var distance = delta.Dot(direction) < 0f ? 0u : (uint)(delta.Length * 1000f);
                _order.Add(new KeyValuePair<uint, Chest480Particles.Effect>(distance + SortingBias, effect));
            }
            // Stable: far first, scene order for equal keys.
            for (var i = 1; i < _order.Count; i++)
            {
                var item = _order[i];
                var j = i - 1;
                while (j >= 0 && _order[j].Key < item.Key)
                {
                    _order[j + 1] = _order[j];
                    j--;
                }
                _order[j + 1] = item;
            }

            foreach (var entry in _order)
            {
                var effect = entry.Value;
                var w = effect.World;
                var ex = new DavaVector3(w[0], w[1], w[2]).Normalized();
                var ey = new DavaVector3(w[4], w[5], w[6]).Normalized();
                var ez = new DavaVector3(w[8], w[9], w[10]).Normalized();
                _basis[2] = ey; _basis[3] = ez;
                _basis[4] = ex; _basis[5] = ez;
                _basis[6] = ey; _basis[7] = ex;
                foreach (var group in effect.Groups)
                {
                    if (group.Particles.Count == 0) continue;
                    AppendGroup(effect, group, direction);
                }
            }
        }

        private void AppendGroup(Chest480Particles.Effect effect, Chest480Particles.Group group, DavaVector3 cameraDirection)
        {
            var layer = group.Layer;
            var source = layer.Source;
            var faces = Faces(source.orientation);
            var first = _indices.Count;
            var particles = group.Particles;
            for (var p = particles.Count - 1; p >= 0; p--)
            {
                var particle = particles[p];
                var overLife = particle.Life / particle.LifeTime;
                var color = particle.Color;
                if (!layer.ColorOverLife.IsNull) color = layer.ColorOverLife.Color(overLife);
                if (!layer.AlphaOverLife.IsNull) color.a = layer.AlphaOverLife.Scalar(overLife);
                var color32 = new Color32(Byte(color.r), Byte(color.g), Byte(color.b), Byte(Math.Min(color.a, 1f)));
                // "-" because artists consider positive rotation to be clockwise.
                var sin = (float)Math.Sin(-particle.Angle);
                var cos = (float)Math.Cos(-particle.Angle);
                var center = particle.Position;
                if (layer.InheritPosition) center = center + effect.Position;
                for (var f = 0; f < faces; f++)
                {
                    var ex = _basis[_faces[f] * 2];
                    var ey = _basis[_faces[f] * 2 + 1];
                    if (source.isLong != 0)
                    {
                        ey = particle.Speed;
                        var velocity = ey.Length;
                        var scaleBase = 0f;
                        if (velocity < Chest480Particles.Epsilon) ey = new DavaVector3(0f, 0f, 1f);
                        else scaleBase = source.scaleVelocityBase / velocity;
                        ex = ey.Cross(cameraDirection).Normalized();
                        ey = ey * (scaleBase + source.scaleVelocityFactor);
                    }
                    var left = ex * cos + ey * sin;
                    var right = -left;
                    var top = ey * -cos + ex * sin;
                    var bottom = -top;
                    left = left * (0.5f * particle.CurrSize.x * (1f + layer.PivotX));
                    right = right * (0.5f * particle.CurrSize.x * (1f - layer.PivotX));
                    top = top * (0.5f * particle.CurrSize.y * (1f + layer.PivotY));
                    bottom = bottom * (0.5f * particle.CurrSize.y * (1f - layer.PivotY));
                    var start = _vertices.Count;
                    _vertices.Add((center + left + bottom).ToUnity());
                    _vertices.Add((center + right + bottom).ToUnity());
                    _vertices.Add((center + left + top).ToUnity());
                    _vertices.Add((center + right + top).ToUnity());
                    // DAVA gives v0 the sprite's top-left texel (UV v is flipped for Unity).
                    _uv.Add(new Vector2(0f, 1f));
                    _uv.Add(new Vector2(1f, 1f));
                    _uv.Add(new Vector2(0f, 0f));
                    _uv.Add(new Vector2(1f, 0f));
                    for (var c = 0; c < 4; c++) _colors.Add(color32);
                    _indices.Add(start); _indices.Add(start + 1); _indices.Add(start + 2);
                    _indices.Add(start + 2); _indices.Add(start + 1); _indices.Add(start + 3);
                }
            }
            var count = _indices.Count - first;
            if (count == 0) return;
            var last = _batches.Count - 1;
            if (last >= 0 && _batches[last].Sprite == source.sprite && _batches[last].Blending == source.blending)
            {
                var merged = _batches[last];
                merged.IndexCount += count;
                _batches[last] = merged;
            }
            else
            {
                _batches.Add(new BatchInfo { Sprite = source.sprite, Blending = source.blending, FirstIndex = first, IndexCount = count });
            }
        }

        /// <summary>ParticleRenderObject::PrepareBasisIndexes.</summary>
        private int Faces(int orientation)
        {
            var count = 0;
            var world = (orientation & 16) != 0;
            if ((orientation & 1) != 0) _faces[count++] = 0;
            if ((orientation & 2) != 0) _faces[count++] = world ? 4 : 1;
            if ((orientation & 4) != 0) _faces[count++] = world ? 5 : 2;
            if ((orientation & 8) != 0) _faces[count++] = world ? 6 : 3;
            return count;
        }

        /// <summary>rhi::NativeColorRGBA: clamp, then truncate to 8 bits.</summary>
        private static byte Byte(float value)
        {
            if (!(value > 0f)) return 0;
            if (value >= 1f) return 255;
            return (byte)(value * 255f);
        }

        public void Apply(Mesh mesh)
        {
            mesh.Clear(false);
            mesh.indexFormat = _vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_vertices);
            mesh.SetUVs(0, _uv);
            mesh.SetColors(_colors);
            mesh.subMeshCount = Math.Max(1, _batches.Count);
            for (var i = 0; i < _batches.Count; i++)
            {
                var batch = _batches[i];
                mesh.SetIndices(_indices.GetRange(batch.FirstIndex, batch.IndexCount), MeshTopology.Triangles, i, false);
            }
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
        }
    }
}
