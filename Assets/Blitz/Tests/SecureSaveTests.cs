using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Sandbox;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Telemetry;
using BlitzLegacy.Runtime.Save;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class SecureSaveTests
    {
        private const double Now = 1757000000d;

        private static byte[] Key(byte seed)
        {
            var key = new byte[SaveCipher.KeySize];
            for (var i = 0; i < key.Length; i++)
            {
                key[i] = (byte)(seed + i * 7);
            }

            return key;
        }

        private static AccountBook BookWithSasha()
        {
            var book = new AccountBook();
            Assert.IsTrue(book.CreateAccount("Sasha", "login1", "pass1", Now).Ok);
            return book;
        }

        /// <summary>Stands in for the Android Keystore: only keys it wrapped itself unwrap.</summary>
        private sealed class FakeKeystore : ISaveKeyProtector
        {
            private readonly Dictionary<string, byte[]> _keys = new Dictionary<string, byte[]>();
            private int _next;

            public bool Unavailable;

            public string Protect(byte[] key)
            {
                if (Unavailable)
                {
                    return null;
                }

                _keys.Clear();
                var handle = "wrapped-" + (++_next);
                _keys[handle] = (byte[])key.Clone();
                return handle;
            }

            public bool TryUnprotect(string wrapped, out byte[] key)
            {
                key = null;
                byte[] stored;
                if (Unavailable || wrapped == null || !_keys.TryGetValue(wrapped, out stored))
                {
                    return false;
                }

                key = (byte[])stored.Clone();
                return true;
            }
        }

        private sealed class ThrowingKeystore : ISaveKeyProtector
        {
            public string Protect(byte[] key)
            {
                throw new InvalidOperationException("no keystore");
            }

            public bool TryUnprotect(string wrapped, out byte[] key)
            {
                throw new InvalidOperationException("no keystore");
            }
        }

        [Test]
        public void SaveLayout_UsesTheNewEncryptedFolder()
        {
            Assert.AreEqual("saves", SaveService.SaveDirectory);
            Assert.AreNotEqual(SecureSaves.LegacySaveDirectory, SaveService.SaveDirectory);
            StringAssert.StartsWith(SaveService.SaveDirectory + "/", SaveService.MainPath);
            StringAssert.StartsWith(SaveService.SaveDirectory + "/", SaveService.SettingsPath);
            StringAssert.StartsWith(SaveService.SaveDirectory + "/", SaveService.AccountsDirectory);
            StringAssert.StartsWith(SaveService.SaveDirectory + "/", SandboxPresetStore.PresetsPath);
            StringAssert.StartsWith(SaveService.SaveDirectory + "/", SaveKeyRing.KeyPath);
            StringAssert.EndsWith(".sav", SaveService.MainPath);
            StringAssert.DoesNotStartWith(SaveService.SaveDirectory, TelemetryPolicy.PreferencePath);
        }

        [Test]
        public void Cipher_RoundTripsAndRejectsAnythingElse()
        {
            var cipher = new SaveCipher(Key(1));
            var plain = Encoding.UTF8.GetBytes("{\"credits\":1}");
            var blob = cipher.Seal("saves/main.sav", plain);
            byte[] opened;

            Assert.IsTrue(cipher.TryOpen("saves/main.sav", blob, out opened));
            CollectionAssert.AreEqual(plain, opened);

            var tampered = (byte[])blob.Clone();
            tampered[tampered.Length / 2] ^= 0x01;
            Assert.IsFalse(cipher.TryOpen("saves/main.sav", tampered, out opened), "edited file");
            Assert.IsFalse(cipher.TryOpen("saves/settings.sav", blob, out opened), "file moved to another slot");
            Assert.IsFalse(new SaveCipher(Key(2)).TryOpen("saves/main.sav", blob, out opened), "another key");
            Assert.IsFalse(cipher.TryOpen("saves/main.sav", plain, out opened), "plain text");
            Assert.IsFalse(cipher.TryOpen("saves/main.sav", new byte[0], out opened), "empty file");
            var truncated = new byte[blob.Length - 1];
            Buffer.BlockCopy(blob, 0, truncated, 0, truncated.Length);
            Assert.IsFalse(cipher.TryOpen("saves/main.sav", truncated, out opened), "truncated file");
        }

        [Test]
        public void Cipher_UsesAFreshIvForEverySeal()
        {
            var cipher = new SaveCipher(Key(3));
            var plain = Encoding.UTF8.GetBytes("same text");
            var first = cipher.Seal("saves/a.sav", plain);
            var second = cipher.Seal("saves/a.sav", plain);

            CollectionAssert.AreNotEqual(first, second);
            byte[] opened;
            Assert.IsTrue(cipher.TryOpen("saves/a.sav", second, out opened));
            CollectionAssert.AreEqual(plain, opened);
        }

        [Test]
        public void EncryptedStorage_RoundTripsSavesWithoutPlainText()
        {
            var inner = new MemorySaveStorage();
            var service = new SaveService(new EncryptedSaveStorage(inner, new SaveCipher(Key(4))));
            service.SaveBook(BookWithSasha());
            service.SaveSettings(new SettingsData());

            foreach (var path in inner.ListFiles(SaveService.SaveDirectory, null))
            {
                var raw = inner.ReadAllText(path);
                StringAssert.DoesNotContain("Sasha", raw, path);
                StringAssert.DoesNotContain("login1", raw, path);
                StringAssert.DoesNotStartWith("{", raw, path);
            }

            var reopened = new SaveService(new EncryptedSaveStorage(inner, new SaveCipher(Key(4))));
            var book = reopened.LoadBook();
            Assert.AreEqual(1, book.Count);
            Assert.IsTrue(book.Has("login1:pass1"));
            Assert.IsNotNull(reopened.LoadSettings());
            Assert.IsTrue(inner.Exists(SaveService.SettingsPath));
        }

        [Test]
        public void EncryptedStorage_DeletesPlainAndForeignSaves()
        {
            var inner = new MemorySaveStorage();
            var plainService = new SaveService(inner);
            plainService.SaveBook(BookWithSasha());
            plainService.SaveSettings(new SettingsData());

            var storage = new EncryptedSaveStorage(inner, new SaveCipher(Key(5)));
            var rejected = new List<string>();
            storage.Rejected += rejected.Add;
            var service = new SaveService(storage);

            Assert.AreEqual(0, service.LoadBook().Count, "a plain save must never load");
            Assert.IsNotNull(service.LoadSettings());
            Assert.AreEqual(3, storage.RejectedCount);
            Assert.AreEqual(3, rejected.Count);
            Assert.IsFalse(inner.Exists(SaveService.MainPath));
            Assert.IsFalse(inner.Exists(SaveService.SettingsPath));
            CollectionAssert.IsEmpty(inner.ListFiles(SaveService.AccountsDirectory, null));

            new SaveService(new EncryptedSaveStorage(inner, new SaveCipher(Key(6)))).SaveBook(BookWithSasha());
            Assert.AreEqual(0, service.LoadBook().Count, "a save sealed with another key must never load");
            CollectionAssert.IsEmpty(inner.ListFiles(SaveService.AccountsDirectory, null));
        }

        [Test]
        public void KeyRing_KeystoreKeyReopensAndWipesWhenItIsGone()
        {
            var storage = new MemorySaveStorage();
            var keystore = new FakeKeystore();
            var first = SaveKeyRing.Open(storage, keystore, "device-a", n => Key(7));

            Assert.AreEqual(SaveKeyRing.ModeKeystore, first.Mode);
            Assert.IsFalse(first.Wiped);
            Assert.AreEqual("missing", first.Reason);
            StringAssert.DoesNotContain(Convert.ToBase64String(Key(7)), storage.ReadAllText(SaveKeyRing.KeyPath));
            CollectionAssert.AreNotEqual(Key(7), first.MasterKey, "the sealing key is bound to the device");

            new SaveService(new EncryptedSaveStorage(storage, new SaveCipher(first.MasterKey))).SaveBook(BookWithSasha());
            var again = SaveKeyRing.Open(storage, keystore, "device-a");
            Assert.IsFalse(again.Wiped);
            CollectionAssert.AreEqual(first.MasterKey, again.MasterKey);
            Assert.AreEqual(1, new SaveService(new EncryptedSaveStorage(storage, new SaveCipher(again.MasterKey))).LoadBook().Count);

            // Another phone (or a restored backup): its keystore never held this key.
            var other = SaveKeyRing.Open(storage, new FakeKeystore(), "device-a");
            Assert.IsTrue(other.Wiped);
            Assert.AreEqual("unwrap", other.Reason);
            CollectionAssert.AreNotEqual(first.MasterKey, other.MasterKey);
            Assert.IsFalse(storage.Exists(SaveService.MainPath));
            CollectionAssert.IsEmpty(storage.ListFiles(SaveService.AccountsDirectory, null));
            Assert.IsTrue(storage.Exists(SaveKeyRing.KeyPath));
        }

        [Test]
        public void KeyRing_WithoutKeystoreStillBindsToTheDevice()
        {
            var storage = new MemorySaveStorage();
            var first = SaveKeyRing.Open(storage, null, "device-a");
            Assert.AreEqual(SaveKeyRing.ModeDevice, first.Mode);

            new SaveService(new EncryptedSaveStorage(storage, new SaveCipher(first.MasterKey))).SaveBook(BookWithSasha());
            CollectionAssert.AreEqual(first.MasterKey, SaveKeyRing.Open(storage, null, "device-a").MasterKey);

            var moved = SaveKeyRing.Open(storage, null, "device-b");
            Assert.IsTrue(moved.Wiped);
            Assert.AreEqual("device", moved.Reason);
            Assert.IsFalse(storage.Exists(SaveService.MainPath));

            Assert.AreEqual(SaveKeyRing.ModeDevice, SaveKeyRing.Open(new MemorySaveStorage(), new ThrowingKeystore(), "x").Mode);
            Assert.AreEqual(SaveKeyRing.ModeDevice,
                SaveKeyRing.Open(new MemorySaveStorage(), new FakeKeystore { Unavailable = true }, "x").Mode);
        }

        [Test]
        public void KeyRing_WipesWhenTheKeyFileIsBroken()
        {
            var storage = new MemorySaveStorage();
            var first = SaveKeyRing.Open(storage, new ThrowingKeystore(), "device-a");
            new SaveService(new EncryptedSaveStorage(storage, new SaveCipher(first.MasterKey))).SaveBook(BookWithSasha());

            storage.WriteAllText(SaveKeyRing.KeyPath, "{ not a key");
            var broken = SaveKeyRing.Open(storage, new ThrowingKeystore(), "device-a");
            Assert.IsTrue(broken.Wiped);
            Assert.AreEqual("corrupt", broken.Reason);
            Assert.IsFalse(storage.Exists(SaveService.MainPath));

            // A keystore-mode key file met by a keystore that throws is unreadable too.
            var keystore = new FakeKeystore();
            var wrapped = SaveKeyRing.Open(new MemorySaveStorage(), keystore, "d");
            Assert.AreEqual(SaveKeyRing.ModeKeystore, wrapped.Mode);
            var keyed = new MemorySaveStorage();
            SaveKeyRing.Open(keyed, keystore, "d");
            Assert.AreEqual("unwrap", SaveKeyRing.Open(keyed, new ThrowingKeystore(), "d").Reason);
        }

        [Test]
        public void SecureSaves_OpenOnDiskReopensOnlyOnTheSameDevice()
        {
            var root = TempRoot();
            try
            {
                SaveKeyResult key;
                var storage = SecureSaves.Open(root, null, "device-a", out key);
                Assert.IsFalse(key.Wiped);
                new SaveService(storage).SaveBook(BookWithSasha());

                var accountFile = Path.Combine(root, SaveService.AccountsDirectory.Replace('/', Path.DirectorySeparatorChar));
                var files = Directory.GetFiles(accountFile);
                Assert.AreEqual(1, files.Length);
                StringAssert.DoesNotContain("Sasha", File.ReadAllText(files[0]));

                Assert.AreEqual(1, new SaveService(SecureSaves.Open(root, null, "device-a", out key)).LoadBook().Count);
                Assert.IsFalse(key.Wiped);

                Assert.AreEqual(0, new SaveService(SecureSaves.Open(root, null, "device-b", out key)).LoadBook().Count);
                Assert.IsTrue(key.Wiped);
                Assert.AreEqual(0, Directory.GetFiles(accountFile).Length);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void SecureSaves_PurgeLegacyDeletesOldSavesKeepsModsAndMovesDiagnostics()
        {
            var root = TempRoot();
            var legacy = TempRoot();
            try
            {
                Write(legacy, "save/main.json", "{\"current\":\"login1:pass1\"}");
                Write(legacy, "save/accounts/login1_pass1.json", "{\"login\":\"login1\"}");
                Write(legacy, "mods/skin/mod.json", "{}");
                Write(legacy, TelemetryPolicy.PreferencePath, "{\"enabled\":true}");
                Write(root, "save/settings.json", "{}");

                Assert.AreEqual(2, SecureSaves.PurgeLegacy(root, new[] { legacy }));

                Assert.IsFalse(Directory.Exists(Path.Combine(legacy, "save")));
                Assert.IsFalse(Directory.Exists(Path.Combine(root, "save")));
                Assert.IsTrue(File.Exists(Path.Combine(legacy, "mods", "skin", "mod.json")), "mods stay where they are");
                Assert.IsFalse(File.Exists(Path.Combine(legacy, TelemetryPolicy.PreferencePath)));
                Assert.AreEqual("{\"enabled\":true}", File.ReadAllText(Path.Combine(root, TelemetryPolicy.PreferencePath)));

                // An opt-in already on the new root wins over a stale legacy one.
                Write(legacy, TelemetryPolicy.PreferencePath, "{\"enabled\":false}");
                Assert.AreEqual(0, SecureSaves.PurgeLegacy(root, new[] { legacy }));
                Assert.AreEqual("{\"enabled\":true}", File.ReadAllText(Path.Combine(root, TelemetryPolicy.PreferencePath)));
                Assert.IsFalse(File.Exists(Path.Combine(legacy, TelemetryPolicy.PreferencePath)));
            }
            finally
            {
                Directory.Delete(root, true);
                Directory.Delete(legacy, true);
            }
        }

        [Test]
        public void SecureSaves_PurgeLegacyOnTheSameRootKeepsDiagnosticsAndNewSaves()
        {
            var root = TempRoot();
            try
            {
                Write(root, "save/main.json", "{}");
                Write(root, TelemetryPolicy.PreferencePath, "{\"enabled\":true}");
                SaveKeyResult key;
                new SaveService(SecureSaves.Open(root, null, "device-a", out key)).SaveBook(BookWithSasha());

                Assert.AreEqual(1, SecureSaves.PurgeLegacy(root, new[] { root }));

                Assert.IsFalse(Directory.Exists(Path.Combine(root, "save")));
                Assert.IsTrue(File.Exists(Path.Combine(root, TelemetryPolicy.PreferencePath)));
                Assert.AreEqual(1, new SaveService(SecureSaves.Open(root, null, "device-a", out key)).LoadBook().Count);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static string TempRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "blitz-secure-saves-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void Write(string root, string relativePath, string text)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }
    }
}
