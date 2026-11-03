using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using BlitzLegacy.Runtime.Lighting;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ShadowQuality = UnityEngine.ShadowQuality;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Every setting has to do something. A row that writes nothing, a saved
    /// value nothing ever reads and a graphics option that never reaches the
    /// engine all look identical to a player: a switch that does not work.
    ///
    /// So the settings are walked as a whole here instead of one at a time.
    /// Each row on the screen is moved through the screen's own accessors and
    /// has to move exactly one saved field; each saved field has to be either
    /// reachable from a row or listed below with what drives it; and each one
    /// has to be read somewhere outside the settings plumbing itself.
    /// </summary>
    public sealed class SettingsWiringTests
    {
        /// <summary>
        /// Settings the ported screen has no row for, with what does drive
        /// them. They still have to be read by the game, which is checked
        /// alongside every other setting below.
        /// </summary>
        private static readonly Dictionary<string, string> OffScreen =
            new Dictionary<string, string>
            {
                { "PcControls", "the controls tab, which binds keys instead of values" },
                { "VolumeMaster", "AudioService, which mixes the buses" },
                { "VolumeMusic", "AudioService" },
                { "VolumeUi", "AudioService" },
                { "VolumeSfx", "AudioService" },
                { "HangarMusic", "the hangar music player, which keeps its own track, volume and switches" },
            };

        /// <summary>The settings plumbing itself does not count as a reader.</summary>
        private static readonly string[] NotReaders =
        {
            "Core/Save/SettingsData.cs",
            "Runtime/Ui/SettingsScreen.cs",
            "/Tests/",
        };

        private SettingsData _boundBefore;
        private GraphicsState _graphicsBefore;

        [SetUp]
        public void SetUp()
        {
            _boundBefore = GraphicsQuality.Settings;
            _graphicsBefore = new GraphicsState();
        }

        [TearDown]
        public void TearDown()
        {
            // Bind also applies settings. Restore the binding (including null)
            // first, then the actual authored state, which may deliberately
            // differ from the bound save. Never replace it with new defaults.
            try { GraphicsQuality.Bind(_boundBefore); }
            finally { _graphicsBefore?.Restore(); }
        }

        /// <summary>Quality and pipeline values before the fixture, not SettingsData defaults.</summary>
        private sealed class GraphicsState
        {
            private readonly int _aa = QualitySettings.antiAliasing;
            private readonly ShadowQuality _shadows = QualitySettings.shadows;
            private readonly float _shadowDistance = QualitySettings.shadowDistance;
            private readonly int _mipLimit = QualitySettings.globalTextureMipmapLimit;
            private readonly AnisotropicFiltering _anisotropy = QualitySettings.anisotropicFiltering;
            private readonly int _vsync = QualitySettings.vSyncCount;
            private readonly int _fps = Application.targetFrameRate;
            private readonly UniversalRenderPipelineAsset _pipeline;
            private readonly int _samples;
            private readonly float _renderScale, _pipelineShadowDistance;
            private readonly bool _hdr;
            private readonly Dictionary<Light, LightShadows> _lightShadows = new Dictionary<Light, LightShadows>();

            public GraphicsState()
            {
                _pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (_pipeline != null)
                {
                    _samples = _pipeline.msaaSampleCount;
                    _renderScale = _pipeline.renderScale;
                    _hdr = _pipeline.supportsHDR;
                    _pipelineShadowDistance = _pipeline.shadowDistance;
                }
                foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
                    _lightShadows.Add(light, light.shadows);
            }

            public void Restore()
            {
                QualitySettings.antiAliasing = _aa;
                QualitySettings.shadows = _shadows;
                QualitySettings.shadowDistance = _shadowDistance;
                QualitySettings.globalTextureMipmapLimit = _mipLimit;
                QualitySettings.anisotropicFiltering = _anisotropy;
                QualitySettings.vSyncCount = _vsync;
                Application.targetFrameRate = _fps;
                if (_pipeline != null)
                {
                    _pipeline.msaaSampleCount = _samples;
                    _pipeline.renderScale = _renderScale;
                    _pipeline.supportsHDR = _hdr;
                    _pipeline.shadowDistance = _pipelineShadowDistance;
                }
                foreach (var entry in _lightShadows)
                    if (entry.Key != null) entry.Key.shadows = entry.Value;
            }
        }

        [Test]
        public void EveryRowOnTheScreenMovesExactlyOneSetting()
        {
            var settings = new SettingsData();
            var rows = SettingsScreen.SettingRow.Load();
            Assert.Greater(rows.Count, 0, "the settings manifest carried no rows");

            foreach (var row in rows)
            {
                var before = Snapshot(settings);
                Move(settings, row);
                var moved = Changed(before, Snapshot(settings));

                Assert.AreEqual(
                    1,
                    moved.Count,
                    "\"" + row.LabelKey + "\" (" + row.Setting + ") moved "
                        + moved.Count + " settings: " + string.Join(", ", moved.ToArray()));
            }
        }

        [Test]
        public void EverySavedSettingIsEitherOnTheScreenOrAccountedFor()
        {
            var reachable = Reachable();

            foreach (var field in Fields())
            {
                if (reachable.ContainsKey(field.Name) || OffScreen.ContainsKey(field.Name))
                {
                    continue;
                }

                Assert.Fail(
                    field.Name + " is saved but nothing on the settings screen "
                    + "can change it. Give it a row or list what drives it.");
            }
        }

        [Test]
        public void EverySavedSettingIsReadByTheGame()
        {
            var sources = Sources();

            foreach (var field in Fields())
            {
                var read = false;
                foreach (var source in sources)
                {
                    if (source.Value.Contains("." + field.Name))
                    {
                        read = true;
                        break;
                    }
                }

                Assert.IsTrue(
                    read,
                    field.Name + " is saved and can be changed, but nothing "
                    + "outside the settings screen ever reads it.");
            }
        }

        [Test]
        public void SkipLoginNeedsAnAccountToSkipTo()
        {
            var accounts = new AccountBook();
            var off = new SettingsData { SkipLogin = false };
            var on = new SettingsData { SkipLogin = true };

            Assert.IsFalse(BlitzBootstrap.SkipsLogin(on, null));
            Assert.IsFalse(BlitzBootstrap.SkipsLogin(on, accounts), "No account is chosen yet");
            var account = accounts.CreateAccount("Player", "player", "pass", 0d).Account;
            accounts.SwitchAccount(account.Key);
            Assert.IsFalse(BlitzBootstrap.SkipsLogin(off, accounts));
            Assert.IsTrue(BlitzBootstrap.SkipsLogin(on, accounts));
        }

        [Test]
        public void SkipLoginWaitsForTheSignInAfterLogout()
        {
            var accounts = new AccountBook();
            var on = new SettingsData { SkipLogin = true };
            var account = accounts.CreateAccount("Player", "player", "pass", 0d).Account;
            accounts.SwitchAccount(account.Key);

            accounts.LogOut();
            Assert.IsFalse(BlitzBootstrap.SkipsLogin(on, accounts), "The boot skipped the login after logout");
            Assert.IsTrue(on.SkipLogin, "Logout must not switch the setting itself off");
            Assert.IsTrue(accounts.LogInToCurrentAccount(60d));
            Assert.IsTrue(BlitzBootstrap.SkipsLogin(on, accounts), "Signing in again did not bring the skip back");
        }

        [Test]
        public void TheHoldPositionRowFreezesTheBots()
        {
            var settings = new SettingsData();

            AiTankController.ApplySettings(settings);
            Assert.IsFalse(AiTankController.BotsStationary);

            settings.BotsStationary = true;
            AiTankController.ApplySettings(settings);
            Assert.IsTrue(AiTankController.BotsStationary);

            AiTankController.ApplySettings(null);
            Assert.IsFalse(AiTankController.BotsStationary);
        }

        [Test]
        public void TheHarmlessBotsRowOnlyStopsBotDamage()
        {
            var settings = new SettingsData();

            Assert.IsTrue(
                BattleTank.DamageLands(settings, null, null),
                "the row is off by default");

            settings.BotsHarmless = true;
            Assert.IsTrue(
                BattleTank.DamageLands(settings, null, null),
                "damage with no attacker still lands");
        }

        [Test]
        public void TheNoDispersionRowPinsTheCone()
        {
            var settings = new SettingsData();

            Assert.IsTrue(BattleTank.DispersionWanted(settings));

            settings.NoShotDispersion = true;
            Assert.IsFalse(BattleTank.DispersionWanted(settings));
        }

        [Test]
        public void TheShellGravityRowScalesEveryArc()
        {
            var settings = new SettingsData();

            Assert.AreEqual(
                1f, BattleProjectile.ShellGravityScale(settings), 0.0001f);

            settings.ShellGravityMul = 2.5f;
            Assert.AreEqual(
                2.5f, BattleProjectile.ShellGravityScale(settings), 0.0001f);

            settings.ShellGravityMul = 99f;
            Assert.AreEqual(
                5f, BattleProjectile.ShellGravityScale(settings), 0.0001f);
        }

        [Test]
        public void TheDeviationRowsWidenTheDamageAndPenetrationRolls()
        {
            var settings = new SettingsData();

            settings.DmgDeviation = SettingsData.Dev25;
            Assert.AreEqual(
                0.25f, BattleProjectile.DamageDeviationFor(settings), 0.0001f);

            settings.DmgDeviation = SettingsData.Dev0;
            Assert.AreEqual(
                0f, BattleProjectile.DamageDeviationFor(settings), 0.0001f);

            settings.PenDeviation = SettingsData.Dev15;
            Assert.AreEqual(
                0.15f, BattleProjectile.PenetrationDeviationFor(settings), 0.0001f);

            settings.PenDeviation = SettingsData.Dev5;
            Assert.AreEqual(
                0.05f, BattleProjectile.PenetrationDeviationFor(settings), 0.0001f);
        }

        [Test]
        public void TheTeamSizeRowPicksTheSlotCount()
        {
            var settings = new SettingsData();

            settings.MatchmakerTeamSize = SettingsData.TeamSize1x1;
            Assert.AreEqual(1, BattleSession.TeamSizeFor(settings));

            settings.MatchmakerTeamSize = SettingsData.TeamSize10x10;
            Assert.AreEqual(10, BattleSession.TeamSizeFor(settings));

            settings.MatchmakerTeamSize = SettingsData.DefaultMatchmakerTeamSize;
            Assert.AreEqual(7, BattleSession.TeamSizeFor(settings));
        }

        [Test]
        public void TheBattleTimeRowPicksTheBattleClockLimit()
        {
            var settings = new SettingsData();

            settings.BattleMaxTime = SettingsData.Time5;
            Assert.AreEqual(300f, BattleRoster.TimeLimitFor(settings), 0.01f);

            settings.BattleMaxTime = SettingsData.Time7;
            Assert.AreEqual(
                BattleRoster.DefaultTimeLimitSeconds,
                BattleRoster.TimeLimitFor(settings),
                0.01f,
                "the default row has to agree with the roster default");

            settings.BattleMaxTime = SettingsData.Time15;
            Assert.AreEqual(900f, BattleRoster.TimeLimitFor(settings), 0.01f);

            // The roster reads a limit of zero as no countdown.
            settings.BattleMaxTime = SettingsData.TimeInf;
            Assert.AreEqual(0f, BattleRoster.TimeLimitFor(settings), 0.01f);
        }

        [Test]
        public void FullscreenPicksTheWindowMode()
        {
            Assert.AreEqual(
                FullScreenMode.FullScreenWindow, GraphicsQuality.WindowMode(true));
            Assert.AreEqual(
                FullScreenMode.Windowed, GraphicsQuality.WindowMode(false));
        }

        [TestCase(SettingsData.FpsLimit30, 180, SettingsData.FpsLimit30)]
        [TestCase(SettingsData.FpsLimit60, 180, SettingsData.FpsLimit60)]
        [TestCase(SettingsData.FpsLimit90, 180, SettingsData.FpsLimit90)]
        [TestCase(SettingsData.FpsLimit120, 180, SettingsData.FpsLimit120)]
        [TestCase(SettingsData.FpsLimitUncapped, 60, 60)]
        [TestCase(SettingsData.FpsLimitUncapped, 144, 144)]
        [TestCase(SettingsData.FpsLimitUncapped, 180, 180)]
        [TestCase(SettingsData.FpsLimitUncapped, 0, 60)]
        [TestCase(SettingsData.FpsLimitUncapped, 1200, 1000)]
        public void InfinityUsesMaximumDisplayRateAndNumericCapsStayExact(
            int savedLimit, int refreshRate, int expected)
        {
            Assert.AreEqual(expected,
                GraphicsQuality.TargetFrameRate(savedLimit, refreshRate));
        }

        [Test]
        public void MaximumRefreshComesFromSupportedCurrentResolutionNotPreferredMode()
        {
            var current = new GraphicsQuality.DisplayModeInfo(2560, 1440, 60d);
            var supported = new[]
            {
                new GraphicsQuality.DisplayModeInfo(2560, 1440, 60d),
                new GraphicsQuality.DisplayModeInfo(2560, 1440, 120d),
                new GraphicsQuality.DisplayModeInfo(2560, 1440, 144d),
                new GraphicsQuality.DisplayModeInfo(2560, 1440, 180d),
                new GraphicsQuality.DisplayModeInfo(1920, 1080, 240d),
            };

            Assert.AreEqual(180,
                GraphicsQuality.MaximumRefreshRate(current, supported),
                "a faster mode at another resolution must not override native 180 Hz");
            Assert.AreEqual(120, GraphicsQuality.MaximumRefreshRate(
                new GraphicsQuality.DisplayModeInfo(1080, 2400, 60d),
                new[] { new GraphicsQuality.DisplayModeInfo(2400, 1080, 120d) }),
                "phone orientation may transpose the native dimensions");
            Assert.AreEqual(144, GraphicsQuality.MaximumRefreshRate(
                new GraphicsQuality.DisplayModeInfo(111, 222, 0d),
                new[] { new GraphicsQuality.DisplayModeInfo(1920, 1080, 144d) }),
                "all supported modes are the fallback when dimensions are absent");
            Assert.AreEqual(75, GraphicsQuality.MaximumRefreshRate(
                new GraphicsQuality.DisplayModeInfo(1920, 1080, 75d), null));
            Assert.AreEqual(60, GraphicsQuality.MaximumRefreshRate(
                new GraphicsQuality.DisplayModeInfo(0, 0, 0d), null));
        }

        [Test]
        public void XrandrParserUsesActivePrimaryOutput()
        {
            const string modes =
                "Screen 0: current 2560 x 1440\n"
                + "DP-1 connected primary 2560x1440+0+0\n"
                + "   2560x1440    60.00+ 179.94*\n"
                + "HDMI-0 connected 3840x2160+2560+0\n"
                + "   3840x2160    239.76*+ 120.00\n";
            Assert.AreEqual(180,
                GraphicsQuality.ParseXrandrActiveRefreshRate(modes),
                "the faster secondary display must not cap the primary window");

            const string noPrimary =
                "DP-1 connected 1920x1080+0+0\n"
                + "   1920x1080    74.97*+ 60.00\n"
                + "HDMI-0 connected 2560x1440+1920+0\n"
                + "   2560x1440    240.00*+\n";
            Assert.AreEqual(75,
                GraphicsQuality.ParseXrandrActiveRefreshRate(noPrimary),
                "without a primary marker the first connected output wins");
            Assert.AreEqual(0,
                GraphicsQuality.ParseXrandrActiveRefreshRate("not xrandr output"));
            Assert.AreEqual(0,
                GraphicsQuality.ParseXrandrActiveRefreshRate(string.Empty));
        }

#if UNITY_EDITOR_LINUX
        [Test]
        public void LinuxDisplayProbeReturnsAnActiveRateWhenDisplayIsAvailable()
        {
            if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("DISPLAY")))
            {
                Assert.Ignore("headless Linux has no XRandR display");
            }

            var detected = GraphicsQuality.LinuxDisplayRefreshRate();
            Debug.Log("[Blitz Test] XRandR active refresh=" + detected);
            Assert.Greater(detected, 0);
        }
#endif

        [TestCase(SettingsData.FpsLimitUncapped, true, true, 0)]
        [TestCase(SettingsData.FpsLimitUncapped, false, true, 0)]
        [TestCase(SettingsData.FpsLimitUncapped, true, false, 1)]
        [TestCase(SettingsData.FpsLimitUncapped, false, false, 0)]
        [TestCase(SettingsData.FpsLimit60, true, false, 0)]
        [TestCase(SettingsData.FpsLimit60, false, false, 0)]
        public void NumericCapAndEditorPacerDoNotCompeteWithVSync(
            int savedLimit, bool savedVsync, bool isEditor, int expected)
        {
            Assert.AreEqual(expected,
                GraphicsQuality.VSyncCount(savedLimit, savedVsync, isEditor));
        }

        [Test]
        public void TheGraphicsSettingsReachTheEngine()
        {
            var settings = new SettingsData
            {
                GraphicsMsaa = SettingsData.Msaa4X,
                GraphicsShadows = SettingsData.ShadowsOff,
                GraphicsShadowDistance = 123f,
                GraphicsTextureQuality = SettingsData.TextureQualityHalf,
                GraphicsAnisotropic = SettingsData.AnisoForced,
                GraphicsVsync = false,
                FpsLimit = SettingsData.FpsLimit30,
            };

            GraphicsQuality.Bind(settings);
            GraphicsQuality.Apply();

            Assert.AreEqual(SettingsData.Msaa4X, QualitySettings.antiAliasing);
            Assert.AreEqual(ShadowQuality.Disable, QualitySettings.shadows);
            Assert.AreEqual(123f, QualitySettings.shadowDistance, 0.01f);
            Assert.AreEqual(
                SettingsData.TextureQualityHalf, QualitySettings.globalTextureMipmapLimit);
            Assert.AreEqual(
                AnisotropicFiltering.ForceEnable, QualitySettings.anisotropicFiltering);
            Assert.AreEqual(0, QualitySettings.vSyncCount);
            Assert.AreEqual(SettingsData.FpsLimit30, Application.targetFrameRate);

            settings.GraphicsShadows = SettingsData.ShadowsSoft;
            settings.GraphicsTextureQuality = SettingsData.TextureQualityFull;
            settings.GraphicsVsync = true;
            GraphicsQuality.Apply();

            Assert.AreEqual(ShadowQuality.All, QualitySettings.shadows);
            Assert.AreEqual(0, QualitySettings.globalTextureMipmapLimit);
            Assert.AreEqual(0, QualitySettings.vSyncCount,
                "the explicit 30 FPS cap must win over the vSync row");

            settings.FpsLimit = SettingsData.FpsLimitUncapped;
            GraphicsQuality.ApplyFrameRate();
            var maximum = GraphicsQuality.MaximumDisplayRefreshRate();
            Assert.AreEqual(maximum, Application.targetFrameRate,
                "infinity must bind the detected display maximum, not Unity's preferred 60 Hz mode");
        }

        [TestCase(SettingsData.MsaaOff, false)]
        [TestCase(SettingsData.Msaa2X, false)]
        [TestCase(SettingsData.Msaa4X, false)]
        [TestCase(SettingsData.Msaa8X, false)]
        [TestCase(SettingsData.MsaaOff, true)]
        [TestCase(SettingsData.Msaa2X, true)]
        [TestCase(SettingsData.Msaa4X, true)]
        [TestCase(SettingsData.Msaa8X, true)]
        public void GraphicsFixtureRestoresAuthoredStateNotDefaults(int samples, bool previouslyBound)
        {
            var before = previouslyBound ? new SettingsData { GraphicsMsaa = SettingsData.Msaa2X } : null;
            GraphicsQuality.Bind(before);
            QualitySettings.antiAliasing = samples;
            QualitySettings.shadows = ShadowQuality.HardOnly;
            QualitySettings.shadowDistance = 73.5f;
            QualitySettings.globalTextureMipmapLimit = 1;
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 37;
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline != null)
            {
                pipeline.msaaSampleCount = samples == SettingsData.MsaaOff ? 1 : samples;
                pipeline.renderScale = 0.75f;
                pipeline.supportsHDR = false;
                pipeline.shadowDistance = 73.5f;
            }

            var fixture = new SettingsWiringTests();
            fixture.SetUp();
            try { fixture.TheGraphicsSettingsReachTheEngine(); }
            finally { fixture.TearDown(); }

            Assert.AreEqual(samples, QualitySettings.antiAliasing, "AA before the fixture is authoritative, not a new SettingsData default");
            Assert.AreEqual(ShadowQuality.HardOnly, QualitySettings.shadows);
            Assert.AreEqual(73.5f, QualitySettings.shadowDistance);
            Assert.AreEqual(1, QualitySettings.globalTextureMipmapLimit);
            Assert.AreEqual(AnisotropicFiltering.Disable, QualitySettings.anisotropicFiltering);
            Assert.AreEqual(0, QualitySettings.vSyncCount);
            Assert.AreEqual(37, Application.targetFrameRate);
            Assert.AreSame(before, GraphicsQuality.Settings, "an originally unbound fixture must remain unbound");
            if (pipeline != null)
            {
                Assert.AreSame(pipeline, GraphicsSettings.currentRenderPipeline);
                Assert.AreEqual(samples == SettingsData.MsaaOff ? 1 : samples, pipeline.msaaSampleCount);
                Assert.AreEqual(0.75f, pipeline.renderScale);
                Assert.IsFalse(pipeline.supportsHDR);
                Assert.AreEqual(73.5f, pipeline.shadowDistance);
            }
        }

        // ------------------------------------------------------------------

        /// <summary>Which saved field each row on the screen writes.</summary>
        private static Dictionary<string, string> Reachable()
        {
            var settings = new SettingsData();
            var map = new Dictionary<string, string>();

            foreach (var row in SettingsScreen.SettingRow.Load())
            {
                var before = Snapshot(settings);
                Move(settings, row);
                foreach (var name in Changed(before, Snapshot(settings)))
                {
                    map[name] = row.Setting;
                }
            }

            return map;
        }

        /// <summary>
        /// Moves a row off whatever it currently holds, exactly the way the
        /// screen moves it: a box flips, an enum picks another member and a
        /// slider runs to the far end of its own range.
        /// </summary>
        private static void Move(SettingsData settings, SettingsScreen.SettingRow row)
        {
            switch (row.Kind)
            {
                case "boolean":
                    settings.SetBool(row.Setting, !settings.GetBool(row.Setting));
                    return;

                case "slider":
                    var current = settings.GetFloat(row.Setting);
                    var far = Mathf.Abs(current - row.Min) > 0.001f ? row.Min : row.Max;
                    settings.SetFloat(row.Setting, far);
                    return;

                case "enum":
                    foreach (var option in row.Options)
                    {
                        if (row.IsString)
                        {
                            if (settings.GetString(row.Setting) == option.Text) { continue; }
                            settings.SetString(row.Setting, option.Text);
                            return;
                        }

                        if (settings.GetInt(row.Setting) == option.Value) { continue; }
                        settings.SetInt(row.Setting, option.Value);
                        return;
                    }

                    Assert.Fail(row.Setting + " is an enum row with nothing to pick");
                    return;

                case "hangar":
                    // The picker lists the catalog after the automatic choice.
                    foreach (var info in HangarCatalog.All)
                    {
                        if (settings.GetString(row.Setting) == info.Id) { continue; }
                        settings.SetString(row.Setting, info.Id);
                        return;
                    }

                    Assert.Fail(row.Setting + " is a hangar row with no hangar to pick");
                    return;

                case "hangar_music":
                    // The box flips the music player of the hangar the picker shows.
                    var picked = HangarCatalog.Find(settings.Hangar);
                    var kind = picked != null ? picked.Kind : HangarCatalog.All[0].Kind;
                    settings.SetHangarMusicPlayer(kind, !settings.HangarMusicPlayer(kind));
                    return;

                default:
                    Assert.Fail(row.Setting + " has an unknown row kind " + row.Kind);
                    return;
            }
        }

        private static IEnumerable<FieldInfo> Fields()
        {
            var fields = typeof(SettingsData).GetFields(
                BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                yield return field;
            }
        }

        private static Dictionary<string, object> Snapshot(SettingsData settings)
        {
            var values = new Dictionary<string, object>();
            foreach (var field in Fields())
            {
                values[field.Name] = field.GetValue(settings);
            }

            // The per-hangar music player switches sit behind methods, not a field.
            values["hangar_music_player"] = settings.ToJson()["hangar_music_player"].ToJson(false);
            return values;
        }

        private static List<string> Changed(
            Dictionary<string, object> before, Dictionary<string, object> after)
        {
            var moved = new List<string>();
            foreach (var pair in before)
            {
                if (!Equals(pair.Value, after[pair.Key])) { moved.Add(pair.Key); }
            }

            return moved;
        }

        /// <summary>Every script that could be reading a setting.</summary>
        private static Dictionary<string, string> Sources()
        {
            var root = Path.Combine(Application.dataPath, "Blitz");
            var sources = new Dictionary<string, string>();

            foreach (var path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var normalized = path.Replace('\\', '/');
                var skip = false;
                foreach (var exclude in NotReaders)
                {
                    if (normalized.Contains(exclude)) { skip = true; break; }
                }

                if (skip) { continue; }

                sources[normalized] = File.ReadAllText(path);
            }

            Assert.Greater(sources.Count, 0, "no scripts to read");
            return sources;
        }
    }
}
