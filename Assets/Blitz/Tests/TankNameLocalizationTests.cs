using System.Text.RegularExpressions;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using UnityEngine.UI;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class TankNameLocalizationTests
    {
        private static LocalizationTable Load()
        { return LocalizationTable.Parse(Resources.Load<TextAsset>("Data/lang").text); }

        [Test]
        public void CompleteRosterTitlesHaveExplicitEnglishAndRussianRows()
        {
            var table=Load();
            var cards=JsonNode.Parse(Resources.Load<TextAsset>("Data/tanks").text)["tanks"];
            var authored=JsonNode.Parse(Resources.Load<TextAsset>("Data/tank_name_localization").text)["tanks"];
            Assert.AreEqual(522,cards.Count);
            for(var i=0;i<cards.Count;i++)
            {
                var tank=TankInfo.FromJson(cards[i]);
                foreach(var shortName in new[]{false,true})
                {
                    var field=shortName?"short":"name";
                    var key="tank:"+tank.Name+"/"+field;
                    Assert.IsTrue(table.Has(key),key);
                    foreach(var language in new[]{"en","ru"})
                    {
                        table.Language=language;
                        var value=table.GetTankName(tank,shortName);
                        Assert.AreEqual(authored[tank.Name][field]["translations"][language].AsString(),value,key);
                        Assert.IsNotEmpty(value,key);
                        if(language=="en") Assert.IsFalse(Regex.IsMatch(value,"[А-Яа-яЁё]"),key+": "+value);
                    }
                }
            }
        }

        [Test]
        public void KpzAndLegacyT49UseExactClientEnglishTitles()
        {
            var table=Load();
            Assert.AreEqual("Kampfpanzer 70 Missile",table.GetTankName("g14_kpfpz_70r","",false));
            Assert.AreEqual("Kpz 70 Missile",table.GetTankName("g14_kpfpz_70r",""));
            Assert.AreEqual("T49 ATM",table.GetTankName("t49_rocket","",false));
            Assert.AreEqual("T49 A",table.GetTankName("t49_rocket",""));
            Assert.AreEqual("Kpz 70 Missile",table.Get("Kpz 70 Ракетный"));
        }

        [Test]
        public void ExistingCardAndSavedResultFollowLanguageChangesWithoutMutation()
        {
            var table=Load();
            var tank=new TankInfo{Name="ms-1",DisplayName="МС-1",DisplayNameShort="МС-1"};
            Assert.AreEqual("MS-1",table.GetTankName(tank));
            table.Language="ru";
            Assert.AreEqual("МС-1",table.GetTankName(tank));
            Assert.AreEqual("Kpz 70 Ракетный",table.GetTankName("g14_kpfpz_70r","Kpz 70 Ракетный"));
            table.Language="en";
            Assert.AreEqual("Kpz 70 Missile",table.GetTankName("g14_kpfpz_70r","Kpz 70 Ракетный"));
            Assert.AreEqual("МС-1",tank.DisplayName);
        }

        [TestCase("en", "ms-1", "MS-1")]
        [TestCase("ru", "ms-1", "МС-1")]
        [TestCase("en", "g14_kpfpz_70r", "Kpz 70 Missile")]
        [TestCase("ru", "g14_kpfpz_70r", "Kpz 70 Ракетный")]
        public void ResearchCardActuallyDrawsTheSelectedLanguage(string language, string id, string expected)
        {
            var lang=Load();lang.Language=language;
            var kit=new HangarUiKit(AtlasRegionTable.Parse(BlitzResources.LoadText(BlitzResources.AtlasRegionsData)),
                lang,BlitzResources.LoadFont(BlitzResources.RegularFontName),
                BlitzResources.LoadFont(BlitzResources.BoldFontName),null);
            var root=new GameObject("tank-title-test",typeof(RectTransform));
            try
            {
                var tank=TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData)).Get(id);
                var card=TechTreeCard.Build(kit,(RectTransform)root.transform,0,0,400,170,1,
                    tank,ResearchStatus.NotAvailable,new TankSaveData{Name=id},null);
                Assert.AreEqual(expected,card.Root.Find("name").GetComponent<Text>().text);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ModTitlesAndUnknownIdsKeepAuthoredFallback()
        {
            var table=Load();
            Assert.AreEqual("My tank",table.GetTankName(new TankInfo{Name="mod-tank",DisplayName="My tank"}));
            Assert.AreEqual("Historic custom",table.GetTankName("removed-mod","Historic custom"));
            Assert.AreEqual(string.Empty,table.GetTankName((TankInfo)null));
        }
    }
}
