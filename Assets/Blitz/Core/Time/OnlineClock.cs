using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace BlitzLegacy.Core.Time
{
    /// <summary>Asks one time server for the current unix time.</summary>
    public interface INetworkTimeSource
    {
        bool TryQuery(string host, int timeoutMs, out double unixSeconds);
    }

    /// <summary>SNTP over UDP port 123.</summary>
    public sealed class NtpTimeSource : INetworkTimeSource
    {
        public const int Port = 123;

        public bool TryQuery(string host, int timeoutMs, out double unixSeconds)
        {
            unixSeconds = 0d;
            try
            {
                var budget = Stopwatch.StartNew();
                var lookup = Dns.GetHostAddressesAsync(host);
                if (!lookup.Wait(timeoutMs)) return false;
                var addresses = lookup.Result;
                for (var i = 0; i < addresses.Length; i++)
                {
                    if (addresses[i].AddressFamily != AddressFamily.InterNetwork &&
                        addresses[i].AddressFamily != AddressFamily.InterNetworkV6)
                    {
                        continue;
                    }

                    var remaining = timeoutMs - (int)budget.ElapsedMilliseconds;
                    if (remaining <= 0) return false;
                    if (QueryAddress(addresses[i], remaining, out unixSeconds))
                    {
                        return true;
                    }
                }
            }
            catch (SocketException)
            {
            }
            catch (ArgumentException)
            {
            }
            catch (AggregateException)
            {
            }

            return false;
        }

        private static bool QueryAddress(IPAddress address, int timeoutMs, out double unixSeconds)
        {
            unixSeconds = 0d;
            using (var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.ReceiveTimeout = timeoutMs;
                socket.SendTimeout = timeoutMs;
                var request = NetworkTimeProtocol.BuildRequest();
                var reply = new byte[NetworkTimeProtocol.PacketSize * 2];
                try
                {
                    socket.Connect(new IPEndPoint(address, Port));
                    var watch = Stopwatch.StartNew();
                    socket.Send(request);
                    var length = socket.Receive(reply);
                    watch.Stop();
                    return NetworkTimeProtocol.TryParseResponse(
                        reply, length, watch.Elapsed.TotalSeconds, request, out unixSeconds);
                }
                catch (SocketException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }
    }

    public enum OnlineClockState
    {
        Unknown = 0,
        Syncing = 1,
        Online = 2,
        Offline = 3,
    }

    /// <summary>
    /// Trusted wall clock for online-only features (the weekly shop). The
    /// time comes from network time servers; between syncs it advances with
    /// a monotonic stopwatch, so changing the device clock has no effect.
    /// Without a successful sync there is no time at all and callers must
    /// treat the feature as offline.
    /// </summary>
    public sealed class OnlineClock
    {
        public static readonly string[] DefaultHosts =
        {
            "time.google.com",
            "time.cloudflare.com",
            "pool.ntp.org",
            "time.windows.com",
        };

        /// <summary>A sync older than this is refreshed when the shop opens.</summary>
        public const double RefreshAfterSeconds = 60d;

        /// <summary>A sync older than this no longer counts as online.</summary>
        public const double ExpireAfterSeconds = 120d;

        public const int TimeoutMs = 2500;

        private readonly object _gate = new object();
        private readonly INetworkTimeSource _source;
        private readonly IList<string> _hosts;
        private readonly Func<double> _monotonic;
        private OnlineClockState _state;
        private double _syncedUnix;
        private double _syncedAt;
        private int _generation;
        private bool _syncRunning;

        public OnlineClock(INetworkTimeSource source, IList<string> hosts = null, Func<double> monotonicSeconds = null)
        {
            _source = source ?? new NtpTimeSource();
            _hosts = hosts ?? DefaultHosts;
            _monotonic = monotonicSeconds ?? StopwatchSeconds;
        }

        public OnlineClockState State
        {
            get
            {
                lock (_gate)
                {
                    if (_state == OnlineClockState.Online && _monotonic() - _syncedAt > ExpireAfterSeconds)
                    {
                        return OnlineClockState.Offline;
                    }

                    return _state;
                }
            }
        }

        public bool IsOnline
        {
            get { return State == OnlineClockState.Online; }
        }

        public bool IsSyncing
        {
            get
            {
                lock (_gate)
                {
                    return _state == OnlineClockState.Syncing;
                }
            }
        }

        /// <summary>Trusted unix time, or false when not synced recently.</summary>
        public bool TryGetNow(out double unixSeconds)
        {
            lock (_gate)
            {
                unixSeconds = 0d;
                var age = _monotonic() - _syncedAt;
                // A failed refresh means the network is gone: offline-only
                // callers lose the clock even if the last sync is recent.
                var usable = _state == OnlineClockState.Online;
                if (!usable || _syncedUnix <= 0d || age > ExpireAfterSeconds)
                {
                    return false;
                }

                unixSeconds = _syncedUnix + age;
                return true;
            }
        }

        public bool NeedsRefresh
        {
            get
            {
                lock (_gate)
                {
                    return _syncedUnix <= 0d || _monotonic() - _syncedAt > RefreshAfterSeconds;
                }
            }
        }

        /// <summary>Starts a background sync unless one is already running.</summary>
        public void RequestSync(bool force = false)
        {
            int generation;
            lock (_gate)
            {
                if (_syncRunning)
                {
                    return;
                }

                if (!force && _state == OnlineClockState.Online &&
                    _monotonic() - _syncedAt <= RefreshAfterSeconds)
                {
                    return;
                }

                _syncRunning = true;
                _state = OnlineClockState.Syncing;
                generation = ++_generation;
            }

            Task.Run(() => SyncNow(generation));
        }

        /// <summary>Blocking sync on the calling thread; tests and tools use it.</summary>
        public bool SyncBlocking()
        {
            int generation;
            lock (_gate)
            {
                if (_syncRunning) return false;
                _syncRunning = true;
                _state = OnlineClockState.Syncing;
                generation = ++_generation;
            }

            return SyncNow(generation);
        }

        /// <summary>Feeds a known server time, for tests and editor renders.</summary>
        public void SetSynced(double unixSeconds)
        {
            if (double.IsNaN(unixSeconds) || double.IsInfinity(unixSeconds) || unixSeconds <= 0d)
                throw new ArgumentOutOfRangeException("unixSeconds");
            lock (_gate)
            {
                _generation++;
                _syncedUnix = unixSeconds;
                _syncedAt = _monotonic();
                _state = OnlineClockState.Online;
            }
        }

        public void SetOffline()
        {
            lock (_gate)
            {
                _generation++;
                _syncedUnix = 0d;
                _state = OnlineClockState.Offline;
            }
        }

        private bool SyncNow(int generation)
        {
            double unix = 0d;
            var ok = false;
            try
            {
                for (var i = 0; i < _hosts.Count && !ok; i++)
                {
                    try
                    {
                        ok = _source.TryQuery(_hosts[i], TimeoutMs, out unix) &&
                            !double.IsNaN(unix) && !double.IsInfinity(unix) && unix > 0d;
                    }
                    catch (Exception) { ok = false; }
                }
            }
            catch (Exception)
            {
                ok = false;
            }

            var at = _monotonic();
            lock (_gate)
            {
                _syncRunning = false;
                if (generation != _generation)
                {
                    return false;
                }

                if (ok)
                {
                    _syncedUnix = unix;
                    _syncedAt = at;
                    _state = OnlineClockState.Online;
                }
                else
                {
                    _syncedUnix = 0d;
                    _state = OnlineClockState.Offline;
                }
            }

            return ok;
        }

        private static double StopwatchSeconds()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }
    }
}
