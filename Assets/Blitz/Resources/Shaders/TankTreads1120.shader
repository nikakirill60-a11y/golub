// Port of Blitz 11.20 DECAL_TREAD + FADE_OUT_WITH_TIME behaviour.
// Geometry is a bounded strip rather than a deferred projector, but the client
// albedo/alpha, distance-tail opacity and time fade remain data-driven.
Shader "BlitzLegacy/Battle/Tank Treads 11.20"
{
    Properties
    {
        [NoScaleOffset] _BaseMap("Blitz 11.20 Tread", 2D) = "white" {}
        _Tint("Tint", Color) = (1, 1, 1, 1)
        _FadeStart("Time Fade Start", Float) = 24
        _FadeEnd("Time Fade End", Float) = 30
        [HideInInspector] _CurrentTime("Current Time", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Unlit"
            "Queue" = "Transparent-50"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "TankTreads"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest LEqual
            Offset -1, -1

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/Blitz/Runtime/Battle/BlitzFog.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 birth : TEXCOORD1;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                float born : TEXCOORD3;
                half4 color : COLOR;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                float _FadeStart;
                float _FadeEnd;
                float _CurrentTime;
            CBUFFER_END

            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normal = GetVertexNormalInputs(input.normalOS);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = NormalizeNormalPerVertex(normal.normalWS);
                output.uv = input.uv;
                output.born = input.birth.x;
                output.color = input.color;
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 tread = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                float fadeWidth = max(_FadeEnd - _FadeStart, 0.001);
                float age = max(0.0, _CurrentTime - input.born);
                half timeOpacity = 1.0h - (half)smoothstep(
                    _FadeStart, _FadeStart + fadeWidth, age);
                half alpha = tread.a * input.color.a * _Tint.a * timeOpacity;
                clip(alpha - 0.003h);

                half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half diffuse = saturate(dot(normalWS, mainLight.direction));
                half3 illumination = SampleSH(normalWS) +
                    mainLight.color * diffuse * mainLight.distanceAttenuation *
                    mainLight.shadowAttenuation;
                half3 colour = tread.rgb * _Tint.rgb * max(illumination, 0.12h);
                colour = BlitzMixFog(colour, input.positionWS);
                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }
}
