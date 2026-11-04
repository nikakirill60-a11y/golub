using System;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Runtime.Assets;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class BigBossTests
    {
        private BigBossRules Rules { get { return new BigBossRules(BlitzResources.LoadText(BigBossRules.Resource)); } }
        [Test] public void ClientVersionAndSparseRoleIdsArePreserved()
        {
            var rules = Rules; Assert.AreEqual("reconstruction", rules.Data["status"].AsString());
            foreach (var id in new[] {1, 2, 4, 5, 6}) { Assert.AreEqual(id, rules.Role(id)["id"].AsInt()); Assert.AreEqual(5, rules.Role(id)["levels"].Count); }
            Assert.Throws<ArgumentOutOfRangeException>(() => rules.Role(3));
            Assert.AreEqual(48, rules.Data["abilities"].Count); Assert.AreEqual(0, rules.Data["reconstructedStrings"].Count);
        }
        [TestCase(0,1)] [TestCase(74,1)] [TestCase(75,2)] [TestCase(199,2)] [TestCase(200,3)] [TestCase(324,3)] [TestCase(325,4)] [TestCase(474,4)] [TestCase(475,5)]
        public void ProgressionUsesCumulativeClientThresholds(int points, int level) { Assert.AreEqual(level, Rules.Level(points)); }
        [Test] public void MaxLevelAndThresholdProgressAreBounded()
        { Assert.AreEqual(0, Rules.Progress(75)); Assert.AreEqual(1, Rules.Progress(900)); Assert.AreEqual(.5f, Rules.Progress(137.5), .0001f); }
        [Test] public void SourceNumbersOverrideHistoricalWebDescriptions()
        {
            var r = Rules; Assert.AreEqual(1.4f, r.Number("bigBoss", "commonDamageFactor")); Assert.AreEqual(40, r.Number("gravityBurst", "cooldown"));
            Assert.AreEqual(10, r.Number("shieldGiverAbilityL1", "shieldDuration")); Assert.AreEqual(30, r.Number("shieldGiverAbilityL5", "shieldDuration"));
            Assert.AreEqual(7, r.Number("gravityShotL1", "effectDuration")); Assert.AreEqual(45, r.Number("healingAuraL1", "cooldown"));
            Assert.AreEqual(1, r.Ability("bigBoss")["duplicateParams"].Count);
        }
        [Test] public void LevelUpReadiesTheButtonAbilityOfTheNewLevel()
        {
            var s = new BigBossState(Rules, 4); s.StartLife(10); Assert.True(s.Activate("poisonedShellL1", 10));
            s.Reward(100); Assert.AreEqual(1, s.Level); Assert.AreEqual(34, s.Remaining("poisonedShellL1", 11), "points short of a level keep the cooldown");
            s.Reward(10000); Assert.AreEqual(5, s.Level); Assert.AreEqual("poisonedShellL5", s.ManualAbility(s.Level));
            Assert.Zero(s.Remaining("poisonedShellL5", 11)); Assert.True(s.Activate("poisonedShellL5", 11)); Assert.AreEqual(25, s.Remaining("poisonedShellL5", 11));
            s.Reward(10000); Assert.AreEqual(25, s.Remaining("poisonedShellL5", 11), "the last level has nothing more to ready");
        }
        [Test] public void LevelUpKeepsSelfActivatingCooldownsAndTheBossLevel()
        {
            var s = new BigBossState(Rules, 2); s.StartLife(0); Assert.AreEqual("healingAuraL1", s.ManualAbility(1));
            Assert.True(s.Activate("healingAuraL1", 100)); Assert.True(s.Activate("shieldGiverAbilityL1", 100));
            s.Reward(0, 750); Assert.AreEqual(2, s.Level);
            Assert.Zero(s.Remaining("healingAuraL2", 101), "the button ability is ready again");
            Assert.AreEqual(39, s.Remaining("shieldGiverAbilityL2", 101), .001, "the self-activating shield keeps its cooldown");
            var boss = new BigBossState(Rules, 1); boss.StartLife(0); Assert.AreEqual("gravityBurst", boss.ManualAbility(boss.Level));
            Assert.True(boss.Activate("gravityBurst", 0)); boss.Reward(10000);
            Assert.AreEqual(1, boss.Level); Assert.AreEqual(40, boss.Remaining("gravityBurst", 0));
        }
        [Test] public void MirrorTakesTheLevelUpReadinessFromTheHost()
        {
            var host = new BigBossState(Rules, 5); host.StartLife(0); Assert.True(host.Activate("kamikazeAbilityL1", 100));
            var cooling = host.Capture(101); Assert.AreEqual(39, cooling.Ready0, .001f);
            host.Reward(750); Assert.AreEqual(2, host.Level); var levelled = host.Capture(101); Assert.Zero(levelled.Ready0);
            var mirror = new BigBossState(Rules, 5);
            Assert.True(mirror.Apply(cooling, 50)); Assert.AreEqual(39, mirror.Remaining("kamikazeAbilityL1", 50), .001);
            Assert.True(mirror.Apply(levelled, 51)); Assert.AreEqual(2, mirror.Level); Assert.Zero(mirror.Remaining("kamikazeAbilityL2", 51));
        }
        [Test] public void WrongRoleAndPassiveCannotBeActivated()
        {
            var s = new BigBossState(Rules, 6); s.StartLife(0);
            Assert.False(s.Activate("gravityBurst",0)); Assert.False(s.Activate("permanentDamageShieldL1",0)); Assert.False(s.Activate("gravityShotL1",double.NaN));
            Assert.True(s.Activate("gravityShotL1",0)); Assert.False(s.Activate("gravityShotL1",0));
        }
        [Test] public void BossHasThreeLivesNotThreeReturns()
        {
            var s = new BigBossState(Rules,1); s.StartLife(0); var random = new Random(11);
            for (var life=0; life<3; life++) { Assert.True(s.Die(life*10)); Assert.False(s.Die(life*10)); Assert.AreEqual(2-life,s.Lives); if (life<2) { Assert.False(s.Return(life*10+6.9,random)); Assert.True(s.Return(life*10+7,random)); Assert.AreEqual(1,s.RoleId); } }
            Assert.True(s.Exhausted); Assert.False(s.Respawning); Assert.False(s.Return(100,random)); Assert.AreEqual(2,s.LifeSerial);
        }
        [Test] public void OrdinaryReturnsAreUnlimitedAndRetainProgress()
        {
            var s = new BigBossState(Rules,2); s.Reward(2000,250,true); var points = s.Points; var random = new Random(12);
            for (var i=0;i<40;i++) { var role=s.RoleId; Assert.True(s.Die(i*10)); Assert.True(s.Return(i*10+7,random)); Assert.AreNotEqual(role,s.RoleId); Assert.False(s.Boss); Assert.AreEqual(points,s.Points); }
            Assert.AreEqual(40,s.LifeSerial); Assert.False(s.Exhausted);
        }
        [Test] public void RespawnInitialCooldownUsesCurrentLevelSourceData()
        {
            var s = new BigBossState(Rules,1); s.StartLife(20); Assert.AreEqual(30,s.Remaining("railgunCharger",20)); Assert.AreEqual(0,s.Remaining("gravityBurst",20));
            s.Die(20); Assert.True(s.Return(27,new Random(3))); Assert.AreEqual(30,s.Remaining("railgunCharger",27));
        }
        [Test] public void TicketLedgerDeduplicatesSlotAndLifeAndDoesNotResolveEarly()
        {
            var m = new BigBossMatch(Rules); Assert.True(m.Death(0,0,0,true)); Assert.False(m.Death(0,0,0,true)); Assert.AreEqual(25,m.TeamA);
            Assert.True(m.Death(0,1,0,true)); Assert.AreEqual(20,m.TeamA); Assert.False(m.Verdict(false,out _));
            Assert.True(m.Verdict(true,out var winner)); Assert.AreEqual(1,winner);
        }
        [Test] public void SimultaneousTicketExhaustionIsDrawRegardlessOfCallbackOrder()
        {
            foreach (var reverse in new[]{false,true}) { var m=new BigBossMatch(Rules); for(var i=0;i<30;i++) { m.Death(i+1,0,reverse?1:0,false); m.Death(i+31,0,reverse?0:1,false); } Assert.True(m.Verdict(false,out var winner)); Assert.AreEqual(-1,winner); Assert.AreEqual(0,m.TeamA); Assert.AreEqual(0,m.TeamB); }
        }
        [Test] public void TimeoutEqualTicketsIsDrawAndResetClearsDeduplication()
        { var m=new BigBossMatch(Rules); Assert.True(m.Verdict(true,out var winner)); Assert.AreEqual(-1,winner); m.Death(0,0,0,true); m.Reset(); Assert.AreEqual(30,m.TeamA); Assert.True(m.Death(0,0,0,true)); }
        [TestCase(1,false)] [TestCase(4,false)] [TestCase(5,true)] [TestCase(10,true)] [TestCase(11,true)] [TestCase(12,false)]
        public void EligibilityIsTierFiveThroughEleven(int tier,bool eligible) { Assert.AreEqual(eligible,BigBossRules.EligibleTier(tier)); }
        [Test] public void ModeIsAppendedAndPlayableOnline()
        {
            Assert.AreEqual(5,(int)BattleMode.BigBoss); Assert.AreEqual(4,(int)BattleMode.WorldOfDucks);
            Assert.AreEqual("bossmode",BattleModeCatalog.Id(BattleMode.BigBoss)); Assert.False(BattleModeCatalog.OfflineOnly(BattleMode.BigBoss)); Assert.True(BattleModeCatalog.AllowsOnline(BattleMode.BigBoss));
            Assert.False(SandboxOnlineBan.IsSandbox((int)BattleMode.BigBoss)); Assert.False(SandboxOnlineBan.Violates(BattleMode.BigBoss));
            var card=Array.IndexOf(BattleModeCatalog.MenuModes,BattleMode.BigBoss);
            Assert.AreEqual(Array.IndexOf(BattleModeCatalog.MenuModes,BattleMode.Uprising)+1,card,"the card follows Uprising");
            Assert.AreEqual(BattleMode.WorldOfDucks,BattleModeCatalog.MenuModes[card+1]); Assert.AreEqual(BattleMode.Sandbox,BattleModeCatalog.MenuModes[BattleModeCatalog.MenuModes.Length-1]);
        }
        [Test] public void BattlesLastFifteenMinutesAndPayLikeRandomBattles()
        {
            Assert.AreEqual(900,Rules.P("timeLimitSeconds"));
            Assert.True(BattleModeCatalog.PaysRewards(BattleMode.BigBoss)); Assert.True(BattleModeCatalog.PaysRewards(BattleMode.Normal)); Assert.True(BattleModeCatalog.PaysRewards(BattleMode.Uprising));
            Assert.False(BattleModeCatalog.PaysRewards(BattleMode.WorldOfDucks)); Assert.False(BattleModeCatalog.PaysRewards(BattleMode.Sandbox));
        }
        [Test] public void SnapshotMirrorsRoleLevelLifeAndCooldowns()
        {
            var host=new BigBossState(Rules,4); host.StartLife(0); host.Reward(2000); Assert.AreEqual(3,host.Level);
            Assert.True(host.Activate("poisonedShellL3",100)); var s=host.Capture(105);
            Assert.AreEqual(4,s.RoleId); Assert.AreEqual(-1,s.Lives); Assert.False(s.Respawning); Assert.AreEqual(25,s.Ready0,.001f);
            var mirror=new BigBossState(Rules,2); Assert.True(mirror.Apply(s,50));
            Assert.AreEqual(4,mirror.RoleId); Assert.AreEqual(3,mirror.Level); Assert.AreEqual(host.Points,mirror.Points,.001); Assert.False(mirror.Boss);
            Assert.AreEqual(host.Remaining("poisonedShellL3",105),mirror.Remaining("poisonedShellL3",50),.001);
            Assert.True(host.Die(110,new Random(3))); var dead=host.Capture(112);
            Assert.True(dead.Respawning); Assert.AreEqual(5,dead.RespawnIn,.001f); Assert.AreEqual(host.NextRoleId,dead.NextRoleId); Assert.GreaterOrEqual(dead.NextRoleId,0);
            Assert.True(mirror.Apply(dead,200)); Assert.True(mirror.Respawning); Assert.AreEqual(205,mirror.RespawnAt,.001); Assert.AreEqual(host.NextRoleId,mirror.NextRoleId);
            Assert.True(host.Return(117,new Random(3))); var next=host.Capture(117); Assert.AreEqual(1,next.LifeSerial);
            Assert.True(mirror.Apply(next,300)); Assert.False(mirror.Respawning); Assert.AreEqual(1,mirror.LifeSerial); Assert.AreEqual(host.RoleId,mirror.RoleId); Assert.AreEqual(-1,mirror.NextRoleId);
        }
        [Test] public void SnapshotCarriesBossLivesAndRejectsWhatTheTablesCannotDescribe()
        {
            var boss=new BigBossState(Rules,1); boss.StartLife(0); Assert.True(boss.Die(10)); var s=boss.Capture(10);
            Assert.AreEqual(2,s.Lives); var mirror=new BigBossState(Rules,2); Assert.True(mirror.Apply(s,0)); Assert.True(mirror.Boss); Assert.AreEqual(2,mirror.Lives);
            var bad=s; bad.RoleId=3; Assert.False(mirror.Apply(bad,0));
            bad=s; bad.NextRoleId=3; Assert.False(mirror.Apply(bad,0));
            bad=s; bad.Points=float.NaN; Assert.False(mirror.Apply(bad,0));
            Assert.False(mirror.Apply(s,double.NaN)); Assert.True(mirror.Boss); Assert.AreEqual(2,mirror.Lives);
            var wild=s; wild.Flags=BigBossSnapshot.FlagRespawning; wild.RespawnIn=float.PositiveInfinity; wild.Ready0=float.NaN; wild.Ready1=-4; wild.Ready2=0;
            Assert.True(mirror.Apply(wild,0)); Assert.True(mirror.Respawning); Assert.AreEqual(3600,mirror.RespawnAt,.001);
            foreach(var id in Rules.Abilities(1,1)) Assert.AreEqual(0,mirror.Remaining(id,0),.001,id);
            var gone=s; gone.Lives=0; Assert.True(mirror.Apply(gone,0)); Assert.True(mirror.Exhausted); Assert.False(mirror.Respawning,"no lives, no return");
            var m=new BigBossMatch(Rules); m.ApplyNet(12,-3); Assert.AreEqual(12,m.TeamA); Assert.AreEqual(0,m.TeamB);
        }
        [Test] public void OnlyAvailableClientTextIsImported()
        {
            var lang=LocalizationTable.Parse(BlitzResources.LoadText("lang"));
            foreach(var key in new[]{"battle_mode:bossmode/name","#bossmode:ability/active","#bossmode:ability/passive","#respawn/awaitReturnToBattleText"}) Assert.True(lang.Has(key),key);
            Assert.False(lang.Has("#bossmode:boss_role/descr")); Assert.False(lang.Has("big_boss:reconstruction"));
        }
        private static string Row(BigBossRules rules, string ability, string row)
        {
            foreach (var p in rules.Ability(ability)["descrParams"].Items) if (p["nameKey"].AsString() == row) return p["value"].AsString();
            return null;
        }
        [Test] public void SandboxTablesScaleEveryPassiveBoostAndPrintItOnTheCards()
        {
            var json = BlitzResources.LoadText(BigBossRules.Resource); var r = BigBossRules.Tuned(json, 5, 12, 2); var stock = Rules;
            Assert.AreEqual(5f, r.P("bossLives")); Assert.AreEqual(12f, r.P("startPoints")); Assert.AreEqual(9f, r.P("bossMaxHealthFactor"), 1e-5f, "x5 keeps twice its distance from 1");
            // Factors keep twice their distance from 1, never below the floor.
            Assert.AreEqual((float)BigBossRules.MinBoostFactor, r.Number("bigBoss", "gunReloadFactor"), 1e-6f); Assert.AreEqual(1.8f, r.Number("bigBoss", "commonDamageFactor"), 1e-5f);
            Assert.AreEqual(1.6f, r.Number("bigBoss", "enginePowerFactor"), 1e-5f); Assert.AreEqual(1.6f, r.Number("bigBoss", "speedLimitsFactor"), 1e-5f);
            Assert.AreEqual(.5f, r.Number("killerPassiveL1", "gunAimingFactor"), 1e-5f); Assert.AreEqual(.9f, r.Number("killerPassiveL1", "pumpGunReloadFactor"), 1e-5f);
            Assert.AreEqual(1.3f, r.Number("kamikazePassiveL1", "speedLimitsFactor"), 1e-5f);
            // Bonuses double; debuffs, odds and active abilities keep the source numbers.
            Assert.AreEqual(600f, r.Number("bigBoss", "maxHealthPercentBonus"), 1e-4f); Assert.AreEqual(60f, r.Number("permanentDamageShieldL5", "damageReductionPercent"), 1e-4f);
            Assert.AreEqual(.8f, r.Number("healingShotL1", "healFactor"), 1e-5f); Assert.AreEqual(.15f, r.Number("healingShotL1", "healRandomFactor"), 1e-5f);
            Assert.AreEqual(.5f, r.Number("kamikazePassiveL1", "chassisHealthFactor"), 1e-5f); Assert.AreEqual(20f, r.Number("bigBoss", "maxStaticsDamagePercent"));
            Assert.AreEqual(stock.Ability("railgunCharger")["params"].ToJson(false), r.Ability("railgunCharger")["params"].ToJson(false));
            Assert.AreEqual(stock.Ability("healingAuraL5")["params"].ToJson(false), r.Ability("healingAuraL5")["params"].ToJson(false));
            // The ability cards print the numbers in play.
            Assert.AreEqual("95", Row(r, "bigBoss", "Boss_reloadtime_description")); Assert.AreEqual("80", Row(r, "bigBoss", "Boss_damage_description"));
            Assert.AreEqual("60", Row(r, "bigBoss", "Boss_enginepower_description")); Assert.AreEqual("60", Row(r, "bigBoss", "Boss_maxspeed_description"));
            Assert.AreEqual("10", Row(r, "killerPassiveL1", "Killer_cd_description")); Assert.AreEqual("50", Row(r, "killerPassiveL1", "Killer_aim_description"));
            Assert.AreEqual("30", Row(r, "killerPassiveL1", "Killer_damage_description")); Assert.AreEqual("30", Row(r, "kamikazePassiveL1", "Kamikazi_maxspeed_description"));
            Assert.AreEqual("20", Row(r, "permanentDamageShieldL1", "Graviton_shield_description")); Assert.AreEqual("80", Row(r, "healingShotL1", "Healshot_power_description"));
            Assert.AreEqual("50", Row(stock, "bigBoss", "Boss_reloadtime_description"), "the shipped cards are untouched");
        }
        [Test] public void SandboxBoostMultiplierZeroPlaysPlainTanksAndLargeOnesStayBounded()
        {
            var json = BlitzResources.LoadText(BigBossRules.Resource); var plain = BigBossRules.Tuned(json, 1, 1, 0);
            Assert.AreEqual(1f, plain.P("bossMaxHealthFactor")); Assert.AreEqual(1f, plain.Number("bigBoss", "gunReloadFactor")); Assert.AreEqual(1f, plain.Number("killerPassiveL5", "commonDamageFactor"));
            Assert.AreEqual(0f, plain.Number("permanentDamageShieldL5", "damageReductionPercent")); Assert.AreEqual(0f, plain.Number("healingShotL5", "healFactor"));
            Assert.AreEqual("0", Row(plain, "bigBoss", "Boss_damage_description")); Assert.AreEqual("0", Row(plain, "kamikazePassiveL5", "Kamikazi_enginepower_description"));
            Assert.AreEqual("0", Row(plain, "killerPassiveL5", "Killer_aim_description"));
            var strong = BigBossRules.Tuned(json, 10, 200, 10);
            Assert.AreEqual(BigBossRules.MaxDamageReductionPercent, strong.Number("permanentDamageShieldL5", "damageReductionPercent"), 1e-4); Assert.AreEqual("90", Row(strong, "permanentDamageShieldL5", "Graviton_shield_description"));
            Assert.AreEqual(5f, strong.Number("bigBoss", "commonDamageFactor"), 1e-4f); Assert.AreEqual(41f, strong.P("bossMaxHealthFactor"), 1e-4f);
            Assert.AreEqual((float)BigBossRules.MinBoostFactor, strong.Number("killerPassiveL5", "gunAimingFactor"), 1e-6f); Assert.AreEqual("95", Row(strong, "killerPassiveL5", "Killer_aim_description"));
            Assert.Throws<ArgumentOutOfRangeException>(() => BigBossRules.Tuned(json, 0, 30, 1)); Assert.Throws<ArgumentOutOfRangeException>(() => BigBossRules.Tuned(json, 3, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => BigBossRules.Tuned(json, 3, 30, float.NaN)); Assert.Throws<ArgumentOutOfRangeException>(() => BigBossRules.Tuned(json, 3, 30, -1));
        }
    }
}
