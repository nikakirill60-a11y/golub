using System.Collections.Generic;
using BlitzLegacy.Core.Input;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// ui_settings.tscn and ui_settings.gd.
    ///
    /// Every number the port hardcodes is checked against
    /// Resources/Data/settings_source_manifest.json, which
    /// Tools/port_godot_settings.py generates from a headless dump of the
    /// Godot scene. If the source is re-dumped and turns out to differ, these
    /// tests fail instead of the port silently drifting.
    /// </summary>
    public sealed class SettingsScreenTests
    {
        private const float Tolerance = 0.0005f;

        private JsonNode _manifest;
        private JsonNode _screen;
        private GameObject _root;
        private SettingsScreen _ui;
        private SettingsData _settings;
        private bool _mobile;

        [SetUp]
        public void SetUp()
        {
            var text = Resources.Load<TextAsset>("Data/settings_source_manifest");
            Assert.IsNotNull(text, "settings source manifest is missing");
            _manifest = JsonNode.Parse(text.text);
            _screen = _manifest["screen"];

            _mobile = SettingsData.MobilePlatform;
            SettingsData.MobilePlatform = false;
            _settings = new SettingsData();

            _root = new GameObject("SettingsScreenTests");
            _ui = SettingsScreen.Open(_root.transform, _settings, 0, false, null);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }

            _ui = null;
            SettingsData.MobilePlatform = _mobile;
        }

        // ------------------------------------------------------------------
        // Geometry against the Godot dump.
        // ------------------------------------------------------------------

        [Test]
        public void Header_MatchesTheDumpedGodotGeometry()
        {
            var back = _screen["backButton"];
            AssertRect((RectTransform)_ui.Back.transform, back);
            Assert.AreEqual("ButtonSmallGeneric", back["variation"].AsString());
            Assert.AreEqual(50, back["iconMaxWidth"].AsInt());

            var title = _screen["title"];
            AssertRect(_ui.Title.rectTransform, title);
            Assert.AreEqual(title["fontSize"].AsInt(), _ui.Title.fontSize);
            Assert.AreEqual("SETTINGS", title["text"].AsString());

            var header = Find("panel_main_background");
            AssertRect(header, _screen["header"]);
            Assert.AreEqual(
                _screen["header"]["scale"][0].AsFloat(),
                header.localScale.x, Tolerance);
            AssertRect(Find("ThinRedLine"), _screen["thinRedLine"]);
        }

        [Test]
        public void TabStrip_SplitsTheCanvasLikeTheExpandingHBox()
        {
            var bar = _screen["tabBar"];
            Assert.AreEqual(92f, bar["offsets"][1].AsFloat(), Tolerance);
            Assert.AreEqual(158f, bar["offsets"][3].AsFloat(), Tolerance);
            // The four source tabs plus the port-only graphics tab.
            Assert.AreEqual(
                5, _ui.TabButtons.Count,
                "off a phone the controls tab shows next to graphics");

            // SIZE_EXPAND_FILL with the theme's 4 px separation: each button
            // takes an equal share of the strip minus its share of the gaps,
            // so the strip still fits a wider-than-reference window.
            const float separation = 4f;
            var count = _ui.TabButtons.Count;
            for (var i = 0; i < count; i++)
            {
                var rt = (RectTransform)_ui.TabButtons[i].transform;
                Assert.AreEqual(i / (float)count, rt.anchorMin.x, Tolerance);
                Assert.AreEqual((i + 1) / (float)count, rt.anchorMax.x, Tolerance);
                Assert.AreEqual(
                    i * separation / count, rt.offsetMin.x, Tolerance);
                Assert.AreEqual(
                    (i - (count - 1)) * separation / count,
                    rt.offsetMax.x, Tolerance);
            }
        }

        /// <summary>pc_controls_settings hides itself on a phone.</summary>
        [Test]
        public void ControlsTab_IsDesktopOnly()
        {
            Assert.IsTrue(
                _manifest["screen"]["behaviour"]["controlsTabDesktopOnly"].AsBool());
            Assert.AreEqual(
                PcControlBindings.Definitions.Count, _ui.ControlRows.Count,
                "every rebindable action of the source gets a row");
            Assert.IsNull(_ui.CapturingAction);

            var mobileRoot = new GameObject("SettingsScreenTestsMobile");
            try
            {
                var mobile = SettingsScreen.Open(
                    mobileRoot.transform, new SettingsData(), 0, true, null);
                // main, graphics, matchmaker and gameplay; no controls tab.
                Assert.AreEqual(4, mobile.TabButtons.Count);
                Assert.AreEqual(0, mobile.ControlRows.Count);
            }
            finally
            {
                Object.DestroyImmediate(mobileRoot);
            }
        }

        /// <summary>_begin_capture/_finish_capture move a conflicting bind.</summary>
        [Test]
        public void ControlsTab_CapturesAndMovesABinding()
        {
            _ui.BeginCapture(PcControlBindings.MoveBack);
            Assert.AreEqual(PcControlBindings.MoveBack, _ui.CapturingAction);

            _ui.FinishCapture("W");

            Assert.IsNull(_ui.CapturingAction);
            Assert.AreEqual("W", _settings.PcControls.Get(PcControlBindings.MoveBack));
            Assert.IsNull(_settings.PcControls.Get(PcControlBindings.MoveForward));

            _ui.ResetControls();

            Assert.IsTrue(_settings.PcControls.IsDefault);
        }

        [Test]
        public void Rows_FollowTheOrderAndColumnsOfTheSourceScript()
        {
            var rows = _screen["rows"];
            var expected = new List<string>();
            for (var i = 0; i < rows.Count; i++)
            {
                expected.Add(rows[i]["setting"].AsString());
            }

            Assert.AreEqual(
                expected.Count, _ui.Rows.Count,
                "every add_*_option call of ui_settings.gd builds one row");

            // The manifest lists the calls in source order; the screen groups
            // them per tab, so compare the sets and then the per-column order.
            var built = new List<string>();
            for (var i = 0; i < _ui.Rows.Count; i++)
            {
                built.Add(_ui.Rows[i].Source.Setting);
            }

            CollectionAssert.AreEquivalent(expected, built);

            // The source gives each column half of the page, so a row
            // follows the live width of the canvas.
            var content = (RectTransform)_ui.Rows[0].Node.parent;
            var columnWidth = (content.rect.width - 4f) * 0.5f;
            for (var i = 0; i < _ui.Rows.Count; i++)
            {
                var row = _ui.Rows[i];
                Assert.AreEqual(
                    SettingsScreen.RowHeight,
                    row.Node.rect.height, Tolerance, row.Source.Setting);
                Assert.AreEqual(
                    columnWidth, row.Node.rect.width, 0.01f, row.Source.Setting);
            }
        }

        [Test]
        public void RowTemplates_MatchTheDumpedMinimumSizeAndFonts()
        {
            var templates = _screen["templates"];
            foreach (var kind in new[] { "boolean", "enum", "slider" })
            {
                Assert.AreEqual(
                    500f, templates[kind]["root"]["minimumSize"][0].AsFloat(), Tolerance);
                Assert.AreEqual(
                    SettingsScreen.RowHeight,
                    templates[kind]["root"]["minimumSize"][1].AsFloat(), Tolerance);
                Assert.AreEqual(
                    21, templates[kind]["label"]["fontSize"].AsInt(),
                    "name is LabelBold at 21");
            }

            Assert.AreEqual(
                3f, _screen["checkboxScale"].AsFloat(), Tolerance);
            Assert.AreEqual(
                25, templates["enum"]["button"]["fontSize"].AsInt());
            Assert.AreEqual(
                77.29f,
                templates["enum"]["button"]["minimumSize"][0].AsFloat(), 0.01f);
            Assert.AreEqual(
                20, templates["slider"]["value"]["fontSize"].AsInt());
            Assert.AreEqual(
                0.33f, templates["slider"]["frame"]["scale"][0].AsFloat(), Tolerance);
            Assert.AreEqual(
                0.5f, templates["slider"]["slider"]["scale"][0].AsFloat(), Tolerance);
        }

        // ------------------------------------------------------------------
        // Behaviour.
        // ------------------------------------------------------------------

        [Test]
        public void Checkbox_TogglesTheSettingAndSwapsTheGlyph()
        {
            var row = RowFor("skip_login");
            Assert.IsFalse(_settings.SkipLogin);
            var off = row.Checkbox.sprite;

            row.Checkbox.GetComponent<Button>().onClick.Invoke();
            Assert.IsTrue(_settings.SkipLogin);
            Assert.AreNotSame(off, row.Checkbox.sprite);

            row.Checkbox.GetComponent<Button>().onClick.Invoke();
            Assert.IsFalse(_settings.SkipLogin);
            Assert.AreSame(off, row.Checkbox.sprite);
        }

        [Test]
        public void EnumRow_WritesTheSaveSettingsEnumMember()
        {
            var row = RowFor("matchmaker_team_size");
            Assert.AreEqual(5, row.Options.Count, "1x1, 2x2, 5x5, 7x7 and 10x10");
            Assert.AreEqual(
                SettingsData.TeamSize7x7, _settings.MatchmakerTeamSize,
                "TEAMSIZE_7X7 is the source default");

            row.Options[0].onClick.Invoke();
            Assert.AreEqual(SettingsData.TeamSize1x1, _settings.MatchmakerTeamSize);
            Assert.AreEqual(
                1, BlitzLegacy.Runtime.Battle.BattleSession.TeamSizeFor(_settings));

            row.Options[4].onClick.Invoke();
            Assert.AreEqual(SettingsData.TeamSize10x10, _settings.MatchmakerTeamSize);
        }

        [Test]
        public void LanguageRow_IsTheOnlyStringSettingAndRefusesUnknownLocales()
        {
            var row = RowFor("language");
            Assert.IsTrue(row.Source.IsString);
            row.Options[1].onClick.Invoke();
            Assert.AreEqual("ru", _settings.Language);

            _settings.SetString("language", "de");
            Assert.AreEqual("ru", _settings.Language, "set_language refuses it");
        }

        [Test]
        public void SliderRow_SnapsToTheSourceRangeAndMovesTheGrabber()
        {
            var row = RowFor("map_loading_screen_timer_mul");
            Assert.AreEqual(0.2f, row.Source.Min, Tolerance);
            Assert.AreEqual(3f, row.Source.Max, Tolerance);
            Assert.AreEqual(0.1f, row.Source.Step, Tolerance);

            var width = row.Slider.GetComponent<RectTransform>().rect.width;
            row.Slider.Configure(
                row.Source.Min, row.Source.Max, row.Source.Step,
                row.Slider.GrabberSize, null);

            // Dragging past the right edge lands on max, not beyond it.
            Assert.AreEqual(
                3f, row.Slider.ValueAt(width * 2f), Tolerance);
            Assert.AreEqual(
                0.2f, row.Slider.ValueAt(-100f), Tolerance);

            _settings.SetFloat("map_loading_screen_timer_mul", 10f);
            Assert.AreEqual(3f, _settings.MapIntroTimeMul, Tolerance);
        }

        [Test]
        public void Defaults_MatchTheSourceResourceOnDesktop()
        {
            Assert.AreEqual("en", _settings.Language);
            Assert.AreEqual(0, _settings.FpsLimit, "desktop starts uncapped");
            Assert.AreEqual(0.3f, _settings.MouseSensitivitySniper, Tolerance);
            Assert.AreEqual(19f, _settings.CameraStartDistance, Tolerance);
            Assert.AreEqual(1f, _settings.CameraHeightMul, Tolerance);
            Assert.AreEqual(0.75f, _settings.UiScaleMenu, Tolerance);
            Assert.AreEqual(0.75f, _settings.UiScaleBattle, Tolerance);
            Assert.IsFalse(_settings.AutoAimSoft);
            Assert.AreEqual(SettingsData.Time7, _settings.BattleMaxTime);
            Assert.AreEqual(SettingsData.Dev25, _settings.DmgDeviation);
            Assert.AreEqual(SettingsData.Dev5, _settings.PenDeviation);
            Assert.AreEqual(0.25f, _settings.BattleSpottingDelay, Tolerance);

            SettingsData.MobilePlatform = true;
            var phone = new SettingsData();
            Assert.AreEqual(60, phone.FpsLimit);
            // The client ships one arcade distance for every device.
            Assert.AreEqual(19f, phone.CameraStartDistance, Tolerance);
            Assert.AreEqual(0.6f, phone.CameraHeightMul, Tolerance);
            Assert.AreEqual(1f, phone.UiScaleBattle, Tolerance);
            Assert.IsTrue(phone.AutoAimSoft);
        }

        [Test]
        public void EverySetting_SurvivesASaveRoundTrip()
        {
            var rows = _screen["rows"];
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var name = row["setting"].AsString();
                switch (row["kind"].AsString())
                {
                    case "boolean":
                        _settings.SetBool(name, true);
                        break;
                    case "slider":
                        _settings.SetFloat(
                            name,
                            (row["min"].AsFloat() + row["max"].AsFloat()) * 0.5f);
                        break;
                    case "enum":
                        var last = row["options"][row["options"].Count - 1]["value"];
                        if (last.Kind == JsonKind.String)
                        {
                            _settings.SetString(name, last.AsString());
                        }
                        else
                        {
                            _settings.SetInt(name, last.AsInt());
                        }

                        break;
                }
            }

            var restored = SettingsData.FromJson(
                JsonNode.Parse(_settings.ToJson().ToJson()));
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var name = row["setting"].AsString();
                switch (row["kind"].AsString())
                {
                    case "boolean":
                        Assert.AreEqual(
                            _settings.GetBool(name), restored.GetBool(name), name);
                        break;
                    case "slider":
                        Assert.AreEqual(
                            _settings.GetFloat(name),
                            restored.GetFloat(name), 0.001f, name);
                        break;
                    case "enum":
                        if (name == "language")
                        {
                            Assert.AreEqual(_settings.Language, restored.Language);
                        }
                        else
                        {
                            Assert.AreEqual(
                                _settings.GetInt(name), restored.GetInt(name), name);
                        }

                        break;
                }
            }
        }

        [Test]
        public void EveryCaption_IsATranslationKeyTheTableCarries()
        {
            var lang = LocalizationTable.Parse(
                BlitzResources.LoadText(BlitzResources.LanguageData));
            Assert.IsTrue(lang.Has("SETTINGS"));
            foreach (var key in SettingsScreen.TabKeys)
            {
                Assert.IsTrue(lang.Has(key), key);
            }

            var rows = _screen["rows"];
            for (var i = 0; i < rows.Count; i++)
            {
                Assert.IsTrue(
                    lang.Has(rows[i]["labelKey"].AsString()),
                    rows[i]["labelKey"].AsString());
                var options = rows[i]["options"];
                for (var o = 0; o < options.Count; o++)
                {
                    Assert.IsTrue(
                        lang.Has(options[o]["textKey"].AsString()),
                        options[o]["textKey"].AsString());
                }
            }
        }

        [Test]
        public void GodotRange_SnapsAndDrawsLikeTheSourceSlider()
        {
            // Range::_set_value measures the step from min, so a range that
            // does not start on a multiple of the step keeps its own grid.
            Assert.AreEqual(0.3f, GodotRangeMath.Snap(0.33f, 0.2f, 3f, 0.1f), Tolerance);
            Assert.AreEqual(3f, GodotRangeMath.Snap(99f, 0.2f, 3f, 0.1f), Tolerance);
            Assert.AreEqual(0.2f, GodotRangeMath.Snap(-1f, 0.2f, 3f, 0.1f), Tolerance);

            // Slider::_draw: the fill runs to the centre of the grabber and
            // the grabber itself never hangs over either end.
            Assert.AreEqual(66f, GodotRangeMath.FillWidth(632f, 132f, 0f, 0f, 1f), Tolerance);
            Assert.AreEqual(566f, GodotRangeMath.FillWidth(632f, 132f, 1f, 0f, 1f), Tolerance);
            Assert.AreEqual(0f, GodotRangeMath.GrabberLeft(632f, 132f, 0f, 0f, 1f), Tolerance);
            Assert.AreEqual(500f, GodotRangeMath.GrabberLeft(632f, 132f, 1f, 0f, 1f), Tolerance);

            Assert.AreEqual("1", GodotRangeMath.Text(1f));
            Assert.AreEqual("0.6", GodotRangeMath.Text(0.6f));
        }

        /// <summary>
        /// Every tab of ui_settings.tscn sits in a ScrollContainer, whose
        /// mouse_filter is STOP and whose horizontal_scroll_mode is disabled.
        /// The port has to give that area a raycast target of its own: every
        /// graphic it builds leaves UiFactory with raycastTarget off, and an
        /// untargeted ScrollRect is handed no wheel and no drag at all.
        /// </summary>
        [Test]
        public void EveryTab_ScrollsVerticallyAndCatchesThePointer()
        {
            for (var i = 0; i < SettingsScreen.TabNames.Length; i++)
            {
                var name = SettingsScreen.TabNames[i];
                var page = Find(name);

                var scroll = page.GetComponent<ScrollRect>();
                Assert.IsNotNull(scroll, name + " has no ScrollContainer");
                Assert.IsTrue(scroll.vertical, name + " cannot scroll");
                Assert.IsFalse(
                    scroll.horizontal,
                    name + " scrolls sideways; horizontal_scroll_mode is 0");
                Assert.AreSame(page, scroll.viewport);
                Assert.IsNotNull(scroll.content, name + " has no content");
                Assert.Greater(
                    scroll.content.sizeDelta.y, 0f,
                    name + " has nothing to scroll");

                var catcher = page.GetComponent<Image>();
                Assert.IsNotNull(catcher, name + " catches no pointer");
                Assert.IsTrue(
                    catcher.raycastTarget, name + " is deaf to the wheel");
            }
        }

        // ------------------------------------------------------------------
        // Helpers.
        // ------------------------------------------------------------------

        private SettingsScreen.Row RowFor(string setting)
        {
            for (var i = 0; i < _ui.Rows.Count; i++)
            {
                if (_ui.Rows[i].Source.Setting == setting)
                {
                    return _ui.Rows[i];
                }
            }

            Assert.Fail("the screen has no row for " + setting);
            return null;
        }

        private RectTransform Find(string name)
        {
            var found = FindIn(_ui.Root, name);
            Assert.IsNotNull(found, "the screen has no node " + name);
            return found;
        }

        private static RectTransform FindIn(Transform parent, string name)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name)
                {
                    return child as RectTransform;
                }

                var found = FindIn(child, name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static void AssertRect(RectTransform rt, JsonNode node)
        {
            var anchors = node["anchors"];
            var offsets = node["offsets"];
            var expected = GodotAnchors.Convert(
                anchors[0].AsFloat(), anchors[1].AsFloat(),
                anchors[2].AsFloat(), anchors[3].AsFloat(),
                offsets[0].AsFloat(), offsets[1].AsFloat(),
                offsets[2].AsFloat(), offsets[3].AsFloat());

            Assert.AreEqual(expected.AnchorMinX, rt.anchorMin.x, Tolerance, "anchorMin.x");
            Assert.AreEqual(expected.AnchorMinY, rt.anchorMin.y, Tolerance, "anchorMin.y");
            Assert.AreEqual(expected.AnchorMaxX, rt.anchorMax.x, Tolerance, "anchorMax.x");
            Assert.AreEqual(expected.AnchorMaxY, rt.anchorMax.y, Tolerance, "anchorMax.y");
            Assert.AreEqual(expected.OffsetMinX, rt.offsetMin.x, Tolerance, "offsetMin.x");
            Assert.AreEqual(expected.OffsetMinY, rt.offsetMin.y, Tolerance, "offsetMin.y");
            Assert.AreEqual(expected.OffsetMaxX, rt.offsetMax.x, Tolerance, "offsetMax.x");
            Assert.AreEqual(expected.OffsetMaxY, rt.offsetMax.y, Tolerance, "offsetMax.y");
        }
    }
}
