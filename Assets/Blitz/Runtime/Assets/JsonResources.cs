using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;
using UnityEngine;

namespace BlitzLegacy.Runtime.Assets
{
    /// <summary>
    /// Parsed JSON text assets, read once per run. Effect sources and the
    /// wreck/crash manifests were parsed again for every effect, wreck and
    /// ammo-zone build, which put a multi-millisecond parse of up to a few
    /// hundred kilobytes of JSON into the frame that spawned them. Callers
    /// only read the trees, so one parse is shared. Main thread only.
    /// </summary>
    public static class JsonResources
    {
        private static readonly Dictionary<string, JsonNode> Parsed =
            new Dictionary<string, JsonNode>(System.StringComparer.Ordinal);

        /// <summary>
        /// The parsed tree of the TextAsset at <paramref name="path"/>, or
        /// null when there is no such asset. Treat the result as read-only.
        /// </summary>
        public static JsonNode Load(string path)
        {
            JsonNode node;
            if (Parsed.TryGetValue(path, out node))
            {
                return node;
            }

            var asset = Resources.Load<TextAsset>(path);
            if (asset == null)
            {
                return null;
            }

            node = JsonNode.Parse(asset.text);
            Parsed[path] = node;
            return node;
        }

        /// <summary>Forgets every parsed tree.</summary>
        public static void Clear()
        {
            Parsed.Clear();
        }
    }
}
