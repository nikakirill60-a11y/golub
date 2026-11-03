using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class BattleMapTests
    {
        /// <summary>
        /// The manifest is the port's copy of _rudniki.tscn, so these
        /// numbers are the ones a reviewer can grep for in the source scene.
        /// </summary>
        [Test]
        public void MapManifest_MatchesTheSourceScene()
        {
            var map = BattleWorld.LoadDefinition(BattleWorld.DefaultMapId);

            Assert.AreEqual("rudniki", map.Id);
            Assert.AreEqual("map:rudniki/name", map.NameKey);
            Assert.AreEqual(513, map.Terrain.Side);
            Assert.AreEqual(1.17f, map.Terrain.Step, 0.0001f);
            Assert.AreEqual(599.04f, map.Terrain.Extent, 0.001f);
            Assert.AreEqual(0f, map.Terrain.MinHeight, 0.0001f);
            Assert.AreEqual(79.924744f, map.Terrain.MaxHeight, 0.0001f);
            Assert.AreEqual(4210732, map.Terrain.Bytes);
            Assert.IsTrue(map.Terrain.CollisionEnabled);
            // The landscape node sets cast_shadow = 0.
            Assert.AreEqual(0, map.Terrain.CastShadow);

            // Tools/port_godot_map_scene.py copies the hterrain maps the live
            // Godot scene samples; the tile detail stays lossless as in its
            // .import (compress/mode = 0).
            Assert.IsTrue(map.GodotScene, "Rudniki is a Godot-ported render scene");
            Assert.AreEqual("Maps/Rudniki/rudniki_scene", map.ExternalScene);
            CollectionAssert.AreEqual(new[] { 0f, 0f, 0f }, map.Terrain.Origin);
            Assert.AreEqual("Maps/Rudniki/Textures/terrain_color_raw", map.Terrain.ColorMapResource);
            Assert.AreEqual("Maps/Rudniki/Textures/terrain_splat_raw_nomip", map.Terrain.TileMaskResource);
            Assert.AreEqual("Maps/Rudniki/Textures/terrain_tile_lossless_raw", map.Terrain.TileTextureResource);
            Assert.AreEqual("Maps/Rudniki/Textures/terrain_normal_raw_nomip", map.Terrain.NormalMapResource);
            Assert.IsNotNull(map.Terrain.Godot);
            Assert.AreEqual(513, map.Terrain.Godot.Resolution);
            Assert.AreEqual(1.17f, map.Terrain.Godot.MapScale[0], 0.0001f);
            Assert.AreEqual(1.17f, map.Terrain.Godot.MapScale[2], 0.0001f);
            Assert.AreEqual(
                "maps/rudniki/terrain/normal.png",
                map.Terrain.NormalSource);
            Assert.AreEqual(-0.036834292f, map.Terrain.SurfaceNormal[0], 0.0000001f);
            Assert.AreEqual(0.999046445f, map.Terrain.SurfaceNormal[1], 0.0000001f);
            Assert.AreEqual(-0.023440003f, map.Terrain.SurfaceNormal[2], 0.0000001f);
            Assert.AreEqual(50.000002f, map.Terrain.TileTexScale, 0.0001f);
            Assert.AreEqual(1f, map.Terrain.DetailStrength, 0.0001f);
            Assert.AreEqual(0.42000002f, map.Terrain.DetailContrast, 0.0001f);
            Assert.AreEqual(1f, map.Terrain.Brightness, 0.0001f);
            Assert.AreEqual(1f, map.Terrain.Contrast, 0.0001f);
            Assert.AreEqual(1.1800001f, map.Terrain.MaskSharpness, 0.0001f);
            CollectionAssert.AreEqual(
                new[] { 0.7058824f, 0.6784314f, 0.27450982f, 1f },
                map.Terrain.TileColour2);

            Assert.AreEqual(1.465f, map.Light.Energy, 0.0001f);
            Assert.AreEqual(10, map.TeamA.Count);
            Assert.AreEqual(10, map.TeamB.Count);
            Assert.AreEqual(0, map.TeamA[0].Team);
            Assert.AreEqual(1, map.TeamB[0].Team);

            // map_border_min / map_border_max drive _battle.gd's play area.
            Assert.AreEqual(516.8951f, map.SizeX, 0.01f);
            Assert.AreEqual(520.3784f, map.SizeZ, 0.01f);

            // The five sky planes are scene nodes now (role "sky"), drawn by
            // the unshaded Godot sky shader instead of manifest faces.
            Assert.AreEqual(0, map.SkyFaces.Count);
        }

        [Test]
        public void SkyPlanes_AreUnshadedSceneNodesAtTheFarPlane()
        {
            var map = BattleWorld.LoadDefinition(BattleWorld.DefaultMapId);
            var layout = JsonUtility.FromJson<ClientMapLayout>(
                Resources.Load<TextAsset>(map.ExternalScene + "_layout").text);
            var sky = 0;
            for (var i = 0; i < layout.nodes.Length; i++)
            {
                var node = layout.nodes[i];
                if (node.role != ClientMapScene.SkyRole)
                {
                    continue;
                }

                sky++;
                StringAssert.StartsWith("sky/face", node.name);
                for (var m = 0; m < node.materials.Length; m++)
                {
                    var material = layout.materials[node.materials[m]];
                    Assert.AreEqual(ClientMapScene.GodotSkyKind, material.kind, node.name);
                    Assert.IsTrue(material.unshaded, node.name);
                }

                // QuadMesh 10000 x 10000 planes 5000 m out (scale 5000).
                var size = new Vector3(
                    node.max[0] - node.min[0], node.max[1] - node.min[1], node.max[2] - node.min[2]);
                Assert.Greater(Mathf.Max(size.x, size.y, size.z), 9990f, node.name);
            }

            Assert.AreEqual(5, sky);
        }

        [Test]
        public void LandscapeMaterial_UsesTheSourceDetailBlend()
        {
            var map = BattleWorld.LoadDefinition(BattleWorld.DefaultMapId);
            var material = BattleWorld.CreateLandscapeMaterial(map);

            try
            {
                // landscape.gdshader: colormap/splat/normal/tile maps with the
                // hterrain cell UV, per-vertex lambert from the normal map.
                Assert.AreEqual(ClientMapScene.GodotLandscapeShader, material.shader.name);
                Assert.AreSame(
                    Resources.Load<Texture2D>(map.Terrain.ColorMapResource),
                    material.GetTexture("_ColorMap"));
                Assert.AreSame(
                    Resources.Load<Texture2D>(map.Terrain.TileMaskResource),
                    material.GetTexture("_SplatMap"));
                Assert.AreSame(
                    Resources.Load<Texture2D>(map.Terrain.NormalMapResource),
                    material.GetTexture("_NormalMap"));
                Assert.AreSame(
                    Resources.Load<Texture2D>(map.Terrain.TileTextureResource),
                    material.GetTexture("_TileTexture"));

                var godot = map.Terrain.Godot;
                var terrain = material.GetVector("_Terrain");
                Assert.AreEqual(godot.MapScale[0], terrain.x, 0.0001f);
                Assert.AreEqual(godot.MapScale[2], terrain.y, 0.0001f);
                Assert.AreEqual(513f, terrain.z, 0.0001f);
                var detail = material.GetVector("_Detail");
                Assert.AreEqual(50.000002f, detail.x, 0.0001f);
                Assert.AreEqual(1f, detail.y, 0.0001f);
                Assert.AreEqual(0.42000002f, detail.z, 0.0001f);
                Assert.AreEqual(1.1800001f, detail.w, 0.0001f);
                var grade = material.GetVector("_Grade");
                Assert.AreEqual(1f, grade.x, 0.0001f);
                Assert.AreEqual(1f, grade.y, 0.0001f);
                // The source fragment shader has no macro luminance
                // normalisation. Re-adding one flattened the authored
                // colour map into grey blotches across the whole map.
                Assert.IsFalse(material.HasProperty("_MacroVariationStrength"),
                    "the landscape must not normalise the source colour map");

                // tile_col_* are source_color uniforms: the porter stores them
                // already decoded to linear.
                var tileColour = material.GetVector("_TileColor2");
                Assert.AreEqual(godot.TileColours[2][0], tileColour.x, 0.0001f);
                Assert.AreEqual(godot.TileColours[2][1], tileColour.y, 0.0001f);
                Assert.AreEqual(godot.TileColours[2][2], tileColour.z, 0.0001f);
                Assert.Less(tileColour.x, map.Terrain.TileColour2[0],
                    "the tile colour has to be linear, not the sRGB value");
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void LandscapeTextures_FollowTheGodotImport()
        {
            // color.png: VRAM-compressed with mipmaps in its .import.
            AssertTerrainImport("terrain_color_raw", true, TextureImporterCompression.CompressedHQ,
                TextureImporterFormat.ASTC_4x4);
            // hterrain splat and normal maps are sampled at one level.
            AssertTerrainImport("terrain_splat_raw_nomip", false, TextureImporterCompression.Uncompressed,
                TextureImporterFormat.RGBA32);
            AssertTerrainImport("terrain_normal_raw_nomip", false, TextureImporterCompression.Uncompressed,
                TextureImporterFormat.RGBA32);
            // TileTxRudniki: compress/mode = 0. DXT/ASTC drew 4x4 blocks
            // across the near ground that the Godot frame does not have.
            AssertTerrainImport("terrain_tile_lossless_raw", true, TextureImporterCompression.Uncompressed,
                TextureImporterFormat.RGBA32);
        }

        private static void AssertTerrainImport(
            string name, bool mipMaps, TextureImporterCompression compression,
            TextureImporterFormat android)
        {
            var path = "Assets/Blitz/Resources/Maps/Rudniki/Textures/" + name + ".png";
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            Assert.IsNotNull(importer, path + " has no texture importer");
            Assert.IsFalse(importer.sRGBTexture, path + " must stay linear data");
            Assert.AreEqual(mipMaps, importer.mipmapEnabled, path);
            Assert.AreEqual(
                mipMaps ? FilterMode.Trilinear : FilterMode.Bilinear,
                importer.filterMode, path);
            // Godot samples these filter_linear(_mipmap), never anisotropic.
            Assert.AreEqual(1, importer.anisoLevel, path);
            Assert.AreEqual(compression, importer.textureCompression, path);

            var settings = importer.GetPlatformTextureSettings("Android");
            Assert.IsTrue(settings.overridden, path + " needs an Android override");
            Assert.AreEqual(android, settings.format, path);
        }

        [Test]
        public void TerrainHeightField_ReadsTheExportedGrid()
        {
            var map = BattleWorld.LoadDefinition(BattleWorld.DefaultMapId);
            var field = BattleWorld.LoadHeights(map);

            Assert.IsNotNull(field, "terrain height field is missing");
            Assert.AreEqual(513, field.Side);
            Assert.AreEqual(263169, field.VertexCount);
            Assert.AreEqual(524288, field.TriangleCount);
            Assert.AreEqual(599.04f, field.ExtentX, 0.001f);
            Assert.AreEqual(599.04f, field.ExtentZ, 0.001f);

            // The source grid starts on the north-west ridge and drops to zero
            // along the other three corners. A transposed or flipped export
            // would move this value and invalidate every authored placement.
            Assert.AreEqual(37.167683f, field.HeightAt(0, 0), 0.0001f);
            Assert.AreEqual(0f, field.HeightAt(512, 0), 0.0001f);
            Assert.AreEqual(0f, field.HeightAt(0, 512), 0.0001f);
            Assert.AreEqual(0f, field.HeightAt(512, 512), 0.0001f);

            Assert.AreEqual(field.HeightAt(10, 20),
                field.SampleHeight(10f * field.StepX, 20f * field.StepZ), 0.001f);
        }

        /// <summary>
        /// The landscape is what a spawning tank lands on, so every marker is
        /// measured against it. hterrain.gd:731 builds the ground through
        /// Transform3D(basis.scaled(map_scale), origin), so the heights carry
        /// map_scale.y = 0.25. terrain_mesh.tres was baked at 0.3 and used to
        /// feed the export, which raised the landscape by a fifth, buried the
        /// markers and dropped a spawning tank straight through the map.
        /// </summary>
        [Test]
        public void SpawnMarkers_StandOnTheLandscape()
        {
            var map = BattleWorld.LoadDefinition(BattleWorld.DefaultMapId);
            var field = BattleWorld.LoadHeights(map);

            AssertOnTheGround(field, map.TeamA);
            AssertOnTheGround(field, map.TeamB);
        }

        private static void AssertOnTheGround(
            TerrainHeightField field, List<MapSpawnPoint> spawns)
        {
            Assert.AreEqual(10, spawns.Count);
            for (var i = 0; i < spawns.Count; i++)
            {
                var origin = spawns[i].Origin;
                var ground = field.SampleHeight(origin[0], origin[2]);
                Assert.AreEqual(ground, origin[1], 2f,
                    spawns[i].Name + " is too far from the landscape");
            }
        }

        /// <summary>
        /// The whole port hangs on mirroring the source exactly once, so the
        /// two spawn markers that face each other are asserted head on.
        /// </summary>
        [Test]
        public void GodotSpace_MirrorsTheSourceAcrossZ()
        {
            var point = GodotSpace.Point(418.10153f, 8.130057f, 479.62875f);
            Assert.AreEqual(418.10153f, point.X, 0.0001f);
            Assert.AreEqual(8.130057f, point.Y, 0.0001f);
            Assert.AreEqual(-479.62875f, point.Z, 0.0001f);

            var map = BattleWorld.LoadDefinition(BattleWorld.DefaultMapId);
            Vec3 right, up, forward;
            // Godot writes a basis by rows, so an axis is a column: the
            // spawn's Z axis is (basis[2], basis[5], basis[8]).
            GodotSpace.Basis(map.TeamA[0].Basis, out right, out up, out forward);
            Assert.AreEqual(-0.29452404f, forward.X, 0.0001f);
            Assert.AreEqual(0.9556441f, forward.Z, 0.0001f);
            Assert.AreEqual(1f, up.Y, 0.0001f);

            Vec3 enemyRight, enemyUp, enemyForward;
            GodotSpace.Basis(map.TeamB[0].Basis, out enemyRight, out enemyUp, out enemyForward);
            Assert.AreEqual(0.9796944f, enemyForward.X, 0.0001f);
            Assert.AreEqual(-0.2004966f, enemyForward.Z, 0.0001f);

            // Both sides still point into the play area after the mirror.
            var facing = forward.X * enemyForward.X + forward.Z * enemyForward.Z;
            Assert.Less(facing, -0.45f);
        }

        /// <summary>
        /// Godot serialises a basis by rows, so an axis is a column. Reading
        /// the rows as axes transposes the matrix, which for a rotation is its
        /// inverse, and the map sun then pointed up into the sky: nothing lit
        /// the terrain but flat ambient and the device rendered a black frame.
        /// </summary>
        [Test]
        public void Sun_ShinesDownOnTheMap()
        {
            var map = BattleWorld.LoadDefinition(BattleWorld.DefaultMapId);

            Vec3 right, up, forward;
            GodotSpace.Basis(map.Light.Basis, out right, out up, out forward);

            Assert.AreEqual(0.63418645f, forward.X, 0.0001f);
            Assert.AreEqual(-0.77314985f, forward.Y, 0.0001f);
            Assert.AreEqual(-0.00684564f, forward.Z, 0.0001f);
            Assert.Less(forward.Y, -0.7f, "the sun has to light the ground");
        }

        /// <summary>
        /// Rebuilding the landscape has to reproduce the baked ArrayMesh, so
        /// the mesh is checked for size, placement and, above all, for facing
        /// upwards: getting the mirrored winding wrong is invisible in the
        /// numbers but leaves the whole map inside out.
        /// </summary>
        [Test]
        public void TerrainMesh_SitsAtTheOriginAndFacesUp()
        {
            var map = BattleWorld.LoadDefinition(BattleWorld.DefaultMapId);
            var field = BattleWorld.LoadHeights(map);
            var mesh = BattleTerrain.BuildMesh(field, "TestTerrain");

            try
            {
                Assert.AreEqual(field.VertexCount, mesh.vertexCount);
                Assert.AreEqual(field.TriangleCount * 3, mesh.triangles.Length);

                // The HTerrain node has no transform, so the surface starts at
                // the origin and runs into negative Z once mirrored.
                var bounds = mesh.bounds;
                Assert.AreEqual(0f, bounds.min.x, 0.001f);
                Assert.AreEqual(599.04f, bounds.max.x, 0.01f);
                Assert.AreEqual(-599.04f, bounds.min.z, 0.01f);
                Assert.AreEqual(0f, bounds.max.z, 0.001f);
                Assert.AreEqual(0f, bounds.min.y, 0.001f);
                Assert.AreEqual(79.924744f, bounds.max.y, 0.001f);

                // Rudniki paints one normal per height sample. The runtime mesh
                // must carry that complete authored field instead of flattening
                // it to the compatibility sample stored in the manifest.
                var normals = mesh.normals;
                var worstNormalError = 0f;
                for (var z = 0; z < field.Side; z++)
                {
                    for (var x = 0; x < field.Side; x++)
                    {
                        float nx, ny, nz;
                        field.NormalAt(x, z, out nx, out ny, out nz);
                        var source = GodotSpace.Point(nx, ny, nz);
                        var expected = new Vector3(source.X, source.Y, source.Z).normalized;
                        var index = z * field.Side + x;
                        worstNormalError = Mathf.Max(
                            worstNormalError, (normals[index] - expected).sqrMagnitude);
                    }
                }

                Assert.Less(worstNormalError, 1e-10f,
                    "terrain normals do not match the source map");

                // Explicit normals no longer let RecalculateNormals witness the
                // winding, so verify the authored upward order separately.
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                var upward = 0;
                for (var i = 0; i < triangles.Length; i += 3)
                {
                    var cross = Vector3.Cross(
                        vertices[triangles[i + 1]] - vertices[triangles[i]],
                        vertices[triangles[i + 2]] - vertices[triangles[i]]);
                    if (cross.y > 0f)
                    {
                        upward++;
                    }
                }

                Assert.AreEqual(triangles.Length / 3, upward,
                    "the rebuilt landscape is wound inside out");
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void RenderPipelines_KeepTheirShadowQualityBudgets()
        {
            AssertShadowPipeline(
                "Assets/Settings/Mobile_RPAsset.asset",
                55f,
                2,
                1024,
                1,
                0.35f,
                0.6f);
            AssertShadowPipeline(
                "Assets/Settings/PC_RPAsset.asset",
                100f,
                4,
                2048,
                3,
                0.1f,
                0.5f);
        }

        private static void AssertShadowPipeline(
            string path,
            float distance,
            int cascades,
            int resolution,
            int softShadowQuality,
            float depthBias,
            float normalBias)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            Assert.IsNotNull(asset, path);
            var settings = new SerializedObject(asset);
            Assert.AreEqual(distance,
                settings.FindProperty("m_ShadowDistance").floatValue, 0.001f, path);
            Assert.AreEqual(cascades,
                settings.FindProperty("m_ShadowCascadeCount").intValue, path);
            Assert.AreEqual(resolution,
                settings.FindProperty("m_MainLightShadowmapResolution").intValue, path);
            Assert.IsTrue(
                settings.FindProperty("m_MainLightShadowsSupported").boolValue, path);
            Assert.IsTrue(settings.FindProperty("m_SoftShadowsSupported").boolValue, path);
            Assert.AreEqual(softShadowQuality,
                settings.FindProperty("m_SoftShadowQuality").intValue, path);
            Assert.AreEqual(depthBias,
                settings.FindProperty("m_ShadowDepthBias").floatValue, 0.001f, path);
            Assert.AreEqual(normalBias,
                settings.FindProperty("m_ShadowNormalBias").floatValue, 0.001f, path);
        }
    }
}
