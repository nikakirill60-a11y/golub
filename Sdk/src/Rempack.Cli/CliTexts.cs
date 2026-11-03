using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Sdk
{
    /// <summary>Keys of the CLI's own lines in the game's <c>lang.json</c>.</summary>
    internal static class CliKeys
    {
        public const string Usage = "sdk_usage";
        public const string UsageValidate = "sdk_usage_validate";
        public const string UsagePack = "sdk_usage_pack";
        public const string UsageInspect = "sdk_usage_inspect";
        public const string Version = "sdk_version";
        public const string NoFeatures = "sdk_no_features";
        public const string SeeHelp = "sdk_see_help";
        public const string UnknownCommand = "sdk_unknown_command";
        public const string UnknownOption = "sdk_unknown_option";
        public const string OptionValue = "sdk_option_value";
        public const string ExtraArgument = "sdk_extra_argument";
        public const string MissingPath = "sdk_missing_path";
        public const string BadLanguage = "sdk_bad_language";
        public const string NotFound = "sdk_not_found";
        public const string NotAFile = "sdk_not_a_file";
        public const string NotAFolder = "sdk_not_a_folder";
        public const string Checking = "sdk_checking";
        public const string Packing = "sdk_packing";
        public const string Accepted = "sdk_accepted";
        public const string AcceptedHeavy = "sdk_accepted_heavy";
        public const string Refused = "sdk_refused";
        public const string RefusedStrict = "sdk_refused_strict";
        public const string NeedsNewerGame = "sdk_needs_newer_game";
        public const string Counts = "sdk_counts";
        public const string Dropped = "sdk_dropped";
        public const string Written = "sdk_written";
        public const string NotWritten = "sdk_not_written";
        public const string WriteFailed = "sdk_write_failed";
        public const string SkippedLink = "sdk_skipped_link";
        public const string Skipped = "sdk_skipped";
        public const string PressEnter = "sdk_press_enter";
        public const string LabelFile = "sdk_label_file";
        public const string LabelId = "sdk_label_id";
        public const string LabelVersion = "sdk_label_version";
        public const string LabelName = "sdk_label_name";
        public const string LabelDescription = "sdk_label_description";
        public const string LabelAuthors = "sdk_label_authors";
        public const string LabelLicense = "sdk_label_license";
        public const string LabelHomepage = "sdk_label_homepage";
        public const string LabelGame = "sdk_label_game";
        public const string LabelRequires = "sdk_label_requires";
        public const string LabelConflicts = "sdk_label_conflicts";
        public const string LabelContent = "sdk_label_content";
        public const string LabelReplaces = "sdk_label_replaces";
        public const string LabelSize = "sdk_label_size";
        public const string GameFrom = "sdk_game_from";
        public const string GameAny = "sdk_game_any";
        public const string None = "sdk_none";
        public const string SizeLine = "sdk_size_line";
        public const string FilesHeader = "sdk_files_header";
        public const string KindTanks = "sdk_kind_tanks";
        public const string KindHangars = "sdk_kind_hangars";
        public const string KindMaps = "sdk_kind_maps";
        public const string Units = "sdk_units";

        /// <summary>Every key, for the test that keeps the table complete.</summary>
        public static IEnumerable<string> All
        {
            get
            {
                foreach (var field in typeof(CliKeys).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.IsLiteral && field.FieldType == typeof(string))
                    {
                        yield return (string)field.GetRawConstantValue();
                    }
                }
            }
        }

        public static string Kind(RempackContentKind kind)
        {
            switch (kind)
            {
                case RempackContentKind.Tank: return KindTanks;
                case RempackContentKind.Hangar: return KindHangars;
                default: return KindMaps;
            }
        }
    }

    /// <summary>
    /// The CLI's words come from the game's localization table, embedded at build time:
    /// <c>rempack_*</c> rows word package issues exactly as the MODS screen does, and
    /// <c>sdk_*</c> rows are the CLI's own lines.
    /// </summary>
    internal sealed class CliTexts
    {
        public const string ResourceName = "lang.json";

        private static JsonNode _table;

        public CliTexts(string language)
        {
            Language = CliLanguage.IsSupported(language) ? language : "en";
            Lookup = RempackMessages.FromTable(Table, Language);
        }

        public string Language { get; private set; }

        /// <summary>Key to template, English when the language lacks the row.</summary>
        public Func<string, string> Lookup { get; private set; }

        internal static JsonNode Table
        {
            get { return _table ?? (_table = Load()); }
        }

        /// <summary>A row with <c>{0}</c>.. filled in; the key itself when the table lacks it.</summary>
        public string Get(string key, params string[] args)
        {
            var template = Lookup(key);
            return string.IsNullOrEmpty(template) ? key : RempackMessages.Fill(template, args);
        }

        /// <summary>A byte count the way people read it: 812 B, 4.2 MB.</summary>
        public string Size(long bytes)
        {
            var units = Get(CliKeys.Units).Split('|');
            if (units.Length < 4)
            {
                units = new[] { "B", "KB", "MB", "GB" };
            }

            if (bytes < 1024)
            {
                return bytes.ToString(CultureInfo.InvariantCulture) + " " + units[0];
            }

            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < 3)
            {
                value /= 1024;
                unit++;
            }

            return value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        private static JsonNode Load()
        {
            using (var stream = typeof(CliTexts).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                {
                    return JsonNode.NewObject();
                }

                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    JsonNode table;
                    return JsonNode.TryParse(reader.ReadToEnd(), out table) && table != null ? table : JsonNode.NewObject();
                }
            }
        }
    }
}
