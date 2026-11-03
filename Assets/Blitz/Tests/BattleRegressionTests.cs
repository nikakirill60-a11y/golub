using System.Collections;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    public sealed class BattleRegressionTests
    {
        public static IEnumerable GuidedCases()
        {
            foreach (float speed in new[] { 75f, 85f })
            foreach (float range in new[] { 20f, 50f, 100f, 200f, 400f, 600f })
            foreach (float height in new[] { -3f, 0f, 3f })
            foreach (float delta in new[] { 1f / 30f, 1f / 60f, 1f / 100f })
                yield return new TestCaseData(speed, range, height, delta);
        }
        [TestCaseSource(nameof(GuidedCases))]
        public void OverheadRocket_ActuallyIntersectsTheAimPoint(float speed, float range, float height, float delta)
        {
            var turn = speed == 75f ? .5f : .6f;
            var position = new Vector3(0, 5, 0);
            var target = new Vector3(0, 5 + height, range);
            var pitch = ArtyMath.GuidedLaunchPitch(range, 12, speed, turn, height);
            var velocity = Quaternion.Euler(-pitch, 0, 0) * Vector3.forward * speed;
            var origin = position;
            var launchVelocity = velocity;
            float closest = float.PositiveInfinity;
            for (var t = 0f; t < 720f / speed; t += delta)
            {
                var before = velocity;
                velocity = BattleProjectile.GuidedVelocity(velocity, position, target, turn, delta, true, origin, launchVelocity);
                Assert.AreEqual(speed, velocity.magnitude, .002f, "no speed boosts or teleporting to the target");
                Assert.LessOrEqual(Vector3.Angle(before, velocity) * Mathf.Deg2Rad, turn * delta + .001f);
                var next = position + velocity * delta;
                var segment = next - position;
                var fraction = Mathf.Clamp01(Vector3.Dot(target - position, segment) / segment.sqrMagnitude);
                closest = Mathf.Min(closest, Vector3.Distance(target, position + segment * fraction));
                position = next;
                if (position.z > range + 10) break;
            }
            Assert.Less(closest, .25f, "terminal guidance must reach the tank rather than fly above it");
        }
        [Test]
        public void LobGuidance_EndsAtTheActualTargetNotAboveIt()
        {
            var origin = new Vector3(0, 2, 0);
            var target = new Vector3(0, 2, 400);
            var launch = Quaternion.Euler(-12, 0, 0) * Vector3.forward * 85;
            Assert.AreEqual(target, BattleProjectile.LobTarget(origin, launch, target, target, .6f, .02f));
            var middle = BattleProjectile.LobTarget(origin, launch, new Vector3(0, 20, 200), target, .6f, .02f);
            Assert.Greater(middle.y, target.y);
        }
        [TestCase("maps/rudniki/objects/static/stone_ss_04.tscn", true)]
        [TestCase("maps/rudniki/objects/static/plentyOfStones.tscn", false)]
        [TestCase("maps/rudniki/objects/static/bl_03.tscn", false)]
        [TestCase(null, false)]
        public void SolidRockClassification_DoesNotCloseGapsInArchitecture(string source, bool solid)
        { Assert.AreEqual(solid, BattleObjects.IsSolidRock(source)); }
        [Test]
        public void SniperLens_StaysOnGunPivotAndRestoresTheTankModel()
        {
            BattleTank tank = null;
            BattleCamera view = null;
            GameObject sniperRoot = null;
            GameObject artyRoot = null;
            try
            {
                var db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                tank = BattleTank.Spawn(
                    null, "scope regression", db.Get("t49_rocket"), null, new Bounds(), 0);
                tank.GetComponent<Rigidbody>().isKinematic = true;
                view = BattleCamera.Create(tank, false);
                sniperRoot = view.Sniper.transform.parent.gameObject;
                artyRoot = view.Arty.transform.parent.gameObject;
                var renderers = tank.Model.GetComponentsInChildren<Renderer>(true);
                Assert.Greater(renderers.Length, 0);

                view.SetSniperMode(true);
                for (var i = 0; i < renderers.Length; i++)
                {
                    Assert.IsTrue(renderers[i].forceRenderingOff);
                }

                view.Look(new Vector2(0f, 100000f));
                view.Follow(1f / 60f);
                Assert.Less(Vector3.Distance(
                    tank.Turret.GunPivot.position, view.Sniper.transform.position), 1e-4f);

                view.Look(new Vector2(0f, -200000f));
                view.Follow(1f / 60f);
                Assert.Less(Vector3.Distance(
                    tank.Turret.GunPivot.position, view.Sniper.transform.position), 1e-4f);
                Assert.Less(view.Sniper.transform.localPosition.sqrMagnitude, 1e-8f);

                view.SetSniperMode(false);
                for (var i = 0; i < renderers.Length; i++)
                {
                    Assert.IsFalse(renderers[i].forceRenderingOff);
                }

                // Handing the camera to another observed tank restores the
                // previous model and applies the current scope state to the new one.
                view.SetSniperMode(true);
                view.Tank = null;
                for (var i = 0; i < renderers.Length; i++)
                {
                    Assert.IsFalse(renderers[i].forceRenderingOff);
                }

                view.Tank = tank;
                for (var i = 0; i < renderers.Length; i++)
                {
                    Assert.IsTrue(renderers[i].forceRenderingOff);
                }
                view.SetSniperMode(false);
            }
            finally
            {
                if (sniperRoot != null) Object.DestroyImmediate(sniperRoot);
                if (artyRoot != null) Object.DestroyImmediate(artyRoot);
                if (view != null) Object.DestroyImmediate(view.gameObject);
                if (tank != null) Object.DestroyImmediate(tank.gameObject);
            }
        }
        /// <summary>
        /// Device repro: the board lives in a nested override canvas under the
        /// HUD (BattleHud.SortingOrder) while the controls canvas (one above)
        /// carries a full screen touch_surface. EventSystem ranked the touch
        /// surface first, so a tap on the list never reached the button.
        /// </summary>
        [Test]
        public void ScoreboardRaycaster_OutranksControlsCanvasTouchSurface()
        {
            var hud = new GameObject("hud canvas", typeof(RectTransform), typeof(Canvas));
            var controls = new GameObject("controls canvas", typeof(RectTransform), typeof(Canvas));
            try
            {
                var hudCanvas = hud.GetComponent<Canvas>();
                hudCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                hudCanvas.sortingOrder = BattleHud.SortingOrder;
                var controlsCanvas = controls.GetComponent<Canvas>();
                controlsCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                controlsCanvas.sortingOrder = BattleHud.SortingOrder + 1;
                controls.AddComponent<UnityEngine.UI.GraphicRaycaster>();

                var board = BattleScoreboard.Create((RectTransform)hud.transform);
                board.Toggle();
                board.Advance(10f);
                Canvas.ForceUpdateCanvases();
                var raycaster = board.GetComponent<BattleScoreboardRaycaster>();
                var controlsRaycaster = controls.GetComponent<UnityEngine.UI.GraphicRaycaster>();
                Assert.Greater(raycaster.sortOrderPriority, controlsRaycaster.sortOrderPriority,
                    "scoreboard must outrank the controls canvas regardless of nested canvas reporting");

                Assert.GreaterOrEqual(raycaster.sortOrderPriority, BattleScoreboard.SortingOrder);
                Assert.Less(raycaster.sortOrderPriority, BattlePauseMenu.SortingOrder,
                    "pause menu must still outrank the scoreboard");
            }
            finally { Object.DestroyImmediate(hud); Object.DestroyImmediate(controls); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ScoreboardRaycast_DoesNotDependOnInvisibleGraphicBeingRendered(bool disableGraphic)
        {
            var root = new GameObject("scoreboard input canvas", typeof(RectTransform), typeof(Canvas));
            var events = new GameObject("scoreboard input events", typeof(EventSystem));
            try
            {
                var canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = BattleScoreboard.SortingOrder;
                var rect = BlitzLegacy.Runtime.Ui.UiFactory.Node("handle", root.transform);
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
                rect.sizeDelta = new Vector2(100, 100);
                var image = rect.gameObject.AddComponent<UnityEngine.UI.Image>();
                image.color = Color.clear;
                var button = rect.gameObject.AddComponent<BattleScoreboardButton>();
                button.targetGraphic = image;
                var raycaster = root.AddComponent<BattleScoreboardRaycaster>();
                raycaster.Bind(button, null, BattleScoreboard.SortingOrder);
                Canvas.ForceUpdateCanvases();
                image.enabled = !disableGraphic;
                image.canvasRenderer.cull = true;
                var pointer = new PointerEventData(events.GetComponent<EventSystem>())
                { position = RectTransformUtility.WorldToScreenPoint(null, rect.position) };
                var hits = new System.Collections.Generic.List<RaycastResult>();
                raycaster.Raycast(pointer, hits);
                Assert.AreEqual(1, hits.Count, "invisible handle must still receive input");
                Assert.AreSame(button.gameObject, hits[0].gameObject);
                Assert.AreEqual(BattleScoreboard.SortingOrder, hits[0].sortingOrder);
                hits.Clear();
                button.interactable = false;
                raycaster.Raycast(pointer, hits);
                Assert.IsEmpty(hits, "disabled button must not intercept gameplay input");
                button.interactable = true;
                var group = root.AddComponent<CanvasGroup>();
                group.blocksRaycasts = false;
                raycaster.Raycast(pointer, hits);
                Assert.IsEmpty(hits, "ancestor input filter must be respected");
                group.blocksRaycasts = true;
                pointer.position += new Vector2(101, 0);
                raycaster.Raycast(pointer, hits);
                Assert.IsEmpty(hits, "outside handle remains gameplay input");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(events); }
        }
        [Test]
        public void SlidingScoreboard_OnePressTogglesOnceEvenWhenReleasedElsewhere()
        {
            var root = new GameObject("scoreboard button test", typeof(RectTransform));
            var events = new GameObject("test events", typeof(EventSystem));
            try
            {
                var button = root.AddComponent<BattleScoreboardButton>();
                var toggles = 0;
                button.onClick.AddListener(() => toggles++);
                var press = new PointerEventData(events.GetComponent<EventSystem>()) { pointerId = 3 };
                button.OnPointerDown(press);
                Assert.AreEqual(1, toggles);
                button.OnPointerDown(press);
                button.OnPointerUp(press);
                button.OnPointerClick(press);
                Assert.AreEqual(1, toggles, "no duplicate on release after the moving panel");
                button.OnSubmit(new BaseEventData(events.GetComponent<EventSystem>()));
                Assert.AreEqual(2, toggles, "keyboard submit still works");
                button.interactable = false;
                button.OnPointerDown(press);
                Assert.AreEqual(2, toggles);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(events); }
        }
    }
}
