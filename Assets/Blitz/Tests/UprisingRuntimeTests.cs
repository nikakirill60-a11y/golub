using System.Reflection;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using BlitzLegacy.Runtime.Ui;
using BlitzLegacy.Runtime.Platform;

namespace BlitzLegacy.Tests
{
    public sealed class UprisingRuntimeTests
    {
        private BattleSession _session;
        [TearDown] public void Cleanup() { if (_session != null) Object.DestroyImmediate(_session.gameObject); }
        private void Create() { _session=BattleSession.Create(null,false,autoStart:true,mode:BattleMode.Uprising); }
        private static void Kill(BattleTank tank) { for(var i=0;i<2 && !tank.Dead;i++)tank.ReceiveDamage(new DamageInfo { Dmg=tank.Health.MaxHealth*10,DmgBase=tank.Health.MaxHealth*10,DmgType=DamageType.Ability }); Assert.IsTrue(tank.Dead); }
        private static void Revive(UprisingTank c)
        {
            Assert.IsTrue(c.State.Respawn(Time.time+11,true));
            typeof(UprisingTank).GetMethod("Restore",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(c,null);
        }
        [Test] public void DeathDoesNotEliminateTeamWithPendingLives()
        {
            Create();foreach(var tank in _session.Enemies)Kill(tank);
            Assert.IsTrue(_session.Started);Assert.AreEqual(_session.Enemies.Count,_session.Roster.TeamAScore);
            foreach(var tank in _session.Enemies)Assert.IsTrue(tank.GetComponent<UprisingTank>().State.Respawning);
        }
        [Test] public void ThreeDeathsCountAsThreeKillsAndFinalLifeEliminates()
        {
            Create();for(var life=0;life<3;life++)
            {
                foreach(var tank in _session.Enemies)Kill(tank);
                if(life<2)foreach(var tank in _session.Enemies)Revive(tank.GetComponent<UprisingTank>());
            }
            Assert.AreEqual(3*_session.Enemies.Count,_session.Roster.TeamAScore);Assert.IsFalse(_session.Started);Assert.AreEqual(0,_session.Roster.WinnerTeam);
        }
        [Test] public void RespawnRestoresFullHealthAmmoModulesAndPlayerCamera()
        {
            Create();var player=_session.Player;var c=player.GetComponent<UprisingTank>();var ammo=new System.Collections.Generic.List<int>(player.ShellAmounts);
            player.SetShellAmounts(new[]{0,0,0});Kill(player);Revive(c);
            Assert.IsFalse(player.Dead);Assert.AreEqual(player.Health.MaxHealth,player.Health.Health);Assert.IsFalse(player.Blocked);Assert.IsFalse(player.TurretBlock);
            CollectionAssert.AreEqual(ammo,player.ShellAmounts);Assert.AreSame(player,_session.View.Tank);Assert.AreEqual(1,c.State.RespawnsLeft);
        }
        [Test] public void CombatMediationAppliesShieldAndDamageBeforeHealthEvents()
        {
            Create();var player=_session.Player;var enemy=_session.Enemies[0];var attacker=player.GetComponent<UprisingTank>();var recipient=enemy.GetComponent<UprisingTank>();
            attacker.State.Grant("damageBoost2");recipient.State.ShieldArmed=true;recipient.State.ActiveUntil=Time.time+10;
            var hp=enemy.Health.Health;enemy.ReceiveDamage(new DamageInfo {AttackerTank=player,Dmg=100,DmgBase=100,DmgType=DamageType.Shell,ShotId=1});Assert.AreEqual(hp,enemy.Health.Health);
            enemy.ReceiveDamage(new DamageInfo {AttackerTank=player,Dmg=100,DmgBase=100,DmgType=DamageType.Shell,ShotId=2});Assert.AreEqual(hp-140,enemy.Health.Health);Assert.AreEqual(7,attacker.State.Elixir);
        }
        [TestCase("moonwalk")][TestCase("returnInTime")][TestCase("pretendDead")][TestCase("vortex")]
        [TestCase("berserk")][TestCase("huntersMark")][TestCase("curse")]
        public void AllSevenActiveAbilitiesExecuteAndEnforceCooldown(string id)
        {
            var scale=Time.timeScale;Time.timeScale=1;
            try {
                Create();var player=_session.Player;var controller=player.GetComponent<UprisingTank>();
                typeof(UprisingTank).GetField("<State>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(controller,new UprisingState(controller.State.Catalog,id));
                var enemy=_session.Enemies[0];enemy.SetSpotted(true);
                typeof(BattleTank).GetProperty("GunTraceTank").SetValue(player,enemy);
                if(id=="returnInTime")typeof(UprisingTank).GetMethod("FixedUpdate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(controller,null);
                Assert.IsTrue(controller.UseActive(),id);Assert.Greater(controller.State.ReadyAt,Time.time);Assert.IsFalse(controller.UseActive(),"Cooldown must reject a second press");
                if(id=="huntersMark" || id=="curse")Assert.IsTrue(enemy.GetComponent<UprisingTank>().HasStatus(id));
                if(id=="pretendDead")Assert.IsTrue(controller.State.ShieldArmed);
            } finally {Time.timeScale=scale;}
        }
        [Test] public void BotSchedulesTimeRewindTwoTenthsToOneSecondAfterHeavyDamage()
        {
            var scale=Time.timeScale;Time.timeScale=1;
            try
            {
                Create();var enemy=_session.Enemies[0];var c=enemy.GetComponent<UprisingTank>();
                typeof(UprisingTank).GetField("<State>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(c,new UprisingState(c.State.Catalog,"returnInTime"));
                var before=Time.time;enemy.ReceiveDamage(new DamageInfo {AttackerTank=_session.Player,Dmg=Mathf.CeilToInt(enemy.Health.MaxHealth*.2f),DmgBase=Mathf.CeilToInt(enemy.Health.MaxHealth*.2f),DmgType=DamageType.Shell,ShotId=909});
                var useAt=(float)typeof(UprisingTank).GetField("_rewindUseAt",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(c);
                Assert.GreaterOrEqual(useAt-before,.2f);Assert.LessOrEqual(useAt-before,1.01f);Assert.IsFalse(c.State.Running(Time.time));
            }
            finally {Time.timeScale=scale;}
        }
        [Test] public void ActiveThanatosCannotBeCapturedByAutoAim()
        {
            Create();var enemy=_session.Enemies[0];var c=enemy.GetComponent<UprisingTank>();enemy.SetSpotted(true);
            typeof(UprisingTank).GetField("<State>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(c,new UprisingState(c.State.Catalog,"pretendDead"));
            c.State.ActiveUntil=Time.time+10;
            Assert.IsFalse(_session.Player.AutoAim.CanCapture(enemy,true));Assert.IsFalse(UprisingTank.IsTargetable(enemy));
            c.State.ActiveUntil=-1;Assert.IsTrue(_session.Player.AutoAim.CanCapture(enemy,true));
        }
        [Test] public void PlayerHudKeepsRealHpWhileThanatosIsActive()
        {
            Create();var player=_session.Player;var c=player.GetComponent<UprisingTank>();
            player.Health.ReceiveDamageBasic(40);
            typeof(UprisingTank).GetField("<State>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(c,new UprisingState(c.State.Catalog,"pretendDead"));
            c.State.ActiveUntil=Time.time+10;
            typeof(BattleHud).GetMethod("RefreshCombatReadouts",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(_session.Hud,null);
            Assert.AreEqual(player.Health.Health,player.DisplayHealth);Assert.AreEqual("160 / 200",_session.Hud.HealthLabel.text);
        }
        [Test] public void BotsKnowEarlyThanatosBeforeAnyLivesAreLost()
        {
            Create();var player=_session.Player;var c=player.GetComponent<UprisingTank>();var enemy=_session.Enemies[0];var ai=enemy.GetComponent<AiTankController>();
            player.SetSpotted(true);player.transform.position=enemy.transform.position+enemy.transform.forward*20f;Physics.SyncTransforms();
            typeof(UprisingTank).GetField("<State>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(c,new UprisingState(c.State.Catalog,"pretendDead"));
            c.State.ActiveUntil=Time.time+10;
            Assert.AreEqual(0,_session.Roster.TeamAScore+_session.Roster.TeamBScore);
            Assert.IsTrue(UprisingTank.IsTargetable(player,enemy));
            ai.TargetTank=player;Assert.IsTrue(ai.EnemyTargetIsValid());
        }
        [Test] public void BotsIgnoreLateThanatosUntilVisibleActionRevealsIt()
        {
            Create();var player=_session.Player;var c=player.GetComponent<UprisingTank>();var enemy=_session.Enemies[0];var ai=enemy.GetComponent<AiTankController>();
            var victim=_session.Enemies.Count>1?_session.Enemies[1]:_session.Enemies[0];Kill(victim);
            player.SetSpotted(true);player.transform.position=enemy.transform.position+enemy.transform.forward*20f;Physics.SyncTransforms();
            typeof(UprisingTank).GetField("<State>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(c,new UprisingState(c.State.Catalog,"pretendDead"));
            c.State.ActiveUntil=Time.time+10;
            Assert.Greater(_session.Roster.TeamAScore+_session.Roster.TeamBScore,0);
            Assert.IsFalse(UprisingTank.IsTargetable(player,enemy));
            ai.TargetTank=player;Assert.IsFalse(ai.EnemyTargetIsValid());
            typeof(UprisingTank).GetMethod("OnShot",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(c,new object[]{true,0});
            Assert.IsTrue(UprisingTank.IsTargetable(player,enemy));
            ai.TargetTank=player;Assert.IsTrue(ai.EnemyTargetIsValid());
        }
        [Test] public void ThanatosMotionRevealUsesOwnControlsNotExternalPushes()
        {
            Create();var player=_session.Player;var c=player.GetComponent<UprisingTank>();var enemy=_session.Enemies[0];
            var victim=_session.Enemies.Count>1?_session.Enemies[1]:_session.Enemies[0];Kill(victim);
            player.SetSpotted(true);player.transform.position=enemy.transform.position+enemy.transform.forward*20f;Physics.SyncTransforms();
            typeof(UprisingTank).GetField("<State>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(c,new UprisingState(c.State.Catalog,"pretendDead"));
            c.State.ActiveUntil=Time.time+10;
            player.GetComponent<Rigidbody>().linearVelocity=player.transform.forward*12f;
            typeof(UprisingTank).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(c,null);
            Assert.IsFalse(UprisingTank.IsTargetable(player,enemy));
            player.Drive(1f,0f,false);
            typeof(UprisingTank).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(c,null);
            Assert.IsTrue(UprisingTank.IsTargetable(player,enemy));
        }
        [Test] public void RespawningPlayerCannotCycleSpectatorButFinalDeathCan()
        {
            Create();var player=_session.Player;var c=player.GetComponent<UprisingTank>();
            for(var life=0;life<2;life++)
            {
                Kill(player);Assert.IsFalse(_session.Controls.CanSpectate);
                _session.Controls.CycleSpectator(1);Assert.AreSame(player,_session.View.Tank);
                Revive(c);
            }
            Kill(player);Assert.IsTrue(_session.Controls.CanSpectate);
            _session.Controls.CycleSpectator(1);Assert.AreNotSame(player,_session.View.Tank);
        }
        [Test] public void PerkPointerClickPurchasesWithoutMovingCameraAndReleasesLogicalMouseLock()
        {
            var captured=MouseLock.Captured;GameObject events=null;
            try
            {
                Create();MouseLock.Set(true);Kill(_session.Player);
                var c=_session.Player.GetComponent<UprisingTank>();c.State.AddElixir(1000);
                var ui=_session.GetComponentInChildren<UprisingBattleUi>(true);
                typeof(UprisingBattleUi).GetMethod("Refresh",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(ui,null);Assert.AreEqual(0,ui.OfferAlpha);Assert.IsFalse(MouseLock.Captured);
                typeof(UprisingState).GetProperty("RespawnAt").SetValue(c.State,Time.time+UprisingState.RespawnDelay-1.3);
                typeof(UprisingBattleUi).GetMethod("Refresh",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(ui,null);Assert.AreEqual(1,ui.OfferAlpha);
                var card=ui.transform.Find("respawn/content/ability_panel/card1").GetComponent<Button>();Assert.IsTrue(card.interactable);
                if(EventSystem.current==null)events=new GameObject("events",typeof(EventSystem));
                var pointer=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left};
                ExecuteEvents.Execute(card.gameObject,pointer,ExecuteEvents.pointerClickHandler);
                _session.Controls.CycleSpectator(1);
                Assert.IsTrue(c.State.Pending.ContainsKey(c.State.Offer[0]));Assert.AreSame(_session.Player,_session.View.Tank);
                Revive(c);typeof(UprisingBattleUi).GetMethod("Refresh",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(ui,null);Assert.IsTrue(MouseLock.Captured);
            }
            finally{if(events!=null)Object.DestroyImmediate(events);MouseLock.Set(captured);}
        }
        [Test] public void PerkModalOwnsRaycasterAboveControlsAndBelowPause()
        {
            Create();Kill(_session.Player);
            var ui=_session.GetComponentInChildren<UprisingBattleUi>(true);
            var modal=ui.transform.Find("respawn").GetComponent<Canvas>();
            Assert.IsTrue(modal.overrideSorting);
            Assert.IsNotNull(modal.GetComponent<GraphicRaycaster>());
            Assert.Greater(modal.sortingOrder,_session.Controls.GetComponentInChildren<Canvas>().sortingOrder);
            Assert.Less(modal.sortingOrder,BattlePauseMenu.SortingOrder);
        }
        [TestCase(false)][TestCase(true)]
        public void DragHeldBeforeDeathIsCancelledAndCameraStaysBlockedRegardlessOfCapture(bool mobile)
        {
            var captured=MouseLock.Captured;
            try
            {
                _session=BattleSession.Create(null,mobile,autoStart:true,mode:BattleMode.Uprising);
                var controls=_session.Controls;var surface=controls.Surface;
                surface.Press(42,new Vector2(100,100));Assert.AreEqual(1,surface.PointerCount);
                Kill(_session.Player);Assert.IsTrue(controls.ModalOpen);
                var yaw=_session.View.Yaw;var pitch=_session.View.Pitch;var length=_session.View.SpringLength;
                // Even the gesture already retained by EventSystem cannot leak
                // before the next Update disables and clears the surface.
                surface.Move(42,new Vector2(220,150),new Vector2(120,50));
                Assert.AreEqual(yaw,_session.View.Yaw);Assert.AreEqual(pitch,_session.View.Pitch);
                MouseLock.Set(true);
                typeof(BattleControls).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(controls,null);
                Assert.IsFalse(surface.InputEnabled);Assert.IsFalse(surface.GetComponent<Image>().raycastTarget);Assert.AreEqual(0,surface.PointerCount);
                surface.Press(42,new Vector2(100,100));surface.Move(42,new Vector2(400,300),new Vector2(300,200));
                Assert.AreEqual(0,surface.PointerCount);Assert.AreEqual(yaw,_session.View.Yaw);Assert.AreEqual(pitch,_session.View.Pitch);Assert.AreEqual(length,_session.View.SpringLength);
                Revive(_session.Player.GetComponent<UprisingTank>());
                typeof(BattleControls).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(controls,null);
                Assert.IsFalse(controls.ModalOpen);Assert.IsTrue(surface.InputEnabled);Assert.AreEqual(0,surface.PointerCount);
                surface.Press(43,Vector2.zero);surface.Move(43,Vector2.one*50,Vector2.one*50);
                Assert.AreNotEqual(yaw,_session.View.Yaw);
            }
            finally {MouseLock.Set(captured);}
        }
        [Test] public void RespawnKeepsPointerFreeIfPauseAbovePerksRemainsOpen()
        {
            var captured=MouseLock.Captured;
            try
            {
                Create();MouseLock.Set(true);Kill(_session.Player);
                var ui=_session.GetComponentInChildren<UprisingBattleUi>(true);
                var refresh=typeof(UprisingBattleUi).GetMethod("Refresh",BindingFlags.Instance|BindingFlags.NonPublic);
                refresh.Invoke(ui,null);_session.PauseMenu.Show();
                Revive(_session.Player.GetComponent<UprisingTank>());refresh.Invoke(ui,null);
                Assert.IsFalse(MouseLock.Captured);Assert.IsTrue(_session.PauseMenu.Visible);
                _session.PauseMenu.Toggle();Assert.IsTrue(MouseLock.Captured);
            }
            finally {MouseLock.Set(captured);}
        }
        [Test] public void PerkPresentationWaitsOneSecondThenFadesSmoothly()
        {
            Assert.AreEqual(0,BattleRespawnFrame.Fade(0));Assert.AreEqual(0,BattleRespawnFrame.Fade(.999));
            Assert.AreEqual(0,BattleRespawnFrame.Fade(1));Assert.AreEqual(.5f,BattleRespawnFrame.Fade(1.11),.0001);
            Assert.AreEqual(1,BattleRespawnFrame.Fade(1.3));
            var previous=0f;for(var tick=0;tick<150;tick++){var value=BattleRespawnFrame.Fade(tick/100d);Assert.GreaterOrEqual(value,previous);Assert.LessOrEqual(value-previous,.07f);previous=value;}
        }
        private static UprisingTank Ability(BattleTank tank, string id, bool running)
        {
            var c=tank.GetComponent<UprisingTank>();
            typeof(UprisingTank).GetField("<State>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(c,new UprisingState(c.State.Catalog,id));
            if(running){c.State.ActiveUntil=Time.time+10;c.State.ReadyAt=Time.time+35;if(id=="pretendDead")c.State.ShieldArmed=true;}
            return c;
        }
        [Test] public void ThanatosLooksLikeAWreckToEnemyPlayerOnly()
        {
            Create();var player=_session.Player;var enemy=_session.Enemies[0];
            Ability(enemy,"pretendDead",true);
            Assert.AreSame(enemy.GetComponent<UprisingTank>(),enemy.Uprising);
            Assert.IsTrue(enemy.LooksDeadTo(player));Assert.IsTrue(enemy.ShownDeadToLocalPlayer);Assert.IsFalse(enemy.LooksDeadTo(enemy));
            if(_session.Enemies.Count>1)Assert.IsFalse(enemy.LooksDeadTo(_session.Enemies[1]),"Team-mates know about the act");
            // The reticle never rests on the act: no target, no armour paint, no outline.
            enemy.SetSpotted(true);
            player.AimTraceHit(enemy.GetComponentInChildren<Collider>());
            Assert.IsNull(player.TankInAim);
            enemy.SetFakeWreck(true);
            enemy.PreviewArmor(true,player.CurrentShell());enemy.SwitchOutline(true);
            Assert.IsFalse(enemy.ArmorShown);Assert.IsTrue(enemy.FakeWreckShown);
            enemy.SetFakeWreck(false);enemy.GetComponent<UprisingTank>().State.ActiveUntil=-1;
            Assert.IsFalse(enemy.LooksDeadTo(player));Assert.IsFalse(enemy.ShownDeadToLocalPlayer);
        }
        [Test] public void FakeWreckStopsWheelsAndTrackScroll()
        {
            Create();var enemy=_session.Enemies[0];
            enemy.SetFakeWreck(true);var before=enemy.TrackScroll;
            enemy.Drive(1f,0f,false);
            var rotate=typeof(BattleTank).GetMethod("RotateTracks",BindingFlags.Instance|BindingFlags.NonPublic);
            for(var i=0;i<5;i++)rotate.Invoke(enemy,null);
            Assert.AreEqual(before,enemy.TrackScroll);Assert.AreEqual(0f,enemy.TracksSpeed);
            enemy.SetFakeWreck(false);
        }
        [Test] public void ThanatosActivationShedsEveryNegativeEffect()
        {
            var scale=Time.timeScale;Time.timeScale=1;
            try
            {
                Create();var player=_session.Player;var enemy=_session.Enemies[0];
                var caster=Ability(enemy,"curse",false);player.SetSpotted(true);
                typeof(BattleTank).GetProperty("GunTraceTank").SetValue(enemy,player);
                Assert.IsTrue(caster.UseActive(player));
                var c=player.GetComponent<UprisingTank>();Assert.IsTrue(c.HasStatus("curse"));
                player.DamageModule(TankModuleIds.TrackLeft,100000);Assert.IsTrue(player.TrackCrashed(TrackSide.Left));
                var self=Ability(player,"pretendDead",false);Assert.AreSame(c,self);
                Assert.IsTrue(self.UseActive());
                Assert.IsFalse(self.HasStatus("curse"));Assert.IsFalse(player.TrackCrashed(TrackSide.Left));Assert.IsTrue(self.State.ShieldArmed);
            }
            finally {Time.timeScale=scale;}
        }
        [Test] public void ThanatosRamHurtsTheEnemyOnlyAndEndsTheAct()
        {
            Create();var player=_session.Player;var enemy=_session.Enemies[0];var c=Ability(player,"pretendDead",true);
            var hp=player.Health.Health;var enemyHp=enemy.Health.Health;
            var expected=Mathf.Min(enemyHp,BattleRam.DamageFor(enemy,player,3f));Assert.Greater(expected,0);
            _session.Ram.Resolve(player,enemy,3f);
            Assert.AreEqual(hp,player.Health.Health,"Play Dead takes no ram damage");
            Assert.AreEqual(enemyHp-expected,enemy.Health.Health,"The ram lands like a live tank's");
            Assert.IsFalse(c.PretendDeadActive);Assert.IsFalse(c.State.ShieldArmed);Assert.Greater(c.State.ReadyAt,Time.time,"The cooldown keeps running");
        }
        [Test] public void EnemyRammingThanatosOnlyEndsTheAct()
        {
            Create();var player=_session.Player;var enemy=_session.Enemies[0];var c=Ability(enemy,"pretendDead",true);
            var hp=player.Health.Health;var enemyHp=enemy.Health.Health;
            var expected=Mathf.Min(hp,BattleRam.DamageFor(player,enemy,3f));Assert.Greater(expected,0);
            _session.Ram.Resolve(player,enemy,3f);
            Assert.AreEqual(enemyHp,enemy.Health.Health);Assert.AreEqual(hp-expected,player.Health.Health);Assert.IsFalse(c.PretendDeadActive);
        }
        [Test] public void TwoThanatosTanksCollidingBothLoseItWithoutDamage()
        {
            Create();var player=_session.Player;var enemy=_session.Enemies[0];
            var a=Ability(player,"pretendDead",true);var b=Ability(enemy,"pretendDead",true);
            var hp=player.Health.Health;var enemyHp=enemy.Health.Health;
            _session.Ram.Resolve(player,enemy,60f);
            Assert.AreEqual(hp,player.Health.Health);Assert.AreEqual(enemyHp,enemy.Health.Health);
            Assert.IsFalse(a.PretendDeadActive);Assert.IsFalse(b.PretendDeadActive);
            Assert.Greater(a.State.ReadyAt,Time.time,"The ability goes on cooldown");Assert.Greater(b.State.ReadyAt,Time.time);
        }
        [Test] public void ThanatosIsSpottedLikeARealWreck()
        {
            Create();var player=_session.Player;var enemy=_session.Enemies[0];
            enemy.transform.position=player.transform.position+Vector3.right*5000f;Physics.SyncTransforms();enemy.SetSpotted(false);
            Ability(enemy,"pretendDead",true);
            var spotter=_session.Spotter;Assert.IsNotNull(spotter);
            spotter.Tick(Time.time+100f);
            Assert.IsTrue(enemy.Spotted);
        }
        [Test] public void ShadowHidesTrackMarksFromEnemiesOnly()
        {
            Create();var player=_session.Player;var enemy=_session.Enemies[0];
            Ability(enemy,"moonwalk",true);Ability(player,"moonwalk",true);
            Assert.IsTrue(enemy.Uprising.Concealed);
            Assert.IsTrue(enemy.HidesTrackMarksFromLocalPlayer);Assert.IsFalse(player.HidesTrackMarksFromLocalPlayer);
            Assert.IsFalse(enemy.Uprising.HidesTrackMarksFrom(enemy.Team));
            enemy.Uprising.State.ActiveUntil=-1;enemy.Uprising.State.CloakUntil=-1;Assert.IsFalse(enemy.HidesTrackMarksFromLocalPlayer);
        }
        [Test] public void ShadowKeepsHuntersMarkInsteadOfCleansingIt()
        {
            var scale=Time.timeScale;Time.timeScale=1;
            try
            {
                Create();var player=_session.Player;var enemy=_session.Enemies[0];
                var caster=Ability(player,"huntersMark",false);enemy.SetSpotted(true);
                typeof(BattleTank).GetProperty("GunTraceTank").SetValue(player,enemy);
                Assert.IsTrue(caster.UseActive());
                var c=enemy.GetComponent<UprisingTank>();Assert.IsTrue(c.HasStatus("huntersMark"));
                var statuses=typeof(UprisingTank).GetField("_statuses",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(c);
                typeof(UprisingTank).GetField("<State>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(c,new UprisingState(c.State.Catalog,"moonwalk"));
                c.State.ActiveUntil=Time.time+10;enemy.SetSpotted(false);
                var tick=typeof(UprisingTank).GetMethod("TickStatuses",BindingFlags.Instance|BindingFlags.NonPublic);
                tick.Invoke(c,null);Assert.IsTrue(c.HasStatus("huntersMark"),"Shadow hides the mark, it does not remove it");
                c.State.ActiveUntil=-1;tick.Invoke(c,null);Assert.IsFalse(c.HasStatus("huntersMark"));
                Assert.IsNotNull(statuses);
            }
            finally {Time.timeScale=scale;}
        }
        [Test] public void NormalModeDoesNotMountUprisingControllers()
        {
            _session=BattleSession.Create(null,false,autoStart:false);Assert.IsNull(_session.Player.GetComponent<UprisingTank>());Assert.AreEqual(BattleMode.Normal,_session.Mode);
        }
    }
}
