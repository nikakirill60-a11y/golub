// Blitz 4.8.0 chest scene: the unlit background plane (Textured.Opaque).
// Textures are imported without sRGB decoding and the target is a linear
// RenderTexture, so the raw source bytes pass through like in DAVA.
Shader "BlitzLegacy/Chest480/Unlit"
{
 Properties { _MainTex("Albedo",2D)="white" {} }
 SubShader {
  Tags { "RenderType"="Opaque" "Queue"="Geometry" }
  Pass {
   Cull Off ZWrite On ZTest LEqual
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex;
   float4x4 _Chest480MatrixVP;
   struct A { float4 p:POSITION; float2 uv:TEXCOORD0; };
   struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };
   V vert(A a) { V v; v.p=mul(_Chest480MatrixVP,mul(unity_ObjectToWorld,float4(a.p.xyz,1))); v.uv=a.uv; return v; }
   fixed4 frag(V v):SV_Target { return fixed4(tex2D(_MainTex,v.uv).rgb,1); }
   ENDCG
  }
 }
}
