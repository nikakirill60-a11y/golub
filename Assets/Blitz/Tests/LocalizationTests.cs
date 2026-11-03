using BlitzLegacy.Core.Localization;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class LocalizationTests
    {
        private const string Sample =
            "{\"languages\":[\"en\",\"ru\"],\"keys\":{" +
            "\"LOGIN\":{\"en\":\"LOGIN\",\"ru\":\"ВОЙТИ\"}," +
            "\"LOGIN_CURRENT_AKK\":{\"en\":\"Current account: \",\"ru\":\"Текущий аккаунт: \"}," +
            "\"LOGIN_CANT\":{\"en\":\"Can't login?\"}}}";

        [Test]
        public void Get_ReturnsRequestedLanguage()
        {
            var table = LocalizationTable.Parse(Sample);
            table.Language = "ru";
            Assert.AreEqual("ВОЙТИ", table.Get("LOGIN"));
        }

        [Test]
        public void Get_FallsBackToEnglishWhenTranslationMissing()
        {
            var table = LocalizationTable.Parse(Sample);
            table.Language = "ru";
            Assert.AreEqual("Can't login?", table.Get("LOGIN_CANT"));
        }

        [Test]
        public void Get_EchoesUnknownKey_LikeGodotTr()
        {
            var table = LocalizationTable.Parse(Sample);
            Assert.AreEqual("NOT_A_REAL_KEY", table.Get("NOT_A_REAL_KEY"));
        }

        [Test]
        public void Get_KeepsTrailingSpaceFromCsv()
        {
            // Godot concatenates this one with the account name, so the trailing
            // space is part of the translation and must survive the port.
            var table = LocalizationTable.Parse(Sample);
            Assert.AreEqual("Current account: ", table.Get("LOGIN_CURRENT_AKK"));
        }

        [Test]
        public void RudnikiName_IsLocalizedInBothLanguages()
        {
            var asset = Resources.Load<TextAsset>("Data/lang");
            Assert.IsNotNull(asset);
            var table = LocalizationTable.Parse(asset.text);

            table.Language = "en";
            Assert.AreEqual("Mines", table.Get("map:rudniki/name"));
            table.Language = "ru";
            Assert.AreEqual("Рудники", table.Get("map:rudniki/name"));
        }

        /// <summary>
        /// Ordeal's name comes from the client's own Strings tables, so both
        /// languages must read back exactly what the 4.8.0 pack ships.
        /// </summary>
        [Test]
        public void OrdealName_IsLocalizedInBothLanguages()
        {
            var asset = Resources.Load<TextAsset>("Data/lang");
            Assert.IsNotNull(asset);
            var table = LocalizationTable.Parse(asset.text);

            table.Language = "en";
            Assert.AreEqual("Trial by Fire", table.Get("map:ordeal/name"));
            table.Language = "ru";
            Assert.AreEqual("Испытание огнём", table.Get("map:ordeal/name"));
        }

        [Test]
        public void Parse_ReturnsEmptyTableOnGarbage()
        {
            var table = LocalizationTable.Parse("not json at all");
            Assert.AreEqual(0, table.Count);
            Assert.AreEqual("LOGIN", table.Get("LOGIN"));
        }
    }
}
