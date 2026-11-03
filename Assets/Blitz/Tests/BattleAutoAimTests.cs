using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The ported auto aim: the automatic sniper zoom of
    /// <c>_update_sniper_auto_zoom</c> and the capture pull of
    /// <c>_stick_camera_to_target</c>, whose decisions are pinned here so a
    /// regression shows up without a live battle.
    /// </summary>
    public sealed class BattleAutoAimTests
    {
        [Test]
        public void SniperAutoZoom_StaysWideInAClinch()
        {
            Assert.AreEqual(
                CameraTuning.SniperFovMax,
                CameraTuning.SniperFovForTarget(10f, 3f),
                0.001f);
            Assert.AreEqual(
                CameraTuning.SniperFovMax,
                CameraTuning.SniperFovForTarget(
                    CameraTuning.SniperAutoCloseDisableDistance, 3f),
                0.001f);
        }

        [Test]
        public void SniperAutoZoom_FramesTheTargetByItsHeight()
        {
            // Past the framing distance the measured height owns the fov.
            var framed = 2f * Mathf.Atan(
                3f / (2f * 120f * CameraTuning.SniperAutoTargetScreenFraction))
                * Mathf.Rad2Deg;
            Assert.AreEqual(framed, CameraTuning.SniperFovForTarget(120f, 3f), 0.01f);

            // A taller tank at the same distance needs a wider view, and a
            // height the porter could not measure falls back to 3 m.
            Assert.Greater(
                CameraTuning.SniperFovForTarget(400f, 20f),
                CameraTuning.SniperFovForTarget(400f, 3f));
            Assert.AreEqual(
                CameraTuning.SniperFovForTarget(120f, 3f),
                CameraTuning.SniperFovForTarget(120f, 0f),
                0.001f);

            // Never past the ladder\'s own ends.
            Assert.GreaterOrEqual(
                CameraTuning.SniperFovForTarget(1000f, 3f), CameraTuning.SniperFovMin);
            Assert.LessOrEqual(
                CameraTuning.SniperFovForTarget(60f, 3f), CameraTuning.SniperFovMax);

            // Inside the handoff band the clinch view is only eased out of.
            var handoff = CameraTuning.SniperFovForTarget(60f, 3f);
            Assert.Less(handoff, CameraTuning.SniperFovMax);
            Assert.Greater(handoff, CameraTuning.SniperFovForTarget(75f, 3f));
        }

        [Test]
        public void SmoothSniperFov_DoesNotDependOnTheFramerate()
        {
            var once = CameraTuning.SmoothSniperFov(40f, 10f, 6f, 0.2f);
            var twice = CameraTuning.SmoothSniperFov(
                CameraTuning.SmoothSniperFov(40f, 10f, 6f, 0.1f), 10f, 6f, 0.1f);
            Assert.AreEqual(once, twice, 0.001f);
            Assert.AreEqual(40f, CameraTuning.SmoothSniperFov(40f, 10f, 6f, 0f), 0.0001f);
        }

        [Test]
        public void NearestZoomStep_SnapsToTheLadder()
        {
            Assert.AreEqual(0, CameraTuning.NearestZoomStep(40f));
            Assert.AreEqual(0, CameraTuning.NearestZoomStep(38f));
            Assert.AreEqual(2, CameraTuning.NearestZoomStep(11f));
            Assert.AreEqual(
                CameraTuning.ZoomSteps.Length - 1,
                CameraTuning.NearestZoomStep(CameraTuning.SniperFovMin));
        }

        [Test]
        public void EscapeBudget_OnlyGrowsWhileTurningAwayFromTheCapture()
        {
            // A swipe against the yaw error the pull still has to cover.
            Assert.IsTrue(CameraTuning.WantsAway(-1f, 0.5f));
            Assert.IsFalse(CameraTuning.WantsAway(1f, 0.5f));
            Assert.IsFalse(CameraTuning.WantsAway(1f, 0f));

            // A purely vertical swipe against the pitch pull leaves the capture
            // too, so tilting the camera can no longer be held by a lock.
            Assert.IsTrue(CameraTuning.LookWantsAway(0f, -1f, 0f, 0.3f));
            Assert.IsTrue(CameraTuning.LookWantsAway(0f, 1f, 0.2f, -0.3f));
            Assert.IsFalse(CameraTuning.LookWantsAway(0f, 1f, 0f, 0.3f));
            Assert.IsFalse(CameraTuning.LookWantsAway(0f, 0f, 0.5f, 0.5f));
            Assert.AreEqual(0.5f, CameraTuning.LookTravel(0.3f, -0.4f), 1e-5f);

            var budget = CameraTuning.EscapeBudget(0f, 0.2f, true);
            Assert.AreEqual(0.2f, budget, 0.0001f);

            // Settling back onto the target spends the budget again.
            Assert.AreEqual(0f, CameraTuning.EscapeBudget(budget, 0.5f, false), 0.0001f);

            // And it bleeds back on its own between swipes.
            Assert.AreEqual(0f, CameraTuning.DecayEscape(0.1f, 1f), 0.0001f);
            Assert.AreEqual(
                0.4f, CameraTuning.DecayEscape(1f, 1f), 0.0001f);

            Assert.IsFalse(CameraTuning.ReleasesCapture(0.3f, 25f));
            Assert.IsTrue(CameraTuning.ReleasesCapture(0.5f, 25f));
        }

        [Test]
        public void LerpAngle_TakesTheShortWayRound()
        {
            var half = CameraTuning.LerpAngle(3f, -3f, 0.5f);
            Assert.AreEqual(3f + (Mathf.PI * 2f - 6f) * 0.5f, half, 0.0001f);
            Assert.AreEqual(
                0f, CameraTuning.AngleDifference(1f, 1f + Mathf.PI * 2f), 0.0001f);
            Assert.AreEqual(1f, CameraTuning.LerpAngle(1f, 1f, 0.5f), 0.0001f);
        }

        [Test]
        public void AnchorWorld_FollowsTheColliderItWasTakenFrom()
        {
            var target = new GameObject("auto aim target").transform;
            var turret = new GameObject("turret").transform;
            turret.SetParent(target, false);
            turret.localPosition = new Vector3(0f, 1.2f, 0.4f);

            // The anchor is stored in the collider\'s own space on capture.
            var local = turret.InverseTransformPoint(turret.position);

            target.position = new Vector3(5f, 0f, -30f);
            target.rotation = Quaternion.Euler(0f, 20f, 0f);

            var tracked = BattleAutoAim.AnchorWorld(turret, local);
            Assert.Less(
                (turret.position - tracked).magnitude,
                0.0001f,
                "tracking follows collider translation and rotation");

            Object.DestroyImmediate(target.gameObject);
        }
    }
}
