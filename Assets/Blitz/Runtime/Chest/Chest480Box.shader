// Blitz 4.8.0 chest box: DAVA NormalizedBlinnPhongAllQualities.Opaque with
// one point light, evaluated in gamma space on raw (non-sRGB) textures.
Shader "BlitzLegacy/Chest480/Box"
{
 Properties {
  _MainTex("Albedo",2D)="white" {}
  _NormalTex("Normal map",2D)="bump" {}
  _Glossiness("Glossiness",Float)=1
  _Specularity("Specularity",Float)=1
  _NormalScale("Normal scale",Float)=1
  _Fresnel("Metal fresnel reflectance",Vector)=(0.5,0.5,0.5,0)
  _LightPos("Light position (Unity world)",Vector)=(0,0,0,0)
  _LightColor("Light colour",Vector)=(1,1,1,0)
  _Ambient("Ambient colour",Vector)=(0,0,0,0)
  _CameraPos("Camera position (Unity world)",Vector)=(0,0,0,0)
 }
 SubShader {
  Tags { "RenderType"="Opaque" "Queue"="Geometry" }
  Pass {
   Cull Off ZWrite On ZTest LEqual
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex;
   sampler2D _NormalTex;
   float _Glossiness, _Specularity, _NormalScale;
   float4 _Fresnel, _LightPos, _LightColor, _Ambient, _CameraPos;
   float4x4 _Chest480MatrixVP;
   struct A { float4 p:POSITION; float3 n:NORMAL; float4 t:TANGENT; float2 uv:TEXCOORD0; float3 b:TEXCOORD1; };
   struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; float3 world:TEXCOORD1; float3 n:TEXCOORD2; float3 t:TEXCOORD3; float3 b:TEXCOORD4; };
   V vert(A a) {
    V v;
    float4 world=mul(unity_ObjectToWorld,float4(a.p.xyz,1));
    v.p=mul(_Chest480MatrixVP,world);
    v.world=world.xyz;
    v.uv=a.uv;
    v.n=mul((float3x3)unity_ObjectToWorld,a.n);
    v.t=mul((float3x3)unity_ObjectToWorld,a.t.xyz);
    v.b=mul((float3x3)unity_ObjectToWorld,a.b);
    return v;
   }
   float4 frag(V v):SV_Target {
    float4 albedo=tex2D(_MainTex,v.uv);
    float3 nm=tex2D(_NormalTex,v.uv).xyz*2.0-1.0;
    nm.xy*=_NormalScale;
    nm=normalize(nm);
    float3 n=normalize(normalize(v.t)*nm.x+normalize(v.b)*nm.y+normalize(v.n)*nm.z);
    float3 l=normalize(_LightPos.xyz-v.world);
    float3 e=normalize(_CameraPos.xyz-v.world);
    float3 h=normalize(l+e);
    float nl=saturate(dot(n,l));
    float nh=saturate(dot(n,h));
    float nv=saturate(dot(n,e));
    float gp=pow(5000.0,_Glossiness*albedo.a);
    float3 fresnel=_Fresnel.rgb+(1.0-_Fresnel.rgb)*pow(1.0-nv,5.0);
    float3 spec=(gp+2.0)*0.125*pow(nh,gp)*nl*_Specularity*fresnel;
    float3 color=(_Ambient.rgb+nl*0.31830988*_LightColor.rgb)*albedo.rgb+spec*_LightColor.rgb;
    return float4(color,1);
   }
   ENDCG
  }
 }
}
