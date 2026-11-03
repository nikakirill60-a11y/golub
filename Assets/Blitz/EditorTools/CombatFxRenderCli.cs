using System;
using System.IO;
using BlitzLegacy.Runtime.Battle.CombatFx;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// GPU captures of the ported combat effects, case for case and frame for
    /// frame the same as <c>Tools/godot_combat_fx_render.gd</c> renders in
    /// Godot: one fixed camera, a flat background, no fog, emitter seeds
    /// 1000 + index and exactly 1/60 s per frame.
    /// Output: <c>Logs/combat-fx-render/&lt;case&gt;_&lt;frame&gt;.png</c>.
    /// </summary>
    public static class CombatFxRenderCli
    {
        private const int Width = 960;
        private const int Height = 540;
        private const float Delta = 1f / 60f;
        private const uint SeedBase = 1000u;
        private const string Output = "Logs/combat-fx-render";

        // Godot-space poses of the reference script.
        private static readonly Vector3 CameraPosition = new Vector3(0f, 1.5f, 7f);
        private static readonly Vector3 CameraTarget = new Vector3(0f, 0.8f, 0f);
        private static readonly Vector3 GunPosition = new Vector3(-1.5f, 0.8f, 0f);
        private static readonly Vector3 GunDirection = new Vector3(1f, 0.05f, -0.2f);
        private static readonly Vector3 TrailStart = new Vector3(-6f, 0.8f, 0f);
        private static readonly Vector3 TrailDirection = new Vector3(1f, 0f, -0.15f);

        private sealed class Case
        {
            public string Name;
            public string Kind;
            public string Scene;
            public int Caliber;
            public Vector3 Normal = Vector3.up;
            public bool Heat;
            public float Speed;
            public int[] Frames;
        }

        private static readonly Case[] Cases =
        {
            new Case { Name = "armor_ap", Kind = "impact", Scene = "armor_ap", Caliber = 100, Frames = new[] { 2, 6, 15, 40 } },
            new Case { Name = "armor_he", Kind = "impact", Scene = "armor_he", Caliber = 100, Normal = new Vector3(0f, 0f, 1f), Frames = new[] { 2, 6, 15, 40 } },
            new Case { Name = "armor_he_tilt", Kind = "impact", Scene = "armor_he", Caliber = 100, Normal = new Vector3(0f, 0.3f, 1f), Frames = new[] { 2, 6, 15, 40 } },
            new Case { Name = "object_heat", Kind = "impact", Scene = "object_heat", Caliber = 100, Frames = new[] { 2, 6, 15, 40 } },
            new Case { Name = "object_ap", Kind = "impact", Scene = "object_ap", Caliber = 85, Normal = new Vector3(0.6f, 0.8f, 0f), Frames = new[] { 2, 6, 15, 40 } },
            new Case { Name = "shoot_main", Kind = "shot", Scene = "shoot_main", Frames = new[] { 1, 3, 8, 30 } },
            new Case { Name = "shoot_medium", Kind = "shot", Scene = "shoot_medium", Frames = new[] { 1, 3, 8, 30 } },
            new Case { Name = "shoot_huge", Kind = "shot", Scene = "shoot_huge", Frames = new[] { 1, 3, 8, 30 } },
            new Case { Name = "trail_ap", Kind = "trail", Caliber = 100, Speed = 150f, Frames = new[] { 6, 12, 20, 30 } },
            new Case { Name = "trail_heat", Kind = "trail", Caliber = 122, Heat = true, Speed = 150f, Frames = new[] { 6, 12, 20, 30 } },
        };

        public static void Render()
        {
            var errors = 0;
            Application.logMessageReceived += (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception) errors++;
            };

            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Directory.CreateDirectory(Output);
                // No fog: the reference environment disables it.
                Shader.SetGlobalVector("_BlitzFogColour", Vector4.zero);
                Shader.SetGlobalVector("_BlitzFogRange", Vector4.zero);
                Shader.SetGlobalVector("_BlitzFogHeight", Vector4.zero);

                var camera = new GameObject("combat fx observer").AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.35f, 0.4f, 0.45f, 1f);
                camera.fieldOfView = 50f;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 500f;
                camera.allowMSAA = false;
                camera.allowHDR = false;
                camera.transform.position = GodotFxMath.Mirror(CameraPosition);
                camera.transform.LookAt(GodotFxMath.Mirror(CameraTarget), Vector3.up);

                var only = Environment.GetEnvironmentVariable("BLITZ_COMBAT_FX_CASE");
                foreach (var c in Cases)
                {
                    if (!string.IsNullOrEmpty(only) && only != c.Name) continue;
                    RenderCase(c, camera);
                }

                UnityEngine.Object.DestroyImmediate(camera.gameObject);
                if (errors != 0) throw new InvalidOperationException("combat fx render errors: " + errors);
                Debug.Log("[Blitz] combat fx captures complete -> " + Output);
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(1);
            }
        }

        private static void RenderCase(Case c, Camera camera)
        {
            var manifest = CombatFxManifest.Shared;
            GodotParticleEffect effect = null;
            BattleGpuTrail trail = null;
            var step = Vector3.zero;
            if (c.Kind == "trail")
            {
                var start = GodotFxMath.Mirror(TrailStart);
                var direction = GodotFxMath.Mirror(TrailDirection.normalized);
                trail = BattleGpuTrail.Create(c.Caliber, c.Heat, start, start + direction * 10f);
                step = direction * c.Speed / 60f;
            }
            else
            {
                var scene = manifest.Scene(c.Scene);
                effect = GodotParticleEffect.Create(scene, c.Name);
                if (c.Kind == "impact")
                {
                    var scale = c.Caliber * manifest.ImpactScaleFactor;
                    var root = scene.Root;
                    effect.Root = new GodotXform(root.X.normalized * scale, root.Y.normalized * scale, root.Z.normalized * scale, Vector3.zero);
                    effect.Play(false, c.Normal, i => SeedBase + (uint)i);
                }
                else
                {
                    var gun = GodotFxMath.LookingAt(GunPosition, GunPosition + GunDirection);
                    var basis = scene.Root;
                    basis.Origin = Vector3.zero;
                    effect.Root = gun * basis;
                    effect.Play(false, null, i => SeedBase + (uint)i);
                }
            }

            var last = 0;
            foreach (var f in c.Frames) last = Mathf.Max(last, f);
            for (var frame = 1; frame <= last; frame++)
            {
                if (trail != null)
                {
                    trail.Follow(GodotFxMath.Mirror(TrailStart) + step * (frame - 1));
                    trail.Tick(Delta, camera.transform);
                }
                else
                {
                    effect.Tick(Delta, camera.transform);
                }

                if (Array.IndexOf(c.Frames, frame) >= 0)
                {
                    Capture(camera, string.Format("{0}_{1:00}", c.Name, frame));
                }
            }

            if (trail != null) UnityEngine.Object.DestroyImmediate(trail.gameObject);
            if (effect != null) UnityEngine.Object.DestroyImmediate(effect.gameObject);
        }

        private static void Capture(Camera camera, string name)
        {
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var image = new Texture2D(Width, Height, TextureFormat.RGB24, false, false);
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                image.Apply();
                File.WriteAllBytes(Path.Combine(Output, name + ".png"), image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(image);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
