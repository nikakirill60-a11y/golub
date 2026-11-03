using System;
using BlitzLegacy.Core.Audio;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>The bands, the switch and the limiter of the hangar music equalizer.</summary>
    public sealed class HangarMusicEqualizerTests
    {
        private const int Rate = 48000;

        [Test]
        public void TheBandsSitAnOctaveApartFrom31HzTo16kHz()
        {
            Assert.AreEqual(10, HangarMusicEqualizer.BandCount);
            var expected = new[] { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
            for (var band = 0; band < HangarMusicEqualizer.BandCount; band++)
            {
                Assert.AreEqual(expected[band], HangarMusicEqualizer.Frequency(band));
            }
        }

        [TestCase(44100, 12)]
        [TestCase(48000, 12)]
        [TestCase(48000, -12)]
        [TestCase(48000, 5)]
        public void EachBandPeaksAtItsCentreByItsGainAndLeavesFarFrequenciesAlone(int rate, int gain)
        {
            for (var band = 0; band < HangarMusicEqualizer.BandCount; band++)
            {
                var equalizer = new HangarMusicEqualizer { Enabled = true };
                equalizer.SetGain(band, gain);
                var centre = HangarMusicEqualizer.Frequency(band);
                Assert.AreEqual(gain, equalizer.Response(centre, rate), 0.01d, "the centre of " + centre + " Hz");

                // An octave wide: a quarter of the gain an octave off, next
                // to nothing three octaves off.
                foreach (var octaves in new[] { -3, -1, 1, 3 })
                {
                    var frequency = centre * Math.Pow(2d, octaves);
                    if (frequency < 20d || frequency > 0.4d * rate)
                    {
                        continue;
                    }

                    var response = equalizer.Response(frequency, rate);
                    var limit = Math.Abs(octaves) == 1 ? Math.Abs(gain) * 0.22d : Math.Abs(gain) * 0.012d;
                    Assert.LessOrEqual(Math.Abs(response), limit + 1e-9,
                        centre + " Hz at " + gain + " dB reads " + response + " dB at " + frequency + " Hz");
                    Assert.AreEqual(Math.Sign(gain), Math.Sign(response), "a band bends one way only");
                }
            }
        }

        [Test]
        public void TheResponseOfTheBandsAddsUpAndIsFlatOff()
        {
            var equalizer = new HangarMusicEqualizer { Enabled = true };
            equalizer.SetGain(1, 6);
            equalizer.SetGain(7, -4);
            double b0, b1, b2, a1, a2;
            var sum = 0d;
            Assert.IsTrue(HangarMusicEqualizer.Coefficients(1, 6, Rate, out b0, out b1, out b2, out a1, out a2));
            sum += HangarMusicEqualizer.Magnitude(700d, Rate, b0, b1, b2, a1, a2);
            Assert.IsTrue(HangarMusicEqualizer.Coefficients(7, -4, Rate, out b0, out b1, out b2, out a1, out a2));
            sum += HangarMusicEqualizer.Magnitude(700d, Rate, b0, b1, b2, a1, a2);
            Assert.AreEqual(sum, equalizer.Response(700d, Rate), 1e-9);

            equalizer.Enabled = false;
            Assert.AreEqual(0d, equalizer.Response(62d, Rate));
            Assert.AreEqual(6, equalizer.Gain(1), "off keeps the bands for the next time it goes on");
        }

        [Test]
        public void BandsReadInHertzThenKilohertzAndGainsWithTheirSign()
        {
            var labels = new string[HangarMusicEqualizer.BandCount];
            for (var band = 0; band < labels.Length; band++)
            {
                labels[band] = HangarMusicEqualizer.FrequencyLabel(band, "{0}k");
            }

            CollectionAssert.AreEqual(new[] { "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" }, labels);
            Assert.AreEqual("16\u043a", HangarMusicEqualizer.FrequencyLabel(9, "{0}\u043a"));
            Assert.AreEqual("+12", HangarMusicEqualizer.GainLabel(12));
            Assert.AreEqual("0", HangarMusicEqualizer.GainLabel(0));
            Assert.AreEqual("-5", HangarMusicEqualizer.GainLabel(-5));
        }

        [Test]
        public void GainsAreHeldToTwelveDecibelsEitherWay()
        {
            var equalizer = new HangarMusicEqualizer();
            equalizer.SetGain(0, 40);
            equalizer.SetGain(9, -99);
            Assert.AreEqual(12, equalizer.Gain(0));
            Assert.AreEqual(-12, equalizer.Gain(9));
        }

        [Test]
        public void ABandTooCloseToNyquistIsLeftOut()
        {
            double b0, b1, b2, a1, a2;
            Assert.IsFalse(HangarMusicEqualizer.Coefficients(9, 12, 22050, out b0, out b1, out b2, out a1, out a2));
            Assert.IsTrue(HangarMusicEqualizer.Coefficients(9, 12, 44100, out b0, out b1, out b2, out a1, out a2));
            Assert.IsFalse(HangarMusicEqualizer.Coefficients(4, 0, 44100, out b0, out b1, out b2, out a1, out a2),
                "a band at 0 dB is no filter at all");
        }

        [Test]
        public void OffOrFlatTheMusicPassesUntouched()
        {
            var input = Noise(4096, 2, 0.7f, 7);
            var equalizer = new HangarMusicEqualizer();
            equalizer.SetGain(3, 9);
            var off = (float[])input.Clone();
            equalizer.Process(off, 2, Rate);
            CollectionAssert.AreEqual(input, off, "off");

            var flat = new HangarMusicEqualizer { Enabled = true };
            var on = (float[])input.Clone();
            flat.Process(on, 2, Rate);
            CollectionAssert.AreEqual(input, on, "on with every band at 0 dB");
        }

        [Test]
        public void ARaisedBandRaisesASineAtItsCentreOnly()
        {
            var equalizer = new HangarMusicEqualizer { Enabled = true };
            equalizer.SetGain(5, 6);
            Assert.AreEqual(6d, GainOfSine(equalizer, 1000d, 0.05f), 0.1d, "1 kHz under the 1 kHz band");

            var other = new HangarMusicEqualizer { Enabled = true };
            other.SetGain(5, 6);
            Assert.AreEqual(0d, GainOfSine(other, 62d, 0.05f), 0.1d, "62 Hz under the 1 kHz band");

            var cut = new HangarMusicEqualizer { Enabled = true };
            cut.SetGain(1, -12);
            Assert.AreEqual(-12d, GainOfSine(cut, 62d, 0.5f), 0.15d, "62 Hz under the 62 Hz band cut");
        }

        [Test]
        public void TheLimiterKeepsRaisedMusicUnderTheCeiling()
        {
            var equalizer = new HangarMusicEqualizer { Enabled = true };
            equalizer.SetGain(1, 12);
            var data = Sine(Rate, 2, 62d, 0.9f);
            equalizer.Process(data, 2, Rate);
            var peak = 0f;
            var sum = 0d;
            foreach (var sample in data)
            {
                peak = Math.Max(peak, Math.Abs(sample));
                sum += sample * (double)sample;
            }

            Assert.LessOrEqual(peak, HangarMusicEqualizer.Ceiling + 1e-6f, "+12 dB on a 0.9 sine must not clip");
            Assert.Greater(Math.Sqrt(sum / data.Length), 0.6d, "the limiter holds the level, it does not mute");
        }

        [Test]
        public void TheSwitchTakesEffectOnTheNextBlock()
        {
            var equalizer = new HangarMusicEqualizer();
            equalizer.SetGain(5, 12);
            var block = Sine(4800, 1, 1000d, 0.01f);
            var before = (float[])block.Clone();
            equalizer.Process(block, 1, Rate);
            CollectionAssert.AreEqual(before, block);

            equalizer.Enabled = true;
            Assert.Greater(GainOfSine(equalizer, 1000d, 0.01f), 11.5d);
            equalizer.Enabled = false;
            Assert.AreEqual(0d, GainOfSine(equalizer, 1000d, 0.01f), 1e-6d);
        }

        /// <summary>The gain in dB a settled sine of <paramref name="frequency"/> gets.</summary>
        private static double GainOfSine(HangarMusicEqualizer equalizer, double frequency, float amplitude)
        {
            var data = Sine(Rate, 2, frequency, amplitude);
            var input = (float[])data.Clone();
            equalizer.Process(data, 2, Rate);

            // The second half, after the filters have settled.
            double a = 0d, b = 0d;
            for (var i = data.Length / 2; i < data.Length; i++)
            {
                a += input[i] * (double)input[i];
                b += data[i] * (double)data[i];
            }

            return 10d * Math.Log10(b / a);
        }

        private static float[] Sine(int frames, int channels, double frequency, float amplitude)
        {
            var data = new float[frames * channels];
            for (var frame = 0; frame < frames; frame++)
            {
                var value = (float)(amplitude * Math.Sin(2d * Math.PI * frequency * frame / Rate));
                for (var channel = 0; channel < channels; channel++)
                {
                    data[(frame * channels) + channel] = value;
                }
            }

            return data;
        }

        private static float[] Noise(int frames, int channels, float amplitude, int seed)
        {
            var random = new Random(seed);
            var data = new float[frames * channels];
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (float)(((random.NextDouble() * 2d) - 1d) * amplitude);
            }

            return data;
        }
    }
}
