using UnityEngine;

namespace BlitzLegacy.Runtime.Audio
{
    /// <summary>
    /// The one AudioListener. It sits on its own child of the persistent audio
    /// root, so carrying it to the battle view never drags the 2D voices and
    /// one-shots parented to that root. A battle hands it a pose source (Godot
    /// listens from the current camera); without one it rests on the root.
    /// Runs after the camera rigs have settled for the frame.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed class AudioListenerRig : MonoBehaviour
    {
        /// <summary>Where to listen from this frame; false when there is no view to follow.</summary>
        public delegate bool PoseSource(out Vector3 position, out Quaternion rotation);

        private static AudioListenerRig _active;

        /// <summary>The rig spatial voices measure from, if any.</summary>
        public static AudioListenerRig Active { get { return _active != null ? _active : null; } }

        public AudioListener Listener { get; private set; }

        /// <summary>The pose the listener follows; null rests it on the audio root.</summary>
        public PoseSource Source { get; set; }

        public static AudioListenerRig Create(Transform root)
        {
            var go = new GameObject("Listener");
            go.transform.SetParent(root, false);
            var rig = go.AddComponent<AudioListenerRig>();
            rig.Listener = go.AddComponent<AudioListener>();
            _active = rig;
            return rig;
        }

        /// <summary>Moves the listener onto the current pose.</summary>
        public void Follow()
        {
            if (Source != null && Source(out var position, out var rotation))
            {
                transform.SetPositionAndRotation(position, rotation);
                return;
            }

            transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        private void OnEnable()
        {
            _active = this;
        }

        private void LateUpdate()
        {
            Follow();
        }

        private void OnDestroy()
        {
            if (_active == this) { _active = null; }
        }
    }
}
