using System;
using System.IO;
using BlitzLegacy.Core.Serialization;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Do not compress source UI texels; noise/mask textures are linear data.</summary>
    public sealed class BigBossTextureImport : AssetPostprocessor
    {
        private const string Root = "Assets/Blitz/Resources/BigBoss/";
        private static bool DataTexture(string path)
        {
            const string manifest = "Assets/Blitz/Resources/Data/big_boss_fx_1120.json";
            if (!File.Exists(manifest)) return false;
            foreach (var texture in JsonNode.Parse(File.ReadAllText(manifest))["textures"].Fields)
            {
                if ("Assets/Blitz/Resources/" + texture.Value["resource"].AsString() + ".png" != path) continue;
                foreach (var usage in texture.Value["usage"].Items)
                    if (usage.AsString() != "spritePath") return true;
            }
            return false;
        }
        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Root, StringComparison.Ordinal)) return;
            Configure((TextureImporter)assetImporter, DataTexture(assetPath));
        }
        private static void Configure(TextureImporter importer, bool data)
        {
            importer.sRGBTexture = !data; importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false; importer.alphaIsTransparency = false; importer.filterMode = FilterMode.Bilinear;
            importer.npotScale = TextureImporterNPOTScale.None; importer.wrapMode = data ? TextureWrapMode.Repeat : TextureWrapMode.Clamp; importer.maxTextureSize = 8192;
        }
        public static void Apply()
        {
            var count = 0;
            foreach (var path in Directory.GetFiles(Root, "*.png", SearchOption.AllDirectories))
            {
                if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue;
                var data = DataTexture(path);
                if (importer.sRGBTexture == !data && !importer.mipmapEnabled && !importer.alphaIsTransparency && importer.textureCompression == TextureImporterCompression.Uncompressed && importer.npotScale == TextureImporterNPOTScale.None && importer.wrapMode == (data ? TextureWrapMode.Repeat : TextureWrapMode.Clamp) && importer.maxTextureSize == 8192) continue;
                Configure(importer, data); importer.SaveAndReimport(); count++;
            }
            Debug.Log("[Blitz] Big Boss source texture imports normalized: " + count);
        }
    }
}
