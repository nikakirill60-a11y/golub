using System;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Runtime.Security;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Watches every networked battle for the Sandbox and for mod tanks. The
    /// plan and the connect request already refuse both on the wire; this is
    /// the last line for a tampered build that flips the mode, activates the
    /// Sandbox rules or swaps a mod tank in after the battle was built. The
    /// honest side never plays on: it disconnects itself through the anti-cheat
    /// leave hook and ends the battle locally.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SandboxNetGuard : MonoBehaviour
    {
        public const float BannerSeconds = 6f;

        private BattleSession _session;
        private bool _tripped;
        private float _hideBannerAt = -1f;

        /// <summary>Raised once when the guard trips (tests, telemetry).</summary>
        public static event Action<BattleSession> Tripped;

        public static SandboxNetGuard Attach(BattleSession session)
        {
            if (session == null || session.Net == null) { return null; }
            var guard = session.gameObject.GetComponent<SandboxNetGuard>();
            if (guard == null) { guard = session.gameObject.AddComponent<SandboxNetGuard>(); }
            guard._session = session;
            return guard;
        }

        /// <summary>True when a networked session carries anything of the Sandbox or a mod tank.</summary>
        public static bool Violated(BattleSession session)
        {
            return ViolationKey(session) != null;
        }

        /// <summary>Notice key of what a networked session breaks, or null while it is clean.</summary>
        public static string ViolationKey(BattleSession session)
        {
            if (session == null || session.Net == null) { return null; }
            if (session.Sandbox != null || SandboxBattle.Current != null) { return SandboxOnlineBan.ErrorKey; }
            if (SandboxOnlineBan.Violates(session.Mode)) { return SandboxOnlineBan.ErrorKey; }
            var plan = session.Net.Plan;
            if (plan != null && SandboxOnlineBan.Violates(plan.Mode)) { return SandboxOnlineBan.ErrorKey; }
            if (ModTankOnlineBan.FirstViolation(plan) >= 0) { return ModTankOnlineBan.BattleErrorKey; }
            var tanks = session.SlotTanks;
            if (tanks != null)
            {
                for (var i = 0; i < tanks.Count; i++)
                {
                    var card = tanks[i] != null ? tanks[i].Card : null;
                    if (card != null && (card.Mod || ModTankOnlineBan.Violates(card.Name)))
                    {
                        return ModTankOnlineBan.BattleErrorKey;
                    }
                }
            }
            return null;
        }

        private void Update()
        {
            if (_tripped)
            {
                if (_hideBannerAt > 0f && Time.realtimeSinceStartup >= _hideBannerAt)
                {
                    _hideBannerAt = -1f;
                    NetStatusBanner.Hide();
                    Destroy(gameObject);
                }
                return;
            }
            var key = ViolationKey(_session);
            if (key == null) { return; }
            Trip(key);
        }

        private async void Trip(string key)
        {
            _tripped = true;
            var session = _session;
            Debug.LogWarning("[Blitz] online ban (" + key + "): a networked battle breaks it; leaving it");
            // Outlive the battle object for the notice: the session is torn down below.
            var holder = new GameObject("SandboxNetGuardNotice");
            var notice = holder.AddComponent<SandboxNetGuard>();
            notice._tripped = true;
            notice._hideBannerAt = Time.realtimeSinceStartup + BannerSeconds;
            NetStatusBanner.Show(key);
            var handler = Tripped;
            if (handler != null) { handler(session); }
            var leave = AntiCheatResponder.LeaveOnline;
            if (leave != null)
            {
                try { await leave(); }
                catch (Exception error) { Debug.LogWarning("[Blitz] online ban: leave failed: " + error.Message); }
            }
            if (session != null) { session.ForceFinish(-1); }
        }
    }
}
