using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// Port of Godot <c>TanksDataBase</c> (assets/data/db_tanks.gd). Godot scans
    /// res://assets/tanks/ at boot; here the same cards arrive as one exported
    /// table, keyed by the lower-case card name Godot uses for save data.
    /// </summary>
    public sealed class TanksDatabase
    {
        /// <summary>Cards clamp into tiers 1..MaxLevel (XI).</summary>
        public const int MaxLevel = 11;

        private readonly List<TankInfo> _tanks = new List<TankInfo>();
        private readonly List<TankInfo>[] _byLevel = NewLevelBuckets();
        private readonly Dictionary<string, TankInfo> _byName =
            new Dictionary<string, TankInfo>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<TankInfo> Tanks
        {
            get { return _tanks; }
        }

        public int Count
        {
            get { return _tanks.Count; }
        }

        public bool Has(string name)
        {
            return !string.IsNullOrEmpty(name) && _byName.ContainsKey(name);
        }

        /// <summary>
        /// Godot <c>tank_exists</c>: a known card that is not hidden. The source never
        /// registers its hidden event vehicles, so a seat naming one picks anew.
        /// </summary>
        public bool HasVisible(string name)
        {
            var tank = Get(name);
            return tank != null && !tank.Hidden;
        }

        /// <summary>Godot <c>get_tank_by_name</c>: null when the card is missing.</summary>
        public TankInfo Get(string name)
        {
            TankInfo tank;
            if (string.IsNullOrEmpty(name) || !_byName.TryGetValue(name, out tank))
            {
                return null;
            }

            return tank;
        }

        /// <summary>Godot <c>_add_tank</c>: cleanup(), then the name and tier indexes.</summary>
        public void Add(TankInfo tank)
        {
            if (tank == null || string.IsNullOrEmpty(tank.Name))
            {
                return;
            }

            tank.Cleanup();
            if (_byName.ContainsKey(tank.Name))
            {
                return;
            }

            _tanks.Add(tank);
            _byName[tank.Name] = tank;
            Bucket(tank.Level).Add(tank);
        }

        /// <summary>Drops every mod card, so a changed mods folder can be applied live.</summary>
        public int RemoveMods()
        {
            var gone = new List<TankInfo>();
            for (var i = 0; i < _tanks.Count; i++)
            {
                if (_tanks[i] != null && _tanks[i].Mod) { gone.Add(_tanks[i]); }
            }

            for (var i = 0; i < gone.Count; i++)
            {
                _tanks.Remove(gone[i]);
                _byName.Remove(gone[i].Name);
                for (var b = 0; b < _byLevel.Length; b++) { _byLevel[b].Remove(gone[i]); }
            }

            return gone.Count;
        }

        /// <summary>
        /// Godot <c>lvl_to_array</c>: the cards of one tier in table order. A tier
        /// outside 1..MaxLevel clamps onto the nearest bucket, which is what the source
        /// does when the matchmaker rolls a slot off the tier range.
        /// </summary>
        public IReadOnlyList<TankInfo> ByLevel(int level)
        {
            return Bucket(level);
        }

        private List<TankInfo> Bucket(int level)
        {
            var index = level - 1;
            if (index < 0)
            {
                index = 0;
            }
            else if (index >= MaxLevel)
            {
                index = MaxLevel - 1;
            }

            return _byLevel[index];
        }

        private static List<TankInfo>[] NewLevelBuckets()
        {
            var buckets = new List<TankInfo>[MaxLevel];
            for (var i = 0; i < buckets.Length; i++)
            {
                buckets[i] = new List<TankInfo>();
            }

            return buckets;
        }

        /// <summary>Tanks shown in the research tree, ordered by tier then name.</summary>
        public List<TankInfo> TreeTanks(Nation nation)
        {
            var result = new List<TankInfo>();
            for (var i = 0; i < _tanks.Count; i++)
            {
                var tank = _tanks[i];
                if (tank.TreeTank && tank.Nation == nation)
                {
                    result.Add(tank);
                }
            }

            result.Sort(delegate(TankInfo left, TankInfo right)
            {
                if (left.Level != right.Level)
                {
                    return left.Level.CompareTo(right.Level);
                }

                if (left.TreeBranch != right.TreeBranch)
                {
                    return left.TreeBranch.CompareTo(right.TreeBranch);
                }

                return string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            });
            return result;
        }

        /// <summary>Cards that unlock <paramref name="name"/> (Godot walks the same graph backwards).</summary>
        public List<TankInfo> PrecedingTanks(string name)
        {
            var result = new List<TankInfo>();
            if (string.IsNullOrEmpty(name))
            {
                return result;
            }

            for (var i = 0; i < _tanks.Count; i++)
            {
                if (_tanks[i].UnlockTanks.ContainsKey(name))
                {
                    result.Add(_tanks[i]);
                }
            }

            return result;
        }

        /// <summary>Research cost in XP, or zero when nothing unlocks the tank.</summary>
        public int ResearchCost(string name)
        {
            var preceding = PrecedingTanks(name);
            var cost = 0;
            for (var i = 0; i < preceding.Count; i++)
            {
                int value;
                if (!preceding[i].UnlockTanks.TryGetValue(name, out value))
                {
                    continue;
                }

                if (cost == 0 || value < cost)
                {
                    cost = value;
                }
            }

            return cost;
        }

        public static TanksDatabase FromJson(JsonNode node)
        {
            var database = new TanksDatabase();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return database;
            }

            var tanks = node["tanks"];
            if (tanks.Kind != JsonKind.Array)
            {
                return database;
            }

            for (var i = 0; i < tanks.Count; i++)
            {
                database.Add(TankInfo.FromJson(tanks[i]));
            }

            database.ApplyAdditionalResearchLinks(node["additional_research_links"]);
            database.ApplyEventVehicles(node["event_vehicles"]);
            return database;
        }

        /// <summary>
        /// Cards an event mode owns (<c>event_vehicles</c>: card name -> mode). The event's
        /// battles bring their own card (World of Ducks plays the 4.8 WarDuck), so the
        /// shared one is hidden everywhere else - the hangar carousel, the Sandbox and
        /// every bot roll - like Godot, which never registers its WarDuck.
        /// </summary>
        private void ApplyEventVehicles(JsonNode vehicles)
        {
            if (vehicles == null || vehicles.Kind != JsonKind.Object) { return; }
            foreach (var entry in vehicles.Fields)
            {
                var tank = Get(entry.Key);
                if (tank == null || string.IsNullOrEmpty(entry.Value.AsString())) { continue; }
                tank.Hidden = true;
                tank.IgnoreMatchmaker = true;
            }
        }

        private void ApplyAdditionalResearchLinks(JsonNode links)
        {
            if (links == null || links.Kind != JsonKind.Array) { return; }
            for (var i = 0; i < links.Count; i++)
            {
                var link = links[i];
                var source = Get(link["from"].AsString());
                var target = Get(link["to"].AsString());
                var cost = link["cost_xp"].AsInt();
                if (source == null || target == null || source == target || cost <= 0
                    || source.Premium || target.Premium || !target.TreeTank
                    || source.Nation != target.Nation || source.UnlockTanks.ContainsKey(target.Name))
                { continue; }

                ModuleKind kind;
                switch (link["required_kind"].AsString())
                {
                    case "gun": kind = ModuleKind.Gun; break;
                    case "turret": kind = ModuleKind.Turret; break;
                    case "chassis": case "tracks": kind = ModuleKind.Tracks; break;
                    case "engine": kind = ModuleKind.Engine; break;
                    case "radio": kind = ModuleKind.Radio; break;
                    default: continue;
                }
                ModuleInfo required = null;
                var modules = TankResearchService.Modules(source, kind);
                var moduleName = link["required_module"].AsString();
                for (var m = 0; m < modules.Count; m++)
                {
                    if (string.Equals(modules[m].Name, moduleName, StringComparison.Ordinal))
                    { required = modules[m]; break; }
                }
                if (required == null) { continue; }

                TurretInfo requiredTurret = null;
                var turretName = link["required_turret"].AsString();
                if (!string.IsNullOrEmpty(turretName))
                {
                    for (var t = 0; t < source.Turrets.Count; t++)
                    {
                        if (string.Equals(source.Turrets[t].Name, turretName, StringComparison.Ordinal))
                        { requiredTurret = source.Turrets[t]; break; }
                    }
                    if (requiredTurret == null) { continue; }
                    if (kind == ModuleKind.Gun && !requiredTurret.CanMount(moduleName)) { continue; }
                }
                source.UnlockTanks[target.Name] = cost;
                source.AdditionalResearchLinks[target.Name] = new AdditionalTankResearchLink(
                    kind, required, requiredTurret, link["preserve_legacy_elite"].AsBool(true));
            }
        }

        public static TanksDatabase Parse(string json)
        {
            JsonNode node;
            return JsonNode.TryParse(json, out node) ? FromJson(node) : new TanksDatabase();
        }
    }
}
