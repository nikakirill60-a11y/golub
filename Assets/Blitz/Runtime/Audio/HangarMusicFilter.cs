using BlitzLegacy.Core.Audio;
using UnityEngine;

namespace BlitzLegacy.Runtime.Audio
{
    /// <summary>
    /// Runs the voice of the hangar music player through its equalizer. It
    /// sits after the AudioSource on the player's object, so Unity hands it
    /// every block the voice plays on the audio thread.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AudioSource))]
    public sealed class HangarMusicFilter : MonoBehaviour
    {
        private volatile HangarMusicEqualizer _equalizer;
        private volatile int _sampleRate;

        /// <summary>The equalizer the voice goes through; null passes it untouched.</summary>
        public HangarMusicEqualizer Equalizer
        {
            get { return _equalizer; }
            set { _equalizer = value; }
        }

        private void Awake()
        {
            _sampleRate = AudioSettings.outputSampleRate;
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
        }

        private void OnDestroy()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
        }

        private void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            // A new output device may run at another rate.
            _sampleRate = AudioSettings.outputSampleRate;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            var equalizer = _equalizer;
            if (equalizer != null)
            {
                equalizer.Process(data, channels, _sampleRate);
            }
        }
    }
}
