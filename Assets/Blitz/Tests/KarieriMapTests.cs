using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Karieri ("23_karieri_kr", "Copperfield" in English), ported "1 in 1"
    /// out of the 4.8.0 client as a binary client scene, the way Mirage is:
    /// every batch keeps its DAVA material, lightmap and vertex streams, and
    /// the map stands in the client's own handedness. It authors one spawn
    /// corner per team and no PathComponents, so these assertions also pin
    /// the places the porter behaves differently from Ordeal and Mirage.
    /// </summary>
    public sealed class KarieriMapTests
    {
        private const string MapId = "karieri";
        private const string SceneResource = "Maps/Karieri/karieri_scene";

        [Test]
        public void Manifest_MatchesTheClientScene()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            Assert.AreEqual("karieri", map.Id);
            Assert.AreEqual("map:karieri/name", map.NameKey);
            Assert.AreEqual("3d/Maps/23_karieri_kr/23_karieri_kr.sc2", map.Scene);
            Assert.AreEqual(SceneResource, map.ExternalScene);
            // The OBJ port drew the houses without the lightmaps 352 of the
            // 450 LOD-0 batches are authored with, which is why they were grey.
            Assert.IsTrue(map.ClientScene, "karieri has to ship the binary client scene");
            Assert.IsTrue(map.ClientOrientation, "karieri keeps the client's handedness");
            Assert.IsFalse(map.GodotScene);
            Assert.AreEqual("Maps/Karieri/minimap", map.Minimap);
            Assert.AreEqual("Maps/Karieri/loading_screen", map.LoadingScreen);

            // `from_apk/Data/maps.yaml` authors `levels: [2, 3, 4, 5, 6, 7]`,
            // but the port unlocks the quarry for every tier: Rudniki was the
            // only candidate a tier 8-10 random battle had, so "random" chose
            // the same map every single time.
            CollectionAssert.IsEmpty(map.RandomTiers);
            Assert.IsTrue(BattleMapCatalog.IsRandomAvailable(map, 1));
            Assert.IsTrue(BattleMapCatalog.IsRandomAvailable(map, 10));
            CollectionAssert.Contains(BattleMapCatalog.AvailableForTier(10), "karieri");

            // Landscape bbox (-300,-300,0)..(300,300,120): 600 m across,
            // 511 quads, corner at -300 on both horizontal axes.
            Assert.AreEqual(512, map.Terrain.Side);
            Assert.AreEqual(600f, map.Terrain.Extent, 0.001f);
            Assert.AreEqual(600f / 511f, map.Terrain.Step, 0.0001f);
            Assert.AreEqual(-300f, map.Terrain.Origin[0], 0.001f);
            Assert.AreEqual(0f, map.Terrain.Origin[1], 0.001f);
            Assert.AreEqual(-300f, map.Terrain.Origin[2], 0.001f);
            Assert.AreEqual(16.780041f, map.Terrain.MinHeight, 0.0001f);
            Assert.AreEqual(84.733345f, map.Terrain.MaxHeight, 0.0001f);
            Assert.IsTrue(map.Terrain.CollisionEnabled);

            Assert.AreEqual("Maps/Karieri/colormap", map.Terrain.ColorMapResource);
            Assert.AreEqual("Maps/Karieri/tilemask", map.Terrain.TileMaskResource);
            Assert.AreEqual("Maps/Karieri/tiletextures", map.Terrain.TileTextureResource);

            // The play area is the authored client boundary
            // (borderTop/BotX/Y of the landscape), not the landscape bbox: the
            // red no-entry fence stands well inside the terrain, and the hull
            // clamp has to sit on the fence.
            // The manifest stores -y in this orientation: DAVA y -223.4..186.9.
            Assert.AreEqual(-230.6f, map.BorderMin[0], 0.01f);
            Assert.AreEqual(-223.4f, map.BorderMin[2], 0.01f);
            Assert.AreEqual(249.5f, map.BorderMax[0], 0.01f);
            Assert.AreEqual(186.9f, map.BorderMax[2], 0.01f);
            Assert.AreEqual(480.1f, map.SizeX, 0.01f);
            Assert.AreEqual(410.3f, map.SizeZ, 0.01f);

            // SkyOldDeepHarizon is an ordinary entity inside the .sc2, so the
            // port must not add the substitute sky planes the Godot maps need.
            Assert.AreEqual(0, map.SkyFaces.Count);
        }

        /// <summary>
        /// Ordeal authors alternate corners per team and the porter has to
        /// pick the widest separated pair. Karieri authors exactly one corner
        /// each - team one east, team two west - so every marker of a team
        /// belongs to the same battle and none of them may be dropped.
        /// </summary>
        [Test]
        public void Spawns_KeepTheSingleAuthoredCornerPerTeam()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            Assert.AreEqual(10, map.TeamA.Count);
            Assert.AreEqual(10, map.TeamB.Count);
            Assert.AreEqual(0, map.TeamA[0].Team);
            Assert.AreEqual(1, map.TeamB[0].Team);

            var allies = TeamCentre(map.TeamA);
            var enemies = TeamCentre(map.TeamB);
            Assert.Greater(allies.x, 100f, "team one starts on the east side");
            Assert.Less(enemies.x, -100f, "team two starts on the west side");

            // One corner each means one tight cluster each, not the merged
            // pair of corners Ordeal ends up with.
            AssertClustered(map.TeamA, allies);
            AssertClustered(map.TeamB, enemies);
        }

        [Test]
        public void SpawnMarkers_StandOnTheLandscape()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var field = BattleWorld.LoadHeights(map);

            AssertOnTheGround(map, field, map.TeamA);
            AssertOnTheGround(map, field, map.TeamB);
        }

        /// <summary>
        /// This scene ships no PathComponents, so the porter falls back on the
        /// positions the client itself sends a bot to: the `controlpoint`, the
        /// `strategicpoint` bases and the fourteen `botspawn` entities. With an
        /// empty list every bot would stand still in its spawn.
        /// </summary>
        [Test]
        public void AiMarkers_ComeFromTheAuthoredBotAndBaseMarkers()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            Assert.Greater(map.AiMarkers.Count, 8);

            // The control point and the bases outrank an ordinary bot post,
            // which is what a non-zero priority means to the tactical scoring.
            var objectives = map.AiMarkers.FindAll(marker =>
                marker.Priority > 0 && !marker.Name.StartsWith("route_"));
            Assert.AreEqual(3, objectives.Count);
            Assert.IsNotNull(
                map.AiMarkers.Find(marker => marker.Name.StartsWith("botspawn_")),
                "the authored bot posts should survive into the manifest");

            var field = BattleWorld.LoadHeights(map);
            for (var i = 0; i < map.AiMarkers.Count; i++)
            {
                var marker = map.AiMarkers[i];
                Assert.Greater(marker.Radius, 0f);

                var ground = field.SampleHeight(
                    marker.Origin[0] - map.Terrain.Origin[0],
                    marker.Origin[2] - map.Terrain.Origin[2]);
                Assert.AreEqual(ground, marker.Origin[1], 2.5f,
                    marker.Name + " is not standing on the landscape");
            }
        }

        /// <summary>
        /// The client's own markers all sit in a narrow band around the middle
        /// of the quarry, so bots kept milling about their spawn instead of
        /// taking ground. The port adds a route set on top: the centre and two
        /// hills on opposite sides, with approaches to walk there from either
        /// corner.
        /// </summary>
        [Test]
        public void AiMarkers_AuthorTheCentreAndBothHills()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var routes = map.AiMarkers.FindAll(
                marker => marker.Name.StartsWith("route_"));

            Assert.AreEqual(13, routes.Count);

            var centre = routes.FindAll(m => m.Name.StartsWith("route_center"));
            var north = routes.FindAll(m => m.Name.StartsWith("route_hill_north"));
            var south = routes.FindAll(m => m.Name.StartsWith("route_hill_south"));
            Assert.AreEqual(3, centre.Count);
            Assert.AreEqual(3, north.Count);
            Assert.AreEqual(3, south.Count);

            // The two hills have to be on opposite sides of the centre, or the
            // bots of both teams pile onto the same slope.
            for (var i = 0; i < north.Count; i++)
            {
                Assert.Less(north[i].Origin[2], -80f, north[i].Name);
            }

            for (var i = 0; i < south.Count; i++)
            {
                Assert.Greater(south[i].Origin[2], 80f, south[i].Name);
            }

            // A hill is worth holding and worth shooting from, so each one
            // carries a held post plus sniper spots - the mix the tactical
            // scoring needs to spread a team out.
            Assert.IsNotNull(north.Find(
                m => m.Type == MapAiMarkerType.PrioritySecondary));
            Assert.IsNotNull(south.Find(
                m => m.Type == MapAiMarkerType.PrioritySecondary));
            Assert.AreEqual(2, north.FindAll(
                m => m.Type == MapAiMarkerType.Snipe).Count);
            Assert.AreEqual(2, south.FindAll(
                m => m.Type == MapAiMarkerType.Snipe).Count);

            // Both spawn corners need a marker on their own half, otherwise the
            // first goal a bot picks is already across the quarry.
            var approaches = routes.FindAll(
                m => m.Name.StartsWith("route_approach"));
            Assert.AreEqual(4, approaches.Count);
            Assert.IsNotNull(approaches.Find(m => m.Origin[0] > 100f));
            Assert.IsNotNull(approaches.Find(m => m.Origin[0] < -100f));
        }

        /// <summary>
        /// The scene's own Scene_Global_Material authors the fog, and
        /// `Dynamic_light` - not Ordeal's `Light dynamic` - the ambient colour
        /// and the sun, which shines along the node's -Y like every DAVA light.
        /// </summary>
        [Test]
        public void Environment_CarriesTheClientFogAndAmbient()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            Assert.IsNotNull(map.Environment);
            Assert.IsTrue(map.Environment.FogLinear);
            Assert.AreEqual(-40f, map.Environment.FogStart, 0.01f);
            Assert.AreEqual(700f, map.Environment.FogEnd, 0.01f);
            Assert.AreEqual(0.7f, map.Environment.FogLimit, 0.001f);
            Assert.IsTrue(map.Environment.FogAtmosphere);
            Assert.AreEqual(7f, map.Environment.FogScattering, 0.001f);
            Assert.AreEqual(100f, map.Environment.FogAtmosphereDistance, 0.001f);

            // Dynamic_light's -Y: 29 degrees down towards DAVA (+x, -y), which
            // is Unity (+x, -z), where the lightmaps throw the shadows.
            var sun = map.Environment.SunDirection;
            Assert.AreEqual(0.501814f, sun[0], 0.0001f);
            Assert.AreEqual(-0.490697f, sun[1], 0.0001f);
            Assert.AreEqual(-0.712319f, sun[2], 0.0001f);

            // shadowColor of the global material, read by ShadowRect.
            Assert.IsNotNull(map.Environment.ShadowColor);
            Assert.AreEqual(0.729168f, map.Environment.ShadowColor[0], 0.0001f);
        }

        /// <summary>
        /// FOG_HALFSPACE is on in this scene (exponential: no
        /// FOG_HALFSPACE_LINEAR), so the quarry floor holds up to a fifth more
        /// haze than the distance curve alone - Mirage switches it off.
        /// </summary>
        [Test]
        public void Environment_CarriesTheHalfSpaceFog()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var halfspace = map.Environment.FogHalfspace;

            Assert.IsNotNull(halfspace);
            Assert.AreEqual(0.1f, halfspace.Density, 0.0001f);
            Assert.AreEqual(0.01f, halfspace.Falloff, 0.0001f);
            Assert.AreEqual(50f, halfspace.Height, 0.0001f);
            Assert.AreEqual(0.2f, halfspace.Limit, 0.0001f);
            Assert.IsFalse(halfspace.Linear);

            Assert.IsNull(BattleWorld.LoadDefinition("lake").Environment.FogHalfspace);
        }

        /// <summary>
        /// The client draws the map with one sun and the ambient of its light
        /// node; the exposure trim of the OBJ port is gone with its shader.
        /// </summary>
        [Test]
        public void Environment_UsesTheSharedClientExposure()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            Assert.AreEqual(1.25f, map.Light.Energy, 0.0001f);
            Assert.AreEqual(0f, map.Environment.AmbientLuminance, 0.0001f);
        }

        [Test]
        public void Heightmap_FillsTheLandscapeBoundingBox()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var field = BattleWorld.LoadHeights(map);

            Assert.AreEqual(512, field.Side);
            Assert.AreEqual(600f, field.ExtentX, 0.01f);
            Assert.AreEqual(600f, field.ExtentZ, 0.01f);

            var min = float.MaxValue;
            var max = float.MinValue;
            for (var z = 0; z < field.Side; z++)
            {
                for (var x = 0; x < field.Side; x++)
                {
                    var height = field.HeightAt(x, z);
                    min = Mathf.Min(min, height);
                    max = Mathf.Max(max, height);
                }
            }

            // Heights are bbox.minZ + (bbox.maxZ - bbox.minZ) * raw / 65535,
            // so they stay inside the authored 0..120 box.
            Assert.AreEqual(map.Terrain.MinHeight, min, 0.0001f);
            Assert.AreEqual(map.Terrain.MaxHeight, max, 0.0001f);
            Assert.Greater(min, 0f);
            Assert.Less(max, 120f);
        }

        /// <summary>
        /// Karieri parks its dome off to one side of the map instead of around
        /// the origin. Scaling it out to the horizon multiplies that offset
        /// too, so without re-centring the sky ends up kilometres past the
        /// border and the horizon opens up on one side of the battlefield.
        /// </summary>
        [Test]
        public void SkyDome_IsRecentredWhenTheClientParksItOffOrigin()
        {
            Assert.IsTrue(BattleObjects.IsSkyName("entity_182_SkyOldDeepHarizon_sc2"));

            var holder = new GameObject("sky");
            var mesh = new Mesh();
            try
            {
                // The authored bounds of SkyOldDeepHarizon: a ~19.5 m dome
                // centred near (44, 11) rather than on the map centre.
                mesh.vertices = new[]
                {
                    new Vector3(34.56f, -7.57f, 1.18f),
                    new Vector3(54.09f, -7.57f, 1.18f),
                    new Vector3(54.09f, 7.82f, 20.72f),
                    new Vector3(34.56f, 7.82f, 20.72f),
                };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                mesh.RecalculateBounds();

                BattleObjects.PushSkyToTheHorizon(holder.transform, mesh);

                var scale = holder.transform.localScale.x;
                var extents = mesh.bounds.extents;
                var reach = Mathf.Max(extents.x, extents.z) * scale;
                Assert.AreEqual(BattleObjects.SkyRadius, reach, 0.01f);

                var centre = holder.transform.localPosition +
                    Vector3.Scale(mesh.bounds.center, holder.transform.localScale);
                Assert.AreEqual(0f, centre.x, 0.01f, "the dome has to cover the map");
                Assert.AreEqual(0f, centre.z, 0.01f, "the dome has to cover the map");

                var map = BattleWorld.LoadDefinition(MapId);
                Assert.Greater(reach, map.SizeX);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(holder);
            }
        }

        /// <summary>
        /// A dome already modelled around the origin, the way Ordeal authors
        /// it, must not be moved by the re-centring.
        /// </summary>
        [Test]
        public void SkyDome_LeavesACentredDomeWhereItIs()
        {
            var holder = new GameObject("sky");
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    new Vector3(-9.93f, -0.83f, -9.93f),
                    new Vector3(9.93f, -0.83f, -9.93f),
                    new Vector3(9.93f, 5.33f, 9.93f),
                    new Vector3(-9.93f, 5.33f, 9.93f),
                };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                mesh.RecalculateBounds();

                BattleObjects.PushSkyToTheHorizon(holder.transform, mesh);

                Assert.AreEqual(0f, holder.transform.localPosition.x, 0.01f);
                Assert.AreEqual(0f, holder.transform.localPosition.y, 0.01f);
                Assert.AreEqual(0f, holder.transform.localPosition.z, 0.01f);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(holder);
            }
        }

        /// <summary>
        /// The quarry is unlocked for every tier. Rudniki is the only other map
        /// in the roster and the only one a tier 8-10 battle used to be allowed
        /// to pick, so a high tier "random" battle was not random at all.
        /// </summary>
        [Test]
        public void Catalog_OffersKarieriToEveryTier()
        {
            for (var tier = 1; tier <= 10; tier++)
            {
                CollectionAssert.Contains(
                    BattleMapCatalog.AvailableForTier(tier), MapId,
                    "tier " + tier + " should be able to roll Karieri");
            }

            // And a tier the roster has no dedicated map for still has a real
            // choice to make rather than one candidate.
            Assert.Greater(BattleMapCatalog.AvailableForTier(10).Count, 1);
        }

        /// <summary>
        /// The 21 bushes are DAVA SpeedTreeObjects: 2D card corners around a
        /// pivot. The OBJ port froze them into fixed fans of cards (the
        /// "buggy bushes"); the client scene keeps the pivot and the card
        /// offset, and SpeedTreeLeaf spans every card across the view plane.
        /// </summary>
        [Test]
        public void Bushes_AreClientSpeedTreeBillboards()
        {
            var layout = LoadLayout();
            var bushes = 0;
            for (var i = 0; i < layout.nodes.Length; i++)
            {
                var node = layout.nodes[i];
                if (!node.name.Contains("_bush_sc2"))
                {
                    continue;
                }

                bushes++;
                Assert.AreNotEqual(0, node.attributes & 16, node.name + " lost its card offsets");
                for (var m = 0; m < node.materials.Length; m++)
                {
                    var material = layout.materials[node.materials[m]];
                    Assert.IsTrue(material.speedTree, node.name);
                    Assert.IsTrue(material.alphaTest, node.name);
                    Assert.IsFalse(material.cull, node.name + " must draw both faces");
                }
            }

            Assert.AreEqual(21, bushes);
        }

        /// <summary>
        /// The houses, sheds and props are TextureLightmap.Opaque: albedo times
        /// twice the baked lightmap. Every one of those batches must carry the
        /// lightmap and its own uvScale/uvOffset rectangle in the atlas.
        /// </summary>
        [Test]
        public void Buildings_KeepTheirBakedLightmaps()
        {
            var layout = LoadLayout();
            var lightmapped = 0;
            for (var i = 0; i < layout.materials.Length; i++)
            {
                var material = layout.materials[i];
                if (!material.effect.StartsWith("TextureLightmap"))
                {
                    continue;
                }

                lightmapped++;
                Assert.IsFalse(string.IsNullOrEmpty(material.lightmap), material.name);
                Assert.IsNotNull(Resources.Load<Texture2D>(material.lightmap), material.lightmap);
                Assert.Greater(material.lightmapST[0], 0f, material.name);
                Assert.Greater(material.lightmapST[1], 0f, material.name);
            }

            Assert.Greater(lightmapped, 400);

            var houses = 0;
            for (var i = 0; i < layout.nodes.Length; i++)
            {
                var node = layout.nodes[i];
                if (!node.name.Contains("_bld_kr_house"))
                {
                    continue;
                }

                houses++;
                Assert.AreNotEqual(0, node.attributes & 2, node.name + " has no lightmap UV");
                for (var m = 0; m < node.materials.Length; m++)
                {
                    StringAssert.StartsWith(
                        "TextureLightmap", layout.materials[node.materials[m]].effect, node.name);
                }
            }

            Assert.Greater(houses, 10);
        }

        [Test]
        public void SceneTextures_AreLinear()
        {
            var guids = AssetDatabase.FindAssets(
                "t:Texture", new[] { "Assets/Blitz/Resources/Maps/Karieri/Textures" });
            Assert.AreEqual(37, guids.Length);
            for (var i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                Assert.IsFalse(importer.sRGBTexture, path);
                Assert.IsTrue(importer.mipmapEnabled, path);
            }

            // The old OBJ scene is gone for good.
            Assert.IsNull(Resources.Load<GameObject>(SceneResource));
        }

        [Test]
        public void ClientScene_BuildsEveryNodeWithItsMaterials()
        {
            var parent = new GameObject("karieri-scene-test");
            try
            {
                var scene = ClientMapScene.Build(parent.transform, SceneResource);

                Assert.AreEqual(495, scene.Layout.nodes.Length);
                Assert.AreEqual(495, scene.NodeCount);
                Assert.AreEqual(182375, scene.TriangleCount);

                var kinds = new HashSet<string>();
                for (var i = 0; i < scene.Layout.materials.Length; i++)
                {
                    kinds.Add(scene.Layout.materials[i].kind);
                }

                CollectionAssert.IsSupersetOf(kinds, new[]
                {
                    ClientMapScene.SurfaceKind,
                    ClientMapScene.SkyKind,
                });

                var nodes = scene.GetComponentsInChildren<ClientMapNode>(true);
                Assert.AreEqual(495, nodes.Length);
                for (var i = 0; i < nodes.Length; i++)
                {
                    var renderer = nodes[i].GetComponent<MeshRenderer>();
                    Assert.IsNotNull(renderer, nodes[i].name);
                    for (var m = 0; m < renderer.sharedMaterials.Length; m++)
                    {
                        Assert.IsNotNull(renderer.sharedMaterials[m], nodes[i].name);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        private static ClientMapLayout LoadLayout()
        {
            var asset = Resources.Load<TextAsset>(SceneResource + "_layout");
            Assert.IsNotNull(asset, "the karieri scene layout is not in Resources");
            return JsonUtility.FromJson<ClientMapLayout>(asset.text);
        }

        internal static void AssertFoliageIsUpright(
            string resource, string keyword, int expected)
        {
            var prefab = Resources.Load<GameObject>(resource);
            Assert.IsNotNull(prefab, "missing external map scene: " + resource);

            var found = 0;
            var filters = prefab.GetComponentsInChildren<MeshFilter>(true);
            for (var i = 0; i < filters.Length; i++)
            {
                var filter = filters[i];
                if (filter.name.IndexOf(keyword) < 0 || filter.sharedMesh == null)
                {
                    continue;
                }

                found++;
                var size = filter.sharedMesh.bounds.size;
                Assert.Greater(size.y, 1f,
                    filter.name + " is a flat card lying on the ground again");
            }

            Assert.GreaterOrEqual(found, expected,
                "the " + keyword + " groups should survive the export");
        }

        [Test]
        public void ClientSceneTower_KeepsIts4_8WorldTransformAndHeight()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var parent = new GameObject("karieri geometry test");
            BattleObjects objects = null;
            try
            {
                objects = BattleObjects.BuildExternal(
                    parent.transform, map.ExternalScene, map.ExternalDestructibles);
                var towers = new List<MeshRenderer>();
                var renderers = objects.Root.GetComponentsInChildren<MeshRenderer>(true);
                for (var i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i].gameObject.name.Contains("bld_kr_tower_sc2"))
                    {
                        towers.Add(renderers[i]);
                    }
                }

                Assert.AreEqual(4, towers.Count);
                var tower = towers.Find(
                    renderer => renderer.gameObject.name.Contains("entity_1_"));
                Assert.IsNotNull(tower);
                Assert.AreEqual(26.8705f, tower.bounds.size.y, 0.05f,
                    "the tower must use the client-authored vertical axis/scale");
                Assert.AreEqual(17.685f, tower.bounds.min.y, 0.05f,
                    "the tower foot must stay on the authored terrain elevation");
                Assert.AreEqual(44.5555f, tower.bounds.max.y, 0.05f);
                // DAVA (31.42, 2.92) is Unity (+31.42, +2.92): the client's own
                // layout, the same axes BattleTerrain builds the landscape in.
                // Signed on purpose - a mirrored map passes an Abs() check.
                Assert.AreEqual(31.4203f, tower.bounds.center.x, 0.05f,
                    "the tower must not be mirrored across the map");
                Assert.AreEqual(2.9185f, tower.bounds.center.z, 0.05f,
                    "the tower must keep its authored horizontal offset");
            }
            finally
            {
                if (objects != null) objects.Release();
                Object.DestroyImmediate(parent);
            }
        }

        /// <summary>
        /// Every house, hangar and tower is authored standing on the landscape.
        /// Mirrored or offset object geometry shows up as buildings hanging
        /// in the air or buried in a slope, so each footprint is checked
        /// against the same height field the tanks drive on.
        /// </summary>
        [Test]
        public void ClientSceneBuildings_StandOnTheLandscape()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var field = BattleWorld.LoadHeights(map);
            var parent = new GameObject("karieri ground test");
            BattleObjects objects = null;
            try
            {
                objects = BattleObjects.BuildExternal(
                    parent.transform, map.ExternalScene, map.ExternalDestructibles);
                var renderers = objects.Root.GetComponentsInChildren<MeshRenderer>(true);
                var checkedCount = 0;
                for (var i = 0; i < renderers.Length; i++)
                {
                    var name = renderers[i].gameObject.name;
                    if (!name.Contains("bld_kr_") || name.EndsWith("_wreck"))
                    {
                        continue;
                    }

                    // Some sheds straddle a quarry edge: the foot may not be
                    // above the ground at the centre, nor below the lowest
                    // ground under the footprint.
                    var bounds = renderers[i].bounds;
                    var ground = float.MaxValue;
                    for (var corner = 0; corner < 5; corner++)
                    {
                        var x = corner == 4 ? bounds.center.x
                            : (corner & 1) == 0 ? bounds.min.x : bounds.max.x;
                        var z = corner == 4 ? bounds.center.z
                            : (corner & 2) == 0 ? bounds.min.z : bounds.max.z;
                        ground = Mathf.Min(ground, field.SampleHeight(
                            x - map.Terrain.Origin[0], -z - map.Terrain.Origin[2]));
                    }

                    var centre = field.SampleHeight(
                        bounds.center.x - map.Terrain.Origin[0],
                        -bounds.center.z - map.Terrain.Origin[2]);
                    Assert.LessOrEqual(bounds.min.y, centre + 1.5f,
                        name + " floats above the landscape");
                    Assert.GreaterOrEqual(bounds.min.y, ground - 2.5f,
                        name + " is buried in the landscape");
                    checkedCount++;
                }

                Assert.Greater(checkedCount, 10, "the Karieri buildings should be imported");
            }
            finally
            {
                if (objects != null) objects.Release();
                Object.DestroyImmediate(parent);
            }
        }

        /// <summary>
        /// The 4.8.0 CustomProperties: 109 props author Health and a wreck
        /// switch state (concrete rings, brick and log stacks, boxes, fuel
        /// tanks, sign boards); 108 cacti author CollisionType 5 / FallType 2
        /// and no Health - the client knocks them over like trees.
        /// </summary>
        [Test]
        public void ClientSceneDestructibles_UseAuthoredWrecksAndCactiFall()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            Assert.AreEqual(109, map.ExternalDestructibles.Count);
            Assert.AreEqual(108, map.ExternalTrees.Count);

            var layout = LoadLayout();
            var names = new HashSet<string>();
            for (var i = 0; i < layout.nodes.Length; i++)
            {
                names.Add(layout.nodes[i].name);
            }

            ExternalMapDestructible wreckInfo = null;
            for (var i = 0; i < map.ExternalDestructibles.Count; i++)
            {
                var item = map.ExternalDestructibles[i];
                Assert.Greater(item.HealthFromClient, 0, item.Name);
                Assert.AreEqual(item.HealthFromClient, item.Health, item.Name);
                Assert.IsTrue(names.Contains(item.IntactGroup), item.IntactGroup);
                Assert.IsTrue(names.Contains(item.WreckGroup), item.Name + " has no authored wreck");
                if (wreckInfo == null) wreckInfo = item;
            }

            for (var i = 0; i < map.ExternalTrees.Count; i++)
            {
                var tree = map.ExternalTrees[i];
                StringAssert.StartsWith("env_kr_cactus", tree.Name);
                Assert.AreEqual(2, tree.FallType, tree.Name);
                Assert.IsTrue(names.Contains(tree.Group), tree.Group);
            }

            var parent = new GameObject("karieri destructible test");
            BattleObjects objects = null;
            try
            {
                objects = BattleObjects.BuildExternal(
                    parent.transform, map.ExternalScene,
                    map.ExternalDestructibles, map.ExternalTrees);
                Assert.AreEqual(109, objects.DestructibleCount);
                Assert.AreEqual(108, objects.TreeCount);

                // A cactus tips over away from the tank, lets the hull through
                // and stays down.
                var cacti = objects.Root.GetComponentsInChildren<BattleTree>(true);
                Assert.AreEqual(108, cacti.Length);
                for (var i = 0; i < cacti.Length; i++)
                {
                    var sensor = cacti[i].GetComponentInChildren<BattleTreeTrigger>(true);
                    Assert.IsNotNull(sensor, cacti[i].name);
                    Assert.AreEqual(BattleLayers.Physics, sensor.gameObject.layer, cacti[i].name);
                    Assert.IsTrue(sensor.GetComponent<Collider>().isTrigger, cacti[i].name);
                    var node = cacti[i].GetComponentInChildren<ClientMapNode>(true);
                    Assert.IsNotNull(node, cacti[i].name);
                    Assert.AreEqual(BattleLayers.Foliage, node.gameObject.layer, cacti[i].name);
                }

                var cactus = cacti[0];
                var cactusRenderer = cactus.GetComponentInChildren<MeshRenderer>(true);
                var topBefore = cactusRenderer.bounds.max.y;
                var impact = cactus.transform.position + Vector3.right * 3f;
                Assert.IsTrue(cactus.FallFrom(impact));
                cactus.Advance(cactus.Duration + 0.1f);
                Assert.IsTrue(cactus.Fallen);
                Assert.Less(cactusRenderer.bounds.max.y, topBefore - 0.5f, "the cactus did not fall");
                Assert.Less(cactusRenderer.bounds.center.x, cactus.transform.position.x,
                    "the cactus fell towards the tank");
                Assert.IsFalse(cactus.FallFrom(impact), "a fallen cactus fell twice");

                var native = FindDestructible(objects.Root, wreckInfo.IntactGroup);
                Assert.IsNotNull(native);
                Assert.AreEqual(BattleLayers.Destructibles, native.gameObject.layer);
                var nativeRenderer = native.GetComponent<MeshRenderer>();
                var nativeFilter = native.GetComponent<MeshFilter>();
                var nativeCollider = native.GetComponent<MeshCollider>();
                var wreckRenderer = FindRenderer(objects.Root, wreckInfo.WreckGroup);
                Assert.IsNotNull(wreckRenderer);
                var wreckFilter = wreckRenderer.GetComponent<MeshFilter>();
                Assert.IsFalse(wreckRenderer.enabled,
                    "switch state 1 must not render before the intact prop is hit");
                Assert.IsTrue(native.Damage(native.Health));
                Assert.IsTrue(native.Destroyed);
                Assert.AreSame(wreckFilter.sharedMesh, nativeFilter.sharedMesh);
                CollectionAssert.AreEqual(
                    wreckRenderer.sharedMaterials, nativeRenderer.sharedMaterials);
                Assert.IsFalse(wreckRenderer.enabled);
                Assert.IsNotNull(nativeCollider);
            }
            finally
            {
                if (objects != null) objects.Release();
                Object.DestroyImmediate(parent);
            }
        }

        private static BattleDestructible FindDestructible(GameObject root, string group)
        {
            var items = root.GetComponentsInChildren<BattleDestructible>(true);
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i].gameObject.name.Contains(group)) return items[i];
            }

            return null;
        }

        private static MeshRenderer FindRenderer(GameObject root, string group)
        {
            if (string.IsNullOrEmpty(group)) return null;
            var items = root.GetComponentsInChildren<MeshRenderer>(true);
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i].gameObject.name.Contains(group)) return items[i];
            }

            return null;
        }

        private static void AssertClustered(List<MapSpawnPoint> spawns, Vector3 centre)
        {
            for (var i = 0; i < spawns.Count; i++)
            {
                var origin = spawns[i].Origin;
                var offset = new Vector2(origin[0] - centre.x, origin[2] - centre.z);
                Assert.Less(offset.magnitude, 150f,
                    spawns[i].Name + " is not in its team's corner");
            }
        }

        private static void AssertOnTheGround(
            MapDefinition map, TerrainHeightField field, List<MapSpawnPoint> spawns)
        {
            Assert.Greater(spawns.Count, 6, "a 7v7 battle needs seven markers");
            for (var i = 0; i < spawns.Count; i++)
            {
                var origin = spawns[i].Origin;
                var ground = field.SampleHeight(
                    origin[0] - map.Terrain.Origin[0],
                    origin[2] - map.Terrain.Origin[2]);
                Assert.AreEqual(ground, origin[1], 0.01f,
                    spawns[i].Name + " is too far from the landscape");
            }
        }

        /// <summary>
        /// The bake CLI used to leave the previously baked surface loaded while
        /// it triangulated the new one, so every rebake stacked another copy of
        /// Karieri on top of the last (12845 → 18110 triangles). Duplicated
        /// polygons make a route pass straight through a wall on the stale
        /// copy, which is what left most bots wedged at spawn.
        /// </summary>
        [Test]
        public void Navmesh_IsBakedOnceInsteadOfStackedOnTheOldSurface()
        {
            var polygons = NavmeshPolygons();
            var seen = new HashSet<string>();
            var duplicates = 0;
            for (var i = 0; i < polygons.Count; i++)
            {
                if (!seen.Add(PolygonKey(polygons[i])))
                {
                    duplicates++;
                }
            }

            Assert.AreEqual(0, duplicates,
                "the karieri navmesh carries " + duplicates +
                " duplicated polygons of " + polygons.Count);
        }

        /// <summary>
        /// The client exports buildings as open render shells, so the bake kept
        /// the floor inside them walkable and routed bots through walls. Solid
        /// props now contribute a box over their bounds — except the hangar and
        /// the mine shed, which are driven through. Karieri's control point
        /// stands inside the hangar, so boxing it cut the base off the surface.
        /// </summary>
        [Test]
        public void Navmesh_ClosesTheHousesAndKeepsTheHangarBaseDrivable()
        {
            var polygons = NavmeshPolygons();

            // House entity_190 (bld_kr_house04), x -161..-144.6, z -34.5..-10.7
            // in the client's own orientation.
            var insideHouse = CountInside(polygons, -159f, -146f, -32.5f, -12.7f);
            Assert.LessOrEqual(insideHouse, 8,
                "the bake left " + insideHouse +
                " polygons inside the house04 footprint");

            // Hangar entity_98 around the Control1 base at (10.7, 17.7, -1.6).
            var insideHangar = CountInside(polygons, 0f, 21f, -12f, 8f);
            Assert.Greater(insideHangar, 0,
                "the bake boxed the hangar and cut the control point off");
        }

        private static int CountInside(
            List<Vector3[]> polygons, float minX, float maxX, float minZ, float maxZ)
        {
            var inside = 0;
            for (var i = 0; i < polygons.Count; i++)
            {
                var centre = Centre(polygons[i]);
                if (centre.x > minX && centre.x < maxX &&
                    centre.z > minZ && centre.z < maxZ)
                {
                    inside++;
                }
            }

            return inside;
        }

        private static List<Vector3[]> NavmeshPolygons()
        {
            var asset = Resources.Load<TextAsset>(BattleNavigation.ResourcePrefix + MapId);
            Assert.IsNotNull(asset, "the karieri navmesh is not in Resources");

            var table = NavigationMeshTable.Parse(asset.bytes);
            var polygons = new List<Vector3[]>();
            for (var r = 0; r < table.RegionCount; r++)
            {
                var region = table.RegionAt(r);
                for (var p = 0; p < region.PolygonCount; p++)
                {
                    var indices = region.PolygonAt(p);
                    var corners = new Vector3[indices.Length];
                    for (var i = 0; i < indices.Length; i++)
                    {
                        var point = region.VertexAt(indices[i]);
                        corners[i] = new Vector3(point.X, point.Y, point.Z);
                    }

                    polygons.Add(corners);
                }
            }

            return polygons;
        }

        private static Vector3 Centre(Vector3[] corners)
        {
            var total = Vector3.zero;
            for (var i = 0; i < corners.Length; i++)
            {
                total += corners[i];
            }

            return total / corners.Length;
        }

        /// <summary>
        /// A polygon identified by its rounded corners, so a second bake of the
        /// same ground is recognised however its vertices were reindexed.
        /// </summary>
        private static string PolygonKey(Vector3[] corners)
        {
            var keys = new List<string>(corners.Length);
            for (var i = 0; i < corners.Length; i++)
            {
                keys.Add(
                    corners[i].x.ToString("F2") + "," +
                    corners[i].y.ToString("F2") + "," +
                    corners[i].z.ToString("F2"));
            }

            keys.Sort();
            return string.Join("|", keys);
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
