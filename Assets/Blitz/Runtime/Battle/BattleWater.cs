using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The water of the loaded map as a gameplay surface. Every map family
    /// draws its rivers and lakes with one of the three water shaders, so the
    /// field is gathered from exactly those sub-meshes: their up-facing
    /// triangles become the height field tanks are sampled against. A mesh
    /// the importer left unreadable falls back to the top of its bounds,
    /// which is what a flat water plane is anyway.
    /// </summary>
    public sealed class BattleWater
    {
        private static readonly HashSet<string> WaterShaders = new HashSet<string>
        {
            BattleObjects.WaterShaderName,
            ClientMapScene.WaterShader,
            ClientMapScene.GodotWaterShader,
        };

        private BattleWater(WaterSurfaceField field, int renderers)
        {
            Field = field;
            RendererCount = renderers;
        }

        /// <summary>The water of the battle that is running, or null on a dry map.</summary>
        public static BattleWater Active { get; private set; }

        public WaterSurfaceField Field { get; private set; }

        /// <summary>Water renderers the field was built from.</summary>
        public int RendererCount { get; private set; }

        public int TriangleCount { get { return Field.TriangleCount; } }

        /// <summary>Makes <paramref name="water"/> the battle's water; null clears it.</summary>
        public static void SetActive(BattleWater water)
        {
            Active = water;
        }

        /// <summary>Clears the active water if it is still <paramref name="water"/>.</summary>
        public static void Release(BattleWater water)
        {
            if (Active == water) { Active = null; }
        }

        /// <summary>Surface height at a world position, if there is water there.</summary>
        public bool TrySample(Vector3 position, out float height)
        {
            return Field.TrySample(position.x, position.z, out height);
        }

        /// <summary>Whether <paramref name="material"/> draws map water.</summary>
        public static bool IsWaterMaterial(Material material)
        {
            return material != null && material.shader != null && WaterShaders.Contains(material.shader.name);
        }

        /// <summary>Collects the water under <paramref name="root"/>; null when there is none.</summary>
        public static BattleWater Build(Transform root)
        {
            if (root == null) { return null; }
            var field = new WaterSurfaceField();
            var renderers = 0;
            var indices = new List<int>();
            var vertices = new List<Vector3>();
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var materials = renderer.sharedMaterials;
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || materials == null) { continue; }
                var used = false;
                for (var sub = 0; sub < materials.Length && sub < mesh.subMeshCount; sub++)
                {
                    if (!IsWaterMaterial(materials[sub])) { continue; }
                    used = true;
                    if (mesh.isReadable)
                    {
                        AddSubMesh(field, renderer.transform.localToWorldMatrix, mesh, sub, vertices, indices);
                    }
                    else
                    {
                        AddBoundsTop(field, renderer.bounds);
                    }
                }

                if (used) { renderers++; }
            }

            if (field.TriangleCount == 0) { return null; }
            field.Build();
            return new BattleWater(field, renderers);
        }

        private static void AddSubMesh(
            WaterSurfaceField field, Matrix4x4 matrix, Mesh mesh, int sub,
            List<Vector3> vertices, List<int> indices)
        {
            if (mesh.GetTopology(sub) != MeshTopology.Triangles) { return; }
            vertices.Clear();
            indices.Clear();
            mesh.GetVertices(vertices);
            mesh.GetTriangles(indices, sub);
            for (var i = 0; i < vertices.Count; i++) { vertices[i] = matrix.MultiplyPoint3x4(vertices[i]); }
            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                var a = vertices[indices[i]];
                var b = vertices[indices[i + 1]];
                var c = vertices[indices[i + 2]];
                field.AddTriangle(a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z);
            }
        }

        private static void AddBoundsTop(WaterSurfaceField field, Bounds bounds)
        {
            var y = bounds.max.y;
            field.AddTriangle(bounds.min.x, y, bounds.min.z, bounds.max.x, y, bounds.max.z, bounds.max.x, y, bounds.min.z);
            field.AddTriangle(bounds.min.x, y, bounds.min.z, bounds.min.x, y, bounds.max.z, bounds.max.x, y, bounds.max.z);
        }
    }
}
