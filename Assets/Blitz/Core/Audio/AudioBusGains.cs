using System;

namespace BlitzLegacy.Core.Audio
{
    public enum AudioBus
    {
        Master,
        Music,
        Ui,
        Sfx,
        Radio,
        Sniper,
    }

    /// <summary>
    /// Bus gains taken from Godot's default_bus_layout.tres.
    ///
    /// The values remain available for parity checks, but Godot's Audio
    /// autoload immediately replaces every non-master bus level through
    /// AudioServer.set_bus_volume_linear(). Runtime playback must therefore
    /// apply the saved linear bus value instead of multiplying it by the stale
    /// default dB value. The one real effect in the layout is a disabled SFX
    /// low-pass filter; it lands with the battle-muffle slice.
    /// </summary>
    public static class AudioBusGains
    {
        public const float MasterDb = -0.3815832f;
        public const float MusicDb = -1.58813f;
        public const float UiDb = -8.4173f;
        public const float SfxDb = -11.214649f;
        public const float RadioDb = -16.235643f;
        public const float SniperDb = -57.487488f;

        public static float DecibelsFor(AudioBus bus)
        {
            switch (bus)
            {
                case AudioBus.Master:
                    return MasterDb;
                case AudioBus.Music:
                    return MusicDb;
                case AudioBus.Ui:
                    return UiDb;
                case AudioBus.Sfx:
                    return SfxDb;
                case AudioBus.Radio:
                    return RadioDb;
                case AudioBus.Sniper:
                    return SniperDb;
                default:
                    return 0f;
            }
        }

        public static float ToLinear(float decibels)
        {
            return (float)Math.Pow(10d, decibels / 20d);
        }

        public static float LinearFor(AudioBus bus)
        {
            return ToLinear(DecibelsFor(bus));
        }

        /// <summary>
        /// Final AudioSource volume: Godot's retained master-bus gain, the
        /// user's master value, the runtime linear value that replaces the
        /// selected child bus gain, and the per-call multiplier. UI buttons set
        /// <paramref name="extra"/> to 0.7.
        /// </summary>
        public static float Volume(
            AudioBus bus,
            float busSliderVolume,
            float masterSliderVolume,
            float extra)
        {
            var childBusVolume = bus == AudioBus.Master ? 1f : busSliderVolume;
            return LinearFor(AudioBus.Master) * masterSliderVolume *
                   childBusVolume * extra;
        }
    }
}
