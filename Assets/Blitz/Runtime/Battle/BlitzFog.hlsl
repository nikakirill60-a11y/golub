#ifndef BLITZ_FOG_INCLUDED
#define BLITZ_FOG_INCLUDED

// Port of Godot's fog_process (scene_forward_lights.glsl). The source fog
// carries a density, a depth curve and an optional height term on top of the
// range, and Unity's built-in linear fog expresses none of the three: it
// ramps linearly from start to end and stops there. So the ported shaders
// reproduce the source curve themselves and read the numbers from these
// globals, which SourceEnvironment pushes straight out of the manifest.
//
// The colour arrives as a raw vector rather than a Color, which is the point:
// Unity gamma-decodes a Color on its way to the GPU, and the manifest already
// holds the linear value Godot authored.
float4 _BlitzFogColour; // rgb: fog_light_color * fog_light_energy * exposure, a: 1 while fog is on
float4 _BlitzFogDepth;  // x: fog_depth_begin, y: fog_depth_end, z: fog_density, w: fog_depth_curve
float4 _BlitzFogHeight; // x: fog_height, y: fog_height_density

// The 4.8 client maps carry DAVA fog instead, which is a plain linear ramp
// between fogStart and fogEnd clamped at fogLimit. Unity's built-in linear fog
// has no such clamp and reaches pure fog colour at its far distance, which is
// what turned every distant building white while the terrain stayed clear.
float4 _BlitzFogRange; // x: fogStart, y: fogEnd, z: fogLimit, w: 1 linear / 2 exponential DAVA fog

// FOG_ATMOSPHERE, the flag the client's global material also sets. With it the
// haze is not one colour: DAVA reads fogAtmosphereColorSky away from the sun
// and fogAtmosphereColorSun towards it, fading in from the flat fogColor over
// fogAtmosphereDistance. Left out, the map's horizon is a flat near-white
// wall instead of the authored blue-grey.
float4 _BlitzFogSky; // rgb: fogAtmosphereColorSky, a: 1 while atmosphere fog is on
float4 _BlitzFogSun; // rgb: fogAtmosphereColorSun, a: fogAtmosphereScattering
float4 _BlitzFogSunDirection; // xyz: the direction sunlight travels, w: fogAtmosphereDistance

// FOG_HALFSPACE of a client-shaded 4.8 map (Karieri): haze that pools in the
// low ground, added on top of the distance fog before the fogLimit clamp.
float4 _BlitzFogHalfspace; // x: fogHalfspaceDensity, y: fogHalfspaceFalloff, z: fogHalfspaceHeight, w: fogHalfspaceLimit
// x: 1 for a map drawn with the client's own fog formulas - the atmosphere
// angle is then vp-fog-math's dot * 0.5 + 0.5 whatever the distance curve;
// y: 1 while FOG_HALFSPACE is on; z: 1 for its FOG_HALFSPACE_LINEAR variant.
// All zero (the default of an unset global) keeps every other scene as it was.
float4 _BlitzFogClient;

// vp-fog-math.slh's half-space amount, in Unity space: the client's z (up) is
// Unity's y, so `eye.z` is the camera height and `view.z` the ray's rise.
float BlitzFogHalfspaceAmount(float3 positionWS)
{
    float3 eye = GetCameraPositionWS();
    float3 view = positionWS - eye;
    float distance = max(length(view), 0.0001);
    float amount;
    if (_BlitzFogClient.z > 0.5)
    {
        // FOG_HALFSPACE_LINEAR: Lengyel's unified fog.
        float k = step(eye.y, _BlitzFogHalfspace.z);
        float fdotp = positionWS.y - _BlitzFogHalfspace.z;
        float fdotc = eye.y - _BlitzFogHalfspace.z;
        float c1 = k * (fdotp + fdotc);
        float c2 = (1.0 - 2.0 * k) * fdotp;
        float g = min(c2, 0.0);
        g = -distance * _BlitzFogHalfspace.x * (c1 - g * g / max(abs(view.y), 0.0001));
        amount = 1.0 - exp2(-g);
    }
    else
    {
        // density * exp(-falloff * (eye - height)) * (1 - exp(-falloff * k * d)) / k,
        // whose limit for a level ray (k -> 0) is density * exp(..) * falloff * d.
        float k = view.y / distance;
        float b = eye.y - _BlitzFogHalfspace.z;
        float falloff = _BlitzFogHalfspace.y;
        float along = abs(k) > 0.0001
            ? (1.0 - exp(-falloff * k * distance)) / k
            : falloff * distance;
        amount = _BlitzFogHalfspace.x * exp(-falloff * b) * along;
    }
    return clamp(amount, 0.0, _BlitzFogHalfspace.w);
}

float BlitzFogAmount(float3 positionWS)
{
    if (_BlitzFogRange.w > 0.5)
    {
        float distance = length(positionWS - GetCameraPositionWS());
        // 4.8 premium hangar disables FOG_LINEAR and authors fogDensity=0.005.
        // Keep the old map branch unchanged; their range.w is always one.
        float amount = _BlitzFogRange.w > 1.5
            ? 1.0 - exp(-_BlitzFogDepth.z * distance)
            : (distance - _BlitzFogRange.x) / max(_BlitzFogRange.y - _BlitzFogRange.x, 0.0001);
        // vp-fog-math adds the half-space term before the fogLimit clamp.
        if (_BlitzFogClient.y > 0.5)
            amount += BlitzFogHalfspaceAmount(positionWS);
        return clamp(amount, 0.0, _BlitzFogRange.z);
    }

    if (_BlitzFogColour.a < 0.5)
    {
        return 0.0;
    }

    // Godot measures the depth term on the length of the view-space fragment
    // position, which is its distance to the camera.
    float depth = length(positionWS - GetCameraPositionWS());
    float fogZ = smoothstep(_BlitzFogDepth.x, _BlitzFogDepth.y, depth);
    float amount = pow(fogZ, _BlitzFogDepth.w) * _BlitzFogDepth.z;

    // The height term is a separate exponential the source takes the maximum
    // of, not a factor: it fills the basin below fog_height even up close.
    if (abs(_BlitzFogHeight.y) >= 0.0001)
    {
        float above = positionWS.y - _BlitzFogHeight.x;
        float heightAmount = 1.0 - exp(min(0.0, above * _BlitzFogHeight.y));
        amount = max(heightAmount, amount);
    }

    return saturate(amount);
}

half3 BlitzFogColour(float3 positionWS)
{
    if (_BlitzFogSky.a < 0.5)
    {
        return (half3)_BlitzFogColour.rgb;
    }

    float3 offset = positionWS - GetCameraPositionWS();
    float distance = length(offset);
    float3 view = distance > 0.0001 ? offset / distance : float3(0.0, 0.0, 1.0);
    float towardsSun = saturate(dot(view, -_BlitzFogSunDirection.xyz));
    if (_BlitzFogRange.w > 1.5 || _BlitzFogClient.x > 0.5)
        towardsSun = dot(view, -_BlitzFogSunDirection.xyz) * 0.5 + 0.5;
    float3 atmosphere = lerp(
        _BlitzFogSky.rgb, _BlitzFogSun.rgb,
        pow(towardsSun, max(_BlitzFogSun.a, 0.0001)));
    float blend = saturate(distance / max(_BlitzFogSunDirection.w, 0.0001));
    return (half3)lerp(_BlitzFogColour.rgb, atmosphere, blend);
}

half3 BlitzMixFog(half3 colour, float3 positionWS)
{
    return lerp(colour, BlitzFogColour(positionWS), (half)BlitzFogAmount(positionWS));
}

#endif
