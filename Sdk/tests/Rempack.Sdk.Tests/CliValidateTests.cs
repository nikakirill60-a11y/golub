using System.IO;
using System.Text;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Tests;
using NUnit.Framework;

namespace BlitzLegacy.Sdk.Tests
{
    public sealed class CliValidateTests
    {
        [Test]
        public void ValidatesAFolderWithoutWritingAnything()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank()))
            {
                var before = ModFolder.Listing(folder.Base);

                var run = CliRun.Of(folder, "validate", folder.Root);

                Assert.That(run.Exit, Is.EqualTo(Cli.AcceptedExit), run.ToString());
                Assert.That(run.Output, Does.Contain("Checking " + folder.Root));
                Assert.That(run.Output, Does.Contain("Accepted: the game will install this package."));
                Assert.That(ModFolder.Listing(folder.Base), Is.EqualTo(before));
                Assert.That(Directory.GetFiles(folder.Temp), Is.Empty);
            }
        }

        [Test]
        public void AFolderIsCheckedAsThePackageItWouldBecome()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank().Text("notes.txt", "todo")))
            {
                folder.Text("tanks/t1/My Skin.png", "png");

                var run = CliRun.Of(folder, "validate", folder.Root, "--json");

                Assert.That(run.Exit, Is.EqualTo(Cli.RefusedExit), run.ToString());
                Assert.That(run.Json["folder"].AsBool(), Is.True);
                Assert.That(run.Paths(RempackCodes.PathInvalid), Is.EqualTo(new[] { "tanks/t1/My Skin.png" }));
                Assert.That(run.Paths(RempackCodes.FileUnused), Is.EqualTo(new[] { "notes.txt" }));
                Assert.That(Directory.GetFiles(folder.Temp), Is.Empty);
            }
        }

        [Test]
        public void ValidatesAPackageFile()
        {
            var fixture = RempackFixture.Tank();
            using (var folder = new ModFolder())
            {
                var package = fixture.Write(folder.Base);

                var run = CliRun.Of(folder, "validate", package, "--json");

                Assert.That(run.Exit, Is.EqualTo(Cli.AcceptedExit), run.ToString());
                Assert.That(run.Json["folder"].AsBool(true), Is.False);
                Assert.That(run.Json["path"].AsString(), Is.EqualTo(Path.GetFullPath(package)));
            }
        }

        [Test]
        public void ATamperedPackageIsRefused()
        {
            var fixture = RempackFixture.Tank();
            using (var folder = new ModFolder())
            {
                var package = Path.Combine(folder.Base, "tampered.rempack");
                File.WriteAllBytes(package, fixture.Tampered(delegate(BlitzLegacy.Core.Serialization.JsonNode files)
                {
                    files.Set("tanks/t1/tank.json", new string('0', 64));
                }));

                var run = CliRun.Of(folder, "validate", package, "--json");

                Assert.That(run.Exit, Is.EqualTo(Cli.RefusedExit), run.ToString());
                Assert.That(run.Paths(RempackCodes.FileHashMismatch), Is.EqualTo(new[] { "tanks/t1/tank.json" }));
                Assert.That(run.Json["ok"].AsBool(true), Is.False);
                Assert.That(run.Json["status"].AsString(), Is.EqualTo("refused"));
            }
        }

        [Test]
        public void AFileThatIsNoPackageIsRefused()
        {
            using (var folder = new ModFolder())
            {
                var path = Path.Combine(folder.Base, "photo.rempack");
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("this is not a zip archive at all"));

                var run = CliRun.Of(folder, "validate", path, "--json");

                Assert.That(run.Exit, Is.EqualTo(Cli.RefusedExit), run.ToString());
                Assert.That(run.Codes(), Does.Contain(RempackCodes.ZipInvalid));
            }
        }

        [Test]
        public void APackageForANewerGameSaysSo()
        {
            var fixture = RempackFixture.Tank().Set("game", "{\"features\": [\"tanks@1\", \"maps@9\"]}");
            using (var folder = ModFolder.Of(fixture))
            {
                var text = CliRun.Of(folder, "validate", folder.Root);
                var json = CliRun.Of(folder, "validate", folder.Root, "--json");

                Assert.That(text.Exit, Is.EqualTo(Cli.RefusedExit), text.ToString());
                Assert.That(text.Output, Does.Contain("Only a newer game can load this package"));
                Assert.That(json.Json["needsNewerGame"].AsBool(), Is.True);
                Assert.That(json.Codes(), Is.EqualTo(new[] { RempackCodes.FeatureUnsupported }));
            }
        }

        [Test]
        public void ABrokenPackageIsNotBlamedOnTheGame()
        {
            var fixture = RempackFixture.Tank().Set("game", "{\"features\": [\"tanks@1\", \"maps@9\"]}")
                .Set("content", "{\"tanks\": [\"t1\", \"t2\"]}");
            using (var folder = ModFolder.Of(fixture))
            {
                var run = CliRun.Of(folder, "validate", folder.Root, "--json");

                Assert.That(run.Exit, Is.EqualTo(Cli.RefusedExit), run.ToString());
                Assert.That(run.Json["needsNewerGame"].AsBool(true), Is.False);
            }
        }

        [Test]
        public void VerboseOutputNamesTheCodes()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank().Text("notes.txt", "todo")))
            {
                var run = CliRun.Of(folder, "validate", folder.Root, "--verbose");

                Assert.That(run.Output, Does.Contain("Warning: notes.txt: No content uses this file. [file.unused]"));
            }
        }
    }
}
