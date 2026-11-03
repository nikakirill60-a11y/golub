using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Rng
{
    /// <summary>
    /// The Godot RNG calls the ported gameplay code needs behind one seam:
    /// <c>randi_range()</c> and <c>randf()</c>. Everything else Godot offers
    /// (<c>Array.shuffle()</c>, <c>Array.pick_random()</c>, <c>Util.random_chance()</c>)
    /// is expressed through those two, so an EditMode test can pin a whole
    /// battle roster down with one seed.
    /// </summary>
    public interface IRandomSource
    {
        /// <summary>Godot <c>randi_range(from, to)</c>: both ends included.</summary>
        int RangeInclusive(int from, int to);

        /// <summary>Godot <c>randf()</c>: zero included, one excluded.</summary>
        float Value();
    }

    /// <summary>A reproducible source, used by tests and headless tools.</summary>
    public sealed class SeededRandomSource : IRandomSource
    {
        private readonly Random _random;

        public SeededRandomSource(int seed)
        {
            _random = new Random(seed);
        }

        public int RangeInclusive(int from, int to)
        {
            if (to < from)
            {
                var swap = from;
                from = to;
                to = swap;
            }

            return from + _random.Next(to - from + 1);
        }

        public float Value()
        {
            return (float)_random.NextDouble();
        }
    }

    public static class RandomSourceExtensions
    {
        /// <summary>Godot <c>Util.random_chance(chance)</c>: a percentage against randf().</summary>
        public static bool Chance(this IRandomSource random, float percent)
        {
            if (random == null)
            {
                throw new ArgumentNullException("random");
            }

            return percent * 0.01f > random.Value();
        }

        /// <summary>Godot <c>Array.shuffle()</c>.</summary>
        public static void Shuffle<T>(this IRandomSource random, IList<T> items)
        {
            if (random == null)
            {
                throw new ArgumentNullException("random");
            }

            if (items == null)
            {
                return;
            }

            for (var i = items.Count - 1; i > 0; i--)
            {
                var j = random.RangeInclusive(0, i);
                var swap = items[i];
                items[i] = items[j];
                items[j] = swap;
            }
        }

        /// <summary>Godot <c>Array.pick_random()</c>: the default value on an empty array.</summary>
        public static T PickRandom<T>(this IRandomSource random, IReadOnlyList<T> items)
        {
            if (random == null)
            {
                throw new ArgumentNullException("random");
            }

            if (items == null || items.Count == 0)
            {
                return default(T);
            }

            return items[random.RangeInclusive(0, items.Count - 1)];
        }
    }
}
