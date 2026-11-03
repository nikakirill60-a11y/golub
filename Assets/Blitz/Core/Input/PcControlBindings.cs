using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Input
{
    /// <summary>
    /// Port of assets/data/battle_input_bindings.gd.
    ///
    /// The source keeps one editable slot per action (MAX_SLOTS = 1), lists the
    /// actions the controls tab shows grouped by category, and stores the
    /// player's overrides in save_settings.pc_control_bindings. Godot records an
    /// InputEvent; the port has no InputMap, so a binding is a token instead:
    /// a UnityEngine.InputSystem.Key name ("W", "LeftShift", "Digit1") or one of
    /// the mouse tokens below. The runtime side resolves the token, which keeps
    /// this model free of any engine reference and unit testable.
    /// </summary>
    public sealed class PcControlBindings
    {
        /// <summary>battle_input_bindings.gd: const MAX_SLOTS = 1.</summary>
        public const int MaxSlots = 1;

        public const string MouseLeft = "mouse_left";
        public const string MouseRight = "mouse_right";
        public const string MouseMiddle = "mouse_middle";
        public const string WheelUp = "wheel_up";
        public const string WheelDown = "wheel_down";

        // The action ids, as the source names them.
        public const string MoveForward = "move_fw";
        public const string MoveBack = "move_back";
        public const string MoveLeft = "move_left";
        public const string MoveRight = "move_right";
        public const string Attack = "attack1";
        public const string BlockTurret = "block_turret";
        public const string SniperMode = "sniper_mode";
        public const string ReloadBurst = "reload_burst";
        public const string ZoomIn = "zoom_in";
        public const string ZoomOut = "zoom_out";
        public const string Shell1 = "shell1";
        public const string Shell2 = "shell2";
        public const string Shell3 = "shell3";
        public const string Consumable1 = "consumable1";
        public const string Consumable2 = "consumable2";
        public const string Consumable3 = "consumable3";
        public const string ModeAbility = "mode_ability";
        public const string Scoreboard = "scoreboard";
        public const string Menu = "menu";
        public const string Fullscreen = "fullscreen";

        /// <summary>One entry of the source's ACTIONS array.</summary>
        public sealed class ActionDefinition
        {
            public ActionDefinition(string category, string action, string defaultToken)
            {
                CategoryKey = category;
                Action = action;
                DefaultToken = defaultToken;
            }

            public string CategoryKey { get; private set; }

            public string Action { get; private set; }

            /// <summary>The event project.godot ships for the action.</summary>
            public string DefaultToken { get; private set; }

            /// <summary>label_key_for_action: "controls:action/&lt;action&gt;".</summary>
            public string LabelKey
            {
                get { return "controls:action/" + Action; }
            }
        }

        /// <summary>ACTIONS, in source order; the categories group the rows.</summary>
        public static readonly IList<ActionDefinition> Definitions =
            new List<ActionDefinition>
            {
                new ActionDefinition("controls:category/movement", MoveForward, "W"),
                new ActionDefinition("controls:category/movement", MoveBack, "S"),
                new ActionDefinition("controls:category/movement", MoveLeft, "A"),
                new ActionDefinition("controls:category/movement", MoveRight, "D"),
                new ActionDefinition("controls:category/combat", Attack, MouseLeft),
                new ActionDefinition("controls:category/combat", BlockTurret, MouseRight),
                new ActionDefinition("controls:category/combat", SniperMode, "LeftShift"),
                new ActionDefinition("controls:category/combat", ReloadBurst, "C"),
                new ActionDefinition("controls:category/camera", ZoomIn, WheelUp),
                new ActionDefinition("controls:category/camera", ZoomOut, WheelDown),
                new ActionDefinition("controls:category/ammo", Shell1, "Digit1"),
                new ActionDefinition("controls:category/ammo", Shell2, "Digit2"),
                new ActionDefinition("controls:category/ammo", Shell3, "Digit3"),
                new ActionDefinition(
                    "controls:category/equipment", Consumable1, "Digit4"),
                new ActionDefinition(
                    "controls:category/equipment", Consumable2, "Digit5"),
                new ActionDefinition(
                    "controls:category/equipment", Consumable3, "Digit6"),
                new ActionDefinition(
                    "controls:category/equipment", ModeAbility, "Digit7"),
                new ActionDefinition("controls:category/interface", Scoreboard, "Tab"),
                new ActionDefinition("controls:category/interface", Menu, "Escape"),
                new ActionDefinition("controls:category/interface", Fullscreen, "F11"),
            };

        /// <summary>The overrides, i.e. what export_current() would write.</summary>
        private readonly Dictionary<string, string> _bindings =
            new Dictionary<string, string>();

        /// <summary>The actions that were cleared, which is not the same as unset.</summary>
        private readonly HashSet<string> _cleared = new HashSet<string>();

        public static ActionDefinition Find(string action)
        {
            for (var i = 0; i < Definitions.Count; i++)
            {
                if (Definitions[i].Action == action)
                {
                    return Definitions[i];
                }
            }

            return null;
        }

        /// <summary>The token an action currently answers to, or null.</summary>
        public string Get(string action)
        {
            string token;
            if (_bindings.TryGetValue(action, out token))
            {
                return token;
            }

            if (_cleared.Contains(action))
            {
                return null;
            }

            var definition = Find(action);
            return definition == null ? null : definition.DefaultToken;
        }

        /// <summary>find_conflict: the action already holding this input.</summary>
        public string FindConflict(string token, string exceptAction)
        {
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            for (var i = 0; i < Definitions.Count; i++)
            {
                var action = Definitions[i].Action;
                if (action == exceptAction)
                {
                    continue;
                }

                if (Get(action) == token)
                {
                    return action;
                }
            }

            return null;
        }

        /// <summary>
        /// set_binding: a physical input belongs to exactly one action, so the
        /// source strips it off every other action instead of refusing the
        /// rebind. Returns the action the input was taken from, if any.
        /// </summary>
        public string SetBinding(string action, string token)
        {
            if (Find(action) == null || string.IsNullOrEmpty(token))
            {
                return null;
            }

            var conflict = FindConflict(token, action);
            if (conflict != null)
            {
                ClearBinding(conflict);
            }

            _cleared.Remove(action);
            _bindings[action] = token;
            return conflict;
        }

        /// <summary>clear_binding: the action keeps no event at all.</summary>
        public void ClearBinding(string action)
        {
            if (Find(action) == null)
            {
                return;
            }

            _bindings.Remove(action);
            _cleared.Add(action);
        }

        /// <summary>reset_to_defaults, i.e. apply_saved({}).</summary>
        public void ResetToDefaults()
        {
            _bindings.Clear();
            _cleared.Clear();
        }

        public bool IsDefault
        {
            get { return _bindings.Count == 0 && _cleared.Count == 0; }
        }

        /// <summary>
        /// event_text: the mouse buttons are localized, a key shows the label
        /// Godot prints for it. Returns null when the action is unbound.
        /// </summary>
        public static string TextKey(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            switch (token)
            {
                case MouseLeft:
                case MouseRight:
                case MouseMiddle:
                case WheelUp:
                case WheelDown:
                    return "controls:" + token;
                default:
                    return null;
            }
        }

        /// <summary>The text a key token shows when it is not localized.</summary>
        public static string KeyText(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return string.Empty;
            }

            // Key.Digit1 prints as "1", which is what the source's as_text()
            // gives for the same physical key.
            if (token.Length == 6 && token.StartsWith("Digit"))
            {
                return token.Substring(5);
            }

            return token;
        }

        /// <summary>export_current: only the overrides are written.</summary>
        public JsonNode ToJson()
        {
            var node = JsonNode.NewObject();
            for (var i = 0; i < Definitions.Count; i++)
            {
                var action = Definitions[i].Action;
                if (_bindings.ContainsKey(action))
                {
                    node.Set(action, _bindings[action]);
                }
                else if (_cleared.Contains(action))
                {
                    node.Set(action, string.Empty);
                }
            }

            return node;
        }

        /// <summary>apply_saved: an unknown action falls back to its default.</summary>
        public static PcControlBindings FromJson(JsonNode node)
        {
            var bindings = new PcControlBindings();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return bindings;
            }

            for (var i = 0; i < Definitions.Count; i++)
            {
                var action = Definitions[i].Action;
                var value = node[action];
                if (value.Kind != JsonKind.String)
                {
                    continue;
                }

                var token = value.AsString();
                if (string.IsNullOrEmpty(token))
                {
                    bindings.ClearBinding(action);
                }
                else
                {
                    bindings.SetBinding(action, token);
                }
            }

            return bindings;
        }
    }
}
