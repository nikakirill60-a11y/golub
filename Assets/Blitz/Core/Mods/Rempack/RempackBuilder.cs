using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Mods.Rempack
{
    /// <summary>
    /// Packs files into a <c>.rempack</c>: writes the SHA-256 of every file into the
    /// manifest's <c>files</c> and stores everything in a deterministic container, so the
    /// same input gives the same bytes. The SDK's <c>pack</c> and the game's tests share it.
    /// </summary>
    public static class RempackBuilder
    {
        /// <summary>
        /// The package bytes. <paramref name="manifest"/> is written as given except for
        /// <c>files</c>, which is replaced by the hashes of <paramref name="files"/>.
        /// Throws <see cref="RempackException"/> for a path that cannot be packed.
        /// </summary>
        public static byte[] Pack(JsonNode manifest, IDictionary<string, byte[]> files)
        {
            using (var output = new MemoryStream())
            {
                Pack(manifest, files, output);
                return output.ToArray();
            }
        }

        public static void Pack(JsonNode manifest, IDictionary<string, byte[]> files, Stream output)
        {
            var sources = new List<RempackSource>();
            foreach (var pair in files ?? new Dictionary<string, byte[]>())
            {
                sources.Add(RempackSource.FromBytes(pair.Key, pair.Value));
            }

            Pack(manifest, sources, output);
        }

        /// <summary>
        /// Streams <paramref name="files"/> into a package: each one is read once for its
        /// hash and once into the container, so a package of any allowed size packs in
        /// little memory when <paramref name="output"/> can seek.
        /// </summary>
        public static void Pack(JsonNode manifest, IEnumerable<RempackSource> files, Stream output)
        {
            if (manifest == null || manifest.Kind != JsonKind.Object)
            {
                throw new ArgumentException("The manifest must be a JSON object.", "manifest");
            }

            var writer = new RempackZipWriter();
            var sorted = new SortedDictionary<string, RempackSource>(StringComparer.Ordinal);
            foreach (var file in files ?? new RempackSource[0])
            {
                if (file == null)
                {
                    continue;
                }

                if (file.Path == RempackFormat.ManifestFile)
                {
                    throw new RempackException(RempackCodes.PathInvalid, file.Path);
                }

                writer.Add(file);
                sorted[file.Path] = file;
            }

            var hashes = JsonNode.NewObject();
            foreach (var pair in sorted)
            {
                hashes.Set(pair.Key, Sha256(pair.Value));
            }

            manifest.Set("files", hashes);
            writer.Add(RempackFormat.ManifestFile, new UTF8Encoding(false).GetBytes(manifest.ToJson(true) + "\n"));
            writer.Write(output);
        }

        /// <summary>Lowercase hex SHA-256, the form <c>files</c> uses.</summary>
        public static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return RempackZipReader.Hex(sha.ComputeHash(data ?? new byte[0]));
            }
        }

        public static string Sha256(RempackSource source)
        {
            using (var sha = SHA256.Create())
            using (var input = source.Open())
            {
                return RempackZipReader.Hex(sha.ComputeHash(input));
            }
        }
    }
}
