using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// Port of <c>ai_state.gd</c> (<c>AIState</c>): the base a bot state runs
    /// from, a tick every physics frame and a think on a jittered delay.
    /// </summary>
    public abstract class AiState
    {
        /// <summary><c>get_think_delay</c>.</summary>
        public const float DefaultThinkDelay = 0.8f;

        protected AiTankController Ai;

        /// <summary><c>enter(ai)</c>.</summary>
        public virtual void Enter(AiTankController ai)
        {
            Ai = ai;
        }

        /// <summary><c>exit</c>.</summary>
        public virtual void Exit()
        {
        }

        /// <summary><c>get_think_delay</c>.</summary>
        public virtual float ThinkDelay()
        {
            return DefaultThinkDelay;
        }

        /// <summary>
        /// <c>_update(delta)</c>. The source passes its random range reversed,
        /// as <c>randf_range(0.5, 0.1)</c>, which still lerps over the same
        /// interval, so a think lands every 0.1 to 0.5 of the think delay.
        /// </summary>
        public void Update(float delta)
        {
            if (Ai == null || Ai.Tank == null)
            {
                return;
            }

            Tick(delta);
            if (Ai.DelayElapsedSet("next_think_state", Random.Range(0.1f, 0.5f) * ThinkDelay()))
            {
                Think();
            }
        }

        /// <summary><c>think</c>.</summary>
        protected virtual void Think()
        {
        }

        /// <summary><c>tick(delta)</c>.</summary>
        protected virtual void Tick(float delta)
        {
        }

        /// <summary><c>tank()</c>.</summary>
        protected BattleTank Tank()
        {
            return Ai != null ? Ai.Tank : null;
        }
    }
}
