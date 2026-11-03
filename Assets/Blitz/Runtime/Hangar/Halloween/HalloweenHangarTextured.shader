// Port of the WoT Blitz 11.21 "Textured.material" / "TextureLightmap.material"
// (Shaders/Default/materials-vp.sl, materials-fp.sl) as used by
// hangar_halloween_2026 and hangar_miku_void. Albedo and lightmaps are gamma
// textures sampled without sRGB decoding; all maths happens in gamma space
// like the client.
Shader "BlitzLegacy/HalloweenHangar/Textured"
{
    Properties
    {
        _Albedo ("Albedo (gamma)", 2D) = "white" {}
        _Lightmap ("Lightmap (gamma)", 2D) = "grey" {}

        _UvScaleOffset ("Lightmap UV Scale (xy) Offset (zw)", Vector) = (0, 0, 0, 0)
        _Tex0ShiftPerSecond ("Albedo UV Shift Per Second (xy)", Vector) = (0, 0, 0, 0)
        _FlatColor ("Flat Color", Vector) = (1, 1, 1, 1)
        _AlphatestThreshold ("Alpha Test Threshold", Float) = 0.5
        _AlphaBlend ("Output Alpha", Float) = 0

        _SunDirection ("Direction To Sun (world)", Vector) = (0, 1, 0, 0)
        _ShadowParams ("Shadow Cutoff Slope (xy) LM Gate Factor (zw)", Vector) = (0.92, 0.96, 0.5, 1.5)
        _ShadowColor ("Shadow Color", Vector) = (0.65, 0.726, 0.77, 1)
        _FogColor ("Fog Color (rgb) Density (a)", Vector) = (0, 0, 0, 0)
        _FogLimit ("Fog Limit", Float) = 1

        [Toggle(_HH_LIGHTMAP)] _HHLightmap ("Material Lightmap", Float) = 0
        [Toggle(_HH_RECEIVE_SHADOW)] _HHReceiveShadow ("Receive Shadow", Float) = 0
        [Toggle(_HH_ALPHATEST)] _HHAlphaTest ("Alpha Test", Float) = 0
        [Toggle(_HH_SHADOW_ALPHATEST)] _HHShadowAlphaTest ("Shadow Alpha Test", Float) = 0
        [Toggle(_HH_SKINNED)] _HHSkinned ("Skinned Normal Fix-up", Float) = 0

        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite ("Z Write", Float) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Z Test", Float) = 2
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "HalloweenHangarCommon.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _Albedo_ST;
            float4 _UvScaleOffset;
            float4 _Tex0ShiftPerSecond;
            float4 _FlatColor;
            float _AlphatestThreshold;
            float _AlphaBlend;
            float _FogLimit;
            float _HHLightmap;
            float4 _SunDirection;
            float4 _ShadowParams;
            float4 _ShadowColor;
            float4 _FogColor;
            float _HHReceiveShadow;
            float _HHAlphaTest;
            float _HHShadowAlphaTest;
            float _HHSkinned;
            float _SrcBlend;
            float _DstBlend;
            float _ZWrite;
            float _ZTest;
            float _Cull;
        CBUFFER_END

        TEXTURE2D(_Albedo);   SAMPLER(sampler_Albedo);
        TEXTURE2D(_Lightmap); SAMPLER(sampler_Lightmap);

        struct HHAttributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv0 : TEXCOORD0;
            float2 uv1 : TEXCOORD1;
            float4 bindNormal : TEXCOORD5;
            float4 bindTangent : TEXCOORD6;
        };

        // TEXTURE0_ANIMATION_SHIFT: varTexCoord0 += frac(tex0ShiftPerSecond * globalTime);
        // the shift is zero on the materials without the define.
        float2 HHTexCoord0(float2 uv)
        {
            return uv + frac(_Tex0ShiftPerSecond.xy * _Time.y);
        }

        void HHAlphaClip(float2 uv)
        {
            float alpha = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, uv).a;
            clip(alpha - _AlphatestThreshold);
        }
        ENDHLSL

        Pass
        {
            Name "HHForward"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ _HH_LIGHTMAP
            #pragma multi_compile_local _ _HH_RECEIVE_SHADOW
            #pragma multi_compile_local_fragment _ _HH_ALPHATEST
            #pragma multi_compile_local_vertex _ _HH_SKINNED
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uv : TEXCOORD0;          // xy albedo, zw lightmap
                float3 positionWS : TEXCOORD1;
                float2 lightFog : TEXCOORD2;    // x per vertex NdotL, y fog amount
                float4 screenPos : TEXCOORD3;
            };

            Varyings vert(HHAttributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.screenPos = ComputeScreenPos(positions.positionCS);

                // SoftSkinnedNormal(): (k - S) * bind normal + S * skinned normal.
            #if defined(_HH_SKINNED)
                float3 normalOS = input.bindNormal.w * input.bindNormal.xyz + input.bindTangent.w * input.normalOS;
            #else
                float3 normalOS = input.normalOS;
            #endif
                float3 normalWS = HHObjectToWorldDir(normalOS);
                output.lightFog.x = max(dot(normalWS, _SunDirection.xyz), 0.0);
                output.lightFog.y = HHFogAmount(positions.positionWS, _FogColor.a, _FogLimit);

                output.uv.xy = HHTexCoord0(input.uv0);
                output.uv.zw = _UvScaleOffset.xy * input.uv1 + _UvScaleOffset.zw;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float4 textureColor0 = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, input.uv.xy);
            #if defined(_HH_ALPHATEST)
                clip(textureColor0.a - _AlphatestThreshold);
            #endif

            #if defined(_HH_RECEIVE_SHADOW)
                HHShadow shadowInfo = HHSampleShadow(input.positionWS, input.screenPos);
                float shadowValue = HHShadowValue(shadowInfo, input.lightFog.x, _ShadowParams.xy);
            #endif

            #if defined(_HH_LIGHTMAP)
                float3 lightmapColor = SAMPLE_TEXTURE2D(_Lightmap, sampler_Lightmap, input.uv.zw).rgb;
                #if defined(_HH_RECEIVE_SHADOW)
                    float lmBrightness = (lightmapColor.x + lightmapColor.y + lightmapColor.z) * 0.33;
                    float lmLighteningMult = lerp(_ShadowParams.w, 1.0, saturate(lmBrightness * _ShadowParams.z));
                    lightmapColor *= lerp(_ShadowColor.rgb * lmLighteningMult, float3(1.0, 1.0, 1.0), shadowValue);
                #endif
                float3 color = textureColor0.rgb * lightmapColor * 2.0;
            #else
                float3 color = textureColor0.rgb;
                #if defined(_HH_RECEIVE_SHADOW)
                    color *= lerp(_ShadowColor.rgb, float3(1.0, 1.0, 1.0), shadowValue);
                #endif
            #endif

                float4 outColor = float4(color, _AlphaBlend > 0.5 ? textureColor0.a : 1.0);
                outColor *= _FlatColor;
                outColor.rgb = lerp(outColor.rgb, _FogColor.rgb, input.lightFog.y);
                return float4(HHOutput(outColor.rgb), outColor.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local_fragment _ _HH_SHADOW_ALPHATEST

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(HHAttributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = HHShadowCasterPositionCS(positionWS, normalWS);
                output.uv = HHTexCoord0(input.uv0);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
            #if defined(_HH_SHADOW_ALPHATEST)
                HHAlphaClip(input.uv);
            #endif
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local_fragment _ _HH_ALPHATEST

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(HHAttributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = HHTexCoord0(input.uv0);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
            #if defined(_HH_ALPHATEST)
                HHAlphaClip(input.uv);
            #endif
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }
}
