using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Mods.Rempack
{
    /// <summary>
    /// <c>.cache/verified.json</c>: packages whose every file already matched its SHA-256,
    /// keyed by file name, size, modification time and the manifest's own hash. Hashing a
    /// large package takes seconds on a phone, so the library does it once per file
    /// instead of on every start; any change to the file brings the full check back.
    /// </summary>
    internal sealed class RempackVerifiedCache
    {
        public const string Folder = ".cache";
        public const string FileName = "verified.json";
        private const int FormatVersion = 1;
        private const int MaxEntries = 4096;

        private readonly string _path;
        private readonly Dictionary<string, Stamp> _stamps = new Dictionary<string, Stamp>(StringComparer.Ordinal);
        private bool _dirty;

        private struct Stamp
        {
            public long Size;
            public long Modified;
            public string Manifest;
        }

        private RempackVerifiedCache(string path)
        {
            _path = path;
        }

        public static string PathIn(string root)
        {
            return Path.Combine(Path.Combine(root, Folder), FileName);
        }

        /// <summary>A missing or unreadable cache is an empty one: the worst case is hashing again.</summary>
        public static RempackVerifiedCache Load(string root)
        {
            var cache = new RempackVerifiedCache(PathIn(root));
            try
            {
                if (!File.Exists(cache._path))
                {
                    return cache;
                }

                JsonNode node;
                if (!JsonNode.TryParse(File.ReadAllText(cache._path, Encoding.UTF8), out node) || node == null
                    || node.Kind != JsonKind.Object || node["format"].AsInt() != FormatVersion)
                {
                    return cache;
                }

                foreach (var pair in node["files"].Fields)
                {
                    var manifest = pair.Value["manifest"].AsString();
                    if (cache._stamps.Count >= MaxEntries || !RempackManifest.IsSha256(manifest))
                    {
                        continue;
                    }

                    cache._stamps[pair.Key] = new Stamp
                    {
                        Size = pair.Value["size"].AsLong(-1),
                        Modified = pair.Value["modified"].AsLong(-1),
                        Manifest = manifest,
                    };
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return cache;
        }

        public bool IsVerified(string fileName, long size, long modified, string manifestHash)
        {
            Stamp stamp;
            return _stamps.TryGetValue(fileName, out stamp) && stamp.Size == size && stamp.Modified == modified
                && string.Equals(stamp.Manifest, manifestHash, StringComparison.Ordinal);
        }

        public void Remember(string fileName, long size, long modified, string manifestHash)
        {
            if (IsVerified(fileName, size, modified, manifestHash) || (_stamps.Count >= MaxEntries && !_stamps.ContainsKey(fileName)))
            {
                return;
            }

            _stamps[fileName] = new Stamp { Size = size, Modified = modified, Manifest = manifestHash };
            _dirty = true;
        }

        public void Forget(string fileName)
        {
            if (_stamps.Remove(fileName))
            {
                _dirty = true;
            }
        }

        /// <summary>Drops the files that are gone, then writes the cache if anything changed.</summary>
        public void Save(ICollection<string> present)
        {
            var gone = new List<string>();
            foreach (var name in _stamps.Keys)
            {
                if (present != null && !present.Contains(name))
                {
                    gone.Add(name);
                }
            }

            foreach (var name in gone)
            {
                Forget(name);
            }

            if (!_dirty)
            {
                return;
            }

            var root = JsonNode.NewObject();
            root.Set("format", FormatVersion);
            var files = JsonNode.NewObject();
            var names = new List<string>(_stamps.Keys);
            names.Sort(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var stamp = _stamps[name];
                var item = JsonNode.NewObject();
                item.Set("size", stamp.Size);
                item.Set("modified", stamp.Modified);
                item.Set("manifest", stamp.Manifest);
                files.Set(name, item);
            }

            root.Set("files", files);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var temp = _path + ".tmp";
                File.WriteAllText(temp, root.ToJson(true) + "\n", new UTF8Encoding(false));
                ModState.Replace(temp, _path);
                _dirty = false;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
