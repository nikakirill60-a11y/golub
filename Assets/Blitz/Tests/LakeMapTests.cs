using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Mirage ("20_lake_lk") is the first map ported as a binary client scene
    /// instead of an OBJ: every render batch keeps its own DAVA material,
    /// lightmap and vertex streams. These assertions pin the numbers a
    /// reviewer can read back out of the 4.8.0 .sc2 and maps.yaml.
    /// </summary>
    public sealed class LakeMapTests
    {
        private const string MapId = "lake";
        private const string SceneResource = "Maps/Lake/lake_scene";

        [Test]
        public void Manifest_MatchesTheClientScene()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            Assert.AreEqual("lake", map.Id);
            Assert.AreEqual("map:lake/name", map.NameKey);
            Assert.AreEqual("3d/Maps/20_lake_lk/20_lake_lk.sc2", map.Scene);
            Assert.AreEqual(SceneResource, map.ExternalScene);
            Assert.AreEqual("Maps/Lake/minimap", map.Minimap);
            Assert.AreEqual("Maps/Lake/loading_screen", map.LoadingScreen);
            Assert.IsTrue(map.ClientScene, "Mirage ships the binary client scene");
            Assert.IsTrue(map.ClientOrientation, "Mirage keeps DAVA handedness");

            // maps.yaml has `levels: [4, 5, 6, 7]`; the port opens every
            // map but Trial by Fire to tiers I-X.
            CollectionAssert.AreEqual(
                new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, map.RandomTiers);

            // Landscape bbox (-300,-300,0)..(300,300,50), 512 samples.
            Assert.AreEqual(512, map.Terrain.Side);
            Assert.AreEqual(600f, map.Terrain.Extent, 0.001f);
            Assert.AreEqual(-300f, map.Terrain.Origin[0], 0.001f);
            Assert.AreEqual(-300f, map.Terrain.Origin[2], 0.001f);
            Assert.AreEqual(8.596933f, map.Terrain.MinHeight, 0.0001f);
            Assert.AreEqual(50f, map.Terrain.MaxHeight, 0.0001f);

            // The landscape material of the .sc2, not Godot defaults.
            Assert.IsNotNull(map.Terrain.Client);
            Assert.AreEqual(55f, map.Terrain.Client.Tiling[0], 0.001f);
            Assert.AreEqual(55f, map.Terrain.Client.Tiling[1], 0.001f);

            // borderTop/BotX/Y of the landscape; Mirage keeps DAVA handedness,
            // so DAVA Y becomes Unity Z unchanged.
            Assert.AreEqual(-248.44f, map.BorderMin[0], 0.01f);
            Assert.AreEqual(246.97f, map.BorderMax[0], 0.01f);
            Assert.AreEqual(-261.62f, map.BorderMin[2], 0.01f);
            Assert.AreEqual(251.07f, map.BorderMax[2], 0.01f);
            Assert.AreEqual(0, map.SkyFaces.Count);
        }

        [Test]
        public void Catalog_OffersMirageToEveryTier()
        {
            for (var tier = 1; tier <= 10; tier++)
            {
                Assert.IsTrue(
                    BattleMapCatalog.AvailableForTier(tier).Contains(MapId),
                    "tier " + tier);
            }
        }

        /// <summary>
        /// The client lights Mirage with exponential fog (FOG_LINEAR 0,
        /// density 0.003) and darkens shadowed texels to the authored
        /// shadowColor instead of to black.
        /// </summary>
        [Test]
        public void Environment_CarriesExponentialFogAndShadowColour()
        {
            var env = BattleWorld.LoadDefinition(MapId).Environment;

            Assert.IsNotNull(env);
            Assert.IsFalse(env.FogLinear);
            Assert.AreEqual(0.003f, env.FogDensity, 0.00001f);
            Assert.AreEqual(0.75f, env.FogLimit, 0.001f);
            Assert.IsNotNull(env.ShadowColor);
            Assert.AreEqual(4, env.ShadowColor.Length);
            Assert.AreEqual(0.801297f, env.ShadowColor[0], 0.0001f);
            Assert.AreEqual(0.837964f, env.ShadowColor[2], 0.0001f);

            // The runtime sun travels along -Y of `Light Dynamic`: down and
            // towards +X/+Z, which is where the palm trunks' baked shadows
            // fall in the colour map.
            Assert.Less(env.SunDirection[1], -0.5f);
            Assert.Greater(env.SunDirection[0], 0.4f);
            Assert.Greater(env.SunDirection[2], 0.4f);
        }

        [Test]
        public void Spawns_PickTheWidestAuthoredPair()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            Assert.AreEqual(7, map.TeamA.Count);
            Assert.AreEqual(7, map.TeamB.Count);
            var allies = TeamCentre(map.TeamA);
            var enemies = TeamCentre(map.TeamB);
            Assert.Greater(Vector3.Distance(allies, enemies), 250f);

            var field = BattleWorld.LoadHeights(map);
            AssertOnTheGround(map, field, map.TeamA);
            AssertOnTheGround(map, field, map.TeamB);
        }

        [Test]
        public void AiMarkers_StandOnTheLandscape()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var field = BattleWorld.LoadHeights(map);

            Assert.AreEqual(18, map.AiMarkers.Count);
            Assert.AreEqual(4, map.AiMarkers.FindAll(m => m.Priority > 0).Count);
            for (var i = 0; i < map.AiMarkers.Count; i++)
            {
                var marker = map.AiMarkers[i];
                var ground = field.SampleHeight(
                    marker.Origin[0] - map.Terrain.Origin[0],
                    marker.Origin[2] - map.Terrain.Origin[2]);
                Assert.AreEqual(ground, marker.Origin[1], 2.5f,
                    marker.Name + " is not standing on the landscape");
            }
        }

        [Test]
        public void Shaders_CompileWithoutErrors()
        {
            var names = new[]
            {
                ClientMapScene.SurfaceShader,
                ClientMapScene.SkyShader,
                ClientMapScene.WaterShader,
                ClientMapScene.ShadowVolumeShader,
                ClientMapScene.LandscapeShader,
            };
            for (var i = 0; i < names.Length; i++)
            {
                var shader = Shader.Find(names[i]);
                Assert.IsNotNull(shader, names[i]);
                Assert.IsFalse(ShaderUtil.ShaderHasError(shader), names[i]);
            }
        }

        [Test]
        public void SceneTextures_AreLinearAndTheWaterCubeIsACubemap()
        {
            var guids = AssetDatabase.FindAssets(
                "t:Texture", new[] { "Assets/Blitz/Resources/Maps/Lake/Textures" });
            Assert.AreEqual(66, guids.Length);
            var cubes = 0;
            for (var i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                Assert.IsFalse(importer.sRGBTexture, path);
                Assert.IsTrue(importer.mipmapEnabled, path);
                if (path.EndsWith("_cube.png"))
                {
                    cubes++;
                    Assert.AreEqual(TextureImporterShape.TextureCube, importer.textureShape, path);
                }
            }

            Assert.Greater(cubes, 0);
        }

        [Test]
        public void ClientScene_BuildsEveryNodeWithItsMaterials()
        {
            var parent = new GameObject("lake-test");
            try
            {
                var scene = ClientMapScene.Build(parent.transform, SceneResource);

                Assert.AreEqual(1627, scene.Layout.nodes.Length);
                Assert.AreEqual(1627, scene.NodeCount);
                Assert.Greater(scene.TriangleCount, 600000);

                var nodes = scene.GetComponentsInChildren<ClientMapNode>(true);
                Assert.AreEqual(1627, nodes.Length);
                var solid = 0;
                var casters = 0;
                var kinds = new HashSet<string>();
                for (var i = 0; i < scene.Layout.materials.Length; i++)
                {
                    kinds.Add(scene.Layout.materials[i].kind);
                }

                for (var i = 0; i < nodes.Length; i++)
                {
                    if (nodes[i].HasSolid)
                    {
                        solid++;
                        Assert.IsNotNull(nodes[i].CollisionMesh, nodes[i].name);
                    }

                    var renderer = nodes[i].GetComponent<MeshRenderer>();
                    Assert.IsNotNull(renderer, nodes[i].name);
                    for (var m = 0; m < renderer.sharedMaterials.Length; m++)
                    {
                        Assert.IsNotNull(renderer.sharedMaterials[m], nodes[i].name);
                    }

                    if (renderer.shadowCastingMode != ShadowCastingMode.Off)
                    {
                        casters++;
                        Assert.IsTrue(nodes[i].CastsShadowVolume, nodes[i].name);
                    }
                }

                Assert.Greater(solid, 500);
                Assert.Greater(casters, 100);
                CollectionAssert.IsSupersetOf(kinds, new[]
                {
                    ClientMapScene.SurfaceKind,
                    ClientMapScene.SkyKind,
                    ClientMapScene.WaterKind,
                    ClientMapScene.ShadowKind,
                });
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void Destructibles_NameNodesThatExistInTheScene()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var layout = JsonUtility.FromJson<ClientMapLayout>(
                Resources.Load<TextAsset>(SceneResource + "_layout").text);
            var names = new HashSet<string>();
            for (var i = 0; i < layout.nodes.Length; i++)
            {
                names.Add(layout.nodes[i].name);
            }

            Assert.AreEqual(466, map.ExternalDestructibles.Count);
            for (var i = 0; i < map.ExternalDestructibles.Count; i++)
            {
                var item = map.ExternalDestructibles[i];
                Assert.IsTrue(names.Contains(item.IntactGroup), item.IntactGroup);
                if (!string.IsNullOrEmpty(item.WreckGroup))
                {
                    Assert.IsTrue(names.Contains(item.WreckGroup), item.WreckGroup);
                }
            }
        }

        [Test]
        public void Trees_FallUnderATankAndPropsBreakAsInTheClient()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            // 115 palm01 + 9 akacia01 (CollisionType 8); bushes stay foliage.
            Assert.AreEqual(124, map.ExternalTrees.Count);

            var parent = new GameObject("lake-trees-test");
            try
            {
                var objects = BattleObjects.BuildExternal(
                    parent.transform, map.ExternalScene,
                    map.ExternalDestructibles, map.ExternalTrees);
                Assert.AreEqual(124, objects.TreeCount);
                Assert.AreEqual(466, objects.DestructibleCount);

                var trees = objects.Root.GetComponentsInChildren<BattleTree>(true);
                Assert.AreEqual(124, trees.Length);
                for (var i = 0; i < trees.Length; i++)
                {
                    var sensor = trees[i].GetComponentInChildren<BattleTreeTrigger>(true);
                    Assert.IsNotNull(sensor, trees[i].name);
                    Assert.AreEqual(BattleLayers.Physics, sensor.gameObject.layer, trees[i].name);
                    Assert.IsTrue(sensor.GetComponent<Collider>().isTrigger, trees[i].name);

                    // The trunk no longer stops a tank as a static post.
                    var node = trees[i].GetComponentInChildren<ClientMapNode>(true);
                    Assert.IsNotNull(node, trees[i].name);
                    Assert.AreEqual(BattleLayers.Foliage, node.gameObject.layer, trees[i].name);
                }

                // A palm tips over away from the tank and stays down.
                var palm = trees[0];
                var nodeRenderer = palm.GetComponentInChildren<MeshRenderer>(true);
                var topBefore = nodeRenderer.bounds.max.y;
                var impact = palm.transform.position + Vector3.right * 3f;
                Assert.IsTrue(palm.FallFrom(impact));
                palm.Advance(palm.Duration + 0.1f);
                Assert.IsTrue(palm.Fallen);
                Assert.Less(nodeRenderer.bounds.max.y, topBefore - 1f, "the palm did not fall");
                Assert.Less(nodeRenderer.bounds.center.x, palm.transform.position.x,
                    "the palm fell towards the tank");
                Assert.IsFalse(palm.FallFrom(impact), "a fallen palm fell twice");

                // Military tents/boxes and concrete blocks: CollisionType 3
                // with a wreck state but no Health still break in 4.8.0.
                var destructibles = objects.Root.GetComponentsInChildren<BattleDestructible>(true);
                var switchOnly = 0;
                for (var i = 0; i < destructibles.Length; i++)
                {
                    var name = destructibles[i].ObjectName;
                    if (name.StartsWith("env_lk_28_MilitaryTent") ||
                        name.StartsWith("env_lk_MilitaryBox0") ||
                        name.StartsWith("fag_lk_02_block01"))
                    {
                        switchOnly++;
                        Assert.Greater(destructibles[i].Health, 0, name);
                    }
                }
                Assert.AreEqual(53, switchOnly);
                var tent = System.Array.Find(destructibles,
                    d => d.ObjectName.StartsWith("env_lk_28_MilitaryTent"));
                Assert.IsTrue(tent.Damage(1000));
                Assert.IsTrue(tent.Destroyed);
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void Navmesh_IsBakedForMirage()
        {
            var asset = Resources.Load<TextAsset>(BattleNavigation.ResourcePrefix + MapId);
            Assert.IsNotNull(asset, "the lake navmesh is not in Resources");
            var table = NavigationMeshTable.Parse(asset.bytes);
            Assert.Greater(table.RegionCount, 0);
        }

        private static void AssertOnTheGround(
            MapDefinition map, TerrainHeightField field, List<MapSpawnPoint> spawns)
        {
            for (var i = 0; i < spawns.Count; i++)
            {
                var origin = spawns[i].Origin;
                var ground = field.SampleHeight(
                    origin[0] - map.Terrain.Origin[0],
                    origin[2] - map.Terrain.Origin[2]);
                Assert.AreEqual(ground, origin[1], 0.05f,
                    spawns[i].Name + " is too far from the landscape");
            }
        }

        private static Vector3 TeamCentre(List<MapSpawnPoint> spawns)
        {
            var total = Vector3.zero;
            for (var i = 0; i < spawns.Count; i++)
            {
                var origin = spawns[i].Origin;
                total += new Vector3(origin[0], origin[1], origin[2]);
            }

            return total / spawns.Count;
        }
    }
}
