using System;
using BlitzLegacy.Core.Battle;

namespace BlitzLegacy.Core.Sandbox
{
    /// <summary>
    /// Final per-tank factors of one Sandbox seat: the global layer of the preset
    /// combined with the player's or the bot slot's own layer
    /// (TrainingModeConfig.combined in base_tank.apply_training_overrides).
    /// </summary>
    public struct SandboxTankFactors
    {
        public float Hp, Damage, Penetration, Reload, MagazineReload, Intraclip, Speed, Engine, AimTime, TurnRate;

        public static readonly SandboxTankFactors Neutral = new SandboxTankFactors
        {
            Hp = 1f, Damage = 1f, Penetration = 1f, Reload = 1f, MagazineReload = 1f,
            Intraclip = 1f, Speed = 1f, Engine = 1f, AimTime = 1f, TurnRate = 1f,
        };

        /// <param name="local">The seat's own layer; null for an unconfigured bot.</param>
        public static SandboxTankFactors For(SandboxConfig config, SandboxMultipliers local)
        {
            if (config == null) { return Neutral; }
            var g = config.Global;
            return new SandboxTankFactors
            {
                Hp = Combine(g, local, SandboxStat.Hp),
                Damage = Combine(g, local, SandboxStat.Damage),
                Penetration = Combine(g, local, SandboxStat.Penetration),
                Reload = Combine(g, local, SandboxStat.Reload),
                MagazineReload = Combine(g, local, SandboxStat.MagazineReload),
                Intraclip = Combine(g, local, SandboxStat.Intraclip),
                Speed = Combine(g, local, SandboxStat.Speed),
                Engine = Combine(g, local, SandboxStat.Engine),
                AimTime = Combine(g, local, SandboxStat.AimTime),
                TurnRate = Combine(g, local, SandboxStat.TurnRate),
            };
        }

        private static float Combine(SandboxMultipliers global, SandboxMultipliers local, SandboxStat stat)
        {
            return SandboxMultiplierMath.Combine(global[stat], local == null ? 1f : local[stat]);
        }
    }

    /// <summary>
    /// The arithmetic of base_tank.apply_training_overrides and receive_damage,
    /// on plain numbers so the battle code and the tests share one rule set.
    /// Godot roundi rounds half away from zero.
    /// </summary>
    public static class SandboxTuning
    {
        public const float MinReload = 0.05f;
        public const float MinTiming = 0.01f;
        public const float MinTurnRate = 1f;

        public static int Round(double value)
        {
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        /// <summary>maxi(1, roundi(health * hp)).</summary>
        public static int Health(int baseHealth, float factor)
        {
            return Math.Max(1, Round((double)baseHealth * factor));
        }

        /// <summary>shell.pen = maxi(1, roundi(shell.pen * factor)).</summary>
        public static float Penetration(float penetration, float factor)
        {
            return Math.Max(1, Round((double)penetration * factor));
        }

        /// <summary>gun_info.reload = maxf(0.05, reload * factor).</summary>
        public static float Reload(float seconds, float factor)
        {
            return Math.Max(MinReload, seconds * factor);
        }

        /// <summary>Aim time, intraclip and intraburst: maxf(0.01, value * factor).</summary>
        public static float Timing(float seconds, float factor)
        {
            return Math.Max(MinTiming, seconds * factor);
        }

        /// <summary>Speed caps and engine power: maxi(1, roundi(value * factor)).</summary>
        public static float Mobility(float value, float factor)
        {
            return Math.Max(1, Round((double)value * factor));
        }

        /// <summary>Hull traverse in degrees per second: maxf(1, rate * factor). Not in Godot.</summary>
        public static float TurnRate(float degreesPerSecond, float factor)
        {
            return Math.Max(MinTurnRate, degreesPerSecond * factor);
        }

        /// <summary>
        /// receive_damage: the attacker's outgoing multiplier on a hit record,
        /// skipped when it is (approximately) neutral.
        /// </summary>
        public static int Damage(int amount, float multiplier)
        {
            if (SandboxMultiplierMath.IsEqualApprox(multiplier, 1f)) { return amount; }
            return Math.Max(0, Round((double)amount * multiplier));
        }

        /// <summary>Base rule set the Sandbox runs on.</summary>
        public static BattleMode BaseMode(SandboxConfig config)
        {
            if (config == null) { return BattleMode.Normal; }
            switch (config.BaseMode)
            {
                case SandboxBaseMode.Gravity: return BattleMode.Gravity;
                case SandboxBaseMode.Uprising: return BattleMode.Uprising;
                case SandboxBaseMode.BigBoss: return BattleMode.BigBoss;
                default: return BattleMode.Normal;
            }
        }

        /// <summary>Uprising economy of the preset (Global.uprising_* in Training).</summary>
        public static UprisingRules Uprising(SandboxConfig config)
        {
            if (config == null) { return UprisingRules.Standard; }
            return new UprisingRules(
                config.UprisingLives,
                config.UprisingElixirPerSecond,
                config.UprisingElixirPerDamagingShot,
                config.UprisingElixirDamageStep,
                config.UprisingElixirPerDamageStep,
                config.UprisingElixirPerDeath,
                config.UprisingUpgradePowerMul,
                new[] { config.UprisingCostLevel1, config.UprisingCostLevel2, config.UprisingCostLevel3 });
        }

        /// <summary>
        /// BIG BOSS tables of the preset: its boss lives, starting team points
        /// and boost multiplier over the shipped 11.20 tables in <paramref name="json"/>.
        /// </summary>
        public static BigBossRules BigBoss(SandboxConfig config, string json)
        {
            if (config == null) { return new BigBossRules(json); }
            return BigBossRules.Tuned(json, config.BigBossLives, config.BigBossTeamPoints, config.BigBossBuffMul);
        }
    }
}
