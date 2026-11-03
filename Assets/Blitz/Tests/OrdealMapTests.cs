using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Ordeal ("15_ordeal_ord") comes straight from the 4.8.0 client instead of
    /// the Godot project, so these assertions pin the numbers a reviewer can
    /// read back out of the .sc2: the Landscape bounding box, the 512x512
    /// heightmap, the authored Spawn markers and the scene's own fog.
    /// </summary>
    public sealed class OrdealMapTests
    {
        private const string MapId = "ordeal";

        [Test]
        public void Manifest_MatchesTheClientScene()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            Assert.AreEqual("ordeal", map.Id);
            Assert.AreEqual("map:ordeal/name", map.NameKey);
            Assert.AreEqual("3d/Maps/15_ordeal_ord/15_ordeal_ord.sc2", map.Scene);
            Assert.AreEqual("Maps/Ordeal/ordeal_scene", map.ExternalScene);
            Assert.AreEqual("Maps/Ordeal/minimap", map.Minimap);
            Assert.AreEqual("Maps/Ordeal/loading_screen", map.LoadingScreen);

            // The client marks this map as tutorial only; the port deliberately
            // opens it to low tier random battles instead.
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, map.RandomTiers);

            // Landscape bbox (-300,-300,0)..(300,300,100): 600 m across,
            // 511 quads, corner at -300 on both horizontal axes.
            Assert.AreEqual(512, map.Terrain.Side);
            Assert.AreEqual(600f, map.Terrain.Extent, 0.001f);
            Assert.AreEqual(600f / 511f, map.Terrain.Step, 0.0001f);
            Assert.AreEqual(-300f, map.Terrain.Origin[0], 0.001f);
            Assert.AreEqual(0f, map.Terrain.Origin[1], 0.001f);
            Assert.AreEqual(-300f, map.Terrain.Origin[2], 0.001f);
            Assert.AreEqual(12.735180f, map.Terrain.MinHeight, 0.0001f);
            Assert.AreEqual(47.434196f, map.Terrain.MaxHeight, 0.0001f);
            Assert.IsTrue(map.Terrain.CollisionEnabled);

            Assert.AreEqual("Maps/Ordeal/colormap", map.Terrain.ColorMapResource);
            Assert.AreEqual("Maps/Ordeal/tilemask", map.Terrain.TileMaskResource);
            Assert.AreEqual("Maps/Ordeal/tiletextures", map.Terrain.TileTextureResource);

            // The play area is the authored client boundary
            // (borderTop/BotX/Y of the landscape), not the landscape bbox: the
            // red no-entry fence stands well inside the terrain, and the hull
            // clamp has to sit on the fence.
            // The `dava` orientation stores -y as the manifest z, so the
            // client's (borderBotY, borderTopY) = (-252.5, 226.2) flips.
            Assert.AreEqual(-250.8f, map.BorderMin[0], 0.01f);
            Assert.AreEqual(-226.2f, map.BorderMin[2], 0.01f);
            Assert.AreEqual(227.9f, map.BorderMax[0], 0.01f);
            Assert.AreEqual(252.5f, map.BorderMax[2], 0.01f);
            Assert.AreEqual(478.7f, map.SizeX, 0.01f);
            Assert.AreEqual(478.7f, map.SizeZ, 0.01f);

            // SkyFlattenSphere is an ordinary entity inside the .sc2, so the
            // port must not add the substitute sky planes the Godot maps need.
            Assert.AreEqual(0, map.SkyFaces.Count);

            // The scene authors three ally corners and two enemy ones. A
            // battle uses one of each, so the port keeps the widest separated
            // pair: the allies north and the enemies south (Spawn2x). Of the
            // three ally sets only the open north slope survives - the walled
            // tutorial pocket and the fort yard are dropped - and three
            // replacement slots beside it keep the team at seven.
            Assert.AreEqual(7, map.TeamA.Count);
            Assert.AreEqual(10, map.TeamB.Count);
            Assert.AreEqual(0, map.TeamA[0].Team);
            Assert.AreEqual(1, map.TeamB[0].Team);

            // Opposite corners, both driving at the control point in between.
            // Manifest z is -y: the allies' north slope is z < 0.
            Assert.Less(TeamCentre(map.TeamA).z, -50f);
            Assert.Greater(TeamCentre(map.TeamB).z, 100f);
            Assert.IsTrue(map.ClientOrientation, "orientation: dava");
            Assert.IsTrue(map.ClientScene, "sceneFormat: client");
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

        /// <summary>
        /// The scene's Scene_Global_Material authors the fog the client draws
        /// with, and `Light dynamic` the ambient colour. Neither reaches the
        /// port through a Godot lighting manifest, so the manifest carries
        /// them itself.
        /// </summary>
        [Test]
        public void Environment_CarriesTheClientFogAndAmbient()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            Assert.IsNotNull(map.Environment);
            Assert.AreEqual(-50f, map.Environment.FogStart, 0.01f);
            Assert.AreEqual(500f, map.Environment.FogEnd, 0.01f);
            Assert.AreEqual(0.85f, map.Environment.FogLimit, 0.001f);
            Assert.AreEqual(0.99f, map.Environment.FogColor[0], 0.01f);

            // DAVA stops at fogLimit while Unity's own linear fog always
            // reaches full opacity, so the built-in far distance is stretched
            // to land on the authored limit at the authored range.
            var end = BattleWorld.LinearFogEnd(-50f, 500f, 0.85f);
            Assert.AreEqual(0.85f, 1f - ((end - 500f) / (end + 50f)), 0.001f);
        }

        /// <summary>
        /// Ordeal's Scene_Global_Material switches FOG_HALFSPACE on in its
        /// FOG_HALFSPACE_LINEAR variant (Lengyel's unified fog): the haze
        /// lies below 16 m, in the river valley and the low fort yard.
        /// </summary>
        [Test]
        public void Environment_CarriesTheLinearHalfSpaceFog()
        {
            var halfspace = BattleWorld.LoadDefinition(MapId).Environment.FogHalfspace;

            Assert.IsNotNull(halfspace);
            Assert.IsTrue(halfspace.Linear);
            Assert.AreEqual(0.006f, halfspace.Density, 0.00001f);
            Assert.AreEqual(1f, halfspace.Falloff, 0.0001f);
            Assert.AreEqual(16f, halfspace.Height, 0.0001f);
            Assert.AreEqual(0.2f, halfspace.Limit, 0.0001f);
        }

        /// <summary>
        /// The 4.8.0 CustomProperties: 224 props author Health or a wreck
        /// switch state and break into their authored wreck; the 95 trees and
        /// the 16 telegraph poles and loudspeaker masts (CollisionType 5,
        /// FallType 2) topple under a tank.
        /// </summary>
        [Test]
        public void ClientSceneDestructibles_UseAuthoredWrecksAndPolesFall()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            Assert.AreEqual(224, map.ExternalDestructibles.Count);
            var wrecks = 0;
            for (var i = 0; i < map.ExternalDestructibles.Count; i++)
            {
                if (!string.IsNullOrEmpty(map.ExternalDestructibles[i].WreckGroup))
                {
                    wrecks++;
                }
            }

            Assert.AreEqual(224, wrecks, "every switch wreck of 4.8.0 should be exported");

            var layout = LoadLayout();
            var poles = 0;
            for (var i = 0; i < layout.nodes.Length; i++)
            {
                var name = layout.nodes[i].name;
                if (name.Contains("env_27_stolb") || name.Contains("env_23_Loudspeaker"))
                {
                    poles++;
                    StringAssert.DoesNotEndWith("_intact", name, name + " is no prop with Health");
                }
            }

            Assert.AreEqual(16, poles);
            Assert.AreEqual(111, map.ExternalTrees.Count, "95 trees and 16 poles fall");
        }

        /// <summary>
        /// The client routes its tutorial bots along PathComponents, and the
        /// port turns their leaves into the roaming goals the AI scores. With
        /// an empty list every bot stands still in its spawn.
        /// </summary>
        [Test]
        public void AiMarkers_CoverTheAuthoredBotRoutes()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            Assert.Greater(map.AiMarkers.Count, 8);
            var objective = map.AiMarkers.Find(marker => marker.Priority > 0);
            Assert.IsNotNull(objective, "the control point should outrank a route end");

            // A marker the bots cannot reach is the same as no marker at all,
            // so every one of them has to sit on the landscape rather than
            // under it or off the edge.
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
        /// `.mtl` cannot express either effect, so the porter carries them in
        /// the material name.
        /// </summary>
        [Test]
        public void MaterialNames_CarryTheClientEffect()
        {
            Assert.IsTrue(BattleObjects.IsTwoSidedName("m_12_fence_nocull"));
            Assert.IsTrue(BattleObjects.IsCutoutName("m_12_leaf_cutout"));
            Assert.IsTrue(BattleObjects.IsTwoSidedName("m_12_leaf_nocull_cutout"));
            Assert.IsFalse(BattleObjects.IsTwoSidedName("m_12_house"));
            Assert.IsFalse(BattleObjects.IsCutoutName("m_12_house"));
        }

        /// <summary>
        /// Ordeal carries 116 SpeedTreeObjects, the same kind of entity that
        /// put black rectangles on Karieri's ground, so its trees have to be
        /// rebuilt into upright billboards too.
        /// </summary>
        [Test]
        public void Foliage_StandsUpInsteadOfLyingOnTheGround()
        {
            var layout = LoadLayout();
            var trees = 0;
            for (var i = 0; i < layout.nodes.Length; i++)
            {
                var node = layout.nodes[i];
                if (!node.name.Contains("_tree_"))
                {
                    continue;
                }

                trees++;
                Assert.Greater(node.max[1] - node.min[1], 1f,
                    node.name + " is a flat card lying on the ground again");
            }

            Assert.GreaterOrEqual(trees, 95, "every authored tree should survive the export");
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

            // Heights are bbox.minZ + (bbox.maxZ - bbox.minZ) * raw / 65535.
            Assert.AreEqual(map.Terrain.MinHeight, min, 0.0001f);
            Assert.AreEqual(map.Terrain.MaxHeight, max, 0.0001f);
            Assert.Greater(min, 0f);
            Assert.Less(max, 100f);
        }

        /// <summary>
        /// Unity negates X while importing an OBJ, so a scene exported without
        /// the porter's pre-mirror lands every prop on the opposite side of the
        /// map from the landscape it was authored on: houses hang in the air
        /// and others sink into a slope. Each building footprint is checked
        /// against the same height field the tanks drive on.
        /// </summary>
        [Test]
        public void ClientSceneBuildings_StandOnTheLandscape()
        {
            // Client-authored exceptions: the watchtowers and the ruin stand on
            // the walls rather than on the ground, and the corner wall is sunk
            // into its hill. They match the 4.8.0 placements exactly.
            var authoredOffTheGround = new HashSet<string>
            {
                "entity_233_bld_ord_ruin_sc2",
                "entity_391_bld_ord_observation_sc2",
                "entity_393_bld_ord_observation_sc2",
                "entity_423_bld_ord_observation2_sc2",
                "entity_412_bld_ord_angleWall_sc2",
            };

            var map = BattleWorld.LoadDefinition(MapId);
            var field = BattleWorld.LoadHeights(map);
            var parent = new GameObject("ordeal ground test");
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
                    if (!name.Contains("bld_ord_") || name.EndsWith("_wreck")
                        || authoredOffTheGround.Contains(name))
                    {
                        continue;
                    }

                    // A wall may straddle a slope: the foot may not be above
                    // the ground at the centre, nor below the lowest ground
                    // under the footprint.
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

                Assert.Greater(checkedCount, 100, "the Ordeal buildings should be imported");
            }
            finally
            {
                if (objects != null) objects.Release();
                Object.DestroyImmediate(parent);
            }
        }

        /// <summary>
        /// The client starts its tutorial either in the walled pocket north
        /// east of the fort or in the fort yard, and seals the pocket with
        /// no-entry fences. Both are one-exit traps in a random battle, so the
        /// port spawns nobody there, drops the fences that closed the pocket
        /// off and the green tutorial decals that pointed at those starts.
        /// </summary>
        [Test]
        public void TutorialStarts_AreNeitherUsedNorWalledOff()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            // Manifest coordinates: x and -y of the client scene.
            var pocket = new Vector2(-64f, -123f);
            var yard = new Vector2(-228f, 56f);
            for (var i = 0; i < map.TeamA.Count; i++)
            {
                var origin = map.TeamA[i].Origin;
                var position = new Vector2(origin[0], origin[2]);
                Assert.Greater(Vector2.Distance(position, pocket), 40f,
                    map.TeamA[i].Name + " still starts in the tutorial pocket");
                Assert.Greater(Vector2.Distance(position, yard), 40f,
                    map.TeamA[i].Name + " still starts in the fort yard");
            }

            var layout = LoadLayout();
            for (var i = 0; i < layout.nodes.Length; i++)
            {
                var node = layout.nodes[i];
                var name = node.name;
                Assert.IsFalse(name.Contains("select01"),
                    name + " is a tutorial decal and should not be exported");
                if (name.Contains("env_ord_restriction"))
                {
                    // The pocket's own fences; the rest of the chain marks the
                    // map border and stays. Layout bounds are Unity space,
                    // where z is the client's y.
                    var x = (node.min[0] + node.max[0]) * 0.5f;
                    var z = (node.min[2] + node.max[2]) * 0.5f;
                    Assert.IsFalse(x > -100f && x < -24f && z > 118f,
                        name + " still seals the tutorial pocket off");
                }
            }
        }

        [Test]
        public void SpawnMarkers_StandOnTheLandscape()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var field = BattleWorld.LoadHeights(map);

            AssertOnTheGround(map, field, map.TeamA);
            AssertOnTheGround(map, field, map.TeamB);
        }

        private static void AssertOnTheGround(
            MapDefinition map, TerrainHeightField field, List<MapSpawnPoint> spawns)
        {
            Assert.Greater(spawns.Count, 6, "a 7v7 battle needs seven markers");
            for (var i = 0; i < spawns.Count; i++)
            {
                var origin = spawns[i].Origin;
                // Unlike Rudniki, this landscape is centred on the origin, so a
                // sample has to be taken relative to the bounding box corner.
                var ground = field.SampleHeight(
                    origin[0] - map.Terrain.Origin[0],
                    origin[2] - map.Terrain.Origin[2]);
                Assert.AreEqual(ground, origin[1], 0.01f,
                    spawns[i].Name + " is too far from the landscape");
            }
        }

        [Test]
        public void Catalog_OffersOrdealToLowTiersOnly()
        {
            for (var tier = 1; tier <= 4; tier++)
            {
                CollectionAssert.Contains(
                    BattleMapCatalog.AvailableForTier(tier), MapId,
                    "tier " + tier + " should be able to roll Ordeal");
            }

            for (var tier = 5; tier <= 10; tier++)
            {
                CollectionAssert.DoesNotContain(
                    BattleMapCatalog.AvailableForTier(tier), MapId,
                    "tier " + tier + " must not roll Ordeal");
            }

            // A manifest without randomTiers keeps its old "every tier" rule.
            CollectionAssert.Contains(
                BattleMapCatalog.AvailableForTier(10), BattleWorld.DefaultMapId);
        }

        [Test]
        public void Catalog_PicksAMapFromTheTierRoster()
        {
            var roster = BattleMapCatalog.AvailableForTier(2);
            for (var index = 0; index < roster.Count; index++)
            {
                var picked = BattleMapCatalog.PickRandomForTier(
                    2, new FixedRandomSource(index));
                Assert.AreEqual(roster[index], picked);
            }

            // A tier with no eligible optional map still starts a battle.
            Assert.AreEqual(
                BattleWorld.DefaultMapId,
                BattleMapCatalog.PickRandomForTier(10, new FixedRandomSource(0)));
        }

        /// <summary>
        /// SkyFlattenSphere is authored as a ~10 m dome because the client
        /// draws it locked to the camera. Left alone it would be a small grey
        /// blob in the middle of the map.
        /// </summary>
        [Test]
        public void SkyDome_IsPushedOutToTheHorizon()
        {
            Assert.IsTrue(BattleObjects.IsSkyName("entity_93_SkyFlattenSphere_sc2"));
            Assert.IsFalse(BattleObjects.IsSkyName("entity_12_house_sc2"));

            var holder = new GameObject("sky");
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    new Vector3(-10f, 0f, -10f),
                    new Vector3(10f, 0f, -10f),
                    new Vector3(10f, 5f, 10f),
                    new Vector3(-10f, 5f, 10f),
                };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                mesh.RecalculateBounds();

                BattleObjects.PushSkyToTheHorizon(holder.transform, mesh);

                var map = BattleWorld.LoadDefinition(MapId);
                var reach = mesh.bounds.extents.x * holder.transform.localScale.x;
                Assert.AreEqual(BattleObjects.SkyRadius, reach, 0.01f);
                Assert.Greater(reach, map.SizeX);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(holder);
            }
        }

        /// <summary>
        /// The client fences Ordeal with chains of env_ord_restriction panels:
        /// a translucent pane about 15 m wide carrying a red no-entry sign on
        /// both faces. The play area is clamped by MapDefinition.Border*, so
        /// the panels only blocked the view and the drivable ground behind
        /// them. They must leave no renderer and no collider behind.
        /// </summary>
        [Test]
        public void ClientRestrictionPanels_AreDroppedFromTheBattleScene()
        {
            Assert.IsTrue(BattleObjects.IsMapBorderName(
                "entity_296_env_ord_restriction02_sc2"));
            Assert.IsTrue(BattleObjects.IsMapBorderName(
                "entity_159_env_ord_restriction01_sc2"));
            Assert.IsFalse(BattleObjects.IsMapBorderName("entity_234_bld_ord_ruin_sc2"));
            Assert.IsFalse(BattleObjects.IsMapBorderName(
                "entity_509_env_border01_sc2_intact"));

            var map = BattleWorld.LoadDefinition(MapId);
            var layout = LoadLayout();
            var authored = 0;
            for (var i = 0; i < layout.nodes.Length; i++)
            {
                if (BattleObjects.IsMapBorderName(layout.nodes[i].name))
                {
                    authored++;
                }
            }

            // 32 in the client, less the four tutorial-tagged pocket panels.
            Assert.AreEqual(28, authored,
                "the client scene should still author the restriction panels");

            var parent = new GameObject("ordeal border test");
            BattleObjects objects = null;
            try
            {
                objects = BattleObjects.BuildExternal(
                    parent.transform, map.ExternalScene, map.ExternalDestructibles);

                Assert.AreEqual(28, objects.BorderPanelCount);

                var renderers = objects.Root.GetComponentsInChildren<MeshRenderer>(true);
                for (var i = 0; i < renderers.Length; i++)
                {
                    Assert.IsFalse(
                        BattleObjects.IsMapBorderName(renderers[i].gameObject.name),
                        renderers[i].gameObject.name + " is still in the battle scene");
                }

                var destructibles = objects.Root
                    .GetComponentsInChildren<BattleDestructible>(true);
                for (var i = 0; i < destructibles.Length; i++)
                {
                    Assert.IsFalse(
                        BattleObjects.IsMapBorderName(destructibles[i].gameObject.name),
                        destructibles[i].gameObject.name + " is still destructible");
                }
            }
            finally
            {
                if (objects != null)
                {
                    objects.Release();
                }

                Object.DestroyImmediate(parent);
            }
        }

        /// <summary>
        /// Ordeal doubles as the 4.8.0 tutorial. Everything the client tags
        /// `tutorial` - the glowing brick fences beside the north slope
        /// spawns, the Invisible_wall planes by the bunkers, the arrows, path
        /// decals and lesson blocks - belongs to the scripted lesson, not to
        /// a random battle, and must not be exported.
        /// </summary>
        [Test]
        public void TutorialProps_AreNotExported()
        {
            var layout = LoadLayout();
            string[] tutorialOnly =
            {
                "brick_fence", "brick_glow", "Invisible_wall", "Tut_arrow",
                "Brick_slope", "Oldtruck_tut", "bunker_eye", "old_truck_eye",
            };
            for (var i = 0; i < layout.nodes.Length; i++)
            {
                var name = layout.nodes[i].name;
                for (var k = 0; k < tutorialOnly.Length; k++)
                {
                    Assert.IsFalse(name.Contains(tutorialOnly[k]),
                        name + " is a tutorial prop and should not be exported");
                }
            }
        }

        /// <summary>
        /// The client draws a fence as loose planks and a rock as a dented
        /// shell, so cooking that geometry concave gives a hull ledges to
        /// snag on. Prop sized colliders have to be convex; architecture the
        /// tanks drive into keeps its authored shape.
        /// </summary>
        [Test]
        public void PropColliders_AreConvexSoTanksSlideOff()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var parent = new GameObject("ordeal collider test");
            BattleObjects objects = null;
            try
            {
                objects = BattleObjects.BuildExternal(
                    parent.transform, map.ExternalScene, map.ExternalDestructibles);

                var colliders = objects.Root.GetComponentsInChildren<MeshCollider>(true);
                var convex = 0;
                var concave = 0;
                var fences = 0;
                for (var i = 0; i < colliders.Length; i++)
                {
                    var collider = colliders[i];
                    if (collider.isTrigger || collider.sharedMesh == null)
                    {
                        continue;
                    }

                    var name = collider.gameObject.name;
                    if (collider.convex)
                    {
                        convex++;
                        var size = Vector3.Scale(
                            collider.sharedMesh.bounds.size, collider.transform.lossyScale);
                        Assert.Greater(
                            Mathf.Min(Mathf.Abs(size.x), Mathf.Min(Mathf.Abs(size.y), Mathf.Abs(size.z))),
                            0f,
                            name + " has no volume to cook a hull from");
                    }
                    else
                    {
                        concave++;
                    }

                    if (name.IndexOf("concretefence", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fences++;
                        Assert.IsTrue(collider.convex,
                            name + " is a fence panel and must not snag a hull");
                    }
                }

                Assert.Greater(fences, 0, "the concrete fences belong to this map");
                Assert.Greater(convex, concave,
                    "most of the client scene is prop sized geometry");
                Assert.Greater(concave, 0,
                    "buildings and cliffs must keep their authored shape");
            }
            finally
            {
                if (objects != null)
                {
                    objects.Release();
                }

                Object.DestroyImmediate(parent);
            }
        }

        private static ClientMapLayout LoadLayout()
        {
            var asset = Resources.Load<TextAsset>("Maps/Ordeal/ordeal_scene_layout");
            Assert.IsNotNull(asset, "the ordeal scene layout is not in Resources");
            return JsonUtility.FromJson<ClientMapLayout>(asset.text);
        }

        private sealed class FixedRandomSource : IRandomSource
        {
            private readonly int value;

            public FixedRandomSource(int value)
            {
                this.value = value;
            }

            public int RangeInclusive(int minimum, int maximum)
            {
                return Mathf.Clamp(value, minimum, maximum);
            }

            public float Value()
            {
                return 0f;
            }
        }
    }
}
