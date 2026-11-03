Shader "BlitzLegacy/UI/ResultBackdrop"
{
    Properties { _MainTex ("Captured hangar", 2D) = "black" {} }
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
            sampler2D _MainTex;
            v2f vert(appdata v) { v2f o; o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o; }
            float4 frag(v2f i):SV_Target {
                // ui_blur.gdshader: SCREEN_UV +/- .003, mip 2, sqrt, source tint.
                float3 c=float3(tex2Dlod(_MainTex,float4(i.uv+.003,0,2)).r,
                    tex2Dlod(_MainTex,float4(i.uv,0,2)).g,
                    tex2Dlod(_MainTex,float4(i.uv-.003,0,2)).b);
                #ifndef UNITY_COLORSPACE_GAMMA
                    c=LinearToGammaSpace(c);
                #endif
                c=sqrt(max(c,0))*float3(.32740578,.41763112,.4824441);
                #ifndef UNITY_COLORSPACE_GAMMA
                    c=GammaToLinearSpace(c);
                #endif
                return float4(c,1);
            }
            ENDCG
        }
    }
}
