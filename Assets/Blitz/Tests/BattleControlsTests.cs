using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class BattleControlsTests
    {
        private const float Tolerance = 1e-4f;

        private const float CenterX = ControlsTuning.JoystickWidth * 0.5f;

        private const float CenterY = ControlsTuning.JoystickHeight * 0.5f;

        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }

            _spawned.Clear();
        }

        [Test]
        public void TheKnobFollowsTheFingerInsideTheBaseRadius()
        {
            float knobX;
            float knobY;

            ControlsTuning.KnobPosition(
                CenterX,
                CenterY,
                CenterX + 30f,
                CenterY - 40f,
                ControlsTuning.JoystickBaseRadius,
                out knobX,
                out knobY);

            Assert.AreEqual(CenterX + 30f, knobX, Tolerance);
            Assert.AreEqual(CenterY - 40f, knobY, Tolerance);
        }

        [Test]
        public void TheKnobIsPinnedToTheBaseRadiusOutsideIt()
        {
            float knobX;
            float knobY;

            ControlsTuning.KnobPosition(
                CenterX,
                CenterY,
                CenterX + 300f,
                CenterY,
                ControlsTuning.JoystickBaseRadius,
                out knobX,
                out knobY);

            Assert.AreEqual(
                ControlsTuning.JoystickBaseRadius,
                ControlsTuning.Distance(CenterX, CenterY, knobX, knobY),
                1e-3f);
            Assert.AreEqual(CenterX + ControlsTuning.JoystickBaseRadius, knobX, 1e-3f);
            Assert.AreEqual(CenterY, knobY, 1e-3f);
        }

        [Test]
        public void TheDeadzoneGateIsStrict()
        {
            Assert.IsFalse(ControlsTuning.Engaged(ControlsTuning.JoystickDeadzone));
            Assert.IsTrue(ControlsTuning.Engaged(ControlsTuning.JoystickDeadzone + 0.01f));
        }

        [Test]
        public void PushingTheStickUpDrivesForwardWithoutSteering()
        {
            float forward;
            float right;

            ControlsTuning.DriveAxes(
                CenterX, CenterY, CenterX, CenterY - 80f, out forward, out right);

            Assert.AreEqual(1f, forward, Tolerance);
            Assert.AreEqual(0f, right, Tolerance);
        }

        [Test]
        public void AxisComponentsBelowTheThresholdAreDropped()
        {
            float forward;
            float right;

            var fifteen = 15f * Mathf.Deg2Rad;
            ControlsTuning.DriveAxes(
                CenterX,
                CenterY,
                CenterX + Mathf.Sin(fifteen) * 100f,
                CenterY - Mathf.Cos(fifteen) * 100f,
                out forward,
                out right);

            // sin 15 is 0.2588, under the 0.33 the source demands.
            Assert.AreEqual(0f, right, Tolerance);
            Assert.AreEqual(Mathf.Cos(fifteen), forward, Tolerance);

            var twenty = 20f * Mathf.Deg2Rad;
            ControlsTuning.DriveAxes(
                CenterX,
                CenterY,
                CenterX + Mathf.Sin(twenty) * 100f,
                CenterY - Mathf.Cos(twenty) * 100f,
                out forward,
                out right);

            // sin 20 is 0.342, which survives.
            Assert.AreEqual(Mathf.Sin(twenty), right, Tolerance);
        }

        [Test]
        public void TheDriveIsDirectionBasedRatherThanProportional()
        {
            float nearForward;
            float nearRight;
            float farForward;
            float farRight;

            ControlsTuning.DriveAxes(
                CenterX, CenterY, CenterX, CenterY - 30f, out nearForward, out nearRight);
            ControlsTuning.DriveAxes(
                CenterX, CenterY, CenterX, CenterY - 200f, out farForward, out farRight);

            Assert.AreEqual(nearForward, farForward, Tolerance);
            Assert.AreEqual(1f, farForward, Tolerance);
        }

        [Test]
        public void InsideTheDeadzoneNothingDrives()
        {
            float forward;
            float right;

            ControlsTuning.DriveAxes(
                CenterX, CenterY, CenterX, CenterY - 20f, out forward, out right);

            Assert.AreEqual(0f, forward, Tolerance);
            Assert.AreEqual(0f, right, Tolerance);
        }

        [Test]
        public void ControlPointPutsTheOriginInTheTopLeftCorner()
        {
            var width = ControlsTuning.JoystickWidth;
            var height = ControlsTuning.JoystickHeight;

            // The top left corner of a centre pivoted rect, in Unity's space.
            Assert.AreEqual(
                0f, ControlsTuning.ControlPointX(-width * 0.5f, width, 0.5f), Tolerance);
            Assert.AreEqual(
                0f, ControlsTuning.ControlPointY(height * 0.5f, height, 0.5f), Tolerance);

            // The pivot itself is the centre of the control.
            Assert.AreEqual(
                width * 0.5f, ControlsTuning.ControlPointX(0f, width, 0.5f), Tolerance);
            Assert.AreEqual(
                height * 0.5f, ControlsTuning.ControlPointY(0f, height, 0.5f), Tolerance);
        }

        [Test]
        public void TheSceneRectsSurviveThePort()
        {
            Assert.AreEqual(139f, ControlsTuning.JoystickWidth, Tolerance);
            Assert.AreEqual(138f, ControlsTuning.JoystickHeight, Tolerance);
            Assert.AreEqual(2.625f, ControlsTuning.JoystickScale, Tolerance);
            Assert.AreEqual(125f, ControlsTuning.FireWidth, Tolerance);
            Assert.AreEqual(125f, ControlsTuning.FireHeight, Tolerance);
            Assert.AreEqual(97f, ControlsTuning.SniperWidth, Tolerance);
            Assert.AreEqual(97f, ControlsTuning.SniperHeight, Tolerance);
            Assert.AreEqual(53f, ControlsTuning.AutoAimWidth, Tolerance);
            Assert.AreEqual(53f, ControlsTuning.AutoAimHeight, Tolerance);
            Assert.AreEqual(78f, ControlsTuning.MenuWidth, Tolerance);
            Assert.AreEqual(62f, ControlsTuning.MenuHeight, Tolerance);
        }

        [Test]
        public void TheJoystickReportsTheAxesFromAControlSpaceTouch()
        {
            var joystick = NewJoystick();
            var center = joystick.Center;

            joystick.Press(new Vector2(center.x, center.y - 100f));

            Assert.IsTrue(joystick.Engaged);
            Assert.AreEqual(1f, joystick.Forward, Tolerance);
            Assert.AreEqual(0f, joystick.Right, Tolerance);

            // The tip is drawn inside the base in Godot, so a finger far past
            // the ring pins it to the travel radius instead of letting it
            // leave the widget.
            Assert.AreEqual(
                center.y - ControlsTuning.JoystickKnobTravel,
                joystick.KnobPoint.y,
                Tolerance);
        }

        [Test]
        public void ReleasingTheJoystickStopsTheDrive()
        {
            var joystick = NewJoystick();
            var center = joystick.Center;
            joystick.Press(new Vector2(center.x, center.y - 100f));

            joystick.Release();

            Assert.IsFalse(joystick.Engaged);
            Assert.AreEqual(0f, joystick.Forward, Tolerance);
            Assert.AreEqual(0f, joystick.Right, Tolerance);
            Assert.AreEqual(center.x, joystick.KnobPoint.x, Tolerance);
            Assert.AreEqual(center.y, joystick.KnobPoint.y, Tolerance);
        }

        [Test]
        public void TwoFingersPinchAndSuppressTheLook()
        {
            var surface = NewSurface();
            var looks = 0;
            var pinch = -1f;
            surface.Looked = value => looks++;
            surface.Pinched = distance => pinch = distance;

            surface.Press(0, new Vector2(100f, 100f));
            surface.Press(1, new Vector2(400f, 100f));
            surface.Move(1, new Vector2(500f, 100f), new Vector2(100f, 0f));

            Assert.AreEqual(400f, pinch, Tolerance);
            Assert.AreEqual(0, looks);
        }

        [Test]
        public void OneFingerLooksWithTheGodotRelative()
        {
            var surface = NewSurface();
            var relative = Vector2.zero;
            var pinches = 0;
            surface.Looked = value => relative = value;
            surface.Pinched = distance => pinches++;

            surface.Press(0, new Vector2(100f, 100f));
            surface.Move(0, new Vector2(112f, 93f), new Vector2(12f, -7f));

            Assert.AreEqual(12f, relative.x, Tolerance);
            Assert.AreEqual(-7f, relative.y, Tolerance);
            Assert.AreEqual(0, pinches);
        }

        [Test]
        public void EveryPressAndReleaseDropsThePinchBaseline()
        {
            var surface = NewSurface();
            var resets = 0;
            surface.PinchEnded = () => resets++;

            surface.Press(0, new Vector2(10f, 10f));
            surface.Press(1, new Vector2(20f, 20f));
            surface.Release(1);

            Assert.AreEqual(3, resets);
            Assert.AreEqual(1, surface.PointerCount);
        }

        private BattleJoystick NewJoystick()
        {
            var go = new GameObject("joystick", typeof(RectTransform));
            _spawned.Add(go);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(
                ControlsTuning.JoystickWidth, ControlsTuning.JoystickHeight);
            return go.AddComponent<BattleJoystick>();
        }

        private BattleTouchSurface NewSurface()
        {
            var go = new GameObject("touch_surface");
            _spawned.Add(go);
            return go.AddComponent<BattleTouchSurface>();
        }
    }
}
