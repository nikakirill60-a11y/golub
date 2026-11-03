using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Telemetry
{
    /// <summary>
    /// The diagnostics preference, ported from Godot's
    /// <c>user://diagnostics.cfg</c> onto the existing save seam. Like the
    /// source client it collects until a player says otherwise, and a damaged
    /// or half-written file counts as "no": a preference that cannot be read
    /// must never silently opt a player who had turned collection off back in.
    /// </summary>
    public sealed class TelemetryPreferences
    {
        private readonly ISaveStorage _storage;
        private bool _loaded;
        private bool _enabled;

        public TelemetryPreferences(ISaveStorage storage)
        {
            _storage = storage;
        }

        /// <summary>
        /// No file yet means the player has never been asked, which the source
        /// client treats as collecting; an existing file is obeyed exactly.
        /// </summary>
        public bool Enabled
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _enabled = _storage == null
                        || !_storage.Exists(TelemetryPolicy.PreferencePath)
                        || ReadEnabled(_storage.ReadAllText(TelemetryPolicy.PreferencePath));
                }

                return _enabled;
            }
        }

        public void Set(bool value)
        {
            if (_storage != null)
            {
                _storage.WriteAllText(TelemetryPolicy.PreferencePath, Serialize(value));
            }

            _loaded = true;
            _enabled = value;
        }

        /// <summary>
        /// Mirrors the <c>[privacy] diagnostics_enabled</c> pair of the Godot
        /// config file, so a player who moves between the two clients keeps the
        /// same choice in a recognizable shape.
        /// </summary>
        public static bool ReadEnabled(string text)
        {
            JsonNode node;
            if (!JsonNode.TryParse(text, out node) || node == null)
            {
                return false;
            }

            var privacy = node.Get("privacy");
            if (privacy == null || privacy.IsNull)
            {
                return false;
            }

            var flag = privacy.Get("diagnostics_enabled");
            return flag != null && flag.Kind == JsonKind.Bool && flag.AsBool();
        }

        public static string Serialize(bool enabled)
        {
            var privacy = JsonNode.NewObject();
            privacy.Set("diagnostics_enabled", enabled);
            var root = JsonNode.NewObject();
            root.Set("privacy", privacy);
            return root.ToJson();
        }
    }
}
