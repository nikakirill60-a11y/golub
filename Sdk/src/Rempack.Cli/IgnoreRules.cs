using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BlitzLegacy.Sdk
{
    /// <summary>
    /// Which files of a mod folder stay out of the package: editor sources by default,
    /// plus the patterns of <c>.rempackignore</c> in the folder's root, written like
    /// <c>.gitignore</c>: <c>*</c> and <c>?</c> stop at a slash, <c>**</c> does not,
    /// <c>[abc]</c> is a class, a trailing slash matches folders only, a slash elsewhere
    /// anchors the pattern to the root, <c>!</c> takes a file back and the last matching
    /// line wins. Matching ignores case, like package paths do.
    /// </summary>
    internal sealed class IgnoreRules
    {
        public const string FileName = ".rempackignore";

        /// <summary>Files no game loader reads: packages, editor scenes and layered images, backups.</summary>
        public static readonly string[] Defaults =
        {
            "*.rempack", "*.tmp", "*.bak", "*~", "*.blend", "*.blend[0-9]", "*.psd", "*.kra", "*.xcf", "*.spp", "*.sbs",
        };

        private readonly List<Rule> _rules = new List<Rule>();

        private sealed class Rule
        {
            public Regex Pattern;
            public bool Negated;
            public bool FoldersOnly;
        }

        public int Count
        {
            get { return _rules.Count; }
        }

        /// <summary>The defaults, then <c>.rempackignore</c> of <paramref name="root"/> when it has one.</summary>
        public static IgnoreRules Load(string root)
        {
            var rules = WithDefaults();
            var file = Path.Combine(root, FileName);
            if (File.Exists(file))
            {
                foreach (var line in File.ReadAllLines(file, new UTF8Encoding(false)))
                {
                    rules.Add(line);
                }
            }

            return rules;
        }

        public static IgnoreRules WithDefaults()
        {
            var rules = new IgnoreRules();
            foreach (var line in Defaults)
            {
                rules.Add(line);
            }

            return rules;
        }

        public void Add(string line)
        {
            var text = (line ?? string.Empty).TrimEnd();
            if (text.Length == 0 || text[0] == '#')
            {
                return;
            }

            var negated = false;
            if (text[0] == '!')
            {
                negated = true;
                text = text.Substring(1);
            }
            else if (text.StartsWith("\\!", StringComparison.Ordinal) || text.StartsWith("\\#", StringComparison.Ordinal))
            {
                text = text.Substring(1);
            }

            // Windows users write folders with backslashes; package paths only know '/'.
            text = text.Replace('\\', '/');
            var foldersOnly = text.EndsWith("/", StringComparison.Ordinal);
            text = text.TrimEnd('/');
            var anchored = text.IndexOf('/') >= 0;
            text = text.TrimStart('/');
            if (text.Length == 0)
            {
                return;
            }

            var expression = (anchored ? "^" : "(^|/)") + Translate(text) + "$";
            _rules.Add(new Rule
            {
                Pattern = new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                Negated = negated,
                FoldersOnly = foldersOnly,
            });
        }

        /// <summary>
        /// Whether <paramref name="path"/> (relative, with '/') is left out. The caller does
        /// not walk into a folder that is, so nothing inside it can be taken back.
        /// </summary>
        public bool IsIgnored(string path, bool folder)
        {
            var ignored = false;
            foreach (var rule in _rules)
            {
                if ((!rule.FoldersOnly || folder) && rule.Pattern.IsMatch(path))
                {
                    ignored = !rule.Negated;
                }
            }

            return ignored;
        }

        private static string Translate(string glob)
        {
            var regex = new StringBuilder(glob.Length * 2);
            for (var i = 0; i < glob.Length; i++)
            {
                var c = glob[i];
                if (c == '*')
                {
                    var doubled = i + 1 < glob.Length && glob[i + 1] == '*';
                    if (!doubled)
                    {
                        regex.Append("[^/]*");
                        continue;
                    }

                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        // "**/" is any number of folders, none included.
                        i++;
                        regex.Append("(.*/)?");
                    }
                    else
                    {
                        regex.Append(".*");
                    }

                    continue;
                }

                if (c == '?')
                {
                    regex.Append("[^/]");
                    continue;
                }

                if (c == '[')
                {
                    // "[]]" is a class of ']'; a '[' that never closes is itself.
                    var close = i + 2 < glob.Length ? glob.IndexOf(']', i + 2) : -1;
                    if (close > i)
                    {
                        var body = glob.Substring(i + 1, close - i - 1);
                        var negated = body.Length > 0 && (body[0] == '!' || body[0] == '^');
                        if (negated)
                        {
                            body = body.Substring(1);
                        }

                        regex.Append(negated ? "[^/" : "[");
                        foreach (var member in body)
                        {
                            regex.Append(member == '-' ? "-" : Regex.Escape(member.ToString()));
                        }

                        regex.Append(']');
                        i = close;
                        continue;
                    }
                }

                regex.Append(Regex.Escape(c.ToString()));
            }

            return regex.ToString();
        }
    }
}
