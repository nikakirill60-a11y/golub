using NUnit.Framework;

namespace BlitzLegacy.Sdk.Tests
{
    public sealed class IgnoreRulesTests
    {
        private static IgnoreRules Rules(params string[] lines)
        {
            var rules = new IgnoreRules();
            foreach (var line in lines)
            {
                rules.Add(line);
            }

            return rules;
        }

        [Test]
        public void EditorSourcesStayOutByDefault()
        {
            var rules = IgnoreRules.WithDefaults();

            Assert.That(rules.IsIgnored("tanks/t1/model.blend", false), Is.True);
            Assert.That(rules.IsIgnored("tanks/t1/model.blend1", false), Is.True);
            Assert.That(rules.IsIgnored("art/skin.PSD", false), Is.True);
            Assert.That(rules.IsIgnored("old.rempack", false), Is.True);
            Assert.That(rules.IsIgnored("notes.txt~", false), Is.True);
            Assert.That(rules.IsIgnored("tanks/t1/skin.png", false), Is.False);
            Assert.That(rules.IsIgnored("tanks/t1/tank.json", false), Is.False);
        }

        [Test]
        public void APatternWithoutASlashMatchesAtAnyDepth()
        {
            var rules = Rules("*.txt", "cache");

            Assert.That(rules.IsIgnored("a.txt", false), Is.True);
            Assert.That(rules.IsIgnored("deep/down/a.txt", false), Is.True);
            Assert.That(rules.IsIgnored("deep/cache", true), Is.True);
            Assert.That(rules.IsIgnored("a.txt.png", false), Is.False);
            Assert.That(rules.IsIgnored("cached", true), Is.False);
        }

        [Test]
        public void ASlashAnchorsThePatternToTheRoot()
        {
            var rules = Rules("docs/*.md", "/build");

            Assert.That(rules.IsIgnored("docs/guide.md", false), Is.True);
            Assert.That(rules.IsIgnored("tanks/docs/guide.md", false), Is.False);
            Assert.That(rules.IsIgnored("docs/deep/guide.md", false), Is.False);
            Assert.That(rules.IsIgnored("build", true), Is.True);
            Assert.That(rules.IsIgnored("tanks/build", true), Is.False);
        }

        [Test]
        public void ATrailingSlashMatchesFoldersOnly()
        {
            var rules = Rules("temp/");

            Assert.That(rules.IsIgnored("temp", true), Is.True);
            Assert.That(rules.IsIgnored("tanks/temp", true), Is.True);
            Assert.That(rules.IsIgnored("temp", false), Is.False);
        }

        [Test]
        public void TheLastMatchingLineWins()
        {
            Assert.That(Rules("*.txt", "!LICENSE.txt").IsIgnored("LICENSE.txt", false), Is.False);
            Assert.That(Rules("!LICENSE.txt", "*.txt").IsIgnored("LICENSE.txt", false), Is.True);
            Assert.That(Rules("*.txt", "!LICENSE.txt").IsIgnored("notes.txt", false), Is.True);
        }

        [Test]
        public void TwoStarsCrossFolders()
        {
            var rules = Rules("**/raw", "src/**/*.wav", "export/**");

            Assert.That(rules.IsIgnored("raw", true), Is.True);
            Assert.That(rules.IsIgnored("a/b/raw", true), Is.True);
            Assert.That(rules.IsIgnored("src/a.wav", false), Is.True);
            Assert.That(rules.IsIgnored("src/x/y/a.wav", false), Is.True);
            Assert.That(rules.IsIgnored("other/src/a.wav", false), Is.False);
            Assert.That(rules.IsIgnored("export/a/b.png", false), Is.True);
            Assert.That(rules.IsIgnored("export", true), Is.False);
        }

        [Test]
        public void ClassesAndSingleCharacters()
        {
            var rules = Rules("shot[0-9].png", "draft[!a-c].png", "v?.json", "odd[");

            Assert.That(rules.IsIgnored("shot7.png", false), Is.True);
            Assert.That(rules.IsIgnored("shotx.png", false), Is.False);
            Assert.That(rules.IsIgnored("draftd.png", false), Is.True);
            Assert.That(rules.IsIgnored("drafta.png", false), Is.False);
            Assert.That(rules.IsIgnored("v1.json", false), Is.True);
            Assert.That(rules.IsIgnored("v12.json", false), Is.False);
            Assert.That(rules.IsIgnored("odd[", false), Is.True);
        }

        [Test]
        public void CommentsBlanksCaseAndBackslashes()
        {
            var rules = Rules("# a comment", "", "   ", "\\#hash.png", "Work\\Files/", "*.PNG");

            Assert.That(rules.Count, Is.EqualTo(3));
            Assert.That(rules.IsIgnored("#hash.png", false), Is.True);
            Assert.That(rules.IsIgnored("work/files", true), Is.True);
            Assert.That(rules.IsIgnored("skin.png", false), Is.True);
        }
    }
}
