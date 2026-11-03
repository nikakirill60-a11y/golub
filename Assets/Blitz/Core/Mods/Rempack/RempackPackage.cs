using System;
using System.IO;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Mods.Rempack
{
    /// <summary>
    /// An open <c>.rempack</c>: the validated ZIP directory and the parsed manifest. The
    /// file stays packed; every read is bounded by the entry's promised size and checked
    /// against its CRC-32. Thread-safe for reads.
    /// </summary>
    public sealed class RempackPackage : IDisposable
    {
        private RempackPackage(string filePath, RempackZipReader zip, RempackManifest manifest)
        {
            FilePath = filePath ?? string.Empty;
            Zip = zip;
            Manifest = manifest;
        }

        public string FilePath { get; private set; }
        public RempackZipReader Zip { get; private set; }
        public RempackManifest Manifest { get; private set; }

        public long Size
        {
            get { return Zip.ArchiveSize; }
        }

        /// <summary>
        /// Opens a package file and reads its manifest. Problems go to <paramref name="report"/>;
        /// null means the file is not a readable package at all (broken ZIP, no manifest).
        /// </summary>
        public static RempackPackage Open(string path, RempackReport report)
        {
            RempackZipReader zip;
            try
            {
                zip = RempackZipReader.Open(path);
            }
            catch (RempackException error)
            {
                report.Add(error.Issue);
                return null;
            }

            return Wrap(path, zip, report);
        }

        /// <summary>Opens a package held by a seekable stream (tests, the CLI's stdin).</summary>
        public static RempackPackage Open(Stream stream, bool leaveOpen, RempackReport report)
        {
            RempackZipReader zip;
            try
            {
                zip = RempackZipReader.Open(stream, leaveOpen);
            }
            catch (RempackException error)
            {
                report.Add(error.Issue);
                return null;
            }

            return Wrap(null, zip, report);
        }

        private static RempackPackage Wrap(string path, RempackZipReader zip, RempackReport report)
        {
            var entry = zip.Find(RempackFormat.ManifestFile);
            if (entry == null || entry.Path != RempackFormat.ManifestFile)
            {
                report.Error(RempackCodes.ManifestMissing, string.Empty);
                zip.Dispose();
                return null;
            }

            if (entry.Size > RempackLimits.ManifestBytes)
            {
                report.Error(RempackCodes.FileTooLarge, RempackFormat.ManifestFile,
                    entry.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
                zip.Dispose();
                return null;
            }

            RempackManifest manifest;
            try
            {
                manifest = RempackManifest.Parse(zip.ReadText(entry, RempackLimits.ManifestBytes), report);
            }
            catch (RempackException error)
            {
                report.Add(error.Issue);
                manifest = null;
            }

            if (manifest == null)
            {
                zip.Dispose();
                return null;
            }

            return new RempackPackage(path, zip, manifest);
        }

        /// <summary>Exact-name lookup: package paths are case-sensitive even though duplicates that differ only in case are refused.</summary>
        public RempackZipEntry Entry(string path)
        {
            var entry = Zip.Find(path);
            return entry != null && entry.Path == path ? entry : null;
        }

        public bool Contains(string path)
        {
            return Entry(path) != null;
        }

        /// <summary>A whole file. Throws <see cref="RempackException"/> when it is missing, too large or damaged.</summary>
        public byte[] ReadBytes(string path, long maxBytes)
        {
            return Zip.ReadAll(Require(path), maxBytes);
        }

        public string ReadText(string path, long maxBytes)
        {
            return Zip.ReadText(Require(path), maxBytes);
        }

        /// <summary>A JSON file, bounded in size and depth; problems go to the report and null comes back.</summary>
        public JsonNode ReadJson(string path, RempackReport report)
        {
            var entry = Entry(path);
            if (entry == null)
            {
                report.Error(RempackCodes.FileMissing, path);
                return null;
            }

            if (entry.Size > RempackLimits.JsonBytes)
            {
                report.Error(RempackCodes.JsonTooLarge, path);
                return null;
            }

            if (entry.Size > RempackLimits.JsonSoftBytes)
            {
                report.Warning(RempackCodes.JsonHeavy, path);
            }

            try
            {
                return RempackJson.Parse(Zip.ReadText(entry, RempackLimits.JsonBytes), path, report);
            }
            catch (RempackException error)
            {
                report.Add(error.Issue);
                return null;
            }
        }

        /// <summary>Streams a file without holding it in memory (long music to the audio cache).</summary>
        public void CopyTo(string path, Stream output)
        {
            Zip.CopyTo(Require(path), output);
        }

        public string Sha256(string path)
        {
            return Zip.Sha256(Require(path));
        }

        public void Dispose()
        {
            Zip.Dispose();
        }

        private RempackZipEntry Require(string path)
        {
            var entry = Entry(path);
            if (entry == null)
            {
                throw new RempackException(RempackCodes.FileMissing, path ?? string.Empty);
            }

            return entry;
        }
    }
}
