// lit-materials-vp.sl / lit-materials-fp.sl of the 11.20 client for the lit
// effects of a map scene (BlinnPhongAllQualities: PIXEL_LIT,
// NORMALIZED_BLINN_PHONG, metal fresnel):
//
//   albedo   = albedo(uv0 + texture0Shift + frac(tex0ShiftPerSecond * t))
//   rgb      = (lightAmbientColor0 + lightColor0 * NdotL / pi) * albedo.rgb
//   gloss    = inGlossiness * albedo.a, power = 5000 ^ gloss
//   rgb     += lightColor0 * (power + 2) / 8 * NdotH ^ power
//              * NdotL * inSpecularity
//              * FresnelShlick(NdotV, metalFresnelReflectance)
//   out = (rgb, ALPHABLEND ? albedo.a : 1) * vertexColour * flatColor
//   out.rgb = lerp(out.rgb, fogColour, fogAmount)
//
// lightColor0 and lightAmbientColor0 are the scene light's raw colours
// (_ClientLightColor, _ClientAmbientColor), its direction Unity's main
// light. A lit material without a normal map gets a pink placeholder in the
// client; the port lights the vertex normal instead.
Shader "BlitzLegacy/ClientMap/Lit"
{
    Properties
    {
        [MainTexture] _Albedo("Albedo", 2D) = "white" {}
        _FlatColor("flatColor", Vector) = (1, 1, 1, 1)
        _TexShift("texture0Shift, tex0ShiftPerSecond", Vector) = (0, 0, 0, 0)
        _Lit("inGlossiness, inSpecularity, metalFresnelReflectance", Vector) = (0.5, 1, 0.5, 0)
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

    // The scene light's colours as the client's shaders read them.
    float4 _ClientLightColor;
    float4 _ClientAmbientColor;

    CBUFFER_START(UnityPerMaterial)
        float4 _FlatColor;
        float4 _TexShift;
        float4 _Lit;
        float4 _Options;
        float4 _Options2;
        float4 _FogColor;
        float _Cutoff;
    CBUFFER_END

    struct Attributes
    {
        float4 positionOS : POSITION;
        float3 normalOS : NORMAL;
        float2 uv0 : TEXCOORD0;
        half4 color : COLOR;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv0 : TEXCOORD0;
        float3 positionWS : TEXCOORD1;
        float3 normalWS : TEXCOORD2;
        half4 color : COLOR;
    };

    Varyings Vertex(Attributes input)
    {
        Varyings output = (Varyings)0;
        output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
        output.positionCS = TransformWorldToHClip(output.positionWS);
        output.normalWS = TransformObjectToWorldNormal(input.normalOS);
        output.uv0 = input.uv0;
        output.color = _Options2.x > 0.5 ? input.color : (half4)1.0;
        return output;
    }

    half4 Albedo(Varyings input)
    {
        float2 uv = input.uv0 + _TexShift.xy + frac(_TexShift.zw * _Time.y);
        return SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, DavaUv(uv));
    }

    float3 FresnelShlick(float cosine, float3 reflectance)
    {
        return (1.0 - reflectance) * pow(1.0 - cosine, 5.0) + reflectance;
    }

    // NormalizedBlinnPhong of lit-materials-fp. The power is capped where
    // the highlight is already narrower than a pixel: 5000 ^ 3 overflows
    // the client's mediump and only ever lit single texels.
    float BlinnPhong(float NdotH, float glossiness, float specularity)
    {
        float power = min(pow(5000.0, glossiness), 10000.0);
        return (power + 2.0) / 8.0 * pow(NdotH, power) * specularity;
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
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"

            half4 Fragment(Varyings input) : SV_Target
            {
                half4 albedo = Albedo(input);

                // The products are rotation invariant, so the client's
                // vectors can stay in Unity's world.
                float3 normal = normalize(input.normalWS);
                float3 toLight = normalize(_MainLightPosition.xyz);
                float3 toCamera = normalize(GetCameraPositionWS() - input.positionWS);
                float3 halfVector = normalize(toCamera + toLight);
                float NdotL = max(dot(normal, toLight), 0.0);
                float NdotH = max(dot(normal, halfVector), 0.0);
                float NdotV = max(dot(normal, toCamera), 0.0);

                float3 light = _ClientLightColor.rgb;
                float3 rgb = (_ClientAmbientColor.rgb + light * NdotL / PI) * albedo.rgb;
                float specular = BlinnPhong(NdotH, _Lit.x * albedo.a, NdotL * _Lit.y);
                rgb += light * specular * FresnelShlick(NdotV, _Lit.zzz);

                half4 result = half4((half3)rgb, _Options.z > 0.5 ? albedo.a : 1.0);
                result *= input.color;
                result *= (half4)_FlatColor;
                if (_Options.w > 0.5)
                {
                    half4 fog = ClientFog(input.positionWS);
                    if (_Options2.y > 0.5)
                        fog.rgb = (half3)_FogColor.rgb;
                    result.rgb = lerp(result.rgb, fog.rgb, fog.a);
                }
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

            half4 DepthFragment(Varyings input) : SV_Target
            {
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }
}
