// Port of the WoT Blitz 11.21 "PBR.material" (Shaders/Default/pbr-vp.sl,
// pbr-fp.sl) at SHADER_ULTRA quality, as used by hangar_halloween_2026.
// Lighting constants (sun, fog, IBL, the renderer's two point lights) are
// material properties written by the hangar builder from the client scene.
Shader "BlitzLegacy/HalloweenHangar/PBR"
{
    Properties
    {
        _BaseColorMap ("Base Color (sRGB)", 2D) = "white" {}
        _BaseNormalMap ("Normal (GA)", 2D) = "grey" {}
        _BaseRMMap ("Roughness / Metallic (GA)", 2D) = "white" {}
        _MiscMap ("Occlusion / Emissive Mask (GA)", 2D) = "white" {}
        _PbrLightmap ("Directional Shadow / Occlusion Lightmap (GA)", 2D) = "white" {}
        _SpecularBRDF ("Specular BRDF LUT", 2D) = "black" {}
        _DiffuseIrradianceMap ("Diffuse Irradiance", Cube) = "black" {}
        _SpecularReflectionMap ("Specular Reflection", Cube) = "black" {}

        _BaseColorFactor ("Base Color Factor", Vector) = (1, 1, 1, 1)
        _RoughnessMetallicFactor ("Roughness Metallic Factor", Vector) = (1, 1, 0, 0)
        _EmissiveColor ("Emissive Color", Vector) = (0, 0, 0, 0)
        _NormalScale ("Normal Scale", Float) = 1
        _AlphatestThreshold ("Alpha Test Threshold", Float) = 0.5
        _AlphaBlend ("Output Alpha", Float) = 0
        _PbrUvScaleOffset ("Lightmap UV Scale (xy) Offset (zw)", Vector) = (0, 0, 0, 0)

        _SunDirection ("Direction To Sun (world)", Vector) = (0, 1, 0, 0)
        _SunColor ("Sun Color x Intensity", Vector) = (1, 1, 1, 0)
        _ShadowParams ("Shadow Cutoff Slope (xy) Lit Normal Scale (z)", Vector) = (0.92, 0.96, 0.15, 0)
        _FogColor ("Fog Color (rgb) Density (a)", Vector) = (0, 0, 0, 0)
        _FogLimit ("Fog Limit", Float) = 1
        _IBLParams ("Specular Mip Count (x) Environment Rotation (y)", Vector) = (9, 0, 0, 0)
        _PointLight0 ("Point Light 0 Position (xyz) Radius (w)", Vector) = (0, 0, 0, 0)
        _PointLight0Color ("Point Light 0 Color (rgb) Falloff (w)", Vector) = (0, 0, 0, 2)
        _PointLight1 ("Point Light 1 Position (xyz) Radius (w)", Vector) = (0, 0, 0, 0)
        _PointLight1Color ("Point Light 1 Color (rgb) Falloff (w)", Vector) = (0, 0, 0, 2)
        _PointLightIntensity ("Point Light Intensities (xy)", Vector) = (0, 0, 0, 0)

        [Toggle(_HH_LIGHTMAP)] _HHLightmap ("PBR Lightmap", Float) = 0
        [Toggle(_HH_ALPHATEST)] _HHAlphaTest ("Alpha Test", Float) = 0
        [Toggle(_HH_SHADOW_ALPHATEST)] _HHShadowAlphaTest ("Shadow Alpha Test", Float) = 0
        [Toggle(_HH_SKINNED)] _HHSkinned ("Skinned Frame Fix-up", Float) = 0

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
            float4 _BaseColorMap_ST;
            float4 _BaseColorFactor;
            float4 _RoughnessMetallicFactor;
            float4 _EmissiveColor;
            float _NormalScale;
            float _AlphatestThreshold;
            float _AlphaBlend;
            float _FogLimit;
            float4 _PbrUvScaleOffset;
            float4 _SunDirection;
            float4 _SunColor;
            float4 _ShadowParams;
            float4 _FogColor;
            float4 _IBLParams;
            float4 _PointLight0;
            float4 _PointLight0Color;
            float4 _PointLight1;
            float4 _PointLight1Color;
            float4 _PointLightIntensity;
            float _HHLightmap;
            float _HHAlphaTest;
            float _HHShadowAlphaTest;
            float _HHSkinned;
            float _SrcBlend;
            float _DstBlend;
            float _ZWrite;
            float _ZTest;
            float _Cull;
        CBUFFER_END

        TEXTURE2D(_BaseColorMap);           SAMPLER(sampler_BaseColorMap);
        TEXTURE2D(_BaseNormalMap);          SAMPLER(sampler_BaseNormalMap);
        TEXTURE2D(_BaseRMMap);              SAMPLER(sampler_BaseRMMap);
        TEXTURE2D(_MiscMap);                SAMPLER(sampler_MiscMap);
        TEXTURE2D(_PbrLightmap);            SAMPLER(sampler_PbrLightmap);
        TEXTURE2D(_SpecularBRDF);           SAMPLER(sampler_SpecularBRDF);
        TEXTURECUBE(_DiffuseIrradianceMap); SAMPLER(sampler_DiffuseIrradianceMap);
        TEXTURECUBE(_SpecularReflectionMap); SAMPLER(sampler_SpecularReflectionMap);

        struct HHAttributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 tangentOS : TANGENT;
            float2 uv0 : TEXCOORD0;
            float2 uv1 : TEXCOORD1;
            float3 binormalOS : TEXCOORD2;
            float4 bindNormal : TEXCOORD5;
            float4 bindTangent : TEXCOORD6;
        };

        // Object space tangent frame of the client: for skinned meshes the
        // client's weighted lerp of bind and joint transformed vectors.
        void HHObjectFrame(HHAttributes input, out float3 n, out float3 t, out float3 b)
        {
        #if defined(_HH_SKINNED)
            HHSkinnedFrame(input.normalOS, input.tangentOS.xyz, input.bindNormal, input.bindTangent, input.binormalOS, n, t, b);
        #else
            n = input.normalOS;
            t = input.tangentOS.xyz;
            b = input.binormalOS;
        #endif
        }

        void HHAlphaClip(float2 uv, bool enabled)
        {
            if (enabled)
            {
                float alpha = SAMPLE_TEXTURE2D(_BaseColorMap, sampler_BaseColorMap, uv).a;
                clip(alpha - _AlphatestThreshold);
            }
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
            #pragma multi_compile_local _ _HH_ALPHATEST
            #pragma multi_compile_local_vertex _ _HH_SKINNED
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uv : TEXCOORD0;              // xy albedo, zw lightmap
                float3 positionWS : TEXCOORD1;
                float3 tangentWS : TEXCOORD2;
                float3 binormalWS : TEXCOORD3;
                float3 normalWS : TEXCOORD4;
                float4 toLightTangentFog : TEXCOORD5; // xyz sun in tangent space, w fog amount
                float4 screenPos : TEXCOORD6;
            };

            Varyings vert(HHAttributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.screenPos = ComputeScreenPos(positions.positionCS);

                float3 n, t, b;
                HHObjectFrame(input, n, t, b);
                float3 tWS = HHObjectToWorldDir(t);
                float3 bWS = HHObjectToWorldDir(b);
                float3 nWS = HHObjectToWorldDir(n);
                output.tangentWS = tWS;
                output.binormalWS = bWS;
                output.normalWS = nWS;

                float3 toLight = _SunDirection.xyz;
                output.toLightTangentFog.xyz = float3(dot(toLight, tWS), dot(toLight, bWS), dot(toLight, nWS));
                output.toLightTangentFog.w = HHFogAmount(positions.positionWS, _FogColor.a, _FogLimit);

                output.uv.xy = input.uv0;
                output.uv.zw = _PbrUvScaleOffset.xy * input.uv1 + _PbrUvScaleOffset.zw;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float2 texCoord = input.uv.xy;
                float4 baseColor = SAMPLE_TEXTURE2D(_BaseColorMap, sampler_BaseColorMap, texCoord);
            #if defined(_HH_ALPHATEST)
                clip(baseColor.a - _AlphatestThreshold);
            #endif
                baseColor *= _BaseColorFactor;

                float3 baseNormal = HHUnpackNormalGA(SAMPLE_TEXTURE2D(_BaseNormalMap, sampler_BaseNormalMap, texCoord));
                float2 roughnessMetallic = SAMPLE_TEXTURE2D(_BaseRMMap, sampler_BaseRMMap, texCoord).ga * _RoughnessMetallicFactor.xy;
                float2 miscellaneous = SAMPLE_TEXTURE2D(_MiscMap, sampler_MiscMap, texCoord).ga;
                float occlusion = miscellaneous.r;

                float3 N = baseNormal;
                N.xy *= _NormalScale;
                N = normalize(N.x * input.tangentWS + N.y * input.binormalWS + N.z * input.normalWS);
                float3 polygonN = normalize(input.normalWS);

            #if defined(_HH_LIGHTMAP)
                float2 directionalAndAO = SAMPLE_TEXTURE2D(_PbrLightmap, sampler_PbrLightmap, input.uv.zw).ga;
                occlusion *= directionalAndAO.y;
            #endif

                baseColor = saturate(baseColor);
                roughnessMetallic = saturate(roughnessMetallic);
                occlusion = saturate(occlusion);
                float3 emission = miscellaneous.g * _EmissiveColor.rgb;

                float3 V = normalize(_WorldSpaceCameraPos - input.positionWS);
                float3 L = _SunDirection.xyz;

                HHShadow shadowInfo = HHSampleShadow(input.positionWS, input.screenPos);
            #if defined(_HH_LIGHTMAP)
                float shadow = lerp(directionalAndAO.x, shadowInfo.raw, 1.0 - shadowInfo.outBlend);
            #else
                float3 normalShady = normalize(float3(baseNormal.xy * _ShadowParams.z, baseNormal.z));
                float NdotLShady = max(dot(normalShady, normalize(input.toLightTangentFog.xyz)), 0.0);
                float shadow = HHShadowValue(shadowInfo, NdotLShady, _ShadowParams.xy);
            #endif

                HHPBRInputs p;
                p.polygonN = polygonN;
                p.N = N;
                p.V = V;
                p.L = L;
                p.lightColor = _SunColor.rgb;
                p.baseColor = baseColor.rgb;
                p.metallic = roughnessMetallic.y;
                p.roughness = roughnessMetallic.x;
                p.occlusion = occlusion;
                p.shadow = shadow;
                p.emissive = emission;
                p.environmentRotation = _IBLParams.y;
                p.specularMipCount = _IBLParams.x;
                float3 color = HHGetPBR(p,
                    TEXTURECUBE_ARGS(_DiffuseIrradianceMap, sampler_DiffuseIrradianceMap),
                    TEXTURECUBE_ARGS(_SpecularReflectionMap, sampler_SpecularReflectionMap),
                    TEXTURE2D_ARGS(_SpecularBRDF, sampler_SpecularBRDF));

                UNITY_BRANCH if (_PointLightIntensity.x > 0.0)
                    color += HHPointLight(_PointLight0, _PointLight0Color, _PointLightIntensity.x, input.positionWS,
                                          N, V, baseColor.rgb, roughnessMetallic.y, roughnessMetallic.x);
                UNITY_BRANCH if (_PointLightIntensity.y > 0.0)
                    color += HHPointLight(_PointLight1, _PointLight1Color, _PointLightIntensity.y, input.positionWS,
                                          N, V, baseColor.rgb, roughnessMetallic.y, roughnessMetallic.x);

                float alpha = _AlphaBlend > 0.5 ? baseColor.a : 1.0;
                float3 gammaColor = HHLinearToSRGB3(color);
                gammaColor = lerp(gammaColor, _FogColor.rgb, input.toLightTangentFog.w);
                return float4(HHOutput(gammaColor), alpha);
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
            #pragma multi_compile_local _ _HH_SHADOW_ALPHATEST

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
                output.uv = input.uv0;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
            #if defined(_HH_SHADOW_ALPHATEST)
                HHAlphaClip(input.uv, true);
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
            #pragma multi_compile_local _ _HH_ALPHATEST

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(HHAttributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv0;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
            #if defined(_HH_ALPHATEST)
                HHAlphaClip(input.uv, true);
            #endif
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }
}
