using BlitzLegacy.Core.Maps;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>Port of Godot <c>DamageInfo.DamageType</c>.</summary>
    public enum DamageType
    {
        Shell = 0,
        Explosion = 1,
        Collision = 2,
        Fire = 3,
        Ability = 4,
        Environment = 5,
        /// <summary>Blitz 4.8.0 <c>ATTACK_REASONS_DROWNING</c>: the flooding countdown ran out.</summary>
        Drowning = 6,
    }

    /// <summary>
    /// Port of Godot <c>DamageInfo</c>: one damage event travelling from the
    /// source that dealt it to the health pool that takes it.
    /// </summary>
    public sealed class DamageInfo
    {
        /// <summary>
        /// <c>attacker</c> and <c>attacker_tank</c>. The player and tank types
        /// live above this assembly, so attribution rides along untyped and the
        /// battle code casts it back.
        /// </summary>
        public object Attacker;

        /// <summary>The tank credited with the damage.</summary>
        public object AttackerTank;

        /// <summary>Damage that landed, which is zero for a blocked hit.</summary>
        public int Dmg;

        /// <summary>Damage the source rolled before armour took its cut.</summary>
        public int DmgBase;

        /// <summary><c>dmg_type</c>.</summary>
        public DamageType DmgType = DamageType.Shell;

        /// <summary><c>shell_type</c>.</summary>
        public ShellType Shell = ShellType.Ap;

        /// <summary>
        /// <c>shot_id</c>: instance id of the shell that dealt this damage, 0
        /// for ramming, fire and other sources that do not spend a shell. One
        /// high-explosive shell can damage several tanks, so hit counters use
        /// this to count a shell only once.
        /// </summary>
        public int ShotId;

        // Launch-time Big Boss policy; defaults leave every other mode unchanged.
        public float BigBossDamageFactor = 1f;
        public float BigBossPoisonPercent, BigBossPoisonDelay;
        public int BigBossPoisonTicks, BigBossLifeSerial;
        public bool BigBossPoisonTick;

        /// <summary>
        /// Where the hit landed, for markers and sounds. Core carries no engine
        /// references, so this is the plain vector the ported map code uses.
        /// </summary>
        public Vec3 Pos;

        /// <summary>
        /// <c>suppress_feedback</c>. Periodic effects still carry attacker
        /// attribution and can kill, but may opt out of shell-impact
        /// presentation such as the red direction marker and the
        /// inside-the-tank penetration sound.
        /// </summary>
        public bool SuppressFeedback;

        /// <summary>
        /// The hit still lands on a team-mate but is never charged to the
        /// attacker as team damage, e.g. the ally a team-mate rammed into.
        /// </summary>
        public bool NotTeamDamage;

        /// <summary><c>should_have_marker</c>.</summary>
        public bool ShouldHaveMarker()
        {
            return !SuppressFeedback
                && (DmgType == DamageType.Shell
                    || DmgType == DamageType.Explosion
                    || DmgType == DamageType.Ability);
        }

        /// <summary><c>is_null</c>: nothing landed and nothing was rolled.</summary>
        public bool IsNull()
        {
            return Dmg == 0 && DmgBase == 0;
        }

        /// <summary><c>is_block</c>: a shell arrived and armour ate all of it.</summary>
        public bool IsBlock()
        {
            return ShouldHaveMarker() && Dmg == 0;
        }
    }
}
