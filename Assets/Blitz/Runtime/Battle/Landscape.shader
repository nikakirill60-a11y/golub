Shader "BlitzLegacy/Map/Landscape"
{
    Properties
    {
        [NoScaleOffset] _ColorMap("Color Map", 2D) = "white" {}
        [NoScaleOffset] _TileMask("Tile Mask", 2D) = "white" {}
        [NoScaleOffset] _TileTexture("Packed Tile Detail", 2D) = "white" {}
        _TileTexScale("Tile Scale", Float) = 50.0
        _TileColor0("Tile Color 0", Color) = (1.0, 1.0, 1.0, 1.0)
        _TileColor1("Tile Color 1", Color) = (1.0, 1.0, 1.0, 1.0)
        _TileColor2("Tile Color 2", Color) = (1.0, 1.0, 1.0, 1.0)
        _TileColor3("Tile Color 3", Color) = (1.0, 1.0, 1.0, 1.0)
        _DetailStrength("Detail Strength", Range(0.0, 2.5)) = 0.68
        _DetailContrast("Detail Contrast", Range(0.01, 4.0)) = 0.414
        _Brightness("Brightness", Range(0.1, 3.0)) = 0.775
        _Contrast("Contrast", Range(0.1, 3.0)) = 2.246
        _MaskSharpness("Mask Sharpness", Range(0.01, 8.0)) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex TerrainVertex
            #pragma fragment TerrainFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _ADDITIONAL_LIGHTS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "BlitzFog.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_ColorMap);
            SAMPLER(sampler_ColorMap);
            TEXTURE2D(_TileMask);
            SAMPLER(sampler_TileMask);
            TEXTURE2D(_TileTexture);
            SAMPLER(sampler_TileTexture);

            CBUFFER_START(UnityPerMaterial)
                float _TileTexScale;
                half4 _TileColor0;
                half4 _TileColor1;
                half4 _TileColor2;
                half4 _TileColor3;
                half _DetailStrength;
                half _DetailContrast;
                half _Brightness;
                half _Contrast;
                half _MaskSharpness;
            CBUFFER_END

            Varyings TerrainVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);
                output.uv = input.uv;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = NormalizeNormalPerVertex(normalInputs.normalWS);
                output.positionCS = positionInputs.positionCS;
                return output;
            }

            half3 SourceAlbedo(float2 uv)
            {
                // Line for line port of fragment() in
                // maps/components/landscape.gdshader. These three maps are
                // imported as linear data because the Godot samplers do not
                // carry source_color; the final pow is the authored conversion.
                //
                // The source colour map carries broad authored luminance
                // patches; they are terrain shading, not a port defect, and
                // Godot renders them unchanged. An extra Unity-only macro
                // normalisation used to divide that low frequency out, which
                // pushed every dark patch towards one target luma and turned
                // authored shade into the flat grey blotches seen on device.
                half3 baseColor = SAMPLE_TEXTURE2D(_ColorMap, sampler_ColorMap, uv).rgb;

                half4 mask = SAMPLE_TEXTURE2D(_TileMask, sampler_TileMask, uv);
                mask = pow(saturate(mask), max(_MaskSharpness, 0.01h));

                half4 tile = SAMPLE_TEXTURE2D(
                    _TileTexture, sampler_TileTexture, uv * _TileTexScale);
                tile = pow(saturate(tile), rcp(max(_DetailContrast, 0.01h)));

                half3 detailMix =
                    tile.r * mask.r * _TileColor0.rgb +
                    tile.g * mask.g * _TileColor1.rgb +
                    tile.b * mask.b * _TileColor2.rgb +
                    tile.a * mask.a * _TileColor3.rgb;

                half3 blended = baseColor * detailMix * 2.0h;
                half3 detailed = lerp(
                    baseColor, blended, clamp(_DetailStrength, 0.0h, 2.5h));
                detailed = saturate(detailed);
                return pow(saturate(detailed * _Brightness), max(_Contrast, 0.01h));
            }

            half4 TerrainFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = NormalizeNormalPerPixel(input.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                // Per pixel: a cascade picked per vertex blends two atlas tiles
                // across every triangle that straddles a cascade border.
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.vertexLighting = half3(0.0h, 0.0h, 0.0h);
                inputData.bakedGI = SampleSH(inputData.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = SourceAlbedo(input.uv);
                surfaceData.specular = half3(0.0h, 0.0h, 0.0h);
                surfaceData.smoothness = 0.0h;
                surfaceData.normalTS = half3(0.0h, 0.0h, 1.0h);
                surfaceData.occlusion = 1.0h;
                surfaceData.alpha = 1.0h;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = BlitzMixFog(color.rgb, input.positionWS);
                color.a = 1.0h;
                return color;
            }
            ENDHLSL
        }
    }
}
