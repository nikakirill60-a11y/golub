using System;
using System.Globalization;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;

namespace BlitzLegacy.Network
{
    /// <summary>
    /// Automated test harness only, never a player feature: it is inert unless
    /// the environment sets BLITZ_NET_SMOKE=1 (see Tools/net_smoke.sh).
    ///
    ///   -blitzNetHost &lt;room&gt; [-blitzNetGuests N] [-blitzNetLobby seconds]
    ///   -blitzNetJoin &lt;room&gt;
    ///   -blitzNetOnline [-blitzNetLobby seconds]   (the hangar's Online mode, through the UI)
    ///
    /// Smoke switches: -blitzNetSmoke &lt;name&gt; logs in (creating the account
    /// on a clean profile), -blitzNetAutoDrive drives the client tank and
    /// -blitzNetQuitAfter seconds leaves gracefully and quits once the run is
    /// long enough, -blitzNetDropAt seconds cuts a client's connection the
    /// way a network failure would, -blitzNetTank id puts the account in
    /// another tank (matchmaking tier tests), -blitzNetMode normal|gravity|uprising
    /// picks the online battle mode and -blitzNetUseActive casts the Uprising
    /// active ability whenever it is ready; on the host -blitzNetKillHumansAt
    /// seconds destroys every remote human tank once (respawn replication test).
    /// -blitzNetArty selects the tank's ATGM and holds the overhead arty view
    /// (lob replication test); every peer logs where each rocket ended.
    /// </summary>
    public sealed class NetCli : MonoBehaviour
    {
        private string _hostRoom;
        private string _joinRoom;
        private int _guests = 1;
        private float _lobby = 60f;
        private string _smokeName;
        private string _tank;
        private bool _autoDrive;
        private float _quitAfter = -1f;
        private float _dropAt = -1f;
        private bool _online;
        private bool _useActive;
        private bool _arty;
        private readonly System.Collections.Generic.Dictionary<int, Vector3> _rockets =
            new System.Collections.Generic.Dictionary<int, Vector3>();
        private readonly System.Collections.Generic.HashSet<int> _rocketSeen = new System.Collections.Generic.HashSet<int>();
        private float _killHumansAt = -1f;
        private float _liveSince = -1f;
        private BlitzLegacy.Core.Battle.BattleMode? _mode;
        private bool _started;
        private float _nextReport;
        private float _nextActive;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Environment.GetEnvironmentVariable("BLITZ_NET_SMOKE") != "1") { return; }
            var args = Environment.GetCommandLineArgs();
            var cli = Parse(args);
            if (cli == null) { return; }
            var go = new GameObject("NetCli");
            DontDestroyOnLoad(go);
            var component = go.AddComponent<NetCli>();
            component.CopyFrom(cli);
        }

        private sealed class Options
        {
            public string Host, Join, Smoke, Tank;
            public int Guests = 1;
            public float Lobby = -1f, QuitAfter = -1f, DropAt = -1f;
            public bool AutoDrive, Online, UseActive, Arty;
            public float KillHumansAt = -1f;
            public BlitzLegacy.Core.Battle.BattleMode? Mode;
        }

        private static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var next = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "-blitzNetHost": o.Host = next; i++; break;
                    case "-blitzNetJoin": o.Join = next; i++; break;
                    case "-blitzNetGuests": o.Guests = ParseInt(next, 1); i++; break;
                    case "-blitzNetLobby": o.Lobby = ParseFloat(next, -1f); i++; break;
                    case "-blitzNetDropAt": o.DropAt = ParseFloat(next, -1f); i++; break;
                    case "-blitzNetOnline": o.Online = true; break;
                    case "-blitzNetTank": o.Tank = next; i++; break;
                    case "-blitzNetSmoke": o.Smoke = next; i++; break;
                    case "-blitzNetQuitAfter": o.QuitAfter = ParseFloat(next, -1f); i++; break;
                    case "-blitzNetAutoDrive": o.AutoDrive = true; break;
                    case "-blitzNetUseActive": o.UseActive = true; break;
                    case "-blitzNetArty": o.Arty = true; break;
                    case "-blitzNetKillHumansAt": o.KillHumansAt = ParseFloat(next, -1f); i++; break;
                    case "-blitzNetMode": o.Mode = ParseMode(next); i++; break;
                }
            }
            return string.IsNullOrEmpty(o.Host) && string.IsNullOrEmpty(o.Join) && !o.Online ? null : o;
        }

        private void CopyFrom(Options o)
        {
            _hostRoom = o.Host;
            _joinRoom = o.Join;
            _guests = o.Guests;
            _lobby = o.Lobby > 0f ? o.Lobby : 60f;
            _online = o.Online;
            _dropAt = o.DropAt;
            if (o.Online && o.Lobby > 0f) { NetSession.LobbySecondsOverride = o.Lobby; }
            _smokeName = o.Smoke;
            _tank = o.Tank;
            _autoDrive = o.AutoDrive;
            _quitAfter = o.QuitAfter;
            _useActive = o.UseActive;
            _arty = o.Arty;
            _killHumansAt = o.KillHumansAt;
            _mode = o.Mode;
        }

        private static BlitzLegacy.Core.Battle.BattleMode? ParseMode(string value)
        {
            foreach (var mode in BlitzLegacy.Core.Battle.BattleModeCatalog.SelectorModes)
            {
                if (string.Equals(BlitzLegacy.Core.Battle.BattleModeCatalog.Id(mode), value, StringComparison.OrdinalIgnoreCase)) { return mode; }
            }
            return null;
        }

        private static int ParseInt(string value, int fallback)
        {
            int result;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        private static float ParseFloat(string value, float fallback)
        {
            float result;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        private void Update()
        {
            if (_quitAfter > 0f && Time.realtimeSinceStartup >= _quitAfter)
            {
                Debug.Log("[BlitzNet] smoke: quit after " + _quitAfter + "s");
                _quitAfter = -1f;
                if (NetSession.Current != null) { NetSession.Current.Shutdown(); }
                Application.Quit(0);
                return;
            }

            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap == null) { return; }
            if (bootstrap.Hangar == null && bootstrap.Battle == null)
            {
                if (!string.IsNullOrEmpty(_smokeName)) { SmokeLogin(bootstrap); }
                return;
            }

            if (!_started)
            {
                _started = true;
                if (_online)
                {
                    // Same path as a player: Mode -> Online, then Battle!
                    Debug.Log("[BlitzNet] smoke: online queue through the hangar");
                    bootstrap.OnlineSelected = true;
                    if (_mode.HasValue) { bootstrap.SelectedMode = _mode.Value; }
                    bootstrap.EnterBattle(bootstrap.Hangar);
                }
                else
                {
                    var created = !string.IsNullOrEmpty(_hostRoom)
                        ? NetSession.Host(_hostRoom, _guests, _lobby)
                        : NetSession.Join(_joinRoom);
                    created.Failed += reason => Debug.LogError("[BlitzNet] smoke: session failed: " + reason);
                }
            }

            var session = NetSession.Current;
            if (session != null) { session.AutoDrive = _autoDrive; }
            if (_killHumansAt > 0f && bootstrap.Battle != null && bootstrap.Battle.Started && session != null && session.IsHost)
            {
                if (_liveSince < 0f) { _liveSince = Time.realtimeSinceStartup; }
                if (Time.realtimeSinceStartup - _liveSince >= _killHumansAt)
                {
                    _killHumansAt = -1f;
                    var battle = bootstrap.Battle;
                    for (var i = 0; i < battle.NetPlan.Slots.Count && i < battle.SlotTanks.Count; i++)
                    {
                        var tank = battle.SlotTanks[i];
                        if (!battle.NetPlan.Slots[i].IsHuman || tank == null || tank == battle.Player || tank.Dead) { continue; }
                        Debug.Log("[BlitzNet] smoke: destroying human slot " + i);
                        tank.Health.ReceiveDamageBasic(tank.Health.Health);
                    }
                }
            }
            if (_useActive && bootstrap.Battle != null && bootstrap.Battle.Started && bootstrap.Battle.Player != null)
            {
                var uprising = bootstrap.Battle.Player.GetComponent<BlitzLegacy.Runtime.Battle.UprisingTank>();
                if (uprising != null && uprising.State != null && !bootstrap.Battle.Player.Dead
                    && uprising.State.ReadyAt <= Time.time && !uprising.State.Running(Time.time)
                    && Time.realtimeSinceStartup >= _nextActive)
                {
                    _nextActive = Time.realtimeSinceStartup + 3f;
                    Debug.Log("[BlitzNet] smoke: use active " + uprising.State.ActiveId);
                    uprising.UseActive();
                }
            }
            if (_arty && bootstrap.Battle != null && bootstrap.Battle.Started) { HoldArty(bootstrap.Battle); }
            if (!string.IsNullOrEmpty(_smokeName)) { TrackRockets(); }
            if (_dropAt > 0f && Time.realtimeSinceStartup >= _dropAt && session != null &&
                bootstrap.Battle != null && bootstrap.Battle.Started)
            {
                _dropAt = -1f;
                session.SimulateConnectionLoss();
            }

            if (!string.IsNullOrEmpty(_smokeName) && Time.realtimeSinceStartup >= _nextReport)
            {
                _nextReport = Time.realtimeSinceStartup + 2f;
                Report(bootstrap);
            }
        }

        private static void HoldArty(BlitzLegacy.Runtime.Battle.BattleSession battle)
        {
            var tank = battle.Player;
            if (tank == null || tank.Dead || tank.GunInfo == null || battle.View == null) { return; }
            var current = tank.CurrentShell();
            if (current == null || !current.Atgm)
            {
                for (var i = 0; i < tank.GunInfo.Shells.Count; i++)
                {
                    if (tank.GunInfo.Shells[i].Atgm) { tank.SelectShell(i); break; }
                }
            }
            if (!battle.View.ArtyMode && battle.View.ArtyAvailable)
            {
                Debug.Log("[BlitzNet] smoke: arty view on");
                battle.View.SetArtyMode(true);
            }
        }

        /// <summary>Logs where every rocket this peer flew (real or replayed) ended.</summary>
        private void TrackRockets()
        {
            var live = BlitzLegacy.Runtime.Battle.BattleProjectile.LiveGuided;
            _rocketSeen.Clear();
            for (var i = 0; i < live.Count; i++)
            {
                var rocket = live[i];
                if (rocket == null || !rocket.Alive) { continue; }
                var id = rocket.HostShotId > 0 ? rocket.HostShotId : rocket.ShotId;
                _rocketSeen.Add(id);
                _rockets[id] = rocket.transform.position;
            }
            var ended = new System.Collections.Generic.List<int>();
            foreach (var pair in _rockets) { if (!_rocketSeen.Contains(pair.Key)) { ended.Add(pair.Key); } }
            for (var i = 0; i < ended.Count; i++)
            {
                var p = _rockets[ended[i]];
                _rockets.Remove(ended[i]);
                Debug.Log("[BlitzNet] smoke: rocket " + ended[i] + " ended at "
                    + p.x.ToString("F1", CultureInfo.InvariantCulture) + ","
                    + p.y.ToString("F1", CultureInfo.InvariantCulture) + ","
                    + p.z.ToString("F1", CultureInfo.InvariantCulture));
            }
        }

        private void SmokeLogin(BlitzBootstrap bootstrap)
        {
            var login = FindAnyObjectByType<LoginScreen>();
            if (login == null) { return; }
            var accounts = bootstrap.Accounts;
            if (accounts.GetCurrentAccount() == null || accounts.GetCurrentAccount().Name != _smokeName)
            {
                AccountData_Ensure(bootstrap);
            }
            accounts.LogInToCurrentAccount(bootstrap.Clock.NowUnixSeconds);
            var account = accounts.GetCurrentAccount();
            if (account != null && !string.IsNullOrEmpty(_tank))
            {
                if (account.GetTank(_tank) == null) { account.AddTank(_tank); }
                account.SelectedTankName = _tank;
                Debug.Log("[BlitzNet] smoke: tank " + _tank);
            }
            bootstrap.Persist();
            bootstrap.EnterHangar(login);
        }

        private void AccountData_Ensure(BlitzBootstrap bootstrap)
        {
            var accounts = bootstrap.Accounts;
            foreach (var pair in accounts.Accounts)
            {
                if (pair.Value.Name == _smokeName)
                {
                    accounts.SwitchAccount(pair.Key);
                    return;
                }
            }
            var created = accounts.CreateAccount(_smokeName, _smokeName, Guid.NewGuid().ToString("N"),
                bootstrap.Clock.NowUnixSeconds);
            if (created.Ok) { accounts.SwitchAccount(created.Account.Key); }
        }

        /// <summary>Smoke log: where every human tank is, as this peer sees it.</summary>
        private static void Report(BlitzBootstrap bootstrap)
        {
            var battle = bootstrap.Battle;
            if (battle == null || battle.NetPlan == null) { return; }
            var plan = battle.NetPlan;
            var line = new System.Text.StringBuilder("[BlitzNet] smoke state:");
            for (var i = 0; i < plan.Slots.Count && i < battle.SlotTanks.Count; i++)
            {
                if (!plan.Slots[i].IsHuman) { continue; }
                var tank = battle.SlotTanks[i];
                if (tank == null) { continue; }
                var p = tank.transform.position;
                line.Append(" slot").Append(i).Append("(owner ").Append(plan.Slots[i].Owner).Append(")=")
                    .Append(p.x.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                    .Append(p.z.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(" hp=").Append(tank.Health != null ? tank.Health.Health : -1)
                    .Append(" yaw=").Append(tank.Turret != null ? tank.Turret.Yaw.ToString("F2", CultureInfo.InvariantCulture) : "-")
                    .Append(" pitch=").Append(tank.Turret != null ? tank.Turret.Pitch.ToString("F3", CultureInfo.InvariantCulture) : "-")
                    .Append(tank.Turret != null && tank.Turret.LobAim ? " lob" : "")
                    .Append(" disp=").Append(tank.Dispersion.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(tank.NetPuppet ? " puppet" : " sim")
                    .Append(" v=").Append(tank.Suspension != null ? tank.Suspension.SpeedMs.ToString("F1", CultureInfo.InvariantCulture) : "-")
                    .Append(" trk=").Append(tank.TrackScroll.ToString("F2", CultureInfo.InvariantCulture));
            }
            if (battle.BigBoss != null)
            {
                for (var i = 0; i < plan.Slots.Count && i < battle.SlotTanks.Count; i++)
                {
                    var tank = battle.SlotTanks[i];
                    if (tank == null || tank.BigBoss == null || !plan.Slots[i].IsHuman) { continue; }
                    line.Append(" boss").Append(i).Append("=").Append(tank.BigBoss.Describe());
                }
                line.Append(" tickets=").Append(battle.BigBoss.Match.TeamA).Append(':').Append(battle.BigBoss.Match.TeamB);
            }
            if (battle.Mode == BlitzLegacy.Core.Battle.BattleMode.Uprising)
            {
                for (var i = 0; i < plan.Slots.Count && i < battle.SlotTanks.Count; i++)
                {
                    var tank = battle.SlotTanks[i];
                    var u = tank != null ? tank.GetComponent<BlitzLegacy.Runtime.Battle.UprisingTank>() : null;
                    if (u == null || u.State == null || !plan.Slots[i].IsHuman) { continue; }
                    var s = u.State;
                    line.Append(" up").Append(i).Append("=").Append(s.ActiveId)
                        .Append(" el=").Append(s.Elixir).Append(" lives=").Append(s.RespawnsLeft)
                        .Append(" life=").Append(s.LifeSerial).Append(s.Respawning ? " respawning" : "")
                        .Append(" run=").Append(s.Running(Time.time))
                        .Append(" ready=").Append(Mathf.Max(0f, (float)(s.ReadyAt - Time.time)).ToString("F1", CultureInfo.InvariantCulture))
                        .Append(" owned=").Append(s.Owned.Count).Append(" offer=").Append(s.Offer.Count);
                }
            }
            line.Append(" fx=").Append(NetBattle.EffectsReplayed);
            line.Append(" mode=").Append(battle.Mode).Append(" live=").Append(battle.Started)
                .Append(" host=").Append(NetSession.Current != null && NetSession.Current.IsHost)
                .Append(" banner=").Append(NetStatusBanner.CurrentText ?? "-");
            Debug.Log(line.ToString());
        }
    }
}
