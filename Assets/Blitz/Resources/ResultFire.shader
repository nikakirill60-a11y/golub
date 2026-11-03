Shader "Blitz/UI/ResultFire"
{
 Properties { [PerRendererData] _MainTex("Texture",2D)="white"{} _Color("Tint",Color)=(1,1,1,1) _StencilComp("Stencil Comparison",Float)=8 _Stencil("Stencil ID",Float)=0 _StencilOp("Stencil Operation",Float)=0 _StencilWriteMask("Stencil Write Mask",Float)=255 _StencilReadMask("Stencil Read Mask",Float)=255 _ColorMask("Color Mask",Float)=15 }
 SubShader {
 Tags {"Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "CanUseSpriteAtlas"="True"}
 Stencil {Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask]}
 Cull Off Lighting Off ZWrite Off ZTest [unity_GUIZTestMode] Blend SrcAlpha One ColorMask [_ColorMask]
 Pass {
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
 #include "UnityCG.cginc"
 #include "UnityUI.cginc"
 struct a{float4 vertex:POSITION;fixed4 color:COLOR;float2 uv:TEXCOORD0;};
 struct v{float4 vertex:SV_POSITION;fixed4 color:COLOR;float2 uv:TEXCOORD0;float4 local:TEXCOORD1;};
 sampler2D _MainTex;fixed4 _Color;float4 _ClipRect;
 v vert(a x){v o;o.local=x.vertex;o.vertex=UnityObjectToClipPos(x.vertex);o.uv=x.uv;o.color=x.color*_Color;return o;}
 fixed4 frag(v x):SV_Target{fixed4 c=tex2D(_MainTex,x.uv)*x.color;
 #ifdef UNITY_UI_CLIP_RECT
 c.a*=UnityGet2DClipping(x.local.xy,_ClipRect);
 #endif
 return c;}
 ENDCG
 }
 }
}
