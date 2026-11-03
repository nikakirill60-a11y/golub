using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The ported navigation mesh.
    ///
    /// The source plans every bot move on the navmesh baked into the map scene
    /// and asks NavigationServer3D two questions: map_get_closest_point and
    /// map_get_path. Unity answers both, so the port rebuilds the baked surface
    /// as NavMeshData rather than deriving a walkable area of its own. A
    /// derived surface would disagree with the source exactly where it matters
    /// at Rudniki - the lake and the quarry walls are missing from the bake,
    /// which is why the source bots never drive into the water and never grind
    /// against a slope they cannot climb.
    /// </summary>
    public sealed class BattleNavigation : MonoBehaviour
    {
        /// <summary>Resource prefix written by Tools/port_godot_navmesh.py.</summary>
        public const string ResourcePrefix = "Data/navmesh_";

        /// <summary>
        /// <c>map_get_path</c>'s own tolerance for "this path reached the goal",
        /// which the source uses to reject partial corridors.
        /// </summary>
        public const float GoalReachedDistance = 1f;

        /// <summary>
        /// <c>map_get_closest_point</c> searches the whole map, while
        /// SamplePosition needs a radius, so the port widens one until the
        /// surface answers. The ladder ends past the diagonal of Rudniki, which
        /// keeps "there is no navmesh near this point" a real answer instead of
        /// a search that gave up early.
        /// </summary>
        private static readonly float[] SampleRadii = { 4f, 16f, 64f, 256f, 1024f };

        private NavMeshData _data;
        private NavMeshDataInstance _instance;
        private Mesh _mesh;
        private NavMeshPath _probe;
        private NavMeshPath _route;

        /// <summary>The navigation of the battle being played, if any.</summary>
        public static BattleNavigation Current { get; private set; }

        /// <summary>Whether a baked surface is loaded and queryable.</summary>
        public bool Ready
        {
            get { return _instance.valid; }
        }

        /// <summary>Polygons in the ported bake, for the battle log.</summary>
        public int PolygonCount { get; private set; }

        /// <summary>Those polygons fanned into triangles for the builder.</summary>
        public int TriangleCount { get; private set; }

        public static BattleNavigation Create(Transform parent, string mapId)
        {
            var holder = new GameObject("Navigation");
            holder.transform.SetParent(parent, false);
            var navigation = holder.AddComponent<BattleNavigation>();
            navigation.Build(mapId);
            return navigation;
        }

        private void Awake()
        {
            Current = this;
        }

        private void Build(string mapId)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var asset = Resources.Load<TextAsset>(ResourcePrefix + mapId);
            if (asset == null)
            {
                Debug.Log("[Blitz] no ported navmesh for map " + mapId);
                return;
            }

            NavigationMeshTable table;
            try
            {
                table = NavigationMeshTable.Parse(asset.bytes);
            }
            catch (ArgumentException error)
            {
                Debug.LogError("[Blitz] unreadable navmesh for " + mapId + ": " + error.Message);
                return;
            }

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var polygons = 0;
            for (var r = 0; r < table.RegionCount; r++)
            {
                var region = table.RegionAt(r);
                polygons += region.PolygonCount;
                Append(region, vertices, triangles);
            }

            if (triangles.Count == 0)
            {
                Debug.LogError("[Blitz] the ported navmesh for " + mapId + " is empty");
                return;
            }

            _mesh = new Mesh();
            _mesh.name = "Navmesh_" + mapId;
            _mesh.indexFormat = vertices.Count > 65000
                ? IndexFormat.UInt32
                : IndexFormat.UInt16;
            _mesh.SetVertices(vertices);
            _mesh.SetTriangles(triangles, 0);
            _mesh.RecalculateBounds();

            var first = table.RegionAt(0);
            var settings = NavMesh.GetSettingsByID(0);
            // The baked polygons are already inset by agent_radius, so insetting
            // them again would shave another 3.5 metres off every wall.
            settings.agentRadius = 0f;
            settings.agentHeight = first.AgentHeight;
            settings.agentClimb = first.AgentMaxClimb;
            settings.agentSlope = first.AgentMaxSlope;
            settings.overrideVoxelSize = true;
            // The source cell is the resolution its bake was rasterised at, not
            // a resolution to rasterise a finished surface with again: one
            // voxel per source cell would round the outline of every polygon
            // away. Three voxels per cell is the ratio Unity itself keeps
            // between agent radius and voxel size, and it reproduces the source
            // outline to within a voxel.
            settings.voxelSize = first.CellSize / 3f;

            var source = new NavMeshBuildSource();
            source.shape = NavMeshBuildSourceShape.Mesh;
            source.sourceObject = _mesh;
            source.transform = Matrix4x4.identity;
            source.area = 0;

            var sources = new List<NavMeshBuildSource>(1);
            sources.Add(source);

            var bounds = _mesh.bounds;
            // One voxel of slack, so a polygon lying exactly on the bake bounds
            // is not clipped away by the voxeliser.
            bounds.Expand(first.CellSize * 2f);

            _data = NavMeshBuilder.BuildNavMeshData(
                settings, sources, bounds, Vector3.zero, Quaternion.identity);
            if (_data == null)
            {
                Debug.LogError("[Blitz] could not build the navmesh for " + mapId);
                return;
            }

            _data.name = "Navmesh_" + mapId;
            _instance = NavMesh.AddNavMeshData(_data);
            PolygonCount = polygons;
            TriangleCount = triangles.Count / 3;

            var built = NavMesh.CalculateTriangulation();
            Debug.Log(
                "[Blitz] navmesh built: map=" + mapId +
                " source=" + PolygonCount + " polygons/" + TriangleCount + " tris" +
                " baked=" + (built.indices.Length / 3) + " tris" +
                " radius=" + first.AgentRadius.ToString("F2") +
                " climb=" + first.AgentMaxClimb.ToString("F2") +
                " slope=" + first.AgentMaxSlope.ToString("F1") +
                " cell=" + first.CellSize.ToString("F2") +
                " voxel=" + settings.voxelSize.ToString("F2") +
                " in " + watch.ElapsedMilliseconds + " ms");
        }

        /// <summary>
        /// Fans one region's convex polygons into triangles. Mirroring the
        /// source across Z reverses a polygon's winding, and a navmesh triangle
        /// facing down is not walkable, so the winding is taken from the
        /// polygon's own normal instead of from a convention.
        /// </summary>
        private static void Append(
            NavigationMeshTable.Region region,
            List<Vector3> vertices,
            List<int> triangles)
        {
            var offset = vertices.Count;
            for (var v = 0; v < region.VertexCount; v++)
            {
                var point = region.VertexAt(v);
                vertices.Add(new Vector3(point.X, point.Y, point.Z));
            }

            for (var p = 0; p < region.PolygonCount; p++)
            {
                var polygon = region.PolygonAt(p);
                var up = PolygonUp(vertices, offset, polygon);
                for (var i = 2; i < polygon.Length; i++)
                {
                    var a = offset + polygon[0];
                    var b = offset + polygon[i - 1];
                    var c = offset + polygon[i];
                    if (up)
                    {
                        triangles.Add(a);
                        triangles.Add(b);
                        triangles.Add(c);
                    }
                    else
                    {
                        triangles.Add(a);
                        triangles.Add(c);
                        triangles.Add(b);
                    }
                }
            }
        }

        /// <summary>Whether the polygon's Newell normal already points up.</summary>
        private static bool PolygonUp(List<Vector3> vertices, int offset, int[] polygon)
        {
            var normal = Vector3.zero;
            for (var i = 0; i < polygon.Length; i++)
            {
                var current = vertices[offset + polygon[i]];
                var next = vertices[offset + polygon[(i + 1) % polygon.Length]];
                normal.x += (current.y - next.y) * (current.z + next.z);
                normal.y += (current.z - next.z) * (current.x + next.x);
                normal.z += (current.x - next.x) * (current.y + next.y);
            }

            return normal.y >= 0f;
        }

        /// <summary><c>NavigationServer3D.map_get_closest_point</c>.</summary>
        public bool ClosestPoint(Vector3 point, out Vector3 projected)
        {
            projected = Vector3.zero;
            if (!Ready)
            {
                return false;
            }

            for (var i = 0; i < SampleRadii.Length; i++)
            {
                NavMeshHit hit;
                if (NavMesh.SamplePosition(point, out hit, SampleRadii[i], NavMesh.AllAreas))
                {
                    projected = hit.position;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// <c>navigation_path_reaches</c>: a reachability probe, not the route
        /// used for steering.
        /// </summary>
        public bool PathReaches(Vector3 from, Vector3 projected)
        {
            if (!Ready)
            {
                return false;
            }

            if (_probe == null)
            {
                _probe = new NavMeshPath();
            }

            if (!NavMesh.CalculatePath(from, projected, NavMesh.AllAreas, _probe))
            {
                return false;
            }

            var corners = _probe.corners;
            if (corners.Length == 0)
            {
                return false;
            }

            return Vector3.Distance(corners[corners.Length - 1], projected) <= GoalReachedDistance;
        }

        /// <summary>
        /// <c>NavigationServer3D.map_get_path(..., optimize = true)</c>: the
        /// corner list the steering follows. Unity always funnels its corridor,
        /// which is what the source's optimized query asks for.
        /// </summary>
        public bool CalculateRoute(Vector3 from, Vector3 to, List<Vector3> route)
        {
            route.Clear();
            if (!Ready)
            {
                return false;
            }

            if (_route == null)
            {
                _route = new NavMeshPath();
            }

            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, _route))
            {
                return false;
            }

            var corners = _route.corners;
            for (var i = 0; i < corners.Length; i++)
            {
                route.Add(corners[i]);
            }

            return route.Count > 0;
        }

        private void OnDestroy()
        {
            if (_instance.valid)
            {
                _instance.Remove();
            }

            if (_mesh != null)
            {
                Destroy(_mesh);
                _mesh = null;
            }

            _data = null;
            _probe = null;
            _route = null;
            if (Current == this)
            {
                Current = null;
            }
        }
    }
}
