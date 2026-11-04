using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// Pure port of the queue counters embedded in ui_hangar.tscn.  It owns no
    /// clock so the runtime can drive the exact five-second cadence and tests
    /// can supply deterministic random functions.
    /// </summary>
    public sealed class BattleQueueModel
    {
        public const float RefreshIntervalSeconds = 5f;
        public const float MatchDelayMinSeconds = 1f;
        public const float MatchDelayMaxSeconds = 3f;
        public const int TierRandomMin = 134;
        public const int TierRandomMax = 354;
        public const float TypeShareMin = 0.05f;
        public const float TypeShareMax = 0.25f;

        private static readonly int[] SourceRatios = { 3, 9, 6, 12, 4, 5, 7, 8, 12, 9, 2 };
        private readonly int[] _tiers = new int[TanksDatabase.MaxLevel];
        private readonly int[] _types = new int[4];

        public IReadOnlyList<int> Tiers { get { return _tiers; } }
        public IReadOnlyList<int> Types { get { return _types; } }
        public int TotalPlayers { get; private set; }

        public static IReadOnlyList<int> TierRatios { get { return SourceRatios; } }

        /// <summary>
        /// update_players(): each tier is floor(randint(134,354) * ratio / 100),
        /// then HT/MT/LT each take 5..25 percent and TD receives the remainder.
        /// </summary>
        public void Refresh(
            Func<int, int, int> randomInclusive,
            Func<float, float, float> randomFloat)
        {
            if (randomInclusive == null)
            {
                throw new ArgumentNullException(nameof(randomInclusive));
            }
            if (randomFloat == null)
            {
                throw new ArgumentNullException(nameof(randomFloat));
            }

            TotalPlayers = 0;
            for (var index = 0; index < _tiers.Length; index++)
            {
                var roll = Clamp(randomInclusive(TierRandomMin, TierRandomMax),
                    TierRandomMin, TierRandomMax);
                _tiers[index] = roll * SourceRatios[index] / 100;
                TotalPlayers += _tiers[index];
            }

            var used = 0;
            for (var index = 0; index < 3; index++)
            {
                var share = Clamp(randomFloat(TypeShareMin, TypeShareMax),
                    TypeShareMin, TypeShareMax);
                _types[index] = (int)(TotalPlayers * share);
                used += _types[index];
            }
            _types[3] = Math.Max(0, TotalPlayers - used);
        }

        public static string FormatElapsed(float elapsedSeconds)
        {
            var total = Math.Max(0, (int)elapsedSeconds);
            return (total / 60).ToString() + ":" + (total % 60).ToString("00");
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        private static float Clamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
