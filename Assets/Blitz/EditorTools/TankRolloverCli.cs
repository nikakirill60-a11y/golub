using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace BlitzLegacy.EditorTools
{
    /// <summary>Live PhysX rollover trajectories and negative controls on actual imported hulls.</summary>
    [InitializeOnLoad]
    public static class TankRolloverCli
    {
        private const string Key="Blitz.RolloverPhysics";
        private const int PositiveCases=24, TotalCases=31;
        private static bool _hooked;
        private static int _case,_errors,_failures;
        private static DateTime _deadline;
        private static float _at,_stableAt=-1;
        private static BattleTank _tank;
        private static GameObject _floor,_wall;
        private static Rigidbody _body;
        private static TankRolloverPhysics _rollover;
        private static Quaternion _previous;
        private static float _previousTick,_maxStep,_path;
        private static TanksDatabase _db;
        private static readonly string[] Cards={"ms-1","t49_rocket","maus","m-6-y"};
        private static readonly List<string> Results=new List<string>();
        private static bool _firstMotor;
        static TankRolloverCli(){if(SessionState.GetBool(Key,false))Hook();}
        public static void Run(){SessionState.SetBool(Key,true);SessionState.SetInt(Key+".result",-1);Hook();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);EditorApplication.isPlaying=true;}
        private static void Hook()
        {
            if(_hooked)return;_hooked=true;_deadline=DateTime.UtcNow.AddMinutes(5);
            Application.logMessageReceived+=(message,stack,type)=>{if(type==LogType.Error || type==LogType.Exception)_errors++;};
            EditorApplication.update+=Tick;EditorApplication.playModeStateChanged+=state=>{
                if(state==PlayModeStateChange.EnteredPlayMode)EditorApplication.delayCall+=Prepare;
                if(state==PlayModeStateChange.EnteredEditMode && SessionState.GetInt(Key+".result",-1)>=0){SessionState.SetBool(Key,false);EditorApplication.Exit(SessionState.GetInt(Key+".result",1));}};
        }
        private static void Prepare(){try{TankSuspension.ApplySourcePhysicsDefaults();Application.runInBackground=true;_db=TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));Begin();}catch(Exception e){Debug.LogException(e);_failures++;Finish();}}
        private static void Check(bool ok,string message){if(!ok)_failures++;Debug.Log("[Rollover] "+(ok?"PASS ":"FAIL ")+message);}
        private static void Begin()
        {
            if(_tank!=null)UnityEngine.Object.DestroyImmediate(_tank.gameObject);if(_floor!=null)UnityEngine.Object.DestroyImmediate(_floor);if(_wall!=null)UnityEngine.Object.DestroyImmediate(_wall);
            var control=_case>=PositiveCases;
            var scenario=control?12+_case-PositiveCases:_case%12;
            var gravity=(!control && _case>=12) || scenario==18;
            var id=control?"t49_rocket":Cards[scenario/3];
            if(scenario!=13 && scenario!=14 && scenario!=18){_floor=GameObject.CreatePrimitive(PrimitiveType.Cube);_floor.name="rollover ground";_floor.layer=BattleLayers.Static;_floor.transform.position=new Vector3(1000,-.5f,1000);_floor.transform.localScale=new Vector3(100,1,100);}
            _tank=BattleTank.Spawn(null,id,_db.Get(id),null,new Bounds(),0);_body=_tank.GetComponent<Rigidbody>();_rollover=_tank.GetComponent<TankRolloverPhysics>();
            if(gravity)GravityModeTank.Attach(_tank,null);
            var variant=scenario%3;var axis=variant==0?Vector3.right:Vector3.forward;var direction=variant==1?-1f:1f;
            var angle=variant==2?180f:155f*direction;
            _body.position=new Vector3(1000,scenario==13 || scenario==18?100:4,1000);_body.rotation=Quaternion.AngleAxis(angle,axis);_body.linearVelocity=Vector3.zero;
            _body.angularVelocity=variant==2?Vector3.zero:axis*(direction*.8f);
            if(scenario==12){_body.rotation=Quaternion.identity;_body.angularVelocity=Vector3.zero;}
            if(scenario==14){_body.useGravity=false;_body.position=new Vector3(1000,10,1000);_body.rotation=Quaternion.AngleAxis(180,Vector3.forward);_body.angularVelocity=Vector3.zero;_body.linearVelocity=Vector3.right*2;_wall=GameObject.CreatePrimitive(PrimitiveType.Cube);_wall.layer=BattleLayers.Static;_wall.transform.position=new Vector3(1004,10,1000);_wall.transform.localScale=new Vector3(1,50,100);}
            if(scenario==15)_tank.ReceiveDamage(new DamageInfo{Dmg=100000,DmgBase=100000});
            if(scenario==16)_tank.Blocked=true;
            if(scenario==17)_body.isKinematic=true;
            _tank.transform.SetPositionAndRotation(_body.position,_body.rotation);foreach(var p in _tank.Plates)p.SyncToMount();_tank.GetComponent<TankContactPhysics>().ResetState();Physics.SyncTransforms();
            _at=Time.time;_stableAt=-1;_previous=_body.rotation;_previousTick=Time.fixedTime;_maxStep=0;_path=0;_firstMotor=false;
        }
        private static void Tick()
        {
            if(!EditorApplication.isPlaying || _tank==null)return;
            try
            {
                if(DateTime.UtcNow>_deadline){_failures++;Finish();return;}
                if(Time.fixedTime!=_previousTick)
                {
                    var dt=Time.fixedTime-_previousTick;var step=Quaternion.Angle(_previous,_body.rotation);_maxStep=Mathf.Max(_maxStep,step);_path+=step;
                    CheckStep(step<=_body.maxAngularVelocity*dt*Mathf.Rad2Deg+3f,step,dt);
                    _previous=_body.rotation;_previousTick=Time.fixedTime;
                }
                if(!_firstMotor && _rollover.Active){_firstMotor=true;Check(Time.time-_at<4,"contact-driven start, no delayed reset case="+_case);}
                var up=Vector3.Dot(_body.rotation*Vector3.up,Vector3.up);
                if(_case<PositiveCases)
                {
                    var stable=up>.90f && _tank.Suspension.WheelHits>=2 && _body.angularVelocity.magnitude<1.2f;
                    if(stable && _stableAt<0)_stableAt=Time.time;if(!stable)_stableAt=-1;
                    if(_stableAt>=0 && Time.time-_stableAt>.6f){Check(_rollover.RolloverCount>0,"physical assist used case="+_case);Check(_path>100,"continuous tumble path case="+_case);CompleteCase();}
                    else if(Time.time-_at>9){Check(false,"failed to land on tracks case="+_case+" up="+up+" active="+_rollover.Active+" wheels="+_tank.Suspension.WheelHits);CompleteCase();}
                }
                else if(Time.time-_at>1.5f){Check(_rollover.RolloverCount==0 && _rollover.LastAngularAcceleration==Vector3.zero,"no assist upright / unsupported air / wall / wreck / blocked / kinematic / gravity air case="+_case);CompleteCase();}
            }
            catch(Exception e){Debug.LogException(e);_failures++;Finish();}
        }
        private static void CheckStep(bool ok,float step,float dt){if(!ok){_failures++;Debug.Log("[Rollover] FAIL orientation discontinuity case="+_case+" degrees="+step+" dt="+dt);}}
        private static void CompleteCase()
        {
            var text="case="+_case+" tank="+_tank.Card.Name+" gravity="+_tank.Suspension.AirControlEnabled+" elapsed="+(Time.time-_at)+" up="+Vector3.Dot(_body.rotation*Vector3.up,Vector3.up)+" path="+_path+" maxStep="+_maxStep+" starts="+_rollover.RolloverCount;
            Debug.Log("[Rollover] "+text);Results.Add(text);if(++_case<TotalCases)Begin();else Finish();
        }
        private static void Finish(){_tank=null;Directory.CreateDirectory("Logs");File.WriteAllLines("Logs/rollover-trajectories.txt",Results);Debug.Log("[Rollover] finished failures="+_failures+" errors="+_errors);SessionState.SetInt(Key+".result",_failures==0 && _errors==0?0:1);EditorApplication.isPlaying=false;}
    }
}
