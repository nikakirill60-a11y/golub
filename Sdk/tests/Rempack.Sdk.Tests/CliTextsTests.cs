using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;
using NUnit.Framework;

namespace BlitzLegacy.Sdk.Tests
{
    public sealed class CliTextsTests
    {
        [Test]
        public void EveryLineOfTheToolIsTranslated()
        {
            var keys = CliTexts.Table["keys"];
            foreach (var key in CliKeys.All)
            {
                var row = keys[key];
                Assert.That(row.Kind, Is.EqualTo(JsonKind.Object), key + " is missing from lang.json");
                var english = row["en"].AsString();
                var russian = row["ru"].AsString();
                Assert.That(english, Is.Not.Empty, key + " has no English text");
                Assert.That(russian, Is.Not.Empty, key + " has no Russian text");
                Assert.That(Placeholders(russian), Is.EqualTo(Placeholders(english)), key + " fills other values in Russian");
            }
        }

        [Test]
        public void EveryIssueTheToolCanPrintIsTranslated()
        {
            var keys = CliTexts.Table["keys"];
            foreach (var code in RempackCodes.All)
            {
                var row = keys[RempackMessages.Key(code)];
                Assert.That(row["en"].AsString(), Is.Not.Empty, code);
                Assert.That(row["ru"].AsString(), Is.Not.Empty, code);
            }
        }

        [Test]
        public void TheEmbeddedTableIsTheGames()
        {
            string root = null;
            foreach (var attribute in typeof(CliTextsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (attribute.Key == "GameRoot")
                {
                    root = attribute.Value;
                }
            }

            var path = Path.Combine(root, "Assets", "Blitz", "Resources", "Data", "lang.json");
            var game = JsonNode.Parse(File.ReadAllText(path));

            Assert.That(CliTexts.Table["keys"].Count, Is.EqualTo(game["keys"].Count));
        }

        [Test]
        public void SizesReadTheWayPeopleWriteThem()
        {
            var english = new CliTexts("en");
            var russian = new CliTexts("ru");

            Assert.That(english.Size(812), Is.EqualTo("812 B"));
            Assert.That(english.Size(1536), Is.EqualTo("1.5 KB"));
            Assert.That(english.Size(4404019), Is.EqualTo("4.2 MB"));
            Assert.That(english.Size(300L << 20), Is.EqualTo("300 MB"));
            Assert.That(english.Size(3L << 30), Is.EqualTo("3.0 GB"));
            Assert.That(russian.Size(812), Is.EqualTo("812 \u0411"));
            Assert.That(russian.Size(1536), Is.EqualTo("1.5 \u041a\u0411"));
        }

        [Test]
        public void AnUnknownLanguageSpeaksEnglish()
        {
            var texts = new CliTexts("de");

            Assert.That(texts.Language, Is.EqualTo("en"));
            Assert.That(texts.Get(CliKeys.NotFound, "x"), Is.EqualTo("No such file or folder: x"));
            Assert.That(texts.Get("sdk_no_such_row"), Is.EqualTo("sdk_no_such_row"));
        }

        private static List<string> Placeholders(string text)
        {
            var found = new SortedSet<string>();
            foreach (Match match in Regex.Matches(text, @"\{\d\}"))
            {
                found.Add(match.Value);
            }

            return new List<string>(found);
        }
    }
}
