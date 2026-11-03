using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Locks the engagement layer: the odds a bot fights by, the duel,
    /// withdraw and chase judgement with its hysteresis, the front-plate
    /// steering, the cover search and the lob flight check that keeps an
    /// overhead ATGM launcher from firing into the wall in front of it.
    /// </summary>
    public sealed class BattleAiEngagementTests
    {
        private const float Tolerance = 1e-4f;

        /// <summary>Far from anything another fixture might leave behind.</summary>
        private static readonly Vector3 Origin = new Vector3(8000f, 0f, 0f);

        private GameObject _root;
        private BattleRoster _roster;

        [TearDown]
        public void TearDown()
        {
            if (_roster != null)
            {
                _roster.End();
                _roster = null;
            }

            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        private static AiCombatant Gun(float health, float alpha, float secondsPerShot)
        {
            return new AiCombatant
            {
                Health = health,
                Alpha = alpha,
                SecondsPerShot = secondsPerShot,
                HitChance = 1f,
                PenetrationChance = 1f,
            };
        }

        [Test]
        public void TimeToKillCountsShellsReloadAndAccuracy()
        {
            var gun = Gun(1000f, 100f, 5f);
            Assert.AreEqual(10f, AiEngagementMath.TimeToKill(gun, 250f), Tolerance,
                "three shells of alpha, two reloads between them");

            gun.ReloadRemaining = 3f;
            Assert.AreEqual(13f, AiEngagementMath.TimeToKill(gun, 250f), Tolerance,
                "a gun still reloading fires its first shell later");

            gun.ReloadRemaining = 0f;
            gun.HitChance = 0.5f;
            Assert.AreEqual(25f, AiEngagementMath.TimeToKill(gun, 250f), Tolerance,
                "half the shells miss, so twice as many are fired");

            gun.Harmless = true;
            Assert.IsTrue(float.IsPositiveInfinity(AiEngagementMath.TimeToKill(gun, 250f)));
        }

        [Test]
        public void DuelOddsTieOnEqualKillTimesAndRewardTheLoadedGun()
        {
            Assert.AreEqual(0.5f, AiEngagementMath.DuelOdds(10f, 10f), Tolerance);
            Assert.Greater(AiEngagementMath.DuelOdds(4f, 12f), 0.85f);
            Assert.Less(AiEngagementMath.DuelOdds(12f, 4f), 0.15f);
            Assert.AreEqual(AiEngagementMath.OddsFloor,
                AiEngagementMath.DuelOdds(float.PositiveInfinity, 5f), Tolerance);
            Assert.AreEqual(AiEngagementMath.OddsCeiling,
                AiEngagementMath.DuelOdds(5f, float.PositiveInfinity), Tolerance);
            Assert.AreEqual(0.5f,
                AiEngagementMath.DuelOdds(float.PositiveInfinity, float.PositiveInfinity), Tolerance);

            // Two guns that one-shot each other: the one already loaded wins.
            var me = Gun(200f, 300f, 8f);
            var them = Gun(200f, 300f, 8f);
            them.ReloadRemaining = 4f;
            Assert.Greater(AiEngagementMath.DuelOdds(me, them), 0.9f);
            Assert.Less(AiEngagementMath.DuelOdds(them, me), 0.1f);
        }

        [Test]
        public void ForceOddsFollowTheSquareLaw()
        {
            // Twice the hulls is four times the strength.
            Assert.AreEqual(0.2f, AiEngagementMath.ForceOdds(10f, 1000f, 20f, 2000f), Tolerance);
            Assert.AreEqual(0.5f, AiEngagementMath.ForceOdds(10f, 1000f, 10f, 1000f), Tolerance);
            Assert.AreEqual(0.5f, AiEngagementMath.ForceOdds(0f, 0f, 0f, 0f), Tolerance);
            Assert.AreEqual(AiEngagementMath.OddsCeiling,
                AiEngagementMath.ForceOdds(10f, 1000f, 0f, 3000f), Tolerance,
                "a crowd that cannot hurt the bot is no threat");
        }

        [Test]
        public void HitPenetrationAndCycleEstimatesStayInTheirBands()
        {
            var near = AiEngagementMath.HitChance(0.36f, 100f, 1.4f);
            var far = AiEngagementMath.HitChance(0.36f, 400f, 1.4f);
            Assert.Greater(near, 0.95f);
            Assert.Less(far, near);
            Assert.Less(AiEngagementMath.HitChance(0.6f, 400f, 1.4f), far);

            Assert.AreEqual(0.03f, AiEngagementMath.PenetrationChance(50f, 100f), Tolerance);
            Assert.AreEqual(0.97f, AiEngagementMath.PenetrationChance(200f, 100f), Tolerance);
            Assert.AreEqual(0.5f, AiEngagementMath.PenetrationChance(110f, 100f), Tolerance);
            Assert.AreEqual(0.97f, AiEngagementMath.PenetrationChance(10f, 0f), Tolerance);

            Assert.AreEqual(10f, AiEngagementMath.SecondsPerShot(10f, 1, 0f, 0f), Tolerance);
            Assert.AreEqual(3.75f, AiEngagementMath.SecondsPerShot(12f, 4, 1f, 0f), Tolerance,
                "a four-shell magazine spreads its reload over the clip");
            Assert.AreEqual(2.5f, AiEngagementMath.SecondsPerShot(10f, 3, 1f, 2.5f), Tolerance);

            Assert.AreEqual(AiEngagementMath.AngledFront, AiEngagementMath.FrontalAngle(0.5f), Tolerance);
            Assert.AreEqual(AiEngagementMath.SquareFront, AiEngagementMath.FrontalAngle(0.05f), Tolerance);
            Assert.AreEqual(AiEngagementMath.SquareFront, AiEngagementMath.FrontalAngle(0.95f), Tolerance);
            Assert.Less(AiEngagementMath.PerceptionInterval(AiSkillLevel.PurpleArmy),
                AiEngagementMath.PerceptionInterval(AiSkillLevel.WhiteArmy));
        }

        private static AiEngagementInputs Fight()
        {
            return new AiEngagementInputs
            {
                Current = AiEngagement.None,
                CurrentFor = 10f,
                Mobile = true,
                Smart = true,
                HasTarget = true,
                DuelOdds = 0.6f,
                ForceOdds = 0.6f,
                EnemiesEngaging = 1,
                TargetDistance = 150f,
                DuelRange = 260f,
                HealthFraction = 1f,
                QuarryDistance = float.PositiveInfinity,
            };
        }

        [Test]
        public void EngagementDuelsAWinnableFightInsideItsRange()
        {
            var inputs = Fight();
            Assert.AreEqual(AiEngagement.Duel, AiEngagementMath.ChooseEngagement(inputs));

            inputs.TargetDistance = 400f;
            Assert.AreEqual(AiEngagement.None, AiEngagementMath.ChooseEngagement(inputs),
                "beyond the duel range the ported states keep the fight");

            inputs = Fight();
            inputs.EnemiesEngaging = 3;
            inputs.ForceOdds = 0.55f;
            Assert.AreEqual(AiEngagement.None, AiEngagementMath.ChooseEngagement(inputs),
                "three guns on the bot is no duel");

            inputs.ForceOdds = 0.8f;
            Assert.AreEqual(AiEngagement.Duel, AiEngagementMath.ChooseEngagement(inputs),
                "a hull that outclasses the whole crowd still takes it on");

            inputs = Fight();
            inputs.Mobile = false;
            Assert.AreEqual(AiEngagement.None, AiEngagementMath.ChooseEngagement(inputs));

            inputs = Fight();
            inputs.Atgm = true;
            Assert.AreEqual(AiEngagement.None, AiEngagementMath.ChooseEngagement(inputs),
                "the ATGM controller keeps moving a rocket carrier");
        }

        [Test]
        public void EngagementWithdrawsFromLosingFightsWithHysteresis()
        {
            var inputs = Fight();
            inputs.ForceOdds = 0.3f;
            Assert.AreEqual(AiEngagement.Withdraw, AiEngagementMath.ChooseEngagement(inputs));

            inputs = Fight();
            inputs.DuelOdds = 0.2f;
            Assert.AreEqual(AiEngagement.Withdraw, AiEngagementMath.ChooseEngagement(inputs));

            inputs = Fight();
            inputs.EnemiesEngaging = 3;
            inputs.ForceOdds = 0.45f;
            Assert.AreEqual(AiEngagement.Withdraw, AiEngagementMath.ChooseEngagement(inputs));

            inputs = Fight();
            inputs.HealthFraction = 0.2f;
            inputs.DuelOdds = 0.5f;
            Assert.AreEqual(AiEngagement.Withdraw, AiEngagementMath.ChooseEngagement(inputs));

            inputs = Fight();
            inputs.Current = AiEngagement.Withdraw;
            inputs.ForceOdds = 0.45f;
            Assert.AreEqual(AiEngagement.Withdraw, AiEngagementMath.ChooseEngagement(inputs),
                "leaving cover needs better odds than taking it");
            inputs.ForceOdds = 0.6f;
            Assert.AreEqual(AiEngagement.Duel, AiEngagementMath.ChooseEngagement(inputs));

            inputs = Fight();
            inputs.Atgm = true;
            inputs.ForceOdds = 0.2f;
            Assert.AreEqual(AiEngagement.Withdraw, AiEngagementMath.ChooseEngagement(inputs));

            inputs = Fight();
            inputs.Smart = false;
            inputs.ForceOdds = 0.33f;
            Assert.AreNotEqual(AiEngagement.Withdraw, AiEngagementMath.ChooseEngagement(inputs),
                "a white army crew only notices a fight it is plainly losing");
        }

        [Test]
        public void EngagementHoldsAFreshDuelAndCoverWithoutContact()
        {
            var inputs = Fight();
            inputs.Current = AiEngagement.Duel;
            inputs.CurrentFor = 1f;
            inputs.DuelOdds = 0.3f;
            Assert.AreEqual(AiEngagement.Duel, AiEngagementMath.ChooseEngagement(inputs),
                "a duel is not re-judged inside its dwell");
            inputs.CurrentFor = 3f;
            Assert.AreEqual(AiEngagement.None, AiEngagementMath.ChooseEngagement(inputs));

            inputs = Fight();
            inputs.HasTarget = false;
            inputs.Current = AiEngagement.Withdraw;
            inputs.HoldingAfterWithdraw = true;
            Assert.AreEqual(AiEngagement.Withdraw, AiEngagementMath.ChooseEngagement(inputs));
            inputs.HoldingAfterWithdraw = false;
            Assert.AreEqual(AiEngagement.None, AiEngagementMath.ChooseEngagement(inputs));

            inputs = Fight();
            inputs.HasTarget = false;
            inputs.HitByUnseen = true;
            Assert.AreEqual(AiEngagement.Withdraw, AiEngagementMath.ChooseEngagement(inputs),
                "shells from an unseen gun send a smart crew into cover");
            inputs.Smart = false;
            Assert.AreEqual(AiEngagement.None, AiEngagementMath.ChooseEngagement(inputs));
        }

        [Test]
        public void EngagementChasesOnlyAFreshWinnableQuarry()
        {
            var inputs = Fight();
            inputs.HasTarget = false;
            inputs.QuarryFresh = true;
            inputs.QuarryOdds = 0.7f;
            inputs.QuarryDistance = 100f;
            Assert.AreEqual(AiEngagement.Chase, AiEngagementMath.ChooseEngagement(inputs));

            inputs.QuarryOdds = 0.4f;
            Assert.AreEqual(AiEngagement.None, AiEngagementMath.ChooseEngagement(inputs));

            inputs.QuarryOdds = 0.7f;
            inputs.QuarryDistance = 300f;
            Assert.AreEqual(AiEngagement.None, AiEngagementMath.ChooseEngagement(inputs));

            inputs.QuarryDistance = 100f;
            inputs.QuarryFresh = false;
            Assert.AreEqual(AiEngagement.None, AiEngagementMath.ChooseEngagement(inputs));

            inputs.QuarryFresh = true;
            inputs.Smart = false;
            Assert.AreEqual(AiEngagement.None, AiEngagementMath.ChooseEngagement(inputs));

            inputs.Smart = true;
            inputs.Atgm = true;
            Assert.AreEqual(AiEngagement.None, AiEngagementMath.ChooseEngagement(inputs));
        }

        [Test]
        public void TargetPriorityFinishesWoundedAndSkipsUnpennable()
        {
            var finish = new AiTargetPriorityInputs
            {
                ShotsToKill = 1f,
                PenetrationChance = 0.9f,
                ReloadRemaining = -1f,
                Distance = 100f,
            };
            var healthy = finish;
            healthy.ShotsToKill = 5f;
            Assert.Greater(AiEngagementMath.TargetPriorityBonus(finish),
                AiEngagementMath.TargetPriorityBonus(healthy));

            var armoured = healthy;
            armoured.PenetrationChance = 0.05f;
            Assert.Less(AiEngagementMath.TargetPriorityBonus(armoured), 0f);

            var reloading = healthy;
            reloading.ReloadRemaining = 6f;
            Assert.Greater(AiEngagementMath.TargetPriorityBonus(reloading),
                AiEngagementMath.TargetPriorityBonus(healthy));

            var current = healthy;
            current.Current = true;
            Assert.AreEqual(AiEngagementMath.TargetPriorityBonus(healthy) + 3f,
                AiEngagementMath.TargetPriorityBonus(current), Tolerance);
            Assert.AreEqual(1f, AiEngagementMath.ShotsToKill(300f, 250f), Tolerance);
            Assert.AreEqual(3f, AiEngagementMath.ShotsToKill(100f, 250f), Tolerance);
        }

        private static GameObject Box(Transform parent, string name, Vector3 center, Vector3 size)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.layer = BattleLayers.Static;
            box.transform.SetParent(parent, false);
            box.transform.position = center;
            box.transform.localScale = size;
            return box;
        }

        [Test]
        public void LobFlightCheckFindsTheWallInFrontOfTheLauncher()
        {
            _root = new GameObject("AtgmFlightCheckTests");
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get("t49_rocket");
            Assert.IsNotNull(card, "t49_rocket card is missing");
            var gun = card.Guns[card.Guns.Count - 1];
            var shell = gun.Shells[0];
            Assert.IsTrue(shell.Atgm);

            Box(_root.transform, "Ground", Origin + new Vector3(0f, -1f, 150f), new Vector3(200f, 2f, 500f));
            var wall = Box(_root.transform, "Wall", Origin + new Vector3(0f, 15f, 10f), new Vector3(30f, 30f, 2f));
            Physics.SyncTransforms();

            var muzzle = Origin + new Vector3(0f, 2f, 0f);
            var aim = Origin + new Vector3(0f, 2f, 200f);
            var speed = Mathf.Abs(shell.Speed);
            var pitch = ArtyMath.GuidedLaunchPitch(200f, gun.DeviationUp, speed, shell.TurnSpeed);
            var launch = Quaternion.AngleAxis(-pitch, Vector3.right) * Vector3.forward * speed;
            var seconds = GunMath.ShellLifetime(shell.MaxDistance, shell.Speed);

            Vector3 hit;
            Assert.IsTrue(AtgmFlightCheck.Obstructed(
                muzzle, launch, aim, shell.TurnSpeed, true, 0f, seconds, 0.02f, out hit),
                "the lob climbs into the wall ten metres ahead");
            Assert.AreEqual(10f, hit.z - Origin.z, 1.5f);

            wall.SetActive(false);
            Physics.SyncTransforms();
            Assert.IsFalse(AtgmFlightCheck.Obstructed(
                muzzle, launch, aim, shell.TurnSpeed, true, 0f, seconds, 0.02f, out hit),
                "over open ground the lob comes down on its aim point");
        }

        private BattleTank SpawnTank(TankInfo card, string name, Vector3 position, int team)
        {
            var spawn = new GameObject(name + "Spawn").transform;
            spawn.SetParent(_root.transform, false);
            spawn.position = position;
            var area = new Bounds(Origin, new Vector3(1000f, 400f, 1000f));
            return BattleTank.Spawn(_root.transform, name, card, spawn, area, team);
        }

        private TankInfo DefaultCard()
        {
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            return database.Get(BattleSession.DefaultTankId);
        }

        [Test]
        public void FrontPlateSteeringTurnsTowardsTheThreat()
        {
            _root = new GameObject("AiFacingTests");
            var tank = SpawnTank(DefaultCard(), "Facer", Origin + Vector3.up * 10f, 0);
            tank.Blocked = false;
            tank.transform.rotation = Quaternion.identity;
            var ai = AiTankController.Attach(tank, AiSkillLevel.GreenArmy);

            Assert.IsFalse(ai.FaceTowards(tank.Pos() + (Vector3.right * 50f), 0f));
            Assert.Greater(tank.RightAxis, 0f, "a threat on the right turns the hull right");
            ai.FaceTowards(tank.Pos() - (Vector3.right * 50f), 0f);
            Assert.Less(tank.RightAxis, 0f);
            Assert.IsTrue(ai.FaceTowards(tank.Pos() + (Vector3.forward * 50f), 0f));
            Assert.AreEqual(0f, tank.RightAxis, Tolerance);
            Assert.IsFalse(ai.FaceTowards(tank.Pos() + (Vector3.forward * 50f), 25f));
            Assert.Greater(tank.RightAxis, 0f, "a positive angle swings the hull clockwise");
        }

        [Test]
        public void RouteLookDoesNotStealTheTurretFromAValidTarget()
        {
            _root = new GameObject("AiLookTests");
            var card = DefaultCard();
            var tank = SpawnTank(card, "Looker", Origin + Vector3.up * 10f, 0);
            var enemy = SpawnTank(card, "Seen", Origin + new Vector3(0f, 10f, 150f), 1);
            enemy.SetSpotted(true);
            var ai = AiTankController.Attach(tank, AiSkillLevel.GreenArmy);
            ai.TargetTank = enemy;
            tank.SetAimTarget(enemy.Pos(), false, false);
            var aimed = tank.Turret.AimTarget;

            var route = tank.Pos() + (Vector3.right * 100f);
            ai.DriveToPos(route, false, false, true);
            Assert.Less((tank.Turret.AimTarget - aimed).sqrMagnitude, 0.01f,
                "the turret stays on the enemy while the hull follows its route");

            enemy.SetSpotted(false);
            ai.DriveToPos(route, false, false, true);
            var look = tank.Turret.AimTarget - route;
            look.y = 0f;
            Assert.Less(look.sqrMagnitude, 0.25f, "with nothing to shoot it looks along the route");
        }

        [Test]
        public void CoverSearchFindsAPointTheWallHidesFromTheThreat()
        {
            _root = new GameObject("AiCoverTests");
            var card = DefaultCard();
            var tank = SpawnTank(card, "Hider", Origin, 0);
            var threat = Origin + new Vector3(0f, 0f, 100f);
            Box(_root.transform, "Wall", Origin + new Vector3(0f, 5f, -8f), new Vector3(30f, 10f, 2f));
            Physics.SyncTransforms();
            var ai = AiTankController.Attach(tank, AiSkillLevel.GreenArmy);

            var cover = ai.FindCoverFrom(threat, 45f);
            Assert.AreNotEqual(Vector3.zero, cover);
            Assert.Less(cover.z - Origin.z, -8f, "the cover lies behind the wall");
            Assert.IsTrue(ai.CoverHides(cover, threat));
            Assert.IsFalse(ai.CoverHides(tank.Pos(), threat), "the open spot stays exposed");
        }

        [Test]
        public void OddsSendABotIntoADuelAndOutOfACrowd()
        {
            _root = new GameObject("AiEngagementTests");
            var card = DefaultCard();
            var stationary = AiTankController.BotsStationary;
            AiTankController.BotsStationary = false;
            try
            {
                var tank = SpawnTank(card, "Duelist", Origin, 0);
                var first = SpawnTank(card, "First", Origin + new Vector3(0f, 0f, 50f), 1);
                _roster = BattleRoster.Begin(null, 0, new BattleAiCover[0]);
                _roster.Add(tank);
                _roster.Add(first);
                first.SetSpotted(true);
                var ai = AiTankController.Attach(tank, AiSkillLevel.GreenArmy);

                ai.TrySwitchingToRelevantState();
                Assert.AreEqual(AiStateKind.Duel, ai.CurrentStateKind,
                    "an even one on one at close range is a duel");
                Assert.IsInstanceOf<AiStateDuel>(ai.CurrentState);
                Assert.AreEqual(0.5f, ai.Engagement.DuelOdds, 0.05f);

                var second = SpawnTank(card, "Second", Origin + new Vector3(10f, 0f, 55f), 1);
                var third = SpawnTank(card, "Third", Origin + new Vector3(-10f, 0f, 55f), 1);
                _roster.Add(second);
                _roster.Add(third);
                second.SetSpotted(true);
                third.SetSpotted(true);

                ai.TrySwitchingToRelevantState();
                Assert.AreEqual(AiStateKind.Withdraw, ai.CurrentStateKind,
                    "three equal guns at point blank outweigh one");
                Assert.IsInstanceOf<AiStateWithdraw>(ai.CurrentState);
                Assert.AreEqual(3, ai.Engagement.EnemiesEngaging);
                Assert.Less(ai.Engagement.ForceOdds, AiEngagementMath.WithdrawForceEnter);

                ai.EnterState(AiStateKind.Chase);
                Assert.IsInstanceOf<AiStateChase>(ai.CurrentState);
            }
            finally
            {
                AiTankController.BotsStationary = stationary;
            }
        }
    }
}