Shader "BlitzLegacy/Post/Adjustments"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "SourceAdjustments"

            Cull Off
            ZTest Always
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Fragment

            // A capture tool takes a single frame, and a shader still
            // compiling renders as magenta in it.
            #pragma editor_sync_compilation

            // Blit.hlsl is not self-contained: it uses TEXTURE2D_X, which
            // lives in core's TextureXR.hlsl and reaches a shader through
            // URP's Core.hlsl. Omitting Core.hlsl compiles in the editor and
            // then fails the player build on gles3 and vulkan, so these two
            // includes stay in the order URP's own CoreBlit.shader uses.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Vert, _BlitTexture and _BlitScaleBias all come from here, and
            // AddBlitPass draws exactly the procedural triangle this Vert
            // expects.
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // brightness, contrast, saturation, enabled. SourceAdjustments.cs
            // pushes it from the manifest and only enqueues the pass when the
            // source turns the adjustment on.
            float4 _BlitzAdjust;

            // linear_to_srgb / srgb_to_linear of tonemap_mobile.glsl.
            float3 GodotLinearToSrgb(float3 c)
            {
                float3 curve = 1.055 * pow(max(c, 0.0), 1.0 / 2.4) - 0.055;
                return lerp(curve, 12.92 * c, step(c, 0.0031308));
            }

            float3 GodotSrgbToLinear(float3 c)
            {
                float3 curve = pow((c + 0.055) * (1.0 / 1.055), 2.4);
                return lerp(curve, c * (1.0 / 12.92), step(c, 0.04045));
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 source = SAMPLE_TEXTURE2D_X_LOD(
                    _BlitTexture, sampler_PointClamp, input.texcoord, 0.0);
                float3 colour = source.rgb;

                // The use_bcs block of Godot 4.7's tonemap_mobile.glsl, in
                // its order and in its encodings: the colour is sRGB-encoded
                // first, then brightness (mix from black), contrast (pivot
                // 0.5) and saturation (towards the unweighted channel mean)
                // all work on the encoded value. Measured against Godot
                // captures of Rudniki: multiplying the linear colour instead
                // left every pixel ~6% darker. The old port did all three on
                // linear values, which crushed the shadows and bleached the sky.
                colour = GodotLinearToSrgb(colour);
                colour *= _BlitzAdjust.x;
                colour = lerp(float3(0.5, 0.5, 0.5), colour, _BlitzAdjust.y);
                float mean = dot(float3(1.0, 1.0, 1.0), colour) * (1.0 / 3.0);
                colour = lerp(float3(mean, mean, mean), colour, _BlitzAdjust.z);

                // Godot writes this straight to the 8-bit swapchain; Unity's
                // target is linear, so decode it again (clamped at black as
                // the 8-bit store would).
                colour = GodotSrgbToLinear(max(colour, 0.0));
                return half4(colour, source.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
