using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>How the mode slot key reads this frame.</summary>
    public enum BattleModeAbilityPhase
    {
        /// <summary>No ability right now (a Big Boss role not yet assigned): the empty key.</summary>
        Hidden = 0,
        Ready,
        /// <summary>Ready, but nothing valid to use it on (a targeted Uprising active).</summary>
        NoTarget,
        Cooling,
        /// <summary>The effect runs, or is armed and waits for its trigger.</summary>
        Active,
    }

    /// <summary>One read of the mode slot: phase, art and the countdown on the key.</summary>
    public struct BattleModeAbilityState
    {
        public BattleModeAbilityPhase Phase;

        /// <summary>Resources path of the key art.</summary>
        public string Icon;

        /// <summary>Drawn size of the art; zero keeps the texture's own size.</summary>
        public Vector2 IconSize;

        /// <summary>Seconds the key counts down, 0 for none.</summary>
        public float Seconds;
    }

    /// <summary>
    /// The battle-mode ability behind the fourth consumable slot: the Uprising active, the
    /// Big Boss role's button ability or the Gravity jet jump. The slot polls
    /// <see cref="Read"/> every physics tick and presses <see cref="Use"/> behind the bar's
    /// press gate, from the key and from Digit7 alike.
    /// </summary>
    public interface IBattleModeAbility
    {
        BattleModeAbilityState Read(float now);

        bool Use();
    }
}
