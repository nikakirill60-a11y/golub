using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Rng;

namespace BlitzLegacy.Core.Audio
{
    /// <summary>
    /// The play order of the hangar music player, with the transport of the
    /// Panzer War Miku player: next, previous, shuffle and repeat one.
    ///
    /// In order, the playlist runs round and round. Shuffled, every round
    /// plays every track once in a fresh order, the next round never opens
    /// with the track that closed the last one, and previous walks back
    /// through what was played. Repeat one only holds a track that plays to
    /// its end; next and previous still move.
    /// </summary>
    public sealed class HangarMusicQueue
    {
        /// <summary>Previous past this many seconds into a track starts it over instead.</summary>
        public const double RestartSeconds = 3d;

        private readonly IRandomSource _random;

        /// <summary>The shuffled play order, every round appended to the last.</summary>
        private readonly List<int> _order = new List<int>();
        private int _position;

        public HangarMusicQueue(int count, int current, IRandomSource random)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count");
            }

            _random = random ?? throw new ArgumentNullException("random");
            Count = count;
            Current = count == 0 ? -1 : Math.Max(0, Math.Min(count - 1, current));
        }

        public int Count { get; }

        /// <summary>The track playing, or -1 for an empty playlist.</summary>
        public int Current { get; private set; }

        public bool Shuffle { get; private set; }

        public bool RepeatOne { get; set; }

        /// <summary>
        /// Shuffle on deals a new order that starts with the track playing;
        /// shuffle off goes on in playlist order from it.
        /// </summary>
        public void SetShuffle(bool on)
        {
            if (on == Shuffle)
            {
                return;
            }

            Shuffle = on;
            _order.Clear();
            _position = 0;
            if (on && Count > 0)
            {
                Deal(Current);
            }
        }

        /// <summary>Plays <paramref name="index"/> now; a shuffled order is dealt again from it.</summary>
        public int Select(int index)
        {
            if (Count == 0)
            {
                return Current;
            }

            Current = Math.Max(0, Math.Min(Count - 1, index));
            if (Shuffle)
            {
                _order.Clear();
                _position = 0;
                Deal(Current);
            }

            return Current;
        }

        /// <summary>The track after one that played to its end.</summary>
        public int Finished()
        {
            return RepeatOne ? Current : Next();
        }

        /// <summary>The next track; past the last one the playlist starts again.</summary>
        public int Next()
        {
            if (Count == 0)
            {
                return Current;
            }

            if (!Shuffle)
            {
                Current = (Current + 1) % Count;
                return Current;
            }

            if (_position + 1 >= _order.Count)
            {
                Deal(-1);
            }

            _position++;
            Current = _order[_position];
            return Current;
        }

        /// <summary>
        /// Previous, <paramref name="elapsed"/> seconds into the current track:
        /// past <see cref="RestartSeconds"/> it stays on the track, which the
        /// player then starts over, else it steps back. Shuffled, it walks back
        /// through the tracks played; before the first one it stays.
        /// </summary>
        public int Previous(double elapsed)
        {
            if (Count == 0 || elapsed > RestartSeconds)
            {
                return Current;
            }

            if (!Shuffle)
            {
                Current = (Current - 1 + Count) % Count;
                return Current;
            }

            if (_position > 0)
            {
                _position--;
                Current = _order[_position];
            }

            return Current;
        }

        /// <summary>
        /// Appends a shuffled round of every track. With <paramref name="first"/>
        /// set the round opens with it; otherwise it does not open with the
        /// track that closed the last round.
        /// </summary>
        private void Deal(int first)
        {
            var start = _order.Count;
            for (var i = 0; i < Count; i++)
            {
                _order.Add(i);
            }

            // Fisher-Yates over the new round only.
            for (var i = Count - 1; i > 0; i--)
            {
                var j = _random.RangeInclusive(0, i);
                var swap = _order[start + i];
                _order[start + i] = _order[start + j];
                _order[start + j] = swap;
            }

            if (first >= 0)
            {
                MoveToFront(start, first);
            }
            else if (Count > 1 && start > 0 && _order[start] == _order[start - 1])
            {
                var other = start + _random.RangeInclusive(1, Count - 1);
                var swap = _order[start];
                _order[start] = _order[other];
                _order[other] = swap;
            }
        }

        private void MoveToFront(int start, int track)
        {
            for (var i = start; i < _order.Count; i++)
            {
                if (_order[i] == track)
                {
                    _order[i] = _order[start];
                    _order[start] = track;
                    return;
                }
            }
        }
    }
}
