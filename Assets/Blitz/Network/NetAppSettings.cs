using System;
using BlitzLegacy.Core;
using Fusion.Photon.Realtime;
using UnityEngine;

namespace BlitzLegacy.Network
{
    /// <summary>
    /// Resolves the Photon App ID without ever committing it. Lookup order:
    /// the <c>BLITZ_PHOTON_APP_ID</c> environment variable, the gitignored
    /// resource <c>BlitzNet/photon_app_id</c> (written by
    /// <c>Tools/photon_app_id.sh</c>), then whatever was typed into
    /// PhotonAppSettings in the editor.
    /// </summary>
    public static class NetAppSettings
    {
        public const string EnvironmentVariable = "BLITZ_PHOTON_APP_ID";
        public const string Resource = "BlitzNet/photon_app_id";

        /// <summary>
        /// Peers only meet peers of the same build: Photon keeps rooms of
        /// different AppVersions apart, so an old client never joins a new host.
        /// </summary>
        public static string Build
        {
            get { return BlitzVersion.Current + "/" + BlitzVersion.Code; }
        }

        public static FusionAppSettings Resolve()
        {
            if (!PhotonAppSettings.TryGetGlobal(out var global) || global == null)
            {
                Debug.LogError("[BlitzNet] PhotonAppSettings asset is missing");
                return null;
            }
            var settings = global.AppSettings.GetCopy();
            var id = FromEnvironment() ?? FromResource() ?? settings.AppIdFusion;
            if (!IsValidAppId(id))
            {
                Debug.LogError("[BlitzNet] no Photon Fusion App ID: set " + EnvironmentVariable
                    + " or run Tools/photon_app_id.sh <app-id>");
                return null;
            }
            settings.AppIdFusion = id.Trim();
            settings.AppVersion = Build;
            return settings;
        }

        /// <summary>Quiet check used to decide whether online play is offered at all.</summary>
        public static bool HasAppId()
        {
            var id = FromEnvironment() ?? FromResource();
            if (id == null && PhotonAppSettings.TryGetGlobal(out var global) && global != null)
            {
                id = global.AppSettings.AppIdFusion;
            }
            return IsValidAppId(id);
        }

        public static bool IsValidAppId(string id)
        {
            Guid parsed;
            return !string.IsNullOrEmpty(id) && Guid.TryParse(id.Trim(), out parsed);
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
