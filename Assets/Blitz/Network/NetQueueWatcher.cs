using System;
using System.Collections.Generic;
using System.Reflection;
using BlitzLegacy.Core.Battle;
using Fusion;
using Fusion.Matchmaking;
using Fusion.Photon.Realtime;
using Photon.Realtime;
using UnityEngine;

namespace BlitzLegacy.Network
{
    /// <summary>
    /// Live view of the shared matchmaking queue. A Fusion runner inside a room
    /// no longer receives the lobby list (and the project runs a single peer),
    /// so this keeps a second, lobby-only Photon Realtime connection that just
    /// listens to the open battles of this build and sums them up
    /// (<see cref="OnlineQueue.Aggregate"/>). It never joins a room. It costs
    /// one extra CCU per searching player and is stopped as soon as the
    /// player enters the battle loading screen.
    /// </summary>
    public sealed class NetQueueWatcher : MonoBehaviour, IConnectionCallbacks, ILobbyCallbacks
    {
        /// <summary>Lobby make-up property, shared with <see cref="NetSession"/>.</summary>
        public const string MixProperty = "x";
        public const string TierProperty = "t";
        public const string ModeProperty = "m";
        public const string StartsInProperty = "s";

        private const float RetrySeconds = 10f;
        /// <summary>How long a dropped client is still serviced so its disconnect completes.</summary>
        private const float DrainSeconds = 5f;
        private const string FallbackLobby = "ClientServer";

        /// <summary>Raised on the main thread whenever the lobby list changes.</summary>
        public event Action<OnlineQueue.Snapshot> Changed;

        public OnlineQueue.Snapshot Last { get; private set; }

        /// <summary>True while the lobby list is live, so <see cref="Battles"/> can be trusted.</summary>
        public bool InLobby { get; private set; }

        /// <summary>The open battles of this mode as matchmaking judges them (capacity left 0).</summary>
        public IReadOnlyCollection<OnlineBalancer.Room> Battles { get { return _battles.Values; } }

        private RealtimeClient _client;
        private TypedLobby _lobby;
        private int _mode;
        private string _region;
        private float _retryAt = -1f;
        private bool _stopped;
        private readonly Dictionary<string, OnlineQueue.Room> _rooms = new Dictionary<string, OnlineQueue.Room>();
        private readonly Dictionary<string, OnlineBalancer.Room> _battles = new Dictionary<string, OnlineBalancer.Room>();

        // Clients told to disconnect. Photon only finishes a disconnect inside
        // Service(); a client nobody services any more leaves its socket tasks
        // to fail on their own, which surfaced as unobserved
        // WebSocketException / DisconnectException after a cancelled queue.
        private struct Draining { public RealtimeClient Client; public float Until; }
        private readonly List<Draining> _draining = new List<Draining>();

        public static NetQueueWatcher Start(Transform parent, BattleMode mode, string region)
        {
            var go = new GameObject("NetQueueWatcher");
            go.transform.SetParent(parent, false);
            var watcher = go.AddComponent<NetQueueWatcher>();
            watcher._mode = (int)mode;
            watcher._region = string.IsNullOrWhiteSpace(region) ? null : region.Trim();
            watcher._lobby = ClientServerLobby();
            watcher.Connect();
            return watcher;
        }

        /// <summary>
        /// The lobby Fusion publishes ClientServer sessions in. Read from
        /// Fusion itself so both always match; Fusion is not obfuscated.
        /// </summary>
        private static TypedLobby ClientServerLobby()
        {
            try
            {
                var type = typeof(NetworkRunner).Assembly.GetType("Fusion.CloudServicesMetadata");
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var field = type != null ? type.GetField("LobbyClientServer", flags) : null;
                object value = field != null ? field.GetValue(null) : null;
                if (value == null && type != null)
                {
                    var property = type.GetProperty("LobbyClientServer", flags);
                    value = property != null ? property.GetValue(null, null) : null;
                }
                var lobby = value as TypedLobby;
                if (lobby != null) { return lobby; }
                var name = value as string;
                if (!string.IsNullOrEmpty(name)) { return new TypedLobby(name, LobbyType.Default); }
            }
            catch (Exception error)
            {
                Debug.LogWarning("[BlitzNet] queue: lobby name lookup failed: " + error.Message);
            }
            return new TypedLobby(FallbackLobby, LobbyType.Default);
        }

        private void Connect()
        {
            if (_stopped) { return; }
            _retryAt = -1f;
            var settings = NetAppSettings.Resolve();
            if (settings == null) { return; }
            if (!string.IsNullOrEmpty(_region)) { settings.FixedRegion = _region; }
            try
            {
                _client = MatchmakingArgumentsExtensions.BuildRealtimeClient(settings);
                _client.AddCallbackTarget(this);
                // BuildRealtimeClient sets the Fusion client type and plugin, so
                // the Fusion App ID and AppVersion (the build) are used as is.
                var app = settings;
                Debug.Log("[BlitzNet] queue: watching lobby '" + _lobby + "' region=" + (_region ?? "best"));
                if (!_client.ConnectUsingSettings(app)) { Retry("connect refused"); }
            }
            catch (Exception error)
            {
                Debug.LogWarning("[BlitzNet] queue: " + error.Message);
                Retry("exception");
            }
        }

        private void Retry(string reason)
        {
            Drop();
            if (_stopped) { return; }
            Debug.Log("[BlitzNet] queue: watcher offline (" + reason + "), retry in " + RetrySeconds + "s");
            _retryAt = Time.realtimeSinceStartup + RetrySeconds;
        }

        private void Drop()
        {
            InLobby = false;
            var client = _client;
            _client = null;
            if (client == null) { return; }
            client.RemoveCallbackTarget(this);
            try
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                    _draining.Add(new Draining { Client = client, Until = Time.realtimeSinceStartup + DrainSeconds });
                }
            }
            catch (Exception error) { Debug.LogWarning("[BlitzNet] queue: disconnect: " + error.Message); }
        }

        /// <summary>Leaves the lobby for good (battle loading screen, cancel, failure).</summary>
        public void Stop()
        {
            if (_stopped) { return; }
            _stopped = true;
            Changed = null;
            Drop();
            if (this == null) { return; }
            if (_draining.Count == 0) { Destroy(gameObject); return; }
            // Outlive the session that owns this object until the disconnect
            // is through; Update destroys it once nothing is left to drain.
            transform.SetParent(null, false);
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            ServiceDraining();
            if (_client != null) { Service(_client); }
            else if (_stopped) { if (_draining.Count == 0) { Destroy(gameObject); } }
            else if (_retryAt > 0f && Time.realtimeSinceStartup >= _retryAt) { Connect(); }
        }

        private static void Service(RealtimeClient client)
        {
            try { client.Service(); }
            catch (Exception error) { Debug.LogWarning("[BlitzNet] queue: service: " + error.Message); }
        }

        private void ServiceDraining()
        {
            var now = Time.realtimeSinceStartup;
            for (var i = _draining.Count - 1; i >= 0; i--)
            {
                var item = _draining[i];
                Service(item.Client);
                if (!item.Client.IsConnected || now >= item.Until) { _draining.RemoveAt(i); }
            }
        }

        private void OnDestroy()
        {
            _stopped = true;
            Drop();
            _draining.Clear();
        }

        private void Publish()
        {
            Last = OnlineQueue.Aggregate(_rooms.Values);
            var handler = Changed;
            if (handler != null) { handler(Last); }
        }

        // ----- Photon Realtime callbacks ----------------------------------------

        public void OnConnected() { }

        public void OnConnectedToMaster()
        {
            if (_client == null) { return; }
            if (!_client.OpJoinLobby(_lobby)) { Retry("join lobby refused"); }
        }

        public void OnDisconnected(DisconnectCause cause)
        {
            if (_stopped || _client == null) { return; }
            InLobby = false;
            _rooms.Clear();
            _battles.Clear();
            Publish();
            Retry(cause.ToString());
        }

        public void OnRegionListReceived(RegionHandler regionHandler) { }
        public void OnCustomAuthenticationResponse(Dictionary<string, object> data) { }
        public void OnCustomAuthenticationFailed(string debugMessage) { }

        public void OnJoinedLobby()
        {
            _rooms.Clear();
            _battles.Clear();
            InLobby = true;
            Debug.Log("[BlitzNet] queue: in lobby");
        }

        public void OnLeftLobby() { InLobby = false; }

        public void OnRoomListUpdate(List<RoomInfo> roomList)
        {
            // Photon sends deltas: changed rooms, and removed ones flagged.
            for (var i = 0; roomList != null && i < roomList.Count; i++)
            {
                var info = roomList[i];
                if (info == null || string.IsNullOrEmpty(info.Name)) { continue; }
                if (info.RemovedFromList || !info.IsOpen || !info.IsVisible ||
                    ReadInt(info, ModeProperty, (int)BattleMode.Normal) != _mode)
                {
                    _rooms.Remove(info.Name);
                    _battles.Remove(info.Name);
                    continue;
                }
                _rooms[info.Name] = new OnlineQueue.Room
                {
                    Players = info.PlayerCount,
                    Tier = ReadInt(info, TierProperty, 0),
                    Mix = ReadString(info, MixProperty),
                };
                _battles[info.Name] = new OnlineBalancer.Room
                {
                    Name = info.Name,
                    Tier = ReadInt(info, TierProperty, 0),
                    Players = info.PlayerCount,
                    StartsIn = ReadInt(info, StartsInProperty, -1),
                };
            }
            Publish();
        }

        public void OnLobbyStatisticsUpdate(List<TypedLobbyInfo> lobbyStatistics) { }

        // ----- untrusted room properties ---------------------------------------

        private static object Read(RoomInfo info, string key)
        {
            var properties = info.CustomProperties;
            if (properties == null || !properties.ContainsKey(key)) { return null; }
            return properties[key];
        }

        private static int ReadInt(RoomInfo info, string key, int fallback)
        {
            var value = Read(info, key);
            if (value is int) { return (int)value; }
            if (value is long) { var l = (long)value; return l >= int.MinValue && l <= int.MaxValue ? (int)l : fallback; }
            if (value is short) { return (short)value; }
            if (value is byte) { return (byte)value; }
            return fallback;
        }

        private static string ReadString(RoomInfo info, string key)
        {
            var value = Read(info, key) as string;
            return value != null && value.Length <= 64 ? value : null;
        }
    }
}
