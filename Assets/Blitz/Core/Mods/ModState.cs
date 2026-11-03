using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Mods
{
    /// <summary>
    /// <c>mods.json</c> next to the installed mods: which mods are switched on, the order
    /// Remaster Packages load in (a later package wins a contested stock replacement) and
    /// the mods a crash switched off, until the MODS screen has shown them. It replaces
    /// <c>mods.txt</c>, the list of switched-off legacy mods, and imports it once.
    ///
    /// Every change is read-modify-write through <see cref="Update"/>, so the legacy and
    /// the package libraries never overwrite each other's changes, and the file is
    /// replaced atomically, so a kill mid-write keeps the previous state.
    /// </summary>
    public sealed class ModState
    {
        public const string FileName = "mods.json";
        public const string LegacyFileName = "mods.txt";
        public const string BrokenSuffix = ".broken";
        public const int FormatVersion = 1;
        private const int MaxEntries = 4096;

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<string> _crashed = new List<string>();

        private sealed class Entry
        {
            public string Id;
            public bool Enabled;
        }

        /// <summary>True when the file could not be read and was set aside as <c>mods.json.broken</c>.</summary>
        public bool Recovered { get; private set; }

        /// <summary>True when this load imported <c>mods.txt</c>.</summary>
        public bool Migrated { get; private set; }

        /// <summary>Every remembered id in order (packages and legacy mods).</summary>
        public IReadOnlyList<string> Ids
        {
            get
            {
                var ids = new List<string>(_entries.Count);
                foreach (var entry in _entries)
                {
                    ids.Add(entry.Id);
                }

                return ids;
            }
        }

        /// <summary>Mods a crash during their load switched off, not yet shown to the player.</summary>
        public IReadOnlyList<string> Crashed
        {
            get { return _crashed; }
        }

        public static string PathIn(string root)
        {
            return Path.Combine(root, FileName);
        }

        /// <summary>
        /// Reads the state of <paramref name="root"/>; a missing folder or file is an empty
        /// state, a damaged file is set aside and read as empty (<see cref="Recovered"/>).
        /// A file that exists but stays locked throws <see cref="IOException"/>: an empty
        /// state saved over it would switch every mod back on.
        /// </summary>
        public static ModState Load(string root)
        {
            var state = new ModState();
            if (string.IsNullOrEmpty(root))
            {
                return state;
            }

            var path = PathIn(root);
            var temp = path + ".tmp";
            if (!File.Exists(path) && File.Exists(temp))
            {
                // The previous save wrote the new file but died before the rename.
                TryMove(temp, path);
            }

            if (File.Exists(path))
            {
                if (!state.Read(ReadText(path)))
                {
                    state._entries.Clear();
                    state._crashed.Clear();
                    state.Recovered = true;
                    TryMove(path, path + BrokenSuffix);
                }

                return state;
            }

            var legacy = Path.Combine(root, LegacyFileName);
            if (File.Exists(legacy))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(legacy))
                    {
                        var id = line.Trim();
                        if (id.Length > 0 && state.Find(id) == null && state._entries.Count < MaxEntries)
                        {
                            state._entries.Add(new Entry { Id = id, Enabled = false });
                        }
                    }

                    state.Migrated = true;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return state;
        }

        /// <summary>
        /// Loads, changes and saves in one step. <c>mods.txt</c> is deleted only after the
        /// imported state is safely on disk.
        /// </summary>
        public static ModState Update(string root, Action<ModState> change)
        {
            var state = Load(root);
            change(state);
            state.Save(root);
            return state;
        }

        public void Save(string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            Directory.CreateDirectory(root);
            var path = PathIn(root);
            var temp = path + ".tmp";
            var bytes = new UTF8Encoding(false).GetBytes(ToJson().ToJson(true) + "\n");
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }

            Replace(temp, path);
            var legacy = Path.Combine(root, LegacyFileName);
            if (Migrated && File.Exists(legacy))
            {
                try
                {
                    File.Delete(legacy);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        public bool IsKnown(string id)
        {
            return Find(id) != null;
        }

        /// <summary>Mods start switched on; only an explicit switch-off is remembered as off.</summary>
        public bool IsEnabled(string id)
        {
            var entry = Find(id);
            return entry == null || entry.Enabled;
        }

        public void SetEnabled(string id, bool enabled)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            var entry = Find(id);
            if (entry == null)
            {
                if (_entries.Count >= MaxEntries)
                {
                    return;
                }

                entry = new Entry { Id = id };
                _entries.Add(entry);
            }

            entry.Enabled = enabled;
            if (enabled)
            {
                _crashed.Remove(id);
            }
        }

        /// <summary>Remembers a new mod at the end of the load order, switched on.</summary>
        public void Append(string id)
        {
            if (!string.IsNullOrEmpty(id) && Find(id) == null && _entries.Count < MaxEntries)
            {
                _entries.Add(new Entry { Id = id, Enabled = true });
            }
        }

        public void Forget(string id)
        {
            var entry = Find(id);
            if (entry != null)
            {
                _entries.Remove(entry);
            }

            _crashed.Remove(id);
        }

        /// <summary>Position in the load order, or -1.</summary>
        public int IndexOf(string id)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (string.Equals(_entries[i].Id, id, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Moves <paramref name="id"/> so that it loads right before <paramref name="before"/> (null: last).</summary>
        public void MoveBefore(string id, string before)
        {
            var entry = Find(id);
            if (entry == null || string.Equals(id, before, StringComparison.Ordinal))
            {
                return;
            }

            _entries.Remove(entry);
            var at = before == null ? -1 : IndexOf(before);
            if (at < 0)
            {
                _entries.Add(entry);
            }
            else
            {
                _entries.Insert(at, entry);
            }
        }

        public void MarkCrashed(string id)
        {
            SetEnabled(id, false);
            if (!_crashed.Contains(id))
            {
                _crashed.Add(id);
            }
        }

        public void ClearCrashed()
        {
            _crashed.Clear();
        }

        public JsonNode ToJson()
        {
            var root = JsonNode.NewObject();
            root.Set("format", FormatVersion);
            var mods = JsonNode.NewArray();
            foreach (var entry in _entries)
            {
                var item = JsonNode.NewObject();
                item.Set("id", entry.Id);
                item.Set("enabled", entry.Enabled);
                mods.Add(item);
            }

            root.Set("mods", mods);
            var crashed = JsonNode.NewArray();
            foreach (var id in _crashed)
            {
                crashed.Add(JsonNode.Str(id));
            }

            root.Set("crashed", crashed);
            return root;
        }

        private bool Read(string text)
        {
            JsonNode root;
            if (!JsonNode.TryParse(text, out root) || root == null || root.Kind != JsonKind.Object
                || root["format"].Kind != JsonKind.Number || root["format"].AsInt() < 1)
            {
                return false;
            }

            var mods = root["mods"];
            if (mods.Kind != JsonKind.Array)
            {
                return false;
            }

            foreach (var item in mods.Items)
            {
                var id = item["id"].AsString();
                if (item.Kind != JsonKind.Object || id.Length == 0 || id.Length > 256 || Find(id) != null)
                {
                    continue;
                }

                if (_entries.Count < MaxEntries)
                {
                    _entries.Add(new Entry { Id = id, Enabled = item["enabled"].AsBool(true) });
                }
            }

            var crashed = root["crashed"];
            if (crashed.Kind == JsonKind.Array)
            {
                foreach (var item in crashed.Items)
                {
                    var id = item.AsString();
                    if (id.Length > 0 && id.Length <= 256 && !_crashed.Contains(id) && _crashed.Count < MaxEntries)
                    {
                        _crashed.Add(id);
                    }
                }
            }

            return true;
        }

        /// <summary>A virus scanner or a second game process may hold the file for a moment.</summary>
        private static string ReadText(string path)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return File.ReadAllText(path, Encoding.UTF8);
                }
                catch (IOException) when (attempt < 3 && File.Exists(path))
                {
                    System.Threading.Thread.Sleep(50);
                }
            }
        }

        private Entry Find(string id)
        {
            var at = IndexOf(id);
            return at < 0 ? null : _entries[at];
        }

        /// <summary>Atomic where the platform allows it: rename over the old file.</summary>
        internal static void Replace(string source, string target)
        {
            if (!File.Exists(target))
            {
                File.Move(source, target);
                return;
            }

            try
            {
                File.Replace(source, target, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Delete(target);
                File.Move(source, target);
            }
        }

        private static void TryMove(string source, string target)
        {
            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(source, target);
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
