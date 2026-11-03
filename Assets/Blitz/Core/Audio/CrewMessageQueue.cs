using System.Collections.Generic;

namespace BlitzLegacy.Core.Audio
{
    /// <summary>
    /// Port of the crew voice queue in Godot's Audio autoload
    /// (<c>play_crew_message</c> / <c>process_crew_messages</c>).
    ///
    /// The queue is a plain list: a message already waiting is dropped, a
    /// top-priority message wipes everything that was waiting before it, and
    /// <c>process</c> pops from the back, so the newest line is spoken first.
    /// Playback is gated by <c>next_cmsg</c>, which the caller advances with
    /// the length of the clip it actually started plus the source's 0.25 s
    /// tail; the source also waits half a second before the line is heard.
    /// </summary>
    public sealed class CrewMessageQueue
    {
        /// <summary><c>next_cmsg = curtime() + length + 0.25</c>.</summary>
        public const float MessageTail = 0.25f;

        /// <summary><c>await create_timer(0.5).timeout</c> before play_sound2d.</summary>
        public const float MessageDelay = 0.5f;

        /// <summary>
        /// <c>crew_msg_top_priority</c>: module and death callouts clear
        /// whatever the crew was about to say.
        /// </summary>
        public static readonly CrewMessage[] TopPriority =
        {
            CrewMessage.ModuleTrackCrit,
            CrewMessage.ModuleTrackDestroyed,
            CrewMessage.ModuleTrackFixed,
            CrewMessage.ModuleAmmoBayCrit,
            CrewMessage.FireStarted,
            CrewMessage.FireStopped,
            CrewMessage.TankDestroyed,
        };

        /// <summary>
        /// <c>crew_msg_onetimers</c>. The source declares the list but never
        /// reads it, so it is kept here for parity without behaviour.
        /// </summary>
        public static readonly CrewMessage[] OneTimers =
        {
            CrewMessage.ArmorNotHit,
        };

        private readonly List<CrewMessage> _scheduled = new List<CrewMessage>();

        private float _next;

        public int Count { get { return _scheduled.Count; } }

        public float NextMessageTime { get { return _next; } }

        public static bool IsTopPriority(CrewMessage message)
        {
            for (var i = 0; i < TopPriority.Length; i++)
            {
                if (TopPriority[i] == message)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Port of <c>play_crew_message</c>.</summary>
        public void Schedule(CrewMessage message)
        {
            for (var i = 0; i < _scheduled.Count; i++)
            {
                if (_scheduled[i] == message)
                {
                    return;
                }
            }

            if (IsTopPriority(message))
            {
                _scheduled.Clear();
            }

            _scheduled.Add(message);
        }

        /// <summary>
        /// Port of <c>process_crew_messages</c>: pops the newest waiting line
        /// once the previous one has finished.
        /// </summary>
        public bool TryPop(float time, out CrewMessage message)
        {
            message = CrewMessage.BattleStart;
            if (_scheduled.Count == 0 || time < _next)
            {
                return false;
            }

            var last = _scheduled.Count - 1;
            message = _scheduled[last];
            _scheduled.RemoveAt(last);
            return true;
        }

        /// <summary>Blocks the queue for the length of the started clip.</summary>
        public void Started(float time, float clipLength)
        {
            _next = time + clipLength + MessageTail;
        }

        public void Clear()
        {
            _scheduled.Clear();
            _next = 0f;
        }
    }
}
