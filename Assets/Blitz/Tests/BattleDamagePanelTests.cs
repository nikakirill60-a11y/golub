using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The two damage plates of the Blitz 4.8.0 HUD: the art the porter cut
    /// out of <c>Gfx2/UI/DamagedModules</c> and <c>BattleScreenHUD</c>, and the
    /// rows the panel builds out of it.
    /// </summary>
    public sealed class BattleDamagePanelTests
    {
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

        private static AtlasRegionTable Atlas()
        {
            return AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
        }

        [Test]
        public void AtlasCarriesEveryFrameOfTheClientPanels()
        {
            var atlas = Atlas();
            var states = new[] { "ok", "damaged", "destroyed" };

            for (var i = 0; i < TankModuleIds.All.Length; i++)
            {
                for (var s = 0; s < states.Length; s++)
                {
                    var name = "damage_module_" + TankModuleIds.All[i] + "_" + states[s];
                    var sprite = atlas.Get(name);
                    Assert.IsNotNull(sprite, name);
                    Assert.AreEqual(
                        DamagePanelTuning.ModuleIconWidth, sprite.rect.width, 0.5f, name);
                    Assert.AreEqual(
                        DamagePanelTuning.ModuleIconHeight, sprite.rect.height, 0.5f, name);
                }

                Assert.IsNotNull(
                    atlas.Get("damage_enemy_" + TankModuleIds.All[i]),
                    TankModuleIds.All[i]);
            }

            for (var i = 0; i < TankCrewRoles.All.Length; i++)
            {
                for (var s = 0; s < states.Length; s++)
                {
                    var name = "damage_crew_" + TankCrewRoles.All[i] + "_" + states[s];
                    var sprite = atlas.Get(name);
                    Assert.IsNotNull(sprite, name);
                    Assert.AreEqual(
                        DamagePanelTuning.CrewIconWidth, sprite.rect.width, 0.5f, name);
                }

                Assert.IsNotNull(
                    atlas.Get("damage_enemy_" + TankCrewRoles.All[i]),
                    TankCrewRoles.All[i]);
            }

            var tankBg = atlas.Get("damage_panel_tank_bg");
            Assert.IsNotNull(tankBg);
            Assert.AreEqual(DamagePanelTuning.TankBgWidth, tankBg.rect.width, 0.5f);
            Assert.AreEqual(DamagePanelTuning.TankBgHeight, tankBg.rect.height, 0.5f);

            var crewBg = atlas.Get("damage_panel_crew_bg");
            Assert.IsNotNull(crewBg);
            Assert.AreEqual(DamagePanelTuning.CrewBgWidth, crewBg.rect.width, 0.5f);
            Assert.AreEqual(DamagePanelTuning.CrewBgHeight, crewBg.rect.height, 0.5f);

            Assert.IsNotNull(atlas.Get("damage_fire_icon"));
            Assert.IsNotNull(atlas.Get("damage_extinguisher_icon"));
        }

        [Test]
        public void PanelBuildsEveryModuleAndCrewIconUpFront()
        {
            _root = new GameObject("BattleDamagePanelTests", typeof(RectTransform));
            var panel = BattleDamagePanel.Create(
                (RectTransform)_root.transform, Atlas(), null);

            Assert.IsNotNull(panel);
            Assert.AreEqual(TankModuleIds.All.Length, panel.ModuleCount);
            Assert.AreEqual(TankCrewRoles.All.Length, panel.CrewCount);

            // visible: false until something is no longer intact.
            Assert.IsFalse(panel.TankRow.gameObject.activeSelf);
            Assert.IsFalse(panel.CrewRow.gameObject.activeSelf);

            for (var i = 0; i < TankModuleIds.All.Length; i++)
            {
                Assert.AreEqual(
                    TankModule.Status.Normal, panel.StatusOf(TankModuleIds.All[i]));
            }
        }

        [Test]
        public void PanelFollowsTheTrackModuleThroughItsThreeStates()
        {
            _root = new GameObject("BattleDamagePanelTests");
            var host = new GameObject("tank", typeof(Rigidbody));
            host.transform.SetParent(_root.transform, false);
            var tank = host.AddComponent<BattleTank>();
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            tank.Initialize(database.Get(BattleSession.DefaultTankId));

            var canvas = new GameObject("canvas", typeof(RectTransform));
            canvas.transform.SetParent(_root.transform, false);
            var atlas = Atlas();
            var panel = BattleDamagePanel.Create(
                (RectTransform)canvas.transform, atlas, tank);
            Assert.IsNotNull(panel);

            var tracks = tank.Modules.Get(TankModuleIds.Tracks);
            Assert.IsNotNull(tracks);

            tracks.ReceiveDamage(tracks.HealthComponent.MaxHealth - 1, 0f);
            panel.Refresh();
            Assert.AreEqual(
                TankModule.Status.Damaged, panel.StatusOf(TankModuleIds.Tracks));
            Assert.AreEqual(
                atlas.Get("damage_module_tracks_damaged"),
                panel.SpriteOf(TankModuleIds.Tracks));
            Assert.IsTrue(panel.TankRow.gameObject.activeSelf);

            tracks.ReceiveDamage(1000, 0f);
            panel.Refresh();
            Assert.AreEqual(
                TankModule.Status.Destroyed, panel.StatusOf(TankModuleIds.Tracks));
            Assert.AreEqual(
                atlas.Get("damage_module_tracks_destroyed"),
                panel.SpriteOf(TankModuleIds.Tracks));

            // The crew carries no health yet, so its row stays hidden.
            Assert.IsFalse(panel.CrewRow.gameObject.activeSelf);
        }

        /// <summary>
        /// The plates share the bottom edge with the health bar, so they slide
        /// out of the screen edge instead of appearing on top of it, and the
        /// stack reports how far it has come so the readouts can ride it.
        /// </summary>
        [Test]
        public void PanelSlidesTheTankPlateOutOfTheBottomEdge()
        {
            _root = new GameObject("BattleDamagePanelTests", typeof(RectTransform));
            var panel = BattleDamagePanel.Create(
                (RectTransform)_root.transform, Atlas(), null);
            Assert.IsNotNull(panel);
            Assert.AreEqual(0f, panel.StackHeight, 0.001f);

            var row = panel.TankRow;
            var height = DamagePanelTuning.TankBgHeight * DamagePanelTuning.PanelScale;
            var rest = DamagePanelTuning.TankRowBottom;

            panel.Show(true, false);
            Assert.IsTrue(row.gameObject.activeSelf);
            Assert.AreEqual(-(rest + height), row.anchoredPosition.y, 0.01f,
                "the plate starts parked below the screen edge");

            panel.Advance(DamagePanelTuning.SlideDuration * 0.5f);
            Assert.Greater(row.anchoredPosition.y, -(rest + height));
            Assert.Less(row.anchoredPosition.y, rest);
            Assert.Greater(panel.StackHeight, 0f);

            panel.Advance(DamagePanelTuning.SlideDuration);
            Assert.AreEqual(rest, row.anchoredPosition.y, 0.01f);
            Assert.AreEqual(
                rest + height + DamagePanelTuning.StackPadding,
                panel.StackHeight, 0.01f);

            // Repairs send it back out of the edge and free the health plate.
            panel.Show(false, false);
            panel.Advance(DamagePanelTuning.SlideDuration);
            Assert.AreEqual(0f, panel.StackHeight, 0.001f);
            Assert.IsFalse(row.gameObject.activeSelf);
        }

        [Test]
        public void PanelStringsComeFromTheClientTable()
        {
            var lang = BlitzLegacy.Core.Localization.LocalizationTable.Parse(
                BlitzResources.LoadText(BlitzResources.LanguageData));

            for (var i = 0; i < TankModuleIds.All.Length; i++)
            {
                var key = TankModuleIds.LabelKey(TankModuleIds.All[i]);
                Assert.AreNotEqual(key, lang.Get(key), key);
            }

            for (var i = 0; i < TankCrewRoles.All.Length; i++)
            {
                var key = TankCrewRoles.LabelKey(TankCrewRoles.All[i]);
                Assert.AreNotEqual(key, lang.Get(key), key);
            }

            Assert.AreNotEqual("track_destroyed", lang.Get("track_destroyed"));
            Assert.AreNotEqual("ammo_bay_damaged", lang.Get("ammo_bay_damaged"));
            Assert.AreNotEqual("vehicle_exploded", lang.Get("vehicle_exploded"));
        }
    }
}
