using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the high-explosive burst ported out of Godot
    /// <c>Projectile.explode</c>: the splash fan, its damage-over-distance
    /// falloff, the client's own splash radii and the four places a shell
    /// bursts instead of scoring a block.
    /// </summary>
    public sealed class BattleHeExplosionTests
    {
        private const float Tolerance = 1e-4f;

        private TanksDatabase _database;
        private GameObject _root;

        [SetUp]
        public void SetUp()
        {
            var text = Resources.Load<TextAsset>("Data/tanks");
            Assert.IsNotNull(text, "generated Data/tanks.json is missing");
            _database = TanksDatabase.Parse(text.text);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        /// <summary>
        /// <c>dmg_over_dist</c>: half the warhead at the burst itself, faded
        /// linearly out to the splash radius, less a flat 1.1 points per
        /// millimetre of the plate the ray met.
        /// </summary>
        [Test]
        public void DmgOverDist_FadesWithDistanceAndPaysForTheArmour()
        {
            // 0.5 * 300 - 1.1 * 13 = 135.7, rounded up.
            Assert.AreEqual(
                136,
                ExplosionMath.DmgOverDist(300f, 0f, 2f, 13, 1f, 1f));

            // Halfway out: 0.5 * 300 * 0.5 - 14.3 = 60.7.
            Assert.AreEqual(
                61,
                ExplosionMath.DmgOverDist(300f, 1f, 2f, 13, 1f, 1f));

            // At the rim and past it nothing is left.
            Assert.AreEqual(
                0, ExplosionMath.DmgOverDist(300f, 2f, 2f, 13, 1f, 1f));
            Assert.AreEqual(
                0, ExplosionMath.DmgOverDist(300f, 5f, 2f, 13, 1f, 1f));

            // A thick enough plate eats the whole burst, which is why HE is
            // aimed at roofs and not at glacis.
            Assert.AreEqual(
                0, ExplosionMath.DmgOverDist(300f, 0f, 2f, 200, 1f, 1f));

            // The spall liner only ever raises the armour term.
            var bare = ExplosionMath.DmgOverDist(300f, 0f, 2f, 50, 1f, 1f);
            var lined = ExplosionMath.DmgOverDist(
                300f, 0f, 2f, 50, ExplosionMath.SpallLinerCoefficient, 1f);
            Assert.AreEqual(95, bare);
            Assert.Less(lined, bare);

            // A shell with no radius at all reaches nothing but its own
            // impact point.
            Assert.AreEqual(
                0, ExplosionMath.DmgOverDist(300f, 0.5f, 0f, 0, 1f, 1f));
            Assert.Greater(
                ExplosionMath.DmgOverDist(300f, 0f, 0f, 0, 1f, 1f), 0);
        }

        /// <summary>
        /// The fan itself: 8 spokes per ring, 8 rings over a hemisphere and 16
        /// over the whole sphere, every ray a unit vector. Ring zero lies in the
        /// plane of the surface, 22.5 degrees apart, and the source's own quirk
        /// is preserved - spoke zero is the rotation axis of the rings, so every
        /// ring repeats that one ray.
        /// </summary>
        [Test]
        public void RayFan_MatchesTheSourceSpokesRingsAndItsOwnQuirk()
        {
            Assert.AreEqual(8, ExplosionMath.Steps);
            Assert.AreEqual(8, ExplosionMath.RingCount(true));
            Assert.AreEqual(16, ExplosionMath.RingCount(false));
            Assert.AreEqual(
                Mathf.PI / 8f, ExplosionMath.AngleStep, Tolerance);

            var normal = new Vec3(0f, 1f, 0f);
            var heading = new Vec3(0f, -1f, 1f);
            var spokeZero = ExplosionMath.RayDirection(normal, heading, 0, 0);

            for (var ring = 0; ring < ExplosionMath.RingCount(false); ring++)
            {
                for (var spoke = 0; spoke < ExplosionMath.Steps; spoke++)
                {
                    var ray = ExplosionMath.RayDirection(
                        normal, heading, ring, spoke);
                    Assert.AreEqual(
                        1f,
                        ExplosionMath.Length(ray),
                        Tolerance,
                        "ray " + ring + "/" + spoke + " is not a unit vector");

                    if (ring == 0)
                    {
                        // Ring zero only ever spins about the normal, so it
                        // stays in the plane of the surface.
                        Assert.AreEqual(
                            0f,
                            ExplosionMath.Dot(ray, normal),
                            Tolerance,
                            "ring 0 left the surface plane");
                    }

                    if (spoke == 0)
                    {
                        // rotated(rotator, ...) with rotator == the ray itself
                        // is the identity: the source spends eight rays on one
                        // direction and the port keeps that.
                        Assert.AreEqual(
                            1f,
                            ExplosionMath.Dot(ray, spokeZero),
                            Tolerance,
                            "spoke 0 of ring " + ring + " moved");
                    }
                }
            }

            // Ring zero spokes sit one angle step apart.
            var first = ExplosionMath.RayDirection(normal, heading, 0, 1);
            Assert.AreEqual(
                Mathf.Cos(ExplosionMath.AngleStep),
                ExplosionMath.Dot(spokeZero, first),
                Tolerance);

            // A round arriving straight down the normal has no cross product
            // to build its first spoke from, and still gets a usable fan.
            var headOn = ExplosionMath.RayDirection(
                normal, new Vec3(0f, -1f, 0f), 3, 5);
            Assert.AreEqual(1f, ExplosionMath.Length(headOn), Tolerance);
        }

        /// <summary>
        /// The splash radius is client data, not a guess: every explosive shell
        /// of the 4.8.0 pack ships its own <c>explosionRadius</c>, and only a
        /// card without one falls back to the calibre rule.
        /// </summary>
        [Test]
        public void EveryExplosiveShell_CarriesTheClientSplashRadius()
        {
            var explosive = 0;
            foreach (var tank in _database.Tanks)
            {
                foreach (var gun in tank.Guns)
                {
                    foreach (var shell in gun.Shells)
                    {
                        var name = tank.Name + "/" + gun.Name + "/" + shell.Name;
                        var radius = ArmorMath.ExplosionRadiusFor(
                            shell.ExplosionRadius, shell.Caliber);
                        if (!ArmorMath.IsExplosive(shell.Type))
                        {
                            Assert.Greater(radius, 0f, name);
                            continue;
                        }

                        explosive++;
                        Assert.Greater(
                            shell.ExplosionRadius,
                            0f,
                            name + " has no authored explosionRadius");
                        Assert.AreEqual(shell.ExplosionRadius, radius, Tolerance, name);

                        // The burst has to be worth something on bare steel,
                        // otherwise the data never reaches the fan.
                        Assert.Greater(
                            ExplosionMath.DmgOverDist(
                                shell.Damage, 0f, radius, 0, 1f, 1f),
                            0,
                            name);
                    }
                }
            }

            Assert.Greater(explosive, 100, "the roster lost its explosive shells");
        }

        /// <summary>
        /// The burst on a live tank. Two paths matter and the source spends
        /// them differently: the plate that stopped the shell is charged
        /// against a direct ray at zero distance, while the 8 x 8 fan sprays
        /// the hemisphere out of the surface and is what reaches a tank
        /// standing next to the burst. Either way the damage arrives as
        /// <see cref="DamageType.Explosion"/> rather than a block, and a live
        /// team-mate in the same spot takes nothing at all.
        /// </summary>
        [Test]
        public void Burst_SplashesTheEnemyAndSparesTheTeamMate()
        {
            _root = new GameObject("BattleHeExplosionTests");
            var card = LoadMs1();
            var area = new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f));
            var shooter = BattleTank.Spawn(
                _root.transform, "Shooter", card,
                Spawn("ShooterSpawn", new Vector3(0f, 0f, -200f)), area, 0);
            var enemy = BattleTank.Spawn(
                _root.transform, "Enemy", card,
                Spawn("EnemySpawn", Vector3.zero), area, 1);
            var ally = BattleTank.Spawn(
                _root.transform, "Ally", card,
                Spawn("AllySpawn", new Vector3(200f, 0f, 0f)), area, 0);
            Physics.SyncTransforms();

            var enemyHealth = enemy.Health.Health;
            var allyHealth = ally.Health.Health;

            // The roof of the MS-1 is 8 mm, so the direct ray keeps almost the
            // whole warhead: the shell that bounced still guts the tank.
            var direct = Burst(shooter, enemy, true);
            Assert.AreEqual(1, direct.Count, "the direct hit took no damage");
            Assert.Greater(direct[0], 0, "the direct ray was worth nothing");
            Assert.Less(
                enemy.Health.Health,
                enemyHealth,
                "a 152 mm burst against the MS-1 roof did nothing");
            Assert.AreEqual(
                DamageType.Explosion,
                enemy.Health.DamageInfosTaken[0].DmgType,
                "splash arrived as a plain shell hit");

            // A burst two and a half metres off the hull side reaches it
            // through the fan alone, with nothing handed in as a direct hit.
            var splash = Burst(shooter, enemy, false);
            Assert.AreEqual(
                1, splash.Count, "the fan never reached the enemy hull");
            Assert.Greater(splash[0], 0, "splash inside the radius did nothing");

            var friendly = Burst(shooter, ally, false);
            Assert.AreEqual(
                0, friendly.Count, "the burst damaged a live team-mate");
            Assert.AreEqual(
                allyHealth, ally.Health.Health, "the team-mate lost health");
        }

        /// <summary>
        /// The four places <c>handle_hit</c> bursts an explosive round instead
        /// of letting it behave like solid shot, spelled out on the shell that
        /// actually reaches them.
        /// </summary>
        [Test]
        public void ExplosiveShell_IsTheOnlyOneThatBursts()
        {
            var he = new ShellInfo
            {
                Type = ShellType.He,
                Damage = 750f,
                Caliber = 152,
                ExplosionRadius = 5.11f,
                Speed = 75f,
            };
            var ap = new ShellInfo
            {
                Type = ShellType.Ap, Damage = 40f, Caliber = 37, Speed = 442f,
            };

            var burst = BattleProjectile.Create(
                he, null, Vector3.zero, Vector3.zero, Vector3.forward, false);
            var solid = BattleProjectile.Create(
                ap, null, Vector3.zero, Vector3.zero, Vector3.forward, false);
            try
            {
                Assert.IsTrue(burst.Explosive());
                Assert.IsFalse(solid.Explosive());

                // One warhead, one burst: a second call is dropped, which is
                // the source's own exploded guard.
                burst.Explode(Vector3.zero, Vector3.up);
                burst.Explode(Vector3.zero, Vector3.up);
                Assert.AreEqual(0, burst.ExplosionTanks.Count);
            }
            finally
            {
                Object.DestroyImmediate(burst.gameObject);
                Object.DestroyImmediate(solid.gameObject);
            }
        }

        /// <summary>
        /// Fires one 152 mm high-explosive burst at <paramref name="target"/>
        /// and reports the damage it handed out. <paramref name="direct"/>
        /// models the non-penetration path of <c>handle_hit</c>, which bursts on
        /// the plate it met and charges that plate against the direct ray;
        /// otherwise the burst stands off the hull side and only the fan can
        /// reach it.
        /// </summary>
        private System.Collections.Generic.IReadOnlyList<int> Burst(
            BattleTank shooter, BattleTank target, bool direct)
        {
            var shell = new ShellInfo
            {
                Type = ShellType.He,
                Damage = 750f,
                Caliber = 152,
                ExplosionRadius = 5.11f,
                Speed = 75f,
            };

            // On the roof, pointing down: the warhead that just failed to
            // pierce 8 mm of it. The stand-off burst hangs beside the hull at
            // the height of its own side plates instead.
            var point = direct
                ? target.transform.position + Vector3.up * 1.45f
                : target.transform.position
                    + new Vector3(2.5f, 0.86f, 0f);
            var projectile = BattleProjectile.Create(
                shell, shooter, point, point, Vector3.down, false);
            try
            {
                if (direct)
                {
                    projectile.Explode(point, Vector3.up, 8, target);
                }
                else
                {
                    projectile.Explode(point, Vector3.up);
                }
                var damage = new System.Collections.Generic.List<int>(
                    projectile.ExplosionDamage);
                return damage;
            }
            finally
            {
                Object.DestroyImmediate(projectile.gameObject);
            }
        }

        private Transform Spawn(string name, Vector3 position)
        {
            var spawn = new GameObject(name);
            spawn.transform.SetParent(_root.transform, false);
            spawn.transform.position = position;
            return spawn.transform;
        }

        private static TankInfo LoadMs1()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(BattleSession.DefaultTankId);
            Assert.IsNotNull(card, "MS-1 card is missing from tanks.json");
            return card;
        }
    }
}
