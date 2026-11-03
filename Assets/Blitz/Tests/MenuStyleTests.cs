using System.Linq;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Tests
{
    /// <summary>The Classic/Modern menu switch, its carousel rules and the Modern skin.</summary>
    public sealed class MenuStyleTests
    {
        [TearDown]
        public void TearDown()
        {
            MenuStyle.ToolOverride = null;
        }

        [Test]
        public void OldSavesHaveNoStyleAndAskForThePicker()
        {
            var settings = SettingsData.FromJson(JsonNode.Parse("{\"language\":\"ru\"}"));
            Assert.AreEqual(SettingsData.UiStyleUnset, settings.UiStyle);
            Assert.AreEqual(SettingsData.CarouselSizeNormal, settings.UiCarouselSize);
            Assert.IsTrue(MenuStyleRules.NeedsPicker(settings));
            Assert.IsFalse(MenuStyleRules.IsModern(settings, null), "an unset style keeps the classic menus");
        }

        [Test]
        public void StyleAndCarouselSizeRoundTrip()
        {
            var settings = new SettingsData();
            settings.SetInt("ui_style", SettingsData.UiStyleModern);
            settings.SetInt("ui_carousel_size", SettingsData.CarouselSizeLarge);
            var back = SettingsData.FromJson(JsonNode.Parse(settings.ToJson().ToString()));
            Assert.AreEqual(SettingsData.UiStyleModern, back.UiStyle);
            Assert.AreEqual(SettingsData.CarouselSizeLarge, back.UiCarouselSize);
            Assert.IsFalse(MenuStyleRules.NeedsPicker(back));
            Assert.IsTrue(MenuStyleRules.IsModern(back, null));
        }

        [Test]
        public void OutOfRangeValuesAreClamped()
        {
            var settings = new SettingsData();
            settings.SetInt("ui_style", 9);
            settings.SetInt("ui_carousel_size", -4);
            Assert.AreEqual(SettingsData.UiStyleModern, settings.GetInt("ui_style"));
            Assert.AreEqual(SettingsData.CarouselSizeCompact, settings.GetInt("ui_carousel_size"));
        }

        [Test]
        public void EnvironmentOverrideWins()
        {
            var settings = new SettingsData { UiStyle = SettingsData.UiStyleClassic };
            Assert.IsTrue(MenuStyleRules.IsModern(settings, "Modern"));
            settings.UiStyle = SettingsData.UiStyleModern;
            Assert.IsFalse(MenuStyleRules.IsModern(settings, " classic "));
            Assert.IsTrue(MenuStyleRules.IsModern(settings, "bogus"));
        }

        [Test]
        public void CarouselMetricsGrowWithSizeAndShrinkOnNarrowScreens()
        {
            var compact = MenuStyleRules.Metrics(SettingsData.CarouselSizeCompact, 2f);
            var normal = MenuStyleRules.Metrics(SettingsData.CarouselSizeNormal, 2f);
            var large = MenuStyleRules.Metrics(SettingsData.CarouselSizeLarge, 2f);
            Assert.Less(compact.BarHeight, normal.BarHeight);
            Assert.Less(normal.BarHeight, large.BarHeight);
            Assert.Less(large.CardHeight, large.BarHeight);
            var tablet = MenuStyleRules.Metrics(SettingsData.CarouselSizeLarge, 4f / 3f);
            Assert.AreEqual(compact.BarHeight, tablet.BarHeight);
        }

        [Test]
        public void CarouselModesReserveTheBottomEdge()
        {
            var metrics = MenuStyleRules.Metrics(SettingsData.CarouselSizeNormal, 2f);
            Assert.AreEqual(metrics.BarHeight, MenuStyleRules.OccupiedHeight(CarouselMode.Normal, metrics));
            Assert.AreEqual(metrics.BarHeight, MenuStyleRules.OccupiedHeight(CarouselMode.Locked, metrics));
            Assert.AreEqual(MenuStyleRules.HandleHeight, MenuStyleRules.OccupiedHeight(CarouselMode.Collapsed, metrics));
            Assert.AreEqual(0f, MenuStyleRules.OccupiedHeight(CarouselMode.Hidden, metrics));
            Assert.IsTrue(MenuStyleRules.AcceptsSelection(CarouselMode.Normal));
            Assert.IsTrue(MenuStyleRules.AcceptsSelection(CarouselMode.Expanded));
            Assert.IsFalse(MenuStyleRules.AcceptsSelection(CarouselMode.Locked));
            Assert.IsFalse(MenuStyleRules.AcceptsSelection(CarouselMode.Collapsed));
        }

        [Test]
        public void OpenBurgerMenuFoldsTheMainCarousel()
        {
            Assert.AreEqual(CarouselMode.Normal, MenuStyleRules.MainCarouselMode(false));
            Assert.AreEqual(CarouselMode.Collapsed, MenuStyleRules.MainCarouselMode(true),
                "the drawer reaches the bottom edge, so the carousel must fold out of its way");
            Assert.IsFalse(MenuStyleRules.AcceptsSelection(MenuStyleRules.MainCarouselMode(true)),
                "no card can be tapped through the open drawer");
            var metrics = MenuStyleRules.Metrics(SettingsData.CarouselSizeLarge, 2f);
            Assert.Less(MenuStyleRules.OccupiedHeight(MenuStyleRules.MainCarouselMode(true), metrics),
                metrics.BarHeight);
        }

        [Test]
        public void CarouselSlideSurvivesAFrameHitchAndStartsWhereTheBarIs()
        {
            Assert.AreEqual(MenuStyleRules.MaxTweenStep, MenuStyleRules.TweenStep(0.5f),
                "a long frame must not jump the slide to its end");
            Assert.AreEqual(0.01f, MenuStyleRules.TweenStep(0.01f), 1e-6f);
            Assert.AreEqual(0f, MenuStyleRules.TweenStep(-1f));
            Assert.AreEqual(0f, MenuStyleRules.TweenStep(float.NaN));
            var steps = Mathf.CeilToInt(ModernTheme.TweenTime / MenuStyleRules.MaxTweenStep);
            Assert.GreaterOrEqual(steps, 5, "a full slide takes several frames even at a hitch rate");

            Assert.AreEqual(1f, MenuStyleRules.CarouselShown(CarouselMode.Normal));
            Assert.AreEqual(1f, MenuStyleRules.CarouselShown(CarouselMode.Expanded));
            Assert.AreEqual(1f, MenuStyleRules.CarouselShown(CarouselMode.Locked));
            Assert.AreEqual(0f, MenuStyleRules.CarouselShown(CarouselMode.Collapsed));
            Assert.AreEqual(0f, MenuStyleRules.CarouselShown(CarouselMode.Hidden));

            Assert.AreEqual(0.2f, MenuStyleRules.CarouselTweenTime(0f, 1f, 0.2f), 1e-6f);
            Assert.AreEqual(0.1f, MenuStyleRules.CarouselTweenTime(0.5f, 0f, 0.2f), 1e-6f,
                "a slide reversed halfway takes the remaining half");
            Assert.AreEqual(0f, MenuStyleRules.CarouselTweenTime(1f, 1f, 0.2f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ModePickerLeavesThroughTheBackArrowOfTheActiveStyle(bool modern)
        {
            MenuStyle.ToolOverride = modern ? SettingsData.UiStyleModern : SettingsData.UiStyleClassic;
            var host = new GameObject("mode_picker_test");
            try
            {
                var picker = BattleModeScreen.Create(host.transform, BattleMode.Normal, null);
                var root = (RectTransform)picker.transform;
                Assert.IsNull(root.Find("close"), "no CLOSE text button any more");
                var back = (RectTransform)root.Find("btn_menu");
                Assert.IsNotNull(back, "the back arrow of the shop and the containers");
                Assert.IsNotNull(back.Find("icon").GetComponent<Image>().sprite);
                Assert.IsFalse(back.GetComponentsInChildren<Text>(true).Any(t => !string.IsNullOrEmpty(t.text)),
                    "an arrow, not a caption");
                var size = modern
                    ? new Vector2(HangarUiKit.ModernTopButtonWidth, HangarUiKit.ModernTopButtonHeight)
                    : new Vector2(HangarUiKit.ClassicBackButtonWidth, HangarUiKit.ClassicBackButtonHeight);
                Assert.AreEqual(size, back.rect.size);
                var centre = root.InverseTransformPoint(back.TransformPoint(back.rect.center));
                Assert.AreEqual(root.rect.yMax - BattleModeScreen.HeaderHeight * 0.5f, centre.y, 0.01f,
                    "centred in the header band");

                back.GetComponent<Button>().onClick.Invoke();
                Assert.IsTrue(picker == null, "the arrow closes the picker");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void FilterIsOrWithinAGroupAndAndAcrossGroups()
        {
            var filter = new CarouselFilter();
            var heavy8 = new TankInfo { Name = "a", Level = 8, Class = TankClass.Heavy, Nation = Nation.Ussr };
            var medium8 = new TankInfo { Name = "b", Level = 8, Class = TankClass.Medium, Nation = Nation.Ussr };
            var heavy9 = new TankInfo { Name = "c", Level = 9, Class = TankClass.Heavy, Nation = (Nation)2 };
            var premium = new TankInfo { Name = "d", Level = 8, Class = TankClass.Heavy, Premium = true };

            Assert.IsFalse(filter.IsActive);
            Assert.IsTrue(filter.Matches(heavy8) && filter.Matches(medium8) && filter.Matches(heavy9));

            filter.ToggleLevel(8);
            filter.ToggleLevel(9);
            filter.ToggleClass(TankClass.Heavy);
            Assert.AreEqual(3, filter.ActiveCount);
            Assert.IsTrue(filter.Matches(heavy8));
            Assert.IsTrue(filter.Matches(heavy9));
            Assert.IsFalse(filter.Matches(medium8));

            filter.ToggleNation(Nation.Ussr);
            Assert.IsFalse(filter.Matches(heavy9));

            filter.TogglePremium();
            Assert.IsFalse(filter.Matches(heavy8));
            filter.ToggleNation(Nation.Ussr);
            Assert.IsTrue(filter.Matches(premium));

            filter.ToggleLevel(9);
            Assert.IsFalse(filter.HasLevel(9));
            filter.Clear();
            Assert.AreEqual(0, filter.ActiveCount);
            Assert.IsTrue(filter.Matches(medium8));
        }

        [Test]
        public void FilterStatusTogglesGiveEveryVehicleOneStatus()
        {
            var filter = new CarouselFilter();
            var tree = new TankInfo { Name = "tree", Level = 6 };
            var premium = new TankInfo { Name = "premium", Level = 6, Premium = true };
            var collector = new TankInfo { Name = "collector", Level = 6, Premium = true, Collector = true };
            var mod = new TankInfo { Name = "mod", Level = 6, Premium = true, Mod = true, Nation = Nation.Other };
            var all = new[] { tree, premium, collector, mod };

            // Off, the status toggles keep every vehicle.
            foreach (var tank in all) { Assert.IsTrue(filter.Matches(tank), tank.Name); }

            // The tech tree toggle keeps the white names alone: a premium, a
            // collector or a mod never counts as a tech tree vehicle.
            filter.ToggleRegular();
            Assert.IsTrue(filter.Regular);
            AssertMatches(filter, all, "tree");
            filter.TogglePremium();
            AssertMatches(filter, all, "tree", "premium");
            Assert.AreEqual(2, filter.ActiveCount);
            filter.ToggleRegular();
            AssertMatches(filter, all, "premium");

            // A collector or a mod is never counted as a premium, and a mod
            // is never a collector: the status is the colour of the name.
            filter.ToggleCollector();
            AssertMatches(filter, all, "premium", "collector");
            filter.TogglePremium();
            AssertMatches(filter, all, "collector");
            filter.ToggleMods();
            Assert.IsTrue(filter.Mods);
            AssertMatches(filter, all, "collector", "mod");
            filter.ToggleCollector();
            AssertMatches(filter, all, "mod");
            Assert.AreEqual(1, filter.ActiveCount);

            // The mod group combines with the others like any other group.
            filter.ToggleNation(Nation.Ussr);
            AssertMatches(filter, all);
            filter.ToggleNation(Nation.Other);
            AssertMatches(filter, all, "mod");
            Assert.AreEqual(3, filter.ActiveCount);

            filter.ToggleRegular();
            filter.Clear();
            Assert.IsFalse(filter.Regular);
            Assert.IsFalse(filter.Mods);
            Assert.IsFalse(filter.IsActive);
            foreach (var tank in all) { Assert.IsTrue(filter.Matches(tank), tank.Name); }
        }

        private static void AssertMatches(CarouselFilter filter, TankInfo[] tanks, params string[] expected)
        {
            foreach (var tank in tanks)
            {
                Assert.AreEqual(expected.Contains(tank.Name), filter.Matches(tank), tank.Name);
            }
        }

        [Test]
        public void SkinRedrawsChromeButNeverIconsOrTheBattleButton()
        {
            var atlas = AtlasRegionTable.Parse(BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            var original = atlas.Get("hangar_gold");
            var battle = atlas.Get("hangar_play");
            var plate = atlas.Get("button_generic");
            MenuStyle.Apply(atlas, true);

            var icons = new[]
            {
                "hangar_play", "hangar_gold", "hangar_credits", "hangar_freexp", "hangar_exp",
                "hangar_lvl1", "hangar_class_ht", "hangar_nation1", "tank_level_8", "tank_class_heavy",
                "tank_nation_1", "hangar_menu", "hangar_shop", "hangar_research", "hangar_techtree",
                "hangar_crew", "hangar_news", "hangar_filter_icon", "hangar_premium", "hangar_collector",
                "hangar_regular_badge", "hangar_collector_badge", "hangar_mod_badge", "tank_nation_9",
                "equipment_slot_empty", "equipment_slot_filled", "shell_bg_generic", "flow_queue_clock",
                "flow_intro_logo", "hangar_red_button", "button_orange",
            };
            foreach (var icon in icons)
            {
                Assert.IsFalse(ModernSkin.Remaps(icon), icon + " must keep its original artwork");
            }

            foreach (var region in ModernSkin.ChromeRegions)
            {
                Assert.IsFalse(region.StartsWith("tank_") || region.Contains("icon") || region == "hangar_play",
                    region + " looks like an icon");
                Assert.IsTrue(atlas.Contains(region), region + " is not an atlas region");
            }

            Assert.AreSame(original, atlas.Get("hangar_gold"));
            Assert.AreSame(battle, atlas.Get("hangar_play"));
            Assert.AreNotSame(plate, atlas.Get("button_generic"));
            Assert.IsNotNull(atlas.Get("button_generic" + UiTheme.PressedRegionSuffix));
            Assert.AreSame(plate, atlas.GetOriginal("button_generic"));

            MenuStyle.Apply(atlas, false);
            Assert.AreSame(plate, atlas.Get("button_generic"));
        }

        [Test]
        public void ModernChromeIsSlicedForStretching()
        {
            var sprite = ModernSkin.Instance.Resolve("panel_body", null);
            Assert.IsNotNull(sprite);
            Assert.Greater(sprite.border.x, 0f);
            Assert.AreSame(sprite, ModernSkin.Instance.Resolve("panel_body", null), "sprites are cached");
        }

        [Test]
        public void SettingsManifestOffersBothInterfaceRows()
        {
            var rows = JsonNode.Parse(BlitzResources.LoadText(BlitzResources.SettingsManifestData))["screen"]["rows"];
            var style = rows.Items.FirstOrDefault(r => r["setting"].AsString() == "ui_style");
            var size = rows.Items.FirstOrDefault(r => r["setting"].AsString() == "ui_carousel_size");
            Assert.IsNotNull(style);
            Assert.IsNotNull(size);
            Assert.AreEqual("main", style["tab"].AsString());
            Assert.AreEqual(2, style["options"].Count);
            Assert.AreEqual(3, size["options"].Count);
            Assert.AreEqual(SettingsData.UiStyleClassic, style["options"][0]["value"].AsInt());
            Assert.AreEqual(SettingsData.UiStyleModern, style["options"][1]["value"].AsInt());
        }

        [Test]
        public void ToolOverrideForcesTheStyleAndSkipsThePicker()
        {
            MenuStyle.ToolOverride = SettingsData.UiStyleModern;
            Assert.IsTrue(MenuStyle.IsModern);
            Assert.IsFalse(MenuStyle.NeedsPicker);
            MenuStyle.ToolOverride = SettingsData.UiStyleClassic;
            Assert.IsFalse(MenuStyle.IsModern);
        }
    }
}
