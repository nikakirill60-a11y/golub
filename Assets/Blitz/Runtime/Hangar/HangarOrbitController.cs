using BlitzLegacy.Core.Input;
using UnityEngine;

namespace BlitzLegacy.Runtime.Hangar
{
    /// <summary>
    /// Unity equivalent of hangar_low.tscn's camera-root/spring-arm orbit.
    /// Pitch keeps the Godot spring-arm convention, where cam_angle_min = -60
    /// lifts the camera above the pivot and cam_angle_max = 5 leaves it hanging
    /// just below, so Unity applies the sign in reverse when placing the camera.
    /// </summary>
    public sealed class HangarOrbitController : MonoBehaviour
    {
        public const float SourceYaw = -71.08f;
        /// <summary>
        /// hangar_low.tscn saves the spring arm at 8.066 degrees, but its camera
        /// script clamps rotation.x into cam_angle_min..cam_angle_max on the
        /// first physics frame, so the hangar always opens at 5 degrees with the
        /// camera below the pivot looking up at the tank, not down at the floor.
        /// </summary>
        public const float SourcePitch = 8.066f;
        public const float SourceDistance = 9.59928f;
        public const float SourceFov = 50f;

        private Camera _camera;
        private float _sensitivity = HangarInputMath.DefaultSensitivity;
        private float _yaw = SourceYaw;
        private float _pitch = HangarInputMath.ClampPitch(SourcePitch);
        private Vector2 _velocity;
        private bool _dragging;
        private float _shift;
        private float _shiftTarget;
        private float _shiftSpeed;
        private bool _shifted;

        public float Yaw { get { return _yaw; } }
        public float Pitch { get { return _pitch; } }
        public bool IsDragging { get { return _dragging; } }

        /// <summary>The current horizontal view shift, in normalised device units.</summary>
        public float ViewShift { get { return _shift; } }

        /// <summary>The view shift a slide is heading for; equal to <see cref="ViewShift"/> at rest.</summary>
        public float ViewShiftTarget { get { return _shiftTarget; } }

        public void Configure(Camera camera, float sensitivity)
        {
            _camera = camera;
            _sensitivity = HangarInputMath.NormalizeSensitivity(sensitivity);
            if (_camera != null)
            {
                _camera.fieldOfView = SourceFov;
            }

            ApplyCamera();
        }

        public void SetView(float yaw, float pitch)
        {
            _yaw = yaw;
            _pitch = HangarInputMath.ClampPitch(pitch);
            _velocity = Vector2.zero;
            ApplyCamera();
        }

        public void BeginDrag()
        {
            _dragging = true;
            _velocity = Vector2.zero;
        }

        /// <summary>
        /// add_look() of hangar_low.tscn, in Unity's screen and camera frame.
        ///
        /// The source adds <c>-relative.x</c> to the yaw of a rig whose camera
        /// hangs at +Z, because a Godot camera looks down its own -Z. This
        /// orbit places the camera at -Z and looks back at the pivot, which
        /// mirrors that rig, so the copied minus sign turned the tank away from
        /// the finger: a swipe to the left spun it to the right. Unity's drag
        /// delta is also y-up where Godot's is y-down, and that flip is what
        /// the pitch below already accounts for.
        /// </summary>
        public void Drag(Vector2 pixelDelta)
        {
            var yawStep = HangarInputMath.DegreesForPixels(pixelDelta.x, _sensitivity);
            var pitchStep = HangarInputMath.DegreesForPixels(pixelDelta.y, _sensitivity);
            _velocity = new Vector2(yawStep, pitchStep);
            ApplyStep(_velocity);

            // The source moves the rig inside _physics_process, one frame
            // behind the input event. Placing the camera right here keeps the
            // tank under the finger instead of trailing it by a frame.
            ApplyCamera();
        }

        public void EndDrag()
        {
            _dragging = false;
        }

        /// <summary>
        /// Moves the picture sideways without turning the camera: the
        /// camouflage shop of the 4.8 client covers the left of the screen
        /// and shows the vehicle centred in the free part on the right. The
        /// shift is in normalised device units (2 is the whole width), so a
        /// panel covering a fraction f of the screen asks for f to centre
        /// the stand between the panel and the right edge. A positive
        /// <paramref name="seconds"/> slides there linearly, like the shop's
        /// 0.16 s panel transitions.
        /// </summary>
        public void SetViewShift(float shift, float seconds)
        {
            _shiftTarget = shift;
            if (seconds <= 0f)
            {
                _shift = shift;
                _shiftSpeed = 0f;
                ApplyProjection();
                return;
            }

            _shiftSpeed = Mathf.Abs(shift - _shift) / seconds;
        }

        /// <summary>One step of the view-shift slide; LateUpdate runs it on unscaled time.</summary>
        public void AdvanceViewShift(float deltaTime)
        {
            if (Mathf.Approximately(_shift, _shiftTarget))
            {
                return;
            }

            _shift = _shiftSpeed > 0f
                ? Mathf.MoveTowards(_shift, _shiftTarget, _shiftSpeed * Mathf.Max(0f, deltaTime))
                : _shiftTarget;
        }

        private void LateUpdate()
        {
            if (!_dragging && _velocity.sqrMagnitude > 0.000001f)
            {
                ApplyStep(_velocity);
                var damping = 1f - Mathf.Exp(-7.5f * Time.unscaledDeltaTime);
                _velocity = Vector2.Lerp(_velocity, Vector2.zero, damping);
            }

            AdvanceViewShift(Time.unscaledDeltaTime);
            ApplyCamera();
            ApplyProjection();
        }

        /// <summary>
        /// An off-centre projection: the default one with the principal point
        /// moved. It is rebuilt every frame from the camera's own field of
        /// view and aspect, so a resized window keeps the shift exact; with no
        /// shift the camera goes back to Unity's automatic projection.
        /// </summary>
        private void ApplyProjection()
        {
            if (_camera == null)
            {
                return;
            }

            if (Mathf.Approximately(_shift, 0f))
            {
                _shift = 0f;
                if (_shifted)
                {
                    _camera.ResetProjectionMatrix();
                    _shifted = false;
                }

                return;
            }

            _camera.ResetProjectionMatrix();
            var projection = _camera.projectionMatrix;
            projection[0, 2] = -_shift;
            _camera.projectionMatrix = projection;
            _shifted = true;
        }

        private void OnDisable()
        {
            if (_camera != null && _shifted)
            {
                _camera.ResetProjectionMatrix();
            }

            _shifted = false;
            _shift = _shiftTarget = 0f;
        }

        private void ApplyStep(Vector2 step)
        {
            _yaw += step.x;
            _pitch = HangarInputMath.ClampPitch(_pitch + step.y);
        }

        private void ApplyCamera()
        {
            if (_camera == null)
            {
                return;
            }

            var orbit = Quaternion.Euler(-_pitch, _yaw, 0f);
            var position = transform.position + orbit * new Vector3(0f, 0f, -SourceDistance);
            _camera.transform.position = position;
            _camera.transform.rotation = Quaternion.LookRotation(
                transform.position - position, Vector3.up);
        }
    }
}
