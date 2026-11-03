using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Sdk
{
    /// <summary>
    /// <c>rempack inspect &lt;file&gt;</c>: what a package is and holds (the facts the MODS
    /// screen shows, plus dependencies, replaced stock content and sizes), then the check
    /// report. <c>--files</c> lists every entry.
    /// </summary>
    internal static class InspectCommand
    {
        public static int Run(CliContext context, string path)
        {
            if (!File.Exists(path))
            {
                return context.UsageError(Directory.Exists(path) ? CliKeys.NotAFile : CliKeys.NotFound, path);
            }

            var full = Path.GetFullPath(path);
            var report = new RempackReport();
            var document = context.Document(CliOptions.Inspect);
            document.Set("path", full);
            using (var package = RempackPackage.Open(full, report))
            {
                if (package == null)
                {
                    Rows(context, new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>(CliKeys.LabelFile, full),
                    });
                    document.Set("manifest", JsonNode.Null());
                }
                else
                {
                    Rows(context, Describe(context, package, full));
                    if (context.Options.Files)
                    {
                        Files(context, package);
                    }

                    document.Set("manifest", package.Manifest.ToJson());
                    var sizes = JsonNode.NewObject();
                    sizes.Set("archive", package.Size);
                    sizes.Set("unpacked", package.Zip.UnpackedSize);
                    sizes.Set("files", package.Zip.Entries.Count);
                    document.Set("sizes", sizes);
                    document.Set("entries", Entries(package));
                    RempackValidator.Validate(package, context.Validation, report);
                }
            }

            context.Line(string.Empty);
            ReportOutput.Print(context, report);
            var exit = ReportOutput.Verdict(context, report);
            ReportOutput.Describe(document, context, report);
            context.Emit(document);
            return exit;
        }

        private static List<KeyValuePair<string, string>> Describe(CliContext context, RempackPackage package, string path)
        {
            var texts = context.Texts;
            var manifest = package.Manifest;
            var none = texts.Get(CliKeys.None);
            var rows = new List<KeyValuePair<string, string>>();
            Action<string, string> row = delegate(string key, string value)
            {
                rows.Add(new KeyValuePair<string, string>(key, string.IsNullOrEmpty(value) ? none : value));
            };

            row(CliKeys.LabelFile, path);
            row(CliKeys.LabelId, manifest.Id);
            row(CliKeys.LabelVersion, manifest.Version.ToString());
            row(CliKeys.LabelName, manifest.DisplayName(texts.Language));
            var description = manifest.Description.Get(texts.Language);
            if (description.Length > 0)
            {
                row(CliKeys.LabelDescription, description);
            }

            row(CliKeys.LabelAuthors, string.Join(", ", manifest.Authors));
            row(CliKeys.LabelLicense, manifest.License);
            if (manifest.Homepage.Length > 0)
            {
                row(CliKeys.LabelHomepage, manifest.Homepage);
            }

            var game = manifest.GameMinVersion != null
                ? texts.Get(CliKeys.GameFrom, manifest.GameMinVersion.ToString())
                : texts.Get(CliKeys.GameAny);
            row(CliKeys.LabelGame, game + "; " + (manifest.Features.Count > 0 ? string.Join(", ", manifest.Features) : none));
            row(CliKeys.LabelRequires, References(manifest.Dependencies));
            row(CliKeys.LabelConflicts, References(manifest.Conflicts));
            var content = new StringBuilder();
            foreach (var kind in RempackFormat.Kinds)
            {
                var ids = manifest.Content(kind);
                if (ids.Count == 0)
                {
                    continue;
                }

                if (content.Length > 0)
                {
                    content.Append('\n');
                }

                content.Append(texts.Get(CliKeys.Kind(kind))).Append(" (")
                    .Append(ids.Count.ToString(CultureInfo.InvariantCulture)).Append("): ").Append(string.Join(", ", ids));
            }

            row(CliKeys.LabelContent, content.ToString());
            var overrides = new List<string>();
            foreach (var reference in manifest.Overrides)
            {
                overrides.Add(reference.ToString());
            }

            row(CliKeys.LabelReplaces, string.Join(", ", overrides));
            row(CliKeys.LabelSize, texts.Get(CliKeys.SizeLine, texts.Size(package.Size), texts.Size(package.Zip.UnpackedSize),
                package.Zip.Entries.Count.ToString(CultureInfo.InvariantCulture)));
            return rows;
        }

        /// <summary>Label and value columns; a value of several lines keeps to its column.</summary>
        private static void Rows(CliContext context, List<KeyValuePair<string, string>> rows)
        {
            var width = 0;
            foreach (var pair in rows)
            {
                width = Math.Max(width, context.Texts.Get(pair.Key).Length + 1);
            }

            var indent = new string(' ', width + 2);
            foreach (var pair in rows)
            {
                var label = (context.Texts.Get(pair.Key) + ":").PadRight(width + 2);
                var lines = pair.Value.Replace("\r\n", "\n").Split('\n');
                context.Line(label + lines[0]);
                for (var i = 1; i < lines.Length; i++)
                {
                    context.Line(indent + lines[i]);
                }
            }
        }

        private static void Files(CliContext context, RempackPackage package)
        {
            var texts = context.Texts;
            context.Line(string.Empty);
            context.Say(CliKeys.FilesHeader);
            var entries = new List<RempackZipEntry>(package.Zip.Entries);
            entries.Sort(delegate(RempackZipEntry a, RempackZipEntry b) { return string.CompareOrdinal(a.Path, b.Path); });
            foreach (var entry in entries)
            {
                context.Line("  " + texts.Size(entry.Size).PadLeft(9) + "  " + texts.Size(entry.CompressedSize).PadLeft(9) + "  " + entry.Path);
            }
        }

        private static JsonNode Entries(RempackPackage package)
        {
            var entries = new List<RempackZipEntry>(package.Zip.Entries);
            entries.Sort(delegate(RempackZipEntry a, RempackZipEntry b) { return string.CompareOrdinal(a.Path, b.Path); });
            var array = JsonNode.NewArray();
            foreach (var entry in entries)
            {
                var node = JsonNode.NewObject();
                node.Set("path", entry.Path);
                node.Set("size", entry.Size);
                node.Set("compressedSize", entry.CompressedSize);
                node.Set("method", entry.Method == 0 ? "store" : "deflate");
                string hash;
                node.Set("sha256", package.Manifest.Files.TryGetValue(entry.Path, out hash) ? JsonNode.Str(hash) : JsonNode.Null());
                array.Add(node);
            }

            return array;
        }

        private static string References(List<RempackModReference> references)
        {
            var parts = new List<string>();
            foreach (var reference in references)
            {
                parts.Add(reference.Id + " " + reference.Range.Text);
            }

            return string.Join(", ", parts);
        }
    }
}
