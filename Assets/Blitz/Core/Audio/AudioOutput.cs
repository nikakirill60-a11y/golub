namespace BlitzLegacy.Core.Audio
{
    /// <summary>
    /// Desired output configuration shared by editor setup and the runtime.
    /// Unity may choose the closest DSP buffer an Android device supports, but
    /// the native 48 kHz mix rate must never be intentionally downsampled.
    /// </summary>
    public static class AudioOutput
    {
        public const int SampleRate = 48000;
        public const int DspBufferSize = 512;
    }
}
