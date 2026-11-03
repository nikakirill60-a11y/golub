using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The texts of package issues live in lang.json, shared by the MODS screen and the
    /// SDK CLI: every code needs both languages and the same placeholders in each.
    /// </summary>
    public sealed class RempackMessagesTests
    {
        private static readonly Regex Placeholder = new Regex("\\{[0-9]\\}");
        private static JsonNode _table;

        private static JsonNode Table()
        {
            if (_table == null)
            {
                var path = LangPath();
                Assert.That(path, Is.Not.Null, "Resources/Data/lang.json was not found");
                _table = JsonNode.Parse(File.ReadAllText(path));
            }

            return _table;
        }

        /// <summary>Next to this source file, or under the project root the Unity runner starts in.</summary>
        private static string LangPath([CallerFilePath] string source = "")
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(source))
            {
                candidates.Add(Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, "..", "Resources", "Data", "lang.json"));
            }

            candidates.Add(Path.Combine("Assets", "Blitz", "Resources", "Data", "lang.json"));
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<string> TextKeys()
        {
            return RempackCodes.All.Select(RempackMessages.Key)
                .Concat(new[] { RempackSeverity.Error, RempackSeverity.Warning, RempackSeverity.Info }.Select(RempackMessages.SeverityKey));
        }

        [Test]
        public void Codes_AreUniqueAndMapToDistinctKeys()
        {
            var codes = RempackCodes.All.ToList();

            Assert.That(codes.Count, Is.GreaterThan(70));
            Assert.That(codes.Distinct().Count(), Is.EqualTo(codes.Count));
            Assert.That(codes.Select(RempackMessages.Key).Distinct().Count(), Is.EqualTo(codes.Count));
            Assert.That(codes.All(c => Regex.IsMatch(c, "^[a-z0-9_]+\\.[a-z0-9_]+$")), Is.True);
        }

        [Test]
        public void EveryCode_HasEnglishAndRussianText()
        {
            var keys = Table()["keys"];
            var missing = new List<string>();
            foreach (var key in TextKeys())
            {
                foreach (var language in new[] { "en", "ru" })
                {
                    if (string.IsNullOrEmpty(keys[key][language].AsString()))
                    {
                        missing.Add(key + " [" + language + "]");
                    }
                }
            }

            Assert.That(missing, Is.Empty);
        }

        [Test]
        public void EveryPackageText_BelongsToACode()
        {
            var known = new HashSet<string>(TextKeys());
            var orphans = Table()["keys"].Fields.Select(p => p.Key)
                .Where(k => k.StartsWith(RempackMessages.KeyPrefix, StringComparison.Ordinal) && !known.Contains(k)).ToList();

            Assert.That(orphans, Is.Empty);
        }

        [Test]
        public void BothLanguages_UseTheSamePlaceholders()
        {
            var keys = Table()["keys"];
            var different = new List<string>();
            foreach (var key in TextKeys())
            {
                var english = Placeholders(keys[key]["en"].AsString());
                var russian = Placeholders(keys[key]["ru"].AsString());
                if (!english.SetEquals(russian))
                {
                    different.Add(key);
                }
            }

            Assert.That(different, Is.Empty);
        }

        [Test]
        public void Fill_ReplacesPlaceholdersInOnePass()
        {
            Assert.That(RempackMessages.Fill("{0} needs {1}", new[] { "{1}", "x" }), Is.EqualTo("{1} needs x"), "an argument cannot inject a placeholder");
            Assert.That(RempackMessages.Fill("{0}/{2}", new[] { "a" }), Is.EqualTo("a/"), "a missing argument is empty");
            Assert.That(RempackMessages.Fill("{a} {} {10} {0", new[] { "z" }), Is.EqualTo("{a} {} {10} {0"), "only {digit} is a placeholder");
            Assert.That(RempackMessages.Fill("{0}", null), Is.EqualTo(string.Empty));
            Assert.That(RempackMessages.Fill("{0}", new string[] { null }), Is.EqualTo(string.Empty));
        }

        [Test]
        public void Format_FallsBackToTheCodeWithoutAText()
        {
            Assert.That(RempackMessages.Format("file.missing", new[] { "a", "b" }, null), Is.EqualTo("file.missing (a, b)"));
            Assert.That(RempackMessages.Format("file.missing", new string[0], key => key), Is.EqualTo("file.missing"));
            Assert.That(RempackMessages.Format("file.missing", new[] { "x" }, key => "Missing: {0}"), Is.EqualTo("Missing: x"));
            Assert.That(RempackMessages.Key("zip.multi_disk"), Is.EqualTo("rempack_zip_multi_disk"));
        }

        [Test]
        public void FromTable_AnswersInTheLanguageOrInEnglish()
        {
            var table = JsonNode.Parse("{\"keys\": {\"rempack_file_missing\": {\"en\": \"Missing {0}\", \"ru\": \"\u041d\u0435\u0442 {0}\"}}}");

            Assert.That(RempackMessages.FromTable(table, "ru")("rempack_file_missing"), Is.EqualTo("\u041d\u0435\u0442 {0}"));
            Assert.That(RempackMessages.FromTable(table, "de")("rempack_file_missing"), Is.EqualTo("Missing {0}"));
            Assert.That(RempackMessages.FromTable(table, null)("rempack_file_missing"), Is.EqualTo("Missing {0}"));
            Assert.That(RempackMessages.FromTable(table, "ru")("rempack_unknown"), Is.Null);
            Assert.That(RempackMessages.FromTable(null, "ru")("rempack_file_missing"), Is.Null);
        }

        [Test]
        public void ReportFormat_ListsErrorsFirstWithTheSeverityWord()
        {
            var report = new RempackReport();
            report.Warning(RempackCodes.FileUnused, "notes.txt");
            report.Error(RempackCodes.FileMissing, "tanks/t1/tank.json");
            report.Info(RempackCodes.InstallReinstall, string.Empty, "1.0.0");
            var lookup = RempackMessages.FromTable(Table(), "ru");
            var lines = report.Format(lookup).TrimEnd('\n').Split('\n');

            Assert.That(lines.Length, Is.EqualTo(3));
            Assert.That(lines[0], Does.StartWith(lookup(RempackMessages.SeverityKey(RempackSeverity.Error)) + ": tanks/t1/tank.json: "));
            Assert.That(lines[1], Does.StartWith(lookup(RempackMessages.SeverityKey(RempackSeverity.Warning)) + ": notes.txt: "));
            Assert.That(lines[2], Does.Contain("1.0.0"));
            Assert.That(report.Format(null).Split('\n')[0], Is.EqualTo("error: tanks/t1/tank.json: file.missing"));
        }

        [Test]
        public void IssueToString_IsTheEnglishLogLine()
        {
            var issue = new RempackIssue(RempackSeverity.Error, RempackCodes.ZipInvalid, "a.rempack", "x").WithDetail("bad header");

            Assert.That(issue.ToString(), Is.EqualTo("error zip.invalid [a.rempack] (x) - bad header"));
        }

        private static HashSet<string> Placeholders(string text)
        {
            return new HashSet<string>(Placeholder.Matches(text ?? string.Empty).Cast<Match>().Select(m => m.Value));
        }
    }
}
