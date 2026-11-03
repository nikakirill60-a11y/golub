using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Time;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// screens/main/menu, the hangar's burger drawer.
    ///
    /// Every number the port hardcodes is checked against
    /// Resources/Data/hangar_menu_source_manifest.json, which
    /// Tools/port_godot_hangar_menu.py generates from a headless dump of
    /// ui_hangar.tscn. If the drawer is re-dumped and the source turns out to
    /// differ, these tests fail instead of the port silently drifting.
    /// </summary>
    public sealed class HangarMenuTests
    {
        private const float Tolerance = 0.0005f;

        private JsonNode _manifest;
        private HangarUiKit _kit;
        private TanksDatabase _tanks;
        private AccountData _account;
        private TankResearchService _research;
        private GameObject _root;

        [SetUp]
        public void SetUp()
        {
            var text = Resources.Load<TextAsset>("Data/hangar_menu_source_manifest");
            Assert.IsNotNull(text, "hangar menu source manifest is missing");
            _manifest = JsonNode.Parse(text.text);

            _kit = new HangarUiKit(
                AtlasRegionTable.Parse(
                    BlitzResources.LoadText(BlitzResources.AtlasRegionsData)),
                LocalizationTable.Parse(
                    BlitzResources.LoadText(BlitzResources.LanguageData)),
                BlitzResources.LoadFont(BlitzResources.RegularFontName),
                BlitzResources.LoadFont(BlitzResources.BoldFontName),
                null);

            _tanks = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            _account = new AccountData();
            _research = new TankResearchService(_account, _tanks);
            _account.AddTank("ms-1", true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        // ------------------------------------------------------------------
        // Contract against the Godot dump.
        // ------------------------------------------------------------------

        [Test]
        public void Panel_MatchesTheDumpedGodotGeometry()
        {
            var panel = _manifest["panel"];
            Assert.AreEqual(HangarMenuPanel.Width, panel["width"].AsFloat(), Tolerance);
            Assert.AreEqual(HangarMenuPanel.ClosedX, panel["closedX"].AsFloat(), Tolerance);
            Assert.AreEqual(HangarMenuPanel.OpenX, panel["openX"].AsFloat(), Tolerance);
            Assert.AreEqual(
                HangarMenuPanel.Separation, panel["separation"].AsFloat(), Tolerance);

            // anchors: left, top, right, bottom. Only the top one is not a
            // preset value, and it is what parks the drawer under the header.
            Assert.AreEqual(0f, panel["anchors"][0].AsFloat(), Tolerance);
            Assert.AreEqual(
                HangarMenuPanel.AnchorTop, panel["anchors"][1].AsFloat(), Tolerance);
            Assert.AreEqual(0f, panel["anchors"][2].AsFloat(), Tolerance);
            Assert.AreEqual(1f, panel["anchors"][3].AsFloat(), Tolerance);

            var edge = panel["edgeStrip"];
            Assert.AreEqual(
                -HangarMenuPanel.EdgeStripWidth, edge["offsets"][0].AsFloat(), Tolerance);
            Assert.AreEqual(
                HangarMenuPanel.EdgeStripWidth, edge["rect"][2].AsFloat(), Tolerance);
            Assert.AreEqual(1f, edge["anchors"][0].AsFloat(), Tolerance);
            Assert.AreEqual(0f, edge["color"][0].AsFloat(), Tolerance);
            Assert.AreEqual(1f, edge["color"][3].AsFloat(), Tolerance);

            // blur.tscn is approximated by the shared ScreenTint, but the
            // colour it is derived from has to stay the shader's own.
            var blur = panel["blur"]["parameters"]["color"];
            Assert.AreEqual(
                HangarUiKit.BlurSourceColor.r, blur[0].AsFloat(), Tolerance);
            Assert.AreEqual(
                HangarUiKit.BlurSourceColor.g, blur[1].AsFloat(), Tolerance);
            Assert.AreEqual(
                HangarUiKit.BlurSourceColor.b, blur[2].AsFloat(), Tolerance);
        }

        [Test]
        public void ShowMenuTrack_MatchesTheDumpedAnimation()
        {
            var animation = _manifest["animation"];
            Assert.AreEqual("show_menu", animation["name"].AsString());
            Assert.IsTrue(animation["closesBackwards"].AsBool(),
                "closing the drawer must reuse show_menu backwards");

            // Animation.INTERPOLATION_LINEAR, i.e. no easing to reproduce.
            Assert.AreEqual(1, animation["interpolation"].AsInt());
            Assert.AreEqual(
                HangarMenuPanel.SlideSeconds, animation["seconds"].AsFloat(), Tolerance);

            var keys = animation["keys"];
            Assert.AreEqual(2, keys.Count);
            Assert.AreEqual(0f, keys[0][0].AsFloat(), Tolerance);
            Assert.AreEqual(HangarMenuPanel.ClosedX, keys[0][1][0].AsFloat(), Tolerance);
            Assert.AreEqual(
                HangarMenuPanel.SlideSeconds, keys[1][0].AsFloat(), 0.0001f);
            Assert.AreEqual(HangarMenuPanel.OpenX, keys[1][1][0].AsFloat(), Tolerance);

            // The RESET track is the closed pose, which is why a fresh hangar
            // starts with the drawer parked off-screen.
            Assert.AreEqual(
                HangarMenuPanel.ClosedX, animation["reset"][0].AsFloat(), Tolerance);
        }

        [Test]
        public void Buttons_MatchTheDumpedRectsAndCaptions()
        {
            var logout = _manifest["buttons"]["logout"];
            Assert.AreEqual(0f, logout["rect"][1].AsFloat(), Tolerance);
            Assert.AreEqual(HangarMenuPanel.Width, logout["rect"][2].AsFloat(), Tolerance);
            Assert.AreEqual(
                HangarMenuPanel.ButtonHeight, logout["rect"][3].AsFloat(), Tolerance);
            Assert.AreEqual(
                HangarMenuPanel.ButtonFontSize, logout["fontSize"].AsInt());
            Assert.AreEqual("LOGOUT", logout["text"].AsString());
            Assert.AreEqual("ButtonRed", logout["variation"].AsString());

            var settings = _manifest["buttons"]["settings"];
            Assert.AreEqual(
                HangarMenuPanel.SettingsTop, settings["rect"][1].AsFloat(), Tolerance);
            Assert.AreEqual(
                HangarMenuPanel.ButtonHeight + HangarMenuPanel.Separation,
                settings["rect"][1].AsFloat(),
                Tolerance);
            Assert.AreEqual("SETTINGS", settings["text"].AsString());
            Assert.AreEqual(
                HangarMenuPanel.IconScale, settings["icon"]["scale"].AsFloat(), 0.0001f);

            // The two captions are the only user-facing strings the drawer has
            // outside the debug toolbox, and they come from lang.json.
            Assert.AreNotEqual(string.Empty, _kit.Str("LOGOUT"));
            Assert.AreNotEqual(string.Empty, _kit.Str("SETTINGS"));
        }

        [Test]
        public void OpenButton_KeepsTheSourceSlideSound()
        {
            var open = _manifest["openButton"];
            Assert.IsTrue(open["toggleMode"].AsBool(), "btn_menu is a toggle");
            Assert.AreEqual(3, open["sndVar"].AsInt());
            Assert.AreEqual("assets/sfx/ui/filter_open.wav", open["sound"].AsString());
            Assert.AreEqual(50, open["iconMaxWidth"].AsInt());
        }

        [Test]
        public void Toolbox_MatchesTheDumpedRows()
        {
            var toolbox = _manifest["toolbox"];
            Assert.IsTrue(toolbox["debugBuildOnly"].AsBool());
            Assert.AreEqual(
                HangarAccountToolbox.Height, toolbox["height"].AsFloat(), Tolerance);
            Assert.AreEqual(
                HangarMenuPanel.ToolboxTop, toolbox["rect"][1].AsFloat(), Tolerance);
            Assert.AreEqual(
                HangarAccountToolbox.Width, toolbox["rect"][2].AsFloat(), Tolerance);
            Assert.AreEqual(
                HangarAccountToolbox.InnerHeight,
                toolbox["innerRect"][3].AsFloat(),
                Tolerance);

            // Row pitch and the two column widths, read back off the dump.
            var rows = toolbox["rows"];
            var credits = FindRow(rows, "btn_set_credits");
            var gold = FindRow(rows, "btn_set_gold");
            var field = FindRow(rows, "tedit_set_credits");
            Assert.AreEqual(34f, gold["rect"][1].AsFloat() - credits["rect"][1].AsFloat(),
                Tolerance);
            Assert.AreEqual(142f, credits["rect"][2].AsFloat(), Tolerance);
            Assert.AreEqual(146f, field["rect"][0].AsFloat(), Tolerance);
            Assert.AreEqual(143f, field["rect"][2].AsFloat(), Tolerance);

            // The captions are Godot literals, so they live in lang.json like
            // every other string the port shows.
            var added = _manifest["localization"]["added"];
            foreach (var pair in added.Fields)
            {
                Assert.AreEqual(
                    pair.Value["caption"].AsString(),
                    _kit.Str(pair.Key),
                    "lang.json lost " + pair.Key);
            }
        }

        // ------------------------------------------------------------------
        // The slide itself.
        // ------------------------------------------------------------------

        [Test]
        public void SlideX_SamplesTheTrackAndClampsOutsideIt()
        {
            Assert.AreEqual(
                HangarMenuPanel.ClosedX, HangarMenuPanel.SlideX(0f), Tolerance);
            Assert.AreEqual(
                HangarMenuPanel.OpenX,
                HangarMenuPanel.SlideX(HangarMenuPanel.SlideSeconds),
                Tolerance);
            Assert.AreEqual(
                HangarMenuPanel.ClosedX * 0.5f,
                HangarMenuPanel.SlideX(HangarMenuPanel.SlideSeconds * 0.5f),
                Tolerance);
            Assert.AreEqual(
                HangarMenuPanel.ClosedX, HangarMenuPanel.SlideX(-1f), Tolerance);
            Assert.AreEqual(HangarMenuPanel.OpenX, HangarMenuPanel.SlideX(9f), Tolerance);
        }

        [Test]
        public void Drawer_StartsParkedAndReversesFromWhereverItIs()
        {
            var panel = CreatePanel();
            Assert.IsFalse(panel.IsOpen);
            Assert.IsFalse(panel.IsVisible);
            Assert.AreEqual(HangarMenuPanel.ClosedX, panel.PositionX, Tolerance);

            panel.Toggle();
            Assert.IsTrue(panel.IsOpen, "btn_menu.toggled(true) opens the drawer");

            panel.Advance(HangarMenuPanel.SlideSeconds * 0.5f);
            var half = panel.PositionX;
            Assert.AreEqual(HangarMenuPanel.ClosedX * 0.5f, half, 0.01f);
            Assert.IsTrue(panel.IsVisible);

            // play_backwards() resumes from the playhead: a drawer caught half
            // way out travels back the short way, it does not jump open first.
            panel.Toggle();
            panel.Advance(HangarMenuPanel.SlideSeconds * 0.25f);
            Assert.Less(panel.PositionX, half);

            panel.Advance(HangarMenuPanel.SlideSeconds);
            Assert.AreEqual(HangarMenuPanel.ClosedX, panel.PositionX, Tolerance);
            Assert.IsFalse(panel.IsVisible);

            // Overshooting a step never runs past the keys at either end.
            panel.SetOpen(true);
            panel.Advance(10f);
            Assert.AreEqual(HangarMenuPanel.OpenX, panel.PositionX, Tolerance);
        }

        [Test]
        public void Drawer_BuildsTheSourceChildrenAndStaysInTheTree()
        {
            var panel = CreatePanel();
            Assert.IsNotNull(panel.Logout, "btn_logout is missing");
            Assert.IsNotNull(panel.Settings, "btn_settings is missing");

            // Godot animates the position instead of hiding the node, so the
            // drawer has to stay active even when it is parked off-screen.
            Assert.IsTrue(panel.gameObject.activeSelf);

            var logout = (RectTransform)panel.Logout.transform;
            Assert.AreEqual(
                HangarMenuPanel.Width, logout.rect.width, 0.01f);
            Assert.AreEqual(
                HangarMenuPanel.ButtonHeight, logout.rect.height, 0.01f);
            Assert.IsNotNull(logout.Find("icon"), "btn_logout lost its icon");
            Assert.AreEqual(
                _kit.Str("LOGOUT"), logout.Find("label").GetComponent<Text>().text);

            var pressed = 0;
            panel.LogoutPressed = () => pressed++;
            panel.Logout.onClick.Invoke();
            Assert.AreEqual(1, pressed);
        }

        /// <summary>
        /// MODS belongs directly under SETTINGS, not below the debug toolbox:
        /// it used to be placed against the toolbox's tinted background
        /// (246 px) while the toolbox's rows actually run 270 px, so the game
        /// button ended up buried under the debug rows. Squeezing the column
        /// with a uniform localScale hid that, but it also narrowed every
        /// button while the blur backdrop and edge strip kept the full 289 px.
        /// </summary>
        [Test]
        public void Mods_SitsUnderSettingsAndTheDrawerKeepsItsWidth()
        {
            var panel = CreatePanel();
            panel.BuildToolbox(CreateToolbox());

            Assert.IsNotNull(panel.Mods, "btn_mods is missing");
            Assert.AreEqual(
                HangarMenuPanel.SettingsTop + HangarMenuPanel.ButtonHeight +
                    HangarMenuPanel.Separation,
                HangarMenuPanel.ModsTop,
                Tolerance,
                "MODS is not in the slot under SETTINGS");

            // The toolbox follows MODS and the whole column still clears the
            // hangar footer: the footer plate sits at y = 593.333 and the
            // drawer starts at 0.16 of a 733.333 px canvas, so the column has
            // 476 px of drawer space before it runs under it.
            Assert.AreEqual(
                HangarMenuPanel.ModsTop + HangarMenuPanel.ButtonHeight +
                    HangarMenuPanel.Separation,
                HangarMenuPanel.ToolboxRuntimeTop,
                Tolerance);
            Assert.LessOrEqual(
                HangarMenuPanel.ToolboxRuntimeTop + HangarAccountToolbox.InnerHeight,
                476f,
                "the drawer column runs under the hangar footer");

            // No scaling anywhere: every row spans the drawer's full width.
            Assert.AreEqual(
                Vector3.one, panel.transform.Find("VBoxContainer").localScale);
            foreach (var name in new[] { "btn_logout", "btn_settings", "btn_mods" })
            {
                var button = panel.transform.Find("VBoxContainer/" + name);
                Assert.IsNotNull(button, name + " is missing");
                Assert.AreEqual(
                    HangarMenuPanel.Width,
                    ((RectTransform)button).rect.width,
                    0.01f,
                    name + " no longer spans the drawer");
            }

            if (HangarAccountToolbox.Enabled)
            {
                Assert.IsNotNull(panel.Toolbox, "the debug toolbox is missing");
                Assert.AreEqual(Vector3.one, panel.Toolbox.Root.localScale);
            }
        }

        // ------------------------------------------------------------------
        // ui_account_toolbox.gd.
        // ------------------------------------------------------------------

        [Test]
        public void Toolbox_ErasesRewardsAndBattlesWithoutTouchingAnythingElse()
        {
            var toolbox = CreateToolbox();
            var persisted = 0;
            toolbox.AccountChanged = () => persisted++;
            _account.Rewards["credits"] = 10;
            _account.BattleResults.Add(new BattleResultData());
            _account.Credits = 7;

            toolbox.EraseRewards();
            Assert.AreEqual(0, _account.Rewards.Count);
            Assert.AreEqual(1, _account.BattleResults.Count);

            toolbox.EraseBattles();
            Assert.AreEqual(0, _account.BattleResults.Count);
            Assert.AreEqual(7, _account.Credits);
            Assert.AreEqual(2, persisted);
        }

        [Test]
        public void Toolbox_CurrencySettersRefreshAndPersistTheAccount()
        {
            var toolbox = CreateToolbox();
            var refreshed = 0;
            var rebuilt = 0;
            toolbox.CurrenciesChanged = () => refreshed++;
            toolbox.AccountChanged = () => rebuilt++;

            toolbox.SetCredits(1234);
            toolbox.SetGold(56);
            toolbox.SetFreeXp(78);

            Assert.AreEqual(1234, _account.Credits);
            Assert.AreEqual(56, _account.Gold);
            Assert.AreEqual(78, _account.FreeXp);

            // The port persists every debug mutation so the values survive a
            // restart; the real hangar callback also refreshes the labels.
            Assert.AreEqual(0, refreshed);
            Assert.AreEqual(3, rebuilt);
        }

        [Test]
        public void Toolbox_AddsPremiumHoursFromNowAndThenFromTheExpiry()
        {
            var clock = new FixedClock(1000d);
            var toolbox = new HangarAccountToolbox(
                _kit, _account, _tanks, _research, clock);
            var persisted = 0;
            toolbox.AccountChanged = () => persisted++;

            toolbox.AddPremHours(2);
            Assert.AreEqual(1000d + 7200d, _account.PremAccountExpiration, 0.001d);

            // Still premium, so the second batch stacks on the expiry.
            toolbox.AddPremHours(1);
            Assert.AreEqual(1000d + 10800d, _account.PremAccountExpiration, 0.001d);

            // Expired, so validate() pulls the stale expiry up to now first.
            clock.NowUnixSeconds = 100000d;
            toolbox.AddPremHours(1);
            Assert.AreEqual(100000d + 3600d, _account.PremAccountExpiration, 0.001d);
            Assert.AreEqual(3, persisted);
        }

        [Test]
        public void Toolbox_UnlocksEveryTankOnceAndKeepsTheSaveValid()
        {
            var toolbox = CreateToolbox();
            var rebuilt = 0;
            toolbox.AccountChanged = () => rebuilt++;

            toolbox.UnlockAllTanks();

            var expected = 0;
            for (var i = 0; i < _tanks.Tanks.Count; i++)
            {
                if (!_tanks.Tanks[i].Hidden && _tanks.Tanks[i].Name != TankSaveData.DefaultName)
                {
                    expected++;
                }
            }

            Assert.AreEqual(expected, _account.TanksBoughtCount);
            Assert.AreEqual(expected, _account.Tanks.Count);
            Assert.AreEqual(1, rebuilt, "the hangar is rebuilt once, not per tank");
            Assert.IsNull(_account.GetTank("oth10_warduck"), "the World of Ducks card stays out of the hangar");

            // Running it twice must not duplicate the cards.
            toolbox.UnlockAllTanks();
            Assert.AreEqual(expected, _account.Tanks.Count);

            var t62a = _account.GetTank("t62a");
            Assert.IsNotNull(t62a);
            Assert.IsTrue(t62a.Bought);
            Assert.AreEqual(ResearchStatus.InstalledBought, _research.TankStatus("t62a"));
        }

        [Test]
        public void Toolbox_FullUpgradeInstallsTopModulesAndFillsTheRack()
        {
            var toolbox = CreateToolbox();
            var info = _tanks.Get("ms-1");
            var save = _account.GetTank("ms-1");
            save.GunId = 0;
            for (var i = 0; i < save.ShellsAmounts.Count; i++)
            {
                save.ShellsAmounts[i] = 0;
            }

            toolbox.FullUpgradeAllTanks();

            var guns = TankResearchService.UniqueModules(info.Guns);
            var engines = TankResearchService.UniqueModules(info.Engines);
            Assert.AreEqual(guns.Count - 1, save.GunId);
            Assert.AreEqual(engines.Count - 1, save.EngineId);
            Assert.Contains(
                guns[guns.Count - 1].Name,
                _account.ResearchedModules,
                "the whole module tree is marked researched");

            var gun = (GunInfo)guns[save.GunId];
            var loaded = 0;
            for (var i = 0; i < save.ShellsAmounts.Count; i++)
            {
                loaded += save.ShellsAmounts[i];
            }

            Assert.AreEqual(gun.MaxAmmo, loaded, "the rack is filled to capacity");
        }

        [Test]
        public void FullAmmoRack_SpreadsCapacityAndGivesTheRemainderToTheFirstShell()
        {
            var gun = new GunInfo { MaxAmmo = 100 };
            gun.Shells.Add(new ShellInfo());
            gun.Shells.Add(new ShellInfo());
            gun.Shells.Add(new ShellInfo());

            var rack = HangarAccountToolbox.FullAmmoRack(gun, 3);
            Assert.AreEqual(34, rack[0]);
            Assert.AreEqual(33, rack[1]);
            Assert.AreEqual(33, rack[2]);

            // A gun with fewer shell types than slots leaves the rest empty.
            var two = new GunInfo { MaxAmmo = 41 };
            two.Shells.Add(new ShellInfo());
            two.Shells.Add(new ShellInfo());
            rack = HangarAccountToolbox.FullAmmoRack(two, 3);
            Assert.AreEqual(21, rack[0]);
            Assert.AreEqual(20, rack[1]);
            Assert.AreEqual(0, rack[2]);

            Assert.AreEqual(3, HangarAccountToolbox.FullAmmoRack(null, 3).Count);
        }

        [Test]
        public void ParseAmount_MatchesGodotsIntCast()
        {
            Assert.AreEqual(0, HangarAccountToolbox.ParseAmount(null));
            Assert.AreEqual(0, HangarAccountToolbox.ParseAmount(string.Empty));
            Assert.AreEqual(0, HangarAccountToolbox.ParseAmount("abc"));
            Assert.AreEqual(12, HangarAccountToolbox.ParseAmount("12.7"));
            Assert.AreEqual(12, HangarAccountToolbox.ParseAmount(" 12abc"));
            Assert.AreEqual(-5, HangarAccountToolbox.ParseAmount("-5"));
            Assert.AreEqual(5, HangarAccountToolbox.ParseAmount("+5"));

            // Currencies are 64-bit like Godot's ints: entries past the old
            // 32-bit cap are kept, and only one past long.MaxValue saturates
            // rather than wrapping into the opposite sign.
            Assert.AreEqual(99999999999L, HangarAccountToolbox.ParseAmount("99999999999"));
            Assert.AreEqual(
                long.MaxValue, HangarAccountToolbox.ParseAmount("9223372036854775807"));
            Assert.AreEqual(
                long.MaxValue, HangarAccountToolbox.ParseAmount("99999999999999999999"));
            Assert.AreEqual(
                long.MinValue, HangarAccountToolbox.ParseAmount("-99999999999999999999"));
        }

        [Test]
        public void FormatNumber_GroupsSixtyFourBitBalances()
        {
            Assert.AreEqual("35 000", HangarUiKit.FormatNumber(35000));
            Assert.AreEqual("3 000 000 000", HangarUiKit.FormatNumber(3000000000L));
            Assert.AreEqual(
                "9 223 372 036 854 775 807", HangarUiKit.FormatNumber(long.MaxValue));
            Assert.AreEqual(
                "-9 223 372 036 854 775 808", HangarUiKit.FormatNumber(long.MinValue));
        }

        // ------------------------------------------------------------------

        private HangarMenuPanel CreatePanel()
        {
            _root = new GameObject("menu_test_root", typeof(RectTransform));
            var parent = (RectTransform)_root.transform;
            parent.sizeDelta = new Vector2(1386f, 780f);
            return HangarMenuPanel.Create(parent, _kit);
        }

        private HangarAccountToolbox CreateToolbox()
        {
            return new HangarAccountToolbox(
                _kit, _account, _tanks, _research, new FixedClock(0d));
        }

        private static JsonNode FindRow(JsonNode rows, string name)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i]["name"].AsString() == name)
                {
                    return rows[i];
                }
            }

            Assert.Fail("the dump has no row named " + name);
            return null;
        }
    }
}
