using System.Collections.Generic;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Lighting;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Sea of Tranquility ("40_moon_mn") is the first map ported from the
    /// 11.20 client pack: decal, lit and additive materials, two skinned
    /// props playing their clips, generated vegetation, a landscape without
    /// highlights and no shadow maps. These assertions pin the numbers a
    /// reviewer can read back out of the 11.20 .sc2, its clips and maps.yaml.
    /// </summary>
    public sealed class MoonMapTests
    {
        private const string MapId = "moon";
        private const string SceneResource = "Maps/Moon/moon_scene";
        private const string Ufo = "entity_160_UFO2_sc2";
        private const string Duck = "entity_161_stone_duck_space_sc2";

        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }

            // Edit mode never calls OnDestroy on a plain MonoBehaviour, so the
            // navmesh a world or a BattleNavigation adds outlives its root. A
            // second copy over the same ground splits paths between the two
            // and walls a marker off that the map's own surface reaches.
            NavMesh.RemoveAllNavMeshData();
        }

        [Test]
        public void Manifest_MatchesTheClientScene()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            Assert.AreEqual("moon", map.Id);
            Assert.AreEqual("map:moon/name", map.NameKey);
            Assert.AreEqual("3d/Maps/40_moon_mn/40_moon_mn.sc2", map.Scene);
            Assert.AreEqual(SceneResource, map.ExternalScene);
            Assert.AreEqual("Maps/Moon/minimap", map.Minimap);
            Assert.AreEqual("Maps/Moon/loading_screen", map.LoadingScreen);
            Assert.AreEqual("map_intro_moon", map.IntroMusic);
            Assert.IsTrue(map.ClientScene, "Sea of Tranquility ships the binary client scene");
            Assert.IsTrue(map.ClientOrientation, "Sea of Tranquility keeps DAVA handedness");
            CollectionAssert.AreEqual(
                new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, map.RandomTiers);

            // Landscape bbox (-300,-300,0)..(300,300,70), 512 samples.
            Assert.AreEqual(512, map.Terrain.Side);
            Assert.AreEqual(600f, map.Terrain.Extent, 0.001f);
            Assert.AreEqual(-300f, map.Terrain.Origin[0], 0.001f);
            Assert.AreEqual(-300f, map.Terrain.Origin[2], 0.001f);
            Assert.AreEqual(2.155489f, map.Terrain.MinHeight, 0.0001f);
            Assert.AreEqual(48.227207f, map.Terrain.MaxHeight, 0.0001f);

            // 11.20's tilemask without PBR: the baked light rides in the
            // colour map's alpha and the ground has no highlight.
            var client = map.Terrain.Client;
            Assert.IsNotNull(client);
            Assert.AreEqual(50f, client.Tiling[0], 0.001f);
            Assert.IsFalse(client.Specular);
            Assert.IsTrue(client.SeparateLightmapChannel);

            // MapBorderComponent rect (-249, -243.8)..(249.1, 240) in DAVA
            // x/y; the manifest's z is Godot's, -y.
            Assert.AreEqual(-249f, map.BorderMin[0], 0.01f);
            Assert.AreEqual(249.1f, map.BorderMax[0], 0.01f);
            Assert.AreEqual(-240f, map.BorderMin[2], 0.01f);
            Assert.AreEqual(243.8f, map.BorderMax[2], 0.01f);
            Assert.AreEqual(0, map.SkyFaces.Count);

            // MiniMapSmall@2x paints 505 x 505 of its 512 pixels; the HUD
            // draws that whole frame at the 4.8.0 minimaps' 326 x 326.
            CollectionAssert.AreEqual(new[] { 0, 0, 505, 505 }, map.MinimapArea.Frame);
            CollectionAssert.AreEqual(new[] { 326, 326 }, map.MinimapArea.Size);
            Assert.AreEqual(-300f, map.MinimapArea.Min[0], 0.001f);
            Assert.AreEqual(300f, map.MinimapArea.Max[1], 0.001f);
        }

        [Test]
        public void Catalog_OffersSeaOfTranquilityToEveryTier()
        {
            for (var tier = 1; tier <= 10; tier++)
            {
                Assert.IsTrue(
                    BattleMapCatalog.AvailableForTier(tier).Contains(MapId),
                    "tier " + tier);
            }
        }

        [Test]
        public void Name_IsTheClientsInBothLanguages()
        {
            var table = LocalizationTable.Parse(Resources.Load<TextAsset>("Data/lang").text);
            table.Language = "en";
            Assert.AreEqual("Sea of Tranquility", table.Get("map:moon/name"));
            table.Language = "ru";
            Assert.AreEqual("Море Спокойствия", table.Get("map:moon/name"));
        }

        /// <summary>
        /// Exponential fog (density 0.0005), the light node's ambient and
        /// colour, and no shadowColor: the map never renders shadow maps.
        /// </summary>
        [Test]
        public void Environment_CarriesExponentialFogAndNoShadows()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var env = map.Environment;

            Assert.IsNotNull(env);
            Assert.IsFalse(env.FogLinear);
            Assert.AreEqual(0.0005f, env.FogDensity, 0.000001f);
            Assert.IsNull(env.ShadowColor);
            Assert.AreEqual(1.018826f, env.Ambient[0], 0.0001f);
            Assert.AreEqual(1.124224f, env.Ambient[2], 0.0001f);
            Assert.AreEqual(0.610977f, env.SunDirection[0], 0.001f);
            Assert.AreEqual(-0.577815f, env.SunDirection[1], 0.001f);
            Assert.AreEqual(-0.541144f, env.SunDirection[2], 0.001f);

            Assert.IsFalse(map.Light.Shadows);
            Assert.AreEqual(0.5f, map.Light.Color[0], 0.0001f);
            Assert.AreEqual(0.8f, map.Light.Color[2], 0.0001f);
        }

        [Test]
        public void Spawns_HoldOppositeCornersOnTheGround()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            // Seven TankSpawner markers per pointNumber, one respawn group.
            Assert.AreEqual(7, map.TeamA.Count);
            Assert.AreEqual(7, map.TeamB.Count);
            var allies = TeamCentre(map.TeamA);
            var enemies = TeamCentre(map.TeamB);
            Assert.Greater(Vector3.Distance(allies, enemies), 450f);
            Assert.Less(allies.x, -150f);
            Assert.Greater(allies.z, 200f);
            Assert.Greater(enemies.x, 150f);
            Assert.Less(enemies.z, -150f);

            var field = BattleWorld.LoadHeights(map);
            AssertOnTheGround(map, field, map.TeamA);
            AssertOnTheGround(map, field, map.TeamB);
        }

        [Test]
        public void AiMarkers_StandOnTheLandscape()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var field = BattleWorld.LoadHeights(map);

            // The control point and the fourteen PerfBot botspawns.
            Assert.AreEqual(15, map.AiMarkers.Count);
            Assert.AreEqual(1, map.AiMarkers.FindAll(m => m.Priority > 0).Count);
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
                ClientMapScene.LandscapeShader,
                ClientMapScene.LitShader,
                ClientMapScene.VegetationShader,
            };
            for (var i = 0; i < names.Length; i++)
            {
                var shader = Shader.Find(names[i]);
                Assert.IsNotNull(shader, names[i]);
                Assert.IsFalse(ShaderUtil.ShaderHasError(shader), names[i]);
            }
        }

        [Test]
        public void SceneTextures_AreLinear()
        {
            var guids = AssetDatabase.FindAssets(
                "t:Texture", new[] { "Assets/Blitz/Resources/Maps/Moon/Textures" });
            Assert.AreEqual(23, guids.Length);
            for (var i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                Assert.IsFalse(importer.sRGBTexture, path);
                Assert.IsTrue(importer.mipmapEnabled, path);
            }
        }

        [Test]
        public void ClientScene_BuildsNodesMaterialsAndCollision()
        {
            _root = new GameObject("moon-scene-test");
            var scene = ClientMapScene.Build(_root.transform, SceneResource);

            Assert.AreEqual(172, scene.Layout.nodes.Length);
            Assert.AreEqual(172, scene.NodeCount);
            Assert.AreEqual(80865, scene.TriangleCount);

            var hidden = 0;
            var passable = 0;
            for (var i = 0; i < scene.Nodes.Count; i++)
            {
                var node = scene.Nodes[i];
                var renderer = node.GetComponent<MeshRenderer>();
                if (!renderer.enabled)
                {
                    // Five invisible walls without the VISIBLE flag hold the
                    // north-west corner.
                    hidden++;
                    StringAssert.Contains("invisiblewall", node.name);
                    Assert.IsTrue(node.HasSolid, node.name);
                }
                if (node.Info.noCollision)
                {
                    // No CollisionTypeComponent: the dome, the crater ring,
                    // bunkers far outside, the UFO, the duck, the border.
                    passable++;
                    Assert.IsFalse(node.HasSolid, node.name);
                    Assert.IsNull(node.CollisionMesh, node.name);
                }
            }
            Assert.AreEqual(5, hidden);
            Assert.AreEqual(10, passable);

            var materials = MaterialsByName(scene);
            var sky = materials["0000_Skyobject"];
            Assert.AreEqual(ClientMapScene.SkyShader, sky.shader.name);
            Assert.AreEqual(2499, sky.renderQueue);

            // Decal reads its decal texture through the lightmap path.
            var decal = materials["0139_Decal"];
            Assert.IsTrue(decal.IsKeywordEnabled("_CLIENT_LIGHTMAP"));
            Assert.AreEqual("006_cratersTile", decal.GetTexture("_Lightmap").name);

            var lit = materials["0182_BlinnPhongAllQualities"];
            Assert.AreEqual(ClientMapScene.LitShader, lit.shader.name);
            Assert.AreEqual(new Vector4(3f, 2f, 0.5f, 0f), lit.GetVector("_Lit"));

            // The UFO's glow adds itself, scrolls at 7 per second and fogs
            // towards its own fogColor (2, 2, 2).
            var glow = materials["0184_Textured"];
            Assert.AreEqual((float)BlendMode.SrcAlpha, glow.GetFloat("_SrcBlend"));
            Assert.AreEqual((float)BlendMode.One, glow.GetFloat("_DstBlend"));
            Assert.AreEqual(7f, glow.GetVector("_TexShift").z, 0.0001f);
            Assert.AreEqual(1f, glow.GetVector("_Options2").y);
            Assert.AreEqual(2f, glow.GetVector("_FogColor").x, 0.0001f);

            // The red ribbon of MapBorderComponent: vertex colour, blended.
            var border = materials["0190_MapBorder"];
            Assert.AreEqual(1f, border.GetVector("_Options2").x);
            Assert.AreEqual(0f, border.GetVector("_Options2").y);
            Assert.AreEqual((float)BlendMode.OneMinusSrcAlpha, border.GetFloat("_DstBlend"));
            Assert.AreEqual(3000, border.renderQueue);
        }

        /// <summary>
        /// The UFO waits 120 + rand(60) s after the scene loads, plays its
        /// 33.3 s clip once at rate 2 and holds the last pose; the duck loops
        /// its 133.9 s clip from the start. Joints the clip drives start from
        /// identity, the others keep their bind pose.
        /// </summary>
        [Test]
        public void Skeletons_PlayTheirClipsAsTheActionsTimeThem()
        {
            _root = new GameObject("moon-skeleton-test");
            var scene = ClientMapScene.Build(_root.transform, SceneResource);
            Assert.AreEqual(2, scene.Skeletons.Count);

            var ufo = scene.Skeletons.Find(s => s.Info.name == Ufo);
            Assert.IsNotNull(ufo);
            Assert.AreEqual(2, ufo.Joints.Length);
            Assert.AreSame(ufo.Joints[0], Node(scene, Ufo + "_UFO_anim_02_Root").transform.parent);
            Assert.AreSame(ufo.Joints[1], Node(scene, Ufo + "_UFO_anim_02_Gun").transform.parent);
            Assert.AreEqual(ClientMapSkeleton.PlayState.Waiting, ufo.State);
            Assert.That(ufo.Wait, Is.InRange(120f, 180f));
            Assert.AreEqual(5f, ufo.Joints[0].localScale.x, 0.0001f);

            ufo.Restart(0f);
            Assert.AreEqual(120f, ufo.Wait, 0.0001f);
            ufo.Advance(119.9f);
            Assert.AreEqual(ClientMapSkeleton.PlayState.Waiting, ufo.State);
            ufo.Advance(0.2f);
            Assert.AreEqual(ClientMapSkeleton.PlayState.Playing, ufo.State);
            Assert.AreEqual(Vector3.zero, ufo.Joints[0].localPosition);
            ufo.Advance(8f);
            Assert.AreEqual(16f, ufo.Time, 0.001f, "the clip runs at twice the clock");
            Assert.Greater(ufo.Joints[0].localPosition.magnitude, 10f, "the UFO did not move");
            ufo.Advance(9f);
            Assert.AreEqual(ClientMapSkeleton.PlayState.Stopped, ufo.State);
            var last = ufo.Joints[0].localPosition;
            Assert.AreEqual(378.33f, last.x, 0.01f);
            Assert.AreEqual(-2.574f, last.y, 0.01f);
            Assert.AreEqual(346.713f, last.z, 0.01f);
            ufo.Advance(30f);
            Assert.AreEqual(last, ufo.Joints[0].localPosition, "the UFO left its last pose");

            var duck = scene.Skeletons.Find(s => s.Info.name == Duck);
            Assert.IsNotNull(duck);
            Assert.AreEqual(6, duck.Joints.Length);
            Assert.AreEqual(-285.31058f, duck.transform.localPosition.x, 0.001f);
            duck.Restart(0f);
            duck.Advance(0.02f);
            Assert.AreEqual(ClientMapSkeleton.PlayState.Playing, duck.State);
            var start = duck.Joints[2].localRotation;
            duck.Advance(30f);
            Assert.Greater(Quaternion.Angle(start, duck.Joints[2].localRotation), 1f, "the duck does not turn");
            Assert.AreEqual(1f, Norm(duck.Joints[2].localRotation), 0.0001f);
            duck.Advance(200f);
            Assert.AreEqual(ClientMapSkeleton.PlayState.Playing, duck.State, "the duck stopped looping");
            Assert.Less(duck.Time, 133.934f);
            // Dummy006 has no track: it keeps its bind offset.
            Assert.AreEqual(35.871147f, duck.Joints[3].localPosition.z, 0.0001f);
        }

        [Test]
        public void Clips_RejectAForeignPayload()
        {
            Assert.Throws<System.InvalidOperationException>(
                () => ClientMapSkeleton.ParseClips(System.Text.Encoding.ASCII.GetBytes("BLTZMAP1\0\0\0\0")));
        }

        /// <summary>
        /// The stones of the VegetationRenderObject: 304 / 76 / 19 clusters
        /// per pattern resolution, single cells near the camera point, halves
        /// past 9.4 m, whole patterns past 28.1 m, nothing past 50 m.
        /// </summary>
        [Test]
        public void Vegetation_DrawsTheClientsResolutionsAroundTheCamera()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var field = BattleWorld.LoadHeights(map);
            _root = new GameObject("moon-vegetation-test");
            var scene = ClientMapScene.Build(_root.transform, SceneResource);
            var info = scene.Layout.vegetation;
            Assert.IsTrue(ClientMapVegetation.Present(info));
            var vegetation = ClientMapVegetation.Build(scene.transform, info, field, map.Terrain.Origin);

            Assert.AreEqual(3, vegetation.Meshes.Length);
            Assert.AreEqual(9088, vegetation.Meshes[0].vertexCount);
            Assert.AreEqual(16, vegetation.Meshes[0].subMeshCount);
            Assert.AreEqual(4, vegetation.Meshes[1].subMeshCount);
            Assert.AreEqual(1, vegetation.Meshes[2].subMeshCount);
            Assert.AreEqual(9088 + 2272 + 568, vegetation.VertexCount);
            Assert.AreEqual(ClientMapScene.VegetationShader, vegetation.Material.shader.name);
            Assert.AreEqual(new Vector2(-300f, -300f), vegetation.HeightOrigin);
            Assert.AreEqual(600f / 511f, vegetation.HeightStep.x, 0.0001f);
            Assert.AreEqual(512, vegetation.HeightTexture.width);
            Assert.Greater(vegetation.FilledCells, 15000);

            var spawn = map.TeamA[0].Origin;
            var camera = new Vector3(spawn[0], spawn[1] + 8f, -spawn[2]);
            var draws = vegetation.Collect(camera, Vector3.forward);
            Assert.Greater(draws.Count, 10);
            var resolutions = new HashSet<int>();
            var cells = new HashSet<Vector2Int>();
            var fading = 0;
            for (var i = 0; i < draws.Count; i++)
            {
                var draw = draws[i];
                resolutions.Add(draw.Resolution);
                Assert.LessOrEqual(draw.Distance, 2500f);
                Assert.That(draw.LodMorph, Is.InRange(0f, 1f));
                if (draw.DistanceScale < 1f) fading++;

                // The draws tile the ground without overlapping.
                var width = 1 << draw.Resolution;
                var perRow = ClientMapVegetation.NodeCells / width;
                var cornerX = Mathf.RoundToInt((draw.Origin.x + 300f) / info.unit);
                var cornerZ = Mathf.RoundToInt((draw.Origin.y + 300f) / info.unit);
                var x0 = cornerX + draw.Submesh % perRow * width;
                var z0 = cornerZ + draw.Submesh / perRow * width;
                for (var z = 0; z < width; z++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        Assert.IsTrue(cells.Add(new Vector2Int(x0 + x, z0 + z)), "cell drawn twice");
                    }
                }
            }
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, resolutions);
            Assert.Greater(fading, 0, "no cluster fades out towards 50 m");

            // Off the landscape's far side nothing is near enough.
            Assert.AreEqual(0, vegetation.Collect(new Vector3(1000f, 0f, 1000f), Vector3.forward).Count);
        }

        [Test]
        public void World_DrawsTheMapWithoutShadowsAndWithItsVegetation()
        {
            _root = new GameObject("moon-world-test");
            var world = BattleWorld.Create(_root.transform, MapId);

            Assert.IsNotNull(world.Sun.GetComponent<BakedSun>());
            Assert.AreEqual(LightShadows.None, world.Sun.shadows);
            Assert.IsNotNull(world.Vegetation);
            Assert.AreEqual(162, world.Objects.ColliderCount);

            var landscape = world.Landscape.GetComponent<MeshRenderer>().sharedMaterial;
            Assert.AreEqual(ClientMapScene.LandscapeShader, landscape.shader.name);
            Assert.AreEqual(new Vector4(1f, 0f, 0f, 0f), landscape.GetVector("_LandscapeOptions"));

            var light = Shader.GetGlobalVector(BattleWorld.ClientLightColorId);
            Assert.AreEqual(0.6f, light.y, 0.0001f);
            var ambient = Shader.GetGlobalVector(BattleWorld.ClientAmbientColorId);
            Assert.AreEqual(1.055708f, ambient.y, 0.0001f);
        }

        [Test]
        public void LoadingScreen_PlaysTheMapsOwnMusic()
        {
            Assert.AreEqual("map_intro_moon",
                BattleLoadingScreen.IntroMusic(BattleWorld.LoadDefinition(MapId)));
            Assert.IsNotNull(BlitzResources.LoadAudio("map_intro_moon"));
            Assert.AreEqual(BattleLoadingScreen.DefaultIntroMusic,
                BattleLoadingScreen.IntroMusic(BattleWorld.LoadDefinition("asia")));
            Assert.AreEqual(BattleLoadingScreen.DefaultIntroMusic, BattleLoadingScreen.IntroMusic(null));
        }

        [Test]
        public void Navmesh_ReachesEveryGoalFromBothSpawns()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            _root = new GameObject("moon-navigation-test");
            var navigation = BattleNavigation.Create(_root.transform, MapId);
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

        private static Dictionary<string, Material> MaterialsByName(ClientMapScene scene)
        {
            var result = new Dictionary<string, Material>();
            for (var i = 0; i < scene.Nodes.Count; i++)
            {
                var shared = scene.Nodes[i].GetComponent<MeshRenderer>().sharedMaterials;
                for (var m = 0; m < shared.Length; m++)
                {
                    result[shared[m].name] = shared[m];
                }
            }
            return result;
        }

        private static ClientMapNode Node(ClientMapScene scene, string name)
        {
            var node = scene.Nodes.Find(n => n.Info.name == name);
            Assert.IsNotNull(node, name);
            return node;
        }

        private static float Norm(Quaternion q)
        {
            return Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
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
