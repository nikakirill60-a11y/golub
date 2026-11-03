using System.IO;
using System.Text;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Tests;
using NUnit.Framework;

namespace BlitzLegacy.Sdk.Tests
{
    public sealed class CliPackTests
    {
        [Test]
        public void PacksAnAcceptedPackageBesideTheFolder()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank()))
            {
                folder.Text("README.md", "# Test mod");
                folder.Text("tanks/t1/model.blend", "editor scene");
                folder.Text(".git/HEAD", "ref: refs/heads/main");
                folder.Text("Thumbs.db", "junk");

                var run = CliRun.Of(folder, "pack", folder.Root);

                Assert.That(run.Exit, Is.EqualTo(Cli.AcceptedExit), run.ToString());
                Assert.That(run.Output, Does.Contain("Accepted"));
                Assert.That(run.Output, Does.Contain("Written: " + folder.DefaultPackage));
                Assert.That(ModFolder.Entries(folder.DefaultPackage),
                    Is.EqualTo(new[] { "README.md", "rempack.json", "tanks/t1/tank.json" }));
                var bytes = File.ReadAllBytes(folder.DefaultPackage);
                Assert.That(RempackFixture.Validate(bytes, RempackFixture.Options()).HasErrors, Is.False);
                var report = new RempackReport();
                using (var package = RempackPackage.Open(folder.DefaultPackage, report))
                {
                    Assert.That(package.Manifest.Files["tanks/t1/tank.json"],
                        Is.EqualTo(RempackBuilder.Sha256(File.ReadAllBytes(folder.PathOf("tanks/t1/tank.json")))));
                }

                Assert.That(ModFolder.Listing(folder.Base), Has.None.EndsWith(".tmp"));
            }
        }

        [Test]
        public void PackingTheSameFolderTwiceGivesTheSameBytes()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank().Text("tanks/t1/notes.json", "{}")))
            {
                var first = Path.Combine(folder.Base, "first.rempack");
                var second = Path.Combine(folder.Base, "second.rempack");

                Assert.That(CliRun.Of(folder, "pack", folder.Root, "-o", first).Exit, Is.EqualTo(Cli.AcceptedExit));
                Assert.That(CliRun.Of(folder, "pack", folder.Root, "--output=" + second).Exit, Is.EqualTo(Cli.AcceptedExit));

                Assert.That(File.ReadAllBytes(second), Is.EqualTo(File.ReadAllBytes(first)));
            }
        }

        [Test]
        public void ARefusedPackageIsNotWritten()
        {
            var fixture = RempackFixture.Tank().Set("content", "{\"tanks\": [\"t1\", \"t2\"]}");
            using (var folder = ModFolder.Of(fixture))
            {
                var run = CliRun.Of(folder, "pack", folder.Root);

                Assert.That(run.Exit, Is.EqualTo(Cli.RefusedExit), run.ToString());
                Assert.That(run.Output, Does.Contain("Error: rempack.json: Content \"t2\" has no definition file tanks/t2/tank.json."));
                Assert.That(run.Output, Does.Contain("Refused"));
                Assert.That(run.Output, Does.Contain("No package was written."));
                Assert.That(File.Exists(folder.DefaultPackage), Is.False);
                Assert.That(ModFolder.Listing(folder.Base), Has.None.EndsWith(".tmp"));
            }
        }

        [Test]
        public void AnEarlierPackageStaysWhenTheNewOneIsRefused()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank()))
            {
                Assert.That(CliRun.Of(folder, "pack", folder.Root).Exit, Is.EqualTo(Cli.AcceptedExit));
                var before = File.ReadAllBytes(folder.DefaultPackage);
                File.Delete(folder.PathOf("tanks/t1/tank.json"));

                Assert.That(CliRun.Of(folder, "pack", folder.Root).Exit, Is.EqualTo(Cli.RefusedExit));

                Assert.That(File.ReadAllBytes(folder.DefaultPackage), Is.EqualTo(before));
            }
        }

        [Test]
        public void StrictModeRefusesWarnings()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank().Text("notes.txt", "todo")))
            {
                var relaxed = Path.Combine(folder.Base, "relaxed.rempack");
                var strict = Path.Combine(folder.Base, "strict.rempack");

                var accepted = CliRun.Of(folder, "pack", folder.Root, "-o", relaxed);
                var refused = CliRun.Of(folder, "pack", folder.Root, "-o", strict, "--strict");

                Assert.That(accepted.Exit, Is.EqualTo(Cli.AcceptedExit), accepted.ToString());
                Assert.That(accepted.Output, Does.Contain("Warning: notes.txt"));
                Assert.That(refused.Exit, Is.EqualTo(Cli.RefusedExit), refused.ToString());
                Assert.That(refused.Output, Does.Contain("Refused in strict mode"));
                Assert.That(File.Exists(relaxed), Is.True);
                Assert.That(File.Exists(strict), Is.False);
            }
        }

        [Test]
        public void NamesAPackageCannotHoldAreReportedOnce()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank()))
            {
                folder.Text("tanks/t1/My Texture.png", "png");
                folder.Text("Source Files/a.txt", "a");
                folder.Text("Source Files/b.txt", "b");

                var run = CliRun.Of(folder, "pack", folder.Root, "--json");

                Assert.That(run.Exit, Is.EqualTo(Cli.RefusedExit), run.ToString());
                Assert.That(run.Paths(RempackCodes.PathInvalid), Is.EqualTo(new[] { "Source Files/", "tanks/t1/My Texture.png" }));
                Assert.That(run.Json["written"].AsBool(true), Is.False);
            }
        }

        [Test]
        public void TheIgnoreFileLeavesFilesOut()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank()))
            {
                folder.Text(".rempackignore", "# sources\ndocs/\n*.txt\n!LICENSE.txt\n");
                folder.Text("docs/guide.md", "guide");
                folder.Text("tanks/t1/todo.txt", "todo");
                folder.Text("LICENSE.txt", "CC-BY-4.0");

                var run = CliRun.Of(folder, "pack", folder.Root, "-v");

                Assert.That(run.Exit, Is.EqualTo(Cli.AcceptedExit), run.ToString());
                Assert.That(run.Output, Does.Contain("Not packed (ignored): docs/"));
                Assert.That(run.Output, Does.Contain("Not packed (ignored): tanks/t1/todo.txt"));
                Assert.That(ModFolder.Entries(folder.DefaultPackage),
                    Is.EqualTo(new[] { "LICENSE.txt", "rempack.json", "tanks/t1/tank.json" }));
            }
        }

        [Test]
        public void APackageWrittenIntoItsFolderIsNotPackedAgain()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank()))
            {
                var inside = folder.PathOf("out.rempack");

                Assert.That(CliRun.Of(folder, "pack", folder.Root, "-o", inside).Exit, Is.EqualTo(Cli.AcceptedExit));
                var again = CliRun.Of(folder, "pack", folder.Root, "-o", inside);

                Assert.That(again.Exit, Is.EqualTo(Cli.AcceptedExit), again.ToString());
                Assert.That(ModFolder.Entries(inside), Is.EqualTo(new[] { "rempack.json", "tanks/t1/tank.json" }));
            }
        }

        [Test]
        public void AnOutputFolderTakesTheDefaultName()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank(version: "2.1.0-beta.1")))
            {
                var dist = Path.Combine(folder.Base, "dist");
                Directory.CreateDirectory(dist);

                var run = CliRun.Of(folder, "pack", folder.Root, "-o", dist);

                Assert.That(run.Exit, Is.EqualTo(Cli.AcceptedExit), run.ToString());
                Assert.That(File.Exists(Path.Combine(dist, "author.test_mod-2.1.0-beta.1.rempack")), Is.True);
            }
        }

        [Test]
        public void AMissingOutputFolderIsCreated()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank()))
            {
                var nested = Path.Combine(folder.Base, "build", "mods", "tank.rempack");

                Assert.That(CliRun.Of(folder, "pack", folder.Root, "-o", nested).Exit, Is.EqualTo(Cli.AcceptedExit));

                Assert.That(File.Exists(nested), Is.True);
            }
        }

        [Test]
        public void TheDefaultNameFallsBackToTheFolderName()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank(id: "Not An Id", version: "latest")))
            {
                var source = SourceFolder.Scan(folder.Root);

                Assert.That(PackCommand.DefaultName(source), Is.EqualTo("my_mod.rempack"));
                Assert.That(PackCommand.OutputPath(null, source), Is.EqualTo(Path.Combine(folder.Base, "my_mod.rempack")));
            }
        }

        [Test]
        public void TheJsonReportDescribesTheResult()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank().Text("notes.txt", "todo")))
            {
                var run = CliRun.Of(folder, "pack", folder.Root, "--json");
                var json = run.Json;

                Assert.That(run.Exit, Is.EqualTo(Cli.AcceptedExit), run.ToString());
                Assert.That(json["tool"].AsString(), Is.EqualTo("rempack"));
                Assert.That(json["command"].AsString(), Is.EqualTo("pack"));
                Assert.That(json["ok"].AsBool(), Is.True);
                Assert.That(json["status"].AsString(), Is.EqualTo("accepted"));
                Assert.That(json["written"].AsBool(), Is.True);
                Assert.That(json["output"].AsString(), Is.EqualTo(folder.DefaultPackage));
                Assert.That(json["files"].AsInt(), Is.EqualTo(3));
                Assert.That(json["size"].AsLong(), Is.EqualTo(new FileInfo(folder.DefaultPackage).Length));
                Assert.That(json["counts"]["warnings"].AsInt(), Is.EqualTo(1));
                var issue = json["issues"][0];
                Assert.That(issue["severity"].AsString(), Is.EqualTo("warning"));
                Assert.That(issue["code"].AsString(), Is.EqualTo(RempackCodes.FileUnused));
                Assert.That(issue["path"].AsString(), Is.EqualTo("notes.txt"));
                Assert.That(issue["message"].AsString(), Is.EqualTo("No content uses this file."));
            }
        }

        [Test]
        public void ManifestProblemsStopThePack()
        {
            AssertRefused(null, RempackCodes.ManifestMissing);
            AssertRefused(Encoding.UTF8.GetBytes("{\"format\": "), RempackCodes.JsonInvalid);
            AssertRefused(new byte[] { (byte)'{', 0xFF, (byte)'}' }, RempackCodes.TextNotUtf8);
            AssertRefused(Encoding.UTF8.GetBytes("[1, 2]"), RempackCodes.FieldNotObject);
        }

        [Test]
        public void AManifestWithAByteOrderMarkIsRead()
        {
            var fixture = RempackFixture.Tank();
            using (var folder = ModFolder.Of(fixture))
            {
                var text = new UTF8Encoding(true).GetPreamble();
                var body = Encoding.UTF8.GetBytes(fixture.Manifest.ToJson(true));
                var data = new byte[text.Length + body.Length];
                text.CopyTo(data, 0);
                body.CopyTo(data, text.Length);
                folder.Bytes(RempackFormat.ManifestFile, data);

                var run = CliRun.Of(folder, "pack", folder.Root);

                Assert.That(run.Exit, Is.EqualTo(Cli.AcceptedExit), run.ToString());
            }
        }

        [Test]
        public void LinksAreNotFollowed()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank()))
            {
                var outside = Path.Combine(folder.Base, "outside.png");
                File.WriteAllText(outside, "png");
                try
                {
                    File.CreateSymbolicLink(folder.PathOf("tanks/t1/linked.png"), outside);
                    Directory.CreateSymbolicLink(folder.PathOf("shared"), folder.Base);
                }
                catch (IOException)
                {
                    Assert.Ignore("This system does not let the test create links.");
                }
                catch (System.UnauthorizedAccessException)
                {
                    Assert.Ignore("This system does not let the test create links.");
                }

                var run = CliRun.Of(folder, "pack", folder.Root);

                Assert.That(run.Exit, Is.EqualTo(Cli.AcceptedExit), run.ToString());
                Assert.That(run.Output, Does.Contain("links are not followed: shared/"));
                Assert.That(run.Output, Does.Contain("links are not followed: tanks/t1/linked.png"));
                Assert.That(ModFolder.Entries(folder.DefaultPackage), Is.EqualTo(new[] { "rempack.json", "tanks/t1/tank.json" }));
            }
        }

        [Test]
        public void NamesThatDifferOnlyInCaseAreRefused()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank()))
            {
                folder.Text("tanks/t1/skin.png", "a");
                folder.Text("tanks/t1/Skin.png", "b");
                if (Directory.GetFiles(folder.PathOf("tanks/t1")).Length < 3)
                {
                    Assert.Ignore("This file system ignores case, so the two names are one file.");
                }

                var run = CliRun.Of(folder, "pack", folder.Root, "--json");

                Assert.That(run.Exit, Is.EqualTo(Cli.RefusedExit), run.ToString());
                Assert.That(run.Paths(RempackCodes.PathDuplicate), Is.EqualTo(new[] { "tanks/t1/skin.png" }));
            }
        }

        [Test]
        public void PackNeedsAFolder()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank()))
            {
                var run = CliRun.Of(folder, "pack", folder.PathOf(RempackFormat.ManifestFile));

                Assert.That(run.Exit, Is.EqualTo(Cli.UsageExit));
                Assert.That(run.Error, Does.Contain("is a file; pack needs a mod folder"));
            }
        }

        private static void AssertRefused(byte[] manifest, string code)
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank()))
            {
                if (manifest == null)
                {
                    File.Delete(folder.PathOf(RempackFormat.ManifestFile));
                }
                else
                {
                    folder.Bytes(RempackFormat.ManifestFile, manifest);
                }

                var run = CliRun.Of(folder, "pack", folder.Root, "--json");

                Assert.That(run.Exit, Is.EqualTo(Cli.RefusedExit), code + ": " + run);
                Assert.That(run.Codes(), Does.Contain(code));
                Assert.That(run.Json["written"].AsBool(true), Is.False);
                Assert.That(ModFolder.Listing(folder.Base), Has.None.EndsWith(".rempack"));
            }
        }
    }
}
