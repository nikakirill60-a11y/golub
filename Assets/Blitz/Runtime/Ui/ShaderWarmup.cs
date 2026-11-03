using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Compiles every shader the game draws with behind a loading bar, once
    /// per install and once after every update (or a GPU / driver / API
    /// change), so the hangar and the first battle do not hitch while the
    /// driver builds pipelines on first use.
    /// </summary>
    public static class ShaderWarmup
    {
        public const string StampFile = "shader_warmup.stamp";
        public const string SkipEnvironment = "BLITZ_SKIP_SHADER_WARMUP";
        public const string KeyVersion = "v2";

        /// <summary>Share of the bar spent on the per-shader draw pass.</summary>
        public const float DrawShare = 0.8f;

        public static readonly string[] ShaderNames =
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Simple Lit",
            "Universal Render Pipeline/Unlit",
            "Universal Render Pipeline/Particles/Unlit",
            "Universal Render Pipeline/Particles/Lit",
            "Particles/Standard Unlit",
            "Skybox/6 Sided",
            "Sprites/Default",
            "UI/Default",
            "Unlit/Transparent",
            "Blitz/UiColorReplace",
            "BlitzLegacy/Hangar/Armor Fill",
            "BlitzLegacy/Map/Object",
            "BlitzLegacy/Map/Vegetation",
            "BlitzLegacy/Map/Water",
            "BlitzLegacy/Map/Sky Face",
            "BlitzLegacy/Map/Landscape",
            "BlitzLegacy/Map/Lightmapped Object",
            "BlitzLegacy/Battle/Shell Trail",
            "BlitzLegacy/Post/Adjustments",
            "BlitzLegacy/ClientMap/Surface",
            "BlitzLegacy/ClientMap/Sky",
            "BlitzLegacy/Chest480/Unlit",
            "BlitzLegacy/Chest480/Box",
            "BlitzLegacy/Chest480/Particle",
            "BlitzLegacy/Chest480/Present",
            "BlitzLegacy/ClientMap/Water",
            "BlitzLegacy/ClientMap/ShadowVolume",
            "BlitzLegacy/ClientMap/Landscape",
        };

        /// <summary>Whether this process owns a graphics device that can draw.</summary>
        public static bool CanRender
        {
            get
            {
                return SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            }
        }

        public static string BuildKey(
            string appVersion, string unityVersion, string gpu, string api, string driver)
        {
            return string.Join("|", new[]
            {
                KeyVersion, appVersion ?? string.Empty, unityVersion ?? string.Empty,
                gpu ?? string.Empty, api ?? string.Empty, driver ?? string.Empty,
            });
        }

        public static bool IsCurrent(string stamp, string key)
        {
            return !string.IsNullOrEmpty(stamp) && stamp.Trim() == key;
        }

        public static string CurrentKey()
        {
            return BuildKey(
                Application.version, Application.unityVersion,
                SystemInfo.graphicsDeviceName,
                SystemInfo.graphicsDeviceType.ToString(),
                SystemInfo.graphicsDeviceVersion);
        }

        private static string StampPath
        {
            get { return Path.Combine(Application.persistentDataPath, StampFile); }
        }

        public static bool Needed
        {
            get
            {
                if (!Application.isPlaying || Application.isBatchMode) { return false; }
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SkipEnvironment)))
                {
                    return false;
                }

                try
                {
                    var path = StampPath;
                    var stamp = File.Exists(path) ? File.ReadAllText(path) : null;
                    return !IsCurrent(stamp, CurrentKey());
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        public static void MarkDone()
        {
            try { File.WriteAllText(StampPath, CurrentKey()); }
            catch (Exception e) { Debug.LogWarning("[Blitz] shader stamp not saved: " + e.Message); }
        }

        /// <summary>
        /// Draws a quad with every listed shader into a tiny target so the
        /// driver builds the real pipelines the game uses. Reports progress
        /// 0..1 between frames. Deliberately never calls WarmupAllShaders:
        /// Unity 6.6 mixes URP particle keyword spaces in that global path and
        /// also binds a non-MS texture to an MS sampler.
        /// </summary>
        public static IEnumerator Run(Action<float> progress)
        {
            var shaders = new List<Shader>();
            foreach (var name in ShaderNames)
            {
                var shader = Shader.Find(name);
                if (shader != null && !shaders.Contains(shader)) { shaders.Add(shader); }
            }

            // -nographics intentionally owns no render device. Keep the
            // iterator and its progress contract testable there, but never ask
            // Unity to create a RenderTexture that cannot exist. Shader.Find
            // above still validates the same player shader list; graphics runs
            // continue through the real draw path below.
            if (!CanRender)
            {
                for (var i = 0; i < shaders.Count; i++)
                {
                    progress(DrawShare * (i + 1f) / shaders.Count);
                    yield return null;
                }

                progress(1f);
                yield return null;
                yield break;
            }

            var target = RenderTexture.GetTemporary(64, 64, 16);
            var camGo = new GameObject("ShaderWarmupCamera");
            camGo.transform.position = new Vector3(0f, -2000f, 0f);
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.targetTexture = target;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 20f;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var collider = quad.GetComponent<Collider>();
            DestroySafely(collider);
            quad.transform.position = camGo.transform.position + new Vector3(0f, 0f, 2f);
            var renderer = quad.GetComponent<Renderer>();

            for (var i = 0; i < shaders.Count; i++)
            {
                var material = new Material(shaders[i]);
                renderer.sharedMaterial = material;
                try { cam.Render(); }
                catch (Exception e) { Debug.LogWarning("[Blitz] warmup " + shaders[i].name + ": " + e.Message); }
                DestroySafely(material);
                progress(DrawShare * (i + 1f) / shaders.Count);
                yield return null;
            }

            DestroySafely(quad);
            DestroySafely(camGo);
            RenderTexture.ReleaseTemporary(target);

            // The loading screen keeps filling while these targeted draws are
            // already compiled; no global variant pass is safe or necessary.
            progress(1f);
            yield return null;
        }

        private static void DestroySafely(UnityEngine.Object value)
        {
            if (value == null) { return; }
            if (Application.isPlaying) { UnityEngine.Object.Destroy(value); }
            else { UnityEngine.Object.DestroyImmediate(value); }
        }
    }
}
