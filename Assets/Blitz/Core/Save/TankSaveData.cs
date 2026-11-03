using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Save
{
    /// <summary>Port of Godot <c>SaveTank</c> (assets/data/tank_save.gd).</summary>
    public sealed class TankSaveData
    {
        public const int ShellSlotCount = 3;

        /// <summary>The summer, winter and desert camouflage slots.</summary>
        public const int CamouflageSlotCount = 3;
        public const string DefaultName = "base_tank";

        public string Name = DefaultName;
        public bool Bought;

        public int TurretId;
        public int GunId;
        public int EngineId;
        public int TrackId;
        public int RadioId;
        public int Exp;

        public int AchMaxDamage;
        public int AchMaxBlock;
        public int AchInvincibleStreak;
        public int AchDiehardStreak;

        public readonly List<int> ShellsAmounts = new List<int> { 0, 0, 0 };

        /// <summary>Godot kept a layout version to migrate Sheridan ammo order.</summary>
        public int AmmoLayoutVersion;

        public readonly List<string> Equipment = new List<string>();
        public readonly List<string> Consumables = new List<string>();
        public readonly Dictionary<string, int> Rewards =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Blitz 4.8.0 camouflage slots, summer / winter / desert: the
        /// installed camouflage id, empty while the slot is bare.
        /// </summary>
        public readonly List<string> Camouflages = new List<string>();

        /// <summary>
        /// The camouflages bought for this tank. A purchase is permanent:
        /// an owned camouflage goes in and out of any slot that takes it for
        /// free. Every installed camouflage is owned.
        /// </summary>
        public readonly List<string> OwnedCamouflages = new List<string>();

        /// <summary>The camouflage shop was opened for this tank, so the hangar badge is gone.</summary>
        public bool CamouflageSeen;

        public int StatBattles;
        public int StatKills;
        public int StatMaxKills;
        public int StatMaxXp;
        public int StatDamageDealt;
        public int StatDamageTaken;
        public int StatShellsShot;
        public int StatShellsHits;

        public static TankSaveData WithName(string name)
        {
            var tank = new TankSaveData { Name = string.IsNullOrEmpty(name) ? DefaultName : name };
            tank.Cleanup();
            return tank;
        }

        /// <summary>Mirrors <c>SaveTank.cleanup()</c>: keep the shell array well formed.</summary>
        public void Cleanup()
        {
            while (ShellsAmounts.Count < ShellSlotCount)
            {
                ShellsAmounts.Add(0);
            }

            while (ShellsAmounts.Count > ShellSlotCount)
            {
                ShellsAmounts.RemoveAt(ShellsAmounts.Count - 1);
            }

            for (var i = 0; i < Camouflages.Count; i++)
            {
                Camouflages[i] = Camouflages[i] ?? string.Empty;
            }

            while (Camouflages.Count < CamouflageSlotCount)
            {
                Camouflages.Add(string.Empty);
            }

            while (Camouflages.Count > CamouflageSlotCount)
            {
                Camouflages.RemoveAt(Camouflages.Count - 1);
            }

            CleanupOwnedCamouflages();
        }

        /// <summary>
        /// Drops empty and repeated ids and adds every installed camouflage:
        /// saves from before permanent camouflages own what they wear.
        /// </summary>
        private void CleanupOwnedCamouflages()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < OwnedCamouflages.Count; i++)
            {
                var id = OwnedCamouflages[i];
                if (string.IsNullOrEmpty(id) || !seen.Add(id))
                {
                    OwnedCamouflages.RemoveAt(i--);
                }
            }

            for (var i = 0; i < Camouflages.Count; i++)
            {
                var id = Camouflages[i];
                if (!string.IsNullOrEmpty(id) && seen.Add(id))
                {
                    OwnedCamouflages.Add(id);
                }
            }
        }

        public JsonNode ToJson()
        {
            return JsonNode.NewObject()
                .Set("name", Name)
                .Set("bought", Bought)
                .Set("turret_id", TurretId)
                .Set("gun_id", GunId)
                .Set("engine_id", EngineId)
                .Set("track_id", TrackId)
                .Set("radio_id", RadioId)
                .Set("exp", Exp)
                .Set("ach_max_dmg", AchMaxDamage)
                .Set("ach_max_block", AchMaxBlock)
                .Set("ach_invincible_streak", AchInvincibleStreak)
                .Set("ach_diehard_streak", AchDiehardStreak)
                .Set("shells_amounts", JsonMap.FromInts(ShellsAmounts))
                .Set("ammo_layout_version", AmmoLayoutVersion)
                .Set("equipment", JsonMap.FromStrings(Equipment))
                .Set("consumables", JsonMap.FromStrings(Consumables))
                .Set("rewards", JsonMap.FromIntMap(Rewards))
                .Set("camouflages", JsonMap.FromStrings(Camouflages))
                .Set("camouflages_owned", JsonMap.FromStrings(OwnedCamouflages))
                .Set("camouflage_seen", CamouflageSeen)
                .Set("stat_battles", StatBattles)
                .Set("stat_kills", StatKills)
                .Set("stat_max_kills", StatMaxKills)
                .Set("stat_max_xp", StatMaxXp)
                .Set("stat_damage_dealt", StatDamageDealt)
                .Set("stat_damage_taken", StatDamageTaken)
                .Set("stat_shells_shot", StatShellsShot)
                .Set("stat_shells_hits", StatShellsHits);
        }

        public static TankSaveData FromJson(JsonNode node)
        {
            var tank = new TankSaveData();
            if (node == null || node.Kind != JsonKind.Object)
            {
                tank.Cleanup();
                return tank;
            }

            tank.Name = node["name"].AsString(DefaultName);
            tank.Bought = node["bought"].AsBool();
            tank.TurretId = node["turret_id"].AsInt();
            tank.GunId = node["gun_id"].AsInt();
            tank.EngineId = node["engine_id"].AsInt();
            tank.TrackId = node["track_id"].AsInt();
            tank.RadioId = node["radio_id"].AsInt();
            tank.Exp = node["exp"].AsInt();
            tank.AchMaxDamage = node["ach_max_dmg"].AsInt();
            tank.AchMaxBlock = node["ach_max_block"].AsInt();
            tank.AchInvincibleStreak = node["ach_invincible_streak"].AsInt();
            tank.AchDiehardStreak = node["ach_diehard_streak"].AsInt();
            JsonMap.ReadInts(node["shells_amounts"], tank.ShellsAmounts);
            tank.AmmoLayoutVersion = node["ammo_layout_version"].AsInt();
            JsonMap.ReadStrings(node["equipment"], tank.Equipment);
            JsonMap.ReadStrings(node["consumables"], tank.Consumables);
            JsonMap.ReadIntMap(node["rewards"], tank.Rewards);
            JsonMap.ReadStrings(node["camouflages"], tank.Camouflages);
            JsonMap.ReadStrings(node["camouflages_owned"], tank.OwnedCamouflages);
            tank.CamouflageSeen = node["camouflage_seen"].AsBool();
            tank.StatBattles = node["stat_battles"].AsInt();
            tank.StatKills = node["stat_kills"].AsInt();
            tank.StatMaxKills = node["stat_max_kills"].AsInt();
            tank.StatMaxXp = node["stat_max_xp"].AsInt();
            tank.StatDamageDealt = node["stat_damage_dealt"].AsInt();
            tank.StatDamageTaken = node["stat_damage_taken"].AsInt();
            tank.StatShellsShot = node["stat_shells_shot"].AsInt();
            tank.StatShellsHits = node["stat_shells_hits"].AsInt();
            tank.Cleanup();
            return tank;
        }
    }
}
