// TileMaskSpecular, the HIGH variant of TileMaskAllQualities
// (Landscape/tilemask-vp.sl / tilemask-fp.sl, TILEMASK + LANDSCAPE_SPECULAR):
//
//   tile  = tileTexture0(texCoord * textureTiling)
//   mask  = tileMask(texCoord), colour = colorTexture(texCoord)
//   rgb   = (tile.r * mask.r * tileColor0 + tile.g * mask.g * tileColor1
//          + tile.b * mask.b * tileColor2 + tile.a * mask.a * tileColor3)
//          * colour.rgb * 2
//   gloss = inGlossiness * colour.a, power = 5000 ^ gloss
//   rgb  += (gloss + 2) / 8 * NdotH ^ power * NdotL * inSpecularity
//          * FresnelShlick(NdotV, metalFresnelReflectance) * lightColor0
//   then fog, then ShadowRect.
//
// The 11.20 client (tilemask-fp.sl without PBR) keeps its baked light in
// the colour map's alpha (LANDSCAPE_SEPARATE_LIGHTMAP_CHANNEL) and draws no
// highlight at all:
//
//   rgb   = (tile.r * mask.r * tileColor0 + ...) * colour.rgb * colour.a * 2
//
// _LandscapeOptions picks that variant per map.
//
// BattleTerrain's v runs the way the client's texCoord does for a map kept in
// the client's orientation: v = 1 - (y - minY) / extent.
Shader "BlitzLegacy/ClientMap/Landscape"
{
    Properties
    {
        _ColorMap("colorTexture", 2D) = "white" {}
        _TileMask("tileMask", 2D) = "white" {}
        _TileTexture("tileTexture0", 2D) = "white" {}
        _Tiling("textureTiling", Vector) = (50, 50, 0, 0)
        _TileColor0("tileColor0", Vector) = (1, 1, 1, 1)
        _TileColor1("tileColor1", Vector) = (1, 1, 1, 1)
        _TileColor2("tileColor2", Vector) = (1, 1, 1, 1)
        _TileColor3("tileColor3", Vector) = (1, 1, 1, 1)
        _Specular("inGlossiness, inSpecularity, metalFresnelReflectance", Vector) = (0.5, 1, 0.5, 0)
        _LightColor("lightColor0", Vector) = (1, 1, 1, 1)
        _LandscapeOptions("x: separate lightmap channel, y: specular", Vector) = (0, 1, 0, 0)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Name "ClientLandscape"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #include "ClientMap.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"

            TEXTURE2D(_ColorMap); SAMPLER(sampler_ColorMap);
            TEXTURE2D(_TileMask); SAMPLER(sampler_TileMask);
            TEXTURE2D(_TileTexture); SAMPLER(sampler_TileTexture);

            CBUFFER_START(UnityPerMaterial)
                float4 _Tiling;
                float4 _TileColor0;
                float4 _TileColor1;
                float4 _TileColor2;
                float4 _TileColor3;
                float4 _Specular;
                float4 _LightColor;
                float4 _LandscapeOptions;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                return output;
            }

            float3 FresnelShlick(float cosine, float3 reflectance)
            {
                return (1.0 - reflectance) * pow(1.0 - cosine, 5.0) + reflectance;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float2 uv = DavaUv(input.uv);
                float2 tiled = DavaUv(input.uv * _Tiling.xy);
                half4 tile = SAMPLE_TEXTURE2D(_TileTexture, sampler_TileTexture, tiled);
                half4 mask = SAMPLE_TEXTURE2D(_TileMask, sampler_TileMask, uv);
                half4 colour = SAMPLE_TEXTURE2D(_ColorMap, sampler_ColorMap, uv);
                half3 shade = _LandscapeOptions.x > 0.5 ? colour.rgb * colour.a : colour.rgb;
                half3 rgb = (tile.r * mask.r * (half3)_TileColor0.rgb +
                             tile.g * mask.g * (half3)_TileColor1.rgb +
                             tile.b * mask.b * (half3)_TileColor2.rgb +
                             tile.a * mask.a * (half3)_TileColor3.rgb) * shade * 2.0;

                // The products are rotation invariant, so the client's
                // vectors can stay in Unity's world.
                float3 normal = normalize(input.normalWS);
                float3 toLight = normalize(_MainLightPosition.xyz);
                float3 toCamera = normalize(GetCameraPositionWS() - input.positionWS);
                float3 halfVector = normalize(toCamera + toLight);
                float NdotL = max(dot(normal, toLight), 0.0);
                float NdotH = max(dot(normal, halfVector), 0.0);
                float NdotV = max(dot(normal, toCamera), 0.0);
                float3 fresnel = FresnelShlick(NdotV, _Specular.zzz);
                float glossiness = _Specular.x * colour.a;
                float power = pow(5000.0, glossiness);
                float specular = (glossiness + 2.0) / 8.0 * pow(NdotH, power) * NdotL * _Specular.y;
                if (_LandscapeOptions.y > 0.5)
                    rgb += (half3)(specular * fresnel * _LightColor.rgb);

                half4 fog = ClientFog(input.positionWS);
                rgb = lerp(rgb, fog.rgb, fog.a);
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
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 DepthVertex(float4 positionOS : POSITION) : SV_POSITION
            {
                return TransformObjectToHClip(positionOS.xyz);
            }

            half4 DepthFragment(float4 positionCS : SV_POSITION) : SV_Target
            {
                return positionCS.z;
            }
            ENDHLSL
        }
    }
}
