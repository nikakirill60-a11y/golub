using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Deterministic tactical gate for bot active abilities.</summary>
    public static class UprisingAiDecision
    {
        public struct Context
        {
            public string Ability;
            public TankClass SelfClass;
            public TankClass TargetClass;
            public AiStateKind State;
            public float HealthFraction;
            public float Distance;
            public float ClosingSpeed;
            public float SelfSpeed;
            public float TargetSpeed;
            public float SelfArmor;
            public float TargetArmor;
            public bool HasTarget;
            public bool HasRamming;
            public bool CanPenetrate;
        }

        public static bool ShouldUse(Context c)
        {
            if (string.IsNullOrEmpty(c.Ability)) return false;
            switch (c.Ability)
            {
                case "moonwalk":
                    return c.HasTarget &&
                        ((c.HealthFraction <= .42f && c.Distance <= 140f) ||
                         (RamFavored(c) && c.Distance >= 12f && c.Distance <= 75f));
                case "pretendDead":
                    return c.HasTarget &&
                        ((c.Distance <= 24f && c.ClosingSpeed >= 5f) ||
                         (c.Distance <= 32f && c.SelfSpeed >= 8f) ||
                         (c.HealthFraction <= .22f && c.Distance <= 100f));
                case "berserk":
                    return c.HasTarget &&
                        (c.Distance <= 85f ||
                         ((c.State == AiStateKind.Brawl || c.State == AiStateKind.Single
                           || c.State == AiStateKind.Duel || c.State == AiStateKind.Chase)
                          && c.Distance <= 140f));
                case "vortex":
                    return c.HasTarget && c.Distance <= 130f &&
                        ((c.TargetClass == TankClass.Light && (c.TargetSpeed >= 7f || c.Distance <= 90f)) ||
                         !c.CanPenetrate || c.Distance <= 45f);
                case "huntersMark":
                    return c.HasTarget && c.Distance <= 250f;
                case "curse":
                    return c.HasTarget && c.Distance <= 180f && c.TargetSpeed >= 3f;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The perk a bot buys from its respawn offer: the one that pays for
        /// the way its class fights (a heavy rams and splashes, a light runs on
        /// its engine, a tank destroyer or a sniper hits harder), a higher
        /// grade or a dearer perk breaking ties (the price adds under half a
        /// point, so it never outweighs the class fit); null when nothing is
        /// affordable. The AI then plays the perks it owns (see
        /// AiTankController.HasUprisingPerk).
        /// </summary>
        public static string ChoosePerk(IList<string> offer, TankClass cls, bool sniper,
            Func<string, bool> canBuy, Func<string, int> cost)
        {
            if (offer == null) return null;
            string best = null;
            var bestScore = float.NegativeInfinity;
            for (var i = 0; i < offer.Count; i++)
            {
                var id = offer[i];
                if (string.IsNullOrEmpty(id) || (canBuy != null && !canBuy(id))) continue;
                var score = PerkScore(id, cls, sniper) + (cost != null ? Math.Min(cost(id), 999) / 2500f : 0f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = id;
                }
            }

            return best;
        }

        /// <summary>How much a perk family is worth to a class, before its price.</summary>
        public static float PerkScore(string id, TankClass cls, bool sniper)
        {
            var heavy = cls == TankClass.Heavy;
            var medium = cls == TankClass.Medium;
            var light = cls == TankClass.Light;
            var destroyer = cls == TankClass.TankDestroyer;
            switch (UprisingCatalog.Family(id))
            {
                case "damageBoost": return destroyer || sniper ? 6f : 4f;
                case "reloadBoost": return medium || destroyer ? 5f : 3f;
                case "engineBoost": return light ? 6f : heavy ? 2f : 3f;
                case "ramming": return heavy ? 6f : medium ? 2f : 0f;
                case "comeBackKit": return heavy ? 4f : 3f;
                case "ghost": return light || sniper ? 5f : 2f;
                case "duplet": return heavy || medium ? 4f : 1f;
                case "splash": return heavy ? 5f : 3f;
                case "lightning": return 3f;
                case "enemyReloadTime": return 2f;
                default: return 1f;
            }
        }

        private static bool RamFavored(Context c)
        {
            return c.HasRamming || c.SelfArmor >= c.TargetArmor * 1.15f;
        }
    }
}
