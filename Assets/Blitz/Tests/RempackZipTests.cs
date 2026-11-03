using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Mods.Rempack;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class RempackZipTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "rempack_zip_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Test]
        public void Writer_RoundTrip_ReadsEveryFileBack()
        {
            var files = new Dictionary<string, byte[]>
            {
                { "a.json", Encoding.UTF8.GetBytes(new string('x', 5000)) },
                { "tanks/t1/icon.png", RempackFixture.Png(64, 64) },
                { "empty.bin", new byte[0] },
                { "tanks/t1/model.bin", Noise(70000) },
            };
            var writer = new RempackZipWriter();
            foreach (var pair in files)
            {
                writer.Add(pair.Key, pair.Value);
            }

            using (var reader = RempackZipReader.Open(new MemoryStream(writer.ToArray()), false))
            {
                Assert.That(reader.Entries.Count, Is.EqualTo(files.Count));
                foreach (var pair in files)
                {
                    var entry = reader.Find(pair.Key);
                    Assert.That(entry, Is.Not.Null, pair.Key);
                    CollectionAssert.AreEqual(pair.Value, reader.ReadAll(entry, long.MaxValue), pair.Key);
                    Assert.That(reader.Sha256(entry), Is.EqualTo(RempackBuilder.Sha256(pair.Value)), pair.Key);
                }

                Assert.That(reader.Find("a.json").Method, Is.EqualTo(8), "text compresses");
                Assert.That(reader.Find("tanks/t1/icon.png").Method, Is.EqualTo(0), "PNG is stored as is");
                Assert.That(reader.Find("A.JSON"), Is.Not.Null, "lookups ignore case");
            }
        }

        [Test]
        public void Writer_SameFilesInAnyOrder_GiveTheSameBytes()
        {
            var first = new RempackZipWriter();
            first.Add("b.txt", Encoding.UTF8.GetBytes("bbb"));
            first.Add("a/c.txt", Encoding.UTF8.GetBytes("ccc"));
            var second = new RempackZipWriter();
            second.Add("a/c.txt", Encoding.UTF8.GetBytes("ccc"));
            second.Add("b.txt", Encoding.UTF8.GetBytes("bbb"));
            CollectionAssert.AreEqual(first.ToArray(), second.ToArray());
        }

        [TestCase("../evil.txt")]
        [TestCase("a/../../evil.txt")]
        [TestCase("/etc/passwd")]
        [TestCase("C:/evil.txt")]
        [TestCase("a\\b.txt")]
        [TestCase(".hidden")]
        [TestCase("con.txt")]
        [TestCase("name with space.txt")]
        public void Writer_RefusesUnsafeNames(string name)
        {
            var error = Assert.Throws<RempackException>(() => new RempackZipWriter().Add(name, new byte[1]));
            Assert.That(error.Code, Is.EqualTo(RempackCodes.PathInvalid));
        }

        [Test]
        public void Writer_RefusesNamesThatDifferOnlyInCase()
        {
            var writer = new RempackZipWriter();
            writer.Add("Tank.json", new byte[1]);
            var error = Assert.Throws<RempackException>(() => writer.Add("tank.json", new byte[1]));
            Assert.That(error.Code, Is.EqualTo(RempackCodes.PathDuplicate));
        }

        [TestCase("../evil.txt")]
        [TestCase("/abs.txt")]
        [TestCase("a\\..\\evil.txt")]
        [TestCase("C:/evil.txt")]
        [TestCase("dir/../../evil.txt")]
        public void Reader_RefusesTraversal(string name)
        {
            AssertRefused(new RawZip().Add("ok.txt", new byte[3]).Add(name, new byte[3]).ToArray(), RempackCodes.PathInvalid);
        }

        [Test]
        public void Reader_RefusesNamesThatDifferOnlyInCase()
        {
            AssertRefused(new RawZip().Add("A.txt", new byte[1]).Add("a.txt", new byte[1]).ToArray(), RempackCodes.PathDuplicate);
        }

        [Test]
        public void Reader_RefusesEncryptedEntries()
        {
            AssertRefused(new RawZip().Add("a.txt", new byte[4], flags: 0x0801).ToArray(), RempackCodes.ZipEncrypted);
        }

        [Test]
        public void Reader_RefusesUnknownCompression()
        {
            AssertRefused(new RawZip().Add("a.txt", new byte[4], method: 14).ToArray(), RempackCodes.ZipMethod);
        }

        [Test]
        public void Reader_RefusesZip64Markers()
        {
            AssertRefused(new RawZip().Add("a.txt", new byte[4], method: 8, size: 0xFFFFFFFF).ToArray(), RempackCodes.ZipZip64);
        }

        [Test]
        public void Reader_RefusesSplitArchives()
        {
            var zip = new RawZip { Disk = 1 };
            AssertRefused(zip.Add("a.txt", new byte[4]).ToArray(), RempackCodes.ZipMultiDisk);
        }

        [Test]
        public void Reader_RefusesUnixSymlinks()
        {
            AssertRefused(new RawZip().Add("link", Encoding.UTF8.GetBytes("/etc/passwd"), madeBy: 0x0314, external: 0xA1FF0000).ToArray(),
                RempackCodes.PathLink);
        }

        [Test]
        public void Reader_RefusesWindowsReparsePoints()
        {
            AssertRefused(new RawZip().Add("link", new byte[2], madeBy: 20, external: 0x400).ToArray(), RempackCodes.PathLink);
        }

        [Test]
        public void Reader_RefusesCompressionBombs()
        {
            AssertRefused(new RawZip().Add("bomb.bin", new byte[2000], method: 8, size: 100L << 20).ToArray(), RempackCodes.ZipBomb);
        }

        [Test]
        public void Reader_RefusesEntriesSharingBytes()
        {
            AssertRefused(new RawZip().AddNested("outer.bin", "inner.bin", Noise(100)).ToArray(), RempackCodes.ZipBomb);
        }

        [Test]
        public void Reader_RefusesStoredSizeMismatch()
        {
            AssertRefused(new RawZip().Add("a.bin", new byte[10], size: 20).ToArray(), RempackCodes.ZipSizeMismatch);
        }

        [Test]
        public void Reader_RefusesOversizedFiles()
        {
            AssertRefused(new RawZip().Add("big.bin", new byte[16], method: 8, size: RempackLimits.FileBytes + 1).ToArray(), RempackCodes.FileTooLarge);
        }

        [Test]
        public void Reader_RefusesTooManyEntries()
        {
            var zip = new RawZip();
            for (var i = 0; i <= RempackLimits.Entries; i++)
            {
                zip.Add("f" + i, new byte[0]);
            }

            AssertRefused(zip.ToArray(), RempackCodes.TooManyFiles);
        }

        [Test]
        public void Reader_RefusesArchivesOverTheSizeLimit()
        {
            var data = new RawZip().Add("a.txt", new byte[4]).ToArray();
            var error = Assert.Throws<RempackException>(() => RempackZipReader.Open(new LongStream(data, RempackLimits.ArchiveBytes + 1), false));
            Assert.That(error.Code, Is.EqualTo(RempackCodes.ArchiveTooLarge));
        }

        [TestCase(0)]
        [TestCase(10)]
        [TestCase(21)]
        public void Reader_RefusesTruncatedFiles(int keep)
        {
            var data = new RawZip().Add("a.txt", new byte[40]).ToArray();
            var cut = new byte[Math.Min(keep, data.Length)];
            Array.Copy(data, cut, cut.Length);
            AssertRefused(cut, RempackCodes.ZipInvalid);
        }

        [Test]
        public void Reader_RefusesFilesThatAreNotZip()
        {
            AssertRefused(Noise(4096), RempackCodes.ZipInvalid);
        }

        [Test]
        public void Reader_RefusesDirectoriesWithData()
        {
            AssertRefused(new RawZip().Add("dir/", new byte[3]).ToArray(), RempackCodes.ZipInvalid);
        }

        [Test]
        public void Reader_SkipsArchiverLeftovers()
        {
            var data = new RawZip()
                .Add("__MACOSX/._a.txt", new byte[3])
                .Add(".DS_Store", new byte[3])
                .Add("sub/Thumbs.db", new byte[3])
                .Add("dir/", new byte[0])
                .Add("dir/a.txt", new byte[3])
                .ToArray();
            using (var reader = RempackZipReader.Open(new MemoryStream(data), false))
            {
                Assert.That(reader.Entries.Count, Is.EqualTo(1));
                Assert.That(reader.Entries[0].Path, Is.EqualTo("dir/a.txt"));
            }
        }

        [Test]
        public void ReadAll_DetectsDamagedStoredData()
        {
            var data = new RawZip().Add("a.bin", Noise(64)).ToArray();
            data[30 + 5 + 10] ^= 0xFF;
            using (var reader = RempackZipReader.Open(new MemoryStream(data), false))
            {
                var error = Assert.Throws<RempackException>(() => reader.ReadAll(reader.Find("a.bin"), long.MaxValue));
                Assert.That(error.Code, Is.EqualTo(RempackCodes.ZipCrcMismatch));
                error = Assert.Throws<RempackException>(() => reader.Sha256(reader.Find("a.bin")));
                Assert.That(error.Code, Is.EqualTo(RempackCodes.ZipCrcMismatch));
            }
        }

        [Test]
        public void ReadAll_DetectsAWrongCrc()
        {
            var data = new RawZip().Add("a.txt", Encoding.UTF8.GetBytes(new string('a', 300)), method: 8, crc: 12345).ToArray();
            using (var reader = RempackZipReader.Open(new MemoryStream(data), false))
            {
                var error = Assert.Throws<RempackException>(() => reader.ReadAll(reader.Find("a.txt"), long.MaxValue));
                Assert.That(error.Code, Is.EqualTo(RempackCodes.ZipCrcMismatch));
            }
        }

        [Test]
        public void ReadAll_RefusesEntriesOverTheCallersLimit()
        {
            var data = new RawZip().Add("a.bin", new byte[100]).ToArray();
            using (var reader = RempackZipReader.Open(new MemoryStream(data), false))
            {
                var error = Assert.Throws<RempackException>(() => reader.ReadAll(reader.Find("a.bin"), 99));
                Assert.That(error.Code, Is.EqualTo(RempackCodes.FileTooLarge));
            }
        }

        [Test]
        public void ReadText_RefusesInvalidUtf8()
        {
            var data = new RawZip().Add("a.json", new byte[] { 0x7B, 0xC3, 0x28, 0x7D }).ToArray();
            using (var reader = RempackZipReader.Open(new MemoryStream(data), false))
            {
                var error = Assert.Throws<RempackException>(() => reader.ReadText(reader.Find("a.json"), 1024));
                Assert.That(error.Code, Is.EqualTo(RempackCodes.TextNotUtf8));
            }
        }

        [Test]
        public void ReadText_SkipsTheByteOrderMark()
        {
            var data = new RawZip().Add("a.json", new byte[] { 0xEF, 0xBB, 0xBF, 0x7B, 0x7D }).ToArray();
            using (var reader = RempackZipReader.Open(new MemoryStream(data), false))
            {
                Assert.That(reader.ReadText(reader.Find("a.json"), 1024), Is.EqualTo("{}"));
            }
        }

        [Test]
        public void CopyTo_StreamsALargeEntry()
        {
            var payload = Noise(300000);
            var writer = new RempackZipWriter();
            writer.Add("music/theme.bin", payload);
            using (var reader = RempackZipReader.Open(new MemoryStream(writer.ToArray()), false))
            {
                var output = new MemoryStream();
                reader.CopyTo(reader.Find("music/theme.bin"), output);
                CollectionAssert.AreEqual(payload, output.ToArray());
            }
        }

        [Test]
        public void OpenFile_KeepsReadingTheOpenedFileWhenItIsReplaced()
        {
            var path = Path.Combine(_root, "mod.rempack");
            var first = new RempackZipWriter();
            first.Add("a.bin", Noise(5000, 1));
            File.WriteAllBytes(path, first.ToArray());
            using (var reader = RempackZipReader.Open(path))
            {
                // An update renames the old file away and puts a new one in its place.
                File.Move(path, path + ".old");
                var second = new RempackZipWriter();
                second.Add("b.bin", Noise(9000, 2));
                File.WriteAllBytes(path, second.ToArray());
                File.Delete(path + ".old");

                CollectionAssert.AreEqual(Noise(5000, 1), reader.ReadAll(reader.Find("a.bin"), long.MaxValue));
                var output = new MemoryStream();
                reader.CopyTo(reader.Find("a.bin"), output);
                CollectionAssert.AreEqual(Noise(5000, 1), output.ToArray());
            }
        }

        [Test]
        public void Writer_FileSourcesGiveTheSameBytesAsInMemoryOnes()
        {
            var text = Encoding.UTF8.GetBytes(new string('t', 200000));
            var noise = Noise(200000, 3);
            File.WriteAllBytes(Path.Combine(_root, "text.json"), text);
            File.WriteAllBytes(Path.Combine(_root, "noise.bin"), noise);

            var memory = new RempackZipWriter();
            memory.Add("a/text.json", text);
            memory.Add("a/noise.bin", noise);
            var files = new RempackZipWriter();
            files.Add(RempackSource.FromFile("a/text.json", Path.Combine(_root, "text.json")));
            files.Add(RempackSource.FromFile("a/noise.bin", Path.Combine(_root, "noise.bin")));
            CollectionAssert.AreEqual(memory.ToArray(), files.ToArray());
        }

        [Test]
        public void Writer_GivesTheSameBytesToAStreamThatCannotSeek()
        {
            var writer = new RempackZipWriter();
            writer.Add("a.json", Encoding.UTF8.GetBytes(new string('x', 100000)));
            writer.Add("b.bin", Noise(100000, 4));
            writer.Add("c.txt", Encoding.UTF8.GetBytes("small"));
            var seekable = writer.ToArray();
            var forward = new ForwardOnlyStream();
            writer.Write(forward);
            CollectionAssert.AreEqual(seekable, forward.ToArray());

            using (var reader = RempackZipReader.Open(new MemoryStream(seekable), false))
            {
                Assert.That(reader.Find("a.json").Method, Is.EqualTo(8));
                Assert.That(reader.Find("b.bin").Method, Is.EqualTo(0), "noise does not deflate");
                CollectionAssert.AreEqual(Noise(100000, 4), reader.ReadAll(reader.Find("b.bin"), long.MaxValue));
            }
        }

        [Test]
        public void Writer_StoredFallbackLeavesNoStaleTail()
        {
            // The last entry deflates to a few hundred bytes more than it holds; once it
            // is stored instead, those bytes must not linger past the end record.
            var writer = new RempackZipWriter();
            writer.Add("z.bin", Noise(4000000, 6));
            var path = Path.Combine(_root, "tail.rempack");
            using (var output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
            {
                writer.Write(output);
            }

            var forward = new ForwardOnlyStream();
            writer.Write(forward);
            CollectionAssert.AreEqual(forward.ToArray(), File.ReadAllBytes(path));
            using (var reader = RempackZipReader.Open(path))
            {
                CollectionAssert.AreEqual(Noise(4000000, 6), reader.ReadAll(reader.Find("z.bin"), long.MaxValue));
            }
        }

        [Test]
        public void Writer_ReadsAStreamSourceAgainWhenStoringPaysOff()
        {
            var opened = 0;
            var payload = Noise(50000, 5);
            var writer = new RempackZipWriter();
            writer.Add(RempackSource.FromStream("x.bin", delegate
            {
                opened++;
                return new MemoryStream(payload, false);
            }));
            var data = writer.ToArray();
            Assert.That(opened, Is.EqualTo(2), "one deflate try, one stored copy");
            using (var reader = RempackZipReader.Open(new MemoryStream(data), false))
            {
                CollectionAssert.AreEqual(payload, reader.ReadAll(reader.Find("x.bin"), long.MaxValue));
            }
        }

        [Test]
        public void Writer_RefusesSourcesOverTheFileLimitBeforeReadingThem()
        {
            var error = Assert.Throws<RempackException>(() => new RempackZipWriter().Add(RempackSource.FromStream(
                "big.bin", delegate { return new MemoryStream(); }, RempackLimits.FileBytes + 1)));
            Assert.That(error.Code, Is.EqualTo(RempackCodes.FileTooLarge));
        }

        [Test]
        public void Writer_RefusesMoreFilesThanAPackageMayHold()
        {
            var writer = new RempackZipWriter();
            for (var i = 0; i < RempackLimits.Entries; i++)
            {
                writer.Add("f" + i + ".txt", new byte[0]);
            }

            var error = Assert.Throws<RempackException>(() => writer.Add("one_more.txt", new byte[0]));
            Assert.That(error.Code, Is.EqualTo(RempackCodes.TooManyFiles));
        }

        [Test]
        public void Builder_PacksFilesFromDiskLikeFilesInMemory()
        {
            var fixture = RempackFixture.Tank().Bytes("tanks/t1/model.bin", Noise(150000, 8));
            var sources = new List<RempackSource>();
            foreach (var pair in fixture.Files)
            {
                var file = Path.Combine(_root, pair.Key.Replace('/', '_'));
                File.WriteAllBytes(file, pair.Value);
                sources.Add(RempackSource.FromFile(pair.Key, file));
            }

            var expected = fixture.Build();
            var path = Path.Combine(_root, "out.rempack");
            using (var output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
            {
                RempackBuilder.Pack(fixture.Manifest, sources, output);
            }

            CollectionAssert.AreEqual(expected, File.ReadAllBytes(path));
        }

        private static void AssertRefused(byte[] data, string code)
        {
            var error = Assert.Throws<RempackException>(() => RempackZipReader.Open(new MemoryStream(data), false).Dispose());
            Assert.That(error.Code, Is.EqualTo(code), error.Message);
        }

        internal static byte[] Noise(int length, int seed = 7)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        /// <summary>A write-only stream that cannot seek, like a pipe or a network socket.</summary>
        private sealed class ForwardOnlyStream : Stream
        {
            private readonly MemoryStream _inner = new MemoryStream();

            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }

            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public byte[] ToArray()
            {
                return _inner.ToArray();
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
            }
        }

        /// <summary>A stream that claims to be longer than it is, to reach the size limit cheaply.</summary>
        private sealed class LongStream : Stream
        {
            private readonly MemoryStream _inner;
            private readonly long _length;

            public LongStream(byte[] data, long length)
            {
                _inner = new MemoryStream(data);
                _length = length;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return _length; } }

            public override long Position
            {
                get { return _inner.Position; }
                set { _inner.Position = value; }
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _inner.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                return _inner.Seek(offset, origin);
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
