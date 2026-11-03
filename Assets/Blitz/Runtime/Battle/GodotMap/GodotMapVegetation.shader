// maps/vegetation/_vegetation_shader.gdshader: the unshaded, double-sided
// billboard quads of every MultiMesh bush and tree crown.
//
// vertex:   VERTEX += sin((node.y + VERTEX.y * 10) * sway_scale
//                         + TIME / sway_duration * 2pi) * sway_amount * dir
//           (only within 150 m of the node), then the camera basis around
//           the instance origin with the instance scale kept.
// fragment: colour = albedo * texture_albedo           (both source_color)
//           close  = max(.5, (|camera - (node + origin_offset)| - 7) / 8)
//           ALPHA  = a * (a * close < bayer4x4 || a * close < .01 ? 0 : 1)
//           alpha scissor at alpha_scissor_threshold, then fog.
//
// The mesh keeps the instance origin in POSITION, the keep-scale lengths in
// NORMAL, the MultiMeshInstance3D origin (NODE_POSITION_WORLD) in TANGENT
// and the quad corner in UV2; all three follow a falling tree's trunk.
Shader "BlitzLegacy/GodotMap/Vegetation"
{
    Properties
    {
        [MainTexture] _Albedo("Albedo", 2D) = "white" {}
        _Color("Albedo colour (linear)", Vector) = (1, 1, 1, 1)
        _UvTransform("uv1 scale, offset", Vector) = (1, 1, 0, 0)
        _OriginOffset("origin_offset", Vector) = (0, 2, 0, 0)
        _Sway("sway amount, scale, duration", Vector) = (0.05, 0.5, 3, 0)
        _SwayDirection("sway direction", Vector) = (1, 0, 0, 0)
        _Cutoff("alpha_scissor_threshold", Float) = 0.5
        _Fog("Fog", Float) = 1
        _Cull("Cull", Float) = 0
    }

    HLSLINCLUDE
    #include "GodotMap.hlsl"

    TEXTURE2D(_Albedo); SAMPLER(sampler_Albedo);

    CBUFFER_START(UnityPerMaterial)
        float4 _Albedo_ST;
        float4 _Color;
        float4 _UvTransform;
        float4 _OriginOffset;
        float4 _Sway;
        float4 _SwayDirection;
        float _Cutoff;
        float _Fog;
    CBUFFER_END

    struct Attributes
    {
        float4 positionOS : POSITION;
        float3 normalOS : NORMAL;
        float4 tangentOS : TANGENT;
        float2 uv0 : TEXCOORD0;
        float2 corner : TEXCOORD2;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv0 : TEXCOORD0;
        float3 positionWS : TEXCOORD1;
        float3 nodeWS : TEXCOORD2;
    };

    Varyings VegetationVertex(Attributes input)
    {
        Varyings output;
        float3 pivotWS = TransformObjectToWorld(input.positionOS.xyz);
        float3 nodeWS = TransformObjectToWorld(input.tangentOS.xyz);
        float3 local = float3(input.corner, 0.0);
        float distance = length(GetCameraPositionWS() - nodeWS);
        if (distance < 150.0)
        {
            float swayTime = (_Time.y / _Sway.z) * 6.28318;
            float sway = sin((nodeWS.y + local.y * 10.0) * _Sway.y + swayTime) * _Sway.x;
            local += sway * _SwayDirection.xyz;
        }
        output.positionWS = GodotBillboard(pivotWS, local, input.normalOS);
        output.positionCS = TransformWorldToHClip(output.positionWS);
        output.uv0 = GodotUv(input.uv0 * _UvTransform.xy + _UvTransform.zw);
        output.nodeWS = nodeWS;
        return output;
    }

    float GodotBayer(float4 positionCS)
    {
        // dither_vals of the source, indexed [x][y] as its getValue does.
        const float4x4 bayer = float4x4(
            0.0625, 0.8125, 0.25, 1.0,
            0.5625, 0.3125, 0.75, 0.5,
            0.1875, 0.9375, 0.125, 0.875,
            0.6875, 0.4375, 0.625, 0.375);
        uint2 p = uint2(positionCS.xy) & 3u;
        return bayer[p.y][p.x];
    }

    float4 VegetationColour(Varyings input)
    {
        float4 tex = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, input.uv0);
        float4 colour = _Color * tex;
        float distance = length(GetCameraPositionWS() - (input.nodeWS + _OriginOffset.xyz));
        float close = max(0.5, (distance - 7.0) / 8.0);
        float faded = colour.a * close;
        float limit = GodotBayer(input.positionCS);
        float keep = (faded < limit || faded < 0.01) ? 0.0 : 1.0;
        colour.a = _Color.a * tex.a * keep;
        clip(colour.a - _Cutoff);
        return colour;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "RenderPipeline" = "UniversalPipeline" "Queue" = "AlphaTest" }

        Pass
        {
            Name "GodotForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex VegetationVertex
            #pragma fragment Fragment

            half4 Fragment(Varyings input) : SV_Target
            {
                float4 colour = VegetationColour(input);
                return half4(GodotFog((half3)colour.rgb, input.positionWS, _Fog), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull [_Cull]
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex VegetationVertex
            #pragma fragment DepthFragment

            half DepthFragment(Varyings input) : SV_Target
            {
                VegetationColour(input);
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
