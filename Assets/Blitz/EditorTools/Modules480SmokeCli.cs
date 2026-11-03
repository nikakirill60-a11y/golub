using System;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Actual physics shots, natural repair tick and live death/restore.</summary>
    [InitializeOnLoad]
    public static class Modules480SmokeCli
    {
        private const string Key="Blitz.Modules480Smoke";
        private static BattleTank _shooter,_target;
        private static int _stage,_shots,_errors;
        private static float _at;
        private static DateTime _deadline;
        private static bool _hooked;
        static Modules480SmokeCli(){if(SessionState.GetBool(Key,false))Hook();}
        public static void Run()
        {
            SessionState.SetBool(Key,true);SessionState.SetInt(Key+".result",-1);Hook();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);EditorApplication.isPlaying=true;
        }
        private static void Hook()
        {
            if(_hooked)return;_hooked=true;_deadline=DateTime.UtcNow.AddMinutes(3);
            EditorApplication.playModeStateChanged+=Changed;EditorApplication.update+=Tick;Application.logMessageReceived+=Message;
        }
        private static void Message(string text,string stack,LogType type){if(type==LogType.Error||type==LogType.Exception)_errors++;}
        private static void Changed(PlayModeStateChange state)
        {
            if(state==PlayModeStateChange.EnteredPlayMode)EditorApplication.delayCall+=Prepare;
            if(state==PlayModeStateChange.EnteredEditMode)
            {
                var result=SessionState.GetInt(Key+".result",-1);if(result>=0)Finish(result);
            }
        }
        private static void Prepare()
        {
            try
            {
                var database=TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                _shooter=BattleTank.Spawn(null,"shooter",database.Get("ms-1"),null,new Bounds(),0);
                _target=BattleTank.Spawn(null,"target",database.Get("maus"),null,new Bounds(),1);
                _shooter.transform.position=new Vector3(1000,20,975);_target.transform.position=new Vector3(1000,20,1000);
                _shooter.GetComponent<Rigidbody>().constraints=RigidbodyConstraints.FreezeAll;
                _target.GetComponent<Rigidbody>().constraints=RigidbodyConstraints.FreezeAll;
                foreach(var tank in new[]{_shooter,_target})foreach(var p in tank.Plates)p.SyncToMount();
                foreach(var shell in _shooter.GunInfo.Shells){shell.Type=ShellType.Ap;shell.Penetration=1;}
                _shooter.SetShellAmounts(new[]{20,0,0});Physics.SyncTransforms();Fire();_stage=1;
            }
            catch(Exception e){Fail(e);}
        }
        private static void Fire()
        {
            var plan=ArmorShotPlanner.Choose(_shooter,_target);Require(plan.Valid&&plan.Tracks,"No exposed track fallback");
            var from=_shooter.GunShootPoint();var direction=(plan.Point-from).normalized;
            BattleProjectile.Create(_shooter.GunInfo.Shells[plan.Shell],_shooter,from,from,direction);
            _shots++;_at=Time.time;
        }
        private static void Tick()
        {
            if(DateTime.UtcNow>_deadline){Fail(new TimeoutException("Modules480 Play Mode timed out"));return;}
            if(!EditorApplication.isPlaying||_target==null)return;
            try
            {
                if(_stage==1&&Time.time-_at>.35f)
                {
                    Require(_target.Modules.Get(TankModuleIds.Tracks).HealthComponent.Health<300,"Physical projectile did not damage track");
                    Require(_target.Health.Health==_target.Health.MaxHealth,"Nonpenetrating track shot damaged hull HP");
                    if(!_target.TracksCrashed){Require(_shots<8,"Track fallback never disabled tracks");Fire();return;}
                    Require(_target.Suspension.ForwardAxis==0&&_target.Suspension.RightAxis==0,"Disabled track did not stop drive");_at=Time.time;_stage=2;
                }
                else if(_stage==2&&Time.time-_at>_target.GetRepairTime()+.5f)
                {
                    Require(!_target.TracksCrashed,"Natural FixedUpdate repair did not restore tracks");
                    var zone=_target.AmmoZones.Zones[0];var point=zone.Mount.TransformPoint(zone.Bounds.center);
                    var shell=_shooter.GunInfo.Shells[0];shell.DeviceDamage=1000;
                    Require(_target.ApplyPenetratingModuleHit(shell,point,zone.Mount.right,new DamageInfo{AttackerTank=_shooter,ShotId=777},0),"Rack volume rejected hit");
                    Require(_target.Dead&&_target.AmmoBayExploded,"Destroyed rack did not kill tank");_at=Time.time;_stage=3;
                }
                else if(_stage==3&&Time.time-_at>2)
                {
                    Require(_target.TracksSpeed==0,"Dead tracks scrolled in live physics");
                    var wreck=_target.GetComponentInChildren<Original480Wreck>(true);
                    Require(wreck.Playing&&wreck.Effects[1].LiveParticles>0,"Original wreck did not emit");
                    _target.RestoreLife(null);Require(!_target.Dead&&!wreck.Playing&&!_target.AmmoBayExploded,"Live restore failed");
                    Require(_errors==0,"Runtime error count="+_errors);
                    Debug.Log("[Blitz] Modules480 Play Mode passed: physical track shots="+_shots+", natural repair, geometric rack crit, original wreck, stopped wheels, restore; errors=0");Exit(0);
                }
            }
            catch(Exception e){Fail(e);}
        }
        private static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        private static void Fail(Exception e){Debug.LogException(e);Exit(1);}
        private static void Exit(int result){SessionState.SetInt(Key+".result",result);EditorApplication.update-=Tick;if(EditorApplication.isPlaying)EditorApplication.isPlaying=false;else Finish(result);}
        private static void Finish(int result)
        {
            SessionState.SetBool(Key,false);EditorApplication.playModeStateChanged-=Changed;EditorApplication.update-=Tick;Application.logMessageReceived-=Message;_hooked=false;EditorApplication.Exit(result);
        }
    }
}
