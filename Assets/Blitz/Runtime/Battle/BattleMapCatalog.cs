using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Assets;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The small random-battle roster used by the queue. A map may opt into a
    /// precise tier range through randomTiers; legacy manifests without that
    /// field remain eligible for every tier, preserving their old behaviour.
    /// </summary>
    public static class BattleMapCatalog
    {
        private static readonly string[] MapIds = { "rudniki", "ordeal", "karieri", "lake", "asia", "moon" };

        public static bool IsRandomAvailable(MapDefinition map, int tankTier)
        {
            if (map == null || tankTier < 1)
            {
                return false;
            }

            if (map.RandomTiers.Count == 0)
            {
                return true;
            }

            return map.RandomTiers.Contains(tankTier);
        }

        public static List<string> AvailableForTier(int tankTier)
        {
            var result = new List<string>();
            for (var i = 0; i < MapIds.Length; i++)
            {
                try
                {
                    var map = BattleWorld.LoadDefinition(MapIds[i]);
                    if (IsRandomAvailable(map, tankTier))
                    {
                        result.Add(map.Id);
                    }
                }
                catch (Exception)
                {
                    // A partially installed optional map must not prevent the
                    // queue from starting with the remaining roster.
                }
            }

            return result;
        }

        /// <summary>Ids of the shipped battle maps, in catalogue order.</summary>
        public static IReadOnlyList<string> Ids { get { return MapIds; } }

        /// <summary>
        /// Every catalogue map the Sandbox offers (Godot _training_map_ids:
        /// all non-event maps, regardless of the random-battle tier ranges).
        /// A map whose table fails to load is left out.
        /// </summary>
        public static List<MapDefinition> SandboxMaps()
        {
            var result = new List<MapDefinition>();
            for (var i = 0; i < MapIds.Length; i++)
            {
                try
                {
                    var map = BattleWorld.LoadDefinition(MapIds[i]);
                    if (map != null) { result.Add(map); }
                }
                catch (Exception)
                {
                    // Same tolerance as the random roster: an optional map
                    // that is not installed must not hide the others.
                }
            }

            return result;
        }

        public static string PickRandomForTier(int tankTier, IRandomSource random)
        {
            var available = AvailableForTier(tankTier);
            if (available.Count == 0)
            {
                return BattleWorld.DefaultMapId;
            }

            var index = random == null
                ? 0
                : random.RangeInclusive(0, available.Count - 1);
            return available[index];
        }

        public static string PickRandomForTank(string tankId, IRandomSource random)
        {
            var tier = 1;
            try
            {
                var database = TanksDatabase.Parse(
                    BlitzResources.LoadText(BlitzResources.TanksData));
                // A mod tank is not in the shipped table; without its card the
                // tier fell back to I and drew the tier 1-4 map.
                BlitzLegacy.Runtime.Mods.ModRuntime.Apply(database);
                var card = database.Get(tankId);
                if (card != null)
                {
                    tier = card.Level;
                }
            }
            catch (Exception)
            {
                // Default to tier I, which is explicitly covered by Ordeal.
            }

            return PickRandomForTier(tier, random);
        }
    }
}
