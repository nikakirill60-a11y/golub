using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Save
{
    /// <summary>In-memory <see cref="ISaveStorage"/> for tests and dry runs.</summary>
    public sealed class MemorySaveStorage : ISaveStorage
    {
        private readonly Dictionary<string, string> _files =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public int FileCount
        {
            get { return _files.Count; }
        }

        public bool Exists(string relativePath)
        {
            return relativePath != null && _files.ContainsKey(relativePath);
        }

        public string ReadAllText(string relativePath)
        {
            string contents;
            return _files.TryGetValue(relativePath ?? string.Empty, out contents)
                ? contents
                : string.Empty;
        }

        public void WriteAllText(string relativePath, string contents)
        {
            _files[relativePath] = contents ?? string.Empty;
        }

        public void Delete(string relativePath)
        {
            _files.Remove(relativePath ?? string.Empty);
        }

        public IEnumerable<string> ListFiles(string relativeDirectory, string extension)
        {
            var prefix = (relativeDirectory ?? string.Empty).TrimEnd('/') + "/";
            var matches = new List<string>();
            foreach (var path in _files.Keys)
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(extension) &&
                    !path.EndsWith(extension, StringComparison.Ordinal))
                {
                    continue;
                }

                matches.Add(path);
            }

            matches.Sort(StringComparer.Ordinal);
            return matches;
        }
    }
}
