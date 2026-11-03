using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Tanks;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Telemetry;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The hull half of <c>assets/tanks/base/base_tank.gd</c> together with the
    /// root <c>RigidBody3D</c> of <c>base_tank.tscn</c>: the body that carries
    /// the suspension, assembles the model, keeps the tank inside the map
    /// border, forwards drive input and scrolls the tracks. The turret, the
    /// gun trace, the armour plates, shooting and damage are wired in here as
    /// well; module health and the AI controller arrive with the later slices.
    ///
    /// Godot runs <c>_physics_process</c> on a parent before its children, so
    /// the hull has to tick before <see cref="TankSuspension"/>.
    /// </summary>
    [DefaultExecutionOrder(-1)]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class BattleTank : MonoBehaviour
    {
        /// <summary>The visible model; only the MS-1 is ported so far.</summary>
        public const string ModelResource = HangarWorld.TankResource;

        /// <summary>
        /// <c>hull_phys</c>, the low-poly donor the source measures the hull
        /// from and builds the hull collider out of. It never renders, so
        /// <c>port_godot_hangar.py</c> writes it as its own OBJ.
        /// </summary>
        public const string PhysicsHullResource = "Models/Tanks/MS1/MS1_phys";

        /// <summary>Shared MS-1 albedo, alpha doubling as the smoothness map.</summary>
        public const string TextureResource = "Models/Tanks/MS1/Textures/MS1";

        /// <summary><c>PhysicsMaterial_oo81g</c>: friction 0.34, rough.</summary>
        public const float SourceFriction = 0.34f;

        /// <summary><c>hull_phys.angular_damp</c>.</summary>
        public const float SourceAngularDamping = 0.2f;

        /// <summary><c>clampf(global_position.y, -1000, 1000)</c>.</summary>
        public const float HeightLimit = 1000f;

        /// <summary><c>last_movement</c> needs <c>absf(speed) > 1</c>.</summary>
        public const float LastMovementSpeed = 1f;

        /// <summary><c>tracks_speed = suspension.speed_ms * -delta * 3.33</c>.</summary>
        public const float TrackSpeedFactor = 3.33f;

        /// <summary><c>rotate_tracks</c> gives up past <c>400 ** 2</c>.</summary>
        public const float TrackCameraDistance = 400f;

        /// <summary><c>rotate_tracks</c> returns while idling below this speed.</summary>
        public const float TrackIdleSpeed = 2f;

        /// <summary><c>rotate_track_mat</c> resets a runaway offset.</summary>
        public const float TrackOffsetLimit = 10000f;

        /// <summary><c>rotate_wheel</c>: <c>tracks_speed * 0.25 / radius</c>.</summary>
        public const float WheelSpeedFactor = 0.25f;

        /// <summary><c>imp_pos = gun.position + global_basis.y * 2</c>.</summary>
        public const float RecoilHeight = 2f;

        /// <summary>
        /// <c>random_vector_in_cone</c> swaps its helper axis once the cone
        /// points nearly straight up or down.
        /// </summary>
        public const float ConeAxisLimit = 0.99f;

        private const int GunTraceBufferSize = 32;

        /// <summary><c>CMP_EPSILON</c>, the tolerance behind <c>is_equal_approx</c>.</summary>
        public const float SourceEpsilon = 1e-05f;

        /// <summary>Same shading inputs the source tank shader receives.</summary>
        public const float TankMetallic = 0.01f;
        public const float TankSmoothness = 0.75f;
        public const float TrackAlphaCutoff = 0.1f;

        /// <summary>
        /// Real material assets pin the exact URP keyword variants in Android
        /// builds; runtime-only variants can look opaque white on a device.
        /// </summary>
        public const string BodyMaterialResource = "Materials/MS1BattleBody";
        public const string TrackMaterialResource = "Materials/MS1BattleTracks";

        /// <summary>
        /// The tank is not a map object: <c>assets/tanks/base/outline2.gdshader</c>
        /// keeps albedo, metallic and an alpha cutoff, and reads roughness out of
        /// the albedo alpha. <c>BlitzLegacy/Map/Object</c> has none of those
        /// inputs, so the hull stays on URP Lit and pins its own shader name.
        /// </summary>
        public const string TankShaderName = "Universal Render Pipeline/Lit";

        /// <summary>Submesh order of the deterministic OBJ writer.</summary>
        public const int BodySubMesh = 0;
        public const int TracksSubMesh = 1;

        /// <summary>Card this tank was assembled from.</summary>
        public TankInfo Card { get; private set; }

        /// <summary>Drivetrain this hull owns.</summary>
        public TankSuspension Suspension { get; private set; }

        /// <summary>Blitz 11.20 terrain marks emitted by both tracks.</summary>
        public TankTrackMarks TrackMarks { get; private set; }

        /// <summary>Measured width of one visible track for its ground mark.</summary>
        public float TrackMarkWidth { get; private set; }

        /// <summary>Measured lateral distance from hull centre to one track.</summary>
        public float TrackMarkHalfSpacing { get; private set; }

        /// <summary>Turret and gun rig this hull owns.</summary>
        public TankTurret Turret { get; private set; }

        /// <summary><c>gun_component</c>: the reload clock of the mounted gun.</summary>
        public BattleGun Gun { get; private set; }

        /// <summary><c>health_component</c>.</summary>
        public HealthComponent Health { get; private set; }

        /// <summary><c>gun_info</c>, the card of the mounted gun.</summary>
        public GunInfo GunInfo { get; private set; }

        /// <summary><c>track_info</c>, which also picks the dispersion model.</summary>
        public TracksInfo TrackInfo { get; private set; }

        /// <summary>
        /// The researched turret this hull carries, or null for a card that
        /// still keeps its turret values on the hull.
        /// </summary>
        public TurretInfo TurretModule { get; private set; }

        /// <summary>Index of <see cref="TurretModule"/> in the card.</summary>
        public int TurretIndex { get; private set; }

        /// <summary>
        /// Client mesh slot of the installed gun (<c>gun_05</c>), taken off the
        /// turret that mounts it, because the same gun sits in a different node
        /// in each turret.
        /// </summary>
        public string GunMesh
        {
            get
            {
                if (TurretModule == null || GunInfo == null)
                {
                    return null;
                }

                var slot = TurretModule.Slot(GunInfo.Name);
                return slot != null ? slot.Mesh : null;
            }
        }

        /// <summary><c>shell_id</c>: which of the gun's shells is loaded.</summary>
        [System.NonSerialized]
        public int ShellId;
        private readonly List<int> _shellAmounts = new List<int>();
        public IReadOnlyList<int> ShellAmounts { get { return _shellAmounts; } }

        /// <summary><c>disp_temp</c>: the cone the gun sits at right now.</summary>
        public float Dispersion { get; private set; }

        public float ViewRange { get; private set; }

        /// <summary>
        /// <c>camo_factor = (0.4 / tank_vol) ** 0.5</c> of setup(): a small
        /// hull hides better than a large one.
        /// </summary>
        public float CamoFactorBase { get; private set; }

        /// <summary>
        /// The camouflage this hull fights in, empty without one. It is in
        /// force whether or not the Camouflage graphics setting draws it.
        /// </summary>
        public string CamouflageId { get; private set; } = string.Empty;

        /// <summary>What <see cref="CamouflageId"/> adds to <see cref="CamoFactorBase"/>.</summary>
        public float CamouflageBonus { get; private set; }

        /// <summary>
        /// <c>get_spot_radius()</c>: a parked tank under a camonet sees 25 per
        /// cent further.
        /// </summary>
        public float CurrentViewRange
        {
            get
            {
                var range = ViewRange;
                if (Parked() && HasEquipment(
                        BlitzLegacy.Core.Battle.SpottingMath.CamonetId))
                {
                    range *= BlitzLegacy.Core.Battle.SpottingMath.CamonetFactor;
                }

                return range;
            }
        }

        /// <summary><c>armor_nodes</c>: the plates a shell trace can hit.</summary>
        public IReadOnlyList<BattleArmorPlate> Plates { get { return _plates; } }

        /// <summary><c>dead()</c>.</summary>
        public bool Dead { get { return Health != null && Health.Dead; } }

        /// <summary>The Uprising controller of this hull, or null outside that mode.</summary>
        public UprisingTank Uprising { get; internal set; }
        public BigBossTank BigBoss { get; internal set; }

        /// <summary>
        /// What the battle mode adds to this crew's HUD, registered by the mode when it
        /// attaches; null in the standard battle. Read it through <see cref="BattleHudMode.Of"/>.
        /// </summary>
        public BattleHudMode HudMode { get; internal set; }

        /// <summary>
        /// True when <paramref name="observer"/> must treat this tank as a
        /// wreck: it is dead, or it plays dead (Uprising Play Dead) and the
        /// observer has not seen through it.
        /// </summary>
        public bool LooksDeadTo(BattleTank observer)
        {
            return Dead || (Uprising != null && Uprising.LooksDeadTo(observer));
        }

        /// <summary><see cref="LooksDeadTo"/> for the local player's HUD.</summary>
        public bool ShownDeadToLocalPlayer
        {
            get
            {
                if (Dead) { return true; }
                var session = Uprising != null ? Uprising.Session : null;
                return session != null && Uprising.LooksDeadTo(session.Player);
            }
        }

        /// <summary>Uprising Shadow: the local player's enemy lays no visible track marks.</summary>
        public bool HidesTrackMarksFromLocalPlayer
        {
            get
            {
                var roster = BattleRoster.Current;
                return Uprising != null && roster != null
                    && Uprising.HidesTrackMarksFrom(roster.PlayerTeam);
            }
        }

        /// <summary>Play Dead is showing the wreck visuals on this hull.</summary>
        public bool FakeWreckShown { get { return _forceCrashedTracksVisual; } }

        /// <summary><c>team</c>: zero for the player side, one for the target side.</summary>
        public int Team { get; private set; }

        /// <summary>
        /// <c>spotted</c>: whether the enemy team can currently see this tank.
        /// The slice has no spotting system yet, so every tank stays spotted
        /// and the AI behaves as it would against a fully lit enemy.
        /// </summary>
        public bool Spotted { get { return _spotted; } }

        /// <summary>
        /// <c>is_enemy and !spotted</c>: the tank (and every effect parented
        /// to it in the source) is hidden from the local player.
        /// </summary>
        public bool ConcealedFromPlayer
        {
            get
            {
                var roster = BattleRoster.Current;
                return roster != null && roster.PlayerTeam != Team && !_spotted;
            }
        }

        /// <summary><c>aim_points</c>: the points the AI aims a shot at.</summary>
        public IReadOnlyList<Transform> AimPoints { get { return _aimPoints; } }

        /// <summary><c>spot_points</c>: the points a visibility trace starts from.</summary>
        public IReadOnlyList<Transform> SpotPoints { get { return _spotPoints; } }

        /// <summary><c>avg_armor</c>, in millimetres.</summary>
        public int AvgArmor { get; private set; }

        /// <summary><c>is_td_no_full_rot</c>: a casemate gun with a limited arc.</summary>
        public bool IsTdNoFullRotation { get; private set; }

        /// <summary><c>hull_len</c>.</summary>
        public float HullLength { get { return Hull.Length; } }

        /// <summary><c>hull_width</c>.</summary>
        public float HullWidth { get { return Hull.Width; } }

        /// <summary><c>linear_velocity</c>, which the AI leads its shots with.</summary>
        public Vector3 LinearVelocity
        {
            get { return _rb != null ? _rb.linearVelocity : Vector3.zero; }
        }

        /// <summary>The endpoint of the latest 720-metre ballistic gun trace.</summary>
        public Vector3 GunTracePoint { get; private set; }

        /// <summary>The collider reached by the latest ballistic gun trace.</summary>
        public Collider GunTraceCollider { get; private set; }

        /// <summary>The live enemy tank under the gun trace, if any.</summary>
        public BattleTank GunTraceTank { get; private set; }

        /// <summary>Surface normal of the latest gun trace hit; zero on a miss.</summary>
        public Vector3 GunTraceNormal { get; private set; }

        /// <summary>The world-space half-width of the current dispersion cone.</summary>
        public Vector3 DispersionOffset { get; private set; }

        /// <summary>The point the random launch cone is built around.</summary>
        public Vector3 DispersionPoint { get; private set; }

        /// <summary>
        /// <c>is_player()</c>: in this slice a hull counts as the player once a
        /// camera rig is attached, and only then does it trace its own aim.
        /// </summary>
        [System.NonSerialized]
        public BattleCamera View;

        /// <summary>Instantiated visible model.</summary>
        public GameObject Model { get; private set; }

        /// <summary>
        /// <c>tank_particles_manager.wreck_effect</c>: the stopped explosion
        /// this tank owns for all of its lives.
        /// </summary>
        public BattleTankWreck Wreck { get; private set; }

        /// <summary>Hull measurements taken off <c>hull_phys</c>.</summary>
        public TankSuspension.HullGeometry Hull { get; private set; }

        /// <summary>
        /// <c>map_border_min</c> / <c>map_border_max</c>, already mirrored and
        /// normalised by <see cref="BattleWorld"/>.
        /// </summary>
        public Bounds PlayArea { get; set; }

        /// <summary>
        /// <c>blocked</c>: no drive commands leave the hull. The source keeps
        /// the flag on the tank and the suspension reads <c>base.blocked</c>
        /// every physics step, so the write has to reach the component too.
        /// Without it a wreck kept the yaw authority of whatever steering axis
        /// it held when it died, and span in place instead of standing still.
        /// </summary>
        public bool Blocked
        {
            get { return _blocked; }
            set
            {
                _blocked = value;
                if (Suspension != null)
                {
                    Suspension.Blocked = value;
                }
            }
        }

        private bool _blocked;
        private TankModelAssets _assets;
        private IReadOnlyList<string> _equipment;

        /// <summary>The track module body, i.e. Godot's <c>module_tracks</c>.</summary>
        private BattleArmorPlate _tracksPlate;

        /// <summary><c>turret_block</c>: the stationary bot keeps this on.</summary>
        [System.NonSerialized]
        public bool TurretBlock;

        /// <summary><c>modules</c>: every damageable module of this hull.</summary>
        public TankModuleSet Modules { get; private set; }

        /// <summary><c>tracks_len</c>, the z size of the track module body.</summary>
        public float TracksLength { get; private set; }

        /// <summary><c>tracks_are_crashed()</c>.</summary>
        public bool TracksCrashed
        {
            get
            {
                return Modules != null
                    && Modules.StatusOf(TankModuleIds.Tracks)
                        == TankModule.Status.Destroyed;
            }
        }

        public bool TrackCrashed(TrackSide side) => ModuleStatus(side == TrackSide.Left ? TankModuleIds.TrackLeft : TankModuleIds.TrackRight) == TankModule.Status.Destroyed;
        private bool CanUseEmergencyTracks => HasEmergencyTrack
            && (!TrackCrashed(TrackSide.Left) || TrackInfo.InternalTrack.ActiveFor(TrackSide.Left))
            && (!TrackCrashed(TrackSide.Right) || TrackInfo.InternalTrack.ActiveFor(TrackSide.Right));
        public TrackSide TrackSideAt(Vector3 worldPoint)
        {
            var x = transform.InverseTransformPoint(worldPoint).x;
            return Mathf.Abs(x - _leftTrackCentreX) <= Mathf.Abs(x - _rightTrackCentreX) ? TrackSide.Left : TrackSide.Right;
        }
        private void ApplyTrackDriveCaps()
        {
            if (Suspension == null) return;
            if (!TracksCrashed || !CanUseEmergencyTracks) { Suspension.SetDriveSpeedCaps(1f, 1f); return; }
            var info = TrackInfo.InternalTrack;
            Suspension.SetDriveTrackCaps(
                TrackCrashed(TrackSide.Left) ? info.ForwardLimit(TrackSide.Left) : 1f,
                TrackCrashed(TrackSide.Left) ? info.BackwardLimit(TrackSide.Left) : 1f,
                TrackCrashed(TrackSide.Right) ? info.ForwardLimit(TrackSide.Right) : 1f,
                TrackCrashed(TrackSide.Right) ? info.BackwardLimit(TrackSide.Right) : 1f,
                Mathf.Max(.1f, Mathf.Abs(_rightTrackCentreX - _leftTrackCentreX) * .5f),
                _leftTrackCentreX < _rightTrackCentreX);
        }

        public bool HasEmergencyTrack
        {
            get
            {
                return TrackInfo != null
                    && TrackInfo.InternalTrack != null
                    && TrackInfo.InternalTrack.Active;
            }
        }

        public float EmergencyTrackForwardSpeedLimit
        {
            get
            {
                return TrackInfo != null && TrackInfo.InternalTrack != null
                    ? TrackInfo.InternalTrack.ForwardSpeedLimit
                    : 1f;
            }
        }

        public float EmergencyTrackBackwardSpeedLimit
        {
            get
            {
                return TrackInfo != null && TrackInfo.InternalTrack != null
                    ? TrackInfo.InternalTrack.BackwardSpeedLimit
                    : 1f;
            }
        }

        /// <summary>Real HP of the tank itself.</summary>
        public int DisplayHealth
        {
            get { return Health != null ? Health.Health : 0; }
        }

        /// <summary>HP shown on an external marker for a specific observer.</summary>
        public int ExternalDisplayHealth(BattleTank observer)
        {
            if (Health == null) { return 0; }
            var uprising = Uprising;
            return uprising != null && uprising.ShowsZeroHealthVisualTo(observer)
                ? 0
                : Health.Health;
        }

        /// <summary><c>tracks_speed</c>.</summary>
        public float TracksSpeed { get; private set; }

        /// <summary><c>last_movement</c>.</summary>
        public float LastMovement { get; private set; }

        /// <summary><c>turret_ang_vel</c>, zeroed while the turret is blocked.</summary>
        public float TurretAngularVelocity { get; private set; }

        /// <summary><c>velocity_prev</c>.</summary>
        public Vector3 PreviousVelocity { get; private set; }

        /// <summary><c>velocity_prev_angular</c>.</summary>
        public Vector3 PreviousAngularVelocity { get; private set; }

        /// <summary><c>rotation_prev</c>, in the source's YXZ degrees.</summary>
        public Vector3 PreviousRotation { get; private set; }

        /// <summary><c>fw_axis</c>.</summary>
        public float ForwardAxis { get { return _fwAxis; } }

        /// <summary><c>r_axis</c>.</summary>
        public float RightAxis { get { return _rAxis; } }

        /// <summary><c>fw_axis_lowp</c>.</summary>
        public float ForwardAxisLow { get { return _fwAxisLow; } }

        /// <summary><c>r_axis_lowp</c>.</summary>
        public float RightAxisLow { get { return _rAxisLow; } }

        private static readonly Dictionary<Mesh, Mesh> MirroredMeshes =
            new Dictionary<Mesh, Mesh>();

        private readonly List<Wheel> _wheelsLeft = new List<Wheel>();
        private readonly List<Wheel> _wheelsRight = new List<Wheel>();

        private readonly List<BattleArmorPlate> _plates = new List<BattleArmorPlate>();
        private float _nextRemoveArmorPreview = -1f;
        private readonly List<Transform> _aimPoints = new List<Transform>();
        private readonly List<Transform> _spotPoints = new List<Transform>();
        // set_spotted() starts a battle with nobody spotted: an enemy has to
        // be found by BattleSpotter before it is visible or shootable.
        private bool _spotted;
        private Renderer[] _spottedRenderers;
        private readonly HashSet<Renderer> _armorPreviewRenderers = new HashSet<Renderer>();
        private readonly RaycastHit[] _gunTraceHits = new RaycastHit[GunTraceBufferSize];
        // Main thread only, shared by every tank's gun trace.
        private static readonly RaycastBatch GunTraceRays = new RaycastBatch(GunTraceBufferSize);
        private static int[] GunTraceRayIndex = new int[64];
        private float _mobileAiGunTraceNext = -1f;
        private Rigidbody _rb;
        private Camera _camera;
        private Material _trackLeft;
        private Material _trackRight;
        private TankCamouflageView _camouflage;

        // show_hide_tracks() swaps these three: the running pair the client
        // draws on a live hull and the one `tracks_crash` mesh it draws on a
        // dead one or on a destroyed track module. Visibility is held on the
        // GameObject, not on the renderer, because ApplySpottedVisibility
        // owns `enabled` for every renderer of an enemy hull.
        private GameObject _trackObjectLeft;
        private GameObject _trackObjectRight;
        private GameObject _crashedTracks;
        private bool _forceCrashedTracksVisual;
        private GameObject _crashedTrackLeft, _crashedTrackRight;
        private MeshRenderer _crashedTrackWhole;
        private float _leftTrackCentreX = -1f, _rightTrackCentreX = 1f;
        private static readonly Dictionary<Mesh, Mesh[]> SplitCrashMeshes = new Dictionary<Mesh, Mesh[]>();

        /// <summary><c>outline_materials</c>: one per visible tank material.</summary>
        private readonly List<Material> _outlineMaterials = new List<Material>();
        private Vector2 _trackOffsetLeft;
        private Vector2 _trackOffsetRight;
        private float _fwAxis;
        private float _rAxis;
        private float _fwAxisLow;
        private float _rAxisLow;
        private float _lowPriorityLockedUntil;
        private float _trackRotationSpeed = SuspensionTuning.DefaultRotationSpeed;

        /// <summary>One wheel of <c>wheelsL</c> / <c>wheelsR</c>.</summary>
        private struct Wheel
        {
            public Transform Node;
            public Vector3 Pivot;
            public float Radius;
        }

        private bool HasPlayArea
        {
            get { return PlayArea.size.x > 0f && PlayArea.size.z > 0f; }
        }

        /// <summary>
        /// <c>_battle.spawn_tank_at_spawner</c>: place a tank on a spawner and
        /// assemble it from its card.
        /// </summary>
        public static BattleTank Spawn(
            Transform parent,
            string name,
            TankInfo card,
            Transform spawn,
            Bounds playArea,
            int team = 0,
            int gunIndex = 0,
            int engineIndex = 0,
            int trackIndex = 0,
            IReadOnlyList<string> equipment = null,
            int turretIndex = 0)
        {
            var host = new GameObject(name);
            host.transform.SetParent(parent, false);
            if (spawn != null)
            {
                host.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
            }

            var tank = host.AddComponent<BattleTank>();
            tank.Team = team;
            tank.PlayArea = playArea;
            tank.Initialize(
                card, gunIndex, engineIndex, trackIndex, equipment, turretIndex);
            return tank;
        }

        /// <summary>
        /// <c>base_tank.setup</c>. Stock modules are index 0 of each list: the
        /// source reads the installed ids off the save, which the battle slice
        /// does not carry yet.
        /// </summary>
        public void Initialize(
            TankInfo card, int gunIndex = 0, int engineIndex = 0, int trackIndex = 0,
            IReadOnlyList<string> equipment = null, int turretIndex = 0)
        {
            Card = card;
            // A card rebuilt from the Blitz client researches its turret, and
            // the traverse speed, the view range and the gun list all come off
            // that turret instead of the hull.
            TurretModule = card != null ? card.Turret(turretIndex) : null;
            TurretIndex = turretIndex;
            _assets = TankModelAssets.For(card);
            if (_rb == null)
            {
                _rb = GetComponent<Rigidbody>();
            }

            var engine = Pick(card != null ? card.Engines : null, engineIndex);
            var track = Pick(card != null ? card.Tracks : null, trackIndex);
            _trackRotationSpeed = track != null
                ? track.RotationSpeed
                : SuspensionTuning.DefaultRotationSpeed;

            var tracksBounds = BuildModel();
            var hullBounds = BuildCollider();
            if (GetComponent<TankContactPhysics>() == null) gameObject.AddComponent<TankContactPhysics>();
            Hull = MeasureHull(hullBounds, tracksBounds);
            // Ride height follows the visible track bottom in hull space, so the
            // suspension can hold the rendered track on the terrain surface.
            var hull = Hull; hull.TracksBottom = TrackBottomLocal(tracksBounds.min.y); Hull = hull;
            // setup(): camo_factor = (0.4 / tank_vol) ** 0.5, over the physics
            // hull AABB the source measures right here.
            var tankVolume = Hull.Width * Hull.Height * Hull.Length;
            CamoFactorBase = tankVolume > 0.0001f
                ? Mathf.Sqrt(0.4f / tankVolume)
                : 0f;

            _equipment = equipment;
            var equipmentModifiers = EquipmentCatalog.Modifiers(equipment);
            _rb.mass = MassOf(card, gunIndex, engineIndex, trackIndex, turretIndex)
                + equipmentModifiers.AddedWeight;
            // TankContactPhysics sets the low, measured in-hull mass centre.
            _rb.angularDamping = SourceAngularDamping;
            _rb.linearDamping = 0f;
            // can_sleep = false.
            _rb.sleepThreshold = 0f;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            TankSuspension.ApplySourcePhysicsDefaults();

            Suspension = gameObject.AddComponent<TankSuspension>();
            Suspension.Blocked = _blocked;
            // suspension_component.gd casts every wheel ray with
            // collision_mask = 1 + 2: the map and the physics shell of a tank,
            // never the armour plates the hull carries around. Leaving Unity's
            // default "hit everything" mask let the rays land on this tank's own
            // plates, and a hull whose plates hang lower at one end - the T-43 -
            // ended up standing on them instead of on its tracks.
            Suspension.RaycastMask = BattleLayers.WheelMask;
            Suspension.Setup(
                card != null ? card.HullWeight : 1f,
                engine != null ? engine.Power : SuspensionTuning.DefaultEnginePower,
                track != null ? track.RotationSpeed : SuspensionTuning.DefaultRotationSpeed,
                Hull);

            // base_tank.setup writes the limits into int fields, which truncates.
            GetComponent<TankContactPhysics>().Setup(this, _rb, transform.Find("hull_phys_col").GetComponent<MeshCollider>(), hullBounds);

            Suspension.MaxSpeedForward = card != null
                ? (float)(int)card.SpeedForward
                : SuspensionTuning.DefaultMaxSpeedForward;
            Suspension.MaxSpeedBack = card != null
                ? (float)(int)card.SpeedBackward
                : SuspensionTuning.DefaultMaxSpeedBack;

            MeasureTrackMarkFootprint();
            TrackMarks = GetComponent<TankTrackMarks>();
            if (TrackMarks == null) { TrackMarks = gameObject.AddComponent<TankTrackMarks>(); }
            TrackMarks.Configure(
                this,
                card != null ? card.Nation : Nation.None,
                card != null ? card.Name : string.Empty,
                TrackMarkWidth,
                TrackMarkHalfSpacing);

            GunInfo = RuntimeGun(
                (card != null ? card.GunFor(turretIndex, gunIndex) : null)
                    ?? new GunInfo(),
                equipmentModifiers);
            var spotRadius = TurretModule != null
                ? TurretModule.SpotRadius
                : (card != null ? card.SpotRadius : 0f);
            ViewRange = card == null ? 0f
                : Mathf.Min(spotRadius * equipmentModifiers.ViewRange, 500f);
            TrackInfo = track ?? new TracksInfo();
            // The source works on tank_info.duplicate(true), so this hull owns
            // its traverse speed and a consumable may only change its own.
            TurretRotationSpeed = TurretModule != null
                ? TurretModule.RotationSpeed
                : (card != null ? card.TurretRotationSpeed : 0f);
            Turret = gameObject.AddComponent<TankTurret>();
            Turret.Rig(this);

            // setup(): a gun that cannot cover 90 degrees to either side
            // has to be aimed with the hull.
            IsTdNoFullRotation = Mathf.Abs(GunInfo.DeviationLeft) < 90f
                || Mathf.Abs(GunInfo.DeviationRight) < 90f;

            // disp_manager floors disp_temp at the gun's own cone on every
            // tick, so a tank that has not moved yet already sits there.
            Dispersion = GunInfo.Dispersion;

            Health = new HealthComponent();
            if (card != null) { Health.MaxHealth = card.TotalHealthWith(TurretModule); }
            // get_destroyed is connected to health_component.on_death at setup.
            Health.OnDeath += GetDestroyed;
            Health.OnDamageInfoTaken += RaiseNetDamageTaken;

            // setup(): ModuleGun.FireMode.BURST and .AUTOLOAD load a magazine,
            // so they get the clip clock instead of the plain one.
            Gun = GunInfo.FireMode == 1 || GunInfo.FireMode == 2
                ? gameObject.AddComponent<BattleGunBurst>()
                : gameObject.AddComponent<BattleGun>();
            Gun.Setup(GunInfo, this);
            Gun.Shoot += ShootShell;

            BuildArmor();
            InitModules();
            AmmoZones.Build(this);
            MeasureArmor();
            BuildHelperPoints();

            // tank_particles_manager.setup: every tank but the hangar one
            // carries its own stopped explosion1.tscn, and the hangar in this
            // port never assembles a BattleTank.
            Wreck = BattleTankWreck.Attach(this);

            // Buoyancy, drag, the drowning countdown and the under-water gun.
            Water = GetComponent<TankWaterPhysics>();
            if (Water == null) { Water = gameObject.AddComponent<TankWaterPhysics>(); }
            Water.Setup(this, _rb);
        }

        /// <summary>The hull's water state; see <see cref="TankWaterPhysics"/>.</summary>
        public TankWaterPhysics Water { get; private set; }

        /// <summary>
        /// <c>base_tank.drive</c>. The blocked branch writes the suspension axes
        /// straight through and never touches the command queue, so a command
        /// queued a moment earlier still lands afterwards.
        /// </summary>
        public void Drive(float forward, float right, bool lowPriority = false)
        {
            if (!IsEqualApprox(forward, 0f)) { forward = SuspensionTuning.Sign(forward); }
            if (!IsEqualApprox(right, 0f)) { right = SuspensionTuning.Sign(right); }

            var emergencyTrack = TracksCrashed && CanUseEmergencyTracks;
            if (Blocked || (TracksCrashed && !emergencyTrack))
            {
                _fwAxisLow = 0f;
                _rAxisLow = 0f;
                _fwAxis = 0f;
                _rAxis = 0f;
                if (Suspension != null)
                {
                    Suspension.SetDriveSpeedCaps(1f, 1f);
                    Suspension.StopDrive();
                }
                return;
            }
            ApplyTrackDriveCaps();
            if (emergencyTrack)
            {
                if ((forward > 0f && Suspension.DriveForwardLimitMultiplier <= 0f)
                    || (forward < 0f && Suspension.DriveBackwardLimitMultiplier <= 0f)) forward = 0f;
                // A single broken belt keeps differential steering within each authored belt-speed limit.
                // With both emergency belts destroyed the supported command remains straight retreat.
                if (TrackCrashed(TrackSide.Left) && TrackCrashed(TrackSide.Right)) right = 0f;
            }

            if (lowPriority && Time.time < _lowPriorityLockedUntil)
            {
                _fwAxisLow = 0f;
                _rAxisLow = 0f;
                return;
            }

            // The source reads the previous high-priority forward axis here, even
            // when the call itself is low priority.
            var newRight = SuspensionTuning.SteerAxis(right, _fwAxis);
            if (lowPriority)
            {
                _fwAxisLow = forward;
                _rAxisLow = newRight;
            }
            else
            {
                _fwAxis = forward;
                _rAxis = newRight;
            }
        }

        /// <summary><c>_low_priority_drive_locked_until_msec</c>.</summary>
        public void LockLowPriorityDrive(float seconds)
        {
            _lowPriorityLockedUntil = Time.time + seconds;
        }

        /// <summary>
        /// <c>base_tank.setup</c> / <c>setup_hull_phys</c>: everything the
        /// suspension needs comes off the assembled meshes.
        /// <c>tank_size</c> is the <c>hull_phys</c> AABB, <c>hull_bottom_offset</c>
        /// its lowest point in the hull's own space, and <c>tracks_height</c> the
        /// height of the right track.
        /// </summary>
        public static TankSuspension.HullGeometry MeasureHull(Bounds hull, Bounds tracks)
        {
            return new TankSuspension.HullGeometry
            {
                Width = hull.size.x,
                Length = hull.size.z,
                Height = hull.size.y + hull.min.y,
                TracksHeight = tracks.size.y,
                TracksBottom = tracks.min.y,
            };
        }

        /// <summary>Measures the two authored track meshes in hull space. The
        /// fallback is the suspension layout used by merged legacy imports.</summary>
        private void MeasureTrackMarkFootprint()
        {
            var fallbackWidth = Mathf.Max(0.18f, Hull.Width * 0.16f);
            var fallbackHalfSpacing = Suspension != null
                ? Suspension.DistanceSides * 0.5f
                : Hull.Width * SuspensionTuning.HullSideFactor * 0.5f;
            TrackMarkWidth = fallbackWidth;
            TrackMarkHalfSpacing = Mathf.Max(fallbackWidth * 0.55f, fallbackHalfSpacing);

            Bounds left;
            Bounds right;
            if (!TryTankLocalBounds(_trackObjectLeft, out left) ||
                !TryTankLocalBounds(_trackObjectRight, out right))
            {
                return;
            }

            var measuredWidth = (left.size.x + right.size.x) * 0.5f;
            var measuredHalfSpacing = Mathf.Abs(left.center.x - right.center.x) * 0.5f;
            if (measuredWidth > 0.05f && measuredHalfSpacing > 0.05f)
            {
                TrackMarkWidth = measuredWidth;
                TrackMarkHalfSpacing = Mathf.Max(
                    measuredWidth * 0.55f, measuredHalfSpacing);
            }
        }

        private bool TryTankLocalBounds(GameObject source, out Bounds result)
        {
            result = new Bounds();
            if (source == null) { return false; }
            var filters = source.GetComponentsInChildren<MeshFilter>(true);
            var found = false;
            for (var f = 0; f < filters.Length; f++)
            {
                var filter = filters[f];
                var mesh = filter.sharedMesh;
                if (mesh == null) { continue; }
                var bounds = mesh.bounds;
                for (var i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (i & 1) == 0 ? bounds.min.x : bounds.max.x,
                        (i & 2) == 0 ? bounds.min.y : bounds.max.y,
                        (i & 4) == 0 ? bounds.min.z : bounds.max.z);
                    var local = transform.InverseTransformPoint(
                        filter.transform.TransformPoint(corner));
                    if (!found) { result = new Bounds(local, Vector3.zero); found = true; }
                    else { result.Encapsulate(local); }
                }
            }
            return found;
        }

        /// <summary>Lowest visible track vertex in this tank's local space. Falls
        /// back to the mesh-space minimum when no separate track object exists.</summary>
        private float TrackBottomLocal(float fallback)
        {
            var bottom = float.PositiveInfinity;
            foreach (var track in new[] { _trackObjectLeft, _trackObjectRight })
            {
                var filter = track != null ? track.GetComponent<MeshFilter>() : null;
                if (filter == null || filter.sharedMesh == null) continue;
                var b = filter.sharedMesh.bounds;
                for (var i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (i & 1) == 0 ? b.min.x : b.max.x,
                        (i & 2) == 0 ? b.min.y : b.max.y,
                        (i & 4) == 0 ? b.min.z : b.max.z);
                    var local = transform.InverseTransformPoint(track.transform.TransformPoint(corner));
                    bottom = Mathf.Min(bottom, local.y);
                }
            }
            return float.IsInfinity(bottom) || float.IsNaN(bottom) ? fallback : bottom;
        }

        /// <summary>
        /// <c>self.mass = tank_info.hull_weight</c> plus the installed gun,
        /// engine and track weights.
        /// </summary>
        public static GunInfo RuntimeGun(GunInfo source, EquipmentModifiers modifiers)
        {
            modifiers = modifiers ?? new EquipmentModifiers();
            var gun = new GunInfo
            {
                Name = source.Name,
                DisplayName = source.DisplayName,
                Level = source.Level,
                CostXp = source.CostXp,
                Health = source.Health,
                Weight = source.Weight,
                Reload = source.Reload * modifiers.Reload,
                Dispersion = source.Dispersion,
                AimTime = source.AimTime * modifiers.AimTime,
                DeviationUp = source.DeviationUp,
                DeviationDown = source.DeviationDown,
                DeviationLeft = source.DeviationLeft,
                DeviationRight = source.DeviationRight,
                Caliber = source.Caliber,
                ShotSound = source.ShotSound,
                FireMode = source.FireMode,
                ImpulseMultiplier = source.ImpulseMultiplier,
                RotationSpeed = source.RotationSpeed,
                MaxAmmo = source.MaxAmmo,
                DispersionTurret = source.DispersionTurret * modifiers.Dispersion,
                DispersionShot = source.DispersionShot * modifiers.Dispersion,
                DispersionDamaged = source.DispersionDamaged * modifiers.Dispersion,
            };
            gun.Burst.IntraClip = source.Burst.IntraClip * modifiers.Reload;
            gun.Burst.IntraBurst = source.Burst.IntraBurst * modifiers.Reload;
            gun.Burst.Bursts.AddRange(source.Burst.Bursts);
            foreach (var seconds in source.AutoReloadTimes)
            { gun.AutoReloadTimes.Add(seconds * modifiers.Reload); }
            gun.Shells.AddRange(source.Shells);
            return gun;
        }

        public static float MassOf(
            TankInfo card, int gunIndex, int engineIndex, int trackIndex,
            int turretIndex = 0)
        {
            if (card == null) { return 1f; }

            var mass = card.HullWeight;
            mass += WeightOf(card.Guns, card.GunIdFor(turretIndex, gunIndex));
            mass += WeightOf(card.Engines, engineIndex);
            mass += WeightOf(card.Tracks, trackIndex);
            // The turret weight of the client is deliberately left out: the
            // ported hull weight already carries the whole hull mass, so
            // adding it would move the balance of every ported card.
            return mass;
        }

        /// <summary>
        /// <c>_physics_process</c> pins the hull inside the map border. Written
        /// against a normalised <see cref="Bounds"/> the clamp survives the Z
        /// mirror untouched.
        /// </summary>
        public static Vector3 ClampPosition(Vector3 position, Bounds area)
        {
            return new Vector3(
                Mathf.Clamp(position.x, area.min.x, area.max.x),
                Mathf.Clamp(position.y, -HeightLimit, HeightLimit),
                Mathf.Clamp(position.z, area.min.z, area.max.z));
        }

        /// <summary>
        /// Second half of the border handling: a tank sitting exactly on an edge
        /// may only move inwards, and touching any edge also kills upward
        /// velocity so it cannot climb the wall.
        /// </summary>
        public static Vector3 LimitBorderVelocity(
            Vector3 position, Vector3 velocity, Bounds area)
        {
            var touched = false;
            if (IsEqualApprox(position.x, area.min.x))
            {
                velocity.x = Mathf.Max(velocity.x, 0f);
                touched = true;
            }
            if (IsEqualApprox(position.x, area.max.x))
            {
                velocity.x = Mathf.Min(velocity.x, 0f);
                touched = true;
            }
            if (IsEqualApprox(position.z, area.min.z))
            {
                velocity.z = Mathf.Max(velocity.z, 0f);
                touched = true;
            }
            if (IsEqualApprox(position.z, area.max.z))
            {
                velocity.z = Mathf.Min(velocity.z, 0f);
                touched = true;
            }

            if (touched) { velocity.y = Mathf.Min(velocity.y, 0f); }
            return velocity;
        }

        /// <summary>
        /// Godot's <c>is_equal_approx</c>: <c>CMP_EPSILON</c> scaled by the
        /// magnitude of the first operand, never below <c>CMP_EPSILON</c>.
        /// </summary>
        public static bool IsEqualApprox(float a, float b)
        {
            if (a == b) { return true; }

            var tolerance = SourceEpsilon * Mathf.Abs(a);
            if (tolerance < SourceEpsilon) { tolerance = SourceEpsilon; }
            return Mathf.Abs(a - b) < tolerance;
        }

        /// <summary><c>rotate_track_mat</c>'s per-tick UV step.</summary>
        public static float TrackScrollStep(
            float tracksSpeed, float hullLength, float rotationAdd)
        {
            if (hullLength <= 0f) { return 0f; }
            return (tracksSpeed / hullLength) + rotationAdd * 0.5f;
        }

        /// <summary><c>rotate_wheel</c>'s per-tick angle, in radians.</summary>
        public static float WheelSpin(float tracksSpeed, float radius, float rotationAdd)
        {
            if (radius <= 0f) { return 0f; }
            return (tracksSpeed * WheelSpeedFactor / radius) + rotationAdd;
        }

        /// <summary>
        /// Turns an imported tank mesh halfway around Y so its authored -Z
        /// front faces battle +Z without exchanging its left and right sides.
        /// Unity's OBJ importer already flipped X: reflecting only Z here
        /// mirrored every tank relative to the hangar. This proper rotation
        /// flips X and Z together, preserving winding and tangent handedness.
        /// The historical method name is retained for existing callers.
        /// Results are cached and the shared hangar source remains untouched.
        /// </summary>
        public static Mesh Mirror(Mesh source)
        {
            if (source == null) { return null; }

            Mesh cached;
            if (MirroredMeshes.TryGetValue(source, out cached) && cached != null)
            {
                return cached;
            }

            var mirrored = new Mesh
            {
                name = source.name + "Mirrored",
                indexFormat = source.indexFormat,
            };

            var vertices = source.vertices;
            for (var i = 0; i < vertices.Length; i++)
            {
                vertices[i].x = -vertices[i].x;
                vertices[i].z = -vertices[i].z;
            }
            mirrored.vertices = vertices;

            var normals = source.normals;
            if (normals != null && normals.Length == vertices.Length)
            {
                for (var i = 0; i < normals.Length; i++)
                {
                    normals[i].x = -normals[i].x;
                    normals[i].z = -normals[i].z;
                }
                mirrored.normals = normals;
            }

            var tangents = source.tangents;
            if (tangents != null && tangents.Length == vertices.Length)
            {
                for (var i = 0; i < tangents.Length; i++)
                {
                    tangents[i].x = -tangents[i].x;
                    tangents[i].z = -tangents[i].z;
                }
                mirrored.tangents = tangents;
            }

            var uv = source.uv;
            if (uv != null && uv.Length == vertices.Length) { mirrored.uv = uv; }

            mirrored.subMeshCount = source.subMeshCount;
            for (var subMesh = 0; subMesh < source.subMeshCount; subMesh++)
            {
                var triangles = source.GetTriangles(subMesh);
                mirrored.SetTriangles(triangles, subMesh);
            }

            mirrored.RecalculateBounds();
            MirroredMeshes[source] = mirrored;
            return mirrored;
        }

        /// <summary>Bounds of one submesh, for an importer that merges records.</summary>
        public static Bounds SubMeshBounds(Mesh mesh, int subMesh)
        {
            var bounds = new Bounds();
            if (mesh == null || subMesh < 0 || subMesh >= mesh.subMeshCount)
            {
                return bounds;
            }

            var vertices = mesh.vertices;
            var triangles = mesh.GetTriangles(subMesh);
            var started = false;
            for (var i = 0; i < triangles.Length; i++)
            {
                var point = vertices[triangles[i]];
                if (!started)
                {
                    bounds = new Bounds(point, Vector3.zero);
                    started = true;
                    continue;
                }
                bounds.Encapsulate(point);
            }

            return bounds;
        }

        /// <summary><c>effective_dispersion</c>.</summary>
        public float EffectiveDispersion()
        {
            // The source triples the cone while the duplet ability is up, and
            // nothing in the battle slice grants abilities.
            return DispersionWanted(CurrentSettings()) ? Dispersion : 0f;
        }

        /// <summary>
        /// save_settings "NO_DISP": with the row on, the gun keeps a pinpoint
        /// cone however the tank is moving.
        /// </summary>
        public static bool DispersionWanted(SettingsData settings)
        {
            return settings == null || !settings.NoShotDispersion;
        }

        private static SettingsData CurrentSettings()
        {
            return BlitzBootstrap.Current != null
                ? BlitzBootstrap.Current.BattleRules : null;
        }

        /// <summary>
        /// <c>disp_manager</c>. The MS-1 takes the legacy branch: its tracks
        /// ship no <c>disp_rotation_mul</c>, so the audited Blitz model the same
        /// function keeps for the vehicles that do never runs here.
        /// </summary>
        private void DispersionManager(float delta)
        {
            if (GunInfo == null || TrackInfo == null || Suspension == null) { return; }

            if (!TrackInfo.UsesBlitzDispersionModel)
            {
                Dispersion = DispersionMath.LegacyStep(
                    GunInfo.Dispersion,
                    Dispersion,
                    GunInfo.AimTime,
                    TurretAngularVelocity,
                    GunInfo.DispersionTurret,
                    TrackInfo.DispersionMoveMultiplier,
                    (int)Suspension.RightAxis,
                    Suspension.Speed,
                    Suspension.AnyHit,
                    delta);
                return;
            }

            var moving = DispersionMath.FromVelocity(
                GunInfo.Dispersion,
                TrackInfo.DispersionMoveMultiplier,
                TrackInfo.DispersionRotationMultiplier,
                GunInfo.DispersionTurret,
                Suspension.SpeedMs,
                _rb != null ? _rb.angularVelocity.magnitude : 0f,
                TurretAngularVelocity);

            Dispersion = moving > Dispersion
                ? moving
                : DispersionMath.Recover(Dispersion, GunInfo.Dispersion, GunInfo.AimTime, delta);
        }

        /// <summary><c>disp_bloom</c>: the kick a shot gives the cone.</summary>
        public void DispersionBloom(float coeff)
        {
            if (GunInfo == null) { return; }

            Dispersion = TrackInfo != null && TrackInfo.UsesBlitzDispersionModel
                ? DispersionMath.AfterShot(GunInfo.Dispersion, Dispersion, coeff)
                : DispersionMath.LegacyBloom(Dispersion, coeff);
        }

        /// <summary>Stock is independent of death, battle locks and trigger cooldowns.</summary>
        public bool HasShellAmmo(int index)
        {
            if (GunInfo == null || index < 0 || index >= GunInfo.Shells.Count) { return false; }
            return _shellAmounts.Count == 0 || SandboxBattle.InfiniteAmmo(this)
                || (index < _shellAmounts.Count && _shellAmounts[index] > 0);
        }

        /// <summary>Includes rounds in the magazine; they are debited only on launch.</summary>
        public int AvailableShellCount
        {
            get
            {
                if (GunInfo == null || GunInfo.Shells.Count == 0) { return 0; }
                if (_shellAmounts.Count == 0 || SandboxBattle.InfiniteAmmo(this)) { return int.MaxValue; }
                long total = 0;
                for (var i = 0; i < GunInfo.Shells.Count && i < _shellAmounts.Count; i++)
                    total += _shellAmounts[i];
                return (int)System.Math.Min(int.MaxValue, total);
            }
        }

        /// <summary>Keep the chosen type while stocked, otherwise cycle to the next stocked slot.</summary>
        public bool EnsureShellAvailable()
        {
            if (HasShellAmmo(ShellId)) { return true; }
            var count = GunInfo != null ? GunInfo.Shells.Count : 0;
            for (var offset = 1; offset <= count; offset++)
            {
                var slot = ((ShellId % count + count) % count + offset) % count;
                if (!HasShellAmmo(slot)) { continue; }
                SelectShell(slot);
                return true;
            }
            return false;
        }

        /// <summary><c>can_launch_shell</c>.</summary>
        public bool CanLaunchShell(int index)
        {
            return !Dead && !Blocked && HasShellAmmo(index);
        }

        public void SetShellAmounts(IReadOnlyList<int> amounts)
        {
            _shellAmounts.Clear();
            if (amounts != null)
            {
                for (var i = 0; i < amounts.Count; i++)
                    _shellAmounts.Add(Mathf.Max(0, amounts[i]));
            }
            EnsureShellAvailable();
            Gun?.RefreshAmmunition();
        }

        /// <summary>
        /// <c>projectile_fired(first_shot, shell_id)</c>, which the player HUD
        /// listens to so the rack counters follow the gun instead of polling.
        /// </summary>
        public event Action<bool, int> ProjectileFired;

        /// <summary>
        /// Network client: the host's damage model resolved a hit on this
        /// puppet. Presentation only (sound, markers); health still arrives
        /// with the replicated state.
        /// </summary>
        public event Action<DamageInfo> NetDamageFeedback;

        public void ApplyNetDamageFeedback(DamageInfo info)
        {
            if (!NetPuppet || info == null || Health == null || Health.Dead) { return; }
            NetDamageFeedback?.Invoke(info);
        }

        private void RaiseNetDamageTaken(DamageInfo info)
        {
            if (!BattleNetHooks.PuppetWorld) { BattleNetHooks.RaiseDamageTaken(this, info); }
        }

        /// <summary>Every successfully launched scheduler round, including non-bloom burst rounds.
        /// Extra duplet rounds do not raise this event: firstShot is not an origin flag.</summary>
        public event Action<int> PrimaryProjectileFired;
        public int PendingDupletShots { get; internal set; }

        /// <summary>
        /// <c>select_shell</c>: the id is clamped to the gun's rack, so the
        /// source lets a key for a slot the gun does not carry fall back onto
        /// the last one it does.
        /// </summary>
        public void SelectShell(int id)
        {
            var count = GunInfo != null ? GunInfo.Shells.Count : 0;
            if (count <= 0) { return; }

            var selected = Mathf.Clamp(id, 0, count - 1);
            var changed = ShellId != selected;
            ShellId = selected;
            if (changed && View != null && View.ArtyMode && !View.ArtyAvailable)
            {
                // Keep overhead aim when switching missiles; only an ordinary
                // shell makes this view unavailable.
                View.SetArtyMode(false);
            }

            // select_shell repaints the tank the scope is already resting on,
            // so swapping high explosive for armour piercing recolours the
            // plates where they are instead of waiting for the reticle to
            // leave the target and come back.
            if (View != null && View.SniperMode
                && TankInAim != null && !TankInAim.Dead
                && TankInAim.Team != Team)
            {
                TankInAim.PreviewArmor(false, null);
                TankInAim.PreviewArmor(true, CurrentShell());
            }
        }

        // ------------------------------------------------------------------
        // Consumables: add_consumable / use_consumable of base_tank.gd.
        // ------------------------------------------------------------------

        private readonly List<ConsumableRuntime> _consumables =
            new List<ConsumableRuntime>();

        /// <summary><c>active_consumables</c>, at most MAX_CONSUMABLES long.</summary>
        public IReadOnlyList<ConsumableRuntime> ActiveConsumables
        {
            get { return _consumables; }
        }

        /// <summary><c>on_consumable_used(cons)</c>.</summary>
        public event Action<ConsumableInfo> ConsumableUsed;

        /// <summary>
        /// <c>tank_info.turret_rot_speed</c>. base_tank.setup duplicates the
        /// card per tank, so the improved fuel may only speed up this hull's
        /// turret; the shared <see cref="TankInfo"/> asset must stay untouched.
        /// </summary>
        public float TurretRotationSpeed { get; set; }

        /// <summary>
        /// <c>add_consumable</c>: mounted items past MAX_CONSUMABLES are
        /// dropped, and a PASSIVE one is applied the moment it is mounted.
        /// </summary>
        public ConsumableRuntime AddConsumable(string id)
        {
            var info = ConsumableCatalog.Get(id);
            if (info == null) { return null; }
            if (_consumables.Count >= ConsumableCatalog.MaxActive) { return null; }

            var active = new ConsumableRuntime(info);
            var applied = false;
            active.Apply = () =>
            {
                // Consumable.on_apply refuses while its own meta flag is set.
                if (applied) { return false; }
                if (!ApplyConsumableEffect(info)) { return false; }

                PlayConsumableSound(info.ApplySound);
                applied = true;
                return true;
            };
            active.Stop = () =>
            {
                // on_stop_use is a no-op without that same meta flag.
                if (!applied) { return; }

                PlayConsumableSound(info.StopSound);
                StopConsumableEffect(info);
                applied = false;
            };
            active.Message = SendPlayerMessage;
            _consumables.Add(active);

            if (info.Type == ConsumableType.Passive)
            {
                active.UseTry(Time.time, true);
            }

            return active;
        }

        /// <summary><c>use_consumable(id, force)</c>.</summary>
        public void UseConsumable(int id, bool force = false)
        {
            if (id < 0 || id >= _consumables.Count) { return; }

            var active = _consumables[id];
            active.UseTry(Time.time, force);
            if (ConsumableUsed != null) { ConsumableUsed(active.Info); }
        }

        /// <summary><c>send_player_message</c>, which the source only prints.</summary>
        public void SendPlayerMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) { return; }

            Debug.Log("Player message: " + message);
        }

        /// <summary>Per-hull loader modifier; never changes projectile or intra-clip cadence.</summary>
        public float AdrenalineReloadMultiplier { get; private set; } = 1f;

        /// <summary>The _on_apply lambda db_tanks.gd registers per item.</summary>
        private bool ApplyConsumableEffect(ConsumableInfo info)
        {
            switch (info.Id)
            {
                case ConsumableCatalog.RepairKit:
                    return RepairEverything();
                case ConsumableCatalog.Berserk:
                    if (GunInfo == null) { return false; }
                    AdrenalineReloadMultiplier = 0.75f;
                    Gun?.ScaleReloadTime(0.75f);
                    return true;
                case ConsumableCatalog.Afterburning:
                    if (Suspension == null) { return false; }
                    Suspension.EnginePower *= 1.25f;
                    Suspension.MaxSpeedForward += 5f;
                    Suspension.MaxSpeedBack += 2f;
                    return true;
                case ConsumableCatalog.Fuel:
                    if (Suspension == null) { return false; }
                    Suspension.EnginePower *= 1.1f;
                    Suspension.RotationSpeed *= 1.1f;
                    TurretRotationSpeed *= 1.1f;
                    Suspension.MaxSpeedForward += 3f;
                    Suspension.MaxSpeedBack += 1f;
                    return true;
                default:
                    // Every ration is apply_crew_mastery_bonus(0.9): a burst
                    // gun keeps its reload, every gun gets the aim time.
                    if (GunInfo == null) { return false; }
                    // ModuleGun.FireMode.NORMAL is the zero of that enum.
                    if (GunInfo.FireMode == 0)
                    {
                        GunInfo.Reload *= 0.9f;
                    }

                    GunInfo.AimTime *= 0.9f;
                    return true;
            }
        }

        /// <summary>The _on_stop_use lambda; only the two timed items have one.</summary>
        private void StopConsumableEffect(ConsumableInfo info)
        {
            switch (info.Id)
            {
                case ConsumableCatalog.Berserk:
                    AdrenalineReloadMultiplier = 1f;
                    Gun?.ScaleReloadTime(1f / 0.75f);
                    break;
                case ConsumableCatalog.Afterburning:
                    if (Suspension == null) { break; }
                    Suspension.EnginePower /= 1.25f;
                    Suspension.MaxSpeedForward -= 5f;
                    Suspension.MaxSpeedBack -= 2f;
                    break;
            }
        }

        /// <summary>Consumable.on_apply only plays its cue for the player.</summary>
        private void PlayConsumableSound(string clip)
        {
            if (string.IsNullOrEmpty(clip) || View == null) { return; }

            var audio = BlitzBootstrap.Current != null
                ? BlitzBootstrap.Current.Audio : null;
            if (audio == null) { return; }

            audio.PlaySound2D(clip, AudioBus.Ui, false, 1f);
        }

        /// <summary>The two Util.timer_curtime deadlines of every mounted item.</summary>
        private void TickConsumables()
        {
            for (var i = 0; i < _consumables.Count; i++)
            {
                _consumables[i].Tick(Time.time);
            }
        }

        /// <summary>
        /// The <c>0.066</c> the aim trace hands <c>switch_armor_outline</c>:
        /// the overlay dies a frame after the scope leaves the target.
        /// </summary>
        public const float ArmorPreviewHold = 0.066f;

        /// <summary><c>armor_shown</c>.</summary>
        public bool ArmorShown { get; private set; }

        /// <summary><c>outline_shown</c>.</summary>
        public bool OutlineShown { get; private set; }

        /// <summary>
        /// <c>tank_in_aim</c>: whoever the crosshair ray is resting on. The
        /// source arms the overlay from <c>cam_trace</c>, not from the
        /// ballistic gun trace, because the stepped parabola of the gun trace
        /// skips past a hull between two of its segments and makes the overlay
        /// blink while the reticle has not moved at all.
        /// </summary>
        public BattleTank TankInAim { get; private set; }

        private BattleAutoAim _autoAim;

        /// <summary>
        /// The <c>auto_aim_*</c> capture state of base_tank.gd. Created on
        /// demand, so a hull that never aims pays nothing for it.
        /// </summary>
        public BattleAutoAim AutoAim
        {
            get { return _autoAim ?? (_autoAim = new BattleAutoAim(this)); }
        }

        /// <summary>
        /// <c>auto_aim_offset</c>: how far over the hull origin the aim point
        /// sits, which is also the first helper point below.
        /// </summary>
        public float AutoAimOffset { get; private set; }

        /// <summary><c>auto_aim_point()</c>.</summary>
        public Vector3 AutoAimPoint()
        {
            return transform.position + Vector3.up * AutoAimOffset;
        }

        /// <summary>
        /// The tail of <c>cam_trace</c>: paint the armour of whoever the
        /// crosshair rests on, and trace every tank it rests on, dead ones and
        /// the tank's own hull aside.
        /// </summary>
        public void AimTraceHit(Collider collider)
        {
            var tank = collider != null ? FindTank(collider) : null;
            if (tank == null || tank == this || tank.LooksDeadTo(this))
            {
                TankInAim = null;
                return;
            }

            TankInAim = tank;
            var enemy = tank.Team != Team;
            tank.OutlineSetup(enemy);
            tank.SwitchArmorOutline(
                View != null && View.SniperMode && enemy,
                true,
                ArmorPreviewHold,
                CurrentShell());
        }

        /// <summary>
        /// The shell the plates are currently painted against. The source
        /// repaints them from <c>select_shell</c>; keeping the shell here lets
        /// the aim trace notice the swap on its own, so the overlay follows a
        /// mid-aim change from high explosive to armour piercing instead of
        /// staying amber until the scope leaves the target and comes back.
        /// </summary>
        private ShellInfo _previewShell;

        /// <summary>
        /// <c>switch_armor_outline</c>. The aim trace re-arms the deadline every
        /// tick it holds this tank, and a change of state repaints the plates.
        /// </summary>
        public void SwitchArmorOutline(
            bool armor, bool outline, float duration, ShellInfo shell)
        {
            _nextRemoveArmorPreview = Time.time + duration;
            if (armor != ArmorShown || (armor && shell != _previewShell))
            {
                PreviewArmor(armor, shell);
            }

            if (outline != OutlineShown) { SwitchOutline(outline); }
        }

        /// <summary>
        /// <c>preview_armor</c>: show or hide every plate's overlay and write
        /// the shell the shader colours it against.
        /// </summary>
        public void PreviewArmor(bool start, ShellInfo shell)
        {
            // A wreck, real or played, never carries the armour paint.
            if (_forceCrashedTracksVisual) { start = false; }
            ArmorShown = start;
            _previewShell = start ? shell : null;
            for (var i = 0; i < _plates.Count; i++)
            {
                if (_plates[i] != null) { _plates[i].ShowPreview(start, shell); }
            }
        }

        /// <summary>
        /// The <c>_physics_process</c> line that drops the overlay once the
        /// aim trace stops re-arming it.
        /// </summary>
        private void TickArmorPreview()
        {
            if (_nextRemoveArmorPreview < 0f ||
                Time.time <= _nextRemoveArmorPreview)
            {
                return;
            }

            _nextRemoveArmorPreview = -1f;
            if (ArmorShown) { PreviewArmor(false, null); }
            if (OutlineShown) { SwitchOutline(false); }
        }

        /// <summary>
        /// <c>outline_switch</c>: the source keeps the outline pass on every
        /// tank material and only drives the alpha of its colour, so a tank
        /// under the reticle is traced in its team colour and a wreck is not
        /// traced at all.
        /// </summary>
        public void SwitchOutline(bool on)
        {
            OutlineShown = on;
            if (Dead || _forceCrashedTracksVisual) { on = false; }

            for (var i = 0; i < _outlineMaterials.Count; i++)
            {
                var material = _outlineMaterials[i];
                if (material == null) { continue; }

                var colour = material.GetColor(TankOutlineShader.Color);
                colour.a = on ? 1f : 0f;
                material.SetColor(TankOutlineShader.Color, colour);
            }
        }

        /// <summary>
        /// <c>outline_width</c> of every outline pass: the Sandbox wallhack
        /// draws a thicker trace and puts the default back afterwards.
        /// </summary>
        public void SetOutlineWidth(float width)
        {
            for (var i = 0; i < _outlineMaterials.Count; i++)
            {
                var material = _outlineMaterials[i];
                if (material != null) { material.SetFloat(TankOutlineShader.Width, width); }
            }
        }

        private bool _forceVisible;

        /// <summary>
        /// The Sandbox wallhack's <c>target.visible = true</c>: an enemy model
        /// stays drawn while unspotted. Releasing it hands visibility back to
        /// spotting (<c>visible = spotted or dead</c>).
        /// </summary>
        public void ForceModelVisible(bool forced)
        {
            if (_forceVisible == forced) { return; }
            _forceVisible = forced;
            ApplySpottedVisibility();
        }

        /// <summary><c>set_outline_color</c>.</summary>
        public void SetOutlineColor(Color colour)
        {
            for (var i = 0; i < _outlineMaterials.Count; i++)
            {
                var material = _outlineMaterials[i];
                if (material == null) { continue; }

                colour.a = OutlineShown ? 1f : 0f;
                material.SetColor(TankOutlineShader.Color, colour);
            }
        }

        /// <summary>
        /// <c>outline_setup</c>: allies are traced green and enemies red. The
        /// team of the tank that owns the camera decides which is which, so a
        /// spectated bot sees the same colours its own side does.
        /// </summary>
        public void OutlineSetup(bool enemy)
        {
            SetOutlineColor(enemy
                ? TankOutlineShader.EnemyColor
                : TankOutlineShader.AllyColor);
        }

        /// <summary><c>current_shell</c>.</summary>
        public ShellInfo CurrentShell()
        {
            if (GunInfo == null || GunInfo.Shells.Count == 0) { return null; }

            return GunInfo.Shells[Mathf.Clamp(ShellId, 0, GunInfo.Shells.Count - 1)];
        }

        /// <summary><c>gun_dist_to_aim</c>: the gun to wherever the aim ray landed.</summary>
        public float GunDistanceToAim()
        {
            if (Turret == null) { return 0f; }

            var gun = Turret.GunPivot != null ? Turret.GunPivot.position : Turret.GunPosition;
            var target = GunTracePoint == Vector3.zero ? Turret.AimTarget : GunTracePoint;
            return Vector3.Distance(gun, target);
        }

        /// <summary>
        /// <c>gun_trace</c>: trace the current shell parabola for 720 metres,
        /// then place the two-sided dispersion offset around its endpoint.
        /// </summary>
        public void UpdateGunTrace(bool cheap = false)
        {
            using var perfScope = BattlePerfMarkers.GunTrace.Auto();
            GunTraceCollider = null;
            GunTraceTank = null;
            GunTraceNormal = Vector3.zero;
            DispersionOffset = Vector3.zero;

            var shell = CurrentShell();
            if (Dead || shell == null || Turret == null || Turret.GunPivot == null)
            {
                GunTracePoint = Turret != null ? Turret.AimTarget : transform.position;
                DispersionPoint = GunTracePoint;
                return;
            }

            var speed = Mathf.Abs(shell.Speed);
            if (speed <= SourceEpsilon)
            {
                GunTracePoint = Turret.GunPosition;
                DispersionPoint = GunTracePoint;
                return;
            }

            var origin = Turret.GunPosition;
            var direction = Turret.GunPivot.forward;
            // gun_trace builds the shell's own gravity and then zeroes it for a
            // homing round, so a rocket traces the straight line it actually
            // flies instead of a parabola that lands next to the hull.
            var gravity = AimMath.ShellGravity(shell.GravityMultiplier, shell.Atgm)
                * BattleProjectile.ShellGravityScaleNow();
            var flightTime = AimMath.TraceDistance / speed;
            var steps = AimMath.GunTraceSteps(
                flightTime, gravity, cheap, Application.isMobilePlatform);

            RaycastHit hit;
            GunTracePoint = TraceBallistic(
                origin, direction, speed, gravity, flightTime, steps, out hit);
            if (hit.collider != null)
            {
                GunTraceCollider = hit.collider;
                GunTraceNormal = hit.normal;
                GunTraceTank = FindTank(hit.collider);
                if (GunTraceTank != null && GunTraceTank.LooksDeadTo(this))
                {
                    GunTraceTank = null;
                }
            }

            var distance = Vector3.Distance(Turret.GunPivot.position, GunTracePoint);
            DispersionOffset = Turret.GunPivot.right
                * GunMath.DispersionOffset(distance, EffectiveDispersion());
            DispersionPoint = GunTracePoint + DispersionOffset;
        }

        /// <summary>
        /// Walks the ballistic arc segment by segment and stops at the first
        /// segment that meets a collider that is not this tank's own. All
        /// segment rays are answered in one physics batch first; the walk over
        /// their results is the same as casting them one after another.
        /// </summary>
        private Vector3 TraceBallistic(
            Vector3 origin,
            Vector3 direction,
            float speed,
            float gravity,
            float flightTime,
            int steps,
            out RaycastHit hit)
        {
            hit = default(RaycastHit);
            steps = Mathf.Max(1, steps);
            var timeStep = flightTime / steps;
            var velocity = direction.normalized * speed;
            velocity.y -= gravity * timeStep * 0.5f;
            var from = origin;

            if (GunTraceRayIndex.Length < steps)
            {
                GunTraceRayIndex = new int[steps];
            }

            GunTraceRays.Clear();
            for (var i = 0; i < steps; i++)
            {
                var to = from + velocity * timeStep;
                var segment = to - from;
                var distance = segment.magnitude;
                GunTraceRayIndex[i] = distance <= SourceEpsilon ? -1 : GunTraceRays.Add(
                    from, segment / distance, distance, BattleLayers.AimMask, QueryTriggerInteraction.Ignore);
                velocity.y -= gravity * timeStep;
                from = to;
            }

            GunTraceRays.Run();
            for (var i = 0; i < steps; i++)
            {
                if (GunTraceRayIndex[i] >= 0 && NearestForeignHit(GunTraceRayIndex[i], out hit))
                {
                    return hit.point;
                }
            }

            hit = default(RaycastHit);
            return from;
        }

        private bool NearestForeignHit(int ray, out RaycastHit best)
        {
            best = default(RaycastHit);
            var count = GunTraceRays.Hits(ray, _gunTraceHits);
            var found = false;
            for (var i = 0; i < count; i++)
            {
                var collider = _gunTraceHits[i].collider;
                if (collider == null || Owns(collider)) { continue; }
                if (!found || _gunTraceHits[i].distance < best.distance)
                {
                    best = _gunTraceHits[i];
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// The <c>min_armor</c> / <c>max_armor</c> walk of <c>setup</c>. Both
        /// start at zero and the second test is an <c>elif</c>, so the minimum
        /// stays at zero for a tank whose thinnest plate is thicker than that
        /// and the average ends up at half the thickest plate. Ported as
        /// written, because the AI's armour ratios are tuned against it.
        /// </summary>
        private void MeasureArmor()
        {
            var minArmor = 0;
            var maxArmor = 0;
            for (var i = 0; i < _plates.Count; i++)
            {
                // The track module is its own body in the source and never
                // joins armor_nodes, so it does not move min/max armour.
                if (!string.IsNullOrEmpty(_plates[i].Module)) { continue; }

                var armor = _plates[i].Armor;
                if (armor < minArmor)
                {
                    minArmor = armor;
                }
                else if (armor > maxArmor)
                {
                    maxArmor = armor;
                }
            }

            AvgArmor = (int)((minArmor + maxArmor) * 0.5f);
        }

        /// <summary>
        /// <c>create_helper_point</c>: the aim points an AI shoots at and the
        /// spotting points a visibility trace runs between. The source measures
        /// them off three AABBs - the physics hull, the visual hull and the
        /// turret - and the port only keeps the physics hull and the turret
        /// renderers, so the hull points sit on the same box as the collider.
        /// </summary>
        private void BuildHelperPoints()
        {
            _aimPoints.Clear();
            _spotPoints.Clear();

            var lengthHalf = Hull.Length * 0.5f;
            var widthHalf = Hull.Width * 0.5f;
            var heightHalf = Hull.Height * 0.5f;
            var turretSize = TurretSize();
            var noTurret = turretSize == Vector3.zero;

            // auto_aim_offset, which is also the first aim point.
            var autoAimOffset = noTurret ? Hull.Height * 0.55f : Hull.Height * 0.75f;
            AutoAimOffset = autoAimOffset;
            AddHelperPoint(Vector3.up * autoAimOffset, true, false);

            for (var sx = -1; sx <= 1; sx += 2)
            {
                for (var sz = -1; sz <= 1; sz += 2)
                {
                    var corner = ((Vector3.forward * lengthHalf * sz)
                        + (Vector3.right * widthHalf * sx)) * 0.65f;
                    AddHelperPoint(corner + (Vector3.up * heightHalf), true, false);
                }
            }

            var turretOffset = new Vector3(0f, turretSize.y * 0.5f, 0f);
            AddHelperPoint(turretOffset * 1.3f, true, true);
            if (!noTurret)
            {
                AddHelperPoint(turretOffset + (Vector3.right * turretSize.x * 0.25f), true, true);
                AddHelperPoint(turretOffset - (Vector3.right * turretSize.x * 0.25f), true, true);
                AddHelperPoint(turretOffset + (Vector3.forward * turretSize.z * 0.4f), true, true);
                AddHelperPoint(turretOffset - (Vector3.forward * turretSize.z * 0.4f), true, true);
            }

            for (var sx = -1; sx <= 1; sx += 2)
            {
                for (var sz = -1; sz <= 1; sz += 2)
                {
                    var corner = (Vector3.forward * lengthHalf * sz)
                        + (Vector3.right * widthHalf * sx)
                        + (Vector3.up * heightHalf);
                    AddHelperPoint(corner * 0.75f, false, false);
                }
            }

            var turretLocal = Turret != null && Turret.TurretPivot != null
                ? transform.InverseTransformPoint(Turret.TurretPivot.position)
                : Vector3.zero;
            AddHelperPoint(
                new Vector3(turretLocal.x, Hull.Height, turretLocal.z), false, false);
        }

        /// <summary>One helper point, parented to the hull or to the turret.</summary>
        private Transform AddHelperPoint(Vector3 offset, bool aim, bool turretAttach)
        {
            var parent = turretAttach && Turret != null && Turret.TurretPivot != null
                ? Turret.TurretPivot
                : transform;
            var list = aim ? _aimPoints : _spotPoints;
            var host = new GameObject(
                (aim ? "aim_point_" : "spotting_point_") + (list.Count + 1));
            host.transform.SetParent(parent, false);
            host.transform.localPosition = offset;
            list.Add(host.transform);
            return host.transform;
        }

        /// <summary>The turret box the turret aim points are measured off.</summary>
        private Vector3 TurretSize()
        {
            if (Turret == null || Turret.TurretPivot == null)
            {
                return Vector3.zero;
            }

            var renderers = Turret.TurretPivot.GetComponentsInChildren<MeshRenderer>();
            if (renderers.Length == 0)
            {
                return Vector3.zero;
            }

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds.size;
        }

        private static BattleTank FindTank(Collider collider)
        {
            if (collider == null) { return null; }
            var plate = collider.GetComponent<BattleArmorPlate>();
            return plate != null ? plate.Tank : collider.GetComponentInParent<BattleTank>();
        }

        /// <summary><c>receive_damage</c>.</summary>
        public void ReceiveDamage(DamageInfo dinfo)
        {
            if (Health == null || dinfo == null || Health.Dead) { return; }

            // Security: on a network client the host alone decides damage;
            // a locally simulated hit must never move the health pool.
            if (BattleNetHooks.PuppetWorld) { return; }

            if (!DamageLands(CurrentSettings(), dinfo.AttackerTank as BattleTank, this))
            {
                return;
            }
            // Online friendly fire: a team-mate takes a quarter of the damage
            // and a tenth of a ram; the host counts what lands as team damage.
            var attacker = dinfo.AttackerTank as BattleTank;
            var bootstrap = BlitzBootstrap.Current;
            var session = bootstrap != null ? bootstrap.Battle : null;
            var online = session != null && session.Net != null;
            var friendly = attacker != null && attacker != this && attacker.Team == Team;
            if (friendly)
            {
                dinfo.Dmg = TeamKillPolicy.FriendlyDamage(dinfo.Dmg, dinfo.DmgType, online);
            }
            SandboxTank.ScaleOutgoing(dinfo);
            UprisingTank.Mediate(this, dinfo);
            BigBossTank.Mediate(this, dinfo);
            var before = Health.Health;
            Health.ReceiveDamage(dinfo);
            if (friendly && online && !dinfo.NotTeamDamage)
            {
                session.RecordTeamDamage(attacker, this, before - Health.Health);
            }
        }

        /// <summary>
        /// Blue team-killer status: its nick and HP bar turn light blue for
        /// team-mates and damage dealt to it is not team damage.
        /// </summary>
        public bool TeamKiller { get; set; }

        /// <summary>
        /// save_settings "BOTS_HARMLESS": a bot can still shoot, spot and ram,
        /// but nothing it deals reaches the tank the player drives.
        /// </summary>
        public static bool DamageLands(
            SettingsData settings, BattleTank attacker, BattleTank victim)
        {
            // The Sandbox has its own per-bot HARMLESS switch (the trigger is
            // skipped instead); the personal battle setting does not apply there.
            // War of Ducks is an event battle with fixed rules: every duck bites.
            if (settings == null || !settings.BotsHarmless || SandboxBattle.Active
                || WorldOfDucks.WorldOfDucksBattle.Active)
            {
                return true;
            }

            if (attacker == null || victim == null || !victim.IsPlayerDriven)
            {
                return true;
            }

            return !attacker.IsBotDriven;
        }

        /// <summary>True while the local player drives this tank.</summary>
        public bool IsPlayerDriven
        {
            get
            {
                var bootstrap = BlitzBootstrap.Current;
                return bootstrap != null && bootstrap.Battle != null
                    && bootstrap.Battle.Player == this;
            }
        }

        /// <summary>True when a bot brain drives this tank.</summary>
        public bool IsBotDriven
        {
            get { return !IsPlayerDriven; }
        }

        /// <summary><c>pos</c>.</summary>
        public Vector3 Pos()
        {
            return transform.position;
        }

        /// <summary><c>get_spot_pos</c>: the gun node, which rides the turret.</summary>
        public Vector3 SpotPosition()
        {
            if (Turret != null && Turret.GunPivot != null)
            {
                return Turret.GunPivot.position;
            }

            return transform.position;
        }

        /// <summary><c>get_gun_pos</c>: the muzzle pushed back along the barrel.</summary>
        public Vector3 GunShootPoint()
        {
            return Turret != null ? Turret.GunPosition : transform.position;
        }

        /// <summary><c>dist_sqr_to_tank</c>.</summary>
        public float DistanceSquaredTo(BattleTank other)
        {
            if (other == null)
            {
                return 0f;
            }

            return (other.transform.position - transform.position).sqrMagnitude;
        }

        /// <summary>
        /// <c>can_shoot</c>: the gun has to be loaded and the shell has to
        /// exist. The source also counts the carried rounds, which this slice
        /// does not track.
        /// </summary>
        public bool CanShoot()
        {
            return Gun != null && Gun.CanShoot() && CanLaunchShell(ShellId);
        }

        /// <summary><c>set_aim_target</c>.</summary>
        public void SetAimTarget(
            Vector3 target, bool clampY = false, bool ballisticLead = true)
        {
            if (Turret == null)
            {
                return;
            }

            Turret.AimBallisticLead = ballisticLead;
            Turret.SetAim(target, clampY);
        }

        /// <summary>
        /// The spotting flag, which raises <c>tank_got_unspotted</c> on the
        /// roster the way <c>Battle</c> does in the source.
        /// </summary>
        public void SetSpotted(bool value)
        {
            if (!value && BigBoss != null && BigBoss.State != null && BigBoss.State.Boss && !Dead) value = true;
            if (_spotted == value)
            {
                return;
            }

            _spotted = value;
            ApplySpottedVisibility();
            if (SpottedChanged != null)
            {
                SpottedChanged(value);
            }
            if (!value && BattleRoster.Current != null)
            {
                BattleRoster.Current.NotifyUnspotted(this);
            }
        }

        /// <summary><c>on_spotted(spotted)</c> of the source tank.</summary>
        public event System.Action<bool> SpottedChanged;

        /// <summary><c>just_shot()</c>: the muzzle flash gives a tank away.</summary>
        public bool JustShot()
        {
            return Gun != null && Gun.JustShot();
        }

        /// <summary><c>Util.curtime() - last_movement > 3</c>.</summary>
        public bool Parked()
        {
            return Time.time - LastMovement
                > BlitzLegacy.Core.Battle.SpottingMath.CamonetParkedSeconds;
        }

        /// <summary>
        /// <c>get_camo_factor()</c>: the hull camouflage, spoiled by a shot or
        /// by driving, and helped by a camonet on a parked tank.
        /// </summary>
        public float CamoFactor()
        {
            var camo = CamoFactorBase + CamouflageBonus;
            if (JustShot() && GunInfo != null)
            {
                camo -= Mathf.Pow(
                    GunInfo.Caliber
                        * BlitzLegacy.Core.Battle.SpottingMath.ShotCamoCaliberFactor,
                    BlitzLegacy.Core.Battle.SpottingMath.ShotCamoExponent);
            }

            if (Card != null && Card.Class != TankClass.Light
                && Suspension != null
                && Mathf.Abs(Suspension.Speed) > LastMovementSpeed)
            {
                camo *= BlitzLegacy.Core.Battle.SpottingMath.MovingCamoFactor;
            }

            if (Parked() && HasEquipment(
                    BlitzLegacy.Core.Battle.SpottingMath.CamonetId))
            {
                camo *= BlitzLegacy.Core.Battle.SpottingMath.CamonetFactor;
            }

            return Mathf.Max(camo, 0f);
        }

        /// <summary>Applies the current spotting state to the model again.</summary>
        public void RefreshSpottedVisibility()
        {
            ApplySpottedVisibility();
        }

        /// <summary>
        /// Godot hides the observed tank's meshes while its scope is open.
        /// <see cref="Renderer.forceRenderingOff"/> leaves spotting and armour
        /// preview ownership of <see cref="Renderer.enabled"/> untouched.
        /// </summary>
        public void SetScopeModelVisible(bool visible)
        {
            if (Model == null) { return; }

            var renderers = Model.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    renderers[i].forceRenderingOff = !visible;
                }
            }
        }

        /// <summary>
        /// <c>if is_enemy: self.visible = spotted</c>. Only the other team
        /// disappears; own tanks are always drawn.
        /// </summary>
        private void ApplySpottedVisibility()
        {
            var roster = BattleRoster.Current;
            if (roster == null || roster.PlayerTeam == Team)
            {
                return;
            }

            if (_spottedRenderers == null || _spottedRenderers.Length == 0)
            {
                _spottedRenderers = GetComponentsInChildren<Renderer>(true);
            }

            for (var i = 0; i < _spottedRenderers.Length; i++)
            {
                if (_spottedRenderers[i] != null)
                {
                    // Spotting owns the model visibility, not the scope toggle.
                    // Re-enabling every renderer also enabled hidden armor plates.
                    var renderer = _spottedRenderers[i];
                    renderer.enabled = (_spotted || _forceVisible)
                        && (!_armorPreviewRenderers.Contains(renderer) || ArmorShown);
                }
            }
        }

        /// <summary>
        /// <c>_on_ram_body_entered</c>: the relative approach speed along the
        /// axis between the two hulls, taken before and after the callback so a
        /// stationary heavy target still counts as a ram.
        /// </summary>
        private void OnCollisionEnter(Collision collision)
        {
            if (collision == null || collision.rigidbody == null)
            {
                return;
            }

            var other = collision.rigidbody.GetComponent<BattleTank>();
            if (other == null || other == this || Dead || other.Dead)
            {
                return;
            }

            var axis = other.transform.position - transform.position;
            if (axis.sqrMagnitude <= 0.0001f)
            {
                return;
            }

            axis.y = 0f;
            if (axis.sqrMagnitude < .0001f) return;
            axis = axis.normalized;
            var before = Vector3.Dot(PreviousVelocity - other.PreviousVelocity, axis);
            var now = Vector3.Dot(LinearVelocity - other.LinearVelocity, axis);
            BattleRam.Request(this, other, Mathf.Max(0f, Mathf.Max(before, now)));
        }

        /// <summary><c>trace_exclude</c>: the hull, its plates and the rest of the tank.</summary>
        public bool Owns(Collider collider)
        {
            return collider != null && collider.transform.IsChildOf(transform);
        }

        /// <summary>
        /// <c>get_destroyed</c>, which <c>health_component.on_death</c> calls.
        /// </summary>
        public void SetFakeWreck(bool on)
        {
            var changed = _forceCrashedTracksVisual != on;
            _forceCrashedTracksVisual = on;
            ShowHideTracks(on);
            if(Wreck!=null){if(on)Wreck.Play();else if(!Dead)Wreck.Stop();}
            if (on)
            {
                // Same as get_destroyed: the overlays drop at once.
                _nextRemoveArmorPreview = -1f;
                if (ArmorShown) { PreviewArmor(false, null); }
                SwitchOutline(false);
                TracksSpeed = 0f;
            }
            if (changed)
            {
                var sounds = GetComponent<BlitzLegacy.Runtime.Audio.TankSoundManager>();
                if (sounds != null) { sounds.SetFakeWreck(on); }
            }
        }
        public void RestoreLife(Transform spawn)
        {
            using var slowScope = BlitzPerfMonitor.Slow("tank.restoreLife");
            Health.MaxHealth = Health.MaxHealth;
            Modules?.RepairEverything();
            _forceCrashedTracksVisual = false;
            if (Wreck != null) Wreck.Stop();
            AmmoBayExploded = false;
            if (spawn != null) { _rb.position = spawn.position; _rb.rotation = spawn.rotation; }
            // Network puppets are kinematic: the host's pose arrives separately.
            if (!_rb.isKinematic) { _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero; }
            _rb.WakeUp();
            GetComponent<TankContactPhysics>()?.ResetState();
            if (Water != null) { Water.ResetState(); }
            Blocked = false; TurretBlock = false;
            ShowHideTracks(); SetSpotted(false);
            if (Gun is BattleGunBurst burst) burst.ResetBursts();
            Gun?.Reload(0);
        }
        public void TightenDispersion() { if (GunInfo != null) Dispersion = Mathf.Max(GunInfo.Dispersion, Dispersion * .5f); }
        public void LaunchExtraShell(int shellId)
        {
            if (GunInfo == null || shellId < 0 || shellId >= GunInfo.Shells.Count || !CanLaunchShell(shellId)) return;
            var old = ShellId;
            ShellId = shellId;
            try { LaunchSingleShell(false, false); }
            finally { ShellId = old; EnsureShellAvailable(); Gun?.RefreshAmmunition(); }
        }
        private void GetDestroyed()
        {
            using var slowScope = BlitzPerfMonitor.Slow("tank.destroyed");
            // outline_switch(false) and preview_armor(false) turn off the two
            // shader overlays, so a wreck carries neither the armour paint nor
            // the team trace no matter where the reticle was when it died.
            SwitchOutline(false);
            PreviewArmor(false, null);
            _nextRemoveArmorPreview = -1f;

            // block(true). The source clears the driving axes only on the way
            // out of a block, so a wreck keeps whatever it was holding.
            Blocked = true;
            Suspension?.StopDrive();
            _fwAxis = _rAxis = _fwAxisLow = _rAxisLow = TracksSpeed = 0f;

            // set_spotted(true): a wreck is visible to everybody.
            SetSpotted(true);

            // Util.timer(0, show_hide_tracks) swaps the running tracks for the
            // broken pair one frame later; the port swaps them here, because
            // nothing between the two reads the meshes. The track module's own
            // health is untouched: show_hide_tracks reads Dead as well.
            ShowHideTracks(true);

            // for c in armor_nodes: c.get_child(0).collision_layer += 2 ** 7.
            // Godot adds the dead-tank bit next to the armour bit; a Unity
            // object holds exactly one layer, so moving the plate onto
            // BattleLayers.Modules would drop it out of the armour mask and
            // make a wreck untraceable. Nothing in this slice reads the extra
            // bit, so the plates keep the layer that shells trace against.

            // tank_particles_manager._start_wreck_effect.
            if (Wreck != null) { Wreck.Play(); }
        }

        /// <summary>
        /// <c>_launch_single_shell</c>: roll the shot inside the dispersion
        /// cone, put the round at the muzzle and kick the hull back.
        /// </summary>
        private void ShootShell(bool firstShot) { LaunchSingleShell(firstShot, true); }

        private void LaunchSingleShell(bool firstShot, bool primary)
        {
            // A network puppet never launches a real round: the host does, and
            // its exact shell arrives through NetVisualShot.
            if (NetPuppet) { return; }
            if (Turret == null || Turret.GunPivot == null) { return; }
            if (!CanLaunchShell(ShellId)) { return; }

            var shell = CurrentShell();
            if (shell == null) { return; }

            // The cached mobile AI trace only serves the thinking ticks: the
            // launch tick refreshes it, so dispersion, hit ownership and
            // obstruction of a real shot are never stale.
            if (Application.isMobilePlatform && View == null && !NetPuppet)
            {
                UpdateGunTrace(true);
                _mobileAiGunTraceNext = Time.time + AimMath.MobileAiGunTraceInterval;
            }

            var pivot = Turret.GunPivot;

            // gun_trace parks the cone's edge one dispersion offset beside the
            // aim point, and the roll is narrowed by how far the gun has
            // drifted from its own dispersion.
            if (GunTracePoint == Vector3.zero)
            {
                UpdateGunTrace();
            }

            var coneTarget = DispersionPoint - pivot.position;
            var angle = coneTarget.sqrMagnitude <= SourceEpsilon
                ? 0f
                : Vector3.Angle(pivot.forward, coneTarget.normalized) * Mathf.Deg2Rad;
            var uprising = GetComponent<UprisingTank>();
            var spread = angle * GunMath.ConeMultiplier(GunInfo.Dispersion, Dispersion) * (uprising != null && uprising.State.Has("duplet") ? uprising.State.Number("duplet", "dispersion_mul", 3f) : 1f);
            var direction = RandomVectorInCone(pivot.forward, spread);

            // lastpos starts on the gun axis at the muzzle's own offset, so the
            // round's first trace covers the barrel it just left.
            var local = Turret.Muzzle != null ? Turret.Muzzle.localPosition : Vector3.zero;
            var origin = pivot.position + pivot.right * local.x + pivot.up * local.y;
            var muzzle = Turret.Muzzle != null ? Turret.Muzzle.position : pivot.position;

            var projectile = BattleProjectile.Create(shell, this, origin, muzzle, direction);
            BattleNetHooks.RaiseShellLaunched(this, ShellId, origin, muzzle, direction,
                projectile != null ? projectile.ShotId : 0);
            if (_shellAmounts.Count > ShellId && _shellAmounts[ShellId] > 0 && !SandboxBattle.InfiniteAmmo(this))
            {
                _shellAmounts[ShellId]--;
            }

            // apply_impulse(gun.global_basis.z * recoil, gun.position + up * 2).
            // The source hands apply_impulse a node-local offset, which Godot
            // reads as an unrotated offset from the body origin.
            var recoil = GunMath.Recoil(GunInfo.ImpulseMultiplier, GunInfo.Caliber);
            var gravity = GetComponent<GravityModeTank>();
            if (gravity != null) recoil *= GravityModeTank.RecoilMultiplier * gravity.ShotPowerMultiplier;
            if (!SandboxBattle.NoRecoil)
            {
                _rb.AddForceAtPosition(
                    -pivot.forward * recoil,
                    transform.position + pivot.localPosition + transform.up * RecoilHeight,
                    ForceMode.Impulse);
            }

            if (firstShot)
            {
                DispersionBloom(GunInfo.DispersionShot);
            }

            // projectile_fired.emit(first_shot, shell_id) fires after the rack
            // has been debited, so listeners read the counter that remains.
            var firedSlot = ShellId; // a listener may switch the selected slot during notification
            ProjectileFired?.Invoke(firstShot, firedSlot);
            if (primary) PrimaryProjectileFired?.Invoke(firedSlot);
            EnsureShellAvailable();
        }

        /// <summary>
        /// <c>random_vector_in_cone</c>. Godot's cross products wind the other
        /// way, but the basis only spins a random offset around the shot axis,
        /// so the roll keeps its distribution.
        /// </summary>
        public static Vector3 RandomVectorInCone(Vector3 direction, float maxAngle)
        {
            var tangent = Mathf.Abs(direction.y) > ConeAxisLimit ? Vector3.right : Vector3.up;
            var binormal = Vector3.Cross(direction, tangent).normalized;
            tangent = Vector3.Cross(binormal, direction).normalized;

            // 1 - randf_range(-1, 1) lands in [0, 2] and the clamp cuts it back
            // to one, so most shots sit near the middle of the cone.
            var r = Mathf.Clamp(1f - UnityEngine.Random.Range(-1f, 1f), -1f, 1f);
            var r2 = UnityEngine.Random.Range(-1f, 1f);

            var angle = r * r2 * maxAngle * 0.5f;
            var spin = r2 * 2f * Mathf.PI;

            var cone = direction * Mathf.Cos(angle)
                + tangent * Mathf.Cos(spin) * Mathf.Sin(angle)
                + binormal * Mathf.Sin(spin) * Mathf.Sin(angle);
            return cone.normalized;
        }

        /// <summary>
        /// <c>add_armor_from_mesh</c> over every plate of the armour model: the
        /// source hides the plate, hangs a trimesh body off it on the armour or
        /// the screen layer with an empty collision mask, writes the nominal
        /// thickness into that body's metadata and keeps it out of the tank's
        /// own traces.
        ///
        /// The OBJ importer merges the plates into one mesh, so every plate is
        /// rebuilt out of its recorded submesh. Those vertices are already in
        /// the local space of the recorded BlitzPACKSold mount.
        /// </summary>
        /// <summary><c>has_equipment</c>.</summary>
        public bool HasEquipment(string id)
        {
            if (_equipment == null || string.IsNullOrEmpty(id)) { return false; }

            for (var i = 0; i < _equipment.Count; i++)
            {
                if (string.Equals(_equipment[i], id, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary><c>get_repair_time()</c>: the toolbox shortens it by a quarter.</summary>
        public float GetRepairTime()
        {
            return ModuleTuning.RepairTime
                * (HasEquipment(ModuleTuning.ToolboxId)
                    ? ModuleTuning.ToolboxRepairFactor : 1f);
        }

        /// <summary><c>get_module_status</c>.</summary>
        public TankModule.Status ModuleStatus(string id)
        {
            return Modules == null ? TankModule.Status.Normal : Modules.StatusOf(id);
        }

        /// <summary>
        /// <c>get_reload_total</c> pays the client's ammo rack penalty: a
        /// damaged bay slows the loader down, `ammo_bay_damaged`.
        /// </summary>
        public float ReloadPenalty
        {
            get
            {
                return ModuleStatus(TankModuleIds.AmmoBay) == TankModule.Status.Damaged
                    ? ModuleTuning.AmmoBayDamagedReload
                    : 1f;
            }
        }

        /// <summary>
        /// <c>init_modules()</c>. The source only ever registers the tracks;
        /// this port adds the ammo rack, whose health the 4.8.0 card authors as
        /// <c>hull/ammoBayHealth</c>. The remaining five modules and the crew
        /// are drawn by the damage panel but carry no logic yet.
        /// </summary>
        private void InitModules()
        {
            Modules = new TankModuleSet();
            if (_tracksPlate != null)
            {
                var bounds = _tracksPlate.Shape != null
                    && _tracksPlate.Shape.sharedMesh != null
                        ? _tracksPlate.Shape.sharedMesh.bounds
                        : new Bounds();
                TracksLength = bounds.size.z > 0f ? bounds.size.z : 1f;
            }
            else
            {
                TracksLength = 1f;
                Debug.LogWarning(
                    "[Blitz] no tracks module in the model: "
                    + (Card != null ? Card.Name : name));
            }

            var trackHealth = Mathf.RoundToInt(TrackInfo != null ? TrackInfo.Health : 0f);
            var tracks = Modules.Register(new TankTracksModule(trackHealth, GetRepairTime, () => Dead));
            if (TrackInfo != null)
            {
                var autoRepair = Mathf.Max(1, Mathf.FloorToInt(trackHealth * 0.5f) - 1);
                tracks.RegenHealth = Mathf.Min(TrackInfo.MaxRegenHealth, autoRepair);
            }

            tracks.OnDamaged += id => SpeakModule(CrewMessage.ModuleTrackCrit);
            tracks.OnCrit += id =>
            {
                ShowHideTracks();

                // Clear normal-drive commands immediately; emergency speed caps
                // must apply before the next player or AI input arrives.
                if (Suspension != null)
                {
                    ApplyTrackDriveCaps();
                    Suspension.StopDrive();
                }
                _fwAxis = 0f;
                _rAxis = 0f;
                _fwAxisLow = 0f;
                _rAxisLow = 0f;

                SpeakModule(CrewMessage.ModuleTrackDestroyed);
                PlayModuleSound(ModuleTuning.TracksDestroySound);
            };
            tracks.OnRepaired += id =>
            {
                ApplyTrackDriveCaps();
                ShowHideTracks();
                SpeakModule(CrewMessage.ModuleTrackFixed);
                PlayModuleSound(ModuleTuning.TracksFixSound);
            };

            var bay = Card != null ? Card.AmmoBay : null;
            if (bay != null)
            {
                var ammoBay = Modules.Register(new TankModule(
                    TankModuleIds.AmmoBay,
                    Mathf.RoundToInt(bay.Health),
                    GetRepairTime,
                    () => Dead));
                ammoBay.RegenHealth = bay.MaxRegenHealth;
                ammoBay.OnDamaged += id => SpeakModule(CrewMessage.ModuleAmmoBayCrit);
                ammoBay.OnCrit += id => ExplodeAmmoBay();
            }
        }

        /// <summary>
        /// <c>show_hide_tracks()</c>: draw the client's broken-track mesh
        /// instead of the running pair once the hull is dead or the track
        /// module is destroyed, and put the running pair back on a repair.
        /// </summary>
        private void ShowHideTracks()
        {
            ShowHideTracks(false);
        }

        /// <summary>
        /// <c>show_hide_tracks()</c>, with the death the caller already knows
        /// about. The source defers the swap with <c>Util.timer(0, ...)</c>
        /// because <c>dead()</c> only reads true after the health component
        /// finishes its own death handling; passing the fact in keeps the swap
        /// on the frame the hull dies instead of waiting a frame for it.
        /// </summary>
        private void ShowHideTracks(bool dying)
        {
            if (_crashedTracks == null ||
                _trackObjectLeft == null ||
                _trackObjectRight == null)
            {
                return;
            }

            // `if is_player() and sniper_mode: return`. The scope sits inside
            // the hull, so swapping meshes under it only pops geometry.
            if (View != null && View.SniperMode && !dying && !Dead) { return; }

            // The cosmetic swap never touches the module itself; drive() stays
            // disabled by the module status, not by this mesh.
            var both = dying || Dead || _forceCrashedTracksVisual;
            var left = both || TrackCrashed(TrackSide.Left);
            var right = both || TrackCrashed(TrackSide.Right);
            _crashedTracks.SetActive(left || right);
            var whole = left && right;
            // Keep the source's single draw when both belts are broken/dead; partition only a one-sided break.
            if (_crashedTrackWhole != null) _crashedTrackWhole.forceRenderingOff = !whole;
            if (_crashedTrackLeft != null) _crashedTrackLeft.SetActive(left && !whole);
            if (_crashedTrackRight != null) _crashedTrackRight.SetActive(right && !whole);
            _trackObjectLeft.SetActive(!left);
            _trackObjectRight.SetActive(!right);
        }

        /// <summary>The crew only comments on the local player's own hull.</summary>
        private void SpeakModule(CrewMessage message)
        {
            if (View == null) { return; }

            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap == null || bootstrap.Audio == null) { return; }

            bootstrap.Audio.PlayCrewMessage(message);
        }

        /// <summary><c>Audio.play_sound3d(..., pos(), self)</c>.</summary>
        private void PlayModuleSound(string sound)
        {
            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap == null || bootstrap.Audio == null) { return; }

            bootstrap.Audio.PlayPool3D(bootstrap.Audio.Sounds.Sound(sound), Pos());
        }

        /// <summary>
        /// `vehicle_exploded`: a destroyed ammo rack takes the hull with it.
        /// Rack damage requires an internal volume hit, not just hull penetration.
        /// </summary>
        public InternalAmmoZones AmmoZones { get; } = new InternalAmmoZones();
        private DamageInfo _ammoAttribution;
        /// <summary>Confirmed volume hit plus original XML saving throw; the
        /// volumes are explicitly reconstructed, independent of original HP.</summary>
        public bool ApplyPenetratingModuleHit(ShellInfo shell, Vector3 point,
            Vector3 direction, DamageInfo attribution, float? chanceRoll = null)
        {
            if (Dead || shell == null || shell.DeviceDamage <= 0f || Modules == null ||
                !AmmoZones.Intersects(point, direction, shell.Caliber * .001f * AmmoZones.TraceDepthCalibres)) return false;
            if (!DamageLands(CurrentSettings(), attribution != null ? attribution.AttackerTank as BattleTank : null, this)) return false;
            if ((chanceRoll ?? UnityEngine.Random.value) >= AmmoZones.ProjectileChance) return false;
            _ammoAttribution = attribution;
            DamageModule(TankModuleIds.AmmoBay, Mathf.RoundToInt(shell.DeviceDamage));
            _ammoAttribution = null;
            return true;
        }

        public bool AmmoBayExploded { get; private set; }
        private void ExplodeAmmoBay()
        {
            if (Health == null || Health.Dead) { return; }

            AmmoBayExploded = true;
            SpeakModule(CrewMessage.ModuleAmmoBayCrit);
            var dinfo = new DamageInfo
            {
                Dmg = Health.Health,
                DmgBase = Health.Health,
                DmgType = DamageType.Explosion,
                Attacker = _ammoAttribution != null ? _ammoAttribution.Attacker : null,
                AttackerTank = _ammoAttribution != null ? _ammoAttribution.AttackerTank : null,
                ShotId = _ammoAttribution != null ? _ammoAttribution.ShotId : 0,
                Shell = _ammoAttribution != null ? _ammoAttribution.Shell : ShellType.Ap,
                Pos = _ammoAttribution != null ? _ammoAttribution.Pos : default,
            };
            Health.ReceiveDamage(dinfo);
        }

        /// <summary>
        /// Use original chassis bulkHealthFactor for the central track run.
        /// Its 75% region is inherited from the port, not an authored hitbox.
        /// </summary>
        public void ModuleCollide(BattleArmorPlate plate, int moduleDamage, Vector3 pos)
        {
            if (Blocked || plate == null || Modules == null) { return; }
            if (string.IsNullOrEmpty(plate.Module)) { return; }

            if (plate.Module == TankModuleIds.Tracks)
            {
                var zoffset = plate.transform.InverseTransformPoint(pos).z;
                var coeff = Mathf.Abs(zoffset) / (TracksLength * 0.5f);
                if (coeff < ModuleTuning.TracksCentreShare)
                {
                    moduleDamage = Mathf.RoundToInt(moduleDamage / Mathf.Max(1f, TrackInfo != null ? TrackInfo.BulkHealthFactor : 1f));
                }
            }

            var id = plate.Module == TankModuleIds.Tracks
                ? (TrackSideAt(pos) == TrackSide.Left ? TankModuleIds.TrackLeft : TankModuleIds.TrackRight)
                : plate.Module;
            DamageModule(id, moduleDamage);
        }

        /// <summary><c>damage_module</c>.</summary>
        public void DamageModule(string id, int dmg)
        {
            if (Modules == null) { return; }

            Modules.Damage(id, dmg, Time.time);
        }

        /// <summary>
        /// <c>repair_everything</c>: true only when a module really was
        /// damaged, which is what lets the repair kit refuse - silently,
        /// because the source clears its cant_use_msg.
        /// </summary>
        public bool RepairEverything()
        {
            return Modules != null && Modules.RepairEverything();
        }

        private void BuildArmor()
        {
            IsolateArmorLayers();

            var assets = _assets ?? TankModelAssets.For(Card);
            var asset = TankAssetSource.ArmorAsset(assets);
            if (asset == null)
            {
                Debug.LogError("[Blitz] missing armour table: " + assets.ArmorDataResource);
                return;
            }

            var prefab = TankAssetSource.ArmorModel(assets);
            var filter = prefab != null ? prefab.GetComponentInChildren<MeshFilter>(true) : null;
            var source = Mirror(filter != null ? filter.sharedMesh : null);
            if (source == null)
            {
                Debug.LogError("[Blitz] missing armour model: " + assets.ArmorModelResource);
                return;
            }

            var table = TankArmorTable.Parse(asset.text);
            var previewShader = ArmorPreviewShader.Load();
            for (var i = 0; i < table.Count; i++)
            {
                var plate = table.Plates[i];
                // The export ships the shell of every researchable turret and
                // the mantlet of every gun; only the installed pair is armour.
                if (!MountInstalled(plate.Mount)) { continue; }
                if (plate.Module == TankModuleIds.Tracks && TrackInfo != null && TrackInfo.Armor > 0)
                { plate.Armor = TrackInfo.Armor; }
                if (!string.IsNullOrEmpty(plate.RequiredTurret)
                    && (Turret == null || plate.RequiredTurret != Turret.TurretNodeName)) { continue; }

                var shape = PlateMesh(source, plate);
                if (shape == null) { continue; }

                var mount = MountFor(plate.Mount);
                var host = new GameObject(plate.Name);
                host.layer = plate.Screen ? BattleLayers.Screen : BattleLayers.Armor;
                host.transform.SetParent(mount, false);
                // The exporter writes each collision plate in the local space
                // of its BlitzPACKSold hull/turret/gun mount, so no world-space
                // pivot subtraction belongs here. The only correction is the
                // measured mount offset: the pivot the exporter recovered from
                // bounding box centres drifts upwards on parts with a cupola or
                // an antenna, and the shell drifts with it.
                var offset = table.MirroredOffsetOf(plate.Mount);
                host.transform.localPosition =
                    new Vector3(offset.X, offset.Y, offset.Z);
                host.transform.localRotation = Quaternion.identity;
                host.transform.localScale = Vector3.one;

                // Godot gives every plate its own StaticBody3D. A kinematic body
                // is the Unity shape of that: it keeps a concave collider legal
                // under the hull's dynamic body and out of its compound shape.
                var body = host.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.useGravity = false;

                var collider = host.AddComponent<MeshCollider>();
                collider.sharedMesh = shape;
                collider.convex = false;

                var armor = host.AddComponent<BattleArmorPlate>();
                armor.Bind(this, plate, collider, mount);
                AttachPlatePreview(armor, host, shape, previewShader);
                _plates.Add(armor);
                if (armor.Preview != null) _armorPreviewRenderers.Add(armor.Preview);
                if (armor.Module == TankModuleIds.Tracks) { _tracksPlate = armor; }
            }
        }

        /// <summary>
        /// <c>add_armor_from_mesh</c> hangs the armour shader off the plate
        /// mesh itself and hides it, so the scope can flip the same mesh back
        /// on. The collider mesh carries no normals, and the overlay needs
        /// them to read the impact angle, so the renderer gets its own copy.
        /// </summary>
        private static void AttachPlatePreview(
            BattleArmorPlate plate, GameObject host, Mesh shape, Shader shader)
        {
            if (shader == null) { return; }

            var mesh = UnityEngine.Object.Instantiate(shape);
            mesh.name = shape.name + "_preview";
            mesh.RecalculateNormals();

            var filter = host.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            var renderer = host.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = ArmorPreviewShader.NewMaterial(shader);
            renderer.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage =
                UnityEngine.Rendering.ReflectionProbeUsage.Off;

            plate.AttachPreview(renderer, renderer.sharedMaterial);
        }

        /// <summary>
        /// True when a plate rides a part this loadout installs. Hull plates
        /// always count; a turret or gun plate only counts for the module the
        /// rig mounted.
        /// </summary>
        private bool MountInstalled(string mount)
        {
            if (string.IsNullOrEmpty(mount) || mount == TankArmorTable.HullMount)
            {
                return true;
            }

            if (Turret == null)
            {
                return true;
            }

            return mount == Turret.TurretNodeName || mount == Turret.GunNodeName;
        }

        /// <summary>The node a plate turns with: the hull, the turret or the gun.</summary>
        private Transform MountFor(string mount)
        {
            if (Turret != null)
            {
                if (mount == Turret.TurretNodeName && Turret.TurretPivot != null)
                {
                    return Turret.TurretPivot;
                }

                if (mount == Turret.GunNodeName && Turret.GunPivot != null)
                {
                    return Turret.GunPivot;
                }
            }

            return transform;
        }

        /// <summary>
        /// The collision mesh of one plate, taken out of the submesh the porter
        /// exported it into. Only the vertices that plate uses are carried over,
        /// so a collider never cooks the whole tank's worth of points.
        /// </summary>
        private static Mesh PlateMesh(Mesh source, TankArmorTable.Plate plate)
        {
            if (plate.Submesh < 0 || plate.Submesh >= source.subMeshCount)
            {
                Debug.LogError("[Blitz] armour plate outside the model: " + plate.Name);
                return null;
            }

            var triangles = source.GetTriangles(plate.Submesh);
            if (triangles.Length / 3 != plate.Triangles)
            {
                Debug.LogWarning(
                    "[Blitz] armour plate " + plate.Name + " holds "
                    + (triangles.Length / 3) + " triangles, the table says " + plate.Triangles);
            }

            var points = source.vertices;
            var moved = new Dictionary<int, int>(triangles.Length);
            var vertices = new List<Vector3>(triangles.Length);
            var indices = new int[triangles.Length];
            for (var i = 0; i < triangles.Length; i++)
            {
                int index;
                if (!moved.TryGetValue(triangles[i], out index))
                {
                    index = vertices.Count;
                    moved[triangles[i]] = index;
                    vertices.Add(points[triangles[i]]);
                }

                indices[i] = index;
            }

            var mesh = new Mesh { name = plate.Name };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(indices, 0);
            return mesh;
        }

        /// <summary>
        /// <c>collision_mask = 0</c> on every plate body: armour and screens
        /// stop nothing and only a shell trace notices them. Unity spells that
        /// as a cleared row of the collision matrix, because a concave mesh
        /// collider cannot be a trigger.
        /// </summary>
        private static void IsolateArmorLayers()
        {
            for (var layer = 0; layer < 32; layer++)
            {
                Physics.IgnoreLayerCollision(BattleLayers.Armor, layer, true);
                Physics.IgnoreLayerCollision(BattleLayers.Screen, layer, true);
            }
        }

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _rb.useGravity = true;
        }

        private void OnDestroy()
        {
            if (_camouflage != null)
            {
                _camouflage.Release();
                _camouflage = null;
            }
        }

        /// <summary>
        /// Puts a camouflage on the hull. The tank fights in it whenever it
        /// can wear it - an id it cannot wear, an unknown one off the network
        /// included, is dropped - and <paramref name="painted"/> says whether
        /// the Camouflage graphics setting draws it.
        /// </summary>
        public void WearCamouflage(string camouflage, bool painted)
        {
            var catalog = CamouflageAssets.Catalog;
            var tank = Card != null ? catalog.Tank(Card.Name) : null;
            CamouflageId = tank != null && catalog.Get(camouflage) != null && tank.Has(camouflage)
                ? camouflage
                : string.Empty;
            CamouflageBonus = CamouflageService.ConcealmentBonus(
                catalog, Card != null ? Card.Name : string.Empty, CamouflageId);
            RepaintCamouflage(painted);
        }

        /// <summary>Draws the worn camouflage, or the plain sheets, after a settings change.</summary>
        public void RepaintCamouflage(bool painted)
        {
            if (_camouflage == null) { return; }
            _camouflage.Show(painted ? CamouflageId : string.Empty);
        }

        private void FixedUpdate()
        {
            using var slowScope = BlitzPerfMonitor.Slow("tank.fixedUpdate");
            // Util._physics_process fires the consumable deadlines, so they
            // keep running even while the hull itself is still assembling.
            using (BattlePerfMarkers.TankUpkeep.Auto())
            {
                TickConsumables();

                // process_modules(): the scheduled half repair of every module.
                if (Modules != null) { Modules.Tick(Time.time); }
                ShowHideTracks(_forceCrashedTracksVisual);

                // next_remove_outline_armor is checked in _physics_process too.
                TickArmorPreview();
            }

            if (Suspension == null) { return; }

            if (NetPuppet)
            {
                NetPuppetTick();
                return;
            }

            if (Suspension.ForwardAxis != 0f &&
                Mathf.Abs(Suspension.Speed) > LastMovementSpeed)
            {
                LastMovement = Time.time;
            }

            if (HasPlayArea)
            {
                var position = ClampPosition(_rb.position, PlayArea);
                if (position != _rb.position) { _rb.position = position; }
                // A kinematic hull (wreck, respawn) has no velocity to limit;
                // writing one only made the engine log a warning every tick.
                if (!_rb.isKinematic)
                {
                    _rb.linearVelocity =
                        LimitBorderVelocity(position, _rb.linearVelocity, PlayArea);
                }
            }

            PreviousVelocity = _rb.linearVelocity;
            PreviousAngularVelocity = _rb.angularVelocity;
            PreviousRotation = new Vector3(
                Suspension.PitchDegrees, Suspension.YawDegrees, Suspension.RollDegrees);

            if (!Blocked)
            {
                var highPriority = _fwAxis != 0f || _rAxis != 0f;
                Suspension.Enqueue(
                    highPriority ? _fwAxis : _fwAxisLow,
                    highPriority ? _rAxis : _rAxisLow);
            }

            // if is_player(): cam_trace() sits right in front of adjust_aim.
            if (View != null)
            {
                using (BattlePerfMarkers.CamTrace.Auto())
                {
                    View.CamTrace();
                }
            }

            // adjust_aim runs before _physics_process bails out on blocked.
            if (Turret != null)
            {
                using (BattlePerfMarkers.TankTurret.Auto())
                {
                    Turret.AdjustAim(Time.fixedDeltaTime);
                }

                if (!Blocked)
                {
                    // gun_trace precedes rotate_turret / rotate_gun in the source.
                    var cheapTrace = View == null;
                    if (AimMath.GunTraceDue(Application.isMobilePlatform, cheapTrace, Time.time,
                        GetEntityId().GetHashCode(), ref _mobileAiGunTraceNext))
                    {
                        UpdateGunTrace(cheapTrace);
                    }

                    using (BattlePerfMarkers.TankTurret.Auto())
                    {
                        Turret.Rotate(Time.fixedDeltaTime);
                    }

                    TurretAngularVelocity = Turret.AngularVelocity;
                }
            }
            else if (TurretBlock)
            {
                TurretAngularVelocity = 0f;
                Drive(0f, 0f, true);
            }

            // Run after turret rotation so this tick's real angular speed is used.
            DispersionManager(Time.fixedDeltaTime);

            if (Suspension.WheelHits > 1)
            {
                TracksSpeed = Suspension.SpeedMs * -Time.fixedDeltaTime * TrackSpeedFactor;
            }

            using (BattlePerfMarkers.TankTracks.Auto())
            {
                RotateTracks();
            }
        }

        /// <summary>
        /// True on a network client: the host simulates this hull and the
        /// network layer places it every frame. Physics, the suspension and the
        /// turret traverse are off; tracks and the camera trace keep running.
        /// </summary>
        public bool NetPuppet { get; private set; }

        /// <summary>Turns this tank into a kinematic puppet of the host state.</summary>
        public void SetNetPuppet()
        {
            if (NetPuppet) { return; }
            NetPuppet = true;
            if (_rb != null)
            {
                _netKinematic = _rb.isKinematic;
                _netInterpolation = _rb.interpolation;
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                _rb.isKinematic = true;
                _rb.interpolation = RigidbodyInterpolation.None;
            }
            _netDisabled.Clear();
            DisableIfPresent<TankSuspension>();
            DisableIfPresent<TankContactPhysics>();
            DisableIfPresent<BattleHullUnstick>();
            DisableIfPresent<GravityModeTank>();
            if (Turret != null) { Turret.NetDriven = true; }
        }

        /// <summary>
        /// Host migration: the puppet becomes a simulated tank again, starting
        /// from the last pose and health the old host sent.
        /// </summary>
        public void ClearNetPuppet()
        {
            if (!NetPuppet) { return; }
            NetPuppet = false;
            for (var i = 0; i < _netDisabled.Count; i++)
            {
                if (_netDisabled[i] != null) { _netDisabled[i].enabled = true; }
            }
            _netDisabled.Clear();
            // Keep rolling at the speed the old host last reported, so the hand-over
            // does not stop the tank dead.
            var carried = Suspension != null ? transform.forward * Suspension.SpeedMs : Vector3.zero;
            if (Suspension != null) { Suspension.ClearNetMotion(); }
            if (_rb != null)
            {
                _rb.isKinematic = _netKinematic;
                _rb.interpolation = _netInterpolation;
                _rb.angularVelocity = Vector3.zero;
                if (!_rb.isKinematic) { _rb.linearVelocity = carried; }
            }
            if (Turret != null) { Turret.NetDriven = false; Turret.ResetNet(); }
        }

        private readonly List<Behaviour> _netDisabled = new List<Behaviour>();
        private bool _netKinematic;
        private RigidbodyInterpolation _netInterpolation;

        private void DisableIfPresent<T>() where T : Behaviour
        {
            var component = GetComponent<T>();
            if (component != null && component.enabled)
            {
                component.enabled = false;
                _netDisabled.Add(component);
            }
        }

        /// <summary>
        /// Applies one interpolated host sample. Health only ever moves the way
        /// the host moved it, so the HUD and the wreck react exactly as they
        /// would to a local hit.
        /// </summary>
        public void ApplyNetState(Vector3 position, Quaternion rotation, float yaw, float pitch, int health)
        {
            if (!NetPuppet) { return; }
            transform.SetPositionAndRotation(position, rotation);
            if (Turret != null)
            {
                Turret.NetPredicted = View != null;
                Turret.ApplyNetworked(yaw, pitch);
            }
            if (Health != null && !Health.Dead && health < Health.Health)
            {
                Health.ReceiveDamageBasic(Health.Health - Mathf.Max(health, 0));
            }
            else if (Health != null && !Health.Dead && health > Health.Health)
            {
                // A heal on the host (Uprising's Return in Time puts the old
                // health back) lands as a restore, never as a damage event.
                Health.Restore(health);
            }
        }

        /// <summary>
        /// Replays the host's real round on a puppet: same origin, muzzle and
        /// direction, so the tracer lands where the host's hit landed. Damage is
        /// never applied here (see <see cref="BattleNetHooks.PuppetWorld"/>).
        /// <paramref name="railgun"/>: the round spent the host's Big Boss railgun charge.
        /// </summary>
        public void NetVisualShot(int shellId, Vector3 origin, Vector3 muzzle, Vector3 direction, int hostShotId = 0, bool railgun = false)
        {
            if (Card == null || GunInfo == null) { return; }
            var previous = ShellId;
            if (shellId >= 0 && shellId < _shellAmounts.Count) { ShellId = shellId; }
            var shell = CurrentShell();
            ShellId = previous;
            if (shell == null) { return; }
            if (BigBoss != null) { BigBoss.PrimeNetShot(railgun); }
            var projectile = BattleProjectile.Create(shell, this, origin, muzzle, direction.normalized);
            if (BigBoss != null) { BigBoss.PrimeNetShot(false); }
            // A rocket keeps steering after the shot: its replay rides the
            // host's own flight instead of guessing the steering locally.
            if (projectile != null) { projectile.FollowHost(hostShotId); }
            ProjectileFired?.Invoke(true, shellId);
        }

        /// <summary>
        /// Applies the host's aim state of this puppet: somebody else's smoothed
        /// aim point and arty flag (replayed rockets steer and loft exactly as
        /// the host's), and for every puppet the host's live dispersion, so the
        /// own reticle grows and settles with the cone the host shoots with.
        /// </summary>
        public void ApplyNetAim(Vector3 aimLerp, bool lob, float dispersion)
        {
            if (!NetPuppet) { return; }
            if (Turret != null) { Turret.ApplyNetAim(aimLerp, lob); }
            if (!float.IsNaN(dispersion) && !float.IsInfinity(dispersion) && dispersion >= 0f) { Dispersion = dispersion; }
        }

        /// <summary>
        /// Applies the host's drive state so the puppet's tracks scroll, its
        /// wheels spin and its ground marks are laid exactly as on the host.
        /// </summary>
        public void ApplyNetMotion(float forwardAxis, float rightAxis, float speedMs, int wheelHits, bool onTank)
        {
            if (!NetPuppet || Suspension == null) { return; }
            Suspension.ApplyNetMotion(forwardAxis, rightAxis, speedMs, wheelHits, onTank);
        }

        /// <summary>Left track texture scroll, for tests that check the tracks move.</summary>
        public float TrackScroll { get { return _trackOffsetLeft.y; } }

        private void NetPuppetTick()
        {
            // The local player's crosshair still traces from its own camera.
            if (View != null) { View.CamTrace(); }
            if (Turret != null)
            {
                var own = View != null;
                Turret.NetPredicted = own;
                Turret.NetAimTick(Time.fixedDeltaTime, own);
                if (own)
                {
                    // The host's gun_trace never reaches a client: without it
                    // the reticle sat on the bare barrel line (no shell drop,
                    // no cone, no armour lighting). Same trace as the host.
                    TurretAngularVelocity = Turret.AngularVelocity;
                    if (!Blocked) { UpdateGunTrace(false); }
                }
            }

            // Same rule as the simulated tick: tracks only follow the ground speed
            // while the hull is actually supported.
            if (Suspension.WheelHits > 1)
            {
                TracksSpeed = Suspension.SpeedMs * -Time.fixedDeltaTime * TrackSpeedFactor;
            }
            RotateTracks();
        }

        /// <summary>
        /// Sandbox: scales the traverse the track and wheel animation turns
        /// with, so the tracks keep pace with a faster or slower hull.
        /// </summary>
        internal void ScaleTrackTurn(float factor)
        {
            _trackRotationSpeed *= factor;
        }

        /// <summary><c>rotate_tracks</c>.</summary>
        private void RotateTracks()
        {
            if (Dead || _forceCrashedTracksVisual || (TrackCrashed(TrackSide.Left) && TrackCrashed(TrackSide.Right))) { TracksSpeed = 0f; return; }
            if (_camera == null) { _camera = Camera.main; }
            if (_camera != null)
            {
                var distanceSqr =
                    (_camera.transform.position - transform.position).sqrMagnitude;
                if (distanceSqr > TrackCameraDistance * TrackCameraDistance) { return; }
            }

            if (Suspension.RightAxis == 0f && Suspension.ForwardAxis == 0f &&
                Suspension.Speed < TrackIdleSpeed)
            {
                return;
            }

            var rotationAdd = Hull.Length > 0f
                ? 0.5f * Suspension.RightAxis * (_trackRotationSpeed * Mathf.Deg2Rad) /
                  Hull.Length
                : 0f;
            var goingForward = Suspension.ForwardAxis != 0f;

            if (!TrackCrashed(TrackSide.Left))
            {
                SpinWheels(_wheelsLeft, goingForward ? Mathf.Max(rotationAdd * -1f, 0f) : rotationAdd * -1f);
                _trackOffsetLeft = ScrollTrack(_trackLeft, _trackOffsetLeft,
                    goingForward ? Mathf.Min(rotationAdd * -1f, 0f) : rotationAdd * -1f);
            }
            if (!TrackCrashed(TrackSide.Right))
            {
                SpinWheels(_wheelsRight, goingForward ? Mathf.Max(rotationAdd, 0f) : rotationAdd);
                if (_trackRight != _trackLeft) _trackOffsetRight = ScrollTrack(_trackRight, _trackOffsetRight,
                    goingForward ? Mathf.Min(rotationAdd, 0f) : rotationAdd);
            }
        }

        /// <summary><c>rotate_track_mat</c>.</summary>
        private Vector2 ScrollTrack(Material material, Vector2 offset, float rotationAdd)
        {
            if (material == null) { return offset; }

            if (Mathf.Abs(offset.y) > TrackOffsetLimit) { offset.y = 0f; }
            offset.y += TrackScrollStep(TracksSpeed, Hull.Length, rotationAdd);
            material.mainTextureOffset = offset;
            return offset;
        }

        /// <summary>
        /// <c>rotate_wheel</c>. The source turns each wheel about its local X;
        /// pitch flips under the Z mirror, so the angle is negated here. The
        /// imported OBJ children keep their geometry in model space, so the turn
        /// runs around the wheel's own centre instead of the node origin.
        /// </summary>
        private void SpinWheels(List<Wheel> wheels, float rotationAdd)
        {
            if (Model == null) { return; }

            var axis = Model.transform.right;
            for (var i = 0; i < wheels.Count; i++)
            {
                var wheel = wheels[i];
                if (wheel.Node == null) { continue; }

                var radians = WheelSpin(TracksSpeed, wheel.Radius, rotationAdd);
                if (radians == 0f) { continue; }

                wheel.Node.RotateAround(
                    Model.transform.TransformPoint(wheel.Pivot),
                    axis,
                    -radians * Mathf.Rad2Deg);
            }
        }

        /// <summary>
        /// Instantiates the visible model, rotates every mesh into battle space
        /// and hands each object its material. Returns the right track's bounds,
        /// which is where <c>tracks_height</c> comes from.
        /// </summary>
        private Bounds BuildModel()
        {
            var tracks = new Bounds();
            var assets = _assets ?? TankModelAssets.For(Card);
            var prefab = TankAssetSource.Model(assets);
            if (prefab == null)
            {
                Debug.LogError("[Blitz] missing tank model: " + assets.ModelResource);
                return tracks;
            }

            Model = Instantiate(prefab, transform, false);
            Model.name = assets.Id;
            Model.transform.localPosition = Vector3.zero;
            Model.transform.localRotation = Quaternion.identity;

            var bodyTexture = TankAssetSource.BodyTexture(assets);
            var trackTexture = TankAssetSource.TrackTexture(assets);
            var body = CreateTankMaterial(assets.Id + "Body", bodyTexture, false);
            _trackLeft = CreateTankMaterial(assets.Id + "TrackL", trackTexture, true);
            _trackRight = CreateTankMaterial(assets.Id + "TrackR", trackTexture, true);
            var nodeText = TankAssetSource.NodesAsset(assets);
            var nodes = TankNodeTable.Parse(nodeText != null ? nodeText.text : null);
            var albedoMaterials = new Dictionary<string, Material>();

            var renderers = Model.GetComponentsInChildren<MeshRenderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter != null ? Mirror(filter.sharedMesh) : null;
                if (filter != null && mesh != null) { filter.sharedMesh = mesh; }

                if (renderers.Length == 1)
                {
                    // Fallback for an importer that merges the OBJ object records:
                    // both tracks then share one material and cannot scroll apart.
                    Debug.LogWarning(
                        "[Blitz] tank import merged its object records; " +
                        "tracks and wheels share the fallback material");
                    renderer.sharedMaterials = new[] { body, _trackLeft };
                    _trackRight = _trackLeft;
                    tracks = SubMeshBounds(mesh, TracksSubMesh);
                }
                else
                {
                    var objectName = TrimCloneSuffix(renderer.gameObject.name);
                    if (objectName == "trackL")
                    {
                        renderer.sharedMaterial = _trackLeft;
                        _trackObjectLeft = renderer.gameObject;
                    }
                    else if (objectName == "trackR")
                    {
                        renderer.sharedMaterial = _trackRight;
                        _trackObjectRight = renderer.gameObject;
                        if (mesh != null) { tracks = mesh.bounds; }
                    }
                    else if (objectName == "tracks_crash")
                    {
                        // tracks_crashed.material_override = trackR_mat, and
                        // tracks_crashed.hide() until the hull needs it.
                        renderer.sharedMaterial = _trackRight;
                        _crashedTracks = renderer.gameObject;
                        _crashedTracks.SetActive(false);
                    }
                    else
                    {
                        var material = body;
                        var albedoId = nodes.AlbedoOf(objectName);
                        if (!string.IsNullOrEmpty(albedoId) && albedoId != assets.Id)
                        {
                            var materialKey = nodes.HasMaterialSettings(objectName)
                                ? albedoId + "/" + objectName : albedoId;
                            if (!albedoMaterials.TryGetValue(materialKey, out material))
                            {
                                var texture = LoadTexture(
                                    assets.AlbedoTextureResource(albedoId));
                                material = texture != null
                                    ? CreateTankNodeMaterial(
                                        assets.Id + "_" + albedoId, texture, nodes, objectName)
                                    : body;
                                albedoMaterials[materialKey] = material;
                            }
                        }
                        renderer.sharedMaterial = material;
                        if (objectName.StartsWith("wheelL"))
                        {
                            Register(_wheelsLeft, renderer.transform, mesh);
                        }
                        else if (objectName.StartsWith("wheelR"))
                        {
                            Register(_wheelsRight, renderer.transform, mesh);
                        }
                    }
                }

                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
                AttachOutline(renderer);
            }

            _camouflage = TankCamouflageView.Build(
                Model, assets, nodes, assets.TankName, _trackLeft, _trackRight);
            BuildSideTrackVisuals();
            OutlineSetup(false);
            SwitchOutline(false);
            return tracks;
        }

        private void BuildSideTrackVisuals()
        {
            if (_trackObjectLeft == null || _trackObjectRight == null || _crashedTracks == null) return;
            var left = _trackObjectLeft.GetComponent<MeshFilter>(); var right = _trackObjectRight.GetComponent<MeshFilter>();
            if (left != null && left.sharedMesh != null) _leftTrackCentreX = transform.InverseTransformPoint(left.transform.TransformPoint(left.sharedMesh.bounds.center)).x;
            if (right != null && right.sharedMesh != null) _rightTrackCentreX = transform.InverseTransformPoint(right.transform.TransformPoint(right.sharedMesh.bounds.center)).x;
            var filter = _crashedTracks.GetComponent<MeshFilter>(); var renderer = _crashedTracks.GetComponent<MeshRenderer>();
            if (filter == null || renderer == null || filter.sharedMesh == null) return;
            var source = filter.sharedMesh;
            if (!SplitCrashMeshes.TryGetValue(source, out var halves) || halves[0] == null || halves[1] == null)
            {
                halves = new[] { Instantiate(source), Instantiate(source) };
                halves[0].name = source.name + "Left"; halves[1].name = source.name + "Right";
                var vertices = source.vertices;
                var toTank = transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                for (var submesh = 0; submesh < source.subMeshCount; submesh++)
                {
                    var indices = source.GetTriangles(submesh); var l = new List<int>(); var r = new List<int>();
                    for (var i = 0; i + 2 < indices.Length; i += 3)
                    {
                        var centre = toTank.MultiplyPoint3x4((vertices[indices[i]] + vertices[indices[i + 1]] + vertices[indices[i + 2]]) / 3f).x;
                        var side = Mathf.Abs(centre - _leftTrackCentreX) <= Mathf.Abs(centre - _rightTrackCentreX) ? l : r;
                        side.Add(indices[i]); side.Add(indices[i + 1]); side.Add(indices[i + 2]);
                    }
                    halves[0].SetTriangles(l, submesh); halves[1].SetTriangles(r, submesh);
                }
                // Vertex/UV/normal/tangent streams and every source triangle are retained, only partitioned.
                SplitCrashMeshes[source] = halves;
            }
            for (var side = 0; side < 2; side++)
            {
                var host = new GameObject(side == 0 ? "tracks_crash_left" : "tracks_crash_right");
                host.transform.SetParent(_crashedTracks.transform, false); host.layer = _crashedTracks.layer;
                host.AddComponent<MeshFilter>().sharedMesh = halves[side];
                var part = host.AddComponent<MeshRenderer>(); var materials = renderer.sharedMaterials;
                if (materials.Length > 0) materials[0] = side == 0 ? _trackLeft : _trackRight;
                part.sharedMaterials = materials; part.shadowCastingMode = renderer.shadowCastingMode; part.receiveShadows = renderer.receiveShadows;
                part.lightProbeUsage = renderer.lightProbeUsage; part.reflectionProbeUsage = renderer.reflectionProbeUsage; part.renderingLayerMask = renderer.renderingLayerMask;
                if (side == 0) _crashedTrackLeft = host; else _crashedTrackRight = host;
            }
            // Spotted visibility owns enabled, so it must never resurrect the unsplit duplicate renderer.
            _crashedTrackWhole = renderer; renderer.forceRenderingOff = true;
        }

        /// <summary>
        /// The source hangs the outline shader off each tank material as its
        /// <c>next_pass</c>, which Unity spells as one more material on the
        /// renderer drawn after the ones it already has.
        /// </summary>
        private void AttachOutline(MeshRenderer renderer)
        {
            var shader = TankOutlineShader.Load();
            var maskShader = TankOutlineShader.LoadMask();
            if (shader == null || maskShader == null) { return; }

            var materials = renderer.sharedMaterials;
            var grown = new Material[materials.Length + 2];
            for (var i = 0; i < materials.Length; i++) { grown[i] = materials[i]; }

            // The stamp draws in the opaque queue and the outline in the
            // transparent one, so the whole tank has stencilled itself before
            // the first wheel is traced. Without that split every part outlines
            // itself and the tank reads as an x-ray of its own parts.
            var mask = TankOutlineShader.NewMaskMaterial(maskShader);
            var outline = TankOutlineShader.NewMaterial(shader);
            grown[materials.Length] = mask;
            grown[materials.Length + 1] = outline;
            renderer.sharedMaterials = grown;
            _outlineMaterials.Add(mask);
            _outlineMaterials.Add(outline);
        }

        /// <summary>
        /// <c>Util.replace_collision_from_donor</c> builds the hull shape out of
        /// the <c>hull_phys</c> mesh rather than the placeholder shape saved in
        /// the scene. Returns its bounds, which is where the hull measurements
        /// come from.
        /// </summary>
        private Bounds BuildCollider()
        {
            var bounds = new Bounds();
            var assets = _assets ?? TankModelAssets.For(Card);
            var prefab = TankAssetSource.PhysicsModel(assets);
            if (prefab == null)
            {
                Debug.LogError(
                    "[Blitz] missing tank physics hull: " + assets.PhysicsResource);
                return bounds;
            }

            var filter = prefab.GetComponentInChildren<MeshFilter>(true);
            var mesh = Mirror(filter != null ? filter.sharedMesh : null);
            if (mesh == null)
            {
                Debug.LogError("[Blitz] tank physics hull carries no mesh: " + assets.Id);
                return bounds;
            }

            var host = new GameObject("hull_phys_col");
            // The shell of a tank sits on its own layer in the source, which is
            // not in the mask a shell traces with: rounds resolve on armour.
            host.layer = BattleLayers.Physics;
            host.transform.SetParent(transform, false);
            var collider = host.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            collider.convex = true;
            collider.sharedMaterial = CreateHullMaterial();

            // The solver separates the contacts it sees; the rest - a rock the
            // hull settled onto, a shell a corner ended up inside - is what
            // leaves a tank wedged for good.
            var unstick = GetComponent<BattleHullUnstick>();
            if (unstick == null)
            {
                unstick = gameObject.AddComponent<BattleHullUnstick>();
            }

            unstick.Setup(_rb, collider);
            return mesh.bounds;
        }

        private static void Register(List<Wheel> wheels, Transform node, Mesh mesh)
        {
            if (node == null || mesh == null) { return; }

            wheels.Add(new Wheel
            {
                Node = node,
                Pivot = mesh.bounds.center,
                // sort_wheel: radius = wheel.get_aabb().abs().size.y * 0.5
                Radius = mesh.bounds.size.y * 0.5f,
            });
        }

        /// <summary>
        /// Low-friction hull contacts prevent armour acting as a climbing ramp.
        /// Terrain grip is supplied separately by the loaded wheel suspension.
        /// </summary>
        private static PhysicsMaterial CreateHullMaterial()
        {
            return new PhysicsMaterial("TankHull")
            {
                dynamicFriction = .08f,
                staticFriction = .12f,
                bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounceCombine = PhysicsMaterialCombine.Average,
            };
        }

        /// <summary>
        /// Clones a shipped material instead of assembling a keyword set only
        /// at runtime. Unity's player variant stripper cannot infer those code
        /// paths, which previously left the Android battle MS-1 white/glassy.
        /// </summary>
        public static Material CreateTankMaterial(
            string name, Texture2D texture, bool alphaClip)
        {
            var resource = alphaClip ? TrackMaterialResource : BodyMaterialResource;
            var template = Resources.Load<Material>(resource);
            Material material;
            if (template != null)
            {
                material = new Material(template);
            }
            else
            {
                var shader = Shader.Find(TankShaderName);
                if (shader == null) { shader = Shader.Find("Standard"); }
                material = new Material(shader);
                Debug.LogWarning("[Blitz] missing pinned tank material: " + resource);
            }

            material.name = name;
            ConfigureTankMaterial(material, texture, alphaClip);
            return material;
        }

        /// <summary>Optional authored render state for an individual source detail.</summary>
        public static Material CreateTankNodeMaterial(
            string name, Texture2D texture, TankNodeTable nodes, string objectName)
        {
            var cutoff = nodes != null ? Mathf.Clamp01(nodes.AlphaCutoffOf(objectName)) : 0f;
            var material = CreateTankMaterial(name, texture, cutoff > 0f);
            if (nodes != null && nodes.HasMaterialSettings(objectName))
            {
                if (material.HasProperty("_Cutoff")) { material.SetFloat("_Cutoff", cutoff); }
                if (material.HasProperty("_Cull"))
                { material.SetFloat("_Cull", Mathf.Clamp(nodes.CullModeOf(objectName), 0, 2)); }
            }
            return material;
        }

        /// <summary>
        /// Exact runtime and generated-asset configuration for the battle MS-1.
        /// The source shader is opaque, reads roughness from albedo alpha, writes
        /// SPECULAR=0 and only clips the tracks. Both reflected and direct URP
        /// specular therefore stay off rather than turning the bright map sky
        /// into a white glass coating.
        /// </summary>
        public static void ConfigureTankMaterial(
            Material material, Texture2D texture, bool alphaClip)
        {
            if (material == null) { return; }

            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", TankMetallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", TankSmoothness);
            if (material.HasProperty("_SmoothnessTextureChannel"))
            {
                material.SetFloat("_SmoothnessTextureChannel", 1f);
            }
            material.EnableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");

            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 0f);
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
            if (material.HasProperty("_SrcBlend"))
            {
                material.SetFloat("_SrcBlend", (float)BlendMode.One);
            }
            if (material.HasProperty("_DstBlend"))
            {
                material.SetFloat("_DstBlend", (float)BlendMode.Zero);
            }
            if (material.HasProperty("_Cull"))
            {
                material.SetFloat("_Cull", (float)CullMode.Back);
            }
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 1f);
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");

            if (material.HasProperty("_SpecularHighlights"))
            {
                material.SetFloat("_SpecularHighlights", 0f);
            }
            if (material.HasProperty("_EnvironmentReflections"))
            {
                material.SetFloat("_EnvironmentReflections", 0f);
            }
            material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            material.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");

            if (material.HasProperty("_AlphaClip"))
            {
                material.SetFloat("_AlphaClip", alphaClip ? 1f : 0f);
            }
            if (material.HasProperty("_Cutoff"))
            {
                material.SetFloat("_Cutoff", TrackAlphaCutoff);
            }

            if (alphaClip)
            {
                material.EnableKeyword("_ALPHATEST_ON");
                material.SetOverrideTag("RenderType", "TransparentCutout");
                material.renderQueue = (int)RenderQueue.AlphaTest;
            }
            else
            {
                material.DisableKeyword("_ALPHATEST_ON");
                material.SetOverrideTag("RenderType", "Opaque");
                material.renderQueue = (int)RenderQueue.Geometry;
            }
        }

        private static float WeightOf<T>(List<T> modules, int index) where T : ModuleInfo
        {
            var module = Pick(modules, index);
            return module != null ? module.Weight : 0f;
        }

        private static T Pick<T>(List<T> modules, int index) where T : class
        {
            if (modules == null || modules.Count == 0) { return null; }
            return index >= 0 && index < modules.Count ? modules[index] : modules[0];
        }

        private static Texture2D LoadTexture(string path)
        {
            var texture = Resources.Load<Texture2D>(path);
            if (texture == null)
            {
                Debug.LogWarning("[Blitz] missing texture: " + path);
            }
            return texture;
        }

        private static string TrimCloneSuffix(string value)
        {
            if (string.IsNullOrEmpty(value)) { return string.Empty; }

            var index = value.IndexOf("(Clone)", System.StringComparison.Ordinal);
            return index >= 0 ? value.Substring(0, index).Trim() : value;
        }
    }
}
