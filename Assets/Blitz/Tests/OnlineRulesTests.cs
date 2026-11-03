using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Online every peer must play by the same stock rules, whatever its
    /// player changed on the matchmaker and gameplay tabs for offline battles.
    /// </summary>
    public sealed class OnlineRulesTests
    {
        [Test]
        public void StandardRules_AreTheStockMatchmakerAndGameplayRows()
        {
            var rules = SettingsData.StandardRules();
            Assert.AreEqual(SettingsData.TeamSize7x7, rules.MatchmakerTeamSize);
            Assert.AreEqual(SettingsData.Time7, rules.BattleMaxTime);
            Assert.AreEqual(SettingsData.Dev25, rules.DmgDeviation);
            Assert.AreEqual(SettingsData.Dev5, rules.PenDeviation);
            Assert.AreEqual(1f, rules.ShellGravityMul);
            Assert.AreEqual(0.25f, rules.BattleSpottingDelay);
            Assert.IsFalse(rules.NoAimTime);
            Assert.IsFalse(rules.NoShotDispersion);
            Assert.IsFalse(rules.BotsHarmless);
            Assert.IsFalse(rules.BotsStationary);
        }

        [Test]
        public void StandardRules_AreSevenVersusSeven()
        {
            Assert.AreEqual(7, BattleOnline.TeamSize);
            Assert.AreEqual(14, BattleOnline.Capacity);
            Assert.AreEqual(BattleOnline.TeamSize, BattleSession.TeamSizeFor(SettingsData.StandardRules()));
        }

        [Test]
        public void StandardRules_IgnoreAnEditedSave()
        {
            var personal = new SettingsData
            {
                MatchmakerTeamSize = SettingsData.TeamSize1x1,
                NoShotDispersion = true,
                BotsHarmless = true,
                ShellGravityMul = 3f,
            };
            Assert.AreEqual(1, BattleSession.TeamSizeFor(personal));
            var rules = SettingsData.StandardRules();
            Assert.AreNotSame(personal, rules);
            Assert.AreEqual(7, BattleSession.TeamSizeFor(rules));
            Assert.IsTrue(BattleTank.DispersionWanted(rules));
            Assert.AreEqual(1f, BattleProjectile.ShellGravityScale(rules));
        }
    }
}
