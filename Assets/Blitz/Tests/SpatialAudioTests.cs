using System;
using BlitzLegacy.Core.Audio;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class SpatialAudioTests
    {
        private const int Rate = 48000;

        private static readonly SpatialRange[] Ranges =
        {
            SpatialAudio.TankBeds, SpatialAudio.GunNear, SpatialAudio.GunFar,
            SpatialAudio.ExplosionNear, SpatialAudio.ExplosionFar, SpatialAudio.Collision,
            SpatialAudio.OneShot, SpatialAudio.AtgmFlight, SpatialAudio.UprisingCue,
            SpatialAudio.Cue(400f),
        };

        [Test]
        public void DistanceLawKeepsTheInverseCurveAndEasesOutAtTheCut()
        {
            var beds = SpatialAudio.TankBeds;
            Assert.AreEqual(1f, SpatialAudio.DistanceGain(0f, beds));
            Assert.AreEqual(1f, SpatialAudio.DistanceGain(beds.Reference, beds));
            Assert.AreEqual(0.5f, SpatialAudio.DistanceGain(50f, beds), 1e-5f);
            var fadeStart = beds.Max * (1f - SpatialAudio.FadeShare);
            Assert.AreEqual(beds.Reference / fadeStart, SpatialAudio.DistanceGain(fadeStart, beds), 1e-5f);
            Assert.Less(SpatialAudio.DistanceGain(100f, beds), beds.Reference / 100f, "the last stretch fades");
            Assert.Less(SpatialAudio.DistanceGain(beds.Max - 0.5f, beds), 0.001f, "no step at the cut");
            Assert.AreEqual(0f, SpatialAudio.DistanceGain(beds.Max, beds));
            Assert.AreEqual(0f, SpatialAudio.DistanceGain(5000f, beds));

            foreach (var range in Ranges)
            {
                var previous = 1f;
                for (var i = 0; i <= 400; i++)
                {
                    var distance = range.Max * 1.1f * i / 400f;
                    var gain = SpatialAudio.DistanceGain(distance, range);
                    Assert.LessOrEqual(gain, previous + 1e-6f, range + " at " + distance);
                    previous = gain;
                }

                Assert.Less(SpatialAudio.DistanceGain(range.Max * 0.995f, range), 0.002f, range + " steps at the cut");
            }
        }

        [Test]
        public void FarAbilityCuesAreQuietInsteadOfLinearToTheirReach()
        {
            // The old linear 5..400 m rolloff left a cue 200 m away at half level.
            var cue = SpatialAudio.Cue(400f);
            Assert.AreEqual(10f, cue.Reference);
            Assert.AreEqual(400f, cue.Max);
            Assert.AreEqual(0.05f, SpatialAudio.DistanceGain(200f, cue), 1e-5f);
            Assert.Less(SpatialAudio.DistanceGain(380f, cue), 0.01f);
            Assert.AreEqual(1f, SpatialAudio.DistanceGain(8f, cue));

            Assert.AreEqual(0f, SpatialAudio.DistanceGain(SpatialAudio.AtgmFlight.Max, SpatialAudio.AtgmFlight));
            Assert.Less(SpatialAudio.DistanceGain(120f, SpatialAudio.AtgmFlight), 0.08f,
                "a rocket 120 m off barely hums");
        }

        [Test]
        public void NearAndDistantReportsMatchAtTheSwitchAndOnlyTheDistantOneCarries()
        {
            var gunSwitch = TankSoundTuning.GunSoundNearDistance;
            Assert.AreEqual(
                SpatialAudio.DistanceGain(gunSwitch - 0.1f, SpatialAudio.GunNear),
                SpatialAudio.DistanceGain(gunSwitch + 0.1f, SpatialAudio.GunFar), 0.002f);
            var blastSwitch = TankSoundTuning.ExplosionSoundNearDistance;
            Assert.AreEqual(
                SpatialAudio.DistanceGain(blastSwitch - 0.1f, SpatialAudio.ExplosionNear),
                SpatialAudio.DistanceGain(blastSwitch + 0.1f, SpatialAudio.ExplosionFar), 0.002f);

            Assert.Greater(SpatialAudio.GunNear.Max, gunSwitch);
            Assert.Greater(SpatialAudio.ExplosionNear.Max, blastSwitch, "no silent band before the switch");
            Assert.Greater(SpatialAudio.DistanceGain(800f, SpatialAudio.GunFar), 0f);
            Assert.Greater(SpatialAudio.DistanceGain(800f, SpatialAudio.ExplosionFar), 0f);
        }

        [Test]
        public void RangesKeepRoomForTheirFade()
        {
            var tiny = new SpatialRange(0f, 0f);
            Assert.AreEqual(SpatialRange.MinReference, tiny.Reference);
            Assert.AreEqual(SpatialRange.MinReference * SpatialRange.MinSpan, tiny.Max, 1e-6f);
            Assert.AreEqual(100f, new SpatialRange(50f, 60f).Max);
            Assert.AreEqual(new SpatialRange(10f, 400f), SpatialAudio.Cue(400f));
            Assert.AreNotEqual(SpatialAudio.GunNear, SpatialAudio.GunFar);
            Assert.False(SpatialAudio.Cue(0f).IsEmpty);
            Assert.True(default(SpatialRange).IsEmpty);
            Assert.AreEqual(1f, SpatialAudio.DistanceGain(1000f, default(SpatialRange)));
            Assert.AreEqual(SpatialAudio.OpenCutoffHz, SpatialAudio.AirCutoff(1000f, default(SpatialRange)));
        }

        [Test]
        public void AirAbsorptionDarkensDistantVoicesInOctaves()
        {
            var range = SpatialAudio.GunFar;
            Assert.AreEqual(SpatialAudio.OpenCutoffHz, SpatialAudio.AirCutoff(range.Reference, range));
            Assert.AreEqual(SpatialAudio.AirCutoffHz, SpatialAudio.AirCutoff(range.Max, range), 1f);
            Assert.AreEqual(SpatialAudio.AirCutoffHz, SpatialAudio.AirCutoff(range.Max * 3f, range), 1f);
            var middle = SpatialAudio.AirCutoff((range.Reference + range.Max) / 2f, range);
            Assert.AreEqual(Math.Sqrt(SpatialAudio.OpenCutoffHz * SpatialAudio.AirCutoffHz), middle, 1.0);
        }

        [Test]
        public void OcclusionMufflesByObstacleThickness()
        {
            Assert.AreEqual(0f, SpatialAudio.Occlusion(false, 30f));
            Assert.AreEqual(SpatialAudio.MinOcclusion, SpatialAudio.Occlusion(true, 0f));
            Assert.AreEqual(0.7f, SpatialAudio.Occlusion(true, SpatialAudio.FullOcclusionThickness / 2f), 1e-5f);
            Assert.AreEqual(1f, SpatialAudio.Occlusion(true, 40f), 1e-6f);

            Assert.AreEqual(1f, SpatialAudio.OcclusionGain(0f));
            Assert.AreEqual(SpatialAudio.OccludedGain, SpatialAudio.OcclusionGain(1f), 1e-6f);
            Assert.AreEqual(SpatialAudio.OccludedGain, SpatialAudio.OcclusionGain(3f), 1e-6f);
            Assert.AreEqual(SpatialAudio.OpenCutoffHz, SpatialAudio.OcclusionCutoff(0f), 0.5f);
            Assert.AreEqual(SpatialAudio.OccludedCutoffHz, SpatialAudio.OcclusionCutoff(1f), 0.5f);
            Assert.Less(SpatialAudio.OcclusionCutoff(SpatialAudio.MinOcclusion), SpatialAudio.AirCutoffHz,
                "even a thin wall muffles more than the whole range of air");
        }

        [Test]
        public void OcclusionGlidesInsteadOfSwitching()
        {
            Assert.AreEqual(1f, SpatialAudio.Smooth(1f, 0f, 0f));
            Assert.AreEqual(0f, SpatialAudio.Smooth(1f, 0f, 0.1f, 0f));
            Assert.AreEqual((float)Math.Exp(-1.0),
                SpatialAudio.Smooth(1f, 0f, SpatialAudio.SmoothingSeconds), 1e-5f);

            var value = 0f;
            for (var i = 0; i < 3; i++) { value = SpatialAudio.Smooth(value, 1f, 1f / 60f); }
            Assert.Less(value, 0.5f, "three frames in, still on the way");
            for (var i = 0; i < 57; i++) { value = SpatialAudio.Smooth(value, 1f, 1f / 60f); }
            Assert.Greater(value, 0.99f, "settled within a second");

            Assert.AreEqual(1.0 - Math.Exp(-2.0 * Math.PI * 1000.0 / Rate),
                SpatialAudio.LowPassCoefficient(1000f, Rate), 1e-6);
            Assert.AreEqual(SpatialAudio.LowPassCoefficient(1000f, AudioOutput.SampleRate),
                SpatialAudio.LowPassCoefficient(1000f, 0));
        }

        [Test]
        public void FilterPassesAClearVoiceUntouched()
        {
            var filter = new SpatialFilter();
            var tone = Tone(10000f, 1024, 2);
            var heard = (float[])tone.Clone();
            filter.Process(heard, 2, 1f, SpatialAudio.OpenCutoffHz, Rate);
            CollectionAssert.AreEqual(tone, heard);
            Assert.AreEqual(1f, filter.Gain);
        }

        [Test]
        public void FilterRampsGainAcrossTheBufferWithoutAClick()
        {
            var filter = new SpatialFilter();
            var level = Constant(1f, 512, 2);
            filter.Process(level, 2, 0.5f, SpatialAudio.OpenCutoffHz, Rate);
            Assert.AreEqual(1f, level[0], 0.002f);
            Assert.AreEqual(0.5f, level[level.Length - 1], 1e-5f);
            for (var i = 2; i < level.Length; i++)
            {
                Assert.LessOrEqual(level[i], level[i - 2] + 1e-6f, "ramps down at " + i);
                Assert.LessOrEqual(level[i - 2] - level[i], 0.5f / 512f + 1e-5f, "no step at " + i);
            }

            Assert.AreEqual(level[0], level[1], "channels ramp together");
            Assert.AreEqual(0.5f, filter.Gain);
            var held = Constant(1f, 512, 2);
            filter.Process(held, 2, 0.5f, SpatialAudio.OpenCutoffHz, Rate);
            foreach (var sample in held) { Assert.AreEqual(0.5f, sample, 1e-5f); }
        }

        [Test]
        public void OccludedFilterCutsTheTopAndKeepsTheBody()
        {
            var cutoff = SpatialAudio.OcclusionCutoff(1f);
            Assert.Less(Heard(10000f, cutoff), 0.05f, "10 kHz behind a wall");
            Assert.Greater(Heard(100f, cutoff), 0.9f, "100 Hz behind a wall");
            Assert.Greater(Heard(3000f, SpatialAudio.AirCutoff(SpatialAudio.GunFar.Max, SpatialAudio.GunFar)), 0.75f,
                "air absorption keeps the mids");
        }

        [Test]
        public void FilterStaysStableWhileTheCornerJumps()
        {
            var filter = new SpatialFilter();
            var random = new Random(7);
            var corners = new[] { 600f, SpatialAudio.OpenCutoffHz, 6000f, 600f, 150f, SpatialAudio.OpenCutoffHz };
            for (var buffer = 0; buffer < 60; buffer++)
            {
                var data = new float[256 * 2];
                for (var i = 0; i < data.Length; i++) { data[i] = (float)(random.NextDouble() * 2.0 - 1.0); }
                filter.Process(data, 2, buffer % 3 == 0 ? 1f : 0.2f, corners[buffer % corners.Length], Rate);
                foreach (var sample in data)
                {
                    Assert.False(float.IsNaN(sample) || float.IsInfinity(sample));
                    Assert.LessOrEqual(Math.Abs(sample), 1.0001f);
                }
            }

            var mono = new float[64];
            filter.Process(mono, 1, 1f, SpatialAudio.OpenCutoffHz, Rate);
            filter.Process(new float[0], 2, 1f, 600f, Rate);
            filter.Process(null, 2, 1f, 600f, Rate);
            var wide = Constant(0.25f, 32, 6);
            filter.Process(wide, 6, 1f, 600f, Rate);
            foreach (var sample in wide) { Assert.False(float.IsNaN(sample)); }
        }

        /// <summary>The share of a steady tone's RMS that survives the filter at <paramref name="cutoffHz"/>.</summary>
        private static float Heard(float frequency, float cutoffHz)
        {
            var filter = new SpatialFilter();
            var tone = Tone(frequency, 4096, 2);
            var settle = (float[])tone.Clone();
            filter.Process(settle, 2, 1f, cutoffHz, Rate);
            var heard = (float[])tone.Clone();
            filter.Process(heard, 2, 1f, cutoffHz, Rate);
            return Rms(heard) / Rms(tone);
        }

        private static float[] Tone(float frequency, int frames, int channels)
        {
            var data = new float[frames * channels];
            for (var f = 0; f < frames; f++)
            {
                var value = (float)Math.Sin(2.0 * Math.PI * frequency * f / Rate);
                for (var c = 0; c < channels; c++) { data[f * channels + c] = value; }
            }

            return data;
        }

        private static float[] Constant(float value, int frames, int channels)
        {
            var data = new float[frames * channels];
            for (var i = 0; i < data.Length; i++) { data[i] = value; }
            return data;
        }

        private static float Rms(float[] data)
        {
            var sum = 0.0;
            foreach (var sample in data) { sum += sample * sample; }
            return (float)Math.Sqrt(sum / data.Length);
        }
    }
}
