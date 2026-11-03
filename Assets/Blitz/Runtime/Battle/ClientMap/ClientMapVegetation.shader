// grass-vp.sl / grass-fp.sl of the 11.20 client for a VegetationRenderObject
// (ClientMapVegetation draws one pattern submesh per node):
//
//   pivot     = the cluster's pivot in the pattern + the node's corner
//   uvColor   = 0.5 + pivot.xy / worldSize.xy
//   density   = vegetationmap(uvColor).a
//   pos.z, pivot.z += height of the landscape under the pivot
//   switchLod = |pivot.resolution - switchLodIndex| < 0.1
//   scale     = density * lerp(lodMorph, 1, switchLod) * distanceScale
//   pos       = lerp(pivot, pos, scale), dropped when scale <= 0.001
//   out       = albedo(uv) * vegetationmap(uvColor).rgb * baseColorMult
//   out.rgb   = lerp(out.rgb, fogColour, fogAmount)
//
// The landscape heights come as the very grid BattleTerrain builds the
// ground from, read with Load and blended by hand.
Shader "BlitzLegacy/ClientMap/Vegetation"
{
    Properties
    {
        [MainTexture] _Albedo("Albedo", 2D) = "white" {}
        _ColorMap("vegetationmap", 2D) = "white" {}
        [NoScaleOffset] _Heights("Landscape heights", 2D) = "black" {}
        _HeightGrid("Height grid corner xz, 1 / step xz", Vector) = (0, 0, 1, 1)
        _HeightSide("Height grid side", Float) = 2
        _World("worldSize xy, baseColorMult, fog", Vector) = (600, 600, 1, 1)
        _Cull("Cull", Float) = 2
    }

    HLSLINCLUDE
    #include "ClientMap.hlsl"

    TEXTURE2D(_Albedo); SAMPLER(sampler_Albedo);
    TEXTURE2D(_ColorMap); SAMPLER(sampler_ColorMap);
    TEXTURE2D_FLOAT(_Heights);

    // Per draw: distanceScale, lodMorph, switchLodIndex.
    float4 _Tile;

    CBUFFER_START(UnityPerMaterial)
        float4 _HeightGrid;
        float4 _World;
        float _HeightSide;
    CBUFFER_END

    struct Attributes
    {
        float4 positionOS : POSITION;
        float2 uv : TEXCOORD0;
        // The cluster's pivot in the pattern and the coarsest resolution
        // the cluster is part of.
        float4 pivot : TEXCOORD1;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
        float3 positionWS : TEXCOORD1;
        half3 colour : TEXCOORD2;
    };

    float TerrainHeight(float2 xz)
    {
        float2 grid = clamp((xz - _HeightGrid.xy) * _HeightGrid.zw, 0.0, _HeightSide - 1.0001);
        int2 cell = (int2)grid;
        float2 t = grid - (float2)cell;
        float h00 = LOAD_TEXTURE2D_LOD(_Heights, cell, 0).r;
        float h10 = LOAD_TEXTURE2D_LOD(_Heights, cell + int2(1, 0), 0).r;
        float h01 = LOAD_TEXTURE2D_LOD(_Heights, cell + int2(0, 1), 0).r;
        float h11 = LOAD_TEXTURE2D_LOD(_Heights, cell + int2(1, 1), 0).r;
        return lerp(lerp(h00, h10, t.x), lerp(h01, h11, t.x), t.y);
    }

    Varyings Vertex(Attributes input)
    {
        Varyings output = (Varyings)0;
        float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
        float3 pivotWS = TransformObjectToWorld(input.pivot.xyz);
        // DAVA's (0.5 + x / W, 0.5 - y / W) with v counted from the top row.
        float2 uvColor = 0.5 + pivotWS.xz / _World.xy;
        half4 colour = SAMPLE_TEXTURE2D_LOD(_ColorMap, sampler_ColorMap, uvColor, 0);
        float height = TerrainHeight(pivotWS.xz);
        positionWS.y += height;
        pivotWS.y += height;
        float switchLod = step(abs(input.pivot.w - _Tile.z), 0.1);
        float scale = colour.a * lerp(_Tile.y, 1.0, switchLod) * _Tile.x;
        // A dropped cluster collapses onto its pivot and rasterises nothing.
        scale = scale <= 0.001 ? 0.0 : scale;
        positionWS = lerp(pivotWS, positionWS, scale);
        output.positionWS = positionWS;
        output.positionCS = TransformWorldToHClip(positionWS);
        output.uv = input.uv;
        output.colour = colour.rgb;
        return output;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Name "ClientForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            half4 Fragment(Varyings input) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, DavaUv(input.uv));
                half3 rgb = albedo.rgb * input.colour * (half)_World.z;
                if (_World.w > 0.5)
                {
                    half4 fog = ClientFog(input.positionWS);
                    rgb = lerp(rgb, fog.rgb, fog.a);
                }
                return half4(ClientOutput(ClientShadowed(rgb, input.positionWS)), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vertex
            #pragma fragment DepthFragment

            half4 DepthFragment(Varyings input) : SV_Target
            {
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }
}
