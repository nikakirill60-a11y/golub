using System;
using System.Linq;
using System.Reflection;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Lighting;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class TankTrackMarksTests
    {
        private GameObject _root;
        private TanksDatabase _database;
        private FieldInfo _settingsField;
        private object _settingsBefore;
        private SettingsData _settings;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("tank track marks tests");
            _database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            _settingsField = typeof(GraphicsQuality).GetField(
                "_settings", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(_settingsField);
            _settingsBefore = _settingsField.GetValue(null);
            _settings = new SettingsData { GraphicsTankTreads = true };
            // Do not call Bind here: the marks read the live save, while the
            // test must not serialize graphics state into ProjectSettings.
            _settingsField.SetValue(null, _settings);
        }

        [TearDown]
        public void TearDown()
        {
            _settingsField.SetValue(null, _settingsBefore);
            if (_root != null) { UnityEngine.Object.DestroyImmediate(_root); }
        }

        [Test]
        public void SourceManifestKeepsEveryOriginalNationalTexture()
        {
            var asset = Resources.Load<TextAsset>("Data/tank_treads_1120_manifest");
            Assert.IsNotNull(asset);
            var manifest = JsonNode.Parse(asset.text);
            Assert.AreEqual("Blitz 11.20.0", manifest["client"].AsString());
            Assert.AreEqual("TankTreads", manifest["sourceBehaviour"]["option"].AsString());
            Assert.AreEqual("DECAL_TREAD", manifest["sourceBehaviour"]["shaderDefines"][0].AsString());
            Assert.AreEqual("FADE_OUT_WITH_TIME", manifest["sourceBehaviour"]["shaderDefines"][1].AsString());
            Assert.AreEqual(10, manifest["textures"].Count);

            var resources = new[]
            {
                "Ussr", "Germany", "Usa", "Gb", "Japan", "France",
                "China", "Other", "European", "Other_Oth50_STT",
            };
            foreach (var key in resources)
            {
                var texture = Resources.Load<Texture2D>("TankTreads1120/" + key);
                Assert.IsNotNull(texture, key + " tread texture was not imported");
                Assert.AreEqual(256, texture.width, key);
                Assert.AreEqual(256, texture.height, key);
                Assert.AreEqual(64, manifest["textures"][key]["sourceSha256"].AsString().Length, key);
                Assert.AreEqual(64, manifest["textures"][key]["pngSha256"].AsString().Length, key);
            }
        }

        [Test]
        public void NationMappingUsesTheBlitz1120Artwork()
        {
            Assert.AreEqual("TankTreads1120/Ussr", TankTrackMarks.TextureResource(Nation.Ussr));
            Assert.AreEqual("TankTreads1120/Germany", TankTrackMarks.TextureResource(Nation.Germany));
            Assert.AreEqual("TankTreads1120/Usa", TankTrackMarks.TextureResource(Nation.Usa));
            Assert.AreEqual("TankTreads1120/Gb", TankTrackMarks.TextureResource(Nation.Gb));
            Assert.AreEqual("TankTreads1120/Japan", TankTrackMarks.TextureResource(Nation.Japan));
            Assert.AreEqual("TankTreads1120/France", TankTrackMarks.TextureResource(Nation.France));
            Assert.AreEqual("TankTreads1120/China", TankTrackMarks.TextureResource(Nation.China));
            Assert.AreEqual("TankTreads1120/European", TankTrackMarks.TextureResource(Nation.European));
            Assert.AreEqual("TankTreads1120/Other", TankTrackMarks.TextureResource(Nation.Other));
            Assert.AreEqual(
                "TankTreads1120/Other_Oth50_STT",
                TankTrackMarks.TextureResource(Nation.Other, "Oth50_STT"));

            foreach (var tank in _database.Tanks)
            {
                var resource = TankTrackMarks.TextureResource(tank.Nation, tank.Name);
                Assert.IsNotNull(
                    Resources.Load<Texture2D>(resource),
                    tank.Name + " has no imported tread texture at " + resource);
            }
        }

        [Test]
        public void DistanceAndTimeBoundsFadeThenDiscardOldMarks()
        {
            Assert.AreEqual(1f, TankTrackMarks.DistanceOpacity(0f));
            Assert.AreEqual(1f, TankTrackMarks.DistanceOpacity(TankTrackMarks.DistanceFadeStart));
            Assert.AreEqual(0.5f, TankTrackMarks.DistanceOpacity(
                (TankTrackMarks.DistanceFadeStart + TankTrackMarks.MaximumTrailDistance) * 0.5f), 0.0001f);
            Assert.AreEqual(0f, TankTrackMarks.DistanceOpacity(TankTrackMarks.MaximumTrailDistance));

            Assert.IsTrue(TankTrackMarks.Retains(96f, 0f, 30f, 0f));
            Assert.IsFalse(TankTrackMarks.Retains(96.01f, 0f, 30f, 0f));
            Assert.IsFalse(TankTrackMarks.Retains(96f, 0f, 30.01f, 0f));
        }

        [Test]
        public void EveryInitializedTankOwnsOneConfiguredBoundedTrailMesh()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "track mark ground";
            ground.layer = BattleLayers.Static;
            ground.transform.SetParent(_root.transform);
            ground.transform.position = new Vector3(0f, -0.05f, 0f);
            ground.transform.localScale = new Vector3(300f, 0.1f, 300f);

            var host = new GameObject("ms-1", typeof(Rigidbody));
            host.transform.SetParent(_root.transform);
            host.transform.position = new Vector3(0f, 0.6f, 0f);
            var tank = host.AddComponent<BattleTank>();
            tank.Initialize(_database.Get("ms-1"));
            Assert.IsNotNull(tank.TrackMarks);
            Assert.AreSame(tank.TrackMarks, host.GetComponent<TankTrackMarks>());
            Assert.Greater(tank.TrackMarkWidth, 0.1f);
            Assert.Greater(tank.TrackMarkHalfSpacing, tank.TrackMarkWidth * 0.5f);

            SetWheelHits(tank.Suspension, 1);
            Physics.SyncTransforms();
            tank.TrackMarks.SimulateNow(1f);
            for (var step = 0; step < 3; step++)
            {
                host.transform.position += Vector3.forward * 0.1f;
                Physics.SyncTransforms();
                SetWheelHits(tank.Suspension, 1);
                tank.TrackMarks.SimulateNow(1.1f + step * 0.1f);
            }
            Assert.AreEqual(4, tank.TrackMarks.SampleCount,
                "three sub-sample moves must accumulate into one mark per side");
            host.transform.position += Vector3.forward * 0.35f;
            Physics.SyncTransforms();
            SetWheelHits(tank.Suspension, 1);
            tank.TrackMarks.SimulateNow(2f);

            Assert.GreaterOrEqual(tank.TrackMarks.SampleCount, 6);
            Assert.Greater(tank.TrackMarks.VertexCount, 0);
            Assert.Greater(tank.TrackMarks.TriangleCount, 0);
            Assert.IsNotNull(tank.TrackMarks.TrailMesh);
            var markRenderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(renderer => renderer.name.EndsWith("_TrackMarks", StringComparison.Ordinal));
            Assert.AreEqual(1, markRenderers, "marks must share one mesh, not one object per stamp");

            for (var i = 0; i < 420; i++)
            {
                host.transform.position += Vector3.forward * TankTrackMarks.SampleDistance;
                Physics.SyncTransforms();
                SetWheelHits(tank.Suspension, 1);
                tank.TrackMarks.SimulateNow(2.01f + i * 0.01f);
            }
            var distanceBudget = 2 * (
                Mathf.CeilToInt(TankTrackMarks.MaximumTrailDistance /
                    TankTrackMarks.SampleDistance) + 2);
            Assert.LessOrEqual(tank.TrackMarks.SampleCount, distanceBudget);
            Assert.LessOrEqual(
                tank.TrackMarks.SampleCount,
                TankTrackMarks.MaximumSamplesPerSide * 2);
            var renderersAfterDrive = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(renderer => renderer.name.EndsWith("_TrackMarks", StringComparison.Ordinal));
            Assert.AreEqual(markRenderers, renderersAfterDrive);

            tank.TrackMarks.SimulateNow(TankTrackMarks.TimeFadeEnd + 10f);
            Assert.AreEqual(0, tank.TrackMarks.SampleCount);
            Assert.AreEqual(0, tank.TrackMarks.VertexCount);
        }

        [Test]
        public void GraphicsSwitchClearsExistingMarksImmediately()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.layer = BattleLayers.Static;
            ground.transform.SetParent(_root.transform);
            ground.transform.position = new Vector3(0f, -0.05f, 0f);
            ground.transform.localScale = new Vector3(30f, 0.1f, 30f);
            var host = new GameObject("switch tank", typeof(Rigidbody));
            host.transform.SetParent(_root.transform);
            host.transform.position = new Vector3(0f, 0.6f, 0f);
            var tank = host.AddComponent<BattleTank>();
            tank.Initialize(_database.Get("ms-1"));
            SetWheelHits(tank.Suspension, 1);
            tank.TrackMarks.SimulateNow(1f);
            host.transform.position += Vector3.forward * 0.6f;
            Physics.SyncTransforms();
            SetWheelHits(tank.Suspension, 1);
            tank.TrackMarks.SimulateNow(2f);
            Assert.Greater(tank.TrackMarks.SampleCount, 0);

            _settings.GraphicsTankTreads = false;
            tank.TrackMarks.SimulateNow(2.1f);
            Assert.IsFalse(tank.TrackMarks.EnabledBySettings);
            Assert.AreEqual(0, tank.TrackMarks.SampleCount);
            Assert.AreEqual(0, tank.TrackMarks.VertexCount);
        }

        private static void SetWheelHits(TankSuspension suspension, int value)
        {
            var field = typeof(TankSuspension).GetField(
                "_wheelHits", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field);
            field.SetValue(suspension, value);
        }
    }
}
