// The Sprite3D LODs map_object_vegetation.gd shows instead of the billboard
// crowns once dist^2 * fov > 1000^2. SpriteBase3D draws them with the
// StandardMaterial3D of get_material_for_2d: unshaded, albedo texture
// (source_color) times modulate, transparent (alpha blended, no depth
// write) since alpha_cut is off, billboard with keep scale for bushes and
// fixed crossed planes for trees, fog applied.
Shader "BlitzLegacy/GodotMap/Sprite"
{
    Properties
    {
        [MainTexture] _Albedo("Albedo", 2D) = "white" {}
        _Color("Modulate (linear)", Vector) = (1, 1, 1, 1)
        _Cutoff("Alpha scissor", Float) = 0.5
        _Fog("Fog", Float) = 1
        _Cull("Cull", Float) = 2
        _SrcBlend("Source blend", Float) = 5
        _DstBlend("Destination blend", Float) = 10
        _ZWrite("Depth write", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "GodotForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            ZWrite [_ZWrite]
            ZTest LEqual
            Blend [_SrcBlend] [_DstBlend]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_local _ _GODOT_BILLBOARD
            #pragma multi_compile_local _ _GODOT_ALPHATEST
            #include "GodotMap.hlsl"

            TEXTURE2D(_Albedo); SAMPLER(sampler_Albedo);

            CBUFFER_START(UnityPerMaterial)
                float4 _Albedo_ST;
                float4 _Color;
                float _Cutoff;
                float _Fog;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv0 : TEXCOORD0;
                float2 corner : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv0 : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                #if defined(_GODOT_BILLBOARD)
                    positionWS = GodotBillboard(positionWS, float3(input.corner, 0.0), input.normalOS);
                #endif
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv0 = GodotUv(input.uv0);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float4 colour = _Color * SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, input.uv0);
                #if defined(_GODOT_ALPHATEST)
                    clip(colour.a - _Cutoff);
                    colour.a = 1.0;
                #endif
                return half4(GodotFog((half3)colour.rgb, input.positionWS, _Fog), colour.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
