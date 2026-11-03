using System;
using System.Globalization;
using System.Threading;

namespace BlitzLegacy.Core.Audio
{
    /// <summary>
    /// The ten-band equalizer of the hangar music player, after the Panzer War
    /// Miku player: a peaking biquad an octave wide (Q 1.41) at each of 31 Hz
    /// to 16 kHz, from -12 to +12 dB, and a peak limiter with a 0 dB ceiling
    /// behind them, so raised bands cannot clip the music.
    ///
    /// The main thread moves the bands and the switch; the audio thread runs
    /// <see cref="Process"/> and works the coefficients out itself whenever
    /// they, the sample rate or the channel count changed since its last
    /// block. Off, the equalizer leaves the samples untouched; on, with every
    /// band at 0 dB, it does too, as long as the music stays under the ceiling.
    /// </summary>
    public sealed class HangarMusicEqualizer
    {
        public const int BandCount = 10;
        public const int MinGain = -12;
        public const int MaxGain = 12;

        /// <summary>One octave between the -3 dB points of a band.</summary>
        public const double Q = 1.41;

        /// <summary>The loudest sample the limiter lets through: 0 dBFS.</summary>
        public const float Ceiling = 1f;

        /// <summary>How long the limiter holds its gain after a peak: longer than a 31 Hz wave.</summary>
        public const double HoldSeconds = 0.05;

        /// <summary>How fast the limiter lets go once the hold is over.</summary>
        public const double ReleaseSeconds = 0.1;

        /// <summary>A band this close to the Nyquist frequency or over it is left out.</summary>
        private const double NyquistShare = 0.45;

        /// <summary>Filter memory under this is flushed to zero, so silence never runs into denormals.</summary>
        private const double Denormal = 1e-25;

        private static readonly int[] Centres = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

        // Written by the main thread.
        private readonly int[] _gains = new int[BandCount];
        private volatile bool _enabled;
        private int _version;

        // Owned by the audio thread.
        private int _builtVersion = -1;
        private int _builtRate;
        private int _builtChannels;
        private bool _builtEnabled;
        private int _active;
        private readonly double[] _b0 = new double[BandCount];
        private readonly double[] _b1 = new double[BandCount];
        private readonly double[] _b2 = new double[BandCount];
        private readonly double[] _a1 = new double[BandCount];
        private readonly double[] _a2 = new double[BandCount];
        private double[] _z1 = new double[0];
        private double[] _z2 = new double[0];
        private double _envelope;
        private int _holding;
        private int _hold;
        private double _release;

        /// <summary>The centre frequency of <paramref name="band"/>, in hertz.</summary>
        public static int Frequency(int band)
        {
            return Centres[band];
        }

        /// <summary>
        /// The caption of <paramref name="band"/>: its hertz under 1 kHz, its
        /// kilohertz in <paramref name="kilohertz"/>'s {0} above, as "1k".
        /// </summary>
        public static string FrequencyLabel(int band, string kilohertz)
        {
            var hertz = Centres[band];
            return hertz < 1000
                ? hertz.ToString(CultureInfo.InvariantCulture)
                : string.Format(CultureInfo.InvariantCulture, kilohertz, hertz / 1000);
        }

        /// <summary>A gain as the band's value shows it: "+3", "0", "-5".</summary>
        public static string GainLabel(int decibels)
        {
            var number = decibels.ToString(CultureInfo.InvariantCulture);
            return decibels > 0 ? "+" + number : number;
        }

        /// <summary>Whether the equalizer shapes the music; off, it passes it through untouched.</summary>
        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value)
                {
                    return;
                }

                _enabled = value;
                Interlocked.Increment(ref _version);
            }
        }

        /// <summary>The gain of <paramref name="band"/>, in whole decibels.</summary>
        public int Gain(int band)
        {
            return Volatile.Read(ref _gains[band]);
        }

        /// <summary>Sets the gain of <paramref name="band"/>, held to -12..+12 dB.</summary>
        public void SetGain(int band, int decibels)
        {
            var gain = ClampGain(decibels);
            if (Volatile.Read(ref _gains[band]) == gain)
            {
                return;
            }

            Volatile.Write(ref _gains[band], gain);
            Interlocked.Increment(ref _version);
        }

        public static int ClampGain(int decibels)
        {
            return Math.Max(MinGain, Math.Min(MaxGain, decibels));
        }

        /// <summary>
        /// The equalizer's response at <paramref name="frequency"/>, in dB,
        /// for the gains set now at <paramref name="sampleRate"/>: the sum of
        /// the bands' biquads, the limiter left out. Off, it is flat.
        /// </summary>
        public double Response(double frequency, int sampleRate)
        {
            if (!_enabled)
            {
                return 0d;
            }

            var total = 0d;
            for (var band = 0; band < BandCount; band++)
            {
                double b0, b1, b2, a1, a2;
                if (Coefficients(band, Gain(band), sampleRate, out b0, out b1, out b2, out a1, out a2))
                {
                    total += Magnitude(frequency, sampleRate, b0, b1, b2, a1, a2);
                }
            }

            return total;
        }

        /// <summary>
        /// Runs the interleaved block <paramref name="data"/> of
        /// <paramref name="channels"/> channels through the bands and the
        /// limiter in place. Only the audio thread calls it.
        /// </summary>
        public void Process(float[] data, int channels, int sampleRate)
        {
            if (data == null || channels <= 0 || sampleRate <= 0)
            {
                return;
            }

            var version = Volatile.Read(ref _version);
            if (version != _builtVersion || sampleRate != _builtRate || channels != _builtChannels)
            {
                Build(version, sampleRate, channels);
            }

            if (!_builtEnabled)
            {
                return;
            }

            var frames = data.Length / channels;
            var envelope = _envelope;
            var holding = _holding;
            for (var frame = 0; frame < frames; frame++)
            {
                var offset = frame * channels;
                var peak = 0d;
                for (var channel = 0; channel < channels; channel++)
                {
                    double x = data[offset + channel];
                    var state = channel * BandCount;
                    for (var band = 0; band < BandCount; band++)
                    {
                        if ((_active & (1 << band)) == 0)
                        {
                            continue;
                        }

                        // Transposed direct form II.
                        var z = state + band;
                        var y = (_b0[band] * x) + _z1[z];
                        _z1[z] = (_b1[band] * x) - (_a1[band] * y) + _z2[z];
                        _z2[z] = (_b2[band] * x) - (_a2[band] * y);
                        x = y;
                    }

                    data[offset + channel] = (float)x;
                    peak = Math.Max(peak, Math.Abs(x));
                }

                // The channels share one gain, so the stereo image holds. The
                // envelope jumps to a peak at once, holds it for a wave of the
                // lowest band, so the gain does not ripple with it, and then
                // lets go over the release.
                if (peak >= envelope)
                {
                    envelope = peak;
                    holding = _hold;
                }
                else if (holding > 0)
                {
                    holding--;
                }
                else
                {
                    envelope = peak + ((envelope - peak) * _release);
                }

                if (envelope > Ceiling)
                {
                    var gain = Ceiling / envelope;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        data[offset + channel] = (float)(data[offset + channel] * gain);
                    }
                }
            }

            _envelope = envelope < Denormal ? 0d : envelope;
            _holding = holding;
            for (var i = 0; i < _z1.Length; i++)
            {
                if (Math.Abs(_z1[i]) < Denormal) { _z1[i] = 0d; }
                if (Math.Abs(_z2[i]) < Denormal) { _z2[i] = 0d; }
            }
        }

        private void Build(int version, int sampleRate, int channels)
        {
            var enabled = _enabled;
            if (channels != _builtChannels || (enabled && !_builtEnabled) || sampleRate != _builtRate)
            {
                // Memory from another layout or from before the switch went
                // on would only click; the bands start from silence.
                if (_z1.Length != channels * BandCount)
                {
                    _z1 = new double[channels * BandCount];
                    _z2 = new double[channels * BandCount];
                }
                else
                {
                    Array.Clear(_z1, 0, _z1.Length);
                    Array.Clear(_z2, 0, _z2.Length);
                }

                _envelope = 0d;
                _holding = 0;
            }

            _active = 0;
            for (var band = 0; band < BandCount; band++)
            {
                if (Coefficients(band, Gain(band), sampleRate,
                        out _b0[band], out _b1[band], out _b2[band], out _a1[band], out _a2[band]))
                {
                    _active |= 1 << band;
                }
            }

            _hold = (int)Math.Ceiling(HoldSeconds * sampleRate);
            _release = Math.Exp(-1d / (ReleaseSeconds * sampleRate));
            _builtEnabled = enabled;
            _builtVersion = version;
            _builtRate = sampleRate;
            _builtChannels = channels;
        }

        /// <summary>
        /// The peaking biquad of the Audio EQ Cookbook (R. Bristow-Johnson)
        /// for <paramref name="band"/> at <paramref name="gain"/> dB,
        /// normalised by a0. False for a band that changes nothing: 0 dB, or
        /// too close to the Nyquist frequency of <paramref name="sampleRate"/>.
        /// </summary>
        public static bool Coefficients(
            int band, int gain, int sampleRate,
            out double b0, out double b1, out double b2, out double a1, out double a2)
        {
            b0 = 1d;
            b1 = b2 = a1 = a2 = 0d;
            var frequency = (double)Centres[band];
            if (gain == 0 || sampleRate <= 0 || frequency >= NyquistShare * sampleRate)
            {
                return false;
            }

            var a = Math.Pow(10d, gain / 40d);
            var w0 = 2d * Math.PI * frequency / sampleRate;
            var cos = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2d * Q);
            var a0 = 1d + (alpha / a);
            b0 = (1d + (alpha * a)) / a0;
            b1 = (-2d * cos) / a0;
            b2 = (1d - (alpha * a)) / a0;
            a1 = (-2d * cos) / a0;
            a2 = (1d - (alpha / a)) / a0;
            return true;
        }

        /// <summary>The gain in dB of a normalised biquad at <paramref name="frequency"/>.</summary>
        public static double Magnitude(
            double frequency, int sampleRate, double b0, double b1, double b2, double a1, double a2)
        {
            var w = 2d * Math.PI * frequency / sampleRate;
            var cos1 = Math.Cos(w);
            var sin1 = Math.Sin(w);
            var cos2 = Math.Cos(2d * w);
            var sin2 = Math.Sin(2d * w);
            var numeratorRe = b0 + (b1 * cos1) + (b2 * cos2);
            var numeratorIm = -((b1 * sin1) + (b2 * sin2));
            var denominatorRe = 1d + (a1 * cos1) + (a2 * cos2);
            var denominatorIm = -((a1 * sin1) + (a2 * sin2));
            var numerator = (numeratorRe * numeratorRe) + (numeratorIm * numeratorIm);
            var denominator = (denominatorRe * denominatorRe) + (denominatorIm * denominatorIm);
            return 10d * Math.Log10(numerator / denominator);
        }
    }
}
