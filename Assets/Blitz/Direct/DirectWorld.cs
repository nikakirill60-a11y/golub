using System;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Mods;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;

namespace BlitzLegacy.Direct
{
    /// <summary>Battle state <-> wire, shared by the server and the client.</summary>
    public static class DirectWorld
    {
        /// <summary><see cref="DirectTankState.Flags"/> bit: the slot's Uprising snapshot is valid.</summary>
        public const byte UprisingValidFlag = 8;

        /// <summary><see cref="DirectTankState.Flags"/> bit: the slot's Big Boss snapshot is valid.</summary>
        public const byte BigBossValidFlag = 16;

        public static BattleSession Session
        {
            get
            {
                var bootstrap = BlitzBootstrap.Current;
                return bootstrap != null ? bootstrap.Battle : null;
            }
        }

        /// <summary>Server: the replicated state of one tank (same fields as the Photon host).</summary>
        public static DirectTankState Capture(BattleTank tank, BattleSession session)
        {
            var transform = tank.transform;
            var suspension = tank.Suspension;
            return new DirectTankState
            {
                Forward = suspension != null ? suspension.ForwardAxis : 0f,
                Right = suspension != null ? suspension.RightAxis : 0f,
                SpeedMs = suspension != null ? suspension.SpeedMs : 0f,
                WheelHits = (byte)(suspension != null ? Mathf.Clamp(suspension.WheelHits, 0, 255) : 0),
                Flags = (byte)((suspension != null && suspension.OnTank ? 1 : 0) | (tank.TeamKiller ? 2 : 0)
                    | (tank.Turret != null && tank.Turret.LobAim ? 4 : 0)),
                Aim = tank.Turret != null ? tank.Turret.AimTargetLerp : transform.position,
                Dispersion = tank.Dispersion,
                Position = transform.position,
                Rotation = transform.rotation,
                Yaw = tank.Turret != null ? tank.Turret.Yaw : 0f,
                Pitch = tank.Turret != null ? tank.Turret.Pitch : 0f,
                Health = tank.Health != null ? tank.Health.Health : 0,
                TeamDamage = session != null ? session.TeamDamage.BattleDamage(tank) : 0,
            };
        }

        public static bool IsUnset(Quaternion q)
        {
            return q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f;
        }

        public static float LerpRadians(float a, float b, float t)
        {
            // Unclamped: past 1 the turn carries on (extrapolation past the newest snapshot).
            return a + Mathf.DeltaAngle(a * Mathf.Rad2Deg, b * Mathf.Rad2Deg) * Mathf.Deg2Rad * t;
        }

        /// <summary>
        /// Client: puts one interpolated server state on a puppet tank. An
        /// alpha past 1 extrapolates the pose along the last two states.
        /// </summary>
        public static void Apply(BattleSession session, BattleTank tank, DirectTankState a, DirectTankState b, float alpha,
            bool uprising, UprisingSnapshot up, bool bigBoss = false, BigBossSnapshot boss = default(BigBossSnapshot))
        {
            if (IsUnset(b.Rotation)) { return; }
            if (IsUnset(a.Rotation)) { a = b; }
            // Before health: a death and its respawn countdown land together,
            // so the roster never sees a respawning team as wiped out.
            if (uprising && (b.Flags & UprisingValidFlag) != 0)
            {
                var component = tank.GetComponent<UprisingTank>();
                if (component != null) { component.ApplyNet(up); }
            }
            if (bigBoss && (b.Flags & BigBossValidFlag) != 0 && tank.BigBoss != null) { tank.BigBoss.ApplyNet(boss); }
            tank.ApplyNetState(
                Vector3.LerpUnclamped(a.Position, b.Position, alpha),
                Quaternion.SlerpUnclamped(a.Rotation, b.Rotation, alpha),
                LerpRadians(a.Yaw, b.Yaw, alpha),
                LerpRadians(a.Pitch, b.Pitch, alpha),
                b.Health);
            tank.ApplyNetMotion(b.Forward, b.Right, Mathf.Lerp(a.SpeedMs, b.SpeedMs, alpha),
                b.WheelHits, (b.Flags & 1) != 0);
            tank.TeamKiller = (b.Flags & 2) != 0;
            tank.ApplyNetAim(Vector3.Lerp(a.Aim, b.Aim, alpha), (b.Flags & 4) != 0,
                Mathf.Lerp(a.Dispersion, b.Dispersion, alpha));
            if (tank == session.Player) { session.TeamDamage.ReplicatedLocalDamage = Mathf.Max(0, b.TeamDamage); }
        }

        public static UprisingTank UprisingAt(BattleSession session, int slot)
        {
            if (session == null || slot < 0 || slot >= session.SlotTanks.Count) { return null; }
            var tank = session.SlotTanks[slot];
            return tank != null ? tank.GetComponent<UprisingTank>() : null;
        }

        public static BigBossTank BigBossAt(BattleSession session, int slot)
        {
            if (session == null || slot < 0 || slot >= session.SlotTanks.Count) { return null; }
            var tank = session.SlotTanks[slot];
            return tank != null ? tank.BigBoss : null;
        }

        /// <summary>Effect ids a client replays; anything else from the wire is dropped.</summary>
        public static bool IsKnownEffect(string id)
        {
            return !string.IsNullOrEmpty(id) && id.Length <= 32 && (UprisingVfx.Sources.ContainsKey(id) || id == "pretendDead");
        }

        /// <summary>
        /// True when the tank ships with this build (a hello may name anything).
        /// Online seats stock tanks only: installed mods never count.
        /// </summary>
        public static bool IsKnownTank(string tankId)
        {
            return ModTankOnlineBan.IsStock(tankId);
        }

        /// <summary>
        /// Headless runs (the server and smoke clients): log in to the named
        /// local account through the login screen, creating it on a clean
        /// profile, and enter the hangar. Returns true once it was attempted.
        /// </summary>
        public static bool AutoLogin(BlitzBootstrap bootstrap, string name, string tankId)
        {
            var login = UnityEngine.Object.FindAnyObjectByType<LoginScreen>();
            if (login == null) { return false; }
            var accounts = bootstrap.Accounts;
            var current = accounts.GetCurrentAccount();
            if (current == null || current.Name != name)
            {
                var found = false;
                foreach (var pair in accounts.Accounts)
                {
                    if (pair.Value.Name != name) { continue; }
                    accounts.SwitchAccount(pair.Key);
                    found = true;
                    break;
                }
                if (!found)
                {
                    var created = accounts.CreateAccount(name, name, Guid.NewGuid().ToString("N"),
                        bootstrap.Clock.NowUnixSeconds);
                    if (created.Ok) { accounts.SwitchAccount(created.Account.Key); }
                }
            }
            accounts.LogInToCurrentAccount(bootstrap.Clock.NowUnixSeconds);
            var account = accounts.GetCurrentAccount();
            if (account != null && !string.IsNullOrEmpty(tankId))
            {
                if (account.GetTank(tankId) == null) { account.AddTank(tankId); }
                account.SelectedTankName = tankId;
            }
            bootstrap.Persist();
            bootstrap.EnterHangar(login);
            return true;
        }
    }
}
