using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Mods;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Mods;
using BlitzLegacy.Runtime.Tanks;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class PortableModTests
    {
        private string _root;
        private GameObject _host;
        private ModLibrary _oldLibrary;
        private FieldInfo _library;
        private bool _swapped;
        [SetUp] public void SetUp() { _root = Path.Combine(Path.GetTempPath(), "blitz_portable_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
        [TearDown] public void TearDown()
        {
            if (_host != null) { UnityEngine.Object.DestroyImmediate(_host); }
            if (_swapped) { TankAssetSource.Forget(); ModRuntime.Forget(); _library.SetValue(null, _oldLibrary); }
            if (Directory.Exists(_root)) { Directory.Delete(_root, true); }
        }
        [TestCase("../bad")][TestCase("/bad")][TestCase("a/../bad")][TestCase("C:/bad")][TestCase("a\\bad")]
        public void PathsRejectTraversal(string path) { Assert.Throws<ModArchiveException>(() => PortableModPackage.Relative(path)); }
        [Test] public void RawSdkPackageExplainsConversion()
        {
            var path = Path.Combine(_root, "source.modpack"); File.WriteAllText(path, "not executed");
            var result = new ModLibrary(Path.Combine(_root, "mods")).Install(path);
            Assert.That(result.ErrorKey, Is.EqualTo("mods_error_conversion_required"));
        }
        [Test] public void PortableExtensionIsDiscoverable() { Assert.That(ModArchive.LooksLikeArchive("tank.blitzmod"), Is.True); }
        [Test] public void MaliciousZipDoesNotWriteOutsideStaging()
        {
            var path = Path.Combine(_root, "bad.blitzmod");
            using (var stream = File.Create(path))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("../escape").Open())) { writer.Write("bad"); }
            var result = new ModLibrary(Path.Combine(_root, "mods")).Install(path);
            Assert.That(result.Ok, Is.False); Assert.That(File.Exists(Path.Combine(_root, "mods", "escape")), Is.False);
        }
        [Test] public void RenamedSdkZipStillExplainsConversion()
        {
            var path = Path.Combine(_root, "source.zip");
            using (var stream = File.Create(path))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("package.json").Open())) { writer.Write("{}"); }
            var result = new ModLibrary(Path.Combine(_root, "mods")).Install(path);
            Assert.That(result.ErrorKey, Is.EqualTo("mods_error_conversion_required"));
        }
        [Test] public void RenamedPortableZipUsesBoundedPortableValidation()
        {
            var path = Path.Combine(_root, "converted.zip"); File.Copy(Fixture(), path);
            var result = new ModLibrary(Path.Combine(_root, "mods")).Install(path);
            Assert.That(result.Ok, Is.True, result.ErrorKey); Assert.That(result.Mod.Portable, Is.True);
        }
        private string Fixture()
        {
            var path = Path.GetFullPath("build/mods/Argonaut.blitzmod");
            if (!File.Exists(path)) { Assert.Ignore("Generate the local delivered modpack fixture with Tools/modpack/convert.py."); }
            return path;
        }
        [Test] public void ActualConvertedArchiveInstallsRestartsDisablesAndRemoves()
        {
            var library = new ModLibrary(Path.Combine(_root, "mods")); var result = library.Install(Fixture());
            Assert.That(result.Ok, Is.True, result.ErrorKey); var id = result.Mod.Id;
            Assert.That(result.Mod.Portable, Is.True); var card = library.Cards(null)[0];
            Assert.That(card.Mod, Is.True); Assert.That(card.TotalHealth, Is.EqualTo(1650));
            Assert.That(card.Guns[0].Reload, Is.EqualTo(8.6f).Within(.001f));
            Assert.That(ModTankOnlineBan.Violates(card.Name), Is.True);
            library.SetEnabled(id, false); library = new ModLibrary(library.RootPath); library.Refresh();
            Assert.That(library.Cards(null), Is.Empty); library.SetEnabled(id, true);
            Assert.That(library.Cards(null).Count, Is.EqualTo(1));
            var reinstall = library.Install(Fixture()); Assert.That(reinstall.Ok, Is.True, reinstall.ErrorKey);
            library.Remove(id); Assert.That(library.Mods, Is.Empty);
        }
        [Test] public void CorruptUpgradePreservesPreviouslyInstalledMod()
        {
            var library = new ModLibrary(Path.Combine(_root, "mods")); Assert.That(library.Install(Fixture()).Ok, Is.True);
            var corrupt = Path.Combine(_root, "bad.blitzmod"); File.Copy(Fixture(), corrupt);
            using (var stream = File.Open(corrupt, FileMode.Open))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("manifest.json"); entry.Delete();
                using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) { writer.Write("{}"); }
            }
            Assert.That(library.Install(corrupt).Ok, Is.False); library.Refresh(); Assert.That(library.Cards(null).Count, Is.EqualTo(1));
        }
        [Test] public void RestartRecoversInterruptedPromotion()
        {
            var library = new ModLibrary(Path.Combine(_root, "mods")); var result = library.Install(Fixture());
            Assert.That(result.Ok, Is.True, result.ErrorKey);
            var id = result.Mod.Id;
            Directory.Move(result.Mod.FolderPath, Path.Combine(library.RootPath, ".backup_" + id));
            library = new ModLibrary(library.RootPath); library.Refresh();
            Assert.That(library.Find(id), Is.Not.Null); Assert.That(library.Cards(null).Count, Is.EqualTo(1));
        }
        [Test] public void DowngradeIsBlockedWithoutRemovingNewerInstall()
        {
            var library = new ModLibrary(Path.Combine(_root, "mods")); var result = library.Install(Fixture());
            Assert.That(result.Ok, Is.True, result.ErrorKey);
            var path = Path.Combine(result.Mod.FolderPath, "manifest.json");
            var text = File.ReadAllText(path); File.WriteAllText(path, text.Replace("\"version\":2", "\"version\":3"));
            var older = library.Install(Fixture());
            Assert.That(older.Ok, Is.False); Assert.That(older.ErrorKey, Is.EqualTo("mods_error_portable_downgrade"));
            library.Refresh(); Assert.That(library.Find(result.Mod.Id).PackageVersion, Is.EqualTo(3));
        }
        [Test] public void PayloadHashTamperingIsRejected()
        {
            var library = new ModLibrary(Path.Combine(_root, "mods")); var result = library.Install(Fixture());
            Assert.That(result.Ok, Is.True, result.ErrorKey);
            var tank = result.Mod.Tanks[0]; File.AppendAllText(tank.ArmorPath, " ");
            library.Refresh(); Assert.That(library.Mods, Is.Empty);
        }
        [Test] public void ConvertedModelBuildsAndSpawnsWithNativeArmorAndGunRig()
        {
            var library = new ModLibrary(Path.Combine(_root, "mods")); var result = library.Install(Fixture());
            Assert.That(result.Ok, Is.True, result.ErrorKey);
            _library = typeof(ModRuntime).GetField("_library", BindingFlags.Static | BindingFlags.NonPublic);
            _oldLibrary = (ModLibrary)_library.GetValue(null); _library.SetValue(null, library); _swapped = true;
            var card = library.Cards(null)[0];
            var model = TankAssetSource.Model(TankModelAssets.For(card));
            Assert.That(model, Is.Not.Null); Assert.That(model.GetComponentsInChildren<MeshFilter>().Length, Is.EqualTo(28));
            _host = new GameObject("PortableModFixture");
            var tank = BattleTank.Spawn(_host.transform, "Converted", card, null, new Bounds(Vector3.zero, Vector3.one * 1000));
            Assert.That(tank.Health.MaxHealth, Is.EqualTo(1650));
            Assert.That(tank.Plates.Count, Is.GreaterThanOrEqualTo(19));
            Assert.That(tank.Turret.GunPivot, Is.Not.Null);
            Assert.That(tank.Turret.GunPivot.GetComponentsInChildren<MeshFilter>().Length, Is.GreaterThan(0));
            Assert.That(tank.Turret.Muzzle.localPosition.z, Is.GreaterThan(0));
            Physics.SyncTransforms();
            foreach (var plate in tank.Plates)
            {
                Assert.That(plate.Shape, Is.Not.Null);
                var mesh = plate.Shape.sharedMesh; var tri = mesh.triangles; var vertices = mesh.vertices;
                var a = plate.Shape.transform.TransformPoint(vertices[tri[0]]);
                var b = plate.Shape.transform.TransformPoint(vertices[tri[1]]);
                var c = plate.Shape.transform.TransformPoint(vertices[tri[2]]);
                var normal = Vector3.Cross(b - a, c - a).normalized;
                if (normal.sqrMagnitude < .1f) { continue; }
                var centre = (a + b + c) / 3f;
                RaycastHit hit;
                Assert.That(plate.Shape.Raycast(new Ray(centre + normal * .2f, -normal), out hit, .4f), Is.True, plate.PlateName);
            }
        }
    }
}
