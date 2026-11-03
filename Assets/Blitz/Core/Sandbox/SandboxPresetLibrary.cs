using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Sandbox
{
    /// <summary>
    /// Port of assets/data/training_preset.gd: a named configuration. The name
    /// belongs to the preset rather than the config, so copies stay independent
    /// without leaking UI metadata into battle rules.
    /// </summary>
    public sealed class SandboxPreset
    {
        public string Id = string.Empty;
        public string DisplayName = string.Empty;
        public SandboxConfig Config;
    }

    /// <summary>Why <see cref="SandboxPresetLibrary.Import"/> refused a config code.</summary>
    public enum SandboxImportError { None, Limit, Format, Version, Config }

    /// <summary>
    /// Port of assets/data/training_preset_library.gd: persistent named Sandbox
    /// configurations with one active preset, the export code format and the
    /// importer. The export format is the Godot one, so codes are portable
    /// between both builds.
    /// </summary>
    public sealed class SandboxPresetLibrary
    {
        public const int MaxPresets = 24;
        public const int MaxNameLength = 40;
        public const string ExportFormat = "blitz-legacy-training-preset";
        public const int ExportVersion = 1;

        public string ActiveId = string.Empty;
        public int NextId = 1;
        public readonly List<SandboxPreset> Presets = new List<SandboxPreset>();

        public int Count
        {
            get { return Presets.Count; }
        }

        public bool IsFull
        {
            get { return Presets.Count >= MaxPresets; }
        }

        public string SanitizeName(string value, string fallback)
        {
            var clean = Collapse((value ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim());
            if (clean.Length == 0)
            {
                clean = (fallback ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
            }

            if (clean.Length == 0)
            {
                clean = Math.Max(1, NextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return clean.Length > MaxNameLength ? clean.Substring(0, MaxNameLength) : clean;
        }

        private static string Collapse(string value)
        {
            while (value.Contains("  "))
            {
                value = value.Replace("  ", " ");
            }

            return value;
        }

        public SandboxPreset Get(string id)
        {
            for (var i = 0; i < Presets.Count; i++)
            {
                if (Presets[i] != null && string.Equals(Presets[i].Id, id, StringComparison.Ordinal))
                {
                    return Presets[i];
                }
            }

            return null;
        }

        public int IndexOf(string id)
        {
            for (var i = 0; i < Presets.Count; i++)
            {
                if (Presets[i] != null && string.Equals(Presets[i].Id, id, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        public SandboxPreset Active
        {
            get
            {
                var preset = Get(ActiveId);
                if (preset == null && Presets.Count > 0)
                {
                    preset = Presets[0];
                    ActiveId = preset.Id;
                }

                return preset;
            }
        }

        public SandboxConfig ActiveConfig
        {
            get
            {
                var preset = Active;
                return preset == null ? null : preset.Config;
            }
        }

        private string NewId()
        {
            NextId = Math.Max(1, NextId);
            while (true)
            {
                var candidate = "preset_" + NextId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                NextId++;
                if (Get(candidate) == null)
                {
                    return candidate;
                }
            }
        }

        /// <summary>Adds a preset (a copy of <paramref name="source"/> or defaults) and activates it.</summary>
        public SandboxPreset Add(string displayName, SandboxConfig source = null)
        {
            if (Presets.Count >= MaxPresets)
            {
                return null;
            }

            var preset = new SandboxPreset
            {
                Id = NewId(),
                DisplayName = SanitizeName(displayName,
                    (Presets.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Config = source == null ? new SandboxConfig() : source.Copy(),
            };
            Presets.Add(preset);
            ActiveId = preset.Id;
            return preset;
        }

        public SandboxPreset Duplicate(string sourceId, string displayName)
        {
            var source = Get(sourceId);
            return source == null ? null : Add(displayName, source.Config);
        }

        public bool Select(string id)
        {
            if (Get(id) == null)
            {
                return false;
            }

            ActiveId = id;
            return true;
        }

        public bool Rename(string id, string displayName, string fallback)
        {
            var preset = Get(id);
            if (preset == null)
            {
                return false;
            }

            preset.DisplayName = SanitizeName(displayName, fallback);
            return true;
        }

        /// <summary>Removes a preset; the last one can never be removed.</summary>
        public bool Remove(string id)
        {
            if (Presets.Count <= 1)
            {
                return false;
            }

            for (var i = 0; i < Presets.Count; i++)
            {
                if (Presets[i] == null || !string.Equals(Presets[i].Id, id, StringComparison.Ordinal))
                {
                    continue;
                }

                Presets.RemoveAt(i);
                if (string.Equals(ActiveId, id, StringComparison.Ordinal))
                {
                    ActiveId = Presets[Math.Min(i, Presets.Count - 1)].Id;
                }

                return true;
            }

            return false;
        }

        public bool ReplaceActiveConfig(SandboxConfig value)
        {
            var preset = Active;
            if (preset == null || value == null)
            {
                return false;
            }

            preset.Config = value;
            return true;
        }

        public bool OverwriteActive(SandboxConfig source, ICollection<string> mapIds = null)
        {
            var preset = Active;
            if (preset == null || source == null)
            {
                return false;
            }

            preset.Config = source.Copy();
            preset.Config.Validate(mapIds);
            return true;
        }

        /// <summary>
        /// Drops broken entries, repairs ids, names and shared configs, validates
        /// every config and guarantees one active preset.
        /// </summary>
        public bool Validate(string defaultName, ICollection<string> mapIds = null)
        {
            var changed = false;
            NextId = Math.Max(1, NextId);
            while (Presets.Count > MaxPresets)
            {
                Presets.RemoveAt(Presets.Count - 1);
                changed = true;
            }

            var valid = new List<SandboxPreset>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenConfigs = new HashSet<SandboxConfig>();
            for (var i = 0; i < Presets.Count; i++)
            {
                var preset = Presets[i];
                if (preset == null)
                {
                    changed = true;
                    continue;
                }

                if (string.IsNullOrEmpty(preset.Id) || seenIds.Contains(preset.Id))
                {
                    preset.Id = NewId();
                    changed = true;
                }

                seenIds.Add(preset.Id);
                var cleanName = SanitizeName(preset.DisplayName, defaultName);
                if (!string.Equals(preset.DisplayName, cleanName, StringComparison.Ordinal))
                {
                    preset.DisplayName = cleanName;
                    changed = true;
                }

                if (preset.Config == null)
                {
                    preset.Config = new SandboxConfig();
                    changed = true;
                }
                else if (seenConfigs.Contains(preset.Config))
                {
                    preset.Config = preset.Config.Copy();
                    changed = true;
                }

                seenConfigs.Add(preset.Config);
                changed = preset.Config.Validate(mapIds) || changed;
                valid.Add(preset);
            }

            Presets.Clear();
            Presets.AddRange(valid);
            if (Presets.Count == 0)
            {
                Add(defaultName);
                changed = true;
            }

            if (Get(ActiveId) == null)
            {
                ActiveId = Presets[0].Id;
                changed = true;
            }

            return changed;
        }

        /// <summary>The shareable config code of the active preset (compact JSON).</summary>
        public string ExportActive()
        {
            var preset = Active;
            if (preset == null || preset.Config == null)
            {
                return string.Empty;
            }

            return JsonNode.NewObject()
                .Set("format", ExportFormat)
                .Set("version", ExportVersion)
                .Set("name", preset.DisplayName)
                .Set("config", preset.Config.ToExport())
                .ToJson(false);
        }

        /// <summary>True when <paramref name="text"/> looks like a config code (clipboard prefill).</summary>
        public static bool LooksLikeCode(string text)
        {
            return !string.IsNullOrEmpty(text) && text.Contains(ExportFormat);
        }

        /// <summary>
        /// Imports a config code as a new active preset. The created id is
        /// returned through <paramref name="id"/>.
        /// </summary>
        public SandboxImportError Import(string encoded, string fallbackName, ICollection<string> mapIds, out string id)
        {
            id = string.Empty;
            if (Presets.Count >= MaxPresets)
            {
                return SandboxImportError.Limit;
            }

            JsonNode decoded;
            if (!JsonNode.TryParse((encoded ?? string.Empty).Trim(), out decoded) || decoded.Kind != JsonKind.Object)
            {
                return SandboxImportError.Format;
            }

            if (SandboxJson.Text(decoded, "format", string.Empty) != ExportFormat)
            {
                return SandboxImportError.Format;
            }

            if (SandboxJson.Int(decoded, "version", -1) != ExportVersion)
            {
                return SandboxImportError.Version;
            }

            var configData = decoded["config"];
            if (configData.Kind != JsonKind.Object)
            {
                return SandboxImportError.Config;
            }

            var imported = SandboxConfig.FromExport(configData);
            imported.Validate(mapIds);
            var preset = Add(SandboxJson.Text(decoded, "name", fallbackName), imported);
            if (preset == null)
            {
                return SandboxImportError.Limit;
            }

            // Add() copies; keep the validated instance itself, it is not shared.
            preset.Config = imported;
            id = preset.Id;
            return SandboxImportError.None;
        }

        /// <summary>The on-disk save shape (not the share code).</summary>
        public JsonNode ToJson()
        {
            var list = JsonNode.NewArray();
            for (var i = 0; i < Presets.Count; i++)
            {
                var preset = Presets[i];
                if (preset == null)
                {
                    continue;
                }

                list.Add(JsonNode.NewObject()
                    .Set("id", preset.Id ?? string.Empty)
                    .Set("display_name", preset.DisplayName ?? string.Empty)
                    .Set("config", (preset.Config ?? new SandboxConfig()).ToExport()));
            }

            return JsonNode.NewObject()
                .Set("active_id", ActiveId ?? string.Empty)
                .Set("next_id", NextId)
                .Set("presets", list);
        }

        public static SandboxPresetLibrary FromJson(JsonNode node)
        {
            var library = new SandboxPresetLibrary();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return library;
            }

            library.ActiveId = SandboxJson.Text(node, "active_id", string.Empty);
            library.NextId = SandboxJson.Int(node, "next_id", 1);
            var list = node["presets"];
            if (list.Kind == JsonKind.Array)
            {
                var items = list.Items;
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i].Kind != JsonKind.Object)
                    {
                        continue;
                    }

                    var config = items[i]["config"];
                    library.Presets.Add(new SandboxPreset
                    {
                        Id = SandboxJson.Text(items[i], "id", string.Empty),
                        DisplayName = SandboxJson.Text(items[i], "display_name", string.Empty),
                        Config = config.Kind == JsonKind.Object ? SandboxConfig.FromExport(config) : null,
                    });
                }
            }

            return library;
        }
    }
}
