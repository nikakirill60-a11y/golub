using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The overhead ATGM view: how it is entered and left, how far the aim
    /// point may be walked, how much the gun is lifted for a given range and
    /// the arc the guidance then flies the rocket along.
    /// </summary>
    public sealed class BattleArtyModeTests
    {
        private const float Step = 2f;
        private const float LengthMax = 40f;

        [Test]
        public void OneWheelStepPastTheLongestArmOpensTheView()
        {
            Assert.IsTrue(CameraTuning.EntersArty(LengthMax, Step, 1f, LengthMax));
        }

        [Test]
        public void AnArmWithRoomLeftKeepsTheArcadeRig()
        {
            Assert.IsFalse(CameraTuning.EntersArty(20f, Step, 1f, LengthMax));
        }

        [Test]
        public void ZoomingInNeverOpensTheView()
        {
            Assert.IsFalse(CameraTuning.EntersArty(LengthMax, Step, -1f, LengthMax));
            Assert.IsTrue(CameraTuning.LeavesArty(-1f));
            Assert.IsFalse(CameraTuning.LeavesArty(1f));
        }

        [Test]
        public void CameraPanIsSmoothAndFrameRateIndependent()
        {
            var start = new Vector3(-20f, 3f, 10f);
            var target = new Vector3(80f, 23f, 210f);

            var oneStep = BattleCamera.SmoothArtyAim(start, target, 0.2f);
            var twoSteps = BattleCamera.SmoothArtyAim(
                BattleCamera.SmoothArtyAim(start, target, 0.1f), target, 0.1f);

            Assert.Greater(Vector3.Distance(start, oneStep), 0f);
            Assert.Less(Vector3.Distance(start, oneStep), Vector3.Distance(start, target));
            Assert.Less(Vector3.Distance(oneStep, twoSteps), 1e-4f);
        }

        [Test]
        public void TheAimPointReachesAcrossTheMap()
        {
            // A short shell still gets a usable view, a long one gets its own
            // range, and nothing gets an unbounded one.
            Assert.AreEqual(ArtyMath.RangeMin, ArtyMath.Range(120f), 1e-4f);
            Assert.AreEqual(720f, ArtyMath.Range(720f), 1e-4f);
            Assert.AreEqual(ArtyMath.RangeMax, ArtyMath.Range(9000f), 1e-4f);
        }

        [Test]
        public void TheGunIsLiftedByTheRange()
        {
            const float Stop = 90f;

            var near = ArtyMath.LaunchPitch(10f, Stop);
            var far = ArtyMath.LaunchPitch(400f, Stop);

            Assert.Less(near, far);
            Assert.AreEqual(ArtyMath.LaunchPitchMin, ArtyMath.LaunchPitch(0f, Stop), 1e-4f);
            Assert.AreEqual(ArtyMath.LaunchPitchMax, far, 1e-4f);
        }

        [Test]
        public void TheGunNeverPassesItsOwnStop()
        {
            // A real gun elevates by about twelve degrees, and no view is
            // allowed to bend the barrel past that.
            Assert.AreEqual(12f, ArtyMath.LaunchPitch(400f, -12f), 1e-4f);
        }

        [Test]
        public void TheRocketIsSteeredAboveTheTargetAndSinksOntoIt()
        {
            var far = ArtyMath.LoftHeight(400f);
            var close = ArtyMath.LoftHeight(20f);

            Assert.AreEqual(ArtyMath.LoftCeiling, far, 1e-4f);
            Assert.Less(close, far);
            Assert.Greater(close, 0f);

            // On top of the target the lift is gone, so the last of the flight
            // is a dive onto the hull rather than a pass over it.
            Assert.AreEqual(0f, ArtyMath.LoftHeight(0f), 1e-4f);
        }

        [Test]
        public void TheDrawnArcBowsOverTheStraightLine()
        {
            Assert.AreEqual(0f, ArtyMath.ArcHeight(40f, 0f), 1e-4f);
            Assert.AreEqual(0f, ArtyMath.ArcHeight(40f, 1f), 1e-4f);
            Assert.AreEqual(40f, ArtyMath.ArcHeight(40f, 0.5f), 1e-4f);
        }

        [Test]
        public void TheCircleGrowsWithRangeAndStaysReadable()
        {
            // Two metres per hundred at four hundred metres is eight.
            Assert.AreEqual(8f, BattleArtyReticle.Radius(2f, 400f), 1e-4f);

            Assert.AreEqual(
                BattleArtyReticle.RadiusMin, BattleArtyReticle.Radius(2f, 1f), 1e-4f);
            Assert.AreEqual(
                BattleArtyReticle.RadiusMax, BattleArtyReticle.Radius(20f, 5000f), 1e-4f);
        }

        [Test]
        public void TheSightStopsAtWhateverBlocksIt()
        {
            var aim = new Vector3(0f, 0f, 300f);
            var wall = new Vector3(0f, 0f, 120f);

            Assert.AreEqual(aim, BattleArtyReticle.Sight(aim, false, wall));
            Assert.AreEqual(wall, BattleArtyReticle.Sight(aim, true, wall));
        }
    }
}
