using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Save
{
    /// <summary>
    /// Wraps a save data key so only this device can unwrap it (on Android:
    /// an Android Keystore key that never leaves the device).
    /// </summary>
    public interface ISaveKeyProtector
    {
        /// <summary>Wrapped form of a fresh data key, or null when unavailable.</summary>
        string Protect(byte[] key);

        bool TryUnprotect(string wrapped, out byte[] key);
    }

    /// <summary>Outcome of <see cref="SaveKeyRing.Open"/>.</summary>
    public sealed class SaveKeyResult
    {
        public byte[] MasterKey { get; internal set; }

        /// <summary>"keystore" when wrapped by the protector, "device" otherwise.</summary>
        public string Mode { get; internal set; }

        /// <summary>True when existing save files were deleted because the key could not be opened.</summary>
        public bool Wiped { get; internal set; }

        /// <summary>Why a new key was made ("missing", "unwrap", ...), or empty.</summary>
        public string Reason { get; internal set; }
    }

    /// <summary>
    /// The per-device save key. A random data key is kept in
    /// <see cref="KeyPath"/>, wrapped by the platform protector, and the key
    /// the saves are actually sealed with is that data key bound to the
    /// device id. Whenever the key file is missing, cannot be unwrapped (the
    /// keystore key is gone: another device, a restored backup, a reinstall)
    /// or does not match this device, every file under the save directory is
    /// deleted and a new key is made: those saves can never be opened again.
    /// </summary>
    public static class SaveKeyRing
    {
        public const string KeyPath = SaveService.SaveDirectory + "/device.key";
        public const string ModeKeystore = "keystore";
        public const string ModeDevice = "device";

        private const int FormatVersion = 1;

        public static SaveKeyResult Open(
            ISaveStorage storage, ISaveKeyProtector protector, string deviceId,
            Func<int, byte[]> random = null)
        {
            if (storage == null)
            {
                throw new ArgumentNullException("storage");
            }

            string reason;
            var existing = TryLoad(storage, protector, deviceId, out reason);
            if (existing != null)
            {
                return existing;
            }

            var wiped = Wipe(storage);
            var raw = (random ?? RandomBytes)(SaveCipher.KeySize);
            string wrapped = null;
            if (protector != null)
            {
                try
                {
                    wrapped = protector.Protect(raw);
                }
                catch (Exception)
                {
                    wrapped = null;
                }
            }

            var mode = string.IsNullOrEmpty(wrapped) ? ModeDevice : ModeKeystore;
            var master = Bind(raw, deviceId);
            var node = JsonNode.NewObject()
                .Set("v", FormatVersion)
                .Set("mode", mode)
                .Set("key", mode == ModeKeystore ? wrapped : Convert.ToBase64String(raw))
                .Set("check", Check(master));
            storage.WriteAllText(KeyPath, node.ToJson(false));
            return new SaveKeyResult { MasterKey = master, Mode = mode, Wiped = wiped, Reason = reason };
        }

        /// <summary>Deletes every file under the save directories; true if any save existed.</summary>
        public static bool Wipe(ISaveStorage storage)
        {
            var any = false;
            var files = new List<string>();
            files.AddRange(storage.ListFiles(SaveService.AccountsDirectory, null));
            files.AddRange(storage.ListFiles(SaveService.SaveDirectory, null));
            for (var i = 0; i < files.Count; i++)
            {
                if (!string.Equals(files[i], KeyPath, StringComparison.Ordinal))
                {
                    any = true;
                }

                storage.Delete(files[i]);
            }

            return any;
        }

        private static SaveKeyResult TryLoad(
            ISaveStorage storage, ISaveKeyProtector protector, string deviceId, out string reason)
        {
            if (!storage.Exists(KeyPath))
            {
                reason = "missing";
                return null;
            }

            JsonNode node;
            if (!JsonNode.TryParse(storage.ReadAllText(KeyPath), out node) ||
                node.Kind != JsonKind.Object || node["v"].AsInt() != FormatVersion)
            {
                reason = "corrupt";
                return null;
            }

            var mode = node["mode"].AsString();
            var key = node["key"].AsString();
            byte[] raw = null;
            if (mode == ModeKeystore)
            {
                try
                {
                    if (protector == null || !protector.TryUnprotect(key, out raw))
                    {
                        raw = null;
                    }
                }
                catch (Exception)
                {
                    raw = null;
                }

                if (raw == null)
                {
                    reason = "unwrap";
                    return null;
                }
            }
            else if (mode == ModeDevice)
            {
                try
                {
                    raw = Convert.FromBase64String(key);
                }
                catch (FormatException)
                {
                    raw = null;
                }
            }

            if (raw == null || raw.Length != SaveCipher.KeySize)
            {
                reason = "corrupt";
                return null;
            }

            var master = Bind(raw, deviceId);
            if (!string.Equals(Check(master), node["check"].AsString(), StringComparison.Ordinal))
            {
                reason = "device";
                return null;
            }

            reason = string.Empty;
            return new SaveKeyResult { MasterKey = master, Mode = mode, Wiped = false, Reason = string.Empty };
        }

        private static byte[] Bind(byte[] raw, string deviceId)
        {
            return SaveCipher.Derive(raw, "blitz-save-device-v1:" + (deviceId ?? string.Empty));
        }

        private static string Check(byte[] master)
        {
            var hash = SaveCipher.Derive(master, "blitz-save-key-check-v1");
            var builder = new StringBuilder(hash.Length * 2);
            for (var i = 0; i < hash.Length; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }

            return builder.ToString();
        }

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var random = new RNGCryptoServiceProvider())
            {
                random.GetBytes(bytes);
            }

            return bytes;
        }
    }
}
