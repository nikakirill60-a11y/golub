// Port of assets/tanks/base/outline2.gdshader, the pass the source hangs off
// every tank material as next_pass so a tank under the reticle is traced in
// its team colour.
//
// The source pushes each vertex along its own screen space normal and draws
// the result unshaded with the depth test off, so the trace stays readable
// through cover. What keeps that expanded shell from flooding the tank with
// flat colour is the stencil: the tank writes 1 (stencil_mode write) and the
// outline only draws where the buffer is still below that (stencil_mode read,
// compare_greater, 1).
//
// The stamp is written by TankOutlineMask.shader, which rides along in the
// opaque queue. Keeping it in a separate material matters: Unity draws the
// whole opaque queue before this one, so by the time a road wheel is traced
// the hull has already stamped itself over it and only the silhouette of the
// tank is left.
Shader "BlitzLegacy/Battle/Tank Outline"
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
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Unlit"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            Cull Front
            ZWrite Off
            ZTest Always

            // stencil_mode read, compare_greater, 1: only outside the stamp.
            Stencil
            {
                Ref 1
                Comp Greater
                Pass Keep
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
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float4 positionCS = TransformObjectToHClip(input.positionOS.xyz);

                // if(outline_color.a >= 0.5): the source skips the whole offset
                // while the outline is off, and the degenerate hull it leaves
                // behind is what keeps a hidden outline off the screen.
                if (_OutlineColor.a >= 0.5)
                {
                    float3 normalVS = TransformWorldToViewDir(
                        TransformObjectToWorldNormal(input.normalOS));
                    float3 normalCS = mul((float3x3)UNITY_MATRIX_P, normalVS);
                    float2 offset = normalize(normalCS.xy + 1e-6)
                        / _ScreenParams.xy * positionCS.w * _OutlineWidth * 2.0;

                    positionCS.xy += offset;
                }
                else
                {
                    positionCS = 0;
                }

                output.positionCS = positionCS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return half4(_OutlineColor.rgb, _OutlineColor.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
