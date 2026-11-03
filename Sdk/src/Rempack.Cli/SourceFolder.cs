using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Sdk
{
    /// <summary>
    /// A mod folder as the packer sees it: the manifest, and every file that goes into the
    /// package under its package path. Hidden files, archiver junk and whatever the ignore
    /// rules leave out stay behind; links are not followed, so a package holds only what
    /// the folder itself contains. Names a package cannot hold are reported with the
    /// game's own codes and the rest is still collected, so one run shows every problem.
    /// </summary>
    internal sealed class SourceFolder
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly HashSet<string> _folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private IgnoreRules _rules;
        private int _seen;

        private SourceFolder(string root)
        {
            Root = root;
        }

        /// <summary>The folder, as a full path.</summary>
        public string Root { get; private set; }

        /// <summary>The parsed <c>rempack.json</c>; null when the folder has none that can be read.</summary>
        public JsonNode Manifest { get; private set; }

        /// <summary>The files to pack, in package path order.</summary>
        public readonly List<RempackSource> Files = new List<RempackSource>();

        /// <summary>Paths the ignore rules left out; folders end with a slash.</summary>
        public readonly List<string> Skipped = new List<string>();

        /// <summary>Links found in the folder, which are not followed.</summary>
        public readonly List<string> Links = new List<string>();

        public readonly RempackReport Report = new RempackReport();

        /// <summary>Size of the files to pack together.</summary>
        public long Bytes { get; private set; }

        /// <summary>
        /// Whether packing makes sense: the manifest is readable and the folder is within
        /// the limits that would only refuse the result after writing gigabytes.
        /// </summary>
        public bool CanPack
        {
            get
            {
                return Manifest != null && !Report.Has(RempackCodes.TooManyFiles)
                    && !Report.Has(RempackCodes.UnpackedTooLarge);
            }
        }

        public static SourceFolder Scan(string folder)
        {
            var source = new SourceFolder(Path.GetFullPath(folder));
            source.Manifest = source.ReadManifest();
            if (source.Manifest == null)
            {
                return source;
            }

            source.LoadRules();
            source.Walk(new DirectoryInfo(source.Root), string.Empty);

            // The manifest is one more entry of the package.
            if (source._seen + 1 > RempackLimits.Entries)
            {
                source.Report.Error(RempackCodes.TooManyFiles, string.Empty,
                    (source._seen + 1).ToString(CultureInfo.InvariantCulture));
            }

            if (source.Bytes > RempackLimits.UnpackedBytes)
            {
                source.Report.Error(RempackCodes.UnpackedTooLarge, string.Empty);
            }

            return source;
        }

        private JsonNode ReadManifest()
        {
            const string name = RempackFormat.ManifestFile;
            var path = Path.Combine(Root, name);
            if (!File.Exists(path))
            {
                Report.Error(RempackCodes.ManifestMissing, string.Empty);
                return null;
            }

            byte[] data;
            try
            {
                var length = new FileInfo(path).Length;
                if (length > RempackLimits.ManifestBytes)
                {
                    Report.Error(RempackCodes.FileTooLarge, name, length.ToString(CultureInfo.InvariantCulture));
                    return null;
                }

                data = File.ReadAllBytes(path);
            }
            catch (Exception error) when (IsFileError(error))
            {
                Report.Add(Unreadable(name, error));
                return null;
            }

            // The game reads the manifest as strict UTF-8 with an optional BOM; so does the packer.
            var start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            string text;
            try
            {
                text = StrictUtf8.GetString(data, start, data.Length - start);
            }
            catch (DecoderFallbackException)
            {
                Report.Error(RempackCodes.TextNotUtf8, name);
                return null;
            }

            var root = RempackJson.Parse(text, name, Report);
            if (root == null)
            {
                return null;
            }

            if (root.Kind != JsonKind.Object)
            {
                Report.Error(RempackCodes.FieldNotObject, name, "(root)");
                return null;
            }

            return root;
        }

        private void LoadRules()
        {
            try
            {
                _rules = IgnoreRules.Load(Root);
            }
            catch (Exception error) when (IsFileError(error))
            {
                Report.Add(Unreadable(IgnoreRules.FileName, error));
                _rules = IgnoreRules.WithDefaults();
            }
        }

        private void Walk(DirectoryInfo folder, string prefix)
        {
            FileSystemInfo[] entries;
            try
            {
                entries = folder.GetFileSystemInfos();
            }
            catch (Exception error) when (IsFileError(error))
            {
                Report.Add(Unreadable(prefix, error));
                return;
            }

            Array.Sort(entries, delegate(FileSystemInfo a, FileSystemInfo b) { return string.CompareOrdinal(a.Name, b.Name); });
            foreach (var entry in entries)
            {
                var name = entry.Name;
                var relative = prefix + name;
                var directory = entry as DirectoryInfo;
                if (name.StartsWith(".", StringComparison.Ordinal)
                    || RempackPaths.IsJunk(directory != null ? relative + "/" : relative))
                {
                    // .git, .rempackignore, .DS_Store, __MACOSX/ and the like.
                    continue;
                }

                if (entry.LinkTarget != null)
                {
                    Links.Add(directory != null ? relative + "/" : relative);
                    continue;
                }

                if (directory != null)
                {
                    if (_rules.IsIgnored(relative, true))
                    {
                        Skipped.Add(relative + "/");
                    }
                    else if (!RempackPaths.IsValid(relative))
                    {
                        Report.Error(RempackCodes.PathInvalid, relative + "/");
                    }
                    else
                    {
                        Walk(directory, relative + "/");
                    }

                    continue;
                }

                if (prefix.Length == 0 && string.Equals(name, RempackFormat.ManifestFile, StringComparison.OrdinalIgnoreCase))
                {
                    // Packed from the parsed manifest, with "files" filled in.
                    continue;
                }

                if (_rules.IsIgnored(relative, false))
                {
                    Skipped.Add(relative);
                    continue;
                }

                Add((FileInfo)entry, relative);
            }
        }

        private void Add(FileInfo file, string relative)
        {
            _seen++;
            if (!RempackPaths.IsValid(relative))
            {
                Report.Error(RempackCodes.PathInvalid, relative);
                return;
            }

            if (!_folded.Add(relative))
            {
                // Only a case-sensitive file system lets two such names live side by side.
                Report.Error(RempackCodes.PathDuplicate, relative);
                return;
            }

            long length;
            try
            {
                length = file.Length;
            }
            catch (Exception error) when (IsFileError(error))
            {
                Report.Add(Unreadable(relative, error));
                return;
            }

            if (length > RempackLimits.FileBytes)
            {
                Report.Error(RempackCodes.FileTooLarge, relative, length.ToString(CultureInfo.InvariantCulture));
                return;
            }

            Bytes += length;
            if (Files.Count + 1 < RempackLimits.Entries)
            {
                var full = file.FullName;
                Files.Add(RempackSource.FromStream(relative, delegate { return SourceStream.Open(full, relative); }, length));
            }
        }

        internal static bool IsFileError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException;
        }

        private static RempackIssue Unreadable(string path, Exception error)
        {
            return new RempackIssue(RempackSeverity.Error, RempackCodes.FileUnreadable, path).WithDetail(error.Message);
        }
    }

    /// <summary>
    /// A file of the folder opened for packing. A read that fails becomes a package issue
    /// naming the file, so a locked or vanished file is reported instead of ending the
    /// tool, and any I/O error left over comes from writing the package.
    /// </summary>
    internal sealed class SourceStream : Stream
    {
        private readonly Stream _inner;
        private readonly string _path;

        private SourceStream(Stream inner, string path)
        {
            _inner = inner;
            _path = path;
        }

        public static Stream Open(string file, string path)
        {
            try
            {
                return new SourceStream(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920), path);
            }
            catch (Exception error) when (SourceFolder.IsFileError(error))
            {
                throw RempackException.Detailed(RempackCodes.FileUnreadable, path, error.Message);
            }
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return _inner.Read(buffer, offset, count);
            }
            catch (Exception error) when (SourceFolder.IsFileError(error))
            {
                throw RempackException.Detailed(RempackCodes.FileUnreadable, _path, error.Message);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
