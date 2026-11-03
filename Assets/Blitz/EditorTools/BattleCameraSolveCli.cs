using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    // Temporary diagnostic used to recover the camera of the saved underpass
    // frame. It is removed before the production fix is committed.
    public static class BattleCameraSolveCli
    {
        private const int Width = 299;
        private const int Height = 135;
        private const string ReferencePath = "Logs/battle-render/underpass-clean.png";

        private struct Candidate
        {
            public float Score;
            public float Azimuth;
            public float Distance;
            public float Height;
            public float TargetHeight;
            public float Fov;
        }

        public static void Refine()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var world = BattleWorld.Create(null, BattleWorld.DefaultMapId);
                DynamicGI.UpdateEnvironment();
                var focus = world.Objects.Root.transform.Find("a_bridge01_01");
                if (focus == null) throw new InvalidOperationException("bridge focus missing");
                var reference = LoadReference();
                var cameraGo = new GameObject("CameraRefine");
                var camera = cameraGo.AddComponent<Camera>();
                camera.aspect = Width / (float)Height;
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 4000f;
                camera.clearFlags = CameraClearFlags.Skybox;
                var current = new Candidate
                {
                    Azimuth = 225f, Distance = 60f, Height = 24f,
                    TargetHeight = 0f, Fov = 55f, Score = -2f
                };
                Place(camera, focus.position, current.Azimuth, current.Distance,
                    current.Height, current.TargetHeight, current.Fov);
                Render(camera); Render(camera);
                current.Score = Evaluate(camera, focus.position, reference, current);
                var steps = new[] { 5f, 5f, 2f, 2f, 2f };
                for (var pass = 0; pass < 5; pass++)
                {
                    for (var parameter = 0; parameter < 5; parameter++)
                    {
                        var best = current;
                        for (var offset = -5; offset <= 5; offset++)
                        {
                            var candidate = current;
                            SetParameter(ref candidate, parameter,
                                GetParameter(candidate, parameter) + offset * steps[parameter]);
                            candidate.Score = Evaluate(camera, focus.position, reference, candidate);
                            if (candidate.Score > best.Score) best = candidate;
                        }
                        current = best;
                    }
                    for (var i = 0; i < steps.Length; i++) steps[i] *= 0.5f;
                    Debug.Log("[Blitz] camera refine pass=" + pass + " " + Describe(current));
                }
                Debug.Log("[Blitz] camera refined focus=" + focus.position.ToString("F3") +
                    " " + Describe(current));
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        public static void Solve()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var world = BattleWorld.Create(null, BattleWorld.DefaultMapId);
                DynamicGI.UpdateEnvironment();
                var focus = world.Objects.Root.transform.Find("a_bridge01_01");
                if (focus == null) throw new InvalidOperationException("bridge focus missing");

                var reference = LoadReference();
                var cameraGo = new GameObject("CameraSolve");
                var camera = cameraGo.AddComponent<Camera>();
                camera.aspect = Width / (float)Height;
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 4000f;
                camera.clearFlags = CameraClearFlags.Skybox;

                // Prime URP once before scoring moving cameras.
                Place(camera, focus.position, 225f, 70f, 15f, 4f, 60f);
                Render(camera);
                Render(camera);

                var best = new List<Candidate>();
                for (var fov = 55f; fov <= 65f; fov += 10f)
                {
                    for (var azimuth = 0f; azimuth < 360f; azimuth += 15f)
                    {
                        for (var distance = 30f; distance <= 120f; distance += 30f)
                        {
                            for (var height = 8f; height <= 24f; height += 8f)
                            {
                                for (var targetHeight = 0f; targetHeight <= 5f; targetHeight += 5f)
                                {
                                    Place(camera, focus.position, azimuth, distance, height, targetHeight, fov);
                                    var score = Score(reference, Render(camera));
                                    Add(best, new Candidate
                                    {
                                        Score = score,
                                        Azimuth = azimuth,
                                        Distance = distance,
                                        Height = height,
                                        TargetHeight = targetHeight,
                                        Fov = fov
                                    });
                                }
                            }
                        }
                    }
                }

                Debug.Log("[Blitz] camera solve focus=" + focus.position.ToString("F3"));
                for (var i = 0; i < best.Count; i++)
                {
                    var c = best[i];
                    Debug.Log("[Blitz] camera candidate " + i +
                        " score=" + c.Score.ToString("F6") +
                        " az=" + c.Azimuth.ToString("F1") +
                        " dist=" + c.Distance.ToString("F1") +
                        " height=" + c.Height.ToString("F1") +
                        " targetHeight=" + c.TargetHeight.ToString("F1") +
                        " fov=" + c.Fov.ToString("F1"));
                }

                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static float Evaluate(Camera camera, Vector3 focus,
            float[] reference, Candidate candidate)
        {
            Place(camera, focus, candidate.Azimuth, candidate.Distance,
                candidate.Height, candidate.TargetHeight, candidate.Fov);
            return Score(reference, Render(camera));
        }

        private static float GetParameter(Candidate candidate, int parameter)
        {
            switch (parameter)
            {
                case 0: return candidate.Azimuth;
                case 1: return candidate.Distance;
                case 2: return candidate.Height;
                case 3: return candidate.TargetHeight;
                default: return candidate.Fov;
            }
        }

        private static void SetParameter(ref Candidate candidate, int parameter, float value)
        {
            switch (parameter)
            {
                case 0: candidate.Azimuth = value; break;
                case 1: candidate.Distance = Mathf.Max(5f, value); break;
                case 2: candidate.Height = value; break;
                case 3: candidate.TargetHeight = value; break;
                default: candidate.Fov = Mathf.Clamp(value, 20f, 100f); break;
            }
        }

        private static string Describe(Candidate candidate)
        {
            return "score=" + candidate.Score.ToString("F6") +
                " az=" + candidate.Azimuth.ToString("F3") +
                " dist=" + candidate.Distance.ToString("F3") +
                " height=" + candidate.Height.ToString("F3") +
                " targetHeight=" + candidate.TargetHeight.ToString("F3") +
                " fov=" + candidate.Fov.ToString("F3");
        }

        private static void Place(Camera camera, Vector3 focus, float azimuth,
            float distance, float height, float targetHeight, float fov)
        {
            var radians = azimuth * Mathf.Deg2Rad;
            var offset = new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians)) * distance;
            camera.transform.position = focus + offset + Vector3.up * height;
            camera.transform.rotation = Quaternion.LookRotation(
                focus + Vector3.up * targetHeight - camera.transform.position,
                Vector3.up);
            camera.fieldOfView = fov;
        }

        private static float[] LoadReference()
        {
            var full = new Texture2D(2, 2, TextureFormat.RGB24, false, false);
            full.LoadImage(File.ReadAllBytes(ReferencePath), false);
            if (full.width != Width * 4 || full.height != Height * 4)
                throw new InvalidOperationException("unexpected reference size");
            var source = full.GetPixels32();
            var result = new float[Width * Height];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var sum = 0f;
                    for (var yy = 0; yy < 4; yy++)
                    for (var xx = 0; xx < 4; xx++)
                    {
                        var p = source[(y * 4 + yy) * full.width + x * 4 + xx];
                        sum += (p.r + p.g + p.b) / (3f * 255f);
                    }
                    result[y * Width + x] = sum / 16f;
                }
            }
            UnityEngine.Object.DestroyImmediate(full);
            return result;
        }

        private static float[] Render(Camera camera)
        {
            var target = new RenderTexture(Width, Height, 24,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false, false);
            var oldTarget = camera.targetTexture;
            var oldActive = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                texture.Apply(false, false);
                var pixels = texture.GetPixels32();
                var result = new float[pixels.Length];
                for (var i = 0; i < pixels.Length; i++)
                    result[i] = (pixels[i].r + pixels[i].g + pixels[i].b) / (3f * 255f);
                return result;
            }
            finally
            {
                RenderTexture.active = oldActive;
                camera.targetTexture = oldTarget;
                UnityEngine.Object.DestroyImmediate(texture);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static float Score(float[] reference, float[] candidate)
        {
            // Geometry and the large terrain stains dominate normalized luma.
            // Ignore a thin edge where camera clipping is least stable.
            double ar = 0, ac = 0;
            var count = 0;
            for (var y = 3; y < Height - 3; y++)
            for (var x = 3; x < Width - 3; x++)
            {
                var i = y * Width + x;
                ar += reference[i];
                ac += candidate[i];
                count++;
            }
            ar /= count; ac /= count;
            double rr = 0, cc = 0, rc = 0;
            for (var y = 3; y < Height - 3; y++)
            for (var x = 3; x < Width - 3; x++)
            {
                var i = y * Width + x;
                var r = reference[i] - ar;
                var c = candidate[i] - ac;
                rr += r * r; cc += c * c; rc += r * c;
            }
            return (float)(rc / Math.Sqrt(Math.Max(rr * cc, 1e-12)));
        }

        private static void Add(List<Candidate> best, Candidate candidate)
        {
            var index = best.Count;
            for (var i = 0; i < best.Count; i++)
            {
                if (candidate.Score > best[i].Score) { index = i; break; }
            }
            best.Insert(index, candidate);
            if (best.Count > 20) best.RemoveAt(best.Count - 1);
        }
    }
}
