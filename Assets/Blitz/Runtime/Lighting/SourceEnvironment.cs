using System;
using BlitzLegacy.Core.Maps;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Lighting
{
    /// <summary>
    /// Applies one source WorldEnvironment to Unity's render settings.
    ///
    /// Both ported scenes describe their environment in the same manifest, yet
    /// the two were drifting apart in code: the map read the manifest while the
    /// hangar carried hand-measured calibration factors from the days it was
    /// lit in realtime. The conversions every ported environment needs live
    /// here once, so the two scenes cannot disagree about them.
    /// </summary>
    public static class SourceEnvironment
    {
        // BlitzFog.hlsl reads the source fog curve from these three globals.
        public static readonly int FogColourId = Shader.PropertyToID("_BlitzFogColour");
        private static readonly int FogDepthId = Shader.PropertyToID("_BlitzFogDepth");
        private static readonly int FogHeightId = Shader.PropertyToID("_BlitzFogHeight");

        /// <summary>
        /// The 4.8 client maps switch BlitzFog.hlsl onto a DAVA linear curve
        /// through this global. A Godot environment has to clear it, or a
        /// battle loaded after a client map would keep the wrong fog.
        /// </summary>
        public static readonly int FogRangeId = Shader.PropertyToID("_BlitzFogRange");

        public static readonly int FogSkyId = Shader.PropertyToID("_BlitzFogSky");

        /// <summary>FOG_HALFSPACE density, falloff, height and limit of a client map.</summary>
        public static readonly int FogHalfspaceId = Shader.PropertyToID("_BlitzFogHalfspace");

        /// <summary>
        /// Switches of the client fog formulas (exact atmosphere angle,
        /// half-space fog and its linear variant); zero for every other scene.
        /// </summary>
        public static readonly int FogClientId = Shader.PropertyToID("_BlitzFogClient");

        /// <summary>Turns the client-only fog terms off for a scene that has none.</summary>
        public static void ClearClientFog()
        {
            Shader.SetGlobalVector(FogHalfspaceId, Vector4.zero);
            Shader.SetGlobalVector(FogClientId, Vector4.zero);
        }

        public static readonly int FogSunId = Shader.PropertyToID("_BlitzFogSun");

        public static readonly int FogSunDirectionId =
            Shader.PropertyToID("_BlitzFogSunDirection");

        /// <summary>
        /// Encodes a colour the source already resolved to linear, because
        /// Unity reads a Color field as sRGB and decodes it on the way to the
        /// shader. Encoding here is what keeps a ported colour from arriving
        /// squared. Authored manifest values are not linear yet - run them
        /// through LightingEnvironment.SourceChannel first.
        /// </summary>
        public static Color SourceColour(Color linear)
        {
            return linear.gamma;
        }

        /// <summary>
        /// The ambient term as Godot resolves it, carrying the tonemap
        /// exposure. Godot's linear tonemapper multiplies the whole frame after
        /// shading and Unity has no such stage, so the factor folds into the
        /// terms the port feeds the renderer instead.
        /// </summary>
        public static Color AmbientColour(LightingEnvironment source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            var ambient = source.AmbientRgb();
            return SourceColour(new Color(
                ambient[0] * source.TonemapExposure,
                ambient[1] * source.TonemapExposure,
                ambient[2] * source.TonemapExposure,
                1f));
        }

        /// <summary>
        /// Fog colour times its energy, with the same exposure folded in so it
        /// meets the shaded frame in the same units.
        /// </summary>
        public static Color FogColour(LightingEnvironment source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            var fog = source.FogLightRgb();
            return SourceColour(new Color(
                fog[0] * source.TonemapExposure,
                fog[1] * source.TonemapExposure,
                fog[2] * source.TonemapExposure,
                1f));
        }

        /// <summary>
        /// The same fog colour, shaped for the shader global the ported map
        /// shaders read. The difference from the Color above is the one that
        /// bit the port before: Unity decodes a Color on its way to the GPU,
        /// while a raw vector arrives untouched, so this one carries the
        /// decoded colour as it is. Alpha carries whether fog is enabled at
        /// all, which is what lets one global switch it off.
        /// </summary>
        public static Vector4 FogColourVector(LightingEnvironment source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            var fog = source.FogLightRgb();
            return new Vector4(
                fog[0] * source.TonemapExposure,
                fog[1] * source.TonemapExposure,
                fog[2] * source.TonemapExposure,
                source.FogEnabled ? 1f : 0f);
        }

        /// <summary>
        /// Intensity a ported light needs to stand in for a source one: the
        /// authored energy through the same exposure fold.
        /// </summary>
        public static float LightIntensity(LightingEnvironment source, float energy)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return energy * source.TonemapExposure;
        }

        /// <summary>
        /// Ambient, fog and the ambient probe. Whatever draws the background
        /// stays the caller's business: both scenes derive it from the same
        /// background colour, but only the map draws a sky while the hangar
        /// clears its camera to a solid colour.
        /// </summary>
        public static void Apply(LightingEnvironment source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            // ambient_light_source stays on Background in both scenes, so the
            // ambient term is background_color * background_energy_multiplier
            // and the ambient_light_color the Environment also stores is unused.
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = AmbientColour(source);

            // Unity's built-in linear fog is what everything outside the
            // ported shaders reads - the tank draws with URP Lit - so it keeps
            // the colour, the energy and the range.
            RenderSettings.fog = source.FogEnabled;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = FogColour(source);
            RenderSettings.fogStartDistance = source.FogDepthBegin;
            RenderSettings.fogEndDistance = source.FogDepthEnd;

            // The ported map shaders read the source curve instead, because a
            // linear ramp expresses none of the density, the depth curve or the
            // height term the source fog also carries. The map needs all three:
            // density 0.77, curve 0.78 and a 0.04 height term below y = 10.
            Shader.SetGlobalVector(FogColourId, FogColourVector(source));
            Shader.SetGlobalVector(FogDepthId, new Vector4(
                source.FogDepthBegin,
                source.FogDepthEnd,
                source.FogDensity,
                source.FogDepthCurve));
            Shader.SetGlobalVector(FogHeightId, new Vector4(
                source.FogHeight,
                source.FogHeightDensity,
                0f,
                0f));
            Shader.SetGlobalVector(FogRangeId, Vector4.zero);
            Shader.SetGlobalVector(FogSkyId, Vector4.zero);
            ClearClientFog();

            // The frame-wide grade cannot live in the map shaders the way
            // the fog does: the source applies adjustment_* to the finished
            // frame, so the sky and the tank are graded with the map.
            SourceAdjustments.Apply(source);

            // Everything above is the authored environment, which the scene
            // re-pushes every time it loads. The fog row of the graphics tab
            // is the player's answer to it, so it is applied last and wins.
            GraphicsQuality.ApplyFog();

            // A player build never refreshes the ambient probe on its own:
            // flat ambient only reaches the shaders once the environment is
            // pushed, which is why the render CLIs do the same before a capture.
            DynamicGI.UpdateEnvironment();
        }
    }
}
