using System.Linq;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;
namespace BlitzLegacy.Tests
{
    public sealed class AdvancedRewardTests
    {
        private static BattleRewardInput Input()=>new BattleRewardInput{HadFirstVictory=true,HadFirstBlood=true};
        [TestCase(2,"lehvaslaiho")][TestCase(3,"oskin")][TestCase(4,"nikolas")]
        public void Medium_ClassRewardsUseSourceKillThresholds(int kills,string medal){var i=Input();i.Kills=kills;i.PlayerTankClass=(int)TankClass.Medium;Assert.IsTrue(BattleRewardCalculator.Calculate(i).Rewards.Any(r=>r.Id==medal));}
        [TestCase(2,false)][TestCase(3,true)][TestCase(5,true)]
        public void Orlik_LightOnlyAtLeastThreeKills(int kills,bool awarded){var i=Input();i.Kills=kills;i.PlayerTankClass=(int)TankClass.Light;Assert.AreEqual(awarded,BattleRewardCalculator.Calculate(i).Rewards.Any(r=>r.Id=="orlik"));}
        [TestCase(2,"none")][TestCase(3,"billotte")][TestCase(4,"bruno_pietro")][TestCase(5,"bruno_pietro")][TestCase(6,"tarzcay")]
        public void ModuleLowHealthAwards_AreExclusiveAndStrictGreater(int kills,string expected){var i=Input();i.TarczayKills=kills;var ids=BattleRewardCalculator.Calculate(i).Rewards.Select(r=>r.Id).ToArray();Assert.AreEqual(expected=="none"?0:1,ids.Length);if(ids.Length>0)Assert.AreEqual(expected,ids[0]);}
        [Test] public void Piercer_ProducesOneRewardPerTenHitSeries(){var i=Input();i.PiercerStreaks=3;var r=BattleRewardCalculator.Calculate(i);Assert.AreEqual(3,r.Rewards.Count);Assert.AreEqual(60,r.Rewards.Sum(x=>x.Gold));}
        [TestCase(5,"radley_walters")][TestCase(6,"lafayette_pool")][TestCase(7,"rassenay")][TestCase(10,"rassenay")]
        public void KillMedals_RespectExactCountsAndTeamMaximum(int kills,string expected){var i=Input();i.Kills=kills;i.MaxTeamTanks=kills>7?kills:7;Assert.IsTrue(BattleRewardCalculator.Calculate(i).Rewards.Any(r=>r.Id==expected));}
        [Test] public void BetaReward_OneTimeAndGivesBoughtKv220Once(){var i=Input();i.Id="beta";i.SourceBetaBuild=true;var r=BattleRewardCalculator.Calculate(i);var a=new AccountData();Assert.IsTrue(BattleResultApplier.Apply(a,r));Assert.IsTrue(a.HasTank("kv-220"));Assert.AreEqual(0,a.AchievementsDone.Count);Assert.IsFalse(BattleResultApplier.Apply(a,r));i.HadBetaTester=true;Assert.IsFalse(BattleRewardCalculator.Calculate(i).Rewards.Any(x=>x.Id=="beta_tester"));}
        [Test] public void Mastery_UsesSourceExclusiveThresholdsAndResolvedHistoryQuirk(){var i=Input();i.PreviousTankBattles=11;i.Damage=800;i.MasteryReferenceXp=100;Assert.IsTrue(BattleRewardCalculator.Calculate(i).Rewards.Any(r=>r.Id=="mastery_mark_m"));i.MasteryReferenceXp=0;Assert.IsTrue(BattleRewardCalculator.Calculate(i).Rewards.Any(r=>r.Id=="mastery_mark_m"));i.PreviousTankBattles=10;Assert.IsFalse(BattleRewardCalculator.Calculate(i).Rewards.Any(r=>r.Id.StartsWith("mastery_")));}
        [Test] public void DefenderAndKolobanov_UseRecoveredCounters(){var i=Input();i.BaseTicksDefendedMax=70;i.KolobanovKills=3;var r=BattleRewardCalculator.Calculate(i);Assert.IsTrue(r.Rewards.Any(x=>x.Id=="defender"));Assert.IsTrue(r.Rewards.Any(x=>x.Id=="kolobanov"));}
    }
}
