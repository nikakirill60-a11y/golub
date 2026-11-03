using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Sandbox
{
    /// <summary>One bot seat of a Sandbox battle as _spawn_training_roster decides it.</summary>
    public sealed class SandboxSeat
    {
        /// <summary>Tank card name of the seat.</summary>
        public string TankName = string.Empty;

        /// <summary>
        /// The configured slot (an exact vehicle), or null for a random seat.
        /// Its multipliers, modules, skill and behaviour apply to the tank.
        /// </summary>
        public SandboxBotSlot Profile;

        /// <summary>Skill 0..4, or -1 for the ordinary skill roll.</summary>
        public int Skill = SandboxBotSlot.RandomSelection;
        public bool Harmless;
        public bool Stationary;
    }

    /// <summary>The whole Sandbox roster; the player is seat 0 of the allied side.</summary>
    public sealed class SandboxRosterPlan
    {
        /// <summary>True when the player takes the map's team A spawners.</summary>
        public bool PlayerOnTeamA = true;

        /// <summary>Allied team total including the player, after the map clamp.</summary>
        public int AlliedTotal;
        public int EnemyTotal;

        /// <summary>Allied bots, seats 1..AlliedTotal-1.</summary>
        public readonly List<SandboxSeat> Allies = new List<SandboxSeat>();
        public readonly List<SandboxSeat> Enemies = new List<SandboxSeat>();

        /// <summary>True when a side was shortened because the map has fewer spawners.</summary>
        public bool AlliedLimitedByMap;
        public bool EnemyLimitedByMap;
    }

    /// <summary>
    /// The engine-free half of the Sandbox branch of _battle.gd: side choice,
    /// map-limited team totals, tank picks (exact, player tier, random tier or
    /// mirrored allies), skill/behaviour defaults and the module overrides.
    /// </summary>
    public static class SandboxRosterPlanner
    {
        public const int MinTier = 1;
        public const int MaxTier = 10;

        /// <summary>The dice of start_battle(), forced by the player team choice.</summary>
        public static bool PlayerOnTeamA(SandboxConfig config, IRandomSource random)
        {
            switch (config.PlayerTeam)
            {
                case SandboxTeamChoice.TeamA: return true;
                case SandboxTeamChoice.TeamB: return false;
                default: return random.Chance(50f);
            }
        }

        public static SandboxRosterPlan Build(
            SandboxConfig config, TanksDatabase tanks, TankInfo playerCard,
            bool playerOnTeamA, int playerSpawnCount, int enemySpawnCount, IRandomSource random)
        {
            if (config == null) { throw new ArgumentNullException("config"); }
            if (tanks == null) { throw new ArgumentNullException("tanks"); }
            if (playerCard == null) { throw new ArgumentNullException("playerCard"); }
            if (random == null) { throw new ArgumentNullException("random"); }

            var plan = new SandboxRosterPlan { PlayerOnTeamA = playerOnTeamA };
            plan.AlliedTotal = Math.Min(config.AlliedTeamSize, Math.Max(0, playerSpawnCount));
            plan.EnemyTotal = Math.Min(config.EnemyTeamSize, Math.Max(0, enemySpawnCount));
            plan.AlliedLimitedByMap = plan.AlliedTotal < config.AlliedTeamSize;
            plan.EnemyLimitedByMap = plan.EnemyTotal < config.EnemyTeamSize;
            if (plan.AlliedTotal <= 0 || plan.EnemyTotal <= 0)
            {
                throw new InvalidOperationException("No usable TankSpawner for one of the Sandbox teams");
            }

            var matchmaker = new Matchmaker(tanks, random);
            var playerLevel = playerCard.Level;
            var allyNames = new List<string> { playerCard.Name };
            for (var i = 1; i < plan.AlliedTotal; i++)
            {
                var slot = config.AlliedSlots[i - 1];
                var name = PickName(config, slot, tanks, matchmaker, playerCard, random);
                allyNames.Add(name);
                plan.Allies.Add(SeatFor(config, slot, name));
            }

            for (var i = 0; i < plan.EnemyTotal; i++)
            {
                var slot = config.EnemySlots[i];
                var configured = slot != null && !slot.RandomTank;
                var mirrored = !configured && config.Balancer == SandboxBalancer.MirrorAllies && i < allyNames.Count;
                var name = mirrored ? allyNames[i] : PickName(config, slot, tanks, matchmaker, playerCard, random);
                plan.Enemies.Add(SeatFor(config, slot, name));
            }

            return plan;
        }

        private static SandboxSeat SeatFor(SandboxConfig config, SandboxBotSlot slot, string name)
        {
            var configured = slot != null && !slot.RandomTank ? slot : null;
            return new SandboxSeat
            {
                TankName = name,
                Profile = configured,
                Skill = configured != null ? configured.Skill : config.DefaultBotSkill,
                Harmless = configured != null ? configured.Harmless : config.DefaultBotsHarmless,
                Stationary = configured != null ? configured.Stationary : config.DefaultBotsStationary,
            };
        }

        /// <summary>_training_pick_name: the exact tank if it exists (a hidden event
        /// vehicle does not), else a tier roll.</summary>
        public static string PickName(
            SandboxConfig config, SandboxBotSlot slot, TanksDatabase tanks,
            Matchmaker matchmaker, TankInfo playerCard, IRandomSource random)
        {
            if (slot != null && !slot.RandomTank && tanks.HasVisible(slot.TankName))
            {
                return slot.TankName;
            }

            var tier = playerCard.Level;
            if (config.Balancer == SandboxBalancer.Random)
            {
                tier = random.RangeInclusive(MinTier, MaxTier);
            }

            var name = matchmaker.RollBotName(playerCard.Level, tier);
            // A tier with no card at all keeps the seat filled with the player's card.
            return string.IsNullOrEmpty(name) ? playerCard.Name : name;
        }

        /// <summary>
        /// _training_modules on top of the ordinary skill-based roll: a configured
        /// slot pins its gun, engine and tracks. The client authors guns per
        /// turret, so a pinned gun also takes the top turret able to mount it.
        /// The rack is refilled with the Sandbox split for the final gun.
        /// </summary>
        public static BotLoadout ApplyModules(TankInfo card, BotLoadout rolled, SandboxBotSlot profile)
        {
            if (card == null) { throw new ArgumentNullException("card"); }
            var loadout = rolled;
            if (profile != null)
            {
                if (profile.GunId != SandboxBotSlot.RandomSelection && card.Guns.Count > 0)
                {
                    loadout.Gun = SandboxMultiplierMath.Clamp(profile.GunId, 0, card.Guns.Count - 1);
                    loadout.Turret = TurretFor(card, loadout.Gun, loadout.Turret);
                }

                if (profile.EngineId != SandboxBotSlot.RandomSelection && card.Engines.Count > 0)
                {
                    loadout.Engine = SandboxMultiplierMath.Clamp(profile.EngineId, 0, card.Engines.Count - 1);
                }

                if (profile.TrackId != SandboxBotSlot.RandomSelection && card.Tracks.Count > 0)
                {
                    loadout.Tracks = SandboxMultiplierMath.Clamp(profile.TrackId, 0, card.Tracks.Count - 1);
                }
            }

            loadout.Gun = card.GunIdFor(loadout.Turret, loadout.Gun);
            loadout.Ammo = AmmoFor(card.Guns.Count == 0 ? null : card.GunFor(loadout.Turret, loadout.Gun));
            return loadout;
        }

        /// <summary>The rolled turret when it mounts the gun, else the top turret that does.</summary>
        public static int TurretFor(TankInfo card, int gun, int rolledTurret)
        {
            if (card.Turrets.Count == 0 || gun < 0 || gun >= card.Guns.Count)
            {
                return rolledTurret;
            }

            var gunName = card.Guns[gun].Name;
            var rolled = card.Turret(rolledTurret);
            if (rolled != null && (rolled.Guns.Count == 0 || rolled.CanMount(gunName)))
            {
                return rolledTurret;
            }

            for (var i = card.Turrets.Count - 1; i >= 0; i--)
            {
                if (card.Turrets[i].CanMount(gunName))
                {
                    return i;
                }
            }

            return rolledTurret;
        }

        /// <summary>
        /// _fill_training_ammo: one shell type gets the whole rack, two split
        /// 75/25 and three 40/20/rest, rounded like GDScript roundi.
        /// </summary>
        public static int[] AmmoFor(GunInfo gun)
        {
            var count = gun == null ? 0 : gun.Shells.Count;
            var ammo = new int[count];
            if (count == 0)
            {
                return ammo;
            }

            var max = gun.MaxAmmo;
            if (count == 1)
            {
                ammo[0] = max;
            }
            else if (count == 2)
            {
                ammo[0] = RoundI(max * 0.75);
                ammo[1] = max - ammo[0];
            }
            else
            {
                ammo[0] = RoundI(max * 0.4);
                ammo[1] = RoundI(max * 0.2);
                ammo[2] = max - ammo[0] - ammo[1];
            }

            return ammo;
        }

        /// <summary>
        /// _fill_player_ammo: the garage rack, copied and clamped to the
        /// installed gun, so a type left empty in the garage stays empty.
        /// </summary>
        public static int[] PlayerAmmo(GunInfo gun, IReadOnlyList<int> bought)
        {
            var count = gun == null ? 0 : gun.Shells.Count;
            var ammo = new int[count];
            for (var i = 0; i < count; i++)
            {
                var value = bought != null && i < bought.Count ? bought[i] : 0;
                ammo[i] = SandboxMultiplierMath.Clamp(value, 0, gun.MaxAmmo);
            }

            return ammo;
        }

        private static int RoundI(double value)
        {
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }
    }
}
