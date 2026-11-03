using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The sixth-sense bulb ("anims/lamp" and its timers) and the hit
    /// direction markers, ported from ui_player_battle.gd and
    /// ui_damage_indicator.gd.
    /// </summary>
    public sealed class SpottedLampDamageMarkerTests
    {
        private const float Pi = 3.1415927f;
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        private BattleSpottedLamp NewLamp()
        {
            _root = new GameObject("SpottedLampDamageMarkerTests");
            var canvas = new GameObject("canvas", typeof(RectTransform));
            canvas.transform.SetParent(_root.transform, false);
            return BattleSpottedLamp.Create(canvas.transform);
        }

        private BattleSession NewSession()
        {
            Random.InitState(982451653);
            _root = new GameObject("SpottedLampDamageMarkerTests");
            var session = BattleSession.Create(
                _root.transform, false, BattleWorld.DefaultMapId, "t-34", false, "Tester");
            session.StartBattle();
            return session;
        }

        // ------------------------------------------------------------ lamp

        [Test]
        public void LampAnimation_MatchesTheAnimsResKeys()
        {
            Assert.That(SpottedLampTuning.LampAlpha(0f), Is.EqualTo(1f));
            Assert.That(SpottedLampTuning.LampAlpha(4.6f), Is.EqualTo(1f));
            Assert.That(SpottedLampTuning.LampAlpha(5f), Is.EqualTo(0f));
            // The fade segment is eased with transition 1.1487: x^1.1487.
            var mid = (4.633333206f + 5f) * 0.5f;
            Assert.That(SpottedLampTuning.LampAlpha(mid),
                Is.EqualTo(1f - Mathf.Pow(0.5f, 1.148698449f)).Within(1e-4f));

            Assert.That(SpottedLampTuning.ShineAlpha(0f), Is.EqualTo(0f));
            Assert.That(SpottedLampTuning.ShineAlpha(0.5f), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(SpottedLampTuning.ShineAlpha(1f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(SpottedLampTuning.ShineAlpha(1.966666698f), Is.EqualTo(0f).Within(1e-5f));
            Assert.That(SpottedLampTuning.ShineAlpha(2.966666698f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(SpottedLampTuning.ShineAlpha(5f), Is.EqualTo(0f));

            Assert.That(SpottedLampTuning.Ease(0.25f, 2f), Is.EqualTo(0.0625f).Within(1e-6f));
            Assert.That(SpottedLampTuning.Ease(0.75f, 0.5f), Is.EqualTo(1f - 0.0625f).Within(1e-6f));
        }

        [Test]
        public void Lamp_StaysActiveSoItsOwnUpdateKeepsTheTimersRunning()
        {
            var lamp = NewLamp();

            // The old port parented the component under the hidden node, so
            // Update() never ran and the bulb never lit.
            Assert.That(lamp.gameObject.activeInHierarchy, Is.True);
            Assert.That(lamp.isActiveAndEnabled, Is.True);
            Assert.That(lamp.Visible, Is.False);
            Assert.That(lamp.Group.alpha, Is.EqualTo(0f), "setup(): TRANSPARENT");
            Assert.That(lamp.Group.blocksRaycasts, Is.False);
        }

        [Test]
        public void Lamp_LightsThreeSecondsAfterTheSpotAndPlaysTheAnimation()
        {
            var lamp = NewLamp();

            lamp.Schedule(0f);
            lamp.Tick(2.9f);
            Assert.That(lamp.Visible, Is.False, "before the delay");

            lamp.Tick(3f);
            Assert.That(lamp.Visible, Is.True);
            Assert.That(lamp.ShowCount, Is.EqualTo(1));
            Assert.That(lamp.Group.alpha, Is.EqualTo(1f));
            Assert.That(lamp.Shine.color.a, Is.EqualTo(0f).Within(1e-5f));

            lamp.Tick(4f);
            Assert.That(lamp.Shine.color.a, Is.EqualTo(1f).Within(1e-5f), "shine peak");

            lamp.Tick(7.8f);
            Assert.That(lamp.Group.alpha, Is.LessThan(1f).And.GreaterThan(0f), "fading");

            lamp.Tick(8.1f);
            Assert.That(lamp.Visible, Is.True, "node still shown until 5.2 s");
            Assert.That(lamp.Group.alpha, Is.EqualTo(0f), "animation finished");

            lamp.Tick(8.2f);
            Assert.That(lamp.Visible, Is.False, "_hide_spotted_lamp");
        }

        [Test]
        public void Lamp_EverySpotStartsItsOwnTimerButAPlayingAnimationIgnoresIt()
        {
            var lamp = NewLamp();

            lamp.Schedule(0f);
            lamp.Schedule(1f);
            Assert.That(lamp.PendingCount, Is.EqualTo(2));

            lamp.Tick(3f);
            lamp.Tick(4f);
            Assert.That(lamp.ShowCount, Is.EqualTo(1), "second timer hit a playing animation");
            Assert.That(lamp.PendingCount, Is.EqualTo(0));

            // A spot after the animation ended lights it again.
            lamp.Schedule(6f);
            lamp.Tick(9f);
            Assert.That(lamp.ShowCount, Is.EqualTo(2));
            Assert.That(lamp.Visible, Is.True);
            Assert.That(lamp.Group.alpha, Is.EqualTo(1f));
        }

        [Test]
        public void Lamp_StaysDarkForADeadTankOrAHiddenCombatHud()
        {
            var lamp = NewLamp();
            var alive = false;
            lamp.CanShow = () => alive;

            lamp.Schedule(0f);
            Assert.That(lamp.PendingCount, Is.EqualTo(0), "_on_player_spotted on a dead tank");

            alive = true;
            lamp.Schedule(0f);
            lamp.SetCombatVisible(false);
            lamp.Tick(3f);
            Assert.That(lamp.ShowCount, Is.EqualTo(0), "!_combat_hud_alive");

            lamp.SetCombatVisible(true);
            lamp.Schedule(10f);
            lamp.Tick(13f);
            Assert.That(lamp.Visible, Is.True);

            lamp.SetCombatVisible(false);
            Assert.That(lamp.Visible, Is.False, "set_combat_hud_visible(false) hides the lamp");
            Assert.That(lamp.Group.alpha, Is.EqualTo(0f));
        }

        [Test]
        public void Session_SchedulesTheLampWhenThePlayerIsSpotted()
        {
            var session = NewSession();
            // The HUD root itself waits for the map intro; the lamp node never
            // switches itself off.
            Assert.That(session.Lamp.gameObject.activeSelf, Is.True);
            Assert.That(session.Lamp.transform.parent, Is.EqualTo(session.Hud.Root));

            session.Player.SetSpotted(true);
            Assert.That(session.Lamp.PendingCount, Is.EqualTo(1));

            session.SetCombatHudVisible(false);
            session.Lamp.Tick(Time.time + 10f);
            Assert.That(session.Lamp.ShowCount, Is.EqualTo(0));
        }

        // --------------------------------------------------------- markers

        [Test]
        public void MarkerRotation_PointsAtTheAttackerRoundTheCrosshair()
        {
            var camera = new Vec3(0f, 0f, 0f);

            // Source space, camera yaw 0 looks down -Z.
            Assert.That(DamageMarkerMath.Rotation(camera, new Vec3(0f, 0f, -10f), 0f),
                Is.EqualTo(-Pi * 0.5f).Within(1e-5f), "ahead: arc above the centre");
            Assert.That(DamageMarkerMath.Rotation(camera, new Vec3(10f, 0f, 0f), 0f),
                Is.EqualTo(0f).Within(1e-5f), "right: arc to the right");
            Assert.That(DamageMarkerMath.Rotation(camera, new Vec3(0f, 0f, 10f), 0f),
                Is.EqualTo(-Pi * 1.5f).Within(1e-5f), "behind: arc below");
            Assert.That(DamageMarkerMath.Rotation(camera, new Vec3(-10f, 0f, 0f), 0f),
                Is.EqualTo(-Pi).Within(1e-5f), "left: arc to the left");

            // Turning the camera 90 degrees left puts that attacker ahead.
            Assert.That(DamageMarkerMath.Rotation(camera, new Vec3(-10f, 0f, 0f), Pi * 0.5f),
                Is.EqualTo(-Pi * 0.5f).Within(1e-5f));
        }

        [Test]
        public void MarkerRotation_MirroredInputsMatchTheSourceFormula()
        {
            // Unity camera at the origin looking down +Z (source -Z).
            var right = new Vec3(1f, 0f, 0f);
            var up = new Vec3(0f, 1f, 0f);
            var forward = new Vec3(0f, 0f, 1f);
            var origin = new Vec3(0f, 0f, 0f);

            Assert.That(DamageMarkerMath.RotationFromMirrored(
                origin, new Vec3(0f, 0f, 10f), right, up, forward),
                Is.EqualTo(-Pi * 0.5f).Within(1e-5f), "ahead");
            Assert.That(DamageMarkerMath.RotationFromMirrored(
                origin, new Vec3(10f, 0f, 0f), right, up, forward),
                Is.EqualTo(0f).Within(1e-5f), "right");

            // Camera turned to look along Unity +X: an attacker there is ahead.
            Assert.That(DamageMarkerMath.RotationFromMirrored(
                origin, new Vec3(10f, 0f, 0f),
                new Vec3(0f, 0f, -1f), up, new Vec3(1f, 0f, 0f)),
                Is.EqualTo(-Pi * 0.5f).Within(1e-4f).Or.EqualTo(Pi * 1.5f).Within(1e-4f));

            Assert.That(DamageMarkerMath.UnityZDegrees(Pi * 0.5f), Is.EqualTo(-90f).Within(1e-4f));
        }

        [Test]
        public void MarkerTint_AndLifetimeFollowTheSource()
        {
            Assert.That(DamageMarkerMath.Tint(true), Is.EqualTo(new[] { 0.808f, 0f, 0f, 0.808f }));
            Assert.That(DamageMarkerMath.Tint(false), Is.EqualTo(new[] { 0.061f, 0.061f, 0.061f, 0.583f }));
            Assert.That(DamageMarkerMath.Fade(0f), Is.EqualTo(1f));
            Assert.That(DamageMarkerMath.Fade(5f), Is.EqualTo(1f));
            Assert.That(DamageMarkerMath.Fade(5.5f), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(DamageMarkerMath.Fade(6f), Is.EqualTo(0f));
            Assert.That(DamageMarkerMath.Expired(5.99f), Is.False);
            Assert.That(DamageMarkerMath.Expired(6f), Is.True);
        }

        private static DamageInfo Hit(BattleTank attacker, int dmg, DamageType type = DamageType.Shell)
        {
            return new DamageInfo { AttackerTank = attacker, Dmg = dmg, DmgBase = 100, DmgType = type };
        }

        [Test]
        public void Markers_RedForPenetrationGreyForABlockAndGoneAfterSixSeconds()
        {
            var session = NewSession();
            var markers = session.Hud.DamageMarkers;
            Assert.That(markers, Is.Not.Null);
            Assert.That(markers.Handle.parent, Is.EqualTo(session.Hud.Root));
            Assert.That(markers.Handle.GetSiblingIndex(),
                Is.EqualTo(session.Hud.DynamicCrosshair.GetSiblingIndex() + 1),
                "markers sits right after crosshair_dynamic");

            var enemy = session.Enemies[0];
            markers.Receive(Hit(enemy, 120), 0f);
            markers.Receive(Hit(session.Enemies[1], 0), 0f);
            Assert.That(markers.Count, Is.EqualTo(2));

            var red = markers.ColorOf(0);
            Assert.That(red.r, Is.EqualTo(0.808f).Within(1e-5f));
            Assert.That(red.a, Is.EqualTo(0.808f).Within(1e-5f));
            var grey = markers.ColorOf(1);
            Assert.That(grey.g, Is.EqualTo(0.061f).Within(1e-5f));
            Assert.That(grey.a, Is.EqualTo(0.583f).Within(1e-5f));

            // marker_template_dmg/head geometry.
            var head = markers.HeadOf(0);
            Assert.That(head.rect.size, Is.EqualTo(new Vector2(678f, 180f)));
            Assert.That(head.pivot, Is.EqualTo(new Vector2(0.5f, 1f)));
            Assert.That(head.anchoredPosition.x, Is.EqualTo(233.5f).Within(1e-3f));
            Assert.That(head.localScale.x, Is.EqualTo(0.5f));

            markers.Tick(5.5f);
            Assert.That(markers.ColorOf(0).a, Is.EqualTo(0.404f).Within(1e-4f), "half faded");
            markers.Tick(6f);
            Assert.That(markers.Count, Is.EqualTo(0));
        }

        [Test]
        public void Markers_LaterDamageFromTheSameAttackerReplacesItsMarker()
        {
            var session = NewSession();
            var markers = session.Hud.DamageMarkers;
            var enemy = session.Enemies[0];
            var other = session.Enemies[1];

            markers.Receive(Hit(enemy, 100), 0f);
            markers.Receive(Hit(other, 100), 0f);
            // A second hit by the same attacker in the same frame keeps both.
            markers.Receive(Hit(enemy, 50), 0f);
            Assert.That(markers.Count, Is.EqualTo(3));

            // A block does not reach on_enemy_damage: the old marker stays.
            markers.Receive(Hit(enemy, 0), 1f);
            Assert.That(markers.Count, Is.EqualTo(4));

            // A penetration later on clears that attacker's earlier markers.
            markers.Receive(Hit(enemy, 80), 2f);
            Assert.That(markers.Count, Is.EqualTo(2));
            Assert.That(markers.AttackerOf(0), Is.EqualTo(other));
            Assert.That(markers.AttackerOf(1), Is.EqualTo(enemy));

            // A ram has no marker of its own but is still damage.
            markers.Receive(Hit(enemy, 30, DamageType.Collision), 3f);
            Assert.That(markers.Count, Is.EqualTo(1));
            Assert.That(markers.AttackerOf(0), Is.EqualTo(other));
        }

        [Test]
        public void Markers_OnlyForMarkerHitsWithAnAttackerWhileTheCombatHudIsUp()
        {
            var session = NewSession();
            var markers = session.Hud.DamageMarkers;
            var enemy = session.Enemies[0];

            markers.Receive(Hit(null, 100), 0f);
            markers.Receive(Hit(enemy, 30, DamageType.Collision), 0f);
            markers.Receive(new DamageInfo
            {
                AttackerTank = enemy, Dmg = 40, DmgBase = 40, DmgType = DamageType.Shell,
                SuppressFeedback = true,
            }, 0f);
            Assert.That(markers.Count, Is.EqualTo(0));

            markers.Receive(Hit(enemy, 100), 1f);
            Assert.That(markers.Count, Is.EqualTo(1));

            session.SetCombatHudVisible(false);
            Assert.That(markers.Count, Is.EqualTo(0), "set_combat_hud_visible(false) frees them");
            markers.Receive(Hit(enemy, 100), 2f);
            Assert.That(markers.Count, Is.EqualTo(0));

            session.SetCombatHudVisible(true);
            markers.Receive(Hit(enemy, 100), 3f);
            Assert.That(markers.Count, Is.EqualTo(1));
        }

        [Test]
        public void Markers_FollowThePlayerHealthEventsAndFaceTheAttacker()
        {
            var session = NewSession();
            var markers = session.Hud.DamageMarkers;
            var player = session.Player;
            var enemy = session.Enemies[0];
            var camera = session.Hud.ActiveCamera;
            Assert.That(camera, Is.Not.Null);

            player.Health.ReceiveDamage(Hit(enemy, 10));
            Assert.That(markers.Count, Is.EqualTo(1), "on_damage_info_taken");

            markers.Tick(Time.time);
            var view = camera.transform;
            var expected = DamageMarkerMath.RotationFromMirrored(
                new Vec3(view.position.x, view.position.y, view.position.z),
                new Vec3(enemy.Pos().x, enemy.Pos().y, enemy.Pos().z),
                new Vec3(view.right.x, view.right.y, view.right.z),
                new Vec3(view.up.x, view.up.y, view.up.z),
                new Vec3(view.forward.x, view.forward.y, view.forward.z));
            var actual = markers.RotationOf(0);
            var delta = Mathf.DeltaAngle(actual * Mathf.Rad2Deg, expected * Mathf.Rad2Deg);
            Assert.That(delta, Is.EqualTo(0f).Within(1e-2f));
        }
    }
}
