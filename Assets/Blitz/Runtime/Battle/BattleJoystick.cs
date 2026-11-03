using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The on screen stick from ui_player.tscn, ported from
    /// addons/touch_screen_joystick/touch_screen_joystick.gd.
    ///
    /// The source arbitrates fingers itself: the first finger that lands inside
    /// the rect becomes the owner (event_index), drags from any other finger are
    /// ignored, and only the owner can release the knob. Unity's event system
    /// already routes a pointer to a single object, so the ownership check here
    /// only has to reject a second finger landing on the same widget.
    ///
    /// The knob math itself lives in <see cref="ControlsTuning"/> so it can be
    /// tested without a canvas.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class BattleJoystick : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        /// <summary>
        /// Unity numbers the mouse buttons -1..-3, so "nobody owns the stick"
        /// cannot be spelled -1 the way the Godot addon does.
        /// </summary>
        private const int NoPointer = int.MinValue;

        private RectTransform _rect;
        private RectTransform _knob;
        private int _pointer = NoPointer;

        public Image Background { get; private set; }

        public Image KnobImage { get; private set; }

        /// <summary>The move_fw / move_back axis as BaseTank.drive reads it.</summary>
        public float Forward { get; private set; }

        /// <summary>The move_right / move_left axis.</summary>
        public float Right { get; private set; }

        /// <summary>Whether the finger is past the deadzone, i.e. actions are pressed.</summary>
        public bool Engaged { get; private set; }

        /// <summary>Knob position in the control's own unscaled space, Y down.</summary>
        public Vector2 KnobPoint { get; private set; }

        public bool Pressed { get { return _pointer != NoPointer; } }

        /// <summary>touch_screen_joystick.gd works against size / 2.0.</summary>
        public Vector2 Center { get { return Rect.rect.size * 0.5f; } }

        private RectTransform Rect
        {
            get
            {
                if (_rect == null)
                {
                    _rect = (RectTransform)transform;
                }

                return _rect;
            }
        }

        public static BattleJoystick Create(
            RectTransform parent, Sprite backgroundSprite = null, Sprite knobSprite = null)
        {
            var go = new GameObject("VirtualJoystick", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;

            // anchors_preset 2 is the bottom left corner, so both vertical
            // anchors sit at 1 and the offsets are negative upwards.
            //
            // The source node carries scale = 2.625 and no pivot_offset, so
            // Godot grows it right and down from its top left corner. A centre
            // pivot would spread the same growth in every direction and push
            // the base off the left edge of the screen, which is what the
            // device run showed.
            GodotRect.Apply(
                rect, 0f, 1f, 0f, 1f,
                ControlsTuning.JoystickOffsetLeft,
                ControlsTuning.JoystickOffsetTop,
                ControlsTuning.JoystickOffsetRight,
                ControlsTuning.JoystickOffsetBottom,
                GodotRect.TopLeftPivot);
            rect.localScale = new Vector3(
                ControlsTuning.JoystickScale, ControlsTuning.JoystickScale, 1f);

            var background = go.AddComponent<Image>();
            background.sprite = backgroundSprite;
            background.type = Image.Type.Simple;
            background.color = backgroundSprite == null
                ? new Color(1f, 1f, 1f, 0.16f)
                : Color.white;

            var knobGo = new GameObject("tip", typeof(RectTransform));
            knobGo.transform.SetParent(rect, false);

            var knob = (RectTransform)knobGo.transform;
            knob.anchorMin = new Vector2(0.5f, 0.5f);
            knob.anchorMax = new Vector2(0.5f, 0.5f);
            knob.pivot = new Vector2(0.5f, 0.5f);
            knob.sizeDelta = new Vector2(
                ControlsTuning.JoystickKnobRadius * 2f,
                ControlsTuning.JoystickKnobRadius * 2f);
            knob.anchoredPosition = Vector2.zero;

            var knobImage = knobGo.AddComponent<Image>();
            knobImage.sprite = knobSprite;
            knobImage.type = Image.Type.Simple;
            knobImage.color = knobSprite == null
                ? new Color(1f, 1f, 1f, 0.34f)
                : Color.white;

            // Only the base takes the touch, exactly like the single Control in
            // the source scene.
            knobImage.raycastTarget = false;

            var joystick = go.AddComponent<BattleJoystick>();
            joystick._knob = knob;
            joystick.Background = background;
            joystick.KnobImage = knobImage;
            joystick.Release();
            return joystick;
        }

        /// <summary>touch_knob: the owning finger goes down.</summary>
        public void Press(Vector2 touch)
        {
            ApplyTouch(touch);
        }

        /// <summary>move_knob, driven by on_screen_drag.</summary>
        public void MoveTo(Vector2 touch)
        {
            ApplyTouch(touch);
        }

        /// <summary>release_knob: reset_actions, reset_knob, drop the owner.</summary>
        public void Release()
        {
            _pointer = NoPointer;
            Forward = 0f;
            Right = 0f;
            Engaged = false;
            KnobPoint = Center;
            UpdateKnob();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_pointer != NoPointer)
            {
                return;
            }

            _pointer = eventData.pointerId;
            Press(ToControlPoint(eventData));
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.pointerId != _pointer)
            {
                return;
            }

            MoveTo(ToControlPoint(eventData));
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.pointerId != _pointer)
            {
                return;
            }

            Release();
        }

        /// <summary>
        /// ui_player_battle.gd calls _reset_values whenever the mobile layer is
        /// hidden, so a stick that disappears mid drive must not keep driving.
        /// </summary>
        private void OnDisable()
        {
            Release();
        }

        private void ApplyTouch(Vector2 touch)
        {
            var center = Center;

            float forward;
            float right;
            ControlsTuning.DriveAxes(
                center.x, center.y, touch.x, touch.y, out forward, out right);
            Forward = forward;
            Right = right;

            Engaged = ControlsTuning.Engaged(
                ControlsTuning.Distance(center.x, center.y, touch.x, touch.y));

            float knobX;
            float knobY;
            ControlsTuning.KnobPosition(
                center.x,
                center.y,
                touch.x,
                touch.y,
                ControlsTuning.JoystickKnobTravel,
                out knobX,
                out knobY);
            KnobPoint = new Vector2(knobX, knobY);

            UpdateKnob();
        }

        /// <summary>
        /// move_knob divides the finger position by the node scale, which is
        /// what ScreenPointToLocalPointInRectangle already undoes, and then
        /// works from the top left corner with Y down.
        /// </summary>
        private Vector2 ToControlPoint(PointerEventData eventData)
        {
            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                Rect, eventData.position, eventData.pressEventCamera, out local);

            var size = Rect.rect.size;
            var pivot = Rect.pivot;
            return new Vector2(
                ControlsTuning.ControlPointX(local.x, size.x, pivot.x),
                ControlsTuning.ControlPointY(local.y, size.y, pivot.y));
        }

        private void UpdateKnob()
        {
            if (_knob == null)
            {
                return;
            }

            var center = Center;

            // Back to Unity's Y up, relative to the centre of the base.
            _knob.anchoredPosition = new Vector2(
                KnobPoint.x - center.x, center.y - KnobPoint.y);
        }
    }
}
