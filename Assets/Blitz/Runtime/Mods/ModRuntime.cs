using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Mods;
using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Mods
{
    /// <summary>
    /// The player-side home of the Godot mods: where they live on disk, which
    /// folders the install screen offers archives from, and how their loose
    /// PNG assets become textures.
    ///
    /// The Godot build kept mods next to the save file and re-read them at
    /// boot, so the port does the same under persistentDataPath/mods. Nothing
    /// is copied into the Unity project: a mod stays an unpacked Godot folder
    /// and the compatibility layer reads it in place.
    /// </summary>
    public static class ModRuntime
    {
        public const string FolderName = "mods";

        private static ModLibrary _library;
        private static ModCrashGuard _guard;

        /// <summary>persistentDataPath/mods, created on first use.</summary>
        public static string RootPath
        {
            get { return Path.Combine(Application.persistentDataPath, FolderName); }
        }

        public static ModLibrary Library
        {
            get
            {
                if (_library == null)
                {
                    _library = new ModLibrary(RootPath);
                    _library.Refresh();
                }

                return _library;
            }
        }

        /// <summary>Marks the mod whose content is loading, so a crash switches it off.</summary>
        public static ModCrashGuard Guard
        {
            get
            {
                if (_guard == null)
                {
                    _guard = new ModCrashGuard(RootPath);
                }

                return _guard;
            }
        }

        /// <summary>
        /// Runs once at start, before any mod loads: a mod that took the previous
        /// run down while loading is switched off and stays listed as crashed
        /// until the MODS screen has shown it.
        /// </summary>
        public static IReadOnlyList<string> Boot()
        {
            try
            {
                var crashed = Guard.Recover();
                if (crashed.Count > 0)
                {
                    Debug.LogWarning("Mods: switched off after a crash while loading: "
                        + string.Join(", ", crashed));
                }

                return crashed;
            }
            catch (Exception error)
            {
                Debug.LogWarning("Mods: " + error.Message);
                return Array.Empty<string>();
            }
        }

        /// <summary>Drops the cache so the next read picks up a fresh folder.</summary>
        public static void Forget()
        {
            _library = null;
            Icons.Clear();
            ModTankModel.Forget();
        }

        /// <summary>
        /// Adds every enabled mod card to a freshly parsed stock database.
        /// Call sites parse the shipped table first, so a mod can only add
        /// vehicles, never overwrite one.
        /// </summary>
        public static int Apply(TanksDatabase database)
        {
            try
            {
                var language = BlitzBootstrap.Current != null && BlitzBootstrap.Current.Settings != null
                    ? BlitzBootstrap.Current.Settings.Language : "en";
                foreach (var mod in Library.Mods)
                {
                    foreach (var vehicle in mod.Tanks)
                    {
                        if (vehicle.PortableCard == null) { continue; }
                        var name = language == "ru" && !string.IsNullOrEmpty(vehicle.RussianName)
                            ? vehicle.RussianName : vehicle.DefaultName;
                        vehicle.PortableCard.DisplayName = name;
                        vehicle.PortableCard.DisplayNameShort = name;
                    }
                }
                return Library.Apply(database);
            }
            catch (Exception error)
            {
                Debug.LogWarning("Mods: " + error.Message);
                return 0;
            }
        }

        /// <summary>
        /// Folders the install screen scans for archives: the mods folder
        /// itself, the app's own storage and the usual download directories of
        /// the desktop and Android builds.
        /// </summary>
        public static List<string> SearchFolders()
        {
            var folders = new List<string>();
            Add(folders, RootPath);
            Add(folders, Application.persistentDataPath);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                Add(folders, Path.Combine(home, "Downloads"));
                Add(folders, Path.Combine(home, "Загрузки"));
                Add(folders, home);
            }

            if (Application.platform == RuntimePlatform.Android)
            {
                Add(folders, "/sdcard/Download");
                Add(folders, "/storage/emulated/0/Download");
            }

            return folders;
        }

        /// <summary>Every archive the search folders hold, newest first.</summary>
        public static List<string> FindArchives()
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folders = SearchFolders();
            for (var i = 0; i < folders.Count; i++)
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(folders[i]);
                }
                catch (Exception)
                {
                    continue;
                }

                for (var f = 0; f < files.Length; f++)
                {
                    if (ModArchive.LooksLikeArchive(files[f]) && seen.Add(files[f]))
                    {
                        found.Add(files[f]);
                    }
                }
            }

            found.Sort(delegate(string left, string right)
            {
                var l = SafeWriteTime(left);
                var r = SafeWriteTime(right);
                return r.CompareTo(l);
            });
            return found;
        }

        /// <summary>A mod PNG loaded as a runtime texture, or null.</summary>
        public static Texture2D LoadTexture(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (!texture.LoadImage(File.ReadAllBytes(path), false))
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }

                texture.name = Path.GetFileNameWithoutExtension(path);
                return texture;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The same texture wrapped as a UI sprite.</summary>
        public static Sprite LoadSprite(string path)
        {
            var texture = LoadTexture(path);
            if (texture == null)
            {
                return null;
            }

            return Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f));
        }

        /// <summary>The mod card that owns <paramref name="tankName"/>, or null.</summary>
        public static ModVehicleAssets TankAssets(string tankName)
        {
            ModVehicleAssets tank;
            return FindTank(tankName, out tank) != null ? tank : null;
        }

        /// <summary>The id of the enabled mod that adds <paramref name="tankName"/>, or null.</summary>
        public static string TankModId(string tankName)
        {
            ModVehicleAssets tank;
            var mod = FindTank(tankName, out tank);
            return mod != null ? mod.Id : null;
        }

        private static InstalledMod FindTank(string tankName, out ModVehicleAssets tank)
        {
            tank = null;
            if (string.IsNullOrEmpty(tankName))
            {
                return null;
            }

            var mods = Library.Mods;
            for (var i = 0; i < mods.Count; i++)
            {
                if (!mods[i].Enabled)
                {
                    continue;
                }

                var tanks = mods[i].Tanks;
                for (var t = 0; t < tanks.Count; t++)
                {
                    if (string.Equals(tanks[t].Id, tankName, StringComparison.OrdinalIgnoreCase))
                    {
                        tank = tanks[t];
                        return mods[i];
                    }
                }
            }

            return null;
        }

        private static readonly Dictionary<string, Sprite> Icons =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The icon PNG a mod shipped for <paramref name="tankName"/>, cached; null when none.</summary>
        public static Sprite Icon(string tankName)
        {
            if (string.IsNullOrEmpty(tankName))
            {
                return null;
            }

            Sprite sprite;
            if (Icons.TryGetValue(tankName, out sprite) && sprite != null)
            {
                return sprite;
            }

            var tank = TankAssets(tankName);
            sprite = tank == null ? null : LoadSprite(tank.IconPath);
            Icons[tankName] = sprite;
            return sprite;
        }

        public static bool IsModTank(string tankName)
        {
            return TankAssets(tankName) != null;
        }

        private static DateTime SafeWriteTime(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        private static void Add(List<string> folders, string path)
        {
            if (string.IsNullOrEmpty(path) || folders.Contains(path))
            {
                return;
            }

            folders.Add(path);
        }
    }
}
