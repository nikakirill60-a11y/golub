// The stencil half of the outline port of assets/tanks/base/outline2.gdshader.
//
// In the source the tank's own material carries stencil_mode write, so every
// part of the tank has stamped the buffer by the time any outline draws: the
// outline of a road wheel is already covered by the hull and never reaches the
// screen. Only the silhouette of the whole tank survives.
//
// Unity cannot hang a stencil write off URP/Lit, and a mask pass living in the
// same material as the outline is drawn per renderer, so a wheel would stamp
// and outline itself before the hull stamped anything. That is what turned the
// trace into an x-ray of every wheel and every part inside the tank.
//
// So the stamp lives here, in its own material in the opaque queue. Unity
// draws the whole opaque queue before the transparent one, which is exactly
// the ordering the source gets for free.
Shader "BlitzLegacy/Battle/Tank Outline Mask"
{
    Properties
    {
        _OutlineColor ("Outline colour", Color) = (1, 0, 0, 0)
        _OutlineWidth ("Outline width (pixels)", Float) = 4
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Unlit"
            // Last of the opaque queue: after the tank paints itself, before
            // the outline draws in the transparent queue.
            "Queue" = "Geometry+450"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "OutlineMask"
            Tags { "LightMode" = "UniversalForward" }

            ColorMask 0
            Cull Back
            ZWrite Off
            // write_depth_fail: the parts of the tank behind cover stamp too,
            // so the outline stays a silhouette rather than growing seams
            // wherever the tank is occluded.
            ZTest Always

            Stencil
            {
                Ref 1
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _OutlineColor;
                float _OutlineWidth;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

                // A hidden outline stamps nothing, so every other tank in the
                // battle pays no stencil traffic for this.
                if (_OutlineColor.a < 0.5) { output.positionCS = 0; }
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
