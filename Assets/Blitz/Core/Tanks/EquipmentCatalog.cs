using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>Authored equipment table from assets/data/db_tanks.gd.</summary>
    public sealed class EquipmentInfo
    {
        public string Id;
        public string NameKey;
        public string DescriptionKey;
        public string IconRegion;
        public float CostFactor = 1f;
        public float Weight = 200f;
    }

    public sealed class EquipmentModifiers
    {
        public float Reload = 1f;
        public float AimTime = 1f;
        public float Dispersion = 1f;
        public float ViewRange = 1f;
        public float AddedWeight;
    }

    public static class EquipmentCatalog
    {
        public static readonly int[] CostByLevel =
        {
            1500, 5000, 35000, 35000, 70000,
            90000, 90000, 90000, 180000, 180000,
            // Tier XI holds the tier IX/X plateau instead of silently
            // reusing it through the index clamp below.
            180000,
        };

        public static readonly EquipmentInfo[] All =
        {
            New("rammer", "equipment_rammer", 2f),
            New("vent", "equipment_vent", 2f),
            New("aimStab", "equipment_aimStab"),
            New("aimDrive", "equipment_aimDrive"),
            New("toolbox", "equipment_toolbox"),
            New("spallliner", "equipment_spallliner"),
            New("optics", "equipment_optics"),
            New("camonet", "equipment_camonet"),
            New("stereoscope", "equipment_stereoscope"),
        };

        public static EquipmentInfo Get(string id)
        {
            for (var i = 0; i < All.Length; i++)
            {
                if (string.Equals(All[i].Id, id, StringComparison.Ordinal))
                {
                    return All[i];
                }
            }
            return null;
        }

        public static int Cost(string id, int level)
        {
            var item = Get(id);
            if (item == null) { return 0; }
            var index = Math.Max(0, Math.Min(CostByLevel.Length - 1, level - 1));
            return (int)(CostByLevel[index] * item.CostFactor);
        }

        public static EquipmentModifiers Modifiers(IReadOnlyList<string> installed)
        {
            var result = new EquipmentModifiers();
            if (installed == null) { return result; }
            for (var i = 0; i < installed.Count; i++)
            {
                var item = Get(installed[i]);
                if (item == null) { continue; }
                result.AddedWeight += item.Weight;
                switch (item.Id)
                {
                    case "rammer": result.Reload *= 0.9f; break;
                    case "vent":
                        result.Reload *= 0.95f;
                        result.AimTime *= 0.95f;
                        break;
                    case "aimStab": result.Dispersion *= 0.8f; break;
                    case "aimDrive": result.AimTime *= 0.9f; break;
                    case "optics": result.ViewRange *= 1.1f; break;
                }
            }
            return result;
        }

        private static EquipmentInfo New(string id, string icon, float factor = 1f)
        {
            return new EquipmentInfo
            {
                Id = id,
                NameKey = "equipment:" + id + "/name",
                DescriptionKey = "equipment:" + id + "/desc",
                IconRegion = icon,
                CostFactor = factor,
            };
        }
    }
}
