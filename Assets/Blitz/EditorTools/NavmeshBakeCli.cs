using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Bakes the navigation surface of a map that has no source bake.
    ///
    /// Tools/port_godot_navmesh.py copies the NavigationRegion3D the Godot
    /// maps ship with, but a map taken straight from the 4.8.0 client has no
    /// such resource: the client plans bot moves on data that is not in the
    /// .sc2. Without any surface AiTankController falls back to steering at a
    /// clamped goal, which on Ordeal leaves every bot standing still.
    ///
    /// So the port bakes one from the map it just built, with the agent the
    /// source bake of Rudniki uses (radius 3, height 1.5, climb 0.5, slope
    /// 25, cell 1.5) and writes it in the same BLNM blob, in source space, so
    /// BattleNavigation stays a single code path for every map.
    /// </summary>
    public static class NavmeshBakeCli
    {
        public const string Magic = "BLNM";
        public const int Version = 1;

        /// <summary>The Rudniki bake settings, read back from its own blob.</summary>
        public const float CellSize = 1.5f;
        public const float CellHeight = 0.25f;
        public const float AgentRadius = 3f;
        public const float AgentHeight = 1.5f;
        public const float AgentClimb = 0.5f;
        public const float AgentSlope = 25f;

        private const string OutputFolder = "Assets/Blitz/Resources/Data";

        public static void Bake()
        {
            var mapId = Environment.GetEnvironmentVariable("BLITZ_BATTLE_MAP");
            mapId = string.IsNullOrEmpty(mapId) ? "ordeal" : mapId.Trim();
            try
            {
                EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var world = BattleWorld.Create(null, mapId);
                if (world == null)
                {
                    Debug.LogError("[Blitz] navmesh bake: the world did not build");
                    EditorApplication.Exit(1);
                    return;
                }

                var bounds = world.PlayArea;
                bounds.Expand(CellSize * 4f);

                var settings = NavMesh.GetSettingsByID(0);
                settings.agentRadius = AgentRadius;
                settings.agentHeight = AgentHeight;
                settings.agentClimb = AgentClimb;
                settings.agentSlope = AgentSlope;
                settings.overrideVoxelSize = true;
                settings.voxelSize = CellSize / 3f;

                // Only the static map stops a navigation agent: the foliage
                // layer is walked through, the tank layers do not exist yet,
                // and a destructible breaks when a tank rams it
                // (BattleDestructible.Ram). Baked as walls, the fences and
                // gates that ring Lost Temple's town cut the whole centre off
                // the spawns, so bots never left their corners.
                var mask = 1 << BattleLayers.Static;
                var sources = new List<NavMeshBuildSource>();
                NavMeshBuilder.CollectSources(
                    bounds, mask, NavMeshCollectGeometry.PhysicsColliders, 0,
                    new List<NavMeshBuildMarkup>(), sources);
                var solids = AddSolidProps(world, sources);
                Debug.Log("[Blitz] navmesh bake: solid prop boxes=" + solids);
                if (sources.Count == 0)
                {
                    Debug.LogError("[Blitz] navmesh bake: the map has no collider");
                    EditorApplication.Exit(1);
                    return;
                }

                var data = NavMeshBuilder.BuildNavMeshData(
                    settings, sources, bounds, Vector3.zero, Quaternion.identity);
                if (data == null)
                {
                    Debug.LogError("[Blitz] navmesh bake: Unity produced no surface");
                    EditorApplication.Exit(1);
                    return;
                }

                NavMesh.RemoveAllNavMeshData();
                var instance = NavMesh.AddNavMeshData(data);
                var surface = NavMesh.CalculateTriangulation();
                NavMesh.RemoveNavMeshData(instance);
                if (surface.indices.Length < 3)
                {
                    Debug.LogError("[Blitz] navmesh bake: the surface is empty");
                    EditorApplication.Exit(1);
                    return;
                }

                Directory.CreateDirectory(OutputFolder);
                var path = Path.Combine(
                    OutputFolder, "navmesh_" + mapId + ".bytes");
                File.WriteAllBytes(path, Encode(surface));
                AssetDatabase.ImportAsset(path);

                Debug.Log(
                    "[Blitz] navmesh baked: map=" + mapId +
                    " sources=" + sources.Count +
                    " vertices=" + surface.vertices.Length +
                    " triangles=" + (surface.indices.Length / 3) +
                    " radius=" + AgentRadius.ToString("F2") +
                    " out=" + path);
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogError("[Blitz] navmesh bake failed: " + error);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// The client props are exported as open render shells: a house or a
        /// container is four walls with no floor, and the voxeliser left the
        /// ground inside them walkable. On Karieri that routed bots straight
        /// through house walls, where they stood for the rest of the battle.
        /// Every solid static prop therefore also contributes a box over its
        /// own mesh bounds, oriented with the prop, so the bake treats it as
        /// the solid block a hull actually meets. Rocks and the surrounding
        /// terrain dressing keep their exact mesh: a box would close the
        /// slopes that run over them, and the big mine shed and the hangar are
        /// driven through: the Karieri control point sits inside the hangar.
        /// </summary>
        private static int AddSolidProps(BattleWorld world, List<NavMeshBuildSource> sources)
        {
            var added = 0;
            var colliders = world.GetComponentsInChildren<MeshCollider>(false);
            for (var i = 0; i < colliders.Length; i++)
            {
                var collider = colliders[i];
                if (collider.isTrigger || collider.sharedMesh == null ||
                    collider.gameObject.layer != BattleLayers.Static)
                {
                    continue;
                }

                var name = collider.gameObject.name;
                if (!(name.Contains("_bld_") || name.Contains("_env_")) ||
                    name.Contains("bigmineshed") || name.Contains("hangar") ||
                    name.Contains("_stn_"))
                {
                    continue;
                }

                var local = collider.sharedMesh.bounds;
                var size = Vector3.Scale(local.size, collider.transform.lossyScale);
                if (Mathf.Abs(size.y) < AgentClimb * 2f ||
                    Mathf.Max(Mathf.Abs(size.x), Mathf.Abs(size.z)) > 60f)
                {
                    continue;
                }

                var source = new NavMeshBuildSource();
                source.shape = NavMeshBuildSourceShape.Box;
                source.transform = collider.transform.localToWorldMatrix *
                    Matrix4x4.Translate(local.center);
                source.size = local.size;
                source.area = 0;
                sources.Add(source);
                added++;
            }

            return added;
        }

        /// <summary>
        /// One BLNM region holding the baked triangles, mirrored back into
        /// source space because NavigationMeshTable mirrors every vertex it
        /// reads through GodotSpace.
        /// </summary>
        public static byte[] Encode(NavMeshTriangulation surface)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                for (var i = 0; i < Magic.Length; i++)
                {
                    writer.Write((byte)Magic[i]);
                }

                writer.Write(Version);
                writer.Write(1);
                writer.Write(CellSize);
                writer.Write(CellHeight);
                writer.Write(AgentRadius);
                writer.Write(AgentHeight);
                writer.Write(AgentClimb);
                writer.Write(AgentSlope);

                writer.Write(surface.vertices.Length);
                for (var i = 0; i < surface.vertices.Length; i++)
                {
                    var point = surface.vertices[i];
                    writer.Write(point.x);
                    writer.Write(point.y);
                    writer.Write(-point.z);
                }

                var triangles = surface.indices.Length / 3;
                writer.Write(triangles);
                for (var i = 0; i < triangles; i++)
                {
                    writer.Write(3);
                    writer.Write(surface.indices[(i * 3) + 0]);
                    writer.Write(surface.indices[(i * 3) + 1]);
                    writer.Write(surface.indices[(i * 3) + 2]);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
