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
    /// Lost Temple ("10_asia_as") ships as a binary client scene like Mirage.
    /// It is the first port with invisible walls: render objects authored
    /// without the VISIBLE flag that the client never draws but still
    /// collides with. These assertions pin the numbers a reviewer can read
    /// back out of the 4.8.0 .sc2 and maps.yaml.
    /// </summary>
    public sealed class AsiaMapTests
    {
        private const string MapId = "asia";
        private const string SceneResource = "Maps/Asia/asia_scene";

        [Test]
        public void Manifest_MatchesTheClientScene()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            Assert.AreEqual("asia", map.Id);
            Assert.AreEqual("map:asia/name", map.NameKey);
            Assert.AreEqual("3d/Maps/10_asia_as/10_asia_as.sc2", map.Scene);
            Assert.AreEqual(SceneResource, map.ExternalScene);
            Assert.AreEqual("Maps/Asia/minimap", map.Minimap);
            Assert.AreEqual("Maps/Asia/loading_screen", map.LoadingScreen);
            Assert.IsTrue(map.ClientScene, "Lost Temple ships the binary client scene");
            Assert.IsTrue(map.ClientOrientation, "Lost Temple keeps DAVA handedness");

            // maps.yaml has `levels: [4, 5, 6, 7]`; the port opens every
            // map but Trial by Fire to tiers I-X.
            CollectionAssert.AreEqual(
                new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, map.RandomTiers);

            // Landscape bbox (-300,-300,0)..(300,300,55), 512 samples.
            Assert.AreEqual(512, map.Terrain.Side);
            Assert.AreEqual(600f, map.Terrain.Extent, 0.001f);
            Assert.AreEqual(-300f, map.Terrain.Origin[0], 0.001f);
            Assert.AreEqual(-300f, map.Terrain.Origin[2], 0.001f);
            Assert.AreEqual(20.944229f, map.Terrain.MinHeight, 0.0001f);
            Assert.AreEqual(55f, map.Terrain.MaxHeight, 0.0001f);

            // The landscape material of the .sc2, not Godot defaults.
            Assert.IsNotNull(map.Terrain.Client);
            Assert.AreEqual(40f, map.Terrain.Client.Tiling[0], 0.001f);
            Assert.AreEqual(40f, map.Terrain.Client.Tiling[1], 0.001f);

            // borderTop/BotX/Y of the landscape; Lost Temple keeps DAVA handedness,
            // so DAVA Y becomes Unity Z unchanged.
            Assert.AreEqual(-261.5f, map.BorderMin[0], 0.01f);
            Assert.AreEqual(266.5f, map.BorderMax[0], 0.01f);
            Assert.AreEqual(-264.6f, map.BorderMin[2], 0.01f);
            Assert.AreEqual(263.0f, map.BorderMax[2], 0.01f);
            Assert.AreEqual(0, map.SkyFaces.Count);
        }

        [Test]
        public void Catalog_OffersLostTempleToEveryTier()
        {
            for (var tier = 1; tier <= 10; tier++)
            {
                Assert.IsTrue(
                    BattleMapCatalog.AvailableForTier(tier).Contains(MapId),
                    "tier " + tier);
            }
        }

        /// <summary>
        /// The client lights Lost Temple with exponential fog (FOG_LINEAR 0,
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
            Assert.AreEqual(0.9f, env.FogLimit, 0.001f);
            Assert.IsNotNull(env.ShadowColor);
            Assert.AreEqual(4, env.ShadowColor.Length);
            Assert.AreEqual(0.643809f, env.ShadowColor[0], 0.0001f);
            Assert.AreEqual(0.824079f, env.ShadowColor[2], 0.0001f);

            // The runtime sun travels along -Y of `Light  dinamic` (two
            // spaces in the .sc2): down, mostly towards +Z, slightly -X.
            Assert.AreEqual(-0.122179f, env.SunDirection[0], 0.001f);
            Assert.AreEqual(-0.614262f, env.SunDirection[1], 0.001f);
            Assert.AreEqual(0.779586f, env.SunDirection[2], 0.001f);
        }

        [Test]
        public void Spawns_PickTheWidestAuthoredPair()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            // Ten of the fourteen authored spawn1N/spawn2N per side; team A
            // holds the north-east corner, team B the south-west one.
            Assert.AreEqual(10, map.TeamA.Count);
            Assert.AreEqual(10, map.TeamB.Count);
            var allies = TeamCentre(map.TeamA);
            var enemies = TeamCentre(map.TeamB);
            Assert.Greater(Vector3.Distance(allies, enemies), 250f);
            Assert.Greater(allies.x, 150f);
            Assert.Less(allies.z, -50f);
            Assert.Less(enemies.x, -50f);
            Assert.Greater(enemies.z, 150f);

            var field = BattleWorld.LoadHeights(map);
            AssertOnTheGround(map, field, map.TeamA);
            AssertOnTheGround(map, field, map.TeamB);
        }

        [Test]
        public void AiMarkers_StandOnTheLandscape()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var field = BattleWorld.LoadHeights(map);

            // Control point, 14 botspawn and two of the three strategic
            // points: strategicpoint_2 shares the control point's spot and
            // is thinned away as a duplicate goal.
            Assert.AreEqual(17, map.AiMarkers.Count);
            Assert.AreEqual(3, map.AiMarkers.FindAll(m => m.Priority > 0).Count);
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
                "t:Texture", new[] { "Assets/Blitz/Resources/Maps/Asia/Textures" });
            Assert.AreEqual(46, guids.Length);
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
            var parent = new GameObject("asia-test");
            try
            {
                var scene = ClientMapScene.Build(parent.transform, SceneResource);

                Assert.AreEqual(2133, scene.Layout.nodes.Length);
                Assert.AreEqual(2133, scene.NodeCount);
                Assert.Greater(scene.TriangleCount, 1000000);

                var nodes = scene.GetComponentsInChildren<ClientMapNode>(true);
                Assert.AreEqual(2133, nodes.Length);
                var solid = 0;
                var casters = 0;
                var hidden = 0;
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

                    if (!renderer.enabled)
                    {
                        // Only the invisible walls are authored without the
                        // VISIBLE render-object flag.
                        hidden++;
                        StringAssert.Contains("invisiblewall", nodes[i].name);
                        Assert.AreEqual(ShadowCastingMode.Off, renderer.shadowCastingMode, nodes[i].name);
                        Assert.IsTrue(nodes[i].HasSolid, nodes[i].name);
                    }

                    if (renderer.shadowCastingMode != ShadowCastingMode.Off)
                    {
                        casters++;
                        Assert.IsTrue(nodes[i].CastsShadowVolume, nodes[i].name);
                    }
                }

                Assert.AreEqual(22, hidden);
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

            Assert.AreEqual(543, map.ExternalDestructibles.Count);
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
            // 113 asia_sosna_* pines (CollisionType 8, FallType 0);
            // asia_bush_01 has no FallType and stays foliage.
            Assert.AreEqual(113, map.ExternalTrees.Count);

            var parent = new GameObject("asia-trees-test");
            try
            {
                var objects = BattleObjects.BuildExternal(
                    parent.transform, map.ExternalScene,
                    map.ExternalDestructibles, map.ExternalTrees);
                Assert.AreEqual(113, objects.TreeCount);
                Assert.AreEqual(543, objects.DestructibleCount);

                var trees = objects.Root.GetComponentsInChildren<BattleTree>(true);
                Assert.AreEqual(113, trees.Length);
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

                // A pine tips over away from the tank and stays down.
                var pine = trees[0];
                var nodeRenderer = pine.GetComponentInChildren<MeshRenderer>(true);
                var topBefore = nodeRenderer.bounds.max.y;
                var impact = pine.transform.position + Vector3.right * 3f;
                Assert.IsTrue(pine.FallFrom(impact));
                pine.Advance(pine.Duration + 0.1f);
                Assert.IsTrue(pine.Fallen);
                Assert.Less(nodeRenderer.bounds.max.y, topBefore - 1f, "the pine did not fall");
                Assert.Less(nodeRenderer.bounds.center.x, pine.transform.position.x,
                    "the pine fell towards the tank");
                Assert.IsFalse(pine.FallFrom(impact), "a fallen pine fell twice");

                // Rickshaws: CollisionType 3 with a wreck state but no Health
                // still break in 4.8.0.
                var destructibles = objects.Root.GetComponentsInChildren<BattleDestructible>(true);
                var switchOnly = 0;
                for (var i = 0; i < destructibles.Length; i++)
                {
                    var name = destructibles[i].ObjectName;
                    if (name.StartsWith("env_07_rick01"))
                    {
                        switchOnly++;
                        Assert.Greater(destructibles[i].Health, 0, name);
                    }
                }
                Assert.AreEqual(26, switchOnly);
                var rickshaw = System.Array.Find(destructibles,
                    d => d.ObjectName.StartsWith("env_07_rick01"));
                Assert.IsTrue(rickshaw.Damage(1000));
                Assert.IsTrue(rickshaw.Destroyed);

                // Invisible walls stay solid static colliders without a
                // visible renderer.
                var walls = 0;
                var renderers = objects.Root.GetComponentsInChildren<MeshRenderer>(true);
                for (var i = 0; i < renderers.Length; i++)
                {
                    if (!renderers[i].name.Contains("invisiblewall"))
                    {
                        continue;
                    }

                    walls++;
                    Assert.IsFalse(renderers[i].enabled, renderers[i].name);
                    Assert.AreEqual(BattleLayers.Static, renderers[i].gameObject.layer, renderers[i].name);
                    var wall = renderers[i].GetComponent<MeshCollider>();
                    Assert.IsNotNull(wall, renderers[i].name);
                    Assert.IsNotNull(wall.sharedMesh, renderers[i].name);
                    Assert.IsFalse(wall.isTrigger, renderers[i].name);
                }
                Assert.AreEqual(22, walls);
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void Navmesh_IsBakedForLostTemple()
        {
            var asset = Resources.Load<TextAsset>(BattleNavigation.ResourcePrefix + MapId);
            Assert.IsNotNull(asset, "the asia navmesh is not in Resources");
            var table = NavigationMeshTable.Parse(asset.bytes);
            Assert.Greater(table.RegionCount, 0);
        }

        /// <summary>
        /// Fences and gates ring the town in the middle. Baked as walls they
        /// cut the control point off both corners; a tank rams through them,
        /// so the bake must not.
        /// </summary>
        [Test]
        public void Navmesh_ReachesEveryGoalFromBothSpawns()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var root = new GameObject("asia-navigation-test");
            try
            {
                var navigation = BattleNavigation.Create(root.transform, MapId);
                Assert.IsTrue(navigation.Ready);
                var teams = new[] { map.TeamA, map.TeamB };
                for (var t = 0; t < teams.Length; t++)
                {
                    Vector3 from;
                    Assert.IsTrue(navigation.ClosestPoint(Point(teams[t][0].Origin), out from), "team " + t);
                    for (var m = 0; m < map.AiMarkers.Count; m++)
                    {
                        Vector3 goal;
                        var marker = map.AiMarkers[m];
                        Assert.IsTrue(navigation.ClosestPoint(Point(marker.Origin), out goal), marker.Name);
                        Assert.IsTrue(navigation.PathReaches(from, goal),
                            marker.Name + " is cut off from team " + t);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static Vector3 Point(float[] origin)
        {
            var point = GodotSpace.Point(origin);
            return new Vector3(point.X, point.Y, point.Z);
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
