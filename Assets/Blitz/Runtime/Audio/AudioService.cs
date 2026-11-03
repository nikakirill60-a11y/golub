using System.Collections;
using System.Collections.Generic;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;

namespace BlitzLegacy.Runtime.Audio
{
    /// <summary>
    /// Port of Godot's Audio autoload (_audio.gd).
    ///
    /// Unity's AudioMixer cannot be created from a script, so the Godot bus
    /// layout is reproduced as gain stages on plain AudioSources. Every voice
    /// remembers its bus so the volume sliders can be re-applied live.
    ///
    /// Besides 2D playback this owns the battle sound table exported out of
    /// the Godot .tres resources, the crew voice queue and the sniper-mode
    /// muffle, all of which live on the same autoload in the source.
    /// </summary>
    public sealed class AudioService : MonoBehaviour
    {
        public const string SoundEnter = "enter_01";
        public const string SoundSelect = "select_01";
        public const string SoundBattle = "start_battle";
        public const string SoundFilterOpen = "filter_open";
        public const string SoundFilterClose = "filter_close";

        /// <summary>Audio.ui_drag, the tick every slider notch makes.</summary>
        public const string SoundDrag = "drag_01";

        /// <summary>Audio.ui_install (assets/sfx/ui/module_install_01.wav).</summary>
        public const string SoundInstall = "module_install_01";

        /// <summary>Audio.ui_sell (assets/sfx/ui/sell_01.wav).</summary>
        public const string SoundSell = "sell_01";

        /// <summary>ui_shell_slider.check_value_changed() plays it at 0.5.</summary>
        public const float DragVolume = 0.5f;

        public const string MusicHarmony = "harmony";
        public const string MusicAmbient = "ambient_wo_explosions";
        public const string MusicDrums = "drums";

        /// <summary>ButtonCustom._pressed() plays its click at 0.7 * snd_vol.</summary>
        public const float ButtonClickVolume = 0.7f;

        private sealed class Voice
        {
            public AudioSource Source;
            public AudioBus Bus;
            public float Requested;
        }

        private readonly List<Voice> _voices = new List<Voice>();
        private readonly CrewMessageQueue _crew = new CrewMessageQueue();
        private SettingsData _settings = new SettingsData();
        private AudioListenerRig _listener;
        private TankSoundTable _sounds;
        private bool _muffled;
        private float _muffle;

        /// <summary>
        /// The battle sound table (engine, track, gun, shell, collision and
        /// crew pools) exported by Tools/port_godot_sounds.py.
        /// </summary>
        public TankSoundTable Sounds
        {
            get
            {
                if (_sounds == null)
                {
                    _sounds = TankSoundTable.Parse(
                        BlitzResources.LoadText(TankSoundTable.Resource));
                }

                return _sounds;
            }
        }

        /// <summary>muffle_sfx(): true while the player is in sniper mode.</summary>
        public bool Muffled { get { return _muffled; } }

        /// <summary>The 0..1 progress of the 0.3 s muffle tween.</summary>
        public float MuffleAmount { get { return _muffle; } }

        public int CrewMessagesScheduled { get { return _crew.Count; } }
        /// <summary>Spatial rigs are not in the transient 2D voice list; notify them of bus/slider changes.</summary>
        public event System.Action SpatialGainsChanged;

        /// <summary>
        /// StreamRandom.pick_random()'s index draw, handed to the shared pool
        /// so the Core model stays free of UnityEngine.Random.
        /// </summary>
        private static int RandomBelow(int count)
        {
            return Random.Range(0, count);
        }

        public static AudioService Create(Transform parent, SettingsData settings)
        {
            ConfigureOutput();

            var go = new GameObject("Audio");
            go.transform.SetParent(parent, false);

            // The bootstrap scene intentionally has no Camera. Unity still
            // requires one enabled AudioListener even for fully 2D sources, so
            // the persistent audio root owns it, on a child a battle carries
            // to its view.
            var rig = AudioListenerRig.Create(go.transform);

            var service = go.AddComponent<AudioService>();
            service._settings = settings ?? new SettingsData();
            service._listener = rig;
            return service;
        }

        /// <summary>The listener every spatial voice is heard from.</summary>
        public AudioListenerRig Listener { get { return _listener; } }

        /// <summary>Where the listener stands this frame.</summary>
        public Vector3 ListenerPosition
        {
            get { return _listener != null ? _listener.transform.position : transform.position; }
        }

        /// <summary>
        /// Hear the world from <paramref name="source"/>'s pose from now on:
        /// a battle's view, as Godot listens from the current camera.
        /// </summary>
        public void ListenFrom(AudioListenerRig.PoseSource source)
        {
            if (_listener == null) { return; }
            _listener.Source = source;
            _listener.Follow();
        }

        /// <summary>Drops <paramref name="source"/> if it still leads; the listener returns to the root.</summary>
        public void StopListeningFrom(AudioListenerRig.PoseSource source)
        {
            if (_listener == null || source == null || _listener.Source != source) { return; }
            _listener.Source = null;
            _listener.Follow();
        }

        private static void ConfigureOutput()
        {
            var requested = AudioSettings.GetConfiguration();
            var needsReset =
                requested.sampleRate != AudioOutput.SampleRate ||
                requested.dspBufferSize != AudioOutput.DspBufferSize;

            var accepted = true;
            if (needsReset)
            {
                requested.sampleRate = AudioOutput.SampleRate;
                requested.dspBufferSize = AudioOutput.DspBufferSize;
                accepted = AudioSettings.Reset(requested);
            }

            var active = AudioSettings.GetConfiguration();
            Debug.Log(
                "[Blitz] audio device: request=" + AudioOutput.SampleRate + "Hz/" +
                AudioOutput.DspBufferSize + " samples active=" + active.sampleRate +
                "Hz/" + active.dspBufferSize + " samples accepted=" + accepted);
        }

        /// <summary>
        /// Audio.safe_spatial_filter_cutoff(): the attenuation low-pass never
        /// lands above what the active mix rate can carry.
        /// </summary>
        public static float SpatialFilterCutoff()
        {
            var rate = AudioSettings.GetConfiguration().sampleRate;
            if (rate <= 0) { rate = AudioOutput.SampleRate; }
            return TankSoundTuning.SpatialFilterCutoff(rate);
        }

        public SettingsData Settings
        {
            get { return _settings; }
            set
            {
                _settings = value ?? new SettingsData();
                ApplySettings();
            }
        }

        /// <summary>Port of play_sound2d(stream, bus, loop, vol).</summary>
        public AudioSource PlaySound2D(string clipName, AudioBus bus, bool loop, float volume)
        {
            var clip = BlitzResources.LoadAudio(clipName);
            if (clip == null)
            {
                Debug.LogWarning("[Blitz] missing audio clip: " + clipName);
                return null;
            }

            return PlaySound2D(clip, bus, loop, volume);
        }

        public AudioSource PlaySound2D(AudioClip clip, AudioBus bus, bool loop, float volume)
        {
            if (clip == null)
            {
                return null;
            }

            var go = new GameObject(clip.name);
            go.transform.SetParent(transform, false);

            var source = go.AddComponent<AudioSource>();
            // Only the hangar ambience sends to a reverb zone; see HalloweenHangarAudio.
            source.bypassReverbZones = true;
            source.clip = clip;
            source.loop = loop;
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;

            var voice = new Voice { Source = source, Bus = bus, Requested = volume };
            _voices.Add(voice);
            source.volume = VolumeOf(voice);
            source.Play();

            if (!loop)
            {
                Destroy(go, clip.length + 0.25f);
            }

            return source;
        }

        /// <summary>
        /// play_sound2d for a StreamRandom pool: one of the interchangeable
        /// samples at the pool's own gain, on the UI bus like the source.
        /// </summary>
        public AudioSource PlayPool2D(
            SoundPool pool, AudioBus bus = AudioBus.Ui, float volume = 1f)
        {
            if (pool == null || !pool.HasStreams)
            {
                return null;
            }

            var name = pool.PickRandom(RandomBelow);
            return PlaySound2D(name, bus, false, volume * pool.Volume);
        }

        /// <summary>
        /// Port of play_sound3d(stream, vol, pos, parent): a one-shot spatial
        /// voice on the SFX bus that frees itself when the sample ends.
        /// </summary>
        public AudioSource PlaySound3D(
            string clipName,
            Vector3 position,
            float volume = 1f,
            AudioBus bus = AudioBus.Sfx,
            SpatialRange? range = null)
        {
            var clip = BlitzResources.LoadAudio(clipName);
            if (clip == null)
            {
                Debug.LogWarning("[Blitz] missing audio clip: " + clipName);
                return null;
            }

            var go = new GameObject(clip.name);
            go.transform.SetParent(transform, false);
            go.transform.position = position;

            var source = go.AddComponent<AudioSource>();
            // Only the hangar ambience sends to a reverb zone; see HalloweenHangarAudio.
            source.bypassReverbZones = true;
            source.clip = clip;
            source.loop = false;
            source.playOnAwake = false;
            ConfigureSpatial(source, range ?? SpatialAudio.OneShot);

            var voice = new Voice { Source = source, Bus = bus, Requested = volume };
            _voices.Add(voice);
            source.volume = VolumeOf(voice);
            source.Play();
            Destroy(go, clip.length + 0.25f);
            return source;
        }

        /// <summary>
        /// A spatial voice that loops until it is stopped and rides the
        /// transform it was attached to. The Blitz client plays the rocket's
        /// <c>/weapons/tracer/tracer</c> bed this way, from the shot until the
        /// ATGM stops flying, which a one-shot <see cref="PlaySound3D"/> voice
        /// cannot do. Stop it with <see cref="StopSound2D"/>, which frees any
        /// voice of either kind.
        /// </summary>
        public AudioSource PlayLoop3D(
            SoundPool pool,
            Transform follow,
            float volume = 1f,
            SpatialRange? range = null)
        {
            if (pool == null || !pool.HasStreams || follow == null)
            {
                return null;
            }

            var clip = BlitzResources.LoadAudio(pool.PickRandom(RandomBelow));
            if (clip == null)
            {
                return null;
            }

            var go = new GameObject(clip.name);
            go.transform.SetParent(follow, false);
            go.transform.localPosition = Vector3.zero;

            var source = go.AddComponent<AudioSource>();
            // Only the hangar ambience sends to a reverb zone; see HalloweenHangarAudio.
            source.bypassReverbZones = true;
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = false;
            ConfigureSpatial(source, range ?? SpatialAudio.OneShot);

            var voice = new Voice
            {
                Source = source,
                Bus = AudioBus.Sfx,
                Requested = volume * pool.Volume,
            };
            _voices.Add(voice);
            source.volume = VolumeOf(voice);
            source.Play();
            return source;
        }

        /// <summary>
        /// <see cref="PlayLoop3D"/> for one named clip: a looped spatial voice
        /// riding <paramref name="follow"/> until <see cref="StopSound2D"/>.
        /// </summary>
        public AudioSource PlayClipLoop3D(
            string clipName,
            Transform follow,
            float volume = 1f,
            SpatialRange? range = null)
        {
            if (follow == null)
            {
                return null;
            }

            var clip = BlitzResources.LoadAudio(clipName);
            if (clip == null)
            {
                Debug.LogWarning("[Blitz] missing audio clip: " + clipName);
                return null;
            }

            var go = new GameObject(clip.name);
            go.transform.SetParent(follow, false);
            go.transform.localPosition = Vector3.zero;

            var source = go.AddComponent<AudioSource>();
            // Only the hangar ambience sends to a reverb zone; see HalloweenHangarAudio.
            source.bypassReverbZones = true;
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = false;
            ConfigureSpatial(source, range ?? SpatialAudio.OneShot);

            var voice = new Voice { Source = source, Bus = AudioBus.Sfx, Requested = volume };
            _voices.Add(voice);
            source.volume = VolumeOf(voice);
            source.Play();
            return source;
        }

        public AudioSource PlayPool3D(
            SoundPool pool, Vector3 position, float volume = 1f, SpatialRange? range = null)
        {
            if (pool == null || !pool.HasStreams)
            {
                return null;
            }

            return PlaySound3D(
                pool.PickRandom(RandomBelow), position, volume * pool.Volume, AudioBus.Sfx, range);
        }

        /// <summary>
        /// Every spatial voice: the inverse-distance law tank_sound_manager.gd
        /// authors its players with, heard from the battle view, eased to
        /// silence at the range's end and filtered for distance and occlusion
        /// (<see cref="SpatialVoice"/>).
        /// </summary>
        public static SpatialVoice ConfigureSpatial(AudioSource source, SpatialRange range)
        {
            return SpatialVoice.Configure(source, range);
        }

        /// <summary>Port of stop_sound2d(player).</summary>
        public void StopSound2D(AudioSource source)
        {
            if (source == null)
            {
                return;
            }

            for (var i = _voices.Count - 1; i >= 0; i--)
            {
                if (_voices[i].Source == source)
                {
                    _voices.RemoveAt(i);
                }
            }

            source.Stop();
            Destroy(source.gameObject);
        }

        /// <summary>The click every themed button makes when pressed.</summary>
        public void PlayButtonClick(string clipName)
        {
            PlaySound2D(clipName, AudioBus.Ui, false, ButtonClickVolume);
        }

        /// <summary>
        /// Port of muffle_sfx(muffle): sniper mode ducks the SFX bus to 85%
        /// over 0.3 s and brings the Sniper bus - the inside-the-hull beds -
        /// up from silence at the same rate.
        /// </summary>
        public void MuffleSfx(bool muffle)
        {
            _muffled = muffle;
        }

        /// <summary>Port of play_crew_message(msg).</summary>
        public void PlayCrewMessage(CrewMessage message)
        {
            _crew.Schedule(message);
        }

        /// <summary>Port of clear_sounds() on a level change.</summary>
        public void ClearSounds()
        {
            _crew.Clear();
            StopAll();
        }

        public void ApplySettings()
        {
            for (var i = _voices.Count - 1; i >= 0; i--)
            {
                var voice = _voices[i];
                if (voice.Source == null)
                {
                    _voices.RemoveAt(i);
                    continue;
                }

                voice.Source.volume = VolumeOf(voice);
            }
            SpatialGainsChanged?.Invoke();
        }

        public void StopAll()
        {
            for (var i = 0; i < _voices.Count; i++)
            {
                if (_voices[i].Source != null)
                {
                    Destroy(_voices[i].Source.gameObject);
                }
            }

            _voices.Clear();
        }

        private void Update()
        {
            var target = _muffled ? 1f : 0f;
            if (!Mathf.Approximately(_muffle, target))
            {
                var step = Time.unscaledDeltaTime
                    / TankSoundTuning.MuffleFadeDuration;
                _muffle = Mathf.MoveTowards(_muffle, target, step);
                ApplySettings();
            }

            ProcessCrewMessages();
        }

        /// <summary>Port of process_crew_messages(), driven from _process.</summary>
        private void ProcessCrewMessages()
        {
            CrewMessage message;
            if (!_crew.TryPop(Time.time, out message))
            {
                return;
            }

            var pool = Sounds.Crew(message);
            var name = pool.PickRandom(RandomBelow);
            var clip = string.IsNullOrEmpty(name)
                ? null : BlitzResources.LoadAudio(name);
            if (clip == null)
            {
                Debug.LogWarning(
                    "[Blitz] crew message has no usable stream: " + message);
                return;
            }

            _crew.Started(Time.time, clip.length);
            StartCoroutine(SpeakCrewMessage(clip, pool.Volume));
        }

        private IEnumerator SpeakCrewMessage(AudioClip clip, float volume)
        {
            yield return new WaitForSeconds(CrewMessageQueue.MessageDelay);
            PlaySound2D(clip, AudioBus.Ui, false, volume);
        }

        /// <summary>
        /// The gain a spatial tank player has to write into its own volume:
        /// the bus stage and the sliders, with the spatial falloff left to
        /// the AudioSource itself.
        /// </summary>
        public float SpatialVolume(AudioBus bus, float requested)
        {
            return BusVolume(bus, requested);
        }

        /// <summary>
        /// The gain any voice the service does not own has to write into its
        /// volume - a spatial tank player, the hangar music player - so the
        /// bus stage, the sliders and the muffle reach it as they reach the
        /// service's own voices.
        /// </summary>
        public float BusVolume(AudioBus bus, float requested)
        {
            return AudioBusGains.Volume(
                bus, SliderFor(bus), _settings.VolumeMaster, requested)
                * MuffleFactor(bus);
        }

        private float VolumeOf(Voice voice)
        {
            return AudioBusGains.Volume(
                voice.Bus, SliderFor(voice.Bus), _settings.VolumeMaster, voice.Requested)
                * MuffleFactor(voice.Bus);
        }

        /// <summary>
        /// The bus-level part of muffle_sfx. The Sniper bus sits at silence
        /// until sniper mode lifts it, which is what makes the inside beds
        /// audible only while zoomed in.
        /// </summary>
        private float MuffleFactor(AudioBus bus)
        {
            switch (bus)
            {
                case AudioBus.Sfx:
                    return Mathf.Lerp(1f, TankSoundTuning.MuffleSfxShare, _muffle);
                case AudioBus.Sniper:
                    return _muffle;
                default:
                    return 1f;
            }
        }

        /// <summary>
        /// Godot exposes three sliders. Radio and Sniper are SFX-family buses
        /// with no slider of their own, so they follow the SFX one.
        /// </summary>
        private float SliderFor(AudioBus bus)
        {
            switch (bus)
            {
                case AudioBus.Music:
                    return _settings.VolumeMusic;
                case AudioBus.Ui:
                    return _settings.VolumeUi;
                case AudioBus.Sfx:
                case AudioBus.Radio:
                case AudioBus.Sniper:
                    return _settings.VolumeSfx;
                default:
                    return 1f;
            }
        }
    }
}
