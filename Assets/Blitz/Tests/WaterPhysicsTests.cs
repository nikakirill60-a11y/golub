using System.Linq;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>Buoyancy, drag, the drowning countdown and its HUD.</summary>
    public sealed class WaterPhysicsTests
    {
        private GameObject _root;
        private TanksDatabase _db;

        [SetUp]
        public void Prepare()
        {
            _root = new GameObject("water tests");
            _db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            BattleGun.Clock = () => 100f;
        }

        [TearDown]
        public void Cleanup()
        {
            BattleGun.Clock = null;
            BattleWater.SetActive(null);
            Object.DestroyImmediate(_root);
        }

        private BattleTank Tank(string id = "ms-1", float y = 0f)
        {
            var go = new GameObject(id, typeof(Rigidbody));
            go.transform.SetParent(_root.transform);
            go.transform.position = new Vector3(0f, y, 0f);
            var tank = go.AddComponent<BattleTank>();
            tank.Initialize(_db.Get(id));
            Physics.SyncTransforms();
            return tank;
        }

        private GameObject Plane(string shader, float height, float half = 50f)
        {
            var go = new GameObject("water plane " + shader);
            go.transform.SetParent(_root.transform);
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(-half, height, -half), new Vector3(-half, height, half),
                new Vector3(half, height, half), new Vector3(half, height, -half),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var found = Shader.Find(shader);
            Assert.IsNotNull(found, "missing shader " + shader);
            go.AddComponent<MeshRenderer>().sharedMaterial = new Material(found);
            return go;
        }

        private BattleWater Water(float height)
        {
            var field = Plane(BattleObjects.WaterShaderName, height);
            var water = BattleWater.Build(field.transform);
            Assert.IsNotNull(water);
            return water;
        }

        private static void Run(TankWaterPhysics physics, float seconds, float dt = 0.02f)
        {
            for (var t = 0f; t < seconds; t += dt) { physics.Step(dt); }
        }

        [Test]
        public void RulesKeepSubmersionDragAndSpeedBounded()
        {
            Assert.AreEqual(0f, WaterRules.Submersion(0f, 2f, -1f));
            Assert.AreEqual(0.5f, WaterRules.Submersion(0f, 2f, 1f), 1e-5f);
            Assert.AreEqual(1f, WaterRules.Submersion(0f, 2f, 5f));
            Assert.AreEqual(1f, WaterRules.SpeedFactor(0f));
            Assert.AreEqual(1f - WaterRules.MaxSpeedLoss, WaterRules.SpeedFactor(1f), 1e-5f);
            Assert.Less(WaterRules.SpeedFactor(0.6f), WaterRules.SpeedFactor(0.3f));
            // Drag opposes motion and a single step can never reverse it.
            var drag = WaterRules.DragAcceleration(10f, WaterRules.SideDragGain, 1f, 0.02f);
            Assert.Less(drag, 0f);
            Assert.GreaterOrEqual(10f + drag * 0.02f, 0f);
            Assert.Greater(WaterRules.DragAcceleration(-3f, WaterRules.ForwardDragGain, 1f, 0.02f), 0f);
            Assert.AreEqual(0f, WaterRules.DragAcceleration(5f, WaterRules.ForwardDragGain, 0f, 0.02f));
            // A steel hull always sinks: buoyancy carries only a share of gravity.
            Assert.Less(WaterRules.BuoyancyAcceleration(1f, 9.81f), 9.81f * 0.5f);
            Assert.Greater(WaterRules.BuoyancyAcceleration(1f, 9.81f), WaterRules.BuoyancyAcceleration(0.4f, 9.81f));
            // The water level is a share of the flood line, clamped to it.
            Assert.AreEqual(0.5f, WaterRules.FloodLevel(0.6f, 1.2f), 1e-5f);
            Assert.AreEqual(1f, WaterRules.FloodLevel(3f, 1.2f));
            Assert.AreEqual(0f, WaterRules.FloodLevel(-1f, 1.2f));
            Assert.That(WaterRules.IndicatorLevel, Is.InRange(0.1f, 0.6f));
        }

        [Test]
        public void SplashFollowsVehicleXmlWaterContactSpeeds()
        {
            Assert.AreEqual(0, WaterRules.SplashLevel(5.4f));
            Assert.AreEqual(1, WaterRules.SplashLevel(5.5f));
            Assert.AreEqual(2, WaterRules.SplashLevel(7f));
        }

        [Test]
        public void TimerTextIsTheOriginalMinutesSecondsRoundedUp()
        {
            Assert.AreEqual("00:10", WaterRules.FormatTimer(10f));
            Assert.AreEqual("00:10", WaterRules.FormatTimer(9.2f));
            Assert.AreEqual("00:01", WaterRules.FormatTimer(0.3f));
            Assert.AreEqual("00:00", WaterRules.FormatTimer(0f));
            Assert.AreEqual("01:05", WaterRules.FormatTimer(65f));
            Assert.AreEqual("00:00", WaterRules.FormatTimer(-3f));
        }

        [Test]
        public void DrowningTimerRunsOnlyWhileFloodedAndFiresOnce()
        {
            var timer = new DrowningTimer();
            Assert.AreEqual(WaterRules.DrownSeconds, timer.Duration);
            Assert.IsFalse(timer.Tick(false, 5f));
            Assert.IsFalse(timer.Running);
            Assert.IsFalse(timer.Tick(true, 6f));
            Assert.IsTrue(timer.Running);
            Assert.AreEqual(4f, timer.Remaining, 1e-4f);
            // Surfacing resets the countdown in full.
            timer.Tick(false, 0.1f);
            Assert.AreEqual(timer.Duration, timer.Remaining);
            Assert.IsFalse(timer.Tick(true, 9.9f));
            Assert.IsTrue(timer.Tick(true, 0.2f));
            Assert.IsTrue(timer.Expired);
            Assert.IsFalse(timer.Tick(true, 1f), "the countdown fired twice");
            timer.Reset();
            Assert.IsFalse(timer.Expired);
            Assert.AreEqual(timer.Duration, timer.Remaining);
        }

        [Test]
        public void SurfaceFieldSamplesUpFacingTrianglesAndPrefersTheHighest()
        {
            var field = new WaterSurfaceField();
            Assert.IsTrue(field.AddTriangle(0, 1, 0, 0, 1, 10, 10, 1, 0));
            Assert.IsFalse(field.AddTriangle(0, 0, 0, 0, 5, 0, 10, 0, 0), "a wall is not a water surface");
            Assert.IsTrue(field.AddTriangle(0, 3, 0, 0, 3, 4, 4, 3, 0));
            field.Build(2f);
            float h;
            Assert.IsTrue(field.TrySample(1f, 1f, out h));
            Assert.AreEqual(3f, h, 1e-4f);
            Assert.IsTrue(field.TrySample(6f, 2f, out h));
            Assert.AreEqual(1f, h, 1e-4f);
            Assert.IsFalse(field.TrySample(9f, 9f, out h));
            Assert.IsFalse(field.TrySample(-1f, 1f, out h));
            Assert.AreEqual(2, field.TriangleCount);
        }

        [Test]
        public void WaterIsCollectedOnlyFromWaterShaders()
        {
            Assert.IsNull(BattleWater.Build(Plane("Sprites/Default", 4f).transform));
            foreach (var shader in new[]
                { BattleObjects.WaterShaderName, ClientMapScene.WaterShader, ClientMapScene.GodotWaterShader })
            {
                var water = BattleWater.Build(Plane(shader, 4f).transform);
                Assert.IsNotNull(water, shader);
                float h;
                Assert.IsTrue(water.TrySample(new Vector3(3f, 0f, -7f), out h));
                Assert.AreEqual(4f, h, 1e-4f);
                Assert.AreEqual(1, water.RendererCount);
            }
        }

        [Test]
        public void DryTankIsUntouched()
        {
            var tank = Tank();
            var physics = tank.Water;
            Assert.IsNotNull(physics);
            physics.UseWater(Water(-20f));
            Run(physics, 1f);
            Assert.IsFalse(physics.InWater);
            Assert.IsFalse(physics.Flooded);
            Assert.AreEqual(0f, physics.Submersion);
            Assert.AreEqual(1f, tank.Suspension.EnvironmentSpeedFactor);
        }

        [Test]
        public void FordingTankIsLightenedAndSlowedButNotFlooded()
        {
            var tank = Tank();
            var physics = tank.Water;
            var hull = tank.Hull;
            var surface = tank.transform.TransformPoint(new Vector3(0f, (hull.TracksBottom + hull.Height) * 0.5f, 0f)).y;
            physics.UseWater(Water(surface));
            physics.Step(0.02f);
            Assert.IsTrue(physics.InWater);
            Assert.IsFalse(physics.Flooded);
            Assert.That(physics.Submersion, Is.InRange(0.3f, 0.7f));
            Assert.Greater(physics.LastBuoyancy, 0f);
            Assert.Less(physics.LastBuoyancy, WaterRules.BuoyancyAcceleration(1f, Physics.gravity.magnitude));
            Assert.Less(tank.Suspension.EnvironmentSpeedFactor, 1f);
            Assert.Greater(tank.Suspension.EnvironmentSpeedFactor, 1f - WaterRules.MaxSpeedLoss);
            Run(physics, WaterRules.DrownSeconds + 1f);
            Assert.IsFalse(tank.Dead, "a hull with its roof above water must not drown");
        }

        [Test]
        public void FloodedTankDrownsAfterTheCountdown()
        {
            var tank = Tank();
            var physics = tank.Water;
            physics.UseWater(Water(50f));
            var drowned = 0;
            physics.Drowned += _ => drowned++;
            physics.Step(0.02f);
            Assert.IsTrue(physics.Flooded);
            Assert.IsTrue(physics.GunUnderWater);
            Assert.IsTrue(physics.TimerVisible);
            Run(physics, WaterRules.DrownSeconds - 0.5f);
            Assert.IsFalse(tank.Dead, "drowned before the countdown ran out");
            Run(physics, 1f);
            Assert.IsTrue(tank.Dead);
            Assert.AreEqual(1, drowned);
            Assert.AreEqual(0, tank.Health.Health);
            Assert.AreEqual(DamageType.Drowning, tank.Health.DamageInfosTaken.Last().DmgType);
            Assert.IsFalse(physics.TimerVisible, "a wreck keeps no countdown");
        }

        [Test]
        public void SurfacingResetsTheCountdownAndRespawnClearsIt()
        {
            var tank = Tank();
            var physics = tank.Water;
            physics.UseWater(Water(50f));
            Run(physics, 6f);
            Assert.Less(physics.Timer.Remaining, 5f);
            physics.UseWater(Water(-50f));
            physics.Step(0.02f);
            Assert.IsFalse(physics.Flooded);
            Assert.AreEqual(physics.Timer.Duration, physics.Timer.Remaining);
            physics.UseWater(Water(50f));
            Run(physics, 3f);
            tank.RestoreLife(null);
            Assert.IsFalse(physics.Timer.Running);
            Assert.AreEqual(physics.Timer.Duration, physics.Timer.Remaining);
        }

        [Test]
        public void NetworkClientShowsTheCountdownButLeavesTheKillToTheHost()
        {
            var tank = Tank();
            var physics = tank.Water;
            physics.UseWater(Water(50f));
            BattleNetHooks.PuppetWorld = true;
            try
            {
                Run(physics, WaterRules.DrownSeconds + 1f);
                Assert.IsTrue(physics.Timer.Expired);
                Assert.IsFalse(tank.Dead);
            }
            finally
            {
                BattleNetHooks.PuppetWorld = false;
            }
        }

        [Test]
        public void GunUnderWaterCannotShoot()
        {
            var tank = Tank();
            var physics = tank.Water;
            var gun = tank.Gun;
            gun.Reload(-1f);
            Assume.That(gun.CanShoot(), "the test gun is not loaded");
            var shots = 0;
            gun.Shoot += _ => shots++;
            var blocked = 0;
            physics.ShotBlocked += _ => blocked++;
            physics.UseWater(Water(50f));
            physics.Step(0.02f);
            gun.ShootAttempt();
            Assert.AreEqual(0, shots);
            Assert.AreEqual(1, blocked);
            Assert.IsTrue(gun.CanShoot(), "a blocked trigger must keep the shell loaded");
            physics.UseWater(Water(-50f));
            physics.Step(0.02f);
            gun.ShootAttempt();
            Assert.AreEqual(1, shots);
        }

        [Test]
        public void HudShowsTheOriginalCountdownAndMessages()
        {
            var tank = Tank();
            var physics = tank.Water;
            var canvas = new GameObject("canvas", typeof(RectTransform), typeof(Canvas));
            canvas.transform.SetParent(_root.transform);
            var atlas = AtlasRegionTable.Parse(BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            var lang = LocalizationTable.Parse(BlitzResources.LoadText(BlitzResources.LanguageData));
            lang.Language = "ru";
            var font = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            var hud = BattleWaterIndicator.Create((RectTransform)canvas.transform, atlas, lang, font, tank);
            Assert.IsFalse(hud.TimerShown);
            Assert.IsNotNull(hud.Ring.sprite);

            physics.UseWater(Water(50f));
            physics.Step(0.02f);
            hud.Refresh();
            Assert.IsTrue(hud.TimerShown);
            Assert.AreEqual("00:10", hud.TimerText.text);
            Assert.AreEqual(BattleWaterIndicator.TimerColour, hud.TimerText.color);
            Run(physics, 4.5f);
            hud.Refresh();
            Assert.AreEqual("00:06", hud.TimerText.text);
            Assert.That(hud.Ring.fillAmount, Is.InRange(0.5f, 0.6f));

            tank.Gun.Reload(-1f);
            tank.Gun.ShootAttempt();
            Assert.IsTrue(hud.NoticeShown);
            Assert.AreEqual(lang.Get(BattleWaterIndicator.GunUnderWaterKey, "ru"), hud.NoticeText.text);

            hud.SetCombatVisible(false);
            Assert.IsFalse(hud.TimerShown);
            hud.SetCombatVisible(true);
            Run(physics, 6f);
            hud.Refresh();
            Assert.IsTrue(tank.Dead);
            Assert.IsFalse(hud.TimerShown);
            Assert.AreEqual(lang.Get(BattleWaterIndicator.DrownKey, "ru"), hud.NoticeText.text);
        }

        [Test]
        public void HudShowsTheTankInWaterBeforeTheCountdown()
        {
            var tank = Tank("renaultue57");
            var physics = tank.Water;
            var hull = tank.Hull;
            var bottom = hull.TracksBottom;
            var roof = Mathf.Max(hull.Height, bottom + 0.5f);
            var canvas = new GameObject("canvas", typeof(RectTransform), typeof(Canvas));
            canvas.transform.SetParent(_root.transform);
            var atlas = AtlasRegionTable.Parse(BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            var lang = LocalizationTable.Parse(BlitzResources.LoadText(BlitzResources.LanguageData));
            var font = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            var hud = BattleWaterIndicator.Create((RectTransform)canvas.transform, atlas, lang, font, tank);

            // Water over the tracks only: the tank is in water but the HUD stays clear.
            physics.UseWater(Water(bottom + (roof - bottom) * 0.15f));
            physics.Step(0.02f);
            hud.Refresh();
            Assert.IsTrue(physics.InWater);
            Assert.IsFalse(physics.IndicatorVisible);
            Assert.IsFalse(hud.Shown);

            // The river under the bridge of the asia map: 87 % of the UE 57
            // hull under water, roof still dry. The plate shows the tank in
            // water, its ring at the water level, without a countdown.
            physics.UseWater(Water(bottom + (roof - bottom) * 0.87f));
            physics.Step(0.02f);
            hud.Refresh();
            Assert.IsFalse(physics.Flooded);
            Assert.IsFalse(physics.Timer.Running);
            Assert.That(physics.Level, Is.InRange(0.85f, 0.89f));
            Assert.IsTrue(physics.IndicatorVisible);
            Assert.IsTrue(hud.WadingShown);
            Assert.IsFalse(hud.TimerShown);
            Assert.AreEqual(physics.Level, hud.Ring.fillAmount, 1e-4f);

            // The roof goes under: the same plate switches to the countdown.
            physics.UseWater(Water(roof + 0.3f));
            physics.Step(0.02f);
            hud.Refresh();
            Assert.IsTrue(physics.Flooded);
            Assert.AreEqual(1f, physics.Level);
            Assert.IsTrue(hud.TimerShown);
            Assert.IsFalse(hud.WadingShown);
            Assert.AreEqual("00:10", hud.TimerText.text);

            // Out of the water: the plate goes away and the countdown resets.
            physics.UseWater(Water(-50f));
            physics.Step(0.02f);
            hud.Refresh();
            Assert.IsFalse(hud.Shown);
            Assert.IsFalse(physics.Timer.Running);
        }

        [Test]
        public void OriginalStringsSpritesAndEffectsAreShipped()
        {
            var lang = LocalizationTable.Parse(BlitzResources.LoadText(BlitzResources.LanguageData));
            Assert.AreEqual("Техника затоплена.", lang.Get(BattleWaterIndicator.DrownKey, "ru"));
            Assert.AreEqual("Vehicle drowned.", lang.Get(BattleWaterIndicator.DrownKey, "en"));
            Assert.AreEqual("Стрельба невозможна: техника под водой.", lang.Get(BattleWaterIndicator.GunUnderWaterKey, "ru"));
            Assert.AreNotEqual(BattleWaterIndicator.GunUnderWaterKey, lang.Get(BattleWaterIndicator.GunUnderWaterKey, "en"));

            var atlas = AtlasRegionTable.Parse(BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            for (var i = 0; i < 3; i++) { Assert.IsNotNull(atlas.Get("water_indicator_" + i), "water_indicator_" + i); }

            foreach (var scene in new[] { "water_tank_splash", "water_tank_splash_low", "wave_water_s", "wave_water_m", "wave_water_l" })
            {
                Assert.IsNotNull(Resources.Load<TextAsset>("Legacy480/Sources/" + scene), scene);
            }
        }
    }
}
