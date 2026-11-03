namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// What Gravity adds to the standard battle HUD: the jet jump in the mode slot, for every
    /// hull but the heavies, which never get one.
    /// </summary>
    public sealed class GravityHudMode : BattleHudMode, IBattleModeAbility
    {
        private readonly GravityModeTank _owner;

        public GravityHudMode(GravityModeTank owner)
        {
            _owner = owner;
        }

        public GravityModeTank Owner { get { return _owner; } }

        public override IBattleModeAbility Ability { get { return _owner.JetJump != null ? this : null; } }

        public BattleModeAbilityState Read(float now)
        {
            var jump = _owner.JetJump;
            if (jump == null) { return default(BattleModeAbilityState); }

            var phase = jump.IsActive(now) ? BattleModeAbilityPhase.Active
                : jump.Ready(now) ? BattleModeAbilityPhase.Ready
                : BattleModeAbilityPhase.Cooling;
            return new BattleModeAbilityState
            {
                Phase = phase,
                Icon = GravityModeTank.JetJumpIconResource,
                Seconds = phase == BattleModeAbilityPhase.Ready ? 0f : jump.TimerSeconds(now),
            };
        }

        public bool Use()
        {
            return _owner.UseJetJump();
        }
    }
}
