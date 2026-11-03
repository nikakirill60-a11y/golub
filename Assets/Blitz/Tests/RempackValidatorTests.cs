using System;
using System.Collections.Generic;
using System.Text;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class RempackValidatorTests
    {
        private const string OtherHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Test]
        public void Validate_AcceptsAWellFormedPackage()
        {
            var report = RempackFixture.Tank()
                .Bytes("icon.png", RempackFixture.Png(256, 256))
                .Text("locale/en.json", "{\"tank.t1.hint\": \"Fast\", \"tank_t1_role\": \"Scout\"}")
                .Text("locale/pt-BR.json", "{\"tank_t1_role\": \"Batedor\"}")
                .Text("LICENSE.txt", "CC-BY-4.0")
                .Text("README.md", "# Test")
                .Validate();

            Assert.That(report.Issues, Is.Empty, report.ToString());
        }

        [Test]
        public void Validate_RefusesFilesTheListDoesNotName()
        {
            var extra = new Dictionary<string, byte[]> { { "tanks/t1/extra.json", Encoding.UTF8.GetBytes("{}") } };
            var report = RempackFixture.Validate(RempackFixture.Tank().Tampered(null, extra), RempackFixture.Options());

            Assert.That(report.Has(RempackCodes.FileUnlisted), Is.True, report.ToString());
        }

        [Test]
        public void Validate_RefusesListedFilesThatAreMissing()
        {
            var package = RempackFixture.Tank().Tampered(files => files.Set("tanks/t1/skin.png", OtherHash));
            var report = RempackFixture.Validate(package, RempackFixture.Options());

            Assert.That(report.Has(RempackCodes.FileMissing), Is.True, report.ToString());
        }

        [Test]
        public void Validate_RefusesAFileThatDoesNotMatchItsHash()
        {
            var package = RempackFixture.Tank().Tampered(files => files.Set("tanks/t1/tank.json", OtherHash));
            var report = RempackFixture.Validate(package, RempackFixture.Options());

            Assert.That(report.Has(RempackCodes.FileHashMismatch), Is.True, report.ToString());
            Assert.That(report.FirstError.Path, Is.EqualTo("tanks/t1/tank.json"));
        }

        [Test]
        public void Validate_WithoutHashing_TrustsTheList()
        {
            var package = RempackFixture.Tank().Tampered(files => files.Set("tanks/t1/tank.json", OtherHash));
            var options = RempackFixture.Options();
            options.VerifyHashes = false;
            var report = RempackFixture.Validate(package, options);

            Assert.That(report.HasErrors, Is.False, report.ToString());
            Assert.That(options.Copy(true).VerifyHashes, Is.True, "a copy can hash again");
        }

        [Test]
        public void Validate_NamesTheFeaturesTheGameLacks()
        {
            var options = RempackFixture.Options();
            options.Features = new RempackFeatureSet("hangars@1");
            var report = RempackFixture.Tank().Validate(options);

            Assert.That(report.Has(RempackCodes.FeatureUnsupported), Is.True, report.ToString());
            Assert.That(RempackCodes.NeedsNewerGame(report.FirstError.Code), Is.True);
            Assert.That(report.FirstError.Args, Is.EqualTo(new[] { "tanks@1" }));
        }

        [Test]
        public void Validate_NeedsTheFeatureVersionThePackageAsksFor()
        {
            var fixture = RempackFixture.Tank().Set("game", "{\"features\": [\"tanks@2\"]}");

            Assert.That(fixture.Validate().Has(RempackCodes.FeatureUnsupported), Is.True);
        }

        [Test]
        public void Validate_AcceptsEverythingTheBuiltInSetProvides()
        {
            var game = RempackFeatureSet.Game;
            foreach (var name in game.Names)
            {
                Assert.That(game.Provides(name), Is.True, name);
            }
        }

        [TestCase("2.0.1", true)]
        [TestCase("2.0.0", false)]
        [TestCase("1.9.9", false)]
        public void Validate_RefusesPackagesForANewerGame(string minVersion, bool refused)
        {
            var fixture = RempackFixture.Tank().Set("game", "{\"features\": [\"tanks@1\"], \"minVersion\": \"" + minVersion + "\"}");
            var report = fixture.Validate();

            Assert.That(report.Has(RempackCodes.GameTooOld), Is.EqualTo(refused), report.ToString());
        }

        [Test]
        public void Validate_ChecksTheIconFormatAndSize()
        {
            Assert.That(RempackFixture.Tank().Bytes("icon.png", RempackFixture.Png(1024, 1024)).Validate().HasErrors, Is.False);
            Assert.That(RempackFixture.Tank().Bytes("icon.png", RempackFixture.Png(2048, 512)).Validate().Has(RempackCodes.ImageInvalid), Is.True);
            Assert.That(RempackFixture.Tank().Text("icon.png", "not an image").Validate().Has(RempackCodes.ImageInvalid), Is.True);
        }

        [Test]
        public void Validate_FindsTheIconTheManifestNames()
        {
            var named = RempackFixture.Tank().Set("icon", "\"art/badge.png\"").Bytes("art/badge.png", RempackFixture.Png(64, 64));
            var missing = RempackFixture.Tank().Set("icon", "\"art/badge.png\"");

            Assert.That(named.Validate().Issues, Is.Empty, named.Validate().ToString());
            Assert.That(missing.Validate().Has(RempackCodes.ReferenceMissing), Is.True);
        }

        [Test]
        public void Validate_ChecksEveryPreview()
        {
            var fixture = RempackFixture.Tank().Set("previews", "[\"art/one.jpg\", \"art/two.png\"]")
                .Bytes("art/one.jpg", Jpeg(1920, 1080));
            var report = fixture.Validate();

            Assert.That(report.Has(RempackCodes.ReferenceMissing), Is.True, report.ToString());
            fixture.Bytes("art/two.png", RempackFixture.Png(9000, 100));
            Assert.That(fixture.Validate().Has(RempackCodes.ImageInvalid), Is.True, "wider than any texture");
            fixture.Bytes("art/two.png", RempackFixture.Png(4096, 2048));
            Assert.That(fixture.Validate().Issues, Is.Empty, fixture.Validate().ToString());
        }

        [TestCase("locale/english.json", "{}", RempackCodes.LocaleLanguage)]
        [TestCase("locale/en.txt", "{}", RempackCodes.LocaleLanguage)]
        [TestCase("locale/en.json", "[\"a\"]", RempackCodes.FieldNotObject)]
        [TestCase("locale/en.json", "{\"Hint\": \"a\"}", RempackCodes.LocaleKey)]
        [TestCase("locale/en.json", "{\"1hint\": \"a\"}", RempackCodes.LocaleKey)]
        [TestCase("locale/en.json", "{\"hint\": 5}", RempackCodes.FieldNotString)]
        [TestCase("locale/en.json", "{\"hint\": \"a\\u0007b\"}", RempackCodes.TextControl)]
        [TestCase("locale/en.json", "{\"hint\": \"a\\u2066b\"}", RempackCodes.TextControl)]
        [TestCase("locale/en.json", "{\"hint\": ", RempackCodes.JsonInvalid)]
        public void Validate_ChecksTheStringTables(string path, string json, string code)
        {
            var report = RempackFixture.Tank().Text(path, json).Validate();

            Assert.That(report.Has(code), Is.True, report.ToString());
        }

        [Test]
        public void Validate_RefusesOverlongTableTexts()
        {
            var text = new string('a', RempackLimits.DescriptionLength + 1);
            var report = RempackFixture.Tank().Text("locale/ru.json", "{\"hint\": \"" + text + "\"}").Validate();

            Assert.That(report.Has(RempackCodes.TextTooLong), Is.True, report.ToString());
        }

        [Test]
        public void Validate_LetsLineBreaksThroughInTexts()
        {
            var report = RempackFixture.Tank().Text("locale/ru.json", "{\"hint\": \"one\\ntwo\\tthree\"}").Validate();

            Assert.That(report.Issues, Is.Empty, report.ToString());
        }

        [Test]
        public void Validate_RefusesContentWithoutADefinition()
        {
            var report = RempackFixture.Tank().Set("content", "{\"tanks\": [\"t1\", \"t2\"]}").Validate();

            Assert.That(report.Has(RempackCodes.ContentMissing), Is.True, report.ToString());
            Assert.That(report.FirstError.Args, Is.EqualTo(new[] { "t2", "tanks/t2/tank.json" }));
        }

        [TestCase("[1, 2]", RempackCodes.FieldNotObject)]
        [TestCase("\"tank\"", RempackCodes.FieldNotObject)]
        [TestCase("{\"name\": ", RempackCodes.JsonInvalid)]
        [TestCase("", RempackCodes.JsonEmpty)]
        public void Validate_RefusesABrokenDefinition(string json, string code)
        {
            var report = RempackFixture.Tank().Text("tanks/t1/tank.json", json).Validate();

            Assert.That(report.Has(code), Is.True, report.ToString());
        }

        [Test]
        public void Validate_OnlyWarnsAboutUnusedFiles()
        {
            var report = RempackFixture.Tank().Text("notes/todo.txt", "later").Text("docs/README.md", "inside a folder").Validate();

            Assert.That(report.HasErrors, Is.False, report.ToString());
            Assert.That(report.Count(RempackSeverity.Warning), Is.EqualTo(2), report.ToString());
            Assert.That(report.Has(RempackCodes.FileUnused), Is.True);
        }

        [TestCase("LICENSE", true)]
        [TestCase("license.md", true)]
        [TestCase("Licence.txt", true)]
        [TestCase("README.md", true)]
        [TestCase("CHANGELOG.txt", true)]
        [TestCase("CREDITS", true)]
        [TestCase("NOTICE.txt", true)]
        [TestCase("README.pdf", false)]
        [TestCase("docs/README.md", false)]
        [TestCase("LICENSES.txt", false)]
        [TestCase("", false)]
        public void IsDocument_KnowsTheFilesThatTravelUnused(string path, bool document)
        {
            Assert.That(RempackValidator.IsDocument(path), Is.EqualTo(document));
        }

        [Test]
        public void Validate_WarnsAboutHeavyJson()
        {
            var big = new StringBuilder("{\"name\": \"t1\", \"pad\": \"");
            big.Append('x', (int)RempackLimits.JsonSoftBytes);
            big.Append("\"}");
            var report = RempackFixture.Tank().Text("tanks/t1/tank.json", big.ToString()).Validate();

            Assert.That(report.HasErrors, Is.False, report.ToString());
            Assert.That(report.Has(RempackCodes.JsonHeavy), Is.True);
            Assert.That(RempackCodes.IsHeavy(RempackCodes.JsonHeavy), Is.True);
        }

        [Test]
        public void Validate_RefusesJsonOverTheHardLimit()
        {
            // Letters that do not compress 200:1, or the reader would call it a bomb first.
            var random = new Random(7);
            var big = new StringBuilder("{\"pad\": \"");
            for (var i = 0; i < RempackLimits.JsonBytes; i++)
            {
                big.Append((char)('a' + random.Next(26)));
            }

            big.Append("\"}");
            var report = RempackFixture.Tank().Text("tanks/t1/tank.json", big.ToString()).Validate();

            Assert.That(report.Has(RempackCodes.JsonTooLarge), Is.True, report.ToString());
        }

        [Test]
        public void Validate_LetsACheckerClaimTheFilesItUses()
        {
            var fixture = RempackFixture.Tank()
                .Text("tanks/t1/tank.json", "{\"model\": \"model.glb\"}")
                .Bytes("tanks/t1/model.glb", new byte[] { 1, 2, 3 })
                .Bytes("tanks/t1/old_model.glb", new byte[] { 4 });
            var options = RempackFixture.Options();
            var checker = new ModelChecker();
            options.Checkers.Add(checker);
            var report = fixture.Validate(options);

            Assert.That(checker.Seen, Is.EqualTo(new[] { "t1 tanks/t1/tank.json -> tanks/t1/model.glb" }));
            Assert.That(report.HasErrors, Is.False, report.ToString());
            Assert.That(report.Issues.Count, Is.EqualTo(1), report.ToString());
            Assert.That(report.Issues[0].Code, Is.EqualTo(RempackCodes.FileUnused));
            Assert.That(report.Issues[0].Path, Is.EqualTo("tanks/t1/old_model.glb"));
        }

        [Test]
        public void Validate_ReportsWhatACheckerCannotFind()
        {
            var fixture = RempackFixture.Tank().Text("tanks/t1/tank.json", "{\"model\": \"../t2/model.glb\"}");
            var options = RempackFixture.Options();
            options.Checkers.Add(new ModelChecker());
            var report = fixture.Validate(options);

            Assert.That(report.Has(RempackCodes.ReferenceMissing), Is.True, report.ToString());
            Assert.That(report.FirstError.Args, Is.EqualTo(new[] { "model", "tanks/t2/model.glb" }));
        }

        [Test]
        public void Validate_TurnsACheckerExceptionIntoAnIssue()
        {
            var options = RempackFixture.Options();
            options.Checkers.Add(new ThrowingChecker());
            var report = RempackFixture.Tank().Validate(options);

            Assert.That(report.Has(RempackCodes.FieldRange), Is.True, report.ToString());
        }

        [Test]
        public void ValidateFile_ReportsAMissingFile()
        {
            var report = RempackValidator.ValidateFile("/nonexistent/" + Guid.NewGuid().ToString("N") + ".rempack", RempackFixture.Options());

            Assert.That(report.Has(RempackCodes.FileUnreadable), Is.True, report.ToString());
        }

        [Test]
        public void Validate_StopsAtAFormatItDoesNotKnow()
        {
            var fixture = RempackFixture.Tank().Set("formatVersion", "2").Set("layout", "{\"v2\": true}");
            var report = fixture.Validate();

            Assert.That(report.Has(RempackCodes.ManifestFormatVersion), Is.True, report.ToString());
            Assert.That(report.Has(RempackCodes.FieldUnknown), Is.False, "a newer layout is not judged by the old rules");
        }

        /// <summary>A JPEG header (SOI, APP0, SOF0) of the given size: all the probe reads.</summary>
        internal static byte[] Jpeg(int width, int height)
        {
            return new byte[]
            {
                0xFF, 0xD8,
                0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
                0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
                0xFF, 0xD9,
            };
        }

        private sealed class ModelChecker : IRempackContentChecker
        {
            public readonly List<string> Seen = new List<string>();

            public RempackContentKind Kind
            {
                get { return RempackContentKind.Tank; }
            }

            public void Check(RempackContentContext context)
            {
                var model = context.Reference("model", context.Definition["model"].AsString());
                Seen.Add(context.LocalId + " " + context.DefinitionPath + " -> " + model);
            }
        }

        private sealed class ThrowingChecker : IRempackContentChecker
        {
            public RempackContentKind Kind
            {
                get { return RempackContentKind.Tank; }
            }

            public void Check(RempackContentContext context)
            {
                throw new RempackException(RempackCodes.FieldRange, context.DefinitionPath, "speed", "0", "100");
            }
        }
    }
}
