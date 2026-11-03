namespace BlitzLegacy.Core.Audio
{
    /// <summary>
    /// The DSP half of <see cref="SpatialAudio"/> for one voice, run on the
    /// audio thread over interleaved buffers: a gain ramped across each buffer
    /// and a 12 dB/octave low-pass built from two one-pole stages, which stay
    /// stable however fast the corner moves. A clear voice passes untouched.
    /// </summary>
    public sealed class SpatialFilter
    {
        /// <summary>Gains this close to unity count as unity.</summary>
        public const float UnityGain = 0.9999f;

        private float[] _stage1 = new float[2];
        private float[] _stage2 = new float[2];
        private float _gain = 1f;
        private float _cutoff = SpatialAudio.OpenCutoffHz;

        /// <summary>The gain the last buffer ended on.</summary>
        public float Gain { get { return _gain; } }

        /// <summary>
        /// Filters <paramref name="data"/> in place, gliding from the previous
        /// buffer's gain to <paramref name="gain"/> and low-passing at
        /// <paramref name="cutoffHz"/> unless both are open.
        /// </summary>
        public void Process(float[] data, int channels, float gain, float cutoffHz, int sampleRate)
        {
            if (data == null || channels <= 0) { return; }
            var frames = data.Length / channels;
            if (frames == 0) { return; }
            if (_stage1.Length < channels)
            {
                _stage1 = new float[channels];
                _stage2 = new float[channels];
            }

            var open = cutoffHz >= SpatialAudio.OpenCutoffHz && _cutoff >= SpatialAudio.OpenCutoffHz;
            if (open && gain >= UnityGain && _gain >= UnityGain)
            {
                // Bypassed: the stages track the signal, so engaging later does not click.
                var last = (frames - 1) * channels;
                for (var c = 0; c < channels; c++)
                {
                    _stage1[c] = data[last + c];
                    _stage2[c] = data[last + c];
                }

                _gain = 1f;
                _cutoff = cutoffHz;
                return;
            }

            var a = SpatialAudio.LowPassCoefficient(cutoffHz, sampleRate);
            var start = _gain;
            var step = (gain - start) / frames;
            var i = 0;
            for (var f = 0; f < frames; f++)
            {
                var g = start + step * (f + 1);
                for (var c = 0; c < channels; c++, i++)
                {
                    var x = data[i];
                    if (open)
                    {
                        _stage1[c] = x;
                        _stage2[c] = x;
                    }
                    else
                    {
                        _stage1[c] += a * (x - _stage1[c]);
                        _stage2[c] += a * (_stage1[c] - _stage2[c]);
                        x = _stage2[c];
                    }

                    data[i] = x * g;
                }
            }

            _gain = gain;
            _cutoff = cutoffHz;
        }
    }
}
