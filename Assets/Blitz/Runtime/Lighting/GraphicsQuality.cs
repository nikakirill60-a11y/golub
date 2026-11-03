using System.Collections.Generic;
using BlitzLegacy.Core.Save;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BlitzLegacy.Runtime.Lighting
{
    /// <summary>
    /// The runtime half of the graphics tab.
    ///
    /// <see cref="SettingsData"/> stores what the player picked; this applies
    /// it to the three places Unity keeps render state in: the URP asset
    /// (MSAA, render scale, HDR, shadow distance), the global QualitySettings
    /// (texture mip limit, anisotropy, vsync, shadow mode) and the live scene
    /// (camera far planes, light shadow modes, the fog switch).
    ///
    /// Both ported scenes go through it. The hangar and the battle each build
    /// their own camera and their own lights, so each registers what it built
    /// and calls <see cref="Apply"/> once the scene is up; a setting changed
    /// from the settings screen then reaches whichever of the two is on screen
    /// without either scene knowing about the other.
    ///
    /// Fog is the one setting that cannot simply be written once. Both scenes
    /// re-push their authored environment whenever they load
    /// (SourceEnvironment.Apply, BattleWorld.ApplyClientEnvironment), which
    /// would overwrite the player's choice, so those two call
    /// <see cref="ApplyFog"/> at the end of their own environment pass.
    /// </summary>
    public static class GraphicsQuality
    {
        /// <summary>A camera the current scene wants kept in sync.</summary>
        private struct CameraBinding
        {
            public Camera Camera;

            /// <summary>
            /// The far plane the scene authored. The draw distance slider is
            /// a battlefield control, and the hangar's own 150 m box is much
            /// smaller than its lowest step, so a scene that would be clipped
            /// by the chosen distance keeps its own plane instead.
            /// </summary>
            public float AuthoredFar;
        }

        private static readonly List<CameraBinding> Cameras = new List<CameraBinding>();

        private static SettingsData _settings;
        private static int _requestedMobileRefreshRate;

        /// <summary>The settings the last <see cref="Bind"/> handed over.</summary>
        public static SettingsData Settings { get { return _settings; } }

        /// <summary>
        /// Raised once <see cref="Apply"/> has pushed the settings, for the
        /// views that redraw themselves rather than read the pipeline.
        /// </summary>
        public static event System.Action Applied;

        /// <summary>Bootstrap hands the loaded save over once, at startup.</summary>
        public static void Bind(SettingsData settings)
        {
            _settings = settings;
            Apply();
        }

        /// <summary>
        /// A scene camera that follows the draw distance. <paramref
        /// name="authoredFar"/> is the plane the scene picked for itself and
        /// is never shortened below.
        /// </summary>
        public static void Register(Camera camera, float authoredFar)
        {
            if (camera == null)
            {
                return;
            }

            Unregister(camera);
            Cameras.Add(new CameraBinding
            {
                Camera = camera,
                AuthoredFar = authoredFar,
            });
            ApplyCamera(Cameras[Cameras.Count - 1]);
        }

        public static void Unregister(Camera camera)
        {
            for (var i = Cameras.Count - 1; i >= 0; i--)
            {
                if (Cameras[i].Camera == null || Cameras[i].Camera == camera)
                {
                    Cameras.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Push every graphics setting. Cheap enough to call whenever a scene
        /// finishes building or a row of the graphics tab is touched.
        /// </summary>
        public static void Apply()
        {
            if (_settings == null)
            {
                return;
            }

            ApplyPipeline();
            ApplyWindow();
            ApplyTextures();
            ApplyShadows();
            ApplyFrameRate();
            ApplyFog();

            for (var i = Cameras.Count - 1; i >= 0; i--)
            {
                if (Cameras[i].Camera == null)
                {
                    Cameras.RemoveAt(i);
                    continue;
                }

                ApplyCamera(Cameras[i]);
            }

            var applied = Applied;
            if (applied != null) { applied(); }
        }

        /// <summary>save_settings "FULLSCREEN" as a Unity display mode.</summary>
        public static FullScreenMode WindowMode(bool fullscreen)
        {
            return fullscreen
                ? FullScreenMode.FullScreenWindow
                : FullScreenMode.Windowed;
        }

        /// <summary>
        /// The fullscreen row. It was saved and drawn but never read, so the
        /// client opened in whatever mode the player last left the window in.
        /// A phone has no window to resize and the editor owns its own, so
        /// only a desktop player is switched.
        /// </summary>
        public static void ApplyWindow()
        {
            if (_settings == null || Application.isEditor
                || Application.isMobilePlatform)
            {
                return;
            }

            var mode = WindowMode(_settings.Fullscreen);
            if (Screen.fullScreenMode != mode)
            {
                Screen.fullScreenMode = mode;
            }
        }

        /// <summary>The active URP asset, or null in a non-URP smoke run.</summary>
        private static UniversalRenderPipelineAsset Pipeline()
        {
            return GraphicsSettings.currentRenderPipeline
                as UniversalRenderPipelineAsset;
        }

        /// <summary>
        /// The jagged edges the graphics tab exists for. URP ignores
        /// QualitySettings.antiAliasing and reads msaaSampleCount off its own
        /// asset, so the sample count goes there; the render scale is the
        /// second half of the same fix, because supersampling cleans up the
        /// edges MSAA cannot reach (alpha-tested foliage, the shader aliasing
        /// on the tank's own normal maps).
        /// </summary>
        private static void ApplyPipeline()
        {
            var samples = SettingsData.NormalizeMsaa(_settings.GraphicsMsaa);

            // Kept in step for anything reading the legacy value, and it is
            // what a built-in-pipeline smoke run would use.
            QualitySettings.antiAliasing = samples;

            var pipeline = Pipeline();
            if (pipeline == null)
            {
                return;
            }

            // The URP asset takes 1 for "no MSAA" where the option stores 0.
            pipeline.msaaSampleCount = samples <= 1 ? 1 : samples;
            pipeline.renderScale = _settings.GraphicsRenderScale;
            pipeline.supportsHDR = _settings.GraphicsHdr;
            pipeline.shadowDistance = _settings.GraphicsShadowDistance;
        }

        /// <summary>
        /// Texture budget and filtering. The mip limit drops the top mip of
        /// every texture, which is the cheapest way to fit a low-memory phone
        /// and the one the source's own quality preset uses.
        /// </summary>
        private static void ApplyTextures()
        {
            QualitySettings.globalTextureMipmapLimit = SettingsData.Clamp(
                _settings.GraphicsTextureQuality, 0, 2);

            switch (_settings.GraphicsAnisotropic)
            {
                case SettingsData.AnisoOff:
                    QualitySettings.anisotropicFiltering =
                        AnisotropicFiltering.Disable;
                    break;
                case SettingsData.AnisoForced:
                    QualitySettings.anisotropicFiltering =
                        AnisotropicFiltering.ForceEnable;
                    break;
                default:
                    QualitySettings.anisotropicFiltering =
                        AnisotropicFiltering.Enable;
                    break;
            }
        }

        /// <summary>
        /// Shadows. URP keeps its own "are shadows supported" flags on the
        /// asset and exposes no runtime setter for them, so the switch is made
        /// on the lights themselves, which is also the only way to reach the
        /// hangar's single dynamic spot and the battle's sun with one call.
        /// </summary>
        private static void ApplyShadows()
        {
            var mode = SettingsData.Clamp(
                _settings.GraphicsShadows,
                SettingsData.ShadowsOff,
                SettingsData.ShadowsSoft);

            QualitySettings.shadowDistance = _settings.GraphicsShadowDistance;

            // URP declares a ShadowQuality of its own, so the built-in one
            // QualitySettings takes has to be named in full.
            switch (mode)
            {
                case SettingsData.ShadowsOff:
                    QualitySettings.shadows = UnityEngine.ShadowQuality.Disable;
                    break;
                case SettingsData.ShadowsHard:
                    QualitySettings.shadows = UnityEngine.ShadowQuality.HardOnly;
                    break;
                default:
                    QualitySettings.shadows = UnityEngine.ShadowQuality.All;
                    break;
            }

            var lights = Object.FindObjectsByType<Light>(
                FindObjectsInactive.Include);
            for (var i = 0; i < lights.Length; i++)
            {
                var light = lights[i];
                if (light == null || light.type == LightType.Rectangle
                    || light.type == LightType.Disc)
                {
                    continue;
                }

                if (light.GetComponent<BakedSun>() != null
                    || light.GetComponent<ShadowlessLight>() != null)
                {
                    light.shadows = LightShadows.None;
                    continue;
                }

                switch (mode)
                {
                    case SettingsData.ShadowsOff:
                        light.shadows = LightShadows.None;
                        break;
                    case SettingsData.ShadowsHard:
                        light.shadows = LightShadows.Hard;
                        break;
                    default:
                        light.shadows = LightShadows.Soft;
                        break;
                }
            }
        }

        /// <summary>A testable projection of Unity's Resolution struct.</summary>
        public struct DisplayModeInfo
        {
            public int Width;
            public int Height;
            public double RefreshRate;

            public DisplayModeInfo(int width, int height, double refreshRate)
            {
                Width = width;
                Height = height;
                RefreshRate = refreshRate;
            }
        }

        /// <summary>
        /// SaveSettings.apply_fps_limit. Explicit 30/60/90/120 values are hard
        /// caps. Infinity means the fastest refresh mode available at the
        /// current resolution, on desktop and mobile alike.
        /// </summary>
        public static void ApplyFrameRate()
        {
            if (_settings == null)
            {
                return;
            }

            var maximum = MaximumDisplayRefreshRate();
            QualitySettings.vSyncCount = VSyncCount(
                _settings.FpsLimit, _settings.GraphicsVsync, Application.isEditor);
            Application.targetFrameRate = TargetFrameRate(_settings.FpsLimit, maximum);

            if (_settings.FpsLimit <= SettingsData.FpsLimitUncapped)
            {
                RequestMaximumMobileRefreshRate(maximum);
            }
            else
            {
                _requestedMobileRefreshRate = 0;
            }
        }

        /// <summary>
        /// A chosen numerical cap must not be replaced by vSync with the full
        /// monitor rate. The Editor Game view also cannot use desktop vSync as
        /// a reliable pacer. For infinity in a player, the user's vSync switch
        /// remains authoritative while targetFrameRate still records the
        /// detected maximum for platforms that ignore vSync.
        /// </summary>
        public static int VSyncCount(
            int fpsLimit, bool graphicsVsync, bool isEditor)
        {
            if (isEditor || fpsLimit > SettingsData.FpsLimitUncapped)
            {
                return 0;
            }

            return graphicsVsync ? 1 : 0;
        }

        public static int TargetFrameRate(int fpsLimit, int maximumRefreshRate)
        {
            return fpsLimit > SettingsData.FpsLimitUncapped
                ? fpsLimit
                : Mathf.Clamp(maximumRefreshRate > 0 ? maximumRefreshRate : 60, 1, 1000);
        }

        /// <summary>
        /// Unity can report the display's preferred mode (60 Hz here) through
        /// currentResolution even while the compositor is running it at 180
        /// Hz. Supported modes are therefore authoritative: use the fastest
        /// mode matching the current pixel dimensions. Only if Unity exposes
        /// no matching mode do we fall back to all modes, then current, then
        /// the conservative 60 Hz baseline.
        /// </summary>
        public static int MaximumRefreshRate(
            DisplayModeInfo current, DisplayModeInfo[] supported)
        {
            var best = ValidRefresh(current.RefreshRate);
            var matched = false;
            if (supported != null)
            {
                for (var i = 0; i < supported.Length; i++)
                {
                    var mode = supported[i];
                    if (!SameDimensions(current, mode)) { continue; }
                    matched = true;
                    best = System.Math.Max(best, ValidRefresh(mode.RefreshRate));
                }

                if (!matched)
                {
                    for (var i = 0; i < supported.Length; i++)
                    {
                        best = System.Math.Max(
                            best, ValidRefresh(supported[i].RefreshRate));
                    }
                }
            }

            return Mathf.Clamp(
                Mathf.RoundToInt((float)(best > 0d ? best : 60d)), 1, 1000);
        }

        public static int MaximumDisplayRefreshRate()
        {
            var current = Screen.currentResolution;
            var resolutions = Screen.resolutions;
            var supported = new DisplayModeInfo[resolutions.Length];
            for (var i = 0; i < resolutions.Length; i++)
            {
                supported[i] = Mode(resolutions[i]);
            }

            var maximum = MaximumRefreshRate(Mode(current), supported);
#if UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
            // Unity 6 under XWayland exposes only the monitor's 60 Hz
            // preferred mode even when the compositor and XRandR are actively
            // presenting at 180 Hz. Ask XRandR for the marked active mode and
            // use it when it contains better information.
            maximum = Mathf.Max(maximum, LinuxDisplayRefreshRate());
#endif
            return maximum;
        }

#if UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
        public static int LinuxDisplayRefreshRate()
        {
            const string xrandr = "/usr/bin/xrandr";
            if (!System.IO.File.Exists(xrandr))
            {
                return 0;
            }

            try
            {
                var start = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = xrandr,
                    Arguments = "--current",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var process = System.Diagnostics.Process.Start(start))
                {
                    if (process == null)
                    {
                        return 0;
                    }
                    if (!process.WaitForExit(1000))
                    {
                        try { process.Kill(); }
                        catch { /* best-effort cleanup of a diagnostic query */ }
                        return 0;
                    }
                    if (process.ExitCode != 0)
                    {
                        return 0;
                    }

                    return ParseXrandrActiveRefreshRate(
                        process.StandardOutput.ReadToEnd());
                }
            }
            catch
            {
                // Display probing must never prevent the game from starting;
                // the Unity-derived value remains the fallback.
                return 0;
            }
        }
#endif

        /// <summary>
        /// Reads the refresh marked with '*' for XRandR's primary output. A
        /// secondary monitor must not raise the game's cap above the display
        /// that owns the main window. If no output is marked primary, the
        /// first connected output is used.
        /// </summary>
        public static int ParseXrandrActiveRefreshRate(string output)
        {
            if (string.IsNullOrEmpty(output))
            {
                return 0;
            }

            var lines = output.Split(new[] { '\r', '\n' },
                System.StringSplitOptions.RemoveEmptyEntries);
            var hasPrimary = output.Contains(" connected primary ");
            var useOutput = !hasPrimary;
            var firstConnectedSeen = false;
            var best = 0d;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var indented = line.Length > 0 && char.IsWhiteSpace(line[0]);
                if (!indented && line.Contains(" connected"))
                {
                    if (hasPrimary)
                    {
                        useOutput = line.Contains(" connected primary ");
                    }
                    else
                    {
                        useOutput = !firstConnectedSeen;
                        firstConnectedSeen = true;
                    }
                    continue;
                }

                if (!useOutput)
                {
                    continue;
                }

                for (var star = line.IndexOf('*'); star >= 0;
                     star = line.IndexOf('*', star + 1))
                {
                    var end = star - 1;
                    var begin = end;
                    while (begin >= 0 && (char.IsDigit(line[begin])
                        || line[begin] == '.'))
                    {
                        begin--;
                    }

                    if (end < begin + 1)
                    {
                        continue;
                    }

                    double refresh;
                    if (double.TryParse(
                        line.Substring(begin + 1, end - begin),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out refresh))
                    {
                        best = System.Math.Max(best, ValidRefresh(refresh));
                    }
                }
            }

            return Mathf.Clamp(Mathf.RoundToInt((float)best), 0, 1000);
        }

        private static DisplayModeInfo Mode(Resolution mode)
        {
            return new DisplayModeInfo(
                mode.width, mode.height, mode.refreshRateRatio.value);
        }

        private static bool SameDimensions(DisplayModeInfo left, DisplayModeInfo right)
        {
            return (left.Width == right.Width && left.Height == right.Height)
                || (left.Width == right.Height && left.Height == right.Width);
        }

        private static double ValidRefresh(double refreshRate)
        {
            return double.IsNaN(refreshRate) || double.IsInfinity(refreshRate)
                || refreshRate <= 0d ? 0d : refreshRate;
        }

        /// <summary>
        /// On high-refresh phones targetFrameRate alone does not consistently
        /// switch Android/iOS from a 60 Hz display mode. Request the matching
        /// native mode explicitly; the call is asynchronous and unsupported
        /// platforms safely ignore it.
        /// </summary>
        private static void RequestMaximumMobileRefreshRate(int maximum)
        {
            if (Application.isEditor || !Application.isMobilePlatform
                || _requestedMobileRefreshRate == maximum)
            {
                return;
            }

            var current = Screen.currentResolution;
            var resolutions = Screen.resolutions;
            var best = -1;
            var bestRate = 0d;
            for (var i = 0; i < resolutions.Length; i++)
            {
                var mode = resolutions[i];
                if (!SameDimensions(Mode(current), Mode(mode))) { continue; }
                var rate = ValidRefresh(mode.refreshRateRatio.value);
                if (rate > bestRate)
                {
                    bestRate = rate;
                    best = i;
                }
            }

            if (best < 0 || Mathf.RoundToInt((float)bestRate) < maximum)
            {
                return;
            }

            var selected = resolutions[best];
            Screen.SetResolution(
                current.width, current.height,
                Screen.fullScreenMode, selected.refreshRateRatio);
            _requestedMobileRefreshRate = maximum;
        }

        /// <summary>
        /// The fog switch, applied on top of whatever environment the scene
        /// just pushed. Unity's own linear fog is what the URP Lit materials
        /// read (the tank, the props), while the ported map and hangar shaders
        /// read the source curve out of the BlitzFog.hlsl globals, so turning
        /// fog off has to silence both: the built-in flag, the alpha of
        /// _BlitzFogColour that gates the Godot curve, and the w of
        /// _BlitzFogRange that gates the 4.8 client curve.
        ///
        /// Called by both scenes at the end of their environment pass, so it
        /// must leave an enabled setting completely alone - the authored fog
        /// is the correct fog, and re-deriving it here would fight the scene.
        /// </summary>
        public static void ApplyFog()
        {
            if (_settings == null || _settings.GraphicsFog)
            {
                return;
            }

            RenderSettings.fog = false;

            var colour = Shader.GetGlobalVector(SourceEnvironment.FogColourId);
            colour.w = 0f;
            Shader.SetGlobalVector(SourceEnvironment.FogColourId, colour);
            Shader.SetGlobalVector(SourceEnvironment.FogRangeId, Vector4.zero);
            Shader.SetGlobalVector(SourceEnvironment.FogSkyId, Vector4.zero);
            SourceEnvironment.ClearClientFog();
        }

        /// <summary>
        /// The draw distance on one registered camera. A scene that authored a
        /// nearer plane than the player picked keeps its own, so the hangar
        /// box is not pushed out to a battlefield's ten kilometres for nothing.
        /// </summary>
        private static void ApplyCamera(CameraBinding binding)
        {
            if (binding.Camera == null || _settings == null)
            {
                return;
            }

            var distance = SettingsData.Clamp(
                _settings.GraphicsDrawDistance,
                SettingsData.DrawDistanceMin,
                SettingsData.DrawDistanceMax);

            binding.Camera.farClipPlane = Mathf.Min(binding.AuthoredFar, distance);
            binding.Camera.allowMSAA = _settings.GraphicsMsaa > 0;
            binding.Camera.allowHDR = _settings.GraphicsHdr;
        }
    }
}
