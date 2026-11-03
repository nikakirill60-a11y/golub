using System;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>What use_try() did with the press.</summary>
    public enum ConsumableUse
    {
        /// <summary>The effect ran and the cooldown started.</summary>
        Used,

        /// <summary>A PASSIVE item: applied once, never pressed again.</summary>
        Passive,

        /// <summary>consumables:generic/cant_use, a spent SINGLE_USE item.</summary>
        CantUse,

        /// <summary>consumables:generic/already_in_use.</summary>
        AlreadyInUse,

        /// <summary>consumables:generic/on_cooldown.</summary>
        OnCooldown,

        /// <summary>on_apply returned false, e.g. an undamaged tank.</summary>
        Refused,
    }

    /// <summary>
    /// base_tank.gd's inner class <c>ConsumableActive</c>: one mounted
    /// consumable of one tank, with its use window and its cooldown.
    ///
    /// The source drives the two deadlines with Util.timer_curtime, which is
    /// battle time rather than wall time, so this port keeps the same shape
    /// and lets the owner feed it that clock through <see cref="Tick"/>.
    /// </summary>
    public sealed class ConsumableRuntime
    {
        public const string CantUseKey = "consumables:generic/cant_use";
        public const string AlreadyInUseKey = "consumables:generic/already_in_use";
        public const string OnCooldownKey = "consumables:generic/on_cooldown";

        private bool _stopPending;
        private bool _cooldownPending;

        public ConsumableRuntime(ConsumableInfo info)
        {
            Info = info;
            LastUse = -1f;
        }

        public ConsumableInfo Info { get; private set; }

        /// <summary>last_use, negative until the item has been used once.</summary>
        public float LastUse { get { return _lastUse.Get("consumable.last_use"); } private set { _lastUse = value; } }

        // Obscured: freezing last_use would skip the cooldown.
        private Security.ObscuredFloat _lastUse;

        /// <summary>cons_ref._on_apply: false refuses and burns nothing.</summary>
        public Func<bool> Apply;

        /// <summary>cons_ref._on_stop_use.</summary>
        public Action Stop;

        public Action Used;
        public Action StoppedUse;
        public Action CooldownEnd;

        /// <summary>tank.send_player_message.</summary>
        public Action<string> Message;

        public bool Passive
        {
            get { return Info != null && Info.Type == ConsumableType.Passive; }
        }

        public ConsumableRuntime Reset()
        {
            LastUse = -1f;
            _stopPending = false;
            _cooldownPending = false;
            return this;
        }

        /// <summary>is_active(): a PASSIVE item always counts as running.</summary>
        public bool IsActive(float now)
        {
            if (Info == null) { return false; }
            switch (Info.Type)
            {
                case ConsumableType.Reusable:
                    return LastUse > 0f && now < LastUse + Info.UseTime;
                case ConsumableType.Passive:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>use_try(force).</summary>
        public ConsumableUse UseTry(float now, bool force)
        {
            if (Info == null) { return ConsumableUse.CantUse; }

            if (!force && LastUse >= 0f)
            {
                if (Info.Type == ConsumableType.SingleUse)
                {
                    Send(CantUseKey);
                    return ConsumableUse.CantUse;
                }

                if (Info.Type == ConsumableType.Reusable)
                {
                    if (now < LastUse + Info.UseTime)
                    {
                        Send(AlreadyInUseKey);
                        return ConsumableUse.AlreadyInUse;
                    }

                    if (now < LastUse + Info.UseTime + Info.Cooldown)
                    {
                        Send(OnCooldownKey);
                        return ConsumableUse.OnCooldown;
                    }
                }
            }

            var applied = Apply == null || Apply();

            // A PASSIVE item never starts a window and never sets last_use:
            // the source only announces it so the battle icon lights up.
            if (Passive)
            {
                Raise(Used);
                return ConsumableUse.Passive;
            }

            if (!applied)
            {
                Send(Info.CantUseKey);
                return ConsumableUse.Refused;
            }

            Raise(Used);
            _stopPending = true;
            _cooldownPending = true;
            LastUse = now;
            return ConsumableUse.Used;
        }

        /// <summary>The two Util.timer_curtime deadlines of use_try().</summary>
        public void Tick(float now)
        {
            if (Info == null || LastUse < 0f) { return; }

            if (_stopPending && now >= LastUse + Info.UseTime)
            {
                _stopPending = false;
                if (Stop != null) { Stop(); }
                Raise(StoppedUse);
            }

            if (_cooldownPending && now >= LastUse + Info.UseTime + Info.Cooldown)
            {
                _cooldownPending = false;
                Raise(CooldownEnd);
            }
        }

        /// <summary>
        /// ui_consumable_icon._physics_process: while the effect runs the
        /// label counts the use window down, afterwards the cooldown. The
        /// source assigns the float to an int, which truncates.
        /// </summary>
        public int TimerSeconds(float now)
        {
            if (Info == null || LastUse < 0f) { return 0; }

            if (IsActive(now))
            {
                return (int)(Info.UseTime - (now - LastUse) + 1f);
            }

            return (int)(Info.Cooldown - (now - LastUse - Info.UseTime) + 1f);
        }

        /// <summary>Whether a press would be accepted right now.</summary>
        public bool Ready(float now)
        {
            if (Info == null || Passive) { return false; }
            if (LastUse < 0f) { return true; }
            if (Info.Type == ConsumableType.SingleUse) { return false; }
            return now >= LastUse + Info.UseTime + Info.Cooldown;
        }

        private void Send(string key)
        {
            if (Message != null && !string.IsNullOrEmpty(key)) { Message(key); }
        }

        private static void Raise(Action action)
        {
            if (action != null) { action(); }
        }
    }
}
