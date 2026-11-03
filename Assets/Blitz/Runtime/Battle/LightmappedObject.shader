// The source lights both scenes with LightmapGI and marks the lights baked, so
// ported geometry is shaded by the atlas rather than by realtime lights: the
// bake already carries the sun, the bounces and the contact shadows. Albedo
// times lightmap is exactly what Godot's shader does for a lightmapped surface
// once its lights are baked and its bake exposure is one.
//
// The rest mirrors maps/components/map_object.gdshader, which every baked
// surface in both scenes draws with: a tint times an albedo texture, raised to
// a contrast exponent, plus an unlit emission of the tint times a second
// texture. Emission sits outside the exponent and outside the lightmap there,
// so it does here too.
Shader "BlitzLegacy/Map/Lightmapped Object"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Tint", Color) = (1, 1, 1, 1)
        _EmissionMap("Emission", 2D) = "black" {}
        [Toggle] _Emissive("Emissive", Float) = 0
        _Contrast("Albedo contrast", Float) = 1
        _Lightmap("Baked lightmap", 2D) = "white" {}
        _Exposure("Source tonemap exposure", Float) = 1
        _LightmapScaleOffset("Atlas transform", Vector) = (1, 1, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Unlit"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "BlitzFog.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);
            TEXTURE2D(_Lightmap);
            SAMPLER(sampler_Lightmap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Exposure;
                half _Emissive;
                half _Contrast;
            CBUFFER_END

            // Every instance owns a rect of one shared strip, so the placement
            // travels per instance and the props keep batching.
            UNITY_INSTANCING_BUFFER_START(Baked)
                UNITY_DEFINE_INSTANCED_PROP(float4, _LightmapScaleOffset)
            UNITY_INSTANCING_BUFFER_END(Baked)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float2 lightmapUv : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 lightmapUv : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VertexPositionInputs positions =
                    GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                float4 placement =
                    UNITY_ACCESS_INSTANCED_PROP(Baked, _LightmapScaleOffset);
                output.lightmapUv = input.lightmapUv * placement.xy + placement.zw;
                output.positionWS = positions.positionWS;
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half3 albedo =
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb *
                    _BaseColor.rgb;
                half3 baked =
                    SAMPLE_TEXTURE2D(_Lightmap, sampler_Lightmap, input.lightmapUv).rgb;

                // The source tints the emission with the same albedo colour and
                // keeps it out of the lightmap, so a lit sign stays lit.
                half3 emission = _Emissive * _BaseColor.rgb * SAMPLE_TEXTURE2D(
                    _EmissionMap, sampler_EmissionMap, input.uv).rgb;

                // The source multiplies the whole frame by tonemap_exposure,
                // and Unity has no such stage, so it folds in here.
                half3 colour = (pow(albedo, _Contrast) * baked + emission) * _Exposure;
                colour = BlitzMixFog(colour, input.positionWS);
                return half4(colour, 1);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
