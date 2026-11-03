// Port of assets/tanks/base/armor_new.gdshader, the overlay the source paints
// over an enemy's armour plates while the player holds it in the sniper scope.
//
// The source renders it unshaded, additive, back faces culled and fog free, at
// a third of full strength, so a plate the current shell goes through adds
// nothing at all and stays invisible. A plate it cannot beat adds red, and a
// high explosive round paints the plates its splash can still hurt in amber.
Shader "BlitzLegacy/Battle/Armor Preview"
{
    Properties
    {
        _NominalArmor ("Nominal armour (mm)", Float) = 38
        _Caliber ("Caliber (mm)", Float) = 12
        _Penetration ("Penetration (mm)", Float) = 53
        _Normalization ("Normalization (radians)", Float) = 0
        _HE ("High explosive", Float) = 0
        _HEAT ("Shaped charge", Float) = 0
        [HideInInspector] _StencilComp ("Stencil comparison", Float) = 3

        // The scope draws the overlay exactly as the source does. The hangar
        // inspector reuses the same colour rule but wants the plates on top of
        // the tank rather than fighting its depth, so it overrides these.
        [HideInInspector] _ZTest ("Depth test", Float) = 4
        [HideInInspector] _SrcBlend ("Source blend", Float) = 5
        [HideInInspector] _DstBlend ("Destination blend", Float) = 1
        [HideInInspector] _Extrude ("Extrusion from the node origin", Float) = 1
        [HideInInspector] _Alpha ("Overlay strength", Float) = 0.33
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Unlit"
            "Queue" = "Transparent+1"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }

            // blend_add with ALPHA = 0.33: the fragment adds a third of its
            // own colour and never darkens what is already on screen. The
            // strength now rides the alpha channel, so SrcAlpha One reproduces
            // the source and SrcAlpha OneMinusSrcAlpha gives the hangar a
            // readable opaque fill from the very same fragment.
            Blend [_SrcBlend] [_DstBlend]
            Cull Back
            ZWrite Off
            ZTest [_ZTest]

            // AimTraceHit enables the target's outline together with armour.
            // Its opaque mask stamps exactly the visible tank silhouette. Clip
            // the low-poly collision shell to that stamp so authored plates
            // can never float above or beside the rendered turret.
            Stencil
            {
                Ref 1
                Comp [_StencilComp]
                Pass Keep
            }

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _NominalArmor;
                float _Caliber;
                float _Penetration;
                float _Normalization;
                float _HE;
                float _HEAT;
                float _Extrude;
                float _Alpha;
            CBUFFER_END

            // ALPHA = 0.33 in the source shader.
            static const float ArmorPreviewAlpha = 0.33;

            // The angle the source calls a graze. It compares the angle in
            // radians against the literal 70, which no angle ever reaches, so
            // the branch never fires; it is kept here to stay honest to it.
            static const float ArmorPreviewGrazeAngle = 70.0;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;

                // The source pushes every vertex away from the node origin so
                // the overlay floats just clear of the hull it covers. Its
                // NODE_POSITION_WORLD is this object's origin, which is the
                // object space origin here.
                float3 positionOS = input.positionOS.xyz;
                float distanceToOrigin = length(positionOS);
                float3 direction = distanceToOrigin > 0.0
                    ? positionOS / distanceToOrigin
                    : float3(0.0, 0.0, 0.0);
                positionOS += direction
                    * ((distanceToOrigin * 0.01 + 0.02) * _Extrude);

                output.positionWS = TransformObjectToWorld(positionOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float3 toCamera = normalize(_WorldSpaceCameraPos - input.positionWS);
                float d = dot(toCamera, normalize(input.normalWS));

                // How far the shell arrives off the plate normal, minus the
                // normalisation it turns into the plate with.
                float angle = PI * 0.5 - asin(abs(d)) - _Normalization;
                angle = abs(max(angle, 0.0));

                bool explodes = _HEAT > 0.5 || _HE > 0.5;
                bool threeCaliber = !explodes && _NominalArmor * 3.0 < _Caliber;

                float effectiveArmor = threeCaliber
                    ? 0.0
                    : max(_NominalArmor / cos(angle), 0.0);
                effectiveArmor = (!explodes && angle >= ArmorPreviewGrazeAngle)
                    ? _Penetration + 1.0
                    : effectiveArmor;

                // One where the plate wins, zero where the shell goes through.
                float blocked = step(_Penetration, effectiveArmor);

                float3 albedo = _HE > 0.5
                    ? float3(blocked, blocked * 0.33, 0.0)
                    : float3(blocked, 0.0, 0.0);

                // Premultiplying here would lock the overlay to additive
                // blending. Carrying the strength in alpha instead lets one
                // shader serve both the scope and the hangar; with
                // Blend SrcAlpha One and _Alpha at ALPHA the result is the
                // source's albedo * 0.33 to the bit.
                return half4(albedo, blocked * _Alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
