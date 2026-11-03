using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BlitzLegacy.Core.Mods
{
    /// <summary>
    /// Keeps a mod that crashes the game while it loads from doing it again. While a
    /// mod's content loads, its id sits in <c>.loading</c> next to the mods (written to
    /// disk before the load starts); a clean load removes it. If the game dies mid-load,
    /// the next start finds the marker, switches those mods off in <c>mods.json</c> and
    /// lists them as crashed until the MODS screen has told the player.
    /// </summary>
    public sealed class ModCrashGuard
    {
        public const string MarkerName = ".loading";
        private const int MaxIds = 256;

        private readonly object _gate = new object();
        private readonly Dictionary<string, int> _loading = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        public ModCrashGuard(string rootPath)
        {
            RootPath = rootPath ?? string.Empty;
        }

        public string RootPath { get; private set; }

        public string MarkerPath
        {
            get { return Path.Combine(RootPath, MarkerName); }
        }

        /// <summary>
        /// Call once at start, before any mod loads. Returns the ids the previous run was
        /// loading when it ended, now switched off; empty after a clean exit.
        /// </summary>
        public IReadOnlyList<string> Recover()
        {
            var ids = new List<string>();
            if (RootPath.Length == 0 || !File.Exists(MarkerPath))
            {
                return ids;
            }

            try
            {
                foreach (var line in File.ReadAllLines(MarkerPath, Encoding.UTF8))
                {
                    var id = line.Trim();
                    if (id.Length > 0 && id.Length <= 256 && !ids.Contains(id) && ids.Count < MaxIds)
                    {
                        ids.Add(id);
                    }
                }

                if (ids.Count > 0)
                {
                    ModState.Update(RootPath, delegate(ModState state)
                    {
                        foreach (var id in ids)
                        {
                            state.MarkCrashed(id);
                        }
                    });
                }

                File.Delete(MarkerPath);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // Unreadable or locked: better to retry next start than to guess.
            }

            return ids;
        }

        /// <summary>Marks <paramref name="id"/> as loading until the returned scope is disposed.</summary>
        public IDisposable Loading(string id)
        {
            Begin(id);
            return new Scope(this, id);
        }

        public void Begin(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            lock (_gate)
            {
                int count;
                _loading.TryGetValue(id, out count);
                _loading[id] = count + 1;
                if (count == 0)
                {
                    _order.Add(id);
                    Write();
                }
            }
        }

        public void End(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            lock (_gate)
            {
                int count;
                if (!_loading.TryGetValue(id, out count))
                {
                    return;
                }

                if (count > 1)
                {
                    _loading[id] = count - 1;
                    return;
                }

                _loading.Remove(id);
                _order.Remove(id);
                Write();
            }
        }

        /// <summary>Ids loading right now, oldest first.</summary>
        public IReadOnlyList<string> Current
        {
            get
            {
                lock (_gate)
                {
                    return _order.ToArray();
                }
            }
        }

        private void Write()
        {
            if (RootPath.Length == 0)
            {
                return;
            }

            try
            {
                if (_order.Count == 0)
                {
                    if (File.Exists(MarkerPath))
                    {
                        File.Delete(MarkerPath);
                    }

                    return;
                }

                Directory.CreateDirectory(RootPath);
                var temp = MarkerPath + ".tmp";
                var bytes = new UTF8Encoding(false).GetBytes(string.Join("\n", _order.ToArray()) + "\n");
                using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }

                ModState.Replace(temp, MarkerPath);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // Without the marker a crash goes unnoticed, which is how it was before.
            }
        }

        private sealed class Scope : IDisposable
        {
            private ModCrashGuard _guard;
            private readonly string _id;

            public Scope(ModCrashGuard guard, string id)
            {
                _guard = guard;
                _id = id;
            }

            public void Dispose()
            {
                var guard = _guard;
                _guard = null;
                if (guard != null)
                {
                    guard.End(_id);
                }
            }
        }
    }
}
