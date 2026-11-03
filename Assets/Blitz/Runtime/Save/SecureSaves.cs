using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Telemetry;
using UnityEngine;

namespace BlitzLegacy.Runtime.Save
{
    /// <summary>
    /// Where the player's saves live and how they are opened.
    ///
    /// Saves and settings go to the app's internal storage
    /// (<c>/data/data/&lt;package&gt;/files</c> on Android, which other apps and
    /// file managers cannot read) instead of <see cref="Application.persistentDataPath"/>
    /// (<c>Android/data/&lt;package&gt;/files</c>, which stays the home of mods and
    /// other non-secret files). Every save is sealed by <see cref="EncryptedSaveStorage"/>
    /// with a key from <see cref="SaveKeyRing"/>: wrapped by the Android Keystore
    /// and bound to the device id, so a save copied to another device, restored
    /// from a backup or edited by hand is wiped instead of loaded.
    ///
    /// The plain save folder of earlier builds is deleted on first start, so
    /// those files can never be read again; only the diagnostics opt-in moves over.
    /// </summary>
    public static class SecureSaves
    {
        /// <summary>The unencrypted save folder of builds before this one.</summary>
        public const string LegacySaveDirectory = "save";

        private static string _root;
        private static bool _migrated;
        private static FileSaveStorage _preferences;
        private static EncryptedSaveStorage _saves;

        /// <summary>Internal app storage on Android, persistentDataPath elsewhere.</summary>
        public static string Root
        {
            get
            {
                if (_root == null)
                {
                    _root = ResolveRoot();
                }

                return _root;
            }
        }

        /// <summary>Encrypted, device-bound storage for accounts, settings and presets.</summary>
        public static EncryptedSaveStorage Saves
        {
            get
            {
                if (_saves == null)
                {
                    EnsureMigrated();
                    SaveKeyResult key;
                    _saves = Open(Root, CreateProtector(), SystemInfo.deviceUniqueIdentifier, out key);
                    _saves.Rejected += path => Debug.LogWarning("[Blitz] saves: discarded unreadable " + path);
                    Debug.Log("[Blitz] saves: key=" + key.Mode
                        + (key.Wiped ? " wiped (" + key.Reason + ")" : string.Empty));
                }

                return _saves;
            }
        }

        /// <summary>
        /// Plain storage on the same root for files that must survive a save
        /// wipe and hold nothing worth protecting (the diagnostics opt-in).
        /// </summary>
        public static FileSaveStorage Preferences
        {
            get
            {
                if (_preferences == null)
                {
                    EnsureMigrated();
                    _preferences = new FileSaveStorage(Root);
                }

                return _preferences;
            }
        }

        /// <summary>Opens (or creates) the encrypted saves under <paramref name="root"/>.</summary>
        public static EncryptedSaveStorage Open(
            string root, ISaveKeyProtector protector, string deviceId, out SaveKeyResult key)
        {
            var files = new FileSaveStorage(root);
            key = SaveKeyRing.Open(files, protector, deviceId);
            return new EncryptedSaveStorage(files, new SaveCipher(key.MasterKey));
        }

        /// <summary>
        /// Deletes the plain save folder of earlier builds under the new root
        /// and every legacy root, and moves the diagnostics opt-in to the new
        /// root. Mods and everything else are left alone. Returns the number
        /// of save folders removed.
        /// </summary>
        public static int PurgeLegacy(string root, IList<string> legacyRoots)
        {
            var removed = 0;
            var roots = new List<string> { root };
            if (legacyRoots != null)
            {
                for (var i = 0; i < legacyRoots.Count; i++)
                {
                    if (!string.IsNullOrEmpty(legacyRoots[i]))
                    {
                        roots.Add(legacyRoots[i]);
                    }
                }
            }

            for (var i = 0; i < roots.Count; i++)
            {
                var directory = Path.Combine(roots[i], LegacySaveDirectory);
                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, true);
                        removed++;
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[Blitz] saves: could not delete " + directory + ": " + exception.Message);
                }
            }

            for (var i = 1; i < roots.Count; i++)
            {
                if (SamePath(roots[i], root))
                {
                    continue;
                }

                var old = Path.Combine(roots[i], TelemetryPolicy.PreferencePath);
                var fresh = Path.Combine(root, TelemetryPolicy.PreferencePath);
                try
                {
                    if (File.Exists(old))
                    {
                        if (!File.Exists(fresh))
                        {
                            Directory.CreateDirectory(root);
                            File.Copy(old, fresh);
                        }

                        File.Delete(old);
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[Blitz] saves: could not move " + old + ": " + exception.Message);
                }
            }

            return removed;
        }

        private static void EnsureMigrated()
        {
            if (_migrated)
            {
                return;
            }

            _migrated = true;
            var removed = PurgeLegacy(Root, new[] { Application.persistentDataPath });
            if (removed > 0)
            {
                Debug.Log("[Blitz] saves: removed " + removed + " unencrypted save folder(s) of an older build");
            }
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                var left = Path.GetFullPath(a).TrimEnd('/', '\\');
                var right = Path.GetFullPath(b).TrimEnd('/', '\\');
                return string.Equals(left, right, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return string.Equals(a, b, StringComparison.Ordinal);
            }
        }

        private static ISaveKeyProtector CreateProtector()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidKeystoreProtector();
#else
            return null;
#endif
        }

        private static string ResolveRoot()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var directory = activity.Call<AndroidJavaObject>("getFilesDir"))
                {
                    var path = directory.Call<string>("getAbsolutePath");
                    if (!string.IsNullOrEmpty(path))
                    {
                        return path;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Blitz] saves: getFilesDir failed: " + exception.Message);
            }

            var persistent = Application.persistentDataPath;
            if (!string.IsNullOrEmpty(persistent) &&
                persistent.IndexOf("/Android/data/", StringComparison.Ordinal) < 0)
            {
                return persistent;
            }

            return "/data/data/" + Application.identifier + "/files";
#else
            return Application.persistentDataPath;
#endif
        }
    }
}
