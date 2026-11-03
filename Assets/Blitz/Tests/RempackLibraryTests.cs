using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BlitzLegacy.Core.Mods;
using BlitzLegacy.Core.Mods.Rempack;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class RempackLibraryTests
    {
        private string _base;
        private string _root;
        private string _downloads;

        [SetUp]
        public void SetUp()
        {
            _base = Path.Combine(Path.GetTempPath(), "rempack_library_" + Guid.NewGuid().ToString("N"));
            _root = Path.Combine(_base, "mods");
            _downloads = Path.Combine(_base, "downloads");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_downloads);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_base))
            {
                Directory.Delete(_base, true);
            }
        }

        private RempackLibrary Library()
        {
            var library = new RempackLibrary(_root, RempackFixture.Options());
            library.Refresh();
            return library;
        }

        private static string[] Ids(IEnumerable<RempackMod> mods)
        {
            return mods.Select(m => m.Id.Length > 0 ? m.Id : m.FileName).ToArray();
        }

        [Test]
        public void Refresh_WithoutAFolder_IsEmpty()
        {
            var missing = Path.Combine(_base, "nowhere");
            var library = new RempackLibrary(missing, RempackFixture.Options());
            library.Refresh();

            Assert.That(library.Mods, Is.Empty);
            Assert.That(library.Active, Is.Empty);
            Assert.That(Directory.Exists(missing), Is.False, "reading creates nothing");
        }

        [Test]
        public void Refresh_LoadsValidPackagesInNameOrder()
        {
            RempackFixture.Tank("b.second").Write(_root);
            RempackFixture.Tank("a.first").Write(_root);
            var library = Library();

            Assert.That(Ids(library.Active), Is.EqualTo(new[] { "a.first", "b.second" }));
            Assert.That(library.Mods.All(m => m.Status == RempackModStatus.Active && m.Reason == null), Is.True);
            var state = ModState.Load(_root);
            Assert.That(state.Ids, Is.EqualTo(new[] { "a.first", "b.second" }), "new packages join the load order");
            Assert.That(state.IsEnabled("a.first"), Is.True);
        }

        [Test]
        public void Refresh_ShowsWhatMakesAFileInvalid()
        {
            File.WriteAllBytes(Path.Combine(_root, "junk.rempack"), new byte[] { 1, 2, 3, 4 });
            RempackFixture.Tank().Write(_root, "zzz.rempack");
            var library = Library();
            var junk = library.Mods.Single(m => m.FileName == "junk.rempack");

            Assert.That(junk.Status, Is.EqualTo(RempackModStatus.Invalid));
            Assert.That(junk.Id, Is.Empty);
            Assert.That(junk.Usable, Is.False);
            Assert.That(junk.Reason.Code, Is.EqualTo(RempackCodes.ZipInvalid));
            Assert.That(junk.DisplayName("en"), Is.EqualTo("junk.rempack"));
            Assert.That(library.Mods.Last(), Is.SameAs(junk), "files without an id come last");
            Assert.That(ModState.Load(_root).Ids, Is.EqualTo(new[] { "author.test_mod" }));
        }

        [Test]
        public void Refresh_KeepsTheIdOfAnInvalidPackage()
        {
            var broken = RempackFixture.Tank().Tampered(files => files.Set("tanks/t1/tank.json", new string('0', 64)));
            File.WriteAllBytes(Path.Combine(_root, "author.test_mod.rempack"), broken);
            var mod = Library().Find("author.test_mod");

            Assert.That(mod.Status, Is.EqualTo(RempackModStatus.Invalid));
            Assert.That(mod.Reason.Code, Is.EqualTo(RempackCodes.FileHashMismatch));
            Assert.That(mod.DisplayName("ru"), Is.EqualTo("\u0422\u0435\u0441\u0442\u043e\u0432\u044b\u0439 \u043c\u043e\u0434"));
        }

        [Test]
        public void Refresh_TellsANewerGameApartFromABrokenFile()
        {
            RempackFixture.Tank("a.features").Set("game", "{\"features\": [\"tanks@1\", \"scripts@1\"]}").Write(_root);
            RempackFixture.Tank("b.format").Set("formatVersion", "2").Write(_root);
            RempackFixture.Tank("c.game").Set("game", "{\"features\": [\"tanks@1\"], \"minVersion\": \"9.0.0\"}").Write(_root);
            var library = Library();

            Assert.That(library.Find("a.features").Reason.Code, Is.EqualTo(RempackCodes.FeatureUnsupported));
            Assert.That(library.Find("b.format").Reason.Code, Is.EqualTo(RempackCodes.ManifestFormatVersion));
            Assert.That(library.Find("c.game").Reason.Code, Is.EqualTo(RempackCodes.GameTooOld));
            Assert.That(library.Mods.All(m => m.Status == RempackModStatus.Unsupported), Is.True, string.Join(", ", library.Mods));
        }

        [Test]
        public void Refresh_IgnoresEverythingButPackageFiles()
        {
            RempackFixture.Tank().Write(_root);
            File.WriteAllText(Path.Combine(_root, "readme.txt"), "hello");
            RempackFixture.Tank("hidden.mod").Write(_root, ".hidden.rempack");
            Directory.CreateDirectory(Path.Combine(_root, "legacy_mod", "assets"));
            var library = Library();

            Assert.That(Ids(library.Mods), Is.EqualTo(new[] { "author.test_mod" }));
        }

        [Test]
        public void Refresh_MarksHeavyPackagesAndStockReplacements()
        {
            var pad = new string('x', (int)RempackLimits.JsonSoftBytes);
            RempackFixture.Tank("a.heavy").Text("tanks/t1/tank.json", "{\"pad\": \"" + pad + "\"}").Write(_root);
            RempackFixture.Tank("b.light").Overrides("stock:tank/t-34").Write(_root);
            var library = Library();

            Assert.That(library.Find("a.heavy").Heavy, Is.True);
            Assert.That(library.Find("a.heavy").IsActive, Is.True);
            Assert.That(library.Find("b.light").Heavy, Is.False);
            Assert.That(library.Find("b.light").ReplacesStock, Is.True);
        }

        [Test]
        public void SetEnabled_SwitchesOffAndIsRemembered()
        {
            RempackFixture.Tank("a.first").Write(_root);
            RempackFixture.Tank("b.second").Write(_root);
            var library = Library();
            library.SetEnabled("a.first", false);

            Assert.That(library.Find("a.first").Status, Is.EqualTo(RempackModStatus.Disabled));
            Assert.That(library.Find("a.first").Reason, Is.Null);
            Assert.That(Ids(library.Active), Is.EqualTo(new[] { "b.second" }));
            var again = Library();
            Assert.That(again.Find("a.first").Enabled, Is.False);
            Assert.That(Ids(again.Active), Is.EqualTo(new[] { "b.second" }));

            again.SetEnabled("a.first", true);
            Assert.That(Ids(Library().Active), Is.EqualTo(new[] { "a.first", "b.second" }));
        }

        [Test]
        public void MoveBefore_ChangesTheLoadOrderForGood()
        {
            RempackFixture.Tank("a.one").Write(_root);
            RempackFixture.Tank("b.two").Write(_root);
            RempackFixture.Tank("c.three").Write(_root);
            var library = Library();
            library.MoveBefore("c.three", "a.one");

            Assert.That(Ids(library.Active), Is.EqualTo(new[] { "c.three", "a.one", "b.two" }));
            library.MoveBefore("c.three", null);
            library.MoveBefore("a.one", "c.three");
            Assert.That(Ids(Library().Active), Is.EqualTo(new[] { "b.two", "a.one", "c.three" }));
        }

        [Test]
        public void Install_PutsANewPackageInPlace()
        {
            var source = RempackFixture.Tank().Write(_downloads, "Test Mod (1).rempack");
            var library = Library();
            var plan = library.Prepare(source);

            Assert.That(plan.CanInstall, Is.True, plan.Report.ToString());
            Assert.That(plan.Kind, Is.EqualTo(RempackInstallKind.New));
            Assert.That(plan.Existing, Is.Null);
            Assert.That(plan.SourceName, Is.EqualTo("Test Mod (1).rempack"));
            Assert.That(plan.Manifest.Id, Is.EqualTo("author.test_mod"));
            Assert.That(Path.GetDirectoryName(plan.StagedPath), Is.EqualTo(library.IncomingPath));
            Assert.That(File.Exists(plan.StagedPath), Is.True);
            Assert.That(library.Mods, Is.Empty, "nothing is installed before the player confirms");

            var staged = plan.StagedPath;
            var mod = library.Install(plan);
            Assert.That(mod, Is.Not.Null, plan.Report.ToString());
            Assert.That(mod.IsActive, Is.True);
            Assert.That(mod.FilePath, Is.EqualTo(library.PathFor("author.test_mod")));
            Assert.That(File.Exists(staged), Is.False);
            Assert.That(plan.StagedPath, Is.Null);
            Assert.That(File.Exists(source), Is.True, "the picked file is copied, not moved");
            Assert.That(File.ReadAllBytes(mod.FilePath), Is.EqualTo(File.ReadAllBytes(source)));
        }

        [Test]
        public void Prepare_SaysWhatAnInstallReplaces()
        {
            var library = Library();
            library.Install(library.Prepare(RempackFixture.Tank(version: "1.1.0").Write(_downloads)));

            var update = library.Prepare(RempackFixture.Tank(version: "1.2.0").Write(_downloads, "update.rempack"));
            Assert.That(update.Kind, Is.EqualTo(RempackInstallKind.Update));
            Assert.That(update.Existing.Version.ToString(), Is.EqualTo("1.1.0"));
            Assert.That(update.Report.Issues.Single(i => i.Code == RempackCodes.InstallUpdate).Args, Is.EqualTo(new[] { "1.1.0", "1.2.0" }));
            library.Discard(update);

            var same = library.Prepare(RempackFixture.Tank(version: "1.1.0").Write(_downloads, "same.rempack"));
            Assert.That(same.Kind, Is.EqualTo(RempackInstallKind.Reinstall));
            Assert.That(same.Report.Has(RempackCodes.InstallReinstall), Is.True);
            library.Discard(same);

            var older = library.Prepare(RempackFixture.Tank(version: "1.0.0").Write(_downloads, "older.rempack"));
            Assert.That(older.Kind, Is.EqualTo(RempackInstallKind.Downgrade));
            Assert.That(older.Report.Issues.Single(i => i.Code == RempackCodes.InstallDowngrade).Severity, Is.EqualTo(RempackSeverity.Warning));
            Assert.That(older.CanInstall, Is.True, "a downgrade is allowed after a warning");
            Assert.That(library.Install(older).Version.ToString(), Is.EqualTo("1.0.0"));
        }

        [Test]
        public void Install_AnUpdateKeepsTheSwitchAndThePlace()
        {
            RempackFixture.Tank("a.one").Write(_root);
            RempackFixture.Tank("b.two").Write(_root);
            var library = Library();
            library.SetEnabled("a.one", false);
            library.MoveBefore("b.two", "a.one");

            var mod = library.Install(library.Prepare(RempackFixture.Tank("a.one", "2.0.0").Write(_downloads)));

            Assert.That(mod.Version.ToString(), Is.EqualTo("2.0.0"));
            Assert.That(mod.Enabled, Is.False);
            Assert.That(Ids(library.Mods), Is.EqualTo(new[] { "b.two", "a.one" }));
        }

        [Test]
        public void Install_ReplacesEveryOtherCopyOfTheId()
        {
            RempackFixture.Tank().Write(_root);
            RempackFixture.Tank().Write(_root, "author.test_mod (copy).rempack");
            var library = Library();
            Assert.That(library.Mods.Count, Is.EqualTo(2));

            library.Install(library.Prepare(RempackFixture.Tank(version: "1.5.0").Write(_downloads)));

            Assert.That(Directory.GetFiles(_root, "*.rempack").Select(Path.GetFileName), Is.EqualTo(new[] { "author.test_mod.rempack" }));
            Assert.That(library.Mods.Single().Version.ToString(), Is.EqualTo("1.5.0"));
        }

        [Test]
        public void Prepare_RefusesABrokenFileAndKeepsNoCopy()
        {
            var junk = Path.Combine(_downloads, "junk.rempack");
            File.WriteAllBytes(junk, new byte[] { 0x50, 0x4B, 3, 4, 0, 0 });
            var library = Library();
            var plan = library.Prepare(junk);

            Assert.That(plan.CanInstall, Is.False);
            Assert.That(plan.Report.HasErrors, Is.True);
            Assert.That(plan.StagedPath, Is.Null);
            Assert.That(Directory.GetFiles(library.IncomingPath), Is.Empty);
            Assert.Throws<InvalidOperationException>(() => library.Install(plan));
        }

        [Test]
        public void Prepare_RefusesAPackageWithErrorsButNamesIt()
        {
            var source = Path.Combine(_downloads, "tampered.rempack");
            File.WriteAllBytes(source, RempackFixture.Tank().Tampered(files => files.Set("tanks/t1/tank.json", new string('1', 64))));
            var plan = Library().Prepare(source);

            Assert.That(plan.CanInstall, Is.False);
            Assert.That(plan.Manifest.Id, Is.EqualTo("author.test_mod"));
            Assert.That(plan.Report.Has(RempackCodes.FileHashMismatch), Is.True);
        }

        [Test]
        public void Prepare_ReportsASourceItCannotRead()
        {
            var plan = Library().Prepare(Path.Combine(_downloads, "gone.rempack"));

            Assert.That(plan.CanInstall, Is.False);
            Assert.That(plan.Report.FirstError.Code, Is.EqualTo(RempackCodes.FileUnreadable));
            Assert.That(plan.SourceName, Is.EqualTo("gone.rempack"));
        }

        [Test]
        public void Prepare_ReadsAStream()
        {
            var library = Library();
            var plan = library.Prepare(new MemoryStream(RempackFixture.Tank().Build()), "content.rempack");

            Assert.That(plan.CanInstall, Is.True, plan.Report.ToString());
            Assert.That(plan.Size, Is.EqualTo(RempackFixture.Tank().Build().Length));
            Assert.That(library.Install(plan).IsActive, Is.True);
        }

        [Test]
        public void Prepare_RefusesAnArchiveOverTheLimitWithoutCopyingIt()
        {
            var plan = Library().Prepare(new HugeStream(RempackLimits.ArchiveBytes + 1), "huge.rempack");

            Assert.That(plan.Report.FirstError.Code, Is.EqualTo(RempackCodes.ArchiveTooLarge));
            Assert.That(plan.StagedPath, Is.Null);
        }

        [Test]
        public void Prepare_ReportsAStreamThatFails()
        {
            var plan = Library().Prepare(new FailingStream(), "revoked.rempack");

            Assert.That(plan.Report.FirstError.Code, Is.EqualTo(RempackCodes.InstallCopyFailed));
            Assert.That(plan.Report.FirstError.Detail, Is.EqualTo("revoked"));
            Assert.That(plan.StagedPath, Is.Null);
        }

        [Test]
        public void Discard_DeletesTheCheckedCopy()
        {
            var library = Library();
            var plan = library.Prepare(RempackFixture.Tank().Write(_downloads));
            var staged = plan.StagedPath;
            library.Discard(plan);

            Assert.That(File.Exists(staged), Is.False);
            Assert.That(plan.StagedPath, Is.Null);
            Assert.That(plan.CanInstall, Is.False);
            library.Discard(plan);
        }

        [Test]
        public void Refresh_ClearsLeftoverCopiesButNotAWaitingPlan()
        {
            var library = Library();
            var plan = library.Prepare(RempackFixture.Tank().Write(_downloads));
            var stray = Path.Combine(library.IncomingPath, "0123456789abcdef.rempack");
            File.WriteAllBytes(stray, new byte[] { 1 });
            library.Refresh();

            Assert.That(File.Exists(stray), Is.False);
            Assert.That(File.Exists(plan.StagedPath), Is.True);
            Assert.That(library.Install(plan), Is.Not.Null);
        }

        [Test]
        public void Refresh_PutsBackAVersionAnInterruptedInstallMovedAside()
        {
            var incoming = Path.Combine(_root, RempackLibrary.IncomingFolder);
            Directory.CreateDirectory(incoming);
            File.WriteAllBytes(Path.Combine(incoming, "author.test_mod.rempack.old"), RempackFixture.Tank().Build());
            File.WriteAllBytes(Path.Combine(incoming, "other.mod.rempack.old"), RempackFixture.Tank("other.mod", "1.0.0").Build());
            RempackFixture.Tank("other.mod", "2.0.0").Write(_root);
            var library = Library();

            Assert.That(library.Find("author.test_mod").IsActive, Is.True, "the old version is back");
            Assert.That(library.Find("other.mod").Version.ToString(), Is.EqualTo("2.0.0"), "a finished install keeps the new file");
            Assert.That(Directory.GetFiles(incoming), Is.Empty);
        }

        [Test]
        public void Refresh_UsesOneCopyPerId()
        {
            RempackFixture.Tank(version: "1.0.0").Write(_root);
            RempackFixture.Tank(version: "1.2.0").Write(_root, "newer.rempack");
            RempackFixture.Tank("x.same").Write(_root, "b-copy.rempack");
            RempackFixture.Tank("x.same").Write(_root, "a-copy.rempack");
            RempackFixture.Tank("x.same").Write(_root);
            var library = Library();

            var higher = library.Find("author.test_mod");
            Assert.That(higher.FileName, Is.EqualTo("newer.rempack"), "the higher version wins");
            Assert.That(higher.IsActive, Is.True);
            var loser = library.Mods.Single(m => m.FileName == "author.test_mod.rempack");
            Assert.That(loser.Status, Is.EqualTo(RempackModStatus.Blocked));
            Assert.That(loser.Reason.Code, Is.EqualTo(RempackCodes.Duplicate));
            Assert.That(loser.Reason.Args, Is.EqualTo(new[] { "newer.rempack" }));

            Assert.That(library.Find("x.same").FileName, Is.EqualTo("x.same.rempack"), "then the canonical name");
            Assert.That(library.Mods.Where(m => m.Id == "x.same" && !m.Usable).Select(m => m.FileName), Is.EquivalentTo(new[] { "a-copy.rempack", "b-copy.rempack" }));
            Assert.That(library.Active.Count, Is.EqualTo(2));
        }

        [Test]
        public void Refresh_PrefersTheFirstNameAmongEqualCopies()
        {
            RempackFixture.Tank().Write(_root, "b.rempack");
            RempackFixture.Tank().Write(_root, "a.rempack");

            Assert.That(Library().Find("author.test_mod").FileName, Is.EqualTo("a.rempack"));
        }

        [Test]
        public void Remove_DeletesEveryCopyAndForgetsTheId()
        {
            RempackFixture.Tank().Write(_root);
            RempackFixture.Tank().Write(_root, "copy.rempack");
            RempackFixture.Tank("b.keep").Write(_root);
            var library = Library();

            Assert.That(library.Remove(library.Find("author.test_mod")), Is.True);
            Assert.That(Ids(library.Mods), Is.EqualTo(new[] { "b.keep" }));
            Assert.That(Directory.GetFiles(_root, "*.rempack").Length, Is.EqualTo(1));
            Assert.That(ModState.Load(_root).IsKnown("author.test_mod"), Is.False);
            Assert.That(library.Remove(null), Is.False);
        }

        [Test]
        public void Remove_DeletesAFileWithoutAnId()
        {
            File.WriteAllBytes(Path.Combine(_root, "junk.rempack"), new byte[] { 9 });
            var library = Library();

            Assert.That(library.Remove(library.Mods[0]), Is.True);
            Assert.That(library.Mods, Is.Empty);
        }

        [Test]
        public void Refresh_TrustsAFileItVerifiedUntilItChanges()
        {
            var random = new Random(3);
            var sound = new byte[300];
            random.NextBytes(sound);
            var path = RempackFixture.Tank().Bytes("tanks/t1/engine.ogg", sound).Write(_root);
            Assert.That(Library().Find("author.test_mod").IsActive, Is.True);
            Assert.That(File.Exists(Path.Combine(_root, ".cache", "verified.json")), Is.True);

            // Flip a byte of the stored sound and put the old size and time back: the
            // library does not hash a file it already checked, which is the point.
            var written = File.GetLastWriteTimeUtc(path);
            var bytes = File.ReadAllBytes(path);
            var at = IndexOf(bytes, sound) + 100;
            bytes[at] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, written);
            Assert.That(Library().Find("author.test_mod").IsActive, Is.True);

            File.SetLastWriteTimeUtc(path, written.AddMinutes(1));
            var changed = Library().Find("author.test_mod");
            Assert.That(changed.Status, Is.EqualTo(RempackModStatus.Invalid), changed.Report.ToString());
        }

        [Test]
        public void Install_RemembersThePackageAsVerified()
        {
            var random = new Random(4);
            var sound = new byte[300];
            random.NextBytes(sound);
            var library = Library();
            var mod = library.Install(library.Prepare(RempackFixture.Tank().Bytes("tanks/t1/engine.ogg", sound).Write(_downloads)));

            var written = File.GetLastWriteTimeUtc(mod.FilePath);
            var bytes = File.ReadAllBytes(mod.FilePath);
            bytes[IndexOf(bytes, sound) + 10] ^= 0xFF;
            File.WriteAllBytes(mod.FilePath, bytes);
            File.SetLastWriteTimeUtc(mod.FilePath, written);

            Assert.That(Library().Find("author.test_mod").IsActive, Is.True, "hashed once, as the checked copy");
        }

        [Test]
        public void Refresh_ShowsWhichPackagesACrashSwitchedOff()
        {
            RempackFixture.Tank("a.crashy").Write(_root);
            RempackFixture.Tank("b.fine").Write(_root);
            Library();
            ModState.Update(_root, delegate(ModState state) { state.MarkCrashed("a.crashy"); });

            var library = Library();
            var crashed = library.Find("a.crashy");
            Assert.That(crashed.Status, Is.EqualTo(RempackModStatus.Disabled));
            Assert.That(crashed.Crashed, Is.True);
            Assert.That(crashed.Reason.Code, Is.EqualTo(RempackCodes.SafeModeCrashed));
            Assert.That(library.Crashed, Is.EqualTo(new[] { "a.crashy" }));
            Assert.That(Ids(library.Active), Is.EqualTo(new[] { "b.fine" }));

            library.AcknowledgeCrashes();
            Assert.That(library.Crashed, Is.Empty);
            Assert.That(Library().Find("a.crashy").Crashed, Is.False);
            Assert.That(Library().Find("a.crashy").Enabled, Is.False, "it stays off until switched on");
        }

        [Test]
        public void SetEnabled_ClearsTheCrashMark()
        {
            RempackFixture.Tank().Write(_root);
            ModState.Update(_root, s => s.MarkCrashed("author.test_mod"));
            var library = Library();
            library.SetEnabled("author.test_mod", true);

            Assert.That(library.Find("author.test_mod").IsActive, Is.True);
            Assert.That(library.Crashed, Is.Empty);
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length && match; j++)
                {
                    match = haystack[i + j] == needle[j];
                }

                if (match)
                {
                    return i;
                }
            }

            throw new AssertionException("the stored file is not in the archive");
        }

        /// <summary>Claims a length it never delivers, like a huge file behind a provider.</summary>
        private sealed class HugeStream : Stream
        {
            private readonly long _length;

            public HugeStream(long length)
            {
                _length = length;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return _length; } }
            public override long Position { get; set; }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { throw new AssertionException("read a stream that is too large"); }
            public override long Seek(long offset, SeekOrigin origin) { return Position = offset; }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        /// <summary>A document whose permission goes away mid-copy.</summary>
        private sealed class FailingStream : Stream
        {
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { throw new InvalidOperationException("revoked"); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
