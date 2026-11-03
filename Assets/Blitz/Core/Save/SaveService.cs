using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Save
{
    /// <summary>
    /// Port of the Godot save layout: <c>main</c> plus one file per account
    /// under <c>accounts/</c>. The JSON is written through an encrypted,
    /// device-bound <see cref="EncryptedSaveStorage"/> in the player
    /// (<c>Runtime/Save/SecureSaves</c>); the plain <c>save/*.json</c> folder
    /// of earlier builds is deleted, never read.
    /// </summary>
    public sealed class SaveService
    {
        public const string SaveDirectory = "saves";
        public const string AccountsDirectory = "saves/accounts";
        public const string MainPath = "saves/main.sav";
        public const string SettingsPath = "saves/settings.sav";
        public const string Extension = ".sav";

        private readonly ISaveStorage _storage;

        public SaveService(ISaveStorage storage)
        {
            if (storage == null)
            {
                throw new ArgumentNullException("storage");
            }

            _storage = storage;
        }

        public AccountBook LoadBook()
        {
            var book = new AccountBook();

            if (_storage.Exists(MainPath))
            {
                JsonNode index;
                if (JsonNode.TryParse(_storage.ReadAllText(MainPath), out index))
                {
                    book.IndexFromJson(index);
                }
            }

            foreach (var path in _storage.ListFiles(AccountsDirectory, Extension))
            {
                JsonNode node;
                if (!JsonNode.TryParse(_storage.ReadAllText(path), out node))
                {
                    continue;
                }

                var account = AccountData.FromJson(node);
                if (string.IsNullOrEmpty(account.Login) && string.IsNullOrEmpty(account.Password))
                {
                    continue;
                }

                book.Put(account);
            }

            if (!book.Has(book.CurrentAccountKey))
            {
                book.CurrentAccountKey = string.Empty;
            }

            return book;
        }

        public void SaveBook(AccountBook book)
        {
            if (book == null)
            {
                throw new ArgumentNullException("book");
            }

            _storage.WriteAllText(MainPath, book.IndexToJson().ToJson());

            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in book.Accounts)
            {
                var path = AccountPath(pair.Value);
                expected.Add(path);
                _storage.WriteAllText(path, pair.Value.ToJson().ToJson());
            }

            // Drop files for accounts deleted in this session.
            var stale = new List<string>();
            foreach (var path in _storage.ListFiles(AccountsDirectory, Extension))
            {
                if (!expected.Contains(path))
                {
                    stale.Add(path);
                }
            }

            for (var i = 0; i < stale.Count; i++)
            {
                _storage.Delete(stale[i]);
            }
        }

        public SettingsData LoadSettings()
        {
            if (!_storage.Exists(SettingsPath))
            {
                return new SettingsData();
            }

            JsonNode node;
            return JsonNode.TryParse(_storage.ReadAllText(SettingsPath), out node)
                ? SettingsData.FromJson(node)
                : new SettingsData();
        }

        public void SaveSettings(SettingsData settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            _storage.WriteAllText(SettingsPath, settings.ToJson().ToJson());
        }

        public static string AccountPath(AccountData account)
        {
            return AccountsDirectory + "/" + SanitizeFileName(account.Key) + Extension;
        }

        /// <summary>Account keys contain ':' and free-form names, so map to a safe stem.</summary>
        public static string SanitizeFileName(string value)
        {
            var text = value ?? string.Empty;
            var builder = new System.Text.StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                var safe = (c >= 'a' && c <= 'z') ||
                           (c >= 'A' && c <= 'Z') ||
                           (c >= '0' && c <= '9') ||
                           c == '-' || c == '_';
                builder.Append(safe ? c : '_');
            }

            return builder.Length == 0 ? "account" : builder.ToString();
        }
    }
}
