using System;
using System.Security.Cryptography;

namespace BlitzLegacy.Core.Time
{
    /// <summary>
    /// SNTP (RFC 4330) packet helpers. Pure byte work so the parser is
    /// covered by EditMode tests without touching the network.
    /// </summary>
    public static class NetworkTimeProtocol
    {
        public const int PacketSize = 48;

        /// <summary>Seconds between 1900-01-01 (NTP era 0) and 1970-01-01.</summary>
        public const double NtpToUnixSeconds = 2208988800d;

        /// <summary>Client request: LI 0, version 4, mode 3 (client).</summary>
        public static byte[] BuildRequest()
        {
            var packet = new byte[PacketSize];
            packet[0] = (0 << 6) | (4 << 3) | 3;
            // SNTP is unauthenticated. An opaque nonce echoed in the origin
            // timestamp rejects stale/unrelated replies without the device clock.
            var nonce = new byte[8];
            using (var random = RandomNumberGenerator.Create()) { random.GetBytes(nonce); }
            Array.Copy(nonce, 0, packet, 40, nonce.Length);
            return packet;
        }

        /// <summary>
        /// Reads a 64-bit NTP timestamp as unix seconds. Values with the top
        /// bit clear belong to era 1 (after 2036-02-07), as RFC 4330 advises.
        /// </summary>
        public static double ReadTimestamp(byte[] packet, int offset)
        {
            var seconds = ReadUInt32(packet, offset);
            var fraction = ReadUInt32(packet, offset + 4);
            var ntp = seconds + (fraction / 4294967296d);
            if ((seconds & 0x80000000u) == 0)
            {
                ntp += 4294967296d;
            }

            return ntp - NtpToUnixSeconds;
        }

        public static void WriteTimestamp(byte[] packet, int offset, double unixSeconds)
        {
            var ntp = unixSeconds + NtpToUnixSeconds;
            if (ntp >= 4294967296d)
            {
                ntp -= 4294967296d;
            }

            var seconds = (uint)Math.Floor(ntp);
            var fraction = (uint)Math.Min(4294967295d, Math.Floor((ntp - seconds) * 4294967296d));
            WriteUInt32(packet, offset, seconds);
            WriteUInt32(packet, offset + 4, fraction);
        }

        /// <summary>
        /// Validates a server reply and returns the server time at the moment
        /// the reply arrived. <paramref name="roundTripSeconds"/> is the local
        /// monotonic time between send and receive; the server processing time
        /// (transmit - receive) is removed and half the remaining delay added.
        /// </summary>
        public static bool TryParseResponse(
            byte[] packet, int length, double roundTripSeconds, out double unixSeconds)
        {
            unixSeconds = 0d;
            if (packet == null || packet.Length < PacketSize || length < PacketSize || length > packet.Length ||
                double.IsNaN(roundTripSeconds) || double.IsInfinity(roundTripSeconds) ||
                roundTripSeconds < 0d || roundTripSeconds > 10d)
            {
                return false;
            }

            var leap = packet[0] >> 6;
            var version = (packet[0] >> 3) & 0x7;
            var mode = packet[0] & 0x7;
            var stratum = packet[1];
            // Mode 4 = server; leap 3 = clock not synchronised; stratum 0 is a
            // kiss-o'-death packet and 16+ is unsynchronised.
            if ((version != 3 && version != 4) || mode != 4 || leap == 3 || stratum == 0 || stratum > 15)
            {
                return false;
            }

            if (ReadUInt32(packet, 32) == 0 || ReadUInt32(packet, 40) == 0)
            {
                return false;
            }

            var received = ReadTimestamp(packet, 32);
            var transmitted = ReadTimestamp(packet, 40);
            var processing = transmitted - received;
            if (processing < 0d || processing > roundTripSeconds + 0.25d)
            {
                return false;
            }

            var delay = Math.Max(0d, roundTripSeconds - processing);
            unixSeconds = transmitted + (delay * 0.5d);
            // Reject a broken server clock; SNTP does not authenticate its time.
            return unixSeconds > 1704067200d;
        }

        /// <summary>Runtime parser: also require the server to echo our request nonce.</summary>
        public static bool TryParseResponse(
            byte[] packet, int length, double roundTripSeconds, byte[] request, out double unixSeconds)
        {
            unixSeconds = 0d;
            if (request == null || request.Length < PacketSize || packet == null || packet.Length < PacketSize)
                return false;
            for (var i = 0; i < 8; i++)
                if (packet[24 + i] != request[40 + i]) return false;
            return TryParseResponse(packet, length, roundTripSeconds, out unixSeconds);
        }

        private static uint ReadUInt32(byte[] packet, int offset)
        {
            return ((uint)packet[offset] << 24) | ((uint)packet[offset + 1] << 16) |
                ((uint)packet[offset + 2] << 8) | packet[offset + 3];
        }

        private static void WriteUInt32(byte[] packet, int offset, uint value)
        {
            packet[offset] = (byte)(value >> 24);
            packet[offset + 1] = (byte)(value >> 16);
            packet[offset + 2] = (byte)(value >> 8);
            packet[offset + 3] = (byte)value;
        }
    }
}
