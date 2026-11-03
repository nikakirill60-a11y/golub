// Port of the WoT Blitz 11.21 "Skyobject.material"
// (Shaders/Default/skyobject-materials-vp.sl, -fp.sl) with FLOWMAP and
// FLATCOLOR as used by hangar_halloween_2026: the dome follows the camera
// (translation-free transform), sits on the far plane and scrolls its albedo
// along a flow map. No lighting, no fog.
Shader "BlitzLegacy/HalloweenHangar/Sky"
{
    Properties
    {
        _Albedo ("Albedo (gamma)", 2D) = "white" {}
        _Flowmap ("Flow Map", 2D) = "grey" {}
        _FlatColor ("Flat Color", Vector) = (1, 1, 1, 1)
        _FlowParams ("Flow Anim Speed (x) Offset (y), Vertical Scale (z) Offset (w)", Vector) = (0, 0, 1, 0)
        [Toggle(_HH_FLOWMAP)] _HHFlowmap ("Flow Map Animation", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite ("Z Write", Float) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Z Test", Float) = 4
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Background" "Queue" = "Geometry+460" "IgnoreProjector" = "True" "PreviewType" = "Skybox" }

        Pass
        {
            Name "HHSky"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Cull [_Cull]
            ZWrite [_ZWrite]
            ZTest [_ZTest]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ _HH_FLOWMAP
            #include "HalloweenHangarCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Albedo_ST;
                float4 _FlatColor;
                float4 _FlowParams;
                float _HHFlowmap;
                float _Cull;
                float _ZWrite;
                float _ZTest;
            CBUFFER_END

            TEXTURE2D(_Albedo);  SAMPLER(sampler_Albedo);
            TEXTURE2D(_Flowmap); SAMPLER(sampler_Flowmap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0 : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 flowData : TEXCOORD1; // xy phase offsets, z blend
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 localPos = input.positionOS.xyz;
                localPos.y = localPos.y * _FlowParams.z + _FlowParams.w; // client z (up) = Unity y
                float3 directionWS = mul((float3x3)GetObjectToWorldMatrix(), localPos);
                float4 vecPos = mul(GetWorldToHClipMatrix(), float4(directionWS, 0.0));
                const float depthBias = 0.0001;
            #if UNITY_REVERSED_Z
                float farClipZ = depthBias;
            #else
                float farClipZ = vecPos.w - depthBias;
            #endif
                output.positionCS = float4(vecPos.x, vecPos.y, farClipZ, vecPos.w);
                output.uv = input.uv0;

                float scaledTime = _Time.y * _FlowParams.x;
                float2 flowPhases = frac(float2(scaledTime, scaledTime + 0.5)) - float2(0.5, 0.5);
                float flowBlend = abs(flowPhases.x * 2.0);
                output.flowData = float3(flowPhases * _FlowParams.y, flowBlend);
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
            #if defined(_HH_FLOWMAP)
                float2 flowDir = SAMPLE_TEXTURE2D(_Flowmap, sampler_Flowmap, input.uv).xy * 2.0 - 1.0;
                float3 flowSample1 = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, input.uv + flowDir * input.flowData.x).rgb;
                float3 flowSample2 = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, input.uv + flowDir * input.flowData.y).rgb;
                float3 color = lerp(flowSample1, flowSample2, input.flowData.z);
            #else
                float3 color = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, input.uv).rgb;
            #endif
                color *= _FlatColor.rgb;
                return float4(HHOutput(color), 1.0);
            }
            ENDHLSL
        }
    }
}
