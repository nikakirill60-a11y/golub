using System.Collections.Generic;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Input;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Save
{
    /// <summary>
    /// Port of assets/data/save_settings.gd: every setting the ported settings
    /// screen (assets/gfx/ui/ui_settings.tscn) shows, with the source defaults
    /// and the source clamps.
    ///
    /// Fields the port has no subsystem for yet - the renderer backend, the
    /// render pipeline and the save migrations - are deliberately absent
    /// rather than stored and ignored.
    ///
    /// Several defaults depend on <c>SaveSettings.is_mobile_platform()</c>,
    /// which is a static in the source as well; <see cref="MobilePlatform"/>
    /// is its counterpart and is set once at bootstrap.
    /// </summary>
    public sealed class SettingsData
    {
        public const float DefaultVolumeSfx = 0.45f;
        public const float DefaultVolumeUi = 0.35f;
        public const float DefaultVolumeMusic = 0.4f;

        /// <summary>SaveSettings.FPS_LIMIT_*.</summary>
        public const int FpsLimitUncapped = 0;
        public const int FpsLimit30 = 30;
        public const int FpsLimit60 = 60;
        public const int FpsLimit90 = 90;
        public const int FpsLimit120 = 120;

        /// <summary>Battle chat row: everything, the own team only, or nothing.</summary>
        public const int ChatOn = 0;
        public const int ChatTeamOnly = 1;
        public const int ChatOff = 2;

        /// <summary>Online battles run on the project's own dedicated server (default).</summary>
        public const int OnlineServerDirect = 0;
        /// <summary>Online battles run on Photon Cloud with a player as the host.</summary>
        public const int OnlineServerPhoton = 1;

        /// <summary>
        /// Menu interface style. Saves written before the redesign carry no
        /// value and read back as <see cref="UiStyleUnset"/>, which is what
        /// makes the first-run style picker appear exactly once after the
        /// update. The battle HUD is the same in every style.
        /// </summary>
        public const int UiStyleUnset = 0;
        /// <summary>The Godot-faithful menus, unchanged.</summary>
        public const int UiStyleClassic = 1;
        /// <summary>The redesigned glass menus.</summary>
        public const int UiStyleModern = 2;

        /// <summary>Card size of the modern hangar carousel.</summary>
        public const int CarouselSizeCompact = 0;
        public const int CarouselSizeNormal = 1;
        public const int CarouselSizeLarge = 2;

        /// <summary>default_fps_limit(true): the stable cap new phones start on.</summary>
        public const int DefaultFpsLimit = FpsLimit60;

        /// <summary>SaveSettings' anonymous enum, in declaration order.</summary>
        public const int CamVanilla = 0;
        public const int CamModern = 1;
        public const int CamsBelow = 2;
        public const int CamsAccurate = 3;
        public const int CamsAbove = 4;
        public const int MmVanilla15 = 5;
        public const int MmAllLoyal38 = 6;
        public const int TeamSize1x1 = 7;
        public const int TeamSize2x2 = 8;
        public const int TeamSize5x5 = 9;
        public const int TeamSize7x7 = 10;
        public const int TeamSize10x10 = 11;
        public const int Time5 = 12;
        public const int Time7 = 13;
        public const int Time15 = 14;
        public const int TimeInf = 15;
        public const int Dev25 = 16;
        public const int Dev15 = 17;
        public const int Dev5 = 18;
        public const int Dev0 = 19;

        public const int DefaultMatchmakerTeamSize = TeamSize7x7;

        // -- graphics ---------------------------------------------------
        //
        // The source shipped one "fps limit" row and left everything else to
        // the engine's own quality preset, which is why the port renders every
        // map with no anti-aliasing at all. These are the options the graphics
        // tab adds on top of the ported ones; they have no save_settings.gd
        // counterpart, so the names are new but follow the same convention.

        /// <summary>MSAA samples. 0 is off; the rest are what URP accepts.</summary>
        public const int MsaaOff = 0;
        public const int Msaa2X = 2;
        public const int Msaa4X = 4;
        public const int Msaa8X = 8;

        public const int ShadowsOff = 0;
        public const int ShadowsHard = 1;
        public const int ShadowsSoft = 2;

        /// <summary>Mip levels dropped from every texture: full, half, quarter.</summary>
        public const int TextureQualityFull = 0;
        public const int TextureQualityHalf = 1;
        public const int TextureQualityQuarter = 2;

        public const int AnisoOff = 0;
        public const int AnisoPerTexture = 1;
        public const int AnisoForced = 2;

        /// <summary>
        /// Blitz 4.8.0 Settings Graphics/Camouflage: whose camouflage is
        /// painted. Only the look; the concealment bonus never depends on it.
        /// </summary>
        public const int CamouflageOff = 0;
        public const int CamouflageForPlayer = 1;
        public const int CamouflageForAll = 2;

        public const float RenderScaleMin = 0.5f;
        public const float RenderScaleMax = 2f;

        /// <summary>
        /// The far plane the draw distance slider drives. The top of the range
        /// is CameraTuning.Far, the distance battle cameras were ported with,
        /// so the default keeps the maps looking exactly as authored.
        /// </summary>
        public const float DrawDistanceMin = 250f;
        public const float DrawDistanceMax = 10000f;

        public const float ShadowDistanceMin = 20f;
        public const float ShadowDistanceMax = 400f;
        public const float DefaultShadowDistance = 100f;

        /// <summary>
        /// <c>save_settings.map_loading_screen_timer_mul</c>: the "MAP INTRO
        /// TIME MUL" slider, clamped to the same range the source clamps to.
        /// </summary>
        public const float DefaultMapIntroTimeMul = 1f;
        public const float MapIntroTimeMulMin = 0.2f;
        public const float MapIntroTimeMulMax = 3f;

        /// <summary>
        /// The client ships one arcade camera distance for every device (4.8.0
        /// options*.yaml, "Camera fixed positions / 1) Arcade"), so the saved
        /// setting starts on the same number the battle camera is tuned to.
        /// </summary>
        public const float CameraStartDistanceDefault = CameraTuning.LengthStart;

        /// <summary>
        /// <c>SaveSettings.is_mobile_platform()</c>. A static in the source as
        /// well, so the defaults below can read it the same way.
        /// </summary>
        public static bool MobilePlatform;

        // -- main tab ---------------------------------------------------
        public string Language = "en";
        public bool SkipLogin;
        public bool Fullscreen;
        public int FpsLimit;
        public int BattleChat = ChatOn;
        public int OnlineServer = OnlineServerDirect;
        public float MouseSensitivity = HangarInputMath.DefaultSensitivity;
        public float MouseSensitivitySniper = 0.3f;
        public float MapIntroTimeMul = DefaultMapIntroTimeMul;
        public bool CameraSmoothing;
        public bool AutoCaptureTarget;
        public bool SniperAutoZoom;
        public float CameraStartDistance;
        public float UiScaleBattle;
        public float UiScaleMenu = 0.75f;
        public float CameraHeightMul;
        public bool AutoAimSoft;
        public float AutoAimStickStrength = 6f;
        public float AutoAimSensMul = 0.55f;
        public float AutoAimReleaseAngle = 25f;
        public float AutoAimStickStrengthSniper = 9f;
        public float AutoAimLeadMul = 1f;
        public float AutoAimRecaptureDelay = 0.8f;
        public float AutoAimCoverGrace = 1.2f;

        // -- interface ------------------------------------------------------
        public int UiStyle = UiStyleUnset;
        public int UiCarouselSize = CarouselSizeNormal;

        /// <summary>
        /// The hangar picked in the settings, by <see cref="HangarInfo.Id"/>. Empty (the default) leaves the
        /// choice to <see cref="HangarSchedule"/>; see <see cref="HangarSelection"/>.
        /// </summary>
        public string Hangar = string.Empty;

        // Only the hangars whose music player was switched by hand; the rest follow the catalog.
        private readonly Dictionary<string, bool> _hangarMusicPlayer = new Dictionary<string, bool>();

        /// <summary>The track, volume and switches of the hangar music player.</summary>
        public HangarMusicSettings HangarMusic = new HangarMusicSettings();

        // -- graphics tab -----------------------------------------------
        public int GraphicsMsaa;
        public float GraphicsRenderScale = 1f;
        public bool GraphicsFog = true;
        public float GraphicsDrawDistance;
        public int GraphicsShadows;
        public float GraphicsShadowDistance = DefaultShadowDistance;
        public int GraphicsTextureQuality = TextureQualityFull;
        public int GraphicsAnisotropic = AnisoPerTexture;
        public bool GraphicsVsync = true;
        public bool GraphicsHdr;
        /// <summary>Blitz 11.20 TankTreads graphics option.</summary>
        public bool GraphicsTankTreads = true;
        public int GraphicsCamouflage = CamouflageForAll;

        // -- matchmaker tab ---------------------------------------------
        public int BattleMaxTime = Time7;
        public int MatchmakerTeamSize = DefaultMatchmakerTeamSize;
        public int DmgDeviation = Dev25;
        public int PenDeviation = Dev5;

        // -- gameplay tab -----------------------------------------------
        public float BattleSpottingDelay = 0.25f;
        public float ShellGravityMul = 1f;
        public bool NoAimTime;
        public bool NoShotDispersion;
        public bool BotsHarmless;
        public bool BotsStationary;

        // -- controls tab -----------------------------------------------

        /// <summary>
        /// save_settings.pc_control_bindings: the overrides the controls tab
        /// writes, keyed by action. An empty model means "project defaults".
        /// </summary>
        public PcControlBindings PcControls = new PcControlBindings();

        // -- not shown by the settings screen ---------------------------
        public float VolumeMaster = 1f;
        public float VolumeMusic = DefaultVolumeMusic;
        public float VolumeUi = DefaultVolumeUi;
        public float VolumeSfx = DefaultVolumeSfx;

        public SettingsData()
        {
            // The source declares these with an is_mobile_platform() default,
            // which a C# field initializer cannot express.
            FpsLimit = DefaultFps(MobilePlatform);
            AutoCaptureTarget = MobilePlatform;
            SniperAutoZoom = MobilePlatform;
            AutoAimSoft = MobilePlatform;
            CameraHeightMul = MobilePlatform ? 0.6f : 1f;
            UiScaleBattle = MobilePlatform ? 1f : 0.75f;
            CameraStartDistance = DefaultCameraStartDistance();

            // A phone cannot afford the desktop defaults, and the whole point
            // of the tab is that the jagged edges are gone out of the box, so
            // both platforms start with anti-aliasing on - just not the same
            // amount of it.
            GraphicsMsaa = MobilePlatform ? Msaa2X : Msaa4X;
            GraphicsShadows = MobilePlatform ? ShadowsHard : ShadowsSoft;
            GraphicsDrawDistance = MobilePlatform ? 3000f : DrawDistanceMax;
            GraphicsHdr = !MobilePlatform;
        }

        /// <summary>
        /// The nearest sample count Unity will accept. A save written by an
        /// older build, or hand-edited, can hold anything.
        /// </summary>
        public static int NormalizeMsaa(int value)
        {
            if (value >= Msaa8X) { return Msaa8X; }
            if (value >= Msaa4X) { return Msaa4X; }
            return value >= Msaa2X ? Msaa2X : MsaaOff;
        }

        /// <summary>SaveSettings.default_fps_limit.</summary>
        public static int DefaultFps(bool mobile)
        {
            return mobile ? FpsLimit60 : FpsLimitUncapped;
        }

        /// <summary>SaveSettings.normalize_fps_limit.</summary>
        public static int NormalizeFpsLimit(int value, bool mobile)
        {
            // Infinity is a real choice on every platform. New phones still
            // default to 60, but a player who explicitly selects infinity gets
            // the fastest display mode the device reports.
            if (value == FpsLimitUncapped || value == FpsLimit30
                || value == FpsLimit60 || value == FpsLimit90
                || value == FpsLimit120)
            {
                return value;
            }

            return DefaultFps(mobile);
        }

        /// <summary>
        /// SaveSettings.default_camera_start_distance. The source splits this
        /// by platform; the client does not, so neither does the port.
        /// </summary>
        public static float DefaultCameraStartDistance()
        {
            return CameraStartDistanceDefault;
        }

        /// <summary><c>clampf(a, 0.2, 3)</c> in the source setter.</summary>
        public static float ClampMapIntroTimeMul(float value)
        {
            return Clamp(value, MapIntroTimeMulMin, MapIntroTimeMulMax);
        }

        public static float Clamp(float value, float min, float max)
        {
            if (value < min) { return min; }
            return value > max ? max : value;
        }

        public static int Clamp(int value, int min, int max)
        {
            if (value < min) { return min; }
            return value > max ? max : value;
        }

        // ------------------------------------------------------------------
        // Settings by source name.
        //
        // ui_settings.gd addresses every option through
        // Global.save_settings.get(name) / .set(name, value), so the screen is
        // driven by the option names of save_settings.gd rather than by one
        // callback per row. These are that lookup, with the setters applying
        // the same clamps the source properties apply.
        // ------------------------------------------------------------------

        public bool GetBool(string option)
        {
            switch (option)
            {
                case "skip_login": return SkipLogin;
                case "fullscreen": return Fullscreen;
                case "camera_smoothing": return CameraSmoothing;
                case "auto_capture_target": return AutoCaptureTarget;
                case "sniper_auto_zoom": return SniperAutoZoom;
                case "auto_aim_soft": return AutoAimSoft;
                case "battle_mechanics_no_aim_time": return NoAimTime;
                case "battle_mechanics_no_disp": return NoShotDispersion;
                case "battle_bots_harmless": return BotsHarmless;
                case "battle_bots_stationary": return BotsStationary;
                case "graphics_fog": return GraphicsFog;
                case "graphics_vsync": return GraphicsVsync;
                case "graphics_hdr": return GraphicsHdr;
                case "graphics_tank_treads": return GraphicsTankTreads;
                default: return false;
            }
        }

        public void SetBool(string option, bool value)
        {
            switch (option)
            {
                case "graphics_fog": GraphicsFog = value; break;
                case "graphics_vsync": GraphicsVsync = value; break;
                case "graphics_hdr": GraphicsHdr = value; break;
                case "graphics_tank_treads": GraphicsTankTreads = value; break;
                case "skip_login": SkipLogin = value; break;
                case "fullscreen": Fullscreen = value; break;
                case "camera_smoothing": CameraSmoothing = value; break;
                case "auto_capture_target": AutoCaptureTarget = value; break;
                case "sniper_auto_zoom": SniperAutoZoom = value; break;
                case "auto_aim_soft": AutoAimSoft = value; break;
                case "battle_mechanics_no_aim_time": NoAimTime = value; break;
                case "battle_mechanics_no_disp": NoShotDispersion = value; break;
                case "battle_bots_harmless": BotsHarmless = value; break;
                case "battle_bots_stationary": BotsStationary = value; break;
            }
        }

        public float GetFloat(string option)
        {
            switch (option)
            {
                case "mouse_sens": return MouseSensitivity;
                case "mouse_sens_sniper": return MouseSensitivitySniper;
                case "map_loading_screen_timer_mul": return MapIntroTimeMul;
                case "camera_len_start": return CameraStartDistance;
                case "ui_scale_battle": return UiScaleBattle;
                case "ui_scale_menu": return UiScaleMenu;
                case "camera_height_mul": return CameraHeightMul;
                case "auto_aim_stick_strength": return AutoAimStickStrength;
                case "auto_aim_sens_mul": return AutoAimSensMul;
                case "auto_aim_release_angle": return AutoAimReleaseAngle;
                case "auto_aim_stick_strength_sniper":
                    return AutoAimStickStrengthSniper;
                case "auto_aim_lead_mul": return AutoAimLeadMul;
                case "auto_aim_recapture_delay": return AutoAimRecaptureDelay;
                case "auto_aim_cover_grace": return AutoAimCoverGrace;
                case "battle_spotting_delay": return BattleSpottingDelay;
                case "battle_mechanics_projectiles_gravity_mul":
                    return ShellGravityMul;
                case "graphics_render_scale": return GraphicsRenderScale;
                case "graphics_draw_distance": return GraphicsDrawDistance;
                case "graphics_shadow_distance": return GraphicsShadowDistance;
                default: return 0f;
            }
        }

        public void SetFloat(string option, float value)
        {
            switch (option)
            {
                case "mouse_sens":
                    MouseSensitivity = HangarInputMath.NormalizeSensitivity(value);
                    break;
                case "mouse_sens_sniper":
                    MouseSensitivitySniper = Clamp(value, 0.05f, 8f);
                    break;
                case "map_loading_screen_timer_mul":
                    MapIntroTimeMul = ClampMapIntroTimeMul(value);
                    break;
                case "camera_len_start":
                    CameraStartDistance = Clamp(
                        value, CameraTuning.LengthMin, CameraTuning.LengthMax);
                    break;
                case "ui_scale_battle":
                    UiScaleBattle = Clamp(value, 0.5f, 2f);
                    break;
                case "ui_scale_menu":
                    UiScaleMenu = Clamp(value, 0.5f, 2f);
                    break;
                case "camera_height_mul":
                    CameraHeightMul = Clamp(value, 0.2f, 2f);
                    break;
                case "auto_aim_stick_strength":
                    AutoAimStickStrength = Clamp(value, 0f, 20f);
                    break;
                case "auto_aim_sens_mul":
                    AutoAimSensMul = Clamp(value, 0.1f, 1f);
                    break;
                case "auto_aim_release_angle":
                    AutoAimReleaseAngle = Clamp(value, 5f, 90f);
                    break;
                case "auto_aim_stick_strength_sniper":
                    AutoAimStickStrengthSniper = Clamp(value, 0f, 20f);
                    break;
                case "auto_aim_lead_mul":
                    AutoAimLeadMul = Clamp(value, 0f, 3f);
                    break;
                case "auto_aim_recapture_delay":
                    AutoAimRecaptureDelay = Clamp(value, 0f, 3f);
                    break;
                case "auto_aim_cover_grace":
                    AutoAimCoverGrace = Clamp(value, 0f, 5f);
                    break;
                case "battle_spotting_delay":
                    BattleSpottingDelay = Clamp(value, 0.1f, 0.5f);
                    break;
                case "battle_mechanics_projectiles_gravity_mul":
                    ShellGravityMul = Clamp(value, 0.1f, 5f);
                    break;
                case "graphics_render_scale":
                    GraphicsRenderScale = Clamp(
                        value, RenderScaleMin, RenderScaleMax);
                    break;
                case "graphics_draw_distance":
                    GraphicsDrawDistance = Clamp(
                        value, DrawDistanceMin, DrawDistanceMax);
                    break;
                case "graphics_shadow_distance":
                    GraphicsShadowDistance = Clamp(
                        value, ShadowDistanceMin, ShadowDistanceMax);
                    break;
            }
        }

        public int GetInt(string option)
        {
            switch (option)
            {
                case "fps_limit": return FpsLimit;
                case "battle_chat": return BattleChat;
                case "online_server": return OnlineServer;
                case "battle_max_time": return BattleMaxTime;
                case "matchmaker_team_size": return MatchmakerTeamSize;
                case "dmg_deviation": return DmgDeviation;
                case "pen_deviation": return PenDeviation;
                case "graphics_msaa": return GraphicsMsaa;
                case "graphics_shadows": return GraphicsShadows;
                case "graphics_texture_quality": return GraphicsTextureQuality;
                case "graphics_aniso": return GraphicsAnisotropic;
                case "graphics_camouflage": return GraphicsCamouflage;
                case "ui_style": return UiStyle;
                case "ui_carousel_size": return UiCarouselSize;
                default: return 0;
            }
        }

        public void SetInt(string option, int value)
        {
            switch (option)
            {
                case "graphics_msaa":
                    GraphicsMsaa = NormalizeMsaa(value);
                    break;
                case "graphics_shadows":
                    GraphicsShadows = Clamp(value, ShadowsOff, ShadowsSoft);
                    break;
                case "graphics_texture_quality":
                    GraphicsTextureQuality = Clamp(
                        value, TextureQualityFull, TextureQualityQuarter);
                    break;
                case "graphics_aniso":
                    GraphicsAnisotropic = Clamp(value, AnisoOff, AnisoForced);
                    break;
                case "graphics_camouflage":
                    GraphicsCamouflage = Clamp(value, CamouflageOff, CamouflageForAll);
                    break;
                case "fps_limit":
                    FpsLimit = NormalizeFpsLimit(value, MobilePlatform);
                    break;
                case "battle_chat":
                    BattleChat = Clamp(value, ChatOn, ChatOff);
                    break;
                case "online_server":
                    OnlineServer = Clamp(value, OnlineServerDirect, OnlineServerPhoton);
                    break;
                case "battle_max_time":
                    BattleMaxTime = Clamp(value, Time5, TimeInf);
                    break;
                case "matchmaker_team_size":
                    MatchmakerTeamSize = Clamp(value, TeamSize1x1, TeamSize10x10);
                    break;
                case "dmg_deviation":
                    DmgDeviation = Clamp(value, Dev25, Dev0);
                    break;
                case "pen_deviation":
                    PenDeviation = Clamp(value, Dev25, Dev0);
                    break;
                case "ui_style":
                    UiStyle = Clamp(value, UiStyleUnset, UiStyleModern);
                    break;
                case "ui_carousel_size":
                    UiCarouselSize = Clamp(
                        value, CarouselSizeCompact, CarouselSizeLarge);
                    break;
            }
        }

        public string GetString(string option)
        {
            switch (option)
            {
                case "language": return Language;
                case "hangar": return Hangar;
                default: return string.Empty;
            }
        }

        /// <summary>
        /// SaveSettings.set_language: unknown locales are refused. The hangar takes an empty choice or the
        /// id of a catalog entry; anything else (a hangar a later build dropped) clears the choice.
        /// </summary>
        public void SetString(string option, string value)
        {
            if (option == "hangar")
            {
                Hangar = HangarCatalog.Find(value) != null ? value : string.Empty;
                return;
            }

            if (option != "language")
            {
                return;
            }

            if (value == "en" || value == "ru")
            {
                Language = value;
            }
        }

        /// <summary>Whether the hangar music player is up in <paramref name="kind"/>.</summary>
        public bool HangarMusicPlayer(HangarKind kind)
        {
            var info = HangarCatalog.Get(kind);
            bool on;
            return _hangarMusicPlayer.TryGetValue(info.Id, out on) ? on : info.MusicPlayerByDefault;
        }

        public void SetHangarMusicPlayer(HangarKind kind, bool on)
        {
            var info = HangarCatalog.Get(kind);
            if (on == info.MusicPlayerByDefault)
            {
                _hangarMusicPlayer.Remove(info.Id);
            }
            else
            {
                _hangarMusicPlayer[info.Id] = on;
            }
        }

        private JsonNode HangarMusicPlayerJson()
        {
            var node = JsonNode.NewObject();
            foreach (var info in HangarCatalog.All)
            {
                bool on;
                if (_hangarMusicPlayer.TryGetValue(info.Id, out on))
                {
                    node.Set(info.Id, on);
                }
            }

            return node;
        }

        public JsonNode ToJson()
        {
            return JsonNode.NewObject()
                .Set("language", Language)
                .Set("skip_login", SkipLogin)
                .Set("fullscreen", Fullscreen)
                .Set("fps_limit", FpsLimit)
                .Set("battle_chat", BattleChat)
                .Set("online_server", OnlineServer)
                .Set("mouse_sens", MouseSensitivity)
                .Set("mouse_sens_sniper", MouseSensitivitySniper)
                .Set("map_loading_screen_timer_mul", MapIntroTimeMul)
                .Set("camera_smoothing", CameraSmoothing)
                .Set("auto_capture_target", AutoCaptureTarget)
                .Set("sniper_auto_zoom", SniperAutoZoom)
                .Set("camera_len_start", CameraStartDistance)
                .Set("ui_scale_battle", UiScaleBattle)
                .Set("ui_scale_menu", UiScaleMenu)
                .Set("camera_height_mul", CameraHeightMul)
                .Set("auto_aim_soft", AutoAimSoft)
                .Set("auto_aim_stick_strength", AutoAimStickStrength)
                .Set("auto_aim_sens_mul", AutoAimSensMul)
                .Set("auto_aim_release_angle", AutoAimReleaseAngle)
                .Set("auto_aim_stick_strength_sniper", AutoAimStickStrengthSniper)
                .Set("auto_aim_lead_mul", AutoAimLeadMul)
                .Set("auto_aim_recapture_delay", AutoAimRecaptureDelay)
                .Set("auto_aim_cover_grace", AutoAimCoverGrace)
                .Set("ui_style", UiStyle)
                .Set("ui_carousel_size", UiCarouselSize)
                .Set("hangar", Hangar)
                .Set("hangar_music_player", HangarMusicPlayerJson())
                .Set("hangar_music", HangarMusic.ToJson())
                .Set("graphics_msaa", GraphicsMsaa)
                .Set("graphics_render_scale", GraphicsRenderScale)
                .Set("graphics_fog", GraphicsFog)
                .Set("graphics_draw_distance", GraphicsDrawDistance)
                .Set("graphics_shadows", GraphicsShadows)
                .Set("graphics_shadow_distance", GraphicsShadowDistance)
                .Set("graphics_texture_quality", GraphicsTextureQuality)
                .Set("graphics_aniso", GraphicsAnisotropic)
                .Set("graphics_vsync", GraphicsVsync)
                .Set("graphics_hdr", GraphicsHdr)
                .Set("graphics_tank_treads", GraphicsTankTreads)
                .Set("graphics_camouflage", GraphicsCamouflage)
                .Set("battle_max_time", BattleMaxTime)
                .Set("matchmaker_team_size", MatchmakerTeamSize)
                .Set("dmg_deviation", DmgDeviation)
                .Set("pen_deviation", PenDeviation)
                .Set("battle_spotting_delay", BattleSpottingDelay)
                .Set("battle_mechanics_projectiles_gravity_mul", ShellGravityMul)
                .Set("battle_mechanics_no_aim_time", NoAimTime)
                .Set("battle_mechanics_no_disp", NoShotDispersion)
                .Set("battle_bots_harmless", BotsHarmless)
                .Set("battle_bots_stationary", BotsStationary)
                .Set("volume_master", VolumeMaster)
                .Set("volume_music", VolumeMusic)
                .Set("volume_ui", VolumeUi)
                .Set("volume_sfx", VolumeSfx)
                .Set("pc_control_bindings", PcControls.ToJson());
        }

        /// <summary>
        /// The rules every online battle runs with: the stock values of the
        /// matchmaker and gameplay tabs (7x7, 7 minutes, stock damage and
        /// penetration rolls, real ballistics and bots). Only these fields are
        /// meant to be read from it; graphics, controls and sound stay personal.
        /// </summary>
        public static SettingsData StandardRules()
        {
            return new SettingsData();
        }

        public static SettingsData FromJson(JsonNode node)
        {
            var settings = new SettingsData();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return settings;
            }

            settings.SetString("language", node["language"].AsString("en"));
            settings.SkipLogin = node["skip_login"].AsBool();
            settings.Fullscreen = node["fullscreen"].AsBool();
            settings.SetInt("fps_limit", node["fps_limit"].AsInt(settings.FpsLimit));
            settings.SetInt("battle_chat", node["battle_chat"].AsInt(settings.BattleChat));
            settings.SetInt("online_server", node["online_server"].AsInt(settings.OnlineServer));
            settings.CameraSmoothing = node["camera_smoothing"].AsBool(
                settings.CameraSmoothing);
            settings.AutoCaptureTarget = node["auto_capture_target"].AsBool(
                settings.AutoCaptureTarget);
            settings.SniperAutoZoom = node["sniper_auto_zoom"].AsBool(
                settings.SniperAutoZoom);
            settings.AutoAimSoft = node["auto_aim_soft"].AsBool(settings.AutoAimSoft);
            settings.NoAimTime = node["battle_mechanics_no_aim_time"].AsBool();
            settings.NoShotDispersion = node["battle_mechanics_no_disp"].AsBool();
            settings.BotsHarmless = node["battle_bots_harmless"].AsBool();
            settings.BotsStationary = node["battle_bots_stationary"].AsBool();

            ReadFloat(settings, node, "mouse_sens");
            ReadFloat(settings, node, "mouse_sens_sniper");
            ReadFloat(settings, node, "map_loading_screen_timer_mul");
            ReadFloat(settings, node, "camera_len_start");
            ReadFloat(settings, node, "ui_scale_battle");
            ReadFloat(settings, node, "ui_scale_menu");
            ReadFloat(settings, node, "camera_height_mul");
            ReadFloat(settings, node, "auto_aim_stick_strength");
            ReadFloat(settings, node, "auto_aim_sens_mul");
            ReadFloat(settings, node, "auto_aim_release_angle");
            ReadFloat(settings, node, "auto_aim_stick_strength_sniper");
            ReadFloat(settings, node, "auto_aim_lead_mul");
            ReadFloat(settings, node, "auto_aim_recapture_delay");
            ReadFloat(settings, node, "auto_aim_cover_grace");
            ReadFloat(settings, node, "battle_spotting_delay");
            ReadFloat(settings, node, "battle_mechanics_projectiles_gravity_mul");

            settings.GraphicsFog = node["graphics_fog"].AsBool(settings.GraphicsFog);
            settings.GraphicsVsync = node["graphics_vsync"].AsBool(
                settings.GraphicsVsync);
            settings.GraphicsHdr = node["graphics_hdr"].AsBool(settings.GraphicsHdr);
            settings.GraphicsTankTreads = node["graphics_tank_treads"].AsBool(
                settings.GraphicsTankTreads);

            ReadFloat(settings, node, "graphics_render_scale");
            ReadFloat(settings, node, "graphics_draw_distance");
            ReadFloat(settings, node, "graphics_shadow_distance");

            ReadInt(settings, node, "graphics_msaa");
            ReadInt(settings, node, "graphics_shadows");
            ReadInt(settings, node, "graphics_texture_quality");
            ReadInt(settings, node, "graphics_aniso");
            ReadInt(settings, node, "graphics_camouflage");

            ReadInt(settings, node, "battle_max_time");
            ReadInt(settings, node, "matchmaker_team_size");
            ReadInt(settings, node, "dmg_deviation");
            ReadInt(settings, node, "pen_deviation");
            ReadInt(settings, node, "ui_style");
            ReadInt(settings, node, "ui_carousel_size");
            settings.SetString("hangar", node["hangar"].AsString());
            foreach (var field in node["hangar_music_player"].Fields)
            {
                var info = HangarCatalog.Find(field.Key);
                if (info != null && field.Value.Kind == JsonKind.Bool)
                {
                    settings.SetHangarMusicPlayer(info.Kind, field.Value.AsBool());
                }
            }

            settings.HangarMusic = HangarMusicSettings.FromJson(node["hangar_music"]);

            settings.VolumeMaster = node["volume_master"].AsFloat(1f);
            settings.VolumeMusic = node["volume_music"].AsFloat(DefaultVolumeMusic);
            settings.VolumeUi = node["volume_ui"].AsFloat(DefaultVolumeUi);
            settings.VolumeSfx = node["volume_sfx"].AsFloat(DefaultVolumeSfx);
            settings.PcControls = PcControlBindings.FromJson(
                node["pc_control_bindings"]);
            return settings;
        }

        private static void ReadFloat(SettingsData settings, JsonNode node, string option)
        {
            settings.SetFloat(
                option, node[option].AsFloat(settings.GetFloat(option)));
        }

        private static void ReadInt(SettingsData settings, JsonNode node, string option)
        {
            settings.SetInt(option, node[option].AsInt(settings.GetInt(option)));
        }
    }
}
