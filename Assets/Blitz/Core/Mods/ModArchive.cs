using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BlitzLegacy.Core.Mods
{
    /// <summary>
    /// The container formats the Godot mods were shipped in. The scene packed
    /// them as .rar, later mirrors repacked the same trees as .zip, .7z and
    /// .tar/.tar.gz, so the installer has to recognise a mod by its bytes and
    /// not by the extension somebody typed.
    /// </summary>
    public enum ModArchiveFormat
    {
        Unknown = 0,
        Zip = 1,
        Rar4 = 2,
        Rar5 = 3,
        SevenZip = 4,
        Tar = 5,
        TarGz = 6,
    }

    public sealed class ModArchiveEntry
    {
        public string Path = string.Empty;
        public long Size;
        public bool IsDirectory;
    }

    /// <summary>
    /// Thrown when the archive is readable but this build cannot unpack it,
    /// e.g. a solid RAR block. The screen shows <see cref="Reason"/> so the
    /// player knows to repack rather than seeing a silent failure.
    /// </summary>
    public sealed class ModArchiveException : Exception
    {
        public ModArchiveException(string reason) : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; private set; }
    }

    /// <summary>
    /// A dependency-free reader for the archive formats the old mods travel in.
    ///
    /// Zip, tar and tar.gz are decoded in full. RAR and 7z carry entries that
    /// are merely stored, and those are decoded too; a compressed RAR or 7z
    /// payload needs a system unpacker, which <see cref="ModUnpacker"/> looks
    /// for before falling back to this reader.
    /// </summary>
    public static class ModArchive
    {
        private static readonly byte[] Rar4Signature =
            { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };

        private static readonly byte[] Rar5Signature =
            { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };

        private static readonly byte[] SevenZipSignature =
            { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

        /// <summary>The extensions the file picker offers.</summary>
        public static readonly string[] Extensions =
            { ".rar", ".zip", ".7z", ".tar", ".tar.gz", ".tgz", ".blitzmod", ".modpack" };

        public static bool LooksLikeArchive(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var lower = path.ToLowerInvariant();
            for (var i = 0; i < Extensions.Length; i++)
            {
                if (lower.EndsWith(Extensions[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Sniffs the container by its magic bytes.</summary>
        public static ModArchiveFormat Detect(Stream stream)
        {
            if (stream == null || !stream.CanSeek)
            {
                return ModArchiveFormat.Unknown;
            }

            var origin = stream.Position;
            var head = new byte[512];
            var read = Fill(stream, head, 0, head.Length);
            stream.Position = origin;
            return Detect(head, read);
        }

        public static ModArchiveFormat Detect(byte[] head, int length)
        {
            if (head == null || length < 4)
            {
                return ModArchiveFormat.Unknown;
            }

            if (Starts(head, length, Rar5Signature))
            {
                return ModArchiveFormat.Rar5;
            }

            if (Starts(head, length, Rar4Signature))
            {
                return ModArchiveFormat.Rar4;
            }

            if (Starts(head, length, SevenZipSignature))
            {
                return ModArchiveFormat.SevenZip;
            }

            if (head[0] == 0x50 && head[1] == 0x4B
                && (head[2] == 0x03 || head[2] == 0x05 || head[2] == 0x07))
            {
                return ModArchiveFormat.Zip;
            }

            if (head[0] == 0x1F && head[1] == 0x8B)
            {
                return ModArchiveFormat.TarGz;
            }

            // ustar lives at offset 257 of the first tar header block.
            if (length >= 265
                && head[257] == (byte)'u' && head[258] == (byte)'s'
                && head[259] == (byte)'t' && head[260] == (byte)'a'
                && head[261] == (byte)'r')
            {
                return ModArchiveFormat.Tar;
            }

            return ModArchiveFormat.Unknown;
        }

        public static ModArchiveFormat DetectFile(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                return Detect(stream);
            }
        }

        /// <summary>
        /// Unpacks <paramref name="archivePath"/> under <paramref
        /// name="destination"/> and returns what landed there. Entries that
        /// try to escape the destination are dropped, because a mod archive is
        /// downloaded content and nothing stops it from carrying "../" names.
        /// </summary>
        public static List<ModArchiveEntry> Extract(string archivePath, string destination)
        {
            if (string.IsNullOrEmpty(archivePath))
            {
                throw new ArgumentNullException("archivePath");
            }

            if (string.IsNullOrEmpty(destination))
            {
                throw new ArgumentNullException("destination");
            }

            Directory.CreateDirectory(destination);
            var format = DetectFile(archivePath);
            using (var stream = File.OpenRead(archivePath))
            {
                return Extract(stream, format, destination);
            }
        }

        public static List<ModArchiveEntry> Extract(
            Stream stream, ModArchiveFormat format, string destination)
        {
            switch (format)
            {
                case ModArchiveFormat.Zip:
                    return ExtractZip(stream, destination);
                case ModArchiveFormat.Tar:
                    return ExtractTar(stream, destination);
                case ModArchiveFormat.TarGz:
                    using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                    {
                        return ExtractTar(gzip, destination);
                    }
                case ModArchiveFormat.Rar4:
                    return ExtractRar4(stream, destination);
                case ModArchiveFormat.Rar5:
                    return ExtractRar5(stream, destination);
                case ModArchiveFormat.SevenZip:
                    throw new ModArchiveException("mods_error_7z_needs_tool");
                default:
                    throw new ModArchiveException("mods_error_unknown_format");
            }
        }

        // ------------------------------------------------------------------
        // Zip.
        // ------------------------------------------------------------------

        private static List<ModArchiveEntry> ExtractZip(Stream stream, string destination)
        {
            var entries = new List<ModArchiveEntry>();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
            {
                for (var i = 0; i < zip.Entries.Count; i++)
                {
                    var entry = zip.Entries[i];
                    var name = Normalize(entry.FullName);
                    if (name == null)
                    {
                        continue;
                    }

                    if (name.EndsWith("/", StringComparison.Ordinal) || entry.Length == 0
                        && string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(Path.Combine(destination, name));
                        entries.Add(new ModArchiveEntry { Path = name, IsDirectory = true });
                        continue;
                    }

                    using (var source = entry.Open())
                    {
                        Write(destination, name, source, entry.Length);
                    }

                    entries.Add(new ModArchiveEntry { Path = name, Size = entry.Length });
                }
            }

            return entries;
        }

        // ------------------------------------------------------------------
        // Tar.
        // ------------------------------------------------------------------

        private static List<ModArchiveEntry> ExtractTar(Stream stream, string destination)
        {
            var entries = new List<ModArchiveEntry>();
            var header = new byte[512];
            var longName = string.Empty;
            while (true)
            {
                if (Fill(stream, header, 0, 512) < 512 || IsZeroBlock(header))
                {
                    break;
                }

                var name = Ascii(header, 0, 100);
                var prefix = Ascii(header, 345, 155);
                if (!string.IsNullOrEmpty(prefix))
                {
                    name = prefix + "/" + name;
                }

                if (!string.IsNullOrEmpty(longName))
                {
                    name = longName;
                    longName = string.Empty;
                }

                var size = Octal(header, 124, 12);
                var type = (char)header[156];
                var padded = (size + 511L) / 512L * 512L;

                if (type == 'L')
                {
                    var buffer = new byte[size];
                    Fill(stream, buffer, 0, buffer.Length);
                    Skip(stream, padded - size);
                    longName = Encoding.UTF8.GetString(buffer).TrimEnd('\0');
                    continue;
                }

                var normalized = Normalize(name);
                if (normalized == null || type == 'x' || type == 'g')
                {
                    Skip(stream, padded);
                    continue;
                }

                if (type == '5')
                {
                    Directory.CreateDirectory(Path.Combine(destination, normalized));
                    entries.Add(new ModArchiveEntry { Path = normalized, IsDirectory = true });
                    Skip(stream, padded);
                    continue;
                }

                if (type != '0' && type != '\0')
                {
                    Skip(stream, padded);
                    continue;
                }

                using (var slice = new SubStream(stream, size))
                {
                    Write(destination, normalized, slice, size);
                }

                Skip(stream, padded - size);
                entries.Add(new ModArchiveEntry { Path = normalized, Size = size });
            }

            return entries;
        }

        // ------------------------------------------------------------------
        // RAR.
        // ------------------------------------------------------------------

        private static List<ModArchiveEntry> ExtractRar4(Stream stream, string destination)
        {
            var entries = new List<ModArchiveEntry>();
            stream.Position = Rar4Signature.Length;
            var reader = new BinaryReader(stream, Encoding.UTF8, true);
            var stored = 0;
            var skipped = 0;
            while (stream.Position + 7 <= stream.Length)
            {
                var start = stream.Position;
                reader.ReadUInt16();
                var type = reader.ReadByte();
                var flags = reader.ReadUInt16();
                var headerSize = reader.ReadUInt16();
                if (headerSize < 7)
                {
                    break;
                }

                long dataSize = 0;
                if ((flags & 0x8000) != 0)
                {
                    dataSize = reader.ReadUInt32();
                }

                if (type == 0x74)
                {
                    var unpacked = (long)reader.ReadUInt32();
                    reader.ReadByte();
                    reader.ReadUInt32();
                    reader.ReadUInt32();
                    reader.ReadByte();
                    var method = reader.ReadByte();
                    var nameSize = reader.ReadUInt16();
                    reader.ReadUInt32();
                    if ((flags & 0x0100) != 0)
                    {
                        dataSize |= (long)reader.ReadUInt32() << 32;
                        unpacked |= (long)reader.ReadUInt32() << 32;
                    }

                    var nameBytes = reader.ReadBytes(nameSize);
                    var name = Normalize(Encoding.UTF8.GetString(nameBytes).Split('\0')[0]);
                    var isDirectory = (flags & 0xE0) == 0xE0;
                    stream.Position = start + headerSize;
                    if (name != null && isDirectory)
                    {
                        Directory.CreateDirectory(Path.Combine(destination, name));
                        entries.Add(new ModArchiveEntry { Path = name, IsDirectory = true });
                    }
                    else if (name != null && method == 0x30)
                    {
                        using (var slice = new SubStream(stream, dataSize))
                        {
                            Write(destination, name, slice, unpacked);
                        }

                        entries.Add(new ModArchiveEntry { Path = name, Size = unpacked });
                        stored++;
                    }
                    else if (name != null)
                    {
                        skipped++;
                    }

                    stream.Position = start + headerSize + dataSize;
                    continue;
                }

                stream.Position = start + headerSize + dataSize;
            }

            if (skipped > 0 || (stored == 0 && entries.Count == 0))
            {
                throw new ModArchiveException("mods_error_rar_needs_tool");
            }

            return entries;
        }

        private static List<ModArchiveEntry> ExtractRar5(Stream stream, string destination)
        {
            var entries = new List<ModArchiveEntry>();
            stream.Position = Rar5Signature.Length;
            var stored = 0;
            var skipped = 0;
            while (stream.Position + 4 < stream.Length)
            {
                var crcEnd = stream.Position + 4;
                stream.Position = crcEnd;
                long headerSize;
                if (!TryVarInt(stream, out headerSize) || headerSize <= 0)
                {
                    break;
                }

                var headerStart = stream.Position;
                var headerEnd = headerStart + headerSize;
                long type, headerFlags;
                if (!TryVarInt(stream, out type) || !TryVarInt(stream, out headerFlags))
                {
                    break;
                }

                long extraSize = 0;
                long dataSize = 0;
                if ((headerFlags & 0x0001) != 0 && !TryVarInt(stream, out extraSize))
                {
                    break;
                }

                if ((headerFlags & 0x0002) != 0 && !TryVarInt(stream, out dataSize))
                {
                    break;
                }

                if (type == 2)
                {
                    long fileFlags, unpacked, attributes, compression, hostOs, nameLength;
                    if (!TryVarInt(stream, out fileFlags)
                        || !TryVarInt(stream, out unpacked)
                        || !TryVarInt(stream, out attributes))
                    {
                        break;
                    }

                    if ((fileFlags & 0x0002) != 0)
                    {
                        Skip(stream, 4);
                    }

                    if ((fileFlags & 0x0004) != 0)
                    {
                        Skip(stream, 4);
                    }

                    if (!TryVarInt(stream, out compression)
                        || !TryVarInt(stream, out hostOs)
                        || !TryVarInt(stream, out nameLength))
                    {
                        break;
                    }

                    var nameBytes = new byte[nameLength];
                    Fill(stream, nameBytes, 0, nameBytes.Length);
                    var name = Normalize(Encoding.UTF8.GetString(nameBytes));
                    var method = (compression >> 7) & 7;
                    var isDirectory = (fileFlags & 0x0001) != 0;
                    stream.Position = headerEnd;
                    if (name != null && isDirectory)
                    {
                        Directory.CreateDirectory(Path.Combine(destination, name));
                        entries.Add(new ModArchiveEntry { Path = name, IsDirectory = true });
                    }
                    else if (name != null && method == 0)
                    {
                        using (var slice = new SubStream(stream, dataSize))
                        {
                            Write(destination, name, slice, unpacked);
                        }

                        entries.Add(new ModArchiveEntry { Path = name, Size = unpacked });
                        stored++;
                    }
                    else if (name != null)
                    {
                        skipped++;
                    }

                    stream.Position = headerEnd + dataSize;
                    continue;
                }

                if (type == 5)
                {
                    break;
                }

                stream.Position = headerEnd + dataSize;
            }

            if (skipped > 0 || (stored == 0 && entries.Count == 0))
            {
                throw new ModArchiveException("mods_error_rar_needs_tool");
            }

            return entries;
        }

        private static bool TryVarInt(Stream stream, out long value)
        {
            value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var read = stream.ReadByte();
                if (read < 0)
                {
                    return false;
                }

                value |= (long)(read & 0x7F) << shift;
                if ((read & 0x80) == 0)
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Shared plumbing.
        // ------------------------------------------------------------------

        /// <summary>
        /// Cleans an archive path into a relative one, or null when it points
        /// outside the destination.
        /// </summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var text = path.Replace('\\', '/').Trim();
            while (text.StartsWith("./", StringComparison.Ordinal))
            {
                text = text.Substring(2);
            }

            if (text.Length == 0 || text.StartsWith("/", StringComparison.Ordinal)
                || text.Contains(":"))
            {
                return null;
            }

            var parts = text.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "..")
                {
                    return null;
                }
            }

            return text;
        }

        private static void Write(string destination, string name, Stream source, long size)
        {
            var full = Path.Combine(destination, name);
            var folder = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            using (var target = File.Create(full))
            {
                var buffer = new byte[64 * 1024];
                var left = size;
                while (left > 0)
                {
                    var want = (int)Math.Min(buffer.Length, left);
                    var read = source.Read(buffer, 0, want);
                    if (read <= 0)
                    {
                        break;
                    }

                    target.Write(buffer, 0, read);
                    left -= read;
                }
            }
        }

        private static bool Starts(byte[] head, int length, byte[] signature)
        {
            if (length < signature.Length)
            {
                return false;
            }

            for (var i = 0; i < signature.Length; i++)
            {
                if (head[i] != signature[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static int Fill(Stream stream, byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        private static void Skip(Stream stream, long count)
        {
            if (count <= 0)
            {
                return;
            }

            if (stream.CanSeek)
            {
                stream.Position += count;
                return;
            }

            var buffer = new byte[8192];
            while (count > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0)
                {
                    return;
                }

                count -= read;
            }
        }

        private static bool IsZeroBlock(byte[] block)
        {
            for (var i = 0; i < block.Length; i++)
            {
                if (block[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static string Ascii(byte[] buffer, int offset, int count)
        {
            var end = offset;
            while (end < offset + count && buffer[end] != 0)
            {
                end++;
            }

            return Encoding.UTF8.GetString(buffer, offset, end - offset).Trim();
        }

        private static long Octal(byte[] buffer, int offset, int count)
        {
            var text = Ascii(buffer, offset, count).Trim();
            long value = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '7')
                {
                    break;
                }

                value = value * 8 + (text[i] - '0');
            }

            return value;
        }

        /// <summary>A window over the archive so one entry cannot read past itself.</summary>
        private sealed class SubStream : Stream
        {
            private readonly Stream _inner;
            private long _left;

            public SubStream(Stream inner, long length)
            {
                _inner = inner;
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
                if (_left <= 0)
                {
                    return 0;
                }

                var read = _inner.Read(buffer, offset, (int)Math.Min(count, _left));
                _left -= read;
                return read;
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
        }
    }
}
