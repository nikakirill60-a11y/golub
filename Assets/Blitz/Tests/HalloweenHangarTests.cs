using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Save;
using BlitzLegacy.EditorTools;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Hangar.Halloween;
using BlitzLegacy.Runtime.Lighting;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>The 11.21 Halloween 2026 hangar as the stand builds it from Resources.</summary>
    public sealed class HalloweenHangarTests
    {
        [Test]
        public void SceneBuildsEveryClientPartFromResources()
        {
            var scene = HalloweenHangarScene.Create(null);
            try
            {
                Assert.IsFalse(scene.gameObject.activeSelf, "The hangar starts when its owner shows it");
                Assert.AreEqual("hangar_halloween_2026", scene.Id);
                Assert.AreEqual(2352, scene.NodeCount);
                Assert.AreEqual(420, scene.RendererCount);
                Assert.Greater(scene.MaterialCount, 0);
                Assert.AreEqual(18, scene.ClipCount);
                Assert.AreEqual(38, scene.AnimationCount);
                Assert.AreEqual(16, scene.EffectCount);
                Assert.AreEqual(5, scene.StateSwitcherCount);
                Assert.AreEqual(2, scene.LightCount, "The sun and the vehicle's point light");
                Assert.Greater(scene.ParticleSystemCount, 0);
                Assert.Greater(scene.TriangleCount, 0);
                Assert.AreEqual(16, scene.Audio.EmitterCount);
                Assert.AreEqual(49, scene.Audio.ClipCount);
                Assert.IsNotNull(scene.Audio.ReverbZone);
                Assert.AreEqual(2, scene.Rebrands.Count, "The wall emblem and the billboard screen wear the port's art");
                Assert.AreEqual("/prop_icon_metal.sc2", scene.Rebrands[0].Path);
                Assert.AreEqual("/env_hg_board.sc2", scene.Rebrands[1].Path);
                foreach (var warning in scene.Warnings) TestContext.WriteLine("warning: " + warning);

                var shaders = new HashSet<string>(HalloweenHangarScene.ShaderNames, StringComparer.Ordinal);
                foreach (var renderer in scene.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material == null && renderer is ParticleSystemRenderer) continue;
                        Assert.IsNotNull(material, renderer.name + " has an empty material slot");
                        Assert.IsTrue(shaders.Contains(material.shader.name),
                            renderer.name + " is drawn by " + material.shader.name);
                    }
                }

                foreach (var states in scene.States)
                {
                    Assert.Greater(states.StateCount, 0, states.name + " has no states");
                    Assert.AreEqual(-1, states.Drawn, "The draw waits for the hangar to show");
                }

                // The tank's point light must stay out of the shadow budget.
                foreach (var light in scene.GetComponentsInChildren<Light>(true))
                {
                    if (light.type == LightType.Point)
                    {
                        Assert.IsNotNull(light.GetComponent<ShadowlessLight>(), light.name);
                        Assert.AreEqual(LightShadows.None, light.shadows, light.name);
                    }
                }
                Assert.AreEqual(LightType.Directional, scene.Sun.type);
            }
            finally
            {
                scene.Release();
                Object.DestroyImmediate(scene.gameObject);
            }
        }

        [Test]
        public void SwitchingToHalloweenAndBackKeepsTankCameraAndRenderState()
        {
            var world = HangarWorld.Create(null, new SettingsData());
            try
            {
                Assert.AreEqual(HangarKind.Regular, world.Kind);
                var tank = world.TankModel;
                var camera = world.HangarCamera;
                var reflectionMode = RenderSettings.defaultReflectionMode;
                var reflection = RenderSettings.customReflectionTexture;
                var ambientMode = RenderSettings.ambientMode;
                var ambient = RenderSettings.ambientLight;
                var depthOption = camera.GetUniversalAdditionalCameraData().requiresDepthOption;
                world.Orbit.SetView(30f, -20f);
                var relativeYaw = world.Orbit.Yaw - world.StandYaw;

                Assert.IsTrue(world.SetKind(HangarKind.Halloween2026));
                var scene = world.PortedScene;
                Assert.IsNotNull(scene);
                Assert.IsNull(world.ClientScene);
                Assert.IsFalse(world.IsPremium);
                Assert.AreSame(scene.gameObject, world.HangarModel);
                Assert.IsTrue(scene.gameObject.activeSelf);
                Assert.AreSame(tank, world.TankModel);
                Assert.AreSame(camera, world.HangarCamera);
                Assert.AreEqual(scene.RendererCount, world.BakedInstanceCount);
                Assert.AreEqual(scene.LightCount, world.SourceLightCount);
                Assert.AreEqual(DefaultReflectionMode.Custom, RenderSettings.defaultReflectionMode);
                Assert.IsInstanceOf<Cubemap>(RenderSettings.customReflectionTexture);
                Assert.AreEqual(AmbientMode.Trilight, RenderSettings.ambientMode);
                Assert.AreSame(scene.Sun, RenderSettings.sun);
                Assert.IsTrue(camera.GetUniversalAdditionalCameraData().requiresDepthTexture);
                Assert.AreEqual(scene.BackgroundColour, camera.backgroundColor);
                Assert.AreEqual(world.StandPosition, tank.transform.localPosition);
                Assert.Less(Quaternion.Angle(world.StandRotation, tank.transform.localRotation), 0.01f);
                Assert.AreEqual(world.StandPosition + Vector3.up * HangarWorld.OrbitHeight,
                    world.Orbit.transform.localPosition);
                Assert.AreEqual(0f, Mathf.DeltaAngle(world.Orbit.Yaw - world.StandYaw, relativeYaw), 0.01f,
                    "The orbit turns with the stand, so the view of the vehicle stays");
                Assert.AreEqual(-20f, world.Orbit.Pitch, 0.01f);
                Assert.IsFalse(world.SetKind(HangarKind.Halloween2026), "The same hangar is not rebuilt");

                Assert.IsTrue(world.SetKind(HangarKind.Regular));
                Assert.IsNull(world.PortedScene);
                Assert.IsTrue(scene == null, "The Halloween hangar is destroyed with its switch");
                Assert.AreEqual("hangar", world.ClientScene.Definition.id);
                Assert.AreSame(tank, world.TankModel);
                Assert.AreEqual(reflectionMode, RenderSettings.defaultReflectionMode);
                Assert.AreSame(reflection, RenderSettings.customReflectionTexture);
                Assert.AreEqual(ambientMode, RenderSettings.ambientMode);
                Assert.AreEqual(ambient, RenderSettings.ambientLight);
                Assert.AreEqual(depthOption, camera.GetUniversalAdditionalCameraData().requiresDepthOption);
                Assert.AreEqual(Vector3.zero, tank.transform.localPosition);
                Assert.Less(Quaternion.Angle(Quaternion.Euler(0f, HangarWorld.ModelYaw, 0f), tank.transform.localRotation), 0.01f);
                Assert.AreEqual(0f, Mathf.DeltaAngle(world.Orbit.Yaw - world.StandYaw, relativeYaw), 0.01f);
                var fog = Shader.GetGlobalVector(SourceEnvironment.FogRangeId);
                Assert.AreEqual(12f, fog.x, 0.001f);
                Assert.AreEqual(60f, fog.y, 0.001f);

                Assert.IsTrue(world.SetKind(HangarKind.Premium));
                Assert.IsTrue(world.IsPremium);
                Assert.AreEqual("hangar_prem_2018", world.ClientScene.Definition.id);
            }
            finally
            {
                Object.DestroyImmediate(world.gameObject);
            }
        }

        [Test]
        public void ExplicitKindOverridesThePremiumFlag()
        {
            var world = HangarWorld.Create(null, new SettingsData(), premium: true, kind: HangarKind.Halloween2026);
            try
            {
                Assert.AreEqual(HangarKind.Halloween2026, world.Kind);
                Assert.IsNotNull(world.PortedScene);
                Assert.IsNull(world.ClientScene);
                Assert.AreEqual(world.StandPosition, world.TankModel.transform.localPosition);
                // The 4.8 opening view, turned with the stand.
                Assert.AreEqual(0f, Mathf.DeltaAngle(world.Orbit.Yaw,
                    HangarWorld.SourceViewYaw + world.StandYaw), 0.01f);
            }
            finally
            {
                Object.DestroyImmediate(world.gameObject);
            }
        }

        [Test]
        public void StandBuiltBeforeTheOldOneGoesKeepsItsLightingAndTextures()
        {
            var reflectionMode = RenderSettings.defaultReflectionMode;
            var reflection = RenderSettings.customReflectionTexture;
            var ambientMode = RenderSettings.ambientMode;
            var ambient = RenderSettings.ambientLight;
            var sun = RenderSettings.sun;
            var fogRange = Shader.GetGlobalVector(SourceEnvironment.FogRangeId);
            var old = HangarWorld.Create(null, new SettingsData(), kind: HangarKind.Halloween2026);
            HangarWorld rebuilt = null;
            try
            {
                var litFog = Shader.GetGlobalVector(SourceEnvironment.FogRangeId);
                Assert.AreNotEqual(fogRange, litFog);
                // A hangar screen built again for a style switch: the new
                // stand is lit while the old one is still waiting to go.
                old.gameObject.SetActive(false);
                rebuilt = HangarWorld.Create(null, new SettingsData(), kind: HangarKind.Halloween2026);
                var oldScene = old.PortedScene;
                Object.DestroyImmediate(old.gameObject);
                old = null;

                var scene = rebuilt.PortedScene;
                Assert.Greater(oldScene.SharedTextureCount, 0, "Both stands load the same textures");
                Assert.AreEqual(0, oldScene.UnloadedTextureCount,
                    "The old stand unloaded the textures the new one draws with");
                Assert.AreEqual(AmbientMode.Trilight, RenderSettings.ambientMode,
                    "The old stand took the ambient light with it");
                Assert.AreSame(scene.Sun, RenderSettings.sun);
                Assert.AreEqual(DefaultReflectionMode.Custom, RenderSettings.defaultReflectionMode);
                Assert.IsInstanceOf<Cubemap>(RenderSettings.customReflectionTexture);
                Assert.AreEqual(litFog, Shader.GetGlobalVector(SourceEnvironment.FogRangeId),
                    "The old stand took the fog with it");

                Object.DestroyImmediate(rebuilt.gameObject);
                rebuilt = null;
                Assert.AreEqual(oldScene.SharedTextureCount, scene.UnloadedTextureCount,
                    "The last stand to go unloads the textures");
                Assert.AreEqual(0, scene.SharedTextureCount);
                Assert.AreEqual(reflectionMode, RenderSettings.defaultReflectionMode,
                    "The last stand to go gives back the state from before both");
                Assert.AreSame(reflection, RenderSettings.customReflectionTexture);
                Assert.AreEqual(ambientMode, RenderSettings.ambientMode);
                Assert.AreEqual(ambient, RenderSettings.ambientLight);
                Assert.AreSame(sun, RenderSettings.sun);
                Assert.AreEqual(fogRange, Shader.GetGlobalVector(SourceEnvironment.FogRangeId));
            }
            finally
            {
                if (old != null) Object.DestroyImmediate(old.gameObject);
                if (rebuilt != null) Object.DestroyImmediate(rebuilt.gameObject);
            }
        }

        [Test]
        public void ReleasedStandGivesTheLightingBackAtOnce()
        {
            var reflectionMode = RenderSettings.defaultReflectionMode;
            var reflection = RenderSettings.customReflectionTexture;
            var ambientMode = RenderSettings.ambientMode;
            var fogRange = Shader.GetGlobalVector(SourceEnvironment.FogRangeId);
            var old = HangarWorld.Create(null, new SettingsData(), kind: HangarKind.Halloween2026);
            HangarWorld next = null;
            try
            {
                Assert.AreEqual(AmbientMode.Trilight, RenderSettings.ambientMode);
                for (var i = 0; i < 2; i++)
                {
                    old.Release();
                    Assert.AreEqual(reflectionMode, RenderSettings.defaultReflectionMode);
                    Assert.AreSame(reflection, RenderSettings.customReflectionTexture);
                    Assert.AreEqual(ambientMode, RenderSettings.ambientMode);
                    Assert.AreEqual(fogRange, Shader.GetGlobalVector(SourceEnvironment.FogRangeId));
                }

                // HangarScreen.Discard: the released stand goes after the
                // next one has lit itself.
                next = HangarWorld.Create(null, new SettingsData(), kind: HangarKind.Halloween2026);
                var litFog = Shader.GetGlobalVector(SourceEnvironment.FogRangeId);
                Object.DestroyImmediate(old.gameObject);
                old = null;
                Assert.AreEqual(AmbientMode.Trilight, RenderSettings.ambientMode);
                Assert.AreSame(next.PortedScene.Sun, RenderSettings.sun);
                Assert.IsInstanceOf<Cubemap>(RenderSettings.customReflectionTexture);
                Assert.AreEqual(litFog, Shader.GetGlobalVector(SourceEnvironment.FogRangeId));
            }
            finally
            {
                if (old != null) Object.DestroyImmediate(old.gameObject);
                if (next != null) Object.DestroyImmediate(next.gameObject);
            }
        }

        [Test]
        public void StateSwitchersDrawTheClientsRandomRange()
        {
            // ParticleEffectsSwitcher / StateSwitcher draw 0..n inclusive.
            Assert.AreEqual(0, HalloweenHangarStates.Draw(0f, 3));
            Assert.AreEqual(1, HalloweenHangarStates.Draw(0.26f, 3));
            Assert.AreEqual(3, HalloweenHangarStates.Draw(0.99f, 3));
            Assert.AreEqual(3, HalloweenHangarStates.Draw(1f, 3));
        }

        [Test]
        public void LowpassStagesMeetAtMinusThreeDecibels()
        {
            Assert.AreEqual(1000f, HalloweenHangarAudio.CascadeLowpass(0f, 1000f));
            Assert.AreEqual(1000f, HalloweenHangarAudio.CascadeLowpass(1000f, 0f));
            foreach (var pair in new[] { new Vector2(1000f, 1000f), new Vector2(300f, 5000f), new Vector2(22000f, 800f) })
            {
                var f = HalloweenHangarAudio.CascadeLowpass(pair.x, pair.y);
                Assert.Less(f, Mathf.Min(pair.x, pair.y));
                var gain = (1d + f * (double)f / (pair.x * (double)pair.x)) * (1d + f * (double)f / (pair.y * (double)pair.y));
                Assert.AreEqual(2d, gain, 1e-3, pair.ToString());
            }
            Assert.AreEqual(1000f * Mathf.Sqrt(Mathf.Sqrt(2f) - 1f), HalloweenHangarAudio.CascadeLowpass(1000f, 1000f), 0.01f);
        }

        [Test]
        public void HighpassStagesMeetAtMinusThreeDecibels()
        {
            Assert.AreEqual(100f, HalloweenHangarAudio.CascadeHighpass(0f, 100f));
            foreach (var pair in new[] { new Vector2(100f, 100f), new Vector2(40f, 900f) })
            {
                var f = HalloweenHangarAudio.CascadeHighpass(pair.x, pair.y);
                Assert.Greater(f, Mathf.Max(pair.x, pair.y));
                var gain = (1d + pair.x * (double)pair.x / (f * (double)f)) * (1d + pair.y * (double)pair.y / (f * (double)f));
                Assert.AreEqual(2d, gain, 1e-3, pair.ToString());
            }
        }

        [Test]
        public void AzimuthIsMeasuredFromTheListenersFacing()
        {
            var facing = Quaternion.Euler(0f, 90f, 0f);
            Assert.AreEqual(0f, HalloweenHangarAudio.Azimuth(new Vector3(5f, 1f, 0f), facing), 0.01f);
            Assert.AreEqual(90f, HalloweenHangarAudio.Azimuth(new Vector3(0f, 0f, -5f), facing), 0.01f);
            Assert.AreEqual(-90f, HalloweenHangarAudio.Azimuth(new Vector3(0f, 0f, 5f), facing), 0.01f);
            Assert.AreEqual(180f, Mathf.Abs(HalloweenHangarAudio.Azimuth(new Vector3(-5f, 0f, 0f), facing)), 0.01f);
            Assert.AreEqual(0f, HalloweenHangarAudio.Azimuth(Vector3.up, facing));
        }

        [Test]
        public void ReverbSendFollowsTheDryAttenuation()
        {
            var dry = new HalloweenHangarAudio.EventSpec();
            Assert.AreEqual(0f, HalloweenHangarAudio.ReverbZoneMix(dry, 10f, -6f));

            var wet = new HalloweenHangarAudio.EventSpec { Reverb = true, ReverbLevel = -6f };
            Assert.AreEqual(1f, HalloweenHangarAudio.ReverbZoneMix(wet, 10f, -6f), 1e-5f);
            Assert.AreEqual(0.5f, HalloweenHangarAudio.ReverbZoneMix(wet, 10f, 0f), 0.01f);
            Assert.AreEqual(1.05f, HalloweenHangarAudio.ReverbZoneMix(wet, 10f, -11f), 1e-4f);
            Assert.AreEqual(HalloweenHangarAudio.MaxReverbZoneMix, HalloweenHangarAudio.ReverbZoneMix(wet, 10f, -40f));
            Assert.AreEqual(0f, HalloweenHangarAudio.ReverbZoneMix(
                new HalloweenHangarAudio.EventSpec { Reverb = true, ReverbLevel = -90f }, 10f, 0f));

            var curve = new HalloweenHangarAudio.EventSpec
            {
                Reverb = true,
                ReverbCurve = new HalloweenHangarAudio.Curve(new[] { 0f, 20f }, new[] { 0f, -20f }),
            };
            Assert.AreEqual(Mathf.Pow(10f, -0.5f), HalloweenHangarAudio.ReverbZoneMix(curve, 10f, 0f), 1e-4f);
        }

        [Test]
        public void ImporterReadsEveryTextureTheExporterListed()
        {
            var specs = HalloweenHangarImport.ParseSpecs(File.ReadAllText(HalloweenHangarImport.SpecPathOf(PortedHangar.Halloween2026)));
            Assert.AreEqual(384, specs.Count);
            foreach (var pair in specs)
            {
                Assert.IsTrue(File.Exists(HalloweenHangarImport.RootOf(PortedHangar.Halloween2026) + pair.Key), pair.Key);
                Assert.GreaterOrEqual(pair.Value.MaxSize, 32, pair.Key);
                Assert.IsTrue(pair.Value.Android.ToString().StartsWith("ASTC_", StringComparison.Ordinal), pair.Key);
            }

            foreach (var path in Directory.GetFiles(HalloweenHangarImport.RootOf(PortedHangar.Halloween2026), "*.png", SearchOption.AllDirectories))
            {
                var relative = path.Replace('\\', '/').Substring(HalloweenHangarImport.RootOf(PortedHangar.Halloween2026).Length);
                Assert.IsTrue(specs.ContainsKey(relative), relative + " has no import settings");
            }
        }

        [Test]
        public void ExportShowsThePortsArtInPlaceOfTheBlitzLogos()
        {
            var scene = HalloweenJson.Parse(File.ReadAllText(HalloweenHangarImport.RootOf(PortedHangar.Halloween2026) + "scene.json"));
            var rebrands = scene.Req("rebrand").Items;
            Assert.AreEqual(2, rebrands.Count);
            var sign = rebrands[0];
            Assert.AreEqual("sign", sign.Req("name").S());
            Assert.AreEqual("/prop_icon_metal.sc2", sign.Req("path").S());
            Assert.AreEqual("Roman numeral VI", sign.Req("port").S());
            CollectionAssert.AreEqual(new[] { 48, 243 }, sign.Req("vertices").Ints(), "The numeral replaces the client plaque's mesh");
            CollectionAssert.AreEqual(new[] { 50, 131 }, sign.Req("triangles").Ints());
            Assert.AreEqual(243, scene.Req("meshes")[sign.Req("mesh").I()].Req("vertexCount").I());
            var screen = rebrands[1];
            Assert.AreEqual("screen", screen.Req("name").S());
            Assert.AreEqual("/env_hg_board.sc2", screen.Req("path").S());
            Assert.AreEqual("Tools/HalloweenHangarArt/screen.jpg", screen.Req("port").S());

            // Only the two objects' own textures carry the port's art, at its own
            // size and with a finer ASTC block than the client's.
            var expected = new Dictionary<int, (int size, string block)>();
            foreach (var index in sign.Req("textures").Ints()) expected[index] = (512, "6x6");
            foreach (var index in screen.Req("textures").Ints()) expected[index] = (1024, "4x4");
            Assert.AreEqual(5, expected.Count, "The emblem's four textures and the billboard's colour");
            var specs = HalloweenHangarImport.ParseSpecs(File.ReadAllText(HalloweenHangarImport.SpecPathOf(PortedHangar.Halloween2026)));
            var textures = scene.Req("textures");
            for (int i = 0; i < textures.Count; i++)
            {
                var texture = textures[i];
                var file = texture.Req("file").S();
                if (!expected.TryGetValue(i, out var art))
                {
                    Assert.IsFalse(texture["replaced"].B(), file + " is the client's own");
                    continue;
                }
                Assert.IsTrue(texture["replaced"].B(), file);
                Assert.AreEqual(art.size, texture.Req("width").I(), file);
                Assert.AreEqual(art.size, texture.Req("height").I(), file);
                Assert.AreEqual("ASTC " + art.block, texture.Req("format").S(), file);
                Assert.AreEqual(art.size, specs[file].MaxSize, file);
                Assert.AreEqual("ASTC_" + art.block, specs[file].Android.ToString(), file);
            }
        }

        [Test]
        public void ImporterReadsOggLengthsWithoutDecoding()
        {
            foreach (var path in Directory.GetFiles(HalloweenHangarImport.RootOf(PortedHangar.Halloween2026) + "Audio", "*.ogg"))
            {
                var seconds = HalloweenHangarImport.OggSeconds(File.ReadAllBytes(path));
                Assert.Greater(seconds, 0d, path);
                Assert.Less(seconds, 60d, path);
            }
            Assert.AreEqual(-1d, HalloweenHangarImport.OggSeconds(new byte[64]));
        }
    }

    /// <summary>The Unity conventions the Halloween hangar runtime pins, measured.</summary>
    public sealed class HalloweenHangarProbeTests
    {
        [Test]
        public void ParticleRotationConventionsHold()
        {
            TestContext.WriteLine(HalloweenHangarProbes.Particles());
        }

        [Test]
        public void CubeFaceRowsMatchTheGpu()
        {
            if (!HalloweenHangarProbes.CanProbeGpu)
            {
                Assert.Ignore("No graphics device; ClientHangarRenderCli runs this probe on the GPU");
            }
            TestContext.WriteLine(HalloweenHangarProbes.CubeRows());
        }
    }
}
