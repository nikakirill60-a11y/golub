using System;
using System.Globalization;
using System.IO;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Sdk
{
    /// <summary>
    /// <c>rempack pack &lt;folder&gt;</c>: packs a mod folder into a <c>.rempack</c>. The
    /// package is written to a temporary file beside its destination and checked there;
    /// only an accepted package replaces the destination, so a refused or interrupted run
    /// never leaves a broken or half-written package behind.
    /// </summary>
    internal static class PackCommand
    {
        public static int Run(CliContext context, string folder)
        {
            if (!Directory.Exists(folder))
            {
                return context.UsageError(File.Exists(folder) ? CliKeys.NotAFolder : CliKeys.NotFound, folder);
            }

            context.Say(CliKeys.Packing, folder);
            var source = SourceFolder.Scan(folder);
            var report = new RempackReport();
            report.AddRange(source.Report);
            var document = context.Document(CliOptions.Pack);
            document.Set("path", source.Root);
            string output = null;
            PackageFacts facts = null;
            if (source.CanPack)
            {
                output = OutputPath(context.Options.Output, source);
                try
                {
                    facts = Write(context, source, output, report);
                }
                catch (Exception error) when (SourceFolder.IsFileError(error))
                {
                    return context.Failure(CliKeys.WriteFailed, output, error.Message);
                }
            }

            Listed(context, source);
            ReportOutput.Print(context, report);
            var exit = ReportOutput.Verdict(context, report);
            if (facts != null)
            {
                context.Say(CliKeys.Written, output, context.Texts.Size(facts.Size),
                    facts.Files.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                context.Say(CliKeys.NotWritten);
            }

            if (context.Json)
            {
                document.Set("output", output != null ? JsonNode.Str(output) : JsonNode.Null());
                document.Set("written", facts != null);
                if (facts != null)
                {
                    document.Set("size", facts.Size);
                    document.Set("unpacked", facts.Unpacked);
                    document.Set("files", facts.Files);
                }

                document.Set("skipped", Strings(source.Skipped));
                document.Set("links", Strings(source.Links));
                ReportOutput.Describe(document, context, report);
                context.Emit(document);
            }

            return exit;
        }

        /// <summary>
        /// Where the package goes: <c>-o</c> as given (a folder takes the default name), else
        /// <c>&lt;id&gt;-&lt;version&gt;.rempack</c> beside the mod folder.
        /// </summary>
        internal static string OutputPath(string option, SourceFolder source)
        {
            var name = DefaultName(source);
            if (string.IsNullOrEmpty(option))
            {
                var parent = Path.GetDirectoryName(Trimmed(source.Root));
                return Path.Combine(string.IsNullOrEmpty(parent) ? Directory.GetCurrentDirectory() : parent, name);
            }

            var full = Path.GetFullPath(option);
            return Directory.Exists(full) ? Path.Combine(full, name) : full;
        }

        internal static string DefaultName(SourceFolder source)
        {
            var id = source.Manifest == null ? string.Empty : source.Manifest["id"].AsString();
            var version = source.Manifest == null ? string.Empty : source.Manifest["version"].AsString();
            var stem = RempackIds.IsModId(id) ? id : Path.GetFileName(Trimmed(source.Root));
            if (string.IsNullOrEmpty(stem))
            {
                stem = RempackFormat.Name;
            }

            SemanticVersion parsed;
            if (SemanticVersion.TryParse(version, out parsed))
            {
                stem += "-" + parsed;
            }

            return stem + RempackFormat.Extension;
        }

        private static PackageFacts Write(CliContext context, SourceFolder source, string output, RempackReport report)
        {
            var folder = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            string temp;
            var stream = TempFiles.Create(string.IsNullOrEmpty(folder) ? "." : folder, Path.GetFileName(output) + ".", false, out temp);
            var kept = false;
            try
            {
                PackageFacts facts = null;
                using (stream)
                {
                    if (Packer.Build(source, stream, report))
                    {
                        facts = Packer.Check(stream, context.Validation, report);
                    }
                }

                if (facts == null || ReportOutput.Refuses(report, context.Options.Strict))
                {
                    return null;
                }

                File.Move(temp, output, true);
                kept = true;
                return facts;
            }
            finally
            {
                if (kept)
                {
                    TempFiles.Keep(temp);
                }
                else
                {
                    TempFiles.Delete(temp);
                }
            }
        }

        /// <summary>What did not go into the package: links always, ignored files with <c>-v</c>.</summary>
        internal static void Listed(CliContext context, SourceFolder source)
        {
            foreach (var link in source.Links)
            {
                context.Say(ConsoleColor.Yellow, CliKeys.SkippedLink, link);
            }

            if (context.Verbose)
            {
                foreach (var skipped in source.Skipped)
                {
                    context.Say(CliKeys.Skipped, skipped);
                }
            }
        }

        internal static JsonNode Strings(System.Collections.Generic.IEnumerable<string> values)
        {
            var array = JsonNode.NewArray();
            foreach (var value in values)
            {
                array.Add(JsonNode.Str(value));
            }

            return array;
        }

        private static string Trimmed(string path)
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length == 0 ? path : trimmed;
        }
    }
}
