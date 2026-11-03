// Minimal JSON reader for the exported Halloween hangar description. The
// exporter writes NaN / Infinity for a few particle values, which the Unity
// JsonUtility cannot read, so the hangar parses the file itself.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace BlitzLegacy.Runtime.Hangar.Halloween
{
    public sealed class HalloweenJson
    {
        public enum Kind { Missing, Null, Bool, Number, String, Array, Object }

        public static readonly HalloweenJson Missing = new HalloweenJson(Kind.Missing);
        static readonly List<HalloweenJson> NoItems = new List<HalloweenJson>();
        static readonly List<string> NoKeys = new List<string>();

        public readonly Kind kind;
        double number;
        string text;
        bool flag;
        List<HalloweenJson> items;
        Dictionary<string, HalloweenJson> fields;
        List<string> keys;

        HalloweenJson(Kind kind) { this.kind = kind; }

        public bool Exists => kind != Kind.Missing && kind != Kind.Null;
        public bool IsArray => kind == Kind.Array;
        public bool IsObject => kind == Kind.Object;
        public bool IsNumber => kind == Kind.Number;
        public bool IsString => kind == Kind.String;
        public int Count => kind == Kind.Array ? items.Count : kind == Kind.Object ? fields.Count : 0;
        public IReadOnlyList<HalloweenJson> Items => kind == Kind.Array ? items : NoItems;
        public IReadOnlyList<string> Keys => kind == Kind.Object ? keys : NoKeys;

        public HalloweenJson this[string key]
        {
            get
            {
                if (kind == Kind.Object && fields.TryGetValue(key, out var value))
                    return value;
                return Missing;
            }
        }

        public HalloweenJson this[int index] => kind == Kind.Array && index >= 0 && index < items.Count ? items[index] : Missing;

        public bool Has(string key) => this[key].Exists;

        // Required member: a missing key in the exported data is a broken export.
        public HalloweenJson Req(string key)
        {
            var value = this[key];
            if (!value.Exists)
                throw new InvalidOperationException("scene.json: missing required member '" + key + "'");
            return value;
        }

        public float F(float fallback = 0f)
        {
            if (kind == Kind.Number) return (float)number;
            if (kind == Kind.Bool) return flag ? 1f : 0f;
            return fallback;
        }

        public double D(double fallback = 0) => kind == Kind.Number ? number : fallback;

        public int I(int fallback = 0)
        {
            if (kind == Kind.Number) return (int)Math.Round(number);
            if (kind == Kind.Bool) return flag ? 1 : 0;
            return fallback;
        }

        public long L(long fallback = 0) => kind == Kind.Number ? (long)Math.Round(number) : fallback;

        public bool B(bool fallback = false)
        {
            if (kind == Kind.Bool) return flag;
            if (kind == Kind.Number) return Math.Abs(number) > 0;
            return fallback;
        }

        public string S(string fallback = null) => kind == Kind.String ? text : fallback;

        public float[] Floats()
        {
            if (kind == Kind.Number) return new[] { (float)number };
            if (kind != Kind.Array) return new float[0];
            var result = new float[items.Count];
            for (int i = 0; i < items.Count; i++)
                result[i] = items[i].F();
            return result;
        }

        public int[] Ints()
        {
            if (kind != Kind.Array) return new int[0];
            var result = new int[items.Count];
            for (int i = 0; i < items.Count; i++)
                result[i] = items[i].I();
            return result;
        }

        public Vector2 V2(Vector2 fallback = default)
        {
            var f = Floats();
            return f.Length >= 2 ? new Vector2(f[0], f[1]) : fallback;
        }

        public Vector3 V3(Vector3 fallback = default)
        {
            var f = Floats();
            return f.Length >= 3 ? new Vector3(f[0], f[1], f[2]) : fallback;
        }

        public Vector4 V4(Vector4 fallback = default)
        {
            var f = Floats();
            if (f.Length >= 4) return new Vector4(f[0], f[1], f[2], f[3]);
            if (f.Length == 3) return new Vector4(f[0], f[1], f[2], fallback.w);
            if (f.Length == 2) return new Vector4(f[0], f[1], fallback.z, fallback.w);
            if (f.Length == 1) return new Vector4(f[0], fallback.y, fallback.z, fallback.w);
            return fallback;
        }

        public Quaternion Q()
        {
            var f = Floats();
            if (f.Length != 4)
                throw new InvalidOperationException("scene.json: expected a quaternion");
            return new Quaternion(f[0], f[1], f[2], f[3]);
        }

        public Color C(Color fallback = default)
        {
            var f = Floats();
            if (f.Length >= 4) return new Color(f[0], f[1], f[2], f[3]);
            if (f.Length == 3) return new Color(f[0], f[1], f[2], 1f);
            return fallback;
        }

        // --------------------------------------------------------------- parser
        public static HalloweenJson Parse(string source)
        {
            var parser = new Parser(source);
            var value = parser.Value();
            parser.SkipSpace();
            if (!parser.AtEnd)
                throw parser.Error("trailing characters");
            return value;
        }

        sealed class Parser
        {
            readonly string s;
            int p;

            public Parser(string source) { s = source; }

            public bool AtEnd => p >= s.Length;

            public Exception Error(string message) =>
                new FormatException("JSON: " + message + " at offset " + p);

            public void SkipSpace()
            {
                while (p < s.Length && (s[p] == ' ' || s[p] == '\n' || s[p] == '\r' || s[p] == '\t'))
                    p++;
            }

            bool Match(string word)
            {
                if (string.CompareOrdinal(s, p, word, 0, word.Length) != 0)
                    return false;
                p += word.Length;
                return true;
            }

            public HalloweenJson Value()
            {
                SkipSpace();
                if (p >= s.Length)
                    throw Error("unexpected end");
                char c = s[p];
                switch (c)
                {
                    case '{': return Object();
                    case '[': return Array();
                    case '"': return new HalloweenJson(Kind.String) { text = String() };
                }
                if (Match("true")) return new HalloweenJson(Kind.Bool) { flag = true };
                if (Match("false")) return new HalloweenJson(Kind.Bool) { flag = false };
                if (Match("null")) return new HalloweenJson(Kind.Null);
                if (Match("NaN")) return new HalloweenJson(Kind.Number) { number = double.NaN };
                if (Match("Infinity")) return new HalloweenJson(Kind.Number) { number = double.PositiveInfinity };
                if (Match("-Infinity")) return new HalloweenJson(Kind.Number) { number = double.NegativeInfinity };
                return Number();
            }

            HalloweenJson Object()
            {
                p++;
                var node = new HalloweenJson(Kind.Object) { fields = new Dictionary<string, HalloweenJson>(), keys = new List<string>() };
                SkipSpace();
                if (p < s.Length && s[p] == '}') { p++; return node; }
                while (true)
                {
                    SkipSpace();
                    if (p >= s.Length || s[p] != '"')
                        throw Error("expected member name");
                    string key = String();
                    SkipSpace();
                    if (p >= s.Length || s[p] != ':')
                        throw Error("expected ':'");
                    p++;
                    var value = Value();
                    if (!node.fields.ContainsKey(key))
                        node.keys.Add(key);
                    node.fields[key] = value;
                    SkipSpace();
                    if (p >= s.Length)
                        throw Error("unterminated object");
                    if (s[p] == ',') { p++; continue; }
                    if (s[p] == '}') { p++; return node; }
                    throw Error("expected ',' or '}'");
                }
            }

            HalloweenJson Array()
            {
                p++;
                var node = new HalloweenJson(Kind.Array) { items = new List<HalloweenJson>() };
                SkipSpace();
                if (p < s.Length && s[p] == ']') { p++; return node; }
                while (true)
                {
                    node.items.Add(Value());
                    SkipSpace();
                    if (p >= s.Length)
                        throw Error("unterminated array");
                    if (s[p] == ',') { p++; continue; }
                    if (s[p] == ']') { p++; return node; }
                    throw Error("expected ',' or ']'");
                }
            }

            string String()
            {
                p++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (p >= s.Length)
                        throw Error("unterminated string");
                    char c = s[p++];
                    if (c == '"')
                        return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (p >= s.Length)
                        throw Error("unterminated escape");
                    char e = s[p++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (p + 4 > s.Length)
                                throw Error("bad unicode escape");
                            sb.Append((char)int.Parse(s.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            p += 4;
                            break;
                        default:
                            throw Error("bad escape");
                    }
                }
            }

            HalloweenJson Number()
            {
                int start = p;
                if (p < s.Length && (s[p] == '-' || s[p] == '+')) p++;
                while (p < s.Length && (char.IsDigit(s[p]) || s[p] == '.' || s[p] == 'e' || s[p] == 'E' || s[p] == '-' || s[p] == '+'))
                    p++;
                if (p == start)
                    throw Error("unexpected character '" + s[p] + "'");
                string token = s.Substring(start, p - start);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    throw Error("bad number '" + token + "'");
                return new HalloweenJson(Kind.Number) { number = value };
            }
        }
    }
}
