// addons/gputrail/shaders/trail_draw_pass.gdshader as trail_preset.tscn sets
// it up (flags 0: no billboard, dewiggle, overlap clipping or red-as-alpha):
//
//   render_mode unshaded, world_vertex_coords, cull_disabled
//   ALBEDO = textureLod(tex, UV, 0).rgb * textureLod(color_ramp, UV, 0).rgb
//   ALPHA  = textureLod(tex, UV, 0).a   * textureLod(color_ramp, UV, 0).a
//
// tex repeats, color_ramp clamps, both are source_color and read from mip 0.
// The default mask is white at full strength, so it drops out. Fog applies
// (the shader does not disable it) and the blend is mix. BattleGpuTrail builds
// the world-space ribbon on the CPU and passes Godot's UV.
Shader "BlitzLegacy/Battle/Gpu Trail"
{
    Properties
    {
        [MainTexture] _MainTex("Trail texture", 2D) = "white" {}
        _Ramp("Colour ramp", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "GpuTrail"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "../GodotMap/GodotMap.hlsl"

            TEXTURE2D(_MainTex);
            TEXTURE2D(_Ramp);
            SAMPLER(sampler_linear_repeat);
            SAMPLER(sampler_linear_clamp);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Ramp_ST;
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
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv0 = input.uv0;
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float4 tex = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_linear_repeat, GodotUv(input.uv0), 0);
                // The ramp is a one-row texture built here, so it needs no flip.
                float4 ramp = SAMPLE_TEXTURE2D_LOD(_Ramp, sampler_linear_clamp, input.uv0, 0);
                float4 colour = tex * ramp;
                return half4(GodotFog((half3)colour.rgb, input.positionWS, 1.0), (half)colour.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
