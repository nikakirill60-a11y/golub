// Presents the chest RenderTexture on the UI. The scene was rendered on raw
// gamma values (as DAVA did), so in a linear project the colour is decoded
// once here and the sRGB back buffer shows the original bytes. _Darkness is
// the reward screen's dimming, applied in gamma space; alpha is the image
// alpha only (the scene itself is opaque).
Shader "BlitzLegacy/Chest480/Present"
{
 Properties {
  [PerRendererData] _MainTex("Scene",2D)="black" {}
  _Darkness("Darkness",Range(0,1))=0
 }
 SubShader {
  Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="False" }
  Pass {
   Cull Off Lighting Off ZWrite Off ZTest [unity_GUIZTestMode]
   Blend SrcAlpha OneMinusSrcAlpha
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex;
   float4 _MainTex_ST;
   float _Darkness;
   struct A { float4 p:POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; };
   struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; };
   V vert(A a) { V v; v.p=UnityObjectToClipPos(a.p); v.uv=TRANSFORM_TEX(a.uv,_MainTex); v.color=a.color; return v; }
   fixed4 frag(V v):SV_Target {
    float3 raw=saturate(tex2D(_MainTex,v.uv).rgb*(1.0-_Darkness));
   #ifndef UNITY_COLORSPACE_GAMMA
    raw=GammaToLinearSpace(raw);
   #endif
    return fixed4(raw,v.color.a);
   }
   ENDCG
  }
 }
}
