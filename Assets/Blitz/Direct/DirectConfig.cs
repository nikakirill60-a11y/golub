using System;
using BlitzLegacy.Core;
using UnityEngine;

namespace BlitzLegacy.Direct
{
    /// <summary>
    /// Where the dedicated server lives and which build may meet it. Lookup
    /// order of the address: the <c>-blitzServerAddress host[:port]</c> command
    /// line switch, the <c>BLITZ_DIRECT_SERVER</c> environment variable, then
    /// the committed resource <c>BlitzDirect/server</c>.
    /// </summary>
    public static class DirectConfig
    {
        public const string EnvironmentVariable = "BLITZ_DIRECT_SERVER";
        public const string Resource = "BlitzDirect/server";
        public const string AddressSwitch = "-blitzServerAddress";

        /// <summary>Peers only meet peers of the same build, exactly as on Photon.</summary>
        public static string Build
        {
            get { return BlitzVersion.Current + "/" + BlitzVersion.Code; }
        }

        /// <summary>The configured server, or false when none is set.</summary>
        public static bool TryGetServer(out string host, out int port)
        {
            var value = FromCommandLine() ?? FromEnvironment() ?? FromResource();
            return TryParse(value, out host, out port);
        }

        public static bool HasServer()
        {
            string host;
            int port;
            return TryGetServer(out host, out port);
        }

        /// <summary>"host", "host:port" or "[v6]:port"; the port defaults to <see cref="DirectProtocol.DefaultPort"/>.</summary>
        public static bool TryParse(string value, out string host, out int port)
        {
            host = null;
            port = DirectProtocol.DefaultPort;
            if (string.IsNullOrWhiteSpace(value)) { return false; }
            value = value.Trim();
            var portText = (string)null;
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                var close = value.IndexOf(']');
                if (close < 0) { return false; }
                host = value.Substring(1, close - 1);
                if (close + 1 < value.Length)
                {
                    if (value[close + 1] != ':') { return false; }
                    portText = value.Substring(close + 2);
                }
            }
            else
            {
                var colon = value.LastIndexOf(':');
                if (colon >= 0 && value.IndexOf(':') == colon)
                {
                    host = value.Substring(0, colon);
                    portText = value.Substring(colon + 1);
                }
                else
                {
                    host = value;
                }
            }
            if (string.IsNullOrEmpty(host)) { return false; }
            if (portText != null)
            {
                int parsed;
                if (!int.TryParse(portText, out parsed) || parsed <= 0 || parsed > 65535) { return false; }
                port = parsed;
            }
            return true;
        }

        private static string FromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == AddressSwitch) { return args[i + 1]; }
            }
            return null;
        }

        private static string FromEnvironment()
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static string FromResource()
        {
            var asset = Resources.Load<TextAsset>(Resource);
            if (asset == null) { return null; }
            var value = asset.text;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
