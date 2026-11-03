using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Security;
using BlitzLegacy.Runtime.Ui;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace BlitzLegacy.Direct
{
    /// <summary>
    /// A player's connection to the dedicated server, from the hangar queue
    /// to the end of the battle. Everything on screen is a puppet of the
    /// server's snapshots, played back a little in the past so two snapshots
    /// always bracket the frame; this peer only sends its controls.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public sealed class DirectClient : MonoBehaviour
    {
        /// <summary>
        /// Longest stretch played past the newest snapshot (dead reckoning
        /// along the recent motion): a short loss shows as a tank that keeps
        /// driving, not as a frozen hull that then jumps.
        /// </summary>
        public const float MaxExtrapolation = 0.35f;
        /// <summary>Shortest history the dead reckoning measures the motion over.</summary>
        public const double ExtrapolationBase = 0.1;
        /// <summary>A jump this long between two snapshots is a teleport (respawn): never extrapolated.</summary>
        public const float TeleportMeters = 10f;
        public const float ReconnectSeconds = 60f;
        public const float ReconnectInterval = 2f;
        public const float LoadWaitSeconds = DirectServer.LoadWaitSeconds;
        private const int BufferLength = 32;

        public static DirectClient Current { get; private set; }

        /// <summary>Smoke only: circle and fire every few seconds.</summary>
        public static bool AutoDrive;

        public IBattleOnlineSink Sink;

        private struct Sample
        {
            public double Time;
            public DirectTankState State;
            public UprisingSnapshot Uprising;
            public BigBossSnapshot BigBoss;
        }

        private bool _hasTickets;
        private double _ticketTime;
        private int _ticketsA, _ticketsB;

        private struct RocketFrame
        {
            public double Time;
            public List<DirectRocket> Rockets;
        }

        private string _host;
        private int _port;
        private BattleMode _mode;
        private NetManager _net;
        private NetPeer _peer;
        private readonly EventBasedNetListener _listener = new EventBasedNetListener();
        private readonly NetDataWriter _w = new NetDataWriter();
        private NetDataWriter _connect;
        private string _seat;
        private int _owner;
        private bool _closing;
        private bool _polling;
        private bool _shutdownPending;
        private NetPeer _leavingPeer;
        private float _leaveUntil = -1f;
        private bool _statusShown;

        private BattleNetPlan _pendingPlan;
        private BattleSession _battle;
        private bool _sawBattle;
        private bool _ended;
        private bool _released;
        private bool _serverStarted;
        private bool _loadedSent;
        private bool _waitingBanner;
        private float _clientHoldAt = -1f;
        private float _reconnectUntil = -1f;
        private float _nextAttemptAt;
        private float _hideBannerAt = -1f;

        private uint _inputSequence;
        private float _inputClock;
        private bool _fireLatch;
        private byte _fireSequence;
        private byte _uprisingAction;
        private byte _uprisingSequence;

        private readonly List<Sample>[] _slots = new List<Sample>[BattleNetPlan.MaxSlots];
        private readonly List<RocketFrame> _rocketFrames = new List<RocketFrame>();
        private readonly DirectTimeline _timeline = new DirectTimeline();
        private int _frames;
        private int _starved;
        private int _extrapolated;
        private int _corrections;
        private readonly DirectSmoothing[] _smoothing = new DirectSmoothing[BattleNetPlan.MaxSlots];

        public static DirectClient Open(IBattleOnlineSink sink, BattleMode mode, string host, int port)
        {
            var go = new GameObject("DirectClient");
            DontDestroyOnLoad(go);
            var client = go.AddComponent<DirectClient>();
            client.Sink = sink;
            client._mode = mode;
            client._host = host;
            client._port = port;
            Current = client;
            return client;
        }

        private void Start()
        {
            for (var i = 0; i < _slots.Length; i++) { _slots[i] = new List<Sample>(BufferLength); }
            var token = new byte[DirectProtocol.TokenLength];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(token); }
            _seat = DirectProtocol.SeatOf(token);
            _connect = new NetDataWriter();
            try
            {
                DirectProtocol.WriteConnect(_connect, DirectConfig.Build, _mode, token,
                    BattleNetHelloFactory.FromCurrentAccount(DirectConfig.Build));
            }
            catch (Exception e) when (OnlineBan.IsBan(e))
            {
                // Hard online bans (the Sandbox, mod tanks): the request never leaves this device.
                Fail(OnlineBan.KeyOf(e));
                return;
            }

            _listener.PeerConnectedEvent += OnConnected;
            _listener.PeerDisconnectedEvent += OnDisconnected;
            _listener.NetworkReceiveEvent += OnReceive;
            _net = new NetManager(_listener)
            {
                AutoRecycle = true,
                DisconnectTimeout = 10000,
                UpdateTime = 10,
                ChannelsCount = 1,
            };
            if (!_net.Start())
            {
                Fail("online:error/no_connection");
                return;
            }
            BattleNetHooks.HoldIntro = HoldIntroHook;
            BattleNetHooks.IntroReleased = IntroReleasedHook;
            BattleNetHooks.PingMs = PingHook;
            BattleNetHooks.UprisingActionRequested += OnUprisingActionRequested;
            Status("online:queue/connecting");
            Connect();
        }

        private void Connect()
        {
            try
            {
                _peer = _net.Connect(_host, _port, _connect);
                Debug.Log("[BlitzDirect] connecting to " + _host + ":" + _port);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BlitzDirect] connect failed: " + e.Message);
                _peer = null;
            }
        }

        private void Status(string key)
        {
            if (Sink != null && !_sawBattle) { Sink.OnlineStatus(key); }
        }

        private void Fail(string key)
        {
            Debug.LogWarning("[BlitzDirect] matchmaking failed: " + key);
            _ended = true;
            var sink = Sink;
            Sink = null;
            if (sink != null && !_sawBattle) { sink.OnlineFailed(key); }
            Shutdown();
        }

        public void Shutdown()
        {
            if (_closing) { return; }
            // Not from inside PollEvents: the manager is stopped right after it.
            if (_polling) { _shutdownPending = true; return; }
            _closing = true;
            if (_net != null) { _net.Stop(true); }
            Unhook();
            if (Current == this) { Current = null; }
            if (this != null) { Destroy(gameObject); }
        }

        /// <summary>Longest wait for the server to confirm a queue leave.</summary>
        public const float LeaveSeconds = 1.5f;

        /// <summary>
        /// Leaves matchmaking for good. Stopping the socket at once sends one
        /// unreliable goodbye that a phone often loses, and the server then
        /// counts the player in its lobby until the timeout: a re-queue shows
        /// up as an extra "player". A peer disconnect is resent until the
        /// server acknowledges it, so the socket stays up for that long.
        /// </summary>
        public void Leave()
        {
            if (_closing || _leaveUntil > 0f) { return; }
            _ended = true;
            Sink = null;
            Unhook();
            if (Current == this) { Current = null; }
            var peer = _peer;
            _peer = null;
            if (_net == null || peer == null || peer.ConnectionState == ConnectionState.Disconnected)
            {
                Shutdown();
                return;
            }
            _leavingPeer = peer;
            _leaveUntil = Time.realtimeSinceStartup + LeaveSeconds;
            peer.Disconnect();
            Debug.Log("[BlitzDirect] leaving the queue");
        }

        /// <summary>Polls a pending leave; true while the goodbye is still in flight.</summary>
        private bool TickLeave()
        {
            if (_leaveUntil <= 0f) { return false; }
            var done = _leavingPeer == null || _leavingPeer.ConnectionState == ConnectionState.Disconnected
                || Time.realtimeSinceStartup >= _leaveUntil;
            if (done)
            {
                _leaveUntil = -1f;
                _leavingPeer = null;
                Shutdown();
            }
            return true;
        }

        private void Unhook()
        {
            BattleNetHooks.UprisingActionRequested -= OnUprisingActionRequested;
            if (BattleNetHooks.HoldIntro == (Func<bool>)HoldIntroHook) { BattleNetHooks.HoldIntro = null; }
            if (BattleNetHooks.IntroReleased == (Func<bool>)IntroReleasedHook) { BattleNetHooks.IntroReleased = null; }
            if (BattleNetHooks.PingMs == (Func<int>)PingHook) { BattleNetHooks.PingMs = null; }
            if (BattleChatView.NetSend == (Action<ChatChannel, string>)SendChat) { BattleChatView.NetSend = null; }
            if (_sawBattle) { BattleNetHooks.PuppetWorld = false; }
            BattleNetHooks.HostRockets.Clear();
            TankTurret.NetLead = TankTurret.NetLeadSeconds;
        }

        private double RoundTripSeconds()
        {
            return _peer != null && _peer.ConnectionState == ConnectionState.Connected ? _peer.RoundTripTime / 1000.0 : 0.0;
        }

        /// <summary>
        /// Smoke diagnostics since the last call: playback delay and jitter, how
        /// many frames the own tank was extrapolated or starved, and the own
        /// turret against the host's.
        /// </summary>
        public string TakeStats()
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            var line = "delay=" + (_timeline.Delay * 1000).ToString("F0", c) + "ms jitter=" + (_timeline.Jitter * 1000).ToString("F0", c)
                + "ms frames=" + _frames + " extrap=" + _extrapolated + " starved=" + _starved + " snaps=" + _timeline.Snaps
                + " corrections=" + _corrections + " lead=" + TankTurret.NetLead.ToString("F2", c);
            var player = _battle != null ? _battle.Player : null;
            if (player != null && player.Turret != null)
            {
                line += " yaw=" + (player.Turret.Yaw * Mathf.Rad2Deg).ToString("F0", c)
                    + " hostYaw=" + (player.Turret.NetHostYaw * Mathf.Rad2Deg).ToString("F0", c);
            }
            _frames = 0;
            _extrapolated = 0;
            _starved = 0;
            _corrections = 0;
            return line;
        }

        private void OnDestroy()
        {
            if (!_closing)
            {
                _closing = true;
                if (_net != null) { _net.Stop(true); }
                Unhook();
            }
            if (Current == this) { Current = null; }
        }

        /// <summary>Local memory edit caught: leave the battle for good.</summary>
        public static Task LeaveForAntiCheat()
        {
            var client = Current;
            if (client != null)
            {
                client._ended = true;
                client._reconnectUntil = -1f;
                client.Shutdown();
            }
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------ network events

        private void OnConnected(NetPeer peer)
        {
            Debug.Log("[BlitzDirect] connected, rtt " + peer.RoundTripTime + " ms");
            if (!_statusShown) { _statusShown = true; Status("online:queue/waiting"); }
        }

        private void OnDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (peer != _peer || _closing) { return; }
            _peer = null;
            Debug.LogWarning("[BlitzDirect] disconnected: " + info.Reason);
            if (_ended) { return; }
            if (_battle != null)
            {
                BeginReconnect();
                return;
            }
            var key = "online:error/no_connection";
            if (info.Reason == DisconnectReason.ConnectionRejected && info.AdditionalData != null && info.AdditionalData.AvailableBytes >= 2)
            {
                try
                {
                    var sent = DirectProtocol.GetSafeString(info.AdditionalData, DirectProtocol.MaxKeyLength);
                    if (sent.StartsWith("online:", StringComparison.Ordinal)) { key = sent; }
                }
                catch (FormatException) { }
            }
            Fail(key);
        }

        private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            if (peer != _peer || reader.AvailableBytes < 1) { return; }
            try
            {
                var type = (DirectMessage)reader.GetByte();
                switch (type)
                {
                    case DirectMessage.Lobby: OnLobby(DirectLobby.Read(reader)); break;
                    case DirectMessage.Plan: OnPlan(reader); break;
                    case DirectMessage.Snapshot: OnSnapshot(reader); break;
                    case DirectMessage.Rockets: OnRockets(reader); break;
                    case DirectMessage.Shot: OnShot(reader); break;
                    case DirectMessage.Hit: OnHit(DirectHit.Read(reader)); break;
                    case DirectMessage.UprisingEffect: OnUprisingEffect(reader); break;
                    case DirectMessage.UprisingChain: OnUprisingChain(reader); break;
                    case DirectMessage.BigBossEffect: OnBigBossEffect(reader); break;
                    case DirectMessage.BigBossTrap: OnBigBossTrap(reader); break;
                    case DirectMessage.BattleStart: OnBattleStart(); break;
                    case DirectMessage.IntroReleased: _released = true; break;
                    case DirectMessage.BattleEnd: DirectProtocol.Need(reader, 4); OnBattleEnd(reader.GetInt()); break;
                    case DirectMessage.ChatDeliver: OnChatDeliver(reader); break;
                    case DirectMessage.ChatNotice: OnChatNotice(reader); break;
                    case DirectMessage.Kick: OnKicked(DirectProtocol.GetSafeString(reader, DirectProtocol.MaxKeyLength)); break;
                }
            }
            catch (Exception e)
            {
                var ban = OnlineBan.KeyOf(e);
                if (ban != null) { LeaveForBan(ban); return; }
                Debug.LogWarning("[BlitzDirect] bad packet from the server: " + e.Message);
            }
        }

        /// <summary>
        /// Hard client-side online bans: the server tried to move this battle
        /// into the offline-only Sandbox or to seat a mod tank in it. An honest
        /// client leaves on its own, whatever the server does next.
        /// </summary>
        private void LeaveForBan(string key)
        {
            if (string.IsNullOrEmpty(key)) { key = SandboxOnlineBan.ErrorKey; }
            Debug.LogWarning("[BlitzDirect] online ban (" + key + "); leaving the battle");
            if (_battle == null) { Fail(key); return; }
            _ended = true;
            _reconnectUntil = -1f;
            var peer = _peer;
            if (peer != null && peer.ConnectionState != ConnectionState.Disconnected) { peer.Disconnect(); }
            NetStatusBanner.Show(key);
            _hideBannerAt = Time.realtimeSinceStartup + 6f;
            _battle.ForceFinish(-1);
        }

        private void OnLobby(DirectLobby lobby)
        {
            if (Sink == null || _sawBattle) { return; }
            Sink.OnlineLobby(Mathf.Max(1, lobby.Players), lobby.Capacity, lobby.StartsIn, lobby.Tiers, lobby.Classes);
            Sink.OnlineQueue(lobby.Waiting, lobby.QueueTiers, lobby.QueueClasses);
        }

        private void OnPlan(NetPacketReader reader)
        {
            DirectProtocol.Need(reader, 5);
            var owner = reader.GetInt();
            var plan = BattleNetPlan.FromBytes(reader.GetRemainingBytes());
            if (plan.Build != DirectConfig.Build)
            {
                Debug.LogWarning("[BlitzDirect] server runs build " + plan.Build);
                Fail("online:error/version");
                return;
            }
            if (plan.SlotOf(owner) < 0)
            {
                if (_battle != null) { GiveUpBattle(); } else { Fail("online:error/room_closed"); }
                return;
            }
            _owner = owner;
            if (_battle != null)
            {
                if (!_battle.AdoptNetPlan(plan, owner))
                {
                    Debug.LogWarning("[BlitzDirect] the server's plan is a different battle");
                    GiveUpBattle();
                    return;
                }
                Debug.Log("[BlitzDirect] back in the battle as player " + owner);
                _reconnectUntil = -1f;
                NetStatusBanner.Hide();
                return;
            }
            Debug.Log("[BlitzDirect] plan: map=" + plan.MapId + " mode=" + plan.Mode + " slots=" + plan.Slots.Count
                + " own slot " + plan.SlotOf(owner));
            _pendingPlan = plan;
        }

        // ------------------------------------------------------------ world

        private void OnSnapshot(NetPacketReader reader)
        {
            var h = DirectSnapshot.ReadHeader(reader);
            if (reader.AvailableBytes < h.Count * DirectSnapshot.SlotSize(h.Mode)) { throw new FormatException("short snapshot"); }
            _timeline.OnPacket(h.Time, Time.realtimeSinceStartupAsDouble);
            if (_battle == null) { return; }
            var bossValid = false;
            for (var i = 0; i < h.Count; i++)
            {
                var sample = new Sample { Time = h.Time, State = DirectTankState.Read(reader) };
                if (h.Uprising) { sample.Uprising = DirectUprising.Read(reader); }
                if (h.BigBoss)
                {
                    sample.BigBoss = DirectBigBoss.Read(reader);
                    bossValid |= (sample.State.Flags & DirectWorld.BigBossValidFlag) != 0;
                }
                Insert(_slots[h.First + i], sample);
            }
            // Tickets ride on every chunk; only the newest tick's ledger counts.
            if (bossValid && (!_hasTickets || h.Time >= _ticketTime))
            {
                _hasTickets = true;
                _ticketTime = h.Time;
                _ticketsA = h.TeamA;
                _ticketsB = h.TeamB;
            }
        }

        private static void Insert(List<Sample> buffer, Sample sample)
        {
            var at = buffer.Count;
            while (at > 0 && buffer[at - 1].Time >= sample.Time)
            {
                if (buffer[at - 1].Time == sample.Time) { return; } // duplicate
                at--;
            }
            buffer.Insert(at, sample);
            if (buffer.Count > BufferLength) { buffer.RemoveAt(0); }
        }

        private void OnRockets(NetPacketReader reader)
        {
            var rockets = new List<DirectRocket>();
            uint tick;
            double time;
            DirectSnapshot.ReadRockets(reader, out tick, out time, rockets);
            if (_battle == null) { return; }
            var at = _rocketFrames.Count;
            while (at > 0 && _rocketFrames[at - 1].Time > time) { at--; }
            if (at > 0 && _rocketFrames[at - 1].Time == time)
            {
                _rocketFrames[at - 1].Rockets.AddRange(rockets); // second chunk of one tick
                return;
            }
            _rocketFrames.Insert(at, new RocketFrame { Time = time, Rockets = rockets });
            if (_rocketFrames.Count > BufferLength) { _rocketFrames.RemoveAt(0); }
        }

        private void Interpolate()
        {
            var session = _battle;
            if (session == null || session.NetPlan == null || !_timeline.HasClock) { return; }
            var renderTime = _timeline.Advance(Time.realtimeSinceStartupAsDouble);
            _frames++;
            // The own turret may lead the host's angles by what this link explains.
            TankTurret.NetLead = (float)(RoundTripSeconds() + _timeline.Delay + 2.0 / DirectProtocol.SnapshotRate + 0.05);
            var uprising = session.Mode == BattleMode.Uprising;
            var bigBoss = session.Mode == BattleMode.BigBoss;
            var tanks = session.SlotTanks;
            var count = Mathf.Min(tanks.Count, BattleNetPlan.MaxSlots);
            for (var i = 0; i < count; i++)
            {
                var tank = tanks[i];
                var buffer = _slots[i];
                if (tank == null || buffer.Count == 0) { continue; }
                int a, b;
                float alpha;
                var past = Bracket(buffer, renderTime, out a, out b, out alpha);
                if (tank == session.Player && past > 0)
                {
                    if (past > MaxExtrapolation) { _starved++; } else { _extrapolated++; }
                }
                var jump = (buffer[b].State.Position - buffer[a].State.Position).sqrMagnitude > TeleportMeters * TeleportMeters;
                if (alpha > 1f && jump) { alpha = 1f; }
                DirectWorld.Apply(session, tank, buffer[a].State, buffer[b].State, alpha, uprising, buffer[b].Uprising,
                    bigBoss, buffer[b].BigBoss);
                // Hides the correction once late data lands: the prediction error
                // fades out over a few frames instead of teleporting the hull.
                var span = buffer[b].Time - buffer[a].Time;
                var velocity = span > 0 && !jump ? (buffer[b].State.Position - buffer[a].State.Position) / (float)span : Vector3.zero;
                var shown = tank.transform;
                Vector3 position;
                Quaternion rotation;
                if (_smoothing[i].Step(shown.position, shown.rotation, velocity, Time.unscaledDeltaTime, out position, out rotation))
                {
                    _corrections++;
                }
                if (_smoothing[i].Active) { shown.SetPositionAndRotation(position, rotation); }
                // Older samples than the bracket are never needed again.
                if (a > 1) { buffer.RemoveRange(0, a - 1); }
            }
            if (_hasTickets && session.BigBoss != null) { session.BigBoss.ApplyNet(_ticketsA, _ticketsB); }
            InterpolateRockets(renderTime);
        }

        /// <summary>
        /// The two samples around the time and the blend between them; past the
        /// newest sample the last two are extrapolated for up to
        /// <see cref="MaxExtrapolation"/>. Returns how far the time is past the
        /// newest sample (0 inside the buffer).
        /// </summary>
        private static double Bracket(List<Sample> buffer, double time, out int a, out int b, out float alpha)
        {
            var last = buffer.Count - 1;
            if (time >= buffer[last].Time)
            {
                var past = time - buffer[last].Time;
                a = b = last;
                alpha = 0f;
                if (last == 0) { return past; }
                a = last - 1;
                // Extrapolate along a longer base than one snapshot: a single
                // jittery pair blown up by a third of a second would swing the hull.
                while (a > 0 && buffer[last].Time - buffer[a].Time < ExtrapolationBase) { a--; }
                var lastSpan = buffer[last].Time - buffer[a].Time;
                alpha = lastSpan > 0 ? 1f + (float)(Math.Min(past, MaxExtrapolation) / lastSpan) : 1f;
                return past;
            }
            if (time <= buffer[0].Time) { a = b = 0; alpha = 0f; return 0; }
            b = 1;
            while (b < last && buffer[b].Time < time) { b++; }
            a = b - 1;
            var span = buffer[b].Time - buffer[a].Time;
            alpha = span > 0 ? Mathf.Clamp01((float)((time - buffer[a].Time) / span)) : 1f;
            return 0;
        }

        /// <summary>The server's rockets on the tanks' clock (see <see cref="BattleNetHooks.HostRockets"/>).</summary>
        private void InterpolateRockets(double time)
        {
            var samples = BattleNetHooks.HostRockets;
            samples.Clear();
            if (_rocketFrames.Count == 0) { return; }
            var last = _rocketFrames.Count - 1;
            // The server stopped sending: the last rocket is long gone.
            if (time - _rocketFrames[last].Time > 0.5) { _rocketFrames.Clear(); return; }
            int a = last, b = last;
            float alpha = 0f;
            if (time < _rocketFrames[last].Time)
            {
                b = 0;
                while (b < last && _rocketFrames[b].Time < time) { b++; }
                a = Mathf.Max(0, b - 1);
                var span = _rocketFrames[b].Time - _rocketFrames[a].Time;
                alpha = span > 0 ? Mathf.Clamp01((float)((time - _rocketFrames[a].Time) / span)) : 1f;
            }
            var from = _rocketFrames[a].Rockets;
            var to = _rocketFrames[b].Rockets;
            for (var i = 0; i < to.Count; i++)
            {
                var rocket = to[i];
                if (rocket.Id == 0) { continue; }
                var position = rocket.Position;
                for (var j = 0; j < from.Count; j++)
                {
                    if (from[j].Id != rocket.Id) { continue; }
                    position = Vector3.Lerp(from[j].Position, rocket.Position, alpha);
                    break;
                }
                samples[rocket.Id] = new BattleNetHooks.RocketSample { Position = position, Velocity = rocket.Velocity };
            }
            if (a > 1) { _rocketFrames.RemoveRange(0, a - 1); }
        }

        private void OnShot(NetPacketReader reader)
        {
            DirectProtocol.Need(reader, 2 + 36 + 4 + 1);
            var slot = reader.GetByte();
            var shellId = reader.GetByte();
            var origin = DirectProtocol.GetVector3(reader);
            var muzzle = DirectProtocol.GetVector3(reader);
            var direction = DirectProtocol.GetVector3(reader);
            var shotId = reader.GetInt();
            var railgun = reader.GetBool();
            var session = _battle;
            if (session == null || slot >= session.SlotTanks.Count) { return; }
            if (!DirectProtocol.IsFinite(origin) || !DirectProtocol.IsFinite(muzzle) || !DirectProtocol.IsFinite(direction)) { return; }
            var tank = session.SlotTanks[slot];
            if (tank != null) { tank.NetVisualShot(shellId, origin, muzzle, direction, shotId, railgun); }
        }

        private void OnUprisingEffect(NetPacketReader reader)
        {
            DirectProtocol.Need(reader, 1);
            var slot = reader.GetByte();
            var id = DirectProtocol.GetSafeString(reader, 32);
            DirectProtocol.Need(reader, 12);
            var point = DirectProtocol.GetVector3(reader);
            if (!DirectWorld.IsKnownEffect(id)) { return; }
            var uprising = DirectWorld.UprisingAt(_battle, slot);
            if (uprising != null) { uprising.ReplayNetEffect(id, point); }
        }

        private void OnBigBossEffect(NetPacketReader reader)
        {
            DirectProtocol.Need(reader, 1);
            var slot = reader.GetByte();
            var id = DirectProtocol.GetSafeString(reader, 32);
            DirectProtocol.Need(reader, 12);
            var point = DirectProtocol.GetVector3(reader);
            if (!BigBossTank.IsNetEffect(id) || !DirectProtocol.IsFinite(point)) { return; }
            var seat = DirectWorld.BigBossAt(_battle, slot);
            if (seat != null) { seat.ReplayNetEffect(id, point); }
        }

        private void OnBigBossTrap(NetPacketReader reader)
        {
            DirectProtocol.Need(reader, 1);
            var slot = reader.GetByte();
            var id = DirectProtocol.GetSafeString(reader, 32);
            DirectProtocol.Need(reader, 12);
            var point = DirectProtocol.GetVector3(reader);
            var session = _battle;
            var seat = DirectWorld.BigBossAt(session, slot);
            if (seat == null || session.BigBoss == null || !session.BigBoss.IsTrap(id)) { return; }
            session.BigBoss.ReplayTrap(seat, id, point);
        }

        private void OnUprisingChain(NetPacketReader reader)
        {
            DirectProtocol.Need(reader, 1 + 24);
            var slot = reader.GetByte();
            var from = DirectProtocol.GetVector3(reader);
            var to = DirectProtocol.GetVector3(reader);
            var uprising = DirectWorld.UprisingAt(_battle, slot);
            if (uprising != null) { uprising.ReplayNetChain(from, to); }
        }

        private void OnBattleStart()
        {
            Debug.Log("[BlitzDirect] server battle is live");
            _serverStarted = true;
            if (_battle != null && !_battle.Started) { _battle.StartBattle(); }
        }

        private void OnBattleEnd(int winner)
        {
            Debug.Log("[BlitzDirect] server ended the battle, winner team " + winner);
            _ended = true;
            _reconnectUntil = -1f;
            NetStatusBanner.Hide();
            if (_battle != null) { _battle.ForceFinish(winner); }
        }

        private void OnChatDeliver(NetPacketReader reader)
        {
            DirectProtocol.Need(reader, 2);
            var slot = reader.GetByte();
            var chatChannel = reader.GetByte();
            var text = DirectProtocol.GetSafeString(reader, DirectProtocol.MaxChatLength);
            var view = BattleChatView.Current;
            if (chatChannel > (byte)ChatChannel.All || _battle == null || view == null || slot >= _battle.SlotTanks.Count) { return; }
            // Receiver-side: the view sanitizes again and re-checks the channel.
            view.Receive(_battle.SlotTanks[slot], (ChatChannel)chatChannel, text);
        }

        private void OnChatNotice(NetPacketReader reader)
        {
            DirectProtocol.Need(reader, 3);
            var kind = reader.GetByte();
            var slot = reader.GetByte();
            var blue = reader.GetBool();
            var view = BattleChatView.Current;
            if (_battle == null || view == null || slot >= _battle.SlotTanks.Count) { return; }
            if (kind != (byte)ChatNotice.TeamKillKick && kind != (byte)ChatNotice.TurnedBlue) { return; }
            view.Notice((ChatNotice)kind, _battle.SlotTanks[slot], blue);
        }

        private void OnKicked(string key)
        {
            if (!key.StartsWith("online:", StringComparison.Ordinal)) { key = "online:net/lost"; }
            Debug.LogWarning("[BlitzDirect] removed by the server: " + key);
            if (_battle == null) { Fail(key); return; }
            _ended = true;
            _reconnectUntil = -1f;
            NetStatusBanner.Show(key);
            _battle.ForceFinish(-1);
            _hideBannerAt = Time.realtimeSinceStartup + 6f;
        }

        private void SendChat(ChatChannel chatChannel, string text)
        {
            if (_peer == null || _peer.ConnectionState != ConnectionState.Connected) { return; }
            var clean = ChatSanitizer.CleanText(text);
            if (clean.Length == 0) { return; }
            if (clean.Length > DirectProtocol.MaxChatLength) { clean = clean.Substring(0, DirectProtocol.MaxChatLength); }
            _w.Reset();
            _w.Put((byte)DirectMessage.Chat);
            _w.Put((byte)chatChannel);
            _w.Put(clean);
            _peer.Send(_w, DeliveryMethod.ReliableOrdered);
        }

        private void OnUprisingActionRequested(byte action)
        {
            if (action == 0 || action > BattleRemoteDriver.MaxAction) { return; }
            _uprisingAction = action;
            unchecked { _uprisingSequence++; }
            if (_uprisingSequence == 0) { _uprisingSequence = 1; }
        }

        /// <summary>
        /// The server's verdict on a hit: the victim's puppet plays the bounce
        /// or the penetration heard inside the hull, as the offline game does.
        /// </summary>
        private void OnHit(DirectHit hit)
        {
            var session = _battle;
            if (session == null || hit.Victim >= session.SlotTanks.Count) { return; }
            var victim = session.SlotTanks[hit.Victim];
            if (victim == null) { return; }
            var attacker = hit.Attacker < session.SlotTanks.Count ? session.SlotTanks[hit.Attacker] : null;
            victim.ApplyNetDamageFeedback(hit.ToInfo(attacker));
        }

        // ------------------------------------------------------------ per frame

        private void Update()
        {
            if (_net == null || _closing) { return; }
            _polling = true;
            try { _net.PollEvents(); }
            finally { _polling = false; }
            if (_shutdownPending) { Shutdown(); }
            if (_closing || TickLeave()) { return; }
            var now = Time.realtimeSinceStartup;
            if (_hideBannerAt > 0f && now >= _hideBannerAt) { _hideBannerAt = -1f; NetStatusBanner.Hide(); }
            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap == null) { return; }

            if (_pendingPlan != null && bootstrap.Battle == null) { EnterBattle(bootstrap); }
            if (!TrackBattle(bootstrap)) { return; }
            if (_battle == null) { return; }

            if (_reconnectUntil > 0f) { TickReconnect(now); }
            TickSynchronizedStart(bootstrap, now);
            Interpolate();
            SendInput();
        }

        private void EnterBattle(BlitzBootstrap bootstrap)
        {
            var plan = _pendingPlan;
            _pendingPlan = null;
            BattleNetHooks.PuppetWorld = true;
            var net = new BattleNetSetup
            {
                IsClient = true,
                Mode = plan.Mode,
                Plan = plan,
                LocalOwner = _owner,
                LocalSeat = _seat,
            };
            if (!bootstrap.EnterNetBattle(net))
            {
                BattleNetHooks.PuppetWorld = false;
                _pendingPlan = plan;
                return;
            }
            // The queue screen is gone with the battle transition.
            _sawBattle = true;
            Sink = null;
        }

        /// <summary>Returns false once this connection is over (back in the hangar).</summary>
        private bool TrackBattle(BlitzBootstrap bootstrap)
        {
            var current = bootstrap.Battle;
            if (current == _battle) { return true; }
            if (current == null)
            {
                if (_battle != null || _sawBattle)
                {
                    Debug.Log("[BlitzDirect] back in the hangar; leaving the server");
                    if (_waitingBanner) { NetStatusBanner.Hide(); }
                    _battle = null;
                    Shutdown();
                    return false;
                }
                return true;
            }
            if (!_sawBattle || current.Net == null || !current.Net.IsClient) { return true; }
            _battle = current;
            for (var i = 0; i < _smoothing.Length; i++) { _smoothing[i].Reset(); }
            BattleChatView.NetSend = SendChat;
            if (_serverStarted && !_battle.Started) { _battle.StartBattle(); }
            return true;
        }

        private void TickSynchronizedStart(BlitzBootstrap bootstrap, float now)
        {
            var intro = bootstrap.MapIntro;
            if (_ended || intro == null || _battle.Started)
            {
                if (_waitingBanner) { _waitingBanner = false; NetStatusBanner.Hide(); }
                return;
            }
            if (!_loadedSent && _peer != null && _peer.ConnectionState == ConnectionState.Connected)
            {
                _loadedSent = true;
                _w.Reset();
                _w.Put((byte)DirectMessage.Loaded);
                _peer.Send(_w, DeliveryMethod.ReliableOrdered);
                Debug.Log("[BlitzDirect] battle loaded, waiting for the server to release the intro");
            }
            if (intro.TimeUp && _clientHoldAt < 0f) { _clientHoldAt = now; }
            var waiting = intro.TimeUp && !_released;
            if (waiting != _waitingBanner)
            {
                _waitingBanner = waiting;
                if (waiting) { NetStatusBanner.Show("online:net/waiting_players"); }
                else { NetStatusBanner.Hide(); }
            }
        }

        private void SendInput()
        {
            var battle = _battle;
            if (battle.Player == null || battle.Controls == null) { return; }
            var controls = battle.Controls;
            _fireLatch |= controls.FireHeld;
            _inputClock += Time.unscaledDeltaTime;
            const float interval = 1f / DirectProtocol.InputRate;
            if (_inputClock < interval) { return; }
            _inputClock = Mathf.Min(_inputClock - interval, interval);
            if (_peer == null || _peer.ConnectionState != ConnectionState.Connected || _ended) { _fireLatch = false; return; }

            var input = new DirectInput
            {
                Sequence = ++_inputSequence,
                // The server kicks axes outside [-1, 1] as a modified client.
                Forward = Mathf.Clamp(controls.DriveForward, -1f, 1f),
                Right = Mathf.Clamp(controls.DriveRight, -1f, 1f),
                ShellId = (byte)Mathf.Clamp(battle.Player.ShellId, 0, 255),
                UprisingAction = _uprisingAction,
                UprisingSequence = _uprisingSequence,
            };
            var fire = _fireLatch;
            _fireLatch = false;
            if (battle.View != null)
            {
                input.Aim = battle.View.AimTarget;
                input.Buttons |= DirectButtons.HasAim;
                if (battle.View.ArtyMode) { input.Buttons |= DirectButtons.Arty; }
            }
            if (AutoDrive)
            {
                var t = Time.realtimeSinceStartup;
                input.Forward = 1f;
                input.Right = Mathf.Sin(t * 0.5f) > 0f ? 1f : 0f;
                fire = (int)t % 4 == 0;
                if (battle.View == null || !battle.View.ArtyMode)
                {
                    var tank = battle.Player.transform;
                    // Sweeps the aim around the hull, so the smoke sees the server traverse.
                    input.Aim = tank.position + Quaternion.Euler(0f, t * 20f, 0f) * tank.forward * 100f;
                    input.Buttons |= DirectButtons.HasAim;
                }
            }
            if (fire) { input.Buttons |= DirectButtons.Fire; _fireSequence++; }
            input.FireSequence = _fireSequence;
            if (battle.Player.TurretBlock) { input.Buttons |= DirectButtons.TurretBlock; }
            _w.Reset();
            input.Write(_w);
            _peer.Send(_w, DeliveryMethod.Sequenced);
        }

        // ------------------------------------------------------------ reconnect

        private void BeginReconnect()
        {
            if (_ended || _battle == null) { return; }
            if (_reconnectUntil < 0f)
            {
                _reconnectUntil = Time.realtimeSinceStartup + ReconnectSeconds;
                _nextAttemptAt = Time.realtimeSinceStartup + 1f;
                Debug.LogWarning("[BlitzDirect] connection lost in battle, reconnecting");
            }
            NetStatusBanner.Show("online:net/reconnecting");
        }

        private void TickReconnect(float now)
        {
            if (_ended) { _reconnectUntil = -1f; return; }
            if (now >= _reconnectUntil) { GiveUpBattle(); return; }
            if (_peer != null || now < _nextAttemptAt) { return; }
            _nextAttemptAt = now + ReconnectInterval;
            Connect();
        }

        private void GiveUpBattle()
        {
            Debug.LogWarning("[BlitzDirect] could not get back into the battle");
            _reconnectUntil = -1f;
            _ended = true;
            NetStatusBanner.Show("online:net/lost");
            _hideBannerAt = Time.realtimeSinceStartup + 4f;
            if (_battle != null) { _battle.ForceFinish(-1); }
        }

        // ------------------------------------------------------------ hooks

        private static bool HoldIntroHook()
        {
            var c = Current;
            if (c == null || c._battle == null || c._ended || c._released) { return false; }
            // Never wait forever on a server that went quiet.
            return !(c._clientHoldAt > 0f && Time.realtimeSinceStartup - c._clientHoldAt > LoadWaitSeconds + 10f);
        }

        private static bool IntroReleasedHook()
        {
            var c = Current;
            return c != null && c._battle != null && !c._ended && c._released;
        }

        private static int PingHook()
        {
            var c = Current;
            if (c == null || c._peer == null || c._peer.ConnectionState != ConnectionState.Connected) { return -1; }
            return c._peer.RoundTripTime;
        }
    }

    /// <summary>The hangar's online mode on the project's own server.</summary>
    public sealed class DirectOnlineService : IBattleOnlineService
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            // The server process plays no online itself.
            if (DirectServer.IsServerProcess) { return; }
            var previous = AntiCheatResponder.LeaveOnline;
            AntiCheatResponder.LeaveOnline = async () =>
            {
                if (previous != null) { await previous(); }
                await DirectClient.LeaveForAntiCheat();
            };
            if (DirectConfig.HasServer()) { BattleOnline.DirectService = new DirectOnlineService(); }
        }

        public void Begin(IBattleOnlineSink sink, BattleMode mode)
        {
            var current = DirectClient.Current;
            if (current != null)
            {
                // A connection left over from the last battle never blocks the next one.
                if (BlitzBootstrap.Current != null && BlitzBootstrap.Current.Battle == null) { current.Shutdown(); }
                else { sink.OnlineFailed("online:error/no_connection"); return; }
            }
            if (AntiCheatBan.IsBanned)
            {
                sink.OnlineFailed("online:error/banned");
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
            string host;
            int port;
            if (!DirectConfig.TryGetServer(out host, out port))
            {
                sink.OnlineFailed("online:error/not_configured");
                return;
            }
            DirectClient.Open(sink, mode, host, port);
        }

        public void Cancel()
        {
            var client = DirectClient.Current;
            if (client != null && BlitzBootstrap.Current != null && BlitzBootstrap.Current.Battle == null)
            {
                client.Leave();
            }
        }
    }
}
