using BlitzLegacy.Core.Tanks;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the ports of Godot <c>DamageInfo</c> and <c>HealthComponent</c>,
    /// including the two places where the source reports damage against the
    /// health that is left rather than the health the hit started from.
    /// </summary>
    public sealed class TankDamageTests
    {
        // The MS-1 hull has 54 hit points and UBRT1 hits are worth 40.
        private const int MaxHealth = 54;
        private const int ShellDamage = 40;

        private static DamageInfo ShellHit(int dmg, int dmgBase)
        {
            return new DamageInfo
            {
                Dmg = dmg,
                DmgBase = dmgBase,
                DmgType = DamageType.Shell,
                Shell = ShellType.Ap,
                ShotId = 7,
            };
        }

        [Test]
        public void DamageInfo_MarksShellsExplosionsAndAbilities()
        {
            var shell = ShellHit(ShellDamage, ShellDamage);
            var explosion = new DamageInfo { Dmg = 5, DmgBase = 5, DmgType = DamageType.Explosion };
            var ability = new DamageInfo { Dmg = 5, DmgBase = 5, DmgType = DamageType.Ability };
            var ramming = new DamageInfo { Dmg = 5, DmgBase = 5, DmgType = DamageType.Collision };
            var fire = new DamageInfo { Dmg = 5, DmgBase = 5, DmgType = DamageType.Fire };

            Assert.That(shell.ShouldHaveMarker(), Is.True);
            Assert.That(explosion.ShouldHaveMarker(), Is.True);
            Assert.That(ability.ShouldHaveMarker(), Is.True);
            Assert.That(ramming.ShouldHaveMarker(), Is.False);
            Assert.That(fire.ShouldHaveMarker(), Is.False);
        }

        [Test]
        public void DamageInfo_SuppressedFeedbackDropsTheMarkerAndTheBlock()
        {
            var burning = new DamageInfo
            {
                Dmg = 0,
                DmgBase = ShellDamage,
                DmgType = DamageType.Shell,
                SuppressFeedback = true,
            };

            Assert.That(burning.ShouldHaveMarker(), Is.False);
            Assert.That(burning.IsBlock(), Is.False);
            Assert.That(burning.IsNull(), Is.False);
        }

        [Test]
        public void DamageInfo_IsNullOnlyWhenNothingWasRolled()
        {
            Assert.That(new DamageInfo().IsNull(), Is.True);
            Assert.That(ShellHit(0, ShellDamage).IsNull(), Is.False);
            Assert.That(ShellHit(0, ShellDamage).IsBlock(), Is.True);
            Assert.That(ShellHit(ShellDamage, ShellDamage).IsBlock(), Is.False);
        }

        [Test]
        public void MaxHealth_FillsThePoolAndAnnouncesIt()
        {
            var pool = new HealthComponent();
            var reported = 0;
            pool.HealthChanged += health => reported = health;

            pool.MaxHealth = MaxHealth;

            Assert.That(pool.Health, Is.EqualTo(MaxHealth));
            Assert.That(pool.MaxHealth, Is.EqualTo(MaxHealth));
            Assert.That(reported, Is.EqualTo(MaxHealth));
            Assert.That(pool.Dead, Is.False);
        }

        [Test]
        public void ReceiveDamage_TakesTheDamageThatLanded()
        {
            var pool = new HealthComponent { MaxHealth = MaxHealth };
            var taken = -1;
            pool.OnDamageTaken += dmg => taken = dmg;

            pool.ReceiveDamage(ShellHit(ShellDamage, ShellDamage));

            Assert.That(pool.Health, Is.EqualTo(MaxHealth - ShellDamage));
            Assert.That(pool.DamageInfosTaken.Count, Is.EqualTo(1));
            // Faithful to the source: it reports against the health that is
            // left, so a 40 point hit on 54 points announces 14.
            Assert.That(taken, Is.EqualTo(MaxHealth - ShellDamage));
        }

        [Test]
        public void ReceiveDamage_LethalHitEmptiesThePoolAndFiresDeath()
        {
            var pool = new HealthComponent { MaxHealth = MaxHealth };
            var deaths = 0;
            var taken = -1;
            pool.OnDeath += () => deaths += 1;
            pool.OnDamageTaken += dmg => taken = dmg;

            pool.ReceiveDamage(ShellHit(MaxHealth + 10, MaxHealth + 10));

            Assert.That(pool.Health, Is.EqualTo(0));
            Assert.That(pool.Dead, Is.True);
            Assert.That(deaths, Is.EqualTo(1));
            // The source clamps against the already emptied pool, so the
            // killing blow announces nothing and the kill reads off the death
            // signal instead.
            Assert.That(taken, Is.EqualTo(0));
        }

        [Test]
        public void ReceiveDamage_BlockedHitOnlyRaisesTheBlock()
        {
            var pool = new HealthComponent { MaxHealth = MaxHealth };
            var blocked = 0;
            var taken = 0;
            pool.OnDamageBlock += dmg => blocked = dmg;
            pool.OnDamageTaken += dmg => taken = dmg;

            pool.ReceiveDamage(ShellHit(0, ShellDamage));

            Assert.That(blocked, Is.EqualTo(ShellDamage));
            Assert.That(taken, Is.EqualTo(0));
            Assert.That(pool.Health, Is.EqualTo(MaxHealth));
            Assert.That(pool.DamageInfosTaken.Count, Is.EqualTo(1));
        }

        [Test]
        public void ReceiveDamage_DropsNullHitsAndIgnoresADeadPool()
        {
            var pool = new HealthComponent { MaxHealth = MaxHealth };

            pool.ReceiveDamage(new DamageInfo());

            Assert.That(pool.DamageInfosTaken.Count, Is.EqualTo(0));
            Assert.That(pool.Health, Is.EqualTo(MaxHealth));

            pool.ReceiveDamage(ShellHit(MaxHealth, MaxHealth));
            pool.ReceiveDamage(ShellHit(ShellDamage, ShellDamage));

            Assert.That(pool.Dead, Is.True);
            Assert.That(pool.DamageInfosTaken.Count, Is.EqualTo(1));
            Assert.That(pool.Health, Is.EqualTo(0));
        }

        [Test]
        public void ReceiveDamageBasic_NeverGoesBelowZero()
        {
            var pool = new HealthComponent { MaxHealth = MaxHealth };

            pool.ReceiveDamageBasic(10);
            Assert.That(pool.Health, Is.EqualTo(MaxHealth - 10));

            pool.ReceiveDamageBasic(1000);
            Assert.That(pool.Health, Is.EqualTo(0));
            Assert.That(pool.Dead, Is.True);
        }
    }
}
