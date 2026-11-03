using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace BlitzLegacy.Runtime.Security
{
    /// <summary>
    /// The online ban of a device caught editing memory.
    ///
    /// Photon Public Cloud has no server of ours that could keep a ban list,
    /// and a real hardware ban would need Photon Custom Authentication behind
    /// our own web endpoint. What a client-only game can do is:
    /// <list type="bullet">
    /// <item>remember the ban on this install, outside the save folder, so
    /// deleting the account or making a new one does not lift it;</item>
    /// <item>derive a salted hash of the device id (on Android the per-app
    /// ANDROID_ID, which survives a reinstall) and send it with the Sentry
    /// issue, so the device can be put on the shipped blocklist
    /// (<c>Resources/Data/banned_devices.txt</c>) of the next build.</item>
    /// </list>
    /// SECURITY: this raises the bar, it is not authoritative - clearing the
    /// app data lifts the local marker, and a spoofed device id lifts the
    /// blocklist entry.
    /// </summary>
    public static class AntiCheatBan
    {
        public const string BlocklistResource = "banned_devices";
        private const string PrefsKey = "blitz.ac.ban";
        private const string MarkerFile = ".blitz_ac";
        private const string Salt = "blitz-legacy-ac-v1:";

        private static string _deviceHash;
        private static bool? _blocklisted;

        /// <summary>Salted SHA-256 of the device id, 32 hex chars.</summary>
        public static string DeviceHash
        {
            get
            {
                if (_deviceHash == null) { _deviceHash = Hash(SystemInfo.deviceUniqueIdentifier); }
                return _deviceHash;
            }
        }

        public static bool IsBanned
        {
            get
            {
                if (PlayerPrefs.GetInt(PrefsKey, 0) == 1) { return true; }
                try { if (File.Exists(MarkerPath)) { return true; } }
                catch (Exception) { }
                if (_blocklisted == null) { _blocklisted = InBlocklist(DeviceHash); }
                return _blocklisted.Value;
            }
        }

        /// <summary>Persist the ban in two independent places.</summary>
        public static void Record(string reason)
        {
            PlayerPrefs.SetInt(PrefsKey, 1);
            PlayerPrefs.Save();
            try
            {
                File.WriteAllText(MarkerPath, DeviceHash + "\n" + (reason ?? string.Empty)
                    + "\n" + DateTime.UtcNow.ToString("o"));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Blitz] anti-cheat: ban marker not written: " + e.Message);
            }
        }

        public static string Hash(string deviceId)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Salt + (deviceId ?? string.Empty)));
                var builder = new StringBuilder(32);
                for (var i = 0; i < 16; i++) { builder.Append(bytes[i].ToString("x2")); }
                return builder.ToString();
            }
        }

        /// <summary>One hash per line; blank lines and '#' comments are ignored.</summary>
        public static bool ListContains(string list, string hash)
        {
            if (string.IsNullOrEmpty(list) || string.IsNullOrEmpty(hash)) { return false; }
            foreach (var raw in list.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') { continue; }
                if (string.Equals(line, hash, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        private static bool InBlocklist(string hash)
        {
            var asset = Resources.Load<TextAsset>("Data/" + BlocklistResource);
            return asset != null && ListContains(asset.text, hash);
        }

        private static string MarkerPath
        {
            get { return Path.Combine(Application.persistentDataPath, MarkerFile); }
        }
    }
}
