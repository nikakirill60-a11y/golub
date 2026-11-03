using System.Collections.Generic;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using BlitzLegacy.Runtime.Telemetry;
using UnityEngine;
using UnityEngine.AI;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// <c>ai_controller.gd</c>: one bot brain per hull. It holds the target,
    /// the marker bookkeeping, the visibility traces, the damage memory and the
    /// state machine, and every state drives through it.
    ///
    /// Rudniki carries no authored golden paths or exported navmesh, so goals
    /// use straight-line steering with a terrain projection and a hull obstacle
    /// probe. Its source <c>navmesh_obstacle</c> and <c>ai_nohide</c> groups are
    /// nevertheless preserved, allowing <c>find_hiding_spot</c> to keep the
    /// original cover-selection arithmetic.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class AiTankController : MonoBehaviour
    {
        /// <summary>The <c>Util.timer(0.1)</c> of <c>init</c>.</summary>
        public const float FinishInitDelay = 0.1f;

        /// <summary>A navigation drive older than this stops the hull.</summary>
        public const float NavigationDriveStale = 1f;

        /// <summary>How often the obstacle probe in front of the hull runs.</summary>
        public const float ObstacleCheckInterval = 0.35f;

        /// <summary>
        /// How long a hull keeps steering to the side it picked once something
        /// blocks it. Without the commitment the goal heading wins the wheel
        /// back the moment the nose is clear, the hull turns straight into the
        /// same wreck again, and it grinds against the hulk on the spot.
        /// </summary>
        public const float AvoidCommitTime = 1.1f;

        /// <summary>cos 45 deg: steeper than the bake's 25 deg limit, flatter than a wall.</summary>
        public const float DrivableNormalY = 0.7f;

        /// <summary>How long a hull may push forward without moving before it backs out.</summary>
        public const float StuckTime = 2.5f;

        /// <summary>How long the reverse-and-turn manoeuvre lasts once stuck.</summary>
        public const float UnstuckTime = 1.2f;

        /// <summary>Horizontal progress, in metres, that counts as moving.</summary>
        public const float StuckProgress = 1f;

        /// <summary><c>NAV_UNREACHABLE_GOAL_COOLDOWN</c>.</summary>
        public const float NavUnreachableGoalCooldown = 1f;

        /// <summary><c>NAV_UNREACHABLE_GOAL_RADIUS_SQR</c>.</summary>
        public const float NavUnreachableGoalRadiusSquared = 4f;

        /// <summary><c>MOBILE_NAV_REPATH_INTERVAL</c>.</summary>
        public const float MobileNavRepathInterval = 0.2f;

        /// <summary><c>MOBILE_NAV_URGENT_SHIFT_SQR</c>.</summary>
        public const float MobileNavUrgentShiftSquared = 25f;

        public const float HideNavigationCacheSeconds = 0.75f;
        public const float HideUnreachableRetrySeconds = 3f;
        public const float HideObjectMaxDistance = 400f;
        public const float HideEnemyExclusionDistanceSquared = 150f * 150f * 3f;
        public const float HideDirectionDotThreshold = 0.3f;

        /// <summary>BOTS HOLD POSITION, the source's debug switch.</summary>
        public static bool BotsStationary;

        /// <summary>
        /// Hands the save's BOTS HOLD POSITION row to the bot brains. The
        /// switch is static because the source keeps it global, so a battle
        /// applies it once on the way in.
        /// </summary>
        public static void ApplySettings(SettingsData settings)
        {
            BotsStationary = settings != null && settings.BotsStationary;
        }

        public BattleTank Tank { get; private set; }

        public AiSkillLevel Skill { get; private set; }

        public AiPlayStyle PlayStyle { get; private set; }

        /// <summary><c>active</c>: a dead or idle bot stops thinking.</summary>
        public bool Active { get; private set; }

        public AiState CurrentState { get; private set; }

        public AiStateKind CurrentStateKind { get; private set; }

        /// <summary><c>target_tank</c>.</summary>
        [System.NonSerialized]
        public BattleTank TargetTank;

        /// <summary><c>target_position</c>, the drive goal.</summary>
        [System.NonSerialized]
        public Vector3 TargetPosition;

        /// <summary><c>last_chosen_marker</c>.</summary>
        [System.NonSerialized]
        public BattleAiMarker LastChosenMarker;

        /// <summary><c>spawnpoint</c>.</summary>
        public Vector3 Spawnpoint { get; private set; }

        /// <summary><c>temper</c>, which damage taken pushes down.</summary>
        public float Temper { get; private set; }

        /// <summary><c>tanks_unspotted_there</c>: where an enemy was last seen.</summary>
        public List<Vector3> TanksUnspottedThere
        {
            get { return _unspottedThere; }
        }

        private readonly Dictionary<string, float> _delays = new Dictionary<string, float>();
        private readonly Dictionary<BattleTank, float> _damageFromTanks =
            new Dictionary<BattleTank, float>();
        private readonly Dictionary<BattleTank, int> _dangerCache =
            new Dictionary<BattleTank, int>();
        private readonly List<Vector3> _unspottedThere = new List<Vector3>();
        private readonly List<BattleAiMarker> _reachedMarkers = new List<BattleAiMarker>();
        private readonly List<PendingDamage> _pendingDamage = new List<PendingDamage>();
        private AtgmAiController _atgm;
        private readonly Dictionary<BattleAiCover, HideNavigationCache> _hideNavigationCache =
            new Dictionary<BattleAiCover, HideNavigationCache>();
        private readonly Dictionary<BattleAiCover, float> _hideObjectBlockedUntil =
            new Dictionary<BattleAiCover, float>();

        private float _fullStopUntil;
        private float _damageTakenLast;
        private float _initAt = -1f;
        private bool _initDone;
        private float _lastTacticalAt;
        private bool _firstTacticalRun = true;
        private int _initialDistantPoints;
        private BattleAiMarker _distantMarker;
        private float _distantMarkerAt = -1f;
        private float _shootAt = -1f;
        private float _shootRecoveryDelay;
        private float _recoveryAt = -1f;
        private float _obstacleCheckedAt = -1f;
        private static readonly float[] SelfAnchorRadii = { 6f, 12f, 20f, 30f };
        private const int SelfAnchorDirections = 12;
        private const float SelfAnchorLifetime = 8f;
        private const float SelfAnchorRetry = 2f;
        private const float SelfAnchorKeepSquared = 900f;
        private Vector3 _selfAnchor;
        private Vector3 _selfAnchorFrom;
        private float _selfAnchorUntil = -1f;
        private float _selfAnchorRetryAt = -1f;
        private bool _selfAnchorValid;
        private float? _stuckSince;
        private Vector3 _stuckFrom;
        private Vector3 _stuckHeading;
        private float _unstuckUntil = -1f;
        private float _unstuckTurn = 1f;
        private bool _obstacleClose;

        /// <summary>Whether the diagonal the detour turns towards was free.</summary>
        private bool _avoidForwardClear;

        /// <summary>Time.time the side commitment runs out at.</summary>
        private float _avoidUntil = -1f;

        /// <summary>
        /// Shared hit buffer for the obstacle probe. RaycastAll returns a
        /// fresh array per call, and with up to three probes per bot per
        /// interval a 7x7 battle hands the collector a steady drip of garbage
        /// for nothing; the non-allocating overload reuses this one.
        /// </summary>
        private static readonly RaycastHit[] ObstacleHits = new RaycastHit[8];
        private BattleAiCover _validHideObjectLast;
        private NavigationDrive _navigationDrive;
        private bool _applyingNavigation;

        /// <summary><c>_route_points</c>: the corners the steering follows.</summary>
        private readonly List<Vector3> _routePoints = new List<Vector3>();

        /// <summary><c>_route_index</c>.</summary>
        private int _routeIndex;

        /// <summary><c>_route_goal</c>.</summary>
        private Vector3 _routeGoal;

        /// <summary><c>_nav_target_initialized</c>.</summary>
        private bool _navTargetInitialized;

        /// <summary><c>_nav_unreachable_goal</c>.</summary>
        private Vector3 _navUnreachableGoal;

        /// <summary><c>_nav_unreachable_until</c>.</summary>
        private float _navUnreachableUntil = -1f;

        /// <summary><c>_mobile_next_nav_repath</c>.</summary>
        private float _mobileNextNavRepath = -1f;

        /// <summary><c>path_desired_distance</c>, sized from the hull at init.</summary>
        private float _pathDesiredDistance = 1.2f;

        /// <summary><c>target_desired_distance</c>, sized from the hull at init.</summary>
        private float _targetDesiredDistance = 2f;

        /// <summary>A damage record waiting out <c>get_reaction_time() * 3</c>.</summary>
        private struct PendingDamage
        {
            public BattleTank Attacker;
            public float Damage;
            public float DueAt;
        }

        private struct HideNavigationCache
        {
            public Vector3 Source;
            public Vector3 Position;
            public float ExpiresAt;
        }

        /// <summary>The stored <c>drive_to_pos</c> call the navigation repeats.</summary>
        private struct NavigationDrive
        {
            public bool Valid;
            public Vector3 Position;
            public bool UseNavmesh;
            public bool AllowBack;
            public bool BackOnly;
            public bool AlwaysForward;
            public bool NoObstacleCheck;
            public float RequestedAt;
        }

        /// <summary>The roster the source reads off its Battle autoload.</summary>
        private static BattleRoster Roster
        {
            get { return BattleRoster.Current; }
        }

        /// <summary>The mask of the source's traces: the static world only.</summary>
        private static int VisibilityMask
        {
            get { return 1 << BattleLayers.Static; }
        }

        /// <summary>
        /// What the hull has to drive around. The navmesh is baked before the
        /// battle, so it knows the map but never the tanks standing on it:
        /// with the source's static-only mask a bot drove into a wreck or a
        /// teammate and kept grinding forward. A destroyed tank keeps its hull
        /// shell on BattleLayers.Physics, so adding that layer is what lets
        /// the avoidance steer past both.
        /// </summary>
        private static int ObstacleMask
        {
            get { return (1 << BattleLayers.Static) | (1 << BattleLayers.Physics); }
        }

        /// <summary><c>init</c>: hang a controller on a hull and let it settle.</summary>
        public static AiTankController Attach(
            BattleTank tank, AiSkillLevel skill = AiSkillLevel.WhiteArmy)
        {
            if (tank == null)
            {
                return null;
            }

            var controller = tank.gameObject.AddComponent<AiTankController>();
            controller.Init(tank, skill);
            return controller;
        }

        /// <summary><c>init(tank, skill)</c>.</summary>
        public void Init(BattleTank tank, AiSkillLevel skill)
        {
            Tank = tank;
            Skill = skill;
            PlayStyle = AiPlayStyle.Generic;
            Spawnpoint = tank != null ? tank.Pos() : Vector3.zero;
            ClearTarget(true);
            _initAt = Time.time + FinishInitDelay;

            if (tank != null && tank.Health != null)
            {
                tank.Health.OnDeath += OnDeath;
                tank.Health.OnDamageInfoTaken += OnDamageInfoTaken;
            }
            _atgm = AtgmAiController.Create(this, tank);
        }

        /// <summary>
        /// <c>_finish_init_after_delay</c>: the source writes the agent radius
        /// and the path tolerances here, and only a bot above the white army
        /// remembers where an enemy disappeared.
        /// </summary>
        private void FinishInitAfterDelay()
        {
            _initDone = true;
            if (Tank != null)
            {
                _pathDesiredDistance = Mathf.Clamp(Tank.HullWidth * 0.4f, 1.2f, 2f);
                _targetDesiredDistance = Mathf.Clamp(Tank.HullLength * 0.5f, 2f, 4f);
            }

            if (Skill > AiSkillLevel.WhiteArmy && Roster != null)
            {
                Roster.TankGotUnspotted += OnTankGotUnspotted;
            }
        }

        /// <summary><c>activate(on)</c>.</summary>
        public void Activate(bool on)
        {
            Active = on;
            ResetNavigation();
            if (!on)
            {
                if (_atgm != null) _atgm.Stop();
                return;
            }

            if (Skill == AiSkillLevel.Idle)
            {
                Active = false;
                return;
            }

            if (Tank != null && Tank.Turret != null)
            {
                Tank.Turret.AimLerpFactor = AiSkillTable.AimTargetLerpFactor(Skill);
            }

            ChoosePlayStyle();
        }

        /// <summary><c>start_battle</c>: unlock the turret and pick a state.</summary>
        public void StartBattle()
        {
            if (Tank != null)
            {
                Tank.TurretBlock = false;
                Tank.Blocked = false;
            }

            Activate(true);
            if (_atgm != null) _atgm.StartLife();
            TrySwitchingToRelevantState();
        }

        /// <summary><c>choose_play_style</c>.</summary>
        public void ChoosePlayStyle()
        {
            if (Tank == null || Tank.Card == null)
            {
                return;
            }

            var card = Tank.Card;
            var gun = Tank.GunInfo ?? new GunInfo();
            var shell = gun.Shells.Count > 0 ? gun.Shells[0] : null;
            var engine = card.Engines.Count > 0 ? card.Engines[0] : null;
            var tracks = Tank.TrackInfo ?? new TracksInfo();

            // Sandbox sliders bake health, penetration and reload into the
            // hull, but the card still carries the stock speed, engine,
            // traverse and damage, so a buffed bot would pick the style of its
            // stock tank.
            var sandbox = SandboxTank.Of(Tank);
            var damageFactor = sandbox != null ? Mathf.Max(0f, sandbox.DamageMultiplier) : 1f;
            var speedFactor = sandbox != null ? Mathf.Max(0.1f, sandbox.Factors.Speed) : 1f;
            var engineFactor = sandbox != null ? Mathf.Max(0.1f, sandbox.Factors.Engine) : 1f;
            var turnFactor = sandbox != null ? Mathf.Max(0.1f, sandbox.Factors.TurnRate) : 1f;

            PlayStyle = AiTactics.ChoosePlayStyle(
                new AiTactics.PlayStyleInputs
                {
                    ClassIsTd = card.Class == TankClass.TankDestroyer,
                    ClassIsLight = card.Class == TankClass.Light,
                    Level = card.Level,
                    Health = Tank != null && Tank.Health != null
                        ? Tank.Health.MaxHealth
                        : card.TotalHealth,
                    AvgArmor = Tank.AvgArmor,
                    ApPenetration = shell != null ? shell.Penetration : 0f,
                    ApDamage = shell != null ? shell.Damage * damageFactor : 0f,
                    DeviationLeft = gun.DeviationLeft,
                    SpeedLimitForward = card.SpeedForward * speedFactor,
                    EnginePower = engine != null ? engine.Power * engineFactor : 0f,
                    // The card's hull weight is the body mass, in kilogrammes.
                    MassTons = card.HullWeight * 0.001f,
                    TrackRotationSpeed = tracks.RotationSpeed * turnFactor,
                },
                Skill,
                PlayStyle);
        }

        /// <summary><c>play_style_is</c>.</summary>
        public bool PlayStyleIs(AiPlayStyle style)
        {
            return PlayStyle == style;
        }

        /// <summary><c>get_reaction_time</c>.</summary>
        public float ReactionTime()
        {
            return AiSkillTable.BaseReactionTime(Skill) * Random.Range(0.85f, 1.15f);
        }

        /// <summary><c>is_dumb</c>.</summary>
        public bool IsDumb()
        {
            return AiSkillTable.IsDumb(Skill);
        }

        /// <summary><c>skill_level_reached</c>.</summary>
        public bool SkillReached(AiSkillLevel level)
        {
            return AiSkillTable.SkillReached(Skill, level);
        }

        /// <summary><c>health_coeff</c>.</summary>
        public float HealthCoeff()
        {
            if (Tank == null || Tank.Health == null || Tank.Dead)
            {
                return 0f;
            }

            var max = Tank.Health.MaxHealth;
            return max <= 0 ? 0f : (float)Tank.Health.Health / max;
        }

        /// <summary><c>should_notice_health_change</c>.</summary>
        public bool ShouldNoticeHealthChange()
        {
            if (Tank == null || Tank.Card == null || Tank.Card.Health <= 0)
            {
                return false;
            }

            var fraction =
                (float)Tank.Health.Health / Mathf.Max(1, Tank.Health.MaxHealth);
            return fraction < AiSkillTable.HealthNoticeThreshold(Skill);
        }

        /// <summary><c>Util.delay_curtime_elapsed_set</c>, kept per controller.</summary>
        public bool DelayElapsedSet(string name, float seconds)
        {
            float due;
            if (_delays.TryGetValue(name, out due) && Time.time < due)
            {
                return false;
            }

            _delays[name] = Time.time + seconds;
            return true;
        }

        /// <summary><c>pos</c>.</summary>
        public Vector3 Pos()
        {
            return Tank != null ? Tank.Pos() : transform.position;
        }

        /// <summary><c>drive(fw, r, low_p_overrride)</c>.</summary>
        public void Drive(float forward, float right, bool lowPriorityOverride = false)
        {
            if (!_applyingNavigation)
            {
                _navigationDrive.Valid = false;
            }

            if (!_applyingFacing)
            {
                _facing.Valid = false;
            }

            if (Tank == null)
            {
                return;
            }

            if (Time.time < _fullStopUntil)
            {
                Tank.Drive(0f, 0f, false);
                return;
            }

            // BOTS HOLD POSITION takes the forward motion away and leaves the
            // turning alone, exactly as the source does.
            if (BotsStationary || SandboxTank.IsStationary(Tank))
            {
                forward = 0f;
            }

            Tank.Drive(forward, right, false);
            if (lowPriorityOverride)
            {
                Tank.Drive(forward, right, true);
            }
        }

        /// <summary><c>tank_is_valid</c>.</summary>
        public bool TankIsValid(BattleTank t)
        {
            return t != null && !t.Dead && t.Spotted && UprisingTank.IsTargetable(t, Tank);
        }

        /// <summary><c>enemy_target_is_valid</c>.</summary>
        public bool EnemyTargetIsValid()
        {
            return TankIsValid(TargetTank);
        }

        /// <summary><c>drive_target_is_valid</c>.</summary>
        public bool DriveTargetIsValid()
        {
            return TargetPosition != Vector3.zero;
        }

        /// <summary><c>drive_target_is_close</c>.</summary>
        public bool DriveTargetIsClose()
        {
            if (Tank == null)
            {
                return false;
            }

            var hull = Tank.HullLength;
            return (TargetPosition - Tank.Pos()).sqrMagnitude < hull * hull;
        }

        /// <summary><c>clear_target(drive)</c>.</summary>
        public void ClearTarget(bool drive)
        {
            if (drive)
            {
                Drive(0f, 0f);
                TargetPosition = Vector3.zero;
            }
            else
            {
                TargetTank = null;
            }
        }

        /// <summary><c>Battle.get_enemy_tanks(team)</c>.</summary>
        public List<BattleTank> Enemies()
        {
            return Roster != null && Tank != null
                ? Roster.GetEnemyTanks(Tank.Team)
                : new List<BattleTank>();
        }

        /// <summary><c>Battle.get_ally_tanks(team)</c>.</summary>
        public List<BattleTank> Allies()
        {
            return Roster != null && Tank != null
                ? Roster.GetAllyTanks(Tank.Team)
                : new List<BattleTank>();
        }

        /// <summary>
        /// <c>my_tank_visibility_coeff</c>: the share of my own aim points the
        /// terrain leaves exposed, which the source measures from its own gun.
        /// </summary>
        public float MyTankVisibilityCoeff()
        {
            if (Tank == null || Tank.AimPoints.Count == 0)
            {
                return 0f;
            }

            var from = Tank.SpotPosition();
            var clear = 0;
            for (var i = 0; i < Tank.AimPoints.Count; i++)
            {
                if (!Obstructed(from, Tank.AimPoints[i].position))
                {
                    clear++;
                }
            }

            return (float)clear / Tank.AimPoints.Count;
        }

        private Transform _armourAim;
        private BattleTank _plannedTarget;
        private ArmorShotPlanner.Plan _shotPlan;
        private float _nextShotPlan;
        private float _shotPlannedAt;
        private Vector3 _plannedOrigin;
        private Vector3 _plannedTargetPosition;
        private Quaternion _plannedTargetRotation;
        private int _plannedAmmo;

        /// <summary>
        /// The armour shot plan against <paramref name="t"/>, re-planned at
        /// most every quarter second per target. The aim state and the
        /// Uprising ability choice share it instead of planning twice.
        /// </summary>
        public ArmorShotPlanner.Plan ShotPlanFor(BattleTank t)
        {
            RefreshShotPlan(t);
            return _shotPlan;
        }

        /// <summary>Evaluate stocked shells against visible collision surfaces.</summary>
        public Transform TankGetVisibleAimPoint(BattleTank t)
        {
            RefreshShotPlan(t);
            if (!_shotPlan.Valid || Tank == null || t == null || t.Dead ||
                !Tank.CanLaunchShell(_shotPlan.Shell)) return null;
            Tank.SelectShell(_shotPlan.Shell);
            return _armourAim;
        }

        private void RefreshShotPlan(BattleTank t)
        {
            var now = Time.time;
            if (Tank == null || (t == _plannedTarget && now < _nextShotPlan))
            {
                return;
            }

            _nextShotPlan = now + ArmorShotPlanner.ReplanInterval;
            var origin = Tank.GunShootPoint();
            var ammo = LaunchableShells();
            if (t == _plannedTarget && t != null && ArmorShotPlanner.PlanStillFresh(
                    now - _shotPlannedAt,
                    (origin - _plannedOrigin).magnitude,
                    (t.transform.position - _plannedTargetPosition).magnitude,
                    Quaternion.Angle(t.transform.rotation, _plannedTargetRotation),
                    ammo == _plannedAmmo))
            {
                return;
            }

            _shotPlan = ArmorShotPlanner.Choose(Tank, t);
            _plannedTarget = t;
            _shotPlannedAt = now;
            _plannedOrigin = origin;
            _plannedAmmo = ammo;
            if (t != null)
            {
                _plannedTargetPosition = t.transform.position;
                _plannedTargetRotation = t.transform.rotation;
            }

            if (_shotPlan.Valid)
            {
                if (_armourAim == null) _armourAim = new GameObject("AI armour aim").transform;
                _armourAim.SetParent(_shotPlan.Mount, true);
                _armourAim.position = _shotPlan.Point;
            }
        }

        /// <summary>One bit per shell slot the tank can fire right now.</summary>
        private int LaunchableShells()
        {
            var mask = 0;
            var shells = Tank.GunInfo != null ? Tank.GunInfo.Shells.Count : 0;
            for (var slot = 0; slot < shells && slot < 31; slot++)
            {
                if (Tank.CanLaunchShell(slot)) mask |= 1 << slot;
            }

            return mask;
        }

        /// <summary><c>check_pos_to_pos_visibility(a, b)</c>.</summary>
        public bool CheckPosToPosVisibility(Vector3 from, Vector3 to)
        {
            return !Obstructed(from, to);
        }

        /// <summary><c>tank_check_visibility_from_pos(t, pos, cheap)</c>.</summary>
        public bool TankCheckVisibilityFromPos(BattleTank t, Vector3 from, bool cheap)
        {
            if (t == null)
            {
                return false;
            }

            if (cheap)
            {
                return !Obstructed(from, t.SpotPosition());
            }

            for (var i = 0; i < t.SpotPoints.Count; i++)
            {
                if (!Obstructed(from, t.SpotPoints[i].position))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary><c>tank_check_visibility(t, cheap)</c>.</summary>
        public bool TankCheckVisibility(BattleTank t, bool cheap = false)
        {
            return Tank != null && TankCheckVisibilityFromPos(t, Tank.SpotPosition(), cheap);
        }

        /// <summary>
        /// The source's traces run against mask 1, the static world, so hulls
        /// never block a sight line and nothing has to be excluded.
        /// </summary>
        private static bool Obstructed(Vector3 from, Vector3 to)
        {
            using var perfScope = BattlePerfMarkers.AiSight.Auto();
            var dir = to - from;
            var distance = dir.magnitude;
            if (distance <= 0.0001f)
            {
                return false;
            }

            return Physics.Raycast(from, dir / distance, distance, VisibilityMask);
        }

        /// <summary><c>is_tank_looking_at_pos(a, pos, tres)</c>.</summary>
        public bool IsTankLookingAtPos(BattleTank a, Vector3 pos, float tres = 0.75f)
        {
            if (a == null || a.Turret == null || a.Turret.GunPivot == null)
            {
                return false;
            }

            var dir = pos - a.Turret.GunPivot.position;
            if (dir.sqrMagnitude <= 0.0001f)
            {
                return false;
            }

            return Vector3.Dot(a.Turret.GunPivot.forward, dir.normalized) > Mathf.Abs(tres);
        }

        /// <summary><c>is_tank_looking_at_tank(a, b, tres)</c>.</summary>
        public bool IsTankLookingAtTank(BattleTank a, BattleTank b, float tres = 0.75f)
        {
            if (b == null || b.Turret == null || b.Turret.TurretPivot == null)
            {
                return false;
            }

            return IsTankLookingAtPos(a, b.Turret.TurretPivot.position, tres);
        }

        /// <summary>
        /// <c>is_tank_looking_at_me(other)</c>: below the blue army a bot only
        /// notices a gun pointed at it while it is roughly facing that way.
        /// </summary>
        public bool IsTankLookingAtMe(BattleTank other)
        {
            if (Tank == null || other == null)
            {
                return false;
            }

            var tres = SkillReached(AiSkillLevel.BlueArmy) ? 0f : 0.35f;
            if (!IsTankLookingAtTank(Tank, other, tres))
            {
                return false;
            }

            return IsTankLookingAtTank(other, Tank, 0.85f);
        }

        /// <summary><c>get_tanks_looking_at_me</c>.</summary>
        public int GetTanksLookingAtMe()
        {
            var enemies = Enemies();
            var count = 0;
            for (var i = 0; i < enemies.Count; i++)
            {
                if (!TankIsValid(enemies[i]))
                {
                    continue;
                }

                if (IsTankLookingAtMe(enemies[i]))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary><c>get_tanks_around_pos_team(pos, dist, enemy)</c>.</summary>
        public int GetTanksAroundPosTeam(Vector3 pos, float dist = 200f, bool enemy = false)
        {
            var tanks = enemy ? Enemies() : Allies();
            var around = 0;
            for (var i = 0; i < tanks.Count; i++)
            {
                var t = tanks[i];
                if (!TankIsValid(t))
                {
                    continue;
                }

                if ((t.Pos() - pos).sqrMagnitude < dist * dist)
                {
                    around++;
                }
            }

            return around;
        }

        /// <summary><c>ally_to_enemy_near_coeff(dist)</c>.</summary>
        public float AllyToEnemyNearCoeff(float dist = 200f)
        {
            var enemy = GetTanksAroundPosTeam(Pos(), dist, true);
            var ally = GetTanksAroundPosTeam(Pos(), dist, false);
            if (enemy <= 0)
            {
                return 1f;
            }

            if (ally <= 0)
            {
                return 0f;
            }

            return (float)ally / enemy;
        }

        /// <summary><c>dist_sqr_to_enemies</c>: the mean squared distance.</summary>
        public float DistSqrToEnemies()
        {
            var enemies = Enemies();
            var total = 0f;
            var count = 0;
            for (var i = 0; i < enemies.Count; i++)
            {
                var t = enemies[i];
                if (!TankIsValid(t))
                {
                    continue;
                }

                total += t.DistanceSquaredTo(Tank);
                count++;
            }

            return count == 0 ? 0f : total / count;
        }

        /// <summary><c>get_enemies_danger_level(dist)</c>, truncated to an int.</summary>
        public int GetEnemiesDangerLevel(float dist = 600f)
        {
            var enemies = Enemies();
            var total = 0f;
            var count = 0;
            for (var i = 0; i < enemies.Count; i++)
            {
                var t = enemies[i];
                if (!TankIsValid(t))
                {
                    continue;
                }

                if (t.DistanceSquaredTo(Tank) > dist * dist)
                {
                    continue;
                }

                total += GetTankDangerLevel(t);
                count++;
            }

            return count == 0 ? 0 : (int)(total / count);
        }

        /// <summary><c>get_tank_danger_level(t)</c>, cached per enemy.</summary>
        public int GetTankDangerLevel(BattleTank t)
        {
            if (Tank == null || t == null || t.Health == null || Tank.Health == null)
            {
                return 0;
            }

            int cached;
            if (_dangerCache.TryGetValue(t, out cached))
            {
                return cached;
            }

            var myHealth = Mathf.Max(1f, Tank.Health.Health);
            var theirHealth = Mathf.Max(1f, t.Health.Health);
            var myShell = Tank.CurrentShell();
            var theirShell = t.CurrentShell();
            var myDamage = myShell != null ? myShell.Damage : 0f;
            var theirDamage = theirShell != null ? theirShell.Damage : 0f;
            var myPen = myShell != null ? myShell.Penetration : 0f;
            var theirPen = theirShell != null ? theirShell.Penetration : 0f;

            var danger = AiTactics.DangerLevel(new AiTactics.DangerInputs
            {
                LevelDifference = (t.Card != null ? t.Card.Level : 1)
                    - (Tank.Card != null ? Tank.Card.Level : 1),
                HealthCoeff = theirHealth / myHealth,
                DamageToMyHealth = theirDamage / myHealth,
                MyDamageToTheirHealth = myDamage / theirHealth,
                DpmToMyHealth = Dpm(t) / myHealth,
                MyDpmToTheirHealth = Dpm(Tank) / theirHealth,
                PenetrationToMyArmor = Tank.AvgArmor == 0 ? 0f : theirPen / Tank.AvgArmor,
                MyPenetrationToTheirArmor = t.AvgArmor == 0 ? 0f : myPen / t.AvgArmor,
            });

            _dangerCache[t] = danger;
            return danger;
        }

        /// <summary><c>gun_info.get_dpm</c>: the loaded shell over the reload.</summary>
        private static float Dpm(BattleTank tank)
        {
            if (tank == null || tank.GunInfo == null || tank.GunInfo.Reload <= 0f)
            {
                return 0f;
            }

            var shell = tank.CurrentShell();
            return shell == null ? 0f : shell.Damage * (60f / tank.GunInfo.Reload);
        }

        /// <summary>The entry of <c>tanks_dealt_dmg_to_me</c> with the most damage.</summary>
        private BattleTank WorstAttacker()
        {
            BattleTank worst = null;
            var most = 0f;
            foreach (var pair in _damageFromTanks)
            {
                if (pair.Key == null || pair.Value <= most)
                {
                    continue;
                }

                most = pair.Value;
                worst = pair.Key;
            }

            return worst;
        }

        /// <summary><c>get_target_tank_rating(t, skip_vis)</c>.</summary>
        public float GetTargetTankRating(BattleTank t, bool skipVis)
        {
            bool visible;
            return GetTargetTankRating(t, skipVis, out visible);
        }

        /// <summary>
        /// The ported rating, also reporting whether it counted
        /// <paramref name="t"/> as visible (always so with
        /// <paramref name="skipVis"/>).
        /// </summary>
        private float GetTargetTankRating(BattleTank t, bool skipVis, out bool visible)
        {
            visible = false;
            if (Tank == null || t == null)
            {
                return 0f;
            }

            var distSqr = t.DistanceSquaredTo(Tank);
            var worst = WorstAttacker();
            var myHealth = Tank.Health != null && Tank.Health.MaxHealth > 0
                ? Tank.Health.MaxHealth
                : 1;
            visible = skipVis || TankCheckVisibility(t, distSqr > 100f * 100f && IsDumb());

            return AiTactics.TargetRating(new AiTactics.TargetRatingInputs
            {
                NoticedDamage = ShouldNoticeHealthChange(),
                IsWorstAttacker = worst == t,
                WorstAttackerShare = worst == null ? 0f : _damageFromTanks[worst] / myHealth,
                DamageTemperPenalty = AiSkillTable.DamageTemperPenalty(Skill),
                Visible = visible,
                SkipVisibility = skipVis,
                LevelAdvantage = (Tank.Card != null ? Tank.Card.Level : 1)
                    - (t.Card != null ? t.Card.Level : 1),
                SkillAtLeastGreen = SkillReached(AiSkillLevel.GreenArmy),
                HealthCoeff = Mathf.Max(1f, t.Health.Health) / Mathf.Max(1f, Tank.Health.Health),
                LookingAtMe = IsTankLookingAtMe(t),
                DistanceSquared = distSqr,
                AllyToEnemyNear = AllyToEnemyNearCoeff(AiTactics.TargetRatingNearDistance),
                DangerLevel = GetTankDangerLevel(t),
            });
        }

        /// <summary><c>search_for_target_tank_rating(skip_vis)</c>.</summary>
        public void SearchForTargetTankRating(bool skipVis = false)
        {
            // One reused list instead of a copy per search: every combat
            // state searches on its think, so the copies added up to garbage.
            var order = _targetOrder;
            order.Clear();
            order.AddRange(Enemies());
            Shuffle(order);

            // Green army and better also weighs what a player would: a hull it
            // can finish, a reload to punish, the gun that hurts it most.
            var smart = SkillReached(AiSkillLevel.GreenArmy);
            var bestRating = 0f;
            BattleTank best = null;
            for (var i = 0; i < order.Count; i++)
            {
                var t = order[i];
                if (!TankIsValid(t))
                {
                    continue;
                }

                var rating = SearchRating(t, skipVis, smart);
                if (rating <= 0f || rating <= bestRating)
                {
                    continue;
                }

                bestRating = rating;
                best = t;
            }

            order.Clear();
            TargetTank = best;
        }

        /// <summary>
        /// One candidate's score in <see cref="SearchForTargetTankRating"/>:
        /// the ported rating, a player's priorities for a green army crew or
        /// better (<paramref name="smart"/>), and the mode's preference.
        /// </summary>
        private float SearchRating(BattleTank t, bool skipVis, bool smart)
        {
            bool visible;
            var rating = GetTargetTankRating(t, skipVis, out visible);
            if (smart)
            {
                rating += TargetPriorityBonus(t);
            }

            // A mode preference goes only to a hull this gun can see: a boss
            // bonus on a hull behind a hill kept whole crews aiming at the
            // hill while the guns beside them went unanswered.
            var rated = rating > 0f && (skipVis ? TankCheckVisibility(t, true) : visible);
            return rating + ModeTargetBonus(t, rated);
        }

        /// <summary><c>Array.shuffle</c>.</summary>
        private static void Shuffle<T>(List<T> items)
        {
            for (var i = items.Count - 1; i > 0; i--)
            {
                var j = Random.Range(0, i + 1);
                var swap = items[i];
                items[i] = items[j];
                items[j] = swap;
            }
        }

        /// <summary><c>dir_to_pos_2d(pos, from)</c>.</summary>
        public Vector2 DirToPos2d(Vector3 pos, Vector3 from = default(Vector3))
        {
            var origin = from == Vector3.zero ? Pos() : from;
            var dir = new Vector2(pos.x - origin.x, pos.z - origin.z);
            return dir.sqrMagnitude <= 0.0001f ? Vector2.zero : dir.normalized;
        }

        /// <summary><c>avg_enemies_pos</c>.</summary>
        public Vector3 AvgEnemiesPos()
        {
            var enemies = Enemies();
            var total = Vector3.zero;
            var count = 0;
            for (var i = 0; i < enemies.Count; i++)
            {
                var t = enemies[i];
                if (!TankIsValid(t))
                {
                    continue;
                }

                total += t.Pos();
                count++;
            }

            if (count == 0)
            {
                for (var i = 0; i < _unspottedThere.Count; i++)
                {
                    total += _unspottedThere[i];
                    count++;
                }
            }

            return count == 0 ? Vector3.zero : total / count;
        }

        /// <summary><c>dir_to_enemies</c>.</summary>
        public Vector2 DirToEnemies()
        {
            return DirToPos2d(AvgEnemiesPos());
        }

        /// <summary><c>dot_to_pos(pos)</c>, flattened as the source does.</summary>
        public float DotToPos(Vector3 pos)
        {
            if (Tank == null)
            {
                return 0f;
            }

            var to = pos - Pos();
            to.y = 0f;
            if (to.sqrMagnitude <= 0.0001f)
            {
                return 0f;
            }

            var forward = Tank.transform.forward;
            forward.y = 0f;
            return Vector3.Dot(forward.normalized, to.normalized);
        }

        /// <summary>
        /// <c>sign_to_pos(pos)</c>. The source returns a mirrored cross product
        /// and its callers negate it; Unity's steering axis already turns right
        /// on a positive value, so this returns the axis to drive with.
        /// </summary>
        public float SignToPos(Vector3 pos)
        {
            if (Tank == null)
            {
                return 0f;
            }

            var to = pos - Pos();
            to.y = 0f;
            if (to.sqrMagnitude <= 0.0001f)
            {
                return 0f;
            }

            var right = Tank.transform.right;
            right.y = 0f;
            return SuspensionTuning.Sign(Vector3.Dot(right.normalized, to.normalized));
        }

        /// <summary>
        /// <c>navigation_goal(point)</c>: snap the goal onto the navmesh, snap
        /// the tank onto it too, and reject the goal outright when no path
        /// reaches it. Rejecting unreachable goals here is what keeps the
        /// source's bots out of the lake and off the quarry walls.
        /// </summary>
        public Vector3 NavigationGoal(Vector3 point)
        {
            if (Tank == null || point == Vector3.zero)
            {
                return Vector3.zero;
            }

            var navigation = BattleNavigation.Current;
            if (navigation == null || !navigation.Ready)
            {
                return NavigationGoalWithoutNavmesh(point);
            }

            Vector3 projected;
            if (!navigation.ClosestPoint(point, out projected))
            {
                return Vector3.zero;
            }

            Vector3 from;
            if (!ProjectSelf(navigation, projected, out from))
            {
                return Vector3.zero;
            }

            return projected;
        }

        /// <summary>
        /// The fallback for a map that ships no ported navmesh: the source's
        /// rejection at the play-area edge, with accepted goals projected onto
        /// the reconstructed terrain surface.
        /// </summary>
        private Vector3 NavigationGoalWithoutNavmesh(Vector3 point)
        {
            if (Tank == null)
            {
                return Vector3.zero;
            }

            var area = Tank.PlayArea;
            var inside = point;
            if (area.size.x > 0f && area.size.z > 0f)
            {
                inside = new Vector3(
                    Mathf.Clamp(point.x, area.min.x, area.max.x),
                    point.y,
                    Mathf.Clamp(point.z, area.min.z, area.max.z));
                if ((inside - point).sqrMagnitude > 1f)
                {
                    return Vector3.zero;
                }
            }

            // Pick the lowest upward-facing static surface below the goal. At
            // Rudniki this is the reconstructed terrain even when a roof or a
            // large hide object is above it.
            var hits = Physics.RaycastAll(
                inside + (Vector3.up * 1024f),
                Vector3.down,
                2048f,
                VisibilityMask,
                QueryTriggerInteraction.Ignore);
            var groundY = float.PositiveInfinity;
            for (var i = 0; i < hits.Length; i++)
            {
                if (hits[i].normal.y <= 0.2f || hits[i].point.y >= groundY)
                {
                    continue;
                }

                groundY = hits[i].point.y;
            }

            if (!float.IsPositiveInfinity(groundY))
            {
                inside.y = groundY;
            }

            return inside;
        }

        /// <summary><c>reset_navigation</c>.</summary>
        public void ResetNavigation()
        {
            _navigationDrive.Valid = false;
            _obstacleCheckedAt = -1f;
            _obstacleClose = false;
            _avoidSide = 0f;
            _avoidForwardClear = false;
            _avoidUntil = -1f;
            _routePoints.Clear();
            _routeIndex = 0;
            _routeGoal = Vector3.zero;
            _navTargetInitialized = false;
            _unreachableSince = -1f;
            DriveGoalUnreachable = false;
        }

        /// <summary><c>_route_distance(point)</c>: horizontal distance only.</summary>
        private float RouteDistance(Vector3 point)
        {
            var pos = Pos();
            return new Vector2(point.x - pos.x, point.z - pos.z).magnitude;
        }

        /// <summary>
        /// <c>_next_route_point()</c>: tank pivots and treads need not share the
        /// navmesh altitude, so a corner is consumed by horizontal distance and
        /// the corner tolerance is never inflated to hide that gap.
        /// </summary>
        private Vector3 NextRoutePoint()
        {
            if (_routePoints.Count == 0)
            {
                return Pos();
            }

            while (_routeIndex < _routePoints.Count - 1 &&
                RouteDistance(_routePoints[_routeIndex]) < _pathDesiredDistance)
            {
                _routeIndex++;
            }

            return _routePoints[_routeIndex];
        }

        /// <summary>
        /// <c>update_current_path(pos)</c>: repath only when the goal actually
        /// moves, snap both ends onto the navmesh, reject a partial corridor -
        /// steering the prefix of a route to a disconnected island only parks a
        /// tank against the same wall forever - and sit out a cooldown after an
        /// unreachable goal instead of asking for it again every frame.
        /// </summary>
        private void UpdateCurrentPath(Vector3 pos)
        {
            var navigation = BattleNavigation.Current;
            if (navigation == null || !navigation.Ready)
            {
                _routePoints.Clear();
                _routeIndex = 0;
                _navTargetInitialized = false;
                return;
            }

            if (_navTargetInitialized && SameGoal(_routeGoal, pos))
            {
                return;
            }

            var queriedAt = Time.time;
            if (_navTargetInitialized && Application.isMobilePlatform)
            {
                // Track a moving enemy at reaction-time cadence; a large target
                // jump still repaths at once. Desktop keeps the original rate.
                if (queriedAt < _mobileNextNavRepath &&
                    (_routeGoal - pos).sqrMagnitude < MobileNavUrgentShiftSquared)
                {
                    return;
                }

                _mobileNextNavRepath = queriedAt + MobileNavRepathInterval;
            }

            if (queriedAt < _navUnreachableUntil &&
                (_navUnreachableGoal - pos).sqrMagnitude < NavUnreachableGoalRadiusSquared)
            {
                return;
            }

            using var slowScope = BlitzPerfMonitor.Slow("ai.navPath");
            _routeGoal = pos;
            _routeIndex = 0;

            Vector3 routeStart;
            Vector3 routeGoal;
            if (!navigation.ClosestPoint(pos, out routeGoal) ||
                !ProjectSelf(navigation, routeGoal, out routeStart))
            {
                _routePoints.Clear();
            }
            else
            {
                navigation.CalculateRoute(routeStart, routeGoal, _routePoints);
                if (_selfAnchorValid && _routePoints.Count > 0)
                {
                    // Off the connected surface: drive straight at the anchor
                    // first, then follow the corridor from there.
                    _routePoints.Insert(0, routeStart);
                }
                if (_routePoints.Count > 0 &&
                    Vector3.Distance(_routePoints[_routePoints.Count - 1], routeGoal) >
                        BattleNavigation.GoalReachedDistance)
                {
                    _routePoints.Clear();
                }
            }

            _navTargetInitialized = _routePoints.Count > 0;
            if (!_navTargetInitialized)
            {
                _navUnreachableGoal = pos;
                _navUnreachableUntil = queriedAt + NavUnreachableGoalCooldown;
            }
        }

        /// <summary>
        /// Projects the hull onto the navmesh for a query towards
        /// <paramref name="goal"/>. A spawn slot can sit where the bake
        /// kept only a scrap of surface that connects to nothing - two Karieri
        /// slots on the west hill do - and a bot projected there found every
        /// goal unreachable and never left the spawn. When the nearest surface
        /// does not reach the goal, the hull is anchored instead to the
        /// nearest surface around it that does, which it can drive to
        /// directly; the anchor is kept while the hull stays near it.
        /// </summary>
        private bool ProjectSelf(BattleNavigation navigation, Vector3 goal, out Vector3 from)
        {
            var pos = Pos();
            if (_selfAnchorValid && Time.time < _selfAnchorUntil &&
                (pos - _selfAnchorFrom).sqrMagnitude < SelfAnchorKeepSquared)
            {
                from = _selfAnchor;
                return navigation.PathReaches(from, goal);
            }

            _selfAnchorValid = false;
            if (!navigation.ClosestPoint(pos, out from))
            {
                return false;
            }

            if (navigation.PathReaches(from, goal))
            {
                return true;
            }

            if (Time.time < _selfAnchorRetryAt)
            {
                return false;
            }

            _selfAnchorRetryAt = Time.time + SelfAnchorRetry;
            using var slowScope = BlitzPerfMonitor.Slow("ai.navAnchor");
            for (var r = 0; r < SelfAnchorRadii.Length; r++)
            {
                var radius = SelfAnchorRadii[r];
                for (var a = 0; a < SelfAnchorDirections; a++)
                {
                    var angle = (a * Mathf.PI * 2f) / SelfAnchorDirections;
                    var probe = pos + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * radius;
                    NavMeshHit hit;
                    if (!NavMesh.SamplePosition(probe, out hit, 4f, NavMesh.AllAreas))
                    {
                        continue;
                    }

                    if (Mathf.Abs(hit.position.y - pos.y) > radius ||
                        !navigation.PathReaches(hit.position, goal))
                    {
                        continue;
                    }

                    from = hit.position;
                    _selfAnchor = from;
                    _selfAnchorFrom = pos;
                    _selfAnchorUntil = Time.time + SelfAnchorLifetime;
                    _selfAnchorValid = true;
                    return true;
                }
            }

            return false;
        }

        /// <summary><c>Vector3.is_equal_approx</c>.</summary>
        private static bool SameGoal(Vector3 a, Vector3 b)
        {
            return Mathf.Approximately(a.x, b.x) &&
                Mathf.Approximately(a.y, b.y) &&
                Mathf.Approximately(a.z, b.z);
        }

        /// <summary><c>get_best_marker(min_dist)</c>.</summary>
        public BattleAiMarker GetBestMarker(float minDist = 0f)
        {
            var markers = Roster != null ? Roster.AiMarkers : null;
            if (markers == null || markers.Count == 0)
            {
                return null;
            }

            if (_reachedMarkers.Count >= markers.Count)
            {
                _reachedMarkers.Clear();
            }

            var vacant = new List<BattleAiMarker>();
            // ai_marker.gd keeps an occupier precisely so two bots do not
            // drive at the same spot; the port used to ignore it, and on a map
            // with few goals the whole team queued up behind one tank.
            var free = new List<BattleAiMarker>();
            for (var i = 0; i < markers.Count; i++)
            {
                var marker = markers[i];
                if (marker == null || _reachedMarkers.Contains(marker))
                {
                    continue;
                }

                var position = marker.transform.position;
                if (minDist > 0f && (position - Pos()).sqrMagnitude < minDist * minDist)
                {
                    continue;
                }

                if (NavigationGoal(position) == Vector3.zero)
                {
                    continue;
                }

                vacant.Add(marker);
                if (marker.CanOccupy() || marker.Occupier == this)
                {
                    free.Add(marker);
                }
            }

            if (vacant.Count == 0)
            {
                _reachedMarkers.Clear();
                return null;
            }

            var pool = free.Count > 0 ? free : vacant;
            var chosen = PickFrontMarker(pool);
            _reachedMarkers.Add(chosen);
            return chosen;
        }

        /// <summary><c>get_random_pos_at_marker(marker)</c>.</summary>
        public Vector3 GetRandomPosAtMarker(BattleAiMarker marker)
        {
            if (marker == null)
            {
                return Vector3.zero;
            }

            var center = marker.transform.position;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var angle = Random.value * Mathf.PI * 2f;
                var offset = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle))
                    * marker.Radius
                    * Random.Range(0f, 0.75f);
                var point = NavigationGoal(center + offset);
                if (point != Vector3.zero && Mathf.Abs(point.y - center.y) < 4f)
                {
                    return point;
                }
            }

            return NavigationGoal(center);
        }

        /// <summary><c>set_target_to_marker(marker)</c>.</summary>
        public void SetTargetToMarker(BattleAiMarker marker)
        {
            if (LastChosenMarker != null && LastChosenMarker != marker
                && LastChosenMarker.Occupier == this)
            {
                LastChosenMarker.Occupy(null);
            }

            LastChosenMarker = marker;
            if (marker == null)
            {
                TargetPosition = Vector3.zero;
                return;
            }

            marker.Occupy(this);
            TargetPosition = NavigationGoal(marker.transform.position);
        }

        /// <summary><c>set_target_to_random_marker(min_dist)</c>.</summary>
        public void SetTargetToRandomMarker(float minDist = 0f)
        {
            var marker = GetBestMarker(minDist);
            SetTargetToMarker(marker);

            // A map without markers (or with none left in reach) still gives
            // a roaming hull somewhere to go instead of its spawn.
            if (marker == null || TargetPosition == Vector3.zero)
            {
                TargetPosition = RoamGoal();
            }
        }

        /// <summary><c>distant_marker_is_valid(marker)</c>.</summary>
        public bool DistantMarkerIsValid(BattleAiMarker marker)
        {
            if (marker == null || !(marker.IsNormal() || marker.IsSniper()))
            {
                return false;
            }

            var position = marker.transform.position;
            var radiusSqr = marker.Radius * marker.Radius;
            if ((Pos() - position).sqrMagnitude < radiusSqr)
            {
                return false;
            }

            var enemies = Enemies();
            for (var i = 0; i < enemies.Count; i++)
            {
                var t = enemies[i];
                if (!TankIsValid(t))
                {
                    continue;
                }

                if ((t.Pos() - position).sqrMagnitude < radiusSqr)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary><c>get_distant_marker</c>, cached for three seconds.</summary>
        public BattleAiMarker GetDistantMarker()
        {
            if (_distantMarkerAt >= 0f
                && Time.time - _distantMarkerAt < AiTactics.DistantMarkerCacheInterval)
            {
                return _distantMarker;
            }

            _distantMarkerAt = Time.time;
            _distantMarker = null;

            var markers = Roster != null ? Roster.AiMarkers : null;
            if (markers == null || markers.Count == 0)
            {
                return null;
            }

            var order = new List<BattleAiMarker>(markers);
            Shuffle(order);

            var probes = 0;
            for (var i = 0; i < order.Count; i++)
            {
                var marker = order[i];
                if (!DistantMarkerIsValid(marker))
                {
                    continue;
                }

                if (marker.Occupier != null && marker.Occupier != this)
                {
                    continue;
                }

                if (probes >= AiTactics.DistantMarkerMaxPathProbes)
                {
                    break;
                }

                probes++;
                if (NavigationGoal(marker.transform.position) == Vector3.zero)
                {
                    continue;
                }

                _distantMarker = marker;
                return marker;
            }

            // The source keeps a miss for a shorter while than a hit.
            _distantMarkerAt = Time.time - AiTactics.DistantMarkerCacheInterval + 1f;
            return null;
        }

        /// <summary><c>hide_object_pos_valid</c>.</summary>
        private bool HideObjectPosValid(
            BattleAiCover cover,
            Vector2 dirFromDanger,
            Vector2 dirFromDangerTank)
        {
            if (Tank == null || cover == null || !cover.IsValid)
            {
                return false;
            }

            float blockedUntil;
            if (_hideObjectBlockedUntil.TryGetValue(cover, out blockedUntil)
                && Time.time < blockedUntil)
            {
                return false;
            }

            if (cover.AiNoHide
                || Vector2.Dot(dirFromDanger, dirFromDangerTank) < HideDirectionDotThreshold)
            {
                return false;
            }

            var objectPosition = cover.Position;
            if ((Tank.Pos() - objectPosition).sqrMagnitude
                > HideObjectMaxDistance * HideObjectMaxDistance)
            {
                return false;
            }

            var enemies = Enemies();
            for (var i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];
                if (!TankIsValid(enemy))
                {
                    continue;
                }

                if ((enemy.Pos() - objectPosition).sqrMagnitude
                    < HideEnemyExclusionDistanceSquared)
                {
                    return false;
                }
            }

            var allies = Allies();
            for (var i = 0; i < allies.Count; i++)
            {
                var ally = allies[i];
                if (!TankIsValid(ally))
                {
                    continue;
                }

                if ((ally.Pos() - objectPosition).sqrMagnitude
                    < ally.HullLength * ally.HullLength * 2f)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary><c>hide_object_get_hide_pos</c>.</summary>
        private Vector3 HideObjectGetHidePosition(
            BattleAiCover cover, Vector2 dirFromDanger)
        {
            if (Tank == null || cover == null || !cover.IsValid)
            {
                return Vector3.zero;
            }

            var objectPosition = cover.Position;
            var size = cover.Size;
            var height = size.y * 0.5f;
            var radius = 0.5f * (size.x + size.y) + Tank.HullWidth;
            var sourcePosition = new Vector3(
                objectPosition.x + dirFromDanger.x * radius,
                objectPosition.y + height,
                objectPosition.z + dirFromDanger.y * radius);

            HideNavigationCache cached;
            if (_hideNavigationCache.TryGetValue(cover, out cached))
            {
                var sourceGap = new Vector2(
                    cached.Source.x - sourcePosition.x,
                    cached.Source.z - sourcePosition.z);
                if (Time.time < cached.ExpiresAt
                    && sourceGap.sqrMagnitude <= 16f
                    && cached.Position != Vector3.zero)
                {
                    return cached.Position;
                }
            }

            var projected = NavigationGoal(sourcePosition);
            var maxProjectionGap = Tank.HullWidth * 3f + 8f;
            var projectionGap = new Vector2(
                projected.x - sourcePosition.x,
                projected.z - sourcePosition.z);
            if (projected == Vector3.zero
                || projectionGap.sqrMagnitude > maxProjectionGap * maxProjectionGap)
            {
                _hideNavigationCache.Remove(cover);
                _hideObjectBlockedUntil[cover] = Time.time + HideUnreachableRetrySeconds;
                return Vector3.zero;
            }

            _hideNavigationCache[cover] = new HideNavigationCache
            {
                Source = sourcePosition,
                Position = projected,
                ExpiresAt = Time.time + HideNavigationCacheSeconds,
            };
            return projected;
        }

        /// <summary>
        /// <c>find_hiding_spot</c>: choose the nearest valid source
        /// <c>navmesh_obstacle</c>, keeping the Godot distance, direction,
        /// volume, ally and enemy exclusions intact.
        /// </summary>
        public Vector3 FindHidingSpot(Vector3 threat)
        {
            if (Tank == null || Roster == null || Roster.AiCovers.Count == 0)
            {
                return Vector3.zero;
            }

            var enemiesPosition = AvgEnemiesPos();
            if (enemiesPosition == Vector3.zero && threat != Vector3.zero)
            {
                enemiesPosition = threat;
            }

            var dirFromDangerTank = -DirToPos2d(enemiesPosition);
            var minimumVolume = Tank.Hull.Height * Tank.HullLength * Tank.HullWidth;

            if (_validHideObjectLast != null
                && _validHideObjectLast.Volume >= minimumVolume)
            {
                var lastDirection = -DirToPos2d(
                    enemiesPosition, _validHideObjectLast.Position);
                if (HideObjectPosValid(
                    _validHideObjectLast, lastDirection, dirFromDangerTank))
                {
                    return HideObjectGetHidePosition(_validHideObjectLast, lastDirection);
                }
            }

            BattleAiCover closest = null;
            var closestDirection = Vector2.zero;
            var closestDistanceSquared = float.PositiveInfinity;
            var covers = Roster.AiCovers;
            for (var i = 0; i < covers.Count; i++)
            {
                var cover = covers[i];
                if (cover == null || cover.Volume < minimumVolume)
                {
                    continue;
                }

                var direction = -DirToPos2d(enemiesPosition, cover.Position);
                if (!HideObjectPosValid(cover, direction, dirFromDangerTank))
                {
                    continue;
                }

                var distanceSquared = (Tank.Pos() - cover.Position).sqrMagnitude;
                if (distanceSquared >= closestDistanceSquared)
                {
                    continue;
                }

                closest = cover;
                closestDirection = direction;
                closestDistanceSquared = distanceSquared;
            }

            if (closest == null)
            {
                return Vector3.zero;
            }

            _validHideObjectLast = closest;
            return HideObjectGetHidePosition(closest, closestDirection);
        }

        /// <summary>
        /// <c>follow_map_golden_path</c>: rudniki ships no golden paths, so the
        /// rollout state always falls through to its marker goal.
        /// </summary>
        public bool FollowMapGoldenPath()
        {
            return false;
        }

        /// <summary>The intent the jet jump reads off the last drive.</summary>
        public void NoteDriveIntent(bool retreating)
        {
            Retreating = retreating;
        }

        /// <summary><c>drive_to_pos(...)</c>.</summary>
        public void DriveToPos(
            Vector3 position,
            bool useNavmesh,
            bool allowBack = false,
            bool lookAt = false,
            bool backOnly = false,
            bool alwaysForward = false,
            bool noObstacleCheck = false)
        {
            if (position == Vector3.zero)
            {
                Drive(0f, 0f, true);
                return;
            }

            _facing.Valid = false;

            // The source turns the turret along the route, which used to steal
            // it from the enemy the combat tick was aiming at. With nothing to
            // shoot, a crew watches where an enemy was last heard instead.
            if (lookAt && Tank != null && !EnemyTargetIsValid())
            {
                Vector3 cue;
                Tank.SetAimTarget(TryThreatCue(out cue) ? cue : position, true, false);
            }

            _navigationDrive = new NavigationDrive
            {
                Valid = true,
                Position = position,
                UseNavmesh = useNavmesh,
                AllowBack = allowBack,
                BackOnly = backOnly,
                AlwaysForward = alwaysForward,
                NoObstacleCheck = noObstacleCheck,
                RequestedAt = Time.time,
            };

            ApplyNavigationDrive();
        }

        /// <summary><c>update_navigation_drive</c>: repeat the stored goal.</summary>
        public void UpdateNavigationDrive()
        {
            if (!_navigationDrive.Valid)
            {
                return;
            }

            if (!Active || Tank == null || Tank.Dead)
            {
                _navigationDrive.Valid = false;
                return;
            }

            if (Time.time - _navigationDrive.RequestedAt > NavigationDriveStale)
            {
                _navigationDrive.Valid = false;
                Drive(0f, 0f, true);
                return;
            }

            ApplyNavigationDrive();
        }

        /// <summary><c>_apply_navigation_drive</c>.</summary>
        private void ApplyNavigationDrive()
        {
            if (!_navigationDrive.Valid)
            {
                return;
            }

            _applyingNavigation = true;
            DriveToPosNow(_navigationDrive);
            _applyingNavigation = false;
        }

        /// <summary>
        /// <c>_drive_to_pos_now</c>: steer at the goal, back out when the goal
        /// sits behind the hull, and swing aside when something blocks the way.
        /// </summary>
        private void DriveToPosNow(NavigationDrive drive)
        {
            if (Tank == null)
            {
                return;
            }

            var nextPos = drive.Position;
            var routed = false;
            if (drive.UseNavmesh)
            {
                // A map without a baked navmesh steers straight at the goal
                // (the obstacle probe and the unstuck routine handle the
                // rest); the port parked every navmesh drive there, which left
                // the whole roster at its spawn.
                var navigation = BattleNavigation.Current;
                if (navigation != null && navigation.Ready)
                {
                    UpdateCurrentPath(drive.Position);
                    if (_routePoints.Count > 0)
                    {
                        routed = true;
                        _unreachableSince = -1f;
                        DriveGoalUnreachable = false;
                        nextPos = NextRoutePoint();
                        if (_routeIndex == _routePoints.Count - 1 &&
                            RouteDistance(nextPos) <= _targetDesiredDistance)
                        {
                            Drive(0f, 0f, true);
                            return;
                        }
                    }
                    else
                    {
                        // No corridor: a goal off the mesh or a hull knocked
                        // off it. Hold briefly for a repath, then steer
                        // straight at a close goal; a far one is repicked by
                        // the state (see UnreachableSeconds).
                        if (_unreachableSince < 0f)
                        {
                            _unreachableSince = Time.time;
                        }

                        DriveGoalUnreachable = true;
                        if (Time.time - _unreachableSince < UnreachableDirectSeconds
                            || RouteDistance(drive.Position) > UnreachableDirectDistance)
                        {
                            Drive(0f, 0f, true);
                            return;
                        }
                    }
                }

                if (!routed && RouteDistance(drive.Position) <= _targetDesiredDistance)
                {
                    Drive(0f, 0f, true);
                    return;
                }
            }

            var alignment = DotToPos(nextPos);
            var turn = SignToPos(nextPos);
            var blocked = !drive.NoObstacleCheck && ObstacleAhead();

            // A wreck on the route is the one obstacle the navmesh cannot know
            // about, and the goal heading points straight through it. While the
            // commitment runs, the detour heading wins, so the hull drives
            // around the hulk instead of grinding into it.
            var avoiding = !drive.NoObstacleCheck
                && _avoidSide != 0f
                && Time.time < _avoidUntil;

            var forward = 0f;
            var rotation = 0f;
            if (drive.AllowBack && (drive.BackOnly || alignment < 0f))
            {
                if (alignment < -0.86f)
                {
                    forward = -1f;
                }

                // The rear has to swing towards the goal, which is a yaw away
                // from it; base_tank.drive mirrors the stick while reversing,
                // so the input flips with the forward axis. Feeding the pivot
                // input while backing up steered the rear away from the goal
                // and made every retreat zig-zag.
                if (alignment > -0.985f)
                {
                    rotation = YawInput(-turn, forward);
                }
            }
            else if (avoiding)
            {
                // Steer to the committed side and keep rolling whenever that
                // diagonal is free; only a hull boxed in on both diagonals
                // pivots, and TryUnstuck is what backs that one out.
                rotation = _avoidSide;
                forward = !blocked || _avoidForwardClear ? 1f : 0f;
            }
            else
            {
                var threshold = routed ? 0.86f : 0.55f;
                if ((alignment > threshold || drive.AlwaysForward) && !blocked)
                {
                    forward = 1f;
                }

                if (alignment < 0.985f)
                {
                    rotation = turn;
                }

                if (blocked && rotation == 0f)
                {
                    rotation = _avoidSide;
                }
            }

            TryUnstuck(blocked, ref forward, ref rotation);

            Drive(forward, rotation, true);
        }

        /// <summary>
        /// A hull asked to drive forward that has not moved for a while is
        /// wedged on something the forward probe cannot see - a prop edge, a
        /// low rock, a teammate's flank - and asking again only grinds it.
        /// The port backs it out with a turn and lets the route pick up again
        /// from the new spot, which is what a player does.
        /// </summary>
        private bool TryUnstuck(bool blocked, ref float forward, ref float rotation)
        {
            var now = Time.time;
            if (Tank == null || Tank.Dead || Tank.Blocked || BotsStationary
                || SandboxTank.IsStationary(Tank) || now < _fullStopUntil)
            {
                _stuckSince = null;
                _unstuckUntil = -1f;
                return false;
            }
            if (now < _unstuckUntil)
            {
                forward = _unstuckForward;
                rotation = _unstuckTurn;
                return true;
            }

            var pos = Pos();
            // Facing a wall and swinging back and forth counts too: that is
            // the deadlock the forward probe alone never breaks.
            var wantsToMove = forward != 0f || rotation != 0f || blocked;
            var progress = new Vector2(pos.x - _stuckFrom.x, pos.z - _stuckFrom.z).magnitude;
            var heading = Vector3.ProjectOnPlane(Tank.transform.forward, Vector3.up);
            // A successful pivot makes angular progress without translating.
            // A side wall can block that pivot while the front probe is clear.
            var turningProgress = forward == 0f && rotation != 0f
                && Vector3.Angle(_stuckHeading, heading) > 10f;
            if (!wantsToMove || !_stuckSince.HasValue || progress > StuckProgress || turningProgress)
            {
                _stuckSince = wantsToMove ? now : (float?)null;
                _stuckFrom = pos;
                _stuckHeading = heading;
                return false;
            }

            if (now - _stuckSince.Value < StuckTime)
            {
                return false;
            }

            _stuckSince = null;
            _unstuckUntil = now + UnstuckTime;
            _unstuckTurn = _avoidSide != 0f ? -_avoidSide : (Random.value < 0.5f ? -1f : 1f);

            // A hull wedged while backing into cover gets out forwards.
            _unstuckForward = forward < 0f ? 1f : -1f;
            var origin = pos + Vector3.up * Mathf.Max(0.5f, Tank.Hull.Height * 0.5f);
            var reach = Mathf.Max(2f, Tank.HullLength * 0.75f);
            var escape = Tank.transform.forward * _unstuckForward;
            if (ObstacleHit(origin, escape, reach) && !ObstacleHit(origin, -escape, reach))
                _unstuckForward = -_unstuckForward;
            _obstacleCheckedAt = -1f;
            forward = _unstuckForward;
            rotation = _unstuckTurn;
            return true;
        }

        /// <summary>
        /// <c>_refresh_obstacle_avoidance</c>, reduced to a probe in front of
        /// the hull and a side with room, because there is no navmesh to ask.
        /// </summary>
        /// <summary>
        /// Traces the map and the tanks on it, ignoring the hull the ray
        /// starts inside. Without the self filter a bot would report its own
        /// physics shell as an obstacle and creep sideways forever. Triggers
        /// are skipped too: every destructible carries a ram_area trigger on
        /// the physics layer that a hull drives straight through, and on
        /// Karieri, where cacti and fences line the spawns, reading those as
        /// walls parked the bots in front of them for the whole battle.
        /// </summary>
        private bool ObstacleHit(Vector3 origin, Vector3 direction, float distance)
        {
            using var perfScope = BattlePerfMarkers.AiObstacle.Auto();
            var count = Physics.RaycastNonAlloc(
                origin, direction, ObstacleHits, distance, ObstacleMask,
                QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var collider = ObstacleHits[i].collider;
                if (collider == null)
                {
                    continue;
                }

                if (Tank != null && collider.transform.IsChildOf(Tank.transform))
                {
                    continue;
                }

                // The probe runs level with the hull, so on a rising road it
                // meets the ground ahead. A surface a hull can drive up is
                // road, not a wall: the navmesh already kept the route off
                // anything steeper.
                if (ObstacleHits[i].normal.y >= DrivableNormalY)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private bool ObstacleAhead()
        {
            if (_obstacleCheckedAt >= 0f && Time.time - _obstacleCheckedAt < ObstacleCheckInterval)
            {
                return _obstacleClose;
            }

            _obstacleCheckedAt = Time.time;
            _obstacleClose = false;
            if (Tank == null)
            {
                return false;
            }

            var origin = Pos() + (Vector3.up * Mathf.Max(0.5f, Tank.Hull.Height * 0.5f));
            var forward = Tank.transform.forward;
            var reach = Mathf.Max(6f, Tank.HullLength * 1.5f);
            if (!ObstacleHit(origin, forward, reach))
            {
                return false;
            }

            _obstacleClose = true;
            var right = Tank.transform.right;
            var sideReach = Mathf.Max(4f, Tank.HullWidth * 2f);
            var rightBlocked = ObstacleHit(origin, (forward + right).normalized, sideReach);
            var leftBlocked = ObstacleHit(origin, (forward - right).normalized, sideReach);
            if (rightBlocked && !leftBlocked)
            {
                _avoidSide = -1f;
            }
            else if (leftBlocked && !rightBlocked)
            {
                _avoidSide = 1f;
            }
            else if (_avoidSide == 0f || Time.time >= _avoidUntil)
            {
                _avoidSide = Random.value < 0.5f ? -1f : 1f;
            }

            // Getting around a wreck only works while the hull keeps rolling:
            // a pivot on the spot leaves the hulk exactly where it was. The
            // diagonal the bot turns towards decides whether it can creep
            // through the turn or has to pivot first.
            _avoidForwardClear = _avoidSide > 0f ? !rightBlocked : !leftBlocked;
            _avoidUntil = Time.time + AvoidCommitTime;

            return true;
        }

        public AtgmAiMode AtgmMode
        {
            get { return _atgm != null ? _atgm.Mode : AtgmAiMode.None; }
        }

        public bool TickAtgmCombat(float delta)
        {
            return _atgm != null && _atgm.TickCombat(delta);
        }

        /// <summary><c>can_shoot</c>.</summary>
        public bool CanShoot()
        {
            return Tank != null && Tank.CanShoot();
        }

        /// <summary>
        /// <c>stop_to_shoot(s)</c>. Both branches of the source's ternary hold
        /// the hull for the same three reaction times, and its own
        /// <c>drive</c> guard swallows the traverse it passes afterwards, so
        /// the hull simply stands still while the shot settles.
        /// </summary>
        public void StopToShoot(bool stop)
        {
            if (!CanShoot())
            {
                return;
            }

            _fullStopUntil = Time.time + (ReactionTime() * 3f);
            Drive(0f, Tank != null && Tank.IsTdNoFullRotation ? Tank.RightAxis : 0f);
        }

        /// <summary><c>shoot</c>.</summary>
        public void Shoot()
        {
            if (Tank == null || TargetTank == null || Tank.GunInfo == null)
            {
                return;
            }

            // Source quirk: the squared distance is clamped into 37.5 .. 75, so
            // the aim-time branch below can never see more than 75 and the
            // dispersion stop is unreachable. Both are kept as they are.
            var dist = Mathf.Clamp(
                (TargetTank.Pos() - Pos()).sqrMagnitude,
                AiTactics.LessAimTimeShootDistance * 0.5f,
                AiTactics.LessAimTimeShootDistance);
            var threshold = AiSkillTable.DispersionShootThreshold(Skill);
            var dispersion = Tank.GunInfo.Dispersion;
            var tempered = Tank.Dispersion;
            if (dist > AiTactics.NoAimTimeShootDistance * AiTactics.NoAimTimeShootDistance
                && tempered > 0f
                && dispersion / tempered < dispersion * threshold)
            {
                StopToShoot(true);
                return;
            }

            var reaction = ReactionTime() * 0.45f;
            _shootAt = Time.time + reaction;
            _shootRecoveryDelay = reaction * 0.75f;
        }

        /// <summary><c>_shoot_after_reaction(recovery_delay)</c>.</summary>
        private void ShootAfterReaction(float recoveryDelay)
        {
            if (Tank == null || Roster == null || !Roster.IsOngoing)
            {
                return;
            }

            var plan = ArmorShotPlanner.Choose(Tank, TargetTank);
            if (!plan.Valid || plan.Shell != Tank.ShellId) return;
            // Sandbox HARMLESS: the bot still aims and stops; only the trigger is skipped.
            if (Tank.Gun != null && !SandboxTank.IsHarmless(Tank)) Tank.Gun.ShootAttempt();

            _recoveryAt = Time.time + recoveryDelay;
        }

        /// <summary>
        /// <c>answer_close_fixed_gun_threat(target_pos)</c>: a tank destroyer
        /// turns its hull when a close threat sits outside the gun's arc.
        /// </summary>
        public bool AnswerCloseFixedGunThreat(Vector3 targetPos)
        {
            if (Tank == null || Tank.GunInfo == null || !Tank.IsTdNoFullRotation)
            {
                return false;
            }

            var local = Tank.transform.InverseTransformPoint(targetPos);
            var turn = FixedGunThreatMath.CloseThreatTurn(
                local.x,
                local.z,
                Tank.GunInfo.DeviationLeft,
                Tank.GunInfo.DeviationRight,
                true,
                FixedGunThreatMath.ResponseDistance(Tank.HullLength));
            if (turn == 0)
            {
                return false;
            }

            Drive(0f, turn, true);
            return true;
        }

        /// <summary><c>enter_state(new_state, ignore_same)</c>.</summary>
        public void EnterState(AiStateKind kind, bool ignoreSame = false)
        {
            if (ignoreSame && CurrentState != null && CurrentStateKind == kind)
            {
                return;
            }

            if (CurrentState != null)
            {
                _navigationDrive.Valid = false;
                CurrentState.Exit();
            }

            _navigationDrive.Valid = false;
            _facing.Valid = false;
            _stateEnteredAt = Time.time;
            CurrentStateKind = kind;
            CurrentState = CreateState(kind);
            CurrentState.Enter(this);
            NoteProgress();
        }

        private static AiState CreateState(AiStateKind kind)
        {
            switch (kind)
            {
                case AiStateKind.Careful: return new AiStateCareful();
                case AiStateKind.Brawl: return new AiStateBrawl();
                case AiStateKind.Distant: return new AiStateDistant();
                case AiStateKind.Single: return new AiStateSingle();
                case AiStateKind.Duel: return new AiStateDuel();
                case AiStateKind.Withdraw: return new AiStateWithdraw();
                case AiStateKind.Chase: return new AiStateChase();
                case AiStateKind.Escort: return new AiStateEscort();
                case AiStateKind.Rush: return new AiStateRush();
                default: return new AiStateRollout();
            }
        }

        /// <summary><c>try_switching_to_relevant_state</c>.</summary>
        public void TrySwitchingToRelevantState()
        {
            if (Tank == null || Tank.Card == null)
            {
                return;
            }

            var heavy = PlayStyleIs(AiPlayStyle.Heavy);
            var light = PlayStyleIs(AiPlayStyle.Light);
            var sniper = PlayStyleIs(AiPlayStyle.Sniper);
            var shell = Tank.CurrentShell();
            var armorCoeff = Tank.AvgArmor == 0
                ? 0f
                : (shell != null ? shell.Penetration : 0f) / Tank.AvgArmor;

            if (_firstTacticalRun)
            {
                _firstTacticalRun = false;
                _initialDistantPoints += AiTactics.FirstRunDistantPoints(
                    sniper,
                    IsDumb(),
                    heavy,
                    armorCoeff,
                    Tank.Card.Level,
                    Tank.Card.SpeedForward);
            }

            var enemies = Enemies();
            var spotted = 0;
            for (var i = 0; i < enemies.Count; i++)
            {
                if (TankIsValid(enemies[i]))
                {
                    spotted++;
                }
            }

            // A mode role (a Big Boss escort, bomber, intoxicator or boss, an
            // Uprising rammer) plays its part before the generic odds.
            AiStateKind moded;
            if (TryChooseModeState(spotted, out moded))
            {
                EnterState(moded, true);
                return;
            }

            // With nobody in sight the bot may still be hunting a target that
            // slipped away, or hiding from guns it cannot see.
            AiStateKind engaged;
            if (spotted == 0)
            {
                EnterState(
                    TryChooseEngagementState(spotted, out engaged) ? engaged : AiStateKind.Rollout,
                    true);
                return;
            }

            // The odds of the fight in front of it come first: a duel it can
            // win, or cover when the guns pointed at it outweigh its own.
            if (TryChooseEngagementState(spotted, out engaged))
            {
                EnterState(engaged, true);
                return;
            }

            var allyToEnemy = AllyToEnemyNearCoeff(150f);
            var tanksLooking = GetTanksLookingAtMe();
            var enemiesDanger = GetEnemiesDangerLevel(250f);
            var enemiesNear = GetTanksAroundPosTeam(Pos(), 150f, true);

            var kind = AiTactics.ChooseState(new AiTactics.StateInputs
            {
                Heavy = heavy,
                Light = light,
                InitialCareful = 0,
                InitialBrawl = 0,
                InitialDistant = _initialDistantPoints,
                InitialSingle = 0,
                InitialRollout = 0,
                HealthCoeff = HealthCoeff(),
                TanksLooking = tanksLooking,
                AllyToEnemy = allyToEnemy,
                EnemiesDanger = enemiesDanger,
                EnemiesNear = enemiesNear,
                BattleTime = Roster != null ? Roster.GetTime() : 0f,
                HasDistantMarker = GetDistantMarker() != null,
            });

            EnterState(kind, true);
        }

        /// <summary><c>on_death -> activate(false)</c>.</summary>
        private void OnDeath()
        {
            Activate(false);
            if (LastChosenMarker != null && LastChosenMarker.Occupier == this)
            {
                LastChosenMarker.Occupy(null);
            }
        }

        /// <summary><c>_on_tank_got_unspotted(other)</c>.</summary>
        private void OnTankGotUnspotted(BattleTank other)
        {
            if (Tank == null || other == null || other.Team == Tank.Team)
            {
                return;
            }

            _unspottedThere.Add(other.Pos());
            NoteThreatCue(other.Pos());
        }

        /// <summary><c>_on_damage_info_taken(dinfo)</c>.</summary>
        private void OnDamageInfoTaken(DamageInfo info)
        {
            if (info.IsNull() || info.IsBlock())
            {
                return;
            }

            var attacker = info.AttackerTank as BattleTank;
            NoteHit(attacker);
            if (attacker == null || !attacker.Spotted)
            {
                return;
            }

            _pendingDamage.Add(new PendingDamage
            {
                Attacker = attacker,
                Damage = info.Dmg,
                DueAt = Time.time + (ReactionTime() * 3f),
            });
        }

        /// <summary><c>_record_damage_response(attacker_id, damage)</c>.</summary>
        private void RecordDamageResponse(BattleTank attacker, float damage)
        {
            if (Tank == null || attacker == null || !attacker.Spotted)
            {
                return;
            }

            _damageTakenLast = Time.time;
            float dealt;
            _damageFromTanks.TryGetValue(attacker, out dealt);
            _damageFromTanks[attacker] = dealt + damage;

            var health = Tank.Health != null && Tank.Health.MaxHealth > 0
                ? Tank.Health.MaxHealth
                : 1;
            Temper -= damage / health * 100f * AiSkillTable.DamageTemperPenalty(Skill);
        }

        private void ProcessPendingDamage()
        {
            for (var i = _pendingDamage.Count - 1; i >= 0; i--)
            {
                if (Time.time < _pendingDamage[i].DueAt)
                {
                    continue;
                }

                RecordDamageResponse(_pendingDamage[i].Attacker, _pendingDamage[i].Damage);
                _pendingDamage.RemoveAt(i);
            }
        }

        private void ProcessShootTimers()
        {
            if (_shootAt >= 0f && Time.time >= _shootAt)
            {
                _shootAt = -1f;
                ShootAfterReaction(_shootRecoveryDelay);
            }

            if (_recoveryAt >= 0f && Time.time >= _recoveryAt)
            {
                _recoveryAt = -1f;
                StopToShoot(false);
            }
        }

        /// <summary>The source drops a remembered spot once it drives up to it.</summary>
        private void PruneUnspotted()
        {
            for (var i = _unspottedThere.Count - 1; i >= 0; i--)
            {
                if ((_unspottedThere[i] - Pos()).sqrMagnitude < 50f * 50f)
                {
                    _unspottedThere.RemoveAt(i);
                }
            }
        }

        /// <summary><c>_physics_process(delta)</c>.</summary>
        private void FixedUpdate()
        {
            using var slowScope = BlitzPerfMonitor.Slow("ai.fixedUpdate");
            if (!_initDone && _initAt >= 0f && Time.time >= _initAt)
            {
                FinishInitAfterDelay();
            }

            ProcessPendingDamage();
            ProcessShootTimers();

            if (!Active || CurrentState == null || Tank == null || Tank.Dead)
            {
                return;
            }

            UpdatePerception();
            using (BattlePerfMarkers.AiState.Auto())
            {
                CurrentState.Update(Time.fixedDeltaTime);
            }

            // A withdrawal owns the hull, even under an ATGM launcher.
            if (_atgm != null && CurrentStateKind != AiStateKind.Withdraw) _atgm.ApplyMovement();
            using (BattlePerfMarkers.AiNavigation.Auto())
            {
                UpdateNavigationDrive();
                UpdateFacing();
                AssistGunTraverse();
            }

            UseSupportConsumables();

            if (Time.time - _lastTacticalAt < AiTactics.TacticalInterval)
            {
                return;
            }

            _lastTacticalAt = Time.time;
            using (BattlePerfMarkers.AiTactics.Auto())
            {
                TrySwitchingToRelevantState();
            }
            PruneUnspotted();
        }

        private void OnDestroy()
        {
            if (_atgm != null) { _atgm.Dispose(); _atgm = null; }
            if (_armourAim != null)
            {
                if (Application.isPlaying) Destroy(_armourAim.gameObject);
                else DestroyImmediate(_armourAim.gameObject);
            }
            if (Tank != null && Tank.Health != null)
            {
                Tank.Health.OnDeath -= OnDeath;
                Tank.Health.OnDamageInfoTaken -= OnDamageInfoTaken;
            }

            if (Roster != null)
            {
                Roster.TankGotUnspotted -= OnTankGotUnspotted;
            }
        }

        /// <summary>
        /// <c>note_drive_intent</c>: whether the last drive was a retreat, which
        /// the jet jump and the debug label read back.
        /// </summary>
        public bool Retreating { get; private set; }

        /// <summary>
        /// Pops a remembered enemy position, the way <c>reset_target</c> takes a
        /// random entry out of <c>tanks_unspotted_there</c>.
        /// </summary>
        public Vector3 TakeUnspottedThere(int index)
        {
            if (index < 0 || index >= _unspottedThere.Count)
            {
                return Vector3.zero;
            }

            var position = _unspottedThere[index];
            _unspottedThere.RemoveAt(index);
            return position;
        }

        /// <summary>Which way the hull swings out when a probe hits something.</summary>
        private float _avoidSide;

        /// <summary>Which way a wedged hull drives out: against the way it got stuck.</summary>
        private float _unstuckForward = -1f;
    }
}
