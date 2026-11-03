#ifndef BLITZ_GODOT_MAP_INCLUDED
#define BLITZ_GODOT_MAP_INCLUDED

// Shared terms of the Godot 4.7 mobile renderer as the Rudniki scene uses it
// (Tools/port_godot_map_scene.py). The project forces vertex shading and
// Lambert, the sun is baked (light_bake_mode static) and casts no shadow:
//
//   lightmapped  colour = albedo * bicubic(lightmap) + emission
//   otherwise    colour = albedo * (ambient + light * max(N.L, 0)) + emission
//                where the light term is evaluated per vertex
//   unshaded     colour = albedo
//   then         fog (depth fog, BlitzFog.hlsl) per pixel
//
// pi cancels: Godot scales the light energy by pi and the Lambert BRDF by
// 1/pi. Specular is zero everywhere (light_specular = 0).

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "../BlitzFog.hlsl"

// rgb: srgb_to_linear(background_color) * background_energy, the ambient a
// non-lightmapped surface gets (ambient source = background).
float4 _GodotAmbient;
// xyz: unit vector towards the sun in Unity world space.
float4 _GodotLightDirection;
// rgb: linear light colour times its energy.
float4 _GodotLightColor;

// Godot samples with v growing downwards; Unity imports images bottom-up.
float2 GodotUv(float2 uv)
{
    return float2(uv.x, 1.0 - uv.y);
}

// Diffuse light of one vertex: ambient plus the Lambert sun.
half3 GodotVertexLight(float3 normalWS)
{
    float3 n = normalize(normalWS);
    float lambert = saturate(dot(n, _GodotLightDirection.xyz));
    return (half3)(_GodotAmbient.rgb + _GodotLightColor.rgb * lambert);
}

// Godot's lightmap bicubic (scene_forward_lights_inc / textureArray_bicubic):
// four bilinear taps placed by the cubic B-spline weights.
float GodotW0(float a) { return (1.0 / 6.0) * (a * (a * (-a + 3.0) - 3.0) + 1.0); }
float GodotW1(float a) { return (1.0 / 6.0) * (a * a * (3.0 * a - 6.0) + 4.0); }
float GodotW2(float a) { return (1.0 / 6.0) * (a * (a * (-3.0 * a + 3.0) + 3.0) + 1.0); }
float GodotW3(float a) { return (1.0 / 6.0) * (a * a * a); }
float GodotG0(float a) { return GodotW0(a) + GodotW1(a); }
float GodotG1(float a) { return GodotW2(a) + GodotW3(a); }
float GodotH0(float a) { return -1.0 + GodotW1(a) / (GodotW0(a) + GodotW1(a)); }
float GodotH1(float a) { return 1.0 + GodotW3(a) / (GodotW2(a) + GodotW3(a)); }

// size: xy texture size in texels, zw its reciprocal. The B-spline is
// symmetric, so sampling the v-flipped image gives the same result.
half3 GodotBicubic(TEXTURE2D_PARAM(tex, samplerTex), float2 uv, float4 size)
{
    uv = uv * size.xy + 0.5;
    float2 iuv = floor(uv);
    float2 fuv = frac(uv);

    float g0x = GodotG0(fuv.x);
    float g1x = GodotG1(fuv.x);
    float h0x = GodotH0(fuv.x);
    float h1x = GodotH1(fuv.x);
    float h0y = GodotH0(fuv.y);
    float h1y = GodotH1(fuv.y);

    float2 p0 = (float2(iuv.x + h0x, iuv.y + h0y) - 0.5) * size.zw;
    float2 p1 = (float2(iuv.x + h1x, iuv.y + h0y) - 0.5) * size.zw;
    float2 p2 = (float2(iuv.x + h0x, iuv.y + h1y) - 0.5) * size.zw;
    float2 p3 = (float2(iuv.x + h1x, iuv.y + h1y) - 0.5) * size.zw;

    float3 c0 = SAMPLE_TEXTURE2D_LOD(tex, samplerTex, p0, 0).rgb;
    float3 c1 = SAMPLE_TEXTURE2D_LOD(tex, samplerTex, p1, 0).rgb;
    float3 c2 = SAMPLE_TEXTURE2D_LOD(tex, samplerTex, p2, 0).rgb;
    float3 c3 = SAMPLE_TEXTURE2D_LOD(tex, samplerTex, p3, 0).rgb;
    return (half3)(GodotG0(fuv.y) * (g0x * c0 + g1x * c1) +
                   GodotG1(fuv.y) * (g0x * c2 + g1x * c3));
}

// The billboard of _vegetation_shader.gdshader and of SpriteBase3D's
// material: the camera basis around the instance origin, each axis scaled by
// the length of the instance basis column (billboard keep scale). The mesh
// keeps the pivot in POSITION, the scale in NORMAL and the corner in UV2.
float3 GodotBillboard(float3 pivotWS, float3 local, float3 scale)
{
    float3 right = float3(UNITY_MATRIX_I_V._m00, UNITY_MATRIX_I_V._m10, UNITY_MATRIX_I_V._m20);
    float3 up = float3(UNITY_MATRIX_I_V._m01, UNITY_MATRIX_I_V._m11, UNITY_MATRIX_I_V._m21);
    float3 back = float3(UNITY_MATRIX_I_V._m02, UNITY_MATRIX_I_V._m12, UNITY_MATRIX_I_V._m22);
    return pivotWS + right * (local.x * scale.x) + up * (local.y * scale.y) + back * (local.z * scale.z);
}

half3 GodotFog(half3 colour, float3 positionWS, float enabled)
{
    return enabled > 0.5 ? BlitzMixFog(colour, positionWS) : colour;
}

#endif
