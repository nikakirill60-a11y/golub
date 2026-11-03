using System;
using System.IO;
using BlitzLegacy.Core.Mods;
using BlitzLegacy.Core.Serialization;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class ModStateTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "mod_state_" + Guid.NewGuid().ToString("N"));
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

        private string StatePath
        {
            get { return ModState.PathIn(_root); }
        }

        [Test]
        public void Load_WithoutAFile_IsEmptyAndEverythingIsOn()
        {
            var state = ModState.Load(_root);

            Assert.That(state.Ids, Is.Empty);
            Assert.That(state.Crashed, Is.Empty);
            Assert.That(state.IsEnabled("any.mod"), Is.True);
            Assert.That(state.IsKnown("any.mod"), Is.False);
            Assert.That(state.Recovered, Is.False);
            Assert.That(state.Migrated, Is.False);
            Assert.That(ModState.Load(null).Ids, Is.Empty);
            Assert.That(ModState.Load(Path.Combine(_root, "missing")).Ids, Is.Empty);
        }

        [Test]
        public void Save_ThenLoad_KeepsTheOrderTheSwitchesAndTheCrashes()
        {
            var state = new ModState();
            state.Append("a.one");
            state.Append("b.two");
            state.SetEnabled("legacy_mod", false);
            state.MarkCrashed("b.two");
            state.Save(_root);

            var loaded = ModState.Load(_root);
            Assert.That(loaded.Ids, Is.EqualTo(new[] { "a.one", "b.two", "legacy_mod" }));
            Assert.That(loaded.IsEnabled("a.one"), Is.True);
            Assert.That(loaded.IsEnabled("b.two"), Is.False, "a crash switches the mod off");
            Assert.That(loaded.IsEnabled("legacy_mod"), Is.False);
            Assert.That(loaded.Crashed, Is.EqualTo(new[] { "b.two" }));
            Assert.That(File.Exists(StatePath + ".tmp"), Is.False);

            var json = JsonNode.Parse(File.ReadAllText(StatePath));
            Assert.That(json["format"].AsInt(), Is.EqualTo(ModState.FormatVersion));
            Assert.That(json["mods"][2]["id"].AsString(), Is.EqualTo("legacy_mod"));
            Assert.That(json["mods"][2]["enabled"].AsBool(true), Is.False);
        }

        [Test]
        public void Append_LeavesKnownModsWhereTheyAre()
        {
            var state = new ModState();
            state.Append("a.one");
            state.SetEnabled("b.two", false);
            state.Append("b.two");
            state.Append("a.one");
            state.Append(null);
            state.Append(string.Empty);

            Assert.That(state.Ids, Is.EqualTo(new[] { "a.one", "b.two" }));
            Assert.That(state.IsEnabled("b.two"), Is.False);
        }

        [Test]
        public void MoveBefore_ReordersAndIgnoresWhatItDoesNotKnow()
        {
            var state = new ModState();
            foreach (var id in new[] { "a", "b", "c", "d" })
            {
                state.Append(id);
            }

            state.MoveBefore("d", "b");
            Assert.That(state.Ids, Is.EqualTo(new[] { "a", "d", "b", "c" }));
            state.MoveBefore("a", null);
            Assert.That(state.Ids, Is.EqualTo(new[] { "d", "b", "c", "a" }));
            state.MoveBefore("b", "unknown");
            Assert.That(state.Ids, Is.EqualTo(new[] { "d", "c", "a", "b" }), "an unknown anchor means last");
            state.MoveBefore("c", "c");
            state.MoveBefore("unknown", "a");
            Assert.That(state.Ids, Is.EqualTo(new[] { "d", "c", "a", "b" }));
            Assert.That(state.IndexOf("a"), Is.EqualTo(2));
            Assert.That(state.IndexOf("unknown"), Is.EqualTo(-1));
        }

        [Test]
        public void Forget_DropsTheModAndItsCrash()
        {
            var state = new ModState();
            state.Append("a.one");
            state.MarkCrashed("a.one");
            state.Forget("a.one");
            state.Forget("never.seen");

            Assert.That(state.Ids, Is.Empty);
            Assert.That(state.Crashed, Is.Empty);
        }

        [Test]
        public void SetEnabled_BackOn_ClearsTheCrash()
        {
            var state = new ModState();
            state.MarkCrashed("a.one");
            state.MarkCrashed("a.one");
            Assert.That(state.Crashed, Is.EqualTo(new[] { "a.one" }));

            state.SetEnabled("a.one", true);
            Assert.That(state.Crashed, Is.Empty);
            Assert.That(state.IsEnabled("a.one"), Is.True);

            state.MarkCrashed("a.one");
            state.ClearCrashed();
            Assert.That(state.Crashed, Is.Empty);
            Assert.That(state.IsEnabled("a.one"), Is.False, "acknowledging a crash does not switch the mod back on");
        }

        [TestCase("{ not json")]
        [TestCase("[]")]
        [TestCase("{\"mods\": []}")]
        [TestCase("{\"format\": 1, \"mods\": {}}")]
        [TestCase("")]
        public void Load_SetsADamagedFileAside(string text)
        {
            File.WriteAllText(StatePath, text);
            var state = ModState.Load(_root);

            Assert.That(state.Recovered, Is.True);
            Assert.That(state.Ids, Is.Empty);
            Assert.That(File.Exists(StatePath), Is.False);
            Assert.That(File.ReadAllText(StatePath + ModState.BrokenSuffix), Is.EqualTo(text));

            state.Append("a.one");
            state.Save(_root);
            Assert.That(ModState.Load(_root).Ids, Is.EqualTo(new[] { "a.one" }));
        }

        [Test]
        public void Load_SkipsBrokenEntriesAndRepeats()
        {
            File.WriteAllText(StatePath, "{\"format\": 1, \"mods\": [{\"id\": \"a\", \"enabled\": false}, 5, {\"enabled\": true}, "
                + "{\"id\": \"a\", \"enabled\": true}, {\"id\": \"b\"}], \"crashed\": [\"a\", 7, \"a\"], \"future\": {}}");
            var state = ModState.Load(_root);

            Assert.That(state.Recovered, Is.False);
            Assert.That(state.Ids, Is.EqualTo(new[] { "a", "b" }));
            Assert.That(state.IsEnabled("a"), Is.False, "the first entry of an id counts");
            Assert.That(state.IsEnabled("b"), Is.True, "enabled defaults to on");
            Assert.That(state.Crashed, Is.EqualTo(new[] { "a" }));
        }

        [Test]
        public void Load_FinishesASaveThatDiedBeforeTheRename()
        {
            var state = new ModState();
            state.SetEnabled("a.one", false);
            File.WriteAllText(StatePath + ".tmp", state.ToJson().ToJson(true));

            var loaded = ModState.Load(_root);
            Assert.That(loaded.IsEnabled("a.one"), Is.False);
            Assert.That(File.Exists(StatePath), Is.True);
            Assert.That(File.Exists(StatePath + ".tmp"), Is.False);
        }

        [Test]
        public void Load_ImportsTheLegacySwitchListOnce()
        {
            var legacy = Path.Combine(_root, ModState.LegacyFileName);
            File.WriteAllText(legacy, "tiger_pack\n\n  panther_pack  \ntiger_pack\n");

            var state = ModState.Load(_root);
            Assert.That(state.Migrated, Is.True);
            Assert.That(state.Ids, Is.EqualTo(new[] { "tiger_pack", "panther_pack" }));
            Assert.That(state.IsEnabled("tiger_pack"), Is.False);
            Assert.That(state.IsEnabled("panther_pack"), Is.False);
            Assert.That(File.Exists(legacy), Is.True, "reading alone changes nothing on disk");

            state.Save(_root);
            Assert.That(File.Exists(legacy), Is.False, "the old list goes once the new file is written");
            var again = ModState.Load(_root);
            Assert.That(again.Migrated, Is.False);
            Assert.That(again.IsEnabled("tiger_pack"), Is.False);
        }

        [Test]
        public void Load_PrefersTheNewFileOverALeftoverLegacyList()
        {
            var state = new ModState();
            state.Append("a.one");
            state.Save(_root);
            File.WriteAllText(Path.Combine(_root, ModState.LegacyFileName), "a.one\n");

            var loaded = ModState.Load(_root);
            Assert.That(loaded.Migrated, Is.False);
            Assert.That(loaded.IsEnabled("a.one"), Is.True);
        }

        [Test]
        public void Update_ReadsBeforeItWrites_SoTwoWritersKeepBothChanges()
        {
            ModState.Update(_root, s => s.SetEnabled("legacy_mod", false));
            ModState.Update(_root, s => s.Append("author.pack"));
            var result = ModState.Update(_root, s => s.MoveBefore("author.pack", "legacy_mod"));

            Assert.That(result.Ids, Is.EqualTo(new[] { "author.pack", "legacy_mod" }));
            var loaded = ModState.Load(_root);
            Assert.That(loaded.Ids, Is.EqualTo(new[] { "author.pack", "legacy_mod" }));
            Assert.That(loaded.IsEnabled("legacy_mod"), Is.False);
        }

        [Test]
        public void Save_CreatesTheFolderAndIgnoresANullRoot()
        {
            var nested = Path.Combine(_root, "deeper", "mods");
            var state = new ModState();
            state.Append("a.one");
            state.Save(nested);
            state.Save(null);

            Assert.That(ModState.Load(nested).Ids, Is.EqualTo(new[] { "a.one" }));
        }
    }
}
