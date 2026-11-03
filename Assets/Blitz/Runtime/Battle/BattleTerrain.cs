using BlitzLegacy.Core.Maps;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Rebuilds the landscape from the exported height field. The source baked
    /// the same surface into a 12.6 MB ArrayMesh, but that mesh is a plain
    /// grid, so rebuilding it here is lossless and keeps the repository free of
    /// a multi-megabyte binary that Git would never diff.
    /// </summary>
    public static class BattleTerrain
    {
        /// <summary>
        /// The source landscape shader samples its colour map across the whole
        /// terrain, so UVs are simply the normalised source position.
        /// </summary>
        public static Mesh BuildMesh(TerrainHeightField field, string name)
        {
            if (field == null)
            {
                return null;
            }

            var side = field.Side;
            var vertices = new Vector3[field.VertexCount];
            var normals = new Vector3[field.VertexCount];
            var uv = new Vector2[field.VertexCount];
            var extentX = field.ExtentX;
            var extentZ = field.ExtentZ;

            for (var z = 0; z < side; z++)
            {
                for (var x = 0; x < side; x++)
                {
                    var index = z * side + x;
                    var sourceX = x * field.StepX;
                    var sourceZ = z * field.StepZ;
                    var point = GodotSpace.Point(sourceX, field.HeightAt(x, z), sourceZ);
                    vertices[index] = new Vector3(point.X, point.Y, point.Z);
                    float nx, ny, nz;
                    field.NormalAt(x, z, out nx, out ny, out nz);
                    var sourceNormal = GodotSpace.Point(nx, ny, nz);
                    normals[index] = new Vector3(
                        sourceNormal.X, sourceNormal.Y, sourceNormal.Z).normalized;
                    uv[index] = new Vector2(sourceX / extentX, sourceZ / extentZ);
                }
            }

            var quads = side - 1;
            var triangles = new int[field.TriangleCount * 3];
            var cursor = 0;
            for (var z = 0; z < quads; z++)
            {
                for (var x = 0; x < quads; x++)
                {
                    var v00 = z * side + x;
                    var v10 = v00 + 1;
                    var v01 = v00 + side;
                    var v11 = v01 + 1;

                    if (TerrainHeightField.IsForwardDiagonal(x, z))
                    {
                        cursor = Emit(triangles, cursor, v00, v11, v01);
                        cursor = Emit(triangles, cursor, v00, v10, v11);
                    }
                    else
                    {
                        cursor = Emit(triangles, cursor, v00, v10, v01);
                        cursor = Emit(triangles, cursor, v10, v11, v01);
                    }
                }
            }

            var mesh = new Mesh();
            mesh.name = name;
            // 513 * 513 vertices overflow a 16-bit index buffer.
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static int Emit(int[] triangles, int cursor, int a, int b, int c)
        {
            int x, y, z;
            GodotSpace.Triangle(a, b, c, out x, out y, out z);
            triangles[cursor] = x;
            triangles[cursor + 1] = y;
            triangles[cursor + 2] = z;
            return cursor + 3;
        }
    }
}
