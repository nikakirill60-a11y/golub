Shader "BlitzLegacy/Map/Vegetation"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Tint", Color) = (1, 1, 1, 1)
        _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5
        _Billboard("Billboard", Float) = 1
        _OriginOffset("Fade Origin", Vector) = (0, 2, 0, 0)
        _SwayAmount("Sway Amount", Float) = 0.05
        _SwayScale("Sway Scale", Float) = 0.5
        _SwayDuration("Sway Duration", Float) = 3
        _SwayDirection("Sway Direction", Vector) = (1, 0, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "AlphaTest"
            "IgnoreProjector" = "True"
            "DisableBatching" = "True"
        }

        Cull Off
        ZWrite On
        ZTest LEqual

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "BlitzFog.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Cutoff;
            half _Billboard;
            float4 _OriginOffset;
            half _SwayAmount;
            half _SwayScale;
            half _SwayDuration;
            float4 _SwayDirection;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            float3 centreOS : TEXCOORD1;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        float3 ObjectScaleWS()
        {
            float3 origin = TransformObjectToWorld(float3(0, 0, 0));
            return float3(
                length(TransformObjectToWorld(float3(1, 0, 0)) - origin),
                length(TransformObjectToWorld(float3(0, 1, 0)) - origin),
                length(TransformObjectToWorld(float3(0, 0, 1)) - origin));
        }

        float3 ViewRightWS()
        {
            return normalize(mul((float3x3)UNITY_MATRIX_I_V, float3(1, 0, 0)));
        }

        float3 ViewUpWS()
        {
            return normalize(mul((float3x3)UNITY_MATRIX_I_V, float3(0, 1, 0)));
        }

        float3 ViewForwardWS()
        {
            return normalize(mul((float3x3)UNITY_MATRIX_I_V, float3(0, 0, 1)));
        }

        float3 VegetationPositionWS(Attributes input)
        {
            float3 positionOS = input.positionOS.xyz;
            float3 nodePositionWS = TransformObjectToWorld(float3(0, 0, 0));
            float distanceToView = distance(GetCameraPositionWS(), nodePositionWS);
            if (distanceToView < 150.0 && _SwayAmount != 0.0)
            {
                float duration = max((float)_SwayDuration, 0.001);
                float phase = _Time.y * (3.5 / duration) +
                    nodePositionWS.x * _SwayScale + nodePositionWS.z * _SwayScale +
                    positionOS.y * 10.0;
                positionOS += sin(phase) * _SwayAmount * _SwayDirection.xyz;
            }

            if (_Billboard < 0.5)
            {
                return TransformObjectToWorld(positionOS);
            }

            float3 centreOS = input.centreOS;
            float3 offsetOS = positionOS - centreOS;
            float3 scale = ObjectScaleWS();
            float3 centreWS = TransformObjectToWorld(centreOS);
            return centreWS +
                ViewRightWS() * offsetOS.x * scale.x +
                ViewUpWS() * offsetOS.y * scale.y +
                ViewForwardWS() * offsetOS.z * scale.z;
        }

        half SourceAlpha(float2 uv)
        {
            return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).a * _BaseColor.a;
        }
        ENDHLSL

        Pass
        {
            Name "VegetationForward"
            Tags { "LightMode" = "UniversalForwardOnly" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex VegetationVertex
            #pragma fragment VegetationFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float distanceToOrigin : TEXCOORD2;
                half3 normalWS : TEXCOORD3;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings VegetationVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = VegetationPositionWS(input);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.positionWS = positionWS;
                output.normalWS = _Billboard > 0.5
                    ? -ViewForwardWS()
                    : TransformObjectToWorldNormal(input.normalOS);
                float3 fadeOrigin = TransformObjectToWorld(float3(0, 0, 0)) +
                    _OriginOffset.xyz;
                output.distanceToOrigin = distance(GetCameraPositionWS(), fadeOrigin);
                return output;
            }

            half Bayer4x4(uint2 pixel)
            {
                static const half thresholds[16] =
                {
                    0.0h / 16.0h, 8.0h / 16.0h, 2.0h / 16.0h, 10.0h / 16.0h,
                    12.0h / 16.0h, 4.0h / 16.0h, 14.0h / 16.0h, 6.0h / 16.0h,
                    3.0h / 16.0h, 11.0h / 16.0h, 1.0h / 16.0h, 9.0h / 16.0h,
                    15.0h / 16.0h, 7.0h / 16.0h, 13.0h / 16.0h, 5.0h / 16.0h
                };
                return thresholds[(pixel.y & 3u) * 4u + (pixel.x & 3u)];
            }

            half4 VegetationFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 sample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half alpha = sample.a * _BaseColor.a;
                clip(alpha - _Cutoff);

                // _vegetation_shader.gdshader keeps at least half the coverage
                // and fades only the geometry that intersects the camera.
                half closeFade = max(0.5h, (input.distanceToOrigin - 7.0h) / 8.0h);
                clip(alpha * closeFade - Bayer4x4((uint2)input.positionCS.xy));

                half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                half3 viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                // Cull is off: flip the back face toward the viewer so the same
                // leaf is not sunlit from one side and pitch black from the other.
                normalWS *= dot(normalWS, viewDirectionWS) < 0.0h ? -1.0h : 1.0h;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = viewDirectionWS;
                // Per pixel: a cascade picked per vertex blends two atlas tiles
                // across every triangle that straddles a cascade border.
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.vertexLighting = half3(0.0h, 0.0h, 0.0h);
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV =
                    GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = sample.rgb * _BaseColor.rgb;
                surfaceData.specular = half3(0.0h, 0.0h, 0.0h);
                surfaceData.smoothness = 0.0h;
                surfaceData.normalTS = half3(0.0h, 0.0h, 1.0h);
                surfaceData.occlusion = 1.0h;
                surfaceData.alpha = 1.0h;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = BlitzMixFog(color.rgb, input.positionWS);
                color.a = 1.0h;
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex VegetationShadowVertex
            #pragma fragment VegetationShadowFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowVaryings
            {
                float2 uv : TEXCOORD0;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            ShadowVaryings VegetationShadowVertex(Attributes input)
            {
                ShadowVaryings output = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionWS = VegetationPositionWS(input);
                float3 normalWS = _Billboard > 0.5
                    ? -ViewForwardWS()
                    : TransformObjectToWorldNormal(input.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif
                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                output.positionCS = positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 VegetationShadowFragment(ShadowVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                clip(SourceAlpha(input.uv) - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
