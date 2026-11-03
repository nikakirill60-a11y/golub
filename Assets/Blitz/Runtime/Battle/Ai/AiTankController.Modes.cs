using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// The mode layer on top of the ported brain. In Big Boss every crew plays
    /// its role: the healer and the graviton escort their boss (a graviton
    /// without one pairs with an intoxicator), the healer shoots wounded
    /// allies with its mending shell, the bomber charges a group with its
    /// blast ready and breaks away once it is spent, the intoxicator trades
    /// from cover and a battered boss falls back to its team. In Uprising a
    /// bot fights the way its bought perks pay. In every mode a slow turret
    /// gets the hull's help against a close target, the support consumables
    /// are spent where they matter, and a respawned hull starts afresh.
    /// </summary>
    public sealed partial class AiTankController
    {
        /// <summary>Big Boss role ids of big_boss_1120.json.</summary>
        public const int BossRole = 1;
        public const int HealerRole = 2;
        public const int KillerRole = 4;
        public const int BomberRole = 5;
        public const int GravitonRole = 6;

        /// <summary>Turret error, in degrees, at which the hull starts helping the traverse.</summary>
        public const float TraverseAssistDegrees = 35f;

        /// <summary>Turret error at which the hull hands the rest back to the turret.</summary>
        public const float TraverseReleaseDegrees = 12f;

        /// <summary>Range inside which any noticeably slow traverse gets help.</summary>
        public const float TraverseAssistDistance = 120f;

        /// <summary>A swing the turret finishes within this many seconds needs no help.</summary>
        public const float TraverseAssistMinSeconds = 0.75f;

        /// <summary>Beyond <see cref="TraverseAssistDistance"/> only a swing this long gets help.</summary>
        public const float TraverseAssistSlowSeconds = 2.5f;

        /// <summary>A casemate gun is brought round by its hull up to this range.</summary>
        public const float FixedGunAssistDistance = 300f;

        /// <summary>After handing back, the hull waits this long before helping again...</summary>
        public const float TraverseAssistCooldown = 1.5f;

        /// <summary>...unless the target got this far out of the gun's line.</summary>
        public const float TraverseAssistUrgentDegrees = 100f;

        /// <summary>How long a navmesh drive with no corridor holds before steering straight.</summary>
        public const float UnreachableDirectSeconds = 1.5f;

        /// <summary>Only a goal this close is steered at straight without a corridor.</summary>
        public const float UnreachableDirectDistance = 40f;

        /// <summary>Progress, in metres, that resets the stall watchdog.</summary>
        public const float StallDistance = 4f;

        /// <summary>Seconds without progress after which a roaming hull picks another goal.</summary>
        public const float StallSeconds = 6f;

        /// <summary>How far a healer looks for a wounded ally.</summary>
        public const float HealReach = 160f;

        /// <summary>The range a healer shoots its mending shell from.</summary>
        public const float HealRange = 70f;

        /// <summary>An enemy this close makes an escort fight it rather than keep station.</summary>
        public const float EscortThreatDistance = 25f;

        /// <summary>
        /// For how long an escort keeps a station from which its gun has no
        /// line on its target before it goes and finds one.
        /// </summary>
        public const float EscortBlindSeconds = 15f;

        /// <summary>Station distance behind the anchor.</summary>
        public const float EscortGapHealer = 16f;
        public const float EscortGapGraviton = 24f;

        /// <summary>Inside this range a target outranks everything the rating weighs.</summary>
        public const float PointBlankDistance = 50f;

        /// <summary>How far a bomber goes for a group with its blast ready.</summary>
        public const float BomberChargeDistance = 300f;

        /// <summary>A spent bomber breaks away from enemies closer than this.</summary>
        public const float BomberBreakAwayDistance = 80f;

        /// <summary>The band an intoxicator trades from cover in.</summary>
        public const float CoverFightMinDistance = 35f;
        public const float CoverFightMaxDistance = 400f;

        /// <summary>
        /// For how long a shot still vouches that an intoxicator's peek spot
        /// sees the enemy (stretched to a full reload and two peeks for a slow
        /// gun).
        /// </summary>
        public const float CoverFightLineSeconds = 12f;

        /// <summary>How often the crew checks its consumables.</summary>
        public const float ConsumableInterval = 0.5f;

        private bool _traverseAssist;
        private float _traverseReleasedAt = -100f;
        private float _unreachableSince = -1f;
        private Vector3 _progressFrom;
        private float _progressAt = -1f;
        private float _consumablesAt;

        /// <summary>The navmesh drive found no corridor to its goal.</summary>
        public bool DriveGoalUnreachable { get; private set; }

        /// <summary>For how long the current navmesh drive has had no corridor.</summary>
        public float UnreachableSeconds
        {
            get { return _unreachableSince < 0f ? 0f : Time.time - _unreachableSince; }
        }

        /// <summary>Whether the hull is helping its turret round right now.</summary>
        public bool AssistingTraverse
        {
            get { return _traverseAssist; }
        }

        /// <summary>The Big Boss role this hull plays; 0 outside the mode.</summary>
        public int ModeRole
        {
            get { return RoleOf(Tank); }
        }

        /// <summary>An intoxicator fights from cover: out to shoot, back to reload.</summary>
        public bool CoverFighter
        {
            get { return ModeRole == KillerRole; }
        }

        private static int RoleOf(BattleTank t)
        {
            var c = t != null ? t.BigBoss : null;
            return c != null && c.State != null ? c.State.RoleId : 0;
        }

        /// <summary>Whether the hull owns the Uprising perk <paramref name="family"/> at any grade.</summary>
        public bool HasUprisingPerk(string family)
        {
            var u = Tank != null ? Tank.Uprising : null;
            if (u == null || u.State == null)
            {
                return false;
            }

            return u.State.Has(family) || u.State.Has(family + "1") || u.State.Has(family + "2");
        }

        /// <summary>The own team's living boss.</summary>
        public BattleTank OwnBoss()
        {
            var allies = Allies();
            for (var i = 0; i < allies.Count; i++)
            {
                var a = allies[i];
                if (a != null && a != Tank && !a.Dead && RoleOf(a) == BossRole)
                {
                    return a;
                }
            }

            return null;
        }

        /// <summary>
        /// Whom a healer or a graviton keeps to: the own boss, else (a
        /// graviton) the nearest intoxicator within 250 m, so the trap lands
        /// where the poison does.
        /// </summary>
        public BattleTank EscortAnchor()
        {
            var role = ModeRole;
            if (role != HealerRole && role != GravitonRole)
            {
                return null;
            }

            var boss = OwnBoss();
            if (boss != null || role != GravitonRole)
            {
                return boss;
            }

            BattleTank best = null;
            var bestSqr = 250f * 250f;
            var allies = Allies();
            for (var i = 0; i < allies.Count; i++)
            {
                var a = allies[i];
                if (a == null || a == Tank || a.Dead || RoleOf(a) != KillerRole)
                {
                    continue;
                }

                var d = (a.Pos() - Pos()).sqrMagnitude;
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = a;
                }
            }

            return best;
        }

        /// <summary>
        /// The ally a healer should shoot its mending shell at: the boss below
        /// nine tenths, a tank near the boss (or the healer) below three
        /// quarters, anyone else below half; the boss counts double.
        /// </summary>
        public BattleTank HealTarget()
        {
            if (Tank == null || ModeRole != HealerRole)
            {
                return null;
            }

            var boss = OwnBoss();
            BattleTank best = null;
            var bestScore = 0f;
            var allies = Allies();
            for (var i = 0; i < allies.Count; i++)
            {
                var a = allies[i];
                if (a == null || a == Tank || a.Dead || a.BigBoss == null || a.Health == null)
                {
                    continue;
                }

                var distance = Vector3.Distance(a.Pos(), Pos());
                if (distance > HealReach)
                {
                    continue;
                }

                var fraction = (float)a.Health.Health / Mathf.Max(1, a.Health.MaxHealth);
                var isBoss = a == boss;
                var nearBoss = boss != null && (a.Pos() - boss.Pos()).sqrMagnitude < 60f * 60f;
                var threshold = isBoss ? 0.92f : nearBoss || distance < 40f ? 0.75f : 0.5f;
                if (fraction >= threshold)
                {
                    continue;
                }

                var score = ((1f - fraction) * (isBoss ? 2f : 1f)) + 0.1f - (distance / (HealReach * 4f));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = a;
                }
            }

            return best;
        }

        /// <summary>
        /// The spot an escort holds: behind its anchor as seen from the fight,
        /// a little to one side so two escorts do not share it.
        /// </summary>
        public Vector3 EscortStation(BattleTank anchor)
        {
            if (anchor == null)
            {
                return Vector3.zero;
            }

            var front = FrontAnchor();
            var away = front != Vector3.zero ? anchor.Pos() - front : -anchor.transform.forward;
            away.y = 0f;
            if (away.sqrMagnitude < 1f)
            {
                away = -anchor.transform.forward;
                away.y = 0f;
            }

            if (away.sqrMagnitude < 0.0001f)
            {
                away = Vector3.back;
            }

            away.Normalize();
            // The roster slot picks the side, so two escorts of one anchor usually split.
            var side = Vector3.Cross(Vector3.up, away) * ((Allies().IndexOf(Tank) & 1) == 0 ? 1f : -1f);
            var gap = ModeRole == HealerRole ? EscortGapHealer : EscortGapGraviton;
            var goal = NavigationGoal(anchor.Pos() + (away * gap) + (side * gap * 0.6f));
            if (goal == Vector3.zero)
            {
                goal = NavigationGoal(anchor.Pos() + (away * gap));
            }

            return goal;
        }

        /// <summary>Where the fight is: the spotted enemies, else the living enemy team.</summary>
        public Vector3 FrontAnchor()
        {
            var spotted = AvgEnemiesPos();
            if (spotted != Vector3.zero)
            {
                return spotted;
            }

            var enemies = Enemies();
            var total = Vector3.zero;
            var count = 0;
            for (var i = 0; i < enemies.Count; i++)
            {
                var e = enemies[i];
                if (e == null || e.Dead)
                {
                    continue;
                }

                total += e.Pos();
                count++;
            }

            return count == 0 ? Vector3.zero : total / count;
        }

        /// <summary>
        /// What a charge goes for: a bomber with its blast ready dives at the
        /// spotted enemy with the most others in blast reach (an enemy boss
        /// counts extra), an Uprising rammer at a target worth ramming.
        /// </summary>
        public BattleTank RushTarget()
        {
            if (Tank == null || Tank.Dead)
            {
                return null;
            }

            if (ModeRole == BomberRole)
            {
                var c = Tank.BigBoss;
                var id = c.FamilyAbility("kamikazeAbility");
                if (id == null || c.State.Remaining(id, Time.time) > 0)
                {
                    return null;
                }

                var radius = c.State.Rules.Number(id, "range", 50f);
                BattleTank best = null;
                var bestScore = 0f;
                var enemies = Enemies();
                for (var i = 0; i < enemies.Count; i++)
                {
                    var e = enemies[i];
                    if (!TankIsValid(e))
                    {
                        continue;
                    }

                    var distance = Vector3.Distance(e.Pos(), Pos());
                    if (distance > BomberChargeDistance)
                    {
                        continue;
                    }

                    var score = (Cluster(e, radius) + 1) * 2f
                        + (RoleOf(e) == BossRole ? 1.5f : 0f)
                        - (distance / BomberChargeDistance);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = e;
                    }
                }

                return best;
            }

            return RamWorthy(TargetTank) ? TargetTank : null;
        }

        /// <summary>An Uprising rammer only charges what its hull can break.</summary>
        private bool RamWorthy(BattleTank t)
        {
            if (!HasUprisingPerk("ramming") || !TankIsValid(t) || HealthCoeff() <= 0.35f)
            {
                return false;
            }

            var distance = Vector3.Distance(t.Pos(), Pos());
            if (distance < 4f || distance > 70f)
            {
                return false;
            }

            var mine = Tank.GetComponent<Rigidbody>();
            var theirs = t.GetComponent<Rigidbody>();
            return mine != null && theirs != null && mine.mass >= theirs.mass * 0.9f;
        }

        /// <summary>The bomber's blast is recharging.</summary>
        private bool BomberSpent()
        {
            var c = Tank != null ? Tank.BigBoss : null;
            var id = c != null ? c.FamilyAbility("kamikazeAbility") : null;
            return id != null && c.State.Remaining(id, Time.time) > 0;
        }

        /// <summary>Spotted enemies other than <paramref name="t"/> within <paramref name="radius"/> of it.</summary>
        private int Cluster(BattleTank t, float radius)
        {
            var count = 0;
            var enemies = Enemies();
            for (var i = 0; i < enemies.Count; i++)
            {
                var o = enemies[i];
                if (o != t && TankIsValid(o) && (o.Pos() - t.Pos()).sqrMagnitude <= radius * radius)
                {
                    count++;
                }
            }

            return count;
        }

        private BattleTank NearestSpotted(out float distance)
        {
            distance = float.PositiveInfinity;
            BattleTank best = null;
            var enemies = Enemies();
            for (var i = 0; i < enemies.Count; i++)
            {
                var e = enemies[i];
                if (!TankIsValid(e))
                {
                    continue;
                }

                var d = Vector3.Distance(e.Pos(), Pos());
                if (d < distance)
                {
                    distance = d;
                    best = e;
                }
            }

            return best;
        }

        private int AlliesWithin(float radius)
        {
            var count = 0;
            var allies = Allies();
            for (var i = 0; i < allies.Count; i++)
            {
                var a = allies[i];
                if (a != null && a != Tank && !a.Dead && (a.Pos() - Pos()).sqrMagnitude <= radius * radius)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// The state a mode role calls for, or false to leave the choice to the
        /// engagement layer and the ported scoring.
        /// </summary>
        private bool TryChooseModeState(int spotted, out AiStateKind kind)
        {
            kind = AiStateKind.Rollout;
            if (Tank == null || Tank.Dead || !IsMobile())
            {
                return false;
            }

            float nearest;
            var closest = NearestSpotted(out nearest);
            switch (ModeRole)
            {
                case HealerRole:
                case GravitonRole:
                    // A gun in the face, or a hull about to burn, is fought as usual.
                    if (nearest < EscortThreatDistance || (spotted > 0 && HealthCoeff() < 0.3f))
                    {
                        return false;
                    }

                    if (HealTarget() != null || (EscortAnchor() != null && EscortHasWork()))
                    {
                        kind = AiStateKind.Escort;
                        return true;
                    }

                    return false;
                case BomberRole:
                    if (RushTarget() != null)
                    {
                        kind = AiStateKind.Rush;
                        return true;
                    }

                    if (spotted > 0 && nearest < BomberBreakAwayDistance && BomberSpent())
                    {
                        kind = AiStateKind.Withdraw;
                        return true;
                    }

                    return false;
                case KillerRole:
                    if (closest == null || nearest < CoverFightMinDistance || nearest > CoverFightMaxDistance
                        || HealthCoeff() < 0.15f
                        || !HasLineOfFire()
                        || FindCoverFrom(closest.Pos(), AiStateWithdraw.CoverReach) == Vector3.zero)
                    {
                        return false;
                    }

                    kind = AiStateKind.Withdraw;
                    return true;
                case BossRole:
                    // Five tickets ride on the boss: alone and battered, it falls back.
                    if (spotted >= 2 && HealthCoeff() < 0.6f && AlliesWithin(90f) <= 1)
                    {
                        kind = AiStateKind.Withdraw;
                        return true;
                    }

                    return false;
            }

            if (Tank.Uprising != null && RushTarget() != null)
            {
                kind = AiStateKind.Rush;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether an escort's station still lets its gun join the fight: no
        /// target yet, or a line on it (see <see cref="HasLineOfFire"/>). A
        /// station blind for <see cref="EscortBlindSeconds"/> is given up and
        /// the ported tactics drive the hull to a line; once it has fired, it
        /// is taken back.
        /// </summary>
        private bool EscortHasWork()
        {
            if (!EnemyTargetIsValid() || HasLineOfFire())
            {
                return true;
            }

            return CurrentStateKind == AiStateKind.Escort && Time.time - _stateEnteredAt < EscortBlindSeconds;
        }

        /// <summary>
        /// Whether a cover fight from here has something to shoot at: a shot
        /// went out a moment ago (the peek spot is where it came from), or the
        /// planner has a line on the target right now. Without either, cover
        /// only hides a gun that never fires: the crew has to go and find a
        /// line first.
        /// </summary>
        private bool HasLineOfFire()
        {
            var gun = Tank.Gun;
            if (gun != null && gun.LastShot > 0f)
            {
                var reload = Tank.GunInfo != null ? Tank.GunInfo.Reload : 0f;
                var window = Mathf.Max(CoverFightLineSeconds, reload + AiStateWithdraw.PeekSeconds * 2f);
                if (gun.Ct() - gun.LastShot < window)
                {
                    return true;
                }
            }

            return EnemyTargetIsValid() && ShotPlanFor(TargetTank).Valid;
        }

        /// <summary>
        /// The target preference a mode adds to the ported rating. A hull at
        /// point-blank range comes first in every mode: it is the gun that
        /// hurts, and a crew that keeps shooting something far away while it is
        /// circled reads as broken. A target the ported rating turned down
        /// (<paramref name="rated"/> false: unseen, or not worth it) gets only
        /// that point-blank share.
        /// </summary>
        private float ModeTargetBonus(BattleTank t, bool rated)
        {
            if (Tank == null || t == null)
            {
                return 0f;
            }

            var distance = Vector3.Distance(t.Pos(), Pos());
            var bonus = distance < PointBlankDistance
                ? Mathf.Lerp(8f, 2f, distance / PointBlankDistance)
                : 0f;
            if (!rated)
            {
                return bonus;
            }

            var role = ModeRole;
            if (role != 0)
            {
                if (RoleOf(t) == BossRole)
                {
                    bonus += 6f;
                }

                if (role == HealerRole || role == GravitonRole)
                {
                    var anchor = EscortAnchor();
                    if (anchor != null && (t.Pos() - anchor.Pos()).sqrMagnitude < 80f * 80f)
                    {
                        bonus += 5f;
                    }
                }
                else if (role == BomberRole && distance < 80f)
                {
                    bonus += 4f;
                }
                else if (role == KillerRole && t.BigBoss != null && !t.BigBoss.Poisoned)
                {
                    bonus += 2f;
                }
            }

            if (Tank.Uprising != null)
            {
                if (HasUprisingPerk("splash") || HasUprisingPerk("lightning"))
                {
                    bonus += 2f * Mathf.Min(3, Cluster(t, 25f));
                }

                if (HasUprisingPerk("duplet") && distance < 90f)
                {
                    bonus += 3f;
                }

                if (HasUprisingPerk("ramming") && distance < 60f)
                {
                    bonus += 3f;
                }
            }

            return bonus;
        }

        /// <summary>What the mode multiplies a shooter's alpha by against this victim.</summary>
        private static float ModeDamageFactor(BattleTank shooter, BattleTank victim)
        {
            var factor = 1f;
            var c = shooter != null ? shooter.BigBoss : null;
            if (c != null && c.State != null)
            {
                factor *= c.Param(c.State.Boss ? "bigBoss" : "killerPassive", "commonDamageFactor", 1f);
            }

            var v = victim != null ? victim.BigBoss : null;
            if (v != null && v.State != null)
            {
                factor *= 1f - (v.Param("permanentDamageShield", "damageReductionPercent") / 100f);
            }

            var u = shooter != null ? shooter.Uprising : null;
            if (u != null && u.State != null)
            {
                factor *= u.State.Outgoing(DamageType.Shell, Time.time);
            }

            return Mathf.Max(0f, factor);
        }

        /// <summary>The Uprising reload perks and actives, as a reload time multiplier.</summary>
        private static float ModeReloadFactor(BattleTank shooter)
        {
            var u = shooter != null ? shooter.Uprising : null;
            return u != null && u.State != null ? Mathf.Max(0.05f, u.State.ReloadMultiplier(Time.time)) : 1f;
        }

        /// <summary>An unused Uprising comeback kit survives one more lethal hit.</summary>
        private static float ModeHealthFactor(BattleTank shooter)
        {
            var u = shooter != null ? shooter.Uprising : null;
            return u != null && u.State != null && u.State.Has("comeBackKit") && !u.State.LastStandUsed ? 1.3f : 1f;
        }

        /// <summary>
        /// The hull's share of the traverse, as a right-axis yaw: 1 turns the
        /// hull right, -1 left, 0 leaves the gun to the turret.
        /// <paramref name="errorDegrees"/> is how far the gun still has to
        /// swing, positive to the right: the turret's remaining swing for a
        /// turreted hull, the hull's heading error for a casemate gun, whose
        /// <paramref name="arcDegrees"/> is the traverse limit on that side.
        /// </summary>
        public static float GunTraverseAssist(bool assisting, bool fixedGun, float errorDegrees,
            float distance, float turretDegreesPerSecond, float arcDegrees, float sinceRelease)
        {
            var magnitude = Mathf.Abs(errorDegrees);
            float engage;
            float release;
            if (fixedGun)
            {
                if (distance > FixedGunAssistDistance)
                {
                    return 0f;
                }

                engage = Mathf.Max(arcDegrees - 2f, FaceTolerance);
                release = Mathf.Max(arcDegrees * 0.35f, 2f);
            }
            else
            {
                var seconds = turretDegreesPerSecond > 0.01f
                    ? magnitude / turretDegreesPerSecond
                    : float.PositiveInfinity;
                if (!assisting && (seconds < TraverseAssistMinSeconds
                    || (distance > TraverseAssistDistance && seconds < TraverseAssistSlowSeconds)))
                {
                    return 0f;
                }

                engage = TraverseAssistDegrees;
                release = TraverseReleaseDegrees;
            }

            if (assisting ? magnitude < release : magnitude < engage)
            {
                return 0f;
            }

            if (!assisting && sinceRelease < TraverseAssistCooldown && magnitude < TraverseAssistUrgentDegrees)
            {
                return 0f;
            }

            return errorDegrees > 0f ? 1f : -1f;
        }

        /// <summary>
        /// A slow turret cannot answer a hull that turns up beside or behind it
        /// in time, and a casemate gun cannot answer it at all past the close
        /// threat response: a crew swings the hull into the traverse. Runs after
        /// the state's own drive and replaces only this frame's steering, so
        /// the stored route and facing resume once the gun is on.
        /// </summary>
        private void AssistGunTraverse()
        {
            var turn = 0f;
            if (TraverseAssistApplies())
            {
                var turret = Tank.Turret;
                var to = turret.AimTarget - Pos();
                to.y = 0f;
                var fixedGun = Tank.IsTdNoFullRotation;
                var arc = 180f;
                float error;
                if (fixedGun)
                {
                    var heading = Tank.transform.forward;
                    heading.y = 0f;
                    error = heading.sqrMagnitude > 0.0001f && to.sqrMagnitude > 0.0001f
                        ? Vector3.SignedAngle(heading, to, Vector3.up)
                        : 0f;
                    arc = Mathf.Abs(error >= 0f ? Tank.GunInfo.DeviationRight : Tank.GunInfo.DeviationLeft);
                }
                else
                {
                    var hull = Tank.transform;
                    var target = TankTurret.TurretTargetYaw(turret.GunPosition, turret.AimTarget,
                        hull.forward, hull.up, hull.InverseTransformPoint(turret.AimTarget).x);
                    error = Mathf.DeltaAngle(turret.Yaw * Mathf.Rad2Deg, target * Mathf.Rad2Deg);
                }

                turn = GunTraverseAssist(_traverseAssist, fixedGun, error, to.magnitude,
                    Tank.TurretRotationSpeed * Mathf.Rad2Deg, arc, Time.time - _traverseReleasedAt);
            }

            if (turn == 0f)
            {
                if (_traverseAssist)
                {
                    _traverseAssist = false;
                    _traverseReleasedAt = Time.time;
                }

                return;
            }

            _traverseAssist = true;
            var navigating = _applyingNavigation;
            var facing = _applyingFacing;
            _applyingNavigation = true;
            _applyingFacing = true;
            Drive(0f, YawInput(turn, 0f), true);
            _applyingNavigation = navigating;
            _applyingFacing = facing;
        }

        private bool TraverseAssistApplies()
        {
            if (Tank == null || Tank.Turret == null || Tank.GunInfo == null || Tank.Blocked || Tank.TurretBlock)
            {
                return false;
            }

            // A charge and a retreat keep their own heading; a guided launcher steers itself.
            if (CurrentStateKind == AiStateKind.Rush
                || (CurrentStateKind == AiStateKind.Withdraw && Retreating)
                || (_atgm != null && _atgm.Mode != AtgmAiMode.None))
            {
                return false;
            }

            var escort = CurrentState as AiStateEscort;
            return EnemyTargetIsValid() || (escort != null && escort.Patient != null);
        }

        /// <summary>Spend the support consumables a crew would: repair a broken
        /// track or gun, and boost a charge, a chase, an escort left behind or
        /// a hull running for its life.</summary>
        private void UseSupportConsumables()
        {
            var now = Time.time;
            if (Tank == null || Tank.Dead || now < _consumablesAt)
            {
                return;
            }

            _consumablesAt = now + ConsumableInterval;
            var items = Tank.ActiveConsumables;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.Info == null || !item.Ready(now) || item.IsActive(now))
                {
                    continue;
                }

                var id = item.Info.Id;
                if ((id == ConsumableCatalog.RepairKit && NeedsRepair())
                    || (id == ConsumableCatalog.Afterburning && WantsSpeed()))
                {
                    Tank.UseConsumable(i);
                }
            }
        }

        private bool NeedsRepair()
        {
            return Tank.TracksCrashed
                || Tank.TrackCrashed(TrackSide.Left)
                || Tank.TrackCrashed(TrackSide.Right)
                || Tank.ModuleStatus(TankModuleIds.Gun) == TankModule.Status.Destroyed
                || Tank.ModuleStatus(TankModuleIds.TurretRotator) == TankModule.Status.Destroyed
                || Tank.ModuleStatus(TankModuleIds.Engine) == TankModule.Status.Destroyed;
        }

        private bool WantsSpeed()
        {
            if (!IsMobile())
            {
                return false;
            }

            switch (CurrentStateKind)
            {
                case AiStateKind.Rush:
                case AiStateKind.Chase:
                    return true;
                case AiStateKind.Withdraw:
                    return Retreating && HealthCoeff() < 0.35f;
                case AiStateKind.Escort:
                    var anchor = EscortAnchor();
                    return anchor != null && (anchor.Pos() - Pos()).sqrMagnitude > 60f * 60f;
                default:
                    return false;
            }
        }

        /// <summary>Restart the stall watchdog from here.</summary>
        public void NoteProgress()
        {
            _progressFrom = Pos();
            _progressAt = Time.time;
        }

        /// <summary>
        /// The hull has not got anywhere for <see cref="StallSeconds"/>: a goal
        /// behind a wall the probe keeps turning away from, or a queue behind a
        /// teammate. The roaming state then picks another goal.
        /// </summary>
        public bool Stalled()
        {
            if (_progressAt < 0f || (Pos() - _progressFrom).sqrMagnitude > StallDistance * StallDistance)
            {
                NoteProgress();
                return false;
            }

            return Time.time - _progressAt > StallSeconds;
        }

        /// <summary>
        /// Markers towards the fight first: a random one of the better half (at
        /// most four), ranked by how close it lies to the front and, less, to
        /// the hull, with an occasional uniform pick so a team still spreads.
        /// </summary>
        private BattleAiMarker PickFrontMarker(List<BattleAiMarker> pool)
        {
            var front = FrontAnchor();
            if (front == Vector3.zero || pool.Count <= 2 || Random.value < 0.25f)
            {
                return pool[Random.Range(0, pool.Count)];
            }

            var from = Pos();
            pool.Sort((a, b) => MarkerScore(a, front, from).CompareTo(MarkerScore(b, front, from)));
            return pool[Random.Range(0, Mathf.Clamp((pool.Count + 1) / 2, 1, 4))];
        }

        private static float MarkerScore(BattleAiMarker marker, Vector3 front, Vector3 from)
        {
            var p = marker.transform.position;
            return Vector3.Distance(p, front) + (Vector3.Distance(p, from) * 0.5f);
        }

        /// <summary>
        /// A goal for a map without AI markers (or with all of them used up):
        /// part of the way to the front, offset to one side so a team does not
        /// queue on one line; with no front known, somewhere in the play area.
        /// </summary>
        public Vector3 RoamGoal()
        {
            if (Tank == null)
            {
                return Vector3.zero;
            }

            var from = Pos();
            var front = FrontAnchor();
            for (var attempt = 0; attempt < 4; attempt++)
            {
                Vector3 raw;
                var to = front - from;
                to.y = 0f;
                if (front != Vector3.zero && to.sqrMagnitude > 30f * 30f)
                {
                    var side = Vector3.Cross(Vector3.up, to.normalized);
                    raw = from + (to * Random.Range(0.4f, 0.9f))
                        + (side * Random.Range(-1f, 1f) * Mathf.Min(80f, to.magnitude * 0.35f));
                }
                else
                {
                    var area = Tank.PlayArea;
                    var sized = area.size.x > 0f && area.size.z > 0f;
                    var center = sized ? area.center : from;
                    var radius = sized ? Mathf.Min(area.extents.x, area.extents.z) * 0.6f : 100f;
                    var circle = Random.insideUnitCircle * radius;
                    raw = new Vector3(center.x + circle.x, from.y, center.z + circle.y);
                }

                var goal = NavigationGoal(raw);
                if (goal != Vector3.zero && (goal - from).sqrMagnitude > 15f * 15f)
                {
                    return goal;
                }
            }

            return Vector3.zero;
        }

        /// <summary>
        /// A hull back from a respawn starts from scratch: no stale target,
        /// route, damage memory or marker claim, the new spawn as its home, and
        /// the battle started over, as for a fresh bot.
        /// </summary>
        public void BeginNewLife()
        {
            if (CurrentState != null)
            {
                CurrentState.Exit();
                CurrentState = null;
            }

            ClearTarget(true);
            ClearTarget(false);
            ResetNavigation();
            if (LastChosenMarker != null && LastChosenMarker.Occupier == this)
            {
                LastChosenMarker.Occupy(null);
            }

            LastChosenMarker = null;
            _pendingDamage.Clear();
            _damageFromTanks.Clear();
            _reachedMarkers.Clear();
            _unspottedThere.Clear();
            _delays.Clear();
            EndChase();
            Temper = 0f;
            _fullStopUntil = -1f;
            _shootAt = -1f;
            _recoveryAt = -1f;
            _stuckSince = null;
            _unstuckUntil = -1f;
            _traverseAssist = false;
            _traverseReleasedAt = -100f;
            _consumablesAt = 0f;
            Spawnpoint = Pos();
            NoteProgress();
            StartBattle();
        }
    }
}
