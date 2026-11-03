using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The lighting manifests are the port's copy of WE_low.tres, the two
    /// LightmapGI bakes and every light node, so these numbers are the ones a
    /// reviewer can grep for in the source scenes.
    /// </summary>
    public sealed class LightingManifestTests
    {
        private static LightingDefinition Load(string id)
        {
            var asset = Resources.Load<TextAsset>("Data/lighting_" + id);
            Assert.IsNotNull(asset, "Data/lighting_" + id + " is missing");
            return LightingDefinition.Parse(asset.text);
        }

        private static LightmapMeshLibrary LoadMeshes(LightingDefinition lighting)
        {
            var asset = Resources.Load<TextAsset>(lighting.Meshes.Resource);
            Assert.IsNotNull(asset, lighting.Meshes.Resource + " is missing");
            return LightmapMeshLibrary.Parse(asset.bytes);
        }

        [Test]
        public void HangarEnvironment_MatchesWeLow()
        {
            var env = Load("hangar").Environment;

            Assert.AreEqual("res://maps/hangar_low/WE_low.tres", env.Resource);
            Assert.AreEqual(1, env.BackgroundMode);
            Assert.AreEqual(0.75393677f, env.BackgroundColor[0], 0.0000001f);
            Assert.AreEqual(0.65074903f, env.BackgroundColor[1], 0.0000001f);
            Assert.AreEqual(0.46578008f, env.BackgroundColor[2], 0.0000001f);
            Assert.AreEqual(0.66f, env.BackgroundEnergy, 0.0001f);
            // Linear tonemapping: the port must not fit a filmic curve to it.
            Assert.AreEqual(0, env.TonemapMode);
            Assert.AreEqual(2.33f, env.TonemapExposure, 0.0001f);
            Assert.AreEqual(1f, env.TonemapWhite, 0.0001f);

            // Ambient comes from the background, so ambient_light_color is
            // inert: background_color decoded, then scaled by its multiplier.
            Assert.AreEqual(0, env.AmbientSource);
            Assert.AreEqual(0.34893f, env.AmbientRgb()[0], 0.0001f);
            Assert.AreEqual(0.25148f, env.AmbientRgb()[1], 0.0001f);
            Assert.AreEqual(0.12126f, env.AmbientRgb()[2], 0.0001f);

            Assert.IsTrue(env.FogEnabled);
            Assert.AreEqual(1, env.FogMode);
            Assert.AreEqual(1.23f, env.FogLightEnergy, 0.0001f);
            Assert.AreEqual(1f, env.FogDensity, 0.0001f);
            Assert.AreEqual(10f, env.FogDepthBegin, 0.0001f);
            Assert.AreEqual(90f, env.FogDepthEnd, 0.0001f);
            Assert.AreEqual(1f, env.FogDepthCurve, 0.0001f);
            Assert.AreEqual(0f, env.FogHeightDensity, 0.0001f);

            // Nothing here needs a post stack beyond exposure.
            Assert.IsFalse(env.GlowEnabled);
            Assert.IsFalse(env.AdjustmentEnabled);
            Assert.IsFalse(env.SsaoEnabled);
            Assert.IsFalse(env.SsilEnabled);
            Assert.IsFalse(env.SdfgiEnabled);
            Assert.IsFalse(env.VolumetricFogEnabled);
        }

        [Test]
        public void RudnikiEnvironment_MatchesTheSceneEnvironment()
        {
            var env = Load("rudniki").Environment;

            Assert.AreEqual(
                "res://maps/rudniki/_rudniki.tscn::Environment_3s03q",
                env.Resource);
            Assert.AreEqual(1, env.BackgroundMode);
            Assert.AreEqual(0.31184959f, env.BackgroundColor[0], 0.0000001f);
            Assert.AreEqual(0.31184962f, env.BackgroundColor[1], 0.0000001f);
            Assert.AreEqual(0.31184959f, env.BackgroundColor[2], 0.0000001f);
            Assert.AreEqual(3.26f, env.BackgroundEnergy, 0.0001f);
            Assert.AreEqual(0, env.AmbientSource);
            Assert.AreEqual(1, env.ReflectedLightSource);
            Assert.AreEqual(0, env.TonemapMode);
            Assert.AreEqual(1f, env.TonemapExposure, 0.0001f);

            Assert.IsTrue(env.FogEnabled);
            Assert.AreEqual(0.23f, env.FogDensity, 0.0001f);
            Assert.AreEqual(0f, env.FogHeight, 0.0001f);
            Assert.AreEqual(0f, env.FogHeightDensity, 0.0001f);
            Assert.AreEqual(1f, env.FogDepthCurve, 0.000001f);
            Assert.AreEqual(-50f, env.FogDepthBegin, 0.0001f);
            Assert.AreEqual(1000f, env.FogDepthEnd, 0.001f);

            // The map, unlike the hangar, does colour-grade its frame.
            Assert.IsTrue(env.AdjustmentEnabled);
            Assert.AreEqual(1.1f, env.AdjustmentBrightness, 0.0001f);
            Assert.AreEqual(1.1f, env.AdjustmentContrast, 0.0001f);
            Assert.AreEqual(0.9f, env.AdjustmentSaturation, 0.0001f);
            Assert.IsFalse(env.GlowEnabled);
        }

        /// <summary>
        /// The colour space the port got wrong for a long time: Godot stores an
        /// Environment colour sRGB encoded and runs srgb_to_linear before it
        /// shades. A Godot 4.7 probe that lights a white quad with a 0.5
        /// ambient, with a 0.5 background as the ambient source and with a
        /// saturated 0.5 fog reads 0.5 back out of all three frames, where an
        /// already-linear 0.5 would have read 0.735.
        /// </summary>
        [Test]
        public void SourceColours_AreDecodedBeforeTheyLightTheScene()
        {
            Assert.AreEqual(0.21404f, LightingEnvironment.SourceChannel(0.5f), 0.0001f);
            Assert.AreEqual(0f, LightingEnvironment.SourceChannel(0f), 0.0001f);
            Assert.AreEqual(1f, LightingEnvironment.SourceChannel(1f), 0.0001f);

            var env = Load("rudniki").Environment;
            var ambient = env.AmbientRgb();

            Assert.AreEqual(0.25833f, ambient[0], 0.0001f);
            Assert.AreEqual(0.25833f, ambient[1], 0.0001f);
            Assert.AreEqual(0.25833f, ambient[2], 0.0001f);

            // Taken for linear, the authored values again exceed one. Decoding
            // first keeps the source's strong background from flattening the
            // terrain and village materials into a white wash.
            Assert.Greater(env.BackgroundColor[0] * env.BackgroundEnergy, 1f);
            Assert.Less(ambient[0], 0.3f);
        }

        [Test]
        public void Environment_PushesTheSourceFogCurveToTheShaders()
        {
            var env = Load("rudniki").Environment;

            BlitzLegacy.Runtime.Lighting.SourceEnvironment.Apply(env);

            // Unity's linear fog still carries the range, because anything
            // outside the ported shaders reads it - the tank draws with URP Lit.
            Assert.IsTrue(RenderSettings.fog);
            Assert.AreEqual(-50f, RenderSettings.fogStartDistance, 0.0001f);
            Assert.AreEqual(1000f, RenderSettings.fogEndDistance, 0.001f);

            // The map shaders read the whole source curve, which is the part a
            // linear ramp cannot express: a density, a depth curve and a height
            // term that fills the basin below y = 10.
            var depth = Shader.GetGlobalVector("_BlitzFogDepth");
            Assert.AreEqual(-50f, depth.x, 0.0001f);
            Assert.AreEqual(1000f, depth.y, 0.001f);
            Assert.AreEqual(0.23f, depth.z, 0.0001f);
            Assert.AreEqual(1f, depth.w, 0.000001f);

            var height = Shader.GetGlobalVector("_BlitzFogHeight");
            Assert.AreEqual(0f, height.x, 0.0001f);
            Assert.AreEqual(0f, height.y, 0.0001f);

            // srgb_to_linear(fog_light_color) * fog_light_energy *
            // tonemap_exposure, and left linear on purpose: a Color would have
            // been decoded once more on its way to the GPU, which is how this
            // port squared a colour before. The decode is what keeps the veil
            // the warm sand of the source; the authored numbers taken for
            // linear read as a near-neutral grey over the whole map.
            var colour = Shader.GetGlobalVector("_BlitzFogColour");
            Assert.AreEqual(1f, colour.x, 0.0001f);
            Assert.AreEqual(0.94731f, colour.y, 0.0001f);
            Assert.AreEqual(0.93011f, colour.z, 0.0001f);
            Assert.AreEqual(1f, colour.w, 0.0001f);
            Assert.Greater(colour.x - colour.z, 0.06f);

            // Applying the other scene has to move the globals with it: the two
            // scenes share these shaders, so a stale global would be the hangar
            // drawing with the map's fog.
            BlitzLegacy.Runtime.Lighting.SourceEnvironment.Apply(Load("hangar").Environment);

            var hangarDepth = Shader.GetGlobalVector("_BlitzFogDepth");
            Assert.AreEqual(10f, hangarDepth.x, 0.0001f);
            Assert.AreEqual(90f, hangarDepth.y, 0.0001f);
            Assert.AreEqual(1f, hangarDepth.z, 0.0001f);
            Assert.AreEqual(1f, hangarDepth.w, 0.0001f);
        }

        [Test]
        public void HangarRig_IsBakedExceptTheChestSpot()
        {
            var lighting = Load("hangar");

            Assert.AreEqual(14, lighting.Lights.Count);

            var dynamicLights = 0;
            foreach (var light in lighting.Lights)
            {
                if (light.Path == "chest/SpotLight3D")
                {
                    dynamicLights++;
                    continue;
                }

                Assert.IsTrue(light.IsStaticBake, light.Path + " is not baked");
                Assert.IsTrue(light.ShadowEnabled, light.Path + " bakes no shadow");
            }

            Assert.AreEqual(1, dynamicLights);

            // The one light the source still runs in realtime, and it casts no
            // shadow, so the port may not give it one either.
            var chest = lighting.Light("chest/SpotLight3D");
            Assert.IsTrue(chest.IsDynamicBake);
            Assert.IsFalse(chest.ShadowEnabled);
            Assert.AreEqual(2.617f, chest.Energy, 0.0001f);
            Assert.AreEqual(25.694418f, chest.SpotAngle, 0.0001f);
            Assert.AreEqual(5f, chest.SpotRange, 0.0001f);

            var lamp9 = lighting.Light("hangars/low/hangar_low_batch/light_lamp9");
            Assert.AreEqual(12.421f, lamp9.Energy, 0.0001f);
            Assert.AreEqual(33.1986f, lamp9.SpotAngle, 0.0001f);
            Assert.AreEqual(33.5075f, lamp9.SpotRange, 0.0001f);
        }

        [Test]
        public void RudnikiSun_IsBakedAndCastsNoRealtimeShadow()
        {
            var lighting = Load("rudniki");

            Assert.AreEqual(1, lighting.Lights.Count);

            var sun = lighting.Light("DirectionalLight3D");
            Assert.AreEqual("DirectionalLight3D", sun.Type);
            Assert.AreEqual(1.465f, sun.Energy, 0.0001f);
            // No specular and no realtime shadow: the map's shading is the bake.
            Assert.AreEqual(0f, sun.Specular, 0.0001f);
            Assert.IsTrue(sun.IsStaticBake);
            Assert.IsFalse(sun.ShadowEnabled);
            // Authored but inert in the source; the port reuses it for the tank.
            Assert.AreEqual(0.89f, sun.ShadowOpacity, 0.0001f);
            Assert.AreEqual(0.1f, sun.ShadowBias, 0.0001f);
            Assert.AreEqual(0, sun.DirectionalShadowMode);
            Assert.AreEqual(100f, sun.DirectionalShadowMaxDistance, 0.0001f);
        }

        [Test]
        public void HangarAtlas_MatchesTheBake()
        {
            var lighting = Load("hangar");
            var atlas = lighting.Lightmap;

            Assert.AreEqual("Models/Hangar/Lightmaps/hangar_low", atlas.Atlas);
            Assert.AreEqual(6, atlas.Slices);
            Assert.AreEqual(512, atlas.SliceWidth);
            Assert.AreEqual(512, atlas.SliceHeight);
            Assert.AreEqual(3791859, atlas.Bytes);
            Assert.AreEqual(
                "27876effd0c7dfd03d915898ccff3e3efdefe70664b44db64106638b5361c66d",
                atlas.Sha256);
            // Plain colour, no probes: one texture fetch reproduces the bake.
            Assert.IsFalse(atlas.UsesSphericalHarmonics);
            Assert.AreEqual(0, atlas.Probes);
            Assert.AreEqual(2, atlas.Bounces);
            Assert.AreEqual(1f, atlas.BakedExposure, 0.0001f);
            Assert.IsFalse(atlas.Interior);

            Assert.AreEqual(76, lighting.Entries.Count);
            Assert.AreEqual(0, lighting.StaleEntries.Count);
            foreach (var entry in lighting.Entries)
            {
                Assert.AreEqual(1, entry.CastShadow, entry.Path + " changed cast_shadow");
                Assert.AreEqual(1, entry.GiMode, entry.Path + " changed gi_mode");
            }
        }

        [Test]
        public void RudnikiAtlas_MatchesTheBake()
        {
            var lighting = Load("rudniki");
            var atlas = lighting.Lightmap;

            Assert.AreEqual("Maps/Rudniki/Lightmaps/rudniki", atlas.Atlas);
            Assert.AreEqual(16, atlas.Slices);
            Assert.AreEqual(4, atlas.LayoutHorizontal);
            Assert.AreEqual(4, atlas.LayoutVertical);
            Assert.AreEqual(512, atlas.SliceWidth);
            Assert.AreEqual(256, atlas.SliceHeight);
            Assert.AreEqual(6012400, atlas.Bytes);
            Assert.AreEqual(
                "13135b359bdac406b438902722638bc00bfef8c281bfb08e4afaea225a8091c8",
                atlas.Sha256);
            Assert.IsFalse(atlas.UsesSphericalHarmonics);
            Assert.AreEqual(0, atlas.Probes);

            Assert.AreEqual(478, lighting.Entries.Count);
            Assert.AreEqual(0, lighting.StaleEntries.Count);
        }

        [Test]
        public void AtlasTransform_FoldsTheRectOntoItsSlice()
        {
            var lighting = Load("rudniki");
            var entry = lighting.Entry("scene/bl_03_01/mesh_instance");

            Assert.AreEqual(0, entry.Slice);
            Assert.AreEqual(0.001953125f, entry.Rect[0], 0.0000001f);
            Assert.AreEqual(0.00390625f, entry.Rect[1], 0.0000001f);

            // uv = uv2 * scale + offset into slice zero of the 4x4 atlas,
            // with V mirrored because Godot counts it downwards.
            var st = entry.AtlasScaleOffset;
            Assert.AreEqual(0.11181640625f, st[0], 0.0000001f);
            Assert.AreEqual(-0.2265625f, st[1], 0.0000001f);
            Assert.AreEqual(0.00048828125f, st[2], 0.0000001f);
            Assert.AreEqual(0.9990234375f, st[3], 0.0000001f);
        }

        [Test]
        public void LightmapMeshes_CarryTheSecondUvChannel()
        {
            foreach (var id in new[] { "hangar", "rudniki" })
            {
                var lighting = Load(id);
                var meshes = LoadMeshes(lighting);

                Assert.AreEqual(lighting.Meshes.Count, meshes.Count, id + " mesh count");
                Assert.AreEqual(lighting.Meshes.Vertices, meshes.VertexCount, id + " vertices");
                Assert.AreEqual(lighting.Meshes.Triangles, meshes.TriangleCount, id + " triangles");

                foreach (var mesh in meshes.Meshes)
                {
                    Assert.AreEqual(
                        mesh.VertexCount * 2,
                        mesh.Uv2.Length,
                        mesh.Name + " has no lightmap unwrap");
                }

                foreach (var entry in lighting.Entries)
                {
                    foreach (var name in entry.Meshes)
                    {
                        Assert.IsTrue(meshes.Has(name), entry.Path + " wants '" + name + "'");
                    }
                }
            }
        }

        [Test]
        public void LightmapPayloads_MatchTheExportedTotals()
        {
            var hangar = Load("hangar");
            Assert.AreEqual("Models/Hangar/lightmap_meshes", hangar.Meshes.Resource);
            Assert.AreEqual(76, hangar.Meshes.Count);
            Assert.AreEqual(148308, hangar.Meshes.Vertices);
            Assert.AreEqual(74987, hangar.Meshes.Triangles);

            var rudniki = Load("rudniki");
            Assert.AreEqual("Maps/Rudniki/lightmap_meshes", rudniki.Meshes.Resource);
            // 41 source surfaces carry all 478 baked instances.
            Assert.AreEqual(41, rudniki.Meshes.Count);
            Assert.AreEqual(38552, rudniki.Meshes.Vertices);
            Assert.AreEqual(18495, rudniki.Meshes.Triangles);
            Assert.AreEqual(1766176, rudniki.Meshes.Bytes);
        }

        /// <summary>
        /// Every baked hangar surface draws with one of the three
        /// ShaderMaterials hangar_low_batch assigns, and the manifest is the
        /// port's copy of them: 1.tres, 2.tres and the material the batch
        /// scene keeps inline. All three run map_object.gdshader, which
        /// multiplies its albedo texture by a linear tint, raises the result to
        /// a contrast exponent and adds an emission texture when the surface
        /// emits.
        /// </summary>
        [Test]
        public void HangarMaterials_MatchTheSourceShaderMaterials()
        {
            var hangar = Load("hangar");
            Assert.AreEqual(3, hangar.Materials.Count);

            foreach (var material in hangar.Materials)
            {
                Assert.AreEqual("ShaderMaterial", material.Class, material.Key);
                Assert.AreEqual(
                    "res://maps/components/map_object.gdshader",
                    material.Shader,
                    material.Key);
                Assert.AreEqual(1f, material.Contrast, 0.0001f, material.Key);
                Assert.IsFalse(string.IsNullOrEmpty(material.Texture), material.Key);

                for (var i = 0; i < 4; i++)
                {
                    Assert.AreEqual(
                        1f, material.Albedo[i], 0.0001f, material.Key + " albedo " + i);
                }

                Assert.AreEqual(1f, material.Uv1Scale[0], 0.0001f, material.Key);
                Assert.AreEqual(1f, material.Uv1Scale[1], 0.0001f, material.Key);
                Assert.AreEqual(0f, material.Uv1Offset[0], 0.0001f, material.Key);
                Assert.AreEqual(0f, material.Uv1Offset[1], 0.0001f, material.Key);
            }

            var main = hangar.Material("res://maps/hangar_low/1.tres");
            Assert.AreEqual("Models/Hangar/Textures/Hangar1", main.Texture);
            Assert.IsTrue(main.Emissive);
            Assert.AreEqual(
                "Models/Hangar/Textures/Hangar1Emission", main.EmissiveTexture);

            // 2.tres keeps an emission texture assigned with emissive off, and
            // Godot never samples it, so the export drops it rather than
            // letting the port light a surface the source leaves dark.
            var secondary = hangar.Material("res://maps/hangar_low/2.tres");
            Assert.AreEqual("Models/Hangar/Textures/Hangar2", secondary.Texture);
            Assert.IsFalse(secondary.Emissive);
            Assert.IsNull(secondary.EmissiveTexture);

            var inline = hangar.Material(
                "res://maps/hangar_low/hangar_low_batch.tscn::ShaderMaterial_petxa");
            Assert.AreEqual("Models/Hangar/Textures/Hangar3", inline.Texture);
            Assert.IsTrue(inline.Emissive);
            Assert.AreEqual("Models/Hangar/Textures/Hangar3", inline.EmissiveTexture);
        }

        /// <summary>
        /// The hangar is built out of these entries, so a surface with no
        /// material, or one naming a material the export skipped, would put an
        /// untextured prop in the scene. The 62/13/1 split is the one a
        /// hand-written list of node names used to carry by hand.
        /// </summary>
        [Test]
        public void HangarEntries_EachDrawWithAnExportedMaterial()
        {
            var hangar = Load("hangar");
            var counts = new Dictionary<string, int>();
            foreach (var entry in hangar.Entries)
            {
                Assert.IsFalse(
                    string.IsNullOrEmpty(entry.Material),
                    entry.Path + " draws with no material");
                Assert.IsTrue(
                    hangar.HasMaterial(entry.Material),
                    entry.Path + " draws with unexported '" + entry.Material + "'");
                counts[entry.Material] = counts.ContainsKey(entry.Material)
                    ? counts[entry.Material] + 1
                    : 1;
            }

            Assert.AreEqual(76, hangar.Entries.Count);
            Assert.AreEqual(3, counts.Count);
            Assert.AreEqual(62, counts["res://maps/hangar_low/1.tres"]);
            Assert.AreEqual(13, counts["res://maps/hangar_low/2.tres"]);
            Assert.AreEqual(
                1,
                counts["res://maps/hangar_low/hangar_low_batch.tscn::ShaderMaterial_petxa"]);

            // The map's props carry their materials in the object table, which
            // is why its entries deliberately leave this empty.
            var rudnikiMap = Load("rudniki");
            Assert.AreEqual(0, rudnikiMap.Materials.Count);
            foreach (var entry in rudnikiMap.Entries)
            {
                Assert.IsNull(entry.Material, entry.Path);
            }
        }

        /// <summary>
        /// adjustment_* in the map's Environment. The source applies these to
        /// the finished frame, so the port pushes them to a fullscreen pass
        /// rather than to the map shaders.
        /// </summary>
        [Test]
        public void Environment_PushesTheSourceGradeToTheFrame()
        {
            var rudniki = Load("rudniki").Environment;
            Assert.IsTrue(rudniki.AdjustmentEnabled);

            BlitzLegacy.Runtime.Lighting.SourceEnvironment.Apply(rudniki);

            var grade = Shader.GetGlobalVector("_BlitzAdjust");
            Assert.AreEqual(1.1f, grade.x, 0.0001f);
            Assert.AreEqual(1.1f, grade.y, 0.0001f);
            Assert.AreEqual(0.9f, grade.z, 0.0001f);
            Assert.AreEqual(1f, grade.w, 0.0001f);

            // The hangar leaves the adjustment off, and this flag is the whole
            // reason the pass stays out of its frame.
            var hangar = Load("hangar").Environment;
            Assert.IsFalse(hangar.AdjustmentEnabled);

            BlitzLegacy.Runtime.Lighting.SourceEnvironment.Apply(hangar);
            Assert.AreEqual(
                0f,
                Shader.GetGlobalVector("_BlitzAdjust").w,
                0.0001f);
        }
    }
}
