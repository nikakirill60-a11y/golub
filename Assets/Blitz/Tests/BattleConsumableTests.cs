using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// base_tank.add_consumable / use_consumable and the
    /// shells_equipment/equipment column of ui_player.tscn.
    /// </summary>
    public sealed class BattleConsumableTests
    {
        private const float Tolerance = 1e-3f;

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
        public void ConsumableArtworkAndKeysAreInResources()
        {
            var atlas = AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData));

            // The four EquipmentKeys states of ui_consumable_icon.gd.
            foreach (var region in new[]
                     {
                         "consumable_key_empty", "consumable_key_cooling",
                         "consumable_key_ready", "consumable_key_active",
                         // BgType.GENERIC, the hangar strip frame.
                         "shell_bg_generic",
                     })
            {
                Assert.IsNotNull(atlas.Get(region), region);
            }

            // Every registered consumable brings its own artwork.
            for (var i = 0; i < ConsumableCatalog.All.Length; i++)
            {
                var item = ConsumableCatalog.All[i];
                Assert.IsNotNull(atlas.Get(item.IconRegion), item.Id);
                Assert.AreEqual(
                    item.IconRegion,
                    HangarUiKit.SlotIconRegion(item.Id, true),
                    "a consumable slot resolves its icon out of consumables_list");
            }

            // equipment_icon.init only searches consumables_list when the slot
            // is a consumable one, so an equipment slot must not find them.
            Assert.IsNull(HangarUiKit.SlotIconRegion("repair_kit", false));
            Assert.AreEqual(
                "equipment_rammer", HangarUiKit.SlotIconRegion("rammer", false));
            Assert.AreEqual(
                "equipment_rammer", HangarUiKit.SlotIconRegion("rammer", true));
            Assert.IsNull(HangarUiKit.SlotIconRegion(string.Empty, true));
        }

        [Test]
        public void PassiveConsumablesApplyOnMountAndTheRackCapsAtThree()
        {
            var tank = SpawnTank();
            var enginePower = tank.Suspension.EnginePower;
            var rotation = tank.Suspension.RotationSpeed;
            var turret = tank.TurretRotationSpeed;
            var forward = tank.Suspension.MaxSpeedForward;
            var reload = tank.GunInfo.Reload;
            var aim = tank.GunInfo.AimTime;

            tank.AddConsumable(ConsumableCatalog.Fuel);
            tank.AddConsumable(ConsumableCatalog.RationUssr);

            // cons_improved_fuel and the ration are PASSIVE: add_consumable
            // applies them straight away.
            Assert.AreEqual(enginePower * 1.1f, tank.Suspension.EnginePower, Tolerance);
            Assert.AreEqual(rotation * 1.1f, tank.Suspension.RotationSpeed, Tolerance);
            Assert.AreEqual(turret * 1.1f, tank.TurretRotationSpeed, Tolerance);
            Assert.AreEqual(forward + 3f, tank.Suspension.MaxSpeedForward, Tolerance);
            Assert.AreEqual(reload * 0.9f, tank.GunInfo.Reload, Tolerance);
            Assert.AreEqual(aim * 0.9f, tank.GunInfo.AimTime, Tolerance);

            // Global.MAX_CONSUMABLES keeps the fourth item off the tank.
            tank.AddConsumable(ConsumableCatalog.Berserk);
            Assert.AreEqual(3, tank.ActiveConsumables.Count);
            Assert.IsNull(tank.AddConsumable(ConsumableCatalog.Afterburning));
            Assert.AreEqual(3, tank.ActiveConsumables.Count);

            // A card must never be touched: tank_info is duplicated per tank.
            // The reload is read through the mounted turret, because the client
            // authors reloadTime inside every turret.
            Assert.AreEqual(
                tank.Card.GunFor(tank.TurretIndex, 0).Reload, reload / 1f, Tolerance,
                "the shared card kept its own reload");
        }

        [Test]
        public void BerserkRunsForItsUseTimeAndThenCoolsDown()
        {
            var tank = SpawnTank();
            var active = tank.AddConsumable(ConsumableCatalog.Berserk);
            Assert.IsNotNull(active);

            var reload = tank.GunInfo.Reload;
            var now = 100f;
            Assert.AreEqual(ConsumableUse.Used, active.UseTry(now, false));
            Assert.AreEqual(reload, tank.GunInfo.Reload, Tolerance, "adrenaline must not mutate gun tuning");
            Assert.AreEqual(reload * tank.ReloadPenalty * 0.75f, tank.Gun.ReloadTotal, Tolerance);
            Assert.IsTrue(active.IsActive(now + 10f));

            // already_in_use inside the 30 s window, on_cooldown after it.
            Assert.AreEqual(
                ConsumableUse.AlreadyInUse, active.UseTry(now + 10f, false));
            active.Tick(now + 31f);
            Assert.AreEqual(reload, tank.GunInfo.Reload, Tolerance);
            Assert.AreEqual(
                ConsumableUse.OnCooldown, active.UseTry(now + 31f, false));

            // use_time 30 + cooldown 90 is the full wait of the source item.
            active.Tick(now + 121f);
            Assert.IsTrue(active.Ready(now + 121f));
            Assert.AreEqual(ConsumableUse.Used, active.UseTry(now + 121f, false));
        }

        [Test]
        public void ConsumableColumnMatchesTheSourceGeometryAndStates()
        {
            var tank = SpawnTank();
            tank.AddConsumable(ConsumableCatalog.Berserk);

            var canvasObject = new GameObject("Canvas", typeof(RectTransform));
            canvasObject.transform.SetParent(_root.transform, false);
            var canvas = (RectTransform)canvasObject.transform;

            var bar = BattleConsumableBar.Create(
                canvas,
                tank,
                AtlasRegionTable.Parse(
                    BlitzResources.LoadText(BlitzResources.AtlasRegionsData)),
                BlitzResources.LoadFont(BlitzResources.BoldFontName));
            Assert.IsNotNull(bar);

            // The VBoxContainer is 186 x 638 and its three icons fill it with
            // the 32 px spacers plus Godot's own 4 px separations.
            Assert.AreEqual(186f, bar.Root.rect.width, 0.01f);
            Assert.AreEqual(638f, bar.Root.rect.height, 0.01f);
            Assert.AreEqual(226f, BattleConsumableBar.SlotStep, 0.01f);
            for (var slot = 0; slot < BattleConsumableBar.SlotCount; slot++)
            {
                var rect = (RectTransform)bar.GetIcon(slot).transform;
                Assert.AreEqual(186f, rect.rect.width, 0.01f);
                Assert.AreEqual(186f, rect.rect.height, 0.01f);
                Assert.AreEqual(
                    -slot * BattleConsumableBar.SlotStep,
                    rect.anchoredPosition.y, 0.01f);
            }

            var first = bar.GetIcon(0);
            var empty = bar.GetIcon(1);

            // A mounted item wears EquipmentKeys03 and its own artwork, an
            // unused slot the empty EquipmentKeys frame with no icon at all.
            Assert.AreEqual("consumable_key_ready", first.BackgroundImage.sprite.name);
            Assert.AreEqual("consumable_berserk", first.IconImage.sprite.name);
            Assert.IsFalse(first.TimerLabel.gameObject.activeSelf);
            Assert.AreEqual("consumable_key_empty", empty.BackgroundImage.sprite.name);
            Assert.IsFalse(empty.IconImage.enabled);

            // A press reaches use_consumable, which lights EquipmentKeys04,
            // halves the icon alpha and shows the countdown.
            bar.Use(0);
            Assert.AreEqual("consumable_key_active", first.BackgroundImage.sprite.name);
            Assert.AreEqual(0.5f, first.IconImage.color.a, Tolerance);
            Assert.IsTrue(first.TimerLabel.gameObject.activeSelf);

            // An empty slot cannot be pressed into anything.
            bar.Use(1);
            Assert.AreEqual("consumable_key_empty", empty.BackgroundImage.sprite.name);
        }

        [Test]
        public void TimerCountsTheUseWindowThenTheCooldown()
        {
            var info = ConsumableCatalog.Get(ConsumableCatalog.Berserk);
            var runtime = new ConsumableRuntime(info);
            runtime.UseTry(10f, true);

            // ui_consumable_icon: use_time - elapsed + 1 while running.
            Assert.AreEqual(31, runtime.TimerSeconds(10f));
            Assert.AreEqual(21, runtime.TimerSeconds(20f));

            // cooldown - (elapsed - use_time) + 1 afterwards.
            Assert.AreEqual(91, runtime.TimerSeconds(40f));
            Assert.AreEqual(1, runtime.TimerSeconds(130f));
        }

        private BattleTank SpawnTank()
        {
            _root = new GameObject("BattleConsumableTests");
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(BattleSession.DefaultTankId);
            Assert.IsNotNull(card);

            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);
            return BattleTank.Spawn(
                _root.transform, "PlayerMS1", card, spawn,
                new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)), 0);
        }
    }
}
