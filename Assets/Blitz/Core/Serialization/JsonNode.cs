using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BlitzLegacy.Core.Serialization
{
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>
    /// Dependency-free JSON tree. Kept engine-free so the save format can be
    /// exercised by plain unit tests without booting Unity.
    /// </summary>
    public sealed class JsonNode
    {
        private static readonly Dictionary<string, JsonNode> EmptyFields =
            new Dictionary<string, JsonNode>(StringComparer.Ordinal);

        private static readonly JsonNode[] EmptyItems = new JsonNode[0];

        // Missing lookups are immutable: Set/Add reject Null and Kind has no public setter.
        // Keep explicit Null() factory semantics while eliminating hot-path fallback allocations.
        private static readonly JsonNode Missing = new JsonNode(JsonKind.Null);

        private bool _bool;
        private double _number;
        private long _integer;
        private bool _exactInteger;
        private string _string;
        private List<JsonNode> _array;
        private Dictionary<string, JsonNode> _object;

        private JsonNode(JsonKind kind)
        {
            Kind = kind;
        }

        public JsonKind Kind { get; private set; }

        public static JsonNode Null()
        {
            return new JsonNode(JsonKind.Null);
        }

        public static JsonNode Bool(bool value)
        {
            return new JsonNode(JsonKind.Bool) { _bool = value };
        }

        public static JsonNode Num(double value)
        {
            return new JsonNode(JsonKind.Number) { _number = value };
        }

        /// <summary>
        /// An integer kept exact. A double only holds integers up to 2^53, so
        /// a 64-bit currency balance would otherwise be rounded on save.
        /// </summary>
        public static JsonNode Num(long value)
        {
            return new JsonNode(JsonKind.Number)
            {
                _number = value,
                _integer = value,
                _exactInteger = true,
            };
        }

        public static JsonNode Str(string value)
        {
            if (value == null)
            {
                return Null();
            }

            return new JsonNode(JsonKind.String) { _string = value };
        }

        public static JsonNode NewArray()
        {
            return new JsonNode(JsonKind.Array) { _array = new List<JsonNode>() };
        }

        public static JsonNode NewObject()
        {
            return new JsonNode(JsonKind.Object)
            {
                _object = new Dictionary<string, JsonNode>(StringComparer.Ordinal),
            };
        }

        public bool IsNull
        {
            get { return Kind == JsonKind.Null; }
        }

        public int Count
        {
            get
            {
                if (Kind == JsonKind.Array)
                {
                    return _array.Count;
                }

                if (Kind == JsonKind.Object)
                {
                    return _object.Count;
                }

                return 0;
            }
        }

        public IReadOnlyList<JsonNode> Items
        {
            get { return Kind == JsonKind.Array ? (IReadOnlyList<JsonNode>)_array : EmptyItems; }
        }

        public IEnumerable<KeyValuePair<string, JsonNode>> Fields
        {
            get { return Kind == JsonKind.Object ? _object : EmptyFields; }
        }

        public JsonNode this[string key]
        {
            get { return Get(key); }
            set { Set(key, value); }
        }

        public JsonNode this[int index]
        {
            get
            {
                if (Kind != JsonKind.Array || index < 0 || index >= _array.Count)
                {
                    return Missing;
                }

                return _array[index];
            }
        }

        public bool Has(string key)
        {
            return Kind == JsonKind.Object && key != null && _object.ContainsKey(key);
        }

        public JsonNode Get(string key)
        {
            JsonNode found;
            if (Kind == JsonKind.Object && key != null && _object.TryGetValue(key, out found))
            {
                return found;
            }

            return Missing;
        }

        public JsonNode Set(string key, JsonNode value)
        {
            if (Kind != JsonKind.Object)
            {
                throw new InvalidOperationException("Set requires a JSON object.");
            }

            _object[key] = value ?? Null();
            return this;
        }

        public JsonNode Set(string key, string value)
        {
            return Set(key, Str(value ?? string.Empty));
        }

        public JsonNode Set(string key, bool value)
        {
            return Set(key, Bool(value));
        }

        public JsonNode Set(string key, int value)
        {
            return Set(key, Num(value));
        }

        public JsonNode Set(string key, long value)
        {
            return Set(key, Num(value));
        }

        public JsonNode Set(string key, double value)
        {
            return Set(key, Num(value));
        }

        public JsonNode Add(JsonNode value)
        {
            if (Kind != JsonKind.Array)
            {
                throw new InvalidOperationException("Add requires a JSON array.");
            }

            _array.Add(value ?? Null());
            return this;
        }

        public bool AsBool(bool fallback = false)
        {
            if (Kind == JsonKind.Bool)
            {
                return _bool;
            }

            if (Kind == JsonKind.Number)
            {
                return Math.Abs(_number) > double.Epsilon;
            }

            return fallback;
        }

        public double AsDouble(double fallback = 0d)
        {
            return Kind == JsonKind.Number ? _number : fallback;
        }

        public float AsFloat(float fallback = 0f)
        {
            return (float)AsDouble(fallback);
        }

        public int AsInt(int fallback = 0)
        {
            return Kind == JsonKind.Number ? (int)Math.Round(_number) : fallback;
        }

        public long AsLong(long fallback = 0L)
        {
            if (Kind != JsonKind.Number)
            {
                return fallback;
            }

            return _exactInteger ? _integer : SaturatedLong(Math.Round(_number));
        }

        /// <summary>
        /// Converts a double to a long, clamping values outside the long range
        /// instead of relying on the unspecified overflow of a plain cast.
        /// </summary>
        private static long SaturatedLong(double value)
        {
            if (double.IsNaN(value))
            {
                return 0L;
            }

            if (value >= LongRangeEnd)
            {
                return long.MaxValue;
            }

            return value < -LongRangeEnd ? long.MinValue : (long)value;
        }

        // 2^63: the first double past long.MaxValue (which itself rounds up to it).
        private const double LongRangeEnd = 9223372036854775808d;

        public string AsString(string fallback = "")
        {
            return Kind == JsonKind.String ? _string : fallback;
        }

        public string ToJson(bool pretty = true)
        {
            var sb = new StringBuilder(256);
            Write(sb, pretty, 0);
            return sb.ToString();
        }

        public override string ToString()
        {
            return ToJson(false);
        }

        // ------------------------------------------------------------- writer
        private void Write(StringBuilder sb, bool pretty, int depth)
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    return;
                case JsonKind.Bool:
                    sb.Append(_bool ? "true" : "false");
                    return;
                case JsonKind.Number:
                    if (_exactInteger)
                    {
                        sb.Append(_integer.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        WriteNumber(sb, _number);
                    }

                    return;
                case JsonKind.String:
                    WriteString(sb, _string);
                    return;
                case JsonKind.Array:
                    WriteArray(sb, pretty, depth);
                    return;
                case JsonKind.Object:
                    WriteObject(sb, pretty, depth);
                    return;
                default:
                    throw new InvalidOperationException("Unknown JSON kind: " + Kind);
            }
        }

        private void WriteArray(StringBuilder sb, bool pretty, int depth)
        {
            if (_array.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            sb.Append('[');
            for (var i = 0; i < _array.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                Indent(sb, pretty, depth + 1);
                _array[i].Write(sb, pretty, depth + 1);
            }

            Indent(sb, pretty, depth);
            sb.Append(']');
        }

        private void WriteObject(StringBuilder sb, bool pretty, int depth)
        {
            if (_object.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            // Stable key order keeps saves byte-comparable and diff-friendly.
            var keys = new List<string>(_object.Keys);
            keys.Sort(StringComparer.Ordinal);

            sb.Append('{');
            for (var i = 0; i < keys.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                Indent(sb, pretty, depth + 1);
                WriteString(sb, keys[i]);
                sb.Append(':');
                if (pretty)
                {
                    sb.Append(' ');
                }

                _object[keys[i]].Write(sb, pretty, depth + 1);
            }

            Indent(sb, pretty, depth);
            sb.Append('}');
        }

        private static void Indent(StringBuilder sb, bool pretty, int depth)
        {
            if (!pretty)
            {
                return;
            }

            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static void WriteNumber(StringBuilder sb, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                sb.Append('0');
                return;
            }

            if (Math.Abs(value % 1d) < double.Epsilon &&
                value >= -LongRangeEnd &&
                value < LongRangeEnd)
            {
                sb.Append(((long)value).ToString(CultureInfo.InvariantCulture));
                return;
            }

            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            var text = value ?? string.Empty;
            sb.Append('"');
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }

        // ------------------------------------------------------------- parser
        public static JsonNode Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException("text");
            }

            var index = 0;
            var node = ParseValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index != text.Length)
            {
                throw Error(index, "trailing characters");
            }

            return node;
        }

        public static bool TryParse(string text, out JsonNode node)
        {
            try
            {
                node = Parse(text);
                return true;
            }
            catch (ArgumentNullException)
            {
                node = Null();
                return false;
            }
            catch (FormatException)
            {
                node = Null();
                return false;
            }
        }

        private static JsonNode ParseValue(string text, ref int i)
        {
            SkipWhitespace(text, ref i);
            if (i >= text.Length)
            {
                throw Error(i, "unexpected end of input");
            }

            var c = text[i];
            switch (c)
            {
                case '{':
                    return ParseObject(text, ref i);
                case '[':
                    return ParseArray(text, ref i);
                case '"':
                    return Str(ParseString(text, ref i));
                case 't':
                    ExpectLiteral(text, ref i, "true");
                    return Bool(true);
                case 'f':
                    ExpectLiteral(text, ref i, "false");
                    return Bool(false);
                case 'n':
                    ExpectLiteral(text, ref i, "null");
                    return Null();
                default:
                    return ParseNumber(text, ref i);
            }
        }

        private static JsonNode ParseObject(string text, ref int i)
        {
            var node = NewObject();
            i++; // '{'
            SkipWhitespace(text, ref i);
            if (i < text.Length && text[i] == '}')
            {
                i++;
                return node;
            }

            while (true)
            {
                SkipWhitespace(text, ref i);
                var key = ParseString(text, ref i);
                SkipWhitespace(text, ref i);
                Expect(text, ref i, ':');
                node.Set(key, ParseValue(text, ref i));
                SkipWhitespace(text, ref i);
                if (i >= text.Length)
                {
                    throw Error(i, "unterminated object");
                }

                if (text[i] == ',')
                {
                    i++;
                    continue;
                }

                Expect(text, ref i, '}');
                return node;
            }
        }

        private static JsonNode ParseArray(string text, ref int i)
        {
            var node = NewArray();
            i++; // '['
            SkipWhitespace(text, ref i);
            if (i < text.Length && text[i] == ']')
            {
                i++;
                return node;
            }

            while (true)
            {
                node.Add(ParseValue(text, ref i));
                SkipWhitespace(text, ref i);
                if (i >= text.Length)
                {
                    throw Error(i, "unterminated array");
                }

                if (text[i] == ',')
                {
                    i++;
                    continue;
                }

                Expect(text, ref i, ']');
                return node;
            }
        }

        private static string ParseString(string text, ref int i)
        {
            Expect(text, ref i, '"');
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= text.Length)
                {
                    throw Error(i, "unterminated string");
                }

                var c = text[i++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i >= text.Length)
                {
                    throw Error(i, "unterminated escape");
                }

                var escape = text[i++];
                switch (escape)
                {
                    case '"':
                        sb.Append('"');
                        break;
                    case '\\':
                        sb.Append('\\');
                        break;
                    case '/':
                        sb.Append('/');
                        break;
                    case 'b':
                        sb.Append('\b');
                        break;
                    case 'f':
                        sb.Append('\f');
                        break;
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case 'u':
                        if (i + 4 > text.Length)
                        {
                            throw Error(i, "short unicode escape");
                        }

                        sb.Append((char)int.Parse(
                            text.Substring(i, 4),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default:
                        throw Error(i, "unknown escape");
                }
            }
        }

        private static readonly char[] FractionOrExponent = { '.', 'e', 'E' };

        /// <summary>
        /// Integer literals that fit a long stay exact; anything with a
        /// fraction or an exponent (or past the long range) is a double.
        /// </summary>
        private static JsonNode ParseNumber(string text, ref int i)
        {
            var start = i;
            while (i < text.Length)
            {
                var c = text[i];
                var isNumeric = (c >= '0' && c <= '9') ||
                                c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E';
                if (!isNumeric)
                {
                    break;
                }

                i++;
            }

            var slice = text.Substring(start, i - start);
            long integer;
            if (slice.IndexOfAny(FractionOrExponent) < 0 &&
                long.TryParse(slice, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer))
            {
                return Num(integer);
            }

            double parsed;
            if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                throw Error(start, "invalid number");
            }

            return Num(parsed);
        }

        private static void SkipWhitespace(string text, ref int i)
        {
            while (i < text.Length)
            {
                var c = text[i];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                {
                    return;
                }

                i++;
            }
        }

        private static void Expect(string text, ref int i, char expected)
        {
            SkipWhitespace(text, ref i);
            if (i >= text.Length || text[i] != expected)
            {
                throw Error(i, "expected '" + expected + "'");
            }

            i++;
        }

        private static void ExpectLiteral(string text, ref int i, string literal)
        {
            if (i + literal.Length > text.Length ||
                string.CompareOrdinal(text, i, literal, 0, literal.Length) != 0)
            {
                throw Error(i, "expected '" + literal + "'");
            }

            i += literal.Length;
        }

        private static FormatException Error(int index, string message)
        {
            return new FormatException("JSON error at index " + index + ": " + message);
        }
    }
}
