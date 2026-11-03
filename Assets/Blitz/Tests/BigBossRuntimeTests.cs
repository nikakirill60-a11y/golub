using System;
using System.Collections.Generic;
using System.Reflection;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Sandbox;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    public sealed class BigBossRuntimeTests
    {
        private BattleSession _session;
        private float _scale;
        [SetUp] public void Setup() { _scale=Time.timeScale; Time.timeScale=1; }
        [TearDown] public void Cleanup()
        {
            if(_session!=null) Object.DestroyImmediate(_session.gameObject);
            // Edit mode never runs the level-up badge's OnDestroy: the shared badge camera a mirrored
            // level-up switched on would keep rendering (and building the URP pipeline) in later tests.
            var badge=GameObject.Find("BigBossUiParticlesCamera"); if(badge!=null) Object.DestroyImmediate(badge);
            Time.timeScale=_scale;
        }
        /// <summary>The hangar loadout the player brings: repair kit, boost and adrenaline in the third slot.</summary>
        private static readonly string[] Hangar={ConsumableCatalog.RepairKit,ConsumableCatalog.Afterburning,ConsumableCatalog.Berserk};
        private static TankSaveData Loadout(params string[] consumables)
        {
            // No saved rack: the seat takes the stock one, exactly like a battle without a save.
            var save=new TankSaveData{Name="t-34"}; save.ShellsAmounts.Clear(); save.Consumables.AddRange(consumables); return save;
        }
        private void Create() { Create(Hangar); }
        private void Create(params string[] consumables) { _session=BattleSession.Create(null,false,tankId:"t-34",autoStart:true,playerTank:Loadout(consumables),mode:BattleMode.BigBoss); }
        private static List<string> Ids(BattleTank tank) { var ids=new List<string>(); foreach(var item in tank.ActiveConsumables) ids.Add(item.Info.Id); return ids; }
        private static object Invoke(object instance,string method,params object[] args) { return instance.GetType().GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(instance,args); }
        private void Role(BattleTank tank,int role)
        {
            var c=tank.BigBoss;
            typeof(BigBossTank).GetProperty("State").SetValue(c,new BigBossState(_session.BigBoss.Rules,role));
            Invoke(c,"ApplyModifiers",true); c.Begin();
        }
        private static void Kill(BattleTank tank)
        { tank.ReceiveDamage(new DamageInfo {Dmg=tank.Health.MaxHealth*10,DmgBase=tank.Health.MaxHealth*10,DmgType=DamageType.Ability}); Assert.True(tank.Dead); }
        private void Return(BigBossTank tank)
        {
            Assert.True(tank.State.Return(Time.time+8,_session.BigBoss.Random));
            typeof(BigBossTank).GetField("_returnSpawn",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(tank,tank.Tank.Team==0?_session.World.PlayerSpawns[0]:_session.World.BotSpawns[0]);
            Invoke(tank,"Restore");
        }
        [Test] public void SandboxPlaysBigBossOnAnyTierWithThePresetLivesPointsBoostsAndTraverse()
        {
            var config=new SandboxConfig{BaseMode=SandboxBaseMode.BigBoss,AlliedTeamSize=3,EnemyTeamSize=3,BigBossLives=5,BigBossTeamPoints=12,BigBossBuffMul=2,BattleTimeMinutes=4};
            config.Global[SandboxStat.TurnRate]=2;
            var steps=BattleSession.CreateSteps(null,false,built=>_session=built,BattleWorld.DefaultMapId,"ms-1",true,null,null,false,BattleMode.Sandbox,null,new SandboxBattle(config));
            while(steps.MoveNext()){}
            Assert.AreEqual(BattleMode.BigBoss,_session.Mode); Assert.NotNull(_session.BigBoss); Assert.AreEqual(1,_session.Player.Card.Level,"the Sandbox has no tier V-X rule");
            Assert.AreEqual(12,_session.BigBoss.Match.TeamA); Assert.AreEqual(12,_session.BigBoss.Match.TeamB); Assert.AreEqual(240,_session.Roster.TimeLimitSeconds);
            var bosses=0; var maxHp=typeof(BigBossTank).GetField("_maxHp",BindingFlags.NonPublic|BindingFlags.Instance);
            foreach(var tank in _session.BattleTanks())
            {
                var c=tank.BigBoss; Assert.NotNull(c,tank.name);
                Assert.AreEqual(tank==_session.Player?0:2,c.Tank.ActiveConsumables.Count,tank==_session.Player?"the player's own empty hangar loadout":"the bots' support pair");
                var stock=tank.TrackInfo!=null?tank.TrackInfo.RotationSpeed:SuspensionTuning.DefaultRotationSpeed;
                Assert.AreEqual(stock*2,tank.Suspension.RotationSpeed,1e-3f,"the traverse multiplier on "+tank.name);
                if(!c.State.Boss) continue;
                bosses++; Assert.AreEqual(5,c.State.Lives);
                Assert.AreEqual(Mathf.RoundToInt((int)maxHp.GetValue(c)*9f),tank.Health.MaxHealth,"boosts x2 grow the boss pool from x5 to x9");
            }
            Assert.AreEqual(2,bosses,"one boss a team");
        }
        [Test] public void CompositionIsSevenPerTeamWithOneBossAndTheSupportPairOnEveryBot()
        {
            Create(); Assert.AreEqual(7,_session.Allies.Count);Assert.AreEqual(7,_session.Enemies.Count);Assert.AreEqual(900,_session.Roster.TimeLimitSeconds);
            foreach(var side in new[]{_session.Allies,_session.Enemies}) { var bosses=0;foreach(var tank in side)
            {
                Assert.NotNull(tank.BigBoss);if(tank.BigBoss.State.Boss)bosses++;Assert.True(BigBossRules.EligibleTier(tank.Card.Level));
                // Bots get the repair kit and boost; the player keeps its own hangar loadout, as in every mode.
                CollectionAssert.AreEqual(tank==_session.Player?Hangar:new[]{ConsumableCatalog.RepairKit,ConsumableCatalog.Afterburning},Ids(tank),tank.name);
            }Assert.AreEqual(1,bosses); }
            Assert.AreEqual(30,_session.Roster.TeamAScore);Assert.AreEqual(30,_session.Roster.TeamBScore);
        }
        [Test] public void OnlinePuppetMirrorsTheHostSeatAndAsksTheHostForItsAbility()
        {
            Create(); var host=_session.Enemies[0]; var tank=_session.Player; Role(host,4); Role(tank,2);
            host.BigBoss.State.Reward(2000); host.BigBoss.State.StartLife(Time.time-100); var snapshot=host.BigBoss.CaptureNet();
            Assert.AreEqual(4,snapshot.RoleId); Assert.Zero(snapshot.Ready0);
            var asked=new List<byte>(); Action<byte> listen=a=>asked.Add(a);
            tank.SetNetPuppet(); BattleNetHooks.PuppetWorld=true; BattleNetHooks.UprisingActionRequested+=listen;
            try
            {
                tank.BigBoss.ApplyNet(snapshot);
                Assert.AreEqual(4,tank.BigBoss.State.RoleId); Assert.AreEqual(3,tank.BigBoss.State.Level); Assert.AreEqual(200,tank.BigBoss.State.Points,.01);
                Assert.AreEqual("poisonedShellL3",tank.BigBoss.ManualAbility);
                Assert.True(tank.BigBoss.UseRoleAbility()); CollectionAssert.AreEqual(new[]{BattleRemoteDriver.BigBossAbilityAction},asked);
                Assert.Zero(tank.BigBoss.State.Remaining("poisonedShellL3",Time.time),"the host spends the cooldown");
                Assert.False(tank.BigBoss.Activate("poisonedShellL3"),"a puppet never casts by itself");
            }
            finally { BattleNetHooks.UprisingActionRequested-=listen; BattleNetHooks.PuppetWorld=false; }
        }
        [Test] public void LevelUpReadiesTheFourthSlotAbilityOfflineAndOnline()
        {
            Create(); var tank=_session.Player; var host=_session.Enemies[0]; Role(tank,5); Role(host,5);
            var c=tank.BigBoss; var slot=tank.HudMode.Ability; c.State.StartLife(Time.time-100);
            Assert.True(c.UseRoleAbility()); Assert.AreEqual(BattleModeAbilityPhase.Cooling,slot.Read(Time.time).Phase); Assert.False(c.UseRoleAbility());
            c.State.Reward(750); Assert.AreEqual(2,c.State.Level); Assert.AreEqual("kamikazeAbilityL2",c.ManualAbility);
            Assert.AreEqual(BattleModeAbilityPhase.Ready,slot.Read(Time.time).Phase,"the level-up readies the button");
            Assert.True(c.UseRoleAbility()); Assert.AreEqual(BattleModeAbilityPhase.Cooling,slot.Read(Time.time).Phase);
            host.BigBoss.State.StartLife(Time.time-100); Assert.True(host.BigBoss.State.Activate(host.BigBoss.ManualAbility,Time.time));
            var cooling=host.BigBoss.CaptureNet(); host.BigBoss.State.Reward(750); var levelled=host.BigBoss.CaptureNet();
            var asked=new List<byte>(); Action<byte> listen=a=>asked.Add(a);
            tank.SetNetPuppet(); BattleNetHooks.PuppetWorld=true; BattleNetHooks.UprisingActionRequested+=listen;
            try
            {
                tank.BigBoss.ApplyNet(cooling); Assert.AreEqual(1,c.State.Level); Assert.AreEqual(BattleModeAbilityPhase.Cooling,slot.Read(Time.time).Phase);
                Assert.False(c.UseRoleAbility()); CollectionAssert.IsEmpty(asked);
                tank.BigBoss.ApplyNet(levelled); Assert.AreEqual(2,c.State.Level);
                Assert.AreEqual(BattleModeAbilityPhase.Ready,slot.Read(Time.time).Phase,"the client mirrors the host level-up");
                Assert.True(c.UseRoleAbility()); CollectionAssert.AreEqual(new[]{BattleRemoteDriver.BigBossAbilityAction},asked);
            }
            finally { BattleNetHooks.UprisingActionRequested-=listen; BattleNetHooks.PuppetWorld=false; }
        }
        [Test] public void OnlinePuppetReturnsOnlyWithTheHostsNextLifeAndTakesTheHostsTickets()
        {
            Create(); var tank=_session.Player; Role(tank,2); var alive=tank.BigBoss.CaptureNet(); var tickets=_session.BigBoss.Match.TeamA;
            tank.SetNetPuppet(); BattleNetHooks.PuppetWorld=true;
            try
            {
                tank.ApplyNetState(tank.transform.position,tank.transform.rotation,0,0,0); Assert.True(tank.Dead);
                Assert.AreEqual(tickets,_session.BigBoss.Match.TeamA,"only the host's ledger counts a death");
                tank.BigBoss.ApplyNet(alive); Assert.True(tank.Dead,"a late state of the lost life never revives the wreck");
                var waiting=alive; waiting.Flags=BigBossSnapshot.FlagRespawning; waiting.RespawnIn=5; waiting.NextRoleId=5;
                tank.BigBoss.ApplyNet(waiting); Assert.True(tank.Dead); Assert.True(tank.BigBoss.State.Respawning); Assert.AreEqual(5,tank.BigBoss.State.NextRoleId);
                var next=alive; next.LifeSerial=alive.LifeSerial+1; next.RoleId=5;
                tank.BigBoss.ApplyNet(next); Assert.False(tank.Dead); Assert.AreEqual(5,tank.BigBoss.State.RoleId); Assert.AreEqual(next.LifeSerial,tank.BigBoss.State.LifeSerial);
                _session.BigBoss.ApplyNet(12,9); Assert.AreEqual(12,_session.BigBoss.Match.TeamA); Assert.AreEqual(9,_session.BigBoss.Match.TeamB);
            }
            finally { BattleNetHooks.PuppetWorld=false; }
        }
        [Test] public void OnlineRailgunRoundIsMarkedOnTheHostAndReplayedOnlyWhenPrimed()
        {
            Create(); var tank=_session.Player; Role(tank,1); var c=tank.BigBoss; c.State.StartLife(Time.time-100); Invoke(c,"AutoAbilities"); Assert.True(c.RailgunArmed);
            var shell=tank.CurrentShell();
            Assert.True(c.Launch(shell,41).Railgun); Assert.True(c.IsRailgunShot(41)); Assert.False(c.IsRailgunShot(0));
            Assert.False(c.Launch(shell,42).Railgun); Assert.False(c.IsRailgunShot(42)); Assert.True(c.IsRailgunShot(41),"the mark stays with its round");
            BattleNetHooks.PuppetWorld=true;
            try
            {
                Assert.False(c.Launch(shell,43).Railgun,"a replay is plain unless the host said otherwise");
                c.PrimeNetShot(true); var replay=c.Launch(shell,44); Assert.True(replay.Railgun); Assert.AreEqual(c.State.Rules.Number("railgunCharger","projectileSpeed"),replay.Speed);
                Assert.False(c.Launch(shell,45).Railgun,"the flag covers one round");
            }
            finally { BattleNetHooks.PuppetWorld=false; }
        }
        [Test] public void InvalidTierRejectedBeforeCreatingSession()
        { Assert.Throws<ArgumentException>(()=>BattleSession.Create(null,false,tankId:"ms-1",mode:BattleMode.BigBoss)); }
        [Test] public void TemporaryTeamEliminationCannotEndTicketBattle()
        {
            Create();foreach(var tank in _session.Enemies)Kill(tank); Assert.True(_session.Started); Assert.AreEqual(19,_session.Roster.TeamBScore);
            foreach(var tank in _session.Enemies)Assert.True(tank.BigBoss.State.Respawning);
        }
        [Test] public void ReturnRestoresAmmunitionHealthModulesAndLifeIdentity()
        {
            Create();Role(_session.Player,2);var tank=_session.Player;var ammo=new List<int>(tank.ShellAmounts);tank.SetShellAmounts(new[]{0,0,0});Kill(tank);Return(tank.BigBoss);
            Assert.False(tank.Dead);Assert.AreEqual(tank.Health.MaxHealth,tank.Health.Health);Assert.False(tank.Blocked);Assert.AreEqual(1,tank.BigBoss.State.LifeSerial);CollectionAssert.AreEqual(ammo,tank.ShellAmounts);Assert.AreSame(tank,_session.View.Tank);
            Kill(tank);Assert.AreEqual(28,_session.Roster.TeamAScore);
        }
        [Test] public void BossUsesExactlyThreeLivesAndRemainsVisible()
        {
            Create();Role(_session.Player,1);var tank=_session.Player;
            tank.SetSpotted(false);Assert.True(tank.Spotted);
            for(var life=0;life<3;life++){Kill(tank);if(life<2)Return(tank.BigBoss);}
            Assert.True(tank.BigBoss.State.Exhausted);Assert.AreEqual(15,_session.Roster.TeamAScore);
        }
        [Test] public void BossAndCatalystModifiersUseSourceValuesWithoutChangingCard()
        {
            Create();var tank=_session.Player;Role(tank,2);var baseHp=tank.Health.MaxHealth;var reload=tank.GunInfo.Reload;var cardHp=tank.Card.TotalHealth;
            Role(tank,1);Assert.AreEqual(baseHp*5,tank.Health.MaxHealth);Assert.AreEqual(reload*.5f,tank.GunInfo.Reload,.0001f);Assert.AreEqual(1.4f,tank.BigBoss.Launch(tank.CurrentShell()).Damage,.0001f);Assert.AreEqual(cardHp,tank.Card.TotalHealth);
            Role(tank,4);Assert.AreEqual(baseHp,tank.Health.MaxHealth);Assert.AreEqual(reload*.95f,tank.GunInfo.Reload,.0001f);
        }
        [Test] public void GravitonDoesNotReduceFireOrPoisonButReducesDirectDamage()
        {
            Create();Role(_session.Enemies[0],6);var target=_session.Enemies[0];var hp=target.Health.Health;
            foreach(var info in new[]{new DamageInfo {Dmg=100,DmgBase=100,DmgType=DamageType.Shell},new DamageInfo {Dmg=100,DmgBase=100,DmgType=DamageType.Fire},new DamageInfo {Dmg=100,DmgBase=100,DmgType=DamageType.Ability,BigBossPoisonTick=true}})target.ReceiveDamage(info);
            Assert.AreEqual(hp-290,target.Health.Health);
        }
        [Test] public void FriendlyHealingUsesLaunchSnapshotAndOnlyEffectiveHealthAwardsProgress()
        {
            Create();var healer=_session.Player;Role(healer,2);var ally=_session.Allies.Find(t=>t!=healer);Role(ally,4);ally.Health.ReceiveDamageBasic(100);
            var shot=healer.BigBoss.Launch(healer.CurrentShell());var before=ally.Health.Health;Assert.True(shot.Friendly(healer,ally,healer.CurrentShell()));var effective=ally.Health.Health-before;
            Assert.Greater(effective,0);Assert.LessOrEqual(effective,100);Assert.AreEqual(effective*.1,healer.BigBoss.State.Points,.0001);
            shot.Friendly(healer,ally,healer.CurrentShell());var max=healer.BigBoss.State.Points;shot.Friendly(healer,ally,healer.CurrentShell());Assert.AreEqual(max,healer.BigBoss.State.Points);
            Assert.False(shot.Friendly(healer,_session.Enemies[0],healer.CurrentShell()));
        }
        [Test] public void NonHealerFriendlyShellDoesNotDamageOrHeal()
        {
            Create();Role(_session.Player,4);var ally=_session.Allies.Find(t=>t!=_session.Player);var hp=ally.Health.Health;
            Assert.True(_session.Player.BigBoss.Launch(_session.Player.CurrentShell()).Friendly(_session.Player,ally,_session.Player.CurrentShell()));
            ally.ReceiveDamage(new DamageInfo {AttackerTank=_session.Player,Dmg=100,DmgBase=100});Assert.AreEqual(hp,ally.Health.Health);
        }
        [Test] public void PoisonRefreshesPerCasterAndHealingShotCleanses()
        {
            Create();Role(_session.Player,4);Role(_session.Enemies[0],4);var target=_session.Enemies[0];
            var hit=new DamageInfo {AttackerTank=_session.Player,Dmg=1,DmgBase=1,BigBossPoisonPercent=5,BigBossPoisonTicks=3,BigBossPoisonDelay=2,BigBossLifeSerial=0};
            target.ReceiveDamage(hit);target.ReceiveDamage(hit);Assert.True(target.BigBoss.Poisoned);
            var poisons=(System.Collections.IList)typeof(BigBossTank).GetField("_poisons",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target.BigBoss);Assert.AreEqual(1,poisons.Count);
            target.BigBoss.Heal(target.BigBoss,1,true);Assert.False(target.BigBoss.Poisoned);
        }
        [Test] public void ExplosionStaysAtActivationPointWhenHullMovesOrReturns()
        {
            Create(); var vfx=_session.Player.GetComponent<BigBossVfx>();
            Invoke(vfx,"Event","kamikazeAbility",_session.Player.transform.position);
            DavaFx burst=null;
            foreach(var fx in _session.GetComponentsInChildren<DavaFx>())
                if(fx.name=="DavaFx_Boss_mode_Kamikaze_Explosion")burst=fx;
            Assert.NotNull(burst);Assert.AreSame(_session.transform,burst.transform.parent);
            var position=burst.transform.position;
            _session.Player.transform.position+=new Vector3(180,0,-200);
            Assert.Less(Vector3.Distance(position,burst.transform.position),.0001f);
        }
        [Test] public void SourceFxScenesLoadWithoutInventedMissingTextureFallbacks()
        {
            var manifest=BlitzLegacy.Core.Serialization.JsonNode.Parse(BlitzResources.LoadText("big_boss_fx_1120"));
            Assert.AreEqual(15,manifest["effects"].Count);Assert.AreEqual(4,manifest["missing"].Count);
            foreach(var effect in manifest["effects"].Fields)
            {
                var name=effect.Value["resource"].AsString().Replace("BigBoss/Sources/","");var fx=DavaFx.CreateBigBoss(null,name,2);fx.Simulate(.1f);Object.DestroyImmediate(fx.gameObject);
            }
        }
        [Test] public void SourceRoleWidgetGeometryAndAbilityCardsBuildForEveryRole()
        {
            Create();var ui=_session.Hud.Root.GetComponentInChildren<BigBossBattleUi>();Assert.NotNull(ui);
            foreach(var role in new[]{1,2,4,5,6})
            {
                Role(_session.Player,role);Invoke(ui,"Refresh");
                var widget=(RectTransform)ui.transform.Find("RoleProgressBar");Assert.AreEqual(new Vector2(205,96),widget.sizeDelta);
                var icon=(RectTransform)widget.Find("RoleIcon/Icon");Assert.AreEqual(new Vector2(96,96),icon.sizeDelta);
            }
        }
        [Test] public void SideColumnShowsTheHangarLoadoutAndPutsTheRoleAbilityInTheFourthSlot()
        {
            Create(); var bar=_session.Controls.Consumables;
            Assert.True(bar.gameObject.activeInHierarchy);
            // The three hangar slots of every mode: the third one carries the adrenaline, not an empty cross.
            for(var slot=0;slot<Hangar.Length;slot++)
            {
                var icon=bar.GetIcon(slot); Assert.True(icon.gameObject.activeSelf,Hangar[slot]);
                Assert.AreEqual(Hangar[slot],icon.Consumable.Info.Id); Assert.True(icon.IconImage.enabled,Hangar[slot]);
                Assert.AreEqual("consumable_"+Hangar[slot],icon.IconImage.sprite.name); Assert.AreEqual("consumable_key_ready",icon.BackgroundImage.sprite.name);
            }
            Assert.Null(bar.Root.Find("RoleAbilities"));
            var mode=bar.ModeIcon; Assert.NotNull(mode); Assert.AreEqual("cons_jump",mode.name); Assert.AreSame(bar.Root,mode.transform.parent);
            var rect=(RectTransform)mode.transform; Assert.AreEqual(new Vector2(186,186),rect.sizeDelta);
            Assert.AreEqual(-3*BattleConsumableBar.SlotStep,rect.anchoredPosition.y,.01f);
            var ui=_session.Hud.Root.GetComponentInChildren<BigBossBattleUi>();
            foreach(var role in new[]{1,2,4,5,6})
            {
                Role(_session.Player,role); Invoke(ui,"Refresh"); Invoke(mode,"FixedUpdate");
                var c=_session.Player.BigBoss; var id=c.ManualAbility; Assert.NotNull(id,"role "+role);
                Assert.AreEqual("active",c.State.Rules.Ability(id)["kind"].AsString()); Assert.False(c.State.AutoActivated(id),id);
                Assert.True(mode.IconImage.enabled,id); Assert.NotNull(mode.IconImage.sprite,id);
                StringAssert.EndsWith(mode.IconImage.sprite.texture.name,c.State.Rules.Ability(id)["iconBig"].AsString());
                Assert.AreEqual(new Vector2(128,128),mode.IconImage.rectTransform.sizeDelta);
            }
            _session.SetCombatHudVisible(false); Assert.False(bar.gameObject.activeInHierarchy);
            _session.SetCombatHudVisible(true); Assert.True(bar.gameObject.activeInHierarchy);
        }
        [Test] public void MobileRoleAbilityUsesTheSameFourthSlotAsUprising()
        {
            _session=BattleSession.Create(null,true,tankId:"t-34",autoStart:true,mode:BattleMode.BigBoss);
            Role(_session.Player,1); var ui=_session.Hud.Root.GetComponentInChildren<BigBossBattleUi>(); Invoke(ui,"Refresh");
            var bar=_session.Controls.Consumables; var mode=(RectTransform)bar.ModeIcon.transform;
            var position=mode.anchoredPosition; var parent=bar.Root.anchoredPosition;
            Object.DestroyImmediate(_session.gameObject);
            _session=BattleSession.Create(null,true,autoStart:true,mode:BattleMode.Uprising);
            var uprising=_session.Controls.Consumables;
            Assert.AreEqual(((RectTransform)uprising.ModeIcon.transform).anchoredPosition,position);
            Assert.AreEqual(uprising.Root.anchoredPosition,parent);
        }
        [Test] public void SelfActivatingAbilitiesNeverNeedTheButton()
        {
            Create(); var tank=_session.Player; Role(tank,2); var c=tank.BigBoss;
            var aura=c.FamilyAbility("healingAura"); var shield=c.FamilyAbility("shieldGiverAbility");
            Assert.False(c.State.AutoActivated(aura)); Assert.AreEqual(aura,c.ManualAbility,"the aura is the healer's button");
            Assert.True(c.State.AutoActivated(shield)); Assert.AreNotEqual(shield,c.ManualAbility);
            tank.Health.Restore(tank.Health.MaxHealth/2); Invoke(c,"AutoAbilities");
            Assert.False(c.HealingAuraActive,"a wound does not fire the aura by itself"); Assert.False(c.ShieldArmed,"start cooldown");
            c.State.StartLife(Time.time-100); Invoke(c,"AutoAbilities");
            Assert.True(c.ShieldArmed,"the shield arms itself once ready"); Assert.False(c.HealingAuraActive);
            Assert.True(c.UseRoleAbility()); Assert.True(c.HealingAuraActive); Assert.Greater(c.State.Remaining(aura,Time.time),0);
            Assert.Greater(c.RunningSeconds("healingAura"),0);
            Role(tank,1); c.State.StartLife(Time.time-100);
            Invoke(c,"AutoAbilities"); Assert.True(c.RailgunArmed);
            Assert.Zero(c.State.Remaining("gravityBurst",Time.time),"button ability untouched");
            var shot=c.Launch(tank.CurrentShell()); Assert.True(shot.Railgun); Assert.False(c.RailgunArmed);
            Assert.AreEqual(c.State.Rules.Number("railgunCharger","cooldown"),c.State.Remaining("railgunCharger",Time.time),.01f);
            Assert.True(c.UseRoleAbility()); Assert.Greater(c.State.Remaining("gravityBurst",Time.time),0);
        }
        [Test] public void StatusRowShowsSelfActivatingAndRunningAbilitiesOnly()
        {
            Create(); var tank=_session.Player; var c=tank.BigBoss; var row=_session.Hud.StatusRow;
            Role(tank,2); row.Refresh();
            CollectionAssert.AreEqual(new[]{c.FamilyAbility("shieldGiverAbility")},row.Ids,"the shield sits in the strip, the aura on the button");
            Assert.AreSame(row.transform,_session.Hud.Root.Find("ability_status")); Assert.True(row.gameObject.activeInHierarchy); Assert.AreEqual(1,row.transform.childCount);
            Assert.NotNull(row.transform.Find("status0/icon").GetComponent<UnityEngine.UI.Image>().sprite);
            Assert.True(c.UseRoleAbility()); row.Refresh();
            CollectionAssert.AreEqual(new[]{c.FamilyAbility("healingAura"),c.FamilyAbility("shieldGiverAbility")},row.Ids,"a running aura joins it");
            Role(tank,6); row.Refresh(); CollectionAssert.IsEmpty(row.Ids); Assert.False(row.gameObject.activeSelf);
            Assert.True(c.UseRoleAbility()); row.Refresh();
            CollectionAssert.AreEqual(new[]{c.FamilyAbility("gravityShot")},row.Ids);
            Assert.AreEqual(c.State.Rules.Number(c.FamilyAbility("gravityShot"),"effectDuration"),c.RunningSeconds("gravityShot"),.05f);
            typeof(BigBossTank).GetProperty("ShieldUntil").SetValue(c,Time.time+5);typeof(BigBossTank).GetProperty("ShieldDuration").SetValue(c,10f);
            row.Refresh(); CollectionAssert.Contains(row.Ids,BigBossHudMode.ShieldStatus);
            Kill(tank); row.Refresh(); CollectionAssert.IsEmpty(row.Ids);
        }
        [Test] public void RespawnScreenPresentsTheNextRoleAndItsAbilities()
        {
            Create(); var tank=_session.Player; Role(tank,4); var c=tank.BigBoss; var ui=_session.Hud.Root.GetComponentInChildren<BigBossBattleUi>();
            Kill(tank); var next=c.State.NextRoleId; Assert.GreaterOrEqual(next,0); Assert.AreNotEqual(4,next); Assert.False(c.State.Rules.IsBoss(next));
            Invoke(ui,"Refresh");
            var respawn=ui.transform.Find("RespawnTimer"); Assert.True(respawn.gameObject.activeInHierarchy);
            var lang=BlitzLegacy.Core.Localization.LocalizationTable.Parse(BlitzResources.LoadText("lang"));lang.Language=BlitzLegacy.Runtime.BlitzBootstrap.Current?.Settings.Language??"en";
            Assert.AreEqual(lang.Get(c.State.Rules.Role(next)["nameKey"].AsString()),respawn.Find("content/RoleName").GetComponent<UnityEngine.UI.Text>().text);
            Assert.AreEqual(lang.Get("#respawn/awaitReturnToBattleText"),respawn.Find("content/RespawnText").GetComponent<UnityEngine.UI.Text>().text);
            Assert.AreEqual(c.State.Rules.Abilities(next,c.State.Rules.Level(c.State.Points)).Length,respawn.Find("content/Abilities").childCount);
            Assert.NotNull(respawn.Find("content/RoleIcon").GetComponent<UnityEngine.UI.Image>().sprite);
            Return(c); Assert.AreEqual(next,c.State.RoleId); Assert.AreEqual(-1,c.State.NextRoleId);
            Invoke(ui,"Refresh"); Assert.False(respawn.gameObject.activeSelf);
        }
        [Test] public void BossPoolIsFiveTankPoolsAndPlatesFollowTheLivePool()
        {
            Create(); BattleTank boss=null; foreach(var tank in _session.Enemies) if(tank.BigBoss.State.Boss) boss=tank;
            Assert.NotNull(boss); var pool=(int)typeof(BigBossTank).GetField("_maxHp",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(boss.BigBoss);
            Assert.AreEqual(pool*5,boss.Health.MaxHealth); Assert.AreEqual(boss.Health.MaxHealth,boss.Health.Health);
            BattleTankPlate plate=null; for(var i=0;i<_session.Hud.TankPlateCount;i++) if(_session.Hud.GetTankPlate(i).Tank==boss) plate=_session.Hud.GetTankPlate(i);
            Assert.NotNull(plate); plate.AdvanceAnimations(.1f,Time.unscaledTime);
            Assert.AreEqual(boss.Health.Health+"/"+boss.Health.MaxHealth,plate.HealthLabel.text); Assert.AreEqual(1,plate.TopBar.fillAmount,.0001f);
            boss.ReceiveDamage(new DamageInfo{Dmg=pool,DmgBase=pool,DmgType=DamageType.Ability});
            Assert.AreEqual(.8f,plate.TopBar.fillAmount,.01f); Assert.False(boss.Dead);
            Kill(boss); Invoke(boss.BigBoss,"ApplyModifiers",false); Assert.True(boss.Dead); Assert.AreEqual(0,boss.Health.Health);
        }
        [Test] public void PlateNumeralKeepsItsAtlasSizeWithTheRoleMarkerBeneathIt()
        {
            Create(); Canvas.ForceUpdateCanvases();
            for(var i=0;i<_session.Hud.TankPlateCount;i++)
            {
                var plate=_session.Hud.GetTankPlate(i); var level=plate.LevelIcon; Assert.NotNull(level.sprite);
                Assert.AreEqual(level.sprite.rect.size.x*.9f,level.rectTransform.rect.width,.01f); Assert.AreEqual(level.sprite.rect.size.y*.9f,level.rectTransform.rect.height,.01f);
                Assert.AreEqual(Vector3.one,level.rectTransform.localScale); Assert.False(level.preserveAspect);
                var marker=(RectTransform)plate.Root.Find("BigBossRole"); Assert.NotNull(marker);
                var l=Bounds(plate.Root,level.rectTransform); var m=Bounds(plate.Root,marker); var name=Bounds(plate.Root,plate.NameLabel.rectTransform);
                Assert.LessOrEqual(m.yMax,l.yMin+.01f,"marker under the numeral"); Assert.GreaterOrEqual(m.yMin,name.yMax-4,"marker above the name");
                Assert.AreEqual(l.center.x,m.center.x,.5f);
            }
        }
        private static Rect Bounds(Transform space,RectTransform rect)
        {
            var corners=new Vector3[4]; rect.GetWorldCorners(corners); var a=space.InverseTransformPoint(corners[0]); var b=space.InverseTransformPoint(corners[2]);
            return Rect.MinMaxRect(Mathf.Min(a.x,b.x),Mathf.Min(a.y,b.y),Mathf.Max(a.x,b.x),Mathf.Max(a.y,b.y));
        }
        [Test] public void SupportBoostHasIndependentCooldownAndResetsOnReturn()
        {
            Create(); Role(_session.Player,2); var tank=_session.Player; var bar=_session.Controls.Consumables;
            var power=tank.Suspension.EnginePower; var speed=tank.Suspension.MaxSpeedForward;
            var boost=tank.ActiveConsumables[1];
            bar.Use(1); Assert.GreaterOrEqual(boost.LastUse,0);
            Assert.AreEqual(power*1.25f,tank.Suspension.EnginePower,1);
            var used=boost.LastUse; bar.Use(1); Assert.AreEqual(used,boost.LastUse);
            Assert.True(bar.GetIcon(1).TimerLabel.gameObject.activeSelf);
            Invoke(tank.BigBoss,"ApplyModifiers",false);
            Assert.AreEqual(power*1.25f,tank.Suspension.EnginePower,1);
            Kill(tank); Assert.Less(boost.LastUse,0); Return(tank.BigBoss); Role(tank,2);
            Assert.AreEqual(power,tank.Suspension.EnginePower,1);
            Assert.AreEqual(speed,tank.Suspension.MaxSpeedForward,1);
            Assert.False(bar.GetIcon(1).TimerLabel.gameObject.activeSelf);
            Assert.True(bar.gameObject.activeInHierarchy);
            bar.Use(1); Assert.GreaterOrEqual(boost.LastUse,0);
        }
        [Test] public void PassiveHangarItemsKeepTheirBonusThroughEveryRoleAndReturn()
        {
            Create(); Role(_session.Player,2); var plain=_session.Player.Suspension.EnginePower; Object.DestroyImmediate(_session.gameObject);
            Create(ConsumableCatalog.Fuel,ConsumableCatalog.RepairKit); var tank=_session.Player; Role(tank,2);
            Assert.True(tank.ActiveConsumables[0].Passive);
            Assert.AreEqual(plain*1.1f,tank.Suspension.EnginePower,2,"improved fuel survives the role modifiers");
            Kill(tank); Return(tank.BigBoss); Role(tank,2);
            Assert.AreEqual(plain*1.1f,tank.Suspension.EnginePower,2,"and the return");
            tank.UseConsumable(0);
            Assert.AreEqual(plain*1.1f,tank.Suspension.EnginePower,2,"a press after the return never applies it twice");
        }
        [Test] public void SupportInputsDoNotActivateRoleAndPausedBattleRejectsBoth()
        {
            Create(); Role(_session.Player,6); var c=_session.Player.BigBoss; var bar=_session.Controls.Consumables;
            var id=c.FamilyAbility("gravityShot"); Assert.NotNull(id);
            _session.Player.DamageModule(TankModuleIds.TrackLeft,100000);
            Assert.True(_session.Player.TrackCrashed(TrackSide.Left));
            bar.Use(0); Assert.False(_session.Player.TrackCrashed(TrackSide.Left));
            Assert.GreaterOrEqual(_session.Player.ActiveConsumables[0].LastUse,0);
            bar.Use(1); Assert.Zero(c.State.Remaining(id,Time.time));
            bar.Use(2); Assert.GreaterOrEqual(_session.Player.ActiveConsumables[2].LastUse,0,"the third key spends the hangar adrenaline");
            Assert.Zero(c.State.Remaining(id,Time.time),"and never the role ability");
            Time.timeScale=0; var used=_session.Player.ActiveConsumables[1].LastUse;
            bar.Use(1); bar.UseModeAbility(); Assert.AreEqual(used,_session.Player.ActiveConsumables[1].LastUse);
            Assert.Zero(c.State.Remaining(id,Time.time));
            Time.timeScale=1; bar.UseModeAbility(); Assert.Greater(c.State.Remaining(id,Time.time),0);
        }

        [Test] public void EveryActiveRoleAbilityRejectsRepeatedActivation()
        {
            Create();foreach(var role in new[]{1,2,4,5,6})
            {
                Role(_session.Player,role);var c=_session.Player.BigBoss;var ids=c.State.Rules.Abilities(role,1);
                foreach(var id in ids) if(c.State.Rules.Ability(id)["kind"].AsString()=="active")
                {
                    if(c.State.Remaining(id,Time.time)>0)continue;
                    Assert.True(c.Activate(id),id);Assert.False(c.Activate(id),id+" cooldown");
                }
            }
        }

        /// <summary>Moves <paramref name="tank"/> to a quiet corner far from both spawns.</summary>
        private static void Place(BattleTank tank,Vector3 point) { tank.transform.position=point; Physics.SyncTransforms(); }
        private BattleTank Ordinary(List<BattleTank> side,params BattleTank[] except)
        { return side.Find(t=>t!=_session.Player&&!t.BigBoss.State.Boss&&Array.IndexOf(except,t)<0); }
        [Test] public void HealingShellMendsOnEveryFriendlyHitAndTheArmedShieldGoesToOneAlly()
        {
            Create(); var healer=_session.Player; Role(healer,2); var c=healer.BigBoss; var shell=healer.CurrentShell();
            var first=Ordinary(_session.Allies); var second=Ordinary(_session.Allies,first); Role(first,4); Role(second,4);
            first.Health.ReceiveDamageBasic(300); var before=first.Health.Health;
            var plain=c.Launch(shell); Assert.Zero(plain.Shield);
            Assert.True(plain.Friendly(healer,first,shell)); Assert.Greater(first.Health.Health,before,"no shield needed to heal");
            Assert.LessOrEqual(first.BigBoss.ShieldUntil,Time.time);
            c.State.StartLife(Time.time-100); Invoke(c,"AutoAbilities"); Assert.True(c.ShieldArmed);
            var id=c.FamilyAbility("shieldGiverAbility");
            var armed=c.Launch(shell); Assert.AreEqual(c.State.Rules.Number(id,"shieldDuration"),armed.Shield,.001f);
            Assert.True(c.ShieldArmed,"launching keeps the charge until a shell lands on an ally");
            Assert.False(armed.Friendly(healer,_session.Enemies[0],shell)); Assert.True(c.ShieldArmed,"an enemy hit keeps it");
            first.Health.ReceiveDamageBasic(300); before=first.Health.Health;
            Assert.True(armed.Friendly(healer,first,shell)); Assert.Greater(first.Health.Health,before,"the armed shell still mends");
            Assert.False(c.ShieldArmed); Assert.AreEqual(Time.time+armed.Shield,first.BigBoss.ShieldUntil,.01f); Assert.AreEqual(armed.Shield,first.BigBoss.ShieldDuration,.001f);
            Assert.AreEqual(c.State.Rules.Number(id,"cooldown"),c.State.Remaining(id,Time.time),.05f,"the cooldown restarts from the hand-over");
            second.Health.ReceiveDamageBasic(300); before=second.Health.Health;
            Assert.True(armed.Friendly(healer,second,shell)); Assert.Greater(second.Health.Health,before,"healing has no cooldown of its own");
            Assert.LessOrEqual(second.BigBoss.ShieldUntil,Time.time,"one charge shields one ally");
        }
        [Test] public void ShieldHandOverRejectsAnotherLifeItselfAndWrecks()
        {
            Create(); var healer=_session.Player; Role(healer,2); var c=healer.BigBoss; var ally=Ordinary(_session.Allies); Role(ally,4);
            c.State.StartLife(Time.time-100); Invoke(c,"AutoAbilities"); Assert.True(c.ShieldArmed);
            Assert.False(c.GrantShield(ally.BigBoss,10,c.State.LifeSerial+1),"a shell fired in another life");
            Assert.False(c.GrantShield(c,10,c.State.LifeSerial),"never itself");
            Assert.False(c.GrantShield(ally.BigBoss,0,c.State.LifeSerial));
            Kill(ally); Assert.False(c.GrantShield(ally.BigBoss,10,c.State.LifeSerial),"not a wreck");
            Assert.True(c.ShieldArmed,"every rejected hand-over keeps the charge");
        }
        [Test] public void ReturningBotRestartsItsBrainFromTheNewSpawn()
        {
            Create(); var bot=Ordinary(_session.Enemies); var ai=bot.GetComponent<AiTankController>(); Assert.NotNull(ai); Assert.True(ai.Active);
            Kill(bot); Assert.False(ai.Active,"a wreck does not think");
            Return(bot.BigBoss);
            Assert.False(bot.Dead); Assert.True(ai.Active,"a returning bot drives again"); Assert.NotNull(ai.CurrentState);
            Assert.AreEqual(bot.Pos(),ai.Spawnpoint); Assert.False(bot.Blocked);
        }
        [Test] public void HealerBotMendsWoundedAlliesAndOtherwiseKeepsToItsBoss()
        {
            Create(); var team=_session.Enemies; var boss=team.Find(t=>t.BigBoss.State.Boss); var healer=Ordinary(team); var patient=Ordinary(team,healer);
            Role(healer,2); Role(patient,4); var ai=healer.GetComponent<AiTankController>(); Assert.AreEqual(AiTankController.HealerRole,ai.ModeRole);
            var spot=new Vector3(4000,0,4000); Place(healer,spot); Place(patient,spot+Vector3.right*30); Place(boss,spot+Vector3.forward*120);
            Assert.AreSame(boss,ai.EscortAnchor()); Assert.Null(ai.HealTarget(),"everyone is whole");
            ai.TrySwitchingToRelevantState(); Assert.AreEqual(AiStateKind.Escort,ai.CurrentStateKind,"a healer keeps to its boss");
            patient.Health.Restore(Mathf.RoundToInt(patient.Health.MaxHealth*.7f)); Assert.AreSame(patient,ai.HealTarget(),"a close ally below three quarters");
            boss.Health.Restore(Mathf.RoundToInt(boss.Health.MaxHealth*.5f)); Assert.AreSame(boss,ai.HealTarget(),"the boss counts double");
            Place(patient,spot+Vector3.right*400); boss.Health.Restore(boss.Health.MaxHealth); Assert.Null(ai.HealTarget(),"out of reach");
            Kill(boss); Assert.Null(ai.EscortAnchor(),"a healer without a boss fights on its own");
        }
        [Test] public void BomberChargesOnlyWithItsBlastReadyAndBreaksAwayOnceSpent()
        {
            Create(); var bomber=Ordinary(_session.Enemies); Role(bomber,5); var c=bomber.BigBoss; var ai=bomber.GetComponent<AiTankController>();
            var victim=Ordinary(_session.Allies); var spot=new Vector3(-4000,0,4000); Place(bomber,spot); Place(victim,spot+Vector3.forward*60); victim.SetSpotted(true);
            var blast=c.FamilyAbility("kamikazeAbility"); Assert.AreEqual(blast,c.ManualAbility);
            Assert.AreSame(victim,ai.RushTarget()); ai.TrySwitchingToRelevantState(); Assert.AreEqual(AiStateKind.Rush,ai.CurrentStateKind);
            Assert.False(c.BotWantsAbility("kamikazeAbility",blast),"60 m is outside the blast");
            Place(victim,spot+Vector3.forward*30); Assert.True(c.BotWantsAbility("kamikazeAbility",blast));
            Invoke(c,"BotAbilities"); Assert.Greater(c.State.Remaining(blast,Time.time),0,"the bot sets it off"); Assert.Less(victim.Health.Health,victim.Health.MaxHealth);
            Assert.Null(ai.RushTarget(),"a spent bomber has nothing to charge with");
            ai.TrySwitchingToRelevantState(); Assert.AreEqual(AiStateKind.Withdraw,ai.CurrentStateKind,"and breaks away");
        }
        [Test] public void IntoxicatorWeighsOnlyABossInSightAndFightsFromCoverOnlyWithALineOfFire()
        {
            Create(); var killer=Ordinary(_session.Enemies); Role(killer,4); var ai=killer.GetComponent<AiTankController>(); Assert.True(ai.CoverFighter);
            var boss=_session.Allies.Find(t=>t.BigBoss.State.Boss); var spot=new Vector3(4000,0,-4000);
            Place(killer,spot); Place(boss,spot+Vector3.forward*150); boss.SetSpotted(true); ai.TargetTank=null;
            Assert.True(ai.TankCheckVisibility(boss,true),"open ground"); var seen=SearchRating(ai,boss);
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube); wall.layer=BattleLayers.Static; var clock=BattleGun.Clock; BattleGun.Clock=()=>1000f;
            try
            {
                wall.transform.position=spot+new Vector3(0,10,30); wall.transform.localScale=new Vector3(80,40,2); Physics.SyncTransforms();
                Assert.False(ai.TankCheckVisibility(boss,true),"the wall hides the boss");
                Assert.Greater(seen-SearchRating(ai,boss),1f,"a boss behind a wall loses the mode preference, even in a search that skips the sight check");
                ai.TargetTank=boss; var gun=killer.Gun; var lastShot=typeof(BattleGun).GetProperty("LastShot");
                Assert.False(LineOfFire(ai),"no line and no shot: cover would only hide a gun that never fires");
                lastShot.SetValue(gun,1000f); Assert.True(LineOfFire(ai),"a shot a moment ago vouches for the peek spot it came from");
                lastShot.SetValue(gun,940f); Assert.False(LineOfFire(ai),"a minute-old one does not");
            }
            finally { BattleGun.Clock=clock; Object.DestroyImmediate(wall); }
        }
        [Test] public void BlindEscortLeavesItsStationToFindALineAndReturnsOnceItHasFired()
        {
            Create(); var team=_session.Enemies; var boss=team.Find(t=>t.BigBoss.State.Boss); var escort=Ordinary(team); Role(escort,6); var ai=escort.GetComponent<AiTankController>();
            var foe=Ordinary(_session.Allies); var spot=new Vector3(-4000,0,-4000); Place(escort,spot); Place(boss,spot+Vector3.forward*20); Place(foe,spot+Vector3.forward*150); foe.SetSpotted(true);
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube); wall.layer=BattleLayers.Static; var clock=BattleGun.Clock; BattleGun.Clock=()=>1000f;
            try
            {
                wall.transform.position=spot+new Vector3(0,10,60); wall.transform.localScale=new Vector3(80,40,2); Physics.SyncTransforms();
                ai.TargetTank=null; ai.TrySwitchingToRelevantState(); Assert.AreEqual(AiStateKind.Escort,ai.CurrentStateKind,"no target yet: the graviton keeps to its boss");
                ai.TargetTank=foe; ai.TrySwitchingToRelevantState(); Assert.AreEqual(AiStateKind.Escort,ai.CurrentStateKind,"a blind station is kept for a while");
                typeof(AiTankController).GetField("_stateEnteredAt",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(ai,Time.time-AiTankController.EscortBlindSeconds-1f);
                ai.TrySwitchingToRelevantState(); Assert.AreNotEqual(AiStateKind.Escort,ai.CurrentStateKind,"but not for good: it goes and finds a line");
                ai.TargetTank=foe; typeof(BattleGun).GetProperty("LastShot").SetValue(escort.Gun,1000f);
                ai.TrySwitchingToRelevantState(); Assert.AreEqual(AiStateKind.Escort,ai.CurrentStateKind,"and comes back once it has fired");
            }
            finally { BattleGun.Clock=clock; Object.DestroyImmediate(wall); }
        }
        private static float SearchRating(AiTankController ai,BattleTank t) { return (float)Invoke(ai,"SearchRating",t,true,false); }
        private static bool LineOfFire(AiTankController ai) { return (bool)Invoke(ai,"HasLineOfFire"); }
        [Test] public void BotCrewsSpendTheButtonAbilityOnlyWhereItPays()
        {
            Create(); var team=_session.Enemies; var boss=team.Find(t=>t.BigBoss.State.Boss); var c=boss.BigBoss; var foe=Ordinary(_session.Allies);
            var burst=c.FamilyAbility("gravityBurst"); Assert.AreEqual(burst,c.ManualAbility);
            var spot=new Vector3(4000,0,-4000); Place(boss,spot); Place(foe,spot+Vector3.right*120); foe.SetSpotted(true);
            Assert.False(c.BotWantsAbility("gravityBurst",burst),"nobody in reach");
            Place(foe,spot+Vector3.right*30); Assert.True(c.BotWantsAbility("gravityBurst",burst));
            foe.SetSpotted(false); Assert.False(c.BotWantsAbility("gravityBurst",burst),"an unseen enemy is no reason");
            foe.SetSpotted(true); Invoke(c,"BotAbilities"); Assert.Greater(c.State.Remaining(burst,Time.time),0);
            var healer=Ordinary(team); var ally=Ordinary(team,healer); Role(healer,2); Role(ally,4); var h=healer.BigBoss; var aura=h.FamilyAbility("healingAura");
            var camp=new Vector3(-4000,0,-4000); Place(healer,camp); Place(ally,camp+Vector3.forward*20);
            Assert.False(h.BotWantsAbility("healingAura",aura),"everyone is whole");
            healer.Health.Restore(Mathf.RoundToInt(healer.Health.MaxHealth*.8f)); Assert.False(h.BotWantsAbility("healingAura",aura),"a scratch on the healer");
            healer.Health.Restore(Mathf.RoundToInt(healer.Health.MaxHealth*.6f)); Assert.True(h.BotWantsAbility("healingAura",aura),"the healer itself is hurt");
            healer.Health.Restore(healer.Health.MaxHealth); ally.Health.Restore(Mathf.RoundToInt(ally.Health.MaxHealth*.8f));
            Assert.True(h.BotWantsAbility("healingAura",aura),"an ally in the aura below 85 %");
            Place(ally,camp+Vector3.forward*(h.State.Rules.Number(aura,"range")+20)); Assert.False(h.BotWantsAbility("healingAura",aura),"outside the aura");
            Place(ally,camp+Vector3.forward*20); Invoke(h,"BotAbilities"); Assert.True(h.HealingAuraActive);
            Kill(healer); Assert.False(h.BotWantsAbility("unknownFamily",aura));
        }
    }
}
