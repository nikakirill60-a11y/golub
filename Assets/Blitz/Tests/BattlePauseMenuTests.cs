using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Platform;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// menu_bg from ui_player.tscn and popup_generic.tscn.
    ///
    /// Every number the port hardcodes is checked against
    /// Resources/Data/battle_menu_source_manifest.json, which
    /// Tools/port_godot_battle_menu.py generates from a headless dump of the
    /// two Godot scenes. If the source is re-dumped and turns out to differ,
    /// these tests fail instead of the port silently drifting.
    /// </summary>
    public sealed class BattlePauseMenuTests
    {
        private const float Tolerance = 0.0005f;

        private JsonNode _manifest;
        private GameObject _root;
        private BattlePauseMenu _menu;

        [SetUp]
        public void SetUp()
        {
            var text = Resources.Load<TextAsset>("Data/battle_menu_source_manifest");
            Assert.IsNotNull(text, "battle menu source manifest is missing");
            _manifest = JsonNode.Parse(text.text);

            _root = new GameObject("BattlePauseMenuTests");
            _menu = BattlePauseMenu.Create(_root.transform, false);

            // input_camera._ready captures the pointer for a live battle, and
            // the menu action is only meaningful from that state.
            MouseLock.Set(true);
        }

        [TearDown]
        public void TearDown()
        {
            MouseLock.Set(false);
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }

            _menu = null;
        }

        // ------------------------------------------------------------------
        // Geometry against the Godot dump.
        // ------------------------------------------------------------------

        [Test]
        public void MenuBackground_CoversTheScreenAndStartsHidden()
        {
            var background = _manifest["menu"]["background"];
            Assert.IsFalse(background["visible"].AsBool(), "menu_bg is authored hidden");
            Assert.AreEqual(2, background["mouseFilter"].AsInt(),
                "menu_bg is mouse_filter IGNORE in the source");

            AssertRect(_menu.MenuRoot, background);
            Assert.IsFalse(_menu.Visible, "the menu starts hidden, as menu.hide() leaves it");
            Assert.IsNull(
                _menu.MenuRoot.GetComponent<Graphic>(),
                "an IGNORE control takes no clicks, so the port draws nothing there");
        }

        [Test]
        public void Panel_MatchesTheDumpedGodotGeometry()
        {
            var panel = _manifest["menu"]["panel"];
            AssertRect(_menu.Panel.rectTransform, panel);
            Assert.AreEqual(
                panel["scale"][0].AsFloat(),
                _menu.Panel.rectTransform.localScale.x, Tolerance);
            Assert.AreEqual(
                panel["scale"][1].AsFloat(),
                _menu.Panel.rectTransform.localScale.y, Tolerance);
            Assert.AreEqual(
                BattlePauseMenu.PanelScale, panel["scale"][0].AsFloat(), Tolerance);

            // grey_panel.tscn: the header strip is a child of the body, so the
            // body's 0.5 scale carries it.
            var strip = _menu.Panel.transform.Find("header") as RectTransform;
            Assert.IsNotNull(strip, "the panel header strip is missing");
            AssertRect(strip, _manifest["menu"]["header"]);
        }

        [Test]
        public void Title_MatchesTheDumpedLabel()
        {
            var title = _manifest["menu"]["title"];
            AssertRect(_menu.Title.rectTransform, title);
            Assert.AreEqual(title["fontSize"].AsInt(), _menu.Title.fontSize);
            Assert.AreEqual(BattlePauseMenu.TitleFontSize, title["fontSize"].AsInt());
            Assert.AreEqual(Lang().Get(title["text"].AsString()), _menu.Title.text);
            Assert.AreEqual(TextAnchor.UpperCenter, _menu.Title.alignment,
                "horizontal_alignment 1 with vertical_alignment 0");
        }

        [TestCase("hangar", "TO HANGAR")]
        [TestCase("settings", "SETTINGS")]
        [TestCase("resume", "RESUME")]
        public void Buttons_MatchTheDumpedGeometryAndCaptions(string id, string key)
        {
            var node = _manifest["menu"]["buttons"][id];
            var button = id == "hangar" ? _menu.Hangar
                : id == "settings" ? _menu.Settings : _menu.Resume;

            AssertRect((RectTransform)button.transform, node);
            Assert.AreEqual(key, node["text"].AsString(),
                "the caption key the source label carries");

            var label = button.GetComponentInChildren<Text>();
            Assert.AreEqual(Lang().Get(key), label.text);
            Assert.AreEqual(node["fontSize"].AsInt(), label.fontSize);
            Assert.AreEqual(BattlePauseMenu.ButtonFontSize, node["fontSize"].AsInt());

            // button_generic.gd: Audio.ui_enter at 0.7, which is the shared
            // click volume of the port.
            Assert.AreEqual(
                node["volume"].AsFloat(),
                BlitzLegacy.Runtime.Audio.AudioService.ButtonClickVolume, Tolerance);
        }

        [Test]
        public void Buttons_UseTheThemeVariationArtwork()
        {
            var atlas = BattleFlowUi.LoadAtlas();
            var styles = _manifest["styles"];
            var buttons = _manifest["menu"]["buttons"];

            Assert.AreEqual("ButtonRed", buttons["hangar"]["variation"].AsString());
            Assert.AreEqual("Button", buttons["settings"]["variation"].AsString());
            Assert.AreEqual("ButtonOrange", buttons["resume"]["variation"].AsString());

            AssertRegion(atlas, styles["ButtonRed"], _menu.Hangar);
            AssertRegion(atlas, styles["Button"], _menu.Settings);
            AssertRegion(atlas, styles["ButtonOrange"], _menu.Resume);
        }

        // ------------------------------------------------------------------
        // The "menu" action, one to one with player_controller.
        // ------------------------------------------------------------------

        [Test]
        public void MenuAction_TogglesTheWindowAndHandsThePointerOver()
        {
            Assert.IsTrue(MouseLock.Captured, "the battle starts with the pointer captured");

            _menu.Toggle();
            Assert.IsTrue(_menu.Visible);
            Assert.IsFalse(MouseLock.Captured,
                "Util.mouse_lock(menu.visible) runs before the flip, so the "
                + "pointer is released while the window is up");

            _menu.Toggle();
            Assert.IsFalse(_menu.Visible);
            Assert.IsTrue(MouseLock.Captured, "closing the window takes the pointer back");
        }

        [Test]
        public void MenuAction_NeverPausesTheTree()
        {
            var scale = Time.timeScale;
            _menu.Toggle();
            Assert.AreEqual(scale, Time.timeScale, Tolerance,
                "the source never calls get_tree().paused: the battle runs on");
            _menu.Toggle();
            Assert.AreEqual(scale, Time.timeScale, Tolerance);
        }

        [Test]
        public void Resume_HidesTheWindowAndRecapturesThePointer()
        {
            _menu.Toggle();
            Assert.IsTrue(_menu.Visible);

            _menu.Resume.onClick.Invoke();
            Assert.IsFalse(_menu.Visible);
            Assert.IsTrue(MouseLock.Captured);
        }

        [Test]
        public void Settings_RaisesTheRequestTheSettingsScreenWillAnswer()
        {
            var raised = 0;
            _menu.SettingsPressed = () => raised++;
            _menu.Settings.onClick.Invoke();
            Assert.AreEqual(1, raised);
            Assert.IsFalse(_menu.Visible, "opening settings does not close the menu");
        }

        // ------------------------------------------------------------------
        // popup_generic.tscn.
        // ------------------------------------------------------------------

        [Test]
        public void HangarButton_OpensTheConfirmationFromTheSource()
        {
            var behaviour = _manifest["popup"]["behaviour"];
            _menu.Toggle();
            _menu.Hangar.onClick.Invoke();

            var popup = _menu.Popup;
            Assert.IsNotNull(popup, "btn_hangar creates the CONFIRM popup");
            Assert.AreEqual(
                Lang().Get(behaviour["headerText"].AsString()), popup.Header.text);
            Assert.AreEqual(
                Lang().Get(behaviour["bodyKey"].AsString()), popup.Body.text);

            AssertRect(popup.Panel.rectTransform, _manifest["popup"]["panel"]);
            AssertRect(popup.Header.rectTransform, _manifest["popup"]["header"]);
            AssertRect(popup.Body.rectTransform, _manifest["popup"]["text"]);
            AssertRect(
                (RectTransform)popup.Ok.transform,
                _manifest["popup"]["buttons"]["ok"]);
            AssertRect(
                (RectTransform)popup.Cancel.transform,
                _manifest["popup"]["buttons"]["cancel"]);
            Assert.AreEqual(
                _manifest["popup"]["header"]["fontSize"].AsInt(), popup.Header.fontSize);
            Assert.AreEqual(
                _manifest["popup"]["text"]["fontSize"].AsInt(), popup.Body.fontSize);
        }

        [Test]
        public void PopupAccept_RunsTheCallbackAndClosesTheWindow()
        {
            var left = 0;
            _menu.HangarConfirmed = () => left++;

            _menu.Toggle();
            _menu.Hangar.onClick.Invoke();
            var popup = _menu.Popup;
            popup.Ok.onClick.Invoke();

            Assert.AreEqual(1, left, "btn_ok calls on_accept, i.e. Global.to_hangar");
            Assert.IsTrue(popup == null, "and the window frees itself afterwards");
        }

        [Test]
        public void PopupCancel_OnlyClosesTheWindow()
        {
            var left = 0;
            _menu.HangarConfirmed = () => left++;

            _menu.Toggle();
            _menu.Hangar.onClick.Invoke();
            var popup = _menu.Popup;
            popup.Cancel.onClick.Invoke();

            Assert.AreEqual(0, left);
            Assert.IsTrue(popup == null);
            Assert.IsTrue(_menu.Visible, "the menu itself stays up behind it");
        }

        [Test]
        public void MenuAction_FreesAnOpenPopup()
        {
            _menu.Toggle();
            _menu.Hangar.onClick.Invoke();
            var popup = _menu.Popup;
            Assert.IsNotNull(popup);

            _menu.Toggle();
            Assert.IsTrue(popup == null,
                "player_controller frees player_ui.popup with the same press");
            Assert.IsNull(_menu.Popup);
        }

        [Test]
        public void Captions_AreInTheShippedLanguageTable()
        {
            var lang = Lang();
            var required = _manifest["localization"]["required"];
            for (var i = 0; i < required.Count; i++)
            {
                Assert.IsTrue(lang.Has(required[i].AsString()), required[i].AsString());
            }

            foreach (var pair in _manifest["localization"]["added"].Fields)
            {
                Assert.IsTrue(lang.Has(pair.Key), pair.Key);
                Assert.AreEqual(
                    pair.Value["en"].AsString(), lang.Get(pair.Key, "en"), pair.Key);
                Assert.AreEqual(
                    pair.Value["ru"].AsString(), lang.Get(pair.Key, "ru"), pair.Key);
            }
        }

        // ------------------------------------------------------------------

        private static LocalizationTable Lang()
        {
            return LocalizationTable.Parse(
                BlitzResources.LoadText(BlitzResources.LanguageData));
        }

        private static void AssertRegion(
            AtlasRegionTable atlas, JsonNode style, Button button)
        {
            var sprite = button.GetComponent<Image>().sprite;
            Assert.IsNotNull(sprite, "the button has no artwork");

            // Every AtlasRegionTable.Parse mints its own sprites, so the
            // region is identified by name and by the rect it cuts.
            var expected = atlas.Get(RegionName(style));
            Assert.IsNotNull(expected, RegionName(style));
            Assert.AreEqual(expected.name, sprite.name, RegionName(style));
            Assert.AreEqual(expected.rect, sprite.rect, RegionName(style));
            Assert.AreEqual(style["region"][2].AsFloat(), sprite.rect.width, Tolerance);
            Assert.AreEqual(style["region"][3].AsFloat(), sprite.rect.height, Tolerance);
        }

        /// <summary>The atlas key the port gives each theme variation.</summary>
        private static string RegionName(JsonNode style)
        {
            var stylebox = style["stylebox"].AsString();
            if (stylebox.Contains("red")) { return "button_red"; }
            if (stylebox.Contains("orange")) { return "button_orange"; }
            if (stylebox.Contains("grn")) { return "button_green"; }
            return "button_generic";
        }

        private static void AssertRect(RectTransform rt, JsonNode node)
        {
            Assert.IsNotNull(rt, "missing control for " + node["path"].AsString());
            var anchors = node["anchors"];
            var offsets = node["offsets"];
            var expected = GodotAnchors.Convert(
                anchors[0].AsFloat(), anchors[1].AsFloat(),
                anchors[2].AsFloat(), anchors[3].AsFloat(),
                offsets[0].AsFloat(), offsets[1].AsFloat(),
                offsets[2].AsFloat(), offsets[3].AsFloat());

            var path = node["path"].AsString();
            Assert.AreEqual(expected.AnchorMinX, rt.anchorMin.x, Tolerance, path);
            Assert.AreEqual(expected.AnchorMinY, rt.anchorMin.y, Tolerance, path);
            Assert.AreEqual(expected.AnchorMaxX, rt.anchorMax.x, Tolerance, path);
            Assert.AreEqual(expected.AnchorMaxY, rt.anchorMax.y, Tolerance, path);
            Assert.AreEqual(expected.OffsetMinX, rt.offsetMin.x, Tolerance, path);
            Assert.AreEqual(expected.OffsetMinY, rt.offsetMin.y, Tolerance, path);
            Assert.AreEqual(expected.OffsetMaxX, rt.offsetMax.x, Tolerance, path);
            Assert.AreEqual(expected.OffsetMaxY, rt.offsetMax.y, Tolerance, path);
        }
    }
}
