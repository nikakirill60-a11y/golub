using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Port of <c>base_tank.spot_enemy_tanks</c> / <c>try_spot_tank</c>. Every
    /// tank rescans the other team on the <c>battle_spotting_delay</c> beat,
    /// a spot lasts ten seconds, and an unspotted enemy is invisible - so a
    /// battle no longer starts with the whole enemy team on screen.
    ///
    /// Foliage attenuates the same way the source does: every bush on the way
    /// shortens the ray, except one growing on the viewer or one hiding a tank
    /// that has just fired.
    /// </summary>
    public sealed class BattleSpotter : MonoBehaviour
    {
        private static readonly RaycastHit[] Hits = new RaycastHit[4];
        private static readonly RaycastHit[] BushHits = new RaycastHit[16];
        private static readonly HitOrderComparer HitOrder = new HitOrderComparer();

        private sealed class HitOrderComparer : IComparer<RaycastHit>
        {
            public int Compare(RaycastHit a, RaycastHit b)
            {
                return a.distance.CompareTo(b.distance);
            }
        }

        private readonly Dictionary<BattleTank, float> _nextScan =
            new Dictionary<BattleTank, float>();
        private readonly Dictionary<BattleTank, float> _unspotAt =
            new Dictionary<BattleTank, float>();

        public event System.Action<BattleTank, BattleTank> FirstSpotted;
        private readonly HashSet<BattleTank> _firstSpotted = new HashSet<BattleTank>();
        private BattleRoster _roster;
        private SettingsData _settings;

        public static BattleSpotter Create(
            Transform parent, BattleRoster roster, SettingsData settings)
        {
            var go = new GameObject("BattleSpotter");
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            var spotter = go.AddComponent<BattleSpotter>();
            spotter._roster = roster;
            spotter._settings = settings != null ? settings : new SettingsData();
            if (roster != null)
            {
                Hide(roster.TanksA);
                Hide(roster.TanksB);
            }
            return spotter;
        }

        private static void Hide(List<BattleTank> tanks)
        {
            for (var i = 0; i < tanks.Count; i++)
            {
                if (tanks[i] != null)
                {
                    tanks[i].RefreshSpottedVisibility();
                }
            }
        }

        private void Update()
        {
            if (_roster == null)
            {
                return;
            }

            Tick(Time.time);
        }

        /// <summary>One pass of the source physics step: unspot, then scan.</summary>
        public void Tick(float now)
        {
            Scan(_roster.TanksA, now);
            Scan(_roster.TanksB, now);
        }

        private void Scan(List<BattleTank> viewers, float now)
        {
            for (var i = 0; i < viewers.Count; i++)
            {
                var viewer = viewers[i];
                if (viewer == null)
                {
                    continue;
                }

                Expire(viewer, now);
                if (viewer.Dead || viewer.Blocked)
                {
                    continue;
                }

                float next;
                if (_nextScan.TryGetValue(viewer, out next) && now < next)
                {
                    continue;
                }

                var uprising = viewer.GetComponent<UprisingTank>();
                _nextScan[viewer] = now + (uprising != null && uprising.State.Has("ghost") ? .02f : Mathf.Max(0.1f, _settings.BattleSpottingDelay));
                var targets = _roster.GetEnemyTanks(viewer.Team);
                for (var t = 0; t < targets.Count; t++)
                {
                    var target = targets[t];
                    if (target == null || target.Dead || (target.Uprising != null && target.Uprising.Concealed))
                    {
                        continue;
                    }

                    // set_spotted(true) of get_destroyed: a wreck is visible to
                    // everybody, so Play Dead must be too or vanishing in the
                    // fog would give the act away.
                    if (UprisingTank.InThanatos(target))
                    {
                        Mark(target, now);
                        continue;
                    }

                    if (TrySpot(viewer, target))
                    {
                        if (_firstSpotted.Add(target) && FirstSpotted != null) FirstSpotted(viewer, target);
                        Mark(target, now);
                    }
                }
            }
        }

        /// <summary><c>curtime() > unspot_time</c> drops the spot again.</summary>
        private void Expire(BattleTank tank, float now)
        {
            if (!tank.Spotted || tank.Dead)
            {
                return;
            }

            float until;
            if (!_unspotAt.TryGetValue(tank, out until))
            {
                return;
            }

            if (now > until)
            {
                tank.SetSpotted(false);
            }
        }

        private void Mark(BattleTank tank, float now)
        {
            _unspotAt[tank] = now + SpottingMath.UnspotSeconds;
            tank.SetSpotted(true);
        }

        /// <summary>
        /// <c>try_spot_tank</c>: the close-range grant, then one clear ray from
        /// the gun to any of the target spot points inside the view range.
        /// </summary>
        public static bool TrySpot(BattleTank viewer, BattleTank target)
        {
            if (viewer == null || target == null || viewer == target)
            {
                return false;
            }

            var close = SpottingMath.AlwaysSpotDistance * SpottingMath.AlwaysSpotDistance;
            if ((target.transform.position - viewer.transform.position).sqrMagnitude < close)
            {
                return true;
            }

            var from = ViewPoint(viewer);
            var distance = SpottingMath.SpotDistance(
                viewer.CurrentViewRange, CamoFactor(target));
            var limit = distance * distance;
            var targetJustShot = target.JustShot();
            var points = target.SpotPoints;
            for (var i = 0; i < points.Count; i++)
            {
                if (points[i] == null)
                {
                    continue;
                }

                var to = points[i].position;
                var gap = (to - from).sqrMagnitude;
                if (gap > limit)
                {
                    continue;
                }

                var rayDistance = BushAttenuated(from, to, distance, targetJustShot);
                if (rayDistance < SpottingMath.MinimumSpotDistance)
                {
                    continue;
                }

                if (gap > rayDistance * rayDistance)
                {
                    continue;
                }

                if (!Obstructed(from, to))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The <c>for j in range(5)</c> bush walk: each bush between the two
        /// tanks multiplies the ray by <c>1 - density</c>, and the walk stops at
        /// a bush the viewer sits in, or at one next to a target that just fired.
        /// </summary>
        public static float BushAttenuated(
            Vector3 from, Vector3 to, float distance, bool targetJustShot)
        {
            using var perfScope = BattlePerfMarkers.SpotBush.Auto();
            var delta = to - from;
            var length = delta.magnitude;
            if (length <= 0.001f)
            {
                return distance;
            }

            var count = Physics.RaycastNonAlloc(
                new Ray(from, delta / length), BushHits, length,
                1 << BattleLayers.Foliage);
            if (count <= 0)
            {
                return distance;
            }

            if (count > BushHits.Length)
            {
                count = BushHits.Length;
            }

            System.Array.Sort(BushHits, 0, count, HitOrder);
            var active = SpottingMath.BushActiveDistance * SpottingMath.BushActiveDistance;
            var passes = Mathf.Min(count, SpottingMath.BushPasses);
            for (var i = 0; i < passes; i++)
            {
                var point = BushHits[i].point;
                var closeToViewer = (point - from).sqrMagnitude <= active;
                var closeToTarget = (point - to).sqrMagnitude <= active;
                if (closeToViewer || (closeToTarget && targetJustShot))
                {
                    break;
                }

                distance *= 1f - SpottingMath.BushDensity;
            }

            return distance;
        }

        /// <summary><c>get_camo_factor()</c> of the target hull.</summary>
        public static float CamoFactor(BattleTank tank)
        {
            return tank == null ? 0f : tank.CamoFactor();
        }

        /// <summary><c>get_spot_pos()</c> is the gun muzzle.</summary>
        private static Vector3 ViewPoint(BattleTank tank)
        {
            if (tank.Gun != null)
            {
                return tank.Gun.transform.position;
            }

            return tank.transform.position;
        }

        private static bool Obstructed(Vector3 from, Vector3 to)
        {
            using var perfScope = BattlePerfMarkers.SpotSight.Auto();
            var delta = to - from;
            var distance = delta.magnitude;
            if (distance <= 0.001f)
            {
                return false;
            }

            return Physics.RaycastNonAlloc(
                new Ray(from, delta / distance), Hits, distance,
                1 << BattleLayers.Static) > 0;
        }
    }
}
