using System.IO;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Tests;
using NUnit.Framework;

namespace BlitzLegacy.Sdk.Tests
{
    public sealed class CliInspectTests
    {
        private static RempackFixture Described()
        {
            return RempackFixture.Tank()
                .Set("description", "{\"en\": \"A heavy tank.\\nWith two turrets.\", \"ru\": \"\u0422\u044f\u0436\u0451\u043b\u044b\u0439 \u0442\u0430\u043d\u043a.\"}")
                .Set("license", "\"CC-BY-4.0\"")
                .Set("homepage", "\"https://example.com/mod\"")
                .Depends("author.base_pack", ">=1.2.0")
                .Conflicts("rival.tank_pack", "*");
        }

        [Test]
        public void ShowsWhatAPackageIsAndHolds()
        {
            using (var folder = new ModFolder())
            {
                var package = Described().Write(folder.Base);

                var run = CliRun.Of(folder, "inspect", package);

                Assert.That(run.Exit, Is.EqualTo(Cli.AcceptedExit), run.ToString());
                Assert.That(run.Output, Does.Contain("File:"));
                Assert.That(run.Output, Does.Match(@"Id:\s+author\.test_mod"));
                Assert.That(run.Output, Does.Match(@"Version:\s+1\.0\.0"));
                Assert.That(run.Output, Does.Match(@"Name:\s+Test mod"));
                Assert.That(run.Output, Does.Match(@"Description:\s+A heavy tank\.\r?\n\s+With two turrets\."));
                Assert.That(run.Output, Does.Match(@"Authors:\s+Tester"));
                Assert.That(run.Output, Does.Match(@"License:\s+CC-BY-4\.0"));
                Assert.That(run.Output, Does.Match(@"Homepage:\s+https://example\.com/mod"));
                Assert.That(run.Output, Does.Match(@"Game:\s+any version; tanks@1"));
                Assert.That(run.Output, Does.Match(@"Requires:\s+author\.base_pack >=1\.2\.0"));
                Assert.That(run.Output, Does.Match(@"Conflicts with:\s+rival\.tank_pack \*"));
                Assert.That(run.Output, Does.Match(@"Content:\s+Tanks \(1\): t1"));
                Assert.That(run.Output, Does.Match(@"Replaces:\s+none"));
                Assert.That(run.Output, Does.Match(@"Size:\s+\d+ B packed, \d+ B unpacked, files: 2"));
            }
        }

        [Test]
        public void AnAcceptedPackageInspectsClean()
        {
            using (var folder = new ModFolder())
            {
                var package = RempackFixture.Tank().Write(folder.Base);

                var run = CliRun.Of(folder, "inspect", package);

                Assert.That(run.Exit, Is.EqualTo(Cli.AcceptedExit), run.ToString());
                Assert.That(run.Output, Does.Contain("Errors: 0, warnings: 0, notes: 0."));
            }
        }

        [Test]
        public void FilesListsEveryEntry()
        {
            using (var folder = new ModFolder())
            {
                var package = RempackFixture.Tank().Write(folder.Base);

                var run = CliRun.Of(folder, "inspect", package, "--files");

                Assert.That(run.Output, Does.Contain("Files (size, packed, path):"));
                Assert.That(run.Output, Does.Match(@"\d+ B\s+\d+ B  rempack\.json"));
                Assert.That(run.Output, Does.Match(@"\d+ B\s+\d+ B  tanks/t1/tank\.json"));
            }
        }

        [Test]
        public void SpeaksRussian()
        {
            using (var folder = new ModFolder())
            {
                var package = RempackFixture.Tank().Write(folder.Base);
                var broken = RempackFixture.Tank().Set("content", "{\"tanks\": [\"t1\", \"t2\"]}").Write(folder.Base, "broken.rempack");

                var run = CliRun.Of(folder, "inspect", package, "--lang", "ru");
                var refused = CliRun.Of(folder, "inspect", broken, "--lang", "ru");

                Assert.That(run.Output, Does.Match("\u0412\u0435\u0440\u0441\u0438\u044f:\\s+1\\.0\\.0"));
                Assert.That(run.Output, Does.Match("\u041d\u0430\u0437\u0432\u0430\u043d\u0438\u0435:\\s+\u0422\u0435\u0441\u0442\u043e\u0432\u044b\u0439 \u043c\u043e\u0434"));
                Assert.That(run.Output, Does.Contain("\u041f\u0440\u0438\u043d\u044f\u0442"));
                Assert.That(refused.Output, Does.Contain("\u041e\u0448\u0438\u0431\u043a\u0430: rempack.json: "));
                Assert.That(refused.Output, Does.Contain("\u041e\u0442\u043a\u043b\u043e\u043d\u0451\u043d"));
            }
        }

        [Test]
        public void JsonCarriesTheManifestAndEveryEntry()
        {
            var fixture = RempackFixture.Tank();
            using (var folder = new ModFolder())
            {
                var package = fixture.Write(folder.Base);

                var json = CliRun.Of(folder, "inspect", package, "--json").Json;

                Assert.That(json["manifest"]["id"].AsString(), Is.EqualTo("author.test_mod"));
                Assert.That(json["manifest"]["content"]["tanks"][0].AsString(), Is.EqualTo("t1"));
                Assert.That(json["sizes"]["archive"].AsLong(), Is.EqualTo(new FileInfo(package).Length));
                Assert.That(json["sizes"]["files"].AsInt(), Is.EqualTo(2));
                var entries = json["entries"];
                Assert.That(entries[0]["path"].AsString(), Is.EqualTo("rempack.json"));
                Assert.That(entries[0]["sha256"].IsNull, Is.True);
                Assert.That(entries[1]["path"].AsString(), Is.EqualTo("tanks/t1/tank.json"));
                Assert.That(entries[1]["sha256"].AsString(), Is.EqualTo(RempackBuilder.Sha256(fixture.Files["tanks/t1/tank.json"])));
                Assert.That(json["ok"].AsBool(), Is.True);
            }
        }

        [Test]
        public void AFileThatDoesNotOpenIsStillReported()
        {
            using (var folder = new ModFolder())
            {
                var path = Path.Combine(folder.Base, "empty.rempack");
                File.WriteAllBytes(path, new byte[0]);

                var run = CliRun.Of(folder, "inspect", path, "--json");

                Assert.That(run.Exit, Is.EqualTo(Cli.RefusedExit), run.ToString());
                Assert.That(run.Json["manifest"].IsNull, Is.True);
                Assert.That(run.Codes(), Does.Contain(RempackCodes.ZipInvalid));
            }
        }

        [Test]
        public void InspectNeedsAFile()
        {
            using (var folder = new ModFolder())
            {
                var run = CliRun.Of(folder, "inspect", folder.Root);

                Assert.That(run.Exit, Is.EqualTo(Cli.UsageExit));
                Assert.That(run.Error, Does.Contain("is a folder; inspect needs a .rempack file"));
            }
        }
    }
}
