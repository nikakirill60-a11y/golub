// 4.8 Materials/Shaders/Default/materials-fp.sl: albedo * decal/lightmap * 2.
// Lightmap UV scale/offset is authored in DAVA's top-down texture coordinates.
Shader "BlitzLegacy/Hangar480/Surface"
{
 Properties {
  [MainTexture] _BaseMap("Albedo",2D)="white" {}
  _Secondary("Source lighting",2D)="white" {}
  _SecondaryGain("Lightmap/decal gain",Float)=1
  _SecondaryST("Source UV scale and offset",Vector)=(1,1,0,0)
  _Tint("Source tint",Color)=(1,1,1,1)
  _Cutoff("Alpha threshold",Float)=0
  _Cull("Cull",Float)=2
  _SrcBlend("Source blend",Float)=1
  _DstBlend("Destination blend",Float)=0
  _ZWrite("Depth write",Float)=1
 }
 SubShader {
  Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
  Pass {
   Tags { "LightMode"="UniversalForward" }
   Cull [_Cull] Blend [_SrcBlend] [_DstBlend] ZWrite [_ZWrite]
   HLSLPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
   #include "ClientHangarFog.hlsl"
   TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
   TEXTURE2D(_Secondary); SAMPLER(sampler_Secondary);
   CBUFFER_START(UnityPerMaterial)
   float4 _SecondaryST; half4 _Tint; half _SecondaryGain; half _Cutoff;
   CBUFFER_END
   struct A { float4 p:POSITION; float2 uv:TEXCOORD0; float2 uv2:TEXCOORD1; };
   struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; float2 uv2:TEXCOORD1; half4 fog:TEXCOORD2; };
   V vert(A a) { V v; float3 world=TransformObjectToWorld(a.p.xyz); v.p=TransformWorldToHClip(world); v.fog=ClientHangarVertexFog(world); v.uv=a.uv;
    v.uv2=a.uv2*_SecondaryST.xy+_SecondaryST.zw; v.uv2.y=1-v.uv2.y; return v; }
   half4 frag(V v):SV_Target {
    half4 a=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,v.uv);
    clip(a.a*_Tint.a-_Cutoff);
    half3 light=SAMPLE_TEXTURE2D(_Secondary,sampler_Secondary,v.uv2).rgb;
    half3 encoded=ClientHangarEncoded(a.rgb)*_Tint.rgb*ClientHangarEncoded(light)*_SecondaryGain;
    return half4(ClientHangarFinish(encoded,v.fog),a.a*_Tint.a);
   }
   ENDHLSL
  }
 }
}
