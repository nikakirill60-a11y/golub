using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Battle;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace BlitzLegacy.Direct
{
    /// <summary>
    /// The dedicated battle server: a headless player started with
    /// <c>-blitzServer [-blitzServerPort N] [-blitzServerLobby seconds]
    /// [-blitzServerMinPlayers N]</c>. It owns the only simulation: clients
    /// send intent, the server drives every tank, decides every hit and the
    /// result, and streams the world back. One battle runs per process;
    /// players who knock meanwhile wait in the queue for the next one.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public sealed class DirectServer : MonoBehaviour
    {
        public const string ServerSwitch = "-blitzServer";
        public const string PortSwitch = "-blitzServerPort";
        public const string LobbySwitch = "-blitzServerLobby";
        public const string MinPlayersSwitch = "-blitzServerMinPlayers";

        /// <summary>Local account the server process logs in with (never plays).</summary>
        public const string AccountName = "BlitzServer";

        public const float LoadWaitSeconds = 40f;
        public const float RejoinSeconds = 60f;
        public const float InputTimeoutSeconds = 0.5f;
        public const float KickDelaySeconds = 1.5f;
        public const float LobbyPublishSeconds = 0.5f;
        public const int MaxClients = 64;

        public const string TeamKillKickKey = "online:net/kicked_teamkill";

        /// <summary>Refusal of a Big Boss hello with a tank outside tiers V-X.</summary>

        public const string BossTierErrorKey = "online:error/bossmode_tier";
        public const string CheatKickKey = "online:error/banned";

        public static DirectServer Current { get; private set; }

        public static bool IsServerProcess
        {
            get { return Array.IndexOf(Environment.GetCommandLineArgs(), ServerSwitch) >= 0; }
        }

        private sealed class Client
        {
            public NetPeer Peer;
            public int Owner;
            public string Seat;
            public BattleNetHello Hello;
            public BattleMode Mode;
            public int Tier;
            public int Column;
            public float JoinedAt;
            public bool InBattle;
            public bool Done;
            public bool Replaced;
            public bool Loaded;
            public float LastInputAt = -1f;
            public uint LastSequence;
            /// <summary>The last drive axes as sent; the coast scales these, never the driver's own.</summary>
            public float LastForward;
            public float LastRight;
            public bool LastFire;
            public bool FirePrimed;
            public byte FireSequence;
            public int Strikes;
            public float KickAt = -1f;
            public bool HadRockets;
            public readonly ChatSpamGuard Chat = new ChatSpamGuard();
        }

        private int _port = DirectProtocol.DefaultPort;
        private float _lobbySeconds = OnlineBalancer.LenientStartSeconds;
        private int _minPlayers = 2;

        private NetManager _net;
        private readonly EventBasedNetListener _listener = new EventBasedNetListener();
        private readonly NetDataWriter _w = new NetDataWriter();
        private readonly Dictionary<int, Client> _byPeer = new Dictionary<int, Client>();
        private readonly List<Client> _clients = new List<Client>();
        private int _nextOwner = 1;

        // ----- the battle of this process
        private BattleSession _battle;
        private bool _battleRequested;
        private float _battleRequestedAt;
        private bool _ended;
        private bool _released;
        private float _introAt = -1f;
        private bool _startSent;
        private readonly HashSet<string> _bannedSeats = new HashSet<string>();
        private readonly Dictionary<int, float> _detached = new Dictionary<int, float>();
        private float _snapshotClock;
        private uint _tick;
        private DirectTankState[] _states = new DirectTankState[BattleNetPlan.MaxSlots];
        private UprisingSnapshot[] _uprising = new UprisingSnapshot[BattleNetPlan.MaxSlots];
        private BigBossSnapshot[] _bigBoss = new BigBossSnapshot[BattleNetPlan.MaxSlots];
        private readonly List<DirectRocket> _rockets = new List<DirectRocket>();

        // ----- lobby
        private float _groupSince = -1f;
        private float _nextLobbyAt;
        private bool _loggedIn;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!IsServerProcess) { return; }
            var go = new GameObject("DirectServer");
            DontDestroyOnLoad(go);
            go.AddComponent<DirectServer>().Configure(Environment.GetCommandLineArgs());
        }

        private void Configure(string[] args)
        {
            for (var i = 0; i + 1 < args.Length; i++)
            {
                int n;
                float f;
                switch (args[i])
                {
                    case PortSwitch:
                        if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0 && n < 65536) { _port = n; }
                        break;
                    case LobbySwitch:
                        if (float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out f) && f > 0f) { _lobbySeconds = f; }
                        break;
                    case MinPlayersSwitch:
                        if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) { _minPlayers = Mathf.Clamp(n, 1, 2); }
                        break;
                }
            }
        }

        private void Start()
        {
            Current = this;
            // Headless: no vsync paces the loop, so cap it or one battle eats
            // a whole core (and the burst credits of a small cloud instance).
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            Application.runInBackground = true;
            AudioListener.volume = 0f;

            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += peer => Debug.Log("[BlitzDirect] peer connected: " + peer.Address);
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnReceive;
            _listener.NetworkErrorEvent += (endPoint, error) => Debug.LogWarning("[BlitzDirect] socket error " + error);
            _net = new NetManager(_listener)
            {
                AutoRecycle = true,
                DisconnectTimeout = 10000,
                UpdateTime = 10,
                ChannelsCount = 1,
            };
            if (!_net.Start(_port))
            {
                Debug.LogError("[BlitzDirect] cannot listen on UDP " + _port);
                Application.Quit(1);
                return;
            }
            BattleNetHooks.PuppetWorld = false;
            BattleNetHooks.HoldIntro = HoldIntroHook;
            BattleNetHooks.ShellLaunched += OnShellLaunched;
            BattleNetHooks.DamageTaken += OnDamageTaken;
            BattleNetHooks.UprisingEffectRaised += OnUprisingEffect;
            BattleNetHooks.UprisingChainRaised += OnUprisingChain;
            BattleNetHooks.BigBossEffectRaised += OnBigBossEffect;
            BattleNetHooks.BigBossTrapRaised += OnBigBossTrap;
            BattleChatView.NetSend = null;
            Debug.Log("[BlitzDirect] server " + DirectConfig.Build + " protocol " + DirectProtocol.Version
                + " listening on UDP " + _port + ", lobby " + _lobbySeconds + "s, min players " + _minPlayers);
        }

        private void OnDestroy()
        {
            BattleNetHooks.ShellLaunched -= OnShellLaunched;
            BattleNetHooks.DamageTaken -= OnDamageTaken;
            BattleNetHooks.UprisingEffectRaised -= OnUprisingEffect;
            BattleNetHooks.UprisingChainRaised -= OnUprisingChain;
            BattleNetHooks.BigBossEffectRaised -= OnBigBossEffect;
            BattleNetHooks.BigBossTrapRaised -= OnBigBossTrap;
            if (BattleNetHooks.HoldIntro == (Func<bool>)HoldIntroHook) { BattleNetHooks.HoldIntro = null; }
            UnwireBattle();
            if (_net != null) { _net.Stop(true); }
            if (Current == this) { Current = null; }
        }

        private void OnApplicationQuit()
        {
            if (_net != null) { _net.Stop(true); }
        }

        // ------------------------------------------------------------ connections

        private void OnConnectionRequest(ConnectionRequest request)
        {
            if (_clients.Count >= MaxClients) { Reject(request, "online:error/servers_full"); return; }
            DirectProtocol.ConnectRequest parsed;
            try { parsed = DirectProtocol.ReadConnect(request.Data); }
            catch (Exception e) when (OnlineBan.IsBan(e))
            {
                // Hard online bans (the Sandbox, mod tanks): refused with the reason.
                Debug.LogWarning("[BlitzDirect] refused " + request.RemoteEndPoint + ": " + e.Message);
                Reject(request, OnlineBan.KeyOf(e));
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BlitzDirect] malformed connection request from " + request.RemoteEndPoint + ": " + e.Message);
                request.Reject();
                return;
            }
            if (parsed.Version != DirectProtocol.Version || parsed.Build != DirectConfig.Build
                || parsed.Hello == null || parsed.Hello.Build != DirectConfig.Build)
            {
                Debug.LogWarning("[BlitzDirect] refused " + request.RemoteEndPoint + ": protocol " + parsed.Version
                    + " build " + parsed.Build);
                Reject(request, "online:error/version");
                return;
            }
            var seat = DirectProtocol.SeatOf(parsed.Token);
            if (seat == null || _bannedSeats.Contains(seat)) { Reject(request, TeamKillKickKey); return; }

            var hello = parsed.Hello;
            if (!DirectWorld.IsKnownTank(hello.TankId))
            {
                // Hard mod tank ban: a tank this build does not ship (a mod, a
                // patched client) never gets a seat online.
                Debug.LogWarning("[BlitzDirect] refused " + request.RemoteEndPoint + ": not a stock tank");
                Reject(request, ModTankOnlineBan.ErrorKey);
                return;
            }
            if (parsed.Mode == BattleMode.BigBoss)
            {
                int bossTier, bossColumn;
                BattleOnline.Classify(hello.TankId, out bossTier, out bossColumn);
                if (!BigBossRules.EligibleTier(bossTier))
                {
                    // Big Boss seats tier V-X vehicles only, like the offline mode.
                    Debug.LogWarning("[BlitzDirect] refused " + request.RemoteEndPoint + ": tier " + bossTier + " tank for Big Boss");
                    Reject(request, BossTierErrorKey);
                    return;
                }
            }
            var peer = request.Accept();
            var client = new Client
            {
                Peer = peer,
                Owner = _nextOwner++,
                Seat = seat,
                Hello = hello,
                Mode = parsed.Mode,
                JoinedAt = Time.realtimeSinceStartup,
            };
            BattleOnline.Classify(hello.TankId, out client.Tier, out client.Column);
            // The token wins over an older connection of the same player.
            for (var i = 0; i < _clients.Count; i++)
            {
                var other = _clients[i];
                if (other.Seat != seat || other.Replaced) { continue; }
                other.Replaced = true;
                other.Peer.Disconnect();
            }
            _byPeer[peer.Id] = client;
            _clients.Add(client);
            Debug.Log("[BlitzDirect] hello from " + client.Owner + ": tank=" + hello.TankId + " name=" + hello.Name
                + " mode=" + client.Mode + " clients=" + _clients.Count);
            if (_battle != null && !_ended) { RestoreSeat(client); }
        }

        private void Reject(ConnectionRequest request, string key)
        {
            _w.Reset();
            _w.Put(key);
            request.Reject(_w);
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            Client client;
            if (!_byPeer.TryGetValue(peer.Id, out client) || client.Peer != peer) { return; }
            _byPeer.Remove(peer.Id);
            _clients.Remove(client);
            Debug.Log("[BlitzDirect] player " + client.Owner + " left: " + info.Reason);
            var plan = _battle != null ? _battle.NetPlan : null;
            if (plan == null || _ended || !client.InBattle) { return; }
            var slot = plan.SlotOf(client.Owner);
            if (slot < 0) { return; }
            _battle.DetachSeat(slot);
            if (_bannedSeats.Contains(client.Seat ?? string.Empty) || _battle.IsOutForGood(slot))
            {
                _battle.ReleaseSeatToBot(slot);
                return;
            }
            _detached[slot] = Time.realtimeSinceStartup + RejoinSeconds;
            Debug.Log("[BlitzDirect] seat " + slot + " waits " + RejoinSeconds + "s for its player");
        }

        /// <summary>A reconnecting player proved its seat: give it back.</summary>
        private void RestoreSeat(Client client)
        {
            var plan = _battle.NetPlan;
            var slot = plan != null ? plan.SlotOfSeat(client.Seat) : -1;
            if (slot < 0) { return; } // not part of this battle: waits for the next one
            if (!_battle.ReassignSeat(slot, client.Owner))
            {
                Debug.LogWarning("[BlitzDirect] seat " + slot + " cannot be restored to " + client.Owner);
                return;
            }
            _detached.Remove(slot);
            client.InBattle = true;
            client.Loaded = true;
            Debug.Log("[BlitzDirect] seat " + slot + " restored to player " + client.Owner);
            SendPlan(client);
            if (_released) { SendSignal(client, DirectMessage.IntroReleased); }
            if (_startSent) { SendSignal(client, DirectMessage.BattleStart); }
        }

        // ------------------------------------------------------------ receive

        private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            Client client;
            if (!_byPeer.TryGetValue(peer.Id, out client) || client.Peer != peer) { return; }
            if (reader.AvailableBytes < 1 || reader.AvailableBytes > DirectProtocol.MaxClientPacket)
            {
                Drop(client, "packet size " + reader.AvailableBytes);
                return;
            }
            try
            {
                var type = (DirectMessage)reader.GetByte();
                switch (type)
                {
                    case DirectMessage.Loaded:
                        client.Loaded = true;
                        break;
                    case DirectMessage.Input:
                        if (reader.AvailableBytes != DirectInput.Size) { throw new FormatException("input size"); }
                        OnInput(client, DirectInput.Read(reader));
                        break;
                    case DirectMessage.Chat:
                        DirectProtocol.Need(reader, 1);
                        var chatChannel = reader.GetByte();
                        var text = DirectProtocol.GetSafeString(reader, DirectProtocol.MaxChatLength);
                        OnChat(client, chatChannel, text);
                        break;
                    default:
                        throw new FormatException("message " + (byte)type);
                }
            }
            catch (Exception e)
            {
                Drop(client, e.Message);
            }
        }

        /// <summary>Garbage on the wire: no honest client sends it.</summary>
        private void Drop(Client client, string why)
        {
            Debug.LogWarning("[BlitzDirect] dropping player " + client.Owner + ": " + why);
            client.Peer.Disconnect();
        }

        private void OnInput(Client client, DirectInput input)
        {
            if (client.LastSequence != 0 && unchecked((int)(input.Sequence - client.LastSequence)) <= 0) { return; }
            client.LastSequence = input.Sequence;
            client.LastInputAt = Time.realtimeSinceStartup;
            if (_battle == null || _ended || !client.InBattle) { return; }
            BattleRemoteDriver driver;
            if (!_battle.RemoteDrivers.TryGetValue(client.Owner, out driver) || driver == null) { return; }
            bool hard;
            if (!DirectInputGuard.Sanitize(ref input, out hard) && hard && ++client.Strikes >= DirectInputGuard.KickStrikes)
            {
                Debug.LogWarning("[BlitzDirect] player " + client.Owner + " sends impossible input");
                Kick(client, CheatKickKey);
                return;
            }
            if (!_battle.Started)
            {
                // Countdown: nobody drives or shoots yet.
                input.Forward = 0f;
                input.Right = 0f;
                input.Buttons &= ~DirectButtons.Fire;
            }
            client.LastForward = input.Forward;
            client.LastRight = input.Right;
            client.LastFire = input.Has(DirectButtons.Fire);
            driver.Forward = input.Forward;
            driver.Right = input.Right;
            driver.HasAim = input.Has(DirectButtons.HasAim);
            driver.Aim = input.Aim;
            driver.Fire = client.LastFire;
            // A tap whose own packet was lost still arrives as a changed counter.
            if (client.FirePrimed && input.FireSequence != client.FireSequence && !client.LastFire) { driver.FirePulse = true; }
            client.FirePrimed = true;
            client.FireSequence = input.FireSequence;
            driver.TurretBlock = input.Has(DirectButtons.TurretBlock);
            driver.Arty = input.Has(DirectButtons.Arty);
            driver.ShellId = input.ShellId;
            driver.ApplyUprisingAction(input.UprisingSequence, input.UprisingAction);
        }

        private void OnChat(Client client, byte channel, string raw)
        {
            if (channel > (byte)ChatChannel.All || _battle == null || !client.InBattle) { return; }
            var plan = _battle.NetPlan;
            var slot = plan != null ? plan.SlotOf(client.Owner) : -1;
            if (slot < 0) { return; }
            // Shadow mute: the spammer keeps seeing their own lines, nobody else does.
            if (!client.Chat.Accept(Time.unscaledTimeAsDouble)) { return; }
            var text = ChatSanitizer.CleanText(raw);
            if (text.Length == 0) { return; }
            var senderTeam = plan.Slots[slot].Team;
            for (var i = 0; i < _clients.Count; i++)
            {
                var reader = _clients[i];
                if (reader == client || !reader.InBattle || reader.Replaced) { continue; }
                var readerSlot = plan.SlotOf(reader.Owner);
                if (readerSlot < 0 || !ChatColors.Receives((ChatChannel)channel, senderTeam, plan.Slots[readerSlot].Team)) { continue; }
                _w.Reset();
                _w.Put((byte)DirectMessage.ChatDeliver);
                _w.Put((byte)slot);
                _w.Put(channel);
                _w.Put(text);
                reader.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
            }
        }

        // ------------------------------------------------------------ per frame

        private void Update()
        {
            if (_net == null) { return; }
            _net.PollEvents();
            var now = Time.realtimeSinceStartup;
            for (var i = _clients.Count - 1; i >= 0; i--)
            {
                var c = _clients[i];
                if (c.KickAt > 0f && now >= c.KickAt) { c.KickAt = -1f; c.Peer.Disconnect(); }
            }

            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap == null) { return; }
            if (!_loggedIn || (bootstrap.Hangar == null && bootstrap.Battle == null && !_battleRequested))
            {
                if (bootstrap.Hangar == null && bootstrap.Battle == null)
                {
                    if (DirectWorld.AutoLogin(bootstrap, AccountName, null)) { _loggedIn = true; }
                    return;
                }
                _loggedIn = true;
            }

            TrackBattle(bootstrap);
            if (_battle != null)
            {
                TickIntro(bootstrap, now);
                TickBattle(now);
            }
            else if (!_battleRequested)
            {
                TickLobby(bootstrap, now);
            }
            else if (now - _battleRequestedAt > 120f)
            {
                Debug.LogError("[BlitzDirect] the battle never got built; back to the lobby");
                ResetBattle();
            }
            if (now >= _nextLobbyAt) { _nextLobbyAt = now + LobbyPublishSeconds; PublishLobby(now); }
        }

        private void TrackBattle(BlitzBootstrap bootstrap)
        {
            var current = bootstrap.Battle;
            // Reference checks: a finished battle is a destroyed Unity object,
            // which compares equal to null and would hide the teardown.
            if (ReferenceEquals(_battle, null))
            {
                if (!_battleRequested || current == null || current.NetPlan == null || !current.Dedicated) { return; }
                _battle = current;
                _battle.Finished += OnBattleFinished;
                _battle.TeamDamage.KickRequested += OnTeamKillKick;
                _battle.TeamDamage.TurnedBlue += OnTurnedBlue;
                Debug.Log("[BlitzDirect] battle built: map=" + _battle.NetPlan.MapId + " mode=" + _battle.Mode
                    + " slots=" + _battle.NetPlan.Slots.Count);
                for (var i = 0; i < _clients.Count; i++)
                {
                    if (_clients[i].InBattle) { SendPlan(_clients[i]); }
                }
                return;
            }
            if (!ReferenceEquals(current, _battle) || _battle == null)
            {
                Debug.Log("[BlitzDirect] battle torn down; lobby open");
                if (bootstrap.Result != null) { Destroy(bootstrap.Result.gameObject); }
                ResetBattle();
                // A long-running server must not grow: drop what the map pulled in.
                Resources.UnloadUnusedAssets();
                GC.Collect();
            }
        }

        private void ResetBattle()
        {
            UnwireBattle();
            _battle = null;
            _battleRequested = false;
            _ended = false;
            _released = false;
            _introAt = -1f;
            _startSent = false;
            _bannedSeats.Clear();
            _detached.Clear();
            _groupSince = -1f;
            for (var i = _clients.Count - 1; i >= 0; i--)
            {
                var c = _clients[i];
                if (!c.InBattle) { continue; }
                c.InBattle = false;
                c.Done = true;
                c.Peer.Disconnect();
            }
        }

        private void UnwireBattle()
        {
            if (ReferenceEquals(_battle, null)) { return; }
            _battle.Finished -= OnBattleFinished;
            _battle.TeamDamage.KickRequested -= OnTeamKillKick;
            _battle.TeamDamage.TurnedBlue -= OnTurnedBlue;
        }

        // ------------------------------------------------------------ lobby

        private List<Client> Waiting()
        {
            var list = new List<Client>();
            for (var i = 0; i < _clients.Count; i++)
            {
                var c = _clients[i];
                if (!c.InBattle && !c.Done && !c.Replaced && c.KickAt < 0f) { list.Add(c); }
            }
            list.Sort((a, b) => a.JoinedAt.CompareTo(b.JoinedAt));
            return list;
        }

        /// <summary>The next battle's humans: the longest waiter and everyone its tier takes.</summary>
        private List<Client> Group(List<Client> waiting, float now)
        {
            var group = new List<Client>();
            if (waiting.Count == 0) { return group; }
            var oldest = waiting[0];
            var spread = OnlineBalancer.TierSpreadFor(now - oldest.JoinedAt);
            for (var i = 0; i < waiting.Count && group.Count < BattleOnline.Capacity; i++)
            {
                var c = waiting[i];
                if (c.Mode == oldest.Mode && OnlineBalancer.Accepts(oldest.Tier, c.Tier, spread)) { group.Add(c); }
            }
            return group;
        }

        private OnlineBalancer.LobbyDecision Decide(List<Client> group, float now)
        {
            var humans = group.Count;
            var lonely = _minPlayers <= 1 && humans == 1;
            if (humans >= 2 || lonely) { if (_groupSince < 0f) { _groupSince = lonely ? group[0].JoinedAt : now; } }
            else { _groupSince = -1f; }
            var countdown = OnlineBalancer.CountdownElapsed(now, _groupSince);
            var normalAt = _lobbySeconds * (OnlineBalancer.NormalStartSeconds / OnlineBalancer.LenientStartSeconds);
            if (humans == 0) { return new OnlineBalancer.LobbyDecision { StartsIn = -1f }; }
            if (lonely)
            {
                return new OnlineBalancer.LobbyDecision
                {
                    StartsIn = Mathf.Max(0f, normalAt - countdown),
                    Start = countdown >= normalAt,
                };
            }
            var weights = new List<int>(humans - 1);
            for (var i = 1; i < humans; i++) { weights.Add(OnlineBalancer.Weight(group[i].Tier, group[i].Column)); }
            var anchor = OnlineBalancer.Weight(group[0].Tier, group[0].Column);
            var normalOk = OnlineBalancer.NormalBalanceOk(anchor, weights, BattleOnline.TeamSize);
            return OnlineBalancer.Decide(countdown, humans, BattleOnline.Capacity, normalOk, normalAt, _lobbySeconds);
        }

        private void TickLobby(BlitzBootstrap bootstrap, float now)
        {
            var waiting = Waiting();
            var group = Group(waiting, now);
            var decision = Decide(group, now);
            if (!decision.Start || bootstrap.Battle != null || bootstrap.Hangar == null) { return; }
            BeginBattle(bootstrap, group, decision.Lenient);
        }

        private void BeginBattle(BlitzBootstrap bootstrap, List<Client> group, bool lenient)
        {
            if (bootstrap.Result != null) { Destroy(bootstrap.Result.gameObject); }
            var net = new BattleNetSetup
            {
                IsClient = false,
                Dedicated = true,
                Mode = group[0].Mode,
                Build = DirectConfig.Build,
            };
            for (var i = 0; i < group.Count; i++)
            {
                net.Guests.Add(new BattleNetSetup.Guest { Owner = group[i].Owner, Seat = group[i].Seat, Hello = group[i].Hello });
            }
            if (!net.PromoteAnchor() || !bootstrap.EnterNetBattle(net)) { return; }
            _battleRequested = true;
            _battleRequestedAt = Time.realtimeSinceStartup;
            _ended = false;
            _released = false;
            _introAt = -1f;
            _startSent = false;
            for (var i = 0; i < group.Count; i++)
            {
                group[i].InBattle = true;
                group[i].Loaded = false;
                group[i].LastInputAt = -1f;
                group[i].Strikes = 0;
            }
            Debug.Log("[BlitzDirect] starting " + net.Mode + " battle with " + group.Count + " human(s)"
                + (lenient ? " (lenient balance)" : string.Empty));
        }

        private void PublishLobby(float now)
        {
            var waiting = Waiting();
            if (waiting.Count == 0) { return; }
            var busy = _battleRequested || _battle != null;
            var group = busy ? new List<Client>() : Group(waiting, now);
            var decision = busy ? new OnlineBalancer.LobbyDecision { StartsIn = -1f } : Decide(group, now);
            var groupTiers = new int[OnlineQueue.Tiers];
            var groupClasses = new int[OnlineQueue.Classes];
            for (var i = 0; i < group.Count; i++) { Count(group[i], groupTiers, groupClasses); }
            for (var i = 0; i < waiting.Count; i++)
            {
                var c = waiting[i];
                var inGroup = group.Contains(c);
                var queueTiers = new int[OnlineQueue.Tiers];
                var queueClasses = new int[OnlineQueue.Classes];
                var sameMode = 0;
                for (var j = 0; j < waiting.Count; j++)
                {
                    if (waiting[j].Mode != c.Mode) { continue; }
                    sameMode++;
                    Count(waiting[j], queueTiers, queueClasses);
                }
                var lobby = new DirectLobby
                {
                    Players = inGroup ? group.Count : 1,
                    Capacity = BattleOnline.Capacity,
                    StartsIn = inGroup ? decision.StartsIn : -1f,
                    Waiting = sameMode,
                    Busy = busy,
                    Tiers = inGroup ? groupTiers : Single(c, true),
                    Classes = inGroup ? groupClasses : Single(c, false),
                    QueueTiers = queueTiers,
                    QueueClasses = queueClasses,
                };
                _w.Reset();
                lobby.Write(_w);
                c.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
            }
        }

        private static void Count(Client c, int[] tiers, int[] classes)
        {
            tiers[10 - Mathf.Clamp(c.Tier, 1, 10)]++;
            classes[Mathf.Clamp(c.Column, 0, 3)]++;
        }

        private static int[] Single(Client c, bool tiers)
        {
            var values = new int[tiers ? OnlineQueue.Tiers : OnlineQueue.Classes];
            if (tiers) { values[10 - Mathf.Clamp(c.Tier, 1, 10)] = 1; }
            else { values[Mathf.Clamp(c.Column, 0, 3)] = 1; }
            return values;
        }

        // ------------------------------------------------------------ battle

        private bool HoldIntroHook()
        {
            return _battle != null && !_ended && !_released;
        }

        private bool AllLoaded()
        {
            for (var i = 0; i < _clients.Count; i++)
            {
                var c = _clients[i];
                if (c.InBattle && !c.Replaced && !c.Loaded) { return false; }
            }
            return true;
        }

        private void TickIntro(BlitzBootstrap bootstrap, float now)
        {
            var intro = bootstrap.MapIntro;
            if (!_released && intro != null)
            {
                if (_introAt < 0f) { _introAt = now; }
                var all = AllLoaded();
                if (intro.TimeUp && (all || now - _introAt >= LoadWaitSeconds))
                {
                    _released = true;
                    Debug.Log("[BlitzDirect] releasing every intro: " + (all ? "all players loaded" : "load wait timed out")
                        + " after " + (now - _introAt).ToString("F1", CultureInfo.InvariantCulture) + "s");
                    Broadcast(DirectMessage.IntroReleased);
                }
            }
            if (!_startSent && _battle.Started)
            {
                _startSent = true;
                Debug.Log("[BlitzDirect] battle is live");
                Broadcast(DirectMessage.BattleStart);
            }
        }

        private void TickBattle(float now)
        {
            if (_ended) { return; }
            // Lost seats nobody came back for go to the AI.
            if (_detached.Count > 0)
            {
                List<int> expired = null;
                foreach (var pair in _detached)
                {
                    if (now >= pair.Value) { (expired ?? (expired = new List<int>())).Add(pair.Key); }
                }
                if (expired != null)
                {
                    for (var i = 0; i < expired.Count; i++)
                    {
                        _detached.Remove(expired[i]);
                        _battle.ReleaseSeatToBot(expired[i]);
                    }
                }
            }
            var humans = 0;
            for (var i = 0; i < _clients.Count; i++)
            {
                var c = _clients[i];
                if (!c.InBattle || c.Replaced) { continue; }
                humans++;
                if (c.LastInputAt > 0f) { Coast(c, now - c.LastInputAt); }
            }
            if (humans == 0 && _detached.Count == 0)
            {
                Debug.Log("[BlitzDirect] every player is gone; ending the battle");
                _ended = true;
                _battle.ForceFinish(-1);
            }
        }

        /// <summary>
        /// Server-side prediction of a player whose input is late (see
        /// <see cref="DirectInputCoast"/>): the tank keeps its course and aim,
        /// so it stays a target that can be led and penetrated, then rolls to
        /// a stop if the link stays silent.
        /// </summary>
        private void Coast(Client c, float age)
        {
            BattleRemoteDriver driver;
            if (!_battle.RemoteDrivers.TryGetValue(c.Owner, out driver) || driver == null) { return; }
            if (DirectInputCoast.Expired(age)) { driver.Clear(); return; }
            var scale = DirectInputCoast.DriveScale(age);
            driver.Forward = c.LastForward * scale;
            driver.Right = c.LastRight * scale;
            if (!DirectInputCoast.KeepsFire(age)) { driver.Fire = false; }
        }

        private void FixedUpdate()
        {
            if (_net == null || _battle == null || _battle.NetPlan == null) { return; }
            _snapshotClock += Time.fixedDeltaTime;
            const float interval = 1f / DirectProtocol.SnapshotRate;
            if (_snapshotClock < interval - 0.0001f) { return; }
            _snapshotClock = Mathf.Min(_snapshotClock - interval, interval);
            _tick++;
            var time = Time.fixedTimeAsDouble;

            var tanks = _battle.SlotTanks;
            var count = Mathf.Min(tanks.Count, BattleNetPlan.MaxSlots);
            var mode = DirectSnapshot.ModeOf(_battle.Mode);
            for (var i = 0; i < count; i++)
            {
                var tank = tanks[i];
                if (tank == null) { _states[i] = default(DirectTankState); continue; }
                _states[i] = DirectWorld.Capture(tank, _battle);
                if (mode == DirectSnapshot.ModeUprising)
                {
                    var component = tank.GetComponent<UprisingTank>();
                    if (component != null && component.State != null)
                    {
                        _uprising[i] = component.CaptureNet();
                        _states[i].Flags |= DirectWorld.UprisingValidFlag;
                    }
                }
                else if (mode == DirectSnapshot.ModeBigBoss)
                {
                    var boss = tank.BigBoss;
                    var valid = boss != null && boss.State != null;
                    _bigBoss[i] = valid ? boss.CaptureNet() : default(BigBossSnapshot);
                    if (valid) { _states[i].Flags |= DirectWorld.BigBossValidFlag; }
                }
            }
            var match = mode == DirectSnapshot.ModeBigBoss && _battle.BigBoss != null ? _battle.BigBoss.Match : null;
            var teamA = match != null ? match.TeamA : 0;
            var teamB = match != null ? match.TeamB : 0;
            _rockets.Clear();
            var live = BattleProjectile.LiveGuided;
            for (var i = 0; i < live.Count && _rockets.Count < 64; i++)
            {
                var rocket = live[i];
                if (rocket == null || !rocket.Alive || rocket.FollowsHost) { continue; }
                _rockets.Add(new DirectRocket { Id = rocket.ShotId, Position = rocket.transform.position, Velocity = rocket.Velocity });
            }

            for (var c = 0; c < _clients.Count; c++)
            {
                var client = _clients[c];
                if (!client.InBattle || client.Replaced) { continue; }
                var peer = client.Peer;
                var max = peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable);
                var per = DirectSnapshot.SlotsPerPacket(max, mode);
                for (var first = 0; first < count; first += per)
                {
                    var n = Mathf.Min(per, count - first);
                    _w.Reset();
                    DirectSnapshot.WriteHeader(_w, _tick, time, count, mode, first, n, teamA, teamB);
                    for (var i = first; i < first + n; i++)
                    {
                        _states[i].Write(_w);
                        if (mode == DirectSnapshot.ModeUprising) { DirectUprising.Write(_w, _uprising[i]); }
                        else if (mode == DirectSnapshot.ModeBigBoss) { DirectBigBoss.Write(_w, _bigBoss[i]); }
                    }
                    peer.Send(_w, DeliveryMethod.Unreliable);
                }
                if (_rockets.Count == 0 && !client.HadRockets) { continue; }
                client.HadRockets = _rockets.Count > 0;
                var rocketsPer = Mathf.Max(1, (max - DirectSnapshot.RocketHeaderSize) / DirectRocket.Size);
                var sent = 0;
                do
                {
                    var n = Mathf.Min(rocketsPer, _rockets.Count - sent);
                    _w.Reset();
                    DirectSnapshot.WriteRockets(_w, _tick, time, _rockets, sent, n);
                    peer.Send(_w, DeliveryMethod.Unreliable);
                    sent += n;
                } while (sent < _rockets.Count);
            }
        }

        private void SendPlan(Client client)
        {
            var plan = _battle != null ? _battle.NetPlan : null;
            if (plan == null) { return; }
            var bytes = plan.ToBytes();
            _w.Reset();
            _w.Put((byte)DirectMessage.Plan);
            _w.Put(client.Owner);
            _w.Put(bytes);
            client.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
            Debug.Log("[BlitzDirect] plan sent to " + client.Owner + ": map=" + plan.MapId
                + " slots=" + plan.Slots.Count + " bytes=" + bytes.Length);
        }

        private void SendSignal(Client client, DirectMessage message)
        {
            _w.Reset();
            _w.Put((byte)message);
            client.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
        }

        private void Broadcast(DirectMessage message)
        {
            _w.Reset();
            _w.Put((byte)message);
            SendToBattle(DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Sends the writer's packet to every player of the running battle.</summary>
        private void SendToBattle(DeliveryMethod method)
        {
            for (var i = 0; i < _clients.Count; i++)
            {
                var c = _clients[i];
                if (c.InBattle && !c.Replaced) { c.Peer.Send(_w, method); }
            }
        }

        private void Kick(Client client, string key)
        {
            if (client.KickAt > 0f) { return; }
            if (!string.IsNullOrEmpty(client.Seat)) { _bannedSeats.Add(client.Seat); }
            _w.Reset();
            _w.Put((byte)DirectMessage.Kick);
            _w.Put(key);
            client.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
            // Lets the notice arrive before the connection drops.
            client.KickAt = Time.realtimeSinceStartup + KickDelaySeconds;
        }

        private void OnShellLaunched(BattleTank tank, int shellId, Vector3 origin, Vector3 muzzle, Vector3 direction, int shotId)
        {
            if (_battle == null) { return; }
            var slot = _battle.SlotOfTank(tank);
            if (slot < 0 || slot >= BattleNetPlan.MaxSlots) { return; }
            _w.Reset();
            _w.Put((byte)DirectMessage.Shot);
            _w.Put((byte)slot);
            _w.Put((byte)Mathf.Clamp(shellId, 0, 255));
            DirectProtocol.Put(_w, origin);
            DirectProtocol.Put(_w, muzzle);
            DirectProtocol.Put(_w, direction);
            _w.Put(shotId);
            // A Big Boss railgun round flies at its own speed on the clients too.
            _w.Put(tank.BigBoss != null && tank.BigBoss.IsRailgunShot(shotId));
            SendToBattle(DeliveryMethod.ReliableUnordered);
        }

        /// <summary>Every hit with presentation (bounce / penetration) goes to the battle.</summary>
        private void OnDamageTaken(BattleTank tank, DamageInfo info)
        {
            if (_battle == null || info == null || !info.ShouldHaveMarker()) { return; }
            var slot = _battle.SlotOfTank(tank);
            if (slot < 0 || slot >= BattleNetPlan.MaxSlots) { return; }
            var attacker = info.AttackerTank as BattleTank;
            var hit = DirectHit.From(slot, attacker != null ? _battle.SlotOfTank(attacker) : -1, info);
            _w.Reset();
            hit.Write(_w);
            SendToBattle(DeliveryMethod.ReliableUnordered);
        }

        private int SlotOf(UprisingTank uprising)
        {
            return uprising != null && _battle != null ? _battle.SlotOfTank(uprising.Tank) : -1;
        }

        private void OnUprisingEffect(UprisingTank uprising, string id, Vector3 point)
        {
            if (_battle == null || string.IsNullOrEmpty(id) || id.Length > 32) { return; }
            var slot = SlotOf(uprising);
            if (slot < 0) { return; }
            _w.Reset();
            _w.Put((byte)DirectMessage.UprisingEffect);
            _w.Put((byte)slot);
            _w.Put(id);
            DirectProtocol.Put(_w, point);
            SendToBattle(DeliveryMethod.ReliableOrdered);
        }

        private void OnUprisingChain(UprisingTank uprising, Vector3 from, Vector3 to)
        {
            if (_battle == null) { return; }
            var slot = SlotOf(uprising);
            if (slot < 0) { return; }
            _w.Reset();
            _w.Put((byte)DirectMessage.UprisingChain);
            _w.Put((byte)slot);
            DirectProtocol.Put(_w, from);
            DirectProtocol.Put(_w, to);
            SendToBattle(DeliveryMethod.Unreliable);
        }

        private void OnBigBossEffect(BigBossTank seat, string id, Vector3 point)
        {
            if (_battle == null || seat == null || !BigBossTank.IsNetEffect(id)) { return; }
            SendBigBoss(DirectMessage.BigBossEffect, _battle.SlotOfTank(seat.Tank), id, point);
        }

        private void OnBigBossTrap(BigBossTank seat, string id, Vector3 point)
        {
            if (_battle == null || seat == null || _battle.BigBoss == null || !_battle.BigBoss.IsTrap(id)) { return; }
            SendBigBoss(DirectMessage.BigBossTrap, _battle.SlotOfTank(seat.Tank), id, point);
        }

        private void SendBigBoss(DirectMessage message, int slot, string id, Vector3 point)
        {
            if (slot < 0 || slot >= BattleNetPlan.MaxSlots) { return; }
            _w.Reset();
            _w.Put((byte)message);
            _w.Put((byte)slot);
            _w.Put(id);
            DirectProtocol.Put(_w, point);
            SendToBattle(DeliveryMethod.ReliableOrdered);
        }

        private void OnBattleFinished(BattleSession battle)
        {
            if (_ended) { return; }
            _ended = true;
            var winner = battle.Roster != null ? battle.Roster.WinnerTeam : -1;
            Debug.Log("[BlitzDirect] battle over, winner team " + winner);
            _w.Reset();
            _w.Put((byte)DirectMessage.BattleEnd);
            _w.Put(winner);
            SendToBattle(DeliveryMethod.ReliableOrdered);
        }

        private void RaiseNotice(ChatNotice kind, int slot)
        {
            if (_battle == null || slot < 0 || slot >= BattleNetPlan.MaxSlots) { return; }
            var blue = slot < _battle.SlotTanks.Count && _battle.SlotTanks[slot] != null && _battle.SlotTanks[slot].TeamKiller;
            _w.Reset();
            _w.Put((byte)DirectMessage.ChatNotice);
            _w.Put((byte)kind);
            _w.Put((byte)slot);
            _w.Put(blue);
            SendToBattle(DeliveryMethod.ReliableOrdered);
        }

        private void OnTurnedBlue(BattleTank tank)
        {
            if (_battle != null) { RaiseNotice(ChatNotice.TurnedBlue, _battle.SlotOfTank(tank)); }
        }

        /// <summary>A player went over the team damage limit: notice, removal, the AI takes the seat.</summary>
        private void OnTeamKillKick(BattleTank tank)
        {
            if (_battle == null || tank == null) { return; }
            var slot = _battle.SlotOfTank(tank);
            var plan = _battle.NetPlan;
            if (plan == null || slot < 0 || slot >= plan.Slots.Count || !plan.Slots[slot].IsHuman) { return; }
            RaiseNotice(ChatNotice.TeamKillKick, slot);
            var owner = plan.Slots[slot].Owner;
            for (var i = 0; i < _clients.Count; i++)
            {
                if (_clients[i].Owner != owner || !_clients[i].InBattle) { continue; }
                Debug.LogWarning("[BlitzDirect] kicking player " + owner + " for team damage");
                Kick(_clients[i], TeamKillKickKey);
                return;
            }
        }
    }
}
