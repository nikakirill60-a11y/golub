using System.Collections;
using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Sandbox;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Mods;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Battle.Ai;
using BlitzLegacy.Runtime.Lighting;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Code-first matchmade session. Direct callers retain auto-start; the
    /// complete ported flow builds it paused, presents the map roster/countdown,
    /// then calls StartBattle explicitly.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class BattleSession : MonoBehaviour
    {
        public const string DefaultTankId = "ms-1";
        public const int TeamSize = 2;
        public const float UnfairSkillChance = 80f;

        private readonly List<BattleTank> _allies = new List<BattleTank>();
        private readonly List<BattleTank> _enemies = new List<BattleTank>();
        private readonly List<AiTankController> _bots = new List<AiTankController>();
        private readonly List<BattleParticipantStats> _participants =
            new List<BattleParticipantStats>();
        private readonly Dictionary<BattleTank, BattleParticipantStats> _stats =
            new Dictionary<BattleTank, BattleParticipantStats>();
        private readonly Dictionary<BattleTank, BattleTank> _lastAttacker =
            new Dictionary<BattleTank, BattleTank>();
        private readonly Dictionary<BattleTank, Action<DamageInfo>> _damageHandlers =
            new Dictionary<BattleTank, Action<DamageInfo>>();
        private readonly Dictionary<BattleTank, Action<bool, int>> _shotHandlers =
            new Dictionary<BattleTank, Action<bool, int>>();
        private bool _finishedPublished;
        private TankSoundManager _playerSounds;
        /// <summary>The view pose this battle lends the audio listener.</summary>
        private AudioListenerRig.PoseSource _hearing;

        /// <summary>Effective rule set: Normal, Gravity or Uprising (never Sandbox).</summary>
        public BattleMode Mode { get; private set; }
        public BigBossBattle BigBoss { get; private set; }
        /// <summary>Big Boss tables of this battle, loaded before the roster needs its clock.</summary>
        private BigBossRules _bigBossRules;

        /// <summary>The offline Sandbox around <see cref="Mode"/>, or null.</summary>
        public SandboxBattle Sandbox { get; private set; }

        /// <summary>The War of Ducks event rules (offline only), or null.</summary>
        public WorldOfDucksRules Ducks { get; private set; }

        /// <summary>seed of _setup_world_of_ducks_battle: picks every duck's gun.</summary>
        public long DucksSeed { get; private set; }

        /// <summary>Uprising economy of this battle: the Sandbox preset's or the shipped one.</summary>
        public UprisingRules UprisingRules
        {
            get { return Sandbox != null ? Sandbox.UprisingRules : UprisingRules.Standard; }
        }
        public BattleWorld World { get; private set; }
        public BattleTank Player { get; private set; }
        public BattleTank Ally { get; private set; }
        public BattleTank Bot { get; private set; }
        public BattleCamera View { get; private set; }
        public BattleControls Controls { get; private set; }
        public BattleHud Hud { get; private set; }

        /// <summary>Battle chat feed and input under the header.</summary>
        public BattleChatView Chat { get; private set; }

        /// <summary>menu_bg, the escape menu the "menu" action toggles.</summary>
        public BattlePauseMenu PauseMenu { get; private set; }

        /// <summary>ui_settings.tscn, while it is up over the battle.</summary>
        public SettingsScreen SettingsUi { get; private set; }

        /// <summary>team_ally and team_enemy, the sliding player lists.</summary>
        public BattleScoreboard Scoreboard { get; private set; }

        /// <summary>minimap_handle; null when the map has no minimap.</summary>
        public BattleMinimap Minimap { get; private set; }

        public IEnumerable<BattleTank> BattleTanks()
        {
            if (Roster == null) { yield break; }
            for (var i = 0; i < Roster.TanksA.Count; i++) { yield return Roster.TanksA[i]; }
            for (var i = 0; i < Roster.TanksB.Count; i++) { yield return Roster.TanksB[i]; }
        }
        public BattleRoster Roster { get; private set; }
        public MatchmakerRoster MatchRoster { get; private set; }

        /// <summary>The local tank's TankSoundsManager, when audio is up.</summary>
        public TankSoundManager PlayerSounds { get { return _playerSounds; } }
        public List<BattleTank> Allies { get { return _allies; } }
        public List<BattleTank> Enemies { get { return _enemies; } }
        public List<AiTankController> Bots { get { return _bots; } }
        public IReadOnlyList<BattleParticipantStats> Participants { get { return _participants; } }
        public bool Started { get { return Roster != null && Roster.IsOngoing; } }

        public event Action<BattleSession> Finished;

        public static BattleSession Create(
            Transform parent,
            bool mobile,
            string mapId = BattleWorld.DefaultMapId,
            string tankId = DefaultTankId,
            bool autoStart = true,
            string playerName = null,
            TankSaveData playerTank = null,
            BattleMode mode = BattleMode.Normal)
        {
            BattleSession result = null;
            var steps = CreateSteps(
                parent, mobile, built => result = built,
                mapId, tankId, autoStart, playerName, playerTank, false, mode);
            while (steps.MoveNext())
            {
            }
            return result;
        }

        /// <summary>
        /// The same construction, spread over frames, so the battle queue no
        /// longer blocks the main thread long enough for Android to raise an
        /// ANR (REMASTERDELUXE-2). Callers that drain every step inside one
        /// frame must pass allowAsync false.
        /// </summary>
        public static IEnumerator CreateSteps(
            Transform parent,
            bool mobile,
            Action<BattleSession> built,
            string mapId = BattleWorld.DefaultMapId,
            string tankId = DefaultTankId,
            bool autoStart = true,
            string playerName = null,
            TankSaveData playerTank = null,
            bool allowAsync = true,
            BattleMode mode = BattleMode.Normal,
            BattleNetSetup net = null,
            SandboxBattle sandbox = null)
        {
            // Hard Sandbox ban: the offline-only rules never get a network seat.
            if (net != null && (sandbox != null || BattleModeCatalog.OfflineOnly(mode)))
            {
                throw new SandboxOnlineException("battle");
            }
            if (mode == BattleMode.Sandbox && sandbox == null)
            {
                throw new InvalidOperationException("A Sandbox battle needs its preset");
            }
            if (sandbox != null) { mode = sandbox.BaseMode; }
            WorldOfDucksRules ducks = null;
            if (mode == BattleMode.WorldOfDucks)
            {
                // The event brings its own map and WarDuck card; the garage
                // tank and its modules never enter the pond.
                ducks = WorldOfDucksRules.Parse(BlitzResources.LoadText(WorldOfDucksRules.DataResource));
                mapId = ducks.MapId;
                playerTank = null;
                mode = BattleMode.Normal;
            }
            if (net != null && net.IsClient && net.Plan != null)
            {
                // A client seats exactly what the host decided.
                var own = net.Plan.SlotOf(net.LocalOwner);
                if (own < 0)
                {
                    throw new InvalidOperationException("The host plan has no seat for this player");
                }
                mapId = net.Plan.MapId;
                mode = net.Plan.Mode;
                tankId = net.Plan.Slots[own].TankId;
            }
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            if (net != null)
            {
                // Hard mod tank ban: a networked battle knows the stock tanks
                // only, and the player's own tank must be one of them.
                ModTankOnlineBan.Ensure(string.IsNullOrEmpty(tankId) ? DefaultTankId : tankId, "battle");
            }
            else
            {
                ModRuntime.Apply(database);
            }
            TankInfo card;
            if (ducks != null)
            {
                card = ducks.CreateCard();
                database.Add(card);
            }
            else
            {
                card = database.Get(string.IsNullOrEmpty(tankId) ? DefaultTankId : tankId);
            }
            if (card == null)
            {
                throw new InvalidOperationException("The battle tank card is missing: " + tankId);
            }
            // The Sandbox plays BIG BOSS on any tier.
            if (mode == BattleMode.BigBoss && sandbox == null && !BigBossRules.EligibleTier(card.Level))
                throw new ArgumentException("Big Boss requires a tier V-X vehicle.");
            var host = new GameObject("BattleSession");
            if (parent != null)
            {
                host.transform.SetParent(parent, false);
            }
            var session = host.AddComponent<BattleSession>();
            session.Mode = mode;
            session.Net = net;
            session.Ducks = ducks;
            if (net != null) { SandboxNetGuard.Attach(session); }
            if (sandbox != null)
            {
                session.Sandbox = sandbox;
                sandbox.Activate(session);
            }
            var steps = session.BuildSteps(
                database, card, mobile, mapId, autoStart, playerName,
                playerTank, allowAsync);
            while (steps.MoveNext())
            {
                yield return steps.Current;
            }
            if (built != null)
            {
                built(session);
            }
        }

        private IEnumerator BuildSteps(
            TanksDatabase database, TankInfo card, bool mobile, string mapId,
            bool autoStart, string playerName, TankSaveData playerTank,
            bool allowAsync)
        {
            var worldSteps = BattleWorld.CreateSteps(
                transform, mapId, built => World = built, allowAsync);
            while (worldSteps.MoveNext())
            {
                yield return worldSteps.Current;
            }
            if (Mode == BattleMode.BigBoss && !Dedicated)
            {
                var fxSteps = DavaFx.PreloadBigBoss(transform);
                while (fxSteps.MoveNext())
                    if (allowAsync) yield return fxSteps.Current;
                // Decode ability audio and build the level-up badge before the first activation.
                BigBossAudio.Preload();
                BigBossLevelUpFx.Preload();
            }
            var mapCapacity = Math.Min(World.PlayerSpawns.Count, World.BotSpawns.Count);
            if (mapCapacity <= 0)
            {
                throw new InvalidOperationException(
                    "The map has no usable TankSpawner pair: " + World.Map.Id);
            }

            var settings = BlitzBootstrap.Current != null
                ? BlitzBootstrap.Current.Settings : new SettingsData();
            // Online battles ignore the personal matchmaker/gameplay rows.
            var rules = Net != null ? SettingsData.StandardRules() : settings;
            var random = new UnityRandomSource();
            if (Sandbox != null)
            {
                PlanSandbox(database, card, random);
            }
            else if (Ducks != null)
            {
                PlanWorldOfDucks(card, mapCapacity, random);
            }
            else if (Net == null || !Net.IsClient)
            {
                MatchRoster = new Matchmaker(database, random).BuildRoster(
                    card, Mode == BattleMode.BigBoss ? 7 : Net != null ? BattleOnline.TeamSize : TeamSizeFor(rules), mapCapacity);
            }
            // A client seats the host's plan: its roster is already clamped. The
            // Sandbox seats its preset as it is.
            if (Mode == BattleMode.BigBoss && Sandbox == null && (Net == null || !Net.IsClient))
            {
                var matchmaker = new Matchmaker(database, random);
                for (var i = 0; i < MatchRoster.TeamSize; i++)
                {
                    var tier = Mathf.Clamp(MatchRoster.SlotTiers[i], 5, 10);
                    if (tier == MatchRoster.SlotTiers[i]) continue;
                    MatchRoster.SlotTiers[i] = tier; MatchRoster.Template[i] = tier - card.Level;
                    MatchRoster.Allies[i] = matchmaker.GetByLevelRandom(tier, true);
                    MatchRoster.Enemies[i] = matchmaker.GetByLevelRandom(tier, true);
                }
            }
            var playerTeam = 0;
            if (Net != null && Net.IsClient && Net.Plan != null)
            {
                playerTeam = Net.Plan.Slots[Net.Plan.SlotOf(Net.LocalOwner)].Team;
            }
            if (Mode == BattleMode.BigBoss)
            {
                var tables = BlitzResources.LoadText(BigBossRules.Resource);
                _bigBossRules = Sandbox != null ? SandboxTuning.BigBoss(Sandbox.Config, tables) : new BigBossRules(tables);
            }
            Roster = BattleRoster.Begin(World.AiMarkers, playerTeam, World.Objects.AiCovers);
            Roster.TimeLimitSeconds = Sandbox != null
                ? Sandbox.TimeLimitSeconds
                : Mode == BattleMode.BigBoss ? _bigBossRules.P("timeLimitSeconds") : Ducks != null ? Ducks.TimeLimitSeconds : BattleRoster.TimeLimitFor(rules);
            // The Sandbox brings its own per-bot HOLD POSITION switch.
            AiTankController.ApplySettings(Sandbox != null || Ducks != null ? null : rules);
            Roster.TankGotDestroyed += OnTankDestroyed;
            Roster.BattleEnded += OnBattleEnded;

            if (Sandbox != null)
            {
                var seats = SeatSandbox(database, card, playerName, playerTank, random);
                while (seats.MoveNext()) { yield return seats.Current; }
            }
            else if (Ducks != null)
            {
                var seats = SeatWorldOfDucks(card, playerName, random);
                while (seats.MoveNext()) { yield return seats.Current; }
            }
            else if (Net != null && Net.IsClient)
            {
                var seats = SeatFromPlan(database, card, playerName, playerTank);
                while (seats.MoveNext()) { yield return seats.Current; }
            }
            else
            {
                var seats = SeatMatchmade(database, card, playerName, playerTank, random);
                while (seats.MoveNext()) { yield return seats.Current; }
            }
            GraphicsQuality.Applied += RepaintCamouflages;

            yield return null;
            View = BattleCamera.Create(Player, mobile, settings);
            View.transform.SetParent(transform, true);
            // Dedicated server: the anchor tank is a remote human's. With the
            // server's idle camera as its View, cam_trace overwrote the remote
            // aim every physics step and that player's turret barely turned.
            if (!Dedicated) { Player.View = View; }
            MountBotSupply();
            // Every mode shows the same battle HUD; the mode only adds its widgets.
            var hud = BuildHud(mobile, settings, autoStart);
            while (hud.MoveNext()) { yield return hud.Current; }

            AttachSounds();
            AttachCombatFx();
            if (Ducks != null) { WorldOfDucks.WorldOfDucksBattle.Create(this); }
            // Roles are dealt once the HUD is built: its Big Boss widgets read them lazily.
            if (Mode == BattleMode.BigBoss)
            {
                BigBoss = BigBossBattle.Create(this, UnityEngine.Random.Range(1, int.MaxValue), _bigBossRules);
            }

            // spot_enemy_tanks() and _on_ram_body_entered() live on every tank
            // in the source; the port drives both from the session so the scan
            // beat and the pending ram pairs stay in one place.
            Spotter = BattleSpotter.Create(transform, Roster, rules);
            Spotter.FirstSpotted += (viewer, target) => {
                var stats = StatsFor(viewer);
                if (stats != null) { stats.Spots++; stats.Interaction(target).Spotted = true; }
            };
            Ram = BattleRam.Create(transform);

            if (autoStart)
            {
                StartBattle();
            }
            else
            {
                Player.Blocked = true;
                Player.TurretBlock = true;
                Hud.SetClockRunning(false);
                Hud.SetTimeLimit(Roster.TimeLimitSeconds);
                Hud.SetBattleTime(0f);
                Hud.SetVisible(false);
                // ui_player.tscn is hidden as a whole behind the map intro,
                // so the thumb stick and the combat buttons must not be on
                // screen either, and neither a finger nor the mouse may turn
                // the camera before the intro is over.
                SetInputActive(false);
            }

            Debug.Log("[Blitz] battle session built: map=" + World.Map.Id
                + " tank=" + Player.Card.Name + " allies=" + _allies.Count
                + " enemies=" + _enemies.Count + " bots=" + _bots.Count
                + " autoStart=" + (autoStart ? "yes" : "no")
                + " mobile=" + (mobile ? "yes" : "no"));
        }

        /// <summary>
        /// tank_sound_manager.gd is a child of every battle tank in the source
        /// scene, and the sniper cues plus the SFX muffle hang off the local
        /// tank's camera the way base_tank.sniper_mode_switched does.
        /// </summary>
        /// <summary>
        /// <c>TankParticlesManager</c> on every tank: the shot effect each gun
        /// restarts when it fires. A dedicated server draws nothing.
        /// </summary>
        private void AttachCombatFx()
        {
            // The ducks bring their own shot, hit and death scenes.
            if (Dedicated || Ducks != null) { return; }

            for (var i = 0; i < _allies.Count; i++)
            {
                CombatFx.BattleTankFx.Attach(_allies[i]);
            }

            for (var i = 0; i < _enemies.Count; i++)
            {
                CombatFx.BattleTankFx.Attach(_enemies[i]);
            }
        }

        private void AttachSounds()
        {
            var audio = BlitzBootstrap.Current != null
                ? BlitzBootstrap.Current.Audio : null;
            if (audio == null) { return; }

            // Every spatial voice is heard from the battle view, not the audio root.
            if (View != null)
            {
                _hearing = View.ListenerPose;
                audio.ListenFrom(_hearing);
            }

            // WarDucks have no engine or tracks: WorldOfDucksTank plays the
            // swim loop, the quacks and the water guns instead.
            for (var i = 0; Ducks == null && i < _allies.Count; i++)
            {
                var tank = _allies[i];
                var manager = TankSoundManager.Attach(tank, audio, tank == Player);
                if (tank == Player) { _playerSounds = manager; }
            }

            for (var i = 0; Ducks == null && i < _enemies.Count; i++)
            {
                TankSoundManager.Attach(_enemies[i], audio, false);
            }

            if (View != null)
            {
                View.SniperModeSwitched += on =>
                {
                    audio.MuffleSfx(on);
                    if (_playerSounds != null) { _playerSounds.SetSniperMode(on); }
                };
            }
        }

        /// <summary>
        /// save_settings "TEAMSIZE" as the slot count the matchmaker fills per
        /// team. The mapping used to sit on the save itself, which left the
        /// saved row one hop away from the battle that spawns the slots.
        /// </summary>
        public static int TeamSizeFor(SettingsData settings)
        {
            if (settings == null)
            {
                return 7;
            }

            switch (settings.MatchmakerTeamSize)
            {
                case SettingsData.TeamSize1x1: return 1;
                case SettingsData.TeamSize2x2: return 2;
                case SettingsData.TeamSize5x5: return 5;
                case SettingsData.TeamSize10x10: return 10;
                default: return 7;
            }
        }

        public void StartBattle()
        {
            if (Roster == null || Roster.IsOngoing || _finishedPublished)
            {
                return;
            }
            Player.Blocked = false;
            Player.TurretBlock = false;
            foreach (var driver in _remoteDrivers.Values)
            {
                var remote = driver != null ? driver.GetComponent<BattleTank>() : null;
                if (remote != null) { remote.Blocked = false; remote.TurretBlock = false; }
            }
            Roster.StartBattle();
            if (BigBoss != null) BigBoss.Begin();
            // _battle.gd plays the crew's "battle start" line as the clock runs.
            if (BlitzBootstrap.Current != null && BlitzBootstrap.Current.Audio != null)
            {
                BlitzBootstrap.Current.Audio.PlayCrewMessage(
                    Core.Audio.CrewMessage.BattleStart);
            }
            if (Hud != null)
            {
                Hud.SetTimeLimit(Roster.TimeLimitSeconds);
                Hud.SetBattleTime(0f);
                Hud.SetClockRunning(true);
            }
            for (var i = 0; i < _bots.Count; i++)
            {
                if (_bots[i] != null)
                {
                    _bots[i].StartBattle();
                }
            }
        }

        public BattleParticipantStats StatsFor(BattleTank tank)
        {
            BattleParticipantStats stats;
            return tank != null && _stats.TryGetValue(tank, out stats) ? stats : null;
        }

        public void ForceFinish(int winnerTeam = -1)
        {
            if (Roster != null)
            {
                Roster.ForceFinish(winnerTeam);
            }
        }

        public void StopBots()
        {
            for (var i = 0; i < _bots.Count; i++)
            {
                var controller = _bots[i];
                if (controller == null)
                {
                    continue;
                }
                controller.Activate(false);
                if (controller.Tank != null)
                {
                    controller.Tank.Blocked = true;
                    controller.Tank.TurretBlock = true;
                }
            }
        }

        private void Update()
        {
            if (Roster != null)
            {
                if (Mode != BattleMode.BigBoss) Roster.Tick();
            }
        }

        private readonly Dictionary<TankModule, Action<string>> _moduleCritHandlers = new Dictionary<TankModule, Action<string>>();
        private void RegisterParticipant(BattleTank tank, string nickname, bool isPlayer)
        {
            var stats = new BattleParticipantStats
            {
                Tank = tank,
                Nickname = nickname,
                IsPlayer = isPlayer,
                Team = tank == null ? 0 : tank.Team,
            };
            _participants.Add(stats);
            if (tank == null)
            {
                return;
            }
            _stats[tank] = stats;
            if (tank.Modules != null)
                foreach (var module in tank.Modules.Modules)
                {
                    Action<string> crit = _ => stats.ModuleCrits++;
                    _moduleCritHandlers[module] = crit;
                    module.OnCrit += crit;
                }
            if (tank.Health != null)
            {
                Action<DamageInfo> damage = info => OnDamage(tank, info);
                _damageHandlers[tank] = damage;
                tank.Health.OnDamageInfoTaken += damage;
            }
            if (tank.Gun != null)
            {
                // Count actual launched rounds, including magazine/duplet emissions;
                // a rejected fire command must not spend ammunition or count a shot.
                Action<bool, int> shot = (_, shellId) => {
                    stats.Shots++;
                    if (tank.GunInfo != null && shellId >= 0 && shellId < tank.GunInfo.Shells.Count)
                        stats.AmmoCost += tank.GunInfo.Shells[shellId].Cost;
                };
                _shotHandlers[tank] = shot;
                tank.ProjectileFired += shot;
            }
        }

        private void OnDamage(BattleTank victim, DamageInfo info)
        {
            if (victim == null || info == null)
            {
                return;
            }
            BattleParticipantStats victimStats;
            _stats.TryGetValue(victim, out victimStats);
            if (info.IsBlock())
            {
                if (victimStats != null)
                {
                    victimStats.DamageBlocked += Math.Max(0, info.DmgBase);
                }
                return;
            }

            var available = victim.Health == null ? info.Dmg : victim.Health.Health;
            var actual = Math.Max(0, Math.Min(info.Dmg, available));
            if (victimStats != null)
            {
                victimStats.DamageTaken += actual;
                victimStats.RepairCost += (int)(actual * Math.Max(0f, victim.Card.RepairCost));
            }
            var attacker = info.AttackerTank as BattleTank;
            BattleParticipantStats attackerStats;
            if (attacker != null && _stats.TryGetValue(attacker, out attackerStats))
            {
                var uprising = attacker.GetComponent<UprisingTank>();
                if (uprising != null && attacker.Team != victim.Team) uprising.State.RewardDamage(actual, info.ShotId, info.DmgType);
                attackerStats.Damage += actual;
                attackerStats.Interaction(victim).Damage += actual;
                if (actual > 0)
                {
                    if (info.ShouldHaveMarker()) attackerStats.RecordHit(info.ShotId);
                    // Godot increments on each successful shell damage event;
                    // bounces/misses do not reset this recovered source counter.
                    if (info.DmgType == DamageType.Shell)
                    {
                        attackerStats.PiercerCurrent++;
                        if (attackerStats.PiercerCurrent >= 10) { attackerStats.PiercerStreaks++; attackerStats.PiercerCurrent = 0; }
                    }
                    _lastAttacker[victim] = attacker;
                }
            }
        }

        private void OnTankDestroyed(BattleTank tank)
        {
            BattleTank attacker = null;
            BattleParticipantStats attackerStats;
            if (tank != null && _lastAttacker.TryGetValue(tank, out attacker)
                && attacker.Team != tank.Team
                && _stats.TryGetValue(attacker, out attackerStats))
            {
                attackerStats.Kills++;
                attackerStats.Interaction(tank).Killed = true;
                var alive = 0;
                foreach (var seat in _participants) if (seat.Tank != null && seat.Team == attacker.Team && !seat.Tank.Dead) alive++;
                if (alive == 1) attackerStats.KolobanovKills++;
                if (attacker.Health != null && attacker.Health.Health > 0
                    && (double)attacker.Health.Health / attacker.Health.MaxHealth <= .2 && attackerStats.ModuleCrits >= 5)
                    attackerStats.TarczayKills++;
            }

            // tank_sound_manager.gd hangs the crew report off the local tank's
            // own on_enemy_destroy and drops it for a friendly victim:
            //
            //   if !self.tank.is_player(): return
            //   if Battle.is_player_team(victim.team): return
            //   Audio.play_crew_message(Audio.CrewMessage.ENEMY_KILLED)
            //
            // so two bots trading shots say nothing at all, and the line is
            // only heard for a kill the player actually scored. The source
            // never plays ALLY_KILLED off a death.
            if (tank != null && tank != Player && attacker == Player
                && tank.Team != Roster.PlayerTeam
                && BlitzBootstrap.Current != null
                && BlitzBootstrap.Current.Audio != null)
            {
                BlitzBootstrap.Current.Audio.PlayCrewMessage(
                    Core.Audio.CrewMessage.EnemyKilled);
            }

            var playerTeam = Roster.PlayerTeam;
            var allyScore = playerTeam == 0 ? Roster.TeamAScore : Roster.TeamBScore;
            var enemyScore = playerTeam == 0 ? Roster.TeamBScore : Roster.TeamAScore;
            if (Hud != null)
            {
                // MarkTankDestroyed redraws the icons and the alive counts.
                Hud.MarkTankDestroyed(tank);
            }
            // _on_player_destroyed: the combat HUD goes away and the observer
            // controls come up in its place.
            if (tank != null && tank == Player)
            {
                SetCombatHudVisible(false);
            }

            if (!Roster.IsOngoing)
            {
                StopAllTanks();
            }
        }

        /// <summary>
        /// player_controller's "menu" action. The settings screen handles that
        /// input itself while it is up, so the pause menu underneath ignores
        /// it exactly like the Godot Control that consumes the event.
        /// </summary>
        private void ToggleMenu()
        {
            if (SettingsUi != null)
            {
                return;
            }

            PauseMenu.Toggle();
        }

        /// <summary>btn_settings of the pause menu.</summary>
        private void OpenSettings(bool mobile, SettingsData settings)
        {
            if (SettingsUi != null)
            {
                return;
            }

            SettingsUi = SettingsScreen.Open(
                transform,
                settings,
                BattlePauseMenu.SortingOrder + 1,
                mobile,
                () =>
                {
                    SettingsUi = null;
                    if (BlitzBootstrap.Current != null)
                    {
                        BlitzBootstrap.Current.Persist();
                    }
                });
        }

        /// <summary>
        /// <c>set_combat_hud_visible</c>. The source keeps the HUD and the
        /// mobile widgets in one scene; here they are two objects, so the call
        /// walks both.
        /// </summary>
        public void NotifyUprisingRespawn(BattleTank tank){_lastAttacker.Remove(tank);}
        public void SetCombatHudVisible(bool on)
        {
            if (Hud != null) { Hud.SetCombatVisible(on); }
            if (Controls != null) { Controls.SetCombatVisible(on); }
            if (Lamp != null) { Lamp.SetCombatVisible(on); }
        }

        /// <summary>
        /// Hides the player control layer and stops every look input while a
        /// flow screen (the map intro) owns the display.
        /// </summary>
        /// <summary>The spotting scan of both teams.</summary>
        public BattleSpotter Spotter { get; private set; }

        /// <summary>The ram damage resolver.</summary>
        public BattleRam Ram { get; private set; }

        /// <summary>The sixth-sense bulb of the player HUD.</summary>
        public BlitzLegacy.Runtime.Ui.BattleSpottedLamp Lamp { get; private set; }

        /// <summary><c>_on_player_spotted</c> raises the bulb three seconds later.</summary>
        private void OnPlayerSpotted(bool spotted)
        {
            if (spotted && Lamp != null)
            {
                Lamp.Schedule();
            }
        }

        public void SetInputActive(bool on)
        {
            // A dedicated server has no player at the keyboard: its anchor
            // tank is driven from the wire only.
            if (Dedicated) { on = false; }
            if (Controls != null) { Controls.SetInputActive(on); }
        }

        /// <summary>Battle.get_player_team().</summary>
        public IList<BattleParticipantStats> PlayerTeamRoster()
        {
            var team = Player != null ? Player.Team : 0;
            var side = new List<BattleParticipantStats>();
            for (var i = 0; i < _participants.Count; i++)
            {
                if (_participants[i].Team == team) { side.Add(_participants[i]); }
            }

            return side;
        }

        private void OnBattleEnded(BattleRoster roster)
        {
            if (_finishedPublished)
            {
                return;
            }
            _finishedPublished = true;
            StopAllTanks();
            if (Hud != null)
            {
                Hud.SetClockRunning(false);
                Hud.SetBattleTime(roster.GetTime());
                Hud.RefreshTeamRoster();
            }
            var handler = Finished;
            if (handler != null)
            {
                handler(this);
            }
        }

        private void StopAllTanks()
        {
            Player.Blocked = true;
            Player.TurretBlock = true;
            foreach (var driver in _remoteDrivers.Values)
            {
                var remote = driver != null ? driver.GetComponent<BattleTank>() : null;
                if (remote != null) { remote.Blocked = true; remote.TurretBlock = true; }
            }
            for (var i = 0; i < _bots.Count; i++)
            {
                if (_bots[i] != null)
                {
                    _bots[i].Activate(false);
                }
            }
        }

        private void OnDestroy()
        {
            GraphicsQuality.Applied -= RepaintCamouflages;
            if (_hearing != null && BlitzBootstrap.Current != null && BlitzBootstrap.Current.Audio != null)
            {
                BlitzBootstrap.Current.Audio.StopListeningFrom(_hearing);
            }
            if (Sandbox != null) { Sandbox.Deactivate(); }
            CommitTeamDamage();
            if (Roster != null)
            {
                Roster.TankGotDestroyed -= OnTankDestroyed;
                Roster.BattleEnded -= OnBattleEnded;
                Roster.End();
            }
            foreach (var pair in _moduleCritHandlers) pair.Key.OnCrit -= pair.Value;
            _moduleCritHandlers.Clear();
            foreach (var pair in _damageHandlers)
            {
                if (pair.Key != null && pair.Key.Health != null)
                {
                    pair.Key.Health.OnDamageInfoTaken -= pair.Value;
                }
            }
            foreach (var pair in _shotHandlers)
            {
                if (pair.Key != null && pair.Key.Gun != null)
                {
                    pair.Key.ProjectileFired -= pair.Value;
                }
            }
            _damageHandlers.Clear();
            _shotHandlers.Clear();
        }

        /// <summary>
        /// <c>for item in player_curtank.consumables: add_consumable(...)</c>,
        /// plus the credits the source books per used item through
        /// <c>credits_lost_consumables</c>. Every mode mounts the hangar
        /// loadout this one way; a mode never swaps a human's items.
        /// </summary>
        private void MountConsumables(BattleTank tank, TankSaveData save)
        {
            if (tank == null || save == null) { return; }

            MountLoadout(tank, LoadoutService.ActiveConsumables(save));

            var level = tank.Card != null ? tank.Card.Level : 1;
            tank.ConsumableUsed += info =>
            {
                if (info == null) { return; }

                ConsumableCreditsLost += ConsumableCatalog.Cost(info.Id, level);
            };
        }

        /// <summary>
        /// A remote human's hangar loadout as its hello or plan slot carried it.
        /// The host (or a client promoted to host) spends it for that crew; the
        /// credits are booked by the player's own peer.
        /// </summary>
        private static void MountLoadout(BattleTank tank, IReadOnlyList<string> consumables)
        {
            if (tank == null || consumables == null) { return; }

            for (var i = 0; i < consumables.Count; i++)
            {
                tank.AddConsumable(consumables[i]);
            }
        }

        /// <summary>
        /// The supply of the crews no hangar equips: Big Boss hands every bot the
        /// support pair, spent through AiTankController.UseSupportConsumables.
        /// A human seat always keeps its own loadout, empty slots included.
        /// </summary>
        private void MountBotSupply()
        {
            if (Mode != BattleMode.BigBoss) { return; }

            foreach (var tank in BattleTanks())
            {
                if (tank == null || tank == Player || IsHumanTank(tank) || tank.ActiveConsumables.Count > 0) { continue; }

                tank.AddConsumable(ConsumableCatalog.RepairKit);
                tank.AddConsumable(ConsumableCatalog.Afterburning);
            }
        }

        /// <summary><c>player.credits_lost_consumables</c>.</summary>
        public int ConsumableCreditsLost { get; private set; }


        // ----- network seating ------------------------------------------------

        /// <summary>Null for an offline battle.</summary>
        public BattleNetSetup Net { get; private set; }

        /// <summary>
        /// Dedicated server battle: every human is remote, <see cref="Player"/>
        /// is only the anchor seat (and the camera target), never local input.
        /// </summary>
        public bool Dedicated { get { return Net != null && Net.Dedicated; } }

        /// <summary>The tank this peer's own player drives (none on a dedicated server).</summary>
        private bool IsLocalTank(BattleTank tank) { return tank != null && tank == Player && !Dedicated; }

        /// <summary>
        /// The roster every peer seats from. Built by the host while it seats
        /// its own matchmade battle; handed in by the host on a client.
        /// </summary>
        public BattleNetPlan NetPlan { get; private set; }

        /// <summary>Tanks by plan slot index, the address used on the wire.</summary>
        public IReadOnlyList<BattleTank> SlotTanks { get { return _slotTanks; } }

        /// <summary>Online team damage of this battle (see <see cref="BattleTeamDamage"/>).</summary>
        public BattleTeamDamage TeamDamage { get; } = new BattleTeamDamage();

        /// <summary>Plan slot of <paramref name="tank"/>, or -1.</summary>
        public int SlotOfTank(BattleTank tank)
        {
            return tank == null ? -1 : _slotTanks.IndexOf(tank);
        }

        /// <summary>True while a human (local or remote) drives the tank.</summary>
        public bool IsHumanTank(BattleTank tank)
        {
            var slot = SlotOfTank(tank);
            return slot >= 0 && slot < _planSlots.Count && _planSlots[slot].IsHuman;
        }

        /// <summary>Host: one team-mate hurt another online.</summary>
        public void RecordTeamDamage(BattleTank attacker, BattleTank victim, int dealt)
        {
            if (Net == null || Net.IsClient || !Started) { return; }
            TeamDamage.Record(attacker, victim, dealt, IsHumanTank(attacker));
        }

        /// <summary>Registers the local player with the record of its account.</summary>
        private void RegisterLocalTeamKill(BattleTank tank)
        {
            if (Net == null || tank == null) { return; }
            var bootstrap = BlitzBootstrap.Current;
            var account = bootstrap != null && bootstrap.Accounts != null ? bootstrap.Accounts.GetCurrentAccount() : null;
            if (account == null) { return; }
            var now = TeamKillPolicy.Now();
            TeamDamage.Register(tank, account.TeamKill.IsBlue(now), account.TeamKill.Pending, account.TeamKill.Watched(now));
        }

        /// <summary>
        /// Folds the local player's team damage into the account once, however
        /// the battle ends: result screen, leaving, a kick or a lost host.
        /// </summary>
        public void CommitTeamDamage()
        {
            if (Net == null || Dedicated || TeamDamage.Committed) { return; }
            TeamDamage.Committed = true;
            var damage = TeamDamage.ReplicatedLocalDamage + TeamDamage.BattleDamage(Player);
            var bootstrap = BlitzBootstrap.Current;
            var account = bootstrap != null && bootstrap.Accounts != null ? bootstrap.Accounts.GetCurrentAccount() : null;
            if (account == null || !TeamKillPolicy.ApplyBattle(account.TeamKill, damage, TeamKillPolicy.Now())) { return; }
            Debug.Log("[BlitzNet] team damage this battle: " + damage + ", total " + account.TeamKill.Total
                + ", offences " + account.TeamKill.Offences);
            bootstrap.Persist();
        }

        /// <summary>Host only: the drivers of remote human slots, by Fusion PlayerId.</summary>
        public IReadOnlyDictionary<int, BattleRemoteDriver> RemoteDrivers { get { return _remoteDrivers; } }

        private readonly List<BattleTank> _slotTanks = new List<BattleTank>();
        private readonly List<BattleNetPlan.Slot> _planSlots = new List<BattleNetPlan.Slot>();
        private readonly Dictionary<int, BattleRemoteDriver> _remoteDrivers = new Dictionary<int, BattleRemoteDriver>();

        /// <summary>
        /// Owner written into a human slot whose player is not connected to this
        /// host (yet): after a host migration every other human has to rejoin
        /// and prove its seat before it gets a real PlayerId again.
        /// </summary>
        public static int DetachedOwner(int slot) { return -100 - slot; }

        public static bool IsDetachedOwner(int owner) { return owner <= -100; }

        /// <summary>
        /// Host migration: this client becomes the host of a battle that is
        /// already running. Every puppet goes back to real physics at the pose
        /// and health the old host last sent, bots get their AI back, and the
        /// other humans wait, detached, for their players to rejoin.
        /// </summary>
        public void PromoteToNetHost(int localOwner)
        {
            if (Net == null || !Net.IsClient || NetPlan == null) { return; }
            Net.IsClient = false;
            Net.LocalOwner = localOwner;
            NetPlan.HostOwner = localOwner;
            var started = Started;
            for (var i = 0; i < _slotTanks.Count && i < _planSlots.Count; i++)
            {
                var tank = _slotTanks[i];
                var slot = _planSlots[i];
                if (tank == null) { continue; }
                tank.ClearNetPuppet();
                if (tank == Player)
                {
                    slot.Owner = localOwner;
                    continue;
                }
                if (slot.IsHuman)
                {
                    slot.Owner = DetachedOwner(i);
                    _remoteDrivers[slot.Owner] = BattleRemoteDriver.Attach(tank);
                    if (started) { tank.Blocked = false; tank.TurretBlock = false; }
                    continue;
                }
                var skill = (AiSkillLevel)Mathf.Clamp(slot.Skill, (int)AiSkillLevel.WhiteArmy, (int)AiSkillLevel.PurpleArmy);
                var controller = AttachBot(tank, skill);
                if (started && controller != null && !tank.Dead) { controller.StartBattle(); }
            }
            Debug.Log("[BlitzNet] promoted to host: slots=" + _slotTanks.Count + " started=" + started);
        }

        /// <summary>
        /// Planned hand-over: this host gives the battle to another player and
        /// becomes a client of it. Every tank turns back into a puppet of the
        /// network, bots and remote drivers go away (the new host owns them).
        /// </summary>
        public void DemoteToNetClient()
        {
            if (Net == null || Net.IsClient || NetPlan == null) { return; }
            Net.IsClient = true;
            foreach (var driver in _remoteDrivers.Values)
            {
                if (driver != null) { Destroy(driver); }
            }
            _remoteDrivers.Clear();
            for (var i = 0; i < _bots.Count; i++)
            {
                if (_bots[i] != null) { Destroy(_bots[i]); }
            }
            _bots.Clear();
            for (var i = 0; i < _slotTanks.Count; i++)
            {
                if (_slotTanks[i] != null) { _slotTanks[i].SetNetPuppet(); }
            }
            Debug.Log("[BlitzNet] demoted to client: slots=" + _slotTanks.Count);
        }

        /// <summary>The tank of a seat is destroyed and has no respawn left.</summary>
        public bool IsOutForGood(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= _slotTanks.Count) { return true; }
            var tank = _slotTanks[slotIndex];
            if (tank == null) { return true; }
            if (!tank.Dead) { return false; }
            if (tank.BigBoss != null) return tank.BigBoss.State.Exhausted;
            var uprising = tank.GetComponent<UprisingTank>();
            return uprising == null || (!uprising.State.Respawning && uprising.State.RespawnsLeft == 0);
        }

        /// <summary>Hidden host reputation of the signed-in account.</summary>
        public static int LocalReputation()
        {
            var bootstrap = BlitzBootstrap.Current;
            var account = bootstrap != null && bootstrap.Accounts != null ? bootstrap.Accounts.GetCurrentAccount() : null;
            if (account == null) { return OnlineReputation.Neutral; }
            OnlineReputation.SettleCrash(account.Reputation);
            return OnlineReputation.Score(account.Reputation, account.TimeIngame);
        }

        /// <summary>
        /// Client after a reconnect or a host migration: the battle on screen is
        /// kept, only the owners the (new) host assigned are taken over. Returns
        /// false when the plan describes a different battle.
        /// </summary>
        public bool AdoptNetPlan(BattleNetPlan plan, int localOwner)
        {
            if (Net == null || !Net.IsClient || plan == null || NetPlan == null) { return false; }
            if (plan.MapId != NetPlan.MapId || plan.Slots.Count != _planSlots.Count) { return false; }
            var own = plan.SlotOf(localOwner);
            if (own < 0 || _slotTanks[own] != Player) { return false; }
            for (var i = 0; i < plan.Slots.Count; i++)
            {
                _planSlots[i].Owner = plan.Slots[i].Owner;
                _planSlots[i].Reputation = plan.Slots[i].Reputation;
                _planSlots[i].Seat = plan.Slots[i].Seat;
            }
            Net.LocalOwner = localOwner;
            return true;
        }

        /// <summary>
        /// Host: gives a human seat to the peer that proved it (reconnect or
        /// rejoin after a migration), keyed by that peer's new PlayerId.
        /// </summary>
        public bool ReassignSeat(int slotIndex, int newOwner)
        {
            if (Net == null || Net.IsClient || slotIndex < 0 || slotIndex >= _planSlots.Count) { return false; }
            var slot = _planSlots[slotIndex];
            var tank = _slotTanks[slotIndex];
            if (!slot.IsHuman || tank == null || IsLocalTank(tank)) { return false; }
            BattleRemoteDriver driver;
            if (_remoteDrivers.TryGetValue(slot.Owner, out driver)) { _remoteDrivers.Remove(slot.Owner); }
            if (driver == null) { driver = tank.GetComponent<BattleRemoteDriver>(); }
            if (driver == null) { driver = BattleRemoteDriver.Attach(tank); }
            driver.Clear();
            _remoteDrivers[newOwner] = driver;
            slot.Owner = newOwner;
            if (Started) { tank.Blocked = false; tank.TurretBlock = false; }
            return true;
        }

        /// <summary>Host: marks a human seat as waiting for its player to come back.</summary>
        public void DetachSeat(int slotIndex)
        {
            if (Net == null || Net.IsClient || slotIndex < 0 || slotIndex >= _planSlots.Count) { return; }
            var slot = _planSlots[slotIndex];
            if (!slot.IsHuman || IsDetachedOwner(slot.Owner) || IsLocalTank(_slotTanks[slotIndex])) { return; }
            BattleRemoteDriver driver;
            if (_remoteDrivers.TryGetValue(slot.Owner, out driver))
            {
                _remoteDrivers.Remove(slot.Owner);
                if (driver != null) { driver.Clear(); }
            }
            slot.Owner = DetachedOwner(slotIndex);
            if (driver != null) { _remoteDrivers[slot.Owner] = driver; }
        }

        /// <summary>
        /// Host: the player never came back, so the AI drives the seat for the
        /// rest of the battle instead of leaving a free kill on the map.
        /// </summary>
        public void ReleaseSeatToBot(int slotIndex)
        {
            if (Net == null || Net.IsClient || slotIndex < 0 || slotIndex >= _planSlots.Count) { return; }
            var slot = _planSlots[slotIndex];
            var tank = _slotTanks[slotIndex];
            if (!slot.IsHuman || tank == null || IsLocalTank(tank)) { return; }
            BattleRemoteDriver driver;
            if (_remoteDrivers.TryGetValue(slot.Owner, out driver)) { _remoteDrivers.Remove(slot.Owner); }
            if (driver == null) { driver = tank.GetComponent<BattleRemoteDriver>(); }
            if (driver != null) { Destroy(driver); }
            slot.Owner = BattleNetPlan.BotOwner;
            slot.Seat = string.Empty;
            if (tank.Dead) { return; }
            var controller = AttachBot(tank, AiSkillLevel.GreenArmy);
            if (Started && controller != null) { controller.StartBattle(); }
            Debug.Log("[BlitzNet] seat " + slotIndex + " handed to the AI");
        }

        private IEnumerator SeatMatchmade(
            TanksDatabase database, TankInfo card, string playerName,
            TankSaveData playerTank, IRandomSource random)
        {
            var names = DrawBotNames((MatchRoster.TeamSize * 2) - 1, random);
            var guests = NetGuestOrder(database, card, MatchRoster.TeamSize);
            MirrorSeats(database, card, guests, random);
            if (Dedicated && playerTank != null)
            {
                // The anchor's modules come off the wire: same bounds as a guest's.
                playerTank.GunId = ClampIndex(playerTank.GunId, card.Guns.Count);
                playerTank.TurretId = ClampIndex(playerTank.TurretId, card.Turrets.Count);
            }
            var nameIndex = 0;
            var unfair = random.Chance(UnfairSkillChance);

            for (var slot = 0; slot < MatchRoster.TeamSize; slot++)
            {
                yield return null;
                if (slot == MatchRoster.PlayerSlot)
                {
                    Player = Seat("Player" + card.Name, card,
                        SpawnAt(World.PlayerSpawns, slot, World.PlayerSpawn),
                        0, _allies,
                        playerTank != null ? playerTank.GunId : 0,
                        playerTank != null ? playerTank.EngineId : 0,
                        playerTank != null ? playerTank.TrackId : 0,
                        playerTank != null ? playerTank.ShellsAmounts : null,
                        playerTank != null ? playerTank.Equipment : null,
                        playerTank != null ? playerTank.TurretId : 0);
                    // setup_completed: the source mounts the saved equipment,
                    // then the saved consumables, then fills the rack.
                    MountConsumables(Player, playerTank);
                    var camouflage = CamouflageService.BattleCamouflage(
                        CamouflageAssets.Catalog, playerTank, World.Map.Id);
                    WearCamouflage(Player, camouflage);
                    RegisterParticipant(Player,
                        string.IsNullOrEmpty(playerName) ? "Player" : playerName, true);
                    if (Dedicated)
                    {
                        // The anchor is a remote human like any guest: its
                        // team-kill record comes from its hello, never from
                        // the server's own account, and the wire drives it.
                        var anchor = Net.Anchor != null ? Net.Anchor.Hello : null;
                        TeamDamage.Register(Player, anchor != null && anchor.TeamKiller,
                            anchor != null ? anchor.TeamKillPending : 0, anchor != null && anchor.TeamKillWatched);
                        Player.Blocked = true;
                        Player.TurretBlock = true;
                        _remoteDrivers[Net.LocalOwner] = BattleRemoteDriver.Attach(Player);
                    }
                    else
                    {
                        RegisterLocalTeamKill(Player);
                    }
                    RecordSlot(Player, Net != null ? Net.LocalOwner : BattleNetPlan.BotOwner,
                        string.IsNullOrEmpty(playerName) ? "Player" : playerName,
                        playerTank != null ? playerTank.GunId : 0,
                        playerTank != null ? playerTank.EngineId : 0,
                        playerTank != null ? playerTank.TrackId : 0,
                        playerTank != null ? playerTank.TurretId : 0, 0,
                        playerTank != null ? playerTank.ShellsAmounts : null,
                        playerTank != null ? playerTank.Equipment : null,
                        Net != null ? Net.LocalSeat : null,
                        playerTank != null ? LoadoutService.ActiveConsumables(playerTank) : null,
                        camouflage);
                    continue;
                }

                if (SeatGuest(database, guests, 0, slot, World.PlayerSpawns, World.PlayerSpawn, _allies))
                {
                    continue;
                }

                var allyCard = Card(database, MatchRoster.Allies[slot], card);
                var allySkill = ChooseSkill(allyCard, false, unfair, random);
                var allyLoadout = BotLoadoutSelector.Select(allyCard, allySkill, random);
                var ally = Seat("Ally" + slot + allyCard.Name, allyCard,
                    SpawnAt(World.PlayerSpawns, slot, World.PlayerSpawn),
                    0, _allies, allyLoadout.Gun, allyLoadout.Engine,
                    allyLoadout.Tracks, allyLoadout.Ammo, null, allyLoadout.Turret);
                if (Ally == null) { Ally = ally; }
                var allyName = names[nameIndex++];
                RegisterParticipant(ally, allyName, false);
                AttachBot(ally, allySkill);
                RecordSlot(ally, BattleNetPlan.BotOwner, allyName, allyLoadout.Gun,
                    allyLoadout.Engine, allyLoadout.Tracks, allyLoadout.Turret,
                    (int)allySkill, allyLoadout.Ammo, null, null);
            }

            for (var slot = 0; slot < MatchRoster.TeamSize; slot++)
            {
                yield return null;
                if (SeatGuest(database, guests, 1, slot, World.BotSpawns, World.BotSpawn, _enemies))
                {
                    nameIndex++;
                    continue;
                }
                var enemyCard = Card(database, MatchRoster.Enemies[slot], card);
                var enemySkill = ChooseSkill(enemyCard, true, unfair, random);
                var enemyLoadout = BotLoadoutSelector.Select(enemyCard, enemySkill, random);
                var enemy = Seat("Enemy" + slot + enemyCard.Name, enemyCard,
                    SpawnAt(World.BotSpawns, slot, World.BotSpawn),
                    1, _enemies, enemyLoadout.Gun, enemyLoadout.Engine,
                    enemyLoadout.Tracks, enemyLoadout.Ammo, null, enemyLoadout.Turret);
                if (Bot == null) { Bot = enemy; }
                var enemyName = names[nameIndex++];
                RegisterParticipant(enemy, enemyName, false);
                AttachBot(enemy, enemySkill);
                RecordSlot(enemy, BattleNetPlan.BotOwner, enemyName, enemyLoadout.Gun,
                    enemyLoadout.Engine, enemyLoadout.Tracks, enemyLoadout.Turret,
                    (int)enemySkill, enemyLoadout.Ammo, null, null);
            }
            FinishPlan();

        }

        /// <summary>
        /// The last word on the mirrored cards (<see cref="Matchmaker.MirroredTanks"/>):
        /// writes every guest's card into the seat <see cref="SeatGuest"/> is
        /// about to give it, then evens the mirrored cards out over the bot
        /// seats left. It runs after every roster rewrite (the Big Boss tier
        /// clamp, the humans), so the battle that spawns is the mirrored one.
        /// </summary>
        private void MirrorSeats(
            TanksDatabase database, TankInfo card, List<Queue<BattleNetSetup.Guest>> guests,
            IRandomSource random)
        {
            var lockedAllies = new HashSet<int> { MatchRoster.PlayerSlot };
            var lockedEnemies = new HashSet<int>();
            var allies = guests[0].ToArray();
            var next = 0;
            for (var slot = 0; slot < MatchRoster.TeamSize && next < allies.Length; slot++)
            {
                if (slot == MatchRoster.PlayerSlot) { continue; }
                MatchRoster.Allies[slot] = GuestCardName(database, allies[next++]);
                lockedAllies.Add(slot);
            }
            var enemies = guests[1].ToArray();
            for (var slot = 0; slot < MatchRoster.TeamSize && slot < enemies.Length; slot++)
            {
                MatchRoster.Enemies[slot] = GuestCardName(database, enemies[slot]);
                lockedEnemies.Add(slot);
            }
            var uneven = new Matchmaker(database, random)
                .MirrorRoster(MatchRoster, card.Level, lockedAllies.Contains, lockedEnemies.Contains);
            if (uneven > 0)
            {
                Debug.LogWarning("[Blitz] mirrored tanks left uneven by " + uneven + " cop" + (uneven == 1 ? "y" : "ies"));
            }
        }

        private static string GuestCardName(TanksDatabase database, BattleNetSetup.Guest guest)
        {
            var hello = guest.Hello;
            var info = hello != null ? database.Get(hello.TankId) : null;
            return info != null ? info.Name : DefaultTankId;
        }

        private static string MirrorKey(TankInfo card)
        {
            return card != null && Matchmaker.IsMirrored(card.Name) ? card.Name : null;
        }

        /// <summary>
        /// Splits the guests over both teams by tank strength
        /// (<see cref="OnlineBalancer.SplitTeams(int, IList{int}, int, string, IList{string})"/>),
        /// so equal tanks face each other and two humans in the same mirrored
        /// card always do. Returns the queue of guests for each team.
        /// </summary>
        private List<Queue<BattleNetSetup.Guest>> NetGuestOrder(TanksDatabase database, TankInfo hostCard, int teamSize)
        {
            var result = new List<Queue<BattleNetSetup.Guest>>
            {
                new Queue<BattleNetSetup.Guest>(), new Queue<BattleNetSetup.Guest>(),
            };
            if (Net == null || Net.Guests == null) { return result; }
            var weights = new List<int>(Net.Guests.Count);
            var keys = new List<string>(Net.Guests.Count);
            for (var i = 0; i < Net.Guests.Count; i++)
            {
                var hello = Net.Guests[i].Hello;
                var guestCard = hello != null ? database.Get(hello.TankId) : null;
                weights.Add(WeightOf(guestCard));
                keys.Add(MirrorKey(guestCard));
            }
            var teams = OnlineBalancer.SplitTeams(WeightOf(hostCard), weights, teamSize, MirrorKey(hostCard), keys);
            for (var i = 0; i < Net.Guests.Count; i++)
            {
                if (teams[i] < 0)
                {
                    Debug.LogWarning("[BlitzNet] no seat left for player " + Net.Guests[i].Owner);
                    continue;
                }
                result[teams[i]].Enqueue(Net.Guests[i]);
            }
            return result;
        }

        private static int WeightOf(TankInfo card)
        {
            if (card == null) { return OnlineBalancer.Weight(1, 2); }
            int column;
            switch (card.Class)
            {
                case TankClass.Heavy: column = 0; break;
                case TankClass.Medium: column = 1; break;
                case TankClass.TankDestroyer: column = 3; break;
                default: column = 2; break;
            }
            return OnlineBalancer.Weight(Mathf.Clamp(card.Level, 1, 10), column);
        }

        private bool SeatGuest(
            TanksDatabase database, List<Queue<BattleNetSetup.Guest>> guests, int team,
            int slot, List<Transform> spawns, Transform fallback, List<BattleTank> side)
        {
            if (guests == null || guests[team].Count == 0) { return false; }
            var guest = guests[team].Dequeue();
            var hello = guest.Hello;
            var guestCard = database.Get(hello.TankId);
            if (guestCard == null)
            {
                // An unknown or modded tank the host does not have: seat the
                // player in the stock tank rather than refusing the battle.
                Debug.LogWarning("[BlitzNet] unknown tank '" + hello.TankId + "', seating " + DefaultTankId);
                guestCard = database.Get(DefaultTankId);
            }
            var gun = ClampIndex(hello.Gun, guestCard.Guns.Count);
            var turret = ClampIndex(hello.Turret, guestCard.Turrets.Count);
            var tank = Seat("Net" + guest.Owner + guestCard.Name, guestCard,
                SpawnAt(spawns, slot, fallback), team, side, gun,
                Math.Max(0, hello.Engine), Math.Max(0, hello.Tracks), hello.Ammo, null, turret);
            var name = string.IsNullOrEmpty(hello.Name) ? "Player" + guest.Owner : hello.Name;
            RegisterParticipant(tank, name, false);
            TeamDamage.Register(tank, hello.TeamKiller, hello.TeamKillPending, hello.TeamKillWatched);
            tank.Blocked = true;
            tank.TurretBlock = true;
            _remoteDrivers[guest.Owner] = BattleRemoteDriver.Attach(tank);
            MountLoadout(tank, hello.Consumables);
            var camouflage = CamouflageService.BattleCamouflage(
                CamouflageAssets.Catalog, guestCard.Name, hello.Camouflages, World.Map.Id);
            WearCamouflage(tank, camouflage);
            RecordSlot(tank, guest.Owner, name, gun, hello.Engine, hello.Tracks, turret, 0, hello.Ammo, null, guest.Seat,
                hello.Consumables, camouflage);
            return true;
        }

        private static int ClampIndex(int value, int count)
        {
            return count <= 0 ? 0 : Mathf.Clamp(value, 0, count - 1);
        }

        /// <summary>
        /// Dresses a human's tank in its battle camouflage; bots fight plain.
        /// The camouflage is in force on every peer, and only the drawing
        /// follows this client's Camouflage graphics setting.
        /// </summary>
        private void WearCamouflage(BattleTank tank, string camouflage)
        {
            if (tank != null) { tank.WearCamouflage(camouflage, PaintsCamouflage(tank)); }
        }

        /// <summary>The Camouflage graphics setting: everyone's, the player's own, or none.</summary>
        private bool PaintsCamouflage(BattleTank tank)
        {
            if (Dedicated) { return false; }
            var settings = GraphicsQuality.Settings;
            return CamouflageAssets.Painted(
                settings != null ? settings.GraphicsCamouflage : SettingsData.CamouflageForAll,
                tank == Player);
        }

        /// <summary>The graphics tab is open mid-battle too, and its Camouflage row applies at once.</summary>
        private void RepaintCamouflages()
        {
            foreach (var tank in BattleTanks())
            {
                if (tank != null && !string.IsNullOrEmpty(tank.CamouflageId))
                {
                    tank.RepaintCamouflage(PaintsCamouflage(tank));
                }
            }
        }

        private void RecordSlot(
            BattleTank tank, int owner, string name, int gun, int engine, int tracks,
            int turret, int skill, IReadOnlyList<int> ammo, IReadOnlyList<string> equipment, string seat,
            IReadOnlyList<string> consumables = null, string camouflage = null)
        {
            var slot = new BattleNetPlan.Slot
            {
                Team = tank.Team,
                Owner = owner,
                TankId = tank.Card != null ? tank.Card.Name : DefaultTankId,
                Name = name ?? string.Empty,
                Gun = gun,
                Engine = engine,
                Tracks = tracks,
                Turret = turret,
                Skill = skill,
                Seat = owner == BattleNetPlan.BotOwner ? string.Empty : (seat ?? string.Empty),
            };
            if (ammo != null) { slot.Ammo.AddRange(ammo); }
            if (equipment != null) { slot.Equipment.AddRange(equipment); }
            // Only a human seat brings a hangar loadout; a bot's follows from the mode.
            if (slot.IsHuman)
            {
                slot.Consumables.AddRange(BattleNetConsumables.Sanitized(consumables));
                slot.Camouflage = camouflage ?? string.Empty;
            }
            _planSlots.Add(slot);
            _slotTanks.Add(tank);
        }

        /// <summary>
        /// Seats are recorded team 0 first, then team 1, the order the plan
        /// requires, so the list becomes the plan as it is.
        /// </summary>
        private void FinishPlan()
        {
            if (Net == null) { return; }
            var plan = new BattleNetPlan
            {
                MapId = World.Map.Id,
                Mode = Mode,
                Build = Net.Build ?? string.Empty,
            };
            plan.Slots.AddRange(_planSlots);
            plan.HostOwner = Net.LocalOwner;
            for (var i = 0; i < plan.Slots.Count; i++)
            {
                var seat = plan.Slots[i];
                if (!seat.IsHuman) { continue; }
                if (seat.Owner == Net.LocalOwner)
                {
                    seat.Reputation = Dedicated
                        ? OnlineReputation.Clamp(Net.Anchor != null && Net.Anchor.Hello != null ? Net.Anchor.Hello.Reputation : OnlineReputation.Neutral)
                        : LocalReputation();
                    continue;
                }
                for (var g = 0; Net.Guests != null && g < Net.Guests.Count; g++)
                {
                    var guest = Net.Guests[g];
                    if (guest.Owner == seat.Owner && guest.Hello != null) { seat.Reputation = OnlineReputation.Clamp(guest.Hello.Reputation); }
                }
            }
            plan.Validate();
            NetPlan = plan;
        }

        private IEnumerator SeatFromPlan(
            TanksDatabase database, TankInfo card, string playerName, TankSaveData playerTank)
        {
            var plan = Net.Plan;
            var own = plan.SlotOf(Net.LocalOwner);
            var teamSize = Math.Max(plan.TeamCount(0), plan.TeamCount(1));
            MatchRoster = new MatchmakerRoster();
            for (var i = 0; i < teamSize; i++) { MatchRoster.Template.Add(0); }

            // The local player is always seated on team 0 of its own view in an
            // offline battle. Online the teams are the host's, so the client
            // keeps the host numbering and only its own seat becomes Player.
            var perTeam = new[] { 0, 0 };
            for (var i = 0; i < plan.Slots.Count; i++)
            {
                yield return null;
                var slot = plan.Slots[i];
                var slotCard = database.Get(slot.TankId) ?? card;
                var index = perTeam[slot.Team]++;
                var spawns = slot.Team == 0 ? World.PlayerSpawns : World.BotSpawns;
                var fallback = slot.Team == 0 ? World.PlayerSpawn : World.BotSpawn;
                var tank = Seat("Net" + i + slotCard.Name, slotCard,
                    SpawnAt(spawns, index, fallback), slot.Team,
                    slot.Team == plan.Slots[own].Team ? _allies : _enemies,
                    slot.Gun, slot.Engine, slot.Tracks, slot.Ammo,
                    slot.Equipment.Count > 0 ? slot.Equipment : null, slot.Turret);
                tank.Blocked = true;
                tank.TurretBlock = true;
                tank.SetNetPuppet();
                _slotTanks.Add(tank);
                _planSlots.Add(slot);
                if (i == own)
                {
                    Player = tank;
                    MatchRoster.PlayerSlot = index;
                    MountConsumables(Player, playerTank);
                    RegisterLocalTeamKill(Player);
                }
                else if (slot.Team == plan.Slots[own].Team && Ally == null) { Ally = tank; }
                else if (slot.Team != plan.Slots[own].Team && Bot == null) { Bot = tank; }
                // Another human's loadout rides along, so this peer spends it if it
                // takes the battle over (PromoteToNetHost).
                if (i != own && slot.IsHuman) { MountLoadout(tank, slot.Consumables); }
                WearCamouflage(tank, slot.Camouflage);
                RegisterParticipant(tank, slot.Name, i == own);
            }
            NetPlan = plan;
        }

        private BattleTank Seat(
            string name, TankInfo card, Transform spawn, int team,
            List<BattleTank> side, int gun, int engine, int tracks,
            IReadOnlyList<int> ammo, IReadOnlyList<string> equipment,
            int turret = 0, SandboxSeatRules sandbox = null)
        {
            var tank = BattleTank.Spawn(
                transform, name, card, spawn, World.PlayArea, team,
                gun, engine, tracks, equipment, turret);
            tank.SetShellAmounts(ammo);
            side.Add(tank);
            // apply_battle_mode order: Gravity tunes the suspension, then the
            // Sandbox overrides land, then Uprising banks the final mobility.
            if (Mode == BattleMode.Gravity) GravityModeTank.Attach(tank, this);
            if (Sandbox != null)
            {
                var rules = sandbox ?? new SandboxSeatRules();
                SandboxTank.Apply(tank, Sandbox.Config, rules.Local, rules.Player, rules.Harmless, rules.Stationary);
            }
            if (Mode == BattleMode.Uprising) UprisingTank.Attach(tank, this);
            if (Mode == BattleMode.BigBoss) BigBossTank.Attach(tank, this);
            Roster.Add(tank);
            return tank;
        }

        // ----- war of ducks seating ------------------------------------------

        /// <summary>
        /// _setup_world_of_ducks_battle: two teams of WarDucks (7 each, fewer
        /// when the map lacks spawners), the player in slot 0 of team 0.
        /// </summary>
        private void PlanWorldOfDucks(TankInfo card, int mapCapacity, IRandomSource random)
        {
            var size = Ducks.TeamSizeFor(mapCapacity);
            var roster = new MatchmakerRoster { PlayerSlot = 0 };
            for (var i = 0; i < size; i++)
            {
                roster.Template.Add(0);
                roster.SlotTiers.Add(card.Level);
                roster.Allies.Add(card.Name);
                roster.Enemies.Add(card.Name);
            }
            MatchRoster = roster;
            DucksSeed = random.RangeInclusive(0, 1 << 30);
        }

        /// <summary>
        /// Every duck gets a seeded random water gun (GunForSlot) and its
        /// stock rack; the bots keep the usual skill roll but no HOLD switch.
        /// </summary>
        private IEnumerator SeatWorldOfDucks(TankInfo card, string playerName, IRandomSource random)
        {
            FloatDuckSpawns();
            var size = MatchRoster.TeamSize;
            var gunCount = Math.Min(WorldOfDucksRules.GunCount, card.Guns.Count);
            var names = DrawBotNames((size * 2) - 1, random);
            var nameIndex = 0;
            var unfair = random.Chance(UnfairSkillChance);
            for (var team = 0; team < 2; team++)
            {
                var spawns = team == 0 ? World.PlayerSpawns : World.BotSpawns;
                var fallback = team == 0 ? World.PlayerSpawn : World.BotSpawn;
                var side = team == 0 ? _allies : _enemies;
                for (var slot = 0; slot < size; slot++)
                {
                    yield return null;
                    var gun = WorldOfDucksRules.GunForSlot(DucksSeed, team, slot, gunCount);
                    var ammo = BotLoadoutSelector.AmmoFor(card.GunFor(0, gun));
                    var player = team == 0 && slot == MatchRoster.PlayerSlot;
                    var tank = Seat((player ? "Player" : team == 0 ? "Ally" + slot : "Enemy" + slot) + card.Name,
                        card, SpawnAt(spawns, slot, fallback), team, side, gun, 0, 0, ammo, null, 0);
                    if (player)
                    {
                        Player = tank;
                        var display = string.IsNullOrEmpty(playerName) ? "Player" : playerName;
                        RegisterParticipant(tank, display, true);
                        RegisterLocalTeamKill(tank);
                        RecordSlot(tank, BattleNetPlan.BotOwner, display, gun, 0, 0, 0, 0, ammo, null, null);
                        continue;
                    }
                    if (team == 0 && Ally == null) { Ally = tank; }
                    if (team == 1 && Bot == null) { Bot = tank; }
                    var skill = ChooseSkill(card, team == 1, unfair, random);
                    var name = names[nameIndex++];
                    RegisterParticipant(tank, name, false);
                    AttachBot(tank, skill);
                    RecordSlot(tank, BattleNetPlan.BotOwner, name, gun, 0, 0, 0, (int)skill, ammo, null, null);
                }
            }
            FinishPlan();
        }

        // ----- sandbox seating ------------------------------------------------

        /// <summary>The Sandbox overrides one seat receives.</summary>
        private sealed class SandboxSeatRules
        {
            public SandboxMultipliers Local;
            public bool Player, Harmless, Stationary;
        }

        /// <summary>
        /// _battle.gd for Training: the side dice (or the preset's side), the
        /// independent team sizes clipped by this map's spawners, and the tanks
        /// of every seat. The matchmaker roster is only rebuilt for the HUD.
        /// </summary>
        private void PlanSandbox(TanksDatabase database, TankInfo card, IRandomSource random)
        {
            var onTeamA = SandboxRosterPlanner.PlayerOnTeamA(Sandbox.Config, random);
            if (!onTeamA) { World.SwapSides(); }
            var plan = SandboxRosterPlanner.Build(Sandbox.Config, database, card, onTeamA,
                World.PlayerSpawns.Count, World.BotSpawns.Count, random);
            if (plan.AlliedLimitedByMap)
            {
                Debug.LogWarning("[Blitz] sandbox allied roster limited by map spawners: "
                    + plan.AlliedTotal + " of " + Sandbox.Config.AlliedTeamSize);
            }
            if (plan.EnemyLimitedByMap)
            {
                Debug.LogWarning("[Blitz] sandbox enemy roster limited by map spawners: "
                    + plan.EnemyTotal + " of " + Sandbox.Config.EnemyTeamSize);
            }
            Sandbox.Plan = plan;
            MatchRoster = new MatchmakerRoster();
            for (var i = 0; i < Math.Max(plan.AlliedTotal, plan.EnemyTotal); i++) { MatchRoster.Template.Add(0); }
            MatchRoster.Allies.Add(card.Name);
            foreach (var seat in plan.Allies) { MatchRoster.Allies.Add(seat.TankName); }
            foreach (var seat in plan.Enemies) { MatchRoster.Enemies.Add(seat.TankName); }
            MatchRoster.PlayerSlot = 0;
        }

        /// <summary>
        /// _spawn_training_roster: the player's garage tank on the first spawner
        /// with its equipment, consumables and bought rack, then every planned
        /// bot with its pinned modules, skill and Sandbox profile.
        /// </summary>
        private IEnumerator SeatSandbox(
            TanksDatabase database, TankInfo card, string playerName,
            TankSaveData playerTank, IRandomSource random)
        {
            var plan = Sandbox.Plan;
            var config = Sandbox.Config;
            var names = DrawBotNames(plan.Allies.Count + plan.Enemies.Count, random);
            var nameIndex = 0;
            var unfair = random.Chance(UnfairSkillChance);
            var gun = playerTank != null ? playerTank.GunId : 0;
            var turret = playerTank != null ? playerTank.TurretId : 0;
            var engine = playerTank != null ? playerTank.EngineId : 0;
            var tracks = playerTank != null ? playerTank.TrackId : 0;
            var ammo = SandboxRosterPlanner.PlayerAmmo(card.GunFor(turret, gun),
                playerTank != null ? playerTank.ShellsAmounts : null);
            var display = string.IsNullOrEmpty(playerName) ? "Player" : playerName;

            yield return null;
            Player = Seat("Player" + card.Name, card,
                SpawnAt(World.PlayerSpawns, 0, World.PlayerSpawn), 0, _allies,
                gun, engine, tracks, ammo,
                playerTank != null ? playerTank.Equipment : null, turret,
                new SandboxSeatRules { Local = config.Player, Player = true });
            MountConsumables(Player, playerTank);
            WearCamouflage(Player, CamouflageService.BattleCamouflage(
                CamouflageAssets.Catalog, playerTank, World.Map.Id));
            // setup_training_cheats: only the local player's tank carries them.
            SandboxCheatController.Attach(Player, Sandbox);
            RegisterParticipant(Player, display, true);
            RecordSlot(Player, BattleNetPlan.BotOwner, display, gun, engine, tracks, turret, 0,
                ammo, playerTank != null ? playerTank.Equipment : null, null);

            for (var i = 0; i < plan.Allies.Count; i++)
            {
                yield return null;
                var ally = SeatSandboxBot(database, card, plan.Allies[i], i + 1, 0, false, unfair, random,
                    names[nameIndex++]);
                if (Ally == null) { Ally = ally; }
            }
            for (var i = 0; i < plan.Enemies.Count; i++)
            {
                yield return null;
                var enemy = SeatSandboxBot(database, card, plan.Enemies[i], i, 1, true, unfair, random,
                    names[nameIndex++]);
                if (Bot == null) { Bot = enemy; }
            }
        }

        private BattleTank SeatSandboxBot(
            TanksDatabase database, TankInfo playerCard, SandboxSeat seat, int spawnIndex, int team,
            bool enemyOfPlayer, bool unfair, IRandomSource random, string nickname)
        {
            var botCard = Card(database, seat.TankName, playerCard);
            var skill = SandboxSkill(seat, botCard, enemyOfPlayer, unfair, random);
            var loadout = SandboxRosterPlanner.ApplyModules(
                botCard, BotLoadoutSelector.Select(botCard, skill, random), seat.Profile);
            var spawns = team == 0 ? World.PlayerSpawns : World.BotSpawns;
            var fallback = team == 0 ? World.PlayerSpawn : World.BotSpawn;
            var tank = Seat((team == 0 ? "Ally" : "Enemy") + spawnIndex + botCard.Name, botCard,
                SpawnAt(spawns, spawnIndex, fallback), team, team == 0 ? _allies : _enemies,
                loadout.Gun, loadout.Engine, loadout.Tracks, loadout.Ammo, null, loadout.Turret,
                new SandboxSeatRules
                {
                    Local = seat.Profile != null ? seat.Profile.Multipliers : null,
                    Harmless = seat.Harmless,
                    Stationary = seat.Stationary,
                });
            RegisterParticipant(tank, nickname, false);
            AttachBot(tank, skill);
            RecordSlot(tank, BattleNetPlan.BotOwner, nickname, loadout.Gun, loadout.Engine,
                loadout.Tracks, loadout.Turret, (int)skill, loadout.Ammo, null, null);
            return tank;
        }

        /// <summary>_training_skill: a fixed level, or the ordinary roll for RANDOM.</summary>
        private static AiSkillLevel SandboxSkill(
            SandboxSeat seat, TankInfo card, bool enemyOfPlayer, bool unfair, IRandomSource random)
        {
            if (seat == null || seat.Skill == SandboxBotSlot.RandomSelection)
            {
                return ChooseSkill(card, enemyOfPlayer, unfair, random);
            }
            return (AiSkillLevel)Mathf.Clamp(seat.Skill, (int)AiSkillLevel.Idle, (int)AiSkillLevel.PurpleArmy);
        }

        private static TankInfo Card(
            TanksDatabase database, string name, TankInfo fallback)
        {
            return database.Get(name) ?? fallback;
        }

        private static AiSkillLevel ChooseSkill(
            TankInfo card, bool enemyOfPlayer, bool unfairRoll, IRandomSource random)
        {
            return AiSkillTable.ChooseSkill(
                card != null ? card.Level : 1,
                enemyOfPlayer,
                unfairRoll,
                chance => random.Chance(chance));
        }

        /// <summary>
        /// The ported spawners stand on the sea bed; the 4.8 ducks sail on the
        /// CollisionPlain floor, so each spawner is raised onto it first.
        /// </summary>
        private void FloatDuckSpawns()
        {
            FloatSpawns(World.PlayerSpawns);
            FloatSpawns(World.BotSpawns);
            FloatSpawn(World.PlayerSpawn);
            FloatSpawn(World.BotSpawn);
        }

        private static void FloatSpawns(List<Transform> spawns)
        {
            if (spawns == null) { return; }
            for (var i = 0; i < spawns.Count; i++) { FloatSpawn(spawns[i]); }
        }

        private static void FloatSpawn(Transform spawn)
        {
            if (spawn == null) { return; }
            var position = spawn.position;
            position.y = WorldOfDucksRules.FloatingHeight(position.y);
            spawn.position = position;
        }

        private static Transform SpawnAt(List<Transform> spawns, int index, Transform fallback)
        {
            if (spawns == null || spawns.Count == 0)
            {
                return fallback;
            }
            return spawns[index % spawns.Count];
        }

        private AiTankController AttachBot(BattleTank tank, AiSkillLevel skill)
        {
            if (tank == null)
            {
                return null;
            }
            tank.Blocked = true;
            tank.TurretBlock = true;
            var controller = AiTankController.Attach(tank, skill);
            if (controller != null)
            {
                _bots.Add(controller);
            }
            return controller;
        }

        private static List<string> DrawBotNames(int count, IRandomSource random)
        {
            var available = new List<string>();
            JsonNode root;
            if (JsonNode.TryParse(BlitzResources.LoadText("battle_flow"), out root))
            {
                foreach (var node in root["botNames"].Items)
                {
                    var value = node.AsString();
                    if (!string.IsNullOrEmpty(value) && !available.Contains(value))
                    {
                        available.Add(value);
                    }
                }
            }
            while (available.Count < count)
            {
                available.Add("bot" + (available.Count + 1));
            }
            var result = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var index = random.RangeInclusive(0, available.Count - 1);
                result.Add(available[index]);
                available.RemoveAt(index);
            }
            return result;
        }
    }
}
