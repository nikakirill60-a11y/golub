using UnityEngine;

namespace BlitzLegacy.Direct
{
    /// <summary>
    /// Client-side error smoothing of one puppet. The playback pose is the
    /// "target"; when the target jumps (late snapshots after a loss replace
    /// the dead-reckoned guess), the jump is kept as an offset that fades out
    /// over <see cref="TimeConstant"/>, so the hull glides onto the server's
    /// pose instead of teleporting. A real teleport (respawn, return in time)
    /// is taken as is.
    /// </summary>
    public struct DirectSmoothing
    {
        /// <summary>Exponential time constant of the fade (about 90% gone after 2.3x).</summary>
        public const float TimeConstant = 0.12f;
        /// <summary>A target jump below this is ordinary motion noise (metres).</summary>
        public const float MinJump = 0.15f;
        /// <summary>A rotation jump below this is ordinary motion noise (degrees).</summary>
        public const float MinTurn = 5f;
        /// <summary>A jump at least this long is a real teleport and is never smoothed.</summary>
        public const float TeleportMeters = DirectClient.TeleportMeters;
        private const float Rest = 0.005f;

        private bool _valid;
        private Vector3 _target;
        private Quaternion _targetRotation;
        private Vector3 _offset;
        private Quaternion _turn;

        public Vector3 Offset { get { return _offset; } }
        public bool Active { get { return _valid && (_offset.sqrMagnitude > 0f || Quaternion.Angle(Turn, Quaternion.identity) > 0f); } }
        private Quaternion Turn { get { return _turn.w == 0f && _turn.x == 0f && _turn.y == 0f && _turn.z == 0f ? Quaternion.identity : _turn; } }

        public void Reset() { this = default(DirectSmoothing); }

        /// <summary>
        /// Feeds this frame's target pose and the motion it was following.
        /// Returns true when a new correction was taken up this frame.
        /// </summary>
        public bool Step(Vector3 target, Quaternion targetRotation, Vector3 velocity, float dt,
            out Vector3 position, out Quaternion rotation)
        {
            var corrected = false;
            if (!_valid || dt <= 0f)
            {
                _valid = true;
                _offset = Vector3.zero;
                _turn = Quaternion.identity;
            }
            else
            {
                var decay = Mathf.Exp(-dt / TimeConstant);
                _offset *= decay;
                _turn = Quaternion.Slerp(Quaternion.identity, Turn, decay);
                var expected = _target + velocity * dt;
                var jump = expected - target;
                if ((target - _target).sqrMagnitude >= TeleportMeters * TeleportMeters)
                {
                    _offset = Vector3.zero;
                    _turn = Quaternion.identity;
                }
                else
                {
                    if (jump.sqrMagnitude > MinJump * MinJump)
                    {
                        _offset += jump;
                        corrected = true;
                    }
                    var turn = Quaternion.Inverse(targetRotation) * _targetRotation;
                    if (Quaternion.Angle(turn, Quaternion.identity) > MinTurn)
                    {
                        _turn = turn * Turn;
                        corrected = true;
                    }
                    if (_offset.sqrMagnitude >= TeleportMeters * TeleportMeters) { _offset = Vector3.zero; }
                }
                if (_offset.sqrMagnitude < Rest * Rest) { _offset = Vector3.zero; }
                if (Quaternion.Angle(Turn, Quaternion.identity) < 0.05f) { _turn = Quaternion.identity; }
            }
            _target = target;
            _targetRotation = targetRotation;
            position = target + _offset;
            rotation = targetRotation * Turn;
            return corrected;
        }
    }
}
