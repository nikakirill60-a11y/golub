using System;
using System.Collections.Generic;
using System.Text;

namespace BlitzLegacy.Core.Save
{
    /// <summary>
    /// <see cref="ISaveStorage"/> decorator that seals every file with a
    /// <see cref="SaveCipher"/> before it reaches the inner storage (stored as
    /// base64 text, so any text storage works underneath).
    ///
    /// A file that does not open with this key is never trusted and never
    /// kept: it is deleted and read as missing. That covers saves from builds
    /// before encryption, files copied from another device and edited ones.
    /// </summary>
    public sealed class EncryptedSaveStorage : ISaveStorage
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private readonly ISaveStorage _inner;
        private readonly SaveCipher _cipher;

        public EncryptedSaveStorage(ISaveStorage inner, SaveCipher cipher)
        {
            if (inner == null)
            {
                throw new ArgumentNullException("inner");
            }

            if (cipher == null)
            {
                throw new ArgumentNullException("cipher");
            }

            _inner = inner;
            _cipher = cipher;
        }

        /// <summary>Raised with the relative path of every rejected (deleted) file.</summary>
        public event Action<string> Rejected;

        public int RejectedCount { get; private set; }

        public bool Exists(string relativePath)
        {
            return _inner.Exists(relativePath);
        }

        public string ReadAllText(string relativePath)
        {
            if (!_inner.Exists(relativePath))
            {
                return string.Empty;
            }

            byte[] blob;
            byte[] plain;
            if (TryDecode(_inner.ReadAllText(relativePath), out blob) &&
                _cipher.TryOpen(relativePath, blob, out plain))
            {
                try
                {
                    return Utf8.GetString(plain);
                }
                catch (ArgumentException)
                {
                }
            }

            _inner.Delete(relativePath);
            RejectedCount++;
            var handler = Rejected;
            if (handler != null)
            {
                handler(relativePath);
            }

            return string.Empty;
        }

        public void WriteAllText(string relativePath, string contents)
        {
            var sealedBytes = _cipher.Seal(relativePath, Utf8.GetBytes(contents ?? string.Empty));
            _inner.WriteAllText(relativePath, Convert.ToBase64String(sealedBytes));
        }

        public void Delete(string relativePath)
        {
            _inner.Delete(relativePath);
        }

        public IEnumerable<string> ListFiles(string relativeDirectory, string extension)
        {
            return _inner.ListFiles(relativeDirectory, extension);
        }

        private static bool TryDecode(string text, out byte[] blob)
        {
            blob = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            try
            {
                blob = Convert.FromBase64String(text.Trim());
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
