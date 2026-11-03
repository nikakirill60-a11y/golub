using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// <c>rotate_turret</c> nudges the hull whenever the aim point sits
    /// outside the traverse arc, and the nudge is bang-bang: a full left or
    /// right steer command every physics tick. A tank destroyer with a narrow
    /// arc therefore rides a closed loop - the hull turns, which moves the
    /// gun the target angle is measured from, which decides the next nudge.
    /// When that loop does not settle the vehicle shivers left and right on
    /// its own, with nobody touching the keys.
    ///
    /// The loop is replayed here with the port's own maths - the same target
    /// yaw, the same steer assist, the same traverse step and the same
    /// angular damping the suspension applies once the nudge stops - for
    /// every turret and gun of every card, near and far targets on both
    /// sides. Settling shows as the assist no longer changing its mind: the
    /// single switch back to zero is the stop, repeated switches are the
    /// shiver.
    /// </summary>
    public sealed class TankSteerAssistStabilityTests
    {
        private const float Step = 0.02f;
        private const float Seconds = 15f;

        /// <summary>Where the tail of the run starts, as a share of the ticks.</summary>
        private const float TailShare = 0.6f;

        /// <summary>A stop shows as one switch; anything more is a shiver.</summary>
        private const int MaxSwitches = 1;

        private static readonly float[] Distances = { 12f, 25f, 80f };
        private static readonly float[] Azimuths = { 35f, -35f, 90f, -90f, 160f };

        [Test]
        public void EveryHullSettlesInsteadOfShiveringOnTheSteerAssist()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var failures = new List<string>();
            var measured = 0;
            var runs = 0;

            foreach (var card in database.Tanks)
            {
                var assets = TankModelAssets.For(card);
                var nodesText = Resources.Load<TextAsset>("Data/" + assets.NodeDataResource);
                Assert.IsNotNull(nodesText, card.Name + ": no node table");
                var nodes = TankNodeTable.Parse(nodesText.text);
                var hullSpeed = HullRotationSpeed(card);

                foreach (var turret in card.Turrets)
                {
                    if (!nodes.Has(turret.Mesh))
                    {
                        continue;
                    }

                    foreach (var slot in turret.Guns)
                    {
                        var gun = GunNamed(card, slot.Name);
                        if (gun == null || !nodes.Has(slot.Mesh))
                        {
                            continue;
                        }

                        // A full-traverse turret never asks the hull for help.
                        if (Mathf.Abs(gun.DeviationLeft) >= 180f &&
                            Mathf.Abs(gun.DeviationRight) >= 180f)
                        {
                            continue;
                        }

                        for (var d = 0; d < Distances.Length; d++)
                        {
                            for (var a = 0; a < Azimuths.Length; a++)
                            {
                                runs++;
                                var switches = Replay(
                                    ToVector(nodes.World(turret.Mesh)),
                                    ToVector(nodes.World(slot.Mesh)),
                                    gun,
                                    card.TurretRotationSpeed,
                                    hullSpeed,
                                    Distances[d],
                                    Azimuths[a]);
                                if (switches <= MaxSwitches)
                                {
                                    continue;
                                }

                                failures.Add(string.Format(CultureInfo.InvariantCulture,
                                    "{0}: {1}/{2} keeps steering itself ({3} switches at {4:0} m, {5:0} deg)",
                                    card.Name, turret.Mesh, slot.Mesh, switches,
                                    Distances[d], Azimuths[a]));
                            }
                        }
                    }
                }

                measured++;
            }

            Assert.AreEqual(database.Count, measured, "cards measured");
            Assert.That(runs, Is.GreaterThan(1000), "steer assist runs");
            Assert.IsEmpty(failures, Join(failures));
        }

        /// <summary>
        /// One closed-loop run. Returns how often the steer assist changed its
        /// command over the tail of the run.
        /// </summary>
        private static int Replay(
            Vector3 turretPivot,
            Vector3 gunPivot,
            GunInfo gun,
            float turretSpeed,
            float hullSpeed,
            float distance,
            float azimuthDegrees)
        {
            var target = new Vector3(
                distance * Mathf.Sin(azimuthDegrees * Mathf.Deg2Rad),
                0f,
                distance * Mathf.Cos(azimuthDegrees * Mathf.Deg2Rad));

            var hullYaw = 0f;
            var turretYaw = 0f;
            var spin = 0f;
            var ticks = Mathf.RoundToInt(Seconds / Step);
            var tail = Mathf.RoundToInt(ticks * TailShare);
            var previous = 0f;
            var switches = 0;

            for (var tick = 0; tick < ticks; tick++)
            {
                var hull = Quaternion.Euler(0f, hullYaw * Mathf.Rad2Deg, 0f);
                var mount = turretPivot +
                    Quaternion.Euler(0f, turretYaw * Mathf.Rad2Deg, 0f) *
                    (gunPivot - turretPivot);
                var gunPosition = hull * mount;
                var forward = hull * Vector3.forward;
                var localAimX = (Quaternion.Inverse(hull) * target).x;

                var targetYaw = TankTurret.TurretTargetYaw(
                    gunPosition, target, forward, Vector3.up, localAimX);
                var assist = TankTurret.SteerAssist(
                    targetYaw, gun.DeviationLeft, gun.DeviationRight);

                // suspension_component: a steer command writes the yaw rate
                // outright, and the idle branch damps whatever is left of it.
                if (assist != 0f)
                {
                    spin = hullSpeed * Mathf.Deg2Rad * assist * SuspensionTuning.YawStickFactor;
                }
                else
                {
                    spin -= SuspensionTuning.Clamp(
                        spin,
                        -SuspensionTuning.AngularDampThreshold,
                        SuspensionTuning.AngularDampThreshold) *
                        SuspensionTuning.AngularDampCoeff * Step;
                }

                hullYaw += spin * Step;
                turretYaw = AimMath.RotateTowards(
                    turretYaw,
                    TankTurret.ClampYaw(targetYaw, gun.DeviationLeft, gun.DeviationRight),
                    turretSpeed * Step);

                if (tick >= tail && !Mathf.Approximately(assist, previous))
                {
                    switches++;
                }

                previous = assist;
            }

            return switches;
        }

        /// <summary>The best tracks the card ships turn the hull fastest.</summary>
        private static float HullRotationSpeed(TankInfo card)
        {
            var speed = 45f;
            foreach (var tracks in card.Tracks)
            {
                speed = Mathf.Max(speed, tracks.RotationSpeed);
            }

            return speed;
        }

        private static GunInfo GunNamed(TankInfo card, string name)
        {
            foreach (var gun in card.Guns)
            {
                if (gun.Name == name)
                {
                    return gun;
                }
            }

            return null;
        }

        private static string Join(IReadOnlyList<string> failures)
        {
            var text = new StringBuilder(failures.Count + " hulls steer themselves");
            foreach (var failure in failures)
            {
                text.Append('\n').Append(failure);
            }

            return text.ToString();
        }

        private static Vector3 ToVector(Vec3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }
    }
}
