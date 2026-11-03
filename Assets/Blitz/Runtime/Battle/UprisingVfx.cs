using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using BlitzLegacy.Runtime.Ui;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Game-state mounts/lifetimes only. All artwork and animation are evaluated from original 11.20 SC2.</summary>
    [DisallowMultipleComponent]
    public sealed class UprisingVfx : MonoBehaviour
    {
        public static readonly Dictionary<string,string> Sources=new Dictionary<string,string>(StringComparer.Ordinal) {
            {"huntersMark","halloween_skills_DarkSkill"},{"curse","halloween2019_Spell_Bat_one"},
            {"vortex","halloween2019_Tornado"},{"splash","halloween2019_Splash_Halloween"},
            {"returnInTime","halloween2019_Time_RollBack"},{"moonwalk","halloween_skills_moonwalk"},
            {"moonwalk_out","halloween_skills_moonwalk_out"},{"berserk","halloween2019_Berserk"},
            {"damageBoost","halloween2019_DamageIncrease"},{"engineBoost","halloween2019_Dash"},
            {"reloadBoost","halloween2019_Reload_Halloween"},{"lightning","halloween2019_ChainLightning"}
        };
        private UprisingTank _owner;
        private MeshFilter _hull,_turret,_trackLeft,_trackRight;
        private readonly Dictionary<string,Image> _badges=new Dictionary<string,Image>();
        private readonly List<Sprite> _badgeSprites=new List<Sprite>();private Rigidbody _body;
        private readonly Dictionary<string,DavaFx> _held=new Dictionary<string,DavaFx>();
        private string _active="";
        private bool _cloak,_fakeWreck,_hudFakeDead;
        public static UprisingVfx Attach(UprisingTank owner)
        {
            var c=owner.gameObject.AddComponent<UprisingVfx>();c._owner=owner;c._body=owner.GetComponent<Rigidbody>();
            if(owner.Tank.Model!=null)foreach(var mesh in owner.Tank.Model.GetComponentsInChildren<MeshFilter>(true)){if(mesh.name.StartsWith("hull",StringComparison.OrdinalIgnoreCase))c._hull=mesh;else if(mesh.name.StartsWith("turret",StringComparison.OrdinalIgnoreCase))c._turret=mesh;else if(mesh.name=="trackL")c._trackLeft=mesh;else if(mesh.name=="trackR")c._trackRight=mesh;}
            owner.EffectRequested+=c.Request;owner.ChainRequested+=c.Chain;owner.Respawned+=c.Respawn;
            return c;
        }
        private bool Visible {get {return _owner.Tank==_owner.Session.Player || _owner.Tank.Team==_owner.Session.Player.Team || (_owner.Tank.Spotted && !_owner.Concealed);}}
        private DavaFx Spawn(string id,Transform parent,bool sustain,float duration)
        {
            string source;if(!Sources.TryGetValue(id,out source))return null;
            var fx=DavaFx.Create(parent,source,duration,sustain);
            fx.ViewCamera=_owner.Session.View?.Active;return fx;
        }
        private void Request(string id,Vector3 point)
        {
            if(!Visible || id=="berserk" || id=="pretendDead" || id=="moonwalk" || id=="curse" || id=="huntersMark")return;
            var fx=Spawn(id,transform,false,id=="returnInTime"?2:1);
            if(fx!=null && id=="splash")fx.transform.position=point;
        }
        private void Chain(Vector3 from,Vector3 to)
        {
            if(!Visible)return;
            var fx=Spawn("lightning",transform,false,.15f);fx.SetStripeEndpoints(from,to);
        }
        private void Respawn(){Clear();if(Visible)Spawn("moonwalk",transform,false,1);}
        private void Hold(string slot,string id,bool wanted,Transform parent=null)
        {
            DavaFx fx;_held.TryGetValue(slot,out fx);
            if(!wanted){if(fx!=null)fx.Stop();_held.Remove(slot);return;}
            if(fx==null){fx=Spawn(id,parent!=null?parent:transform,true,1);_held[slot]=fx;}
            fx.ViewCamera=_owner.Session.View?.Active;
        }
        private void LateUpdate()
        {
            if(_owner==null)return;var tank=_owner.Tank;var s=_owner.State;
            SyncHudDeath();
            if(tank.Dead || !_owner.Session.Started || !Visible){Clear();return;}
            var active=s.Running(Time.time)?s.ActiveId:"";
            if((active=="pretendDead")!=_fakeWreck){_fakeWreck=active=="pretendDead";tank.SetFakeWreck(_fakeWreck);}
            // A wreck carries no ability auras or status badges: an enemy that
            // believes Play Dead sees nothing but the wreck itself.
            if(_fakeWreck && tank.LooksDeadTo(_owner.Session.Player)){StopHeld();return;}
            if(_active=="moonwalk" && active!="moonwalk")Spawn("moonwalk_out",transform,false,1);
            _active=active;
            Hold("active",active,active=="moonwalk" || active=="berserk");
            Hold("curse","curse",_owner.HasStatus("curse"));Hold("mark","huntersMark",_owner.HasStatus("huntersMark"));
            Hold("damage","damageBoost",s.Has("damageBoost1") || s.Has("damageBoost2"));
            BodyMount("damage");BodyMount("curse");BodyMount("mark");Badge("huntersMark",_owner.HasStatus("huntersMark"));Badge("curse",_owner.HasStatus("curse"));
            var gun=tank.Turret?.GunPivot;
            Hold("reload","reloadBoost",gun!=null && (s.Has("reloadBoost1") || s.Has("reloadBoost2")),gun);
            DavaFx reload;if(_held.TryGetValue("reload",out reload) && reload!=null){var radius=Mathf.Clamp(tank.GunInfo.Caliber/320f,.2f,.55f);reload.transform.localRotation=Quaternion.Euler(0,180,0);reload.transform.localScale=new Vector3(radius,radius,Mathf.Max(.25f,tank.Turret.GunMeshLength)/5.5f);reload.VisualScale=reload.transform.localScale;reload.Visible=tank!=_owner.Session.Player || !_owner.Session.View.SniperMode;}
            var boost=s.Has("engineBoost1") || s.Has("engineBoost2");
            for(var i=-1;i<=1;i+=2){var slot=i<0?"track-1":"track1";Hold(slot,"engineBoost",boost);DavaFx fx;if(_held.TryGetValue(slot,out fx) && fx!=null){var origin=new Vector3(i*tank.HullWidth*.5f,.05f,0);var mesh=i<0?_trackLeft:_trackRight;if(mesh!=null && mesh.sharedMesh!=null){var bounds=mesh.sharedMesh.bounds;var centre=bounds.center;centre.y=bounds.min.y+.05f;origin=transform.InverseTransformPoint(mesh.transform.TransformPoint(centre));}fx.transform.localPosition=origin;var velocity=tank.Suspension.SpeedMs+(_body!=null?Vector3.Dot(_body.angularVelocity,transform.up)*origin.x:0);fx.SetVariable("Velosity",Mathf.Abs(velocity)/Mathf.Max(1,_owner.BaseForwardSpeed/3.6f));}}
            if(_cloak && s.CloakUntil<=Time.time)Spawn("moonwalk_out",transform,false,1);_cloak=s.CloakUntil>Time.time;
        }
        /// <summary>
        /// The enemy team list follows Play Dead like a real kill and comes
        /// back when the act ends. A real death is reported by BattleSession.
        /// </summary>
        private void SyncHudDeath()
        {
            var session=_owner.Session;var tank=_owner.Tank;
            var fake=!tank.Dead && session.Player!=null && tank.LooksDeadTo(session.Player);
            if(fake==_hudFakeDead)return;_hudFakeDead=fake;
            if(session.Hud==null || (!fake && tank.Dead))return;
            var enemySide=session.Roster!=null && tank.Team!=session.Roster.PlayerTeam;
            session.Hud.SetTeamTankAlive(enemySide,(enemySide?session.Enemies:session.Allies).IndexOf(tank),!fake);
        }
        private void StopHeld(){foreach(var badge in _badges.Values)if(badge!=null)badge.gameObject.SetActive(false);foreach(var fx in _held.Values)if(fx!=null)fx.Stop();_held.Clear();}
        private void Badge(string id,bool wanted)
        {
            Image badge;_badges.TryGetValue(id,out badge);if(!wanted){if(badge!=null)badge.gameObject.SetActive(false);return;}
            if(badge==null){var root=UiFactory.Node("badge_"+id,transform);root.sizeDelta=Vector2.one;var canvas=root.gameObject.AddComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;var texture=Resources.Load<Texture2D>("Uprising/Icons/"+id);var sprite=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f),100);_badgeSprites.Add(sprite);badge=root.gameObject.AddComponent<Image>();badge.sprite=sprite;badge.raycastTarget=false;_badges[id]=badge;}
            var above=transform.position+Vector3.up*(_owner.Tank.Hull.Height+2.5f);if(_turret!=null && _turret.sharedMesh!=null){var bounds=_turret.sharedMesh.bounds;above=_turret.transform.TransformPoint(new Vector3(bounds.center.x,bounds.max.y,bounds.center.z))+Vector3.up*.7f;}
            badge.transform.position=above+Vector3.right*(id=="huntersMark"?-.55f:.55f);var camera=_owner.Session.View?.Active;if(camera!=null)badge.transform.rotation=camera.transform.rotation;badge.gameObject.SetActive(Visible && (_owner.Tank!=_owner.Session.Player || !_owner.Session.View.SniperMode));
        }
        private void BodyMount(string slot)
        {
            DavaFx fx;if(!_held.TryGetValue(slot,out fx) || fx==null)return;var tank=_owner.Tank;
            var bounds=_hull!=null && _hull.sharedMesh!=null?_hull.sharedMesh.bounds:new Bounds(new Vector3(0,tank.Hull.Height*.5f,0),new Vector3(tank.HullWidth,tank.Hull.Height,tank.HullLength));
            var fit=Mathf.Clamp(Mathf.Max(bounds.size.x/3.5f,bounds.size.z/7),.45f,1.25f);
            fx.transform.position=_hull!=null?_hull.transform.TransformPoint(bounds.center):transform.TransformPoint(bounds.center);fx.transform.localScale=Vector3.one*fit;fx.VisualScale=Vector3.one*fit;fx.Visible=tank!=_owner.Session.Player || !_owner.Session.View.SniperMode;
        }
        private void Clear(){foreach(var badge in _badges.Values)if(badge!=null)badge.gameObject.SetActive(false);foreach(var fx in _held.Values)if(fx!=null)fx.Stop();_held.Clear();_active="";_cloak=false;if(_fakeWreck){_fakeWreck=false;_owner.Tank.SetFakeWreck(false);}}
        private void OnDestroy(){foreach(var sprite in _badgeSprites)if(sprite!=null){if(Application.isPlaying)Destroy(sprite);else DestroyImmediate(sprite);}
if(_owner!=null){_owner.EffectRequested-=Request;_owner.ChainRequested-=Chain;_owner.Respawned-=Respawn;}Clear();}
    }
}
