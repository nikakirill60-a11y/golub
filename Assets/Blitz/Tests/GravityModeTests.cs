using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class GravityModeTests
    {
        private GameObject _root;
        private TanksDatabase _db;

        [SetUp]
        public void Setup()
        {
            _root = new GameObject("gravity tests");
            _db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
        }

        [TearDown]
        public void Cleanup()
        {
            Object.DestroyImmediate(_root);
        }

        private BattleTank Tank(string id = "ms-1")
        {
            var go = new GameObject(id, typeof(Rigidbody));
            go.transform.SetParent(_root.transform, false);
            var tank = go.AddComponent<BattleTank>();
            tank.Initialize(_db.Get(id));
            Physics.SyncTransforms();
            return tank;
        }

        [Test]
        public void GravityLocalizationAndArtworkMatchRequestedNames()
        {
            var lang = LocalizationTable.Parse(BlitzResources.LoadText("lang"));
            Assert.AreEqual("GRAVITY MODE", lang.Get("battle_mode:gravity/name", "en"));
            Assert.AreEqual("ГРАВИТАЦИЯ", lang.Get("battle_mode:gravity/name", "ru"));
            Assert.AreEqual("Thruster", lang.Get("consumables:jet_jump/name", "en"));
            Assert.AreEqual("Ракетный двигатель", lang.Get("consumables:jet_jump/name", "ru"));
            Assert.IsNotNull(Resources.Load<Texture2D>("Uprising/Modes/gravity"));
            Assert.IsNotNull(Resources.Load<Texture2D>(GravityModeTank.JetJumpIconResource));
        }

        [Test]
        public void GravityAttachAppliesSourceMobilityAndJumpRules()
        {
            var light = Tank("ms-1");
            var baseEngine = light.Suspension.EnginePower;
            var baseForward = light.Suspension.MaxSpeedForward;
            var baseBack = light.Suspension.MaxSpeedBack;
            var gravity = GravityModeTank.Attach(light, null);
            Assert.AreEqual((float)(int)(baseEngine * GravityModeTank.EnginePowerMultiplier), light.Suspension.EnginePower);
            Assert.AreEqual((float)(int)(baseForward * GravityModeTank.SpeedMultiplier), light.Suspension.MaxSpeedForward);
            Assert.AreEqual((float)(int)(baseBack * GravityModeTank.SpeedMultiplier), light.Suspension.MaxSpeedBack);
            Assert.IsTrue(light.Suspension.AirControlEnabled);
            Assert.IsNotNull(gravity.JetJump);
            Assert.AreEqual(GravityModeTank.JetJumpCooldown, gravity.JetJump.Info.Cooldown);
            Assert.AreEqual(0, gravity.JetJump.Info.UseTime);
            Assert.AreEqual(ConsumableType.Reusable, gravity.JetJump.Info.Type);
            Assert.AreEqual(string.Empty, gravity.JetJump.Info.CantUseKey);

            var heavy = Tank("maus");
            var heavyGravity = GravityModeTank.Attach(heavy, null);
            Assert.IsNull(heavyGravity.JetJump);
        }

        [Test]
        public void GravityModeDisablesLandingDamage()
        {
            var tank = Tank("maus");
            var contact = tank.GetComponent<TankContactPhysics>();
            var hp = tank.Health.Health;
            var tracks = tank.Modules.Get(TankModuleIds.Tracks).HealthComponent;
            GravityModeTank.Attach(tank, null);

            contact.ApplyLanding(30f, 20f);
            Assert.AreEqual(hp, tank.Health.Health);
            Assert.AreEqual(tracks.MaxHealth, tracks.Health);
            Assert.AreEqual(0, contact.LandingCount);
        }

        [Test]
        public void JetJumpImpulseMatchesStandingAndRunningRules()
        {
            var still = GravityModeTank.JetJumpImpulse(60f, 0f, Vector3.zero, 10000f);
            Assert.Greater(still.y, 0f);
            Assert.AreEqual(0f, new Vector2(still.x, still.z).magnitude, 0.0001f);

            var moving = GravityModeTank.JetJumpImpulse(60f, 30f, new Vector3(10f, 0f, 0f), 10000f);
            Assert.AreEqual(still.y, moving.y, 0.0001f);
            Assert.Greater(moving.x, 0f);
            Assert.AreEqual(0f, moving.z, 0.0001f);
        }

        [Test]
        public void AirControlRespondsAndClampsToSourceMaximum()
        {
            var yaw = TankSuspension.ApplyGravityModeAirControl(Vector3.zero, Vector3.up, Vector3.right, 0f, 1f, 1f / 60f);
            // Same sign as grounded steering: right stick, clockwise from above.
            Assert.Greater(Vector3.Dot(yaw, Vector3.up), 0f);

            var tiltedUp = Quaternion.Euler(20f, 0f, 0f) * Vector3.up;
            var pitch = TankSuspension.ApplyGravityModeAirControl(Vector3.zero, tiltedUp, Vector3.right, 1f, 0f, 1f / 60f);
            Assert.Greater(Mathf.Abs(Vector3.Dot(pitch, Vector3.right)), 0f);

            var clamped = TankSuspension.ApplyGravityModeAirControl(new Vector3(10f, 10f, 10f), Vector3.up, Vector3.right, 1f, 1f, 1f / 60f);
            Assert.LessOrEqual(clamped.magnitude, GravityModeTank.AirAngularMax + 0.0001f);
        }

        [Test]
        public void KnockbackDeltaVPreservesSourceOrderingAndCap()
        {
            var penetration = GravityModeTank.KnockbackDeltaV(105f, GravityModeTank.HitType.Penetration);
            var bounce = GravityModeTank.KnockbackDeltaV(105f, GravityModeTank.HitType.Bounce);
            var ricochet = GravityModeTank.KnockbackDeltaV(105f, GravityModeTank.HitType.Ricochet);
            var cap = GravityModeTank.KnockbackDeltaV(183f, GravityModeTank.HitType.Ricochet);
            Assert.Greater(bounce, penetration);
            Assert.Greater(ricochet, bounce);
            Assert.LessOrEqual(cap, GravityModeTank.HitDeltaVMax);
        }

        [Test]
        public void GravityBattleSessionMountsControllerInsteadOfUprising()
        {
            var session = BattleSession.Create(_root.transform, false, autoStart: false, mode: BattleMode.Gravity);
            try
            {
                Assert.AreEqual(BattleMode.Gravity, session.Mode);
                Assert.IsNotNull(session.Player.GetComponent<GravityModeTank>());
                Assert.IsNull(session.Player.GetComponent<UprisingTank>());
            }
            finally
            {
                if (session != null) Object.DestroyImmediate(session.gameObject);
            }
        }
    }
}
