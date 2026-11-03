using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Godot's tr() and <see cref="LocalizationTable"/> both echo an unknown key
    /// back, so a key the port forgets to ship is drawn on the screen as-is.
    /// These tests walk every key the game can ask for - the literals in the
    /// runtime sources, the authored data resources and the equipment and
    /// consumable catalogues - and fail with the list of keys that would show
    /// up raw, in English and in Russian.
    /// </summary>
    public sealed class LocalizationCoverageTests
    {
        /// <summary>lang.Get("literal"), on any of the table's usual names.</summary>
        private static readonly Regex LiteralLookup = new Regex(
            "\\b(?:lang|_lang|Lang|localization|_localization)\\.Get\\(\\s*\"([^\"]+)\"",
            RegexOptions.Compiled);

        private static LocalizationTable Load()
        {
            var asset = Resources.Load<TextAsset>("Data/lang");
            Assert.IsNotNull(asset, "Resources/Data/lang.json is missing");
            return LocalizationTable.Parse(asset.text);
        }

        private static string RuntimeRoot()
        {
            return Path.Combine(Application.dataPath, "Blitz");
        }

        /// <summary>
        /// The row has to exist and answer with text in both languages. A
        /// translation that reads exactly like its key is fine - numbers, "OK"
        /// and "2X" are authored that way - but a missing row is not, because
        /// that is what makes the screen print the key.
        /// </summary>
        private static void Collect(
            LocalizationTable table, string key, string origin, List<string> broken)
        {
            if (string.IsNullOrEmpty(key)) { return; }
            if (!table.Has(key))
            {
                broken.Add(key + " [no row] " + origin);
                return;
            }

            foreach (var language in new[] { "en", "ru" })
            {
                if (string.IsNullOrEmpty(table.Get(key, language)))
                {
                    broken.Add(key + " [" + language + "] " + origin);
                }
            }
        }

        private static void Report(List<string> broken)
        {
            if (broken.Count == 0) { return; }
            var message = new StringBuilder();
            message.Append(broken.Count);
            message.Append(" untranslated key(s) would be drawn as the raw key:");
            for (var i = 0; i < broken.Count; i++)
            {
                message.Append('\n');
                message.Append(broken[i]);
            }
            Assert.Fail(message.ToString());
        }

        [Test]
        public void EveryRuntimeLiteral_IsTranslated()
        {
            var table = Load();
            var broken = new List<string>();
            var scanned = 0;
            var files = Directory.GetFiles(RuntimeRoot(), "*.cs", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                // The test sources deliberately look up made-up keys.
                if (file.Replace('\\', '/').Contains("/Tests/")) { continue; }
                var origin = Path.GetFileName(file);
                foreach (Match match in LiteralLookup.Matches(File.ReadAllText(file)))
                {
                    scanned++;
                    Collect(table, match.Groups[1].Value, origin, broken);
                }
            }

            Assert.Greater(scanned, 0, "no lang.Get literals were found at all");
            Report(broken);
        }

        [Test]
        public void EveryEquipmentAndConsumable_IsTranslated()
        {
            var table = Load();
            var broken = new List<string>();

            for (var i = 0; i < EquipmentCatalog.All.Length; i++)
            {
                var item = EquipmentCatalog.All[i];
                Collect(table, item.NameKey, "EquipmentCatalog " + item.Id, broken);
                Collect(table, item.DescriptionKey, "EquipmentCatalog " + item.Id, broken);
            }

            for (var i = 0; i < ConsumableCatalog.All.Length; i++)
            {
                var item = ConsumableCatalog.All[i];
                Collect(table, item.NameKey, "ConsumableCatalog " + item.Id, broken);
                Collect(table, item.DescriptionKey, "ConsumableCatalog " + item.Id, broken);
                // The repair kit clears its refusal message on purpose.
                Collect(table, item.CantUseKey, "ConsumableCatalog " + item.Id, broken);
            }

            Report(broken);
        }

        /// <summary>
        /// Every authored resource row whose field name ends in "Key" - map
        /// names, flow captions, pause menu entries - has to resolve too.
        /// </summary>
        [Test]
        public void EveryDataResourceKey_IsTranslated()
        {
            var table = Load();
            var broken = new List<string>();
            var data = Path.Combine(Application.dataPath, "Blitz/Resources/Data");
            var files = Directory.GetFiles(data, "*.json", SearchOption.TopDirectoryOnly);
            var scanned = 0;

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                // The per-tank armour and node dumps carry no captions.
                if (name.EndsWith("_armor.json", StringComparison.Ordinal)
                    || name.EndsWith("_nodes.json", StringComparison.Ordinal)
                    || string.Equals(name, "lang.json", StringComparison.Ordinal))
                {
                    continue;
                }

                JsonNode node;
                if (!JsonNode.TryParse(File.ReadAllText(file), out node)) { continue; }
                scanned += Walk(table, node, string.Empty, name, broken);
            }

            Assert.Greater(scanned, 0, "no *Key rows were found in the data resources");
            Report(broken);
        }

        private static int Walk(
            LocalizationTable table, JsonNode node, string field, string origin,
            List<string> broken)
        {
            var found = 0;
            if (node.Kind == JsonKind.Object)
            {
                foreach (var entry in node.Fields)
                {
                    found += Walk(table, entry.Value, entry.Key, origin, broken);
                }
            }
            else if (node.Kind == JsonKind.Array)
            {
                for (var i = 0; i < node.Count; i++)
                {
                    found += Walk(table, node[i], field, origin, broken);
                }
            }
            else if (node.Kind == JsonKind.String
                && field.Length > 3
                && field.EndsWith("Key", StringComparison.Ordinal))
            {
                found++;
                Collect(table, node.AsString(), origin + ":" + field, broken);
            }

            return found;
        }

        /// <summary>
        /// get_tank_status and the shop popups answer with these keys, so a
        /// refused purchase must never flash a raw key at the player.
        /// </summary>
        [Test]
        public void EveryResearchRefusal_IsTranslated()
        {
            var table = Load();
            var broken = new List<string>();
            var keys = new[]
            {
                TankResearchService.ErrorNotEnoughExp,
                TankResearchService.ErrorNotEnoughCredits,
                TankResearchService.ErrorNotEnoughGold,
                TankResearchService.ErrorResearchTankFirst,
                TankResearchService.ErrorBuyTankFirst,
                TankResearchService.ErrorResearchPrevious,
                TankResearchService.ErrorTankAlreadyBought,
                TankResearchService.ErrorModuleAlreadyResearched,
                TankResearchService.ErrorModuleAlreadyInstalled,
                TankResearchService.ErrorGunNeedsTurret,
            };

            for (var i = 0; i < keys.Length; i++)
            {
                Collect(table, keys[i], "TankResearchService", broken);
            }

            Report(broken);
        }
    }
}
