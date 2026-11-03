using System;
using System.IO;
using System.Reflection;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object=UnityEngine.Object;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Deterministic source-contract renders and a real Play Mode death/return smoke.
    /// Generated captures are evidence of this port, not original-client reference captures.</summary>
    [InitializeOnLoad]
    public static class BigBossCli
    {
        private const string Key="Blitz.BigBossSmoke";
        private static BattleSession _session;
        private static DateTime _deadline;
        private static int _stage,_errors,_life,_points;
        private static float _at,_pullStart;
        private static bool _respawnSeen;
        private static BattleTank _victim;
        private static Vector3 _trap;
        private static bool _hooked;
        static BigBossCli(){if(SessionState.GetBool(Key,false))Hook();}
        public static void Smoke()
        {
            SessionState.SetBool(Key,true);SessionState.SetInt(Key+".result",-1);Hook();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);EditorApplication.isPlaying=true;
        }
        private static void Hook()
        {
            if(_hooked)return;_hooked=true;_deadline=DateTime.UtcNow.AddMinutes(3);
            EditorApplication.playModeStateChanged+=Changed;EditorApplication.update+=Tick;Application.logMessageReceived+=Message;
        }
        private static void Message(string message,string stack,LogType type){if(type==LogType.Error || type==LogType.Exception)_errors++;}
        private static void Changed(PlayModeStateChange state)
        {
            if(state==PlayModeStateChange.EnteredPlayMode)EditorApplication.delayCall+=Prepare;
            else if(state==PlayModeStateChange.EnteredEditMode){var result=SessionState.GetInt(Key+".result",-1);if(result>=0)Finish(result);}
        }
        private static void Prepare()
        {
            try
            {
                // A hangar loadout with adrenaline in the third slot, which Big Boss must show like every mode.
                var save=new TankSaveData{Name="t-34"};save.ShellsAmounts.Clear();
                save.Consumables.AddRange(new[]{ConsumableCatalog.RepairKit,ConsumableCatalog.Afterburning,ConsumableCatalog.Berserk});
                _session=BattleSession.Create(null,false,tankId:"t-34",autoStart:true,playerTank:save,mode:BattleMode.BigBoss);
                foreach(var bot in _session.Bots)bot.Activate(false);
                foreach(var tank in _session.BattleTanks())typeof(BigBossTank).GetField("_aiAt",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(tank.BigBoss,float.PositiveInfinity);
                Require(_session.Allies.Count==7 && _session.Enemies.Count==7,"7v7 composition");
                Require(_session.Hud.Root.GetComponentInChildren<BigBossBattleUi>()!=null,"Big Boss HUD attached");
                Require(_session.Controls.Consumables.gameObject.activeInHierarchy,"support column visible");
                Require(_session.Controls.Consumables.GetIcon(0).Consumable.Info.Id==ConsumableCatalog.RepairKit,"repair slot bound");
                Require(_session.Controls.Consumables.GetIcon(1).Consumable.Info.Id==ConsumableCatalog.Afterburning,"boost slot bound");
                Require(ThirdSlotShowsAdrenaline(),"hangar adrenaline in the third slot");
                foreach(var bot in _session.Bots)Require(bot.Tank.ActiveConsumables.Count==2,"bot support pair");
                _life=_session.Player.BigBoss.State.LifeSerial;_points=_session.BigBoss.Match.TeamA;
                Require(_session.Controls.Consumables.ModeIcon!=null,"role ability in the fourth slot");
                _session.Player.SetShellAmounts(new[]{0,0,0});Kill(_session.Player);_at=Time.time;_stage=1;_respawnSeen=false;
                Require(_session.Started && _session.Player.BigBoss.State.Respawning,"death waits for return without elimination");
            }
            catch(Exception e){Debug.LogException(e);Done(1);}
        }
        private static bool ThirdSlotShowsAdrenaline()
        {
            var icon=_session.Controls.Consumables.GetIcon(2);
            return icon!=null && icon.Consumable!=null && icon.Consumable.Info.Id==ConsumableCatalog.Berserk && icon.IconImage.enabled && icon.IconImage.sprite!=null;
        }
        private static void Kill(BattleTank tank){tank.ReceiveDamage(new DamageInfo{Dmg=tank.Health.MaxHealth*10,DmgBase=tank.Health.MaxHealth*10,DmgType=DamageType.Ability});}
        private static void Tick()
        {
            if(DateTime.UtcNow>_deadline){Debug.LogError("[Blitz] Big Boss smoke deadline exceeded");Done(1);return;}
            if(!EditorApplication.isPlaying || _session==null || _stage==0)return;
            try
            {
                if(_stage==1 && !_respawnSeen && Time.time-_at>=3)
                {
                    var ui=_session.Hud.Root.GetComponentInChildren<BigBossBattleUi>();var state=_session.Player.BigBoss.State;
                    var cards=ui.transform.Find("RespawnTimer/content/Abilities");var next=state.NextRoleId>=0?state.NextRoleId:state.RoleId;
                    Require(ui.RespawnAlpha>.99f && cards!=null && cards.childCount==state.Rules.Abilities(next,state.Rules.IsBoss(next)?1:state.Level).Length,"respawn screen presents the next role");
                    _respawnSeen=true;
                }
                if(_stage==1 && Time.time-_at>=8.5f)
                {
                    var tank=_session.Player;Require(!tank.Dead,"player returned through live Update scheduler");
                    Require(tank.BigBoss.State.LifeSerial==_life+1,"life identity advanced exactly once");Require(tank.Health.Health==tank.Health.MaxHealth,"full return HP");Require(tank.AvailableShellCount>0,"ammunition restored");
                    Require(_session.View.Tank==tank && _session.Hud.CombatVisible,"camera and combat HUD restored");
                    Require(_session.Controls.Consumables.gameObject.activeInHierarchy,"support column restored");
                    Require(ThirdSlotShowsAdrenaline(),"hangar adrenaline still in the third slot after the return");
                    Require(_session.Controls.Consumables.ModeIcon.gameObject.activeInHierarchy && _session.Controls.Consumables.ModeIcon.IconImage.enabled,"role ability slot visible after live HUD update");
                    Require(_session.BigBoss.Match.TeamA<_points,"ticket cost applied");
                    // Graviton: an idle enemy 30 m from a fresh trap must be dragged most of the way in.
                    SetRole(tank.BigBoss,6,1);_victim=null;
                    foreach(var enemy in _session.Enemies)if(!enemy.Dead && (_victim==null || enemy.Spotted))_victim=enemy;
                    _trap=_victim.transform.position+_victim.transform.forward*30;
                    _session.BigBoss.AddTrap(tank.BigBoss,tank.BigBoss.FamilyAbility("gravityShot"),_trap);
                    _pullStart=Flat(_victim.transform.position-_trap);_at=Time.time;_stage=2;
                }
                else if(_stage==2 && Time.time-_at>=6.5f)
                {
                    var tank=_session.Player;var end=Flat(_victim.transform.position-_trap);
                    Debug.Log("[Blitz] Big Boss graviton pull: "+_pullStart.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+" m -> "+end.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+" m in 6.5 s");
                    Require(end<_pullStart*.5f,"graviton trap drags an idle enemy at least half way");
                    Kill(tank);Require(_session.Started,"second death still not elimination");
                    for(var i=0;i<40;i++){_session.BigBoss.Match.Death(1000+i,0,0,false);_session.BigBoss.Match.Death(2000+i,0,1,false);}
                    _session.Roster.Tick();Require(!_session.Started && _session.Roster.WinnerTeam==-1,"simultaneous ticket exhaustion draw");
                    Debug.Log("[Blitz] Big Boss live smoke passed: 7v7, return, ammo, HUD, repeated deaths, ticket draw");Done(_errors==0?0:1);
                }
            }
            catch(Exception e){Debug.LogException(e);Done(1);}
        }
        private static float Flat(Vector3 v){v.y=0;return v.magnitude;}
        private static void Require(bool condition,string label){if(!condition)throw new InvalidOperationException("Big Boss smoke: "+label);}
        private static void Done(int code)
        {
            _stage=0;SessionState.SetInt(Key+".result",code);if(_session!=null){Object.Destroy(_session.gameObject);_session=null;}
            if(EditorApplication.isPlaying)EditorApplication.isPlaying=false;else Finish(code);
        }
        private static void Finish(int code)
        {
            SessionState.SetBool(Key,false);EditorApplication.update-=Tick;EditorApplication.playModeStateChanged-=Changed;Application.logMessageReceived-=Message;_hooked=false;EditorApplication.Exit(code);
        }
        private static object Invoke(object value,string method,params object[] args){return value.GetType().GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(value,args);}
        private static void SetRole(BigBossTank tank,int role,int level)
        {
            var state=new BigBossState(tank.State.Rules,role);if(level>1)state.Reward((int)(tank.State.Rules.Data["levels"][level-1].AsDouble()/tank.State.Rules.P("rolePointsPerDamage"))+1);
            typeof(BigBossTank).GetProperty("State").SetValue(tank,state);Invoke(tank,"ApplyModifiers",true);tank.Begin();
        }
        public static void Render()
        {
            var errors=0;Application.logMessageReceived+=(m,s,t)=>{if(t==LogType.Error || t==LogType.Exception)errors++;};
            try
            {
                BigBossTextureImport.Apply();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);Directory.CreateDirectory("Logs/bigboss-render");
                var menu=BattleModeScreen.Create(null,BattleMode.BigBoss,null);var camera=new GameObject("CaptureCamera").AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.16f,.2f);
                CaptureUi(camera,"mode-menu");Object.DestroyImmediate(menu.gameObject);Object.DestroyImmediate(camera.gameObject);
                var session=BattleSession.Create(null,false,tankId:"t-34",autoStart:true,mode:BattleMode.BigBoss);var ui=session.Hud.Root.GetComponentInChildren<BigBossBattleUi>();
                foreach(var role in new[]{1,2,4,5,6})foreach(var level in new[]{1,5})
                {
                    SetRole(session.Player.BigBoss,role,level);Invoke(ui,"Refresh");CaptureUi(session.View.Active,"role-"+role+"-level-"+level);
                    ui.ToggleDetails();CaptureUi(session.View.Active,"details-"+role+"-level-"+level);ui.ToggleDetails();
                }
                // Respawn presentation of the next ordinary role (alpha forced: Edit Mode time is frozen).
                SetRole(session.Player.BigBoss,4,3);Kill(session.Player);Invoke(ui,"Refresh");
                ui.transform.Find("RespawnTimer").GetComponent<CanvasGroup>().alpha=1;CaptureUi(session.View.Active,"respawn-next-role");
                Object.DestroyImmediate(session.gameObject);
                camera=new GameObject("FxCaptureCamera").AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.16f,.2f);camera.transform.position=new Vector3(0,5,-14);camera.transform.LookAt(new Vector3(0,1,0));camera.nearClipPlane=.01f;
                var manifest=JsonResources.Load("Data/big_boss_fx_1120");
                foreach(var pair in manifest["effects"].Fields)
                {
                    var name=pair.Value["resource"].AsString().Replace("BigBoss/Sources/","");var fx=DavaFx.CreateBigBoss(null,name,3);
                    var clock=0f;foreach(var time in new[]{.1f,.35f,.75f,1.2f})
                    {while(clock<time){fx.Simulate(1f/60);clock+=1f/60;}fx.Render(camera);Capture(camera,"fx-"+name+"-"+time.ToString("F2",System.Globalization.CultureInfo.InvariantCulture));}
                    Object.DestroyImmediate(fx.gameObject);
                }
                RenderRailgun(camera);
                Object.DestroyImmediate(camera.gameObject);Debug.Log("[Blitz] Big Boss render completed; errors="+errors);EditorApplication.Exit(errors==0?0:1);
            }
            catch(Exception e){Debug.LogException(e);EditorApplication.Exit(1);}
        }
        /// <summary>The railgun discharge as a battle lays it: muzzle at the origin, shot along +Z,
        /// the beam end riding an 800 m/s shell that lands 90 m out. The scope views sit on the gun
        /// pivot five metres behind the muzzle, looking down a shot that dispersion bent slightly.</summary>
        private static void RenderRailgun(Camera camera)
        {
            var bent=Quaternion.Euler(-.2f,.15f,0)*Vector3.forward;
            var views=new[]{("rear",new Vector3(1.5f,4,-12),new Vector3(0,1,40),60f,Vector3.forward,false),("side",new Vector3(45,8,35),new Vector3(0,0,35),60f,Vector3.forward,false),
                ("scope",new Vector3(0,0,-5),new Vector3(0,0,100),40f,bent,true),("scope-zoom",new Vector3(0,0,-5),new Vector3(0,0,100),10f,bent,true)};
            foreach(var (view,position,target,fov,direction,scope) in views)
            {
                var fx=DavaFx.CreateBigBoss(null,BigBossVfx.RailgunScene,2);BigBossVfx.AimRailgun(fx,Vector3.zero,direction,Vector3.zero,scope);
                camera.transform.position=position;camera.transform.LookAt(target);camera.fieldOfView=fov;
                var start=scope?BigBossVfx.RailgunScopeClearance:0;var clock=0f;foreach(var time in new[]{.05f,.15f,.4f,1f,1.6f})
                {
                    while(clock<time){clock+=1f/60;fx.Simulate(1f/60);BigBossVfx.StretchRailgun(fx,Vector3.zero,direction,direction*Mathf.Min(800*clock,90),start);}
                    fx.Render(camera);Capture(camera,"railgun-"+view+"-"+time.ToString("F2",System.Globalization.CultureInfo.InvariantCulture));
                }
                Object.DestroyImmediate(fx.gameObject);
            }
            camera.fieldOfView=60;
        }
        private static void CaptureUi(Camera camera,string name)
        {
            foreach(var canvas in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))if(canvas.isRootCanvas && canvas.renderMode==RenderMode.ScreenSpaceOverlay){canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=camera.nearClipPlane+.01f;}
            Canvas.ForceUpdateCanvases();Capture(camera,name);
        }
        private static void Capture(Camera camera,string name)
        {
            var target=new RenderTexture(1196,540,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);var image=new Texture2D(1196,540,TextureFormat.RGB24,false,false);var previous=RenderTexture.active;
            try{camera.targetTexture=target;for(var i=0;i<3;i++)camera.Render();RenderTexture.active=target;image.ReadPixels(new Rect(0,0,1196,540),0,0);image.Apply();File.WriteAllBytes("Logs/bigboss-render/"+name+".png",image.EncodeToPNG());}
            finally{camera.targetTexture=null;RenderTexture.active=previous;Object.DestroyImmediate(image);target.Release();Object.DestroyImmediate(target);}
        }
    }
}
