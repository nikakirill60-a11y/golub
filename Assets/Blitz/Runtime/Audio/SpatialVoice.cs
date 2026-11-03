using System.Collections.Generic;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Runtime.Battle;
using UnityEngine;

namespace BlitzLegacy.Runtime.Audio
{
    /// <summary>
    /// One spatial AudioSource heard the <see cref="SpatialAudio"/> way. The
    /// distance law rides Unity's custom rolloff curve, so out-of-range voices
    /// still drop to virtual like any other; this component adds what the
    /// mixer lacks: air absorption, and occlusion by the static world, probed a
    /// few times a second on the main thread and glided on the audio thread.
    /// Runs after <see cref="AudioListenerRig"/> has moved the listener.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [DefaultExecutionOrder(1001)]
    public sealed class SpatialVoice : MonoBehaviour
    {
        /// <summary>Seconds between two occlusion probes of a live voice.</summary>
        public const float ProbeInterval = 0.1f;

        /// <summary>Tank rigs and impacts sit on the ground plane; the probe aims this far above.</summary>
        public const float SourceLift = 1f;

        /// <summary>The probe stops this short of both ends: the camera's own clearance and the surface a sound sits on.</summary>
        public const float EndMargin = 0.5f;

        /// <summary>Rolloff knots between the reference and the cut.</summary>
        public const int CurveKnots = 24;

        private const int OcclusionMask = 1 << BattleLayers.Static;

        private static readonly Dictionary<SpatialRange, AnimationCurve> Curves =
            new Dictionary<SpatialRange, AnimationCurve>();

        private readonly SpatialFilter _filter = new SpatialFilter();
        private AudioSource _source;
        private SpatialRange _range;
        private float _occlusion;
        private float _occlusionTarget;
        private float _nextProbe;
        private bool _primed;
        private int _sampleRate = AudioOutput.SampleRate;
        private volatile float _gain = 1f;
        private volatile float _cutoff = SpatialAudio.OpenCutoffHz;

        public SpatialRange Range { get { return _range; } }

        /// <summary>The glided occlusion amount, 0 (clear) to 1.</summary>
        public float Occlusion { get { return _occlusion; } }

        /// <summary>The gain the audio thread glides to.</summary>
        public float Gain { get { return _gain; } }

        /// <summary>The low-pass corner the audio thread filters at.</summary>
        public float Cutoff { get { return _cutoff; } }

        /// <summary>Makes <paramref name="source"/> a full 3D voice over <paramref name="range"/>.</summary>
        public static SpatialVoice Configure(AudioSource source, SpatialRange range)
        {
            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            var voice = source.GetComponent<SpatialVoice>();
            if (voice == null) { voice = source.gameObject.AddComponent<SpatialVoice>(); }
            voice._source = source;
            voice.SetRange(range);
            return voice;
        }

        /// <summary>Re-authors the distances, e.g. the near or the distant muzzle report.</summary>
        public void SetRange(SpatialRange range)
        {
            if (range.IsEmpty) { range = SpatialAudio.OneShot; }
            _range = range;
            if (_source == null) { _source = GetComponent<AudioSource>(); }
            _source.rolloffMode = AudioRolloffMode.Custom;
            // Keep min <= max at every step, whichever way the range moves.
            if (range.Reference > _source.maxDistance)
            {
                _source.maxDistance = range.Max;
                _source.minDistance = range.Reference;
            }
            else
            {
                _source.minDistance = range.Reference;
                _source.maxDistance = range.Max;
            }
            _source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, RolloffCurve(range));
        }

        /// <summary>
        /// <see cref="SpatialAudio.DistanceGain"/> as a custom rolloff curve
        /// over distance / max, with knots spaced geometrically from the
        /// reference, where the law bends, out to the cut. Shared per range.
        /// </summary>
        public static AnimationCurve RolloffCurve(SpatialRange range)
        {
            if (Curves.TryGetValue(range, out var cached)) { return cached; }

            var knee = range.Reference / range.Max;
            var keys = new Keyframe[CurveKnots + 2];
            keys[0] = new Keyframe(0f, 1f, 0f, 0f);
            for (var i = 0; i <= CurveKnots; i++)
            {
                var x = knee * Mathf.Pow(1f / knee, (float)i / CurveKnots);
                var h = x * 1e-3f;
                var y = CurveGain(range, x);
                keys[i + 1] = new Keyframe(
                    x, y, (y - CurveGain(range, x - h)) / h, (CurveGain(range, x + h) - y) / h);
            }

            var curve = new AnimationCurve(keys);
            Curves[range] = curve;
            return curve;
        }

        private static float CurveGain(SpatialRange range, float share)
        {
            return SpatialAudio.DistanceGain(share * range.Max, range);
        }

        /// <summary>
        /// Occlusion between <paramref name="listener"/> and a voice at
        /// <paramref name="voice"/>: none in the clear, otherwise from the
        /// thickness of static world along the line, measured from both ends.
        /// </summary>
        public static float Probe(Vector3 listener, Vector3 voice)
        {
            var delta = voice - listener;
            var distance = delta.magnitude;
            var span = distance - 2f * EndMargin;
            if (span <= 0f) { return 0f; }

            var direction = delta / distance;
            if (!Physics.Raycast(listener + direction * EndMargin, direction, out var front,
                span, OcclusionMask, QueryTriggerInteraction.Ignore))
            {
                return 0f;
            }

            var back = Physics.Raycast(voice - direction * EndMargin, -direction, out var rear,
                span, OcclusionMask, QueryTriggerInteraction.Ignore) ? rear.distance : 0f;
            return SpatialAudio.Occlusion(true, span - front.distance - back);
        }

        /// <summary>
        /// One main-thread step: probe when due and publish the gain and corner
        /// the audio thread glides to. A silent, 2D or out-of-range voice is
        /// left clear and costs no probe.
        /// </summary>
        public void Refresh(Vector3? listener, bool live, float now, float deltaTime)
        {
            var position = transform.position + Vector3.up * SourceLift;
            var distance = listener.HasValue ? Vector3.Distance(listener.Value, position) : 0f;
            if (!listener.HasValue || !live || distance >= _range.Max)
            {
                _primed = false;
                _occlusion = 0f;
                _occlusionTarget = 0f;
                _gain = 1f;
                _cutoff = SpatialAudio.OpenCutoffHz;
                return;
            }

            if (!_primed || now >= _nextProbe)
            {
                _occlusionTarget = Probe(listener.Value, position);
                _nextProbe = now + ProbeInterval;
            }

            // A voice that starts behind a wall starts muffled; afterwards it glides.
            _occlusion = _primed
                ? SpatialAudio.Smooth(_occlusion, _occlusionTarget, deltaTime)
                : _occlusionTarget;
            _primed = true;
            _gain = SpatialAudio.OcclusionGain(_occlusion);
            _cutoff = Mathf.Min(
                SpatialAudio.AirCutoff(distance, _range),
                SpatialAudio.OcclusionCutoff(_occlusion));
        }

        private void Awake()
        {
            if (_source == null) { _source = GetComponent<AudioSource>(); }
            _sampleRate = AudioSettings.outputSampleRate;
        }

        private void LateUpdate()
        {
            var rig = AudioListenerRig.Active;
            var live = _source != null && _source.isPlaying && !_source.mute
                && _source.spatialBlend >= 0.5f;
            Refresh(rig != null ? rig.transform.position : (Vector3?)null,
                live, Time.time, Time.deltaTime);
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            _filter.Process(data, channels, _gain, _cutoff, _sampleRate);
        }
    }
}
