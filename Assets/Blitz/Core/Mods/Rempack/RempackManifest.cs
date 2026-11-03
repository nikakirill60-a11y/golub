using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Mods.Rempack
{
    /// <summary>A dependency or a conflict: another mod id and the versions it covers.</summary>
    public sealed class RempackModReference
    {
        public RempackModReference(string id, VersionRange range)
        {
            Id = id;
            Range = range ?? VersionRange.Any;
        }

        public string Id { get; private set; }
        public VersionRange Range { get; private set; }
    }

    /// <summary>
    /// <c>rempack.json</c>: who made the mod, which version it is, what it needs from
    /// the game and other mods, what it adds or replaces and the SHA-256 of every file.
    /// </summary>
    public sealed class RempackManifest
    {
        private static readonly string[] TopLevel =
        {
            "format", "formatVersion", "id", "version", "name", "description", "authors", "license",
            "homepage", "icon", "previews", "game", "dependencies", "conflicts", "content", "overrides",
            "files",
        };

        public int FormatVersion = RempackFormat.FormatVersion;
        public string Id = string.Empty;
        public SemanticVersion Version = new SemanticVersion(1, 0, 0);
        public LocalizedText Name = new LocalizedText();
        public LocalizedText Description = new LocalizedText();
        public readonly List<string> Authors = new List<string>();
        public string License = string.Empty;
        public string Homepage = string.Empty;
        public string Icon = string.Empty;
        public readonly List<string> Previews = new List<string>();
        public SemanticVersion GameMinVersion;
        public readonly List<string> Features = new List<string>();
        public readonly List<RempackModReference> Dependencies = new List<RempackModReference>();
        public readonly List<RempackModReference> Conflicts = new List<RempackModReference>();
        public readonly List<RempackStockRef> Overrides = new List<RempackStockRef>();

        /// <summary>Package path to lowercase hex SHA-256.</summary>
        public readonly SortedDictionary<string, string> Files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        private readonly Dictionary<RempackContentKind, List<string>> _content = new Dictionary<RempackContentKind, List<string>>
        {
            { RempackContentKind.Tank, new List<string>() },
            { RempackContentKind.Hangar, new List<string>() },
            { RempackContentKind.Map, new List<string>() },
        };

        public List<string> Content(RempackContentKind kind)
        {
            return _content[kind];
        }

        public int ContentCount
        {
            get { return _content[RempackContentKind.Tank].Count + _content[RempackContentKind.Hangar].Count + _content[RempackContentKind.Map].Count; }
        }

        /// <summary>A package that replaces stock content turns online play off while it is on.</summary>
        public bool ReplacesStock
        {
            get { return Overrides.Count > 0; }
        }

        public string DisplayName(string language)
        {
            var name = Name.Get(language);
            return string.IsNullOrEmpty(name) ? Id : name;
        }

        /// <summary>
        /// Reads a manifest. Problems go to <paramref name="report"/>; null is returned only
        /// when the text is not a manifest at all.
        /// </summary>
        public static RempackManifest Parse(string text, RempackReport report)
        {
            const string file = RempackFormat.ManifestFile;
            if (text != null && text.Length > RempackLimits.ManifestBytes)
            {
                report.Error(RempackCodes.FileTooLarge, file, text.Length.ToString(CultureInfo.InvariantCulture));
                return null;
            }

            var root = RempackJson.Parse(text, file, report);
            if (root == null)
            {
                return null;
            }

            if (root.Kind != JsonKind.Object)
            {
                report.Error(RempackCodes.FieldNotObject, file, "(root)");
                return null;
            }

            if (root["format"].AsString() != RempackFormat.Name)
            {
                report.Error(RempackCodes.ManifestFormat, file);
                return null;
            }

            var manifest = new RempackManifest();
            manifest.FormatVersion = RempackJson.Int(root, "formatVersion", string.Empty, true, 0, 1, int.MaxValue, report, file);

            // The header (id, version, name) means the same in every format version, so a
            // package made for a newer game can still be named when it is refused.
            manifest.Id = RempackJson.Str(root, "id", string.Empty, true, 128, report, file);
            if (manifest.Id.Length > 0 && !RempackIds.IsModId(manifest.Id))
            {
                report.Error(RempackCodes.IdInvalid, file, manifest.Id);
            }

            var version = RempackJson.Str(root, "version", string.Empty, true, 64, report, file);
            SemanticVersion parsed;
            if (version.Length > 0)
            {
                if (SemanticVersion.TryParse(version, out parsed))
                {
                    manifest.Version = parsed;
                }
                else
                {
                    report.Error(RempackCodes.VersionInvalid, file, version);
                }
            }

            manifest.Name = LocalizedText.Read(root["name"], "name", RempackLimits.NameLength, true, report, file);
            if (manifest.FormatVersion > RempackFormat.FormatVersion)
            {
                report.Error(RempackCodes.ManifestFormatVersion, file,
                    manifest.FormatVersion.ToString(CultureInfo.InvariantCulture),
                    RempackFormat.FormatVersion.ToString(CultureInfo.InvariantCulture));
                return manifest;
            }

            RempackJson.Known(root, string.Empty, report, file, TopLevel);
            manifest.Description = LocalizedText.Read(root["description"], "description", RempackLimits.DescriptionLength, false, report, file);
            manifest.Authors.AddRange(RempackJson.StrList(root, "authors", string.Empty, RempackLimits.Authors, RempackLimits.NameLength, report, file));
            if (manifest.Authors.Count == 0)
            {
                report.Error(RempackCodes.FieldMissing, file, "authors");
            }

            manifest.License = RempackJson.Str(root, "license", string.Empty, false, 64, report, file);
            manifest.Homepage = RempackJson.Str(root, "homepage", string.Empty, false, 256, report, file);
            if (manifest.Homepage.Length > 0 && !manifest.Homepage.StartsWith("https://", StringComparison.Ordinal))
            {
                report.Error(RempackCodes.FieldHttps, file, "homepage");
                manifest.Homepage = string.Empty;
            }

            manifest.Icon = RempackJson.Str(root, "icon", string.Empty, false, RempackLimits.PathLength, report, file);
            if (manifest.Icon.Length > 0 && !RempackPaths.IsValid(manifest.Icon))
            {
                report.Error(RempackCodes.PathInvalid, file + " icon");
                manifest.Icon = string.Empty;
            }

            foreach (var preview in RempackJson.StrList(root, "previews", string.Empty, RempackLimits.Previews, RempackLimits.PathLength, report, file))
            {
                if (RempackPaths.IsValid(preview))
                {
                    manifest.Previews.Add(preview);
                }
                else
                {
                    report.Error(RempackCodes.PathInvalid, file + " previews");
                }
            }

            ReadGame(root["game"], manifest, report, file);
            ReadReferences(root, "dependencies", manifest.Dependencies, manifest.Id, report, file);
            ReadReferences(root, "conflicts", manifest.Conflicts, manifest.Id, report, file);
            ReadContent(root["content"], manifest, report, file);
            ReadOverrides(root, manifest, report, file);
            ReadFiles(root["files"], manifest, report, file);
            return manifest;
        }

        private static void ReadGame(JsonNode game, RempackManifest manifest, RempackReport report, string file)
        {
            if (!RempackJson.IsObject(game, "game", false, report, file))
            {
                return;
            }

            RempackJson.Known(game, "game", report, file, "minVersion", "features");
            var min = RempackJson.Str(game, "minVersion", "game", false, 64, report, file);
            if (min.Length > 0)
            {
                SemanticVersion version;
                if (SemanticVersion.TryParse(min, out version))
                {
                    manifest.GameMinVersion = version;
                }
                else
                {
                    report.Error(RempackCodes.VersionInvalid, file, min);
                }
            }

            foreach (var feature in RempackJson.StrList(game, "features", "game", 32, 64, report, file))
            {
                string name;
                int version;
                if (!RempackFeatureSet.Split(feature, out name, out version))
                {
                    report.Error(RempackCodes.FeatureInvalid, file, feature);
                    continue;
                }

                if (!manifest.Features.Contains(feature))
                {
                    manifest.Features.Add(feature);
                }
            }
        }

        private static void ReadReferences(JsonNode root, string key, List<RempackModReference> target, string selfId, RempackReport report, string file)
        {
            var node = root[key];
            if (!RempackJson.IsArray(node, key, false, RempackLimits.Dependencies, report, file))
            {
                return;
            }

            for (var i = 0; i < node.Count; i++)
            {
                var field = RempackJson.Index(key, i);
                var item = node[i];
                string id;
                var rangeText = "*";
                if (item.Kind == JsonKind.String)
                {
                    id = item.AsString();
                }
                else if (RempackJson.IsObject(item, field, true, report, file))
                {
                    RempackJson.Known(item, field, report, file, "id", "version");
                    id = RempackJson.Str(item, "id", field, true, 128, report, file);
                    rangeText = RempackJson.Str(item, "version", field, false, 128, report, file);
                }
                else
                {
                    continue;
                }

                if (!RempackIds.IsModId(id))
                {
                    report.Error(RempackCodes.IdInvalid, file, id);
                    continue;
                }

                if (string.Equals(id, selfId, StringComparison.Ordinal))
                {
                    report.Error(RempackCodes.SelfReference, file + " " + field);
                    continue;
                }

                VersionRange range;
                if (!VersionRange.TryParse(rangeText, out range))
                {
                    report.Error(RempackCodes.RangeInvalid, file, rangeText);
                    continue;
                }

                target.Add(new RempackModReference(id, range));
            }
        }

        private static void ReadContent(JsonNode content, RempackManifest manifest, RempackReport report, string file)
        {
            if (!RempackJson.IsObject(content, "content", true, report, file))
            {
                return;
            }

            RempackJson.Known(content, "content", report, file, "tanks", "hangars", "maps");
            foreach (var kind in RempackFormat.Kinds)
            {
                var key = RempackFormat.Folder(kind);
                var ids = RempackJson.StrList(content, key, "content", RempackLimits.ContentItems, 128, report, file);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in ids)
                {
                    if (!RempackIds.IsLocalId(id))
                    {
                        report.Error(RempackCodes.ContentIdInvalid, file, id);
                        continue;
                    }

                    if (!seen.Add(id))
                    {
                        report.Error(RempackCodes.ContentDuplicate, file, id);
                        continue;
                    }

                    manifest._content[kind].Add(id);
                }

                if (ids.Count > 0 && !manifest.Declares(key))
                {
                    report.Error(RempackCodes.ContentFeature, file, "content." + key, key + "@1");
                }
            }

            if (manifest.ContentCount == 0)
            {
                report.Error(RempackCodes.ContentEmpty, file);
            }
        }

        private static void ReadOverrides(JsonNode root, RempackManifest manifest, RempackReport report, string file)
        {
            foreach (var text in RempackJson.StrList(root, "overrides", string.Empty, RempackLimits.ContentItems, 128, report, file))
            {
                RempackStockRef reference;
                if (!RempackStockRef.TryParse(text, out reference))
                {
                    report.Error(RempackCodes.OverrideInvalid, file, text);
                    continue;
                }

                if (!manifest.Overrides.Contains(reference))
                {
                    manifest.Overrides.Add(reference);
                }
            }

            if (manifest.Overrides.Count > 0)
            {
                if (!manifest.Declares(RempackFormat.OverridesFeature))
                {
                    report.Error(RempackCodes.ContentFeature, file, "overrides", RempackFormat.OverridesFeature + "@1");
                }

                report.Warning(RempackCodes.OverrideOnline, file);
            }
        }

        private static void ReadFiles(JsonNode files, RempackManifest manifest, RempackReport report, string file)
        {
            if (!RempackJson.IsObject(files, "files", true, report, file))
            {
                return;
            }

            if (files.Count > RempackLimits.Entries)
            {
                report.Error(RempackCodes.TooManyFiles, file, files.Count.ToString(CultureInfo.InvariantCulture));
                return;
            }

            var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in files.Fields)
            {
                if (!RempackPaths.IsValid(pair.Key) || pair.Key == RempackFormat.ManifestFile)
                {
                    report.Error(RempackCodes.PathInvalid, pair.Key);
                    continue;
                }

                if (!folded.Add(pair.Key))
                {
                    report.Error(RempackCodes.PathDuplicate, pair.Key);
                    continue;
                }

                var hash = pair.Value.AsString();
                if (hash.StartsWith("sha256:", StringComparison.Ordinal))
                {
                    hash = hash.Substring(7);
                }

                if (!IsSha256(hash))
                {
                    report.Error(RempackCodes.FieldSha256, file, "files." + pair.Key);
                    continue;
                }

                manifest.Files[pair.Key] = hash.ToLowerInvariant();
            }
        }

        /// <summary>Whether <c>game.features</c> names the feature (any version).</summary>
        public bool Declares(string featureName)
        {
            foreach (var feature in Features)
            {
                var at = feature.IndexOf('@');
                var name = at < 0 ? feature : feature.Substring(0, at);
                if (name == featureName)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsSha256(string text)
        {
            if (text == null || text.Length != 64)
            {
                return false;
            }

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The canonical manifest the SDK writes, keys in a fixed order.</summary>
        public JsonNode ToJson()
        {
            var root = JsonNode.NewObject();
            root.Set("format", RempackFormat.Name);
            root.Set("formatVersion", FormatVersion);
            root.Set("id", Id);
            root.Set("version", Version.ToString());
            root.Set("name", Name.ToJson());
            if (!Description.IsEmpty) { root.Set("description", Description.ToJson()); }
            var authors = JsonNode.NewArray();
            foreach (var author in Authors) { authors.Add(JsonNode.Str(author)); }
            root.Set("authors", authors);
            if (License.Length > 0) { root.Set("license", License); }
            if (Homepage.Length > 0) { root.Set("homepage", Homepage); }
            if (Icon.Length > 0) { root.Set("icon", Icon); }
            if (Previews.Count > 0)
            {
                var previews = JsonNode.NewArray();
                foreach (var preview in Previews) { previews.Add(JsonNode.Str(preview)); }
                root.Set("previews", previews);
            }

            var game = JsonNode.NewObject();
            if (GameMinVersion != null) { game.Set("minVersion", GameMinVersion.ToString()); }
            var features = JsonNode.NewArray();
            foreach (var feature in Features) { features.Add(JsonNode.Str(feature)); }
            game.Set("features", features);
            root.Set("game", game);
            root.Set("dependencies", References(Dependencies));
            root.Set("conflicts", References(Conflicts));
            var content = JsonNode.NewObject();
            foreach (var kind in RempackFormat.Kinds)
            {
                var ids = JsonNode.NewArray();
                foreach (var id in _content[kind]) { ids.Add(JsonNode.Str(id)); }
                content.Set(RempackFormat.Folder(kind), ids);
            }

            root.Set("content", content);
            var overrides = JsonNode.NewArray();
            foreach (var reference in Overrides) { overrides.Add(JsonNode.Str(reference.ToString())); }
            root.Set("overrides", overrides);
            var files = JsonNode.NewObject();
            foreach (var pair in Files) { files.Set(pair.Key, pair.Value); }
            root.Set("files", files);
            return root;
        }

        private static JsonNode References(List<RempackModReference> references)
        {
            var array = JsonNode.NewArray();
            foreach (var reference in references)
            {
                var item = JsonNode.NewObject();
                item.Set("id", reference.Id);
                item.Set("version", reference.Range.Text);
                array.Add(item);
            }

            return array;
        }
    }
}
