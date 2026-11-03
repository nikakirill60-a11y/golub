using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    public sealed class UprisingDupletTests
    {
        private BattleSession _session;
        private BattleTank _tank;
        private UprisingTank _controller;
        private AudioService _audio;
        private TankSoundManager _sounds;
        private float _now;
        private int _actual,_primary,_bloom;
        private readonly List<int> _slots=new List<int>();
        [SetUp] public void Setup(){_now=100;_actual=_primary=_bloom=0;_slots.Clear();BattleGun.Clock=()=>_now;}
        [TearDown] public void Cleanup(){BattleGun.Clock=null;if(_session!=null)Object.DestroyImmediate(_session.gameObject);}
        private void Create(string id,bool duplet)
        {
            _session=BattleSession.Create(null,false,tankId:id,mode:BattleMode.Uprising);
            _tank=_session.Player;_tank.SetShellAmounts(new[]{300,300,300});_tank.SelectShell(0);_tank.Gun.Reload(0);_now+=.001f;
            _controller=_tank.GetComponent<UprisingTank>();if(duplet)Assert.IsTrue(_controller.State.Grant("duplet"));
            _tank.ProjectileFired+=(first,slot)=>{_actual++;if(first)_bloom++;_slots.Add(slot);};
            _tank.PrimaryProjectileFired+=slot=>_primary++;
            _audio=AudioService.Create(_session.transform,new SettingsData());_sounds=TankSoundManager.Attach(_tank,_audio,true);
        }
        private int FullMagazine()
        {
            var gun=_tank.Gun as BattleGunBurst;
            var sizes=gun!=null?_tank.GunInfo.Burst.Bursts.ToArray():new[]{1};
            var total=0;
            foreach(var size in sizes)
            {
                var start=_actual;Assert.IsTrue(_tank.Gun.CanShoot());_tank.Gun.ShootAttempt();_tank.Gun.Tick();
                for(var i=1;i<size;i++){
                    _now+=_tank.GunInfo.Burst.IntraBurst+.001f;_tank.Gun.Tick();_controller.TickDuplet();}
                _now+=UprisingTank.DupletShotDelay+.001f;_controller.TickDuplet();
                total+=_actual-start;
                _now=Mathf.Max(_now,_tank.Gun.NextShoot+.001f);
            }
            return total;
        }
        [TestCase(false)][TestCase(true)]
        public void ChineseVkReallyFiresFiveOrTenPerSalvoAndFifteenOrThirtyPerDrum(bool duplet)
        {
            Create("g190_vk_1602_quby",duplet);
            CollectionAssert.AreEqual(new[]{5,5,5},_tank.GunInfo.Burst.Bursts);
            Assert.AreEqual(.15f,_tank.GunInfo.Burst.IntraBurst,.0001f);Assert.AreEqual(1,_tank.GunInfo.Burst.IntraClip,.0001f);
            var gun=(BattleGunBurst)_tank.Gun;var factor=duplet?2:1;
            for(var volley=0;volley<3;volley++){
                var before=_actual;Assert.IsTrue(gun.CanShoot());gun.ShootAttempt();gun.Tick();
                for(var i=1;i<5;i++){_now+=.151f;gun.Tick();_controller.TickDuplet();}
                _now+=.301f;_controller.TickDuplet();Assert.AreEqual(5*factor,_actual-before);
                Assert.AreEqual(2-volley,gun.Bursts.Count);_now=gun.NextShoot+.001f;
            }
            Assert.AreEqual(15*factor,_actual);Assert.AreEqual(15,_primary);
            Assert.AreEqual(3,_bloom,"bloom flag is not a primary/echo flag");
            Assert.AreEqual(300-_actual,_tank.ShellAmounts[0]);Assert.AreEqual(0,_tank.PendingDupletShots);
            Assert.AreEqual(_actual,_sounds.GunPlayer.PlayCount);Assert.AreEqual(_actual,_sounds.GunPlayerInside.PlayCount);
            _now+=10;_controller.TickDuplet();Assert.AreEqual(15*factor,_actual,"extras must never schedule another duplet");
        }
        [TestCase("m-6-y",false)][TestCase("m-6-y",true)]
        [TestCase("a150_mbt_b",false)][TestCase("a150_mbt_b",true)]
        [TestCase("s16_kranvagn",false)][TestCase("s16_kranvagn",true)]
        [TestCase("ms-1",false)][TestCase("ms-1",true)]
        public void OrdinaryDrumAutoreloadersAndSingleGunsDuplicateEachPrimaryOnly(string id,bool duplet)
        {
            Create(id,duplet);var gun=_tank.Gun as BattleGunBurst;
            var primary=gun!=null?_tank.GunInfo.Burst.Bursts.Sum():1;
            Assert.AreEqual(primary*(duplet?2:1),FullMagazine());
            Assert.AreEqual(primary,_primary);Assert.AreEqual(300-_actual,_tank.ShellAmounts[0]);
            if(gun!=null)Assert.AreEqual(0,gun.Bursts.Count);
            Assert.AreEqual(_actual,_sounds.GunPlayer.PlayCount);Assert.AreEqual(0,_tank.PendingDupletShots);
        }
        [Test] public void EchoKeepsTheFiredSlotEvenIfAnEarlierListenerChangesSelection()
        {
            Create("ms-1",true);_tank.ProjectileFired+=(first,slot)=>{if(first)_tank.SelectShell(1);};
            _tank.Gun.ShootAttempt();Assert.AreEqual(1,_tank.ShellId);
            _now+=.301f;_controller.TickDuplet();
            CollectionAssert.AreEqual(new[]{0,0},_slots);Assert.AreEqual(1,_tank.ShellId);
            Assert.AreEqual(298,_tank.ShellAmounts[0]);Assert.AreEqual(300,_tank.ShellAmounts[1]);
        }
        [Test] public void DupletUsesItsDeadlineAndBlocksANewTriggerUntilTheEchoResolves()
        {
            Create("ms-1",true);_tank.Gun.ShootAttempt();_tank.Gun.Reload(0);
            Assert.AreEqual(1,_tank.PendingDupletShots);Assert.IsFalse(_tank.Gun.CanShoot());
            _now+=.299f;_controller.TickDuplet();Assert.AreEqual(1,_actual);
            _now+=.002f;_controller.TickDuplet();Assert.AreEqual(2,_actual);
            Assert.AreEqual(0,_tank.PendingDupletShots);Assert.IsTrue(_tank.Gun.CanShoot());
        }
        [Test] public void AutoreloaderEchoDoesNotResetLoadingOrSpendAnotherLoadedRound()
        {
            Create("a150_mbt_b",true);var gun=(BattleGunBurst)_tank.Gun;
            gun.ShootAttempt();var loaded=gun.Bursts.Count;var delay=gun.NextShellSeconds;var trigger=gun.NextShoot;
            _now+=.301f;_controller.TickDuplet();
            Assert.AreEqual(loaded,gun.Bursts.Count);Assert.AreEqual(trigger,gun.NextShoot);
            Assert.AreEqual(delay-.301f,gun.NextShellSeconds,.001f);Assert.AreEqual(2,_actual);
        }
        [Test] public void EmptyRackSuppressesEchoAndLaterBurstIntentsWithoutPhantomAudio()
        {
            Create("g190_vk_1602_quby",true);_tank.SetShellAmounts(new[]{1,0,0});
            _tank.Gun.ShootAttempt();_tank.Gun.Tick();_now+=.7f;_tank.Gun.Tick();_controller.TickDuplet();
            Assert.AreEqual(1,_actual);Assert.AreEqual(1,_primary);Assert.AreEqual(0,_tank.ShellAmounts[0]);
            Assert.AreEqual(1,_sounds.GunPlayer.PlayCount);Assert.AreEqual(1,_sounds.GunPlayerInside.PlayCount);
            Assert.AreEqual(0,_tank.PendingDupletShots);
        }
        [Test] public void DeathAndRespawnCancelOldLifeEchoAndReleaseThePendingGate()
        {
            Create("ms-1",true);_tank.Gun.ShootAttempt();Assert.AreEqual(1,_tank.PendingDupletShots);
            // This EditMode test is about lifetime cancellation, not transient playback/destruction.
            // Real damage/death audio is covered by the editor Play Mode gate.
            typeof(TankSoundManager).GetMethod("OnDestroy",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(_sounds,null);
            Object.DestroyImmediate(_sounds);_sounds=null;
            _tank.ReceiveDamage(new DamageInfo{Dmg=100000,DmgBase=100000,DmgType=DamageType.Ability});
            Assert.IsTrue(_tank.Dead);Assert.AreEqual(0,_tank.PendingDupletShots);
            Assert.IsTrue(_controller.State.Respawn(Time.time+11,true));
            typeof(UprisingTank).GetMethod("Restore",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(_controller,null);
            _now+=1;_controller.TickDuplet();Assert.AreEqual(1,_actual);
        }
        [Test] public void RemovingControllerCancelsAPendingEcho()
        {
            Create("ms-1",true);_tank.Gun.ShootAttempt();
            typeof(UprisingTank).GetMethod("OnDestroy",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(_controller,null);
            Object.DestroyImmediate(_controller);Assert.AreEqual(0,_tank.PendingDupletShots);
            _now+=1;Assert.AreEqual(1,_actual);
        }
        private void SetMuffle(float amount)
        {
            typeof(AudioService).GetField("_muffle",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(_audio,amount);
            _audio.ApplySettings();
        }
        [Test] public void SniperTransitionsRefreshBothLayersWithoutReplayingOrDoublingTheAttack()
        {
            Create("ms-1",false);_tank.Gun.ShootAttempt();var outside=_sounds.GunPlayer.Source.volume;
            Assert.AreEqual(0,_sounds.GunPlayerInside.Source.volume);
            SetMuffle(1);Assert.Greater(_sounds.GunPlayerInside.Source.volume,0);
            Assert.AreEqual(outside*TankSoundTuning.MuffleSfxShare,_sounds.GunPlayer.Source.volume,.0001f);
            SetMuffle(0);Assert.AreEqual(0,_sounds.GunPlayerInside.Source.volume);Assert.AreEqual(outside,_sounds.GunPlayer.Source.volume,.0001f);
            Assert.AreEqual(1,_sounds.GunPlayer.PlayCount);Assert.AreEqual(1,_sounds.GunPlayerInside.PlayCount);
            _sounds.CollisionPlayer.SetVolumeLinear(.22f);SetMuffle(1);
            Assert.AreEqual(_audio.SpatialVolume(AudioBus.Sfx,.22f),_sounds.CollisionPlayer.Source.volume,.0001f,"retain collision impulse gain during bus tween");
            _audio.Settings.VolumeMaster=.25f;_audio.ApplySettings();
            Assert.AreEqual(_audio.SpatialVolume(AudioBus.Sfx,.22f),_sounds.CollisionPlayer.Source.volume,.0001f);
        }
        [Test] public void SoundRigAttachIsIdempotentAndDestroyDetachesAllShotListeners()
        {
            Create("ms-1",false);Assert.AreSame(_sounds,TankSoundManager.Attach(_tank,_audio,true));
            _tank.Gun.ShootAttempt();var old=_sounds;Assert.AreEqual(1,old.GunPlayer.PlayCount);
            typeof(TankSoundManager).GetMethod("OnDestroy",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(_sounds,null);
            Object.DestroyImmediate(_sounds);_sounds=TankSoundManager.Attach(_tank,_audio,true);
            _tank.Gun.Reload(0);_now+=.001f;_tank.Gun.ShootAttempt();
            Assert.AreEqual(1,old.GunPlayer.PlayCount);Assert.AreEqual(1,_sounds.GunPlayer.PlayCount);
            Assert.AreEqual(1,_tank.GetComponents<TankSoundManager>().Length);
        }
    }
}
