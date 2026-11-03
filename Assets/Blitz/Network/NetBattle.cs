using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using Fusion;
using UnityEngine;

namespace BlitzLegacy.Network
{
    /// <summary>
    /// The one networked object of a battle. The host writes the state of
    /// every tank into <see cref="Tanks"/> each tick and feeds remote players'
    /// input to their <see cref="BattleRemoteDriver"/>; clients interpolate
    /// between the two latest snapshots and place their puppets.
    ///
    /// Tanks are addressed by plan slot, so the tanks themselves never have to
    /// be NetworkObjects and the procedural battle build stays untouched.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetBattle : NetworkBehaviour
    {
        public const string PrefabResource = "BlitzNet/NetBattle";

        public static NetBattle Instance { get; private set; }

        /// <summary>
        /// Client: realtime of the last snapshot carrying a new host tick, or
        /// -1 before any. A host that vanished without a clean leave (app
        /// killed, paused, network cut) stops this clock, and the session's
        /// watchdog then hands the battle over to a new host.
        /// </summary>
        public static float LastHostStateAt { get; private set; } = -1f;

        /// <summary>A host was just (re)found: restart the silence clock.</summary>
        public static void MarkHostAlive() { LastHostStateAt = Time.realtimeSinceStartup; }

        private int _lastSnapshotTick = -1;

        [Networked] public int SlotCount { get; set; }

        [Networked, Capacity(BattleNetPlan.MaxSlots)]
        public NetworkArray<NetTankState> Tanks => default;

        /// <summary>Team damage each slot dealt this battle; a client folds its own into its account.</summary>
        [Networked, Capacity(BattleNetPlan.MaxSlots)]
        public NetworkArray<int> TeamDamage => default;

        /// <summary>Lobby: humans in the room, as the host counts them.</summary>
        [Networked] public int LobbyPlayers { get; set; }

        /// <summary>
        /// Host: every player has loaded (or the wait timed out), so all map
        /// intros end now and the countdowns run together. Networked state
        /// rather than an RPC so a peer still loading cannot miss it.
        /// </summary>
        [Networked] public NetworkBool IntroReleased { get; set; }

        /// <summary>Lobby: seconds until the battle starts, negative while waiting.</summary>
        [Networked] public float LobbyStartsIn { get; set; }

        /// <summary>Lobby make-up for the queue tables: ten tiers (X first), then ht, mt, lt, td.</summary>
        [Networked, Capacity(LobbyMixLength)]
        public NetworkArray<byte> LobbyMix => default;

        public const int LobbyMixLength = 14;

        /// <summary>Uprising mode: per-slot abilities, timers, respawns and statuses.</summary>
        [Networked, Capacity(BattleNetPlan.MaxSlots)]
        public NetworkArray<NetUprisingState> Uprising => default;

        /// <summary>One-shot Uprising effects replayed from the host (smoke diagnostics).</summary>
        public static int EffectsReplayed { get; private set; }

        /// <summary>Big Boss mode: per-slot roles, progression, cooldowns and statuses.</summary>
        [Networked, Capacity(BattleNetPlan.MaxSlots)]
        public NetworkArray<NetBigBossState> BigBoss => default;

        /// <summary>Big Boss ticket ledger: team A, team B.</summary>
        [Networked, Capacity(2)]
        public NetworkArray<int> BigBossTickets => default;

        /// <summary>Guided rockets in flight, replayed on the clients along the host's path.</summary>
        [Networked, Capacity(MaxRockets)]
        public NetworkArray<NetRocketState> Rockets => default;

        public const int MaxRockets = 16;

        private ArrayReader<NetTankState> _reader;
        private ArrayReader<NetUprisingState> _uprisingReader;
        private ArrayReader<NetBigBossState> _bigBossReader;
        private ArrayReader<int> _ticketReader;
        private ArrayReader<NetRocketState> _rocketReader;

        public override void Spawned()
        {
            Instance = this;
            LastHostStateAt = Time.realtimeSinceStartup;
            _lastSnapshotTick = -1;
            _reader = GetArrayReader<NetTankState>(nameof(Tanks));
            _uprisingReader = GetArrayReader<NetUprisingState>(nameof(Uprising));
            _bigBossReader = GetArrayReader<NetBigBossState>(nameof(BigBoss));
            _ticketReader = GetArrayReader<int>(nameof(BigBossTickets));
            _rocketReader = GetArrayReader<NetRocketState>(nameof(Rockets));
            BattleNetHooks.HostRockets.Clear();
            BattleNetHooks.UprisingEffectRaised += OnUprisingEffect;
            BattleNetHooks.DamageTaken += OnDamageTaken;
            BattleNetHooks.UprisingChainRaised += OnUprisingChain;
            BattleNetHooks.BigBossEffectRaised += OnBigBossEffect;
            BattleNetHooks.BigBossTrapRaised += OnBigBossTrap;
            BattleChatView.NetSend = SendChat;
            Debug.Log("[BlitzNet] battle object spawned, authority=" + HasStateAuthority);
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            BattleNetHooks.UprisingEffectRaised -= OnUprisingEffect;
            BattleNetHooks.DamageTaken -= OnDamageTaken;
            BattleNetHooks.UprisingChainRaised -= OnUprisingChain;
            BattleNetHooks.BigBossEffectRaised -= OnBigBossEffect;
            BattleNetHooks.BigBossTrapRaised -= OnBigBossTrap;
            if (BattleChatView.NetSend == (System.Action<ChatChannel, string>)SendChat) { BattleChatView.NetSend = null; }
            if (Instance == this) { Instance = null; }
            BattleNetHooks.HostRockets.Clear();
        }

        private static BattleSession Session
        {
            get
            {
                var bootstrap = BlitzLegacy.Runtime.BlitzBootstrap.Current;
                return bootstrap != null ? bootstrap.Battle : null;
            }
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority) { return; }
            var session = Session;
            if (session == null || session.NetPlan == null) { return; }

            foreach (var player in Runner.ActivePlayers)
            {
                if (player == Runner.LocalPlayer) { continue; }
                BattleRemoteDriver driver;
                if (!session.RemoteDrivers.TryGetValue(player.PlayerId, out driver) || driver == null)
                {
                    continue;
                }
                NetInput input;
                if (!Runner.TryGetInputForPlayer(player, out input)) { continue; }
                // The host is authoritative but trusted raw client input: a
                // patched client could send Forward=50 (speed hack) or NaN aim.
                bool hard;
                if (!NetInputGuard.Sanitize(ref input, out hard) && NetInputGuard.Report(player.PlayerId, hard))
                {
                    NetSession.KickCheater(Runner, player);
                }
                if (!session.Started)
                {
                    // Countdown: the host's own tank cannot drive or shoot
                    // yet, so neither can a remote one.
                    input.Forward = 0f;
                    input.Right = 0f;
                    input.Buttons.Set(NetButton.Fire, false);
                }
                driver.Forward = input.Forward;
                driver.Right = input.Right;
                driver.HasAim = input.Buttons.IsSet(NetButton.HasAim);
                driver.Aim = input.Aim;
                driver.Fire = input.Buttons.IsSet(NetButton.Fire);
                driver.TurretBlock = input.Buttons.IsSet(NetButton.TurretBlock);
                driver.Arty = input.Buttons.IsSet(NetButton.Arty);
                driver.ShellId = input.ShellId;
                driver.ApplyUprisingAction(input.UprisingSequence, input.UprisingAction);
            }

            var tanks = session.SlotTanks;
            var count = Mathf.Min(tanks.Count, BattleNetPlan.MaxSlots);
            for (var i = 0; i < count; i++)
            {
                var tank = tanks[i];
                if (tank == null) { continue; }
                Tanks.Set(i, Capture(tank));
                TeamDamage.Set(i, session.TeamDamage.BattleDamage(tank));
                if (session.Mode == BattleMode.Uprising)
                {
                    var uprising = tank.GetComponent<UprisingTank>();
                    if (uprising != null && uprising.State != null)
                    {
                        Uprising.Set(i, NetUprisingState.From(uprising.CaptureNet()));
                    }
                }
                if (tank.BigBoss != null && tank.BigBoss.State != null)
                {
                    BigBoss.Set(i, NetBigBossState.From(tank.BigBoss.CaptureNet()));
                }
            }
            if (session.BigBoss != null)
            {
                BigBossTickets.Set(0, session.BigBoss.Match.TeamA);
                BigBossTickets.Set(1, session.BigBoss.Match.TeamB);
            }
            SlotCount = count;
            WriteRockets();
        }

        private void WriteRockets()
        {
            var live = BattleProjectile.LiveGuided;
            var written = 0;
            for (var i = 0; i < live.Count && written < MaxRockets; i++)
            {
                var rocket = live[i];
                if (rocket == null || !rocket.Alive || rocket.FollowsHost) { continue; }
                Rockets.Set(written++, new NetRocketState
                {
                    Id = rocket.ShotId,
                    Position = rocket.transform.position,
                    Velocity = rocket.Velocity,
                });
            }
            for (var i = written; i < MaxRockets; i++)
            {
                if (Rockets[i].Id != 0) { Rockets.Set(i, default(NetRocketState)); }
            }
        }

        private static NetTankState Capture(BattleTank tank)
        {
            var transform = tank.transform;
            var suspension = tank.Suspension;
            return new NetTankState
            {
                Forward = suspension != null ? suspension.ForwardAxis : 0f,
                Right = suspension != null ? suspension.RightAxis : 0f,
                SpeedMs = suspension != null ? suspension.SpeedMs : 0f,
                WheelHits = (byte)(suspension != null ? Mathf.Clamp(suspension.WheelHits, 0, 255) : 0),
                Flags = (byte)((suspension != null && suspension.OnTank ? 1 : 0) | (tank.TeamKiller ? 2 : 0)
                    | (tank.Turret != null && tank.Turret.LobAim ? 4 : 0)),
                Aim = tank.Turret != null ? tank.Turret.AimTargetLerp : transform.position,
                Dispersion = tank.Dispersion,
                Position = transform.position,
                Rotation = transform.rotation,
                Yaw = tank.Turret != null ? tank.Turret.Yaw : 0f,
                Pitch = tank.Turret != null ? tank.Turret.Pitch : 0f,
                Health = tank.Health != null ? tank.Health.Health : 0,
            };
        }

        public override void Render()
        {
            if (HasStateAuthority) { return; }
            var session = Session;
            if (session == null || session.NetPlan == null) { return; }

            NetworkBehaviourBuffer from;
            NetworkBehaviourBuffer to;
            float alpha;
            if (!TryGetSnapshotsBuffers(out from, out to, out alpha)) { return; }
            var snapshotTick = to.Tick.Raw;
            if (snapshotTick != _lastSnapshotTick)
            {
                _lastSnapshotTick = snapshotTick;
                LastHostStateAt = Time.realtimeSinceStartup;
            }

            var a = _reader.Read(from);
            var b = _reader.Read(to);
            // Same snapshot as the health below: the latest tick runs ahead of the
            // interpolated one, and a respawn read early would be killed again by
            // the older zero health.
            var uprisingMode = session.Mode == BattleMode.Uprising;
            var up = uprisingMode ? _uprisingReader.Read(to) : default(NetworkArrayReadOnly<NetUprisingState>);
            var bossMode = session.BigBoss != null;
            var boss = bossMode ? _bigBossReader.Read(to) : default(NetworkArrayReadOnly<NetBigBossState>);
            var bossWritten = false;
            var tanks = session.SlotTanks;
            var count = Mathf.Min(Mathf.Min(SlotCount, tanks.Count), BattleNetPlan.MaxSlots);
            for (var i = 0; i < count; i++)
            {
                var tank = tanks[i];
                if (tank == null) { continue; }
                var sa = a[i];
                var sb = b[i];
                // An empty slot (never written yet) has a zero rotation.
                if (IsUnset(sb.Rotation)) { continue; }
                if (IsUnset(sa.Rotation)) { sa = sb; }
                // Before health: a death and its respawn countdown land together,
                // so the roster never sees a respawning team as wiped out.
                if (uprisingMode) { ApplyUprising(tank, up[i]); }
                if (bossMode && boss[i].Valid != 0)
                {
                    bossWritten = true;
                    if (tank.BigBoss != null) { tank.BigBoss.ApplyNet(boss[i].ToSnapshot()); }
                }
                tank.ApplyNetState(
                    Vector3.Lerp(sa.Position, sb.Position, alpha),
                    Quaternion.Slerp(sa.Rotation, sb.Rotation, alpha),
                    LerpRadians(sa.Yaw, sb.Yaw, alpha),
                    LerpRadians(sa.Pitch, sb.Pitch, alpha),
                    sb.Health);
                tank.ApplyNetMotion(sb.Forward, sb.Right, Mathf.Lerp(sa.SpeedMs, sb.SpeedMs, alpha),
                    sb.WheelHits, (sb.Flags & 1) != 0);
                tank.TeamKiller = (sb.Flags & 2) != 0;
                tank.ApplyNetAim(Vector3.Lerp(sa.Aim, sb.Aim, alpha), (sb.Flags & 4) != 0,
                    Mathf.Lerp(sa.Dispersion, sb.Dispersion, alpha));
                if (tank == session.Player) { session.TeamDamage.ReplicatedLocalDamage = Mathf.Max(0, TeamDamage[i]); }
            }
            // The ledger of the same tick as the seats; never the zeros of an unwritten one.
            if (bossWritten)
            {
                var tickets = _ticketReader.Read(to);
                session.BigBoss.ApplyNet(tickets[0], tickets[1]);
            }
            ReadRockets(from, to, alpha);
        }

        /// <summary>The host's rockets on the tanks' interpolation clock.</summary>
        private void ReadRockets(NetworkBehaviourBuffer from, NetworkBehaviourBuffer to, float alpha)
        {
            var a = _rocketReader.Read(from);
            var b = _rocketReader.Read(to);
            var samples = BattleNetHooks.HostRockets;
            samples.Clear();
            for (var i = 0; i < MaxRockets; i++)
            {
                var rocket = b[i];
                if (rocket.Id == 0) { continue; }
                var position = rocket.Position;
                for (var j = 0; j < MaxRockets; j++)
                {
                    if (a[j].Id != rocket.Id) { continue; }
                    position = Vector3.Lerp(a[j].Position, rocket.Position, alpha);
                    break;
                }
                samples[rocket.Id] = new BattleNetHooks.RocketSample { Position = position, Velocity = rocket.Velocity };
            }
        }

        private static void ApplyUprising(BattleTank tank, NetUprisingState state)
        {
            if (state.Valid == 0) { return; }
            var uprising = tank.GetComponent<UprisingTank>();
            if (uprising != null) { uprising.ApplyNet(state.ToSnapshot()); }
        }

        private int SlotOf(UprisingTank uprising)
        {
            var session = Session;
            if (uprising == null || session == null || session.NetPlan == null) { return -1; }
            var tanks = session.SlotTanks;
            for (var i = 0; i < tanks.Count && i < BattleNetPlan.MaxSlots; i++)
            {
                if (tanks[i] == uprising.Tank) { return i; }
            }
            return -1;
        }

        private void OnUprisingEffect(UprisingTank uprising, string id, Vector3 point)
        {
            if (Object == null || !HasStateAuthority || string.IsNullOrEmpty(id) || id.Length > 32) { return; }
            var slot = SlotOf(uprising);
            if (slot >= 0) { RPC_UprisingEffect((byte)slot, id, point); }
        }

        private void OnUprisingChain(UprisingTank uprising, Vector3 from, Vector3 to)
        {
            if (Object == null || !HasStateAuthority) { return; }
            var slot = SlotOf(uprising);
            if (slot >= 0) { RPC_UprisingChain((byte)slot, from, to); }
        }

        private void OnBigBossEffect(BigBossTank seat, string id, Vector3 point)
        {
            var session = Session;
            if (Object == null || !HasStateAuthority || seat == null || session == null || !BigBossTank.IsNetEffect(id)) { return; }
            var slot = session.SlotOfTank(seat.Tank);
            if (slot >= 0 && slot < BattleNetPlan.MaxSlots) { RPC_BigBossEffect((byte)slot, id, point); }
        }

        private void OnBigBossTrap(BigBossTank seat, string id, Vector3 point)
        {
            var session = Session;
            if (Object == null || !HasStateAuthority || seat == null || session == null || string.IsNullOrEmpty(id) || id.Length > 32) { return; }
            var slot = session.SlotOfTank(seat.Tank);
            if (slot >= 0 && slot < BattleNetPlan.MaxSlots) { RPC_BigBossTrap((byte)slot, id, point); }
        }

        private static BigBossTank BigBossAt(byte slot)
        {
            var session = Session;
            if (session == null || slot >= session.SlotTanks.Count) { return null; }
            var tank = session.SlotTanks[slot];
            return tank != null ? tank.BigBoss : null;
        }

        /// <summary>Host -> clients: replay a one-shot Big Boss effect (cast, heal, shield hand-over).</summary>
        [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies)]
        public void RPC_BigBossEffect(byte slot, string id, Vector3 point)
        {
            if (!BigBossTank.IsNetEffect(id)) { return; }
            var seat = BigBossAt(slot);
            if (seat != null) { seat.ReplayNetEffect(id, point); EffectsReplayed++; }
        }

        /// <summary>Host -> clients: a gravity trap was placed; it is drawn and timed, never pulled.</summary>
        [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies)]
        public void RPC_BigBossTrap(byte slot, string id, Vector3 point)
        {
            var session = Session;
            var seat = BigBossAt(slot);
            if (seat == null || session == null || session.BigBoss == null || !session.BigBoss.IsTrap(id)) { return; }
            session.BigBoss.ReplayTrap(seat, id, point);
            EffectsReplayed++;
        }

        private static UprisingTank UprisingAt(byte slot)
        {
            var session = Session;
            if (session == null || slot >= session.SlotTanks.Count) { return null; }
            var tank = session.SlotTanks[slot];
            return tank != null ? tank.GetComponent<UprisingTank>() : null;
        }

        /// <summary>Host -> clients: replay a one-shot Uprising effect (ability cast, splash).</summary>
        [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies)]
        public void RPC_UprisingEffect(byte slot, string id, Vector3 point)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 32 || !UprisingVfx.Sources.ContainsKey(id) && id != "pretendDead") { return; }
            var uprising = UprisingAt(slot);
            if (uprising != null) { uprising.ReplayNetEffect(id, point); EffectsReplayed++; }
        }

        /// <summary>Host -> clients: replay one chain-lightning arc.</summary>
        [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies, Channel = RpcChannel.Unreliable)]
        public void RPC_UprisingChain(byte slot, Vector3 from, Vector3 to)
        {
            var uprising = UprisingAt(slot);
            if (uprising != null) { uprising.ReplayNetChain(from, to); EffectsReplayed++; }
        }

        private static bool IsUnset(Quaternion q)
        {
            return q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f;
        }

        private static float LerpRadians(float a, float b, float t)
        {
            return Mathf.LerpAngle(a * Mathf.Rad2Deg, b * Mathf.Rad2Deg, t) * Mathf.Deg2Rad;
        }

        /// <summary>Host: a hit with presentation (bounce / penetration) for the proxies.</summary>
        private void OnDamageTaken(BattleTank tank, DamageInfo info)
        {
            var session = Session;
            if (Object == null || !HasStateAuthority || session == null || info == null || !info.ShouldHaveMarker()) { return; }
            var victim = session.SlotOfTank(tank);
            if (victim < 0 || victim >= BattleNetPlan.MaxSlots) { return; }
            var attacker = session.SlotOfTank(info.AttackerTank as BattleTank);
            RPC_Hit((byte)victim, (byte)(attacker >= 0 && attacker < BattleNetPlan.MaxSlots ? attacker : 255),
                Mathf.Max(0, info.Dmg), Mathf.Max(0, info.DmgBase), (byte)info.DmgType, (byte)info.Shell,
                new Vector3(info.Pos.X, info.Pos.Y, info.Pos.Z), info.ShotId);
        }

        /// <summary>Host -> clients: the host's damage model resolved a hit (sound only, never health).</summary>
        [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies)]
        public void RPC_Hit(byte victim, byte attacker, int dmg, int dmgBase, byte type, byte shell, Vector3 position, int shotId)
        {
            var session = Session;
            if (session == null || victim >= session.SlotTanks.Count) { return; }
            if (dmg < 0 || dmgBase < 0 || type > (byte)DamageType.Drowning || shell > (byte)ShellType.Hesh) { return; }
            var tank = session.SlotTanks[victim];
            if (tank == null) { return; }
            var source = attacker < session.SlotTanks.Count ? session.SlotTanks[attacker] : null;
            tank.ApplyNetDamageFeedback(new DamageInfo
            {
                AttackerTank = source, Dmg = dmg, DmgBase = dmgBase, DmgType = (DamageType)type, Shell = (ShellType)shell,
                ShotId = shotId, Pos = new Vec3(position.x, position.y, position.z),
            });
        }

        /// <summary>Host -> clients: replay a real round on the slot's puppet.</summary>
        [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies, Channel = RpcChannel.Unreliable)]
        public void RPC_Shot(byte slot, byte shellId, Vector3 origin, Vector3 muzzle, Vector3 direction, int shotId, NetworkBool railgun)
        {
            var session = Session;
            if (session == null || slot >= session.SlotTanks.Count) { return; }
            var tank = session.SlotTanks[slot];
            if (tank != null) { tank.NetVisualShot(shellId, origin, muzzle, direction, shotId, railgun); }
        }

        /// <summary>Host -> clients: the host's roster decided the battle.</summary>
        [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies)]
        public void RPC_BattleEnd(int winnerTeam)
        {
            var session = Session;
            if (session != null)
            {
                Debug.Log("[BlitzNet] host ended the battle, winner team " + winnerTeam);
                if (NetSession.Current != null) { NetSession.Current.MarkEnded(); }
                session.ForceFinish(winnerTeam);
            }
        }

        /// <summary>Host -> clients: the countdown is over, the battle is live.</summary>
        [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies)]
        public void RPC_BattleStart()
        {
            NetSession.NotifyHostStarted();
        }

        // ------------------------------------------------------------ chat

        /// <summary>Host: one spam guard per plan slot, the authoritative mute.</summary>
        private readonly Dictionary<int, ChatSpamGuard> _chatGuards = new Dictionary<int, ChatSpamGuard>();

        /// <summary>Local player -> network (the view already showed the line locally).</summary>
        private void SendChat(ChatChannel channel, string text)
        {
            if (Object == null || Runner == null) { return; }
            if (HasStateAuthority)
            {
                var session = Session;
                if (session == null) { return; }
                HostRelay(session.SlotOfTank(session.Player), channel, text, Runner.LocalPlayer);
            }
            else
            {
                RPC_ChatSend((byte)channel, ChatSanitizer.CleanText(text));
            }
        }

        /// <summary>Client -> host: a chat line. The host resolves the sender from the RPC source, never from the payload.</summary>
        [Rpc(RpcSources.Proxies, RpcTargets.StateAuthority)]
        public void RPC_ChatSend(byte channel, string text, RpcInfo info = default)
        {
            if (channel > (byte)ChatChannel.All) { return; }
            var session = Session;
            var plan = session != null ? session.NetPlan : null;
            if (plan == null) { return; }
            var slot = plan.SlotOf(info.Source.PlayerId);
            if (slot < 0) { return; }
            ChatSpamGuard guard;
            if (!_chatGuards.TryGetValue(slot, out guard)) { guard = new ChatSpamGuard(); _chatGuards[slot] = guard; }
            // Shadow mute: the spammer keeps seeing their own lines, nobody else does.
            if (!guard.Accept(Time.unscaledTimeAsDouble)) { return; }
            HostRelay(slot, (ChatChannel)channel, text, info.Source);
        }

        private void HostRelay(int slot, ChatChannel channel, string raw, PlayerRef source)
        {
            var session = Session;
            var plan = session != null ? session.NetPlan : null;
            if (plan == null || slot < 0 || slot >= plan.Slots.Count || slot >= session.SlotTanks.Count) { return; }
            var text = ChatSanitizer.CleanText(raw);
            if (text.Length == 0) { return; }
            var senderTeam = plan.Slots[slot].Team;
            var view = BattleChatView.Current;
            if (source != Runner.LocalPlayer && view != null) { view.Receive(session.SlotTanks[slot], channel, text); }
            foreach (var player in Runner.ActivePlayers)
            {
                if (player == Runner.LocalPlayer || player == source) { continue; }
                var readerSlot = plan.SlotOf(player.PlayerId);
                if (readerSlot < 0 || !ChatColors.Receives(channel, senderTeam, plan.Slots[readerSlot].Team)) { continue; }
                RPC_ChatDeliver(player, (byte)slot, (byte)channel, text);
            }
        }

        /// <summary>Host -> one recipient: a chat line it may read (team lines never reach the enemy).</summary>
        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        public void RPC_ChatDeliver([RpcTarget] PlayerRef target, byte slot, byte channel, string text)
        {
            if (channel > (byte)ChatChannel.All) { return; }
            var session = Session;
            var view = BattleChatView.Current;
            if (session == null || view == null || slot >= session.SlotTanks.Count) { return; }
            // Receiver-side: the view sanitizes again and re-checks the channel.
            view.Receive(session.SlotTanks[slot], (ChatChannel)channel, text);
        }

        /// <summary>Host: broadcast a yellow system notice about the tank in <paramref name="slot"/>.</summary>
        public void RaiseNotice(ChatNotice kind, int slot)
        {
            if (Object == null || !HasStateAuthority || slot < 0 || slot >= BattleNetPlan.MaxSlots) { return; }
            var session = Session;
            var blue = session != null && slot < session.SlotTanks.Count && session.SlotTanks[slot] != null
                && session.SlotTanks[slot].TeamKiller;
            RPC_ChatNotice((byte)kind, (byte)slot, blue);
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        public void RPC_ChatNotice(byte kind, byte slot, NetworkBool blue)
        {
            var session = Session;
            var view = BattleChatView.Current;
            if (session == null || view == null || slot >= session.SlotTanks.Count) { return; }
            if (kind != (byte)ChatNotice.TeamKillKick && kind != (byte)ChatNotice.TurnedBlue) { return; }
            view.Notice((ChatNotice)kind, session.SlotTanks[slot], blue);
        }
    }
}
