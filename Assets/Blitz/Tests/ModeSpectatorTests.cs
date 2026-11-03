using System.Reflection;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The observer strip and the camera cycling of the respawn modes: a pending return keeps
    /// the player on the respawn screen, only a tank without lives left may watch its team.
    /// </summary>
    public sealed class ModeSpectatorTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private BattleSession _session;
        private float _scale;

        [SetUp] public void Setup() { _scale = Time.timeScale; Time.timeScale = 1; }
        [TearDown] public void Cleanup() { if (_session != null) Object.DestroyImmediate(_session.gameObject); Time.timeScale = _scale; }

        private static void Kill(BattleTank tank)
        {
            for (var i = 0; i < 2 && !tank.Dead; i++)
                tank.ReceiveDamage(new DamageInfo { Dmg = tank.Health.MaxHealth * 10, DmgBase = tank.Health.MaxHealth * 10, DmgType = DamageType.Ability });
            Assert.IsTrue(tank.Dead);
        }

        /// <summary>One control frame: the strip follows the game state, not only the death event.</summary>
        private void Tick() { typeof(BattleControls).GetMethod("Update", Hidden).Invoke(_session.Controls, null); }

        private bool StripShown
        {
            get { var strip = _session.Controls.SpectatorControls; return strip != null && strip.gameObject.activeInHierarchy; }
        }

        private void BigBossRole(BattleTank tank, int role)
        {
            var c = tank.BigBoss;
            typeof(BigBossTank).GetProperty("State").SetValue(c, new BigBossState(_session.BigBoss.Rules, role));
            typeof(BigBossTank).GetMethod("ApplyModifiers", Hidden).Invoke(c, new object[] { true }); c.Begin();
        }

        private void BigBossReturn(BigBossTank tank)
        {
            Assert.IsTrue(tank.State.Return(Time.time + 8, _session.BigBoss.Random));
            typeof(BigBossTank).GetField("_returnSpawn", Hidden).SetValue(tank, _session.World.PlayerSpawns[0]);
            typeof(BigBossTank).GetMethod("Restore", Hidden).Invoke(tank, null);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void UprisingStripAndCyclingWaitForTheFinalDeath(bool mobile)
        {
            _session = BattleSession.Create(null, mobile, autoStart: true, mode: BattleMode.Uprising);
            var player = _session.Player; var c = player.GetComponent<UprisingTank>();
            for (var life = 0; life < 2; life++)
            {
                Kill(player); Tick();
                Assert.IsFalse(_session.Controls.CanSpectate, "life " + life); Assert.IsFalse(StripShown, "life " + life);
                _session.Controls.CycleSpectator(1); Assert.AreSame(player, _session.View.Tank);
                Assert.IsTrue(c.State.Respawn(Time.time + 11, true));
                typeof(UprisingTank).GetMethod("Restore", Hidden).Invoke(c, null);
                Tick(); Assert.IsFalse(StripShown);
            }
            Kill(player); Tick();
            Assert.IsTrue(_session.Controls.CanSpectate);
            Assert.AreEqual(mobile, StripShown, "the touch strip is the only spectator UI and exists on phones only");
            if (mobile) _session.Controls.SpectatorNext.onClick.Invoke(); else _session.Controls.CycleSpectator(1);
            Assert.AreNotSame(player, _session.View.Tank);
        }

        [Test]
        public void BigBossOrdinaryRoleNeverSpectatesWhileItsReturnIsPending()
        {
            _session = BattleSession.Create(null, true, tankId: "t-34", autoStart: true, mode: BattleMode.BigBoss);
            var player = _session.Player; BigBossRole(player, 4);
            for (var life = 0; life < 3; life++)
            {
                Kill(player); Tick();
                Assert.IsTrue(player.BigBoss.State.Respawning);
                Assert.IsFalse(_session.Controls.CanSpectate, "life " + life); Assert.IsFalse(StripShown, "life " + life);
                _session.Controls.CycleSpectator(1); Assert.AreSame(player, _session.View.Tank);
                BigBossReturn(player.BigBoss); Tick(); Assert.IsFalse(StripShown);
            }
        }

        [Test]
        public void BigBossBossSpectatesOnlyOnceItsThreeLivesAreSpent()
        {
            _session = BattleSession.Create(null, true, tankId: "t-34", autoStart: true, mode: BattleMode.BigBoss);
            var player = _session.Player; BigBossRole(player, 1);
            for (var life = 0; life < 2; life++)
            {
                Kill(player); Tick();
                Assert.IsFalse(_session.Controls.CanSpectate, "life " + life); Assert.IsFalse(StripShown, "life " + life);
                BigBossReturn(player.BigBoss); Tick(); Assert.IsFalse(StripShown);
            }
            Kill(player); Tick();
            Assert.IsTrue(player.BigBoss.State.Exhausted);
            Assert.IsTrue(_session.Controls.CanSpectate); Assert.IsTrue(StripShown);
            _session.Controls.SpectatorNext.onClick.Invoke(); Assert.AreNotSame(player, _session.View.Tank);
        }
    }
}
