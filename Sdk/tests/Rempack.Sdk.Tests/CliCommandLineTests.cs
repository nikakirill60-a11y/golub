using System.IO;
using BlitzLegacy.Core;
using BlitzLegacy.Tests;
using NUnit.Framework;

namespace BlitzLegacy.Sdk.Tests
{
    public sealed class CliCommandLineTests
    {
        [Test]
        public void AnUnknownOptionIsAUsageError()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank()))
            {
                var run = CliRun.Of(folder, "validate", folder.Root, "--frobnicate");

                Assert.That(run.Exit, Is.EqualTo(Cli.UsageExit));
                Assert.That(run.Error, Does.Contain("Unknown option: --frobnicate"));
                Assert.That(run.Error, Does.Contain("rempack --help"));
                Assert.That(run.Output, Is.Empty);
            }
        }

        [Test]
        public void UsageErrorsAreJsonWhenJsonWasAskedFor()
        {
            var run = CliRun.Of(null, "validate", "--frobnicate", "--json");

            Assert.That(run.Exit, Is.EqualTo(Cli.UsageExit));
            Assert.That(run.Json["ok"].AsBool(true), Is.False);
            Assert.That(run.Json["status"].AsString(), Is.EqualTo("usage"));
            Assert.That(run.Json["error"]["code"].AsString(), Is.EqualTo(CliKeys.UnknownOption));
            Assert.That(run.Json["error"]["message"].AsString(), Is.EqualTo("Unknown option: --frobnicate"));
        }

        [Test]
        public void CommandsNeedExactlyOnePath()
        {
            var missing = CliRun.Of(null, "validate");
            var extra = CliRun.Of(null, "inspect", "a.rempack", "b.rempack");

            Assert.That(missing.Exit, Is.EqualTo(Cli.UsageExit));
            Assert.That(missing.Error, Does.Contain("Give the path of a mod folder or a .rempack file."));
            Assert.That(extra.Exit, Is.EqualTo(Cli.UsageExit));
            Assert.That(extra.Error, Does.Contain("Unexpected argument: b.rempack"));
        }

        [Test]
        public void UnknownCommandsAndPathsAreUsageErrors()
        {
            var command = CliRun.Of(null, "frobnicate");
            var path = CliRun.Of(null, "validate", Path.Combine(Path.GetTempPath(), "no_such_mod_" + System.Guid.NewGuid().ToString("N")));

            Assert.That(command.Exit, Is.EqualTo(Cli.UsageExit));
            Assert.That(command.Error, Does.Contain("Unknown command or no such file or folder: frobnicate"));
            Assert.That(path.Exit, Is.EqualTo(Cli.UsageExit));
            Assert.That(path.Error, Does.Contain("No such file or folder: "));
        }

        [Test]
        public void OptionValuesAreChecked()
        {
            var output = CliRun.Of(null, "pack", "mod", "-o");
            var empty = CliRun.Of(null, "pack", "mod", "--output=");
            CliRun language;
            var forced = System.Environment.GetEnvironmentVariable(CliLanguage.Variable);
            try
            {
                // A language the tool does not speak leaves it to the environment's.
                System.Environment.SetEnvironmentVariable(CliLanguage.Variable, "en");
                language = CliRun.Of(null, "--lang", "de", "validate", "mod");
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(CliLanguage.Variable, forced);
            }

            Assert.That(output.Exit, Is.EqualTo(Cli.UsageExit));
            Assert.That(output.Error, Does.Contain("-o needs a value."));
            Assert.That(empty.Exit, Is.EqualTo(Cli.UsageExit));
            Assert.That(empty.Error, Does.Contain("--output needs a value."));
            Assert.That(language.Exit, Is.EqualTo(Cli.UsageExit));
            Assert.That(language.Error, Does.Contain("Unsupported language: de. Use en or ru."));
        }

        [Test]
        public void HelpExplainsTheTool()
        {
            var help = CliRun.Of(null, "--help");
            var nothing = CliRun.Of(null);
            var windows = CliRun.Of(null, "/?");

            Assert.That(help.Exit, Is.EqualTo(Cli.AcceptedExit));
            Assert.That(help.Output, Does.Contain("rempack " + BlitzVersion.Current));
            Assert.That(help.Output, Does.Contain("rempack validate <folder|file.rempack>"));
            Assert.That(help.Output, Does.Contain("Exit codes: 0 accepted, 1 refused"));
            Assert.That(nothing.Exit, Is.EqualTo(Cli.UsageExit));
            Assert.That(nothing.Output, Is.EqualTo(help.Output));
            Assert.That(windows.Exit, Is.EqualTo(Cli.AcceptedExit));
        }

        [Test]
        public void EachCommandHasItsOwnHelp()
        {
            var topic = CliRun.Of(null, "help", "pack");
            var flag = CliRun.Of(null, "pack", "--help");
            var unknown = CliRun.Of(null, "help", "frobnicate");

            Assert.That(topic.Exit, Is.EqualTo(Cli.AcceptedExit));
            Assert.That(topic.Output, Does.StartWith("rempack pack <folder> [-o <file.rempack>]"));
            Assert.That(flag.Output, Is.EqualTo(topic.Output));
            Assert.That(CliRun.Of(null, "help", "validate").Output, Does.StartWith("rempack validate <folder|file.rempack>"));
            Assert.That(CliRun.Of(null, "inspect", "-h").Output, Does.StartWith("rempack inspect <file.rempack>"));
            Assert.That(unknown.Exit, Is.EqualTo(Cli.UsageExit));
        }

        [Test]
        public void VersionNamesWhatTheGameLoads()
        {
            var text = CliRun.Of(null, "--version");
            var json = CliRun.Of(null, "--version", "--json").Json;

            Assert.That(text.Exit, Is.EqualTo(Cli.AcceptedExit));
            Assert.That(text.Output, Does.Contain("rempack " + BlitzVersion.Current + ", package format 1; the game loads: hangars@1, maps@1, overrides@1, tanks@1"));
            Assert.That(json["version"].AsString(), Is.EqualTo(BlitzVersion.Current));
            Assert.That(json["formatVersion"].AsInt(), Is.EqualTo(1));
            Assert.That(json["features"].Count, Is.EqualTo(4));
        }

        [Test]
        public void ADroppedPathPicksItsCommand()
        {
            using (var folder = ModFolder.Of(RempackFixture.Tank()))
            {
                var packed = CliRun.Of(folder, folder.Root);
                var inspected = CliRun.Of(folder, folder.DefaultPackage);

                Assert.That(packed.Exit, Is.EqualTo(Cli.AcceptedExit), packed.ToString());
                Assert.That(File.Exists(folder.DefaultPackage), Is.True);
                Assert.That(inspected.Exit, Is.EqualTo(Cli.AcceptedExit), inspected.ToString());
                Assert.That(inspected.Output, Does.Match(@"Id:\s+author\.test_mod"));
            }
        }

        [Test]
        public void OptionsMayComeAnywhere()
        {
            var options = CliOptions.Parse(new[] { "--json", "pack", "-v", "mod", "--strict", "-o", "out.rempack", "--no-color" });

            Assert.That(options.Error, Is.Null);
            Assert.That(options.Command, Is.EqualTo(CliOptions.Pack));
            Assert.That(options.Arguments, Is.EqualTo(new[] { "mod" }));
            Assert.That(options.Json && options.Verbose && options.Strict && options.NoColor, Is.True);
            Assert.That(options.Output, Is.EqualTo("out.rempack"));
        }

        [Test]
        public void ADoubleDashEndsTheOptions()
        {
            var options = CliOptions.Parse(new[] { "validate", "--", "--json" });

            Assert.That(options.Error, Is.Null);
            Assert.That(options.Json, Is.False);
            Assert.That(options.Arguments, Is.EqualTo(new[] { "--json" }));
        }

        [Test]
        public void ALanguageComesFromTheLocale()
        {
            Assert.That(CliLanguage.FromLocale("ru_RU.UTF-8"), Is.EqualTo("ru"));
            Assert.That(CliLanguage.FromLocale("RU"), Is.EqualTo("ru"));
            Assert.That(CliLanguage.FromLocale("en_GB.UTF-8"), Is.EqualTo("en"));
            Assert.That(CliLanguage.FromLocale("C"), Is.EqualTo("en"));
            Assert.That(CliLanguage.FromLocale(null), Is.EqualTo("en"));
        }
    }
}
