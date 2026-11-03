using System;
using BlitzLegacy.Core.Maps;
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
    /// Locks the arithmetic and map contracts ported from ai_controller.gd.
    /// These decisions are tested without relying on a particular random bot
    /// path, so a source number cannot drift while a smoke battle still happens
    /// to finish.
    /// </summary>
    public sealed class BattleAiTests
    {
        private const float Tolerance = 1e-4f;
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
                UnityEngine.Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        [Test]
        public void RudnikiManifestCarriesAllThirteenAuthoredMarkers()
        {
            var map = BattleWorld.LoadDefinition(BattleWorld.DefaultMapId);

            Assert.AreEqual(13, map.AiMarkers.Count);
            Assert.AreEqual("AIMarker", map.AiMarkers[0].Name);
            Assert.AreEqual(MapAiMarkerType.PriorityPrimary, map.AiMarkers[0].Type);
            Assert.AreEqual(17.095f, map.AiMarkers[0].Radius, Tolerance);
            Assert.AreEqual(1, map.AiMarkers[0].Priority);
            Assert.AreEqual(MapAiMarkerType.Snipe, map.AiMarkers[6].Type);
            Assert.AreEqual(6, map.AiMarkers.FindAll(marker => marker.Type == MapAiMarkerType.Snipe).Count);
        }

        [Test]
        public void RuntimeMarkerMirrorsPositionAndReleasesItsOccupier()
        {
            _root = new GameObject("BattleAiMarkerTests");
            var definition = new MapAiMarker
            {
                Name = "Probe",
                Type = MapAiMarkerType.Snipe,
                Radius = 12.5f,
                Priority = 3,
                Origin = new[] { 10f, 2f, 30f },
            };

            var marker = BattleAiMarker.Create(_root.transform, definition);
            Assert.AreEqual(new Vector3(10f, 2f, -30f), marker.transform.position);
            Assert.AreEqual(12.5f, marker.Radius, Tolerance);
            Assert.AreEqual(3, marker.Priority);
            Assert.IsTrue(marker.IsSniper());
            Assert.IsFalse(marker.IsNormal());
            Assert.IsTrue(marker.CanOccupy());

            marker.Occupy(marker);
            Assert.IsFalse(marker.CanOccupy());
            marker.Occupy(null);
            Assert.IsTrue(marker.CanOccupy());

            _roster = BattleRoster.Begin(new[] { marker }, 0);
            Assert.AreSame(_roster, BattleRoster.Current);
            Assert.AreEqual(1, _roster.AiMarkers.Count);
            Assert.IsFalse(_roster.IsOngoing);
            _roster.StartBattle();
            Assert.IsTrue(_roster.IsOngoing);
            Assert.GreaterOrEqual(_roster.GetTime(), 0f);
        }

        [Test]
        public void SkillTablesKeepSourceReactionAndTierSelection()
        {
            Assert.AreEqual(0.35f,
                AiSkillTable.BaseReactionTime(AiSkillLevel.WhiteArmy), Tolerance);
            Assert.AreEqual(0.25f,
                AiSkillTable.BaseReactionTime(AiSkillLevel.PurpleArmy), Tolerance);
            Assert.AreEqual(30, AiSkillTable.HeavyArmorForLevel(1));
            Assert.AreEqual(80, AiSkillTable.HeavyWeightForLevel(10));
            Assert.IsTrue(AiSkillTable.IsDumb(AiSkillLevel.WhiteArmy));
            Assert.IsFalse(AiSkillTable.IsDumb(AiSkillLevel.GreenArmy));

            var mediumTier = AiSkillTable.ChooseSkill(
                6, false, false, chance => Math.Abs(chance - 75f) < Tolerance);
            Assert.AreEqual(AiSkillLevel.BlueArmy, mediumTier);

            var unfairEnemy = AiSkillTable.ChooseSkill(
                1, true, true, chance => Math.Abs(chance - 25f) < Tolerance);
            Assert.AreEqual(AiSkillLevel.PurpleArmy, unfairEnemy);
        }

        [Test]
        public void PlayStyleScoringRecognisesSlowLightAndCasemateProfiles()
        {
            var slow = new AiTactics.PlayStyleInputs
            {
                Level = 1,
                Health = 100f,
                ApPenetration = 100f,
                ApDamage = 10f,
                DeviationLeft = 180f,
                SpeedLimitForward = 30f,
            };
            Assert.AreEqual(
                AiPlayStyle.Heavy,
                AiTactics.ChoosePlayStyle(
                    slow, AiSkillLevel.GreenArmy, AiPlayStyle.Generic));

            var light = new AiTactics.PlayStyleInputs
            {
                ClassIsLight = true,
                Level = 5,
                Health = 800f,
                ApPenetration = 150f,
                ApDamage = 160f,
                DeviationLeft = 180f,
                SpeedLimitForward = 70f,
                EnginePower = 500f,
                MassTons = 20f,
                TrackRotationSpeed = 40f,
            };
            Assert.AreEqual(
                AiPlayStyle.Light,
                AiTactics.ChoosePlayStyle(
                    light, AiSkillLevel.GreenArmy, AiPlayStyle.Generic));

            var casemate = new AiTactics.PlayStyleInputs
            {
                ClassIsTd = true,
                Level = 5,
                Health = 800f,
                AvgArmor = 10f,
                ApPenetration = 100f,
                ApDamage = 100f,
                DeviationLeft = 15f,
                SpeedLimitForward = 45f,
            };
            Assert.AreEqual(
                AiPlayStyle.Sniper,
                AiTactics.ChoosePlayStyle(
                    casemate, AiSkillLevel.GreenArmy, AiPlayStyle.Generic));
        }

        [Test]
        public void DangerAndTargetRatingsKeepTheSourceThresholds()
        {
            Assert.AreEqual(-2, AiTactics.DangerLevel(new AiTactics.DangerInputs()));

            var danger = AiTactics.DangerLevel(new AiTactics.DangerInputs
            {
                LevelDifference = 1,
                HealthCoeff = 2f,
                DamageToMyHealth = 0.8f,
                DpmToMyHealth = 2f,
                PenetrationToMyArmor = 7f,
            });
            Assert.AreEqual(18, danger);

            var rating = AiTactics.TargetRating(new AiTactics.TargetRatingInputs
            {
                Visible = true,
                SkillAtLeastGreen = true,
                HealthCoeff = 2f,
                LookingAtMe = false,
                DistanceSquared = 100f * 100f,
                AllyToEnemyNear = 1f,
                DangerLevel = 0,
            });
            Assert.AreEqual(11f, rating, Tolerance);
        }

        [Test]
        public void StateScoringKeepsLightRolloutAndLowHealthRetreat()
        {
            var rollout = AiTactics.ChooseState(new AiTactics.StateInputs
            {
                Light = true,
                HealthCoeff = 1f,
                BattleTime = 20f,
                EnemiesNear = 0,
                HasDistantMarker = false,
            });
            Assert.AreEqual(AiStateKind.Rollout, rollout);

            var careful = AiTactics.ChooseState(new AiTactics.StateInputs
            {
                HealthCoeff = 0.25f,
                BattleTime = 100f,
                EnemiesNear = 4,
                HasDistantMarker = false,
            });
            Assert.AreEqual(AiStateKind.Careful, careful);
        }

        [Test]
        public void RosterScoresKillsAndEndsOnlyAfterTeamElimination()
        {
            _root = new GameObject("BattleRosterScoreTests");
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(BattleSession.DefaultTankId);
            var area = new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f));

            var spawnA = new GameObject("SpawnA").transform;
            spawnA.SetParent(_root.transform, false);
            spawnA.position = new Vector3(-10f, 10f, 0f);
            var spawnB1 = new GameObject("SpawnB1").transform;
            spawnB1.SetParent(_root.transform, false);
            spawnB1.position = new Vector3(10f, 10f, 0f);
            var spawnB2 = new GameObject("SpawnB2").transform;
            spawnB2.SetParent(_root.transform, false);
            spawnB2.position = new Vector3(20f, 10f, 0f);

            var ally = BattleTank.Spawn(
                _root.transform, "Ally", card, spawnA, area, 0);
            var enemyOne = BattleTank.Spawn(
                _root.transform, "EnemyOne", card, spawnB1, area, 1);
            var enemyTwo = BattleTank.Spawn(
                _root.transform, "EnemyTwo", card, spawnB2, area, 1);

            _roster = BattleRoster.Begin(null, 0);
            _roster.Add(ally);
            _roster.Add(enemyOne);
            _roster.Add(enemyTwo);
            var destroyedEvents = 0;
            _roster.TankGotDestroyed += ignored => destroyedEvents++;
            _roster.StartBattle();

            enemyOne.Health.ReceiveDamageBasic(10000);
            Assert.AreEqual(1, _roster.TeamAScore);
            Assert.AreEqual(0, _roster.TeamBScore);
            Assert.IsTrue(_roster.IsOngoing);
            Assert.AreEqual(-1, _roster.WinnerTeam);

            enemyTwo.Health.ReceiveDamageBasic(10000);
            Assert.AreEqual(2, _roster.TeamAScore);
            Assert.AreEqual(2, destroyedEvents);
            Assert.IsFalse(_roster.IsOngoing);
            Assert.AreEqual(0, _roster.WinnerTeam);
        }

        [Test]
        public void HidingSpotUsesNearestSourceCoverAndRejectsUnsafeObjects()
        {
            _root = new GameObject("BattleAiHideTests");
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(BattleSession.DefaultTankId);
            var area = new Bounds(Vector3.zero, new Vector3(1000f, 400f, 1000f));

            var allySpawn = new GameObject("HideAllySpawn").transform;
            allySpawn.SetParent(_root.transform, false);
            allySpawn.position = new Vector3(0f, 10f, 0f);
            var enemySpawn = new GameObject("HideEnemySpawn").transform;
            enemySpawn.SetParent(_root.transform, false);
            enemySpawn.position = new Vector3(0f, 10f, 300f);

            var ally = BattleTank.Spawn(
                _root.transform, "HideAlly", card, allySpawn, area, 0);
            var enemy = BattleTank.Spawn(
                _root.transform, "HideEnemy", card, enemySpawn, area, 1);
            Assert.AreEqual(9, ally.AvgArmor,
                "the source min_armor=0/elif walk averages the client's 8 and " +
                "18 mm MS-1 plates to 9 mm");
            var noHide = CreateCover(
                "NoHide", new Vector3(0f, 0f, -10f), new Vector3(12f, 12f, 12f), true);
            var tooSmall = CreateCover(
                "TooSmall", new Vector3(0f, 0f, -12f), Vector3.one * 0.25f, false);
            var valid = CreateCover(
                "ValidCover", new Vector3(0f, 0f, -20f), Vector3.one * 10f, false);

            _roster = BattleRoster.Begin(null, 0, new[] { noHide, tooSmall, valid });
            _roster.Add(ally);
            _roster.Add(enemy);
            // Spotting starts a battle blind now, and the AI only reacts to
            // enemies it can see, so this fixture spots the target it is about
            // to hide from.
            enemy.SetSpotted(true);
            var ai = AiTankController.Attach(ally, AiSkillLevel.GreenArmy);
            Physics.SyncTransforms();

            var hidingSpot = ai.FindHidingSpot(enemy.Pos());
            var expectedRadius = 0.5f * (valid.Size.x + valid.Size.y) + ally.HullWidth;
            Assert.AreNotEqual(Vector3.zero, hidingSpot);
            Assert.AreEqual(valid.Position.x, hidingSpot.x, Tolerance);
            Assert.AreEqual(valid.Position.z - expectedRadius, hidingSpot.z, Tolerance);
            Assert.Greater(
                Mathf.Abs(hidingSpot.z - valid.Position.z),
                valid.Size.z * 0.5f,
                "the source radius must place the tank beyond the cover AABB");
            Assert.AreEqual(hidingSpot, ai.FindHidingSpot(enemy.Pos()));
            Assert.AreEqual(0.75f, AiTankController.HideNavigationCacheSeconds, Tolerance);
            Assert.AreEqual(3f, AiTankController.HideUnreachableRetrySeconds, Tolerance);
            Assert.AreEqual(400f, AiTankController.HideObjectMaxDistance, Tolerance);
            Assert.AreEqual(0.3f, AiTankController.HideDirectionDotThreshold, Tolerance);
            var validRoot = _root.transform.Find("ValidCover");
            Assert.IsNotNull(validRoot);

            enemy.transform.position = new Vector3(0f, 10f, 100f);
            Physics.SyncTransforms();
            Assert.AreEqual(Vector3.zero, ai.FindHidingSpot(enemy.Pos()),
                "an enemy inside the source 150²×3 exclusion rejects the cover");

            validRoot.position = new Vector3(399f, 0f, 0f);
            Physics.SyncTransforms();
            var awayFromEnemyAtTank = new Vector2(
                ally.Pos().x - enemy.Pos().x,
                ally.Pos().z - enemy.Pos().z).normalized;
            var awayFromEnemyAtCover = new Vector2(
                valid.Position.x - enemy.Pos().x,
                valid.Position.z - enemy.Pos().z).normalized;
            Assert.Less(
                Vector2.Dot(awayFromEnemyAtCover, awayFromEnemyAtTank),
                AiTankController.HideDirectionDotThreshold);
            Assert.Less(
                (ally.Pos() - valid.Position).sqrMagnitude,
                AiTankController.HideObjectMaxDistance
                    * AiTankController.HideObjectMaxDistance);
            Assert.Greater(
                (enemy.Pos() - valid.Position).sqrMagnitude,
                AiTankController.HideEnemyExclusionDistanceSquared);
            Assert.AreEqual(Vector3.zero, ai.FindHidingSpot(enemy.Pos()),
                "a cover below the source direction dot threshold is rejected");

            enemy.transform.position = new Vector3(0f, 10f, 300f);
            validRoot.position = new Vector3(0f, 0f, -401f);
            Physics.SyncTransforms();
            Assert.Greater(
                (ally.Pos() - valid.Position).sqrMagnitude,
                AiTankController.HideObjectMaxDistance
                    * AiTankController.HideObjectMaxDistance);
            Assert.Greater(
                (enemy.Pos() - valid.Position).sqrMagnitude,
                AiTankController.HideEnemyExclusionDistanceSquared);
            awayFromEnemyAtTank = new Vector2(
                ally.Pos().x - enemy.Pos().x,
                ally.Pos().z - enemy.Pos().z).normalized;
            awayFromEnemyAtCover = new Vector2(
                valid.Position.x - enemy.Pos().x,
                valid.Position.z - enemy.Pos().z).normalized;
            Assert.GreaterOrEqual(
                Vector2.Dot(awayFromEnemyAtCover, awayFromEnemyAtTank),
                AiTankController.HideDirectionDotThreshold);
            Assert.AreEqual(Vector3.zero, ai.FindHidingSpot(enemy.Pos()),
                "a source cover beyond 400 metres is rejected");

            validRoot.position = new Vector3(0f, 0f, -20f);
            Physics.SyncTransforms();
            var blockerSpawn = new GameObject("HideBlockerSpawn").transform;
            blockerSpawn.SetParent(_root.transform, false);
            blockerSpawn.position = valid.Position;
            var blocker = BattleTank.Spawn(
                _root.transform, "HideBlocker", card, blockerSpawn, area, 0);
            _roster.Add(blocker);
            // tank_is_valid() asks for a spotted tank, so the reservation only
            // sees a blocker its own side has actually found.
            blocker.SetSpotted(true);
            Physics.SyncTransforms();
            Assert.AreEqual(Vector3.zero, ai.FindHidingSpot(enemy.Pos()),
                "a live ally inside hull_len²×2 reserves the cover");
            Assert.IsFalse(ai.FollowMapGoldenPath(),
                "Rudniki has no authored golden paths");
        }

        private BattleAiCover CreateCover(
            string name, Vector3 position, Vector3 scale, bool aiNoHide)
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.name = name;
            root.transform.SetParent(_root.transform, false);
            root.transform.localPosition = position;
            root.transform.localScale = scale;
            var cover = BattleAiCover.Create(root, new MapObjectInstance
            {
                Name = name,
                Kind = MapObjectKind.Static,
                NavigationObstacle = true,
                AiNoHide = aiNoHide,
            });
            Assert.IsNotNull(cover);
            return cover;
        }

        [Test]
        public void FixedGunThreatTurnsOnlyOutsideTheTraverseAndInsideRange()
        {
            Assert.AreEqual(1, FixedGunThreatMath.CloseThreatTurn(
                10f, 10f, 20f, 20f, true, 50f));
            Assert.AreEqual(-1, FixedGunThreatMath.CloseThreatTurn(
                -10f, 10f, 20f, 20f, true, 50f));
            Assert.AreEqual(0, FixedGunThreatMath.CloseThreatTurn(
                1f, 20f, 20f, 20f, true, 50f));
            Assert.AreEqual(0, FixedGunThreatMath.CloseThreatTurn(
                100f, 100f, 20f, 20f, true, 50f));
            Assert.AreEqual(0, FixedGunThreatMath.CloseThreatTurn(
                10f, 10f, 20f, 20f, false, 50f));
            Assert.AreEqual(45f, FixedGunThreatMath.ResponseDistance(2f), Tolerance);
            Assert.AreEqual(70f, FixedGunThreatMath.ResponseDistance(20f), Tolerance);
        }

        [Test]
        public void HullHelpsOnlyASlowTraverseAndHandsTheRestBackToTheTurret()
        {
            // A slow turret with an enemy beside it gets the hull's help, on
            // the side the gun has to swing.
            Assert.AreEqual(1f, AiTankController.GunTraverseAssist(false, false, 90f, 30f, 20f, 0f, 10f));
            Assert.AreEqual(-1f, AiTankController.GunTraverseAssist(false, false, -90f, 30f, 20f, 0f, 10f));
            // A turret that finishes the swing on its own needs no help.
            Assert.AreEqual(0f, AiTankController.GunTraverseAssist(false, false, 60f, 30f, 120f, 0f, 10f));
            // Far away only a really long swing is worth turning the hull.
            Assert.AreEqual(0f, AiTankController.GunTraverseAssist(false, false, 60f, 200f, 30f, 0f, 10f));
            Assert.AreEqual(1f, AiTankController.GunTraverseAssist(false, false, 60f, 200f, 20f, 0f, 10f));
            // Below the engage angle the turret keeps it; once helping, the
            // hull only lets go near the line of fire.
            Assert.AreEqual(0f, AiTankController.GunTraverseAssist(false, false, 30f, 30f, 5f, 0f, 10f));
            Assert.AreEqual(0f, AiTankController.GunTraverseAssist(true, false, 11f, 30f, 20f, 0f, 10f));
            Assert.AreEqual(1f, AiTankController.GunTraverseAssist(true, false, 13f, 30f, 20f, 0f, 10f));
            // Right after a hand-back the hull waits, unless the target is far
            // out of the gun's line.
            Assert.AreEqual(0f, AiTankController.GunTraverseAssist(false, false, 60f, 30f, 20f, 0f, 0.5f));
            Assert.AreEqual(1f, AiTankController.GunTraverseAssist(false, false, 120f, 30f, 20f, 0f, 0.5f));
        }

        [Test]
        public void CasemateGunIsBroughtRoundByItsHullOnlyWithinRange()
        {
            Assert.AreEqual(0f, AiTankController.GunTraverseAssist(false, true, 20f, 350f, 0f, 10f, 10f));
            Assert.AreEqual(1f, AiTankController.GunTraverseAssist(false, true, 20f, 100f, 0f, 10f, 10f));
            Assert.AreEqual(-1f, AiTankController.GunTraverseAssist(false, true, -20f, 100f, 0f, 10f, 10f));
            Assert.AreEqual(0f, AiTankController.GunTraverseAssist(false, true, 5f, 100f, 0f, 10f, 10f),
                "inside the traverse arc the gun answers on its own");
            Assert.AreEqual(1f, AiTankController.GunTraverseAssist(true, true, 4f, 100f, 0f, 10f, 10f));
            Assert.AreEqual(0f, AiTankController.GunTraverseAssist(true, true, 3f, 100f, 0f, 10f, 10f));
            Assert.AreEqual(0f, AiTankController.GunTraverseAssist(false, true, 6f, 100f, 0f, 3f, 10f),
                "a narrow arc still engages only past the facing tolerance");
        }

        [Test]
        public void NavmeshDriveWithoutABakedMeshSteersStraightAtTheGoal()
        {
            AiTankController.ApplySettings(null);
            _root = new GameObject("BattleAiStraightDriveTests");
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(BattleSession.DefaultTankId);
            var area = new Bounds(Vector3.zero, new Vector3(1000f, 400f, 1000f));
            var spawn = new GameObject("StraightDriveSpawn").transform;
            spawn.SetParent(_root.transform, false);
            spawn.position = new Vector3(0f, 10f, 0f);
            var tank = BattleTank.Spawn(_root.transform, "StraightDriver", card, spawn, area, 0);
            _roster = BattleRoster.Begin(null, 0);
            _roster.Add(tank);
            _roster.StartBattle();
            var ai = AiTankController.Attach(tank, AiSkillLevel.GreenArmy);
            Physics.SyncTransforms();
            Assert.IsNull(BattleNavigation.Current, "the fixture needs a map without a baked navmesh");

            ai.DriveToPos(tank.Pos() + (tank.transform.forward * 80f), true);
            Assert.AreEqual(1f, tank.ForwardAxis, "a goal straight ahead is driven at, not parked at");
            Assert.AreEqual(0f, tank.RightAxis);

            ai.DriveToPos(tank.Pos() + (tank.transform.right * 80f), true);
            Assert.AreEqual(0f, tank.ForwardAxis, "a goal abeam is turned towards first");
            Assert.AreNotEqual(0f, tank.RightAxis);

            ai.DriveToPos(tank.Pos() + (tank.transform.forward * 1f), true);
            Assert.AreEqual(0f, tank.ForwardAxis, "a reached goal stops the hull");
            Assert.AreEqual(0f, tank.RightAxis);
        }
    }
}
