using System;
using System.Globalization;
using System.IO;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Save;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Renders the ported hangar off-screen at the resolution of the Godot
    /// reference render, so lighting, fog and smoke can be compared against
    /// the source numerically instead of through an Android round trip.
    /// </summary>
    public static class HangarRenderCli
    {
        /// <summary>Matches the reference render, which is half of 2392x1080.</summary>
        public const int Width = 1196;

        /// <summary>Matches the reference render, which is half of 2392x1080.</summary>
        public const int Height = 540;

        private const string OutputDirectory = "Logs/hangar-render";

        /// <summary>
        /// The source smoke lives eight seconds, so the first frame is taken
        /// once the volume is full. The second frame follows two seconds later,
        /// which is the interval the reference pair was captured at.
        /// </summary>
        private const float SettleSeconds = 8f;

        private const float FrameGapSeconds = 2f;

        /// <summary>
        /// URP fills in light, shadow and ambient data while rendering, so
        /// the first frames of a camera it has never seen are discarded.
        /// </summary>
        private const int WarmupFrames = 4;

        /// <summary>
        /// Fixed floor patch under the camera, in ReadPixels' bottom-left
        /// coordinates. The clean Godot reference measures 0.225 here. Feeding
        /// its top-down primary V to Unity unchanged measured 0.012 with 96%
        /// black pixels, so these margins catch the mirrored albedo without
        /// fitting the rest of the lighting.
        /// </summary>
        private const int FloorX = 500;
        private const int FloorY = 90;
        private const int FloorWidth = 200;
        private const int FloorHeight = 50;
        private const float MinimumFloorLuma = 0.15f;
        private const float MaximumFloorBlackShare = 0.10f;

        public static void Render()
        {
            try
            {
                EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);

                var card = TankCard();
                var world = HangarWorld.Create(
                    null, new SettingsData(), card, TankSave(card));
                if (world == null || world.HangarCamera == null)
                {
                    Debug.LogError("[Blitz] hangar render: world or camera missing");
                    EditorApplication.Exit(1);
                    return;
                }

                // Flat ambient is only picked up by the shaders after the
                // environment is pushed into the ambient probe.
                DynamicGI.UpdateEnvironment();

                Directory.CreateDirectory(OutputDirectory);
                var smoke = world.GetComponentsInChildren<ParticleSystem>(true);

                Advance(smoke, SettleSeconds, true);
                Capture(world.HangarCamera, "frame_a.png");
                Advance(smoke, FrameGapSeconds, false);
                Capture(world.HangarCamera, "frame_b.png");

                // A frame without the emitters isolates how much the additive
                // smoke actually adds, which is the only way to compare it
                // against the source instead of guessing from frame motion.
                for (var i = 0; i < smoke.Length; i++)
                {
                    smoke[i].Clear(true);
                    smoke[i].gameObject.SetActive(false);
                }

                var clean = Capture(world.HangarCamera, "frame_clean.png");

                // BLITZ_HANGAR_CAMOUFLAGE=<id>: the same stand in a camouflage.
                var camouflage = Environment.GetEnvironmentVariable("BLITZ_HANGAR_CAMOUFLAGE");
                if (!string.IsNullOrEmpty(camouflage))
                {
                    var painted = world.ShowCamouflage(camouflage);
                    if (painted == 0)
                    {
                        throw new InvalidOperationException(
                            "Camouflage " + camouflage + " paints nothing on " + card.Name);
                    }

                    Capture(world.HangarCamera, "camouflage.png");
                    Debug.Log("[Blitz] hangar camouflage: " + camouflage + " materials=" + painted);
                    world.ShowCamouflage(string.Empty);
                }

                var armorMode = Environment.GetEnvironmentVariable("BLITZ_HANGAR_ARMOR");
                if (!string.IsNullOrEmpty(armorMode))
                {
                    var viewer = HangarArmorViewer.Create(world);
                    if (viewer == null || !viewer.Open(card, TankSave(card)))
                    {
                        throw new InvalidOperationException("Armor viewer failed to open");
                    }
                    viewer.SetMode(armorMode.Equals("sight", StringComparison.OrdinalIgnoreCase)
                        ? HangarArmorViewer.ViewMode.Sight
                        : armorMode.Equals("test", StringComparison.OrdinalIgnoreCase)
                            ? HangarArmorViewer.ViewMode.Penetration
                            : HangarArmorViewer.ViewMode.Fill);
                    Capture(world.HangarCamera, "armor_" + armorMode.ToLowerInvariant() + ".png");

                    float worstLeak;
                    var audit = viewer.Audit(out worstLeak);
                    for (var i = 0; i < audit.Count; i++)
                    {
                        Debug.Log("[Blitz] armour align: " + audit[i]);
                    }

                    Debug.Log("[Blitz] armour align worst leak=" +
                        worstLeak.ToString("F3", CultureInfo.InvariantCulture));
                }

                Debug.Log(
                    "[Blitz] hangar render ok: " + Width + "x" + Height +
                    " smoke=" + smoke.Length +
                    " ambient=" + RenderSettings.ambientLight +
                    " floor=luma:" + clean.Luma.ToString(
                        "F4", CultureInfo.InvariantCulture) +
                    ",black:" + clean.BlackShare.ToString(
                        "F4", CultureInfo.InvariantCulture) +
                    " out=" + Path.GetFullPath(OutputDirectory));

                if (clean.Luma < MinimumFloorLuma ||
                    clean.BlackShare > MaximumFloorBlackShare)
                {
                    Debug.LogError(
                        "[Blitz] hangar render: the floor albedo is black or mirrored");
                    EditorApplication.Exit(1);
                    return;
                }

                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// Renders a grid of light, ambient and fog multipliers over the source
        /// values so the realtime rig can be matched against the Godot
        /// reference render. Godot bakes the thirteen hangar lights into
        /// hangar_low.lmbake and stops rendering them at runtime, so the source
        /// frame is lit by a lightmap that already carries occlusion and
        /// bounce; the port has to light the hangar live and needs a measured
        /// factor to land on the same exposure.
        /// </summary>
        public static void Calibrate()
        {
            try
            {
                EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);

                var card = TankCard();
                var world = HangarWorld.Create(
                    null, new SettingsData(), card, TankSave(card));
                if (world == null || world.HangarCamera == null)
                {
                    Debug.LogError("[Blitz] hangar calibration: world or camera missing");
                    EditorApplication.Exit(1);
                    return;
                }

                DynamicGI.UpdateEnvironment();
                Directory.CreateDirectory(OutputDirectory);

                // The sweep scores how the ported lighting reads against the
                // source frames, so the smoke stays off: its additive quads
                // would pay into every band being compared.
                var emitters = world.GetComponentsInChildren<ParticleSystem>(true);
                for (var i = 0; i < emitters.Length; i++)
                {
                    emitters[i].Clear(true);
                    emitters[i].gameObject.SetActive(false);
                }

                var lights = world.GetComponentsInChildren<Light>(true);
                var sourceIntensity = new float[lights.Length];
                for (var i = 0; i < lights.Length; i++)
                {
                    sourceIntensity[i] = lights[i].intensity;
                }
                var sourceAmbient = RenderSettings.ambientLight;
                var sourceFog = RenderSettings.fogColor;

                var lightScales = ScalesFrom(
                    "BLITZ_LIGHT_SCALES", new[] { 0.15f, 0.3f, 0.5f, 0.75f, 1f });
                var ambientScales = ScalesFrom(
                    "BLITZ_AMBIENT_SCALES", new[] { 0.15f, 0.35f, 0.6f, 1f });
                var fogScales = ScalesFrom("BLITZ_FOG_SCALES", new[] { 0.5f, 1f });
                var frames = 0;
                for (var l = 0; l < lightScales.Length; l++)
                {
                    for (var i = 0; i < lights.Length; i++)
                    {
                        lights[i].intensity = sourceIntensity[i] * lightScales[l];
                    }
                    for (var a = 0; a < ambientScales.Length; a++)
                    {
                        RenderSettings.ambientLight = sourceAmbient * ambientScales[a];
                        DynamicGI.UpdateEnvironment();
                        for (var f = 0; f < fogScales.Length; f++)
                        {
                            RenderSettings.fogColor = sourceFog * fogScales[f];
                            Capture(world.HangarCamera, string.Format(
                                CultureInfo.InvariantCulture,
                                "calib_l{0:0.00}_a{1:0.00}_f{2:0.00}.png",
                                lightScales[l], ambientScales[a], fogScales[f]));
                            frames++;
                        }
                    }
                }

                Debug.Log("[Blitz] hangar calibration ok: frames=" + frames);
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// Every card in the tree has its own pair of sheets, so checking one
        /// of them has to be possible without editing this file. An unset or
        /// unknown name keeps the MS-1, which is the hangar default.
        /// </summary>
        private static TankInfo TankCard()
        {
            var name = Environment.GetEnvironmentVariable("BLITZ_HANGAR_TANK");
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            BlitzLegacy.Runtime.Mods.ModRuntime.Apply(database);
            if (string.IsNullOrEmpty(name))
            {
                return database.Get(TankModelAssets.DefaultTankName);
            }

            var card = database.Get(name.Trim());
            if (card == null)
            {
                Debug.LogWarning("[Blitz] unknown hangar tank: " + name);
                return database.Get(TankModelAssets.DefaultTankName);
            }

            return card;
        }

        /// <summary>
        /// The loadout of that card in the player's own save, so the frame
        /// carries the researched turret and gun rather than the stock pair.
        /// A machine without a save simply renders the stock modules.
        /// </summary>
        private static TankSaveData TankSave(TankInfo card)
        {
            if (card == null) { return null; }

            try
            {
                var book = new SaveService(SecureSaves.Saves).LoadBook();
                return TankLoadoutMeshes.SaveOf(book.GetCurrentAccount(), card.Name);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Blitz] hangar render save: " + exception.Message);
                return null;
            }
        }

        /// <summary>
        /// Lets a sweep narrow the grid from the command line, so refining the
        /// factor does not need a code change.
        /// </summary>
        private static float[] ScalesFrom(string variable, float[] fallback)
        {
            var raw = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(raw))
            {
                return fallback;
            }

            var parts = raw.Split(',');
            var scales = new float[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                scales[i] = float.Parse(
                    parts[i].Trim(), CultureInfo.InvariantCulture);
            }

            return scales;
        }

        private static void Advance(
            ParticleSystem[] systems, float seconds, bool restart)
        {
            for (var i = 0; i < systems.Length; i++)
            {
                systems[i].Simulate(seconds, true, restart);
            }
        }

        private static FloorSample Capture(Camera camera, string fileName)
        {
            // The reference frames are sRGB encoded screenshots, so the target
            // has to be written through the same conversion.
            var target = new RenderTexture(
                Width, Height, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false, false);
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
                return MeasureFloor(texture);
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

        private static FloorSample MeasureFloor(Texture2D texture)
        {
            var pixels = texture.GetPixels32();
            var luma = 0d;
            var black = 0;
            var samples = 0;
            for (var y = FloorY; y < FloorY + FloorHeight; y++)
            {
                for (var x = FloorX; x < FloorX + FloorWidth; x++)
                {
                    var pixel = pixels[y * Width + x];
                    var value = (0.2126d * pixel.r + 0.7152d * pixel.g +
                        0.0722d * pixel.b) / 255d;
                    luma += value;
                    if (value < 0.04d)
                    {
                        black++;
                    }
                    samples++;
                }
            }

            return new FloorSample
            {
                Luma = samples == 0 ? 0f : (float)(luma / samples),
                BlackShare = samples == 0 ? 1f : black / (float)samples,
            };
        }

        private struct FloorSample
        {
            public float Luma;
            public float BlackShare;
        }
    }
}
