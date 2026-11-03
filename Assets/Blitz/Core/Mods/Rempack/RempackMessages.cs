using System;
using System.Collections.Generic;
using System.Reflection;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Mods.Rempack
{
    /// <summary>Stable issue codes. Never renamed: modders search for them.</summary>
    public static class RempackCodes
    {
        public const string FileUnreadable = "file.unreadable";
        public const string ZipInvalid = "zip.invalid";
        public const string ZipZip64 = "zip.zip64";
        public const string ZipMultiDisk = "zip.multi_disk";
        public const string ZipEncrypted = "zip.encrypted";
        public const string ZipMethod = "zip.method";
        public const string ZipSizeMismatch = "zip.size_mismatch";
        public const string ZipCrcMismatch = "zip.crc_mismatch";
        public const string ZipBomb = "zip.bomb";
        public const string ArchiveTooLarge = "archive.too_large";
        public const string ArchiveHeavy = "archive.heavy";
        public const string UnpackedTooLarge = "archive.unpacked_too_large";
        public const string TooManyFiles = "archive.too_many_files";
        public const string FileTooLarge = "file.too_large";
        public const string PathInvalid = "path.invalid";
        public const string PathDuplicate = "path.duplicate";
        public const string PathLink = "path.link";
        public const string TextNotUtf8 = "text.not_utf8";
        public const string FileUnlisted = "file.unlisted";
        public const string FileMissing = "file.missing";
        public const string FileHashMismatch = "file.hash_mismatch";
        public const string FileUnused = "file.unused";
        public const string JsonEmpty = "json.empty";
        public const string JsonInvalid = "json.invalid";
        public const string JsonTooDeep = "json.too_deep";
        public const string JsonTooLarge = "json.too_large";
        public const string JsonHeavy = "json.heavy";

        public const string FieldMissing = "field.missing";
        public const string FieldNotObject = "field.not_object";
        public const string FieldNotArray = "field.not_array";
        public const string FieldNotString = "field.not_string";
        public const string FieldNotNumber = "field.not_number";
        public const string FieldNotInteger = "field.not_integer";
        public const string FieldNotBoolean = "field.not_boolean";
        public const string FieldNotText = "field.not_text";
        public const string FieldNotVector = "field.not_vector";
        public const string FieldRange = "field.range";
        public const string FieldCount = "field.count";
        public const string FieldUnknown = "field.unknown";
        public const string FieldLanguage = "field.language";
        public const string FieldHttps = "field.https";
        public const string FieldSha256 = "field.sha256";
        public const string TextTooLong = "text.too_long";
        public const string TextControl = "text.control";

        public const string ManifestMissing = "manifest.missing";
        public const string ManifestFormat = "manifest.format";
        public const string ManifestFormatVersion = "manifest.format_version";
        public const string IdInvalid = "manifest.id_invalid";
        public const string VersionInvalid = "manifest.version_invalid";
        public const string NameMissingEnglish = "manifest.name_missing_en";
        public const string FeatureUnsupported = "manifest.feature_unsupported";
        public const string FeatureInvalid = "manifest.feature_invalid";
        public const string GameTooOld = "manifest.game_too_old";
        public const string RangeInvalid = "manifest.range_invalid";
        public const string SelfReference = "manifest.self_reference";
        public const string ContentIdInvalid = "content.id_invalid";
        public const string ContentDuplicate = "content.duplicate";
        public const string ContentMissing = "content.missing";
        public const string ContentEmpty = "content.empty";
        public const string ContentFeature = "content.feature";
        public const string OverrideInvalid = "override.invalid";
        public const string OverrideOnline = "override.online";
        public const string OverrideShadowed = "override.shadowed";
        public const string ReferenceMissing = "reference.missing";
        public const string ImageInvalid = "image.invalid";
        public const string LocaleLanguage = "locale.language";
        public const string LocaleKey = "locale.key";

        public const string DependencyMissing = "dependency.missing";
        public const string DependencyVersion = "dependency.version";
        public const string DependencyInactive = "dependency.inactive";
        public const string ConflictPresent = "conflict.present";
        public const string Duplicate = "library.duplicate";
        public const string InstallDowngrade = "install.downgrade";
        public const string InstallReinstall = "install.reinstall";
        public const string InstallUpdate = "install.update";
        public const string InstallBreaks = "install.breaks";
        public const string InstallCopyFailed = "install.copy_failed";
        public const string SafeModeCrashed = "safe_mode.crashed";

        /// <summary>Refusals that an update of the game fixes, not a change of the package.</summary>
        public static bool NeedsNewerGame(string code)
        {
            return code == ManifestFormatVersion || code == FeatureUnsupported || code == GameTooOld;
        }

        /// <summary>Soft-limit warnings that mark a package as heavy.</summary>
        public static bool IsHeavy(string code)
        {
            return code == ArchiveHeavy || code == JsonHeavy;
        }

        /// <summary>Every code, for the tests that keep the texts complete and for the CLI.</summary>
        public static IEnumerable<string> All
        {
            get
            {
                foreach (var field in typeof(RempackCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.IsLiteral && field.FieldType == typeof(string))
                    {
                        yield return (string)field.GetRawConstantValue();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Wording of issues. The texts live in the game's localization table
    /// (<c>lang.json</c>, keys <c>rempack_&lt;code&gt;</c>), which the SDK CLI embeds as
    /// well, so the MODS screen and the CLI word a problem identically. A lookup maps a
    /// key to its template; <c>{0}</c>.. are the issue arguments.
    /// </summary>
    public static class RempackMessages
    {
        public const string KeyPrefix = "rempack_";

        public static string Key(string code)
        {
            return KeyPrefix + (code ?? string.Empty).Replace('.', '_');
        }

        public static string SeverityKey(RempackSeverity severity)
        {
            switch (severity)
            {
                case RempackSeverity.Error: return KeyPrefix + "severity_error";
                case RempackSeverity.Warning: return KeyPrefix + "severity_warning";
                default: return KeyPrefix + "severity_info";
            }
        }

        /// <summary>
        /// The text of one code. A missing template (an older table, a typo) falls back
        /// to the code and its arguments, which is still searchable.
        /// </summary>
        public static string Format(string code, string[] args, Func<string, string> lookup)
        {
            var key = Key(code);
            var template = lookup == null ? null : lookup(key);
            if (string.IsNullOrEmpty(template) || template == key)
            {
                return (code ?? string.Empty) + (args != null && args.Length > 0 ? " (" + string.Join(", ", args) + ")" : string.Empty);
            }

            return Fill(template, args);
        }

        /// <summary>Replaces <c>{0}</c>..<c>{9}</c> in one pass, so an argument cannot inject a placeholder.</summary>
        public static string Fill(string template, string[] args)
        {
            var text = new System.Text.StringBuilder(template.Length + 32);
            for (var i = 0; i < template.Length; i++)
            {
                var c = template[i];
                if (c == '{' && i + 2 < template.Length && template[i + 1] >= '0' && template[i + 1] <= '9' && template[i + 2] == '}')
                {
                    var index = template[i + 1] - '0';
                    text.Append(args != null && index < args.Length ? args[index] ?? string.Empty : string.Empty);
                    i += 2;
                    continue;
                }

                text.Append(c);
            }

            return text.ToString();
        }

        /// <summary>
        /// A lookup over a table in the <c>lang.json</c> layout (<c>{"keys": {key: {lang: text}}}</c>),
        /// for the CLI. English answers when the language lacks a row.
        /// </summary>
        public static Func<string, string> FromTable(JsonNode table, string language)
        {
            var keys = table == null ? null : table["keys"];
            return delegate(string key)
            {
                if (keys == null || keys.Kind != JsonKind.Object)
                {
                    return null;
                }

                var row = keys[key];
                if (row.Kind != JsonKind.Object)
                {
                    return null;
                }

                var text = string.IsNullOrEmpty(language) ? string.Empty : row[language].AsString();
                return string.IsNullOrEmpty(text) ? row["en"].AsString() : text;
            };
        }
    }
}
