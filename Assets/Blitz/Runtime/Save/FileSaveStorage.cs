using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Save;

namespace BlitzLegacy.Runtime.Save
{
    /// <summary>
    /// Maps Core save paths onto a folder on disk (plain text). The player's
    /// saves go through <see cref="SecureSaves"/>, which picks the folder and
    /// layers encryption on top.
    /// </summary>
    public sealed class FileSaveStorage : ISaveStorage
    {
        private readonly string _root;

        public FileSaveStorage(string root)
        {
            _root = root;
        }

        public string Root
        {
            get { return _root; }
        }

        public bool Exists(string relativePath)
        {
            return File.Exists(Absolute(relativePath));
        }

        public string ReadAllText(string relativePath)
        {
            var path = Absolute(relativePath);
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }

        public void WriteAllText(string relativePath, string contents)
        {
            var path = Absolute(relativePath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write-then-move so a kill mid-save cannot truncate an existing save.
            var temp = path + ".tmp";
            File.WriteAllText(temp, contents ?? string.Empty);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);
        }

        public void Delete(string relativePath)
        {
            var path = Absolute(relativePath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public IEnumerable<string> ListFiles(string relativeDirectory, string extension)
        {
            var directory = Absolute(relativeDirectory);
            var results = new List<string>();
            if (!Directory.Exists(directory))
            {
                return results;
            }

            var pattern = string.IsNullOrEmpty(extension) ? "*" : "*" + extension;
            var files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
            for (var i = 0; i < files.Length; i++)
            {
                var name = Path.GetFileName(files[i]);
                results.Add(relativeDirectory.TrimEnd('/') + "/" + name);
            }

            results.Sort(System.StringComparer.Ordinal);
            return results;
        }

        private string Absolute(string relativePath)
        {
            return Path.Combine(_root, (relativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
