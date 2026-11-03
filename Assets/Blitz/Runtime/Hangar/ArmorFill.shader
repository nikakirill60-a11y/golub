Shader "BlitzLegacy/Hangar/Armor Fill"
{
    Properties
    {
        _Color ("Armor colour", Color) = (1, 0, 0, 0.62)
        [HideInInspector] _ZTest ("Depth test", Float) = 8
        [HideInInspector] _Extrude ("Extrusion from the node origin", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Unlit"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ArmorFill"
            Tags { "LightMode" = "UniversalForward" }
            // The armour map replaces the tank rather than floating over it,
            // exactly as the armour sites do. That is the only way the plates
            // sort against each other honestly: an overlay drawn on top has to
            // choose between losing to the hull's depth, which leaves nothing
            // but slivers, and ignoring depth, which let the 5 mm roof paint
            // itself over the 240 mm turret front.
            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                // A flat fill hides every edge of a low-poly shell. One fixed
                // key light keeps the plate boundaries readable without
                // changing the colour the thickness maps to.
                float3 key = normalize(float3(0.3, 0.9, -0.45));
                float shade = 0.72 + 0.28 * saturate(dot(normalize(input.normalWS), key));
                return half4(_Color.rgb * shade, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
