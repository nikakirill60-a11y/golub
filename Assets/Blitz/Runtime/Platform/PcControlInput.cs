using System;
using BlitzLegacy.Core.Input;
using BlitzLegacy.Core.Localization;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace BlitzLegacy.Runtime.Platform
{
    /// <summary>
    /// The engine half of <see cref="PcControlBindings"/>.
    ///
    /// Godot resolves an action through the InputMap, which holds real
    /// InputEvents; the port stores a token instead and resolves it here, so
    /// the binding model itself stays engine free. A token is either a
    /// UnityEngine.InputSystem.Key name or one of the mouse tokens the model
    /// declares, which is exactly the set battle_input_bindings.is_supported_event
    /// accepts (InputEventKey and InputEventMouseButton).
    /// </summary>
    public static class PcControlInput
    {
        /// <summary>
        /// True while the battle chat input owns the keyboard: every battle
        /// action reads as released so typing never drives the tank.
        /// </summary>
        public static bool Suppressed { get; set; }

        /// <summary>Input.is_action_pressed for the action's current binding.</summary>
        public static bool Held(PcControlBindings bindings, string action)
        {
            return TokenHeld(Token(bindings, action));
        }

        /// <summary>Input.is_action_just_pressed.</summary>
        public static bool Pressed(PcControlBindings bindings, string action)
        {
            return TokenPressed(Token(bindings, action));
        }

        public static string Token(PcControlBindings bindings, string action)
        {
            if (bindings != null)
            {
                return bindings.Get(action);
            }

            var definition = PcControlBindings.Find(action);
            return definition == null ? null : definition.DefaultToken;
        }

        public static bool TokenHeld(string token)
        {
            if (Suppressed || string.IsNullOrEmpty(token))
            {
                return false;
            }

            var mouse = Mouse.current;
            switch (token)
            {
                case PcControlBindings.MouseLeft:
                    return mouse != null && mouse.leftButton.isPressed;
                case PcControlBindings.MouseRight:
                    return mouse != null && mouse.rightButton.isPressed;
                case PcControlBindings.MouseMiddle:
                    return mouse != null && mouse.middleButton.isPressed;
                case PcControlBindings.WheelUp:
                    return WheelDelta() > 0f;
                case PcControlBindings.WheelDown:
                    return WheelDelta() < 0f;
            }

            var control = KeyControl(token);
            return control != null && control.isPressed;
        }

        public static bool TokenPressed(string token)
        {
            if (Suppressed || string.IsNullOrEmpty(token))
            {
                return false;
            }

            var mouse = Mouse.current;
            switch (token)
            {
                case PcControlBindings.MouseLeft:
                    return mouse != null && mouse.leftButton.wasPressedThisFrame;
                case PcControlBindings.MouseRight:
                    return mouse != null && mouse.rightButton.wasPressedThisFrame;
                case PcControlBindings.MouseMiddle:
                    return mouse != null && mouse.middleButton.wasPressedThisFrame;
                case PcControlBindings.WheelUp:
                    return WheelDelta() > 0f;
                case PcControlBindings.WheelDown:
                    return WheelDelta() < 0f;
            }

            var control = KeyControl(token);
            return control != null && control.wasPressedThisFrame;
        }

        /// <summary>
        /// pc_controls_settings._input: the first supported event ends the
        /// capture. Returns the token, or null while nothing was pressed.
        /// </summary>
        public static string Capture()
        {
            var mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    return PcControlBindings.MouseLeft;
                }

                if (mouse.rightButton.wasPressedThisFrame)
                {
                    return PcControlBindings.MouseRight;
                }

                if (mouse.middleButton.wasPressedThisFrame)
                {
                    return PcControlBindings.MouseMiddle;
                }

                var scroll = WheelDelta();
                if (scroll > 0f)
                {
                    return PcControlBindings.WheelUp;
                }

                if (scroll < 0f)
                {
                    return PcControlBindings.WheelDown;
                }
            }

            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return null;
            }

            foreach (var control in keyboard.allKeys)
            {
                // The source ignores echoes; wasPressedThisFrame is the edge.
                if (control.wasPressedThisFrame)
                {
                    return control.keyCode.ToString();
                }
            }

            return null;
        }

        /// <summary>event_text: the label the controls tab prints on a slot.</summary>
        public static string Text(LocalizationTable lang, string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return lang == null ? string.Empty : lang.Get("controls:unbound");
            }

            var key = PcControlBindings.TextKey(token);
            if (key != null)
            {
                return lang == null ? key : lang.Get(key);
            }

            return PcControlBindings.KeyText(token);
        }

        private static float WheelDelta()
        {
            var mouse = Mouse.current;
            return mouse == null ? 0f : mouse.scroll.ReadValue().y;
        }

        private static KeyControl KeyControl(string token)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return null;
            }

            Key key;
            if (!Enum.TryParse(token, false, out key) || key == Key.None)
            {
                return null;
            }

            return keyboard[key];
        }
    }
}
