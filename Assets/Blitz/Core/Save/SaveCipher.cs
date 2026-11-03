using System;
using System.Security.Cryptography;
using System.Text;

namespace BlitzLegacy.Core.Save
{
    /// <summary>
    /// Authenticated save encryption: AES-256-CBC with a random IV, then an
    /// HMAC-SHA256 over the header, the file's relative path, the IV and the
    /// ciphertext (encrypt-then-MAC). AES-GCM is not available on every
    /// Mono/IL2CPP target; this construction is, and gives the same
    /// guarantees: nothing is decrypted before the tag has been verified.
    /// The path is part of the tag, so a valid file cannot be moved into
    /// another slot (one account's save copied over another's, say).
    /// </summary>
    public sealed class SaveCipher
    {
        public const int KeySize = 32;

        private const int IvSize = 16;
        private const int TagSize = 32;
        private const int BlockSize = 16;
        private const byte FormatVersion = 1;
        private static readonly byte[] Magic = { (byte)'B', (byte)'L', (byte)'Z', (byte)'S' };
        private static readonly int HeaderSize = Magic.Length + 1;

        private readonly byte[] _encryptionKey;
        private readonly byte[] _macKey;

        public SaveCipher(byte[] masterKey)
        {
            if (masterKey == null || masterKey.Length != KeySize)
            {
                throw new ArgumentException("The save key must be 32 bytes.", "masterKey");
            }

            _encryptionKey = Derive(masterKey, "blitz-save-encryption-v1");
            _macKey = Derive(masterKey, "blitz-save-authentication-v1");
        }

        /// <summary>HMAC-SHA256 of a label: a one-step key derivation.</summary>
        public static byte[] Derive(byte[] key, string label)
        {
            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(label ?? string.Empty));
            }
        }

        public byte[] Seal(string relativePath, byte[] plain)
        {
            plain = plain ?? new byte[0];
            var iv = new byte[IvSize];
            using (var random = new RNGCryptoServiceProvider())
            {
                random.GetBytes(iv);
            }

            byte[] cipher;
            using (var aes = CreateAes(iv))
            using (var encryptor = aes.CreateEncryptor())
            {
                cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
            }

            var blob = new byte[HeaderSize + IvSize + cipher.Length + TagSize];
            Buffer.BlockCopy(Magic, 0, blob, 0, Magic.Length);
            blob[Magic.Length] = FormatVersion;
            Buffer.BlockCopy(iv, 0, blob, HeaderSize, IvSize);
            Buffer.BlockCopy(cipher, 0, blob, HeaderSize + IvSize, cipher.Length);
            var tag = Tag(relativePath, blob, blob.Length - TagSize);
            Buffer.BlockCopy(tag, 0, blob, blob.Length - TagSize, TagSize);
            return blob;
        }

        /// <summary>
        /// False for anything this key did not seal for this path: plain
        /// text, a file from another device, a truncated or edited one.
        /// </summary>
        public bool TryOpen(string relativePath, byte[] blob, out byte[] plain)
        {
            plain = null;
            if (blob == null || blob.Length < HeaderSize + IvSize + BlockSize + TagSize)
            {
                return false;
            }

            for (var i = 0; i < Magic.Length; i++)
            {
                if (blob[i] != Magic[i])
                {
                    return false;
                }
            }

            var cipherLength = blob.Length - HeaderSize - IvSize - TagSize;
            if (blob[Magic.Length] != FormatVersion || cipherLength % BlockSize != 0)
            {
                return false;
            }

            var expected = Tag(relativePath, blob, blob.Length - TagSize);
            if (!FixedTimeEquals(expected, blob, blob.Length - TagSize))
            {
                return false;
            }

            var iv = new byte[IvSize];
            Buffer.BlockCopy(blob, HeaderSize, iv, 0, IvSize);
            try
            {
                using (var aes = CreateAes(iv))
                using (var decryptor = aes.CreateDecryptor())
                {
                    plain = decryptor.TransformFinalBlock(blob, HeaderSize + IvSize, cipherLength);
                }
            }
            catch (CryptographicException)
            {
                plain = null;
                return false;
            }

            return true;
        }

        private Aes CreateAes(byte[] iv)
        {
            // Constructed directly: Aes.Create() resolves through CryptoConfig
            // reflection, which IL2CPP code stripping can break.
            var aes = new AesManaged();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = _encryptionKey;
            aes.IV = iv;
            return aes;
        }

        private byte[] Tag(string relativePath, byte[] blob, int length)
        {
            var path = Encoding.UTF8.GetBytes((relativePath ?? string.Empty).Replace('\\', '/'));
            var pathLength = new[]
            {
                (byte)(path.Length >> 24), (byte)(path.Length >> 16),
                (byte)(path.Length >> 8), (byte)path.Length,
            };

            using (var hmac = new HMACSHA256(_macKey))
            {
                hmac.TransformBlock(pathLength, 0, pathLength.Length, null, 0);
                hmac.TransformBlock(path, 0, path.Length, null, 0);
                hmac.TransformFinalBlock(blob, 0, length);
                return hmac.Hash;
            }
        }

        private static bool FixedTimeEquals(byte[] expected, byte[] blob, int offset)
        {
            var difference = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                difference |= expected[i] ^ blob[offset + i];
            }

            return difference == 0;
        }
    }
}
