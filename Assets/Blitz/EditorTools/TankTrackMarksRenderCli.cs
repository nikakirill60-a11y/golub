using System;
using System.IO;
using System.Reflection;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.EditorTools
{
    /// <summary>GPU smoke for the production 11.20 tread mesh and shader.</summary>
    public static class TankTrackMarksRenderCli
    {
        private const int Width = 800;
        private const int Height = 600;
        private const string Output = "Logs/tank-treads-smoke.png";

        public static void Render()
        {
            GameObject floor = null;
            GameObject tankHost = null;
            GameObject cameraHost = null;
            GameObject lightHost = null;
            Material floorMaterial = null;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Directory.CreateDirectory("Logs");

                floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                floor.name = "TreadSmokeGround";
                floor.layer = BattleLayers.Static;
                floor.transform.localScale = new Vector3(6f, 1f, 6f);
                floorMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                floorMaterial.color = new Color(0.58f, 0.48f, 0.35f, 1f);
                floor.GetComponent<MeshRenderer>().sharedMaterial = floorMaterial;

                lightHost = new GameObject("TreadSmokeSun");
                var light = lightHost.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.7f;
                light.shadows = LightShadows.Soft;
                light.transform.rotation = Quaternion.Euler(50f, -35f, 0f);

                var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                tankHost = new GameObject("TreadSmokeTank", typeof(Rigidbody));
                tankHost.transform.position = new Vector3(0f, 0.6f, -12f);
                var tank = tankHost.AddComponent<BattleTank>();
                tank.Initialize(database.Get("ms-1"));
                var wheelHits = typeof(TankSuspension).GetField(
                    "_wheelHits", BindingFlags.NonPublic | BindingFlags.Instance);
                if (wheelHits == null) { throw new MissingFieldException("TankSuspension._wheelHits"); }

                for (var i = 0; i <= 120; i++)
                {
                    var z = -12f + i * 0.2f;
                    var x = Mathf.Sin(i * 0.085f) * 3.2f;
                    var nextX = Mathf.Sin((i + 1) * 0.085f) * 3.2f;
                    tankHost.transform.position = new Vector3(x, 0.6f, z);
                    var direction = new Vector3(nextX - x, 0f, 0.2f);
                    tankHost.transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
                    Physics.SyncTransforms();
                    wheelHits.SetValue(tank.Suspension, 1);
                    tank.TrackMarks.SimulateNow(Time.time);
                }
                if (tank.TrackMarks.SampleCount < 120 ||
                    tank.TrackMarks.TriangleCount < 100 ||
                    tank.TrackMarks.SampleCount > TankTrackMarks.MaximumSamplesPerSide * 2)
                {
                    throw new InvalidOperationException(
                        "Tread mesh budget failed: samples=" + tank.TrackMarks.SampleCount +
                        " triangles=" + tank.TrackMarks.TriangleCount);
                }
                if (tank.Model != null) { tank.Model.SetActive(false); }

                cameraHost = new GameObject("TreadSmokeCamera");
                var camera = cameraHost.AddComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = 17f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 50f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.magenta;
                camera.allowHDR = true;
                camera.allowMSAA = false;
                camera.transform.position = new Vector3(0f, 20f, 0f);
                camera.transform.LookAt(Vector3.zero, Vector3.forward);

                var target = new RenderTexture(
                    Width, Height, 24, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.sRGB);
                var image = new Texture2D(Width, Height, TextureFormat.RGB24, false, false);
                var previous = RenderTexture.active;
                try
                {
                    camera.targetTexture = target;
                    for (var i = 0; i < 4; i++) { camera.Render(); }
                    RenderTexture.active = target;
                    image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                    image.Apply();
                    var markedPixels = image.GetPixels32();
                    File.WriteAllBytes(Output, image.EncodeToPNG());

                    // Compare against the exact same scene without the tread
                    // mesh. A fixed "dark pixel" threshold is invalid here:
                    // lighting and colour-space conversion can make the real
                    // 11.20 textures lighter while they remain clearly visible.
                    var sampleCount = tank.TrackMarks.SampleCount;
                    var triangleCount = tank.TrackMarks.TriangleCount;
                    tank.TrackMarks.ClearTrail();
                    for (var i = 0; i < 4; i++) { camera.Render(); }
                    RenderTexture.active = target;
                    image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                    image.Apply();
                    var baselinePixels = image.GetPixels32();
                    var changedPixels = 0;
                    long absoluteDifference = 0;
                    for (var i = 0; i < markedPixels.Length; i++)
                    {
                        var marked = markedPixels[i];
                        var baseline = baselinePixels[i];
                        var difference =
                            Mathf.Abs(marked.r - baseline.r) +
                            Mathf.Abs(marked.g - baseline.g) +
                            Mathf.Abs(marked.b - baseline.b);
                        absoluteDifference += difference;
                        if (difference > 12) { changedPixels++; }
                    }
                    if (changedPixels < 250 || absoluteDifference < 10000)
                    {
                        throw new InvalidOperationException(
                            "Tread capture did not differ enough from its baseline: changed=" +
                            changedPixels + " absoluteDifference=" + absoluteDifference +
                            " output=" + Output);
                    }
                    Debug.Log("[Blitz] tank treads GPU smoke ok: samples=" +
                        sampleCount + " triangles=" + triangleCount +
                        " changedPixels=" + changedPixels +
                        " absoluteDifference=" + absoluteDifference +
                        " output=" + Output);
                }
                finally
                {
                    camera.targetTexture = null;
                    RenderTexture.active = previous;
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                    UnityEngine.Object.DestroyImmediate(image);
                }
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
            finally
            {
                if (tankHost != null) { UnityEngine.Object.DestroyImmediate(tankHost); }
                if (floor != null) { UnityEngine.Object.DestroyImmediate(floor); }
                if (cameraHost != null) { UnityEngine.Object.DestroyImmediate(cameraHost); }
                if (lightHost != null) { UnityEngine.Object.DestroyImmediate(lightHost); }
                if (floorMaterial != null) { UnityEngine.Object.DestroyImmediate(floorMaterial); }
            }
        }
    }
}
