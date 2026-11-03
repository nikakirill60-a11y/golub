using System;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace BlitzLegacy.EditorTools
{
    /// <summary>Unmocked PhysX drive/ram, live stack, wreck stack and free fall.</summary>
    [InitializeOnLoad]
    public static class ContactPhysicsSmokeCli
    {
        private const string Key="Blitz.ContactPhysicsSmoke";
        private static bool _hooked;
        private static DateTime _deadline;
        private static int _stage,_errors,_hpA,_hpB,_ramCase,_driveCase,_hz=60;
        private static float _at,_baseY,_maxRise,_crossedAt;
        private static BattleTank _a,_b;
        private static TanksDatabase _db;
        static ContactPhysicsSmokeCli(){if(SessionState.GetBool(Key,false))Hook();}
        public static void Run(){SessionState.SetBool(Key,true);SessionState.SetInt(Key+".result",-1);Hook();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);EditorApplication.isPlaying=true;}
        private static void Hook()
        {
            if(_hooked)return;_hooked=true;_deadline=DateTime.UtcNow.AddMinutes(3);
            EditorApplication.update+=Tick;EditorApplication.playModeStateChanged+=Changed;Application.logMessageReceived+=Message;
        }
        private static void Message(string m,string stack,LogType type){if(type==LogType.Error||type==LogType.Exception)_errors++;}
        private static void Changed(PlayModeStateChange state)
        {
            if(state==PlayModeStateChange.EnteredPlayMode)EditorApplication.delayCall+=Prepare;
            if(state==PlayModeStateChange.EnteredEditMode){var r=SessionState.GetInt(Key+".result",-1);if(r>=0)Finish(r);}
        }
        private static BattleTank Spawn(string id,Vector3 pos)
        {
            var t=BattleTank.Spawn(null,id,_db.Get(id),null,new Bounds(),0);t.GetComponent<Rigidbody>().position=pos;t.transform.position=pos;
            foreach(var p in t.Plates)p.SyncToMount();t.GetComponent<TankContactPhysics>().ResetState();
            // Initialize reapplies source defaults; override only AFTER spawning.
            Time.fixedDeltaTime=1f/_hz;Physics.SyncTransforms();return t;
        }
        private static void Prepare()
        {
            try
            {
                _db=TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                var hz = Environment.GetEnvironmentVariable("BLITZ_CONTACT_HZ");
                _hz = string.IsNullOrEmpty(hz) ? 60 : int.Parse(hz);
                Require(_hz==50||_hz==60||_hz==100,"Contact smoke Hz must be 50, 60 or 100");
                var ground=GameObject.CreatePrimitive(PrimitiveType.Cube);ground.name="contact test ground";ground.layer=BattleLayers.Static;
                ground.transform.position=new Vector3(1000,19.5f,1000);ground.transform.localScale=new Vector3(100,1,100);
                _a=Spawn("ms-1",new Vector3(1000,20.2f,992));_b=Spawn("maus",new Vector3(1000,20.2f,1000));_at=Time.time;_stage=0;_ramCase=0;
            }
            catch(Exception e){Fail(e);}
        }
        private static void Tick()
        {
            if(DateTime.UtcNow>_deadline){Fail(new TimeoutException("Contact physics smoke timeout"));return;}
            if(!EditorApplication.isPlaying||_a==null)return;
            try
            {
                var elapsed=Time.time-_at;
                if(_stage==0&&elapsed>2)
                {
                    Require(_a.Suspension.AnyHit&&_b.Suspension.AnyHit,"Suspension failed to support installed mass");
                    _baseY=_a.transform.position.y;_maxRise=0;_a.GetComponent<Rigidbody>().linearVelocity=Vector3.forward*16;_a.Drive(1,0);_at=Time.time;_stage=1;
                }
                else if(_stage==1)
                {
                    _maxRise=Mathf.Max(_maxRise,_a.transform.position.y-_baseY);
                    if(elapsed<3)return;
                    Require(_maxRise<.65f,"Fast ram climbed onto sloped armour: rise="+_maxRise);
                    Require(_a.GetComponent<TankContactPhysics>().StackContactCount==0,"Side ram mistaken for stack");
                    Debug.Log("[Blitz] Contact ram case="+_ramCase+" max rise="+_maxRise+"m; suspension stable");
                    UnityEngine.Object.Destroy(_a.gameObject);UnityEngine.Object.Destroy(_b.gameObject);
                    if(_ramCase==0)
                    {
                        _ramCase=1;_a=Spawn("maus",new Vector3(1000,20.2f,992));_b=Spawn("ms-1",new Vector3(1000,20.2f,1000));
                        _at=Time.time;_stage=0;return;
                    }
                    _a=Spawn("ms-1",new Vector3(1020,20,1000));_a.GetComponent<Rigidbody>().isKinematic=true;
                    _b=Spawn("ms-1",new Vector3(1020,23,1000));var top=_b.GetComponent<TankContactPhysics>().ContactBand;var bottom=_a.GetComponent<TankContactPhysics>().ContactBand;
                    var body=_b.GetComponent<Rigidbody>();body.position+=Vector3.up*(bottom.bounds.max.y-top.bounds.min.y+.03f);
                    body.constraints=RigidbodyConstraints.FreezePositionX|RigidbodyConstraints.FreezePositionZ|RigidbodyConstraints.FreezeRotation;
                    _b.GetComponent<TankContactPhysics>().ResetState();_at=Time.time;_stage=2;
                }
                else if(_stage==2&&elapsed>1)
                {Require(_a.GetComponent<TankContactPhysics>().StackContactCount>0,"Real vertical contact not detected");_hpA=_a.Health.Health;_hpB=_b.Health.Health;_at=Time.time;_stage=3;}
                else if(_stage==3&&elapsed>1)
                {
                    var a=_hpA-_a.Health.Health;var b=_hpB-_b.Health.Health;
                    Require(a>=4&&a<=6&&b>=4&&b<=6,"Stack rate is not 5 HP/s: "+a+"/"+b);
                    Debug.Log("[Blitz] Live stack one-second HP loss="+a+"/"+b);
                    _b.ReceiveDamage(new DamageInfo{Dmg=100000});_hpA=_a.Health.Health;_at=Time.time;_stage=4;
                }
                else if(_stage==4&&elapsed>1)
                {
                    Require(_b.Dead&&_hpA-_a.Health.Health>=4,"Wreck on a live tank stopped dealing crush damage");
                    Debug.Log("[Blitz] Wreck stack one-second HP loss="+(_hpA-_a.Health.Health));
                    UnityEngine.Object.Destroy(_a.gameObject);UnityEngine.Object.Destroy(_b.gameObject);
                    _a=Spawn("g14_kpfpz_70r",new Vector3(980,22,1000));_hpA=_a.Health.Health;_b=null;_at=Time.time;_stage=5;
                }
                else if(_stage==5&&elapsed>3)
                {
                    Require(_a.Suspension.AnyHit,"Small KpfPz fall did not settle");
                    Require(_a.Health.Health==_hpA&&!_a.TracksCrashed,"Small KpfPz hill damaged hull/mobility");
                    Require(_a.Modules.Get(TankModuleIds.Tracks).HealthComponent.Health==Mathf.RoundToInt(_a.TrackInfo.Health),"Small KpfPz hill damaged tracks");
                    Debug.Log("[Blitz] KpfPz 70 small drop: hull/track damage=0; hz="+Mathf.RoundToInt(1f/Time.fixedDeltaTime));
                    UnityEngine.Object.Destroy(_a.gameObject);
                    _a=Spawn("g14_kpfpz_70r",new Vector3(980,25,1000));_hpA=_a.Health.Health;_at=Time.time;_stage=8;
                }
                else if(_stage==8&&elapsed>3)
                {
                    var p=_a.GetComponent<TankContactPhysics>();
                    Require(_a.Suspension.AnyHit,"Medium KpfPz fall did not settle");
                    Require(_a.Health.Health==_hpA&&!_a.TracksCrashed,"Medium KpfPz hill damaged hull/mobility");
                    var tracks=_a.Modules.Get(TankModuleIds.Tracks).HealthComponent;
                    Require(tracks.Health>0&&tracks.Health<tracks.MaxHealth&&p.LandingCount==1,"Medium KpfPz fall did not apply track-only damage once");
                    Debug.Log("[Blitz] KpfPz 70 medium drop: hull damage=0; tracks="+tracks.Health+"/"+tracks.MaxHealth+"; landings="+p.LandingCount);
                    UnityEngine.Object.Destroy(_a.gameObject);
                    _a=Spawn("ms-1",new Vector3(980,36,1000));_hpA=_a.Health.Health;_at=Time.time;_stage=9;
                }
                else if(_stage==9&&elapsed>3)
                {
                    var p=_a.GetComponent<TankContactPhysics>();
                    Require(!_a.Dead,"16m drop should hurt, not instantly destroy a healthy tank");
                    Require(p.LandingCount==1&&p.LastFallDamage>0,"Measured free fall did not damage hull: landings="+p.LandingCount);
                    Require(_a.TracksCrashed,"High drop did not disable tracks");
                    Require(_errors==0,"Runtime errors: "+_errors);
                    Debug.Log("[Blitz] Contact physics Play Mode passed: fast ram, live/wreck stack, measured fall speed="+p.LastLandingSpeed+" hull damage="+p.LastFallDamage+" tracks disabled; errors=0");
                    UnityEngine.Object.Destroy(_a.gameObject);
                    var ramp=GameObject.CreatePrimitive(PrimitiveType.Cube);ramp.name="20 degree rollback test";ramp.layer=BattleLayers.Static;
                    ramp.transform.position=new Vector3(970,30,1000);ramp.transform.localScale=new Vector3(20,1,70);ramp.transform.rotation=Quaternion.Euler(-20,0,0);
                    _a=Spawn("maus",new Vector3(970,32,1000));_a.GetComponent<Rigidbody>().rotation=ramp.transform.rotation;_a.transform.rotation=ramp.transform.rotation;
                    foreach(var plate in _a.Plates)plate.SyncToMount();Physics.SyncTransforms();
                    _at=Time.time;_stage=6;
                }
                else if(_stage==6&&elapsed>2)
                {
                    Require(_a.Suspension.AnyHit&&_a.Suspension.GroundNormal.z<-.2f,"Rollback test did not settle on the 20 degree slope");
                    _a.GetComponent<Rigidbody>().linearVelocity=-_a.transform.forward*6;_a.Drive(1,0);
                    _driveCase=0;_crossedAt=-1;_at=Time.time;_stage=7;
                }
                else if(_stage==7)
                {
                    var speed=Vector3.Dot(_a.GetComponent<Rigidbody>().linearVelocity,_a.transform.forward);
                    if(_crossedAt<0&&(_driveCase==0?speed>.1f:speed<-.1f))_crossedAt=elapsed;
                    if(elapsed<2.2f)return;
                    Require(_crossedAt>=0&&_crossedAt<2,"Slope reversal remained unresponsive: case="+_driveCase+" crossing="+_crossedAt+" speed="+speed);
                    Debug.Log("[Blitz] 20-degree slope reversal case="+_driveCase+" crossed requested direction in "+_crossedAt+"s; speed="+speed+"m/s");
                    if(_driveCase==0)
                    {
                        _a.GetComponent<Rigidbody>().linearVelocity=_a.transform.forward*9;_a.Drive(-1,0);
                        _driveCase=1;_crossedAt=-1;_at=Time.time;return;
                    }
                    Require(_errors==0,"Runtime errors: "+_errors);
                    Debug.Log("[Blitz] Contact and drive response Play Mode passed: both overspeed reversals on 20-degree slope; errors=0");
                    SessionState.SetInt(Key+".result",0);EditorApplication.isPlaying=false;_a=null;
                }
            }
            catch(Exception e){Fail(e);}
        }
        private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        private static void Fail(Exception e){Debug.LogException(e);SessionState.SetInt(Key+".result",1);if(EditorApplication.isPlaying)EditorApplication.isPlaying=false;else Finish(1);}
        private static void Finish(int code){SessionState.SetBool(Key,false);EditorApplication.update-=Tick;EditorApplication.playModeStateChanged-=Changed;Application.logMessageReceived-=Message;_hooked=false;EditorApplication.Exit(code);}
    }
}
