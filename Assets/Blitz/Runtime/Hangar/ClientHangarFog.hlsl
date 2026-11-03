#ifndef CLIENT_HANGAR_FOG_INCLUDED
#define CLIENT_HANGAR_FOG_INCLUDED
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "../Battle/BlitzFog.hlsl"

// Original 4.8 vp-fog-math.slh calculates RGBA fog at each vertex.
// materials-fp.sl interpolates that varying and mixes it with the encoded
// texture result BEFORE writing Android's gamma-space framebuffer.
// Do not mix raw source fog colours with Unity's decoded linear texture data.
half4 ClientHangarVertexFog(float3 world)
{
    return half4(BlitzFogColour(world), BlitzFogAmount(world));
}
half3 ClientHangarEncoded(half3 sampled)
{
    #ifndef UNITY_COLORSPACE_GAMMA
        return LinearToSRGB(sampled);
    #else
        return sampled;
    #endif
}
half3 ClientHangarFinish(half3 encoded, half4 fog)
{
    half3 result = lerp(encoded, fog.rgb, fog.a);
    #ifndef UNITY_COLORSPACE_GAMMA
        return SRGBToLinear(result);
    #else
        return result;
    #endif
}
#endif
