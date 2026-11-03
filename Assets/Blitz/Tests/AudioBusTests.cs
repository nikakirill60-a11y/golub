using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Save;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class AudioBusTests
    {
        [Test]
        public void ToLinear_ZeroDecibelsIsUnityGain()
        {
            Assert.AreEqual(1f, AudioBusGains.ToLinear(0f), 1e-6f);
        }

        [Test]
        public void ToLinear_MinusSixDecibelsIsHalfAmplitude()
        {
            Assert.AreEqual(0.5f, AudioBusGains.ToLinear(-6.0206f), 1e-4f);
        }

        [Test]
        public void Buses_MatchGodotBusLayout()
        {
            Assert.AreEqual(0.9570f, AudioBusGains.LinearFor(AudioBus.Master), 1e-3f);
            Assert.AreEqual(0.8329f, AudioBusGains.LinearFor(AudioBus.Music), 1e-3f);
            Assert.AreEqual(0.3794f, AudioBusGains.LinearFor(AudioBus.Ui), 1e-3f);
            Assert.AreEqual(0.2745f, AudioBusGains.LinearFor(AudioBus.Sfx), 1e-3f);
            Assert.AreEqual(0.1541f, AudioBusGains.LinearFor(AudioBus.Radio), 1e-3f);
            Assert.AreEqual(0.001336f, AudioBusGains.LinearFor(AudioBus.Sniper), 1e-5f);
        }

        [Test]
        public void Volume_ReproducesRuntimeButtonClickChain()
        {
            // apply_sound_settings() replaces the UI bus dB value with 0.35
            // linear, then ButtonCustom passes 0.7 * snd_vol to play_sound2d().
            var volume = AudioBusGains.Volume(
                AudioBus.Ui, SettingsData.DefaultVolumeUi, 1f, 0.7f);
            Assert.AreEqual(0.23446f, volume, 1e-4f);
        }

        [Test]
        public void Volume_MasterSliderScalesEverything()
        {
            var full = AudioBusGains.Volume(AudioBus.Music, 1f, 1f, 1f);
            var half = AudioBusGains.Volume(AudioBus.Music, 1f, 0.5f, 1f);
            Assert.AreEqual(full * 0.5f, half, 1e-6f);
        }
    }
}
