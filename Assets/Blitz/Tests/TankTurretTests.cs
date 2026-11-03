using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Locks the turret half of <c>assets/tanks/base/base_tank.gd</c>:
    /// <c>get_gun_pos</c>, the target angles of <c>rotate_turret</c> and
    /// <c>rotate_gun</c>, the deviation clamps, the hull nudge the traverse
    /// queues and the smoothing of <c>adjust_aim</c>.
    /// </summary>
    public sealed class TankTurretTests
    {
        private const float Tolerance = 1e-4f;

        /// <summary><c>get_gun_pos</c>: the muzzle pulled back down the barrel.</summary>
        [Test]
        public void GunOriginSitsBehindTheMuzzle()
        {
            var origin = TankTurret.GunOrigin(
                new Vector3(0f, 1.5f, 2f), Vector3.forward, 0.9f);

            // gun_mesh_len plus the source's 0.1 margin, back along the barrel.
            Assert.AreEqual(0f, origin.x, Tolerance);
            Assert.AreEqual(1.5f, origin.y, Tolerance);
            Assert.AreEqual(1f, origin.z, Tolerance);
        }

        /// <summary>sign(0) is 0 in the source, which recentres the turret.</summary>
        [Test]
        public void TargetYawIsZeroDeadAhead()
        {
            var yaw = TankTurret.TurretTargetYaw(
                Vector3.zero, new Vector3(0f, 0f, 10f), Vector3.forward, Vector3.up, 0f);

            Assert.AreEqual(0f, yaw, Tolerance);
        }

        /// <summary>
        /// The source signs the traverse with <c>sign(to_local(aim_target).x) * -1</c>;
        /// the Z mirror flips that, so a positive yaw traverses right here.
        /// </summary>
        [Test]
        public void TargetYawSignsFromTheAimSide()
        {
            var right = TankTurret.TurretTargetYaw(
                Vector3.zero, new Vector3(10f, 0f, 0f), Vector3.forward, Vector3.up, 10f);
            var left = TankTurret.TurretTargetYaw(
                Vector3.zero, new Vector3(-10f, 0f, 0f), Vector3.forward, Vector3.up, -10f);

            Assert.AreEqual(Mathf.PI * 0.5f, right, Tolerance);
            Assert.AreEqual(-Mathf.PI * 0.5f, left, Tolerance);
        }

        /// <summary>
        /// <c>dir_to_target.slide(up)</c> flattens the aim onto the hull plane,
        /// so target height never leaks into the traverse angle.
        /// </summary>
        [Test]
        public void TargetYawIgnoresTargetHeight()
        {
            var flat = TankTurret.TurretTargetYaw(
                Vector3.zero, new Vector3(10f, 0f, 0f), Vector3.forward, Vector3.up, 10f);
            var high = TankTurret.TurretTargetYaw(
                Vector3.zero, new Vector3(10f, 50f, 0f), Vector3.forward, Vector3.up, 10f);

            Assert.AreEqual(flat, high, Tolerance);
        }

        /// <summary>A target straight overhead leaves nothing to project.</summary>
        [Test]
        public void TargetYawHoldsWhenTheAimCollapses()
        {
            var yaw = TankTurret.TurretTargetYaw(
                Vector3.zero, new Vector3(0f, 10f, 0f), Vector3.forward, Vector3.up, 0f);

            Assert.AreEqual(0f, yaw, Tolerance);
        }

        /// <summary>Elevation is a negative pitch once the port mirrors the axis.</summary>
        [Test]
        public void TargetPitchSignsElevationNegative()
        {
            var level = TankTurret.GunTargetPitch(
                Vector3.zero, new Vector3(0f, 0f, 10f), Vector3.up);
            var up = TankTurret.GunTargetPitch(
                Vector3.zero, new Vector3(0f, 10f, 10f), Vector3.up);
            var down = TankTurret.GunTargetPitch(
                Vector3.zero, new Vector3(0f, -10f, 10f), Vector3.up);

            Assert.AreEqual(0f, level, Tolerance);
            Assert.AreEqual(-Mathf.PI * 0.25f, up, Tolerance);
            Assert.AreEqual(Mathf.PI * 0.25f, down, Tolerance);
        }

        /// <summary>
        /// <c>deviation_r.x</c> is the left half of the arc and <c>deviation_r.y</c>
        /// the right one, so an asymmetric card clamps asymmetrically.
        /// </summary>
        [Test]
        public void YawClampKeepsTheArcSides()
        {
            Assert.AreEqual(
                30f * Mathf.Deg2Rad, TankTurret.ClampYaw(Mathf.PI, 20f, 30f), Tolerance);
            Assert.AreEqual(
                -20f * Mathf.Deg2Rad, TankTurret.ClampYaw(-Mathf.PI, 20f, 30f), Tolerance);
        }

        /// <summary><c>deviation_up.x</c> elevates and <c>deviation_up.y</c> depresses.</summary>
        [Test]
        public void PitchClampKeepsTheArcSides()
        {
            Assert.AreEqual(
                -20f * Mathf.Deg2Rad, TankTurret.ClampPitch(-Mathf.PI, 20f, 8f), Tolerance);
            Assert.AreEqual(
                8f * Mathf.Deg2Rad, TankTurret.ClampPitch(Mathf.PI, 20f, 8f), Tolerance);
        }

        /// <summary>
        /// <c>rotate_turret</c> queues a low priority drive so the hull carries
        /// the turret the rest of the way to a target outside the arc.
        /// </summary>
        [Test]
        public void SteerAssistNudgesTheHullPastTheLimits()
        {
            Assert.AreEqual(-1f, TankTurret.SteerAssist(-1f, 20f, 20f), Tolerance);
            Assert.AreEqual(1f, TankTurret.SteerAssist(1f, 20f, 20f), Tolerance);
            Assert.AreEqual(0f, TankTurret.SteerAssist(0.1f, 20f, 20f), Tolerance);
        }

        /// <summary><c>gun_deviation_reached</c> fires on any of the four edges.</summary>
        [Test]
        public void DeviationReachedFiresOnTheArcEdges()
        {
            Assert.IsTrue(TankTurret.IsDeviationReached(
                20f * Mathf.Deg2Rad, 0f, 20f, 20f, 20f, 8f));
            Assert.IsTrue(TankTurret.IsDeviationReached(
                0f, 8f * Mathf.Deg2Rad, 20f, 20f, 20f, 8f));
            Assert.IsFalse(TankTurret.IsDeviationReached(
                0f, 0f, 20f, 20f, 20f, 8f));
        }

        /// <summary>
        /// <c>adjust_aim</c> snaps past 50 * 2 square metres and eases with
        /// delta * 10 below that.
        /// </summary>
        [Test]
        public void AimSnapsWhenItJumpsAndEasesWhenItCreeps()
        {
            var snapped = TankTurret.AdjustAim(
                Vector3.zero, new Vector3(0f, 0f, 20f), 1f / 60f);
            var eased = TankTurret.AdjustAim(
                Vector3.zero, new Vector3(0f, 0f, 5f), 1f / 60f);
            var edge = TankTurret.AdjustAim(
                Vector3.zero, new Vector3(0f, 0f, 10f), 1f / 60f);

            Assert.AreEqual(20f, snapped.z, Tolerance);
            Assert.AreEqual(5f / 6f, eased.z, Tolerance);
            // Exactly 100 is not greater than 100, so the edge still eases.
            Assert.AreEqual(10f / 6f, edge.z, Tolerance);
        }
    }
}
