using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Renders the battle world off-screen so lighting, sky and prop
    /// placement can be read as numbers here instead of through an Android
    /// round trip. The first device pass came back black with the houses
    /// buried in the hills, and both causes were numeric: the map sun was
    /// read from a transposed basis and pointed up into the sky, and the
    /// props were exported without the transform of the node they hang under.
    /// A frame rendered on this machine catches either in seconds.
    /// </summary>
    public static class BattleRenderCli
    {
        /// <summary>Matches the hangar render, which is half of 2392x1080.</summary>
        public const int Width = 1196;

        /// <inheritdoc cref="Width"/>
        public const int Height = 540;

        private const string OutputDirectory = "Logs/battle-render";

        /// <summary>
        /// Every map in the roster needs the same frames before it ships, so
        /// BLITZ_BATTLE_MAP points this run at one that is not the default.
        /// </summary>
        private static string RequestedMapId()
        {
            var raw = Environment.GetEnvironmentVariable("BLITZ_BATTLE_MAP");
            return string.IsNullOrEmpty(raw) ? BattleWorld.DefaultMapId : raw.Trim();
        }

        /// <summary>
        /// URP fills in light, shadow and ambient data while rendering, so the
        /// first frames of a camera it has never seen are discarded.
        /// </summary>
        private const int WarmupFrames = 4;

        /// <summary>
        /// A map lit by nothing but the port's flat ambient reads far below
        /// this, which is exactly what the device reported.
        /// </summary>
        private const float BlackFrameLuma = 0.02f;

        /// <summary>
        /// The old colour-map-only material measured 0.0013 here. Repeating
        /// the source detail texture 48 times raises the close ground to 0.026,
        /// while the overview remains correctly mip-filtered at a distance.
        /// </summary>
        private const float MinimumSpawnGroundDetail = 0.008f;

        /// <summary>
        /// The overview must change when the foliage layer is removed. This
        /// catches a valid object table paired with a stripped/broken shader,
        /// the exact failure mode where 179 roots exist but the map looks the same.
        /// </summary>
        private const float MinimumVegetationMeanDelta = 0.002f;
        private const float MinimumVegetationChangedShare = 0.02f;

        /// <summary>A Rudniki tree; foliage this tall keeps the full 20-metre framing.</summary>
        private const float TallFoliageHeight = 8f;

        /// <summary>
        /// The mis-oriented six-sided conversion exposed a tall cyan strip at
        /// the edge of the real mobile spawn view. The source's five overlapping
        /// planes leave no continuous blue wall there.
        /// </summary>
        private const float MaximumSpawnCyanEdgeShare = 0.01f;

        /// <summary>
        /// The white background of the first Rudniki device pass slipped past
        /// every gate here, because both existing frames look down at close
        /// ground. A level shot along the spawn forward keeps the distant
        /// static_bg ridges in view, where Unity's built-in linear fog used to
        /// wash them out to a flat white sheet. Nothing authored in the source
        /// map is that bright and that grey at once, so the share of near-white
        /// low-saturation pixels below the sky band is what fails the gate.
        /// </summary>
        private const float MaximumHorizonWhiteShare = 0.05f;
        private const double HorizonWhiteLuma = 0.82d;
        private const int HorizonWhiteChroma = 16;

        /// <summary>A square top-down capture maps one pixel grid to the terrain UVs.</summary>
        private const int TerrainCaptureSize = 512;

        /// <summary>
        /// Turning every object caster off must visibly alter the fixed mine
        /// shot; otherwise the renderer flags exist but no shadow reaches a frame.
        /// </summary>
        private const float MinimumShadowMeanDelta = 0.001f;
        private const float MinimumShadowChangedShare = 0.01f;

        /// <summary>
        /// The top-down map has to keep the broad luminance variance that
        /// the source colour map carries. A Unity-only macro normalisation
        /// used to divide it out and measured 0.09 here, where the authored
        /// map reads about 0.17, which is what turned shade into blotches.
        /// </summary>
        private const float MinimumAuthoredMacroDeviation = 0.10f;

        public static void Render()
        {
            try
            {
                EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);

                var world = BattleWorld.Create(null, RequestedMapId());
                if (world == null || world.PlayerSpawn == null)
                {
                    Debug.LogError("[Blitz] battle render: the world did not build");
                    EditorApplication.Exit(1);
                    return;
                }

                var vegetationShader = Shader.Find(BattleObjects.VegetationShaderName);
                if (vegetationShader == null || !vegetationShader.isSupported ||
                    ShaderUtil.ShaderHasError(vegetationShader))
                {
                    Debug.LogError("[Blitz] battle render: vegetation shader is invalid");
                    EditorApplication.Exit(1);
                    return;
                }

                // Flat ambient and the sky only reach the shaders once the
                // environment is pushed into the ambient probe.
                DynamicGI.UpdateEnvironment();
                Directory.CreateDirectory(OutputDirectory);
                LogEnvironment(world);

                var camera = NewCamera();
                if (CaptureRequestedViews(camera))
                {
                    EditorApplication.Exit(0);
                    return;
                }

                var spawn = world.PlayerSpawn;
                camera.transform.position =
                    spawn.position - spawn.forward * 9f + Vector3.up * 4f;
                camera.transform.rotation = Quaternion.LookRotation(
                    spawn.forward + Vector3.down * 0.18f, Vector3.up);
                var atSpawn = Capture(camera, "spawn.png");
                var gameplaySpawn = CaptureGameplaySpawn(world);

                // Half the map in one frame: a prop sunk into a hill is
                // invisible from a hull and obvious from up here.
                camera.transform.position =
                    world.PlayArea.center + new Vector3(0f, 210f, -250f);
                camera.transform.rotation = Quaternion.LookRotation(
                    new Vector3(0f, -0.62f, 0.78f), Vector3.up);
                var overview = Capture(camera, "overview.png");

                // Level with the spawn: the ridges the fog bug bleached are
                // only in frame when the camera is not pitched at the ground.
                camera.transform.position =
                    spawn.position + Vector3.up * 6f;
                camera.transform.rotation = Quaternion.LookRotation(
                    new Vector3(spawn.forward.x, 0f, spawn.forward.z).normalized,
                    Vector3.up);
                var horizon = Capture(camera, "horizon.png");

                // A close, fixed source tree and its neighbouring bushes make
                // the foliage pass measurable rather than a handful of pixels
                // in the 600-metre overview.
                float focusHeight;
                var focus = FoliageFocus(world, out focusHeight);
                if (focus == null)
                {
                    Debug.LogError("[Blitz] battle render: vegetation focus is missing");
                    EditorApplication.Exit(1);
                    return;
                }

                // The 20-metre framing was written around Rudniki's tall
                // trees. Karieri's foliage is cacti and knee-high bushes, so
                // a short cluster is framed proportionally closer.
                var framing = Mathf.Clamp(focusHeight / TallFoliageHeight, 0.5f, 1f);
                camera.transform.position =
                    focus.Value + new Vector3(-14f, 5f, -14f) * framing;
                camera.transform.rotation = Quaternion.LookRotation(
                    focus.Value + Vector3.up * 5f * framing - camera.transform.position,
                    Vector3.up);
                var vegetation = Capture(camera, "vegetation.png", true);
                var fullMask = camera.cullingMask;
                camera.cullingMask = fullMask & ~(1 << BattleLayers.Foliage);
                var withoutVegetation = Capture(
                    camera, "vegetation-no-foliage.png", true);
                camera.cullingMask = fullMask;
                var vegetationDelta = Difference(vegetation, withoutVegetation);
                var shadowDelta = CaptureShadowEvidence(world, camera);
                var terrainMetric = CaptureTerrainMetric(world, camera);

                Debug.Log(
                    "[Blitz] battle render ok: " + Width + "x" + Height +
                    " spawn=" + Describe(atSpawn) +
                    " gameplaySpawn=" + Describe(gameplaySpawn) +
                    " overview=" + Describe(overview) +
                    " horizon=" + Describe(horizon) +
                    " vegetation=mean:" + vegetationDelta.Mean.ToString("F4") +
                        ",changed:" + vegetationDelta.ChangedShare.ToString("F4") +
                    " shadow=mean:" + shadowDelta.Mean.ToString("F4") +
                        ",changed:" + shadowDelta.ChangedShare.ToString("F4") +
                    " terrain=" + Describe(terrainMetric) +
                    " out=" + Path.GetFullPath(OutputDirectory));

                if (atSpawn.Luma < BlackFrameLuma || overview.Luma < BlackFrameLuma)
                {
                    Debug.LogError(
                        "[Blitz] battle render: the frame is black, nothing lights the map");
                    EditorApplication.Exit(1);
                    return;
                }

                // A binary client scene draws the client's own dome, water,
                // shadow hulls and TileMaskSpecular landscape. Mirage's dome is
                // genuinely cyan at the horizon, only its stones carry shadow
                // volumes (buildings are baked into the colour map) and its
                // sand has less macro variance than Rudniki, so the three
                // gates written for the Godot conversions do not apply there.
                var clientScene = world.Map.ClientScene;
                if (clientScene)
                {
                    Debug.Log(
                        "[Blitz] battle render: client scene, skipping the sky wall, " +
                        "object shadow and terrain macro gates");
                }

                if (!clientScene && gameplaySpawn.CyanEdgeShare > MaximumSpawnCyanEdgeShare)
                {
                    Debug.LogError(
                        "[Blitz] battle render: a cyan sky wall crosses the spawn edge; share=" +
                        gameplaySpawn.CyanEdgeShare.ToString("F4"));
                    EditorApplication.Exit(1);
                    return;
                }

                // A client dome draws its authored clouds right down to the
                // horizon line, which a level camera puts at the frame middle;
                // Karieri's overcast SkyOldDeepHarizon is near-white there. Only
                // the half below that line is ground and fogged ridges.
                var horizonWhite = clientScene
                    ? horizon.WhiteShareBelowHorizon
                    : horizon.WhiteShare;
                if (horizonWhite > MaximumHorizonWhiteShare)
                {
                    Debug.LogError(
                        "[Blitz] battle render: the horizon is washed out white; share=" +
                        horizonWhite.ToString("F4"));
                    EditorApplication.Exit(1);
                    return;
                }

                if (atSpawn.GroundDetail < MinimumSpawnGroundDetail)
                {
                    Debug.LogError(
                        "[Blitz] battle render: close terrain is smooth; detail=" +
                        atSpawn.GroundDetail.ToString("F4"));
                    EditorApplication.Exit(1);
                    return;
                }

                if (vegetationDelta.Mean < MinimumVegetationMeanDelta ||
                    vegetationDelta.ChangedShare < MinimumVegetationChangedShare)
                {
                    Debug.LogError(
                        "[Blitz] battle render: vegetation does not visibly change the map");
                    EditorApplication.Exit(1);
                    return;
                }

                // A Godot-ported scene casts no realtime shadow at all: its
                // sun is static-baked with shadow_enabled = false, and the
                // object shadows live in the LightmapGI slices instead.
                var bakedScene = world.Map.GodotScene;
                if (bakedScene)
                {
                    Debug.Log("[Blitz] battle render: Godot scene, shadows are baked");
                }

                if (!clientScene && !bakedScene && (shadowDelta.Mean < MinimumShadowMeanDelta ||
                    shadowDelta.ChangedShare < MinimumShadowChangedShare))
                {
                    Debug.LogError(
                        "[Blitz] battle render: object casters produce no visible shadow");
                    EditorApplication.Exit(1);
                    return;
                }

                if (!clientScene && terrainMetric.MacroDeviation < MinimumAuthoredMacroDeviation)
                {
                    Debug.LogError(
                        "[Blitz] battle render: terrain shading is flattened; macro=" +
                        terrainMetric.MacroDeviation.ToString("F4"));
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
        /// The thickest patch of foliage in the built world.
        ///
        /// Rudniki names its source tree `tree_02_01`, so the gate used to
        /// look that node up by name and every client map - which ships its
        /// foliage inside the scene OBJ under names such as
        /// `entity_803_tree_02_sc2` - failed the run after the frames were
        /// already written. The focus is now whichever foliage renderer has
        /// the most foliage around it, which is the same tree on Rudniki.
        /// </summary>
        private static Vector3? FoliageFocus(BattleWorld world, out float height)
        {
            height = TallFoliageHeight;
            if (world.Objects == null || world.Objects.Root == null)
            {
                return null;
            }

            var named = world.Objects.Root.transform.Find("tree_02_01");
            if (named != null)
            {
                return named.position;
            }

            var renderers = world.Objects.Root.GetComponentsInChildren<MeshRenderer>(true);
            var foliage = new List<Vector3>();
            var heights = new List<float>();
            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].gameObject.layer == BattleLayers.Foliage)
                {
                    // A client map bakes its world positions into the mesh, so
                    // the node transform sits at the origin and only the
                    // renderer bounds say where the tree actually stands.
                    foliage.Add(renderers[i].bounds.center);
                    heights.Add(renderers[i].bounds.size.y);
                }
            }

            Vector3? best = null;
            var bestCount = 0;
            for (var i = 0; i < foliage.Count; i++)
            {
                var count = 0;
                var position = foliage[i];
                for (var j = 0; j < foliage.Count; j++)
                {
                    if ((foliage[j] - position).sqrMagnitude < 20f * 20f)
                    {
                        count++;
                    }
                }

                if (count > bestCount)
                {
                    bestCount = count;
                    best = position;
                    height = heights[i];
                }
            }

            return best;
        }

        /// <summary>
        /// The object the shadow regression frames are built around. Rudniki
        /// authors `rudnik_01`; a client map has no such landmark, so the
        /// tallest solid prop near the middle of the play area stands in - it
        /// casts the long ground shadow the frames are meant to show.
        /// </summary>
        private static Vector3? ShadowFocus(BattleWorld world)
        {
            if (world.Objects == null || world.Objects.Root == null)
            {
                return null;
            }

            var named = world.Objects.Root.transform.Find("rudnik_01");
            if (named != null)
            {
                return named.position;
            }

            var centre = world.Landscape == null
                ? Vector3.zero
                : world.Landscape.GetComponent<MeshRenderer>().bounds.center;
            var renderers = world.Objects.Root.GetComponentsInChildren<MeshRenderer>(true);
            Vector3? best = null;
            var bestHeight = 0f;
            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].gameObject.layer == BattleLayers.Foliage)
                {
                    continue;
                }

                var bounds = renderers[i].bounds;
                var flat = bounds.center - centre;
                flat.y = 0f;
                // Too far out and the frame looks at the map border instead of
                // the ground; too tall and the shadow leaves the frame.
                if (flat.magnitude > 150f || bounds.size.y > 20f)
                {
                    continue;
                }

                if (bounds.size.y > bestHeight)
                {
                    bestHeight = bounds.size.y;
                    // The mesh of a client map carries its own world
                    // position, so use the bounds rather than the transform.
                    best = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                }
            }

            return bestHeight >= 2f ? best : null;
        }

        /// <summary>
        /// Both bugs the device pass found were invisible in a screenshot and
        /// obvious in these numbers, so they are printed before the frames.
        /// </summary>
        private static void LogEnvironment(BattleWorld world)
        {
            var sun = world.Sun;
            var sky = RenderSettings.skybox;
            Debug.Log(
                "[Blitz] battle render environment:" +
                " sun=" + (sun == null
                    ? "none"
                    : sun.transform.forward.ToString("F3") +
                        " intensity=" + sun.intensity.ToString("F2")) +
                " ambient=" + RenderSettings.ambientLight.ToString("F3") +
                " sky=" + (sky == null
                    ? "none"
                    : sky.shader.name + " supported=" + sky.shader.isSupported) +
                " fog=" + RenderSettings.fog +
                " objects=" + (world.Objects == null ? 0 : world.Objects.InstanceCount));
        }

        /// <summary>What one rendered frame measures out to.</summary>
        private struct Frame
        {
            public int Width;
            public int Height;
            public float Luma;
            public float SkyLuma;
            public float BlackShare;
            public float GroundDetail;
            public float CyanEdgeShare;
            public float WhiteShare;
            public float WhiteShareBelowHorizon;
            public Color32[] Pixels;
        }

        private struct FrameDelta
        {
            public float Mean;
            public float ChangedShare;
        }

        private struct TerrainMetric
        {
            public float MacroDeviation;
            public float LowDecile;
        }

        private static string Describe(Frame frame)
        {
            return "luma=" + frame.Luma.ToString("F3") +
                ",sky=" + frame.SkyLuma.ToString("F3") +
                ",black=" + frame.BlackShare.ToString("F3") +
                ",detail=" + frame.GroundDetail.ToString("F4") +
                ",cyanEdge=" + frame.CyanEdgeShare.ToString("F4") +
                ",white=" + frame.WhiteShare.ToString("F4") +
                ",whiteBelowHorizon=" + frame.WhiteShareBelowHorizon.ToString("F4");
        }

        private static string Describe(TerrainMetric metric)
        {
            return "macro:" + metric.MacroDeviation.ToString("F4") +
                ",low10:" + metric.LowDecile.ToString("F4");
        }

        private static Frame CaptureGameplaySpawn(BattleWorld world)
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(BattleSession.DefaultTankId);
            if (card == null)
            {
                throw new InvalidOperationException("the default battle tank is missing");
            }

            var tank = BattleTank.Spawn(
                null, "RenderSpawnTank", card, world.PlayerSpawn, world.PlayArea, 0);
            var rig = BattleCamera.Create(tank, true);
            try
            {
                rig.Follow(0f);
                return Capture(rig.Active, "gameplay-spawn.png");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rig.gameObject);
                UnityEngine.Object.DestroyImmediate(tank.gameObject);
            }
        }

        /// <summary>
        /// BLITZ_RENDER_VIEWS="name:px,py,pz,tx,ty,tz;..." renders only these
        /// frames, so a reviewer can look at one district of a new map
        /// without the regression gates tuned for the existing roster.
        /// </summary>
        private static bool CaptureRequestedViews(Camera camera)
        {
            var raw = Environment.GetEnvironmentVariable("BLITZ_RENDER_VIEWS");
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            // Requested views are compared pixel for pixel with Godot
            // captures (_ref_capture/map_capture.gd), which render at
            // scaling_3d_scale 1 without MSAA. The asset's mobile 0.8 scale
            // halved the measured detail of every frame. The change is never
            // marked dirty, so the asset on disk keeps its own values.
            var pipeline = GraphicsSettings.currentRenderPipeline
                as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            if (pipeline != null)
            {
                pipeline.renderScale = 1f;
                pipeline.msaaSampleCount = 1;
            }

            var views = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < views.Length; i++)
            {
                var parts = views[i].Split(':');
                var values = parts[parts.Length - 1].Split(',');
                var numbers = new float[6];
                for (var v = 0; v < numbers.Length; v++)
                {
                    numbers[v] = float.Parse(
                        values[v], System.Globalization.CultureInfo.InvariantCulture);
                }

                var position = new Vector3(numbers[0], numbers[1], numbers[2]);
                var target = new Vector3(numbers[3], numbers[4], numbers[5]);
                camera.transform.position = position;
                camera.transform.rotation = Quaternion.LookRotation(target - position, Vector3.up);
                var name = parts.Length > 1 ? parts[0] : "view-" + i;
                var frame = Capture(camera, name + ".png");
                Debug.Log("[Blitz] battle render view " + name + ": " + Describe(frame));
            }

            return true;
        }

        private static Camera NewCamera()
        {
            var go = new GameObject("RenderCamera");
            var camera = go.AddComponent<Camera>();
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.3f;
            // The map is 600 metres across and the sky sits beyond it.
            camera.farClipPlane = CameraTuning.Far;
            camera.clearFlags = CameraClearFlags.Skybox;
            return camera;
        }

        private static FrameDelta CaptureShadowEvidence(
            BattleWorld world, Camera camera)
        {
            var focus = ShadowFocus(world);
            if (focus == null)
            {
                throw new InvalidOperationException("the map has no shadow caster to focus on");
            }

            var direction = Vector3.ProjectOnPlane(
                world.Sun.transform.forward, Vector3.up).normalized;
            var side = Vector3.Cross(Vector3.up, direction).normalized;
            // Frame the long ground shadow itself rather than spending most of
            // the regression frame on the mine and the distant horizon.
            var target = focus.Value + direction * 11f + Vector3.up * 1.5f;
            camera.orthographic = false;
            camera.fieldOfView = 36f;
            camera.transform.position =
                focus.Value - direction * 12f + side * 11f + Vector3.up * 10f;
            camera.transform.rotation = Quaternion.LookRotation(
                target - camera.transform.position, Vector3.up);

            var fullMask = camera.cullingMask;
            camera.cullingMask = fullMask & ~(1 << BattleLayers.Foliage);
            var renderers = world.Objects.Root.GetComponentsInChildren<Renderer>();
            var modes = new ShadowCastingMode[renderers.Length];
            try
            {
                var withShadows = Capture(camera, "shadow.png", true);
                for (var i = 0; i < renderers.Length; i++)
                {
                    modes[i] = renderers[i].shadowCastingMode;
                    renderers[i].shadowCastingMode = ShadowCastingMode.Off;
                }

                var withoutCasters = Capture(
                    camera, "shadow-no-casters.png", true);
                return Difference(withShadows, withoutCasters);
            }
            finally
            {
                for (var i = 0; i < renderers.Length; i++)
                {
                    renderers[i].shadowCastingMode = modes[i];
                }

                camera.cullingMask = fullMask;
            }
        }

        /// <summary>
        /// A fogless, objectless, top-down frame of nothing but the ground,
        /// so the terrain shading can be compared against the source colour
        /// map as numbers instead of through a device screenshot.
        /// </summary>
        private static TerrainMetric CaptureTerrainMetric(
            BattleWorld world, Camera camera)
        {
            var landscapeRenderer = world.Landscape.GetComponent<MeshRenderer>();
            var oldFogVector = Shader.GetGlobalVector("_BlitzFogColour");
            var noFogVector = oldFogVector;
            noFogVector.w = 0f;

            var objectsActive = world.Objects.Root.activeSelf;
            var oldMask = camera.cullingMask;
            var oldClearFlags = camera.clearFlags;
            var oldBackground = camera.backgroundColor;
            var oldOrthographic = camera.orthographic;
            var oldOrthographicSize = camera.orthographicSize;
            var oldAspect = camera.aspect;
            var oldPosition = camera.transform.position;
            var oldRotation = camera.transform.rotation;

            Frame authored;
            try
            {
                world.Objects.Root.SetActive(false);
                Shader.SetGlobalVector("_BlitzFogColour", noFogVector);
                camera.cullingMask = 1 << world.Landscape.layer;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.orthographic = true;
                camera.aspect = 1f;

                var bounds = landscapeRenderer.bounds;
                camera.orthographicSize = Mathf.Max(bounds.size.x, bounds.size.z) * 0.5f;
                camera.transform.position =
                    new Vector3(bounds.center.x, bounds.max.y + 700f, bounds.center.z);
                camera.transform.rotation = Quaternion.LookRotation(
                    Vector3.down, Vector3.forward);

                authored = Capture(
                    camera,
                    "terrain-authored.png",
                    true,
                    TerrainCaptureSize,
                    TerrainCaptureSize);
            }
            finally
            {
                Shader.SetGlobalVector("_BlitzFogColour", oldFogVector);
                world.Objects.Root.SetActive(objectsActive);
                camera.cullingMask = oldMask;
                camera.clearFlags = oldClearFlags;
                camera.backgroundColor = oldBackground;
                camera.orthographic = oldOrthographic;
                camera.orthographicSize = oldOrthographicSize;
                camera.aspect = oldAspect;
                camera.transform.position = oldPosition;
                camera.transform.rotation = oldRotation;
            }

            return MeasureTerrain(authored);
        }

        private static TerrainMetric MeasureTerrain(Frame frame)
        {
            if (frame.Pixels == null || frame.Width != frame.Height)
            {
                throw new InvalidOperationException("terrain capture has no square pixels");
            }

            // Ignore the outer half-cell where exact terrain bounds meet the
            // clear colour, then compare 24x24 low-frequency cell means. Sand
            // grain is far smaller than a cell and cannot masquerade as a blotch.
            const int cells = 24;
            var margin = frame.Width / 32;
            var inner = frame.Width - margin * 2;
            var cellSize = inner / cells;
            var means = new double[cells * cells];
            var cursor = 0;
            for (var cellY = 0; cellY < cells; cellY++)
            {
                for (var cellX = 0; cellX < cells; cellX++)
                {
                    var total = 0d;
                    for (var y = 0; y < cellSize; y++)
                    {
                        var pixelY = margin + cellY * cellSize + y;
                        for (var x = 0; x < cellSize; x++)
                        {
                            var pixelX = margin + cellX * cellSize + x;
                            total += Luma(frame.Pixels[pixelY * frame.Width + pixelX]);
                        }
                    }

                    means[cursor++] = total / (cellSize * cellSize);
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

            var sorted = (double[])means.Clone();
            Array.Sort(sorted);
            var metric = new TerrainMetric();
            metric.MacroDeviation = mean <= 0d
                ? float.PositiveInfinity
                : (float)(Math.Sqrt(variance / means.Length) / mean);
            metric.LowDecile = (float)sorted[sorted.Length / 10];
            return metric;
        }

        private static Frame Capture(
            Camera camera,
            string fileName,
            bool retainPixels = false,
            int width = Width,
            int height = Height)
        {
            // The device screenshots this is compared against are sRGB, so
            // the target has to be written through the same conversion.
            var target = new RenderTexture(
                width, height, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false, false);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                // Edit mode never runs LateUpdate, so Godot vegetation LODs
                // are switched for this view explicitly.
                foreach (var lods in UnityEngine.Object.FindObjectsByType<BattleVegetationLods>())
                {
                    lods.Refresh(camera);
                }

                for (var i = 0; i < WarmupFrames; i++)
                {
                    camera.Render();
                }

                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                texture.Apply(false, false);
                File.WriteAllBytes(
                    Path.Combine(OutputDirectory, fileName), texture.EncodeToPNG());
                return Measure(texture, retainPixels);
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

        /// <summary>
        /// ReadPixels starts at the bottom row, so the last third of the
        /// buffer is the sky band. A lit map under a black sky and a black map
        /// under a drawn sky are different bugs and have to read differently.
        /// </summary>
        private static Frame Measure(Texture2D texture, bool retainPixels)
        {
            var width = texture.width;
            var height = texture.height;
            var pixels = texture.GetPixels32();
            var skyFrom = width * (height - height / 3);
            var total = 0d;
            var sky = 0d;
            var black = 0;
            var cyanEdge = 0;
            var white = 0;
            var whiteBelowHorizon = 0;
            var horizonFrom = width * (height / 2);
            var edgeWidth = Math.Max(1, width / 50);
            for (var i = 0; i < pixels.Length; i++)
            {
                var luma = Luma(pixels[i]);
                total += luma;
                if (i >= skyFrom)
                {
                    sky += luma;
                }

                if (luma < 0.004d)
                {
                    black++;
                }

                var x = i % width;
                var pixel = pixels[i];
                if (i < skyFrom && luma > HorizonWhiteLuma &&
                    Math.Max(pixel.r, Math.Max(pixel.g, pixel.b)) -
                        Math.Min(pixel.r, Math.Min(pixel.g, pixel.b)) <=
                        HorizonWhiteChroma)
                {
                    white++;
                    if (i < horizonFrom)
                    {
                        whiteBelowHorizon++;
                    }
                }
                if ((x < edgeWidth || x >= width - edgeWidth) &&
                    pixel.b >= 80 && pixel.b >= pixel.r + 20 &&
                    pixel.b >= pixel.g + 10)
                {
                    cyanEdge++;
                }
            }

            // GetPixels32 starts at the bottom-left. The lower 60% of the
            // spawn frame is close ground, so its one-pixel luma gradient
            // directly distinguishes tiled sand from a stretched colour map.
            var groundRows = height * 3 / 5;
            var detail = 0d;
            var detailSamples = 0;
            for (var y = 0; y < groundRows; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = y * width + x;
                    var centre = Luma(pixels[index]);
                    if (x + 1 < width)
                    {
                        detail += Math.Abs(centre - Luma(pixels[index + 1]));
                        detailSamples++;
                    }

                    if (y + 1 < groundRows)
                    {
                        detail += Math.Abs(centre - Luma(pixels[index + width]));
                        detailSamples++;
                    }
                }
            }

            var frame = new Frame();
            frame.Width = width;
            frame.Height = height;
            frame.Luma = (float)(total / pixels.Length);
            frame.SkyLuma = (float)(sky / (pixels.Length - skyFrom));
            frame.BlackShare = black / (float)pixels.Length;
            frame.GroundDetail = detailSamples == 0
                ? 0f
                : (float)(detail / detailSamples);
            frame.CyanEdgeShare = cyanEdge /
                (float)(edgeWidth * height * 2);
            frame.WhiteShare = skyFrom == 0 ? 0f : white / (float)skyFrom;
            frame.WhiteShareBelowHorizon = horizonFrom == 0
                ? 0f
                : whiteBelowHorizon / (float)horizonFrom;
            frame.Pixels = retainPixels ? pixels : null;
            return frame;
        }

        private static FrameDelta Difference(Frame first, Frame second)
        {
            if (first.Pixels == null || second.Pixels == null ||
                first.Pixels.Length != second.Pixels.Length)
            {
                throw new InvalidOperationException("render comparison has no pixels");
            }

            var total = 0d;
            var changed = 0;
            for (var i = 0; i < first.Pixels.Length; i++)
            {
                var a = first.Pixels[i];
                var b = second.Pixels[i];
                var red = Math.Abs(a.r - b.r);
                var green = Math.Abs(a.g - b.g);
                var blue = Math.Abs(a.b - b.b);
                total += (red + green + blue) / (3d * 255d);
                if (Math.Max(red, Math.Max(green, blue)) > 12)
                {
                    changed++;
                }
            }

            var delta = new FrameDelta();
            delta.Mean = (float)(total / first.Pixels.Length);
            delta.ChangedShare = changed / (float)first.Pixels.Length;
            return delta;
        }

        private static double Luma(Color32 pixel)
        {
            return (pixel.r + pixel.g + pixel.b) / (3d * 255d);
        }
    }
}
