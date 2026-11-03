// Port of assets/gfx/ui/ui_color_replace.gdshader (canvas_item shader used by
// ui_scoreboard_loading_team.tscn for the small tank icons).  The original
// replaces every texel within `tolerance` of `prev_color` by `new_color`,
// blending by the measured distance and keeping the source alpha.
Shader "Blitz/UiColorReplace"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _PrevColor ("Replaced Color", Color) = (0,0,0,0.846)
        _NewColor ("New Color", Color) = (0,0,0,1)
        _Tolerance ("Tolerance", Range(0,2)) = 0.999999977648
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _PrevColor;
            fixed4 _NewColor;
            float _Tolerance;

            v2f vert(appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 source = tex2D(_MainTex, i.texcoord);
                fixed4 color = source;
                float colorDistance = distance(_PrevColor, source);
                if (colorDistance <= _Tolerance)
                {
                    color = lerp(_NewColor, source, colorDistance);
                    color.a = source.a;
                }
                return color * i.color;
            }
            ENDCG
        }
    }
}
