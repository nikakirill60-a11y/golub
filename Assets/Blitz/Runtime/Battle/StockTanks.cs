using System;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The tanks shipped with the game (tanks.json, never mods). Online play
    /// knows only these: the list registers itself with
    /// <see cref="ModTankOnlineBan"/> at load, in players and in the editor.
    /// </summary>
    public static class StockTanks
    {
        private static readonly object Gate = new object();
        private static TanksDatabase _database;

        /// <summary>The stock database, parsed once. Never hand it to <c>ModRuntime.Apply</c>.</summary>
        public static TanksDatabase Database
        {
            get
            {
                var database = _database;
                if (database != null) { return database; }
                lock (Gate)
                {
                    if (_database == null)
                    {
                        _database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                    }
                    return _database;
                }
            }
        }

        /// <summary>True for a tank this build ships; false for mods and unknown ids.</summary>
        public static bool IsStock(string tankId)
        {
            if (string.IsNullOrWhiteSpace(tankId)) { return false; }
            var card = Database.Get(tankId);
            return card != null && !card.Mod;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
            ModTankOnlineBan.StockResolver = IsStock;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Warm()
        {
            // Parse on the main thread before any network callback asks.
            try
            {
                if (Database == null) { Debug.LogWarning("[Blitz] stock tanks: no tank list"); }
            }
            catch (Exception error)
            {
                Debug.LogWarning("[Blitz] stock tanks: " + error.Message);
            }
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterInEditor()
        {
            Register();
        }
#endif
    }
}
