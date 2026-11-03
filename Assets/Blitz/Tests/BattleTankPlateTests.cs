using System.Reflection;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class BattleTankPlateTests
    {
        private const float Tolerance = 1e-4f;
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
        public void ExternalPlateAtlasCarriesTheSourceRegions()
        {
            var atlas = AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            var names = new[]
            {
                "battle_otm_hp_back",
                "battle_otm_hp_under",
                "battle_otm_hp_top",
                "battle_otm_class_light",
                "battle_otm_class_medium",
                "battle_otm_class_heavy",
                "battle_otm_class_td",
                "battle_otm_class_shadow",
                "battle_otm_level_1",
                "battle_otm_level_10",
                "battle_otm_name_shadow",
            };

            for (var i = 0; i < names.Length; i++)
            {
                var sprite = atlas.Get(names[i]);
                Assert.IsNotNull(sprite, names[i]);
                Assert.AreEqual(names[i], sprite.name);
            }
        }

        [Test]
        public void ProjectionUsesCurrentCameraSpaceAndIgnoresHudParentMotion()
        {
            _root = new GameObject("BattleTankPlateProjectionTests");
            var hudParent = new GameObject("MovingHudParent").transform;
            hudParent.SetParent(_root.transform, false);
            var canvasObject = new GameObject("Canvas", typeof(RectTransform));
            canvasObject.transform.SetParent(hudParent, false);
            var canvas = (RectTransform)canvasObject.transform;
            canvas.sizeDelta = new Vector2(1040f, 550f);

            var cameraObject = new GameObject("Camera", typeof(Camera));
            cameraObject.transform.SetParent(_root.transform, false);
            var camera = cameraObject.GetComponent<Camera>();
            camera.aspect = 16f / 9f;
            camera.fieldOfView = 54f;
            camera.transform.SetPositionAndRotation(
                new Vector3(5f, 8f, -20f), Quaternion.Euler(8f, 15f, 0f));

            var world = camera.transform.position
                + camera.transform.forward * 100f
                + camera.transform.right * 12f
                + camera.transform.up * 4f;
            Vector2 first;
            Assert.IsTrue(BattleTankPlate.TryProjectToCanvas(
                camera, canvas, world, out first));

            var viewport = camera.WorldToViewportPoint(world);
            Assert.AreEqual(
                Mathf.LerpUnclamped(canvas.rect.xMin, canvas.rect.xMax, viewport.x),
                first.x, Tolerance);
            Assert.AreEqual(
                Mathf.LerpUnclamped(canvas.rect.yMin, canvas.rect.yMax, viewport.y),
                first.y, Tolerance);

            // Moving downhill translates the followed camera and the HUD owner
            // in the same frame. Neither motion may alter the projected pixel.
            var translation = new Vector3(17f, -6f, 9f);
            camera.transform.position += translation;
            world += translation;
            hudParent.SetPositionAndRotation(
                new Vector3(-31f, 14f, 22f), Quaternion.Euler(24f, 71f, -18f));

            Vector2 moved;
            Assert.IsTrue(BattleTankPlate.TryProjectToCanvas(
                camera, canvas, world, out moved));
            Assert.AreEqual(first.x, moved.x, Tolerance);
            Assert.AreEqual(first.y, moved.y, Tolerance);

            Vector2 behind;
            Assert.IsFalse(BattleTankPlate.TryProjectToCanvas(
                camera, canvas, camera.transform.position - camera.transform.forward,
                out behind));
        }

        [Test]
        public void EnemyPlateUsesSourceGeometryAndTracksActualHealth()
        {
            _root = new GameObject("BattleTankPlateTests");
            var canvasObject = new GameObject("Canvas", typeof(RectTransform));
            canvasObject.transform.SetParent(_root.transform, false);
            var canvas = (RectTransform)canvasObject.transform;

            var card = LoadMs1();
            var playerSpawn = Spawn("PlayerSpawn", new Vector3(0f, 10f, 0f));
            var enemySpawn = Spawn("EnemySpawn", new Vector3(0f, 10f, 20f));
            var area = new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f));
            var player = BattleTank.Spawn(
                _root.transform, "PlayerMS1", card, playerSpawn, area, 0);
            var enemy = BattleTank.Spawn(
                _root.transform, "EnemyMS1", card, enemySpawn, area, 1);

            var atlas = AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            var plate = BattleTankPlate.Create(
                canvas,
                enemy,
                player,
                null,
                atlas,
                BlitzResources.LoadFont(BlitzResources.BoldFontName),
                card.DisplayNameShort);

            Assert.IsNotNull(plate);
            Assert.AreSame(enemy, plate.Tank);
            Assert.AreEqual(TankPlateTuning.RootScale, plate.Root.localScale.x, Tolerance);
            Assert.AreEqual(TankPlateTuning.RootScale, plate.Root.localScale.y, Tolerance);
            Assert.AreEqual("battle_otm_hp_top", plate.TopBar.sprite.name);
            Assert.AreEqual("battle_otm_hp_under", plate.UnderBar.sprite.name);
            Assert.AreEqual("battle_otm_class_light", plate.ClassIcon.sprite.name);
            Assert.AreEqual(0.96f, plate.TopBar.color.r, Tolerance);
            Assert.AreEqual(0f, plate.TopBar.color.g, Tolerance);
            Assert.AreEqual(1f, plate.TopBar.fillAmount, Tolerance);
            Assert.AreEqual("200/200", plate.HealthLabel.text);
            Assert.AreEqual(card.DisplayNameShort, plate.NameLabel.text);

            enemy.Health.ReceiveDamageBasic(40);
            Assert.AreEqual(160f / 200f, plate.TopBar.fillAmount, Tolerance);
            Assert.AreEqual(1f, plate.UnderBar.fillAmount, Tolerance);
            Assert.AreEqual("160/200", plate.HealthLabel.text);
            Assert.AreEqual("-40", plate.DamageLabel.text);

            enemy.Health.ReceiveDamageBasic(10);
            Assert.AreEqual(150f / 200f, plate.TopBar.fillAmount, Tolerance);
            Assert.AreEqual("-50", plate.DamageLabel.text);
            Assert.AreEqual(Color.white, plate.ClassIcon.color,
                "damage starts the source white class flash");

            var now = Time.unscaledTime;
            plate.AdvanceAnimations(0f, now);
            Assert.AreEqual(1f, plate.DamageLabel.color.a, Tolerance);
            plate.AdvanceAnimations(0.5f, now + 0.25f);
            Assert.AreEqual(150f / 200f, plate.UnderBar.fillAmount, Tolerance,
                "the delayed underbar catches up with the source ×2 rate");
            Assert.Greater(plate.ClassIcon.color.g, plate.TopBar.color.g,
                "the class icon is still fading out of white at 0.25 seconds");

            plate.AdvanceAnimations(
                0f,
                now + TankPlateTuning.DamageVisibleTime
                    + TankPlateTuning.DamageFadeTime * 0.5f);
            Assert.AreEqual(0.5f, plate.DamageLabel.color.a, 0.01f);
            plate.AdvanceAnimations(
                0f,
                now + TankPlateTuning.DamageVisibleTime
                    + TankPlateTuning.DamageFadeTime + 0.1f);
            Assert.AreEqual(0f, plate.DamageLabel.color.a, Tolerance);
            Assert.AreEqual(plate.TopBar.color, plate.ClassIcon.color,
                "the class icon returns to its team colour");

            var allySpawn = Spawn("AllySpawn", new Vector3(20f, 10f, 0f));
            var ally = BattleTank.Spawn(
                _root.transform, "AllyMS1", card, allySpawn, area, 0);
            var allyPlate = BattleTankPlate.Create(
                canvas,
                ally,
                player,
                null,
                atlas,
                BlitzResources.LoadFont(BlitzResources.BoldFontName),
                card.DisplayNameShort);
            Assert.AreEqual(0.396f, allyPlate.TopBar.color.r, Tolerance);
            Assert.AreEqual(0.867f, allyPlate.TopBar.color.g, Tolerance);
            Assert.AreEqual(0f, allyPlate.TopBar.color.b, Tolerance);

            var group = plate.GetComponent<CanvasGroup>();
            group.alpha = 1f;
            Assert.IsTrue(plate.Visible);
            var liveName = plate.NameLabel.color;
            enemy.Health.ReceiveDamageBasic(10000);
            Assert.IsTrue(plate.Visible,
                "ui_tank_external keeps a wreck's plate on screen");
            Assert.AreEqual(0f, plate.TopBar.fillAmount, Tolerance,
                "the wreck's bar reads empty");
            Assert.AreEqual("0/200", plate.HealthLabel.text);
            Assert.AreEqual(liveName.r * 0.5f, plate.NameLabel.color.r, Tolerance,
                "get_team_modulate(true) halves the team tint on the nick");
            Assert.AreEqual(liveName.g * 0.5f, plate.NameLabel.color.g, Tolerance);
            Assert.AreEqual(1f, plate.NameLabel.color.a, Tolerance,
                "and puts the alpha straight back to one");

            Assert.IsNull(BattleTankPlate.Create(
                canvas, player, player, null, atlas, null, card.DisplayNameShort));
        }

        [Test]
        public void ThanatosShowsZeroHpVisuallyUntilTheAbilityEnds()
        {
            _root = new GameObject("BattleTankPlateThanatosTests");
            var canvasObject = new GameObject("Canvas", typeof(RectTransform));
            canvasObject.transform.SetParent(_root.transform, false);
            var canvas = (RectTransform)canvasObject.transform;

            var card = LoadMs1();
            var playerSpawn = Spawn("PlayerSpawn", new Vector3(0f, 10f, 0f));
            var enemySpawn = Spawn("EnemySpawn", new Vector3(0f, 10f, 20f));
            var area = new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f));
            var player = BattleTank.Spawn(
                _root.transform, "PlayerMS1", card, playerSpawn, area, 0);
            var enemy = BattleTank.Spawn(
                _root.transform, "EnemyMS1", card, enemySpawn, area, 1);
            enemy.Health.ReceiveDamageBasic(40);

            var atlas = AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            var plate = BattleTankPlate.Create(
                canvas,
                enemy,
                player,
                null,
                atlas,
                BlitzResources.LoadFont(BlitzResources.BoldFontName),
                card.DisplayNameShort);
            Assert.IsNotNull(plate);
            Assert.AreEqual("160/200", plate.HealthLabel.text);

            var uprising = enemy.gameObject.AddComponent<UprisingTank>();
            var state = new UprisingState(
                new UprisingCatalog(BlitzResources.LoadText("uprising_abilities")),
                "pretendDead");
            typeof(UprisingTank).GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(uprising, state);
            typeof(UprisingTank).GetField("<Tank>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(uprising, enemy);
            // UprisingTank.Attach wires the hull's controller the same way.
            typeof(BattleTank).GetProperty("Uprising").SetValue(enemy, uprising);
            state.ActiveUntil = Time.time + 10f;

            typeof(BattleTankPlate).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(plate, null);
            Assert.AreEqual(enemy.Health.Health, enemy.DisplayHealth);
            Assert.AreEqual("0/200", plate.HealthLabel.text);
            Assert.AreEqual(0f, plate.TopBar.fillAmount, Tolerance);

            state.ActiveUntil = -1f;
            typeof(BattleTankPlate).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(plate, null);
            Assert.AreEqual(enemy.Health.Health, enemy.DisplayHealth);
            Assert.AreEqual("160/200", plate.HealthLabel.text);
            Assert.AreEqual(160f / 200f, plate.TopBar.fillAmount, Tolerance);
        }

        private Transform Spawn(string name, Vector3 position)
        {
            var spawn = new GameObject(name).transform;
            spawn.SetParent(_root.transform, false);
            spawn.position = position;
            return spawn;
        }

        private static TankInfo LoadMs1()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(BattleSession.DefaultTankId);
            Assert.IsNotNull(card);
            return card;
        }
    }
}
