using System;
using BlitzLegacy.Core.Maps;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace BlitzLegacy.Runtime.Lighting
{
    /// <summary>
    /// Ports one WorldEnvironment's colour adjustment.
    ///
    /// The fog could live inside the ported map shaders, because only ported
    /// geometry is fogged. This cannot: Godot grades the finished frame in
    /// tonemap.glsl, so the sky and the tank are graded along with the map.
    /// It therefore runs as one fullscreen pass after post processing, which
    /// is where the source applies it in the frame too.
    /// </summary>
    public static class SourceAdjustments
    {
        /// <summary>Pinned in ProjectBootstrap so a player build keeps it.</summary>
        public const string ShaderName = "BlitzLegacy/Post/Adjustments";

        private static readonly int AdjustId = Shader.PropertyToID("_BlitzAdjust");

        private static AdjustmentPass _pass;
        private static Material _material;
        private static bool _hooked;
        private static bool _graded;

        /// <summary>
        /// brightness, contrast, saturation and whether the source grades at
        /// all. The hangar leaves adjustment_enabled off, so it reports the
        /// identity grade with a zero flag and the pass stays out of its frame.
        /// </summary>
        public static Vector4 AdjustVector(LightingEnvironment source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (!source.AdjustmentEnabled)
            {
                return new Vector4(1f, 1f, 1f, 0f);
            }

            return new Vector4(
                source.AdjustmentBrightness,
                source.AdjustmentContrast,
                source.AdjustmentSaturation,
                1f);
        }

        /// <summary>
        /// Pushes the grade and, while an applied environment asks for one,
        /// enqueues the pass on the cameras URP renders.
        /// </summary>
        public static void Apply(LightingEnvironment source)
        {
            var adjust = AdjustVector(source);
            Shader.SetGlobalVector(AdjustId, adjust);
            _graded = adjust.w > 0.5f;

            if (!_graded || _hooked)
            {
                return;
            }

            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            _hooked = true;
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!_graded || camera == null)
            {
                return;
            }

            // Material previews and reflection probes render frames the grade
            // has no business touching; the game view and the render CLIs do.
            if (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)
            {
                return;
            }

            var cameraData = camera.GetUniversalAdditionalCameraData();
            if (cameraData == null || cameraData.scriptableRenderer == null)
            {
                return;
            }

            if (_material == null)
            {
                var shader = Shader.Find(ShaderName);
                if (shader == null)
                {
                    // An ungraded frame beats a black one: ProjectBootstrap
                    // ships the shader, and this says so when it has not.
                    Debug.LogWarning("[Blitz] source adjustments off: no " + ShaderName);
                    _graded = false;
                    return;
                }

                _material = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }

            if (_pass == null)
            {
                _pass = new AdjustmentPass(_material);
            }

            cameraData.scriptableRenderer.EnqueuePass(_pass);
        }

        private sealed class AdjustmentPass : ScriptableRenderPass
        {
            private readonly Material _material;

            public AdjustmentPass(Material material)
            {
                _material = material;
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;

                // The grade reads the frame it writes, so URP has to hand the
                // camera an intermediate colour target instead of letting it
                // render straight into the backbuffer.
                requiresIntermediateTexture = true;
                profilingSampler = new ProfilingSampler("Blitz source adjustments");
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (!resources.cameraColor.IsValid())
                {
                    return;
                }

                // A raster pass cannot sample the attachment it writes to, so
                // the frame is copied once and the grade reads the copy.
                var description = renderGraph.GetTextureDesc(resources.cameraColor);
                description.name = "_BlitzAdjustmentSource";
                description.clearBuffer = false;

                var copy = renderGraph.CreateTexture(description);
                renderGraph.AddBlitPass(
                    resources.cameraColor,
                    copy,
                    Vector2.one,
                    Vector2.zero,
                    passName: "Blitz adjustments copy");

                renderGraph.AddBlitPass(
                    new RenderGraphUtils.BlitMaterialParameters(
                        copy,
                        resources.activeColorTexture,
                        _material,
                        0),
                    "Blitz source adjustments");
            }
        }
    }
}
