using System;
using System.Globalization;
using System.Text;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;

namespace BlitzLegacy.Direct
{
    /// <summary>
    /// Automated test harness only, never a player feature: inert unless the
    /// environment sets BLITZ_NET_SMOKE=1 (see Tools/direct_smoke.sh).
    ///
    ///   -blitzDirect -blitzNetSmoke &lt;name&gt; [-blitzNetTank id] [-blitzNetMode normal|gravity|uprising|bossmode]
    ///   [-blitzNetAutoDrive] [-blitzNetQuitAfter seconds]
    ///
    /// logs in to (or creates) the named account, picks the own-server
    /// online mode in the hangar and queues exactly like a player.
    /// </summary>
    public sealed class DirectCli : MonoBehaviour
    {
        private string _name;
        private string _tank;
        private BattleMode? _mode;
        private bool _autoDrive;
        private float _quitAfter = -1f;
        private float _leaveAfter = -1f;
        private bool _queued;
        private float _nextReport;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Environment.GetEnvironmentVariable("BLITZ_NET_SMOKE") != "1") { return; }
            var args = Environment.GetCommandLineArgs();
            if (Array.IndexOf(args, "-blitzDirect") < 0) { return; }
            var cli = new GameObject("DirectCli").AddComponent<DirectCli>();
            DontDestroyOnLoad(cli.gameObject);
            for (var i = 0; i < args.Length; i++)
            {
                var next = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "-blitzNetSmoke": cli._name = next; i++; break;
                    case "-blitzNetTank": cli._tank = next; i++; break;
                    case "-blitzNetAutoDrive": cli._autoDrive = true; break;
                    case "-blitzNetQuitAfter":
                        float seconds;
                        if (float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) { cli._quitAfter = seconds; }
                        i++;
                        break;
                    case "-blitzNetLeaveAfter":
                        // Leaves the queue through its back button, like a player.
                        float leave;
                        if (float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out leave)) { cli._leaveAfter = leave; }
                        i++;
                        break;
                    case "-blitzNetMode":
                        foreach (var mode in BattleModeCatalog.SelectorModes)
                        {
                            if (string.Equals(BattleModeCatalog.Id(mode), next, StringComparison.OrdinalIgnoreCase)) { cli._mode = mode; }
                        }
                        i++;
                        break;
                }
            }
            if (string.IsNullOrEmpty(cli._name)) { cli._name = "directsmoke"; }
        }

        private void Update()
        {
            if (_quitAfter > 0f && Time.realtimeSinceStartup >= _quitAfter)
            {
                Debug.Log("[BlitzDirect] smoke: quit after " + _quitAfter + "s");
                _quitAfter = -1f;
                if (DirectClient.Current != null) { DirectClient.Current.Shutdown(); }
                Application.Quit(0);
                return;
            }
            DirectClient.AutoDrive = _autoDrive;
            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap == null) { return; }
            if (bootstrap.Hangar == null && bootstrap.Battle == null)
            {
                if (!_queued) { DirectWorld.AutoLogin(bootstrap, _name, _tank); }
                return;
            }
            if (!_queued && bootstrap.Hangar != null)
            {
                _queued = true;
                bootstrap.Settings.OnlineServer = SettingsData.OnlineServerDirect;
                bootstrap.OnlineSelected = true;
                if (_mode.HasValue) { bootstrap.SelectedMode = _mode.Value; }
                Debug.Log("[BlitzDirect] smoke: online queue through the hangar");
                bootstrap.EnterBattle(bootstrap.Hangar);
            }
            if (_leaveAfter > 0f && Time.realtimeSinceStartup >= _leaveAfter && bootstrap.Queue != null)
            {
                _leaveAfter = -1f;
                Debug.Log("[BlitzDirect] smoke: leaving the queue");
                bootstrap.Queue.Cancel();
            }
            if (Time.realtimeSinceStartup >= _nextReport)
            {
                _nextReport = Time.realtimeSinceStartup + 2f;
                Report(bootstrap);
            }
        }

        /// <summary>Smoke log: where every human tank is, as this peer sees it.</summary>
        private static void Report(BlitzBootstrap bootstrap)
        {
            var battle = bootstrap.Battle;
            if (battle == null || battle.NetPlan == null) { return; }
            var plan = battle.NetPlan;
            var line = new StringBuilder("[BlitzDirect] smoke state:");
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
                    .Append(tank == battle.Player ? " own" : string.Empty)
                    .Append(tank.NetPuppet ? " puppet" : " sim");
            }
            var ping = BlitzLegacy.Runtime.Battle.BattleNetHooks.PingMs != null ? BlitzLegacy.Runtime.Battle.BattleNetHooks.PingMs() : -1;
            line.Append(" mode=").Append(battle.Mode).Append(" live=").Append(battle.Started)
                .Append(" ping=").Append(ping)
                .Append(" banner=").Append(NetStatusBanner.CurrentText ?? "-");
            if (battle.BigBoss != null)
            {
                for (var i = 0; i < plan.Slots.Count && i < battle.SlotTanks.Count; i++)
                {
                    var tank = battle.SlotTanks[i];
                    if (tank == null || tank.BigBoss == null || !plan.Slots[i].IsHuman) { continue; }
                    line.Append(" boss").Append(i).Append('=').Append(tank.BigBoss.Describe());
                }
                line.Append(" tickets=").Append(battle.BigBoss.Match.TeamA).Append(':').Append(battle.BigBoss.Match.TeamB);
            }
            if (DirectClient.Current != null) { line.Append(' ').Append(DirectClient.Current.TakeStats()); }
            Debug.Log(line.ToString());
        }
    }
}
