using BlitzLegacy.Core.Rng;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Live Godot-style random source backed by UnityEngine.Random.</summary>
    public sealed class UnityRandomSource : IRandomSource
    {
        public int RangeInclusive(int from, int to)
        {
            if (to < from)
            {
                var swap = from;
                from = to;
                to = swap;
            }
            return Random.Range(from, to + 1);
        }

        public float Value()
        {
            return Random.value;
        }
    }
}
