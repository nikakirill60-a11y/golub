Shader "BlitzLegacy/BigBossUiAdditive"
{
    Properties { [PerRendererData] _MainTex ("Source UI particle render target", 2D) = "black" {} }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "CanUseSpriteAtlas"="False" }
        Cull Off Lighting Off ZWrite Off ZTest [unity_GUIZTestMode]
        // The offscreen pass already applied source alpha. Composite that RGB once.
        Blend One One
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex:POSITION; float4 color:COLOR; float2 uv:TEXCOORD0; };
            struct v2f { float4 vertex:SV_POSITION; float4 color:COLOR; float2 uv:TEXCOORD0; };
            sampler2D _MainTex;
            v2f vert(appdata v) { v2f o; o.vertex=UnityObjectToClipPos(v.vertex); o.color=v.color; o.uv=v.uv; return o; }
            fixed4 frag(v2f i):SV_Target { fixed4 c=tex2D(_MainTex,i.uv); return fixed4(c.rgb*i.color.rgb*i.color.a,0); }
            ENDCG
        }
    }
}
