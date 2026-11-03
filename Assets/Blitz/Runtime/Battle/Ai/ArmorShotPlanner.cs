using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>Samples visible collision surfaces, not hidden internal modules.
    /// Uses the projectile's armour rules and only ammunition actually available.</summary>
    public static class ArmorShotPlanner
    {
        public struct Plan
        {
            /// <summary>
            /// Hopeless: no stocked shell gets through a visible surface and no
            /// track is exposed, so this is the least bad shot - HE for its
            /// splash, else (at point-blank range only) the thinnest plate.
            /// A crew still fires it instead of staring at the target.
            /// </summary>
            public bool Valid, Tracks, Hopeless;
            public int Shell;
            public Vector3 Point;
            public Transform Mount;
        }

        private sealed class HitOrder : IComparer<RaycastHit>
        {
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }
        // Main-thread only, like Unity physics. Full buffer fails closed.
        private static readonly RaycastHit[] Hits = new RaycastHit[64];
        private static readonly HitOrder Order = new HitOrder();
        private static readonly BattleArmorPlate[] Surfaces = new BattleArmorPlate[64];
        private static readonly bool[] Own = new bool[64];
        private static bool[] _launchable = new bool[8];
        private static float[] _penetration = new float[8];
        private static readonly RaycastBatch WorldRays = new RaycastBatch(1);
        private static readonly RaycastBatch TankRays = new RaycastBatch(32);
        private static Vector3[] _sampleDirection = new Vector3[256];
        private static float[] _sampleLength = new float[256];
        private static int[] _sampleRay = new int[256];
        private static int _sampleCount;

        /// <summary>How often a bot re-checks its plan against the same target.</summary>
        public const float ReplanInterval = 0.25f;

        /// <summary>The longest a plan is kept without re-planning.</summary>
        public const float MaxPlanAge = 1f;

        /// <summary>Shooter muzzle or target movement, in metres, that re-plans.</summary>
        public const float ReplanShift = 0.3f;

        /// <summary>Target hull turn, in degrees, that re-plans.</summary>
        public const float ReplanTurnDegrees = 2f;

        /// <summary>Inside this range a hopeless kinetic shot is still taken.</summary>
        public const float HopelessReach = 60f;

        /// <summary>
        /// Whether a plan can serve another re-check. The plan's aim point
        /// rides the target's plate, so as long as neither the muzzle nor the
        /// target moved or turned noticeably and the same shells are loaded,
        /// a new plan would trace the same geometry to the same answer. It is
        /// still renewed at least every <see cref="MaxPlanAge"/> for cover
        /// that moves on its own, and every shot plans afresh anyway.
        /// </summary>
        public static bool PlanStillFresh(float age, float originShift, float targetShift,
            float targetTurnDegrees, bool sameAmmo)
        {
            return sameAmmo
                && age < MaxPlanAge
                && originShift < ReplanShift
                && targetShift < ReplanShift
                && targetTurnDegrees < ReplanTurnDegrees;
        }

        /// <summary>Tank colliders a planning ray can meet: armour and spaced armour.</summary>
        private static int TankMask { get { return (1 << BattleLayers.Armor) | (1 << BattleLayers.Screen); } }

        /// <summary>The rest of the projectile mask: the map and its destructibles.</summary>
        private static int WorldMask { get { return BattleLayers.ProjectileMask & ~TankMask; } }

        /// <summary>
        /// Picks the best shell and aim point on <paramref name="target"/>.
        ///
        /// Every probe walks the projectile-mask hits in distance order until
        /// the first one that is not the shooter's own: a map or destructible
        /// collider always ends the walk, so only the nearest one matters.
        /// The probe therefore asks for that nearest world hit first (a
        /// closest-hit query, which stops early instead of collecting every
        /// terrain and prop crossing) and gathers the tank colliders only up
        /// to it. A probe whose world hit lies before the target's bounding
        /// sphere cannot reach the target at all and is dropped. The walk and
        /// the scoring themselves are unchanged.
        /// </summary>
        public static Plan Choose(BattleTank shooter, BattleTank target)
        {
            using var perfScope = BattlePerfMarkers.ArmorPlan.Auto();
            var best = new Plan();
            if (shooter == null || target == null || target.Dead || shooter.GunInfo == null)
                return best;
            var origin = shooter.GunShootPoint();
            var shells = shooter.GunInfo.Shells;
            var score = float.NegativeInfinity;
            var trackBest = new Plan();
            var trackScore = float.NegativeInfinity;

            // Ammunition and the rolled-up penetration do not change while the
            // probes run, so they are read once instead of once per probe.
            if (_launchable.Length < shells.Count)
            {
                _launchable = new bool[shells.Count];
                _penetration = new float[shells.Count];
            }

            var anyShell = false;
            var deviation = 1f + BattleProjectile.PenetrationDeviationNow();
            for (var slot = 0; slot < shells.Count; slot++)
            {
                _launchable[slot] = shooter.CanLaunchShell(slot);
                _penetration[slot] = shells[slot].Penetration * deviation;
                anyShell |= _launchable[slot];
            }

            if (!anyShell) return best;

            Vector3 centre;
            float radius;
            if (!TargetSphere(target, out centre, out radius)) return best;

            var tankMask = TankMask;
            var worldMask = WorldMask;
            var hopeless = new Plan();
            var hopelessScore = float.NegativeInfinity;
            var close = (centre - origin).sqrMagnitude < HopelessReach * HopelessReach;

            // Pass 1: every probe and its nearest world hit, in one batch.
            WorldRays.Clear();
            _sampleCount = 0;
            foreach (var plate in target.Plates)
            {
                if (plate.Shape == null || plate.Shape.sharedMesh == null) continue;
                foreach (var localPoint in plate.AimSamples)
                {
                    var point = plate.transform.TransformPoint(localPoint);
                    var delta = point - origin;
                    if (delta.sqrMagnitude < 0.001f) continue;
                    if (_sampleCount == _sampleDirection.Length)
                    {
                        Array.Resize(ref _sampleDirection, _sampleCount * 2);
                        Array.Resize(ref _sampleLength, _sampleCount * 2);
                        Array.Resize(ref _sampleRay, _sampleCount * 2);
                    }

                    var direction = delta.normalized;
                    var length = delta.magnitude + 0.1f;
                    _sampleDirection[_sampleCount] = direction;
                    _sampleLength[_sampleCount] = length;
                    WorldRays.Add(origin, direction, length, worldMask, QueryTriggerInteraction.Collide);
                    _sampleCount++;
                }
            }

            WorldRays.Run();

            // Pass 2: the tank colliders of every probe that is not walled
            // off before the target, up to its wall, in one batch.
            TankRays.Clear();
            for (var sample = 0; sample < _sampleCount; sample++)
            {
                var direction = _sampleDirection[sample];
                var length = _sampleLength[sample];
                RaycastHit wall;
                if (WorldRays.First(sample, out wall))
                {
                    length = wall.distance;
                    if (length < Vector3.Dot(centre - origin, direction) - radius)
                    {
                        _sampleRay[sample] = -1;
                        continue;
                    }
                }

                _sampleLength[sample] = length;
                _sampleRay[sample] = TankRays.Add(origin, direction, length, tankMask, QueryTriggerInteraction.Collide);
            }

            TankRays.Run();

            // Pass 3: the unchanged walk and scoring, probe by probe in order.
            for (var sample = 0; sample < _sampleCount; sample++)
            {
                {
                    var ray = _sampleRay[sample];
                    if (ray < 0) continue;
                    var direction = _sampleDirection[sample];
                    var hitCount = TankRays.Hits(ray, Hits);
                    if (hitCount == TankRays.MaxHits)
                    {
                        // Rare crowded line of fire: ask again with the full buffer.
                        hitCount = Physics.RaycastNonAlloc(origin, direction, Hits, _sampleLength[sample],
                            tankMask, QueryTriggerInteraction.Collide);
                    }

                    if (hitCount == Hits.Length) continue;
                    Array.Sort(Hits, 0, hitCount, Order);
                    for (var h = 0; h < hitCount; h++)
                    {
                        var collider = Hits[h].collider;
                        Own[h] = shooter.Owns(collider);
                        Surfaces[h] = Own[h] ? null : collider.GetComponent<BattleArmorPlate>();
                    }

                    for (var slot = 0; slot < shells.Count; slot++)
                    {
                        if (!_launchable[slot]) continue;
                        var shell = shells[slot];
                        var penetration = _penetration[slot];
                        var screened = false;
                        var screenPoint = Vector3.zero;
                        var first = true;
                        for (var h = 0; h < hitCount; h++)
                        {
                            if (Own[h]) continue;
                            var hit = Hits[h];
                            var surface = Surfaces[h];
                            if (surface == null || surface.Tank != target) break;
                            if (first && surface.Module == TankModuleIds.Tracks && shell.DeviceDamage > 0f)
                            {
                                // Prefer HE and the exposed end wheels, avoiding the
                                // tougher central run when both are visible.
                                var end = Mathf.Abs(surface.transform.InverseTransformPoint(hit.point).z);
                                var value = (shell.Type == ShellType.He ? 10000f : 0f) + shell.DeviceDamage + end;
                                if (value > trackScore)
                                {
                                    trackScore = value;
                                    trackBest = Make(slot, hit, surface, true);
                                }
                            }
                            first = false;
                            var angle = ArmorMath.ImpactAngle(Vector3.Dot(hit.normal, direction));
                            if (surface.Screen)
                            {
                                if (shell.Type == ShellType.He) break;
                                penetration = HitResolver.PenetrationAfterPlate(penetration,
                                    surface.UseNominalArmor ? surface.Armor :
                                    ArmorMath.EffectiveArmor(shell.Type, shell.Caliber, surface.Armor, angle));
                                if (penetration <= 0f) break;
                                screened = true;
                                screenPoint = hit.point;
                                continue;
                            }
                            if (screened) penetration = HitResolver.PenetrationAfterGap(shell.Type,
                                penetration, Vector3.Distance(screenPoint, hit.point));
                            var outcome = HitResolver.ResolveArmor(shell.Type, shell.Caliber, penetration,
                                surface.Armor, angle, screened, shell.RicochetAngle);
                            if (outcome == HitOutcome.Penetration || outcome == HitOutcome.Overmatch)
                            {
                                var value = shell.Damage - (shell.Premium ? 10f : 0f) +
                                    Mathf.Clamp(penetration - ArmorMath.EffectiveArmor(shell.Type,
                                        shell.Caliber, surface.Armor, angle), 0f, 100f);
                                if (value > score)
                                {
                                    score = value;
                                    best = Make(slot, hit, surface, false);
                                }
                            }
                            else if (shell.Type == ShellType.He || close)
                            {
                                var value = (shell.Type == ShellType.He ? 1000f : 0f) + penetration
                                    - ArmorMath.EffectiveArmor(shell.Type, shell.Caliber, surface.Armor, angle);
                                if (value > hopelessScore)
                                {
                                    hopelessScore = value;
                                    hopeless = Make(slot, hit, surface, false);
                                    hopeless.Hopeless = true;
                                }
                            }
                            break;
                        }
                    }
                }
            }
            return best.Valid ? best : trackBest.Valid ? trackBest : hopeless;
        }

        /// <summary>
        /// A sphere around every plate collider of the target: any ray that
        /// meets one of them does so inside it.
        /// </summary>
        private static bool TargetSphere(BattleTank target, out Vector3 centre, out float radius)
        {
            var bounds = new Bounds();
            var any = false;
            foreach (var plate in target.Plates)
            {
                if (plate == null || plate.Shape == null) continue;
                if (any) bounds.Encapsulate(plate.Shape.bounds);
                else { bounds = plate.Shape.bounds; any = true; }
            }

            centre = bounds.center;
            radius = bounds.extents.magnitude + 0.5f;
            return any;
        }

        private static Plan Make(int slot, RaycastHit hit, BattleArmorPlate plate, bool tracks)
        {
            return new Plan { Valid = true, Tracks = tracks, Shell = slot,
                Point = hit.point, Mount = plate.FollowMount != null ? plate.FollowMount : plate.transform };
        }
    }
}
