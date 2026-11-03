// The five sky planes of Rudniki: PlaneMesh nodes with an unshaded
// StandardMaterial3D (albedo_color * albedo_texture, both source_color) and
// fog left on. They hang 5 km out; the depth is pinned to the far plane so a
// shorter draw distance (3 km on phones) does not clip them, while the fog
// still reads the real distance, as Godot's does.
Shader "BlitzLegacy/GodotMap/Sky"
{
    Properties
    {
        [MainTexture] _Albedo("Albedo", 2D) = "white" {}
        _Color("Albedo colour (linear)", Vector) = (1, 1, 1, 1)
        _UvTransform("uv1 scale, offset", Vector) = (1, 1, 0, 0)
        _Fog("Fog", Float) = 1
        _Cull("Cull", Float) = 2
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+500" }

        Pass
        {
            Name "GodotForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "GodotMap.hlsl"

            TEXTURE2D(_Albedo); SAMPLER(sampler_Albedo);

            CBUFFER_START(UnityPerMaterial)
                float4 _Albedo_ST;
                float4 _Color;
                float4 _UvTransform;
                float _Fog;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0 : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv0 : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float4 clip = TransformWorldToHClip(output.positionWS);
                #if UNITY_REVERSED_Z
                    clip.z = clip.w * 1e-6;
                #else
                    clip.z = clip.w * (1.0 - 1e-6);
                #endif
                output.positionCS = clip;
                output.uv0 = GodotUv(input.uv0 * _UvTransform.xy + _UvTransform.zw);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float3 colour = _Color.rgb * SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, input.uv0).rgb;
                return half4(GodotFog((half3)colour, input.positionWS, _Fog), 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
