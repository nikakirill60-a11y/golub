using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.EditorTools
{
    // Temporary diagnostic. Every editor capture so far ran on the PC quality
    // level, while the shipped Android frame runs the Mobile one: a different
    // pipeline asset, 0.8 render scale, a 1024 shadow map and no depth or
    // opaque copy. This shoots the same gameplay cameras through both levels
    // and then peels one layer off at a time under the mobile pipeline, so the
    // blotches can be attributed to a layer instead of guessed at. Removed
    // before the fix is committed.
    public static class BattleParityCli
    {
        private const int Width = 1196;
        private const int Height = 540;
        private const string OutputDirectory = "Logs/battle-parity";
        private const int WarmupFrames = 8;

        private sealed class Variant
        {
            public string Name;
            public Action Apply;
            public Action Undo;
        }

        private struct Frame
        {
            public float Luma;
            public float GroundMacro;
        }

        public static void Capture()
        {
            try
            {
                EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);

                var world = BattleWorld.Create(null, BattleWorld.DefaultMapId);
                if (world == null || world.PlayerSpawn == null)
                {
                    throw new InvalidOperationException("the world did not build");
                }

                DynamicGI.UpdateEnvironment();
                Directory.CreateDirectory(OutputDirectory);

                var camera = NewCamera();
                var material = world.Landscape
                    .GetComponent<MeshRenderer>().sharedMaterial;

                var names = QualitySettings.names;
                for (var level = 0; level < names.Length; level++)
                {
                    QualitySettings.SetQualityLevel(level, true);
                    DynamicGI.UpdateEnvironment();
                    var pipeline = QualitySettings.renderPipeline;
                    Debug.Log("[Blitz] parity level=" + names[level] +
                        " pipeline=" +
                        (pipeline == null ? "graphics-default" : pipeline.name) +
                        " shadows=" + QualitySettings.shadows +
                        " aniso=" + QualitySettings.anisotropicFiltering);
                    Shoot(world, camera, names[level].ToLowerInvariant());
                }

                // The shipped level, with one layer removed at a time.
                QualitySettings.SetQualityLevel(0, true);
                DynamicGI.UpdateEnvironment();

                var fogColour = Shader.GetGlobalVector("_BlitzFogColour");
                var sunShadows = world.Sun.shadows;
                var detailStrength = material.GetFloat("_DetailStrength");
                var brightness = material.GetFloat("_Brightness");
                var contrast = material.GetFloat("_Contrast");

                var variants = new List<Variant>
                {
                    new Variant
                    {
                        Name = "fog-off",
                        Apply = () => Shader.SetGlobalVector("_BlitzFogColour",
                            new Vector4(fogColour.x, fogColour.y, fogColour.z, 0f)),
                        Undo = () => Shader.SetGlobalVector("_BlitzFogColour", fogColour)
                    },
                    new Variant
                    {
                        Name = "sun-shadows-off",
                        Apply = () => world.Sun.shadows = LightShadows.None,
                        Undo = () => world.Sun.shadows = sunShadows
                    },
                    new Variant
                    {
                        Name = "sun-off",
                        Apply = () => world.Sun.enabled = false,
                        Undo = () => world.Sun.enabled = true
                    },
                    new Variant
                    {
                        Name = "detail-off",
                        Apply = () => material.SetFloat("_DetailStrength", 0f),
                        Undo = () => material.SetFloat("_DetailStrength", detailStrength)
                    },
                    new Variant
                    {
                        Name = "grade-off",
                        Apply = () =>
                        {
                            material.SetFloat("_Brightness", 1f);
                            material.SetFloat("_Contrast", 1f);
                        },
                        Undo = () =>
                        {
                            material.SetFloat("_Brightness", brightness);
                            material.SetFloat("_Contrast", contrast);
                        }
                    },
                    new Variant
                    {
                        Name = "objects-off",
                        Apply = () => world.Objects.Root.SetActive(false),
                        Undo = () => world.Objects.Root.SetActive(true)
                    }
                };

                for (var i = 0; i < variants.Count; i++)
                {
                    var variant = variants[i];
                    variant.Apply();
                    try
                    {
                        Shoot(world, camera, "mobile-" + variant.Name);
                    }
                    finally
                    {
                        variant.Undo();
                    }
                }

                Debug.Log("[Blitz] parity ok out=" +
                    Path.GetFullPath(OutputDirectory));
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static void Shoot(BattleWorld world, Camera camera, string tag)
        {
            var spawn = world.PlayerSpawn;
            camera.transform.position =
                spawn.position - spawn.forward * 9f + Vector3.up * 4f;
            camera.transform.rotation = Quaternion.LookRotation(
                spawn.forward + Vector3.down * 0.18f, Vector3.up);
            Log("spawn/" + tag, Capture(camera, "spawn-" + tag + ".png"));

            // Tank height across the middle of the map: the band where the
            // reported stains cover the ground.
            camera.transform.position =
                world.PlayArea.center + new Vector3(0f, 14f, -150f);
            camera.transform.rotation = Quaternion.LookRotation(
                new Vector3(0f, -0.06f, 1f), Vector3.up);
            Log("field/" + tag, Capture(camera, "field-" + tag + ".png"));

            camera.transform.position =
                world.PlayArea.center + new Vector3(0f, 210f, -250f);
            camera.transform.rotation = Quaternion.LookRotation(
                new Vector3(0f, -0.62f, 0.78f), Vector3.up);
            Log("overview/" + tag, Capture(camera, "overview-" + tag + ".png"));
        }

        private static void Log(string label, Frame frame)
        {
            Debug.Log("[Blitz] parity " + label +
                " luma=" + frame.Luma.ToString("F4") +
                " groundMacro=" + frame.GroundMacro.ToString("F4"));
        }

        private static Camera NewCamera()
        {
            var go = new GameObject("ParityCamera");
            var camera = go.AddComponent<Camera>();
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 4000f;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.aspect = Width / (float)Height;
            return camera;
        }

        private static Frame Capture(Camera camera, string fileName)
        {
            var target = new RenderTexture(
                Width, Height, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            var texture = new Texture2D(
                Width, Height, TextureFormat.RGB24, false, false);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                for (var i = 0; i < WarmupFrames; i++)
                {
                    camera.Render();
                }

                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
                texture.Apply(false, false);
                File.WriteAllBytes(
                    Path.Combine(OutputDirectory, fileName), texture.EncodeToPNG());
                return Measure(texture);
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = previousTarget;
                UnityEngine.Object.DestroyImmediate(texture);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        // ReadPixels starts at the bottom row, so the lower half of the buffer
        // is ground rather than sky. Low frequency cell means over that band
        // are what a stain shows up in.
        private static Frame Measure(Texture2D texture)
        {
            var pixels = texture.GetPixels32();
            var total = 0d;
            for (var i = 0; i < pixels.Length; i++)
            {
                total += Luma(pixels[i]);
            }

            const int cellsX = 24;
            const int cellsY = 8;
            var cellWidth = Width / cellsX;
            var cellHeight = Height / 2 / cellsY;
            var means = new double[cellsX * cellsY];
            var cursor = 0;
            for (var cellY = 0; cellY < cellsY; cellY++)
            {
                for (var cellX = 0; cellX < cellsX; cellX++)
                {
                    var cell = 0d;
                    for (var y = 0; y < cellHeight; y++)
                    {
                        var row = cellY * cellHeight + y;
                        for (var x = 0; x < cellWidth; x++)
                        {
                            cell += Luma(pixels[row * Width + cellX * cellWidth + x]);
                        }
                    }

                    means[cursor++] = cell / (cellWidth * cellHeight);
                }
            }

            var mean = 0d;
            for (var i = 0; i < means.Length; i++) mean += means[i];
            mean /= means.Length;
            var variance = 0d;
            for (var i = 0; i < means.Length; i++)
            {
                var delta = means[i] - mean;
                variance += delta * delta;
            }

            var frame = new Frame();
            frame.Luma = (float)(total / pixels.Length);
            frame.GroundMacro = mean <= 0d
                ? float.PositiveInfinity
                : (float)(Math.Sqrt(variance / means.Length) / mean);
            return frame;
        }

        private static double Luma(Color32 pixel)
        {
            return (pixel.r * 0.2126d + pixel.g * 0.7152d + pixel.b * 0.0722d) / 255d;
        }
    }
}
