using System.Collections.Generic;
using UnityEngine;

namespace BlitzLegacy.Network
{
    /// <summary>
    /// Host-side sanity limits on remote input. Honest clients only ever send
    /// axes in [-1, 1] and a finite aim point, so anything else is a modified
    /// client; it is clamped to a legal value and counted, never applied.
    /// </summary>
    public static class NetInputGuard
    {
        public const float MaxAimDistance = 5000f;
        private static readonly Dictionary<int, int> Strikes = new Dictionary<int, int>();

        /// <summary>Hard strikes after which the host kicks and bans the player from the battle.</summary>
        public const int KickStrikes = 8;

        /// <summary>Returns false when the input had to be corrected.</summary>
        public static bool Sanitize(ref NetInput input)
        {
            bool hard;
            return Sanitize(ref input, out hard);
        }

        /// <summary>
        /// <paramref name="hard"/>: an axis outside [-1, 1] or non-finite,
        /// which no honest client can produce. A far or broken aim point alone
        /// is only clamped, since sky aim on a big map could reach it.
        /// </summary>
        public static bool Sanitize(ref NetInput input, out bool hard)
        {
            var axes = true;
            input.Forward = Axis(input.Forward, ref axes);
            input.Right = Axis(input.Right, ref axes);
            hard = !axes;
            var ok = axes;
            var a = input.Aim;
            if (!(IsFinite(a.x) && IsFinite(a.y) && IsFinite(a.z)) || a.sqrMagnitude > MaxAimDistance * MaxAimDistance)
            {
                input.Aim = Vector3.zero;
                input.Buttons.Set(NetButton.HasAim, false);
                ok = false;
            }
            return ok;
        }

        public static void Report(int playerId)
        {
            Count(Strikes, playerId);
        }

        /// <summary>Counts a violation; true once a player reaches <see cref="KickStrikes"/> hard ones.</summary>
        public static bool Report(int playerId, bool hard)
        {
            Count(Strikes, playerId);
            if (!hard) { return false; }
            return Count(HardStrikes, playerId) == KickStrikes;
        }

        private static readonly Dictionary<int, int> HardStrikes = new Dictionary<int, int>();

        private static int Count(Dictionary<int, int> table, int playerId)
        {
            int n; table.TryGetValue(playerId, out n); table[playerId] = ++n;
            if (table != Strikes) { return n; }
            // power-of-two throttle keeps a flooding client from flooding the log
            if ((n & (n - 1)) == 0)
            {
                Debug.LogWarning("[BlitzNet] anti-cheat: illegal input from player " + playerId + " (x" + n + ")");
            }
            return n;
        }

        public static int StrikesOf(int playerId) { int n; return Strikes.TryGetValue(playerId, out n) ? n : 0; }

        public static void Reset() { Strikes.Clear(); HardStrikes.Clear(); }

        private static float Axis(float v, ref bool ok)
        {
            if (!IsFinite(v)) { ok = false; return 0f; }
            if (v > 1f) { ok = false; return 1f; }
            if (v < -1f) { ok = false; return -1f; }
            return v;
        }

        private static bool IsFinite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
    }
}
