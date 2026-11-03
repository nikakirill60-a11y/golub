using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BlitzLegacy.Runtime.Lighting;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Hangar
{
    /// <summary>The original 4.8 ordinary/premium world. Owns all generated
    /// meshes/materials, so repeated premium changes do not leak GPU objects.</summary>
    public sealed class ClientHangarScene : MonoBehaviour
    {
        public const string SurfaceShader = "BlitzLegacy/Hangar480/Surface";
        public const string ParticleShader = "BlitzLegacy/Hangar480/Particle";
        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();
        public ClientHangarDefinition Definition { get; private set; }
        public int BatchCount { get; private set; }
        public int TriangleCount { get; private set; }
        public int ParticleLayerCount { get; private set; }
        public int SmokeEmitterCount { get; private set; }
        public int RuntimeLightCount { get; private set; }
        public int AnimationCount { get { return Definition.animations.Length; } }
        public Bounds GeometryBounds { get; private set; }

        public static ClientHangarScene Create(Transform parent, bool premium)
        {
            var definition = ClientHangarDefinition.Load(premium);
            var root = new GameObject("Blitz480_" + definition.id);
            root.transform.SetParent(parent,false);root.SetActive(false);
            var scene=root.AddComponent<ClientHangarScene>();scene.Definition=definition;
            root.transform.localPosition=Vector3.down*definition.standHeight;
            try { scene.Build(); return scene; }
            catch { ReleaseObject(root); throw; }
        }
        private static void ReleaseObject(UnityEngine.Object obj)
        {
            if (Application.isPlaying) Destroy(obj); else DestroyImmediate(obj);
        }
        private Texture2D Texture(string path)
        {
            if(string.IsNullOrEmpty(path))return Texture2D.whiteTexture;
            var texture=Resources.Load<Texture2D>(path);
            if(texture==null)throw new InvalidOperationException("Missing source hangar texture: "+path);
            return texture;
        }
        private void Build()
        {
            var shader=Shader.Find(SurfaceShader);
            if(shader==null)throw new InvalidOperationException("Missing hangar surface shader");
            var materials=new Material[Definition.materials.Length];
            for(var i=0;i<materials.Length;i++)
            {
                var s=Definition.materials[i];var m=new Material(shader){name=s.name};_owned.Add(m);
                m.SetTexture("_BaseMap",Texture(s.albedo));m.SetTexture("_Secondary",Texture(s.secondary));
                m.SetFloat("_SecondaryGain",s.secondaryMultiplier);
                m.SetVector("_SecondaryST",new Vector4(s.uvScale[0],s.uvScale[1],s.uvOffset[0],s.uvOffset[1]));
                m.SetVector("_Tint",new Vector4(s.tint[0],s.tint[1],s.tint[2],s.tint[3]));
                m.SetFloat("_Cutoff",s.alphaTest?s.cutoff:0);
                m.SetFloat("_Cull",s.noCull?(float)CullMode.Off:(float)CullMode.Back);
                if(s.alphaBlend)
                {
                    m.SetFloat("_SrcBlend",(float)BlendMode.SrcAlpha);m.SetFloat("_DstBlend",(float)BlendMode.OneMinusSrcAlpha);
                    m.SetFloat("_ZWrite",0);m.renderQueue=(int)RenderQueue.Transparent;
                    m.SetOverrideTag("RenderType","Transparent");
                }
                materials[i]=m;
            }
            var motions=new ClientHangarMotion[Definition.animations.Length];
            for(var i=0;i<motions.Length;i++)
            {
                var go=new GameObject(Definition.animations[i].name);go.transform.SetParent(transform,false);
                motions[i]=go.AddComponent<ClientHangarMotion>();motions[i].Configure(Definition.animations[i]);
            }
            var asset=Resources.Load<TextAsset>(Definition.meshResource);
            if(asset==null)throw new InvalidOperationException("Missing hangar mesh payload");
            using(var reader=new BinaryReader(new MemoryStream(asset.bytes)))
            {
                if(Encoding.ASCII.GetString(reader.ReadBytes(8))!="BLTZHGR1")throw new InvalidDataException("Invalid hangar payload magic");
                if(reader.ReadInt32()!=Definition.batches.Length)throw new InvalidDataException("Hangar batch count mismatch");
                foreach(var b in Definition.batches)
                {
                    var count=reader.ReadInt32();var indexCount=reader.ReadInt32();
                    if(count!=b.vertexCount || indexCount!=b.triangleCount*3)throw new InvalidDataException("Hangar source geometry mismatch");
                    var positions=ReadVectors(reader,count);var normals=ReadVectors(reader,count);
                    var uv=ReadUvs(reader,count);var uv2=ReadUvs(reader,count);var indices=new int[indexCount];
                    for(var i=0;i<indexCount;i++)
                    {
                        indices[i]=reader.ReadInt32();
                        if(indices[i]<0 || indices[i]>=count)throw new InvalidDataException("Hangar index out of range");
                    }
                    var mesh=new Mesh { name=b.name, indexFormat=count>65535?IndexFormat.UInt32:IndexFormat.UInt16 };
                    _owned.Add(mesh);mesh.vertices=positions;mesh.normals=normals;mesh.uv=uv;mesh.uv2=uv2;mesh.triangles=indices;mesh.RecalculateBounds();
                    var go=new GameObject(b.name);go.transform.SetParent(transform,false);
                    go.AddComponent<MeshFilter>().sharedMesh=mesh;
                    var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=materials[b.material];
                    renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;

                    if(b.animation>=0)
                    {
                        var joints=new int[count*4];var weights=new float[count*4];
                        for(var i=0;i<joints.Length;i++)joints[i]=reader.ReadInt32();
                        for(var i=0;i<weights.Length;i++)weights[i]=reader.ReadSingle();
                        motions[b.animation].Bind(mesh,positions,normals,joints,weights);
                    }
                    if(BatchCount==0)GeometryBounds=mesh.bounds;
                    else {var bounds=GeometryBounds;bounds.Encapsulate(mesh.bounds);GeometryBounds=bounds;}
                    BatchCount++;TriangleCount+=b.triangleCount;
                }
                if(reader.BaseStream.Position!=reader.BaseStream.Length)throw new InvalidDataException("Trailing hangar mesh bytes");
            }
            BuildLights();BuildEffects();BuildSounds();
        }
        private static Vector3[] ReadVectors(BinaryReader r,int count)
        {
            var a=new Vector3[count];for(var i=0;i<count;i++)a[i]=new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());return a;
        }
        private static Vector2[] ReadUvs(BinaryReader r,int count)
        {
            var a=new Vector2[count];for(var i=0;i<count;i++)a[i]=new Vector2(r.ReadSingle(),r.ReadSingle());return a;
        }
        private void BuildLights()
        {
            foreach(var s in Definition.lights)
            {
                // Flags: bit 0 is dynamic, bit 1 is static. All static lights
                // already live in the source lightmaps and must not be added
                // to that geometry again. The stand vehicle remains dynamic.
                if((s.flags&1)==0)continue;
                var go=new GameObject(s.name);go.transform.SetParent(transform,false);
                go.transform.localPosition=ClientHangarDefinition.V(s.position);
                go.transform.rotation=Quaternion.LookRotation(ClientHangarDefinition.V(s.direction),Vector3.up);
                var light=go.AddComponent<Light>();
                light.type=s.type==0?LightType.Directional:s.type==1?LightType.Spot:LightType.Point;
                light.range=60f;light.spotAngle=90f;
                light.color=new Color(s.color[0],s.color[1],s.color[2]);
                // The shipped scenes author 300 DAVA units. Use the same
                // 300 -> 1.25 energy conversion as the existing 4.8 maps.
                light.intensity=s.intensity/300f*1.25f;
                light.shadows=LightShadows.None;RuntimeLightCount++;
            }
        }
        public void ApplyEnvironment()
        {
            var e=Definition.environment;
            RenderSettings.ambientMode=AmbientMode.Flat;
            var ambient=new Color(0.371622f,0.371622f,0.371622f);
            foreach(var light in Definition.lights)
                if((light.flags&1)!=0) {ambient=new Color(light.ambient[0],light.ambient[1],light.ambient[2]);break;}
            RenderSettings.ambientLight=ambient;RenderSettings.ambientIntensity=1f;
            RenderSettings.fog=false;RenderSettings.skybox=null;
            Shader.SetGlobalVector(SourceEnvironment.FogColourId,new Vector4(e.color[0],e.color[1],e.color[2],0));
            Shader.SetGlobalVector(SourceEnvironment.FogRangeId,new Vector4(e.start,e.end,e.limit,e.linear?1:2));
            SourceEnvironment.ClearClientFog();
            Shader.SetGlobalVector("_BlitzFogDepth",new Vector4(0,0,e.density,0));Shader.SetGlobalVector("_BlitzFogHeight",Vector4.zero);
            Shader.SetGlobalVector("_BlitzFogSky",new Vector4(e.sky[0],e.sky[1],e.sky[2],e.atmosphere?1:0));
            Shader.SetGlobalVector("_BlitzFogSun",new Vector4(e.sun[0],e.sun[1],e.sun[2],e.scattering));
            var direction=Vector3.down;
            foreach(var light in Definition.lights)if(light.type==0&&(light.flags&1)!=0)direction=ClientHangarDefinition.V(light.direction);
            Shader.SetGlobalVector("_BlitzFogSunDirection",new Vector4(direction.x,direction.y,direction.z,e.distance));
            SourceAdjustments.Apply(new BlitzLegacy.Core.Maps.LightingEnvironment());
            GraphicsQuality.Apply();
        }
        private void BuildEffects()
        {
            foreach(var effect in Definition.effects)
            {
                if(effect.layers.Length==0)continue; // empty event-banner slots
                if(Array.Exists(effect.layers,l=>l.source.EndsWith("/smoke.yaml",StringComparison.Ordinal)))SmokeEmitterCount++;
                foreach(var layer in effect.layers)
                {
                    BuildParticle(effect,layer);ParticleLayerCount++;
                }
            }
        }
        private static AnimationCurve Curve(ClientHangarKey[] keys,int axis)
        {
            var result=new AnimationCurve();
            foreach(var key in keys)result.AddKey(new Keyframe(key.time,key.value[Mathf.Min(axis,key.value.Length-1)]));
            for(var i=0;i<result.length;i++)
            {
                var key=result.keys[i];
                float before=i>0?(key.value-result.keys[i-1].value)/Mathf.Max(0.0001f,key.time-result.keys[i-1].time):0;
                float after=i<result.length-1?(result.keys[i+1].value-key.value)/Mathf.Max(0.0001f,result.keys[i+1].time-key.time):0;
                key.inTangent=before;key.outTangent=after;result.MoveKey(i,key);
            }
            return result;
        }
        private static float Value(ClientHangarKey[] keys,float time,int axis)
        {
            int right=0;while(right<keys.Length-1 && keys[right].time<time)right++;
            int left=Mathf.Max(0,right-1);var a=keys[left];var b=keys[right];
            float av=a.value[Mathf.Min(axis,a.value.Length-1)], bv=b.value[Mathf.Min(axis,b.value.Length-1)];
            return a.time==b.time?av:Mathf.Lerp(av,bv,Mathf.Clamp01((time-a.time)/(b.time-a.time)));
        }
        private void BuildParticle(ClientHangarEffect effect,ClientHangarLayer layer)
        {
            var go=new GameObject(effect.name+"/"+layer.name);go.transform.SetParent(transform,false);
            go.transform.localPosition=ClientHangarDefinition.V(effect.position);
            var direction=ClientHangarDefinition.V(effect.direction);
            go.transform.localRotation=Quaternion.LookRotation(direction.sqrMagnitude>0.01f?direction:Vector3.up,Vector3.forward);
            var ps=go.AddComponent<ParticleSystem>();ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
            var main=ps.main;main.playOnAwake=false;main.useUnscaledTime=true;
            main.loop=true;main.duration=Mathf.Clamp(effect.duration,0.1f,100f);
            main.startLifetime=new ParticleSystem.MinMaxCurve(Mathf.Max(.01f,layer.life-layer.lifeVariation),Mathf.Max(.01f,layer.life+layer.lifeVariation));
            main.startSpeed=layer.single?0:new ParticleSystem.MinMaxCurve(layer.velocity-layer.velocityVariation,layer.velocity+layer.velocityVariation);
            main.startSize3D=true;main.startSizeX=layer.size[0];main.startSizeY=layer.size[1];main.startSizeZ=1;
            main.startRotation=new ParticleSystem.MinMaxCurve((layer.angle-layer.angleVariation)*Mathf.Deg2Rad,(layer.angle+layer.angleVariation)*Mathf.Deg2Rad);
            main.simulationSpace=ParticleSystemSimulationSpace.World;
            main.maxParticles=layer.single?1:Mathf.Clamp(Mathf.CeilToInt(layer.number*layer.life*2),32,2048);
            var emission=ps.emission;emission.enabled=true;
            if(layer.single)
            {
                // Single layers are persistent luminous quads, not one sprite
                // emitted per second. Lifetime/duration must agree to prevent
                // a gap (or stacking) at the loop boundary.
                main.duration=Mathf.Clamp(layer.life,.1f,100f);main.startLifetime=main.duration;main.startDelay=0;
                emission.rateOverTime=0;emission.SetBursts(new[]{new ParticleSystem.Burst(0f,1)});
            }
            else
            {
                var duration=main.duration;
                var begin=Mathf.Clamp01(layer.start/duration);var end=Mathf.Clamp01(layer.end/duration);
                var rate=new AnimationCurve(new Keyframe(0,begin==0?layer.number:0),new Keyframe(begin,layer.number),new Keyframe(Mathf.Max(begin,end-.0001f),layer.number),new Keyframe(end,end<1?0:layer.number),new Keyframe(1,end<1?0:layer.number));
                emission.rateOverTime=new ParticleSystem.MinMaxCurve(1f,rate);
            }
            var shape=ps.shape;shape.enabled=!layer.single;
            if(effect.box[0]>0 || effect.box[1]>0 || effect.box[2]>0)
            {shape.shapeType=ParticleSystemShapeType.Box;shape.scale=new Vector3(Mathf.Max(.001f,effect.box[0]),Mathf.Max(.001f,effect.box[1]),Mathf.Max(.001f,effect.box[2]));}
            else {shape.shapeType=ParticleSystemShapeType.Cone;shape.angle=effect.emissionAngle;shape.radius=0.001f;}
            var size=ps.sizeOverLifetime;size.enabled=true;size.separateAxes=true;
            size.x=new ParticleSystem.MinMaxCurve(1,Curve(layer.sizeKeys,0));size.y=new ParticleSystem.MinMaxCurve(1,Curve(layer.sizeKeys,1));size.z=1;
            // Unity gradients accept at most eight keys; the original welding
            // flash has ten. Sample the FULL source curves into a lookup texture
            // and send particle age to the shader rather than dropping keys.
            var lifeTexture=new Texture2D(512,1,TextureFormat.RGBA32,false,false)
                {name=layer.name+"_source_lifetime",wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear};
            _owned.Add(lifeTexture);
            var pixels=new Color[512];
            for(var i=0;i<pixels.Length;i++)
            {
                var time=i/(float)(pixels.Length-1);
                pixels[i]=new Color(Value(layer.colorKeys,time,0),Value(layer.colorKeys,time,1),
                    Value(layer.colorKeys,time,2),Value(layer.colorKeys,time,3)*Value(layer.alphaKeys,time,0));
            }
            lifeTexture.SetPixels(pixels);lifeTexture.Apply(false,true);
            var rotation=ps.rotationOverLifetime;rotation.enabled=Mathf.Abs(layer.spin)+Mathf.Abs(layer.spinVariation)>0;
            rotation.z=new ParticleSystem.MinMaxCurve((layer.spin-layer.spinVariation)*Mathf.Deg2Rad,(layer.spin+layer.spinVariation)*Mathf.Deg2Rad);
            var force=ps.forceOverLifetime;force.enabled=false;
            foreach(var key in layer.forceKeys)
                if(ClientHangarDefinition.V(key.value).sqrMagnitude>0)force.enabled=true;
            force.space=ParticleSystemSimulationSpace.World;
            force.x=new ParticleSystem.MinMaxCurve(1,Curve(layer.forceKeys,0));
            force.y=new ParticleSystem.MinMaxCurve(1,Curve(layer.forceKeys,1));
            force.z=new ParticleSystem.MinMaxCurve(1,Curve(layer.forceKeys,2));
            if(layer.drag>0)
            {
                var damping=ps.limitVelocityOverLifetime;damping.enabled=true;
                damping.limit=float.MaxValue;damping.drag=layer.drag;
                damping.multiplyDragByParticleSize=false;damping.multiplyDragByParticleVelocity=false;
            }
            if(layer.planes.Length>0)
            {
                var collision=ps.collision;collision.enabled=true;collision.type=ParticleSystemCollisionType.Planes;
                collision.dampen=0;collision.lifetimeLoss=0;collision.radiusScale=.1f;
                collision.bounce=new ParticleSystem.MinMaxCurve(layer.planes[0].bounceMin,layer.planes[0].bounceMax);
                for(var i=0;i<layer.planes.Length;i++)
                {
                    var plane=new GameObject("SourceSparkCollisionPlane");plane.transform.SetParent(transform,false);
                    plane.transform.localPosition=ClientHangarDefinition.V(layer.planes[i].position);
                    plane.transform.localRotation=Quaternion.FromToRotation(Vector3.up,ClientHangarDefinition.V(layer.planes[i].normal));
                    collision.SetPlane(i,plane.transform);
                }
            }
            var material=new Material(Shader.Find(ParticleShader)){name=layer.sourceLayer+"_"+layer.name};_owned.Add(material);
            material.SetTexture("_LifeTex",lifeTexture);
            material.SetTexture("_MainTex",Texture(layer.frames[0]));material.SetFloat("_SrcBlend",(float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend",layer.additive?(float)BlendMode.One:(float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_Fresnel",layer.fresnel?1:0);material.SetFloat("_FresnelPower",layer.fresnelPower);material.SetFloat("_FresnelBias",layer.fresnelBias);material.SetFloat("_Fog",layer.fog?1:0);
            var renderer=ps.GetComponent<ParticleSystemRenderer>();renderer.sharedMaterial=material;
            renderer.minParticleSize=0;renderer.maxParticleSize=100;renderer.pivot=new Vector3(layer.pivot[0],layer.pivot[1],0);
            renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
            renderer.SetActiveVertexStreams(new List<ParticleSystemVertexStream>{
                ParticleSystemVertexStream.Position,ParticleSystemVertexStream.Normal,
                ParticleSystemVertexStream.Color,ParticleSystemVertexStream.UV,ParticleSystemVertexStream.AgePercent});
            if(layer.orientation==1)renderer.renderMode=ParticleSystemRenderMode.Billboard;
            else
            {
                renderer.renderMode=ParticleSystemRenderMode.Mesh;renderer.alignment=ParticleSystemRenderSpace.World;
                var matrix=ClientHangarDefinition.M(effect.matrix);
                var axis=layer.orientation==2?Vector3.right:layer.orientation==4?Vector3.up:Vector3.forward;
                var normal=matrix.MultiplyVector(axis).normalized;
                var basis=Quaternion.FromToRotation(Vector3.forward,normal);
                var quad=new Mesh{name="SourceParticlePlane"};_owned.Add(quad);
                quad.vertices=new[]{basis*new Vector3(-.5f,-.5f,0),basis*new Vector3(.5f,-.5f,0),basis*new Vector3(.5f,.5f,0),basis*new Vector3(-.5f,.5f,0)};
                quad.normals=new[]{normal,normal,normal,normal};quad.uv=new[]{new Vector2(0,0),new Vector2(1,0),new Vector2(1,1),new Vector2(0,1)};quad.triangles=new[]{0,1,2,0,2,3};quad.RecalculateBounds();renderer.mesh=quad;
            }
            // Most authored descriptors contain one frame. Multi-frame sprites
            // are installed as Unity sprites, retaining each frame's padding.
            if(layer.frames.Length>1)
            {
                var sheet=ps.textureSheetAnimation;sheet.enabled=true;sheet.mode=ParticleSystemAnimationMode.Sprites;
                foreach(var path in layer.frames)
                {
                    var texture=Texture(path);var sprite=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f));_owned.Add(sprite);sheet.AddSprite(sprite);
                }
                sheet.frameOverTime=new ParticleSystem.MinMaxCurve(1,new AnimationCurve(new Keyframe(0,0),new Keyframe(1,1)));
                sheet.cycleCount=Mathf.Max(1,Mathf.RoundToInt(layer.frameFps*layer.life/layer.frames.Length));
                if(layer.randomFrame)sheet.startFrame=new ParticleSystem.MinMaxCurve(0,1);
            }
            ps.useAutoRandomSeed=false;ps.randomSeed=(uint)(ParticleLayerCount+1);ps.Play();
        }
        private void BuildSounds()
        {
            foreach(var sound in Definition.sounds)
            {
                if(string.IsNullOrEmpty(sound.clip))continue;
                var clip=Resources.Load<AudioClip>(sound.clip);
                if(clip==null)throw new InvalidOperationException("Missing hangar ambience "+sound.clip);
                var go=new GameObject(sound.name);go.transform.SetParent(transform,false);go.transform.localPosition=ClientHangarDefinition.V(sound.position);
                var audio=go.AddComponent<AudioSource>();audio.bypassReverbZones=true;audio.clip=clip;audio.loop=true;audio.spatialBlend=1;audio.minDistance=4;audio.maxDistance=70;audio.volume=.25f;audio.Play();
            }
        }
        private void OnDestroy()
        {
            foreach(var obj in _owned)if(obj!=null)ReleaseObject(obj);_owned.Clear();
        }
    }
}
