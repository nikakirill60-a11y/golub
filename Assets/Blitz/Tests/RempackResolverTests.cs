using System;
using System.IO;
using System.Linq;
using BlitzLegacy.Core.Mods.Rempack;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>Dependencies, conflicts and stock replacements, decided over real package files.</summary>
    public sealed class RempackResolverTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "rempack_resolver_" + Guid.NewGuid().ToString("N"));
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

        private RempackLibrary Library()
        {
            var library = new RempackLibrary(_root, RempackFixture.Options());
            library.Refresh();
            return library;
        }

        private static string[] Active(RempackLibrary library)
        {
            return library.Active.Select(m => m.Id).ToArray();
        }

        private static void AssertBlocked(RempackLibrary library, string id, string code, params string[] args)
        {
            var mod = library.Find(id);
            Assert.That(mod.Status, Is.EqualTo(RempackModStatus.Blocked), id);
            Assert.That(mod.Reason.Code, Is.EqualTo(code), id);
            Assert.That(mod.Reason.Args, Is.EqualTo(args), id);
            Assert.That(library.Resolution.BlockedBy(mod), Is.SameAs(mod.Reason));
        }

        [Test]
        public void Dependency_ThatIsNotInstalled_KeepsThePackageOff()
        {
            RempackFixture.Tank("a.needs").Depends("z.base", "^1.0").Write(_root);
            var library = Library();

            AssertBlocked(library, "a.needs", RempackCodes.DependencyMissing, "z.base", "^1.0");
            Assert.That(library.Active, Is.Empty);
        }

        [Test]
        public void Dependency_InTheWrongVersion_KeepsThePackageOff()
        {
            RempackFixture.Tank("z.base", "1.4.0").Write(_root);
            RempackFixture.Tank("a.needs").Depends("z.base", ">=2.0.0").Write(_root);
            var library = Library();

            AssertBlocked(library, "a.needs", RempackCodes.DependencyVersion, "z.base", ">=2.0.0", "1.4.0");
            Assert.That(Active(library), Is.EqualTo(new[] { "z.base" }));
        }

        [Test]
        public void Dependency_ThatIsSwitchedOff_KeepsThePackageOff()
        {
            RempackFixture.Tank("z.base").Write(_root);
            RempackFixture.Tank("a.needs").Depends("z.base", "*").Write(_root);
            var library = Library();
            library.SetEnabled("z.base", false);

            AssertBlocked(library, "a.needs", RempackCodes.DependencyInactive, "z.base");
            Assert.That(library.Find("z.base").Status, Is.EqualTo(RempackModStatus.Disabled));
        }

        [Test]
        public void Dependency_ThatIsInvalid_CountsAsMissing()
        {
            File.WriteAllBytes(Path.Combine(_root, "z.base.rempack"),
                RempackFixture.Tank("z.base").Tampered(files => files.Set("tanks/t1/tank.json", new string('2', 64))));
            RempackFixture.Tank("a.needs").Depends("z.base", "*").Write(_root);
            var library = Library();

            AssertBlocked(library, "a.needs", RempackCodes.DependencyMissing, "z.base", "*");
        }

        [Test]
        public void Dependency_ThatCannotLoad_TakesItsDependentsAlong()
        {
            RempackFixture.Tank("b.middle").Depends("z.base", "*").Write(_root);
            RempackFixture.Tank("c.top").Depends("b.middle", "*").Write(_root);
            var library = Library();

            AssertBlocked(library, "b.middle", RempackCodes.DependencyMissing, "z.base", "*");
            AssertBlocked(library, "c.top", RempackCodes.DependencyInactive, "b.middle");
        }

        [Test]
        public void Dependency_LaterInTheOrder_StillLoads()
        {
            RempackFixture.Tank("a.needs").Depends("z.base", "1.x").Write(_root);
            RempackFixture.Tank("z.base", "1.9.0").Write(_root);
            var library = Library();

            Assert.That(Active(library), Is.EqualTo(new[] { "a.needs", "z.base" }), "active packages keep the load order");
        }

        [Test]
        public void Dependency_Cycle_LoadsWhenNothingElseStopsIt()
        {
            RempackFixture.Tank("a.one").Depends("b.two", "*").Write(_root);
            RempackFixture.Tank("b.two").Depends("a.one", "*").Write(_root);
            var library = Library();

            Assert.That(Active(library), Is.EqualTo(new[] { "a.one", "b.two" }));
        }

        [Test]
        public void Dependency_Cycle_FallsTogetherWhenOneMemberLoses()
        {
            RempackFixture.Tank("a.rival").Conflicts("b.one", "*").Write(_root);
            RempackFixture.Tank("b.one").Depends("c.two", "*").Write(_root);
            RempackFixture.Tank("c.two").Depends("b.one", "*").Write(_root);
            var library = Library();

            Assert.That(Active(library), Is.EqualTo(new[] { "a.rival" }));
            AssertBlocked(library, "b.one", RempackCodes.ConflictPresent, "a.rival");
            AssertBlocked(library, "c.two", RempackCodes.DependencyInactive, "b.one");
        }

        [Test]
        public void Conflict_TheEarlierPackageWins_WhicheverSideDeclaresIt()
        {
            RempackFixture.Tank("a.first").Write(_root);
            RempackFixture.Tank("b.second").Conflicts("a.first", "*").Write(_root);
            RempackFixture.Tank("c.third").Write(_root);
            RempackFixture.Tank("d.fourth").Write(_root);
            var library = Library();
            AssertBlocked(library, "b.second", RempackCodes.ConflictPresent, "a.first");

            library.MoveBefore("b.second", "a.first");
            AssertBlocked(library, "a.first", RempackCodes.ConflictPresent, "b.second");
            Assert.That(Active(library), Is.EqualTo(new[] { "b.second", "c.third", "d.fourth" }));
        }

        [Test]
        public void Conflict_OnlyCoversTheVersionsItNames()
        {
            RempackFixture.Tank("a.first", "2.1.0").Write(_root);
            RempackFixture.Tank("b.second").Conflicts("a.first", "<2.0.0").Write(_root);
            var library = Library();

            Assert.That(Active(library), Is.EqualTo(new[] { "a.first", "b.second" }));
        }

        [Test]
        public void Conflict_WithASwitchedOffPackage_DoesNotCount()
        {
            RempackFixture.Tank("a.first").Write(_root);
            RempackFixture.Tank("b.second").Conflicts("a.first", "*").Write(_root);
            var library = Library();
            library.SetEnabled("a.first", false);

            Assert.That(Active(library), Is.EqualTo(new[] { "b.second" }));
        }

        [Test]
        public void Conflict_LostByADependency_KeepsTheDependentOff()
        {
            RempackFixture.Tank("a.rival").Conflicts("b.base", "*").Write(_root);
            RempackFixture.Tank("b.base").Write(_root);
            RempackFixture.Tank("c.needs").Depends("b.base", "*").Write(_root);
            var library = Library();

            AssertBlocked(library, "b.base", RempackCodes.ConflictPresent, "a.rival");
            AssertBlocked(library, "c.needs", RempackCodes.DependencyInactive, "b.base");
        }

        [Test]
        public void Override_ClaimedTwice_GoesToTheLaterPackage()
        {
            RempackFixture.Tank("a.first").Overrides("stock:tank/t-34", "stock:map/rudniki").Write(_root);
            RempackFixture.Tank("b.second").Overrides("stock:tank/t-34").Write(_root);
            var library = Library();
            RempackStockRef tank, map;
            RempackStockRef.TryParse("stock:tank/t-34", out tank);
            RempackStockRef.TryParse("stock:map/rudniki", out map);

            Assert.That(library.Resolution.Overrides[tank].Id, Is.EqualTo("b.second"));
            Assert.That(library.Resolution.Overrides[map].Id, Is.EqualTo("a.first"));
            var note = library.Find("a.first").Notes.Single();
            Assert.That(note.Code, Is.EqualTo(RempackCodes.OverrideShadowed));
            Assert.That(note.Severity, Is.EqualTo(RempackSeverity.Warning));
            Assert.That(note.Args, Is.EqualTo(new[] { "stock:tank/t-34", "b.second" }));
            Assert.That(library.Find("b.second").Notes, Is.Empty);

            library.MoveBefore("b.second", "a.first");
            Assert.That(library.Resolution.Overrides[tank].Id, Is.EqualTo("a.first"));
            Assert.That(library.Find("b.second").Notes.Single().Code, Is.EqualTo(RempackCodes.OverrideShadowed));
        }

        [Test]
        public void Override_OfAPackageThatDoesNotLoad_IsNotUsed()
        {
            RempackFixture.Tank("a.first").Overrides("stock:tank/t-34").Write(_root);
            var library = Library();
            library.SetEnabled("a.first", false);

            Assert.That(library.Resolution.Overrides, Is.Empty);
        }

        [Test]
        public void Prepare_WarnsWhatTheNewPackageWillNotGet()
        {
            var library = Library();
            var plan = library.Prepare(new MemoryStream(RempackFixture.Tank("a.needs").Depends("z.base", "^1.0").Build()), "a.rempack");
            var warning = plan.Report.Issues.Single(i => i.Code == RempackCodes.DependencyMissing);

            Assert.That(warning.Severity, Is.EqualTo(RempackSeverity.Warning));
            Assert.That(warning.Args, Is.EqualTo(new[] { "z.base", "^1.0" }));
            Assert.That(plan.CanInstall, Is.True, "a missing dependency can be installed later");
            library.Discard(plan);
        }

        [Test]
        public void Prepare_WarnsWhichPackagesAnUpdateWouldBreak()
        {
            RempackFixture.Tank("z.base", "1.2.0").Write(_root);
            RempackFixture.Tank("a.needs").Depends("z.base", "^1.0").Write(_root);
            RempackFixture.Tank("b.other").Write(_root);
            var library = Library();
            var plan = library.Prepare(new MemoryStream(RempackFixture.Tank("z.base", "2.0.0").Build()), "z.base.rempack");

            var broken = plan.Report.Issues.Where(i => i.Code == RempackCodes.InstallBreaks).ToArray();
            Assert.That(broken.Length, Is.EqualTo(1), plan.Report.ToString());
            Assert.That(broken[0].Args, Is.EqualTo(new[] { "a.needs" }));
            Assert.That(plan.Kind, Is.EqualTo(RempackInstallKind.Update));

            library.Install(plan);
            AssertBlocked(library, "a.needs", RempackCodes.DependencyVersion, "z.base", "^1.0", "2.0.0");
        }

        [Test]
        public void Prepare_WarnsAboutAConflictThatKeepsTheNewPackageOff()
        {
            RempackFixture.Tank("a.first").Write(_root);
            var library = Library();
            var plan = library.Prepare(new MemoryStream(RempackFixture.Tank("b.second").Conflicts("a.first", "*").Build()), "b.rempack");

            Assert.That(plan.Report.Issues.Single(i => i.Code == RempackCodes.ConflictPresent).Args, Is.EqualTo(new[] { "a.first" }));
            Assert.That(plan.Report.Has(RempackCodes.InstallBreaks), Is.False, "the new package loses, the old one stays");
        }
    }
}
