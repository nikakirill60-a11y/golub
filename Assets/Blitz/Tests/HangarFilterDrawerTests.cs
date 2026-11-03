using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// screens/main/filter_handle of ui_hangar.tscn, the classic carousel
    /// filter, and the fade the Modern filter puts the hangar chrome under.
    /// The expected numbers are the scene's own -- show_filter's keys and
    /// transition, the rows' rects and the frames -- but for the nine-nation
    /// row and the mod toggle, which the scene never had.
    /// </summary>
    public sealed class HangarFilterDrawerTests
    {
        private const float Tolerance = 0.01f;
        private const float ScreenWidth = 1386.6667f;
        private const float ScreenHeight = 733.3333f;

        private AtlasRegionTable _atlas;
        private HangarUiKit _kit;
        private CarouselFilter _filter;
        private RectTransform _root;

        [SetUp]
        public void SetUp()
        {
            _atlas = AtlasRegionTable.Parse(BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            _kit = new HangarUiKit(
                _atlas,
                LocalizationTable.Parse(BlitzResources.LoadText(BlitzResources.LanguageData)),
                BlitzResources.LoadFont(BlitzResources.RegularFontName),
                BlitzResources.LoadFont(BlitzResources.BoldFontName),
                null);
            _filter = new CarouselFilter();

            var root = new GameObject("filter_test_root", typeof(RectTransform));
            _root = (RectTransform)root.transform;
            _root.sizeDelta = new Vector2(ScreenWidth, ScreenHeight);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root.gameObject);
                _root = null;
            }
        }

        // ------------------------------------------------------------------
        // show_filter.
        // ------------------------------------------------------------------

        [Test]
        public void Ease_MatchesGodotMathEase()
        {
            Assert.AreEqual(0.25f, HangarFilterDrawer.Ease(0.5f, 2f), 1e-5f);
            Assert.AreEqual(0.75f, HangarFilterDrawer.Ease(0.5f, 0.5f), 1e-5f);
            Assert.AreEqual(0.125f, HangarFilterDrawer.Ease(0.25f, -2f), 1e-5f);
            Assert.AreEqual(0.875f, HangarFilterDrawer.Ease(0.75f, -2f), 1e-5f);
            Assert.AreEqual(0f, HangarFilterDrawer.Ease(0.5f, 0f), 1e-5f);
            Assert.AreEqual(0f, HangarFilterDrawer.Ease(-1f, 2f), 1e-5f);
            Assert.AreEqual(1f, HangarFilterDrawer.Ease(2f, 2f), 1e-5f);
        }

        [Test]
        public void SlideY_SamplesTheShowFilterTrack()
        {
            Assert.AreEqual(-135f, HangarFilterDrawer.SlideY(0f), Tolerance);
            Assert.AreEqual(-399f, HangarFilterDrawer.SlideY(0.4f), Tolerance);
            Assert.AreEqual(-135f, HangarFilterDrawer.SlideY(-1f), Tolerance);
            Assert.AreEqual(-399f, HangarFilterDrawer.SlideY(5f), Tolerance);

            // Both keys carry transition = 1.1095695: pow(t, c) halfway.
            var halfway = -135f - (264f * Mathf.Pow(0.5f, 1.1095695f));
            Assert.AreEqual(halfway, HangarFilterDrawer.SlideY(0.2f), Tolerance);
            Assert.AreEqual(-257.34f, HangarFilterDrawer.SlideY(0.2f), 0.05f);
        }

        [Test]
        public void Create_ParksTheBandOnTheScreenEdge()
        {
            var drawer = CreateDrawer();

            Assert.AreEqual("filter_handle", drawer.name);
            Assert.AreEqual("filter", drawer.Band.name);
            Assert.IsFalse(drawer.IsOpen);
            Assert.IsFalse(drawer.IsVisible);
            Assert.AreEqual(HangarFilterDrawer.ClosedY, drawer.PositionY, Tolerance);
            Assert.AreEqual(0f, Lift(drawer.Band), Tolerance);
            Assert.AreEqual(HangarFilterDrawer.BandHeight, Height(drawer.Band), Tolerance);
            Assert.AreEqual(ScreenWidth, Width(drawer.Band), Tolerance);
        }

        [Test]
        public void Slide_LiftsTheBandBy264Pixels()
        {
            var drawer = CreateDrawer();
            drawer.SetOpen(true);
            Assert.IsTrue(drawer.IsOpen);
            Assert.AreEqual(0f, drawer.Playhead, Tolerance, "SetOpen only starts the slide");

            drawer.Advance(0.2f);
            Assert.AreEqual(-HangarFilterDrawer.SlideY(0.2f) - 135f, Lift(drawer.Band), Tolerance);

            drawer.Advance(1f);
            Assert.AreEqual(HangarFilterDrawer.SlideSeconds, drawer.Playhead, Tolerance);
            Assert.AreEqual(HangarFilterDrawer.OpenY, drawer.PositionY, Tolerance);
            Assert.AreEqual(264f, Lift(drawer.Band), Tolerance);
            Assert.IsTrue(drawer.IsVisible);
        }

        [Test]
        public void Slide_TurnsAroundWhereItIs()
        {
            var drawer = CreateDrawer();
            drawer.SetOpen(true);
            drawer.Advance(0.1f);
            drawer.SetOpen(false);
            drawer.Advance(0.05f);

            Assert.AreEqual(0.05f, drawer.Playhead, 1e-4f);
            Assert.AreEqual(HangarFilterDrawer.SlideY(0.05f), drawer.PositionY, Tolerance);

            drawer.Advance(1f);
            Assert.AreEqual(0f, drawer.Playhead, Tolerance);
            Assert.AreEqual(HangarFilterDrawer.ClosedY, drawer.PositionY, Tolerance);
            Assert.IsFalse(drawer.IsVisible);
        }

        [Test]
        public void SetOpenImmediate_JumpsToTheEnd()
        {
            var drawer = CreateDrawer();
            drawer.SetOpenImmediate(true);
            Assert.AreEqual(HangarFilterDrawer.OpenY, drawer.PositionY, Tolerance);
            Assert.AreEqual(264f, Lift(drawer.Band), Tolerance);

            drawer.SetOpenImmediate(false);
            Assert.IsFalse(drawer.IsOpen);
            Assert.AreEqual(0f, Lift(drawer.Band), Tolerance);
        }

        // ------------------------------------------------------------------
        // The rows.
        // ------------------------------------------------------------------

        [Test]
        public void BuildControls_BuildsEveryToggleOfTheScene()
        {
            var drawer = CreateDrawer();
            drawer.BuildControls();

            Assert.AreEqual(27, drawer.ToggleCount, "10 levels, 4 status toggles, 4 classes, 9 nations");
            for (var level = 1; level <= 10; level++)
            {
                Assert.IsNotNull(drawer.FindToggle("lvl" + level), "lvl" + level);
            }

            foreach (var name in new[]
            {
                "filter_regulars", "filter_premiums", "filter_collectors", "filter_mods",
                "lt", "mt", "ht", "td",
            })
            {
                Assert.IsNotNull(drawer.FindToggle(name), name);
            }

            Assert.AreEqual(9, HangarFilterDrawer.NationCount, "one toggle per nation");
            for (var nation = 1; nation <= HangarFilterDrawer.NationCount; nation++)
            {
                Assert.IsNotNull(drawer.FindToggle("nation" + nation), "nation" + nation);
            }

            Assert.IsNull(drawer.FindToggle("nation10"));
            Assert.IsNotNull(drawer.ResetButton);
            Assert.AreEqual("reset_filter", drawer.ResetButton.name);
            Assert.Throws<InvalidOperationException>(drawer.BuildControls);
        }

        [Test]
        public void BuildControls_KeepsTheSceneOrder()
        {
            var drawer = CreateDrawer();
            drawer.BuildControls();

            var expected = new[]
            {
                "bg", "border", "border2", "border3", "border4", "border5", "border6", "border7",
                "border8", "filter_levels", "filter_classes", "filter_nations", "reset_filter",
            };
            Assert.AreEqual(expected.Length, drawer.Band.childCount);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], drawer.Band.GetChild(i).name);
            }
        }

        [Test]
        public void Rows_SitInTheSceneRectsOnceOpen()
        {
            var drawer = CreateDrawer();
            drawer.BuildControls();
            drawer.SetOpenImmediate(true);

            // Screen-space rects: the band's bottom edge is 264 px up.
            AssertRect(Find(drawer, "filter_levels"), 241.3333f, 511.3333f, 636f, 60f);
            AssertRect(Find(drawer, "filter_classes"), 241.3333f, 631.3333f, 252f, 60f);
            AssertRect((RectTransform)drawer.FindToggle("lvl10").transform, 817.3333f, 511.3333f, 60f, 60f);

            // filter_nations starts where the scene's does, on the class
            // row's line, and is 56 px longer for its four extra toggles;
            // reset_filter moves over by as much.
            AssertRect(Find(drawer, "filter_nations"), 558.3333f, 631.3333f, 572f, 60f);
            AssertRect((RectTransform)drawer.FindToggle("nation9").transform, 1070.3333f, 631.3333f, 60f, 60f);
            AssertRect((RectTransform)drawer.ResetButton.transform, 1169.3333f, 630.3323f, 60f, 60f);
            AssertRect(Find(drawer, "border3"), 553.3333f, 622.3333f, 583f, 79f);
            AssertRect(Find(drawer, "border4"), 1163.3333f, 624.3323f, 72f, 72f);

            // bg runs from the band's bottom edge to past the screen's.
            AssertRect(Find(drawer, "bg"), 0f, 469.3333f, ScreenWidth, HangarFilterDrawer.LayerHeight);
        }

        [Test]
        public void Frames_AreFaintNinePatchTilesAtAThirdOfTheirSize()
        {
            var drawer = CreateDrawer();
            drawer.BuildControls();

            foreach (var name in new[]
            {
                "border", "border2", "border3", "border4", "border5", "border6", "border7", "border8",
            })
            {
                var frame = Find(drawer, name).GetComponent<Image>();
                Assert.AreEqual(Image.Type.Sliced, frame.type, name);
                Assert.AreEqual(HangarFilterDrawer.FrameAlpha, frame.color.a, 1e-4f, name);
                Assert.AreEqual(HangarFilterDrawer.NodeScale, frame.rectTransform.localScale.x, 1e-5f, name);
                Assert.AreEqual(
                    new Vector4(20f, 20f, 20f, 20f), frame.sprite.border, name + " patch margins");
            }

            var layer = Find(drawer, "bg").GetComponent<Image>();
            Assert.AreEqual(new Vector4(5f, 5f, 5f, 5f), layer.sprite.border);
            Assert.IsTrue(layer.raycastTarget, "bg is mouse_filter = STOP");

            // border5 .. border8 frame the status toggles, border3 every
            // nation and border4 the reset.
            var status = new[] { "filter_regulars", "filter_premiums", "filter_collectors", "filter_mods" };
            for (var i = 0; i < status.Length; i++)
            {
                var frame = Bounds(Find(drawer, "border" + (5 + i)));
                var toggle = Bounds((RectTransform)drawer.FindToggle(status[i]).transform);
                Assert.IsTrue(Contains(frame, toggle), status[i] + " leaves its frame");
                Assert.AreEqual(72f, frame.width, 0.05f, status[i] + " frame");
                Assert.AreEqual(72f, frame.height, 0.05f, status[i] + " frame");
            }

            var nations = Bounds(Find(drawer, "border3"));
            for (var nation = 1; nation <= HangarFilterDrawer.NationCount; nation++)
            {
                var toggle = Bounds((RectTransform)drawer.FindToggle("nation" + nation).transform);
                Assert.IsTrue(Contains(nations, toggle), "nation" + nation + " leaves border3");
            }

            var reset = Bounds((RectTransform)drawer.ResetButton.transform);
            Assert.IsTrue(Contains(Bounds(Find(drawer, "border4")), reset));
            Assert.IsFalse(nations.Overlaps(Bounds(Find(drawer, "border4"))), "border3 runs into border4");
            Assert.IsFalse(
                Bounds(Find(drawer, "border")).Overlaps(Bounds(Find(drawer, "border5"))), "border runs into border5");
            for (var i = 5; i < 5 + HangarFilterDrawer.StatusCount - 1; i++)
            {
                var frame = "border" + i;
                var next = "border" + (i + 1);
                Assert.IsFalse(
                    Bounds(Find(drawer, frame)).Overlaps(Bounds(Find(drawer, next))), frame + " runs into " + next);
            }
        }

        [Test]
        public void StatusToggles_ShareTheBorderRowAndTheSelectorGeometry()
        {
            var drawer = CreateDrawer();
            drawer.BuildControls();
            drawer.SetOpenImmediate(true);

            // filter_regulars sits where the scene's filter_premiums does.
            var first = (RectTransform)drawer.FindToggle("filter_regulars").transform;
            var firstBounds = Bounds(first);
            var firstOutline = first.Find("selected").GetComponent<Image>();
            var firstIcon = first.Find("icon").GetComponent<Image>();
            Assert.AreEqual("border", first.parent.name);
            Assert.AreEqual(61f, firstBounds.width, 0.05f);
            Assert.AreEqual(new Vector4(9f, 9f, 9f, 9f), firstOutline.sprite.border);

            var previous = firstBounds;
            foreach (var name in new[] { "filter_premiums", "filter_collectors", "filter_mods" })
            {
                var toggle = (RectTransform)drawer.FindToggle(name).transform;
                Assert.AreSame(first.parent, toggle.parent, name);
                Assert.AreEqual(first.rect.size.x, toggle.rect.size.x, Tolerance, name);
                Assert.AreEqual(first.rect.size.y, toggle.rect.size.y, Tolerance, name);

                var bounds = Bounds(toggle);
                Assert.AreEqual(firstBounds.y, bounds.y, Tolerance, name + " leaves the row");
                Assert.AreEqual(HangarFilterDrawer.StatusPitch, bounds.x - previous.x, 0.05f, name);
                Assert.Less(previous.xMax, bounds.xMin, name + " overlaps the toggle before it");
                Assert.LessOrEqual(bounds.xMax, ScreenWidth, name + " leaves the screen");
                Assert.LessOrEqual(bounds.yMax, ScreenHeight, name + " leaves the screen");
                previous = bounds;

                var outline = toggle.Find("selected").GetComponent<Image>();
                Assert.AreSame(firstOutline.sprite, outline.sprite, name);
                Assert.AreEqual(firstOutline.rectTransform.rect.size.x, outline.rectTransform.rect.size.x, Tolerance, name);
                Assert.AreEqual(firstOutline.rectTransform.rect.size.y, outline.rectTransform.rect.size.y, Tolerance, name);

                // Every badge is the premium wreath in the status colour:
                // same picture size, same place on the toggle.
                var icon = toggle.Find("icon").GetComponent<Image>();
                Assert.IsNotNull(icon.sprite, name + " has no badge");
                Assert.AreEqual(firstIcon.sprite.rect.size, icon.sprite.rect.size, name);
                Assert.AreEqual(firstIcon.rectTransform.rect.size, icon.rectTransform.rect.size, name);
                Assert.AreEqual(firstIcon.rectTransform.anchoredPosition, icon.rectTransform.anchoredPosition, name);
            }

            var badges = new Dictionary<string, string>
            {
                { "filter_regulars", HangarUiKit.RegularBadgeRegion },
                { "filter_premiums", HangarUiKit.PremiumBadgeRegion },
                { "filter_collectors", HangarUiKit.CollectorBadgeRegion },
                { "filter_mods", HangarUiKit.ModBadgeRegion },
            };
            foreach (var badge in badges)
            {
                Assert.AreEqual(badge.Value,
                    drawer.FindToggle(badge.Key).transform.Find("icon").GetComponent<Image>().sprite.name, badge.Key);
            }
        }

        [Test]
        public void NationToggles_CarryEveryFlagInAClassSizedToggle()
        {
            var drawer = CreateDrawer();
            drawer.BuildControls();
            drawer.SetOpenImmediate(true);

            var classToggle = Bounds((RectTransform)drawer.FindToggle("td").transform);
            var classOutline = drawer.FindToggle("td").transform.Find("selected").GetComponent<Image>();
            for (var i = 1; i <= HangarFilterDrawer.NationCount; i++)
            {
                var nation = (Nation)i;
                var toggle = drawer.FindToggle("nation" + i);
                var bounds = Bounds((RectTransform)toggle.transform);
                Assert.AreEqual(classToggle.width, bounds.width, Tolerance, "nation" + i);
                Assert.AreEqual(classToggle.height, bounds.height, Tolerance, "nation" + i);
                Assert.AreEqual(classToggle.y, bounds.y, Tolerance, "nation" + i + " leaves the class row's line");

                var icon = toggle.transform.Find("icon").GetComponent<Image>();
                Assert.IsNotNull(icon.sprite, "nation" + i + " has no flag");
                Assert.AreEqual(HangarUiKit.FilterNationRegion(nation), icon.sprite.name, "nation" + i);
                Assert.AreEqual(HangarFilterDrawer.FlagWidth, icon.rectTransform.rect.width, Tolerance, "nation" + i);
                Assert.Less(icon.rectTransform.rect.height, HangarFilterDrawer.ToggleSize - 20f, "nation" + i);
                Assert.Greater(icon.rectTransform.rect.height, 25f, "nation" + i + " draws a banner, not a flag");

                var outline = toggle.transform.Find("selected").GetComponent<Image>();
                Assert.AreEqual(classOutline.rectTransform.rect.size, outline.rectTransform.rect.size, "nation" + i);
            }

            Assert.AreEqual("hangar_nation1", HangarUiKit.FilterNationRegion(Nation.Ussr));
            Assert.AreEqual("hangar_nation7", HangarUiKit.FilterNationRegion(Nation.China));
            Assert.AreEqual("tank_nation_8", HangarUiKit.FilterNationRegion(Nation.Other));
            Assert.AreEqual("tank_nation_9", HangarUiKit.FilterNationRegion(Nation.European));
        }

        [Test]
        public void Toggles_NarrowTheFilterAndDrawTheOutline()
        {
            var drawer = CreateDrawer();
            drawer.BuildControls();
            var changes = 0;
            drawer.Changed = () => changes++;

            Assert.IsFalse(drawer.IsSelected("lvl7"));
            drawer.FindToggle("lvl7").onClick.Invoke();
            Assert.IsTrue(_filter.HasLevel(7));
            Assert.IsTrue(drawer.IsSelected("lvl7"));
            Assert.IsTrue(GodotButtonState.Of(drawer.FindToggle("lvl7")).Latched);
            Assert.AreEqual(1, changes);

            drawer.FindToggle("mt").onClick.Invoke();
            drawer.FindToggle("nation9").onClick.Invoke();
            drawer.FindToggle("filter_regulars").onClick.Invoke();
            drawer.FindToggle("filter_collectors").onClick.Invoke();
            drawer.FindToggle("filter_mods").onClick.Invoke();
            Assert.IsTrue(_filter.HasClass(TankClass.Medium));
            Assert.IsTrue(_filter.HasNation(Nation.European));
            Assert.IsTrue(_filter.Regular);
            Assert.IsTrue(_filter.Collector);
            Assert.IsTrue(_filter.Mods);
            Assert.IsTrue(drawer.IsSelected("filter_regulars"));
            Assert.IsTrue(drawer.IsSelected("filter_mods"));
            Assert.AreEqual(6, _filter.ActiveCount);
            Assert.AreEqual(6, changes);

            drawer.FindToggle("lvl7").onClick.Invoke();
            Assert.IsFalse(_filter.HasLevel(7));
            Assert.IsFalse(drawer.IsSelected("lvl7"));
            Assert.IsFalse(GodotButtonState.Of(drawer.FindToggle("lvl7")).Latched);
            Assert.AreEqual(7, changes);
        }

        [Test]
        public void Toggles_NeverDarkenTheButton()
        {
            var drawer = CreateDrawer();
            drawer.BuildControls();
            var button = drawer.FindToggle("lvl3");
            var image = button.GetComponent<Image>();
            var normal = image.sprite;

            button.onClick.Invoke();
            Assert.AreSame(normal, image.sprite);
            Assert.AreEqual(Color.white, image.color);
        }

        [Test]
        public void Reset_ReleasesEveryToggle()
        {
            var drawer = CreateDrawer();
            drawer.BuildControls();
            var changes = 0;
            drawer.Changed = () => changes++;
            drawer.FindToggle("lvl3").onClick.Invoke();
            drawer.FindToggle("filter_regulars").onClick.Invoke();
            drawer.FindToggle("filter_premiums").onClick.Invoke();
            drawer.FindToggle("filter_mods").onClick.Invoke();
            changes = 0;

            drawer.ResetButton.onClick.Invoke();
            Assert.IsFalse(_filter.IsActive);
            Assert.IsFalse(drawer.IsSelected("lvl3"));
            Assert.IsFalse(drawer.IsSelected("filter_regulars"));
            Assert.IsFalse(drawer.IsSelected("filter_premiums"));
            Assert.IsFalse(drawer.IsSelected("filter_mods"));
            Assert.AreEqual(1, changes);

            drawer.ResetButton.onClick.Invoke();
            Assert.AreEqual(1, changes, "an empty filter is not rebuilt");
        }

        [Test]
        public void Refresh_ReadsAFilterChangedElsewhere()
        {
            var drawer = CreateDrawer();
            drawer.BuildControls();
            _filter.ToggleNation(Nation.Usa);
            Assert.IsFalse(drawer.IsSelected("nation3"));

            drawer.Refresh();
            Assert.IsTrue(drawer.IsSelected("nation3"));
        }

        [Test]
        public void GetNinePatch_CutsTheOriginalRegionOnce()
        {
            var tile = _atlas.GetNinePatch("hangar_select_tile", 20f);
            Assert.IsNotNull(tile);
            Assert.AreEqual(new Vector4(20f, 20f, 20f, 20f), tile.border);
            Assert.AreEqual(46f, tile.rect.width, Tolerance);
            Assert.AreEqual(46f, tile.rect.height, Tolerance);
            Assert.AreSame(tile, _atlas.GetNinePatch("hangar_select_tile", 20f));
            Assert.AreNotSame(tile, _atlas.GetNinePatch("hangar_select_tile", 5f));
            Assert.IsNull(_atlas.GetNinePatch("no_such_region", 4f));
        }

        // ------------------------------------------------------------------
        // ModernFade.
        // ------------------------------------------------------------------

        [Test]
        public void ModernFade_DropsTapsAtOnceAndFadesOut()
        {
            var fade = _root.gameObject.AddComponent<ModernFade>();
            var widget = UiFactory.Node("widget", _root);
            var group = fade.Add(widget);
            Assert.AreEqual(1f, group.alpha, 1e-5f);

            fade.Show(false);
            Assert.IsFalse(group.blocksRaycasts, "a hidden widget must not take taps");
            Assert.AreEqual(1f, group.alpha, 1e-5f, "Show only starts the fade");
            Assert.IsTrue(fade.IsFading);

            fade.Advance(fade.Seconds * 0.5f);
            Assert.AreEqual(0.5f, group.alpha, 1e-4f);

            fade.Advance(fade.Seconds);
            Assert.AreEqual(0f, group.alpha, 1e-5f);
            Assert.IsFalse(fade.IsFading);
            Assert.IsTrue(widget.gameObject.activeSelf, "only alpha and taps change");
        }

        [Test]
        public void ModernFade_TurnsAroundWhereItIs()
        {
            var fade = _root.gameObject.AddComponent<ModernFade>();
            var group = fade.Add(UiFactory.Node("widget", _root));
            fade.Show(false);
            fade.Advance(fade.Seconds * 0.25f);
            Assert.AreEqual(0.75f, fade.Playhead, 1e-4f);

            fade.Show(true);
            Assert.IsTrue(group.blocksRaycasts);
            fade.Advance(fade.Seconds * 0.25f);
            Assert.AreEqual(1f, fade.Playhead, 1e-5f);
            Assert.AreEqual(1f, group.alpha, 1e-5f);
        }

        [Test]
        public void ModernFade_CanSwitchAHiddenWidgetOff()
        {
            var fade = _root.gameObject.AddComponent<ModernFade>();
            fade.DeactivateWhenHidden = true;
            var widget = UiFactory.Node("widget", _root);
            var group = fade.Add(widget);

            fade.Show(false);
            fade.Advance(fade.Seconds * 0.5f);
            Assert.IsTrue(widget.gameObject.activeSelf, "still fading");
            fade.Advance(fade.Seconds);
            Assert.IsFalse(widget.gameObject.activeSelf);

            fade.Show(true);
            Assert.IsTrue(widget.gameObject.activeSelf, "a widget fading in is drawn from its first frame");
            Assert.AreEqual(0f, group.alpha, 1e-5f);

            fade.ShowImmediate(false);
            Assert.IsFalse(widget.gameObject.activeSelf);
        }

        [Test]
        public void ModernFade_ReusesTheWidgetsCanvasGroup()
        {
            var fade = _root.gameObject.AddComponent<ModernFade>();
            var widget = UiFactory.Node("widget", _root);
            var own = widget.gameObject.AddComponent<CanvasGroup>();

            Assert.AreSame(own, fade.Add(widget));
            fade.Add(widget);
            Assert.AreEqual(1, fade.Count);
            Assert.IsTrue(fade.Contains(widget));
            Assert.IsFalse(fade.Contains(_root));

            fade.ShowImmediate(false);
            Assert.AreEqual(0f, own.alpha, 1e-5f);
            Assert.AreEqual(0f, ModernFade.Smooth(0f), 1e-5f);
            Assert.AreEqual(1f, ModernFade.Smooth(1f), 1e-5f);
            Assert.AreEqual(0.5f, ModernFade.Smooth(0.5f), 1e-5f);
        }

        // ------------------------------------------------------------------
        // Helpers.
        // ------------------------------------------------------------------

        private HangarFilterDrawer CreateDrawer()
        {
            return HangarFilterDrawer.Create(_root, _kit, _filter);
        }

        private static RectTransform Find(HangarFilterDrawer drawer, string name)
        {
            var child = drawer.Band.Find(name);
            Assert.IsNotNull(child, name + " is missing");
            return (RectTransform)child;
        }

        /// <summary>A rect in Godot screen space: x right, y down from the top-left corner.</summary>
        private Rect Bounds(RectTransform rt)
        {
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            var screen = new Vector3[4];
            _root.GetWorldCorners(screen);
            var left = corners[0].x - screen[0].x;
            var top = screen[1].y - corners[1].y;
            return new Rect(left, top, corners[2].x - corners[0].x, corners[1].y - corners[0].y);
        }

        private static bool Contains(Rect outer, Rect inner)
        {
            return inner.xMin >= outer.xMin && inner.xMax <= outer.xMax
                && inner.yMin >= outer.yMin && inner.yMax <= outer.yMax;
        }

        private void AssertRect(RectTransform rt, float x, float y, float width, float height)
        {
            var bounds = Bounds(rt);
            Assert.AreEqual(x, bounds.x, 0.05f, rt.name + " x");
            Assert.AreEqual(y, bounds.y, 0.05f, rt.name + " y");
            Assert.AreEqual(width, bounds.width, 0.05f, rt.name + " width");
            Assert.AreEqual(height, bounds.height, 0.05f, rt.name + " height");
        }

        /// <summary>How far the rect's bottom edge sits above the screen's.</summary>
        private float Lift(RectTransform rt)
        {
            return ScreenHeight - Bounds(rt).yMax;
        }

        private float Height(RectTransform rt)
        {
            return Bounds(rt).height;
        }

        private float Width(RectTransform rt)
        {
            return Bounds(rt).width;
        }
    }
}
