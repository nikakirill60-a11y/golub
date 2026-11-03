// Skyobject.Flowmap: materials-vp.sl's SKYOBJECT branch drops the
// translation from the world-view-projection and pins the dome to the far
// plane, so it follows the camera and never occludes anything; FLOWMAP_SKY
// drifts the albedo along the flow map with two phases half a period apart.
//
//   phase = frac(t * flowAnimSpeed + (0, 0.5)) - 0.5
//   flow  = flowmap(uv).xy * 2 - 1
//   rgb   = lerp(albedo(uv + flow * phase.x * flowAnimOffset),
//                albedo(uv + flow * phase.y * flowAnimOffset), |phase.x * 2|)
Shader "BlitzLegacy/ClientMap/Sky"
{
    Properties
    {
        [MainTexture] _Albedo("Albedo", 2D) = "white" {}
        _Flowmap("Flow map", 2D) = "grey" {}
        _Flow("flowAnimSpeed, flowAnimOffset", Vector) = (0.1, 0.2, 0, 0)
        _Cull("Cull", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry+499" }

        Pass
        {
            Name "ClientSky"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            // Writing the far-plane depth keeps URP's skybox pass, which
            // only fills still-cleared depth, from painting over the dome.
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "ClientMap.hlsl"

            TEXTURE2D(_Albedo); SAMPLER(sampler_Albedo);
            TEXTURE2D(_Flowmap); SAMPLER(sampler_Flowmap);

            CBUFFER_START(UnityPerMaterial)
                float4 _Flow;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0 : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv0 : TEXCOORD0;
                float3 flow : TEXCOORD1;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                // The mesh is stored around the origin: the dome's own
                // rotation and scale, without its placement.
                float3 direction = mul((float3x3)UNITY_MATRIX_M, input.positionOS.xyz);
                float4 clip = TransformWorldToHClip(GetCameraPositionWS() + direction);
                #if UNITY_REVERSED_Z
                    clip.z = clip.w * 1e-5;
                #else
                    clip.z = clip.w * (1.0 - 1e-5);
                #endif
                output.positionCS = clip;
                output.uv0 = input.uv0;
                float2 phase = frac(_Time.y * _Flow.x + float2(0.0, 0.5)) - 0.5;
                output.flow = float3(phase * _Flow.y, abs(phase.x * 2.0));
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float2 flow = SAMPLE_TEXTURE2D(_Flowmap, sampler_Flowmap, DavaUv(input.uv0)).xy * 2.0 - 1.0;
                half3 first = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo,
                    DavaUv(input.uv0 + flow * input.flow.x)).rgb;
                half3 second = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo,
                    DavaUv(input.uv0 + flow * input.flow.y)).rgb;
                return half4(ClientOutput(lerp(first, second, input.flow.z)), 1.0);
            }
            ENDHLSL
        }
    }
}
