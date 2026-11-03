using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;

namespace BlitzLegacy.Core.Battle
{
    public struct BotLoadout
    {
        public int Gun;
        public int Engine;
        public int Tracks;

        /// <summary>
        /// Turrets do not exist in <c>tank_select_modules</c>, because the Godot
        /// cards had none. A bot rolls its turret the same way it rolls the
        /// other modules and then keeps a gun that turret can carry.
        /// </summary>
        public int Turret;
        public int[] Ammo;
    }

    /// <summary>Port of _battle.gd::tank_select_modules and bot ammo fill.</summary>
    public static class BotLoadoutSelector
    {
        public static BotLoadout Select(
            TankInfo card, AiSkillLevel skill, IRandomSource random)
        {
            if (card == null) { throw new ArgumentNullException("card"); }
            if (random == null) { throw new ArgumentNullException("random"); }

            var loadout = new BotLoadout
            {
                Engine = Index(random, card.Engines.Count),
                Tracks = Index(random, card.Tracks.Count),
                // Everything the source rolls is a stock-to-top index, so the
                // turret follows the same rule: the top one unless the 15%
                // branch rolls the whole loadout.
                Turret = Math.Max(card.Turrets.Count - 1, 0),
            };

            if (random.Chance(15f))
            {
                loadout.Gun = Index(random, card.Guns.Count);
                loadout.Engine = Index(random, card.Engines.Count);
                loadout.Tracks = Index(random, card.Tracks.Count);
                loadout.Turret = Index(random, card.Turrets.Count);
            }
            else
            {
                loadout.Gun = BestGun(card.Guns, skill, random);
            }

            // A gun the rolled turret cannot mount falls back to that turret's
            // own top gun, exactly like installing it in the hangar does.
            loadout.Gun = card.GunIdFor(loadout.Turret, loadout.Gun);
            var gun = card.Guns.Count == 0
                ? null
                : card.GunFor(loadout.Turret, loadout.Gun);
            loadout.Ammo = AmmoFor(gun);
            return loadout;
        }

        public static int[] AmmoFor(GunInfo gun)
        {
            var count = gun == null ? 0 : gun.Shells.Count;
            var ammo = new int[count];
            if (gun == null || count == 0) { return ammo; }
            if (count == 1) { ammo[0] = gun.MaxAmmo; }
            else if (count == 2)
            {
                ammo[0] = (int)(gun.MaxAmmo * 0.75f);
                ammo[1] = (int)(gun.MaxAmmo * 0.25f);
            }
            else if (count == 3)
            {
                ammo[0] = (int)(gun.MaxAmmo * 0.4f);
                ammo[1] = (int)(gun.MaxAmmo * 0.2f);
                ammo[2] = (int)(gun.MaxAmmo * 0.4f);
            }
            return ammo;
        }

        private static int BestGun(
            IReadOnlyList<GunInfo> source, AiSkillLevel skill, IRandomSource random)
        {
            if (source == null || source.Count == 0) { return 0; }
            var guns = new List<GunInfo>(source);
            random.Shuffle(guns);
            var points = new List<int>(guns.Count);
            for (var pointer = 0; pointer < guns.Count; pointer++)
            {
                var gun = guns[pointer];
                var bestDamage = Damage(gun);
                var bestDpm = Dpm(gun);
                var bestPen = Penetration(gun);
                var bestAccuracy = gun.Dispersion;
                for (var i = 0; i < source.Count; i++)
                {
                    var other = source[i];
                    bestDamage = Math.Max(bestDamage, Damage(other));
                    bestDpm = Math.Max(bestDpm, Dpm(other));
                    bestPen = Math.Max(bestPen, Penetration(other));
                    bestAccuracy = Math.Max(bestAccuracy, other.Dispersion);
                }

                var value = 0;
                var damageBest = bestDamage == Damage(gun);
                var dpmBest = bestDpm == Dpm(gun);
                var lowSkill = skill <= AiSkillLevel.GreenArmy;
                if (damageBest && !dpmBest) { value += lowSkill ? 15 : 5; }
                if (dpmBest && !damageBest) { value += lowSkill ? 10 : 15; }
                if (bestPen == Penetration(gun)) { value += lowSkill ? 5 : 10; }
                // Source compares `best_accuracy == g.disp` after taking max(disp).
                if (bestAccuracy == gun.Dispersion) { value += lowSkill ? 2 : 15; }
                if (value < 10 && random.Chance(25f) && pointer < source.Count - 1)
                {
                    value += 50;
                }
                points.Add(value);
            }

            var best = 0;
            var selected = 0;
            for (var i = 0; i < points.Count; i++)
            {
                if (points[i] < best) { continue; }
                selected = i; // Deliberately the shuffled position: source does this.
                best = points[i];
            }
            return selected;
        }

        private static int Index(IRandomSource random, int count)
        {
            return count <= 1 ? 0 : random.RangeInclusive(0, count - 1);
        }

        private static float Damage(GunInfo gun)
        {
            return gun == null || gun.Shells.Count == 0 ? 0f : gun.Shells[0].Damage;
        }

        private static float Penetration(GunInfo gun)
        {
            return gun == null || gun.Shells.Count == 0 ? 0f : gun.Shells[0].Penetration;
        }

        private static float Dpm(GunInfo gun)
        {
            if (gun == null || gun.Reload <= 0f) { return 0f; }
            var rate = 60f / gun.Reload;
            if ((gun.FireMode == 1 || gun.FireMode == 2) && gun.Burst.Bursts.Count > 0)
            {
                var shots = 0;
                var unload = 0f;
                for (var i = 0; i < gun.Burst.Bursts.Count; i++)
                {
                    var burst = gun.Burst.Bursts[i];
                    if (shots > 0 && burst > 1) { unload += burst * gun.Burst.IntraBurst; }
                    shots += burst;
                }
                unload += (gun.Burst.Bursts.Count - 1) * gun.Burst.IntraClip;
                rate = unload + gun.Reload <= 0f ? 0f
                    : (60f / (unload + gun.Reload)) * shots;
            }
            rate = (float)Math.Round(rate, 2, MidpointRounding.AwayFromZero);
            return rate * Damage(gun);
        }
    }
}
