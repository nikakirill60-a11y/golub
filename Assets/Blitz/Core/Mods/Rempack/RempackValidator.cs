using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Mods.Rempack
{
    /// <summary>Checks one kind of content (tank, hangar, map) beyond its definition being a JSON object.</summary>
    public interface IRempackContentChecker
    {
        RempackContentKind Kind { get; }

        void Check(RempackContentContext context);
    }

    /// <summary>What a content checker sees: the package, its definition and the report.</summary>
    public sealed class RempackContentContext
    {
        private readonly HashSet<string> _used;

        internal RempackContentContext(RempackPackage package, RempackReport report, RempackValidationOptions options,
            RempackContentKind kind, string localId, JsonNode definition, HashSet<string> used)
        {
            Package = package;
            Report = report;
            Options = options;
            Kind = kind;
            LocalId = localId;
            Definition = definition;
            _used = used;
        }

        public RempackPackage Package { get; private set; }
        public RempackReport Report { get; private set; }
        public RempackValidationOptions Options { get; private set; }
        public RempackContentKind Kind { get; private set; }
        public string LocalId { get; private set; }
        public JsonNode Definition { get; private set; }

        public string DefinitionPath
        {
            get { return RempackFormat.DefinitionPath(Kind, LocalId); }
        }

        /// <summary>
        /// A file the definition refers to, relative to the definition's folder. Reports a
        /// bad or missing reference under <paramref name="field"/>; returns the package path
        /// (marked as used) or null.
        /// </summary>
        public string Reference(string field, string relative)
        {
            var path = RempackPaths.Resolve(DefinitionPath, relative);
            if (path == null)
            {
                Report.Error(RempackCodes.PathInvalid, DefinitionPath + " " + field);
                return null;
            }

            if (!Package.Contains(path))
            {
                Report.Error(RempackCodes.ReferenceMissing, DefinitionPath, field, path);
                return null;
            }

            Use(path);
            return path;
        }

        public void Use(string path)
        {
            _used.Add(path);
        }
    }

    public sealed class RempackValidationOptions
    {
        public RempackFeatureSet Features = RempackFeatureSet.Game;
        public SemanticVersion GameVersion = SemanticVersion.FromGameVersion(BlitzVersion.Current);

        /// <summary>
        /// Hash every file against the manifest. Installs always do; the library skips it
        /// for a file it already verified and that has not changed since.
        /// </summary>
        public bool VerifyHashes = true;

        public readonly List<IRempackContentChecker> Checkers = new List<IRempackContentChecker>();

        /// <summary>The same options with the same checkers; for one pass that hashes differently.</summary>
        public RempackValidationOptions Copy(bool verifyHashes)
        {
            var copy = new RempackValidationOptions { Features = Features, GameVersion = GameVersion, VerifyHashes = verifyHashes };
            copy.Checkers.AddRange(Checkers);
            return copy;
        }

        public IRempackContentChecker Checker(RempackContentKind kind)
        {
            for (var i = 0; i < Checkers.Count; i++)
            {
                if (Checkers[i].Kind == kind)
                {
                    return Checkers[i];
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The checks a package passes before the game installs or loads it, and the very
    /// checks the SDK CLI runs before it ships one: container limits, manifest, the game's
    /// features and version, the file list and its SHA-256, icons, string tables and
    /// content definitions. Everything found goes to one report; nothing throws.
    /// </summary>
    public static class RempackValidator
    {
        public const string LocaleFolder = "locale";
        public const string DefaultIcon = "icon.png";
        public const int LocaleKeyLength = 64;
        public const int LocaleKeys = 4096;

        private static readonly string[] Documents = { "LICENSE", "LICENCE", "README", "CHANGELOG", "CREDITS", "NOTICE" };

        public static RempackReport ValidateFile(string path, RempackValidationOptions options)
        {
            var report = new RempackReport();
            using (var package = RempackPackage.Open(path, report))
            {
                if (package != null)
                {
                    Validate(package, options, report);
                }
            }

            return report;
        }

        public static void Validate(RempackPackage package, RempackValidationOptions options, RempackReport report)
        {
            options = options ?? new RempackValidationOptions();
            var manifest = package.Manifest;
            if (manifest.FormatVersion > RempackFormat.FormatVersion)
            {
                // Parse already said "needs a newer game"; the rest of the layout is unknown.
                return;
            }

            if (package.Size > RempackLimits.ArchiveSoftBytes)
            {
                report.Warning(RempackCodes.ArchiveHeavy, string.Empty);
            }

            CheckGame(manifest, options, report);
            CheckFiles(package, options, report);
            var used = new HashSet<string>(StringComparer.Ordinal);
            CheckImages(package, report, used);
            CheckLocales(package, report, used);
            CheckContent(package, options, report, used);
            CheckUnused(package, options, report, used);
        }

        /// <summary>The icon a package shows: the manifest's, or <c>icon.png</c> at the root.</summary>
        public static string IconPath(RempackPackage package)
        {
            if (package.Manifest.Icon.Length > 0)
            {
                return package.Manifest.Icon;
            }

            return package.Contains(DefaultIcon) ? DefaultIcon : string.Empty;
        }

        private static void CheckGame(RempackManifest manifest, RempackValidationOptions options, RempackReport report)
        {
            foreach (var feature in manifest.Features)
            {
                if (!options.Features.Provides(feature))
                {
                    report.Error(RempackCodes.FeatureUnsupported, RempackFormat.ManifestFile, feature);
                }
            }

            if (manifest.GameMinVersion != null && options.GameVersion != null && manifest.GameMinVersion > options.GameVersion)
            {
                report.Error(RempackCodes.GameTooOld, RempackFormat.ManifestFile, manifest.GameMinVersion.ToString(),
                    options.GameVersion.ToString());
            }
        }

        private static void CheckFiles(RempackPackage package, RempackValidationOptions options, RempackReport report)
        {
            var listed = package.Manifest.Files;
            foreach (var entry in package.Zip.Entries)
            {
                if (entry.Path == RempackFormat.ManifestFile)
                {
                    continue;
                }

                if (!listed.ContainsKey(entry.Path))
                {
                    report.Error(RempackCodes.FileUnlisted, entry.Path);
                }
            }

            foreach (var pair in listed)
            {
                var entry = package.Entry(pair.Key);
                if (entry == null)
                {
                    report.Error(RempackCodes.FileMissing, pair.Key);
                    continue;
                }

                if (!options.VerifyHashes)
                {
                    continue;
                }

                try
                {
                    if (package.Zip.Sha256(entry) != pair.Value)
                    {
                        report.Error(RempackCodes.FileHashMismatch, pair.Key);
                    }
                }
                catch (RempackException error)
                {
                    report.Add(error.Issue);
                }
                catch (Exception error)
                {
                    report.Add(new RempackIssue(RempackSeverity.Error, RempackCodes.FileUnreadable, pair.Key).WithDetail(error.Message));
                }
            }
        }

        private static void CheckImages(RempackPackage package, RempackReport report, HashSet<string> used)
        {
            var icon = IconPath(package);
            if (icon.Length > 0)
            {
                CheckImage(package, "icon", icon, RempackLimits.IconDimension, report, used);
            }

            for (var i = 0; i < package.Manifest.Previews.Count; i++)
            {
                CheckImage(package, RempackJson.Index("previews", i), package.Manifest.Previews[i], RempackLimits.TextureDimension, report, used);
            }
        }

        private static void CheckImage(RempackPackage package, string field, string path, int maxDimension, RempackReport report, HashSet<string> used)
        {
            if (!package.Contains(path))
            {
                report.Error(RempackCodes.ReferenceMissing, RempackFormat.ManifestFile, field, path);
                return;
            }

            used.Add(path);
            try
            {
                RempackImageFormat format;
                int width, height;
                var data = package.ReadBytes(path, RempackLimits.ImageBytes);
                if (!RempackImage.TryProbe(data, out format, out width, out height) || width > maxDimension || height > maxDimension)
                {
                    report.Error(RempackCodes.ImageInvalid, path, maxDimension.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (RempackException error)
            {
                report.Add(error.Issue);
            }
        }

        /// <summary><c>locale/&lt;language&gt;.json</c>: flat string tables; the game prefixes their keys with the mod id.</summary>
        private static void CheckLocales(RempackPackage package, RempackReport report, HashSet<string> used)
        {
            foreach (var entry in package.Zip.Entries)
            {
                var path = entry.Path;
                if (!path.StartsWith(LocaleFolder + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                used.Add(path);
                var name = path.Substring(LocaleFolder.Length + 1);
                if (!name.EndsWith(".json", StringComparison.Ordinal) || !LocalizedText.IsLanguage(name.Substring(0, name.Length - 5)))
                {
                    report.Error(RempackCodes.LocaleLanguage, path);
                    continue;
                }

                var table = package.ReadJson(path, report);
                if (table == null)
                {
                    continue;
                }

                if (table.Kind != JsonKind.Object)
                {
                    report.Error(RempackCodes.FieldNotObject, path, "(root)");
                    continue;
                }

                if (table.Count > LocaleKeys)
                {
                    report.Error(RempackCodes.FieldCount, path, "(root)", table.Count.ToString(CultureInfo.InvariantCulture),
                        LocaleKeys.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                foreach (var pair in table.Fields)
                {
                    if (!IsLocaleKey(pair.Key))
                    {
                        report.Error(RempackCodes.LocaleKey, path, pair.Key);
                        continue;
                    }

                    if (pair.Value.Kind != JsonKind.String)
                    {
                        report.Error(RempackCodes.FieldNotString, path, pair.Key);
                        continue;
                    }

                    var value = pair.Value.AsString();
                    if (value.Length > RempackLimits.DescriptionLength)
                    {
                        report.Error(RempackCodes.TextTooLong, path, pair.Key, RempackLimits.DescriptionLength.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (HasControl(value))
                    {
                        report.Error(RempackCodes.TextControl, path, pair.Key);
                    }
                }
            }
        }

        private static void CheckContent(RempackPackage package, RempackValidationOptions options, RempackReport report, HashSet<string> used)
        {
            foreach (var kind in RempackFormat.Kinds)
            {
                var checker = options.Checker(kind);
                foreach (var id in package.Manifest.Content(kind))
                {
                    var definitionPath = RempackFormat.DefinitionPath(kind, id);
                    if (!package.Contains(definitionPath))
                    {
                        report.Error(RempackCodes.ContentMissing, RempackFormat.ManifestFile, id, definitionPath);
                        continue;
                    }

                    used.Add(definitionPath);
                    var definition = package.ReadJson(definitionPath, report);
                    if (definition == null)
                    {
                        continue;
                    }

                    if (definition.Kind != JsonKind.Object)
                    {
                        report.Error(RempackCodes.FieldNotObject, definitionPath, "(root)");
                        continue;
                    }

                    if (checker == null)
                    {
                        // Nothing knows this content's files yet: all of its folder counts as used.
                        MarkFolder(package, RempackFormat.Folder(kind) + "/" + id + "/", used);
                        continue;
                    }

                    try
                    {
                        checker.Check(new RempackContentContext(package, report, options, kind, id, definition, used));
                    }
                    catch (RempackException error)
                    {
                        report.Add(error.Issue);
                    }
                }
            }
        }

        private static void CheckUnused(RempackPackage package, RempackValidationOptions options, RempackReport report, HashSet<string> used)
        {
            foreach (var pair in package.Manifest.Files)
            {
                if (!used.Contains(pair.Key) && !IsDocument(pair.Key) && package.Contains(pair.Key))
                {
                    report.Warning(RempackCodes.FileUnused, pair.Key);
                }
            }
        }

        private static void MarkFolder(RempackPackage package, string prefix, HashSet<string> used)
        {
            foreach (var entry in package.Zip.Entries)
            {
                if (entry.Path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    used.Add(entry.Path);
                }
            }
        }

        /// <summary>Licence, readme and change log files at the root travel with a package unused.</summary>
        public static bool IsDocument(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('/') >= 0)
            {
                return false;
            }

            var dot = path.IndexOf('.');
            var stem = (dot < 0 ? path : path.Substring(0, dot)).ToUpperInvariant();
            var extension = dot < 0 ? string.Empty : path.Substring(dot).ToLowerInvariant();
            return Array.IndexOf(Documents, stem) >= 0 && (extension.Length == 0 || extension == ".txt" || extension == ".md");
        }

        /// <summary>Lowercase Latin letters, digits, '_' and '.', starting with a letter.</summary>
        public static bool IsLocaleKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > LocaleKeyLength || key[0] < 'a' || key[0] > 'z')
            {
                return false;
            }

            foreach (var c in key)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Control characters other than line breaks and tabs.</summary>
        public static bool HasControl(string text)
        {
            foreach (var c in text)
            {
                if ((c < ' ' && c != '\n' && c != '\t' && c != '\r') || (c >= '\u007f' && c < '\u00a0') || c == '\u2028' || c == '\u2029'
                    || (c >= '\u202a' && c <= '\u202e') || (c >= '\u2066' && c <= '\u2069'))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
