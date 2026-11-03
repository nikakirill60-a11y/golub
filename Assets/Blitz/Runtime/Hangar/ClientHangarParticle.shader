Shader "BlitzLegacy/Hangar480/Particle"
{
 Properties {
  _MainTex("Sprite",2D)="white" {}
  _LifeTex("Source lifetime colour",2D)="white" {}
  _SrcBlend("Source blend",Float)=5
  _DstBlend("Destination blend",Float)=1
  _Fresnel("Fresnel to alpha",Float)=0
  _FresnelPower("Fresnel power",Float)=1
  _FresnelBias("Fresnel bias",Float)=0
  _Fog("Source fog",Float)=1
 }
 SubShader {
  Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
  Pass {
   Tags { "LightMode"="UniversalForward" }
   Blend [_SrcBlend] [_DstBlend] Cull Off ZWrite Off
   HLSLPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
   #include "ClientHangarFog.hlsl"
   TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
   TEXTURE2D(_LifeTex); SAMPLER(sampler_LifeTex);
   CBUFFER_START(UnityPerMaterial)
   half _Fresnel, _FresnelPower, _FresnelBias, _Fog;
   CBUFFER_END
   struct A { float4 p:POSITION; float3 n:NORMAL; float3 uv:TEXCOORD0; half4 color:COLOR; };
   struct V { float4 p:SV_POSITION; float3 world:TEXCOORD0; float3 uv:TEXCOORD1; half4 color:COLOR; float3 n:TEXCOORD2; half4 fog:TEXCOORD3; };
   V vert(A a) { V v; v.world=TransformObjectToWorld(a.p.xyz); v.p=TransformWorldToHClip(v.world); v.uv=a.uv; v.color=a.color; v.n=TransformObjectToWorldNormal(a.n); v.fog=ClientHangarVertexFog(v.world); v.fog.a*=_Fog; return v; }
   half4 frag(V v):SV_Target {
    half4 sprite=SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,v.uv.xy);
    half4 life=SAMPLE_TEXTURE2D(_LifeTex,sampler_LifeTex,float2(saturate(v.uv.z),0.5));
    half4 c=half4(ClientHangarEncoded(sprite.rgb)*v.color.rgb*ClientHangarEncoded(life.rgb),sprite.a*v.color.a*life.a);
    if(_Fresnel>0.5) c.a*=saturate(_FresnelBias+pow(abs(dot(normalize(v.n),normalize(GetCameraPositionWS()-v.world))),_FresnelPower));
    c.rgb=ClientHangarFinish(c.rgb,v.fog); return c;
   }
   ENDHLSL
  }
 }
}
