Shader "BlitzLegacy/Map/Water"
{
    Properties
    {
        [NoScaleOffset] _BaseMap("Water Texture", 2D) = "white" {}
        _BaseColor("Albedo", Color) = (1, 1, 1, 1)
        _Shift1("Primary Shift", Vector) = (0.007, 0.007, 0, 0)
        _Shift2("Secondary Shift", Vector) = (0.04, 0.04, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex WaterVertex
            #pragma fragment WaterFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _ADDITIONAL_LIGHTS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "BlitzFog.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _Shift1;
                float4 _Shift2;
            CBUFFER_END

            Varyings WaterVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normals = GetVertexNormalInputs(input.normalOS);
                output.uv = input.uv;
                output.positionWS = positions.positionWS;
                output.normalWS = NormalizeNormalPerVertex(normals.normalWS);
                output.positionCS = positions.positionCS;
                return output;
            }

            half4 WaterFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv1 = input.uv + _Shift1.xy * _Time.y;
                float2 uv2 = float2(uv1.x + uv1.y, uv1.x - uv1.y) +
                    _Shift2.xy * _Time.y;
                half3 albedo =
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv1).rgb *
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv2).rgb *
                    _BaseColor.rgb;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = NormalizeNormalPerPixel(input.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                // A water sheet is a handful of very large triangles, so a
                // shadow coordinate interpolated from its corners drifts far
                // enough inside the quad to sample the wrong cascade and drop
                // whole rectangles into shadow. Build it per pixel instead.
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.vertexLighting = half3(0, 0, 0);
                inputData.bakedGI = SampleSH(inputData.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.specular = half3(0, 0, 0);
                surfaceData.smoothness = 0;
                surfaceData.normalTS = half3(0, 0, 1);
                surfaceData.occlusion = 1;
                surfaceData.alpha = 1;

                half4 colour = UniversalFragmentPBR(inputData, surfaceData);
                colour.rgb = BlitzMixFog(colour.rgb, input.positionWS);
                colour.a = 1;
                return colour;
            }
            ENDHLSL
        }
    }
}
