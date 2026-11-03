using BlitzLegacy.Core.Audio;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Decoded original Wwise media, routed through the existing SFX bus and user volume controls.</summary>
    public sealed class UprisingAudio : MonoBehaviour
    {
        private UprisingTank _owner;private AudioSource _loop;private bool _berserk;private float _loopAt;
        public static void Attach(UprisingTank owner){var c=owner.gameObject.AddComponent<UprisingAudio>();c._owner=owner;owner.EffectRequested+=c.PlayEvent;owner.ChainRequested+=c.Chain;owner.Respawned+=c.Respawn;}
        private bool Visible {get {return _owner.Tank.Team==_owner.Session.Player.Team || (_owner.Tank.Spotted && !_owner.Concealed);}}
        private AudioSource Play(string id,bool loop=false,float volume=1)
        {
            var audio=BlitzBootstrap.Current?.Audio;if(!Application.isPlaying || audio==null || !Visible)return null;
            var clip=Resources.Load<AudioClip>("Uprising/Audio/"+id);if(clip==null)return null;
            var source=audio.PlaySound2D(clip,AudioBus.Sfx,loop,volume);
            if(source!=null && _owner.Tank!=_owner.Session.Player){AudioService.ConfigureSpatial(source,SpatialAudio.UprisingCue);source.transform.position=transform.position;}
            return source;
        }
        private void PlayEvent(string id,Vector3 point){if((id=="curse" || id=="huntersMark") && !_owner.HasStatus(id))return;if(id=="berserk")Play("berserk_on_"+Random.Range(1,4),false,.63f);else Play(id);}
        private void Chain(Vector3 from,Vector3 to){Play("lightning");}
        private void Respawn(){Play("moonwalk");}
        private void Update()
        {
            if(_owner==null)return;
            var active=_owner.Session.Started && !_owner.Tank.Dead && _owner.State.ActiveId=="berserk" && _owner.State.Running(Time.time);
            if(active && !_berserk)_loopAt=Time.time+1;
            if(active && _loop==null && Time.time>=_loopAt)_loop=Play("berserk_loop",true,.63f);
            if(!active && _berserk){StopLoop();if(!_owner.Tank.Dead && _owner.Session.Started)Play("berserk_off");}
            if(_loop!=null){if(!Visible)StopLoop();else if(_loop.spatialBlend>0)_loop.transform.position=transform.position;}
            _berserk=active;
        }
        private void StopLoop(){if(_loop==null)return;var audio=BlitzBootstrap.Current?.Audio;if(audio!=null)audio.StopSound2D(_loop);else Destroy(_loop.gameObject);_loop=null;}
        private void OnDestroy(){StopLoop();if(_owner!=null){_owner.EffectRequested-=PlayEvent;_owner.ChainRequested-=Chain;_owner.Respawned-=Respawn;}}
    }
}
