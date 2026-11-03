using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class HangarSelectionTests
    {
        private static readonly DateTime DuringEvent = new DateTime(2026, 10, 31, 12, 0, 0);
        private static readonly DateTime AfterEvent = new DateTime(2026, 11, 20, 12, 0, 0);

        [Test]
        public void CatalogListsEveryHangarOnceUnderAStableId()
        {
            var ids = new HashSet<string>();
            var kinds = new HashSet<HangarKind>();
            foreach (var info in HangarCatalog.All)
            {
                Assert.IsTrue(ids.Add(info.Id), info.Id);
                Assert.IsTrue(kinds.Add(info.Kind), info.Kind.ToString());
                Assert.AreSame(info, HangarCatalog.Get(info.Kind));
                Assert.AreSame(info, HangarCatalog.Find(info.Id));
                StringAssert.StartsWith("ui:hangar/", info.NameKey);
            }

            foreach (HangarKind kind in Enum.GetValues(typeof(HangarKind)))
            {
                Assert.IsTrue(kinds.Contains(kind), kind + " has no catalog entry");
            }

            Assert.AreEqual("hangar", HangarCatalog.Get(HangarKind.Regular).Id);
            Assert.AreEqual("hangar_prem_2018", HangarCatalog.Get(HangarKind.Premium).Id);
            Assert.AreEqual("hangar_halloween_2026", HangarCatalog.Get(HangarKind.Halloween2026).Id);
            Assert.IsNull(HangarCatalog.Find(null));
            Assert.IsNull(HangarCatalog.Find(string.Empty));
            Assert.IsNull(HangarCatalog.Find("hangar_gone"));
        }

        [Test]
        public void OnlyThePremiumHangarNeedsPremium()
        {
            foreach (var info in HangarCatalog.All)
            {
                Assert.AreEqual(info.Kind == HangarKind.Premium, info.RequiresPremium, info.Id);
                Assert.IsTrue(HangarSelection.IsAvailable(info, true, true), info.Id);
                Assert.AreEqual(!info.RequiresPremium, HangarSelection.IsAvailable(info, false, true), info.Id);
                Assert.AreEqual(!info.Ported, HangarSelection.IsAvailable(info, true, false), info.Id);
            }

            Assert.IsFalse(HangarSelection.IsAvailable(null, true, true));
        }

        [TestCase(false, HangarKind.Regular)]
        [TestCase(true, HangarKind.Premium)]
        public void NoChoiceKeepsTheSchedule(bool premium, HangarKind outside)
        {
            Assert.AreEqual(HangarKind.Halloween2026, HangarSelection.Resolve(string.Empty, DuringEvent, premium));
            Assert.AreEqual(HangarKind.Halloween2026, HangarSelection.Resolve(null, DuringEvent, premium));
            Assert.AreEqual(outside, HangarSelection.Resolve(string.Empty, AfterEvent, premium));
            Assert.AreEqual(outside, HangarSelection.Resolve("hangar_gone", AfterEvent, premium));
        }

        [Test]
        public void AChoiceIsHonouredInsideAndOutsideTheEvent()
        {
            Assert.AreEqual(HangarKind.Regular, HangarSelection.Resolve("hangar", DuringEvent, true));
            Assert.AreEqual(HangarKind.Premium, HangarSelection.Resolve("hangar_prem_2018", DuringEvent, true));
            Assert.AreEqual(
                HangarKind.Halloween2026, HangarSelection.Resolve("hangar_halloween_2026", AfterEvent, false));
        }

        [Test]
        public void APremiumChoiceWithoutPremiumFallsBackToTheSchedule()
        {
            Assert.AreEqual(HangarKind.Regular, HangarSelection.Resolve("hangar_prem_2018", AfterEvent, false));
            Assert.AreEqual(
                HangarKind.Halloween2026, HangarSelection.Resolve("hangar_prem_2018", DuringEvent, false));
        }

        [Test]
        public void APortedChoiceFallsBackWhereItCannotBeDrawn()
        {
            Assert.AreEqual(
                HangarKind.Premium, HangarSelection.Resolve("hangar_halloween_2026", AfterEvent, true, false));
            Assert.AreEqual(
                HangarKind.Regular, HangarSelection.Resolve("hangar_halloween_2026", DuringEvent, false, false));
        }

        [Test]
        public void ResolveLocalJudgesTheDeviceCalendar()
        {
            var unix = HangarSchedule.UnixSeconds(AfterEvent, null);
            Assert.AreEqual(HangarKind.Regular, HangarSelection.ResolveLocal(string.Empty, unix, false));
            Assert.AreEqual(HangarKind.Halloween2026, HangarSelection.ResolveLocal(
                string.Empty, HangarSchedule.UnixSeconds(DuringEvent, null), false));
        }

        [Test]
        public void SettingsKeepAKnownChoiceAndDropAnUnknownOne()
        {
            var settings = new SettingsData();
            Assert.AreEqual(string.Empty, settings.Hangar);
            settings.SetString("hangar", "hangar_halloween_2026");
            Assert.AreEqual("hangar_halloween_2026", settings.GetString("hangar"));

            var restored = SettingsData.FromJson(JsonNode.Parse(settings.ToJson().ToJson()));
            Assert.AreEqual("hangar_halloween_2026", restored.Hangar);

            settings.SetString("hangar", "hangar_gone");
            Assert.AreEqual(string.Empty, settings.Hangar);
            var old = JsonNode.Parse("{\"hangar\": \"hangar_gone\"}");
            Assert.AreEqual(string.Empty, SettingsData.FromJson(old).Hangar);
            Assert.AreEqual(string.Empty, SettingsData.FromJson(JsonNode.Parse("{}")).Hangar);
        }

        [Test]
        public void MusicPlayerFollowsTheCatalogUntilSwitchedByHand()
        {
            var settings = new SettingsData();
            foreach (var info in HangarCatalog.All)
            {
                Assert.AreEqual(info.MusicPlayerByDefault, settings.HangarMusicPlayer(info.Kind), info.Id);
            }

            settings.SetHangarMusicPlayer(HangarKind.Regular, true);
            settings.SetHangarMusicPlayer(HangarKind.Premium, false);
            var json = settings.ToJson();
            Assert.IsTrue(json["hangar_music_player"]["hangar"].AsBool());
            Assert.IsFalse(json["hangar_music_player"].Has("hangar_prem_2018"), "a default is not stored");

            var restored = SettingsData.FromJson(JsonNode.Parse(json.ToJson()));
            Assert.IsTrue(restored.HangarMusicPlayer(HangarKind.Regular));
            Assert.IsFalse(restored.HangarMusicPlayer(HangarKind.Premium));

            var odd = JsonNode.Parse(
                "{\"hangar_music_player\": {\"hangar_gone\": true, \"hangar\": 1, \"hangar_prem_2018\": true}}");
            var parsed = SettingsData.FromJson(odd);
            Assert.IsFalse(parsed.HangarMusicPlayer(HangarKind.Regular), "only booleans are read");
            Assert.IsTrue(parsed.HangarMusicPlayer(HangarKind.Premium));
        }
    }
}
