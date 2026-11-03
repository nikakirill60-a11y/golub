#include "common.slh"

#if PARTICLES_FRESNEL_TO_ALPHA
    #include "fresnel-shlick.slh"
#endif

vertex_in
{    
    [vertex] float3 position : POSITION;
    [vertex] float3 normal : NORMAL;
    [vertex] float2 texcoord0 : TEXCOORD0;
    #if VERTEX_COLOR
        [vertex] float4 color0 : COLOR0;
    #endif

    [instance] float4 worldMatrix0 : TEXCOORD1;
    [instance] float4 worldMatrix1 : TEXCOORD2;
    [instance] float4 worldMatrix2 : TEXCOORD3;
    [instance] float4 spriteRect : TEXCOORD4;
    [instance] float4 color1 : COLOR1;

    #if FRAME_BLEND || PARTICLES_FLOWMAP_ANIMATION
        [instance] float4 nextSpriteRect : TEXCOORD5;
    #endif

    #if PARTICLES_MASK
        [instance] float4 maskSpriteRect : TANGENT;
    #endif

    #if FRAME_BLEND || PARTICLES_FLOWMAP_ANIMATION || PARTICLES_FRESNEL_TO_ALPHA || PARTICLES_ALPHA_REMAP
        // x - animation time.
        // y - alpha remap.
        // z - fresnel bias.
        // w - fresnel power.
        [instance] float4 texcoord6 : TEXCOORD6;
    #endif

    #if PARTICLES_FLOWMAP || PARTICLES_FLOWMAP_ANIMATION
        [instance] float4 flowMapRect : TEXCOORD7;
        [instance] float2 flowSpeedAndOffset : TEXCOORD8; // x - flow speed, y - flow offset.
    #endif

    #if PARTICLES_NOISE
        [instance] float4 noiseRect : NORMAL1;
        [instance] float noiseScale : NORMAL2;
    #endif

    #if PARTICLES_FLOWMAP_ANIMATION
        [instance] float4 nextFlowMapRect : NORMAL3;
    #endif

    #if PARTICLES_VERTEX_ANIMATION
        [instance] float4 vertexAnimationSpriteRect : BINORMAL;
        [instance] float vertexAnimationAmplitude : BLENDWEIGHT;
    #endif
};

vertex_out
{
    float4 position : SV_POSITION;
    float2 varTexCoord0 : TEXCOORD0;
    [lowp] half4 varColor1 : COLOR1;
    
    #if FRAME_BLEND || PARTICLES_FLOWMAP_ANIMATION
        float2 varNextFrameTexCoord : TEXCOORD1;
    #endif

    #if PARTICLES_MASK
        float2 varMaskUv : COLOR2;
    #endif

    #if PARTICLES_FLOWMAP || PARTICLES_FLOWMAP_ANIMATION
        float2 varParticleFlowTexCoord : TEXCOORD2;
    #endif

    #if USE_VERTEX_FOG
        [lowp] half4 varFog : TEXCOORD3;
    #endif

    #if PARTICLES_NOISE
        #if PARTICLES_FRESNEL_TO_ALPHA
            float4 varTexcoord4 : TEXCOORD4; // xy - noise uv, z - noise scale, w - fresnel.
        #else
            float3 varTexcoord4 : TEXCOORD4; // xy - noise uv, z - noise scale.
        #endif
    #elif PARTICLES_FRESNEL_TO_ALPHA
        float varTexcoord4 : TEXCOORD4; // Fresnel.
    #endif

    #if FRAME_BLEND && PARTICLES_ALPHA_REMAP
        half2 varTexcoord5 : TEXCOORD5; // x - animation time, y - alpha remap value.
    #elif FRAME_BLEND || PARTICLES_ALPHA_REMAP || PARTICLES_FLOWMAP_ANIMATION
        half varTexcoord5 : TEXCOORD5; // x - animation time.
    #endif

    #if PARTICLES_FLOWMAP || PARTICLES_FLOWMAP_ANIMATION
        [lowp] float3 varFlowData : TEXCOORD6; // xy - next flowmap uv, z - flow blend value.
    #endif

    #if SOFT_PARTICLES && RETRIEVE_FRAG_DEPTH_AVAILABLE
        float4 projectedPosition : TEXCOORD7;
    #endif
};

#if PARTICLES_VERTEX_ANIMATION
    uniform sampler2D vertexAnimationTex;
#endif

[auto][a] property float4x4 viewProjMatrix;

#if USE_VERTEX_FOG && FOG_ATMOSPHERE
    [auto][a] property float4 lightPosition0;
#endif

#if USE_VERTEX_FOG
    [auto][a] property float4x4 viewMatrix;
#endif

#if PARTICLES_FRESNEL_TO_ALPHA
    [auto][a] property float3 cameraDirection;
#endif

#include "vp-fog-props.slh"

#if USE_VERTEX_FOG
    [auto][a] property float3 cameraPosition;
#endif

vertex_out vp_main(vertex_in input)
{
    vertex_out  output;

    output.varTexCoord0.xy = float2(
                                input.spriteRect.x + (input.texcoord0.x * input.spriteRect.z),
                                input.spriteRect.y + (input.texcoord0.y * input.spriteRect.w));

    output.varColor1 = half4(input.color1);
    #if VERTEX_COLOR
		#if !PARTICLES_VERTEX_ANIMATION_MASK
			output.varColor1.xyz *= half3(input.color0.xyz);
		#endif
        output.varColor1.w *= half(input.color0.w);
    #endif

    #if PARTICLES_MASK
        output.varMaskUv = float2(
                                input.maskSpriteRect.x + (input.texcoord0.x * input.maskSpriteRect.z),
                                input.maskSpriteRect.y + (input.texcoord0.y * input.maskSpriteRect.w));
    #endif

    #if PARTICLES_FLOWMAP && !PARTICLES_FLOWMAP_ANIMATION
        float scaledTime = input.flowSpeedAndOffset.x;
        float flowOffset = input.flowSpeedAndOffset.y;
        output.varParticleFlowTexCoord.xy = float2(
                                input.flowMapRect.x + (input.texcoord0.x * input.flowMapRect.z),
                                input.flowMapRect.y + (input.texcoord0.y * input.flowMapRect.w));
        float2 flowPhases = frac(float2(scaledTime, scaledTime + 0.5)) - float2(0.5, 0.5);
        float flowBlend = abs(flowPhases.x * 2.0);
        output.varFlowData = float3(flowPhases * flowOffset, flowBlend);
    #elif PARTICLES_FLOWMAP_ANIMATION
        float flowOffset = input.flowSpeedAndOffset.y;
        output.varParticleFlowTexCoord.xy = float2(
                                input.flowMapRect.x + (input.texcoord0.x * input.flowMapRect.z),
                                input.flowMapRect.y + (input.texcoord0.y * input.flowMapRect.w));
        output.varFlowData.xy = float2(
                                    input.nextFlowMapRect.x + (input.texcoord0.x * input.nextFlowMapRect.z),
                                    input.nextFlowMapRect.y + (input.texcoord0.y * input.nextFlowMapRect.w));
        output.varFlowData.z = flowOffset;
    #endif

    #if PARTICLES_NOISE
        output.varTexcoord4.xy = float2(input.noiseRect.x + (input.texcoord0.x * input.noiseRect.z), input.noiseRect.y + (input.texcoord0.y * input.noiseRect.w));
        output.varTexcoord4.z = input.noiseScale;
    #endif

    float4x4 worldMatrix = float4x4(
        float4(input.worldMatrix0.x,  input.worldMatrix1.x,  input.worldMatrix2.x, 0.0),
        float4(input.worldMatrix0.y,  input.worldMatrix1.y,  input.worldMatrix2.y, 0.0),
        float4(input.worldMatrix0.z,  input.worldMatrix1.z,  input.worldMatrix2.z, 0.0),
        float4(input.worldMatrix0.w,  input.worldMatrix1.w,  input.worldMatrix2.w, 1.0)
    );

    float4 modelPos = float4(input.position.xyz, 1.0);

    #if PARTICLES_VERTEX_ANIMATION
		float2 vertexAnimationUv = input.vertexAnimationSpriteRect.xy + input.texcoord0.xy * input.vertexAnimationSpriteRect.zw;
		float vertexAnimationValue = 2.0 * tex2Dlod(vertexAnimationTex, vertexAnimationUv, 0.0).x - 1.0;
		#if VERTEX_COLOR && PARTICLES_VERTEX_ANIMATION_MASK
			vertexAnimationValue *= input.color0.x;
		#endif
		modelPos.xyz += input.normal * (vertexAnimationValue * input.vertexAnimationAmplitude);
    #endif
	
    float4 worldPos = mul(modelPos, worldMatrix);
    output.position = mul(worldPos, viewProjMatrix);

    #if SOFT_PARTICLES && RETRIEVE_FRAG_DEPTH_AVAILABLE
        output.projectedPosition = output.position;
    #endif

    #if PARTICLES_FRESNEL_TO_ALPHA        
        // We assume that non-uniform scale is not allowed when fresnel to alpha is enabled.
        float3 normalWorld = normalize(mul(float4(input.normal, 0.0f), worldMatrix).xyz);
        float normDotCam = dot(normalWorld, cameraDirection);
        float fresnelBias = input.texcoord6.z;
        float fresnelPower = input.texcoord6.w;
        float fresnelToAlpha = FresnelShlickCustom(normDotCam, fresnelBias, fresnelPower);
        
        #if PARTICLES_NOISE
            output.varTexcoord4.w = fresnelToAlpha;
        #else
            output.varTexcoord4 = fresnelToAlpha;
        #endif 
    #endif

    #if USE_VERTEX_FOG
        float3 FOG_view_position = mul(worldPos, viewMatrix).xyz;
        #if USE_FOG_HALFSPACE
            float3 FOG_world_position = worldPos.xyz;
        #endif
        #define FOG_eye_position cameraPosition
        #define FOG_in_position worldPos.xyz
        #define FOG_to_light_dir lightPosition0.xyz
        #include "vp-fog-math.slh"
        output.varFog = half4(FOG_result);
    #endif

    #if FRAME_BLEND || PARTICLES_FLOWMAP_ANIMATION
        output.varNextFrameTexCoord.xy = float2(
                                input.nextSpriteRect.x + (input.texcoord0.x * input.nextSpriteRect.z),
                                input.nextSpriteRect.y + (input.texcoord0.y * input.nextSpriteRect.w));
        #if PARTICLES_ALPHA_REMAP
            output.varTexcoord5.x = input.texcoord6.x;
            output.varTexcoord5.y = input.texcoord6.y;
        #else
            output.varTexcoord5 = input.texcoord6.x;
        #endif
    #elif PARTICLES_ALPHA_REMAP
        output.varTexcoord5 = input.texcoord6.y;
    #endif

    return output;
}
