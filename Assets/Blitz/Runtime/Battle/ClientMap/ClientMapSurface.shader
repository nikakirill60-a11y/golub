// materials-vp.sl / materials-fp.sl of the 4.8 client for the effects a map
// scene uses: Textured.Opaque, TextureLightmap.Opaque, Textured.Alphatest,
// SpeedTreeLeaf.Alphatest and Textured.Alphablend.Alphamask; and of the
// 11.20 client for Textured, TextureLightmap, Decal (a lightmap read from
// the decal slot) and MapBorder (VERTEX_COLOR).
//
//   albedo   = albedo(uv0 + texture0Shift + frac(tex0ShiftPerSecond * t))
//   ALPHA_MASK  albedo.a *= alphamask(uv1)
//   ALPHATEST   discard when albedo.a (* vertex alpha) < 0.5
//   LIGHTMAP    rgb = albedo.rgb * lightmap(uv1 * uvScale + uvOffset).rgb * 2
//   out = (rgb, ALPHABLEND ? albedo.a : 1) * vertexColour * flatColor
//   out.rgb = lerp(out.rgb, fogColour, fogAmount)
//
// An 11.20 material may author its own `fogColor`, which replaces the
// global one in that lerp.
//
// None of these effects is lit: the client bakes all light into the
// textures and lightmaps, and only the shadow volumes darken the frame.
Shader "BlitzLegacy/ClientMap/Surface"
{
    Properties
    {
        [MainTexture] _Albedo("Albedo", 2D) = "white" {}
        _Lightmap("Lightmap", 2D) = "white" {}
        _AlphaMask("Alpha mask", 2D) = "white" {}
        _LightmapST("uvScale, uvOffset", Vector) = (1, 1, 0, 0)
        _FlatColor("flatColor", Vector) = (1, 1, 1, 1)
        _TexShift("texture0Shift, tex0ShiftPerSecond", Vector) = (0, 0, 0, 0)
        _LeafColor("treeLeafColorMul", Vector) = (0.5, 0.5, 0.5, 0.5)
        _LeafOcclusion("treeLeafOcclusionMul, treeLeafOcclusionOffset", Vector) = (0.5, 0, 0, 0)
        _Cutoff("Alpha test threshold", Float) = 0.5
        _Options("x: alpha mask, y: speed tree, z: blend, w: fog", Vector) = (0, 0, 0, 1)
        _Options2("x: vertex colour, y: material fog colour", Vector) = (0, 0, 0, 0)
        _FogColor("fogColor", Vector) = (1, 1, 1, 1)
        _Cull("Cull", Float) = 2
        _SrcBlend("Source blend", Float) = 1
        _DstBlend("Destination blend", Float) = 0
        _ZWrite("Depth write", Float) = 1
    }

    HLSLINCLUDE
    #include "ClientMap.hlsl"

    TEXTURE2D(_Albedo); SAMPLER(sampler_Albedo);
    TEXTURE2D(_Lightmap); SAMPLER(sampler_Lightmap);
    TEXTURE2D(_AlphaMask); SAMPLER(sampler_AlphaMask);

    CBUFFER_START(UnityPerMaterial)
        float4 _LightmapST;
        float4 _FlatColor;
        float4 _TexShift;
        float4 _LeafColor;
        float4 _LeafOcclusion;
        float4 _Options;
        float4 _Options2;
        float4 _FogColor;
        float _Cutoff;
    CBUFFER_END

    struct Attributes
    {
        float4 positionOS : POSITION;
        float2 uv0 : TEXCOORD0;
        float2 uv1 : TEXCOORD1;
        // SpeedTree leaf: the card corner around its pivot, already scaled
        // by the node's worldScale, spanned across the view plane.
        float2 billboard : TEXCOORD2;
        half4 color : COLOR;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv0 : TEXCOORD0;
        float2 uv1 : TEXCOORD1;
        float3 positionWS : TEXCOORD2;
        half4 color : COLOR;
    };

    // SPEED_TREE_OBJECT: the vertex stands on its pivot and the card offset
    // is added in eye space, so a leaf always faces the camera.
    float3 ClientPositionWS(Attributes input, out float4 positionCS)
    {
        float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
        if (_Options.y > 0.5)
        {
            float3 view = TransformWorldToView(positionWS);
            view.xy += input.billboard;
            positionCS = TransformWViewToHClip(view);
            positionWS = mul(UNITY_MATRIX_I_V, float4(view, 1.0)).xyz;
        }
        else
        {
            positionCS = TransformWorldToHClip(positionWS);
        }
        return positionWS;
    }

    float2 AlbedoUv(float2 uv0)
    {
        return DavaUv(uv0 + _TexShift.xy + frac(_TexShift.zw * _Time.y));
    }

    half AlbedoAlpha(Varyings input, out half4 albedo)
    {
        albedo = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, AlbedoUv(input.uv0));
        if (_Options.x > 0.5)
            albedo.a *= SAMPLE_TEXTURE2D(_AlphaMask, sampler_AlphaMask, DavaUv(input.uv1)).a;
        return albedo.a * input.color.a;
    }

    Varyings Vertex(Attributes input)
    {
        Varyings output = (Varyings)0;
        output.positionWS = ClientPositionWS(input, output.positionCS);
        output.uv0 = input.uv0;
        output.uv1 = input.uv1;
        // Only SpeedTreeLeaf (and in 11.20 MapBorder) defines VERTEX_COLOR;
        // the bushes carry a colour stream too, but their effect never reads it.
        half4 colour = 1.0;
        if (_Options.y > 0.5)
        {
            // The legacy SpeedTree lighting of materials-vp.sl.
            colour.rgb = input.color.rgb * (half3)_LeafColor.rgb * (half)_LeafOcclusion.x
                + (half)_LeafOcclusion.y;
            colour.a = input.color.a;
        }
        else if (_Options2.x > 0.5)
        {
            colour = input.color;
        }
        output.color = colour;
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
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_local _ _CLIENT_LIGHTMAP
            #pragma multi_compile_local _ _CLIENT_ALPHATEST
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            half4 Fragment(Varyings input) : SV_Target
            {
                half4 albedo;
                half alpha = AlbedoAlpha(input, albedo);
                #if defined(_CLIENT_ALPHATEST)
                    clip(alpha - _Cutoff);
                #endif
                half3 colour = albedo.rgb;
                #if defined(_CLIENT_LIGHTMAP)
                    float2 lightmapUv = input.uv1 * _LightmapST.xy + _LightmapST.zw;
                    colour *= SAMPLE_TEXTURE2D(_Lightmap, sampler_Lightmap, DavaUv(lightmapUv)).rgb * 2.0;
                #endif
                half4 result = half4(colour, _Options.z > 0.5 ? albedo.a : 1.0);
                result *= input.color;
                result *= (half4)_FlatColor;
                if (_Options.w > 0.5)
                {
                    half4 fog = ClientFog(input.positionWS);
                    if (_Options2.y > 0.5)
                        fog.rgb = (half3)_FogColor.rgb;
                    result.rgb = lerp(result.rgb, fog.rgb, fog.a);
                }
                // Translucent effects draw after ShadowRect has darkened
                // the frame, so only the opaque ones take the shadow.
                if (_Options.z < 0.5)
                    result.rgb = ClientShadowed(result.rgb, input.positionWS);
                return half4(ClientOutput(result.rgb), saturate(result.a));
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
            #pragma target 3.0
            #pragma vertex Vertex
            #pragma fragment DepthFragment
            #pragma multi_compile_local _ _CLIENT_ALPHATEST

            half4 DepthFragment(Varyings input) : SV_Target
            {
                #if defined(_CLIENT_ALPHATEST)
                    half4 albedo;
                    clip(AlbedoAlpha(input, albedo) - _Cutoff);
                #endif
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }
}
