using System;

namespace BlitzLegacy.Direct
{
    /// <summary>
    /// Client playback clock for the server's snapshots: which server time a
    /// frame shows. The server clock is the fastest packet of the last two
    /// seconds (a server hitch or drift is forgotten within that window, where
    /// an all-time maximum kept the playback ahead of the data and the hull
    /// only moved when a snapshot landed). The delay follows the measured
    /// arrival jitter, so a jittery mobile or VPN link plays a little later
    /// instead of freezing between snapshots. The playback only ever speeds up
    /// or slows down by a bounded amount; it jumps only on a large error.
    /// </summary>
    public sealed class DirectTimeline
    {
        public const double BucketSeconds = 0.5;
        public const int Buckets = 4;
        public const double MinDelay = 0.06;
        public const double MaxDelay = 0.35;
        /// <summary>Delay on top of the jitter: one snapshot interval plus the server's send slack.</summary>
        public const double DelayMargin = 1.0 / DirectProtocol.SnapshotRate + 0.015;
        /// <summary>Errors above this jump the playback instead of slewing it.</summary>
        public const double SnapError = 0.3;
        /// <summary>Largest playback speed change while catching up (fraction of real time).</summary>
        public const double MaxSlew = 0.15;
        /// <summary>A packet this much behind the estimate is a new server clock (restart).</summary>
        public const double ResetError = 1.0;

        private readonly double[] _fast = new double[Buckets];
        private readonly double[] _late = new double[Buckets];
        private readonly bool[] _used = new bool[Buckets];
        private int _bucket;
        private double _bucketStart;
        private double _lastFast;
        private bool _hasClock;
        private bool _hasRender;
        private double _render;
        private double _lastLocal;
        private double _delay = 0.1;

        public bool HasClock { get { return _hasClock; } }

        /// <summary>Current playback delay behind the fastest packet (seconds).</summary>
        public double Delay { get { return _delay; } }

        /// <summary>Worst lateness of a packet against the fastest one in the window (seconds).</summary>
        public double Jitter { get; private set; }

        /// <summary>How many times the playback jumped instead of slewing.</summary>
        public int Snaps { get; private set; }

        /// <summary>The estimated server time right now (fastest path).</summary>
        public double ServerNow(double localTime) { return localTime + Fast(); }

        public void Reset()
        {
            _hasClock = false;
            _hasRender = false;
            _delay = 0.1;
            Jitter = 0;
            for (var i = 0; i < Buckets; i++) { _used[i] = false; _fast[i] = 0; _late[i] = 0; }
        }

        /// <summary>One packet stamped with the server time, received at the local time.</summary>
        public void OnPacket(double serverTime, double localTime)
        {
            var sample = serverTime - localTime;
            if (_hasClock && sample < Fast() - ResetError) { Reset(); }
            if (!_hasClock)
            {
                _hasClock = true;
                _bucket = 0;
                _bucketStart = localTime;
                _used[0] = true;
                _fast[0] = sample;
                _late[0] = 0;
                _lastFast = sample;
                return;
            }
            Roll(localTime);
            var fast = Math.Max(Fast(), sample);
            if (!_used[_bucket] || sample > _fast[_bucket])
            {
                _fast[_bucket] = sample;
                _used[_bucket] = true;
            }
            _lastFast = fast;
            var late = fast - sample;
            if (late > _late[_bucket]) { _late[_bucket] = late; }
            Jitter = MaxLate();
        }

        /// <summary>The server time to show this frame (call once per frame).</summary>
        public double Advance(double localTime)
        {
            if (!_hasClock) { return 0; }
            Roll(localTime);
            Jitter = MaxLate();
            var dt = _hasRender ? Math.Max(0, localTime - _lastLocal) : 0;
            _lastLocal = localTime;

            var wanted = Clamp(Jitter + DelayMargin, MinDelay, MaxDelay);
            // A worse link is followed at once (the slew below spreads it),
            // a calmer one only slowly, so one quiet second does not undo it.
            _delay = wanted > _delay ? wanted : Math.Max(wanted, _delay - dt * 0.05);

            var target = localTime + Fast() - _delay;
            if (!_hasRender || Math.Abs(target - (_render + dt)) > SnapError)
            {
                if (_hasRender) { Snaps++; }
                _hasRender = true;
                _render = target;
                return _render;
            }
            _render += dt;
            var error = target - _render;
            var step = error * Math.Min(1.0, 3.0 * dt);
            _render += Clamp(step, -MaxSlew * dt, MaxSlew * dt);
            return _render;
        }

        private double Fast()
        {
            var found = false;
            var best = 0.0;
            for (var i = 0; i < Buckets; i++)
            {
                if (!_used[i]) { continue; }
                if (!found || _fast[i] > best) { best = _fast[i]; found = true; }
            }
            return found ? best : _lastFast;
        }

        private double MaxLate()
        {
            var worst = 0.0;
            for (var i = 0; i < Buckets; i++)
            {
                if (_used[i] && _late[i] > worst) { worst = _late[i]; }
            }
            return worst;
        }

        private void Roll(double localTime)
        {
            var steps = 0;
            while (localTime - _bucketStart >= BucketSeconds)
            {
                if (++steps > Buckets) { _bucketStart = localTime; break; }
                _bucket = (_bucket + 1) % Buckets;
                _bucketStart += BucketSeconds;
                _used[_bucket] = false;
                _late[_bucket] = 0;
            }
            if (steps > Buckets)
            {
                // Silence longer than the window: start a fresh one, keep the last estimate.
                for (var i = 0; i < Buckets; i++) { _used[i] = false; _late[i] = 0; }
            }
        }

        private static double Clamp(double v, double min, double max)
        {
            return v < min ? min : (v > max ? max : v);
        }
    }
}
