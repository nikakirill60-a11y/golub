// The fragment stage of the StandardMaterial3D every combat particle draws
// with in the Godot source (unshaded, cull disabled, no depth write):
//
//   albedo = albedo_color * texture(albedo_texture, UV)   (source_color)
//   albedo *= COLOR                         when vertex_color_use_as_albedo
//   ALPHA  = albedo.a                       when transparency is not disabled
//   fog    = depth fog (BlitzFog.hlsl)       unless disable_fog
//
// with blend mix or add. GodotParticleEmitter has already done the vertex
// stage on the CPU (billboards, particle frames, transform alignment), so the
// mesh arrives in world space with Godot's top-down UV and the linear
// particle COLOR in the vertex colour.
Shader "BlitzLegacy/Battle/Godot Particle"
{
    Properties
    {
        [MainTexture] _MainTex("Albedo texture", 2D) = "white" {}
        _Albedo("Albedo colour (linear)", Vector) = (1, 1, 1, 1)
        _UseAlpha("Transparency", Float) = 1
        _VertexColor("Vertex colour as albedo", Float) = 0
        _VertexSrgb("Vertex colour is sRGB", Float) = 0
        _Fog("Fog", Float) = 1
        _SrcBlend("Source blend", Float) = 5
        _DstBlend("Destination blend", Float) = 10
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "GodotParticle"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend [_SrcBlend] [_DstBlend]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vertex
            #pragma fragment Fragment
            // TEXTURE_FILTER_LINEAR samples mip 0, LINEAR_WITH_MIPMAPS is trilinear.
            #pragma multi_compile_local _ _GODOT_NO_MIPMAPS
            #pragma multi_compile_local _ _GODOT_CLAMP
            #include "../GodotMap/GodotMap.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_linear_repeat);
            SAMPLER(sampler_linear_clamp);
            SAMPLER(sampler_trilinear_repeat);
            SAMPLER(sampler_trilinear_clamp);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Albedo;
                float _UseAlpha;
                float _VertexColor;
                float _VertexSrgb;
                float _Fog;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0 : TEXCOORD0;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv0 : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float4 color : COLOR;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv0 = GodotUv(input.uv0);
                float4 colour = input.color;
                if (_VertexSrgb > 0.5)
                {
                    colour.rgb = lerp(pow((colour.rgb + 0.055) * (1.0 / 1.055), 2.4), colour.rgb * (1.0 / 12.92), step(colour.rgb, 0.04045));
                }
                output.color = colour;
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                #if defined(_GODOT_NO_MIPMAPS)
                    #if defined(_GODOT_CLAMP)
                        float4 tex = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_linear_clamp, input.uv0, 0);
                    #else
                        float4 tex = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_linear_repeat, input.uv0, 0);
                    #endif
                #else
                    #if defined(_GODOT_CLAMP)
                        float4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_trilinear_clamp, input.uv0);
                    #else
                        float4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_trilinear_repeat, input.uv0);
                    #endif
                #endif
                float4 albedo = _Albedo * tex;
                if (_VertexColor > 0.5)
                {
                    albedo *= input.color;
                }
                float alpha = _UseAlpha > 0.5 ? albedo.a : 1.0;
                return half4(GodotFog((half3)albedo.rgb, input.positionWS, _Fog), (half)alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
