using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class TankDriveResponseTests
    {
        [TestCase(1f, -9f)][TestCase(-1f, 9f)]
        public void OppositeOverspeedCannotDisableRequestedDrive(float axis, float speed)
        {
            var acceleration = TankSuspension.DriveAcceleration(speed, axis, 100000, 1750, 25, 15, 1, false, 1f / 60);
            Assert.Greater(acceleration * axis, 7f);
            Assert.LessOrEqual(Mathf.Abs(acceleration), 8.5f);
        }

        [TestCase(50)][TestCase(60)][TestCase(100)]
        public void UphillCommandStopsDownhillRollbackWithinTwoSeconds(int hz)
        {
            var speed = -6f;
            var elapsed = 0f;
            var gravity = -10f * Mathf.Sin(20f * Mathf.Deg2Rad);
            while (speed < .1f && elapsed < 2f)
            {
                speed += (TankSuspension.DriveAcceleration(speed, 1, 100000, 1750, 25, 15, 1, false, 1f / hz, gravity) + gravity) / hz;
                elapsed += 1f / hz;
            }
            Assert.Greater(speed, .1f);
            Assert.Less(elapsed, 2f);
        }

        [TestCase("ms-1")][TestCase("maus")][TestCase("t-54")]
        public void FlatDriveRetainsCardSpeedAndImprovesAcceleration(string id)
        {
            var card = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData)).Get(id);
            var mass = BattleTank.MassOf(card, 0, 0, 0);
            var horsepower = card.Engines[0].Power;
            var speed = 0f;
            for (var step = 0; step < 30 * 60; step++)
                speed += TankSuspension.DriveAcceleration(speed, 1, mass, horsepower, card.SpeedForward, card.SpeedBackward, 1, false, 1f / 60) / 60;
            Assert.GreaterOrEqual(speed * 3.6f, card.SpeedForward * .95f);
            Assert.Less(speed * 3.6f, card.SpeedForward * 1.03f);
            var originalStart = horsepower * 745.7f * .82f / (mass * 2f);
            var newStart = TankSuspension.DriveAcceleration(0, 1, mass, horsepower, card.SpeedForward, card.SpeedBackward, 1, false, 1f / 60);
            Assert.Greater(newStart, originalStart);
        }

        [Test] public void RequestedLimitBlockedAndAirborneCannotCreatePoweredAcceleration()
        {
            Assert.LessOrEqual(TankSuspension.DriveAcceleration(8, 1, 100000, 1750, 25, 15, 1, false, .02f), 0);
            Assert.GreaterOrEqual(TankSuspension.DriveAcceleration(-5, -1, 100000, 1750, 25, 15, 1, false, .02f), 0);
            Assert.AreEqual(0, TankSuspension.DriveAcceleration(0, 1, 100000, 1750, 25, 15, 1, true, .02f));
            Assert.AreEqual(0, TankSuspension.DriveAcceleration(-9, 1, 100000, 1750, 25, 15, 0, false, .02f));
        }
    }
}
