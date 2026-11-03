using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Source map_object_tree.gd behaviour: the first tank entering the trunk
    /// capsule tips the visible trunk and its crown away from the tank. The
    /// source default reaches 75 degrees in about 3.27 seconds at fall_speed
    /// 0.2; the spring easing keeps the same deliberate, weighty finish.
    /// </summary>
    public sealed class BattleTree : MonoBehaviour
    {
        private Transform _trunk;
        private Quaternion _from;
        private Quaternion _to;
        private float _duration;
        private float _elapsed;
        private bool _falling;
        private bool _fallen;

        public bool Falling
        {
            get { return _falling; }
        }

        public bool Fallen
        {
            get { return _fallen; }
        }

        public float Duration
        {
            get { return _duration; }
        }

        public void Bind(Transform trunk, float fallSpeed)
        {
            _trunk = trunk;
            // map_object_tree.gd: duration = (1 / fall_speed) * radians(75) * 0.5.
            _duration = Mathf.Deg2Rad * 75f * 0.5f / Mathf.Max(0.001f, fallSpeed);
        }

        /// <summary>Begin the one-shot fall away from an impact position.</summary>
        public bool FallFrom(Vector3 impactPosition)
        {
            if (_trunk == null || _falling || _fallen)
            {
                return false;
            }

            var away = transform.position - impactPosition;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f)
            {
                away = transform.forward;
                away.y = 0f;
            }

            away.Normalize();
            var axis = Vector3.Cross(Vector3.up, away).normalized;
            _from = _trunk.rotation;
            var targetWorld = Quaternion.AngleAxis(75f, axis) * _from;
            _to = Quaternion.Inverse(_trunk.parent.rotation) * targetWorld;
            _from = _trunk.localRotation;
            _elapsed = 0f;
            _falling = true;
            return true;
        }

        private void Update()
        {
            Advance(Time.deltaTime);
        }

        /// <summary>Deterministic step shared by gameplay and edit-mode tests.</summary>
        public void Advance(float deltaTime)
        {
            if (!_falling || _trunk == null || deltaTime <= 0f)
            {
                return;
            }

            _elapsed += deltaTime;
            var progress = Mathf.Clamp01(_elapsed / Mathf.Max(0.001f, _duration));

            // Ease-out spring: quick at the trunk, then one small settling arc.
            var eased = 1f - Mathf.Exp(-6f * progress) *
                Mathf.Cos(9f * progress);
            _trunk.localRotation = Quaternion.SlerpUnclamped(_from, _to, eased);
            if (progress < 1f)
            {
                return;
            }

            _trunk.localRotation = _to;
            _falling = false;
            _fallen = true;
        }
    }

    /// <summary>Forwards a child trigger to the tree while filtering non-tanks.</summary>
    public sealed class BattleTreeTrigger : MonoBehaviour
    {
        private BattleTree _tree;

        public void Bind(BattleTree tree)
        {
            _tree = tree;
        }

        private void OnTriggerEnter(Collider other)
        {
            TryEnter(other);
        }

        /// <summary>
        /// Shared trigger path so every tree family and approach direction can
        /// be exercised deterministically without running an edit-mode physics
        /// frame. Gameplay reaches the same method through OnTriggerEnter.
        /// </summary>
        public bool TryEnter(Collider other)
        {
            if (_tree == null || other == null)
            {
                return false;
            }

            var tank = other.GetComponentInParent<BattleTank>();
            return tank != null && _tree.FallFrom(tank.transform.position);
        }
    }
}
