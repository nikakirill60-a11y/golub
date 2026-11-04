using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>Consumable.ConsumableType of assets/data/consumable.gd.</summary>
    public enum ConsumableType
    {
        SingleUse = 0,
        Reusable = 1,
        Passive = 2,
    }

    /// <summary>
    /// One entry of TanksDataBase.consumables_list. The source builds these
    /// with new_consumable(id, cooldown, on_apply, on_stop_use, type), so the
    /// defaults here are the defaults of consumable.gd: cooldown 60,
    /// use_time 30, SINGLE_USE and the generic refusal message.
    /// </summary>
    public sealed class ConsumableInfo
    {
        private static readonly Nation[] AnyNation = new Nation[0];

        public string Id;
        public string NameKey;
        public string DescriptionKey;
        public string IconRegion;
        public float CostFactor = 1f;
        // Obscured (anti memory-editing): the equipment cooldown and use window.
        private Security.ObscuredInt _cooldown = 60;
        private Security.ObscuredInt _useTime = 30;
        public int Cooldown { get { return _cooldown.Get("consumable.cooldown"); } set { _cooldown = value; } }
        public int UseTime { get { return _useTime.Get("consumable.use_time"); } set { _useTime = value; } }
        public ConsumableType Type = ConsumableType.SingleUse;

        /// <summary>with_sounds(): Audio samples played for the player only.</summary>
        public string ApplySound;
        public string StopSound;

        /// <summary>
        /// with_cant_use_msg(): the repair kit clears it, because refusing to
        /// repair an undamaged tank must stay silent.
        /// </summary>
        public string CantUseKey = "consumables:generic/cant_use";

        /// <summary>EquipmentBase.nations_whitelist, empty means every nation.</summary>
        public Nation[] Nations = AnyNation;

        /// <summary>EquipmentBase.can_be_used_on_nation.</summary>
        public bool CanBeUsedOn(Nation nation)
        {
            if (Nations == null || Nations.Length == 0) { return true; }
            for (var i = 0; i < Nations.Length; i++)
            {
                if (Nations[i] == nation) { return true; }
            }

            return false;
        }
    }

    /// <summary>
    /// What the PASSIVE consumables of a loadout do to the tank card, in the
    /// same shape as <see cref="EquipmentModifiers"/>.
    /// </summary>
    public sealed class ConsumableModifiers
    {
        public float Reload = 1f;
        public float AimTime = 1f;
        public float EnginePower = 1f;
        public float TrackRotation = 1f;
        public float TurretRotation = 1f;
        public float SpeedForward;
        public float SpeedBack;
    }

    /// <summary>
    /// TanksDataBase.consumables_list in registration order: the four items
    /// new_consumable() registers while the members initialise, then the four
    /// nation rations init_equipment() adds. jet_jump is deliberately absent,
    /// exactly like in the source, because it is a mode ability rather than a
    /// mounted consumable.
    /// </summary>
    public static class ConsumableCatalog
    {
        /// <summary>TanksDataBase.cost_by_lvl_consumables.</summary>
        public static readonly int[] CostByLevel =
        {
            500, 750, 1000, 1250, 1500,
            1750, 2000, 2250, 2500, 2750,
            // Tier XI continues the flat +250 step of the levels before it.
            3000,
        };

        /// <summary>Global.MAX_CONSUMABLES: the three battle slots.</summary>
        public const int MaxActive = 3;

        public const string RepairKit = "repair_kit";
        public const string Berserk = "berserk";
        public const string Afterburning = "afterburning";
        public const string Fuel = "fuel";
        public const string RationUssr = "ration_ussr";

        public static readonly ConsumableInfo[] All =
        {
            new ConsumableInfo
            {
                Id = RepairKit,
                NameKey = "consumables:repair_kit/name",
                DescriptionKey = "consumables:repair_kit/desc",
                IconRegion = "consumable_repair_kit",
                Cooldown = 45,
                UseTime = 0,
                Type = ConsumableType.Reusable,
                ApplySound = "repair_06",
                CantUseKey = string.Empty,
            },
            new ConsumableInfo
            {
                Id = Berserk,
                NameKey = "consumables:berserk/name",
                DescriptionKey = "consumables:berserk/desc",
                IconRegion = "consumable_berserk",
                Cooldown = 90,
                UseTime = 30,
                Type = ConsumableType.Reusable,
                ApplySound = "adrenalin",
                StopSound = "adrenalin_off",
            },
            new ConsumableInfo
            {
                Id = Afterburning,
                NameKey = "consumables:afterburning/name",
                DescriptionKey = "consumables:afterburning/desc",
                IconRegion = "consumable_afterburning",
                Cooldown = 90,
                UseTime = 30,
                Type = ConsumableType.Reusable,
                ApplySound = "furious",
                StopSound = "furious_off",
            },
            new ConsumableInfo
            {
                Id = Fuel,
                NameKey = "consumables:fuel/name",
                DescriptionKey = "consumables:fuel/desc",
                IconRegion = "consumable_fuel",
                Cooldown = 0,
                Type = ConsumableType.Passive,
            },
            new ConsumableInfo
            {
                Id = RationUssr,
                NameKey = "consumables:ration_ussr/name",
                DescriptionKey = "consumables:ration_ussr/desc",
                IconRegion = "consumable_ration_ussr",
                Cooldown = 0,
                Type = ConsumableType.Passive,
                Nations = new[] { Nation.Ussr },
            },
            Ration("choc_bar", "ration_ger", "consumable_choc_bar", Nation.Germany),
            Ration("coca_cola", "ration_usa", "consumable_coca_cola", Nation.Usa),
            Ration("teapudding", "ration_gb", "consumable_teapudding", Nation.Gb),
            Ration("onigiri", "ration_jp", "consumable_onigiri", Nation.Japan),
        };

        public static ConsumableInfo Get(string id)
        {
            if (string.IsNullOrEmpty(id)) { return null; }
            for (var i = 0; i < All.Length; i++)
            {
                if (string.Equals(All[i].Id, id, StringComparison.Ordinal))
                {
                    return All[i];
                }
            }

            return null;
        }

        /// <summary>The nation-filtered list the hangar grid draws.</summary>
        public static List<ConsumableInfo> For(Nation nation)
        {
            var list = new List<ConsumableInfo>();
            for (var i = 0; i < All.Length; i++)
            {
                if (All[i].CanBeUsedOn(nation)) { list.Add(All[i]); }
            }

            return list;
        }

        /// <summary>get_equipment_cost_by_id for the consumables list.</summary>
        public static int Cost(string id, int level)
        {
            var item = Get(id);
            if (item == null) { return 0; }
            var index = Math.Max(0, Math.Min(CostByLevel.Length - 1, level - 1));
            return (int)(CostByLevel[index] * item.CostFactor);
        }

        /// <summary>
        /// add_consumable() applies a PASSIVE item the moment it is mounted,
        /// so the mounted rations and the improved fuel are folded into the
        /// tank card the same way equipment is.
        /// </summary>
        public static ConsumableModifiers Passive(IReadOnlyList<string> mounted)
        {
            var result = new ConsumableModifiers();
            if (mounted == null) { return result; }

            var used = 0;
            for (var i = 0; i < mounted.Count; i++)
            {
                var item = Get(mounted[i]);
                if (item == null) { continue; }

                // Global.MAX_CONSUMABLES caps what reaches the tank at all.
                if (used >= MaxActive) { break; }
                used++;
                if (item.Type != ConsumableType.Passive) { continue; }

                if (string.Equals(item.Id, Fuel, StringComparison.Ordinal))
                {
                    result.EnginePower *= 1.1f;
                    result.TrackRotation *= 1.1f;
                    result.TurretRotation *= 1.1f;
                    result.SpeedForward += 3f;
                    result.SpeedBack += 1f;
                    continue;
                }

                // Every ration is apply_crew_mastery_bonus(0.9).
                result.Reload *= 0.9f;
                result.AimTime *= 0.9f;
            }

            return result;
        }

        private static ConsumableInfo Ration(
            string id, string key, string icon, Nation nation)
        {
            // Consumable.copy_from() carries the behaviour of ration_ussr and
            // set_main_info() replaces the id, the strings and the artwork.
            return new ConsumableInfo
            {
                Id = id,
                NameKey = "consumables:" + key + "/name",
                DescriptionKey = "consumables:ration/desc",
                IconRegion = icon,
                Cooldown = 0,
                Type = ConsumableType.Passive,
                Nations = new[] { nation },
            };
        }
    }
}
