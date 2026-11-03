using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class BattleCameraTests
    {
        private const float Tolerance = 1e-4f;

        [Test]
        public void PitchClampKeepsTheSixtyFiveDegreeArc()
        {
            var inside = 20f * Mathf.Deg2Rad;
            Assert.AreEqual(inside, BattleCamera.ClampPitch(inside), Tolerance);

            Assert.AreEqual(
                CameraTuning.AngleMax * Mathf.Deg2Rad,
                BattleCamera.ClampPitch(80f * Mathf.Deg2Rad),
                Tolerance);

            Assert.AreEqual(
                CameraTuning.AngleMin * Mathf.Deg2Rad,
                BattleCamera.ClampPitch(-80f * Mathf.Deg2Rad),
                Tolerance);
        }

        [Test]
        public void PitchClampNeverLetsANonFiniteAngleThrough()
        {
            Assert.AreEqual(0f, BattleCamera.ClampPitch(float.NaN));
            Assert.AreEqual(
                CameraTuning.AngleMax * Mathf.Deg2Rad,
                BattleCamera.ClampPitch(float.PositiveInfinity),
                Tolerance);
            Assert.AreEqual(
                CameraTuning.AngleMin * Mathf.Deg2Rad,
                BattleCamera.ClampPitch(float.NegativeInfinity),
                Tolerance);
        }

        [Test]
        public void IsFiniteRejectsNanAndInfinities()
        {
            Assert.IsTrue(CameraTuning.IsFinite(0f));
            Assert.IsTrue(CameraTuning.IsFinite(-123.5f));
            Assert.IsFalse(CameraTuning.IsFinite(float.NaN));
            Assert.IsFalse(CameraTuning.IsFinite(float.PositiveInfinity));
            Assert.IsFalse(CameraTuning.IsFinite(float.NegativeInfinity));
        }

        [Test]
        public void SpringPullKeepsTheMargin()
        {
            // A wall 10 m behind the pivot leaves the camera at 9.5 m.
            Assert.AreEqual(
                10f - CameraTuning.SpringMargin,
                BattleCamera.SpringDistance(16f, 10f),
                Tolerance);
        }

        [Test]
        public void SpringPullNeverGoesBehindThePivot()
        {
            // Inside the margin the arm collapses onto the pivot, it does not
            // swing the camera out in front of the tank.
            Assert.AreEqual(0f, BattleCamera.SpringDistance(16f, 0.2f), Tolerance);
            Assert.AreEqual(0f, BattleCamera.SpringDistance(16f, 0f), Tolerance);
        }

        [Test]
        public void SpringPullStopsAtTheArmLength()
        {
            // A hit further away than the arm cannot push the camera out.
            Assert.AreEqual(16f, BattleCamera.SpringDistance(16f, 100f), Tolerance);
        }

        [Test]
        public void PinchInsideTheDeadzoneDoesNothing()
        {
            Assert.AreEqual(0f, BattleCamera.PinchZoom(104f, 100f), Tolerance);

            // The source compares with a strict greater-than, so a move of
            // exactly one deadzone still counts as noise.
            Assert.AreEqual(
                0f,
                BattleCamera.PinchZoom(100f + CameraTuning.PinchDeadzone, 100f),
                Tolerance);
        }

        [Test]
        public void PinchSpreadPullsTheCameraCloser()
        {
            Assert.AreEqual(-1f, BattleCamera.PinchZoom(140f, 100f), Tolerance);
            Assert.AreEqual(1f, BattleCamera.PinchZoom(100f, 140f), Tolerance);
        }

        [Test]
        public void ZoomLadderEntersAndLeavesSniperAtTheSourceEdges()
        {
            // One more step in from the shortest arm crosses into sniper mode.
            Assert.IsTrue(CameraTuning.EntersSniper(
                CameraTuning.LengthMin, CameraTuning.LengthStep, -1f, CameraTuning.LengthMin));
            Assert.IsFalse(CameraTuning.EntersSniper(
                CameraTuning.LengthMax, CameraTuning.LengthStep, -1f, CameraTuning.LengthMin));

            // Widening past the widest sniper fov drops back to arcade.
            Assert.IsTrue(CameraTuning.LeavesSniper(CameraTuning.SniperFovMax, 1f));
            Assert.IsFalse(CameraTuning.LeavesSniper(CameraTuning.SniperFovMin, 1f));
        }
    }
}
