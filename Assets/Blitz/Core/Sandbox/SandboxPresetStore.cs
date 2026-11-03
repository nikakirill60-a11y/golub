using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Sandbox
{
    /// <summary>
    /// Persistence of the Sandbox presets: Godot keeps them in their own
    /// <c>user://save/training_presets.tres</c>, apart from settings and
    /// accounts, so a Sandbox config can never leak into a progression save.
    /// </summary>
    public sealed class SandboxPresetStore
    {
        public const string PresetsPath = SaveService.SaveDirectory + "/training_presets.sav";

        private readonly ISaveStorage _storage;

        public SandboxPresetStore(ISaveStorage storage)
        {
            if (storage == null)
            {
                throw new ArgumentNullException("storage");
            }

            _storage = storage;
        }

        /// <summary>
        /// Loads and validates the library; a missing or corrupt file starts
        /// with one default preset. The repaired library is written back, as
        /// Godot does in <c>_load_training_presets</c>.
        /// </summary>
        public SandboxPresetLibrary Load(string defaultName, ICollection<string> mapIds, TanksDatabase tanks)
        {
            SandboxPresetLibrary library = null;
            if (_storage.Exists(PresetsPath))
            {
                JsonNode node;
                if (JsonNode.TryParse(_storage.ReadAllText(PresetsPath), out node))
                {
                    library = SandboxPresetLibrary.FromJson(node);
                }
            }

            if (library == null)
            {
                library = new SandboxPresetLibrary();
            }

            if (library.Count == 0)
            {
                library.Add(defaultName);
            }

            SandboxValidation.Validate(library, defaultName, mapIds, tanks);
            Save(library);
            return library;
        }

        public void Save(SandboxPresetLibrary library)
        {
            if (library == null)
            {
                throw new ArgumentNullException("library");
            }

            _storage.WriteAllText(PresetsPath, library.ToJson().ToJson());
        }
    }

    /// <summary>
    /// Global._validate_training_config_value: numeric clamps plus the tank
    /// catalogue checks a config alone cannot do.
    /// </summary>
    public static class SandboxValidation
    {
        public static bool Validate(SandboxPresetLibrary library, string defaultName,
            ICollection<string> mapIds, TanksDatabase tanks)
        {
            if (library == null)
            {
                throw new ArgumentNullException("library");
            }

            var changed = library.Validate(defaultName, mapIds);
            for (var i = 0; i < library.Presets.Count; i++)
            {
                changed = Validate(library.Presets[i].Config, mapIds, tanks) || changed;
            }

            return changed;
        }

        /// <summary>
        /// Clamps the config, turns slots whose tank no longer exists into
        /// random seats and clamps module indices to the selected tank.
        /// </summary>
        public static bool Validate(SandboxConfig config, ICollection<string> mapIds, TanksDatabase tanks)
        {
            if (config == null)
            {
                return false;
            }

            var changed = config.Validate(mapIds);
            if (tanks == null)
            {
                return changed;
            }

            changed = ValidateSlots(config.AlliedSlots, tanks) || changed;
            return ValidateSlots(config.EnemySlots, tanks) || changed;
        }

        private static bool ValidateSlots(List<SandboxBotSlot> slots, TanksDatabase tanks)
        {
            var changed = false;
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot.RandomTank || string.IsNullOrEmpty(slot.TankName))
                {
                    continue;
                }

                var info = tanks.Get(slot.TankName);
                if (info == null)
                {
                    slot.MakeRandom();
                    changed = true;
                    continue;
                }

                changed = slot.ValidateModules(info.Guns.Count, info.Engines.Count, info.Tracks.Count) || changed;
            }

            return changed;
        }
    }
}
