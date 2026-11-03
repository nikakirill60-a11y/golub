// maps/components/landscape.gdshader on the hterrain of Rudniki, drawn by
// the mobile renderer with vertex shading:
//
// vertex:   cell   = terrain_inverse_transform * world .xz + 0.5
//           UV     = cell / heightmap size
//           NORMAL = normal_basis * unpack_normal(normalmap(UV))
//           light  = ambient + Lambert sun, per vertex
// fragment: base   = colormap(UV)            (no source_color: raw)
//           mask   = pow(splatmap(UV), mask_sharpness)
//           tile   = pow(tile_texture(UV * tiletex_scale), 1 / detail_contrast)
//           detail = sum(tile.c * mask.c * tile_col_c)   (tile_col source_color)
//           albedo = clamp(mix(base, base * detail * 2, detail_strength), 0, 1)
//           albedo = pow(albedo * brightness, contrast)
//           colour = albedo * light, then fog.
Shader "BlitzLegacy/GodotMap/Landscape"
{
    Properties
    {
        _ColorMap("Colour map", 2D) = "white" {}
        _SplatMap("Splat map", 2D) = "red" {}
        _NormalMap("Normal map", 2D) = "bump" {}
        _TileTexture("Tile texture", 2D) = "white" {}
        _TileColor0("tile_col_0 (linear)", Vector) = (1, 1, 1, 1)
        _TileColor1("tile_col_1 (linear)", Vector) = (1, 1, 1, 1)
        _TileColor2("tile_col_2 (linear)", Vector) = (1, 1, 1, 1)
        _TileColor3("tile_col_3 (linear)", Vector) = (1, 1, 1, 1)
        _Terrain("map scale x, z, heightmap size, -", Vector) = (1, 1, 513, 0)
        _Detail("tiletex scale, detail strength, detail contrast, mask sharpness", Vector) = (50, 1, 1, 1)
        _Grade("brightness, contrast, -, -", Vector) = (1, 1, 0, 0)
        _Fog("Fog", Float) = 1
    }

    HLSLINCLUDE
    #include "GodotMap.hlsl"

    TEXTURE2D(_ColorMap); SAMPLER(sampler_ColorMap);
    TEXTURE2D(_SplatMap); SAMPLER(sampler_SplatMap);
    TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);
    TEXTURE2D(_TileTexture); SAMPLER(sampler_TileTexture);

    CBUFFER_START(UnityPerMaterial)
        float4 _TileColor0;
        float4 _TileColor1;
        float4 _TileColor2;
        float4 _TileColor3;
        float4 _Terrain;
        float4 _Detail;
        float4 _Grade;
        float _Fog;
    CBUFFER_END

    // Godot UV of a landscape point: the terrain sits at the origin with
    // map_scale on x/z, and Unity z is Godot -z.
    float2 LandscapeUv(float3 positionOS)
    {
        float2 cell = float2(positionOS.x / _Terrain.x, -positionOS.z / _Terrain.y) + 0.5;
        return cell / _Terrain.z;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "GodotForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vertex
            #pragma fragment Fragment

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 light : TEXCOORD2;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                float2 uv = GodotUv(LandscapeUv(input.positionOS.xyz));
                output.uv = uv;

                // unpack_normal: rgba.xzy * 2 - 1, z negated; normal_basis is
                // the inverse transpose of diag(map_scale); then Godot -> Unity.
                float3 packed = SAMPLE_TEXTURE2D_LOD(_NormalMap, sampler_NormalMap, uv, 0).rgb;
                float3 n = packed.xzy * 2.0 - 1.0;
                n.z = -n.z;
                n = float3(n.x / _Terrain.x, n.y, n.z / _Terrain.y);
                float3 normalOS = float3(n.x, n.y, -n.z);
                output.light = GodotVertexLight(TransformObjectToWorldNormal(normalOS));
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float3 base = SAMPLE_TEXTURE2D(_ColorMap, sampler_ColorMap, input.uv).rgb;
                float4 mask = SAMPLE_TEXTURE2D(_SplatMap, sampler_SplatMap, input.uv);
                mask = pow(max(mask, 0.0), _Detail.w);

                // uv * tiletex_scale in Godot space, flipped for Unity.
                float2 godotUv = GodotUv(input.uv) * _Detail.x;
                float4 tile = SAMPLE_TEXTURE2D(_TileTexture, sampler_TileTexture, GodotUv(godotUv));
                tile = pow(max(tile, 0.0), 1.0 / _Detail.z);
                float3 detail =
                    tile.r * mask.r * _TileColor0.rgb +
                    tile.g * mask.g * _TileColor1.rgb +
                    tile.b * mask.b * _TileColor2.rgb +
                    tile.a * mask.a * _TileColor3.rgb;
                float3 blended = base * detail * 2.0;
                float3 albedo = lerp(base, blended, clamp(_Detail.y, 0.0, 2.5));
                albedo = saturate(albedo);
                albedo = pow(max(albedo * _Grade.x, 0.0), _Grade.y);
                half3 colour = (half3)(albedo * input.light);
                return half4(GodotFog(colour, input.positionWS, _Fog), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment

            float4 DepthVertex(float4 positionOS : POSITION) : SV_POSITION
            {
                return TransformObjectToHClip(positionOS.xyz);
            }

            half DepthFragment() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
