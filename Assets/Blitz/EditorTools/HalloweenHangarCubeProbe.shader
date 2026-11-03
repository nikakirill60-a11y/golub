// Editor-only probe: samples _Cube along _Direction so HalloweenHangarProbes can
// read back which texel the GPU picks for a direction.
Shader "Hidden/HalloweenHangar/CubeProbe"
{
    Properties
    {
        _Cube ("Cube", Cube) = "" {}
        _Direction ("Direction", Vector) = (0, 0, 1, 0)
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            samplerCUBE _Cube;
            float4 _Direction;

            float4 frag(v2f_img i) : SV_Target
            {
                return texCUBElod(_Cube, float4(_Direction.xyz, 0.0));
            }
            ENDCG
        }
    }
}
