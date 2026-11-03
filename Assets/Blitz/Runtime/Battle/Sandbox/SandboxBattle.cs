using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Sandbox;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The rules of one running Sandbox battle (Godot Global.is_training_mode()
    /// with prepare_battle_rules applied). Built from a validated copy of the
    /// active preset, so editing presets while a battle runs changes nothing.
    /// Only ever active in an offline session: activating it on a networked one
    /// throws, and <see cref="Current"/> is cleared when that session goes.
    /// </summary>
    public sealed class SandboxBattle
    {
        /// <summary>The Sandbox of the battle that is running now, or null.</summary>
        public static SandboxBattle Current { get; private set; }

        public readonly SandboxConfig Config;
        public readonly UprisingRules UprisingRules;

        public BattleSession Session { get; private set; }

        /// <summary>Roster actually seated, after the map's spawn limits.</summary>
        public SandboxRosterPlan Plan { get; internal set; }

        public SandboxBattle(SandboxConfig config)
        {
            Config = config != null ? config.Copy() : new SandboxConfig();
            Config.Validate();
            UprisingRules = SandboxTuning.Uprising(Config);
        }

        /// <summary>Rule set the battle runs: Standard, Gravity, Uprising or BIG BOSS.</summary>
        public BattleMode BaseMode { get { return SandboxTuning.BaseMode(Config); } }

        public float DamageDeviation { get { return Config.DamageDeviation; } }
        public float PenetrationDeviation { get { return Config.PenetrationDeviation; } }
        public float ShellGravity { get { return Config.ProjectileGravityMul; } }
        public bool FriendlyFire { get { return Config.FriendlyFire; } }

        /// <summary>Roster time limit in seconds; 0 runs without a limit.</summary>
        public float TimeLimitSeconds { get { return Config.BattleTimeSeconds; } }

        public float GravitySpeed { get { return Config.GravitySpeedMul; } }
        public float GravityShotPower { get { return Config.GravityShotPowerMul; } }
        public float GravityJumpPower { get { return Config.GravityJumpPowerMul; } }
        public float GravityGravity { get { return Config.GravityGravityMul; } }

        internal void Activate(BattleSession session)
        {
            if (session == null) { throw new System.ArgumentNullException("session"); }
            if (session.Net != null) { throw new SandboxOnlineException("session"); }
            Session = session;
            Current = this;
            Debug.Log("[Blitz] sandbox active: base=" + BattleModeCatalog.Id(BaseMode)
                + " allies=" + Config.AlliedTeamSize + " enemies=" + Config.EnemyTeamSize
                + " time=" + TimeLimitSeconds + " ff=" + (FriendlyFire ? "on" : "off")
                + (BaseMode == BattleMode.BigBoss
                    ? " boss_lives=" + Config.BigBossLives + " team_points=" + Config.BigBossTeamPoints
                        + " buff=" + Config.BigBossBuffMul.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty));
        }

        internal void Deactivate()
        {
            if (Current == this) { Current = null; }
        }

        /// <summary>True while an offline Sandbox battle runs.</summary>
        public static bool Active
        {
            get
            {
                var current = Current;
                return current != null && current.Session != null && current.Session.Net == null;
            }
        }

        private static SandboxBattle Live { get { return Active ? Current : null; } }

        public static bool FriendlyFireOn { get { var s = Live; return s != null && s.FriendlyFire; } }

        /// <summary>A direct shell hit on a live team-mate is swallowed without damage.</summary>
        public static bool BlocksFriendlyHit(BattleTank owner, BattleTank target)
        {
            var s = Live;
            return s != null && !s.FriendlyFire && owner != null && target != null
                && target != owner && !target.Dead && target.Team == owner.Team;
        }

        public static bool InfiniteAmmo(BattleTank tank)
        {
            var s = Live;
            return s != null && s.Config.CheatInfiniteAmmo && tank != null && tank == s.Session.Player;
        }

        /// <summary>training_cheat_no_recoil(): the preset switch disables every shot's kick.</summary>
        public static bool NoRecoil { get { var s = Live; return s != null && s.Config.CheatNoRecoil; } }

        public static float DamageDeviationOr(float fallback) { var s = Live; return s != null ? s.DamageDeviation : fallback; }
        public static float PenetrationDeviationOr(float fallback) { var s = Live; return s != null ? s.PenetrationDeviation : fallback; }
        public static float ShellGravityOr(float fallback) { var s = Live; return s != null ? s.ShellGravity : fallback; }
    }
}
