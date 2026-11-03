using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>The hangar rows of the settings screen and the list they open.</summary>
    public sealed class HangarPickerTests
    {
        private GameObject _root;
        private SettingsData _settings;
        private SettingsScreen _ui;
        private bool _premium;
        private bool _drawable;
        private double _now;
        private bool _mobile;
        private readonly List<string> _changed = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _mobile = SettingsData.MobilePlatform;
            SettingsData.MobilePlatform = false;
            _settings = new SettingsData();
            _premium = false;
            _drawable = true;
            _now = HangarSchedule.UnixSeconds(new DateTime(2026, 11, 20, 12, 0, 0), null);
            _changed.Clear();
            _root = new GameObject("HangarPickerTests");
            Open();
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }

            SettingsData.MobilePlatform = _mobile;
        }

        private void Open()
        {
            if (_ui != null)
            {
                Object.DestroyImmediate(_ui.gameObject);
            }

            _ui = SettingsScreen.Open(
                _root.transform, _settings, 0, false, null,
                new HangarChoiceContext(() => _now, () => _premium, () => _drawable));
            _ui.SettingChanged = setting => _changed.Add(setting);
        }

        private SettingsScreen.Row RowFor(string setting)
        {
            foreach (var row in _ui.Rows)
            {
                if (row.Source.Setting == setting)
                {
                    return row;
                }
            }

            Assert.Fail("no row for " + setting);
            return null;
        }

        private HangarPickerPopup.Entry EntryFor(HangarKind kind)
        {
            foreach (var entry in _ui.HangarPicker.Entries)
            {
                if (entry.Info.Kind == kind)
                {
                    return entry;
                }
            }

            Assert.Fail("no entry for " + kind);
            return null;
        }

        [Test]
        public void HangarRow_NamesTheHangarOnScreen()
        {
            var row = RowFor("hangar");
            Assert.IsNotNull(row.Picker);
            Assert.AreEqual("STANDARD", row.Value.text);

            _premium = true;
            Open();
            Assert.AreEqual("PREMIUM", RowFor("hangar").Value.text, "no choice: the account hangar");

            _now = HangarSchedule.UnixSeconds(new DateTime(2026, 10, 31, 12, 0, 0), null);
            Open();
            Assert.AreEqual("HALLOWEEN 2026", RowFor("hangar").Value.text, "no choice: the event hangar");
        }

        [Test]
        public void Picker_ListsEveryHangarAndGreysThePremiumOneWithoutPremium()
        {
            RowFor("hangar").Picker.onClick.Invoke();
            var picker = _ui.HangarPicker;
            Assert.IsNotNull(picker);
            Assert.AreEqual(HangarCatalog.All.Count, picker.Entries.Count);

            var premium = EntryFor(HangarKind.Premium);
            Assert.IsFalse(premium.Available);
            Assert.IsFalse(premium.Button.interactable);
            Assert.IsNotNull(premium.Note);
            Assert.AreEqual("Premium account only", premium.Note.text);
            Assert.IsNotNull(premium.Badge);

            var regular = EntryFor(HangarKind.Regular);
            Assert.IsTrue(regular.Available);
            Assert.IsNotNull(regular.Check, "the hangar on screen is ticked");
            Assert.IsNull(EntryFor(HangarKind.Halloween2026).Check);

            picker.Pick(premium);
            Assert.AreEqual(string.Empty, _settings.Hangar, "a greyed entry cannot be picked");
            Assert.IsNotNull(_ui.HangarPicker);
            CollectionAssert.IsEmpty(_changed);
        }

        [Test]
        public void Picker_WithPremiumOffersEveryHangar()
        {
            _premium = true;
            Open();
            _ui.OpenHangarPicker();
            foreach (var entry in _ui.HangarPicker.Entries)
            {
                Assert.IsTrue(entry.Available, entry.Info.Id);
                Assert.IsNull(entry.Note, entry.Info.Id);
            }

            Assert.IsNotNull(EntryFor(HangarKind.Premium).Check);
        }

        [Test]
        public void Picker_GreysThePortedHangarsWhereTheyCannotBeDrawn()
        {
            _drawable = false;
            Open();
            _ui.OpenHangarPicker();
            var halloween = EntryFor(HangarKind.Halloween2026);
            Assert.IsFalse(halloween.Available);
            Assert.AreEqual("Not supported on this device", halloween.Note.text);
        }

        [Test]
        public void PickingAnEntryStoresItAndClosesTheList()
        {
            _ui.OpenHangarPicker();
            EntryFor(HangarKind.Halloween2026).Button.onClick.Invoke();
            Assert.AreEqual("hangar_halloween_2026", _settings.Hangar);
            Assert.IsNull(_ui.HangarPicker);
            Assert.AreEqual("HALLOWEEN 2026", RowFor("hangar").Value.text);
            Assert.AreEqual(HangarKind.Halloween2026, _ui.CurrentHangar);
            CollectionAssert.AreEqual(new[] { "hangar" }, _changed);
        }

        [Test]
        public void PickingTheHangarOnScreenKeepsTheScheduleInCharge()
        {
            _ui.OpenHangarPicker();
            EntryFor(HangarKind.Regular).Button.onClick.Invoke();
            Assert.AreEqual(string.Empty, _settings.Hangar);
            Assert.IsNull(_ui.HangarPicker);
            CollectionAssert.IsEmpty(_changed);

            _settings.SetString("hangar", "hangar_halloween_2026");
            _ui.OpenHangarPicker();
            EntryFor(HangarKind.Regular).Button.onClick.Invoke();
            Assert.AreEqual("hangar", _settings.Hangar, "an explicit choice is replaced");
        }

        [Test]
        public void CancelAndTheDimmedAreaCloseWithoutAChange()
        {
            _ui.OpenHangarPicker();
            _ui.HangarPicker.Cancel.onClick.Invoke();
            Assert.IsNull(_ui.HangarPicker);

            _ui.OpenHangarPicker();
            _ui.HangarPicker.Outside.onClick.Invoke();
            Assert.IsNull(_ui.HangarPicker);
            Assert.AreEqual(string.Empty, _settings.Hangar);
            CollectionAssert.IsEmpty(_changed);
        }

        [Test]
        public void ThePanelDoesNotBubbleClicksToTheDimmedArea()
        {
            _ui.OpenHangarPicker();
            var panel = _ui.HangarPicker.Panel;
            for (var t = panel.transform; t != null; t = t.parent)
            {
                Assert.AreNotSame(_ui.HangarPicker.Outside.transform, t);
            }

            Assert.IsTrue(panel.GetComponent<Image>().raycastTarget);
        }

        [Test]
        public void MusicRow_SwitchesThePlayerOfTheHangarOnScreen()
        {
            var row = RowFor("hangar_music_player");
            var off = row.Checkbox.sprite;
            Assert.IsFalse(_settings.HangarMusicPlayer(HangarKind.Regular));

            row.Checkbox.GetComponent<Button>().onClick.Invoke();
            Assert.IsTrue(_settings.HangarMusicPlayer(HangarKind.Regular));
            Assert.IsFalse(_settings.HangarMusicPlayer(HangarKind.Halloween2026));
            Assert.AreNotSame(off, row.Checkbox.sprite);
            CollectionAssert.AreEqual(new[] { "hangar_music" }, _changed);

            _ui.OpenHangarPicker();
            EntryFor(HangarKind.Halloween2026).Button.onClick.Invoke();
            Assert.AreSame(off, row.Checkbox.sprite, "the row follows the picked hangar");
        }
    }
}
