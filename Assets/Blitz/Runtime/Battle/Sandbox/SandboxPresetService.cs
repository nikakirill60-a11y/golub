using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Sandbox;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Mods;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Global.training_preset_library plus the map and tank catalogues the
    /// Sandbox validates against. The library lives in its own save file,
    /// separate from the account and the settings, so nothing the player sets
    /// up here can leak into progression.
    /// </summary>
    public sealed class SandboxPresetService
    {
        /// <summary>Localization key of the first preset's name.</summary>
        public const string DefaultNameKey = "training:preset/default";

        private readonly SandboxPresetStore _store;
        private readonly string _defaultName;

        public SandboxPresetLibrary Library { get; private set; }

        /// <summary>Every map the Sandbox may pick, in catalogue order.</summary>
        public IReadOnlyList<MapDefinition> Maps { get; private set; }

        /// <summary>Ids of <see cref="Maps"/>.</summary>
        public ReadOnlyCollection<string> MapIds { get; private set; }

        /// <summary>The tank table, with installed mods applied.</summary>
        public TanksDatabase Tanks { get; private set; }

        private SandboxPresetService(ISaveStorage storage, string defaultName,
            IReadOnlyList<MapDefinition> maps, TanksDatabase tanks)
        {
            _store = new SandboxPresetStore(storage);
            _defaultName = string.IsNullOrEmpty(defaultName) ? "Default" : defaultName;
            Maps = maps;
            var ids = new List<string>(maps.Count);
            for (var i = 0; i < maps.Count; i++) { ids.Add(maps[i].Id); }
            MapIds = ids.AsReadOnly();
            Tanks = tanks;
            Library = _store.Load(_defaultName, ids, tanks);
        }

        /// <summary>The preset the next Sandbox battle plays.</summary>
        public SandboxConfig Active { get { return Library.ActiveConfig; } }

        /// <summary>Name given to an unnamed or reset preset.</summary>
        public string DefaultName { get { return _defaultName; } }

        /// <summary>Loads (and repairs) the library from the player's save storage.</summary>
        public static SandboxPresetService Load(ISaveStorage storage, LocalizationTable lang)
        {
            if (storage == null) { throw new ArgumentNullException("storage"); }
            var tanks = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            ModRuntime.Apply(tanks);
            var name = lang != null ? lang.Get(DefaultNameKey) : DefaultNameKey;
            return new SandboxPresetService(storage, name, BattleMapCatalog.SandboxMaps(), tanks);
        }

        /// <summary>For tests: explicit catalogues, no resources.</summary>
        public static SandboxPresetService Create(ISaveStorage storage, string defaultName,
            IReadOnlyList<MapDefinition> maps, TanksDatabase tanks)
        {
            if (storage == null) { throw new ArgumentNullException("storage"); }
            return new SandboxPresetService(storage, defaultName,
                maps ?? new List<MapDefinition>(), tanks ?? new TanksDatabase());
        }

        /// <summary>Validates everything again and writes the library back.</summary>
        public void Save()
        {
            SandboxValidation.Validate(Library, _defaultName, MapIds, Tanks);
            _store.Save(Library);
        }

        /// <summary>The chosen map, or a random one (Godot Map.get_random()) when RANDOM is picked.</summary>
        public string PickMap(SandboxConfig config, IRandomSource random)
        {
            if (config != null && !config.MapIsRandom && Contains(config.SelectedMapId))
            {
                return config.SelectedMapId;
            }
            if (MapIds.Count == 0) { return BattleWorld.DefaultMapId; }
            var index = random == null ? 0 : random.RangeInclusive(0, MapIds.Count - 1);
            return MapIds[index];
        }

        public bool Contains(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) { return false; }
            for (var i = 0; i < MapIds.Count; i++)
            {
                if (string.Equals(MapIds[i], mapId, StringComparison.Ordinal)) { return true; }
            }
            return false;
        }

        public MapDefinition Map(string mapId)
        {
            for (var i = 0; i < Maps.Count; i++)
            {
                if (string.Equals(Maps[i].Id, mapId, StringComparison.Ordinal)) { return Maps[i]; }
            }
            return null;
        }

        /// <summary>Loading-screen art of a map, used as its card (Godot card_texture = splash_texture).</summary>
        public static Texture2D CardTexture(MapDefinition map)
        {
            return map == null || string.IsNullOrEmpty(map.LoadingScreen)
                ? null : Resources.Load<Texture2D>(map.LoadingScreen);
        }
    }
}
