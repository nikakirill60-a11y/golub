using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Renders every ported tank the way the sniper scope shows it and reads
    /// the armour overlay back as numbers.
    ///
    /// Separate frames per view make the overlay measurable instead of a matter
    /// of opinion: empty, tank, raw plates, outlined tank and the final sight.
    /// Raw plates diagnose authored collision shells; the outlined baseline
    /// isolates exactly what the player-visible armour pass adds.
    /// </summary>
    public static class ArmorSightRenderCli
    {
        public const int Width = 640;
        public const int Height = 480;

        private const string OutputDirectory = "Logs/armor-sight";

        /// <summary>A mid sniper step, so the tank is framed as the scope shows it.</summary>
        private const float SightFov = 8f;

        /// <summary>URP needs a few frames on a camera it has never seen.</summary>
        private const int WarmupFrames = 3;

        /// <summary>Uniform clear colour no tank texture and no overlay matches.</summary>
        private static readonly Color Background = new Color(0f, 0f, 90f / 255f, 1f);

        /// <summary>The share of overlay pixels allowed to miss the tank.</summary>
        private const float MaximumLeak = 0.06f;

        /// <summary>Views the audit frames each tank from, as (yaw, pitch).</summary>
        private static readonly Vector2[] Views =
        {
            new Vector2(35f, 14f),
            new Vector2(115f, 8f),
            new Vector2(215f, 20f),
        };

        private static readonly string[] ViewNames = { "front", "side", "rear" };

        public static void Render()
        {
            try
            {
                EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Directory.CreateDirectory(OutputDirectory);

                var sunHost = new GameObject("sun");
                var sun = sunHost.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.intensity = 1.1f;
                sun.shadows = LightShadows.None;
                sunHost.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
                RenderSettings.ambientMode =
                    UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.45f, 0.46f, 0.5f, 1f);
                RenderSettings.fog = false;
                DynamicGI.UpdateEnvironment();

                var camera = NewCamera();
                var database = TanksDatabase.Parse(
                    BlitzResources.LoadText(BlitzResources.TanksData));
                var wanted = Wanted();
                var report = new StringBuilder();
                report.Append("{\n  \"tanks\": [\n");

                var rendered = 0;
                var failed = 0;
                var first = true;
                for (var i = 0; i < database.Tanks.Count; i++)
                {
                    var card = database.Tanks[i];
                    if (wanted != null && !wanted.Contains(card.Name)) { continue; }

                    var row = RenderCard(camera, card);
                    rendered++;
                    if (row.Failed) { failed++; }

                    if (!first) { report.Append(",\n"); }
                    first = false;
                    report.Append(row.Json);
                }

                report.Append("\n  ],\n  \"rendered\": ").Append(rendered);
                report.Append(",\n  \"failed\": ").Append(failed).Append("\n}\n");
                File.WriteAllText(
                    Path.Combine(OutputDirectory, "report.json"), report.ToString());

                Debug.Log(
                    "[Blitz] armor sight render: rendered=" + rendered +
                    " failed=" + failed +
                    " out=" + Path.GetFullPath(OutputDirectory));
                EditorApplication.Exit(failed == 0 ? 0 : 1);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static HashSet<string> Wanted()
        {
            var raw = Environment.GetEnvironmentVariable("BLITZ_ARMOR_TANKS");
            if (string.IsNullOrEmpty(raw)) { return null; }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parts = raw.Split(',');
            for (var i = 0; i < parts.Length; i++)
            {
                var value = parts[i].Trim();
                if (value.Length > 0) { set.Add(value); }
            }

            return set.Count == 0 ? null : set;
        }

        private struct Row
        {
            public string Json;
            public bool Failed;
        }

        private static Row RenderCard(Camera camera, TankInfo card)
        {
            var id = TankModelAssets.ResourceId(card.Name);
            var tank = BattleTank.Spawn(
                null, "ArmorSight_" + id, card, null,
                new Bounds(Vector3.zero, new Vector3(2000f, 2000f, 2000f)));
            var row = new Row();
            try
            {
                var body = tank.GetComponent<Rigidbody>();
                if (body != null) { body.isKinematic = true; }

                var plates = tank.Plates;
                var shell = new ShellInfo
                {
                    Name = "audit",
                    Caliber = 1,
                    Penetration = 0f,
                    Type = ShellType.Ap,
                };

                var visible = VisibleRenderers(tank);
                var bounds = Envelope(visible);
                var json = new StringBuilder();
                json.Append("    { \"tank\": \"").Append(card.Name).Append("\"");
                json.Append(", \"id\": \"").Append(id).Append("\"");
                json.Append(", \"plates\": ").Append(plates.Count);
                json.Append(", \"views\": [");

                for (var view = 0; view < Views.Length; view++)
                {
                    Frame(camera, bounds, Views[view]);

                    Show(visible, false);
                    tank.PreviewArmor(false, null);
                    tank.SwitchOutline(false);
                    var empty = Capture(camera, null);

                    Show(visible, true);
                    var silhouette = Capture(
                        camera, FileName(id, view, "tank"));

                    // Preserve the raw collision shell as a diagnostic image,
                    // but judge the frame the player actually sees below.
                    Show(visible, false);
                    SetStencilComparison(
                        plates, UnityEngine.Rendering.CompareFunction.Always);
                    tank.PreviewArmor(true, shell);
                    var armour = Capture(camera, FileName(id, view, "armor"));
                    tank.PreviewArmor(false, null);
                    var rawMetric = Compare(empty, silhouette, armour);

                    SetStencilComparison(
                        plates, UnityEngine.Rendering.CompareFunction.Equal);
                    Show(visible, true);
                    tank.SwitchOutline(true);
                    var outlined = Capture(camera, null);
                    tank.PreviewArmor(true, shell);
                    var sight = Capture(camera, FileName(id, view, "sight"));
                    tank.PreviewArmor(false, null);
                    tank.SwitchOutline(false);

                    var metric = CompareOverlay(
                        empty, silhouette, outlined, sight);
                    var worst = rawMetric.Leak > MaximumLeak
                        ? DiagnoseWorstPlate(
                            camera, tank, plates, visible, shell, empty, silhouette)
                        : default(PlateMetric);
                    if (view > 0) { json.Append(", "); }
                    json.Append("{ \"view\": \"").Append(ViewNames[view]).Append("\"");
                    json.Append(", \"leak\": ").Append(Number(metric.Leak));
                    json.Append(", \"coverage\": ").Append(Number(metric.Coverage));
                    json.Append(", \"tankPixels\": ").Append(metric.TankPixels);
                    json.Append(", \"armorPixels\": ").Append(metric.ArmorPixels);
                    json.Append(", \"rawLeak\": ").Append(Number(rawMetric.Leak));
                    json.Append(", \"rawCoverage\": ")
                        .Append(Number(rawMetric.Coverage));
                    if (!string.IsNullOrEmpty(worst.Name))
                    {
                        json.Append(", \"worstPlate\": \"").Append(worst.Name).Append("\"");
                        json.Append(", \"worstPlateLeak\": ").Append(Number(worst.Metric.Leak));
                        json.Append(", \"worstPlateLeakPixels\": ")
                            .Append(worst.Metric.LeakPixels);
                        json.Append(", \"worstPlateArmorPixels\": ")
                            .Append(worst.Metric.ArmorPixels);
                    }
                    json.Append(" }");

                    if (metric.Leak > MaximumLeak)
                    {
                        row.Failed = true;
                    }
                }

                json.Append("], \"failed\": ")
                    .Append(row.Failed ? "true" : "false").Append(" }");
                row.Json = json.ToString();
                return row;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tank.gameObject);
            }
        }

        private static string FileName(string id, int view, string kind)
        {
            return id + "-" + ViewNames[view] + "-" + kind + ".png";
        }

        private static string Number(float value)
        {
            return value.ToString("F4", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The visible hull, turret, gun, tracks and wheels. The plate hosts
        /// carry the overlay, and the wreck effect carries particle renderers
        /// whose bounds are not the tank, so neither may reach the framing.
        /// </summary>
        private static List<Renderer> VisibleRenderers(BattleTank tank)
        {
            var list = new List<Renderer>();
            if (tank.Model == null) { return list; }

            var all = tank.Model.GetComponentsInChildren<MeshRenderer>(true);
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i].GetComponent<BattleArmorPlate>() != null) { continue; }
                if (!all[i].enabled) { continue; }
                list.Add(all[i]);
            }

            return list;
        }

        private static void Show(List<Renderer> renderers, bool on)
        {
            for (var i = 0; i < renderers.Count; i++)
            {
                renderers[i].enabled = on;
            }
        }

        private static Bounds Envelope(List<Renderer> renderers)
        {
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            var started = false;
            for (var i = 0; i < renderers.Count; i++)
            {
                if (!started)
                {
                    bounds = renderers[i].bounds;
                    started = true;
                    continue;
                }

                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }

        /// <summary>
        /// Points the sight at the tank from one authored angle and backs off
        /// until the whole envelope fits, so a Maus and an MS-1 are framed the
        /// same way and one pixel means the same share of the hull.
        /// </summary>
        private static void Frame(Camera camera, Bounds bounds, Vector2 view)
        {
            var radius = bounds.extents.magnitude;
            var vertical = Mathf.Tan(SightFov * 0.5f * Mathf.Deg2Rad);
            var horizontal = vertical * Width / (float)Height;
            var distance = radius / Mathf.Min(vertical, horizontal) * 1.15f;
            var direction = Quaternion.Euler(-view.y, view.x, 0f) * Vector3.forward;
            camera.transform.position = bounds.center - direction * distance;
            camera.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
            camera.nearClipPlane = Mathf.Max(0.05f, distance - radius * 3f);
            camera.farClipPlane = distance + radius * 4f;
        }

        private static Camera NewCamera()
        {
            var host = new GameObject("SightCamera");
            var camera = host.AddComponent<Camera>();
            camera.fieldOfView = SightFov;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Background;
            camera.allowHDR = false;
            return camera;
        }

        private static Color32[] Capture(Camera camera, string fileName)
        {
            var target = new RenderTexture(
                Width, Height, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            var texture = new Texture2D(
                Width, Height, TextureFormat.RGB24, false, false);
            var previousActive = RenderTexture.active;
            var previousTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = target;
                for (var i = 0; i < WarmupFrames; i++) { camera.Render(); }

                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
                texture.Apply(false, false);
                if (!string.IsNullOrEmpty(fileName))
                {
                    File.WriteAllBytes(
                        Path.Combine(OutputDirectory, fileName),
                        texture.EncodeToPNG());
                }

                return texture.GetPixels32();
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

        private struct Metric
        {
            public float Leak;
            public float Coverage;
            public int TankPixels;
            public int ArmorPixels;
            public int LeakPixels;
        }

        private struct PlateMetric
        {
            public string Name;
            public Metric Metric;
        }

        private static void SetStencilComparison(
            IReadOnlyList<BattleArmorPlate> plates,
            UnityEngine.Rendering.CompareFunction comparison)
        {
            for (var i = 0; i < plates.Count; i++)
            {
                var material = plates[i] != null ? plates[i].PreviewMaterial : null;
                if (material != null)
                {
                    material.SetFloat(ArmorPreviewShader.StencilComp, (float)comparison);
                }
            }
        }

        /// <summary>
        /// Finds the individual plate responsible for a raw collision-shell
        /// leak. Running this only for suspect views keeps the full-roster audit
        /// fast while making a bad mount or bad shell immediately actionable.
        /// </summary>
        private static PlateMetric DiagnoseWorstPlate(
            Camera camera,
            BattleTank tank,
            IReadOnlyList<BattleArmorPlate> plates,
            List<Renderer> visible,
            ShellInfo shell,
            Color32[] empty,
            Color32[] silhouette)
        {
            tank.PreviewArmor(false, null);
            Show(visible, false);
            SetStencilComparison(
                plates, UnityEngine.Rendering.CompareFunction.Always);
            var worst = new PlateMetric();
            for (var i = 0; i < plates.Count; i++)
            {
                var plate = plates[i];
                if (plate == null) { continue; }

                plate.ShowPreview(true, shell);
                var pixels = Capture(camera, null);
                plate.ShowPreview(false, null);
                var metric = Compare(empty, silhouette, pixels);
                if (string.IsNullOrEmpty(worst.Name) ||
                    metric.LeakPixels > worst.Metric.LeakPixels)
                {
                    worst.Name = plate.PlateName;
                    worst.Metric = metric;
                }
            }

            SetStencilComparison(
                plates, UnityEngine.Rendering.CompareFunction.Equal);
            return worst;
        }

        /// <summary>
        /// The overlay against the tank it is supposed to sit on. Both masks
        /// come out of a difference against the same empty frame, so a dark
        /// texture cannot be mistaken for background.
        /// </summary>
        private static Metric CompareOverlay(
            Color32[] empty,
            Color32[] silhouette,
            Color32[] outlined,
            Color32[] sight)
        {
            var tankPixels = 0;
            var armorPixels = 0;
            var both = 0;
            for (var i = 0; i < empty.Length; i++)
            {
                var isTank = Differs(empty[i], silhouette[i]);
                var isArmor = Differs(outlined[i], sight[i]);
                if (isTank) { tankPixels++; }
                if (isArmor) { armorPixels++; }
                if (isTank && isArmor) { both++; }
            }

            return BuildMetric(tankPixels, armorPixels, both);
        }

        private static Metric Compare(
            Color32[] empty, Color32[] silhouette, Color32[] armour)
        {
            var tankPixels = 0;
            var armorPixels = 0;
            var both = 0;
            for (var i = 0; i < empty.Length; i++)
            {
                var isTank = Differs(empty[i], silhouette[i]);
                var isArmor = Differs(empty[i], armour[i]);
                if (isTank) { tankPixels++; }
                if (isArmor) { armorPixels++; }
                if (isTank && isArmor) { both++; }
            }

            return BuildMetric(tankPixels, armorPixels, both);
        }

        private static Metric BuildMetric(int tankPixels, int armorPixels, int both)
        {
            var metric = new Metric();
            metric.TankPixels = tankPixels;
            metric.ArmorPixels = armorPixels;
            metric.LeakPixels = armorPixels - both;
            metric.Leak = armorPixels == 0
                ? 0f
                : metric.LeakPixels / (float)armorPixels;
            metric.Coverage = tankPixels == 0 ? 0f : both / (float)tankPixels;
            return metric;
        }

        private static bool Differs(Color32 a, Color32 b)
        {
            return Mathf.Abs(a.r - b.r) > 6 ||
                Mathf.Abs(a.g - b.g) > 6 ||
                Mathf.Abs(a.b - b.b) > 6;
        }
    }
}
