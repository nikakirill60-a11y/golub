using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Lighting;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The bake is only worth anything if every prop finds its own rect. These
    /// tests pin the two things that would silently mislight the map: the rule
    /// that maps a prop to a lightmap user, and the atlas placement the shader
    /// reads per instance.
    /// </summary>
    public sealed class BakedLightingTests
    {
        [Test]
        public void EveryBakedEntry_BelongsToOneMapObject()
        {
            var lighting = BakedLighting.Load(BattleWorld.DefaultMapId);
            Assert.IsNotNull(lighting, "the Rudniki bake has to load");

            var table = BattleObjects.LoadTable(BattleWorld.DefaultMapId);
            var meshesByPath = new Dictionary<string, string>();
            var kindsByPath = new Dictionary<string, MapObjectKind>();
            for (var i = 0; i < table.Objects.Count; i++)
            {
                var instance = table.Objects[i];
                if (!string.IsNullOrEmpty(instance.BakedPath))
                {
                    meshesByPath[instance.BakedPath] = instance.Mesh;
                    kindsByPath[instance.BakedPath] = instance.Kind;
                }

                // The nine electro poles are falling-tree objects in gameplay,
                // but their static trunk renderer still belongs to the source bake.
                if (!instance.IsTree) continue;
                for (var partIndex = 0; partIndex < instance.Parts.Count; partIndex++)
                {
                    var part = instance.Parts[partIndex];
                    if (string.IsNullOrEmpty(part.Mesh)) continue;
                    var path = "scene/" + instance.Name + "/" + part.Name;
                    meshesByPath[path] = part.Mesh;
                    kindsByPath[path] = instance.Kind;
                }
            }

            var entries = lighting.Definition.Entries;
            Assert.AreEqual(478, entries.Count);

            var statics = 0;
            var destructibles = 0;
            var trees = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                string mesh;
                Assert.IsTrue(
                    meshesByPath.TryGetValue(entry.Path, out mesh),
                    "no map object owns " + entry.Path);

                // The payload keys a surface as mesh#surface, and every baked
                // prop is a single-surface mesh.
                Assert.AreEqual(1, entry.Meshes.Count, entry.Path);
                Assert.AreEqual(mesh + "#0", entry.Meshes[0], entry.Path);

                var kind = kindsByPath[entry.Path];
                if (kind == MapObjectKind.Static) statics++;
                else if (kind == MapObjectKind.Destructible) destructibles++;
                else if (kind == MapObjectKind.Tree) trees++;
            }

            Assert.AreEqual(224, statics);
            Assert.AreEqual(table.CountOf(MapObjectKind.Static), statics);
            Assert.AreEqual(245, destructibles);
            Assert.AreEqual(table.CountOf(MapObjectKind.Destructible) - 6, destructibles);
            Assert.AreEqual(9, trees);
            Assert.AreEqual(0, lighting.Definition.StaleEntries.Count);

            lighting.Release();
        }

        [Test]
        public void LightmappedMesh_CarriesTheUnwrapOnTheSecondChannel()
        {
            var lighting = BakedLighting.Load(BattleWorld.DefaultMapId);
            var entry = lighting.Entry("scene/bl_03_01/mesh_instance");
            var mesh = lighting.MeshFor(entry.Meshes[0]);

            Assert.AreEqual(entry.Meshes[0], mesh.name);
            Assert.Greater(mesh.vertexCount, 0);
            Assert.AreEqual(mesh.vertexCount, mesh.uv.Length);
            Assert.AreEqual(mesh.vertexCount, mesh.uv2.Length);

            var uv2 = mesh.uv2;
            for (var i = 0; i < uv2.Length; i++)
            {
                Assert.That(uv2[i].x, Is.InRange(-0.001f, 1.001f));
                Assert.That(uv2[i].y, Is.InRange(-0.001f, 1.001f));
            }

            // The same mesh is shared by a whole prop family, so asking twice
            // has to hand back one Unity mesh rather than build a second.
            Assert.AreSame(mesh, lighting.MeshFor(entry.Meshes[0]));
            Assert.AreEqual(1, lighting.MeshCount);

            lighting.Release();
        }

        /// <summary>
        /// Godot flips glTF's bottom-up primary V while importing the source
        /// model, but the port copies the PNG bytes into Unity unchanged. The
        /// baked mesh therefore has to undo that flip. The hangar floor makes
        /// the mismatch especially visible: unchanged UVs sample a black part
        /// of Hangar2 over whole triangles. UV2 is different: the atlas
        /// placement already converts its V and must not be flipped twice.
        /// </summary>
        [Test]
        public void LightmappedMesh_ConvertsPrimaryVButKeepsLightmapV()
        {
            var lighting = BakedLighting.Load("hangar");
            try
            {
                var entry = lighting.Entry(
                    "hangars/low/hangar_low_batch/new/4_1_sc2_001");
                Assert.AreEqual("res://maps/hangar_low/2.tres", entry.Material);

                var payload = Resources.Load<TextAsset>(lighting.Definition.Meshes.Resource);
                Assert.IsNotNull(payload, lighting.Definition.Meshes.Resource);
                var source = LightmapMeshLibrary.Parse(payload.bytes).Get(entry.Meshes[0]);
                var mesh = lighting.MeshFor(entry.Meshes[0]);
                var primary = mesh.uv;
                var lightmap = mesh.uv2;

                Assert.AreEqual(source.VertexCount, primary.Length);
                Assert.AreEqual(source.VertexCount, lightmap.Length);
                for (var i = 0; i < source.VertexCount; i++)
                {
                    var component = i * 2;
                    Assert.AreEqual(
                        source.Uv[component], primary[i].x, 1e-6f,
                        "primary U " + i);
                    Assert.AreEqual(
                        1f - source.Uv[component + 1], primary[i].y, 1e-6f,
                        "primary V " + i);
                    Assert.AreEqual(
                        source.Uv2[component], lightmap[i].x, 1e-6f,
                        "lightmap U " + i);
                    Assert.AreEqual(
                        source.Uv2[component + 1], lightmap[i].y, 1e-6f,
                        "lightmap V " + i);
                }
            }
            finally
            {
                lighting.Release();
            }
        }

        [Test]
        public void AtlasMaterial_FoldsInTheSourceExposure()
        {
            var map = BakedLighting.Load(BattleWorld.DefaultMapId);
            var material = map.CreateMaterial("RudnikiBaked", null, Color.white);

            Assert.IsNotNull(material, "the lightmapped shader has to be found");
            Assert.AreEqual(BakedLighting.ShaderName, material.shader.name);
            Assert.AreSame(map.Atlas, material.GetTexture("_Lightmap"));
            Assert.IsTrue(material.enableInstancing);

            // The map Environment leaves tonemap_exposure at 1, WE_low.tres
            // multiplies the hangar by 2.33, and Unity has no such stage, so
            // the shader is where it lands.
            Assert.AreEqual(1f, material.GetFloat("_Exposure"), 0.0001f);
            map.Release();

            var hangar = BakedLighting.Load("hangar");
            var hangarMaterial = hangar.CreateMaterial("HangarBaked", null, Color.white);
            Assert.AreEqual(2.33f, hangarMaterial.GetFloat("_Exposure"), 0.001f);
            hangar.Release();
        }

        [Test]
        public void Bind_PlacesTheRectAndDropsTheRealtimeShadow()
        {
            var lighting = BakedLighting.Load(BattleWorld.DefaultMapId);
            var entry = lighting.Entry("scene/bl_03_01/mesh_instance");
            var go = new GameObject("BakedProp");
            try
            {
                var renderer = go.AddComponent<MeshRenderer>();
                lighting.Bind(renderer, entry);

                Assert.AreEqual(ShadowCastingMode.Off, renderer.shadowCastingMode);
                Assert.IsFalse(renderer.receiveShadows);
                Assert.AreEqual(1, lighting.BoundCount);

                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                var placement = block.GetVector("_LightmapScaleOffset");
                var expected = entry.AtlasScaleOffset;

                Assert.AreEqual(expected[0], placement.x, 1e-6f);
                Assert.AreEqual(expected[1], placement.y, 1e-6f);
                Assert.AreEqual(expected[2], placement.z, 1e-6f);
                Assert.AreEqual(expected[3], placement.w, 1e-6f);

                // Slice zero occupies the first cell of Rudniki's 4x4 atlas.
                Assert.AreEqual(0.11181640625f, placement.x, 1e-6f);
                Assert.AreEqual(-0.2265625f, placement.y, 1e-6f);
                Assert.AreEqual(0.00048828125f, placement.z, 1e-6f);
                Assert.AreEqual(0.9990234375f, placement.w, 1e-6f);
            }
            finally
            {
                Object.DestroyImmediate(go);
                lighting.Release();
            }
        }

        /// <summary>
        /// The sun used to be read from the map export and is now read from the
        /// lighting export. Two dumps of one scene have to agree, or the switch
        /// moved the light.
        /// </summary>
        [Test]
        public void BakeAndMapExport_DescribeTheSameSun()
        {
            var lighting = BakedLighting.Load(BattleWorld.DefaultMapId);
            var map = BattleWorld.LoadDefinition(BattleWorld.DefaultMapId);
            var sun = lighting.Definition.Light(BattleWorld.SunLightPath);

            Assert.IsNotNull(sun);
            Assert.AreEqual(map.Light.Energy, sun.Energy, 0.0001f);
            for (var i = 0; i < 9; i++)
            {
                Assert.AreEqual(map.Light.Basis[i], sun.Basis[i], 1e-6f, "basis " + i);
            }

            for (var i = 0; i < 3; i++)
            {
                Assert.AreEqual(map.Light.Origin[i], sun.Origin[i], 1e-4f, "origin " + i);
            }

            // Baked and disabled in the source. The Unity battle deliberately
            // replaces this metadata with its own realtime shadow tuning.
            Assert.IsTrue(sun.IsStaticBake);
            Assert.IsFalse(sun.ShadowEnabled);
            Assert.AreEqual(0.89f, sun.ShadowOpacity, 0.001f);

            lighting.Release();
        }

        /// <summary>
        /// Each baked instance has to draw with the material the source gave
        /// it. These two sit next to each other in hangar_low_batch and their
        /// names differ by a suffix: 1_0_sc2 draws with 2.tres and
        /// 1_0_sc2_001 with 1.tres. A port that matched on node names had to
        /// keep that list by hand; reading the material off the entry cannot
        /// get it wrong.
        /// </summary>
        [Test]
        public void MaterialFor_DrawsEachInstanceWithItsSourceMaterial()
        {
            var hangar = BakedLighting.Load("hangar");
            try
            {
                var emissive = hangar.Entry("hangars/low/hangar_low_batch/new/1_0_sc2_001");
                var plain = hangar.Entry("hangars/low/hangar_low_batch/new/1_0_sc2");

                var emissiveMaterial = hangar.MaterialFor(emissive);
                var plainMaterial = hangar.MaterialFor(plain);

                Assert.AreEqual(BakedLighting.ShaderName, emissiveMaterial.shader.name);
                Assert.AreNotSame(emissiveMaterial, plainMaterial);
                Assert.AreEqual("Hangar1", emissiveMaterial.GetTexture("_BaseMap").name);
                Assert.AreEqual("Hangar2", plainMaterial.GetTexture("_BaseMap").name);

                // One Unity material per source material, so asking twice hands
                // back the same instance and the whole family still batches.
                Assert.AreSame(emissiveMaterial, hangar.MaterialFor(emissive));
                Assert.AreEqual(2, hangar.MaterialCount);

                // The flag is what gates the emission sample in the source
                // shader, and 2.tres leaves it off.
                Assert.AreEqual(1f, emissiveMaterial.GetFloat("_Emissive"), 0.0001f);
                Assert.IsNotNull(emissiveMaterial.GetTexture("_EmissionMap"));
                Assert.AreEqual(
                    "Hangar1Emission", emissiveMaterial.GetTexture("_EmissionMap").name);
                Assert.AreEqual(0f, plainMaterial.GetFloat("_Emissive"), 0.0001f);

                // Contrast 1 and an identity uv1 transform throughout, so the
                // shader leaves the albedo where the source left it.
                Assert.AreEqual(1f, emissiveMaterial.GetFloat("_Contrast"), 0.0001f);
                Assert.AreEqual(Vector2.one, emissiveMaterial.mainTextureScale);
                Assert.AreEqual(Vector2.zero, emissiveMaterial.mainTextureOffset);
            }
            finally
            {
                hangar.Release();
            }

            // The map's props carry their materials in the object table, so
            // asking the bake for one has to stop rather than guess.
            var map = BakedLighting.Load(BattleWorld.DefaultMapId);
            try
            {
                var prop = map.Entry("scene/bl_03_01/mesh_instance");
                Assert.Throws<System.ArgumentException>(() => map.MaterialFor(prop));
            }
            finally
            {
                map.Release();
            }
        }

        /// <summary>
        /// Sampling the atlas for an instance the bake never covered would be
        /// wrong rather than merely dim, so Bind refuses anything but Godot's
        /// STATIC gi_mode instead of drawing a lie.
        /// </summary>
        [Test]
        public void Bind_RefusesAnInstanceTheBakeDoesNotCover()
        {
            var lighting = BakedLighting.Load(BattleWorld.DefaultMapId);
            var go = new GameObject("DynamicProp");
            try
            {
                var renderer = go.AddComponent<MeshRenderer>();
                var entry = new LightingEntry
                {
                    Path = "objects/dynamic/mesh_instance",
                    GiMode = 0,
                };

                Assert.Throws<System.ArgumentException>(
                    () => lighting.Bind(renderer, entry));
                Assert.AreEqual(0, lighting.BoundCount);
            }
            finally
            {
                Object.DestroyImmediate(go);
                lighting.Release();
            }
        }
    }
}
