// maps/components/map_object.gdshader as the Godot 4.7 mobile renderer draws
// it on Rudniki (see GodotMap.hlsl):
//
//   UV      = UV * uv1_scale + uv1_offset
//   ALBEDO  = pow(albedo * texture_albedo(UV), contrast)   (both source_color)
//   EMISSION = albedo * texture_emissive(UV)                when emissive
//   lightmapped: albedo * bicubic(lightmap slice, uv2 placed in its rect)
//   otherwise:   albedo * (ambient + vertex Lambert sun)
//
// Also used for the default white StandardMaterial3D of three wrecks.
Shader "BlitzLegacy/GodotMap/Object"
{
    Properties
    {
        [MainTexture] _Albedo("Albedo", 2D) = "white" {}
        _Emission("Emission", 2D) = "black" {}
        _Lightmap("Lightmap slice", 2D) = "white" {}
        _Color("Albedo colour (linear)", Vector) = (1, 1, 1, 1)
        _UvTransform("uv1 scale, offset", Vector) = (1, 1, 0, 0)
        _LightmapSize("Lightmap size, 1/size", Vector) = (1, 1, 1, 1)
        _Contrast("Contrast", Float) = 1
        _Emissive("Emissive", Float) = 0
        _Fog("Fog", Float) = 1
        _Cull("Cull", Float) = 2
    }

    HLSLINCLUDE
    #include "GodotMap.hlsl"

    TEXTURE2D(_Albedo); SAMPLER(sampler_Albedo);
    TEXTURE2D(_Emission); SAMPLER(sampler_Emission);
    TEXTURE2D(_Lightmap); SAMPLER(sampler_Lightmap);

    CBUFFER_START(UnityPerMaterial)
        float4 _Albedo_ST;
        float4 _Color;
        float4 _UvTransform;
        float4 _LightmapSize;
        float _Contrast;
        float _Emissive;
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
            #pragma multi_compile_local _ _GODOT_LIGHTMAP

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                half3 light : TEXCOORD3;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv0 = GodotUv(input.uv0 * _UvTransform.xy + _UvTransform.zw);
                output.uv1 = GodotUv(input.uv1);
                #if defined(_GODOT_LIGHTMAP)
                    output.light = half3(0.0, 0.0, 0.0);
                #else
                    output.light = GodotVertexLight(TransformObjectToWorldNormal(input.normalOS));
                #endif
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float3 albedo = _Color.rgb * SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, input.uv0).rgb;
                albedo = pow(max(albedo, 0.0), _Contrast);
                float3 emission = _Emissive > 0.5
                    ? _Color.rgb * SAMPLE_TEXTURE2D(_Emission, sampler_Emission, input.uv0).rgb
                    : float3(0.0, 0.0, 0.0);
                #if defined(_GODOT_LIGHTMAP)
                    float3 light = GodotBicubic(
                        TEXTURE2D_ARGS(_Lightmap, sampler_Lightmap), input.uv1, _LightmapSize);
                #else
                    float3 light = input.light;
                #endif
                half3 colour = (half3)(albedo * light + emission);
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
