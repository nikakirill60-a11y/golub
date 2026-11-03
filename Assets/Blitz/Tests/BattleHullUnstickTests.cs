using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// A hull that ends up inside a rock has to be walked back out: the client
    /// never lets a tank into a prop, and a wedged hull in the port stayed
    /// wedged until the battle ended.
    /// </summary>
    public sealed class BattleHullUnstickTests
    {
        private const float Step = 1f / 60f;
        private static readonly Vector3 HullSize = new Vector3(3f, 2f, 6f);

        private GameObject _tank;
        private GameObject _rock;
        private BattleHullUnstick _unstick;
        private Rigidbody _body;
        private BoxCollider _hull;
        private BoxCollider _rockCollider;

        [SetUp]
        public void SetUp()
        {
            _tank = new GameObject("tank");
            _tank.layer = BattleLayers.Physics;
            _body = _tank.AddComponent<Rigidbody>();
            _body.useGravity = false;
            _hull = _tank.AddComponent<BoxCollider>();
            _hull.size = HullSize;
            _unstick = _tank.AddComponent<BattleHullUnstick>();
            _unstick.Setup(_body, _hull);

            _rock = new GameObject("rock");
            _rock.layer = BattleLayers.Static;
            _rockCollider = _rock.AddComponent<BoxCollider>();
            _rockCollider.size = new Vector3(2f, 2f, 2f);
            Clear();
        }

        [TearDown]
        public void TearDown()
        {
            if (_tank != null) { Object.DestroyImmediate(_tank); }
            if (_rock != null) { Object.DestroyImmediate(_rock); }
        }

        /// <summary>Parks the rock well clear of the hull.</summary>
        private void Clear()
        {
            _rock.transform.position = new Vector3(50f, 0f, 0f);
        }

        /// <summary>Sinks the hull into the rock by the given depth.</summary>
        private void Overlap(float depth)
        {
            _rock.transform.position = _tank.transform.position + new Vector3(
                (HullSize.x * 0.5f) + 1f - depth, 0f, 0f);
        }

        /// <summary>
        /// Holds the overlap steady: the separation moves the body, and a hull
        /// the map keeps shoving back in never gets to walk away from it.
        /// </summary>
        private void Pin(float depth)
        {
            _tank.transform.position = Vector3.zero;
            _body.position = Vector3.zero;
            Overlap(depth);
        }

        [Test]
        public void ClearHull_IsLeftAlone()
        {
            Assert.AreEqual(Vector3.zero, _unstick.Resolve(Step, true, 0f));
            Assert.IsFalse(_unstick.Escaping, "nothing to escape from");
        }

        [Test]
        public void HullInsideARock_IsPushedBackOut()
        {
            Overlap(0.5f);

            var push = _unstick.Resolve(Step, false, 0f);

            Assert.Less(push.x, 0f, "the hull is walked away from the rock");
            Assert.AreEqual(
                BattleHullUnstick.PushSpeed * Step, push.magnitude, 0.0001f,
                "a deep overlap is walked off at the separation speed");
            Assert.AreEqual(0f, push.y, 0.0001f, "a rolling hull is not lifted");
        }

        [Test]
        public void ShallowContact_KeepsTheContact()
        {
            // Leaning on a wall has to stay solid, so the contact skin PhysX
            // keeps around every touch is never separated.
            Overlap(BattleHullUnstick.Skin * 0.5f);

            Assert.AreEqual(Vector3.zero, _unstick.Resolve(Step, true, 0f));
        }

        [Test]
        public void WedgedHull_EscapesWithLiftOnceItStopsMoving()
        {
            var elapsed = 0f;
            Vector3 push = Vector3.zero;
            while (elapsed <= BattleHullUnstick.StuckTime + Step)
            {
                // A hull the geometry keeps shoving back in - under a ledge,
                // between two rocks - never wins the separation on its own.
                Pin(0.5f);
                push = _unstick.Resolve(Step, true, 0f);
                elapsed += Step;
            }

            Assert.IsTrue(_unstick.Escaping, "a hull that cannot move is wedged");
            Assert.Greater(push.y, 0f, "the escape lifts the hull off the ledge");
            Assert.Less(push.x, 0f, "and keeps walking it out of the rock");
            Assert.AreEqual(
                BattleHullUnstick.EscapeSpeed * Step, push.magnitude, 0.0001f);
        }

        [Test]
        public void RepeatedPasses_WalkTheHullClearOfTheRock()
        {
            Overlap(0.5f);

            // Nothing holds the hull in, so the separation alone frees it.
            for (var i = 0; i < 60; i++)
            {
                _unstick.Resolve(Step, true, 0f);
            }

            Assert.AreEqual(
                Vector3.zero, _unstick.Resolve(Step, true, 0f),
                "the hull ends up outside the rock");
            Assert.IsFalse(_unstick.Escaping);
        }

        [Test]
        public void MovingHull_IsNeverTreatedAsWedged()
        {
            var elapsed = 0f;
            while (elapsed <= BattleHullUnstick.StuckTime * 2f)
            {
                Pin(0.5f);
                _unstick.Resolve(Step, true, BattleHullUnstick.StuckSpeed + 1f);
                elapsed += Step;
            }

            Assert.IsFalse(_unstick.Escaping, "a hull that is rolling is not stuck");
        }
    }
}
