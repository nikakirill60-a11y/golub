using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BlitzLegacy.Core.Mods
{
    /// <summary>
    /// One [header] block of a Godot text resource with the properties that
    /// follow it. Values are kept as the source text: the converter knows what
    /// a field means, the parser only has to find it.
    /// </summary>
    public sealed class GodotSection
    {
        public string Kind = string.Empty;
        public readonly Dictionary<string, string> Attributes =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Properties =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public string Id
        {
            get { return Attribute("id"); }
        }

        public string Attribute(string name)
        {
            string value;
            return Attributes.TryGetValue(name, out value) ? value : string.Empty;
        }

        public string Raw(string name)
        {
            string value;
            return Properties.TryGetValue(name, out value) ? value : string.Empty;
        }

        public bool Has(string name)
        {
            return Properties.ContainsKey(name);
        }
    }

    /// <summary>
    /// A reader for the .tres files the Godot mods ship their cards in.
    ///
    /// The mods are plain Godot 4 text resources: a gd_resource header, a list
    /// of ext_resource script references that say what each sub_resource is,
    /// and one sub_resource per module. Nothing here evaluates GDScript, so a
    /// mod cannot run code through this path; the parser only lifts scalars,
    /// vectors and references out of the text.
    /// </summary>
    public sealed class GodotResourceFile
    {
        private readonly List<GodotSection> _sections = new List<GodotSection>();
        private readonly Dictionary<string, GodotSection> _byId =
            new Dictionary<string, GodotSection>(StringComparer.Ordinal);

        public IReadOnlyList<GodotSection> Sections
        {
            get { return _sections; }
        }

        /// <summary>The trailing [resource] block, i.e. the card itself.</summary>
        public GodotSection Root { get; private set; }

        public GodotSection Section(string id)
        {
            GodotSection section;
            return _byId.TryGetValue(id, out section) ? section : null;
        }

        /// <summary>The section a property points at, following SubResource().</summary>
        public GodotSection Follow(GodotSection owner, string property)
        {
            if (owner == null)
            {
                return null;
            }

            var id = Reference(owner.Raw(property));
            return string.IsNullOrEmpty(id) ? null : Section(id);
        }

        /// <summary>Every section an Array[...]([...]) property points at.</summary>
        public List<GodotSection> FollowArray(GodotSection owner, string property)
        {
            var result = new List<GodotSection>();
            if (owner == null)
            {
                return result;
            }

            var ids = References(owner.Raw(property));
            for (var i = 0; i < ids.Count; i++)
            {
                var section = Section(ids[i]);
                if (section != null)
                {
                    result.Add(section);
                }
            }

            return result;
        }

        /// <summary>The script path behind a section, e.g. module_gun.gd.</summary>
        public string ScriptOf(GodotSection section)
        {
            if (section == null)
            {
                return string.Empty;
            }

            var id = Reference(section.Raw("script"));
            var ext = string.IsNullOrEmpty(id) ? null : Section(id);
            if (ext == null)
            {
                return string.Empty;
            }

            var path = ext.Attribute("path");
            var slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        public static GodotResourceFile Parse(string text)
        {
            var file = new GodotResourceFile();
            if (string.IsNullOrEmpty(text))
            {
                return file;
            }

            GodotSection current = null;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed.StartsWith("[", StringComparison.Ordinal)
                    && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    current = Header(trimmed.Substring(1, trimmed.Length - 2));
                    file._sections.Add(current);
                    if (!string.IsNullOrEmpty(current.Id))
                    {
                        file._byId[current.Id] = current;
                    }

                    if (current.Kind == "resource")
                    {
                        file.Root = current;
                    }

                    continue;
                }

                if (current == null)
                {
                    continue;
                }

                var equals = trimmed.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                var key = trimmed.Substring(0, equals).Trim();
                var value = trimmed.Substring(equals + 1).Trim();

                // A value may wrap over several lines, as Curve and Array do.
                while (Unbalanced(value) && i + 1 < lines.Length)
                {
                    i++;
                    value += lines[i].Trim();
                }

                current.Properties[key] = value;
            }

            return file;
        }

        private static GodotSection Header(string body)
        {
            var section = new GodotSection();
            var parts = Split(body);
            if (parts.Count > 0)
            {
                section.Kind = parts[0];
            }

            for (var i = 1; i < parts.Count; i++)
            {
                var equals = parts[i].IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                var key = parts[i].Substring(0, equals).Trim();
                section.Attributes[key] = Text(parts[i].Substring(equals + 1).Trim());
            }

            return section;
        }

        /// <summary>Splits a header on spaces that are not inside quotes.</summary>
        private static List<string> Split(string body)
        {
            var parts = new List<string>();
            var token = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                if (c == '"')
                {
                    quoted = !quoted;
                    token.Append(c);
                    continue;
                }

                if (c == ' ' && !quoted)
                {
                    if (token.Length > 0)
                    {
                        parts.Add(token.ToString());
                        token.Length = 0;
                    }

                    continue;
                }

                token.Append(c);
            }

            if (token.Length > 0)
            {
                parts.Add(token.ToString());
            }

            return parts;
        }

        private static bool Unbalanced(string value)
        {
            var depth = 0;
            var quoted = false;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '"')
                {
                    quoted = !quoted;
                }
                else if (!quoted && (c == '(' || c == '[' || c == '{'))
                {
                    depth++;
                }
                else if (!quoted && (c == ')' || c == ']' || c == '}'))
                {
                    depth--;
                }
            }

            return depth > 0 || quoted;
        }

        // ------------------------------------------------------------------
        // Value helpers.
        // ------------------------------------------------------------------

        /// <summary>Strips the quotes off a Godot string literal.</summary>
        public static string Text(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }

            var value = raw.Trim();
            if (value.Length >= 2 && value[0] == '"'
                && value[value.Length - 1] == '"')
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value.Replace("\\\"", "\"");
        }

        public static float Number(string raw, float fallback)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return fallback;
            }

            float value;
            return float.TryParse(
                raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        public static int Integer(string raw, int fallback)
        {
            var value = Number(raw, float.NaN);
            return float.IsNaN(value) ? fallback : (int)Math.Round(value);
        }

        public static bool Flag(string raw, bool fallback)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return fallback;
            }

            var value = raw.Trim();
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return fallback;
        }

        /// <summary>Vector2(x, y) -&gt; its two components.</summary>
        public static bool TryVector(string raw, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            var open = raw.IndexOf('(');
            var close = raw.LastIndexOf(')');
            if (open < 0 || close <= open)
            {
                return false;
            }

            var parts = raw.Substring(open + 1, close - open - 1).Split(',');
            if (parts.Length < 2)
            {
                return false;
            }

            x = Number(parts[0], 0f);
            y = Number(parts[1], 0f);
            return true;
        }

        /// <summary>The first SubResource/ExtResource id in the value.</summary>
        public static string Reference(string raw)
        {
            var ids = References(raw);
            return ids.Count > 0 ? ids[0] : string.Empty;
        }

        /// <summary>Every SubResource/ExtResource id in the value, in order.</summary>
        public static List<string> References(string raw)
        {
            var ids = new List<string>();
            if (string.IsNullOrEmpty(raw))
            {
                return ids;
            }

            var index = 0;
            while (index < raw.Length)
            {
                var open = raw.IndexOf("Resource(", index, StringComparison.Ordinal);
                if (open < 0)
                {
                    break;
                }

                var quote = raw.IndexOf('"', open);
                if (quote < 0)
                {
                    break;
                }

                var end = raw.IndexOf('"', quote + 1);
                if (end < 0)
                {
                    break;
                }

                ids.Add(raw.Substring(quote + 1, end - quote - 1));
                index = end + 1;
            }

            return ids;
        }
    }
}
