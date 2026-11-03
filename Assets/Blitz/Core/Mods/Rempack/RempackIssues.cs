using System;
using System.Collections.Generic;
using System.Text;

namespace BlitzLegacy.Core.Mods.Rempack
{
    public enum RempackSeverity
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>
    /// One finding about a package: a stable code, the file it concerns and the values
    /// the message needs. The game and the SDK print the same text for the same code.
    /// </summary>
    public sealed class RempackIssue
    {
        public RempackIssue(RempackSeverity severity, string code, string path, params string[] args)
        {
            Severity = severity;
            Code = code ?? string.Empty;
            Path = path ?? string.Empty;
            Args = args ?? new string[0];
        }

        public RempackSeverity Severity { get; private set; }
        public string Code { get; private set; }
        public string Path { get; private set; }
        public string[] Args { get; private set; }

        /// <summary>
        /// English technical detail (which ZIP structure broke, an OS error) for the log and
        /// the CLI's verbose output; the localized text never depends on it.
        /// </summary>
        public string Detail { get; private set; }

        public RempackIssue WithDetail(string detail)
        {
            Detail = detail ?? string.Empty;
            return this;
        }

        /// <summary>The localized line: <c>path: text</c>. See <see cref="RempackMessages"/>.</summary>
        public string Format(Func<string, string> lookup)
        {
            var text = RempackMessages.Format(Code, Args, lookup);
            return string.IsNullOrEmpty(Path) ? text : Path + ": " + text;
        }

        /// <summary>The log line, always English: severity, code, path, arguments and detail.</summary>
        public override string ToString()
        {
            return Severity.ToString().ToLowerInvariant() + " " + Code + (Path.Length > 0 ? " [" + Path + "]" : string.Empty)
                + (Args.Length > 0 ? " (" + string.Join(", ", Args) + ")" : string.Empty)
                + (string.IsNullOrEmpty(Detail) ? string.Empty : " - " + Detail);
        }
    }

    /// <summary>Thrown where a check cannot continue; carries the issue it stopped on.</summary>
    public sealed class RempackException : Exception
    {
        public RempackException(string code, string path, params string[] args)
            : this(new RempackIssue(RempackSeverity.Error, code, path, args))
        {
        }

        private RempackException(RempackIssue issue)
            : base(issue.ToString())
        {
            Issue = issue;
        }

        /// <summary>An error with an English technical detail for the log.</summary>
        public static RempackException Detailed(string code, string path, string detail, params string[] args)
        {
            return new RempackException(new RempackIssue(RempackSeverity.Error, code, path, args).WithDetail(detail));
        }

        public RempackIssue Issue { get; private set; }

        public string Code
        {
            get { return Issue.Code; }
        }
    }

    /// <summary>Everything a validation pass found, errors first when printed.</summary>
    public sealed class RempackReport
    {
        /// <summary>Kept per kind: errors, and everything else.</summary>
        public const int MaxIssues = 500;

        private readonly List<RempackIssue> _issues = new List<RempackIssue>();
        private int _errors;

        public IReadOnlyList<RempackIssue> Issues
        {
            get { return _issues; }
        }

        public bool HasErrors
        {
            get { return _errors > 0; }
        }

        /// <summary>Issues past <see cref="MaxIssues"/> that were counted but not kept.</summary>
        public int Dropped { get; private set; }

        public int Count(RempackSeverity severity)
        {
            var count = 0;
            for (var i = 0; i < _issues.Count; i++)
            {
                if (_issues[i].Severity == severity)
                {
                    count++;
                }
            }

            return count;
        }

        public bool Has(string code)
        {
            for (var i = 0; i < _issues.Count; i++)
            {
                if (_issues[i].Code == code)
                {
                    return true;
                }
            }

            return false;
        }

        public RempackIssue FirstError
        {
            get
            {
                for (var i = 0; i < _issues.Count; i++)
                {
                    if (_issues[i].Severity == RempackSeverity.Error)
                    {
                        return _issues[i];
                    }
                }

                return null;
            }
        }

        public void Error(string code, string path, params string[] args)
        {
            Add(new RempackIssue(RempackSeverity.Error, code, path, args));
        }

        public void Warning(string code, string path, params string[] args)
        {
            Add(new RempackIssue(RempackSeverity.Warning, code, path, args));
        }

        public void Info(string code, string path, params string[] args)
        {
            Add(new RempackIssue(RempackSeverity.Info, code, path, args));
        }

        public void Add(RempackIssue issue)
        {
            if (issue == null)
            {
                return;
            }

            // A broken file tends to repeat the same complaint per element; a few
            // hundred lines are plenty to fix it, and keep a hostile package cheap.
            // Errors have their own allowance, so no flood of warnings can push the
            // one error that refuses a package out of the report.
            var error = issue.Severity == RempackSeverity.Error;
            if ((error ? _errors : _issues.Count - _errors) >= MaxIssues)
            {
                Dropped++;
                return;
            }

            _issues.Add(issue);
            if (error)
            {
                _errors++;
            }
        }

        public void AddRange(RempackReport other)
        {
            if (other == null)
            {
                return;
            }

            for (var i = 0; i < other._issues.Count; i++)
            {
                Add(other._issues[i]);
            }
        }

        /// <summary>Localized lines, errors first, each prefixed with its severity word.</summary>
        public string Format(Func<string, string> lookup)
        {
            var text = new StringBuilder();
            foreach (var severity in new[] { RempackSeverity.Error, RempackSeverity.Warning, RempackSeverity.Info })
            {
                var key = RempackMessages.SeverityKey(severity);
                var word = lookup == null ? null : lookup(key);
                if (string.IsNullOrEmpty(word) || word == key)
                {
                    word = severity.ToString().ToLowerInvariant();
                }

                for (var i = 0; i < _issues.Count; i++)
                {
                    if (_issues[i].Severity == severity)
                    {
                        text.Append(word).Append(": ").Append(_issues[i].Format(lookup)).Append('\n');
                    }
                }
            }

            return text.ToString();
        }

        /// <summary>English log lines (codes, not texts), errors first.</summary>
        public override string ToString()
        {
            var text = new StringBuilder();
            foreach (var severity in new[] { RempackSeverity.Error, RempackSeverity.Warning, RempackSeverity.Info })
            {
                for (var i = 0; i < _issues.Count; i++)
                {
                    if (_issues[i].Severity == severity)
                    {
                        text.Append(_issues[i]).Append('\n');
                    }
                }
            }

            return text.ToString();
        }
    }

    /// <summary>
    /// Paths inside a package. Only portable names survive every file system the game
    /// and the SDK run on: Latin letters, digits, dot, dash and underscore, forward
    /// slashes, no hidden or reserved names, compared without case.
    /// </summary>
    public static class RempackPaths
    {
        private static readonly string[] Reserved =
        {
            "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8",
            "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
        };

        /// <summary>Returns the path unchanged when it is safe; throws otherwise.</summary>
        public static string Validate(string path)
        {
            if (!IsValid(path))
            {
                throw new RempackException(RempackCodes.PathInvalid, path ?? string.Empty);
            }

            return path;
        }

        public static bool IsValid(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > RempackLimits.PathLength)
            {
                return false;
            }

            var segments = path.Split('/');
            for (var s = 0; s < segments.Length; s++)
            {
                var segment = segments[s];
                if (segment.Length == 0 || segment.Length > RempackLimits.SegmentLength || segment[0] == '.'
                    || segment[segment.Length - 1] == '.')
                {
                    return false;
                }

                for (var i = 0; i < segment.Length; i++)
                {
                    var c = segment[i];
                    var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                        || c == '.' || c == '_' || c == '-';
                    if (!ok)
                    {
                        return false;
                    }
                }

                var stem = segment;
                var dot = stem.IndexOf('.');
                if (dot > 0)
                {
                    stem = stem.Substring(0, dot);
                }

                if (Array.IndexOf(Reserved, stem.ToLowerInvariant()) >= 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Resolves <paramref name="relative"/> against the folder of <paramref name="file"/>
        /// (both package paths). <c>../</c> may climb inside the package but never out of it.
        /// Returns null for anything that does not stay a valid package path.
        /// </summary>
        public static string Resolve(string file, string relative)
        {
            if (string.IsNullOrEmpty(relative) || relative.IndexOf('\\') >= 0 || relative.IndexOf(':') >= 0
                || relative.StartsWith("/", StringComparison.Ordinal))
            {
                return null;
            }

            var parts = new List<string>();
            var slash = string.IsNullOrEmpty(file) ? -1 : file.LastIndexOf('/');
            if (slash > 0)
            {
                parts.AddRange(file.Substring(0, slash).Split('/'));
            }

            foreach (var part in relative.Split('/'))
            {
                if (part == "." || part.Length == 0)
                {
                    continue;
                }

                if (part == "..")
                {
                    if (parts.Count == 0)
                    {
                        return null;
                    }

                    parts.RemoveAt(parts.Count - 1);
                    continue;
                }

                parts.Add(Uri.UnescapeDataString(part));
            }

            var joined = string.Join("/", parts.ToArray());
            return IsValid(joined) ? joined : null;
        }

        /// <summary>Files archivers add on their own; the reader skips them instead of failing.</summary>
        public static bool IsJunk(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            if (path.StartsWith("__MACOSX/", StringComparison.Ordinal))
            {
                return true;
            }

            var slash = path.LastIndexOf('/');
            var name = slash >= 0 ? path.Substring(slash + 1) : path;
            return name == ".DS_Store" || string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for a name that cannot escape: no rooted, drive or parent segments.</summary>
        public static bool IsContained(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0
                || path.StartsWith("/", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var part in path.Split('/'))
            {
                if (part == "..")
                {
                    return false;
                }
            }

            return true;
        }
    }
}
