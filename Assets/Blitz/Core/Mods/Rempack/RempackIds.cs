using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Mods.Rempack
{
    /// <summary>
    /// Identifiers. A mod is <c>author.mod_name</c>; its content uses local ids
    /// (<c>t34_miku</c>) that only have to be unique inside the mod; stock content is
    /// named <c>stock:tank/&lt;id&gt;</c>. Every part is lowercase Latin letters and digits
    /// joined by single underscores, which keeps ids valid as file names, resource
    /// keys and save keys on every platform and lets the game derive the internal
    /// name <c>rp_author__mod_name__local</c> without any chance of a collision.
    /// </summary>
    public static class RempackIds
    {
        public const string InternalPrefix = "rp_";
        public const string StockPrefix = "stock:";
        public const int ModIdLength = 64;
        public const int LocalIdLength = 48;

        public static bool IsModId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > ModIdLength)
            {
                return false;
            }

            var dot = id.IndexOf('.');
            return dot > 0 && id.IndexOf('.', dot + 1) < 0
                && IsWord(id.Substring(0, dot), 32) && IsWord(id.Substring(dot + 1), 48);
        }

        public static bool IsLocalId(string id)
        {
            return IsWord(id, LocalIdLength);
        }

        /// <summary>The name the game's tables use for a piece of mod content.</summary>
        public static string InternalName(string modId, string localId)
        {
            if (!IsModId(modId) || !IsLocalId(localId))
            {
                throw new ArgumentException("Invalid mod or content id: " + modId + "/" + localId);
            }

            return InternalPrefix + modId.Replace(".", "__") + "__" + localId;
        }

        public static bool IsInternalName(string name)
        {
            string modId, localId;
            return TryParseInternalName(name, out modId, out localId);
        }

        public static bool TryParseInternalName(string name, out string modId, out string localId)
        {
            modId = null;
            localId = null;
            if (string.IsNullOrEmpty(name) || !name.StartsWith(InternalPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var parts = name.Substring(InternalPrefix.Length).Split(new[] { "__" }, StringSplitOptions.None);
            if (parts.Length != 3)
            {
                return false;
            }

            var mod = parts[0] + "." + parts[1];
            if (!IsModId(mod) || !IsLocalId(parts[2]))
            {
                return false;
            }

            modId = mod;
            localId = parts[2];
            return true;
        }

        /// <summary>Segments of <c>[a-z0-9]</c> joined by single underscores.</summary>
        private static bool IsWord(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length > maxLength || text[0] == '_' || text[text.Length - 1] == '_')
            {
                return false;
            }

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '_')
                {
                    if (text[i - 1] == '_')
                    {
                        return false;
                    }

                    continue;
                }

                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>A reference to content shipped with the game: <c>stock:tank/ussr_t34</c>.</summary>
    public sealed class RempackStockRef : IEquatable<RempackStockRef>
    {
        public RempackStockRef(RempackContentKind kind, string id)
        {
            Kind = kind;
            Id = id ?? string.Empty;
        }

        public RempackContentKind Kind { get; private set; }

        /// <summary>The stock name as the game's tables store it.</summary>
        public string Id { get; private set; }

        public static bool TryParse(string text, out RempackStockRef reference)
        {
            reference = null;
            if (string.IsNullOrEmpty(text) || !text.StartsWith(RempackIds.StockPrefix, StringComparison.Ordinal)
                || text.Length > 128)
            {
                return false;
            }

            var body = text.Substring(RempackIds.StockPrefix.Length);
            var slash = body.IndexOf('/');
            if (slash <= 0 || slash == body.Length - 1)
            {
                return false;
            }

            var word = body.Substring(0, slash);
            var id = body.Substring(slash + 1);
            for (var i = 0; i < id.Length; i++)
            {
                var c = id[i];
                // Stock tank names come from the shipped tables ("t-34-85", "ussr_t34").
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.'))
                {
                    return false;
                }
            }

            foreach (var kind in RempackFormat.Kinds)
            {
                if (RempackFormat.KindWord(kind) == word)
                {
                    reference = new RempackStockRef(kind, id);
                    return true;
                }
            }

            return false;
        }

        public bool Equals(RempackStockRef other)
        {
            return other != null && other.Kind == Kind && string.Equals(other.Id, Id, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as RempackStockRef);
        }

        public override int GetHashCode()
        {
            return ((int)Kind * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
        }

        public override string ToString()
        {
            return RempackIds.StockPrefix + RempackFormat.KindWord(Kind) + "/" + Id;
        }
    }

    /// <summary>
    /// Text in several languages: either a plain string (English) or an object such
    /// as <c>{ "en": "Miku pack", "ru": "Набор Мику" }</c>. Lookups fall back to the base
    /// language, then English, then whatever exists.
    /// </summary>
    public sealed class LocalizedText
    {
        private readonly Dictionary<string, string> _texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<KeyValuePair<string, string>> Entries
        {
            get { return _texts; }
        }

        public bool IsEmpty
        {
            get { return _texts.Count == 0; }
        }

        public bool Has(string language)
        {
            return language != null && _texts.ContainsKey(language);
        }

        public void Set(string language, string text)
        {
            _texts[language] = text ?? string.Empty;
        }

        public string Get(string language)
        {
            string text;
            if (!string.IsNullOrEmpty(language))
            {
                if (_texts.TryGetValue(language, out text))
                {
                    return text;
                }

                var dash = language.IndexOf('-');
                if (dash > 0 && _texts.TryGetValue(language.Substring(0, dash), out text))
                {
                    return text;
                }
            }

            if (_texts.TryGetValue("en", out text))
            {
                return text;
            }

            foreach (var pair in _texts)
            {
                return pair.Value;
            }

            return string.Empty;
        }

        public static bool IsLanguage(string code)
        {
            if (string.IsNullOrEmpty(code) || (code.Length != 2 && code.Length != 5))
            {
                return false;
            }

            for (var i = 0; i < code.Length; i++)
            {
                var c = code[i];
                var ok = i == 2 ? c == '-' : i < 2 ? (c >= 'a' && c <= 'z') : ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Reads a localized field; reports problems under <paramref name="field"/>.</summary>
        public static LocalizedText Read(
            JsonNode node, string field, int maxLength, bool requireEnglish, RempackReport report, string file)
        {
            var text = new LocalizedText();
            if (node.Kind == JsonKind.String)
            {
                text.Set("en", Checked(node.AsString(), field, maxLength, report, file));
                return text;
            }

            if (node.Kind == JsonKind.Null)
            {
                if (requireEnglish)
                {
                    report.Error(RempackCodes.FieldMissing, file, field);
                }

                return text;
            }

            if (node.Kind != JsonKind.Object)
            {
                report.Error(RempackCodes.FieldNotText, file, field);
                return text;
            }

            foreach (var pair in node.Fields)
            {
                if (!IsLanguage(pair.Key))
                {
                    report.Error(RempackCodes.FieldLanguage, file, field + "." + pair.Key);
                    continue;
                }

                if (pair.Value.Kind != JsonKind.String)
                {
                    report.Error(RempackCodes.FieldNotString, file, field + "." + pair.Key);
                    continue;
                }

                text.Set(pair.Key, Checked(pair.Value.AsString(), field + "." + pair.Key, maxLength, report, file));
            }

            if (requireEnglish && !text.Has("en"))
            {
                report.Error(RempackCodes.NameMissingEnglish, file, field);
            }

            return text;
        }

        public JsonNode ToJson()
        {
            var node = JsonNode.NewObject();
            var keys = new List<string>(_texts.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                node.Set(key, _texts[key]);
            }

            return node;
        }

        private static string Checked(string value, string field, int maxLength, RempackReport report, string file)
        {
            if (value.Length > maxLength)
            {
                report.Error(RempackCodes.TextTooLong, file, field, maxLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
                value = value.Substring(0, maxLength);
            }

            if (RempackValidator.HasControl(value))
            {
                report.Error(RempackCodes.TextControl, file, field);
            }

            return value;
        }
    }
}
