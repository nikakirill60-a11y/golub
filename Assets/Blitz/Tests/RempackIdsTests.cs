using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>Ids, stock references, texts, package paths, features, image headers and packing.</summary>
    public sealed class RempackIdsTests
    {
        [TestCase("miku.void_pack", true)]
        [TestCase("a.b", true)]
        [TestCase("author2.mod_2_x", true)]
        [TestCase("miku", false)]
        [TestCase("miku.void.pack", false)]
        [TestCase(".pack", false)]
        [TestCase("miku.", false)]
        [TestCase("Miku.pack", false)]
        [TestCase("miku.void-pack", false)]
        [TestCase("miku._pack", false)]
        [TestCase("miku.pack_", false)]
        [TestCase("miku.void__pack", false)]
        [TestCase("m\u00efku.pack", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void IsModId_AcceptsAuthorDotName(string id, bool valid)
        {
            Assert.That(RempackIds.IsModId(id), Is.EqualTo(valid));
        }

        [Test]
        public void IsModId_LimitsTheLengthOfEachPart()
        {
            Assert.That(RempackIds.IsModId(new string('a', 32) + "." + new string('b', 31)), Is.True);
            Assert.That(RempackIds.IsModId(new string('a', 33) + ".b"), Is.False);
            Assert.That(RempackIds.IsModId("a." + new string('b', 49)), Is.False);
            Assert.That(RempackIds.IsLocalId(new string('t', RempackIds.LocalIdLength)), Is.True);
            Assert.That(RempackIds.IsLocalId(new string('t', RempackIds.LocalIdLength + 1)), Is.False);
            Assert.That(RempackIds.IsLocalId("t34.85"), Is.False);
        }

        [Test]
        public void InternalName_RoundTripsAndCannotCollide()
        {
            var name = RempackIds.InternalName("miku.void_pack", "t34_void");
            string mod, local;

            Assert.That(name, Is.EqualTo("rp_miku__void_pack__t34_void"));
            Assert.That(RempackIds.TryParseInternalName(name, out mod, out local), Is.True);
            Assert.That(mod, Is.EqualTo("miku.void_pack"));
            Assert.That(local, Is.EqualTo("t34_void"));
            Assert.That(RempackIds.InternalName("a_b.c", "d"), Is.Not.EqualTo(RempackIds.InternalName("a.b_c", "d")));
            Assert.That(RempackIds.IsInternalName("t-34-85"), Is.False, "stock names never look like mod content");
            Assert.That(RempackIds.IsInternalName("rp_a__b"), Is.False);
            Assert.That(RempackIds.IsInternalName("rp_a__b__c__d"), Is.False);
            Assert.Throws<ArgumentException>(() => RempackIds.InternalName("bad", "t1"));
            Assert.Throws<ArgumentException>(() => RempackIds.InternalName("a.b", "T1"));
        }

        [TestCase("stock:tank/t-34-85", RempackContentKind.Tank, "t-34-85")]
        [TestCase("stock:hangar/default", RempackContentKind.Hangar, "default")]
        [TestCase("stock:map/rudniki_v2.1", RempackContentKind.Map, "rudniki_v2.1")]
        public void StockRef_ParsesTheThreeKinds(string text, RempackContentKind kind, string id)
        {
            RempackStockRef reference;
            Assert.That(RempackStockRef.TryParse(text, out reference), Is.True);
            Assert.That(reference.Kind, Is.EqualTo(kind));
            Assert.That(reference.Id, Is.EqualTo(id));
            Assert.That(reference.ToString(), Is.EqualTo(text));
        }

        [TestCase("tank/t-34")]
        [TestCase("stock:tanks/t-34")]
        [TestCase("stock:tank/")]
        [TestCase("stock:/t-34")]
        [TestCase("stock:tank")]
        [TestCase("stock:tank/T-34")]
        [TestCase("stock:tank/t 34")]
        [TestCase("stock:tank/../x")]
        [TestCase("")]
        public void StockRef_RefusesAnythingElse(string text)
        {
            RempackStockRef reference;
            Assert.That(RempackStockRef.TryParse(text, out reference), Is.False, text);
        }

        [Test]
        public void StockRef_ComparesByKindAndName()
        {
            var a = new RempackStockRef(RempackContentKind.Tank, "t-34");
            var set = new HashSet<RempackStockRef> { a, new RempackStockRef(RempackContentKind.Tank, "T-34"), new RempackStockRef(RempackContentKind.Map, "t-34") };

            Assert.That(set.Count, Is.EqualTo(2));
            Assert.That(a.Equals(null), Is.False);
        }

        [Test]
        public void LocalizedText_FallsBackToTheBaseLanguageThenEnglish()
        {
            var text = new LocalizedText();
            text.Set("en", "Tank");
            text.Set("pt", "Tanque");
            text.Set("zh-TW", "\u5766\u514b");

            Assert.That(text.Get("pt-BR"), Is.EqualTo("Tanque"));
            Assert.That(text.Get("zh-tw"), Is.EqualTo("\u5766\u514b"), "language tags ignore case");
            Assert.That(text.Get("de"), Is.EqualTo("Tank"));
            Assert.That(text.Get(null), Is.EqualTo("Tank"));
            var only = new LocalizedText();
            only.Set("ru", "\u0422\u0430\u043d\u043a");
            Assert.That(only.Get("de"), Is.EqualTo("\u0422\u0430\u043d\u043a"), "then whatever exists");
            Assert.That(new LocalizedText().Get("en"), Is.Empty);
        }

        [TestCase("en", true)]
        [TestCase("pt-BR", true)]
        [TestCase("zh-tw", true)]
        [TestCase("EN", false)]
        [TestCase("eng", false)]
        [TestCase("pt_BR", false)]
        [TestCase("pt-B1", false)]
        [TestCase("", false)]
        public void IsLanguage_AcceptsTwoLetterCodesWithARegion(string code, bool valid)
        {
            Assert.That(LocalizedText.IsLanguage(code), Is.EqualTo(valid));
        }

        [Test]
        public void LocalizedText_ReadsAPlainStringAsEnglish()
        {
            var report = new RempackReport();
            var text = LocalizedText.Read(JsonNode.Str("Miku"), "name", 64, true, report, "rempack.json");

            Assert.That(report.Issues, Is.Empty);
            Assert.That(text.Get("ru"), Is.EqualTo("Miku"));
            Assert.That(text.ToJson().ToJson(false), Is.EqualTo("{\"en\":\"Miku\"}"));
        }

        [Test]
        public void LocalizedText_CutsAnOverlongTextAndSaysSo()
        {
            var report = new RempackReport();
            var text = LocalizedText.Read(JsonNode.Str(new string('a', 70)), "name", 64, true, report, "rempack.json");

            Assert.That(report.Has(RempackCodes.TextTooLong), Is.True);
            Assert.That(text.Get("en").Length, Is.EqualTo(64));
        }

        [TestCase("tanks/t1/tank.json", true)]
        [TestCase("art/Icon-2_b.PNG", true)]
        [TestCase("a", true)]
        [TestCase("/abs.json", false)]
        [TestCase("a//b", false)]
        [TestCase("a/./b", false)]
        [TestCase("a/../b", false)]
        [TestCase(".hidden", false)]
        [TestCase("name.", false)]
        [TestCase("a\\b", false)]
        [TestCase("c:/x", false)]
        [TestCase("sp ace.txt", false)]
        [TestCase("\u0442\u0430\u043d\u043a.json", false)]
        [TestCase("art/con.png", false)]
        [TestCase("LPT1", false)]
        [TestCase("aux.tar.gz", false)]
        [TestCase("console.txt", true)]
        [TestCase("", false)]
        public void Paths_AreSafeOnEveryFileSystem(string path, bool valid)
        {
            Assert.That(RempackPaths.IsValid(path), Is.EqualTo(valid), path);
        }

        [Test]
        public void Paths_LimitTheirLength()
        {
            Assert.That(RempackPaths.IsValid(new string('a', RempackLimits.SegmentLength)), Is.True);
            Assert.That(RempackPaths.IsValid(new string('a', RempackLimits.SegmentLength + 1)), Is.False);
            var longPath = string.Join("/", Enumerable.Repeat(new string('a', 40), 7));
            Assert.That(longPath.Length, Is.GreaterThan(RempackLimits.PathLength));
            Assert.That(RempackPaths.IsValid(longPath), Is.False);
            Assert.Throws<RempackException>(() => RempackPaths.Validate("../x"));
            Assert.That(RempackPaths.Validate("ok.txt"), Is.EqualTo("ok.txt"));
        }

        [TestCase("tanks/t1/tank.json", "model.glb", "tanks/t1/model.glb")]
        [TestCase("tanks/t1/tank.json", "./textures/a.ktx2", "tanks/t1/textures/a.ktx2")]
        [TestCase("tanks/t1/tank.json", "../shared/a.png", "tanks/shared/a.png")]
        [TestCase("tanks/t1/tank.json", "../../icon.png", "icon.png")]
        [TestCase("tanks/t1/tank.json", "my%20file.png", null)]
        [TestCase("tanks/t1/tank.json", "a%2Db.png", "tanks/t1/a-b.png")]
        [TestCase("tanks/t1/tank.json", "../../../escape.png", null)]
        [TestCase("tanks/t1/tank.json", "%2E%2E/%2E%2E/%2E%2E/x.png", null)]
        [TestCase("tanks/t1/tank.json", "/etc/passwd", null)]
        [TestCase("tanks/t1/tank.json", "c:\\x.png", null)]
        [TestCase("tanks/t1/tank.json", "..\\x.png", null)]
        [TestCase("tanks/t1/tank.json", "", null)]
        [TestCase("rempack.json", "icon.png", "icon.png")]
        public void Resolve_StaysInsideThePackage(string file, string relative, string expected)
        {
            Assert.That(RempackPaths.Resolve(file, relative), Is.EqualTo(expected));
        }

        [TestCase("__MACOSX/._icon.png", true)]
        [TestCase("art/.DS_Store", true)]
        [TestCase("art/thumbs.db", true)]
        [TestCase("Desktop.ini", true)]
        [TestCase("art/icon.png", false)]
        public void IsJunk_KnowsWhatArchiversAdd(string path, bool junk)
        {
            Assert.That(RempackPaths.IsJunk(path), Is.EqualTo(junk));
        }

        [Test]
        public void FeatureSet_ProvidesUpToItsVersion()
        {
            var set = new RempackFeatureSet("tanks@2", "maps", "tanks@1");

            Assert.That(set.Provides("tanks@1"), Is.True);
            Assert.That(set.Provides("tanks@2"), Is.True);
            Assert.That(set.Provides("tanks"), Is.True);
            Assert.That(set.Provides("tanks@3"), Is.False);
            Assert.That(set.Provides("maps@1"), Is.True);
            Assert.That(set.Provides("hangars@1"), Is.False);
            Assert.That(set.Provides("Tanks@1"), Is.False);
            Assert.That(set.Names, Is.EqualTo(new[] { "maps@1", "tanks@2" }));
            Assert.Throws<ArgumentException>(() => new RempackFeatureSet("tanks@0"));
        }

        [TestCase("tanks@1", "tanks", 1)]
        [TestCase("lua_scripts@12", "lua_scripts", 12)]
        [TestCase("maps", "maps", 1)]
        public void FeatureSplit_ReadsNameAndVersion(string feature, string name, int version)
        {
            string parsedName;
            int parsedVersion;
            Assert.That(RempackFeatureSet.Split(feature, out parsedName, out parsedVersion), Is.True);
            Assert.That(parsedName, Is.EqualTo(name));
            Assert.That(parsedVersion, Is.EqualTo(version));
        }

        [TestCase("tanks@")]
        [TestCase("@1")]
        [TestCase("tanks@x")]
        [TestCase("tanks@12345")]
        [TestCase("tanks2@1")]
        [TestCase("tanks@-1")]
        [TestCase("")]
        public void FeatureSplit_RefusesMalformedNames(string feature)
        {
            string name;
            int version;
            Assert.That(RempackFeatureSet.Split(feature, out name, out version), Is.False, feature);
        }

        [Test]
        public void Image_ReadsPngAndJpegSizesFromTheHeader()
        {
            RempackImageFormat format;
            int width, height;

            Assert.That(RempackImage.TryProbe(RempackFixture.Png(640, 480), out format, out width, out height), Is.True);
            Assert.That(new object[] { format, width, height }, Is.EqualTo(new object[] { RempackImageFormat.Png, 640, 480 }));
            Assert.That(RempackImage.TryProbe(RempackValidatorTests.Jpeg(1920, 1080), out format, out width, out height), Is.True);
            Assert.That(new object[] { format, width, height }, Is.EqualTo(new object[] { RempackImageFormat.Jpeg, 1920, 1080 }));
        }

        [Test]
        public void Image_RefusesBrokenHeaders()
        {
            RempackImageFormat format;
            int width, height;
            var png = RempackFixture.Png(64, 64);
            var zeroWide = RempackFixture.Png(0, 64);
            var truncatedJpeg = RempackValidatorTests.Jpeg(64, 64).Take(24).ToArray();
            var gif = Encoding.ASCII.GetBytes("GIF89a\u0001\u0000\u0001\u0000");
            var huge = (byte[])png.Clone();
            huge[16] = 0x80;

            Assert.That(RempackImage.TryProbe(zeroWide, out format, out width, out height), Is.False);
            Assert.That(RempackImage.TryProbe(png.Take(20).ToArray(), out format, out width, out height), Is.False);
            Assert.That(RempackImage.TryProbe(truncatedJpeg, out format, out width, out height), Is.False);
            Assert.That(RempackImage.TryProbe(gif, out format, out width, out height), Is.False);
            Assert.That(RempackImage.TryProbe(huge, out format, out width, out height), Is.False, "a width past 2^31 is not a size");
            Assert.That(RempackImage.TryProbe(null, out format, out width, out height), Is.False);
            Assert.That(format, Is.EqualTo(RempackImageFormat.Unknown));
        }

        [Test]
        public void Builder_WritesTheHashesAndTheSameBytesEveryTime()
        {
            var first = RempackFixture.Tank().Text("b.txt", "b").Text("a.txt", "a");
            var second = RempackFixture.Tank().Text("a.txt", "a").Text("b.txt", "b");
            var bytes = first.Build();

            Assert.That(bytes, Is.EqualTo(second.Build()));
            Assert.That(first.Manifest["files"]["a.txt"].AsString(), Is.EqualTo(RempackBuilder.Sha256(Encoding.UTF8.GetBytes("a"))));
            Assert.That(RempackBuilder.Sha256(new byte[0]), Is.EqualTo("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));
            Assert.That(RempackFixture.Validate(bytes, RempackFixture.Options()).HasErrors, Is.False);
        }

        [Test]
        public void Builder_RefusesAManifestAmongTheFiles()
        {
            var files = new Dictionary<string, byte[]> { { RempackFormat.ManifestFile, new byte[1] } };

            Assert.Throws<RempackException>(() => RempackBuilder.Pack(JsonNode.NewObject(), files));
            Assert.Throws<ArgumentException>(() => RempackBuilder.Pack(JsonNode.NewArray(), new Dictionary<string, byte[]>()));
            Assert.Throws<RempackException>(() => RempackBuilder.Pack(JsonNode.NewObject(), new Dictionary<string, byte[]> { { "../x", new byte[1] } }));
        }
    }
}
