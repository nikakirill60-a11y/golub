using System.Collections.Generic;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Mods;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// What the hangar's online queue shows while the network layer looks for
    /// players. Implemented by the queue screen; every text is a localization
    /// key, never a literal.
    /// </summary>
    public interface IBattleOnlineSink
    {
        /// <summary>Players in the room, its capacity, the lobby countdown (negative while waiting) and
        /// the lobby make-up: ten tier counts (tier X first) and four class counts (ht, mt, lt, td).</summary>
        void OnlineLobby(int players, int capacity, float startsIn,
            IReadOnlyList<int> tiers, IReadOnlyList<int> classes);

        /// <summary>
        /// The shared queue: humans waiting in every open battle of this build
        /// and mode, with the summed tier (tier X first) and class tables.
        /// </summary>
        void OnlineQueue(int waiting, IReadOnlyList<int> tiers, IReadOnlyList<int> classes);

        /// <summary>A localization key describing the current step (connecting, waiting...).</summary>
        void OnlineStatus(string key);

        /// <summary>Matchmaking gave up; the key explains why. The queue closes.</summary>
        void OnlineFailed(string key);
    }

    /// <summary>The network layer behind the hangar's online mode.</summary>
    public interface IBattleOnlineService
    {
        /// <summary>Finds or opens a room for the current account, tank and selected mode.</summary>
        void Begin(IBattleOnlineSink sink, BlitzLegacy.Core.Battle.BattleMode mode);

        /// <summary>Leaves matchmaking (only before the battle is built).</summary>
        void Cancel();
    }

    /// <summary>
    /// Registry that lets Runtime start online play without referencing the
    /// network assemblies: BlitzLegacy.Network (Photon) and BlitzLegacy.Direct
    /// (own dedicated server) register themselves at load. The settings pick
    /// which one the hangar uses; the other is the fallback.
    /// </summary>
    public static class BattleOnline
    {
        /// <summary>Photon Cloud, a player hosts (BlitzLegacy.Network).</summary>
        public static IBattleOnlineService PhotonService { get; set; }

        /// <summary>The project's own authoritative server (BlitzLegacy.Direct).</summary>
        public static IBattleOnlineService DirectService { get; set; }

        /// <summary>The service the current settings select, or the one available.</summary>
        public static IBattleOnlineService Service
        {
            get { return Pick(PreferredServer(), PhotonService, DirectService); }
        }

        public static bool Available { get { return Service != null; } }

        private static IBattleOnlineService _active;

        /// <summary>Starts matchmaking on the selected service; <see cref="Cancel"/> stops that one.</summary>
        public static void Begin(IBattleOnlineSink sink, BlitzLegacy.Core.Battle.BattleMode mode)
        {
            // Hard Sandbox ban: the offline-only mode never reaches a service.
            if (BlitzLegacy.Core.Battle.SandboxOnlineBan.Violates(mode))
            {
                _active = null;
                sink.OnlineFailed(BlitzLegacy.Core.Battle.SandboxOnlineBan.ErrorKey);
                return;
            }
            // Hard mod tank ban: only a stock tank may queue for an online battle.
            if (BlitzLegacy.Core.Battle.ModTankOnlineBan.Violates(BattleNetHelloFactory.SelectedTankId()))
            {
                _active = null;
                sink.OnlineFailed(BlitzLegacy.Core.Battle.ModTankOnlineBan.ErrorKey);
                return;
            }
            _active = Service;
            if (_active == null) { sink.OnlineFailed("online:error/not_configured"); return; }
            _active.Begin(sink, mode);
        }

        public static void Cancel()
        {
            var active = _active ?? Service;
            _active = null;
            if (active != null) { active.Cancel(); }
        }

        /// <summary>The selected service when registered, otherwise whichever exists.</summary>
        public static IBattleOnlineService Pick(int preferred, IBattleOnlineService photon, IBattleOnlineService direct)
        {
            if (preferred == SettingsData.OnlineServerPhoton) { return photon ?? direct; }
            return direct ?? photon;
        }

        private static int PreferredServer()
        {
            var bootstrap = BlitzLegacy.Runtime.BlitzBootstrap.Current;
            var settings = bootstrap != null ? bootstrap.Settings : null;
            return settings != null ? settings.OnlineServer : SettingsData.OnlineServerDirect;
        }

        /// <summary>Online battles are always 7 vs 7; bots fill the free seats.</summary>
        public const int TeamSize = 7;

        /// <summary>Humans one online battle takes.</summary>
        public const int Capacity = TeamSize * 2;

        public const string ModeId = "online";
        public const string NameKey = "online:mode/name";
        public const string DescriptionKey = "online:mode/desc";

        private static TanksDatabase _database;

        /// <summary>
        /// Tier (1..MaxLevel) and queue class column (0 ht, 1 mt, 2 lt, 3 td) of a
        /// tank, for the queue tables. Unknown tanks count as tier 1 light.
        /// </summary>
        public static void Classify(string tankId, out int tier, out int column)
        {
            if (_database == null)
            {
                _database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                ModRuntime.Apply(_database);
            }
            var card = string.IsNullOrEmpty(tankId) ? null : _database.Get(tankId);
            tier = card != null ? UnityEngine.Mathf.Clamp(card.Level, 1, TanksDatabase.MaxLevel) : 1;
            var type = card != null ? card.Class : TankClass.Light;
            switch (type)
            {
                case TankClass.Heavy: column = 0; break;
                case TankClass.Medium: column = 1; break;
                case TankClass.TankDestroyer: column = 3; break;
                default: column = 2; break;
            }
        }
    }
}
