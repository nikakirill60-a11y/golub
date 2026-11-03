using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Save;
using BlitzLegacy.EditorTools;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Hangar.Halloween;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>The Miku hangar (hangar_miku_void) as the stand builds it from Resources.</summary>
    public sealed class MikuHangarTests
    {
        // Seven-segment patterns the clock's graphs switch, segments A..G.
        private readonly Dictionary<string, char> _digits = new Dictionary<string, char>
        {
            { "1111110", '0' }, { "0110000", '1' }, { "1101101", '2' }, { "1111001", '3' }, { "0110011", '4' },
            { "1011011", '5' }, { "1011111", '6' }, { "1110000", '7' }, { "1111111", '8' }, { "1111011", '9' },
        };

        [Test]
        public void SceneBuildsFromResources()
        {
            var scene = HalloweenHangarScene.Create(null, PortedHangar.Miku);
            try
            {
                Assert.IsFalse(scene.gameObject.activeSelf, "The hangar starts when its owner shows it");
                Assert.AreSame(PortedHangar.Miku, scene.Export);
                Assert.AreEqual("hangar_miku_void", scene.Id);
                Assert.AreEqual("Miku_hangar_miku_void", scene.name);
                Assert.AreEqual(184, scene.NodeCount);
                Assert.AreEqual(149, scene.RendererCount);
                Assert.Greater(scene.TriangleCount, 90000);
                Assert.AreEqual(0, scene.ClipCount);
                Assert.AreEqual(0, scene.EffectCount);
                Assert.AreEqual(0, scene.ParticleSystemCount);
                Assert.AreEqual(0, scene.StateSwitcherCount);
                Assert.AreEqual(15, scene.ScenarioCount, "12 clock digits, the Earth and two turning nodes");
                Assert.IsNull(scene.Audio, "The scene has no sound emitters");
                Assert.AreEqual(3, scene.LightCount, "The sun and the vehicle's two point lights");
                Assert.AreEqual(0, scene.Rebrands.Count);
                CollectionAssert.IsEmpty(scene.Warnings, string.Join("\n", scene.Warnings));

                var shaders = new HashSet<string>(HalloweenHangarScene.ShaderNames, StringComparer.Ordinal);
                var shifted = new HashSet<Material>();
                foreach (var renderer in scene.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (var material in renderer.sharedMaterials)
                    {
                        Assert.IsNotNull(material, renderer.name + " has an empty material slot");
                        Assert.IsTrue(shaders.Contains(material.shader.name), renderer.name + " is drawn by " + material.shader.name);
                        if (material.GetVector("_Tex0ShiftPerSecond") != Vector4.zero) shifted.Add(material);
                    }
                }
                Assert.AreEqual(6, shifted.Count, "The materials with TEXTURE0_ANIMATION_SHIFT scroll their albedo");
                Assert.IsTrue(shifted.Any(m => m.GetVector("_Tex0ShiftPerSecond") == new Vector4(-0.05f, 0f, 0f, 0f)),
                    "The Earth turns its texture by -0.05 a second");
            }
            finally
            {
                scene.Release();
                Object.DestroyImmediate(scene.gameObject);
            }
        }

        [Test]
        public void ClockShowsTheDateAndTimeOfTheClient()
        {
            var scene = HalloweenHangarScene.Create(null, PortedHangar.Miku);
            try
            {
                Tick(scene, new DateTime(2026, 11, 7, 21, 34, 56));
                Assert.AreEqual("07.11.26 21:34:56", Display(scene));
                Tick(scene, new DateTime(2030, 1, 19, 8, 5, 0));
                Assert.AreEqual("19.01.30 08:05:00", Display(scene));
                Tick(scene, new DateTime(2029, 12, 31, 23, 59, 59));
                Assert.AreEqual("31.12.29 23:59:59", Display(scene));
            }
            finally
            {
                scene.Release();
                Object.DestroyImmediate(scene.gameObject);
            }
        }

        [Test]
        public void EarthTakesTheNightTintFromEightPmToSevenAm()
        {
            var scene = HalloweenHangarScene.Create(null, PortedHangar.Miku);
            try
            {
                var earth = scene.Scenarios.Single(s => s.name == "Earth");
                var renderer = earth.GetComponent<Renderer>();
                var block = new MaterialPropertyBlock();
                var night = new Vector4(0.3f, 0.2f, 1f, 1f);
                var day = new Vector4(1f, 1f, 0f, 1f);
                foreach (var (hour, tint) in new[] { (0, night), (7, night), (8, day), (12, day), (19, day), (20, night), (23, night) })
                {
                    earth.Tick(0f, new DateTime(2026, 11, 7, hour, 30, 0));
                    renderer.GetPropertyBlock(block);
                    Assert.AreEqual(tint, block.GetVector("_FlatColor"), hour + ":30");
                }
            }
            finally
            {
                scene.Release();
                Object.DestroyImmediate(scene.gameObject);
            }
        }

        [Test]
        public void DisksTurnAtTheClientsSpeedWhateverTheFrameRate()
        {
            var scene = HalloweenHangarScene.Create(null, PortedHangar.Miku);
            try
            {
                var disk = scene.Scenarios.Single(s => s.name == "Disk1");
                var now = new DateTime(2026, 11, 7, 12, 0, 0);
                var start = disk.transform.localRotation;
                for (var frame = 0; frame < 60; frame++) disk.Tick(1f / 60f, now);
                var at60 = Quaternion.Angle(start, disk.transform.localRotation);
                disk.transform.localRotation = start;
                for (var frame = 0; frame < 30; frame++) disk.Tick(1f / 30f, now);
                var at30 = Quaternion.Angle(start, disk.transform.localRotation);
                // Lerp(0.5, 1, dt) degrees a 60 Hz frame: about 30.5 degrees a second.
                Assert.AreEqual(60f * (0.5f + 0.5f / 60f), at60, 0.05f);
                Assert.AreEqual(at60, at30, 1f, "A slower frame rate turns about as far a second");
            }
            finally
            {
                scene.Release();
                Object.DestroyImmediate(scene.gameObject);
            }
        }

        [Test]
        public void StandSwitchesToMikuWithoutForcingTheDepthTexture()
        {
            var world = HangarWorld.Create(null, new SettingsData());
            try
            {
                var camera = world.HangarCamera;
                var depthOption = camera.GetUniversalAdditionalCameraData().requiresDepthOption;
                Assert.IsTrue(world.SetKind(HangarKind.Miku));
                Assert.AreEqual(0f, Mathf.DeltaAngle(world.Orbit.Yaw - world.StandYaw, PortedHangar.Miku.OpeningYaw.Value), 0.01f,
                    "The 4.8 opening view turns into the Miku hangar's own");
                var scene = world.PortedScene;
                Assert.IsNotNull(scene);
                Assert.AreSame(PortedHangar.Miku, scene.Export);
                Assert.IsNull(world.ClientScene);
                Assert.AreEqual(HangarKind.Miku, world.Kind);
                Assert.IsTrue(scene.gameObject.activeSelf);
                Assert.AreSame(scene.Sun, RenderSettings.sun);
                Assert.AreEqual(DefaultReflectionMode.Custom, RenderSettings.defaultReflectionMode);
                Assert.AreEqual(depthOption, camera.GetUniversalAdditionalCameraData().requiresDepthOption,
                    "A hangar without soft particles leaves the depth texture alone");
                Assert.AreEqual(0f, Shader.GetGlobalVector("_BlitzFogRange").w, "The scene's fog is switched off");
                Assert.AreEqual(world.StandPosition, world.TankModel.transform.localPosition);

                Assert.IsTrue(world.SetKind(HangarKind.Halloween2026), "One ported hangar replaces the other");
                Assert.IsTrue(scene == null, "The Miku hangar is destroyed with its switch");
                Assert.AreSame(PortedHangar.Halloween2026, world.PortedScene.Export);
                Assert.AreEqual(0f, Mathf.DeltaAngle(world.Orbit.Yaw - world.StandYaw, HangarWorld.SourceViewYaw), 0.01f,
                    "The Halloween hangar opens on the 4.8 view");
            }
            finally
            {
                world.Release();
                Object.DestroyImmediate(world.gameObject);
            }
        }

        [Test]
        public void OpeningViewShowsTheVehicleInFrontOfTheClock()
        {
            var world = HangarWorld.Create(null, new SettingsData(), kind: HangarKind.Miku);
            try
            {
                Assert.AreEqual(PortedHangar.Miku.OpeningYaw.Value, world.OpeningYaw);
                Assert.AreEqual(0f, Mathf.DeltaAngle(world.Orbit.Yaw - world.StandYaw, world.OpeningYaw), 0.01f);
                var camera = world.HangarCamera;
                camera.aspect = 16f / 9f;
                var eye = camera.transform.position;
                var vehicle = BoundsOf(world.TankModel.GetComponentsInChildren<Renderer>()).center;
                var sight = new Ray(eye, vehicle - eye);
                var range = Vector3.Distance(eye, vehicle);
                foreach (var renderer in world.PortedScene.GetComponentsInChildren<Renderer>())
                {
                    var bounds = renderer.bounds;
                    // The floor, the sky and the shells around the whole stand.
                    if (bounds.Contains(eye) || bounds.Contains(vehicle)) continue;
                    Assert.IsFalse(bounds.IntersectRay(sight, out var distance) && distance < range,
                        renderer.name + " stands between the camera and the vehicle");
                }

                var planes = GeometryUtility.CalculateFrustumPlanes(camera);
                foreach (var group in new[] { "DDMMYY", "HHMMSS" })
                {
                    var node = world.PortedScene.GetComponentsInChildren<Transform>(true).Single(t => t.name == group);
                    Assert.IsTrue(GeometryUtility.TestPlanesAABB(planes, BoundsOf(node.GetComponentsInChildren<Renderer>())),
                        "The opening view shows the clock's " + group);
                }
            }
            finally
            {
                world.Release();
                Object.DestroyImmediate(world.gameObject);
            }
        }

        [Test]
        public void CatalogPortsExactlyTheHangarsTheRuntimeBuilds()
        {
            foreach (var info in HangarCatalog.All)
            {
                var export = PortedHangar.For(info.Kind);
                Assert.AreEqual(info.Ported, export != null, info.Id);
                if (export != null) Assert.AreEqual(info.Id, export.MapName);
            }
            Assert.AreEqual(PortedHangar.All.Count, HangarCatalog.All.Count(info => info.Ported));
        }

        [Test]
        public void ImporterReadsEveryTextureTheExporterListed()
        {
            var root = HalloweenHangarImport.RootOf(PortedHangar.Miku);
            var specs = HalloweenHangarImport.ParseSpecs(File.ReadAllText(HalloweenHangarImport.SpecPathOf(PortedHangar.Miku)));
            Assert.AreEqual(53, specs.Count);
            foreach (var pair in specs)
            {
                Assert.IsTrue(File.Exists(root + pair.Key), pair.Key);
                Assert.AreSame(PortedHangar.Miku, HalloweenHangarImport.ExportOf(root + pair.Key));
                Assert.IsFalse(pair.Value.Srgb, pair.Key + ": the client's maths runs on gamma texels");
                Assert.IsTrue(pair.Value.Android.ToString().StartsWith("ASTC_", StringComparison.Ordinal), pair.Key);
            }
            foreach (var path in Directory.GetFiles(root, "*.png", SearchOption.AllDirectories))
            {
                var relative = path.Replace('\\', '/').Substring(root.Length);
                Assert.IsTrue(specs.ContainsKey(relative), relative + " has no import settings");
            }
        }

        private static Bounds BoundsOf(IEnumerable<Renderer> renderers)
        {
            Bounds? all = null;
            foreach (var renderer in renderers)
            {
                var bounds = renderer.bounds;
                if (all.HasValue) bounds.Encapsulate(all.Value);
                all = bounds;
            }
            Assert.IsTrue(all.HasValue, "Nothing is drawn");
            return all.Value;
        }

        private static void Tick(HalloweenHangarScene scene, DateTime now)
        {
            foreach (var scenario in scene.Scenarios) scenario.Tick(0f, now);
        }

        private string Display(HalloweenHangarScene scene)
        {
            return Digit(scene, "Day") + "." + Digit(scene, "Month") + "." + Digit(scene, "Year") + " " +
                Digit(scene, "Hour") + ":" + Digit(scene, "Minute") + ":" + Digit(scene, "Second");
        }

        private string Digit(HalloweenHangarScene scene, string field)
        {
            var text = string.Empty;
            foreach (var place in new[] { "_01", "_02" })
            {
                var digit = scene.Scenarios.Single(s => s.name == field + place);
                Assert.AreEqual(7, digit.TargetCount, digit.name);
                var pattern = string.Concat("ABCDEFG".Select(segment =>
                    digit.transform.Find(segment.ToString()).gameObject.activeSelf ? '1' : '0'));
                Assert.IsTrue(_digits.TryGetValue(pattern, out var shown), digit.name + " shows the segments " + pattern);
                text += shown;
            }
            return text;
        }
    }
}
