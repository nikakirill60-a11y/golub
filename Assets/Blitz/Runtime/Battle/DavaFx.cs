using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Telemetry;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Source-driven DAVA 11.20 FX renderer. Owns simulation buffers and materials;
    /// does not depend on Godot's particle nodes, watchdogs, ramps or generated timelines.
    /// Raw SC2 fields stay in Resources/Uprising/Sources for traceability.</summary>
    [DefaultExecutionOrder(100)]
    public sealed class DavaFx : MonoBehaviour
    {
        private sealed class Particle
        {
            public Vector3 Position,Velocity,Size,Angle,Spin;
            public Color Colour;
            public float Age,Life,Noise,NoiseU,NoiseV,ScrollU,ScrollV,Frame;public int NoiseIndex;
            public List<Group> Children;
        }
        private sealed class StripeNode { public Vector3 Position,Velocity; public float Age,Size; }
        /// <summary>Immutable source fields read for every simulated or drawn particle, resolved
        /// once per shared layer node instead of by name per particle and frame.</summary>
        private sealed class LayerInfo
        {
            public bool Stripe,Super,Single,Looped,InheritPosition,Noise,FrameOverLife,Long,HasForces,HasColourOverLife,HasAlphaOverLife,Erosion,HasLegacyPivot;
            public float Start,End,LoopEnd,FrameFps;public int Orientation;public Vector2 LegacyPivot,Pivot;
            public bool[] ActiveLods;
            public JsonNode Forces,LegacyForces,VelocityOverLife,SpinOverLife,NoiseUOverLife,NoiseVOverLife,AnimSpeedOverLife,ColourOverLife,AlphaOverLife,SizeOverLife,ErosionOverLife,NoiseScaleOverLife;
        }
        private static readonly Dictionary<JsonNode,LayerInfo> LayerInfos=new Dictionary<JsonNode,LayerInfo>();
        private static LayerInfo Info(JsonNode l)
        {
            if(LayerInfos.TryGetValue(l,out var info))return info;
            var type=l["layerType"].AsString();var lods=l["activeLODS"];var legacyPivot=l["legacyPivot"];var pivot=l["pivotPoint3d"];
            info=new LayerInfo{Stripe=type=="particlesStripe",Super=type=="superEmitter",Single=l["isSingle"].AsBool(),Looped=l["isLooped"].AsBool(),
                InheritPosition=l["inheritPosition"].AsBool(),Noise=l["enableNoise"].AsBool(),FrameOverLife=l["frameOverLifeEnabled"].AsBool(),Long=l["isLong"].AsBool(),
                Start=l["startTime"].AsFloat(),End=l["endTime"].AsFloat(),LoopEnd=l["loopEndTime"].AsFloat(),FrameFps=l["frameOverLifeFPS"].AsFloat(),Orientation=l["particleOrientation"].AsInt(1),
                Forces=l["forces"],LegacyForces=l["legacyForces"],HasForces=l["forces"].Count>0,
                VelocityOverLife=l["velocityOverLife"],SpinOverLife=l["spinOverLife3d"],NoiseUOverLife=l["noiseUScrollSpeedOverLife"],NoiseVOverLife=l["noiseVScrollSpeedOverLife"],
                AnimSpeedOverLife=l["animSpeedOverLife"],ColourOverLife=l["colorOverLife"],AlphaOverLife=l["alphaOverLife"],SizeOverLife=l["sizeOverLife3ds"],
                ErosionOverLife=l["alphaErosionOverLife"],NoiseScaleOverLife=l["noiseScaleOverLife"],
                HasLegacyPivot=legacyPivot.Kind==JsonKind.Array,LegacyPivot=new Vector2(legacyPivot[0].AsFloat(),legacyPivot[1].AsFloat()),Pivot=new Vector2(pivot[0].AsFloat(),pivot[1].AsFloat())};
            info.HasColourOverLife=info.ColourOverLife.Kind!=JsonKind.Null;info.HasAlphaOverLife=info.AlphaOverLife.Kind!=JsonKind.Null;info.Erosion=info.ErosionOverLife.Kind!=JsonKind.Null;
            if(lods.Kind==JsonKind.Array){info.ActiveLods=new bool[4];for(var i=0;i<4;i++)info.ActiveLods[i]=lods[i].AsBool(true);}
            LayerInfos[l]=info;return info;
        }
        /// <summary>Recycled leaf particles. Emitting parents are never pooled: their child
        /// groups keep reading the parent's position after it dies.</summary>
        private static readonly Stack<Particle> ParticlePool=new Stack<Particle>();
        private const int ParticlePoolLimit=8192;
        private static Particle RentParticle()
        {
            if(ParticlePool.Count==0)return new Particle();
            var p=ParticlePool.Pop();p.Age=0;p.Frame=0;p.Children=null;return p;
        }
        private static void RecycleParticle(Particle p){if(p.Children==null && ParticlePool.Count<ParticlePoolLimit)ParticlePool.Push(p);}
        private sealed class Group
        {
            public JsonNode Emitter,Layer; public LayerInfo Info; public Matrix4x4 Mount;
            public Vector3 LastOrigin; public bool HasOrigin; public Particle Parent; public float Time,Accumulator,LoopStart,LoopLayerStart,LoopDuration;
            public readonly List<Particle> Particles=new List<Particle>();
            public readonly List<StripeNode> Stripe=new List<StripeNode>();
            public Batch Batch; public bool Finished;
        }
        private sealed class Batch
        {
            public Mesh Mesh,Geometry;public MeshRenderer Renderer;public Material Material;public JsonNode Sheet;public Transform Transform;
            public Matrix4x4 WorldToLocal;public Quaternion InverseRotation;public bool SharedMaterial;
            public Vector3[] GeometryVertices,GeometryNormals;public Vector2[] GeometryUv;public int[] GeometryTriangles;
            public int Frames,Cols,Rows;public bool LoopAnimation,NoiseScroll;
            public readonly List<Vector3> Positions=new List<Vector3>(),Normals=new List<Vector3>(),Noise=new List<Vector3>(),Next=new List<Vector3>();
            public readonly List<Vector3> UV=new List<Vector3>();public readonly List<Color> Colours=new List<Color>();public readonly List<int> Indices=new List<int>();
            public void Clear(){Positions.Clear();Normals.Clear();UV.Clear();Colours.Clear();Indices.Clear();Noise.Clear();Next.Clear();}
        }
        private readonly List<Group> _groups=new List<Group>();
        private readonly Dictionary<JsonNode,Batch> _layerBatches=new Dictionary<JsonNode,Batch>();
        private readonly List<Batch> _batches=new List<Batch>();
        private readonly Dictionary<string,Mesh> _geometry=new Dictionary<string,Mesh>();
        private readonly Dictionary<string,double> _variables=new Dictionary<string,double>(StringComparer.Ordinal);
        private Vector3? _stripeFrom,_stripeTo;
        public void SetStripeEndpoints(Vector3 from,Vector3 to){_stripeFrom=from;_stripeTo=to;foreach(var g in _groups)if(g.Info.Stripe && g.Particles.Count>0)BuildStripe(g,g.Particles[0]);}
        private Vector3 _travelAxis;private float _travelStart,_travelLimit=float.PositiveInfinity;private bool _travelClip;
        /// <summary>Confines a travelling discharge to a segment ahead of its origin: a top-level
        /// superEmitter carrier expires once it is <paramref name="distance"/> metres along
        /// <paramref name="axis"/>, and inner emitters stay silent while their carrier is short of
        /// <paramref name="start"/> metres. Call again to stretch the segment as its target moves.</summary>
        public void LimitTravel(Vector3 axis,float distance,float start=0){_travelAxis=axis.sqrMagnitude>1e-8f?axis.normalized:Vector3.forward;_travelStart=Mathf.Max(0,start);_travelLimit=Mathf.Max(0,distance);_travelClip=true;}
        /// <summary>Removes the top-level emitter or layer groups with these source names from this
        /// instance, e.g. a muzzle flash that must not render inside the shooter's own scope.</summary>
        public void Drop(params string[] names)
        {
            foreach(var g in _groups)
            {
                if(g.Parent!=null || (Array.IndexOf(names,g.Emitter["name"].AsString())<0 && Array.IndexOf(names,g.Layer["name"].AsString())<0))continue;
                foreach(var p in g.Particles){if(p.Children!=null)foreach(var child in p.Children)child.Finished=true;RecycleParticle(p);}
                g.Particles.Clear();g.Stripe.Clear();g.Finished=true;
            }
        }
        private JsonNode _manifest;private float _prewarmSeconds;private float _started,_stopAt;private bool _stopping;
        private UnityEngine.Random.State _randomState;private int _spawnSerial;
        public int LiveParticles {get {var count=0;foreach(var g in _groups)count+=g.Particles.Count;return count;}}
        /// <summary>Hold the effect instance until Stop, never extend its authored one-shot layer windows.</summary>
        public bool Sustain {get;set;}
        private bool _visible=true;
        public bool Visible {get {return _visible;}set{_visible=value;foreach(var b in _batches)b.Renderer.enabled=value;}}
        public Vector3 VisualScale {get;set;}=Vector3.one;
        public static DavaFx Create(Transform parent,string source,float seconds=3,bool sustain=false)
        { return CreateSource(parent, source, "Uprising", "Data/uprising_source_manifest", seconds, sustain); }
        /// <summary>Original modern missile scenes, independent of the legacy tracer.</summary>
        public static DavaFx CreateAtgm1120(Transform parent,string source,float seconds=10,bool sustain=false)
        { var fx=CreateSource(parent,source,"Atgm1120","Data/atgm_1120_source_manifest",seconds,sustain);fx._followStripeEmitter=true;return fx; }
        public static DavaFx CreateBigBoss(Transform parent, string source, float seconds = 3, bool sustain = false)
        { return CreateSource(parent, source, "BigBoss", "Data/big_boss_fx_1120", seconds, sustain); }
        /// <summary>Prepare source trees, textures, geometry and shared materials behind
        /// the loading curtain, yielding between scenes instead of hitching first activation.</summary>
        public static IEnumerator PreloadBigBoss(Transform parent)
        {
            var holder=new GameObject("BigBossFxPreload");holder.transform.SetParent(parent,false);holder.SetActive(false);
            try
            {
                var manifest=JsonResources.Load("Data/big_boss_fx_1120");
                foreach(var effect in manifest["effects"].Fields)
                {
                    // UI scenes too: the level-up badge otherwise built its materials mid-battle.
                    var resource=effect.Value["resource"].AsString();
                    var fx=CreateBigBoss(holder.transform,resource.Substring("BigBoss/Sources/".Length));
                    foreach(var group in fx._groups)
                        if(group.Info.Super)fx.PreloadEmitterMaterials(group.Layer["innerEmitter"]);
                    Release(fx.gameObject);
                    yield return null;
                }
            }
            finally{Release(holder);}
        }
        private void PreloadEmitterMaterials(JsonNode emitter)
        {
            foreach(var layer in emitter["layers"].Items)
            {
                if(MissingSourceLayer(layer))continue;
                if(layer["layerType"].AsString()=="superEmitter")PreloadEmitterMaterials(layer["innerEmitter"]);
                else if(!_layerBatches.ContainsKey(layer))_layerBatches[layer]=MakeBatch(layer);
            }
        }
        private bool _followStripeEmitter;
        public static DavaFx Create480(Transform parent,string source,float seconds=10,bool sustain=false)
        { return CreateSource(parent, source, "Legacy480", "Data/legacy480_wreck_manifest", seconds, sustain); }
        /// <summary>War of Ducks: the 4.8.0 particle scenes ported by Tools/port_world_of_ducks.py.</summary>
        /// <remarks>Its only ribbon, the torpedo wake, is authored with
        /// stripeInheritPositionForBase and rides the moving shell; like DAVA
        /// the ribbon colour is also tinted by the layer colour.</remarks>
        public static DavaFx CreateWorldOfDucks(Transform parent,string source,float seconds=10,bool sustain=false)
        { var fx=CreateSource(parent, source, "Particles/WorldOfDucks", "Data/world_of_ducks_fx_manifest", seconds, sustain);fx._followStripeEmitter=true;fx._stripeLayerTint=true;return fx; }
        private bool _stripeLayerTint;
        private bool _bigBoss;
        private readonly float[] _lodDistances={5,80,150,300};
        private static readonly Dictionary<JsonNode,Material> BigBossMaterials=new Dictionary<JsonNode,Material>();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearBigBossMaterials()
        {
            foreach(var material in BigBossMaterials.Values)Release(material);
            BigBossMaterials.Clear();LayerInfos.Clear();ParticlePool.Clear();
        }
        private static DavaFx CreateSource(Transform parent,string source,string folder,string manifest,float seconds,bool sustain)
        {
            using var slowScope = BlitzPerfMonitor.Slow("davafx.create");
            // Sources, manifests and decoded geometry are read-only and shared
            // by every instance; parsing them per spawn hitched the frame.
            var raw=JsonResources.Load(folder+"/Sources/"+source);
            if(raw==null)throw new FileNotFoundException("Missing original DAVA scene: "+source);
            var host=new GameObject("DavaFx_"+source);host.transform.SetParent(parent,false);
            var fx=host.AddComponent<DavaFx>();fx.Sustain=sustain;fx._started=Time.time;fx._stopAt=Time.time+seconds;
            fx._manifest=JsonResources.Load(manifest);fx._bigBoss=folder=="BigBoss";
            var nodes=new Dictionary<int,JsonNode>();
            foreach(var n in raw["#dataNodes"].Items)if(n["##name"].AsString()=="ParticleEmitterNode")nodes[Id(n["#id"])]=n;
            foreach(var g in SharedGeometry(folder+"/Sources/"+source+"_geometry"))fx._geometry[g.Key]=g.Value;
            fx.Walk(raw["#hierarchy"],Matrix4x4.identity,nodes);
            foreach(var item in raw["legacyEmitters"].Items)
                fx.AddEmitter(item["emitter"],Matrix4x4.Translate(fx.Vector(item["position"],0)),null);
            var before=UnityEngine.Random.state;UnityEngine.Random.InitState(unchecked((int)StableHash(source)));fx._randomState=UnityEngine.Random.state;UnityEngine.Random.state=before;
            var warm=fx._prewarmSeconds;while(warm>0){var step=Mathf.Min(warm,1f/60);fx.Simulate(step);warm-=step;}
            return fx;
        }
        private static readonly Dictionary<string,Dictionary<string,Mesh>> GeometryCache=new Dictionary<string,Dictionary<string,Mesh>>(StringComparer.Ordinal);
        private static readonly Dictionary<string,Mesh> NoGeometry=new Dictionary<string,Mesh>();
        /// <summary>The decoded source meshes of one effect, built once and shared by
        /// its instances; rebuilt if an asset unload destroyed them.</summary>
        private static Dictionary<string,Mesh> SharedGeometry(string path)
        {
            if(GeometryCache.TryGetValue(path,out var cached)){var alive=true;foreach(var mesh in cached.Values)if(mesh==null){alive=false;break;}if(alive)return cached;}
            var json=JsonResources.Load(path);if(json==null)return NoGeometry;
            var decoded=new Dictionary<string,Mesh>(StringComparer.Ordinal);
            foreach(var g in json.Items){var mesh=DecodeMesh(g);mesh.hideFlags=HideFlags.DontSave;decoded[Id(g["#id"]).ToString()]=mesh;}
            GeometryCache[path]=decoded;return decoded;
        }
        private static uint StableHash(string s){uint h=5381;foreach(var c in s)h=unchecked(h*33+c);return h;}
        public Camera ViewCamera {get;set;}
        public void SetVariable(string name,double value){_variables[name]=value;}
        /// <summary>Local heights of the top-level emitter mounts.</summary>
        public IEnumerable<float> EmitterHeights {get {foreach(var g in _groups)if(g.Parent==null)yield return g.Mount.m13;}}
        /// <summary>Move every top-level emitter mount to one local height,
        /// keeping its horizontal offset and orientation.</summary>
        public void LevelEmitters(float height){foreach(var g in _groups){if(g.Parent!=null)continue;var m=g.Mount;m.m13=height;g.Mount=m;}}
        public void Stop(){Sustain=false;_stopping=true;foreach(var g in _groups){g.Finished=true;if(g.Info.Single){foreach(var p in g.Particles)RecycleParticle(p);g.Particles.Clear();}}}
        private static int Id(JsonNode n){var b=Hex(n["hex"].AsString());return b.Length>=4?BitConverter.ToInt32(b,0):0;}
        private static byte[] Hex(string s){var b=new byte[s.Length/2];for(var i=0;i<b.Length;i++)b[i]=Convert.ToByte(s.Substring(i*2,2),16);return b;}
        private float Value(JsonNode n,float t,float fallback=0){return (float)DavaProperty.Scalar(n,t,fallback,_variables);}
        private Vector3 Vector(JsonNode n,float t,float fallback=0){return new Vector3((float)DavaProperty.Component(n,t,0,fallback,_variables),(float)DavaProperty.Component(n,t,2,fallback,_variables),(float)DavaProperty.Component(n,t,1,fallback,_variables));}
        private Color Colour(JsonNode n,float t){if(n.Kind==JsonKind.Number){var packed=unchecked((uint)(long)n.AsDouble());return new Color(((packed>>24)&255)/255f,((packed>>16)&255)/255f,((packed>>8)&255)/255f,(packed&255)/255f);}return new Color((float)DavaProperty.Component(n,t,0,1,_variables),(float)DavaProperty.Component(n,t,1,1,_variables),(float)DavaProperty.Component(n,t,2,1,_variables),(float)DavaProperty.Component(n,t,3,1,_variables));}
        private static Quaternion Rotation(JsonNode n){return n.Count>=4?new Quaternion(-n[0].AsFloat(),-n[2].AsFloat(),-n[1].AsFloat(),n[3].AsFloat()):Quaternion.identity;}
        private void Walk(JsonNode n,Matrix4x4 parent,Dictionary<int,JsonNode> nodes)
        {
            if(n.Kind==JsonKind.Array){foreach(var x in n.Items)Walk(x,parent,nodes);return;}
            if(n.Kind!=JsonKind.Object)return;
            if(n["##name"].AsString()!="Entity"){foreach(var x in n.Fields)Walk(x.Value,parent,nodes);return;}
            var matrix=parent;
            foreach(var c in n["components"].Fields)if(c.Value["comp.typename"].AsString()=="TransformComponent")
            {var v=c.Value;matrix=parent*Matrix4x4.TRS(Vector(v["tc.localTranslation"],0),Rotation(v["tc.localRotation"]),Vector(v["tc.localScale"],0,1));}
            if(_bigBoss)foreach(var c in n["components"].Fields)if(c.Value["comp.typename"].AsString()=="LodComponent")
                for(var i=0;i<_lodDistances.Length;i++)_lodDistances[i]=c.Value["lc.loddist"]["distance"+i].AsFloat(_lodDistances[i]);
            foreach(var c in n["components"].Fields)if(c.Value["comp.typename"].AsString()=="ParticleEffectComponent")
            {
                _prewarmSeconds=Mathf.Max(_prewarmSeconds,c.Value["pe.startFromTime"].AsFloat());
                foreach(var e in c.Value["pe.emitters"].Items)
                {
                    JsonNode chosen=null;foreach(var d in e["emitter.data"].Items)if(chosen==null || d["emitter.quality"].AsString()=="Default")chosen=d;
                    JsonNode emitter;if(chosen==null || !nodes.TryGetValue(chosen["emitter.id"].AsInt(),out emitter))continue;
                    var mount=matrix*Matrix4x4.TRS(Vector(e["emitter.position"],0),Rotation(e["emitter.rotation"]),Vector(e["emitter.scale"],0,1));
                    AddEmitter(emitter,mount,null);
                }
            }
            Walk(n["#hierarchy"],matrix,nodes);
        }
        private List<Group> AddEmitter(JsonNode emitter,Matrix4x4 mount,Particle parent)
        {
            var output=new List<Group>();
            foreach(var layer in emitter["layers"].Items)
            {
                if (MissingSourceLayer(layer)) continue;
                if(layer["legacyLod"].Kind==JsonKind.Number && layer["activeLODS"].Kind==JsonKind.Array &&
                    layer["activeLODS"][layer["legacyLod"].AsInt()].AsInt()==0)continue;
                var info=Info(layer);
                var group=new Group{Emitter=emitter,Layer=layer,Info=info,Mount=mount,Parent=parent,LoopLayerStart=info.Start,LoopDuration=info.End};
                if(!info.Super){if(!_layerBatches.TryGetValue(layer,out group.Batch)){group.Batch=MakeBatch(layer);_layerBatches[layer]=group.Batch;}}
                _groups.Add(group);output.Add(group);
            }
            return output;
        }
        private bool MissingSourceLayer(JsonNode layer)
        {
            foreach (var field in new[] { "spritePath", "noisePath", "maskSpritePath", "flowmapPath", "alphaRemapPath" })
            {
                if (field == "noisePath" && !layer["enableNoise"].AsBool()) continue;
                if (field == "maskSpritePath" && !layer["enableMask"].AsBool()) continue;
                if (_manifest["textures"][layer[field].AsString()]["missing"].AsBool()) return true;
            }
            return false;
        }
        private Batch MakeBatch(JsonNode layer)
        {
            var shader=Shader.Find("BlitzLegacy/DavaParticle1120");if(shader==null)throw new InvalidOperationException("DAVA 11.20 particle shader missing");
            var b=new Batch{Mesh=new Mesh{name=layer["name"].AsString(),indexFormat=IndexFormat.UInt32},SharedMaterial=_bigBoss};b.Mesh.MarkDynamic();
            Material shared=null;
            var reuse=_bigBoss && BigBossMaterials.TryGetValue(layer,out shared) && shared!=null;
            b.Material=reuse?shared:new Material(shader);
            var sprite=layer["spritePath"].AsString();b.Sheet=_manifest["textures"][sprite];
            b.Frames=Mathf.Max(1,b.Sheet["frames"].AsInt(1));b.Cols=Mathf.Max(1,b.Sheet["cols"].AsInt(1));b.Rows=Mathf.Max(1,b.Sheet["rows"].AsInt(1));
            b.LoopAnimation=layer["loopSpriteAnimation"].AsBool();b.NoiseScroll=layer["enableNoiseScroll"].AsBool();
            if(!reuse)
            {
                var texture=Resources.Load<Texture2D>(b.Sheet["resource"].AsString());if(texture==null)throw new FileNotFoundException("Missing DAVA sprite: "+sprite);
                b.Material.SetTexture("_MainTex",texture);
                foreach(var binding in new[]{new[]{"noisePath","_NoiseTex"},new[]{"maskSpritePath","_MaskTex"}})
                {var p=layer[binding[0]].AsString();if(p!="")b.Material.SetTexture(binding[1],Resources.Load<Texture2D>(_manifest["textures"][p]["resource"].AsString()));}
                var blend=layer["blending"].AsInt();var additive=blend==2 || blend==3;
                // Exact enum/factors from original Materials/Shaders/blending.slh.
                var src=blend==0 || blend==2 || blend==7 || blend==8?BlendMode.One:blend==5?BlendMode.DstColor:BlendMode.SrcAlpha;
                var dst=blend==0 || blend==5?BlendMode.Zero:additive?BlendMode.One:BlendMode.OneMinusSrcAlpha;
                b.Material.SetFloat("_SrcBlend",(float)src);b.Material.SetFloat("_DstBlend",(float)dst);
                b.Material.SetFloat("_Additive",additive?1:0);b.Material.SetFloat("_NoiseEnabled",layer["enableNoise"].AsBool()?1:0);
                b.Material.SetFloat("_MaskEnabled",layer["enableMask"].AsBool()?1:0);b.Material.SetFloat("_FrameBlend",layer["enableFrameBlend"].AsBool() && layer["layerType"].AsString()!="particlesStripe"?1:0);
                b.Material.SetFloat("_FogEnabled",layer["enableFog"].AsBool()?1:0);
                b.Material.SetFloat("_SoftParticles",layer["softParticles"].AsBool()?1:0);b.Material.SetFloat("_SoftRgb",blend==2?1:0);
                if(layer["useFresToAlpha"].AsBool()){b.Material.SetFloat("_FresnelPower",layer["fresToAlphaPower"].AsFloat());b.Material.SetFloat("_FresnelBias",layer["fresToAlphaBias"].AsFloat());}
                // The source's tiled ribbon sampler differs from its clamped sprite sampler.
                b.Material.SetFloat("_RepeatSprite",_bigBoss && layer["spriteSampler"].AsInt()==2102592?1:0);
                if(_bigBoss)
                {
                    // particle-materials-fp.sl material properties of the 11.20 layer.
                    b.Material.SetFloat("_SoftSlope",layer["depthDifferenceSlope"].AsFloat(2));
                    if(blend==8){var edges=layer["fuseBlendAddEdges"];b.Material.SetFloat("_Fuse",1);b.Material.SetVector("_FuseEdges",new Vector4(edges[0].AsFloat(.45f),edges[1].AsFloat(.55f),0,0));}
                    if(layer["alphaErosionOverLife"].Kind!=JsonKind.Null){b.Material.SetFloat("_Erosion",1);b.Material.SetFloat("_ErosionAccel",layer["alphaErosionAcceleration"].AsFloat(2));}
                    b.Material.hideFlags=HideFlags.DontSave;BigBossMaterials[layer]=b.Material;
                }
            }
            if(layer["layerType"].AsString()=="mesh" && !_geometry.TryGetValue(layer["meshGeometryId"].AsInt().ToString(),out b.Geometry))throw new FileNotFoundException("DAVA particle geometry missing: "+layer["meshGeometryId"].AsInt());
            // Immutable source geometry: Unity array access otherwise copies native buffers per particle/frame.
            if(b.Geometry!=null){b.GeometryVertices=b.Geometry.vertices;b.GeometryNormals=b.Geometry.normals;b.GeometryUv=b.Geometry.uv;b.GeometryTriangles=b.Geometry.triangles;}
            var host=new GameObject(layer["name"].AsString());host.transform.SetParent(transform,false);b.Transform=host.transform;
            host.AddComponent<MeshFilter>().sharedMesh=b.Mesh;var renderer=host.AddComponent<MeshRenderer>();b.Renderer=renderer;renderer.enabled=_visible;renderer.sharedMaterial=b.Material;renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
            _batches.Add(b);return b;
        }
        private Vector3 Source(Group g){return g.Parent==null?transform.position:g.Parent.Position;}
        private Matrix4x4 World(Group g){return transform.localToWorldMatrix*g.Mount;}
        private void Update()
        {
            if(!_stopping && !Sustain && Time.time>=_stopAt)Stop();
            // Bounded catch-up: replaying a long hitch in 1/60 s steps for every live
            // effect made the next frame long too and snowballed into a freeze.
            var left=Mathf.Min(Time.deltaTime,MaxCatchUp);while(left>0){var dt=Mathf.Min(left,1f/60f);Simulate(dt);left-=dt;}
            if(_stopping && LiveParticles==0)Destroy(gameObject);
        }
        private const float MaxCatchUp=.1f;
        private void LateUpdate(){Render(ViewCamera!=null?ViewCamera:Camera.main);}
        public void Simulate(float dt)
        {
            if(!float.IsFinite(dt) || dt<0)throw new ArgumentOutOfRangeException(nameof(dt));
            if(_groups.Count==0)return;
            var previous=UnityEngine.Random.state;UnityEngine.Random.state=_randomState;
            try{SimulateCore(dt);}finally{_randomState=UnityEngine.Random.state;UnityEngine.Random.state=previous;}
        }
        /// <summary>ParticleEffectComponent starts at desiredLodLevel 1; level 0 is the client's
        /// reduced-effects quality option, so authored distances only step a source effect
        /// further down (DAVA uses the next distance as the transition out of a level).</summary>
        private int SourceLod(Camera camera)
        {
            if(!_bigBoss)return 0;
            if(camera==null)return DefaultSourceLod;
            var distance=Vector3.Distance(camera.transform.position,transform.position);
            var lod=0;while(lod<3 && distance>=_lodDistances[lod+1])lod++;
            return Mathf.Max(DefaultSourceLod,lod);
        }
        private const int DefaultSourceLod=1;
        private bool ActiveAtLod(Group g,int lod)
        {return !_bigBoss || g.Info.ActiveLods==null || g.Info.ActiveLods[lod];}
        private static readonly Predicate<Group> Drained=g=>g.Finished && g.Particles.Count==0 && g.Stripe.Count==0;
        private void SimulateCore(float dt)
        {
            _groups.RemoveAll(Drained);
            var count=_groups.Count;var lod=SourceLod(ViewCamera);
            for(var index=0;index<count;index++)
            {
                var g=_groups[index];var l=g.Layer;var info=g.Info;g.Time+=dt;var t=g.Time-g.LoopStart;
                var start=info.Start;var end=info.End;
                if(info.Looped && t>g.LoopDuration && g.Time<info.LoopEnd){g.LoopStart=g.Time;t=0;g.LoopLayerStart=l["deltaTime"].AsFloat()+l["deltaVariation"].AsFloat()*UnityEngine.Random.value;g.LoopDuration=g.LoopLayerStart+end-start+l["loopVariation"].AsFloat()*UnityEngine.Random.value;}
                var world=World(g);var inverse=info.HasForces?world.inverse:Matrix4x4.identity;
                var origin=g.Parent==null?world.MultiplyPoint3x4(Vector3.zero):g.Parent.Position;
                var movement=g.HasOrigin?origin-g.LastOrigin:Vector3.zero;g.LastOrigin=origin;g.HasOrigin=true;
                // The modern missile's stripe base follows the moving emitter,
                // while its emitted stripe nodes remain in world space.
                var follow=info.InheritPosition || (_followStripeEmitter && g.Parent==null && info.Stripe);
                var moves=!info.Stripe || g.Parent==null;var overLayer=t/Mathf.Max(.001f,end-start);
                for(var i=g.Particles.Count-1;i>=0;i--)
                {
                    var p=g.Particles[i];p.Age+=dt;
                    if(follow)p.Position+=movement;
                    if(_travelClip && info.Super && g.Parent==null && Vector3.Dot(p.Position-transform.position,_travelAxis)>_travelLimit)p.Age=p.Life;
                    if(p.Age>=p.Life){if(p.Children!=null)foreach(var child in p.Children)child.Finished=true;g.Particles.RemoveAt(i);RecycleParticle(p);continue;}
                    var age=p.Age/p.Life;
                    if(moves)
                    {
                        p.Position+=p.Velocity*Value(info.VelocityOverLife,age,1)*dt;
                        p.Angle+=UnityEngine.Vector3.Scale(p.Spin,Vector(info.SpinOverLife,age,1))*dt;
                        Forces(g,p,dt,age,overLayer,world,inverse);
                    }
                    if(info.Noise)
                    {p.NoiseU+=p.ScrollU*Value(info.NoiseUOverLife,age,1)*dt;p.NoiseV+=p.ScrollV*Value(info.NoiseVOverLife,age,1)*dt;}
                    if(info.FrameOverLife)p.Frame+=info.FrameFps*Value(info.AnimSpeedOverLife,age,1)*dt;
                }
                if(info.Stripe)UpdateStripe(g,dt);
                if(g.Time>(info.Looped?info.LoopEnd:end))g.Finished=true;
                if(g.Finished || t<=g.LoopLayerStart || t>g.LoopDuration || !ActiveAtLod(g,lod))continue;
                if(_travelClip && g.Parent!=null && Vector3.Dot(g.Parent.Position-transform.position,_travelAxis)<_travelStart)continue;
                if(info.Single || info.Stripe)
                {if(g.Particles.Count==0)Spawn(g,t,world);}
                else
                {
                    g.Accumulator+=Mathf.Max(0,Value(l["number"],t)+Value(l["numberVariation"],t)*UnityEngine.Random.value)*dt;
                    while(g.Accumulator>=1){g.Accumulator-=1;Spawn(g,t,world);}
                }
            }
        }
        private void Spawn(Group g,float t,Matrix4x4 world)
        {
            var l=g.Layer;var e=g.Emitter;
            var axis=Vector(e["emissionVector"],g.Time);var magnitude=axis.magnitude;var alignment=magnitude>1e-8f?Quaternion.FromToRotation(Vector3.up,axis/magnitude):Quaternion.identity;
            var position=Vector3.zero;var type=e["type"].AsInt();
            var radius=Value(e["radius"],g.Time,1);var inner=Value(e["innerRadius"],g.Time);
            if(type==1)position=UnityEngine.Vector3.Scale(Vector(e["size"],g.Time),new Vector3(UnityEngine.Random.value-.5f,UnityEngine.Random.value-.5f,UnityEngine.Random.value-.5f));
            if(type==2 || type==3)
            {var angle=(Value(e["emissionAngle"],g.Time)+Value(e["emissionAngleVariation"],g.Time,360)*UnityEngine.Random.value)*Mathf.Deg2Rad;var r=type==2?Mathf.Lerp(inner,radius,Mathf.Sqrt(UnityEngine.Random.value)):radius;position=new Vector3(r*Mathf.Cos(angle),0,r*Mathf.Sin(angle));}
            if(type==4)position=UnityEngine.Random.onUnitSphere*(e["generateOnSurface"].AsBool()?radius:Mathf.Lerp(inner,radius,Mathf.Sqrt(UnityEngine.Random.value)));
            var theta=Value(e["emissionRange"],g.Time)*.5f*Mathf.Deg2Rad*UnityEngine.Random.value;var phi=UnityEngine.Random.value*Mathf.PI*2;
            var velocity=new Vector3(Mathf.Sin(theta)*Mathf.Cos(phi),Mathf.Cos(theta),Mathf.Sin(theta)*Mathf.Sin(phi))*magnitude;
            if(e["shockwaveMode"].AsInt()!=0 && (type==2 || type==3 || type==4))velocity=position.normalized*magnitude;
            position=alignment*position;velocity=alignment*velocity;
            var p=RentParticle();p.NoiseIndex=_spawnSerial++;p.Life=Mathf.Max(.001f,Value(l["life"],t,1)+Value(l["lifeVariation"],t)*UnityEngine.Random.value);p.Colour=Colour(l["colorRandom"],UnityEngine.Random.value)*Colour(e["colorOverLife"],g.Time);
            // Emitter mount translations are rotated but never scaled in DAVA.
            p.Position=world.MultiplyPoint3x4(position);if(g.Parent!=null)p.Position+=g.Parent.Position-transform.position;
            p.Velocity=world.MultiplyVector(velocity).normalized*velocity.magnitude*(Value(l["velocity"],t)+Value(l["velocityVariation"],t)*UnityEngine.Random.value);
            p.Size=Vector(l["size3d"],t,1)+Vector(l["sizeVariation3d"],t)*UnityEngine.Random.value;
            p.Angle=Vector(l["angle3d"],t)+Vector(l["angleVariation3d"],t)*UnityEngine.Random.value;
            p.Spin=Vector(l["spin3d"],t)+Vector(l["spinVariation3d"],t)*UnityEngine.Random.value;
            if(l["legacySpinDirection"].AsBool() && l["randomSpinDirection"].AsBool() && UnityEngine.Random.value<.5f)p.Spin=-p.Spin;
            if(l["randomSpinDirection"].AsBool())p.Spin*=UnityEngine.Random.value<.5f?-1:1;
            p.Noise=Value(l["noiseScale"],t)+Value(l["noiseScaleVariation"],t)*UnityEngine.Random.value;
            p.ScrollU=Value(l["noiseUScrollSpeed"],t)+Value(l["noiseUScrollSpeedVariation"],t)*UnityEngine.Random.value;
            p.ScrollV=Value(l["noiseVScrollSpeed"],t)+Value(l["noiseVScrollSpeedVariation"],t)*UnityEngine.Random.value;p.NoiseU=p.ScrollU;p.NoiseV=p.ScrollV;
            if(g.Batch!=null && l["randomFrameOnStart"].AsBool())p.Frame=UnityEngine.Random.Range(0,Mathf.Max(1,g.Batch.Sheet["frames"].AsInt()));
            if(g.Info.Super)p.Children=AddEmitter(l["innerEmitter"],Matrix4x4.identity,p);
            g.Particles.Add(p);
            if(g.Info.Stripe && _stripeFrom.HasValue && g.Stripe.Count==0)BuildStripe(g,p);
        }
        private void BuildStripe(Group g,Particle p)
        {
            var length=Vector3.Distance(_stripeFrom.Value,_stripeTo.Value);var segments=Mathf.Clamp(Mathf.CeilToInt(length/Mathf.Max(.1f,Value(g.Layer["stripeVertexSpawnStep"],g.Time,1))),1,256);
            // A growing beam re-lays the nodes it already owns: their ages keep the authored fade
            // running and a per-frame endpoint update allocates nothing once the ribbon is laid.
            if(g.Stripe.Count>segments+1)g.Stripe.RemoveRange(segments+1,g.Stripe.Count-segments-1);
            var size=Value(g.Layer["stripeStartSize"],g.Time,1);
            for(var i=0;i<=segments;i++)
            {
                var position=Vector3.Lerp(_stripeFrom.Value,_stripeTo.Value,(float)i/segments);
                if(i<g.Stripe.Count)g.Stripe[i].Position=position;else g.Stripe.Add(new StripeNode{Position=position,Size=size});
            }
            p.Position=_stripeTo.Value;
        }
        private void Forces(Group g,Particle p,float dt,float overLife,float overLayer,Matrix4x4 matrix,Matrix4x4 inverse)
        {
            // Indexed: foreach over the IReadOnlyList boxed an enumerator per particle and step.
            var legacy=g.Info.LegacyForces;var legacyCount=legacy.Kind==JsonKind.Array?legacy.Count:0;
            for(var i=0;i<legacyCount;i++)
            {var force=legacy[i];p.Velocity += matrix.MultiplyVector(Vector(force["force"],g.Time)) * Value(force["overLife"],overLife,1) * dt;}
            var forces=g.Info.Forces;var forceCount=forces.Kind==JsonKind.Array?forces.Count:0;
            for(var k=0;k<forceCount;k++)
            {
                var f=forces[k];
                if(!f["forceIsActive"].AsBool(true))continue;
                var local=!f["worldAlign"].AsBool();var pos=local?inverse.MultiplyPoint3x4(p.Position):p.Position;
                var vel=local?inverse.MultiplyVector(p.Velocity):p.Velocity;
                var centre=Vector(f["dragForcePosition"],0)+(local?Vector3.zero:transform.position);
                var delta=pos-centre;
                var type=f["forceType"].AsString();
                if(type!="grav" && !f["dragForceInfinityRange"].AsBool())
                {if(f["dragForceShape"].AsString()=="sphere" && delta.sqrMagnitude>Mathf.Pow(f["dragForceRadius"].AsFloat(),2))continue;var box=Vector(f["dragForceBoxSize"],0)*.5f;if(f["dragForceShape"].AsString()=="box" && (Mathf.Abs(delta.x)>box.x || Mathf.Abs(delta.y)>box.y || Mathf.Abs(delta.z)>box.z))continue;}
                var timing=f["dragForceTimingType"].AsString();var t=timing=="ovr_layer"?overLayer:timing=="seconds"?p.Age:overLife;
                var strength=Vector(f["dragForceLine"],t);var direction=Vector(f["forceDirection"],0);
                if(type=="drag")vel=UnityEngine.Vector3.Scale(vel,new Vector3(Mathf.Max(0,1-strength.x*dt),Mathf.Max(0,1-strength.y*dt),Mathf.Max(0,1-strength.z*dt)));
                else if(type=="vortex")vel+=UnityEngine.Vector3.Scale(strength,-Vector3.Cross(delta,direction).normalized)*dt;
                else if(type=="grav")vel+=Vector3.down*strength.x*dt;
                else if(type=="wind")
                {
                    var power=Value(f["turbulenceLine"],t);var frequency=f["forceWindFreq"].AsFloat();var multiplier=1f;
                    if(Mathf.Abs(power)>1e-7f){DavaNoise.Sample(overLife,f["windTurbulenceFrequency"].AsFloat(),p.NoiseIndex,out var nx,out var ny,out var nz);var turbulence=new Vector3(nx,nz,ny);if(100-f["backwardTurbulenceProbability"].AsFloat()>p.NoiseIndex%57%100 && Vector3.Dot(direction.normalized,turbulence.normalized)<0)turbulence=-turbulence;pos+=turbulence*power*dt;}
                    if(Mathf.Abs(frequency)>1e-7f){DavaNoise.Sample(overLife,frequency,p.NoiseIndex,out var noise,out _,out _);multiplier=noise+f["forceWindBias"].AsFloat();}
                    vel+=direction*strength.x*dt*dt*100*multiplier;
                }
                else if(type=="pointgr")
                {var distance=f["pointGravityRadius"].AsFloat();if(delta.sqrMagnitude>distance*distance)vel+=UnityEngine.Vector3.Scale(strength,-delta.normalized)*dt;else if(f["killParticles"].AsBool())p.Age=p.Life;else pos=centre+delta.normalized*distance;}
                if(local){p.Velocity=matrix.MultiplyVector(vel);p.Position=matrix.MultiplyPoint3x4(pos);}else{p.Velocity=vel;p.Position=pos;}
            }
        }
        private void UpdateStripe(Group g,float dt)
        {
            var p=g.Particles.Count>0?g.Particles[0]:null;var position=p==null?Vector3.zero:g.Parent==null?p.Position:g.Parent.Position;
            var step=Value(g.Layer["stripeVertexSpawnStep"],g.Time,1);
            if(!_stripeFrom.HasValue && p!=null && (g.Stripe.Count==0 || (g.Stripe[0].Position-position).sqrMagnitude>step*step))g.Stripe.Insert(0,new StripeNode{Position=position,Velocity=p.Velocity,Size=Value(g.Layer["stripeStartSize"],g.Time,1)});
            var life=Value(g.Layer["stripeLifetime"],g.Time,1);
            for(var i=g.Stripe.Count-1;i>=0;i--){var n=g.Stripe[i];n.Age+=dt;n.Position+=n.Velocity*Value(g.Layer["velocityOverLife"],n.Age/Mathf.Max(.001f,life),1)*dt;if(n.Age>=life)g.Stripe.RemoveAt(i);}
        }
        private Camera _depthCamera;
        private UniversalAdditionalCameraData _depthCameraData;
        public void Render(Camera camera)
        {
            if(camera==null)camera=Camera.current;if(camera==null)return;
            if(_depthCamera!=camera || _depthCameraData==null){_depthCamera=camera;_depthCameraData=camera.GetUniversalAdditionalCameraData();}
            if(!_depthCameraData.requiresDepthTexture)_depthCameraData.requiresDepthTexture=true;ViewCamera=camera;
            foreach(var b in _batches)
            {
                b.Clear();
                // Cache native Transform reads once per mesh, not twice per emitted vertex.
                b.WorldToLocal=b.Transform.worldToLocalMatrix;b.InverseRotation=Quaternion.Inverse(b.Transform.rotation);
            }
            if(!Visible){foreach(var b in _batches)if(b.Mesh.vertexCount>0)b.Mesh.Clear();return;}
            var lod=SourceLod(camera);
            var view=camera.transform;var cameraRight=view.right;var cameraUp=view.up;var cameraForward=view.forward;var cameraPosition=view.position;
            foreach(var g in _groups)
            {
                if(g.Batch==null || !ActiveAtLod(g,lod))continue;var info=g.Info;var b=g.Batch;
                if(info.Stripe){if(_bigBoss)DrawSourceStripe(g,cameraPosition);else DrawStripe(g,cameraPosition);}
                else
                {
                    var worldRotation=World(g).rotation;var orientation=info.Orientation;var basis=(orientation&16)!=0?Quaternion.identity:worldRotation;
                    foreach(var p in g.Particles)
                    {
                        var age=p.Age/p.Life;Color colour;
                        if(_bigBoss)colour=SourceColour(info,p,age);
                        else{colour=p.Colour*Colour(info.ColourOverLife,age);colour.a*=Value(info.AlphaOverLife,age,1);}
                        var size=UnityEngine.Vector3.Scale(UnityEngine.Vector3.Scale(p.Size,VisualScale),Vector(info.SizeOverLife,age,1));
                        var sprite=Sprite(g,p);
                        if(b.Geometry!=null)DrawGeometry(g,p,size,worldRotation*Quaternion.Euler(p.Angle),colour,sprite);
                        else
                        {
                            if((orientation&1)!=0 || (_bigBoss && info.Long))Quad(g,p,size,cameraRight,cameraUp,cameraForward,colour,sprite);
                            if((orientation&2)!=0)Quad(g,p,size,basis*Vector3.forward,basis*Vector3.up,cameraForward,colour,sprite);
                            if((orientation&4)!=0)Quad(g,p,size,basis*Vector3.right,basis*Vector3.up,cameraForward,colour,sprite);
                            if((orientation&8)!=0)Quad(g,p,size,basis*Vector3.right,basis*Vector3.forward,cameraForward,colour,sprite);
                        }
                    }
                }
            }
            foreach(var b in _batches)
            {
                if(b.Positions.Count==0 && b.Mesh.vertexCount==0)continue;
                b.Mesh.Clear();b.Mesh.SetVertices(b.Positions);b.Mesh.SetNormals(b.Normals);b.Mesh.SetColors(b.Colours);b.Mesh.SetUVs(0,b.UV);b.Mesh.SetUVs(1,b.Noise);b.Mesh.SetUVs(2,b.Next);
                // Keep the explicit final bound pass: SetTriangles alone differs at the bitwise geometry gate.
                b.Mesh.SetTriangles(b.Indices,0,false);b.Mesh.RecalculateBounds();
            }
        }
        private struct SpriteState { public int Frame,Next;public float Blend,Noise,NoiseU,NoiseV; }
        private SpriteState Sprite(Group g,Particle p)
        {
            var b=g.Batch;var frame=b.LoopAnimation?((int)p.Frame%b.Frames):Mathf.Min(b.Frames-1,(int)p.Frame);
            return new SpriteState{Frame=frame,Next=b.LoopAnimation?(frame+1)%b.Frames:Mathf.Min(b.Frames-1,frame+1),Blend=p.Frame-Mathf.Floor(p.Frame),
                Noise=p.Noise*Value(g.Info.NoiseScaleOverLife,p.Age/p.Life,1),NoiseU=b.NoiseScroll?p.NoiseU:0,NoiseV=b.NoiseScroll?p.NoiseV:0};
        }
        private static void Vertex(Batch b,Vector3 position,Vector3 normal,Vector2 uv,Color colour,SpriteState sprite,float perspectiveWeight=1)
        {
            b.Positions.Add(b.WorldToLocal.MultiplyPoint3x4(position));b.Normals.Add(b.InverseRotation*normal);b.Colours.Add(colour);
            b.UV.Add(new Vector3((uv.x+sprite.Frame%b.Cols)/b.Cols*perspectiveWeight,(uv.y+b.Rows-1-sprite.Frame/b.Cols)/b.Rows*perspectiveWeight,perspectiveWeight));
            b.Next.Add(new Vector3((uv.x+sprite.Next%b.Cols)/b.Cols,(uv.y+b.Rows-1-sprite.Next/b.Cols)/b.Rows,sprite.Blend));
            b.Noise.Add(new Vector3((uv.x+sprite.NoiseU)*perspectiveWeight,(uv.y+sprite.NoiseV)*perspectiveWeight,sprite.Noise));
        }
        private void Quad(Group g,Particle p,Vector3 size,Vector3 basisRight,Vector3 basisUp,Vector3 view,Color colour,SpriteState sprite)
        {
            if(g.Info.Long)
            {
                var velocity=p.Velocity;var speed=velocity.magnitude;var longitudinal=speed>1e-6f?velocity:Vector3.forward;
                var transverse=Vector3.Cross(longitudinal,view).normalized;if(transverse.sqrMagnitude<1e-6f)transverse=Vector3.right;
                longitudinal*=((speed>1e-6f?Value(g.Layer["scaleVelocityBase"],g.Time)/speed:0)+Value(g.Layer["scaleVelocityFactor"],g.Time));
                basisRight=transverse;basisUp=longitudinal;
            }
            var spin=Quaternion.AngleAxis(p.Angle.y,Vector3.Cross(basisRight,basisUp));var right=spin*basisRight*size.x*.5f;var up=spin*basisUp*size.z*.5f;var normal=Vector3.Cross(basisRight,basisUp).normalized;var b=g.Batch;var first=b.Positions.Count;
            var centre=p.Position;
            if(g.Info.HasLegacyPivot)centre-=right*(2*g.Info.LegacyPivot.x)+up*(2*g.Info.LegacyPivot.y);
            // ParticleRenderObject scales each half extent by (1 +/- pivot): the quad centre
            // moves by pivot times the half size, not the full size.
            else if(_bigBoss)centre-=right*g.Info.Pivot.x+up*g.Info.Pivot.y;
            Vertex(b,centre-right-up,normal,Vector2.zero,colour,sprite);Vertex(b,centre+right-up,normal,Vector2.right,colour,sprite);Vertex(b,centre+right+up,normal,Vector2.one,colour,sprite);Vertex(b,centre-right+up,normal,Vector2.up,colour,sprite);
            AddQuadIndices(b,first,first+1,first+2,first,first+2,first+3);
        }
        private static void AddQuadIndices(Batch b,int a,int c,int d,int e,int f,int h)
        {b.Indices.Add(a);b.Indices.Add(c);b.Indices.Add(d);b.Indices.Add(e);b.Indices.Add(f);b.Indices.Add(h);}
        private static void DrawGeometry(Group g,Particle p,Vector3 size,Quaternion rotation,Color colour,SpriteState sprite)
        {
            var b=g.Batch;var first=b.Positions.Count;var vertices=b.GeometryVertices;var normals=b.GeometryNormals;var uv=b.GeometryUv;
            for(var i=0;i<vertices.Length;i++)Vertex(b,p.Position+rotation*UnityEngine.Vector3.Scale(vertices[i],size),rotation*normals[i],uv[i],colour,sprite);
            foreach(var i in b.GeometryTriangles)b.Indices.Add(first+i);
        }
        /// <summary>11.20 ParticleRenderObject: a layer colour or alpha line replaces the spawn
        /// colour instead of tinting it, alpha-erosion layers carry their erosion line in vertex
        /// alpha, and the packed vertex colour saturates.</summary>
        private Color SourceColour(LayerInfo info,Particle p,float age)
        {
            var colour=info.HasColourOverLife?Colour(info.ColourOverLife,age):p.Colour;
            if(info.HasAlphaOverLife)colour.a=Value(info.AlphaOverLife,age,1);
            if(info.Erosion)colour.a=Value(info.ErosionOverLife,age,1);
            return Saturate(colour);
        }
        private static Color Saturate(Color c){return new Color(Mathf.Clamp01(c.r),Mathf.Clamp01(c.g),Mathf.Clamp01(c.b),Mathf.Clamp01(c.a));}
        private static Vector3 StripePoint(Group g,int i,bool withHead,Vector3 head){return withHead?(i==0?head:g.Stripe[i-1].Position):g.Stripe[i].Position;}
        /// <summary>11.20 ribbon: a base vertex rides the emitter, every node keeps the start width
        /// it was emitted with and scales it by its own life, the particle's colour and alpha lines
        /// tint the per-node stripe colour, and both authored distance fades apply.</summary>
        private void DrawSourceStripe(Group g,Vector3 cameraPosition)
        {
            if(g.Particles.Count==0 || g.Stripe.Count==0)return;
            var p=g.Particles[0];var l=g.Layer;var info=g.Info;var b=g.Batch;var sprite=Sprite(g,p);
            var lifetime=Mathf.Max(.001f,Value(l["stripeLifetime"],g.Time,1));var life=p.Age/Mathf.Max(.001f,p.Life);
            var current=info.HasColourOverLife?Colour(info.ColourOverLife,life):p.Colour;if(info.HasAlphaOverLife)current.a=Value(info.AlphaOverLife,life,1);
            var lines=l["stripeColorOverLife"];var hasLines=lines.Kind!=JsonKind.Null;
            var front=Value(l["stripeFadeDistanceFromFront"],g.Time);var back=Value(l["stripeFadeDistanceFromBack"],g.Time);
            var u=Value(l["stripeUScrollSpeed"],life)*p.Age;var scroll=Value(l["stripeVScrollSpeed"],life)*p.Age;
            // Explicit endpoints own the whole ribbon; otherwise the base node sits on the emitter.
            var head=g.Parent==null?p.Position:g.Parent.Position;
            var withHead=!_stripeFrom.HasValue && (head-g.Stripe[0].Position).sqrMagnitude>1e-8f;
            var count=g.Stripe.Count+(withHead?1:0);if(count<2)return;
            float height=0;for(var i=1;i<count;i++)height+=Vector3.Distance(StripePoint(g,i,withHead,head),StripePoint(g,i-1,withHead,head));
            var sizeLine=l["stripeSizeOverLifeProp"];var tileLine=l["stripeTextureTileOverLife"];var perspective=l["usePerspectiveMapping"].AsBool();
            var headSize=Value(l["stripeStartSize"],g.Time,1);float distance=0;
            for(var i=0;i<count;i++)
            {
                var position=StripePoint(g,i,withHead,head);var node=withHead?(i==0?null:g.Stripe[i-1]):g.Stripe[i];
                if(i>0)distance+=Vector3.Distance(position,StripePoint(g,i-1,withHead,head));
                var tangent=i+1<count?position-StripePoint(g,i+1,withHead,head):StripePoint(g,i-1,withHead,head)-position;
                var side=Vector3.Cross(tangent,cameraPosition-position).normalized;var age=node==null?0:node.Age/lifetime;
                var width=(node==null?headSize:node.Size)*Value(sizeLine,age,1)*.5f*VisualScale.x;
                var colour=hasLines?current*Colour(lines,age):current;
                if(front>1e-3f)colour.a*=Mathf.Clamp01(distance/front);
                if(back>1e-3f)colour.a*=Mathf.Clamp01((height-distance)/back);
                colour=Saturate(colour);
                var v=distance*Value(tileLine,age,1)+scroll;var first=b.Positions.Count;
                var weight=perspective?Mathf.Max(.0001f,width*2):1;var normal=Vector3.Cross(side,tangent).normalized;
                Vertex(b,position-side*width,normal,new Vector2(u,v),colour,sprite,weight);Vertex(b,position+side*width,normal,new Vector2(u+1,v),colour,sprite,weight);
                if(i>0)AddQuadIndices(b,first-2,first-1,first,first,first-1,first+1);
            }
        }
        private void DrawStripe(Group g,Vector3 cameraPosition)
        {
            if(g.Particles.Count==0 || g.Stripe.Count<2)return;var p=g.Particles[0];var l=g.Layer;var b=g.Batch;var sprite=Sprite(g,p);float distance=0;var lifetime=Mathf.Max(.001f,Value(l["stripeLifetime"],g.Time,1));
            var tint=_stripeLayerTint && l["stripeColorOverLife"].Kind!=JsonKind.Null?Colour(l["colorOverLife"],p.Age/Mathf.Max(.001f,p.Life)):Color.white;
            // 4.8 stripeFadeDistanceFromTop: the ribbon fades in over this distance from its newest node.
            var fadeTop=Value(l["stripeFadeDistanceFromTop"],g.Time);
            for(var i=0;i<g.Stripe.Count;i++)
            {
                var n=g.Stripe[i];if(i>0)distance+=Vector3.Distance(n.Position,g.Stripe[i-1].Position);
                var tangent=i+1<g.Stripe.Count?n.Position-g.Stripe[i+1].Position:g.Stripe[i-1].Position-n.Position;
                var side=Vector3.Cross(tangent,cameraPosition-n.Position).normalized;var age=n.Age/lifetime;
                var width=Value(l["stripeStartSize"],g.Time,1)*Value(l["stripeSizeOverLifeProp"],age,1)*.5f*VisualScale.x;
                var colour=Colour(l["stripeColorOverLife"].Kind==JsonKind.Null?l["colorOverLife"]:l["stripeColorOverLife"],age)*p.Colour*tint;colour.a*=Value(l["alphaOverLife"],age,1);
                if(fadeTop>0)colour.a*=Mathf.Clamp01(distance/fadeTop);
                var v=distance*Value(l["stripeTextureTileOverLife"],age,1)+Value(l["stripeVScrollSpeed"],age)*g.Time;var u=Value(l["stripeUScrollSpeed"],age)*g.Time;var first=b.Positions.Count;
                var weight=l["usePerspectiveMapping"].AsBool()?Mathf.Max(.0001f,width*2):1;var normal=Vector3.Cross(side,tangent).normalized;
                Vertex(b,n.Position-side*width,normal,new Vector2(u,v),colour,sprite,weight);Vertex(b,n.Position+side*width,normal,new Vector2(u+1,v),colour,sprite,weight);
                if(i>0)AddQuadIndices(b,first-2,first-1,first,first,first-1,first+1);
            }
        }
        private static Mesh DecodeMesh(JsonNode n)
        {
            var vertices=Hex(n["vertices"]["hex"].AsString());var indices=Hex(n["indices"]["hex"].AsString());var count=n["vertexCount"].AsInt();var stride=vertices.Length/count;
            if(n["vertexFormat"].AsInt()!=11 || stride!=32)throw new InvalidDataException("Unsupported original FX vertex format");
            var positions=new Vector3[count];var normals=new Vector3[count];var uv=new Vector2[count];
            for(var i=0;i<count;i++){var p=i*stride;positions[i]=new Vector3(BitConverter.ToSingle(vertices,p),BitConverter.ToSingle(vertices,p+8),BitConverter.ToSingle(vertices,p+4));normals[i]=new Vector3(BitConverter.ToSingle(vertices,p+12),BitConverter.ToSingle(vertices,p+20),BitConverter.ToSingle(vertices,p+16));uv[i]=new Vector2(BitConverter.ToSingle(vertices,p+24),1-BitConverter.ToSingle(vertices,p+28));}
            var triangles=new int[n["indexCount"].AsInt()];for(var i=0;i<triangles.Length;i++)triangles[i]=BitConverter.ToUInt16(indices,i*2);
            var mesh=new Mesh{name="Original DAVA FX mesh"};mesh.vertices=positions;mesh.normals=normals;mesh.uv=uv;mesh.triangles=triangles;mesh.RecalculateBounds();return mesh;
        }
        private static void Release(UnityEngine.Object o){if(o==null)return;if(Application.isPlaying)Destroy(o);else DestroyImmediate(o);}
        private void OnDestroy()
        {
            // Source geometry is shared through GeometryCache and outlives the instance.
            foreach(var b in _batches){if(b.Mesh!=null)Release(b.Mesh);if(b.Material!=null && !b.SharedMaterial)Release(b.Material);}
        }
    }
}
