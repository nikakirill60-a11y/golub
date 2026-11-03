// Blitz 4.8.0 chest particles: sprite * vertex colour with the layer's
// blending (1 = alpha, 3 = additive), depth-tested, never depth-written.
Shader "BlitzLegacy/Chest480/Particle"
{
 Properties {
  _MainTex("Sprite",2D)="white" {}
  _SrcBlend("Source blend",Float)=5
  _DstBlend("Destination blend",Float)=10
 }
 SubShader {
  Tags { "RenderType"="Transparent" "Queue"="Transparent" }
  Pass {
   Blend [_SrcBlend] [_DstBlend] Cull Off ZWrite Off ZTest LEqual
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex;
   float4x4 _Chest480MatrixVP;
   struct A { float4 p:POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; };
   struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; };
   V vert(A a) { V v; v.p=mul(_Chest480MatrixVP,mul(unity_ObjectToWorld,float4(a.p.xyz,1))); v.uv=a.uv; v.color=a.color; return v; }
   fixed4 frag(V v):SV_Target { return tex2D(_MainTex,v.uv)*v.color; }
   ENDCG
  }
 }
}
