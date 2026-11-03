// WoT Blitz 11.21 shading math (Materials/Shaders of the client, SHADER_ULTRA
// quality) for the Halloween hangar port. The client lights in linear space,
// converts to gamma with LinearToSRGB, applies vertex fog in gamma space and
// writes an 8-bit gamma framebuffer. Unity renders in linear space into an
// sRGB target, so every pass returns SRGBToLinear(saturate(davaGammaColor)):
// the stored framebuffer value then equals the client's.
#ifndef HALLOWEEN_HANGAR_COMMON_INCLUDED
#define HALLOWEEN_HANGAR_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

#define HH_PI 3.141592654
#define HH_INVERSE_PI 0.31830988618

// srgb.slh, SHADER_ULTRA branch.
float HHSRGBToLinear(float srgb)
{
    return srgb <= 0.0404482362771082 ? srgb / 12.92 : pow(max((srgb + 0.055) / 1.055, 0.0), 2.4);
}

float HHLinearToSRGB(float lin)
{
    return lin > 0.0031308 ? 1.055 * pow(max(lin, 0.0031308), 1.0 / 2.4) - 0.055 : 12.92 * lin;
}

float3 HHLinearToSRGB3(float3 c)
{
    return float3(HHLinearToSRGB(c.r), HHLinearToSRGB(c.g), HHLinearToSRGB(c.b));
}

// The client's framebuffer value (gamma, clamped by the UNORM target) as the
// linear colour Unity has to output for the same stored value.
float3 HHOutput(float3 davaGamma)
{
    float3 g = saturate(davaGamma);
    return float3(HHSRGBToLinear(g.r), HHSRGBToLinear(g.g), HHSRGBToLinear(g.b));
}

// BlitzFog.hlsl's range global. Its w is 0 while the graphics tab's fog is
// off (GraphicsQuality.ApplyFog) and 2 while the hangar's DAVA exponential
// fog is on, so the material's own fog constants follow the same switch.
float4 _BlitzFogRange;

// vp-fog-math.slh, exponential branch: computed per vertex from the eye-space
// distance, clamped to fogLimit and blended in gamma space.
float HHFogAmount(float3 positionWS, float density, float limit)
{
    float distance = length(positionWS - _WorldSpaceCameraPos);
    return clamp(1.0 - exp(-density * distance), 0.0, limit) * step(0.5, _BlitzFogRange.w);
}

// Unity world axes (y up) to the client's (z up) and back: x, y, z -> x, z, y.
float3 HHToDava(float3 v) { return v.xzy; }

// --- Real-time sun shadow -------------------------------------------------
// shadow-mapping.slh returns (shadowValue, -, 1 - outBlend, rawShadow). The
// client fades the cascades out at the far end of the last one; URP's
// equivalent is the main light shadow fade at the end of the shadow distance.
#if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
    #define HH_RECEIVE_SHADOW 1
#else
    #define HH_RECEIVE_SHADOW 0
#endif

struct HHShadow
{
    float raw;      // shadow map sample, 1 = lit
    float outBlend; // 0 inside the shadow range, 1 beyond it
};

HHShadow HHSampleShadow(float3 positionWS, float4 screenPos)
{
    HHShadow s;
    s.raw = 1.0;
    s.outBlend = 1.0;
#if HH_RECEIVE_SHADOW
    #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
        float4 shadowCoord = screenPos;
    #else
        float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
    #endif
    s.raw = MainLightRealtimeShadow(shadowCoord);
    s.outBlend = GetMainLightShadowFade(positionWS);
#endif
    return s;
}

// getCascadedShadow(...).x: the slope cut-off darkens surfaces turning away
// from the sun, outBlend lifts the shadow where the cascades end.
float HHShadowValue(HHShadow s, float NdotL, float2 cutoffSlope)
{
    float slopeFactor = saturate(1.0 - NdotL);
    float slopeCutoff = saturate((cutoffSlope.y - slopeFactor) / (cutoffSlope.y - cutoffSlope.x));
    return saturate(s.raw * slopeCutoff + s.outBlend);
}

// --- pbr-utils.slh ---------------------------------------------------------
float3 HHFresnelSchlick(float3 f0, float cosTheta)
{
    return f0 + (1.0 - f0) * pow(1.0 - cosTheta, 5.0);
}

float HHDGGX(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float f = (NdotH * a2 - NdotH) * NdotH + 1.0;
    return a2 / (HH_PI * f * f);
}

float HHGSmithGGX1(float NdotV, float a2)
{
    return 2.0 * NdotV / (NdotV + sqrt(a2 + (1.0 - a2) * NdotV * NdotV));
}

// Specular(): D * G * 0.25 / NdotV; the caller multiplies by NdotL again.
float HHSpecular(float NdotH, float NdotL, float NdotV, float roughness)
{
    float alpha = roughness * roughness;
    float alpha2 = alpha * alpha;
    float D = HHDGGX(NdotH, roughness);
    float V = HHGSmithGGX1(NdotL, alpha2) * HHGSmithGGX1(NdotV, alpha2) * 0.25 / NdotV;
    return D * V;
}

// ibl.slh MipFromRoughness with REFLECTION_CAPTURE_ROUGHEST_MIP 4 and
// REFLECTION_CAPTURE_ROUGHNESS_MIP_SCALE 1.2.
float HHMipFromRoughness(float roughness, float mipCount)
{
    float levelFrom1x1 = 4.0 - 1.2 * log2(roughness);
    return mipCount - 1.0 - levelFrom1x1;
}

// getPointLightPBR(): direct diffuse and specular of one light.
float3 HHDirectPBR(float3 N, float3 V, float3 L, float3 radiance, float3 diffuseColor, float3 F0, float roughness)
{
    float3 H = normalize(V + L);
    float NdotV = max(dot(N, V), 0.0001);
    float NdotL = max(dot(N, L), 0.0001);
    float NdotH = saturate(dot(N, H));
    float HdotV = saturate(dot(H, V));
    float3 light0 = radiance * NdotL;
    float3 F = HHFresnelSchlick(F0, HdotV);
    float3 diffuseDirect = light0 * diffuseColor * HH_INVERSE_PI;
    float3 specularDirect = light0 * F * HHSpecular(NdotH, NdotL, NdotV, roughness);
    return diffuseDirect + specularDirect;
}

// pbr-fp.sl point lights: 4 pi / d^falloff, faded over the last 5 % of the
// radius. positionRadius = (world position, radius), colorFalloff = (colour,
// falloff exponent). An unused slot has zero intensity.
float3 HHPointLight(float4 positionRadius, float4 colorFalloff, float intensity, float3 positionWS,
                    float3 N, float3 V, float3 baseColor, float metallic, float roughness)
{
    float3 diffuseColor = lerp(baseColor, float3(0.0, 0.0, 0.0), metallic);
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), baseColor, metallic);
    float3 toLight = positionRadius.xyz - positionWS;
    float distanceToLight = max(0.01, length(toLight));
    float smoothEdgeScale = 1.0 - smoothstep(positionRadius.w * 0.95, positionRadius.w, distanceToLight);
    float attenuation = 4.0 * HH_PI / pow(distanceToLight, colorFalloff.w);
    float3 L = normalize(toLight);
    float3 radiance = saturate(colorFalloff.rgb) * (smoothEdgeScale * attenuation * intensity);
    return HHDirectPBR(N, V, L, radiance, diffuseColor, F0, roughness);
}

// --- Image based lighting (ibl.slh, pbr-lighting.slh getPBR) ----------------
// The prefiltered cube maps keep the client's face layout, i.e. they are
// indexed with client world directions. environmentRotation turns the lookup
// about the client's up axis: v' = v * M, M rows (c, -s, 0), (s, c, 0), (0, 0, 1).
float3 HHEnvironmentDirection(float3 dirWS, float environmentRotation)
{
    float3 v = HHToDava(dirWS);
    float s, c;
    sincos(2.0 * HH_PI * environmentRotation, s, c);
    return float3(v.x * c + v.y * s, -v.x * s + v.y * c, v.z);
}

struct HHPBRInputs
{
    float3 polygonN;
    float3 N;
    float3 V;
    float3 L;
    float3 lightColor;      // lightColor0 * lightIntensity0
    float3 baseColor;
    float metallic;
    float roughness;
    float occlusion;
    float shadow;
    float3 emissive;
    float environmentRotation;
    float specularMipCount;
};

float3 HHGetPBR(HHPBRInputs p,
                TEXTURECUBE_PARAM(diffuseIrradianceMap, sampler_diffuseIrradianceMap),
                TEXTURECUBE_PARAM(specularReflectionMap, sampler_specularReflectionMap),
                TEXTURE2D_PARAM(specularBRDF, sampler_specularBRDF))
{
    float3 N = p.N;
    float3 H = normalize(p.V + p.L);
    float NdotV = max(dot(N, p.V), 0.0001);
    float NdotL = max(dot(N, p.L), 0.0001);
    float NdotH = saturate(dot(N, H));
    float HdotV = saturate(dot(H, p.V));
    float3 R = normalize(2.0 * NdotV * N - p.V);
    R = lerp(N, R, saturate(1.35 * (1.0 - p.roughness)));
    R = R + saturate(-dot(p.polygonN, R)) * p.polygonN; // clip light from the polygon's negative half space

    float3 diffuseColor = lerp(p.baseColor, float3(0.0, 0.0, 0.0), p.metallic);
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), p.baseColor, p.metallic);

    float3 envN = HHEnvironmentDirection(N, p.environmentRotation);
    float3 envR = HHEnvironmentDirection(R, p.environmentRotation);
    float3 diffuseIrradiance = SAMPLE_TEXTURECUBE_LOD(diffuseIrradianceMap, sampler_diffuseIrradianceMap, envN, 0.0).rgb;
    float lod = max(HHMipFromRoughness(p.roughness, p.specularMipCount), 0.0);
    float3 specularReflection = SAMPLE_TEXTURECUBE_LOD(specularReflectionMap, sampler_specularReflectionMap, envR, lod).rgb;
    float2 dfg = SAMPLE_TEXTURE2D_LOD(specularBRDF, sampler_specularBRDF, float2(NdotV, p.roughness), 0.0).xy;
    float3 F = F0 * dfg.x + dfg.y;

    float3 diffuseIBL = diffuseIrradiance * diffuseColor * p.occlusion;
    float3 specularIBL = specularReflection * F * p.occlusion;

    float3 light0 = p.lightColor * NdotL;
    float3 Fd = HHFresnelSchlick(F0, HdotV);
    float3 diffuseDirect = light0 * diffuseColor * HH_INVERSE_PI;
    float3 specularDirect = light0 * Fd * HHSpecular(NdotH, NdotL, NdotV, p.roughness);

    return p.emissive + (diffuseDirect + specularDirect) * p.shadow + diffuseIBL + specularIBL;
}

// --- Soft skinning frame fix-up ---------------------------------------------
// SoftSkinnedTBN() lerps every vector between its bind value and the joint
// transformed one per influence: sum_i lerp(v, R_i v, w_i) = (k - S) v + S Rv,
// with k the influence count and S the weight sum (often below 1 in the
// hangar's rigs). Unity skins with weights normalised to S, so the bind frame
// travels in UV5/UV6 (xyz = bind normal / tangent, w = k - S / S) and the bind
// binormal (not skinned by Unity) in UV2; the binormal is carried by the
// rotation between the bind and the skinned orthonormal frames.
void HHOrthonormalFrame(float3 n, float3 t, out float3 e1, out float3 e2, out float3 e3)
{
    e1 = normalize(n);
    float3 tt = t - dot(t, e1) * e1;
    e2 = dot(tt, tt) > 1e-12 ? normalize(tt) : normalize(cross(e1, abs(e1.x) < 0.9 ? float3(1, 0, 0) : float3(0, 1, 0)));
    e3 = cross(e1, e2);
}

void HHSkinnedFrame(float3 skinnedNormal, float3 skinnedTangent, float4 bindNormal, float4 bindTangent,
                    float3 bindBinormal, out float3 n, out float3 t, out float3 b)
{
    float a = bindNormal.w;
    float s = bindTangent.w;
    float3 b1, b2, b3, s1, s2, s3;
    HHOrthonormalFrame(bindNormal.xyz, bindTangent.xyz, b1, b2, b3);
    HHOrthonormalFrame(skinnedNormal, skinnedTangent, s1, s2, s3);
    float3 skinnedBinormal = s1 * dot(bindBinormal, b1) + s2 * dot(bindBinormal, b2) + s3 * dot(bindBinormal, b3);
    n = a * bindNormal.xyz + s * skinnedNormal;
    t = a * bindTangent.xyz + s * skinnedTangent;
    b = a * bindBinormal + s * skinnedBinormal;
}

// The client transforms normals, tangents and binormals with the inverse
// transpose of the world matrix and normalises them.
float3 HHObjectToWorldDir(float3 v)
{
    return SafeNormalize(mul(v, (float3x3)GetWorldToObjectMatrix()));
}

// --- Shadow caster ------------------------------------------------------------
// The client renders its shadow maps with its own depth biases; URP's caster
// bias (light settings) plays that role here.
float3 _LightDirection;

float4 HHShadowCasterPositionCS(float3 positionWS, float3 normalWS)
{
    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
#if UNITY_REVERSED_Z
    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif
    return positionCS;
}

// Two-channel maps (normal, roughness/metallic, AO/emissive, PBR lightmap) are
// stored grey + alpha: the client reads them with FP_SWIZZLE = .ga.
float3 HHUnpackNormalGA(float4 packedNormal)
{
    float3 n;
    n.xy = packedNormal.ga * 2.0 - 1.0;
    n.z = sqrt(1.0 - saturate(dot(n.xy, n.xy)));
    return n;
}

#endif
