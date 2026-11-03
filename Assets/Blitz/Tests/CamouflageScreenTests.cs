using System.Linq;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The Blitz 4.8.0 CamouflagesShop screen: the three slots, the list with
    /// the green frame of what the tank owns, the footer button with its
    /// price and confirmation, the badges, the preview and the stand's slide,
    /// in both menu styles.
    /// </summary>
    public sealed class CamouflageScreenTests
    {
        private const string StonySoil = "the_stony_soil";

        /// <summary>The ported strings and art with two camouflages: forest is summer only.</summary>
        private static readonly string TwoCamouflagesJson = (
            "{'base_price':2800,'concealment_bonus':0.02,'slots':['summer','winter','desert'],"
            + "'map_kinds':{'rudniki':'summer','moon':'winter','lake':'desert'},"
            + "'groups':{'standard':{'name':'#vehicle_customization:Camouflage/Standard/Name','rarity':10}},"
            + "'camouflages':{"
            + "'forest':{'id':1,'name':'#vehicle_customization:camouflage/name/TheStonySoil',"
            + "'description':'#vehicle_customization:Camouflage/Summer/Descr','group':'standard','kinds':['summer'],"
            + "'category':'disposable-cheap','tile':'camo_tile_the_stony_soil','slot_icon':'camo_slot_the_stony_soil'},"
            + "'rock':{'id':2,'name':'#vehicle_customization:camouflage/name/TheStonySoil',"
            + "'description':'#vehicle_customization:Camouflage/Universal/Descr','group':'standard',"
            + "'kinds':['summer','winter','desert'],"
            + "'category':'disposable-cheap','tile':'camo_tile_the_stony_soil','slot_icon':'camo_slot_the_stony_soil'}},"
            + "'tanks':{'t-34':{'pack':'480','price_factor':1,'price':2800,"
            + "'masks':{'T34':'Camouflages/Masks/480/USSR/CamouflageMasks/T-34_CM'},"
            + "'camouflages':{'forest':PAINT,'rock':PAINT}}},"
            + "'unsupported':{}}")
            .Replace("PAINT", "{'alpha':0.9,'scale':[0.5,0.5],'offset':[0,0],'texture':'Camouflages/Textures/480/common/common_01'}")
            .Replace('\'', '"');

        private RectTransform _canvas;
        private HangarUiKit _kit;
        private AccountData _account;
        private TankSaveData _save;
        private CamouflageScreen _screen;
        private HangarWorld _world;
        private int _changed;

        private void Build(
            bool modern, string tank = "t-34", long credits = 10000, CamouflageCatalog catalog = null,
            HangarWorld world = null)
        {
            _canvas = UiFactory.Node("camouflage_test", null);
            _canvas.sizeDelta = new Vector2(1386.67f, 733.33f);
            _canvas.gameObject.AddComponent<Canvas>();
            var atlas = AtlasRegionTable.Parse(Resources.Load<TextAsset>("Data/atlas_regions").text);
            if (modern) atlas.Skin = ModernSkin.Instance;
            var lang = LocalizationTable.Parse(Resources.Load<TextAsset>("Data/lang").text);
            lang.Language = "ru";
            _kit = new HangarUiKit(atlas, lang,
                BlitzResources.LoadFont(BlitzResources.RegularFontName),
                BlitzResources.LoadFont(BlitzResources.BoldFontName), null);
            _account = new AccountData { Credits = credits };
            _save = _account.AddTank(tank, true);
            _changed = 0;
            _screen = new CamouflageScreen(_kit, _account, world, modern, catalog);
            _screen.ModalLayer = _canvas;
            _screen.Settings = new SettingsData();
            _screen.Changed = () => _changed++;
            _screen.Build(_canvas);
            _screen.Init(_save);
            _screen.Show();
            Canvas.ForceUpdateCanvases();
        }

        private static string Caption(Button button)
        {
            return button.transform.Find("label").GetComponent<Text>().text;
        }

        [TearDown]
        public void TearDown()
        {
            if (_screen != null) _screen.Hide();
            if (_canvas != null) Object.DestroyImmediate(_canvas.gameObject);
            if (_world != null) Object.DestroyImmediate(_world.gameObject);
            _screen = null;
            _canvas = null;
            _world = null;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Purchase_AsksFirstThenPaysAndFillsTheSlot(bool modern)
        {
            Build(modern);
            Assert.IsTrue(_screen.Supported);
            Assert.AreEqual(CamouflageKind.Summer, _screen.SelectedSlot);
            Assert.AreEqual(StonySoil, _screen.SelectedCamouflage);
            Assert.AreEqual(1, _screen.ElementCount);
            Assert.IsFalse(_screen.NoneAvailableShown);
            Assert.AreEqual(CamouflageAction.Buy, _screen.CurrentAction);
            Assert.IsTrue(_screen.ApplyShown);
            Assert.AreEqual("КУПИТЬ", _screen.ApplyCaption);
            Assert.AreEqual("Стоимость:", _screen.PriceCaption);
            Assert.AreEqual(HangarUiKit.FormatNumber(2800), _screen.PriceText);
            Assert.IsTrue(_screen.PriceShown);
            Assert.IsTrue(_screen.ElementPriceShown(StonySoil));
            Assert.IsFalse(_screen.ElementOwnedShown(StonySoil));
            Assert.AreEqual("Каменистая почва", _screen.InfoTitle);
            StringAssert.StartsWith("+2% ", _screen.InfoText);
            Assert.AreEqual(string.Empty, _screen.SlotIcon(CamouflageKind.Summer));
            Assert.IsFalse(
                _screen.Root.GetComponentsInChildren<Text>(true).Any(t => t.text.StartsWith("#")),
                "every caption is translated");

            _screen.PressApply();
            var popup = _screen.ActivePopup;
            Assert.IsNotNull(popup);
            Assert.AreEqual("ПОДТВЕРЖДЕНИЕ", popup.Header.text);
            Assert.AreEqual("Купить камуфляж за " + HangarUiKit.FormatNumber(2800) + "?", popup.Body.text);
            Assert.AreEqual("КУПИТЬ", Caption(popup.Ok));
            Assert.AreEqual("ОТМЕНИТЬ", Caption(popup.Cancel));
            Assert.AreEqual(10000, _account.Credits, "the confirmation does not spend");

            popup.Accept();
            Assert.IsNull(_screen.ActivePopup);
            Assert.AreEqual(7200, _account.Credits);
            Assert.AreEqual(StonySoil, _save.Camouflages[(int)CamouflageKind.Summer]);
            CollectionAssert.AreEqual(new[] { StonySoil }, _save.OwnedCamouflages);
            Assert.AreEqual(CamouflageAction.Remove, _screen.CurrentAction);
            Assert.AreEqual("СНЯТЬ", _screen.ApplyCaption);
            Assert.IsFalse(_screen.PriceShown, "what the tank owns has no price");
            Assert.IsTrue(_screen.ElementInstalledShown(StonySoil));
            Assert.IsTrue(_screen.ElementOwnedShown(StonySoil));
            Assert.IsFalse(_screen.ElementPriceShown(StonySoil));
            Assert.AreEqual(new Rect(2f, 2f, 88f, 86f), _screen.ElementOwnedFrame(StonySoil),
                "picked, the green frame runs just inside the orange one, as thick");
            Assert.AreEqual("camo_slot_the_stony_soil", _screen.SlotIcon(CamouflageKind.Summer));
            Assert.AreEqual(string.Empty, _screen.SlotIcon(CamouflageKind.Winter));
            Assert.Greater(_changed, 1, "the seen mark and the purchase are saved");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OwnedCamouflage_ComesOffAndGoesBackFreeWithoutAsking(bool modern)
        {
            Build(modern);
            _save.Camouflages[(int)CamouflageKind.Winter] = StonySoil;
            _save.OwnedCamouflages.Add(StonySoil);
            _screen.SelectSlot(CamouflageKind.Winter);
            Assert.AreEqual(CamouflageAction.Remove, _screen.CurrentAction);
            Assert.IsFalse(_screen.PriceShown);
            Assert.AreEqual("camo_slot_the_stony_soil", _screen.SlotIcon(CamouflageKind.Winter));

            _screen.PressApply();
            Assert.IsNull(_screen.ActivePopup, "REMOVE does not ask");
            Assert.AreEqual(10000, _account.Credits, "removing refunds nothing");
            Assert.AreEqual(string.Empty, _save.Camouflages[(int)CamouflageKind.Winter]);
            Assert.AreEqual(string.Empty, _screen.SlotIcon(CamouflageKind.Winter));
            Assert.AreEqual(CamouflageAction.Install, _screen.CurrentAction);
            Assert.AreEqual("УСТАНОВИТЬ", _screen.ApplyCaption);
            Assert.IsFalse(_screen.PriceShown);
            Assert.IsTrue(_screen.ElementOwnedShown(StonySoil));
            Assert.IsFalse(_screen.ElementPriceShown(StonySoil));

            _screen.PressApply();
            Assert.IsNull(_screen.ActivePopup, "INSTALL does not ask");
            Assert.AreEqual(10000, _account.Credits);
            Assert.AreEqual(StonySoil, _save.Camouflages[(int)CamouflageKind.Winter]);
            Assert.AreEqual(CamouflageAction.Remove, _screen.CurrentAction);

            _screen.SelectSlot(CamouflageKind.Summer);
            Assert.AreEqual(CamouflageAction.Install, _screen.CurrentAction, "bought once, for every slot");
            _screen.PressApply();
            Assert.AreEqual(10000, _account.Credits);
            Assert.AreEqual(StonySoil, _save.Camouflages[(int)CamouflageKind.Summer]);
            CollectionAssert.AreEqual(new[] { StonySoil }, _save.OwnedCamouflages);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Purchase_WithoutTheCredits_ShowsTheError(bool modern)
        {
            Build(modern, "t-34", 1000);
            _screen.PressApply();
            var popup = _screen.ActivePopup;
            Assert.IsNotNull(popup);
            Assert.AreEqual("ОШИБКА!", popup.Header.text);
            Assert.AreEqual("Не хватает кредитов: " + HangarUiKit.FormatNumber(2800), popup.Body.text);
            Assert.IsTrue(_screen.HandleEscape(), "escape closes the popup, not the screen");
            popup.Accept();
            Assert.IsNull(_screen.ActivePopup);
            Assert.AreEqual(1000, _account.Credits);
            Assert.AreEqual(string.Empty, _save.Camouflages[(int)CamouflageKind.Summer]);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnotherCamouflage_IsAPurchaseOfItsOwnAndDimsWhatTheSlotCannotTake(bool modern)
        {
            Build(modern, "t-34", 10000, CamouflageCatalog.Parse(TwoCamouflagesJson));
            Assert.AreEqual(2, _screen.ElementCount);
            Assert.AreEqual("forest", _screen.SelectedCamouflage, "the first camouflage the slot takes");
            _screen.PressApply();
            _screen.ActivePopup.Accept();
            Assert.AreEqual(7200, _account.Credits);

            _screen.SelectCamouflage("rock");
            Assert.AreEqual(CamouflageAction.Buy, _screen.CurrentAction);
            Assert.AreEqual("КУПИТЬ", _screen.ApplyCaption);
            Assert.AreEqual(HangarUiKit.FormatNumber(2800), _screen.PriceText);
            Assert.IsTrue(_screen.ElementOwnedShown("forest"));
            Assert.AreEqual(new Rect(0f, 0f, 92f, 90f), _screen.ElementOwnedFrame("forest"),
                "not picked, the green frame takes the tile's edge");
            Assert.IsFalse(_screen.ElementPriceShown("forest"));
            Assert.IsFalse(_screen.ElementOwnedShown("rock"));
            Assert.IsTrue(_screen.ElementPriceShown("rock"));
            _screen.PressApply();
            var popup = _screen.ActivePopup;
            Assert.IsNotNull(popup);
            Assert.AreEqual("Купить камуфляж за " + HangarUiKit.FormatNumber(2800) + "?", popup.Body.text);
            popup.Accept();
            Assert.AreEqual(4400, _account.Credits);
            Assert.AreEqual("rock", _save.Camouflages[(int)CamouflageKind.Summer]);
            Assert.IsTrue(_screen.ElementInstalledShown("rock"));
            Assert.IsFalse(_screen.ElementInstalledShown("forest"));

            _screen.SelectCamouflage("forest");
            Assert.AreEqual(CamouflageAction.Install, _screen.CurrentAction);
            _screen.PressApply();
            Assert.IsNull(_screen.ActivePopup);
            Assert.AreEqual(4400, _account.Credits, "forest is the tank's already");
            Assert.AreEqual("forest", _save.Camouflages[(int)CamouflageKind.Summer]);

            _screen.SelectSlot(CamouflageKind.Winter);
            Assert.AreEqual("rock", _screen.SelectedCamouflage, "forest is summer only");
            _screen.SelectCamouflage("forest");
            Assert.AreEqual("rock", _screen.SelectedCamouflage, "a dimmed element cannot be picked");
            Assert.AreEqual(CamouflageAction.Install, _screen.CurrentAction);
            Assert.IsFalse(_screen.ElementPriceShown("forest"), "dimmed or not, an owned tile has no price");
        }

        [TestCase("gb_mark_i")]
        [TestCase("oth10_warduck")]
        public void UnsupportedVehicle_HasNoCamouflagesAvailable(string tank)
        {
            Build(false, tank);
            Assert.IsFalse(_screen.Supported);
            Assert.IsTrue(_screen.NoneAvailableShown);
            Assert.AreEqual(0, _screen.ElementCount);
            Assert.IsFalse(_screen.ApplyShown);
            Assert.AreEqual(CamouflageAction.None, _screen.CurrentAction);
            _screen.PressApply();
            Assert.IsNull(_screen.ActivePopup);
            Assert.AreEqual(10000, _account.Credits);
            CollectionAssert.IsEmpty(_save.OwnedCamouflages);
        }

        [Test]
        public void Badges_MarkNewCamouflagesUntilPicked()
        {
            Build(false);
            Assert.IsTrue(_save.CamouflageSeen, "opening the shop drops the hangar badge");
            Assert.AreEqual(1, _changed);
            Assert.IsTrue(_screen.SlotBadgeShown(CamouflageKind.Summer));
            Assert.IsTrue(_screen.ElementBadgeShown(StonySoil));

            _screen.SelectCamouflage(StonySoil);
            Assert.IsFalse(_screen.ElementBadgeShown(StonySoil));
            Assert.IsFalse(_screen.SlotBadgeShown(CamouflageKind.Summer));
            Assert.IsFalse(_screen.SlotBadgeShown(CamouflageKind.Winter));

            _screen.Init(_save);
            _screen.Show();
            Assert.IsFalse(_screen.ElementBadgeShown(StonySoil), "a second visit has nothing new");
            Assert.AreEqual(1, _changed);
        }

        [Test]
        public void Purchase_WarnsWhileBattleCamouflageIsHidden()
        {
            Build(false);
            var note = _kit.Str(CamouflageScreen.HiddenInBattleKey);
            _screen.Settings = new SettingsData { GraphicsCamouflage = SettingsData.CamouflageOff };
            _screen.PressApply();
            StringAssert.EndsWith("\n" + note, _screen.ActivePopup.Body.text);
            _screen.ActivePopup.Close();
            Assert.IsNull(_screen.ActivePopup);

            _screen.Settings = new SettingsData { GraphicsCamouflage = SettingsData.CamouflageForPlayer };
            _screen.PressApply();
            StringAssert.DoesNotContain(note, _screen.ActivePopup.Body.text);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Closing_SlidesTheStandBackTheWayItCame(bool modern)
        {
            _world = HangarWorld.Create(null, new SettingsData());
            Build(modern, world: _world);
            var orbit = _world.Orbit;
            var aside = CamouflageScreen.ContentWidth / _screen.Root.rect.width;
            Assert.AreEqual(aside, orbit.ViewShiftTarget, 1e-4f, "entering moves the stand right of the panels");
            Assert.AreEqual(0f, orbit.ViewShift, 1e-4f, "... sliding, not jumping");
            orbit.AdvanceViewShift(CamouflageScreen.SlideSeconds);
            Assert.AreEqual(aside, orbit.ViewShift, 1e-4f);

            _screen.Hide();
            Assert.AreEqual(aside, orbit.ViewShift, 1e-4f, "closing does not jump either");
            Assert.AreEqual(0f, orbit.ViewShiftTarget);
            orbit.AdvanceViewShift(CamouflageScreen.SlideSeconds / 2f);
            Assert.AreEqual(aside / 2f, orbit.ViewShift, 1e-4f);
            _screen.Hide();
            orbit.AdvanceViewShift(CamouflageScreen.SlideSeconds / 2f);
            Assert.AreEqual(0f, orbit.ViewShift, 1e-4f, "hiding again keeps the slide's pace");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Preview_SlidesThePanelsAwayAndEscapeBringsThemBack(bool modern)
        {
            Build(modern);
            Assert.IsFalse(_screen.HandleEscape(), "nothing open: escape leaves the screen");
            _screen.SetPreview(true);
            Assert.IsTrue(_screen.IsPreview);
            _screen.Tick(CamouflageScreen.SlideSeconds / 2f);
            Assert.AreEqual(0.5f, _screen.PreviewProgress, 0.01f);
            _screen.Tick(1f);
            Assert.AreEqual(1f, _screen.PreviewProgress);

            Assert.IsTrue(_screen.HandleEscape());
            Assert.IsFalse(_screen.IsPreview);
            _screen.Tick(1f);
            Assert.AreEqual(0f, _screen.PreviewProgress);

            _screen.SetPreview(true);
            _screen.Hide();
            Assert.IsFalse(_screen.IsPreview, "leaving the screen ends the preview");
            Assert.IsFalse(_screen.Root.gameObject.activeSelf);
        }
    }
}
