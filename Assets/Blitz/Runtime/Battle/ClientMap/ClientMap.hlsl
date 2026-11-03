#ifndef CLIENT_MAP_INCLUDED
#define CLIENT_MAP_INCLUDED

// Shared pieces of the shaders that draw a 4.8 client map with the client's
// own formulas (Materials/Shaders/Default/materials-*.sl, Landscape/tilemask-*,
// water-*). Three rules hold for all of them:
//
// * Every texture of such a map imports without sRGB decoding, so a sample is
//   the value DAVA reads. The arithmetic - lightmap * 2, tile mask * 2, the
//   fog lerp, the shadow multiply - runs on those encoded values exactly like
//   the client's gamma framebuffer, and only the result is decoded for
//   Unity's linear target.
// * DAVA loads an image top row first, so v = 0 is the top; Unity's is the
//   bottom. DavaUv flips v after the client's own UV arithmetic.
// * World space is Unity's; the client's vectors are (x, z, y) of it.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "../BlitzFog.hlsl"

// rgb: the global material's shadowColor; what ShadowRect multiplies the
// frame by inside a shadow volume.
float4 _ClientShadowColor;

float2 DavaUv(float2 uv)
{
    return float2(uv.x, 1.0 - uv.y);
}

// vp-fog-math.slh: rgb is the (atmosphere) fog colour, a the amount.
half4 ClientFog(float3 positionWS)
{
    return half4(BlitzFogColour(positionWS), BlitzFogAmount(positionWS));
}

// The realtime shadow of the main light without URP's strength and fade,
// which the client has no notion of: a pixel is in a volume or it is not.
half ClientShadowMask(float3 positionWS)
{
#if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
    float4 coord = TransformWorldToShadowCoord(positionWS);
    half shadow = MainLightRealtimeShadow(coord);
    half strength = GetMainLightShadowParams().x;
    shadow = strength > 0.0001 ? 1.0 - (1.0 - shadow) / strength : 1.0;
    return lerp(saturate(shadow), 1.0, GetMainLightShadowFade(positionWS));
#else
    return 1.0;
#endif
}

// The framebuffer write: DAVA clamps to the 8-bit target, ShadowRect then
// multiplies by shadowColor where a volume covers the pixel.
half3 ClientShadowed(half3 encoded, float3 positionWS)
{
    half3 colour = saturate(encoded);
    if (_ClientShadowColor.a > 0.5)
        colour *= lerp((half3)_ClientShadowColor.rgb, 1.0, ClientShadowMask(positionWS));
    return colour;
}

half3 ClientOutput(half3 encoded)
{
#ifndef UNITY_COLORSPACE_GAMMA
    return SRGBToLinear(saturate(encoded));
#else
    return saturate(encoded);
#endif
}

#endif
