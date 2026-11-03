using System;
using System.Linq;
using BlitzLegacy.Core.Sandbox;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// training_mode_screen.gd in both menu styles: every tab builds with
    /// localized text only, and the map, tank picker, preset and exchange
    /// flows edit and persist the active preset.
    /// </summary>
    public sealed class SandboxScreenTests
    {
        private static TanksDatabase _tanks;

        private GameObject _root;
        private MemorySaveStorage _storage;
        private SandboxPresetService _presets;
        private SandboxScreen _screen;
        private int _applied;
        private int _closed;
        private string _started;
        private int _starts;

        private static TanksDatabase Tanks
        {
            get
            {
                if (_tanks == null)
                {
                    _tanks = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                }
                return _tanks;
            }
        }

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("SandboxScreenTests");
            _storage = new MemorySaveStorage();
            _presets = SandboxPresetService.Create(_storage, "Default", BattleMapCatalog.SandboxMaps(), Tanks);
            _applied = 0;
            _closed = 0;
            _started = "unset";
            _starts = 0;
        }

        [TearDown]
        public void TearDown()
        {
            MenuStyle.ToolOverride = null;
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
            _screen = null;
        }

        private SandboxScreen Open(bool modern = false)
        {
            MenuStyle.ToolOverride = modern ? SettingsData.UiStyleModern : SettingsData.UiStyleClassic;
            _screen = SandboxScreen.Open(_root.transform, _presets, 0, false,
                () => _applied++,
                map => { _started = map; _starts++; },
                () => _closed++);
            return _screen;
        }

        private void AssertLocalized(string context)
        {
            foreach (var text in _screen.Root.GetComponentsInChildren<Text>(true))
            {
                var value = text.text ?? string.Empty;
                Assert.IsFalse(value.StartsWith("training:", StringComparison.Ordinal),
                    context + ": untranslated key '" + value + "' on " + text.name);
                Assert.IsFalse(value.Contains("%s") || value.Contains("%d"),
                    context + ": unformatted pattern '" + value + "' on " + text.name);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Screen_BuildsEveryTabInBothStyles(bool modern)
        {
            var screen = Open(modern);
            Assert.IsNotNull(screen.Root);
            Assert.IsNotNull(screen.StartButton);
            Assert.IsNotNull(screen.CloseButton);
            Assert.IsNotNull(screen.ManageButton);
            Assert.AreEqual(SandboxScreen.PageKeys.Length, screen.PrimaryButtons.Count);
            Assert.IsFalse(string.IsNullOrEmpty(screen.SummaryText));
            Assert.AreEqual(modern, screen.StartButton.GetComponent<PressFeedback>() != null,
                "only Modern buttons carry press feedback");
            AssertLocalized("battle");

            foreach (SandboxScreen.Page page in Enum.GetValues(typeof(SandboxScreen.Page)))
            {
                screen.SelectPage(page);
                Assert.AreEqual(page, screen.CurrentPage);
                AssertLocalized(page.ToString());
            }
        }

        [Test]
        public void Scenario_BigBossShowsItsSettingsAndPersistsThem()
        {
            var screen = Open();
            Assert.IsNotNull(FindNode(screen.Root.transform, "mode_bigboss"), "the BIG BOSS rule set button");
            Assert.AreEqual(SandboxMultipliers.Count, SandboxScreen.MultiplierKeys.Length, "a row per modifier, hull traverse included");
            screen.SelectBaseMode(SandboxBaseMode.BigBoss);
            var settings = FindNode(screen.Root.transform, "training_mode_settings");
            Assert.IsTrue(settings.gameObject.activeSelf);
            Assert.AreEqual(3, settings.GetComponentsInChildren<UiValueSlider>(true).Length,
                "boss lives, team points and the boost multiplier");
            AssertLocalized("BIG BOSS settings");

            screen.Config.BigBossLives = 4;
            screen.Config.BigBossTeamPoints = 50;
            screen.Config.BigBossBuffMul = 1.5f;
            screen.Apply();
            var reloaded = SandboxPresetService.Create(_storage, "Default", BattleMapCatalog.SandboxMaps(), Tanks);
            Assert.AreEqual(SandboxBaseMode.BigBoss, reloaded.Active.BaseMode);
            Assert.AreEqual(4, reloaded.Active.BigBossLives);
            Assert.AreEqual(50, reloaded.Active.BigBossTeamPoints);
            Assert.AreEqual(1.5f, reloaded.Active.BigBossBuffMul, 1e-5f);
        }

        [Test]
        public void Scenario_MapAndModeChoicesPersistOnApply()
        {
            var screen = Open();
            var map = _presets.MapIds[1];
            screen.SelectMap(map);
            screen.SelectBaseMode(SandboxBaseMode.Uprising);
            Assert.AreEqual(map, screen.Config.SelectedMapId);
            Assert.AreEqual(SandboxBaseMode.Uprising, screen.Config.BaseMode);

            screen.Apply();
            Assert.AreEqual(1, _applied);
            Assert.AreEqual(1, _closed);
            Assert.AreEqual(0, _starts);

            var reloaded = SandboxPresetService.Create(_storage, "Default", BattleMapCatalog.SandboxMaps(), Tanks);
            Assert.AreEqual(map, reloaded.Active.SelectedMapId);
            Assert.AreEqual(SandboxBaseMode.Uprising, reloaded.Active.BaseMode);
        }

        [Test]
        public void Start_PassesTheMapOrNullForRandom()
        {
            var screen = Open();
            screen.SelectMap(string.Empty);
            screen.StartBattle();
            Assert.AreEqual(1, _starts);
            Assert.IsNull(_started, "RANDOM hands no map to the launcher");
            Assert.AreEqual(1, _closed);

            screen = Open();
            screen.SelectMap(_presets.MapIds[0]);
            screen.StartBattle();
            screen.StartBattle();
            Assert.AreEqual(2, _starts, "a second press while launching is ignored");
            Assert.AreEqual(_presets.MapIds[0], _started);
        }

        [Test]
        public void TankPicker_FillsFreeSlotsAndFilters()
        {
            var screen = Open();
            screen.Config.EnemyTeamSize = 3;
            screen.SelectPage(SandboxScreen.Page.Enemies);
            screen.OpenTankPicker(true, -1);
            Assert.IsNotNull(screen.TankPicker);
            Assert.AreEqual(0, screen.PickerNextSlot);
            var catalogue = SandboxEditor.PickerTanks(Tanks, null);
            Assert.AreEqual(catalogue.Count, screen.PickerVisibleCount);

            var first = catalogue[0].Name;
            screen.SetTankSearch(first);
            Assert.That(screen.PickerVisibleCount, Is.InRange(1, catalogue.Count - 1));
            screen.SetTankSearch("zz-no-such-tank-zz");
            Assert.AreEqual(0, screen.PickerVisibleCount);
            screen.SetTankSearch(string.Empty);

            screen.ChooseTank(first);
            screen.ChooseTank(catalogue[1].Name);
            Assert.AreEqual(first, screen.Config.EnemySlots[0].TankName);
            Assert.AreEqual(catalogue[1].Name, screen.Config.EnemySlots[1].TankName);
            Assert.AreEqual(SandboxRosterMode.Custom, screen.Config.EnemyRosterMode);
            Assert.AreEqual(2, screen.PickerNextSlot);
            AssertLocalized("picker");

            screen.RemovePickerTank(0);
            Assert.IsTrue(screen.Config.EnemySlots[0].RandomTank);
            Assert.AreEqual(0, screen.PickerNextSlot);
            screen.CloseTankPicker();
            Assert.IsNull(screen.TankPicker);

            screen.SelectSlot(true, 1);
            Assert.AreEqual(1, screen.SelectedSlot(true));
            AssertLocalized("slot editor");
            screen.ClearTeamTanks(true);
            Assert.IsTrue(screen.Config.EnemySlots.All(s => s.RandomTank));
        }

        [Test]
        public void Presets_DeleteNeedsConfirmation()
        {
            var screen = Open();
            screen.OpenPresetManager();
            Assert.IsNotNull(screen.PresetManager);
            screen.CreatePreset();
            Assert.AreEqual(2, _presets.Library.Count);
            Assert.AreSame(_presets.Active, screen.Config, "the editor follows the new preset");

            screen.DeletePreset();
            Assert.AreEqual(2, _presets.Library.Count, "the first press only arms the button");
            screen.DeletePreset();
            Assert.AreEqual(1, _presets.Library.Count);
            screen.DeletePreset();
            screen.DeletePreset();
            Assert.AreEqual(1, _presets.Library.Count, "the last preset cannot be deleted");

            var preview = screen.PresetPreview(screen.Config);
            Assert.AreEqual(8, preview.Split('\n').Length);
            AssertLocalized("presets");
            screen.ClosePresetManager();
            Assert.IsNull(screen.PresetManager);
        }

        [Test]
        public void Exchange_ExportThenImportAddsAPreset()
        {
            var screen = Open();
            screen.SelectMap(_presets.MapIds[2]);
            screen.OpenPresetManager();
            screen.OpenExchange(false);
            var code = screen.ExchangeText;
            Assert.IsTrue(SandboxPresetLibrary.LooksLikeCode(code));
            screen.CloseExchange();
            Assert.IsNull(screen.ExchangeDialog);

            screen.OpenExchange(true);
            screen.SetExchangeText("definitely not a preset");
            screen.ImportExchange();
            Assert.IsNotNull(screen.ExchangeDialog, "a bad code keeps the dialog open");
            Assert.IsFalse(string.IsNullOrEmpty(screen.ExchangeStatus));
            AssertLocalized("import error");

            screen.SetExchangeText(code);
            screen.ImportExchange();
            Assert.IsNull(screen.ExchangeDialog);
            Assert.AreEqual(2, _presets.Library.Count);
            Assert.AreEqual(_presets.MapIds[2], screen.Config.SelectedMapId);
        }

        /// <summary>
        /// Every control that takes drags on a vertical page (team and module
        /// strips, slider rows) gives a vertical swipe to the page.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void EveryPage_TakesAVerticalSwipeFromEveryNestedControl(bool modern)
        {
            var screen = Open(modern);
            screen.Config.AlliedTeamSize = 3;
            screen.SelectPage(SandboxScreen.Page.Allies);
            screen.OpenTankPicker(false, 0);
            screen.ChooseTank(SandboxEditor.PickerTanks(Tanks, null)[0].Name);
            screen.CloseTankPicker();
            screen.SelectSlot(false, 0);
            foreach (var name in new[] { "allied_cards", "modules_gun", "modules_engine", "modules_tracks" })
            {
                var node = FindNode(screen.Root, name);
                Assert.IsNotNull(node, name);
                Assert.IsNotNull(node.GetComponent<NestedScrollRect>(), name + " hands vertical swipes to the page");
            }

            var strips = 0;
            var sliders = 0;
            foreach (var page in screen.Root.GetComponentsInChildren<ScrollRect>(true))
            {
                if (!page.vertical || page.horizontal || page.content == null)
                {
                    continue;
                }

                Show(page.transform);
                foreach (var control in page.content.GetComponentsInChildren<MonoBehaviour>())
                {
                    if (!(control is IDragHandler))
                    {
                        continue;
                    }

                    var label = control.name + " (" + control.GetType().Name + ") on " + page.name;
                    var begin = control as IBeginDragHandler;
                    Assert.IsNotNull(begin, label + " takes drags without choosing a direction");
                    var swipe = new PointerEventData(null)
                    {
                        button = PointerEventData.InputButton.Left,
                        pressPosition = Vector2.zero,
                        position = new Vector2(4f, 60f),
                        pointerDrag = control.gameObject,
                    };
                    begin.OnBeginDrag(swipe);
                    Assert.AreSame(page.gameObject, swipe.pointerDrag, label + " keeps a vertical swipe");
                    page.OnEndDrag(swipe);
                    strips += control is NestedScrollRect ? 1 : 0;
                    sliders += control is UiValueSlider ? 1 : 0;
                }
            }

            Assert.GreaterOrEqual(strips, 5, "both team strips and the gun, engine and tracks strips");
            Assert.Greater(sliders, SandboxMultipliers.Count, "the multiplier and cheat sliders");
        }

        private static Transform FindNode(Transform root, string name)
        {
            return root.GetComponentsInChildren<Transform>(true).FirstOrDefault(node => node.name == name);
        }

        private static void Show(Transform node)
        {
            for (; node != null; node = node.parent)
            {
                node.gameObject.SetActive(true);
            }
        }

        [Test]
        public void HoldRepeat_PlainClickIsNotSwallowed()
        {
            var go = new GameObject("repeat", typeof(RectTransform));
            go.transform.SetParent(_root.transform, false);
            var repeat = go.AddComponent<SandboxHoldRepeat>();
            var calls = 0;
            repeat.Configure(() => calls++);
            Assert.IsFalse(repeat.ConsumeRepeat());
            Assert.AreEqual(0, calls);
            Assert.Greater(SandboxHoldRepeat.InitialDelay, SandboxHoldRepeat.Interval);
        }
    }
}
