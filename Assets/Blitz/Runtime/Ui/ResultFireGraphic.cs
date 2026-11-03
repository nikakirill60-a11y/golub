using System;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;
using UnityEngine.UI;
namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>uGUI flipbook equivalent of the source GPUParticles2D emitters; native textures and process values.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class ResultFireGraphic : MaskableGraphic
    {
        private Texture2D _texture;
        private Material _ownedMaterial;
        private bool _small;
        private float _start;
        private readonly float[] _scales=new float[8], _angular=new float[8];
        private readonly Vector2[] _origins=new Vector2[8];
        private static readonly Vector2[] Corners={new Vector2(-.5f,-.5f),new Vector2(-.5f,.5f),new Vector2(.5f,.5f),new Vector2(.5f,-.5f)};
        public float PreviewTime=1.35f;
        public override Texture mainTexture=>_texture;
        public static ResultFireGraphic Create(Transform parent,string name,float x,float y,bool small=false,bool centered=true,float scale=1)
        {
            var rt=UiFactory.Node(name,parent);rt.anchorMin=rt.anchorMax=new Vector2(centered?.5f:0,1);rt.pivot=new Vector2(.5f,.5f);rt.anchoredPosition=new Vector2(x,-y);rt.sizeDelta=new Vector2(1000,500);rt.localScale=Vector3.one*scale;
            var fire=rt.gameObject.AddComponent<ResultFireGraphic>();fire._small=small;fire._start=Time.unscaledTime;fire._texture=BlitzResources.LoadTexture(small?"result_fire2":"result_fire1");fire.raycastTarget=false;
            fire._ownedMaterial=new Material(Resources.Load<Shader>("ResultFire"));fire.material=fire._ownedMaterial;
            var hash=317;foreach(var c in name)hash=unchecked(hash*31+c);
            for(var i=0;i<8;i++)
            {
                var rng=new System.Random(unchecked(hash+i*71));
                fire._scales[i]=(small?.25f:.7f)+(float)rng.NextDouble()*(small?.25f:.5f);
                fire._angular[i]=(small?-50f:-194.46f)+(float)rng.NextDouble()*(small?100f:362.64f);
                if(small){var phi=(float)rng.NextDouble()*Mathf.PI*2;var radius=Mathf.Sqrt((float)rng.NextDouble())*30.8f;fire._origins[i]=new Vector2(Mathf.Cos(phi),Mathf.Sin(phi))*radius;}
            }
            fire.SetVerticesDirty();return fire;
        }
        private void Update()=>SetVerticesDirty();
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();if(_texture==null)return;
            var lifetime=_small?2f:1.8f;var frames=_small?18:14;var time=Application.isPlaying?Time.unscaledTime-_start:PreviewTime;
            var frameW=_texture.width/(float)frames;var frameH=_texture.height;
            for(var i=0;i<8;i++)
            {
                var birth=i*lifetime/8; if(time<birth)continue;
                var age=Mathf.Repeat(time-birth,lifetime);var progress=age/lifetime;
                var size=_scales[i];var angle=_angular[i]*age*Mathf.Deg2Rad;
                var pos=_origins[i]+new Vector2(0,.5f*(_small?40:48)*age*age);
                var anim=_small?Mathf.Min(.99999f,age):Mathf.Repeat(-age,1);var frame=Mathf.FloorToInt(anim*frames);
                var tint=Color.Lerp(new Color(1,.75f,0,1),new Color(.45f,0,0,1),Mathf.Clamp01(progress/.99336284f));
                var begin=mesh.currentVertCount;
                for(var j=0;j<4;j++){var p=Vector2.Scale(Corners[j],new Vector2(frameW,frameH))*size;var rotated=new Vector2(p.x*Mathf.Cos(angle)-p.y*Mathf.Sin(angle),p.x*Mathf.Sin(angle)+p.y*Mathf.Cos(angle));var v=UIVertex.simpleVert;v.position=pos+rotated;v.color=tint;v.uv0=new Vector2((frame+(j>1?1:0))/(float)frames,(j==1||j==2)?1:0);mesh.AddVert(v);}
                mesh.AddTriangle(begin,begin+1,begin+2);mesh.AddTriangle(begin+2,begin+3,begin);
            }
        }
        protected override void OnDestroy(){base.OnDestroy();if(_ownedMaterial!=null){if(Application.isPlaying)Destroy(_ownedMaterial);else DestroyImmediate(_ownedMaterial);}}
    }
}
