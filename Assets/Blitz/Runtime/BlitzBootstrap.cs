using System;
using System.Collections;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Time;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Lighting;
using BlitzLegacy.Runtime.Platform;
using BlitzLegacy.Runtime.Save;
using BlitzLegacy.Runtime.Telemetry;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;

namespace BlitzLegacy.Runtime
{
    /// <summary>
    /// Persistent code-first entry point and the ported hangar -> queue -> map
    /// intro -> countdown -> battle -> result state machine.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BlitzBootstrap : MonoBehaviour
    {
        public static BlitzBootstrap Current { get; private set; }

        public SaveService Saves { get; private set; }
        public AccountBook Accounts { get; private set; }
        public SettingsData Settings { get; private set; }

        /// <summary>
        /// What the battle simulation reads for its rules. Online every peer
        /// plays by <see cref="SettingsData.StandardRules"/>, whatever the
        /// player set up for offline battles.
        /// </summary>
        public SettingsData BattleRules
        {
            get { return Battle != null && Battle.Net != null ? StandardRules : Settings; }
        }

        private static readonly SettingsData StandardRules = SettingsData.StandardRules();
        public IClock Clock { get; private set; }

        /// <summary>
        /// Replaces the wall clock (the system clock again for null). Smoke
        /// runs pin it so dated content, such as the seasonal hangar, shows
        /// the same on every run.
        /// </summary>
        public void UseClock(IClock clock)
        {
            Clock = clock ?? SystemClock.Instance;
        }

        /// <summary>
        /// Network time for the online-only shop: the weekly tank rotation
        /// never trusts the device clock.
        /// </summary>
        public OnlineClock NetworkClock { get; private set; }
        public AudioService Audio { get; private set; }

        /// <summary>The hangar music player; it outlives the hangars that attach it.</summary>
        public HangarMusicPlayer Music { get; private set; }
        public HangarScreen Hangar { get; private set; }
        public BattleMode SelectedMode { get; set; } = BattleMode.Normal;

        /// <summary>The hangar's mode card "online": the battle button queues for real players.</summary>
        public bool OnlineSelected { get; set; }
        public BattleSession Battle { get; private set; }
        public BattleQueueScreen Queue { get; private set; }
        public LoadingOverlay Loading { get; private set; }
        public PleaseWaitScreen PleaseWait { get; private set; }
        public BattleLoadingScreen MapIntro { get; private set; }
        public BattleCountdownScreen Countdown { get; private set; }
        public BattleResultScreen Result { get; private set; }
        public BattleResultData LastBattleResult { get; private set; }
        public AccountStatisticsScreen Statistics { get; private set; }

        /// <summary>
        /// The hangar carousel's filter. The session owns it, not the hangar:
        /// the hangar is built anew after every battle and style switch, and
        /// the tiers, classes and nations picked in it stay picked.
        /// </summary>
        public CarouselFilter CarouselFilter
        {
            get { return _carouselFilter; }
        }

        private readonly CarouselFilter _carouselFilter = new CarouselFilter();
        private HangarScreen _queuedHangar;
        private Coroutine _transition;
        private ISaveStorage _storage;
        private SandboxPresetService _sandboxPresets;

        /// <summary>
        /// The Sandbox preset library, loaded from its own save file on first
        /// use (Global._load_training_presets).
        /// </summary>
        public SandboxPresetService SandboxPresets
        {
            get
            {
                if (_sandboxPresets == null && _storage != null)
                {
                    var lang = LocalizationTable.Parse(BlitzResources.LoadText("lang"));
                    lang.Language = Settings != null ? Settings.Language : "en";
                    _sandboxPresets = SandboxPresetService.Load(_storage, lang);
                }
                return _sandboxPresets;
            }
        }

        private void Awake()
        {
            if (Current != null && Current != this)
            {
                Destroy(gameObject);
                return;
            }
            Current = this;
            DontDestroyOnLoad(gameObject);

            Clock = SystemClock.Instance;
            NetworkClock = new OnlineClock(new NtpTimeSource());
            // SaveSettings.is_mobile_platform(): several source defaults read
            // it, so it has to be known before the first SettingsData exists.
            SettingsData.MobilePlatform = MouseLock.IsMobile;
            // Encrypted and bound to this device; see SecureSaves.
            var storage = SecureSaves.Saves;
            _storage = storage;
            Saves = new SaveService(storage);
            Settings = Saves.LoadSettings();
            Accounts = Saves.LoadBook();

            // The graphics tab writes into the same save, and the hangar is
            // built moments from here, so the chosen quality has to be on the
            // pipeline before the first frame is drawn.
            GraphicsQuality.Bind(Settings);
            GraphicsQuality.ApplyWindow();
            ApplyFpsLimit();
            MainThreadPriority.Attach(transform);

            Debug.Log("[Blitz] bootstrap: graphics=" + SystemInfo.graphicsDeviceType
                + " accounts=" + Accounts.Count + " fps=" + Application.targetFrameRate
                + " refresh=" + Mathf.RoundToInt((float)Screen.currentResolution.refreshRateRatio.value)
                + "/" + GraphicsQuality.MaximumDisplayRefreshRate()
                + " saves=" + SecureSaves.Root);

            // Diagnostics read their own opt-in file next to the saves, so
            // they can be armed before the first screen exists and stay silent
            // until the player has agreed.
            BlitzTelemetry.Initialize(SecureSaves.Preferences);
            TelemetryRuntime.Attach(transform);
            BlitzPerfMonitor.AttachIfRequested(transform);
            // After telemetry: a caught memory edit reports through it.
            Security.AntiCheatResponder.Attach(transform);
            BlitzTelemetry.MarkStage("startup", "login");
            // Before anything reads a mod: one that crashed the last run while
            // loading is switched off here.
            Mods.ModRuntime.Boot();

            Audio = AudioService.Create(transform, Settings);
            Music = HangarMusicPlayer.Create(transform, this);

            if (ShaderWarmup.Needed)
            {
                StartCoroutine(WarmThenBoot());
                return;
            }

            BootScreens();
        }

        private IEnumerator WarmThenBoot()
        {
            var screen = ShaderWarmupScreen.Create(transform, MouseLock.IsMobile);
            yield return null;
            yield return null;
            yield return StartCoroutine(ShaderWarmup.Run(screen.SetProgress));
            ShaderWarmup.MarkDone();
            while (!screen.Filled) { yield return null; }
            screen.Close();
            BootScreens();
        }

        private void BootScreens()
        {
            // save_settings "SKIP LOGIN SCREEN". The row was on the screen and
            // in the save, but nothing ever read it, so the login screen came
            // up either way. With an account already chosen there is nothing
            // to ask for, so the boot walks straight into the hangar.
            if (SkipsLogin(Settings, Accounts))
            {
                BlitzTelemetry.MarkStage("startup", "hangar");
                Accounts.LogInToCurrentAccount(Clock.NowUnixSeconds);
                Persist();
                EnterHangar(null);
                return;
            }

            LoginScreen.Create(this);
        }

        /// <summary>
        /// Whether the login screen can be skipped at boot. Without a current
        /// account there is nothing to skip to, and after LOGOUT the screen
        /// opens until the player signs in again.
        /// </summary>
        public static bool SkipsLogin(SettingsData settings, AccountBook accounts)
        {
            return settings != null && settings.SkipLogin && accounts != null &&
                !accounts.LoggedOut && accounts.GetCurrentAccount() != null;
        }

        /// <summary>
        /// SaveSettings.apply_fps_limit: Engine.max_fps = fps_limit, where
        /// FPS_LIMIT_UNCAPPED (0) means no cap. Unity spells the cap as
        /// Application.targetFrameRate. Infinity resolves to the fastest mode
        /// of the current monitor or phone; a chosen numerical cap switches
        /// vSync off so 30/60/90/120 cannot be replaced by the display rate.
        /// </summary>
        public void ApplyFpsLimit()
        {
            // GraphicsQuality owns display-mode detection as well as the cap;
            // do not independently read currentResolution here because Unity
            // can report the 60 Hz preferred mode on a 180 Hz active display.
            GraphicsQuality.ApplyFrameRate();
        }

        private void OnDestroy()
        {
            if (Current == this)
            {
                Current = null;
            }
        }

        public void EnterHangar(LoginScreen login)
        {
            if (Hangar != null)
            {
                return;
            }
            var account = Accounts.GetCurrentAccount();
            if (account == null)
            {
                Debug.LogWarning("[Blitz] hangar transition rejected: no current account");
                return;
            }
            if (string.IsNullOrEmpty(account.SelectedTankName))
            {
                account.SelectedTankName = BattleSession.DefaultTankId;
                account.SelectedTankId = 0;
                Persist();
            }
            if (login != null)
            {
                Destroy(login.gameObject);
            }
            // A new sign-in opens the whole garage.
            _carouselFilter.Clear();
            Hangar = HangarScreen.Create(this, account);
            ShowStylePickerIfNeeded();
            BlitzTelemetry.MarkStage("hangar", "hangar");
            Debug.Log("[Blitz] transition login -> hangar: tank=ms-1");
        }

        /// <summary>
        /// btn_logout once its popup is confirmed (Global.to_login): the
        /// hangar goes and the login screen opens, still on the same account.
        /// The account keeps its login and password; the session ends, so
        /// SKIP LOGIN SCREEN waits for the next sign-in. Refused while a
        /// queue or a battle owns the screen.
        /// </summary>
        public void LogOut(HangarScreen hangar)
        {
            if (hangar == null || Queue != null || Battle != null || _transition != null)
            {
                return;
            }

            CloseResult();
            CloseAccountStatistics();
            Accounts.LogOut();
            Persist();
            if (hangar == Hangar)
            {
                Hangar = null;
            }

            hangar.Discard();
            LoginScreen.Create(this);
            BlitzTelemetry.MarkStage("logged_out", "login");
            Debug.Log("[Blitz] transition hangar -> login: logged out");
        }

        /// <summary>The first-run menu style picker while it is up, or null.</summary>
        public StylePickerScreen StylePicker { get; private set; }

        /// <summary>
        /// Rebuilds the hangar in place, e.g. after the menu style or the
        /// carousel size changed in Settings. Refused while a queue or a
        /// battle owns the screen.
        /// </summary>
        public void RebuildHangar()
        {
            if (Hangar == null || Queue != null || Battle != null)
            {
                return;
            }

            var account = Accounts.GetCurrentAccount();
            if (account == null)
            {
                return;
            }

            var old = Hangar;
            Hangar = null;
            old.Discard();
            Hangar = HangarScreen.Create(this, account);
            Debug.Log("[Blitz] hangar rebuilt: style=" + (Hangar.IsModern ? "modern" : "classic"));
        }

        /// <summary>
        /// Offers the Classic/Modern choice once, over the freshly built
        /// hangar. Batch-mode tools and smokes never see it: they pin the
        /// style with BLITZ_UI_STYLE or play the classic default.
        /// </summary>
        private void ShowStylePickerIfNeeded()
        {
            if (StylePicker != null || Hangar == null || Application.isBatchMode || !MenuStyle.NeedsPicker)
            {
                return;
            }

            Hangar.SetAccountOverlayVisible(true);
            StylePicker = StylePickerScreen.Create(transform, Settings.Language, style =>
            {
                StylePicker = null;
                Settings.UiStyle = style;
                Persist();
                Debug.Log("[Blitz] menu style chosen: " + (style == SettingsData.UiStyleModern ? "modern" : "classic"));
                if (Hangar == null)
                {
                    return;
                }

                if (Hangar.IsModern != MenuStyle.IsModern)
                {
                    RebuildHangar();
                }
                else
                {
                    Hangar.SetAccountOverlayVisible(false);
                }
            });
        }

        /// <summary>enter_queue from ui_hangar.tscn.</summary>
        public void EnterBattle(HangarScreen hangar)
        {
            if (Battle != null || Queue != null || _transition != null)
            {
                return;
            }
            var source = hangar != null ? hangar : Hangar;
            var account = Accounts.GetCurrentAccount();
            if (source == null || account == null)
            {
                Debug.LogWarning("[Blitz] queue transition rejected: hangar/account missing");
                return;
            }
            if (SelectedMode == BattleMode.BigBoss)
            {
                var tanks = BlitzLegacy.Core.Tanks.TanksDatabase.Parse(BlitzResources.LoadText("tanks"));
                var selected = tanks.Get(account.SelectedTankName);
                if (selected == null || !BigBossRules.EligibleTier(selected.Level))
                {
                    source.ShowNotice("battle_mode:bossmode/name", "#arenas:type/bossmode/description");
                    return;
                }
            }
            if (SelectedMode == BattleMode.Sandbox)
            {
                // Sandbox is the only direct-launch context: no queue and
                // never online (Global.battle_uses_matchmaking_queue).
                EnterSandbox(source, null);
                return;
            }
            _queuedHangar = source;
            // War of Ducks is an offline event: its queue never goes online.
            if (OnlineSelected && BattleOnline.Available && BattleModeCatalog.AllowsOnline(SelectedMode))
            {
                Queue = BattleQueueScreen.CreateOnline(
                    transform, account, OnOnlineCancelled, OnOnlineFailed, SelectedMode);
                BlitzTelemetry.MarkStage("battle_queue", "online");
                Debug.Log("[Blitz] transition hangar -> online queue");
                source.SetQueueMode(true);
                BattleOnline.Begin(Queue, SelectedMode);
                return;
            }
            Queue = BattleQueueScreen.Create(transform, account,
                OnQueueMatched, OnQueueCancelled, SelectedMode);
            source.SetQueueMode(true);
            BlitzTelemetry.MarkStage("battle_queue", "queue");
            Debug.Log("[Blitz] transition hangar -> queue");
        }

        /// <summary>
        /// ui_hangar._launch_training_map: starts the active Sandbox preset
        /// offline, straight into the map intro. <paramref name="mapId"/>
        /// overrides the preset's map; null plays the preset's choice (or a
        /// random map for RANDOM). Returns false while another transition runs.
        /// </summary>
        public bool EnterSandbox(HangarScreen hangar, string mapId)
        {
            if (Battle != null || Queue != null || _transition != null)
            {
                return false;
            }
            var source = hangar != null ? hangar : Hangar;
            var presets = SandboxPresets;
            if (source == null || presets == null || Accounts.GetCurrentAccount() == null)
            {
                Debug.LogWarning("[Blitz] sandbox transition rejected: hangar/account/presets missing");
                return false;
            }
            SelectedMode = BattleMode.Sandbox;
            OnlineSelected = false;
            presets.Save();
            var sandbox = new SandboxBattle(presets.Active);
            var map = presets.Contains(mapId) ? mapId : presets.PickMap(sandbox.Config, new UnityRandomSource());
            _queuedHangar = source;
            _transition = StartCoroutine(BuildBattleFlow(null, sandbox, map));
            BlitzTelemetry.MarkStage("battle_queue", "sandbox");
            Debug.Log("[Blitz] transition hangar -> sandbox: map=" + map
                + " base=" + sandbox.BaseMode);
            return true;
        }

        private void OnOnlineCancelled()
        {
            BattleOnline.Cancel();
            OnQueueCancelled();
        }

        private void OnOnlineFailed(string reasonKey)
        {
            Debug.LogWarning("[Blitz] online queue failed: " + reasonKey);
            var hangar = _queuedHangar != null ? _queuedHangar : Hangar;
            OnQueueCancelled();
            if (hangar != null) { hangar.ShowNotice("online:error/title", reasonKey); }
        }

        private void OnQueueCancelled()
        {
            if (Queue != null)
            {
                Destroy(Queue.gameObject);
                Queue = null;
            }
            var queued = _queuedHangar != null ? _queuedHangar : Hangar;
            if (queued != null) { queued.SetQueueMode(false); }
            _queuedHangar = null;
            BlitzTelemetry.MarkStage("queue_cancelled", "hangar");
            Debug.Log("[Blitz] queue cancelled -> hangar");
        }

        private void OnQueueMatched()
        {
            if (_transition != null || Battle != null)
            {
                return;
            }
            if (Queue != null)
            {
                Destroy(Queue.gameObject);
                Queue = null;
            }
            _transition = StartCoroutine(BuildBattleFlow());
        }

        /// <summary>Raised once a battle is built, before its map intro.</summary>
        public event System.Action<BattleSession> BattleBuilt;

        /// <summary>
        /// Starts a networked battle straight from the hangar, without the
        /// offline queue: the host seats its guests, a client seats the host's
        /// plan. Returns false while another transition is running.
        /// </summary>
        public bool EnterNetBattle(BattleNetSetup net)
        {
            if (net == null || Battle != null || _transition != null)
            {
                return false;
            }
            if (BattleModeCatalog.OfflineOnly(net.Mode))
            {
                // Hard ban: the Sandbox and War of Ducks never get an online seat.
                Debug.LogWarning("[Blitz] net battle rejected: offline-only mode " + BattleModeCatalog.Id(net.Mode));
                return false;
            }
            if (Queue != null)
            {
                // The online queue hands over to the battle it found.
                if (!Queue.Online) { return false; }
                Destroy(Queue.gameObject);
                Queue = null;
            }
            if (_queuedHangar == null) { _queuedHangar = Hangar; }
            _transition = StartCoroutine(BuildBattleFlow(net));
            Debug.Log("[Blitz] transition hangar -> net battle (" + (net.IsClient ? "client" : "host") + ")");
            return true;
        }

        private IEnumerator BuildBattleFlow(BattleNetSetup net = null,
            SandboxBattle sandbox = null, string sandboxMap = null)
        {
            Loading = LoadingOverlay.Create(transform, false, false, 500);
            // Godot waits for two physics frames before changing the map and
            // raising the global overlay. Two Unity frames preserve that order
            // without relying on EndOfFrame, which is unavailable in some
            // headless/player smoke runners.
            yield return null;
            yield return null;

            var source = _queuedHangar != null ? _queuedHangar : Hangar;
            if (source != null)
            {
                source.gameObject.SetActive(false);
            }
            var account = Accounts.GetCurrentAccount();
            var playerName = account == null ? string.Empty : account.Name;
            var selectedTankId = account == null || string.IsNullOrEmpty(account.SelectedTankName)
                ? BattleSession.DefaultTankId : account.SelectedTankName;
            var playerTank = account == null ? null : account.GetTank(account.SelectedTankName);
            if (net != null && net.Dedicated && net.Anchor != null && net.Anchor.Hello != null)
            {
                // Dedicated server: the host slot belongs to a remote human,
                // seated exactly like any guest, from its hello.
                var hello = net.Anchor.Hello;
                playerName = string.IsNullOrEmpty(hello.Name) ? "Player" + net.Anchor.Owner : hello.Name;
                selectedTankId = string.IsNullOrEmpty(hello.TankId) ? BattleSession.DefaultTankId : hello.TankId;
                playerTank = DedicatedSeatTank(hello);
            }
            var mapId = sandbox != null && !string.IsNullOrEmpty(sandboxMap)
                ? sandboxMap
                : BattleMapCatalog.PickRandomForTank(selectedTankId, new UnityRandomSource());
            var build = BattleSession.CreateSteps(
                transform, Application.isMobilePlatform,
                built => Battle = built,
                mapId,
                selectedTankId,
                false, playerName,
                playerTank,
                mode: net != null ? net.Mode : (sandbox != null ? BattleMode.Sandbox : SelectedMode),
                net: net,
                sandbox: sandbox);
            while (build.MoveNext())
            {
                yield return build.Current;
            }
            Battle.Finished += OnBattleFinished;
            BattleBuilt?.Invoke(Battle);

            if (source != null)
            {
                Destroy(source.gameObject);
            }
            Hangar = null;
            _queuedHangar = null;
            yield return null;

            // maps/loading_screen.tscn covers the whole window while the map
            // finishes loading, so the scene is warmed up behind the intro
            // instead of hitching on the first shot of the battle: every
            // material the map and the tanks brought in is compiled now, and
            // the pointer is captured the way Global does when a battle takes
            // over the screen. The control layer stays off until the intro is
            // done, so the capture cannot turn the camera yet.
            MouseLock.Set(true);
            yield return null;

            MapIntro = BattleLoadingScreen.Create(
                transform, Battle, Application.isMobilePlatform, OnIntroComplete);
            // The queue overlay only goes away once the intro curtain exists,
            // so no frame can show the battle UI on the black screen between
            // the two.
            if (Loading != null)
            {
                Destroy(Loading.gameObject);
                Loading = null;
            }
            yield return null;
            // Keep the actual battle cameras rendering behind the opaque intro
            // for one more frame. That compiles the map/tank variants in their
            // own material keyword spaces. Shader.WarmupAllShaders cannot be
            // used here: Unity 6.6 mixes URP Particles/Unlit and Particles/Lit
            // keyword spaces and binds a non-MS texture to an MS sampler in
            // that global path, producing native assertions before the battle.
            yield return null;
            _transition = null;
            BlitzTelemetry.MarkStage("map_intro", "battle", new System.Collections.Generic.Dictionary<string, string>
            {
                { "map", Battle.World.Map.Id },
                { "players", Battle.Participants.Count.ToString() },
            });
            Debug.Log("[Blitz] queue -> map intro: map=" + Battle.World.Map.Id
                + " players=" + Battle.Participants.Count);
        }

        private void OnIntroComplete()
        {
            if (MapIntro != null)
            {
                Destroy(MapIntro.gameObject);
                MapIntro = null;
            }
            if (Battle == null)
            {
                return;
            }
            // The map is loaded now, so the battle UI (crosshair included)
            // becomes visible exactly when the source countdown starts, and
            // the stick, the buttons and the look inputs come back with it.
            if (Battle.Hud != null)
            {
                Battle.Hud.SetVisible(true);
            }
            Battle.SetInputActive(true);
            Countdown = BattleCountdownScreen.Create(
                transform, Application.isMobilePlatform, OnCountdownComplete);
        }

        private void OnCountdownComplete()
        {
            if (Countdown != null)
            {
                Destroy(Countdown.gameObject);
                Countdown = null;
            }
            if (Battle != null)
            {
                Battle.StartBattle();
                BlitzTelemetry.MarkStage("battle_live", "battle");
                Debug.Log("[Blitz] countdown -> live battle");
            }
        }

        /// <summary>
        /// Global.to_hangar: the pause menu's confirmed exit. please_wait goes
        /// up, the crew queue is dropped, the running battle is torn down and
        /// the hangar is rebuilt. There is no result screen on this path - the
        /// source never reaches _battle.gd's finish flow - and nothing is
        /// awarded, because Battle.status only moves to FINISHING.
        /// </summary>
        public void ToHangar()
        {
            if (Battle == null || _transition != null)
            {
                return;
            }

            _transition = StartCoroutine(ToHangarFlow(Battle));
        }

        private IEnumerator ToHangarFlow(BattleSession battle)
        {
            PleaseWait = PleaseWaitScreen.Create(transform, true,
                Application.isMobilePlatform, 500);
            if (Audio != null)
            {
                Audio.ClearSounds();
            }

            // to_hangar can be confirmed while the map intro or the countdown
            // still owns the screen. The source drops the whole battle branch
            // there, so neither plate may survive into the hangar and neither
            // timer may keep running behind it.
            DropFlowScreens();

            // Util.mouse_lock(false) at the end of to_hangar.
            MouseLock.Set(false);
            yield return null;

            battle.Finished -= OnBattleFinished;
            Destroy(battle.gameObject);
            Battle = null;
            yield return null;

            var account = Accounts.GetCurrentAccount();
            if (account != null)
            {
                Hangar = HangarScreen.Create(this, account);
            }

            if (PleaseWait != null)
            {
                Destroy(PleaseWait.gameObject);
                PleaseWait = null;
            }

            _transition = null;
            BlitzTelemetry.MarkStage("battle_left", "hangar");
            Debug.Log("[Blitz] battle -> hangar: left from the pause menu");
        }

        private void OnBattleFinished(BattleSession battle)
        {
            if (battle == null || battle != Battle || _transition != null)
            {
                return;
            }
            _transition = StartCoroutine(battle.Sandbox != null
                ? FinishSandboxFlow(battle) : FinishBattleFlow(battle));
        }

        /// <summary>
        /// _battle.gd's finish timers with Global.battle_shows_result() false:
        /// the Sandbox is battle-local, so after the same 5 s + 1.8 s +
        /// 0.2 s beat the player is back in the hangar with no result, no
        /// rewards and no account write; only the preset library is saved.
        /// </summary>
        private IEnumerator FinishSandboxFlow(BattleSession battle)
        {
            ReleaseFinishedBattlePointer(battle);
            DropFlowScreens();
            yield return new WaitForSecondsRealtime(FlowDelay(5f));
            yield return new WaitForSecondsRealtime(FlowDelay(1.8f));
            PleaseWait = PleaseWaitScreen.Create(transform, true,
                Application.isMobilePlatform, 500);
            yield return new WaitForSecondsRealtime(FlowDelay(0.2f));
            if (Audio != null)
            {
                Audio.ClearSounds();
            }

            battle.Finished -= OnBattleFinished;
            Destroy(battle.gameObject);
            Battle = null;
            yield return null;

            if (_sandboxPresets != null)
            {
                _sandboxPresets.Save();
            }
            var account = Accounts.GetCurrentAccount();
            if (account != null)
            {
                Hangar = HangarScreen.Create(this, account);
            }
            if (PleaseWait != null)
            {
                Destroy(PleaseWait.gameObject);
                PleaseWait = null;
            }
            // The hangar is a menu: whatever ran during the finish beat, the
            // pointer must be free once it is on screen.
            MouseLock.Set(false);
            _transition = null;
            BlitzTelemetry.MarkStage("battle_left", "hangar");
            Debug.Log("[Blitz] sandbox battle -> hangar: no result, no rewards");
        }

        /// <summary>The loadout a dedicated server seats its anchor human with.</summary>
        private static TankSaveData DedicatedSeatTank(BattleNetHello hello)
        {
            var tank = TankSaveData.WithName(hello.TankId);
            tank.GunId = System.Math.Max(0, hello.Gun);
            tank.EngineId = System.Math.Max(0, hello.Engine);
            tank.TrackId = System.Math.Max(0, hello.Tracks);
            tank.TurretId = System.Math.Max(0, hello.Turret);
            tank.ShellsAmounts.Clear();
            tank.ShellsAmounts.AddRange(hello.Ammo);
            tank.Consumables.Clear();
            tank.Consumables.AddRange(hello.Consumables);
            tank.Camouflages.Clear();
            tank.Camouflages.AddRange(hello.Camouflages);
            return tank;
        }

        private IEnumerator FinishBattleFlow(BattleSession battle)
        {
            // _battle.gd releases the pointer as soon as the battle is over:
            // the result screen is a menu and has to be clickable, and the
            // hangar behind it is built on this same path.
            ReleaseFinishedBattlePointer(battle);
            DropFlowScreens();
            yield return new WaitForSecondsRealtime(FlowDelay(5f));
            var account = Accounts.GetCurrentAccount();
            LastBattleResult = ComposeResult(battle, account);
            // War of Ducks is transient: the result screen shows the battle,
            // but no credits, XP, rewards or statistics reach the account.
            // Big Boss pays like a random battle.
            var paid = battle.Ducks == null && BattleModeCatalog.PaysRewards(battle.Mode);
            if (!paid) { WorldOfDucksRules.StripProgression(LastBattleResult); }
            // A dedicated server played nobody's battle: its account earns nothing.
            if (account != null && !battle.Dedicated && paid
                && BattleResultApplier.Apply(account, LastBattleResult))
            {
                Persist();
            }

            yield return new WaitForSecondsRealtime(FlowDelay(1.8f));
            // _battle.gd raises UIGlobal.please_wait_toggle(true) 1.8 s after the
            // result timer, so the post-battle cover is please_wait.tscn, not the
            // loading wheel.
            PleaseWait = PleaseWaitScreen.Create(transform, true,
                Application.isMobilePlatform, 500);
            yield return new WaitForSecondsRealtime(FlowDelay(0.2f));

            battle.Finished -= OnBattleFinished;
            Destroy(battle.gameObject);
            Battle = null;
            yield return null;

            if (account != null)
            {
                Hangar = HangarScreen.Create(this, account);
            }
            if (PleaseWait != null)
            {
                Destroy(PleaseWait.gameObject);
                PleaseWait = null;
            }
            if (Hangar != null) Hangar.SetAccountOverlayVisible(true);
            Result = BattleResultScreen.Create(transform, LastBattleResult, CloseResult, OpenAccountStatistics);
            _transition = null;
            BlitzTelemetry.MarkStage("battle_result", "result", new System.Collections.Generic.Dictionary<string, string>
            {
                { "outcome", LastBattleResult.Outcome.ToString() },
            });
            Debug.Log("[Blitz] battle -> result: " + LastBattleResult.Outcome
                + " credits=" + LastBattleResult.Credits
                + " xp=" + LastBattleResult.Xp
                + " rewards=" + LastBattleResult.Rewards.Count);
        }

        private BattleResultData ComposeResult(BattleSession battle, AccountData account)
        {
            var stats = battle.StatsFor(battle.Player) ?? new BattleParticipantStats();
            var now = Clock.NowUnixSeconds;
            var card = battle.Player == null ? null : battle.Player.Card;
            var outcome = BattleOutcomeRules.FromWinner(
                battle.Roster.WinnerTeam, battle.Roster.PlayerTeam);
            var input = new BattleRewardInput
            {
                Id = "battle-" + ((long)(now * 1000d)).ToString() + "-"
                    + (account == null ? 0 : account.StatBattles),
                MapId = battle.World == null ? string.Empty : battle.World.Map.Id,
                TankId = card == null ? BattleSession.DefaultTankId : card.Name,
                PlayerName = stats.Nickname,
                TankName = card == null ? string.Empty : card.DisplayNameShort,
                FinishedAt = now,
                DurationSeconds = battle.Roster.GetTime(),
                Outcome = outcome,
                FinishReason = battle.Roster.FinishReason,
                TankLevel = card == null ? 1 : card.Level,
                FarmCoefficient = card == null ? 1f : card.FarmCoefficient,
                PremiumTank = card != null && card.Premium,
                PremiumAccount = account != null && account.PremAccountExpiration > now,
                HadFirstVictory = HasReward(account, "first_victory"),
                HadFirstBlood = HasReward(account, "first_blood"),
                HadWarrior = HasReward(account, "warrior"),
                SourceBetaBuild = true, // _global.gd VERSION_TYPE = BETA
                HadBetaTester = HasReward(account, "beta_tester"),
                PlayerTankClass = card == null ? -1 : (int)card.Class,
                MaxTeamTanks = battle.MatchRoster == null ? 7 : battle.MatchRoster.TeamSize,
                PiercerStreaks = stats.PiercerStreaks,
                KolobanovKills = stats.KolobanovKills,
                TarczayKills = stats.TarczayKills,
                BaseTicksDefendedMax = stats.BaseTicksDefendedMax,
                Spots = stats.Spots,
                BaseTicks = stats.BaseTicks,
                Damage = stats.Damage,
                DamageTaken = stats.DamageTaken,
                DamageBlocked = stats.DamageBlocked,
                Kills = stats.Kills,
                Shots = stats.Shots,
                Hits = stats.Hits,
                Survived = battle.Player != null && !battle.Player.Dead,
                RepairCost = stats.RepairCost,
                // credits_lost_consumables, booked per use_consumable call.
                ConsumableCost = battle.ConsumableCreditsLost,
            };

            if (account != null) foreach (var previous in account.BattleResults)
                if (previous.TankId == input.TankId) input.PreviousTankBattles++;
            // Applied ledger entries are resolved, as in the source hangar.
            input.MasteryReferenceXp = 0;
            input.AmmoCost = stats.AmmoCost;
            input.Interactions.AddRange(stats.Interactions.Values);

            for (var i = 0; i < battle.Participants.Count; i++)
            {
                var live = battle.Participants[i];
                var participant = live.Snapshot();
                var won = battle.Roster.WinnerTeam >= 0
                    && participant.Team == battle.Roster.WinnerTeam;
                var level = live.Tank == null ? 1 : live.Tank.Card.Level;
                var xp = BattleRewardCalculator.RawXp(level, participant.Spots,
                    participant.Damage, participant.Kills, participant.BaseTicks,
                    participant.DamageBlocked, live.Interactions.Values);
                participant.XpRaw = Math.Max(0, (int)xp);
                if (live.Tank != null && live.Tank.Card.Premium) xp *= BattleRewardCalculator.PremiumTankXpMultiplier;
                if (won) xp *= BattleRewardCalculator.VictoryXpMultiplier;
                participant.Xp = Math.Max(0, (int)xp);
                input.Participants.Add(participant);
                if (!participant.IsPlayer && participant.Team == battle.Roster.PlayerTeam)
                {
                    input.HighestAllyKills = Math.Max(
                        input.HighestAllyKills, participant.Kills);
                }
            }
            var result=BattleRewardCalculator.Calculate(input);
            if(battle.BigBoss!=null)
            {
                result.ModeId="bossmode";var a=battle.Roster.PlayerTeam==0;
                result.RemainingAllyPoints=a?battle.BigBoss.Match.TeamA:battle.BigBoss.Match.TeamB;
                result.RemainingEnemyPoints=a?battle.BigBoss.Match.TeamB:battle.BigBoss.Match.TeamA;
            }
            return result;
        }

        private static bool HasReward(AccountData account, string id)
        {
            return account != null && account.Rewards.ContainsKey(id);
        }

        private static float FlowDelay(float sourceSeconds)
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("BLITZ_FLOW_FAST"), "1",
                StringComparison.Ordinal) ? 0.01f : sourceSeconds;
        }

        /// <summary>
        /// Tear down every flow plate that can still be on screen and still be
        /// running its own unscaled timer: the map intro, the countdown and the
        /// global loading wheel. Each one is cancelled first so a pending
        /// callback cannot push the flow one state further after the battle it
        /// belonged to is gone.
        /// </summary>
        /// <summary>
        /// Releases the pointer for good once a battle is over. Releasing it
        /// once is not enough: the battle controls stay live for the finish
        /// beat, so the menu action (Escape/P) toggling the pause menu, or the
        /// chat closing with its remembered capture, would grab the pointer
        /// again and the hangar would come up with a locked, hidden cursor.
        /// The battle input is cut, the pause menu and the chat are closed, and
        /// only then is the lock dropped.
        /// </summary>
        private static void ReleaseFinishedBattlePointer(BattleSession battle)
        {
            if (battle != null)
            {
                battle.SetInputActive(false);
                if (battle.PauseMenu != null)
                {
                    battle.PauseMenu.Hide();
                }
                if (battle.Chat != null)
                {
                    battle.Chat.Close();
                }
            }
            MouseLock.Set(false);
        }

        private void DropFlowScreens()
        {
            if (MapIntro != null)
            {
                MapIntro.Cancel();
                Destroy(MapIntro.gameObject);
                MapIntro = null;
            }
            if (Countdown != null)
            {
                Countdown.Cancel();
                Destroy(Countdown.gameObject);
                Countdown = null;
            }
            if (Loading != null)
            {
                Destroy(Loading.gameObject);
                Loading = null;
            }
        }

        public void OpenAccountStatistics()
        {
            if (Battle != null || _transition != null) return;
            var account = Accounts.GetCurrentAccount();
            if (account == null || Hangar == null) return;
            CloseResult();
            CloseAccountStatistics();
            Hangar.SetAccountOverlayVisible(true);
            Statistics = AccountStatisticsScreen.Create(transform, account, CloseAccountStatistics, OpenHistoricalResult);
        }

        public void CloseAccountStatistics()
        {
            if (Statistics != null)
            {
                Statistics.gameObject.SetActive(false);
                Destroy(Statistics.gameObject);
                Statistics = null;
            }
            if (Hangar != null) Hangar.SetAccountOverlayVisible(false);
        }

        public void OpenHistoricalResult(BattleResultData result)
        {
            if (result == null || Battle != null || _transition != null) return;
            CloseAccountStatistics();
            CloseResult();
            if (Hangar != null) Hangar.SetAccountOverlayVisible(true);
            // Read-only history: never Apply(), Persist(), or replay new-battle music.
            Result = BattleResultScreen.Create(transform, result, CloseResult, OpenAccountStatistics, false);
        }

        private void CloseResult()
        {
            if (Result != null)
            {
                Result.gameObject.SetActive(false);
                Destroy(Result.gameObject);
                Result = null;
            }
            if (Hangar != null) Hangar.SetAccountOverlayVisible(false);
        }

        public void Persist()
        {
            Saves.SaveBook(Accounts);
            Saves.SaveSettings(Settings);
        }
    }
}
