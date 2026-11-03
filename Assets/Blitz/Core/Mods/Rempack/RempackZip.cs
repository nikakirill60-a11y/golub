using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace BlitzLegacy.Core.Mods.Rempack
{
    /// <summary>One file stored in a Remaster Package container.</summary>
    public sealed class RempackZipEntry
    {
        internal RempackZipEntry(
            string path, ushort method, uint crc, long compressedSize, long size, long localHeaderOffset)
        {
            Path = path;
            Method = method;
            Crc32 = crc;
            CompressedSize = compressedSize;
            Size = size;
            LocalHeaderOffset = localHeaderOffset;
        }

        /// <summary>The normalized path inside the package, always with forward slashes.</summary>
        public string Path { get; private set; }

        /// <summary>0 for stored entries, 8 for deflate.</summary>
        public ushort Method { get; private set; }

        public uint Crc32 { get; private set; }

        public long CompressedSize { get; private set; }

        /// <summary>The unpacked size the central directory promises; reads hold it to that.</summary>
        public long Size { get; private set; }

        internal long LocalHeaderOffset { get; private set; }

        internal long DataOffset { get; set; }
    }

    /// <summary>
    /// A deliberately small ZIP reader for the package container.
    ///
    /// The archive is never unpacked: the game keeps the single file and reads
    /// entries by the central directory on demand, so the reader validates the
    /// whole directory once when it opens and refuses anything a package has no
    /// business containing: encryption, ZIP64, multi-disk archives, compression
    /// other than store/deflate, links, unsafe or duplicate names, entries whose
    /// data overlaps (the trick behind non-recursive zip bombs) and data outside
    /// the file. Every read is bounded by the promised size and checked against
    /// the stored CRC-32. The same code runs in the game and in the SDK CLI.
    /// </summary>
    public sealed class RempackZipReader : IDisposable
    {
        private const uint EndOfCentralDirectory = 0x06054b50;
        private const uint CentralHeader = 0x02014b50;
        private const uint LocalHeader = 0x04034b50;
        private const int MaxCommentLength = 65535;

        private readonly Stream _stream;
        private readonly bool _ownsStream;
        private readonly object _gate = new object();
        private readonly List<RempackZipEntry> _entries = new List<RempackZipEntry>();
        private readonly Dictionary<string, RempackZipEntry> _byPath =
            new Dictionary<string, RempackZipEntry>(StringComparer.OrdinalIgnoreCase);

        private RempackZipReader(Stream stream, bool ownsStream)
        {
            _stream = stream;
            _ownsStream = ownsStream;
        }

        public IReadOnlyList<RempackZipEntry> Entries
        {
            get { return _entries; }
        }

        public long ArchiveSize { get; private set; }

        /// <summary>The sum of the promised unpacked sizes.</summary>
        public long UnpackedSize { get; private set; }

        /// <summary>Opens and validates a package file. Throws <see cref="RempackException"/>.</summary>
        public static RempackZipReader Open(string path)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            }
            catch (Exception error)
            {
                throw RempackException.Detailed(RempackCodes.FileUnreadable, path, error.Message);
            }

            try
            {
                var reader = new RempackZipReader(stream, true);
                reader.ReadDirectory();
                return reader;
            }
            catch (IOException error)
            {
                stream.Dispose();
                throw RempackException.Detailed(RempackCodes.FileUnreadable, path, error.Message);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>Opens and validates a package held by a seekable stream.</summary>
        public static RempackZipReader Open(Stream stream, bool leaveOpen)
        {
            if (stream == null || !stream.CanSeek || !stream.CanRead)
            {
                throw RempackException.Detailed(RempackCodes.ZipInvalid, string.Empty, "stream is not seekable");
            }

            var reader = new RempackZipReader(stream, !leaveOpen);
            try
            {
                reader.ReadDirectory();
                return reader;
            }
            catch (IOException error)
            {
                reader.Dispose();
                throw RempackException.Detailed(RempackCodes.FileUnreadable, string.Empty, error.Message);
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        public RempackZipEntry Find(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            RempackZipEntry entry;
            return _byPath.TryGetValue(path, out entry) ? entry : null;
        }

        public bool Contains(string path)
        {
            return Find(path) != null;
        }

        /// <summary>The whole entry, verified against its size and CRC-32.</summary>
        public byte[] ReadAll(RempackZipEntry entry, long maxBytes)
        {
            if (entry == null)
            {
                throw new ArgumentNullException("entry");
            }

            if (entry.Size > maxBytes || entry.Size > int.MaxValue - 64)
            {
                throw new RempackException(RempackCodes.FileTooLarge, entry.Path, entry.Size.ToString(CultureInfo.InvariantCulture));
            }

            byte[] compressed;
            lock (_gate)
            {
                try
                {
                    compressed = ReadRaw(_stream, entry);
                }
                catch (IOException error)
                {
                    throw RempackException.Detailed(RempackCodes.FileUnreadable, entry.Path, error.Message);
                }
            }

            var data = entry.Method == 0 ? compressed : Inflate(compressed, entry);
            if (Crc32Of(data, 0, data.Length) != entry.Crc32)
            {
                throw new RempackException(RempackCodes.ZipCrcMismatch, entry.Path);
            }

            return data;
        }

        /// <summary>UTF-8 text of an entry (a BOM is skipped).</summary>
        public string ReadText(RempackZipEntry entry, long maxBytes)
        {
            var data = ReadAll(entry, maxBytes);
            var start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            try
            {
                return new UTF8Encoding(false, true).GetString(data, start, data.Length - start);
            }
            catch (DecoderFallbackException)
            {
                throw new RempackException(RempackCodes.TextNotUtf8, entry.Path);
            }
        }

        /// <summary>
        /// Streams an entry into <paramref name="output"/> without holding it in memory: used
        /// to put long music on disk for the streaming audio loader. CRC-checked at the end.
        /// Reads go through the reader's own handle, chunk by chunk under its lock, so a
        /// package renamed or replaced on disk meanwhile is still read consistently.
        /// </summary>
        public void CopyTo(RempackZipEntry entry, Stream output)
        {
            if (entry == null)
            {
                throw new ArgumentNullException("entry");
            }

            var raw = new SharedWindowStream(_stream, _gate, entry.DataOffset, entry.CompressedSize);
            Stream source = entry.Method == 0
                ? (Stream)raw
                : new DeflateStream(raw, CompressionMode.Decompress);
            using (source)
            {
                var crc = 0xFFFFFFFFu;
                var buffer = new byte[81920];
                long written = 0;
                while (true)
                {
                    int read;
                    try
                    {
                        read = source.Read(buffer, 0, buffer.Length);
                    }
                    catch (InvalidDataException)
                    {
                        throw RempackException.Detailed(RempackCodes.ZipInvalid, entry.Path, "bad deflate data");
                    }
                    catch (IOException error)
                    {
                        // The disk, not the package: a read failed under the reader's handle.
                        throw RempackException.Detailed(RempackCodes.FileUnreadable, entry.Path, error.Message);
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    written += read;
                    if (written > entry.Size)
                    {
                        throw new RempackException(RempackCodes.ZipSizeMismatch, entry.Path);
                    }

                    crc = Crc32Update(crc, buffer, 0, read);
                    output.Write(buffer, 0, read);
                }

                if (written != entry.Size)
                {
                    throw new RempackException(RempackCodes.ZipSizeMismatch, entry.Path);
                }

                if ((crc ^ 0xFFFFFFFFu) != entry.Crc32)
                {
                    throw new RempackException(RempackCodes.ZipCrcMismatch, entry.Path);
                }
            }
        }

        /// <summary>Lowercase hex SHA-256 of an entry, streamed and CRC-checked like <see cref="CopyTo"/>.</summary>
        public string Sha256(RempackZipEntry entry)
        {
            using (var sha = SHA256.Create())
            {
                using (var sink = new HashSink(sha))
                {
                    CopyTo(entry, sink);
                }

                sha.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(sha.Hash);
            }
        }

        public static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++)
            {
                text.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        public void Dispose()
        {
            if (_ownsStream)
            {
                _stream.Dispose();
            }
        }

        // ------------------------------------------------------------------

        private void ReadDirectory()
        {
            var length = _stream.Length;
            ArchiveSize = length;
            if (length > RempackLimits.ArchiveBytes)
            {
                throw new RempackException(RempackCodes.ArchiveTooLarge, string.Empty, length.ToString(CultureInfo.InvariantCulture));
            }

            if (length < 22)
            {
                throw RempackException.Detailed(RempackCodes.ZipInvalid, string.Empty, "too short");
            }

            var tailLength = (int)Math.Min(length, 22 + MaxCommentLength);
            var tail = new byte[tailLength];
            _stream.Position = length - tailLength;
            ReadExactly(_stream, tail, 0, tailLength);
            var eocd = -1;
            for (var i = tailLength - 22; i >= 0; i--)
            {
                if (U32(tail, i) == EndOfCentralDirectory && i + 22 + U16(tail, i + 20) == tailLength)
                {
                    eocd = i;
                    break;
                }
            }

            if (eocd < 0)
            {
                throw RempackException.Detailed(RempackCodes.ZipInvalid, string.Empty, "no end of central directory");
            }

            var disk = U16(tail, eocd + 4);
            var cdDisk = U16(tail, eocd + 6);
            var diskEntries = U16(tail, eocd + 8);
            var totalEntries = U16(tail, eocd + 10);
            var cdSize = U32(tail, eocd + 12);
            var cdOffset = U32(tail, eocd + 16);
            if (disk != 0 || cdDisk != 0 || diskEntries != totalEntries)
            {
                throw new RempackException(RempackCodes.ZipMultiDisk, string.Empty);
            }

            if (totalEntries == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
            {
                throw new RempackException(RempackCodes.ZipZip64, string.Empty);
            }

            if (totalEntries > RempackLimits.Entries)
            {
                throw new RempackException(RempackCodes.TooManyFiles, string.Empty, totalEntries.ToString(CultureInfo.InvariantCulture));
            }

            var eocdPosition = length - tailLength + eocd;
            if ((long)cdOffset + cdSize > eocdPosition)
            {
                throw RempackException.Detailed(RempackCodes.ZipInvalid, string.Empty, "central directory out of range");
            }

            var directory = new byte[cdSize];
            _stream.Position = cdOffset;
            ReadExactly(_stream, directory, 0, directory.Length);
            var at = 0;
            long unpacked = 0;
            var ranges = new List<KeyValuePair<long, long>>();
            for (var n = 0; n < totalEntries; n++)
            {
                if (at + 46 > directory.Length || U32(directory, at) != CentralHeader)
                {
                    throw RempackException.Detailed(RempackCodes.ZipInvalid, string.Empty, "bad central header");
                }

                var madeBy = U16(directory, at + 4);
                var flags = U16(directory, at + 8);
                var method = U16(directory, at + 10);
                var crc = U32(directory, at + 16);
                long compressed = U32(directory, at + 20);
                long size = U32(directory, at + 24);
                var nameLength = U16(directory, at + 28);
                var extraLength = U16(directory, at + 30);
                var commentLength = U16(directory, at + 32);
                var startDisk = U16(directory, at + 34);
                var external = U32(directory, at + 38);
                long localOffset = U32(directory, at + 42);
                var nameStart = at + 46;
                at = nameStart + nameLength + extraLength + commentLength;
                if (at > directory.Length)
                {
                    throw RempackException.Detailed(RempackCodes.ZipInvalid, string.Empty, "central header overrun");
                }

                var rawName = DecodeName(directory, nameStart, nameLength);
                if (RempackPaths.IsJunk(rawName))
                {
                    // Archiver leftovers (__MACOSX, Thumbs.db) are skipped, never read.
                    if (!RempackPaths.IsContained(rawName))
                    {
                        throw new RempackException(RempackCodes.PathInvalid, rawName);
                    }

                    continue;
                }

                if ((flags & 0x0001) != 0 || (flags & 0x0040) != 0)
                {
                    throw new RempackException(RempackCodes.ZipEncrypted, rawName);
                }

                if (startDisk != 0 || compressed == 0xFFFFFFFF || size == 0xFFFFFFFF || localOffset == 0xFFFFFFFF)
                {
                    throw new RempackException(RempackCodes.ZipZip64, rawName);
                }

                var host = madeBy >> 8;
                var unixType = (external >> 16) & 0xF000;
                if (host == 3 && unixType != 0 && unixType != 0x8000 && unixType != 0x4000)
                {
                    throw new RempackException(RempackCodes.PathLink, rawName);
                }

                if (host == 0 && (external & 0x400) != 0)
                {
                    // FILE_ATTRIBUTE_REPARSE_POINT written by Windows archivers for links.
                    throw new RempackException(RempackCodes.PathLink, rawName);
                }

                var directoryEntry = rawName.EndsWith("/", StringComparison.Ordinal);
                if (directoryEntry)
                {
                    if (size != 0)
                    {
                        throw RempackException.Detailed(RempackCodes.ZipInvalid, rawName, "directory with data");
                    }

                    RempackPaths.Validate(rawName.TrimEnd('/'));
                    continue;
                }

                var path = RempackPaths.Validate(rawName);
                if (method != 0 && method != 8)
                {
                    throw new RempackException(RempackCodes.ZipMethod, path, method.ToString(CultureInfo.InvariantCulture));
                }

                if (method == 0 && compressed != size)
                {
                    throw new RempackException(RempackCodes.ZipSizeMismatch, path);
                }

                if (size > RempackLimits.FileBytes)
                {
                    throw new RempackException(RempackCodes.FileTooLarge, path, size.ToString(CultureInfo.InvariantCulture));
                }

                if (method == 8 && size > RempackLimits.RatioCheckFloor
                    && size > compressed * RempackLimits.CompressionRatio)
                {
                    throw new RempackException(RempackCodes.ZipBomb, path);
                }

                unpacked += size;
                if (unpacked > RempackLimits.UnpackedBytes)
                {
                    throw new RempackException(RempackCodes.UnpackedTooLarge, path, unpacked.ToString(CultureInfo.InvariantCulture));
                }

                if (_byPath.ContainsKey(path))
                {
                    throw new RempackException(RempackCodes.PathDuplicate, path);
                }

                var entry = new RempackZipEntry(path, method, crc, compressed, size, localOffset);
                ResolveData(_stream, entry);
                if (entry.DataOffset + compressed > cdOffset)
                {
                    throw RempackException.Detailed(RempackCodes.ZipInvalid, path, "data out of range");
                }

                ranges.Add(new KeyValuePair<long, long>(localOffset, entry.DataOffset + compressed));
                _byPath.Add(path, entry);
                _entries.Add(entry);
            }

            // Two directory records pointing at shared data would let a small file
            // promise many times its size; every entry must own its own bytes.
            ranges.Sort((left, right) => left.Key.CompareTo(right.Key));
            for (var i = 1; i < ranges.Count; i++)
            {
                if (ranges[i].Key < ranges[i - 1].Value)
                {
                    throw RempackException.Detailed(RempackCodes.ZipBomb, string.Empty, "overlapping entries");
                }
            }

            UnpackedSize = unpacked;
        }

        private static void ResolveData(Stream stream, RempackZipEntry entry)
        {
            var header = new byte[30];
            stream.Position = entry.LocalHeaderOffset;
            ReadExactly(stream, header, 0, header.Length);
            if (U32(header, 0) != LocalHeader)
            {
                throw RempackException.Detailed(RempackCodes.ZipInvalid, entry.Path, "bad local header");
            }

            var flags = U16(header, 6);
            if ((flags & 0x0001) != 0)
            {
                throw new RempackException(RempackCodes.ZipEncrypted, entry.Path);
            }

            if (U16(header, 8) != entry.Method)
            {
                throw RempackException.Detailed(RempackCodes.ZipInvalid, entry.Path, "method mismatch");
            }

            var nameLength = U16(header, 26);
            var extraLength = U16(header, 28);
            var name = new byte[nameLength];
            ReadExactly(stream, name, 0, nameLength);
            if (!string.Equals(DecodeName(name, 0, nameLength), entry.Path, StringComparison.Ordinal))
            {
                throw RempackException.Detailed(RempackCodes.ZipInvalid, entry.Path, "local name mismatch");
            }

            entry.DataOffset = entry.LocalHeaderOffset + 30 + nameLength + extraLength;
        }

        private static byte[] ReadRaw(Stream stream, RempackZipEntry entry)
        {
            var raw = new byte[entry.CompressedSize];
            stream.Position = entry.DataOffset;
            ReadExactly(stream, raw, 0, raw.Length);
            return raw;
        }

        private static byte[] Inflate(byte[] compressed, RempackZipEntry entry)
        {
            var output = new byte[entry.Size];
            try
            {
                using (var source = new DeflateStream(new MemoryStream(compressed, false), CompressionMode.Decompress))
                {
                    var at = 0;
                    while (at < output.Length)
                    {
                        var read = source.Read(output, at, output.Length - at);
                        if (read <= 0)
                        {
                            break;
                        }

                        at += read;
                    }

                    if (at != output.Length || source.ReadByte() >= 0)
                    {
                        throw new RempackException(RempackCodes.ZipSizeMismatch, entry.Path);
                    }
                }
            }
            catch (InvalidDataException)
            {
                throw RempackException.Detailed(RempackCodes.ZipInvalid, entry.Path, "bad deflate data");
            }

            return output;
        }

        private static string DecodeName(byte[] data, int start, int length)
        {
            // Only ASCII names pass RempackPaths, so a strict UTF-8 decode is enough
            // to reject the rest with a readable name in the message.
            return Encoding.UTF8.GetString(data, start, length);
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read <= 0)
                {
                    throw RempackException.Detailed(RempackCodes.ZipInvalid, string.Empty, "truncated");
                }

                offset += read;
                count -= read;
            }
        }

        private static ushort U16(byte[] data, int at)
        {
            return (ushort)(data[at] | (data[at + 1] << 8));
        }

        private static uint U32(byte[] data, int at)
        {
            return (uint)(data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24));
        }

        // ------------------------------------------------------------------ CRC-32

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        internal static uint Crc32Update(uint crc, byte[] data, int offset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                crc = CrcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        public static uint Crc32Of(byte[] data, int offset, int count)
        {
            return Crc32Update(0xFFFFFFFFu, data, offset, count) ^ 0xFFFFFFFFu;
        }

        /// <summary>
        /// A read-only window over the compressed bytes of one entry in the shared stream:
        /// every read seeks and reads under the reader's lock, so several windows (and
        /// <see cref="ReadAll"/>) can interleave on one handle.
        /// </summary>
        private sealed class SharedWindowStream : Stream
        {
            private readonly Stream _inner;
            private readonly object _gate;
            private long _position;
            private long _left;

            public SharedWindowStream(Stream inner, object gate, long offset, long length)
            {
                _inner = inner;
                _gate = gate;
                _position = offset;
                _left = length;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }

            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_left <= 0 || count <= 0)
                {
                    return 0;
                }

                int read;
                lock (_gate)
                {
                    _inner.Position = _position;
                    read = _inner.Read(buffer, offset, (int)Math.Min(count, _left));
                }

                if (read <= 0)
                {
                    throw RempackException.Detailed(RempackCodes.ZipInvalid, string.Empty, "unexpected end of file");
                }

                _position += read;
                _left -= read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }

    /// <summary>A write-only stream that feeds a hash.</summary>
    internal sealed class HashSink : Stream
    {
        private readonly HashAlgorithm _hash;

        public HashSink(HashAlgorithm hash)
        {
            _hash = hash;
        }

        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.TransformBlock(buffer, offset, count, null, 0);
        }
    }

    /// <summary>
    /// One file to pack: its path in the package and where its bytes come from. A file
    /// source is read when the package is written, never held in memory as a whole.
    /// </summary>
    public sealed class RempackSource
    {
        private readonly byte[] _data;
        private readonly Func<Stream> _open;

        private RempackSource(string path, byte[] data, Func<Stream> open, long length)
        {
            Path = path ?? string.Empty;
            _data = data;
            _open = open;
            Length = length;
        }

        public string Path { get; private set; }

        /// <summary>Size in bytes, or -1 when only the stream knows it.</summary>
        public long Length { get; private set; }

        public static RempackSource FromBytes(string path, byte[] data)
        {
            data = data ?? new byte[0];
            return new RempackSource(path, data, null, data.Length);
        }

        public static RempackSource FromFile(string path, string file)
        {
            if (string.IsNullOrEmpty(file))
            {
                throw new ArgumentException("A file path is required.", "file");
            }

            return new RempackSource(path, null, delegate { return File.OpenRead(file); }, new FileInfo(file).Length);
        }

        /// <summary>A source that opens a fresh stream each time it is read (it is read twice).</summary>
        public static RempackSource FromStream(string path, Func<Stream> open, long length = -1)
        {
            if (open == null)
            {
                throw new ArgumentNullException("open");
            }

            return new RempackSource(path, null, open, length < 0 ? -1 : length);
        }

        public Stream Open()
        {
            if (_data != null)
            {
                return new MemoryStream(_data, false);
            }

            var stream = _open();
            if (stream == null)
            {
                throw new RempackException(RempackCodes.FileUnreadable, Path);
            }

            return stream;
        }
    }

    /// <summary>
    /// Writes package containers byte for byte the same for the same input: sorted
    /// entries, a fixed timestamp, no extra fields. Already compressed formats are
    /// stored so the game can read them without inflating. Files stream through in
    /// chunks; on a seekable output nothing is buffered beyond one chunk.
    /// </summary>
    public sealed class RempackZipWriter
    {
        private const int Chunk = 81920;
        private const int SmallFile = 64;
        private static readonly string[] StoredExtensions = { ".ktx2", ".png", ".jpg", ".jpeg", ".ogg", ".webp" };

        private readonly SortedDictionary<string, RempackSource> _files =
            new SortedDictionary<string, RempackSource>(StringComparer.Ordinal);

        private readonly HashSet<string> _folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int Count
        {
            get { return _files.Count; }
        }

        public void Add(string path, byte[] data)
        {
            Add(RempackSource.FromBytes(path, data));
        }

        public void Add(RempackSource source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            var normalized = RempackPaths.Validate(source.Path);
            if (!_folded.Add(normalized))
            {
                throw new RempackException(RempackCodes.PathDuplicate, normalized);
            }

            if (_files.Count >= RempackLimits.Entries)
            {
                throw new RempackException(RempackCodes.TooManyFiles, normalized,
                    RempackLimits.Entries.ToString(CultureInfo.InvariantCulture));
            }

            if (source.Length > RempackLimits.FileBytes)
            {
                throw new RempackException(RempackCodes.FileTooLarge, normalized,
                    source.Length.ToString(CultureInfo.InvariantCulture));
            }

            _files.Add(normalized, source);
        }

        /// <summary>
        /// Writes the container. An output that can seek and stands at its end is written
        /// in place; any other output gets each entry buffered in memory first.
        /// </summary>
        public void Write(Stream output)
        {
            if (output == null)
            {
                throw new ArgumentNullException("output");
            }

            var inPlace = output.CanSeek && output.Position == output.Length;
            var central = new MemoryStream();
            long offset = 0;
            var count = 0;
            foreach (var pair in _files)
            {
                var name = Encoding.UTF8.GetBytes(pair.Key);
                var entry = inPlace
                    ? WriteInPlace(output, pair.Key, name, pair.Value)
                    : WriteBuffered(output, pair.Key, name, pair.Value);

                Put32(central, 0x02014b50);
                Put16(central, 0x0314);
                Put16(central, 20);
                Put16(central, 0x0800);
                Put16(central, entry.Method);
                Put16(central, 0);
                Put16(central, 0x0021);
                Put32(central, entry.Crc);
                Put32(central, (uint)entry.Compressed);
                Put32(central, (uint)entry.Size);
                Put16(central, (ushort)name.Length);
                Put16(central, 0);
                Put16(central, 0);
                Put16(central, 0);
                Put16(central, 0);
                Put32(central, 0x81A40000u);
                Put32(central, (uint)offset);
                central.Write(name, 0, name.Length);
                offset += 30 + name.Length + entry.Compressed;
                count++;
                if (offset > RempackLimits.ArchiveBytes)
                {
                    throw new RempackException(RempackCodes.ArchiveTooLarge, pair.Key, offset.ToString(CultureInfo.InvariantCulture));
                }
            }

            var directory = central.ToArray();
            output.Write(directory, 0, directory.Length);
            var end = new MemoryStream();
            Put32(end, 0x06054b50);
            Put16(end, 0);
            Put16(end, 0);
            Put16(end, (ushort)count);
            Put16(end, (ushort)count);
            Put32(end, (uint)directory.Length);
            Put32(end, (uint)offset);
            Put16(end, 0);
            var tail = end.ToArray();
            output.Write(tail, 0, tail.Length);
            if (inPlace && output.Length > output.Position)
            {
                // A stored fallback shorter than the deflated try leaves a stale tail.
                output.SetLength(output.Position);
            }
        }

        public byte[] ToArray()
        {
            using (var memory = new MemoryStream())
            {
                Write(memory);
                return memory.ToArray();
            }
        }

        private struct EntryData
        {
            public ushort Method;
            public uint Crc;
            public long Size;
            public long Compressed;
        }

        private static EntryData WriteInPlace(Stream output, string path, byte[] name, RempackSource source)
        {
            var headerAt = output.Position;
            var placeholder = LocalHeader(name, new EntryData());
            output.Write(placeholder, 0, placeholder.Length);
            var entry = Encode(path, source, output);
            var endAt = output.Position;
            output.Position = headerAt;
            var header = LocalHeader(name, entry);
            output.Write(header, 0, header.Length);
            output.Position = endAt;
            return entry;
        }

        private static EntryData WriteBuffered(Stream output, string path, byte[] name, RempackSource source)
        {
            using (var buffer = new MemoryStream())
            {
                var entry = Encode(path, source, buffer);
                var header = LocalHeader(name, entry);
                output.Write(header, 0, header.Length);
                output.Write(buffer.GetBuffer(), 0, (int)entry.Compressed);
                return entry;
            }
        }

        /// <summary>
        /// Writes the payload at the sink's position: deflated when that saves at least a
        /// twentieth, stored otherwise (the sink is rewound and the source read again).
        /// </summary>
        private static EntryData Encode(string path, RempackSource source, Stream sink)
        {
            var start = sink.Position;
            EntryData entry;
            if (!IsStored(path) && (source.Length < 0 || source.Length > SmallFile))
            {
                var counter = new CountingSink(sink);
                using (var input = source.Open())
                {
                    using (var deflate = new DeflateStream(counter, CompressionLevel.Optimal, true))
                    {
                        entry = Pump(path, input, deflate);
                    }
                }

                if (entry.Size > SmallFile && counter.Count < entry.Size - (entry.Size / 20))
                {
                    entry.Method = 8;
                    entry.Compressed = counter.Count;
                    return entry;
                }

                sink.Position = start;
            }

            using (var input = source.Open())
            {
                entry = Pump(path, input, sink);
            }

            entry.Method = 0;
            entry.Compressed = entry.Size;
            return entry;
        }

        private static EntryData Pump(string path, Stream input, Stream target)
        {
            var entry = new EntryData();
            var crc = 0xFFFFFFFFu;
            var buffer = new byte[Chunk];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                entry.Size += read;
                if (entry.Size > RempackLimits.FileBytes)
                {
                    throw new RempackException(RempackCodes.FileTooLarge, path, entry.Size.ToString(CultureInfo.InvariantCulture));
                }

                crc = RempackZipReader.Crc32Update(crc, buffer, 0, read);
                target.Write(buffer, 0, read);
            }

            entry.Crc = crc ^ 0xFFFFFFFFu;
            return entry;
        }

        private static byte[] LocalHeader(byte[] name, EntryData entry)
        {
            var local = new MemoryStream(30 + name.Length);
            Put32(local, 0x04034b50);
            Put16(local, 20);
            Put16(local, 0x0800);
            Put16(local, entry.Method);
            Put16(local, 0);
            Put16(local, 0x0021);
            Put32(local, entry.Crc);
            Put32(local, (uint)entry.Compressed);
            Put32(local, (uint)entry.Size);
            Put16(local, (ushort)name.Length);
            Put16(local, 0);
            local.Write(name, 0, name.Length);
            return local.ToArray();
        }

        private static bool IsStored(string path)
        {
            var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(StoredExtensions, extension) >= 0;
        }

        private static void Put16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
        }

        private static void Put32(Stream stream, uint value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }

        /// <summary>Forwards writes and counts them; leaves the inner stream open.</summary>
        private sealed class CountingSink : Stream
        {
            private readonly Stream _inner;

            public CountingSink(Stream inner)
            {
                _inner = inner;
            }

            public long Count { get; private set; }

            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }

            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
                Count += count;
            }
        }
    }
}
