using System.Reflection;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the guided-rocket flight of <c>projectile.gd</c>: a homing round
    /// carries no gravity at all (<c>projectile.gd:74</c> and <c>:364</c>) and
    /// steers onto <c>aim_target_lerp</c>, the smoothed crosshair point
    /// <c>base_tank.gd:998</c> hands it, instead of onto the ballistic gun
    /// trace. Homing onto that trace made the XM81 rockets of the T49 dive at
    /// the dirt in front of the hull even when the player aimed up.
    /// </summary>
    public sealed class BattleAtgmGuidanceTests
    {
        private const float Tolerance = 0.0001f;
        private const float Delta = 0.02f;

        private TanksDatabase _database;

        [SetUp]
        public void SetUp()
        {
            var text = Resources.Load<TextAsset>("Data/tanks");
            Assert.IsNotNull(text, "generated Data/tanks.json is missing");
            _database = TanksDatabase.Parse(text.text);
        }

        [Test]
        public void EveryShell_SpendsTheGravityTheClientAuthors()
        {
            var guided = 0;
            foreach (var tank in _database.Tanks)
            {
                foreach (var gun in tank.Guns)
                {
                    foreach (var shell in gun.Shells)
                    {
                        var name = tank.Name + "/" + gun.Name + "/" + shell.Name;
                        var gravity =
                            AimMath.ShellGravity(shell.GravityMultiplier, shell.Atgm);
                        if (!shell.Atgm)
                        {
                            // No client card scales shell gravity, so every
                            // ballistic round falls at the source default.
                            Assert.AreEqual(1f, shell.GravityMultiplier, Tolerance, name);
                            Assert.AreEqual(AimMath.DefaultGravity, gravity, Tolerance, name);
                            continue;
                        }

                        guided++;
                        Assert.AreEqual(0f, gravity, Tolerance, name);
                        Assert.Greater(shell.TurnSpeed, 0f, name);
                        Assert.Greater(Mathf.Abs(shell.Speed), 0f, name);

                        // The rocket must live long enough to cross its own
                        // range: max_dist / speed, as the source times it.
                        Assert.AreEqual(
                            shell.MaxDistance / Mathf.Abs(shell.Speed),
                            GunMath.ShellLifetime(shell.MaxDistance, shell.Speed),
                            Tolerance,
                            name);
                    }
                }
            }

            Assert.AreEqual(4, guided, "the two XM81 rockets, KpfPz 70 Missile and Sheridan Missile");
        }

        [Test]
        public void GuidedRocket_ClimbsTowardAnAimPointAboveTheMuzzle()
        {
            var shell = Rocket();
            var start = new Vector3(0f, 1.5f, 0f);
            // The player holds the crosshair high up the slope ahead.
            var aim = new Vector3(0f, 200f, 300f);

            var position = start;
            var velocity = Vector3.forward * shell.Speed;
            for (var i = 0; i < 100; i++)
            {
                velocity = BattleProjectile.HomingVelocity(
                    velocity, position, aim, shell.TurnSpeed, Delta);
                velocity.y -= AimMath.ShellGravity(shell.GravityMultiplier, shell.Atgm) * Delta;
                position += velocity * Delta;
            }

            Assert.Greater(velocity.y, 20f, "the rocket must pull its nose up");
            Assert.Greater(position.y, start.y + 20f, "the rocket must gain height");
            Assert.AreEqual(shell.Speed, velocity.magnitude, 0.01f,
                "steering keeps the rocket's own speed");
        }

        [Test]
        public void GuidedRocket_KeepsItsHeightWhenTheAimPointIsStraightAhead()
        {
            var shell = Rocket();
            var start = new Vector3(0f, 1.5f, 0f);
            var aim = start + Vector3.forward * AimMath.TraceDistance;

            var position = start;
            var velocity = Vector3.forward * shell.Speed;
            for (var i = 0; i < 200; i++)
            {
                velocity = BattleProjectile.HomingVelocity(
                    velocity, position, aim, shell.TurnSpeed, Delta);
                velocity.y -= AimMath.ShellGravity(shell.GravityMultiplier, shell.Atgm) * Delta;
                position += velocity * Delta;
            }

            // A rocket that still fell at 0.01 * 10 m/s^2 would sag; the source
            // zeroes homing gravity outright, so it flies flat.
            Assert.AreEqual(start.y, position.y, 0.001f);
        }

        [Test]
        public void HomingOntoTheBallisticGunTrace_DivesIntoTheGround()
        {
            // Negative control for the bug this fix closes: the gun trace of an
            // 85 m/s round drops onto the ground a few metres ahead of the
            // hull, and a rocket that homes there never leaves the dirt.
            var shell = Rocket();
            var start = new Vector3(0f, 1.5f, 0f);
            var trace = new Vector3(0f, 0f, 8f);

            var position = start;
            var velocity = Vector3.forward * shell.Speed;
            for (var i = 0; i < 100; i++)
            {
                velocity = BattleProjectile.HomingVelocity(
                    velocity, position, trace, shell.TurnSpeed, Delta);
                position += velocity * Delta;
            }

            Assert.Less(velocity.y, 0f);
            Assert.Less(position.y, 0f, "the rocket buries itself in front of the tank");
        }

        [Test]
        public void BotTacticsExposeArtilleryAndMobileAssaultStyles()
        {
            Assert.AreEqual(AtgmAiMode.Artillery, AtgmAiTactics.ChooseMode(0f));
            Assert.AreEqual(AtgmAiMode.Assault, AtgmAiTactics.ChooseMode(1f));
            Assert.AreEqual(AtgmMovement.Hold, AtgmAiTactics.Movement(AtgmAiMode.Artillery, 250f));
            Assert.AreEqual(AtgmMovement.Approach, AtgmAiTactics.Movement(AtgmAiMode.Artillery, 500f));
            Assert.AreEqual(AtgmMovement.Circle, AtgmAiTactics.Movement(AtgmAiMode.Assault, 80f));
            Assert.AreEqual(AtgmMovement.Approach, AtgmAiTactics.Movement(AtgmAiMode.Assault, 180f));
            Assert.IsTrue(AtgmAiTactics.CanFire(AtgmAiMode.Artillery, 300f));
            Assert.IsFalse(AtgmAiTactics.CanFire(AtgmAiMode.Assault, 300f));
        }

        [Test]
        public void BotMissesAreRareAndLargeEnoughToPassTheTarget()
        {
            Assert.IsTrue(AtgmAiTactics.ShouldDeliberatelyMiss(0f));
            Assert.IsFalse(AtgmAiTactics.ShouldDeliberatelyMiss(.13f));
            Assert.Less(AtgmAiTactics.DeliberateMissChance, .15f);
            Assert.GreaterOrEqual(AtgmAiTactics.MissRadius(0f), 7f);
            Assert.LessOrEqual(AtgmAiTactics.MissRadius(1f), 14f);
            Assert.Greater(AtgmAiTactics.LeadSeconds(300f, 85f), 0f,
                "live guidance must keep leading a moving spotted target");
        }

        [Test]
        public void RocketTankBotReceivesSpecializedAtgmController()
        {
            BattleTank tank = null;
            try
            {
                var card = _database.Get("t49_rocket");
                tank = BattleTank.Spawn(null, "atgm bot", card, null, new Bounds(), 1);
                tank.SetShellAmounts(new[] { 10, 10 });
                var ai = AiTankController.Attach(tank, AiSkillLevel.BlueArmy);
                ai.StartBattle();
                Assert.AreNotEqual(AtgmAiMode.None, ai.AtgmMode);
            }
            finally
            {
                if (tank != null) Object.DestroyImmediate(tank.gameObject);
            }
        }

        [Test]
        public void ArtilleryBotAimsOnlyAtSpottedTargetAndEnablesLobGuidance()
        {
            BattleTank shooter = null, target = null;
            try
            {
                var card = _database.Get("t49_rocket");
                shooter = BattleTank.Spawn(null, "atgm shooter", card, null, new Bounds(), 0);
                target = BattleTank.Spawn(null, "atgm target", card, null, new Bounds(), 1);
                shooter.transform.position = Vector3.zero;
                target.transform.position = Vector3.forward * 250f;
                shooter.SetShellAmounts(new[] { 10, 10 });
                var ai = AiTankController.Attach(shooter, AiSkillLevel.BlueArmy);
                ai.StartBattle();
                ai.TargetTank = target;
                var atgm = typeof(AiTankController).GetField("_atgm", BindingFlags.Instance|BindingFlags.NonPublic).GetValue(ai);
                atgm.GetType().GetProperty("Mode", BindingFlags.Instance|BindingFlags.Public).SetValue(atgm, AtgmAiMode.Artillery);

                target.SetSpotted(false);
                Assert.IsTrue(ai.TickAtgmCombat(.02f));
                Assert.IsFalse(shooter.Turret.LobAim);

                target.SetSpotted(true);
                Assert.IsTrue(ai.TickAtgmCombat(.02f));
                Assert.IsTrue(shooter.Turret.LobAim);
                Assert.Less((shooter.Turret.AimTarget-target.AutoAimPoint()).sqrMagnitude, .01f);
            }
            finally
            {
                if (shooter != null) Object.DestroyImmediate(shooter.gameObject);
                if (target != null) Object.DestroyImmediate(target.gameObject);
            }
        }

        private ShellInfo Rocket()
        {
            var t49 = _database.Get("t49_rocket");
            Assert.IsNotNull(t49, "t49_rocket card is missing");
            var launcher = t49.Guns[t49.Guns.Count - 1];
            var shell = launcher.Shells[0];
            Assert.IsTrue(shell.Atgm, "the XM81 launcher loads guided rockets");
            return shell;
        }
    }
}
