using System;
using System.Globalization;
using BlitzLegacy.Core;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Sdk
{
    /// <summary>
    /// A check report the way the tool shows it: every issue worded as the MODS screen
    /// words it, errors first, then the counts and the verdict; or the same as JSON.
    /// </summary>
    internal static class ReportOutput
    {
        private static readonly RempackSeverity[] Order = { RempackSeverity.Error, RempackSeverity.Warning, RempackSeverity.Info };

        public static void Print(CliContext context, RempackReport report)
        {
            foreach (var severity in Order)
            {
                var word = Word(context, severity);
                foreach (var issue in report.Issues)
                {
                    if (issue.Severity != severity)
                    {
                        continue;
                    }

                    context.Write(Color(severity), word + ":");
                    var line = " " + issue.Format(context.Texts.Lookup);
                    if (context.Verbose)
                    {
                        line += " [" + issue.Code + "]";
                    }

                    context.Line(line);
                    if (context.Verbose && !string.IsNullOrEmpty(issue.Detail))
                    {
                        context.Line("    " + issue.Detail);
                    }
                }
            }

            if (report.Dropped > 0)
            {
                context.Say(CliKeys.Dropped, report.Dropped.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Errors refuse a package; with <c>--strict</c> warnings do as well.</summary>
        public static bool Refuses(RempackReport report, bool strict)
        {
            return report.HasErrors || (strict && report.Count(RempackSeverity.Warning) > 0);
        }

        /// <summary>The counts and the verdict; returns the exit code.</summary>
        public static int Verdict(CliContext context, RempackReport report)
        {
            context.Say(CliKeys.Counts,
                report.Count(RempackSeverity.Error).ToString(CultureInfo.InvariantCulture),
                report.Count(RempackSeverity.Warning).ToString(CultureInfo.InvariantCulture),
                report.Count(RempackSeverity.Info).ToString(CultureInfo.InvariantCulture));
            if (report.HasErrors)
            {
                if (NeedsNewerGame(report))
                {
                    context.Say(CliKeys.NeedsNewerGame, BlitzVersion.Current);
                }

                context.Say(ConsoleColor.Red, CliKeys.Refused);
                return Cli.RefusedExit;
            }

            if (Refuses(report, context.Options.Strict))
            {
                context.Say(ConsoleColor.Red, CliKeys.RefusedStrict);
                return Cli.RefusedExit;
            }

            context.Say(ConsoleColor.Green, IsHeavy(report) ? CliKeys.AcceptedHeavy : CliKeys.Accepted);
            return Cli.AcceptedExit;
        }

        /// <summary><c>accepted</c>, <c>heavy</c> or <c>refused</c>: the verdict for scripts.</summary>
        public static string Status(RempackReport report, bool strict)
        {
            if (Refuses(report, strict))
            {
                return "refused";
            }

            return IsHeavy(report) ? "heavy" : "accepted";
        }

        /// <summary>The verdict, the counts and every issue with its localized text, for <c>--json</c>.</summary>
        public static void Describe(JsonNode document, CliContext context, RempackReport report)
        {
            var strict = context.Options.Strict;
            document.Set("ok", !Refuses(report, strict));
            document.Set("status", Status(report, strict));
            document.Set("strict", strict);
            document.Set("needsNewerGame", report.HasErrors && NeedsNewerGame(report));
            var counts = JsonNode.NewObject();
            counts.Set("errors", report.Count(RempackSeverity.Error));
            counts.Set("warnings", report.Count(RempackSeverity.Warning));
            counts.Set("notes", report.Count(RempackSeverity.Info));
            document.Set("counts", counts);
            document.Set("dropped", report.Dropped);
            var issues = JsonNode.NewArray();
            foreach (var severity in Order)
            {
                foreach (var issue in report.Issues)
                {
                    if (issue.Severity == severity)
                    {
                        issues.Add(Issue(context, issue));
                    }
                }
            }

            document.Set("issues", issues);
        }

        private static JsonNode Issue(CliContext context, RempackIssue issue)
        {
            var node = JsonNode.NewObject();
            node.Set("severity", issue.Severity.ToString().ToLowerInvariant());
            node.Set("code", issue.Code);
            node.Set("path", issue.Path);
            var args = JsonNode.NewArray();
            foreach (var arg in issue.Args)
            {
                args.Add(JsonNode.Str(arg ?? string.Empty));
            }

            node.Set("args", args);
            node.Set("message", RempackMessages.Format(issue.Code, issue.Args, context.Texts.Lookup));
            if (!string.IsNullOrEmpty(issue.Detail))
            {
                node.Set("detail", issue.Detail);
            }

            return node;
        }

        /// <summary>Every error is one an update of the game fixes: the package itself is fine.</summary>
        private static bool NeedsNewerGame(RempackReport report)
        {
            var errors = 0;
            foreach (var issue in report.Issues)
            {
                if (issue.Severity != RempackSeverity.Error)
                {
                    continue;
                }

                if (!RempackCodes.NeedsNewerGame(issue.Code))
                {
                    return false;
                }

                errors++;
            }

            return errors > 0;
        }

        private static bool IsHeavy(RempackReport report)
        {
            foreach (var issue in report.Issues)
            {
                if (RempackCodes.IsHeavy(issue.Code))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Word(CliContext context, RempackSeverity severity)
        {
            var key = RempackMessages.SeverityKey(severity);
            var word = context.Texts.Lookup(key);
            return string.IsNullOrEmpty(word) ? severity.ToString() : word;
        }

        private static ConsoleColor Color(RempackSeverity severity)
        {
            switch (severity)
            {
                case RempackSeverity.Error: return ConsoleColor.Red;
                case RempackSeverity.Warning: return ConsoleColor.Yellow;
                default: return ConsoleColor.Cyan;
            }
        }
    }
}
