using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BlitzLegacy.Core.Mods.Rempack
{
    /// <summary>
    /// A semantic version (<c>1.4.2</c>, <c>2.0.0-beta.1</c>). One or two numbers are
    /// accepted and padded with zeros, because modders write <c>1.0</c> as often as
    /// <c>1.0.0</c>; build metadata after <c>+</c> is kept but never compared.
    /// </summary>
    public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
    {
        private readonly string[] _pre;

        public SemanticVersion(int major, int minor, int patch, string prerelease = null, string build = null)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Prerelease = prerelease ?? string.Empty;
            Build = build ?? string.Empty;
            _pre = Prerelease.Length == 0 ? new string[0] : Prerelease.Split('.');
        }

        public int Major { get; private set; }
        public int Minor { get; private set; }
        public int Patch { get; private set; }
        public string Prerelease { get; private set; }
        public string Build { get; private set; }

        public static SemanticVersion Parse(string text)
        {
            SemanticVersion version;
            if (!TryParse(text, out version))
            {
                throw new FormatException("Not a semantic version: " + text);
            }

            return version;
        }

        public static bool TryParse(string text, out SemanticVersion version)
        {
            version = null;
            if (string.IsNullOrEmpty(text) || text.Length > 64)
            {
                return false;
            }

            var build = string.Empty;
            var plus = text.IndexOf('+');
            if (plus >= 0)
            {
                build = text.Substring(plus + 1);
                text = text.Substring(0, plus);
                if (!Identifiers(build, false))
                {
                    return false;
                }
            }

            var pre = string.Empty;
            var dash = text.IndexOf('-');
            if (dash >= 0)
            {
                pre = text.Substring(dash + 1);
                text = text.Substring(0, dash);
                if (!Identifiers(pre, true))
                {
                    return false;
                }
            }

            var parts = text.Split('.');
            if (parts.Length < 1 || parts.Length > 3)
            {
                return false;
            }

            var numbers = new int[3];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!Number(parts[i], out numbers[i]))
                {
                    return false;
                }
            }

            version = new SemanticVersion(numbers[0], numbers[1], numbers[2], pre, build);
            return true;
        }

        /// <summary>
        /// The numeric head of a game build string such as <c>2.0b1</c> or <c>2.1.3-rc</c>;
        /// everything after the numbers is ignored. Null when there is no number at all.
        /// </summary>
        public static SemanticVersion FromGameVersion(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var numbers = new int[3];
            var index = 0;
            var digits = 0;
            long value = 0;
            for (var i = 0; i <= text.Length && index < 3; i++)
            {
                var c = i < text.Length ? text[i] : '\0';
                if (c >= '0' && c <= '9')
                {
                    value = Math.Min(int.MaxValue, (value * 10) + (c - '0'));
                    digits++;
                    continue;
                }

                if (digits == 0)
                {
                    break;
                }

                numbers[index++] = (int)value;
                value = 0;
                digits = 0;
                if (c != '.')
                {
                    break;
                }
            }

            return index == 0 ? null : new SemanticVersion(numbers[0], numbers[1], numbers[2]);
        }

        public int CompareTo(SemanticVersion other)
        {
            if (ReferenceEquals(other, null))
            {
                return 1;
            }

            var c = Major.CompareTo(other.Major);
            if (c != 0) { return c; }
            c = Minor.CompareTo(other.Minor);
            if (c != 0) { return c; }
            c = Patch.CompareTo(other.Patch);
            if (c != 0) { return c; }

            // A release outranks any of its pre-releases.
            if (_pre.Length == 0 || other._pre.Length == 0)
            {
                return other._pre.Length.CompareTo(_pre.Length) == 0 ? 0 : (_pre.Length == 0 ? 1 : -1);
            }

            for (var i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
            {
                int left, right;
                var leftNumber = int.TryParse(_pre[i], NumberStyles.None, CultureInfo.InvariantCulture, out left);
                var rightNumber = int.TryParse(other._pre[i], NumberStyles.None, CultureInfo.InvariantCulture, out right);
                if (leftNumber && rightNumber)
                {
                    c = left.CompareTo(right);
                }
                else if (leftNumber != rightNumber)
                {
                    c = leftNumber ? -1 : 1;
                }
                else
                {
                    c = string.CompareOrdinal(_pre[i], other._pre[i]);
                }

                if (c != 0)
                {
                    return c < 0 ? -1 : 1;
                }
            }

            return _pre.Length.CompareTo(other._pre.Length);
        }

        public bool Equals(SemanticVersion other)
        {
            return !ReferenceEquals(other, null) && CompareTo(other) == 0;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as SemanticVersion);
        }

        public override int GetHashCode()
        {
            return (Major * 397) ^ (Minor * 31) ^ Patch ^ Prerelease.GetHashCode();
        }

        public static bool operator <(SemanticVersion left, SemanticVersion right) { return Compare(left, right) < 0; }
        public static bool operator >(SemanticVersion left, SemanticVersion right) { return Compare(left, right) > 0; }
        public static bool operator <=(SemanticVersion left, SemanticVersion right) { return Compare(left, right) <= 0; }
        public static bool operator >=(SemanticVersion left, SemanticVersion right) { return Compare(left, right) >= 0; }

        private static int Compare(SemanticVersion left, SemanticVersion right)
        {
            if (ReferenceEquals(left, null))
            {
                return ReferenceEquals(right, null) ? 0 : -1;
            }

            return left.CompareTo(right);
        }

        public override string ToString()
        {
            var text = new StringBuilder();
            text.Append(Major).Append('.').Append(Minor).Append('.').Append(Patch);
            if (Prerelease.Length > 0) { text.Append('-').Append(Prerelease); }
            if (Build.Length > 0) { text.Append('+').Append(Build); }
            return text.ToString();
        }

        private static bool Number(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || text.Length > 9 || (text.Length > 1 && text[0] == '0'))
            {
                return false;
            }

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                {
                    return false;
                }

                value = (value * 10) + (text[i] - '0');
            }

            return true;
        }

        private static bool Identifiers(string text, bool numericNoZero)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (var part in text.Split('.'))
            {
                if (part.Length == 0)
                {
                    return false;
                }

                var numeric = true;
                for (var i = 0; i < part.Length; i++)
                {
                    var c = part[i];
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-'))
                    {
                        return false;
                    }

                    numeric &= c >= '0' && c <= '9';
                }

                if (numericNoZero && numeric && part.Length > 1 && part[0] == '0')
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// A version requirement as npm writes it: <c>*</c>, <c>1.2.3</c>, <c>&gt;=1.2 &lt;2</c>,
    /// <c>^1.2</c>, <c>~1.2.3</c>, <c>1.x</c>, alternatives joined by <c>||</c>.
    /// </summary>
    public sealed class VersionRange
    {
        private readonly List<List<KeyValuePair<string, SemanticVersion>>> _sets;

        private VersionRange(string text, List<List<KeyValuePair<string, SemanticVersion>>> sets)
        {
            Text = text;
            _sets = sets;
        }

        public string Text { get; private set; }

        public static readonly VersionRange Any =
            new VersionRange("*", new List<List<KeyValuePair<string, SemanticVersion>>> { new List<KeyValuePair<string, SemanticVersion>>() });

        public static bool TryParse(string text, out VersionRange range)
        {
            range = null;
            if (text == null || text.Length > 128)
            {
                return false;
            }

            var trimmed = text.Trim();
            if (trimmed.Length == 0 || trimmed == "*")
            {
                range = Any;
                return true;
            }

            var sets = new List<List<KeyValuePair<string, SemanticVersion>>>();
            foreach (var alternative in trimmed.Split(new[] { "||" }, StringSplitOptions.None))
            {
                var set = new List<KeyValuePair<string, SemanticVersion>>();
                var tokens = alternative.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                {
                    return false;
                }

                for (var t = 0; t < tokens.Length; t++)
                {
                    // ">= 1.2" is written as often as ">=1.2".
                    var token = tokens[t];
                    if (IsOperator(token) && t + 1 < tokens.Length)
                    {
                        token += tokens[++t];
                    }

                    if (!AddComparator(token, set))
                    {
                        return false;
                    }
                }

                sets.Add(set);
            }

            range = new VersionRange(trimmed, sets);
            return true;
        }

        public bool Matches(SemanticVersion version)
        {
            if (version == null)
            {
                return false;
            }

            foreach (var set in _sets)
            {
                var ok = true;
                foreach (var comparator in set)
                {
                    var c = version.CompareTo(comparator.Value);
                    switch (comparator.Key)
                    {
                        case ">=": ok &= c >= 0; break;
                        case ">": ok &= c > 0; break;
                        case "<=": ok &= c <= 0; break;
                        case "<": ok &= c < 0; break;
                        default: ok &= c == 0; break;
                    }
                }

                if (ok)
                {
                    return true;
                }
            }

            return false;
        }

        public override string ToString()
        {
            return Text;
        }

        private static bool AddComparator(string token, List<KeyValuePair<string, SemanticVersion>> set)
        {
            if (token == "*")
            {
                return true;
            }

            string op;
            if (token.StartsWith(">=", StringComparison.Ordinal) || token.StartsWith("<=", StringComparison.Ordinal))
            {
                op = token.Substring(0, 2);
            }
            else if (token[0] == '>' || token[0] == '<' || token[0] == '=' || token[0] == '^' || token[0] == '~')
            {
                op = token.Substring(0, 1);
            }
            else
            {
                op = string.Empty;
            }

            var body = token.Substring(op.Length);
            if (body.Length == 0)
            {
                return false;
            }

            // Wildcards: 1.x, 1.2.*, 1 -> a range over the missing parts.
            var parts = body.Split('.');
            var given = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "x" || parts[i] == "X" || parts[i] == "*")
                {
                    break;
                }

                given++;
            }

            if (given < parts.Length && (op.Length > 0 && op != "="))
            {
                return false;
            }

            var head = string.Join(".", parts, 0, Math.Max(given, 1));
            if (given == 0)
            {
                return true;
            }

            SemanticVersion version;
            if (!SemanticVersion.TryParse(head, out version))
            {
                return false;
            }

            var partial = given < 3 && body.IndexOf('-') < 0;
            switch (op)
            {
                case ">=":
                case ">":
                case "<=":
                case "<":
                    if (partial && (op == ">" || op == "<="))
                    {
                        // ">1.2" means above every 1.2.x; "<=1.2" includes every 1.2.x.
                        var next = Bump(version, given);
                        set.Add(new KeyValuePair<string, SemanticVersion>(op == ">" ? ">=" : "<", next));
                    }
                    else
                    {
                        set.Add(new KeyValuePair<string, SemanticVersion>(op, version));
                    }

                    return true;
                case "^":
                    set.Add(new KeyValuePair<string, SemanticVersion>(">=", version));
                    set.Add(new KeyValuePair<string, SemanticVersion>("<", version.Major > 0 || given == 1
                        ? new SemanticVersion(version.Major + 1, 0, 0)
                        : version.Minor > 0 || given == 2
                            ? new SemanticVersion(0, version.Minor + 1, 0)
                            : new SemanticVersion(0, 0, version.Patch + 1)));
                    return true;
                case "~":
                    set.Add(new KeyValuePair<string, SemanticVersion>(">=", version));
                    set.Add(new KeyValuePair<string, SemanticVersion>("<", given == 1
                        ? new SemanticVersion(version.Major + 1, 0, 0)
                        : new SemanticVersion(version.Major, version.Minor + 1, 0)));
                    return true;
                default:
                    if (partial)
                    {
                        set.Add(new KeyValuePair<string, SemanticVersion>(">=", version));
                        set.Add(new KeyValuePair<string, SemanticVersion>("<", Bump(version, given)));
                    }
                    else
                    {
                        set.Add(new KeyValuePair<string, SemanticVersion>("=", version));
                    }

                    return true;
            }
        }

        private static bool IsOperator(string token)
        {
            return token == ">=" || token == "<=" || token == ">" || token == "<" || token == "=" || token == "^" || token == "~";
        }

        private static SemanticVersion Bump(SemanticVersion version, int given)
        {
            return given == 1
                ? new SemanticVersion(version.Major + 1, 0, 0)
                : new SemanticVersion(version.Major, version.Minor + 1, 0);
        }
    }
}
