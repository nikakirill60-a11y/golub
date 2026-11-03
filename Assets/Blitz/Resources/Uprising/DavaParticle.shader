Shader "BlitzLegacy/DavaParticle1120"
{
 Properties {
  _MainTex("Original sprite",2D)="white"{}
  _RepeatSprite("Source tiled sprite sampler",Float)=0
  _NoiseTex("Original noise",2D)="white"{}
  _MaskTex("Original mask",2D)="white"{}
  _SrcBlend("Source blend",Float)=5
  _DstBlend("Destination blend",Float)=10
  _NoiseEnabled("Noise",Float)=0
  _MaskEnabled("Mask",Float)=0
  _FrameBlend("Frame blending",Float)=0
  _FogEnabled("Fog",Float)=0
  _Additive("Additive",Float)=0
  _SoftParticles("Original soft intersection",Float)=0
  _SoftRgb("Fade additive RGB",Float)=0
  _FresnelPower("Fresnel power",Float)=0
  _FresnelBias("Fresnel bias",Float)=0
  _SoftSlope("Original soft depth slope",Float)=2
  _Fuse("Original fuse blend add",Float)=0
  _FuseEdges("Fuse blend add edges",Vector)=(0.45,0.55,0,0)
  _Erosion("Original alpha erosion",Float)=0
  _ErosionAccel("Alpha erosion acceleration",Float)=2
 }
 SubShader {
  Tags {"Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline"}
  Pass {
   Blend [_SrcBlend] [_DstBlend]
   ZWrite Off
   Cull Off
   HLSLPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma multi_compile_fog
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
   #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
   #include "../../Runtime/Battle/BlitzFog.hlsl"
   TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
   TEXTURE2D(_NoiseTex); SAMPLER(sampler_NoiseTex);
   TEXTURE2D(_MaskTex); SAMPLER(sampler_MaskTex);
   CBUFFER_START(UnityPerMaterial)
    float _RepeatSprite,_NoiseEnabled,_MaskEnabled,_FrameBlend,_FogEnabled,_Additive,_FresnelPower,_FresnelBias,_SoftParticles,_SoftRgb;
    float _SoftSlope,_Fuse,_Erosion,_ErosionAccel;float4 _FuseEdges;
   CBUFFER_END
   struct A {float4 vertex:POSITION;float3 normal:NORMAL;float4 color:COLOR;float3 uv:TEXCOORD0;float3 noise:TEXCOORD1;float3 next:TEXCOORD2;};
   struct V {float4 position:SV_POSITION;float4 color:COLOR;float3 uv:TEXCOORD0;float3 noise:TEXCOORD1;float3 next:TEXCOORD2;float3 normal:TEXCOORD3;float3 view:TEXCOORD4;float fog:TEXCOORD5;float3 world:TEXCOORD6;};
   V vert(A a) {
    V o;float3 world=TransformObjectToWorld(a.vertex.xyz);o.position=TransformWorldToHClip(world);
    o.color=a.color;
    #ifndef UNITY_COLORSPACE_GAMMA
     o.color.rgb=SRGBToLinear(o.color.rgb);
    #endif
    o.uv=a.uv;o.noise=a.noise;o.next=a.next;
    o.normal=TransformObjectToWorldNormal(a.normal);o.view=GetWorldSpaceViewDir(world);
    o.world=world;o.fog=ComputeFogFactor(o.position.z);return o;
   }
   half4 SampleSourceSprite(float2 uv) {
    [branch] if(_RepeatSprite>0.5)return SAMPLE_TEXTURE2D(_MainTex,sampler_LinearRepeat,uv);
    return SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,uv);
   }
   half4 frag(V i):SV_Target {
    float weight=max(i.uv.z,.0001);float2 uv=i.uv.xy/weight;
    // Original 11.20 mesh-particle-materials-fp.sl: one signed R noise sample offsets BOTH axes.
    if(_NoiseEnabled>0.5) {float n=SAMPLE_TEXTURE2D(_NoiseTex,sampler_NoiseTex,i.noise.xy/weight).r*2-1;uv+=n*i.noise.z;}
    half4 c=SampleSourceSprite(uv);half sourceAlpha=c.a;
    if(_FrameBlend>0.5)c=lerp(c,SampleSourceSprite(i.next.xy),i.next.z);
    if(_MaskEnabled>0.5)c*=SAMPLE_TEXTURE2D(_MaskTex,sampler_MaskTex,i.uv.xy/weight);
    c*=i.color;
    // 11.20 ALPHA_EROSION: vertex alpha decides how much of the raw sprite alpha survives.
    if(_Erosion>0.5)c.a=saturate(sourceAlpha-(_ErosionAccel+1-sourceAlpha*_ErosionAccel)*saturate(1-i.color.a));
    if(_FresnelPower>0)c.a*=(_FresnelBias+(1-_FresnelBias)*pow(saturate(1-abs(dot(normalize(i.normal),normalize(i.view)))),_FresnelPower));
    if(_FogEnabled>0.5){float amount=BlitzFogAmount(i.world);c.rgb=lerp(c.rgb,_Additive>0.5?half3(0,0,0):BlitzFogColour(i.world),amount);}
    if(_SoftParticles>0.5){float scene=LinearEyeDepth(SampleSceneDepth(GetNormalizedScreenSpaceUV(i.position)),_ZBufferParams);float particle=-TransformWorldToView(i.world).z;float delta=max(0,scene-particle);float fade=1-exp(-_SoftSlope*delta*delta);if(_SoftRgb>0.5)c*=fade;else c.a*=fade;}
    // 11.20 BLENDING_FUSE_BLEND_ADD (ONE, INV_SRC_ALPHA): bright texels add, faint ones blend.
    if(_Fuse>0.5){float f=_FuseEdges.y>_FuseEdges.x?smoothstep(_FuseEdges.x,_FuseEdges.y,c.a):step(_FuseEdges.x,c.a);c.rgb*=c.a;c.a*=1-f;}
    return c;
   }
   ENDHLSL
  }
 }
}
