using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Security;
using BlitzLegacy.Runtime.Telemetry;
using BlitzLegacy.Runtime.Ui;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

namespace BlitzLegacy.Network
{
    /// <summary>
    /// One online battle over Photon Fusion in Host mode. The host is a
    /// player: it seats the guests that said hello, runs the whole simulation
    /// and streams it through <see cref="NetBattle"/>. A client only sends
    /// input and shows what the host simulated.
    ///
    /// Flow: matchmaking (or a named room) -> guests join and send a hello ->
    /// the host enters the battle when the room is full or the lobby countdown
    /// runs out -> every guest gets the plan and builds the same battle.
    ///
    /// Survival:
    ///  * a client that loses its connection keeps its battle on screen and
    ///    rejoins the same session with the same secret token; the host gives
    ///    the seat back to whoever proves it (SHA-256 of the token is the seat
    ///    id in the plan) and hands it to the AI if nobody comes back in time;
    ///  * when the host leaves, Fusion elects a new host among the clients; that
    ///    client turns its puppets back into simulated tanks and carries on, the
    ///    others rejoin it the same way as after a reconnect.
    ///
    /// SECURITY: everything coming from peers is untrusted. Input is clamped by
    /// <see cref="BattleRemoteDriver"/>, plan/hello bytes are bounds-checked,
    /// and after the battle has started only peers presenting the token of a
    /// seat in the plan are let in.
    /// </summary>
    public sealed class NetSession : MonoBehaviour, INetworkRunnerCallbacks
    {
        /// <summary>Humans per battle: 7 vs 7, bots take the free seats.</summary>
        public const int MaxPlayers = BattleOnline.Capacity;

        // Photon room slots above MaxPlayers, so a player whose old connection
        // has not timed out yet can already rejoin a full battle.
        private const int RejoinHeadroom = 4;

        /// <summary>Matchmaking: the lenient balancer's start time (the normal one starts at 3/4 of it).</summary>
        public const float DefaultLobbySeconds = OnlineBalancer.LenientStartSeconds;

        /// <summary>Matchmaking never starts a battle with fewer humans than this.</summary>
        public const int MinimumHumanPlayers = 2;

        /// <summary>How long a lost seat waits for its player before the AI takes it.</summary>
        public const float RejoinSeconds = 60f;

        /// <summary>How long a client keeps trying to get back into its battle.</summary>
        public const float ReconnectSeconds = 60f;

        /// <summary>Client: no fresh host snapshot for this long means the host is gone.</summary>
        public const float HostSilentSeconds = 2f;

        /// <summary>Client: failed rejoins of the old room before the battle moves to a new host.</summary>
        private const int OriginalRoomAttempts = 2;
        /// <summary>Host leaving a live battle: time for the hand-over notice to go out.</summary>
        private const int HostLeaveNoticeMs = 600;
        /// <summary>Retry pace while the battle moves to its new host.</summary>
        private const float HandoverRetry = 0.6f;
        /// <summary>Planned hand-over: time for the notice to reach the clients.</summary>
        private const int HandHostNoticeMs = 250;
        private const float ReputationCheckDelay = 0.5f;
        private const float ResumedReputationDelay = 3f;
        /// <summary>A new host nobody followed goes back to try the old room once.</summary>
        private const float LonelyHostSeconds = 5f;
        private const float PingSampleInterval = 1f;
        private const float WatchdogGrace = 3f;

        private const float ReconnectInterval = 1.5f;
        private const int TokenLength = 32;

        public static NetSession Current { get; private set; }

        public bool IsHost { get { return Runner != null && Runner.IsServer; } }
        public string Room { get; private set; }
        public NetworkRunner Runner { get; private set; }

        /// <summary>Host: battle starts once this many guests have said hello.</summary>
        [System.NonSerialized]
        public int ExpectedGuests = MaxPlayers - 1;

        /// <summary>Host: start after this many seconds (see <see cref="_lobbyFromFirstGuest"/>).</summary>
        [System.NonSerialized]
        public float LobbySeconds = DefaultLobbySeconds;

        /// <summary>Smoke runs drive the local tank on their own.</summary>
        [System.NonSerialized]
        public bool AutoDrive;

        /// <summary>
        /// The hangar's queue screen, if the session came from it. The screen
        /// is a MonoBehaviour that can be destroyed while the session still
        /// runs (hangar rebuilt, battle entered); a plain interface reference
        /// would then pass a null check and throw on the first label it
        /// touches, so a destroyed screen reads as no screen at all.
        /// </summary>
        public IBattleOnlineSink Sink
        {
            get
            {
                var sink = _sink;
                var unity = sink as UnityEngine.Object;
                if (!ReferenceEquals(unity, null) && unity == null) { _sink = null; return null; }
                return sink;
            }
            set { _sink = value; }
        }

        [System.NonSerialized]
        private IBattleOnlineSink _sink;

        /// <summary>Raised with a localization key when the session gives up.</summary>
        public event Action<string> Failed;

        private enum StartMode { Host, Join, Matchmake }

        private StartMode _mode;
        private bool _lobbyFromFirstGuest;
        private byte[] _token;
        private string _seat;
        private string _sessionName;
        private bool _matchmakeHosting;
        private int _matchmakeAttempts;
        private float _matchmakeStartedAt = -1f;

        // Matchmaking: the open battles of this build, as the Photon lobby lists
        // them, and the battles that already turned this peer away.
        private List<SessionInfo> _sessionList;
        private readonly HashSet<string> _skipRooms = new HashSet<string>();
        private string _joiningRoom;
        private int _tier = 1;
        private BattleMode _battleMode = BattleMode.Normal;
        private int _publishedStartsIn = int.MinValue;
        private string _roomMix = string.Empty;
        private string _publishedMix;
        private NetQueueWatcher _queueWatcher;
        private float _publishedAt = -1f;

        private const float SessionListWait = 4f;
        private const float PropertyInterval = 1f;
        private const string TierProperty = NetQueueWatcher.TierProperty;
        private const string StartsInProperty = NetQueueWatcher.StartsInProperty;
        private const string ModeProperty = NetQueueWatcher.ModeProperty;
        private const string MixProperty = NetQueueWatcher.MixProperty;

        private const int MatchmakeJoinAttempts = 3;

        // Matchmaking: cloud timeouts in the queue are retried before giving up.
        private const int TransientRetries = 2;
        private const float TransientRetryDelay = 2f;
        private int _transientRetries;
        private float _retryAt = -1f;

        // Two players searching at the same moment can each open a room. A host
        // still alone after a random while closes its room and searches again,
        // so such pairs merge instead of waiting for nobody.
        private const float LonelyMinSeconds = 8f;
        private const float LonelyMaxSeconds = 16f;
        // Fallback only: blind lone-host rematch while the lobby watcher is offline.
        private float _lonelyRematchAt = -1f;
        private int _lonelyRounds;
        // Lone host merging: the battle seen in the lobby it may move into.
        private const float MergeCheckInterval = 0.5f;
        private string _mergeTarget;
        private float _mergeTargetSince = -1f;
        private float _mergeCheckAt = -1f;
        private readonly List<OnlineBalancer.Room> _mergeRooms = new List<OnlineBalancer.Room>();
        // Once the player saw "waiting for players", re-searches in the
        // background keep that status instead of flashing "connecting".
        private bool _queueShown;
        private bool _rematching;

        private readonly List<BattleNetSetup.Guest> _guests = new List<BattleNetSetup.Guest>();
        private float _lobbyOpenedAt = -1f;
        private float _firstGuestAt = -1f;
        private bool _battleRequested;
        private bool _startAnnounced;
        private bool _hostStarted;
        private bool _ended;
        private bool _starting;
        private BattleNetPlan _pendingPlan;
        private BattleSession _battle;
        private bool _hostWired;
        private byte _uprisingAction;
        private byte _uprisingSequence;
        private bool _sawBattle;

        // Client: trying to get back into the running battle.
        private float _reconnectUntil = -1f;
        private float _nextAttemptAt;
        private HostMigrationToken _migration;

        // Host hand-over: the room the battle moves to after its host is gone.
        // Every survivor joins the same derived name, so
        // Photon's atomic room creation elects exactly one new host.
        private string _handoverRoom;
        private bool _handingOver;
        private bool _hostLeftNotice;
        private int _originalAttempts;
        /// <summary>Slot of the host the battle is moving away from (-1 unknown).</summary>
        private int _previousHostSlot = -1;
        private int _handoverTarget = -1;
        private bool _handoverNotice;
        private string _previousRoom;
        private bool _lonelyReturned;
        /// <summary>Room the cloud already answered GameNotFound/GameClosed for.</summary>
        private string _goneRoom;
        private float _lonelyHostCheckAt = -1f;
        private float _followHandoverAt = -1f;
        private float _reputationCheckAt = -1f;
        private bool _reputationArmed;
        private bool _deathHandoverDone;
        private bool _repOpen;
        private bool _localOut;
        private double _pingSum;
        private int _pingSamples;
        private float _nextPingAt;
        private float _watchdogGraceUntil = -1f;

        // Synchronized start: the host holds every map intro until each human
        // in the plan has loaded, then releases them all at once.
        public const float LoadWaitSeconds = 40f;
        private readonly HashSet<int> _loaded = new HashSet<int>();
        private bool _loadedSent;
        private float _hostIntroAt = -1f;
        private float _clientHoldAt = -1f;
        private bool _waitingBanner;

        // Host: seats kicked by the anti-cheat; they may not rejoin this battle.
        private readonly HashSet<string> _bannedSeats = new HashSet<string>();

        public const string BannedKey = "online:error/banned";

        // Host: seats whose player is gone, by slot, with the time the AI takes over.
        private readonly Dictionary<int, float> _detached = new Dictionary<int, float>();

        // ----- creation ----------------------------------------------------------

        public static NetSession Host(string room, int expectedGuests, float lobbySeconds)
        {
            var session = Create(StartMode.Host, room);
            session.ExpectedGuests = Mathf.Clamp(expectedGuests, 0, MaxPlayers - 1);
            session.LobbySeconds = Mathf.Max(5f, lobbySeconds);
            return session;
        }

        public static NetSession Join(string room)
        {
            return Create(StartMode.Join, room);
        }

        /// <summary>The hangar's online mode: join any open battle of this build or open one.</summary>
        public static NetSession Matchmake(IBattleOnlineSink sink, BattleMode mode)
        {
            SandboxOnlineBan.Ensure(mode, "matchmake");
            var session = Create(StartMode.Matchmake, null);
            session.Sink = sink;
            session._battleMode = mode;
            session._lobbyFromFirstGuest = true;
            session._matchmakeStartedAt = Time.realtimeSinceStartup;
            if (LobbySecondsOverride > 0f) { session.LobbySeconds = LobbySecondsOverride; }
            return session;
        }

        /// <summary>Test harness only: a shorter matchmaking lobby.</summary>
        public static float LobbySecondsOverride = -1f;

        /// <summary>
        /// Test harness only: drop the connection as a network failure would,
        /// without telling the session it was on purpose.
        /// </summary>
        public void SimulateConnectionLoss()
        {
            if (Runner != null && Runner.IsRunning && !Runner.IsServer)
            {
                Debug.LogWarning("[BlitzNet] smoke: dropping the connection");
                Runner.Shutdown(true, ShutdownReason.ConnectionTimeout);
            }
        }

        private static NetSession Create(StartMode mode, string room)
        {
            if (Current != null)
            {
                throw new InvalidOperationException("A network session is already running");
            }
            // Hard mod tank ban: no session of any kind (matchmaking, a hosted or
            // joined room) starts with a mod tank selected.
            ModTankOnlineBan.Ensure(BattleNetHelloFactory.SelectedTankId(), "session");
            if (mode != StartMode.Matchmake && (string.IsNullOrWhiteSpace(room) || room.Length > 64))
            {
                throw new ArgumentException("room name must be 1..64 characters", "room");
            }
            var go = new GameObject("NetSession");
            DontDestroyOnLoad(go);
            var session = go.AddComponent<NetSession>();
            session._mode = mode;
            session.Room = room != null ? room.Trim() : null;
            session._token = new byte[TokenLength];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(session._token); }
            session._seat = SeatOf(session._token);
            Current = session;
            BattleNetHooks.UprisingActionRequested += session.OnUprisingActionRequested;
            BattleNetHooks.HoldIntro = HoldIntroHook;
            BattleNetHooks.IntroReleased = IntroReleasedHook;
            BattleNetHooks.PingMs = PingHook;
            return session;
        }

        /// <summary>Public seat id of a secret token: lower-case hex SHA-256.</summary>
        public static string SeatOf(byte[] token)
        {
            if (token == null || token.Length != TokenLength) { return null; }
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(token);
                var chars = new char[hash.Length * 2];
                const string hex = "0123456789abcdef";
                for (var i = 0; i < hash.Length; i++)
                {
                    chars[i * 2] = hex[hash[i] >> 4];
                    chars[i * 2 + 1] = hex[hash[i] & 15];
                }
                return new string(chars);
            }
        }

        internal static void NotifyHostStarted()
        {
            Debug.Log("[BlitzNet] host battle is live");
            var session = Current;
            if (session == null || session._ended) { return; }
            session._hostStarted = true;
            session.StartClientBattleIfReady();
        }

        /// <summary>
        /// A client must advance its local roster when the authoritative host
        /// starts the battle. Besides keeping the clock and spectator state in
        /// sync, this flag is what lets a promoted client restart bot brains
        /// during host migration.
        /// </summary>
        private void StartClientBattleIfReady()
        {
            if (!_hostStarted || IsHost || _battle == null || _battle.Started) { return; }
            _battle.StartBattle();
        }

        private void Start()
        {
            if (AntiCheatBan.IsBanned)
            {
                Fail(BannedKey);
                return;
            }
            if (NetAppSettings.Resolve() == null)
            {
                Fail("online:error/not_configured");
                return;
            }
            Status(_queueShown ? "online:queue/waiting" : "online:queue/connecting");
            var args = BaseArgs();
            switch (_mode)
            {
                case StartMode.Host:
                    args.GameMode = GameMode.Host;
                    args.SessionName = Room;
                    break;
                case StartMode.Join:
                    args.GameMode = GameMode.Client;
                    args.SessionName = Room;
                    break;
                default:
                    _matchmakeAttempts++;
                    Search();
                    return;
            }
            StartRunner(args, false);
        }

        private StartGameArgs BaseArgs()
        {
            return new StartGameArgs
            {
                PlayerCount = MaxPlayers + RejoinHeadroom,
                CustomPhotonAppSettings = NetAppSettings.Resolve(),
                EnableClientSessionCreation = false,
                // SECURITY: the secret that proves this peer's seat on rejoin.
                ConnectionToken = _token,
            };
        }

        /// <summary>
        /// Every start uses a fresh runner on its own child object: a Fusion
        /// runner is single use and destroys its GameObject on shutdown, which
        /// must never take the session (and its reconnect state) with it.
        /// </summary>
        private async void StartRunner(StartGameArgs args, bool retry, NetworkRunner lobbyRunner = null)
        {
            if (_starting) { return; }
            _starting = true;
            NetworkRunner runner;
            if (lobbyRunner != null && lobbyRunner == Runner)
            {
                // The runner that read the session list goes on into the game.
                runner = lobbyRunner;
            }
            else
            {
                var old = Runner;
                Runner = null;
                if (old != null && old.IsRunning)
                {
                    await ShutdownQuietly(old);
                }
                if (this == null) { return; }
                // A runner still shutting down makes the next StartGame fail.
                await WaitForOtherRunners(null);
                if (this == null) { return; }
                runner = NewRunner();
            }
            var holder = runner.gameObject;

            Debug.Log("[BlitzNet] starting " + args.GameMode + " room='" + (args.SessionName ?? "*")
                + "' build=" + NetAppSettings.Build + (args.HostMigrationToken != null ? " (migration)" : "")
                + (retry ? " (rejoin)" : ""));
            StartGameResult result;
            var thrown = ShutdownReason.Error;
            try
            {
                result = await runner.StartGame(args);
            }
            catch (Exception error)
            {
                thrown = ReasonOf(error);
                result = null;
            }
            _starting = false;
            if (this == null || runner != Runner)
            {
                // Superseded while starting (cancel, hand-over, shutdown): a
                // start that still went through must not stay connected.
                if (result != null && result.Ok) { ReleaseRunner(runner); }
                return;
            }
            if (result == null || !result.Ok)
            {
                var reason = result != null ? result.ShutdownReason : thrown;
                Debug.LogWarning("[BlitzNet] start failed: " + reason
                    + (result != null ? " " + result.ErrorMessage : string.Empty));
                Runner = null;
                if (holder != null) { Destroy(holder); }
                OnStartFailed(reason, retry);
                return;
            }

            _sessionName = runner.SessionInfo != null ? runner.SessionInfo.Name : args.SessionName;
            _transientRetries = 0;
            Debug.Log("[BlitzNet] started as " + (runner.IsServer ? "host" : "client") + ", local player "
                + runner.LocalPlayer.PlayerId + " session='" + _sessionName + "' region="
                + (runner.SessionInfo != null ? runner.SessionInfo.Region : "?"));

            if (_mode == StartMode.Matchmake && !_sawBattle)
            {
                EnsureQueueWatch(runner.SessionInfo != null ? runner.SessionInfo.Region : null);
            }

            if (runner.IsServer)
            {
                if (runner.IsResume) { return; } // OnHostMigrationResume did the work.
                if (_handingOver && _battle != null)
                {
                    ResumeAsHost(runner);
                    return;
                }
                _lobbyOpenedAt = Time.realtimeSinceStartup;
                // Each round alone waits longer, so a lone player does not keep
                // reopening its battle every few seconds.
                _lonelyRounds = Mathf.Min(_lonelyRounds + 1, 4);
                _lonelyRematchAt = _mode == StartMode.Matchmake
                    ? _lobbyOpenedAt + UnityEngine.Random.Range(LonelyMinSeconds, LonelyMaxSeconds) * _lonelyRounds
                    : -1f;
                // Guarantee a fresh lobby scan exactly when the allowed tier
                // range widens, even if this peer is currently hosting alone.
                var expandAt = _matchmakeStartedAt + OnlineBalancer.ExpandAfterSeconds;
                if (_mode == StartMode.Matchmake && _lobbyOpenedAt < expandAt)
                {
                    _lonelyRematchAt = Mathf.Min(_lonelyRematchAt, expandAt);
                }
                if (!SpawnBattleObject(runner)) { return; }
                Status("online:queue/waiting");
                _queueShown = true;
            }
            else
            {
                _watchdogGraceUntil = Time.realtimeSinceStartup + WatchdogGrace;
                SendHello();
            }
        }

        private NetworkRunner NewRunner()
        {
            var holder = new GameObject("NetRunner");
            holder.transform.SetParent(transform, false);
            var runner = holder.AddComponent<NetworkRunner>();
            runner.ProvideInput = true;
            runner.AddCallbacks(this);
            Runner = runner;
            return runner;
        }

        // ----- matchmaking -------------------------------------------------------

        /// <summary>
        /// Reads the open battles of this build from the Photon lobby and joins
        /// the best one for the selected tank (<see cref="OnlineBalancer.Pick"/>),
        /// or opens a new battle at this tank's tier.
        /// </summary>
        private async void Search()
        {
            if (_starting) { return; }
            _starting = true;
            var old = Runner;
            Runner = null;
            if (old != null && old.IsRunning) { await ShutdownQuietly(old); }
            if (this == null) { return; }
            await WaitForOtherRunners(null);
            if (this == null) { return; }

            int column;
            var account = BlitzBootstrap.Current != null && BlitzBootstrap.Current.Accounts != null
                ? BlitzBootstrap.Current.Accounts.GetCurrentAccount() : null;
            BattleOnline.Classify(account != null ? account.SelectedTankName : null, out _tier, out column);
            {
                var ownTiers = new int[10];
                var ownClasses = new int[4];
                Count(account != null ? account.SelectedTankName : null, ownTiers, ownClasses);
                _roomMix = OnlineQueue.Encode(ownTiers, ownClasses);
                _publishedMix = null;
            }

            var runner = NewRunner();
            _sessionList = null;
            Debug.Log("[BlitzNet] searching " + _battleMode + " battles for tier " + _tier + " build=" + NetAppSettings.Build);
            StartGameResult result;
            var thrown = ShutdownReason.Error;
            try
            {
                result = await runner.JoinSessionLobby(SessionLobby.ClientServer, null, null, NetAppSettings.Resolve());
            }
            catch (Exception error)
            {
                thrown = ReasonOf(error);
                result = null;
            }
            _starting = false;
            if (this == null || runner != Runner)
            {
                if (result != null && result.Ok) { ReleaseRunner(runner); }
                return;
            }
            if (result == null || !result.Ok)
            {
                var reason = result != null ? result.ShutdownReason : thrown;
                Debug.LogWarning("[BlitzNet] lobby failed: " + reason);
                Runner = null;
                if (runner != null) { Destroy(runner.gameObject); }
                if (!RetryTransient(reason)) { Fail(ReasonKey(reason)); }
                return;
            }

            var deadline = Time.realtimeSinceStartup + SessionListWait;
            while (_sessionList == null && Time.realtimeSinceStartup < deadline)
            {
                await Task.Delay(100);
                if (this == null || runner != Runner)
                {
                    // Cancelled while reading the list: Shutdown already let it go.
                    return;
                }
            }

            var rooms = new List<OnlineBalancer.Room>();
            for (var i = 0; _sessionList != null && i < _sessionList.Count; i++)
            {
                var info = _sessionList[i];
                if (info == null || !info.IsValid || !info.IsOpen || !info.IsVisible) { continue; }
                if (ReadInt(info, ModeProperty, (int)BattleMode.Normal) != (int)_battleMode) { continue; }
                rooms.Add(new OnlineBalancer.Room
                {
                    Name = info.Name,
                    Tier = ReadInt(info, TierProperty, 0),
                    Players = info.PlayerCount,
                    Capacity = MaxPlayers,
                    StartsIn = ReadInt(info, StartsInProperty, -1),
                });
            }
            if (_queueWatcher == null || _queueWatcher.Last == null) { PublishQueueFromSessions(); }
            var waited = Mathf.Max(0f, Time.realtimeSinceStartup - _matchmakeStartedAt);
            var tierSpread = OnlineBalancer.TierSpreadFor(waited);
            var pick = OnlineBalancer.Pick(rooms, _tier, tierSpread, _skipRooms);
            var args = BaseArgs();
            if (pick >= 0)
            {
                Debug.Log("[BlitzNet] " + rooms.Count + " open battle(s), joining '" + rooms[pick].Name
                    + "' tier " + rooms[pick].Tier + " players " + rooms[pick].Players
                    + " after " + waited.ToString("F0") + "s, spread +/-" + tierSpread);
                args.GameMode = GameMode.Client;
                args.SessionName = rooms[pick].Name;
                _matchmakeHosting = false;
                _joiningRoom = rooms[pick].Name;
            }
            else
            {
                Debug.Log("[BlitzNet] " + rooms.Count + " open battle(s), none fits tier " + _tier
                    + " at spread +/-" + tierSpread + ": opening one");
                args.GameMode = GameMode.Host;
                args.SessionName = "bl-" + Guid.NewGuid().ToString("N");
                args.SessionProperties = RoomProperties(-1);
                _matchmakeHosting = true;
                _joiningRoom = null;
                _publishedStartsIn = -1;
            }
            StartRunner(args, false, runner);
        }

        private Dictionary<string, SessionProperty> RoomProperties(int startsIn)
        {
            return new Dictionary<string, SessionProperty>
            {
                { TierProperty, _tier },
                { StartsInProperty, startsIn },
                { ModeProperty, (int)_battleMode },
                { MixProperty, string.IsNullOrEmpty(_roomMix) ? OnlineQueue.Encode(null, null) : _roomMix },
            };
        }

        // ----- shared queue -------------------------------------------------------

        /// <summary>Matchmaking: keep a lobby-only watcher on the whole queue while waiting.</summary>
        private void EnsureQueueWatch(string region)
        {
            if (_mode != StartMode.Matchmake || _sawBattle || _battleRequested || _queueWatcher != null) { return; }
            _queueWatcher = NetQueueWatcher.Start(transform, _battleMode, region);
            _queueWatcher.Changed += OnQueueChanged;
        }

        /// <summary>Entering the battle (loading screen) takes the player out of the queue view.</summary>
        private void StopQueueWatch()
        {
            var watcher = _queueWatcher;
            _queueWatcher = null;
            if (watcher != null) { watcher.Stop(); }
        }

        private void OnQueueChanged(OnlineQueue.Snapshot snapshot)
        {
            if (snapshot == null || Sink == null || _sawBattle || _battleRequested) { return; }
            Sink.OnlineQueue(snapshot.Waiting, snapshot.TierCounts, snapshot.ClassCounts);
        }

        /// <summary>One-off queue view from the matchmaking runner's own lobby scan.</summary>
        private void PublishQueueFromSessions()
        {
            if (Sink == null || _sessionList == null) { return; }
            var rooms = new List<OnlineQueue.Room>();
            for (var i = 0; i < _sessionList.Count; i++)
            {
                var info = _sessionList[i];
                if (info == null || !info.IsValid || !info.IsOpen || !info.IsVisible) { continue; }
                if (ReadInt(info, ModeProperty, (int)BattleMode.Normal) != (int)_battleMode) { continue; }
                rooms.Add(new OnlineQueue.Room
                {
                    Players = info.PlayerCount,
                    Tier = ReadInt(info, TierProperty, 0),
                    Mix = ReadString(info, MixProperty),
                });
            }
            var snapshot = OnlineQueue.Aggregate(rooms);
            Sink.OnlineQueue(snapshot.Waiting, snapshot.TierCounts, snapshot.ClassCounts);
        }

        private static string ReadString(SessionInfo info, string key)
        {
            SessionProperty value;
            if (info.Properties != null && info.Properties.TryGetValue(key, out value) && value != null && value.IsString)
            {
                var text = value.PropertyValue as string;
                return text != null && text.Length <= 64 ? text : null;
            }
            return null;
        }

        private static int ReadInt(SessionInfo info, string key, int fallback)
        {
            SessionProperty value;
            if (info.Properties != null && info.Properties.TryGetValue(key, out value) && value != null && value.IsInt)
            {
                return (int)value;
            }
            return fallback;
        }

        /// <summary>Host: keeps the lobby listing current so searchers can judge the battle.</summary>
        private void PublishRoomProperties(float startsIn)
        {
            if (_mode != StartMode.Matchmake || Runner == null || Runner.SessionInfo == null) { return; }
            var seconds = startsIn < 0f ? -1 : Mathf.CeilToInt(startsIn);
            var now = Time.realtimeSinceStartup;
            if ((seconds == _publishedStartsIn && _roomMix == _publishedMix) || now - _publishedAt < PropertyInterval) { return; }
            _publishedStartsIn = seconds;
            _publishedMix = _roomMix;
            _publishedAt = now;
            Runner.SessionInfo.UpdateCustomProperties(RoomProperties(seconds));
        }

        /// <summary>Matchmaking: this battle did not take us; look for another one.</summary>
        private bool SearchAgain(string reason)
        {
            if (_mode != StartMode.Matchmake || _sawBattle || _matchmakeAttempts >= MatchmakeJoinAttempts) { return false; }
            if (!string.IsNullOrEmpty(_joiningRoom)) { _skipRooms.Add(_joiningRoom); }
            Debug.Log("[BlitzNet] battle '" + _joiningRoom + "' did not take us (" + reason + "), searching again");
            _joiningRoom = null;
            ResetLobby(false);
            Start();
            return true;
        }

        private static async Task ShutdownQuietly(NetworkRunner runner, ShutdownReason reason = ShutdownReason.Ok)
        {
            try { await runner.Shutdown(true, reason); }
            catch (Exception error) { Debug.LogWarning("[BlitzNet] shutdown: " + error.Message); }
        }

        /// <summary>
        /// Lets go of a runner without waiting for it. A runner still inside
        /// StartGame / JoinSessionLobby is shut down as well, not only a
        /// running one, and it is moved off the session object first: the
        /// session destroys itself right after, and destroying a runner in
        /// the middle of its start left Fusion's cloud tasks working on a dead
        /// runner (NullReference in SendReflexiveInfo, "Must be inside a
        /// room", unobserved DisconnectException / WebSocketException after
        /// the player cancelled the queue). Fusion destroys the object itself
        /// once the shutdown is done.
        /// </summary>
        private static void ReleaseRunner(NetworkRunner runner, ShutdownReason reason = ShutdownReason.Ok)
        {
            if (runner == null || runner.IsShutdown) { return; }
            if (!runner.IsRunning && !runner.IsStarting) { return; }
            if (runner.transform.parent != null)
            {
                runner.transform.SetParent(null, false);
                DontDestroyOnLoad(runner.gameObject);
            }
            try { runner.Shutdown(true, reason); }
            catch (Exception error) { Debug.LogWarning("[BlitzNet] shutdown: " + error.Message); }
        }

        /// <summary>
        /// The shutdown reason behind an exception StartGame / JoinSessionLobby
        /// threw instead of returning a result. Known cloud refusals and
        /// timeouts are ordinary outcomes handled like a failed result.
        /// </summary>
        private static ShutdownReason ReasonOf(Exception error)
        {
            // Fusion.StartGameException is internal: read its message, which
            // is either the ShutdownReason name or Photon's refusal text.
            if (error != null && error.GetType().FullName == "Fusion.StartGameException")
            {
                var text = (error.Message ?? string.Empty).Trim();
                ShutdownReason parsed;
                if (!Enum.TryParse(text, out parsed))
                {
                    var lower = text.ToLowerInvariant();
                    parsed = lower.Contains("does not exist") || lower.Contains("not found") ? ShutdownReason.GameNotFound
                        : lower.Contains("full") ? ShutdownReason.GameIsFull
                        : lower.Contains("closed") ? ShutdownReason.GameClosed
                        : lower.Contains("timeout") || lower.Contains("timed out") ? ShutdownReason.OperationTimeout
                        : ShutdownReason.Error;
                }
                Debug.LogWarning("[BlitzNet] start refused: " + parsed + " (" + text + ")");
                return parsed;
            }
            if (error is TimeoutException)
            {
                Debug.LogWarning("[BlitzNet] start timed out: " + error.Message);
                return ShutdownReason.OperationTimeout;
            }
            if (error is OperationCanceledException)
            {
                Debug.LogWarning("[BlitzNet] start cancelled");
                return ShutdownReason.OperationCanceled;
            }
            Debug.LogException(error);
            return ShutdownReason.Error;
        }

        /// <summary>A failure a second try a moment later usually gets past.</summary>
        private static bool IsTransient(ShutdownReason reason)
        {
            switch (reason)
            {
                case ShutdownReason.PhotonCloudTimeout:
                case ShutdownReason.OperationTimeout:
                case ShutdownReason.ConnectionTimeout:
                case ShutdownReason.Error:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Matchmaking, still in the queue: a cloud timeout or a Fusion
        /// internal error does not end the search at once, the session tries
        /// again a little later (a few times) with the queue screen kept open.
        /// </summary>
        private bool RetryTransient(ShutdownReason reason)
        {
            if (_mode != StartMode.Matchmake || _sawBattle || _battleRequested) { return false; }
            if (!IsTransient(reason) || _transientRetries >= TransientRetries || Sink == null) { return false; }
            _transientRetries++;
            var delay = TransientRetryDelay * _transientRetries;
            Debug.LogWarning("[BlitzNet] " + reason + ", trying again in " + delay.ToString("F0") + "s ("
                + _transientRetries + "/" + TransientRetries + ")");
            _retryAt = Time.realtimeSinceStartup + delay;
            return true;
        }

        private bool SpawnBattleObject(NetworkRunner runner)
        {
            var prefab = Resources.Load<NetworkObject>(NetBattle.PrefabResource);
            if (prefab == null)
            {
                Debug.LogError("[BlitzNet] missing prefab Resources/" + NetBattle.PrefabResource
                    + "; run Tools/unity.sh net-setup");
                Fail("online:error/not_configured");
                return false;
            }
            runner.Spawn(prefab);
            return true;
        }

        private void OnStartFailed(ShutdownReason reason, bool retry)
        {
            if (retry && InBattle)
            {
                // The old room has no host to take us back: move the battle
                // to a new host instead of ending it.
                if (_handingOver && reason == ShutdownReason.GameIdAlreadyExists)
                {
                    // Another survivor created the hand-over room first: join it.
                    _nextAttemptAt = Time.realtimeSinceStartup + 0.1f;
                    return;
                }
                var roomGone = reason == ShutdownReason.GameNotFound || reason == ShutdownReason.GameClosed;
                if (!_handingOver && roomGone) { _goneRoom = _sessionName; }
                if (!_handingOver &&
                    (roomGone || reason == ShutdownReason.OperationTimeout ||
                     ++_originalAttempts >= OriginalRoomAttempts))
                {
                    var lonelyBack = _lonelyReturned && roomGone;
                    BeginHandover("old room unreachable (" + reason + ")");
                    // We were the lonely new host a moment ago: nobody else
                    // waits in the hand-over room, reopen it at once.
                    if (lonelyBack && _handingOver) { _nextAttemptAt = Time.realtimeSinceStartup + 0.1f; }
                    return;
                }
                _nextAttemptAt = Time.realtimeSinceStartup + (_handingOver ? HandoverRetry : ReconnectInterval);
                return;
            }
            // The battle filled up, started or vanished between the list and the join.
            if (!retry && !_matchmakeHosting &&
                (reason == ShutdownReason.GameNotFound || reason == ShutdownReason.GameIsFull ||
                 reason == ShutdownReason.GameClosed || reason == ShutdownReason.ConnectionRefused) &&
                SearchAgain(reason.ToString()))
            {
                return;
            }
            if (!retry && RetryTransient(reason)) { return; }
            Fail(ReasonKey(reason));
        }

        private static string ReasonKey(ShutdownReason reason)
        {
            switch (reason)
            {
                case ShutdownReason.MaxCcuReached: return "online:error/servers_full";
                case ShutdownReason.GameIsFull:
                case ShutdownReason.GameNotFound:
                case ShutdownReason.GameClosed:
                case ShutdownReason.ConnectionRefused: return "online:error/room_closed";
                case ShutdownReason.IncompatibleConfiguration: return "online:error/version";
                default: return "online:error/no_connection";
            }
        }

        // ----- hello / plan ------------------------------------------------------

        private void SendHello()
        {
            var hello = BuildHello();
            byte[] bytes;
            try { bytes = hello.ToBytes(); }
            catch (Exception error) when (OnlineBan.IsBan(error))
            {
                // Hard mod tank ban: a mod tank never joins an online battle.
                LeaveForBan(OnlineBan.KeyOf(error));
                return;
            }
            Runner.SendReliableDataToServer(ReliableKey.FromInts(NetChannel.Hello, 0, 0, 0), bytes);
            Debug.Log("[BlitzNet] hello sent: tank=" + hello.TankId + " name=" + hello.Name);
        }

        private static BattleNetHello BuildHello()
        {
            return BattleNetHelloFactory.FromCurrentAccount(NetAppSettings.Build);
        }

        private bool InBattle { get { return _battle != null && !_ended; } }

        // ----- per frame ---------------------------------------------------------

        private void Update()
        {
            if (_hideBannerAt > 0f && Time.realtimeSinceStartup >= _hideBannerAt)
            {
                _hideBannerAt = -1f;
                NetStatusBanner.Hide();
            }
            if (_retryAt > 0f && Time.realtimeSinceStartup >= _retryAt)
            {
                _retryAt = -1f;
                if (!_starting && !_sawBattle && !_battleRequested)
                {
                    ResetLobby(false);
                    Start();
                    return;
                }
            }
            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap == null) { return; }

            TrackBattle(bootstrap.Battle);
            if (_hostLeftNotice)
            {
                _hostLeftNotice = false;
                if (InBattle && !IsHost) { BeginHandover("host left the battle"); }
            }
            if (_handoverNotice)
            {
                _handoverNotice = false;
                if (InBattle && !IsHost) { BeginHandover("host handed the battle over", _handoverTarget); }
            }
            TickBattleHousekeeping();
            if (TickHostWatchdog()) { return; }

            if (_reconnectUntil > 0f)
            {
                TickReconnect();
                return;
            }
            if (Runner == null || !Runner.IsRunning) { return; }

            TickSynchronizedStart(bootstrap);
            if (IsHost)
            {
                if (!_battleRequested) { TickHostLobby(bootstrap); }
                if (_battle != null && !_startAnnounced && _battle.Started && NetBattle.Instance != null)
                {
                    _startAnnounced = true;
                    NetBattle.Instance.RPC_BattleStart();
                }
                TickDetachedSeats();
            }
            else
            {
                if (!_sawBattle) { PublishClientLobby(); }
                if (_pendingPlan != null && bootstrap.Battle == null && bootstrap.Hangar != null)
                {
                    EnterClientBattle(bootstrap);
                }
            }
        }

        private void TickHostLobby(BlitzBootstrap bootstrap)
        {
            if (_lobbyOpenedAt < 0f || bootstrap.Hangar == null) { return; }
            var now = Time.realtimeSinceStartup;
            var matchmakeElapsed = _matchmakeStartedAt >= 0f ? now - _matchmakeStartedAt : 0f;
            // Alone, matchmaking never gives up into a bot battle: it keeps
            // merging with other lone hosts until real players turn up. The
            // battle is only closed for another one the lobby actually lists;
            // reopening it blindly made the queue flash "connecting" every few
            // seconds for a player who simply waited alone.
            if (_mode == StartMode.Matchmake && _firstGuestAt < 0f && Runner.ActivePlayers.Count() <= 1)
            {
                var watching = _queueWatcher != null && _queueWatcher.InLobby;
                if (watching ? MergeDue(now) : _lonelyRematchAt > 0f && now >= _lonelyRematchAt)
                {
                    Rematch();
                    return;
                }
            }
            if (_mode == StartMode.Matchmake)
            {
                // The countdown runs from the first guest the balancer took, not
                // from the start of the search: a long lonely wait must not eat it.
                var countdown = OnlineBalancer.CountdownElapsed(now, _firstGuestAt);
                var decision = MatchmakeDecision(countdown);
                PublishHostLobby(decision.StartsIn);
                PublishRoomProperties(decision.StartsIn);
                if (decision.Start)
                {
                    Debug.Log("[BlitzNet] lobby: starting with " + (_guests.Count + 1) + " human(s) after "
                        + matchmakeElapsed.ToString("F0") + "s in queue, " + countdown.ToString("F0") + "s countdown, "
                        + (decision.Lenient ? "lenient" : "normal") + " balance");
                    RequestHostBattle(bootstrap);
                }
                return;
            }
            float startsIn;
            if (_lobbyFromFirstGuest)
            {
                startsIn = _firstGuestAt < 0f ? -1f : LobbySeconds - (now - _firstGuestAt);
            }
            else
            {
                startsIn = LobbySeconds - (now - _lobbyOpenedAt);
            }
            PublishHostLobby(startsIn);
            PublishRoomProperties(startsIn);
            var timedOut = _lobbyFromFirstGuest ? _firstGuestAt >= 0f && startsIn <= 0f : startsIn <= 0f;
            if (_guests.Count >= ExpectedGuests || timedOut)
            {
                RequestHostBattle(bootstrap);
            }
        }

        /// <summary>
        /// <see cref="OnlineBalancer.Decide"/> for this lobby: normal balance
        /// 45 s, lenient 60 s after the first guest (scaled with a smoke-test
        /// lobby override).
        /// </summary>
        private OnlineBalancer.LobbyDecision MatchmakeDecision(float countdown)
        {
            var account = BlitzBootstrap.Current != null && BlitzBootstrap.Current.Accounts != null
                ? BlitzBootstrap.Current.Accounts.GetCurrentAccount() : null;
            var hostWeight = WeightOf(account != null ? account.SelectedTankName : null);
            var guests = new List<int>(_guests.Count);
            for (var i = 0; i < _guests.Count; i++) { guests.Add(WeightOf(_guests[i].Hello.TankId)); }
            var normalOk = OnlineBalancer.NormalBalanceOk(hostWeight, guests, BattleOnline.TeamSize);
            var lenientAt = LobbySeconds;
            var normalAt = LobbySeconds * (OnlineBalancer.NormalStartSeconds / OnlineBalancer.LenientStartSeconds);
            return OnlineBalancer.Decide(countdown, _guests.Count + 1, MaxPlayers, normalOk, normalAt, lenientAt);
        }

        private static int WeightOf(string tankId)
        {
            int tier, column;
            BattleOnline.Classify(tankId, out tier, out column);
            return OnlineBalancer.Weight(tier, column);
        }

        private void PublishHostLobby(float startsIn)
        {
            var tiers = new int[10];
            var classes = new int[4];
            var account = BlitzBootstrap.Current != null && BlitzBootstrap.Current.Accounts != null
                ? BlitzBootstrap.Current.Accounts.GetCurrentAccount() : null;
            Count(account != null ? account.SelectedTankName : null, tiers, classes);
            for (var i = 0; i < _guests.Count; i++) { Count(_guests[i].Hello.TankId, tiers, classes); }
            var players = _guests.Count + 1;
            _roomMix = OnlineQueue.Encode(tiers, classes);

            var battle = NetBattle.Instance;
            if (battle != null && battle.Object != null && battle.Object.IsValid)
            {
                battle.LobbyPlayers = players;
                battle.LobbyStartsIn = startsIn;
                for (var i = 0; i < 10; i++) { battle.LobbyMix.Set(i, (byte)Mathf.Min(tiers[i], 255)); }
                for (var i = 0; i < 4; i++) { battle.LobbyMix.Set(10 + i, (byte)Mathf.Min(classes[i], 255)); }
            }
            if (Sink != null) { Sink.OnlineLobby(players, MaxPlayers, startsIn, tiers, classes); }
        }

        private static void Count(string tankId, int[] tiers, int[] classes)
        {
            int tier, column;
            BattleOnline.Classify(tankId, out tier, out column);
            tiers[10 - tier]++;
            classes[column]++;
        }

        private void PublishClientLobby()
        {
            if (Sink == null) { return; }
            var battle = NetBattle.Instance;
            if (battle == null || battle.Object == null || !battle.Object.IsValid) { return; }
            var tiers = new int[10];
            var classes = new int[4];
            for (var i = 0; i < 10; i++) { tiers[i] = battle.LobbyMix[i]; }
            for (var i = 0; i < 4; i++) { classes[i] = battle.LobbyMix[10 + i]; }
            Sink.OnlineLobby(Mathf.Max(1, battle.LobbyPlayers), MaxPlayers, battle.LobbyStartsIn, tiers, classes);
        }

        private void RequestHostBattle(BlitzBootstrap bootstrap)
        {
            var net = new BattleNetSetup
            {
                IsClient = false,
                Mode = _battleMode,
                LocalOwner = Runner.LocalPlayer.PlayerId,
                LocalSeat = _seat,
                Build = NetAppSettings.Build,
            };
            net.Guests.AddRange(_guests);
            if (!bootstrap.EnterNetBattle(net)) { return; }
            _battleRequested = true;
            StopQueueWatch();
            // Nobody new finds the room any more; it stays open for rejoins only.
            if (Runner.SessionInfo != null) { Runner.SessionInfo.IsVisible = false; }
            var now = Time.realtimeSinceStartup;
            Debug.Log("[BlitzNet] host entering battle with " + _guests.Count + " guest(s), lobby open "
                + (now - _lobbyOpenedAt).ToString("F0") + "s"
                + (_firstGuestAt >= 0f ? ", first guest " + (now - _firstGuestAt).ToString("F0") + "s ago" : string.Empty));
        }

        private void EnterClientBattle(BlitzBootstrap bootstrap)
        {
            var plan = _pendingPlan;
            _pendingPlan = null;
            BattleNetHooks.PuppetWorld = true;
            var net = new BattleNetSetup
            {
                IsClient = true,
                Mode = plan.Mode,
                Plan = plan,
                LocalOwner = Runner.LocalPlayer.PlayerId,
                LocalSeat = _seat,
            };
            if (!bootstrap.EnterNetBattle(net))
            {
                BattleNetHooks.PuppetWorld = false;
                _pendingPlan = plan;
            }
        }

        /// <summary>Wires and unwires the battle the bootstrap is running.</summary>
        private void TrackBattle(BattleSession battle)
        {
            if (battle == _battle) { return; }
            if (_battle != null) { UnwireHost(); }
            _battle = battle;
            if (_battle == null)
            {
                if (_sawBattle)
                {
                    // Back in the hangar: this online battle is over for good.
                    var handOver = IsHost && !_ended;
                    // Leaving alive is an abandon; leaving once the tank is out for good is not.
                    if (!_ended) { SettleReputation(_localOut); }
                    _ended = true;
                    NetStatusBanner.Hide();
                    if (handOver) { LeaveAsHost(); }
                    else { Shutdown(); }
                }
                return;
            }
            _sawBattle = true;
            _deathHandoverDone = false;
            _reputationArmed = false;
            _reputationCheckAt = -1f;
            _followHandoverAt = -1f;
            _lonelyHostCheckAt = -1f;
            _lonelyReturned = false;
            _previousRoom = null;
            _goneRoom = null;
            _previousHostSlot = -1;
            _localOut = false;
            _pingSum = 0d;
            _pingSamples = 0;
            StartReputation();
            StopQueueWatch();
            if (IsHost) { WireHost(); SendPlanToAll(); }
            else { StartClientBattleIfReady(); }
        }

        private void WireHost()
        {
            if (_hostWired || _battle == null) { return; }
            _hostWired = true;
            BattleNetHooks.ShellLaunched += OnShellLaunched;
            _battle.Finished += OnHostBattleFinished;
            _battle.TeamDamage.KickRequested += OnTeamKillKick;
            _battle.TeamDamage.TurnedBlue += OnTurnedBlue;
        }

        private void UnwireHost()
        {
            if (!_hostWired) { return; }
            _hostWired = false;
            BattleNetHooks.ShellLaunched -= OnShellLaunched;
            if (_battle != null)
            {
                _battle.Finished -= OnHostBattleFinished;
                _battle.TeamDamage.KickRequested -= OnTeamKillKick;
                _battle.TeamDamage.TurnedBlue -= OnTurnedBlue;
            }
        }

        private void SendPlanToAll()
        {
            var plan = _battle != null ? _battle.NetPlan : null;
            if (plan == null)
            {
                Debug.LogError("[BlitzNet] host battle has no plan");
                return;
            }
            foreach (var player in Runner.ActivePlayers)
            {
                if (player == Runner.LocalPlayer) { continue; }
                if (plan.SlotOf(player.PlayerId) < 0) { continue; }
                SendPlan(player, plan);
            }
        }

        private void SendPlan(PlayerRef player, BattleNetPlan plan)
        {
            var bytes = plan.ToBytes();
            Runner.SendReliableDataToPlayer(player, ReliableKey.FromInts(NetChannel.Plan, 0, 0, 0), bytes);
            Debug.Log("[BlitzNet] plan sent to " + player.PlayerId + ": map=" + plan.MapId
                + " slots=" + plan.Slots.Count + " bytes=" + bytes.Length);
        }

        private PlayerRef FindPlayer(int playerId)
        {
            foreach (var player in Runner.ActivePlayers)
            {
                if (player.PlayerId == playerId) { return player; }
            }
            return PlayerRef.None;
        }

        private void OnShellLaunched(BattleTank tank, int shellId, Vector3 origin, Vector3 muzzle, Vector3 direction, int shotId)
        {
            var battle = NetBattle.Instance;
            if (battle == null || _battle == null || !IsHost) { return; }
            var tanks = _battle.SlotTanks;
            for (var i = 0; i < tanks.Count; i++)
            {
                if (tanks[i] != tank) { continue; }
                battle.RPC_Shot((byte)i, (byte)Mathf.Clamp(shellId, 0, 255), origin, muzzle, direction, shotId,
                    tank.BigBoss != null && tank.BigBoss.IsRailgunShot(shotId));
                return;
            }
        }

        private void OnHostBattleFinished(BattleSession battle)
        {
            if (!IsHost) { return; }
            SettleReputation(true);
            _ended = true;
            var winner = battle.Roster != null ? battle.Roster.WinnerTeam : -1;
            if (NetBattle.Instance != null) { NetBattle.Instance.RPC_BattleEnd(winner); }
        }

        /// <summary>The host decided the battle: nothing is to be rescued after this.</summary>
        internal void MarkEnded()
        {
            SettleReputation(true);
            _ended = true;
            _reconnectUntil = -1f;
            NetStatusBanner.Hide();
        }

        // ----- host: lost seats --------------------------------------------------

        private void TickDetachedSeats()
        {
            if (_detached.Count == 0 || _battle == null) { return; }
            var now = Time.realtimeSinceStartup;
            List<int> expired = null;
            foreach (var pair in _detached)
            {
                if (now >= pair.Value) { (expired ?? (expired = new List<int>())).Add(pair.Key); }
            }
            if (expired == null) { return; }
            for (var i = 0; i < expired.Count; i++)
            {
                _detached.Remove(expired[i]);
                _battle.ReleaseSeatToBot(expired[i]);
            }
        }

        // ----- client: reconnect -------------------------------------------------

        private void BeginReconnect()
        {
            if (_ended || _battle == null) { return; }
            if (_reconnectUntil < 0f)
            {
                _reconnectUntil = Time.realtimeSinceStartup + ReconnectSeconds;
                _nextAttemptAt = Time.realtimeSinceStartup + 1f;
                _originalAttempts = 0;
                Debug.LogWarning("[BlitzNet] connection lost in battle, reconnecting to '" + _sessionName + "'");
            }
            NetStatusBanner.Show("online:net/reconnecting");
        }

        private void TickReconnect()
        {
            if (_ended || _battle == null) { _reconnectUntil = -1f; return; }
            var now = Time.realtimeSinceStartup;
            if (now >= _reconnectUntil)
            {
                GiveUpBattle();
                return;
            }
            if (Runner != null && Runner.IsRunning && !Runner.IsServer)
            {
                return; // connected, waiting for the host to hand the seat back
            }
            if (_starting || now < _nextAttemptAt || string.IsNullOrEmpty(_sessionName)) { return; }
            _nextAttemptAt = now + (_handingOver ? HandoverRetry : ReconnectInterval);
            var args = BaseArgs();
            if (_handingOver)
            {
                // One atomic join-or-create on the derived name: the first
                // survivor in becomes the Host, everyone after it a Client.
                // No attempt can fail with GameNotFound (Host-only) or
                // GameIdAlreadyExists (Client-only), so the hand-over logs no
                // errors. Client session creation is enabled only for this
                // hidden hand-over room; matchmaking rooms keep it off.
                // The successor order is kept by the start stagger.
                args.GameMode = GameMode.AutoHostOrClient;
                args.EnableClientSessionCreation = true;
                args.SessionName = _handoverRoom;
                args.IsVisible = false;
            }
            else
            {
                args.GameMode = GameMode.Client;
                args.SessionName = _sessionName;
            }
            StartRunner(args, true);
        }

        private void GiveUpBattle()
        {
            Debug.LogWarning("[BlitzNet] could not get back into the battle");
            _reconnectUntil = -1f;
            _handingOver = false;
            _handoverRoom = null;
            SettleReputation(false);
            _ended = true;
            NetStatusBanner.Show("online:net/lost");
            var bootstrap = BlitzBootstrap.Current;
            if (_battle != null) { _battle.ForceFinish(-1); }
            else if (bootstrap != null && bootstrap.Battle != null) { bootstrap.ToHangar(); }
            HideBannerIn(4f);
        }

        /// <summary>
        /// Client: the host stopped sending the world (it quit, was killed,
        /// paused in the background or lost its network) and neither Fusion's
        /// host migration nor a disconnect callback took over. Returns true
        /// when a reconnect or a hand-over began this frame.
        /// </summary>
        private bool TickHostWatchdog()
        {
            if (_ended || _battle == null || !_sawBattle) { return false; }
            if (_battle.Net == null || !_battle.Net.IsClient) { return false; }
            if (Runner != null && Runner.IsRunning && Runner.IsServer) { return false; }
            // A migration or reconnect in flight has its own deadline.
            if (_reconnectUntil > 0f || _starting) { return false; }
            if (Time.realtimeSinceStartup < _watchdogGraceUntil) { return false; }
            var last = NetBattle.LastHostStateAt;
            if (last < 0f) { return false; }
            var silent = Time.realtimeSinceStartup - last;
            var lostLink = Runner == null || !Runner.IsRunning;
            if (silent < HostSilentSeconds && !lostLink) { return false; }
            if (lostLink && silent < 1f) { return false; }
            if (lostLink)
            {
                // Our own link may be the broken one: try the old room first,
                // TickReconnect escalates to the hand-over when it is gone.
                BeginReconnect();
                return true;
            }
            BeginHandover("host silent for " + silent.ToString("F0") + "s");
            return true;
        }

        /// <summary>
        /// Client: the host is gone for good (left, crashed, lost its network)
        /// and the old room cannot take us back. Moves the battle to a new
        /// host: all survivors meet in one derived room name (one Host, the rest Client),
        /// the first one in becomes the host from its own copy of the battle,
        /// the others rejoin it with their seat tokens.
        /// </summary>
        private void BeginHandover(string why, int target = -1, ShutdownReason shutdownReason = ShutdownReason.Ok)
        {
            if (_ended || _battle == null || _handingOver) { return; }
            _handingOver = true;
            _previousRoom = _sessionName;
            _handoverRoom = HandoverRoomName(_sessionName);
            _migration = null;
            var now = Time.realtimeSinceStartup;
            var local = _battle.Net != null ? _battle.Net.LocalOwner : -1;
            var planned = target >= 0 && _battle.NetPlan != null && _battle.NetPlan.SlotOf(target) >= 0;
            var rank = planned ? (target == local ? 0 : HandoverRank() + 1) : HandoverRank();
            _reconnectUntil = now + ReconnectSeconds;
            // Who gets in first hosts. Planned: the chosen player starts at
            // once, the rest after it had time to create the room. Unplanned:
            // the reputation order staggers the survivors, a bad-reputation
            // peer waits even longer. A lost race only costs the order, the
            // resumed host's reputation check moves the host on afterwards.
            _nextAttemptAt = now + OnlineReputation.HandoverStartDelay(planned, target == local, rank, LocalIsBad());
            _previousHostSlot = _battle.NetPlan != null ? _battle.NetPlan.SlotOf(_battle.NetPlan.HostOwner) : -1;
            Debug.LogWarning("[BlitzNet] " + why + ": handing the host over in '" + _handoverRoom + "' (rank " + rank + ")");
            BlitzTelemetry.MarkStage("host_handover", null, new Dictionary<string, string>
            {
                { "why", why },
                { "rank", rank.ToString() },
            });
            var runner = Runner;
            Runner = null;
            ReleaseRunner(runner, shutdownReason);
            NetStatusBanner.Show("online:net/host_migration");
        }

        /// <summary>Same derived name on every survivor: room, room~h1, room~h2, ...</summary>
        internal static string HandoverRoomName(string room)
        {
            if (string.IsNullOrEmpty(room)) { return "bl-handover"; }
            var mark = room.LastIndexOf("~h", StringComparison.Ordinal);
            int generation;
            if (mark > 0 && int.TryParse(room.Substring(mark + 2), out generation))
            {
                return room.Substring(0, mark) + "~h" + (generation + 1);
            }
            return room + "~h1";
        }

        /// <summary>Place of this peer in the reputation order of the survivors.</summary>
        private int HandoverRank()
        {
            var battle = _battle;
            var plan = battle != null ? battle.NetPlan : null;
            var local = battle != null && battle.Net != null ? battle.Net.LocalOwner : -1;
            if (plan == null || local < 0) { return 0; }
            var order = OnlineReputation.SuccessorOrder(plan, plan.HostOwner, s => !battle.IsOutForGood(s));
            for (var i = 0; i < order.Count; i++)
            {
                if (plan.Slots[order[i]].Owner == local) { return i; }
            }
            return Mathf.Min(order.Count, BattleNetPlan.MaxSlots);
        }

        private bool LocalIsBad()
        {
            var plan = _battle != null ? _battle.NetPlan : null;
            var own = plan != null && _battle.Net != null ? plan.SlotOf(_battle.Net.LocalOwner) : -1;
            return own >= 0 && OnlineReputation.IsBad(plan.Slots[own].Reputation);
        }

        // ----- planned hand-over, reputation -------------------------------------

        /// <summary>
        /// Per frame in a battle: ping samples for the reputation, and on the
        /// host the planned hand-overs (cleaner player at the start, host tank
        /// out for good) and the recovery from split rooms.
        /// </summary>
        private void TickBattleHousekeeping()
        {
            if (_battle == null || _ended || !_sawBattle) { return; }
            var now = Time.realtimeSinceStartup;
            var plan = _battle.NetPlan;
            var own = plan != null && _battle.Net != null ? plan.SlotOf(_battle.Net.LocalOwner) : -1;
            if (!_battle.Started) { return; }
            if (own >= 0) { _localOut = _battle.IsOutForGood(own); }
            if (now >= _nextPingAt)
            {
                _nextPingAt = now + PingSampleInterval;
                var ping = PingHook();
                if (ping > 0) { _pingSum += ping; _pingSamples++; }
            }
            var runner = Runner;
            if (runner == null || !runner.IsRunning || !runner.IsServer || _handingOver || plan == null) { return; }
            var local = runner.LocalPlayer.PlayerId;
            if (!_reputationArmed)
            {
                _reputationArmed = true;
                _reputationCheckAt = now + ReputationCheckDelay;
            }
            if (_reputationCheckAt > 0f && now >= _reputationCheckAt)
            {
                _reputationCheckAt = -1f;
                var better = OnlineReputation.BetterHost(plan, local);
                if (better >= 0 && !_battle.IsOutForGood(better) && IsConnected(plan.Slots[better].Owner))
                {
                    HandHostTo(better, "a cleaner player takes the host");
                    return;
                }
            }
            if (!_deathHandoverDone && _localOut && own >= 0)
            {
                var battle = _battle;
                var order = OnlineReputation.SuccessorOrder(plan, local, s => !battle.IsOutForGood(s));
                if (order.Count > 0 && !battle.IsOutForGood(order[0]) &&
                    !OnlineReputation.IsBad(plan.Slots[order[0]].Reputation) && IsConnected(plan.Slots[order[0]].Owner))
                {
                    _deathHandoverDone = true;
                    HandHostTo(order[0], "the host tank is out of the battle");
                    return;
                }
            }
            if (_followHandoverAt > 0f && now >= _followHandoverAt)
            {
                _followHandoverAt = -1f;
                if (RemotePlayerCount() == 0)
                {
                    // Everyone alive left at once: they most likely lost this
                    // host (a stall on our side) and moved to the hand-over room.
                    DemoteAndHandOver("every player left at once", -1);
                    return;
                }
            }
            if (_lonelyHostCheckAt > 0f && now >= _lonelyHostCheckAt)
            {
                _lonelyHostCheckAt = -1f;
                // Skip when the cloud already reported the old room gone: it
                // cannot take anybody back (the old host crashed or quit).
                if (RemotePlayerCount() == 0 && OtherHumans(plan) > 0 && !string.IsNullOrEmpty(_previousRoom) &&
                    _previousRoom != _goneRoom)
                {
                    ReturnToPreviousRoom();
                }
            }
        }

        private bool IsConnected(int owner)
        {
            if (owner < 0 || Runner == null) { return false; }
            foreach (var player in Runner.ActivePlayers)
            {
                if (player.PlayerId == owner && player != Runner.LocalPlayer) { return true; }
            }
            return false;
        }

        private int RemotePlayerCount()
        {
            var count = 0;
            if (Runner == null) { return 0; }
            foreach (var player in Runner.ActivePlayers)
            {
                if (player != Runner.LocalPlayer) { count++; }
            }
            return count;
        }

        private int OtherHumans(BattleNetPlan plan)
        {
            var count = 0;
            for (var i = 0; plan != null && i < plan.Slots.Count && i < _battle.SlotTanks.Count; i++)
            {
                // The host we moved away from is not counted: if it is alive
                // it follows its players itself (_followHandoverAt), if it
                // crashed its room is closed and probing it only fails.
                if (i == _previousHostSlot) { continue; }
                if (plan.Slots[i].IsHuman && _battle.SlotTanks[i] != _battle.Player) { count++; }
            }
            return count;
        }

        /// <summary>
        /// Host: gives the running battle to the player of <paramref name="slot"/>.
        /// Every client is told who the new host is, so it creates the new
        /// room at once and the others only join it; this peer follows as a
        /// client from its own copy of the battle.
        /// </summary>
        private async void HandHostTo(int slot, string why)
        {
            var runner = Runner;
            var plan = _battle != null ? _battle.NetPlan : null;
            if (runner == null || !runner.IsRunning || !runner.IsServer || plan == null || _handingOver || _ended) { return; }
            if (slot < 0 || slot >= plan.Slots.Count || plan.Slots[slot].Owner < 0) { return; }
            var target = plan.Slots[slot].Owner;
            _handingOver = true;
            Debug.Log("[BlitzNet] " + why + ": handing the host to player " + target
                + " (reputation " + plan.Slots[slot].Reputation + ")");
            BlitzTelemetry.MarkStage("host_handover_planned", null, new Dictionary<string, string> { { "why", why } });
            var payload = BitConverter.GetBytes(target);
            foreach (var player in runner.ActivePlayers)
            {
                if (player == runner.LocalPlayer) { continue; }
                runner.SendReliableDataToPlayer(player, ReliableKey.FromInts(NetChannel.HostHandover, 0, 0, 0), payload);
            }
            await Task.Delay(HandHostNoticeMs);
            _handingOver = false;
            if (this == null || Runner != runner || _ended || _battle == null) { return; }
            DemoteAndHandOver(why, target);
        }

        private void DemoteAndHandOver(string why, int target)
        {
            if (_battle == null || _ended) { return; }
            UnwireHost();
            _detached.Clear();
            BattleNetHooks.PuppetWorld = true;
            _battle.DemoteToNetClient();
            BeginHandover(why, target);
        }

        /// <summary>
        /// New host that nobody followed within a few seconds: it most likely
        /// was the one that lost the link. Try the old room once before
        /// keeping the battle here.
        /// </summary>
        private void ReturnToPreviousRoom()
        {
            Debug.LogWarning("[BlitzNet] nobody followed into '" + _sessionName + "', trying '" + _previousRoom + "' again");
            _lonelyReturned = true;
            UnwireHost();
            _detached.Clear();
            BattleNetHooks.PuppetWorld = true;
            _battle.DemoteToNetClient();
            _sessionName = _previousRoom;
            _previousRoom = null;
            var runner = Runner;
            Runner = null;
            ReleaseRunner(runner);
            _handingOver = false;
            _reconnectUntil = Time.realtimeSinceStartup + ReconnectSeconds;
            _nextAttemptAt = Time.realtimeSinceStartup + 0.2f;
            _originalAttempts = OriginalRoomAttempts - 1;
            NetStatusBanner.Show("online:net/reconnecting");
        }

        private static AccountData CurrentAccount()
        {
            var bootstrap = BlitzBootstrap.Current;
            return bootstrap != null && bootstrap.Accounts != null ? bootstrap.Accounts.GetCurrentAccount() : null;
        }

        private static void SaveAccounts()
        {
            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap == null || bootstrap.Saves == null || bootstrap.Accounts == null) { return; }
            try { bootstrap.Saves.SaveBook(bootstrap.Accounts); }
            catch (Exception error) { Debug.LogWarning("[BlitzNet] could not save the reputation: " + error.Message); }
        }

        private void StartReputation()
        {
            var account = CurrentAccount();
            _repOpen = account != null;
            if (account == null) { return; }
            OnlineReputation.BattleStarted(account.Reputation);
            SaveAccounts();
        }

        /// <summary>Clean = played to the end, or left only once the tank was out for good.</summary>
        private void SettleReputation(bool clean)
        {
            if (!_repOpen) { return; }
            _repOpen = false;
            var account = CurrentAccount();
            if (account == null) { return; }
            var ping = _pingSamples > 0 ? (int)Math.Round(_pingSum / _pingSamples) : -1;
            OnlineReputation.BattleSettled(account.Reputation, clean, ping);
            Debug.Log("[BlitzNet] battle settled " + (clean ? "clean" : "abandoned") + ", mean ping " + ping);
            SaveAccounts();
        }

        /// <summary>
        /// Host leaving a live battle on purpose: tells every client to hand
        /// the battle over right away instead of waiting for the silence
        /// watchdog, then closes its runner.
        /// </summary>
        private async void LeaveAsHost()
        {
            var runner = Runner;
            if (runner != null && runner.IsRunning && runner.IsServer)
            {
                Debug.Log("[BlitzNet] host leaves the running battle, handing it over");
                AnnounceHostLeaving(runner);
                await Task.Delay(HostLeaveNoticeMs);
            }
            if (this == null) { return; }
            Shutdown();
        }

        private static void AnnounceHostLeaving(NetworkRunner runner)
        {
            foreach (var player in runner.ActivePlayers)
            {
                if (player == runner.LocalPlayer) { continue; }
                runner.SendReliableDataToPlayer(player, ReliableKey.FromInts(NetChannel.HostLeaving, 0, 0, 0), new byte[] { 1 });
            }
        }

        // A timestamp instead of Invoke(nameof(...)): string lookups would pin
        // the method name against symbol obfuscation.
        private float _hideBannerAt = -1f;

        private void HideBannerIn(float seconds) { _hideBannerAt = Time.realtimeSinceStartup + seconds; }

        // ----- synchronized start ------------------------------------------------

        private bool Released
        {
            get
            {
                var battle = NetBattle.Instance;
                return _hostStarted || (battle != null && battle.Object != null && battle.Object.IsValid && battle.IntroReleased);
            }
        }

        private void TickSynchronizedStart(BlitzBootstrap bootstrap)
        {
            var intro = bootstrap.MapIntro;
            if (_battle == null || _ended || intro == null || _battle.Started)
            {
                if (_waitingBanner) { _waitingBanner = false; NetStatusBanner.Hide(); }
                return;
            }
            var now = Time.realtimeSinceStartup;
            if (!IsHost)
            {
                if (!_loadedSent)
                {
                    _loadedSent = true;
                    Runner.SendReliableDataToServer(ReliableKey.FromInts(NetChannel.Loaded, 0, 0, 0), new byte[] { 1 });
                    Debug.Log("[BlitzNet] battle loaded, waiting for the host to release the intro");
                }
                if (intro.TimeUp && _clientHoldAt < 0f) { _clientHoldAt = now; }
            }
            else if (!Released)
            {
                if (_hostIntroAt < 0f) { _hostIntroAt = now; }
                var all = AllLoaded();
                if (intro.TimeUp && (all || now - _hostIntroAt >= LoadWaitSeconds))
                {
                    var battle = NetBattle.Instance;
                    if (battle != null && battle.Object != null && battle.Object.IsValid)
                    {
                        battle.IntroReleased = true;
                        Debug.Log("[BlitzNet] releasing every intro: " + (all ? "all players loaded" : "load wait timed out")
                            + " after " + (now - _hostIntroAt).ToString("F1") + "s");
                    }
                }
            }
            var waiting = intro.TimeUp && !Released;
            if (waiting != _waitingBanner)
            {
                _waitingBanner = waiting;
                if (waiting) { NetStatusBanner.Show("online:net/waiting_players"); }
                else { NetStatusBanner.Hide(); }
            }
        }

        /// <summary>Host: every connected human of the plan reported a loaded map.</summary>
        private bool AllLoaded()
        {
            var plan = _battle != null ? _battle.NetPlan : null;
            if (plan == null) { return true; }
            foreach (var player in Runner.ActivePlayers)
            {
                if (player == Runner.LocalPlayer || plan.SlotOf(player.PlayerId) < 0) { continue; }
                if (!_loaded.Contains(player.PlayerId)) { return false; }
            }
            return true;
        }

        private static bool HoldIntroHook()
        {
            var s = Current;
            if (s == null || s._battle == null || s._ended || s.Runner == null || !s.Runner.IsRunning) { return false; }
            if (s.Released) { return false; }
            // A client never waits forever on a host that went quiet.
            if (!s.IsHost && s._clientHoldAt > 0f && Time.realtimeSinceStartup - s._clientHoldAt > LoadWaitSeconds + 10f) { return false; }
            return true;
        }

        private static bool IntroReleasedHook()
        {
            // Only a client's intro is cut short; the host releases when its
            // own intro is over, so its curtain drops the same moment.
            var s = Current;
            return s != null && !s.IsHost && s._battle != null && !s._ended && s.Released;
        }

        /// <summary>
        /// Client: its own round trip to the host. Host: the mean round trip
        /// of its clients (its own is zero by definition).
        /// </summary>
        private static int PingHook()
        {
            var s = Current;
            var runner = s != null ? s.Runner : null;
            if (runner == null || !runner.IsRunning || s._battle == null) { return -1; }
            if (!runner.IsServer) { return Mathf.RoundToInt((float)(runner.GetPlayerRtt(runner.LocalPlayer) * 1000.0)); }
            double sum = 0; var n = 0;
            foreach (var player in runner.ActivePlayers)
            {
                if (player == runner.LocalPlayer) { continue; }
                sum += runner.GetPlayerRtt(player);
                n++;
            }
            // Host with nobody connected has no link to measure: hide the
            // label instead of showing a misleading 0 ms.
            return n == 0 ? -1 : Mathf.RoundToInt((float)(sum / n * 1000.0));
        }

        // ----- host migration ----------------------------------------------------

        public async void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken)
        {
            if (runner != Runner) { return; }
            Debug.LogWarning("[BlitzNet] host left, migrating; this peer becomes " + hostMigrationToken.GameMode);
            if (_ended)
            {
                Shutdown();
                return;
            }
            if (!_sawBattle)
            {
                // Still in the lobby: simply look for (or open) another room.
                _migration = null;
                // Fusion expects the migrating runner to go down with this reason.
                await ShutdownQuietly(runner, ShutdownReason.HostMigration);
                if (this == null) { return; }
                ResetLobby();
                Start();
                return;
            }
            // Fusion's token only reaches the peers it chose to tell; the own
            // hand-over gives every survivor the same room and one new host.
            BeginHandover("Fusion reported the host gone", -1, ShutdownReason.HostMigration);
        }

        /// <summary>
        /// Lone host: true when the lobby lists a battle to move into now
        /// (<see cref="OnlineBalancer.MergeTarget"/>). Checked twice a second.
        /// </summary>
        private bool MergeDue(float now)
        {
            if (now < _mergeCheckAt) { return false; }
            _mergeCheckAt = now + MergeCheckInterval;
            _mergeRooms.Clear();
            foreach (var battle in _queueWatcher.Battles)
            {
                var room = battle;
                room.Capacity = MaxPlayers;
                _mergeRooms.Add(room);
            }
            var spread = OnlineBalancer.TierSpreadFor(Mathf.Max(0f, now - _matchmakeStartedAt));
            var hosted = now - _lobbyOpenedAt;
            // Without patience limits: the battle this host would end up in.
            var candidate = OnlineBalancer.MergeTarget(_mergeRooms, _sessionName, _tier, spread, hosted, float.MaxValue, _skipRooms);
            var name = candidate >= 0 ? _mergeRooms[candidate].Name : null;
            if (name != _mergeTarget)
            {
                _mergeTarget = name;
                _mergeTargetSince = now;
            }
            if (name == null) { return false; }
            var target = OnlineBalancer.MergeTarget(_mergeRooms, _sessionName, _tier, spread, hosted, now - _mergeTargetSince, _skipRooms);
            if (target < 0) { return false; }
            Debug.Log("[BlitzNet] lobby: moving to '" + _mergeRooms[target].Name + "' tier " + _mergeRooms[target].Tier
                + " players " + _mergeRooms[target].Players + " after " + hosted.ToString("F0") + "s alone");
            return true;
        }

        /// <summary>Alone in a fresh room: close it and look for another one.</summary>
        private async void Rematch()
        {
            if (_rematching) { return; }
            _rematching = true;
            Debug.Log("[BlitzNet] nobody joined, searching again");
            var own = _sessionName;
            var runner = Runner;
            Runner = null;
            if (runner != null) { await ShutdownQuietly(runner); }
            _rematching = false;
            if (this == null) { return; }
            ResetLobby();
            // The lobby may still list the battle just closed.
            if (!string.IsNullOrEmpty(own)) { _skipRooms.Add(own); }
            Start();
        }

        private void ResetLobby(bool fresh = true)
        {
            if (fresh) { _matchmakeAttempts = 0; _skipRooms.Clear(); }
            _lonelyRematchAt = -1f;
            _mergeTarget = null;
            _mergeTargetSince = -1f;
            _mergeCheckAt = -1f;
            _guests.Clear();
            _firstGuestAt = -1f;
            _lobbyOpenedAt = -1f;
            _battleRequested = false;
            _hostStarted = false;
            _pendingPlan = null;
        }

        /// <summary>New host: rebuild the network side of a battle that never stopped.</summary>
        private void OnHostMigrationResume(NetworkRunner runner) { ResumeAsHost(runner); }

        private void ResumeAsHost(NetworkRunner runner)
        {
            Debug.Log("[BlitzNet] resuming the battle as the new host in '" + _sessionName + "'");
            _handingOver = false;
            _handoverRoom = null;
            _originalAttempts = 0;
            _migration = null;
            NetBattle.MarkHostAlive();
            if (!SpawnBattleObject(runner)) { return; }
            // The intros were released before this host took over, or nobody
            // is left to wait for; never hold a migrated battle.
            if (NetBattle.Instance != null) { NetBattle.Instance.IntroReleased = true; }
            _battleRequested = true;
            _startAnnounced = true;
            if (runner.SessionInfo != null) { runner.SessionInfo.IsVisible = false; }
            if (_battle == null) { return; }
            BattleNetHooks.PuppetWorld = false;
            _battle.PromoteToNetHost(runner.LocalPlayer.PlayerId);
            WireHost();
            var plan = _battle.NetPlan;
            var releaseAt = Time.realtimeSinceStartup + RejoinSeconds;
            for (var i = 0; plan != null && i < plan.Slots.Count; i++)
            {
                if (BattleSession.IsDetachedOwner(plan.Slots[i].Owner)) { _detached[i] = releaseAt; }
            }
            _deathHandoverDone = false;
            _reputationArmed = true;
            _reputationCheckAt = Time.realtimeSinceStartup + ResumedReputationDelay;
            if (!_lonelyReturned && !string.IsNullOrEmpty(_previousRoom))
            {
                _lonelyHostCheckAt = Time.realtimeSinceStartup + LonelyHostSeconds;
            }
            _reconnectUntil = -1f;
            NetStatusBanner.Hide();
        }

        // ----- teardown ----------------------------------------------------------

        private void Fail(string key)
        {
            Debug.LogWarning("[BlitzNet] session failed: " + key);
            if (Sink != null) { Sink.OnlineFailed(key); }
            Failed?.Invoke(key);
            Shutdown();
        }

        // ----- anti-cheat --------------------------------------------------------

        public const string TeamKillKickKey = "online:net/kicked_teamkill";

        /// <summary>
        /// Host: a player in its strict week went over the team-damage limit
        /// of one battle. Tell them why, refuse their seat for the rest of the
        /// battle and disconnect them; the AI takes the seat over.
        /// </summary>
        private void OnTeamKillKick(BattleTank tank)
        {
            var runner = Runner;
            if (runner == null || !runner.IsServer || _battle == null || tank == null) { return; }
            if (tank == _battle.Player) { KickSelfForTeamKill(); return; }
            var slot = _battle.SlotOfTank(tank);
            var plan = _battle.NetPlan;
            if (plan == null || slot < 0 || slot >= plan.Slots.Count || !plan.Slots[slot].IsHuman) { return; }
            var owner = plan.Slots[slot].Owner;
            if (NetBattle.Instance != null) { NetBattle.Instance.RaiseNotice(ChatNotice.TeamKillKick, slot); }
            foreach (var player in runner.ActivePlayers)
            {
                if (player.PlayerId != owner || player == runner.LocalPlayer) { continue; }
                var seat = SeatOf(runner.GetPlayerConnectionToken(player));
                if (seat != null) { _bannedSeats.Add(seat); }
                Debug.LogWarning("[BlitzNet] kicking player " + owner + " for team damage");
                BlitzTelemetry.MarkStage("teamkill_kick", null, new Dictionary<string, string>
                {
                    { "player", owner.ToString() },
                });
                runner.SendReliableDataToPlayer(player, ReliableKey.FromInts(NetChannel.TeamKillKick, 0, 0, 0), new byte[] { 1 });
                StartCoroutine(DisconnectLater(runner, player));
                return;
            }
        }

        /// <summary>Host: a yellow chat notice for everyone that a player turned blue.</summary>
        private void OnTurnedBlue(BattleTank tank)
        {
            var runner = Runner;
            if (runner == null || !runner.IsServer || _battle == null || tank == null) { return; }
            var slot = _battle.SlotOfTank(tank);
            if (slot >= 0 && NetBattle.Instance != null) { NetBattle.Instance.RaiseNotice(ChatNotice.TurnedBlue, slot); }
        }

        /// <summary>Lets the kick notice arrive before the connection drops.</summary>
        private System.Collections.IEnumerator DisconnectLater(NetworkRunner runner, PlayerRef player)
        {
            yield return new WaitForSecondsRealtime(1.5f);
            if (runner != null && runner == Runner && runner.IsRunning) { runner.Disconnect(player); }
        }

        /// <summary>Client: the host removed us for team damage.</summary>
        private void OnTeamKillKicked()
        {
            if (!InBattle) { return; }
            Debug.LogWarning("[BlitzNet] removed from the battle for team damage");
            _ended = true;
            _reconnectUntil = -1f;
            NetStatusBanner.Show(TeamKillKickKey);
            if (_battle != null) { _battle.ForceFinish(-1); }
            HideBannerIn(6f);
        }

        /// <summary>
        /// Hard client-side online bans: the host tried to move this battle into
        /// the offline-only Sandbox or to seat a mod tank in it. An honest client
        /// disconnects itself at once; before the battle that is a plain
        /// matchmaking failure. <paramref name="key"/> is the notice to show.
        /// </summary>
        private async void LeaveForBan(string key)
        {
            if (string.IsNullOrEmpty(key)) { key = SandboxOnlineBan.ErrorKey; }
            Debug.LogWarning("[BlitzNet] online ban (" + key + "); leaving the battle");
            if (!InBattle)
            {
                if (!_ended) { Fail(key); }
                return;
            }
            var battle = _battle;
            NetStatusBanner.Show(key);
            await LeaveForAntiCheat();
            if (battle != null) { battle.ForceFinish(-1); }
            if (this != null) { HideBannerIn(6f); }
        }

        /// <summary>
        /// The host itself went over the limit: hand the battle over to the
        /// next host like a player who leaves, then end it locally.
        /// </summary>
        private async void KickSelfForTeamKill()
        {
            Debug.LogWarning("[BlitzNet] host removed itself from the battle for team damage");
            var battle = _battle;
            UnwireHost();
            NetStatusBanner.Show(TeamKillKickKey);
            await LeaveForAntiCheat();
            if (battle != null) { battle.ForceFinish(-1); }
            if (this != null) { HideBannerIn(6f); }
        }

        /// <summary>
        /// Host: a remote player sent input no honest client can produce.
        /// Disconnect them and refuse their seat token for the rest of the
        /// battle; the seat goes to the AI like any other lost seat.
        /// </summary>
        public static void KickCheater(NetworkRunner runner, PlayerRef player)
        {
            var session = Current;
            if (session == null || runner == null || runner != session.Runner || !runner.IsServer) { return; }
            var seat = SeatOf(runner.GetPlayerConnectionToken(player));
            if (seat != null) { session._bannedSeats.Add(seat); }
            Debug.LogWarning("[BlitzNet] anti-cheat: kicking player " + player.PlayerId + " for illegal input");
            BlitzTelemetry.MarkStage("anticheat_kick", null, new Dictionary<string, string>
            {
                { "player", player.PlayerId.ToString() },
            });
            runner.Disconnect(player);
        }

        /// <summary>
        /// Local memory edit caught (see <see cref="AntiCheatResponder"/>):
        /// leave the battle for good. As host, push a fresh migration snapshot
        /// first so the next host resumes from the current state rather than
        /// one up to five seconds old; shutting down then hands the host over.
        /// </summary>
        public static async Task LeaveForAntiCheat()
        {
            var session = Current;
            if (session == null) { return; }
            session._ended = true;
            session._reconnectUntil = -1f;
            var runner = session.Runner;
            session.Runner = null;
            if (runner == null || !runner.IsRunning) { return; }
            if (runner.IsServer && session._sawBattle)
            {
                AnnounceHostLeaving(runner);
                try
                {
                    var pushed = await runner.PushHostMigrationSnapshot();
                    Debug.Log("[BlitzNet] anti-cheat: host migration snapshot " + (pushed ? "pushed" : "failed"));
                }
                catch (Exception e) { Debug.LogWarning("[BlitzNet] anti-cheat: snapshot failed: " + e.Message); }
            }
            try { await runner.Shutdown(true, ShutdownReason.Ok); }
            catch (Exception e) { Debug.LogWarning("[BlitzNet] anti-cheat: shutdown failed: " + e.Message); }
        }

        public void Shutdown()
        {
            var runner = Runner;
            Runner = null;
            ReleaseRunner(runner);
            // Free the session slot now: Destroy only lands at the end of the
            // frame, and the hangar may already ask for the next battle.
            ReleaseStatics();
            if (this != null) { Destroy(gameObject); }
        }

        /// <summary>
        /// A session that can no longer take the player anywhere: its battle
        /// is over (or never came) and nothing is running for it.
        /// </summary>
        internal bool IsStale
        {
            get
            {
                var bootstrap = BlitzBootstrap.Current;
                var battleRunning = bootstrap != null && bootstrap.Battle != null;
                if (battleRunning) { return false; }
                if (_ended || _sawBattle) { return true; }
                return Sink == null && !_starting && (Runner == null || !Runner.IsRunning);
            }
        }

        /// <summary>Drops a leftover session so the hangar can start a new one.</summary>
        internal static void DropStale()
        {
            var session = Current;
            if (ReferenceEquals(session, null)) { return; }
            if (session == null)
            {
                // The object is gone but OnDestroy never cleared the slot.
                Current = null;
                return;
            }
            if (!session.IsStale) { return; }
            Debug.LogWarning("[BlitzNet] dropping a leftover session from the last battle");
            session.Sink = null;
            session.Shutdown();
        }

        private void ReleaseStatics()
        {
            StopQueueWatch();
            if (Current != this) { return; }
            BattleNetHooks.HoldIntro = null;
            BattleNetHooks.IntroReleased = null;
            BattleNetHooks.PingMs = null;
            Current = null;
        }

        /// <summary>
        /// Fusion runs one peer per process: a runner from the last battle that
        /// is still shutting down makes the next StartGame fail at once.
        /// </summary>
        private static async Task WaitForOtherRunners(NetworkRunner keep)
        {
            var until = Time.realtimeSinceStartup + 6f;
            while (Time.realtimeSinceStartup < until)
            {
                var busy = false;
                foreach (var other in NetworkRunner.Instances)
                {
                    if (other != null && other != keep && !other.IsShutdown) { busy = true; break; }
                }
                if (!busy) { return; }
                await Task.Delay(100);
            }
            Debug.LogWarning("[BlitzNet] an old runner is still shutting down; starting anyway");
        }

        private void OnUprisingActionRequested(byte action)
        {
            if (IsHost || action == 0 || action > BattleRemoteDriver.MaxAction) { return; }
            _uprisingAction = action;
            unchecked { _uprisingSequence++; }
            if (_uprisingSequence == 0) { _uprisingSequence = 1; }
        }

        private void OnDestroy()
        {
            BattleNetHooks.UprisingActionRequested -= OnUprisingActionRequested;
            UnwireHost();
            BattleNetHooks.PuppetWorld = false;
            ReleaseStatics();
        }

        // ----- INetworkRunnerCallbacks ----------------------------------------

        public void OnInput(NetworkRunner runner, NetworkInput input)
        {
            if (runner != Runner || runner.IsServer) { return; }
            var battle = _battle;
            if (battle == null || battle.Player == null || battle.Controls == null) { return; }

            var controls = battle.Controls;
            var data = new NetInput
            {
                // The host kicks axes outside [-1, 1] as a modified client.
                Forward = Mathf.Clamp(controls.DriveForward, -1f, 1f),
                Right = Mathf.Clamp(controls.DriveRight, -1f, 1f),
                ShellId = (byte)Mathf.Clamp(battle.Player.ShellId, 0, 255),
                UprisingAction = _uprisingAction,
                UprisingSequence = _uprisingSequence,
            };
            var fire = controls.FireHeld;
            if (battle.View != null)
            {
                data.Aim = battle.View.AimTarget;
                data.Buttons.Set(NetButton.HasAim, true);
                data.Buttons.Set(NetButton.Arty, battle.View.ArtyMode);
            }
            if (AutoDrive) { ApplyAutoDrive(ref data, ref fire, battle); }
            data.Buttons.Set(NetButton.Fire, fire);
            data.Buttons.Set(NetButton.TurretBlock, battle.Player.TurretBlock);
            input.Set(data);
        }

        /// <summary>Smoke only: circle and fire every few seconds.</summary>
        private static void ApplyAutoDrive(ref NetInput data, ref bool fire, BattleSession battle)
        {
            var t = Time.realtimeSinceStartup;
            data.Forward = 1f;
            data.Right = Mathf.Sin(t * 0.5f) > 0f ? 1f : 0f;
            fire = (int)t % 4 == 0;
            // Arty smoke: the overhead camera's own aim point, as a player's.
            if (battle.View != null && battle.View.ArtyMode) { return; }
            var tank = battle.Player.transform;
            data.Aim = tank.position + tank.forward * 100f;
            data.Buttons.Set(NetButton.HasAim, true);
        }

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            if (runner != Runner) { return; }
            Debug.Log("[BlitzNet] player joined: " + player.PlayerId);
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            if (runner != Runner) { return; }
            Debug.Log("[BlitzNet] player left: " + player.PlayerId);
            _guests.RemoveAll(g => g.Owner == player.PlayerId);
            if (_guests.Count == 0 && !_battleRequested) { _firstGuestAt = -1f; }
            if (!runner.IsServer || _battle == null || _battle.NetPlan == null || _ended) { return; }
            var slot = _battle.NetPlan.SlotOf(player.PlayerId);
            if (slot < 0) { return; }
            _battle.DetachSeat(slot);
            _detached[slot] = Time.realtimeSinceStartup + RejoinSeconds;
            Debug.Log("[BlitzNet] seat " + slot + " waits " + RejoinSeconds + "s for its player");
            var seat = _battle.NetPlan.Slots[slot].Seat;
            if (!_handingOver && _battle.Started && !_battle.IsOutForGood(slot) &&
                (seat == null || !_bannedSeats.Contains(seat)))
            {
                // Re-checked shortly: if every remote player is gone by then,
                // this host was the one that stalled and follows them.
                _followHandoverAt = Time.realtimeSinceStartup + 0.3f;
            }
        }

        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data)
        {
            if (runner != Runner) { return; }
            int channel, b, c, d;
            key.GetInts(out channel, out b, out c, out d);
            if (data.Length > 16 * 1024)
            {
                Debug.LogWarning("[BlitzNet] oversized data from " + player.PlayerId);
                if (runner.IsServer) { runner.Disconnect(player); }
                return;
            }
            var bytes = data.ToArray();
            try
            {
                if (channel == NetChannel.Hello && runner.IsServer)
                {
                    OnHello(player, BattleNetHello.FromBytes(bytes));
                }
                else if (channel == NetChannel.Plan && !runner.IsServer)
                {
                    OnPlan(BattleNetPlan.FromBytes(bytes));
                }
                else if (channel == NetChannel.TeamKillKick && !runner.IsServer)
                {
                    OnTeamKillKicked();
                }
                else if (channel == NetChannel.HostLeaving && !runner.IsServer)
                {
                    _hostLeftNotice = true;
                }
                else if (channel == NetChannel.HostHandover && !runner.IsServer)
                {
                    if (bytes.Length != 4) { throw new FormatException("hand-over notice size " + bytes.Length); }
                    _handoverTarget = BitConverter.ToInt32(bytes, 0);
                    _handoverNotice = true;
                }
                else if (channel == NetChannel.Loaded && runner.IsServer)
                {
                    if (_loaded.Add(player.PlayerId)) { Debug.Log("[BlitzNet] player " + player.PlayerId + " loaded the battle"); }
                }
            }
            catch (Exception error) when (!runner.IsServer && OnlineBan.IsBan(error))
            {
                LeaveForBan(OnlineBan.KeyOf(error));
            }
            catch (Exception error)
            {
                // Untrusted bytes: a malformed packet costs the sender its seat,
                // never the receiver its session.
                Debug.LogWarning("[BlitzNet] rejected data on channel " + channel + " from "
                    + player.PlayerId + ": " + error.Message);
                if (runner.IsServer) { runner.Disconnect(player); }
            }
        }

        private void OnHello(PlayerRef player, BattleNetHello hello)
        {
            if (hello.Build != NetAppSettings.Build)
            {
                Debug.LogWarning("[BlitzNet] player " + player.PlayerId + " runs build " + hello.Build);
                Runner.Disconnect(player);
                return;
            }
            var seat = SeatOf(Runner.GetPlayerConnectionToken(player));
            if (seat == null)
            {
                Runner.Disconnect(player);
                return;
            }
            if (_battleRequested)
            {
                RestoreSeat(player, seat);
                return;
            }
            // SECURITY: the list only advertises the tier, so a client may still
            // knock with a tank this battle does not take.
            int tier, column;
            BattleOnline.Classify(hello.TankId, out tier, out column);
            if (_mode == StartMode.Matchmake &&
                !OnlineBalancer.Accepts(_tier, tier, OnlineBalancer.ExpandedTierSpread))
            {
                Debug.LogWarning("[BlitzNet] player " + player.PlayerId + " brings tier " + tier
                    + " into a tier " + _tier + " battle");
                Runner.Disconnect(player);
                return;
            }
            _guests.RemoveAll(g => g.Owner == player.PlayerId || g.Seat == seat);
            _guests.Add(new BattleNetSetup.Guest { Owner = player.PlayerId, Seat = seat, Hello = hello });
            if (_firstGuestAt < 0f) { _firstGuestAt = Time.realtimeSinceStartup; }
            Debug.Log("[BlitzNet] hello from " + player.PlayerId + ": tank=" + hello.TankId
                + " name=" + hello.Name + " guests=" + _guests.Count + "/" + ExpectedGuests);
        }

        /// <summary>Host, battle running: a peer proved a seat, give it back.</summary>
        private void RestoreSeat(PlayerRef player, string seat)
        {
            var plan = _battle != null ? _battle.NetPlan : null;
            var slot = plan != null ? plan.SlotOfSeat(seat) : -1;
            if (slot < 0 || _battle.SlotTanks[slot] == _battle.Player)
            {
                Debug.LogWarning("[BlitzNet] late hello from " + player.PlayerId + " without a seat, refused");
                Runner.Disconnect(player);
                return;
            }
            var previous = plan.Slots[slot].Owner;
            if (!BattleSession.IsDetachedOwner(previous) && previous != player.PlayerId)
            {
                // The old connection has not timed out yet; the token wins.
                var stale = FindPlayer(previous);
                if (stale != PlayerRef.None) { Runner.Disconnect(stale); }
            }
            if (!_battle.ReassignSeat(slot, player.PlayerId))
            {
                Runner.Disconnect(player);
                return;
            }
            _detached.Remove(slot);
            Debug.Log("[BlitzNet] seat " + slot + " restored to player " + player.PlayerId);
            SendPlan(player, plan);
        }

        private void OnPlan(BattleNetPlan plan)
        {
            _watchdogGraceUntil = Time.realtimeSinceStartup + WatchdogGrace;
            if (plan.Build != NetAppSettings.Build)
            {
                Debug.LogWarning("[BlitzNet] host runs build " + plan.Build);
                Fail("online:error/version");
                return;
            }
            var local = Runner.LocalPlayer.PlayerId;
            if (plan.SlotOf(local) < 0)
            {
                if (InBattle) { GiveUpBattle(); } else { Fail("online:error/room_closed"); }
                return;
            }
            if (_battle != null)
            {
                // Reconnected or migrated: keep the battle on screen.
                if (!_battle.AdoptNetPlan(plan, local))
                {
                    Debug.LogWarning("[BlitzNet] the host's plan is a different battle");
                    GiveUpBattle();
                    return;
                }
                Debug.Log("[BlitzNet] back in the battle as player " + local + " in '" + _sessionName + "'");
                _reconnectUntil = -1f;
                _migration = null;
                _handingOver = false;
                _handoverRoom = null;
                _originalAttempts = 0;
                NetBattle.MarkHostAlive();
                NetStatusBanner.Hide();
                return;
            }
            Debug.Log("[BlitzNet] plan received: map=" + plan.MapId + " slots=" + plan.Slots.Count);
            Status("online:queue/joining");
            _pendingPlan = plan;
        }

        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token)
        {
            if (runner != Runner) { request.Refuse(); return; }
            var seat = SeatOf(token);
            if (seat == null || _bannedSeats.Contains(seat)) { request.Refuse(); return; }
            if (!_battleRequested)
            {
                // The headroom is for rejoins only: the lobby stops at 7 vs 7.
                if (runner.ActivePlayers.Count() >= MaxPlayers) { request.Refuse(); }
                else { request.Accept(); }
                return;
            }
            // SECURITY: once the battle runs only a seat holder gets in.
            var plan = _battle != null ? _battle.NetPlan : null;
            if (plan != null && plan.SlotOfSeat(seat) >= 0 && seat != _seat) { request.Accept(); }
            else { request.Refuse(); }
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            Debug.Log("[BlitzNet] shutdown: " + shutdownReason);
            if (runner != Runner) { return; } // a runner we replaced on purpose
            if (_starting) { return; } // StartGame failed: StartRunner handles it
            Runner = null;
            if (shutdownReason == ShutdownReason.HostMigration) { return; }
            if (_ended)
            {
                if (_battle == null && this != null) { Destroy(gameObject); }
                return;
            }
            if (_battle == null)
            {
                if (!_sawBattle && !SearchAgain(shutdownReason.ToString())) { Fail(ReasonKey(shutdownReason)); }
                else if (this != null) { Destroy(gameObject); }
                return;
            }
            if (runner.IsServer)
            {
                // The host itself dropped: the clients hand the battle over to
                // a new host among themselves; this copy is no longer online.
                Debug.LogWarning("[BlitzNet] host lost the session; the clients take the battle over");
                _detached.Clear();
                _ended = true;
                NetStatusBanner.Show("online:net/lost");
                _battle.ForceFinish(-1);
                HideBannerIn(4f);
                return;
            }
            BeginReconnect();
        }

        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        {
            if (runner != Runner) { return; }
            Debug.LogWarning("[BlitzNet] disconnected from host: " + reason);
            if (InBattle && reason != NetDisconnectReason.Requested) { BeginReconnect(); }
        }

        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason)
        {
            Debug.LogWarning("[BlitzNet] connect failed: " + reason);
        }

        private void Status(string key)
        {
            if (Sink != null && !_sawBattle) { Sink.OnlineStatus(key); }
        }

        public void OnConnectedToServer(NetworkRunner runner) { Debug.Log("[BlitzNet] connected to host"); }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        // Still part of INetworkRunnerCallbacks in 2.1 although marked obsolete.
#pragma warning disable CS0618
        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
#pragma warning restore CS0618
        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
        {
            if (runner != Runner) { return; }
            _sessionList = sessionList != null ? new List<SessionInfo>(sessionList) : new List<SessionInfo>();
        }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
    }

    /// <summary>The hangar's online mode, backed by <see cref="NetSession.Matchmake"/>.</summary>
    public sealed class NetOnlineService : IBattleOnlineService
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            // Chained: the dedicated-server client registers its own leave too.
            var previous = AntiCheatResponder.LeaveOnline;
            AntiCheatResponder.LeaveOnline = async () =>
            {
                if (previous != null) { await previous(); }
                await NetSession.LeaveForAntiCheat();
            };
            // A build without an App ID offers no online mode at all.
            if (NetAppSettings.HasAppId()) { BattleOnline.PhotonService = new NetOnlineService(); }
        }

        public void Begin(IBattleOnlineSink sink, BattleMode mode)
        {
            NetSession.DropStale();
            if (NetSession.Current != null)
            {
                sink.OnlineFailed("online:error/no_connection");
                return;
            }
            if (AntiCheatBan.IsBanned)
            {
                sink.OnlineFailed(NetSession.BannedKey);
                return;
            }
            if (SandboxOnlineBan.Violates(mode))
            {
                sink.OnlineFailed(SandboxOnlineBan.ErrorKey);
                return;
            }
            if (ModTankOnlineBan.Violates(BattleNetHelloFactory.SelectedTankId()))
            {
                sink.OnlineFailed(ModTankOnlineBan.ErrorKey);
                return;
            }
            NetSession.Matchmake(sink, mode);
        }

        public void Cancel()
        {
            var session = NetSession.Current;
            if (session != null && BlitzBootstrap.Current != null && BlitzBootstrap.Current.Battle == null)
            {
                session.Sink = null;
                session.Shutdown();
            }
        }
    }
}
