using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Lighting;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class MapObjectsTests
    {
        /// <summary>The exported root counts are directly countable in the source scene.</summary>
        [Test]
        public void ObjectTable_MatchesTheSourceScene()
        {
            var table = BattleObjects.LoadTable(BattleWorld.DefaultMapId);

            Assert.AreEqual("rudniki", table.MapId);
            Assert.AreEqual(713, table.Objects.Count);
            Assert.AreEqual(224, table.CountOf(MapObjectKind.Static));
            Assert.AreEqual(251, table.CountOf(MapObjectKind.Destructible));
            Assert.AreEqual(121, table.CountOf(MapObjectKind.Tree));
            Assert.AreEqual(106, table.CountOf(MapObjectKind.Vegetation));
            Assert.AreEqual(1, table.CountOf(MapObjectKind.Water));
            Assert.AreEqual(10, table.CountOf(MapObjectKind.Decoration));
            Assert.AreEqual(0, table.Skipped.Count);

            var navigationObstacles = 0;
            var aiNoHide = 0;
            var blockedNavigationObstacles = 0;
            for (var i = 0; i < table.Objects.Count; i++)
            {
                var instance = table.Objects[i];
                if (instance.NavigationObstacle) navigationObstacles++;
                if (instance.AiNoHide) aiNoHide++;
                if (instance.NavigationObstacle && instance.AiNoHide)
                {
                    blockedNavigationObstacles++;
                }
            }

            Assert.AreEqual(224, navigationObstacles);
            Assert.AreEqual(43, aiNoHide);
            Assert.AreEqual(42, blockedNavigationObstacles);
            Assert.AreEqual(182, navigationObstacles - blockedNavigationObstacles);

            Assert.AreEqual(37, table.Materials.Count);
            Assert.AreEqual(7, table.MultiMeshes.Count);
            Assert.AreEqual("Maps/Rudniki/object_meshes", table.Meshes.Resource);
            Assert.AreEqual(66, table.Meshes.Count);
            Assert.AreEqual(48596, table.Meshes.Vertices);
            Assert.AreEqual(24768, table.Meshes.Triangles);
            Assert.AreEqual(1855618, table.Meshes.Bytes);
            Assert.AreEqual("Maps/Rudniki/object_shapes", table.Shapes.Resource);
            Assert.AreEqual(30, table.Shapes.Count);
            Assert.AreEqual(11025, table.Shapes.Vertices);
            Assert.AreEqual(3675, table.Shapes.Triangles);
            Assert.AreEqual(133144, table.Shapes.Bytes);

            Assert.AreEqual(1, table.Layers["static"]);
            Assert.AreEqual(2, table.Layers["physics"]);
            Assert.AreEqual(256, table.Layers["destructible"]);
            Assert.AreEqual(32, table.Layers["vegetation"]);
        }

        [Test]
        public void ObjectLibraries_MatchTheManifest()
        {
            var table = BattleObjects.LoadTable(BattleWorld.DefaultMapId);
            var meshes = BattleObjects.LoadMeshes(table);
            var shapes = BattleObjects.LoadShapes(table);

            Assert.AreEqual(table.Meshes.Count, meshes.Count);
            Assert.AreEqual(table.Meshes.Vertices, meshes.VertexCount);
            Assert.AreEqual(table.Meshes.Triangles, meshes.TriangleCount);
            Assert.AreEqual(table.Shapes.Count, shapes.Count);
            Assert.AreEqual(table.Shapes.Vertices, shapes.VertexCount);
            Assert.AreEqual(table.Shapes.Triangles, shapes.TriangleCount);

            var water = meshes.Get("rudniki_bulk_1_seaSurface_sc2_batch_0");
            Assert.AreEqual(176, water.VertexCount);
            Assert.AreEqual(300, water.TriangleCount);
            Assert.AreEqual(water.VertexCount * 3, water.Normals.Length);
            Assert.AreEqual(water.VertexCount * 2, water.Uv.Length);

            var trunk = meshes.Get("tree_trunk_Cylinder");
            Assert.AreEqual(18, trunk.VertexCount);
            Assert.AreEqual(18, trunk.TriangleCount);
        }

        [Test]
        public void ObjectTable_ResolvesEveryReference()
        {
            var table = BattleObjects.LoadTable(BattleWorld.DefaultMapId);
            var meshes = BattleObjects.LoadMeshes(table);
            var shapes = BattleObjects.LoadShapes(table);
            var primitiveCount = 0;
            var triggerCount = 0;
            var destructiblesWithoutCollider = 0;

            for (var i = 0; i < table.Objects.Count; i++)
            {
                var instance = table.Objects[i];
                var name = instance.Name;
                Assert.AreEqual(3, instance.Origin.Length);
                Assert.AreEqual(9, instance.Basis.Length);

                if (instance.IsTree || instance.IsVegetation)
                {
                    Assert.IsNull(instance.Mesh, name + " should use nested parts");
                    Assert.Greater(instance.Parts.Count, 0,
                        name + " lost all visual parts");
                    for (var partIndex = 0; partIndex < instance.Parts.Count; partIndex++)
                    {
                        var part = instance.Parts[partIndex];
                        Assert.IsTrue(table.Materials.ContainsKey(part.Material),
                            name + " is missing a part material");
                        Assert.AreNotEqual(part.Mesh != null, part.MultiMesh != null,
                            name + " must use exactly one part geometry source");
                        Assert.IsTrue(
                            part.Mesh != null ? meshes.Has(part.Mesh) :
                                table.MultiMeshes.ContainsKey(part.MultiMesh),
                            name + " is missing part geometry");
                    }

                    primitiveCount += instance.Colliders.Count;
                    for (var colliderIndex = 0;
                        colliderIndex < instance.Colliders.Count; colliderIndex++)
                    {
                        var collider = instance.Colliders[colliderIndex];
                        Assert.AreEqual(9, collider.Basis.Length);
                        Assert.AreEqual(3, collider.Origin.Length);
                        if (collider.IsTrigger) triggerCount++;
                    }

                    if (instance.IsTree)
                    {
                        Assert.AreEqual(0.2f, instance.FallSpeed, 0.0001f);
                        var tankSensors = 0;
                        for (var colliderIndex = 0;
                            colliderIndex < instance.Colliders.Count; colliderIndex++)
                        {
                            var collider = instance.Colliders[colliderIndex];
                            if (collider.IsTrigger)
                            {
                                tankSensors++;
                                Assert.AreEqual(0, collider.CollisionLayer,
                                    name + " sensor must stay layerless in Godot");
                                Assert.AreEqual(2, collider.CollisionMask,
                                    name + " sensor must watch the tank body layer");
                            }
                            else
                            {
                                Assert.AreEqual(32, collider.CollisionLayer,
                                    name + " leaf shape lost the foliage layer");
                                Assert.AreEqual(0, collider.CollisionMask,
                                    name + " leaf shape must not stop a tank");
                            }
                        }
                        Assert.AreEqual(1, tankSensors,
                            name + " needs exactly one all-sided tank sensor");
                    }
                    else
                    {
                        Assert.GreaterOrEqual(instance.Colliders.Count, 2,
                            name + " lost its foliage collision");
                        for (var colliderIndex = 0;
                            colliderIndex < instance.Colliders.Count; colliderIndex++)
                        {
                            var collider = instance.Colliders[colliderIndex];
                            Assert.IsFalse(collider.IsTrigger, name);
                            Assert.AreEqual(32, collider.CollisionLayer, name);
                            Assert.AreEqual(0, collider.CollisionMask,
                                name + " bush must not stop a tank");
                        }
                    }

                    continue;
                }

                Assert.IsTrue(meshes.Has(instance.Mesh), name + " is missing its mesh");
                Assert.IsTrue(table.Materials.ContainsKey(instance.Material),
                    name + " is missing its material");
                if (instance.Shape != null)
                {
                    Assert.IsTrue(shapes.Has(instance.Shape), name + " is missing its shape");
                }

                if (!instance.IsDestructible)
                {
                    Assert.IsNull(instance.MeshDestroyed, name + " is static but has a wreck");
                    Assert.AreEqual(0, instance.Health, name + " is static but has health");
                    continue;
                }

                Assert.IsTrue(meshes.Has(instance.MeshDestroyed),
                    name + " is missing its wreck mesh");
                if (instance.Shape == null) destructiblesWithoutCollider++;
                Assert.Greater(instance.Health, 0, name + " starts dead");
                if (instance.ShapeDestroyed != null)
                {
                    Assert.IsTrue(shapes.Has(instance.ShapeDestroyed),
                        name + " is missing its wreck shape");
                }
            }

            // Rudniki carries 121 tank triggers and 370 physical foliage shapes.
            Assert.AreEqual(491, primitiveCount);
            Assert.AreEqual(121, triggerCount);
            // Ten source wreckables deliberately have no gameplay shape.
            Assert.AreEqual(10, destructiblesWithoutCollider);
        }

        [Test]
        public void MultiMeshes_KeepSharedSourceTransforms()
        {
            var table = BattleObjects.LoadTable(BattleWorld.DefaultMapId);
            MapMultiMesh bush = null;
            MapMultiMesh tree = null;
            var sourceQuads = 0;
            foreach (var item in table.MultiMeshes.Values)
            {
                if (item.Source.EndsWith("bush01.multimesh")) bush = item;
                if (item.Source.EndsWith("tree_03_leaves.multimesh")) tree = item;
                sourceQuads += item.Count;
            }

            Assert.IsNotNull(bush);
            Assert.IsNotNull(tree);
            Assert.AreEqual(35, bush.Count);
            Assert.AreEqual(162, tree.Count);
            Assert.AreEqual(480, sourceQuads);
            Assert.AreEqual(4f, bush.Transforms[0].Basis[0], 0.000001f);
            Assert.AreEqual(-0.073715955f, bush.Transforms[0].Origin[0], 0.000001f);
            Assert.AreEqual(1.23484004f, bush.Transforms[0].Origin[1], 0.000001f);
            Assert.AreEqual(-0.063425511f, bush.Transforms[0].Origin[2], 0.000001f);

            var mesh = BattleObjects.BuildBillboardMesh(bush);
            try
            {
                Assert.AreEqual(140, mesh.vertexCount);
                Assert.AreEqual(70, mesh.triangles.Length / 3);
                var centres = new List<Vector3>();
                mesh.GetUVs(1, centres);
                Assert.AreEqual(140, centres.Count);
                Assert.AreEqual(-0.073715955f, centres[0].x, 0.000001f);
                Assert.AreEqual(1.23484004f, centres[0].y, 0.000001f);
                Assert.AreEqual(0.063425511f, centres[0].z, 0.000001f);
                Assert.AreEqual(centres[0], centres[3]);
                Assert.AreNotEqual(centres[0], centres[4]);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void WaterMaterial_KeepsSourceShaderParameters()
        {
            var table = BattleObjects.LoadTable(BattleWorld.DefaultMapId);
            var waterCount = 0;
            foreach (var source in table.Materials.Values)
            {
                if (!source.IsWater) continue;
                waterCount++;
                Assert.AreEqual("water", source.Shader);
                Assert.AreEqual("Maps/Rudniki/Objects/Water/w", source.Texture);
                Assert.AreEqual(0.007f, source.Shift1[0], 0.000001f);
                Assert.AreEqual(0.007f, source.Shift1[1], 0.000001f);
                Assert.AreEqual(0.04f, source.Shift2[0], 0.000001f);
                Assert.AreEqual(0.04f, source.Shift2[1], 0.000001f);
            }

            Assert.AreEqual(1, waterCount);
        }

        [Test]
        public void VegetationMaterials_KeepSourceCutoutAndSway()
        {
            var table = BattleObjects.LoadTable(BattleWorld.DefaultMapId);
            var vegetation = 0;
            var billboard = 0;
            var fixedLeaves = 0;
            foreach (var source in table.Materials.Values)
            {
                if (!source.IsVegetation) continue;
                vegetation++;
                Assert.AreEqual(0.5f, source.AlphaClip, 0.0001f);
                Assert.Greater(source.SwayAmount, 0f);
                Assert.Greater(source.SwayDuration, 0f);
                if (source.Billboard) billboard++;
                else fixedLeaves++;
            }

            Assert.AreEqual(7, vegetation);
            Assert.AreEqual(7, billboard);
            Assert.AreEqual(0, fixedLeaves);
            var shader = Shader.Find(BattleObjects.VegetationShaderName);
            Assert.IsNotNull(shader);
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader),
                "the vegetation shader has a compile error");
        }

        [Test]
        public void VegetationTextures_KeepAlphaDetailOnAndroid()
        {
            AssertCutoutTexture("vegetation_bush01");
            AssertCutoutTexture("vegetation_bush02");
            AssertCutoutTexture("vegetation_tree_02_2");
            AssertCutoutTexture("vegetation_tree_K4_1");
        }

        private static void AssertCutoutTexture(string stem)
        {
            var path = "Assets/Blitz/Resources/Maps/Rudniki/Objects/Vegetation/" +
                stem + ".png";
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            Assert.IsNotNull(importer, path);
            Assert.IsTrue(importer.sRGBTexture, path);
            Assert.IsTrue(importer.mipmapEnabled, path);
            Assert.IsTrue(importer.alphaIsTransparency, path);
            Assert.AreEqual(FilterMode.Trilinear, importer.filterMode, path);
            Assert.AreEqual(4, importer.anisoLevel, path);
            var android = importer.GetPlatformTextureSettings("Android");
            Assert.IsTrue(android.overridden, path);
            Assert.AreEqual(TextureImporterFormat.ASTC_4x4, android.format, path);
        }

        [Test]
        public void ObjectMeshes_KeepTheirSourceWinding()
        {
            var table = BattleObjects.LoadTable(BattleWorld.DefaultMapId);
            var library = BattleObjects.LoadMeshes(table);

            var agree = 0;
            var disagree = 0;
            foreach (var source in library.Meshes)
            {
                var mesh = BattleObjects.BuildMesh(source);
                try
                {
                    var vertices = mesh.vertices;
                    var normals = mesh.normals;
                    var triangles = mesh.triangles;
                    for (var i = 0; i < triangles.Length; i += 3)
                    {
                        var a = triangles[i];
                        var b = triangles[i + 1];
                        var c = triangles[i + 2];
                        var wound = Vector3.Cross(
                            vertices[b] - vertices[a], vertices[c] - vertices[a]);
                        if (wound.sqrMagnitude < 1e-12f) continue;
                        var authored = normals[a] + normals[b] + normals[c];
                        if (Vector3.Dot(wound, authored) > 0f) agree++;
                        else disagree++;
                    }
                }
                finally
                {
                    Object.DestroyImmediate(mesh);
                }
            }

            var total = agree + disagree;
            Assert.Greater(total, 24000, "the object meshes did not load");
            Assert.Greater(agree / (float)total, 0.95f,
                "the object triangles are wound inside out");
        }

        [Test]
        public void MapObjects_FillTheMap()
        {
            GameObject root;
            var objects = BuildAll(out root);

            try
            {
                Assert.AreEqual(713, objects.InstanceCount);
                Assert.AreEqual(871, objects.RendererCount);
                Assert.IsTrue(objects.UsesRealtimeLighting);
                Assert.AreEqual(0, objects.LightmappedCount,
                    "the battle must not retain Godot's baked shadows");
                Assert.AreEqual(1160, objects.ColliderCount);
                Assert.AreEqual(362, objects.TriggerCount);
                Assert.AreEqual(251, objects.DestructibleCount);
                Assert.AreEqual(121, objects.TreeCount);
                Assert.AreEqual(106, objects.VegetationCount);
                Assert.AreEqual(1, objects.WaterCount);
                Assert.AreEqual(10, objects.DecorationCount);
                Assert.AreEqual(264, objects.MultiMeshRendererCount);
                Assert.AreEqual(18476, objects.BillboardQuadCount);
                Assert.AreEqual(182806, objects.TriangleCount);
                Assert.IsTrue(Physics.GetIgnoreLayerCollision(
                    BattleLayers.Physics, BattleLayers.Foliage),
                    "Godot layer-6 foliage has mask 0 and must not stop a tank");
                Assert.IsTrue(Physics.GetIgnoreLayerCollision(
                    BattleLayers.Physics, BattleLayers.Destructibles));

                var ramTriggers = objects.Root
                    .GetComponentsInChildren<BattleDestructibleTrigger>();
                Assert.AreEqual(241, ramTriggers.Length);
                var colliderless = 0;
                var destructibles = objects.Root
                    .GetComponentsInChildren<BattleDestructible>();
                for (var i = 0; i < destructibles.Length; i++)
                {
                    if (destructibles[i]
                            .GetComponentInChildren<BattleDestructibleTrigger>() == null)
                    {
                        colliderless++;
                    }
                }
                Assert.AreEqual(10, colliderless,
                    "colliderless source props must not gain invented ram areas");

                var renderers = objects.Root.GetComponentsInChildren<MeshRenderer>();
                Assert.AreEqual(objects.RendererCount, renderers.Length);
                var shadowless = 0;
                var noShadowReceive = 0;
                var waterRenderers = 0;
                for (var i = 0; i < renderers.Length; i++)
                {
                    var renderer = renderers[i];
                    if (renderer.shadowCastingMode ==
                        UnityEngine.Rendering.ShadowCastingMode.Off) shadowless++;
                    if (!renderer.receiveShadows) noShadowReceive++;
                    Assert.IsTrue(renderer.sharedMaterial.enableInstancing, renderer.name);
                    Assert.AreNotEqual(BakedLighting.ShaderName,
                        renderer.sharedMaterial.shader.name, renderer.name);
                    var shaderName = renderer.sharedMaterial.shader.name;
                    if (shaderName == BattleObjects.WaterShaderName) waterRenderers++;
                    Assert.IsTrue(
                        shaderName == BattleObjects.LitShaderName ||
                        shaderName == BattleObjects.VegetationShaderName ||
                        shaderName == BattleObjects.WaterShaderName,
                        renderer.name + " uses an unknown map shader: " + shaderName);
                }

                Assert.AreEqual(11, shadowless,
                    "only water and the ten backdrop meshes skip realtime shadows");
                Assert.AreEqual(10, noShadowReceive,
                    "only the distant backdrop ignores gameplay shadows");
                Assert.AreEqual(1, waterRenderers);

                var building = objects.Root.transform.Find("bl_03_01");
                Assert.IsNotNull(building, "bl_03_01 was not placed");
                Assert.AreEqual(490.93536f, building.localPosition.x, 0.001f);
                Assert.AreEqual(57.269264f, building.localPosition.y, 0.001f);
                Assert.AreEqual(-162.12024f, building.localPosition.z, 0.001f);

                var scaled = objects.Root.transform.Find("bld6111_01");
                var twin = objects.Root.transform.Find("bld6111_02");
                Assert.AreEqual(0.8142857f, scaled.localScale.x, 0.0001f);
                Assert.AreSame(scaled.GetComponent<MeshFilter>().sharedMesh,
                    twin.GetComponent<MeshFilter>().sharedMesh);

                var bush = objects.Root.transform.Find("bush02_low");
                var bushTwin = objects.Root.transform.Find("bush02_low2");
                Assert.IsNotNull(bush.GetComponentInChildren<MeshRenderer>());
                Assert.AreSame(
                    bush.GetComponentInChildren<MeshFilter>().sharedMesh,
                    bushTwin.GetComponentInChildren<MeshFilter>().sharedMesh);
                Assert.AreEqual(2, bush.GetComponentsInChildren<CapsuleCollider>().Length);
                Assert.AreEqual(1, bush.GetComponentsInChildren<SphereCollider>().Length);
                var bushColliders = bush.GetComponentsInChildren<Collider>();
                for (var i = 0; i < bushColliders.Length; i++)
                {
                    Assert.AreEqual(BattleLayers.Foliage,
                        bushColliders[i].gameObject.layer, bushColliders[i].name);
                    Assert.IsFalse(bushColliders[i].isTrigger, bushColliders[i].name);
                }

                var tree = objects.Root.transform.Find("tree_02_01");
                Assert.IsNotNull(tree.GetComponent<BattleTree>());
                Assert.AreEqual(2, tree.GetComponentsInChildren<MeshRenderer>().Length);
                Assert.AreEqual(2, tree.GetComponentsInChildren<CapsuleCollider>().Length);
                var treeSensors = tree.GetComponentsInChildren<BattleTreeTrigger>();
                Assert.AreEqual(1, treeSensors.Length);
                var sensor = treeSensors[0].GetComponent<Collider>();
                Assert.IsNotNull(sensor);
                Assert.IsTrue(sensor.isTrigger);
                Assert.AreEqual(BattleLayers.Physics, sensor.gameObject.layer,
                    "a tree's layerless mask-2 Area3D must still see tank bodies");

                var allTrees = objects.Root.GetComponentsInChildren<BattleTree>();
                Assert.AreEqual(121, allTrees.Length);
                for (var i = 0; i < allTrees.Length; i++)
                {
                    var sensors = allTrees[i]
                        .GetComponentsInChildren<BattleTreeTrigger>();
                    Assert.AreEqual(1, sensors.Length, allTrees[i].name);
                    Assert.AreEqual(BattleLayers.Physics,
                        sensors[0].gameObject.layer, allTrees[i].name);
                    var colliders = allTrees[i].GetComponentsInChildren<Collider>();
                    for (var colliderIndex = 0;
                        colliderIndex < colliders.Length; colliderIndex++)
                    {
                        if (colliders[colliderIndex].isTrigger) { continue; }
                        Assert.AreEqual(BattleLayers.Foliage,
                            colliders[colliderIndex].gameObject.layer,
                            allTrees[i].name);
                    }
                }
            }
            finally
            {
                objects.Release();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void EveryTreeFamilyFallsFromAllFourTankApproaches()
        {
            GameObject root;
            var objects = BuildAll(out root);
            var families = new[]
            {
                "electro_pole",
                "tree_02",
                "tree_03",
                "tree_k4",
            };
            var awayDirections = new[]
            {
                Vector3.forward,
                Vector3.right,
                Vector3.back,
                Vector3.left,
            };

            try
            {
                for (var familyIndex = 0;
                    familyIndex < families.Length; familyIndex++)
                {
                    for (var directionIndex = 0;
                        directionIndex < awayDirections.Length; directionIndex++)
                    {
                        var objectName = string.Format(
                            "{0}_{1:00}", families[familyIndex], directionIndex + 1);
                        var treeRoot = objects.Root.transform.Find(objectName);
                        Assert.IsNotNull(treeRoot, objectName);
                        var tree = treeRoot.GetComponent<BattleTree>();
                        var sensor = treeRoot.GetComponentInChildren<BattleTreeTrigger>();
                        Assert.IsNotNull(tree, objectName);
                        Assert.IsNotNull(sensor, objectName);

                        var tankObject = new GameObject(objectName + "_ram_test");
                        try
                        {
                            tankObject.layer = BattleLayers.Physics;
                            tankObject.transform.position = treeRoot.position -
                                awayDirections[directionIndex] * 2f;
                            var body = tankObject.AddComponent<Rigidbody>();
                            body.useGravity = false;
                            tankObject.AddComponent<BattleTank>();
                            var hull = tankObject.AddComponent<BoxCollider>();

                            Assert.IsTrue(sensor.TryEnter(hull),
                                objectName + " ignored a tank approach");
                            Assert.IsTrue(tree.Falling,
                                objectName + " never started falling");
                            tree.Advance(tree.Duration + 0.1f);
                            var horizontal = Vector3.ProjectOnPlane(
                                treeRoot.Find("trunk").up, Vector3.up).normalized;
                            Assert.Greater(
                                Vector3.Dot(horizontal, awayDirections[directionIndex]),
                                0.99f,
                                objectName + " fell toward the tank");
                        }
                        finally
                        {
                            Object.DestroyImmediate(tankObject);
                        }
                    }
                }
            }
            finally
            {
                objects.Release();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Trees_FallOnceAwayFromTheTank()
        {
            GameObject root;
            var objects = BuildAll(out root);
            try
            {
                var treeRoot = objects.Root.transform.Find("tree_02_01");
                var tree = treeRoot.GetComponent<BattleTree>();
                var trunk = treeRoot.Find("trunk");
                var away = treeRoot.forward;
                var impact = treeRoot.position - away * 2f;

                Assert.IsTrue(tree.FallFrom(impact));
                Assert.IsTrue(tree.Falling);
                Assert.AreEqual(3.27249f, tree.Duration, 0.001f);
                tree.Advance(tree.Duration + 0.1f);
                Assert.IsTrue(tree.Fallen);
                Assert.IsFalse(tree.Falling);
                Assert.AreEqual(Mathf.Cos(75f * Mathf.Deg2Rad),
                    Vector3.Dot(trunk.up, Vector3.up), 0.001f);
                Assert.Greater(Vector3.Dot(Vector3.ProjectOnPlane(trunk.up, Vector3.up), away),
                    0.9f);
                Assert.IsFalse(tree.FallFrom(impact), "a fallen tree fell twice");
            }
            finally
            {
                objects.Release();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Destructibles_SwapToTheirWreck()
        {
            GameObject root;
            var objects = BuildAll(out root);

            try
            {
                var shed = objects.Root.transform.Find("bld6111_01")
                    .GetComponent<BattleDestructible>();
                var filter = shed.GetComponent<MeshFilter>();
                var collider = shed.GetComponent<MeshCollider>();

                Assert.AreEqual(10, shed.Health);
                Assert.IsFalse(shed.Damage(4));
                Assert.AreEqual(6, shed.Health);
                Assert.IsTrue(shed.Damage(6));
                Assert.IsTrue(shed.Destroyed);
                Assert.AreEqual("rudniki_destroyed_r_bld6111_sc2_lod2_007",
                    filter.sharedMesh.name);
                Assert.IsFalse(collider.enabled);
                Assert.IsFalse(shed.Damage(10));

                var rammed = objects.Root.transform.Find("bld6111_02")
                    .GetComponent<BattleDestructible>();
                var ramCollider = rammed.GetComponent<MeshCollider>();
                var trigger = rammed.GetComponentInChildren<BattleDestructibleTrigger>();
                Assert.IsNotNull(trigger);
                Assert.IsTrue(trigger.GetComponent<MeshCollider>().isTrigger);
                Assert.AreEqual(BattleLayers.Physics, trigger.gameObject.layer);
                Assert.IsTrue(Physics.GetIgnoreLayerCollision(
                    BattleLayers.Physics, BattleLayers.Destructibles));

                var tankObject = new GameObject("RamTank");
                try
                {
                    tankObject.layer = BattleLayers.Physics;
                    tankObject.transform.position = rammed.transform.position + Vector3.right;
                    var body = tankObject.AddComponent<Rigidbody>();
                    body.mass = 5f;
                    body.useGravity = false;
                    var tank = tankObject.AddComponent<BattleTank>();
                    body.useGravity = false;

                    var impulse = BattleDestructible.RamImpulse(rammed.Health, body.mass);
                    Assert.AreEqual(5f, impulse, 0.0001f);
                    Assert.AreEqual(new Vector3(0.5f, 0f, 0f),
                        BattleDestructible.RamForce(Vector3.right, impulse));
                    Assert.AreEqual(new Vector3(0f, 0f, -0.25f),
                        BattleDestructible.RamTorque(Vector3.right, impulse));

                    Assert.IsTrue(rammed.Ram(tank));
                    Assert.IsTrue(rammed.Destroyed);
                    Assert.IsFalse(ramCollider.enabled);
                    Assert.IsFalse(rammed.Ram(tank), "a rammed prop broke twice");
                }
                finally
                {
                    Object.DestroyImmediate(tankObject);
                }
            }
            finally
            {
                objects.Release();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Props_StandOnTheLandscape()
        {
            var map = BattleWorld.LoadDefinition(BattleWorld.DefaultMapId);
            var field = BattleWorld.LoadHeights(map);
            var table = BattleObjects.LoadTable(BattleWorld.DefaultMapId);

            var grounded = 0;
            var deltas = new List<float>();
            for (var i = 0; i < table.Objects.Count; i++)
            {
                var origin = table.Objects[i].Origin;
                var delta = origin[1] - field.SampleHeight(origin[0], origin[2]);
                deltas.Add(delta);
                if (delta > -1f && delta < 1f) grounded++;
            }

            deltas.Sort();
            Assert.AreEqual(0f, deltas[deltas.Count / 2], 0.25f,
                "the props hover over the map");
            Assert.Greater(grounded / (float)table.Objects.Count, 0.85f,
                "the props stopped following the ground");
        }

        private BattleObjects BuildAll(out GameObject root)
        {
            var table = BattleObjects.LoadTable(BattleWorld.DefaultMapId);
            var meshes = BattleObjects.LoadMeshes(table);
            var shapes = BattleObjects.LoadShapes(table);
            root = new GameObject("MapObjectsTests");
            return BattleObjects.Build(
                root.transform,
                table,
                meshes,
                shapes,
                null,
                useBakedLighting: false);
        }
    }
}
