using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Real projectile/rack/magazine/audio requests and native PCM, not mocked shoot events.</summary>
    [InitializeOnLoad]
    public static class UprisingDupletCli
    {
        private const string Key="Blitz.UprisingDuplet";
        private static readonly string[] Cards={"g190_vk_1602_quby","m-6-y","a150_mbt_b","s16_kranvagn","ms-1"};
        private static readonly List<string> Results=new List<string>();
        private static readonly List<float> Pcm=new List<float>();
        private static BattleSession _session;
        private static AudioService _audio;
        private static TankSoundManager _sounds;
        private static BattleTank _tank;
        private static int _case,_shots,_triggers,_baseRounds,_capacity,_errors,_failures,_lastFrame=-1;
        private static int[] _volleyShots;
        private static float _at,_lastShot;
        private static bool _hooked,_duplet,_sniper,_recording;
        private static DateTime _deadline;
        private static int _channels;
        static UprisingDupletCli(){if(SessionState.GetBool(Key,false))Hook();}
        public static void Run(){BeginRun(false);}
        public static void RunBaseline(){BeginRun(true);}
        private static void BeginRun(bool baseline)
        {
            SessionState.SetBool(Key,true);SessionState.SetBool(Key+".baseline",baseline);
            SessionState.SetInt(Key+".result",-1);Hook();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            EditorApplication.isPlaying=true;
        }
        private static void Hook()
        {
            if(_hooked)return;_hooked=true;_deadline=DateTime.UtcNow.AddMinutes(8);
            Application.logMessageReceived+=(m,s,t)=>{if(t==LogType.Error || t==LogType.Exception)_errors++;};
            EditorApplication.update+=Tick;
            EditorApplication.playModeStateChanged+=state=>{
                if(state==PlayModeStateChange.EnteredPlayMode)EditorApplication.delayCall+=BeginCase;
                if(state==PlayModeStateChange.EnteredEditMode && SessionState.GetInt(Key+".result",-1)>=0){
                    SessionState.SetBool(Key,false);EditorApplication.Exit(SessionState.GetInt(Key+".result",1));}};
        }
        private static void Check(bool ok,string what)
        {
            if(!ok)_failures++;
            Debug.Log("[Duplet] "+(ok?"PASS ":"FAIL ")+what+" case="+_case);
        }
        private static void BeginCase()
        {
            try
            {
                if(_recording){AudioRenderer.Stop();_recording=false;}
                if(_session!=null)UnityEngine.Object.DestroyImmediate(_session.gameObject);
                _duplet=(_case%4&1)!=0;_sniper=(_case%4&2)!=0;
                _session=BattleSession.Create(null,false,tankId:Cards[_case/4],mode:BattleMode.Uprising);
                foreach(var t in _session.GetComponentsInChildren<BattleTank>()){
                    var ai=t.GetComponent<AiTankController>();if(ai!=null)ai.enabled=false;
                    var b=t.GetComponent<Rigidbody>();b.constraints=RigidbodyConstraints.FreezeAll;
                    if(t!=_session.Player)t.GetComponent<UprisingTank>().enabled=false;
                }
                _tank=_session.Player;_tank.SetShellAmounts(new[]{300,0,0});_tank.SelectShell(0);
                var c=_tank.GetComponent<UprisingTank>();if(_duplet)Check(c.State.Grant("duplet"),"grant ability");
                var gun=_tank.Gun as BattleGunBurst;
                _capacity=gun!=null?gun.MagazineSize:1;
                _baseRounds=gun!=null?_tank.GunInfo.Burst.Bursts.Sum():1;
                _volleyShots=new int[_capacity];_shots=_triggers=0;
                _tank.ProjectileFired+=ActualShot;
                _audio=AudioService.Create(_session.transform,new SettingsData());
                _sounds=TankSoundManager.Attach(_tank,_audio,true);
                _sounds.EnginePlayer.VolumeMul=_sounds.EnginePlayer.VolumeMulInit=0;
                _sounds.TracksPlayer.VolumeMul=_sounds.TracksPlayer.VolumeMulInit=0;
                _sounds.EnginePlayer.ApplyVolume();_sounds.TracksPlayer.ApplyVolume();
                _session.View.SetSniperMode(_sniper);_audio.MuffleSfx(_sniper);_sounds.SetSniperMode(_sniper);
                _at=Time.time;_lastShot=_at;_lastFrame=-1;Pcm.Clear();
                // Capture uses the real Unity mixer. Restore realtime capture on exit.
                Time.captureFramerate=60;_channels=Channels(AudioSettings.speakerMode);
                _recording=AudioRenderer.Start();Check(_recording,"native audio capture started");
                if(_case/4==0)Check(_baseRounds==15 && _capacity==3 && _tank.GunInfo.Burst.Bursts.All(x=>x==5),"authored VK 15-round / 5-round salvo");
            }
            catch(Exception e){Debug.LogException(e);_failures++;Finish();}
        }
        private static int Channels(AudioSpeakerMode mode)
        {
            switch(mode){case AudioSpeakerMode.Mono:return 1;case AudioSpeakerMode.Quad:return 4;
                case AudioSpeakerMode.Surround:return 5;case AudioSpeakerMode.Mode5point1:return 6;
                case AudioSpeakerMode.Mode7point1:return 8;default:return 2;}
        }
        private static void ActualShot(bool first,int slot)
        {
            _shots++;_lastShot=Time.time;
            if(_triggers>0 && _triggers<=_volleyShots.Length)_volleyShots[_triggers-1]++;
        }
        private static void Tick()
        {
            if(!EditorApplication.isPlaying || _tank==null || _lastFrame==Time.frameCount)return;
            _lastFrame=Time.frameCount;
            try
            {
                if(DateTime.UtcNow>_deadline || Time.time-_at>35){Check(false,"deadline");Finish();return;}
                if(_recording)
                {
                    var samples=AudioRenderer.GetSampleCountForCaptureFrame();
                    if(samples>0){using(var buffer=new NativeArray<float>(samples*_channels,Allocator.Temp)){
                        CheckRender(AudioRenderer.Render(buffer));for(var i=0;i<buffer.Length;i++)Pcm.Add(buffer[i]);}}
                }
                // Let the sniper bus finish its authored 0.3-second fade before the first shot.
                if(_triggers<_capacity && Time.time-_at>.5f && _tank.Gun.CanShoot()){
                    _triggers++;_tank.Gun.ShootAttempt();}
                var burst=_tank.Gun as BattleGunBurst;
                if(_triggers==_capacity && (burst==null || !burst.Bursting) && Time.time-_lastShot>.65f)CompleteCase();
            }
            catch(Exception e){Debug.LogException(e);_failures++;Finish();}
        }
        private static void CheckRender(bool ok){if(!ok)throw new InvalidOperationException("Native audio render failed");}
        private static void CompleteCase()
        {
            var multiplier=_duplet?2:1;
            Check(_shots==_baseRounds*multiplier,"actual rounds expected="+_baseRounds*multiplier+" got="+_shots);
            for(var i=0;i<_capacity;i++){
                var n=(_tank.Gun as BattleGunBurst)!=null?_tank.GunInfo.Burst.Bursts[i]:1;
                Check(_volleyShots[i]==n*multiplier,"salvo="+i+" expected="+n*multiplier+" got="+_volleyShots[i]);}
            Check(_tank.ShellAmounts[0]==300-_shots,"debit every actual round");
            var clip=_tank.Gun as BattleGunBurst;
            if(clip!=null)Check(clip.Bursts.Count==0,"extra rounds do not consume extra magazine slots");
            Check(_sounds.GunPlayer.PlayCount==_shots,"outside attacks expected="+_shots+" got="+_sounds.GunPlayer.PlayCount);
            Check(_sounds.GunPlayerInside.PlayCount==_shots,"inside requests expected="+_shots+" got="+_sounds.GunPlayerInside.PlayCount);
            Check(_sniper?_sounds.GunPlayerInside.Source.volume>0:_sounds.GunPlayerInside.Source.volume==0,"correct sniper bus gain");
            Check(Pcm.Count>0 && Pcm.Any(x=>Mathf.Abs(x)>.0001f),"non-silent native mixer PCM");
            var phase=SessionState.GetBool(Key+".baseline",false)?"baseline":"fixed";
            var file=phase+"-"+Cards[_case/4]+"-"+(_sniper?"sniper":"normal")+"-"+(_duplet?"duplet":"single")+".wav";
            Directory.CreateDirectory("Logs/duplet-audio");WriteWav("Logs/duplet-audio/"+file);
            var result="case="+_case+" tank="+Cards[_case/4]+" sniper="+_sniper+" duplet="+_duplet+" actual="+_shots+" volleys="+string.Join(",",_volleyShots)+" reports="+_sounds.GunPlayer.PlayCount+" pcm="+Pcm.Count;
            Debug.Log("[Duplet] "+result);Results.Add(result);
            if(++_case<Cards.Length*4)BeginCase();else Finish();
        }
        private static void WriteWav(string path)
        {
            using(var w=new BinaryWriter(File.Create(path))){var bytes=Pcm.Count*2;
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));w.Write(36+bytes);w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);w.Write((short)1);w.Write((short)_channels);var rate=AudioSettings.outputSampleRate;
                w.Write(rate);w.Write(rate*_channels*2);w.Write((short)(_channels*2));w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data"));w.Write(bytes);
                foreach(var sample in Pcm)w.Write((short)(Mathf.Clamp(sample,-1,1)*32767));}
        }
        private static void Finish()
        {
            if(_recording){AudioRenderer.Stop();_recording=false;}Time.captureFramerate=0;_tank=null;
            var baseline=SessionState.GetBool(Key+".baseline",false);
            Directory.CreateDirectory("Logs");File.WriteAllLines("Logs/duplet-"+(baseline?"baseline":"fixed")+".txt",Results);
            Debug.Log("[Duplet] finished baseline="+baseline+" failures="+_failures+" errors="+_errors);
            SessionState.SetInt(Key+".result",_errors==0 && (baseline || _failures==0)?0:1);EditorApplication.isPlaying=false;
        }
    }
}
