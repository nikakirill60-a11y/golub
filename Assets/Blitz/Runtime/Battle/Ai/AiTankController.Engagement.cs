using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using BlitzLegacy.Runtime.Telemetry;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>What the perception beat last read off the battlefield.</summary>
    public struct AiEngagementPicture
    {
        public float At;

        /// <summary>Enemies the team currently spots.</summary>
        public int Spotted;

        public bool HasTarget;

        /// <summary>Chance to win the one on one against the target.</summary>
        public float DuelOdds;

        /// <summary>Chance the local fight goes our way, allies included.</summary>
        public float ForceOdds;

        /// <summary>Enemies close enough and in sight to shoot at the bot.</summary>
        public int EnemiesEngaging;

        public float TargetDistance;
        public float NearestEnemyDistance;

        /// <summary>The engaging enemy with the highest damage output on the bot.</summary>
        public BattleTank MostDangerous;

        public float MostDangerousDps;

        /// <summary>Damage-weighted centre of the guns pointed at the bot.</summary>
        public Vector3 ThreatPosition;

        /// <summary>The bot's own chance to land a shell on the target.</summary>
        public float MyHitChance;

        /// <summary>The target's chance to penetrate the bot's front.</summary>
        public float TargetFrontPenetration;

        /// <summary>The target's reload left, or -1 when the crew cannot know it.</summary>
        public float TargetReload;
    }

    /// <summary>
    /// The engagement layer over the ported state machine. A throttled,
    /// staggered perception beat sizes up the fight - kill times, Lanchester
    /// force odds, which guns can actually reach the hull - and lets the bot
    /// commit to a duel, pull back into cover, or hunt a target that slipped
    /// out of sight. Every query here is either plain arithmetic or a cached,
    /// budgeted ray, so a full room of bots stays cheap.
    /// </summary>
    public sealed partial class AiTankController
    {
        /// <summary>Enemies further than this do not count as engaging.</summary>
        public const float EngagementRadius = 450f;

        /// <summary>Closer than this an enemy engages whatever the sight line says.</summary>
        public const float EngagementPointBlank = 70f;

        /// <summary>Fresh sight lines a single beat may trace.</summary>
        public const int EngagementLosChecks = 4;

        public const float EngagementLosCacheSeconds = 1f;

        /// <summary>Allies within this radius join the force tally.</summary>
        public const float AllySupportRadius = 150f;

        /// <summary>A lost target is hunted at most this long after it vanished.</summary>
        public const float ChaseMemorySeconds = 11f;

        /// <summary>The furthest the hunt extrapolates the lost target's motion.</summary>
        public const float ChaseLeadSeconds = 4f;

        public const float UnseenHitMemorySeconds = 3f;

        /// <summary>A withdrawn bot keeps its cover this long after the last contact.</summary>
        public const float WithdrawHoldSeconds = 9f;

        public const float ThreatCueSeconds = 8f;
        public const float CoverCacheSeconds = 1.25f;

        /// <summary>The hull counts as facing its goal inside this many degrees.</summary>
        public const float FaceTolerance = 7f;

        private const float ThreatEyeHeight = 2.2f;
        private const int CoverPathChecks = 3;
        private const int LosMemoLimit = 64;
        private static readonly float[] CoverRadii = { 12f, 24f, 40f };
        private static readonly float[] CoverAngles = { 0f, 45f, -45f, 90f, -90f, 135f, -135f };

        private struct LosMemo
        {
            public bool Visible;
            public float Until;
        }

        private struct FacingRequest
        {
            public bool Valid;
            public BattleTank Tank;
            public Vector3 Point;
            public float Angle;
            public float Forward;
            public float RequestedAt;
        }

        private readonly Dictionary<BattleTank, LosMemo> _losMemo =
            new Dictionary<BattleTank, LosMemo>();
        private readonly List<BattleTank> _targetOrder = new List<BattleTank>();
        private AiEngagementPicture _picture;
        private float _perceptionAt = -1f;
        private int _perceivedSpotted;
        private float _perceivedHealth = 1f;
        private float _stateEnteredAt;
        private float _lastContactAt = -100f;
        private float _lastContactForceOdds = 0.5f;
        private Vector3 _lastThreat;
        private BattleTank _quarry;
        private Vector3 _quarryLastSeen;
        private Vector3 _quarryVelocity;
        private float _quarryLastSeenAt = -100f;
        private float _quarryOdds;
        private Vector3 _threatCue;
        private float _threatCueAt = -100f;
        private float _unseenHitAt = -100f;
        private Vector3 _coverCache;
        private Vector3 _coverCacheThreat;
        private float _coverCacheReach;
        private float _coverCacheUntil = -1f;
        private FacingRequest _facing;
        private bool _applyingFacing;

        /// <summary>The last perception beat.</summary>
        public AiEngagementPicture Engagement
        {
            get { return _picture; }
        }

        /// <summary>When the current state was entered.</summary>
        public float StateEnteredAt
        {
            get { return _stateEnteredAt; }
        }

        /// <summary>The target the bot last fought, kept after it vanished.</summary>
        public BattleTank Quarry
        {
            get { return _quarry; }
        }

        public Vector3 QuarryLastSeen
        {
            get { return _quarryLastSeen; }
        }

        public float QuarryLastSeenAt
        {
            get { return _quarryLastSeenAt; }
        }

        /// <summary>Where the guns were when the bot last had contact.</summary>
        public Vector3 LastThreatPosition
        {
            get { return _lastThreat; }
        }

        /// <summary>A hull that may drive at all.</summary>
        public bool IsMobile()
        {
            return Tank != null && !BotsStationary && !SandboxTank.IsStationary(Tank);
        }

        /// <summary>
        /// Pull the next tactical judgement forward to one reaction time from
        /// now, the moment a player would have registered the change.
        /// </summary>
        public void RequestTacticalSoon()
        {
            var due = Time.time + ReactionTime() - AiTactics.TacticalInterval;
            if (due < _lastTacticalAt)
            {
                _lastTacticalAt = due;
            }
        }

        /// <summary>The throttled beat; FixedUpdate calls it every frame.</summary>
        private void UpdatePerception()
        {
            var now = Time.time;
            if (now < _perceptionAt)
            {
                return;
            }

            _perceptionAt = now
                + (AiEngagementMath.PerceptionInterval(Skill) * Random.Range(0.85f, 1.15f));
            var wasDueling = CurrentStateKind == AiStateKind.Duel;
            RefreshPicture();

            var health = HealthCoeff();
            var newContact = _picture.Spotted > _perceivedSpotted;
            var hurt = health < _perceivedHealth - 0.15f;
            var collapsing = wasDueling && _picture.HasTarget
                && (_picture.DuelOdds < AiEngagementMath.WithdrawDuelEnter
                    || _picture.ForceOdds < AiEngagementMath.WithdrawForceEnter);
            var lostDuel = wasDueling && !_picture.HasTarget;
            if (newContact || hurt || collapsing || lostDuel)
            {
                RequestTacticalSoon();
            }

            _perceivedSpotted = _picture.Spotted;
            _perceivedHealth = health;
        }

        /// <summary>Re-read the fight: odds, engaging guns, threat centre.</summary>
        private void RefreshPicture()
        {
            var now = Time.time;
            var picture = new AiEngagementPicture
            {
                At = now,
                DuelOdds = 0.5f,
                ForceOdds = 0.5f,
                TargetReload = -1f,
                NearestEnemyDistance = float.PositiveInfinity,
            };

            _dangerCache.Clear();
            if (Tank == null || Tank.Health == null || Tank.Dead)
            {
                _picture = picture;
                return;
            }

            if (_losMemo.Count > LosMemoLimit)
            {
                _losMemo.Clear();
            }

            var me = Pos();
            var target = EnemyTargetIsValid() ? TargetTank : null;
            var reloadKnown = SkillReached(AiSkillLevel.BlueArmy);
            var losBudget = EngagementLosChecks;
            var theirDps = 0f;
            var theirHealth = 0f;
            var threatSum = Vector3.zero;
            var threatWeight = 0f;

            var enemies = Enemies();
            for (var i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];
                if (!TankIsValid(enemy))
                {
                    continue;
                }

                picture.Spotted++;
                var distance = Vector3.Distance(enemy.Pos(), me);
                if (distance < picture.NearestEnemyDistance)
                {
                    picture.NearestEnemyDistance = distance;
                }

                if (distance > EngagementRadius && enemy != target)
                {
                    continue;
                }

                var engaging = enemy == target
                    || distance < EngagementPointBlank
                    || HasLineOfSight(enemy, ref losBudget);
                if (!engaging)
                {
                    continue;
                }

                picture.EnemiesEngaging++;
                var them = CombatantOf(enemy, Tank, distance, reloadKnown);
                var dps = AiEngagementMath.ExpectedDps(them);
                theirDps += dps;
                theirHealth += them.Health;
                var weight = Mathf.Max(0.05f, dps);
                threatSum += enemy.Pos() * weight;
                threatWeight += weight;
                if (picture.MostDangerous == null || dps > picture.MostDangerousDps)
                {
                    picture.MostDangerousDps = dps;
                    picture.MostDangerous = enemy;
                }
            }

            var reference = target != null ? target : picture.MostDangerous;
            var referenceDistance = reference != null
                ? Vector3.Distance(reference.Pos(), me)
                : 150f;
            var mine = CombatantOf(Tank, reference, referenceDistance, true);

            // A Sandbox HARMLESS bot only skips the trigger: it still plays
            // the fight it pretends to have instead of hiding forever.
            mine.Harmless = false;
            var ourDps = AiEngagementMath.ExpectedDps(mine);
            var ourHealth = mine.Health;
            var allies = Allies();
            for (var i = 0; i < allies.Count; i++)
            {
                var ally = allies[i];
                if (ally == null || ally == Tank || ally.Dead || ally.Health == null)
                {
                    continue;
                }

                if ((ally.Pos() - me).sqrMagnitude > AllySupportRadius * AllySupportRadius)
                {
                    continue;
                }

                var allyDistance = reference != null
                    ? Vector3.Distance(ally.Pos(), reference.Pos())
                    : referenceDistance;
                var help = CombatantOf(ally, reference, allyDistance, false);
                ourDps += AiEngagementMath.ExpectedDps(help);
                ourHealth += help.Health;
            }

            picture.ForceOdds = AiEngagementMath.ForceOdds(ourDps, ourHealth, theirDps, theirHealth);
            picture.ThreatPosition = threatWeight > 0f
                ? threatSum / threatWeight
                : (target != null ? target.Pos() : Vector3.zero);

            if (target != null)
            {
                picture.HasTarget = true;
                picture.TargetDistance = Vector3.Distance(target.Pos(), me);
                var theirs = CombatantOf(target, Tank, picture.TargetDistance, reloadKnown);
                var myDuel = reference == target
                    ? mine
                    : CombatantOf(Tank, target, picture.TargetDistance, true);
                myDuel.Harmless = false;
                picture.DuelOdds = AiEngagementMath.DuelOdds(myDuel, theirs);
                picture.MyHitChance = myDuel.HitChance;
                picture.TargetFrontPenetration = FrontPenetrationChance(target, Tank);
                if (reloadKnown && target.Gun != null)
                {
                    picture.TargetReload = target.Gun.CanShoot()
                        ? 0f
                        : Mathf.Max(0f, target.Gun.ReloadRemaining());
                }

                _quarry = target;
                _quarryLastSeen = target.Pos();
                _quarryVelocity = target.LinearVelocity;
                _quarryLastSeenAt = now;
                _quarryOdds = picture.DuelOdds;
            }

            if (picture.Spotted > 0)
            {
                _lastContactAt = now;
                _lastContactForceOdds = picture.ForceOdds;
                if (picture.ThreatPosition != Vector3.zero)
                {
                    _lastThreat = picture.ThreatPosition;
                }
            }

            _picture = picture;
        }

        /// <summary>A cached, budgeted sight line from my gun to theirs.</summary>
        private bool HasLineOfSight(BattleTank enemy, ref int budget)
        {
            var now = Time.time;
            LosMemo memo;
            var known = _losMemo.TryGetValue(enemy, out memo);
            if (known && now < memo.Until)
            {
                return memo.Visible;
            }

            if (budget <= 0)
            {
                return known && memo.Visible;
            }

            budget--;
            var visible = !Obstructed(Tank.SpotPosition(), enemy.SpotPosition());
            _losMemo[enemy] = new LosMemo
            {
                Visible = visible,
                Until = now + (EngagementLosCacheSeconds * Random.Range(0.8f, 1.2f)),
            };
            return visible;
        }

        /// <summary>
        /// What <paramref name="shooter"/> brings against
        /// <paramref name="victim"/> at this range. Sandbox buffs need no
        /// special case: health, penetration, reload and aim time are already
        /// baked into the hull, and only the outgoing damage multiplier is
        /// applied on top.
        /// </summary>
        public AiCombatant CombatantOf(
            BattleTank shooter, BattleTank victim, float distance, bool reloadKnown)
        {
            var c = new AiCombatant { Health = 1f, SecondsPerShot = 10f };
            if (shooter == null)
            {
                c.Harmless = true;
                return c;
            }

            if (shooter.Health != null)
            {
                c.Health = Mathf.Max(1f, shooter.Health.Health) * ModeHealthFactor(shooter);
            }

            var gun = shooter.GunInfo;
            var shell = shooter.CurrentShell();
            if (gun == null || shell == null)
            {
                c.Harmless = true;
                return c;
            }

            var sandbox = SandboxTank.Of(shooter);
            c.Alpha = shell.Damage * (sandbox != null ? sandbox.DamageMultiplier : 1f)
                * ModeDamageFactor(shooter, victim);
            var reload = (shooter.Gun != null ? shooter.Gun.ReloadTotal : gun.Reload)
                * ModeReloadFactor(shooter);
            c.SecondsPerShot = AiEngagementMath.SecondsPerShot(
                reload,
                ClipShells(gun),
                gun.Burst != null ? gun.Burst.IntraClip : 0f,
                AutoReloadAverage(gun));
            if (reloadKnown && shooter.Gun != null && !shooter.Gun.CanShoot())
            {
                c.ReloadRemaining = Mathf.Clamp(
                    shooter.Gun.ReloadRemaining(), 0f, Mathf.Max(0f, reload));
            }

            c.AimSeconds = Mathf.Clamp(gun.AimTime * 0.5f, 0.2f, 2f);

            // A guided rocket homes onto the crosshair, so its cone matters less.
            var dispersion = shell.Atgm ? gun.Dispersion * 0.5f : gun.Dispersion;
            c.HitChance = AiEngagementMath.HitChance(
                dispersion, distance, victim != null ? TargetRadius(victim) : 1.3f);
            c.PenetrationChance = AiEngagementMath.PenetrationChance(
                shell.Penetration, victim != null ? EffectiveArmor(victim, shooter) : 0f);
            c.NonPenetrationShare = shell.Type == ShellType.He ? 0.35f : 0f;
            c.Harmless = sandbox != null && sandbox.Harmless;
            return c;
        }

        /// <summary>The chance <paramref name="shooter"/> gets through the victim's front.</summary>
        public static float FrontPenetrationChance(BattleTank shooter, BattleTank victim)
        {
            var shell = shooter != null ? shooter.CurrentShell() : null;
            if (shell == null || victim == null)
            {
                return 0f;
            }

            return AiEngagementMath.PenetrationChance(shell.Penetration, victim.AvgArmor * 1.25f);
        }

        /// <summary>Half the visible silhouette: what a shell has to land on.</summary>
        private static float TargetRadius(BattleTank victim)
        {
            var area = victim.Hull.Height * victim.HullWidth;
            return 0.5f * Mathf.Sqrt(Mathf.Max(1f, area));
        }

        /// <summary>
        /// The averaged plate as the shooter meets it: the front adds its slope,
        /// a flank or the rear gives some of it away.
        /// </summary>
        private static float EffectiveArmor(BattleTank victim, BattleTank shooter)
        {
            float armor = victim.AvgArmor;
            if (shooter == null)
            {
                return armor;
            }

            var to = shooter.Pos() - victim.Pos();
            to.y = 0f;
            var forward = victim.transform.forward;
            forward.y = 0f;
            if (to.sqrMagnitude <= 0.01f || forward.sqrMagnitude <= 0.0001f)
            {
                return armor;
            }

            var facing = Vector3.Dot(forward.normalized, to.normalized);
            if (facing > 0.5f)
            {
                return armor * 1.25f;
            }

            return armor * (facing < -0.5f ? 0.7f : 0.85f);
        }

        private static int ClipShells(GunInfo gun)
        {
            if (!SandboxTank.UsesMagazine(gun) || gun.Burst == null || !gun.Burst.HasBursts)
            {
                return 1;
            }

            var shells = 0;
            for (var i = 0; i < gun.Burst.Bursts.Count; i++)
            {
                shells += Mathf.Max(0, gun.Burst.Bursts[i]);
            }

            return Mathf.Max(1, shells);
        }

        private static float AutoReloadAverage(GunInfo gun)
        {
            var count = gun.AutoReloadTimes.Count;
            if (count == 0)
            {
                return 0f;
            }

            var total = 0f;
            for (var i = 0; i < count; i++)
            {
                total += gun.AutoReloadTimes[i];
            }

            return total / count;
        }

        /// <summary>
        /// The smart crew's extra rating: finish, punish a reload, answer the
        /// gun that hurts, stay on the current target, skip what it cannot pen.
        /// </summary>
        private float TargetPriorityBonus(BattleTank t)
        {
            if (Tank == null || t == null || t.Health == null)
            {
                return 0f;
            }

            var distance = Vector3.Distance(t.Pos(), Pos());
            var reloadKnown = SkillReached(AiSkillLevel.BlueArmy);
            var mine = CombatantOf(Tank, t, distance, true);
            var theirs = CombatantOf(t, Tank, distance, reloadKnown);
            var theirDps = AiEngagementMath.ExpectedDps(theirs);
            var worst = Mathf.Max(theirDps, _picture.MostDangerousDps);
            var reload = -1f;
            if (reloadKnown && t.Gun != null && !t.Gun.CanShoot())
            {
                reload = Mathf.Max(0f, t.Gun.ReloadRemaining());
            }

            return AiEngagementMath.TargetPriorityBonus(new AiTargetPriorityInputs
            {
                Current = t == TargetTank,
                ShotsToKill = AiEngagementMath.ShotsToKill(mine.Alpha, t.Health.Health),
                PenetrationChance = Mathf.Max(mine.PenetrationChance, mine.NonPenetrationShare),
                ReloadRemaining = reload,
                ThreatShare = worst > 0f ? theirDps / worst : 0f,
                Distance = distance,
            });
        }

        private static AiEngagement EngagementOf(AiStateKind kind)
        {
            switch (kind)
            {
                case AiStateKind.Duel: return AiEngagement.Duel;
                case AiStateKind.Withdraw: return AiEngagement.Withdraw;
                case AiStateKind.Chase: return AiEngagement.Chase;
                default: return AiEngagement.None;
            }
        }

        /// <summary>A lost target is still worth hunting.</summary>
        private bool QuarryFresh()
        {
            return _quarry != null
                && !_quarry.Dead
                && !TankIsValid(_quarry)
                && Time.time - _quarryLastSeenAt < ChaseMemorySeconds;
        }

        /// <summary>The hunt is over: forget the lost target.</summary>
        public void EndChase()
        {
            _quarry = null;
            _quarryLastSeenAt = -100f;
        }

        /// <summary>Where the lost target should be by now.</summary>
        public Vector3 PredictedQuarryPosition()
        {
            if (_quarry == null)
            {
                return Vector3.zero;
            }

            var elapsed = Mathf.Clamp(Time.time - _quarryLastSeenAt, 0f, ChaseLeadSeconds);
            var lead = _quarryVelocity;
            lead.y = 0f;
            return _quarryLastSeen + (lead * elapsed);
        }

        /// <summary>
        /// The engagement the odds call for, or false to leave the choice to
        /// the ported scoring.
        /// </summary>
        private bool TryChooseEngagementState(int spotted, out AiStateKind kind)
        {
            kind = AiStateKind.Rollout;
            if (Tank == null || Tank.Health == null)
            {
                return false;
            }

            var now = Time.time;
            if (spotted > 0 && !EnemyTargetIsValid())
            {
                SearchForTargetTankRating(true);
                RefreshPicture();
            }
            else if (now - _picture.At > 0.3f || spotted != _picture.Spotted)
            {
                // A gun appearing or vanishing changes the fight at once.
                RefreshPicture();
            }

            var inputs = new AiEngagementInputs
            {
                Current = EngagementOf(CurrentStateKind),
                CurrentFor = now - _stateEnteredAt,
                Mobile = IsMobile(),
                Atgm = _atgm != null && _atgm.Mode != AtgmAiMode.None,
                Smart = SkillReached(AiSkillLevel.GreenArmy),
                HasTarget = _picture.HasTarget,
                DuelOdds = _picture.DuelOdds,
                ForceOdds = _picture.Spotted > 0 ? _picture.ForceOdds : _lastContactForceOdds,
                EnemiesEngaging = _picture.EnemiesEngaging,
                TargetDistance = _picture.TargetDistance,
                DuelRange = AiEngagementMath.DuelRange(PlayStyle),
                HealthFraction = HealthCoeff(),
                QuarryFresh = QuarryFresh(),
                QuarryOdds = _quarryOdds,
                QuarryDistance = _quarry != null
                    ? Vector3.Distance(Pos(), _quarryLastSeen)
                    : float.PositiveInfinity,
                HitByUnseen = now - _unseenHitAt < UnseenHitMemorySeconds,
                HoldingAfterWithdraw = now - _lastContactAt < WithdrawHoldSeconds,
            };

            switch (AiEngagementMath.ChooseEngagement(inputs))
            {
                case AiEngagement.Duel:
                    kind = AiStateKind.Duel;
                    return true;
                case AiEngagement.Withdraw:
                    kind = AiStateKind.Withdraw;
                    return true;
                case AiEngagement.Chase:
                    kind = AiStateKind.Chase;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A shell from an enemy: remember roughly where it came from, even
        /// when nobody can see the gun, the way a player reads the hit marker.
        /// </summary>
        private void NoteHit(BattleTank attacker)
        {
            if (Tank == null || attacker == null || attacker.Team == Tank.Team)
            {
                return;
            }

            var now = Time.time;
            var jitter = Random.insideUnitSphere * 12f;
            jitter.y = 0f;
            var source = attacker.Pos() + jitter;
            NoteThreatCue(source);
            if (!attacker.Spotted && SkillReached(AiSkillLevel.GreenArmy))
            {
                _unseenHitAt = now;
                _lastContactAt = now;
                _lastThreat = source;
                RequestTacticalSoon();
            }
        }

        /// <summary>Somewhere an enemy is expected: the turret pre-aims there.</summary>
        private void NoteThreatCue(Vector3 position)
        {
            _threatCue = position;
            _threatCueAt = Time.time;
        }

        /// <summary>A fresh spot an enemy was last heard of.</summary>
        public bool TryThreatCue(out Vector3 cue)
        {
            cue = _threatCue;
            if (_threatCue == Vector3.zero || Time.time - _threatCueAt > ThreatCueSeconds)
            {
                return false;
            }

            return (cue - Pos()).sqrMagnitude > 15f * 15f;
        }

        /// <summary>
        /// The steering input that yaws the hull clockwise for a positive
        /// <paramref name="yaw"/>: base_tank.drive mirrors the stick while the
        /// hull reverses, so a reversing hull needs the opposite input.
        /// </summary>
        private static float YawInput(float yaw, float forward)
        {
            return forward < 0f ? -yaw : yaw;
        }

        /// <summary>
        /// One steering step that turns the front plate onto
        /// <paramref name="point"/>, offset by <paramref name="angle"/> degrees
        /// (positive turns the hull clockwise of the line). The hull pivots
        /// first and only creeps by <paramref name="forward"/> once it is
        /// close to the heading, so the front never swings away from the gun
        /// it is meant to face.
        /// </summary>
        public bool FaceTowards(Vector3 point, float angle, float forward = 0f)
        {
            if (Tank == null)
            {
                return false;
            }

            var to = point - Pos();
            to.y = 0f;
            if (to.sqrMagnitude < 1f)
            {
                Drive(0f, 0f, true);
                return true;
            }

            var desired = Quaternion.AngleAxis(angle, Vector3.up) * to.normalized;
            var heading = Tank.transform.forward;
            heading.y = 0f;
            if (heading.sqrMagnitude <= 0.0001f)
            {
                return false;
            }

            var error = Vector3.SignedAngle(heading.normalized, desired, Vector3.up);
            var magnitude = Mathf.Abs(error);
            var move = magnitude <= 25f ? forward : 0f;
            if (move > 0f && ObstacleAhead())
            {
                move = 0f;
            }

            var turn = magnitude > FaceTolerance ? Mathf.Sign(error) : 0f;
            if (turn != 0f && magnitude > 12f)
            {
                move = 0f;
            }

            var rotation = YawInput(turn, move);
            var recovering = TryUnstuck(move == 0f && forward > 0f && ObstacleAhead(),
                ref move, ref rotation);
            Drive(move, rotation, true);
            return !recovering && magnitude <= FaceTolerance;
        }

        /// <summary>
        /// Keep facing <paramref name="tank"/> (or <paramref name="point"/>
        /// when no tank is given) every physics frame until another drive
        /// command replaces the request or it goes stale.
        /// </summary>
        public void HoldFacing(BattleTank tank, Vector3 point, float angle, float forward = 0f)
        {
            _navigationDrive.Valid = false;
            _facing = new FacingRequest
            {
                Valid = true,
                Tank = tank,
                Point = point,
                Angle = angle,
                Forward = forward,
                RequestedAt = Time.time,
            };
            ApplyFacing();
        }

        private void UpdateFacing()
        {
            if (!_facing.Valid)
            {
                return;
            }

            if (!Active || Tank == null || Tank.Dead
                || Time.time - _facing.RequestedAt > NavigationDriveStale)
            {
                _facing.Valid = false;
                return;
            }

            ApplyFacing();
        }

        private void ApplyFacing()
        {
            var point = _facing.Tank != null && !_facing.Tank.Dead
                ? _facing.Tank.Pos()
                : _facing.Point;
            _applyingFacing = true;
            FaceTowards(point, _facing.Angle, _facing.Forward);
            _applyingFacing = false;
        }

        /// <summary>
        /// A spot within <paramref name="maxDistance"/> that the map hides from
        /// <paramref name="threat"/>: a source hide object when one is close
        /// and really out of the threat's sight, otherwise the first
        /// reachable, concealed point on rings around the hull, tried away
        /// from the threat first. Cached briefly, and capped at a few path
        /// probes, because a withdrawing bot asks every think.
        /// </summary>
        public Vector3 FindCoverFrom(Vector3 threat, float maxDistance = 45f)
        {
            if (Tank == null || threat == Vector3.zero)
            {
                return Vector3.zero;
            }

            var now = Time.time;
            if (now < _coverCacheUntil
                && Mathf.Approximately(_coverCacheReach, maxDistance)
                && (threat - _coverCacheThreat).sqrMagnitude < 15f * 15f)
            {
                return _coverCache;
            }

            using var slowScope = BlitzPerfMonitor.Slow("ai.cover");
            _coverCacheThreat = threat;
            _coverCacheReach = maxDistance;
            _coverCacheUntil = now + CoverCacheSeconds;
            _coverCache = SearchCover(threat, maxDistance);
            return _coverCache;
        }

        private Vector3 SearchCover(Vector3 threat, float maxDistance)
        {
            var me = Pos();
            var eye = threat + (Vector3.up * ThreatEyeHeight);
            var limit = maxDistance * maxDistance;

            var spot = FindHidingSpot(threat);
            if (spot != Vector3.zero
                && (spot - me).sqrMagnitude <= limit * 1.5f
                && Concealed(eye, spot))
            {
                return spot;
            }

            var away = me - threat;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
            {
                away = -Tank.transform.forward;
                away.y = 0f;
            }

            if (away.sqrMagnitude < 0.0001f)
            {
                return Vector3.zero;
            }

            away.Normalize();
            var navigation = BattleNavigation.Current;
            var navReady = navigation != null && navigation.Ready;
            var myThreatGap = (me - threat).sqrMagnitude;
            var pathChecks = 0;
            for (var r = 0; r < CoverRadii.Length; r++)
            {
                var radius = CoverRadii[r];
                if (radius > maxDistance)
                {
                    break;
                }

                for (var a = 0; a < CoverAngles.Length; a++)
                {
                    var direction = Quaternion.AngleAxis(CoverAngles[a], Vector3.up) * away;
                    var candidate = me + (direction * radius);
                    Vector3 ground;
                    if (navReady)
                    {
                        if (!navigation.ClosestPoint(candidate, out ground))
                        {
                            continue;
                        }

                        var drift = new Vector2(ground.x - candidate.x, ground.z - candidate.z);
                        if (drift.sqrMagnitude > 36f)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        ground = NavigationGoalWithoutNavmesh(candidate);
                        if (ground == Vector3.zero)
                        {
                            continue;
                        }
                    }

                    // Never run at the guns to hide.
                    if ((ground - threat).sqrMagnitude < myThreatGap * 0.7f)
                    {
                        continue;
                    }

                    if (!Concealed(eye, ground))
                    {
                        continue;
                    }

                    if (pathChecks >= CoverPathChecks)
                    {
                        return Vector3.zero;
                    }

                    pathChecks++;
                    var goal = NavigationGoal(ground);
                    if (goal != Vector3.zero)
                    {
                        return goal;
                    }
                }
            }

            return Vector3.zero;
        }

        /// <summary>The map blocks the line from the threat's gun to my turret roof.</summary>
        private bool Concealed(Vector3 eye, Vector3 ground)
        {
            var height = Tank != null ? Mathf.Max(1.2f, Tank.Hull.Height * 0.9f) : 1.5f;
            return Obstructed(eye, ground + (Vector3.up * height));
        }

        /// <summary>Is <paramref name="position"/> still hidden from <paramref name="threat"/>?</summary>
        public bool CoverHides(Vector3 position, Vector3 threat)
        {
            return position != Vector3.zero
                && threat != Vector3.zero
                && Concealed(threat + (Vector3.up * ThreatEyeHeight), position);
        }

        /// <summary>
        /// Where to fall back when no cover is near: away from the threat,
        /// leaning towards the own spawn and the team, onto reachable ground.
        /// </summary>
        public Vector3 RetreatPoint(Vector3 threat, float distance = 40f)
        {
            if (Tank == null || threat == Vector3.zero)
            {
                return Vector3.zero;
            }

            var me = Pos();
            var away = me - threat;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
            {
                return Vector3.zero;
            }

            away.Normalize();
            var home = Spawnpoint - me;
            home.y = 0f;
            var direction = away;
            if (home.sqrMagnitude > 30f * 30f)
            {
                home.Normalize();
                if (Vector3.Dot(home, away) > -0.3f)
                {
                    direction = ((away * 0.6f) + (home * 0.4f)).normalized;
                }
            }

            var goal = NavigationGoal(me + (direction * distance));
            if (goal != Vector3.zero)
            {
                return goal;
            }

            var side = Random.value < 0.5f ? -45f : 45f;
            goal = NavigationGoal(me + (Quaternion.AngleAxis(side, Vector3.up) * away * distance * 0.6f));
            if (goal != Vector3.zero)
            {
                return goal;
            }

            return NavigationGoal(me + (Quaternion.AngleAxis(-side, Vector3.up) * away * distance * 0.6f));
        }
    }
}