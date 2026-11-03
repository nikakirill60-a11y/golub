using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Lighting;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Rudniki is re-ported straight from the live Godot scene
    /// (Tools/godot_map_scene_dump.gd + Tools/port_godot_map_scene.py) into the
    /// same binary BLTZMAP1 standard as Mirage: every GeometryInstance3D is a
    /// layout node in Unity axes, drawn by a shader that follows Godot 4.7's
    /// mobile renderer, with its LightmapGI slice. These numbers are the ones
    /// the porter prints for _rudniki.tscn.
    /// </summary>
    public sealed class RudnikiMapTests
    {
        private const string MapId = "rudniki";
        private const string SceneResource = "Maps/Rudniki/rudniki_scene";
        private const string TextureFolder = "Assets/Blitz/Resources/Maps/Rudniki/Textures";

        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        [Test]
        public void Manifest_DeclaresTheGodotSceneStandard()
        {
            var map = BattleWorld.LoadDefinition(MapId);

            Assert.IsTrue(map.GodotScene, "sceneFormat has to be godot");
            Assert.IsFalse(map.ClientScene);
            Assert.AreEqual(SceneResource, map.ExternalScene);
            Assert.IsTrue(ClientMapScene.Exists(SceneResource));
            CollectionAssert.AreEqual(new[] { 0f, 0f, 0f }, map.Terrain.Origin);
            Assert.AreEqual(0, map.SkyFaces.Count);
        }

        [Test]
        public void Shaders_CompileWithoutErrors()
        {
            var names = new List<string>(ClientMapScene.GodotShaders)
            {
                SourceAdjustments.ShaderName,
            };
            for (var i = 0; i < names.Count; i++)
            {
                var shader = Shader.Find(names[i]);
                Assert.IsNotNull(shader, names[i]);
                Assert.IsFalse(ShaderUtil.ShaderHasError(shader), names[i]);
                Assert.IsTrue(shader.isSupported, names[i]);
            }
        }

        /// <summary>
        /// The porter names every texture after the Godot sampler that reads
        /// it, and the importer turns that into the same decoding, mip chain
        /// and compression Godot uses.
        /// </summary>
        [Test]
        public void SceneTextures_FollowTheGodotSamplers()
        {
            var guids = AssetDatabase.FindAssets("t:Texture", new[] { TextureFolder });
            var lightmaps = 0;
            var lossless = 0;
            for (var i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var name = Path.GetFileNameWithoutExtension(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                Assert.IsTrue(path.EndsWith("_hdr.exr") || name.EndsWith("_srgb") ||
                    name.EndsWith("_raw") || name.EndsWith("_raw_nomip"),
                    path + " carries no Godot sampler mark");
                Assert.AreEqual(1, importer.anisoLevel, path);
                Assert.AreEqual(name.EndsWith("_srgb"), importer.sRGBTexture, path);

                if (path.EndsWith("_hdr.exr"))
                {
                    lightmaps++;
                    Assert.IsFalse(importer.mipmapEnabled, path);
                    Assert.AreEqual(TextureWrapMode.Clamp, importer.wrapMode, path);
                    Assert.AreEqual(TextureImporterFormat.RGBAHalf,
                        importer.GetPlatformTextureSettings("Android").format, path);
                    continue;
                }

                if (name.EndsWith("_nomip"))
                {
                    Assert.IsFalse(importer.mipmapEnabled, path);
                }
                else
                {
                    Assert.IsTrue(importer.mipmapEnabled, path);
                }

                if (name.Contains("_lossless_"))
                {
                    lossless++;
                    Assert.AreEqual(TextureImporterCompression.Uncompressed,
                        importer.textureCompression, path);
                }
            }

            // _rudniki.lmbake: a 4 x 4 layered atlas.
            Assert.AreEqual(16, lightmaps);
            Assert.GreaterOrEqual(lossless, 1, "TileTxRudniki is imported lossless in Godot");
        }

        [Test]
        public void GodotScene_BuildsEveryNodeAsGodotDrawsIt()
        {
            _root = new GameObject("rudniki-scene-test");
            var scene = ClientMapScene.Build(_root.transform, SceneResource);

            Assert.AreEqual(1503, scene.Layout.nodes.Length);
            Assert.AreEqual(1503, scene.NodeCount);
            Assert.AreEqual(478, scene.LightmappedCount, "LightmapGI users of the bake");
            Assert.Greater(scene.TriangleCount, 200000);

            var godotShaders = new HashSet<string>(ClientMapScene.GodotShaders);
            var roles = new Dictionary<string, int>();
            for (var i = 0; i < scene.Nodes.Count; i++)
            {
                var node = scene.Nodes[i];
                int count;
                roles.TryGetValue(node.Info.role, out count);
                roles[node.Info.role] = count + 1;

                var renderer = node.GetComponent<MeshRenderer>();
                Assert.IsNotNull(renderer, node.name);
                // The Godot sun is static-baked with shadow_enabled = false.
                Assert.AreEqual(ShadowCastingMode.Off, renderer.shadowCastingMode, node.name);
                Assert.AreEqual(!node.Info.hidden, renderer.enabled, node.name);
                Assert.IsNull(node.CollisionMesh, node.name + ": collision comes from the table");
                for (var m = 0; m < renderer.sharedMaterials.Length; m++)
                {
                    var material = renderer.sharedMaterials[m];
                    Assert.IsNotNull(material, node.name);
                    Assert.IsTrue(godotShaders.Contains(material.shader.name),
                        node.name + " draws with " + material.shader.name);
                }
            }

            Assert.AreEqual(5, roles[ClientMapScene.SkyRole]);
            Assert.AreEqual(1, roles["water"]);
            Assert.AreEqual(251, roles["intact"]);
            Assert.AreEqual(251, roles["wreck"]);
            Assert.AreEqual(121, roles["trunk"]);
            Assert.AreEqual(376, roles["lod"]);
        }

        /// <summary>
        /// The alignment standard: every node that stands on the ground has
        /// its foot on the drivable height field (rocks sink into it as in
        /// Godot), and the scene is not mirrored across an axis.
        /// </summary>
        [Test]
        public void SceneProps_StandOnTheLandscape()
        {
            var map = BattleWorld.LoadDefinition(MapId);
            var field = BattleWorld.LoadHeights(map);
            var layout = JsonUtility.FromJson<ClientMapLayout>(
                Resources.Load<TextAsset>(SceneResource + "_layout").text);

            var checkedNodes = 0;
            var total = 0f;
            var mirrored = 0f;
            for (var i = 0; i < layout.nodes.Length; i++)
            {
                var node = layout.nodes[i];
                if (node.hidden || (node.role != "static" && node.role != "intact" && node.role != "trunk"))
                {
                    continue;
                }

                // The harbour crane stands on its pier above the sea.
                if (node.name.StartsWith("boatcrane"))
                {
                    continue;
                }

                var x = (node.min[0] + node.max[0]) * 0.5f;
                var z = (node.min[2] + node.max[2]) * 0.5f;
                // Unity z is Godot -z; the field is sampled in Godot axes.
                var ground = field.SampleHeight(x - map.Terrain.Origin[0], -z - map.Terrain.Origin[2]);
                var foot = node.min[1] - ground;
                Assert.Less(foot, 1.5f, node.name + " floats above the landscape");
                total += Mathf.Abs(foot);
                mirrored += Mathf.Abs(node.min[1] - field.SampleHeight(-x, -z));
                checkedNodes++;
            }

            Assert.Greater(checkedNodes, 500);
            Assert.Less(total / checkedNodes, 2f, "mean |foot - ground|");
            Assert.Greater(mirrored / checkedNodes, 5f * total / checkedNodes,
                "a mirrored scene would fit about as well");
        }

        [Test]
        public void World_DrawsPropsFromTheGodotScene()
        {
            _root = new GameObject("rudniki-world-test");
            var world = BattleWorld.Create(_root.transform, MapId);
            var objects = world.Objects;

            Assert.IsNotNull(objects.Scene, "the Godot render scene was not built");
            Assert.AreEqual(478, objects.LightmappedCount);
            Assert.AreEqual(121, objects.TreeCount);
            Assert.AreEqual(ClientMapScene.GodotLandscapeShader,
                world.Landscape.GetComponent<MeshRenderer>().sharedMaterial.shader.name);
            Assert.IsNotNull(world.Sun.GetComponent<BakedSun>());
            Assert.AreEqual(LightShadows.None, world.Sun.shadows);

            // Every gameplay prop of the table took its own scene nodes.
            var unclaimed = 0;
            for (var i = 0; i < objects.Scene.Nodes.Count; i++)
            {
                var node = objects.Scene.Nodes[i];
                var role = node.Info.role;
                if ((role == "intact" || role == "wreck" || role == "trunk") &&
                    node.transform.parent == objects.Scene.transform)
                {
                    unclaimed++;
                }
            }
            Assert.AreEqual(0, unclaimed, "destructible/tree nodes left outside their objects");

            // A tree carries its trunk node, so the trunk falls with it.
            var trees = objects.Root.GetComponentsInChildren<BattleTree>(true);
            Assert.AreEqual(121, trees.Length);
            for (var i = 0; i < trees.Length; i++)
            {
                var hasTrunk = false;
                foreach (var node in trees[i].GetComponentsInChildren<ClientMapNode>(true))
                {
                    hasTrunk |= node.Info.role == "trunk";
                }
                Assert.IsTrue(hasTrunk, trees[i].name);
            }

            // Breaking a prop swaps the intact nodes for the wreck nodes.
            var swapped = false;
            foreach (var destructible in objects.Root.GetComponentsInChildren<BattleDestructible>(true))
            {
                var intact = new List<Renderer>();
                var wreck = new List<Renderer>();
                foreach (var node in destructible.GetComponentsInChildren<ClientMapNode>(true))
                {
                    if (node.Info.role == "intact") intact.Add(node.GetComponent<Renderer>());
                    if (node.Info.role == "wreck") wreck.Add(node.GetComponent<Renderer>());
                }
                if (intact.Count == 0 || wreck.Count == 0)
                {
                    continue;
                }

                Assert.IsTrue(intact.TrueForAll(r => r.enabled), destructible.ObjectName);
                Assert.IsTrue(wreck.TrueForAll(r => !r.enabled), destructible.ObjectName);
                Assert.IsTrue(destructible.Damage(1000000), destructible.ObjectName);
                Assert.IsTrue(intact.TrueForAll(r => !r.enabled), destructible.ObjectName);
                Assert.IsTrue(wreck.TrueForAll(r => r.enabled), destructible.ObjectName);
                swapped = true;
                break;
            }
            Assert.IsTrue(swapped, "no destructible carries intact and wreck scene nodes");
        }

        /// <summary>
        /// map_object_vegetation.gd: a group is far when
        /// distance² · fov > 1000², then the multimesh leaves hide and the
        /// sprite LOD shows.
        /// </summary>
        [Test]
        public void VegetationLods_SwitchAtTheGodotThreshold()
        {
            Assert.IsFalse(BattleVegetationLods.IsFar(Vector3.zero, new Vector3(100f, 0f, 0f), 60f));
            Assert.IsTrue(BattleVegetationLods.IsFar(Vector3.zero, new Vector3(130f, 0f, 0f), 60f));

            _root = new GameObject("rudniki-lod-test");
            var world = BattleWorld.Create(_root.transform, MapId);
            var lods = world.Objects.VegetationLods;
            Assert.IsNotNull(lods);
            Assert.Greater(lods.GroupCount, 100);

            var camera = new GameObject("lod-camera").AddComponent<Camera>();
            camera.transform.SetParent(_root.transform, false);
            camera.fieldOfView = 60f;

            camera.transform.position = new Vector3(300f, 5000f, -300f);
            lods.Refresh(camera);
            Assert.AreEqual(lods.GroupCount, lods.FarCount);

            var spawn = world.Map.TeamA[0].Origin;
            camera.transform.position = new Vector3(spawn[0], spawn[1] + 3f, -spawn[2]);
            lods.Refresh(camera);
            Assert.Greater(lods.FarCount, 0);
            Assert.Less(lods.FarCount, lods.GroupCount);
        }
    }
}
