using System;
using System.Linq;
using BlitzLegacy.Core;
using BlitzLegacy.Core.Mods.Rempack;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class RempackVersionsTests
    {
        [TestCase("1.2.3", "1.2.3")]
        [TestCase("1.0", "1.0.0")]
        [TestCase("2", "2.0.0")]
        [TestCase("0.0.0", "0.0.0")]
        [TestCase("1.2.3-beta.1", "1.2.3-beta.1")]
        [TestCase("1.2.3-rc-1+build.5", "1.2.3-rc-1+build.5")]
        [TestCase("1.2.3+001", "1.2.3+001")]
        public void Parse_ReadsSemanticVersions(string text, string normalized)
        {
            Assert.That(SemanticVersion.Parse(text).ToString(), Is.EqualTo(normalized));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("v1.2.3")]
        [TestCase(" 1.2.3")]
        [TestCase("01.2.3")]
        [TestCase("1.02.3")]
        [TestCase("1.2.3.4")]
        [TestCase("1..3")]
        [TestCase("1.2.x")]
        [TestCase("1.2.3-")]
        [TestCase("1.2.3-01")]
        [TestCase("1.2.3-beta..1")]
        [TestCase("1.2.3-beta_1")]
        [TestCase("1.2.3+")]
        [TestCase("9999999999.0.0")]
        [TestCase("1.0.0-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
        public void TryParse_RefusesWhatIsNotAVersion(string text)
        {
            SemanticVersion version;
            Assert.That(SemanticVersion.TryParse(text, out version), Is.False);
            Assert.That(version, Is.Null);
        }

        [Test]
        public void CompareTo_FollowsTheSemVerPrecedence()
        {
            var ordered = new[]
            {
                "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2",
                "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "1.10.0", "2.0.0",
            }.Select(SemanticVersion.Parse).ToArray();

            for (var i = 0; i + 1 < ordered.Length; i++)
            {
                Assert.That(ordered[i] < ordered[i + 1], Is.True, ordered[i] + " < " + ordered[i + 1]);
                Assert.That(ordered[i + 1].CompareTo(ordered[i]), Is.EqualTo(1));
            }

            Assert.That(SemanticVersion.Parse("1.0.0+a"), Is.EqualTo(SemanticVersion.Parse("1.0.0+b")), "build metadata is not compared");
            Assert.That(SemanticVersion.Parse("1.0").GetHashCode(), Is.EqualTo(SemanticVersion.Parse("1.0.0+x").GetHashCode()));
            Assert.That(SemanticVersion.Parse("0.1.0").CompareTo(null), Is.EqualTo(1));
            Assert.That(SemanticVersion.Parse("1.0.0") >= SemanticVersion.Parse("1.0"), Is.True);
        }

        [TestCase("2.0b1", "2.0.0")]
        [TestCase("2.1.3-rc", "2.1.3")]
        [TestCase("3", "3.0.0")]
        [TestCase("10.4.2.7", "10.4.2")]
        public void FromGameVersion_ReadsTheNumbersOfABuildName(string build, string expected)
        {
            Assert.That(SemanticVersion.FromGameVersion(build).ToString(), Is.EqualTo(expected));
        }

        [Test]
        public void FromGameVersion_KnowsTheCurrentBuild()
        {
            Assert.That(SemanticVersion.FromGameVersion(BlitzVersion.Current), Is.Not.Null);
            Assert.That(SemanticVersion.FromGameVersion("beta"), Is.Null);
            Assert.That(SemanticVersion.FromGameVersion(null), Is.Null);
        }

        [TestCase("*", "0.0.1", true)]
        [TestCase("", "5.0.0", true)]
        [TestCase("1.2.3", "1.2.3", true)]
        [TestCase("1.2.3", "1.2.4", false)]
        [TestCase("=1.2.3", "1.2.3", true)]
        [TestCase(">=1.2", "1.2.0", true)]
        [TestCase(">=1.2", "1.1.9", false)]
        [TestCase(">= 1.2", "1.3.0", true)]
        [TestCase(">1.2", "1.2.9", false)]
        [TestCase(">1.2", "1.3.0", true)]
        [TestCase(">1.2.3", "1.2.4", true)]
        [TestCase("<2", "1.99.0", true)]
        [TestCase("<2", "2.0.0", false)]
        [TestCase("<=1.2", "1.2.9", true)]
        [TestCase("<=1.2", "1.3.0", false)]
        [TestCase(">=1.2.0 <2.0.0", "1.9.9", true)]
        [TestCase(">=1.2.0 <2.0.0", "2.0.0", false)]
        [TestCase("^1.2", "1.9.0", true)]
        [TestCase("^1.2", "2.0.0", false)]
        [TestCase("^1.2", "1.1.0", false)]
        [TestCase("^0.2.3", "0.2.9", true)]
        [TestCase("^0.2.3", "0.3.0", false)]
        [TestCase("^0.0.3", "0.0.4", false)]
        [TestCase("^0", "0.9.0", true)]
        [TestCase("~1.2.3", "1.2.9", true)]
        [TestCase("~1.2.3", "1.3.0", false)]
        [TestCase("~1", "1.9.0", true)]
        [TestCase("1.x", "1.4.0", true)]
        [TestCase("1.x", "2.0.0", false)]
        [TestCase("1.2.*", "1.2.7", true)]
        [TestCase("1.2.*", "1.3.0", false)]
        [TestCase("1", "1.5.0", true)]
        [TestCase("<1.0.0 || >=2.0.0", "0.5.0", true)]
        [TestCase("<1.0.0 || >=2.0.0", "1.5.0", false)]
        [TestCase("<1.0.0 || >=2.0.0", "2.0.0", true)]
        public void Range_Matches(string range, string version, bool matches)
        {
            VersionRange parsed;
            Assert.That(VersionRange.TryParse(range, out parsed), Is.True, range);
            Assert.That(parsed.Matches(SemanticVersion.Parse(version)), Is.EqualTo(matches), range + " ~ " + version);
        }

        [TestCase("=>1")]
        [TestCase(">1.x")]
        [TestCase("^")]
        [TestCase("1.2 - 2.0")]
        [TestCase("||")]
        [TestCase("latest")]
        [TestCase(">=1.2.3.4")]
        public void Range_RefusesWhatItCannotRead(string range)
        {
            VersionRange parsed;
            Assert.That(VersionRange.TryParse(range, out parsed), Is.False, range);
            Assert.That(VersionRange.TryParse(null, out parsed), Is.False);
            Assert.That(VersionRange.TryParse(new string('1', 200), out parsed), Is.False);
        }

        [Test]
        public void Range_KeepsItsTextAndNeverMatchesNoVersion()
        {
            VersionRange parsed;
            VersionRange.TryParse("  ^1.2  ", out parsed);

            Assert.That(parsed.Text, Is.EqualTo("^1.2"));
            Assert.That(parsed.ToString(), Is.EqualTo("^1.2"));
            Assert.That(parsed.Matches(null), Is.False);
            Assert.That(VersionRange.Any.Matches(SemanticVersion.Parse("0.0.1-alpha")), Is.True);
        }
    }
}
