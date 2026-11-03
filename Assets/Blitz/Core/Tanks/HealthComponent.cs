using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// Port of Godot <c>HealthComponent</c>. The signals become plain C#
    /// events, so a health pool works without a scene node and the headless
    /// tests can drive it directly.
    /// </summary>
    public sealed class HealthComponent
    {
        /// <summary><c>on_damage_block</c>, carrying the damage armour ate.</summary>
        public event Action<int> OnDamageBlock;

        /// <summary><c>on_damage_taken</c>.</summary>
        public event Action<int> OnDamageTaken;

        /// <summary><c>on_damage_info_taken</c>.</summary>
        public event Action<DamageInfo> OnDamageInfoTaken;

        /// <summary><c>health_changed</c>.</summary>
        public event Action<int> HealthChanged;

        /// <summary><c>on_death</c>.</summary>
        public event Action OnDeath;

        /// <summary><c>damage_infos_taken</c>, the log every hit lands in.</summary>
        public readonly List<DamageInfo> DamageInfosTaken = new List<DamageInfo>();

        /// <summary><c>health</c>.</summary>
        // Obscured: the displayed HP is not findable by a memory scanner.
        private Security.ObscuredInt _health = 100;

        public int Health
        {
            get { return _health.Get("tank.health"); }
            set { _health = value; }
        }

        private Security.ObscuredInt _maxHealth = 100;

        /// <summary>
        /// <c>max_health</c>. The Godot setter refills the pool as well, so
        /// assigning a maximum is also how a tank starts a battle at full
        /// health.
        /// </summary>
        public int MaxHealth
        {
            get { return _maxHealth; }
            set
            {
                _maxHealth = value;
                Health = value;
                HealthChanged?.Invoke(Health);
            }
        }

        /// <summary><c>dead</c>.</summary>
        public bool Dead
        {
            get { return Health <= 0; }
        }

        /// <summary><c>receive_damage_basic</c>.</summary>
        public void ReceiveDamageBasic(int dmg)
        {
            if (dmg >= Health)
            {
                OnDeath?.Invoke();
                Health = 0;

                // Faithful to the source: health is already zero by the time it
                // clamps, so the killing blow reports no damage taken and the
                // kill feed reads the death signal instead.
                OnDamageTaken?.Invoke(Clamp(dmg, 0, Health));
                HealthChanged?.Invoke(Health);
                return;
            }

            Health = Clamp(Health - dmg, 0, _maxHealth);
            HealthChanged?.Invoke(Health);

            // Also faithful: the source reports against the health that is
            // left, not against the health the hit started from.
            OnDamageTaken?.Invoke(Math.Min(dmg, Health));
        }

        /// <summary><c>receive_damage</c>.</summary>
        public void ReceiveDamage(DamageInfo dinfo)
        {
            if (Dead)
            {
                return;
            }

            if (dinfo == null || dinfo.IsNull())
            {
                return;
            }

            DamageInfosTaken.Add(dinfo);
            OnDamageInfoTaken?.Invoke(dinfo);

            if (dinfo.IsBlock())
            {
                OnDamageBlock?.Invoke(dinfo.DmgBase);
                return;
            }

            ReceiveDamageBasic(dinfo.Dmg);
        }

        /// <summary>Explicit heal/rewind operation: never emits a damage or death event.</summary>
        public void Restore(int health)
        {
            Health = Clamp(health, 1, _maxHealth);
            HealthChanged?.Invoke(Health);
        }

        /// <summary>Godot's <c>clampi</c>; Core cannot reach the engine's Mathf.</summary>
        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }
    }
}
