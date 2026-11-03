using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BlitzLegacy.Core.Security;
using BlitzLegacy.Runtime.Telemetry;
using UnityEngine;

namespace BlitzLegacy.Runtime.Security
{
    /// <summary>
    /// What happens once an obscured value's decoy was edited (GameGuardian,
    /// Cheat Engine): the decoy is only ever written by a memory editor, so a
    /// mismatch is proof rather than a heuristic.
    ///
    /// In order: ban this device from online play, delete the account, report
    /// a Sentry issue naming the player and the edited value, leave the online
    /// battle (as host, after pushing a fresh migration snapshot so another
    /// player takes the host over), then crash the process.
    ///
    /// The editor only logs: a crash there would kill Unity itself.
    /// </summary>
    public sealed class AntiCheatResponder : MonoBehaviour
    {
        /// <summary>
        /// The network layer's exit, set by BlitzLegacy.Network (Runtime cannot
        /// reference it). Must finish quickly; it is awaited with a timeout.
        /// </summary>
        public static Func<Task> LeaveOnline;

        private const float LeaveTimeoutSeconds = 3f;
        private const float SentryFlushSeconds = 3f;

        private static AntiCheatResponder _instance;
        private static readonly object Gate = new object();
        private static string _pending;
        private static bool _handled;

        /// <summary>The first value caught, once the response started.</summary>
        public static string Caught { get; private set; }

        public static void Attach(Transform parent)
        {
            if (_instance != null) { return; }
            var go = new GameObject("AntiCheat");
            go.transform.SetParent(parent, false);
            _instance = go.AddComponent<AntiCheatResponder>();
            AntiTamper.Detected -= OnDetected;
            AntiTamper.Detected += OnDetected;
        }

        private static void OnDetected(string what)
        {
            // Raised from inside a property getter, possibly mid-simulation:
            // only note it here and respond on the next frame.
            lock (Gate)
            {
                if (_pending == null && !_handled) { _pending = string.IsNullOrEmpty(what) ? "unknown" : what; }
            }
        }

        private void Update()
        {
            string what;
            lock (Gate)
            {
                if (_pending == null || _handled) { return; }
                what = _pending;
                _handled = true;
            }
            StartCoroutine(Respond(what));
        }

        private IEnumerator Respond(string what)
        {
            Caught = what;
            var bootstrap = BlitzBootstrap.Current;
            var account = bootstrap != null && bootstrap.Accounts != null ? bootstrap.Accounts.GetCurrentAccount() : null;
            var nick = account != null && !string.IsNullOrEmpty(account.Login) ? account.Login : "<no account>";
            var details = new Dictionary<string, string>
            {
                { "value", what },
                { "device_hash", AntiCheatBan.DeviceHash },
                { "hits", AntiTamper.Hits.ToString() },
                { "tank", account != null ? account.SelectedTankName : string.Empty },
                { "in_battle", (bootstrap != null && bootstrap.Battle != null) ? "true" : "false" },
                { "online", LeaveOnline != null ? "available" : "none" },
            };

            Debug.LogError("[Blitz] anti-cheat: '" + nick + "' edited " + what);
            if (Application.isEditor) { yield break; }

            // Ban and wipe first: if the cheater kills the app now, both stick.
            AntiCheatBan.Record(what);
            WipeAccount(bootstrap, account);
            BlitzTelemetry.ReportCheat(nick, what, details);

            if (LeaveOnline != null)
            {
                Task leave = null;
                try { leave = LeaveOnline(); }
                catch (Exception e) { Debug.LogWarning("[Blitz] anti-cheat: leave failed: " + e.Message); }
                var until = Time.realtimeSinceStartup + LeaveTimeoutSeconds;
                while (leave != null && !leave.IsCompleted && Time.realtimeSinceStartup < until) { yield return null; }
            }

            BlitzTelemetry.Flush(SentryFlushSeconds);
            BlitzTelemetry.Close();
            UnityEngine.Diagnostics.Utils.ForceCrash(UnityEngine.Diagnostics.ForcedCrashCategory.Abort);
            Application.Quit(1); // unreachable unless ForceCrash is unsupported
        }

        private static void WipeAccount(BlitzBootstrap bootstrap, Core.Save.AccountData account)
        {
            if (bootstrap == null || bootstrap.Accounts == null || account == null) { return; }
            try
            {
                bootstrap.Accounts.DeleteAccount(account.Key);
                bootstrap.Saves.SaveBook(bootstrap.Accounts);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Blitz] anti-cheat: account wipe failed: " + e.Message);
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                AntiTamper.Detected -= OnDetected;
                _instance = null;
            }
        }
    }
}
