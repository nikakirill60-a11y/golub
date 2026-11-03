using System;
using System.Collections.Generic;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Nested drags on a vertical page: a horizontal strip and a slider keep
    /// a drag along their own axis and give a swipe across it to the page,
    /// as Android's nested scrolling decides. The input module is stood in
    /// for by calling the handlers in its order (press, potential drag,
    /// begin past the threshold, drag, release).
    /// </summary>
    public sealed class UiDragHandoffTests
    {
        private const float DragThreshold = 10f;

        private GameObject _root;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("UiDragHandoffTests", typeof(RectTransform));
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        [Test]
        public void Strip_HandsAVerticalSwipeToThePage()
        {
            var page = Page();
            var strip = Strip(page.content, 1200f);

            var swipe = Swipe(strip.gameObject, Centre(strip), new Vector2(0f, 90f));

            Assert.AreSame(page.gameObject, swipe.pointerDrag);
            Assert.AreEqual(80f, page.content.anchoredPosition.y, 0.01f,
                "the page follows the finger from the drag threshold on");
            Assert.AreEqual(0f, strip.content.anchoredPosition.x, 0.01f);
        }

        [Test]
        public void Strip_KeepsASidewaysDrag()
        {
            var page = Page();
            var strip = Strip(page.content, 1200f);
            var at = Centre(strip);

            var drag = Press(strip.gameObject, at);
            MoveTo(drag, at + new Vector2(-DragThreshold, 4f));
            BeginDrag(drag);
            MoveTo(drag, at + new Vector2(-110f, 30f));
            Drag(drag);
            Release(drag);

            Assert.AreSame(strip.gameObject, drag.pointerDrag);
            Assert.AreEqual(-100f, strip.content.anchoredPosition.x, 0.01f, "the strip follows the finger sideways");
            Assert.AreEqual(0f, page.content.anchoredPosition.y, 0.01f, "the page stays put");
        }

        [Test]
        public void Strip_OutsideAPageKeepsEveryDrag()
        {
            var strip = Strip(_root.transform, 1200f);

            var swipe = Swipe(strip.gameObject, Centre(strip), new Vector2(0f, 90f));

            Assert.AreSame(strip.gameObject, swipe.pointerDrag, "with no page around it the strip is a plain ScrollRect");
            Assert.AreEqual(0f, strip.content.anchoredPosition.x, 0.01f);
        }

        [Test]
        public void Strip_LeavesAWheelTurnItCannotUseToThePage()
        {
            var page = Page();
            var strip = Strip(page.content, 200f);

            Wheel(strip.gameObject, new Vector2(0f, -1f));

            Assert.AreEqual(0f, strip.content.anchoredPosition.x, 0.01f);
            Assert.AreEqual(40f, page.content.anchoredPosition.y, 0.01f, "the strip cannot move, so the page scrolls");
        }

        [Test]
        public void Strip_KeepsAWheelTurnWhileItCanMove()
        {
            var page = Page();
            var strip = Strip(page.content, 1200f);

            Wheel(strip.gameObject, new Vector2(0f, 1f));

            Assert.AreEqual(-40f, strip.content.anchoredPosition.x, 0.01f);
            Assert.AreEqual(0f, page.content.anchoredPosition.y, 0.01f);
        }

        [Test]
        public void Slider_OnAPageWaitsForATapOrASidewaysDrag()
        {
            var page = Page();
            var values = new List<float>();
            var slider = Slider(page.content, values);
            var width = Width(slider);

            var tap = Press(slider.gameObject, SliderPoint(slider, 0.25f));
            Assert.IsEmpty(values, "a press on a page only arms the slider");
            Release(tap);
            Assert.AreEqual(1, values.Count, "a tap sets the value on release");
            Assert.AreEqual(slider.ValueAt(width * 0.25f), values[0], 1e-4f);

            values.Clear();
            var swipe = Swipe(slider.gameObject, SliderPoint(slider, 0.5f), new Vector2(0f, 90f));
            Assert.AreSame(page.gameObject, swipe.pointerDrag);
            Assert.AreEqual(80f, page.content.anchoredPosition.y, 0.01f, "a vertical swipe scrolls the page");
            Assert.IsEmpty(values, "and leaves the value alone");

            var at = SliderPoint(slider, 0.5f);
            var drag = Press(slider.gameObject, at);
            MoveTo(drag, at + new Vector2(DragThreshold, 3f));
            BeginDrag(drag);
            MoveTo(drag, SliderPoint(slider, 0.75f) + new Vector2(0f, 30f));
            Drag(drag);
            var count = values.Count;
            Release(drag);
            Assert.AreSame(slider.gameObject, drag.pointerDrag);
            Assert.AreEqual(slider.ValueAt(width * 0.75f), values[values.Count - 1], 1e-4f, "a sideways drag moves the value");
            Assert.AreEqual(count, values.Count, "the release after a drag is not another tap");
            Assert.AreEqual(80f, page.content.anchoredPosition.y, 0.01f, "and leaves the page alone");
        }

        [Test]
        public void Slider_OutsideAPageFollowsThePressAtOnce()
        {
            var values = new List<float>();
            var slider = Slider(_root.transform, values);
            var width = Width(slider);

            var press = Press(slider.gameObject, SliderPoint(slider, 0.25f));
            Assert.AreEqual(1, values.Count, "Godot's HSlider moves on the press");
            Assert.AreEqual(slider.ValueAt(width * 0.25f), values[0], 1e-4f);
            Release(press);
            Assert.AreEqual(1, values.Count, "the release is not a second tap");

            var swipe = Swipe(slider.gameObject, SliderPoint(slider, 0.5f), new Vector2(0f, 90f));
            Assert.AreSame(slider.gameObject, swipe.pointerDrag, "with no page to scroll the slider keeps the drag");
        }

        // ------------------------------------------------------------------
        // Layout.
        // ------------------------------------------------------------------

        /// <summary>A 400×300 vertical page at the origin with 1200 of content.</summary>
        private ScrollRect Page()
        {
            var view = Node("page", _root.transform);
            view.sizeDelta = new Vector2(400f, 300f);
            var content = Node("content", view);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = new Vector2(0f, 1200f);
            content.anchoredPosition = Vector2.zero;
            var scroll = view.gameObject.AddComponent<ScrollRect>();
            Configure(scroll, view, content, false);
            return scroll;
        }

        /// <summary>
        /// A 132-tall horizontal strip across the top of
        /// <paramref name="parent"/>, 40 below its edge, with cards
        /// <paramref name="width"/> wide.
        /// </summary>
        private static NestedScrollRect Strip(Transform parent, float width)
        {
            var view = Node("strip", parent);
            view.anchorMin = new Vector2(0f, 1f);
            view.anchorMax = new Vector2(1f, 1f);
            view.pivot = new Vector2(0.5f, 1f);
            view.sizeDelta = new Vector2(0f, 132f);
            view.anchoredPosition = new Vector2(0f, -40f);
            var content = Node("cards", view);
            content.anchorMin = new Vector2(0f, 0f);
            content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, 0.5f);
            content.sizeDelta = new Vector2(width, 0f);
            content.anchoredPosition = Vector2.zero;
            var scroll = view.gameObject.AddComponent<NestedScrollRect>();
            Configure(scroll, view, content, true);
            return scroll;
        }

        private static UiValueSlider Slider(Transform parent, List<float> values)
        {
            var rect = Node("slider", parent);
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(300f, 44f);
            rect.anchoredPosition = new Vector2(0f, -200f);
            var slider = rect.gameObject.AddComponent<UiValueSlider>();
            slider.Configure(0f, 100f, 1f, 44f, values.Add);
            return slider;
        }

        private static void Configure(ScrollRect scroll, RectTransform view, RectTransform content, bool horizontal)
        {
            scroll.viewport = view;
            scroll.content = content;
            scroll.horizontal = horizontal;
            scroll.vertical = !horizontal;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = false;
            scroll.scrollSensitivity = 40f;
        }

        private static RectTransform Node(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>Screen point of a control's centre (no camera: world = screen).</summary>
        private static Vector2 Centre(Component control)
        {
            var rect = (RectTransform)control.transform;
            return rect.TransformPoint(rect.rect.center);
        }

        private static Vector2 SliderPoint(UiValueSlider slider, float ratio)
        {
            var rect = (RectTransform)slider.transform;
            var local = rect.rect;
            return rect.TransformPoint(new Vector3(local.xMin + (local.width * ratio), local.center.y, 0f));
        }

        private static float Width(UiValueSlider slider)
        {
            return ((RectTransform)slider.transform).rect.width;
        }

        // ------------------------------------------------------------------
        // Input module stand-in.
        // ------------------------------------------------------------------

        private static PointerEventData Press(GameObject target, Vector2 at)
        {
            var e = new PointerEventData(null)
            {
                button = PointerEventData.InputButton.Left,
                position = at,
                pressPosition = at,
                useDragThreshold = true,
                eligibleForClick = true,
            };
            e.pointerPress = Handler<IPointerDownHandler>(target);
            Execute<IPointerDownHandler>(e.pointerPress, h => h.OnPointerDown(e));
            e.pointerDrag = Handler<IDragHandler>(target);
            Execute<IInitializePotentialDragHandler>(e.pointerDrag, h => h.OnInitializePotentialDrag(e));
            return e;
        }

        private static void MoveTo(PointerEventData e, Vector2 at)
        {
            e.delta = at - e.position;
            e.position = at;
        }

        private static void BeginDrag(PointerEventData e)
        {
            Execute<IBeginDragHandler>(e.pointerDrag, h => h.OnBeginDrag(e));
            e.dragging = true;
            if (e.pointerPress != e.pointerDrag)
            {
                Execute<IPointerUpHandler>(e.pointerPress, h => h.OnPointerUp(e));
                e.eligibleForClick = false;
                e.pointerPress = null;
            }
        }

        private static void Drag(PointerEventData e)
        {
            Execute<IDragHandler>(e.pointerDrag, h => h.OnDrag(e));
        }

        private static void Release(PointerEventData e)
        {
            Execute<IPointerUpHandler>(e.pointerPress, h => h.OnPointerUp(e));
            if (e.eligibleForClick)
            {
                Execute<IPointerClickHandler>(e.pointerPress, h => h.OnPointerClick(e));
            }

            if (e.dragging)
            {
                Execute<IEndDragHandler>(e.pointerDrag, h => h.OnEndDrag(e));
            }

            e.dragging = false;
            e.eligibleForClick = false;
        }

        /// <summary>Press, cross the threshold towards the travel, then drag the rest and let go.</summary>
        private static PointerEventData Swipe(GameObject target, Vector2 from, Vector2 travel)
        {
            var e = Press(target, from);
            MoveTo(e, from + (travel.normalized * DragThreshold));
            BeginDrag(e);
            MoveTo(e, from + travel);
            Drag(e);
            Release(e);
            return e;
        }

        private static void Wheel(GameObject target, Vector2 delta)
        {
            var e = new PointerEventData(null) { scrollDelta = delta };
            Execute<IScrollHandler>(Handler<IScrollHandler>(target), h => h.OnScroll(e));
        }

        /// <summary>
        /// ExecuteEvents.GetEventHandler without the isActiveAndEnabled filter:
        /// plain behaviours never get OnEnable in edit mode.
        /// </summary>
        private static GameObject Handler<T>(GameObject target) where T : class, IEventSystemHandler
        {
            for (var node = target == null ? null : target.transform; node != null; node = node.parent)
            {
                foreach (var behaviour in node.GetComponents<MonoBehaviour>())
                {
                    if (behaviour is T)
                    {
                        return node.gameObject;
                    }
                }
            }

            return null;
        }

        private static void Execute<T>(GameObject target, Action<T> call) where T : class, IEventSystemHandler
        {
            if (target == null)
            {
                return;
            }

            foreach (var behaviour in target.GetComponents<MonoBehaviour>())
            {
                var handler = behaviour as T;
                if (handler != null)
                {
                    call(handler);
                }
            }
        }
    }
}
