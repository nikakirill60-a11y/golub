// maps/components/water_shader.gdshader: an opaque lit surface whose albedo
// is the raw mainTexture (no source_color hint) sampled twice and multiplied:
//
//   uv1 = UV + shift1 * TIME
//   uv2 = vec2(uv1.x + uv1.y, uv1.x - uv1.y) + shift2 * TIME
//   ALBEDO = tex(uv1) * tex(uv2) * albedo_color
//
// It is not in the lightmap, so the sun and the ambient light it per vertex.
Shader "BlitzLegacy/GodotMap/Water"
{
    Properties
    {
        [MainTexture] _Albedo("mainTexture", 2D) = "white" {}
        _Color("albedo_color (linear)", Vector) = (1, 1, 1, 1)
        _WaterShift("shift1, shift2", Vector) = (0.007, 0.007, 0.04, 0.04)
        _Fog("Fog", Float) = 1
        _Cull("Cull", Float) = 2
    }

    HLSLINCLUDE
    #include "GodotMap.hlsl"

    TEXTURE2D(_Albedo); SAMPLER(sampler_Albedo);

    CBUFFER_START(UnityPerMaterial)
        float4 _Albedo_ST;
        float4 _Color;
        float4 _WaterShift;
        float _Fog;
    CBUFFER_END
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "GodotForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vertex
            #pragma fragment Fragment

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv0 : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv0 : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 light : TEXCOORD2;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv0 = input.uv0;
                output.light = GodotVertexLight(TransformObjectToWorldNormal(input.normalOS));
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float time = _Time.y;
                float2 uv1 = input.uv0 + _WaterShift.xy * time;
                float2 uv2 = float2(uv1.x + uv1.y, uv1.x - uv1.y) + _WaterShift.zw * time;
                float3 first = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, GodotUv(uv1)).rgb;
                float3 second = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, GodotUv(uv2)).rgb;
                float3 albedo = first * second * _Color.rgb;
                half3 colour = (half3)(albedo * input.light);
                return half4(GodotFog(colour, input.positionWS, _Fog), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull [_Cull]
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment

            float4 DepthVertex(float4 positionOS : POSITION) : SV_POSITION
            {
                return TransformObjectToHClip(positionOS.xyz);
            }

            half DepthFragment() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
