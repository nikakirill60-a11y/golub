using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Localization
{
    /// <summary>
    /// Port of Godot's <c>tr()</c> lookup. Godot resolves keys through
    /// .translation files compiled from lang_dump.csv / lang_extra.csv; here the
    /// same CSV rows are baked into a JSON resource at port time, so no
    /// user-facing string is ever written into C#.
    /// </summary>
    public sealed class LocalizationTable
    {
        public const string FallbackLanguage = "en";

        private readonly Dictionary<string, Dictionary<string, string>> _keys =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        private string _language = FallbackLanguage;

        public string Language
        {
            get { return _language; }
            set { _language = string.IsNullOrEmpty(value) ? FallbackLanguage : value; }
        }

        public int Count
        {
            get { return _keys.Count; }
        }

        public bool Has(string key)
        {
            return key != null && _keys.ContainsKey(key);
        }

        public string Get(string key)
        {
            return Get(key, _language);
        }

        public string Get(string key, string language)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            Dictionary<string, string> translations;
            if (!_keys.TryGetValue(key, out translations))
            {
                // Godot's tr() echoes the key back when it is untranslated.
                return key;
            }

            string text;
            if (!string.IsNullOrEmpty(language) &&
                translations.TryGetValue(language, out text) &&
                !string.IsNullOrEmpty(text))
            {
                return text;
            }

            if (translations.TryGetValue(FallbackLanguage, out text) &&
                !string.IsNullOrEmpty(text))
            {
                return text;
            }

            return key;
        }

        /// <summary>Resolve tank identity, not an ambiguous authored display-name literal.</summary>
        public string GetTankName(TankInfo tank, bool shortName = false)
        {
            if (tank == null) return string.Empty;
            var display = shortName && !string.IsNullOrEmpty(tank.DisplayNameShort)
                ? tank.DisplayNameShort : tank.DisplayName;
            return GetTankName(tank.Name, string.IsNullOrEmpty(display) ? tank.Name : display, shortName);
        }

        /// <summary>Saved results retain IDs and raw titles; resolve at display time in the current language.</summary>
        public string GetTankName(string tankId, string fallback, bool shortName = true)
        {
            var key = "tank:" + tankId + (shortName ? "/short" : "/name");
            return Has(key) ? Get(key) : Get(fallback);
        }

        public void Put(string key, string language, string text)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(language))
            {
                return;
            }

            Dictionary<string, string> translations;
            if (!_keys.TryGetValue(key, out translations))
            {
                translations = new Dictionary<string, string>(StringComparer.Ordinal);
                _keys[key] = translations;
            }

            translations[language] = text ?? string.Empty;
        }

        public static LocalizationTable FromJson(JsonNode node)
        {
            var table = new LocalizationTable();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return table;
            }

            var keys = node["keys"];
            if (keys.Kind != JsonKind.Object)
            {
                return table;
            }

            foreach (var entry in keys.Fields)
            {
                if (entry.Value.Kind != JsonKind.Object)
                {
                    continue;
                }

                foreach (var translation in entry.Value.Fields)
                {
                    table.Put(entry.Key, translation.Key, translation.Value.AsString());
                }
            }

            return table;
        }

        public static LocalizationTable Parse(string json)
        {
            JsonNode node;
            return JsonNode.TryParse(json, out node)
                ? FromJson(node)
                : new LocalizationTable();
        }
    }
}
