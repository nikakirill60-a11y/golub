using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Mods.Rempack
{
    /// <summary>
    /// Bounded JSON reading for package files: depth and size are checked before the
    /// tree is built, and every typed read reports a precise field path instead of
    /// silently falling back, so a typo in a mod is a message, not a mystery.
    /// </summary>
    public static class RempackJson
    {
        public static JsonNode Parse(string text, string file, RempackReport report)
        {
            if (text == null)
            {
                report.Error(RempackCodes.JsonEmpty, file);
                return null;
            }

            if (text.Length > RempackLimits.JsonBytes)
            {
                report.Error(RempackCodes.JsonTooLarge, file);
                return null;
            }

            var depth = 0;
            var quoted = false;
            var escape = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (escape) { escape = false; }
                    else if (c == '\\') { escape = true; }
                    else if (c == '"') { quoted = false; }
                }
                else if (c == '"')
                {
                    quoted = true;
                }
                else if (c == '{' || c == '[')
                {
                    depth++;
                    if (depth > RempackLimits.JsonDepth)
                    {
                        report.Error(RempackCodes.JsonTooDeep, file);
                        return null;
                    }
                }
                else if (c == '}' || c == ']')
                {
                    depth--;
                }
            }

            if (text.Trim().Length == 0)
            {
                report.Error(RempackCodes.JsonEmpty, file);
                return null;
            }

            JsonNode node;
            try
            {
                if (!JsonNode.TryParse(text, out node) || node == null)
                {
                    report.Error(RempackCodes.JsonInvalid, file);
                    return null;
                }
            }
            catch (Exception error)
            {
                report.Add(new RempackIssue(RempackSeverity.Error, RempackCodes.JsonInvalid, file).WithDetail(error.Message));
                return null;
            }

            return node;
        }

        /// <summary>Warns about fields the reader does not know; typos show up at once.</summary>
        public static void Known(JsonNode node, string prefix, RempackReport report, string file, params string[] keys)
        {
            if (node.Kind != JsonKind.Object)
            {
                return;
            }

            foreach (var pair in node.Fields)
            {
                if (Array.IndexOf(keys, pair.Key) < 0 && !pair.Key.StartsWith("$", StringComparison.Ordinal)
                    && pair.Key != "comment")
                {
                    report.Warning(RempackCodes.FieldUnknown, file, Join(prefix, pair.Key));
                }
            }
        }

        public static string Join(string prefix, string key)
        {
            return string.IsNullOrEmpty(prefix) ? key : prefix + "." + key;
        }

        public static string Index(string prefix, int index)
        {
            return prefix + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
        }

        public static bool IsObject(JsonNode node, string field, bool required, RempackReport report, string file)
        {
            if (node.Kind == JsonKind.Object)
            {
                return true;
            }

            if (node.Kind == JsonKind.Null && !required)
            {
                return false;
            }

            report.Error(node.Kind == JsonKind.Null ? RempackCodes.FieldMissing : RempackCodes.FieldNotObject, file, field);
            return false;
        }

        public static bool IsArray(JsonNode node, string field, bool required, int maxCount, RempackReport report, string file)
        {
            if (node.Kind == JsonKind.Null && !required)
            {
                return false;
            }

            if (node.Kind != JsonKind.Array)
            {
                report.Error(node.Kind == JsonKind.Null ? RempackCodes.FieldMissing : RempackCodes.FieldNotArray, file, field);
                return false;
            }

            if (node.Count > maxCount)
            {
                report.Error(RempackCodes.FieldCount, file, field, node.Count.ToString(CultureInfo.InvariantCulture),
                    maxCount.ToString(CultureInfo.InvariantCulture));
                return false;
            }

            return true;
        }

        public static string Str(JsonNode parent, string key, string prefix, bool required, int maxLength, RempackReport report, string file)
        {
            var node = parent[key];
            var field = Join(prefix, key);
            if (node.Kind == JsonKind.Null)
            {
                if (required)
                {
                    report.Error(RempackCodes.FieldMissing, file, field);
                }

                return string.Empty;
            }

            if (node.Kind != JsonKind.String)
            {
                report.Error(RempackCodes.FieldNotString, file, field);
                return string.Empty;
            }

            var value = node.AsString();
            if (value.Length > maxLength)
            {
                report.Error(RempackCodes.TextTooLong, file, field, maxLength.ToString(CultureInfo.InvariantCulture));
                return value.Substring(0, maxLength);
            }

            return value;
        }

        public static double Num(JsonNode parent, string key, string prefix, bool required, double fallback, double min, double max, RempackReport report, string file)
        {
            var node = parent[key];
            var field = Join(prefix, key);
            if (node.Kind == JsonKind.Null)
            {
                if (required)
                {
                    report.Error(RempackCodes.FieldMissing, file, field);
                }

                return fallback;
            }

            if (node.Kind != JsonKind.Number)
            {
                report.Error(RempackCodes.FieldNotNumber, file, field);
                return fallback;
            }

            var value = node.AsDouble();
            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
            {
                report.Error(RempackCodes.FieldRange, file, field, value.ToString("R", CultureInfo.InvariantCulture),
                    min.ToString("R", CultureInfo.InvariantCulture) + ".." + max.ToString("R", CultureInfo.InvariantCulture));
                return fallback;
            }

            return value;
        }

        public static int Int(JsonNode parent, string key, string prefix, bool required, int fallback, int min, int max, RempackReport report, string file)
        {
            var node = parent[key];
            if (node.Kind == JsonKind.Number && Math.Abs(node.AsDouble() - Math.Round(node.AsDouble())) > 1e-9)
            {
                report.Error(RempackCodes.FieldNotInteger, file, Join(prefix, key));
                return fallback;
            }

            return (int)Num(parent, key, prefix, required, fallback, min, max, report, file);
        }

        public static bool Bool(JsonNode parent, string key, string prefix, bool fallback, RempackReport report, string file)
        {
            var node = parent[key];
            if (node.Kind == JsonKind.Null)
            {
                return fallback;
            }

            if (node.Kind != JsonKind.Bool)
            {
                report.Error(RempackCodes.FieldNotBoolean, file, Join(prefix, key));
                return fallback;
            }

            return node.AsBool();
        }

        public static List<string> StrList(JsonNode parent, string key, string prefix, int maxCount, int maxLength, RempackReport report, string file)
        {
            var result = new List<string>();
            var node = parent[key];
            var field = Join(prefix, key);
            if (!IsArray(node, field, false, maxCount, report, file))
            {
                return result;
            }

            for (var i = 0; i < node.Count; i++)
            {
                if (node[i].Kind != JsonKind.String)
                {
                    report.Error(RempackCodes.FieldNotString, file, Index(field, i));
                    continue;
                }

                var value = node[i].AsString();
                if (value.Length > maxLength)
                {
                    report.Error(RempackCodes.TextTooLong, file, Index(field, i), maxLength.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                result.Add(value);
            }

            return result;
        }

        /// <summary>A fixed-length numeric vector such as <c>[x, y, z]</c>.</summary>
        public static float[] Vector(JsonNode parent, string key, string prefix, int length, bool required, float[] fallback, double limit, RempackReport report, string file)
        {
            var node = parent[key];
            var field = Join(prefix, key);
            if (node.Kind == JsonKind.Null)
            {
                if (required)
                {
                    report.Error(RempackCodes.FieldMissing, file, field);
                }

                return fallback;
            }

            if (node.Kind != JsonKind.Array || node.Count != length)
            {
                report.Error(RempackCodes.FieldNotVector, file, field, length.ToString(CultureInfo.InvariantCulture));
                return fallback;
            }

            var result = new float[length];
            for (var i = 0; i < length; i++)
            {
                var value = node[i].AsDouble(double.NaN);
                if (node[i].Kind != JsonKind.Number || double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > limit)
                {
                    var bound = limit.ToString("R", CultureInfo.InvariantCulture);
                    report.Error(RempackCodes.FieldRange, file, Index(field, i), node[i].ToJson(false), "-" + bound + ".." + bound);
                    return fallback;
                }

                result[i] = (float)value;
            }

            return result;
        }

        /// <summary>
        /// Deep merge for <c>base</c> cards: objects merge key by key, everything else
        /// (numbers, strings, arrays) is replaced by the overlay.
        /// </summary>
        public static JsonNode Merge(JsonNode basis, JsonNode overlay)
        {
            if (overlay == null || overlay.Kind == JsonKind.Null)
            {
                return Clone(basis);
            }

            if (basis == null || basis.Kind != JsonKind.Object || overlay.Kind != JsonKind.Object)
            {
                return Clone(overlay);
            }

            var result = Clone(basis);
            foreach (var pair in overlay.Fields)
            {
                result.Set(pair.Key, Merge(basis[pair.Key], pair.Value));
            }

            return result;
        }

        public static JsonNode Clone(JsonNode node)
        {
            if (node == null)
            {
                return JsonNode.Null();
            }

            JsonNode copy;
            return JsonNode.TryParse(node.ToJson(false), out copy) && copy != null ? copy : JsonNode.Null();
        }
    }
}
