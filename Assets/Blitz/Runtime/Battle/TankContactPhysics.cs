using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Tank-only anti-ramp contact band, sustained crushing and measured
    /// landing energy. Gameplay damage tuning is explicit, not original server code.</summary>
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public sealed class TankContactPhysics : MonoBehaviour
    {
        public const float CrushHpPerSecond = 5f;
        private BattleTank _tank;
        private Rigidbody _body;
        private Collider _hull;
        private TankRolloverPhysics _rollover;
        private readonly Dictionary<BattleTank, float> _stackContacts = new Dictionary<BattleTank, float>();
        private readonly List<BattleTank> _expired = new List<BattleTank>();
        private double _crushRemainder;
        private float _airSeconds, _peakHeight, _maximumDownSpeed, _terrainContactUntil;
        public BoxCollider ContactBand { get; private set; }
        public int LandingCount { get; private set; }
        public float LastLandingSpeed { get; private set; }
        public int LastFallDamage { get; private set; }
        public int StackContactCount => _stackContacts.Count;

        public void Setup(BattleTank tank, Rigidbody body, Collider hull, Bounds localBounds)
        {
            _tank = tank; _body = body; _hull = hull;
            _rollover = GetComponent<TankRolloverPhysics>();
            if (_rollover == null) _rollover = gameObject.AddComponent<TankRolloverPhysics>();
            _rollover.Setup(tank, body);
            // A low, in-hull mass centre, not an origin outside the imported hull.
            _body.centerOfMass = localBounds.center - Vector3.up * localBounds.size.y * .22f;
            _body.solverIterations = 12; _body.solverVelocityIterations = 6;
            _body.maxAngularVelocity = 5f; _body.maxDepenetrationVelocity = 3f;
            _hull.contactOffset = .01f;
            // The slope of frontal armour must not become a powered ramp.
            // This band contacts ONLY other tank physics shells, never terrain,
            // projectiles or armour. Original mesh still owns map collisions.
            var band = new GameObject("tank contact band"); band.layer = BattleLayers.Physics;
            band.transform.SetParent(transform, false);
            ContactBand = band.AddComponent<BoxCollider>();
            ContactBand.center = localBounds.center;
            ContactBand.size = new Vector3(localBounds.size.x + .02f, localBounds.size.y, localBounds.size.z + .02f);
            ContactBand.excludeLayers = ~(1 << BattleLayers.Physics);
            ContactBand.contactOffset = .01f;
            ContactBand.sharedMaterial = _hull.sharedMaterial;
            ResetState();
        }

        public void ResetState()
        {
            _stackContacts.Clear(); _crushRemainder = 0; _airSeconds = 0;
            _peakHeight = _body != null ? _body.position.y : transform.position.y; _maximumDownSpeed = 0; _terrainContactUntil = -1;
            LastFallDamage = 0; LastLandingSpeed = 0; LandingCount = 0;
            if (_rollover != null) _rollover.ResetState();
        }

        private void FixedUpdate()
        {
            if (_body == null || _tank == null) return;
            TickCrush(Time.fixedDeltaTime, Time.fixedTime);
            var supported = (_tank.Suspension != null && _tank.Suspension.AnyHit) || Time.fixedTime < _terrainContactUntil;
            if (supported)
            {
                // Wheel rays can touch the ground before the hull collider does.
                // Capture impact energy before suspension support erases the fall.
                if (_airSeconds > .15f && !_tank.Dead)
                    ApplyLanding(Mathf.Max(_maximumDownSpeed, -_body.linearVelocity.y),
                        Mathf.Max(0, _peakHeight - _body.position.y),
                        _tank.Suspension != null ? _tank.Suspension.GroundNormal.y : 1f);
                // Short gaps on uneven terrain do not create a new fall.
                _airSeconds = 0; _peakHeight = _body.position.y; _maximumDownSpeed = 0;
            }
            else
            {
                _airSeconds += Time.fixedDeltaTime;
                _peakHeight = Mathf.Max(_peakHeight, _body.position.y);
                _maximumDownSpeed = Mathf.Max(_maximumDownSpeed, -_body.linearVelocity.y);
            }
        }

        private void OnCollisionEnter(Collision collision) { ObserveCollision(collision); }
        private void OnCollisionStay(Collision collision) { ObserveCollision(collision); }
        private void ObserveCollision(Collision collision)
        {
            if (_tank == null || collision == null) return;
            var other = collision.rigidbody != null ? collision.rigidbody.GetComponent<BattleTank>() : null;
            var upwardNormal = 0f;
            for (var i = 0; i < collision.contactCount; i++)
            {
                var contact = collision.GetContact(i);
                upwardNormal = Mathf.Max(upwardNormal, contact.normal.y);
                // Armour bodies, other tanks and vertical walls are not a supporting map surface.
                if (other == null && collision.rigidbody == null && _rollover != null &&
                    (BattleLayers.WheelMask & (1 << collision.gameObject.layer)) != 0)
                    _rollover.ObserveSupport(contact.normal, Time.fixedTime);
                if (other != null && other != _tank) ObserveTankContact(other, contact.normal, Time.fixedTime);
            }
            if (upwardNormal < .45f) return; // walls/side rams are not landings
            if (_airSeconds > .15f && !_tank.Dead)
            {
                var descent = Mathf.Max(_maximumDownSpeed, -_tank.PreviousVelocity.y);
                ApplyLanding(descent, Mathf.Max(0, _peakHeight - _body.position.y), upwardNormal);
            }
            _airSeconds = 0; _maximumDownSpeed = 0; _peakHeight = _body.position.y;
            if (other == null) _terrainContactUntil = Time.fixedTime + Time.fixedDeltaTime * 2.5f;
        }

        /// <summary>Requires a real vertical support contact and separate heights;
        /// nearby bodies and side impacts cannot activate sustained damage.</summary>
        public void ObserveTankContact(BattleTank other, Vector3 normal, float now)
        {
            if (other == null || other == _tank || Mathf.Abs(normal.y) < .55f) return;
            var otherPhysics = other.GetComponent<TankContactPhysics>();
            if (otherPhysics == null || otherPhysics._hull == null || _hull == null) return;
            var difference = _hull.bounds.center.y - otherPhysics._hull.bounds.center.y;
            var minimum = Mathf.Min(_hull.bounds.size.y, otherPhysics._hull.bounds.size.y) * .35f;
            if (Mathf.Abs(difference) <= Mathf.Max(.15f, minimum)) return;
            _stackContacts[other] = now + Time.fixedDeltaTime * 2.5f;
        }

        public void TickCrush(float delta, float now)
        {
            if (delta <= 0 || float.IsNaN(delta) || float.IsInfinity(delta)) return;
            _expired.Clear();
            foreach (var contact in _stackContacts)
                if (contact.Key == null || contact.Value < now) _expired.Add(contact.Key);
            foreach (var key in _expired) _stackContacts.Remove(key);
            if (_stackContacts.Count == 0 || _tank == null || _tank.Dead) { _crushRemainder = 0; return; }
            // One rate per tank, not per collider/contact point. Retain fractions
            // so 50/60/100 Hz all cause exactly five integer HP each second.
            _crushRemainder += CrushHpPerSecond * (double)delta;
            var hp = (int)System.Math.Floor(_crushRemainder + 1e-6);
            if (hp <= 0) return;
            _crushRemainder -= hp;
            BattleTank attacker = null;
            foreach (var contact in _stackContacts) { attacker = contact.Key; break; }
            EnvironmentalDamage(hp, attacker);
        }

        public struct FallResult { public int HullDamage, TrackDamage; }
        public static FallResult LandingDamage(float speed, float drop, float mass, int chassisHp, int tankHp, float hullHeight, float upNormal)
        {
            if (!Finite(speed) || !Finite(drop) || !Finite(mass) || !Finite(hullHeight) || !Finite(upNormal) ||
                speed <= 0 || drop <= 0 || mass <= 0 || chassisHp <= 0 || tankHp <= 0 || upNormal < .45f) return default;
            // Arcade policy shared by every vehicle, not recovered server code.
            // Require both a measured descent and impact energy. A slope absorbs
            // part of the vertical impact instead of amplifying it like a cliff.
            var normal = Mathf.Clamp01(upNormal);
            var equivalentHeight = Mathf.Min(drop, speed * speed / (2 * TankSuspension.SourceGravity)) * normal * normal;
            // Installed mass/chassis still matter, but cannot turn a small hill
            // into a lethal fall. Hull size only adds a small suspension cushion.
            var strength = Mathf.Clamp(chassisHp * 150f / mass, .85f, 1.15f);
            var cushion = Mathf.Clamp(hullHeight * .1f, 0f, .25f);
            var trackExcess = Mathf.Max(0, equivalentHeight - (3f + cushion));
            var hullExcess = Mathf.Max(0, equivalentHeight - (6f + cushion));
            // Separate track-only and hull-damage zones. Clamp fractions before
            // converting to integer HP, including extreme but finite mod inputs.
            var trackFraction = Mathf.Clamp01(trackExcess / (7f * strength));
            var hullSeverity = Mathf.Min(hullExcess, 20f) / strength;
            var hullFraction = Mathf.Clamp01(.004f * hullSeverity * hullSeverity);
            return new FallResult {
                HullDamage = Mathf.Clamp(Mathf.RoundToInt(tankHp * hullFraction), 0, tankHp),
                TrackDamage = Mathf.Clamp(Mathf.RoundToInt(chassisHp * trackFraction), 0, chassisHp) };
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public void ApplyLanding(float speed, float drop, float upNormal = 1f)
        {
            if (_tank == null || _tank.Dead) return;
            if (_tank.GetComponent<GravityModeTank>() != null) return;
            var result = LandingDamage(speed, drop, _body.mass, Mathf.RoundToInt(_tank.TrackInfo.Health), _tank.Health.MaxHealth, _tank.Hull.Height, upNormal);
            if (result.HullDamage == 0 && result.TrackDamage == 0) return;
            LandingCount++; LastLandingSpeed = speed; LastFallDamage = result.HullDamage;
            _tank.DamageModule(TankModuleIds.Tracks, result.TrackDamage);
            EnvironmentalDamage(result.HullDamage, null);
        }
        private void EnvironmentalDamage(int hp, BattleTank attacker)
        {
            if (hp <= 0 || _tank == null || _tank.Dead) return;
            // Required environmental rate must not be scaled by ram perks,
            // weapon shields or BOTS_HARMLESS; wrecks and friendly stacks count.
            _tank.Health.ReceiveDamage(new DamageInfo { Dmg = hp, DmgBase = hp,
                DmgType = DamageType.Environment, Attacker = attacker,
                AttackerTank = attacker, SuppressFeedback = true });
        }
    }
}
