using System;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEngine;
using UnityEngine.Networking;

namespace BlitzLegacy.Runtime.Audio
{
    /// <summary>
    /// The hangar music player, the native port of the Panzer War Miku hangar
    /// player: one voice on the music bus that plays the exported playlist
    /// (<see cref="HangarMusicPlaylist"/>) track by track from StreamingAssets.
    ///
    /// It lives with the bootstrap, so a rebuilt hangar does not cut the
    /// music, and a hangar attaches it for as long as it is up. The player
    /// follows that hangar: it reads and plays only while the hangar wants
    /// music, fades out and holds while the hangar is hidden or a battle
    /// result fanfare plays, and gives its clip back once no hangar wants it,
    /// keeping the place to resume from. Only the current track is in memory,
    /// as Vorbis, decoded while it plays, and the voice goes through the
    /// ten-band equalizer of <see cref="HangarMusicFilter"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HangarMusicPlayer : MonoBehaviour
    {
        public enum PlayerStatus
        {
            /// <summary>No hangar wants music, or there is no playlist.</summary>
            Off,

            /// <summary>The current track is being read.</summary>
            Loading,

            Playing,

            /// <summary>Paused with the player's own button.</summary>
            Paused,

            /// <summary>The track could not be read; the player moves on by itself.</summary>
            Unavailable,
        }

        /// <summary>The fade of a hold, of a resume and of the pause button.</summary>
        public const float FadeSeconds = 0.5f;

        /// <summary>How long a track that cannot be read is shown before the next one is tried.</summary>
        public const float SkipSeconds = 2f;

        /// <summary>A voice that stops this close to the end of its clip has played the track out.</summary>
        private const float EndSlackSeconds = 1f;

        /// <summary>How far before the end a seek lands at most.</summary>
        private const float SeekMarginSeconds = 0.25f;

        private BlitzBootstrap _bootstrap;
        private AudioSource _source;
        private HangarMusicEqualizer _equalizer;
        private HangarMusicPlaylist _playlist;
        private HangarMusicQueue _queue;

        private Component _owner;
        private Func<bool> _wanted;

        private UnityWebRequest _request;
        private int _requestTrack = -1;
        private AudioClip _clip;
        private int _clipTrack = -1;

        // Where the track goes on once its clip plays: a seek made before
        // that, or the place it was left at when its clip was given back.
        private float _resumeAt;

        // Play() was called for the clip.
        private bool _started;

        // The voice has reported playing since. A machine without an audio
        // device never does, and must not read that as the track's end.
        private bool _heard;

        // Paused by the player itself once a hold has faded out.
        private bool _held;

        // The last position the voice reported while playing.
        private float _lastTime;

        private float _fade;
        private bool _unavailable;
        private float _failedAt;
        private int _failures;

        public static HangarMusicPlayer Create(Transform parent, BlitzBootstrap bootstrap)
        {
            if (bootstrap == null)
            {
                throw new ArgumentNullException("bootstrap");
            }

            var go = new GameObject("HangarMusicPlayer");
            go.transform.SetParent(parent, false);
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
            source.bypassReverbZones = true;
            source.priority = 0;
            source.volume = 0f;
            var player = go.AddComponent<HangarMusicPlayer>();
            player._bootstrap = bootstrap;
            player._source = source;
            player._equalizer = new HangarMusicEqualizer();
            go.AddComponent<HangarMusicFilter>().Equalizer = player._equalizer;
            return player;
        }

        public HangarMusicPlaylist Playlist
        {
            get
            {
                EnsurePlaylist();
                return _playlist;
            }
        }

        public int Count
        {
            get { return Playlist.Count; }
        }

        /// <summary>The index of the current track, or -1 for an empty playlist.</summary>
        public int Index
        {
            get
            {
                EnsurePlaylist();
                return _queue.Current;
            }
        }

        /// <summary>The current track, or null for an empty playlist.</summary>
        public HangarMusicTrack Track
        {
            get
            {
                var index = Index;
                return index < 0 ? null : _playlist[index];
            }
        }

        public bool Shuffle
        {
            get { return Saved.Shuffle; }
        }

        public bool RepeatOne
        {
            get { return Saved.RepeatOne; }
        }

        public bool Collapsed
        {
            get { return Saved.Collapsed; }
        }

        /// <summary>The view the panel shows when it is unfolded.</summary>
        public HangarMusicView View
        {
            get { return Saved.View; }
        }

        public bool Paused
        {
            get { return Saved.Paused; }
        }

        /// <summary>Whether the panel was dragged; until it is, it stands where the hangar puts it.</summary>
        public bool Placed
        {
            get { return Saved.Placed; }
        }

        /// <summary>How far the dragged panel's right edge is from the canvas's, in canvas units.</summary>
        public float PlacedRight
        {
            get { return Saved.Right; }
        }

        /// <summary>How far the dragged panel's top edge is from the canvas's, in canvas units.</summary>
        public float PlacedTop
        {
            get { return Saved.Top; }
        }

        /// <summary>The player's own volume, 0..1.</summary>
        public float Volume
        {
            get { return Saved.Volume; }
        }

        /// <summary>The equalizer the voice goes through, in step with the saved bands.</summary>
        public HangarMusicEqualizer Equalizer
        {
            get
            {
                SyncEqualizer();
                return _equalizer;
            }
        }

        public bool EqualizerOn
        {
            get { return Saved.EqOn; }
        }

        /// <summary>Whether a hangar is attached and wants music.</summary>
        public bool Engaged
        {
            get { return _owner != null && _wanted != null && _wanted() && Count > 0; }
        }

        /// <summary>The hangar the player follows, or null.</summary>
        public Component Owner
        {
            get { return _owner; }
        }

        public PlayerStatus Status
        {
            get
            {
                if (!Engaged)
                {
                    return PlayerStatus.Off;
                }

                if (_unavailable)
                {
                    return PlayerStatus.Unavailable;
                }

                if (Saved.Paused)
                {
                    return PlayerStatus.Paused;
                }

                return ClipReady ? PlayerStatus.Playing : PlayerStatus.Loading;
            }
        }

        /// <summary>Whether the voice is sounding right now, fades included.</summary>
        public bool Audible
        {
            get { return _started && !_held && _source.isPlaying && _source.volume > 0f; }
        }

        /// <summary>Seconds into the current track.</summary>
        public double Elapsed
        {
            get
            {
                if (_unavailable)
                {
                    return 0d;
                }

                return ClipReady && _started ? _source.time : _resumeAt;
            }
        }

        /// <summary>The length of the current track.</summary>
        public double Duration
        {
            get
            {
                if (ClipReady)
                {
                    return _clip.length;
                }

                var track = Track;
                return track == null ? 0d : track.Seconds;
            }
        }

        private HangarMusicSettings Saved
        {
            get { return _bootstrap.Settings.HangarMusic; }
        }

        private bool ClipReady
        {
            get { return _clip != null && _queue != null && _clipTrack == _queue.Current; }
        }

        /// <summary>
        /// Makes <paramref name="owner"/> the hangar the player follows; it
        /// plays while the owner is alive and <paramref name="wanted"/> says
        /// so. A hangar built in place of another takes the music over without
        /// a break.
        /// </summary>
        public void Attach(Component owner, Func<bool> wanted)
        {
            if (owner == null || wanted == null)
            {
                throw new ArgumentNullException(owner == null ? "owner" : "wanted");
            }

            _owner = owner;
            _wanted = wanted;
        }

        /// <summary>Lets go of <paramref name="owner"/>, unless another hangar has taken over since.</summary>
        public void Detach(Component owner)
        {
            if (ReferenceEquals(_owner, owner))
            {
                _owner = null;
                _wanted = null;
            }
        }

        public void TogglePause()
        {
            Saved.Paused = !Saved.Paused;
            Persist();
        }

        public void Next()
        {
            if (Count == 0)
            {
                return;
            }

            _failures = 0;
            ChangeTo(_queue.Next());
        }

        /// <summary>
        /// Plays track <paramref name="index"/> of the playlist from its
        /// start, as a press on its row does, and lifts the pause. The current
        /// track only goes on: paused, it resumes where it was, and one that
        /// could not be read is tried again.
        /// </summary>
        public void Play(int index)
        {
            if (Count == 0)
            {
                return;
            }

            if (index < 0 || index >= Count)
            {
                throw new ArgumentOutOfRangeException("index", index, "no such track in the playlist");
            }

            _failures = 0;
            if (index != _queue.Current)
            {
                ChangeTo(_queue.Select(index));
            }
            else if (_unavailable)
            {
                ChangeTo(index);
            }

            if (Saved.Paused)
            {
                Saved.Paused = false;
                Persist();
            }
        }

        /// <summary>Back a track, or to the start of this one once it has played a few seconds.</summary>
        public void Previous()
        {
            if (Count == 0)
            {
                return;
            }

            _failures = 0;
            ChangeTo(_queue.Previous(Elapsed));
        }

        public void Seek(double seconds)
        {
            if (Count == 0 || _unavailable)
            {
                return;
            }

            var to = (float)Math.Max(0d, Math.Min(seconds, Duration - SeekMarginSeconds));
            if (ClipReady && _started)
            {
                _source.time = to;
                _lastTime = to;
            }
            else
            {
                _resumeAt = to;
            }
        }

        /// <summary>Sets the volume while its slider is dragged; <see cref="CommitVolume"/> saves it.</summary>
        public void SetVolume(float volume)
        {
            Saved.Volume = volume;
            ApplyVolume();
        }

        public void CommitVolume()
        {
            Persist();
        }

        public void SetShuffle(bool on)
        {
            EnsurePlaylist();
            Saved.Shuffle = on;
            _queue.SetShuffle(on);
            Persist();
        }

        public void SetRepeatOne(bool on)
        {
            EnsurePlaylist();
            Saved.RepeatOne = on;
            _queue.RepeatOne = on;
            Persist();
        }

        public void SetCollapsed(bool on)
        {
            Saved.Collapsed = on;
            Persist();
        }

        public void SetView(HangarMusicView view)
        {
            Saved.View = view;
            Persist();
        }

        /// <summary>Saves where the panel was dragged to: its right and top edges in from the canvas's.</summary>
        public void Place(float right, float top)
        {
            var saved = Saved;
            saved.Placed = true;
            saved.Right = right;
            saved.Top = top;
            Persist();
        }

        /// <summary>The gain of equalizer band <paramref name="band"/>, in whole decibels.</summary>
        public int EqualizerGain(int band)
        {
            return Saved.EqGain(band);
        }

        /// <summary>Switches the equalizer; off, the bands keep their gains for the next time.</summary>
        public void SetEqualizer(bool on)
        {
            Saved.EqOn = on;
            SyncEqualizer();
            Persist();
        }

        /// <summary>
        /// Moves a band while its slider is dragged and switches the equalizer
        /// on, so the change is heard; <see cref="CommitEqualizer"/> saves it.
        /// </summary>
        public void SetEqualizerGain(int band, int decibels)
        {
            Saved.SetEqGain(band, decibels);
            Saved.EqOn = true;
            SyncEqualizer();
        }

        public void CommitEqualizer()
        {
            Persist();
        }

        /// <summary>Puts every band back to 0 dB; the switch stays as it is.</summary>
        public void ResetEqualizer()
        {
            for (var band = 0; band < HangarMusicEqualizer.BandCount; band++)
            {
                Saved.SetEqGain(band, 0);
            }

            SyncEqualizer();
            Persist();
        }

        /// <summary>
        /// Hands the saved bands to the equalizer on the audio thread. It runs
        /// every frame, so settings read or put back whole reach the music too;
        /// bands that did not change cost nothing there.
        /// </summary>
        private void SyncEqualizer()
        {
            var saved = Saved;
            for (var band = 0; band < HangarMusicEqualizer.BandCount; band++)
            {
                _equalizer.SetGain(band, saved.EqGain(band));
            }

            _equalizer.Enabled = saved.EqOn;
        }

        private void EnsurePlaylist()
        {
            if (_playlist != null)
            {
                return;
            }

            _playlist = HangarMusicPlaylist.Parse(BlitzResources.LoadText(HangarMusicPlaylist.Resource));
            var saved = Saved;
            _queue = new HangarMusicQueue(
                _playlist.Count, _playlist.StartIndex(saved.Track), new UnityRandomSource());
            _queue.SetShuffle(saved.Shuffle);
            _queue.RepeatOne = saved.RepeatOne;
            if (_playlist.Count == 0)
            {
                Debug.LogWarning("[Blitz] hangar music: the playlist is empty, the player stays off");
            }
        }

        private void Update()
        {
            SyncEqualizer();
            if (!Engaged)
            {
                Release();
                return;
            }

            if (_unavailable)
            {
                if (Time.unscaledTime - _failedAt >= SkipSeconds && _failures < _playlist.Count)
                {
                    ChangeTo(_queue.Next());
                }

                return;
            }

            if (!ClipReady)
            {
                Load();
                if (!ClipReady)
                {
                    return;
                }
            }

            var step = Time.unscaledDeltaTime / FadeSeconds;
            if (!Saved.Paused && !Held())
            {
                if (!_started)
                {
                    Begin();
                }
                else if (_held)
                {
                    _source.UnPause();
                    _held = false;
                }

                _fade = Mathf.MoveTowards(_fade, 1f, step);
            }
            else
            {
                _fade = Mathf.MoveTowards(_fade, 0f, step);
                if (_fade <= 0f && _started && !_held)
                {
                    _source.Pause();
                    _held = true;
                }
            }

            ApplyVolume();
            Watch();
        }

        /// <summary>
        /// Whether the attached hangar holds the music: while it is hidden
        /// (a battle loading over it) or the battle result plays its fanfare.
        /// </summary>
        private bool Held()
        {
            if (!_owner.gameObject.activeInHierarchy)
            {
                return true;
            }

            var result = _bootstrap.Result;
            return result != null && result.MusicPlaying;
        }

        /// <summary>Starts the clip, at the place a seek or the last hangar left.</summary>
        private void Begin()
        {
            _source.clip = _clip;
            _source.Play();
            var at = Mathf.Min(_resumeAt, _clip.length - SeekMarginSeconds);
            if (at > 0f)
            {
                _source.time = at;
                _fade = 0f;
            }
            else
            {
                // A track from its start needs no fade-in; that would only
                // swallow its first notes.
                at = 0f;
                _fade = 1f;
            }

            _lastTime = at;
            _resumeAt = 0f;
            _started = true;
            _held = false;
            _heard = false;
        }

        /// <summary>
        /// Notices the end of a track. A voice that stops near the end has
        /// played it out; one stopped earlier was cut by the system (an output
        /// device that changed), and goes on from where it was.
        /// </summary>
        private void Watch()
        {
            if (!_started || _held)
            {
                return;
            }

            if (_source.isPlaying)
            {
                _heard = true;
                _lastTime = _source.time;
                return;
            }

            if (!_heard || AudioListener.pause)
            {
                return;
            }

            if (_lastTime >= _clip.length - EndSlackSeconds)
            {
                ChangeTo(_queue.Finished());
                return;
            }

            // Once: a voice that never sounds again is not restarted every frame.
            _source.Play();
            _source.time = _lastTime;
            _heard = false;
        }

        /// <summary>
        /// Makes <paramref name="index"/> the current track from its start.
        /// The same track starts over on the clip it has; another one is read
        /// on the next frame.
        /// </summary>
        private void ChangeTo(int index)
        {
            _unavailable = false;
            _resumeAt = 0f;
            _lastTime = 0f;
            if (_clip != null && _clipTrack == index)
            {
                _source.Stop();
                _started = false;
                _held = false;
                _heard = false;
            }
            else
            {
                DropClip();
            }

            Remember();
        }

        private void Load()
        {
            var index = _queue.Current;
            if (_request != null && _requestTrack != index)
            {
                AbortRequest();
            }

            var track = _playlist[index];
            if (_request == null)
            {
                DropClip();
                _request = UnityWebRequestMultimedia.GetAudioClip(TrackUri(track), AudioType.OGGVORBIS);
                var handler = (DownloadHandlerAudioClip)_request.downloadHandler;
                // Kept as Vorbis and decoded while it plays: a few megabytes
                // per track instead of the tens its samples would take.
                handler.compressed = true;
                handler.streamAudio = false;
                _requestTrack = index;
                _request.SendWebRequest();
                return;
            }

            if (!_request.isDone)
            {
                return;
            }

            AudioClip clip = null;
            string error = null;
            if (_request.result == UnityWebRequest.Result.Success)
            {
                clip = DownloadHandlerAudioClip.GetContent(_request);
                if (clip == null || clip.length <= 0f)
                {
                    error = "no audio in the file";
                }
            }
            else
            {
                error = _request.error;
            }

            _request.Dispose();
            _request = null;
            _requestTrack = -1;
            if (error != null)
            {
                if (clip != null)
                {
                    Destroy(clip);
                }

                Fail(track, error);
                return;
            }

            clip.name = "HangarMusic " + track.Id;
            _clip = clip;
            _clipTrack = index;
            _started = false;
            _held = false;
            _heard = false;
            _failures = 0;
            Debug.Log("[Blitz] hangar music: loaded " + track.Id + " (" + (index + 1) + "/" + _playlist.Count
                + ", " + HangarMusicPlaylist.FormatTime(clip.length) + ")");
        }

        private void Fail(HangarMusicTrack track, string error)
        {
            _unavailable = true;
            _failedAt = Time.unscaledTime;
            _failures++;
            Debug.LogWarning("[Blitz] hangar music: cannot read " + track.Id + ": " + error);
        }

        private string TrackUri(HangarMusicTrack track)
        {
            var path = Application.streamingAssetsPath + "/" + _playlist.Folder + "/" + track.File;
            // Android keeps StreamingAssets inside the APK and already names
            // them with a jar: URL; elsewhere they are plain files.
            return path.Contains("://") ? path : new Uri(path).AbsoluteUri;
        }

        private void ApplyVolume()
        {
            var gain = _clipTrack >= 0 && _clipTrack < _playlist.Count ? _playlist[_clipTrack].Gain : 1f;
            var fade = _fade * _fade * (3f - (2f * _fade));
            _source.volume = _bootstrap.Audio.BusVolume(AudioBus.Music, Saved.Volume * gain * fade);
        }

        /// <summary>
        /// Once no hangar wants music: fades out what plays, then gives the
        /// clip back and keeps the place for the next hangar.
        /// </summary>
        private void Release()
        {
            AbortRequest();
            _unavailable = false;
            if (_clip == null)
            {
                _fade = 0f;
                return;
            }

            if (_started && !_held && _fade > 0f && _source.isPlaying)
            {
                _fade = Mathf.MoveTowards(_fade, 0f, Time.unscaledDeltaTime / FadeSeconds);
                ApplyVolume();
                if (_fade > 0f)
                {
                    return;
                }
            }

            if (_started)
            {
                _resumeAt = _source.time;
            }

            DropClip();
        }

        private void DropClip()
        {
            if (_source.clip != null)
            {
                _source.Stop();
                _source.clip = null;
            }

            if (_clip != null)
            {
                Destroy(_clip);
                _clip = null;
            }

            _clipTrack = -1;
            _started = false;
            _held = false;
            _heard = false;
            _fade = 0f;
        }

        private void AbortRequest()
        {
            if (_request == null)
            {
                return;
            }

            _request.Abort();
            _request.Dispose();
            _request = null;
            _requestTrack = -1;
        }

        /// <summary>Saves the current track, so the next session starts on it.</summary>
        private void Remember()
        {
            var track = Track;
            var id = track == null ? string.Empty : track.Id;
            if (string.Equals(Saved.Track, id, StringComparison.Ordinal))
            {
                return;
            }

            Saved.Track = id;
            Persist();
        }

        private void Persist()
        {
            if (_bootstrap.Saves != null)
            {
                _bootstrap.Saves.SaveSettings(_bootstrap.Settings);
            }
        }

        private void OnDestroy()
        {
            AbortRequest();
            if (_clip != null)
            {
                Destroy(_clip);
                _clip = null;
            }
        }
    }
}
