using System.Linq;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class RempackManifestTests
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Test]
        public void Parse_ReadsEveryField()
        {
            var report = new RempackReport();
            var manifest = RempackManifest.Parse(@"{
                ""format"": ""rempack"", ""formatVersion"": 1, ""id"": ""miku.void_pack"", ""version"": ""1.2.3-beta.1"",
                ""name"": {""en"": ""Void pack"", ""ru"": ""Пустота""}, ""description"": ""A test."",
                ""authors"": [""Miku"", ""Rin""], ""license"": ""CC-BY-4.0"", ""homepage"": ""https://example.org/mod"",
                ""icon"": ""art/icon.png"", ""previews"": [""art/shot1.jpg""],
                ""game"": {""minVersion"": ""2.0.0"", ""features"": [""tanks@1"", ""overrides@1""]},
                ""dependencies"": [""miku.base"", {""id"": ""miku.sounds"", ""version"": ""^1.2""}],
                ""conflicts"": [{""id"": ""other.pack"", ""version"": ""<2""}],
                ""content"": {""tanks"": [""t34_void""]},
                ""overrides"": [""stock:tank/t-34-85""],
                ""files"": {""tanks/t34_void/tank.json"": ""sha256:" + Hash.ToUpperInvariant() + @"""}
            }", report);

            Assert.That(manifest, Is.Not.Null);
            Assert.That(report.HasErrors, Is.False, report.ToString());
            Assert.That(manifest.Id, Is.EqualTo("miku.void_pack"));
            Assert.That(manifest.Version.ToString(), Is.EqualTo("1.2.3-beta.1"));
            Assert.That(manifest.DisplayName("ru"), Is.EqualTo("Пустота"));
            Assert.That(manifest.DisplayName("de"), Is.EqualTo("Void pack"));
            Assert.That(manifest.Description.Get("ru"), Is.EqualTo("A test."));
            CollectionAssert.AreEqual(new[] { "Miku", "Rin" }, manifest.Authors);
            Assert.That(manifest.Homepage, Is.EqualTo("https://example.org/mod"));
            Assert.That(manifest.Icon, Is.EqualTo("art/icon.png"));
            Assert.That(manifest.GameMinVersion.ToString(), Is.EqualTo("2.0.0"));
            CollectionAssert.AreEqual(new[] { "tanks@1", "overrides@1" }, manifest.Features);
            Assert.That(manifest.Dependencies.Select(d => d.Id + " " + d.Range.Text), Is.EqualTo(new[] { "miku.base *", "miku.sounds ^1.2" }));
            Assert.That(manifest.Conflicts[0].Range.Matches(SemanticVersion.Parse("1.9.0")), Is.True);
            CollectionAssert.AreEqual(new[] { "t34_void" }, manifest.Content(RempackContentKind.Tank));
            Assert.That(manifest.ReplacesStock, Is.True);
            Assert.That(manifest.Overrides[0].ToString(), Is.EqualTo("stock:tank/t-34-85"));
            Assert.That(manifest.Files["tanks/t34_void/tank.json"], Is.EqualTo(Hash), "hashes are kept lowercase without the prefix");
            Assert.That(report.Has(RempackCodes.OverrideOnline), Is.True, "a stock replacement warns about online play");
        }

        [Test]
        public void ToJson_RoundTripsThroughParse()
        {
            var fixture = RempackFixture.Tank().Depends("author.base", ">=1.0.0 <2.0.0").Overrides("stock:map/rudniki");
            fixture.Set("description", "{\"en\": \"About\"}");
            fixture.Manifest.Set("files", JsonNode.Parse("{\"tanks/t1/tank.json\": \"" + Hash + "\"}"));
            var first = RempackManifest.Parse(fixture.Manifest.ToJson(), new RempackReport());
            var report = new RempackReport();
            var second = RempackManifest.Parse(first.ToJson().ToJson(), report);
            Assert.That(report.HasErrors, Is.False, report.ToString());
            Assert.That(second.ToJson().ToJson(), Is.EqualTo(first.ToJson().ToJson()));
        }

        [TestCase("[]", RempackCodes.FieldNotObject)]
        [TestCase("{\"format\": \"zip\"}", RempackCodes.ManifestFormat)]
        [TestCase("", RempackCodes.JsonEmpty)]
        [TestCase("{\"format\": ", RempackCodes.JsonInvalid)]
        public void Parse_RefusesWhatIsNotAManifest(string text, string code)
        {
            var report = new RempackReport();
            Assert.That(RempackManifest.Parse(text, report), Is.Null);
            Assert.That(report.Has(code), Is.True, report.ToString());
        }

        [TestCase("id", "\"Author.Mod\"", RempackCodes.IdInvalid)]
        [TestCase("id", "\"nodot\"", RempackCodes.IdInvalid)]
        [TestCase("id", "\"a.b.c\"", RempackCodes.IdInvalid)]
        [TestCase("id", "\"a.b__c\"", RempackCodes.IdInvalid)]
        [TestCase("version", "\"1.0.0.0\"", RempackCodes.VersionInvalid)]
        [TestCase("version", "\"01.0.0\"", RempackCodes.VersionInvalid)]
        [TestCase("version", "3", RempackCodes.FieldNotString)]
        [TestCase("name", "{\"ru\": \"Только русский\"}", RempackCodes.NameMissingEnglish)]
        [TestCase("name", "{\"english\": \"x\"}", RempackCodes.FieldLanguage)]
        [TestCase("name", "[\"x\"]", RempackCodes.FieldNotText)]
        [TestCase("name", "\"bad\u0007name\"", RempackCodes.TextControl)]
        [TestCase("name", "\"rtl\u202ename\"", RempackCodes.TextControl)]
        [TestCase("authors", "[]", RempackCodes.FieldMissing)]
        [TestCase("authors", "\"Miku\"", RempackCodes.FieldNotArray)]
        [TestCase("homepage", "\"http://example.org\"", RempackCodes.FieldHttps)]
        [TestCase("icon", "\"../icon.png\"", RempackCodes.PathInvalid)]
        [TestCase("game", "{\"features\": [\"Tanks\"]}", RempackCodes.FeatureInvalid)]
        [TestCase("game", "{\"minVersion\": \"soon\"}", RempackCodes.VersionInvalid)]
        [TestCase("dependencies", "[\"author.test_mod\"]", RempackCodes.SelfReference)]
        [TestCase("dependencies", "[{\"id\": \"a.b\", \"version\": \"=>1\"}]", RempackCodes.RangeInvalid)]
        [TestCase("dependencies", "[\"A.B\"]", RempackCodes.IdInvalid)]
        [TestCase("content", "{\"tanks\": [\"T1\"]}", RempackCodes.ContentIdInvalid)]
        [TestCase("content", "{\"tanks\": [\"t1\", \"t1\"]}", RempackCodes.ContentDuplicate)]
        [TestCase("content", "{\"maps\": [\"m1\"]}", RempackCodes.ContentFeature)]
        [TestCase("content", "{}", RempackCodes.ContentEmpty)]
        [TestCase("content", "{\"skins\": [\"s1\"], \"tanks\": [\"t1\"]}", RempackCodes.FieldUnknown)]
        [TestCase("overrides", "[\"tank/t-34\"]", RempackCodes.OverrideInvalid)]
        [TestCase("overrides", "[\"stock:tank/t-34\"]", RempackCodes.ContentFeature)]
        [TestCase("files", "{\"a.txt\": \"abc\"}", RempackCodes.FieldSha256)]
        [TestCase("files", "{\"../a.txt\": \"" + Hash + "\"}", RempackCodes.PathInvalid)]
        [TestCase("files", "{\"a.txt\": \"" + Hash + "\", \"A.txt\": \"" + Hash + "\"}", RempackCodes.PathDuplicate)]
        [TestCase("files", "{\"rempack.json\": \"" + Hash + "\"}", RempackCodes.PathInvalid)]
        [TestCase("surprise", "1", RempackCodes.FieldUnknown)]
        public void Parse_ReportsTheBrokenField(string key, string json, string code)
        {
            var fixture = RempackFixture.Tank();
            fixture.Manifest.Set("files", JsonNode.NewObject());
            fixture.Set(key, json);
            var report = new RempackReport();
            Assert.That(RempackManifest.Parse(fixture.Manifest.ToJson(), report), Is.Not.Null);
            Assert.That(report.Has(code), Is.True, key + ": " + report);
        }

        [Test]
        public void Parse_UnknownFieldsOnlyWarn()
        {
            var fixture = RempackFixture.Tank().Set("surprise", "1");
            fixture.Manifest.Set("files", JsonNode.NewObject());
            var report = new RempackReport();
            RempackManifest.Parse(fixture.Manifest.ToJson(), report);
            Assert.That(report.HasErrors, Is.False, report.ToString());
            Assert.That(report.Count(RempackSeverity.Warning), Is.EqualTo(1));
        }

        [Test]
        public void Parse_ANewerFormatStillNamesThePackage()
        {
            var report = new RempackReport();
            var manifest = RempackManifest.Parse(
                "{\"format\": \"rempack\", \"formatVersion\": 9, \"id\": \"future.mod\", \"version\": \"5.0.0\", "
                + "\"name\": \"From the future\", \"hologram\": true}", report);
            Assert.That(manifest, Is.Not.Null);
            Assert.That(manifest.Id, Is.EqualTo("future.mod"));
            Assert.That(manifest.DisplayName("en"), Is.EqualTo("From the future"));
            Assert.That(report.Has(RempackCodes.ManifestFormatVersion), Is.True);
            Assert.That(report.Has(RempackCodes.FieldUnknown), Is.False, "the rest of a newer layout is not judged");
            Assert.That(RempackCodes.NeedsNewerGame(report.FirstError.Code), Is.True);
        }

        [Test]
        public void Parse_RefusesHugeManifests()
        {
            var report = new RempackReport();
            Assert.That(RempackManifest.Parse(new string(' ', (int)RempackLimits.ManifestBytes + 1), report), Is.Null);
            Assert.That(report.Has(RempackCodes.FileTooLarge), Is.True);
        }

        [Test]
        public void Parse_RefusesDeepNesting()
        {
            var report = new RempackReport();
            var deep = new string('[', 100) + new string(']', 100);
            Assert.That(RempackManifest.Parse("{\"format\": \"rempack\", \"x\": " + deep + "}", report), Is.Null);
            Assert.That(report.Has(RempackCodes.JsonTooDeep), Is.True, report.ToString());
        }

        [Test]
        public void Issues_AreCappedSoAHostileFileStaysCheap()
        {
            var files = new System.Text.StringBuilder("{");
            for (var i = 0; i < 2000; i++)
            {
                files.Append(i == 0 ? string.Empty : ",").Append("\"f").Append(i).Append(".txt\": \"bad\"");
            }

            var fixture = RempackFixture.Tank();
            fixture.Manifest.Set("files", JsonNode.Parse(files.Append("}").ToString()));
            var report = new RempackReport();
            RempackManifest.Parse(fixture.Manifest.ToJson(), report);
            Assert.That(report.Issues.Count, Is.EqualTo(RempackReport.MaxIssues));
            Assert.That(report.Dropped, Is.EqualTo(2000 - RempackReport.MaxIssues));
        }

        [Test]
        public void Issues_KeepTheErrorAfterAFloodOfWarnings()
        {
            var fixture = RempackFixture.Tank();
            for (var i = 0; i < 600; i++)
            {
                fixture.Set("extra_" + i, "1");
            }

            fixture.Manifest.Set("id", "Not An Id");
            var report = new RempackReport();
            RempackManifest.Parse(fixture.Manifest.ToJson(), report);

            Assert.That(report.HasErrors, Is.True);
            Assert.That(report.FirstError.Code, Is.EqualTo(RempackCodes.IdInvalid));
            Assert.That(report.Count(RempackSeverity.Warning), Is.EqualTo(RempackReport.MaxIssues));
            Assert.That(report.Dropped, Is.EqualTo(100));
        }
    }
}
