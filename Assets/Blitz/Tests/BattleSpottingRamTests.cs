using BlitzLegacy.Core.Battle;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Spotting, ram damage and the sixth-sense bulb, ported from base_tank /
    /// Global.request_ram_damage / ui_player_battle.
    /// </summary>
    public sealed class BattleSpottingRamTests
    {
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        [Test]
        public void SpotDistance_FollowsTheSourceCamouflageCurve()
        {
            Assert.That(SpottingMath.SpotDistance(400f, 0f), Is.EqualTo(400f));
            Assert.That(SpottingMath.SpotDistance(400f, 1f), Is.EqualTo(50f));
            Assert.That(SpottingMath.SpotDistance(400f, 0.5f), Is.EqualTo(225f));
            Assert.That(SpottingMath.SpotDistance(400f, -1f), Is.EqualTo(400f));
        }

        [Test]
        public void RamDamage_MatchesTheSourceFormula()
        {
            // Two 30 t hulls closing at 10 m/s: combined 75, individual 37.5,
            // speed 10 * 0.25.
            var combined = (30000f + 30000f) * RamMath.WeightCoefficient;
            var speed = 10f * RamMath.ClosingSpeedFactor;
            var dmg = RamMath.Damage(
                combined, 30000f * RamMath.WeightCoefficient, speed, false);
            Assert.That(dmg, Is.EqualTo(117.1875f).Within(0.001f));

            var lined = RamMath.Damage(
                combined, 30000f * RamMath.WeightCoefficient, speed, true);
            Assert.That(lined, Is.EqualTo(dmg * RamMath.SpallLinerFactor).Within(0.001f));

            // The lighter tank takes more: its own share of the pair is smaller.
            var mixed = (10000f + 50000f) * RamMath.WeightCoefficient;
            var toLight = RamMath.Damage(
                mixed, 10000f * RamMath.WeightCoefficient, speed, false);
            var toHeavy = RamMath.Damage(
                mixed, 50000f * RamMath.WeightCoefficient, speed, false);
            Assert.That(toLight, Is.GreaterThan(toHeavy));
        }

        [Test]
        public void Session_StartsWithEveryEnemyUnspottedAndWiresTheServices()
        {
            Random.InitState(982451653);
            _root = new GameObject("BattleSpottingRamTests");
            var session = BattleSession.Create(
                _root.transform, false, BattleWorld.DefaultMapId, "t-34", false, "Tester");

            Assert.That(session.Spotter, Is.Not.Null);
            Assert.That(session.Ram, Is.Not.Null);
            Assert.That(session.Lamp, Is.Not.Null);
            Assert.That(session.Player.Spotted, Is.False);
            for (var i = 0; i < session.Enemies.Count; i++)
            {
                Assert.That(session.Enemies[i].Spotted, Is.False, "enemy " + i);
            }

            // try_spot_tank grants everything inside 50 m unconditionally.
            var enemy = session.Enemies[0];
            enemy.transform.position = session.Player.transform.position + new Vector3(10f, 0f, 0f);
            Assert.That(BattleSpotter.TrySpot(session.Player, enemy), Is.True);

            enemy.SetSpotted(true);
            Assert.That(enemy.Spotted, Is.True);
        }

        [Test]
        public void Ram_FoldsBothNotificationsIntoOnePairAndResolvesAfterTheDelay()
        {
            Random.InitState(982451653);
            _root = new GameObject("BattleSpottingRamTests");
            var session = BattleSession.Create(
                _root.transform, false, BattleWorld.DefaultMapId, "t-34", false, "Tester");
            session.StartBattle();

            var ram = session.Ram;
            var player = session.Player;
            var enemy = session.Enemies[0];
            var before = enemy.Health.Health;

            ram.Enqueue(player, enemy, 20f, 0f);
            ram.Enqueue(enemy, player, 4f, 0f);
            Assert.That(ram.PendingCount, Is.EqualTo(1), "one pair");

            ram.Tick(RamMath.ResolveDelay * 0.5f);
            Assert.That(ram.PendingCount, Is.EqualTo(1), "not due yet");
            Assert.That(enemy.Health.Health, Is.EqualTo(before));

            ram.Tick(RamMath.ResolveDelay + 0.001f);
            Assert.That(ram.PendingCount, Is.EqualTo(0), "resolved");
            Assert.That(enemy.Health.Health, Is.LessThan(before), "ram damage");

            // An ally pair is never a ram.
            var ally = session.Allies[1] == player ? session.Allies[0] : session.Allies[1];
            ram.Enqueue(player, ally, 20f, 1f);
            Assert.That(ram.PendingCount, Is.EqualTo(0), "no friendly ram");
        }

        [Test]
        public void Lamp_LightsThreeSecondsAfterTheSpotAndHidesAfterFive()
        {
            _root = new GameObject("BattleSpottingRamTests");
            var canvas = new GameObject("canvas", typeof(RectTransform));
            canvas.transform.SetParent(_root.transform, false);
            var lamp = BattleSpottedLamp.Create(canvas.transform);

            lamp.Schedule(0f);
            lamp.Tick(BattleSpottedLamp.Delay - 0.1f);
            Assert.That(lamp.Visible, Is.False, "before the delay");

            lamp.Tick(BattleSpottedLamp.Delay);
            Assert.That(lamp.Visible, Is.True, "after the delay");

            lamp.Tick(BattleSpottedLamp.Delay + BattleSpottedLamp.Duration - 0.1f);
            Assert.That(lamp.Visible, Is.True, "still on");

            lamp.Tick(BattleSpottedLamp.Delay + BattleSpottedLamp.Duration);
            Assert.That(lamp.Visible, Is.False, "hidden again");
        }
    }
}
