using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// 11.20 Big Boss ability audio. The original Wwise media play on the user-controlled SFX
    /// bus through the bank's HIRC cue trees exported by Tools/port_big_boss_audio_1120.py:
    /// random/sequence/layer containers, loop counts, crossfade and trigger-rate transitions,
    /// avoid-repeat histories and Stop actions. RTPC curves and the native Wwise mixer are not
    /// reproduced.
    /// </summary>
    public sealed class BigBossAudio : MonoBehaviour
    {
        public const string EventPrefix = "play_hlwn_2021_abl_";
        /// <summary>Anti-click release of a stopped voice.</summary>
        private const float StopFade = 0.1f;
        /// <summary>Shortest trigger-rate period, guarding a zero-length source value.</summary>
        private const float MinTriggerRate = 0.05f;

        private sealed class Cue
        {
            public string Type = "random";
            public AudioClip Clip;
            public int Loop = 1;
            public string Transition = "none";
            public float TransitionSeconds;
            public int AvoidRepeats;
            public readonly List<Cue> Children = new List<Cue>();
            /// <summary>Child indices excluded by "avoid repeating the last N".</summary>
            public readonly List<int> Recent = new List<int>();
        }

        private sealed class SoundEvent
        {
            public string Name;
            public Cue Cue;
            public float MaxAttenuation;
            public readonly List<string> Stops = new List<string>();
        }

        private static Dictionary<string, SoundEvent> _events;

        private static Dictionary<string, SoundEvent> Events
        {
            get
            {
                if (_events != null) return _events;
                _events = new Dictionary<string, SoundEvent>();
                var data = JsonResources.Load("Data/big_boss_audio_1120");
                if (data == null) return _events;
                var clips = new Dictionary<string, AudioClip>();
                foreach (var field in data["events"].Fields)
                {
                    var e = new SoundEvent { Name = field.Key, MaxAttenuation = field.Value["maxAttenuation"].AsFloat(250) };
                    e.Cue = ParseCue(field.Value["cue"], data["clips"], clips);
                    foreach (var stop in field.Value["stops"].Items) e.Stops.Add(stop.AsString());
                    _events[field.Key] = e;
                }
                return _events;
            }
        }

        private static Cue ParseCue(JsonNode node, JsonNode clips, Dictionary<string, AudioClip> loaded)
        {
            var cue = new Cue();
            if (node.Has("media"))
            {
                cue.Type = "media";
                var id = node["media"].AsLong().ToString(CultureInfo.InvariantCulture);
                AudioClip clip;
                if (!loaded.TryGetValue(id, out clip))
                {
                    clip = Resources.Load<AudioClip>(clips[id]["resource"].AsString());
                    loaded[id] = clip;
                }
                cue.Clip = clip;
                return cue;
            }
            cue.Type = node["type"].AsString("random");
            cue.Loop = node["loop"].AsInt(1);
            cue.Transition = node["transition"].AsString("none");
            cue.TransitionSeconds = node["transitionSeconds"].AsFloat();
            cue.AvoidRepeats = node["avoidRepeats"].AsInt();
            foreach (var child in node["children"].Items) cue.Children.Add(ParseCue(child, clips, loaded));
            return cue;
        }

        /// <summary>Parses the cue trees and decodes every clip before the battle, so the first
        /// activation does not hitch on Resources.Load and Vorbis decompression.</summary>
        public static void Preload()
        {
            foreach (var e in Events.Values) Warm(e.Cue);
        }

        private static void Warm(Cue cue)
        {
            if (cue.Clip != null && cue.Clip.loadState == AudioDataLoadState.Unloaded) cue.Clip.LoadAudioData();
            foreach (var child in cue.Children) Warm(child);
        }

        /// <summary>The 11.20 Wwise event a <see cref="BigBossTank.Effect"/> id posts, or null.</summary>
        public static string EventFor(string effect)
        {
            switch (effect)
            {
                case "gravityBurst": return EventPrefix + "boss_repulsion";
                case "railgunShot": return EventPrefix + "boss_annihilator";
                case "gravityShot": return EventPrefix + "graviton";
                case "kamikazeAbility": return EventPrefix + "kamikaze";
                case "poisonedShell": return EventPrefix + "killer_activation";
                case "levelUp": return EventPrefix + "minion_level_up";
                case "permanentDamageShield": return EventPrefix + "graviton_passive";
                default: return null;
            }
        }

        /// <summary>
        /// Pure cue scheduling: which clip starts when, whether it loops and how it fades.
        /// <see cref="BigBossAudio"/> binds the voices to AudioSources; tests read the plan.
        /// </summary>
        public sealed class CueTimeline
        {
            public sealed class Voice
            {
                public string Event;
                public AudioClip Clip;
                public bool Loop;
                public float At;
                public float FadeIn;
                public float FadeOutAt = float.PositiveInfinity;
                public float FadeOut;
                public float MaxDistance;
                public bool Anchored;
                public Vector3 Point;
                public bool Started;
                public bool Ramping;
                public AudioSource Source;

                /// <summary>Crossfade/release gain at <paramref name="now"/>.</summary>
                public float Gain(float now)
                {
                    var gain = FadeIn > 0 ? Mathf.Clamp01((now - At) / FadeIn) : 1;
                    if (now >= FadeOutAt) gain *= FadeOut > 0 ? Mathf.Clamp01(1 - (now - FadeOutAt) / FadeOut) : 0;
                    return gain;
                }

                public bool Released(float now) { return now >= FadeOutAt + FadeOut; }
            }

            private sealed class Repeater
            {
                public SoundEvent Event;
                public Cue Cue;
                public float Next;
                public int Index;
                public List<Voice> Last;
                public float LastEnd;
            }

            public readonly List<Voice> Voices = new List<Voice>();
            private readonly List<Repeater> _repeaters = new List<Repeater>();

            public static bool Exists(string eventName) { return eventName != null && Events.ContainsKey(eventName); }

            public bool IsPlaying(string eventName)
            {
                foreach (var v in Voices) if (v.Event == eventName) return true;
                foreach (var r in _repeaters) if (r.Event.Name == eventName) return true;
                return false;
            }

            /// <summary>Posts an event: runs its Stop actions, then schedules its cue from
            /// <paramref name="now"/>. Returns the voices it planned, or null when unknown.</summary>
            public List<Voice> Post(string eventName, float now)
            {
                SoundEvent e;
                if (eventName == null || !Events.TryGetValue(eventName, out e) || e.Cue == null) return null;
                foreach (var target in e.Stops) Stop(target, now);
                var planned = new List<Voice>();
                Schedule(e, e.Cue, now, planned);
                return planned;
            }

            /// <summary>Plays only the opening step of an event's playlist, e.g. the sting that
            /// starts a looping state cue, as a one-shot confirmation without its loop.</summary>
            public List<Voice> PostIntro(string eventName, float now)
            {
                SoundEvent e;
                if (eventName == null || !Events.TryGetValue(eventName, out e) || e.Cue == null) return null;
                var cue = e.Cue;
                while (cue.Type == "sequence" && cue.Children.Count > 0) cue = cue.Children[0];
                // An endless opening step is the loop itself, not a sting (a media cue never loops).
                if (cue.Loop == 0) return null;
                var planned = new List<Voice>();
                Schedule(e, cue, now, planned);
                return planned;
            }

            /// <summary>Releases every voice of an event; unstarted ones are dropped.</summary>
            public void Stop(string eventName, float now)
            {
                _repeaters.RemoveAll(r => r.Event.Name == eventName);
                for (var i = Voices.Count - 1; i >= 0; i--)
                {
                    var v = Voices[i];
                    if (v.Event != eventName) continue;
                    if (!v.Started) { Voices.RemoveAt(i); continue; }
                    if (now < v.FadeOutAt) { v.FadeOutAt = now; v.FadeOut = StopFade; }
                }
            }

            /// <summary>Starts every due iteration of the endless playlists.</summary>
            public void Advance(float now)
            {
                for (var i = _repeaters.Count - 1; i >= 0; i--)
                    if (i < _repeaters.Count) Repeat(_repeaters[i], now, null);
            }

            /// <summary>Schedules <paramref name="cue"/> at <paramref name="at"/> and returns its
            /// length (PositiveInfinity when it never ends).</summary>
            private float Schedule(SoundEvent e, Cue cue, float at, List<Voice> planned)
            {
                if (cue.Type == "media")
                {
                    if (cue.Clip == null) return 0;
                    var voice = new Voice { Event = e.Name, Clip = cue.Clip, At = at, MaxDistance = e.MaxAttenuation };
                    Voices.Add(voice); planned.Add(voice);
                    return cue.Clip.length;
                }
                if (cue.Children.Count == 0) return 0;
                if (cue.Type == "layer")
                {
                    var longest = 0f;
                    foreach (var child in cue.Children) longest = Mathf.Max(longest, Schedule(e, child, at, planned));
                    return longest;
                }
                if (cue.Loop == 0) return Endless(e, cue, at, planned);
                // A random container plays one pick per loop, a sequence its playlist in order.
                var steps = cue.Loop * (cue.Type == "sequence" ? cue.Children.Count : 1);
                var start = at;
                var length = 0f;
                List<Voice> previous = null;
                for (var i = 0; i < steps; i++)
                {
                    var child = cue.Type == "sequence" ? cue.Children[i % cue.Children.Count] : Pick(cue);
                    var begin = i == 0 ? at : Next(cue, start, length);
                    var voices = new List<Voice>();
                    var childLength = Schedule(e, child, begin, voices);
                    if (previous != null) Crossfade(cue, previous, voices, begin, start + length);
                    planned.AddRange(voices);
                    start = begin; length = childLength; previous = voices;
                    if (float.IsPositiveInfinity(length)) return length;
                }
                return start + length - at;
            }

            private float Endless(SoundEvent e, Cue cue, float at, List<Voice> planned)
            {
                var only = cue.Children.Count == 1 ? cue.Children[0] : null;
                if (cue.Transition == "none" && only != null && only.Type == "media")
                {
                    // One sound repeated back to back loops seamlessly on its own source.
                    if (only.Clip == null) return 0;
                    var loop = new Voice { Event = e.Name, Clip = only.Clip, Loop = true, At = at, MaxDistance = e.MaxAttenuation };
                    Voices.Add(loop); planned.Add(loop);
                    return float.PositiveInfinity;
                }
                var repeater = new Repeater { Event = e, Cue = cue, Next = at };
                _repeaters.Add(repeater);
                Repeat(repeater, at, planned);
                return float.PositiveInfinity;
            }

            private void Repeat(Repeater r, float now, List<Voice> planned)
            {
                // A stalled frame resumes the rhythm instead of firing every missed iteration.
                if (r.Last != null && now - r.Next > Mathf.Max(MinTriggerRate, r.Cue.TransitionSeconds)) r.Next = now;
                while (r.Next <= now)
                {
                    var child = r.Cue.Type == "sequence" ? r.Cue.Children[r.Index++ % r.Cue.Children.Count] : Pick(r.Cue);
                    var begin = r.Next;
                    var voices = new List<Voice>();
                    var length = Schedule(r.Event, child, begin, voices);
                    if (r.Last != null) Crossfade(r.Cue, r.Last, voices, begin, r.LastEnd);
                    if (planned != null) planned.AddRange(voices);
                    if (length <= 0 || float.IsPositiveInfinity(length)) { _repeaters.Remove(r); return; }
                    r.Last = voices; r.LastEnd = begin + length;
                    r.Next = Next(r.Cue, begin, length);
                }
            }

            private static float Next(Cue cue, float start, float length)
            {
                switch (cue.Transition)
                {
                    case "triggerRate": return start + Mathf.Max(MinTriggerRate, cue.TransitionSeconds);
                    case "delay": return start + length + cue.TransitionSeconds;
                    case "crossfade": return start + length - Mathf.Min(cue.TransitionSeconds, length * 0.5f);
                    default: return start + length;
                }
            }

            /// <summary>Wwise starts the next item TransitionSeconds before the current one ends;
            /// the overlap is capped at half the current item so its attack stays intact
            /// (adapter choice: the native clamp is not documented in the bank).</summary>
            private static void Crossfade(Cue cue, List<Voice> previous, List<Voice> next, float begin, float previousEnd)
            {
                var overlap = previousEnd - begin;
                if (cue.Transition != "crossfade" || overlap <= 0) return;
                foreach (var v in previous) if (v.FadeOutAt > begin) { v.FadeOutAt = begin; v.FadeOut = overlap; }
                foreach (var v in next) if (Mathf.Approximately(v.At, begin)) v.FadeIn = overlap;
            }

            private static Cue Pick(Cue cue)
            {
                var count = cue.Children.Count;
                if (count == 1) return cue.Children[0];
                var avoid = Mathf.Clamp(cue.AvoidRepeats, 0, count - 1);
                while (cue.Recent.Count > avoid) cue.Recent.RemoveAt(0);
                var pick = Random.Range(0, count - cue.Recent.Count);
                for (var i = 0; i < count; i++)
                {
                    if (cue.Recent.Contains(i)) continue;
                    if (pick-- > 0) continue;
                    if (avoid > 0) cue.Recent.Add(i);
                    return cue.Children[i];
                }
                return cue.Children[0];
            }
        }

        private BigBossTank _owner;
        private readonly CueTimeline _timeline = new CueTimeline();
        private readonly HashSet<string> _held = new HashSet<string>();
        private bool _poison;

        public static void Attach(BigBossTank owner)
        {
            var c = owner.gameObject.AddComponent<BigBossAudio>();
            c._owner = owner;
            owner.Effect += c.Event;
        }

        private bool Own { get { return _owner.Tank == _owner.Session.Player; } }
        private bool Audible { get { return _owner.Tank.Team == _owner.Session.Player.Team || _owner.Tank.Spotted; } }
        private static AudioService Service { get { return Application.isPlaying ? BlitzBootstrap.Current?.Audio : null; } }

        /// <summary>The healer's confirmation that a shell handed its shield to an ally: the
        /// sting of the receiver's shield cue, played for the giving player only (the receiver
        /// already holds the full shield loop on its own hull).</summary>
        public const string ShieldGivenEvent = EventPrefix + "shield_on";

        private void Event(string id, Vector3 point)
        {
            if (id == "shieldGiven")
            {
                if (Service != null && Own) _timeline.PostIntro(ShieldGivenEvent, Time.time);
                return;
            }
            var name = EventFor(id);
            if (name == null || Service == null) return;
            // The bomber's blast is a world event: it is heard even from an unspotted wreck.
            var blast = id == "kamikazeAbility";
            if (!blast && !Audible) return;
            // The passive field hums once per absorbed volley, not once per pellet.
            if (id == "permanentDamageShield" && _timeline.IsPlaying(name)) return;
            var voices = _timeline.Post(name, Time.time);
            if (blast && voices != null) foreach (var v in voices) { v.Anchored = true; v.Point = point; }
        }

        private void Update()
        {
            if (_owner == null || _owner.Session == null || Service == null) return;
            var now = Time.time;
            var alive = _owner.Session.Started && !_owner.Tank.Dead;
            Hold("shield_on", "shield_off", alive && _owner.ShieldUntil > now, alive, now);
            Hold("heal_aura_on", "heal_aura_off", alive && _owner.HealingAuraActive, alive, now);
            var poison = alive && _owner.Poisoned;
            if (poison != _poison)
            {
                if (poison) { if (Audible) _timeline.Post(EventPrefix + "killer_damage_on", now); }
                else
                {
                    _timeline.Stop(EventPrefix + "killer_damage_on", now);
                    if (alive && Audible) _timeline.Post(EventPrefix + "killer_damage_off", now);
                }
                _poison = poison;
            }
            _timeline.Advance(now);
            Bind(now);
        }

        /// <summary>A state loop: the "on" cue plays while the state lasts (muted while the tank
        /// is not audible, so a spotting flicker does not restart it) and "off" closes it.</summary>
        private void Hold(string on, string off, bool wanted, bool alive, float now)
        {
            on = EventPrefix + on;
            if (wanted == _held.Contains(on)) return;
            if (wanted) { _held.Add(on); _timeline.Post(on, now); return; }
            _held.Remove(on);
            _timeline.Stop(on, now);
            if (alive && Audible) _timeline.Post(EventPrefix + off, now);
        }

        private void Bind(float now)
        {
            var audio = Service;
            var muted = !Audible;
            var voices = _timeline.Voices;
            for (var i = voices.Count - 1; i >= 0; i--)
            {
                var v = voices[i];
                if (v.At > now) continue;
                if (!v.Started)
                {
                    v.Started = true;
                    v.Source = audio.PlaySound2D(v.Clip, AudioBus.Sfx, v.Loop, 1);
                    if (v.Source != null) Configure(v);
                }
                if (v.Source == null || (!v.Loop && !v.Source.isPlaying) || v.Released(now))
                {
                    if (v.Source != null) audio.StopSound2D(v.Source);
                    voices.RemoveAt(i);
                    continue;
                }
                var gain = v.Gain(now);
                if (gain < 1 || v.Ramping) { v.Source.volume = audio.SpatialVolume(AudioBus.Sfx, gain); v.Ramping = gain < 1; }
                var mute = muted && _held.Contains(v.Event);
                if (v.Source.mute != mute) v.Source.mute = mute;
                if (v.Source.spatialBlend > 0 && !v.Anchored) v.Source.transform.position = transform.position;
            }
        }

        private void Configure(CueTimeline.Voice v)
        {
            if (Own) return; // the player's own abilities stay 2D, as before
            var s = v.Source;
            // The event's Wwise maxAttenuation is its reach; nearer it follows the
            // battle's inverse-distance law, heard from the view and through cover.
            AudioService.ConfigureSpatial(s, SpatialAudio.Cue(v.MaxDistance));
            s.transform.position = v.Anchored ? v.Point : transform.position;
        }

        private void OnDestroy()
        {
            if (_owner != null) _owner.Effect -= Event;
            var audio = Service;
            foreach (var v in _timeline.Voices)
                if (v.Source != null) { if (audio != null) audio.StopSound2D(v.Source); else Destroy(v.Source.gameObject); }
            _timeline.Voices.Clear();
        }
    }
}
