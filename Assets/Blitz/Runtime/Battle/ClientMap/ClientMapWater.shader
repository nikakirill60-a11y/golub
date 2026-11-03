// WaterPerPixelCubemapAlphablend, the HIGH variant of WaterAllQualities
// (water-vp.sl / water-fp.sl, PIXEL_LIT + FRESNEL_TO_ALPHA):
//
//   uv0 = uv * normal0Scale + frac(normal0ShiftPerSecond * t)
//   uv1 = (u + v, v - u) * normal1Scale + frac(normal1ShiftPerSecond * t)
//   n   = normalize(normalmap(uv0) + normalmap(uv1) - 1)       tangent space
//   fresnel = bias + (1 - bias) * pow(1 - max(dot(-view, n), 0), pow)
//   r   = reflect(view, n), r.z = |r.z|, back through (t, cross(n, t), n)
//   out = (cubemap(r) * reflectionTintColor, fresnel), fogged, alpha blended
//
// The tangent frame and the cube lookup are the client's (x, y, z) - Unity's
// world (x, z, y) - and the cube faces are imported in the client's order, so
// the lookup vector goes in unchanged.
Shader "BlitzLegacy/ClientMap/Water"
{
    Properties
    {
        _NormalMap("Normal map", 2D) = "bump" {}
        _Cube("Reflection cubemap", Cube) = "grey" {}
        _NormalScale("normal0Scale, normal1Scale", Vector) = (1, 1, 0, 0)
        _NormalShift("normal0ShiftPerSecond, normal1ShiftPerSecond", Vector) = (0, 0, 0, 0)
        _Fresnel("fresnelBias, fresnelPow", Vector) = (0, 1, 0, 0)
        _ReflectionTint("reflectionTintColor", Vector) = (1, 1, 1, 1)
        _Options("w: fog", Vector) = (0, 0, 0, 1)
        _Cull("Cull", Float) = 2
        _SrcBlend("Source blend", Float) = 5
        _DstBlend("Destination blend", Float) = 10
        _ZWrite("Depth write", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Name "ClientWater"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "ClientMap.hlsl"

            TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);
            TEXTURECUBE(_Cube); SAMPLER(sampler_Cube);

            CBUFFER_START(UnityPerMaterial)
                float4 _NormalScale;
                float4 _NormalShift;
                float4 _Fresnel;
                float4 _ReflectionTint;
                float4 _Options;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv0 : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normal : TEXCOORD2;
                float3 tangent : TEXCOORD3;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                float2 uv = input.uv0;
                output.uv.xy = uv * _NormalScale.x + frac(_NormalShift.xy * _Time.y);
                output.uv.zw = float2(uv.x + uv.y, uv.y - uv.x) * _NormalScale.y
                    + frac(_NormalShift.zw * _Time.y);
                // The client's frame: Unity (x, y, z) is DAVA (x, z, y).
                output.normal = TransformObjectToWorldNormal(input.normalOS).xzy;
                output.tangent = TransformObjectToWorldDir(input.tangentOS.xyz).xzy;
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float3 normal0 = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, DavaUv(input.uv.xy)).rgb;
                float3 normal1 = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, DavaUv(input.uv.zw)).rgb;
                float3 normal = normalize(normal0 + normal1 - 1.0);

                float3 n = normalize(input.normal);
                float3 t = normalize(input.tangent);
                float3 b = cross(n, t);
                float3 toPoint = (input.positionWS - GetCameraPositionWS()).xzy;
                float3 view = normalize(float3(dot(toPoint, t), dot(toPoint, b), dot(toPoint, n)));

                float lambert = max(dot(-view, normal), 0.0);
                float fresnel = _Fresnel.x + (1.0 - _Fresnel.x) * pow(1.0 - lambert, _Fresnel.y);
                float3 reflection = reflect(view, normal);
                reflection.z = abs(reflection.z);
                float3 world = reflection.x * t + reflection.y * b + reflection.z * n;
                half3 colour = SAMPLE_TEXTURECUBE(_Cube, sampler_Cube, world).rgb * (half3)_ReflectionTint.rgb;
                if (_Options.w > 0.5)
                {
                    half4 fog = ClientFog(input.positionWS);
                    colour = lerp(colour, fog.rgb, fog.a);
                }
                return half4(ClientOutput(colour), saturate(fresnel));
            }
            ENDHLSL
        }
    }
}
