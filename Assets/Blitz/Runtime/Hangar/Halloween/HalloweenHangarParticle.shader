// Port of the WoT Blitz 11.21 particle materials (Shaders/Default/
// particle-materials-vp/fp.sl for billboards, mesh-particle-materials-vp/fp.sl
// for mesh particles, Materials/Particles/*.material) for the Halloween hangar.
//
// The client computes per particle data on the CPU (ParticleRenderObject).
// Here Unity's particle system supplies the particle age, lifetime and stable
// random numbers through custom vertex streams and the shader rebuilds the
// same values:
//   POSITION, NORMAL, COLOR (mesh vertex colour, white for billboards),
//   TEXCOORD0 = (uv.xy, AgePercent, InvStartLifetime), TEXCOORD1 = StableRandom.xyzw.
// Sprite sheets are stored in the image's own orientation with frame k at
// column k % columns of row k / columns from the top-left; uv maths happens in
// the client's texture space (v down) and is turned into Unity's (v up) last.
// The client's property lines that vary over a particle's life are baked into
// _Ramps (256 x 4): row 0 colorRandom(random), row 1 colorOverLife(t), row 2
// alphaOverLife(t), row 3 the integral of animSpeedOverLife over t.
Shader "BlitzLegacy/HalloweenHangar/Particle"
{
    Properties
    {
        _Albedo ("Sprite Sheet (gamma)", 2D) = "white" {}
        _Flowmap ("Flowmap Sheet", 2D) = "grey" {}
        _Noise ("Noise Sheet", 2D) = "grey" {}
        _Mask ("Mask Sheet (gamma)", 2D) = "white" {}
        _Ramps ("Life Ramps", 2D) = "white" {}

        _AlbedoSheet ("Sprite Sheet Columns, Rows, Frames", Vector) = (1, 1, 1, 0)
        _FlowmapSheet ("Flowmap Sheet Columns, Rows, Frames", Vector) = (1, 1, 1, 0)
        _NoiseSheet ("Noise Sheet Columns, Rows, Frames", Vector) = (1, 1, 1, 0)
        _MaskSheet ("Mask Sheet Columns, Rows, Frames", Vector) = (1, 1, 1, 0)
        _FrameParams ("Frame FPS, Loop, Random Start Frame, Frame Over Life", Vector) = (0, 1, 0, 0)
        _RampFlags ("Has colorRandom, colorOverLife, alphaOverLife, animSpeedOverLife", Vector) = (0, 0, 0, 0)
        _UvScale ("UV Scale (xy) Variation (zw)", Vector) = (1, 1, 0, 0)
        _UvScroll ("UV Scroll Speed (xy) Variation (zw)", Vector) = (0, 0, 0, 0)
        _UvFlags ("UV Scroll Enabled, UV Scale Enabled", Vector) = (0, 0, 0, 0)
        _MaskUv ("Mask UV Scale (xy) Scroll Speed (zw)", Vector) = (1, 1, 0, 0)
        _FlowParams ("Flow Speed, Speed Variation, Offset, Offset Variation", Vector) = (0, 0, 0, 0)
        _NoiseParams ("Noise Scale, Scale Variation, U Scroll Speed, V Scroll Speed", Vector) = (0, 0, 0, 0)
        _NoiseScroll ("Noise U, V Scroll Variation, Scroll Enabled", Vector) = (0, 0, 0, 0)
        _FresnelParams ("Fresnel Bias, Power, Mesh Formula, Enabled", Vector) = (0, 1, 0, 0)
        _FuseEdges ("Fuse Blend Add Edges", Vector) = (0.45, 0.55, 0, 0)
        _DepthDifferenceSlope ("Soft Particles Depth Difference Slope", Float) = 2
        _AdditiveSoft ("Soft Particles Scale Colour (additive)", Float) = 0
        _UseVertexColor ("Mesh Vertex Colour", Float) = 0
        _FogEnabled ("Vertex Fog", Float) = 0
        _FogColor ("Fog Color (rgb) Density (a)", Vector) = (0, 0, 0, 0)
        _FogLimit ("Fog Limit", Float) = 1

        [Toggle(_HH_FRAME_BLEND)] _HHFrameBlend ("Frame Blend", Float) = 0
        [Toggle(_HH_FLOWMAP)] _HHFlowmap ("Flowmap", Float) = 0
        [Toggle(_HH_FLOWMAP_ANIMATION)] _HHFlowmapAnimation ("Flowmap Animation", Float) = 0
        [Toggle(_HH_NOISE)] _HHNoise ("Noise", Float) = 0
        [Toggle(_HH_MASK)] _HHMask ("Mask", Float) = 0
        [Toggle(_HH_SOFT)] _HHSoft ("Soft Particles", Float) = 0
        [Toggle(_HH_FUSE)] _HHFuse ("Fuse Blend Add", Float) = 0

        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Z Test", Float) = 2
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

        Pass
        {
            Name "HHParticle"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            ZTest [_ZTest]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ _HH_FRAME_BLEND
            #pragma multi_compile_local _ _HH_FLOWMAP
            #pragma multi_compile_local _ _HH_FLOWMAP_ANIMATION
            #pragma multi_compile_local _ _HH_NOISE
            #pragma multi_compile_local _ _HH_MASK
            #pragma multi_compile_local _ _HH_SOFT
            #pragma multi_compile_local_fragment _ _HH_FUSE

            #include "HalloweenHangarCommon.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Albedo_ST;
                float4 _AlbedoSheet;
                float4 _FlowmapSheet;
                float4 _NoiseSheet;
                float4 _MaskSheet;
                float4 _FrameParams;
                float4 _RampFlags;
                float4 _UvScale;
                float4 _UvScroll;
                float4 _UvFlags;
                float4 _MaskUv;
                float4 _FlowParams;
                float4 _NoiseParams;
                float4 _NoiseScroll;
                float4 _FresnelParams;
                float4 _FuseEdges;
                float4 _FogColor;
                float _DepthDifferenceSlope;
                float _AdditiveSoft;
                float _UseVertexColor;
                float _FogEnabled;
                float _FogLimit;
                float _HHFrameBlend;
                float _HHFlowmap;
                float _HHFlowmapAnimation;
                float _HHNoise;
                float _HHMask;
                float _HHSoft;
                float _HHFuse;
                float _SrcBlend;
                float _DstBlend;
                float _ZTest;
                float _Cull;
            CBUFFER_END

            TEXTURE2D(_Albedo);  SAMPLER(sampler_Albedo);
            TEXTURE2D(_Flowmap); SAMPLER(sampler_Flowmap);
            TEXTURE2D(_Noise);   SAMPLER(sampler_Noise);
            TEXTURE2D(_Mask);    SAMPLER(sampler_Mask);
            TEXTURE2D(_Ramps);   SAMPLER(sampler_Ramps);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
                float4 texcoord0 : TEXCOORD0; // xy uv, z AgePercent, w InvStartLifetime
                float4 random : TEXCOORD1;    // StableRandom.xyzw
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float4 uv : TEXCOORD0;        // xy current frame, zw next frame
                float4 flowUv : TEXCOORD1;    // xy flowmap current frame, zw flowmap next frame
                float4 noiseUv : TEXCOORD2;   // xy noise uv, z noise scale
                float4 flowData : TEXCOORD3;  // xy flow phase offsets, z flow blend or distortion power, w animation time
                float4 maskUvFog : TEXCOORD4; // xy mask uv, z fog amount, w fresnel to alpha
                float4 projected : TEXCOORD5;
            };

            // Client texture space (v down from the image's top row) to Unity's.
            float2 HHUnityUv(float2 davaUv) { return float2(davaUv.x, 1.0 - davaUv.y); }
            float2 HHUnityDelta(float2 davaDelta) { return float2(davaDelta.x, -davaDelta.y); }

            // A frame's rectangle mapped onto the particle: client texture space.
            float2 HHSheetUv(float2 localUv, float frame, float4 sheet)
            {
                float f = clamp(frame, 0.0, sheet.z - 1.0);
                float row = floor((f + 0.5) / sheet.x);
                float column = f - row * sheet.x;
                return (float2(column, row) + localUv) / sheet.xy;
            }

            float4 HHRamp(float row, float t)
            {
                float2 uv = float2((saturate(t) * 255.0 + 0.5) / 256.0, (row + 0.5) * 0.25);
                return SAMPLE_TEXTURE2D_LOD(_Ramps, sampler_Ramps, uv, 0.0);
            }

            // Independent per particle random numbers beyond StableRandom.xyzw.
            float HHHashRandom(float seed, float salt)
            {
                return frac(sin(seed * 12.9898 + salt * 78.233) * 43758.5453);
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.projected = ComputeScreenPos(positions.positionCS);

                float life01 = saturate(input.texcoord0.z);
                float lifeTime = 1.0 / max(input.texcoord0.w, 1e-6);
                float age = life01 * lifeTime;
                float4 random = input.random;

                // Colour: colorRandom at birth, replaced by colorOverLife, alpha by
                // alphaOverLife (ParticleRenderObject); stored as 8 bit, alpha <= 1.
                float4 color = float4(1.0, 1.0, 1.0, 1.0);
                if (_RampFlags.x > 0.5)
                    color = HHRamp(0.0, random.x);
                if (_RampFlags.y > 0.5)
                    color = HHRamp(1.0, life01);
                if (_RampFlags.z > 0.5)
                    color.a = HHRamp(2.0, life01).r;
                color = saturate(color);
                if (_UseVertexColor > 0.5)
                    color *= input.color;
                output.color = color;

                // Sprite frame: random start frame, frameOverLifeFPS (scaled by
                // animSpeedOverLife) advancing frames, looping or holding the last.
                float frames = _AlbedoSheet.z;
                float frame = _FrameParams.z > 0.5 ? min(floor(random.y * frames), frames - 1.0) : 0.0;
                float animTime = 0.0;
                if (_FrameParams.w > 0.5)
                {
                    float progress = _RampFlags.w > 0.5 ? _FrameParams.x * lifeTime * HHRamp(3.0, life01).r : _FrameParams.x * age;
                    float steps = floor(progress);
                    animTime = progress - steps;
                    frame += steps;
                    frame = _FrameParams.y > 0.5 ? fmod(frame, frames) : min(frame, frames - 1.0);
                }
                float nextFrame = frame + 1.0;
                if (nextFrame >= frames)
                    nextFrame = _FrameParams.y > 0.5 ? 0.0 : frames - 1.0;

                float2 localUv = float2(input.texcoord0.x, 1.0 - input.texcoord0.y);
                float2 albedoLocal = localUv;
                if (_UvFlags.y > 0.5)
                    albedoLocal *= _UvScale.xy + _UvScale.zw * random.z;
                if (_UvFlags.x > 0.5)
                    albedoLocal += (_UvScroll.xy + _UvScroll.zw * random.w) * age;
                output.uv.xy = HHUnityUv(HHSheetUv(albedoLocal, frame, _AlbedoSheet));
                output.uv.zw = HHUnityUv(HHSheetUv(albedoLocal, nextFrame, _AlbedoSheet));
                output.flowData.w = animTime;

            #if defined(_HH_FLOWMAP) || defined(_HH_FLOWMAP_ANIMATION)
                output.flowUv.xy = HHUnityUv(HHSheetUv(localUv, frame, _FlowmapSheet));
                output.flowUv.zw = HHUnityUv(HHSheetUv(localUv, nextFrame, _FlowmapSheet));
                float flowOffset = _FlowParams.z + _FlowParams.w * HHHashRandom(random.x, 2.0);
                #if defined(_HH_FLOWMAP_ANIMATION)
                    output.flowData.z = flowOffset;
                #else
                    // texcoord2.z: the particle's flow time, its flow speed integrated
                    // over its age (flowSpeed + flowSpeedVariation * random).
                    float scaledTime = (_FlowParams.x + _FlowParams.y * HHHashRandom(random.x, 1.0)) * age;
                    float2 flowPhases = frac(float2(scaledTime, scaledTime + 0.5)) - float2(0.5, 0.5);
                    output.flowData.xy = flowPhases * flowOffset;
                    output.flowData.z = abs(flowPhases.x * 2.0);
                #endif
            #endif

            #if defined(_HH_NOISE)
                float2 noiseUv = HHSheetUv(localUv, frame, _NoiseSheet);
                if (_NoiseScroll.z > 0.5)
                {
                    // currNoise*Offset start at the scroll speed and advance by it.
                    float uSpeed = _NoiseParams.z + _NoiseScroll.x * HHHashRandom(random.x, 4.0);
                    float vSpeed = _NoiseParams.w + _NoiseScroll.y * HHHashRandom(random.x, 5.0);
                    noiseUv += float2(uSpeed, vSpeed) * (1.0 + age);
                }
                output.noiseUv.xy = HHUnityUv(noiseUv);
                output.noiseUv.z = _NoiseParams.x + _NoiseParams.y * HHHashRandom(random.x, 3.0);
            #endif

            #if defined(_HH_MASK)
                float2 maskLocal = localUv * _MaskUv.xy + _MaskUv.zw * age;
                output.maskUvFog.xy = HHUnityUv(HHSheetUv(maskLocal, frame, _MaskSheet));
            #endif

                output.maskUvFog.z = _FogEnabled > 0.5 ? HHFogAmount(positions.positionWS, _FogColor.a, _FogLimit) : 0.0;

                // Fresnel to alpha: billboards use |cameraDirection . quad normal|
                // (CPU), mesh particles 1 - normal . cameraDirection (vertex shader).
                float fresnel = 1.0;
                if (_FresnelParams.w > 0.5)
                {
                    float3 cameraDirection = -GetWorldToViewMatrix()[2].xyz;
                    float3 normalWS = SafeNormalize(TransformObjectToWorldNormal(input.normalOS));
                    float NdotC = dot(normalWS, cameraDirection);
                    float x = _FresnelParams.z > 0.5 ? max(1.0 - NdotC, 0.0) : abs(NdotC);
                    float power = _FresnelParams.y;
                    float term = power > 0.0 ? pow(max(x, 1e-6), power) : 1.0;
                    fresnel = _FresnelParams.x + (1.0 - _FresnelParams.x) * term;
                }
                output.maskUvFog.w = fresnel;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float animTime = input.flowData.w;
                float4 textureColor0;
            #if defined(_HH_FLOWMAP_ANIMATION)
                float2 offsetCurr = SAMPLE_TEXTURE2D(_Flowmap, sampler_Flowmap, input.flowUv.xy).xy * 2.0 - 1.0;
                offsetCurr *= animTime * input.flowData.z;
                float2 offsetNext = SAMPLE_TEXTURE2D(_Flowmap, sampler_Flowmap, input.flowUv.zw).xy * 2.0 - 1.0;
                offsetNext *= (1.0 - animTime) * input.flowData.z;
                float4 albedoSample = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, input.uv.xy - HHUnityDelta(offsetCurr));
                float4 albedoSampleNext = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, input.uv.zw + HHUnityDelta(offsetNext));
                textureColor0 = lerp(albedoSample, albedoSampleNext, animTime);
            #elif defined(_HH_FLOWMAP)
                float2 flowDir = SAMPLE_TEXTURE2D(_Flowmap, sampler_Flowmap, input.flowUv.xy).xy * 2.0 - 1.0;
                #if defined(_HH_NOISE)
                    flowDir *= SAMPLE_TEXTURE2D(_Noise, sampler_Noise, input.noiseUv.xy).r * input.noiseUv.z;
                #endif
                float4 flowSample1 = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, input.uv.xy + HHUnityDelta(flowDir * input.flowData.x));
                float4 flowSample2 = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, input.uv.xy + HHUnityDelta(flowDir * input.flowData.y));
                textureColor0 = lerp(flowSample1, flowSample2, input.flowData.z);
            #else
                float2 albedoUv = input.uv.xy;
                #if defined(_HH_NOISE)
                    float noiseSample = SAMPLE_TEXTURE2D(_Noise, sampler_Noise, input.noiseUv.xy).r * 2.0 - 1.0;
                    noiseSample *= input.noiseUv.z;
                    albedoUv += HHUnityDelta(float2(noiseSample, noiseSample));
                #endif
                textureColor0 = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, albedoUv);
            #endif

            #if defined(_HH_FRAME_BLEND)
                float4 blendFrameColor = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, input.uv.zw);
                textureColor0 = lerp(textureColor0, blendFrameColor, animTime);
            #endif

                float4 color = textureColor0 * input.color;
            #if defined(_HH_MASK)
                color *= SAMPLE_TEXTURE2D(_Mask, sampler_Mask, input.maskUvFog.xy);
            #endif
                color.a *= input.maskUvFog.w;
                color.rgb = lerp(color.rgb, _FogColor.rgb, input.maskUvFog.z);

            #if defined(_HH_SOFT)
                // Distance between the opaque scene and the fragment along the view axis.
                float2 screenUv = input.projected.xy / input.projected.w;
                float sceneDepth = LinearEyeDepth(SampleSceneDepth(screenUv), _ZBufferParams);
                float distanceDifference = max(0.0, sceneDepth - input.projected.w);
                float softScale = 1.0 - exp(-_DepthDifferenceSlope * distanceDifference * distanceDifference);
                if (_AdditiveSoft > 0.5)
                    color *= softScale;
                else
                    color.a *= softScale;
            #endif

            #if defined(_HH_FUSE)
                // blending src = one, dst = inv_src_alpha
                float fuseBlendAdd = smoothstep(_FuseEdges.x, _FuseEdges.y, color.a);
                color.rgb *= color.a;
                color.a = color.a * (1.0 - fuseBlendAdd);
            #endif

                return float4(HHOutput(color.rgb), saturate(color.a));
            }
            ENDHLSL
        }
    }
}
